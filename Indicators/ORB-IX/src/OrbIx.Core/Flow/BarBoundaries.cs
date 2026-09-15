using System;
using System.Collections.Generic;

namespace OrbIx.Core.Flow;

/// <summary>
/// Where a chart's bars begin and end.
///
/// THE ASSUMPTION THIS REPLACES, AND WHAT IT COST. The footprint engine took a
/// <see cref="TimeSpan"/> and derived every boundary from it by arithmetic — truncate a
/// timestamp to get a bar's open, add the period to get its close, multiply to extend a line
/// N bars. That is correct on a time-bar chart and MEANINGLESS on any other, so the indicator
/// refused to build the engine at all without a period. Measured on the operator's own MNQ
/// tick chart, 2026-09-14T20:19Z: 1,413 prints judged by the aggressor check on that chart
/// while the flow frame reported "closed bars 0, stat columns 0, counter none". Ticks were
/// arriving and nothing was being built from them.
///
/// A chart knows where its own bars begin. This lets the engine ask instead of calculate.
///
/// TIME BARS KEEP THE ARITHMETIC, EXACTLY. <see cref="TimeBarBoundaries"/> is the same
/// truncation, the same addition and the same multiplication that were written inline before,
/// so a time-bar chart cannot change behaviour by adopting this — which is the property the
/// whole change is staged around.
/// </summary>
public interface IBarBoundaries
{
    /// <summary>The open of the bar a timestamp falls in.</summary>
    DateTime OpenOf(DateTime utc);

    /// <summary>The close of the bar that opened at <paramref name="openUtc"/>.</summary>
    DateTime CloseOf(DateTime openUtc);

    /// <summary>
    /// The open of the bar <paramref name="bars"/> bars after the one that opened at
    /// <paramref name="openUtc"/> — how far right a "print line for N bars" reaches.
    /// </summary>
    DateTime OpenAfter(DateTime openUtc, int bars);

    /// <summary>
    /// The fixed duration of every bar, and NULL when bars have no fixed duration.
    ///
    /// Null is the answer on a tick, range, Renko, Kagi, Line Break or Points-and-Figures
    /// chart, and callers must treat it as a real answer rather than a missing one. It exists
    /// for the one caller that legitimately needs a duration: the stacked-imbalance floors were
    /// swept per bar period, so the swept table applies only where this is non-null.
    /// </summary>
    TimeSpan? FixedPeriod { get; }
}

/// <summary>
/// Bars of a fixed duration, on the absolute wall-clock grid.
///
/// THE GRID IS ABSOLUTE AND THAT IS DELIBERATE, carried over verbatim from the code this
/// replaces: the same convention <c>BarAggregator</c> and <c>DeltaSeriesEngine</c> use, so bars
/// line up with the chart's own rather than with whenever the first print happened to arrive.
/// </summary>
public sealed class TimeBarBoundaries : IBarBoundaries
{
    private readonly TimeSpan period;

    public TimeBarBoundaries(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period), period, "A bar period must be positive.");
        }

        this.period = period;
    }

    public TimeSpan? FixedPeriod => this.period;

    public DateTime OpenOf(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % this.period.Ticks), DateTimeKind.Utc);

    public DateTime CloseOf(DateTime openUtc) => openUtc + this.period;

    /// <summary>
    /// <paramref name="bars"/> is floored at one, so a visibility configured with zero print
    /// bars still draws a line of one bar rather than a zero-length one. That floor was applied
    /// at the call site before this existed and moves here with it, so there is one place that
    /// decides it instead of every caller remembering to.
    /// </summary>
    public DateTime OpenAfter(DateTime openUtc, int bars)
        => openUtc + (this.period * Math.Max(bars, 1));
}

/// <summary>
/// Bars whose boundaries the CHART owns: tick, range, Renko, Kagi, Line Break and
/// Points-and-Figures. Their opens are read from the platform and refreshed as bars arrive.
///
/// WHY THE OPENS ARE PUSHED IN RATHER THAN LISTENED FOR. The platform raises an event when a new
/// real-time bar starts (UpdateReason.NewBar, read from the installed assembly: "Indicates a
/// start of new real-time bar"), and consuming it directly would be the obvious design and the
/// wrong one. Prints arrive on a feed thread into a ring and are drained inside the fold; that
/// event arrives on the platform's calculation thread. A boundary processed out of order against
/// the prints around it puts those prints in the wrong bar, silently and unreproducibly. Reading
/// the chart's bar opens during the fold — on the same thread that drains the prints — has no
/// such race, and the fold already reads the chart's bars for seeding.
///
/// NOTHING IS EXTRAPOLATED. Every answer is a recorded open or the instant of the last refresh;
/// no bar duration is averaged, guessed or projected. On these charts a bar's length is a
/// property of the market, not of the chart, so an average of it describes nothing.
/// </summary>
public sealed class PlatformBarBoundaries : IBarBoundaries
{
    private DateTime[] opens = Array.Empty<DateTime>();
    private DateTime asOfUtc = DateTime.MinValue;

    /// <summary>
    /// Bars have no fixed duration here, and NULL says exactly that. It is an answer: a caller
    /// that needs a duration — the swept stacked-imbalance floor is the only one — learns that
    /// its number does not apply to this chart rather than receiving one that looks usable.
    /// </summary>
    public TimeSpan? FixedPeriod => null;

    /// <summary>Whether any bar open has been read yet. Nothing can be bucketed before one is.</summary>
    public bool Known => this.opens.Length > 0;

