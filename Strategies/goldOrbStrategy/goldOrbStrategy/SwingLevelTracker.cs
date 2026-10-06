using System;
using System.Collections.Generic;

namespace goldOrbStrategy;

/// <summary>
/// The operator's own ask (2026-10-05): "target hh and ll levels" instead of a fixed
/// Risk:Reward multiple. Same fractal pivot-confirmation rule as finchDomScalpStrategy's own
/// SwingTracker (a candidate needs PivotLookback bars closed on both sides without a more
/// extreme high/low) — but THAT tracker only ever exposes the single most-recently-confirmed
/// swing high/low, which isn't enough here: once price has already broken above the last
/// confirmed swing high, that value is BEHIND price, not a usable target ahead of it. This one
/// keeps a bounded HISTORY of confirmed swings on each side so the nearest one still ahead of
/// price can be found — the "richer, multi-swing" design noted as a fallback idea back when the
/// simpler single-swing tracker was first built for finchDomScalpStrategy's own stop placement.
/// </summary>
internal sealed class SwingLevelTracker
{
    private const int MaxBarsKept = 500;
    private const int MaxLevelsKept = 50;

    private readonly List<(DateTime Time, double High, double Low)> bars = new();
    private readonly List<double> swingHighs = new();
    private readonly List<double> swingLows = new();
    private readonly int pivotLookback;

    public SwingLevelTracker(int pivotLookback) => this.pivotLookback = Math.Max(1, pivotLookback);

    public void FeedBar(DateTime time, double high, double low)
    {
        this.bars.Add((time, high, low));
        if (this.bars.Count > MaxBarsKept)
            this.bars.RemoveRange(0, this.bars.Count - MaxBarsKept);

        var n = this.bars.Count;
        var pivotIndex = n - 1 - this.pivotLookback;
        if (pivotIndex - this.pivotLookback < 0) return;

        var candidate = this.bars[pivotIndex];
        var isSwingHigh = true;
        var isSwingLow = true;

        for (var j = pivotIndex - this.pivotLookback; j <= pivotIndex + this.pivotLookback; j++)
        {
            if (j == pivotIndex) continue;
            if (this.bars[j].High >= candidate.High) isSwingHigh = false;
            if (this.bars[j].Low <= candidate.Low) isSwingLow = false;
        }

        if (isSwingHigh)
        {
            this.swingHighs.Add(candidate.High);
            if (this.swingHighs.Count > MaxLevelsKept) this.swingHighs.RemoveAt(0);
        }

        if (isSwingLow)
        {
            this.swingLows.Add(candidate.Low);
            if (this.swingLows.Count > MaxLevelsKept) this.swingLows.RemoveAt(0);
        }
    }

    /// <summary>Nearest confirmed swing high strictly above <paramref name="price"/> — the
    /// natural long target (the next resistance structure ahead of the trade). Null if none
    /// qualify yet (fresh attach, or price already above every confirmed swing high).</summary>
    public double? NearestSwingHighAbove(double price)
    {
        double? best = null;
        foreach (var h in this.swingHighs)
        {
            if (h <= price) continue;
            if (best is null || h < best) best = h;
        }
        return best;
    }

    /// <summary>Nearest confirmed swing low strictly below <paramref name="price"/> — the
    /// natural short target.</summary>
    public double? NearestSwingLowBelow(double price)
    {
        double? best = null;
        foreach (var l in this.swingLows)
        {
            if (l >= price) continue;
            if (best is null || l > best) best = l;
        }
        return best;
    }
}
