using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using OrbIx.Core.Config;

namespace OrbIx.Core.Flow;

/// <summary>
/// The twelve chart switches for the absorbed Aramid Flow tools: eleven tools and the one that
/// silences all of them.
///
/// TWELVE IS THE WHOLE BUDGET, and it is the requirement rather than a preference — "without
/// clutter", asked of a merge that would otherwise have produced a 262-row settings dialog. Every
/// other setting those tools had lives in the configuration document. A thirteenth switch is a
/// conversation, not an addition.
///
/// TWO OF ARAMID FLOW'S TOOLS HAVE NO SWITCH HERE AND ARE NOT LOST. Its volume profile and its
/// VWAP are concepts ORB-IX already draws with its own inputs, and the rule for this whole merge is
/// that where both implement a concept, ORB-IX's wins. They reappear as anchors on the profile and
/// VWAP inputs that already exist, which is why eleven tools fit inside twelve switches.
/// </summary>
/// <param name="Enabled">
/// The master switch. Off means none of the eleven draw, whatever their own switch says — one
/// control to clear the chart without losing which tools were configured on.
/// </param>
/// <param name="ClusterStatistics">Numeric rows per bar.</param>
/// <param name="ClusterSearch">Markers on clusters matching the configured filters.</param>
/// <param name="StackedImbalance">Lines left by runs of stacked diagonal imbalance.</param>
/// <param name="Absorption">Lines left where such a run was absorbed.</param>
/// <param name="UnfinishedAuction">Bar extremes where one side traded and the other did not.</param>
/// <param name="BigTrades">Large prints, marked where they happened.</param>
/// <param name="LiveCounter">The forming bar's buy and sell contracts, in the corner.</param>
/// <param name="DomLevels">The largest resting levels in the book.</param>
/// <param name="TrendLines">Two-tap trend lines through pivot highs and lows.</param>
/// <param name="FibFan">A Fibonacci fan over the look-back's extremes.</param>
/// <param name="Gex">Gamma-exposure walls, read from a file or set by hand.</param>
/// <param name="VolumeAbsorptionTier1">
/// Heavy volume at a price the bar could not leave, at the MODERATE threshold.
/// </param>
/// <param name="VolumeAbsorptionTier2">The same reading at the HEAVY threshold.</param>
public readonly record struct FlowToggles(
    bool Enabled,
    bool ClusterStatistics,
    bool ClusterSearch,
    bool StackedImbalance,
    bool Absorption,
    bool UnfinishedAuction,
    bool BigTrades,
    bool LiveCounter,
    bool DomLevels,
    bool TrendLines,
    bool FibFan,
    bool Gex,
    bool VolumeAbsorptionTier1,
    bool VolumeAbsorptionTier2)
{
    /// <summary>Tools the switches cover. Eleven, plus the master that is not a tool.</summary>
    public const int ToolCount = 13;

    /// <summary>Every tool switch with the name it is reported under, in chart order.</summary>
    public IReadOnlyList<(string Name, bool On)> Tools => new[]
    {
        ("cluster statistics", this.ClusterStatistics),
        ("cluster search", this.ClusterSearch),
        ("stacked imbalance", this.StackedImbalance),
        ("absorption stacks", this.Absorption),
        ("unfinished auction", this.UnfinishedAuction),
        ("big trades", this.BigTrades),
        ("live counter", this.LiveCounter),
        ("DOM levels", this.DomLevels),
        ("trend lines", this.TrendLines),
        ("fib fan", this.FibFan),
        ("GEX walls", this.Gex),
        ("absorption t1", this.VolumeAbsorptionTier1),
        ("absorption t2", this.VolumeAbsorptionTier2),
    };

    /// <summary>Every tool off, master on. What a chart shows before anything is switched on.</summary>
    public static FlowToggles Silent { get; } = new(
        Enabled: true,
        ClusterStatistics: false, ClusterSearch: false, StackedImbalance: false, Absorption: false,
        UnfinishedAuction: false, BigTrades: false, LiveCounter: false, DomLevels: false,
        TrendLines: false, FibFan: false, Gex: false,
        VolumeAbsorptionTier1: false, VolumeAbsorptionTier2: false);
}

/// <summary>
/// What the absorbed tools are actually doing on this chart: which are drawing, and what the one
/// period-dependent setting resolved to.
///
/// THE SWITCHES AND THE DOCUMENT MEET HERE AND NOWHERE ELSE. An overlay asks this type whether it
/// should draw, so the master switch cannot be honoured by ten overlays and forgotten by the
/// eleventh, and the volume floor cannot be resolved twice from two different bar periods.
///
/// IT REPORTS RATHER THAN DRAWS. Everything here is answerable without a platform, which is what
/// lets the suite assert it on Linux — the same reason the session palette and the status block
/// live in Core.
/// </summary>
public sealed class FlowDisplay
{
    private readonly FlowToggles toggles;

