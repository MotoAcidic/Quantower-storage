using System;
using System.Collections.Generic;

namespace FinchLite;

/// <summary>
/// "i would like to see the 15minute order blocks and 1hr order blocks that are labeled" (the
/// operator's own ask, 2026-09-23) — one instance per timeframe (15m, 1h), fed that timeframe's
/// own closed bars one at a time via <see cref="Feed"/>.
///
/// DETECTION RULE — structure-break, picked over a plainer "opposite candle before a big candle"
/// rule via an explicit choice the operator made: a swing high/low is confirmed once
/// <see cref="PivotLookback"/> bars have closed on BOTH sides of it without a more extreme
/// high/low (a standard fractal pivot). Once a bar CLOSES beyond the most recently confirmed
/// swing (above a swing high, or below a swing low), that is a structure break — the last
/// opposite-coloured candle before the break bar is the order block. The broken swing is then
/// consumed (set back to unknown) so the SAME swing cannot fire a second order block; only a
/// freshly confirmed swing, broken again, fires another.
///
/// INVALIDATION — "price closes through it": a bullish (support) block is removed the moment a
/// bar CLOSES below its own bottom; a bearish (resistance) block is removed the moment a bar
/// CLOSES above its own top. A wick alone does not remove it. Both the operator's own explicit
/// choice, picked over "any wick invalidates" and "never remove."
/// </summary>
internal sealed class OrderBlockEngine
{
    internal readonly record struct OrderBlockZone(DateTime StartUtc, double Top, double Bottom, bool IsBullish);

    private const int MaxHistoryKept = 500;
    private const int MaxActiveKept = 20;
    private const int MaxBackScan = 20;

    private readonly List<Bar> history = new();
    private readonly List<OrderBlockZone> active = new();
    private readonly int pivotLookback;
    private double? lastSwingHigh;
    private double? lastSwingLow;

    public OrderBlockEngine(int pivotLookback) => this.pivotLookback = Math.Max(1, pivotLookback);

    public IReadOnlyList<OrderBlockZone> Active => this.active;

    public void Reset()
    {
        this.history.Clear();
        this.active.Clear();
        this.lastSwingHigh = null;
        this.lastSwingLow = null;
    }

    public void Feed(Bar bar)
    {
        this.history.Add(bar);

        if (this.history.Count > MaxHistoryKept)
            this.history.RemoveRange(0, this.history.Count - MaxHistoryKept);

        // Mitigation first, against the bar that just closed — a block that closes-through on
        // the very bar that also confirms a new swing should still disappear this cycle.
        for (var i = this.active.Count - 1; i >= 0; i--)
        {
            var zone = this.active[i];
            var mitigated = zone.IsBullish ? bar.Close < zone.Bottom : bar.Close > zone.Top;

            if (mitigated)
                this.active.RemoveAt(i);
        }

        this.ConfirmPivotAndDetect();
    }

    private void ConfirmPivotAndDetect()
    {
        var n = this.history.Count;
        var pivotIndex = n - 1 - this.pivotLookback;

        // The candidate needs PivotLookback bars on BOTH sides to be confirmed.
        if (pivotIndex - this.pivotLookback >= 0)
        {
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
                this.lastSwingHigh = candidate.High;

            if (isSwingLow)
                this.lastSwingLow = candidate.Low;
        }

        var last = this.history[^1];

        if (this.lastSwingHigh is { } swingHigh && last.Close > swingHigh)
        {
            if (this.TryFindOppositeCandle(isBullishBreak: true, out var ob))
                this.AddZone(ob, isBullish: true);

            this.lastSwingHigh = null;
        }

        if (this.lastSwingLow is { } swingLow && last.Close < swingLow)
        {
            if (this.TryFindOppositeCandle(isBullishBreak: false, out var ob))
                this.AddZone(ob, isBullish: false);

            this.lastSwingLow = null;
        }
    }

    /// <summary>Scans backward from just before the break bar for the nearest opposite-coloured
    /// candle (down for a bullish break, up for a bearish one) — the classic order-block
    /// definition. Bounded by <see cref="MaxBackScan"/>; gives up rather than reaching back
    /// through an implausibly long same-colour run.</summary>
    private bool TryFindOppositeCandle(bool isBullishBreak, out Bar found)
    {
        found = default;

        var n = this.history.Count;
        var scanFrom = n - 2; // exclude the break bar itself (n - 1)
        var scanTo = Math.Max(0, n - 1 - MaxBackScan);

        for (var i = scanFrom; i >= scanTo; i--)
        {
            var bar = this.history[i];

            if (isBullishBreak && bar.Close < bar.Open)
            {
                found = bar;
                return true;
            }

            if (!isBullishBreak && bar.Close > bar.Open)
            {
                found = bar;
                return true;
            }
        }

        return false;
    }

    private void AddZone(Bar candle, bool isBullish)
    {
        this.active.Add(new OrderBlockZone(candle.OpenUtc, candle.High, candle.Low, isBullish));

        if (this.active.Count > MaxActiveKept)
            this.active.RemoveAt(0);
    }
}
