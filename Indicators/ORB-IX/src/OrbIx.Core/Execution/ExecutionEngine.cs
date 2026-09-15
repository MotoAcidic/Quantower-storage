using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Config;
using OrbIx.Core.Playbooks;
using OrbIx.Core.Risk;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Execution;

/// <summary>
/// One order on one instrument, with its own bracket.
///
/// A position spanning two contract tiers is two of these, not one order with a mixed
/// bracket: a bracket belongs to exactly one order on exactly one symbol, and the connector
/// validates its leg quantities against that order's quantity alone.
/// </summary>
/// <param name="Instrument">Which contract.</param>
/// <param name="Direction">Which side.</param>
/// <param name="Style">How the entry is placed.</param>
/// <param name="EntryPrice">Reference price for the entry.</param>
/// <param name="Quantity">Contracts of this tier.</param>
/// <param name="Bracket">Targets and stops, in tick offsets, summing to <paramref name="Quantity"/>.</param>
/// <param name="RiskUsd">Currency at risk on this leg if the stop fills.</param>
public sealed record InstrumentOrder(
    InstrumentSpec Instrument,
    TradeDirection Direction,
    EntryStyle Style,
    double EntryPrice,
    int Quantity,
    BracketPlan Bracket,
    double RiskUsd);

/// <summary>
/// A complete, sendable proposal — or a refusal with every reason it cannot be sent.
/// </summary>
/// <param name="PlaybookId">Which setup produced it.</param>
/// <param name="Direction">Which side.</param>
/// <param name="Grade">The confluence grade behind it.</param>
/// <param name="Orders">One entry per contract tier.</param>
/// <param name="TotalRiskUsd">Currency at risk across every leg.</param>
/// <param name="Sendable">Whether it may be sent at all.</param>
/// <param name="Blockers">Every reason it may not be. Empty when sendable.</param>
/// <param name="Mode">The mode this plan was built under, after the validation gate.</param>
/// <param name="Explanation">Human-readable summary for the panel and the journal.</param>
public sealed record OrderPlan(
    string PlaybookId,
    TradeDirection Direction,
    SignalGrade Grade,
    IReadOnlyList<InstrumentOrder> Orders,
    double TotalRiskUsd,
    bool Sendable,
    IReadOnlyList<string> Blockers,
    EngineMode Mode,
    string Explanation)
{
    public int TotalContracts => this.Orders.Sum(o => o.Quantity);

    /// <summary>
    /// Whether the plan may be sent without a human action. Armed and Signal both require
    /// one; only Auto does not, and only after the validation gate has allowed it.
    /// </summary>
    public bool SendsWithoutConfirmation => this.Sendable && this.Mode == EngineMode.Auto;

    public static OrderPlan Refused(
        string playbookId, TradeDirection direction, SignalGrade grade,
        EngineMode mode, IReadOnlyList<string> blockers)
        => new(playbookId, direction, grade, Array.Empty<InstrumentOrder>(), 0d,
               false, blockers, mode,
               "Refused: " + string.Join("; ", blockers));
}

/// <summary>
/// M17. Turns a scored trade plan and a solved size into orders.
///
/// This is where §8's arithmetic is corrected. The specification solves for a total in
/// micro-equivalents and then applies the four-target ladder to that total — but the
/// connector rejects any bracket whose target quantities and stop quantities do not each
/// sum to its own order's quantity, and a forty-four micro-equivalent answer that becomes
/// four minis and four micros is two orders on two instruments. So the position is
/// decomposed first and each leg is laddered independently.
///
/// Target distances are recomputed per instrument from the target PRICE rather than reused
/// as tick counts. Gold's tiers do not share a tick size: the same four-dollar target is
/// forty ticks on the tenth-tick contracts and sixteen on the quarter-tick one, and reusing
/// one tick count across them would place three of the four targets in the wrong place.
/// </summary>
public sealed class ExecutionEngine
{
    private readonly TargetsConfig targets;
    private readonly TrailMode trailMode;

    public ExecutionEngine(TargetsConfig targets, TrailMode trailMode)
    {
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.trailMode = trailMode;
    }

    /// <summary>
    /// Builds the order plan, or a refusal carrying every blocker.
    /// </summary>
    /// <param name="plan">The scored trade plan.</param>
    /// <param name="size">The solved position.</param>
    /// <param name="mode">The effective mode, after the validation gate has run.</param>
    public OrderPlan Build(TradePlan plan, SizeSolution size, EngineMode mode)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (size is null) throw new ArgumentNullException(nameof(size));

