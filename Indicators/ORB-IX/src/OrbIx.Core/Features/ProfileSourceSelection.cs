using System;
using System.Collections.Generic;
using System.Linq;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Features;

/// <summary>
/// One symbol the platform is offering, reduced to what the choice below needs.
///
/// A PROJECTION, NOT THE PLATFORM TYPE. OrbIx.Core has no reference to the trading platform
/// (§11), and that is what makes this decision unit-testable — the shell reads
/// <c>Core.Symbols</c> and fills these in; nothing here knows what a Quantower Symbol is.
/// </summary>
/// <param name="Name">The vendor's name for it, for the log only. Never matched on.</param>
/// <param name="ConnectionId">Opaque connection identifier, used to recognise the chart's own.</param>
/// <param name="ConnectionName">Human-readable connection, for the chart label.</param>
/// <param name="Root">
/// The contract root AS THE VENDOR GAVE IT. Normalisation happens in
/// <see cref="ProfileSourceSelection"/>, so a caller cannot forget to do it.
/// </param>
/// <param name="ExpirationDate">Contract expiry. <c>default</c> when the vendor gives none.</param>
/// <param name="ServesTickHistory">Whether this symbol's connection serves tick history.</param>
/// <param name="LastPrice">Last traded price, for the divergence backstop. NaN when unknown.</param>
public readonly record struct SymbolCandidate(
    string Name,
    string ConnectionId,
    string ConnectionName,
    string Root,
    DateTime ExpirationDate,
    bool ServesTickHistory,
    double LastPrice);

/// <summary>How the search for a tick-history source ended.</summary>
public enum ProfileSourceOutcome
{
    /// <summary>The chart's own connection serves it. Nothing is borrowed.</summary>
    OwnConnection,

    /// <summary>Another open connection carries the same contract and serves it.</summary>
    Borrowed,

    /// <summary>
    /// The symbol list is empty. NOT the same as "no such symbol" — the platform populates
    /// it asynchronously, so this means try again.
    /// </summary>
    NotPopulatedYet,

    /// <summary>The list is populated and no connection on it serves this contract.</summary>
    NoCapableConnection,
}

/// <summary>The chosen source, and the words for it.</summary>
/// <param name="Outcome">How the search ended.</param>
/// <param name="Chosen">The symbol to load from, or null when there is none.</param>
/// <param name="Status">A sentence for the log. Always populated.</param>
/// <param name="SourceLabel">
/// What the CHART says about provenance. Empty when the chart's own connection served it,
/// because there is nothing to disclose; non-empty names the connection borrowed from.
/// </param>
public sealed record ProfileSourceChoice(
    ProfileSourceOutcome Outcome,
    SymbolCandidate? Chosen,
    string Status,
    string SourceLabel);

/// <summary>
/// Picks which connection's tick history should feed the volume profile.
///
/// WHY THIS EXISTS. Measured on ryzen-pc on 2026-09-01, sixty seconds apart: the same
/// instrument served 82,063 prints on one connection/one data vendor and was refused on another connection/another connection,
/// whose own metadata reports <c>available=False</c>, <c>periodsWithLevels=none</c> and
/// <c>AllowedAggregations=(Time)</c>. The capability belongs to the CONNECTION, not the
/// symbol. No amount of waiting makes a connection serve data it does not have — so when the
/// chart's own connection refuses, the same contract is loaded from one that does.
///
/// THE SHARPEST HAZARD IS BORROWING THE WRONG CONTRACT, and the rules below exist for it. A
/// profile drawn from a neighbouring expiry would look entirely plausible and be entirely
/// wrong, and nothing downstream could detect it.
/// </summary>
public static class ProfileSourceSelection
{
    /// <summary>
    /// How far a candidate's last price may sit from the chart's before it is refused.
    ///
    /// A BACKSTOP, NOT THE MATCH. Root and expiry already pin the contract; this catches the
    /// pathological case where they agree and the instrument still is not the same one. It is
    /// deliberately loose because the two feeds are different vendors and their last prints
    /// are not simultaneous — the recorded warning is that prices "should not be assumed
    /// identical between them", not that they should be close to the tick.
    /// </summary>
    public const double MaxPriceDivergence = 0.005;

