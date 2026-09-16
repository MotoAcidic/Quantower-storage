using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansCurrent
{
    public enum GammaSource
    {
        /// <summary>Read the flip and walls from the TradeGEX indicator already on an MNQ chart.</summary>
        [Display(Name = "TradeGEX (on the chart)")] TradeGex = 0,

        /// <summary>The one-row CSV Tide Engine was meant to write.</summary>
        [Display(Name = "Tide Engine CSV")] TideEngineCsv = 1,

        [Display(Name = "Off")] Off = 2
    }

    public enum PanelLayout
    {
        /// <summary>The AlphaXtrade Bias Lite reading: a factor per row, weight, direction, bar.</summary>
        [Display(Name = "Bias panel")] BiasPanel = 0,

        /// <summary>The original eight-cell strip. Smaller, and says nothing per factor.</summary>
        [Display(Name = "Compact badge")] Badge = 1
    }

    public enum BadgeCorner
    {
        [Display(Name = "Top right")] TopRight = 0,
        [Display(Name = "Top left")] TopLeft = 1,
        [Display(Name = "Bottom right")] BottomRight = 2,
        [Display(Name = "Bottom left")] BottomLeft = 3
    }

    /// <summary>
    /// Ocean's Current -- one directional state for the session, held until the evidence for it
    /// stops standing up.
    ///
    /// It is not an entry tool and does not draw a single arrow. It reads where price sits
    /// against its own session VWAP, how hard cumulative delta has pushed, what the overnight
    /// left behind, and which carried-over references price is above -- blends them on weights
    /// you can see and change, transforms those weights by the dealer-gamma regime out of Tide
    /// Engine, and then refuses to change its mind for three closed bars. The badge prints the
    /// state, what is driving it, and the level that would end it.
    ///
    /// The discipline lives in the hysteresis, not in the factors. Anything can produce a score;
    /// the value here is that it produces about three of them a day.
    /// </summary>
    [DisplayName("Oceans Current MNQ")]
    [Category("Ocean")]
    public class OceansCurrentIndicator : Indicator
    {
        private readonly object _sync = new object();
        private readonly BarWindow _bars;

        private TimeContext _time;
        private BiasEngine _engine;

        // The blend the engine is actually running, kept so the panel can print each factor's
        // effective weight without rebuilding three dozen settings on the render thread.
        private FactorConfig _factors;
        private string _configKey;
        private int _configCheckedAt = int.MinValue;
        private string _fault;

        private GexSnapshot _gex = GexSnapshot.Empty;
        private DateTime _gexStamp;
        private long _gexLength = -1;
        private bool _gexPresent;

        private bool _hasProvisional;
        private decimal _provisional;

        // What would change the state, the track record, and the scheduled releases. Computed on
        // the calculation thread once per closed bar at the live edge, read by the renderer as
        // whole-object swaps.
        private TriggerReport _triggers;
        private int _triggersAt = -1;
        private Scorecard _scorecard;
        private EventCalendar _calendar;
        private DateTime _eventAlerted;

        private SessionLogger _logger;
        private readonly Dictionary<string, DateTime> _lastAlert = new Dictionary<string, DateTime>();
        private BiasState _alertedState = BiasState.Neutral;
        private bool _alertedStateSeen;

        private RenderFont _font;
        private RenderFont _labelFont;
        private RenderFont _panelFont;
        private RenderFont _panelLabelFont;
        private string _fontKey;

        #region Session

        [Display(GroupName = "Session", Name = "Time zone", Order = 10)]
        public string TimeZoneId { get; set; } = "Central Standard Time";

        [Display(GroupName = "Session", Name = "Bar clock", Order = 11)]
        public BarClock BarTimes { get; set; } = BarClock.Auto;

        [Display(GroupName = "Session", Name = "Daily halt hour (Houston)", Order = 12)]
        [Range(0, 23)]
        public int HaltHourCt { get; set; } = 16;

        [Display(GroupName = "Session", Name = "Cash open (Houston)", Order = 13)]
        public TimeSpan RthStartCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(GroupName = "Session", Name = "Cash close (Houston)", Order = 14)]
        public TimeSpan RthEndCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(GroupName = "Session", Name = "Evening reopen (Houston)", Order = 15)]
        public TimeSpan OvernightOpenCt { get; set; } = new TimeSpan(17, 0, 0);

        [Display(GroupName = "Session", Name = "Overnight ends (Houston)", Order = 16)]
        public TimeSpan OvernightEndCt { get; set; } = new TimeSpan(8, 30, 0);

        /// <summary>
        /// Renamed from Anchor because its default changed. ATAS restores a saved value whenever
        /// the name still matches, so the old name would have kept every existing chart on "the
        /// cash open" -- which is what left VWAP and Session Delta reading n/a all evening.
        /// </summary>
        [Display(GroupName = "Session", Name = "VWAP and CVD reset at", Order = 17)]
        public SessionAnchor VwapAnchor { get; set; } = SessionAnchor.EachSession;

        #endregion

        #region Factors

        [Display(GroupName = "F1 VWAP position", Name = "Enabled", Order = 100)]
        public bool F1Enabled { get; set; } = true;

        [OFT.Attributes.Parameter]
        [Display(GroupName = "F1 VWAP position", Name = "Weight", Order = 101)]
        [Range(0, 1)]
        public decimal F1Weight { get; set; } = 0.20m;

        [Display(GroupName = "F1 VWAP position", Name = "Slope over (bars)", Order = 102)]
        [Range(1, 500)]
        public int VwapSlopeBars { get; set; } = 10;

        [Display(GroupName = "F1 VWAP position", Name = "Fade beyond (sigma)", Order = 103)]
        [Range(0.1, 10)]
        public decimal FadeAboveSigma { get; set; } = 1.5m;

        [Display(GroupName = "F2 CVD thrust", Name = "Enabled", Order = 110)]
        public bool F2Enabled { get; set; } = true;

        [OFT.Attributes.Parameter]
        [Display(GroupName = "F2 CVD thrust", Name = "Weight", Order = 111)]
        [Range(0, 1)]
        public decimal F2Weight { get; set; } = 0.20m;

        [Display(GroupName = "F2 CVD thrust", Name = "Thrust over (bars)", Order = 112)]
        [Range(2, 500)]
        public int ThrustBars { get; set; } = 20;

        [Display(GroupName = "F2 CVD thrust", Name = "Yardstick window (thrusts)", Order = 113)]
        [Range(5, 2000)]
        public int ThrustNormWindow { get; set; } = 100;

        [Display(GroupName = "F2 CVD thrust", Name = "Divergence over (bars)", Order = 114)]
        [Range(2, 500)]
        public int DivergenceBars { get; set; } = 20;

        [Display(GroupName = "F3 Value migration", Name = "Enabled (not built yet)", Order = 120)]
        public bool F3Enabled { get; set; } = false;

        [OFT.Attributes.Parameter]
        [Display(GroupName = "F3 Value migration", Name = "Weight", Order = 121)]
        [Range(0, 1)]
        public decimal F3Weight { get; set; } = 0.20m;

        [Display(GroupName = "F4 Overnight inventory", Name = "Enabled", Order = 130)]
        public bool F4Enabled { get; set; } = true;

        [OFT.Attributes.Parameter]
        [Display(GroupName = "F4 Overnight inventory", Name = "Weight", Order = 131)]
        [Range(0, 1)]
        public decimal F4Weight { get; set; } = 0.10m;

        [Display(GroupName = "F4 Overnight inventory", Name = "A full gap is (points)", Order = 132)]
        [Range(1, 1000)]
        public decimal GapNormPoints { get; set; } = 40m;

        [Display(GroupName = "F5 Structure ladder", Name = "Enabled", Order = 140)]
        public bool F5Enabled { get; set; } = true;

        [OFT.Attributes.Parameter]
        [Display(GroupName = "F5 Structure ladder", Name = "Weight", Order = 141)]
        [Range(0, 1)]
        public decimal F5Weight { get; set; } = 0.20m;

        [Display(GroupName = "F5 Structure ladder", Name = "Reclaim within (bars)", Order = 142)]
        [Range(1, 50)]
        public int SweepWithinBars { get; set; } = 3;

        [Display(GroupName = "F5 Structure ladder", Name = "Sweep fades over (bars)", Order = 143)]
        [Range(1, 500)]
        public int SweepDecayBars { get; set; } = 20;

        [Display(GroupName = "F6 One-timeframing", Name = "Enabled (not built yet)", Order = 150)]
        public bool F6Enabled { get; set; } = false;

        [OFT.Attributes.Parameter]
        [Display(GroupName = "F6 One-timeframing", Name = "Weight", Order = 151)]
        [Range(0, 1)]
        public decimal F6Weight { get; set; } = 0.10m;

        #endregion

        #region Regime

        [Display(GroupName = "Gamma regime", Name = "Read gamma from", Order = 198)]
        public GammaSource GammaFrom { get; set; } = GammaSource.TradeGex;

        /// <summary>
        /// A converted level farther than this from price is refused, not used. This is what
        /// catches TradeGEX's own ratio fallback of 1, which would otherwise put a QQQ flip at
        /// 713 on a 29,000 chart and read "long gamma" on every bar.
        /// </summary>
        [Display(GroupName = "Gamma regime", Name = "Refuse levels farther than (%)", Order = 199)]
        [Range(1, 50)]
        public decimal MaxLevelDistancePct { get; set; } = 10m;

        [Display(GroupName = "Gamma regime", Name = "Tide Engine CSV", Order = 200)]
        public string GexCsvPath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ATAS", "Ocean", "obe_gex.csv");

        [Display(GroupName = "Gamma regime", Name = "Stale after (minutes)", Order = 201)]
        [Range(1, 240)]
        public int StaleAfterMin { get; set; } = 20;

        [Display(GroupName = "Gamma regime", Name = "Long gamma: CVD weight x", Order = 202)]
        [Range(0, 3)]
        public decimal PosGammaCvdMultiplier { get; set; } = 0.7m;

        [Display(GroupName = "Gamma regime", Name = "Short gamma: CVD weight x", Order = 203)]
        [Range(0, 3)]
        public decimal NegGammaCvdMultiplier { get; set; } = 1.3m;

        [Display(GroupName = "Gamma regime", Name = "Wall zone (points)", Order = 204)]
        [Range(0, 500)]
        public decimal WallZonePoints { get; set; } = 15m;

        [Display(GroupName = "Gamma regime", Name = "In the wall zone, score x", Order = 205)]
        [Range(0, 1)]
        public decimal WallDampMultiplier { get; set; } = 0.6m;

        #endregion

        #region State

        [Display(GroupName = "State", Name = "Enter at", Order = 300)]
        [Range(0, 100)]
        public decimal EnterThreshold { get; set; } = 30m;

        [Display(GroupName = "State", Name = "Leave at", Order = 301)]
        [Range(0, 100)]
        public decimal ExitThreshold { get; set; } = 10m;

        [Display(GroupName = "State", Name = "Hold for at least (bars)", Order = 302)]
        [Range(1, 50)]
        public int MinDwellBars { get; set; } = 3;

        [Display(GroupName = "State", Name = "Smooth the score", Order = 303)]
        public bool SmoothScore { get; set; } = true;

        [Display(GroupName = "State", Name = "Smoothing (bars)", Order = 304)]
        [Range(1, 50)]
        public int SmoothBars { get; set; } = 3;

        #endregion

        #region What changes it

        /// <summary>
        /// Draws the next-close levels that would change the state, as dashed lines coloured by
        /// the state they lead to. This used to draw the structural "flip" (the nearest VWAP or
        /// overnight mid below price), which is not the level the engine acts on.
        /// </summary>
        [Display(GroupName = "What changes it", Name = "Draw the trigger levels", Order = 300)]
        public bool ShowTriggerLines { get; set; } = true;

        [Display(GroupName = "What changes it", Name = "Show the next high-impact release", Order = 301)]
        public bool ShowEvents { get; set; } = true;

        [Display(GroupName = "What changes it", Name = "Alert before a release (minutes, 0 = off)", Order = 302)]
        [Range(0, 240)]
        public int EventAlertMinutes { get; set; } = 15;

        [Display(GroupName = "What changes it", Name = "Show the track record", Order = 303)]
        public bool ShowTrackRecord { get; set; } = true;

        #endregion

        #region Alerts and logging

        [Display(GroupName = "Alerts", Name = "On a state change", Order = 400)]
        public bool AlertOnState { get; set; } = true;

        [Display(GroupName = "Alerts", Name = "Near a trigger level", Order = 401)]
        public bool AlertOnFlip { get; set; } = true;

        [Display(GroupName = "Alerts", Name = "Near means (ticks)", Order = 402)]
        [Range(1, 200)]
        public int AlertTicks { get; set; } = 8;

        [Display(GroupName = "Alerts", Name = "On CVD divergence", Order = 403)]
        public bool AlertOnDivergence { get; set; } = false;

        [Display(GroupName = "Alerts", Name = "When the GEX feed goes stale", Order = 404)]
        public bool AlertOnStaleFeed { get; set; } = true;

        [Display(GroupName = "Alerts", Name = "Cooldown (seconds)", Order = 405)]
        [Range(0, 36000)]
        public int AlertCooldownSeconds { get; set; } = 300;

        [Display(GroupName = "Alerts", Name = "Sound", Order = 406)]
        public string AlertSound { get; set; } = "alert1";

        [Display(GroupName = "Logging", Name = "Log every committed bar", Order = 410)]
        public bool LogCommits { get; set; } = true;

        [Display(GroupName = "Logging", Name = "Folder", Order = 411)]
        public string LogFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ATAS", "OceansCurrent", "logs");

        #endregion

        #region Visuals

        [Display(GroupName = "Badge", Name = "Show", Order = 499)]
        public bool ShowBadge { get; set; } = true;

        /// <summary>
        /// A new property rather than a changed default on an old one, per the standing rule:
        /// ATAS restores a saved value whenever the name still matches, so a chart that already
        /// has this indicator on it would never have seen the new layout.
        /// </summary>
        [Display(GroupName = "Badge", Name = "Layout", Order = 500)]
        public PanelLayout Layout { get; set; } = PanelLayout.BiasPanel;

        /// <summary>
        /// Its own setting rather than a new default on FontSize: at 10 the panel printed at about
        /// seven pixels on a 3440-wide monitor and read as nothing on the chart at all.
        /// </summary>
        [Display(GroupName = "Badge", Name = "Panel text size", Order = 506)]
        [Range(8, 32)]
        public int PanelFontSize { get; set; } = 15;

        [Display(GroupName = "Badge", Name = "Corner", Order = 501)]
        public BadgeCorner Corner { get; set; } = BadgeCorner.TopRight;

        [Display(GroupName = "Badge", Name = "Margin (pixels)", Order = 502)]
        [Range(0, 400)]
        public int BadgeMargin { get; set; } = 12;

        /// <summary>
        /// Renamed from FontName, and it has to stay renamed. ATAS restores a saved property
        /// value whenever the NAME still matches, on every chart and every saved template, so
        /// changing the default alone would have kept serving "JetBrains Mono" -- which is not
        /// installed on this machine -- under the new default forever.
        /// </summary>
        [Display(GroupName = "Badge", Name = "Font", Order = 503)]
        public string BadgeFont { get; set; } = "Consolas";

        [Display(GroupName = "Badge", Name = "Font size", Order = 504)]
        [Range(6, 24)]
        public int FontSize { get; set; } = 10;

        [Display(GroupName = "Colours", Name = "Long", Order = 510)]
        public MColor LongColor { get; set; } = MColor.FromRgb(0x2E, 0xA8, 0x8C);

        [Display(GroupName = "Colours", Name = "Short", Order = 511)]
        public MColor ShortColor { get; set; } = MColor.FromRgb(0xE0, 0x46, 0x5A);

        [Display(GroupName = "Colours", Name = "Neutral", Order = 512)]
        public MColor NeutralColor { get; set; } = MColor.FromRgb(0x78, 0x7D, 0x8C);

        [Display(GroupName = "Colours", Name = "Warning", Order = 513)]
        public MColor WarnColor { get; set; } = MColor.FromRgb(0xFF, 0xCD, 0x46);

        [Display(GroupName = "Colours", Name = "Text", Order = 514)]
        public MColor TextColor { get; set; } = MColor.FromRgb(0xEB, 0xEE, 0xF5);

        [Display(GroupName = "Colours", Name = "Label", Order = 515)]
        public MColor LabelColor { get; set; } = MColor.FromRgb(0x8C, 0x92, 0xA4);

        [Display(GroupName = "Colours", Name = "Panel", Order = 516)]
        public MColor PanelColor { get; set; } = MColor.FromRgb(0x0E, 0x11, 0x16);

        #endregion

        public OceansCurrentIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            // Final ALONE. Every working indicator in this suite passes this one flag, and
            // combining it with LatestBar is the only thing here that differed from all of them.
            // SubscribeToDrawingEvents just stores the value, so whether a combined mask is
            // tested with a flags check or an equality check is the platform's business -- and
            // an equality check means OnRender is simply never raised. Silent, total, nothing in
            // the log. Do not "tidy" this back into a combination.
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;

            _bars = new BarWindow(this);

            // This indicator plots no value per bar. A visible inherited series would draw a zero
            // line across the price panel and be its own bug report.
            var series = DataSeries[0] as ValueDataSeries;
            if (series != null)
            {
                series.VisualType = VisualMode.Hide;
                series.IsHidden = true;
                series.ShowZeroValue = false;
                series.ScaleIt = false;
                series.IgnoredByAlerts = true;
            }
        }

        #region Calculation

        protected override void OnRecalculate()
        {
            lock (_sync)
            {
                _engine = null;
                _time = null;
                _fault = null;
                _hasProvisional = false;
                _alertedStateSeen = false;
                _lastAlert.Clear();
            }
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            lock (_sync)
            {
                try
                {
                    Calculate(bar);
                }
                catch (Exception ex)
                {
                    // A throw on the calculation thread is silent inside ATAS. It lands on the
                    // badge instead, which is the only place it can be read.
                    _engine = null;
                    _fault = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        private void Calculate(int bar)
        {
            // Once a bar, not once a tick. This is a string.Join over three dozen boxed settings
            // and it sat on the per-tick path, which the spec explicitly required to be
            // allocation-free. The cost of checking on bar boundaries is that an edited setting
            // lands one bar later; ATAS raises a recalculation on a property change anyway.
            if (bar != _configCheckedAt)
            {
                _configCheckedAt = bar;
                CheckConfig();
            }

            if (_engine == null)
            {
                var nowUtc = UtcTime.Year > 2000 ? UtcTime : DateTime.UtcNow;
                _time = TimeContext.Create(TimeZoneId, BarTimes, _bars, nowUtc, HaltHourCt);

                if (!_time.Valid) { _fault = _time.Error; return; }

                _fault = null;
                _factors = BuildFactors();
                _engine = new BiasEngine(BuildSession(), _factors, BuildState(), Tick());
                _logger = LogCommits ? new SessionLogger(LogFolder) : null;
            }

            // Went backwards: a reload, or a recalculation from an earlier bar. The state is
            // path-dependent, so it is rebuilt forward from zero rather than patched.
            if (bar < _engine.NextBar - 1)
            {
                _factors = BuildFactors();
                _engine = new BiasEngine(BuildSession(), _factors, BuildState(), Tick());
                _hasProvisional = false;

                // The replay is about to write every one of these bars again. Let the log start
                // each day over rather than stack a second copy under the first.
                if (_logger != null) _logger.Reset();
                _triggers = null;
                _triggersAt = -1;
            }

            var lastClosed = bar - 1;

            while (_engine.NextBar <= lastClosed)
            {
                var next = _engine.NextBar;

                // The feed is read once a bar, never per tick, and only when a bar actually
                // closes. Polling a file on the tick path is how a chart starts stuttering.
                RefreshGex(next);

                var stale = _gexPresent && _gex.IsStale(NowUtc(), StaleAfterMin);
                var usable = _gexPresent && !stale && _gex.Usable;
                var age = _gexPresent ? _gex.AgeSeconds(NowUtc()) : -1;

                _engine.Advance(next, _bars, _time, _gex, usable, age);

                AfterCommit(_engine.Last, stale);
            }

            decimal provisional;
            _hasProvisional = _engine.Provisional(
                bar, _bars, _time, _gex,
                _gexPresent && !_gex.IsStale(NowUtc(), StaleAfterMin) && _gex.Usable,
                out provisional);

            _provisional = provisional;

            AfterLiveCommit(bar);
        }

        /// <summary>
        /// Once per newly closed bar, and only at the live edge: what would change the state,
        /// how the calls on this chart have played out, and the next scheduled release.
        ///
        /// The trigger scan re-scores a couple of thousand hypothetical closes, so running it on
        /// every historical bar of a reload would be a few million scorings for levels nobody
        /// will ever see. The last closed bar is the only one whose triggers mean anything.
        /// </summary>
        private void AfterLiveCommit(int liveBar)
        {
            if (_engine == null || !_engine.Any) return;

            var last = _engine.Last;
            if (last.Bar == _triggersAt) return;
            if (!NearLiveEdge(last.Bar)) return;

            _triggersAt = last.Bar;

            var tick = Tick();
            var usable = _gexPresent && !_gex.IsStale(NowUtc(), StaleAfterMin) && _gex.Usable;

            try
            {
                _triggers = TriggerScan.Run(_engine, liveBar, _bars, _time, _gex, usable, tick);
            }
            catch (Exception ex)
            {
                _triggers = TriggerReport.Failed("triggers: " + ex.GetType().Name);
            }

            if (ShowTrackRecord)
            {
                var reopen = OvernightOpenCt;
                _scorecard = Scorecard.Grade(_engine.Records,
                    local => local.TimeOfDay >= reopen ? local.Date.AddDays(1) : local.Date);
            }

            if (ShowEvents)
            {
                if (_calendar == null) _calendar = new EventCalendar(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "OceansCurrent"));
                _calendar.RefreshIfDue(NowUtc());
            }

            // The trigger levels, not the old structural flip, are what "near the level" means.
            var t = _triggers;
            if (AlertOnFlip && t != null && t.Problem == null && tick > 0m)
            {
                var near = Nearest(t, last.Close);
                if (near.Found && Math.Abs(last.Close - near.Price) <= AlertTicks * tick)
                    Raise("trigger", "Ocean's Current: " + Math.Round(Math.Abs(last.Close - near.Price) / tick) +
                                     " ticks from the " + BiasEngine.StateText(near.To) + " trigger " +
                                     BadgeModel.Price(near.Price, tick));
            }

            if (ShowEvents && EventAlertMinutes > 0 && _calendar != null)
            {
                var next = EventCalendar.Next(_calendar.Events, NowUtc(), TimeSpan.Zero);
                if (next != null && next.WhenUtc != _eventAlerted &&
                    next.WhenUtc - NowUtc() <= TimeSpan.FromMinutes(EventAlertMinutes))
                {
                    _eventAlerted = next.WhenUtc;
                    var local = TimeZoneInfo.ConvertTimeFromUtc(next.WhenUtc, _time.Zone);
                    Raise("event", "Ocean's Current: " + next.Title + " at " +
                                   local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
                                   " CT - every level on the panel predates it.");
                }
            }
        }

        private static Trigger Nearest(TriggerReport t, decimal close)
        {
            if (!t.Below.Found) return t.Above;
            if (!t.Above.Found) return t.Below;
            return close - t.Below.Price <= t.Above.Price - close ? t.Below : t.Above;
        }

        private DateTime NowUtc() => UtcTime.Year > 2000 ? UtcTime : DateTime.UtcNow;

        /// <summary>
        /// The instrument's tick, or zero meaning "unknown -- round nothing".
        ///
        /// Deliberately NOT named TickSize: the base class already has that property, and a
        /// member that hides an inherited one is silent at runtime and has cost this suite two
        /// debugging rounds already. This one also has to answer before the platform has wired
        /// the instrument up, which is when a level would otherwise be rounded to zero.
        /// </summary>
        private decimal Tick()
        {
            var info = InstrumentInfo;
            return info == null || info.TickSize <= 0m ? 0m : info.TickSize;
        }

        /// <summary>
        /// Drops the engine when a setting the model depends on has changed. Called on bar
        /// boundaries rather than on every tick -- see the note in Calculate.
        /// </summary>
        private void CheckConfig()
        {
            var key = ConfigKey();
            if (_configKey == key) return;

            _configKey = key;
            _engine = null;
            _time = null;
            _fault = null;
            _hasProvisional = false;
        }

        /// <summary>
        /// Every setting the MODEL depends on. Editing a weight has to rebuild the whole state
        /// sequence; editing a colour must not. ATAS does not reliably recalculate on a plain
        /// property edit, so the change is noticed here rather than waited for.
        /// </summary>
        private string ConfigKey() => string.Join("|",
            TimeZoneId, (int)BarTimes, HaltHourCt, RthStartCt, RthEndCt,
            OvernightOpenCt, OvernightEndCt, (int)VwapAnchor,
            F1Enabled, F1Weight, VwapSlopeBars, FadeAboveSigma,
            F2Enabled, F2Weight, ThrustBars, ThrustNormWindow, DivergenceBars,
            F3Enabled, F3Weight, F4Enabled, F4Weight, GapNormPoints,
            F5Enabled, F5Weight, SweepWithinBars, SweepDecayBars,
            F6Enabled, F6Weight,
            PosGammaCvdMultiplier, NegGammaCvdMultiplier, WallZonePoints, WallDampMultiplier,
            EnterThreshold, ExitThreshold, MinDwellBars, SmoothScore, SmoothBars,
            LogCommits, LogFolder, (int)GammaFrom, MaxLevelDistancePct);

        private SessionConfig BuildSession() => new SessionConfig
        {
            RthStart = RthStartCt,
            RthEnd = RthEndCt,
            OnStart = OvernightOpenCt,
            OnEnd = OvernightEndCt,
            Anchor = VwapAnchor
        };

        private FactorConfig BuildFactors() => new FactorConfig
        {
            Enabled = new[] { F1Enabled, F2Enabled, F3Enabled, F4Enabled, F5Enabled, F6Enabled },
            Weight = new[] { F1Weight, F2Weight, F3Weight, F4Weight, F5Weight, F6Weight },
            VwapSlopeBars = VwapSlopeBars,
            FadeAboveSigma = FadeAboveSigma,
            ThrustBars = ThrustBars,
            ThrustNormWindow = ThrustNormWindow,
            DivergenceBars = DivergenceBars,
            GapNormPoints = GapNormPoints,
            SweepWithinBars = SweepWithinBars,
            SweepDecayBars = SweepDecayBars,
            PosGammaCvdMultiplier = PosGammaCvdMultiplier,
            NegGammaCvdMultiplier = NegGammaCvdMultiplier,
            WallZonePoints = WallZonePoints,
            WallDampMultiplier = WallDampMultiplier
        };

        private StateConfig BuildState() => new StateConfig
        {
            EnterThreshold = EnterThreshold,
            ExitThreshold = ExitThreshold,
            MinDwellBars = MinDwellBars,
            SmoothScore = SmoothScore,
            SmoothBars = SmoothBars
        };

        #endregion

        #region The regime feed

        /// <summary>
        /// Re-reads the Tide Engine row only when the file has actually changed. A missing or
        /// unreadable file leaves the regime at none and the transforms off -- there is no
        /// last-known-value cache here, because a regime remembered from this morning is exactly
        /// the input that would flip the weights the wrong way this afternoon.
        /// </summary>
        private readonly TradeGexBridge _tradeGex = new TradeGexBridge();

        private void RefreshGex(int bar)
        {
            if (GammaFrom == GammaSource.Off)
            {
                _gexPresent = false;
                _gex = GexSnapshot.Empty;
                _gexProblem = null;
                return;
            }

            if (GammaFrom == GammaSource.TradeGex)
            {
                RefreshFromTradeGex(bar);
                return;
            }

            RefreshFromCsv();
        }

        /// <summary>
        /// The flip and walls TradeGEX has right now -- and only for bars that ARE right now.
        ///
        /// Gamma levels are a snapshot of the present with no history behind them. Scoring
        /// yesterday's bars against today's flip on a chart reload would rewrite the past with a
        /// level that did not exist then, and put a fabricated regime into every historical log
        /// row. So a bar away from the live edge gets no regime at all, and the log shows 0.
        /// </summary>
        private void RefreshFromTradeGex(int bar)
        {
            _gexProblem = null;

            if (!NearLiveEdge(bar))
            {
                _gexPresent = false;
                _gex = GexSnapshot.Empty;
                return;
            }

            _gexPresent = true;

            var reading = _tradeGex.Read(InstrumentInfo?.Instrument);

            if (!reading.Found || reading.Problem != null)
            {
                _gex = GexSnapshot.Failed(reading.Problem ?? "TradeGEX unreadable", "TradeGEX");
                return;
            }

            _gex = GexSnapshot.FromLevels(NowUtc(), _bars.Close(bar),
                                          reading.Flip, reading.CallWall, reading.PutWall,
                                          reading.Ratio, MaxLevelDistancePct / 100m);
        }

        /// <summary>
        /// True when this bar opened recently enough that a reading taken now describes it:
        /// within the staleness window, or three bars, whichever is longer, so a 30-minute chart
        /// is not permanently "historical".
        /// </summary>
        private bool NearLiveEdge(int bar)
        {
            if (_time == null || bar < 0 || bar >= _bars.Count) return false;

            var barLocal = _time.ToLocal(_bars.Time(bar));
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(NowUtc(), DateTimeKind.Utc), _time.Zone);

            var period = bar > 0 ? _bars.Time(bar) - _bars.Time(bar - 1) : TimeSpan.Zero;
            if (period < TimeSpan.Zero) period = TimeSpan.Zero;

            var window = TimeSpan.FromMinutes(StaleAfterMin);
            if (period.Ticks * 3 > window.Ticks) window = TimeSpan.FromTicks(period.Ticks * 3);

            return nowLocal - barLocal <= window;
        }

        private void RefreshFromCsv()
        {
            try
            {
                var path = GexCsvPath;

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    _gexPresent = false;
                    _gex = GexSnapshot.Empty;
                    _gexLength = -1;
                    return;
                }

                var info = new FileInfo(path);
                if (_gexPresent && info.LastWriteTimeUtc == _gexStamp && info.Length == _gexLength)
                    return;

                _gexStamp = info.LastWriteTimeUtc;
                _gexLength = info.Length;

                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();

                _gex = GexSnapshot.Parse(text);
                _gexPresent = true;
            }
            catch (Exception ex)
            {
                _gexPresent = true;
                _gex = GexSnapshot.Parse("");
                _gexLength = -1;
                _fault = null;
                _gexStamp = default;

                // The badge shows the feed as unreadable; the rest of the engine carries on
                // with no regime, which is the same thing it does when the file is absent.
                _gexProblem = ex.GetType().Name;
            }
        }

        private string _gexProblem;

        #endregion

        #region Alerts and logging

        private void AfterCommit(BiasRecord record, bool stale)
        {
            if (_logger != null) _logger.Append(record, Tick());

            // History must be silent. Only the live edge is worth a sound.
            if (record.Bar < CurrentBar - 2) { _alertedState = record.State; _alertedStateSeen = true; return; }

            if (AlertOnState && _alertedStateSeen && record.State != _alertedState)
            {
                Raise("state",
                    "Ocean's Current: " + BiasEngine.StateText(_alertedState) + " -> " +
                    BiasEngine.StateText(record.State) + " @ " +
                    BadgeModel.Price(record.Close, Tick()) +
                    " (S=" + (int)record.Score + ", conf " + (int)record.Confidence + ")");
            }

            _alertedState = record.State;
            _alertedStateSeen = true;

            if (AlertOnDivergence && record.Notes != null && record.Notes.Contains("DIVERGENCE"))
                Raise("divergence", "Ocean's Current: CVD divergence at " +
                                    BadgeModel.Price(record.Close, Tick()));

            if (AlertOnStaleFeed && stale)
                Raise("stale", "Ocean's Current: the GEX feed is stale - regime transforms are off.");
        }

        private void Raise(string kind, string message)
        {
            var now = NowUtc();

            DateTime last;
            if (_lastAlert.TryGetValue(kind, out last)
                && (now - last).TotalSeconds < AlertCooldownSeconds)
                return;

            _lastAlert[kind] = now;

            try { AddAlert(AlertSound, message); }
            catch { /* a refused alert must not take the bar down */ }
        }

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var stage = "enter";
            var region = Rectangle.Empty;
            var drawn = Rectangle.Empty;

            try
            {
                var chart = ChartInfo;
                var container = chart == null ? null : chart.PriceChartContainer;
                if (container == null) { stage = "no price container"; return; }

                region = container.Region;
                if (region.Width <= 0 || region.Height <= 0) { stage = "empty region"; return; }

                BiasRecord record;
                bool hasRecord, hasProvisional, gexPresent, gexStale;
                decimal provisional;
                int gexAge;
                string fault, gexProblem;
                GexSnapshot gex;
                FactorConfig factors;
                TriggerReport triggers;
                Scorecard card;
                EventCalendar calendar;
                TimeZoneInfo zone;

                stage = "snapshot";
                lock (_sync)
                {
                    hasRecord = _engine != null && _engine.Any;
                    record = hasRecord ? _engine.Last : default(BiasRecord);
                    hasProvisional = _hasProvisional;
                    provisional = _provisional;
                    fault = _fault;
                    gex = _gex;
                    gexPresent = _gexPresent;
                    gexProblem = _gexProblem;
                    gexAge = _gexPresent ? _gex.AgeSeconds(NowUtc()) : -1;
                    gexStale = _gexPresent && _gex.IsStale(NowUtc(), StaleAfterMin);
                    factors = _factors;
                    triggers = _triggers;
                    card = _scorecard;
                    calendar = _calendar;
                    zone = _time != null ? _time.Zone : null;
                }

                stage = "fonts";
                EnsureFonts();
                context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);

                var tick = Tick();

                // Each layer is caught on its own. A layer that throws used to take down every
                // layer after it, including the one that could have said what went wrong.
                if (ShowTriggerLines && triggers != null && triggers.Problem == null)
                {
                    stage = "trigger lines";
                    try { DrawTriggers(context, container, region, triggers, record, tick); }
                    catch (Exception ex) { fault = fault ?? "trigger lines: " + ex.GetType().Name; }
                }

                if (!ShowBadge) { stage = "badge off"; return; }

                stage = "panel";
                try
                {
                    if (Layout == PanelLayout.BiasPanel)
                    {
                        PanelModel model;

                        if (fault != null) model = PanelModel.NotReady(fault);
                        else if (!hasRecord || factors == null) model = PanelModel.NotReady("warming up");
                        else
                        {
                            model = PanelModel.Build(record, factors, hasProvisional, provisional,
                                                     gex, gexPresent, gexStale, gexAge, tick);
                            model.AddTriggers(triggers, tick);

                            if (ShowEvents && zone != null)
                            {
                                var events = calendar != null ? calendar.Events : null;
                                model.AddEvent(EventCalendar.Next(events, NowUtc(), TimeSpan.FromMinutes(15)),
                                               calendar != null ? calendar.Problem : null, NowUtc(), zone,
                                               events != null && events.Count > 0);
                            }

                            if (ShowTrackRecord) model.AddTrack(card);
                        }

                        if (gexProblem != null && model.Waiting == null)
                        {
                            model.Feed = "GEX " + gexProblem;
                            model.FeedWarn = true;
                        }

                        drawn = DrawPanel(context, region, model);
                        stage = "drawn";
                        return;
                    }

                    BadgeCell[] cells;

                    if (fault != null) cells = BadgeModel.Waiting(fault);
                    else if (!hasRecord) cells = BadgeModel.Waiting("warming up");
                    else cells = BadgeModel.Build(record, hasProvisional, provisional, gex,
                                                  gexPresent, gexStale, gexAge, tick);

                    if (gexProblem != null && cells.Length == BadgeModel.CellCount)
                        cells[BadgeModel.CellCount - 1].Text = "GEX " + gexProblem;

                    DrawBadge(context, region, cells);
                    stage = "drawn (badge)";
                }
                catch (Exception ex)
                {
                    // This used to be an empty catch, and an empty catch here is indistinguishable
                    // from the indicator not being on the chart at all. Whatever else fails,
                    // SOMETHING gets drawn, using a locally built font and two fixed colours.
                    stage = "panel threw " + ex.GetType().Name + ": " + ex.Message;
                    try { DrawDistress(context, region, "panel: " + ex.GetType().Name); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                stage = "render threw at '" + stage + "' " + ex.GetType().Name + ": " + ex.Message;
                try { DrawDistress(context, region, ex.GetType().Name); }
                catch { }
            }
            finally
            {
                Probe(stage, region, drawn);
            }
        }

        private DateTime _probeWritten;
        private string _probeLast;

        /// <summary>
        /// What the last render did, written to %APPDATA%\ATAS\OceansCurrent\render_probe.txt at
        /// most every five seconds. There is no debugger on the render thread and ATAS logs
        /// nothing a custom indicator's OnRender does, so twice now "it draws nothing" has had no
        /// evidence behind it at all. If this file is not being rewritten, OnRender is not being
        /// called; if it is, it says exactly where the frame stopped and what rectangle it drew.
        /// </summary>
        private void Probe(string stage, Rectangle region, Rectangle drawn)
        {
            try
            {
                var now = DateTime.UtcNow;
                var line = stage + " | region " + region + " | drew " + drawn +
                           " | layout " + Layout + " font " + PanelFontSize + " corner " + Corner;

                if (line == _probeLast && (now - _probeWritten).TotalSeconds < 30) return;
                if ((now - _probeWritten).TotalSeconds < 5) return;

                _probeWritten = now;
                _probeLast = line;

                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "ATAS", "OceansCurrent");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "render_probe.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// The last thing standing. Builds its own font, uses no setting, measures nothing, and
        /// draws at a fixed size in a fixed place, so it works when everything the badge depends
        /// on does not. If this is on screen, read the text and fix that.
        /// </summary>
        private void DrawDistress(RenderContext context, Rectangle region, string message)
        {
            var font = new RenderFont("Consolas", 11);
            var box = new Rectangle(region.Left + 12, region.Top + 12, 300, 26);

            context.FillRectangle(Color.FromArgb(235, 40, 12, 16), box);
            context.DrawRectangle(new RenderPen(Color.FromArgb(255, 224, 70, 90), 1f), box);
            context.DrawString("OCEAN'S CURRENT " + message, font,
                               Color.FromArgb(255, 255, 225, 230), box.Left + 6, box.Top + 6);
        }

        /// <summary>
        /// The levels that would change the state, on the price axis: dashed where the next
        /// close does it, dotted where it takes price settling there. Each coloured as the state
        /// it leads to and labelled at the right edge, so the line says what it means without the
        /// panel.
        /// </summary>
        private void DrawTriggers(RenderContext context, IChartContainer container, Rectangle region,
                                  TriggerReport t, BiasRecord record, decimal tick)
        {
            var from = region.Left;
            try
            {
                var anchor = Math.Max(0, record.Bar - 30);
                var ax = container.GetXByBar(anchor, true);
                if (ax > region.Left && ax < region.Right - 80) from = ax;
            }
            catch { }

            DrawTriggerLine(context, container, region, from, t.Below, false, tick);
            DrawTriggerLine(context, container, region, from, t.Above, false, tick);
            DrawTriggerLine(context, container, region, from, t.HoldBelow, true, tick);
            DrawTriggerLine(context, container, region, from, t.HoldAbove, true, tick);
        }

        private void DrawTriggerLine(RenderContext context, IChartContainer container, Rectangle region,
                                     int from, Trigger tr, bool settled, decimal tick)
        {
            if (!tr.Found) return;

            var y = container.GetYByPrice(tr.Price, false);
            if (y < region.Top || y > region.Bottom) return;

            var c = KindColor(tr.To == BiasState.Long ? RowKind.Long
                            : tr.To == BiasState.Short ? RowKind.Short : RowKind.Neutral);

            var pen = new RenderPen(Color.FromArgb(settled ? 150 : 215, c.R, c.G, c.B), settled ? 1f : 1.5f,
                                    settled ? System.Drawing.Drawing2D.DashStyle.Dot
                                            : System.Drawing.Drawing2D.DashStyle.Dash);

            context.DrawLine(pen, from, y, region.Right, y);

            var text = (settled ? "holds " : "") + BiasEngine.StateText(tr.To) +
                       (tr.Kind == TriggerKind.CloseAbove || tr.Kind == TriggerKind.HoldAbove ? " > " : " < ") +
                       BadgeModel.Price(tr.Price, tick);

            var size = context.MeasureString(text, _panelLabelFont);
            var x = region.Right - (int)size.Width - 10;
            var box = new Rectangle(x - 4, y - (int)size.Height - 3, (int)size.Width + 8, (int)size.Height + 2);

            context.FillRectangle(Conv(PanelColor, 215), box);
            context.DrawString(text, _panelLabelFont, c, x, box.Top + 1);
        }

        /// <summary>
        /// The Bias Lite reading, then what would change it, then the context around it.
        ///
        /// Every width is measured, never assumed, and the size and the drawing come from ONE
        /// walk over the content (<see cref="PanelWalk"/>) run twice -- once to measure, once to
        /// draw -- so the box can never again be sized for one layout and drawn with another.
        /// An absent factor draws a hollow chip and NO bar: a bar of zero length on the centre
        /// line is indistinguishable from a factor that read neutral, and those are not the same.
        /// </summary>
        private Rectangle DrawPanel(RenderContext context, Rectangle region, PanelModel model)
        {
            var g = new PanelGeometry(this, context, model);

            var height = PanelWalk(context, model, g, 0, 0, false);

            var right = Corner == BadgeCorner.TopRight || Corner == BadgeCorner.BottomRight;
            var bottom = Corner == BadgeCorner.BottomLeft || Corner == BadgeCorner.BottomRight;

            var x = right ? region.Right - BadgeMargin - g.Width : region.Left + BadgeMargin;
            var y = bottom ? region.Bottom - BadgeMargin - height : region.Top + BadgeMargin;

            // Never off the left or top edge: a panel wider than the chart is still read from its
            // start, and what is lost is at the far end rather than the title.
            if (x < region.Left) x = region.Left;
            if (y < region.Top) y = region.Top;

            var box = new Rectangle(x, y, g.Width, height);
            context.FillRectangle(Conv(PanelColor, 238), box);
            context.DrawRectangle(new RenderPen(Conv(LabelColor, 90), 1f), box);

            PanelWalk(context, model, g, x, y, true);
            return box;
        }

        /// <summary>Every measured size the panel needs, worked out once per frame.</summary>
        private sealed class PanelGeometry
        {
            public int HBig, HSmall, Pad, Gap, RowGap;
            public int NameW, WtW, ChipW, BarW, LabelW, Width;

            public PanelGeometry(OceansCurrentIndicator ind, RenderContext ctx, PanelModel m)
            {
                var big = ind._panelFont;
                var small = ind._panelLabelFont;

                HBig = Math.Max(8, (int)ctx.MeasureString("Wg", big).Height);
                HSmall = Math.Max(7, (int)ctx.MeasureString("Wg", small).Height);
                Pad = Math.Max(6, HSmall / 2);
                Gap = Math.Max(6, HSmall / 2);
                RowGap = Math.Max(2, HBig / 6);

                foreach (var r in m.Rows) NameW = Math.Max(NameW, W(ctx, r.Name, big));
                WtW = W(ctx, "100", small);
                ChipW = Math.Max(Math.Max(W(ctx, "Neutral", small), W(ctx, "Short", small)), W(ctx, "n/a", small)) + 2 * Pad;
                BarW = Math.Max(ChipW * 3 / 2, 60);

                var body = NameW + Gap + WtW + Gap + ChipW + Gap + BarW;

                foreach (var l in m.Changes) LabelW = Math.Max(LabelW, W(ctx, l.Label, small));
                foreach (var l in m.Context) LabelW = Math.Max(LabelW, W(ctx, l.Label, small));
                LabelW = Math.Max(LabelW, W(ctx, "NEUTRAL", small));

                foreach (var l in m.Changes) body = Math.Max(body, LabelW + Gap + W(ctx, l.Text, small));
                foreach (var l in m.Context) body = Math.Max(body, LabelW + Gap + W(ctx, l.Text, small));

                body = Math.Max(body, W(ctx, "WHAT CHANGES IT  (" + (m.ChangesHeader ?? "") + ")", small));
                body = Math.Max(body, W(ctx, "ACTUAL BIAS VALUE", small) + 2 * Gap + W(ctx, m.Bias + " %", big));
                body = Math.Max(body, W(ctx, StateLine(m), small));
                body = Math.Max(body, LabelW + Gap + W(ctx, FeedLine(m), small));
                body = Math.Max(body, W(ctx, PanelModel.Title, small) + 2 * Gap + W(ctx, "(" + m.Provisional + ")", small));
                if (m.Waiting != null) body = Math.Max(body, W(ctx, m.Waiting, big));

                Width = body + 2 * Pad;
            }

            public static int W(RenderContext ctx, string text, RenderFont font) =>
                string.IsNullOrEmpty(text) ? 0 : (int)ctx.MeasureString(text, font).Width;
        }

        private static string StateLine(PanelModel m) => "STATE  " + m.State + "   CONF " + m.Confidence;

        // The flip distance is on the GAMMA line already; here only which side of it we are on.
        private static string FeedLine(PanelModel m) =>
            (m.Feed ?? "") + (m.RegimeKnown && m.Regime != null
                ? (m.Regime.StartsWith("+") ? ", long gamma" : ", short gamma") : "");

        /// <summary>
        /// Walks the panel top to bottom. With <paramref name="draw"/> false it only adds up the
        /// height; with it true it draws at (x, y). Same code both times, so they cannot disagree.
        /// </summary>
        private int PanelWalk(RenderContext ctx, PanelModel m, PanelGeometry g, int x, int y, bool draw)
        {
            var big = _panelFont;
            var small = _panelLabelFont;
            var text = Conv(TextColor);
            var label = Conv(LabelColor);
            var muted = Conv(LabelColor, 170);
            var warn = Conv(WarnColor);
            var rulePen = new RenderPen(Conv(LabelColor, 60), 1f);

            var left = x + g.Pad;
            var rightEdge = x + g.Width - g.Pad;
            var cy = y + g.Pad;

            Action rule = () =>
            {
                cy += g.RowGap;
                if (draw) ctx.DrawLine(rulePen, left, cy, rightEdge, cy);
                cy += g.RowGap + 2;
            };

            // Title, and the forming bar's provisional read at the right.
            if (draw)
            {
                ctx.DrawString(PanelModel.Title, small, text, left, cy);
                if (m.Provisional != null && m.Waiting == null)
                {
                    var p = "(" + m.Provisional + ")";
                    ctx.DrawString(p, small, label, rightEdge - PanelGeometry.W(ctx, p, small), cy);
                }
            }
            cy += g.HSmall;
            rule();

            if (m.Waiting != null)
            {
                if (draw) ctx.DrawString(m.Waiting, big, warn, left, cy);
                cy += g.HBig + g.Pad;
                return cy - y;
            }

            // The factor table.
            var wtX = left + g.NameW + g.Gap;
            var chipX = wtX + g.WtW + g.Gap;
            var barX = chipX + g.ChipW + g.Gap;
            var barMid = barX + g.BarW / 2;

            foreach (var row in m.Rows)
            {
                if (draw)
                {
                    var absent = row.Kind == RowKind.Absent;

                    ctx.DrawString(row.Name, big, absent ? muted : text, left, cy);

                    var ww = PanelGeometry.W(ctx, row.Weight, small);
                    ctx.DrawString(row.Weight, small, absent ? muted : label,
                                   wtX + g.WtW - ww, cy + (g.HBig - g.HSmall) / 2);

                    DrawChip(ctx, new Rectangle(chipX, cy + 1, g.ChipW, g.HBig - 2), row, g.HSmall);

                    ctx.DrawLine(new RenderPen(Conv(LabelColor, 70), 1f), barMid, cy, barMid, cy + g.HBig);

                    if (row.HasBar)
                    {
                        var span = (int)Math.Round((double)Math.Abs(row.Value) / 100.0 * (g.BarW / 2 - 2));
                        var inset = Math.Max(2, g.HBig / 5);
                        if (span > 0)
                            ctx.FillRectangle(KindColor(row.Kind, 220), row.Value >= 0m
                                ? new Rectangle(barMid + 1, cy + inset, span, g.HBig - 2 * inset)
                                : new Rectangle(barMid - span, cy + inset, span, g.HBig - 2 * inset));
                    }
                }

                cy += g.HBig + g.RowGap;
            }

            rule();

            // The bias value, and the state it is holding.
            if (draw)
            {
                ctx.DrawString("ACTUAL BIAS VALUE", small, label, left, cy + (g.HBig - g.HSmall) / 2);
                var bias = m.Bias + (m.BiasKnown ? " %" : "");
                ctx.DrawString(bias, big, KindColor(m.BiasKind), rightEdge - PanelGeometry.W(ctx, bias, big), cy);
            }
            cy += g.HBig + g.RowGap;

            if (draw) ctx.DrawString(StateLine(m), small, KindColor(m.StateKind), left, cy);
            cy += g.HSmall;

            // What would change it.
            if (m.ChangesHeader != null)
            {
                rule();

                if (draw)
                {
                    ctx.DrawString("WHAT CHANGES IT", small, text, left, cy);
                    var hx = left + PanelGeometry.W(ctx, "WHAT CHANGES IT  ", small);
                    ctx.DrawString("(" + m.ChangesHeader + ")", small, label, hx, cy);
                }
                cy += g.HSmall + g.RowGap;

                foreach (var l in m.Changes)
                {
                    if (draw) DrawLine(ctx, l, left, left + g.LabelW + g.Gap, cy, small, text, muted, warn);
                    cy += g.HSmall + g.RowGap;
                }
            }

            rule();

            foreach (var l in m.Context)
            {
                if (draw) DrawLine(ctx, l, left, left + g.LabelW + g.Gap, cy, small, text, muted, warn);
                cy += g.HSmall + g.RowGap;
            }

            if (draw)
            {
                ctx.DrawString("FEED", small, label, left, cy);
                ctx.DrawString(FeedLine(m), small, m.FeedWarn ? warn : muted, left + g.LabelW + g.Gap, cy);
            }
            cy += g.HSmall + g.Pad;

            return cy - y;
        }

        private void DrawLine(RenderContext ctx, PanelLine l, int labelX, int textX, int y, RenderFont font,
                              Color text, Color muted, Color warn)
        {
            var labelColor = l.Warn ? warn : l.Muted ? muted : l.Coloured ? KindColor(l.Kind) : Conv(LabelColor);
            var textColor = l.Warn ? warn : l.Muted ? muted : text;

            if (!string.IsNullOrEmpty(l.Label)) ctx.DrawString(l.Label, font, labelColor, labelX, y);
            if (!string.IsNullOrEmpty(l.Text)) ctx.DrawString(l.Text, font, textColor, textX, y);
        }

        /// <summary>
        /// The direction cell. Solid where the factor voted -- as Bias Lite draws it -- and
        /// hollow where it could not, which Bias Lite has no way to say.
        /// </summary>
        private void DrawChip(RenderContext context, Rectangle box, PanelRow row, int textHeight)
        {
            var w = context.MeasureString(row.Direction, _panelLabelFont).Width;
            var tx = box.Left + (box.Width - w) / 2;
            var ty = box.Top + (box.Height - textHeight) / 2;

            if (row.Kind == RowKind.Absent)
            {
                context.DrawRectangle(new RenderPen(Conv(LabelColor, 110), 1f,
                                      System.Drawing.Drawing2D.DashStyle.Dot), box);
                context.DrawString(row.Direction, _panelLabelFont, Conv(LabelColor, 170), tx, ty);
                return;
            }

            context.FillRectangle(KindColor(row.Kind, 235), box);

            // Dark text on the filled chip, the way the reference draws it: the fill carries the
            // direction, so the letters only have to stay legible on top of it.
            context.DrawString(row.Direction, _panelLabelFont, Color.FromArgb(235, 8, 10, 14), tx, ty);
        }

        private Color KindColor(RowKind kind, int alpha = 255)
        {
            switch (kind)
            {
                case RowKind.Long: return Conv(LongColor, alpha);
                case RowKind.Short: return Conv(ShortColor, alpha);
                case RowKind.Neutral: return Conv(NeutralColor, alpha);
                default: return Conv(LabelColor, alpha);
            }
        }

        private void DrawBadge(RenderContext context, Rectangle region, BadgeCell[] cells)
        {
            const int PadX = 8;
            const int PadY = 5;
            const int Gap = 3;

            var rowHeight = 0;
            var width = 0;

            for (var i = 0; i < cells.Length; i++)
            {
                var label = context.MeasureString(cells[i].Label, _labelFont);
                var text = context.MeasureString(cells[i].Text, _font);

                var w = (int)label.Width + 8 + (int)text.Width;
                if (w > width) width = w;

                var h = (int)Math.Max(label.Height, text.Height);
                if (h > rowHeight) rowHeight = h;
            }

            // A measurement of zero is not a small badge, it is an invisible one -- and it is
            // exactly what a substituted or unresolvable font produces. Falling back to a legible
            // fixed size keeps the failure on screen where it can be read.
            if (rowHeight < 6) rowHeight = 14;
            if (width < 40) width = 220;

            width += PadX * 2;
            var height = PadY * 2 + cells.Length * rowHeight + (cells.Length - 1) * Gap;

            var right = Corner == BadgeCorner.TopRight || Corner == BadgeCorner.BottomRight;
            var bottom = Corner == BadgeCorner.BottomLeft || Corner == BadgeCorner.BottomRight;

            var x = right ? region.Right - BadgeMargin - width : region.Left + BadgeMargin;
            var y = bottom ? region.Bottom - BadgeMargin - height : region.Top + BadgeMargin;

            context.FillRectangle(Conv(PanelColor, 232), new Rectangle(x, y, width, height));
            context.DrawRectangle(new RenderPen(Conv(LabelColor, 90), 1f),
                                  new Rectangle(x, y, width, height));

            var labelColor = Conv(LabelColor);
            var rowY = y + PadY;

            for (var i = 0; i < cells.Length; i++)
            {
                context.DrawString(cells[i].Label, _labelFont, labelColor, x + PadX, rowY);

                var size = context.MeasureString(cells[i].Text, _font);
                context.DrawString(cells[i].Text, _font, KindColor(cells[i].Kind),
                                   x + width - PadX - size.Width, rowY);

                rowY += rowHeight + Gap;
            }
        }

        private Color KindColor(CellKind kind)
        {
            switch (kind)
            {
                case CellKind.Long: return Conv(LongColor);
                case CellKind.Short: return Conv(ShortColor);
                case CellKind.Neutral: return Conv(NeutralColor);
                case CellKind.Warn: return Conv(WarnColor);
                case CellKind.Muted: return Conv(LabelColor);
                default: return Conv(TextColor);
            }
        }

        /// <summary>
        /// The chosen font, or one the machine actually has.
        ///
        /// This used to be a try/catch around the constructor, on the assumption that a missing
        /// family throws. It does not: RenderFont builds happily from any name at all, including
        /// pure nonsense, so the fallback was dead code that could never run and a font nobody
        /// had was being measured and drawn with. The family is therefore CHECKED rather than
        /// caught, which is the only version of this that can actually fire.
        /// </summary>
        private void EnsureFonts()
        {
            var key = BadgeFont + "|" + FontSize + "|" + PanelFontSize;
            if (_fontKey == key && _font != null) return;

            var family = Installed(BadgeFont) ? BadgeFont : "Consolas";

            _font = new RenderFont(family, FontSize);
            _labelFont = new RenderFont(family, Math.Max(6, FontSize - 2));
            _panelFont = new RenderFont(family, PanelFontSize);
            _panelLabelFont = new RenderFont(family, Math.Max(7, PanelFontSize - 2));
            _fontKey = key;
        }

        private static bool Installed(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return false;

            try
            {
                using (var f = new System.Drawing.FontFamily(family)) return true;
            }
            catch
            {
                // FontFamily is the one that does throw on a name nothing matches.
                return false;
            }
        }

        private static Color Conv(MColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        private static Color Conv(MColor c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        #endregion

        /// <summary>
        /// The chart, as the model sees it: no ATAS types past this boundary. One-deep cache
        /// because every read here comes in runs over the same bar.
        /// </summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansCurrentIndicator _owner;
            private int _cachedIndex = -1;
            private IndicatorCandle _cached;

            public BarWindow(OceansCurrentIndicator owner) { _owner = owner; }

            public int Count => _owner.CurrentBar;

            private IndicatorCandle At(int bar)
            {
                if (bar != _cachedIndex)
                {
                    _cached = _owner.GetCandle(bar);
                    _cachedIndex = bar;
                }
                return _cached;
            }

            public DateTime Time(int bar) => At(bar).Time;
            public decimal Open(int bar) => At(bar).Open;
            public decimal High(int bar) => At(bar).High;
            public decimal Low(int bar) => At(bar).Low;
            public decimal Close(int bar) => At(bar).Close;
            public decimal Volume(int bar) => At(bar).Volume;
            public decimal Delta(int bar) => At(bar).Delta;
        }
    }
}
