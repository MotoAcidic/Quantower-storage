using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Config;
using OrbIx.Core.Features;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Playbooks;

/// <summary>
/// §7's stop and target construction, shared by every playbook.
///
/// Shared rather than reimplemented per playbook because the geometry is the part most
/// likely to drift: two playbooks that place stops "the same way" but in two pieces of code
/// stop being the same the first time one is adjusted. What differs between playbooks is
/// which price the geometry is measured from and what counts as the structural invalidation
/// — both of which arrive as parameters.
/// </summary>
public static class TradeGeometry
{
    /// <summary>
    /// Builds the stop.
    ///
    /// The stop is the WIDEST of the candidates plus a buffer, then capped. Widest because
    /// each candidate answers a different question — where structure breaks, how far this
    /// market normally travels, and how far the range says is noise — and being right about
    /// one while being stopped out by another is no use. Capped because a stop wider than
    /// the product's limit means the trade is refused, never that the stop is tightened:
    /// a stop shrunk to afford a position is a stop in the wrong place.
    /// </summary>
    /// <param name="direction">Which side the trade is.</param>
    /// <param name="entryPrice">Price the stop is measured from.</param>
    /// <param name="structuralPrice">Where the setup becomes structurally wrong.</param>
    /// <param name="range">The session's opening range.</param>
    /// <param name="atrTicks">Average true range on the execution timeframe, in ticks.</param>
    /// <param name="medianSpreadTicks">Prevailing spread in ticks; NaN when unmeasured.</param>
    /// <param name="instrument">Contract specifications, read from the platform.</param>
    /// <param name="stops">Configured geometry.</param>
    /// <param name="maxRiskTicks">The product's hard cap.</param>
    /// <param name="levels">Level graph, so the stop can be nudged clear of a cluster.</param>
    /// <param name="nowUtc">Instant, for level age decay.</param>
    public static StopPlan BuildStop(
        TradeDirection direction,
        double entryPrice,
        double structuralPrice,
        OrSnapshot range,
        double atrTicks,
        double medianSpreadTicks,
        InstrumentSpec instrument,
        StopsConfig stops,
        int maxRiskTicks,
        LevelGraph levels,
        DateTime nowUtc)
    {
        if (range is null) throw new ArgumentNullException(nameof(range));
        if (stops is null) throw new ArgumentNullException(nameof(stops));
        if (levels is null) throw new ArgumentNullException(nameof(levels));

        var sign = direction == TradeDirection.Long ? -1d : 1d;

        // Structural: how far the invalidating swing sits from entry, floored at a fraction
        // of the range so a structure that happens to be one tick away does not produce a
        // one-tick stop.
        var structuralDistance = Math.Abs(entryPrice - structuralPrice);
        var structuralFloor = range.Width * stops.StructuralFloorOrFraction;
        var structural = Math.Max(structuralDistance, structuralFloor);

        // Volatility: what this market normally travels on the timeframe being entered on.
        var volatility = atrTicks > 0
            ? atrTicks * stops.AtrMultiple * instrument.TickSize
            : 0d;

        var buffer = BufferTicks(medianSpreadTicks, range, instrument, stops);
        var bufferDistance = buffer * instrument.TickSize;

        var distance = Math.Max(structural, volatility) + bufferDistance;
        var rawPrice = entryPrice + (sign * distance);
        var price = NudgeClear(rawPrice, direction, instrument, stops, levels, nowUtc);

        var ticks = (int)Math.Ceiling(Math.Abs(entryPrice - price) / instrument.TickSize);
        var withinCap = ticks <= maxRiskTicks;

        var reason = string.Format(
            CultureInfo.InvariantCulture,
            "widest of structural {0:N1}t and volatility {1:N1}t, plus {2}t buffer = {3}t{4}",
            structural / instrument.TickSize,
            volatility / instrument.TickSize,
            buffer,
            ticks,
            withinCap
                ? string.Empty
                : $" — REFUSED, beyond the {maxRiskTicks}t cap for this product; a stop is never tightened to fit a size");

        return new StopPlan(price, ticks, structural, volatility, buffer, withinCap, reason);
    }

