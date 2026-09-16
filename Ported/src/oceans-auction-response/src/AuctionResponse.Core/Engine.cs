using System.Globalization;

namespace AuctionResponse.Core;

public sealed record ConfigChange(Config NewConfig, string Reason);

/// <summary>Result of one decision tick (Section 16).</summary>
public sealed record DecisionResult(
    ViewSnapshot View,
    IReadOnlyList<TransitionRecord> Transitions,
    EngineDiagnostics Diagnostics);

public sealed record EngineDiagnostics
{
    public required long DecisionSequence { get; init; }
    public required long ElapsedNs { get; init; }
    public required int EventsProcessed { get; init; }
    public required long DecideDurationNs { get; init; }
    /// <summary>Gate outcomes for every idle level that was evaluated this tick.</summary>
    public IReadOnlyDictionary<string, CandidateEvaluation> GateResults { get; init; }
        = new Dictionary<string, CandidateEvaluation>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Per-level runtime state. One level's lifecycle never touches another's.</summary>
internal sealed class LevelRuntime
{
    public required LevelDefinition Level { get; set; }
    public SetupState State { get; set; } = SetupState.Idle;
    public CandidateSnapshot? Snapshot { get; set; }
    public PriceDwellTracker? ConfirmationDwell { get; set; }
    public PriceDwellTracker? FailureDwell { get; set; }
    public long ArmedSequence { get; set; }
}

/// <summary>
/// The deterministic core. It holds no threads, no timers and no platform types: the same
/// event sequence fed in the same order always produces the same transitions, which is what
/// makes replay a real test rather than a re-run.
///
/// Contracts (Section 16): Accept, Decide, ChangeConfiguration, ChangeLevel. None is a host
/// override; the adapter translates real callbacks into these.
/// </summary>
public sealed class Engine
{
    private Config _config;
    private readonly InstrumentKey _instrument;
    private readonly TickGrid _grid;
    private readonly SessionCalendar _calendar;
    private readonly BaselineResolver _baseline;
    private readonly OrderBookEvidenceTracker _mbo = new();
    private readonly LiveBaseline _liveBaseline = new();
    private readonly DescriptiveLabelTracker _labelTracker;

    private readonly Dictionary<int, TradeWindow> _trades = new();
    private readonly Dictionary<int, QuotePath> _quotes = new();
    private readonly Dictionary<int, BookFlowWindow> _flow = new();
    private readonly CumulativeDelta _cumulativeDelta = new();

    private readonly LevelManager _levels = new();
    private readonly CooldownRegistry _cooldown;
    private readonly Dictionary<string, LevelRuntime> _runtimes = new(StringComparer.Ordinal);

    private readonly List<HistoryEntry> _history = new();
    private readonly Deque<PlotPoint> _trail = new();
    private readonly HashSet<string> _emittedTransitions = new(StringComparer.Ordinal);

    private int _epoch = 1;
    private long _lastEventNs;
    private long _lastSequence;
    private long _epochStartNs;
    private DateTime _lastUtc = DateTime.UnixEpoch;
    private bool _sawTrade, _sawQuote, _sawDepth;
    private bool _faulted;
    private string _faultReason = "";
    private bool _recorderFaulted;
    private string? _recorderFaultReason;
    private int _backlog;
    private long _eventsAccepted, _tradeEvents, _quoteEvents, _depthEvents, _rejectedEvents;
    private long _processingLagNs;
    private bool _ingressOverflowed;
    private int _overflowCount;
    private string? _lastArmedLevelId;
    private long _lastSampleNs = long.MinValue;
    private string _currentSessionId = "";
    private bool _liveBaselineDirty = true;

    public Engine(Config config, InstrumentKey instrument, decimal tickSize, string feedMode = "Live", bool simulatedData = false)
    {
        _config = config;
        _instrument = instrument;
        _grid = new TickGrid(tickSize);
        _calendar = new SessionCalendar(config.Session);
        _baseline = new BaselineResolver(config);
        _cooldown = new CooldownRegistry(config.Candidate.CooldownMs);
        _labelTracker = new DescriptiveLabelTracker(config.Display.DescriptiveLabelDwellMs);
        FeedMode = feedMode;
        IsSimulatedData = simulatedData;

        foreach (var w in config.Timing.WindowsMs)
        {
            _trades[w] = new TradeWindow(w);
            _quotes[w] = new QuotePath(w);
            _flow[w] = new BookFlowWindow(w);
        }
    }

    /// <summary>True once a price-level depth update has actually been observed. The adapter
    /// sets the MBP capability from observation rather than from the presence of an API.</summary>
    public bool HasObservedDepth => _sawDepth;

    public string FeedMode { get; }
    public bool IsSimulatedData { get; }
    public Config Config => _config;
    public TickGrid Grid => _grid;
    public SessionCalendar Calendar => _calendar;
    public int ConnectionEpoch => _epoch;
    public IReadOnlyList<HistoryEntry> History => _history;

    /// <summary>Capability flags, set by the adapter from what it has actually observed.</summary>
    public CapabilityFlags Capabilities { get; set; } = new();

    public void LoadBaseline(BaselineArtifact? artifact)
    {
        _baseline.Load(artifact, _instrument, _grid.TickSize, FeedMode);
        ExternalBaselineLoaded = artifact is not null;
    }

    /// <summary>True when a baseline file was supplied, rather than one the engine learned.</summary>
    public bool ExternalBaselineLoaded { get; private set; }

    /// <summary>The self-observed reference distribution. Persisted by the host between sessions.</summary>
    public LiveBaseline LiveBaseline => _liveBaseline;

