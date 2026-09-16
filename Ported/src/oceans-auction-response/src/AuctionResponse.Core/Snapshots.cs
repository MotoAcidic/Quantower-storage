namespace AuctionResponse.Core;

/// <summary>
/// Every baseline feature at one instant, each carrying its own availability. Used both as
/// live evidence and, once copied into a <see cref="CandidateSnapshot"/>, as frozen
/// evidence — the two are never the same object.
/// </summary>
public sealed record FeatureSnapshot
{
    public required long AtNs { get; init; }
    public required DateTime AtUtc { get; init; }
    public required long AtSequence { get; init; }

    public Measure BuyVolume { get; init; }
    public Measure SellVolume { get; init; }
    public Measure UnknownVolume { get; init; }
    public Measure TotalVolume { get; init; }
    public Measure Delta { get; init; }
    public Measure NormalizedDelta { get; init; }
    public Measure SideQuality { get; init; }
    public Measure Response { get; init; }
    public Measure QueueImbalance { get; init; }
    public Measure Ofi { get; init; }
    public Measure MeanDepth { get; init; }
    public Measure DepthNormalizedOfi { get; init; }
    public Measure Spread { get; init; }
    public Measure ZoneAttackerVolume { get; init; }
    public Measure ZoneVolumeFraction { get; init; }
    public Measure AttackerVolumePercentile { get; init; }
    public Measure OrientedProgress { get; init; }
    public Measure OrientedExcursion { get; init; }
    public Measure CumulativeDelta { get; init; }

    /// <summary>Only present when the optional regression module is enabled AND compatible.</summary>
    public Measure ResidualEvidence { get; init; }

    /// <summary>Only present when the optional MBO module is enabled AND verified.</summary>
    public Measure NetAdditionEvidence { get; init; }
    public Measure NetRemovalEvidence { get; init; }

    public PlotPoint? Plot { get; init; }

    public static FeatureSnapshot Empty(long ns, DateTime utc, long seq) => new()
    {
        AtNs = ns, AtUtc = utc, AtSequence = seq
    };
}

/// <summary>Which inputs the feed actually provides, established by observation (Section 13).</summary>
public sealed record CapabilityFlags
{
    public bool Trades { get; init; }
    public bool Quotes { get; init; }
    public bool MarketByPrice { get; init; }
    /// <summary>True only when a snapshot fence AND ordering semantics are proven.</summary>
    public bool MarketByOrderVerified { get; init; }
    public bool ExecutionLinkage { get; init; }
    public string? MboUnverifiedReason { get; init; }

    /// <summary>
    /// The baseline setup rule needs trades AND quotes. Depth-only and trades-only modes may
    /// render what they have but cannot satisfy the rule.
    /// </summary>
    public bool CanSatisfyBaselineRule => Trades && Quotes;
}

/// <summary>Health as displayed. Unknown values are N/A with a reason, never zero.</summary>
public sealed record HealthSnapshot
{
    public required DataState State { get; init; }
    public required string StateReason { get; init; }
    public required CapabilityFlags Capabilities { get; init; }

    public Measure BidQuoteAge { get; init; }
    public Measure AskQuoteAge { get; init; }
    public Measure Spread { get; init; }
    public Measure SideQuality { get; init; }
    public Measure ProcessingLag { get; init; }
    public int Backlog { get; init; }

    /// <summary>
    /// What has actually been observed since the epoch began. These exist so that a
    /// capability reading "no" is explainable: no trades observed is a different problem
    /// from trades observed and rejected.
    /// </summary>
    public long EventsAccepted { get; init; }
    public long TradeEvents { get; init; }
    public long QuoteEvents { get; init; }
    public long DepthEvents { get; init; }
    public long RejectedEvents { get; init; }

    /// <summary>Owner inputs the engine is waiting on, each with its remedy.</summary>
    public IReadOnlyList<MissingInput> MissingInputs { get; init; } = Array.Empty<MissingInput>();
    public int IngressCapacity { get; init; }
    public bool IngressOverflowed { get; init; }
    public int OverflowCount { get; init; }
    public bool RecorderFaulted { get; init; }
    public string? RecorderFaultReason { get; init; }
    public int ConnectionEpoch { get; init; }
    public long EpochAgeMs { get; init; }
    public BaselineStatus BaselineStatus { get; init; }
    public string? BaselineReason { get; init; }

    /// <summary>
    /// True when a quiet-but-healthy market has tripped the quote-age guard. Reported as a
    /// freshness guard rather than as a disconnected feed, because those are different
    /// problems with different fixes.
    /// </summary>
    public bool QuoteFreshnessGuardTripped { get; init; }
}

