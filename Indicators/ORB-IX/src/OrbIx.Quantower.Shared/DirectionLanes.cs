using System;
using System.Collections.Generic;
using OrbIx.Core.Direction;
using TradingPlatform.BusinessLayer;

namespace OrbIx.Quantower.Shared;

/// <summary>
/// Pulls the chart's bar period off Quantower and hands the decision to Core.
/// </summary>
/// <remarks>
/// THIS TYPE DOES EXACTLY ONE THING, AND THAT IS THE POINT.
///     The lane RULES used to live here, in a Windows-only file the test project
///     cannot reference -- so the single decision determining which lanes exist
///     was the one piece of the direction read with no test on it. A live journal
///     row then showed a lane set nobody could explain offline.
///
///     The rules now live in <see cref="DirectionLaneSelection"/> in Core, where
///     they are exercised directly. What remains here is the platform lookup,
///     which cannot be unit-tested because it needs a running chart.
/// </remarks>
public static class DirectionLanes
{
    /// <summary>
    /// The chart's own period, when it has one, plus each configured higher timeframe.
    /// </summary>
    /// <param name="data">The chart's historical data. May be null.</param>
    /// <param name="includeChartTimeframe">Whether the chart's own period is a lane.</param>
    /// <param name="higherTimeframes">Comma-separated minutes, e.g. "5,15,60".</param>
    public static List<(string Name, TimeSpan Period)> Build(
        HistoricalData? data, bool includeChartTimeframe, string? higherTimeframes) =>
        DirectionLaneSelection.Select(
            ChartPeriod(data), includeChartTimeframe, higherTimeframes);

    /// <summary>
    /// The chart's bar period, or null when it has none.
    /// </summary>
    /// <remarks>
    /// HistoricalData exposes no Period, and HistoryAggregation.GetPeriod is marked
    /// obsolete in the installed SDK, which directs callers to cast to the specific
    /// aggregation. Both facts were read from the assembly, not recalled.
    ///
    /// The cast also surfaces what a fallback would have hidden: a TICK, RENKO or
    /// RANGE-BAR chart genuinely has NO time period. Null says so; defaulting it to
    /// a minute count would invent a lane nobody chose.
    /// </remarks>
    public static TimeSpan? ChartPeriod(HistoricalData? data) =>
        data?.Aggregation is HistoryAggregationTime time && time.Period.Duration > TimeSpan.Zero
            ? time.Period.Duration
            : null;

    /// <summary>A period as it appears on the panel. Delegates to Core.</summary>
    public static string Label(TimeSpan period) => DirectionLaneSelection.Label(period);
}
