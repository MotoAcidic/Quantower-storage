using System;
using System.Collections.Generic;

namespace OrbIx.Core.Flow;

/// <summary>One fan line: from the origin extreme through the level point on the second extreme's vertical.</summary>
public readonly record struct FanLine(double Ratio, int OriginBar, double OriginPrice, int LevelBar, double LevelPrice)
{
    /// <summary>Price on the line at a bar index, extrapolated to the right.</summary>
    public double PriceAt(int bar)
    {
        if (this.LevelBar == this.OriginBar)
            return this.LevelPrice;

        var slope = (this.LevelPrice - this.OriginPrice) / (this.LevelBar - this.OriginBar);
        return this.OriginPrice + (slope * (bar - this.OriginBar));
    }
}

/// <summary>A fan: its trend line and its level lines.</summary>
public readonly record struct FibFanGrid(
    int OriginBar, double OriginPrice, int SecondBar, double SecondPrice, IReadOnlyList<FanLine> Lines);

/// <summary>
/// The Fibonacci Fan as MetaTrader 5's platform help defines the drawing (Analytical
/// Objects → Fibonacci Tools → Fibonacci Fan, read 2026-09-11):
///
///   "a trendline — for example from a trough to the opposing peak is drawn between two
///    extreme points. Then, an 'invisible' vertical line is automatically drawn through the
///    second extreme point. After that, three trend lines intersecting this invisible
///    vertical line at Fibonacci levels of 38.2, 50, and 61.8 percent are drawn from the
///    first extreme point."
///
/// Neither Quantower's nor ATAS's documentation defines their fan (both checked; Quantower's
/// docs index and its query endpoint report no Fibonacci Fan entry), so the construction is
/// taken from the one vendor that publishes it. The level at ratio r on the vertical is the
/// second extreme's price retraced by r of the leg: second − r × (second − origin).
///
/// The presenter's anchors (27:45–28:44): "run the highest fib fan from the highest level
/// to the lowest level, and then the highest current level" — one fan from the lookback's
/// high to its low, another from that high to the current bar. Both are built here from the
/// points the caller resolves; nothing in this type decides which bars are extremes.
/// </summary>
public static class FibFan
{
    /// <summary>MetaTrader's three default levels.</summary>
    public static readonly IReadOnlyList<double> DefaultRatios = new[] { 0.382, 0.5, 0.618 };

    public static FibFanGrid? Build(
        int originBar, double originPrice, int secondBar, double secondPrice, IReadOnlyList<double> ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);

        if (secondBar <= originBar)
            return null;

        if (!double.IsFinite(originPrice) || !double.IsFinite(secondPrice) || originPrice == secondPrice)
            return null;

        var leg = secondPrice - originPrice;
        var lines = new List<FanLine>(ratios.Count);

        foreach (var ratio in ratios)
        {
            if (!double.IsFinite(ratio))
                continue;

            lines.Add(new FanLine(ratio, originBar, originPrice, secondBar, secondPrice - (leg * ratio)));
        }

        return new FibFanGrid(originBar, originPrice, secondBar, secondPrice, lines);
    }

    /// <summary>
    /// The bar indices of the highest high and lowest low over the last <paramref name="lookback"/>
    /// bars of parallel high/low series. Ties resolve to the OLDER bar, so an extreme that
    /// is revisited does not move the anchor.
    /// </summary>
    public static (int HighBar, double High, int LowBar, double Low)? Extremes(
        IReadOnlyList<double> highs, IReadOnlyList<double> lows, int lookback)
    {
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);

        var count = Math.Min(highs.Count, lows.Count);

        if (count == 0 || lookback < 1)
            return null;

        var first = Math.Max(0, count - lookback);
        var highBar = -1;
        var lowBar = -1;
        var high = double.NegativeInfinity;
        var low = double.PositiveInfinity;

        for (var bar = first; bar < count; bar++)
        {
            if (highs[bar] > high)
            {
                high = highs[bar];
                highBar = bar;
            }

            if (lows[bar] < low)
            {
                low = lows[bar];
                lowBar = bar;
            }
        }

        if (highBar < 0 || lowBar < 0)
            return null;

        return (highBar, high, lowBar, low);
    }
}
