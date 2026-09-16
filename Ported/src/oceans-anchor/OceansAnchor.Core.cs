using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Threading;
using ATAS.Indicators;
using OFT.Rendering.Tools;
using Utils.Common.Logging;
using MColor = System.Windows.Media.Color;

namespace OceansAnchor
{
    /// <summary>
    /// Ocean's Anchor -- outlier HVN zones plus outlier absorption, and nothing else.
    ///
    /// The playbook this implements is one trade: price arrives at a volume shelf that the last
    /// two weeks agree on, something large absorbs the aggressors there without price moving,
    /// and the delta turns. Everything in this indicator exists to make that one moment
    /// unmissable and to write down what it looked like, so the thresholds stop being guesses
    /// after ten sessions.
    ///
    /// It is an INDICATOR. It never places an order. Risk stays with the prop firm's drawdown
    /// and the personal rules; this thing marks, alerts and logs.
    ///
    /// Two absorption engines, on purpose. The live tape path reads actual cumulative market
    /// orders and is the real signal. The cluster path reconstructs an approximation from
    /// footprint bid/ask so history renders on chart load -- without it, calibration would mean
    /// ten sessions of watching a screen in real time. Their agreement rate is itself one of the
    /// numbers being calibrated.
    ///
    /// EVERY time here is Houston time. There is no second zone in the settings or the labels.
    /// </summary>
    [DisplayName("Ocean's Anchor")]
    [Category("Ocean")]
    public partial class OceansAnchor : Indicator
    {
        private const string Dot = "·";
        private const string Arrow = "▶";

        private readonly object _sync = new object();
        private readonly BarWindow _bars;

        private BarClockContext _clock;
        private readonly SessionConfig _session = new SessionConfig();
        private TimeSpan _barSize;

        // Session profile ring, oldest first. One entry per completed CME trade date plus the
        // one in progress.
        private readonly List<SessionProfile> _sessions = new List<SessionProfile>();
        private SessionProfile _liveRth;
        private SessionProfile _liveOvernight;
        private DateTime _liveDate;

        private readonly NakedPocTracker _naked = new NakedPocTracker();
        private List<Zone> _zones = new List<Zone>();

        private readonly SignalEngine _engine = new SignalEngine();
        private AnchorLog _log;

        // Per-bar caches, folded in once per CLOSED bar. Iterating price levels on every tick of
        // every visible bar is what makes an indicator like this stall a chart, so the expensive
        // pass is strictly incremental.
        private readonly List<BarFacts> _facts = new List<BarFacts>();
        private readonly List<decimal> _deltas = new List<decimal>();
        private int _processed = -1;
        private int _lastZoneBar = -1;
        private DateTime _lastZoneDate;

        private decimal _sessionOpen;
        private decimal _adr;

        // --- tape plumbing -------------------------------------------------------------------
        //
        // The callbacks run hot and must never block the chart thread, so they do one thing:
        // snapshot into a bounded queue. A background worker applies the size floor. Everything
        // that touches a Zone happens back on the chart thread in OnCalculate, which is what
        // keeps the whole state machine single-threaded and free of locks it would otherwise
        // need in OnRender.
        private BlockingCollection<TradeSnapshot> _queue;
        private Thread _worker;

        private readonly object _inboxLock = new object();
        private readonly List<TradeSnapshot> _inbox = new List<TradeSnapshot>();
        private readonly ConcurrentQueue<decimal> _prints = new ConcurrentQueue<decimal>();

        private readonly DisplacementWatch _watch = new DisplacementWatch();
        private TradeSnapshot _lastTrade;
        private bool _haveLastTrade;

        // This session's tape, buffered from the history response and replayed bar by bar. Its
        // own displacement watch, because replay time and live time run independently.
        private List<TradeSnapshot> _history;
        private int _historyAt;
        private int _gateBar = int.MinValue;
        private readonly DisplacementWatch _replayWatch = new DisplacementWatch();

        private volatile bool _historyDone;
        private volatile bool _requestWaiting;
        private volatile bool _requestFailed;
        private int _requestBar;
        private string _tapeNote;

        private PriceSelectionDataSeries _marks;

