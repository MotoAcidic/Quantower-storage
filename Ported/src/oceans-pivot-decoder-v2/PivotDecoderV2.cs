// ================================================================================================
//  Ocean Pivot Decoder V2  --  PivotDecoderV2 : Indicator
// ================================================================================================
//
//  ASSUMPTIONS (verified against the ATAS 8.0.14.396 install on this machine, not guessed)
//
//  1. TARGET FRAMEWORK is net10.0-windows, NOT net8.0-windows. The installed ATAS assemblies are
//     themselves built for net10.0; a net8.0 project cannot reference them (MSB3274 / CS1705,
//     "built against a higher version"). <UseWPF>true</UseWPF> is required because the
//     settings-panel colour type is System.Windows.Media.Color.
//
//  2. ASSEMBLY LOCATION is the ATAS install ROOT -- "C:\Program Files (x86)\ATAS Platform".
//     There is no "\Assemblies" subfolder in this build. Referenced with <Private>false</Private>:
//     ATAS.Indicators.dll, ATAS.DataFeedsCore.dll, ATAS.Types.dll, OFT.Attributes.dll,
//     OFT.Core.dll, OFT.Localization.dll, OFT.Rendering.dll, Utils.Common.dll.
//
//  3. LOAD PATH is %APPDATA%\ATAS\Indicators, NOT Documents\ATAS\Indicators. A DLL dropped in
//     Documents is never read. ATAS scans that folder ONLY at startup -- restart after deploying.
//
//  4. EVERY ATAS / OFT SIGNATURE USED HERE WAS READ OUT OF THE INSTALLED METADATA. The // VERIFY
//     notes below therefore never mark a doubtful signature -- they mark a doubtful *behaviour*
//     (feed-dependent data, platform conventions) that only a live chart can settle.
//
//  5. TIME. IndicatorCandle.Time / .LastTime are assumed UTC and converted to Houston time with
//     TimeZoneInfo, never a fixed offset. Some ATAS installs hand back already-local stamps, so
//     that is a toggle (Session > Bar time source), not a hardcode. If every level sits an hour
//     off and stays an hour off, that toggle is the cause.
//
//  6. ONE ZONE. Every setting, every calculation and every label in this file is Houston time.
//     There is no ET anywhere by design: a second zone forces a conversion on every read, and
//     "08:30 ET" invites being misread as the 08:30 CT open.
//
//  7. The globex trade date rolls at 17:00 Houston. That single boundary orders the whole day
//     (overnight -> RTH -> post-close) and is what makes the DST handling fall out for free.
//
// ================================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace Ocean.Indicators
{
    /// <summary>Where the platform's bar stamps come from. Auto-detection is deliberately absent:
    /// a wrong guess silently shifts every session boundary, so this is stated, not inferred.</summary>
    public enum BarClockSource
    {
        /// <summary>Bar stamps are UTC and get converted to Houston time. The normal case.</summary>
        Utc = 0,

        /// <summary>Bar stamps already arrive in exchange local time; no conversion applied.</summary>
        ExchangeLocal = 1
    }

    /// <summary>
    /// Pivot Decoder V2 -- the institutional reference levels price actually reacts to, each
    /// labelled with its full price AND its last three digits so a caller's "@605" resolves at a
    /// glance; coloured by which side of price it sits on; merged into a shaded band where two or
    /// more stack; and marked where a sweep-and-reclaim actually happened rather than where one
    /// was merely possible.
    ///
    /// The design rule throughout: the expensive work happens once per CLOSED bar and is cached.
    /// OnCalculate folds bars forward and never rescans history; OnRender only draws. Nothing that
    /// has been confirmed on a closed bar is ever recomputed, so markers do not repaint.
    /// </summary>
    [DisplayName("Ocean Pivot Decoder V2")]
    [Category("Ocean")]
    public class PivotDecoderV2 : Indicator
    {
        #region Inputs

        // ---------------------------------------------------------------------------------------
        // 01 Session -- every time here is Houston time.
        // ---------------------------------------------------------------------------------------

        private string _timeZoneId = "Central Standard Time";
        private BarClockSource _barClock = BarClockSource.Utc;
        private int _rthStartHour = 8, _rthStartMinute = 30;
        private int _rthEndHour = 15, _rthEndMinute = 0;
        private int _globexRollHour = 17;
        private int _ibMinutes = 60;

        [Display(Name = "Time zone", GroupName = "01 Session", Order = 100,
            Description = "Houston is Central Standard Time. TimeZoneInfo applies DST itself; " +
                          "never replace this with a fixed offset or the boundaries drift twice a year.")]
        public string TimeZoneId
        {
            get { return _timeZoneId; }
            set { SetCompute(ref _timeZoneId, value); }
        }

        [Display(Name = "Bar time source", GroupName = "01 Session", Order = 105,
            Description = "UTC is the normal case. Switch only if every level sits a constant " +
                          "number of hours off.")]
        public BarClockSource BarClock
        {
            get { return _barClock; }
            set { SetCompute(ref _barClock, value); }
        }

        [Display(Name = "RTH start hour (Houston)", GroupName = "01 Session", Order = 110)]
        [Range(0, 23)]
        public int RthStartHour { get { return _rthStartHour; } set { SetCompute(ref _rthStartHour, value); } }

        [Display(Name = "RTH start minute", GroupName = "01 Session", Order = 111)]
        [Range(0, 59)]
        public int RthStartMinute { get { return _rthStartMinute; } set { SetCompute(ref _rthStartMinute, value); } }

        [Display(Name = "RTH end hour (Houston)", GroupName = "01 Session", Order = 112)]
        [Range(0, 23)]
        public int RthEndHour { get { return _rthEndHour; } set { SetCompute(ref _rthEndHour, value); } }

        [Display(Name = "RTH end minute", GroupName = "01 Session", Order = 113)]
        [Range(0, 59)]
        public int RthEndMinute { get { return _rthEndMinute; } set { SetCompute(ref _rthEndMinute, value); } }

        [Display(Name = "Globex roll hour (Houston)", GroupName = "01 Session", Order = 120,
            Description = "17 = the 5 PM Sunday-through-Thursday reopen. This is the trade-date " +
                          "boundary: the overnight window runs from here to the RTH open.")]
        [Range(0, 23)]
        public int GlobexRollHour { get { return _globexRollHour; } set { SetCompute(ref _globexRollHour, value); } }

        [Display(Name = "Initial balance minutes", GroupName = "01 Session", Order = 130,
            Description = "Measured from the RTH open. 60 is the convention.")]
        [Range(1, 720)]
        public int IbMinutes { get { return _ibMinutes; } set { SetCompute(ref _ibMinutes, value); } }

        // ---------------------------------------------------------------------------------------
        // 02 Levels -- one toggle each, so the chart carries only what is being traded.
        // ---------------------------------------------------------------------------------------

        private bool _showPriorRth = true, _showEth = true, _showOvernight = true;
        private bool _showPriorClose = true, _showOpen = true, _showIb = true;
        private bool _showPriorPoc = false, _showWeekly = false;

        [Display(Name = "Prior RTH high / low", GroupName = "02 Levels", Order = 200)]
        public bool ShowPriorRth { get { return _showPriorRth; } set { SetCompute(ref _showPriorRth, value); } }

        [Display(Name = "Prior ETH (full day) high / low", GroupName = "02 Levels", Order = 205)]
        public bool ShowEth { get { return _showEth; } set { SetCompute(ref _showEth, value); } }

        [Display(Name = "Overnight high / low", GroupName = "02 Levels", Order = 210,
            Description = "Globex roll through the RTH open -- Asia plus London.")]
        public bool ShowOvernight { get { return _showOvernight; } set { SetCompute(ref _showOvernight, value); } }

        [Display(Name = "Prior RTH close", GroupName = "02 Levels", Order = 215)]
        public bool ShowPriorClose { get { return _showPriorClose; } set { SetCompute(ref _showPriorClose, value); } }

        [Display(Name = "Session open", GroupName = "02 Levels", Order = 220,
            Description = "Today's RTH open once it exists; before the RTH open, the globex open, " +
                          "labelled gOpen so the two are never confused.")]
        public bool ShowOpen { get { return _showOpen; } set { SetCompute(ref _showOpen, value); } }

        [Display(Name = "Initial balance high / low", GroupName = "02 Levels", Order = 225)]
        public bool ShowIb { get { return _showIb; } set { SetCompute(ref _showIb, value); } }

        [Display(Name = "Prior RTH POC", GroupName = "02 Levels", Order = 230,
            Description = "Off by default: it walks every price level of every RTH bar. Cheap " +
                          "enough once per closed bar, but only worth paying for if it is read.")]
        public bool ShowPriorPoc { get { return _showPriorPoc; } set { SetCompute(ref _showPriorPoc, value); } }

        [Display(Name = "Weekly open / prior-week high / low", GroupName = "02 Levels", Order = 235)]
        public bool ShowWeekly { get { return _showWeekly; } set { SetCompute(ref _showWeekly, value); } }

        private bool _showRound = true;
        private decimal _roundStepA = 25m, _roundStepB = 100m;
        private decimal _roundRangePts = 150m;

        [Display(Name = "Round-number magnets", GroupName = "02 Levels", Order = 240)]
        public bool ShowRound { get { return _showRound; } set { SetCompute(ref _showRound, value); } }

        [Display(Name = "Round step A (points)", GroupName = "02 Levels", Order = 241,
            Description = "25 on NQ. Set 0 to disable this step.")]
        public decimal RoundStepA { get { return _roundStepA; } set { SetCompute(ref _roundStepA, value); } }

        [Display(Name = "Round step B (points)", GroupName = "02 Levels", Order = 242,
            Description = "100 on NQ. A price that is both counts once, weighted to step B.")]
        public decimal RoundStepB { get { return _roundStepB; } set { SetCompute(ref _roundStepB, value); } }

        [Display(Name = "Round magnet range (+/- points)", GroupName = "02 Levels", Order = 243)]
        public decimal RoundRangePts { get { return _roundRangePts; } set { SetCompute(ref _roundRangePts, value); } }

        // ---------------------------------------------------------------------------------------
        // 03 Fair value gaps
        // ---------------------------------------------------------------------------------------

        private bool _showFvg = true;
        private int _fvgMinTicks = 8;
        private int _fvgMaxKept = 10;
        private bool _fvgShowMid = true;
        private bool _fvgTrackReactions = true;

        [Display(Name = "Fair value gaps", GroupName = "03 FVG", Order = 300,
            Description = "Three-candle imbalance on the chart timeframe. Only unfilled gaps are kept.")]
        public bool ShowFvg { get { return _showFvg; } set { SetCompute(ref _showFvg, value); } }

        [Display(Name = "Minimum gap size (ticks)", GroupName = "03 FVG", Order = 305,
            Description = "Below this the gap is noise. 8 ticks = 2 NQ points. NEEDS TUNING.")]
        [Range(1, 1000)]
        public int FvgMinTicks { get { return _fvgMinTicks; } set { SetCompute(ref _fvgMinTicks, value); } }

        [Display(Name = "Maximum unfilled gaps kept", GroupName = "03 FVG", Order = 310)]
        [Range(1, 100)]
        public int FvgMaxKept { get { return _fvgMaxKept; } set { SetCompute(ref _fvgMaxKept, value); } }

        [Display(Name = "Draw consequent encroachment (mid)", GroupName = "03 FVG", Order = 315)]
        public bool FvgShowMid { get { return _fvgShowMid; } set { SetCompute(ref _fvgShowMid, value); } }

        [Display(Name = "Run reaction detector on gaps", GroupName = "03 FVG", Order = 320,
            Description = "Tracks top, bottom and CE of every unfilled gap alongside the session levels.")]
        public bool FvgTrackReactions { get { return _fvgTrackReactions; } set { SetCompute(ref _fvgTrackReactions, value); } }

        // ---------------------------------------------------------------------------------------
        // 04 Reaction. The defaults are sane NQ starting points and NOTHING MORE -- they need
        // tuning against your own tape before any of them is treated as a threshold.
        // ---------------------------------------------------------------------------------------

        private int _proximityTicks = 8;
        private int _maxPenetrationTicks = 12;
        private int _reclaimBars = 3;
        private bool _requireCloseInside = true;
        private bool _useDeltaFilter = false;

        [Display(Name = "Proximity (ticks)", GroupName = "04 Reaction", Order = 400,
            Description = "How close price must trade before a level is armed. 8 ticks = 2 NQ points. TUNE.")]
        [Range(1, 1000)]
        public int ProximityTicks { get { return _proximityTicks; } set { SetCompute(ref _proximityTicks, value); } }

        [Display(Name = "Max penetration (ticks)", GroupName = "04 Reaction", Order = 405,
            Description = "How far price may sweep THROUGH the level and still count as a liquidity " +
                          "grab. Beyond this the level broke, it was not defended. 12 ticks = 3 NQ points. TUNE.")]
        [Range(1, 1000)]
        public int MaxPenetrationTicks { get { return _maxPenetrationTicks; } set { SetCompute(ref _maxPenetrationTicks, value); } }

        [Display(Name = "Reclaim window (bars)", GroupName = "04 Reaction", Order = 410,
            Description = "Bars allowed between the sweep and the close back on the origin side. TUNE.")]
        [Range(1, 100)]
        public int ReclaimBars { get { return _reclaimBars; } set { SetCompute(ref _reclaimBars, value); } }

        [Display(Name = "Require decisive reclaim", GroupName = "04 Reaction", Order = 415,
            Description = "On: the reclaiming bar must close back past the level by at least the " +
                          "proximity distance. Off: any close back on the origin side counts.")]
        public bool RequireCloseInside { get { return _requireCloseInside; } set { SetCompute(ref _requireCloseInside, value); } }

        [Display(Name = "Delta filter", GroupName = "04 Reaction", Order = 420,
            Description = "Confirm only when the sweep absorbed against itself: a sweep DOWN needs " +
                          "non-negative delta for a long, a sweep UP non-positive for a short. " +
                          "Needs a feed that actually carries delta.")]
        public bool UseDeltaFilter { get { return _useDeltaFilter; } set { SetCompute(ref _useDeltaFilter, value); } }

        [Display(Name = "Alerts", GroupName = "04 Reaction", Order = 425,
            Description = "Fires only for reactions confirmed on the most recent bars, never on history load.")]
        public bool AlertsOn { get; set; }

        [Display(Name = "Alert text", GroupName = "04 Reaction", Order = 430)]
        public string AlertText
        {
            get { return _alertText; }
            set { _alertText = string.IsNullOrEmpty(value) ? "Pivot Decoder V2 reaction" : value; }
        }
        private string _alertText = "Pivot Decoder V2 reaction";

        // ---------------------------------------------------------------------------------------
        // 05 Confluence
        // ---------------------------------------------------------------------------------------

        private int _zoneMergeTicks = 12;

        [Display(Name = "Merge distance (ticks)", GroupName = "05 Confluence", Order = 500,
            Description = "Levels within this of each other become one band. 12 ticks = 3 NQ points.")]
        [Range(0, 1000)]
        public int ZoneMergeTicks { get { return _zoneMergeTicks; } set { SetCompute(ref _zoneMergeTicks, value); } }

        [Display(Name = "Zone opacity floor (0-255)", GroupName = "05 Confluence", Order = 505,
            Description = "Two stacked levels. Opacity climbs from here as more stack.")]
        [Range(0, 255)]
        public int ZoneMinAlpha { get; set; } = 38;

        [Display(Name = "Zone opacity ceiling (0-255)", GroupName = "05 Confluence", Order = 510)]
        [Range(0, 255)]
        public int ZoneMaxAlpha { get; set; } = 110;

        [Display(Name = "Weight for full opacity", GroupName = "05 Confluence", Order = 515,
            Description = "Weight, not count: a prior-week extreme carries more than an IB edge.")]
        [Range(2, 40)]
        public int ZoneFullWeight { get; set; } = 6;

        // ---------------------------------------------------------------------------------------
        // 06 Labels
        // ---------------------------------------------------------------------------------------

        [Display(Name = "Show labels", GroupName = "06 Labels", Order = 600)]
        public bool ShowLabels { get; set; } = true;

        [Display(Name = "Show full price", GroupName = "06 Labels", Order = 605,
            Description = "PDH 605 (29,605.50). Turn off and only the caller's shorthand remains.")]
        public bool ShowFullPrice { get; set; } = true;

        [Display(Name = "Three-digit code only", GroupName = "06 Labels", Order = 610,
            Description = "PDH 605. Overrides 'show full price'. This is the caller's format.")]
        public bool ShowOnly3Digit { get; set; }

        [Display(Name = "Label font", GroupName = "06 Labels", Order = 615)]
        public string LabelFontFamily
        {
            get { return _labelFontFamily; }
            set { _labelFontFamily = string.IsNullOrEmpty(value) ? "Consolas" : value; _font = null; }
        }
        private string _labelFontFamily = "Consolas";

        [Display(Name = "Label size", GroupName = "06 Labels", Order = 620)]
        [Range(5, 40)]
        public float LabelFontSize
        {
            get { return _labelFontSize; }
            set { _labelFontSize = value < 5f ? 5f : value > 40f ? 40f : value; _font = null; }
        }
        private float _labelFontSize = 10f;

        [Display(Name = "Right margin (px)", GroupName = "06 Labels", Order = 625,
            Description = "Distance from the right edge of the price region to the tag.")]
        [Range(0, 400)]
        public int RightMargin { get; set; } = 10;

        [Display(Name = "Label backdrop", GroupName = "06 Labels", Order = 630,
            Description = "A dark plate behind the tag so it stays readable over candles.")]
        public bool LabelBackdrop { get; set; } = true;

        // ---------------------------------------------------------------------------------------
        // 07 Style
        // ---------------------------------------------------------------------------------------

        [Display(Name = "Resistance (above price)", GroupName = "07 Style", Order = 700)]
        public MColor ResistanceColor { get; set; } = MColor.FromRgb(225, 70, 70);

        [Display(Name = "Support (below price)", GroupName = "07 Style", Order = 705)]
        public MColor SupportColor { get; set; } = MColor.FromRgb(40, 190, 110);

        [Display(Name = "Neutral (price inside)", GroupName = "07 Style", Order = 710)]
        public MColor NeutralColor { get; set; } = MColor.FromRgb(190, 190, 200);

        [Display(Name = "Session level width", GroupName = "07 Style", Order = 720)]
        [Range(1, 6)]
        public int SessionWidth { get; set; } = 2;

        [Display(Name = "Session level dash", GroupName = "07 Style", Order = 721)]
        public DashStyle SessionDash { get; set; } = DashStyle.Solid;

        [Display(Name = "Round magnet width", GroupName = "07 Style", Order = 730)]
        [Range(1, 6)]
        public int RoundWidth { get; set; } = 1;

        [Display(Name = "Round magnet dash", GroupName = "07 Style", Order = 731)]
        public DashStyle RoundDash { get; set; } = DashStyle.Dot;

        [Display(Name = "FVG edge width", GroupName = "07 Style", Order = 740)]
        [Range(1, 6)]
        public int FvgWidth { get; set; } = 1;

        [Display(Name = "FVG edge dash", GroupName = "07 Style", Order = 741)]
        public DashStyle FvgDash { get; set; } = DashStyle.Dash;

        [Display(Name = "FVG body fill", GroupName = "07 Style", Order = 745,
            Description = "Shade the whole imbalance, not just its edges.")]
        public bool FvgFillBody { get; set; } = true;

        [Display(Name = "FVG fill opacity (0-255)", GroupName = "07 Style", Order = 746)]
        [Range(0, 255)]
        public int FvgFillAlpha { get; set; } = 26;

        [Display(Name = "Reaction markers", GroupName = "07 Style", Order = 750)]
        public bool ShowReactionMarkers { get; set; } = true;

        [Display(Name = "Marker size (px)", GroupName = "07 Style", Order = 755)]
        [Range(3, 30)]
        public int MarkerSize { get; set; } = 7;

        #endregion

        #region Fields / State

        // Everything below is derived state, written only by the fold in OnCalculate. OnRender
        // reads it and never writes it, which is what makes a redraw incapable of changing what
        // the indicator claims.

        private bool _initialized;
        private TimeZoneInfo _zone;
        private string _zoneError;
        private decimal _tick = 0.25m;
        private string _priceFormat = "N2";

        /// <summary>Index of the last CLOSED bar already folded in. The whole no-repaint guarantee
        /// rests on this only ever moving forward.</summary>
        private int _processed = -1;

        private decimal _lastClose;

        private readonly List<DayStats> _days = new List<DayStats>();
        private readonly List<Fvg> _fvgs = new List<Fvg>();
        private readonly List<Reaction> _reactions = new List<Reaction>();
        private readonly HashSet<string> _alerted = new HashSet<string>();

        /// <summary>Reaction state keyed by SNAPPED PRICE, not by level object. Levels are rebuilt
        /// at every session start and gaps come and go; a price does not. This is what lets one
        /// state machine serve session levels, gaps and round magnets alike.</summary>
        private readonly Dictionary<decimal, Rx> _rx = new Dictionary<decimal, Rx>();

        /// <summary>The session-fixed levels for the current trade date.</summary>
        private readonly List<Lvl> _levels = new List<Lvl>();
        private bool _levelsDirty;

        // Render-side scratch, reused every frame.
        private readonly List<Lvl> _draw = new List<Lvl>();
        private readonly List<Zone> _zones = new List<Zone>();
        private readonly List<int> _labelYs = new List<int>();
        private readonly List<decimal> _stale = new List<decimal>();
        private readonly Point[] _tri = new Point[3];
        private static readonly Comparison<Lvl> ByPrice = delegate (Lvl a, Lvl b) { return a.Price.CompareTo(b.Price); };

        private RenderFont _font;

        /// <summary>Warm-up or data problems worth saying out loud rather than drawing nothing.</summary>
        private string _notice;

        /// <summary>Render-layer failures printed so far this frame, so a second one stacks under
        /// the first instead of overwriting it.</summary>
        private int _failures;

        private enum Group { Session, Round, Fvg }

        /// <summary>One candidate level.</summary>
        private sealed class Lvl
        {
            public string Tag;
            public decimal Price;
            public Group Group;
            public int Weight;          // confluence contribution; a weekly extreme outweighs an IB edge
            public Fvg Gap;             // set only on the three gap lines, so the body can be shaded
        }

        private sealed class Fvg
        {
            public int Bar;
            public bool Bullish;
            public decimal Top;
            public decimal Bottom;
            public decimal Mid;
            public bool Filled;
        }

        private sealed class Reaction
        {
            public int Bar;
            public decimal Price;
            public int Dir;             // +1 swept the low and reclaimed = long, -1 = short
            public string Tag;
        }

        private sealed class Rx
        {
            public int Phase;           // 0 idle, 1 armed, 2 swept
            public int Origin;          // +1 price approached from above, -1 from below
            public int ArmBar;
            public int SweepBar;
            public decimal Extreme;     // deepest penetration reached so far
            public decimal SweepDelta;  // delta accumulated across the sweep bars
            public int Touch;           // bar of the most recent touch, for pruning

            public void Reset() { Phase = 0; Origin = 0; SweepDelta = 0m; }
        }

        /// <summary>One globex trade date, folded forward bar by bar and never revisited.</summary>
        private sealed class DayStats
        {
            public DateTime TradeDate;      // the Houston calendar date the globex day belongs to
            public int FirstBar = -1;

            public decimal EthHigh, EthLow;
            public bool HasEth;

            public decimal OnHigh, OnLow;
            public bool HasOn;

            public decimal RthHigh, RthLow, RthOpen, RthClose;
            public bool HasRth;

            public decimal IbHigh, IbLow;
            public bool HasIb;

            public decimal GlobexOpen;

            public Dictionary<decimal, decimal> RthVol;   // allocated only when POC is switched on
            public decimal Poc;
            public bool HasPoc;
        }

        private sealed class Zone
        {
            public decimal Low, High;
            public int Weight;
            public int Count;
            public string Tags;
        }

        #endregion

        #region Lifecycle

        public PivotDecoderV2()
            : base(true)   // useCandles: this reads OHLC and delta, not a single series value
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = false;

            // The base Indicator always exposes one data series. Nothing is plotted through it --
            // horizontal levels must span the visible region, which a bar-indexed series cannot do
            // -- so it is hidden rather than left drawing a stray zero line.
            if (DataSeries.Count > 0)
            {
                var series = DataSeries[0] as ValueDataSeries;
                if (series != null)
                {
                    series.VisualType = VisualMode.Hide;
                    series.IsHidden = true;
                    series.ShowZeroValue = false;
                }
            }

            _initialized = true;
        }

        /// <summary>
        /// Folds newly closed bars forward. This runs on every tick of the forming bar, so the only
        /// unconditional work here is reading one close -- everything else sits behind the
        /// _processed watermark and happens once per closed bar, ever.
        /// </summary>
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
                ResetState();

            if (_zone == null)
                ResolveZone();

            RefreshInstrument();

            // CurrentBar is the bar COUNT, so the forming bar is CurrentBar - 1 and the last bar
            // that can never change again is CurrentBar - 2.
            var lastClosed = CurrentBar - 2;

            while (_processed < lastClosed)
            {
                _processed++;
                try
                {
                    FoldBar(_processed);
                }
                catch (Exception ex)
                {
                    _notice = "Fold failed at bar " + _processed.ToString(CultureInfo.InvariantCulture) +
                              ": " + ex.Message;
                    break;
                }
            }

            // The forming bar's close drives the support/resistance split and the round magnets.
            // One property read per tick; no level maths is touched.
            var candle = GetCandle(bar);
            if (candle != null)
                _lastClose = candle.Close;
        }

        protected override void OnRecalculate()
        {
            ResetState();
        }

        private void ResetState()
        {
            _processed = -1;
            _days.Clear();
            _fvgs.Clear();
            _reactions.Clear();
            _alerted.Clear();
            _rx.Clear();
            _levels.Clear();
            _levelsDirty = false;
            _lastClose = 0m;
            _notice = null;
        }

        /// <summary>Compute-affecting inputs go through here: change one and the platform replays
        /// history, so the cached fold is rebuilt from scratch rather than patched.</summary>
        private void SetCompute<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;

            if (!_initialized)
                return;

            _zone = null;
            RecalculateValues();
        }

        private void ResolveZone()
        {
            try
            {
                _zone = TimeZoneInfo.FindSystemTimeZoneById(
                    string.IsNullOrEmpty(_timeZoneId) ? "Central Standard Time" : _timeZoneId);
                _zoneError = null;
            }
            catch (Exception ex)
            {
                _zone = TimeZoneInfo.Utc;
                _zoneError = "Time zone '" + _timeZoneId + "' not found (" + ex.Message +
                             "). Sessions are being cut on UTC and every level is WRONG.";
            }
        }

        private void RefreshInstrument()
        {
            // No fallback to a hardcoded tick and no fallback to the obsolete Indicator.TickSize.
            // Until the platform supplies the instrument, the bootstrap value stands and nothing
            // is snapped to a size that might be wrong for the symbol on the chart.
            if (InstrumentInfo == null)
                return;

            var t = InstrumentInfo.TickSize;
            if (t <= 0m || t == _tick)
                return;

            _tick = t;

            // Decimals come from the tick, never from a hardcoded instrument assumption.
            var d = 0;
            var probe = t;
            while (probe != Math.Truncate(probe) && d < 10)
            {
                probe *= 10m;
                d++;
            }
            _priceFormat = "N" + d.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region Session / Level computation

        private static int MinuteOfDay(DateTime local)
        {
            return local.Hour * 60 + local.Minute;
        }

        /// <summary>
        /// Minutes since the globex roll. This is the single ordering key for a trading day:
        /// overnight occupies [0, rthStart), RTH [rthStart, rthEnd), post-close the remainder.
        /// Because it is derived from the DST-converted local time, the boundaries move with the
        /// clock instead of drifting an hour twice a year.
        /// </summary>
        private int SessionMinute(DateTime local)
        {
            var roll = _globexRollHour * 60;
            return ((MinuteOfDay(local) - roll) + 1440) % 1440;
        }

        private int RthStartSm
        {
            get { return ((_rthStartHour * 60 + _rthStartMinute) - _globexRollHour * 60 + 1440) % 1440; }
        }

        private int RthEndSm
        {
            get { return ((_rthEndHour * 60 + _rthEndMinute) - _globexRollHour * 60 + 1440) % 1440; }
        }

        /// <summary>The globex trade date a bar belongs to: after the roll, the day has already
        /// become tomorrow. Sunday-evening bars therefore carry Monday's date, which is what makes
        /// the weekly grouping fall out with no special case.</summary>
        private DateTime TradeDate(DateTime local)
        {
            return local.Hour >= _globexRollHour ? local.Date.AddDays(1) : local.Date;
        }

        private DateTime ToExchange(DateTime stamp)
        {
            if (_barClock == BarClockSource.ExchangeLocal)
                return stamp;

            var utc = stamp.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(stamp, DateTimeKind.Utc)
                : stamp.ToUniversalTime();

            return TimeZoneInfo.ConvertTimeFromUtc(utc, _zone);
        }

        /// <summary>
        /// Everything that happens once per closed bar: day aggregates, imbalance detection, and
        /// one step of the reaction state machine. Called strictly in bar order, exactly once per
        /// bar. No LINQ, and no allocation beyond what a genuinely new day or gap needs.
        /// </summary>
        private void FoldBar(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

            var local = ToExchange(c.Time);
            var td = TradeDate(local);
            var sm = SessionMinute(local);

            var rthStart = RthStartSm;
            var rthEnd = RthEndSm;

            // A malformed window would silently mislabel every level, so it is refused, not coerced.
            if (rthEnd <= rthStart)
            {
                _notice = "RTH window is empty or wraps the globex roll. Check the session times.";
                return;
            }

            var day = _days.Count > 0 ? _days[_days.Count - 1] : null;

            if (day == null || day.TradeDate != td)
            {
                if (day != null)
                    FinalizeDay(day);

                day = new DayStats();
                day.TradeDate = td;
                day.FirstBar = bar;
                day.GlobexOpen = c.Open;
                if (_showPriorPoc)
                    day.RthVol = new Dictionary<decimal, decimal>();
                _days.Add(day);

                _levelsDirty = true;
            }

            // --- ETH: the whole globex day, overnight included --------------------------------
            if (!day.HasEth)
            {
                day.EthHigh = c.High; day.EthLow = c.Low; day.HasEth = true;
            }
            else
            {
                if (c.High > day.EthHigh) day.EthHigh = c.High;
                if (c.Low < day.EthLow) day.EthLow = c.Low;
            }

            // --- Overnight: the roll through the RTH open --------------------------------------
            if (sm < rthStart)
            {
                if (!day.HasOn)
                {
                    day.OnHigh = c.High; day.OnLow = c.Low; day.HasOn = true; _levelsDirty = true;
                }
                else
                {
                    if (c.High > day.OnHigh) { day.OnHigh = c.High; _levelsDirty = true; }
                    if (c.Low < day.OnLow) { day.OnLow = c.Low; _levelsDirty = true; }
                }
            }

            // --- RTH, and the initial balance inside it ----------------------------------------
            var inRth = sm >= rthStart && sm < rthEnd;
            if (inRth)
            {
                if (!day.HasRth)
                {
                    day.RthHigh = c.High; day.RthLow = c.Low; day.RthOpen = c.Open; day.HasRth = true;
                    _levelsDirty = true;   // the session open exists now, and the tag changes with it
                }
                else
                {
                    if (c.High > day.RthHigh) day.RthHigh = c.High;
                    if (c.Low < day.RthLow) day.RthLow = c.Low;
                }

                day.RthClose = c.Close;   // the last RTH bar to close wins, which is the definition

                if (sm < rthStart + _ibMinutes)
                {
                    if (!day.HasIb)
                    {
                        day.IbHigh = c.High; day.IbLow = c.Low; day.HasIb = true; _levelsDirty = true;
                    }
                    else
                    {
                        if (c.High > day.IbHigh) { day.IbHigh = c.High; _levelsDirty = true; }
                        if (c.Low < day.IbLow) { day.IbLow = c.Low; _levelsDirty = true; }
                    }
                }

                if (day.RthVol != null)
                    AccumulateProfile(day, c);
            }

            // Cached: rebuilt only when something a level is made of actually moved, which over a
            // session is a handful of bars, not every bar.
            if (_levelsDirty)
            {
                BuildLevels();
                _levelsDirty = false;
            }

            // --- Imbalances --------------------------------------------------------------------
            if (_showFvg)
            {
                DetectFvg(bar, c);
                UpdateFvgFills(bar, c);
            }

            // --- Reaction machine --------------------------------------------------------------
            StepReactions(bar, c);
        }

        /// <summary>
        /// Volume by price for the session POC. Walks the candle's own price ladder rather than
        /// re-deriving one, and only when the toggle is on.
        /// // VERIFY: GetAllPriceLevels() returns nothing on feeds that do not carry per-price
        /// // volume for historical bars. If pPOC never appears, that is the reason, not this code.
        /// </summary>
        private static void AccumulateProfile(DayStats day, IndicatorCandle c)
        {
            var levels = c.GetAllPriceLevels();
            if (levels == null)
                return;

            foreach (var pv in levels)
            {
                if (pv == null)
                    continue;

                decimal have;
                day.RthVol.TryGetValue(pv.Price, out have);
                day.RthVol[pv.Price] = have + pv.Volume;
            }
        }

        private static void FinalizeDay(DayStats day)
        {
            if (day.RthVol == null || day.RthVol.Count == 0)
                return;

            var best = 0m;
            var bestPrice = 0m;
            foreach (var kv in day.RthVol)
            {
                if (kv.Value <= best)
                    continue;
                best = kv.Value;
                bestPrice = kv.Key;
            }

            if (best > 0m)
            {
                day.Poc = bestPrice;
                day.HasPoc = true;
            }

            // The ladder has served its purpose. Releasing it keeps a long history from carrying
            // one dictionary per day for the life of the chart.
            day.RthVol = null;
        }

        /// <summary>
        /// Rebuilds the session-fixed level set for the current trade date. Called at the session
        /// boundary and whenever a today-derived extreme moves -- never per tick, never per render.
        /// </summary>
        private void BuildLevels()
        {
            _levels.Clear();

            var idx = _days.Count - 1;
            if (idx < 0)
                return;

            var today = _days[idx];
            var prior = FindPriorRthDay(idx);

            if (prior != null)
            {
                if (_showPriorRth && prior.HasRth)
                {
                    Add("PDH", prior.RthHigh, Group.Session, 3);
                    Add("PDL", prior.RthLow, Group.Session, 3);
                }

                if (_showEth && prior.HasEth)
                {
                    Add("ETH-H", prior.EthHigh, Group.Session, 2);
                    Add("ETH-L", prior.EthLow, Group.Session, 2);
                }

                if (_showPriorClose && prior.HasRth)
                    Add("pRTHc", prior.RthClose, Group.Session, 2);

                if (_showPriorPoc && prior.HasPoc)
                    Add("pPOC", prior.Poc, Group.Session, 3);
            }

            if (_showOvernight && today.HasOn)
            {
                Add("ON-H", today.OnHigh, Group.Session, 2);
                Add("ON-L", today.OnLow, Group.Session, 2);
            }

            if (_showIb && today.HasIb)
            {
                Add("IBH", today.IbHigh, Group.Session, 1);
                Add("IBL", today.IbLow, Group.Session, 1);
            }

            if (_showOpen)
            {
                // Before the RTH open there is no RTH open. Rather than plot nothing -- or, worse,
                // plot the globex open under an "Open" tag -- the tag itself changes.
                if (today.HasRth) Add("Open", today.RthOpen, Group.Session, 2);
                else Add("gOpen", today.GlobexOpen, Group.Session, 1);
            }

            if (_showWeekly)
                AddWeeklyLevels(today.TradeDate);
        }

        /// <summary>The most recent completed day that actually had an RTH session, so a holiday or
        /// a Sunday globex stub never becomes "yesterday".</summary>
        private DayStats FindPriorRthDay(int fromIndex)
        {
            var floor = fromIndex - 10;
            for (var i = fromIndex - 1; i >= 0 && i >= floor; i--)
            {
                if (_days[i].HasRth)
                    return _days[i];
            }
            return null;
        }

        /// <summary>
        /// Weekly open and prior-week high/low, derived from the day list. The globex week begins
        /// with the Sunday 17:00 reopen, which the trade date already encodes.
        /// </summary>
        private void AddWeeklyLevels(DateTime tradeDate)
        {
            var thisWeek = WeekStart(tradeDate);
            var prevWeek = thisWeek.AddDays(-7);

            var haveOpen = false;
            var open = 0m;
            var haveP = false;
            var ph = 0m;
            var pl = 0m;

            for (var i = 0; i < _days.Count; i++)
            {
                var d = _days[i];
                var ws = WeekStart(d.TradeDate);

                if (ws == thisWeek)
                {
                    if (!haveOpen)
                    {
                        open = d.GlobexOpen;
                        haveOpen = true;
                    }
                }
                else if (ws == prevWeek && d.HasEth)
                {
                    if (!haveP) { ph = d.EthHigh; pl = d.EthLow; haveP = true; }
                    else
                    {
                        if (d.EthHigh > ph) ph = d.EthHigh;
                        if (d.EthLow < pl) pl = d.EthLow;
                    }
                }
            }

            if (haveOpen)
                Add("wOpen", open, Group.Session, 2);

            if (haveP)
            {
                Add("PWH", ph, Group.Session, 4);
                Add("PWL", pl, Group.Session, 4);
            }
        }

        private static DateTime WeekStart(DateTime date)
        {
            var dow = (int)date.DayOfWeek;          // Sunday = 0
            var back = dow == 0 ? 6 : dow - 1;      // Monday-based
            return date.Date.AddDays(-back);
        }

        private void Add(string tag, decimal price, Group group, int weight)
        {
            if (price <= 0m)
                return;

            var lvl = new Lvl();
            lvl.Tag = tag;
            lvl.Price = Snap(price);
            lvl.Group = group;
            lvl.Weight = weight;
            _levels.Add(lvl);
        }

        #endregion

        #region FVG

        /// <summary>
        /// Three-candle imbalance on the chart timeframe:
        ///   bullish   Low[i]  &gt; High[i-2]   -&gt; the untraded band High[i-2] .. Low[i]
        ///   bearish   High[i] &lt; Low[i-2]    -&gt; the untraded band High[i]   .. Low[i-2]
        /// Detected only on closed bars, so a gap never appears and then vanishes intrabar.
        /// </summary>
        private void DetectFvg(int bar, IndicatorCandle c)
        {
            if (bar < 2)
                return;

            var back = GetCandle(bar - 2);
            if (back == null)
                return;

            var minSize = _fvgMinTicks * _tick;

            if (c.Low > back.High && c.Low - back.High >= minSize)
                AddFvg(bar, true, c.Low, back.High);
            else if (c.High < back.Low && back.Low - c.High >= minSize)
                AddFvg(bar, false, back.Low, c.High);
        }

        private void AddFvg(int bar, bool bullish, decimal top, decimal bottom)
        {
            var g = new Fvg();
            g.Bar = bar;
            g.Bullish = bullish;
            g.Top = Snap(top);
            g.Bottom = Snap(bottom);
            g.Mid = Snap((g.Top + g.Bottom) / 2m);   // consequent encroachment
            _fvgs.Add(g);
        }

        /// <summary>
        /// A gap is filled when price trades fully THROUGH it, not when it is merely tagged. Filled
        /// gaps are dropped rather than greyed out: the point of the list is what is still open.
        /// The list is then trimmed to the newest N so a long history cannot accumulate hundreds.
        /// </summary>
        private void UpdateFvgFills(int bar, IndicatorCandle c)
        {
            var write = 0;
            for (var i = 0; i < _fvgs.Count; i++)
            {
                var g = _fvgs[i];

                if (g.Bar < bar && !g.Filled)
                {
                    if (g.Bullish) { if (c.Low <= g.Bottom) g.Filled = true; }
                    else { if (c.High >= g.Top) g.Filled = true; }
                }

                if (g.Filled)
                {
                    _rx.Remove(g.Top);
                    _rx.Remove(g.Bottom);
                    _rx.Remove(g.Mid);
                    continue;
                }

                _fvgs[write] = g;
                write++;
            }

            if (write < _fvgs.Count)
                _fvgs.RemoveRange(write, _fvgs.Count - write);

            // The list is in bar order by construction, so the oldest are at the front.
            var excess = _fvgs.Count - _fvgMaxKept;
            if (excess > 0)
                _fvgs.RemoveRange(0, excess);
        }

        #endregion

        #region Reaction state machine

        /// <summary>
        /// One step, on one closed bar, across every tracked price: the session levels, the three
        /// lines of each unfilled gap, and the round magnets near price. Roughly fifty comparisons
        /// per closed bar, run exactly once per bar for the life of the chart.
        /// </summary>
        private void StepReactions(int bar, IndicatorCandle c)
        {
            for (var i = 0; i < _levels.Count; i++)
                StepLevel(_levels[i].Price, _levels[i].Tag, bar, c);

            if (_showFvg && _fvgTrackReactions)
            {
                for (var i = 0; i < _fvgs.Count; i++)
                {
                    var g = _fvgs[i];
                    if (g.Bar >= bar)
                        continue;

                    var tag = g.Bullish ? "FVG+" : "FVG-";
                    StepLevel(g.Top, tag + "T", bar, c);
                    StepLevel(g.Bottom, tag + "B", bar, c);
                    if (_fvgShowMid)
                        StepLevel(g.Mid, tag + "CE", bar, c);
                }
            }

            if (_showRound)
            {
                StepRoundStep(_roundStepB, bar, c);
                StepRoundStep(_roundStepA, bar, c);
            }

            PruneRx(bar);
        }

        private void StepRoundStep(decimal step, int bar, IndicatorCandle c)
        {
            if (step <= 0m || _roundRangePts <= 0m || c.Close <= 0m)
                return;

            var span = (int)(_roundRangePts / step);
            if (span > 64)
                span = 64;              // a hard bound: a tiny step must not turn this into a scan

            var basePrice = Math.Floor(c.Close / step) * step;
            var tag = RoundTag(step);

            for (var k = -span; k <= span + 1; k++)
            {
                var p = basePrice + k * step;
                if (p <= 0m || Math.Abs(p - c.Close) > _roundRangePts)
                    continue;

                StepLevel(Snap(p), tag, bar, c);
            }
        }

        /// <summary>
        /// Arm, sweep, reclaim -- with the sweep capped at MaxPenetrationTicks. Beyond that cap the
        /// level did not hold, it broke, and the machine returns to idle rather than waiting for a
        /// reclaim that would be a different trade entirely.
        /// </summary>
        private void StepLevel(decimal price, string tag, int bar, IndicatorCandle c)
        {
            if (price <= 0m)
                return;

            var prox = _proximityTicks * _tick;
            var maxPen = _maxPenetrationTicks * _tick;

            var touches = c.Low <= price + prox && c.High >= price - prox;

            Rx rx;
            if (!_rx.TryGetValue(price, out rx))
            {
                // Do not allocate state for a price nothing has come near yet. Round magnets sweep
                // across a wide band as price moves, and this is what keeps that bounded.
                if (!touches)
                    return;

                rx = new Rx();
                _rx[price] = rx;
            }

            if (touches)
                rx.Touch = bar;

            if (rx.Phase == 0)
            {
                if (!touches)
                    return;

                // Origin comes from the PREVIOUS close. Taking it from this bar's close would make
                // a bar that closed straight through the level report the side it ended on, which
                // is exactly backwards.
                var prevClose = bar > 0 ? GetCandle(bar - 1).Close : c.Open;
                rx.Origin = prevClose > price ? 1 : prevClose < price ? -1 : 0;
                if (rx.Origin == 0)
                    return;

                rx.Phase = 1;
                rx.ArmBar = bar;
                rx.Extreme = rx.Origin > 0 ? c.Low : c.High;
                rx.SweepDelta = 0m;
            }

            // How far this bar pushed PAST the level, on the far side from where price came from.
            var pen = rx.Origin > 0 ? price - c.Low : c.High - price;

            if (pen > 0m)
            {
                if (rx.Phase == 1)
                {
                    rx.Phase = 2;
                    rx.SweepBar = bar;
                    rx.Extreme = rx.Origin > 0 ? c.Low : c.High;
                    rx.SweepDelta = c.Delta;
                }
                else if (rx.Phase == 2)
                {
                    var deeper = rx.Origin > 0 ? c.Low < rx.Extreme : c.High > rx.Extreme;
                    if (deeper)
                    {
                        rx.Extreme = rx.Origin > 0 ? c.Low : c.High;
                        rx.SweepDelta += c.Delta;
                    }
                }

                var deepest = rx.Origin > 0 ? price - rx.Extreme : rx.Extreme - price;
                if (deepest > maxPen)
                {
                    // Broken, not swept. There is nothing here to reclaim.
                    rx.Reset();
                    return;
                }
            }

            if (rx.Phase == 2)
            {
                if (bar - rx.SweepBar > _reclaimBars)
                {
                    rx.Reset();
                    return;
                }

                var need = _requireCloseInside ? prox : 0m;
                var back = rx.Origin > 0 ? c.Close >= price + need : c.Close <= price - need;

                if (back)
                {
                    if (_useDeltaFilter)
                    {
                        // A sweep DOWN that gets reclaimed is a long, and wants delta that refused
                        // to follow price down -- size absorbed into the low.
                        // VERIFY: IndicatorCandle.Delta is zero on feeds without bid/ask separation.
                        // VERIFY: leave this filter off unless delta is known good on this connection.
                        var ok = rx.Origin > 0 ? rx.SweepDelta >= 0m : rx.SweepDelta <= 0m;
                        if (!ok)
                        {
                            rx.Reset();
                            return;
                        }
                    }

                    Confirm(bar, price, rx.Origin, tag);
                    rx.Reset();
                }

                return;
            }

            // Armed, never swept, and price has walked away: let the level go cold.
            if (rx.Phase == 1 && !touches && bar - rx.ArmBar > _reclaimBars)
                rx.Reset();
        }

        private void Confirm(int bar, decimal price, int dir, string tag)
        {
            var r = new Reaction();
            r.Bar = bar;
            r.Price = price;
            r.Dir = dir;
            r.Tag = tag;
            _reactions.Add(r);

            // Confirmations only ever accumulate on closed bars, so a marker placed here stays put.
            if (_reactions.Count > 500)
                _reactions.RemoveRange(0, _reactions.Count - 500);

            if (!AlertsOn)
                return;

            // Alert only at the live edge. Without this, loading a month of history fires a month
            // of alerts the moment the indicator is added.
            if (bar < CurrentBar - 2)
                return;

            var key = bar.ToString(CultureInfo.InvariantCulture) + "|" + tag + "|" +
                      price.ToString(CultureInfo.InvariantCulture);
            if (!_alerted.Add(key))
                return;

            AddAlert("alert1",
                _alertText + ": " + (dir > 0 ? "LONG" : "SHORT") + " " + tag + " " +
                FormatPrice(price) + " (" + Last3(price) + ")");
        }

        private static string RoundTag(decimal step)
        {
            return "R" + Math.Truncate(step).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Round magnets move with price, so idle state for prices price has long since
        /// left behind would otherwise grow without bound over a session.</summary>
        private void PruneRx(int bar)
        {
            if (_rx.Count < 600)
                return;

            _stale.Clear();
            foreach (var kv in _rx)
            {
                if (kv.Value.Phase == 0 && bar - kv.Value.Touch > 500)
                    _stale.Add(kv.Key);
            }

            for (var i = 0; i < _stale.Count; i++)
                _rx.Remove(_stale[i]);
        }

        #endregion

        #region OnRender / drawing

        /// <summary>
        /// Drawing only. Every price on the chart was decided in OnCalculate; nothing here mutates
        /// state, so a redraw at a different zoom cannot change what the indicator claims.
        /// </summary>
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            // VERIFY: this class subscribes only to DrawingLayouts.Final, so the guard should never
            // // fire. It stays because a future SubscribeToDrawingEvents change would otherwise
            // // draw the whole level set once per layout.
            if (layout != DrawingLayouts.Final)
                return;

            if (ChartInfo == null || ChartInfo.PriceChartContainer == null || InstrumentInfo == null)
                return;

            var region = ChartInfo.PriceChartContainer.Region;
            if (region.Width <= 0 || region.Height <= 0)
                return;

            if (_font == null)
                _font = new RenderFont(_labelFontFamily, _labelFontSize);

            context.SetClip(region);
            try
            {
                if (_zoneError != null)
                {
                    context.DrawString(_zoneError, _font, Conv(ResistanceColor), region.Left + 6, region.Top + 4);
                    return;
                }

                if (_notice != null)
                    context.DrawString(_notice, _font, Conv(ResistanceColor), region.Left + 6, region.Top + 4);

                if (_days.Count == 0 || _lastClose <= 0m)
                {
                    // Warm-up. Say so, rather than draw an empty chart that looks like a working one.
                    context.DrawString("Pivot Decoder V2: waiting for history", _font,
                        Conv(NeutralColor), region.Left + 6, region.Top + 20);
                    return;
                }

                // Every layer is caught on its own. A layer that throws inside a shared try
                // takes down every layer after it -- INCLUDING whatever would have reported the
                // problem -- and there is no debugger on the render thread. So each one fails
                // alone and says so on the chart.
                _failures = 0;

                Layer(context, region, "prep", PrepLayer);

                if (_showFvg && FvgFillBody)
                    Layer(context, region, "fvg", DrawFvgBodies);

                Layer(context, region, "zones", DrawZones);
                Layer(context, region, "levels", DrawSingles);

                if (ShowReactionMarkers)
                    Layer(context, region, "markers", DrawMarkers);
            }
            finally
            {
                context.ResetClip();
            }
        }

        /// <summary>Runs one drawing layer in isolation and prints its failure where it can be
        /// seen, stacking messages so a second failure cannot hide behind the first.</summary>
        private void Layer(RenderContext context, Rectangle region, string name,
                           Action<RenderContext, Rectangle> body)
        {
            try
            {
                body(context, region);
            }
            catch (Exception ex)
            {
                try
                {
                    context.DrawString("Pivot Decoder V2 [" + name + "] " + ex.Message, _font,
                        Conv(ResistanceColor), region.Left + 6, region.Top + 36 + _failures * 16);
                    _failures++;
                }
                catch
                {
                    // The reporting itself failed. Nothing further can be said on the chart, and
                    // throwing out of here would take the remaining layers with it.
                }
            }
        }

        private void PrepLayer(RenderContext context, Rectangle region)
        {
            BuildDrawList();
            BuildZones();
            _labelYs.Clear();
        }

        /// <summary>The session levels plus the two that move with price. Reuses the same list every
        /// frame; the dynamic entries are the only per-frame allocation and there are few.</summary>
        private void BuildDrawList()
        {
            _draw.Clear();

            for (var i = 0; i < _levels.Count; i++)
                _draw.Add(_levels[i]);

            if (_showRound)
            {
                // Coarser step first: a price that is both a 100 and a 25 is one level, not two, so
                // the finer step skips the duplicate rather than double-counting it in confluence.
                AddRoundsToDraw(_roundStepB, 2);
                AddRoundsToDraw(_roundStepA, 1);
            }

            if (_showFvg)
            {
                for (var i = 0; i < _fvgs.Count; i++)
                {
                    var g = _fvgs[i];
                    var tag = g.Bullish ? "FVG+" : "FVG-";

                    _draw.Add(MakeLvl(tag + "T", g.Top, Group.Fvg, 1, g));
                    _draw.Add(MakeLvl(tag + "B", g.Bottom, Group.Fvg, 1, g));
                    if (_fvgShowMid)
                        _draw.Add(MakeLvl(tag + "CE", g.Mid, Group.Fvg, 2, g));
                }
            }

            _draw.Sort(ByPrice);
        }

        private void AddRoundsToDraw(decimal step, int weight)
        {
            if (step <= 0m || _roundRangePts <= 0m)
                return;

            var span = (int)(_roundRangePts / step);
            if (span > 64)
                span = 64;

            var basePrice = Math.Floor(_lastClose / step) * step;
            var tag = RoundTag(step);

            for (var k = -span; k <= span + 1; k++)
            {
                var p = Snap(basePrice + k * step);
                if (p <= 0m || Math.Abs(p - _lastClose) > _roundRangePts)
                    continue;

                if (AlreadyDrawn(p))
                    continue;

                _draw.Add(MakeLvl(tag, p, Group.Round, weight, null));
            }
        }

        private bool AlreadyDrawn(decimal price)
        {
            for (var i = 0; i < _draw.Count; i++)
            {
                if (_draw[i].Price == price)
                    return true;
            }
            return false;
        }

        private static Lvl MakeLvl(string tag, decimal price, Group group, int weight, Fvg gap)
        {
            var l = new Lvl();
            l.Tag = tag;
            l.Price = price;
            l.Group = group;
            l.Weight = weight;
            l.Gap = gap;
            return l;
        }

        /// <summary>
        /// A chained merge over the price-sorted list: anything within the merge distance of the
        /// band so far joins it. Bands of one draw as lines; bands of two or more get shaded, with
        /// opacity carrying the weight so the strongest confluence is what the eye lands on.
        /// </summary>
        private void BuildZones()
        {
            _zones.Clear();
            if (_draw.Count == 0)
                return;

            var merge = _zoneMergeTicks * _tick;

            var i = 0;
            while (i < _draw.Count)
            {
                var z = new Zone();
                z.Low = _draw[i].Price;
                z.High = _draw[i].Price;
                z.Weight = _draw[i].Weight;
                z.Count = 1;
                z.Tags = _draw[i].Tag;

                var j = i + 1;
                while (j < _draw.Count && _draw[j].Price - z.High <= merge)
                {
                    z.High = _draw[j].Price;
                    z.Weight += _draw[j].Weight;
                    z.Count++;

                    if (z.Count <= 4) z.Tags = z.Tags + " " + _draw[j].Tag;
                    else if (z.Count == 5) z.Tags = z.Tags + " +";

                    j++;
                }

                _zones.Add(z);
                i = j;
            }
        }

        /// <summary>The imbalance body itself, drawn from the bar that created it to the right edge
        /// so it reads as a zone price has not yet come back for.</summary>
        private void DrawFvgBodies(RenderContext context, Rectangle region)
        {
            for (var i = 0; i < _fvgs.Count; i++)
            {
                var g = _fvgs[i];

                var yTop = Y(g.Top);
                var yBot = Y(g.Bottom);
                if (yBot < yTop) { var t = yTop; yTop = yBot; yBot = t; }
                if (yBot < region.Top || yTop > region.Bottom)
                    continue;

                var x = XOf(g.Bar);
                if (x > region.Right)
                    continue;
                if (x < region.Left)
                    x = region.Left;

                var top = Clamp(yTop, region.Top, region.Bottom);
                var bottom = Clamp(yBot, region.Top, region.Bottom);
                if (bottom - top < 1)
                    bottom = top + 1;

                var rect = Rectangle.FromLTRB(x, top, region.Right, bottom);
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                context.FillRectangle(Alpha(SideColor(SideOf(g.Bottom, g.Top)), FvgFillAlpha), rect);
            }
        }

        private void DrawZones(RenderContext context, Rectangle region)
        {
            for (var i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z.Count < 2)
                    continue;

                var yHigh = Y(z.High);
                var yLow = Y(z.Low);
                if (yLow < region.Top || yHigh > region.Bottom)
                    continue;

                var top = Clamp(yHigh, region.Top, region.Bottom);
                var bottom = Clamp(yLow, region.Top, region.Bottom);

                // A band of two levels one tick apart has no height; give it enough to be seen.
                if (bottom - top < 3) { top = top - 1; bottom = top + 3; }

                var rect = Rectangle.FromLTRB(region.Left, top, region.Right, bottom);
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                var color = SideColor(SideOf(z.Low, z.High));

                context.FillRectangle(Alpha(color, ZoneAlpha(z.Weight)), rect);

                var pen = new RenderPen(color, SessionWidth, SessionDash);
                context.DrawLine(pen, rect.Left, top, rect.Right, top);
                context.DrawLine(pen, rect.Left, bottom, rect.Right, bottom);

                if (!ShowLabels)
                    continue;

                var mid = (z.Low + z.High) / 2m;
                var text = z.Tags + " " + Last3(mid) + " x" + z.Count.ToString(CultureInfo.InvariantCulture);
                if (!ShowOnly3Digit && ShowFullPrice)
                    text = text + "  (" + FormatPrice(z.Low) + Sep + FormatPrice(z.High) + ")";

                DrawTag(context, region, text, (top + bottom) / 2, color);
            }
        }

        private void DrawSingles(RenderContext context, Rectangle region)
        {
            for (var i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z.Count != 1)
                    continue;

                var price = z.Low;
                var y = Y(price);
                if (y < region.Top || y > region.Bottom)
                    continue;

                var lvl = FindDrawLevel(price);
                if (lvl == null)
                    continue;

                var color = SideColor(SideOf(price, price));

                int width;
                DashStyle dash;
                switch (lvl.Group)
                {
                    case Group.Round: width = RoundWidth; dash = RoundDash; break;
                    case Group.Fvg: width = FvgWidth; dash = FvgDash; break;
                    default: width = SessionWidth; dash = SessionDash; break;
                }

                context.DrawLine(new RenderPen(color, width, dash), region.Left, y, region.Right, y);

                if (ShowLabels)
                    DrawTag(context, region, LabelFor(lvl), y, color);
            }
        }

        private Lvl FindDrawLevel(decimal price)
        {
            for (var i = 0; i < _draw.Count; i++)
            {
                if (_draw[i].Price == price)
                    return _draw[i];
            }
            return null;
        }

        /// <summary>"PDH 605 (29,605.50)" -- the tag, the caller's three digits, then the full price.</summary>
        private string LabelFor(Lvl lvl)
        {
            var code = Last3(lvl.Price);

            if (ShowOnly3Digit || !ShowFullPrice)
                return lvl.Tag + " " + code;

            return lvl.Tag + " " + code + "  (" + FormatPrice(lvl.Price) + ")";
        }

        /// <summary>
        /// Right-anchored against the price region, never floating on a bar. Overlapping tags get
        /// nudged down instead of stacking: two unreadable labels are worse than one label and a gap.
        /// </summary>
        private void DrawTag(RenderContext context, Rectangle region, string text, int y, Color color)
        {
            var size = context.MeasureString(text, _font);

            var x = region.Right - size.Width - RightMargin;
            if (x < region.Left)
                x = region.Left + 2;

            var ly = y - size.Height / 2;

            // Each nudge strictly increases ly, so this terminates; the guard is belt and braces
            // against a pathological label count on a very short chart region.
            var guard = 0;
            var collided = true;
            while (collided && guard < 64)
            {
                collided = false;
                guard++;

                for (var i = 0; i < _labelYs.Count; i++)
                {
                    if (Math.Abs(_labelYs[i] - ly) < size.Height)
                    {
                        ly = _labelYs[i] + size.Height;
                        collided = true;
                        break;
                    }
                }
            }

            if (ly < region.Top) ly = region.Top;
            if (ly + size.Height > region.Bottom) ly = region.Bottom - size.Height;
            _labelYs.Add(ly);

            if (LabelBackdrop)
            {
                context.FillRectangle(Color.FromArgb(200, 16, 16, 20),
                    new Rectangle(x - 3, ly - 1, size.Width + 6, size.Height + 2));
            }

            context.DrawString(text, _font, color, x, ly);
        }

        /// <summary>Confirmed reactions only, at the bar they were confirmed on. Because confirmation
        /// happens on closed bars and is never recomputed, these do not move.</summary>
        private void DrawMarkers(RenderContext context, Rectangle region)
        {
            var first = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
            var last = ChartInfo.PriceChartContainer.LastVisibleBarNumber;

            for (var i = 0; i < _reactions.Count; i++)
            {
                var r = _reactions[i];
                if (r.Bar < first - 1 || r.Bar > last + 1)
                    continue;

                var x = XOf(r.Bar);
                var y = Y(r.Price);
                if (x < region.Left || x > region.Right || y < region.Top || y > region.Bottom)
                    continue;

                var color = r.Dir > 0 ? Conv(SupportColor) : Conv(ResistanceColor);
                var s = MarkerSize;

                if (r.Dir > 0)
                {
                    // Reclaimed upward: the apex points where the trade goes, and the body sits
                    // below the level so it does not cover the wick that made it.
                    _tri[0] = new Point(x, y + s + 2);
                    _tri[1] = new Point(x - s, y + s * 2 + 2);
                    _tri[2] = new Point(x + s, y + s * 2 + 2);
                }
                else
                {
                    _tri[0] = new Point(x, y - s - 2);
                    _tri[1] = new Point(x - s, y - s * 2 - 2);
                    _tri[2] = new Point(x + s, y - s * 2 - 2);
                }

                context.FillPolygon(color, _tri);
            }
        }

        #endregion

        #region Helpers

        private const string Sep = " - ";

        private decimal Snap(decimal price)
        {
            if (_tick <= 0m)
                return price;

            return Math.Round(price / _tick, MidpointRounding.AwayFromZero) * _tick;
        }

        /// <summary>
        /// The caller's shorthand. Last three digits of the INTEGER part, zero padded, so 29,605.50
        /// reads 605 and 30,054.00 reads 054 rather than 54.
        /// </summary>
        private static string Last3(decimal price)
        {
            var whole = (long)Math.Truncate(Math.Abs(price));
            var tail = whole % 1000L;
            return tail.ToString("000", CultureInfo.InvariantCulture);
        }

        private string FormatPrice(decimal price)
        {
            return price.ToString(_priceFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>+1 entirely above price (resistance), -1 entirely below (support), 0 when price
        /// is inside the band. Recomputed every frame, so the split follows price as it moves.</summary>
        private int SideOf(decimal low, decimal high)
        {
            if (_lastClose <= 0m)
                return 0;
            if (low > _lastClose) return 1;
            if (high < _lastClose) return -1;
            return 0;
        }

        private Color SideColor(int side)
        {
            return side > 0 ? Conv(ResistanceColor)
                 : side < 0 ? Conv(SupportColor)
                 : Conv(NeutralColor);
        }

        private int ZoneAlpha(int weight)
        {
            var lo = Clamp(ZoneMinAlpha, 0, 255);
            var hi = Clamp(ZoneMaxAlpha, 0, 255);
            if (hi < lo) hi = lo;

            var full = ZoneFullWeight < 2 ? 2 : ZoneFullWeight;
            var t = weight <= 2 ? 0f : weight >= full ? 1f : (weight - 2f) / (full - 2f);

            return lo + (int)((hi - lo) * t);
        }

        private static Color Alpha(Color c, int alpha)
        {
            return Color.FromArgb(Clamp(alpha, 0, 255), c.R, c.G, c.B);
        }

        private static Color Conv(MColor c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
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

        #endregion

        #region Acceptance tests

        // TESTS: run these on a live chart, in this order. Each has a specific failure signature.
        //
        // TESTS 1 -- BUILD AND LOAD.
        //   dotnet build OceansPivotDecoderV2.csproj -c Release  (net10.0-windows; net8.0 gives
        //   CS1705 against the net10 ATAS assemblies). Restart ATAS, add to an NQ 5-minute chart.
        //   PASS: levels render, no exception dialog, no red text at the top of the price region.
        //
        // TESTS 2 -- DST BOUNDARY. Scroll to the first RTH session after the March and the November
        //   US clock change. PDH / PDL for that day must equal the 08:30-15:00 Houston range, not a
        //   range shifted an hour. FAIL SIGNATURE: every level off by exactly one hour on one side
        //   of the change and correct on the other -- that is a fixed offset having crept in
        //   somewhere, since TimeZoneInfo itself cannot drift.
        //
        // TESTS 3 -- OVERNIGHT COVERAGE. Hand-check ON-H / ON-L against the 17:00 (prior day) to
        //   08:30 window. They must EXCLUDE the RTH range: if ON-H equals PDH on a trending day,
        //   the overnight window is leaking into RTH.
        //
        // TESTS 4 -- FVG. Find a clean 3-bar gap by eye. Top, bottom and CE must plot at High[i-2],
        //   Low[i] and their midpoint (bullish case). Let price trade fully back through it: the
        //   gap must disappear on the bar that closes the fill, and not before.
        //
        // TESTS 5 -- THREE-DIGIT CODE. 29,605.50 -> 605. 29,587.75 -> 587. 29,506.00 -> 506.
        //   30,054.00 -> 054, zero padded, NOT 54. This is the entire point of the label; check it
        //   against three live caller marks before trusting one.
        //
        // TESTS 6 -- REACTION, NO REPAINT. Find a sweep-and-reclaim. The triangle must appear on
        //   the bar the reclaim CLOSED, and must not move, vanish or duplicate when the chart is
        //   redrawn, zoomed or scrolled. Change timeframe and back: markers reappear in the same
        //   places because they are recomputed from the same closed bars.
        //
        // TESTS 7 -- COST. Leave it on a live NQ chart through the RTH open. The session scan runs
        //   once per CLOSED bar (the _processed watermark), so tick rate must not move CPU. FAIL
        //   SIGNATURE: chart stutter that scales with history length -- that would mean the fold is
        //   re-running, which the watermark exists to prevent.
        //
        // TESTS 8 -- CONFLUENCE. Set merge distance to 12 ticks and find two levels within 3 NQ
        //   points of each other. They must render as ONE shaded band with a combined tag and an
        //   "x2" count, not two lines. Raise the distance: bands absorb more levels and darken.
        //
        // TESTS 9 -- WARM-UP. Add the indicator to a chart with almost no history loaded. It must
        //   print "waiting for history" and never throw.

        #endregion
    }
}