    /// <summary>Progress toward a usable self-observed baseline, for the status line.</summary>
    public string LiveBaselineProgress => _liveBaseline.ProgressLabel(_currentSessionId, _config);

    public void ReportIngress(int backlog, bool overflowed, int overflowCount, long processingLagNs)
    {
        _backlog = backlog;
        _ingressOverflowed = overflowed;
        _overflowCount = overflowCount;
        _processingLagNs = processingLagNs;
        if (overflowed && !_faulted) Fault("ingress queue overflowed; events were dropped");
    }

    /// <summary>A recorder failure puts the research profile in Faulted: results stop being auditable.</summary>
    public void ReportRecorderFault(string reason)
    {
        _recorderFaulted = true;
        _recorderFaultReason = reason;
    }

    public void Fault(string reason)
    {
        _faulted = true;
        _faultReason = reason;
        foreach (var w in _config.Timing.WindowsMs) _quotes[w].MarkInterval(_lastEventNs, reason);
    }

    // ---------------------------------------------------------------- Accept

    /// <summary>
    /// Consumes one canonical event in ingress order. Never allocates a decision: features
    /// and states are produced only by <see cref="Decide"/>.
    /// </summary>
    public void Accept(MarketEvent ev)
    {
        _lastSequence = ev.EventSequence;
        _eventsAccepted++;
        if (ev.ReceiveElapsedNs >= _lastEventNs) _lastEventNs = ev.ReceiveElapsedNs;
        if (ev.ReceiveUtc > _lastUtc) _lastUtc = ev.ReceiveUtc;

        if ((ev.Quality & QualityFlag.AfterOverflow) != 0 && !_faulted)
            Fault("events arrived after an ingress overflow");

        switch (ev.Kind)
        {
            case EventKind.Trade: AcceptTrade(ev); break;
            case EventKind.BestQuote: AcceptQuote(ev); break;
            case EventKind.DepthSnapshot:
            case EventKind.MboSnapshot:
                // A snapshot initialises state. It contributes no flow, no OFI and no volume.
                break;
            case EventKind.DepthChange: _sawDepth = true; _depthEvents++; break;
            case EventKind.MboChange: AcceptMbo(ev); break;
            case EventKind.Connection: AcceptConnection(ev); break;
            case EventKind.Health:
                if (ev.Detail is { Length: > 0 } d) Fault(d);
                break;
            case EventKind.LevelDefinition:
            case EventKind.Configuration:
            case EventKind.DecisionTick:
                break;
        }
    }

    private void AcceptTrade(MarketEvent ev)
    {
        if ((ev.Quality & QualityFlag.OffGrid) != 0) { _rejectedEvents++; return; }   // never rounded into validity
        if (ev.OriginalQuantity is not { } qty || qty <= 0m) { _rejectedEvents++; return; }
        if (ev.PriceTicks is not { } ticks) { _rejectedEvents++; return; }

        _tradeEvents++;
        _sawTrade = true;
        foreach (var w in _config.Timing.WindowsMs) _trades[w].Add(ev.ReceiveElapsedNs, ev.Direction, qty, ticks);
        _cumulativeDelta.Add(ev.Direction, qty);
    }

    private void AcceptQuote(MarketEvent ev)
    {
        if (ev.BidTicks is not { } bid || ev.AskTicks is not { } ask) { _rejectedEvents++; return; }
        if (ev.BidQuantity is not { } bq || ev.AskQuantity is not { } aq) { _rejectedEvents++; return; }

        _quoteEvents++;
        _sawQuote = true;
        var guard = _config.Health.MaximumQuoteAgeMs;
        foreach (var w in _config.Timing.WindowsMs)
        {
            var path = _quotes[w];
            path.ApplyBestQuote(ev.ReceiveElapsedNs, bid, bq, ask, aq, guard);
            if (path.Current is { } state) _flow[w].Accept(state, contributesFlow: (ev.Quality & QualityFlag.SnapshotInitialisation) == 0);
        }

        // Dwell trackers advance on EVERY quote, not only at decision ticks.
        UpdateDwells(ev.ReceiveElapsedNs);
    }

