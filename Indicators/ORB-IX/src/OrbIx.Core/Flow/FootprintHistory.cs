using System;
using System.Collections.Generic;

namespace OrbIx.Core.Flow;

/// <summary>
/// The closed footprint bars, in time order: seeded from history, appended as live bars close,
/// addressable by open time, and bounded.
///
/// COLLECTION, NOT ACCUMULATION — AND THAT SPLIT IS THE WHOLE POINT OF THIS TYPE.
///     This began as a store that consumed prints itself. Doing that inside ORB-IX would have
///     put a second accumulator on the same tick stream beside
///     <see cref="OrbIx.Core.Features.FootprintEngine"/>: two walks of the tape, and two sets
///     of numbers free to drift apart at a bucket edge.
///
///     So ingestion was removed rather than moved. <c>FootprintEngine</c> stays the one thing
///     that reads prints — untouched, because everything from the imbalance gate to both
///     playbooks already depends on exactly how it counts. This holds what comes out the far
///     side: whole bars, already closed.
///
/// WHERE THE BARS COME FROM. A live bar is assembled when <c>FootprintEngine</c> closes one,
/// from the cells it just handed over plus the chart bar's own OHLC, and passed to
/// <see cref="Append"/>. Historical bars arrive through <see cref="SeedClosed"/> before live
/// accumulation starts, or through <see cref="BackfillClosed"/> afterwards.
///
/// THE FORMING BAR IS NOT HELD HERE. It is derivable at any moment from
/// <c>FootprintEngine.FormingBarCells</c>, and a stored copy would be a second answer to a
/// question that already has one — stale the instant the next print lands.
/// </summary>
public sealed class FootprintHistory
{
    /// <summary>
    /// Bars retained. Generous because a bar is small and a scan's look-back is the thing that
    /// should bound a search, not the store silently running out of history under it.
    /// </summary>
    public const int DefaultMaxBars = 6000;

    private readonly List<FootprintBar> closed = new();
    private readonly double tickSize;
    private readonly IBarBoundaries boundaries;
    private readonly int maxBars;

    private bool liveStarted;

    /// <summary>
    /// A store whose bars are of a fixed duration. Kept because that is what a time-bar chart
    /// has and because every existing caller and test says it this way; it builds the same
    /// boundaries the arithmetic here used to compute inline.
    /// </summary>
    public FootprintHistory(double tickSize, TimeSpan period, int maxBars = DefaultMaxBars)
        : this(tickSize, new TimeBarBoundaries(period), maxBars)
    {
    }

    public FootprintHistory(double tickSize, IBarBoundaries boundaries, int maxBars = DefaultMaxBars)
    {
        ArgumentNullException.ThrowIfNull(boundaries);

        if (!(tickSize > 0) || double.IsInfinity(tickSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickSize), tickSize, "A positive finite tick size is required.");
        }

