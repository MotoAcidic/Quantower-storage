using System;
using System.Collections.Generic;

namespace FinchDomScalpStrategyBackup;

/// <summary>
/// "we should also use the delta as a decision factor for when the reversal is going to happen
/// because if we keep trying to take longs when delta is negative and in the red we are just
/// fighting our selves" (the operator's own ask, 2026-09-27) — tracks buy/sell volume per CHART
/// bar (same tick-classification/bucketing approach as Finch-Lite's own delta panel) and exposes
/// a rolling sum over the last N closed bars, used as a HARD entry filter (the operator's own
/// choice over a softer confirmation, via AskUserQuestion): a long is skipped entirely if the
/// rolling window is net negative, a short is skipped entirely if it's net positive.
/// </summary>
internal sealed class DeltaTracker
{
    private const int MaxBarsKept = 200;

    private readonly Dictionary<DateTime, (double Buy, double Sell)> pending = new();
    private readonly List<double> barDeltas = new();
    private readonly TimeSpan barPeriod;

    public DeltaTracker(TimeSpan barPeriod) => this.barPeriod = barPeriod;

    public void Reset()
    {
        this.pending.Clear();
        this.barDeltas.Clear();
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
    /// (0 if no ticks were ever classified for it) and rolls it into the window.</summary>
    public void CloseBar(DateTime openUtc)
    {
        (double Buy, double Sell) acc = this.pending.TryGetValue(openUtc, out var existing) ? existing : (0d, 0d);
        this.pending.Remove(openUtc);

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
