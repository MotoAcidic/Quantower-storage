using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;
using MarketByOrder = ATAS.DataFeedsCore.MarketByOrder;
using MboUpdateType = ATAS.DataFeedsCore.MarketByOrderUpdateTypes;
using FeedDataType = ATAS.DataFeedsCore.MarketDataType;

namespace OceansDepth
{
    /// <summary>Where the resting liquidity is read from.</summary>
    public enum DepthSource
    {
        /// <summary>Aggregated depth of market -- total size per price. Every feed has this.</summary>
        [Display(Name = "Depth of market (aggregated)")] Dom,

        /// <summary>
        /// Market by order -- every individual order. Only some feeds carry it; when it is not
        /// there the strip says so instead of quietly drawing aggregated depth instead.
        /// </summary>
        [Display(Name = "Market by order (per order)")] Mbo
    }

    /// <summary>How a level is drawn.</summary>
    public enum StripStyle
    {
        [Display(Name = "Bars")] Bars,
        [Display(Name = "Heat blocks")] Heat,
        [Display(Name = "Bars over heat")] Both
    }

    /// <summary>What the gradient is measured against.</summary>
    public enum ScaleMode
    {
        /// <summary>One scale for both sides, so a heavy offer really does out-glow a light bid.</summary>
        [Display(Name = "Shared (bids vs asks comparable)")] Shared,

        /// <summary>Each side scaled to its own biggest level.</summary>
        [Display(Name = "Per side")] PerSide
    }

    /// <summary>Which levels get a printed number.</summary>
    public enum ValueLabels
    {
        [Display(Name = "None")] None,
        [Display(Name = "Biggest levels only")] Top,
        [Display(Name = "Every level")] All
    }

    /// <summary>
    /// Ocean Depth -- a narrow gradient liquidity strip pinned to the right edge of the price
    /// panel, showing where the resting size is on each side with the number written next to it.
    ///
    /// The point of it is that there is nothing to adjust. Row height, row bucketing and the
    /// colour scale are all re-derived from the live chart geometry on every frame, so the same
    /// settings work on a 1 minute chart, a 1 hour chart, and at any zoom in between.
    /// </summary>
    [DisplayName("Oceans Depth")]
    [Category("Ocean")]
    public class OceansDepthIndicator : Indicator
    {
        private const int MaxRows = 4000;

        /// <summary>How long a market-by-order subscription is given before it is called empty.</summary>
        private const int MboGraceMs = 6000;

        private sealed class MboOrder
        {
            public decimal Price;
            public decimal Volume;
            public bool IsAsk;
        }

        private readonly Dictionary<long, MboOrder> _mbo = new Dictionary<long, MboOrder>();
        private readonly object _mboLock = new object();
        private bool _mboRequested;
        private long _mboRequestedMs;
        private bool _mboEverSeen;

        private readonly PeakBook _peaks = new PeakBook();

        private readonly object _addLock = new object();
        private long _depthUpdates;
        private long _mboUpdates;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly Action _tick;
        private TimeSpan _timerPeriod;
        private bool _timerOn;
        private Rectangle _lastRegion;

        private RenderFont _font;
        private int _fontSize = 8;
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        private MColor _bidLow = MColor.FromRgb(10, 60, 45);
        private MColor _bidHigh = MColor.FromRgb(40, 255, 150);
        private MColor _askLow = MColor.FromRgb(70, 20, 30);
        private MColor _askHigh = MColor.FromRgb(255, 70, 95);

        public OceansDepthIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;

            // Nothing is plotted per bar -- the whole indicator is the live book. The inherited
            // series is hidden rather than removed so ATAS still has the series it expects.
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
            _tick = new Action(OnTimerTick);
        }

        #region Settings -- source

        [Display(Name = "Liquidity source", GroupName = "01 Source", Order = 100)]
        public DepthSource Source { get; set; } = DepthSource.Dom;

