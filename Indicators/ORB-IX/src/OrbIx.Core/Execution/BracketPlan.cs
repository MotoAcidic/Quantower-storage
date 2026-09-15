using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Execution;

/// <summary>
/// One leg of a bracket: a distance from entry in whole ticks, and how many contracts come
/// off there.
///
/// Ticks rather than a price because that is the connector's own unit — every order type it
/// exposes measures stop and target offsets that way, and converting to a price and back is
/// an opportunity to be wrong by a tick.
/// </summary>
/// <param name="Ticks">Distance from entry, always positive.</param>
/// <param name="Quantity">Contracts released at this leg.</param>
public readonly record struct BracketLeg(int Ticks, int Quantity);

/// <summary>
/// A complete bracket for exactly one order on exactly one instrument.
///
/// The invariant this type exists to hold: the target quantities sum to the order
/// quantity, and the stop quantities sum to the order quantity. The one data vendor connector
/// checks both before sending and rejects the order outright when either fails, so a plan
/// that cannot satisfy them is a plan that never reaches the exchange. Constructing one is
/// therefore the moment to enforce it, not the moment to hope.
/// </summary>
public sealed class BracketPlan
{
    public BracketPlan(
        InstrumentSpec instrument,
        int quantity,
        IReadOnlyList<BracketLeg> targets,
        IReadOnlyList<BracketLeg> stops,
        bool trailing)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A bracket needs a positive quantity.");

        this.Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.Stops = stops ?? throw new ArgumentNullException(nameof(stops));

        RequireSum(targets, quantity, "target");
        RequireSum(stops, quantity, "stop");

        foreach (var leg in targets.Concat(stops))
        {
            if (leg.Ticks <= 0)
            {
                throw new ArgumentException(
                    $"A bracket leg is {leg.Ticks} ticks from entry; offsets must be positive distances.",
                    nameof(targets));
            }
        }

        this.Instrument = instrument;
        this.Quantity = quantity;
        this.Trailing = trailing;
    }

    public InstrumentSpec Instrument { get; }

    public int Quantity { get; }

    public IReadOnlyList<BracketLeg> Targets { get; }

    public IReadOnlyList<BracketLeg> Stops { get; }

    /// <summary>
    /// Whether the connector's trailing flag is set. It applies to the whole bracket and
    /// trails from the last trade price; per-leg trailing is not expressible here, and
    /// anything finer has to be managed by the strategy at the cost of dying on disconnect.
    /// </summary>
    public bool Trailing { get; }

    /// <summary>Number of targets actually present, which may be fewer than four.</summary>
    public int TargetCount => this.Targets.Count;

    private static void RequireSum(IReadOnlyList<BracketLeg> legs, int quantity, string kind)
    {
        if (legs.Count == 0)
            throw new ArgumentException($"A bracket needs at least one {kind} leg.", nameof(legs));

        var total = legs.Sum(l => l.Quantity);

        if (total != quantity)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The {0} legs total {1} contracts but the order is for {2}. The connector rejects a "
                    + "bracket whose legs do not sum to the order quantity, so this plan could never be sent.",
                    kind, total, quantity),
                nameof(legs));
        }

        if (legs.Any(l => l.Quantity <= 0))
            throw new ArgumentException($"A {kind} leg with no quantity is not a leg.", nameof(legs));
    }
}

/// <summary>
/// Splits a position across the four targets.
///
/// Two rules, in order. A size with an explicit entry in the configured small-size ladder
/// uses it, because 40/30/20/10 of three contracts is a rounding argument rather than a
/// plan and the specification states what small sizes should actually do. Every other size
/// is apportioned from the configured fractions by largest remainder, which is the only
/// common apportionment method that guarantees the parts sum to the whole — and summing to
/// the whole is not a nicety here, it is what the connector validates.
/// </summary>
public static class TargetLadder
{
    /// <summary>
    /// Allocates <paramref name="quantity"/> contracts across the targets whose distances
    /// are given, dropping targets that receive nothing.
    /// </summary>
    /// <param name="quantity">Total contracts in the order.</param>
    /// <param name="targetTicks">
    /// Distance to each target, nearest first. At most four; fewer is permitted when the
    /// structure does not support four.
    /// </param>
    /// <param name="config">Allocation fractions and the small-size table.</param>
    public static IReadOnlyList<BracketLeg> Build(
        int quantity, IReadOnlyList<int> targetTicks, TargetsConfig config)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");
        if (targetTicks is null)
            throw new ArgumentNullException(nameof(targetTicks));
        if (config is null)
            throw new ArgumentNullException(nameof(config));
        if (targetTicks.Count == 0)
            throw new ArgumentException("At least one target distance is required.", nameof(targetTicks));
        if (targetTicks.Count > config.Allocation.Count)
        {
            throw new ArgumentException(
                $"{targetTicks.Count} target distances were supplied but only {config.Allocation.Count} "
                + "allocation fractions are configured.",
                nameof(targetTicks));
        }

