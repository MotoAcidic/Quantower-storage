using System;
using System.Collections.Generic;
using System.Text;

namespace OrbIx.Core.Features;

/// <summary>
/// Which of the platform's gates decided that the vendor's precomputed volume analysis
/// could not be used for this request.
///
/// The order mirrors the platform's own predicate as decompiled from
/// TradingPlatform.BusinessLayer v1.146.18: it returns on the FIRST gate that fails, so a
/// later gate failing tells us nothing while an earlier one already has.
///
/// THE ORDERING IS UNVERIFIED ON v1.147.2. Every gate INPUT was re-confirmed on that
/// assembly, but the method that sequences them was not re-located, so the short-circuit
/// claim is carried forward rather than re-established (docs/RECONCILIATION.md). This
/// enum is a REPORT of which observable gate failed, never an inference from one to
/// another, so a wrong order would mislabel a cause and could not fabricate one.
///
/// AND THE SET IS NOT EXHAUSTIVE. `VolumeAnalysisManager` applies a LICENCE check of its
/// own before any of these — on repeated failure it sends the calculation straight to
/// Finished with no data, having first clamped the request to the last seven days. That
/// cause is NOT modelled here, so it currently surfaces as "no observable gate explains
/// the missing levels".
/// </summary>
public enum VolumeAnalysisGate
{
    /// <summary>Every gate we can observe passed.</summary>
    None,

    /// <summary>The symbol published no volume-analysis metadata at all.</summary>
    NoMetadata,

    /// <summary>
    /// The metadata says volume analysis is unavailable. Read by reflection because the
    /// property is internal — see <see cref="VolumeAnalysisCapability.AvailabilityUnread"/>.
    /// </summary>
    NotAvailable,

    /// <summary>The symbol does not report plain volume, so the analysis cannot apply.</summary>
    WrongVolumeType,

    /// <summary>
    /// The delta calculation the request asks for is not the one the symbol uses for
    /// volume analysis. The parameterless call hardcodes AggressorFlag, so a symbol that
    /// wants anything else diverts here on every request.
    /// </summary>
    DeltaCalculationMismatch,

    /// <summary>The request set a volume filter, which the vendor path refuses.</summary>
    VolumeFilterSet,

    /// <summary>The request asked to compute from ticks, so the vendor path is skipped by choice.</summary>
    ForcedTickData,

    /// <summary>
    /// No period the vendor allows carries PER-PRICE levels. Levels are a separate vendor
    /// capability from volume analysis, with their own period list, so this can fail while
    /// bar totals still arrive — which is exactly the shape of what we observed.
    /// </summary>
    NoPeriodAllowsLevels,
}

