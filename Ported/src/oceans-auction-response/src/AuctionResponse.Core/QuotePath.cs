namespace AuctionResponse.Core;

/// <summary>
/// One coherent best-book state. Midpoint is carried in HALF ticks (bid + ask) so that a
/// half-tick midpoint stays exact in integer arithmetic — boundaries C and F inherit that
/// exactness rather than accumulating binary floating point error.
/// </summary>
public readonly record struct QuoteState(
    long Ns,
    long BidTicks,
    long AskTicks,
    decimal BidQuantity,
    decimal AskQuantity,
    long Index)
{
    /// <summary>m = (b + a) / 2, held doubled. Divide by 2 for ticks.</summary>
    public long MidHalfTicks => BidTicks + AskTicks;

    /// <summary>Spread s = a - b, in ticks.</summary>
    public long SpreadTicks => AskTicks - BidTicks;

    /// <summary>
    /// Section 6 coherence: ask strictly above bid, both sides present, non-negative sizes.
    /// Locked and crossed quotes are invalid for this baseline.
    /// </summary>
    public bool IsCoherent => AskTicks > BidTicks && BidQuantity >= 0m && AskQuantity >= 0m;
}

/// <summary>
/// The best-quote path over a rolling window (Sections 6, 11, 13).
///
/// Holds every coherent state in (t - W, t] plus the single latest state at or before
/// t - W — the state carried to the window start, which both anchors R_W and is included
/// in the excursion maximum. Minima and maxima come from monotonic deques, so evaluating
/// every coherent quote state in the path costs O(1) amortised rather than a rescan.
/// </summary>
public sealed class QuotePath
{
    private readonly Deque<QuoteState> _states = new();
    private readonly Deque<int> _minIdx = new();   // slot positions, increasing mid
    private readonly Deque<int> _maxIdx = new();   // slot positions, decreasing mid
    private readonly long _windowNs;

    private long _nextIndex;

    // Health bookkeeping: the most recent instant known to be invalid or stale.
    private long _lastUnhealthyNs = long.MinValue;
    private string? _lastUnhealthyReason;

    private long _lastBidUpdateNs = long.MinValue;
    private long _lastAskUpdateNs = long.MinValue;
    private long _lastBidTicks, _lastAskTicks;
    private decimal _lastBidQty, _lastAskQty;
    private bool _haveBid, _haveAsk;

    public QuotePath(int windowMs)
    {
        WindowMs = windowMs;
        _windowNs = (long)windowMs * 1_000_000L;
    }

    public int WindowMs { get; }
    public bool HasCurrent => _haveBid && _haveAsk && _states.Count > 0;
    public QuoteState? Current => _states.Count > 0 ? _states.Back : null;
    public int StateCount => _states.Count;
    public string? LastUnhealthyReason => _lastUnhealthyReason;

    /// <summary>
    /// The most recent instant known to have been invalid or stale. A dwell that started
    /// before this cannot be claimed as uninterrupted.
    /// </summary>
    public long LastUnhealthyNs => _lastUnhealthyNs;
    public long LastBidUpdateNs => _lastBidUpdateNs;
    public long LastAskUpdateNs => _lastAskUpdateNs;

