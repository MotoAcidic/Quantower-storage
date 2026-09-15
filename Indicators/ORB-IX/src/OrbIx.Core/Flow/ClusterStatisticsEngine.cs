using System;
using System.Collections.Generic;

namespace OrbIx.Core.Flow;

/// <summary>
/// The rows ATAS Cluster Statistics can show (article 72000602624, "Available
/// Statistics", read 2026-09-11), in the article's order.
/// </summary>
public enum StatRow
{
    /// <summary>"Ask — Volume traded at the Ask."</summary>
    Ask,

    /// <summary>"Bid — Volume traded at the Bid."</summary>
    Bid,

    /// <summary>"Delta — Difference between Ask and Bid volume."</summary>
    Delta,

    /// <summary>"Delta/Volume — Delta as a percentage of total candle volume."</summary>
    DeltaPercent,

    /// <summary>"Session Delta — Cumulative Delta for the current session."</summary>
    SessionDelta,

    /// <summary>"Session Delta/Volume — Session Delta as a percentage of session volume."</summary>
    SessionDeltaPercent,

    /// <summary>"Max Delta — Maximum Delta value within the candle."</summary>
    MaxDelta,

    /// <summary>"Min Delta — Minimum Delta value within the candle."</summary>
    MinDelta,

    /// <summary>"Delta Change — Delta change compared to the previous candle."</summary>
    DeltaChange,

    /// <summary>"Volume — Total traded volume."</summary>
    Volume,

    /// <summary>"Volume/sec — Average traded volume per second."</summary>
    VolumePerSecond,

    /// <summary>"Session Volume — Cumulative session volume."</summary>
    SessionVolume,

    /// <summary>"Trades — Number of executed trades."</summary>
    Trades,

    /// <summary>"Height — Candle height (High − Low)." Reported in ticks.</summary>
    Height,

    /// <summary>"Time — Candle opening time."</summary>
    Time,

    /// <summary>"Duration — Candle duration in seconds."</summary>
    Duration,
}

/// <summary>One bar's column of statistics. Every row's value is here whether or not it is shown.</summary>
public readonly record struct StatColumn(
    DateTime OpenUtc,
    bool IsForming,
    double Ask,
    double Bid,
    double Delta,
    double DeltaPercent,
    double SessionDelta,
    double SessionDeltaPercent,
    double MaxDelta,
    double MinDelta,
    bool HasDeltaExtremes,
    double DeltaChange,
    double Volume,
    double VolumePerSecond,
    double SessionVolume,
    int Trades,
    bool HasTrades,
    double HeightTicks,
    double DurationSeconds)
{
    /// <summary>The numeric value of a row. Time is the open instant as OLE ticks, which callers format themselves.</summary>
    public double Value(StatRow row) => row switch
    {
        StatRow.Ask => this.Ask,
        StatRow.Bid => this.Bid,
        StatRow.Delta => this.Delta,
        StatRow.DeltaPercent => this.DeltaPercent,
        StatRow.SessionDelta => this.SessionDelta,
        StatRow.SessionDeltaPercent => this.SessionDeltaPercent,
        StatRow.MaxDelta => this.MaxDelta,
        StatRow.MinDelta => this.MinDelta,
        StatRow.DeltaChange => this.DeltaChange,
        StatRow.Volume => this.Volume,
        StatRow.VolumePerSecond => this.VolumePerSecond,
        StatRow.SessionVolume => this.SessionVolume,
        StatRow.Trades => this.Trades,
        StatRow.Height => this.HeightTicks,
        StatRow.Time => this.OpenUtc.Ticks,
        StatRow.Duration => this.DurationSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown statistics row."),
    };

    /// <summary>Whether the row carries a measured value for this bar (delta extremes may be unmeasured).</summary>
    public bool IsMeasured(StatRow row) => row switch
    {
        StatRow.MaxDelta or StatRow.MinDelta => this.HasDeltaExtremes,

        // A bar rebuilt from tick history has volume per price and no print counts. Zero would be
        // a reading; this is an absence, and the band shades it rather than printing a number.
        StatRow.Trades => this.HasTrades,

        _ => true,
    };
}

