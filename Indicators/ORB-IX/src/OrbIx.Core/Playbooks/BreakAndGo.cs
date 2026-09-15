using System;
using System.Collections.Generic;
using System.Globalization;
using OrbIx.Core.Config;
using OrbIx.Core.Features;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Playbooks;

/// <summary>
/// P1 — Break &amp; Go. Continuation, with the regime.
///
/// The highest-frequency setup and the lowest per-trade edge, which is why its confirmation
/// set is the strictest part of it rather than an afterthought.
///
/// Several of §5's confirmation and veto clauses need depth or order-level data — book
/// thinning ahead of price, opposing depth failing to replenish within 500 ms, iceberg
/// detection, spoof scoring. Those tiers are not built in phase 1. They are not silently
/// dropped and they are not faked: <see cref="UnavailableClauses"/> names every one, the
/// panel and the journal carry the list, and the scorer redistributes their weight so the
/// score stays honest about being a partial view rather than pretending to be a whole one.
/// </summary>
public sealed class BreakAndGo : IPlaybook
{
    private readonly PlaybookConfig config;
    private readonly StopsConfig stops;
    private readonly TargetsConfig targets;
    private readonly int maxRiskTicks;

    public BreakAndGo(
        PlaybookConfig config, StopsConfig stops, TargetsConfig targets, int maxRiskTicks)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.stops = stops ?? throw new ArgumentNullException(nameof(stops));
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));

        if (maxRiskTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRiskTicks), maxRiskTicks, "The product's risk cap must be positive.");
        }

        this.maxRiskTicks = maxRiskTicks;
    }

    public string Id => "P1";

    public string Name => "Break & Go";

    public double SizeMultiplier => this.config.SizeMult;

    /// <summary>
    /// The §5 clauses this playbook cannot evaluate in the phase-1 data environment. Named
    /// rather than dropped, so a reader of the journal knows the confirmation set was
    /// partial and which parts were missing.
    /// </summary>
    public static IReadOnlyList<string> UnavailableClauses { get; } = new[]
    {
        "book thins ahead of price (needs depth)",
        "opposing depth does not replenish within 500 ms (needs depth)",
        "no iceberg detected at the level (needs order-level data)",
        "spoof score below threshold (needs order-level data)",
    };

    public Eligibility IsEligible(SessionContext context, PlaybookInputs inputs)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        if (!this.config.On)
            return Eligibility.No("P1 is disabled in configuration.");

        if (context.Range is not { } range)
            return Eligibility.No("The opening range has not closed.");

        // §5: compressed or normal only. An exhausted range means the day's likely movement
        // is already inside it, so a continuation break is buying the end of the move.
        if (range.Grade == OrGrade.Exhausted)
        {
            return Eligibility.No(
                $"Range is Exhausted (width {range.Orw:N2} of average daily range); "
                + "continuation is not the trade on a day whose range is already spent.");
        }

        if (!inputs.Break.Broken)
            return Eligibility.No("The range has not been broken.");

        // §5: first break of the session. A second break is P2's business or nobody's.
        if (!inputs.Break.IsFirstBreak)
        {
            return Eligibility.No(
                $"Break {inputs.Break.BreakCount} of the session; P1 takes the first only.");
        }

        return Eligibility.Yes(
            $"Range {range.Grade} at {range.Orw:N2} of average daily range, first break "
            + $"{inputs.Break.Direction}.");
    }

    public TriggerResult? CheckTrigger(SessionContext context, PlaybookInputs inputs)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        if (!this.IsEligible(context, inputs).Eligible)
            return null;

        var state = inputs.Break;

        // §5: velocity above its own median. A break that crawls beyond the edge is not the
        // expansion this playbook is built on.
        if (state.Velocity < 1d)
            return null;

        var direction = state.AsTradeDirection;
        var range = context.Range;

        if (range is null)
            return null;

        // §5: the opposite side of the BREAK BAR's structure — the far edge of the bar that
        // confirmed the break, not the far edge of the range. Using the range edge measures
        // the stop across the entire range, which on a hundred-point range put one unit of
        // risk beyond every projection in the ladder and left the plan with two targets
        // instead of four. The floor at a quarter of the range still applies underneath, in
        // TradeGeometry, so a narrow break bar cannot produce a hairline stop.
        var structural = double.IsNaN(state.StructuralInvalidation)
            ? (direction == TradeDirection.Long ? range.Orl : range.Orh)
            : state.StructuralInvalidation;

        return new TriggerResult(
            direction,
            state.BreakPrice,
            state.Level,
            // Being late costs less than being wrong on a continuation break, and the
            // entry has already been confirmed by a close beyond the edge.
            EntryStyle.MarketOnConfirm,
            structural,
            string.Format(
                CultureInfo.InvariantCulture,
                "Closed {0:N1} ticks beyond the {1} edge at {2:N2} on {3:N2}x median velocity.",
                state.ExcursionTicks, state.Direction, state.Level, state.Velocity));
    }

    public IReadOnlyList<string> Vetoes(SessionContext context, PlaybookInputs inputs)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var vetoes = new List<string>();

        // The gatekeeper's breaches are vetoes wherever they occur.
        vetoes.AddRange(inputs.Quality.Reasons);

        if (!context.CanEnter(inputs.NowUtc, out var sessionReason))
            vetoes.Add(sessionReason);

        // §5: price re-entering the range kills a continuation break outright.
        if (inputs.Break.ReEntered)
            vetoes.Add("Price re-entered the range after the break; the continuation is dead.");

        // §5: delta disagreeing with the direction.
        if (inputs.Flow is { } flow)
        {
            var direction = inputs.Break.AsTradeDirection;
            var deltaAgrees = direction == TradeDirection.Long
                ? flow.CumulativeDelta > 0
                : flow.CumulativeDelta < 0;

            if (!deltaAgrees && flow.Classified > 0)
            {
                vetoes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Cumulative delta {0:N0} disagrees with a {1} break.", flow.CumulativeDelta, direction));
            }

            if (flow.DivergenceScore < 0)
            {
                vetoes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Price and flow are diverging ({0:N2}).", flow.DivergenceScore));
            }
        }

        return vetoes;
    }

    public StopPlan BuildStop(TriggerResult trigger, SessionContext context, PlaybookInputs inputs)
    {
        if (trigger is null) throw new ArgumentNullException(nameof(trigger));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var range = context.Range
            ?? throw new InvalidOperationException("A stop cannot be placed before the range has closed.");

        return TradeGeometry.BuildStop(
            trigger.Direction, trigger.ReferencePrice, trigger.StructuralPrice, range,
            inputs.AtrTicks, inputs.MedianSpreadTicks, context.Instrument, this.stops,
            this.maxRiskTicks, inputs.Levels, inputs.NowUtc);
    }

    public IReadOnlyList<TargetPlan> BuildTargets(
        TriggerResult trigger, StopPlan stop, SessionContext context, PlaybookInputs inputs)
    {
        if (trigger is null) throw new ArgumentNullException(nameof(trigger));
        if (stop is null) throw new ArgumentNullException(nameof(stop));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var range = context.Range
            ?? throw new InvalidOperationException("Targets cannot be placed before the range has closed.");

        return TradeGeometry.BuildTargets(
            trigger.Direction, trigger.ReferencePrice, stop, range, context.Instrument,
            this.targets, inputs.Levels, inputs.NowUtc, armFourthTarget: true);
    }
}
