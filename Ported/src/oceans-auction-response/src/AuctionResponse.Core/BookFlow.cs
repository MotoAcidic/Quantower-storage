namespace AuctionResponse.Core;

/// <summary>Queue imbalance and best-quote order-flow imbalance (Section 7).</summary>
public static class BookFlow
{
    /// <summary>
    /// I(t) = (Qb - Qa) / (Qb + Qa), in [-1, 1] when defined.
    /// Zero total size means UNAVAILABLE, not balanced — the distinction is the whole point.
    /// </summary>
    public static Measure QueueImbalance(decimal bidQuantity, decimal askQuantity, long atNs, int windowMs = 0)
    {
        var total = bidQuantity + askQuantity;
        if (total <= 0m)
            return Measure.Unavailable("ratio", windowMs, atNs, "no displayed size at the best level");
        return Measure.Of((double)((bidQuantity - askQuantity) / total), "ratio", windowMs, atNs);
    }

    /// <summary>
    /// One order-flow imbalance increment between consecutive coherent best-book states:
    ///
    ///   e_b = 1[b_n &gt;= b_(n-1)] * Qb_n  -  1[b_n &lt;= b_(n-1)] * Qb_(n-1)
    ///   e_a = -1[a_n &lt;= a_(n-1)] * Qa_n  +  1[a_n &gt;= a_(n-1)] * Qa_(n-1)
    ///
    /// At unchanged prices both indicators fire, which reduces to bid-size change minus
    /// ask-size change. A trade's signed volume is never added on top: the execution is
    /// already visible here as a queue decrease.
    /// </summary>
    public static decimal Increment(
        long prevBidTicks, decimal prevBidQty, long prevAskTicks, decimal prevAskQty,
        long newBidTicks, decimal newBidQty, long newAskTicks, decimal newAskQty)
    {
        decimal eb = 0m;
        if (newBidTicks >= prevBidTicks) eb += newBidQty;
        if (newBidTicks <= prevBidTicks) eb -= prevBidQty;

        decimal ea = 0m;
        if (newAskTicks <= prevAskTicks) ea -= newAskQty;
        if (newAskTicks >= prevAskTicks) ea += prevAskQty;

        return eb + ea;
    }

    public static decimal Increment(in QuoteState prev, in QuoteState next) =>
        Increment(prev.BidTicks, prev.BidQuantity, prev.AskTicks, prev.AskQuantity,
                  next.BidTicks, next.BidQuantity, next.AskTicks, next.AskQuantity);
}

/// <summary>
/// Rolling OFI and time-weighted representative depth over one window (Section 7).
///
/// Fed from ONE canonical book path — the same best-quote states the QuotePath consumes —
/// never from BBO, MBP and MBO callbacks simultaneously.
/// </summary>
public sealed class BookFlowWindow
{
    private readonly struct Increment
    {
        public readonly long Ns; public readonly decimal E;
        public Increment(long ns, decimal e) { Ns = ns; E = e; }
    }

    private readonly struct DepthPoint
    {
        public readonly long Ns; public readonly decimal AvgDepth;
        public DepthPoint(long ns, decimal avgDepth) { Ns = ns; AvgDepth = avgDepth; }
    }

    private readonly Queue<Increment> _increments = new();
    private readonly Deque<DepthPoint> _depth = new();
    private readonly long _windowNs;

    private decimal _ofi;
    private decimal _closedIntegral;   // running sum of avgDepth * dt over stored segments
    private bool _havePrev;
    private QuoteState _prev;

    public BookFlowWindow(int windowMs)
    {
        WindowMs = windowMs;
        _windowNs = (long)windowMs * 1_000_000L;
    }

    public int WindowMs { get; }
    public decimal OfiSum => _ofi;
    public bool HasDepth => _depth.Count > 0;

    /// <summary>
    /// Accepts the next coherent best-book state. Snapshot-initialisation states must be
    /// passed with <paramref name="contributesFlow"/> false: a snapshot initialises state
    /// and contributes no OFI (Section 13).
    /// </summary>
    public void Accept(in QuoteState state, bool contributesFlow = true)
    {
        if (_havePrev && contributesFlow)
        {
            var e = BookFlow.Increment(_prev, state);
            if (e != 0m) { _increments.Enqueue(new Increment(state.Ns, e)); _ofi += e; }
        }

        var avg = (state.BidQuantity + state.AskQuantity) / 2m;
        if (_depth.Count > 0)
        {
            var last = _depth.Back;
            _closedIntegral += last.AvgDepth * (state.Ns - last.Ns);
        }
        _depth.PushBack(new DepthPoint(state.Ns, avg));

        _prev = state;
        _havePrev = true;
    }

    public void Advance(long nowNs)
    {
        var cutoff = nowNs - _windowNs;
        while (_increments.Count > 0 && _increments.Peek().Ns <= cutoff)
            _ofi -= _increments.Dequeue().E;

        // Keep exactly one point at or before the cutoff so the window start stays anchored.
        while (_depth.Count >= 2 && _depth[1].Ns <= cutoff)
        {
            var first = _depth.PopFront();
            _closedIntegral -= first.AvgDepth * (_depth.Front.Ns - first.Ns);
        }
    }

    /// <summary>
    /// Time-weighted representative depth over the window. The intervals partition the
    /// ENTIRE window, including the state carried to its start, so a quiet book is not
    /// silently treated as a thin one.
    /// </summary>
    public Measure MeanDepth(long nowNs)
    {
        if (_depth.Count == 0)
            return Measure.Unavailable("qty", WindowMs, nowNs, "no book state in window");

        var cutoff = nowNs - _windowNs;
        var first = _depth.Front;
        var last = _depth.Back;

        // The window is only representative if a state was already in effect at its start.
        if (first.Ns > cutoff)
            return Measure.Unavailable("qty", WindowMs, nowNs, "book state does not span the window");

        // Closed segments, minus the part of the leading segment lying before the window,
        // plus the open trailing segment that runs to now.
        var integral = _closedIntegral
                     - first.AvgDepth * (cutoff - first.Ns)
                     + last.AvgDepth * (nowNs - last.Ns);

        return Measure.Of((double)integral / _windowNs, "qty", WindowMs, nowNs);
    }

    /// <summary>
    /// F_W = OFI_W / max(meanDepth, Qfloor). Missing depth makes F unavailable.
    /// </summary>
    public Measure DepthNormalizedOfi(long nowNs, decimal depthFloor)
    {
        var depth = MeanDepth(nowNs);
        if (!depth.IsAvailable)
            return Measure.Unavailable("ratio", WindowMs, nowNs, depth.Reason ?? "depth unavailable");
        var denom = Math.Max(depth.Value!.Value, (double)depthFloor);
        return Measure.Of((double)_ofi / denom, "ratio", WindowMs, nowNs);
    }

    public Measure Ofi(long nowNs) => Measure.Of((double)_ofi, "qty", WindowMs, nowNs);

    public void Clear()
    {
        _increments.Clear(); _depth.Clear();
        _ofi = 0m; _closedIntegral = 0m; _havePrev = false;
    }
}
