using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>One completed daily bar. Only what an average daily range needs.</summary>
/// <param name="SessionDate">The trading date, used only for ordering and for reporting.</param>
/// <param name="High">Daily high.</param>
/// <param name="Low">Daily low.</param>
public readonly record struct DailyBar(DateOnly SessionDate, double High, double Low)
{
    /// <summary>The day's range. Negative or NaN inputs make this unusable, not zero.</summary>
    public double Range => this.High - this.Low;

    /// <summary>
    /// Whether this bar can contribute. A high below its low is a corrupt bar, not a
    /// zero-range day, and averaging it in would quietly drag the mean down.
    /// </summary>
    public bool IsUsable =>
        !double.IsNaN(this.High) && !double.IsNaN(this.Low)
        && !double.IsInfinity(this.High) && !double.IsInfinity(this.Low)
        && this.High >= this.Low;
}

/// <summary>
/// Average daily range over a fixed lookback.
///
/// §2 grades an opening range by its width as a fraction of average daily range, so with no
/// average daily range there is no grade. The engine already reports that honestly —
/// <see cref="Sessions.OrSnapshot.Gradeable"/> is false when this is zero — and this type
/// exists to supply the number rather than to paper over its absence.
///
/// Pure: it takes bars and returns a figure. Loading them is the platform shell's job, which
/// is what keeps OrbIx.Core free of any platform reference.
/// </summary>
public static class DailyRangeHistory
{
    /// <summary>
    /// Average daily range over the most recent <paramref name="period"/> usable bars.
    ///
    /// Returns zero when there is nothing to average, and that zero means "not measured" —
    /// never "the market did not move". Callers must treat it as absence; the snapshot's
    /// <see cref="Sessions.OrSnapshot.Gradeable"/> already does.
    ///
    /// Fewer bars than the period is not an error. Ten days of history give a ten-day
    /// average, and <paramref name="usedBars"/> reports what it was actually computed from
    /// so a caller can decide whether that is enough rather than assuming.
    /// </summary>
    /// <param name="bars">Daily bars in any order; the most recent by date are taken.</param>
    /// <param name="period">How many days to average. Must be positive.</param>
    /// <param name="usedBars">How many bars actually contributed.</param>
    public static double Average(IEnumerable<DailyBar> bars, int period, out int usedBars)
    {
        if (bars is null)
            throw new ArgumentNullException(nameof(bars));

        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period), period, "The lookback must be positive.");

        var usable = new List<DailyBar>();

        foreach (var bar in bars)
        {
            if (bar.IsUsable)
                usable.Add(bar);
        }

        if (usable.Count == 0)
        {
            usedBars = 0;
            return 0d;
        }

        // Sorted rather than assumed ordered: history arrives newest-first from some feeds
        // and oldest-first from others, and taking "the last N" of the wrong order would
        // average the oldest days while looking correct.
        usable.Sort(static (a, b) => a.SessionDate.CompareTo(b.SessionDate));

        var take = Math.Min(period, usable.Count);
        var sum = 0d;

        for (var i = usable.Count - take; i < usable.Count; i++)
            sum += usable[i].Range;

        usedBars = take;
        return sum / take;
    }

    /// <summary>
    /// Convenience overload for callers that do not need the contributing count.
    /// </summary>
    public static double Average(IEnumerable<DailyBar> bars, int period)
        => Average(bars, period, out _);
}