    /// <summary>
    /// The buffer, in ticks: the largest of a floor, a multiple of the prevailing spread,
    /// and a fraction of the opening range.
    ///
    /// It exists because the obvious stop price is the most heavily hunted price on the
    /// chart. Where the spread is unmeasured the spread clause is skipped rather than
    /// assumed — the other two still apply, so the buffer is never zero.
    /// </summary>
    private static int BufferTicks(
        double medianSpreadTicks, OrSnapshot range, InstrumentSpec instrument, StopsConfig stops)
    {
        var candidates = new List<double> { stops.BufferMinTicks };

        if (!double.IsNaN(medianSpreadTicks) && medianSpreadTicks > 0)
            candidates.Add(Math.Ceiling(stops.BufferSpreadMultiple * medianSpreadTicks));

        if (range.Width > 0 && instrument.TickSize > 0)
            candidates.Add(range.Width * stops.BufferOrRangeFraction / instrument.TickSize);

        return (int)Math.Ceiling(candidates.Max());
    }

    /// <summary>
    /// Moves a stop past a round number or level cluster rather than leaving it on one.
    ///
    /// Same reasoning as the buffer: a stop resting exactly on an obvious price is a stop
    /// sitting in the pool everyone else is aiming at. The nudge is always further from
    /// entry, never nearer — moving it closer would tighten risk the trade never agreed to.
    /// </summary>
    private static double NudgeClear(
        double stopPrice,
        TradeDirection direction,
        InstrumentSpec instrument,
        StopsConfig stops,
        LevelGraph levels,
        DateTime nowUtc)
    {
        var nudge = stops.NudgePastLevelTicks * instrument.TickSize;

        if (nudge <= 0)
            return instrument.RoundToTick(stopPrice);

        var cluster = levels.Nearest(stopPrice, nowUtc);

        if (cluster is null)
            return instrument.RoundToTick(stopPrice);

        var withinReach = Math.Abs(cluster.Price - stopPrice) <= nudge;

        if (!withinReach)
            return instrument.RoundToTick(stopPrice);

        // Further from entry: below the cluster for a long, above it for a short.
        var moved = direction == TradeDirection.Long
            ? cluster.Low - nudge
            : cluster.High + nudge;

        return instrument.RoundToTick(moved);
    }

    /// <summary>
    /// Builds the target ladder.
    ///
    /// §7's order is levels first, arithmetic second: a target is placed where something
    /// actually trades where possible, and at a computed distance only where nothing does.
    /// The snap rule moves a target that lands just behind a strong level to just in front
    /// of it — nobody has ever been paid for the last few ticks into a wall.
    /// </summary>
    /// <param name="direction">Which side the trade is.</param>
    /// <param name="entryPrice">Price the ladder is measured from.</param>
    /// <param name="stop">The stop, which defines one unit of risk.</param>
    /// <param name="range">The session's opening range, which the projections come from.</param>
    /// <param name="instrument">Contract specifications, read from the platform.</param>
    /// <param name="targets">Configured target definitions and snap distances.</param>
    /// <param name="levels">Level graph, so targets can be placed where price actually reacts.</param>
    /// <param name="nowUtc">Instant, for level age decay.</param>
    /// <param name="armFourthTarget">
    /// Whether the runner is armed. §6 arms it at the top grade only; below that the ladder
    /// stops at three, because a runner funded by a merely good signal is a position held on
    /// hope.
    /// </param>
    public static IReadOnlyList<TargetPlan> BuildTargets(
        TradeDirection direction,
        double entryPrice,
        StopPlan stop,
        OrSnapshot range,
        InstrumentSpec instrument,
        TargetsConfig targets,
        LevelGraph levels,
        DateTime nowUtc,
        bool armFourthTarget)
    {
        if (stop is null) throw new ArgumentNullException(nameof(stop));
        if (range is null) throw new ArgumentNullException(nameof(range));
        if (targets is null) throw new ArgumentNullException(nameof(targets));
        if (levels is null) throw new ArgumentNullException(nameof(levels));

        var sign = direction == TradeDirection.Long ? 1d : -1d;
        var riskDistance = Math.Abs(entryPrice - stop.Price);

        if (riskDistance <= 0)
            return Array.Empty<TargetPlan>();

        var plans = new List<TargetPlan>(4);

        // TP1 — one unit of risk. Purpose is de-risking, not profit. A level cluster inside
        // the configured band replaces it, because coming off at a price something trades at
        // beats coming off at an arithmetic one.
        var oneR = entryPrice + (sign * riskDistance);
        var pocket = levels.StrongestBetween(
            entryPrice + (sign * riskDistance * targets.Tp1RMin),
            entryPrice + (sign * riskDistance * targets.Tp1RMax),
            nowUtc);

        Add(plans, direction, entryPrice, riskDistance, instrument, targets, levels, nowUtc,
            pocket?.Price ?? oneR,
            pocket is null ? "1.0R" : $"1.0R band, taken at {pocket.Members[0].Label}");

        // TP2 — half a range projected from the broken edge. The measured-move core.
        var edge = direction == TradeDirection.Long ? range.Orh : range.Orl;
        Add(plans, direction, entryPrice, riskDistance, instrument, targets, levels, nowUtc,
            edge + (sign * range.Width * 0.5d), "0.5x OR projection");

        // TP3 — a full range projected, snapped to a level cluster within reach.
        var thirdRaw = edge + (sign * range.Width);
        var snapWindow = range.Width * targets.Tp3SnapOrFraction;
        var cluster = levels.StrongestBetween(thirdRaw - snapWindow, thirdRaw + snapWindow, nowUtc);

        Add(plans, direction, entryPrice, riskDistance, instrument, targets, levels, nowUtc,
            cluster?.Price ?? thirdRaw,
            cluster is null ? "1.0x OR projection" : $"1.0x OR, snapped to {cluster.Members[0].Label}");

        if (armFourthTarget)
        {
            Add(plans, direction, entryPrice, riskDistance, instrument, targets, levels, nowUtc,
                edge + (sign * range.Width * 1.618d), "1.618x OR extension, trailed");
        }

        // Targets must strictly increase away from entry: a ladder whose third rung sits
        // nearer than its second is not a ladder, and the bracket builder refuses it.
        return Monotonic(plans);
    }