        private readonly RenderFont _labelFont = new RenderFont("Arial", 9f, FontStyle.Bold);
        private readonly RenderFont _statusFont = new RenderFont("Consolas", 10f);

        public OceansAnchor() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final | DrawingLayouts.LatestBar |
                                     DrawingLayouts.Historical);

            _bars = new BarWindow(this);

            // The indicator draws everything itself; the inherited series would otherwise plot a
            // line of zeroes down the middle of the price panel.
            DataSeries[0] = new ValueDataSeries("Anchor", "Anchor")
            {
                IsHidden = true,
                VisualType = VisualMode.Hide,
                ShowZeroValue = false
            };

            _marks = new PriceSelectionDataSeries("Marks", "Absorption marks") { IgnoredByAlerts = true };
            DataSeries.Add(_marks);
        }

        #region Settings - Calibration

        // A new name rather than a reuse, per ~/dev/CLAUDE.md: ATAS restores saved values by
        // property name, so only a name no saved chart has ever seen lets this default reach the
        // chart the indicator is already sitting on.
        [Display(GroupName = "Calibration", Name = "Silent calibration", Order = 0,
                 Description = "The ten-session run. Draws nothing and sounds nothing, and forces the " +
                               "CSV log, live tape and cluster path on. Only a clock or log failure is " +
                               "printed on the chart. Turn off once the thresholds are calibrated.")]
        public bool SilentCalibration { get; set; } = true;

        #endregion

        #region Settings - Clock

        [Display(GroupName = "Clock", Name = "Time zone", Order = 1,
                 Description = "One zone for everything. Houston is Central Standard Time.")]
        public string TimeZoneId { get; set; } = "Central Standard Time";

        [Display(GroupName = "Clock", Name = "Bar clock", Order = 2,
                 Description = "Auto settles it from evidence and refuses to guess. Set by hand only if it cannot.")]
        public BarClock BarTimes { get; set; } = BarClock.Auto;

        /// <summary>
        /// 16, NOT 15. This has shipped wrong three times across this suite.
        ///
        /// 15:00 Houston is the CASH close, and trading carries straight on through it -- the
        /// hour after it is one of the busiest of the day, not an empty one. The maintenance
        /// halt, the hour that is genuinely empty on every weekday, is 16:00-17:00.
        ///
        /// Getting it wrong does not produce a wrong level. It produces NOTHING: the halt
        /// signal never resolves, the bar clock comes back undecided, and the indicator draws
        /// its cannot-settle message instead of zones, with nothing pointing at this setting.
        /// If Ocean's Anchor ever draws nothing, check this first.
        /// </summary>
        [Range(0, 23)]
        [Display(GroupName = "Clock", Name = "Daily halt hour", Order = 3,
                 Description = "The hour MNQ is shut, Houston time (16, not 15 - 15:00 is the cash " +
                               "close and trading continues). Used to settle the bar clock.")]
        public int HaltHour { get; set; } = 16;

        #endregion

        #region Settings - Zones

        [Range(1, 40)]
        [Display(GroupName = "Zones", Name = "Composite sessions", Order = 10)]
        public int CompositeSessions { get; set; } = 10;

        [Range(0.05, 1.0)]
        [Display(GroupName = "Zones", Name = "Node peak %", Order = 11,
                 Description = "HVN outlier test against the POC bin. If you have to squint, it is not an HVN.")]
        public decimal NodePeakPct { get; set; } = 0.70m;

        [Range(0.05, 1.0)]
        [Display(GroupName = "Zones", Name = "Shelf edge %", Order = 12)]
        public decimal ShelfEdgePct { get; set; } = 0.50m;

        [Range(0, 50)]
        [Display(GroupName = "Zones", Name = "Smooth ticks", Order = 13)]
        public int SmoothTicks { get; set; } = 4;

        [Range(1, 100)]
        [Display(GroupName = "Zones", Name = "Peak window ticks", Order = 14)]
        public int PeakWindowTicks { get; set; } = 12;

        [Range(4, 1000)]
        [Display(GroupName = "Zones", Name = "Max shelf ticks", Order = 15,
                 Description = "Width cap. 100 ticks = 25 NQ points.")]
        public int MaxShelfTicks { get; set; } = 100;

        [Range(1, 12)]
        [Display(GroupName = "Zones", Name = "Max zones", Order = 16)]
        public int MaxZones { get; set; } = 4;

        [Range(0, 100)]
        [Display(GroupName = "Zones", Name = "Min overnight volume %", Order = 17,
                 Description = "Share of prior RTH volume the overnight needs before its POC is admitted.")]
        public decimal MinOnVolumePct { get; set; } = 15m;

        [Range(0.5, 1.0)]
        [Display(GroupName = "Zones", Name = "Value area %", Order = 18)]
        public decimal ValueAreaPct { get; set; } = 0.70m;

        [Range(1, 60)]
        [Display(GroupName = "Zones", Name = "ADR sessions", Order = 19)]
        public int DailyAtrSessions { get; set; } = 10;

        [Range(0.0, 10.0)]
        [Display(GroupName = "Zones", Name = "Distance ADR multiple", Order = 20,
                 Description = "Zones further than this from the session open dim and do not arm. Zero disables it.")]
        public decimal DistanceAdrMult { get; set; } = 1.5m;

        #endregion

        #region Settings - Trigger

        [Range(1, 100000)]
        [Display(GroupName = "Trigger", Name = "Size floor", Order = 30,
                 Description = "Contracts, NQ chart. Calibrate to p95 of largest_trade from the CSV.")]
        public decimal SizeFloor { get; set; } = 100m;

        [Range(0, 200)]
        [Display(GroupName = "Trigger", Name = "Zone buffer ticks", Order = 31)]
        public int ZoneBufferTicks { get; set; } = 8;

        [Range(0, 200)]
        [Display(GroupName = "Trigger", Name = "Max displacement ticks", Order = 32,
                 Description = "Absorbed means no follow-through. 6 ticks = 1.5 NQ points.")]
        public int MaxDisplacementTicks { get; set; } = 6;

        [Range(100, 60000)]
        [Display(GroupName = "Trigger", Name = "Displacement window ms", Order = 33)]
        public int DisplacementWindowMs { get; set; } = 2000;

        [Range(1, 3600)]
        [Display(GroupName = "Trigger", Name = "Stack window sec", Order = 34)]
        public int StackWindowSec { get; set; } = 90;

        [Display(GroupName = "Trigger", Name = "Tape history aggregation", Order = 35,
                 Description = "How the feed groups prints into cumulative trades when replaying " +
                               "this session. Weak groups least, Strong most -- so a Strong setting " +
                               "reports fatter trades and effectively lowers the size floor. Keep it " +
                               "the same across the whole calibration run or the CSV compares " +
                               "numbers that were never measured the same way.")]
        public CumulativeTradesMode HistoryMode { get; set; } = CumulativeTradesMode.Medium;

        [Display(GroupName = "Trigger", Name = "Use live tape", Order = 36,
                 Description = "The real signal. Turn off only to see what the cluster path alone produces.")]
        public bool UseLiveTape { get; set; } = true;

        #endregion

        #region Settings - Cluster

        [Display(GroupName = "Cluster", Name = "Use cluster path", Order = 40,
                 Description = "Footprint reconstruction so history renders on load. Dimmer marks.")]
        public bool UseClusterPath { get; set; } = true;

        [Range(3, 500)]
        [Display(GroupName = "Cluster", Name = "Delta lookback", Order = 41)]
        public int DeltaLookback { get; set; } = 20;

        [Range(0.1, 49.9)]
        [Display(GroupName = "Cluster", Name = "Delta percentile", Order = 42)]
        public decimal DeltaPercentile { get; set; } = 10m;

        [Range(1, 200)]
        [Display(GroupName = "Cluster", Name = "Extreme ticks", Order = 43)]
        public int ExtremeTicks { get; set; } = 8;

        [Range(1, 100)]
        [Display(GroupName = "Cluster", Name = "Extreme %", Order = 44)]
        public decimal ExtremePct { get; set; } = 30m;

        [Range(0, 200)]
        [Display(GroupName = "Cluster", Name = "Min wick ticks", Order = 45)]
        public int MinWickTicks { get; set; } = 6;

        [Range(1, 100)]
        [Display(GroupName = "Cluster", Name = "Close position %", Order = 46)]
        public decimal ClosePosPct { get; set; } = 50m;

        [Range(1, 4)]
        [Display(GroupName = "Cluster", Name = "Min score", Order = 47,
                 Description = "How many of the four shape tests must pass.")]
        public int MinScore { get; set; } = 3;

        #endregion

        #region Settings - Gate

        [Range(1, 50)]
        [Display(GroupName = "Gate", Name = "Clock bars", Order = 50,
                 Description = "Bars a Triggered zone has to resolve in. Expiry rate is a calibration output.")]
        public int ClockBars { get; set; } = 3;

        [Range(0, 100)]
        [Display(GroupName = "Gate", Name = "Break buffer ticks", Order = 51)]
        public int BreakBufferTicks { get; set; } = 3;

        [Range(2, 50)]
        [Display(GroupName = "Gate", Name = "One-timeframing bars", Order = 52)]
        public int OtfBars { get; set; } = 5;

        [Display(GroupName = "Gate", Name = "One-timeframing guard", Order = 53,
                 Description = "Never fade the freight train.")]
        public bool OtfGuardOn { get; set; } = true;

        [Display(GroupName = "Gate", Name = "Window start (Houston)", Order = 54)]
        public TimeSpan WindowStart { get; set; } = new TimeSpan(8, 30, 0);

        [Display(GroupName = "Gate", Name = "Window end (Houston)", Order = 55)]
        public TimeSpan WindowEnd { get; set; } = new TimeSpan(10, 30, 0);

        [Display(GroupName = "Gate", Name = "Allow all hours", Order = 56)]
        public bool AllowAllHours { get; set; } = false;

        [Display(GroupName = "Gate", Name = "Suppress Friday", Order = 57)]
        public bool SuppressFriday { get; set; } = true;

        #endregion

        #region Settings - Alerts

        [Display(GroupName = "Alerts", Name = "Alert on approach", Order = 60)]
        public bool AlertApproach { get; set; } = false;

        [Display(GroupName = "Alerts", Name = "Alert on trigger", Order = 61)]
        public bool AlertTriggered { get; set; } = true;

        [Display(GroupName = "Alerts", Name = "Alert on signal", Order = 62,
                 Description = "The only one that should ever be at full volume.")]
        public bool AlertSignal { get; set; } = true;

        [Display(GroupName = "Alerts", Name = "Approach sound", Order = 63)]
        public string ApproachWav { get; set; } = "alert1";

        [Display(GroupName = "Alerts", Name = "Trigger sound", Order = 64)]
        public string TriggerWav { get; set; } = "alert1";

        [Display(GroupName = "Alerts", Name = "Signal sound", Order = 65)]
        public string SignalWav { get; set; } = "alert2";

        [Range(0, 3600)]
        [Display(GroupName = "Alerts", Name = "Cooldown sec", Order = 66)]
        public int AlertCooldownSec { get; set; } = 120;

        [Display(GroupName = "Alerts", Name = "Write CSV log", Order = 67,
                 Description = "The ten-session calibration. Documents\\ATAS\\OceansAnchor\\anchor_log.csv")]
        public bool WriteLog { get; set; } = true;

        #endregion

        #region Settings - Visual

        [Display(GroupName = "Visual", Name = "Zone colour", Order = 70)]
        public MColor ZoneColor { get; set; } = MColor.FromRgb(79, 179, 169);

        [Display(GroupName = "Visual", Name = "Absorption colour", Order = 71)]
        public MColor MarkColor { get; set; } = MColor.FromRgb(201, 131, 79);

        [Display(GroupName = "Visual", Name = "Broken colour", Order = 72)]
        public MColor BrokenColor { get; set; } = MColor.FromRgb(194, 95, 95);

        [Range(1, 100)]
        [Display(GroupName = "Visual", Name = "Zone fill opacity %", Order = 73)]
        public int ZoneOpacity { get; set; } = 22;

        [Display(GroupName = "Visual", Name = "Show status line", Order = 74)]
        public bool ShowStatus { get; set; } = true;

        [Display(GroupName = "Visual", Name = "Show zone labels", Order = 75)]
        public bool ShowLabels { get; set; } = true;

        #endregion

        #region Rule assembly

        private HvnSettings Hvn()
        {
            return new HvnSettings
            {
                SmoothTicks = SmoothTicks,
                PeakWindowTicks = PeakWindowTicks,
                NodePeakPct = NodePeakPct,
                ShelfEdgePct = ShelfEdgePct,
                MaxShelfTicks = MaxShelfTicks
            };
        }

        private AbsorptionRules Tape()
        {
            return new AbsorptionRules
            {
                SizeFloor = SizeFloor,
                ZoneBufferTicks = ZoneBufferTicks,
                MaxDisplacementTicks = MaxDisplacementTicks,
                DisplacementWindowMs = DisplacementWindowMs,
                StackWindowSec = StackWindowSec
            };
        }

        private ClusterRules Cluster()
        {
            return new ClusterRules
            {
                DeltaLookback = DeltaLookback,
                DeltaPercentile = DeltaPercentile,
                ExtremeTicks = ExtremeTicks,
                ExtremePct = ExtremePct,
                MinWickTicks = MinWickTicks,
                ClosePosPct = ClosePosPct,
                MinScore = MinScore
            };
        }

        private void ApplyStateRules()
        {
            _engine.Rules.ClockBars = ClockBars;
            _engine.Rules.BreakBufferTicks = BreakBufferTicks;
            _engine.Rules.ZoneBufferTicks = ZoneBufferTicks;
            _engine.Rules.OtfBars = OtfBars;
            _engine.Rules.OtfGuardOn = OtfGuardOn;
            _engine.Rules.WindowStart = WindowStart;
            _engine.Rules.WindowEnd = WindowEnd;
            _engine.Rules.AllowAllHours = AllowAllHours;
            _engine.Rules.SuppressFriday = SuppressFriday;
        }

        // What the indicator actually runs with. Silent calibration pins the three things the CSV
        // needs, so a setting left off on a saved chart cannot quietly empty a calibration day:
        // no log is no data, and a missing path is no agreement rate.
        private bool LogOn { get { return WriteLog || SilentCalibration; } }
        private bool TapeOn { get { return UseLiveTape || SilentCalibration; } }
        private bool ClusterOn { get { return UseClusterPath || SilentCalibration; } }

        private decimal Tick
        {
            get
            {
                var info = InstrumentInfo;
                return info != null && info.TickSize > 0m ? info.TickSize : 0m;
            }
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// ATAS re-runs the whole series on load and on any setting change, arriving here as
        /// bar 0. Everything stateful has to be torn down at that point or the second run stacks
        /// zones, episodes and tape events on top of the first.
        /// </summary>
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
            {
                ResetAll();
                return;
            }

            var tick = Tick;
            if (tick <= 0m) return;

            if (_clock == null || !_clock.Valid) SettleClock();
            if (_clock == null || !_clock.Valid) return;

            // Everything expensive is gated to CLOSED bars. The live bar is CurrentBar - 1, so
            // the newest bar whose footprint is final is the one before it. OnCalculate fires
            // per tick on the live bar; folding a profile in there would iterate every price
            // level of every visible bar, several times a second -- and would fold the same bar
            // in again on the next tick.
            var lastClosed = CurrentBar - 2;
            var gate = HistoryGateBar();

            while (_processed < lastClosed)
            {
                var next = _processed + 1;

                // Pause at the start of today's session until the tape for it has arrived, so
                // historical trades are replayed inside the bars they happened in rather than
                // dumped on a state machine that has already moved on. See HistoryGateBar.
                if (next > 0 && gate >= 0 && next >= gate && !_historyDone)
                {
                    RequestHistory();
                    break;
                }

                _processed = next;
                if (_processed <= 0) continue;

                FoldBar(_processed, tick);
            }

            SettleHistory(lastClosed);
            DrainTape(tick);

            if (bar >= CurrentBar - 1) CacheForRender();
        }

        /// <summary>
        /// Releases the fold gate when there is nothing left to wait for.
        ///
        /// A request that is never answered -- a feed with no tape depth, a weekend chart, a
        /// replay session -- must not leave the fold parked at the session start forever, with
        /// no zones and no alerts and nothing on screen explaining why. So it times out and
        /// carries on with the cluster path, which is what covers history anyway.
        /// </summary>
        private void SettleHistory(int lastClosed)
        {
            if (_historyDone) return;

            if (!TapeOn)
            {
                _historyDone = true;
                return;
            }

            if (_requestFailed)
            {
                _historyDone = true;
                return;
            }

            if (_requestWaiting && _requestBar > 0 && CurrentBar - _requestBar >= HistoryWaitBars)
            {
                _requestWaiting = false;
                _historyDone = true;
                _tapeNote = "tape history did not answer; cluster path only";
                return;
            }

            // Nothing to wait for at all: no gate was ever set, so there is no session to hold
            // the fold back for.
            if (!_requestWaiting && _gateBar < 0 && _processed >= lastClosed) _historyDone = true;
        }

        /// <summary>
        /// How many further calculate passes to wait for the tape before giving up. Bars, not
        /// seconds, because on a stale or weekend chart the wall clock never moves and a
        /// time-based wait would hang forever.
        /// </summary>
        private const int HistoryWaitBars = 3;

        private void ResetAll()
        {
            lock (_sync)
            {
                _sessions.Clear();
                _facts.Clear();
                _deltas.Clear();
                _zones = new List<Zone>();
                _naked.Clear();
                _engine.Reset();

                _liveRth = null;
                _liveOvernight = null;
                _liveDate = default(DateTime);

                _processed = -1;
                _lastZoneBar = -1;
                _lastZoneDate = default(DateTime);
                _sessionOpen = 0m;
                _adr = 0m;

                _clock = null;
                _barSize = TimeSpan.Zero;

                _renderZones = new List<ZoneView>();
                _renderStatus = null;
            }

            _marks.Clear();
            _watch.Clear();
            _replayWatch.Clear();

            lock (_inboxLock)
            {
                _inbox.Clear();
                _history = null;
                _historyAt = 0;
            }

            _gateBar = int.MinValue;
            decimal drop;
            while (_prints.TryDequeue(out drop)) { }

            _haveLastTrade = false;
            _historyDone = false;
            _requestWaiting = false;
            _requestFailed = false;
            _requestBar = 0;
            _tapeNote = null;
            _signalLine = null;
            _clockError = null;

            _log = LogOn ? new AnchorLog(AnchorLog.DefaultPath()) : null;

            ApplyStateRules();
            StartWorker();
        }

        private void SettleClock()
        {
            if (CurrentBar < 3) return;

            _barSize = BarMath.Duration(_bars, 300);

            var marketNow = MarketTime.Year > 2000 ? MarketTime : default(DateTime);

            _clock = BarClockContext.Create(TimeZoneId, BarTimes, _bars, DateTime.UtcNow, marketNow,
                                            HaltHour, _barSize);

            if (_clock != null && !_clock.Valid) this.LogWarn(_clock.Error);
        }

        protected override void OnDispose()
        {
            StopWorker();
            base.OnDispose();
        }

        #endregion

        #region Bar adapter

        /// <summary>
        /// Adapts candle access to IBarWindow, with a one-entry cache because the scan reads
        /// several fields off the same bar in a row.
        ///
        /// GetCandle, never SourceDataSeries: that series is null until the platform wires it up
        /// and returns 0 rather than failing, which settles the bar clock against a wall of
        /// zeroes and draws every zone in the wrong place.
        /// </summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansAnchor _owner;
            private int _cachedIndex = -1;
            private IndicatorCandle _cached;

            public BarWindow(OceansAnchor owner) { _owner = owner; }

            public int Count { get { return _owner.CurrentBar; } }

            private IndicatorCandle At(int bar)
            {
                if (bar != _cachedIndex)
                {
                    _cached = _owner.GetCandle(bar);
                    _cachedIndex = bar;
                }

                return _cached;
            }

            public DateTime Time(int bar) { return At(bar).Time; }
            public decimal Open(int bar) { return At(bar).Open; }
            public decimal High(int bar) { return At(bar).High; }
            public decimal Low(int bar) { return At(bar).Low; }
            public decimal Close(int bar) { return At(bar).Close; }
        }

        #endregion
    }
}