    /// <summary>
    /// Applies a one-sided best-quote update. The two sides are merged into a coherent
    /// state; a state is appended only once both sides have been seen. Staleness of the
    /// other side is evaluated here, not only at decision time, so an interval in which
    /// one side froze is recorded even when no decision tick fell inside it.
    /// </summary>
    public void ApplyQuote(long ns, BookSide side, long priceTicks, decimal quantity, int maxQuoteAgeMs)
    {
        var guardNs = (long)maxQuoteAgeMs * 1_000_000L;

        if (side == BookSide.Bid)
        {
            if (_haveAsk && _lastAskUpdateNs != long.MinValue && ns - _lastAskUpdateNs > guardNs)
                MarkUnhealthy(ns, "quote freshness guard: ask side unchanged for " + ((ns - _lastAskUpdateNs) / 1_000_000L) + "ms");
            _lastBidTicks = priceTicks; _lastBidQty = quantity; _lastBidUpdateNs = ns; _haveBid = true;
        }
        else
        {
            if (_haveBid && _lastBidUpdateNs != long.MinValue && ns - _lastBidUpdateNs > guardNs)
                MarkUnhealthy(ns, "quote freshness guard: bid side unchanged for " + ((ns - _lastBidUpdateNs) / 1_000_000L) + "ms");
            _lastAskTicks = priceTicks; _lastAskQty = quantity; _lastAskUpdateNs = ns; _haveAsk = true;
        }

        if (!_haveBid || !_haveAsk) return;

        var state = new QuoteState(ns, _lastBidTicks, _lastAskTicks, _lastBidQty, _lastAskQty, _nextIndex);
        if (!state.IsCoherent)
        {
            MarkUnhealthy(ns, state.AskTicks <= state.BidTicks ? "locked or crossed quote" : "negative book quantity");
            return; // An incoherent state never enters the path.
        }

        _nextIndex++;
        Append(state);
    }

    /// <summary>
    /// Applies both sides at once as one atomic best bid/ask pair.
    ///
    /// Deliberately NOT two calls to <see cref="ApplyQuote"/>: applying the sides in
    /// sequence manufactures a transient crossed book whenever the whole quote moves by
    /// more than the spread, which would then be recorded as an invalid interval that
    /// never actually occurred on the feed.
    /// </summary>
    public void ApplyBestQuote(long ns, long bidTicks, decimal bidQty, long askTicks, decimal askQty, int maxQuoteAgeMs)
    {
        var guardNs = (long)maxQuoteAgeMs * 1_000_000L;
        if (_haveBid && _lastBidUpdateNs != long.MinValue && ns - _lastBidUpdateNs > guardNs)
            MarkUnhealthy(ns, "quote freshness guard: no bid update for " + ((ns - _lastBidUpdateNs) / 1_000_000L) + "ms");
        if (_haveAsk && _lastAskUpdateNs != long.MinValue && ns - _lastAskUpdateNs > guardNs)
            MarkUnhealthy(ns, "quote freshness guard: no ask update for " + ((ns - _lastAskUpdateNs) / 1_000_000L) + "ms");

        _lastBidTicks = bidTicks; _lastBidQty = bidQty; _lastBidUpdateNs = ns; _haveBid = true;
        _lastAskTicks = askTicks; _lastAskQty = askQty; _lastAskUpdateNs = ns; _haveAsk = true;

        var state = new QuoteState(ns, bidTicks, askTicks, bidQty, askQty, _nextIndex);
        if (!state.IsCoherent)
        {
            MarkUnhealthy(ns, state.AskTicks <= state.BidTicks ? "locked or crossed quote" : "negative book quantity");
            return;
        }

        _nextIndex++;
        Append(state);
    }

    private void Append(QuoteState state)
    {
        var slot = _states.Count;
        _states.PushBack(state);

        while (!_minIdx.IsEmpty && _states[_minIdx.Back].MidHalfTicks >= state.MidHalfTicks) _minIdx.PopBack();
        _minIdx.PushBack(slot);
        while (!_maxIdx.IsEmpty && _states[_maxIdx.Back].MidHalfTicks <= state.MidHalfTicks) _maxIdx.PopBack();
        _maxIdx.PushBack(slot);
    }

    private void MarkUnhealthy(long ns, string reason)
    {
        if (ns >= _lastUnhealthyNs) { _lastUnhealthyNs = ns; _lastUnhealthyReason = reason; }
    }

    /// <summary>Records an externally detected invalid interval, such as a feed interruption.</summary>
    public void MarkInterval(long ns, string reason) => MarkUnhealthy(ns, reason);

