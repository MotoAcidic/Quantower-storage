using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;

using OrbIx.Core.Features;

using OrbIx.Core.Playbooks;

namespace OrbIx.Core.Config;

/// <summary>
/// The bound form of the §12 configuration document. Every threshold the engine applies
/// is reachable from here; no module carries a literal that a walk-forward run might want
/// to change.
///
/// Instances are produced by <see cref="OrbIxConfigLoader"/> and are immutable thereafter.
/// </summary>
public sealed class OrbIxConfig
{
    public required EngineMode Mode { get; init; }
    public required L3Source L3Source { get; init; }
    public required LadderSplitMode LadderSplit { get; init; }
    public required TrailMode TrailMode { get; init; }

    /// <summary>
    /// IANA identifier for the exchange's own clock. Session boundaries are stored here and
    /// converted at use, never stored as UTC offsets, because an offset is wrong on one
    /// side of every daylight-saving transition.
    /// </summary>
    public required string ExchangeTimeZone { get; init; }

    /// <summary>
    /// IANA identifier the session open times in <see cref="Sessions"/> are written in.
    /// The specification's session table quotes them in US Eastern wall time.
    /// </summary>
    public required string SessionTimeZone { get; init; }

    /// <summary>
    /// When the market is open across the week. Without this the session clock built a
    /// window for every calendar date, including Saturdays — see <c>TradingWeek</c> for the
    /// measurement the default values come from.
    /// </summary>
    public required TradingWeekConfig TradingWeek { get; init; }

    public required IReadOnlyDictionary<string, SymbolConfig> Symbols { get; init; }

