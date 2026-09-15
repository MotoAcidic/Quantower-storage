using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Features;

/// <summary>Where a delta bar's numbers came from.</summary>
public enum DeltaSource
{
    /// <summary>Accumulated live from classified prints.</summary>
    LiveTicks,

    /// <summary>Seeded from the platform's volume-analysis backfill.</summary>
    VolumeAnalysis,
}

/// <summary>
/// One chart-timeframe delta bar. Delta is signed volume over CLASSIFIED
/// prints only; <see cref="Unknowns"/> counts the prints excluded for a
/// missing aggressor, so the display can state its own coverage instead
/// of implying every contract was classified.
/// </summary>
public readonly record struct DeltaBar(
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    double Delta,
    double Volume,
    int Buys,
    int Sells,
    int Unknowns,
    double High,
    double Low,
    double Close,
    DeltaSource Source,
    double CumulativeAfter = 0);

/// <summary>
/// Price/flow divergence marker, display only. Keyed by the bar's open
/// instant rather than its index so the marker survives bar eviction.
/// </summary>
public readonly record struct DeltaDivergence(DateTime BarOpenUtc, bool BearishFlow);

/// <summary>
/// Session-anchored per-bar delta over the chart timeframe, per pinned
/// rule 5 of the approved plan (2026-08-28):
///
///   delta = Σ(+size buy aggressor, −size sell aggressor); Unknown
///   aggressor EXCLUDED and counted. Cumulative delta anchors at the
///   session open (<see cref="OnSessionOpen"/> resets the anchor).
///   Divergence marker: a new session CLOSING high whose cumulative delta
///   is below the cumulative delta at the prior session closing high
///   (mirror for lows) — display only.
///
/// Buckets sit on the same absolute wall-clock grid as
/// <see cref="Sessions.BarAggregator"/> so bars line up with the chart's
/// own bars; a bucket closes only when a later print proves it. The
/// series is DISPLAY ONLY: trial 018 measured delta-agreement
/// anti-predictive on MNQ 1m — this engine informs, it never gates.
/// </summary>
public sealed class DeltaSeriesEngine
{
    /// <summary>Bar cap; the oldest bar is evicted past it (a chart shows far fewer).</summary>
    public const int MaxBars = 2000;

    private readonly TimeSpan period;
    private readonly List<DeltaBar> bars = new();
    private readonly List<DeltaDivergence> divergences = new();

    // forming-bucket accumulators
    private DateTime openTimeUtc;
    private double delta;
    private double volume;
    private int buys;
    private int sells;
    private int unknowns;
    private double high;
    private double low;
    private double close;
    private bool forming;

    // session-anchored state
    private double cumulativeAtAnchor;
    private double sessionMaxClose = double.NaN;
    private double cumAtMaxClose = double.NaN;
    private double sessionMinClose = double.NaN;
    private double cumAtMinClose = double.NaN;

