using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansDom
{
    /// <summary>Which edge of the price panel the ladder is pinned to.</summary>
    public enum LadderSide
    {
        [Display(Name = "Right")] Right,
        [Display(Name = "Left")] Left
    }

    /// <summary>
    /// Ocean DOM -- a price ladder pinned to the price panel that puts the two facts about a
    /// price on the same row: what is resting there, and what has actually traded there.
    ///
    /// Nothing here is predicted. The ladder shows measurements and the five states a tracked
    /// level can be in, and it spends its one loud channel on the state that says the trade is
    /// wrong. When there is nothing to say the whole panel dims, because a screen that looks
    /// equally busy with no setup on it is a screen that manufactures trades.
    /// </summary>
    [DisplayName("Oceans DOM")]
    [Category("Ocean")]
    public class OceansDomIndicator : Indicator
    {
        private const int MaxRows = 4000;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TapeBook _tape = new TapeBook();

        private readonly List<LevelWatch> _watches = new List<LevelWatch>();
        private readonly List<LevelState> _states = new List<LevelState>();
        private string _watchedText = null;
        private List<string> _unreadable = new List<string>();

        private readonly Action _tick;
        private TimeSpan _timerPeriod;
        private bool _timerOn;
        private Rectangle _lastRegion;

        private RenderFont _font = new RenderFont("Arial", 8f);
        private readonly RenderFont _chipFont = new RenderFont("Arial", 10f);
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        private MColor _bidColor = MColor.FromRgb(40, 200, 130);
        private MColor _askColor = MColor.FromRgb(230, 80, 100);
        private MColor _tapeBidColor = MColor.FromRgb(30, 130, 200);
        private MColor _tapeAskColor = MColor.FromRgb(190, 140, 40);
        private MColor _loudColor = MColor.FromRgb(255, 210, 60);

        private string _logPath;
        private bool _logBroken;

        public OceansDomIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;

            // Nothing is plotted per bar -- the whole indicator is the live ladder. The inherited
            // series is hidden rather than removed so ATAS still has the series it expects.
            var series = DataSeries[0] as ValueDataSeries;
            if (series != null)
            {
                series.VisualType = VisualMode.Hide;
                series.IsHidden = true;
                series.ShowZeroValue = false;

                // Nothing is ever written to it, so every value is zero. Left scaling, a hidden
                // series of zeros drags the price axis down to include zero.
                series.ScaleIt = false;
            }

            _tick = OnTimerTick;
        }

        #region Settings -- ladder

        [Display(Name = "Side", GroupName = "01 Ladder", Order = 100)]
        public LadderSide Side { get; set; } = LadderSide.Right;

        [Display(Name = "Panel width (px)", GroupName = "01 Ladder", Order = 110)]
        [Range(120, 900)]
        public int PanelWidth { get; set; } = 300;

        [Display(Name = "Edge margin (px)", GroupName = "01 Ladder", Order = 120)]
        [Range(0, 400)]
        public int EdgeMargin { get; set; } = 60;

        [Display(Name = "Minimum row height (px)", GroupName = "01 Ladder", Order = 130,
                 Description = "Ticks are merged into one row until a row is at least this tall.")]
        [Range(1, 60)]
        public int MinRowHeight { get; set; } = 9;

        [Display(Name = "Row gap (px)", GroupName = "01 Ladder", Order = 140)]
        [Range(0, 10)]
        public int RowGap { get; set; } = 1;

        [Display(Name = "Show the price column", GroupName = "01 Ladder", Order = 150)]
        public bool ShowPrices { get; set; } = true;

        [Display(Name = "Text size", GroupName = "01 Ladder", Order = 160)]
        [Range(6, 16)]
        public int TextSize
        {
            get { return _fontSize; }
            set
            {
                if (value < 6 || value > 16) return;
                _fontSize = value;
                _font = new RenderFont("Arial", value);
            }
        }
        private int _fontSize = 8;

        #endregion

        #region Settings -- book

        [Display(Name = "Refresh (ms)", GroupName = "02 Book", Order = 200,
                 Description = "The chart only repaints when bars change, so this drives it. " +
                               "The book is sampled at this rate -- size faster than this is not seen.")]
        [Range(20, 2000)]
        public int RefreshMs { get; set; } = 150;

        [Display(Name = "Levels per side (0 = all)", GroupName = "02 Book", Order = 210)]
        [Range(0, 200)]
        public int LevelsPerSide { get; set; } = 0;

        [Display(Name = "Show resting size", GroupName = "02 Book", Order = 220)]
        public bool ShowRest { get; set; } = true;

        #endregion

        #region Settings -- tape

        [Display(Name = "Show what traded", GroupName = "03 Tape", Order = 300)]
        public bool ShowTape { get; set; } = true;

        [Display(Name = "Window (bars)", GroupName = "03 Tape", Order = 310,
                 Description = "How many bars back the traded volume is read from.")]
        [Range(1, 500)]
        public int TapeWindowBars { get; set; } = 20;

        [Display(Name = "Weight by age", GroupName = "03 Tape", Order = 320)]
        public TapeDecay Decay { get; set; } = TapeDecay.HalfLife;

        #endregion

        #region Settings -- levels

        [Display(Name = "Levels", GroupName = "04 Levels", Order = 400,
                 Description = "One per line or comma separated, as NAME=PRICE or NAME PRICE. " +
                               "For example: PDH=29650.25, PDL 29500, ORB-H=29612.5")]
        public string Levels { get; set; } = "";

        [Display(Name = "Band (ticks)", GroupName = "04 Levels", Order = 410,
                 Description = "Inside this many ticks the level is being tested.")]
        [Range(1, 200)]
        public int BandTicks { get; set; } = 4;

        [Display(Name = "Approach (ticks)", GroupName = "04 Levels", Order = 420)]
        [Range(1, 2000)]
        public int ApproachTicks { get; set; } = 20;

        [Display(Name = "Break (ticks beyond the band)", GroupName = "04 Levels", Order = 430,
                 Description = "How far through the level price has to go before it is called a break.")]
        [Range(1, 500)]
        public int BreakTicks { get; set; } = 8;

        [Display(Name = "Reject (ticks back off the band)", GroupName = "04 Levels", Order = 440)]
        [Range(1, 500)]
        public int RejectTicks { get; set; } = 8;

        [Display(Name = "Hold an event for (s)", GroupName = "04 Levels", Order = 450,
                 Description = "How long a break or a rejection stays on screen after it happens.")]
        [Range(1, 600)]
        public int LatchSeconds { get; set; } = 20;

        #endregion

        #region Settings -- look

        [Display(Name = "Resting bid", GroupName = "05 Look", Order = 500)]
        public MColor BidColor
        {
            get { return _bidColor; }
            set { _bidColor = value; }
        }

        [Display(Name = "Resting ask", GroupName = "05 Look", Order = 510)]
        public MColor AskColor
        {
            get { return _askColor; }
            set { _askColor = value; }
        }

        [Display(Name = "Traded at bid", GroupName = "05 Look", Order = 520)]
        public MColor TapeBidColor
        {
            get { return _tapeBidColor; }
            set { _tapeBidColor = value; }
        }

        [Display(Name = "Traded at ask", GroupName = "05 Look", Order = 530)]
        public MColor TapeAskColor
        {
            get { return _tapeAskColor; }
            set { _tapeAskColor = value; }
        }

        [Display(Name = "The one loud colour", GroupName = "05 Look", Order = 540,
                 Description = "Only ever used for a break. Nothing else on the ladder is allowed it.")]
        public MColor LoudColor
        {
            get { return _loudColor; }
            set { _loudColor = value; }
        }

        [Display(Name = "Dim when nothing is happening (%)", GroupName = "05 Look", Order = 550,
                 Description = "How far the whole panel fades when no level is in play.")]
        [Range(10, 100)]
        public int QuietDim { get; set; } = 45;

        #endregion

        #region Settings -- log

        [Display(Name = "Write an event log", GroupName = "06 Log", Order = 600,
                 Description = "Appends every level state change to ocean-dom-events.jsonl in the " +
                               "ATAS data folder, so what the ladder said can be checked afterwards.")]
        public bool WriteLog { get; set; } = true;

        #endregion

        protected override void OnCalculate(int bar, decimal value)
        {
        }

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

        #region Reading the market

        private List<BookLevel> ReadBook()
        {
            var book = new List<BookLevel>(64);

            var provider = MarketDepthInfo;
            if (provider == null) return book;

            var snapshot = provider.GetMarketDepthSnapshot();
            if (snapshot == null) return book;

            foreach (var arg in snapshot)
            {
                if (arg == null) continue;
                if (!arg.IsAsk && !arg.IsBid) continue;
                if (arg.Volume <= 0m) continue;

                book.Add(new BookLevel(arg.Price, arg.Volume, arg.IsAsk));
            }

            if (LevelsPerSide > 0) book = NearestLevels(book);
            return book;
        }

        /// <summary>
        /// Rebuilds the traded-volume-per-price window from the bars.
        ///
        /// Read from the bars rather than accumulated off the live tape on purpose: an
        /// accumulator quietly loses whatever arrived while the feed was reconnecting and the
        /// hole never shows up on screen.
        /// </summary>
        private void ReadTape()
        {
            _tape.Clear();
            if (!ShowTape) return;

            var newest = CurrentBar - 1;
            if (newest < 0) return;

            var window = TapeWindowBars;
            var prints = new List<TapePrint>(64);

            for (var age = 0; age < window; age++)
            {
                var bar = newest - age;
                if (bar < 0) break;

                var candle = GetCandle(bar);
                if (candle == null) continue;

                prints.Clear();
                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null) continue;
                    if (level.Bid <= 0m && level.Ask <= 0m) continue;

                    prints.Add(new TapePrint(level.Price, level.Bid, level.Ask));
                }

                _tape.AddBar(prints, age, window, Decay);
            }
        }

        private List<BookLevel> NearestLevels(List<BookLevel> levels)
        {
            var bestBid = decimal.MinValue;
            var bestAsk = decimal.MaxValue;

            for (var i = 0; i < levels.Count; i++)
            {
                if (levels[i].IsAsk) { if (levels[i].Price < bestAsk) bestAsk = levels[i].Price; }
                else { if (levels[i].Price > bestBid) bestBid = levels[i].Price; }
            }

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m) return levels;

            var span = tick * LevelsPerSide;
            var kept = new List<BookLevel>(levels.Count);

            for (var i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                if (level.IsAsk)
                {
                    if (bestAsk != decimal.MaxValue && level.Price - bestAsk < span) kept.Add(level);
                }
                else
                {
                    if (bestBid != decimal.MinValue && bestBid - level.Price < span) kept.Add(level);
                }
            }

            return kept;
        }

        #endregion

        #region Levels and state

        private LevelTuning Tuning(decimal tick)
        {
            var tuning = new LevelTuning();
            tuning.TickSize = tick;
            tuning.BandTicks = BandTicks;
            tuning.ApproachTicks = ApproachTicks;
            tuning.BreakTicks = BreakTicks;
            tuning.RejectTicks = RejectTicks;
            tuning.LatchMs = LatchSeconds * 1000;
            return tuning;
        }

        /// <summary>Rebuilds the watch list only when the text actually changed, so state survives.</summary>
        private void SyncWatches()
        {
            var text = Levels ?? "";
            if (_watchedText == text) return;

            _watchedText = text;
            _watches.Clear();

            var parsed = LevelParser.Parse(text);
            _unreadable = parsed.Unreadable;

            for (var i = 0; i < parsed.Levels.Count; i++)
                _watches.Add(new LevelWatch(parsed.Levels[i].Name, parsed.Levels[i].Price));
        }

        private void UpdateStates(decimal price, decimal tick)
        {
            var tuning = Tuning(tick);
            var now = _clock.ElapsedMilliseconds;

            while (_states.Count < _watches.Count) _states.Add(LevelState.Quiet);
            while (_states.Count > _watches.Count) _states.RemoveAt(_states.Count - 1);

            for (var i = 0; i < _watches.Count; i++)
            {
                var before = _watches[i].State;
                var after = _watches[i].Update(price, now, tuning);
                _states[i] = after;

                if (after != before) Log(_watches[i], before, after, price);
            }
        }

        #endregion

        #region Event log

        /// <summary>
        /// Appends one line per state change. Wrapped whole: a logging problem must never take
        /// the ladder down with it, and after one failure it stops trying rather than thrashing.
        /// </summary>
        private void Log(LevelWatch watch, LevelState from, LevelState to, decimal price)
        {
            if (!WriteLog || _logBroken) return;

            try
            {
                if (_logPath == null)
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, "ocean-dom-events.jsonl");
                }

                var line = new StringBuilder(160);
                line.Append("{\"utc\":\"");
                line.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                line.Append("\",\"instrument\":\"");
                line.Append(InstrumentInfo == null ? "" : InstrumentInfo.Instrument);
                line.Append("\",\"level\":\"");
                line.Append(watch.Name);
                line.Append("\",\"levelPrice\":");
                line.Append(watch.Price.ToString(CultureInfo.InvariantCulture));
                line.Append(",\"price\":");
                line.Append(price.ToString(CultureInfo.InvariantCulture));
                line.Append(",\"from\":\"");
                line.Append(from);
                line.Append("\",\"to\":\"");
                line.Append(to);
                line.Append("\"}");

                File.AppendAllText(_logPath, line.ToString() + Environment.NewLine);
            }
            catch (Exception)
            {
                _logBroken = true;
            }
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

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m)
            {
                Status(context, region, "Oceans DOM: the instrument has no tick size yet.");
                return;
            }

            var pixelsPerTick = container.PriceRowHeight;
            var ticksPerRow = LadderGrid.TicksPerRow(pixelsPerTick, MinRowHeight);
            var grid = LadderGrid.Build(container.Low, container.High, tick, ticksPerRow, MaxRows);
            if (grid == null)
            {
                Status(context, region, "Oceans DOM: the chart has no price range yet.");
                return;
            }

            var book = ShowRest ? ReadBook() : new List<BookLevel>();
            ReadTape();

            var rows = DomMath.Aggregate(grid, book, _tape);
            var restRef = DomMath.MaxRest(rows);
            var tapeRef = DomMath.MaxTape(rows);

            SyncWatches();

            var last = 0m;
            if (CurrentBar > 0)
            {
                var candle = GetCandle(CurrentBar - 1);
                if (candle != null) last = candle.Close;
            }

            if (last > 0m) UpdateStates(last, tick);

            var primary = Salience.PrimaryIndex(_states);
            var loud = Salience.LoudIndex(_states);
            var primaryState = primary >= 0 ? _states[primary] : LevelState.Quiet;

            // The restful default. With no level in play the whole panel steps back rather than
            // sitting at full strength inviting a trade that is not there.
            var alpha = primaryState == LevelState.Quiet ? QuietDim / 100m : 1m;

            var rowPixels = (int)Math.Round(pixelsPerTick * ticksPerRow);
            if (rowPixels < 1) rowPixels = 1;

            var panel = PanelRect(region);

            context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);
            context.SetClip(region);

            var columns = Columns(panel);
            _failures.Clear();

            // Every layer is caught on its own. A layer that throws otherwise takes down every
            // layer after it in silence -- including the readout that would have said so.
            try
            {
                Layer("panel", () =>
                    context.FillRectangle(Color.FromArgb(Fade(150, alpha), 12, 14, 18), panel));

                if (ShowTape)
                    Layer("tape", () =>
                        DrawTape(context, container, region, columns, grid, rows, tapeRef, rowPixels, alpha));

                if (ShowRest)
                {
                    Layer("bids", () =>
                        DrawRest(context, container, region, columns, grid, rows, restRef, rowPixels, alpha, false));
                    Layer("asks", () =>
                        DrawRest(context, container, region, columns, grid, rows, restRef, rowPixels, alpha, true));
                }

                if (ShowPrices)
                    Layer("prices", () =>
                        DrawPrices(context, container, region, columns, grid, rowPixels, alpha, last));

                Layer("levels", () =>
                    DrawLevels(context, container, region, columns, panel, rowPixels, alpha, loud));
                Layer("chip", () => DrawChip(context, region, panel, primary, primaryState, alpha));
                Layer("levels setting", () => DrawUnreadable(context, region, panel));

                ReportFailures(context, region);
            }
            finally
            {
                context.ResetClip();
            }
        }

        private readonly List<string> _failures = new List<string>();

        private void Layer(string name, Action draw)
        {
            try
            {
                draw();
            }
            catch (Exception ex)
            {
                _failures.Add(name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// There is no debugger on the render thread, so a layer that died says so on the chart.
        /// Drawn outside <see cref="Layer"/> deliberately -- if this throws there is nothing left
        /// that could report it anyway.
        /// </summary>
        private void ReportFailures(RenderContext context, Rectangle region)
        {
            if (_failures.Count == 0) return;

            var y = region.Top + 20;
            for (var i = 0; i < _failures.Count; i++)
            {
                var text = "Oceans DOM " + _failures[i];
                var size = context.MeasureString(text, _statusFont);
                context.DrawString(text, _statusFont, Color.FromArgb(240, 120, 110),
                                   region.Left + 6, y);
                y += size.Height + 1;
            }
        }

        #region Layout

        private Rectangle PanelRect(Rectangle region)
        {
            var width = PanelWidth;
            if (width > region.Width) width = region.Width;

            var left = Side == LadderSide.Right
                ? region.Right - EdgeMargin - width
                : region.Left + EdgeMargin;

            if (left < region.Left) left = region.Left;
            if (left + width > region.Right) left = region.Right - width;

            return new Rectangle(left, region.Top, width, region.Height);
        }

        private struct Layout
        {
            public int TapeLeft, TapeRight;
            public int BidLeft, BidRight;
            public int PriceLeft, PriceRight;
            public int AskLeft, AskRight;
            public int RailLeft, RailRight;
        }

        /// <summary>
        /// Fixed proportions, always. Every column keeps its place whether or not it has anything
        /// in it -- a ladder that reflows as things appear cannot be read by muscle memory, and
        /// muscle memory is the whole reason a ladder is faster than a chart.
        /// </summary>
        private Layout Columns(Rectangle panel)
        {
            var width = panel.Width;

            var tapeW = ShowTape ? (int)(width * 0.22) : 0;
            var railW = (int)(width * 0.09);
            if (railW < 10) railW = 10;

            var priceW = ShowPrices ? (int)(width * 0.17) : 0;
            var sideW = (width - tapeW - railW - priceW) / 2;
            if (sideW < 1) sideW = 1;

            var layout = new Layout();
            layout.TapeLeft = panel.Left;
            layout.TapeRight = layout.TapeLeft + tapeW;
            layout.BidLeft = layout.TapeRight;
            layout.BidRight = layout.BidLeft + sideW;
            layout.PriceLeft = layout.BidRight;
            layout.PriceRight = layout.PriceLeft + priceW;
            layout.AskLeft = layout.PriceRight;
            layout.AskRight = layout.AskLeft + sideW;
            layout.RailLeft = layout.AskRight;
            layout.RailRight = panel.Right;
            return layout;
        }

        private int RowY(IChartContainer container, LadderGrid grid, int index, int rowPixels, out int height)
        {
            height = rowPixels - RowGap;
            if (height < 1) height = 1;

            return container.GetYByPrice(grid.Mid(index), false) - height / 2;
        }

        #endregion

        #region Drawing

        /// <summary>
        /// Resting size, as a bar whose length is the size, both sides baselined against the price
        /// column. Length against a shared baseline is the encoding people read most accurately;
        /// colour alone is the one they read worst, so colour here only says which side it is.
        /// </summary>
        private void DrawRest(RenderContext context, IChartContainer container, Rectangle region,
                              Layout columns, LadderGrid grid, LadderRow[] rows, decimal reference,
                              int rowPixels, decimal alpha, bool isAsk)
        {
            if (reference <= 0m) return;

            var color = Conv(isAsk ? _askColor : _bidColor);
            var left = isAsk ? columns.AskLeft : columns.BidLeft;
            var right = isAsk ? columns.AskRight : columns.BidRight;
            var width = right - left;
            if (width <= 0) return;

            for (var i = 0; i < rows.Length; i++)
            {
                var size = rows[i].Rest(isAsk);
                if (size <= 0m) continue;

                int height;
                var y = RowY(container, grid, i, rowPixels, out height);
                if (y + height < region.Top || y > region.Bottom) continue;

                var length = (int)(width * DomMath.Normalise(size, reference));
                if (length < 1) length = 1;

                // Both sides grow away from the price column, so the two lengths meet in the
                // middle and can be compared without the eye travelling.
                var barLeft = isAsk ? left : right - length;
                context.FillRectangle(Color.FromArgb(Fade(210, alpha), color.R, color.G, color.B),
                                      new Rectangle(barLeft, y, length, height));

                DrawNumber(context, region, DomMath.Compact(size), color, alpha,
                           isAsk ? barLeft + length + 3 : barLeft - 3, y, height, !isAsk);
            }
        }

        /// <summary>
        /// What actually traded at each price, stacked: the part that hit the bid, then the part
        /// that hit the ask. Sitting on the same row as the resting size is the point of the
        /// whole panel -- size that stayed while trade went through it is absorption, and both
        /// halves of that sentence are now in one place instead of two indicators.
        /// </summary>
        private void DrawTape(RenderContext context, IChartContainer container, Rectangle region,
                              Layout columns, LadderGrid grid, LadderRow[] rows, decimal reference,
                              int rowPixels, decimal alpha)
        {
            if (reference <= 0m) return;

            var width = columns.TapeRight - columns.TapeLeft;
            if (width <= 0) return;

            var bid = Conv(_tapeBidColor);
            var ask = Conv(_tapeAskColor);

            for (var i = 0; i < rows.Length; i++)
            {
                var total = rows[i].TapeTotal;
                if (total <= 0m) continue;

                int height;
                var y = RowY(container, grid, i, rowPixels, out height);
                if (y + height < region.Top || y > region.Bottom) continue;

                var full = (int)(width * DomMath.Normalise(total, reference));
                if (full < 1) full = 1;

                var bidPart = total <= 0m ? 0 : (int)(full * (rows[i].TapeBid / total));
                if (bidPart > full) bidPart = full;

                var x = columns.TapeLeft;
                if (bidPart > 0)
                    context.FillRectangle(Color.FromArgb(Fade(190, alpha), bid.R, bid.G, bid.B),
                                          new Rectangle(x, y, bidPart, height));

                var askPart = full - bidPart;
                if (askPart > 0)
                    context.FillRectangle(Color.FromArgb(Fade(190, alpha), ask.R, ask.G, ask.B),
                                          new Rectangle(x + bidPart, y, askPart, height));
            }
        }

        private void DrawPrices(RenderContext context, IChartContainer container, Rectangle region,
                                Layout columns, LadderGrid grid, int rowPixels, decimal alpha,
                                decimal last)
        {
            var width = columns.PriceRight - columns.PriceLeft;
            if (width <= 0) return;

            var lastIndex = last > 0m ? grid.IndexOf(last) : -1;
            var decimals = Decimals(grid.RowSize);

            for (var i = 0; i < grid.Count; i++)
            {
                int height;
                var y = RowY(container, grid, i, rowPixels, out height);
                if (y + height < region.Top || y > region.Bottom) continue;

                var text = grid.Low(i).ToString("F" + decimals, CultureInfo.InvariantCulture);
                var size = context.MeasureString(text, _font);
                if (height < size.Height - 1) continue;

                var shade = i == lastIndex ? 245 : 150;
                var x = columns.PriceLeft + (width - size.Width) / 2;
                context.DrawString(text, _font, Color.FromArgb(Fade(shade, alpha), 205, 208, 215),
                                   x, y + (height - size.Height) / 2);

                if (i == lastIndex)
                    context.DrawRectangle(new RenderPen(Color.FromArgb(Fade(180, alpha), 220, 220, 230), 1f),
                                          new Rectangle(columns.PriceLeft, y, width, height < 3 ? 3 : height));
            }
        }

        /// <summary>
        /// The level rail. Every tracked level keeps its slot; only the state changes, and only a
        /// break is allowed the loud colour.
        /// </summary>
        private void DrawLevels(RenderContext context, IChartContainer container, Rectangle region,
                                Layout columns, Rectangle panel, int rowPixels, decimal alpha, int loud)
        {
            if (_watches.Count == 0) return;

            var railWidth = columns.RailRight - columns.RailLeft;
            if (railWidth <= 0) return;

            for (var i = 0; i < _watches.Count && i < _states.Count; i++)
            {
                var watch = _watches[i];
                var state = _states[i];
                if (state == LevelState.Quiet) continue;

                var y = container.GetYByPrice(watch.Price, false);
                if (y < region.Top - 20 || y > region.Bottom + 20) continue;

                var isLoud = i == loud;
                var color = isLoud ? Conv(_loudColor) : Color.FromArgb(170, 175, 190);
                var strength = Salience.For(state) == Loudness.Soft ? 200 : 120;
                if (isLoud) strength = 255;

                var thickness = isLoud ? 2f : 1f;
                context.DrawLine(new RenderPen(Color.FromArgb(Fade(strength, alpha), color.R, color.G, color.B),
                                               thickness),
                                 panel.Left, y, panel.Right, y);

                var label = watch.Name + "  " + state.ToString().ToUpperInvariant();
                var size = context.MeasureString(label, _font);
                var x = columns.RailLeft - size.Width - 4;
                if (x < panel.Left) x = panel.Left + 2;

                context.DrawString(label, _font,
                                   Color.FromArgb(Fade(strength, alpha), color.R, color.G, color.B),
                                   x, y - size.Height - 1);
            }
        }

        /// <summary>
        /// The one line that says where things stand. Quiet is deliberately almost nothing --
        /// the absence of a setup should look like absence.
        /// </summary>
        private void DrawChip(RenderContext context, Rectangle region, Rectangle panel,
                              int primary, LevelState state, decimal alpha)
        {
            string text;
            Color color;

            if (_watches.Count == 0)
            {
                text = "no levels set";
                color = Color.FromArgb(120, 122, 132);
            }
            else if (primary < 0 || state == LevelState.Quiet)
            {
                text = "quiet";
                color = Color.FromArgb(120, 122, 132);
            }
            else
            {
                text = state.ToString().ToUpperInvariant() + "  " + _watches[primary].Name;
                color = Salience.For(state) == Loudness.Loud
                    ? Conv(_loudColor)
                    : Color.FromArgb(215, 218, 228);
            }

            var size = context.MeasureString(text, _chipFont);
            var x = panel.Left + (panel.Width - size.Width) / 2;
            var y = region.Top + 4;

            if (Salience.For(state) == Loudness.Loud && primary >= 0)
            {
                var pad = 5;
                context.FillRectangle(Color.FromArgb(45, color.R, color.G, color.B),
                                      new Rectangle(x - pad, y - 2, size.Width + pad * 2, size.Height + 4));
            }

            context.DrawString(text, _chipFont, Color.FromArgb(Fade(255, alpha), color.R, color.G, color.B), x, y);
        }

        /// <summary>A level that could not be read is said out loud rather than quietly missing.</summary>
        private void DrawUnreadable(RenderContext context, Rectangle region, Rectangle panel)
        {
            if (_unreadable == null || _unreadable.Count == 0) return;

            var text = _unreadable.Count == 1
                ? "unreadable level: " + _unreadable[0]
                : _unreadable.Count + " unreadable levels";

            var size = context.MeasureString(text, _statusFont);
            var x = panel.Left + (panel.Width - size.Width) / 2;
            if (x < region.Left) x = region.Left + 4;

            context.DrawString(text, _statusFont, Color.FromArgb(230, 170, 80), x, region.Bottom - size.Height - 4);
        }

        private void DrawNumber(RenderContext context, Rectangle region, string text, Color color,
                                decimal alpha, int x, int y, int height, bool rightAlign)
        {
            if (string.IsNullOrEmpty(text)) return;

            var size = context.MeasureString(text, _font);
            if (height < size.Height - 1) return;

            var left = rightAlign ? x - size.Width : x;
            if (left < region.Left || left + size.Width > region.Right) return;

            context.DrawString(text, _font, Color.FromArgb(Fade(235, alpha), color.R, color.G, color.B),
                               left, y + (height - size.Height) / 2);
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

        private static int Decimals(decimal rowSize)
        {
            if (rowSize >= 1m) return 0;
            if (rowSize >= 0.1m) return 1;
            if (rowSize >= 0.01m) return 2;
            return 4;
        }

        private static int Fade(int alpha, decimal factor)
        {
            var value = (int)Math.Round(alpha * factor);
            if (value < 0) value = 0;
            if (value > 255) value = 255;
            return value;
        }

        private static Color Conv(MColor color)
        {
            return Color.FromArgb(color.R, color.G, color.B);
        }

        #endregion
    }
}
