using System;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Features;

/// <summary>
/// What happened at one price over the absorption window.
///
/// SIX STATES, BECAUSE THERE ARE SIX DISTINCT FACTS. The Python this is ported from
/// (<c>phase1/microstructure.py::_level_stats</c>) reports a single nullable ratio, which
/// collapses three of these into "null" — no baseline, nothing happened, and size that held
/// under fire all read the same. The last of those is the strongest absorption there is, so
/// the collapse loses precisely the case the measure exists to find.
/// </summary>
public enum AbsorptionState
{
    /// <summary>
    /// The size at the start of the window is not known, or the book itself is not known.
    /// A REFUSAL TO ANSWER, never a finding of no absorption.
    /// </summary>
    Unmeasured = 0,

    /// <summary>Nothing traded here and the size did not fall. Nothing happened.</summary>
    Quiet = 1,

    /// <summary>
    /// Size left the level without volume trading through it — orders pulled, not filled.
    /// The opposite of absorption.
    /// </summary>
    Cancelled = 2,

    /// <summary>Volume traded roughly in proportion to the size that disappeared.</summary>
    Consumed = 3,

    /// <summary>Far more traded than the size that disappeared: the level was refilled as it was hit.</summary>
    Absorbed = 4,

    /// <summary>
    /// Volume traded and the size did not fall at all. The strongest form, and the one that
    /// has no ratio — the denominator is zero or negative, so the components carry the claim.
    /// </summary>
    Replenished = 5,
}

/// <summary>
/// One price's absorption over the window.
/// </summary>
/// <param name="State">What happened.</param>
/// <param name="Price">The level.</param>
/// <param name="Side">Which side of the book was resting there.</param>
/// <param name="AbsorbedVolume">Aggressive volume that traded at this price inside the window.</param>
/// <param name="SizeReduction">
/// Size at the window's start minus size now. Negative means the level GREW.
/// </param>
/// <param name="Ratio">
/// <paramref name="AbsorbedVolume"/> over <paramref name="SizeReduction"/>, or null when the
/// reduction is not positive. NEVER infinity: an unbounded ratio is not a number a journal
/// can carry, and the two components say everything it would have.
/// </param>
public readonly record struct AbsorptionReading(
    AbsorptionState State,
    double Price,
    BookSide Side,
    double AbsorbedVolume,
    double SizeReduction,
    double? Ratio)
{
    /// <summary>Nothing could be said about this level.</summary>
    public static AbsorptionReading Unmeasured(double price, BookSide side)
        => new(AbsorptionState.Unmeasured, price, side, 0d, 0d, null);

    /// <summary>The level held under aggression — absorbed outright, or refilled as it was hit.</summary>
    public bool IsAbsorbing
        => this.State is AbsorptionState.Absorbed or AbsorptionState.Replenished;

    /// <summary>Something was measured here, whatever it said.</summary>
    public bool IsMeasured => this.State != AbsorptionState.Unmeasured;
}

