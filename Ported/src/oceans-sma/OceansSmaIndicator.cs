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

namespace OceansSma
{
    /// <summary>Which part of a bar the averages are built from.</summary>
    public enum SmaPrice
    {
        [Display(Name = "Close")] Close,
        [Display(Name = "Open")] Open,
        [Display(Name = "High")] High,
        [Display(Name = "Low")] Low,
        [Display(Name = "Median (H+L)/2")] Median,
        [Display(Name = "Typical (H+L+C)/3")] Typical,
        [Display(Name = "Weighted (H+L+2C)/4")] Weighted
    }

    /// <summary>
    /// Ocean SMA -- the 50, 100 and 200 simple moving averages on the price panel, each tagged
    /// on the right edge with a very small label so the three are told apart at a glance.
    ///
    /// The lines are plain ATAS data series, so the platform draws and scales them. Only the
    /// labels are drawn by hand.
    /// </summary>
    [DisplayName("Oceans SMA")]
    [Category("Ocean")]
    public class OceansSmaIndicator : Indicator
    {
        private const int LineCount = 3;

        private readonly ValueDataSeries[] _series = new ValueDataSeries[LineCount];
        private readonly RollingMean[] _means = new RollingMean[LineCount];
        private readonly int[] _periods = { 50, 100, 200 };

        private SmaPrice _source = SmaPrice.Close;
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);
        private RenderFont _labelFont;
        private int _labelFontSize = 7;

        public OceansSmaIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;

            _series[0] = MakeSeries("Sma1", "SMA 50", MColor.FromRgb(80, 170, 255));
            _series[1] = MakeSeries("Sma2", "SMA 100", MColor.FromRgb(255, 170, 60));
            _series[2] = MakeSeries("Sma3", "SMA 200", MColor.FromRgb(235, 80, 105));

            DataSeries[0] = _series[0];
            DataSeries.Add(_series[1]);
            DataSeries.Add(_series[2]);