/// <summary>
/// Builds the Cluster Statistics table from the footprint store: one column per bar, the
/// forming bar last, session-cumulative rows reset at the boundary the caller chose.
///
/// COLOUR INTENSITY IS THE ARTICLE'S OWN RULE: "Cluster Statistics uses color intensity to
/// highlight stronger values", with "Proportion By Visible Part Of The Chart" deciding
/// whether the scale is the visible bars or the whole loaded history. That is what the
/// presenter reads as "wait for something bright to pop up" (14:09–15:26): a cell is
/// bright because its value is large relative to the scale, nothing more. The scale is
/// computed by <see cref="ScaleMax"/> over whichever columns the caller passes.
/// </summary>
public static class ClusterStatisticsEngine
{
    /// <summary>Rows the article enables by default: "Delta, Session Delta, and Volume."</summary>
    public static readonly IReadOnlyList<StatRow> DefaultRows =
        new[] { StatRow.Delta, StatRow.SessionDelta, StatRow.Volume };

    public static StatColumn[] Compute(
        IReadOnlyList<FootprintBar> closed, FootprintBar? forming,
        ISessionBoundary boundary, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(closed);
        ArgumentNullException.ThrowIfNull(boundary);

        var count = closed.Count + (forming is null ? 0 : 1);
        var columns = new StatColumn[count];

        var sessionStart = DateTime.MinValue;
        var sessionDelta = 0d;
        var sessionVolume = 0d;
        var previousDelta = double.NaN;
        var sessionOpen = false;

        for (var i = 0; i < count; i++)
        {
            FootprintBar bar;

            if (i < closed.Count)
                bar = closed[i];
            else if (forming is { } inProgress)
                bar = inProgress;
            else
                break;

            var start = boundary.SessionStartUtc(bar.OpenUtc);

            if (!sessionOpen || start != sessionStart)
            {
                sessionStart = start;
                sessionDelta = 0d;
                sessionVolume = 0d;
                sessionOpen = true;
            }

            sessionDelta += bar.Delta;
            sessionVolume += bar.Volume;

            var deltaChange = double.IsNaN(previousDelta) ? 0d : bar.Delta - previousDelta;
            previousDelta = bar.Delta;

            columns[i] = new StatColumn(
                bar.OpenUtc,
                IsForming: !bar.IsClosed,
                Ask: bar.BuyVolume,
                Bid: bar.SellVolume,
                Delta: bar.Delta,
                DeltaPercent: Percent(bar.Delta, bar.Volume),
                SessionDelta: sessionDelta,
                SessionDeltaPercent: Percent(sessionDelta, sessionVolume),
                MaxDelta: bar.MaxDelta,
                MinDelta: bar.MinDelta,
                HasDeltaExtremes: bar.HasDeltaExtremes,
                DeltaChange: deltaChange,
                Volume: bar.Volume,
                VolumePerSecond: bar.VolumePerSecond(nowUtc),
                SessionVolume: sessionVolume,
                Trades: bar.Trades,
                HasTrades: bar.HasTrades,
                HeightTicks: bar.Height / bar.TickSize,
                DurationSeconds: bar.Duration.TotalSeconds);
        }

        return columns;
    }

    /// <summary>
    /// The largest absolute value of a row over a range of columns — the 100% of the
    /// intensity scale. Zero when the range is empty or every value is zero.
    /// </summary>
    public static double ScaleMax(IReadOnlyList<StatColumn> columns, StatRow row, int fromIndex, int toIndex)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var max = 0d;
        var lo = Math.Max(fromIndex, 0);
        var hi = Math.Min(toIndex, columns.Count - 1);

        for (var i = lo; i <= hi; i++)
        {
            if (!columns[i].IsMeasured(row))
                continue;

            var magnitude = Math.Abs(columns[i].Value(row));

            if (magnitude > max)
                max = magnitude;
        }

        return max;
    }

    /// <summary>Intensity in [0,1]: the value's share of the scale. Zero scale gives zero.</summary>
    public static double Intensity(double value, double scaleMax)
    {
        if (!(scaleMax > 0) || !double.IsFinite(value))
            return 0d;

        return Math.Clamp(Math.Abs(value) / scaleMax, 0d, 1d);
    }

    /// <summary>Delta / Volume × 100 — the definition Quantower's own volume-analysis page states.</summary>
    private static double Percent(double part, double whole)
        => whole > 0 ? part / whole * 100d : 0d;
}
