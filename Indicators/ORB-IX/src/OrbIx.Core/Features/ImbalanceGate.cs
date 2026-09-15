using System;
using System.Globalization;
using OrbIx.Core.Scoring;

namespace OrbIx.Core.Features;

/// <summary>
/// What the imbalance gate concluded about one break.
/// </summary>
public enum ImbalanceGateState
{
    /// <summary>
    /// The bar carried too little classified volume for any diagonal to be judged, so the
    /// gate has no opinion.
    ///
    /// THIS IS NOT A FAILURE AND MUST NEVER VETO. Blocking here would convert "nothing was
    /// measured" into "the setup was bad", which is the exact conflation this codebase
    /// refuses everywhere else — <see cref="OrbIx.Core.Abstractions.FeatureOutput.Silent"/> for a dark tier, and
    /// <c>UnavailableClauses</c> for confirmation clauses the data cannot support. An
    /// unmeasured gate is reported as an unevaluated clause, the way iceberg and depth
    /// already are.
    /// </summary>
    Unmeasured = 0,

    /// <summary>Measured, and the stacked run in the trade's direction met the threshold.</summary>
    Pass = 1,

    /// <summary>Measured, and it did not.</summary>
    Block = 2,
}

/// <summary>
/// The gate's verdict, recorded whether or not the gate is enforcing.
/// </summary>
/// <param name="State">What the measurement said.</param>
/// <param name="Enforcing">Whether this verdict was allowed to veto the entry.</param>
/// <param name="RequiredRun">Consecutive imbalanced ticks the direction needed.</param>
/// <param name="DirectionalRun">Longest run found in the trade's direction.</param>
/// <param name="OpposingRun">Longest run found against it, for context.</param>
/// <param name="Coverage">Fraction of the bar's diagonals that could be judged.</param>
/// <param name="Reason">Human-readable statement of the above.</param>
public sealed record ImbalanceGateVerdict(
    ImbalanceGateState State,
    bool Enforcing,
    int RequiredRun,
    int DirectionalRun,
    int OpposingRun,
    double Coverage,
    string Reason)
{
    /// <summary>
    /// Whether this verdict actually blocks the entry.
    ///
    /// Only a measured Block, and only while enforcing. Everything else is recorded and
    /// allowed through — which is what makes the counterfactual in the journal readable:
    /// rows where the gate WOULD have blocked but did not are exactly the evidence needed
    /// to decide whether it should.
    /// </summary>
    public bool Blocks => this.Enforcing && this.State == ImbalanceGateState.Block;

    /// <summary>
    /// Whether the gate would have blocked had it been enforcing. Equal to
    /// <see cref="Blocks"/> when it is; the point of it is when it is not.
    /// </summary>
    public bool WouldBlock => this.State == ImbalanceGateState.Block;
}

/// <summary>
/// Stacked footprint imbalance as an entry gate.
///
/// WHAT IT ASKS. A break in a direction should be carried by aggressive flow in that
/// direction: a run of consecutive price rows where buying overwhelmed the selling one tick
/// below it (or the mirror, for a short). The gate asks whether the bar that made the break
/// contains such a run, and how long.
///
/// NO EDGE HAS BEEN MEASURED FOR THIS AND IT SHIPS ANYWAY. That is deliberate, and it is
/// the reason <see cref="ImbalanceGateVerdict.WouldBlock"/> exists alongside
/// <see cref="ImbalanceGateVerdict.Blocks"/>. Every signal records what the gate decided AND
/// what it would have decided, so the question "does this gate help?" is answerable from
/// forward evidence — the only evidence on this stack that has never been faked by a
/// lookahead. Backtesting it first would answer a different question with data that has
/// repeatedly answered it wrongly.
/// </summary>
public static class ImbalanceGate
{
    /// <summary>
    /// Judges a break against the imbalance of the bar that made it.
    /// </summary>
    /// <param name="reading">
    /// The closed bar's imbalance, or null when imbalance was not measured at all — a
    /// different fact from a bar that was measured and found quiet.
    /// </param>
    /// <param name="direction">The direction the break is in.</param>
    /// <param name="minRun">Consecutive imbalanced ticks required. See <see cref="ImbalanceRule.DefaultMinRun"/>.</param>
    /// <param name="enforcing">Whether the verdict may veto, or is recorded only.</param>
    public static ImbalanceGateVerdict Evaluate(
        ImbalanceReading? reading, TradeDirection direction, int minRun, bool enforcing)
    {
        if (minRun < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minRun), minRun,
                "A run of zero is satisfied by every bar, including one that never traded.");
        }

        if (reading is null)
        {
            return new ImbalanceGateVerdict(
                ImbalanceGateState.Unmeasured, enforcing, minRun, 0, 0, 0d,
                "Imbalance was not measured on this bar.");
        }

        var directional = direction == TradeDirection.Long
            ? reading.LongestBuyRun
            : reading.LongestSellRun;

        var opposing = direction == TradeDirection.Long
            ? reading.LongestSellRun
            : reading.LongestBuyRun;

        if (reading.JudgedDiagonals == 0)
        {
            return new ImbalanceGateVerdict(
                ImbalanceGateState.Unmeasured, enforcing, minRun, directional, opposing,
                reading.Coverage,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "No diagonal on the break bar carried enough volume to judge ({0} rows examined).",
                    reading.TotalDiagonals / 2));
        }

        var passed = directional >= minRun;

        return new ImbalanceGateVerdict(
            passed ? ImbalanceGateState.Pass : ImbalanceGateState.Block,
            enforcing, minRun, directional, opposing, reading.Coverage,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} stacked {1} imbalance of {2} ticks against a required {3}; {4} ticks stacked the "
                + "other way, over {5:P0} of the bar's diagonals.",
                passed ? "Confirmed" : "Insufficient",
                direction == TradeDirection.Long ? "buy" : "sell",
                directional, minRun, opposing, reading.Coverage));
    }

    /// <summary>
    /// The clause text naming this gate as unevaluated, for the journal's
    /// <c>UnevaluatedClauses</c> list.
    /// </summary>
    public const string UnevaluatedClause =
        "stacked footprint imbalance in the break direction (bar carried too little classified volume)";
}
