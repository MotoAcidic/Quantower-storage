namespace AuctionResponse.Core;

/// <summary>
/// Rolling trade aggregates over one window (Section 6), left-open / right-closed:
/// events with time in (t - W, t] that were already available at the tick.
///
/// Running sums over a deque; no per-tick rescan. Sums are decimal so they stay exact.
/// </summary>
public sealed class TradeWindow
{
    private readonly struct Entry
    {
        public readonly long Ns; public readonly AggressorDirection Dir; public readonly decimal Qty; public readonly long PriceTicks;
        public Entry(long ns, AggressorDirection dir, decimal qty, long priceTicks) { Ns = ns; Dir = dir; Qty = qty; PriceTicks = priceTicks; }
    }

    private readonly Queue<Entry> _entries = new();
    private readonly long _windowNs;

    private decimal _buy, _sell, _unknown;

    public TradeWindow(int windowMs)
    {
        WindowMs = windowMs;
        _windowNs = (long)windowMs * 1_000_000L;
    }

    public int WindowMs { get; }

    /// <summary>Buy-initiated volume B_W.</summary>
    public decimal Buy => _buy;
    /// <summary>Sell-initiated volume S_W.</summary>
    public decimal Sell => _sell;
    /// <summary>Volume whose aggressor is genuinely unknown. Counts toward V and side quality only.</summary>
    public decimal Unknown => _unknown;
    /// <summary>Total volume V_W = B + S + U.</summary>
    public decimal Total => _buy + _sell + _unknown;
    /// <summary>Signed volume delta D_W = B - S, in quantity units.</summary>
    public decimal Delta => _buy - _sell;
    public int Count => _entries.Count;

    public void Add(long ns, AggressorDirection dir, decimal qty, long priceTicks)
    {
        if (qty <= 0m) return; // Section 6: quantity is positive. Non-positive is a fault upstream.
        _entries.Enqueue(new Entry(ns, dir, qty, priceTicks));
        switch (dir)
        {
            case AggressorDirection.Buy: _buy += qty; break;
            case AggressorDirection.Sell: _sell += qty; break;
            default: _unknown += qty; break;
        }
    }

    /// <summary>Evicts everything at or before t - W. Left-open boundary: ties are excluded.</summary>
    public void Advance(long nowNs)
    {
        var cutoff = nowNs - _windowNs;
        while (_entries.Count > 0 && _entries.Peek().Ns <= cutoff)
        {
            var e = _entries.Dequeue();
            switch (e.Dir)
            {
                case AggressorDirection.Buy: _buy -= e.Qty; break;
                case AggressorDirection.Sell: _sell -= e.Qty; break;
                default: _unknown -= e.Qty; break;
            }
        }
    }

    /// <summary>Attacker volume executed inside an inclusive price zone, within this window.</summary>
    public decimal VolumeInZone(long lowTicks, long highTicks, AggressorDirection dir)
    {
        decimal sum = 0m;
        foreach (var e in _entries)
            if (e.Dir == dir && e.PriceTicks >= lowTicks && e.PriceTicks <= highTicks) sum += e.Qty;
        return sum;
    }

    /// <summary>Normalized delta d_W = D / (B + S). Unavailable when no sided volume exists.</summary>
    public Measure NormalizedDelta(long atNs)
    {
        var sided = _buy + _sell;
        return sided == 0m
            ? Measure.Unavailable("ratio", WindowMs, atNs, "no sided volume in window")
            : Measure.Of((double)(Delta / sided), "ratio", WindowMs, atNs);
    }

    /// <summary>Side quality q_W = (B + S) / V. Unavailable when the window holds no volume.</summary>
    public Measure SideQuality(long atNs)
    {
        var total = Total;
        return total == 0m
            ? Measure.Unavailable("fraction", WindowMs, atNs, "no volume in window")
            : Measure.Of((double)((_buy + _sell) / total), "fraction", WindowMs, atNs);
    }

    /// <summary>Volume rate in quantity per second.</summary>
    public Measure VolumePerSecond(long atNs)
        => Measure.Of((double)Total / (WindowMs / 1000d), "qty/s", WindowMs, atNs);

    public void Clear() { _entries.Clear(); _buy = _sell = _unknown = 0m; }
}

/// <summary>
/// Session cumulative delta (Section 6): the sum of direction x quantity over individual
/// session trades. Explicitly NOT a sum of overlapping window deltas, and not a baseline
/// alert feature — it exists for display and diagnostics only.
/// </summary>
public sealed class CumulativeDelta
{
    public decimal Value { get; private set; }
    public void Add(AggressorDirection dir, decimal qty)
    {
        if (dir == AggressorDirection.Buy) Value += qty;
        else if (dir == AggressorDirection.Sell) Value -= qty;
    }
    public void Reset() => Value = 0m;
}
