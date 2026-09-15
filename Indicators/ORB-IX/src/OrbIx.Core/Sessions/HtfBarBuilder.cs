using System;

namespace OrbIx.Core.Sessions;

/// <summary>One higher-timeframe bar assembled from chart-timeframe bars.</summary>
public readonly record struct HtfBar(
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    bool IsClosed);

/// <summary>
/// Builds fixed-duration higher-timeframe bars from CLOSED chart bars —
/// the bar→bar counterpart of <see cref="BarAggregator"/> (which resamples
/// ticks), sharing its two load-bearing properties verbatim:
///
/// Buckets sit on the absolute wall-clock grid
/// (<c>ticks - ticks % periodTicks</c>), never anchored to the first bar
/// seen, so the HTF grid is identical across sessions and runs.
///
/// An HTF bar closes only when a LATER chart bar proves it closed; empty
/// periods produce no bar; the still-forming bar is available through
/// <see cref="TryPeek"/> and is never marked closed, so a caller cannot
/// treat a partial HTF bar as a confirmation.
///
/// The chart timeframe must divide the HTF period for the aggregation to
/// be exact; a chart bar whose open falls in one bucket while its close
/// belongs to the next would be split silently, so the constructor cannot
/// check it (bar duration arrives per bar) and <see cref="Add"/> refuses
/// a bar that straddles the bucket boundary instead.
/// </summary>
public sealed class HtfBarBuilder
{
    private readonly TimeSpan period;

    private DateTime openTimeUtc;
    private double open;
    private double high;
    private double low;
    private double close;
    private double volume;
    private bool forming;

    public HtfBarBuilder(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period), period, "A bar period must be positive.");
        }

        this.period = period;
    }

    public bool HasOpenBar => this.forming;

    /// <summary>
    /// Adds one CLOSED chart bar.
    /// </summary>
    /// <param name="openUtc">The chart bar's open instant (UTC).</param>
    /// <param name="closeUtc">The chart bar's close instant (UTC).</param>
    /// <param name="open">Chart bar open.</param>
    /// <param name="high">Chart bar high.</param>
    /// <param name="low">Chart bar low.</param>
    /// <param name="close">Chart bar close.</param>
    /// <param name="volume">Chart bar volume.</param>
    /// <param name="closedBar">The HTF bar this chart bar CLOSED, if any.</param>
    /// <returns>True when a closed HTF bar was produced.</returns>
    /// <exception cref="ArgumentException">
    /// The chart bar straddles an HTF bucket boundary — the chart
    /// timeframe does not divide the HTF period, and aggregating it would
    /// silently misassign part of the bar.
    /// </exception>
    public bool Add(
        DateTime openUtc, DateTime closeUtc,
        double open, double high, double low, double close, double volume,
        out HtfBar closedBar)
    {
        var bucket = this.BucketOf(openUtc);
        // The close instant is exclusive: a bar [10:00, 10:05) belongs
        // wholly to the bucket of its open when 10:05 lands on or before
        // the bucket's end.
        if (closeUtc > bucket + this.period)
        {
            throw new ArgumentException(
                $"Chart bar [{openUtc:O}, {closeUtc:O}) straddles the "
                + $"{this.period} bucket starting {bucket:O}; the chart "
                + "timeframe must divide the HTF period.",
                nameof(closeUtc));
        }

        if (this.forming && bucket == this.openTimeUtc)
        {
            if (high > this.high) this.high = high;
            if (low < this.low) this.low = low;
            this.close = close;
            this.volume += volume;
            closedBar = default;
            return false;
        }

        var hadBar = this.forming;
        var completed = this.Snapshot(isClosed: true);

        this.openTimeUtc = bucket;
        this.open = open;
        this.high = high;
        this.low = low;
        this.close = close;
        this.volume = volume;
        this.forming = true;

        closedBar = completed;
        return hadBar;
    }

    /// <summary>Closes the bar still accumulating, if any.</summary>
    public bool Flush(out HtfBar closedBar)
    {
        if (!this.forming)
        {
            closedBar = default;
            return false;
        }

        closedBar = this.Snapshot(isClosed: true);
        this.forming = false;
        return true;
    }

    /// <summary>The forming bar, never marked closed.</summary>
    public bool TryPeek(out HtfBar formingBar)
    {
        if (!this.forming)
        {
            formingBar = default;
            return false;
        }

        formingBar = this.Snapshot(isClosed: false);
        return true;
    }

    private DateTime BucketOf(DateTime instant)
    {
        var periodTicks = this.period.Ticks;
        return new DateTime(instant.Ticks - (instant.Ticks % periodTicks), DateTimeKind.Utc);
    }

    private HtfBar Snapshot(bool isClosed)
        => new(
            this.openTimeUtc, this.openTimeUtc + this.period,
            this.open, this.high, this.low, this.close,
            this.volume, isClosed);
}
