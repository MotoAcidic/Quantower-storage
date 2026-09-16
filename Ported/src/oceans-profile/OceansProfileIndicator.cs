using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansProfile
{
    /// <summary>Where the profile is drawn.</summary>
    public enum ProfileLayout
    {
        /// <summary>One column at the edge of the panel, clear of the candles.</summary>
        [Display(Name = "Edge column")] Edge,

        /// <summary>One column per period, sitting at that period's bars.</summary>
        [Display(Name = "One column per period")] PerPeriod
    }

    /// <summary>How a level's numbers become a bar.</summary>
    public enum HeatStyle
    {
        /// <summary>
        /// A grey bar for everything that traded and a coloured bar for how much of it leaned,
        /// on the same scale. The gap between the two is absorption, at a glance.
        /// </summary>
        [Display(Name = "Volume with delta overlay")] VolumeWithDelta,

        /// <summary>One bar, coloured by which side aggressed.</summary>
        [Display(Name = "Single bar, coloured by side")] HueByDelta
    }

    public enum ProfileSide
    {
        [Display(Name = "Left")] Left,
        [Display(Name = "Right")] Right
    }

    /// <summary>
    /// Ocean Profile -- the higher timeframe's traded volume by price, as a heatmap, on the same
    /// chart you are trading.
    ///
    /// Everything drawn here printed on the tape. Volume, the bid/ask split behind it and the
    /// trade count are published facts, not resting orders that could be pulled and not an
    /// estimate of anything. Where that matters most is absorption: a level that traded heavily
    /// while the aggression stayed balanced is evidence of a large passive order that actually
    /// got filled, which is the honest answer to "where is the size" on a feed with no
    /// market-by-order data.
    /// </summary>
    [DisplayName("Oceans Profile")]
    [Category("Ocean")]
    public class OceansProfileIndicator : Indicator
    {
        private const int MaxProfileTicks = 20000;
        private const int MaxVisiblePeriods = 120;

        private sealed class Cached
        {
            public Profile Profile;
            public DisplayRow[] View;
            public decimal MaxRow;
            public int FirstBar;
            public int LastBar;
            public decimal Signature;
            public int TicksPerRow;
            public decimal LeanScale;
            public ProfileMath.KeyLevel[] Absorbed;
        }

        private readonly Dictionary<DateTime, Cached> _cache = new Dictionary<DateTime, Cached>();
        private readonly Dictionary<int, DateTime> _barPeriod = new Dictionary<int, DateTime>();
        private ProfilePeriod _barPeriodBasis;
        private int _timeTriedAtBar = -1;
        private TimeContext _time;
        private string _timeError;

        private RenderFont _font;
        private int _fontSize = 8;
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        private MColor _sellColor = MColor.FromRgb(230, 70, 90);
        private MColor _neutralColor = MColor.FromRgb(120, 125, 140);
        private MColor _buyColor = MColor.FromRgb(60, 210, 130);
        private MColor _pocColor = MColor.FromRgb(255, 205, 70);
        private MColor _absorbColor = MColor.FromRgb(255, 255, 255);
        private MColor _valueAreaColor = MColor.FromRgb(90, 110, 190);

        public OceansProfileIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            DrawAbovePrice = false;

            var series = DataSeries[0] as ValueDataSeries;
            if (series != null)
            {
                series.VisualType = VisualMode.Hide;
                series.IsHidden = true;
                series.ShowZeroValue = false;
                series.ScaleIt = false;
                series.IgnoredByAlerts = true;
            }

            _font = new RenderFont("Arial", _fontSize);
        }

        #region Settings -- period

        [Display(Name = "Higher timeframe", GroupName = "01 Period", Order = 100,
                 Description = "A real clock period, so it means the same thing on a 1 minute " +
                               "chart and on a 1 hour chart.")]
        public ProfilePeriod Period { get; set; } = ProfilePeriod.H1;

        [Display(Name = "Time zone", GroupName = "01 Period", Order = 110,
                 Description = "Houston is 'Central Standard Time'. Everything is cut and " +
                               "labelled in this one zone.")]
        public string ZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "01 Period", Order = 120,
                 Description = "Whether ATAS stamps bars UTC or already local. Auto works it " +
                               "out from the data and shows how it decided.")]
        public BarClock Clock { get; set; } = BarClock.Auto;

        #endregion

        #region Settings -- layout

        [Display(Name = "Layout", GroupName = "02 Layout", Order = 200)]
        public ProfileLayout Layout { get; set; } = ProfileLayout.Edge;

        [Display(Name = "Bar style", GroupName = "02 Layout", Order = 205,
                 Description = "Volume with delta overlay draws what traded in grey and how much " +
                               "of it leaned in colour, on one scale. The gap between them is " +
                               "absorption.")]
        public HeatStyle Style { get; set; } = HeatStyle.VolumeWithDelta;

        [Display(Name = "Edge: which side", GroupName = "02 Layout", Order = 210)]
        public ProfileSide Side { get; set; } = ProfileSide.Left;

        [Display(Name = "Edge: width (px)", GroupName = "02 Layout", Order = 220)]
        [Range(30, 800)]
        public int EdgeWidth { get; set; } = 150;

        [Display(Name = "Edge: periods included", GroupName = "02 Layout", Order = 230,
                 Description = "How many of the most recent periods are merged into the one " +
                               "column. 1 is just the period in progress.")]
        [Range(1, 60)]
        public int EdgePeriods { get; set; } = 3;

        [Display(Name = "Per period: width (% of period)", GroupName = "02 Layout", Order = 240)]
        [Range(10, 100)]
        public int PeriodWidthPercent { get; set; } = 70;

        [Display(Name = "Minimum row height (px)", GroupName = "02 Layout", Order = 250,
                 Description = "Ticks merge into one drawn row below this height. The analysis " +
                               "always runs at full tick resolution regardless.")]
        [Range(1, 40)]
        public int MinRowHeight { get; set; } = 3;

        [Display(Name = "Row gap (px)", GroupName = "02 Layout", Order = 260)]
        [Range(0, 6)]
        public int RowGap { get; set; } = 1;

        #endregion

        #region Settings -- colours

        [Display(Name = "Sellers aggressing", GroupName = "03 Colours", Order = 300)]
        public MColor SellColor { get { return _sellColor; } set { _sellColor = value; } }

        [Display(Name = "Balanced", GroupName = "03 Colours", Order = 310,
                 Description = "The colour of a level where neither side dominated. Heavy " +
                               "volume in this colour is absorption.")]
        public MColor NeutralColor { get { return _neutralColor; } set { _neutralColor = value; } }

        [Display(Name = "Buyers aggressing", GroupName = "03 Colours", Order = 320)]
        public MColor BuyColor { get { return _buyColor; } set { _buyColor = value; } }

        [Display(Name = "Point of control", GroupName = "03 Colours", Order = 330)]
        public MColor PocColor { get { return _pocColor; } set { _pocColor = value; } }

        [Display(Name = "Absorption", GroupName = "03 Colours", Order = 340)]
        public MColor AbsorbColor { get { return _absorbColor; } set { _absorbColor = value; } }

        [Display(Name = "Value area", GroupName = "03 Colours", Order = 350)]
        public MColor ValueAreaColor { get { return _valueAreaColor; } set { _valueAreaColor = value; } }

        [Display(Name = "Opacity of the smallest (%)", GroupName = "03 Colours", Order = 360)]
        [Range(0, 100)]
        public int MinOpacity { get; set; } = 25;

        [Display(Name = "Opacity of the largest (%)", GroupName = "03 Colours", Order = 370)]
        [Range(0, 100)]
        public int MaxOpacity { get; set; } = 95;

        #endregion

        #region Settings -- what to mark

        [Display(Name = "Point of control", GroupName = "04 Marks", Order = 400)]
        public bool ShowPoc { get; set; } = true;

        [Display(Name = "Value area", GroupName = "04 Marks", Order = 410)]
        public bool ShowValueArea { get; set; } = true;

        [Display(Name = "Value area (%)", GroupName = "04 Marks", Order = 420)]
        [Range(50, 95)]
        public int ValueAreaPercentage { get; set; } = 70;

        [Display(Name = "Absorption", GroupName = "04 Marks", Order = 430,
                 Description = "Heavy volume that traded near balanced -- something passive was " +
                               "filled there. The factual version of 'a big order sits here'.")]
        public bool ShowAbsorption { get; set; } = true;

        [Display(Name = "Absorption: size (% of busiest)", GroupName = "04 Marks", Order = 440)]
        [Range(10, 100)]
        public int AbsorbVolumePercent { get; set; } = 70;

        [Display(Name = "Absorption: balance (% one-sided)", GroupName = "04 Marks", Order = 450,
                 Description = "How lopsided the aggression may be and still count as absorbed. " +
                               "25 means neither side may hold more than 62.5% of the volume.")]
        [Range(1, 60)]
        public int AbsorbBalancePercent { get; set; } = 15;

        [Display(Name = "Absorption: how many to mark", GroupName = "04 Marks", Order = 455,
                 Description = "Only the heaviest shelves get a line across the chart. Marking " +
                               "every one of them is how a signal turns back into wallpaper.")]
        [Range(1, 10)]
        public int AbsorbCount { get; set; } = 3;

        [Display(Name = "Stacked imbalances", GroupName = "04 Marks", Order = 460)]
        public bool ShowStacks { get; set; } = true;

        [Display(Name = "Imbalance ratio", GroupName = "04 Marks", Order = 470)]
        [Range(2, 20)]
        public int ImbalanceRatio { get; set; } = 3;

        [Display(Name = "Imbalance: ignore below this size", GroupName = "04 Marks", Order = 480)]
        [Range(0, 5000)]
        public int ImbalanceMinVolume { get; set; } = 20;

        [Display(Name = "Stack: how many in a row", GroupName = "04 Marks", Order = 490)]
        [Range(2, 10)]
        public int StackRun { get; set; } = 3;

        [Display(Name = "High volume nodes", GroupName = "04 Marks", Order = 500)]
        public bool ShowHighNodes { get; set; } = false;

        [Display(Name = "Low volume nodes", GroupName = "04 Marks", Order = 510)]
        public bool ShowLowNodes { get; set; } = false;

        [Display(Name = "Unfinished auction", GroupName = "04 Marks", Order = 520,
                 Description = "Marks a period high or low that traded on both sides, so the " +
                               "auction never found a price nobody would take.")]
        public bool ShowUnfinished { get; set; } = false;

        #endregion

        #region Settings -- numbers

        [Display(Name = "Numbers on the biggest levels", GroupName = "05 Numbers", Order = 600)]
        public bool ShowNumbers { get; set; } = true;

        [Display(Name = "How many", GroupName = "05 Numbers", Order = 610)]
        [Range(1, 40)]
        public int NumberCount { get; set; } = 6;

        [Display(Name = "Show delta beside volume", GroupName = "05 Numbers", Order = 620)]
        public bool ShowDelta { get; set; } = true;

        [Display(Name = "Text size", GroupName = "05 Numbers", Order = 630)]
        [Range(5, 20)]
        public int FontSize
        {
            get { return _fontSize; }
            set
            {
                if (value < 5) value = 5;
                if (value > 20) value = 20;
                _fontSize = value;
                _font = new RenderFont("Arial", _fontSize);
            }
        }

        [Display(Name = "Show header", GroupName = "05 Numbers", Order = 640)]
        public bool ShowHeader { get; set; } = true;

        [Display(Name = "Say it in words", GroupName = "05 Numbers", Order = 650,
                 Description = "A plain-language read of the profile: where price sits against " +
                               "value, and the levels that matter above and below it.")]
        public bool ShowVerdict { get; set; } = true;

        #endregion

        #region Settings -- levels carried across the chart

        [Display(Name = "Carry the point of control across", GroupName = "06 Key levels", Order = 700)]
        public bool PocRay { get; set; } = true;

        [Display(Name = "Carry absorption across", GroupName = "06 Key levels", Order = 710,
                 Description = "The reason to draw these: a shelf where size was absorbed is " +
                               "where price is likely to react when it comes back to it.")]
        public bool AbsorptionRay { get; set; } = true;

        [Display(Name = "Carry the value area across", GroupName = "06 Key levels", Order = 720)]
        public bool ValueAreaRay { get; set; } = false;

        [Display(Name = "Label the levels with their price", GroupName = "06 Key levels", Order = 730)]
        public bool LabelRays { get; set; } = true;

        #endregion

        protected override void OnCalculate(int bar, decimal value)
        {
            // Profiles are built on demand from the visible bars and cached. Nothing to do
            // per bar except let the cache notice the newest one changed.
        }

        protected override void OnRecalculate()
        {
            _cache.Clear();
            _barPeriod.Clear();
            _time = null;
            _timeError = null;
            _timeTriedAtBar = -1;
        }

        #region Time

        /// <summary>
        /// Establishes whether ATAS is stamping bars UTC or local, from the data. Every period
        /// boundary here is a clock time, so a wrong offset does not look wrong -- it produces a
        /// clean, plausible, silently misplaced profile.
        /// </summary>
        private bool ResolveTime()
        {
            if (_time != null && _time.Valid) return true;

            // The resolver needs history to work with. Failing once on a chart that was still
            // loading must not condemn the indicator for the rest of the session, so it is
            // retried as meaningful history arrives -- but not on every frame, because the
            // daily-halt scan walks every bar.
            if (_time != null && CurrentBar - _timeTriedAtBar < 200) return false;

            _timeTriedAtBar = CurrentBar;

            var window = new BarWindow(this);
            _time = TimeContext.Create(ZoneId, Clock, window, DateTime.UtcNow, 15);
            _timeError = _time.Valid ? null : _time.Error;

            return _time.Valid;
        }

        private DateTime LocalTime(int bar)
        {
            var candle = GetCandle(bar);
            return _time.ToLocal(candle.Time);
        }

        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansProfileIndicator _owner;

            public BarWindow(OceansProfileIndicator owner) { _owner = owner; }

            public int Count { get { return _owner.CurrentBar; } }
            public DateTime Time(int bar) { return _owner.GetCandle(bar).Time; }
            public decimal Open(int bar) { return _owner.GetCandle(bar).Open; }
            public decimal High(int bar) { return _owner.GetCandle(bar).High; }
            public decimal Low(int bar) { return _owner.GetCandle(bar).Low; }
            public decimal Close(int bar) { return _owner.GetCandle(bar).Close; }
        }

        #endregion

        #region Building

        /// <summary>
        /// Groups the visible bars into periods, newest last. Bars carry their own period key so
        /// a period that is only half on screen still gets all of its visible bars.
        /// </summary>
        private List<KeyValuePair<DateTime, List<int>>> VisiblePeriods()
        {
            var result = new List<KeyValuePair<DateTime, List<int>>>();

            if (_barPeriodBasis != Period)
            {
                _barPeriod.Clear();
                _cache.Clear();
                _barPeriodBasis = Period;
            }

            // Scrolling back over a long history would otherwise grow both caches without limit.
            if (_barPeriod.Count > 20000) _barPeriod.Clear();
            if (_cache.Count > 400) _cache.Clear();

            var first = FirstVisibleBarNumber;
            var last = LastVisibleBarNumber;

            if (first < 0) first = 0;
            if (last > CurrentBar - 1) last = CurrentBar - 1;
            if (last < first) return result;

            var index = new Dictionary<DateTime, List<int>>();

            for (var bar = first; bar <= last; bar++)
            {
                DateTime key;
                if (!_barPeriod.TryGetValue(bar, out key))
                {
                    key = PeriodClock.StartOf(LocalTime(bar), Period);
                    _barPeriod[bar] = key;
                }

                List<int> bars;
                if (!index.TryGetValue(key, out bars))
                {
                    bars = new List<int>();
                    index[key] = bars;
                    result.Add(new KeyValuePair<DateTime, List<int>>(key, bars));
                }

                bars.Add(bar);
            }

            result.Sort(delegate (KeyValuePair<DateTime, List<int>> a, KeyValuePair<DateTime, List<int>> b)
            {
                return a.Key.CompareTo(b.Key);
            });

            return result;
        }

        /// <summary>
        /// Builds a period's profile, or returns the cached one. The signature covers the bar
        /// range and the newest bar's volume, so the period in progress rebuilds as it fills and
        /// finished periods are only ever built once.
        /// </summary>
        private Cached Build(DateTime key, List<int> bars, int ticksPerRow)
        {
            if (bars == null || bars.Count == 0) return null;

            var firstBar = bars[0];
            var lastBar = bars[bars.Count - 1];
            var signature = GetCandle(lastBar).Volume + GetCandle(firstBar).Volume;

            Cached cached;
            if (_cache.TryGetValue(key, out cached)
                && cached.FirstBar == firstBar && cached.LastBar == lastBar
                && cached.Signature == signature && cached.TicksPerRow == ticksPerRow)
            {
                return cached;
            }

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m) return null;

            var builder = new ProfileBuilder();
            builder.Start = key;

            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                var candle = GetCandle(bar);
                builder.NoteBar(bar);
                builder.End = candle.LastTime;

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null) continue;
                    builder.Add(level.Price, level.Volume, level.Bid, level.Ask, level.Ticks);
                }
            }

            var profile = builder.Build(tick, MaxProfileTicks);
            if (profile == null) return null;

            ProfileMath.ComputeValueArea(profile, ValueAreaPercentage);

            var absorbed = ShowAbsorption
                ? ProfileMath.FindAbsorption(profile, AbsorbVolumePercent, AbsorbBalancePercent / 100m)
                : null;

            var highNodes = ShowHighNodes ? ProfileMath.FindHighVolumeNodes(profile, 70m) : null;
            var lowNodes = ShowLowNodes ? ProfileMath.FindLowVolumeNodes(profile, 20m) : null;

            Side[] stacked = null;
            if (ShowStacks)
            {
                var imbalances = ProfileMath.FindImbalances(profile, ImbalanceRatio, ImbalanceMinVolume);
                stacked = ProfileMath.StackSides(profile, ProfileMath.FindStacks(imbalances, StackRun));
            }

            cached = new Cached();
            cached.Profile = profile;
            cached.View = ProfileMath.BuildView(profile, ticksPerRow, absorbed, highNodes, lowNodes, stacked);
            cached.MaxRow = ProfileMath.MaxRowVolume(cached.View);

            // Levels too small to matter must not set the colour scale: one six-lot print is
            // perfectly one-sided and would flatten every real level to nothing.
            cached.LeanScale = ProfileMath.MaxLean(cached.View, cached.MaxRow / 20m);
            cached.Absorbed = absorbed == null
                ? new ProfileMath.KeyLevel[0]
                : Heaviest(ProfileMath.AbsorptionLevels(profile, absorbed), AbsorbCount);
            cached.FirstBar = firstBar;
            cached.LastBar = lastBar;
            cached.Signature = signature;
            cached.TicksPerRow = ticksPerRow;

            _cache[key] = cached;
            return cached;
        }

        /// <summary>The heaviest few shelves, so the chart gets a handful of lines and not a grid.</summary>
        private static ProfileMath.KeyLevel[] Heaviest(ProfileMath.KeyLevel[] levels, int keep)
        {
            if (levels == null || levels.Length <= keep) return levels;

            var order = new List<ProfileMath.KeyLevel>(levels);
            order.Sort(delegate (ProfileMath.KeyLevel a, ProfileMath.KeyLevel b)
            {
                return b.Volume.CompareTo(a.Volume);
            });

            order.RemoveRange(keep, order.Count - keep);
            return order.ToArray();
        }

        /// <summary>Merges several periods into one profile for the edge column.</summary>
        private Cached BuildMerged(List<KeyValuePair<DateTime, List<int>>> periods, int take, int ticksPerRow)
        {
            var bars = new List<int>();
            var from = periods.Count - take;
            if (from < 0) from = 0;

            for (var i = from; i < periods.Count; i++) bars.AddRange(periods[i].Value);
            if (bars.Count == 0) return null;

            bars.Sort();

            // DateTime.MaxValue keeps the merged entry clear of the per-period ones. The
            // signature in Build covers a changed span, so this must NOT drop the entry --
            // doing that rebuilt every bar's levels on every repaint.
            return Build(DateTime.MaxValue, bars, ticksPerRow);
        }

        #endregion

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            var container = chart == null ? null : chart.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;
            if (region.Width <= 0 || region.Height <= 0) return;

            if (CurrentBar < 2)
            {
                Status(context, region, "Ocean Profile: no bars loaded yet.");
                return;
            }

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m)
            {
                Status(context, region, "Ocean Profile: waiting for the instrument.");
                return;
            }

            // Quarter-hours, half-hours and hours land on the same instants whatever whole-hour
            // offset applies, so they are safe without a resolved bar clock. Anything coarser
            // shifts with the zone, and a shifted profile looks perfectly plausible -- so it is
            // refused rather than guessed.
            if (!ResolveTime() && PeriodClock.NeedsTimeZone(Period))
            {
                Status(context, region, _timeError);
                return;
            }

            var periods = VisiblePeriods();
            if (periods.Count == 0)
            {
                Status(context, region, "Ocean Profile: no bars in view.");
                return;
            }

            if (periods.Count > MaxVisiblePeriods)
            {
                Status(context, region, "Ocean Profile: " + periods.Count + " periods in view. " +
                                        "Zoom in, or choose a longer higher timeframe.");
                return;
            }

            var ticksPerRow = ProfileMath.TicksPerRow(container.PriceRowHeight, MinRowHeight);
            var rowPixels = (int)Math.Round(container.PriceRowHeight * ticksPerRow);
            if (rowPixels < 1) rowPixels = 1;

            context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);
            context.SetClip(region);

            try
            {
                if (Layout == ProfileLayout.Edge)
                    RenderEdge(context, container, region, periods, ticksPerRow, rowPixels);
                else
                    RenderPerPeriod(context, container, region, periods, ticksPerRow, rowPixels);
            }
            finally
            {
                context.ResetClip();
            }
        }

        #region Edge layout

        private void RenderEdge(RenderContext context, IChartContainer container, Rectangle region,
                                List<KeyValuePair<DateTime, List<int>>> periods,
                                int ticksPerRow, int rowPixels)
        {
            var cached = BuildMerged(periods, EdgePeriods, ticksPerRow);
            if (cached == null)
            {
                Status(context, region, "Ocean Profile: no traded volume in these periods.");
                return;
            }

            int left, right;
            bool growRight;

            if (Side == ProfileSide.Left)
            {
                left = region.Left;
                right = left + EdgeWidth;
                growRight = true;
            }
            else
            {
                right = region.Right;
                left = right - EdgeWidth;
                growRight = false;
            }

            if (left < region.Left) left = region.Left;
            if (right > region.Right) right = region.Right;
            if (right <= left) return;

            DrawColumn(context, container, region, cached, left, right, rowPixels, growRight, true);

            var y = region.Top + 2;

            if (ShowHeader)
            {
                var span = periods.Count < EdgePeriods ? periods.Count : EdgePeriods;
                var header = PeriodName() + " x" + span + "   vol " + ProfileMath.Compact(cached.Profile.TotalVolume) +
                             "   delta " + Signed(cached.Profile.TotalDelta);

                context.DrawString(header, _statusFont, Color.FromArgb(195, 195, 205), left + 2, y);
                y += (int)context.MeasureString(header, _statusFont).Height;
            }

            if (ShowVerdict) DrawVerdict(context, region, cached, left + 2, y);
        }

        #endregion

        #region Per-period layout

        private void RenderPerPeriod(RenderContext context, IChartContainer container, Rectangle region,
                                     List<KeyValuePair<DateTime, List<int>>> periods,
                                     int ticksPerRow, int rowPixels)
        {
            for (var p = 0; p < periods.Count; p++)
            {
                var bars = periods[p].Value;
                var cached = Build(periods[p].Key, bars, ticksPerRow);
                if (cached == null) continue;

                var startX = container.GetXByBar(bars[0], true);
                var endX = container.GetXByBar(bars[bars.Count - 1], false);
                if (endX <= startX) endX = startX + 1;

                var width = (endX - startX) * PeriodWidthPercent / 100;
                if (width < 4) width = 4;

                var left = startX;
                var right = left + width;
                if (right > region.Right) right = region.Right;
                if (left < region.Left) left = region.Left;
                if (right <= left) continue;

                DrawColumn(context, container, region, cached, left, right, rowPixels, true, false);

                if (!ShowHeader) continue;

                var label = PeriodClock.Label(periods[p].Key, Period);
                context.DrawString(label, _statusFont, Color.FromArgb(150, 150, 160), left + 2, region.Top + 2);
            }
        }

        #endregion

        #region Drawing one column

        private void DrawColumn(RenderContext context, IChartContainer container, Rectangle region,
                                Cached cached, int left, int right, int rowPixels,
                                bool growRight, bool wide)
        {
            var view = cached.View;
            var profile = cached.Profile;
            var width = right - left;
            if (cached.MaxRow <= 0m) return;

            var poc = ToColor(_pocColor, 255);
            var absorb = ToColor(_absorbColor, 255);

            // The value area is drawn first, as a wash behind everything else.
            if (ShowValueArea && profile.ValIndex >= 0 && profile.VahIndex >= 0)
            {
                var top = container.GetYByPrice(profile.ValueAreaHigh, false) - rowPixels / 2;
                var bottom = container.GetYByPrice(profile.ValueAreaLow, false) + rowPixels / 2;

                if (bottom > top && top < region.Bottom && bottom > region.Top)
                {
                    context.FillRectangle(ToColor(_valueAreaColor, 30),
                                          new Rectangle(left, top, width, bottom - top));
                }
            }

            var labelled = TopRows(view, ShowNumbers ? NumberCount : 0);

            for (var r = 0; r < view.Length; r++)
            {
                var row = view[r];
                if (row.Volume <= 0m) continue;

                var height = rowPixels - RowGap;
                if (height < 1) height = 1;

                var y = container.GetYByPrice(row.MidPrice, false) - height / 2;
                if (y + height < region.Top || y > region.Bottom) continue;

                var t = ProfileMath.Normalise(row.Volume, cached.MaxRow);
                var length = (int)Math.Round(width * t);
                if (length < 1) length = 1;

                var barLeft = growRight ? left : right - length;
                var rect = new Rectangle(barLeft, y, length, height);

                Rgb rgb;

                if (Style == HeatStyle.VolumeWithDelta)
                {
                    // Two bars on ONE scale. The grey bar is everything that traded; the
                    // coloured bar is how much of it leaned. The GAP between them is the part
                    // that changed hands without going anywhere -- so absorption is not a
                    // subtle hue you have to squint at, it is a long grey bar with almost no
                    // colour in it, and a level buyers really took is grey and green together.
                    var baseRgb = ToRgb(_neutralColor);
                    var baseAlpha = Blend(Opacity(MinOpacity), Opacity(MaxOpacity), t);
                    context.FillRectangle(Color.FromArgb(baseAlpha, baseRgb.R, baseRgb.G, baseRgb.B), rect);

                    var delta = row.Delta;
                    rgb = ToRgb(delta < 0m ? _sellColor : _buyColor);

                    var magnitude = delta < 0m ? -delta : delta;
                    var deltaLength = (int)Math.Round(width * ProfileMath.Normalise(magnitude, cached.MaxRow));

                    if (deltaLength > 0)
                    {
                        var deltaLeft = growRight ? left : right - deltaLength;
                        var deltaHeight = height > 3 ? height - 2 : height;
                        var deltaY = y + (height - deltaHeight) / 2;

                        context.FillRectangle(Color.FromArgb(235, rgb.R, rgb.G, rgb.B),
                                              new Rectangle(deltaLeft, deltaY, deltaLength, deltaHeight));
                    }
                    else
                    {
                        rgb = baseRgb;
                    }
                }
                else
                {
                    // Hue mode. Lean is scaled against the profile's own spread, not against
                    // an absolute 100%: merged over hours, buying and selling net off at every
                    // price and an absolute scale renders the entire column grey.
                    var lean = cached.LeanScale > 0m ? row.Lean / cached.LeanScale : 0m;
                    if (lean > 1m) lean = 1m;
                    if (lean < -1m) lean = -1m;

                    rgb = Lean(lean);
                    var alpha = Blend(Opacity(MinOpacity), Opacity(MaxOpacity), t);
                    var bright = Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
                    var dim = Color.FromArgb((byte)(alpha / 3), rgb.R, rgb.G, rgb.B);

                    if (growRight) context.FillRectangle(bright, dim, rect, false);
                    else context.FillRectangle(dim, bright, rect, false);
                }

                if (ShowAbsorption && row.Absorbed)
                {
                    context.DrawRectangle(new RenderPen(absorb, 1f), rect);
                }

                if (ShowStacks && row.Stacked != OceansProfile.Side.None)
                {
                    var side = row.Stacked == OceansProfile.Side.Buy
                             ? ToColor(_buyColor, 255) : ToColor(_sellColor, 255);

                    var tabX = growRight ? left : right - 3;
                    context.FillRectangle(side, new Rectangle(tabX, y, 3, height));
                }

                if (ShowHighNodes && row.HighNode)
                    context.DrawLine(new RenderPen(Color.FromArgb(150, 200, 200, 220), 1f),
                                     barLeft, y, barLeft + length, y);

                if (ShowLowNodes && row.LowNode)
                    context.DrawLine(new RenderPen(Color.FromArgb(120, 255, 140, 60), 1f),
                                     left, y + height / 2, left + width, y + height / 2);

                if (ShowPoc && row.HasPoc)
                    context.DrawLine(new RenderPen(poc, 2f), left, y + height / 2, left + width, y + height / 2);

                if (!ShowNumbers || !labelled.Contains(r)) continue;
                DrawNumber(context, region, row, rgb, barLeft, length, y, height, growRight, wide);
            }

            if (ShowUnfinished) MarkUnfinished(context, container, region, profile, left, right, rowPixels);

            // Only the edge column carries levels across. Per-period columns already sit at
            // their own bars, and a ray from each of twenty of them is a cross-hatch, not a chart.
            if (wide) DrawKeyLevels(context, container, region, cached, left, right, growRight);
        }

        /// <summary>
        /// Carries the handful of prices that actually matter out of the column and across the
        /// chart. This is the point of the whole indicator: a shelf where size was absorbed is
        /// only useful if you can see price approaching it.
        /// </summary>
        private void DrawKeyLevels(RenderContext context, IChartContainer container, Rectangle region,
                                   Cached cached, int left, int right, bool growRight)
        {
            var from = growRight ? right : region.Left;
            var to = growRight ? region.Right : left;
            if (to <= from) return;

            var profile = cached.Profile;

            if (ValueAreaRay && profile.ValIndex >= 0)
            {
                var wash = ToColor(_valueAreaColor, 110);
                Ray(context, container, region, profile.ValueAreaHigh, from, to, wash, 1f, true, "VAH");
                Ray(context, container, region, profile.ValueAreaLow, from, to, wash, 1f, true, "VAL");
            }

            if (PocRay && profile.PocIndex >= 0)
                Ray(context, container, region, profile.Poc, from, to, ToColor(_pocColor, 210), 1f, false, "POC");

            if (!AbsorptionRay || cached.Absorbed == null) return;

            for (var i = 0; i < cached.Absorbed.Length; i++)
            {
                var level = cached.Absorbed[i];
                var label = "ABS " + ProfileMath.Compact(level.Volume);

                Ray(context, container, region, level.Price, from, to,
                    ToColor(_absorbColor, 190), 1f, true, label);
            }
        }

        private void Ray(RenderContext context, IChartContainer container, Rectangle region,
                         decimal price, int from, int to, Color color, float width, bool dashed,
                         string tag)
        {
            var y = container.GetYByPrice(price, false);
            if (y < region.Top || y > region.Bottom) return;

            var pen = dashed
                ? new RenderPen(color, width, System.Drawing.Drawing2D.DashStyle.Dash)
                : new RenderPen(color, width);

            context.DrawLine(pen, from, y, to, y);

            if (!LabelRays) return;

            var text = tag + " " + ChartInfo.GetPriceString(price);
            var size = context.MeasureString(text, _font);

            // Against the right edge, where price is now, rather than back in the history.
            var x = to - size.Width - 4;
            if (x < from) x = from + 2;

            context.DrawString(text, _font, color, x, y - size.Height - 1);
        }

        private void DrawNumber(RenderContext context, Rectangle region, DisplayRow row, Rgb rgb,
                                int barLeft, int length, int y, int height, bool growRight, bool wide)
        {
            var text = ProfileMath.Compact(row.Volume);
            if (ShowDelta && wide) text = text + "  " + Signed(row.Delta);

            var size = context.MeasureString(text, _font);
            if (height < size.Height - 1) return;

            // Just past the end of its own bar, so the heaviest levels push their numbers
            // furthest and are the first thing the eye lands on.
            var x = growRight ? barLeft + length + 3 : barLeft - size.Width - 3;

            if (x + size.Width > region.Right) x = region.Right - size.Width - 2;
            if (x < region.Left) x = region.Left + 2;

            context.DrawString(text, _font, Color.FromArgb(255, rgb.R, rgb.G, rgb.B),
                               x, y + (height - size.Height) / 2);
        }

        private void MarkUnfinished(RenderContext context, IChartContainer container, Rectangle region,
                                    Profile profile, int left, int right, int rowPixels)
        {
            var pen = new RenderPen(Color.FromArgb(220, 255, 120, 200), 1f, System.Drawing.Drawing2D.DashStyle.Dot);

            if (profile.UnfinishedHigh)
            {
                var y = container.GetYByPrice(profile.PriceAt(profile.Count - 1), false);
                if (y >= region.Top && y <= region.Bottom) context.DrawLine(pen, left, y, right, y);
            }

            if (profile.UnfinishedLow)
            {
                var y = container.GetYByPrice(profile.LowPrice, false);
                if (y >= region.Top && y <= region.Bottom) context.DrawLine(pen, left, y, right, y);
            }
        }

        #endregion

        #region Helpers

        private struct Rgb
        {
            public byte R;
            public byte G;
            public byte B;
        }

        /// <summary>
        /// Sellers at one end, buyers at the other, balanced in the middle. The balanced colour
        /// is doing real work: heavy volume wearing it is size that traded without going
        /// anywhere, which is what absorption looks like.
        /// </summary>
        private Rgb Lean(decimal lean)
        {
            var from = _neutralColor;
            var to = lean < 0m ? _sellColor : _buyColor;

            var amount = lean < 0m ? -lean : lean;
            if (amount > 1m) amount = 1m;

            var rgb = new Rgb();
            rgb.R = Blend(from.R, to.R, amount);
            rgb.G = Blend(from.G, to.G, amount);
            rgb.B = Blend(from.B, to.B, amount);
            return rgb;
        }

        private static byte Blend(byte from, byte to, decimal amount)
        {
            if (amount <= 0m) return from;
            if (amount >= 1m) return to;

            var value = from + (to - from) * amount;
            var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);

            if (rounded < 0) rounded = 0;
            if (rounded > 255) rounded = 255;
            return (byte)rounded;
        }

        private static byte Opacity(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            return (byte)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        }

        private static Rgb ToRgb(MColor c)
        {
            var rgb = new Rgb();
            rgb.R = c.R;
            rgb.G = c.G;
            rgb.B = c.B;
            return rgb;
        }

        private static Color ToColor(MColor c, byte alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        private static string Signed(decimal value)
        {
            return (value > 0m ? "+" : value < 0m ? "-" : "") + ProfileMath.Compact(value < 0m ? -value : value);
        }

        private string PeriodName()
        {
            var display = typeof(ProfilePeriod).GetField(Period.ToString());
            if (display == null) return Period.ToString();

            var attributes = display.GetCustomAttributes(typeof(DisplayAttribute), false);
            if (attributes.Length == 0) return Period.ToString();

            return ((DisplayAttribute)attributes[0]).Name;
        }

        /// <summary>The n heaviest drawn rows -- labelling every level is labelling none.</summary>
        private static HashSet<int> TopRows(DisplayRow[] view, int n)
        {
            var set = new HashSet<int>();
            if (view == null || n <= 0) return set;

            var order = new List<int>(view.Length);
            for (var i = 0; i < view.Length; i++)
            {
                if (view[i].Volume > 0m) order.Add(i);
            }

            order.Sort(delegate (int a, int b)
            {
                var cmp = view[b].Volume.CompareTo(view[a].Volume);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            for (var i = 0; i < order.Count && i < n; i++) set.Add(order[i]);
            return set;
        }

        /// <summary>
        /// The profile, in words. Everything above is a picture you have to interpret; this is
        /// the one line that says where price stands and what is waiting either side of it.
        /// </summary>
        private void DrawVerdict(RenderContext context, Rectangle region, Cached cached, int x, int y)
        {
            var profile = cached.Profile;
            if (profile.PocIndex < 0 || CurrentBar < 1) return;

            var price = GetCandle(CurrentBar - 1).Close;
            var lines = new List<KeyValuePair<string, Color>>();

            if (profile.ValIndex >= 0 && profile.VahIndex >= 0)
            {
                var high = profile.ValueAreaHigh;
                var low = profile.ValueAreaLow;

                if (price > high)
                    lines.Add(Line("ABOVE VALUE  " + ChartInfo.GetPriceString(low) + "-" + ChartInfo.GetPriceString(high),
                                   ToColor(_buyColor, 255)));
                else if (price < low)
                    lines.Add(Line("BELOW VALUE  " + ChartInfo.GetPriceString(low) + "-" + ChartInfo.GetPriceString(high),
                                   ToColor(_sellColor, 255)));
                else
                    lines.Add(Line("IN VALUE  " + ChartInfo.GetPriceString(low) + "-" + ChartInfo.GetPriceString(high),
                                   Color.FromArgb(200, 200, 210)));
            }

            var toPoc = profile.Poc - price;
            lines.Add(Line("POC " + ChartInfo.GetPriceString(profile.Poc) + "  " + Signed(toPoc) + " away",
                           ToColor(_pocColor, 255)));

            // The nearest absorbed shelf each way -- the two prices most likely to matter next.
            if (cached.Absorbed != null && cached.Absorbed.Length > 0)
            {
                decimal above = 0m, below = 0m;

                for (var i = 0; i < cached.Absorbed.Length; i++)
                {
                    var p = cached.Absorbed[i].Price;
                    if (p > price && (above == 0m || p < above)) above = p;
                    if (p < price && (below == 0m || p > below)) below = p;
                }

                var text = "absorbed";
                if (below != 0m) text += "  v " + ChartInfo.GetPriceString(below);
                if (above != 0m) text += "  ^ " + ChartInfo.GetPriceString(above);

                if (below != 0m || above != 0m)
                    lines.Add(Line(text, ToColor(_absorbColor, 235)));
            }

            var lean = profile.TotalVolume <= 0m ? 0m : profile.TotalDelta / profile.TotalVolume;
            var absLean = lean < 0m ? -lean : lean;

            if (absLean < 0.02m)
                lines.Add(Line("two-sided, nobody won it", Color.FromArgb(190, 190, 200)));
            else
                lines.Add(Line(lean > 0m ? "buyers carried the period" : "sellers carried the period",
                               lean > 0m ? ToColor(_buyColor, 255) : ToColor(_sellColor, 255)));

            for (var i = 0; i < lines.Count; i++)
            {
                context.DrawString(lines[i].Key, _font, lines[i].Value, x, y);
                y += (int)context.MeasureString(lines[i].Key, _font).Height;
            }
        }

        private static KeyValuePair<string, Color> Line(string text, Color color)
        {
            return new KeyValuePair<string, Color>(text, color);
        }

        private void Status(RenderContext context, Rectangle region, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            context.DrawString(text, _statusFont, Color.FromArgb(230, 170, 80),
                               region.Left + 6, region.Top + 4);
        }

        #endregion
    }
}