    /// <summary>
    /// Finds the product family a traded contract belongs to.
    ///
    /// Configuration is keyed by product FAMILY — <c>NQ</c>, <c>ES</c>, <c>GC</c> — while a
    /// platform reports the contract's own root, which for a micro is <c>MNQ</c>, <c>MES</c>
    /// or <c>MGC</c>. The two are not the same string and a direct lookup fails on every
    /// micro contract.
    ///
    /// The mapping is not invented here: each family already declares its members in
    /// <see cref="SymbolConfig.Tiers"/>, so this reads the configuration's own statement of
    /// which contracts belong to it. Adding a tier to the configuration is therefore all that
    /// is needed to trade it — no code changes, per §12.
    ///
    /// A direct key match wins, so a family root passed straight in still resolves.
    /// </summary>
    /// <param name="contractRoot">The root the platform reports, e.g. <c>MNQ</c>.</param>
    /// <param name="familyRoot">The configured family key, e.g. <c>NQ</c>.</param>
    /// <param name="symbolConfig">That family's configuration.</param>
    public bool TryResolveProduct(
        string contractRoot,
        [NotNullWhen(true)] out string? familyRoot,
        [NotNullWhen(true)] out SymbolConfig? symbolConfig)
    {
        familyRoot = null;
        symbolConfig = null;

        if (string.IsNullOrWhiteSpace(contractRoot))
            return false;

        var root = contractRoot.Trim();

        foreach (var (key, candidate) in this.Symbols)
        {
            if (string.Equals(key, root, StringComparison.OrdinalIgnoreCase))
            {
                familyRoot = key;
                symbolConfig = candidate;
                return true;
            }
        }

        foreach (var (key, candidate) in this.Symbols)
        {
            foreach (var tier in candidate.Tiers)
            {
                if (!string.Equals(tier, root, StringComparison.OrdinalIgnoreCase))
                    continue;

                familyRoot = key;
                symbolConfig = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every configured product with the contracts it covers, for an error message that shows
    /// what would need adding rather than only what is missing.
    /// </summary>
    public string DescribeProducts()
        => string.Join(
            "; ",
            this.Symbols.Select(pair => $"{pair.Key} [{string.Join(", ", pair.Value.Tiers)}]"));

    public required SessionsConfig Sessions { get; init; }
    public required OpeningRangeConfig Or { get; init; }
    public required ScoringConfig Scoring { get; init; }
    public required IReadOnlyDictionary<string, PlaybookConfig> Playbooks { get; init; }
    public required RiskConfig Risk { get; init; }
    public required IReadOnlyList<AccountConfig> Accounts { get; init; }
    public required TargetsConfig Targets { get; init; }
    public required TrailConfig Trail { get; init; }
    public required DataConfig Data { get; init; }
    public required KillConfig Kill { get; init; }
    public required StopsConfig Stops { get; init; }
    public required RetestConfig Retest { get; init; }
    public required LevelsConfig Levels { get; init; }
    public required MicroQualityConfig MicroQuality { get; init; }
    public required ImbalanceConfig Imbalance { get; init; }
    public required AbsorptionConfig Absorption { get; init; }
    public required ValidationConfig Validation { get; init; }
    public required RecorderConfig Recorder { get; init; }

    /// <summary>
    /// Settings for the twelve display tools absorbed from Aramid Flow.
    ///
    /// REQUIRED, NOT OPTIONAL, AND THAT IS SAFE HERE RATHER THAN STRICT FOR ITS OWN SAKE. The
    /// repository carries exactly one configuration document, the deploy copies it to both hosts
    /// alongside the assembly in the same step and verifies it byte for byte, and every test builds
    /// its fixtures by mutating that same file. So there is no reader that can encounter a document
    /// without this block — and making it optional would mean inventing display thresholds for a
    /// document that omitted it, which is the one thing this loader does not do.
    /// </summary>
    public required FlowConfig Flow { get; init; }
}

/// <summary>
/// Diagonal footprint imbalance: how it is measured, and whether it gates entries.
///
/// EVERY THRESHOLD HERE IS UNVERIFIED ON THIS STACK. The ratio and the minimum match the
/// Python reading in <c>phase1/orderflow_features.py</c> so the two agree on the same tape;
/// neither has been shown to predict anything here. They are configuration rather than
/// constants precisely because the answer is expected to come from the journal.
/// </summary>
/// <summary>
/// Absorption at the touch: did resting size HOLD while volume traded through it.
///
/// THIS SIGNAL IS A MEASURED NULL ON MNQ AND THE GATE SHIPS ENFORCING ANYWAY, by explicit
/// decision. Trial 008: 25,745 episodes, +1.006 ticks versus matched-random at p 0.0260 —
/// failing Bonferroni at p&lt;0.0100 and below the 2.76-tick cost floor even at face value.
/// The verdict and its counterfactual are journalled on every signal so that decision can
/// be re-examined against outcomes rather than re-argued.
/// </summary>
public sealed class AbsorptionConfig
{
    /// <summary>
    /// The span absorption is asked about. Matches
    /// <c>phase1/microstructure.py::ABSORPTION_WINDOW_US</c>, which records that NO OFFICIAL
    /// SOURCE DEFINES IT — Quantower documents Delta as traded volume and says nothing about
    /// absorption. It is a stated choice, changeable here, not an industry standard.
    /// </summary>
    public required double WindowSeconds { get; init; }

    /// <summary>Ratio below which size left faster than it traded: orders pulled, not filled.</summary>
    public required double CancelledBelow { get; init; }

    /// <summary>Ratio at or above which the level was refilled as fast as it was hit.</summary>
    public required double AbsorbedAtOrAbove { get; init; }

    /// <summary>
    /// Whether the gate may veto an entry, or is recorded only. Either way the verdict AND
    /// its counterfactual are journalled.
    /// </summary>
    public required bool GateEntries { get; init; }

    /// <summary>Prices retained per side of the reconstructed book.</summary>
    public required int MaxPricesPerSide { get; init; }
}

public sealed class ImbalanceConfig
{
    /// <summary>
    /// Multiple of the opposing side a diagonal must reach to count as imbalanced. Matches
    /// <c>FOOTPRINT_RATIO</c>.
    /// </summary>
    public required double Ratio { get; init; }

    /// <summary>
    /// Volume a diagonal must carry across both its cells before it is judged at all. Below
    /// it the diagonal is unjudged, never balanced. Matches <c>FOOTPRINT_MIN_PRINTS</c>.
    /// </summary>
    public required double MinVolume { get; init; }

    /// <summary>Consecutive imbalanced ticks required before a run counts as stacked.</summary>
    public required int MinRun { get; init; }

    /// <summary>
    /// Whether the gate may veto an entry, or is recorded only.
    ///
    /// Either way the verdict AND its counterfactual are journalled, so turning this off
    /// does not stop the evidence accumulating — it only stops it acting.
    /// </summary>
    public required bool GateEntries { get; init; }

    /// <summary>Closed bars whose footprints are retained for display.</summary>
    public required int FootprintHistory { get; init; }
}

/// <summary>
/// §7's stop geometry. The stop is the widest candidate plus a buffer, then capped — and
/// never shrunk to fit a size.
/// </summary>
public sealed class StopsConfig
{
    /// <summary>Multiple of average true range for the volatility candidate.</summary>
    public required double AtrMultiple { get; init; }

    /// <summary>Bars of average true range.</summary>
    public required int AtrPeriod { get; init; }

    /// <summary>Floor on the buffer, in ticks.</summary>
    public required int BufferMinTicks { get; init; }

    /// <summary>Multiple of the prevailing spread the buffer must also clear.</summary>
    public required double BufferSpreadMultiple { get; init; }

    /// <summary>Fraction of the opening range the buffer must also clear.</summary>
    public required double BufferOrRangeFraction { get; init; }

    /// <summary>Floor on the structural candidate, as a fraction of the opening range.</summary>
    public required double StructuralFloorOrFraction { get; init; }

    /// <summary>
    /// How far past a round number or level cluster a stop is nudged rather than resting on
    /// it, since the obvious stop price is the most heavily hunted one.
    /// </summary>
    public required int NudgePastLevelTicks { get; init; }
}

/// <summary>
/// M11's geometry. What counts as a touch of a broken level, what counts as the level
/// holding, and what counts as the reclaim having failed.
/// </summary>
public sealed class RetestConfig
{
    /// <summary>Distance from the broken edge that counts as touching it.</summary>
    public required int TouchToleranceTicks { get; init; }

    /// <summary>Movement back in the break direction that confirms the level held.</summary>
    public required int HoldConfirmTicks { get; init; }

    /// <summary>
    /// Travel back through the level beyond which the reclaim has failed. Larger than the
    /// touch tolerance so a wick through the level is not mistaken for a failure.
    /// </summary>
    public required int FailureBeyondTicks { get; init; }

    /// <summary>
    /// After this long, a return to the level is a fresh approach rather than a retest of
    /// that break.
    /// </summary>
    public required int MaxSecondsToRetest { get; init; }
}

/// <summary>
/// M03's weighting. Strength is what distinguishes a level price reacts to from one that
/// merely exists, and it decays with age.
/// </summary>
/// <summary>
/// Which sessions bound the overnight range.
///
/// CONFIGURED RATHER THAN INFERRED. "The electronic session before the day session" is a
/// judgement, not a derivation: 18:00 to 09:30 is conventional for index futures and gold's day
/// begins at the COMEX open instead. A rule that guessed "the first session after some hour"
/// would be an invented boundary dressed as a calculation.
/// </summary>
public sealed class OvernightConfig
{
    /// <summary>Session whose open begins the overnight, on the PREVIOUS local date.</summary>
    public required string StartSession { get; init; }

    /// <summary>Session whose open ends it, on the session date.</summary>
    public required string EndSession { get; init; }

    /// <summary>
    /// Per contract root overrides for the end. Gold's day begins at the COMEX open, so GC ends
    /// there rather than at the equity open.
    /// </summary>
    public required IReadOnlyDictionary<string, string> EndSessionByRoot { get; init; }
}

public sealed class LevelsConfig
{
    /// <summary>Base strength per level kind, before age decay.</summary>
    public required IReadOnlyDictionary<string, double> KindStrength { get; init; }

    /// <summary>Which sessions bound the overnight range.</summary>
    public required OvernightConfig Overnight { get; init; }

    /// <summary>
    /// Which session's opening range IS the initial balance.
    ///
    /// NAMED HERE RATHER THAN HARD-CODED IN THE ENGINE. The initial balance is a session like
    /// any other in this configuration, and a literal "IB" in Core would keep matching the old
    /// name after the session was renamed — producing no initial-balance levels at all, with
    /// nothing said. A name that matches no configured session is REFUSED at load.
    ///
    /// EMPTY DISABLES IT, and that is a supported setting rather than an oversight: with no
    /// name, the initial balance contributes OpeningRangeHigh/Low exactly as every other
    /// session does, which is the behaviour that shipped before these kinds were produced.
    /// </summary>
    public required string InitialBalanceSession { get; init; }

    /// <summary>Hours after which a level is worth half what it was when established.</summary>
    public required double AgeHalfLifeHours { get; init; }

    /// <summary>Levels closer than this merge into one band.</summary>
    public required int ClusterToleranceTicks { get; init; }

    /// <summary>Maximum clusters drawn or considered. The strongest survive.</summary>
    public required int MaxVisible { get; init; }

    /// <summary>
    /// How many completed opening ranges are retained and drawn at once. The oldest is
    /// evicted first, so the newest sessions are always present.
    /// </summary>
    public required int MaxRetainedSessions { get; init; }
}

/// <summary>
/// §13's evidence bar for unattended running. Bound rather than hard-coded so a
/// walk-forward run can raise it without a rebuild.
/// </summary>
public sealed class ValidationConfig
{
    /// <summary>
    /// Out-of-sample trades a playbook must have accumulated, per session, before Auto
    /// mode is permitted for it (V4).
    /// </summary>
    public required int MinOutOfSampleTrades { get; init; }

    /// <summary>
    /// Sessions the system must have run in Armed mode on a live evaluation account
    /// before Auto is permitted (V6).
    /// </summary>
    public required int MinArmedForwardSessions { get; init; }

    /// <summary>
    /// File recording what has actually been validated. Resolved relative to the
    /// configuration file. Its absence means nothing has been attested, which is a
    /// truthful answer rather than a missing one.
    /// </summary>
    public required string AttestationFile { get; init; }
}

/// <summary>Per-product settings. Keyed by the product root, not by a dated contract.</summary>
public sealed class SymbolConfig
{
    public required string Profile { get; init; }

    /// <summary>
    /// Contract tiers available for this product, coarsest first. Membership here is a
    /// permission, not an assertion that the tier is tradeable — the engine only uses a
    /// tier the platform actually resolves to a symbol, which is how a newly listed
    /// contract enters the ladder without a code change.
    /// </summary>
    public required IReadOnlyList<string> Tiers { get; init; }

    /// <summary>Hard cap on stop distance. A wider structural stop refuses the trade.</summary>
    public required int MaxRiskTicks { get; init; }

    public required string EntryTf { get; init; }
    public required int MaxMinis { get; init; }

    /// <summary>
    /// Minimum resting size at the touch before an entry is permitted. Per product because
    /// a depth that is normal for gold would be a liquidity hole in the S&amp;P.
    /// </summary>
    public required int MinTouchDepth { get; init; }

    /// <summary>
    /// Price interval treated as a round number for this product. Per product because a
    /// round number on gold is not a round number on the Nasdaq.
    /// </summary>
    public required double RoundNumberStep { get; init; }

    /// <summary>
    /// Multiplier applied to depth-derived scores. Gold's book is materially thinner than
    /// the index products, so its depth evidence is worth less.
    /// </summary>
    public double DomWeightScale { get; init; } = 1.0;
}

/// <summary>The eight specified session windows plus any operator-defined ones.</summary>
public sealed class SessionsConfig
{
    public required IReadOnlyDictionary<string, SessionConfig> Named { get; init; }
    public required IReadOnlyList<CustomSessionConfig> Custom { get; init; }
}

public class SessionConfig
{
    /// <summary>Wall-clock open in <see cref="OrbIxConfig.SessionTimeZone"/>, "HH:mm".</summary>
    public required string Open { get; init; }

    /// <summary>Opening-range length: a duration such as "15m", or "adaptive".</summary>
    public required string Or { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>Maximum entries this session may produce. Absent means no budget applies.</summary>
    public int? Budget { get; init; }

    /// <summary>
    /// Whether entries are permitted at all. The initial-balance window supplies extension
    /// targets and a day-type read without ever being traded directly.
    /// </summary>
    public bool EntriesAllowed { get; init; } = true;

    /// <summary>
    /// Product roots this session applies to. Empty means all configured products — the
    /// COMEX pit open is a gold session and has no meaning for the index products.
    /// </summary>
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
}

public sealed class CustomSessionConfig : SessionConfig
{
    public required string Name { get; init; }
}

public sealed class OpeningRangeConfig
{
    public required AdaptiveOrConfig Adaptive { get; init; }
    public required OrGradeConfig Grades { get; init; }
    public required int AdrPeriod { get; init; }

    /// <summary>
    /// Projection multiples of the range width, ascending. §7 chooses targets from these,
    /// so they are tunable parameters rather than constants of the method.
    /// </summary>
    public required IReadOnlyList<double> ExtensionMultiples { get; init; }

    /// <summary>Range lengths offered to the operator, including "adaptive".</summary>
    public required IReadOnlyList<string> Lengths { get; init; }
}

/// <summary>
/// The volume-clock opening range. A fixed wall-clock window assumes participation is
/// uniform in time, which it is not on a release morning.
/// </summary>
public sealed class AdaptiveOrConfig
{
    public required bool Enabled { get; init; }

    /// <summary>Multiple of the median opening volume at which the range may close.</summary>
    public required double Kappa { get; init; }

    /// <summary>
    /// Floor on range duration. Without it a single sweep print at the open could define
    /// the whole range.
    /// </summary>
    public required int MinSec { get; init; }

    /// <summary>Ceiling on range duration, in minutes.</summary>
    public required int MaxMin { get; init; }

    /// <summary>
    /// Range width, as a fraction of average daily range, beyond which the range stops
    /// extending because the day's movement is already spent.
    /// </summary>
    public required double MaxWidthAdrPct { get; init; }

    /// <summary>Sessions of history used for the median opening-volume comparison.</summary>
    public required int OpenVolLookbackDays { get; init; }
}

public sealed class OrGradeConfig
{
    public required double CompressedMaxOrw { get; init; }
    public required double ExhaustedMinOrw { get; init; }
}

public sealed class ScoringConfig
{
    public required GradeThresholds GradeThresholds { get; init; }
    public required ScoreWeightsConfig Weights { get; init; }

    /// <summary>
    /// When a data tier is absent, redistribute its groups' weight proportionally across
    /// the surviving groups so the score stays on a 0-100 scale and the grade thresholds
    /// never need retuning per environment.
    /// </summary>
    public required bool RedistributeOnMissingTier { get; init; }
}

public sealed class GradeThresholds
{
    public required double APlus { get; init; }
    public required double A { get; init; }
    public required double B { get; init; }
}

public sealed class ScoreWeightsConfig
{
    public required double Structure { get; init; }
    public required double OrderFlow { get; init; }
    public required double Book { get; init; }
    public required double Micro { get; init; }
    public required double Positioning { get; init; }
}

public sealed class PlaybookConfig
{
    public required bool On { get; init; }
    public required double SizeMult { get; init; }

    /// <summary>Ticks beyond the range edge required before a break is considered real.</summary>
    public int? BreakBufferTicks { get; init; }

    /// <summary>
    /// How far back into the range a retest may travel and still be tradeable, as a
    /// fraction of the range.
    /// </summary>
    public double? MaxRetestDepthPct { get; init; }

    /// <summary>
    /// What this playbook has been MEASURED to do, and whether that measurement has been
    /// through the provenance ledger.
    ///
    /// OPTIONAL, AND ITS ABSENCE IS THE HONEST DEFAULT. A playbook with no entry here is drawn
    /// as having no measured record, which is a true statement about a new playbook and a true
    /// statement about one whose study was never registered. Inventing a neutral-looking
    /// placeholder would make those two indistinguishable from a measured result.
    ///
    /// It lives in configuration rather than beside the drawing code because the numbers change
    /// every time the study is re-run, and a figure compiled into a renderer is a figure that
    /// silently stops being true.
    /// </summary>
    public PlaybookProvenance? Provenance { get; init; }
}

public sealed class RiskConfig
{
    public required double RiskPct { get; init; }

    /// <summary>
    /// How many further losses the day's remaining loss allowance must survive. Dividing
    /// by this is what keeps a single trade from consuming the day.
    /// </summary>
    public required int LossesSurvivableDaily { get; init; }

    /// <summary>As above, against the trailing drawdown buffer.</summary>
    public required int LossesSurvivableDd { get; init; }

    public required double SessionRiskCapPct { get; init; }
    public required int CooldownMinAfterTwoLosses { get; init; }

    /// <summary>
    /// Correlation coefficients between product roots, keyed "ES_NQ". Open risk in one is
    /// counted against the other at this weight.
    /// </summary>
    public required IReadOnlyDictionary<string, double> CorrelationNetting { get; init; }
}

public sealed class AccountConfig
{
    public required string Id { get; init; }
    public required string Policy { get; init; }
    public double? Equity { get; init; }
    public double? DailyLimit { get; init; }
    public TrailingLimitConfig? TrailingLimit { get; init; }
    public double? ConsistencyTarget { get; init; }

    /// <summary>Wall-clock flatten time in <see cref="OrbIxConfig.SessionTimeZone"/>.</summary>
    public string? FlatByTime { get; init; }

    /// <summary>
    /// The news restriction this account carries. Typed rather than a free string, so an
    /// unrecognised value is a load-time fault instead of a setting that silently does
    /// nothing — which is what it was before this was consumed at all.
    /// </summary>
    public required NewsRule NewsRule { get; init; }
    public double? RiskPctOverride { get; init; }
}

public sealed class TrailingLimitConfig
{
    /// <summary>
    /// "intraday" tracks the drawdown line against peak unrealised equity; "eod" against
    /// closed balance at the session end. Treating an intraday rule as end-of-day is the
    /// single most common way an otherwise profitable system fails an evaluation.
    /// </summary>
    public required string Type { get; init; }

    public required double Amount { get; init; }
}

public sealed class TargetsConfig
{
    public required string Tp1 { get; init; }
    public required string Tp2 { get; init; }
    public required string Tp3 { get; init; }
    public required string Tp4 { get; init; }

    /// <summary>
    /// A computed target this close in front of a strong level is moved in front of it
    /// rather than left behind it.
    /// </summary>
    public required int SnapAheadTicks { get; init; }

    /// <summary>
    /// Band around one unit of risk in which a liquidity pocket may replace the arithmetic
    /// first target.
    /// </summary>
    public required double Tp1RMin { get; init; }

    /// <inheritdoc cref="Tp1RMin"/>
    public required double Tp1RMax { get; init; }

    /// <summary>
    /// How far the third target may be moved to land on a level cluster, as a fraction of
    /// the opening range.
    /// </summary>
    public required double Tp3SnapOrFraction { get; init; }

    /// <summary>Fraction of the position released at each of the four targets.</summary>
    public required IReadOnlyList<double> Allocation { get; init; }

    /// <summary>
    /// Explicit per-target quantities for small positions, keyed by position size. A size
    /// present here overrides proportional apportionment, because 40/30/20/10 of three
    /// contracts is not a plan — it is a rounding argument.
    /// </summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<int>> SmallSizeLadder { get; init; }
}

public sealed class TrailConfig
{
    public required TrailGeometry Mode { get; init; }
    public required IReadOnlyDictionary<string, int> BeTicksAfterTp1 { get; init; }
    public required IReadOnlyDictionary<string, int> AfterTp2Ticks { get; init; }
    public required IReadOnlyDictionary<string, int> AfterTp2TicksTightened { get; init; }
    public required IReadOnlyDictionary<string, int> AfterTp3Ticks { get; init; }
    public required IReadOnlyDictionary<string, int> StructureSwingBufferTicks { get; init; }

    /// <summary>
    /// "barClose" or "tick". Re-evaluating on tick trails the position out on a single
    /// spike print, which is why bar close is the default.
    /// </summary>
    public required string EvaluateOn { get; init; }

    /// <summary>
    /// Whether order-flow deterioration may cut the runner independently of the trail.
    /// </summary>
    public required bool FlowOverride { get; init; }
}

public sealed class DataConfig
{
    /// <summary>Minimum tier the engine requires before it will arm at all.</summary>
    public required DataTier RequireTier { get; init; }

    public required L3Config L3 { get; init; }
    public required CalendarConfig Calendar { get; init; }
}

/// <summary>
/// The trading week as wall-clock times in <see cref="OrbIxConfig.SessionTimeZone"/>.
///
/// Configuration rather than constants because the values were derived from 25 days of
/// captured trades, all inside daylight saving. If the winter boundary turns out to differ,
/// it is corrected here rather than in a rebuild.
/// </summary>
public sealed class TradingWeekConfig
{
    /// <summary>Day the week opens, e.g. "Sunday".</summary>
    public required string OpenDay { get; init; }

    /// <summary>Wall-clock open on that day, "HH:mm".</summary>
    public required string OpenTime { get; init; }

    /// <summary>Day the week closes, e.g. "Friday".</summary>
    public required string CloseDay { get; init; }

    /// <summary>Wall-clock close on that day, "HH:mm".</summary>
    public required string CloseTime { get; init; }

    /// <summary>The parsed open day. Validation guarantees this parses.</summary>
    public DayOfWeek OpenDayOfWeek => ParseDay(this.OpenDay, nameof(this.OpenDay));

    /// <summary>The parsed close day. Validation guarantees this parses.</summary>
    public DayOfWeek CloseDayOfWeek => ParseDay(this.CloseDay, nameof(this.CloseDay));

    /// <summary>The parsed open time. Validation guarantees this parses.</summary>
    public TimeSpan OpenLocalTime => ParseTime(this.OpenTime, nameof(this.OpenTime));

    /// <summary>The parsed close time. Validation guarantees this parses.</summary>
    public TimeSpan CloseLocalTime => ParseTime(this.CloseTime, nameof(this.CloseTime));

    private static DayOfWeek ParseDay(string value, string field)
        => Enum.TryParse<DayOfWeek>(value, ignoreCase: true, out var day)
            ? day
            : throw new InvalidOperationException(
                $"tradingWeek.{field}: '{value}' is not a day of the week. Configuration "
                + "validation should have rejected this before it reached the clock.");

    private static TimeSpan ParseTime(string value, string field)
        => OrbIxConfigLoader.TryParseWallClock(value, out var time)
            ? time
            : throw new InvalidOperationException(
                $"tradingWeek.{field}: '{value}' is not an HH:mm wall-clock time. "
                + "Configuration validation should have rejected this before it reached the clock.");
}

public sealed class L3Config
{
    public required string Source { get; init; }
    public required string Pipe { get; init; }
}

public sealed class CalendarConfig
{
    public required string Source { get; init; }

    /// <summary>Calendar file location, resolved relative to the configuration file.</summary>
    public required string Path { get; init; }

    public required int BlockMinBefore { get; init; }
    public required int BlockMinAfter { get; init; }

    /// <summary>
    /// Behaviour when no calendar can be read. "block" refuses entries; "warn" permits
    /// them and surfaces the gap. An absent calendar means "no events known", which is not
    /// the same as "no events".
    /// </summary>
    public required CalendarUnavailablePolicy WhenUnavailable { get; init; }

    /// <summary>
    /// How each source event type maps to an expected disturbance.
    ///
    /// The feed supplies no impact rating of its own — Unusual Whales' published
    /// specification lists only event, time, type, prev, forecast and reported period — so the
    /// classification is a decision, and a decision belongs in configuration where it can be
    /// read and changed without a rebuild.
    ///
    /// Keys are matched case-insensitively: the vendor's specification declares the enum as
    /// <c>fomc</c> while the live API returns <c>FOMC</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, EventImpact> ImpactByType { get; init; }

    /// <summary>
    /// Impact for an event type not listed in <see cref="ImpactByType"/>.
    ///
    /// A type the feed adds later is unrecognised, not harmless. The costly failure is
    /// trading through an unclassified release, not pausing for one, which is why the shipped
    /// default is the blocking one.
    /// </summary>
    public required EventImpact ImpactDefault { get; init; }

    /// <summary>
    /// Which events the firm treats as Tier 1, and how wide their window is.
    /// </summary>
    public required Tier1Config Tier1 { get; init; }

    /// <summary>
    /// The impact for one source event type.
    /// </summary>
    public EventImpact ImpactFor(string? type)
        => !string.IsNullOrWhiteSpace(type) && this.ImpactByType.TryGetValue(type, out var impact)
            ? impact
            : this.ImpactDefault;
}

/// <summary>
/// The gatekeeper's thresholds. A breach of any one refuses a new entry regardless of the
/// confluence score, because a high score computed on a broken feed is a high-scoring
/// mistake.
/// </summary>
/// <summary>
/// Identifies the firm's Tier 1 events, which carry a stricter obligation than other releases.
///
/// My Funded Futures enumerates them by name rather than supplying a flag, and the data feed
/// carries no tier of its own, so the classification is a rule stated here. Published list,
/// verbatim:
///
/// > "For All Traders: FOMC Meetings, FOMC Minutes, Employment Report, CPI"
/// > "For Energy Traders: EIA — For Agricultural Traders: Agricultural Reports"
///
/// EIA and Agricultural Reports are product-specific to traders of those products and are not
/// listed in the shipped configuration, which covers index and metals futures.
/// </summary>
public sealed class Tier1Config
{
    /// <summary>
    /// Source event types that are Tier 1 outright, matched case-insensitively. The feed's
    /// own <c>fomc</c> type covers FOMC meetings and minutes without needing a name match.
    /// </summary>
    public required IReadOnlyList<string> Types { get; init; }

    /// <summary>
    /// Case-insensitive substrings that mark an event name as Tier 1.
    ///
    /// Precision matters more than it looks. "employment" would also match "ADP employment",
    /// a different release on a different day that is NOT on the firm's list, so the shipped
    /// pattern is "employment report".
    /// </summary>
    public required IReadOnlyList<string> NamePatterns { get; init; }

    /// <summary>Minutes before a Tier 1 event during which entries are refused.</summary>
    public required int BlockMinBefore { get; init; }

    /// <summary>Minutes after a Tier 1 event during which entries are refused.</summary>
    public required int BlockMinAfter { get; init; }

    /// <summary>
    /// Whether an event is Tier 1, by its source type or its name.
    /// </summary>
    public bool Matches(string? sourceType, string? name)
    {
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            foreach (var type in this.Types)
            {
                if (string.Equals(type, sourceType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var pattern in this.NamePatterns)
        {
            if (pattern.Length > 0 && name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

public sealed class MicroQualityConfig
{
    /// <summary>
    /// Multiple of the recent median spread above which the market is judged too wide.
    /// Relative rather than absolute so one threshold serves every product.
    /// </summary>
    public required double MaxSpreadMedianMultiple { get; init; }

    /// <summary>Spread observations retained for the median.</summary>
    public required int SpreadSampleWindow { get; init; }

    /// <summary>
    /// Observations required before the median is treated as one. Below this the gate
    /// reports insufficient evidence rather than passing something it has not measured.
    /// </summary>
    public required int MinSpreadSamples { get; init; }

    /// <summary>
    /// Quote age beyond which the top of book is stale for entry purposes. Distinct from
    /// <see cref="KillConfig.StaleQuoteMs"/>, which halts the engine outright.
    /// </summary>
    public required int MaxQuoteAgeMs { get; init; }
}

public sealed class KillConfig
{
    public required int OrderRatePerMin { get; init; }
    public required int SlippageDriftTicks { get; init; }
    public required int StaleQuoteMs { get; init; }
    public required int ExpectancyWindow { get; init; }
}

/// <summary>
/// Tick and book recording. This starts on day one of the build because §13's validation
/// replays recorded streams, and a book cannot be captured retroactively.
/// </summary>
public sealed class RecorderConfig
{
    public required bool Enabled { get; init; }

    /// <summary>
    /// Destination directory. Empty means "beside the loaded assembly", which is the only
    /// location a plugin can rely on being writable.
    /// </summary>
    public required string Directory { get; init; }

    public required bool RecordTrades { get; init; }
    public required bool RecordBook { get; init; }
    public required int FlushIntervalMs { get; init; }
}
