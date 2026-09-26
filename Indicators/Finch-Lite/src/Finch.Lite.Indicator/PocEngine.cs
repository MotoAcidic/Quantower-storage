using System;
using System.Collections.Generic;

namespace FinchLite;

/// <summary>
/// "the poc of the current move and a higher time frame poc of like the 15min" (the operator's
/// own ask, 2026-09-25) — one instance per timeframe (the chart's own timeframe for "the current
/// move", a second instance fed 15-minute bars for the higher-timeframe read), each tracking the
/// point of control — the single price that has traded the most volume — of the price leg
/// currently in progress.
///
/// "CURRENT MOVE" DEFINITION — an explicit choice made via AskUserQuestion, picked over a rolling
/// time window or a session-anchored profile: the move is the leg since the most recently
/// CONFIRMED swing pivot (high or low), using the same fractal pivot-confirmation rule
/// <see cref="OrderBlockEngine"/> already uses (a swing needs <see cref="pivotLookback"/> bars
/// closed on both sides without a more extreme high/low). Every time a NEW swing pivot confirms —
/// in either direction — the volume-by-price accumulator resets and starts fresh from that
/// moment, so the POC always describes "since the market last turned," not some fixed lookback.
/// Deliberately NOT sharing code with <see cref="OrderBlockEngine"/> despite the near-identical
/// pivot check — this project's own convention is small, independently-readable engine files over
/// a premature shared abstraction between two features that happen to both need a swing pivot.
///
/// KNOWN LAG, STATED PLAINLY: a swing pivot is only CONFIRMED <see cref="pivotLookback"/> bars
/// after it actually happened (the same confirmation delay order blocks already carry).
/// Finch-Lite has no historical tick/time-and-sales backfill (see
/// <see cref="FinchLiteIndicator.DrainDeltaTicks"/>'s own reasoning for why live ticks are the
/// only volume source this project has), so there is no way to retroactively reconstruct the
/// volume that traded during those lag bars. The profile simply starts accumulating live from the
/// moment of CONFIRMATION forward, not from the pivot bar's own timestamp — an accepted
/// simplification, not an oversight.
///
/// VOLUME SOURCE — tick-by-tick, not bar volume: fed one trade print (price + size) at a time via
/// <see cref="FeedTrade"/>, reusing the same live tick stream already driving the delta panel and
/// big-trade markers, rather than distributing each bar's total volume evenly across its own
/// high-low range. Unlike the delta panel, POC does not care which side was the aggressor — every
/// trade counts toward its own price's total regardless of direction.
/// </summary>
internal sealed class PocEngine
{
    private const int MaxHistoryKept = 500;

    private readonly List<Bar> history = new();
    private readonly Dictionary<double, double> volumeByPrice = new();
    private readonly int pivotLookback;
    private double? lastSwingHigh;
    private double? lastSwingLow;
    private bool hasMove;

    public PocEngine(int pivotLookback) => this.pivotLookback = Math.Max(1, pivotLookback);

    /// <summary>The current move's point of control, or null until a first swing pivot has
    /// confirmed and at least one trade has been fed since.</summary>
    public double? Poc { get; private set; }

    /// <summary>When the current move's accumulation started — the CONFIRMATION moment, not the
    /// pivot bar's own timestamp (see the class doc comment's stated lag).</summary>
    public DateTime MoveStartUtc { get; private set; }

    public void Reset()
    {
        this.history.Clear();
        this.volumeByPrice.Clear();
        this.lastSwingHigh = null;
        this.lastSwingLow = null;
        this.hasMove = false;
        this.Poc = null;
        this.MoveStartUtc = default;
    }

    /// <summary>Feed one newly-closed bar on this engine's own timeframe — drives swing/move
    /// detection only; volume comes from <see cref="FeedTrade"/> separately.</summary>
    public void FeedBar(Bar bar)
    {
        this.history.Add(bar);

        if (this.history.Count > MaxHistoryKept)
            this.history.RemoveRange(0, this.history.Count - MaxHistoryKept);

        this.ConfirmPivotAndMaybeStartNewMove();
    }

    /// <summary>Feed one trade print — folds into the current move's volume-by-price accumulator
    /// and recomputes the POC. A no-op until a first move has started.</summary>
    public void FeedTrade(double price, double size)
    {
        if (!this.hasMove || size <= 0)
            return;

        this.volumeByPrice[price] = this.volumeByPrice.TryGetValue(price, out var existing)
            ? existing + size
            : size;

        this.RecomputePoc();
    }

    private void ConfirmPivotAndMaybeStartNewMove()
    {
        var n = this.history.Count;
        var pivotIndex = n - 1 - this.pivotLookback;

        // The candidate needs pivotLookback bars on BOTH sides to be confirmed — same fractal
        // check as OrderBlockEngine.
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

        if (!isSwingHigh && !isSwingLow)
            return;

        if (isSwingHigh)
            this.lastSwingHigh = candidate.High;

        if (isSwingLow)
            this.lastSwingLow = candidate.Low;

        // A new swing confirmed THIS bar means the move that was building toward it is over and a
        // new one starts now — reset the accumulator so the POC only ever reflects volume traded
        // since this turn, not anything from the move that just ended.
        var justClosed = this.history[^1];
        this.hasMove = true;
        this.MoveStartUtc = justClosed.OpenUtc;
        this.volumeByPrice.Clear();
        this.Poc = null;
    }

    private void RecomputePoc()
    {
        var bestPrice = 0d;
        var bestVolume = -1d;

        foreach (var kvp in this.volumeByPrice)
        {
            if (kvp.Value > bestVolume)
            {
                bestVolume = kvp.Value;
                bestPrice = kvp.Key;
            }
        }

        this.Poc = bestVolume >= 0 ? bestPrice : null;
    }
}
