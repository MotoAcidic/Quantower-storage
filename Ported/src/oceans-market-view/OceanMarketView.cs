using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansMarketView
{
    /// <summary>
    /// Ocean Market View -- four independently toggleable chart overlays: weekly open, power
    /// hour breakout, market sessions with their opening ranges, and initial balance levels.
    ///
    /// EVERY time in this indicator is Houston time. There is no second time zone to convert
    /// from, and none of the settings are in exchange time.
    /// </summary>
    [DisplayName("Ocean Market View")]
    [Category("Ocean")]
    public class OceanMarketView : Indicator
    {
        private const int SessionCount = 3;

        private readonly object _sync = new object();
        private MarketModel _model;
        private TimeContext _time;
        private readonly BarWindow _bars;
        private volatile bool _dirty = true;
        private int _builtBarCount = -1;
        private string _configKey;
        private readonly HashSet<DateTime> _alerted = new HashSet<DateTime>();

        private readonly RenderFont _labelFont = new RenderFont("Arial", 10f);
        private readonly RenderFont _smallFont = new RenderFont("Arial", 8.5f);
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        public OceanMarketView() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = false;

            if (DataSeries.Count > 0 && DataSeries[0] is ValueDataSeries v)
            {
                v.VisualType = VisualMode.Hide;
                v.IsHidden = true;
                v.ShowZeroValue = false;
            }

            _bars = new BarWindow(this);
        }

        #region Settings -- 01 Weekly open

        [Display(Name = "Show", GroupName = "01 Weekly open", Order = 100)]
        public bool WeeklyOpenShow { get; set; } = true;

        [Display(Name = "Week starts on", GroupName = "01 Weekly open", Order = 110)]
        public DayOfWeek WeekStartDay { get; set; } = DayOfWeek.Sunday;

        [Display(Name = "Week opens at (Houston)", GroupName = "01 Weekly open", Order = 120,
                 Description = "Sunday reopen. 5:00 PM Houston for MNQ.")]
        public TimeSpan WeekStartTimeCt { get; set; } = new TimeSpan(17, 0, 0);

        [Display(Name = "Weeks to show", GroupName = "01 Weekly open", Order = 130)]
        public int WeeksToShow { get; set; } = 1;

        [Display(Name = "Line", GroupName = "01 Weekly open", Order = 140)]
        public PenSettings WeeklyOpenPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(60, 120, 255),
            Width = 1,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "Show label", GroupName = "01 Weekly open", Order = 150)]
        public bool WeeklyOpenLabel { get; set; } = true;

        [Display(Name = "Show price in label", GroupName = "01 Weekly open", Order = 160)]
        public bool WeeklyOpenPriceInLabel { get; set; } = true;

        #endregion

        #region Settings -- 02 Power hour

        [Display(Name = "Show", GroupName = "02 Power hour", Order = 200)]
        public bool PowerHourShow { get; set; } = true;

        [Display(Name = "Starts at (Houston)", GroupName = "02 Power hour", Order = 210,
                 Description = "2:00 PM Houston is the last hour of the regular session.")]
        public TimeSpan PowerHourStartCt { get; set; } = new TimeSpan(14, 0, 0);

        [Display(Name = "Ends at (Houston)", GroupName = "02 Power hour", Order = 220)]
        public TimeSpan PowerHourEndCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(Name = "Days to show", GroupName = "02 Power hour", Order = 230)]
        public int PowerHourDays { get; set; } = 1;

        [Display(Name = "Show range box", GroupName = "02 Power hour", Order = 240)]
        public bool PowerHourBox { get; set; } = true;

        [Display(Name = "Box fill", GroupName = "02 Power hour", Order = 250)]
        public MColor PowerHourFill { get; set; } = MColor.FromRgb(120, 120, 160);

        [Display(Name = "Box transparency (%)", GroupName = "02 Power hour", Order = 260)]
        public int PowerHourTransparency { get; set; } = 88;

        [Display(Name = "High line", GroupName = "02 Power hour", Order = 270)]
        public PenSettings PowerHourHighPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(0, 190, 90),
            Width = 1,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "Low line", GroupName = "02 Power hour", Order = 280)]
        public PenSettings PowerHourLowPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(220, 60, 60),
            Width = 1,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "Carry levels forward", GroupName = "02 Power hour", Order = 290,
                 Description = "Extend the high and low to the right until the range breaks.")]
        public bool PowerHourExtend { get; set; } = true;

        [Display(Name = "Mark the break", GroupName = "02 Power hour", Order = 300)]
        public bool PowerHourMarkBreak { get; set; } = true;

        [Display(Name = "Break needs a close", GroupName = "02 Power hour", Order = 310,
                 Description = "On: a bar must close beyond the level. Off: a wick through counts.")]
        public bool PowerHourBreakOnClose { get; set; } = true;

        [Display(Name = "Alert on the break", GroupName = "02 Power hour", Order = 320)]
        public bool PowerHourAlert { get; set; } = false;

        #endregion

        #region Settings -- 03 Market sessions

        [Display(Name = "Show", GroupName = "03 Market sessions", Order = 400)]
        public bool SessionsShow { get; set; } = true;

        [Display(Name = "Days to show", GroupName = "03 Market sessions", Order = 405)]
        public int SessionDays { get; set; } = 3;

        [Display(Name = "Show session boxes", GroupName = "03 Market sessions", Order = 407)]
        public bool SessionBoxes { get; set; } = true;

        [Display(Name = "Show labels", GroupName = "03 Market sessions", Order = 410)]
        public bool SessionLabels { get; set; } = true;

        [Display(Name = "Show times in label", GroupName = "03 Market sessions", Order = 415)]
        public bool SessionTimesInLabel { get; set; } = false;

        [Display(Name = "Fill transparency (%)", GroupName = "03 Market sessions", Order = 420)]
        public int SessionTransparency { get; set; } = 90;

        [Display(Name = "Outline", GroupName = "03 Market sessions", Order = 425)]
        public LineDashStyle SessionOutline { get; set; } = LineDashStyle.Dash;

        [Display(Name = "Asia - show", GroupName = "03 Market sessions", Order = 430)]
        public bool AsiaShow { get; set; } = true;

        [Display(Name = "Asia - name", GroupName = "03 Market sessions", Order = 431)]
        public string AsiaName { get; set; } = "Asia";

        [Display(Name = "Asia - starts at (Houston)", GroupName = "03 Market sessions", Order = 432)]
        public TimeSpan AsiaStartCt { get; set; } = new TimeSpan(18, 0, 0);

        [Display(Name = "Asia - ends at (Houston)", GroupName = "03 Market sessions", Order = 433)]
        public TimeSpan AsiaEndCt { get; set; } = new TimeSpan(3, 0, 0);

        [Display(Name = "Asia - color", GroupName = "03 Market sessions", Order = 434)]
        public MColor AsiaColor { get; set; } = MColor.FromRgb(230, 145, 40);

        [Display(Name = "London - show", GroupName = "03 Market sessions", Order = 440)]
        public bool LondonShow { get; set; } = true;

        [Display(Name = "London - name", GroupName = "03 Market sessions", Order = 441)]
        public string LondonName { get; set; } = "London";

        [Display(Name = "London - starts at (Houston)", GroupName = "03 Market sessions", Order = 442)]
        public TimeSpan LondonStartCt { get; set; } = new TimeSpan(2, 0, 0);

        [Display(Name = "London - ends at (Houston)", GroupName = "03 Market sessions", Order = 443)]
        public TimeSpan LondonEndCt { get; set; } = new TimeSpan(10, 30, 0);

        [Display(Name = "London - color", GroupName = "03 Market sessions", Order = 444)]
        public MColor LondonColor { get; set; } = MColor.FromRgb(70, 190, 110);

        [Display(Name = "New York - show", GroupName = "03 Market sessions", Order = 450)]
        public bool NewYorkShow { get; set; } = true;

        [Display(Name = "New York - name", GroupName = "03 Market sessions", Order = 451)]
        public string NewYorkName { get; set; } = "New York";

        [Display(Name = "New York - starts at (Houston)", GroupName = "03 Market sessions", Order = 452)]
        public TimeSpan NewYorkStartCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "New York - ends at (Houston)", GroupName = "03 Market sessions", Order = 453)]
        public TimeSpan NewYorkEndCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(Name = "New York - color", GroupName = "03 Market sessions", Order = 454)]
        public MColor NewYorkColor { get; set; } = MColor.FromRgb(60, 130, 230);

        #endregion

        #region Settings -- 04 Session opening range

        [Display(Name = "Show", GroupName = "04 Session opening range", Order = 460,
                 Description = "The first minutes of each session, marked high, low and middle.")]
        public bool OpenRangeShow { get; set; } = true;

        [Display(Name = "Length (minutes)", GroupName = "04 Session opening range", Order = 462)]
        public int OpenRangeMinutes { get; set; } = 15;

        [Display(Name = "Asia - show", GroupName = "04 Session opening range", Order = 464)]
        public bool OpenRangeAsia { get; set; } = true;

        [Display(Name = "London - show", GroupName = "04 Session opening range", Order = 465)]
        public bool OpenRangeLondon { get; set; } = true;

        [Display(Name = "New York - show", GroupName = "04 Session opening range", Order = 466)]
        public bool OpenRangeNewYork { get; set; } = true;

        [Display(Name = "Show high and low", GroupName = "04 Session opening range", Order = 468)]
        public bool OpenRangeEdges { get; set; } = true;

        [Display(Name = "Show middle line", GroupName = "04 Session opening range", Order = 470)]
        public bool OpenRangeMiddle { get; set; } = true;

        [Display(Name = "Shade the opening range", GroupName = "04 Session opening range", Order = 472)]
        public bool OpenRangeShade { get; set; } = true;

        [Display(Name = "High and low width", GroupName = "04 Session opening range", Order = 474)]
        public int OpenRangeWidth { get; set; } = 2;

        [Display(Name = "High and low style", GroupName = "04 Session opening range", Order = 475)]
        public LineDashStyle OpenRangeStyle { get; set; } = LineDashStyle.Solid;

        [Display(Name = "Middle line style", GroupName = "04 Session opening range", Order = 476)]
        public LineDashStyle OpenRangeMidStyle { get; set; } = LineDashStyle.Dot;

        [Display(Name = "Carry past the session", GroupName = "04 Session opening range", Order = 478,
                 Description = "Off stops the lines at the session end.")]
        public bool OpenRangeExtend { get; set; } = false;

        [Display(Name = "Show labels", GroupName = "04 Session opening range", Order = 480)]
        public bool OpenRangeLabels { get; set; } = true;

        #endregion

        #region Settings -- 05 Initial balance

        [Display(Name = "Show", GroupName = "05 Initial balance", Order = 500)]
        public bool IbShow { get; set; } = true;

        [Display(Name = "Starts at (Houston)", GroupName = "05 Initial balance", Order = 510,
                 Description = "8:30 AM Houston is the regular session open.")]
        public TimeSpan IbStartCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "Length (minutes)", GroupName = "05 Initial balance", Order = 520)]
        public int IbMinutes { get; set; } = 60;

        [Display(Name = "Days to show", GroupName = "05 Initial balance", Order = 530)]
        public int IbDays { get; set; } = 1;

        [Display(Name = "IB high line", GroupName = "05 Initial balance", Order = 540)]
        public bool IbShowHigh { get; set; } = true;

        [Display(Name = "IB low line", GroupName = "05 Initial balance", Order = 541)]
        public bool IbShowLow { get; set; } = true;

        [Display(Name = "IB middle line", GroupName = "05 Initial balance", Order = 542,
                 Description = "Halfway between the initial balance high and low.")]
        public bool IbShowMid { get; set; } = true;

        [Display(Name = "0.5x levels", GroupName = "05 Initial balance", Order = 543)]
        public bool IbShow05 { get; set; } = true;

        [Display(Name = "1x levels", GroupName = "05 Initial balance", Order = 544)]
        public bool IbShow10 { get; set; } = true;

        [Display(Name = "1.5x levels", GroupName = "05 Initial balance", Order = 545)]
        public bool IbShow15 { get; set; } = true;

        [Display(Name = "2x levels", GroupName = "05 Initial balance", Order = 546)]
        public bool IbShow20 { get; set; } = true;

        [Display(Name = "2.5x levels", GroupName = "05 Initial balance", Order = 547)]
        public bool IbShow25 { get; set; } = true;

        [Display(Name = "3x levels", GroupName = "05 Initial balance", Order = 548)]
        public bool IbShow30 { get; set; } = true;

        [Display(Name = "3.5x levels", GroupName = "05 Initial balance", Order = 549)]
        public bool IbShow35 { get; set; } = true;

        [Display(Name = "Above line", GroupName = "05 Initial balance", Order = 560)]
        public PenSettings IbUpPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(0, 190, 90),
            Width = 1,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "Below line", GroupName = "05 Initial balance", Order = 570)]
        public PenSettings IbDownPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(220, 60, 60),
            Width = 1,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "IB high/low line", GroupName = "05 Initial balance", Order = 580,
                 Description = "The initial balance itself, drawn heavier than the extensions.")]
        public PenSettings IbZeroPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(255, 255, 255),
            Width = 2,
            LineDashStyle = LineDashStyle.Solid
        };

        [Display(Name = "IB middle line style", GroupName = "05 Initial balance", Order = 585)]
        public PenSettings IbMidPen { get; set; } = new PenSettings
        {
            Color = MColor.FromRgb(255, 255, 255),
            Width = 1,
            LineDashStyle = LineDashStyle.Dot
        };

        [Display(Name = "Show labels", GroupName = "05 Initial balance", Order = 590)]
        public bool IbLabels { get; set; } = true;

        [Display(Name = "Carry today to the right edge", GroupName = "05 Initial balance", Order = 600)]
        public bool IbExtendRight { get; set; } = true;

        #endregion

        #region Settings -- 06 Clock

        [Display(Name = "Your time zone", GroupName = "06 Clock", Order = 700,
                 Description = "Every time in this indicator is in this zone. Houston is " +
                               "'Central Standard Time' -- it covers daylight time too.")]
        public string TimeZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "06 Clock", Order = 720,
                 Description = "Whether ATAS stamps bars in UTC or already in your zone. Auto " +
                               "works it out and prints the answer in the readout; if it cannot, " +
                               "nothing is drawn.")]
        public BarClock BarTimes { get; set; } = BarClock.Auto;

        [Display(Name = "Daily halt hour", GroupName = "06 Clock", Order = 730,
                 Description = "The hour with no trading, used to check the bar clock. " +
                               "16 = the 4-5 PM Houston MNQ halt published by CME. This is " +
                               "NOT the 3 PM cash close.")]
        public int HaltHourCt { get; set; } = 16;

        [Display(Name = "Regular session opens (Houston)", GroupName = "06 Clock", Order = 740)]
        public TimeSpan RthStartCt { get; set; } = new TimeSpan(8, 30, 0);

        [Display(Name = "Regular session closes (Houston)", GroupName = "06 Clock", Order = 750)]
        public TimeSpan RthEndCt { get; set; } = new TimeSpan(15, 0, 0);

        [Display(Name = "Show status readout", GroupName = "06 Clock", Order = 760,
                 Description = "Top-left line with the last bar's time. Check it against your " +
                               "own clock the first time you attach this.")]
        public bool ShowStatus { get; set; } = true;

        #endregion

        #region Settings -- 07 Display

        [Display(Name = "Draw above candles", GroupName = "07 Display", Order = 800,
                 Description = "Off puts the boxes and levels behind the candles.")]
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
                _model = null;
                _builtBarCount = -1;
            }
            _dirty = true;
        }

        /// <summary>
        /// Fingerprint of every setting the MODEL depends on. Editing a session window has to
        /// rebuild; editing a colour must not. ATAS does not reliably recalculate on a plain
        /// property edit, so the change is detected here rather than waited for.
        /// </summary>
        private string ConfigKey() => string.Join("|",
            TimeZoneId, (int)BarTimes, HaltHourCt, RthStartCt, RthEndCt,
            WeekStartDay, WeekStartTimeCt, IbStartCt, IbMinutes,
            PowerHourStartCt, PowerHourEndCt, PowerHourBreakOnClose,
            OpenRangeShow, OpenRangeMinutes, OpenRangeAsia, OpenRangeLondon, OpenRangeNewYork,
            AsiaShow, AsiaName, AsiaStartCt, AsiaEndCt,
            LondonShow, LondonName, LondonStartCt, LondonEndCt,
            NewYorkShow, NewYorkName, NewYorkStartCt, NewYorkEndCt);

        private int OrbMinutesFor(int defIndex)
        {
            if (!OpenRangeShow) return 0;

            var on = defIndex == 0 ? OpenRangeAsia
                   : defIndex == 1 ? OpenRangeLondon
                   : OpenRangeNewYork;

            return on ? Math.Max(0, OpenRangeMinutes) : 0;
        }

        private ModelConfig BuildConfig(TimeContext time)
        {
            var defs = new SessionDef[SessionCount];

            defs[0] = AsiaShow ? new SessionDef
            {
                Name = AsiaName, Start = AsiaStartCt, End = AsiaEndCt, OpeningRangeMinutes = OrbMinutesFor(0)
            } : null;

            defs[1] = LondonShow ? new SessionDef
            {
                Name = LondonName, Start = LondonStartCt, End = LondonEndCt, OpeningRangeMinutes = OrbMinutesFor(1)
            } : null;

            defs[2] = NewYorkShow ? new SessionDef
            {
                Name = NewYorkName, Start = NewYorkStartCt, End = NewYorkEndCt, OpeningRangeMinutes = OrbMinutesFor(2)
            } : null;

            return new ModelConfig
            {
                Time = time,
                WeekStartDay = WeekStartDay,
                WeekStartTime = WeekStartTimeCt,
                IbStart = IbStartCt,
                IbMinutes = Math.Max(1, IbMinutes),
                RthStart = RthStartCt,
                RthEnd = RthEndCt,
                PhStart = PowerHourStartCt,
                PhEnd = PowerHourEndCt,
                BreakoutOnClose = PowerHourBreakOnClose,
                Sessions = defs
            };
        }

        private void Rebuild()
        {
            lock (_sync)
            {
                _configKey = ConfigKey();

                var nowUtc = UtcTime.Year > 2000 ? UtcTime : DateTime.UtcNow;

                _time = TimeContext.Create(TimeZoneId, BarTimes, _bars, nowUtc, HaltHourCt);
                _model = _time.Valid ? MarketModel.Build(_bars, BuildConfig(_time)) : null;
                _builtBarCount = CurrentBar;
                _dirty = false;

                if (_model != null && PowerHourAlert) RaiseBreakoutAlerts();
            }
        }

        private void RaiseBreakoutAlerts()
        {
            foreach (var d in _model.Days)
            {
                if (d.PhBreakBar < 0 || _alerted.Contains(d.Date)) continue;

                // Only the live edge alerts. Replaying history must stay silent.
                if (d.PhBreakBar < CurrentBar - 2) { _alerted.Add(d.Date); continue; }

                _alerted.Add(d.Date);
                var dir = d.PhBreakDir > 0 ? "above" : "below";
                AddAlert("alert1", $"Power hour broke {dir} {d.PhBreakPrice}");
            }
        }

        /// <summary>The IB multiples currently switched on, zero included when either edge is.</summary>
        private List<decimal> IbMultiples()
        {
            var m = new List<decimal>();
            if (IbShowHigh || IbShowLow) m.Add(0m);
            if (IbShow05) m.Add(0.5m);
            if (IbShow10) m.Add(1m);
            if (IbShow15) m.Add(1.5m);
            if (IbShow20) m.Add(2m);
            if (IbShow25) m.Add(2.5m);
            if (IbShow30) m.Add(3m);
            if (IbShow35) m.Add(3.5m);
            return m;
        }

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
                DrawStatus(context, region, _time?.Error ?? "Ocean Market View: starting up.",
                           Color.FromArgb(255, 110, 110));
                return;
            }

            var model = _model;
            if (model == null) return;

            if (SessionsShow) RenderSessions(context, region, model);
            if (IbShow) RenderInitialBalance(context, region, model);
            if (PowerHourShow) RenderPowerHour(context, region, model);
            if (WeeklyOpenShow) RenderWeeklyOpen(context, region, model);

            if (ShowStatus) DrawStatus(context, region, StatusText(model), Color.FromArgb(150, 160, 175));
        }

        private string StatusText(MarketModel model)
        {
            var last = model.LastBarLocal;
            var zone = _time.Abbrev(last);
            var stamp = _time.Clock == BarClock.Utc ? "UTC" : "already local";

            return $"Ocean Market View  ·  all times Houston ({zone})  ·  " +
                   $"last bar {last:ddd h:mm tt}  ·  ATAS stamps bars {stamp} ({_time.Explain})";
        }

        #region Weekly open

        private void RenderWeeklyOpen(RenderContext context, Rectangle region, MarketModel model)
        {
            var pen = WeeklyOpenPen.RenderObject;
            var take = Math.Max(1, WeeksToShow);
            var start = Math.Max(0, model.Weeks.Count - take);

            for (var i = start; i < model.Weeks.Count; i++)
            {
                var w = model.Weeks[i];
                if (w.StartBar < 0) continue;

                var x1 = XStart(w.StartBar);
                var x2 = w.Current ? region.Right : XEnd(w.EndBar);
                if (x2 < region.Left || x1 > region.Right) continue;

                var y = Y(w.Price);
                if (y < region.Top || y > region.Bottom) continue;

                x1 = Math.Max(x1, region.Left);
                x2 = Math.Min(x2, region.Right);
                context.DrawLine(pen, x1, y, x2, y);

                if (!WeeklyOpenLabel) continue;

                var text = WeeklyOpenPriceInLabel
                    ? $"Weekly Open {ChartInfo.GetPriceString(w.Price)}"
                    : "Weekly Open";

                DrawLabel(context, _labelFont, text, Conv(WeeklyOpenPen.Color), x2, y, region);
            }
        }

        #endregion

        #region Market sessions and opening ranges

        private void RenderSessions(RenderContext context, Rectangle region, MarketModel model)
        {
            var days = RecentSessionDates(model, Math.Max(1, SessionDays));

            foreach (var box in model.Sessions)
            {
                if (!days.Contains(box.AnchorDate)) continue;
                if (box.StartBar < 0 || box.EndBar < 0) continue;

                var color = SessionColor(box.DefIndex);
                var x1 = XStart(box.StartBar);
                var x2 = XEnd(box.EndBar);
                if (x2 < region.Left || x1 > region.Right) continue;

                var cx1 = Math.Max(x1, region.Left);
                var cx2 = Math.Min(x2, region.Right);

                if (SessionBoxes)
                {
                    var yTop = Y(box.High);
                    var yBottom = Y(box.Low);
                    var rect = Rectangle.FromLTRB(cx1, Math.Min(yTop, yBottom), cx2, Math.Max(yTop, yBottom));

                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        context.FillRectangle(Conv(color, SessionTransparency), rect);
                        context.DrawRectangle(new RenderPen(Conv(color), 1, ToDash(SessionOutline)), rect);

                        if (SessionLabels) DrawSessionLabel(context, region, box, color, rect, cx1);
                    }
                }
                else if (SessionLabels)
                {
                    var yTop = Y(box.High);
                    DrawSessionLabel(context, region, box, color,
                                     Rectangle.FromLTRB(cx1, yTop, cx2, yTop), cx1);
                }

                if (OpenRangeShow) RenderOpeningRange(context, region, box, color);
            }
        }

        private void DrawSessionLabel(RenderContext context, Rectangle region, SessionBox box,
                                      MColor color, Rectangle rect, int left)
        {
            var text = $"{box.Name} • {box.AnchorDate.DayOfWeek}";

            if (SessionTimesInLabel)
                text += $"  {box.StartLocal:h:mm tt} - {box.EndLocal:h:mm tt}";

            var size = context.MeasureString(text, _labelFont);
            var ly = rect.Top - size.Height - 2;
            if (ly < region.Top) ly = rect.Top + 2;

            context.DrawString(text, _labelFont, Conv(color), left + 4, ly);
        }

        /// <summary>
        /// The first minutes of a session: high, low and the midpoint between them. Drawn from
        /// the opening range itself, so the box shows where it formed and the lines carry it.
        /// </summary>
        private void RenderOpeningRange(RenderContext context, Rectangle region, SessionBox box, MColor color)
        {
            if (!box.HasOpenRange || box.OpenRangeStartBar < 0) return;

            var ox1 = XStart(box.OpenRangeStartBar);
            var ox2 = XEnd(box.OpenRangeEndBar);

            var yHigh = Y(box.OpenRangeHigh);
            var yLow = Y(box.OpenRangeLow);

            if (OpenRangeShade && ox2 >= region.Left && ox1 <= region.Right)
            {
                var shade = Rectangle.FromLTRB(Math.Max(ox1, region.Left), Math.Min(yHigh, yLow),
                                               Math.Min(ox2, region.Right), Math.Max(yHigh, yLow));
                if (shade.Width > 0 && shade.Height > 0)
                    context.FillRectangle(Conv(color, 70), shade);
            }

            // Lines run from the opening range to the end of the session, or on past it when
            // asked. A still-forming session runs to the right edge either way.
            var sessionEnd = box.Complete && !OpenRangeExtend ? XEnd(box.EndBar) : region.Right;
            var lx1 = Math.Max(ox1, region.Left);
            var lx2 = Math.Min(sessionEnd, region.Right);
            if (lx2 <= lx1) return;

            var pen = new RenderPen(Conv(color), Math.Max(1, OpenRangeWidth), ToDash(OpenRangeStyle));
            var midPen = new RenderPen(Conv(color), 1, ToDash(OpenRangeMidStyle));

            if (OpenRangeEdges)
            {
                DrawOrbLine(context, region, pen, lx1, lx2, yHigh, $"{box.Name} OR high", color);
                DrawOrbLine(context, region, pen, lx1, lx2, yLow, $"{box.Name} OR low", color);
            }

            if (OpenRangeMiddle)
                DrawOrbLine(context, region, midPen, lx1, lx2, Y(box.OpenRangeMid),
                            $"{box.Name} OR mid", color);
        }

        private void DrawOrbLine(RenderContext context, Rectangle region, RenderPen pen,
                                 int x1, int x2, int y, string label, MColor color)
        {
            if (y < region.Top || y > region.Bottom) return;

            context.DrawLine(pen, x1, y, x2, y);

            if (OpenRangeLabels)
                DrawLabel(context, _smallFont, label, Conv(color), x2, y, region);
        }

        private MColor SessionColor(int defIndex)
        {
            switch (defIndex)
            {
                case 0: return AsiaColor;
                case 1: return LondonColor;
                default: return NewYorkColor;
            }
        }

        private static HashSet<DateTime> RecentSessionDates(MarketModel model, int count)
        {
            var all = new List<DateTime>();
            foreach (var b in model.Sessions)
                if (!all.Contains(b.AnchorDate)) all.Add(b.AnchorDate);

            all.Sort();

            var set = new HashSet<DateTime>();
            for (var i = Math.Max(0, all.Count - count); i < all.Count; i++) set.Add(all[i]);
            return set;
        }

        #endregion

        #region Initial balance

        private void RenderInitialBalance(RenderContext context, Rectangle region, MarketModel model)
        {
            var multiples = IbMultiples();
            if (multiples.Count == 0) return;

            var take = Math.Max(1, IbDays);
            var shown = 0;

            for (var i = model.Days.Count - 1; i >= 0 && shown < take; i--)
            {
                var d = model.Days[i];
                if (!d.HasIb || !d.IbComplete || d.IbRange <= 0) continue;
                shown++;

                var isLatest = shown == 1;
                var x1 = XEnd(d.IbEndBar);
                var x2 = isLatest && IbExtendRight
                    ? region.Right
                    : XEnd(d.RthEndBar >= 0 ? d.RthEndBar : d.IbEndBar);

                if (x2 < region.Left || x1 > region.Right) continue;
                x1 = Math.Max(x1, region.Left);
                x2 = Math.Min(x2, region.Right);
                if (x2 <= x1) continue;

                foreach (var level in MarketModel.IbLevels(d, multiples))
                {
                    // The two zero lines switch independently of each other.
                    if (level.IsZero && level.Above && !IbShowHigh) continue;
                    if (level.IsZero && !level.Above && !IbShowLow) continue;

                    // The balance itself is drawn in its own colour; only the extensions
                    // keep the above/below coding.
                    var pen = level.IsZero ? IbZeroPen : level.Above ? IbUpPen : IbDownPen;
                    DrawIbLine(context, region, x1, x2, level.Price, pen, level.Label);
                }

                if (IbShowMid)
                    DrawIbLine(context, region, x1, x2, d.IbMid, IbMidPen, "mid");
            }
        }

        private void DrawIbLine(RenderContext context, Rectangle region, int x1, int x2,
                                decimal price, PenSettings pen, string label)
        {
            var y = Y(price);
            if (y < region.Top || y > region.Bottom) return;

            context.DrawLine(new RenderPen(Conv(pen.Color), Math.Max(1, pen.Width),
                                           ToDash(pen.LineDashStyle)), x1, y, x2, y);

            if (IbLabels) DrawLabel(context, _labelFont, label, Conv(pen.Color), x2, y, region);
        }

        #endregion

        #region Power hour

        private void RenderPowerHour(RenderContext context, Rectangle region, MarketModel model)
        {
            var take = Math.Max(1, PowerHourDays);
            var shown = 0;

            for (var i = model.Days.Count - 1; i >= 0 && shown < take; i--)
            {
                var d = model.Days[i];
                if (!d.HasPh || d.PhStartBar < 0 || d.PhEndBar < 0) continue;
                shown++;

                var bx1 = XStart(d.PhStartBar);
                var bx2 = XEnd(d.PhEndBar);

                if (PowerHourBox && bx2 >= region.Left && bx1 <= region.Right)
                {
                    var yTop = Y(d.PhHigh);
                    var yBottom = Y(d.PhLow);
                    var rect = Rectangle.FromLTRB(Math.Max(bx1, region.Left), Math.Min(yTop, yBottom),
                                                  Math.Min(bx2, region.Right), Math.Max(yTop, yBottom));
                    if (rect.Width > 0 && rect.Height > 0)
                        context.FillRectangle(Conv(PowerHourFill, PowerHourTransparency), rect);
                }

                // Levels run from the power hour to whatever ended them: the break if there was
                // one, otherwise the right edge. Drawing them past the break would imply the
                // range still stands.
                var lx2 = !PowerHourExtend
                    ? bx2
                    : d.PhBreakBar >= 0 ? XEnd(d.PhBreakBar) : region.Right;

                DrawPhLevel(context, region, bx1, lx2, d.PhHigh, PowerHourHighPen, "Power hour high");
                DrawPhLevel(context, region, bx1, lx2, d.PhLow, PowerHourLowPen, "Power hour low");

                if (PowerHourMarkBreak && d.PhBreakBar >= 0)
                    DrawBreakMarker(context, region, d);
            }
        }

        private void DrawPhLevel(RenderContext context, Rectangle region, int x1, int x2,
                                 decimal price, PenSettings pen, string label)
        {
            var y = Y(price);
            if (y < region.Top || y > region.Bottom) return;
            if (x2 < region.Left || x1 > region.Right) return;

            x1 = Math.Max(x1, region.Left);
            x2 = Math.Min(x2, region.Right);
            if (x2 <= x1) return;

            context.DrawLine(new RenderPen(Conv(pen.Color), Math.Max(1, pen.Width),
                                           ToDash(pen.LineDashStyle)), x1, y, x2, y);

            DrawLabel(context, _labelFont, label, Conv(pen.Color), x2, y, region);
        }

        private void DrawBreakMarker(RenderContext context, Rectangle region, DayLevels d)
        {
            var up = d.PhBreakDir > 0;
            var pen = up ? PowerHourHighPen : PowerHourLowPen;
            var color = Conv(pen.Color);

            var x = XStart(d.PhBreakBar) + (int)(ChartInfo.PriceChartContainer.BarsWidth / 2);
            var y = Y(d.PhBreakPrice);
            if (x < region.Left || x > region.Right || y < region.Top || y > region.Bottom) return;

            var w = Math.Max(4, (int)ChartInfo.PriceChartContainer.BarsWidth);
            var tip = up ? y - w - 2 : y + w + 2;

            context.FillPolygon(color, new[]
            {
                new Point(x, tip),
                new Point(x - w / 2, up ? y - 2 : y + 2),
                new Point(x + w / 2, up ? y - 2 : y + 2)
            });

            var text = up ? "broke up" : "broke down";
            var size = context.MeasureString(text, _smallFont);
            var ly = up ? tip - size.Height - 2 : tip + 2;
            context.DrawString(text, _smallFont, color, x - size.Width / 2, ly);
        }

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

        #endregion

        /// <summary>
        /// Adapts the indicator's candle access to <see cref="IBarWindow"/>, with a one-entry
        /// cache because the builder reads several fields off the same bar in a row.
        /// </summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly OceanMarketView _owner;
            private int _cachedIndex = -1;
            private IndicatorCandle _cached;

            public BarWindow(OceanMarketView owner) { _owner = owner; }

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
        }
    }
}
