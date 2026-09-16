namespace AuctionResponse.Core;

/// <summary>
/// One frozen reference distribution: a 30-minute session bucket crossed with one window
/// length, for one side. Quantiles are nearest-rank and were computed from NON-OVERLAPPING
/// windows, so the samples are not the same volume counted many times over.
/// </summary>
public sealed record BaselineBucket
{
    /// <summary>Minutes from session start, floor-bucketed by bucketMinutes.</summary>
    public required int BucketStartMinute { get; init; }
    public required int WindowMs { get; init; }
    public required int SampleCount { get; init; }

    /// <summary>Nearest-rank quantiles of buy-initiated volume, keyed by p.</summary>
    public IReadOnlyDictionary<double, double> BuyVolumeQuantiles { get; init; } = new Dictionary<double, double>();
    public IReadOnlyDictionary<double, double> SellVolumeQuantiles { get; init; } = new Dictionary<double, double>();

    /// <summary>Q0.75 of |D_W| and |R_W| — the sign-preserving plot scales.</summary>
    public double? AbsoluteDeltaQ75 { get; init; }
    public double? AbsoluteResponseQ75 { get; init; }

    /// <summary>The sorted reference sample, retained so a displayed percentile uses the count rule.</summary>
    public IReadOnlyList<double> SortedBuyVolumes { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> SortedSellVolumes { get; init; } = Array.Empty<double>();
}

/// <summary>
/// The frozen baseline artifact (Sections 8, 17). Every contributing observation precedes
/// the scoring session. A compatibility mismatch means UNAVAILABLE, never a best-effort
/// substitution, and expired contracts never merge into a current one.
/// </summary>
public sealed record BaselineArtifact
{
    public required InstrumentKey Instrument { get; init; }
    public required decimal TickSize { get; init; }
    public required string SessionTimezone { get; init; }
    public required string CalendarVersion { get; init; }
    public required string ClockMode { get; init; }
    public required string FeedMode { get; init; }
    public required IReadOnlyList<int> WindowsMs { get; init; }
    public required int BucketMinutes { get; init; }
    public required IReadOnlyList<string> EligibleSessionIds { get; init; }
    public required DateTime FitEndUtc { get; init; }
    public required IReadOnlyList<string> SourceLogHashes { get; init; }
    public required string Sha256 { get; init; }
    public string SchemaVersion { get; init; } = Versioning.SchemaVersion;
    public string FeatureFormulaVersion { get; init; } = Versioning.FeatureFormulaVersion;

    /// <summary>
    /// How the response scale was derived. "midpoint" is the Section 6 definition; anything
    /// else is an approximation and is recorded here so it can never pass as the real thing.
    /// </summary>
    public string ResponseScaleSource { get; init; } = "midpoint";

    public IReadOnlyList<BaselineBucket> Buckets { get; init; } = Array.Empty<BaselineBucket>();

    public int SessionCount => EligibleSessionIds.Count;