    /// <param name="toggles">The chart switches.</param>
    /// <param name="config">The document's settings for these tools.</param>
    /// <param name="barPeriod">
    /// The chart's bar period, and null unless its bars are TIME bars.
    ///
    /// NULL IS A REAL CASE AND NOT A MISSING VALUE. The stacked-imbalance floor was measured on time
    /// bars, per period. A tick or range-bar chart reports no period; a Renko, Kagi, Line Break or
    /// Points-and-Figures chart reports the SOURCE period its shapes are built from, which is a
    /// number that looks usable and is not. Both arrive here as null — see
    /// <see cref="TimeBarAggregation"/> — and the tool reports that instead of drawing marks
    /// calibrated for some other chart.
    /// </param>
    /// <param name="calibration">
    /// What <see cref="OrbIx.Core.Features.InstrumentCalibration"/> measured on this chart's own
    /// bars, or null when nothing has been measured yet. Only consulted under
    /// <see cref="ImbalanceFloorSource.Calibrated"/>.
    ///
    /// IT IS A PARAMETER AND NOT A LOOKUP BECAUSE THE FLOOR MOVES. The number is measured from
    /// tape that keeps arriving, so it is not a property of the document and cannot be read from
    /// one. This type is built fresh on every fold for exactly that reason — see the note on
    /// OrbIxIndicator.FlowState — and the caller hands it whatever the builder holds at that
    /// moment.
    ///
    /// OMITTING IT WAS A DEFECT WITH TWO HALVES. The visible half: the status line reported the
    /// swept stand-in forever while the chart drew marks at the calibrated floor, so the number
    /// beside the marks was not the number that produced them. The half that stopped the tool
    /// working at all: a chart with no bar period has no swept number to stand in with, so an
    /// uncalibrated display refuses — and since the calibration never arrived, under a calibrated
    /// source the stacked display could never draw on a tick, range or Renko chart no matter how
    /// much tape had been measured.
    /// </param>
    public FlowDisplay(
        FlowToggles toggles,
        FlowConfig config,
        TimeSpan? barPeriod,
        OrbIx.Core.Features.InstrumentFloor? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (barPeriod is { } period && period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(barPeriod), period, "A bar period must be positive when there is one.");
        }

