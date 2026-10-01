using System;
using System.Collections.Generic;

namespace oceansStackStrategy;

/// <summary>
/// Buy/sell volume per chart bar, bucketed from classified tick prints — same shape as
/// `finchDomScalpStrategy`'s own `DeltaTracker`, copied here rather than shared since it currently
/// lives inside that strategy's own project folder, not a Finch-Lite shared file. Extended with
/// `LastBarBuy`/`LastBarSell` (the single most-recently-closed bar's own buy/sell volume, not a
/// rolling sum) — `AbsorptionTracker` needs a bar's OWN delta and volume to test the
/// big-volume/aggressive-delta/opposite-close absorption pattern, which `RollingDelta`'s rolling
/// window alone can't answer.
/// </summary>
internal sealed class DeltaTracker
{
    private const int MaxBarsKept = 200;

    private readonly Dictionary<DateTime, (double Buy, double Sell)> pending = new();
    private readonly List<double> barDeltas = new();
    private readonly TimeSpan barPeriod;

    public DeltaTracker(TimeSpan barPeriod) => this.barPeriod = barPeriod;

    public double LastBarBuy { get; private set; }
    public double LastBarSell { get; private set; }
    public double LastBarVolume => this.LastBarBuy + this.LastBarSell;
    public double LastBarDelta => this.LastBarBuy - this.LastBarSell;

    public void Reset()
    {
        this.pending.Clear();
        this.barDeltas.Clear();
        this.LastBarBuy = 0;
        this.LastBarSell = 0;
    }

    /// <summary>Feed one classified trade print — buckets it into whichever bar it belongs to,
    /// closed out later by <see cref="CloseBar"/> once that bar's chart candle actually closes.</summary>
    public void FeedTick(DateTime timeUtc, double size, bool isBuy)
    {
        var bucket = Bucket(timeUtc, this.barPeriod);
        (double Buy, double Sell) acc = this.pending.TryGetValue(bucket, out var existing) ? existing : (0d, 0d);
        this.pending[bucket] = isBuy ? (acc.Buy + size, acc.Sell) : (acc.Buy, acc.Sell + size);
    }

    /// <summary>Call once per newly-closed chart bar, in order — closes out that bar's own delta
    /// (0/0 if no ticks were ever classified for it) and rolls it into the window.</summary>
    public void CloseBar(DateTime openUtc)
    {
        (double Buy, double Sell) acc = this.pending.TryGetValue(openUtc, out var existing) ? existing : (0d, 0d);
        this.pending.Remove(openUtc);

        this.LastBarBuy = acc.Buy;
        this.LastBarSell = acc.Sell;

        this.barDeltas.Add(acc.Buy - acc.Sell);

        if (this.barDeltas.Count > MaxBarsKept)
            this.barDeltas.RemoveRange(0, this.barDeltas.Count - MaxBarsKept);
    }

    /// <summary>Sum of the last <paramref name="lookbackBars"/> closed bars' own delta — fewer
    /// bars than requested (early in a run) just sums whatever exists so far.</summary>
    public double RollingDelta(int lookbackBars)
    {
        var count = Math.Min(Math.Max(0, lookbackBars), this.barDeltas.Count);
        var sum = 0d;

        for (var i = this.barDeltas.Count - count; i < this.barDeltas.Count; i++)
            sum += this.barDeltas[i];

        return sum;
    }

    private static DateTime Bucket(DateTime timeUtc, TimeSpan period)
        => new(timeUtc.Ticks - (timeUtc.Ticks % period.Ticks), DateTimeKind.Utc);
}