        [Display(Name = "Size shown", GroupName = "01 Source", Order = 110,
                 Description = "Total size at the price, or the single biggest order there. " +
                               "Biggest order needs market by order data.")]
        public DepthMetric Metric { get; set; } = DepthMetric.TotalSize;

        [Display(Name = "Refresh (ms)", GroupName = "01 Source", Order = 120,
                 Description = "How often the strip is redrawn, and therefore how often the book " +
                               "is sampled. Size that appears and vanishes faster than this is missed.")]
        [Range(50, 2000)]
        public int RefreshMs { get; set; } = 200;

        [Display(Name = "Levels per side (0 = all)", GroupName = "01 Source", Order = 130,
                 Description = "Caps how many price levels either side of the market are drawn. " +
                               "The feed's own depth limit still applies on top of this.")]
        [Range(0, 500)]
        public int LevelsPerSide { get; set; } = 0;

        #endregion

        #region Settings -- layout

        [Display(Name = "Strip width (px)", GroupName = "02 Layout", Order = 200)]
        [Range(20, 600)]
        public int StripWidth { get; set; } = 90;

        [Display(Name = "Right margin (px)", GroupName = "02 Layout", Order = 210)]
        [Range(0, 400)]
        public int RightMargin { get; set; } = 0;

        [Display(Name = "Minimum row height (px)", GroupName = "02 Layout", Order = 220,
                 Description = "Ticks are merged into one row until the row is at least this tall. " +
                               "This is what makes the strip fit any timeframe and zoom by itself.")]
        [Range(1, 40)]
        public int MinRowHeight { get; set; } = 3;

        [Display(Name = "Row gap (px)", GroupName = "02 Layout", Order = 230)]
        [Range(0, 6)]
        public int RowGap { get; set; } = 1;

        [Display(Name = "Style", GroupName = "02 Layout", Order = 240)]
        public StripStyle Style { get; set; } = StripStyle.Both;

        #endregion

        #region Settings -- gradient

        [Display(Name = "Small", GroupName = "03 Bid gradient", Order = 300)]
        public MColor BidLow
        {
            get { return _bidLow; }
            set { _bidLow = value; }
        }

        [Display(Name = "Large", GroupName = "03 Bid gradient", Order = 310)]
        public MColor BidHigh
        {
            get { return _bidHigh; }
            set { _bidHigh = value; }
        }

        [Display(Name = "Small", GroupName = "04 Ask gradient", Order = 400)]
        public MColor AskLow
        {
            get { return _askLow; }
            set { _askLow = value; }
        }

        [Display(Name = "Large", GroupName = "04 Ask gradient", Order = 410)]
        public MColor AskHigh
        {
            get { return _askHigh; }
            set { _askHigh = value; }
        }

        [Display(Name = "Opacity of the smallest (%)", GroupName = "05 Intensity", Order = 500)]
        [Range(0, 100)]
        public int MinOpacity { get; set; } = 22;

        [Display(Name = "Opacity of the largest (%)", GroupName = "05 Intensity", Order = 510)]
        [Range(0, 100)]
        public int MaxOpacity { get; set; } = 96;

        [Display(Name = "Curve", GroupName = "05 Intensity", Order = 520,
                 Description = "Bends the gradient. Emphasise large hides the noise and lights up " +
                               "only real outliers.")]
        public IntensityCurve Curve { get; set; } = IntensityCurve.EmphasiseLarge;

        [Display(Name = "Scale", GroupName = "05 Intensity", Order = 530)]
        public ScaleMode Scale { get; set; } = ScaleMode.Shared;

        #endregion

        #region Settings -- numbers

        [Display(Name = "Show numbers", GroupName = "06 Numbers", Order = 600)]
        public ValueLabels NumberMode { get; set; } = ValueLabels.Top;

        [Display(Name = "How many per side", GroupName = "06 Numbers", Order = 610)]
        [Range(1, 60)]
        public int LabelCount { get; set; } = 6;

        [Display(Name = "Text size", GroupName = "06 Numbers", Order = 620)]
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