/// <summary>One entry in the timestamped setup history (Section 2 correction 2).</summary>
public sealed record HistoryEntry(
    string EventId,
    DateTime TransitionUtc,
    long TransitionElapsedNs,
    string LevelId,
    int Orientation,
    SetupState State,
    string Reason);

/// <summary>Current status of one level's setup, as displayed in the sidebar.</summary>
public sealed record SetupStatus
{
    public required string LevelId { get; init; }
    public required SetupState State { get; init; }
    public required int Orientation { get; init; }
    public long? AgeMs { get; init; }
    public CandidateSnapshot? Candidate { get; init; }
    public long? CooldownRemainingMs { get; init; }

    /// <summary>Level price in ticks, so the overlay can anchor to it exactly.</summary>
    public long PriceTicks { get; init; }
    public long ZoneLowTicks { get; init; }
    public long ZoneHighTicks { get; init; }

    /// <summary>True when the midpoint is inside this level's zone right now.</summary>
    public bool MidpointInZone { get; init; }

    /// <summary>
    /// Live evidence for THIS level, so something useful is on the chart from the first
    /// minute — long before any reference distribution exists.
    /// </summary>
    public Measure AttackerVolume { get; init; }
    public Measure ZoneVolume { get; init; }
    public Measure AttackerPercentile { get; init; }

    /// <summary>
    /// The single current status line. After confirmation this reads "Bearish confirmation"
    /// or "Bullish confirmation" — never a stale line about the buying that preceded it.
    /// </summary>
    public string Headline => State switch
    {
        SetupState.Idle => "No active setup",
        SetupState.Candidate => Orientation > 0 ? "Absorption candidate at resistance" : "Absorption candidate at support",
        SetupState.Confirmed => Orientation > 0 ? "Bearish confirmation" : "Bullish confirmation",
        SetupState.Invalidated => "Invalidated",
        SetupState.Expired => "Expired",
        SetupState.DataInterrupted => "Data interrupted",
        _ => State.ToString()
    };
}

/// <summary>
/// The immutable view the renderer draws from (Section 3). Rendering never touches a
/// collection the event worker is mutating, and never calculates a signal.
///
/// Frozen evidence and live evidence are separate objects on purpose: showing them in one
/// place is exactly the mistake the UI corrections call out.
/// </summary>
public sealed record ViewSnapshot
{
    public required long PublishedAtNs { get; init; }
    public required DateTime PublishedAtUtc { get; init; }
    public required long PublishedAtSequence { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required bool IsSimulatedData { get; init; }
    public required string ModeLabel { get; init; }

    public required HealthSnapshot Health { get; init; }

    /// <summary>Live evidence, updating every decision tick.</summary>
    public required FeatureSnapshot Live { get; init; }

    /// <summary>The status shown in the sidebar: the most recently armed active candidate.</summary>
    public SetupStatus? Current { get; init; }

    /// <summary>All levels with their own independent state.</summary>
    public IReadOnlyList<SetupStatus> AllSetups { get; init; } = Array.Empty<SetupStatus>();

    public IReadOnlyList<LevelDefinition> Levels { get; init; } = Array.Empty<LevelDefinition>();

    /// <summary>Newest first, immutable event ids (Section 3).</summary>
    public IReadOnlyList<HistoryEntry> History { get; init; } = Array.Empty<HistoryEntry>();

    /// <summary>The pressure-response trail, oldest first. Newest point is drawn distinctly.</summary>
    public IReadOnlyList<PlotPoint> Trail { get; init; } = Array.Empty<PlotPoint>();

    public DescriptiveLabel Label { get; init; } = DescriptiveLabel.Unavailable;

    /// <summary>
    /// Null in the baseline. The probability panel stays hidden or reads "Not calibrated"
    /// until a compatible, validated model is loaded — no invented percentages, ever.
    /// </summary>
    public ModelOutput? Model { get; init; }

    public string ConfigurationHash { get; init; } = "";
    public string? BaselineHash { get; init; }
}

/// <summary>
/// Output of the optional probability module. Its labels are FIRST-TOUCH, without flow or
/// dwell, and therefore deliberately differ from rule confirmation.
/// </summary>
public sealed record ModelOutput
{
    public required string ModelVersion { get; init; }
    public required int HorizonMs { get; init; }
    public required IReadOnlyList<string> ClassNames { get; init; }
    public required IReadOnlyList<double> Probabilities { get; init; }
    public string DisplayTitle => "First-touch forecast";
}
