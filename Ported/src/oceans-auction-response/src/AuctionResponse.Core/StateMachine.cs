namespace AuctionResponse.Core;

/// <summary>
/// Health of the inputs. Deliberately independent of <see cref="SetupState"/>: a healthy
/// feed with no candidate and a broken feed are different facts and get different words.
/// </summary>
public enum DataState
{
    /// <summary>An owner-supplied input is missing. No alert is possible.</summary>
    SetupRequired,
    /// <summary>Inputs present, but the baseline or observation window is not yet satisfied.</summary>
    Warmup,
    Ready,
    /// <summary>Usable for display, not for alerts.</summary>
    Degraded,
    Stale,
    Faulted,
    Disposed
}

/// <summary>Lifecycle of one candidate at one level.</summary>
public enum SetupState
{
    Idle,
    Candidate,
    Confirmed,
    Invalidated,
    Expired,
    DataInterrupted
}

public static class SetupStateExtensions
{
    public static bool IsTerminal(this SetupState s) =>
        s is SetupState.Confirmed or SetupState.Invalidated or SetupState.Expired or SetupState.DataInterrupted;
}

/// <summary>
/// Tracks an uninterrupted price dwell beyond a boundary (Section 12).
///
/// Updated on EVERY quote, not only at decision ticks: a decision tick cannot infer
/// uninterrupted dwell across an interval it did not observe. Any intervening quote that
/// violates the boundary, and any freshness failure, resets the timer to nothing.
/// </summary>
public sealed class PriceDwellTracker
{
    private readonly long _requiredNs;
    private long? _satisfiedSinceNs;

    public PriceDwellTracker(int requiredMs) { _requiredNs = (long)requiredMs * 1_000_000L; }

    public long RequiredNs => _requiredNs;
    public bool IsArmed => _satisfiedSinceNs.HasValue;
    public long? StartedAtNs => _satisfiedSinceNs;

    /// <summary>Feeds one observed quote. <paramref name="beyond"/> is the boundary test.</summary>
    public void Observe(long ns, bool beyond)
    {
        if (!beyond) { _satisfiedSinceNs = null; return; }
        _satisfiedSinceNs ??= ns;
    }

    /// <summary>A stale or invalid interval breaks the dwell as surely as a violating quote.</summary>
    public void Break() => _satisfiedSinceNs = null;

    public bool IsComplete(long nowNs) => _satisfiedSinceNs is { } since && nowNs - since >= _requiredNs;

    public long? ElapsedNs(long nowNs) => _satisfiedSinceNs is { } since ? nowNs - since : null;

    public void Reset() => _satisfiedSinceNs = null;
}

/// <summary>Why a candidate left the Candidate state.</summary>
public static class TerminalReason
{
    public const string DataFault = "data fault, reconnect or required input failure";
    public const string SessionExit = "session exit";
    public const string LevelRemoved = "level removed";
    public const string LevelExpired = "level expiry reached";
    public const string ConfigurationChanged = "configuration change";
    public const string AgeExceeded = "candidate age exceeded the timeout";
    public const string AgeAtDeadline = "candidate reached the timeout with no terminal condition";
    public const string FailureDwell = "price held beyond the frozen failure boundary";
    public const string Confirmed = "price held beyond the confirmation boundary with opposite flow";
}

/// <summary>The facts a decision tick needs to resolve a candidate's next state.</summary>
public readonly record struct TransitionInputs(
    long AgeMs,
    bool Healthy,
    bool SessionEligible,
    bool LevelStillDeclared,
    bool ConfigurationUnchanged,
    bool FailureDwellComplete,
    bool ConfirmationDwellComplete,
    bool OppositeFlow);

public readonly record struct TransitionDecision(SetupState State, string Reason)
{
    public bool IsTerminal => State.IsTerminal();
}

