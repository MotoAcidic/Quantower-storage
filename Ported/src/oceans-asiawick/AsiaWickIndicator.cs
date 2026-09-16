using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;
using Utils.Common.Logging;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansAsiaWick
{
    /// <summary>
    /// AsiaWick Levels -- Phase 1. Marks the three Asia-session top-wick signals, draws the two
    /// levels they are measured against, and alerts once per variant per night.
    ///
    /// This indicator only ever REPORTS. The execution half lives in AsiaWickStrategy and is
    /// gated behind a backtest; see README.md.
    ///
    /// Attach to a 15m or 30m chart (60m tolerated). The signal core is shared with the
    /// strategy -- see AsiaWickMath.cs -- so the two can never drift apart.
    /// </summary>
    [DisplayName("AsiaWick Levels")]
    [Category("Ocean")]
    public class AsiaWickIndicator : Indicator
    {
        /// <summary>Designed for 15m/30m. Above this the Asia window has too few bars to mean much.</summary>
        private const int MaxBarMinutes = 60;

        private readonly ValueDataSeries _pdhLine;
        private readonly ValueDataSeries _asiaLine;
        private readonly ValueDataSeries[] _marks = new ValueDataSeries[3];

        private readonly AsiaWickSettings _cfg = new AsiaWickSettings();

        private AsiaWickEngine _engine;
        private SessionClock _clock;
        private BarWindow _bars;

        private BarClock _requestedClock = BarClock.Auto;
        private string _zoneId = "Central Standard Time";

        private readonly LineSegmenter _pdhSeg = new LineSegmenter();
        private readonly LineSegmenter _asiaSeg = new LineSegmenter();

        private int _next;                 // next bar index still to fold
        private bool _ready;
        private bool _historyDone;
        private int _barMinutes;
        private string _status = "";
        private string _problem;

        // Survives recalculation on purpose: it is the last line of defence against a repeat
        // alert when ATAS rebuilds the series mid-session.
        private readonly HashSet<string> _alerted = new HashSet<string>();

        private DateTime _lastRollLogged = DateTime.MinValue;

        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        public AsiaWickIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;

            _pdhLine = new ValueDataSeries("AwPdh", "Prior day high")
            {
                VisualType = VisualMode.Line,
                // Deliberately not candle-red: a level drawn in the same red as the down bars
                // reads as price at a glance. Dashed for the same reason.
                Color = MColor.FromRgb(255, 138, 60),
                LineDashStyle = LineDashStyle.Dash,
                Width = 2,
                ShowZeroValue = false,
                ScaleIt = false,
                ShowCurrentValue = true,
                IgnoredByAlerts = true
            };

            _asiaLine = new ValueDataSeries("AwAsia", "Asia running high")
            {
                VisualType = VisualMode.Line,
                Color = MColor.FromRgb(130, 160, 180),
                Width = 1,
                ShowZeroValue = false,
                ScaleIt = false,
                ShowCurrentValue = true,
                IgnoredByAlerts = true
            };

            // Every variant is a SHORT. The old palette had wick-rejection in green, which
            // reads as a buy to anyone glancing at the chart, and PDH sweep in a red close
            // enough to the down candles to vanish into them. These three share no hue with
            // the candles and none of them says "long".
            _marks[0] = MakeMark("AwMk0", "PDH sweep", MColor.FromRgb(255, 45, 149));
            _marks[1] = MakeMark("AwMk1", "Asia-high sweep", MColor.FromRgb(255, 196, 0));
            _marks[2] = MakeMark("AwMk2", "Wick rejection", MColor.FromRgb(0, 229, 255));

            DataSeries[0] = _pdhLine;
            DataSeries.Add(_asiaLine);
            DataSeries.Add(_marks[0]);
            DataSeries.Add(_marks[1]);
            DataSeries.Add(_marks[2]);
        }

        private static ValueDataSeries MakeMark(string id, string name, MColor color)
        {
            return new ValueDataSeries(id, name)
            {
                VisualType = VisualMode.DownArrow,
                Color = color,
                Width = 3,
                ShowZeroValue = false,
                ScaleIt = false,
                ShowCurrentValue = false,
                IgnoredByAlerts = true
            };
        }

        // ------------------------------------------------------------------ 01 Sessions

        [Display(Name = "Time zone id", GroupName = "01 Sessions", Order = 100,
                 Description = "Windows time zone for every session window. Houston is " +
                               "'Central Standard Time' -- it handles DST on its own.")]
        public string ZoneId
        {
            get { return _zoneId; }
            set { _zoneId = value; Redo(); }
        }

        [Display(Name = "Bar clock", GroupName = "01 Sessions", Order = 110,
                 Description = "Auto works out whether bar stamps are UTC or already Central " +
                               "and shows the answer in the status line. It never guesses: if " +
                               "it cannot tell, nothing is drawn.")]
        public BarClock ClockMode
        {
            get { return _requestedClock; }
            set { _requestedClock = value; Redo(); }
        }

        [Display(Name = "Asia window start", GroupName = "01 Sessions", Order = 120)]
        public TimeSpan AsiaStart
        {
            get { return _cfg.AsiaStart; }
            set { _cfg.AsiaStart = value; Redo(); }
        }

        [Display(Name = "Asia window end", GroupName = "01 Sessions", Order = 130,
                 Description = "Crosses midnight; the whole window belongs to one trade date.")]
        public TimeSpan AsiaEnd
        {
            get { return _cfg.AsiaEnd; }
            set { _cfg.AsiaEnd = value; Redo(); }
        }

        [Display(Name = "RTH start (prior day high)", GroupName = "01 Sessions", Order = 140)]
        public TimeSpan RthStart
        {
            get { return _cfg.RthStart; }
            set { _cfg.RthStart = value; Redo(); }
        }

        [Display(Name = "RTH end (prior day high)", GroupName = "01 Sessions", Order = 150)]
        public TimeSpan RthEnd
        {
            get { return _cfg.RthEnd; }
            set { _cfg.RthEnd = value; Redo(); }
        }

        [Display(Name = "Morning exit", GroupName = "01 Sessions", Order = 160,
                 Description = "How far past the Asia close the levels stay drawn. The strategy " +
                               "flattens here.")]
        public ExitTime Exit
        {
            get { return _cfg.Exit; }
            set { _cfg.Exit = value; Redo(); }
        }

        // ------------------------------------------------------------------ 02 Signals

        [Display(Name = "PDH sweep", GroupName = "02 Signals", Order = 200,
                 Description = "Traded through the prior RTH high and closed back under it.")]
        public bool EnablePdhSweep
        {
            get { return _cfg.EnablePdhSweep; }
            set { _cfg.EnablePdhSweep = value; Redo(); }
        }

        [Display(Name = "Asia-high sweep", GroupName = "02 Signals", Order = 210,
                 Description = "Traded through the running Asia high and closed back under it.")]
        public bool EnableAsiaHighSweep
        {
            get { return _cfg.EnableAsiaHighSweep; }
            set { _cfg.EnableAsiaHighSweep = value; Redo(); }
        }

        [Display(Name = "Wick rejection", GroupName = "02 Signals", Order = 220,
                 Description = "New Asia high rejected by a top-heavy wick closing in the lower half.")]
        public bool EnableWickRejection
        {
            get { return _cfg.EnableWickRejection; }
            set { _cfg.EnableWickRejection = value; Redo(); }
        }

        [Display(Name = "Wick % of range", GroupName = "02 Signals", Order = 230,
                 Description = "Upper wick as a fraction of the bar range. 0.50 = half.")]
        [Range(0.01, 1.0)]
        public decimal WickPct
        {
            get { return _cfg.WickPct; }
            set { _cfg.WickPct = value; Redo(); }
        }

        [Display(Name = "Status line", GroupName = "02 Signals", Order = 240)]
        public bool ShowStatus { get; set; } = true;

        // ------------------------------------------------------------------ 03 Alerts

        [Display(Name = "Alert on signal", GroupName = "03 Alerts", Order = 300,
                 Description = "One alert per variant per night, real-time bars only.")]
        public bool AlertOnSignal { get; set; } = true;

        [Display(Name = "Sound file", GroupName = "03 Alerts", Order = 310)]
        public string AlertSound { get; set; } = "alert1";

        [Display(Name = "Alert background", GroupName = "03 Alerts", Order = 320)]
        public MColor AlertBackground { get; set; } = MColor.FromRgb(30, 30, 30);

        [Display(Name = "Alert foreground", GroupName = "03 Alerts", Order = 330)]
        public MColor AlertForeground { get; set; } = MColor.FromRgb(255, 255, 255);

        // ------------------------------------------------------------------ lifecycle

        /// <summary>A setting changed: throw the pass away and rebuild from bar 0.</summary>
        private void Redo()
        {
            _ready = false;
            _next = 0;
            RecalculateValues();
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // ATAS restarts every pass at bar 0. That is the only place state is rebuilt, which
            // is what makes a recalculation reproduce the previous pass exactly.
            if (bar == 0)
            {
                Setup();
                return;
            }

            if (!_ready) return;

            // Only bars strictly before the forming one are closed. Nothing here is intrabar.
            while (_next <= bar - 1)
            {
                FoldOne(_next);
                _next++;
            }
        }

        protected override void OnFinishRecalculate()
        {
            // Everything up to here was history. Alerts are live-only from now on.
            _historyDone = true;
        }

        private void Setup()
        {
            _problem = null;
            _historyDone = false;
            _next = 0;

            _pdhSeg.Reset();
            _asiaSeg.Reset();

            _pdhLine.Clear();
            _asiaLine.Clear();
            for (var i = 0; i < 3; i++) _marks[i].Clear();

            _bars = new BarWindow(this);

            _barMinutes = AsiaWickUtil.BarMinutes(_bars);

            if (_barMinutes > MaxBarMinutes)
            {
                _problem = "AsiaWick: " + _barMinutes + "m chart. Designed for 15m-30m " +
                           "(60m tolerated); nothing is drawn above that.";
                this.LogWarn(_problem);
                _ready = false;
                return;
            }

            _clock = SessionClock.Create(_zoneId, _requestedClock, _bars,
                                         DateTime.UtcNow, SessionClock.MnqHaltHourCt);

            if (!_clock.Valid)
            {
                // A wrong offset does not look wrong -- it draws a clean, plausible, misplaced
                // level. So we draw nothing and say why.
                _problem = _clock.Error;
                this.LogWarn(_problem);
                _ready = false;
                return;
            }

            _engine = new AsiaWickEngine(_cfg, _clock);
            _engine.Reset();
            _ready = true;
        }

        private void FoldOne(int bar)
        {
            var c = GetCandle(bar);
            var fresh = new List<WickSignal>();

            var st = _engine.Fold(bar, c.Time, c.Open, c.High, c.Low, c.Close, fresh);

            // --- levels ---
            Apply(_pdhLine, bar, _pdhSeg.Step(bar, st.InDisplayWindow ? st.PdhLevel : 0m));
            Apply(_asiaLine, bar, _asiaSeg.Step(bar, st.InAsia ? st.AsiaLevel : 0m));

            // --- markers ---
            foreach (var sig in fresh)
                _marks[(int)sig.Variant][bar] = sig.StopReference;

            // --- contract roll: levels are price-based, so carry on but leave a trace ---
            if (st.TradeDate != _lastRollLogged && IsRollWeek(st.TradeDate) && fresh.Count > 0)
            {
                _lastRollLogged = st.TradeDate;
                this.LogInfo("AsiaWick: signal on a roll-week night ({0:yyyy-MM-dd}). Levels are " +
                             "price-based -- check the PDH against the contract you are on.",
                             st.TradeDate);
            }

            // --- alerts ---
            if (fresh.Count > 0) Announce(bar, fresh);

            _status = BuildStatus(st);
        }

        /// <summary>
        /// Executes one LineSegmenter instruction. All the judgement lives in the segmenter,
        /// which is ATAS-free and tested; this only turns it into series calls.
        /// </summary>
        private static void Apply(ValueDataSeries series, int bar, SegmentStep step)
        {
            if (step.BreakBefore) series.SetPointOfEndLine(bar - 1);
            if (step.HasValue) series[bar] = step.Value;
        }

        /// <summary>
        /// CME equity index rolls the Thursday before the third Friday of Mar/Jun/Sep/Dec. Only
        /// used to annotate the log -- nothing about the signal changes.
        /// </summary>
        private static bool IsRollWeek(DateTime tradeDate)
        {
            var m = tradeDate.Month;
            if (m != 3 && m != 6 && m != 9 && m != 12) return false;

            var third = new DateTime(tradeDate.Year, m, 1);
            var fridays = 0;

            while (fridays < 3)
            {
                if (third.DayOfWeek == DayOfWeek.Friday) fridays++;
                if (fridays < 3) third = third.AddDays(1);
            }

            var roll = third.AddDays(-1);          // the Thursday before
            return Math.Abs((tradeDate - roll).TotalDays) <= 3;
        }

        private void Announce(int bar, List<WickSignal> fresh)
        {
            if (!AlertOnSignal) return;

            // Two independent guards. Historical passes never alert, and even if one slipped
            // through, a key only ever fires once for the life of this indicator instance.
            if (!_historyDone) return;
            if (bar < CurrentBar - 2) return;

            foreach (var sig in fresh)
            {
                if (!_alerted.Add(sig.Key)) continue;

                var text = string.Format(CultureInfo.InvariantCulture,
                    "{0}  AsiaWick {1}  signal {2}  stop-ref {3}  ({4:HH:mm} CT)",
                    InstrumentName, Describe(sig.Variant), sig.SignalPrice,
                    sig.StopReference, sig.TimeCt);

                AddAlert(AlertSound, InstrumentName, text, AlertBackground, AlertForeground);
            }
        }

        private static string Describe(SignalVariant v)
        {
            switch (v)
            {
                case SignalVariant.PdhSweep: return "PDH sweep";
                case SignalVariant.AsiaHighSweep: return "Asia-high sweep";
                default: return "wick rejection";
            }
        }

        private string InstrumentName
        {
            get
            {
                var n = InstrumentInfo == null ? null : InstrumentInfo.Instrument;
                return string.IsNullOrEmpty(n) ? "?" : n;
            }
        }

        private string BuildStatus(BarState st)
        {
            var armed = _engine.Armed ? "armed" : "spent";
            var pdh = _engine.Pdh.HasValue ? _engine.Pdh.Value.ToString(CultureInfo.InvariantCulture) : "--";
            var asia = _engine.AsiaHigh.HasValue ? _engine.AsiaHigh.Value.ToString(CultureInfo.InvariantCulture) : "--";

            return string.Format(CultureInfo.InvariantCulture,
                "AsiaWick  {0:yyyy-MM-dd} {1}  |  PDH {2}  Asia {3}  |  {4}  |  {5}m bars, clock {6} ({7})",
                st.TradeDate, armed, pdh, asia,
                st.InAsia ? "in Asia" : (st.InDisplayWindow ? "post-Asia" : "outside"),
                _barMinutes, _clock.Clock, _clock.Explain);
        }

        // ------------------------------------------------------------------ render

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            // Each layer is caught on its own. A layer that throws otherwise takes down every
            // layer after it silently -- including the readout that would report the problem.
            try
            {
                if (_problem != null)
                {
                    context.DrawString(_problem, _statusFont, Color.OrangeRed,
                                       ChartArea.Left + 6, ChartArea.Top + 6);
                    return;
                }

                if (ShowStatus && _ready && !string.IsNullOrEmpty(_status))
                    context.DrawString(_status, _statusFont, Color.Gainsboro,
                                       ChartArea.Left + 6, ChartArea.Top + 6);
            }
            catch (Exception ex)
            {
                try
                {
                    context.DrawString("AsiaWick render: " + ex.Message, _statusFont,
                                       Color.OrangeRed, ChartArea.Left + 6, ChartArea.Top + 6);
                }
                catch { }
            }
        }

        /// <summary>Adapts the chart to the ATAS-free interface the clock and core are built on.</summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly AsiaWickIndicator _o;

            public BarWindow(AsiaWickIndicator o) { _o = o; }

            public int Count { get { return _o.CurrentBar; } }
            public DateTime Time(int b) { return _o.GetCandle(b).Time; }
            public decimal Open(int b) { return _o.GetCandle(b).Open; }
            public decimal High(int b) { return _o.GetCandle(b).High; }
            public decimal Low(int b) { return _o.GetCandle(b).Low; }
            public decimal Close(int b) { return _o.GetCandle(b).Close; }
        }
    }
}