        var blockers = new List<string>();

        if (plan.Score.Vetoed)
            blockers.AddRange(plan.Score.Vetoes);

        if (!plan.Score.Tradeable && !plan.Score.Vetoed)
            blockers.Add($"Grade {plan.Score.Grade} is below the tradeable band.");

        if (!plan.Stop.WithinCap)
            blockers.Add(plan.Stop.Reason);

        if (!size.Traded)
            blockers.Add(size.Explanation);

        if (plan.Targets.Count == 0)
            blockers.Add("No target could be placed, so there is nothing to exit into.");

        if (blockers.Count > 0)
            return OrderPlan.Refused(plan.PlaybookId, plan.Direction, plan.Score.Grade, mode, blockers);

        var orders = new List<InstrumentOrder>(size.Legs.Count);

        foreach (var leg in size.Legs)
        {
            var order = this.BuildOrder(plan, leg, blockers);

            if (order is not null)
                orders.Add(order);
        }

        if (blockers.Count > 0)
            return OrderPlan.Refused(plan.PlaybookId, plan.Direction, plan.Score.Grade, mode, blockers);

        if (orders.Count == 0)
        {
            return OrderPlan.Refused(
                plan.PlaybookId, plan.Direction, plan.Score.Grade, mode,
                new[] { "The solved position produced no sendable order." });
        }

        var totalRisk = orders.Sum(o => o.RiskUsd);

        return new OrderPlan(
            plan.PlaybookId, plan.Direction, plan.Score.Grade, orders, totalRisk,
            Sendable: true, Blockers: Array.Empty<string>(), Mode: mode,
            Explanation: string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} grade {2}: {3} for {4:C} risk across {5} target{6}. {7}",
                plan.PlaybookId,
                plan.Direction,
                plan.Score.Grade,
                string.Join(" + ", orders.Select(o => $"{o.Quantity} {o.Instrument.Tier}")),
                totalRisk,
                orders[0].Bracket.TargetCount,
                orders[0].Bracket.TargetCount == 1 ? string.Empty : "s",
                mode == EngineMode.Auto
                    ? "Sends without confirmation."
                    : $"{mode} mode: staged, awaiting a human action."));
    }

    private InstrumentOrder? BuildOrder(TradePlan plan, SizeLeg leg, List<string> blockers)
    {
        // Every distance is recomputed from the target price for THIS instrument's grid.
        var targetTicks = new List<int>(plan.Targets.Count);

        foreach (var target in plan.Targets)
        {
            var ticks = (int)Math.Floor(
                Math.Abs(target.Price - plan.EntryPrice) / leg.Instrument.TickSize);

            // Strictly increasing, since the ladder builder requires it and two targets
            // rounding to the same tick count on a coarse grid is a real possibility.
            if (ticks > 0 && (targetTicks.Count == 0 || ticks > targetTicks[^1]))
                targetTicks.Add(ticks);
        }

        if (targetTicks.Count == 0)
        {
            blockers.Add(
                $"No target is more than one tick from entry on {leg.Instrument.Tier}, "
                + "whose grid is too coarse for this plan.");
            return null;
        }

        // Four targets need four units to split across. Where the leg has fewer, the ladder
        // uses as many rungs as it can fund — a target allocated nothing is a target that
        // does not exist, and pretending otherwise produces a bracket the connector rejects.
        var usableTargets = Math.Min(targetTicks.Count, leg.Quantity);
        var legTargets = TargetLadder.Build(
            leg.Quantity, targetTicks.Take(usableTargets).ToList(), this.targets);

        // One stop for the whole leg. The connector's trailing flag applies to the bracket
        // as a whole, so a per-target stop ladder is not expressible here even if it were
        // wanted.
        var legStops = new[] { new BracketLeg(leg.StopTicks, leg.Quantity) };

        var bracket = new BracketPlan(
            leg.Instrument, leg.Quantity, legTargets, legStops,
            trailing: this.trailMode == TrailMode.NativeBracketOnly);

        return new InstrumentOrder(
            leg.Instrument, plan.Direction, plan.Style, plan.EntryPrice,
            leg.Quantity, bracket, leg.RiskUsd);
    }
}