public static class TerminalTransition
{
    /// <summary>
    /// The normative processing priority of Section 12, in order and without exception:
    ///
    ///   1. Data fault / reconnect / required-input failure  -> DataInterrupted
    ///   2. Session exit, level removal or expiry, config change -> Expired (with reason)
    ///   3. Age GREATER THAN the timeout -> Expired; a later confirmation is not accepted
    ///   4. Completed failure dwell -> Invalidated
    ///   5. Completed confirmation dwell plus opposite flow -> Confirmed
    ///   6. Age EXACTLY at the timeout with no terminal condition -> Expired
    ///   7. Otherwise -> Candidate
    ///
    /// Steps 3 and 6 are why a confirmation exactly at the deadline is allowed but one a
    /// single tick later is not.
    /// </summary>
    public static TransitionDecision Resolve(in TransitionInputs i, int timeoutMs)
    {
        if (!i.Healthy)
            return new TransitionDecision(SetupState.DataInterrupted, TerminalReason.DataFault);

        if (!i.SessionEligible)
            return new TransitionDecision(SetupState.Expired, TerminalReason.SessionExit);
        if (!i.LevelStillDeclared)
            return new TransitionDecision(SetupState.Expired, TerminalReason.LevelRemoved);
        if (!i.ConfigurationUnchanged)
            return new TransitionDecision(SetupState.Expired, TerminalReason.ConfigurationChanged);

        if (i.AgeMs > timeoutMs)
            return new TransitionDecision(SetupState.Expired, TerminalReason.AgeExceeded);

        if (i.FailureDwellComplete)
            return new TransitionDecision(SetupState.Invalidated, TerminalReason.FailureDwell);

        if (i.ConfirmationDwellComplete && i.OppositeFlow)
            return new TransitionDecision(SetupState.Confirmed, TerminalReason.Confirmed);

        if (i.AgeMs == timeoutMs)
            return new TransitionDecision(SetupState.Expired, TerminalReason.AgeAtDeadline);

        return new TransitionDecision(SetupState.Candidate, "candidate active");
    }
}

/// <summary>
/// Everything frozen at arming time (Section 16). A confirmed candidate is immutable: a
/// later market move does not rewrite it, and the frozen evidence never becomes the live
/// evidence.
/// </summary>
public sealed record CandidateSnapshot
{
    public required string CandidateId { get; init; }
    public required string LevelId { get; init; }
    public required int Orientation { get; init; }
    public required int ConnectionEpoch { get; init; }
    public required long ArmedAtNs { get; init; }
    public required DateTime ArmedAtUtc { get; init; }
    public required long ArmedAtSequence { get; init; }

    public required long LevelTicks { get; init; }
    public required long ZoneLowTicks { get; init; }
    public required long ZoneHighTicks { get; init; }
    public required long StartMidHalfTicks { get; init; }
    public required CandidateBoundaries Boundaries { get; init; }

    /// <summary>Evidence as measured at detection. Never updated afterwards.</summary>
    public required FeatureSnapshot FrozenEvidence { get; init; }

    public required string ConfigurationHash { get; init; }
    public required string? BaselineHash { get; init; }
    public required string? ModelHash { get; init; }

    public long AgeNs(long nowNs) => nowNs - ArmedAtNs;
    public long AgeMs(long nowNs) => AgeNs(nowNs) / 1_000_000L;
}

/// <summary>
/// One immutable, append-only transition record (Section 17). Carries enough to reconstruct
/// why the engine said what it said, and never carries credentials, account identifiers or
/// positions.
/// </summary>
public sealed record TransitionRecord
{
    public required string TransitionId { get; init; }
    public required string CandidateId { get; init; }
    public required string LevelId { get; init; }
    public required DateTime DetectionUtc { get; init; }
    public required long DetectionElapsedNs { get; init; }
    public required long SourceSequence { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required int ConnectionEpoch { get; init; }
    public required int Orientation { get; init; }
    public required SetupState OldState { get; init; }
    public required SetupState NewState { get; init; }
    public required string Reason { get; init; }
    public required long LevelTicks { get; init; }
    public required long ZoneLowTicks { get; init; }
    public required long ZoneHighTicks { get; init; }
    public required long ConfirmationHalfTicks { get; init; }
    public required long FailureHalfTicks { get; init; }
    public required FeatureSnapshot Features { get; init; }
    public required DataState DataQuality { get; init; }
    public required string ConfigurationHash { get; init; }
    public string? BaselineHash { get; init; }
    public string? ModelHash { get; init; }
    public string SchemaVersion { get; init; } = Versioning.SchemaVersion;
    public string EngineVersion { get; init; } = Versioning.EngineVersion;

    /// <summary>Alert deduplication key: instrument, epoch, candidate and transition type.</summary>
    public string DeduplicationKey => Instrument + "|" + ConnectionEpoch + "|" + CandidateId + "|" + NewState;
}