            _labelFont = new RenderFont("Arial", _labelFontSize);
        }

        private static ValueDataSeries MakeSeries(string id, string name, MColor color)
        {
            return new ValueDataSeries(id, name)
            {
                VisualType = VisualMode.Line,
                Color = color,
                Width = 1,
                LineDashStyle = LineDashStyle.Solid,
                ShowZeroValue = false,
                ScaleIt = true,
                ShowCurrentValue = true,
                IgnoredByAlerts = true
            };
        }

        #region Settings -- the three averages

        [Display(Name = "Show", GroupName = "01 SMA 50", Order = 100)]
        public bool Show1
        {
            get { return _series[0].VisualType == VisualMode.Line; }
            set { _series[0].VisualType = value ? VisualMode.Line : VisualMode.Hide; }
        }

        [Display(Name = "Period", GroupName = "01 SMA 50", Order = 110)]
        [Range(1, 5000)]
        public int Period1
        {
            get { return _periods[0]; }
            set { SetPeriod(0, value); }
        }

        [Display(Name = "Colour", GroupName = "01 SMA 50", Order = 120)]
        public MColor Color1
        {
            get { return _series[0].Color; }
            set { _series[0].Color = value; }
        }

        [Display(Name = "Width", GroupName = "01 SMA 50", Order = 130)]
        [Range(1, 10)]
        public int Width1
        {
            get { return _series[0].Width; }
            set { _series[0].Width = value < 1 ? 1 : value; }
        }

        [Display(Name = "Line style", GroupName = "01 SMA 50", Order = 140)]
        public LineDashStyle Style1
        {
            get { return _series[0].LineDashStyle; }
            set { _series[0].LineDashStyle = value; }
        }

        [Display(Name = "Show", GroupName = "02 SMA 100", Order = 200)]
        public bool Show2
        {
            get { return _series[1].VisualType == VisualMode.Line; }
            set { _series[1].VisualType = value ? VisualMode.Line : VisualMode.Hide; }
        }

        [Display(Name = "Period", GroupName = "02 SMA 100", Order = 210)]
        [Range(1, 5000)]
        public int Period2
        {
            get { return _periods[1]; }
            set { SetPeriod(1, value); }
        }

        [Display(Name = "Colour", GroupName = "02 SMA 100", Order = 220)]
        public MColor Color2
        {
            get { return _series[1].Color; }
            set { _series[1].Color = value; }
        }

        [Display(Name = "Width", GroupName = "02 SMA 100", Order = 230)]
        [Range(1, 10)]
        public int Width2
        {
            get { return _series[1].Width; }
            set { _series[1].Width = value < 1 ? 1 : value; }
        }

        [Display(Name = "Line style", GroupName = "02 SMA 100", Order = 240)]
        public LineDashStyle Style2
        {
            get { return _series[1].LineDashStyle; }
            set { _series[1].LineDashStyle = value; }
        }

        [Display(Name = "Show", GroupName = "03 SMA 200", Order = 300)]
        public bool Show3
        {
            get { return _series[2].VisualType == VisualMode.Line; }
            set { _series[2].VisualType = value ? VisualMode.Line : VisualMode.Hide; }
        }

        [Display(Name = "Period", GroupName = "03 SMA 200", Order = 310)]
        [Range(1, 5000)]
        public int Period3
        {
            get { return _periods[2]; }
            set { SetPeriod(2, value); }
        }

        [Display(Name = "Colour", GroupName = "03 SMA 200", Order = 320)]
        public MColor Color3
        {
            get { return _series[2].Color; }
            set { _series[2].Color = value; }
        }

        [Display(Name = "Width", GroupName = "03 SMA 200", Order = 330)]
        [Range(1, 10)]
        public int Width3
        {
            get { return _series[2].Width; }
            set { _series[2].Width = value < 1 ? 1 : value; }
        }

        [Display(Name = "Line style", GroupName = "03 SMA 200", Order = 340)]
        public LineDashStyle Style3
        {
            get { return _series[2].LineDashStyle; }
            set { _series[2].LineDashStyle = value; }
        }

        #endregion

        #region Settings -- labels

        [Display(Name = "Show labels", GroupName = "04 Labels", Order = 400)]
        public bool ShowLabels { get; set; } = true;

        [Display(Name = "Text size", GroupName = "04 Labels", Order = 410,
                 Description = "Points. 7 is the small default; 6 is about as small as stays readable.")]
        [Range(4, 20)]
        public int LabelTextSize
        {
            get { return _labelFontSize; }
            set
            {
                var size = value < 4 ? 4 : value > 20 ? 20 : value;
                if (size == _labelFontSize) return;
                _labelFontSize = size;
                _labelFont = new RenderFont("Arial", size);
            }
        }

        // Renamed from ScaleToAverages when the default flipped to true. ATAS restores a saved
        // property value whenever the NAME still matches, so a template saved with the old
        // default would have kept serving false under the new meaning.
        [Display(Name = "Keep averages in view", GroupName = "05 Display", Order = 500,
                 Description = "On lets a far-away average stretch the price axis so it stays " +
                               "visible. Off keeps the scale on price alone.")]
        public bool KeepAveragesInView
        {
            get { return _series[0].ScaleIt; }
            set { for (var i = 0; i < LineCount; i++) _series[i].ScaleIt = value; }
        }

        [Display(Name = "Price", GroupName = "05 Display", Order = 490,
                 Description = "Which part of each bar the averages are built from.")]
        public SmaPrice Source
        {
            get { return _source; }
            set
            {
                if (value == _source) return;
                _source = value;
                for (var i = 0; i < LineCount; i++) _means[i] = null;
                RecalculateValues();
            }
        }

        [Display(Name = "Show status", GroupName = "05 Display", Order = 510,
                 Description = "Prints a line top-left when an average has too little history " +
                               "to draw. Without it, a short chart looks like a broken indicator.")]
        public bool ShowStatus { get; set; } = true;

        [Display(Name = "Prefix with SMA", GroupName = "04 Labels", Order = 420,
                 Description = "On gives SMA 50, off gives just 50.")]
        public bool LabelPrefix { get; set; } = true;

        [Display(Name = "Show value in label", GroupName = "04 Labels", Order = 430)]
        public bool LabelValue { get; set; } = false;

        #endregion

        #region Calculation

        private void SetPeriod(int i, int period)
        {
            if (period < 1 || period == _periods[i]) return;

            _periods[i] = period;
            _series[i].Name = "SMA " + period;
            _means[i] = null;
            RecalculateValues();
        }

        protected override void OnRecalculate()
        {
            for (var i = 0; i < LineCount; i++) _means[i] = null;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            for (var i = 0; i < LineCount; i++)
            {
                if (_means[i] == null) _means[i] = new RollingMean(_periods[i], Price);

                var avg = _means[i].At(bar);

                // Bars before the window fills get 0, which ShowZeroValue = false leaves
                // undrawn. The line starts where the average is a real full-length average.
                _series[i][bar] = avg ?? 0m;
            }
        }

        /// <summary>
        /// Price the averages are built from, read straight off the candle.
        ///
        /// This deliberately does NOT use SourceDataSeries. That series is null until the
        /// platform wires it up, and indexing it before then hands back 0 rather than failing --
        /// which averages to 0, and 0 with ShowZeroValue off draws nothing at all. A silent
        /// zero that looks like a broken indicator is worse than no option at all, so the
        /// source is picked explicitly above and the candle is always the thing read.
        /// </summary>
        private decimal Price(int bar)
        {
            var c = GetCandle(bar);

            switch (Source)
            {
                case SmaPrice.Open: return c.Open;
                case SmaPrice.High: return c.High;
                case SmaPrice.Low: return c.Low;
                case SmaPrice.Median: return (c.High + c.Low) / 2m;
                case SmaPrice.Typical: return (c.High + c.Low + c.Close) / 3m;
                case SmaPrice.Weighted: return (c.High + c.Low + 2m * c.Close) / 4m;
                default: return c.Close;
            }
        }

        #endregion

        #region Labels

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            var container = chart == null ? null : chart.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;

            if (CurrentBar < 1)
            {
                if (ShowStatus) DrawStatus(context, region, "Oceans SMA: no bars loaded yet.");
                return;
            }

            if (ShowStatus)
            {
                var waiting = Waiting();
                if (waiting != null) DrawStatus(context, region, waiting);
            }

            if (!ShowLabels) return;

            var bar = container.LastVisibleBarNumber;
            if (bar >= CurrentBar) bar = CurrentBar - 1;
            if (bar < 0) return;

            var tags = new List<Tag>(LineCount);

            for (var i = 0; i < LineCount; i++)
            {
                if (_series[i].VisualType != VisualMode.Line) continue;
                if (bar >= _series[i].Count) continue;

                var v = _series[i][bar];
                if (v == 0m) continue;   // window is not full yet at this bar

                var y = container.GetYByPrice(v, false);
                if (y < region.Top || y > region.Bottom) continue;

                var tag = new Tag();
                tag.Y = y;
                tag.Color = Conv(_series[i].Color);
                tag.Text = LabelText(i, v);
                tags.Add(tag);
            }

            if (tags.Count == 0) return;

            var height = (int)context.MeasureString("0", _labelFont).Height;
            Spread(tags, height + 1, region);

            for (var i = 0; i < tags.Count; i++)
            {
                var width = (int)context.MeasureString(tags[i].Text, _labelFont).Width;
                var x = region.Right - width - 3;
                if (x < region.Left) x = region.Left + 2;

                context.DrawString(tags[i].Text, _labelFont, tags[i].Color, x, tags[i].Y);
            }
        }

        /// <summary>
        /// Names any switched-on average that cannot draw yet, or null when all of them can.
        /// Refusing to average a partial window is right, but silent -- too little history
        /// looks exactly like a broken indicator. This says which it is.
        /// </summary>
        private string Waiting()
        {
            var thin = new List<string>(LineCount);
            var empty = new List<string>(LineCount);
            var last = CurrentBar - 1;

            for (var i = 0; i < LineCount; i++)
            {
                if (_series[i].VisualType != VisualMode.Line) continue;

                if (CurrentBar < _periods[i]) { thin.Add(_periods[i].ToString()); continue; }

                // Enough bars, yet still nothing to draw. That means the price feeding the
                // average came back empty, which must never pass as a quiet blank chart.
                if (last < _series[i].Count && _series[i][last] == 0m)
                    empty.Add(_periods[i].ToString());
            }

            if (empty.Count > 0)
                return "Oceans SMA: " + string.Join(", ", empty) +
                       " got no price from the bars despite " + CurrentBar +
                       " of them. Something is wrong -- do not trade off this.";

            if (thin.Count == 0) return null;

            return "Oceans SMA: " + string.Join(", ", thin) +
                   (thin.Count == 1 ? " needs" : " need") + " more history. Chart has " +
                   CurrentBar + (CurrentBar == 1 ? " bar" : " bars") +
                   " -- load more, or lower the period.";
        }

        private void DrawStatus(RenderContext context, Rectangle region, string text)
        {
            context.DrawString(text, _statusFont, Color.FromArgb(230, 170, 80),
                               region.Left + 6, region.Top + 4);
        }

        private string LabelText(int i, decimal value)
        {
            var name = LabelPrefix ? "SMA " + _periods[i] : _periods[i].ToString();
            return LabelValue ? name + " " + ChartInfo.GetPriceString(value) : name;
        }

        /// <summary>
        /// Pushes labels apart where the averages converge, so three tags stacked on the same
        /// pixel stay three readable tags. Order is kept: the highest average holds the top slot.
        /// </summary>
        private static void Spread(List<Tag> tags, int gap, Rectangle region)
        {
            tags.Sort(delegate (Tag a, Tag b) { return a.Y.CompareTo(b.Y); });

            for (var i = 1; i < tags.Count; i++)
            {
                if (tags[i].Y - tags[i - 1].Y >= gap) continue;

                var t = tags[i];
                t.Y = tags[i - 1].Y + gap;
                tags[i] = t;
            }

            // If pushing down ran the stack off the bottom, pull the whole stack back up.
            var overflow = tags[tags.Count - 1].Y + gap - region.Bottom;
            if (overflow <= 0) return;

            for (var i = 0; i < tags.Count; i++)
            {
                var t = tags[i];
                t.Y -= overflow;
                if (t.Y < region.Top) t.Y = region.Top;
                tags[i] = t;
            }
        }

        private struct Tag
        {
            public int Y;
            public Color Color;
            public string Text;
        }

        private static Color Conv(MColor c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        #endregion
    }
}
