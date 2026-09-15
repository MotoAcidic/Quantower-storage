using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrbIx.Core.Config;
using OrbIx.Core.Direction;
using OrbIx.Core.Features;
using OrbIx.Core.Execution;
using OrbIx.Core.Playbooks;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Telemetry;

/// <summary>What a journal record is about.</summary>
public enum JournalKind
{
    /// <summary>A setup that was scored and permitted.</summary>
    SignalAccepted,

    /// <summary>A setup that was scored and refused. As much of the dataset as an accepted one.</summary>
    SignalRejected,

    /// <summary>A session's opening range closing.</summary>
    RangeClosed,

    /// <summary>A position's outcome, joined to its signal by identifier.</summary>
    Outcome,

    /// <summary>A kill-switch or gate changing the engine's state.</summary>
    EngineState,

    /// <summary>
    /// The direction read, written once per closed bar.
    ///
    /// A KIND OF ITS OWN, AND NOT A FIELD ON EngineState. EngineState rows are
    /// throttled by EngineStateThrottle -- identical consecutive decisions collapse
    /// into one row standing for thousands -- so a direction read riding on them
    /// would be recorded only when the DECISION changed, silently missing every
    /// direction change that happened while the decision held. And a row per fold
    /// would be roughly fifty a second.
    ///
    /// One row per closed bar is the cadence that makes this a dataset: it is the
    /// rate the chart itself moves at, and it is what lets the read be tested later
    /// against what price did next.
    /// </summary>
    DirectionRead,

    /// <summary>
    /// An operator rule broken, written ONCE at the moment it breaks.
    ///
    /// UNTIL THIS EXISTED THERE WAS NO RECORD OF A SINGLE TRANSGRESSION. A breach was
    /// drawn as a Problem line, held by OperatorRuleLatch for the trading day, and gone at
    /// the next attach. On 2026-09-03 the account reached 36 contracts against a cap of 5
    /// and nothing anywhere retained that fact.
    ///
    /// It is also the only way to answer the question trial 031 registered and could not
    /// test: does SHOWING someone a rule change what they do? That needs a durable record
    /// of when each rule fired, which is this row.
    /// </summary>
    Breach,

    /// <summary>
    /// One execution on the OPERATOR's account, as it happened.
    ///
    /// A different subject from every kind above it: those record what the ENGINE decided,
    /// this records what the person did. The engine places no orders, so without this the
    /// journal describes a system that never traded while the account beside it did.
    /// </summary>
    Fill,

    /// <summary>
    /// What the day's fill history contained when a chart attached — a summary, not a replay.
    ///
    /// The journal appends, so replaying the seeded fills as individual rows would re-append
    /// the day on every reattach (four times over on one host in one evening, observed
    /// 2026-09-02). A dataset that is only correct if the reader remembers a dedup rule is a
    /// dataset that will be read wrong, so the history arrives as one summary row and only
    /// LIVE fills get a row each.
    ///
    /// The cost, stated rather than left to be discovered: fills that happened while no chart
    /// was attached are recorded only in aggregate.
    /// </summary>
    FillSeed,
}

