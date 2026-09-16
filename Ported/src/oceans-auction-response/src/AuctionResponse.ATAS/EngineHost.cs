using System.Diagnostics;
using AuctionResponse.Core;
using AuctionResponse.Replay;

namespace AuctionResponse.Atas;

/// <summary>
/// Owns everything that is NOT deterministic core logic: the ingress lock, the single
/// ordered worker, the decision timer, recording, and publication of the immutable view.
///
/// The split matters. A host callback here does nothing but stamp a monotonic time, build
/// an immutable record and enqueue it. All feature and state work happens on the worker,
/// and rendering only ever reads a published snapshot.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly Engine _engine;
    private readonly EventIngress _ingress;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _worker;
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<MarketEvent> _drainBuffer = new(4096);
    private readonly JsonlRecorder _recorder;
    private readonly int _decisionIntervalNs;

    private volatile ViewSnapshot _view;
    private volatile IReadOnlyList<LevelDefinition> _pendingLevels;
    private long _nextDecisionNs;
    private long _lastCallbackHandoffNs;
    private long _maxCallbackHandoffNs;
    private long _maxDecideNs;
    private long _decisionCount;
    private volatile bool _disposed;

    public EngineHost(Engine engine, Config config, JsonlRecorder recorder = null)
    {
        _engine = engine;
        _ingress = new EventIngress(config.Health.IngressCapacityEvents);
        _recorder = recorder;
        _decisionIntervalNs = config.Timing.DecisionIntervalMs * 1_000_000;
        _nextDecisionNs = _decisionIntervalNs;

        _view = EmptyView(engine);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AuctionResponse.Worker",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    /// <summary>The latest published view. Rendering reads this and nothing else.</summary>
    public ViewSnapshot View => _view;

    public Engine Engine => _engine;
    public EventIngress Ingress => _ingress;

    /// <summary>Monotonic elapsed nanoseconds. This is the feature clock (Section 5).</summary>
    public long NowNs => (long)(_clock.Elapsed.TotalMilliseconds * 1_000_000d);

    /// <summary>
    /// Hands the worker the levels currently drawn on the chart. Called from the UI timer;
    /// the worker applies them in sequence, so chart state never mutates the engine directly
    /// from another thread.
    /// </summary>
    public void SubmitLevels(IReadOnlyList<LevelDefinition> levels) => _pendingLevels = levels;

    /// <summary>Raised on the worker thread when transitions occur, for alerts and logging.</summary>
    public event Action<IReadOnlyList<TransitionRecord>> TransitionsEmitted;

    /// <summary>Raised when a new view is published, so the host can request a redraw.</summary>
    public event Action ViewPublished;

    public long MaxCallbackHandoffNs => Interlocked.Read(ref _maxCallbackHandoffNs);
    public long MaxDecideNs => Interlocked.Read(ref _maxDecideNs);
    public long DecisionCount => Interlocked.Read(ref _decisionCount);

    /// <summary>
    /// Enqueues one event. Called from host callback threads: it takes the short ingress
    /// lock and returns. It performs no I/O and never blocks on the worker.
    /// </summary>
    public void Submit(Func<long, MarketEvent> factory)
    {
        if (_disposed) return;

        var start = NowNs;
        var accepted = _ingress.TryEnqueue(factory, out var ev);
        if (accepted && _recorder is not null) { /* recording happens on the worker, not here */ }

        var elapsed = NowNs - start;
        _lastCallbackHandoffNs = elapsed;
        if (elapsed > Interlocked.Read(ref _maxCallbackHandoffNs))
            Interlocked.Exchange(ref _maxCallbackHandoffNs, elapsed);

        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void WorkerLoop()
    {
        while (!_cancel.IsCancellationRequested)
        {
            try
            {
                _wake.Wait(50, _cancel.Token);
            }
            catch (OperationCanceledException) { break; }

            try { Pump(); }
            catch (Exception ex)
            {
                // A worker that dies silently would leave a frozen view looking healthy.
                _engine.Fault("engine worker exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        try { Pump(finalFlush: true); } catch { /* shutting down */ }
    }

    private void Pump(bool finalFlush = false)
    {
        _drainBuffer.Clear();
        _ingress.Drain(_drainBuffer);

        foreach (var ev in _drainBuffer)
        {
            _engine.Accept(ev);
            _recorder?.Append(ev);
        }

        var now = NowNs;

        var levels = _pendingLevels;
        if (levels is not null)
        {
            _pendingLevels = null;
            _engine.SyncLevels(levels, now);
        }

        if (!finalFlush && now < _nextDecisionNs) return;

        // Section 5: when the timer is late, emit ONE tick at the actual elapsed time.
        // Never synthesise several backdated decisions to "catch up".
        var wasLate = now - _nextDecisionNs > _decisionIntervalNs;
        _nextDecisionNs = now + _decisionIntervalNs;

        _ingress.TryAllocateSequence(out var sequence);

        var tick = new DecisionTick
        {
            EventSequence = sequence,
            ConnectionEpoch = _engine.ConnectionEpoch,
            ElapsedNs = now,
            ReceiveUtc = DateTime.UtcNow,
            WasLate = wasLate,
            ProcessingLagNs = _lastCallbackHandoffNs
        };

        _engine.ReportIngress(_ingress.Backlog, _ingress.HasOverflowed, _ingress.OverflowCount, _lastCallbackHandoffNs);
        if (_recorder is { Faulted: true }) _engine.ReportRecorderFault(_recorder.FaultReason ?? "recorder faulted");

        var before = NowNs;
        var result = _engine.Decide(tick);
        var duration = NowNs - before;
        if (duration > Interlocked.Read(ref _maxDecideNs)) Interlocked.Exchange(ref _maxDecideNs, duration);
        Interlocked.Increment(ref _decisionCount);

        _recorder?.Append(tick);
        foreach (var t in result.Transitions) _recorder?.Append(t);

        _view = result.View;

        if (result.Transitions.Count > 0)
        {
            try { TransitionsEmitted?.Invoke(result.Transitions); }
            catch { /* an alert failure must not stop the engine */ }
        }

        try { ViewPublished?.Invoke(); } catch { }
    }

    private static ViewSnapshot EmptyView(Engine engine) => new()
    {
        PublishedAtNs = 0,
        PublishedAtUtc = DateTime.UtcNow,
        PublishedAtSequence = 0,
        Instrument = InstrumentKey.Unknown,
        IsSimulatedData = engine.IsSimulatedData,
        ModeLabel = engine.FeedMode,
        Health = new HealthSnapshot
        {
            State = DataState.SetupRequired,
            StateReason = "starting up",
            Capabilities = new CapabilityFlags()
        },
        Live = FeatureSnapshot.Empty(0, DateTime.UtcNow, 0)
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop accepting work, cancel the worker, flush with a bounded timeout, then record
        // whatever tail remains. A disposed indicator must not emit another alert.
        TransitionsEmitted = null;
        ViewPublished = null;

        _cancel.Cancel();
        try { _wake.Release(); } catch { }
        if (!_worker.Join(TimeSpan.FromSeconds(2)))
        {
            // The worker is a background thread; the process will not be held open by it.
        }

        try
        {
            _recorder?.Flush();
            _recorder?.Dispose();
        }
        catch { }

        _cancel.Dispose();
        _wake.Dispose();
    }
}
