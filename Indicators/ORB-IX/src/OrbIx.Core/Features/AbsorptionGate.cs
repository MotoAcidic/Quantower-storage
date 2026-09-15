using System;
using System.Globalization;
using OrbIx.Core.Scoring;

namespace OrbIx.Core.Features;

/// <summary>
/// What the absorption gate concluded about one break.
/// </summary>
public enum AbsorptionGateState
{
    /// <summary>
    /// The book could not be vouched for, or the touch had no observed baseline, so the gate
    /// has no opinion.
    ///
    /// NEVER VETOES. Blocking here would convert "the depth tier was dark" into "the setup
    /// was bad" — the conflation this codebase refuses everywhere, and the same rule
    /// <see cref="ImbalanceGate"/> follows.
    /// </summary>
    Unmeasured = 0,

    /// <summary>Measured, and the touch on the trade's side held under aggression.</summary>
    Pass = 1,

    /// <summary>Measured, and it did not.</summary>
    Block = 2,
}

/// <summary>
/// The gate's verdict, recorded whether or not it was allowed to act.
/// </summary>
/// <param name="State">What the measurement said.</param>
/// <param name="Enforcing">Whether this verdict could veto the entry.</param>
/// <param name="Reading">The touch on the trade's own side.</param>
/// <param name="Opposing">The other side's touch, for context.</param>
/// <param name="Reason">Human-readable statement of the above.</param>
public sealed record AbsorptionGateVerdict(
    AbsorptionGateState State,
    bool Enforcing,
    AbsorptionReading Reading,
    AbsorptionReading Opposing,
    string Reason)
{
    /// <summary>Whether this verdict actually blocks the entry.</summary>
    public bool Blocks => this.Enforcing && this.State == AbsorptionGateState.Block;

    /// <summary>
    /// Whether the gate WOULD have blocked, independent of whether it was allowed to.
    ///
    /// THE COUNTERFACTUAL. With absorption already measured null on this instrument, the
    /// rows where this is true are the only way the decision to gate on it can ever be
    /// re-examined against outcomes rather than re-argued.
    /// </summary>
    public bool WouldBlock => this.State == AbsorptionGateState.Block;
}

/// <summary>
/// Absorption at the touch as an entry gate.
///
/// WHAT IT ASKS, AND WHICH SIDE. Per trial 008's convention — <c>passive_side BUY</c> means
/// resting BIDS hit by SELL aggressors, and favourable is UP — a long break should be
/// supported by bids ABSORBING the selling into it. A short break wants the mirror: asks
/// holding under aggressive buying. Reading the wrong side would pass exactly the setups
/// this exists to refuse, which is why it has a test of its own.
///
/// IT SHIPS ENFORCING AGAINST A MEASURED NULL, BY THE OPERATOR'S EXPLICIT DECISION. Trial
/// 008 measured absorption at +1.006 ticks versus matched-random (z 2.23, p 0.0260) across
/// 25,745 episodes — failing Bonferroni at p&lt;0.0100 and below the 2.76-tick cost floor
/// even taken at face value; the four-step sequence variant adds +0.475 ± 1.786 over plain
/// absorption. Nothing here overturns that and nothing here claims to. What this type
/// guarantees is that the decision stays a decision: every verdict, its counterfactual and
/// the components behind it reach the journal, so forward sessions can settle it.
/// </summary>
public static class AbsorptionGate
{
    /// <summary>
    /// Judges a break against the touch on its own side.
    /// </summary>
    /// <param name="snapshot">Both touches, or null when absorption was not measured at all.</param>
    /// <param name="direction">The direction the break is in.</param>
    /// <param name="enforcing">Whether the verdict may veto, or is recorded only.</param>
    public static AbsorptionGateVerdict Evaluate(
        AbsorptionSnapshot? snapshot, TradeDirection direction, bool enforcing)
    {
        var longSide = direction == TradeDirection.Long;

        if (snapshot is null || !snapshot.BookKnown)
        {
            return new AbsorptionGateVerdict(
                AbsorptionGateState.Unmeasured, enforcing,
                AbsorptionReading.Unmeasured(double.NaN, longSide ? BookSideFor(true) : BookSideFor(false)),
                AbsorptionReading.Unmeasured(double.NaN, longSide ? BookSideFor(false) : BookSideFor(true)),
                snapshot is null
                    ? "Absorption was not measured."
                    : "The book could not be vouched for, so absorption at the touch is unknown.");
        }

        var reading = snapshot.For(longSide);
        var opposing = snapshot.For(!longSide);

        if (!reading.IsMeasured)
        {
            return new AbsorptionGateVerdict(
                AbsorptionGateState.Unmeasured, enforcing, reading, opposing,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "No observed baseline at the {0} touch, so absorption there cannot be judged.",
                    longSide ? "bid" : "ask"));
        }

        var passed = reading.IsAbsorbing;

        return new AbsorptionGateVerdict(
            passed ? AbsorptionGateState.Pass : AbsorptionGateState.Block,
            enforcing, reading, opposing,
            string.Format(
                CultureInfo.InvariantCulture,
                "The {0} touch at {1:N2} read {2} ({3:N0} traded against {4:N0} of size lost{5}); "
                + "a {6} break wants it absorbing.",
                longSide ? "bid" : "ask",
                reading.Price,
                reading.State,
                reading.AbsorbedVolume,
                reading.SizeReduction,
                reading.Ratio is { } ratio
                    ? string.Format(CultureInfo.InvariantCulture, ", ratio {0:N2}", ratio)
                    : string.Empty,
                direction));
    }

    /// <summary>
    /// The clause text naming this gate as unevaluated, for the journal's
    /// <c>UnevaluatedClauses</c> list.
    /// </summary>
    public const string UnevaluatedClause =
        "absorption at the touch on the trade's side (the depth tier was dark or had no baseline)";

    private static Abstractions.BookSide BookSideFor(bool bid)
        => bid ? Abstractions.BookSide.Bid : Abstractions.BookSide.Ask;
}
