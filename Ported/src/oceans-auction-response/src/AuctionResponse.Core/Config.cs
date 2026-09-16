using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AuctionResponse.Core;

/// <summary>
/// One owner input the engine is waiting on, with the remedy stated in the user's own terms
/// rather than as a config key they then have to decode.
/// </summary>
public sealed record MissingInput(string Key, string What, string HowToSupply);

public sealed record InstrumentConfig
{
    public InstrumentKey? InstrumentKey { get; init; }
    public decimal? TickSize { get; init; }
    public bool CrossInstrumentEnabled { get; init; }
}

public sealed record TimingConfig
{
    public string ClockMode { get; init; } = "ReceiveElapsedWithRecordedDecisionTicks";
    public int DecisionIntervalMs { get; init; } = 250;
    public IReadOnlyList<int> WindowsMs { get; init; } = new[] { 1000, 5000, 30000 };
    public int WarmupMs { get; init; } = 30000;
    public string LateTickPolicy { get; init; } = "OneActualTimeTickNoBackfill";
}

public sealed record BaselineConfig
{
    public string? ArtifactPath { get; init; }
    public int LookbackSessions { get; init; } = 20;
    public int MinimumSessions { get; init; } = 10;
    public int MinimumSamplesPerBucketWindow { get; init; } = 300;
    public int BucketMinutes { get; init; } = 30;
    public string QuantileMethod { get; init; } = "NearestRank";
    public string FreezePolicy { get; init; } = "BeforeSessionStart";

    /// <summary>
    /// Permits a baseline built on a different dated contract of the SAME instrument. Off by
    /// default; when on, the health panel says so, because it is a real relaxation.
    /// </summary>
    public bool AllowCrossExpiry { get; init; }
}

public sealed record CandidateConfig
{
    public int WindowMs { get; init; } = 5000;
    public int ZoneHalfWidthTicks { get; init; } = 2;
    public double AttackerVolumeQuantile { get; init; } = 0.9;
    public string QuantileComparison { get; init; } = "StrictlyGreater";
    public int MinimumProgressTicks { get; init; } = 0;
    public int MaximumProgressTicks { get; init; } = 2;
    public int MaximumForwardExcursionTicks { get; init; } = 2;
    public double MinimumZoneVolumeFraction { get; init; } = 0.25;
    public int ConfirmationBufferTicks { get; init; } = 2;
    public int FailureBeyondZoneTicks { get; init; } = 4;
    public int ConfirmationPriceDwellMs { get; init; } = 1000;
    public int FailurePriceDwellMs { get; init; } = 1000;
    public int TimeoutMs { get; init; } = 30000;
    public int CooldownMs { get; init; } = 30000;
    public string LevelPolicy { get; init; } = "ManualDeclaredBeforeWindow";
    public string ConfirmationPrice { get; init; } = "Midpoint";
}

public sealed record HealthConfig
{
    public int MaximumQuoteAgeMs { get; init; } = 1000;
    public int MaximumSpreadTicks { get; init; } = 8;
    public double MinimumKnownSideFraction { get; init; } = 0.95;
    public int MaximumProcessingLagMs { get; init; } = 250;
    public int IngressCapacityEvents { get; init; } = 100000;
    public string OverflowPolicy { get; init; } = "FaultAndResynchronize";
    public bool RequireCompleteQuotePath { get; init; } = true;
}

public sealed record SessionConfig
{
    public string Timezone { get; init; } = "America/New_York";
    public string WindowsTimezoneEquivalent { get; init; } = "Eastern Standard Time";
    public string StartLocal { get; init; } = "09:30:00";
    public string EndLocalExclusive { get; init; } = "16:00:00";
    public string? CalendarPath { get; init; }
    public IReadOnlyList<string> BlackoutIntervalsUtc { get; init; } = Array.Empty<string>();
    public string BlackoutPolicy { get; init; } = "UserSuppliedNoAutomaticNewsClaims";
}

public sealed record DisplayConfig
{
    public int MaximumRequestedFramesPerSecond { get; init; } = 10;
    public int PlotWindowMs { get; init; } = 5000;
    public int TrailMs { get; init; } = 30000;
    public double PlotClip { get; init; } = 3;
    public double NeutralVisualBand { get; init; } = 0.25;
    public int DescriptiveLabelDwellMs { get; init; } = 5000;
    public bool CircularViewEnabled { get; init; }
    public bool CurrentAndFrozenEvidenceSeparate { get; init; } = true;
}