    public DeltaSeriesEngine(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period), period, "A bar period must be positive.");
        }

        this.period = period;
    }

    public IReadOnlyList<DeltaBar> Bars => this.bars;

    public IReadOnlyList<DeltaDivergence> Divergences => this.divergences;

    /// <summary>Cumulative delta since the session anchor, closed bars only.</summary>
    public double CumulativeDelta { get; private set; }

    /// <summary>
    /// Resets the session anchor: cumulative delta restarts from zero and
    /// the divergence extremes forget the previous session, per pinned
    /// rule 5 ("anchored at the session open").
    /// </summary>
    public void OnSessionOpen()
    {
        this.cumulativeAtAnchor = 0;
        this.CumulativeDelta = 0;
        this.sessionMaxClose = double.NaN;
        this.cumAtMaxClose = double.NaN;
        this.sessionMinClose = double.NaN;
        this.cumAtMinClose = double.NaN;
    }

    /// <summary>
    /// Seeds one HISTORICAL bar (volume-analysis backfill), in time order,
    /// before any live print. Refused once live accumulation has begun —
    /// splicing history under a running series would double-count.
    /// </summary>
    public void SeedHistorical(in DeltaBar bar)
    {
        if (this.forming || this.bars.Count > 0 && this.bars[^1].Source == DeltaSource.LiveTicks)
        {
            throw new InvalidOperationException(
                "Historical seeding must complete before live accumulation starts.");
        }

        if (this.bars.Count > 0 && bar.OpenTimeUtc <= this.bars[^1].OpenTimeUtc)
        {
            throw new ArgumentException(
                $"Seeded bar {bar.OpenTimeUtc:O} is not after the last "
                + $"seeded bar {this.bars[^1].OpenTimeUtc:O}.", nameof(bar));
        }

        this.bars.Add(bar with { Source = DeltaSource.VolumeAnalysis });
        this.AdvanceSessionState(this.bars[^1]);
        this.bars[^1] = this.bars[^1] with { CumulativeAfter = this.CumulativeDelta };
        this.Evict();
    }

    /// <summary>One print. Returns true when a bucket closed.</summary>
    public bool Add(in TickEvent tick, out DeltaBar closedBar)
    {
        var bucket = new DateTime(
            tick.TimestampUtc.Ticks - (tick.TimestampUtc.Ticks % this.period.Ticks),
            DateTimeKind.Utc);

        if (this.forming && bucket == this.openTimeUtc)
        {
            this.Accumulate(tick);
            closedBar = default;
            return false;
        }

        var hadBar = this.forming;
        DeltaBar completed = default;
        if (hadBar)
        {
            completed = this.Snapshot();
            this.bars.Add(completed);
            this.AdvanceSessionState(completed);
            completed = completed with { CumulativeAfter = this.CumulativeDelta };
            this.bars[^1] = completed;
            this.Evict();
        }

        this.openTimeUtc = bucket;
        this.delta = 0;
        this.volume = 0;
        this.buys = 0;
        this.sells = 0;
        this.unknowns = 0;
        this.high = tick.Price;
        this.low = tick.Price;
        this.close = tick.Price;
        this.forming = true;
        this.Accumulate(tick);

        closedBar = completed;
        return hadBar;
    }

    /// <summary>The forming bucket, never fed to session state.</summary>
    public bool TryPeek(out DeltaBar formingBar)
    {
        if (!this.forming)
        {
            formingBar = default;
            return false;
        }

        formingBar = this.Snapshot();
        return true;
    }

    private void Accumulate(in TickEvent tick)
    {
        if (tick.Price > this.high) this.high = tick.Price;
        if (tick.Price < this.low) this.low = tick.Price;
        this.close = tick.Price;
        this.volume += tick.Size;
        this.delta += tick.SignedSize;
        switch (tick.Aggressor)
        {
            case Aggressor.Buy: this.buys++; break;
            case Aggressor.Sell: this.sells++; break;
            default: this.unknowns++; break;
        }
    }

    private DeltaBar Snapshot()
        => new(
            this.openTimeUtc, this.openTimeUtc + this.period,
            this.delta, this.volume, this.buys, this.sells, this.unknowns,
            this.high, this.low, this.close, DeltaSource.LiveTicks);

    private void AdvanceSessionState(in DeltaBar bar)
    {
        this.CumulativeDelta = this.cumulativeAtAnchor + bar.Delta;
        this.cumulativeAtAnchor = this.CumulativeDelta;

        if (double.IsNaN(this.sessionMaxClose) || bar.Close > this.sessionMaxClose)
        {
            if (!double.IsNaN(this.cumAtMaxClose) && this.CumulativeDelta < this.cumAtMaxClose)
                this.divergences.Add(new DeltaDivergence(bar.OpenTimeUtc, BearishFlow: true));

            this.sessionMaxClose = bar.Close;
            this.cumAtMaxClose = this.CumulativeDelta;
        }

        if (double.IsNaN(this.sessionMinClose) || bar.Close < this.sessionMinClose)
        {
            if (!double.IsNaN(this.cumAtMinClose) && this.CumulativeDelta > this.cumAtMinClose)
                this.divergences.Add(new DeltaDivergence(bar.OpenTimeUtc, BearishFlow: false));

            this.sessionMinClose = bar.Close;
            this.cumAtMinClose = this.CumulativeDelta;
        }
    }

    private void Evict()
    {
        while (this.bars.Count > MaxBars)
            this.bars.RemoveAt(0);
    }
}
