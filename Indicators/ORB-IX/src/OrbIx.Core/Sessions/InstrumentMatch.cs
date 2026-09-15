using System;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Sessions;

/// <summary>
/// What a platform fill or position must look like to be counted as this chart's.
/// </summary>
/// <param name="SymbolId">The platform's own identifier, exact.</param>
/// <param name="Root">
/// What the vendor calls the product. On My Funded Futures this is decorated
/// (<c>"/MNQ:XCME"</c>) and on a plain connection it is bare.
/// </param>
/// <param name="Name">
/// The full contract name, consulted only when <paramref name="Root"/> is blank — the same
/// order <c>ResolveContractRoot</c> uses, so both arrive at one answer.
/// </param>
/// <param name="ConnectionId">
/// Which connection served it. NOT decoration: <c>Symbol.ComplexId</c> is
/// <c>(ConnectionId, ExchangeId, Id)</c>, so the platform itself treats connection as part of
/// a symbol's identity, and the same contract on two connections is two symbols.
/// </param>
public readonly record struct SymbolIdentity(
    string? SymbolId,
    string? Root,
    string? Name,
    string? ConnectionId);

/// <summary>
/// Decides whether a platform fill or position belongs to the chart's instrument.
///
/// WHAT THIS FIXES, measured on 2026-09-03. The operator layer watched a full session in
/// which 49 fills were taken on <c>/MNQU26:XCME</c> — including single fills of 7, 10 and 13
/// lots against a cap of 5 — and recorded NOTHING. Not one fill row, realised P&amp;L zero, the
/// position-cap rule silent. The account's own trade store held every one of them.
///
/// The cause was one predicate, written inline three times in the indicator:
///
///     X.Symbol?.Id != this.instrument.SymbolId
///
/// against the trade-history request, the live position list, and the live fill handler. The
/// chart was on <c>/MNQ:XCME</c> — the CONTINUOUS symbol — while fills land on the front
/// month. Exact string equality can never span that, so all three paths matched nothing and
/// every one of them reported the result as "no trades today", which is a different thing
/// entirely and read identically.
///
/// AN EXPERIMENT REFUTED THE FIRST FIX, which is why this does not simply compare contract
/// names. Moving the chart to <c>MNQU6</c> still matched nothing, because <c>MNQU6</c> is
/// one data vendor's name and <c>/MNQU26:XCME</c> is dxFeed's name for the same contract on a
/// different connection. Hence both halves below: the root makes the expiry and the vendor
/// decoration irrelevant, and the connection stops that breadth reaching another broker.
///
/// THE ROOT COMPARISON USES <see cref="ContractRoot.FromContractName"/>, NOT
/// <see cref="ContractRoot.Normalise"/>. Normalise strips decoration only, so
/// <c>"/MNQU26:XCME"</c> normalises to <c>"MNQU26"</c> and would not match — the fix would
/// have been inert while looking correct. FromContractName drops the expiry as well, and
/// drops the month letter ONLY when year digits were actually present, so a bare root
/// survives intact. Both forms reach <c>"MNQ"</c>, which is what
/// <see cref="InstrumentSpec.Tier"/> holds.
/// </summary>
public static class InstrumentMatch
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is the chart's instrument.
    /// </summary>
    /// <param name="instrument">The chart's resolved instrument.</param>
    /// <param name="chartConnectionId">
    /// The connection the chart itself is on. When blank the connection test is SKIPPED rather
    /// than failed: a chart that cannot name its own connection would otherwise match nothing
    /// at all, which is the blindness this type exists to end. The caller reports that it was
    /// unscoped — breadth is survivable, silence is not.
    /// </param>
    /// <param name="candidate">The fill or position being judged.</param>
    public static bool Matches(
        in InstrumentSpec instrument, string? chartConnectionId, in SymbolIdentity candidate)
    {
        // The exact identifier, which is what the platform hands back when the chart is
        // already on the traded contract. Correct on its own and costs one comparison.
        if (!string.IsNullOrWhiteSpace(candidate.SymbolId)
            && string.Equals(candidate.SymbolId, instrument.SymbolId, StringComparison.Ordinal))
        {
            return true;
        }

        // TIER, NOT ROOT. InstrumentSpec is built as (symbol.Id, familyRoot, contractRoot),
        // so Root is the FAMILY ("NQ") and Tier is the contract root ("MNQ"). FromContractName
        // yields "MNQ", so comparing it against Root would match nothing and comparing MNQ
        // against ES's family would be worse than nothing.
        var tier = ContractRoot.Normalise(instrument.Tier);

        if (tier.Length == 0)
            return false;

        var candidateRoot = ContractRootOf(candidate);

        if (!string.Equals(candidateRoot, tier, StringComparison.OrdinalIgnoreCase))
            return false;

        // SCOPED TO THE CONNECTION. Without this, a micro-Nasdaq position on a demo or a
        // second broker would be counted against this account's cap — over-counting, which is
        // a worse failure than the under-counting being fixed, because it would fire the cap
        // rule on contracts the operator does not hold.
        if (string.IsNullOrWhiteSpace(chartConnectionId)
            || string.IsNullOrWhiteSpace(candidate.ConnectionId))
        {
            return true;
        }

        return string.Equals(
            candidate.ConnectionId, chartConnectionId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract root behind a candidate, preferring its root and falling back to its name.
    ///
    /// The same order as the indicator's own <c>ResolveContractRoot</c>, deliberately: two
    /// different readings of "which product is this" would put the matcher and the chart's
    /// instrument resolution into disagreement, and the symptom would be an operator layer
    /// that counts fills on one chart and not another.
    /// </summary>
    public static string ContractRootOf(in SymbolIdentity candidate)
        => !string.IsNullOrWhiteSpace(candidate.Root)
            ? ContractRoot.FromContractName(candidate.Root)
            : ContractRoot.FromContractName(candidate.Name);
}
