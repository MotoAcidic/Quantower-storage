using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Direction;

/// <summary>
/// The flat bands, which decide when a reading is too small to be a direction.
/// </summary>
/// <param name="VwapFlatTicks">
/// Half-width, in ticks, of the band around VWAP inside which price counts as ON it.
/// </param>
/// <param name="DeltaFlatContracts">
/// Magnitude of cumulative delta inside which flow counts as flat.
/// </param>
/// <remarks>
/// NEITHER IS MEASURED AND BOTH ARE STATED. Price is never exactly on VWAP and
/// delta is never exactly zero, so without bands both would vote on the last tick
/// of noise. Where the band belongs is a trading judgement; these are defaults, not
/// findings, and they live in configuration so changing one is visible.
/// </remarks>
public readonly record struct DirectionSettings(
    double VwapFlatTicks, double DeltaFlatContracts)
{
    /// <summary>Two ticks either side of VWAP, and 100 contracts of delta.</summary>
    public static DirectionSettings Default { get; } = new(2d, 100d);
}

/// <summary>
/// Answers "which way is it going right now" from the tick stream.
/// </summary>
/// <remarks>
/// IT DESCRIBES THE PRESENT. It is not a forecast, it carries no claim that any of
/// its inputs predict returns, and it decides nothing — it reports.
///
/// IT OWNS ITS OWN DELTA AND SESSION RANGE so that a host indicator needs only to
/// forward prints. That is what lets the standalone indicator and ORB-IX produce
/// identical panels without ORB-IX's session machinery being a prerequisite for
/// reading direction.
/// </remarks>
public sealed class DirectionEngine
{
    private readonly MultiTimeframeStructure structure;
    private readonly DirectionSettings settings;

    private double cumulativeDelta;
    private long classified;
    private double sessionHigh = double.NaN;
    private double sessionLow = double.NaN;

    /// <summary>
    /// Creates the engine.
    /// </summary>
    /// <param name="periods">Label and period for each structure lane, in display order.</param>
    /// <param name="leftBars">Pivot lookback, matching the chart's HH/LL setting.</param>
    /// <param name="rightBars">Pivot lookforward, matching the chart's HH/LL setting.</param>
    /// <param name="settings">The flat bands.</param>
    public DirectionEngine(
        IReadOnlyList<(string Name, TimeSpan Period)> periods,
        int leftBars, int rightBars, DirectionSettings settings)
    {
        this.structure = new MultiTimeframeStructure(periods, leftBars, rightBars);
        this.settings = settings;
    }

    /// <summary>Today's high-to-low range so far, or NaN before the first print.</summary>
    public double SessionRange =>
        double.IsNaN(this.sessionHigh) || double.IsNaN(this.sessionLow)
            ? double.NaN
            : this.sessionHigh - this.sessionLow;

    /// <summary>Session cumulative delta, in contracts.</summary>
    public double CumulativeDelta => this.cumulativeDelta;

    /// <summary>How many prints carried a usable aggressor this session.</summary>
    public long ClassifiedPrints => this.classified;

    /// <summary>Feeds a print to every lane, and to the delta and range trackers.</summary>
    public void OnTick(in TickEvent tick)
    {
        this.structure.OnTick(tick);

        if (double.IsFinite(tick.Price))
        {
            if (double.IsNaN(this.sessionHigh) || tick.Price > this.sessionHigh)
                this.sessionHigh = tick.Price;

            if (double.IsNaN(this.sessionLow) || tick.Price < this.sessionLow)
                this.sessionLow = tick.Price;
        }

        // SignedSize is zero for an unclassified print, so an unclassified tape
        // accumulates a delta of zero over zero counted prints -- which the flow
        // vote reports as UNAVAILABLE rather than as balanced. Counting only the
        // classified ones is what makes that distinction possible.
        if (tick.Aggressor != Aggressor.Unknown && double.IsFinite(tick.SignedSize))
        {
            this.cumulativeDelta += tick.SignedSize;
            this.classified++;
        }
    }

    /// <summary>
    /// Drops the per-session readings: delta, the counted prints, and the range.
    /// </summary>
    /// <remarks>
    /// THE STRUCTURE LANES ARE DELIBERATELY NOT RESET. A 60-minute structure trend
    /// that forgot itself every session would never hold enough closed bars to form
    /// a pivot and would read Undecided forever. Delta and the session range are
    /// per-session by definition; structure is not.
    /// </remarks>
    public void OnSessionOpen()
    {
        this.cumulativeDelta = 0;
        this.classified = 0;
        this.sessionHigh = double.NaN;
        this.sessionLow = double.NaN;
    }

    /// <summary>Drops everything, including structure. For a symbol change.</summary>
    public void Reset()
    {
        this.OnSessionOpen();
        this.structure.Reset();
    }

    /// <summary>
    /// The current reading, ready to paint.
    /// </summary>
    /// <param name="price">Last traded price.</param>
    /// <param name="vwap">Session VWAP, or NaN when unanchored.</param>
    /// <param name="tickSize">The instrument's price increment.</param>
    /// <param name="averageDailyRange">ADR in price, or NaN when unknown.</param>
    public DirectionPanelContent Panel(
        double price, double vwap, double tickSize, double averageDailyRange)
    {
        IReadOnlyList<DirectionVote> lanes = this.structure.Votes();

        DirectionVote location =
            DirectionInputs.Location(price, vwap, tickSize, this.settings.VwapFlatTicks);

        DirectionVote flow = DirectionInputs.Flow(
            this.cumulativeDelta, this.classified, this.settings.DeltaFlatContracts);

        string regime = DirectionInputs.Regime(this.SessionRange, averageDailyRange);

        return DirectionPanel.Build(lanes, location, flow, regime);
    }

    /// <summary>
    /// The verdict alone, for the journal, without building panel text.
    /// </summary>
    public DirectionRead Read(double price, double vwap, double tickSize)
    {
        var votes = new List<DirectionVote>(this.structure.Count + 2);
        votes.AddRange(this.structure.Votes());
        votes.Add(DirectionInputs.Location(price, vwap, tickSize, this.settings.VwapFlatTicks));
        votes.Add(DirectionInputs.Flow(
            this.cumulativeDelta, this.classified, this.settings.DeltaFlatContracts));

        return DirectionRead.From(votes);
    }
}
