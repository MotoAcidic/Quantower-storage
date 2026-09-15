using System;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Features;
using OrbIx.Core.Playbooks;

namespace OrbIx.Core.Sessions;

/// <summary>
/// The order every module is driven in, held once.
///
/// THE CHART AND THE STUDY MUST BE THE SAME SYSTEM, and that is a stronger requirement than it
/// looks. <see cref="SetupEvaluator"/> already makes them share the DECISION; this makes them
/// share the SEQUENCE that produces the state the decision reads. Two copies of an ordering is
/// two chances for the chart to evaluate against a footprint the study had not yet updated, or
/// a retest armed one bar later — and both would look correct in isolation. Every figure in
/// docs/REPLAY-RESULTS.md is a statement about this sequence, so a second copy of it would
/// quietly make those figures describe a system nobody runs.
///
/// It owns the modules that live for a whole session and takes the per-window ones as
/// arguments, because those are rebuilt at every boundary and their lifetime belongs to the
/// caller — the offline replay and the chart create and discard them at different moments for
/// reasons that have nothing to do with ordering.
///
/// IT DRIVES AND IT DOES NOT DECIDE. No veto, no plan, no session phase and no range close
/// happens here. Those depend on a clock the two hosts do not share: the replay advances on
/// prints alone, while the chart also has a wall clock and must close a range in a session
/// that has gone quiet.
/// </summary>
public sealed class EngineFold
{
    private readonly LevelGraph levels;
    private readonly FootprintEngine footprint;
    private readonly MicroQuality quality;
    private readonly RetestEngine retest;
    private readonly AbsorptionEngine absorption;
    private readonly BarAggregator bars;

    public EngineFold(
        LevelGraph levels,
        FootprintEngine footprint,
        MicroQuality quality,
        RetestEngine retest,
        AbsorptionEngine absorption,
        BarAggregator bars)
    {
        this.levels = levels ?? throw new ArgumentNullException(nameof(levels));
        this.footprint = footprint ?? throw new ArgumentNullException(nameof(footprint));
        this.quality = quality ?? throw new ArgumentNullException(nameof(quality));
        this.retest = retest ?? throw new ArgumentNullException(nameof(retest));

        // REQUIRED, NOT OPTIONAL. A module that may be silently absent produces a result for
        // a configuration nobody ran — the same reason SetupEvaluator refuses a playbook that
        // is enabled with no implementation. Absorption reports Unmeasured when its data is
        // dark; it does not go missing.
        this.absorption = absorption ?? throw new ArgumentNullException(nameof(absorption));
        this.bars = bars ?? throw new ArgumentNullException(nameof(bars));
    }

    /// <summary>The timeframe bars are aggregated on — the one entries are decided on.</summary>
    public TimeFrame TimeFrame => this.bars.TimeFrame;

    /// <summary>
    /// Drives every module for one print.
    /// </summary>
    /// <param name="tick">The print.</param>
    /// <param name="builder">The active range builder, or null outside a session.</param>
    /// <param name="breaker">The active break detector, or null when one could not be built.</param>
    /// <param name="closedBar">The bar this print closed, when it closed one.</param>
    /// <returns>True when a bar closed, and the caller should call <see cref="OnBar"/>.</returns>
    public bool OnTick(
        in TickEvent tick, OrBuilder? builder, BreakDetector? breaker, out BarEvent closedBar)
    {
        this.levels.OnTick(tick);
        this.footprint.OnTick(tick);
        this.quality.OnTick(tick);
        this.retest.OnTick(tick);
        this.absorption.OnTick(tick);
        builder?.OnTick(tick);
        breaker?.OnTick(tick);

        return this.bars.Add(tick, out closedBar);
    }

    /// <summary>
    /// Closes the forming bar once the clock says its period ended, plus a grace.
    ///
    /// SEPARATE FROM <see cref="OnTick"/> BECAUSE IT DRIVES NO MODULE. A print advances every
    /// module in a fixed order; the clock advances nothing — it only decides that a bar prints
    /// already filled is finished. The caller then hands it to <see cref="OnBar"/> exactly as it
    /// would a bar a print closed, so nothing downstream can tell which happened.
    ///
    /// A HOST THAT NEVER CALLS THIS IS UNAFFECTED BY IT EXISTING, which is what keeps the
    /// offline replay comparable with the trials measured through it. See
    /// <see cref="BarAggregator.CloseIfElapsed"/>.
    /// </summary>
    /// <param name="nowUtc">The instant to judge against.</param>
    /// <param name="grace">How long past the period's end to wait for prints still in flight.</param>
    /// <param name="closedBar">The bar that closed, when one did.</param>
    /// <returns>True when a bar closed, and the caller should call <see cref="OnBar"/>.</returns>
    public bool OnClock(DateTime nowUtc, TimeSpan grace, out BarEvent closedBar)
        => this.bars.CloseIfElapsed(nowUtc, grace, out closedBar);

    /// <summary>
    /// Drives the book-reading modules for one depth event.
    ///
    /// THE BOOK ENTERS THE SHARED SEQUENCE HERE. The other consumers of depth — MicroQuality
    /// and OrBuilder — are still fed by each host directly, because they were wired that way
    /// before this type existed and moving them is a separate change with its own blast
    /// radius. Absorption is driven here from the start so the chart and the replay cannot
    /// disagree about the order its book was built in, which is the whole reason this class
    /// holds the sequence.
    /// </summary>
    public void OnBook(in BookDelta delta) => this.absorption.OnBook(delta);

    /// <summary>Absorption at both touches as of an instant.</summary>
    public AbsorptionSnapshot ReadAbsorption(DateTime nowUtc) => this.absorption.Read(nowUtc);

    /// <summary>
    /// Drives every module for a closed bar, and confirms a break if one completed.
    ///
    /// A BREAK CONFIRMS HERE AND NOWHERE ELSE. It is the reason this method exists rather than
    /// each host calling four <c>OnBar</c>s itself: confirming on a different event than the
    /// study did would change which bar arms the retest, and therefore which retests exist.
    /// </summary>
    /// <param name="bar">The bar that closed.</param>
    /// <param name="breaker">The active break detector, or null.</param>
    /// <param name="rangeWidthPrice">
    /// The width of the range that was broken. Retest depth is a fraction of it, so a break
    /// cannot be armed without it — which cannot happen in practice, because a detector has no
    /// levels to break until the range closed and handed it some.
    /// </param>
    /// <returns>The break, when one confirmed on this bar.</returns>
    public BreakState? OnBar(in BarEvent bar, BreakDetector? breaker, double rangeWidthPrice)
    {
        var timeFrame = this.bars.TimeFrame;

        this.levels.OnBar(bar, timeFrame);
        this.footprint.OnBar(bar, timeFrame);
        this.absorption.OnBar(bar, timeFrame);
        this.quality.OnBar(bar, timeFrame);
        this.retest.OnBar(bar, timeFrame);

        if (breaker?.OnBar(bar, timeFrame.Period) is not { } broken)
            return null;

        this.retest.ArmBreak(
            broken.Level, broken.Direction, rangeWidthPrice, bar.Volume, bar.CloseTimeUtc);

        return broken;
    }
}
