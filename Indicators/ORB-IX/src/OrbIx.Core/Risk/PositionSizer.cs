using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Risk;

/// <summary>
/// One tradeable contract tier, with the risk weight that lets tiers be compared.
///
/// The weight is the instrument's value per point of price movement — tick value divided
/// by tick size — not its tick value. Those differ: the one-ounce gold contract ticks in
/// quarters where the hundred-ounce contract ticks in tenths, so comparing tick values
/// would make it look four times heavier than it is. Value per point is the honest
/// common unit because risk is a price move, not a tick count.
///
/// Both inputs come from the platform at runtime. Nothing here knows what a contract is
/// called or how large it should be, which is how a newly listed tier joins the ladder
/// the day it starts trading rather than the day someone edits a table.
/// </summary>
public sealed record ContractTier(string Name, InstrumentSpec Spec)
{
    /// <summary>Currency value of one full point of price movement, for one contract.</summary>
    public double PointValue => this.Spec.TickSize > 0 ? this.Spec.TickValue / this.Spec.TickSize : 0d;

    public bool IsUsable => this.Spec.IsUsable && this.PointValue > 0;
}

/// <summary>One instrument's share of a position.</summary>
public sealed record SizeLeg(InstrumentSpec Instrument, int Quantity, int StopTicks, double RiskUsd);

/// <summary>
/// What the sizing engine decided, and why. The explanation is carried into the journal
/// and onto the panel, because a size with no stated reason cannot be reviewed later.
/// </summary>
public sealed record SizeSolution(
    bool Traded,
    double MicroEquivalents,
    IReadOnlyList<SizeLeg> Legs,
    double RiskUsd,
    double DeferredRiskUsd,
    string Explanation)
{
    public int TotalContracts => this.Legs.Sum(l => l.Quantity);

    /// <summary>The decision not to trade, with the reason preserved.</summary>
    public static SizeSolution NoTrade(double deferredRiskUsd, string explanation)
        => new(false, 0d, Array.Empty<SizeLeg>(), 0d, deferredRiskUsd, explanation);
}

/// <summary>What the caller knows at the moment a size is needed.</summary>
public sealed record SizingRequest
{
    /// <summary>Tiers available for this product, in any order; the sizer orders them.</summary>
    public required IReadOnlyList<ContractTier> Tiers { get; init; }

    /// <summary>Stop distance as a price move, not a tick count — tiers tick differently.</summary>
    public required double StopDistancePrice { get; init; }

    /// <summary>Currency the trade may risk, after every modulation has been applied.</summary>
    public required double RiskBudgetUsd { get; init; }

    /// <summary>Risk banked by previous refusals, available to fund a minimum position.</summary>
    public double DeferredRiskUsd { get; init; }

    /// <summary>Ceiling on contracts of the coarsest tier.</summary>
    public required int MaxMinis { get; init; }

    public required LadderSplitMode SplitMode { get; init; }
}

/// <summary>
/// Solves position size, then decomposes it into contracts.
///
/// The order matters and is the correction this engine makes to the specification's
/// arithmetic. §8 solves for micro-equivalents and then applies the four-target ladder to
/// that total — but a bracket belongs to one order on one instrument, and the connector
/// rejects any bracket whose leg quantities do not sum to its order's quantity. A
/// forty-four micro-equivalent answer that becomes four minis and four micros is two
/// orders on two instruments, each needing its own ladder. So the decomposition happens
/// first and each leg is laddered independently.
/// </summary>
public sealed class PositionSizer
{
    /// <summary>
    /// Solves a size. Returns a refusal rather than a rounded-up position when the budget
    /// cannot fund even the finest tier: rounding up is how a prop account dies, and the
    /// unspent risk is banked instead so the next qualifying setup can use it.
    /// </summary>
    public SizeSolution Solve(SizingRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (request.StopDistancePrice <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.StopDistancePrice,
                "Stop distance must be a positive price move; a size cannot be solved without one.");
        }