    /// <summary>
    /// Compatibility contract. Returns the reason for refusal, or null when usable.
    /// </summary>
    public string? IncompatibleReason(Config config, InstrumentKey runningInstrument, decimal runningTickSize, string feedMode)
    {
        if (FeatureFormulaVersion != Versioning.FeatureFormulaVersion)
            return "baseline feature formula version " + FeatureFormulaVersion + " does not match engine " + Versioning.FeatureFormulaVersion;
        // Symbol and exchange must always match. Expiry is separable: a volume distribution
        // is a property of the instrument and the session, not of one dated contract, and
        // requiring the same expiry leaves you with no baseline at all on roll day. It is
        // still OFF by default, and the UI says so whenever it is on.
        if (!string.Equals(Instrument.Symbol, runningInstrument.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Instrument.Exchange, runningInstrument.Exchange, StringComparison.OrdinalIgnoreCase))
            return "baseline is for " + Instrument + " but the chart is " + runningInstrument;

        if (!config.Baseline.AllowCrossExpiry && !string.Equals(Instrument.Expiry, runningInstrument.Expiry, StringComparison.OrdinalIgnoreCase))
            return "baseline was built on " + Instrument.Expiry + " but the chart is " + runningInstrument.Expiry +
                   "; enable \"Allow cross-expiry baseline\" to use it across a roll";
        if (TickSize != runningTickSize)
            return "baseline tick size " + TickSize + " does not match instrument tick size " + runningTickSize;
        if (ClockMode != config.Timing.ClockMode)
            return "baseline clock mode " + ClockMode + " does not match configured " + config.Timing.ClockMode;
        if (FeedMode != feedMode)
            return "baseline feed mode " + FeedMode + " does not match running feed mode " + feedMode;
        if (BucketMinutes != config.Baseline.BucketMinutes)
            return "baseline bucket size " + BucketMinutes + " does not match configured " + config.Baseline.BucketMinutes;
        if (!config.Timing.WindowsMs.All(WindowsMs.Contains))
            return "baseline does not cover every configured window";

        // An insufficient session count is NOT an incompatibility: Section 8 calls for
        // Warmup in that case, which the resolver reports per bucket. Incompatibility is
        // reserved for artifacts that describe a different instrument, clock or feed.
        return null;
    }

    public BaselineBucket? Find(int bucketStartMinute, int windowMs)
    {
        foreach (var b in Buckets)
            if (b.BucketStartMinute == bucketStartMinute && b.WindowMs == windowMs) return b;
        return null;
    }
}

/// <summary>Why a baseline lookup could not produce a usable reference value.</summary>
public enum BaselineStatus
{
    Ready,
    /// <summary>No artifact is loaded at all.</summary>
    Absent,
    /// <summary>Artifact loaded but incompatible with the running configuration.</summary>
    Incompatible,
    /// <summary>Compatible, but this bucket/window lacks the required sample count.</summary>
    Warmup
}

public readonly record struct BaselineLookup(BaselineStatus Status, double? Value, int SampleCount, string? Reason)
{
    public bool IsReady => Status == BaselineStatus.Ready && Value.HasValue;
}

/// <summary>
/// Resolves frozen reference values for the running session. Suppresses candidate alerts
/// for any window whose bucket has not met the sample gates — the engine shows Warmup
/// rather than quietly using a thin distribution.
/// </summary>
public sealed class BaselineResolver
{
    private readonly Config _config;

    public BaselineResolver(Config config) { _config = config; }

    public BaselineArtifact? Artifact { get; private set; }
    public string? IncompatibleReason { get; private set; }

    /// <summary>Set when a loaded baseline came from a different dated contract.</summary>
    public bool UsingCrossExpiryBaseline { get; private set; }
    public string? CrossExpiryNote { get; private set; }

    public void Load(BaselineArtifact? artifact, InstrumentKey instrument, decimal tickSize, string feedMode)
    {
        if (artifact is null) { Artifact = null; IncompatibleReason = null; return; }
        var reason = artifact.IncompatibleReason(_config, instrument, tickSize, feedMode);
        if (reason is not null) { Artifact = null; IncompatibleReason = reason; return; }
        Artifact = artifact;
        IncompatibleReason = null;

        UsingCrossExpiryBaseline = !string.Equals(artifact.Instrument.Expiry, instrument.Expiry, StringComparison.OrdinalIgnoreCase);
        CrossExpiryNote = UsingCrossExpiryBaseline
            ? "baseline built on " + (string.IsNullOrWhiteSpace(artifact.Instrument.Expiry) ? "an unstamped contract" : artifact.Instrument.Expiry)
              + ", chart is " + (string.IsNullOrWhiteSpace(instrument.Expiry) ? "unstamped" : instrument.Expiry)
            : null;
    }

    public int BucketFor(int minutesFromSessionStart)
        => minutesFromSessionStart / _config.Baseline.BucketMinutes * _config.Baseline.BucketMinutes;