/// <summary>
/// One line of the journal.
///
/// Flat and self-describing rather than nested, because the file is read by whatever is to
/// hand — a replay harness, a spreadsheet, a one-line grep at two in the morning — and a
/// record that needs a schema to interpret is a record nobody checks.
/// </summary>
public sealed record JournalEntry
{
    public required string SignalId { get; init; }
    public required JournalKind Kind { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string SessionName { get; init; }
    public required string SymbolRoot { get; init; }

    public string? PlaybookId { get; init; }
    public string? Direction { get; init; }
    public double? Score { get; init; }
    public string? Grade { get; init; }

    /// <summary>Per-module points, so a score can be re-derived rather than trusted.</summary>
    public IReadOnlyDictionary<string, double>? Contributions { get; init; }

    /// <summary>
    /// Data tiers that were dark, whose weight was redistributed. Without this a score of
    /// eighty in a degraded environment is indistinguishable from eighty in a complete one.
    /// </summary>
    public IReadOnlyList<string>? DarkTiers { get; init; }

    /// <summary>Every veto that fired, or every blocker that refused the plan.</summary>
    public IReadOnlyList<string>? Blockers { get; init; }

    /// <summary>
    /// Confirmation clauses the data environment could not evaluate. Recorded so a later
    /// reader knows the confirmation set was partial and which parts were missing, rather
    /// than reading a passed check that was never run.
    /// </summary>
    public IReadOnlyList<string>? UnevaluatedClauses { get; init; }

    // ---- imbalance gate ------------------------------------------------------------------

    /// <summary>
    /// What the stacked-imbalance gate concluded: Pass, Block, or Unmeasured.
    ///
    /// Unmeasured is a THIRD value on purpose. A bar whose diagonals carried too little
    /// volume to judge produced no opinion, and a reader who saw only pass/fail would count
    /// it as one or the other and mis-weight the whole question.
    /// </summary>
    public string? ImbalanceGate { get; init; }

    /// <summary>Whether the gate was allowed to veto when this row was written.</summary>
    public bool? ImbalanceEnforced { get; init; }

    /// <summary>
    /// Whether the gate WOULD have blocked, independent of whether it was allowed to.
    ///
    /// THE COUNTERFACTUAL, AND THE REASON THESE FIELDS EXIST. Rows where this is true while
    /// <see cref="ImbalanceEnforced"/> is false are setups the gate would have refused and
    /// did not — join them to their outcomes and the question "does this gate help?" is
    /// answered from sessions that actually happened, rather than from a backtest over a
    /// tape that has produced eighteen null families and several lookaheads.
    /// </summary>
    public bool? ImbalanceWouldBlock { get; init; }

    /// <summary>Consecutive imbalanced ticks the direction needed.</summary>
    public int? ImbalanceRequiredRun { get; init; }

    /// <summary>Longest stacked run found in the trade's direction.</summary>
    public int? ImbalanceDirectionalRun { get; init; }

    /// <summary>Longest stacked run found against it.</summary>
    public int? ImbalanceOpposingRun { get; init; }

    /// <summary>
    /// Fraction of the break bar's diagonals that carried enough volume to judge. A run
    /// measured over a tenth of the bar is a weaker claim than the same run over all of it,
    /// and without this the two rows are indistinguishable.
    /// </summary>
    public double? ImbalanceCoverage { get; init; }

    // ---- absorption gate -----------------------------------------------------------------

    /// <summary>What the absorption gate concluded: Pass, Block, or Unmeasured.</summary>
    public string? AbsorptionGate { get; init; }

    /// <summary>Whether it was allowed to veto when this row was written.</summary>
    public bool? AbsorptionEnforced { get; init; }

    /// <summary>
    /// Whether it WOULD have blocked, independent of whether it was allowed to.
    ///
    /// SHARPER HERE THAN FOR IMBALANCE. Absorption is already a measured null on this
    /// instrument (trial 008, 25,745 episodes, below the cost floor), and the gate ships
    /// enforcing by decision rather than by evidence. These rows are the only route by which
    /// that decision can be re-examined against what the trades actually did.
    /// </summary>
    public bool? AbsorptionWouldBlock { get; init; }

    /// <summary>How the touch on the trade's own side read: Absorbed, Replenished, Consumed, Cancelled, Quiet.</summary>
    public string? AbsorptionState { get; init; }

    /// <summary>Price of the touch that was judged.</summary>
    public double? AbsorptionPrice { get; init; }

    /// <summary>Aggressive volume that traded at that price inside the window.</summary>
    public double? AbsorptionVolume { get; init; }

    /// <summary>Resting size lost over the window. Negative means the level GREW.</summary>
    public double? AbsorptionSizeReduction { get; init; }

    /// <summary>
    /// Volume over size lost, or ABSENT when the level did not shrink.
    ///
    /// Absent rather than infinite: an unbounded ratio is not writable as JSON, and the two
    /// components above carry everything it would have said.
    /// </summary>
    public double? AbsorptionRatio { get; init; }

    /// <summary>How the OTHER side's touch read, for context.</summary>
    public string? AbsorptionOpposingState { get; init; }

    /// <summary>The direction verdict: Up, Down, Mixed or Undecided.</summary>
    public string? DirectionVerdict { get; init; }

    /// <summary>How many directional votes agreed with the verdict.</summary>
    public int? DirectionAgreeing { get; init; }

    /// <summary>
    /// How many votes were CAST, which is not how many were offered: an input that
    /// is flat or cannot measure is shown on the panel and counts toward neither
    /// side. Recording both is what lets a later reader tell "four of six agreed"
    /// from "four of six could see at all".
    /// </summary>
    public int? DirectionCast { get; init; }

    /// <summary>How many votes were offered in total, directional or not.</summary>
    public int? DirectionOffered { get; init; }

    /// <summary>
    /// Every vote, as <c>name:State</c> pairs. Carried in full rather than
    /// summarised so a row can be re-derived instead of merely believed.
    /// </summary>
    public string? DirectionVotes { get; init; }

    public double? EntryPrice { get; init; }
    public double? StopPrice { get; init; }
    public int? RiskTicks { get; init; }
    public double? RiskUsd { get; init; }
    public int? Contracts { get; init; }
    public IReadOnlyList<int>? TargetTicks { get; init; }
    public string? Instruments { get; init; }

    public double? Orh { get; init; }
    public double? Orl { get; init; }
    public double? Orw { get; init; }
    public string? OrGrade { get; init; }
    public string? OrCloseReason { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// For an <see cref="JournalKind.EngineState"/> row: how many evaluations happened between
    /// the previous such row and this one, the suppressed ones included.
    ///
    /// A run of identical decisions is collapsed into one row rather than written out, and
    /// this is what keeps that lossless: the previous row's decision held for exactly this
    /// many further evaluations, because a change would have written a row of its own.
    /// </summary>
    public int? EvaluationsSincePrevious { get; init; }

    // ---- Fill and FillSeed ---------------------------------------------------------------

    /// <summary>
    /// The platform's own identifier for an execution, when it supplied one.
    ///
    /// NO UNIQUENESS IS CLAIMED. The vendor documents the trade type only as "Represents
    /// information about trade" and gives its identifier no documentation entry at all. A
    /// reader deduping these rows should key on the COMPOSITE — this, plus
    /// <see cref="TimestampUtc"/>, <see cref="PositionId"/>, <see cref="FillPrice"/> and
    /// <see cref="FillQuantity"/> — rather than on this field alone.
    /// </summary>
    public string? FillId { get; init; }

    /// <summary>Which position the fill belonged to. What separates a trade from a fill.</summary>
    public string? PositionId { get; init; }

    /// <summary>"Buy" or "Sell".</summary>
    public string? Side { get; init; }

    /// <summary>Contracts filled, unsigned; the side carries the direction.</summary>
    public double? FillQuantity { get; init; }

    public double? FillPrice { get; init; }

    /// <summary>
    /// Realised profit on this fill, net of fees, or absent when the platform did not supply
    /// it. Absent and zero are different: zero is a scratch, absent is unknown.
    /// </summary>
    public double? FillNetPnl { get; init; }

    /// <summary>"Opened", "Closed", or "Unknown" — the platform's own word.</summary>
    public string? FillImpact { get; init; }

    /// <summary>
    /// Signed net position after this fill, so the day's size can be re-derived from the file
    /// without replaying the arithmetic that produced it.
    /// </summary>
    public double? NetAfter { get; init; }

    /// <summary>How many fills the seed loaded. Zero is a measurement; absent is not.</summary>
    public int? SeededFills { get; init; }

    /// <summary>The trading day the seed covered, from the session clock.</summary>
    public DateTime? SeedFromUtc { get; init; }

    /// <summary>Largest position held during the seeded history, unsigned.</summary>
    public double? PeakContracts { get; init; }

    /// <summary>
    /// Realised profit across the seeded history, or absent when a closing fill arrived
    /// without a figure and the total could not be stated honestly.
    /// </summary>
    public double? RealisedToday { get; init; }
}

/// <summary>
/// Append-only journal of everything the engine decided.
///
/// Two properties matter more than the format.
///
/// Rejected setups are recorded. §6 requires every veto to fire a record with the full
/// score breakdown at the moment of rejection, because a dataset containing only the trades
/// that were taken cannot answer whether the gate was right to refuse the others — which is
/// exactly the question §13's validation asks.
///
/// It writes files and opens no port. The journal is pulled with the existing SSH trust
/// rather than served, which keeps the trading host's attack surface unchanged
/// (NIST CSF PR.IR-01).
/// </summary>
public sealed class TradeJournal : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,

        // Enums as names, not ordinals. This file exists to be read — by a replay harness,
        // by a spreadsheet, by a grep at two in the morning — and "Kind":1 tells a reader
        // nothing while silently changing meaning the moment a member is inserted above it.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object gate = new();
    private readonly TextWriter writer;
    private readonly bool ownsWriter;

    private long sequence;

    public TradeJournal(TextWriter writer, bool ownsWriter = false)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.ownsWriter = ownsWriter;
    }