    /// <summary>Bar opens held, for reporting how much the boundaries rest on.</summary>
    public int Count => this.opens.Length;

    /// <summary>The instant of the most recent refresh — how far the newest bar is known to run.</summary>
    public DateTime AsOfUtc => this.asOfUtc;

    /// <summary>
    /// Takes the chart's bar opens, oldest first, and the instant they were read.
    ///
    /// ORDER IS CHECKED, NOT ASSUMED. The platform's bar indexer defaults to an origin of End,
    /// where index 0 is the NEWEST bar and the sequence runs backwards; a caller that forgets to
    /// ask for Begin hands over a reversed list, and a reversed list bucketed by binary search
    /// returns confident nonsense rather than failing. This refuses it instead.
    /// </summary>
    public void Refresh(IReadOnlyList<DateTime> opensOldestFirst, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(opensOldestFirst);

        var taken = new DateTime[opensOldestFirst.Count];

        for (var i = 0; i < opensOldestFirst.Count; i++)
        {
            if (i > 0 && opensOldestFirst[i] <= opensOldestFirst[i - 1])
            {
                throw new ArgumentException(
                    "Bar opens must be strictly increasing, oldest first. A reversed or "
                    + "duplicated sequence buckets every print into the wrong bar.",
                    nameof(opensOldestFirst));
            }

            taken[i] = opensOldestFirst[i];
        }

        if (taken.Length > 0 && nowUtc < taken[^1])
        {
            throw new ArgumentOutOfRangeException(
                nameof(nowUtc), nowUtc, "The read instant cannot precede the newest bar's open.");
        }

        this.opens = taken;
        this.asOfUtc = nowUtc;
    }

    /// <summary>
    /// Moves the read instant on without re-reading the bars.
    ///
    /// WHY IT IS SEPARATE FROM Refresh. The caller refreshes on every fold, and folds are far
    /// more frequent than bars: re-reading a thousand bar opens from the platform several times
    /// a second to discover that none of them changed is work that buys nothing. When the bar
    /// count is unchanged the only thing that has moved is how far the newest bar has run, and
    /// that is this.
    /// </summary>
    public void Touch(DateTime nowUtc)
    {
        if (this.opens.Length > 0 && nowUtc < this.opens[^1])
        {
            throw new ArgumentOutOfRangeException(
                nameof(nowUtc), nowUtc, "The read instant cannot precede the newest bar's open.");
        }

        this.asOfUtc = nowUtc;
    }

    /// <summary>
    /// The open of the bar a timestamp falls in.
    ///
    /// A timestamp OLDER than every recorded open returns the oldest one: history the chart no
    /// longer holds cannot be bucketed, and the engine already refuses a print whose bar has
    /// closed. Returning the oldest makes such a print visibly late rather than inventing a bar
    /// before the chart's own history.
    /// </summary>
    public DateTime OpenOf(DateTime utc)
    {
        if (this.opens.Length == 0)
        {
            throw new InvalidOperationException(
                "No bar open has been read from the chart yet, so no print can be placed in a bar.");
        }

        var index = Array.BinarySearch(this.opens, utc);

        // BinarySearch returns the complement of the next-larger index when there is no exact
        // match; one before that is the bar containing it, floored at the oldest held.
        if (index < 0)
            index = Math.Max(~index - 1, 0);

        return this.opens[index];
    }

    /// <summary>
    /// The close of the bar that opened at <paramref name="openUtc"/>: the open of the next bar.
    ///
    /// For the NEWEST bar there is no next open, and its close is genuinely unknown — the market
    /// decides it. The instant of the last refresh is returned, which is how far the bar is known
    /// to have run, and callers building a forming bar mark that close provisional.
    /// </summary>
    public DateTime CloseOf(DateTime openUtc)
    {
        if (this.opens.Length == 0)
        {
            throw new InvalidOperationException(
                "No bar open has been read from the chart yet, so no bar's close is known.");
        }

        var index = Array.BinarySearch(this.opens, openUtc);

        if (index < 0)
            index = Math.Max(~index - 1, 0);

        if (index + 1 < this.opens.Length)
            return this.opens[index + 1];

        // The newest bar. Never before its own open, so a bar read in the same instant it opened
        // still closes after it starts.
        return this.asOfUtc > this.opens[index] ? this.asOfUtc : this.opens[index].AddTicks(1);
    }

    /// <summary>
    /// The open of the bar <paramref name="bars"/> bars on from the one at
    /// <paramref name="openUtc"/>, CLAMPED TO THE NEWEST BAR THE CHART HAS.
    ///
    /// A line asked to run twenty bars to the right, five bars from the end of the chart, has no
    /// fifteenth bar to reach — those bars have not happened. Clamping stops it there and it
    /// grows as bars arrive, because the spans are recomputed every fold. The alternative is to
    /// project a bar length into the future, and on these charts bar length is a property of the
    /// market: projecting it would draw a line to a time the chart may not reach for an hour.
    /// </summary>
    public DateTime OpenAfter(DateTime openUtc, int bars)
    {
        if (this.opens.Length == 0)
        {
            throw new InvalidOperationException(
                "No bar open has been read from the chart yet, so no line length can be measured.");
        }

        var index = Array.BinarySearch(this.opens, openUtc);

        if (index < 0)
            index = Math.Max(~index - 1, 0);

        var target = Math.Min(index + Math.Max(bars, 1), this.opens.Length - 1);

        // A line must still have length when its level sits on the newest bar and there is
        // nothing to its right yet.
        return target > index ? this.opens[target] : this.CloseOf(openUtc);
    }
}
