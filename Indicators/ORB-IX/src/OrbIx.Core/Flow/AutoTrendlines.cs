using System;
using System.Collections.Generic;
using OrbIx.Core.Structure;

namespace OrbIx.Core.Flow;

/// <summary>A straight line through two bar/price points, extended to the right.</summary>
public readonly record struct TrendLine(int StartBar, double StartPrice, int EndBar, double EndPrice, bool IsHighLine)
{
    /// <summary>Price on the line at a bar index, extrapolated past the second point.</summary>
    public double PriceAt(int bar)
    {
        if (this.EndBar == this.StartBar)
            return this.EndPrice;

        var slope = (this.EndPrice - this.StartPrice) / (this.EndBar - this.StartBar);
        return this.StartPrice + (slope * (bar - this.StartBar));
    }
}

/// <summary>
/// The presenter's "two tap" trend lines (29:22–29:40): "a basic trend line tool and
/// you're going to run the high — two tap — and then a low trend line, two tap". Two taps
/// are two swing points, so the high line passes through the last two confirmed swing
/// highs and the low line through the last two confirmed swing lows, both from the
/// structure <see cref="HhLlEngine"/> already finds.
///
/// A swing is confirmed rightBars after it forms (the engine's own documentation), so the
/// lines move only when structure does; that is the trade-off of taking them from confirmed
/// pivots rather than from eyeballed extremes, and it is what makes them reproducible.
/// </summary>
public static class AutoTrendlines
{
    public static TrendLine? Highs(IReadOnlyList<HhLlLabel> labels)
        => Through(labels, isHigh: true);

    public static TrendLine? Lows(IReadOnlyList<HhLlLabel> labels)
        => Through(labels, isHigh: false);

    private static TrendLine? Through(IReadOnlyList<HhLlLabel> labels, bool isHigh)
    {
        ArgumentNullException.ThrowIfNull(labels);

        HhLlLabel? newer = null;

        for (var i = labels.Count - 1; i >= 0; i--)
        {
            var label = labels[i];

            if (IsHigh(label.Kind) != isHigh)
                continue;

            if (newer is null)
            {
                newer = label;
                continue;
            }

            // Two labels on one bar are one tap, not two.
            if (label.Bar == newer.Value.Bar)
                continue;

            return new TrendLine(label.Bar, label.Price, newer.Value.Bar, newer.Value.Price, isHigh);
        }

        return null;
    }

    private static bool IsHigh(HhLlLabelKind kind)
        => kind is HhLlLabelKind.HigherHigh or HhLlLabelKind.LowerHigh;
}