        var usable = request.Tiers
            .Where(t => t.IsUsable)
            .OrderByDescending(t => t.PointValue)
            .ToList();

        if (usable.Count == 0)
        {
            return SizeSolution.NoTrade(
                request.DeferredRiskUsd,
                "No contract tier reported usable specifications from the platform, so no size can be solved.");
        }

        var finest = usable[^1];
        var riskPerMicroEquivalent = request.StopDistancePrice * finest.PointValue;

        if (riskPerMicroEquivalent <= 0)
        {
            return SizeSolution.NoTrade(
                request.DeferredRiskUsd,
                $"The finest tier {finest.Name} reports no value per point, so risk per contract is unknown.");
        }

        var available = request.RiskBudgetUsd + request.DeferredRiskUsd;
        var microEquivalents = (int)Math.Floor(available / riskPerMicroEquivalent);

        if (microEquivalents < 1)
        {
            var banked = available;
            return SizeSolution.NoTrade(
                banked,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Budget {0:C} against {1:C} risk per {2} funds {3:0.00} contracts. Rounding up would "
                    + "exceed the risk limit, so the trade is declined and {4:C} is banked for the next setup.",
                    available, riskPerMicroEquivalent, finest.Name,
                    available / riskPerMicroEquivalent, banked));
        }

        var tiers = this.SelectTiers(usable, request.SplitMode, finest);
        var coarsest = usable[0];
        var legs = Decompose(microEquivalents, tiers, finest, coarsest, request, out var usedMicroEquivalents);
        legs = TrimToBudget(legs, finest, available, ref usedMicroEquivalents);

        if (legs.Count == 0)
        {
            return SizeSolution.NoTrade(
                available,
                "The contract limits left no tier able to take the solved size.");
        }

        var riskUsd = legs.Sum(l => l.RiskUsd);

        return new SizeSolution(
            true,
            usedMicroEquivalents,
            legs,
            riskUsd,
            DeferredRiskUsd: 0d,
            Explanation: string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1:0.##} micro-equivalents at {2:C} each against a {3:C} budget, taken as {4}. Real risk {5:C}.",
                request.SplitMode,
                usedMicroEquivalents,
                riskPerMicroEquivalent,
                available,
                string.Join(" + ", legs.Select(l => $"{l.Quantity} {l.Instrument.Tier}")),
                riskUsd));
    }

    private IReadOnlyList<ContractTier> SelectTiers(
        IReadOnlyList<ContractTier> usable, LadderSplitMode mode, ContractTier finest) => mode switch
        {
            // Every tier is available; the decomposition takes the coarsest that fits.
            LadderSplitMode.DecomposeThenLadderPerLeg => usable,

            // One order, one ladder: the decomposition below will fill the coarsest tier it
            // can and then stop, because no finer tier is offered to it.
            LadderSplitMode.SingleInstrument => usable,

            // Size entirely in the finest tier so the ladder always has units to split.
            LadderSplitMode.MicrosOnly => new[] { finest },

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown ladder split mode."),
        };

    /// <summary>
    /// Turns a micro-equivalent total into whole contracts, coarsest tier first.
    ///
    /// Coarse first because coarse tiers are cheaper per unit of exposure in commission and
    /// because the exit ladder releases them first; a position made entirely of the finest
    /// tier pays more to hold the same risk.
    /// </summary>
    private static IReadOnlyList<SizeLeg> Decompose(
        int microEquivalents,
        IReadOnlyList<ContractTier> tiers,
        ContractTier finest,
        ContractTier coarsest,
        SizingRequest request,
        out int usedMicroEquivalents)
    {
        var legs = new List<SizeLeg>();
        var remaining = microEquivalents;
        usedMicroEquivalents = 0;

        for (var i = 0; i < tiers.Count && remaining > 0; i++)
        {
            var tier = tiers[i];
            var weight = (int)Math.Round(tier.PointValue / finest.PointValue, MidpointRounding.AwayFromZero);

            if (weight <= 0)
                continue;

            var quantity = remaining / weight;

            // The contract limit governs minis — the product's coarsest tier — not
            // whichever tier happens to come first in the list handed to this method.
            // Under MicrosOnly the only tier offered is the finest, and capping it at the
            // mini limit would silently shrink the position by an order of magnitude.
            if (ReferenceEquals(tier, coarsest) && quantity > request.MaxMinis)
                quantity = request.MaxMinis;

            if (quantity <= 0)
                continue;

            var stopTicks = StopTicksFor(request.StopDistancePrice, tier.Spec);

            if (stopTicks <= 0)
                continue;

            legs.Add(new SizeLeg(
                tier.Spec,
                quantity,
                stopTicks,
                quantity * stopTicks * tier.Spec.TickValue));

            remaining -= quantity * weight;
            usedMicroEquivalents += quantity * weight;

            // One order, one ladder: stop after the first tier that took anything.
            if (request.SplitMode == LadderSplitMode.SingleInstrument)
                break;
        }

        return legs;
    }

    /// <summary>
    /// Reduces the position until its real risk fits the budget.
    ///
    /// This step is necessary, not defensive. The micro-equivalent solve prices risk from
    /// the stop distance as a price move, but an order's actual risk is its stop rounded
    /// up to whole ticks — and each tier rounds differently, because gold's tiers do not
    /// share a tick size. A four-dollar stop is 40 ticks on the tenth-tick contracts and
    /// 16 on the quarter-tick one, and 16 quarter-ticks is more than four dollars. The
    /// modelled risk is therefore a slight underestimate, and on a small budget that
    /// underestimate is the difference between inside the limit and outside it.
    ///
    /// Contracts come off the finest funded tier first, since that is the smallest
    /// reduction that moves the total.
    /// </summary>
    private static IReadOnlyList<SizeLeg> TrimToBudget(
        IReadOnlyList<SizeLeg> legs, ContractTier finest, double budget, ref int usedMicroEquivalents)
    {
        if (legs.Count == 0)
            return legs;

        var working = legs.ToList();

        while (working.Sum(l => l.RiskUsd) > budget + 1e-9)
        {
            // Finest funded tier: the smallest value per point still carrying contracts.
            var index = -1;
            var lightest = double.MaxValue;

            for (var i = 0; i < working.Count; i++)
            {
                var pointValue = working[i].Instrument.TickValue / working[i].Instrument.TickSize;

                if (working[i].Quantity > 0 && pointValue < lightest)
                {
                    lightest = pointValue;
                    index = i;
                }
            }

            if (index < 0)
                return Array.Empty<SizeLeg>();

            var leg = working[index];
            var weight = (int)Math.Round(
                leg.Instrument.TickValue / leg.Instrument.TickSize / finest.PointValue,
                MidpointRounding.AwayFromZero);

            var reduced = leg.Quantity - 1;
            usedMicroEquivalents -= Math.Max(weight, 1);

            if (reduced <= 0)
                working.RemoveAt(index);
            else
                working[index] = leg with
                {
                    Quantity = reduced,
                    RiskUsd = reduced * leg.StopTicks * leg.Instrument.TickValue,
                };

            if (working.Count == 0)
                return Array.Empty<SizeLeg>();
        }

        return working;
    }

    /// <summary>
    /// Converts the stop distance to whole ticks for a tier, rounding away from entry.
    ///
    /// Rounding down would place the stop nearer than the structure asked for, which is a
    /// silent tightening of risk; rounding up costs a tick and keeps the stop where the
    /// analysis put it.
    /// </summary>
    private static int StopTicksFor(double stopDistancePrice, InstrumentSpec spec)
        => spec.TickSize > 0
            ? (int)Math.Ceiling(stopDistancePrice / spec.TickSize)
            : 0;
}