    private void AcceptMbo(MarketEvent ev)
    {
        if (!_config.OptionalModules.MboEvidenceEnabled) return;
        if (!Capabilities.MarketByOrderVerified) return;
        if (ev.OrderId is null || ev.PriceTicks is not { } ticks || ev.Side is not { } side) return;
        if (ev.OriginalQuantity is not { } qty) return;
        if (!long.TryParse(ev.OrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return;

        var kind = (MboUpdateKind)(ev.RawUpdateType ?? (int)MboUpdateKind.Change);
        _mbo.Apply(_epoch, id, side, ticks, qty, kind, ev.ReceiveElapsedNs);
    }

    private void AcceptConnection(MarketEvent ev)
    {
        // Reconnect: new epoch, terminate candidates as DataInterrupted, rebuild, warm up.
        _epoch = ev.ConnectionEpoch;
        _epochStartNs = ev.ReceiveElapsedNs;
        foreach (var w in _config.Timing.WindowsMs)
        {
            _trades[w].Clear();
            _quotes[w].Clear();
            _flow[w].Clear();
        }
        _mbo.ResetEpoch();
        _labelTracker.Reset();
        _trail.Clear();
        _cumulativeDelta.Reset();
        _sawTrade = _sawQuote = _sawDepth = false;
        _eventsAccepted = _tradeEvents = _quoteEvents = _depthEvents = _rejectedEvents = 0;
        _faulted = false;
        _faultReason = "";
        PendingEpochInterruption = true;
    }

    /// <summary>Set when a reconnect happened; the next decision tick terminates candidates.</summary>
    public bool PendingEpochInterruption { get; private set; }

    private void UpdateDwells(long ns)
    {
        var path = _quotes[_config.Candidate.WindowMs];
        var current = path.Current;

        var unhealthySince = path.LastUnhealthyNs;

        foreach (var rt in _runtimes.Values)
        {
            if (rt.State != SetupState.Candidate || rt.Snapshot is null) continue;

            if (current is not { } state)
            {
                rt.ConfirmationDwell?.Break();
                rt.FailureDwell?.Break();
                continue;
            }

            // A dwell that began before a known stale or invalid interval was never
            // observed continuously, so it cannot be carried across that interval.
            BreakIfStale(rt.ConfirmationDwell!, unhealthySince);
            BreakIfStale(rt.FailureDwell!, unhealthySince);

            var u = rt.Snapshot.Orientation * state.MidHalfTicks;
            rt.ConfirmationDwell!.Observe(ns, u <= rt.Snapshot.Boundaries.ConfirmationHalfTicks);
            rt.FailureDwell!.Observe(ns, u >= rt.Snapshot.Boundaries.FailureHalfTicks);
        }
    }

    private static void BreakIfStale(PriceDwellTracker dwell, long unhealthySinceNs)
    {
        if (dwell.StartedAtNs is { } started && unhealthySinceNs >= started) dwell.Break();
    }

    // ------------------------------------------------------- Configuration

    /// <summary>
    /// A feature change increments the configuration version and EXPIRES active candidates:
    /// a candidate armed under one rule is never resolved under another.
    /// </summary>
    public IReadOnlyList<string> ChangeConfiguration(ConfigChange change)
    {
        var errors = change.NewConfig.Validate();
        if (errors.Count > 0) return errors;

        var isFeatureChange = change.NewConfig.IsFeatureChangeFrom(_config);
        _config = change.NewConfig;
        if (isFeatureChange) ConfigurationChangedPending = true;
        return Array.Empty<string>();
    }

    public bool ConfigurationChangedPending { get; private set; }

    public LevelChange? ChangeLevel(LevelChange change)
    {
        switch (change.Kind)
        {
            case LevelChangeKind.Added:
            case LevelChangeKind.Replaced:
                var added = _levels.Add(change.Level);
                _runtimes[change.Level.LevelId] = new LevelRuntime { Level = added.Level };
                return added;

            case LevelChangeKind.Removed:
            case LevelChangeKind.Expired:
                var removed = _levels.Remove(change.Level.LevelId, change.Reason);
                if (removed is not null && _runtimes.TryGetValue(change.Level.LevelId, out var rt))
                    rt.Level = removed.Level;
                return removed;
        }
        return null;
    }

    public LevelChange AddLevel(LevelDefinition level) => ChangeLevel(new LevelChange(LevelChangeKind.Added, level, "declared"))!;

    /// <summary>
    /// Reconciles the declared levels with what the chart currently shows.
    ///
    /// Adds lines that are new, removes lines that are gone, and leaves untouched anything at
    /// the same price — so an existing candidate is NOT restarted just because the set was
    /// re-read. A level that moves is a different level: the old one terminates and the new
    /// one starts its own window, because a boundary frozen against the old price would be
    /// measuring something that no longer exists.
    /// </summary>
    public IReadOnlyList<LevelChange> SyncLevels(IReadOnlyList<LevelDefinition> desired, long nowNs)
    {
        var changes = new List<LevelChange>();
        var wanted = desired.ToDictionary(l => l.LevelId, StringComparer.Ordinal);

        foreach (var existing in _levels.Levels.ToList())
        {
            if (wanted.TryGetValue(existing.LevelId, out var match) && match.PriceTicks == existing.PriceTicks)
                continue;

            var removed = _levels.Remove(existing.LevelId, "level removed from the chart");
            if (removed is not null) changes.Add(removed);
        }

        foreach (var level in desired)
        {
            var current = _levels.Get(level.LevelId);
            if (current is not null && current.PriceTicks == level.PriceTicks) continue;

            var added = _levels.Add(level with { EffectiveNs = nowNs, ConnectionEpoch = _epoch });
            _runtimes[level.LevelId] = new LevelRuntime { Level = added.Level };
            changes.Add(added);
        }

        return changes;
    }

    // ---------------------------------------------------------------- Decide

    /// <summary>
    /// Resolves every level at one decision tick, in the normative priority order, and
    /// publishes a bounded immutable view. This is the ONLY place transitions are created.
    /// </summary>
    public DecisionResult Decide(DecisionTick tick)
    {
        var startNs = tick.ElapsedNs;
        var transitions = new List<TransitionRecord>();
        var gateResults = new Dictionary<string, CandidateEvaluation>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var w in _config.Timing.WindowsMs)
        {
            _trades[w].Advance(tick.ElapsedNs);
            _quotes[w].Advance(tick.ElapsedNs, _config.Health.MaximumQuoteAgeMs);
            _flow[w].Advance(tick.ElapsedNs);
        }

        // Expiring a level is a level-lifecycle event, and it terminates its candidate.
        foreach (var expired in _levels.ExpireDue(tick.ElapsedNs))
            notes.Add("level " + expired.Level.LevelId + " expired");

        var health = BuildHealth(tick);
        var live = BuildFeatures(tick, health);
        var sessionEligible = _calendar.IsEligible(tick.ReceiveUtc, out var sessionReason);

        AccumulateLiveBaseline(tick, sessionEligible);

        foreach (var rt in _runtimes.Values.ToList())
        {
            if (rt.State == SetupState.Candidate && rt.Snapshot is not null)
                ResolveCandidate(rt, tick, health, live, sessionEligible, transitions);
        }

        // Arming is considered only after terminal resolution, so a candidate that ended on
        // this tick starts its cooldown before anything else is allowed to arm at that level.
        foreach (var level in _levels.ActiveIn(_epoch, tick.ElapsedNs))
        {
            if (!_runtimes.TryGetValue(level.LevelId, out var rt))
            {
                rt = new LevelRuntime { Level = level };
                _runtimes[level.LevelId] = rt;
            }
            rt.Level = level;

            if (rt.State == SetupState.Candidate) continue;

            var evaluation = TryArm(rt, tick, health, live, sessionEligible, sessionReason, transitions);
            gateResults[level.LevelId] = evaluation;
        }

        PendingEpochInterruption = false;
        ConfigurationChangedPending = false;

        UpdateTrail(live, tick);
        var label = _labelTracker.Update(
            DescriptiveLabelTracker.Propose(live.Delta, live.Response, live.Plot), tick.ElapsedNs);

        var view = BuildView(tick, health, live, label);

        var diagnostics = new EngineDiagnostics
        {
            DecisionSequence = tick.EventSequence,
            ElapsedNs = tick.ElapsedNs,
            EventsProcessed = 0,
            DecideDurationNs = Math.Max(0, tick.ElapsedNs - startNs),
            GateResults = gateResults,
            Notes = notes
        };

        return new DecisionResult(view, transitions, diagnostics);
    }

    /// <summary>
    /// Records one non-overlapping window sample per configured window length, and rebuilds
    /// the frozen view of the learned baseline when the session rolls over.
    /// </summary>
    private void AccumulateLiveBaseline(DecisionTick tick, bool sessionEligible)
    {
        var sessionId = _calendar.SessionId(tick.ReceiveUtc);
        if (!string.Equals(sessionId, _currentSessionId, StringComparison.Ordinal))
        {
            _currentSessionId = sessionId;
            _liveBaselineDirty = true;
        }

        // Only a healthy, in-session window is a valid sample. A stale or degraded interval
        // would poison the very distribution the alert is measured against.
        if (sessionEligible && !ExternalBaselineLoaded &&
            health_IsSampleable() && _calendar.MinutesFromStart(tick.ReceiveUtc) is { } minute)
        {
            foreach (var window in _config.Timing.WindowsMs)
            {
                var windowNs = (long)window * 1_000_000L;
                if (_lastSampleNs != long.MinValue && tick.ElapsedNs - _lastSampleNs < windowNs) continue;

                var trades = _trades[window];
                var path = _quotes[window];
                if (!path.IsCompleteAndFresh(tick.ElapsedNs)) continue;

                var bucket = minute / _config.Baseline.BucketMinutes * _config.Baseline.BucketMinutes;
                var response = path.ResponseHalfTicks(tick.ElapsedNs) is { } rh ? Math.Abs(rh / 2d) : (double?)null;

                _liveBaseline.Record(sessionId, bucket, window,
                    (double)trades.Buy, (double)trades.Sell,
                    Math.Abs((double)trades.Delta), response);
            }

            var shortest = _config.Timing.WindowsMs.Min();
            if (_lastSampleNs == long.MinValue || tick.ElapsedNs - _lastSampleNs >= (long)shortest * 1_000_000L)
            {
                _lastSampleNs = tick.ElapsedNs;
                _liveBaselineDirty = true;
            }
        }

        // Rebuild the served artifact occasionally: it only changes when a session rolls or
        // new samples land, and rebuilding on every tick would be pure waste.
        if (!_liveBaselineDirty || ExternalBaselineLoaded) return;
        _liveBaselineDirty = false;

        var learned = _liveBaseline.BuildExcluding(_currentSessionId, _config, _instrument, _grid.TickSize,
            FeedMode, _calendar.TimezoneId + " " + _config.Session.StartLocal + "-" + _config.Session.EndLocalExclusive);

        if (learned is not null) _baseline.Load(learned, _instrument, _grid.TickSize, FeedMode);
    }

    private bool health_IsSampleable()
    {
        var window = _config.Candidate.WindowMs;
        var path = _quotes[window];
        var trades = _trades[window];
        return _sawTrade && _sawQuote && trades.Total > 0m && path.StateCount > 0 && !_faulted;
    }

    private void ResolveCandidate(
        LevelRuntime rt, DecisionTick tick, HealthSnapshot health, FeatureSnapshot live,
        bool sessionEligible, List<TransitionRecord> transitions)
    {
        var snap = rt.Snapshot!;
        var ageMs = snap.AgeMs(tick.ElapsedNs);

        var healthy = !PendingEpochInterruption
                   && !_faulted
                   && health.State is DataState.Ready or DataState.Warmup
                   && snap.ConnectionEpoch == _epoch;

        // The flow half of confirmation is the LATEST five-second reading; it is not
        // required to have stayed opposite for the whole dwell second.
        var oppositeFlow = live.Delta.IsAvailable && snap.Orientation * live.Delta.Value!.Value < 0d;

        var inputs = new TransitionInputs(
            AgeMs: ageMs,
            Healthy: healthy,
            SessionEligible: sessionEligible,
            LevelStillDeclared: _levels.Get(rt.Level.LevelId) is not null,
            ConfigurationUnchanged: !ConfigurationChangedPending,
            FailureDwellComplete: rt.FailureDwell!.IsComplete(tick.ElapsedNs),
            ConfirmationDwellComplete: rt.ConfirmationDwell!.IsComplete(tick.ElapsedNs),
            OppositeFlow: oppositeFlow);

        var decision = TerminalTransition.Resolve(inputs, _config.Candidate.TimeoutMs);
        if (decision.State == SetupState.Candidate) return;

        Transition(rt, decision.State, decision.Reason, tick, live, health, transitions);
    }

    private CandidateEvaluation TryArm(
        LevelRuntime rt, DecisionTick tick, HealthSnapshot health, FeatureSnapshot live,
        bool sessionEligible, string sessionReason, List<TransitionRecord> transitions)
    {
        var cfg = _config.Candidate;
        var level = rt.Level;
        var o = level.Orientation;
        var window = cfg.WindowMs;
        var (zoneLow, zoneHigh) = level.Zone(cfg.ZoneHalfWidthTicks);

        var path = _quotes[window];
        var trades = _trades[window];
        var windowNs = (long)window * 1_000_000L;

        var attacker = o > 0 ? trades.Buy : trades.Sell;
        var attackerDir = o > 0 ? AggressorDirection.Buy : AggressorDirection.Sell;
        var minutes = _calendar.MinutesFromStart(tick.ReceiveUtc);
        var quantile = minutes is { } m ? _baseline.AttackerVolumeQuantile(m, window, o) : default;

        var cooling = _cooldown.IsCoolingDown(level.LevelId, tick.ElapsedNs);
        var lifecycleClear = !cooling && rt.State != SetupState.Candidate;

        var healthy = health.State == DataState.Ready
                   && Capabilities.CanSatisfyBaselineRule
                   && (!_config.Health.RequireCompleteQuotePath || path.IsCompleteAndFresh(tick.ElapsedNs));

        var inputs = new CandidateInputs
        {
            Orientation = o,
            LevelTicks = level.PriceTicks,
            ZoneLowTicks = zoneLow,
            ZoneHighTicks = zoneHigh,
            MidHalfTicks = path.Current?.MidHalfTicks,
            AttackerVolume = attacker,
            AttackerVolumeQuantile = quantile.IsReady ? quantile.Value : null,
            Delta = trades.Delta,
            OrientedProgressHalfTicks = path.ResponseHalfTicks(tick.ElapsedNs) is { } rh ? o * rh : null,
            OrientedExcursionHalfTicks = path.MaxForwardExcursionHalfTicks(tick.ElapsedNs, o),
            ZoneAttackerVolume = trades.VolumeInZone(zoneLow, zoneHigh, attackerDir),
            OrientedMinimumHalfTicks = path.OrientedMinimumHalfTicks(tick.ElapsedNs, o),
            DataHealthy = healthy,
            DataReason = health.State != DataState.Ready ? health.StateReason
                       : !Capabilities.CanSatisfyBaselineRule ? "baseline rule needs trades AND quotes"
                       : "quote path incomplete over the window",
            SessionEligible = sessionEligible,
            LevelExistedBeforeWindow = level.ExistedBefore(tick.ElapsedNs - windowNs) && level.ConnectionEpoch <= _epoch,
            LifecycleClear = lifecycleClear,
            LifecycleReason = cooling
                ? "level cooling down for another " + (_cooldown.RemainingNs(level.LevelId, tick.ElapsedNs) / 1_000_000L) + "ms"
                : rt.State == SetupState.Candidate ? "candidate already active at this level" : "clear",
            SideQuality = live.SideQuality
        };

        var evaluation = CandidateRule.Evaluate(inputs, cfg, _config.Health.MinimumKnownSideFraction);
        if (!evaluation.Arms)
        {
            if (rt.State.IsTerminal() && !cooling) rt.State = SetupState.Idle;
            return evaluation;
        }

        var boundaries = CandidateRule.Boundaries(
            o, inputs.OrientedMinimumHalfTicks!.Value, zoneLow, zoneHigh,
            cfg.ConfirmationBufferTicks, cfg.FailureBeyondZoneTicks);

        var candidateId = level.LevelId + "#" + tick.EventSequence.ToString(CultureInfo.InvariantCulture);
        var snapshot = new CandidateSnapshot
        {
            CandidateId = candidateId,
            LevelId = level.LevelId,
            Orientation = o,
            ConnectionEpoch = _epoch,
            ArmedAtNs = tick.ElapsedNs,
            ArmedAtUtc = tick.ReceiveUtc,
            ArmedAtSequence = tick.EventSequence,
            LevelTicks = level.PriceTicks,
            ZoneLowTicks = zoneLow,
            ZoneHighTicks = zoneHigh,
            StartMidHalfTicks = path.StartState?.MidHalfTicks ?? path.Current!.Value.MidHalfTicks,
            Boundaries = boundaries,
            FrozenEvidence = live,
            ConfigurationHash = _config.Hash(),
            BaselineHash = _baseline.Artifact?.Sha256,
            ModelHash = null
        };

        rt.Snapshot = snapshot;
        rt.State = SetupState.Candidate;
        rt.ArmedSequence = tick.EventSequence;
        rt.ConfirmationDwell = new PriceDwellTracker(cfg.ConfirmationPriceDwellMs);
        rt.FailureDwell = new PriceDwellTracker(cfg.FailurePriceDwellMs);
        _lastArmedLevelId = level.LevelId;

        Emit(rt, SetupState.Idle, SetupState.Candidate, "candidate armed", tick, live, health, transitions);
        return evaluation;
    }

    private void Transition(
        LevelRuntime rt, SetupState newState, string reason, DecisionTick tick,
        FeatureSnapshot live, HealthSnapshot health, List<TransitionRecord> transitions)
    {
        var old = rt.State;
        rt.State = newState;

        // Cooldown begins at ANY terminal transition, keyed to this level alone.
        if (newState.IsTerminal()) _cooldown.Start(rt.Level.LevelId, tick.ElapsedNs);

        rt.ConfirmationDwell?.Reset();
        rt.FailureDwell?.Reset();

        Emit(rt, old, newState, reason, tick, live, health, transitions);
    }

    private void Emit(
        LevelRuntime rt, SetupState oldState, SetupState newState, string reason,
        DecisionTick tick, FeatureSnapshot live, HealthSnapshot health, List<TransitionRecord> transitions)
    {
        var snap = rt.Snapshot;
        var candidateId = snap?.CandidateId ?? rt.Level.LevelId + "#idle";
        var transitionId = candidateId + ">" + newState + "@" + tick.EventSequence.ToString(CultureInfo.InvariantCulture);

        // Deduplicate by instrument, epoch, candidate and transition type.
        var dedupe = _instrument + "|" + _epoch + "|" + candidateId + "|" + newState;
        if (!_emittedTransitions.Add(dedupe)) return;

        var record = new TransitionRecord
        {
            TransitionId = transitionId,
            CandidateId = candidateId,
            LevelId = rt.Level.LevelId,
            // The DECISION TICK is the detection timestamp, never the earlier extreme.
            DetectionUtc = tick.ReceiveUtc,
            DetectionElapsedNs = tick.ElapsedNs,
            SourceSequence = tick.EventSequence,
            Instrument = _instrument,
            ConnectionEpoch = _epoch,
            Orientation = rt.Level.Orientation,
            OldState = oldState,
            NewState = newState,
            Reason = reason,
            LevelTicks = rt.Level.PriceTicks,
            ZoneLowTicks = snap?.ZoneLowTicks ?? rt.Level.Zone(_config.Candidate.ZoneHalfWidthTicks).Low,
            ZoneHighTicks = snap?.ZoneHighTicks ?? rt.Level.Zone(_config.Candidate.ZoneHalfWidthTicks).High,
            ConfirmationHalfTicks = snap?.Boundaries.ConfirmationHalfTicks ?? 0,
            FailureHalfTicks = snap?.Boundaries.FailureHalfTicks ?? 0,
            Features = live,
            DataQuality = health.State,
            ConfigurationHash = _config.Hash(),
            BaselineHash = _baseline.Artifact?.Sha256,
            ModelHash = null
        };

        transitions.Add(record);
        _history.Insert(0, new HistoryEntry(
            transitionId, tick.ReceiveUtc, tick.ElapsedNs, rt.Level.LevelId,
            rt.Level.Orientation, newState, reason));

        const int maxHistory = 200;
        if (_history.Count > maxHistory) _history.RemoveRange(maxHistory, _history.Count - maxHistory);
    }

    // ---------------------------------------------------------------- Views

    private HealthSnapshot BuildHealth(DecisionTick tick)
    {
        var window = _config.Candidate.WindowMs;
        var path = _quotes[window];
        var trades = _trades[window];
        var hc = _config.Health;

        var missing = _config.MissingInputs();
        var sideQuality = trades.SideQuality(tick.ElapsedNs);

        Measure bidAge = path.LastBidUpdateNs == long.MinValue
            ? Measure.Unavailable("ms", 0, tick.ElapsedNs, "no bid observed yet")
            : Measure.Of((tick.ElapsedNs - path.LastBidUpdateNs) / 1e6, "ms", 0, tick.ElapsedNs);
        Measure askAge = path.LastAskUpdateNs == long.MinValue
            ? Measure.Unavailable("ms", 0, tick.ElapsedNs, "no ask observed yet")
            : Measure.Of((tick.ElapsedNs - path.LastAskUpdateNs) / 1e6, "ms", 0, tick.ElapsedNs);

        Measure spread = path.Current is { } q
            ? Measure.Of(q.SpreadTicks, "ticks", 0, tick.ElapsedNs)
            : Measure.Unavailable("ticks", 0, tick.ElapsedNs, "no coherent quote");

        var lag = Measure.Of(_processingLagNs / 1e6, "ms", 0, tick.ElapsedNs);

        var quoteGuardTripped =
            (bidAge.IsAvailable && bidAge.Value!.Value > hc.MaximumQuoteAgeMs) ||
            (askAge.IsAvailable && askAge.Value!.Value > hc.MaximumQuoteAgeMs);

        var minutes = _calendar.MinutesFromStart(tick.ReceiveUtc);
        var baselineLookup = minutes is { } m
            ? _baseline.AttackerVolumeQuantile(m, window, 1)
            : new BaselineLookup(BaselineStatus.Warmup, null, 0, "outside the session");

        var (state, reason) = ResolveDataState(tick, missing, baselineLookup, spread, sideQuality, lag, quoteGuardTripped);

        return new HealthSnapshot
        {
            State = state,
            StateReason = reason,
            Capabilities = Capabilities,
            BidQuoteAge = bidAge,
            AskQuoteAge = askAge,
            Spread = spread,
            SideQuality = sideQuality,
            ProcessingLag = lag,
            Backlog = _backlog,
            IngressCapacity = hc.IngressCapacityEvents,
            IngressOverflowed = _ingressOverflowed,
            OverflowCount = _overflowCount,
            RecorderFaulted = _recorderFaulted,
            RecorderFaultReason = _recorderFaultReason,
            ConnectionEpoch = _epoch,
            EpochAgeMs = (tick.ElapsedNs - _epochStartNs) / 1_000_000L,
            BaselineStatus = baselineLookup.Status,
            BaselineReason = baselineLookup.Reason,
            QuoteFreshnessGuardTripped = quoteGuardTripped,
            EventsAccepted = _eventsAccepted,
            TradeEvents = _tradeEvents,
            QuoteEvents = _quoteEvents,
            DepthEvents = _depthEvents,
            RejectedEvents = _rejectedEvents,
            MissingInputs = missing
        };
    }

    private (DataState, string) ResolveDataState(
        DecisionTick tick, IReadOnlyList<MissingInput> missing, BaselineLookup baseline,
        Measure spread, Measure sideQuality, Measure lag, bool quoteGuardTripped)
    {
        if (_faulted) return (DataState.Faulted, _faultReason);
        if (missing.Count > 0)
            return (DataState.SetupRequired, "waiting on " + string.Join(", ", missing.Select(m => m.What)));
        if (!Capabilities.CanSatisfyBaselineRule)
            return (DataState.SetupRequired, "baseline setup alerts need both trades and quotes; this mode has " +
                (Capabilities.Trades ? "trades" : "no trades") + " and " + (Capabilities.Quotes ? "quotes" : "no quotes"));

        // Reconnect always requires a full warmup of fresh observation before Ready.
        var epochAgeMs = (tick.ElapsedNs - _epochStartNs) / 1_000_000L;
        if (epochAgeMs < _config.Timing.WarmupMs)
            return (DataState.Warmup, "warming up: " + epochAgeMs + "ms of " + _config.Timing.WarmupMs + "ms observed since the epoch began");
        if (!_sawTrade || !_sawQuote)
            return (DataState.Warmup, "waiting for first " + (!_sawTrade ? "trade" : "quote"));
        if (baseline.Status != BaselineStatus.Ready)
        {
            // When the engine is building its own reference, say so in sessions rather than
            // quoting an internal reason the reader cannot act on.
            if (!ExternalBaselineLoaded)
                return (DataState.Warmup, LiveBaselineProgress + " observed; nothing arms until the reference exists");
            return (DataState.Warmup, baseline.Reason ?? "baseline not ready");
        }

        if (quoteGuardTripped) return (DataState.Stale, "quote freshness guard");
        if (spread.IsAvailable && spread.Value!.Value > _config.Health.MaximumSpreadTicks)
            return (DataState.Degraded, "spread " + spread.Value.Value + " ticks exceeds " + _config.Health.MaximumSpreadTicks);

        // A window with no trades at all is quiet, not degraded: the side-quality guard is a
        // guard on a FRACTION, and there is no fraction over zero trades. The candidate rule
        // still refuses to arm, because its Data gate needs an available side quality.
        if (!sideQuality.IsAvailable)
            return (DataState.Ready, "no trades in the window; quotes fresh, baseline loaded");
        if (sideQuality.Value!.Value < _config.Health.MinimumKnownSideFraction)
            return (DataState.Degraded, "side quality " + sideQuality.Value.Value.ToString("0.###") + " below " + _config.Health.MinimumKnownSideFraction);
        if (lag.IsAvailable && lag.Value!.Value > _config.Health.MaximumProcessingLagMs)
            return (DataState.Degraded, "processing lag " + lag.Value.Value.ToString("0.#") + "ms exceeds " + _config.Health.MaximumProcessingLagMs + "ms");

        return (DataState.Ready, "trades and quotes fresh, baseline loaded");
    }

    private FeatureSnapshot BuildFeatures(DecisionTick tick, HealthSnapshot health)
    {
        var window = _config.Candidate.WindowMs;
        var trades = _trades[window];
        var path = _quotes[window];
        var flow = _flow[window];
        var ns = tick.ElapsedNs;

        var response = path.Response(ns);
        var imbalance = path.Current is { } q
            ? BookFlow.QueueImbalance(q.BidQuantity, q.AskQuantity, ns, window)
            : Measure.Unavailable("ratio", window, ns, "no coherent quote");

        var minutes = _calendar.MinutesFromStart(tick.ReceiveUtc);
        var (deltaScale, responseScale) = minutes is { } m
            ? _baseline.PlotScales(m, window)
            : (new BaselineLookup(BaselineStatus.Warmup, null, 0, "outside the session"),
               new BaselineLookup(BaselineStatus.Warmup, null, 0, "outside the session"));

        PlotPoint? plot = null;
        if (deltaScale.IsReady && responseScale.IsReady && response.IsAvailable)
            plot = PlotMath.Coordinates((double)trades.Delta, deltaScale.Value!.Value, response.Value!.Value, responseScale.Value!.Value, ns);

        // Zone execution is per level, so the live snapshot reports it for the level the
        // sidebar is showing: the most recently armed one, else the first declared.
        var focusLevel = (_lastArmedLevelId is not null ? _levels.Get(_lastArmedLevelId) : null)
                         ?? _levels.ActiveIn(_epoch, ns).FirstOrDefault();

        Measure zoneVolume = Measure.Unavailable("qty", window, ns, "no level declared");
        Measure zoneFraction = Measure.Unavailable("ratio", window, ns, "no level declared");

        if (focusLevel is not null)
        {
            var (zoneLow, zoneHigh) = focusLevel.Zone(_config.Candidate.ZoneHalfWidthTicks);
            var attackerDir = focusLevel.Orientation > 0 ? AggressorDirection.Buy : AggressorDirection.Sell;
            var attacker = focusLevel.Orientation > 0 ? trades.Buy : trades.Sell;
            var inZone = trades.VolumeInZone(zoneLow, zoneHigh, attackerDir);

            zoneVolume = Measure.Of((double)inZone, "qty", window, ns);
            zoneFraction = attacker > 0m
                ? Measure.Of((double)(inZone / attacker), "ratio", window, ns)
                : Measure.Unavailable("ratio", window, ns, "no attacker volume in window");
        }

        var snapshot = new FeatureSnapshot
        {
            AtNs = ns,
            AtUtc = tick.ReceiveUtc,
            AtSequence = tick.EventSequence,
            BuyVolume = Measure.Of((double)trades.Buy, "qty", window, ns),
            SellVolume = Measure.Of((double)trades.Sell, "qty", window, ns),
            UnknownVolume = Measure.Of((double)trades.Unknown, "qty", window, ns),
            TotalVolume = Measure.Of((double)trades.Total, "qty", window, ns),
            Delta = Measure.Of((double)trades.Delta, "qty", window, ns),
            NormalizedDelta = trades.NormalizedDelta(ns),
            SideQuality = trades.SideQuality(ns),
            Response = response,
            QueueImbalance = imbalance,
            Ofi = flow.Ofi(ns),
            MeanDepth = flow.MeanDepth(ns),
            DepthNormalizedOfi = flow.DepthNormalizedOfi(ns, 1m),
            Spread = health.Spread,
            ZoneAttackerVolume = zoneVolume,
            ZoneVolumeFraction = zoneFraction,
            CumulativeDelta = Measure.Of((double)_cumulativeDelta.Value, "qty", 0, ns),
            Plot = plot,
            // Optional modules stay unavailable with a REASON rather than reporting zero.
            ResidualEvidence = _config.OptionalModules.ResponseRegressionEnabled
                ? Measure.Unavailable("z", window, ns, "no validated response model is loaded")
                : Measure.Unavailable("z", window, ns, "response regression module disabled"),
            NetAdditionEvidence = _config.OptionalModules.MboEvidenceEnabled
                ? Measure.Unavailable("ratio", window, ns, _mbo.DisabledReason)
                : Measure.Unavailable("ratio", window, ns, "MBO evidence module disabled"),
            NetRemovalEvidence = _config.OptionalModules.MboEvidenceEnabled
                ? Measure.Unavailable("ratio", window, ns, _mbo.DisabledReason)
                : Measure.Unavailable("ratio", window, ns, "MBO evidence module disabled")
        };

        return snapshot;
    }

    private void UpdateTrail(FeatureSnapshot live, DecisionTick tick)
    {
        if (live.Plot is not { } p) return;
        _trail.PushBack(p);
        var cutoff = tick.ElapsedNs - (long)_config.Display.TrailMs * 1_000_000L;
        while (!_trail.IsEmpty && _trail.Front.AtNs < cutoff) _trail.PopFront();
    }

    private ViewSnapshot BuildView(DecisionTick tick, HealthSnapshot health, FeatureSnapshot live, DescriptiveLabel label)
    {
        var window = _config.Candidate.WindowMs;
        var trades = _trades[window];
        var mid = _quotes[window].Current?.MidHalfTicks;
        var minutes = _calendar.MinutesFromStart(tick.ReceiveUtc);

        var setups = new List<SetupStatus>();
        foreach (var rt in _runtimes.Values.OrderBy(r => r.Level.Ordinal))
        {
            var level = rt.Level;
            var o = level.Orientation;
            var (zoneLow, zoneHigh) = level.Zone(_config.Candidate.ZoneHalfWidthTicks);
            var attackerDir = o > 0 ? AggressorDirection.Buy : AggressorDirection.Sell;
            var attacker = o > 0 ? trades.Buy : trades.Sell;
            var inZone = trades.VolumeInZone(zoneLow, zoneHigh, attackerDir);

            var percentile = minutes is { } m
                ? _baseline.PercentileOf(m, window, o, (double)attacker)
                : null;

            setups.Add(new SetupStatus
            {
                LevelId = level.LevelId,
                State = rt.State,
                Orientation = o,
                AgeMs = rt.Snapshot is { } s && rt.State == SetupState.Candidate ? s.AgeMs(tick.ElapsedNs) : null,
                Candidate = rt.Snapshot,
                CooldownRemainingMs = _cooldown.RemainingNs(level.LevelId, tick.ElapsedNs) / 1_000_000L,
                PriceTicks = level.PriceTicks,
                ZoneLowTicks = zoneLow,
                ZoneHighTicks = zoneHigh,
                MidpointInZone = mid is { } mh && mh >= 2 * zoneLow && mh <= 2 * zoneHigh,
                AttackerVolume = Measure.Of((double)attacker, "qty", window, tick.ElapsedNs),
                ZoneVolume = Measure.Of((double)inZone, "qty", window, tick.ElapsedNs),
                AttackerPercentile = percentile is { } pct
                    ? Measure.Of(pct, "fraction", window, tick.ElapsedNs)
                    : Measure.Unavailable("fraction", window, tick.ElapsedNs, "no reference distribution yet")
            });
        }

        // Default to the most recently armed ACTIVE candidate; ties break by ordinal level id.
        var current = setups
            .Where(s => s.State == SetupState.Candidate)
            .OrderByDescending(s => s.Candidate?.ArmedAtSequence ?? 0)
            .ThenBy(s => s.LevelId, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? setups.FirstOrDefault(s => s.LevelId == _lastArmedLevelId)
            ?? setups.FirstOrDefault();

        return new ViewSnapshot
        {
            PublishedAtNs = tick.ElapsedNs,
            PublishedAtUtc = tick.ReceiveUtc,
            PublishedAtSequence = tick.EventSequence,
            Instrument = _instrument,
            IsSimulatedData = IsSimulatedData,
            ModeLabel = IsSimulatedData ? "SIMULATED DATA" : FeedMode,
            Health = health,
            Live = live,
            Current = current,
            AllSetups = setups,
            Levels = _levels.ActiveIn(_epoch, tick.ElapsedNs).ToList(),
            History = _history.ToList(),
            Trail = _trail.Items().ToList(),
            Label = label,
            Model = null,   // Baseline: absent. No invented percentages.
            ConfigurationHash = _config.Hash(),
            BaselineHash = _baseline.Artifact?.Sha256
        };
    }
}
