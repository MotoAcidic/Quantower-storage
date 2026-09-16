using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using ATAS.Indicators;
using AuctionResponse.Core;
using AuctionResponse.Replay;
using OFT.Attributes;
using AuctionResponse.Ui;
using OFT.Rendering.Context;

namespace AuctionResponse.Atas;

/// <summary>
/// Auction Response Monitor â€” a READ-ONLY ATAS indicator.
///
/// It identifies aggressive trading with limited price progress at a level declared in
/// advance, then waits for a separate directional confirmation. It places no orders, reads
/// no account state, and contains no reachable order-submission path.
///
/// All numerical thresholds are experimental engineering defaults. No learned coefficients,
/// calibrated probabilities, empirical win rate or demonstrated net edge exist for it.
///
/// Layout note (deviation from Section 3, recorded deliberately): ATAS binds one indicator
/// instance to one panel. Rather than requiring two instances and a shared-engine lifetime,
/// this build draws the price overlay AND the companion sidebar inside the price panel, the
/// sidebar as an in-chart HUD. The Section 3 content contract is unchanged.
/// </summary>
[DisplayName("Auction Response Monitor")]
[Category("Ocean")]
[Display(Name = "Auction Response Monitor",
    Description = "Read-only absorption candidate and rule confirmation monitor. Experimental: no calibrated edge.")]
public sealed class AuctionResponseMonitor : Indicator
{
    private readonly object _lifecycle = new();

    private EngineHost _host;
    private ChartRenderer _renderer;
    private Palette _palette;
    private TickGrid _grid;
    private SessionCalendar _calendar;
    private Config _config = new();
    private InstrumentKey _instrument = InstrumentKey.Unknown;