    /// <summary>Frozen attacker-volume quantile for the oriented attacking side.</summary>
    public BaselineLookup AttackerVolumeQuantile(int minutesFromSessionStart, int windowMs, int orientation)
    {
        var bucket = Resolve(minutesFromSessionStart, windowMs, out var failure);
        if (bucket is null) return failure;

        var p = _config.Candidate.AttackerVolumeQuantile;
        var table = orientation > 0 ? bucket.BuyVolumeQuantiles : bucket.SellVolumeQuantiles;
        if (!table.TryGetValue(p, out var q))
            return new BaselineLookup(BaselineStatus.Incompatible, null, bucket.SampleCount,
                "baseline does not carry the p=" + p.ToString(System.Globalization.CultureInfo.InvariantCulture) + " quantile");
        return new BaselineLookup(BaselineStatus.Ready, q, bucket.SampleCount, null);
    }

    /// <summary>Frozen sign-preserving plot scales, s_D and s_R.</summary>
    public (BaselineLookup Delta, BaselineLookup Response) PlotScales(int minutesFromSessionStart, int windowMs)
    {
        var bucket = Resolve(minutesFromSessionStart, windowMs, out var failure);
        if (bucket is null) return (failure, failure);

        var d = bucket.AbsoluteDeltaQ75 is { } dv
            ? new BaselineLookup(BaselineStatus.Ready, PlotMath.Scale(dv), bucket.SampleCount, null)
            : new BaselineLookup(BaselineStatus.Warmup, null, bucket.SampleCount, "baseline has no |D| scale for this bucket");
        var r = bucket.AbsoluteResponseQ75 is { } rv
            ? new BaselineLookup(BaselineStatus.Ready, PlotMath.Scale(rv), bucket.SampleCount, null)
            : new BaselineLookup(BaselineStatus.Warmup, null, bucket.SampleCount, "baseline has no |R| scale for this bucket");
        return (d, r);
    }

    /// <summary>Displayed percentile of an observed value, using the empirical count rule.</summary>
    public double? PercentileOf(int minutesFromSessionStart, int windowMs, int orientation, double value)
    {
        var bucket = Resolve(minutesFromSessionStart, windowMs, out _);
        if (bucket is null) return null;
        var sample = orientation > 0 ? bucket.SortedBuyVolumes : bucket.SortedSellVolumes;
        return Statistics.Percentile(sample, value);
    }

    private BaselineBucket? Resolve(int minutesFromSessionStart, int windowMs, out BaselineLookup failure)
    {
        if (Artifact is null)
        {
            failure = IncompatibleReason is null
                ? new BaselineLookup(BaselineStatus.Absent, null, 0, "no frozen baseline artifact is loaded")
                : new BaselineLookup(BaselineStatus.Incompatible, null, 0, IncompatibleReason);
            return null;
        }

        if (Artifact.SessionCount < _config.Baseline.MinimumSessions)
        {
            failure = new BaselineLookup(BaselineStatus.Warmup, null, 0,
                "baseline has " + Artifact.SessionCount + " sessions, minimum is " + _config.Baseline.MinimumSessions);
            return null;
        }

        var bucket = Artifact.Find(BucketFor(minutesFromSessionStart), windowMs);
        if (bucket is null)
        {
            failure = new BaselineLookup(BaselineStatus.Warmup, null, 0,
                "no baseline bucket for minute " + minutesFromSessionStart + ", window " + windowMs + "ms");
            return null;
        }
        if (bucket.SampleCount < _config.Baseline.MinimumSamplesPerBucketWindow)
        {
            failure = new BaselineLookup(BaselineStatus.Warmup, null, bucket.SampleCount,
                "bucket has " + bucket.SampleCount + " samples, minimum is " + _config.Baseline.MinimumSamplesPerBucketWindow);
            return null;
        }

        failure = default;
        return bucket;
    }
}
