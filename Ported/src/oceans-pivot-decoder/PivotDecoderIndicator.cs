using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansPivotDecoder
{
    /// <summary>How much of the readout to show.</summary>
    public enum PanelMode
    {
        /// <summary>Zones only. The chart still answers where the long and the short are.</summary>
        Off = 0,

        /// <summary>A single verdict strip, top right.</summary>
        OneLine = 1,

        /// <summary>The whole board: bias, plans, base rates, ladder, magnets.</summary>
        Full = 2
    }

    /// <summary>
    /// Pivot Decoder -- prior-session levels clustered into scored zones, validated against the
    /// tape, and reduced to one statement: where the long is and where the short is.
    ///
    /// The v1 chart drew every level it knew. That was the wrong instrument. Thirty labelled lines
    /// do not compose into a decision -- the levels that mattered were in there, invisible among
    /// the ones that were not. So nothing draws as a line now. Levels cluster into bands, bands
    /// are scored by what they are made of, only the best few near price render, and the side they
    /// sit on decides their colour.
    ///
    /// What zones cannot say on their own is whether a level was ever defended, because price
    /// reaches every level eventually. That is what the absorption engine is for: size going INTO
    /// a band with no follow-through past it and a close back out is a level that held. Everything
    /// else is a level that happened to be in the way.
    ///
    /// EVERY time here is Houston time. There is no second zone in the settings or the labels.
    /// </summary>
    [DisplayName("Ocean Pivot Decoder")]
    [Category("Ocean")]
    public class PivotDecoder : Indicator
    {
        // Written as escapes so the source survives any encoding it passes through.
        private const string Up = "▲";
        private const string Down = "▼";
        private const string Dash = "–";
        private const string PoorMark = "¬";

        private readonly object _sync = new object();
        private readonly BarWindow _bars;

        private volatile bool _dirty = true;
        private int _builtBarCount = -1;
        private string _configKey;

        private BarClockContext _clock;
        private Analysis _analysis;

        private bool _selfTestOk;
        private string _selfTestReport = string.Empty;

        private string _logError;
        private string _loggedDate;
        private string _logSignature;

        private readonly HashSet<string> _alerted = new HashSet<string>();

        // Per-bar caches, folded in once per CLOSED bar and never recomputed. Iterating price
        // levels on every tick of every visible bar is what makes an indicator like this stall a
        // chart, so the expensive pass is strictly incremental.
        private readonly List<BarFacts> _facts = new List<BarFacts>();
        private readonly List<decimal[]> _levelVolumes = new List<decimal[]>();
        private readonly Dictionary<string, Dictionary<decimal, decimal>> _profiles =
            new Dictionary<string, Dictionary<decimal, decimal>>();
        private int _processed = -1;

        private LevelAccess _access = LevelAccess.Unknown;
        private TimeSpan _barSize;
        private string _timeframeNote;
        private string _boundaryNote;
        private string _sdkWarning;
        private string _deltaWarning;

        // Whether a price has been traded through since a given bar, memoised and only ever
        // extended forward.
        private readonly Dictionary<string, PierceState> _pierced = new Dictionary<string, PierceState>();

        private List<TouchEvent> _events;
        private DateTime _eventsFor;
        private List<TradingDay> _days;

        private sealed class PierceState
        {
            public int CheckedTo = -1;
            public bool Hit;
        }

        private readonly RenderFont _zoneFont = new RenderFont("Arial", 9.5f, FontStyle.Bold);
        private readonly RenderFont _panelFont = new RenderFont("Consolas", 10f);
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        public PivotDecoder() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = false;

            if (DataSeries.Count > 0 && DataSeries[0] is ValueDataSeries series)
            {
                series.VisualType = VisualMode.Hide;
                series.IsHidden = true;
                series.ShowZeroValue = false;
            }

            _bars = new BarWindow(this);
            _selfTestOk = PivotMath.SelfTest(out _selfTestReport);
        }

        #region 01 Session

        [Display(Name = "Time zone", GroupName = "01 Session", Order = 100,
                 Description = "Houston is Central Standard Time. Every setting below is in it.")]
        public string TimeZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "01 Session", Order = 105,
                 Description = "Whether bar stamps are UTC or already local. Auto works it out " +
                               "from the data and refuses to guess.")]
        public BarClock BarTimes { get; set; } = BarClock.Auto;

        [Display(Name = "Maintenance halt hour (Houston)", GroupName = "01 Session", Order = 110,
                 Description = "Used only to settle the bar clock automatically. 16 = 4 PM, the " +
                               "CME maintenance halt. Not the 3 PM cash close.")]
        public int MaintenanceHaltHourCt { get; set; } = 16;

        [Display(Name = "Prior session used", GroupName = "01 Session", Order = 120)]
        public SessionMode PriorSessionSource { get; set; } = SessionMode.Rth;

        [Display(Name = "RTH starts (Houston)", GroupName = "01 Session", Order = 130)]
        public TimeSpan RthStartCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "RTH ends (Houston)", GroupName = "01 Session", Order = 140)]
        public TimeSpan RthEndCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(Name = "Globex reopens (Houston)", GroupName = "01 Session", Order = 150)]
        public TimeSpan EthStartCt { get; set; } = new TimeSpan(17, 0, 0);

        [Display(Name = "Globex closes (Houston)", GroupName = "01 Session", Order = 160)]
        public TimeSpan EthEndCt { get; set; } = new TimeSpan(16, 0, 0);

        [Display(Name = "Close used in the formulas", GroupName = "01 Session", Order = 170)]
        public CloseSourceMode PivotCloseSource { get; set; } = CloseSourceMode.SessionClose;

        [Display(Name = "Settlement, typed in", GroupName = "01 Session", Order = 180,
                 Description = "0 = fall back to the session close.")]
        public decimal ManualSettlement { get; set; } = 0m;

        [Display(Name = "Compute both windows", GroupName = "01 Session", Order = 190)]
        public bool ComputeBothSessions { get; set; } = true;

        #endregion

        #region 02 Formulas

        [Display(Name = "Floor pivots", GroupName = "02 Formulas", Order = 200)]
        public bool ShowFloorPivots { get; set; } = true;

        [Display(Name = "R3/S3 convention", GroupName = "02 Formulas", Order = 210,
                 Description = "Standard and Narrow are the same formula written two ways and " +
                               "always agree; Both therefore emits one level, R3sn/S3sn.")]
        public R3S3Variant R3S3Rule { get; set; } = R3S3Variant.Both;

        [Display(Name = "Also PP-anchored R3/S3", GroupName = "02 Formulas", Order = 220)]
        public bool ShowWideR3S3 { get; set; } = true;

        [Display(Name = "Camarilla", GroupName = "02 Formulas", Order = 230)]
        public bool ShowCamarilla { get; set; } = true;

        [Display(Name = "Mid pivots", GroupName = "02 Formulas", Order = 240)]
        public bool ShowMidPivots { get; set; } = false;

        [Display(Name = "Prior high / low / close", GroupName = "02 Formulas", Order = 250)]
        public bool ShowPriorHighLow { get; set; } = true;

        #endregion

        #region 03 Rendering

        [Display(Name = "Zone cluster tolerance (pts)", GroupName = "03 Rendering", Order = 300,
                 Description = "Levels within this of each other become one band.")]
        public decimal ZoneClusterTolerance { get; set; } = 6m;

        [Display(Name = "Render range (pts)", GroupName = "03 Rendering", Order = 310,
                 Description = "Only bands this close to price draw. The rest stay in memory for " +
                               "the log -- true, but too far away to be a decision.")]
        public decimal RenderRangePts { get; set; } = 400m;

        [Display(Name = "Max zones per side", GroupName = "03 Rendering", Order = 320)]
        public int MaxZonesPerSide { get; set; } = 3;

        // The panel reaches further than the chart on purpose. Shading a band 900 points away
        // would be noise, but NOT KNOWING it is there is worse: the proximity filter was hiding
        // the strongest level on the board whenever it happened to be far from price.
        [Display(Name = "Ladder range (pts)", GroupName = "03 Rendering", Order = 322,
                 Description = "How far the panel's ABOVE / BELOW ladder looks. Wider than the " +
                               "render range, so a strong band off-chart is still listed.")]
        public decimal LadderRangePts { get; set; } = 1500m;

        [Display(Name = "Ladder rows per side", GroupName = "03 Rendering", Order = 324)]
        public int MaxLadderPerSide { get; set; } = 4;

        [Display(Name = "Long zone colour (below price)", GroupName = "03 Rendering", Order = 330)]
        public MColor LongZoneColor { get; set; } = MColor.FromRgb(40, 190, 110);

        [Display(Name = "Short zone colour (above price)", GroupName = "03 Rendering", Order = 340)]
        public MColor ShortZoneColor { get; set; } = MColor.FromRgb(225, 70, 70);

        [Display(Name = "Failed zone colour", GroupName = "03 Rendering", Order = 350)]
        public MColor FailedZoneColor { get; set; } = MColor.FromRgb(110, 110, 118);

        [Display(Name = "Weakest zone opacity (%)", GroupName = "03 Rendering", Order = 360)]
        public int ZoneMinOpacity { get; set; } = 14;

        [Display(Name = "Strongest zone opacity (%)", GroupName = "03 Rendering", Order = 370)]
        public int ZoneMaxOpacity { get; set; } = 46;

        [Display(Name = "Zone labels", GroupName = "03 Rendering", Order = 380)]
        public bool ShowZoneLabels { get; set; } = true;

        [Display(Name = "Show raw levels (debug)", GroupName = "03 Rendering", Order = 390,
                 Description = "Restores the v1 line-per-level rendering. Useful for decoding a " +
                               "caller, unreadable for trading.")]
        public bool ShowRawLevels { get; set; } = false;

        // Renamed from the ShowBiasPanel on/off: the choice is three-way now, and a saved
        // boolean under the old name would have carried no meaning into the new setting.
        [Display(Name = "Readout", GroupName = "03 Rendering", Order = 395,
                 Description = "Full = the whole board. One line = a single verdict strip, top " +
                               "right. Off = zones only.")]
        public PanelMode Readout { get; set; } = PanelMode.Full;

        #endregion

        #region 04 Sessions

        [Display(Name = "Asia starts (Houston)", GroupName = "04 Sessions", Order = 400)]
        public TimeSpan AsiaStartCt { get; set; } = new TimeSpan(17, 0, 0);

        [Display(Name = "Asia ends (Houston)", GroupName = "04 Sessions", Order = 405)]
        public TimeSpan AsiaEndCt { get; set; } = new TimeSpan(2, 0, 0);

        [Display(Name = "London starts (Houston)", GroupName = "04 Sessions", Order = 410)]
        public TimeSpan LondonStartCt { get; set; } = new TimeSpan(2, 0, 0);

        [Display(Name = "London ends (Houston)", GroupName = "04 Sessions", Order = 415)]
        public TimeSpan LondonEndCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "NY starts (Houston)", GroupName = "04 Sessions", Order = 420)]
        public TimeSpan NyStartCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "NY ends (Houston)", GroupName = "04 Sessions", Order = 425)]
        public TimeSpan NyEndCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(Name = "Session POCs", GroupName = "04 Sessions", Order = 430)]
        public bool ShowSessionPocs { get; set; } = true;

        [Display(Name = "Session VWAPs", GroupName = "04 Sessions", Order = 435,
                 Description = "Prior and developing Asia / London VWAP. Auto-trendlines are " +
                               "deliberately not implemented -- pick different swings and you get " +
                               "a different line, so it would only ever agree with you.")]
        public bool ShowSessionVwaps { get; set; } = true;

        [Display(Name = "Value area (%)", GroupName = "04 Sessions", Order = 440)]
        public decimal SessionValueAreaPercent { get; set; } = 70m;

        // Renamed from NakedPocLookbackDays, whose default this doubles. A naked POC three weeks
        // old is still naked and still pulls; ten sessions was cutting off live magnets.
        [Display(Name = "Magnet lookback (days)", GroupName = "04 Sessions", Order = 445,
                 Description = "How far back to hunt naked POCs and unrepaired extremes.")]
        public int MagnetLookbackDays { get; set; } = 20;

        [Display(Name = "Poor extreme volume ratio", GroupName = "04 Sessions", Order = 450,
                 Description = "The extreme tick counts as un-tapered when it holds this share " +
                               "of the volume three ticks back.")]
        public decimal PoorExtremeVolRatio { get; set; } = 0.35m;

        [Display(Name = "Unfinished repair (ticks)", GroupName = "04 Sessions", Order = 455)]
        public int UnfinishedRepairTicks { get; set; } = 4;

        [Display(Name = "Max session doubt (% of range)", GroupName = "04 Sessions", Order = 460,
                 Description = "When the bars straddling a session boundary leave its high or low " +
                               "less certain than this, the whole session is dropped rather than " +
                               "published with a caution. A 4h chart cannot measure a 6.5h session.")]
        public decimal SessionDoubtLimitPercent { get; set; } = 10m;

        #endregion

        #region 05 Absorption

        [Display(Name = "Validate against the tape", GroupName = "05 Absorption", Order = 500)]
        public bool EnableAbsorption { get; set; } = true;

        [Display(Name = "Delta multiplier", GroupName = "05 Absorption", Order = 510,
                 Description = "Size bar as a multiple of recent average absolute delta. " +
                               "Adaptive on purpose -- a fixed contract count is wrong the moment " +
                               "the session changes character.")]
        public decimal AbsorptionDeltaMultiplier { get; set; } = 1.5m;

        [Display(Name = "Average over (bars)", GroupName = "05 Absorption", Order = 520)]
        public int AbsorptionAverageBars { get; set; } = 20;

        [Display(Name = "Max progress (ticks)", GroupName = "05 Absorption", Order = 530)]
        public int MaxProgressTicks { get; set; } = 8;

        [Display(Name = "Rejection confirm (ticks)", GroupName = "05 Absorption", Order = 540)]
        public int RejectionConfirmTicks { get; set; } = 6;

        [Display(Name = "Acceptance (ticks)", GroupName = "05 Absorption", Order = 550)]
        public int AcceptanceTicks { get; set; } = 12;

        [Display(Name = "Acceptance bars", GroupName = "05 Absorption", Order = 555)]
        public int AcceptanceBars { get; set; } = 2;

        [Display(Name = "Big-print percentile", GroupName = "05 Absorption", Order = 560)]
        public decimal ClusterVolPctile { get; set; } = 90m;

        #endregion

        #region 06 Base rates

        [Display(Name = "Measure base rates", GroupName = "06 Base rates", Order = 600,
                 Description = "Replay the same zone construction over prior sessions and count " +
                               "what price actually did. The only number here that has been " +
                               "checked against anything.")]
        public bool MeasureBaseRates { get; set; } = true;

        [Display(Name = "Sessions to measure", GroupName = "06 Base rates", Order = 610)]
        public int StatsLookbackDays { get; set; } = 30;

        [Display(Name = "Counts as held (pts)", GroupName = "06 Base rates", Order = 620,
                 Description = "How far price must come back off a band to score as a hold.")]
        public decimal StatsRejectPts { get; set; } = 30m;

        [Display(Name = "Counts as broken (pts)", GroupName = "06 Base rates", Order = 630)]
        public decimal StatsAcceptPts { get; set; } = 15m;

        [Display(Name = "Minimum sample", GroupName = "06 Base rates", Order = 640,
                 Description = "Below this many prior touches no rate is quoted at all -- the " +
                               "sample size is reported instead. 3-for-4 reads as 75% and means " +
                               "nothing.")]
        public int MinSampleSize { get; set; } = 8;

        [Display(Name = "Strong band threshold", GroupName = "06 Base rates", Order = 650,
                 Description = "Score at or above which a band is bucketed as strong.")]
        public decimal StrongScore { get; set; } = 4m;

        #endregion

        #region 06b Overnight top / bottom tick

        [Display(Name = "Overnight top / bottom tick", GroupName = "06b Overnight tick", Order = 670,
                 Description = "Turns the best band each side into an order: sell the top of " +
                               "resistance, buy the bottom of support. Shown as TOP TICK / BOT " +
                               "TICK in the readout, top left.")]
        public bool ShowOvernightPlan { get; set; } = true;

        [Display(Name = "Stop beyond the band (ticks)", GroupName = "06b Overnight tick", Order = 675)]
        public int PlanStopTicks { get; set; } = 8;

        #endregion

        #region 07 Weights

        [Display(Name = "Floor pivot", GroupName = "07 Weights", Order = 600)]
        public decimal WeightFloor { get; set; } = 1.0m;

        [Display(Name = "Camarilla", GroupName = "07 Weights", Order = 610)]
        public decimal WeightCamarilla { get; set; } = 1.0m;

        [Display(Name = "Mid pivot", GroupName = "07 Weights", Order = 620)]
        public decimal WeightMid { get; set; } = 1.0m;

        [Display(Name = "Prior H/L/C", GroupName = "07 Weights", Order = 630)]
        public decimal WeightPriorHlc { get; set; } = 1.0m;

        [Display(Name = "Session POC", GroupName = "07 Weights", Order = 640)]
        public decimal WeightSessionPoc { get; set; } = 1.5m;

        [Display(Name = "Naked POC", GroupName = "07 Weights", Order = 650)]
        public decimal WeightNakedPoc { get; set; } = 2.0m;

        [Display(Name = "Unfinished extreme", GroupName = "07 Weights", Order = 660)]
        public decimal WeightPoorExtreme { get; set; } = 2.0m;

        [Display(Name = "Session VWAP", GroupName = "07 Weights", Order = 670)]
        public decimal WeightVwap { get; set; } = 0.75m;

        #endregion

        #region 08 Caller match

        [Display(Name = "Caller levels", GroupName = "08 Caller match", Order = 700,
                 Description = "Comma separated. Full prices (28872) or last-three shorthand (872).")]
        public string CallerLevels { get; set; } = string.Empty;

        [Display(Name = "Caller directions", GroupName = "08 Caller match", Order = 710,
                 Description = "Optional override, e.g. 872:L,293:S.")]
        public string CallerDirections { get; set; } = string.Empty;

        [Display(Name = "Exact tolerance (pts)", GroupName = "08 Caller match", Order = 720)]
        public decimal MatchTolerancePts { get; set; } = 3.0m;

        [Display(Name = "Near tolerance (pts)", GroupName = "08 Caller match", Order = 730)]
        public decimal LooseTolerancePts { get; set; } = 10.0m;

        [Display(Name = "Shorthand search range (pts)", GroupName = "08 Caller match", Order = 740)]
        public decimal ShorthandRangePts { get; set; } = 500m;

        [Display(Name = "Match table", GroupName = "08 Caller match", Order = 750)]
        public bool ShowMatchTable { get; set; } = true;

        #endregion

        #region 09 Reaction

        [Display(Name = "Track reactions", GroupName = "09 Reaction", Order = 800)]
        public bool TrackReactions { get; set; } = true;

        [Display(Name = "Counts as a move (pts)", GroupName = "09 Reaction", Order = 810)]
        public decimal ReactionAwayPts { get; set; } = 30m;

        [Display(Name = "Within (hours)", GroupName = "09 Reaction", Order = 820)]
        public decimal ReactionWindowHours { get; set; } = 2m;

        [Display(Name = "Target (pts)", GroupName = "09 Reaction", Order = 830)]
        public decimal ReactionTargetPts { get; set; } = 50m;

        [Display(Name = "Stop (pts)", GroupName = "09 Reaction", Order = 840)]
        public decimal ReactionStopPts { get; set; } = 30m;

        #endregion

        #region 10 Log and alerts

        [Display(Name = "Write the CSV log", GroupName = "10 Log and alerts", Order = 900)]
        public bool WriteCsvLog { get; set; } = true;

        [Display(Name = "Log file", GroupName = "10 Log and alerts", Order = 910)]
        public string LogPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATAS", "PivotDecoder_log.csv");

        [Display(Name = "Alert on approach", GroupName = "10 Log and alerts", Order = 920)]
        public bool AlertOnApproach { get; set; } = false;

        [Display(Name = "Approach distance (pts)", GroupName = "10 Log and alerts", Order = 930)]
        public decimal ApproachPts { get; set; } = 15m;

        #endregion

        #region Model

        private sealed class Analysis
        {
            public DateTime TradeDate;
            public TradingDay Day;
            public TradingDay PriorRth;
            public TradingDay PriorEth;

            public decimal Price;
            public decimal Tick;

            public int SessionFirstBar = -1;
            public decimal SessionOpen;

            /// <summary>Everything computed. Logged in full; only a few of these ever draw.</summary>
            public List<PivotLevel> Levels = new List<PivotLevel>();

            public List<Zone> AllZones = new List<Zone>();

            /// <summary>Ranked, both sides, out to the ladder range. What the panel lists.</summary>
            public List<Zone> Ladder = new List<Zone>();

            /// <summary>The subset of the ladder close enough to shade. Same ranks.</summary>
            public List<Zone> Drawn = new List<Zone>();

            public Zone LongZone;
            public Zone ShortZone;

            public NakedPoc NextNakedPoc;
            public PoorExtreme NextPoorExtreme;

            /// <summary>Sessions dropped because this timeframe cannot measure their extremes.</summary>
            public List<string> Unmeasured = new List<string>();

            public Profile PriorRthProfile;
            public string Inventory = "unknown";

            /// <summary>Set when price is sitting inside a band rather than between two.</summary>
            public Zone InsideZone;

            public TradePlan ShortPlan;
            public TradePlan LongPlan;

            public BaseRate ShortRate;
            public BaseRate LongRate;

            /// <summary>SHORT, LONG or BALANCED, with the reason in plain words.</summary>
            public string Bias = "BALANCED";
            public string BiasWhy = string.Empty;
            public bool BiasIsShort;

            public List<CallerReport> Reports = new List<CallerReport>();
            public string Note = string.Empty;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar != CurrentBar - 1) return;
            _dirty = true;
        }

        protected override void OnFinishRecalculate()
        {
            _dirty = true;
        }

        protected override void OnRecalculate()
        {
            lock (_sync)
            {
                _analysis = null;
                _clock = null;
                _builtBarCount = -1;
                _alerted.Clear();
                _loggedDate = null;
                _logSignature = null;
                ResetCaches();
            }

            _dirty = true;
        }

        private void ResetCaches()
        {
            _facts.Clear();
            _levelVolumes.Clear();
            _profiles.Clear();
            _processed = -1;
            _access = LevelAccess.Unknown;
            _sdkWarning = null;
            _deltaWarning = null;
            _timeframeNote = null;
            _boundaryNote = null;
            _barSize = TimeSpan.Zero;
            _pierced.Clear();
            _events = null;
            _eventsFor = default(DateTime);
            _days = null;
        }

        /// <summary>
        /// Has price traded at or through <paramref name="price"/> since bar <paramref name="from"/>?
        ///
        /// This has to be asked BAR BY BAR. The obvious version compares the price against each
        /// later session's high and low, and that is wrong: a session that gapped over the level
        /// has a range spanning it while never having traded there. Under the old test every naked
        /// POC sitting inside an overnight gap was quietly retired -- which removed exactly the
        /// magnets that matter most, because a gap is what leaves them naked in the first place.
        /// </summary>
        private bool TradedThrough(int from, decimal price, decimal beyond, bool upward)
        {
            var key = from + "|" + price.ToString("F4", CultureInfo.InvariantCulture) + "|" +
                      beyond.ToString("F4", CultureInfo.InvariantCulture) + "|" + upward;

            PierceState state;

            if (!_pierced.TryGetValue(key, out state))
            {
                state = new PierceState { CheckedTo = from };
                _pierced[key] = state;
            }

            if (state.Hit) return true;

            if (Pierce.Any(_facts, state.CheckedTo + 1, _processed, price, beyond, upward))
                state.Hit = true;

            state.CheckedTo = _processed;
            return state.Hit;
        }

        /// <summary>
        /// Fingerprint of every setting the MODEL depends on. Editing a session window has to
        /// rebuild; changing a colour must not. ATAS does not reliably recalculate on a plain
        /// property edit, so the change is detected here rather than waited for.
        /// </summary>
        private string ConfigKey()
        {
            var w = Weights();

            return string.Join("|", new[]
            {
                TimeZoneId, ((int)BarTimes).ToString(), MaintenanceHaltHourCt.ToString(),
                ((int)PriorSessionSource).ToString(),
                RthStartCt.ToString(), RthEndCt.ToString(),
                EthStartCt.ToString(), EthEndCt.ToString(),
                ((int)PivotCloseSource).ToString(),
                ManualSettlement.ToString(CultureInfo.InvariantCulture),
                ComputeBothSessions.ToString(),
                ShowFloorPivots.ToString(), ((int)R3S3Rule).ToString(), ShowWideR3S3.ToString(),
                ShowCamarilla.ToString(), ShowMidPivots.ToString(), ShowPriorHighLow.ToString(),
                AsiaStartCt.ToString(), AsiaEndCt.ToString(),
                LondonStartCt.ToString(), LondonEndCt.ToString(),
                NyStartCt.ToString(), NyEndCt.ToString(),
                ShowSessionPocs.ToString(), ShowSessionVwaps.ToString(),
                SessionValueAreaPercent.ToString(CultureInfo.InvariantCulture),
                MagnetLookbackDays.ToString(),
                PoorExtremeVolRatio.ToString(CultureInfo.InvariantCulture),
                UnfinishedRepairTicks.ToString(),
                SessionDoubtLimitPercent.ToString(CultureInfo.InvariantCulture),
                ZoneClusterTolerance.ToString(CultureInfo.InvariantCulture),
                RenderRangePts.ToString(CultureInfo.InvariantCulture),
                MaxZonesPerSide.ToString(),
                LadderRangePts.ToString(CultureInfo.InvariantCulture),
                MaxLadderPerSide.ToString(),
                EnableAbsorption.ToString(),
                AbsorptionDeltaMultiplier.ToString(CultureInfo.InvariantCulture),
                AbsorptionAverageBars.ToString(), MaxProgressTicks.ToString(),
                RejectionConfirmTicks.ToString(), AcceptanceTicks.ToString(),
                AcceptanceBars.ToString(),
                ClusterVolPctile.ToString(CultureInfo.InvariantCulture),
                MeasureBaseRates.ToString(), StatsLookbackDays.ToString(),
                StatsRejectPts.ToString(CultureInfo.InvariantCulture),
                StatsAcceptPts.ToString(CultureInfo.InvariantCulture),
                MinSampleSize.ToString(), StrongScore.ToString(CultureInfo.InvariantCulture),
                PlanStopTicks.ToString(), ShowOvernightPlan.ToString(),
                w.Floor.ToString(CultureInfo.InvariantCulture),
                w.Camarilla.ToString(CultureInfo.InvariantCulture),
                w.Mid.ToString(CultureInfo.InvariantCulture),
                w.PriorHlc.ToString(CultureInfo.InvariantCulture),
                w.SessionPoc.ToString(CultureInfo.InvariantCulture),
                w.NakedPoc.ToString(CultureInfo.InvariantCulture),
                w.PoorExtreme.ToString(CultureInfo.InvariantCulture),
                w.Vwap.ToString(CultureInfo.InvariantCulture),
                CallerLevels ?? string.Empty, CallerDirections ?? string.Empty,
                MatchTolerancePts.ToString(CultureInfo.InvariantCulture),
                LooseTolerancePts.ToString(CultureInfo.InvariantCulture),
                ShorthandRangePts.ToString(CultureInfo.InvariantCulture),
                TrackReactions.ToString(),
                ReactionAwayPts.ToString(CultureInfo.InvariantCulture),
                ReactionWindowHours.ToString(CultureInfo.InvariantCulture),
                ReactionTargetPts.ToString(CultureInfo.InvariantCulture),
                ReactionStopPts.ToString(CultureInfo.InvariantCulture)
            });
        }

        private ZoneWeights Weights()
        {
            return new ZoneWeights
            {
                Floor = WeightFloor,
                Camarilla = WeightCamarilla,
                Mid = WeightMid,
                PriorHlc = WeightPriorHlc,
                SessionPoc = WeightSessionPoc,
                NakedPoc = WeightNakedPoc,
                PoorExtreme = WeightPoorExtreme,
                Vwap = WeightVwap
            };
        }

        private SessionConfig BuildSessionConfig()
        {
            return new SessionConfig
            {
                RthStart = RthStartCt,
                RthEnd = RthEndCt,
                EthStart = EthStartCt,
                EthEnd = EthEndCt,
                Intraday = new List<NamedWindow>
                {
                    new NamedWindow("Asia", AsiaStartCt, AsiaEndCt),
                    new NamedWindow("London", LondonStartCt, LondonEndCt),
                    new NamedWindow("NY", NyStartCt, NyEndCt)
                }
            };
        }

        private AbsorptionConfig BuildAbsorptionConfig()
        {
            return new AbsorptionConfig
            {
                DeltaMultiplier = AbsorptionDeltaMultiplier,
                AverageBars = Math.Max(1, AbsorptionAverageBars),
                MaxProgressTicks = MaxProgressTicks,
                RejectionConfirmTicks = RejectionConfirmTicks,
                AcceptanceTicks = AcceptanceTicks,
                AcceptanceBars = Math.Max(1, AcceptanceBars),
                ClusterPercentile = ClusterVolPctile
            };
        }

        private ReactionConfig BuildReactionConfig()
        {
            return new ReactionConfig
            {
                AwayPts = ReactionAwayPts,
                WindowHours = (double)ReactionWindowHours,
                TargetPts = ReactionTargetPts,
                StopPts = ReactionStopPts
            };
        }

        private decimal Tick()
        {
            var tick = InstrumentInfo != null ? InstrumentInfo.TickSize : 0m;
            return tick > 0m ? tick : 0.25m;
        }

        #endregion

        #region Per-bar cache

        private enum LevelAccess { Unknown, AllLevels, PerPrice, None }

        /// <summary>
        /// The single point where per-price data is read out of the SDK.
        ///
        /// Two accessors exist across SDK versions and neither is guaranteed: the enumerator, and
        /// a per-price lookup that has to be walked tick by tick. This tries the cheap one, falls
        /// back to the walk, and if neither answers it records a warning and leaves the profile
        /// and absorption features switched off. It never fabricates level data from OHLC -- a POC
        /// or an absorption verdict invented from a bar's range would look exactly as convincing
        /// on the chart as a real one.
        /// </summary>
        private bool ReadLevels(int bar, List<PriceVolume> into)
        {
            into.Clear();
            if (_access == LevelAccess.None) return false;

            var candle = GetCandle(bar);
            if (candle == null) return false;

            if (_access == LevelAccess.Unknown || _access == LevelAccess.AllLevels)
            {
                try
                {
                    var all = candle.GetAllPriceLevels();

                    if (all != null)
                    {
                        foreach (var info in all)
                        {
                            if (info == null || info.Volume <= 0m) continue;

                            into.Add(new PriceVolume
                            {
                                Price = info.Price,
                                Volume = info.Volume,
                                Bid = info.Bid,
                                Ask = info.Ask
                            });
                        }
                    }

                    if (into.Count > 0)
                    {
                        _access = LevelAccess.AllLevels;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _sdkWarning = "GetAllPriceLevels unavailable (" + ex.GetType().Name +
                                  "), using the per-price lookup";
                    _access = LevelAccess.PerPrice;
                }
            }

            if (_access == LevelAccess.Unknown || _access == LevelAccess.PerPrice)
            {
                try
                {
                    var tick = Tick();
                    var walked = 0;

                    for (var price = candle.Low; price <= candle.High && walked < 4000;
                         price += tick, walked++)
                    {
                        var info = candle.GetPriceVolumeInfo(price);
                        if (info == null || info.Volume <= 0m) continue;

                        into.Add(new PriceVolume
                        {
                            Price = price,
                            Volume = info.Volume,
                            Bid = info.Bid,
                            Ask = info.Ask
                        });
                    }

                    if (into.Count > 0)
                    {
                        _access = LevelAccess.PerPrice;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _sdkWarning = "no per-price volume from this feed (" + ex.GetType().Name + ")";
                    _access = LevelAccess.None;
                    return false;
                }
            }

            // An empty read on one bar is normal. Only conclude the feed carries nothing once
            // enough closed bars have come back empty to rule out a quiet patch.
            if (bar > 60 && _profiles.Count == 0)
            {
                _access = LevelAccess.None;
                _sdkWarning = "this feed carries no volume-at-price on these bars";
            }

            return false;
        }

        /// <summary>
        /// Folds every newly CLOSED bar into the caches. The forming bar is deliberately excluded:
        /// its footprint changes on every tick, and a profile built from it would flicker.
        /// </summary>
        private void FoldNewBars(SessionConfig cfg)
        {
            var lastClosed = CurrentBar - 2;
            if (lastClosed < 0) return;

            var rows = new List<PriceVolume>();

            for (var bar = _processed + 1; bar <= lastClosed; bar++)
            {
                var candle = GetCandle(bar);
                if (candle == null) break;

                var facts = new BarFacts
                {
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    Delta = candle.Delta,
                    Volume = candle.Volume
                };

                decimal[] volumes = null;

                if (ReadLevels(bar, rows))
                {
                    volumes = new decimal[rows.Count];
                    var max = 0m;

                    for (var i = 0; i < rows.Count; i++)
                    {
                        volumes[i] = rows[i].Volume;
                        if (rows[i].Volume > max) max = rows[i].Volume;
                    }

                    facts.MaxLevelVolume = max;
                    Accumulate(bar, rows, cfg);

                    // The whole absorption engine rests on Delta meaning ask-side minus bid-side.
                    // If a future SDK flipped that convention, every validation would invert and
                    // still look plausible, so it is checked against the levels themselves rather
                    // than assumed.
                    if (_deltaWarning == null && bar > 30 && Math.Abs(facts.Delta) > 0m)
                    {
                        var fromLevels = 0m;
                        foreach (var row in rows) fromLevels += row.Ask - row.Bid;

                        if (Math.Abs(fromLevels) > 0m && Math.Sign(fromLevels) != Math.Sign(facts.Delta))
                            _deltaWarning = "candle Delta disagrees in sign with ask-minus-bid " +
                                            "from the price levels -- absorption reads may be inverted";
                    }
                }

                while (_facts.Count < bar) { _facts.Add(new BarFacts()); _levelVolumes.Add(null); }

                _facts.Add(facts);
                _levelVolumes.Add(volumes);
            }

            _processed = lastClosed;
        }

        /// <summary>
        /// What this timeframe can and cannot answer, said plainly rather than left as a silently
        /// missing level set.
        /// </summary>
        private void NoteTimeframe(SessionConfig cfg)
        {
            var size = BarMath.Describe(_barSize);
            var unresolved = new List<string>();

            if (!SessionScan.Resolvable(_barSize, cfg.RthStart, cfg.RthEnd)) unresolved.Add("RTH");

            foreach (var named in cfg.Intraday)
                if (!SessionScan.Resolvable(_barSize, named.Start, named.End)) unresolved.Add(named.Name);

            if (unresolved.Count > 0)
            {
                _timeframeNote = size + " bars cannot resolve " +
                                 string.Join(" / ", unresolved.ToArray()) +
                                 " -- those windows are shorter than one bar. Full-day levels only; " +
                                 "use 1h or finer for session levels.";
                return;
            }

            // Whether the boundary bars straddle the window is not the interesting question --
            // on most timeframes they always do. The interesting question is whether one of them
            // actually SET an extreme, because only then is any published number in doubt. Asked
            // per session, on the window actually in use, and answered with the size of the doubt
            // rather than a standing caution the eye learns to skip.
            _timeframeNote = null;
        }

        /// <summary>
        /// Names the levels a straddling bar put in doubt, and by how much. Silent -- which is the
        /// common case -- when every extreme came from a bar lying wholly inside its window.
        /// </summary>
        private string BoundaryDoubt(Analysis a)
        {
            if (_barSize <= TimeSpan.Zero) return null;

            var size = BarMath.Describe(_barSize);

            // Dropped outright: say that first, because it explains missing levels.
            if (a.Unmeasured.Count > 0)
                return size + " bars cannot measure the " +
                       string.Join(" or ", a.Unmeasured.ToArray()) +
                       " session -- its edge bars straddle the boundary and leave the high or low " +
                       "further than " + PivotLog.Num(SessionDoubtLimitPercent) +
                       "% of the range in doubt, which moves every level off it by more than any " +
                       "tolerance here. Those levels are withheld. Use 1h or finer.";

            var parts = new List<string>();

            AddDoubt(parts, "RTH", a.PriorRth == null ? (Window?)null : a.PriorRth.Rth);
            AddDoubt(parts, "24h", a.PriorEth == null ? (Window?)null : a.PriorEth.Eth);

            if (parts.Count == 0) return null;

            return size + " bars straddle the session boundary and one of them set an extreme: " +
                   string.Join("; ", parts.ToArray()) +
                   ". Within tolerance, but 15m or finer pins it exactly.";
        }

        private void AddDoubt(List<string> parts, string name, Window? window)
        {
            if (window == null || !window.Value.Valid) return;

            var w = window.Value;
            var tick = Tick();

            // A doubt smaller than a tick is not a doubt.
            if (w.HighDoubt >= tick)
                parts.Add("prior " + name + " high " + PivotLog.Num(w.High) + " is between " +
                          Bounds(w.InsideHigh, w.High) +
                          " (" + PivotLog.Num(w.HighDoubt) + " pts)");

            if (w.LowDoubt >= tick)
                parts.Add("prior " + name + " low " + PivotLog.Num(w.Low) + " is between " +
                          Bounds(w.Low, w.InsideLow) +
                          " (" + PivotLog.Num(w.LowDoubt) + " pts)");
        }

        /// <summary>Always lowest first, whichever way the pair arrives.</summary>
        private static string Bounds(decimal a, decimal b)
        {
            return PivotLog.Num(Math.Min(a, b)) + " and " + PivotLog.Num(Math.Max(a, b));
        }

        private void Accumulate(int bar, List<PriceVolume> rows, SessionConfig cfg)
        {
            DateTime local;

            try { local = _clock.ToLocal(GetCandle(bar).Time); }
            catch { return; }

            var date = SessionScan.TradeDateOf(local, cfg);
            if (date == null) return;

            AddTo(ProfileKey(date.Value, "ETH"), rows);

            // Same overlap rule as the session scan, or the profile and the H/L/C would be built
            // from different sets of bars on any chart coarser than the window boundaries.
            if (SessionScan.Resolvable(_barSize, cfg.RthStart, cfg.RthEnd) &&
                SessionScan.Overlaps(local.TimeOfDay, _barSize, cfg.RthStart, cfg.RthEnd))
                AddTo(ProfileKey(date.Value, "RTH"), rows);

            foreach (var named in cfg.Intraday)
                if (SessionScan.Resolvable(_barSize, named.Start, named.End) &&
                    SessionScan.Overlaps(local.TimeOfDay, _barSize, named.Start, named.End))
                    AddTo(ProfileKey(date.Value, named.Name), rows);
        }

        private static string ProfileKey(DateTime date, string window)
        {
            return date.ToString("yyyyMMdd") + "|" + window;
        }

        private void AddTo(string key, List<PriceVolume> rows)
        {
            Dictionary<decimal, decimal> bucket;

            if (!_profiles.TryGetValue(key, out bucket))
            {
                bucket = new Dictionary<decimal, decimal>();
                _profiles[key] = bucket;
            }

            foreach (var row in rows)
            {
                decimal at;
                bucket.TryGetValue(row.Price, out at);
                bucket[row.Price] = at + row.Volume;
            }
        }

        private List<PriceVolume> RowsFor(DateTime date, string window)
        {
            var rows = new List<PriceVolume>();

            Dictionary<decimal, decimal> bucket;
            if (!_profiles.TryGetValue(ProfileKey(date, window), out bucket)) return rows;

            foreach (var pair in bucket)
                rows.Add(new PriceVolume { Price = pair.Key, Volume = pair.Value });

            rows.Sort(delegate (PriceVolume a, PriceVolume b) { return a.Price.CompareTo(b.Price); });
            return rows;
        }

        private Profile ProfileFor(DateTime date, string window)
        {
            var rows = RowsFor(date, window);

            if (rows.Count == 0)
                return new Profile { Problem = _sdkWarning ?? "no volume-at-price for this session" };

            return ProfileMath.Build(rows, SessionValueAreaPercent);
        }

        #endregion

        #region Build

        private void Rebuild()
        {
            lock (_sync)
            {
                var key = ConfigKey();

                // A session-window or value-area change invalidates every cached profile.
                if (_configKey != null && _configKey != key) ResetCaches();

                _configKey = key;
                _builtBarCount = CurrentBar;
                _dirty = false;
                _analysis = null;

                var nowUtc = UtcTime.Year > 2000 ? UtcTime : DateTime.UtcNow;
                var marketNow = MarketTime.Year > 2000 ? MarketTime : default(DateTime);

                _barSize = BarMath.Duration(_bars, 400);

                _clock = BarClockContext.Create(TimeZoneId, BarTimes, _bars, nowUtc, marketNow,
                                                MaintenanceHaltHourCt, _barSize);
                if (!_clock.Valid || _bars.Count == 0) return;

                var cfg = BuildSessionConfig();
                var days = SessionScan.Scan(_bars, _clock, cfg, _barSize);

                NoteTimeframe(cfg);
                if (days.Count == 0) return;

                FoldNewBars(cfg);

                var current = days[days.Count - 1];
                var price = _bars.Close(_bars.Count - 1);

                var callers = CallerParse.Parse(CallerLevels, price, ShorthandRangePts);
                CallerParse.ApplyDirections(callers, CallerDirections);

                _days = days;
                _analysis = Analyze(days, current.TradeDate, callers, price, true);

                if (WriteCsvLog) FlushLog(days, current, callers, price);
                if (AlertOnApproach && _analysis != null) RaiseApproachAlerts(_analysis, price);
            }
        }

        private Analysis Analyze(List<TradingDay> days, DateTime tradeDate,
                                 List<CallerLevel> callers, decimal price, bool full)
        {
            TradingDay day = null;
            foreach (var d in days) if (d.TradeDate == tradeDate) { day = d; break; }
            if (day == null) return null;

            var a = new Analysis
            {
                TradeDate = tradeDate,
                Day = day,
                Price = price,
                Tick = Tick(),
                PriorRth = SessionScan.PriorWithRth(days, tradeDate),
                PriorEth = SessionScan.PriorWithEth(days, tradeDate)
            };

            if (day.Rth.Valid) { a.SessionOpen = day.Rth.Open; a.SessionFirstBar = day.Rth.FirstBar; }
            else if (day.Eth.Valid) { a.SessionOpen = day.Eth.Open; a.SessionFirstBar = day.Eth.FirstBar; }

            AddPivotLevels(a, "RTH", a.PriorRth == null ? (Window?)null : a.PriorRth.Rth);
            AddPivotLevels(a, "ETH", a.PriorEth == null ? (Window?)null : a.PriorEth.Eth);

            AddSessionLevels(a, tradeDate);
            AddNakedPocs(a, days, tradeDate);
            AddPoorExtremes(a, days, tradeDate);

            a.AllZones = ZoneClusterer.Cluster(a.Levels, ZoneClusterTolerance, Weights());

            // Rank once over the wider set, then narrow for drawing. Both views then agree on
            // what S2 means.
            a.Ladder = ZoneClusterer.Select(a.AllZones, price, LadderRangePts, MaxLadderPerSide);
            a.Drawn = ZoneClusterer.WithinRange(a.Ladder, price, RenderRangePts, MaxZonesPerSide);

            if (full && EnableAbsorption) RunAbsorption(a);

            foreach (var zone in a.Ladder)
            {
                // A band price has accepted through is no longer a level. It stays on the chart in
                // grey so you can see what happened, but it must not be offered as a side.
                if (zone.State != null && zone.State.State == AbsorptionState.Failed) continue;

                if (zone.IsLong && (a.LongZone == null || zone.Rank < a.LongZone.Rank)) a.LongZone = zone;
                if (!zone.IsLong && (a.ShortZone == null || zone.Rank < a.ShortZone.Rank)) a.ShortZone = zone;
            }

            if (!full) return a;

            // A band price is sitting INSIDE belongs to neither side, so the ladder drops it. That
            // is the right call for "where is the long" and the wrong one for "what is happening
            // right now", which is usually the more urgent question.
            foreach (var zone in a.AllZones)
                if (price >= zone.Low && price <= zone.High) { a.InsideZone = zone; break; }

            a.Inventory = ReadInventory(a);
            _boundaryNote = BoundaryDoubt(a);
            SetBias(a);
            BuildPlans(a);

            a.Reports = Matcher.Build(callers, a.Levels, MatchTolerancePts, LooseTolerancePts);

            if (TrackReactions) TrackAll(a);

            return a;
        }

        private StatsConfig BuildStatsConfig()
        {
            return new StatsConfig
            {
                RejectPts = StatsRejectPts,
                AcceptPts = StatsAcceptPts,
                MinSample = Math.Max(1, MinSampleSize),
                StrongScore = StrongScore
            };
        }

        /// <summary>
        /// The one-line verdict. Deliberately conservative: a bias is only stated when price is
        /// on one side of BOTH the prior close and prior value. Sitting inside yesterday's value
        /// is not a quiet signal, it is the absence of one, and saying otherwise would put a
        /// direction on the screen every single day whether or not there was one.
        /// </summary>
        private void SetBias(Analysis a)
        {
            if (a.InsideZone != null)
            {
                a.Bias = "IN BAND";
                a.BiasWhy = "price is inside " + PivotLog.Num(a.InsideZone.Low) + Dash +
                            PivotLog.Num(a.InsideZone.High) + " -- wait for it to pick a side";
                return;
            }

            var profile = a.PriorRthProfile;

            if (a.PriorRth == null || profile == null || !profile.Valid)
            {
                a.Bias = "NO READ";
                a.BiasWhy = "no prior value area to measure against";
                return;
            }

            var close = a.PriorRth.Rth.Close;
            var above = a.Price > close && a.Price > profile.Vah;
            var below = a.Price < close && a.Price < profile.Val;

            if (above)
            {
                a.Bias = "SHORT";
                a.BiasIsShort = true;
                a.BiasWhy = "overnight is long above yesterday's value -- those buyers are the " +
                            "ones exposed if the open sells";
                return;
            }

            if (below)
            {
                a.Bias = "LONG";
                a.BiasWhy = "overnight is short below yesterday's value -- those sellers are the " +
                            "ones exposed if the open lifts";
                return;
            }

            a.Bias = "BALANCED";
            a.BiasWhy = "price is inside yesterday's value -- no edge from position alone";
        }

        /// <summary>
        /// Turns the top band on each side into an order: entry at the far edge, stop beyond it,
        /// target the first opposing band. Plus the measured base rate for bands of that kind.
        /// </summary>
        private void BuildPlans(Analysis a)
        {
            var events = MeasureBaseRates ? History(a) : null;
            var cfg = BuildStatsConfig();

            if (a.ShortZone != null)
            {
                a.ShortPlan = PlanMath.Build(a.ShortZone.Low, a.ShortZone.High, true, a.Tick,
                                             PlanStopTicks,
                                             a.LongZone != null ? a.LongZone.High : 0m,
                                             a.LongZone != null);

                a.ShortRate = BaseRateEngine.Summarise(events, a.ShortZone.Score,
                                                       a.ShortZone.HasMagnet, cfg);
            }

            if (a.LongZone != null)
            {
                a.LongPlan = PlanMath.Build(a.LongZone.Low, a.LongZone.High, false, a.Tick,
                                            PlanStopTicks,
                                            a.ShortZone != null ? a.ShortZone.Low : 0m,
                                            a.ShortZone != null);

                a.LongRate = BaseRateEngine.Summarise(events, a.LongZone.Score,
                                                      a.LongZone.HasMagnet, cfg);
            }
        }

        /// <summary>
        /// Replays the whole construction over prior sessions: for each day, build the bands that
        /// WOULD have been known at its open, then walk that day's bars and record what happened.
        ///
        /// Rebuilt once per session roll, not per bar -- it is the only expensive thing here.
        /// </summary>
        private List<TouchEvent> History(Analysis a)
        {
            if (_events != null && _eventsFor == a.TradeDate) return _events;

            var events = new List<TouchEvent>();
            var cfg = BuildStatsConfig();
            var days = _days;

            if (days != null)
            {
                var considered = 0;

                for (var i = days.Count - 1; i >= 0 && considered < Math.Max(1, StatsLookbackDays); i--)
                {
                    var day = days[i];
                    if (day.TradeDate >= a.TradeDate || !day.Eth.Valid) continue;
                    if (day.Eth.LastBar > _processed) continue;

                    considered++;

                    var past = Analyze(days, day.TradeDate, new List<CallerLevel>(), day.Eth.Open, false);
                    if (past == null) continue;

                    foreach (var zone in past.Ladder)
                    {
                        var e = BaseRateEngine.Measure(_facts, day.Eth.FirstBar, day.Eth.LastBar,
                                                       zone.Low, zone.High, zone.IsLong,
                                                       day.Eth.High, day.Eth.Low, cfg);

                        e.Score = zone.Score;
                        e.HasMagnet = zone.HasMagnet;
                        events.Add(e);
                    }
                }
            }

            _events = events;
            _eventsFor = a.TradeDate;
            return events;
        }

        private void AddPivotLevels(Analysis a, string session, Window? window)
        {
            var wanted = ComputeBothSessions
                      || (session == "RTH") == (PriorSessionSource == SessionMode.Rth);
            if (!wanted) return;

            if (window == null || !window.Value.Valid)
            {
                a.Note += (a.Note.Length > 0 ? "   " : string.Empty) +
                          session + ": no prior session loaded";
                return;
            }

            var w = window.Value;

            // Measured, or only bracketed? Everything below is built from H, L and C, so an
            // unmeasured extreme does not degrade the levels gracefully -- it moves all of them,
            // by more than any tolerance here would tolerate.
            if (!w.Trustworthy(SessionDoubtLimitPercent))
            {
                a.Unmeasured.Add(session);
                return;
            }
            var h = w.High;
            var l = w.Low;

            var c = PivotCloseSource == CloseSourceMode.SettlementPrice && ManualSettlement > 0m
                  ? ManualSettlement
                  : w.Close;

            var floor = PivotMath.BuildFloor(session, h, l, c, R3S3Rule, ShowWideR3S3);

            if (ShowFloorPivots) a.Levels.AddRange(floor);
            if (ShowMidPivots) a.Levels.AddRange(PivotMath.BuildMids(session, floor));
            if (ShowCamarilla) a.Levels.AddRange(PivotMath.BuildCamarilla(session, h, l, c));

            // Reference levels use the window's own close, never the typed-in settlement: the
            // point of a prior close is where price stopped, not where it was marked.
            if (ShowPriorHighLow) a.Levels.AddRange(PivotMath.BuildReference(session, h, l, w.Close));

            a.Note += (a.Note.Length > 0 ? "   " : string.Empty) +
                      session + "  H " + PivotLog.Num(h) + "  L " + PivotLog.Num(l) +
                      "  C " + PivotLog.Num(c);
        }

        private void AddSessionLevels(Analysis a, DateTime tradeDate)
        {
            if (a.PriorRth != null) a.PriorRthProfile = ProfileFor(a.PriorRth.TradeDate, "RTH");

            var prior = a.PriorEth != null ? a.PriorEth.TradeDate
                      : a.PriorRth != null ? a.PriorRth.TradeDate
                      : (DateTime?)null;

            if (prior == null) return;

            if (ShowSessionPocs)
            {
                foreach (var window in new[] { "Asia", "London", "NY" })
                {
                    var profile = ProfileFor(prior.Value, window);

                    if (profile.Valid)
                        a.Levels.AddRange(PivotMath.BuildSessionProfile(window, "ETH", profile, false));
                }
            }

            if (!ShowSessionVwaps) return;

            foreach (var window in new[] { "Asia", "London" })
            {
                var priorProfile = ProfileFor(prior.Value, window);
                if (priorProfile.Valid)
                    a.Levels.Add(PivotMath.BuildVwap(window, "ETH", priorProfile.Vwap, false));

                var developing = ProfileFor(tradeDate, window);
                if (developing.Valid)
                    a.Levels.Add(PivotMath.BuildVwap(window, "ETH", developing.Vwap, true));
            }
        }

        /// <summary>
        /// Daily RTH POCs price has never traded back through. A POC retires the moment any later
        /// session's range covers it -- the magnet has been satisfied and stops being one.
        /// </summary>
        private void AddNakedPocs(Analysis a, List<TradingDay> days, DateTime tradeDate)
        {
            var lookback = Math.Max(1, MagnetLookbackDays);
            var considered = 0;

            for (var i = days.Count - 1; i >= 0 && considered < lookback; i--)
            {
                var day = days[i];
                if (day.TradeDate >= tradeDate || !day.Rth.Valid) continue;

                considered++;

                var profile = ProfileFor(day.TradeDate, "RTH");
                if (!profile.Valid) continue;

                // Bar by bar from the end of that session, not against later session ranges:
                // a gap over the POC spans it without ever trading there.
                if (TradedThrough(day.Rth.LastBar, profile.Poc, 0m, true)) continue;

                var poc = new NakedPoc
                {
                    TradeDate = day.TradeDate,
                    Price = profile.Poc,
                    Naked = true,
                    AgeDays = (int)(tradeDate - day.TradeDate).TotalDays
                };

                a.Levels.Add(PivotMath.BuildNakedPoc(poc));

                if (a.NextNakedPoc == null ||
                    Math.Abs(poc.Price - a.Price) < Math.Abs(a.NextNakedPoc.Price - a.Price))
                    a.NextNakedPoc = poc;
            }
        }

        /// <summary>
        /// Poor highs and lows over the lookback, on the RTH and 24h windows. Those are the two
        /// that matter for an overnight hold; the intraday sessions contribute their POCs instead.
        /// </summary>
        private void AddPoorExtremes(Analysis a, List<TradingDay> days, DateTime tradeDate)
        {
            var lookback = Math.Max(1, MagnetLookbackDays);
            var considered = 0;
            var repair = UnfinishedRepairTicks * a.Tick;

            for (var i = days.Count - 1; i >= 0 && considered < lookback; i--)
            {
                var day = days[i];
                if (day.TradeDate >= tradeDate) continue;

                considered++;

                CheckExtremes(a, day.TradeDate, "RTH", day.Rth, repair);
                CheckExtremes(a, day.TradeDate, "ETH", day.Eth, repair);
            }
        }

        private void CheckExtremes(Analysis a, DateTime date, string window, Window w, decimal repair)
        {
            if (!w.Valid) return;

            var rows = RowsFor(date, window);

            CheckOne(a, date, window, rows, w.High, true, w.BarsAtHigh, a.Tick, repair, w.LastBar);
            CheckOne(a, date, window, rows, w.Low, false, w.BarsAtLow, a.Tick, repair, w.LastBar);
        }

        private void CheckOne(Analysis a, DateTime date, string window, List<PriceVolume> rows,
                              decimal price, bool isHigh, int barsAtExtreme, decimal tick,
                              decimal repair, int fromBar)
        {
            string reason;

            if (!ProfileMath.IsPoorExtreme(rows, price, isHigh, tick, PoorExtremeVolRatio,
                                           barsAtExtreme, out reason))
                return;

            if (TradedThrough(fromBar, price, repair, isHigh)) return;

            var extreme = new PoorExtreme
            {
                TradeDate = date,
                Window = window,
                Price = price,
                IsHigh = isHigh,
                Reason = reason,
                Unrepaired = true,
                AgeDays = (int)(a.TradeDate - date).TotalDays
            };

            a.Levels.Add(PivotMath.BuildPoorExtreme(extreme));

            if (a.NextPoorExtreme == null ||
                Math.Abs(extreme.Price - a.Price) < Math.Abs(a.NextPoorExtreme.Price - a.Price))
                a.NextPoorExtreme = extreme;
        }

        private void RunAbsorption(Analysis a)
        {
            if (a.SessionFirstBar < 0 || _processed < a.SessionFirstBar) return;

            var cfg = BuildAbsorptionConfig();
            var deltaThreshold = AbsorptionEngine.DeltaThreshold(_facts, _processed, cfg);
            var clusterThreshold = AbsorptionEngine.ClusterThreshold(_levelVolumes, _processed, cfg);

            // Every listed band, not just the shaded ones: the panel prints a state for each.
            foreach (var zone in a.Ladder)
                zone.State = AbsorptionEngine.Run(_facts, a.SessionFirstBar, _processed,
                                                  zone.Low, zone.High, zone.IsLong, a.Tick,
                                                  deltaThreshold, clusterThreshold, cfg);
        }

        /// <summary>
        /// The overnight inventory read. Price above the prior RTH close AND above prior value is
        /// an overnight book that is net long -- and therefore something that can be liquidated
        /// into the New York open. Below both is the mirror. Inside value is balanced, which is
        /// the case where none of this tells you anything, and it says so rather than picking.
        /// </summary>
        private string ReadInventory(Analysis a)
        {
            if (a.PriorRth == null) return "unknown (no prior RTH session loaded)";

            var close = a.PriorRth.Rth.Close;
            var diff = a.Price - close;

            var versus = "px " + (diff >= 0m ? ">" : "<") + " pRTH-C " +
                         (diff >= 0m ? "+" : "-") + PivotLog.Num(Math.Abs(diff));

            var profile = a.PriorRthProfile;

            if (profile == null || !profile.Valid)
                return (diff > 0m ? "net-LONG" : diff < 0m ? "net-SHORT" : "flat") +
                       " on close only (" + versus + ", no prior value area: " +
                       (profile != null ? profile.Problem : "unavailable") + ")";

            if (diff > 0m && a.Price > profile.Vah) return "net-LONG overnight (" + versus + ", > pVAH)";
            if (diff < 0m && a.Price < profile.Val) return "net-SHORT overnight (" + versus + ", < pVAL)";

            return "balanced (" + versus + ", inside prior value " +
                   PivotLog.Num(profile.Val) + Dash + PivotLog.Num(profile.Vah) + ")";
        }

        private void TrackAll(Analysis a)
        {
            if (a.SessionFirstBar < 0) return;

            var cfg = BuildReactionConfig();
            var last = _bars.Count - 1;

            foreach (var report in a.Reports)
            {
                if (!report.Caller.Resolved) continue;

                var best = report.Best;
                if (best == null || best.Verdict != Verdict.Exact) continue;

                report.Reaction = ReactionTracker.Track(
                    _bars, _clock, a.SessionFirstBar, last,
                    best.Level.Price, a.SessionOpen, report.Caller.Direction, cfg);
            }
        }

        private void RaiseApproachAlerts(Analysis a, decimal price)
        {
            foreach (var report in a.Reports)
            {
                var best = report.Best;
                if (best == null || best.Verdict != Verdict.Exact) continue;
                if (Math.Abs(price - best.Level.Price) > ApproachPts) continue;

                var key = a.TradeDate.ToString("yyyy-MM-dd") + "|" + best.Level.Label;
                if (!_alerted.Add(key)) continue;

                AddAlert("alert1",
                    "Pivot Decoder: price within " +
                    ApproachPts.ToString(CultureInfo.InvariantCulture) + " pts of " +
                    best.Level.Label + " (caller " + report.Caller.Raw + ")");
            }
        }

        #endregion

        #region CSV

        private void FlushLog(List<TradingDay> days, TradingDay current,
                              List<CallerLevel> callers, decimal price)
        {
            var todayKey = current.TradeDate.ToString("yyyy-MM-dd");

            if (_loggedDate != null && _loggedDate != todayKey)
            {
                DateTime previous;
                if (DateTime.TryParseExact(_loggedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out previous))
                {
                    var closing = Analyze(days, previous, callers, price, true);
                    if (closing != null) Flush(closing, "CLOSED", true);
                }

                _logSignature = null;
            }

            if (_analysis != null) Flush(_analysis, "OPEN", false);
            _loggedDate = todayKey;
        }

        private void Flush(Analysis a, string status, bool force)
        {
            var rows = BuildRows(a, status);
            if (rows.Count == 0) return;

            var signature = status;
            foreach (var row in rows) signature += "\n" + PivotLog.Join(row.Fields);

            if (!force && signature == _logSignature) return;
            _logSignature = signature;

            _logError = PivotLog.Upsert(LogPath, rows);
        }

        private List<LogRow> BuildRows(Analysis a, string status)
        {
            var rows = new List<LogRow>();
            var instrument = InstrumentInfo != null && !string.IsNullOrEmpty(InstrumentInfo.Instrument)
                           ? InstrumentInfo.Instrument
                           : "unknown";

            foreach (var report in a.Reports)
            {
                var caller = report.Caller;

                var row = new LogRow
                {
                    Date = a.TradeDate.ToString("yyyy-MM-dd"),
                    Instrument = instrument,
                    CallerLevel = caller.Resolved ? PivotLog.Num(caller.Price) : "raw:" + caller.Raw,
                    Status = status,
                    InventoryRead = a.Inventory
                };

                var best = report.Best;

                if (best == null)
                {
                    row.BestMatchFamily = caller.Resolved ? "none" : "unresolved";
                    row.MatchVerdict = caller.Resolved ? "NO MATCH" : (caller.Problem ?? "unresolved");
                    row.AbsorptionValidated = "N";
                }
                else
                {
                    row.BestMatchFamily = best.Family;
                    row.BestMatchLevelName = best.Level.Name;
                    row.BestMatchPrice = PivotLog.Num(best.Level.Price);
                    row.DeltaPts = PivotLog.Num(best.Delta);
                    row.SessionModeOfMatch = best.Level.Session;
                    row.MatchVerdict = VerdictText(best.Verdict);

                    // Which band the caller's number actually landed in. A level inside an x8 zone
                    // carrying a naked POC is a different finding from one matching a lone
                    // Camarilla line, and the log has to be able to tell them apart later.
                    var zone = ZoneFor(a, caller.Price);

                    row.ZoneScore = zone != null ? zone.ScoreText : string.Empty;
                    row.ZoneMembers = zone != null ? zone.MemberList : string.Empty;
                    row.AbsorptionValidated =
                        zone != null && zone.State != null &&
                        zone.State.State == AbsorptionState.Validated ? "Y" : "N";
                }

                var r = report.Reaction;
                if (r != null)
                {
                    row.Direction = r.Direction == CallDirection.Long ? "L"
                                  : r.Direction == CallDirection.Short ? "S" : "?";

                    if (caller.DirectionOverridden) row.Direction += "*";

                    row.Touched = r.Touched ? "yes" : "no";
                    row.TouchTimeCt = r.Touched && r.TouchLocal != default(DateTime)
                                    ? r.TouchLocal.ToString("HH:mm") : string.Empty;
                    row.MaxAwayPts = PivotLog.Num(r.MaxAwayInWindow);
                    row.MovedAway = r.MovedAwayInWindow ? "yes" : "no";
                    row.MaxFavorablePts = PivotLog.Num(r.MaxFavorable);
                    row.MaxAdversePts = PivotLog.Num(r.MaxAdverse);
                    row.Outcome = r.Outcome.ToString().ToUpperInvariant();
                }

                rows.Add(row);
            }

            return rows;
        }

        /// <summary>The band a price falls in, searched across every zone, not just the drawn few.</summary>
        private static Zone ZoneFor(Analysis a, decimal price)
        {
            foreach (var zone in a.Ladder)
                if (price >= zone.Low && price <= zone.High) return zone;

            foreach (var zone in a.AllZones)
                if (price >= zone.Low && price <= zone.High) return zone;

            return null;
        }

        private static string VerdictText(Verdict v)
        {
            return v == Verdict.Exact ? "EXACT" : v == Verdict.Near ? "NEAR" : "NO MATCH";
        }

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            if (chart == null || chart.PriceChartContainer == null) return;

            var region = chart.PriceChartContainer.Region;

            try
            {
                if (_dirty || _builtBarCount != CurrentBar || _configKey != ConfigKey()) Rebuild();
            }
            catch (Exception ex)
            {
                Status(context, region, 0, "Pivot Decoder: rebuild failed " + Dash + " " + ex.Message,
                       Conv(ShortZoneColor));
                return;
            }

            var y = 0;

            if (!_selfTestOk)
            {
                Status(context, region, y++, "Pivot Decoder: MATH SELF-TEST FAILED " + Dash +
                                             " levels are not trustworthy.", Conv(ShortZoneColor));
                return;
            }

            if (_clock == null || !_clock.Valid)
            {
                Status(context, region, y, _clock != null ? _clock.Error : "Pivot Decoder: starting up.",
                       Conv(ShortZoneColor));
                return;
            }

            var a = _analysis;
            if (a == null)
            {
                Status(context, region, y, "Pivot Decoder: no completed prior session loaded.",
                       Conv(LongZoneColor));
                return;
            }

            // Each layer is caught on its own. A layer that throws would otherwise take down every
            // layer after it in silence, including the readout that would have reported it, and
            // there is no debugger on the render thread.
            Layer(context, region, ref y, "zones", delegate { RenderZones(context, region, a); });

            if (ShowRawLevels)
                Layer(context, region, ref y, "raw levels", delegate { RenderRawLevels(context, region, a); });

            if (Readout == PanelMode.Full)
                Layer(context, region, ref y, "readout", delegate { RenderFull(context, region, a); });
            else if (Readout == PanelMode.OneLine)
                Layer(context, region, ref y, "readout", delegate { RenderOneLine(context, region, a); });

            if (ShowMatchTable && a.Reports.Count > 0 && Readout != PanelMode.OneLine)
                Layer(context, region, ref y, "match table", delegate { RenderMatchTable(context, region, a); });

            var row = y;
            Layer(context, region, ref y, "warnings", delegate { RenderWarnings(context, region, row); });
        }

        private void Layer(RenderContext context, Rectangle region, ref int y, string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Status(context, region, y++, "Pivot Decoder: " + name + " layer failed " + Dash +
                                             " " + ex.Message, Conv(ShortZoneColor));
            }
        }

        private void RenderZones(RenderContext context, Rectangle region, Analysis a)
        {
            var strongest = 0m;
            foreach (var zone in a.Drawn) if (zone.Score > strongest) strongest = zone.Score;

            foreach (var zone in a.Drawn)
            {
                var yTop = Y(zone.High);
                var yBottom = Y(zone.Low);

                if (yBottom < region.Top || yTop > region.Bottom) continue;

                var top = Math.Max(region.Top, Math.Min(yTop, yBottom));
                var bottom = Math.Min(region.Bottom, Math.Max(yTop, yBottom));

                // A band of identical prices has no height; give it enough to be visible.
                if (bottom - top < 3) { top -= 1; bottom = top + 3; }

                var rect = Rectangle.FromLTRB(region.Left, top, region.Right, bottom);
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                var failed = zone.State != null && zone.State.State == AbsorptionState.Failed;

                var baseColor = failed ? FailedZoneColor
                              : zone.IsLong ? LongZoneColor : ShortZoneColor;

                context.FillRectangle(Fade(baseColor, zone.Score, strongest, failed), rect);

                var edge = new RenderPen(Conv(baseColor), 1);
                context.DrawLine(edge, rect.Left, top, rect.Right, top);
                context.DrawLine(edge, rect.Left, bottom, rect.Right, bottom);

                if (ShowZoneLabels) DrawZoneLabel(context, region, zone, Conv(baseColor), top, bottom);
                if (zone.State != null) DrawMarker(context, region, zone);
            }
        }

        /// <summary>
        /// Opacity carries the score, so the strongest band is the one the eye lands on first
        /// without anything having to be read.
        /// </summary>
        private Color Fade(MColor color, decimal score, decimal strongest, bool failed)
        {
            var min = Clamp(ZoneMinOpacity, 0, 100);
            var max = Clamp(ZoneMaxOpacity, 0, 100);
            if (max < min) max = min;

            var share = strongest > 0m ? score / strongest : 1m;
            if (share < 0m) share = 0m;
            if (share > 1m) share = 1m;

            var percent = min + (max - min) * (double)share;
            if (failed) percent = min * 0.6;

            return Color.FromArgb((int)Math.Round(255 * percent / 100.0), color.R, color.G, color.B);
        }

        private static int Clamp(int value, int low, int high)
        {
            return value < low ? low : value > high ? high : value;
        }

        /// <summary>
        /// One label per band: side, rank, range, score, state. Everything a glance has to answer.
        /// The member breakdown deliberately does not appear here -- per-member labels on the
        /// chart are exactly what made v1 unreadable; they live in the panel.
        /// </summary>
        private void DrawZoneLabel(RenderContext context, Rectangle region, Zone zone, Color color,
                                   int top, int bottom)
        {
            var state = zone.State;

            var text = (zone.IsLong ? "L" : "S") + zone.Rank + " " +
                       (zone.IsLong ? Up : Down) + " " +
                       PivotLog.Num(zone.Low) + Dash + PivotLog.Num(zone.High) +
                       " [" + zone.ScoreText + "] " +
                       (state != null ? state.StateText : "UNTESTED");

            if (state != null && state.State == AbsorptionState.Validated)
            {
                text += " " + state.Marker;
                if (state.ClusterPrint) text += " CL";
            }

            foreach (var member in zone.Members)
                if (member.Family == LevelFamily.PoorExtreme) { text += " " + PoorMark; break; }

            var size = context.MeasureString(text, _zoneFont);

            var x = region.Right - size.Width - 8;
            if (x < region.Left) x = region.Left + 2;

            var ly = (top + bottom) / 2 - size.Height / 2;
            if (ly < region.Top) ly = region.Top;
            if (ly + size.Height > region.Bottom) ly = region.Bottom - size.Height;

            context.FillRectangle(Color.FromArgb(200, 16, 16, 20),
                new Rectangle(x - 3, ly - 1, size.Width + 6, size.Height + 2));

            context.DrawString(text, _zoneFont, color, x, ly);
        }

        private void DrawMarker(RenderContext context, Rectangle region, Zone zone)
        {
            var state = zone.State;
            if (state.State != AbsorptionState.Validated || state.ResolvedBar < 0) return;

            var x = XOf(state.ResolvedBar);
            if (x < region.Left || x > region.Right) return;

            var y = Y(zone.IsLong ? zone.Low : zone.High);
            if (y < region.Top || y > region.Bottom) return;

            var color = Conv(zone.IsLong ? LongZoneColor : ShortZoneColor);
            var text = "ABS" + (zone.IsLong ? Up : Down);

            var size = context.MeasureString(text, _zoneFont);
            var ly = zone.IsLong ? y + 4 : y - size.Height - 4;

            context.DrawString(text, _zoneFont, color, x - size.Width / 2, ly);
        }

        /// <summary>The v1 rendering, kept behind a switch for decoding a caller's numbers.</summary>
        private void RenderRawLevels(RenderContext context, Rectangle region, Analysis a)
        {
            var color = Color.FromArgb(150, 170, 170, 180);
            var pen = new RenderPen(color, 1);

            foreach (var level in a.Levels)
            {
                var y = Y(level.Price);
                if (y < region.Top || y > region.Bottom) continue;

                context.DrawLine(pen, region.Left, y, region.Right, y);
                context.DrawString(level.Label, _statusFont, color, region.Left + 4, y - 12);
            }
        }

        /// <summary>
        /// The one-line verdict, top right. Everything the full board says, compressed to the
        /// single sentence you would say out loud.
        /// </summary>
        private void RenderOneLine(RenderContext context, Rectangle region, Analysis a)
        {
            var text = a.Bias;

            var zone = a.BiasIsShort ? a.ShortZone : a.LongZone;
            var plan = a.BiasIsShort ? a.ShortPlan : a.LongPlan;
            var rate = a.BiasIsShort ? a.ShortRate : a.LongRate;

            if (a.Bias == "SHORT" || a.Bias == "LONG")
            {
                if (plan != null && plan.Valid)
                {
                    text += "  " + (a.BiasIsShort ? "sell " : "buy ") + PivotLog.Num(plan.Entry) +
                            "  stop " + PivotLog.Num(plan.Stop);

                    text += plan.R > 0m
                        ? "  target " + PivotLog.Num(plan.Target) + "  " +
                          plan.R.ToString("0.#", CultureInfo.InvariantCulture) + "R"
                        : "  no target";
                }

                if (zone != null) text += "  [" + zone.ScoreText + " " + PlainState(zone) + "]";
                if (rate != null) text += "  " + rate.Text;
            }
            else
            {
                text += "  " + a.BiasWhy;
            }

            var color = a.Bias == "SHORT" ? Conv(ShortZoneColor)
                      : a.Bias == "LONG" ? Conv(LongZoneColor)
                      : Color.FromArgb(210, 210, 220);

            var rows = new List<string> { text };
            var colors = new[] { color };

            var size = context.MeasureString(text, _panelFont);
            DrawPanel(context, new Point(region.Right - size.Width - 22, region.Top + 8),
                      rows, colors, region);
        }

        private void RenderFull(RenderContext context, Rectangle region, Analysis a)
        {
            var rows = new List<string>();
            var colors = new List<Color>();

            var biasColor = a.Bias == "SHORT" ? Conv(ShortZoneColor)
                          : a.Bias == "LONG" ? Conv(LongZoneColor)
                          : Color.FromArgb(210, 210, 220);

            rows.Add("BIAS: " + a.Bias + " -- " + a.BiasWhy);
            colors.Add(biasColor);

            rows.Add("       " + a.Inventory);
            colors.Add(Color.FromArgb(170, 170, 180));

            if (ShowOvernightPlan)
            {
                AddPlan(rows, colors, a, true);
                AddPlan(rows, colors, a, false);
            }

            if (a.Unmeasured.Count > 0)
            {
                rows.Add("WITHHELD: " + string.Join(" and ", a.Unmeasured.ToArray()) +
                         " levels -- " + BarMath.Describe(_barSize) +
                         " bars cannot measure that session. Use 1h or finer.");
                colors.Add(Color.FromArgb(225, 200, 70));
            }

            rows.Add("MAGNETS: " + Magnets(a));
            colors.Add(Color.FromArgb(200, 200, 210));

            AddLadder(rows, colors, a, false);
            AddLadder(rows, colors, a, true);

            DrawPanel(context, new Point(region.Left + 8, region.Top + 8), rows, colors.ToArray(), region);
        }

        /// <summary>
        /// One side's trade, in the order it would be typed: what to do, where, where it is wrong,
        /// where it goes, and how often bands like this have actually held.
        /// </summary>
        private void AddPlan(List<string> rows, List<Color> colors, Analysis a, bool isShort)
        {
            var zone = isShort ? a.ShortZone : a.LongZone;
            var plan = isShort ? a.ShortPlan : a.LongPlan;
            var rate = isShort ? a.ShortRate : a.LongRate;
            var label = isShort ? "TOP TICK " : "BOT TICK ";
            var color = isShort ? Conv(ShortZoneColor) : Conv(LongZoneColor);

            if (zone == null || plan == null || !plan.Valid)
            {
                rows.Add(label + " none -- no band " + (isShort ? "above" : "below") + " price in range");
                colors.Add(Color.Gray);
                return;
            }

            var line = label + (isShort ? "sell " : "buy ") + PivotLog.Num(plan.Entry) +
                       "   stop " + PivotLog.Num(plan.Stop) +
                       " (" + PivotLog.Num(plan.RiskPts) + " pts)";

            line += plan.R > 0m
                ? "   target " + PivotLog.Num(plan.Target) + " (" + PivotLog.Num(plan.RewardPts) +
                  " pts, " + plan.R.ToString("0.#", CultureInfo.InvariantCulture) + "R)"
                : "   " + (plan.Problem ?? "no target");

            rows.Add(line);
            colors.Add(color);

            rows.Add("         band " + PivotLog.Num(zone.Low) + Dash + PivotLog.Num(zone.High) +
                     " [" + zone.ScoreText + "] " + PlainState(zone) +
                     "   built from " + zone.MemberList);
            colors.Add(Color.FromArgb(170, 170, 180));

            if (rate == null) return;

            rows.Add("         history: " + rate.Text + "   (" + rate.Bucket + ", " +
                     StatsLookbackDays + " sessions on this chart)");
            colors.Add(rate.Sufficient ? Color.FromArgb(190, 190, 200) : Color.Gray);
        }

        /// <summary>Words, not codes. The codes are still on the chart labels.</summary>
        private static string PlainState(Zone zone)
        {
            var state = zone.State;
            if (state == null) return "not tested yet";

            switch (state.State)
            {
                case AbsorptionState.Testing: return "being tested now";
                case AbsorptionState.Validated:
                    return "HELD -- size absorbed and price rejected" +
                           (state.ClusterPrint ? ", big print inside" : string.Empty);
                case AbsorptionState.Failed: return "BROKEN -- price accepted through";
                default: return "not tested yet";
            }
        }

        /// <summary>
        /// The ranked ladder for one side. Ordered by SCORE, not by distance -- the question it
        /// answers is which levels are strongest, and the distance column is right there for
        /// sizing once you have picked one.
        /// </summary>
        private void AddLadder(List<string> rows, List<Color> colors, Analysis a, bool longs)
        {
            rows.Add(longs ? "SUPPORT BELOW" : "RESISTANCE ABOVE");
            colors.Add(Color.FromArgb(170, 170, 180));

            var drawn = new HashSet<string>();
            foreach (var zone in a.Drawn) drawn.Add(zone.Key);

            var found = false;

            foreach (var zone in a.Ladder)
            {
                if (zone.IsLong != longs) continue;
                found = true;

                var state = zone.State;
                var away = Math.Abs(zone.Center - a.Price);

                var text = "  " + (longs ? "L" : "S") + zone.Rank + "  " +
                           Pad(PivotLog.Num(zone.Low) + Dash + PivotLog.Num(zone.High), 20) +
                           Pad("[" + zone.ScoreText + "]", 8) +
                           Pad(PivotLog.Num(away) + (longs ? " dn" : " up"), 12) +
                           PlainState(zone);

                if (zone.HasMagnet) text += " + magnet";

                // Say so rather than leaving a band listed here and absent from the chart looking
                // like a rendering bug.
                if (!drawn.Contains(zone.Key)) text += "   (off chart)";

                rows.Add(text);

                var failed = state != null && state.State == AbsorptionState.Failed;

                colors.Add(failed ? Conv(FailedZoneColor)
                         : longs ? Conv(LongZoneColor) : Conv(ShortZoneColor));
            }

            if (found) return;

            rows.Add("  nothing within " + PivotLog.Num(LadderRangePts) + " pts of price");
            colors.Add(Color.Gray);
        }

        private static string Pad(string text, int width)
        {
            var t = text ?? string.Empty;
            return t.Length >= width ? t + " " : t.PadRight(width);
        }

        private string Magnets(Analysis a)
        {
            var naked = a.NextNakedPoc != null
                ? "nPOC " + PivotLog.Num(a.NextNakedPoc.Price) +
                  " (naked, " + a.NextNakedPoc.AgeDays + "d)"
                : "no naked POC";

            var poor = a.NextPoorExtreme != null
                ? (a.NextPoorExtreme.IsHigh ? "POOR HIGH " : "POOR LOW ") +
                  PivotLog.Num(a.NextPoorExtreme.Price) + " (unrepaired)"
                : "no unrepaired extreme";

            return naked + "  |  " + poor;
        }

        private void RenderMatchTable(RenderContext context, Rectangle region, Analysis a)
        {
            var rows = new List<string> { "CALLER LEVEL DECODER" };
            var colors = new List<Color> { Color.White };

            foreach (var report in a.Reports)
            {
                var caller = report.Caller;

                if (!caller.Resolved)
                {
                    rows.Add("  " + caller.Raw + "  unresolved: " + (caller.Problem ?? "?"));
                    colors.Add(Conv(ShortZoneColor));
                    continue;
                }

                var best = report.Best;

                if (best == null)
                {
                    rows.Add("  " + PivotLog.Num(caller.Price) + "  no level computed");
                    colors.Add(Color.Gray);
                    continue;
                }

                var zone = ZoneFor(a, caller.Price);

                rows.Add("  " + PivotLog.Num(caller.Price) + "  " + best.Family + " " +
                         best.Level.Name + "  " + (best.Delta >= 0 ? "+" : "-") +
                         PivotLog.Num(Math.Abs(best.Delta)) + "  " + VerdictText(best.Verdict) +
                         (zone != null ? "  zone [" + zone.ScoreText + "]" : "  no zone"));

                colors.Add(best.Verdict == Verdict.Exact ? Conv(LongZoneColor)
                         : best.Verdict == Verdict.Near ? Color.FromArgb(225, 200, 70)
                         : Conv(ShortZoneColor));
            }

            var width = 0;
            foreach (var text in rows)
            {
                var size = context.MeasureString(text, _panelFont);
                if (size.Width > width) width = size.Width;
            }

            var origin = new Point(region.Right - width - 20, region.Top + 8);
            DrawPanel(context, origin, rows, colors.ToArray(), region);
        }

        private void DrawPanel(RenderContext context, Point origin, List<string> rows,
                               Color[] colors, Rectangle region)
        {
            const int padding = 7;

            var width = 0;
            var height = 0;

            foreach (var text in rows)
            {
                var size = context.MeasureString(text, _panelFont);
                if (size.Width > width) width = size.Width;
                height += size.Height;
            }

            var rect = new Rectangle(origin.X, origin.Y, width + padding * 2, height + padding * 2);
            if (rect.Left < region.Left) rect.X = region.Left;

            context.FillRectangle(Color.FromArgb(216, 16, 16, 20), rect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(130, 150, 150, 160), 1), rect);

            var y = rect.Top + padding;

            for (var i = 0; i < rows.Count; i++)
            {
                context.DrawString(rows[i], _panelFont, colors[i], rect.Left + padding, y);
                y += context.MeasureString(rows[i], _panelFont).Height;
            }
        }

        private void RenderWarnings(RenderContext context, Rectangle region, int row)
        {
            var y = row;

            if (!string.IsNullOrEmpty(_sdkWarning))
                Status(context, region, y++, "Pivot Decoder: " + _sdkWarning + " " + Dash +
                       " POC, unfinished-extreme and absorption features are off.",
                       Color.FromArgb(225, 200, 70));

            if (!string.IsNullOrEmpty(_timeframeNote))
                Status(context, region, y++, "Pivot Decoder: " + _timeframeNote,
                       Color.FromArgb(225, 200, 70));

            if (!string.IsNullOrEmpty(_boundaryNote))
                Status(context, region, y++, "Pivot Decoder: " + _boundaryNote,
                       Color.FromArgb(225, 200, 70));

            if (!string.IsNullOrEmpty(_deltaWarning))
                Status(context, region, y++, "Pivot Decoder: " + _deltaWarning,
                       Color.FromArgb(225, 200, 70));

            if (!string.IsNullOrEmpty(_logError))
                Status(context, region, y++, "Pivot Decoder: log not written " + Dash + " " + _logError,
                       Color.FromArgb(225, 200, 70));
        }

        private void Status(RenderContext context, Rectangle region, int row, string text, Color color)
        {
            context.DrawString(text, _statusFont, color, region.Left + 6,
                               region.Bottom - 18 - row * 14);
        }

        private int XOf(int bar)
        {
            var clamped = bar < 0 ? 0 : bar >= CurrentBar ? Math.Max(0, CurrentBar - 1) : bar;
            return ChartInfo.PriceChartContainer.GetXByBar(clamped, true);
        }

        private int Y(decimal price)
        {
            return ChartInfo.PriceChartContainer.GetYByPrice(price, false);
        }

        private static Color Conv(MColor c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        #endregion

        /// <summary>
        /// Adapts the indicator's candle access to <see cref="IBarWindow"/>, with a one-entry
        /// cache because the scan reads several fields off the same bar in a row.
        ///
        /// GetCandle, never SourceDataSeries: that series is null until the platform wires it up
        /// and returns 0 rather than failing, which draws a blank chart with no error anywhere.
        /// </summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly PivotDecoder _owner;
            private int _cachedIndex = -1;
            private IndicatorCandle _cached;

            public BarWindow(PivotDecoder owner) { _owner = owner; }

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
    }
}