        [Display(Name = "Add order count", GroupName = "06 Numbers", Order = 630,
                 Description = "Writes size and the number of orders making it up, as 240/3. " +
                               "Needs market by order data; nothing is added without it.")]
        public bool ShowOrderCount { get; set; } = false;

        #endregion

        #region Settings -- highlight

        [Display(Name = "Outline the biggest level each side", GroupName = "07 Highlight", Order = 700)]
        public bool HighlightTop { get; set; } = true;

        [Display(Name = "Run a line across the chart", GroupName = "07 Highlight", Order = 710)]
        public bool TopRay { get; set; } = true;

        [Display(Name = "Line width", GroupName = "07 Highlight", Order = 720)]
        [Range(1, 5)]
        public int RayWidth { get; set; } = 1;

        [Display(Name = "Only when it is this many times the average", GroupName = "07 Highlight", Order = 730,
                 Description = "1 always marks the biggest level. Higher values keep the chart " +
                               "clean until something genuinely outsized shows up.")]
        [Range(1, 20)]
        public int RayThreshold { get; set; } = 3;

        #endregion

        #region Settings -- persistence

        [Display(Name = "Hold a pulled level (ms)", GroupName = "08 Persistence", Order = 800,
                 Description = "Size that disappears keeps its full brightness this long. " +
                               "Set both this and the fade to 0 for the raw book.")]
        [Range(0, 60000)]
        public int HoldMs
        {
            get { return _peaks.HoldMs; }
            set { _peaks.HoldMs = value < 0 ? 0 : value; }
        }

        [Display(Name = "Then fade over (ms)", GroupName = "08 Persistence", Order = 810)]
        [Range(0, 120000)]
        public int FadeMs
        {
            get { return _peaks.FadeMs; }
            set { _peaks.FadeMs = value < 0 ? 0 : value; }
        }

        #endregion

        #region Settings -- status

        [Display(Name = "Show feed counts", GroupName = "09 Status", Order = 905,
                 Description = "Adds a line counting aggregated depth updates and per-order " +
                               "updates received. The quickest way to see what this feed carries.")]
        public bool ShowFeedCounts { get; set; } = true;

        [Display(Name = "Show header", GroupName = "09 Status", Order = 900,
                 Description = "One line above the strip: source, how many levels the feed is " +
                               "publishing, and the size the gradient is scaled to.")]
        public bool ShowHeader { get; set; } = true;

        #endregion

        protected override void OnCalculate(int bar, decimal value)
        {
            // Nothing per bar. The strip is built entirely from the live book in OnRender.
        }

        #region Market by order book

        protected override void OnMarketByOrdersChanged(IEnumerable<MarketByOrder> values)
        {
            if (values == null) return;

            lock (_mboLock)
            {
                foreach (var order in values)
                {
                    if (order == null) continue;

                    if (order.Type == MboUpdateType.Delete)
                    {
                        _mbo.Remove(order.ExchangeOrderId);
                        continue;
                    }

                    MboOrder held;
                    if (!_mbo.TryGetValue(order.ExchangeOrderId, out held))
                    {
                        held = new MboOrder();
                        _mbo[order.ExchangeOrderId] = held;
                    }

                    held.Price = order.Price;
                    held.Volume = order.Volume;
                    held.IsAsk = order.Side == FeedDataType.Ask;
                }

                _mboUpdates++;
                if (_mbo.Count > 0) _mboEverSeen = true;
            }
        }