        this.toggles = toggles;
        this.Config = config;
        this.BarPeriod = barPeriod;
        this.Calibration = calibration;
        this.StackedFloor = config.StackedImbalance.Thresholds.ResolveFloor(barPeriod, calibration);
        this.AbsorptionFloor = config.Absorption.Thresholds.ResolveFloor(barPeriod, calibration);
    }

    public FlowConfig Config { get; }

    /// <summary>The chart's bar period, or null on a chart that has none.</summary>
    public TimeSpan? BarPeriod { get; }

    /// <summary>
    /// What was measured on this chart's own tape, or null when nothing has been. Carried so a
    /// caller can report the measurement itself rather than only the floor it produced.
    /// </summary>
    public OrbIx.Core.Features.InstrumentFloor? Calibration { get; }

    /// <summary>The stacked-imbalance volume floor in force, and where the number came from.</summary>
    public ResolvedFloor StackedFloor { get; }

    /// <summary>The absorption volume floor in force, and where the number came from.</summary>
    public ResolvedFloor AbsorptionFloor { get; }

    /// <summary>
    /// The same display, resolved against a calibration measured since it was built.
    ///
    /// WHY THE BUILDER NEEDS THIS. A display is built once per fold, but the calibration is
    /// measured DURING a fold — Seed() takes the largest single jump the sample ever makes and
    /// recalibrates on the spot, then scans. Without a refresh the seeded bars are scanned behind
    /// a gate that predates the measurement, and on a chart with no bar period that gate has no
    /// swept number to fall back on and refuses: the entire seeded history is silently never
    /// scanned, and no later fold goes back for it. Measured, not reasoned — see
    /// FlowFrameBuilderTests.A_seed_on_a_chart_with_no_bar_period_is_scanned_once_the_tape_is_measured.
    ///
    /// Returns THIS when nothing changed, so refreshing per scan costs an equality check.
    /// </summary>
    public FlowDisplay WithCalibration(OrbIx.Core.Features.InstrumentFloor? calibration)
        => Nullable.Equals(this.Calibration, calibration)
            ? this
            : new FlowDisplay(this.toggles, this.Config, this.BarPeriod, calibration);

    /// <summary>False when the master switch is off, whatever the individual switches say.</summary>
    public bool AnyEnabled => this.toggles.Enabled;

    public bool ClusterStatistics => this.On(this.toggles.ClusterStatistics);
    public bool ClusterSearch => this.On(this.toggles.ClusterSearch);
    /// <summary>
    /// Drawing only if its floor resolved. A tool whose one calibrated threshold has no value on
    /// this chart is a tool that cannot draw, and it says so on the status block rather than
    /// drawing nothing and letting that read as a quiet market.
    /// </summary>
    public bool StackedImbalance => this.On(this.toggles.StackedImbalance) && this.StackedFloor.Resolved;

    /// <inheritdoc cref="StackedImbalance"/>
    public bool Absorption => this.On(this.toggles.Absorption) && this.AbsorptionFloor.Resolved;
    public bool UnfinishedAuction => this.On(this.toggles.UnfinishedAuction);
    public bool BigTrades => this.On(this.toggles.BigTrades);
    public bool LiveCounter => this.On(this.toggles.LiveCounter);
    public bool DomLevels => this.On(this.toggles.DomLevels);
    public bool TrendLines => this.On(this.toggles.TrendLines);
    public bool FibFan => this.On(this.toggles.FibFan);
    public bool Gex => this.On(this.toggles.Gex);

    /// <summary>
    /// Heavy volume at a price the bar could not leave, moderate tier.
    ///
    /// NO FLOOR GATE. Unlike stacked imbalance, this reading carries its own thresholds and they
    /// are ratios rather than contract counts, so nothing about it depends on a floor swept per
    /// bar period -- it draws on a tick or Renko chart exactly as it does on a time chart.
    /// </summary>
    public bool VolumeAbsorptionTier1
        => this.On(this.toggles.VolumeAbsorptionTier1) && this.Config.VolumeAbsorption.DrawTier1;

    /// <summary>The same reading at the heavy threshold.</summary>
    public bool VolumeAbsorptionTier2
        => this.On(this.toggles.VolumeAbsorptionTier2) && this.Config.VolumeAbsorption.DrawTier2;

    /// <summary>
    /// A tool draws only when its own switch and the master switch agree.
    ///
    /// Every per-tool property routes through here so the master cannot be honoured by ten
    /// overlays and forgotten by the eleventh.
    /// </summary>
    private bool On(bool tool) => this.toggles.Enabled && tool;

    /// <summary>How many of the eleven are drawing.</summary>
    public int OnCount => this.OnNames.Count;

    /// <summary>
    /// The tools drawing, by name, in chart order.
    ///
    /// DRAWING, NOT SWITCHED ON. A tool switched on whose floor did not resolve is absent from this
    /// list and present in <see cref="Problems"/>, so the two lines never contradict each other.
    /// </summary>
    public IReadOnlyList<string> OnNames
    {
        get
        {
            var names = new List<string>();

            if (!this.toggles.Enabled)
                return names;

            foreach (var (name, on) in this.Drawing)
            {
                if (on)
                    names.Add(name);
            }

            return names;
        }
    }

    /// <summary>
    /// What each tool is actually doing, which is its switch and everything else that has to hold.
    /// </summary>
    private IReadOnlyList<(string Name, bool On)> Drawing => new[]
    {
        ("cluster statistics", this.ClusterStatistics),
        ("cluster search", this.ClusterSearch),
        ("stacked imbalance", this.StackedImbalance),
        ("absorption stacks", this.Absorption),
        ("unfinished auction", this.UnfinishedAuction),
        ("big trades", this.BigTrades),
        ("live counter", this.LiveCounter),
        ("DOM levels", this.DomLevels),
        ("trend lines", this.TrendLines),
        ("fib fan", this.FibFan),
        ("GEX walls", this.Gex),
        ("absorption t1", this.VolumeAbsorptionTier1),
        ("absorption t2", this.VolumeAbsorptionTier2),
    };

    /// <summary>
    /// Tools that were switched on and cannot draw, each with the reason.
    ///
    /// EMPTY ON A HEALTHY CHART, which is what makes a line here worth drawing. A switch that is
    /// simply off is not a problem and never appears.
    /// </summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var problems = new List<string>();

            if (!this.toggles.Enabled)
                return problems;

            if (this.toggles.StackedImbalance && !this.StackedFloor.Resolved)
                problems.Add("flow stacked imbalance: " + this.StackedFloor.Explanation);

            if (this.toggles.Absorption && !this.AbsorptionFloor.Resolved)
                problems.Add("flow absorption: " + this.AbsorptionFloor.Explanation);

            return problems;
        }
    }

    /// <summary>
    /// One line for the log naming what is on and what the period-dependent floor resolved to.
    ///
    /// THE FLOOR IS NAMED WHENEVER IT IS IN FORCE, and that is the point of the line rather than a
    /// detail of it: a reader seeing more or fewer marks than they expected can find out why
    /// without reading code, including whether the number was measured for THIS bar period or
    /// borrowed from a neighbouring one.
    /// </summary>
    public string Describe()
    {
        if (!this.toggles.Enabled)
            return string.Create(
                CultureInfo.InvariantCulture,
                $"flow: off at the master switch ({FlowToggles.ToolCount} tools configured, none drawing)");

        var names = this.OnNames;

        var line = new StringBuilder(string.Create(
            CultureInfo.InvariantCulture,
            $"flow: {names.Count} of {FlowToggles.ToolCount} on"));

        if (names.Count > 0)
            line.Append(" — ").Append(string.Join(", ", names));

        if (this.StackedImbalance)
            line.Append("; stacked imbalance ").Append(this.StackedFloor.Explanation);

        if (this.Absorption)
            line.Append("; absorption ").Append(this.AbsorptionFloor.Explanation);

        return line.ToString();
    }
}