    private decimal _bestBid, _bestAsk, _bestBidQty, _bestAskQty;
    private bool _haveBid, _haveAsk;
    private DateTime _lastQuoteExchangeUtc;

    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);
    private volatile bool _disposed;
    private string _startupError;
    private long _lastRedrawNs;
    private float _lastDpiScale = 1f;
    private string _renderScaleNote = "";
    private string _liveBaselinePath;
    private string _liveBaselineNote;
    private long _lastBaselineSaveNs;

    public AuctionResponseMonitor()
        : base(true)
    {
        // The overlay lives on the price panel, which is the default; nothing is plotted as
        // a data series, so the panel is pinned rather than chosen.
        DenyToChangePanel = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    // ---------------------------------------------------------------- settings

    [Display(GroupName = "Instrument", Name = "Contract expiry", Order = 10,
        Description = "The dated contract this chart is showing, for example 202612. Never guessed: a baseline for one expiry must not be used on another.")]
    public string ContractExpiry { get; set; } = "";

    [Display(GroupName = "Levels", Name = "Use horizontal lines I draw", Order = 19,
        Description = "Every horizontal line on the chart becomes a level. Draw one where you see a level and it is watched; delete it and it stops. Resistance or support is decided by where price is when you draw it.")]
    public bool UseDrawnLines { get; set; } = true;

    [Display(GroupName = "Levels", Name = "Extra levels (optional)", Order = 20,
        Description = "Comma-separated prices, if you would rather type them than draw them. Works alongside drawn lines.")]
    public string ExtraLevels { get; set; } = "";

    [Display(GroupName = "Levels", Name = "Zone half width (ticks)", Order = 22,
        Description = "Zone Z = [L-h, L+h]. Default 2 ticks.")]
    [Range(0, 50)]
    public int ZoneHalfWidthTicks { get; set; } = 2;

    [Display(GroupName = "Baseline", Name = "Baseline file (optional)", Order = 30,
        Description = "Leave empty and the indicator observes its own reference from live data and remembers it between sessions. Supply a file only to override that with one you built yourself.")]
    public string BaselineArtifactPath { get; set; } = "";

    [Display(GroupName = "Baseline", Name = "Allow cross-expiry baseline", Order = 31,
        Description = "Lets a baseline built on a different dated contract of the same instrument be used. Off by default. On roll day the alternative is no baseline at all; when on, the health panel says so.")]
    public bool AllowCrossExpiryBaseline { get; set; }

    [Display(GroupName = "Session", Name = "Session timezone", Order = 40,
        Description = "IANA timezone for the session window and every displayed time. One zone throughout.")]
    public string SessionTimezone { get; set; } = "America/Chicago";

    [Display(GroupName = "Session", Name = "Session start (local)", Order = 41,
        Description = "RTH start in the session timezone. 08:30 Central is the CME equity index open.")]
    public string SessionStartLocal { get; set; } = "08:30:00";

    [Display(GroupName = "Session", Name = "Session end, exclusive (local)", Order = 42,
        Description = "RTH end in the session timezone. 15:00 Central is the cash close.")]
    public string SessionEndLocal { get; set; } = "15:00:00";

    [Display(GroupName = "Recording", Name = "Recording directory", Order = 50,
        Description = "Where the event log is written. Leave empty to record nothing. Recording market data needs your approval and your market-data rights.")]
    public string RecordingDirectory { get; set; } = "";

    [Display(GroupName = "Recording", Name = "I approve recording to that directory", Order = 51,
        Description = "The recorder refuses an unapproved destination. This must be ticked explicitly.")]
    public bool RecordingApproved { get; set; }

    [Display(GroupName = "Display", Name = "Show price overlay", Order = 60)]
    public bool ShowOverlay { get; set; } = true;

    [Display(GroupName = "Display", Name = "Show sidebar", Order = 61)]
    public bool ShowSidebar { get; set; } = true;

    [Display(GroupName = "Display", Name = "UI scale (0 = follow Windows)", Order = 62,
        Description = "Text and spacing multiplier. 0 follows the DPI the chart reports. Set it explicitly (1.0, 1.25, 1.5) if the panel looks too small or too large; the layout measures its own text, so any value stays readable.")]
    [Range(0.0, 3.0)]
    public double UiScale { get; set; }

    [Display(GroupName = "Display", Name = "Show diagnostics", Order = 63,
        Description = "Expands the data-health section into per-input counters and ages. Off by default: the status line above already says what is blocking live operation.")]
    public bool ShowDiagnostics { get; set; }

    [Display(GroupName = "Display", Name = "Maximum redraws per second", Order = 64,
        Description = "Draw requests are throttled to this rate. The host's actual render rate is separate.")]
    [Range(1, 30)]
    public int MaximumFramesPerSecond { get; set; } = 10;

    [Display(GroupName = "Alerts", Name = "Audible alerts", Order = 70,
        Description = "Off by default. Transitions are always logged whether or not this is on.")]
    public bool AudibleAlerts { get; set; }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnInitialize()
    {
        // Subscriptions and construction happen here, never from the render thread.
        lock (_lifecycle)
        {
            try
            {
                _startupError = null;
                BuildEngine();
                SubscribeToTimer(TimeSpan.FromMilliseconds(Math.Max(1000 / Math.Max(MaximumFramesPerSecond, 1), 50)), RequestRedraw);
            }
            catch (Exception ex)
            {
                // A constructor or initialiser that throws is invisible inside ATAS, so the
                // failure is captured and drawn on the chart instead of vanishing.
                _startupError = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    private void BuildEngine()
    {
        TearDown();

        var info = DataProvider?.InstrumentInfo;
        if (info is null || info.TickSize <= 0m)
        {
            _startupError = "instrument metadata is not available yet (tick size unknown)";
            return;
        }

        _grid = new TickGrid(info.TickSize);
        _instrument = new InstrumentKey(
            info.Instrument ?? "",
            info.Exchange ?? "",
            string.IsNullOrWhiteSpace(ContractExpiry) ? "" : ContractExpiry.Trim());

        _config = new Config
        {
            Instrument = new InstrumentConfig
            {
                InstrumentKey = _instrument,
                TickSize = info.TickSize
            },
            Candidate = new CandidateConfig { ZoneHalfWidthTicks = ZoneHalfWidthTicks },
            Session = new SessionConfig
            {
                Timezone = SessionTimezone,
                WindowsTimezoneEquivalent = "Central Standard Time",
                StartLocal = SessionStartLocal,
                EndLocalExclusive = SessionEndLocal
            },
            Baseline = new BaselineConfig
            {
                ArtifactPath = string.IsNullOrWhiteSpace(BaselineArtifactPath) ? null : BaselineArtifactPath
            },
            Recording = new RecordingConfig
            {
                Directory = string.IsNullOrWhiteSpace(RecordingDirectory) ? null : RecordingDirectory,
                PermissionConfirmed = RecordingApproved,
                RequireRecorderForResearch = !string.IsNullOrWhiteSpace(RecordingDirectory)
            },
            Display = new DisplayConfig { MaximumRequestedFramesPerSecond = MaximumFramesPerSecond },
            Alerts = new AlertsConfig { AudibleEnabled = AudibleAlerts }
        };

        var errors = _config.Validate();
        if (errors.Count > 0)
        {
            _startupError = "configuration invalid: " + string.Join("; ", errors);
            return;
        }

        _calendar = new SessionCalendar(_config.Session);

        var engine = new Engine(_config, _instrument, info.TickSize, feedMode: DataProvider?.Name ?? "Live")
        {
            Capabilities = new CapabilityFlags
            {
                // Set from what has actually been observed, not from the presence of an API.
                Trades = false,
                Quotes = false,
                MarketByPrice = false,
                MarketByOrderVerified = false,
                ExecutionLinkage = false,
                MboUnverifiedReason = "snapshot fence and passive execution linkage not established on this feed"
            }
        };

        LoadBaseline(engine);

        JsonlRecorder recorder = null;
        if (!string.IsNullOrWhiteSpace(RecordingDirectory))
        {
            try
            {
                recorder = new JsonlRecorder(
                    new RecordingPermission(RecordingDirectory, RecordingApproved),
                    _instrument,
                    _calendar.SessionId(DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _startupError = "recording refused: " + ex.Message;
                return;
            }
        }

        // Pick up whatever it learned in previous sessions. No path to type, no file to find.
        if (string.IsNullOrWhiteSpace(BaselineArtifactPath))
        {
            _liveBaselinePath = LiveBaselineIo.DefaultPath(_instrument);
            try
            {
                var learned = LiveBaselineIo.Load(_liveBaselinePath);
                engine.LiveBaseline.Import(learned.Export());
            }
            catch (Exception ex)
            {
                // A damaged store is discarded, not half-trusted. It rebuilds from observation.
                _startupError = null;
                _liveBaselineNote = "previous baseline could not be read (" + ex.GetType().Name + "); rebuilding from observation";
            }
        }

        _host = new EngineHost(engine, _config, recorder);
        _host.TransitionsEmitted += OnTransitions;

        _palette = new Palette(_lastDpiScale);
        _renderer = new ChartRenderer(_palette);
    }

    private void LoadBaseline(Engine engine)
    {
        if (string.IsNullOrWhiteSpace(BaselineArtifactPath)) return;
        try
        {
            var artifact = BaselineArtifactIo.Load(BaselineArtifactPath);
            engine.LoadBaseline(artifact);
        }
        catch (Exception ex)
        {
            // A baseline that cannot be read is a missing baseline, not a zeroed one.
            _startupError = "baseline artifact could not be loaded: " + ex.Message;
        }
    }

    /// <summary>
    /// Collects the levels the chart currently shows: every horizontal line drawn on it, plus
    /// anything typed into the settings. Called on the UI timer and handed to the worker.
    ///
    /// A level is identified by its PRICE, so re-reading the chart does not restart a live
    /// candidate, and moving a line genuinely is a different level.
    /// </summary>
    private List<LevelDefinition> CollectLevels()
    {
        var levels = new List<LevelDefinition>();
        if (_grid is null) return levels;

        var reference = CurrentMidpointPrice();
        var seen = new HashSet<long>();

        if (UseDrawnLines)
        {
            var lines = ChartInfo?.DrawingObjectsListInfo?.HorizontalLines;
            if (lines is not null)
                foreach (var line in lines)
                    TryAdd(levels, seen, line.Price, reference, "drawn");
        }

        if (!string.IsNullOrWhiteSpace(ExtraLevels))
            foreach (var raw in ExtraLevels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
                    TryAdd(levels, seen, price, reference, "typed");

        return levels;
    }

    private void TryAdd(List<LevelDefinition> levels, HashSet<long> seen, decimal price, decimal? reference, string source)
    {
        // An off-grid level is refused, never rounded: rounding silently moves the zone.
        if (!_grid.TryToTicks(price, out var ticks)) return;
        if (!seen.Add(ticks)) return;

        // Above the market is resistance, below it is support. If the market has not been
        // seen yet the side is unknowable, so the level waits rather than guessing.
        if (reference is not { } mid) return;
        var side = price >= mid ? LevelSide.Resistance : LevelSide.Support;

        levels.Add(new LevelDefinition
        {
            LevelId = (side == LevelSide.Resistance ? "R" : "S") + _grid.ToPrice(ticks).ToString("0.##", CultureInfo.InvariantCulture),
            Side = side,
            PriceTicks = ticks,
            EffectiveNs = 0,
            ConnectionEpoch = _host?.Engine.ConnectionEpoch ?? 1,
            Source = source
        });
    }

    private decimal? CurrentMidpointPrice()
    {
        if (_haveBid && _haveAsk && _bestAsk > _bestBid) return (_bestBid + _bestAsk) / 2m;

        // Before the first quote arrives, the last bar close is enough to tell whether a
        // level sits above or below the market. It is only used to pick a side.
        try
        {
            var bar = CurrentBar - 1;
            if (bar >= 0 && GetCandle(bar) is { } candle && candle.Close > 0m) return candle.Close;
        }
        catch { }

        return null;
    }

    // ---------------------------------------------------------------- callbacks

    protected override void OnCalculate(int bar, decimal value)
    {
        // Chart mapping only. Historical bars are NOT a source of synthetic ticks, and no
        // signal is ever computed here.
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        var host = _host;
        if (host is null || _disposed || trade is null) return;

        var ns = host.NowNs;
        var utc = DateTime.UtcNow;
        var epoch = host.Engine.ConnectionEpoch;

        host.Submit(seq => EventMapper.Trade(trade, _grid, _instrument, epoch, ns, utc) with { EventSequence = seq });
        MarkCapability(trades: true);
    }

    protected override void OnNewTrades(IEnumerable<MarketDataArg> trades)
    {
        // The batch and single-trade paths are the same stream. Only ONE is used, so a fill
        // cannot be counted twice; the single-trade callback above is the canonical one.
    }

    protected override void OnBestBidAskChanged(MarketDataArg depth)
    {
        var host = _host;
        if (host is null || _disposed || depth is null) return;

        // The host delivers one side at a time. A canonical best-quote event is emitted only
        // once BOTH sides are known, and the pair is applied atomically downstream so a
        // transient crossed book is never manufactured.
        if (depth.DataType == MarketDataType.Bid) { _bestBid = depth.Price; _bestBidQty = depth.Volume; _haveBid = true; }
        else if (depth.DataType == MarketDataType.Ask) { _bestAsk = depth.Price; _bestAskQty = depth.Volume; _haveAsk = true; }
        else return;

        if (depth.Time != default) _lastQuoteExchangeUtc = depth.Time.ToUniversalTime();
        if (!_haveBid || !_haveAsk) return;

        var ns = host.NowNs;
        var utc = DateTime.UtcNow;
        var epoch = host.Engine.ConnectionEpoch;
        var bid = _bestBid; var bidQty = _bestBidQty;
        var ask = _bestAsk; var askQty = _bestAskQty;
        var exchangeUtc = _lastQuoteExchangeUtc == default ? (DateTime?)null : _lastQuoteExchangeUtc;

        host.Submit(seq => EventMapper.BestQuote(_grid, _instrument, epoch, ns, utc, bid, bidQty, ask, askQty, exchangeUtc)
            with { EventSequence = seq });
        MarkCapability(quotes: true);
    }

    protected override void MarketDepthChanged(MarketDataArg depth)
    {
        var host = _host;
        if (host is null || _disposed || depth is null) return;

        var ns = host.NowNs;
        var utc = DateTime.UtcNow;
        var epoch = host.Engine.ConnectionEpoch;

        host.Submit(seq => EventMapper.DepthChange(depth, _grid, _instrument, epoch, ns, utc) with { EventSequence = seq });
        MarkCapability(depth: true);
    }

    private void MarkCapability(bool trades = false, bool quotes = false, bool depth = false)
    {
        var engine = _host?.Engine;
        if (engine is null) return;

        var c = engine.Capabilities;
        if ((trades && c.Trades) && (quotes || !quotes) && !depth) { }   // fast path: nothing new

        var updated = c with
        {
            Trades = c.Trades || trades,
            Quotes = c.Quotes || quotes,
            MarketByPrice = c.MarketByPrice || depth
        };

        if (updated.Trades != c.Trades || updated.Quotes != c.Quotes || updated.MarketByPrice != c.MarketByPrice)
            engine.Capabilities = updated;
    }

    private void OnTransitions(IReadOnlyList<TransitionRecord> transitions)
    {
        if (_disposed) return;

        foreach (var t in transitions)
        {
            // Deduplicated by instrument, epoch, candidate and transition type.
            if (!_alerted.Add(t.DeduplicationKey)) continue;
            if (!AudibleAlerts) continue;
            if (t.NewState is not (SetupState.Confirmed or SetupState.Invalidated)) continue;

            var message = (t.Orientation > 0 ? "Bearish" : "Bullish") + " rule confirmation at " + t.LevelId
                        + " - this does not guarantee further movement";
            if (t.NewState == SetupState.Invalidated)
                message = "Candidate invalidated at " + t.LevelId + " beyond the failure boundary";

            try
            {
                // AddAlert takes WPF media colours on this build, not System.Drawing ones.
                AddAlert("alert1", _instrument.Symbol, message,
                    Media(Theme.Background),
                    Media(t.NewState == SetupState.Confirmed
                        ? (t.Orientation > 0 ? Theme.Coral : Theme.Teal)
                        : Theme.Muted));
            }
            catch { /* an alert failure must never break the engine */ }
        }
    }

    /// <summary>
    /// Writes the observed reference out, at most once per <paramref name="everyMinutes"/>.
    /// Failures are swallowed on purpose: losing a few minutes of learning is a nuisance, but
    /// an exception here would take down the chart.
    /// </summary>
    private void SaveLearnedBaseline(EngineHost host, int everyMinutes)
    {
        if (_liveBaselinePath is null || _disposed) return;

        var now = host.NowNs;
        var intervalNs = (long)everyMinutes * 60 * 1_000_000_000L;
        if (_lastBaselineSaveNs != 0 && now - _lastBaselineSaveNs < intervalNs) return;
        _lastBaselineSaveNs = now;

        try { LiveBaselineIo.Save(host.Engine.LiveBaseline, _liveBaselinePath); } catch { }
    }

    private static System.Windows.Media.Color Media(Color c)
        => System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);

    private void RequestRedraw()
    {
        if (_disposed) return;
        var host = _host;
        if (host is null) return;

        // Re-read the chart's own levels on the same cadence as the redraw, so drawing or
        // deleting a line takes effect without touching settings.
        try { host.SubmitLevels(CollectLevels()); } catch { }

        SaveLearnedBaseline(host, everyMinutes: 5);

        var now = host.NowNs;
        var minimumIntervalNs = 1_000_000_000L / Math.Max(MaximumFramesPerSecond, 1);
        if (now - _lastRedrawNs < minimumIntervalNs) return;
        _lastRedrawNs = now;

        try { RedrawChart(new RedrawArg(Container.Region)); } catch { }
    }

    // ---------------------------------------------------------------- rendering

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (_disposed) return;

        var region = Container?.Region ?? Rectangle.Empty;
        if (region.Width <= 0 || region.Height <= 0) return;

        if (_startupError is not null)
        {
            try
            {
                context.DrawString("Auction Response Monitor: " + _startupError,
                    _palette?.Small ?? new OFT.Rendering.Tools.RenderFont("Segoe UI", 12f),
                    Theme.Coral, region.Left + 8, region.Top + 8);
            }
            catch { }
            return;
        }

        var host = _host;
        var renderer = _renderer;
        if (host is null || renderer is null) return;

        EnsurePaletteMatchesDpi();

        // Read the published snapshot ONCE. Everything drawn comes from this one object.
        var view = host.View;

        renderer.Draw(
            context, region, view,
            price => ChartInfo.PriceChartContainer.GetYByPrice(price, false),
            _grid?.TickSize ?? 1m,
            _calendar,
            ShowOverlay,
            ShowSidebar,
            ShowDiagnostics,
            _renderScaleNote);
    }

    private void EnsurePaletteMatchesDpi()
    {
        var dpi = ChartInfo?.DpiX ?? 96m;
        var automatic = (float)(dpi / 96m);
        if (automatic <= 0f) automatic = 1f;

        // An explicit setting wins; 0 follows whatever the chart reports.
        var scale = UiScale > 0 ? (float)UiScale : automatic;

        _renderScaleNote = "DPI " + dpi.ToString("0", CultureInfo.InvariantCulture) +
                           " · " + scale.ToString("0.##", CultureInfo.InvariantCulture) + "x" +
                           (UiScale > 0 ? " set" : " auto");

        if (Math.Abs(scale - _lastDpiScale) < 0.01f && _palette is not null) return;

        _lastDpiScale = scale <= 0 ? 1f : scale;
        var previous = _palette;
        _palette = new Palette(_lastDpiScale);
        _renderer = new ChartRenderer(_palette);
        previous?.Dispose();
    }

    // ---------------------------------------------------------------- teardown

    protected override void OnDispose()
    {
        _disposed = true;
        TearDown();
        base.OnDispose();
    }

    private void TearDown()
    {
        lock (_lifecycle)
        {
            var host = _host;
            _host = null;

            if (host is not null)
            {
                // Stop the source of work first, then cancel, flush and release.
                host.TransitionsEmitted -= OnTransitions;

                // Keep what this session learned before letting go of the engine.
                if (_liveBaselinePath is not null)
                    try { LiveBaselineIo.Save(host.Engine.LiveBaseline, _liveBaselinePath); } catch { }

                try { host.Dispose(); } catch { }
            }

            _renderer = null;
            _palette?.Dispose();
            _palette = null;
            _alerted.Clear();
            _haveBid = _haveAsk = false;
        }
    }
}