    /// <summary>
    /// Opens a journal file, appending to it when one already exists.
    ///
    /// Appending rather than replacing: a restart mid-session must not discard the session's
    /// earlier decisions, and the accumulated record across restarts is the dataset §13
    /// reads.
    /// </summary>
    public static TradeJournal OpenFile(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.", nameof(fileName));

        Directory.CreateDirectory(directory);

        var stream = new FileStream(
            Path.Combine(directory, fileName),
            FileMode.Append, FileAccess.Write, FileShare.Read);

        return new TradeJournal(new StreamWriter(stream, new UTF8Encoding(false)), ownsWriter: true);
    }

    /// <summary>
    /// Opens a journal, stepping aside to a numbered name when the preferred one is already
    /// held by another writer, and returning null rather than throwing when none can be opened.
    ///
    /// WHY THIS EXISTS. A second chart on the same product asked for the same file, and
    /// <see cref="OpenFile"/> opens with <see cref="FileShare.Read"/>, which refuses a second
    /// writer. The IOException left OnInit and the indicator never started — observed
    /// 2026-08-21T17:04:28Z, a chart that drew nothing at all because a log file was locked.
    ///
    /// THE SHARE MODE IS NOT WIDENED, and that is deliberate. Two independent writers appending
    /// to one file interleave partial lines and corrupt the NDJSON that §13's replay reads.
    /// Trading a loud failure for silent data corruption is strictly worse than the bug.
    ///
    /// The first caller gets the name it asked for, unchanged, so the ordinary single-chart
    /// case produces exactly the file it always did.
    /// </summary>
    /// <param name="directory">Where the file goes.</param>
    /// <param name="fileName">The preferred name, used verbatim when it is free.</param>
    /// <param name="reason">Why nothing could be opened, when nothing could.</param>
    public static TradeJournal? TryOpenFile(string directory, string fileName, out string reason)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.", nameof(fileName));