public sealed record OptionalModulesConfig
{
    public bool ResponseRegressionEnabled { get; init; }
    public bool MboEvidenceEnabled { get; init; }
    public bool ProbabilitiesEnabled { get; init; }
    public int ReconciliationIntervalMs { get; init; } = 100;
    public int ForecastHorizonMs { get; init; } = 30000;
    public int RegressionMinimumTrainWindows { get; init; } = 2000;
    public int RegressionMinimumValidationWindows { get; init; } = 500;
    public double ResidualScaleFloorTicks { get; init; } = 0.5;
    public double EnhancedResistanceResidualThreshold { get; init; } = 1.5;
}

public sealed record RecordingConfig
{
    public string? Directory { get; init; }
    public bool PermissionConfirmed { get; init; }
    public bool RequireRecorderForResearch { get; init; } = true;
    public string Rotation { get; init; } = "InstrumentSession";
    public int? RetentionDays { get; init; }
}

public sealed record AlertsConfig
{
    public bool AudibleEnabled { get; init; }
    public bool LogCandidates { get; init; } = true;
    public bool LogTerminalTransitions { get; init; } = true;
    public bool AutomaticTradingEnabled { get; init; }
}

/// <summary>
/// The complete engine configuration (Section 17). DEFAULTS.json is a research settings
/// file, not a drop-in preset: instrument metadata, the baseline artifact path, the session
/// calendar and the recording location must be resolved locally, and until they are the
/// engine stays in SetupRequired. No alert is ever permitted merely because JSON parsed.
/// </summary>
public sealed record Config
{
    public string Version { get; init; } = "1.0.0";
    public string Status { get; init; } = "EXPERIMENTAL_SPECIFICATION_NOT_ATAS_PRESET";
    public InstrumentConfig Instrument { get; init; } = new();
    public TimingConfig Timing { get; init; } = new();
    public BaselineConfig Baseline { get; init; } = new();
    public CandidateConfig Candidate { get; init; } = new();
    public HealthConfig Health { get; init; } = new();
    public SessionConfig Session { get; init; } = new();
    public DisplayConfig Display { get; init; } = new();
    public OptionalModulesConfig OptionalModules { get; init; } = new();
    public RecordingConfig Recording { get; init; } = new();
    public AlertsConfig Alerts { get; init; } = new();

    /// <summary>Increments on any feature change; a change expires active candidates.</summary>
    public int ConfigurationVersion { get; init; } = 1;

    /// <summary>
    /// Structural validation (Section 17). Returns every problem found, not just the first.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Timing.DecisionIntervalMs <= 0) errors.Add("timing.decisionIntervalMs must be positive.");
        if (Timing.WarmupMs <= 0) errors.Add("timing.warmupMs must be positive.");
        if (!Timing.WindowsMs.Contains(Candidate.WindowMs))
            errors.Add("timing.windowsMs must contain the candidate window (" + Candidate.WindowMs + "ms).");
        if (!Timing.WindowsMs.Contains(5000))
            errors.Add("timing.windowsMs must contain the 5-second window.");
        foreach (var w in Timing.WindowsMs) if (w <= 0) errors.Add("timing.windowsMs entries must be positive.");

        if (Baseline.LookbackSessions <= 0) errors.Add("baseline.lookbackSessions must be positive.");
        if (Baseline.MinimumSessions <= 0) errors.Add("baseline.minimumSessions must be positive.");
        if (Baseline.MinimumSessions > Baseline.LookbackSessions)
            errors.Add("baseline.minimumSessions cannot exceed baseline.lookbackSessions.");
        if (Baseline.MinimumSamplesPerBucketWindow <= 0) errors.Add("baseline.minimumSamplesPerBucketWindow must be positive.");
        if (Baseline.BucketMinutes <= 0) errors.Add("baseline.bucketMinutes must be positive.");