        for (var i = 1; i < targetTicks.Count; i++)
        {
            if (targetTicks[i] <= targetTicks[i - 1])
            {
                throw new ArgumentException(
                    $"Target distances must increase away from entry; target {i + 1} at {targetTicks[i]} ticks "
                    + $"is not beyond target {i} at {targetTicks[i - 1]}.",
                    nameof(targetTicks));
            }
        }

        var quantities = AllocateQuantities(quantity, targetTicks.Count, config);

        var legs = new List<BracketLeg>(targetTicks.Count);
        for (var i = 0; i < targetTicks.Count; i++)
        {
            if (quantities[i] > 0)
                legs.Add(new BracketLeg(targetTicks[i], quantities[i]));
        }

        return legs;
    }

    private static int[] AllocateQuantities(int quantity, int targetCount, TargetsConfig config)
    {
        if (config.SmallSizeLadder.TryGetValue(quantity, out var table))
        {
            var fromTable = new int[targetCount];
            var placed = 0;

            for (var i = 0; i < targetCount; i++)
            {
                fromTable[i] = table[i];
                placed += table[i];
            }

            // The table is written for four targets. When fewer are available, the
            // quantity the missing targets would have taken moves to the last one present
            // rather than being dropped — a contract that is not allocated is a contract
            // with no exit.
            if (placed < quantity)
                fromTable[targetCount - 1] += quantity - placed;

            return fromTable;
        }

        return LargestRemainder(quantity, config.Allocation, targetCount);
    }

    /// <summary>
    /// Largest-remainder apportionment. Each target takes its floor share; the contracts
    /// left over by flooring go to the targets with the largest fractional remainders,
    /// nearest target first on a tie.
    ///
    /// Chosen over rounding each share independently because independent rounding does not
    /// sum to the whole, and the difference is exactly the rejection the connector issues.
    /// </summary>
    private static int[] LargestRemainder(int quantity, IReadOnlyList<double> fractions, int targetCount)
    {
        var weights = new double[targetCount];
        var weightTotal = 0d;

        for (var i = 0; i < targetCount; i++)
        {
            weights[i] = fractions[i];
            weightTotal += fractions[i];
        }

        if (weightTotal <= 0)
        {
            throw new ArgumentException(
                "Allocation fractions for the available targets total zero, so nothing can be apportioned.",
                nameof(fractions));
        }

        var allocated = new int[targetCount];
        var remainders = new (int Index, double Remainder)[targetCount];
        var placed = 0;

        for (var i = 0; i < targetCount; i++)
        {
            var exact = quantity * weights[i] / weightTotal;
            allocated[i] = (int)Math.Floor(exact);
            remainders[i] = (i, exact - allocated[i]);
            placed += allocated[i];
        }

        var leftover = quantity - placed;

        foreach (var (index, _) in remainders
                     .OrderByDescending(r => r.Remainder)
                     .ThenBy(r => r.Index)
                     .Take(leftover))
        {
            allocated[index]++;
        }

        // Flooring can leave a nearer target with nothing while a further one is funded,
        // which would exit the runner before the base hit. Pull the shortfall forward.
        for (var i = 0; i < targetCount; i++)
        {
            if (allocated[i] != 0)
                continue;

            for (var j = targetCount - 1; j > i; j--)
            {
                if (allocated[j] > 1)
                {
                    allocated[j]--;
                    allocated[i]++;
                    break;
                }
            }
        }

        return allocated;
    }
}