        /// <summary>
        /// Collapses the per-order book to one entry per price, carrying the biggest single order
        /// and the order count -- the two things aggregated depth cannot tell you.
        /// </summary>
        private List<DepthLevel> MboLevels()
        {
            var byPrice = new Dictionary<decimal, DepthLevel>();

            lock (_mboLock)
            {
                foreach (var pair in _mbo)
                {
                    var order = pair.Value;
                    if (order.Volume <= 0m) continue;

                    // Price alone is not a key: the same price can be a bid and an ask across a
                    // crossed or stale book, and merging the two would invent liquidity.
                    var key = order.IsAsk ? order.Price : -order.Price;

                    DepthLevel level;
                    if (!byPrice.TryGetValue(key, out level))
                    {
                        level = new DepthLevel(order.Price, 0m, order.IsAsk);
                    }

                    level.Size += order.Volume;
                    level.Orders += 1;
                    if (order.Volume > level.Largest) level.Largest = order.Volume;

                    byPrice[key] = level;
                }
            }

            var result = new List<DepthLevel>(byPrice.Count);
            foreach (var pair in byPrice) result.Add(pair.Value);
            return result;
        }

        /// <summary>
        /// ATAS's own DOM subscribes to per-order data from OnInitialize, and so does this. The
        /// request was originally made from OnRender, which is the wrong thread and the wrong
        /// point in the indicator's life for a data subscription.
        /// </summary>
        protected override void OnInitialize()
        {
            base.OnInitialize();
            RequestMbo();
        }

        private void RequestMbo()
        {
            if (_mboRequested) return;

            _mboRequested = true;
            _mboRequestedMs = _clock.ElapsedMilliseconds;

            try { SubscribeMarketByOrderData(); }
            catch (Exception) { /* reported on the chart as "no data", not swallowed silently */ }
        }

        #endregion

        #region Depth of market

        /// <summary>
        /// One aggregated depth update. Counted only, so the header can say what this feed is
        /// actually sending -- the sizes themselves are read from the book snapshot at draw time.
        /// </summary>
        protected override void MarketDepthChanged(MarketDataArg depth)
        {
            base.MarketDepthChanged(depth);
            Absorb(depth);
        }

        protected override void MarketDepthsChanged(IEnumerable<MarketDataArg> depths)
        {
            base.MarketDepthsChanged(depths);
            if (depths == null) return;

            foreach (var depth in depths) Absorb(depth);
        }

        private void Absorb(MarketDataArg depth)
        {
            if (depth == null) return;
            if (!depth.IsAsk && !depth.IsBid) return;

            lock (_addLock) { _depthUpdates++; }
        }

        private List<DepthLevel> DomLevels()
        {
            var levels = new List<DepthLevel>(64);

            var provider = MarketDepthInfo;
            if (provider == null) return levels;

            var snapshot = provider.GetMarketDepthSnapshot();
            if (snapshot == null) return levels;

            foreach (var arg in snapshot)
            {
                if (arg == null) continue;
                if (!arg.IsAsk && !arg.IsBid) continue;
                if (arg.Volume <= 0m) continue;

                levels.Add(new DepthLevel(arg.Price, arg.Volume, arg.IsAsk));
            }

            return levels;
        }

        #endregion

        #region Redraw pacing

        /// <summary>
        /// The chart repaints when bars change; the book changes far more often than that. This
        /// drives the repaint instead, at a rate the settings control.
        /// </summary>
        private void EnsureTimer()
        {
            var wanted = TimeSpan.FromMilliseconds(RefreshMs);
            if (_timerOn && wanted == _timerPeriod) return;

            if (_timerOn)
            {
                try { UnsubscribeFromTimer(_timerPeriod, _tick); }
                catch (Exception) { }
            }

            _timerPeriod = wanted;
            _timerOn = true;

            try { SubscribeToTimer(_timerPeriod, _tick); }
            catch (Exception) { _timerOn = false; }
        }

        private void OnTimerTick()
        {
            var region = _lastRegion;
            if (region.Width <= 0 || region.Height <= 0) return;

            var arg = new RedrawArg(region);
            arg.ForceRedraw = true;
            RedrawChart(arg);
        }

        public override void Dispose()
        {
            if (_timerOn)
            {
                try { UnsubscribeFromTimer(_timerPeriod, _tick); }
                catch (Exception) { }
                _timerOn = false;
            }

            base.Dispose();
        }

