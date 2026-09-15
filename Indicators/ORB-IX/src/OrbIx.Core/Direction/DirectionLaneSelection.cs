using System;
using System.Collections.Generic;

namespace OrbIx.Core.Direction;

/// <summary>
/// Decides which timeframes the direction panel reads.
/// </summary>
/// <remarks>
/// WHY THIS IS IN CORE AND NOT BESIDE THE RENDERER
///     It used to live in the Quantower-only shared folder, which the test project
///     cannot reference at all — so the one decision that determines WHICH lanes
///     exist was the one piece of the direction read with no test on it.
///
///     That was not theoretical. A live journal row showed a lane set of exactly
///     {15m, 1h} where the defaults should have produced four lanes, and the
///     behaviour could not be reproduced offline to find out why. Untestable code
///     is code whose defects can only be found in production.
///
///     The platform shim now does one thing — pull a TimeSpan off the chart — and
///     every rule below is exercised directly.
/// </remarks>
public static class DirectionLaneSelection
{
    /// <summary>
    /// The chart's own period, when it has one, plus each requested higher timeframe.
    /// </summary>
    /// <param name="chartPeriod">
    /// The chart's bar period, or null when it has none. A TICK, RENKO or RANGE-BAR
    /// chart genuinely has no time period, and null says so rather than pretending
    /// to a minute count nobody chose.
    /// </param>
    /// <param name="includeChartTimeframe">Whether the chart's own period is a lane.</param>
    /// <param name="higherTimeframes">Comma-separated minutes, e.g. "5,15,60".</param>
    public static List<(string Name, TimeSpan Period)> Select(
        TimeSpan? chartPeriod, bool includeChartTimeframe, string? higherTimeframes)
    {
        var lanes = new List<(string, TimeSpan)>();

        TimeSpan? chart = chartPeriod is { } c && c > TimeSpan.Zero ? c : null;

        if (includeChartTimeframe && chart is { } own)
            lanes.Add((Label(own), own));

        foreach (string part in (higherTimeframes ?? string.Empty).Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out int minutes) || minutes <= 0)
                continue;

            var period = TimeSpan.FromMinutes(minutes);

            // A "higher" timeframe at or below the chart's own is not higher; the
            // chart lane already covers it. Only skip when that lane actually
            // exists, or the period would be dropped with nothing standing in.
            if (chart is { } bound && period <= bound && includeChartTimeframe)
                continue;

            string name = Label(period);

            // A repeated label is refused outright by MultiTimeframeStructure, since
            // one panel row would hide two votes that both still counted. Dropping
            // the duplicate here keeps that from being an exception.
            if (lanes.Exists(l => l.Item1 == name))
                continue;

            lanes.Add((name, period));
        }

        // With everything filtered out there would be nothing to read and the panel
        // would say UNDECIDED forever with no way to tell why. Five minutes is the
        // documented floor, and it is a stated choice rather than a silent one.
        if (lanes.Count == 0)
            lanes.Add((Label(TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5)));

        return lanes;
    }

    /// <summary>
    /// A period as it appears on the panel: <c>15s</c>, <c>5m</c>, <c>1h</c>.
    /// </summary>
    /// <remarks>
    /// SUB-MINUTE PERIODS GET SECONDS, and that is a fix rather than a flourish.
    /// The first version formatted every period as whole minutes, so a 15-second
    /// chart rendered as "0m" — and any two sub-minute periods collapsed onto the
    /// same label, at which point the duplicate-label rule silently dropped one.
    /// </remarks>
    public static string Label(TimeSpan period)
    {
        if (period < TimeSpan.FromMinutes(1))
            return $"{period.TotalSeconds:0}s";

        return period.TotalMinutes >= 60 && period.TotalMinutes % 60 == 0
            ? $"{period.TotalHours:0}h"
            : $"{period.TotalMinutes:0}m";
    }
}