    /// <summary>
    /// Chooses the source for one chart.
    ///
    /// The chart's own connection ALWAYS wins when it is capable. Borrowing is a fallback and
    /// never a preference: a chart that can answer for itself must not be quietly fed from
    /// somewhere else.
    /// </summary>
    /// <param name="chartRoot">Root of the chart's symbol, vendor-supplied.</param>
    /// <param name="chartName">
    /// The chart symbol's full vendor name, e.g. <c>MNQU6</c>.
    ///
    /// READ ONLY WHEN AN EXPIRY DATE IS MISSING. Measured 2026-09-01: the platform leaves
    /// <c>ExpirationDate</c> unset on some chart symbols and populated on others, for the
    /// same contract in the same process. The name carries the delivery term on every
    /// observed spelling, so it is the fallback when the date is not there.
    /// </param>
    /// <param name="chartExpiry">
    /// Expiry of the chart's contract, or <c>default</c> when the platform did not supply
    /// one. Unset is a real and common case, not a programmer error.
    /// </param>
    /// <param name="chartLastPrice">Chart's last price, for the backstop. NaN if unknown.</param>
    /// <param name="chartConnectionId">Connection the chart itself is on.</param>
    /// <param name="nowUtc">
    /// The current instant, for choosing the front month of a CONTINUOUS chart.
    ///
    /// PASSED IN, NEVER READ HERE. OrbIx.Core takes no clock of its own (§11) — a decision
    /// that reads the wall clock cannot be tested against a roll date, and the roll date is
    /// the only interesting thing about this parameter.
    /// </param>
    /// <param name="candidates">
    /// Every symbol the platform is currently offering.
    ///
    /// ANNOTATED NULLABLE DELIBERATELY. The shell reads this from the platform, which can hand
    /// back nothing at all, so null is a real runtime possibility rather than something the
    /// type system rules out. It is rejected here with a named exception rather than asserted
    /// away at the call site, where the assertion would be the caller's guess.
    /// </param>
    /// <exception cref="ArgumentNullException">The candidate list is null.</exception>
    public static ProfileSourceChoice Select(
        string chartRoot,
        string chartName,
        DateTime chartExpiry,
        double chartLastPrice,
        string chartConnectionId,
        DateTime nowUtc,
        IReadOnlyList<SymbolCandidate>? candidates)
    {
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));

        // NOT AN ERROR, AND SAYING SO IS THE POINT. Core.Symbols was observed returning 0
        // entries twice and then 1,072 two minutes later, on the same install with no config
        // change. An indicator that treated the empty list as an answer would report "no such
        // symbol" for the first two minutes of every session.
        if (candidates.Count == 0)
        {
            return new ProfileSourceChoice(
                ProfileSourceOutcome.NotPopulatedYet, null,
                "symbol list is empty — the platform has not populated it yet", string.Empty);
        }

        // FromContractName, NOT Normalise, and the difference is the whole match. Normalise
        // strips vendor decoration only, so `MNQU6` and `/MNQU26:XCME` stay carrying their
        // expiry and never equal a bare `MNQ`. FromContractName removes the expiry too, and is
        // idempotent on a root that never had one.
        //
        // BOTH SIDES GO THROUGH THE SAME FUNCTION. That is what makes the comparison sound
        // even for a product this reduction handles imperfectly: whatever it does to one
        // spelling it does to the other, so the two either agree or genuinely differ.
        var wanted = ContractRoot.FromContractName(chartRoot);

        var chartTerm = ContractExpiry.FromContractName(chartName);

        var onRoot = candidates
            .Where(c => ContractRoot.FromContractName(c.Root) == wanted)
            .ToList();

        // A CONTINUOUS CHART NAMES NO CONTRACT, so there is nothing to match it against.
        //
        // Measured 2026-09-01: charts on `/MNQ:XCME` and `/MES:XCME` carry neither an expiry
        // date nor a delivery month in their name, because a continuous series is not one
        // contract. Matching correctly refused them and they drew no profile at all.
        //
        // The operator's decision, taken with the roll consequence stated: such a chart shows
        // the FRONT MONTH's profile. The two conditions below are exactly the two the match
        // rules already use, so "continuous" needs no new definition — it is the state in
        // which neither rule has anything to work with.
        var continuousChart = chartExpiry == default && chartTerm is null;

        var frontMonth = continuousChart ? FrontMonth(onRoot, nowUtc) : null;

        var sameContract = continuousChart
            ? onRoot.Where(c => frontMonth is { } month && c.ExpirationDate.Date == month).ToList()
            : onRoot.Where(c => SameContract(c, chartTerm, chartExpiry)).ToList();

        var contract = continuousChart
            ? DescribeFrontMonth(frontMonth)
            : DescribeContract(chartExpiry, chartTerm);

        // The chart's own connection first, whatever else is on offer.
        var own = sameContract.FirstOrDefault(c => c.ConnectionId == chartConnectionId);

        if (own.ServesTickHistory)
        {
            return new ProfileSourceChoice(
                ProfileSourceOutcome.OwnConnection, own,
                $"{own.ConnectionName} (the chart's own connection) serves tick history",
                string.Empty);
        }

        // Ordered by connection name so two runs over the same platform state choose the same
        // source. An arbitrary order would make the provenance label flicker between reloads.
        var borrowable = sameContract
            .Where(c => c.ConnectionId != chartConnectionId && c.ServesTickHistory)
            .OrderBy(c => c.ConnectionName, StringComparer.Ordinal)
            .ToList();

        foreach (var candidate in borrowable)
        {
            if (!WithinPriceTolerance(candidate.LastPrice, chartLastPrice))
                continue;

            // THE CONTRACT IS NAMED ON THE CHART ONLY WHEN THE CHART CANNOT NAME IT ITSELF.
            //
            // On a dated chart the contract is already on screen and repeating it is noise. On
            // a continuous one it is the one thing the screen does not say — and it is what
            // changes, without warning, on roll day. The operator accepted that roll; this
            // label is what makes it visible the first time the chart is looked at.
            var label = continuousChart
                ? $"via {candidate.ConnectionName} — {candidate.Name}"
                : $"via {candidate.ConnectionName}";

            return new ProfileSourceChoice(
                ProfileSourceOutcome.Borrowed, candidate,
                $"borrowed from {candidate.ConnectionName} as {candidate.Name}, matched "
                + $"{contract}; the chart's own connection does not serve tick history",
                label);
        }

        var refusedOnPrice = borrowable.Count;

        return new ProfileSourceChoice(
            ProfileSourceOutcome.NoCapableConnection, null,
            Describe(wanted, contract, candidates.Count, sameContract.Count, refusedOnPrice),
            string.Empty);
    }

    /// <summary>
    /// Whether a candidate is the SAME CONTRACT as the chart, on the strongest evidence
    /// available.
    ///
    /// THIS IS THE RULE THAT STOPS THE WORST OUTCOME. `MNQU6` (September), `MNQZ6`
    /// (December) and a continuous `/MNQ:XCME` all normalise to the root `MNQ`, so matching
    /// on root alone would happily borrow a different month — or a continuous series that is
    /// not a contract at all — and draw a confident profile of the wrong instrument that
    /// nothing downstream could detect.
    ///
    /// Three rules, strongest first:
    ///
    ///   1. BOTH EXPIRY DATES KNOWN — compare them to the day. The platform's own answer
    ///      wins wherever it gives one.
    ///   2. EITHER UNKNOWN — compare the delivery term parsed from the two NAMES.
    ///   3. EITHER NAME CARRIES NO TERM — refuse.
    ///
    /// Rule 2 exists because an unset date is a real state, not an error: measured
    /// 2026-09-01, two instances of this indicator in one process disagreed about whether
    /// their own chart symbol had an ExpirationDate, both charting MNQU6. Before rule 2 the
    /// two without a date could never borrow and drew no profile at all.
    ///
    /// Rule 3 is rule 1's old guard, intact. A continuous series carries neither a date nor
    /// a parseable term, so it is still refused rather than treated as a wildcard — which is
    /// exactly how a continuous series would otherwise present itself.
    /// </summary>
    private static bool SameContract(
        SymbolCandidate candidate, ContractTerm? chartTerm, DateTime chartExpiry)
    {
        if (candidate.ExpirationDate != default && chartExpiry != default)
            return candidate.ExpirationDate.Date == chartExpiry.Date;

        return chartTerm is { } chart
               && ContractExpiry.FromContractName(candidate.Name) is { } offered
               && ContractExpiry.SameTerm(offered, chart);
    }

    /// <summary>
    /// The nearest expiry that has not yet passed, among dated candidates on this root.
    ///
    /// A CONTRACT EXPIRING TODAY IS STILL THE FRONT MONTH — the comparison is on
    /// <c>Date</c>, not the instant. Chosen by the operator on 2026-09-01 over rolling a
    /// day early: the profile shows the September contract all through its expiry day and
    /// changes to December the following day, so the switch happens once at a date boundary
    /// with no mid-session change of contract underneath a running chart.
    ///
    /// A CANDIDATE WITH NO EXPIRY DATE IS NOT ELIGIBLE. It cannot be ordered by expiry, and
    /// deriving a date from its name would need the single-digit-year expansion that
    /// <see cref="ContractExpiry"/> deliberately refuses to invent. Measured on the
    /// connections in use: every dated candidate carries a real date, so this excludes
    /// nothing that is actually on offer.
    ///
    /// Returns null when nothing dated is on offer — a different situation from a chart
    /// nobody can identify, and reported as one.
    /// </summary>
    private static DateTime? FrontMonth(IReadOnlyList<SymbolCandidate> onRoot, DateTime nowUtc)
    {
        DateTime? front = null;

        foreach (var candidate in onRoot)
        {
            if (candidate.ExpirationDate == default)
                continue;

            var expiry = candidate.ExpirationDate.Date;

            if (expiry < nowUtc.Date)
                continue;

            if (front is null || expiry < front.Value)
                front = expiry;
        }

        return front;
    }

    /// <summary>
    /// The front-month choice, in words, saying plainly that the chart named no contract.
    ///
    /// A profile on a continuous chart is ONE CONTRACT'S volume, not the series'. Nothing
    /// here aggregates across contracts and nothing should read as though it did.
    /// </summary>
    private static string DescribeFrontMonth(DateTime? front)
        => front is { } month
            ? $"front month expiring {month:yyyy-MM-dd} (the chart is a continuous series "
              + "and names no contract of its own)"
            : "as a continuous series, but no unexpired dated contract on this root is on offer";

    /// <summary>
    /// How the contract was identified, for the status line.
    ///
    /// A borrow justified by a term read out of a name is WEAKER EVIDENCE than one
    /// justified by a date the platform supplied, and the operator can only weigh that if
    /// the line says which it was. Collapsing the two into one sentence would present the
    /// fallback as though it were the primary rule.
    /// </summary>
    private static string DescribeContract(DateTime chartExpiry, ContractTerm? chartTerm)
    {
        if (chartExpiry != default)
            return $"expiring {chartExpiry:yyyy-MM-dd}";

        return chartTerm is { } term
            ? $"contract {ContractExpiry.Describe(term)} (the chart's symbol carries no "
              + "expiry date; the term was read from its name)"
            : "with no expiry date and no contract term in its name";
    }

    /// <summary>
    /// The divergence backstop, applied only when both prices are usable.
    ///
    /// An unknown price on either side is NOT treated as a failure: the candidate has already
    /// matched on root and expiry, and refusing it for want of a quote would disable borrowing
    /// on a quiet market — which is precisely when the chart has no other source either.
    /// </summary>
    private static bool WithinPriceTolerance(double candidate, double chart)
    {
        if (!PriceValue.IsUsable(candidate) || !PriceValue.IsUsable(chart))
            return true;

        return Math.Abs(candidate - chart) / chart <= MaxPriceDivergence;
    }

    /// <summary>
    /// Why nothing was chosen, in terms an operator can act on.
    ///
    /// The counts separate three different situations that would otherwise share one sentence:
    /// no connection carries this contract at all, connections carry it but none serves tick
    /// history, and one would have served it but its price disagreed.
    ///
    /// THE CONTRACT ARRIVES PRE-RENDERED rather than as a date. This line used to format
    /// <c>default(DateTime)</c> straight into the message and print "expiring 0001-01-01",
    /// which reads as a corrupt date and sent a reader chasing one that was never there —
    /// the truth was that the chart's symbol carried no expiry at all.
    /// </summary>
    private static string Describe(
        string root, string contract, int offered, int sameContract, int refusedOnPrice)
    {
        if (sameContract == 0)
        {
            return $"no connection offers {root} {contract} "
                + $"({offered} symbol(s) offered in total)";
        }

        if (refusedOnPrice > 0)
        {
            return $"{sameContract} connection(s) carry {root} {contract} and "
                + $"{refusedOnPrice} would have served tick history, but their last price "
                + $"diverged by more than {MaxPriceDivergence:P1} from the chart's";
        }

        return $"{sameContract} connection(s) carry {root} {contract} and none "
            + "serves tick history";
    }
}