        if (Candidate.WindowMs <= 0) errors.Add("candidate.windowMs must be positive.");
        if (Candidate.AttackerVolumeQuantile <= 0d || Candidate.AttackerVolumeQuantile >= 1d)
            errors.Add("candidate.attackerVolumeQuantile must lie in the open interval (0,1).");
        if (Candidate.MinimumZoneVolumeFraction < 0d || Candidate.MinimumZoneVolumeFraction > 1d)
            errors.Add("candidate.minimumZoneVolumeFraction must lie in [0,1].");
        if (Candidate.ZoneHalfWidthTicks < 0) errors.Add("candidate.zoneHalfWidthTicks must be non-negative.");
        if (Candidate.ConfirmationBufferTicks < 0) errors.Add("candidate.confirmationBufferTicks must be non-negative.");
        if (Candidate.FailureBeyondZoneTicks < 0) errors.Add("candidate.failureBeyondZoneTicks must be non-negative.");
        if (Candidate.MinimumProgressTicks > Candidate.MaximumProgressTicks)
            errors.Add("candidate.minimumProgressTicks cannot exceed candidate.maximumProgressTicks.");
        if (Candidate.MaximumForwardExcursionTicks < 0) errors.Add("candidate.maximumForwardExcursionTicks must be non-negative.");
        if (Candidate.ConfirmationPriceDwellMs <= 0) errors.Add("candidate.confirmationPriceDwellMs must be positive.");
        if (Candidate.FailurePriceDwellMs <= 0) errors.Add("candidate.failurePriceDwellMs must be positive.");
        if (Candidate.TimeoutMs <= 0) errors.Add("candidate.timeoutMs must be positive.");
        if (Candidate.CooldownMs < 0) errors.Add("candidate.cooldownMs must be non-negative.");
        if (Candidate.ConfirmationPriceDwellMs > Candidate.TimeoutMs)
            errors.Add("candidate.confirmationPriceDwellMs must fit inside candidate.timeoutMs; otherwise no candidate could ever confirm.");
        if (Candidate.WindowMs > Candidate.TimeoutMs)
            errors.Add("candidate.windowMs must not exceed candidate.timeoutMs.");

        if (Health.MaximumQuoteAgeMs <= 0) errors.Add("health.maximumQuoteAgeMs must be positive.");
        if (Health.MaximumSpreadTicks <= 0) errors.Add("health.maximumSpreadTicks must be positive.");
        if (Health.MinimumKnownSideFraction < 0d || Health.MinimumKnownSideFraction > 1d)
            errors.Add("health.minimumKnownSideFraction must lie in [0,1].");
        if (Health.MaximumProcessingLagMs <= 0) errors.Add("health.maximumProcessingLagMs must be positive.");
        if (Health.IngressCapacityEvents <= 0) errors.Add("health.ingressCapacityEvents must be positive.");

        if (Display.MaximumRequestedFramesPerSecond <= 0) errors.Add("display.maximumRequestedFramesPerSecond must be positive.");
        if (Display.PlotClip <= 0d) errors.Add("display.plotClip must be positive.");
        if (Display.NeutralVisualBand < 0d) errors.Add("display.neutralVisualBand must be non-negative.");
        if (Display.TrailMs <= 0) errors.Add("display.trailMs must be positive.");

        if (OptionalModules.ReconciliationIntervalMs <= 0) errors.Add("optionalModules.reconciliationIntervalMs must be positive.");
        if (OptionalModules.ForecastHorizonMs <= 0) errors.Add("optionalModules.forecastHorizonMs must be positive.");
        if (OptionalModules.ResidualScaleFloorTicks <= 0d) errors.Add("optionalModules.residualScaleFloorTicks must be positive.");

        if (Alerts.AutomaticTradingEnabled)
            errors.Add("alerts.automaticTradingEnabled must be false: this indicator is read-only and contains no order path.");

        if (Instrument.TickSize is { } ts && ts <= 0m) errors.Add("instrument.tickSize must be positive.");