        #endregion

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            var container = chart == null ? null : chart.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;
            if (region.Width <= 0 || region.Height <= 0) return;

            _lastRegion = region;
            EnsureTimer();

            if (Source == DepthSource.Mbo) RequestMbo();

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m)
            {
                Status(context, region, "Oceans Depth: waiting for the instrument.");
                return;
            }

            var raw = Source == DepthSource.Mbo ? MboLevels() : DomLevels();

            // No silent substitution. If per-order data was asked for and the feed is not
            // sending it, that is what the chart says -- it does not quietly show aggregated
            // depth under a market-by-order label. A subscription takes a moment to fill, so
            // the first few seconds say "waiting" rather than accusing the feed.
            if (Source == DepthSource.Mbo && raw.Count == 0 && !_mboEverSeen)
            {
                var waited = _clock.ElapsedMilliseconds - _mboRequestedMs;

                // The depth counter is the useful half of this message. Aggregated updates
                // arriving while per-order updates stay at zero is proof the feed carries no
                // market-by-order data; both at zero means nothing is reaching the indicator
                // at all, which is a different problem with a different fix.
                Status(context, region, waited < MboGraceMs
                    ? "Oceans Depth: waiting for market-by-order data."
                    : "Oceans Depth: no market-by-order data on this feed (" +
                      _depthUpdates + " depth updates, " + _mboUpdates + " order updates).");
                return;
            }

            if (Metric == DepthMetric.LargestOrder && Source == DepthSource.Dom)
            {
                Status(context, region, "Oceans Depth: biggest-order size needs the market-by-order source.");
                return;
            }

            var now = _clock.ElapsedMilliseconds;

            if (LevelsPerSide > 0) raw = NearestLevels(raw, LevelsPerSide);

            _peaks.Observe(raw, now);

            var levels = (HoldMs > 0 || FadeMs > 0) ? _peaks.Emit(now) : raw;

            var pixelsPerTick = container.PriceRowHeight;
            var ticksPerRow = RowGrid.TicksPerRow(pixelsPerTick, MinRowHeight);
            var grid = RowGrid.Build(container.Low, container.High, tick, ticksPerRow, MaxRows);
            if (grid == null)
            {
                Status(context, region, "Oceans Depth: the chart has no price range yet.");
                return;
            }

            var span = grid.RowSize * grid.Count;
            _peaks.Prune(grid.Anchor - span, grid.Anchor + span * 2m);

            var rows = grid.Aggregate(levels);

            var bidRef = DepthMath.Max(rows, Metric, false);
            var askRef = DepthMath.Max(rows, Metric, true);
            if (Scale == ScaleMode.Shared)
            {
                var shared = bidRef > askRef ? bidRef : askRef;
                bidRef = shared;
                askRef = shared;
            }

            if (bidRef <= 0m && askRef <= 0m)
            {
                Status(context, region, "Oceans Depth: no resting size in view.");
                return;
            }

            var right = region.Right - RightMargin;
            var left = right - StripWidth;
            if (left < region.Left + 2) left = region.Left + 2;
            if (right <= left) return;

            var rowPixels = (int)Math.Round(pixelsPerTick * ticksPerRow);
            if (rowPixels < 1) rowPixels = 1;

            var topBid = DepthMath.TopIndices(rows, Metric, false, NumberMode == ValueLabels.Top ? LabelCount : 1);
            var topAsk = DepthMath.TopIndices(rows, Metric, true, NumberMode == ValueLabels.Top ? LabelCount : 1);

            context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);
            context.SetClip(region);

            try
            {
                DrawSide(context, container, region, rows, false, bidRef, left, right, rowPixels, topBid);
                DrawSide(context, container, region, rows, true, askRef, left, right, rowPixels, topAsk);

                if (HighlightTop || TopRay)
                {
                    MarkTop(context, container, region, rows, false, bidRef, left, right, rowPixels, topBid);
                    MarkTop(context, container, region, rows, true, askRef, left, right, rowPixels, topAsk);
                }
            }
            finally
            {
                context.ResetClip();
            }

            if (ShowHeader) Header(context, region, left, raw.Count, bidRef, askRef);
        }

        #region Drawing

        private void DrawSide(RenderContext context, IChartContainer container, Rectangle region,
                              DepthRow[] rows, bool isAsk, decimal reference,
                              int left, int right, int rowPixels, int[] top)
        {
            if (reference <= 0m) return;

            var width = right - left;
            var lo = ToRgb(isAsk ? _askLow : _bidLow);
            var hi = ToRgb(isAsk ? _askHigh : _bidHigh);

            var labelled = new HashSet<int>();
            if (NumberMode == ValueLabels.Top)
            {
                for (var i = 0; i < top.Length; i++) labelled.Add(top[i]);
            }

            for (var i = 0; i < rows.Length; i++)
            {
                var value = rows[i].Value(Metric, isAsk);
                if (value <= 0m) continue;

                var height = rowPixels - RowGap;
                if (height < 1) height = 1;

                var y = container.GetYByPrice(rows[i].Mid, false) - height / 2;

                // A row wide enough to hold both sides only happens when the chart is zoomed
                // right out. Splitting it keeps both readable instead of one hiding the other.
                if (rows[i].Value(Metric, false) > 0m && rows[i].Value(Metric, true) > 0m && height >= 2)
                {
                    var half = height / 2;
                    if (isAsk) height = half;
                    else { y += half; height = height - half; }
                }

                if (y + height < region.Top || y > region.Bottom) continue;

                var t = DepthMath.Shape(DepthMath.Normalise(value, reference), Curve);
                var rgb = DepthMath.Lerp(lo, hi, t);
                var alpha = DepthMath.LerpByte(Opacity(MinOpacity), Opacity(MaxOpacity), t);

                var bright = Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
                var dim = Color.FromArgb((byte)(alpha / 3), rgb.R, rgb.G, rgb.B);

                if (Style != StripStyle.Bars)
                {
                    var heatAlpha = Style == StripStyle.Both ? (byte)(alpha / 2) : alpha;
                    var heat = Color.FromArgb(heatAlpha, rgb.R, rgb.G, rgb.B);
                    context.FillRectangle(heat, new Rectangle(left, y, width, height));
                }

                var barLeft = left;
                if (Style != StripStyle.Heat)
                {
                    var length = (int)Math.Round(width * t);
                    if (length < 1) length = 1;

                    barLeft = right - length;
                    context.FillRectangle(dim, bright, new Rectangle(barLeft, y, length, height), false);
                }

                if (NumberMode == ValueLabels.None) continue;
                if (NumberMode == ValueLabels.Top && !labelled.Contains(i)) continue;

                DrawValue(context, region, rows[i], isAsk, value, rgb, barLeft, left, y, height);
            }
        }

        /// <summary>
        /// The number sits just outside the left end of its own bar, so the biggest levels push
        /// their numbers furthest into the chart and are the first thing the eye lands on.
        /// </summary>
        private void DrawValue(RenderContext context, Rectangle region, DepthRow row, bool isAsk,
                               decimal value, Rgb rgb, int barLeft, int stripLeft, int y, int height)
        {
            var text = DepthMath.Compact(value);

            if (ShowOrderCount)
            {
                var orders = isAsk ? row.AskOrders : row.BidOrders;
                if (orders > 0) text = text + "/" + orders;
            }

            var size = context.MeasureString(text, _font);
            if (height < size.Height - 1) return;

            var x = barLeft - size.Width - 3;
            if (x < region.Left) x = stripLeft + 2;

            var textY = y + (height - size.Height) / 2;
            var color = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

            context.DrawString(text, _font, color, x, textY);
        }

        /// <summary>
        /// Outlines the biggest level a side has, and optionally runs a line back across the
        /// chart from it -- the part that is meant to catch the eye without adding clutter.
        /// </summary>
        private void MarkTop(RenderContext context, IChartContainer container, Rectangle region,
                             DepthRow[] rows, bool isAsk, decimal reference,
                             int left, int right, int rowPixels, int[] top)
        {
            if (top.Length == 0 || reference <= 0m) return;

            var index = top[0];
            var value = rows[index].Value(Metric, isAsk);
            if (value <= 0m) return;

            if (RayThreshold > 1 && value < Average(rows, isAsk) * RayThreshold) return;

            var height = rowPixels - RowGap;
            if (height < 1) height = 1;

            var y = container.GetYByPrice(rows[index].Mid, false) - height / 2;
            if (y + height < region.Top || y > region.Bottom) return;

            var rgb = ToRgb(isAsk ? _askHigh : _bidHigh);
            var color = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

            if (HighlightTop)
            {
                var box = height < 3 ? 3 : height;
                context.DrawRectangle(new RenderPen(color, 1f),
                                      new Rectangle(left, y, right - left, box));
            }

            if (TopRay)
            {
                var mid = y + height / 2;
                context.DrawLine(new RenderPen(Color.FromArgb(150, rgb.R, rgb.G, rgb.B), RayWidth),
                                 region.Left, mid, left, mid);
            }
        }

        private decimal Average(DepthRow[] rows, bool isAsk)
        {
            var total = 0m;
            var count = 0;

            for (var i = 0; i < rows.Length; i++)
            {
                var v = rows[i].Value(Metric, isAsk);
                if (v <= 0m) continue;

                total += v;
                count++;
            }

            return count == 0 ? 0m : total / count;
        }

        private void Header(RenderContext context, Rectangle region, int left, int levelCount,
                            decimal bidRef, decimal askRef)
        {
            var reference = bidRef > askRef ? bidRef : askRef;
            var source = Source == DepthSource.Mbo ? "MBO" : "DOM";

            if (Metric == DepthMetric.LargestOrder) source = source + " ord";

            var text = source + " " + levelCount + " lvl  max " + DepthMath.Compact(reference);
            context.DrawString(text, _statusFont, Color.FromArgb(190, 190, 200),
                               left, region.Top + 2);

            if (!ShowFeedCounts) return;

            var counts = "depth " + _depthUpdates + "  order " + _mboUpdates;
            var size = context.MeasureString(counts, _statusFont);
            context.DrawString(counts, _statusFont, Color.FromArgb(140, 140, 150),
                               left, region.Top + 2 + size.Height);
        }

        private void Status(RenderContext context, Rectangle region, string text)
        {
            var size = context.MeasureString(text, _statusFont);
            var x = region.Right - size.Width - 6;
            if (x < region.Left) x = region.Left + 4;

            context.DrawString(text, _statusFont, Color.FromArgb(230, 170, 80), x, region.Top + 4);
        }

        #endregion

        #region Helpers

        /// <summary>Keeps only the n levels nearest the market on each side.</summary>
        private static List<DepthLevel> NearestLevels(List<DepthLevel> levels, int n)
        {
            var bids = new List<DepthLevel>();
            var asks = new List<DepthLevel>();

            for (var i = 0; i < levels.Count; i++)
            {
                if (levels[i].IsAsk) asks.Add(levels[i]);
                else bids.Add(levels[i]);
            }

            bids.Sort(delegate (DepthLevel a, DepthLevel b) { return b.Price.CompareTo(a.Price); });
            asks.Sort(delegate (DepthLevel a, DepthLevel b) { return a.Price.CompareTo(b.Price); });

            if (bids.Count > n) bids.RemoveRange(n, bids.Count - n);
            if (asks.Count > n) asks.RemoveRange(n, asks.Count - n);

            bids.AddRange(asks);
            return bids;
        }

        private static byte Opacity(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            return (byte)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero);
        }

        private static Rgb ToRgb(MColor c)
        {
            return new Rgb(c.R, c.G, c.B);
        }

        #endregion
    }
}