/// <summary>
/// Absorption at a price: did size HOLD while volume traded through it.
///
/// THE DEFINITION IS NOT NEW AND IS NOT REINVENTED HERE. It is the operator-approved one
/// from <c>the research repository: phase1/microstructure.py</c>:
///
///     absorbed_volume  = volume traded at P inside the window
///     size_reduction   = size_at(window start) - size_now
///     absorption_ratio = absorbed_volume / size_reduction
///
/// with a 5-second window. That file records the reason the window is a stated constant
/// rather than a discovered one: NO OFFICIAL SOURCE DEFINES IT. Quantower's documentation
/// defines Delta as traded volume and says nothing about absorption, and CME's liquidity
/// material was unreachable from that host. So the span is a deliberate choice, named where
/// it can be changed rather than buried in an expression.
///
/// WHAT THE SCALE MEANS: ~0 is size leaving without trading (pure cancellation), ~1 is
/// consumption, and well above 1 is absorption — the level being refilled as fast as it is
/// hit.
///
/// THIS SIGNAL IS A MEASURED NULL ON MNQ AND IS BUILT ANYWAY, BY DECISION. Trial 008:
/// 25,745 episodes over 24 windows and 6 sessions, absorption versus matched-random +1.006
/// ticks at z 2.23 / p 0.0260 — which fails Bonferroni at p&lt;0.0100 AND sits below the
/// 2.76-tick cost floor even if it were real. The four-step sequence variant adds
/// +0.475 ± 1.786 ticks over plain absorption, which is nothing. Nothing in this type
/// overturns that; it computes what it says it computes and the journal records every
/// verdict beside its components so the question is re-answerable from forward evidence.
/// </summary>
public static class AbsorptionRule
{
    /// <summary>
    /// The window over which absorption is asked about, matching
    /// <c>phase1/microstructure.py::ABSORPTION_WINDOW_US</c>.
    ///
    /// A span is required, not optional: over a millisecond nothing is ever absorbed, and
    /// over an hour everything is.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    /// <summary>Below this the size left faster than it was traded: orders pulled, not filled.</summary>
    public const double DefaultCancelledBelow = 0.5d;

    /// <summary>At or above this, far more traded than left — the level was refilled.</summary>
    public const double DefaultAbsorbedAtOrAbove = 2.0d;

    /// <summary>
    /// Classifies one level.
    /// </summary>
    /// <param name="price">The level.</param>
    /// <param name="side">Which side of the book rested there.</param>
    /// <param name="absorbedVolume">Aggressive volume traded at the price inside the window.</param>
    /// <param name="sizeAtWindowStart">
    /// Resting size when the window opened, or null when it was never observed. Null yields
    /// <see cref="AbsorptionState.Unmeasured"/> — a reduction computed from an invented
    /// baseline would report absorption that never happened.
    /// </param>
    /// <param name="sizeNow">Resting size now, or null when the book is not known.</param>
    /// <param name="cancelledBelow">Ratio below which the level is judged cancelled.</param>
    /// <param name="absorbedAtOrAbove">Ratio at or above which the level is judged absorbed.</param>
    public static AbsorptionReading Evaluate(
        double price,
        BookSide side,
        double absorbedVolume,
        double? sizeAtWindowStart,
        double? sizeNow,
        double cancelledBelow = DefaultCancelledBelow,
        double absorbedAtOrAbove = DefaultAbsorbedAtOrAbove)
    {
        if (!(cancelledBelow > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cancelledBelow), cancelledBelow,
                "A cancellation threshold at or below zero can never be met.");
        }

        if (!(absorbedAtOrAbove > cancelledBelow))
        {
            throw new ArgumentOutOfRangeException(
                nameof(absorbedAtOrAbove), absorbedAtOrAbove,
                "The absorbed threshold must sit above the cancelled one, or the two states "
                + "overlap and a level would be both at once.");
        }

        // NaN fails every comparison, so a malformed volume lands on Unmeasured by the same
        // path as an unobserved baseline. That is the right answer for both.
        if (sizeAtWindowStart is not { } then || sizeNow is not { } now
            || double.IsNaN(absorbedVolume) || double.IsInfinity(absorbedVolume)
            || absorbedVolume < 0
            || double.IsNaN(then) || double.IsNaN(now))
        {
            return AbsorptionReading.Unmeasured(price, side);
        }

        var reduction = then - now;

        if (reduction <= 0)
        {
            // The level did not shrink. Whether that is absorption depends entirely on
            // whether anything traded into it.
            return new AbsorptionReading(
                absorbedVolume > 0 ? AbsorptionState.Replenished : AbsorptionState.Quiet,
                price, side, absorbedVolume, reduction, null);
        }

        var ratio = absorbedVolume / reduction;

        var state = ratio >= absorbedAtOrAbove ? AbsorptionState.Absorbed
            : ratio < cancelledBelow ? AbsorptionState.Cancelled
            : AbsorptionState.Consumed;

        return new AbsorptionReading(state, price, side, absorbedVolume, reduction, ratio);
    }
}
