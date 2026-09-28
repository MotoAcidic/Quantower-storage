using System;
using System.Collections.Generic;
using FinchLite;

namespace finchDomScalpStrategy;

/// <summary>
/// "also stop losses should be places at recent swing low for longs and recent swing high for
/// shorts right" (the operator's own ask, 2026-09-27) — a standalone swing-pivot tracker, built
/// specifically for STOP placement rather than reusing <c>PocEngine</c>'s own internal swing
/// detection directly, since that engine only runs when POC trading is enabled and its swing
/// state exists purely to know when to reset its own volume-by-price accumulator. Stops need a
/// swing reference regardless of whether either POC feature is switched on, so this runs
/// unconditionally, fed the same chart bars every poll already reads.
///
/// Same fractal pivot-confirmation rule as `OrderBlockEngine`/`PocEngine` (a candidate needs
/// `PivotLookback` bars closed on both sides without a more extreme high/low) — deliberately NOT
/// shared code with those two; this project's own convention favors small, independently
/// readable files over a premature abstraction between three features that each happen to need
/// the same handful of lines.
/// </summary>
internal sealed class SwingTracker
{
    private const int MaxHistoryKept = 500;

    private readonly List<Bar> history = new();
    private readonly int pivotLookback;

    public SwingTracker(int pivotLookback) => this.pivotLookback = Math.Max(1, pivotLookback);

    /// <summary>The most recently CONFIRMED swing high — null until at least one has confirmed
    /// this run. Never cleared once set; always reflects the latest one seen.</summary>
    public double? LastSwingHigh { get; private set; }

    /// <summary>The most recently CONFIRMED swing low — same lifetime as <see cref="LastSwingHigh"/>.</summary>
    public double? LastSwingLow { get; private set; }

    public void Reset()
    {
        this.history.Clear();
        this.LastSwingHigh = null;
        this.LastSwingLow = null;
    }

    public void FeedBar(Bar bar)
    {
        this.history.Add(bar);

        if (this.history.Count > MaxHistoryKept)
            this.history.RemoveRange(0, this.history.Count - MaxHistoryKept);

        var n = this.history.Count;
        var pivotIndex = n - 1 - this.pivotLookback;

        if (pivotIndex - this.pivotLookback < 0)
            return;

        var candidate = this.history[pivotIndex];
        var isSwingHigh = true;
        var isSwingLow = true;

        for (var j = pivotIndex - this.pivotLookback; j <= pivotIndex + this.pivotLookback; j++)
        {
            if (j == pivotIndex)
                continue;

            if (this.history[j].High >= candidate.High)
                isSwingHigh = false;

            if (this.history[j].Low <= candidate.Low)
                isSwingLow = false;
        }

        if (isSwingHigh)
            this.LastSwingHigh = candidate.High;

        if (isSwingLow)
            this.LastSwingLow = candidate.Low;
    }
}