        return errors;
    }

    /// <summary>
    /// Values whose absence keeps the engine in SetupRequired. These are owner inputs; the
    /// engine never substitutes another instrument's metadata or a synthetic baseline.
    /// </summary>
    public IReadOnlyList<MissingInput> MissingInputs()
    {
        var missing = new List<MissingInput>();

        if (Instrument.InstrumentKey is null || string.IsNullOrWhiteSpace(Instrument.InstrumentKey.Value.Symbol))
            missing.Add(new MissingInput("instrument.instrumentKey", "dated contract",
                "The chart has not reported an instrument yet."));

        if (Instrument.TickSize is null)
            missing.Add(new MissingInput("instrument.tickSize", "validated tick size",
                "The chart has not reported instrument metadata yet."));

        if (Recording.RequireRecorderForResearch && string.IsNullOrWhiteSpace(Recording.Directory))
            missing.Add(new MissingInput("recording.directory", "approved recording location",
                "Set \"Recording directory\", or clear it to disable recording entirely."));

        if (Recording.RequireRecorderForResearch && !Recording.PermissionConfirmed)
            missing.Add(new MissingInput("recording.permissionConfirmed", "approval to record",
                "Tick \"I approve recording to that directory\". Recording market data is your decision " +
                "and your market-data rights."));

        return missing;
    }

    /// <summary>The same list rendered as text, for logs and tests.</summary>
    public IReadOnlyList<string> MissingRequiredInputs()
        => MissingInputs().Select(m => m.Key + " (" + m.What + ")").ToList();

    /// <summary>
    /// Stable hash over the fields that can change a signal. Stamped on every transition so
    /// a log can never be misread as having come from a different configuration.
    /// </summary>
    public string Hash()
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.Append(Version).Append('|').Append(ConfigurationVersion).Append('|')
          .Append(Timing.ClockMode).Append('|').Append(Timing.DecisionIntervalMs).Append('|')
          .Append(string.Join(",", Timing.WindowsMs)).Append('|').Append(Timing.WarmupMs).Append('|')
          .Append(Timing.LateTickPolicy).Append('|')
          .Append(Baseline.LookbackSessions).Append('|').Append(Baseline.MinimumSessions).Append('|')
          .Append(Baseline.MinimumSamplesPerBucketWindow).Append('|').Append(Baseline.BucketMinutes).Append('|')
          .Append(Baseline.QuantileMethod).Append('|').Append(Baseline.AllowCrossExpiry).Append('|')
          .Append(Candidate.WindowMs).Append('|').Append(Candidate.ZoneHalfWidthTicks).Append('|')
          .Append(Candidate.AttackerVolumeQuantile.ToString("R", c)).Append('|').Append(Candidate.QuantileComparison).Append('|')
          .Append(Candidate.MinimumProgressTicks).Append('|').Append(Candidate.MaximumProgressTicks).Append('|')
          .Append(Candidate.MaximumForwardExcursionTicks).Append('|')
          .Append(Candidate.MinimumZoneVolumeFraction.ToString("R", c)).Append('|')
          .Append(Candidate.ConfirmationBufferTicks).Append('|').Append(Candidate.FailureBeyondZoneTicks).Append('|')
          .Append(Candidate.ConfirmationPriceDwellMs).Append('|').Append(Candidate.FailurePriceDwellMs).Append('|')
          .Append(Candidate.TimeoutMs).Append('|').Append(Candidate.CooldownMs).Append('|')
          .Append(Candidate.LevelPolicy).Append('|').Append(Candidate.ConfirmationPrice).Append('|')
          .Append(Health.MaximumQuoteAgeMs).Append('|').Append(Health.MaximumSpreadTicks).Append('|')
          .Append(Health.MinimumKnownSideFraction.ToString("R", c)).Append('|')
          .Append(Health.MaximumProcessingLagMs).Append('|').Append(Health.IngressCapacityEvents).Append('|')
          .Append(Health.RequireCompleteQuotePath).Append('|')
          .Append(Session.Timezone).Append('|').Append(Session.StartLocal).Append('|').Append(Session.EndLocalExclusive).Append('|')
          .Append(OptionalModules.ResponseRegressionEnabled).Append('|').Append(OptionalModules.MboEvidenceEnabled).Append('|')
          .Append(OptionalModules.ProbabilitiesEnabled).Append('|').Append(OptionalModules.ForecastHorizonMs).Append('|')
          .Append(Instrument.TickSize?.ToString(c) ?? "null").Append('|')
          .Append(Instrument.InstrumentKey?.ToString() ?? "null").Append('|')
          .Append(Versioning.FeatureFormulaVersion);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// True when the change alters signal-affecting behaviour, which must expire active
    /// candidates rather than silently re-evaluating them under new rules.
    /// </summary>
    public bool IsFeatureChangeFrom(Config other) => Hash() != other.Hash();
}
