using System;
using System.Text;

namespace OrbIx.Core.Abstractions;

/// <summary>
/// What the platform's own published rules say about a connection's Level 2 stream.
///
/// WHY A QUOTE AND NOT A CLASSIFICATION. <c>Rule.LEVEL2_IS_AGGREGATED</c> and
/// <c>Rule.LEVEL2_HAS_IMPLIED_SIZE</c> are public const strings on the installed
/// TradingPlatform.BusinessLayer v1.147.2, and every accessor that reads them —
/// <c>IsAllowed</c>, <c>GetStringValue</c>, <c>GetIntValue</c> — is public. So obtaining
/// them is trivial. Knowing what they MEAN is not.
///
/// BOTH RULES ARE PHRASED AS FACTS, NOT PERMISSIONS, and that is the whole difficulty.
/// "LEVEL2_IS_AGGREGATED" returning Allowed could mean "this feed IS aggregated" or "you
/// are permitted an aggregated feed"; those are different claims and one of them is the
/// opposite of the other for our purposes. No consumer of either constant was found
/// anywhere in the assembly outside its own declaration — searched
/// <c>Level2Quote</c>, <c>DOMQuote</c>, <c>Symbol</c> and <c>RulesManager</c> — so the
/// platform's own usage does not settle it either.
///
/// THEREFORE THIS TYPE INTERPRETS NOTHING. It records what each accessor returned, verbatim,
/// so the meaning can be established by comparison rather than assumed: reading it on a feed
/// independently measured as per-order and on one measured as aggregated is what will settle
/// which reading is right. Until then, nothing downstream may branch on it.
///
/// AND THERE IS CURRENTLY NOTHING TO OVERRULE. <see cref="MboVerdict"/> classifies a feed
/// statistically, but no production code constructs a <see cref="Level2Evidence"/> or calls
/// <see cref="MboVerdict.Classify"/> — it exists with its tests and nothing else. So this is
/// evidence being gathered toward a decision, not a decision being made.
/// </summary>
/// <param name="IsAggregatedAllowed">
/// <c>IsAllowed(LEVEL2_IS_AGGREGATED, symbol)</c> as the platform worded it, or empty when
/// it could not be read.
/// </param>
/// <param name="IsAggregatedValue">
/// <c>GetStringValue(LEVEL2_IS_AGGREGATED, connectionId)</c>, or empty. Read alongside the
/// permission form because a rule expressing a FACT may carry its answer as a value rather
/// than as an allow/deny, and which one it uses is not established.
/// </param>
/// <param name="HasImpliedSizeAllowed">The same, for <c>LEVEL2_HAS_IMPLIED_SIZE</c>.</param>
/// <param name="HasImpliedSizeValue">The same, for <c>LEVEL2_HAS_IMPLIED_SIZE</c>.</param>
/// <param name="SymbolSeen">
/// Whether there was a symbol to ask about at all.
///
/// SEPARATE FROM THE READINGS BECAUSE IT WAS NOT, AND THAT COST A RUN. The first version
/// returned the same empty report for "no symbol" and "asked and every accessor returned
/// nothing", so when both hosts logged "none could be read" on 2026-09-09 the report could
/// not say which had happened — a diagnostic that cannot diagnose its own failure. The two
/// are different faults with different fixes and they are now told apart.
/// </param>
public sealed record Level2RuleReport(
    string IsAggregatedAllowed,
    string IsAggregatedValue,
    string HasImpliedSizeAllowed,
    string HasImpliedSizeValue,
    bool SymbolSeen = false)
{
    /// <summary>The platform's published name. A typo would query a rule that does not exist.</summary>
    public const string IsAggregatedRuleName = "LEVEL2_IS_AGGREGATED";

    /// <summary>The platform's published name.</summary>
    public const string HasImpliedSizeRuleName = "LEVEL2_HAS_IMPLIED_SIZE";

    /// <summary>There was no symbol to ask about.</summary>
    public static Level2RuleReport NoSymbol { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, SymbolSeen: false);

    /// <summary>A symbol was present and every accessor still returned nothing.</summary>
    public static Level2RuleReport NothingReturned { get; } =
        new(string.Empty, string.Empty, string.Empty, string.Empty, SymbolSeen: true);

    /// <summary>Whether any accessor returned anything at all.</summary>
    public bool AnyRead =>
        this.IsAggregatedAllowed.Length > 0 || this.IsAggregatedValue.Length > 0
        || this.HasImpliedSizeAllowed.Length > 0 || this.HasImpliedSizeValue.Length > 0;

    /// <summary>
    /// The record for orbix-startup.log.
    ///
    /// Carries the caveat with the data, every time. A reader who finds these values in a log
    /// six months from now must not be able to read them as a verdict, because they are not
    /// one until the same rules have been read on a feed of each kind.
    /// </summary>
    public string LogText()
    {
        var builder = new StringBuilder("level-2 rules: ");

        if (!this.AnyRead)
        {
            return builder
                .Append(this.SymbolSeen
                    ? "a symbol was present and all four accessors returned nothing"
                    : "no symbol was available to ask about")
                .ToString();
        }

        return builder
            .Append(IsAggregatedRuleName).Append(" allowed=").Append(Show(this.IsAggregatedAllowed))
            .Append(" value=").Append(Show(this.IsAggregatedValue))
            .Append("; ").Append(HasImpliedSizeRuleName).Append(" allowed=")
            .Append(Show(this.HasImpliedSizeAllowed))
            .Append(" value=").Append(Show(this.HasImpliedSizeValue))
            .ToString();
    }

    private static string Show(string value) => value.Length == 0 ? "unread" : value;
}