        if (maxBars < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBars), maxBars, "At least one bar must be retained.");
        }

        this.tickSize = tickSize;
        this.boundaries = boundaries;
        this.maxBars = maxBars;
    }

    public double TickSize => this.tickSize;

    /// <summary>Where this store's bars begin and end.</summary>
    public IBarBoundaries Boundaries => this.boundaries;

    /// <summary>
    /// The fixed duration of every bar, or NULL on a chart whose bars have none — a tick,
    /// range, Renko, Kagi, Line Break or Points-and-Figures chart.
    /// </summary>
    public TimeSpan? Period => this.boundaries.FixedPeriod;

    /// <summary>Closed bars, oldest first.</summary>
    public IReadOnlyList<FootprintBar> Closed => this.closed;

    /// <summary>Whether a live bar has been appended, which is what closes seeding.</summary>
    public bool LiveStarted => this.liveStarted;

    /// <summary>
    /// The bucket a timestamp falls in, on the absolute wall-clock grid.
    ///
    /// The same convention <c>BarAggregator</c> and <c>DeltaSeriesEngine</c> use, so bars line
    /// up with the chart's own rather than with whenever the first print happened to arrive.
    /// </summary>
    public DateTime BucketOf(DateTime utc) => this.boundaries.OpenOf(utc);

    /// <summary>
    /// Seeds one historical bar, in time order, before any live bar has been appended.
    ///
    /// Refused rather than reordered when it arrives out of sequence: a seeder that has lost
    /// its order is a seeder whose bars cannot be trusted to be whole either, and silently
    /// sorting them would hide that.
    /// </summary>
    public void SeedClosed(FootprintBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (this.liveStarted)
        {
            throw new InvalidOperationException(
                "Historical seeding must complete before live bars are appended.");
        }

        if (!bar.IsClosed)
            throw new ArgumentException("Only closed bars can be seeded.", nameof(bar));

        if (this.closed.Count > 0 && bar.OpenUtc <= this.closed[^1].OpenUtc)
        {
            throw new ArgumentException(
                $"Seeded bar {bar.OpenUtc:O} is not after the last seeded bar "
                + $"{this.closed[^1].OpenUtc:O}.",
                nameof(bar));
        }

        this.closed.Add(bar);
        this.Evict();
    }

    /// <summary>
    /// Appends a bar that has just closed live.
    /// </summary>
    /// <remarks>
    /// The first call ends seeding. A bar that does not follow the last one held is REFUSED
    /// rather than inserted: bars arriving out of order downstream of a single accumulator
    /// means something is wrong upstream, and quietly repairing the order here would hide it
    /// from the one place that could report it.
    /// </remarks>
    public bool Append(FootprintBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (!bar.IsClosed)
            throw new ArgumentException("Only closed bars can be appended.", nameof(bar));

        if (this.closed.Count > 0 && bar.OpenUtc <= this.closed[^1].OpenUtc)
            return false;

        this.liveStarted = true;
        this.closed.Add(bar);
        this.Evict();

        return true;
    }

    /// <summary>
    /// Inserts historical bars in front of what is already held, reporting how many were taken
    /// and how many were refused.
    /// </summary>
    /// <remarks>
    /// THE COUNT OF REFUSALS IS THE POINT, not a diagnostic afterthought. A backfill that
    /// silently kept half its bars would leave a gap in the middle of the history every scan
    /// reads, and every one of them would answer confidently over it.
    ///
    /// A bar is refused when it is null, not closed, out of order against its predecessors in
    /// the same batch, or overlaps a bar already held.
    /// </remarks>
    public (int Accepted, int Refused) BackfillClosed(IReadOnlyList<FootprintBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var earliestHeld = this.closed.Count > 0 ? this.closed[0].OpenUtc : DateTime.MaxValue;

        var accepted = new List<FootprintBar>(bars.Count);
        var refused = 0;
        var previousOpen = DateTime.MinValue;

        foreach (var bar in bars)
        {
            if (bar is null || !bar.IsClosed || bar.OpenUtc <= previousOpen
                || bar.CloseUtc > earliestHeld)
            {
                refused++;
                continue;
            }

            accepted.Add(bar);
            previousOpen = bar.OpenUtc;
        }

        if (accepted.Count > 0)
        {
            this.closed.InsertRange(0, accepted);
            this.Evict();
        }

        return (accepted.Count, refused);
    }

    /// <summary>The bar that opened at an instant, or null when none did.</summary>
    public FootprintBar? FindClosed(DateTime openUtc)
    {
        var index = this.IndexOfClosed(openUtc);
        return index >= 0 ? this.closed[index] : null;
    }

    /// <summary>Index of the bar that opened at an instant, or -1.</summary>
    public int IndexOfClosed(DateTime openUtc)
    {
        // Bars are kept in time order, so a binary search is exact.
        var lo = 0;
        var hi = this.closed.Count - 1;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var candidate = this.closed[mid].OpenUtc;

            if (candidate == openUtc)
                return mid;

            if (candidate < openUtc)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return -1;
    }

    /// <summary>Bars opening at or after an instant, oldest first.</summary>
    public IReadOnlyList<FootprintBar> ClosedSince(DateTime fromUtc)
    {
        var result = new List<FootprintBar>();

        for (var i = this.closed.Count - 1; i >= 0; i--)
        {
            if (this.closed[i].OpenUtc < fromUtc)
                break;

            result.Add(this.closed[i]);
        }

        result.Reverse();
        return result;
    }

    public void Reset()
    {
        this.closed.Clear();
        this.liveStarted = false;
    }

    /// <summary>Drops the oldest bars once the retention bound is passed.</summary>
    private void Evict()
    {
        while (this.closed.Count > this.maxBars)
            this.closed.RemoveAt(0);
    }
}