/// <summary>
/// What the platform declared about volume analysis for one symbol, and which gate — if
/// any — explains a profile that found bars but no per-price levels.
///
/// WHY THIS TYPE EXISTS. Wave 2a established that in-range bars carry no
/// <c>PriceLevels</c> while their <c>Total</c> is populated. It could not say why, and the
/// decompiled predicate offers six independent reasons. Guessing between them would put an
/// invented cause behind a real measurement, so this records the observations instead and
/// names the first gate that fails.
///
/// §11: no reference to the trading platform, so every verdict below is unit-testable.
/// </summary>
/// <param name="MetadataPresent">Whether the symbol published metadata at all.</param>
/// <param name="AvailabilityUnread">
/// True when <c>IsVolumeAnalysisAvailable</c> could not be read. The property is internal
/// to the platform assembly, so a script reaches it only by reflection; when that fails the
/// answer is reported as unknown and never assumed either way.
/// </param>
/// <param name="Available">Meaningful only when <paramref name="AvailabilityUnread"/> is false.</param>
/// <param name="VolumeTypeIsVolume">Whether the symbol reports plain volume.</param>
/// <param name="RequestedDelta">The delta calculation the request will send.</param>
/// <param name="SymbolDelta">The delta calculation the symbol uses for volume analysis.</param>
/// <param name="VolumeFilterSet">Whether a volume filter is set on the request.</param>
/// <param name="ForceTickData">Whether the request asks to compute from ticks.</param>
/// <param name="PeriodsWithLevels">Vendor periods that carry per-price levels.</param>
/// <param name="PeriodsWithoutLevels">Vendor periods that carry totals only.</param>
/// <param name="ChartPeriod">The chart's own period, for display.</param>
/// <param name="ChartPeriodAllowsLevels">
/// Whether the chart's period is one the vendor serves PER-PRICE LEVELS for. Supplied by
/// the caller, which matches by duration rather than by name: the platform's period names
/// and our bar-derived TimeSpan are different representations of the same thing, and
/// comparing their spellings would report a mismatch that does not exist.
/// </param>
/// <param name="ChartPeriodAllowedWithoutLevels">As above, for bar totals.</param>
/// <param name="TickHistoryRule">
/// What the platform's own `ALLOW_VOLUME_ANALYSIS_FROM_TICK_HISTORY` rule says for this
/// symbol, verbatim, or an empty string when it could not be read.
///
/// THIS IS A QUOTE, NOT A VERDICT. It is the only one of the platform's twenty-eight
/// published rules that bears on volume analysis, and it is reported as the platform
/// worded it. Whether it is the SAME rule the licence predicate consults is not
/// established — that call site passes obfuscated constants which were not decoded — so
/// this is evidence about the account's entitlement, never a reproduction of the
/// platform's decision.
/// </param>
public sealed record VolumeAnalysisCapability(
    bool MetadataPresent,
    bool AvailabilityUnread,
    bool Available,
    bool VolumeTypeIsVolume,
    string RequestedDelta,
    string SymbolDelta,
    bool VolumeFilterSet,
    bool ForceTickData,
    IReadOnlyList<string> PeriodsWithLevels,
    IReadOnlyList<string> PeriodsWithoutLevels,
    string ChartPeriod,
    bool ChartPeriodAllowsLevels,
    bool ChartPeriodAllowedWithoutLevels,
    string TickHistoryRule = "")
{
    /// <summary>
    /// The platform's published rule name for volume analysis over tick history.
    ///
    /// NAMED HERE RATHER THAN INLINE because it is a value from the vendor's own API
    /// (`Rule.ALLOW_VOLUME_ANALYSIS_FROM_TICK_HISTORY`, a public const on the installed
    /// v1.147.2 assembly) and a typo in it would silently query a rule that does not exist,
    /// which reads identically to a rule that is allowed.
    /// </summary>
    public const string TickHistoryRuleName = "ALLOW_VOLUME_ANALYSIS_FROM_TICK_HISTORY";

    /// <summary>Whether the rule could be read at all.</summary>
    public bool TickHistoryRuleRead => this.TickHistoryRule.Length > 0;

    /// <summary>
    /// Whether the rule was read AND says something other than allowed.
    ///
    /// Deliberately conservative: an unread rule is not a refusal, and a rule this code
    /// does not recognise is not a refusal either.
    /// </summary>
    public bool TickHistoryRuleRefuses =>
        this.TickHistoryRuleRead
        && !this.TickHistoryRule.StartsWith("Allowed", StringComparison.Ordinal);

    /// <summary>
    /// The FIRST gate that fails, in the platform's own order.
    ///
    /// Order matters and is not cosmetic: the predicate returns on the first failure, so
    /// reporting a later gate while an earlier one already failed would name a cause that
    /// was never reached.
    ///
    /// An unread availability flag is NOT treated as a failure — an unknown is not a fault,
    /// and calling it one would be the invention this type exists to avoid.
    /// </summary>
    public VolumeAnalysisGate FirstFailingGate()
    {
        if (!this.MetadataPresent)
            return VolumeAnalysisGate.NoMetadata;

        if (!this.AvailabilityUnread && !this.Available)
            return VolumeAnalysisGate.NotAvailable;

        if (!this.VolumeTypeIsVolume)
            return VolumeAnalysisGate.WrongVolumeType;

        if (!string.Equals(this.RequestedDelta, this.SymbolDelta, StringComparison.Ordinal))
            return VolumeAnalysisGate.DeltaCalculationMismatch;

        if (this.VolumeFilterSet)
            return VolumeAnalysisGate.VolumeFilterSet;

        if (this.ForceTickData)
            return VolumeAnalysisGate.ForcedTickData;

        if (this.PeriodsWithLevels.Count == 0 || !this.ChartPeriodAllowsLevels)
            return VolumeAnalysisGate.NoPeriodAllowsLevels;

        return VolumeAnalysisGate.None;
    }

    /// <summary>
    /// The platform's LICENCE check, which runs BEFORE every gate above and which this type
    /// deliberately does not claim to observe.
    ///
    /// WHAT IT DOES, decompiled from v1.147.2. `VolumeAnalysisManager` guards its own
    /// `CalculateProfile(HistoricalData, parameters)` with a predicate that consults
    /// `Core.Instance.Licences.GetLicenceRuleItem` and `Core.Instance.RulesManager.IsAllowed`.
    /// On refusal it increments a counter, and after TEN refusals it raises
    /// `OnLicenceCheckError` and returns false — whereupon the calculation is set to
    /// `Finished` and returned WITHOUT COMPUTING ANYTHING. Before that it silently clamps the
    /// request's `From` to seven days ago.
    ///
    /// So its failure looks exactly like the symptom this type was built to explain: volume
    /// analysis reports Finished and no bar carries data.
    ///
    /// WHY IT IS NOT A GATE VALUE. A member of <see cref="VolumeAnalysisGate"/> is a verdict,
    /// and a verdict must be observable. The four rule names the predicate passes to
    /// `IsAllowed` are obfuscated string constants with no plaintext equivalent anywhere in
    /// the assembly, and `Core.Instance` exists only inside the running platform, so neither
    /// static extraction nor an offline probe can recover them. Adding `LicenceRefused` to
    /// the enum would therefore mean returning it on faith — the precise invention this type
    /// exists to prevent. It is named as the leading CANDIDATE for an otherwise unexplained
    /// absence, and never as a finding.
    ///
    /// TO PROMOTE IT TO A REAL GATE: `RulesManager.IsAllowed(string ruleName, Symbol)` is
    /// public and `RulesManager.Defaults` is an internal `List&lt;Rule&gt;` reachable by the
    /// same reflection this type already uses for `IsVolumeAnalysisAvailable`. Logging those
    /// rule names once from inside a live indicator would identify the right one, after
    /// which this becomes observable and belongs in the enum.
    /// </summary>
    public const string LicenceCandidate =
        "no observable gate explains it; the platform ALSO applies a licence check this "
        + "script cannot read, which on refusal finishes the calculation with no data";

    /// <summary>
    /// Why a profile found bars but no per-price levels — the observable gate when one
    /// failed, otherwise the unobservable candidate.
    ///
    /// USE THIS RATHER THAN <see cref="ChartText"/> AT A SITE THAT ALREADY KNOWS LEVELS ARE
    /// MISSING. ChartText answers "did an observable gate fail", which is a different
    /// question and is empty on a chart whose profiles are working perfectly.
    /// </summary>
    public string ExplainMissingLevels()
    {
        var gate = this.ChartText();

        if (gate.Length != 0)
            return gate;

        // A READ RULE BEATS THE CANDIDATE, in either direction. If the platform says the
        // account may not run volume analysis from tick history, that is a measurement and
        // it belongs here instead of a hedge. If it says the account MAY, the licence
        // candidate is weakened and the reader should be told so rather than left with a
        // suggestion the evidence no longer supports.
        if (this.TickHistoryRuleRefuses)
        {
            return $"the platform's {TickHistoryRuleName} rule for this symbol says: "
                   + this.TickHistoryRule;
        }

        if (this.TickHistoryRuleRead)
        {
            return "no observable gate explains it, and the platform's "
                   + $"{TickHistoryRuleName} rule is {this.TickHistoryRule} for this symbol — "
                   + "so an entitlement refusal on THAT rule is not the explanation";
        }

        return LicenceCandidate;
    }

    /// <summary>
    /// Short wording naming the gate, for the problems line when a profile found bars but
    /// no levels. Empty when nothing observable failed — in which case the absence of
    /// levels is NOT explained by anything modelled here, and saying so is the honest
    /// answer rather than blaming the nearest candidate. See
    /// <see cref="ExplainMissingLevels"/> for the caller that already knows they are absent.
    /// </summary>
    public string ChartText() => this.FirstFailingGate() switch
    {
        VolumeAnalysisGate.NoMetadata => "symbol publishes no volume-analysis metadata",
        VolumeAnalysisGate.NotAvailable => "volume analysis unavailable for this symbol",
        VolumeAnalysisGate.WrongVolumeType => "symbol does not report plain volume",
        VolumeAnalysisGate.DeltaCalculationMismatch =>
            $"delta calculation mismatch (asking {this.RequestedDelta}, symbol uses {this.SymbolDelta})",
        VolumeAnalysisGate.VolumeFilterSet => "a volume filter is set on the request",
        VolumeAnalysisGate.ForcedTickData => "tick-data computation was forced",
        VolumeAnalysisGate.NoPeriodAllowsLevels => this.PeriodsWithLevels.Count == 0
            ? "vendor serves per-price levels for no period at all"
            : $"vendor serves no per-price levels at {this.ChartPeriod}",
        _ => string.Empty,
    };

    /// <summary>
    /// The full record for orbix-startup.log: every observation, so a reader can check the
    /// verdict rather than take it on trust.
    /// </summary>
    public string LogText()
    {
        var builder = new StringBuilder("volume-analysis capability: ");

        builder.Append("gate=").Append(this.FirstFailingGate());

        if (!this.MetadataPresent)
        {
            // The licence note rides this path too. It is a precondition of the CALCULATION,
            // not of the metadata, so a symbol that published nothing has still not had it
            // checked — and an early return that dropped the note would make that one line
            // the only one implying otherwise.
            return builder
                .Append("; symbol published no metadata")
                .Append("; licence=unobserved (precedes every gate above)")
                .ToString();
        }

        builder
            .Append("; available=")
            .Append(this.AvailabilityUnread ? "unread (internal property)" : this.Available.ToString())
            .Append("; volumeType=")
            .Append(this.VolumeTypeIsVolume ? "Volume" : "other")
            .Append("; delta requested=").Append(this.RequestedDelta)
            .Append(" symbol=").Append(this.SymbolDelta)
            .Append("; filterSet=").Append(this.VolumeFilterSet)
            .Append("; forceTickData=").Append(this.ForceTickData)
            .Append("; chartPeriod=").Append(this.ChartPeriod)
            .Append(" (levels=").Append(this.ChartPeriodAllowsLevels)
            .Append(", totals=").Append(this.ChartPeriodAllowedWithoutLevels).Append(')')
            .Append("; periodsWithLevels=").Append(Join(this.PeriodsWithLevels))
            .Append("; periodsWithoutLevels=").Append(Join(this.PeriodsWithoutLevels))
            // RECORDED ON EVERY LINE, not only on failure. A reader comparing two logs must
            // be able to see that a precondition exists which neither line measured;
            // mentioning it only when something else went wrong would imply it was checked
            // the rest of the time.
            .Append("; licence=unobserved (precedes every gate above)")
            .Append("; ").Append(TickHistoryRuleName).Append('=')
            .Append(this.TickHistoryRuleRead ? this.TickHistoryRule : "unread");

        return builder.ToString();
    }

    private static string Join(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return "none";

        return string.Join(",", values);
    }
}
