using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansRead
{
    /// <summary>Where the read box sits.</summary>
    public enum BoxCorner
    {
        [Display(Name = "Top left")] TopLeft,
        [Display(Name = "Top right")] TopRight,
        [Display(Name = "Bottom left")] BottomLeft,
        [Display(Name = "Bottom right")] BottomRight
    }

    /// <summary>
    /// Ocean's Read -- one structural read of the auction, in the order the method reads it.
    ///
    /// Condition (balancing or imbalancing), location (against developing and prior value and
    /// the top-down VWAP stack), structure (the character of the last rotations and an Elliott
    /// count only when every rule holds), rhythm (the market's own measured rotation size in
    /// price and time), developing value, acceptance (time AND ground beyond a level, both
    /// scaled by the measured rhythm), and orderflow last -- because orderflow confirms a read,
    /// it does not produce one.
    ///
    /// The deliverable is the READ BOX. The chart keeps only the levels the box refers to, so
    /// the two can be read together without the overlays burying the candles.
    ///
    /// Three things it refuses to do, all because the failure mode is a clean-looking chart
    /// rather than an error:
    ///  - It will not cut a session until <see cref="TimeContext"/> has settled whether ATAS is
    ///    stamping bars UTC or local. A whole-day shift draws a plausible profile of the wrong
    ///    session.
    ///  - It will not judge acceptance without a measured rhythm. A fixed tick count that means
    ///    "far" overnight means "noise" at the open.
    ///  - It will not print an Elliott count unless all four hard rules hold. A labeller that
    ///    always finds five waves would find five in a random walk.
    /// </summary>
    [DisplayName("Oceans Read")]
    [Category("Ocean")]
    public class OceansReadIndicator : Indicator
    {
        private const int MaxProfileTicks = 60000;
        private const int LiveWindow = 6000;      // bars back from the right edge kept fully live

        #region State

        private sealed class SessionState
        {
            public DateTime Key;
            public DateTime TradeDate;
            public ProfileBuilder Builder = new ProfileBuilder();
            public ReadProfile Profile;

            public decimal High;
            public decimal Low = decimal.MaxValue;
            public decimal Open;
            public decimal Delta;
            public decimal Volume;
            public decimal OiOpen;
            public decimal OiLast;

            public decimal OvernightHigh;
            public decimal OvernightLow = decimal.MaxValue;

            public InitialBalance Ib;
            public int FirstBar = -1;
            public int LastBar = -1;
            public DateTime Start;
        }

        private readonly SwingTracker _swings = new SwingTracker();
        private readonly PocTrail _poc = new PocTrail();
        private readonly AcceptanceTracker _acceptance = new AcceptanceTracker();
        private readonly VwapStack _vwap = new VwapStack();

        private readonly List<decimal> _high = new List<decimal>();
        private readonly List<decimal> _low = new List<decimal>();
        private readonly List<decimal> _close = new List<decimal>();
        private readonly List<decimal> _cumDelta = new List<decimal>();
        private readonly List<PivotFlow> _pivotFlow = new List<PivotFlow>();

        private readonly Dictionary<VwapAnchor, ValueDataSeries> _vwapSeries =
            new Dictionary<VwapAnchor, ValueDataSeries>();

        private SessionState _session;
        private SessionState _prior;

        private int _folded;
        private int _swingsEmitted;
        private decimal _cumulative;
        private DateTime _earliestBar = DateTime.MaxValue;
        private bool _footprint;
        private bool _anyOi;
        private decimal _settingsStamp = decimal.MinValue;

        private int _timeTriedAtBar = -1;
        private TimeContext _time;

        private readonly List<string> _layerErrors = new List<string>();

        private readonly RenderFont _font = new RenderFont("Consolas", 11f);
        private readonly RenderFont _boldFont = new RenderFont("Consolas", 11f, FontStyle.Bold);
        private readonly RenderFont _tagFont = new RenderFont("Arial", 8f);

        #endregion

        public OceansReadIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = false;

            if (DataSeries[0] is ValueDataSeries baseSeries)
            {
                baseSeries.VisualType = VisualMode.Hide;
                baseSeries.IsHidden = true;
                baseSeries.ShowZeroValue = false;
                baseSeries.ScaleIt = false;
                baseSeries.IgnoredByAlerts = true;
            }

            // A VWAP is a value per bar, so it is a series: ATAS then scales it, legends it and
            // price-tags it, which is a great deal less code than hand-drawing six lines.
            AddVwapSeries(VwapAnchor.Year, "Yearly VWAP", MColor.FromRgb(190, 130, 255), 2);
            AddVwapSeries(VwapAnchor.Quarter, "Quarterly VWAP", MColor.FromRgb(120, 150, 255), 2);
            AddVwapSeries(VwapAnchor.Month, "Monthly VWAP", MColor.FromRgb(90, 180, 255), 2);
            AddVwapSeries(VwapAnchor.Week, "Weekly VWAP", MColor.FromRgb(60, 210, 190), 2);
            AddVwapSeries(VwapAnchor.Day, "Daily VWAP", MColor.FromRgb(255, 205, 70), 2);
            AddVwapSeries(VwapAnchor.Session, "Session VWAP", MColor.FromRgb(255, 255, 255), 1);
        }

        private void AddVwapSeries(VwapAnchor anchor, string name, MColor color, int width)
        {
            var series = new ValueDataSeries("read" + anchor, name)
            {
                Color = color,
                Width = width,
                VisualType = VisualMode.Line,
                ShowZeroValue = false,
                IgnoredByAlerts = true,
                ScaleIt = false,
                UseMinimizedModeIfEnabled = true
            };

            _vwapSeries[anchor] = series;
            DataSeries.Add(series);
        }

        #region Settings -- session

        [Display(Name = "Session", GroupName = "01 Session", Order = 100,
                 Description = "Which hours the session profile, value area and initial " +
                               "balance are cut from. Everything is in Central time.")]
        public SessionScope Scope { get; set; } = SessionScope.FuturesDay;

        [Display(Name = "Initial balance minutes", GroupName = "01 Session", Order = 110,
                 Description = "The opening range the day type is measured against, from the " +
                               "08:30 Central cash open. 60 is the convention.")]
        [Range(5, 240)]
        public int IbMinutes { get; set; } = 60;

        [Display(Name = "TPO bracket minutes", GroupName = "01 Session", Order = 120,
                 Description = "How long one time bracket lasts. This is what makes a price " +
                               "a single print or a poor high, so it changes those readings.")]
        [Range(1, 120)]
        public int BracketMinutes { get; set; } = 30;

        #endregion

        #region Settings -- clock

        [Display(Name = "Time zone", GroupName = "02 Clock", Order = 200,
                 Description = "Houston is 'Central Standard Time'. Everything is cut and " +
                               "labelled in this one zone -- no second zone anywhere.")]
        public string ZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "02 Clock", Order = 210,
                 Description = "Whether ATAS stamps bars UTC or already local. Auto works it " +
                               "out from the data and says which it found.")]
        public BarClock Clock { get; set; } = BarClock.Auto;

        #endregion

        #region Settings -- profile

        [Display(Name = "Value area size", GroupName = "03 Profile", Order = 300,
                 Description = "The share of the measure inside the value area. 70 is the " +
                               "convention.")]
        [Range(30, 95)]
        public decimal ValueSize { get; set; } = 70m;

        [Display(Name = "Cut value from", GroupName = "03 Profile", Order = 310,
                 Description = "Volume is what changed hands; TPO is how long the market " +
                               "stayed. They disagree, and the disagreement is information.")]
        public ProfileBasis Basis { get; set; } = ProfileBasis.Volume;

        [Display(Name = "Poor extreme needs this many brackets", GroupName = "03 Profile", Order = 320,
                 Description = "A high that several brackets traded flat against is unfinished.")]
        [Range(2, 10)]
        public int PoorTpo { get; set; } = 2;

        [Display(Name = "Single print run (ticks)", GroupName = "03 Profile", Order = 330,
                 Description = "How many ticks of one-bracket prices count as a gap the " +
                               "auction skipped.")]
        [Range(1, 40)]
        public int SinglePrintTicks { get; set; } = 4;

        #endregion

        #region Settings -- rhythm

        [Display(Name = "Rotation threshold (x ATR)", GroupName = "04 Rhythm", Order = 400,
                 Description = "How far price must pull back to end a rotation, as a multiple " +
                               "of average true range. Larger reads bigger, slower rotations.")]
        [Range(0.2, 10.0)]
        public decimal SwingAtr { get; set; } = 1.5m;

        [Display(Name = "ATR bars", GroupName = "04 Rhythm", Order = 410)]
        [Range(3, 500)]
        public int AtrBars { get; set; } = 20;

        [Display(Name = "Rotations to measure", GroupName = "04 Rhythm", Order = 420,
                 Description = "How many completed rotations the median is taken over.")]
        [Range(5, 500)]
        public int RhythmLegs { get; set; } = 40;

        [Display(Name = "Fewest rotations to call it a rhythm", GroupName = "04 Rhythm", Order = 430,
                 Description = "Below this the rhythm is reported as unmeasured rather than " +
                               "guessed, and every threshold scaled by it stands down.")]
        [Range(3, 100)]
        public int RhythmMinLegs { get; set; } = 8;

        #endregion

        #region Settings -- acceptance

        [Display(Name = "Ground needed (x median rotation)", GroupName = "05 Acceptance", Order = 500,
                 Description = "How far beyond a level price must travel to have accepted it, " +
                               "as a multiple of the market's own median rotation.")]
        [Range(0.1, 5.0)]
        public decimal AcceptGround { get; set; } = 1.0m;

        [Display(Name = "Time needed (x median rotation)", GroupName = "05 Acceptance", Order = 510,
                 Description = "How long price must hold beyond, counting only bars that " +
                               "CLOSED beyond, as a multiple of the median rotation's duration.")]
        [Range(0.1, 5.0)]
        public decimal AcceptTime { get; set; } = 1.0m;

        [Display(Name = "Test prior-session value", GroupName = "05 Acceptance", Order = 520)]
        public bool TestPriorValue { get; set; } = true;

        [Display(Name = "Test prior-session high and low", GroupName = "05 Acceptance", Order = 530)]
        public bool TestPriorRange { get; set; } = true;

        [Display(Name = "Test overnight high and low", GroupName = "05 Acceptance", Order = 540)]
        public bool TestOvernight { get; set; } = true;

        [Display(Name = "Test the initial balance", GroupName = "05 Acceptance", Order = 550)]
        public bool TestIb { get; set; } = true;

        #endregion

        #region Settings -- orderflow

        [Display(Name = "Level band (ticks)", GroupName = "06 Orderflow", Order = 600,
                 Description = "How far either side of a level the footprint is summed. The " +
                               "level being defended is never traded to the exact tick.")]
        [Range(0, 40)]
        public int LevelBand { get; set; } = 3;

        [Display(Name = "Absorption: share of session volume", GroupName = "06 Orderflow", Order = 610,
                 Description = "How heavy trade at the level has to be before it counts.")]
        [Range(0.005, 0.5)]
        public decimal AbsorptionShare { get; set; } = 0.04m;

        [Display(Name = "Absorption: one-sided share", GroupName = "06 Orderflow", Order = 620,
                 Description = "How lopsided the aggression has to be, 0.5 to 1.")]
        [Range(0.5, 1.0)]
        public decimal AbsorptionSided { get; set; } = 0.62m;

        [Display(Name = "Absorption: allowed progress (ticks)", GroupName = "06 Orderflow", Order = 630,
                 Description = "Heavy one-sided trade that moved price further than this is a " +
                               "breakout, not absorption.")]
        [Range(0, 100)]
        public int AbsorptionGround { get; set; } = 6;

        [Display(Name = "Delta that counts", GroupName = "06 Orderflow", Order = 640,
                 Description = "Leg delta smaller than this is treated as no opinion rather " +
                               "than a vote either way.")]
        [Range(0, 100000)]
        public decimal MinLegDelta { get; set; } = 200m;

        [Display(Name = "Open interest that counts", GroupName = "06 Orderflow", Order = 650)]
        [Range(0, 1000000)]
        public decimal MinOi { get; set; } = 50m;

        #endregion

        #region Settings -- VWAP

        [Display(Name = "Yearly VWAP", GroupName = "07 VWAP", Order = 700,
                 Description = "Needs a year of bars actually loaded. An anchor the history " +
                               "does not reach is not drawn at all -- a yearly VWAP built from " +
                               "three weeks looks exactly like a real one.")]
        public bool ShowYearVwap { get; set; } = true;

        [Display(Name = "Quarterly VWAP", GroupName = "07 VWAP", Order = 710)]
        public bool ShowQuarterVwap { get; set; } = true;

        [Display(Name = "Monthly VWAP", GroupName = "07 VWAP", Order = 720)]
        public bool ShowMonthVwap { get; set; } = true;

        [Display(Name = "Weekly VWAP", GroupName = "07 VWAP", Order = 730)]
        public bool ShowWeekVwap { get; set; } = true;

        [Display(Name = "Daily VWAP", GroupName = "07 VWAP", Order = 740)]
        public bool ShowDayVwap { get; set; } = true;

        [Display(Name = "Session VWAP", GroupName = "07 VWAP", Order = 750,
                 Description = "Anchored to whichever window the session profile is cut on.")]
        public bool ShowSessionVwap { get; set; } = false;

        #endregion

        #region Settings -- structure

        [Display(Name = "Rotations in the sequence read", GroupName = "08 Structure", Order = 700,
                 Description = "How many rotations the impulsive-or-corrective character is " +
                               "measured over.")]
        [Range(4, 40)]
        public int SequenceLegs { get; set; } = 8;

        [Display(Name = "Overlap share that means corrective", GroupName = "08 Structure", Order = 710,
                 Description = "The share of same-direction rotations that must overlap before " +
                               "the sequence reads as corrective.")]
        [Range(0.1, 1.0)]
        public decimal OverlapShare { get; set; } = 0.5m;

        [Display(Name = "Search back for a valid count", GroupName = "08 Structure", Order = 720,
                 Description = "How many rotations back to look for a five that keeps every " +
                               "rule. 0 only ever reads the last five.")]
        [Range(0, 20)]
        public int CountLookback { get; set; } = 6;

        #endregion

        #region Settings -- the read box

        [Display(Name = "Show the read", GroupName = "09 Read box", Order = 800,
                 Description = "The box is the deliverable. Everything drawn on the chart is " +
                               "there to show where the box got its numbers.")]
        public bool ShowBox { get; set; } = true;

        [Display(Name = "Corner", GroupName = "09 Read box", Order = 810)]
        public BoxCorner Corner { get; set; } = BoxCorner.TopLeft;

        [Display(Name = "Text size", GroupName = "09 Read box", Order = 820)]
        [Range(7, 20)]
        public int BoxTextSize { get; set; } = 11;

        [Display(Name = "Background opacity", GroupName = "09 Read box", Order = 830)]
        [Range(0, 255)]
        public int BoxOpacity { get; set; } = 225;

        #endregion

        #region Settings -- chart

        [Display(Name = "Developing value", GroupName = "10 Chart", Order = 900,
                 Description = "This session's point of control and value area, updated as it " +
                               "develops.")]
        public bool DrawDeveloping { get; set; } = true;

        [Display(Name = "Prior session levels", GroupName = "10 Chart", Order = 910)]
        public bool DrawPrior { get; set; } = true;

        [Display(Name = "Initial balance", GroupName = "10 Chart", Order = 920)]
        public bool DrawIb { get; set; } = true;

        [Display(Name = "Overnight high and low", GroupName = "10 Chart", Order = 930)]
        public bool DrawOvernight { get; set; } = true;

        [Display(Name = "Single prints and poor extremes", GroupName = "10 Chart", Order = 940)]
        public bool DrawUnfinished { get; set; } = true;

        [Display(Name = "Session histogram", GroupName = "10 Chart", Order = 950,
                 Description = "The developing profile as a lane at the right edge. Off by " +
                               "default -- the levels carry the read, the histogram is a check.")]
        public bool DrawHistogram { get; set; } = false;

        [Display(Name = "Rotation pivots", GroupName = "10 Chart", Order = 960,
                 Description = "Where the rhythm measurements come from. Worth turning on once " +
                               "to see whether the rotation threshold matches how you read the chart.")]
        public bool DrawPivots { get; set; } = false;

        [Display(Name = "Wave count", GroupName = "10 Chart", Order = 970,
                 Description = "Labels 1-5 on the chart, and only when every rule holds.")]
        public bool DrawWaves { get; set; } = true;

        [Display(Name = "Acceptance test", GroupName = "10 Chart", Order = 980,
                 Description = "The level under test, with the time and ground still needed.")]
        public bool DrawAcceptance { get; set; } = true;

        [Display(Name = "Level tags", GroupName = "10 Chart", Order = 990)]
        public bool DrawTags { get; set; } = true;

        [Display(Name = "Line width", GroupName = "10 Chart", Order = 1000)]
        [Range(1, 5)]
        public int LineThickness { get; set; } = 1;

        #endregion

        #region Calculation

        protected override void OnRecalculate()
        {
            ResetAll();
        }

        private void ResetAll()
        {
            _swings.Clear();
            _poc.Clear();
            _acceptance.Clear();
            _vwap.Clear();

            _high.Clear();
            _low.Clear();
            _close.Clear();
            _cumDelta.Clear();
            _pivotFlow.Clear();

            foreach (var series in _vwapSeries.Values) series.Clear();

            _session = null;
            _prior = null;
            _folded = 0;
            _swingsEmitted = 0;
            _cumulative = 0m;
            _earliestBar = DateTime.MaxValue;
            _footprint = false;
            _anyOi = false;

            _time = null;
            _timeTriedAtBar = -1;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0) ResetAll();

            // Anything that changes what a measurement IS has to rebuild from the start;
            // anything that only changes how it is drawn must not.
            var stamp = Stamp();
            if (stamp != _settingsStamp)
            {
                _settingsStamp = stamp;
                if (bar != 0) { ResetAll(); return; }
            }

            if (!ResolveTime()) return;

            var tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0m) return;

            // Only closed bars are folded. The forming bar changes under us, and a session
            // profile that includes a bar still being written moves its own point of control.
            while (_folded < bar)
            {
                Fold(_folded, tick);
                _folded++;
            }

            // The series still has to reach the right edge, so the forming bar carries the
            // running value rather than a hole.
            foreach (var anchor in VwapStack.Order)
            {
                var track = _vwap[anchor];
                _vwapSeries[anchor][bar] = VwapDrawable(anchor) && track.Valid ? track.Value : 0m;
            }
        }

        private bool VwapDrawable(VwapAnchor anchor)
        {
            if (!Wanted(anchor)) return false;

            // A yearly VWAP built from three weeks of loaded bars is not a yearly VWAP, and it
            // looks exactly like one, so it is not drawn at all. The read still counts it --
            // the box marks an incomplete anchor with a question mark rather than hiding it,
            // because knowing the yearly is unavailable is part of a top-down read.
            return _vwap[anchor].Complete;
        }

        private bool Wanted(VwapAnchor anchor)
        {
            switch (anchor)
            {
                case VwapAnchor.Year: return ShowYearVwap;
                case VwapAnchor.Quarter: return ShowQuarterVwap;
                case VwapAnchor.Month: return ShowMonthVwap;
                case VwapAnchor.Week: return ShowWeekVwap;
                case VwapAnchor.Day: return ShowDayVwap;
                default: return ShowSessionVwap;
            }
        }

        private decimal Stamp()
        {
            return (int)Scope * 1m
                 + IbMinutes * 3m
                 + BracketMinutes * 7m
                 + ValueSize * 11m
                 + (int)Basis * 13m
                 + PoorTpo * 17m
                 + SinglePrintTicks * 19m
                 + SwingAtr * 23m
                 + AtrBars * 29m
                 + RhythmLegs * 31m
                 + RhythmMinLegs * 37m
                 + AcceptGround * 41m
                 + AcceptTime * 43m
                 + (TestPriorValue ? 47m : 0m)
                 + (TestPriorRange ? 53m : 0m)
                 + (TestOvernight ? 59m : 0m)
                 + (TestIb ? 61m : 0m)
                 + (Clock == BarClock.Auto ? 0m : (int)Clock * 67m);
        }

        private void Fold(int bar, decimal tick)
        {
            var candle = GetCandle(bar);
            if (candle == null) return;

            var local = _time.ToLocal(candle.Time);
            if (local < _earliestBar) _earliestBar = local;

            _high.Add(candle.High);
            _low.Add(candle.Low);
            _close.Add(candle.Close);

            var delta = candle.Delta;
            _cumulative += delta;
            _cumDelta.Add(_cumulative);

            var tradeDate = ReadClock.TradeDate(local);
            var inScope = ReadClock.InScope(local, Scope);

            var sessionKey = Scope == SessionScope.FuturesDay
                           ? ReadClock.OpenOf(tradeDate)
                           : ReadClock.RegularOpenOf(tradeDate);

            if (_session == null || _session.Key != sessionKey)
            {
                if (_session != null && _session.Profile != null) _prior = _session;

                _session = new SessionState
                {
                    Key = sessionKey,
                    TradeDate = tradeDate,
                    Start = local,
                    Open = candle.Open,
                    OiOpen = candle.OI
                };

                _poc.Clear();
            }

            var live = bar >= CurrentBar - LiveWindow;

            if (inScope)
            {
                Accumulate(candle, bar, local, tradeDate);
                if (live) Rebuild(tick);
            }

            // The rotation series runs on every bar in scope, so the rhythm describes the same
            // hours the profile does.
            if (inScope) Rotate(bar, local, candle, tick);

            var price = candle.VWAP > 0m ? candle.VWAP : (candle.High + candle.Low + candle.Close) / 3m;
            _vwap.Add(tradeDate, sessionKey, local, price, candle.Volume, _earliestBar);

            foreach (var anchor in VwapStack.Order)
            {
                var track = _vwap[anchor];
                _vwapSeries[anchor][bar] = VwapDrawable(anchor) && track.Valid ? track.Value : 0m;
            }

            if (live && inScope) Judge(bar, local, candle, tick);
        }

        /// <summary>Everything the session carries: the profile, the extremes, the flow totals.</summary>
        private void Accumulate(IndicatorCandle candle, int bar, DateTime local, DateTime tradeDate)
        {
            var session = _session;

            if (session.FirstBar < 0) session.FirstBar = bar;
            session.LastBar = bar;

            if (candle.High > session.High) session.High = candle.High;
            if (candle.Low < session.Low) session.Low = candle.Low;

            session.Delta += candle.Delta;
            session.Volume += candle.Volume;
            session.OiLast = candle.OI;
            if (candle.OI > 0m) _anyOi = true;

            session.Builder.Note(local);

            var bracket = Bracket(local, session);
            var levels = 0;

            foreach (var level in candle.GetAllPriceLevels())
            {
                if (level == null) continue;

                session.Builder.Add(level.Price, level.Volume, level.Bid, level.Ask, level.Time, bracket);
                levels++;
            }

            if (levels > 0) _footprint = true;
            else
            {
                // No footprint on this chart. The bar's whole volume is put at its close and the
                // box says so in as many words -- a profile built from closes is a different
                // object from one built from the tape, and it must never pass for one.
                session.Builder.Add(candle.Close, candle.Volume, 0m, 0m, 0L, bracket);
            }

            // The overnight run-up belongs to the trade date it precedes, whatever the profile
            // scope is, because the cash open is measured against it either way.
            if (ReadClock.InOvernight(local) && !ReadClock.InRegularHours(local))
            {
                if (candle.High > session.OvernightHigh) session.OvernightHigh = candle.High;
                if (candle.Low < session.OvernightLow) session.OvernightLow = candle.Low;
            }

            if (ReadClock.InRegularHours(local))
            {
                var open = ReadClock.RegularOpenOf(tradeDate);
                var ends = open.AddMinutes(IbMinutes);

                if (local < ends)
                {
                    if (!session.Ib.Set)
                    {
                        session.Ib.Set = true;
                        session.Ib.High = candle.High;
                        session.Ib.Low = candle.Low;
                    }
                    else
                    {
                        if (candle.High > session.Ib.High) session.Ib.High = candle.High;
                        if (candle.Low < session.Ib.Low) session.Ib.Low = candle.Low;
                    }

                    session.Ib.Closed = default;
                }
                else if (session.Ib.Set && session.Ib.Closed == default)
                {
                    session.Ib.Closed = ends;
                }
            }
        }

        private int Bracket(DateTime local, SessionState session)
        {
            var minutes = (local - session.Key).TotalMinutes;
            if (minutes < 0d) minutes = 0d;

            return (int)(minutes / Math.Max(1, BracketMinutes));
        }

        private void Rebuild(decimal tick)
        {
            var profile = _session.Builder.Build(tick, MaxProfileTicks);
            if (profile == null) return;

            ProfileMath.ComputeValueArea(profile, ValueSize, Basis);
            _session.Profile = profile;

            if (profile.PocIndex >= 0) _poc.Add(_session.Builder.End, profile.Poc);
        }

        private void Rotate(int bar, DateTime local, IndicatorCandle candle, decimal tick)
        {
            var atr = RhythmMath.AtrTicks(_high, _low, _close, _high.Count - 1, AtrBars, tick);
            if (atr <= 0m) atr = 1m;

            _swings.Add(bar, local, candle.High, candle.Low, tick, atr * SwingAtr);

            // Cumulative delta is sampled where a rotation turned, which is what a delta
            // divergence is comparing. Sampling every bar and looking for the extreme later
            // would find the delta extreme, not the delta at the price extreme.
            while (_swingsEmitted < _swings.Swings.Count)
            {
                var swing = _swings.Swings[_swingsEmitted];
                var index = Math.Min(swing.PivotBar, _cumDelta.Count - 1);

                _pivotFlow.Add(new PivotFlow
                {
                    IsHigh = swing.IsHigh,
                    Price = swing.Price,
                    CumulativeDelta = index >= 0 ? _cumDelta[index] : 0m,
                    Bar = swing.PivotBar
                });

                _swingsEmitted++;
            }

            if (_pivotFlow.Count > 400) _pivotFlow.RemoveRange(0, _pivotFlow.Count - 400);
        }

        private void Judge(int bar, DateTime local, IndicatorCandle candle, decimal tick)
        {
            var rhythm = RhythmMath.Measure(_swings.Swings, RhythmLegs, RhythmMinLegs);

            var minutes = rhythm.Valid ? rhythm.MedianMinutes * (double)AcceptTime : 0d;
            var ground = rhythm.Valid ? rhythm.MedianTicks * AcceptGround : 0m;

            var barMinutes = BarMinutes(bar, local);
            var poc = _session.Profile != null && _session.Profile.PocIndex >= 0 ? _session.Profile.Poc : 0m;

            _acceptance.Update(local, candle.Close, candle.High, candle.Low, barMinutes, tick, poc,
                               References(), minutes, ground, bar);
        }

        /// <summary>
        /// How long the bar covered. Taken from the gap to the previous bar rather than assumed,
        /// because this has to be right on tick and volume charts as well as minute ones -- and
        /// every acceptance threshold is denominated in minutes.
        /// </summary>
        private double BarMinutes(int bar, DateTime local)
        {
            if (bar <= 0) return 1d;

            var previous = GetCandle(bar - 1);
            if (previous == null) return 1d;

            var span = (local - _time.ToLocal(previous.Time)).TotalMinutes;

            // A session break is not bar time. Anything over an hour is the gap, not the bar.
            return span <= 0d || span > 60d ? 1d : span;
        }

        /// <summary>
        /// The levels acceptance is measured against. Only levels that STAND STILL: a
        /// developing value area high is a different price every bar, and a test opened on one
        /// would chase price and never resolve.
        /// </summary>
        private List<Reference> References()
        {
            var list = new List<Reference>();

            if (_prior?.Profile != null && _prior.Profile.PocIndex >= 0)
            {
                if (TestPriorValue)
                {
                    list.Add(new Reference("prior VAH", _prior.Profile.Vah, 1));
                    list.Add(new Reference("prior POC", _prior.Profile.Poc, 0));
                    list.Add(new Reference("prior VAL", _prior.Profile.Val, 1));
                }

                if (TestPriorRange)
                {
                    list.Add(new Reference("prior high", _prior.High, 2));
                    list.Add(new Reference("prior low", _prior.Low == decimal.MaxValue ? 0m : _prior.Low, 2));
                }
            }

            if (TestOvernight && _session != null && _session.OvernightLow != decimal.MaxValue)
            {
                list.Add(new Reference("overnight high", _session.OvernightHigh, 3));
                list.Add(new Reference("overnight low", _session.OvernightLow, 3));
            }

            if (TestIb && _session != null && _session.Ib.Set && _session.Ib.Closed != default)
            {
                list.Add(new Reference("IB high", _session.Ib.High, 4));
                list.Add(new Reference("IB low", _session.Ib.Low, 4));
            }

            return list;
        }

        #endregion

        #region Clock

        private bool ResolveTime()
        {
            if (_time != null && _time.Valid) return true;

            // Failing once on a chart that was still loading must not condemn the indicator for
            // the rest of the session, so it is retried as history arrives -- but not every
            // bar, because the halt scan walks the whole chart.
            if (_time != null && CurrentBar - _timeTriedAtBar < 200) return false;

            _timeTriedAtBar = CurrentBar;

            // 16, not 15. The CME maintenance break is 4-5 PM Central; 3 PM is the cash close
            // and trading carries straight on through it, so a resolver told to look for an
            // empty hour at 15 finds none and never resolves at all.
            _time = TimeContext.Create(ZoneId, Clock, new BarWindow(this), DateTime.UtcNow,
                                       ReadClock.HaltHour);

            return _time.Valid;
        }

        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansReadIndicator _owner;

            public BarWindow(OceansReadIndicator owner) { _owner = owner; }

            public int Count => _owner.CurrentBar;

            public DateTime Time(int bar) => _owner.GetCandle(bar)?.Time ?? default;
            public decimal Open(int bar) => _owner.GetCandle(bar)?.Open ?? 0m;
            public decimal High(int bar) => _owner.GetCandle(bar)?.High ?? 0m;
            public decimal Low(int bar) => _owner.GetCandle(bar)?.Low ?? 0m;
            public decimal Close(int bar) => _owner.GetCandle(bar)?.Close ?? 0m;
        }

        #endregion

        #region Assembling the read

        private ReadInputs Assemble(decimal tick)
        {
            var input = new ReadInputs { TickSize = tick };

            if (_time == null || !_time.Valid)
            {
                input.ClockError = _time?.Error ?? "Ocean Read: waiting for enough bars to settle the clock.";
                return input;
            }

            var last = GetCandle(CurrentBar);
            var local = _time.ToLocal(last?.Time ?? default);

            input.Now = local;
            input.ZoneAbbrev = _time.Abbrev(local);
            input.ClockHow = _time.Explain;
            input.Price = last?.Close ?? 0m;
            input.RegularHours = ReadClock.InRegularHours(local);
            input.PowerHour = ReadClock.InPowerHour(local);

            input.SessionLabel = _session == null
                               ? ""
                               : _session.TradeDate.ToString("ddd d MMM") +
                                 (input.PowerHour ? "  POWER HOUR" : input.RegularHours ? "  CASH" : "  OVERNIGHT");

            input.Session = _session?.Profile;
            input.Prior = _prior?.Profile;
            input.PriorComplete = _prior != null;

            input.Rhythm = RhythmMath.Measure(_swings.Swings, RhythmLegs, RhythmMinLegs);
            input.Leg = RhythmMath.ReadLeg(_swings, input.Rhythm, local, tick);

            input.Sequence = WaveMath.Character(_swings.Swings, SequenceLegs, OverlapShare);
            input.Wave = WaveMath.Recent(_swings.Swings, CountLookback, out var endIndex);
            input.Correction = WaveMath.Correction(_swings.Swings, input.Wave, endIndex);

            input.VwapText = _vwap.Describe(input.Price, tick * 2m, out var above, out var below, out var stack);
            input.VwapAbove = above;
            input.VwapBelow = below;
            input.VwapStack = stack;
            input.SessionVwap = _vwap[VwapAnchor.Session].Value;

            input.Acceptance = _acceptance.Current();
            input.LastAcceptance = _acceptance.Last();

            Auction(input, tick);
            Flow(input, tick, last);

            if (!_footprint)
                input.Notes.Add("no footprint on this chart -- the profile is built from bar closes, " +
                                "so value, single prints and absorption are all approximations");

            return input;
        }

        private void Auction(ReadInputs input, decimal tick)
        {
            var read = input.Auction;
            var session = _session;
            if (session == null) return;

            read.Ib = session.Ib;
            read.ExtensionUp = session.Ib.ExtensionUp(session.High);
            read.ExtensionDown = session.Ib.ExtensionDown(session.Low == decimal.MaxValue ? 0m : session.Low);

            var minBrackets = Math.Max(2, 120 / Math.Max(1, BracketMinutes));
            read.Shape = ProfileMath.Shape(session.Profile, Basis, minBrackets);

            read.DayType = AuctionMath.ClassifyDay(session.Ib, session.High,
                                                   session.Low == decimal.MaxValue ? 0m : session.Low,
                                                   read.Shape, 1.0m, 0.15m, out var dayWhy);
            read.DayWhy = dayWhy;

            read.ValueMigration = _poc.Measure(60d, tick);

            var haveOverlap = false;
            if (session.Profile != null && session.Profile.PocIndex >= 0 &&
                _prior?.Profile != null && _prior.Profile.PocIndex >= 0)
            {
                read.ValueOverlap = ProfileMath.Overlap(session.Profile.Val, session.Profile.Vah,
                                                        _prior.Profile.Val, _prior.Profile.Vah);

                read.Relation = AuctionMath.Relate(session.Profile.Val, session.Profile.Vah,
                                                   _prior.Profile.Val, _prior.Profile.Vah, tick * 4m);
                haveOverlap = true;
            }

            read.Efficiency = RhythmMath.Efficiency(_swings.Swings, SequenceLegs);
            var haveEfficiency = _swings.Swings.Count >= 2;

            read.Condition = AuctionMath.Judge(read.ValueOverlap, read.Efficiency, haveOverlap,
                                               haveEfficiency, 0.50m, 0.40m, out var why);
            read.ConditionWhy = why;

            if (session.Profile != null)
            {
                input.PoorHigh = ProfileMath.IsPoorHigh(session.Profile, PoorTpo, 2);
                input.PoorLow = ProfileMath.IsPoorLow(session.Profile, PoorTpo, 2);
                input.SinglePrintsOpen =
                    ProfileMath.FindSinglePrints(session.Profile, SinglePrintTicks, 1).Length > 0;
            }
        }

        private void Flow(ReadInputs input, decimal tick, IndicatorCandle last)
        {
            var flow = input.Flow;
            var session = _session;

            flow.HaveFootprint = _footprint;

            if (session != null)
            {
                flow.SessionDelta = session.Delta;
                flow.SessionVolume = session.Volume;

                if (_anyOi && session.OiOpen > 0m)
                {
                    flow.OiChange = session.OiLast - session.OiOpen;

                    var moved = session.Open <= 0m ? 0m : (input.Price - session.Open) / tick;
                    flow.Oi = OrderflowMath.ReadOpenInterest(flow.OiChange, moved, MinOi, 4m);
                }
            }

            flow.BarDelta = last?.Delta ?? 0m;

            if (input.Leg.Active && input.Leg.FromBar >= 0 && _cumDelta.Count > 0)
            {
                var from = Math.Min(input.Leg.FromBar, _cumDelta.Count - 1);
                flow.LegDelta = _cumDelta[_cumDelta.Count - 1] - _cumDelta[from];
            }

            var references = References();

            if (AcceptanceMath.Nearest(references, input.Price, LevelBand * 3, tick, out var nearest))
            {
                flow.LevelName = nearest.Name;
                flow.AtLevel = OrderflowMath.AtLevel(session?.Profile, nearest.Price, LevelBand);

                var ground = input.Leg.Active
                           ? (input.Price - nearest.Price) / tick
                           : 0m;

                flow.Absorption = OrderflowMath.IsAbsorption(flow.AtLevel, flow.SessionVolume, ground,
                                                             AbsorptionShare, (double)AbsorptionSided,
                                                             AbsorptionGround, out var absorbWhy);
                flow.AbsorptionWhy = absorbWhy;
            }

            if (input.Leg.Active)
            {
                flow.Divergence = OrderflowMath.Divergence(_pivotFlow, input.Leg.Up, out var divergenceWhy);
                flow.DivergenceWhy = divergenceWhy;
            }

            var direction = input.Leg.Active ? input.Leg.Up : flow.SessionDelta > 0m;
            flow.Verdict = OrderflowMath.Judge(flow, direction, MinLegDelta, out var flowWhy);
            flow.Why = flowWhy;
        }

        #endregion

        #region Render

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var container = ChartInfo?.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;
            if (region.Width <= 0 || region.Height <= 0) return;

            _layerErrors.Clear();

            var tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0m)
            {
                Banner(context, region, "Ocean Read: waiting for the instrument.");
                return;
            }

            if (CurrentBar < 2)
            {
                Banner(context, region, "Ocean Read: no bars loaded yet.");
                return;
            }

            ReadInputs input = null;
            try
            {
                input = Assemble(tick);
            }
            catch (Exception ex)
            {
                Banner(context, region, "Ocean Read: could not assemble the read -- " + ex.Message);
                return;
            }

            if (input.ClockError == null)
            {
                // Every layer is caught on its own and its failure is printed IN the box. A
                // layer that throws takes down every layer after it, silently -- including the
                // one thing that could report the problem. There is no debugger on this thread.
                Layer("prior levels", () => { if (DrawPrior) PriorLevels(context, container, region, tick); });
                Layer("developing value", () => { if (DrawDeveloping) Developing(context, container, region); });
                Layer("initial balance", () => { if (DrawIb) Ib(context, container, region); });
                Layer("overnight", () => { if (DrawOvernight) Overnight(context, container, region); });
                Layer("unfinished business", () => { if (DrawUnfinished) Unfinished(context, container, region, tick); });
                Layer("histogram", () => { if (DrawHistogram) Histogram(context, container, region); });
                Layer("pivots", () => { if (DrawPivots) Pivots(context, container, region); });
                Layer("wave count", () => { if (DrawWaves) Waves(context, container, region, input); });
                Layer("acceptance", () => { if (DrawAcceptance) Acceptance(context, container, region, input, tick); });
            }

            foreach (var error in _layerErrors) input.Notes.Add(error);

            if (!ShowBox) return;

            try
            {
                var box = ReadModel.Build(input);
                Box(context, region, box);
            }
            catch (Exception ex)
            {
                Banner(context, region, "Ocean Read: the read box failed -- " + ex.Message);
            }
        }

        private void Layer(string name, Action draw)
        {
            try
            {
                draw();
            }
            catch (Exception ex)
            {
                _layerErrors.Add(name + " failed: " + ex.Message);
            }
        }

        private void PriorLevels(RenderContext context, IChartContainer container, Rectangle region, decimal tick)
        {
            var profile = _prior?.Profile;
            if (profile == null || profile.PocIndex < 0) return;

            var from = _prior.LastBar >= 0 ? container.GetXByBar(_prior.LastBar, false) : region.Left;
            if (from < region.Left) from = region.Left;

            var tint = Color.FromArgb(255, 150, 155, 170);

            Ray(context, container, region, profile.Poc, from, region.Right, tint, false, "PD POC");
            Ray(context, container, region, profile.Vah, from, region.Right,
                Color.FromArgb(190, 150, 155, 170), true, "PD VAH");
            Ray(context, container, region, profile.Val, from, region.Right,
                Color.FromArgb(190, 150, 155, 170), true, "PD VAL");

            Ray(context, container, region, _prior.High, from, region.Right,
                Color.FromArgb(140, 120, 125, 140), true, "PD high");

            if (_prior.Low != decimal.MaxValue)
                Ray(context, container, region, _prior.Low, from, region.Right,
                    Color.FromArgb(140, 120, 125, 140), true, "PD low");
        }

        private void Developing(RenderContext context, IChartContainer container, Rectangle region)
        {
            var profile = _session?.Profile;
            if (profile == null || profile.PocIndex < 0) return;

            var from = _session.FirstBar >= 0 ? container.GetXByBar(_session.FirstBar, true) : region.Left;
            if (from < region.Left) from = region.Left;

            var tint = Color.FromArgb(255, 255, 205, 70);

            var top = container.GetYByPrice(profile.Vah, false);
            var bottom = container.GetYByPrice(profile.Val, false);

            if (bottom > top)
            {
                var height = bottom - top;
                context.FillRectangle(Color.FromArgb(18, tint.R, tint.G, tint.B),
                                      new Rectangle(from, top, Math.Max(1, region.Right - from), height));
            }

            Ray(context, container, region, profile.Poc, from, region.Right, tint, false, "POC");
            Ray(context, container, region, profile.Vah, from, region.Right,
                Color.FromArgb(200, tint.R, tint.G, tint.B), true, "VAH");
            Ray(context, container, region, profile.Val, from, region.Right,
                Color.FromArgb(200, tint.R, tint.G, tint.B), true, "VAL");
        }

        private void Ib(RenderContext context, IChartContainer container, Rectangle region)
        {
            var session = _session;
            if (session == null || !session.Ib.Set) return;

            var from = session.FirstBar >= 0 ? container.GetXByBar(session.FirstBar, true) : region.Left;
            var tint = Color.FromArgb(180, 120, 200, 255);

            Ray(context, container, region, session.Ib.High, from, region.Right, tint, true, "IB high");
            Ray(context, container, region, session.Ib.Low, from, region.Right, tint, true, "IB low");
        }

        private void Overnight(RenderContext context, IChartContainer container, Rectangle region)
        {
            var session = _session;
            if (session == null || session.OvernightLow == decimal.MaxValue) return;

            var from = session.FirstBar >= 0 ? container.GetXByBar(session.FirstBar, true) : region.Left;
            var tint = Color.FromArgb(160, 160, 130, 220);

            Ray(context, container, region, session.OvernightHigh, from, region.Right, tint, true, "ON high");
            Ray(context, container, region, session.OvernightLow, from, region.Right, tint, true, "ON low");
        }

        private void Unfinished(RenderContext context, IChartContainer container, Rectangle region, decimal tick)
        {
            var profile = _session?.Profile;
            if (profile == null) return;

            var from = _session.FirstBar >= 0 ? container.GetXByBar(_session.FirstBar, true) : region.Left;
            var prints = ProfileMath.FindSinglePrints(profile, SinglePrintTicks, 1);

            foreach (var print in prints)
            {
                var top = container.GetYByPrice(print.High, false);
                var bottom = container.GetYByPrice(print.Low, false);
                if (bottom <= top) continue;

                context.FillRectangle(Color.FromArgb(34, 255, 120, 120),
                                      new Rectangle(from, top, Math.Max(1, region.Right - from), bottom - top));
            }

            if (ProfileMath.IsPoorHigh(profile, PoorTpo, 2))
                Ray(context, container, region, profile.HighPrice, from, region.Right,
                    Color.FromArgb(220, 255, 110, 110), false, "poor high");

            if (ProfileMath.IsPoorLow(profile, PoorTpo, 2))
                Ray(context, container, region, profile.LowPrice, from, region.Right,
                    Color.FromArgb(220, 110, 220, 140), false, "poor low");
        }

        private void Histogram(RenderContext context, IChartContainer container, Rectangle region)
        {
            var profile = _session?.Profile;
            if (profile == null || profile.MaxVolume <= 0m) return;

            var width = Math.Max(40, region.Width / 8);
            var left = region.Right - width;
            var rowHeight = (int)Math.Max(1, Math.Round(container.PriceRowHeight));

            for (var i = 0; i < profile.Count; i++)
            {
                var weight = ProfileMath.Weight(profile.Levels[i], Basis);
                if (weight <= 0m) continue;

                var max = Basis == ProfileBasis.Volume ? profile.MaxVolume : MaxTpo(profile);
                if (max <= 0m) continue;

                var length = (int)Math.Round(width * (double)(weight / max));
                if (length < 1) length = 1;

                var y = container.GetYByPrice(profile.PriceAt(i), false) - rowHeight / 2;
                if (y < region.Top || y > region.Bottom) continue;

                var inValue = i >= profile.ValIndex && i <= profile.VahIndex;
                var fill = i == profile.PocIndex
                         ? Color.FromArgb(220, 255, 205, 70)
                         : inValue ? Color.FromArgb(120, 90, 180, 255) : Color.FromArgb(70, 110, 115, 130);

                context.FillRectangle(fill, new Rectangle(region.Right - length, y, length, rowHeight));
            }
        }

        private static decimal MaxTpo(ReadProfile profile)
        {
            var max = 0m;
            for (var i = 0; i < profile.Count; i++)
                if (profile.Levels[i].Tpo > max) max = profile.Levels[i].Tpo;

            return max;
        }

        private void Pivots(RenderContext context, IChartContainer container, Rectangle region)
        {
            var swings = _swings.Swings;
            var first = container.FirstVisibleBarNumber;

            for (var i = swings.Count - 1; i >= 0; i--)
            {
                var swing = swings[i];
                if (swing.PivotBar < first - 5) break;

                var x = container.GetXByBar(swing.PivotBar, false);
                var y = container.GetYByPrice(swing.Price, false);
                if (x < region.Left || x > region.Right) continue;

                var color = swing.IsHigh ? Color.FromArgb(220, 255, 130, 130)
                                         : Color.FromArgb(220, 130, 220, 160);

                context.FillRectangle(color, new Rectangle(x - 2, y - 2, 5, 5));

                if (!DrawTags) continue;

                var text = Format.Ticks(swing.Ticks) + " / " + Format.Minutes(swing.Minutes);
                context.DrawString(text, _tagFont, Color.FromArgb(150, 170, 175, 190),
                                   x + 4, swing.IsHigh ? y - 14 : y + 2);
            }
        }

        private void Waves(RenderContext context, IChartContainer container, Rectangle region, ReadInputs input)
        {
            var count = input.Wave;
            if (!count.Valid || count.Bars.Length < 6) return;

            var color = Color.FromArgb(240, 255, 205, 70);

            for (var i = 1; i < 6; i++)
            {
                var x = container.GetXByBar(count.Bars[i], false);
                var y = container.GetYByPrice(count.Points[i], false);
                if (x < region.Left || x > region.Right) continue;

                var up = count.Up == (i % 2 == 1);
                context.DrawString(i.ToString(), _boldFont, color, x - 3, up ? y - 18 : y + 4);
            }

            for (var i = 0; i < 5; i++)
            {
                var x1 = container.GetXByBar(count.Bars[i], false);
                var y1 = container.GetYByPrice(count.Points[i], false);
                var x2 = container.GetXByBar(count.Bars[i + 1], false);
                var y2 = container.GetYByPrice(count.Points[i + 1], false);

                context.DrawLine(new RenderPen(Color.FromArgb(120, 255, 205, 70), 1f, DashStyle.Dot),
                                 x1, y1, x2, y2);
            }
        }

        private void Acceptance(RenderContext context, IChartContainer container, Rectangle region,
                                ReadInputs input, decimal tick)
        {
            var test = input.Acceptance;
            if (test == null) return;

            var from = container.GetXByBar(test.BrokeBar, true);
            if (from < region.Left) from = region.Left;

            var y = container.GetYByPrice(test.Price, false);
            if (y < region.Top || y > region.Bottom) return;

            var target = test.Up ? test.Price + test.GroundNeeded * tick
                                 : test.Price - test.GroundNeeded * tick;

            var yTarget = container.GetYByPrice(target, false);
            var top = Math.Min(y, yTarget);
            var height = Math.Abs(yTarget - y);

            context.FillRectangle(Color.FromArgb(26, 255, 180, 60),
                                  new Rectangle(from, top, Math.Max(1, region.Right - from), Math.Max(1, height)));

            context.DrawLine(new RenderPen(Color.FromArgb(230, 255, 180, 60), LineThickness),
                             from, y, region.Right, y);

            context.DrawLine(new RenderPen(Color.FromArgb(150, 255, 180, 60), 1f, DashStyle.Dash),
                             from, yTarget, region.Right, yTarget);

            if (!DrawTags) return;

            var text = test.Name + "  time " + Format.Percent(test.TimeProgress) +
                       "  ground " + Format.Percent(test.GroundProgress);

            context.DrawString(text, _tagFont, Color.FromArgb(240, 255, 200, 120), from + 4, top - 13);
        }

        private void Ray(RenderContext context, IChartContainer container, Rectangle region,
                         decimal price, int from, int to, Color color, bool dashed, string tag)
        {
            if (price <= 0m) return;

            var y = container.GetYByPrice(price, false);
            if (y < region.Top || y > region.Bottom) return;

            var pen = dashed
                    ? new RenderPen(color, LineThickness, DashStyle.Dash)
                    : new RenderPen(color, LineThickness);

            context.DrawLine(pen, from, y, to, y);

            if (!DrawTags) return;

            var text = tag + " " + ChartInfo.GetPriceString(price);
            var size = context.MeasureString(text, _tagFont);

            // Against the end of the ray, where price is now, rather than back in the history.
            var x = to - size.Width - 4;
            if (x < from) x = from + 2;

            context.DrawString(text, _tagFont, color, x, y - size.Height - 1);
        }

        private void Box(RenderContext context, Rectangle region, ReadBox box)
        {
            var font = new RenderFont("Consolas", BoxTextSize);
            var bold = new RenderFont("Consolas", BoxTextSize, FontStyle.Bold);

            var labelWidth = 0;
            var textWidth = 0;
            var lineHeight = 0;

            foreach (var line in box.Lines)
            {
                var labelSize = context.MeasureString(line.Label.Length == 0 ? " " : line.Label, bold);
                var textSize = context.MeasureString(line.Text.Length == 0 ? " " : line.Text, font);

                if (labelSize.Width > labelWidth) labelWidth = labelSize.Width;
                if (textSize.Width > textWidth) textWidth = textSize.Width;
                if (textSize.Height > lineHeight) lineHeight = textSize.Height;
            }

            if (lineHeight <= 0) return;

            const int Pad = 8;
            const int Gap = 10;

            var width = Pad * 2 + labelWidth + Gap + textWidth;
            var height = Pad * 2 + lineHeight * box.Lines.Count;

            var x = Corner == BoxCorner.TopLeft || Corner == BoxCorner.BottomLeft
                  ? region.Left + 6
                  : region.Right - width - 6;

            var y = Corner == BoxCorner.TopLeft || Corner == BoxCorner.TopRight
                  ? region.Top + 6
                  : region.Bottom - height - 6;

            context.FillRectangle(Color.FromArgb(Math.Min(255, BoxOpacity), 12, 14, 18),
                                  new Rectangle(x, y, width, height));

            context.DrawRectangle(new RenderPen(Color.FromArgb(200, 60, 66, 80), 1f),
                                  new Rectangle(x, y, width, height));

            var cursor = y + Pad;

            foreach (var line in box.Lines)
            {
                var color = Paint(line.Tone);

                if (line.Label.Length > 0)
                    context.DrawString(line.Label, bold, color, x + Pad, cursor);

                if (line.Text.Length > 0)
                    context.DrawString(line.Text, font, color, x + Pad + labelWidth + Gap, cursor);

                cursor += lineHeight;
            }
        }

        private static Color Paint(Tone tone)
        {
            switch (tone)
            {
                case Tone.Header: return Color.FromArgb(255, 255, 255, 255);
                case Tone.Good: return Color.FromArgb(255, 110, 225, 150);
                case Tone.Bad: return Color.FromArgb(255, 255, 115, 115);
                case Tone.Warn: return Color.FromArgb(255, 255, 195, 90);
                case Tone.Muted: return Color.FromArgb(255, 140, 146, 160);
                default: return Color.FromArgb(255, 210, 214, 225);
            }
        }

        private void Banner(RenderContext context, Rectangle region, string text)
        {
            context.DrawString(text, _font, Color.FromArgb(255, 230, 170, 80),
                               region.Left + 10, region.Top + 10);
        }

        #endregion
    }
}