    /// <summary>
    /// Evicts states that can no longer influence the window, keeping exactly one state at
    /// or before t - W as the carried start state. Also records the interval as stale when
    /// either side has frozen past the guard as of now.
    /// </summary>
    public void Advance(long nowNs, int maxQuoteAgeMs)
    {
        var guardNs = (long)maxQuoteAgeMs * 1_000_000L;
        if (_haveBid && nowNs - _lastBidUpdateNs > guardNs)
            MarkUnhealthy(nowNs, "quote freshness guard: bid side unchanged for " + ((nowNs - _lastBidUpdateNs) / 1_000_000L) + "ms");
        if (_haveAsk && nowNs - _lastAskUpdateNs > guardNs)
            MarkUnhealthy(nowNs, "quote freshness guard: ask side unchanged for " + ((nowNs - _lastAskUpdateNs) / 1_000_000L) + "ms");

        var cutoff = nowNs - _windowNs;
        var dropped = 0;
        while (_states.Count >= 2 && _states[1].Ns <= cutoff) { _states.PopFront(); dropped++; }

        if (dropped > 0)
        {
            ShiftDeque(_minIdx, dropped);
            ShiftDeque(_maxIdx, dropped);
        }
    }

    private static void ShiftDeque(Deque<int> d, int dropped)
    {
        // Slot positions shift down as the front is evicted; anything that falls below zero
        // referred to an evicted state and leaves the extrema queue with it.
        var n = d.Count;
        for (var i = 0; i < n; i++)
        {
            var v = d.PopFront() - dropped;
            if (v >= 0) d.PushBack(v);
        }
    }

    /// <summary>The state carried to the window start: m(t - W).</summary>
    public QuoteState? StartState => _states.Count > 0 ? _states.Front : null;

    public long? MinMidHalfTicks => _minIdx.IsEmpty ? null : _states[_minIdx.Front].MidHalfTicks;
    public long? MaxMidHalfTicks => _maxIdx.IsEmpty ? null : _states[_maxIdx.Front].MidHalfTicks;

    /// <summary>
    /// True when the entire window span is free of any known invalid or stale interval and
    /// a start state exists to anchor it (Section 13).
    /// </summary>
    public bool IsCompleteAndFresh(long nowNs)
    {
        if (_states.Count == 0) return false;
        var cutoff = nowNs - _windowNs;
        if (_lastUnhealthyNs > cutoff) return false;
        return _states.Front.Ns <= nowNs;
    }

    /// <summary>
    /// Price response R_W(t) = m(t) - m(t - W), in ticks. Computed only when the whole
    /// quote path in the window passes the freshness policy.
    /// </summary>
    public Measure Response(long nowNs)
    {
        if (!IsCompleteAndFresh(nowNs))
            return Measure.Unavailable("ticks", WindowMs, nowNs, _lastUnhealthyReason ?? "incomplete quote path");
        return Measure.Of((_states.Back.MidHalfTicks - _states.Front.MidHalfTicks) / 2d, "ticks", WindowMs, nowNs);
    }

    /// <summary>Raw response in half ticks, for exact oriented comparisons.</summary>
    public long? ResponseHalfTicks(long nowNs)
        => IsCompleteAndFresh(nowNs) ? _states.Back.MidHalfTicks - _states.Front.MidHalfTicks : null;

    /// <summary>
    /// Oriented maximum forward excursion M_o over the closed path, in half ticks. The
    /// start state is included, so M_o is never negative.
    /// </summary>
    public long? MaxForwardExcursionHalfTicks(long nowNs, int orientation)
    {
        if (!IsCompleteAndFresh(nowNs)) return null;
        var start = _states.Front.MidHalfTicks;
        return orientation > 0
            ? Math.Max(MaxMidHalfTicks!.Value - start, 0)
            : Math.Max(start - MinMidHalfTicks!.Value, 0);
    }

    /// <summary>Oriented minimum K = min over the path of o*m, in half ticks.</summary>
    public long? OrientedMinimumHalfTicks(long nowNs, int orientation)
    {
        if (!IsCompleteAndFresh(nowNs)) return null;
        return orientation > 0 ? MinMidHalfTicks : -MaxMidHalfTicks!.Value;
    }

    public void Clear()
    {
        _states.Clear(); _minIdx.Clear(); _maxIdx.Clear();
        _nextIndex = 0;
        _lastUnhealthyNs = long.MinValue; _lastUnhealthyReason = null;
        _lastBidUpdateNs = _lastAskUpdateNs = long.MinValue;
        _haveBid = _haveAsk = false;
    }
}
