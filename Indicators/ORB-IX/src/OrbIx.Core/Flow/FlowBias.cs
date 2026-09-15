using System;
using System.Collections.Generic;

using OrbIx.Core.Config;
using OrbIx.Core.Structure;

namespace OrbIx.Core.Flow;

/// <summary>
/// The reference geometry drawn over the chart's own bars rather than over the footprint: fans,
/// two-tap trend lines, and gamma walls.
///
/// INDEXED BY BAR, WHICH IS WHY THE TIMES ARE CARRIED. Fans and trend lines are computed in bar
/// INDEX space, because that is the space a straight line is straight in — a line laid out in time
/// bends wherever the market was closed. The painter needs the index-to-time mapping to place them,
/// and taking it from anywhere but the array the geometry was computed over would silently shift
/// every line by however much the two disagreed.
/// </summary>
/// <param name="BarTimes">Open time per bar index, for the bars the geometry was computed over.</param>
/// <param name="Fans">Fibonacci fans over the look-back's extremes.</param>
/// <param name="TrendLines">Two-tap lines through the last two confirmed swings of each kind.</param>
/// <param name="Gex">Gamma walls, translated to chart prices.</param>
/// <param name="GexStatus">
/// Where the walls came from and how old they are. ALWAYS SAID when they are drawn: the options
/// source behind the file is gone, so a file that still exists is exactly as stale as its last
/// write, and a wall drawn without its age reads as current.
/// </param>
public sealed record FlowBias(
    DateTime[] BarTimes,
    FibFanGrid[] Fans,
    TrendLine[] TrendLines,
    ChartGexLevel[] Gex,
    string GexStatus)
{
    public static readonly FlowBias Empty = new(
        Array.Empty<DateTime>(), Array.Empty<FibFanGrid>(), Array.Empty<TrendLine>(),
        Array.Empty<ChartGexLevel>(), string.Empty);

    public bool HasGeometry
        => this.Fans.Length > 0 || this.TrendLines.Length > 0 || this.Gex.Length > 0;
}

/// <summary>
/// Builds <see cref="FlowBias"/> from the chart's bars.
///
/// EVERY ENGINE HERE IS ORB-IX'S OWN, and that is the whole reason this is a builder rather than a
/// reimplementation: the fan construction is <see cref="FibFan"/> (MetaTrader's published
/// definition), the swings are <see cref="HhLlEngine"/>'s labels, the walls are
/// <see cref="GexLevels"/>. Aramid Flow already called all three; this calls them from Core, where
/// the composition can be tested.
///
/// NONE OF IT GATES ANYTHING. Reference geometry, drawn and nothing else.
/// </summary>
public static class FlowBiasBuilder
{
    /// <param name="barTimes">Open time per bar, oldest first. Closed bars only.</param>
    /// <param name="highs">Bar highs, same indexing.</param>
    /// <param name="lows">Bar lows, same indexing.</param>
    /// <param name="closes">Bar closes, same indexing.</param>
    /// <param name="labels">Confirmed swing labels, from the structure engine.</param>
    /// <param name="config">The document's settings for these tools.</param>
    /// <param name="display">Which tools are drawing.</param>
    /// <param name="gex">The parsed gamma snapshot, or null when there is no file to read.</param>
    /// <param name="chartLastPrice">
    /// The chart's last price, which sets the basis the index levels are shifted by. A wall is
    /// published against the INDEX and drawn against the FUTURE, and the two differ.
    /// </param>
    /// <param name="gexStatus">Where the walls came from and how old they are.</param>
    public static FlowBias Build(
        IReadOnlyList<DateTime> barTimes,
        IReadOnlyList<double> highs,
        IReadOnlyList<double> lows,
        IReadOnlyList<double> closes,
        IReadOnlyList<HhLlLabel> labels,
        FlowConfig config,
        FlowDisplay display,
        GexSnapshot? gex,
        double chartLastPrice,
        string gexStatus)
    {
        ArgumentNullException.ThrowIfNull(barTimes);
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(display);

        if (!display.AnyEnabled)
            return FlowBias.Empty;

        var times = new DateTime[barTimes.Count];

        for (var i = 0; i < barTimes.Count; i++)
            times[i] = barTimes[i];

        return new FlowBias(
            times,
            display.FibFan ? Fans(highs, lows, closes, config.FibFan) : Array.Empty<FibFanGrid>(),
            display.TrendLines ? Lines(labels) : Array.Empty<TrendLine>(),
            display.Gex ? Walls(config.Gex, gex, chartLastPrice) : Array.Empty<ChartGexLevel>(),
            display.Gex ? gexStatus : string.Empty);
    }

    private static FibFanGrid[] Fans(
        IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes,
        FlowFanConfig fan)
    {
        if (FibFan.Extremes(highs, lows, Math.Max(fan.LookBackBars, 2)) is not { } extremes)
            return Array.Empty<FibFanGrid>();

        var (highBar, high, lowBar, low) = extremes;
        var built = new List<FibFanGrid>(2);

        if (fan.Anchors is FanAnchor.HighToLow or FanAnchor.Both)
        {
            // "From the highest level to the lowest level": the EARLIER extreme is the origin, so
            // the trend line runs forwards in time whichever extreme came first.
            var grid = highBar < lowBar
                ? FibFan.Build(highBar, high, lowBar, low, fan.Ratios)
                : FibFan.Build(lowBar, low, highBar, high, fan.Ratios);

            if (grid is { } g)
                built.Add(g);
        }

        if (fan.Anchors is FanAnchor.HighToCurrent or FanAnchor.Both && closes.Count > 0)
        {
            var lastBar = closes.Count - 1;

            if (FibFan.Build(highBar, high, lastBar, closes[lastBar], fan.Ratios) is { } g)
                built.Add(g);
        }

        return built.ToArray();
    }

    private static TrendLine[] Lines(IReadOnlyList<HhLlLabel> labels)
    {
        var built = new List<TrendLine>(2);

        if (AutoTrendlines.Highs(labels) is { } high)
            built.Add(high);

        if (AutoTrendlines.Lows(labels) is { } low)
            built.Add(low);

        return built.ToArray();
    }

    /// <summary>
    /// The walls, from the file when there is one and from the manual prices otherwise.
    ///
    /// THE MANUAL PRICES ARE NOT A FALLBACK FOR A FAILED READ — they are the way the display works
    /// without a file at all, which is the situation this instrument is permanently in now that the
    /// options source is gone. A file that parsed wins because somebody put it there on purpose.
    /// </summary>
    private static ChartGexLevel[] Walls(FlowGexConfig config, GexSnapshot? gex, double chartLastPrice)
    {
        if (gex is { } snapshot && double.IsFinite(chartLastPrice) && chartLastPrice > 0)
        {
            var translated = GexLevels.Translate(snapshot, chartLastPrice);

            if (translated.Count > 0)
            {
                var levels = new ChartGexLevel[translated.Count];

                for (var i = 0; i < translated.Count; i++)
                    levels[i] = translated[i];

                return levels;
            }
        }

        var manual = GexLevels.Manual(
            config.ManualCallWall, config.ManualPutWall, config.ManualMaxPain);

        var result = new ChartGexLevel[manual.Count];

        for (var i = 0; i < manual.Count; i++)
            result[i] = manual[i];

        return result;
    }
}