        reason = string.Empty;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            reason = $"{directory} could not be created ({ex.GetType().Name}: {ex.Message})";
            return null;
        }

        FileStream? opened = null;
        string? denied = null;

        var chosen = ChooseFileName(fileName, candidate =>
        {
            try
            {
                opened = new FileStream(
                    Path.Combine(directory, candidate),
                    FileMode.Append, FileAccess.Write, FileShare.Read);

                return true;
            }
            catch (IOException)
            {
                // Held by another writer. Step aside and try the next name.
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Not a collision — this will not improve with a different number, so the
                // search is stopped by reporting it and refusing every remaining candidate.
                denied ??= $"{candidate} could not be written ({ex.Message})";
                return false;
            }
        });

        if (chosen is not null && opened is not null)
            return new TradeJournal(new StreamWriter(opened, new UTF8Encoding(false)), ownsWriter: true);

        reason = denied
                 ?? $"{fileName} and {MaxNameAttempts - 1} numbered alternatives are all held by "
                 + "another writer";

        return null;
    }

    /// <summary>
    /// The first candidate name an opener accepts: the preferred one, then numbered variants.
    ///
    /// THE OPENER IS INJECTED BECAUSE THE REAL ONE CANNOT BE EXERCISED HERE. The collision this
    /// exists to survive is a Windows file lock, and .NET on Unix does not enforce
    /// <see cref="FileShare"/> at all — verified by execution on this machine, where a second
    /// writer opened the same file happily. A test suite running on Linux therefore cannot
    /// reproduce the failure the indicator hit on the trading host.
    ///
    /// Splitting the DECISION from the platform behaviour is what makes it checkable: the
    /// production caller supplies a real open, a test supplies one that refuses whichever names
    /// it likes, and the ordering rule is asserted on every platform.
    /// </summary>
    /// <param name="fileName">The preferred name, offered first and used verbatim when free.</param>
    /// <param name="canOpen">Whether a candidate name can be written. Called in order.</param>
    /// <returns>The accepted name, or null when none was.</returns>
    public static string? ChooseFileName(string fileName, Func<string, bool> canOpen)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A file name is required.", nameof(fileName));

        if (canOpen is null)
            throw new ArgumentNullException(nameof(canOpen));

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var candidate = attempt == 1 ? fileName : $"{stem}-{attempt}{extension}";

            if (canOpen(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// How many names to try before giving up.
    ///
    /// Small on purpose: more than a handful of charts on one product writing separate journals
    /// is a situation worth reporting rather than accommodating silently.
    /// </summary>
    private const int MaxNameAttempts = 8;

    /// <summary>Records already written. Exposed so a caller can confirm a write happened.</summary>
    public long Count
    {
        get { lock (this.gate) { return this.sequence; } }
    }

    /// <summary>
    /// Mints an identifier for a signal, joining its acceptance or rejection to its later
    /// outcome. Deterministic in its inputs so a replay produces the same identifiers as the
    /// live run did, which is what lets the two be compared at all.
    /// </summary>
    public static string MintSignalId(
        string sessionName, string symbolRoot, string playbookId, DateTime timestampUtc)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}-{1}-{2}-{3:yyyyMMdd'T'HHmmss'Z'}",
            symbolRoot, sessionName, playbookId, timestampUtc);

    public void Write(JournalEntry entry)
    {
        if (entry is null)
            throw new ArgumentNullException(nameof(entry));

        lock (this.gate)
        {
            this.writer.WriteLine(JsonSerializer.Serialize(entry, Options));
            this.writer.Flush();
            this.sequence++;
        }
    }

    /// <summary>
    /// Records a scored setup, accepted or refused, with everything needed to re-derive the
    /// decision.
    /// </summary>
    public string RecordSignal(
        SessionContext context,
        TradePlan plan,
        OrderPlan? orders,
        IReadOnlyList<string> unevaluatedClauses,
        DateTime timestampUtc,
        ImbalanceGateVerdict? imbalance = null,
        AbsorptionGateVerdict? absorption = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (unevaluatedClauses is null) throw new ArgumentNullException(nameof(unevaluatedClauses));

        var signalId = MintSignalId(
            context.SessionName, context.SymbolRoot, plan.PlaybookId, timestampUtc);

        var accepted = orders is { Sendable: true };

        this.Write(WithAbsorption(WithImbalance(
            PlanEntry(
                context, plan,
                accepted ? JournalKind.SignalAccepted : JournalKind.SignalRejected,
                signalId, timestampUtc) with
            {
                Blockers = accepted
                    ? Array.Empty<string>()
                    : (orders?.Blockers ?? plan.Score.Vetoes).ToList(),
                UnevaluatedClauses = unevaluatedClauses,
                RiskUsd = orders?.TotalRiskUsd,
                Contracts = orders?.TotalContracts,
                Instruments = orders is null
                    ? null
                    : string.Join("+", orders.Orders.Select(o => $"{o.Quantity}{o.Instrument.Tier}")),
            },
            imbalance),
            absorption));

        return signalId;
    }

    /// <summary>
    /// Everything a scored plan contributes to a record, in ONE place.
    ///
    /// EXTRACTED RATHER THAN COPIED. The routed path and the chart path record the same
    /// decision by different routes — one has an order plan, the other never will — and two
    /// copies of this mapping would let the study and the chart describe the same setup with
    /// different fields, which is precisely the divergence
    /// <see cref="Playbooks.SetupEvaluator"/> exists to prevent one level up.
    /// </summary>
    private static JournalEntry PlanEntry(
        SessionContext context, TradePlan plan, JournalKind kind, string signalId, DateTime atUtc)
        => new()
        {
            SignalId = signalId,
            Kind = kind,
            TimestampUtc = atUtc,
            SessionName = context.SessionName,
            SymbolRoot = context.SymbolRoot,
            PlaybookId = plan.PlaybookId,
            Direction = plan.Direction.ToString(),
            Score = plan.Score.Score,
            Grade = plan.Score.Grade.ToString(),
            Contributions = plan.Score.Contributions
                .ToDictionary(c => c.ModuleId, c => Math.Round(c.Points, 4)),
            DarkTiers = plan.Score.RedistributedFrom.Select(g => g.ToString()).ToList(),
            EntryPrice = plan.EntryPrice,
            StopPrice = plan.Stop.Price,
            RiskTicks = plan.RiskTicks,
            TargetTicks = plan.Targets.Select(t => t.Ticks).ToList(),
            Orh = context.Range?.Orh,
            Orl = context.Range?.Orl,
            Orw = context.Range?.Orw,
            OrGrade = context.Range?.Grade.ToString(),
            Detail = plan.Reason,
        };

    /// <summary>
    /// Records a setup the CHART proposed, with everything needed to re-derive the decision.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="RecordSignal"/> BECAUSE ACCEPTANCE MEANS SOMETHING ELSE HERE.
    /// That method reads acceptance off an <c>OrderPlan</c>; the indicator routes nothing and
    /// never builds one, so calling it from the chart would stamp every setup
    /// <see cref="JournalKind.SignalRejected"/> — a whole dataset mislabelled by a default.
    /// On the chart a setup is accepted when the evaluator produced a plan and nothing vetoed
    /// it, which is the only meaning available to something that does not trade.
    ///
    /// Measured on the live host before this existed: of 8,972 decision records, exactly TWO
    /// carried a plan. These rows are rare and worth their structure.
    /// </remarks>
    public string RecordChartSignal(
        SessionContext context, TradePlan plan, IReadOnlyList<string> vetoes, DateTime atUtc,
        ImbalanceGateVerdict? imbalance = null,
        AbsorptionGateVerdict? absorption = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (vetoes is null) throw new ArgumentNullException(nameof(vetoes));

        var signalId = MintSignalId(
            context.SessionName, context.SymbolRoot, plan.PlaybookId, atUtc);

        this.Write(WithAbsorption(WithImbalance(
            PlanEntry(
                context, plan,
                vetoes.Count == 0 ? JournalKind.SignalAccepted : JournalKind.SignalRejected,
                signalId, atUtc) with
            {
                Blockers = vetoes.ToList(),
                // A setup that reached a plan while the gate was silent had a confirmation
                // clause it could not evaluate. Named here for the same reason iceberg and
                // depth are: an unevaluated check must never read later as one that passed.
                UnevaluatedClauses = UnevaluatedFor(imbalance, absorption),
            },
            imbalance),
            absorption));

        return signalId;
    }

    /// <summary>
    /// The confirmation clauses this signal could not evaluate.
    ///
    /// Built per SIGNAL rather than read off a playbook's static list, because whether depth
    /// was dark is a fact about this moment and not about the build. An unevaluated check
    /// must never read later as one that passed.
    /// </summary>
    private static IReadOnlyList<string>? UnevaluatedFor(
        ImbalanceGateVerdict? imbalance, AbsorptionGateVerdict? absorption)
    {
        var clauses = new List<string>(2);

        if (imbalance is { State: ImbalanceGateState.Unmeasured })
            clauses.Add(ImbalanceGate.UnevaluatedClause);

        if (absorption is { State: AbsorptionGateState.Unmeasured })
            clauses.Add(AbsorptionGate.UnevaluatedClause);

        return clauses.Count == 0 ? null : clauses;
    }

    /// <summary>
    /// Records a setup that TRIGGERED and was refused before a plan could be built.
    /// </summary>
    /// <remarks>
    /// The common refusal, and the one that could not be expressed at all before this.
    /// <see cref="Playbooks.SetupEvaluator"/> collects a playbook's vetoes and moves on to the
    /// next, so a refusal arrives as VETOES WITH NO PLAN — no playbook, no score, no entry
    /// price. A recorder that requires a plan cannot write it, and the result was 413 refusals
    /// on the live host reachable only as free text inside an engine-state line.
    ///
    /// Thin by necessity, not by choice: what is knowable is when it happened and why it was
    /// refused, and inventing the rest would be worse than recording less.
    /// </remarks>
    public void RecordChartRejection(
        SessionContext context, IReadOnlyList<string> vetoes, DateTime atUtc,
        ImbalanceGateVerdict? imbalance = null,
        AbsorptionGateVerdict? absorption = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (vetoes is null) throw new ArgumentNullException(nameof(vetoes));

        if (vetoes.Count == 0)
            return;

        this.Write(WithAbsorption(WithImbalance(
            new JournalEntry
            {
                SignalId = MintSignalId(context.SessionName, context.SymbolRoot, "REFUSED", atUtc),
                Kind = JournalKind.SignalRejected,
                TimestampUtc = atUtc,
                SessionName = context.SessionName,
                SymbolRoot = context.SymbolRoot,
                Blockers = vetoes.ToList(),
                Orh = context.Range?.Orh,
                Orl = context.Range?.Orl,
                Orw = context.Range?.Orw,
                OrGrade = context.Range?.Grade.ToString(),
                UnevaluatedClauses = UnevaluatedFor(imbalance, absorption),
            },
            imbalance),
            absorption));
    }

    /// <summary>
    /// Stamps a gate verdict onto a row.
    ///
    /// ONE MAPPING, THREE CALLERS. The accepted, rejected and routed paths all record the
    /// same verdict, and three copies is how they would come to describe it with different
    /// fields — the divergence <see cref="PlanEntry"/> already exists to prevent one level up.
    /// </summary>
    private static JournalEntry WithImbalance(JournalEntry entry, ImbalanceGateVerdict? gate)
        => gate is null ? entry : entry with
        {
            ImbalanceGate = gate.State.ToString(),
            ImbalanceEnforced = gate.Enforcing,
            ImbalanceWouldBlock = gate.WouldBlock,
            ImbalanceRequiredRun = gate.RequiredRun,
            ImbalanceDirectionalRun = gate.DirectionalRun,
            ImbalanceOpposingRun = gate.OpposingRun,
            ImbalanceCoverage = gate.Coverage,
        };

    /// <summary>
    /// Stamps an absorption verdict onto a row. One mapping, three callers, for the reason
    /// given on <see cref="WithImbalance"/>.
    /// </summary>
    private static JournalEntry WithAbsorption(JournalEntry entry, AbsorptionGateVerdict? gate)
        => gate is null ? entry : entry with
        {
            AbsorptionGate = gate.State.ToString(),
            AbsorptionEnforced = gate.Enforcing,
            AbsorptionWouldBlock = gate.WouldBlock,
            AbsorptionState = gate.Reading.State.ToString(),
            AbsorptionPrice = double.IsNaN(gate.Reading.Price) ? null : gate.Reading.Price,
            AbsorptionVolume = gate.Reading.AbsorbedVolume,
            AbsorptionSizeReduction = gate.Reading.SizeReduction,
            AbsorptionRatio = gate.Reading.Ratio,
            AbsorptionOpposingState = gate.Opposing.State.ToString(),
        };

    /// <summary>Records an opening range closing.</summary>
    public void RecordRange(SessionContext context, OrSnapshot range)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (range is null) throw new ArgumentNullException(nameof(range));

        this.Write(new JournalEntry
        {
            SignalId = MintSignalId(context.SessionName, context.SymbolRoot, "RANGE", range.CloseUtc),
            Kind = JournalKind.RangeClosed,
            TimestampUtc = range.CloseUtc,
            SessionName = context.SessionName,
            SymbolRoot = context.SymbolRoot,
            Orh = range.Orh,
            Orl = range.Orl,
            Orw = range.Orw,
            OrGrade = range.Grade.ToString(),
            OrCloseReason = range.CloseReason.ToString(),
            Detail = string.Format(
                CultureInfo.InvariantCulture,
                "width {0:N2} ({1:N1} ticks), {2} high tests, {3} low tests, delta {4:N0}, "
                + "noise {5:N1} ticks, {6}",
                range.Width, range.WidthTicks, range.HighTests, range.LowTests, range.Delta,
                range.AtrTicks,
                range.Gradeable ? $"graded {range.Grade}" : "ungraded — no average daily range"),
        });
    }

    /// <summary>Records a position's outcome, joined to its signal.</summary>
    public void RecordOutcome(
        string signalId,
        SessionContext context,
        DateTime timestampUtc,
        double realisedTicks,
        double realisedUsd,
        string detail)
    {
        if (string.IsNullOrWhiteSpace(signalId))
            throw new ArgumentException("An outcome must join to a signal.", nameof(signalId));
        if (context is null) throw new ArgumentNullException(nameof(context));

        this.Write(new JournalEntry
        {
            SignalId = signalId,
            Kind = JournalKind.Outcome,
            TimestampUtc = timestampUtc,
            SessionName = context.SessionName,
            SymbolRoot = context.SymbolRoot,
            RiskTicks = (int)Math.Round(realisedTicks),
            RiskUsd = realisedUsd,
            Detail = detail,
        });
    }

    /// <summary>Records an engine-state change: a gate, a kill-switch, a mode downgrade.</summary>
    /// <param name="context">The session the decision was made in.</param>
    /// <param name="timestampUtc">When it was made.</param>
    /// <param name="what">The decision, as one line.</param>
    /// <param name="reasons">Why it went that way: the gates, vetoes or kill switches.</param>
    /// <param name="evaluationsSincePrevious">
    /// How many evaluations this row stands for, from <see cref="EngineStateThrottle"/>.
    /// Identical consecutive decisions are collapsed, so a row can represent thousands.
    /// </param>
    public void RecordEngineState(
        SessionContext context, DateTime timestampUtc, string what, IReadOnlyList<string> reasons,
        int evaluationsSincePrevious = 0)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (reasons is null) throw new ArgumentNullException(nameof(reasons));

        this.Write(new JournalEntry
        {
            SignalId = MintSignalId(context.SessionName, context.SymbolRoot, "ENGINE", timestampUtc),
            Kind = JournalKind.EngineState,
            TimestampUtc = timestampUtc,
            SessionName = context.SessionName,
            SymbolRoot = context.SymbolRoot,
            Blockers = reasons,
            Detail = what,
            EvaluationsSincePrevious = evaluationsSincePrevious,
        });
    }

    /// <summary>
    /// Records the direction read for a closed bar.
    /// </summary>
    /// <remarks>
    /// WRITTEN ON A BAR CLOSE, NOT ON A FOLD. See <see cref="JournalKind.DirectionRead"/>
    /// for why the cadence matters and why this is not a field on an EngineState row.
    ///
    /// IT RECORDS THE READ WHATEVER IT SAYS, including UNDECIDED and MIXED. A journal
    /// that only kept the decisive reads would describe a market that is decisive far
    /// more often than it is, and any later test against it would inherit that.
    /// </remarks>
    /// <param name="context">The session the read was taken in.</param>
    /// <param name="timestampUtc">The close time of the bar this read belongs to.</param>
    /// <param name="read">The verdict and the votes behind it.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public void RecordDirection(
        SessionContext context, DateTime timestampUtc, DirectionRead read)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (read is null) throw new ArgumentNullException(nameof(read));

        this.Write(new JournalEntry
        {
            SignalId = MintSignalId(
                context.SessionName, context.SymbolRoot, "DIR", timestampUtc),
            Kind = JournalKind.DirectionRead,
            TimestampUtc = timestampUtc,
            SessionName = context.SessionName,
            SymbolRoot = context.SymbolRoot,
            DirectionVerdict = read.Verdict.ToString(),
            DirectionAgreeing = read.Agreeing,
            DirectionCast = read.Cast,
            DirectionOffered = read.Offered,
            DirectionVotes = string.Join(
                ",", read.Votes.Select(v => $"{v.Name}:{v.State}")),
            Detail = read.Headline,
        });
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.writer.Flush();

            if (this.ownsWriter)
                this.writer.Dispose();
        }
    }
}
