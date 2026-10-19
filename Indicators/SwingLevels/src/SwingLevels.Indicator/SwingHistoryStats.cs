using System.Collections.Generic;

namespace SwingLevels;

/// <summary>One bar's worth of data the stats backtest needs — close included, unlike the live
/// <see cref="SwingLevelEngine"/> which only ever needed high/low.</summary>
internal readonly record struct StatsBar(System.DateTime TimeUtc, double High, double Low, double Close);

/// <summary>Aggregate "did a confirmed swing level ever get closed through, or did it hold for the
/// rest of the available history" — operator's own ask (2026-10-15), confirmed via
/// AskUserQuestion: a level counts as BROKEN the moment any later bar's CLOSE trades beyond it, no
/// distance or time window; otherwise, if the available history runs out first, it counts as HELD.
/// A real counted statistic from THIS instrument's own history, not an invented heuristic — matches
/// this codebase's own established rigor (`srm.pine`'s "Evidence behind the defaults" section backs
/// every probability claim with a real sample size the same way).</summary>
internal sealed class ReversalStats
{
    public int Held { get; set; }
    public int Broken { get; set; }
    public int Total => this.Held + this.Broken;
    public double? Rate => this.Total > 0 ? (double)this.Held / this.Total : null;
}

/// <summary>
/// Operator's own ask (2026-10-15): "keep that logic that shows if its been retested and
/// probability of a reversal on that level." Runs as a ONE-SHOT pass over the full fetched history
/// (not incrementally per bar) — cheap enough at these bar counts to just redo on every refresh
/// rather than maintain rolling state.
///
/// A companion trend-reach-probability backtest (distance-bucketed, ATR-normalized) was built and
/// briefly shipped alongside this one, then removed the same day at the operator's own request
/// ("remove the historical bucket part in the indicator i dont need it") — deleted outright rather
/// than left disabled, per this codebase's own convention of not keeping unused code around "just
/// in case."
/// </summary>
internal static class SwingHistoryStats
{
    public static ReversalStats ComputeReversalStats(IReadOnlyList<StatsBar> bars, int pivotLookback, int minForwardBars)
    {
        var result = new ReversalStats();
        if (bars.Count < (2 * pivotLookback) + minForwardBars + 1) return result;

        for (var pivotIndex = pivotLookback; pivotIndex < bars.Count - pivotLookback; pivotIndex++)
        {
            if (bars.Count - 1 - pivotIndex < minForwardBars) break; // not enough "future" left to judge fairly

            var candidate = bars[pivotIndex];
            var isSwingHigh = true;
            var isSwingLow = true;

            for (var j = pivotIndex - pivotLookback; j <= pivotIndex + pivotLookback; j++)
            {
                if (j == pivotIndex) continue;
                if (bars[j].High >= candidate.High) isSwingHigh = false;
                if (bars[j].Low <= candidate.Low) isSwingLow = false;
            }

            if (!isSwingHigh && !isSwingLow) continue;

            if (isSwingHigh)
            {
                var broken = false;
                for (var k = pivotIndex + 1; k < bars.Count; k++)
                {
                    if (bars[k].Close > candidate.High) { broken = true; break; }
                }
                if (broken) result.Broken++; else result.Held++;
            }

            if (isSwingLow)
            {
                var broken = false;
                for (var k = pivotIndex + 1; k < bars.Count; k++)
                {
                    if (bars[k].Close < candidate.Low) { broken = true; break; }
                }
                if (broken) result.Broken++; else result.Held++;
            }
        }

        return result;
    }
}
