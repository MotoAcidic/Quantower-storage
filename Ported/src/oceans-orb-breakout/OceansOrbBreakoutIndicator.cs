using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using ATAS.Indicators.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansOrbBreakout
{
    /// <summary>
    /// Ocean ORB Breakout -- the opening range of the regular session, and the bars that leave
    /// it with volume and the VWAP behind them.
    ///
    /// A signal needs all four of these on ONE closed bar:
    ///   1. close above the opening range high
    ///   2. bar volume at or above a multiple of the average opening range bar volume
    ///   3. close above the session VWAP
    ///   4. inside the signal window, which ends at the cutoff time
    ///
    /// EVERY time in this indicator is Houston time. There is no second time zone to convert
    /// from, and none of the settings are in exchange time. The defaults are the Houston
    /// readings of the usual New York numbers: the 8:30 open is the 9:30 cash open, the range
    /// closes 9:00, and the 10:30 cutoff is two hours after the open.
    ///
    /// Works on any time-based chart whose bars divide the range -- 1, 5 and 15 minute all do.
    /// When they do not, the readout says so rather than drawing a level off straddling bars.
    /// </summary>
    [DisplayName("Ocean ORB Breakout")]
    [Category("Ocean")]
    public class OceansOrbBreakoutIndicator : Indicator
    {
        private readonly object _sync = new object();
        private OrbModel _model;
        private TimeContext _time;
        private readonly BarWindow _bars;
        private volatile bool _dirty = true;
        private int _builtBarCount = -1;
        private string _configKey;

        private readonly HashSet<string> _alerted = new HashSet<string>();
        private readonly HashSet<string> _labelled = new HashSet<string>();
        private string _labelError;

        private readonly RenderFont _labelFont = new RenderFont("Arial", 10f);
        private readonly RenderFont _smallFont = new RenderFont("Arial", 8.5f);
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        public OceansOrbBreakoutIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = false;

            if (DataSeries.Count > 0 && DataSeries[0] is ValueDataSeries v)
            {
                v.VisualType = VisualMode.Hide;
                v.IsHidden = true;
            }

            _bars = new BarWindow(this);
        }

        #region Settings -- 01 Opening range

        [Display(Name = "Range length (minutes)", GroupName = "01 Opening range", Order = 100,
                 Description = "Measured from the session open. 30 is the first half hour.")]
        public int OpeningRangeMinutes { get; set; } = 30;

        [Display(Name = "High line", GroupName = "01 Opening range", Order = 110)]
        public PenSettings OrHighPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(0, 190, 90),
            Width = 2,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "Show the low too", GroupName = "01 Opening range", Order = 120,
                 Description = "The signal only uses the high; the low is here to see against.")]
        public bool OrShowLow { get; set; } = false;

        [Display(Name = "Low line", GroupName = "01 Opening range", Order = 130)]
        public PenSettings OrLowPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(220, 60, 60),
            Width = 1,
            LineDashStyle = LineDashStyle.Dash
        };

        [Display(Name = "Carry lines to the session close", GroupName = "01 Opening range", Order = 140,
                 Description = "Off stops them at the cutoff, where the signal window ends.")]
        public bool OrExtendToClose { get; set; } = true;

        [Display(Name = "Shade the range", GroupName = "01 Opening range", Order = 150)]
        public bool OrShade { get; set; } = true;

        [Display(Name = "Shade colour", GroupName = "01 Opening range", Order = 160)]
        public MColor OrShadeColor { get; set; } = MColor.FromRgb(120, 140, 180);

        [Display(Name = "Shade transparency (%)", GroupName = "01 Opening range", Order = 170)]
        public int OrShadeTransparency { get; set; } = 88;

        [Display(Name = "Show line labels", GroupName = "01 Opening range", Order = 180)]
        public bool OrLabels { get; set; } = true;

        #endregion

        #region Settings -- 02 Signal

        [Display(Name = "Volume multiple", GroupName = "02 Signal", Order = 200,
                 Description = "Bar volume must be at least this many times the average volume " +
                               "of one opening range bar. 1.5 is the default.")]
        public decimal VolumeMultiple { get; set; } = 1.5m;

        [Display(Name = "Window ends at (Houston)", GroupName = "02 Signal", Order = 210,
                 Description = "No signal prints at or after this time. 10:30 AM Houston is two " +
                               "hours after the 8:30 open.")]
        public TimeSpan CutoffCt { get; set; } = new TimeSpan(10, 30, 0);

        [Display(Name = "Require close above VWAP", GroupName = "02 Signal", Order = 220)]
        public bool RequireAboveVwap { get; set; } = true;

        [Display(Name = "First signal of the day only", GroupName = "02 Signal", Order = 230,
                 Description = "Off marks every bar in the window that meets all conditions.")]
        public bool FirstSignalOnly { get; set; } = true;

        [Display(Name = "Confirm on bar close", GroupName = "02 Signal", Order = 240,
                 Description = "On ignores the forming bar, whose volume is only partial. Off " +
                               "makes marks and alerts come and go inside the bar.")]
        public bool ConfirmOnClose { get; set; } = true;

        [Display(Name = "Marker colour", GroupName = "02 Signal", Order = 250)]
        public MColor SignalColor { get; set; } = MColor.FromRgb(255, 200, 40);

        [Display(Name = "Show the label", GroupName = "02 Signal", Order = 260,
                 Description = "Text on the chart at the signal bar, with the volume multiple.")]
        public bool SignalLabel { get; set; } = true;

        [Display(Name = "Label size", GroupName = "02 Signal", Order = 270)]
        public int SignalLabelSize { get; set; } = 11;

        #endregion

        #region Settings -- 03 VWAP

        [Display(Name = "Draw the VWAP", GroupName = "03 VWAP", Order = 300)]
        public bool VwapShow { get; set; } = true;

        [Display(Name = "Anchored at", GroupName = "03 VWAP", Order = 310,
                 Description = "Session open is the 8:30 Houston open. Overnight open is the " +
                               "5:00 PM reopen the evening before.")]
        public VwapAnchor VwapAnchoredAt { get; set; } = VwapAnchor.SessionOpen;

        [Display(Name = "VWAP line", GroupName = "03 VWAP", Order = 320)]
        public PenSettings VwapPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(230, 160, 255),
            Width = 1,
            LineDashStyle = LineDashStyle.Solid
        };

        #endregion

        #region Settings -- 04 Alerts

        [Display(Name = "Alert on the signal", GroupName = "04 Alerts", Order = 400,
                 Description = "Fires once per signal, and only at the live edge. Loading " +
                               "history stays silent.")]
        public bool AlertOnSignal { get; set; } = false;

        [Display(Name = "Alert sound", GroupName = "04 Alerts", Order = 410,
                 Description = "Name of a sound file in the ATAS alerts folder.")]
        public string AlertSound { get; set; } = "alert1";

        #endregion

        #region Settings -- 05 Clock

        [Display(Name = "Your time zone", GroupName = "05 Clock", Order = 500,
                 Description = "Every time in this indicator is in this zone. Houston is " +
                               "'Central Standard Time' -- it covers daylight time too.")]
        public string TimeZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "05 Clock", Order = 510,
                 Description = "Whether ATAS stamps bars in UTC or already in your zone. Auto " +
                               "works it out and prints the answer in the readout; if it cannot, " +
                               "nothing is drawn.")]
        public BarClock BarTimes { get; set; } = BarClock.Auto;

        [Display(Name = "Daily halt hour", GroupName = "05 Clock", Order = 520,
                 Description = "The hour with no trading, used to check the bar clock. " +
                               "16 = the 4-5 PM Houston MNQ halt published by CME. This is " +
                               "NOT the 3 PM cash close.")]
        public int HaltHourCt { get; set; } = 16;

        [Display(Name = "Session opens (Houston)", GroupName = "05 Clock", Order = 530,
                 Description = "8:30 AM Houston is the cash open. The opening range starts here.")]
        public TimeSpan SessionOpenCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "Session closes (Houston)", GroupName = "05 Clock", Order = 540)]
        public TimeSpan SessionCloseCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(Name = "Overnight reopen (Houston)", GroupName = "05 Clock", Order = 550,
                 Description = "5:00 PM Houston. Also where one trading day ends and the next " +
                               "begins, so the evening belongs to the following day.")]
        public TimeSpan OvernightOpenCt { get; set; } = new TimeSpan(17, 0, 0);

        [Display(Name = "Show status readout", GroupName = "05 Clock", Order = 560,
                 Description = "Top-left line with the last bar's time. Check it against your " +
                               "own clock the first time you attach this.")]
        public bool ShowStatus { get; set; } = true;

        #endregion

        #region Settings -- 06 Display

        [Display(Name = "Days to show", GroupName = "06 Display", Order = 600)]
        public int DaysToShow { get; set; } = 2;

        [Display(Name = "Draw above candles", GroupName = "06 Display", Order = 610,
                 Description = "Off puts the shading and levels behind the candles.")]
        public bool DrawAboveCandles { get; set; } = false;

        #endregion

        #region Calculation

        protected override void OnCalculate(int bar, decimal value)
        {
            // Nothing is computed per bar; the model is rebuilt from a full scan when the data
            // actually changes. Panning and zooming never trigger a rebuild.
            if (bar != CurrentBar - 1) return;
            _dirty = true;
        }

        protected override void OnFinishRecalculate() => _dirty = true;

        protected override void OnRecalculate()
        {
            lock (_sync)
            {
                _alerted.Clear();
                _labelled.Clear();
                _labelError = null;
                _model = null;
                _builtBarCount = -1;
            }
            _dirty = true;
        }

        /// <summary>
        /// Fingerprint of every setting the MODEL depends on. Editing the volume multiple has
        /// to rebuild; editing a colour must not. ATAS does not reliably recalculate on a plain
        /// property edit, so the change is detected here rather than waited for.
        /// </summary>
        private string ConfigKey() => string.Join("|",
            TimeZoneId, (int)BarTimes, HaltHourCt,
            SessionOpenCt, SessionCloseCt, OvernightOpenCt,
            OpeningRangeMinutes, CutoffCt, VolumeMultiple,
            RequireAboveVwap, FirstSignalOnly, ConfirmOnClose,
            (int)VwapAnchoredAt, DaysToShow);

        private OrbConfig BuildConfig(TimeContext time) => new OrbConfig
        {
            Time = time,
            SessionOpen = SessionOpenCt,
            SessionClose = SessionCloseCt,
            OvernightOpen = OvernightOpenCt,
            OpeningRangeMinutes = Math.Max(1, OpeningRangeMinutes),
            Cutoff = CutoffCt,
            VolumeMultiple = VolumeMultiple,
            RequireAboveVwap = RequireAboveVwap,
            FirstSignalOnly = FirstSignalOnly,
            ConfirmOnClose = ConfirmOnClose,
            Anchor = VwapAnchoredAt,
            Days = Math.Max(1, DaysToShow)
        };

        private void Rebuild()
        {
            lock (_sync)
            {
                _configKey = ConfigKey();

                var nowUtc = UtcTime.Year > 2000 ? UtcTime : DateTime.UtcNow;

                _time = TimeContext.Create(TimeZoneId, BarTimes, _bars, nowUtc, HaltHourCt);
                _model = _time.Valid ? OrbModel.Build(_bars, BuildConfig(_time)) : null;
                _builtBarCount = CurrentBar;
                _dirty = false;

                if (_model == null) return;

                if (SignalLabel) PostLabels();
                if (AlertOnSignal) RaiseAlerts();
            }
        }

        private static string Key(DayOrb day, OrbSignal s) =>
            day.Day.ToString("yyyy-MM-dd") + "#" + s.Bar;

        private void RaiseAlerts()
        {
            foreach (var day in _model.Days)
            {
                foreach (var s in day.Signals)
                {
                    var key = Key(day, s);
                    if (!_alerted.Add(key)) continue;

                    // Only the live edge alerts. Replaying history must stay silent.
                    if (s.Bar < CurrentBar - 2) continue;

                    AddAlert(AlertSound, "ORB break " + ChartInfo.GetPriceString(s.Close) +
                                         " on " + Ratio(s.VolumeRatio) + " volume");
                }
            }
        }

        /// <summary>
        /// The chart-object label at the signal bar. Posted through the GUI thread because the
        /// model is rebuilt off it, and wrapped so a platform refusal shows up in the readout
        /// instead of taking the whole indicator down -- the marker still draws either way.
        /// </summary>
        private void PostLabels()
        {
            var text = Color.FromArgb(20, 20, 20);
            var fill = Conv(SignalColor);
            var size = Math.Max(6, SignalLabelSize);

            foreach (var day in _model.Days)
            {
                foreach (var s in day.Signals)
                {
                    var key = Key(day, s);
                    if (!_labelled.Add(key)) continue;

                    var bar = s.Bar;
                    var price = s.Close;
                    var caption = "ORB " + Ratio(s.VolumeRatio) + " vol";

                    DoActionInGuiThread(() =>
                    {
                        try
                        {
                            AddText(key, caption, true, bar, price, text, fill, fill,
                                    size, DrawingText.TextAlign.Center, true);
                        }
                        catch (Exception ex)
                        {
                            _labelError = "label refused: " + ex.Message;
                        }
                    });
                }
            }
        }

        private static string Ratio(decimal r) => Math.Round(r, 2).ToString("0.##") + "x";

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            if (chart?.PriceChartContainer == null) return;

            if (DrawAbovePrice != DrawAboveCandles) DrawAbovePrice = DrawAboveCandles;

            if (_dirty || _builtBarCount != CurrentBar || _configKey != ConfigKey())
                Rebuild();

            var region = chart.PriceChartContainer.Region;

            if (_time == null || !_time.Valid)
            {
                DrawStatus(context, region, _time?.Error ?? "Ocean ORB Breakout: starting up.",
                           Color.FromArgb(255, 110, 110));
                return;
            }

            var model = _model;
            if (model == null) return;

            // Every layer is caught on its own. A layer that throws otherwise takes down every
            // layer after it, silently -- including the readout, which is the one thing that
            // could have said something was wrong. There is no debugger on the render thread.
            var errors = new List<string>();

            var take = Math.Max(1, DaysToShow);
            var shown = 0;

            for (var i = model.Days.Count - 1; i >= 0 && shown < take; i--)
            {
                var day = model.Days[i];
                if (!day.HasRange) continue;
                shown++;

                var d = day;
                Layer(errors, "vwap", () => RenderVwap(context, region, d), VwapShow);
                Layer(errors, "range", () => RenderRange(context, region, d), true);
                Layer(errors, "signals", () => RenderSignals(context, region, d), true);
            }

            if (!ShowStatus)
            {
                // With the readout switched off a failure would leave no trace at all, so it
                // gets printed anyway.
                if (errors.Count > 0)
                    DrawStatus(context, region, "Ocean ORB Breakout: " + string.Join("  ·  ", errors),
                               Color.FromArgb(255, 110, 110));
                return;
            }

            try
            {
                var text = StatusText(model);
                var color = StatusColor(model);

                if (errors.Count > 0)
                {
                    text += "  ·  " + string.Join("  ·  ", errors);
                    color = Color.FromArgb(255, 110, 110);
                }

                DrawStatus(context, region, text, color);
            }
            catch (Exception ex)
            {
                // The readout is the last thing standing, so its own failure is reported as
                // plainly as possible rather than being allowed to disappear.
                DrawStatus(context, region,
                           "Ocean ORB Breakout: the readout failed -- " +
                           ex.GetType().Name + ": " + ex.Message,
                           Color.FromArgb(255, 110, 110));
            }
        }

        private static void Layer(List<string> errors, string name, Action draw, bool wanted)
        {
            if (!wanted) return;

            try
            {
                draw();
            }
            catch (Exception ex)
            {
                var message = name + " layer failed -- " + ex.GetType().Name + ": " + ex.Message;
                if (!errors.Contains(message)) errors.Add(message);
            }
        }

        /// <summary>The opening range: its shading, its high, and its low when asked for.</summary>
        private void RenderRange(RenderContext context, Rectangle region, DayOrb day)
        {
            var x1 = XStart(day.OrStartBar);
            var yHigh = Y(day.OrHigh);
            var yLow = Y(day.OrLow);

            if (OrShade)
            {
                var boxRight = XEnd(day.OrEndBar);
                if (boxRight >= region.Left && x1 <= region.Right)
                {
                    var shade = Rectangle.FromLTRB(Math.Max(x1, region.Left), Math.Min(yHigh, yLow),
                                                   Math.Min(boxRight, region.Right), Math.Max(yHigh, yLow));
                    if (shade.Width > 0 && shade.Height > 0)
                        context.FillRectangle(Conv(OrShadeColor, OrShadeTransparency), shade);
                }
            }

            var endBar = OrExtendToClose
                ? (day.SessionEndBar >= 0 ? day.SessionEndBar : day.LastBar)
                : (day.WindowEndBar >= 0 ? day.WindowEndBar : day.OrEndBar);

            // A day still in progress runs to the right edge either way.
            var x2 = day.LastBar >= CurrentBar - 1 ? region.Right : XEnd(endBar);

            var lx1 = Math.Max(x1, region.Left);
            var lx2 = Math.Min(x2, region.Right);
            if (lx2 <= lx1) return;

            var highPen = new RenderPen(Conv(OrHighPen.Color), Math.Max(1, OrHighPen.Width),
                                        ToDash(OrHighPen.LineDashStyle));

            DrawLevel(context, region, highPen, lx1, lx2, yHigh,
                      "OR high " + ChartInfo.GetPriceString(day.OrHigh), Conv(OrHighPen.Color));

            if (!OrShowLow) return;

            var lowPen = new RenderPen(Conv(OrLowPen.Color), Math.Max(1, OrLowPen.Width),
                                       ToDash(OrLowPen.LineDashStyle));

            DrawLevel(context, region, lowPen, lx1, lx2, yLow,
                      "OR low " + ChartInfo.GetPriceString(day.OrLow), Conv(OrLowPen.Color));
        }

        private void DrawLevel(RenderContext context, Rectangle region, RenderPen pen,
                               int x1, int x2, int y, string label, Color color)
        {
            if (y < region.Top || y > region.Bottom) return;

            context.DrawLine(pen, x1, y, x2, y);

            if (OrLabels) DrawLabel(context, _smallFont, label, color, x2, y, region);
        }

        private void RenderVwap(RenderContext context, Rectangle region, DayOrb day)
        {
            if (!day.HasVwap) return;

            var pen = new RenderPen(Conv(VwapPen.Color), Math.Max(1, VwapPen.Width),
                                    ToDash(VwapPen.LineDashStyle));

            var half = (int)(ChartInfo.PriceChartContainer.BarsWidth / 2);
            var have = false;
            var px = 0;
            var py = 0;

            for (var k = 0; k < day.Vwap.Count; k++)
            {
                var bar = day.VwapStartBar + k;
                var x = XStart(bar) + half;

                if (x < region.Left - 50 || x > region.Right + 50) { have = false; continue; }

                var y = Y(day.Vwap[k]);

                if (have) context.DrawLine(pen, px, py, x, y);

                px = x;
                py = y;
                have = true;
            }
        }

        private void RenderSignals(RenderContext context, Rectangle region, DayOrb day)
        {
            var color = Conv(SignalColor);
            var width = Math.Max(4, (int)ChartInfo.PriceChartContainer.BarsWidth);
            var half = width / 2;

            foreach (var s in day.Signals)
            {
                var x = XStart(s.Bar) + half;
                if (x < region.Left || x > region.Right) continue;

                // Arrow under the bar that fired, pointing the way it broke.
                var y = Y(GetCandle(s.Bar).Low) + 6;
                if (y < region.Top || y > region.Bottom) continue;

                var tip = new Point(x, y);
                var left = new Point(x - half, y + width);
                var right = new Point(x + half, y + width);

                context.FillPolygon(color, new[] { tip, left, right });
            }
        }

        private Color StatusColor(OrbModel model)
        {
            var warn = _labelError != null;

            foreach (var day in model.Days)
            {
                if (!day.HasRange) continue;
                if (!day.Aligned || day.ThinRange || day.OrAverageVolume <= 0m) warn = true;
                if (RequireAboveVwap && !day.HasVwap) warn = true;
            }

            return warn ? Color.FromArgb(255, 190, 90) : Color.FromArgb(150, 160, 175);
        }

        /// <summary>
        /// The readout. It carries the clock check, and then anything that would make a drawn
        /// level quietly wrong: bars that do not divide the range, a one-bar range, or a feed
        /// with no volume to measure against.
        /// </summary>
        private string StatusText(OrbModel model)
        {
            var last = model.LastBarLocal;
            var zone = _time.Abbrev(last);
            var stamp = _time.Clock == BarClock.Utc ? "UTC" : "already local";

            var minutes = Math.Max(1, OpeningRangeMinutes);
            var orEnd = SessionOpenCt + TimeSpan.FromMinutes(minutes);

            var text = "Ocean ORB Breakout  ·  all times Houston (" + zone + ")  ·  " +
                       "range " + Clock(SessionOpenCt) + "-" + Clock(orEnd) +
                       ", window to " + Clock(CutoffCt) +
                       ", volume " + VolumeMultiple.ToString("0.##") + "x  ·  " +
                       "last bar " + last.ToString("ddd h:mm tt") +
                       "  ·  ATAS stamps bars " + stamp + " (" + _time.Explain + ")";

            DayOrb latest = null;
            for (var i = model.Days.Count - 1; i >= 0; i--)
                if (model.Days[i].HasRange) { latest = model.Days[i]; break; }

            if (latest == null)
                return text + "  ·  no opening range in the loaded history yet";

            if (latest.OrAverageVolume <= 0m)
                return text + "  ·  THIS FEED REPORTS NO VOLUME -- the volume test cannot run, " +
                              "so nothing will signal";

            if (!latest.Aligned)
                return text + "  ·  THE BARS DO NOT DIVIDE THIS RANGE (" + latest.OrBars +
                              " bars, none opening on the boundary) -- use a 1, 5, 10, 15 or " +
                              "30 minute chart";

            if (latest.ThinRange)
                return text + "  ·  the range is a single bar, so the average volume IS that " +
                              "bar -- drop to a faster chart";

            if (RequireAboveVwap && !latest.HasVwap)
                return text + "  ·  no VWAP yet for " + latest.Day.ToString("ddd MMM d");

            if (_labelError != null)
                return text + "  ·  " + _labelError;

            return text + "  ·  " + latest.OrBars + " bars in the range";
        }

        private static string Clock(TimeSpan t) =>
            DateTime.Today.Add(t).ToString("h:mm tt");

        #endregion

        #region Drawing helpers

        private int XStart(int bar) =>
            ChartInfo.PriceChartContainer.GetXByBar(ClampBar(bar), true);

        private int XEnd(int bar) =>
            ChartInfo.PriceChartContainer.GetXByBar(ClampBar(bar), true) +
            (int)ChartInfo.PriceChartContainer.BarsWidth;

        private int ClampBar(int bar) =>
            bar < 0 ? 0 : bar >= CurrentBar ? Math.Max(0, CurrentBar - 1) : bar;

        private int Y(decimal price) =>
            ChartInfo.PriceChartContainer.GetYByPrice(price, false);

        /// <summary>Level label pinned to the right end of the line, sitting just above it.</summary>
        private void DrawLabel(RenderContext context, RenderFont font, string text, Color color,
                               int xRight, int y, Rectangle region)
        {
            var size = context.MeasureString(text, font);
            var x = xRight - size.Width - 4;
            if (x < region.Left) x = region.Left + 2;

            var ly = y - size.Height - 1;
            if (ly < region.Top) ly = y + 1;

            context.DrawString(text, font, color, x, ly);
        }

        private void DrawStatus(RenderContext context, Rectangle region, string text, Color color)
        {
            context.DrawString(text, _statusFont, color, region.Left + 6, region.Top + 4);
        }

        private static Color Conv(MColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

        private static Color Conv(MColor c, int transparency)
        {
            var t = transparency < 0 ? 0 : transparency > 100 ? 100 : transparency;
            return Color.FromArgb((int)Math.Round(255 * (100 - t) / 100.0), c.R, c.G, c.B);
        }

        private static System.Drawing.Drawing2D.DashStyle ToDash(LineDashStyle style)
        {
            switch (style)
            {
                case LineDashStyle.Dash: return System.Drawing.Drawing2D.DashStyle.Dash;
                case LineDashStyle.Dot: return System.Drawing.Drawing2D.DashStyle.Dot;
                case LineDashStyle.DashDot: return System.Drawing.Drawing2D.DashStyle.DashDot;
                case LineDashStyle.DashDotDot: return System.Drawing.Drawing2D.DashStyle.DashDotDot;
                default: return System.Drawing.Drawing2D.DashStyle.Solid;
            }
        }

        #endregion

        /// <summary>
        /// Adapts the indicator's candle access to <see cref="IBarWindow"/>, with a one-entry
        /// cache because the builder reads several fields off the same bar in a row.
        /// </summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansOrbBreakoutIndicator _owner;
            private int _cachedIndex = -1;
            private IndicatorCandle _cached;

            public BarWindow(OceansOrbBreakoutIndicator owner) { _owner = owner; }

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
        }
    }
}