    private static void Add(
        List<TargetPlan> plans,
        TradeDirection direction,
        double entryPrice,
        double riskDistance,
        InstrumentSpec instrument,
        TargetsConfig targets,
        LevelGraph levels,
        DateTime nowUtc,
        double rawPrice,
        string basis)
    {
        var price = SnapAheadOfWall(rawPrice, direction, instrument, targets, levels, nowUtc, ref basis);
        var ticks = (int)Math.Floor(Math.Abs(price - entryPrice) / instrument.TickSize);

        if (ticks <= 0)
            return;

        plans.Add(new TargetPlan(
            instrument.RoundToTick(price), ticks, ticks * instrument.TickSize / riskDistance, basis));
    }

    /// <summary>
    /// Moves a target that lands just behind a strong level to just in front of it.
    ///
    /// Only ever nearer to entry, never further: pulling a target back is giving up a few
    /// ticks to raise the chance of being filled at all, whereas pushing it beyond a wall
    /// would be asking price to do something it has just been shown not to do.
    /// </summary>
    private static double SnapAheadOfWall(
        double price,
        TradeDirection direction,
        InstrumentSpec instrument,
        TargetsConfig targets,
        LevelGraph levels,
        DateTime nowUtc,
        ref string basis)
    {
        var reach = targets.SnapAheadTicks * instrument.TickSize;

        if (reach <= 0)
            return price;

        var cluster = levels.Nearest(price, nowUtc);

        if (cluster is null)
            return price;

        var sign = direction == TradeDirection.Long ? 1d : -1d;
        var beyond = (cluster.Price - price) * sign;

        // The wall sits just past the target: pull the target in front of it.
        if (beyond <= 0 || beyond > reach)
            return price;

        basis += $", moved ahead of {cluster.Members[0].Label}";

        return direction == TradeDirection.Long
            ? cluster.Low - instrument.TickSize
            : cluster.High + instrument.TickSize;
    }

    private static IReadOnlyList<TargetPlan> Monotonic(IReadOnlyList<TargetPlan> plans)
    {
        var kept = new List<TargetPlan>(plans.Count);

        foreach (var plan in plans)
        {
            if (kept.Count == 0 || plan.Ticks > kept[^1].Ticks)
                kept.Add(plan);
        }

        return kept;
    }
}
