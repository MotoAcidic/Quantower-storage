using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbIx.Core.Features;

/// <summary>One completed daily bar, with the close the range calculation does not need.</summary>
/// <param name="Date">The session date the bar covers.</param>
/// <param name="High">Session high.</param>
/// <param name="Low">Session low.</param>
/// <param name="Close">Session close.</param>
public readonly record struct DailySessionBar(DateOnly Date, double High, double Low, double Close);

/// <summary>
/// The reference levels every chart carries: yesterday's extremes and close, and last week's
/// extremes.
///
/// These are derived from daily bars the indicator ALREADY LOADS. The average-daily-range
/// calculation reads them, uses the high and the low, and throws the bars away — so prior-day
/// high, low and close were sitting one field away from being drawn for as long as the
/// indicator has existed.
///
/// NOTHING IS INVENTED WHERE HISTORY IS MISSING. With no prior day, no prior-day level is
/// produced; the caller draws nothing rather than a zero, which on a chart would be a line at
/// the bottom of the axis that looks like a real price.
/// </summary>
public static class ReferenceLevels
{
    /// <summary>
    /// Prior-day and prior-week levels as of a session date.
    /// </summary>
    /// <param name="bars">Daily bars in any order. Only those strictly BEFORE the date count.</param>
    /// <param name="asOf">The session being traded.</param>
    public static IReadOnlyList<Level> FromDailyBars(IReadOnlyList<DailySessionBar> bars, DateOnly asOf)
    {
        if (bars is null)
            throw new ArgumentNullException(nameof(bars));

        // Strictly before: today's own bar is still forming, and using it would make
        // "yesterday's high" move during the session.
        var earlier = bars
            .Where(b => b.Date < asOf && IsUsable(b))
            .OrderByDescending(b => b.Date)
            .ToList();

        if (earlier.Count == 0)
            return Array.Empty<Level>();

        var levels = new List<Level>(5);
        var prior = earlier[0];
        var priorUtc = prior.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        levels.Add(new Level(prior.High, LevelKind.PriorDayHigh, priorUtc, "PDH"));
        levels.Add(new Level(prior.Low, LevelKind.PriorDayLow, priorUtc, "PDL"));
        levels.Add(new Level(prior.Close, LevelKind.PriorDayClose, priorUtc, "PDC"));

        // The most recent completed week, which is the ISO week before the one `asOf` falls in.
        // Taking "the last five bars" instead would silently mean something different across a
        // holiday, and the label says WEEK.
        var currentWeek = WeekOf(asOf);
        var lastWeek = earlier.Where(b => WeekOf(b.Date) < currentWeek).ToList();

        if (lastWeek.Count == 0)
            return levels;

        var week = WeekOf(lastWeek[0].Date);
        var weekBars = lastWeek.Where(b => WeekOf(b.Date) == week).ToList();
        var weekUtc = weekBars[0].Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        levels.Add(new Level(weekBars.Max(b => b.High), LevelKind.PriorWeekHigh, weekUtc, "PWH"));
        levels.Add(new Level(weekBars.Min(b => b.Low), LevelKind.PriorWeekLow, weekUtc, "PWL"));

        return levels;
    }

    /// <summary>
    /// A bar is usable when its prices are real and ordered.
    ///
    /// A high below its low is not a bar with an odd shape, it is a bar that did not come
    /// through correctly, and averaging or drawing it would put a fictional price on the chart.
    /// </summary>
    private static bool IsUsable(DailySessionBar bar)
        => !double.IsNaN(bar.High) && !double.IsNaN(bar.Low) && !double.IsNaN(bar.Close)
           && bar.High > 0 && bar.Low > 0 && bar.Close > 0
           && bar.High >= bar.Low;

    /// <summary>
    /// A sortable week key.
    ///
    /// Weeks are compared, never displayed, so this only has to be monotonic and stable across
    /// a year boundary — which is why it is days-since-epoch divided down rather than a
    /// calendar week number, whose numbering resets and would make the first week of January
    /// sort before the last week of December.
    /// </summary>
    private static int WeekOf(DateOnly date)
        => (date.DayNumber - (int)date.DayOfWeek) / 7;
}
