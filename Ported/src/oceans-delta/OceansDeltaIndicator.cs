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

namespace OceansDelta
{
    public enum BoxCorner
    {
        [Display(Name = "Top left")] TopLeft,
        [Display(Name = "Top right")] TopRight,
        [Display(Name = "Bottom left")] BottomLeft,
        [Display(Name = "Bottom right")] BottomRight
    }

    public enum BandPlace
    {
        [Display(Name = "Bottom of the panel")] Bottom,
        [Display(Name = "Top of the panel")] Top
    }

    public enum LabelDetail
    {
        [Display(Name = "None")] Off,
        [Display(Name = "One line")] Short,
        [Display(Name = "Three lines")] Full
    }

    /// <summary>How much of the panel the delta reads are allowed to take.</summary>
    public enum PanelLayout
    {
        [Display(Name = "Crosses only - no band at all")] CrossOnly,
        [Display(Name = "Two thin strips")] Strips,
        [Display(Name = "Full session delta track")] Track
    }

    public enum ReadoutStyle
    {
        [Display(Name = "Off")] Off,
        [Display(Name = "One line")] OneLine,
        [Display(Name = "Full box")] Full
    }

    public enum HorizontalReach
    {
        [Display(Name = "Right across the chart")] FullWidth,
        [Display(Name = "From the flip bar rightwards")] FromTheFlip
    }

    /// <summary>
    /// Ocean Delta Cross -- four delta reads wired into one another, and one mark on the chart.
    ///
    /// The four, and what each contributes:
    ///
    /// 1. BAR DELTA. Each bar's aggressor imbalance, drawn as a ribbon along the edge of the panel
    ///    rather than over the candles.
    /// 2. SESSION DELTA. The running sum of that since the session opened, drawn as a track with a
    ///    zero line, so the crossings are visible as crossings.
    /// 3. THE FLIP. Where the session delta changed sign -- armed at the crossing, confirmed only
    ///    once it has travelled far enough past zero to be believed, and anchored back to the bar
    ///    that crossed.
    /// 4. CLUSTER SEARCH. Inside that crossing bar, which prices carried genuinely one-sided size.
    ///    This is what grades the flip: a cross backed by a 400-lot one-way cluster and a cross
    ///    that drifted over zero on scraps are not the same event, and are not drawn the same.
    ///
    /// The output is a CROSS: a vertical line on the bar the session delta flipped, and a
    /// horizontal at the price, so the level and the moment read as one mark.
    ///
    /// What this refuses to do: interpolate a price for the exact instant of the crossing. Bar
    /// data records what traded at each price, never in what order, so the tick the sum passed
    /// zero is not recoverable. The horizontal is either the crossing bar's close -- a fact -- or
    /// the cluster that carried the flip -- a reading, labelled as one.
    /// </summary>
    [DisplayName("Oceans Delta Cross MNQ")]
    [Category("Ocean")]
    public class OceansDeltaIndicator : Indicator
    {
        private const int MaxVisibleBars = 5000;
        private const int MaxBarLevels = 6000;
        private const int MaxIceLevels = 4000;

        private readonly DeltaEngine _engine = new DeltaEngine();

        private readonly List<decimal> _barDelta = new List<decimal>();
        private readonly List<decimal> _cum = new List<decimal>();
        private readonly List<int> _sessionOf = new List<int>();
        private readonly List<CrossMark> _marks = new List<CrossMark>();

        private readonly RenderFont _font = new RenderFont("Arial", 8f);
        private readonly RenderFont _boxFont = new RenderFont("Arial", 9f);
        private readonly RenderFont _labelFont = new RenderFont("Arial", 8f, FontStyle.Bold);

        private TimeContext _clock;
        private string _clockKey;
        private int _clockBars;

        private int _sessions;
        private string _markSignature;

        private IcebergScan _ice;
        private string _iceKey;

        private MColor _zoneColor = MColor.FromRgb(120, 150, 220);
        private MColor _iceColor = MColor.FromRgb(255, 190, 80);

        private MColor _buyColor = MColor.FromRgb(60, 210, 130);
        private MColor _sellColor = MColor.FromRgb(230, 70, 90);

        /// <summary>A confirmed flip, with everything the chart and the box need about it.</summary>
        private sealed class CrossMark
        {
            public Flip Flip;
            public int Session;

            public decimal Price;
            public string PriceNote;

            public decimal Close;
            public int ClusterCount;
            public decimal ClusterVolume;
            public bool HasBest;
            public ClusterHit Best;

            public FlipZone Zone;

            /// <summary>The cluster search found nothing that qualified inside the crossing bar.</summary>
            public bool Unbacked => ClusterCount == 0;
        }

        public OceansDeltaIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            DrawAbovePrice = true;

            var series = DataSeries[0] as ValueDataSeries;
            if (series != null)
            {
                series.VisualType = VisualMode.Hide;
                series.IsHidden = true;
                series.ShowZeroValue = false;
                series.ScaleIt = false;
                series.IgnoredByAlerts = true;
            }
        }

        #region Settings -- the flip

        [Display(Name = "Confirm a flip after (contracts past zero)", GroupName = "01 The flip", Order = 100,
                 Description = "Session delta has to travel this far beyond zero before a crossing " +
                               "counts. A crossing that comes back before it does never happened, " +
                               "and no cross is drawn. Set it to 0 to mark every touch of zero.")]
        [Range(0, 1000000)]
        public int ConfirmContracts { get; set; } = 250;

        [Display(Name = "Minimum bars between crosses", GroupName = "01 The flip", Order = 105,
                 Description = "A recross this soon after the last one is chop. The read still " +
                               "changes side -- it has to, or it would be left facing the wrong " +
                               "way -- but no second cross is drawn. 0 turns this off.")]
        [Range(0, 500)]
        public int MinBarsBetweenCrosses { get; set; } = 0;

        [Display(Name = "Mark the session's first side too", GroupName = "01 The flip", Order = 110,
                 Description = "Session delta starts at zero, so the first move away from it is " +
                               "the session choosing a side, not reversing one. Off by default: " +
                               "otherwise every session opens with a cross on it.")]
        public bool MarkFirstSide { get; set; } = false;

        #endregion

        #region Settings -- the cross

        [Display(Name = "Draw the cross", GroupName = "02 The cross", Order = 200,
                 Description = "A vertical on the bar the session delta flipped and a horizontal " +
                               "at the price, crossing on the flip.")]
        public bool ShowCrosses { get; set; } = true;

        [Display(Name = "Horizontal price", GroupName = "02 The cross", Order = 205,
                 Description = "CLOSE is the price the market was at when the flip was on the " +
                               "record -- a fact. FLIP CLUSTER is the price level inside that bar " +
                               "that carried the most one-sided size -- where the flip was paid " +
                               "for, which is a reading, and is labelled as one.")]
        public CrossPriceMode CrossPrice { get; set; } = CrossPriceMode.Close;

        [Display(Name = "Horizontal reaches", GroupName = "02 The cross", Order = 210)]
        public HorizontalReach Reach { get; set; } = HorizontalReach.FullWidth;

        [Display(Name = "This session only", GroupName = "02 The cross", Order = 215,
                 Description = "Older sessions' crosses are dropped rather than stacked up. " +
                               "Turn it off to look back over how the last few sessions turned.")]
        public bool ThisSessionOnly { get; set; } = true;

        [Display(Name = "Most crosses to draw", GroupName = "02 The cross", Order = 220)]
        [Range(1, 200)]
        public int MaxCrosses { get; set; } = 12;

        [Display(Name = "Label the cross", GroupName = "02 The cross", Order = 225,
                 Description = "ONE LINE is the side, the time and the price. THREE LINES adds " +
                               "how far past zero it had to travel and what the cluster search " +
                               "found -- worth it on one cross, not on six.")]
        public LabelDetail CrossLabels { get; set; } = LabelDetail.Short;

        [Display(Name = "Line width (pixels)", GroupName = "02 The cross", Order = 230)]
        [Range(1, 5)]
        public int CrossWidth { get; set; } = 2;

        [Display(Name = "Buyers took it", GroupName = "02 The cross", Order = 235)]
        public MColor BuyColor
        {
            get { return _buyColor; }
            set { _buyColor = value; }
        }

        [Display(Name = "Sellers took it", GroupName = "02 The cross", Order = 240)]
        public MColor SellColor
        {
            get { return _sellColor; }
            set { _sellColor = value; }
        }

        #endregion

        #region Settings -- cluster search

        [Display(Name = "Run the cluster search", GroupName = "03 Cluster search", Order = 300,
                 Description = "Inside the crossing bar, find the prices that carried genuinely " +
                               "one-sided size. This is what tells a flip somebody paid for from " +
                               "a flip that drifted over zero.")]
        public bool RunClusterSearch { get; set; } = true;

        [Display(Name = "Cluster needs volume of", GroupName = "03 Cluster search", Order = 305,
                 Description = "Contracts traded at the price, both sides together.")]
        [Range(0, 1000000)]
        public int MinClusterVolume { get; set; } = 60;

        [Display(Name = "Cluster needs delta of", GroupName = "03 Cluster search", Order = 310,
                 Description = "How far apart the two sides have to be at that price, in contracts.")]
        [Range(0, 1000000)]
        public int MinClusterDelta { get; set; } = 30;

        [Display(Name = "Cluster needs a lean of (%)", GroupName = "03 Cluster search", Order = 315,
                 Description = "The same difference as a share of the level's own volume. A " +
                               "400-lot level split 210/190 is not one side doing anything.")]
        [Range(0, 100)]
        public int MinClusterLean { get; set; } = 35;

        [Display(Name = "Only clusters on the flip's side", GroupName = "03 Cluster search", Order = 320,
                 Description = "Keep only the levels the incoming side won. Off counts both, which " +
                               "reads the bar as a fight rather than as a takeover.")]
        public bool ClustersOnFlipSideOnly { get; set; } = true;

        [Display(Name = "No qualifying cluster, no cross", GroupName = "03 Cluster search", Order = 325,
                 Description = "Off by default: an unbacked flip is drawn DASHED instead of being " +
                               "hidden, because a flip nothing paid for is still worth seeing -- " +
                               "it is just worth less.")]
        public bool RequireCluster { get; set; } = false;

        [Display(Name = "Mark the clusters on the flip bar", GroupName = "03 Cluster search", Order = 330)]
        public bool MarkClusters { get; set; } = true;

        #endregion

        #region Settings -- flip zones

        [Display(Name = "Keep the flip levels", GroupName = "06 Flip zones", Order = 600,
                 Description = "A cross is a moment and it stops mattering when its session ends. " +
                               "The PRICE it left behind does not. Older sessions keep their " +
                               "levels as bands here, without the vertical or the label -- the " +
                               "level is the part still worth anything two sessions later.")]
        public bool ShowZones { get; set; } = true;

        [Display(Name = "Sessions to keep", GroupName = "06 Flip zones", Order = 605,
                 Description = "How far back the levels go. The session in progress is always " +
                               "included and draws full crosses instead of bands.")]
        [Range(1, 30)]
        public int KeepSessions { get; set; } = 3;

        [Display(Name = "Most levels to draw", GroupName = "06 Flip zones", Order = 610)]
        [Range(1, 60)]
        public int MaxZones { get; set; } = 8;

        [Display(Name = "Merge levels that overlap", GroupName = "06 Flip zones", Order = 615,
                 Description = "A price two different sessions both turned on is one level that " +
                               "has done it twice, and that is the whole reason to keep old " +
                               "sessions. Merged levels are drawn heavier and count the sessions " +
                               "behind them. Flips from the SAME session never add to that count.")]
        public bool MergeZones { get; set; } = true;

        [Display(Name = "Label the levels", GroupName = "06 Flip zones", Order = 620)]
        public bool LabelZones { get; set; } = true;

        [Display(Name = "Level colour", GroupName = "06 Flip zones", Order = 625)]
        public MColor ZoneColor
        {
            get { return _zoneColor; }
            set { _zoneColor = value; }
        }

        #endregion

        #region Settings -- lines in the sand

        [Display(Name = "Find lines in the sand", GroupName = "07 Lines in the sand", Order = 700,
                 Description = "Prices that kept absorbing one-sided aggression across several " +
                               "bars of the footprint. THIS IS EVIDENCE, NOT AN IDENTIFICATION: " +
                               "there is no order book behind it. A refreshing iceberg leaves this " +
                               "footprint, and so does one big resting order that was never " +
                               "replenished. What is measured is repeat one-sided absorption at a " +
                               "single price -- which is the thing you can actually trade against.")]
        public bool ShowIcebergs { get; set; } = true;

        [Display(Name = "Look back (bars)", GroupName = "07 Lines in the sand", Order = 705,
                 Description = "A fixed window ending at the newest bar, never the visible range. " +
                               "A level that moved when you scrolled would not be a level.")]
        [Range(5, 500)]
        public int IceWindow { get; set; } = 60;

        [Display(Name = "Needs volume of", GroupName = "07 Lines in the sand", Order = 710,
                 Description = "Contracts traded at the price across the whole window.")]
        [Range(0, 10000000)]
        public int IceMinVolume { get; set; } = 400;

        [Display(Name = "Needs a share of the window (%)", GroupName = "07 Lines in the sand", Order = 715,
                 Description = "The same volume against everything the window traded, so the " +
                               "answer does not change character between a quiet hour and a news bar.")]
        [Range(0, 100)]
        public int IceMinShare { get; set; } = 3;

        [Display(Name = "Needs a lean of (%)", GroupName = "07 Lines in the sand", Order = 720,
                 Description = "How one-sided the aggression into it was. One side kept paying and " +
                               "the price kept being there to pay into.")]
        [Range(0, 100)]
        public int IceMinLean { get; set; } = 40;

        [Display(Name = "Needs this many bars", GroupName = "07 Lines in the sand", Order = 725,
                 Description = "Separate bars that traded there. One enormous print is a big trade, " +
                               "not a level that kept reloading, and this is what tells them apart.")]
        [Range(1, 100)]
        public int IceMinBars { get; set; } = 4;

        [Display(Name = "Counts as broken after (ticks)", GroupName = "07 Lines in the sand", Order = 730,
                 Description = "A CLOSE this far beyond it, against the passive side. Wicks through " +
                               "a level are the level working, so they are not breaks.")]
        [Range(0, 200)]
        public int IceThroughTicks { get; set; } = 4;

        [Display(Name = "Keep the ones that broke", GroupName = "07 Lines in the sand", Order = 735,
                 Description = "Drawn crossed through. A level that failed is where the size that " +
                               "was defending it got run over, which is worth knowing.")]
        public bool IceKeepBroken { get; set; } = false;

        [Display(Name = "Most lines to draw", GroupName = "07 Lines in the sand", Order = 740)]
        [Range(1, 40)]
        public int MaxIcebergs { get; set; } = 5;

        [Display(Name = "Line colour", GroupName = "07 Lines in the sand", Order = 745)]
        public MColor IceColor
        {
            get { return _iceColor; }
            set { _iceColor = value; }
        }

        #endregion

        #region Settings -- how much room this takes

        [Display(Name = "Layout", GroupName = "04 Space", Order = 400,
                 Description = "CROSSES ONLY takes no panel space at all: the flip is the whole " +
                               "point, and the band underneath is only there to show where it came " +
                               "from. TWO THIN STRIPS gives session delta and bar delta as heat " +
                               "strips along the edge -- about twenty pixels for both. FULL TRACK " +
                               "draws session delta as a line against its own zero, which costs " +
                               "five times that.")]
        public PanelLayout DeltaLayout { get; set; } = PanelLayout.Strips;

        [Display(Name = "Strip height (pixels)", GroupName = "04 Space", Order = 405,
                 Description = "Each of the two strips. Both together take twice this.")]
        [Range(3, 30)]
        public int StripHeight { get; set; } = 9;

        [Display(Name = "Track height (pixels)", GroupName = "04 Space", Order = 410,
                 Description = "Only the full track layout uses this.")]
        [Range(30, 400)]
        public int TrackHeight { get; set; } = 70;

        [Display(Name = "Band sits at the", GroupName = "04 Space", Order = 415)]
        public BandPlace Band { get; set; } = BandPlace.Bottom;

        [Display(Name = "Carry the flip line through the band", GroupName = "04 Space", Order = 420,
                 Description = "The vertical arm of the cross continued through the strips, so the " +
                               "colour turning over and the mark on the chart read as one event.")]
        public bool TrackFlipLines { get; set; } = true;

        #endregion

        #region Settings -- readout

        [Display(Name = "Readout", GroupName = "05 Readout", Order = 500,
                 Description = "ONE LINE is session delta, the side and the last flip in a single " +
                               "line in the corner. FULL BOX adds the arming crossing, what the " +
                               "cluster search found, and any render layer that failed -- which is " +
                               "the only place a failure can be reported, so switch to it if " +
                               "something looks missing.")]
        public ReadoutStyle Readout { get; set; } = ReadoutStyle.OneLine;

        [Display(Name = "Readout corner", GroupName = "05 Readout", Order = 505)]
        public BoxCorner ReadoutCorner { get; set; } = BoxCorner.TopLeft;

        #endregion

        #region Settings -- clock

        [Display(Name = "Your time zone", GroupName = "08 Clock", Order = 700,
                 Description = "Every time printed by this indicator is in this zone and no other. " +
                               "Houston is 'Central Standard Time'.")]
        public string ZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "08 Clock", Order = 705,
                 Description = "Whether the times stamped on bars are UTC or already your zone. " +
                               "Auto works it out from the data and says which way it went; it " +
                               "never guesses, and reports instead of printing a shifted time.")]
        public BarClock Clock { get; set; } = BarClock.Auto;

        #endregion

        #region Calculation

        protected override void OnRecalculate()
        {
            _engine.Reset();
            _barDelta.Clear();
            _cum.Clear();
            _sessionOf.Clear();
            _marks.Clear();
            _sessions = 0;
            _markSignature = null;
            _clock = null;
            _clockKey = null;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            Grow(bar);

            var candle = GetCandle(bar);
            if (candle == null) return;

            var newSession = bar == 0 || IsNewSession(bar);
            if (newSession) _sessions = bar == 0 ? 1 : _sessions + 1;

            _engine.ConfirmContracts = ConfirmContracts;
            _engine.MinBarsBetween = MinBarsBetweenCrosses;
            _engine.MarkFirstSide = MarkFirstSide;

            var delta = candle.Delta;
            _barDelta[bar] = delta;

            Flip flip;
            _engine.Feed(bar, candle.Time, delta, newSession, out flip);

            _cum[bar] = _engine.Read.Cum;
            _sessionOf[bar] = _sessions;

            // A mark is built once, from the cluster settings in force at the time, so changing
            // those settings has to throw the built ones away. The platform normally recalculates
            // on a settings change and this would never fire; it is here so that a build which
            // did not recalculate shows the new settings rather than yesterday's answer under
            // today's labels.
            var signature = string.Join("|", RunClusterSearch, MinClusterVolume, MinClusterDelta,
                                        MinClusterLean, ClustersOnFlipSideOnly, CrossPrice);

            if (signature != _markSignature)
            {
                _markSignature = signature;
                _marks.Clear();
            }

            SyncMarks();
        }

        private void Grow(int bar)
        {
            while (_barDelta.Count <= bar)
            {
                _barDelta.Add(0m);
                _cum.Add(0m);
                _sessionOf.Add(0);
            }
        }

        /// <summary>
        /// Keeps the drawn crosses level with the engine's flips. The forming bar can arm a flip
        /// and then take it back on the next tick, so this has to be able to shrink as well as
        /// grow -- the engine truncates from the end, so following it is a truncate and a top-up.
        /// </summary>
        private void SyncMarks()
        {
            var flips = _engine.Flips;

            if (_marks.Count > flips.Count)
                _marks.RemoveRange(flips.Count, _marks.Count - flips.Count);

            // A mark already built for a different flip at the same index is a rebuild, not a
            // top-up: the bar it was anchored to changed under it.
            for (var i = 0; i < _marks.Count; i++)
            {
                if (_marks[i].Flip.Bar == flips[i].Bar && _marks[i].Flip.Sign == flips[i].Sign) continue;

                _marks.RemoveRange(i, _marks.Count - i);
                break;
            }

            while (_marks.Count < flips.Count)
                _marks.Add(Build(flips[_marks.Count]));
        }

        private CrossMark Build(Flip flip)
        {
            var mark = new CrossMark();
            mark.Flip = flip;
            mark.Session = flip.Bar >= 0 && flip.Bar < _sessionOf.Count ? _sessionOf[flip.Bar] : _sessions;

            var candle = GetCandle(flip.Bar);
            mark.Close = candle == null ? 0m : candle.Close;

            var hits = new List<ClusterHit>();

            if (candle != null && RunClusterSearch)
            {
                var levels = new List<PriceVolume>();
                var count = 0;

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null) continue;
                    if (++count > MaxBarLevels) break;

                    var item = new PriceVolume();
                    item.Price = level.Price;
                    item.Volume = level.Volume;
                    item.Bid = level.Bid;
                    item.Ask = level.Ask;
                    levels.Add(item);
                }

                var filter = new ClusterFilter();
                filter.MinVolume = MinClusterVolume;
                filter.MinDelta = MinClusterDelta;
                filter.MinLeanPercent = MinClusterLean;
                filter.Sign = ClustersOnFlipSideOnly ? flip.Sign : 0;

                hits = ClusterSearch.Find(levels, filter);
            }

            mark.ClusterCount = hits.Count;

            for (var i = 0; i < hits.Count; i++) mark.ClusterVolume += hits[i].Volume;

            ClusterHit best;
            mark.HasBest = ClusterSearch.Biggest(hits, out best);
            mark.Best = best;

            var price = CrossMath.Price(CrossPrice, mark.Close, hits);
            mark.Price = price.Price;
            mark.PriceNote = price.Note;

            var start = flip.SessionStartBar >= 0 && flip.SessionStartBar < _barDelta.Count
                ? SessionStamp(flip.SessionStartBar)
                : flip.Time;

            mark.Zone = ZoneMath.Zone(flip, price.Price, hits, mark.Session, start);

            return mark;
        }

        #endregion

        #region Rendering

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            var container = chart == null ? null : chart.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;
            if (region.Width <= 0 || region.Height <= 0) return;

            if (_cum.Count < 2)
            {
                Status(context, region, "Ocean Delta Cross: no bars loaded yet.");
                return;
            }

            var first = FirstVisibleBarNumber;
            var last = LastVisibleBarNumber;
            if (first < 0) first = 0;
            if (last > _cum.Count - 1) last = _cum.Count - 1;
            if (last < first) return;

            if (last - first + 1 > MaxVisibleBars)
            {
                Status(context, region, "Ocean Delta Cross: " + (last - first + 1) +
                                        " bars in view. Zoom in.");
                return;
            }

            context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);
            context.SetClip(region);

            // Every layer is caught on its own. A layer that throws otherwise takes down every
            // layer after it, silently -- including the readout, the one thing that could have
            // said something was wrong. Failures land IN the readout instead.
            var errors = new List<string>();
            var band = BandRect(region);
            var visible = PickCrosses(last);

            // Settled before anything draws: the cross labels print times too, and a label that
            // rendered before the readout had resolved the clock would say so on every flip.
            var clock = Resolve(last);

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            var zones = PickZones(last);
            var ice = ShowIcebergs && tick > 0m ? Icebergs(tick) : new IcebergScan();

            if (ice.Problem != null) errors.Add(ice.Problem);

            try
            {
                Layer(errors, "flip zones", () => RenderZones(context, container, region, first, last, zones),
                      ShowZones && zones.Count > 0);
                Layer(errors, "lines in the sand", () => RenderIcebergs(context, container, region, first, last, ice),
                      ShowIcebergs && ice.Found != null && ice.Found.Count > 0);
                Layer(errors, "session track", () => RenderTrack(context, container, TrackRect(band), first, last, visible),
                      DeltaLayout == PanelLayout.Track);
                Layer(errors, "bar ribbon", () => RenderRibbon(context, container, region, TrackRect(band), first, last),
                      DeltaLayout == PanelLayout.Track);
                Layer(errors, "strips", () => RenderStrips(context, container, band, first, last, visible),
                      DeltaLayout == PanelLayout.Strips);
                Layer(errors, "clusters", () => RenderClusters(context, container, first, last, visible),
                      MarkClusters && RunClusterSearch);
                Layer(errors, "crosses", () => RenderCrosses(context, container, region, first, last, visible),
                      ShowCrosses);

                if (Readout != ReadoutStyle.Off)
                {
                    try
                    {
                        if (Readout == ReadoutStyle.OneLine)
                            RenderOneLine(context, region, visible, zones, ice, errors);
                        else
                            RenderReadout(context, region, last, visible, zones, ice, clock, errors);
                    }
                    catch (Exception ex)
                    {
                        Status(context, region, "Ocean Delta Cross: the readout failed -- " +
                                                ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                context.ResetClip();
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
                errors.Add(name + " layer failed -- " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The levels older sessions left behind. The session in progress is deliberately excluded:
        /// it is drawn as full crosses, and a band underneath its own cross would say nothing the
        /// cross has not already said.
        /// </summary>
        private List<FlipZone> PickZones(int last)
        {
            var current = last >= 0 && last < _sessionOf.Count ? _sessionOf[last] : _sessions;
            var oldest = current - KeepSessions;

            var zones = new List<FlipZone>();

            for (var i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];

                if (mark.Session >= current) continue;
                if (mark.Session <= oldest) continue;
                if (RequireCluster && RunClusterSearch && mark.Unbacked) continue;

                zones.Add(mark.Zone);
            }

            // Merge hands back newest first. The unmerged list is oldest first, so it is reversed
            // to match: whichever way the cap falls, it has to keep the newest levels.
            var picked = MergeZones ? ZoneMath.Merge(zones) : Reversed(zones);

            if (picked.Count > MaxZones) picked.RemoveRange(MaxZones, picked.Count - MaxZones);

            return picked;
        }

        private static List<FlipZone> Reversed(List<FlipZone> zones)
        {
            var flipped = new List<FlipZone>(zones.Count);
            for (var i = zones.Count - 1; i >= 0; i--) flipped.Add(zones[i]);
            return flipped;
        }

        /// <summary>
        /// The iceberg scan, cached against the newest bar and the settings that feed it.
        ///
        /// Anchored to the newest bar, NOT the last visible one. Keying this on the visible range
        /// would rebuild the levels every time the chart was scrolled and move them while it did.
        /// </summary>
        private IcebergScan Icebergs(decimal tick)
        {
            var newest = _barDelta.Count - 1;
            if (newest < 0) return new IcebergScan();

            var key = string.Join("|", newest, IceWindow, IceMinVolume, IceMinShare, IceMinLean,
                                  IceMinBars, IceThroughTicks, IceKeepBroken, tick);

            if (_iceKey == key) return _ice;

            var from = newest - IceWindow + 1;
            if (from < 0) from = 0;

            var window = new List<BarLevels>();

            for (var bar = from; bar <= newest; bar++)
            {
                var candle = GetCandle(bar);
                if (candle == null) continue;

                var levels = new List<PriceVolume>();
                var count = 0;

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null) continue;
                    if (++count > MaxBarLevels) break;

                    var item = new PriceVolume();
                    item.Price = level.Price;
                    item.Volume = level.Volume;
                    item.Bid = level.Bid;
                    item.Ask = level.Ask;
                    levels.Add(item);
                }

                var entry = new BarLevels();
                entry.Bar = bar;
                entry.Close = candle.Close;
                entry.High = candle.High;
                entry.Low = candle.Low;
                entry.Levels = levels;
                window.Add(entry);
            }

            var filter = new IcebergFilter();
            filter.MinVolume = IceMinVolume;
            filter.MinSharePercent = IceMinShare;
            filter.MinLeanPercent = IceMinLean;
            filter.MinBars = IceMinBars;
            filter.ThroughTicks = IceThroughTicks;
            filter.KeepBroken = IceKeepBroken;

            _ice = IcebergSearch.Scan(window, tick, filter, MaxIceLevels);
            _iceKey = key;

            return _ice;
        }

        /// <summary>
        /// Old sessions' flip levels, as bands from the bar that made them to the right edge. No
        /// vertical and no time -- the moment has gone, the price is what is left.
        /// </summary>
        private void RenderZones(RenderContext context, IChartContainer container, Rectangle region,
                                 int first, int last, List<FlipZone> zones)
        {
            var rgb = ToColor(_zoneColor);

            for (var i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];

                var top = container.GetYByPrice(zone.High, false);
                var bottom = container.GetYByPrice(zone.Low, false);
                if (bottom < top) { var swap = top; top = bottom; bottom = swap; }

                // A zone with no cluster span behind it is a price, not a band. It still has to be
                // visible, but it is drawn as the thin thing it is rather than padded out into a
                // band nothing traded in.
                var bare = !zone.FromClusters || bottom - top < 2;
                if (bare) bottom = top + 2;

                if (bottom < region.Top || top > region.Bottom) continue;

                var left = zone.Bar >= first && zone.Bar <= last
                    ? container.GetXByBar(zone.Bar, true)
                    : region.Left;

                var stacked = zone.Sessions > 1;
                var fill = stacked ? 70 : 40;
                var edge = stacked ? 220 : 140;

                var rect = new Rectangle(left, top, Math.Max(1, region.Right - left), bottom - top);

                context.FillRectangle(Color.FromArgb(fill, rgb.R, rgb.G, rgb.B), rect);
                context.DrawLine(new RenderPen(Color.FromArgb(edge, rgb.R, rgb.G, rgb.B),
                                               stacked ? 2f : 1f,
                                               bare ? System.Drawing.Drawing2D.DashStyle.Dash
                                                    : System.Drawing.Drawing2D.DashStyle.Solid),
                                 left, top, region.Right, top);

                if (!LabelZones) continue;

                var text = Price(zone.Price) + "  " + DayStamp(zone.SessionStart) +
                           (stacked ? "  x" + zone.Sessions : string.Empty);

                Tag(context, text, left + 4, top - 1, Color.FromArgb(255, rgb.R, rgb.G, rgb.B));
            }
        }

        /// <summary>
        /// The lines in the sand: prices that kept absorbing. One line each, from the bar the level
        /// first traded to the right edge, with the side that was standing there.
        /// </summary>
        private void RenderIcebergs(RenderContext context, IChartContainer container, Rectangle region,
                                    int first, int last, IcebergScan scan)
        {
            var rgb = ToColor(_iceColor);
            var drawn = 0;

            for (var i = 0; i < scan.Found.Count && drawn < MaxIcebergs; i++)
            {
                var ice = scan.Found[i];

                var y = container.GetYByPrice(ice.Price, false);
                if (y < region.Top || y > region.Bottom) continue;

                var left = ice.FirstBar >= first && ice.FirstBar <= last
                    ? container.GetXByBar(ice.FirstBar, true)
                    : region.Left;

                var pen = ice.Held
                    ? new RenderPen(Color.FromArgb(235, rgb.R, rgb.G, rgb.B), 2f)
                    : new RenderPen(Color.FromArgb(120, rgb.R, rgb.G, rgb.B), 1f,
                                    System.Drawing.Drawing2D.DashStyle.Dash);

                context.DrawLine(pen, left, y, region.Right, y);

                // The passive side is the one that was standing there, so the arrow points the way
                // the level defends: a bid that kept absorbing sellers holds price up.
                var text = (ice.Side > 0 ? "^ " : "v ") + Price(ice.Price) +
                           "  x" + ice.Bars + "  " + Size(ice.Volume) +
                           (ice.Held ? string.Empty : "  broken x" + ice.Breaks);

                Tag(context, text, left + 4, y - 1,
                    Color.FromArgb(ice.Held ? 255 : 150, rgb.R, rgb.G, rgb.B));

                drawn++;
            }
        }

        /// <summary>A one-line caption on a dark plate, sitting just above the line it names.</summary>
        private void Tag(RenderContext context, string text, int x, int y, Color colour)
        {
            var size = context.MeasureString(text, _font);

            context.FillRectangle(Color.FromArgb(205, 12, 14, 19),
                                  new Rectangle(x - 2, y - size.Height, size.Width + 4, size.Height));

            context.DrawString(text, _font, colour, x, y - size.Height);
        }

        /// <summary>The crosses that pass the drawing filters, oldest first.</summary>
        private List<CrossMark> PickCrosses(int last)
        {
            var picked = new List<CrossMark>();
            var session = last >= 0 && last < _sessionOf.Count ? _sessionOf[last] : _sessions;

            for (var i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];

                if (ThisSessionOnly && mark.Session != session) continue;
                if (RequireCluster && RunClusterSearch && mark.Unbacked) continue;

                picked.Add(mark);
            }

            if (picked.Count > MaxCrosses) picked.RemoveRange(0, picked.Count - MaxCrosses);

            return picked;
        }

        /// <summary>
        /// Every pixel this indicator spends below the candles. In CROSSES ONLY it is empty, and
        /// that is the point of that layout -- the chart gets the whole panel back and the flip is
        /// still marked, because the mark was always the deliverable and the band was only ever
        /// the working shown.
        /// </summary>
        private Rectangle BandRect(Rectangle region)
        {
            var height = Reserved();
            if (height <= 0) return new Rectangle(region.Left, region.Bottom, region.Width, 0);

            // Never more than a third of the panel, whatever the settings say. A band that has
            // pushed price into a quarter of its own chart has stopped being a reference.
            var cap = region.Height / 3;
            if (cap > 8 && height > cap) height = cap;

            var top = Band == BandPlace.Bottom ? region.Bottom - height : region.Top;

            return new Rectangle(region.Left, top, region.Width, height);
        }

        private int Reserved()
        {
            switch (DeltaLayout)
            {
                case PanelLayout.Strips: return StripHeight * 2 + 1;
                case PanelLayout.Track: return TrackHeight + StripHeight + 2;
                default: return 0;
            }
        }

        /// <summary>The track's own share of the band, with the bar ribbon taking the rest.</summary>
        private Rectangle TrackRect(Rectangle band)
        {
            var ribbon = StripHeight + 2;
            var height = band.Height - ribbon;
            if (height < 10) height = band.Height;

            var top = Band == BandPlace.Bottom ? band.Top : band.Top + ribbon;

            return new Rectangle(band.Left, top, band.Width, height);
        }

        /// <summary>
        /// Session delta and bar delta as two heat strips: the colour carries the sign, the weight
        /// carries the size. Twenty pixels for both, against the ninety the track wanted.
        ///
        /// The only thing the track shows that this does not is the SHAPE of the run, and the
        /// cross is not read from the shape. Where the session strip turns over IS the crossing,
        /// which is the one thing the band has to make visible.
        /// </summary>
        private void RenderStrips(RenderContext context, IChartContainer container, Rectangle band,
                                  int first, int last, List<CrossMark> visible)
        {
            if (band.Height < 4) return;

            var height = Math.Max(2, (band.Height - 1) / 2);
            var sessionTop = band.Top;
            var barTop = band.Top + height + 1;

            var sessionReach = 0m;
            var barReach = 0m;

            for (var bar = first; bar <= last; bar++)
            {
                var cum = _cum[bar] < 0m ? -_cum[bar] : _cum[bar];
                if (cum > sessionReach) sessionReach = cum;

                var delta = _barDelta[bar] < 0m ? -_barDelta[bar] : _barDelta[bar];
                if (delta > barReach) barReach = delta;
            }

            context.FillRectangle(Color.FromArgb(60, 18, 20, 26), band);

            var width = Math.Max(1, (int)container.BarsWidth);

            for (var bar = first; bar <= last; bar++)
            {
                var x = container.GetXByBar(bar, true);

                Cell(context, x, sessionTop, width, height, _cum[bar], sessionReach);
                Cell(context, x, barTop, width, height, _barDelta[bar], barReach);
            }

            // Captions only when there is room for them to sit inside a strip. Text taller than
            // the strip it names is worse than no caption -- it lands on the candles.
            if (StripHeight >= 12)
            {
                context.DrawString("SESSION", _font, Color.FromArgb(170, 170, 175, 190),
                                   band.Left + 3, sessionTop);
                context.DrawString("BAR", _font, Color.FromArgb(170, 170, 175, 190),
                                   band.Left + 3, barTop);
            }

            if (!TrackFlipLines) return;

            for (var i = 0; i < visible.Count; i++)
            {
                var mark = visible[i];
                if (mark.Flip.Bar < first || mark.Flip.Bar > last) continue;

                var x = container.GetXByBar(mark.Flip.Bar, true) + width / 2;
                var rgb = ToColor(mark.Flip.Sign > 0 ? _buyColor : _sellColor);

                context.DrawLine(new RenderPen(Color.FromArgb(230, rgb.R, rgb.G, rgb.B), 1f),
                                 x, band.Top, x, band.Bottom);
            }
        }

        private void Cell(RenderContext context, int x, int top, int width, int height,
                          decimal value, decimal reach)
        {
            if (value == 0m || reach <= 0m) return;

            var size = value < 0m ? -value : value;

            var alpha = 45 + (int)(size / reach * 205m);
            if (alpha > 255) alpha = 255;
            if (alpha < 0) alpha = 0;

            var rgb = ToColor(value > 0m ? _buyColor : _sellColor);

            context.FillRectangle(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B),
                                  new Rectangle(x, top, width, height));
        }

        /// <summary>
        /// Session delta as a track with its own zero line. The scale is taken from the bars in
        /// view, so the shape of what is on screen is readable rather than being flattened by an
        /// extreme that happened somewhere else.
        /// </summary>
        private void RenderTrack(RenderContext context, IChartContainer container, Rectangle band,
                                 int first, int last, List<CrossMark> visible)
        {
            var reach = 0m;

            for (var bar = first; bar <= last; bar++)
            {
                var size = _cum[bar] < 0m ? -_cum[bar] : _cum[bar];
                if (size > reach) reach = size;
            }

            context.FillRectangle(Color.FromArgb(55, 18, 20, 26), band);

            var zero = band.Top + band.Height / 2;

            context.DrawLine(new RenderPen(Color.FromArgb(110, 130, 136, 152), 1f,
                                           System.Drawing.Drawing2D.DashStyle.Dot),
                             band.Left, zero, band.Right, zero);

            context.DrawString("SESSION DELTA", _font, Color.FromArgb(150, 150, 150, 165),
                               band.Left + 4, band.Top + 2);

            if (reach <= 0m) return;

            var half = band.Height / 2 - 3;
            var previousX = 0;
            var previousY = 0;
            var have = false;

            for (var bar = first; bar <= last; bar++)
            {
                var x = container.GetXByBar(bar, true) + (int)container.BarsWidth / 2;
                var y = zero - (int)(_cum[bar] / reach * half);

                var up = _cum[bar] >= 0m;
                var rgb = ToColor(up ? _buyColor : _sellColor);

                // The area under the track carries the sign, so which side is winning reads at a
                // glance without having to follow the line.
                var top = up ? y : zero;
                var height = up ? zero - y : y - zero;
                if (height > 0)
                    context.FillRectangle(Color.FromArgb(45, rgb.R, rgb.G, rgb.B),
                                          new Rectangle(x, top, Math.Max(1, (int)container.BarsWidth), height));

                if (have)
                    context.DrawLine(new RenderPen(Color.FromArgb(230, rgb.R, rgb.G, rgb.B), 1.5f),
                                     previousX, previousY, x, y);

                previousX = x;
                previousY = y;
                have = true;
            }

            if (!TrackFlipLines) return;

            for (var i = 0; i < visible.Count; i++)
            {
                var mark = visible[i];
                if (mark.Flip.Bar < first || mark.Flip.Bar > last) continue;

                var x = container.GetXByBar(mark.Flip.Bar, true) + (int)container.BarsWidth / 2;
                var rgb = ToColor(mark.Flip.Sign > 0 ? _buyColor : _sellColor);

                context.DrawLine(new RenderPen(Color.FromArgb(200, rgb.R, rgb.G, rgb.B), 1f),
                                 x, band.Top, x, band.Bottom);
            }
        }

        /// <summary>
        /// Bar delta, one cell per bar, scaled against the biggest bar in view. Sits outside the
        /// session track so the two never have to share a scale -- one is a sum, the other is not.
        /// </summary>
        private void RenderRibbon(RenderContext context, IChartContainer container, Rectangle region,
                                  Rectangle band, int first, int last)
        {
            var height = StripHeight;
            var top = Band == BandPlace.Bottom ? band.Bottom + 2 : band.Top - height - 2;

            if (top < region.Top || top + height > region.Bottom) return;

            var reach = 0m;

            for (var bar = first; bar <= last; bar++)
            {
                var size = _barDelta[bar] < 0m ? -_barDelta[bar] : _barDelta[bar];
                if (size > reach) reach = size;
            }

            context.FillRectangle(Color.FromArgb(55, 18, 20, 26),
                                  new Rectangle(region.Left, top, region.Width, height));

            if (reach <= 0m) return;

            var width = Math.Max(1, (int)container.BarsWidth - 1);

            for (var bar = first; bar <= last; bar++)
            {
                var delta = _barDelta[bar];
                if (delta == 0m) continue;

                var size = delta < 0m ? -delta : delta;
                var alpha = 60 + (int)(size / reach * 195m);
                if (alpha > 255) alpha = 255;

                var rgb = ToColor(delta > 0m ? _buyColor : _sellColor);
                var x = container.GetXByBar(bar, true);

                context.FillRectangle(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B),
                                      new Rectangle(x, top, width, height));
            }

            context.DrawString("BAR DELTA", _font, Color.FromArgb(150, 150, 150, 165),
                               region.Left + 4, top - 1);
        }

        /// <summary>
        /// The qualifying clusters, drawn only on the bars that flipped. Marking them on every bar
        /// was the first thing tried and it buried the crosses; here they are evidence for a
        /// specific claim, so they belong on the bar making it.
        /// </summary>
        private void RenderClusters(RenderContext context, IChartContainer container,
                                    int first, int last, List<CrossMark> visible)
        {
            for (var i = 0; i < visible.Count; i++)
            {
                var mark = visible[i];
                if (!mark.HasBest) continue;
                if (mark.Flip.Bar < first || mark.Flip.Bar > last) continue;

                var rgb = ToColor(mark.Flip.Sign > 0 ? _buyColor : _sellColor);
                var x = container.GetXByBar(mark.Flip.Bar, true);
                var width = Math.Max(3, (int)container.BarsWidth);
                var y = container.GetYByPrice(mark.Best.Price, false);

                context.FillRectangle(Color.FromArgb(210, rgb.R, rgb.G, rgb.B),
                                      new Rectangle(x - width, y - 2, width, 4));

                var text = Contracts(mark.Best.Delta);
                var size = context.MeasureString(text, _font);

                context.FillRectangle(Color.FromArgb(200, 14, 16, 21),
                                      new Rectangle(x - width - size.Width - 5, y - size.Height / 2,
                                                    size.Width + 4, size.Height));

                context.DrawString(text, _font, Color.FromArgb(235, rgb.R, rgb.G, rgb.B),
                                   x - width - size.Width - 3, y - size.Height / 2);
            }
        }

        /// <summary>The mark itself: vertical on the bar, horizontal at the price.</summary>
        private void RenderCrosses(RenderContext context, IChartContainer container, Rectangle region,
                                   int first, int last, List<CrossMark> visible)
        {
            for (var i = 0; i < visible.Count; i++)
            {
                var mark = visible[i];
                var flip = mark.Flip;

                var onScreen = flip.Bar >= first && flip.Bar <= last;
                var rgb = ToColor(flip.Sign > 0 ? _buyColor : _sellColor);
                var faded = i < visible.Count - 1;
                var alpha = faded ? 150 : 240;

                var dashed = RunClusterSearch && mark.Unbacked;
                var pen = dashed
                    ? new RenderPen(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B), CrossWidth,
                                    System.Drawing.Drawing2D.DashStyle.Dash)
                    : new RenderPen(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B), CrossWidth);

                var y = container.GetYByPrice(mark.Price, false);

                var left = region.Left;
                if (Reach == HorizontalReach.FromTheFlip && onScreen)
                    left = container.GetXByBar(flip.Bar, true);

                if (y >= region.Top && y <= region.Bottom)
                    context.DrawLine(pen, left, y, region.Right, y);

                if (!onScreen) continue;

                var x = container.GetXByBar(flip.Bar, true) + (int)container.BarsWidth / 2;
                context.DrawLine(pen, x, region.Top, x, region.Bottom);

                if (y >= region.Top && y <= region.Bottom)
                    context.FillRectangle(Color.FromArgb(255, rgb.R, rgb.G, rgb.B),
                                          new Rectangle(x - 3, y - 3, 7, 7));

                if (CrossLabels != LabelDetail.Off)
                    CrossLabel(context, region, mark, x, y, rgb, dashed);
            }
        }

        private void CrossLabel(RenderContext context, Rectangle region, CrossMark mark,
                                int x, int y, Color rgb, bool dashed)
        {
            var flip = mark.Flip;

            var head = (flip.Sign > 0 ? "FLIP UP" : "FLIP DOWN") + "  " + Stamp(flip.Time);

            // The short label keeps the price on the head line: a cross whose horizontal you can
            // see but whose price you cannot read is half a mark.
            if (CrossLabels == LabelDetail.Short)
            {
                var one = head + "  " + Price(mark.Price);
                if (RunClusterSearch && mark.Unbacked) one += "  (unbacked)";

                Label(context, region, new[] { one }, flip.Sign > 0, x, y, rgb, dashed);
                return;
            }

            var body = Price(mark.Price) + "  " + mark.PriceNote;
            var tail = Contracts(flip.ConfirmCum) + " past zero" +
                       (RunClusterSearch
                           ? mark.Unbacked
                               ? "  ·  no cluster backed it"
                               : "  ·  " + mark.ClusterCount + " cluster" + (mark.ClusterCount == 1 ? "" : "s")
                           : string.Empty);

            Label(context, region, new[] { head, body, tail }, flip.Sign > 0, x, y, rgb, dashed);
        }

        private void Label(RenderContext context, Rectangle region, string[] lines, bool up,
                           int x, int y, Color rgb, bool dashed)
        {
            var width = 0;
            var height = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var size = context.MeasureString(lines[i], _labelFont);
                if (size.Width > width) width = size.Width;
                height += size.Height;
            }

            var boxWidth = width + 10;
            var boxHeight = height + 6;

            var boxLeft = x + 6;
            if (boxLeft + boxWidth > region.Right) boxLeft = x - 6 - boxWidth;

            var boxTop = up ? y - boxHeight - 8 : y + 8;
            if (boxTop < region.Top) boxTop = region.Top + 2;
            if (boxTop + boxHeight > region.Bottom) boxTop = region.Bottom - boxHeight - 2;

            var box = new Rectangle(boxLeft, boxTop, boxWidth, boxHeight);

            context.FillRectangle(Color.FromArgb(228, 12, 14, 19), box);
            context.DrawRectangle(new RenderPen(Color.FromArgb(dashed ? 120 : 220, rgb.R, rgb.G, rgb.B), 1f), box);

            var ty = boxTop + 3;

            for (var i = 0; i < lines.Length; i++)
            {
                var colour = i == 0
                    ? Color.FromArgb(255, rgb.R, rgb.G, rgb.B)
                    : Color.FromArgb(210, 205, 210, 220);

                context.DrawString(lines[i], _labelFont, colour, boxLeft + 5, ty);
                ty += context.MeasureString(lines[i], _labelFont).Height;
            }
        }

        /// <summary>
        /// The whole read in one line: where session delta stands, who has it, and the last flip.
        ///
        /// A failed layer cannot be described here -- there is no room -- so this says HOW MANY
        /// failed and where to go to read them. Silently dropping that count would leave a missing
        /// layer looking like a quiet market.
        /// </summary>
        private void RenderOneLine(RenderContext context, Rectangle region, List<CrossMark> visible,
                                   List<FlipZone> zones, IcebergScan ice, List<string> errors)
        {
            var read = _engine.Read;

            var side = read.Side > 0 ? "buyers" : read.Side < 0 ? "sellers" : "no side yet";
            var text = "DELTA  session " + Contracts(read.Cum) + "  ·  " + side;

            if (read.Arming)
            {
                var need = ConfirmContracts - (read.ArmCum < 0m ? -read.ArmCum : read.ArmCum);
                text += "  ·  arming " + (read.ArmSign > 0 ? "up" : "down") + ", " +
                        Contracts(need > 0m ? need : 0m) + " to go";
            }

            if (_marks.Count > 0)
            {
                var mark = _marks[_marks.Count - 1];

                text += "  ·  last flip " + (mark.Flip.Sign > 0 ? "UP " : "DOWN ") +
                        Stamp(mark.Flip.Time) + " @ " + Price(mark.Price);

                if (RunClusterSearch)
                    text += mark.Unbacked ? " (unbacked)" : " (" + mark.ClusterCount + " clusters)";
            }

            if (ShowZones && zones.Count > 0)
                text += "  ·  " + zones.Count + " kept level" + (zones.Count == 1 ? "" : "s");

            if (ShowIcebergs && ice.Found != null && ice.Found.Count > 0)
                text += "  ·  " + Math.Min(ice.Found.Count, MaxIcebergs) + " in the sand";

            if (errors.Count > 0)
                text += "  ·  !! " + errors.Count + " layer" + (errors.Count == 1 ? "" : "s") +
                        " failed - switch the readout to the full box";

            var size = context.MeasureString(text, _boxFont);

            var left = ReadoutCorner == BoxCorner.TopLeft || ReadoutCorner == BoxCorner.BottomLeft
                ? region.Left + 8
                : region.Right - size.Width - 14;

            var top = ReadoutCorner == BoxCorner.TopLeft || ReadoutCorner == BoxCorner.TopRight
                ? region.Top + 6
                : region.Bottom - size.Height - 8;

            context.FillRectangle(Color.FromArgb(190, 12, 14, 19),
                                  new Rectangle(left - 4, top - 2, size.Width + 8, size.Height + 4));

            context.DrawString(text, _boxFont,
                               errors.Count > 0
                                   ? Color.FromArgb(255, 240, 120, 120)
                                   : Color.FromArgb(230, 205, 210, 222),
                               left, top);
        }

        /// <summary>
        /// Everything in words. The chart shows where; this says what, how far past zero it had to
        /// travel to be believed, and what the cluster search found backing it.
        /// </summary>
        private void RenderReadout(RenderContext context, Rectangle region, int last,
                                   List<CrossMark> visible, List<FlipZone> zones, IcebergScan ice,
                                   TimeContext clock, List<string> errors)
        {
            var read = _engine.Read;
            var lines = new List<string>();

            lines.Add("OCEAN DELTA CROSS");
            lines.Add(string.Empty);

            lines.Add("SESSION   " + (read.Open
                ? Stamp(read.StartTime) + " · " + read.Bars + " bars"
                : "not started"));

            if (clock != null && !clock.Valid) lines.Add("          " + clock.Error);
            else if (clock != null) lines.Add("          bar clock " + clock.Explain);

            lines.Add(string.Empty);
            lines.Add("DELTA     session  " + Contracts(read.Cum));
            lines.Add("          bar      " + Contracts(last >= 0 && last < _barDelta.Count
                                                            ? _barDelta[last] : 0m));
            lines.Add("          peak     " + Contracts(read.Peak) +
                      "   trough " + Contracts(read.Trough));

            lines.Add(string.Empty);

            var side = read.Side > 0 ? "BUYERS" : read.Side < 0 ? "SELLERS" : "nobody yet";
            lines.Add("SIDE      " + side);

            if (read.Arming)
            {
                var need = ConfirmContracts - Math.Abs(read.ArmCum);
                lines.Add("          ARMING " + (read.ArmSign > 0 ? "up" : "down") +
                          " since bar " + read.ArmBar);
                lines.Add("          needs " + Contracts(need > 0m ? need : 0m) + " more to confirm");
            }

            lines.Add(string.Empty);

            if (_marks.Count == 0)
            {
                lines.Add("FLIP      none yet");
            }
            else
            {
                var mark = _marks[_marks.Count - 1];
                var flip = mark.Flip;

                lines.Add("FLIP      " + (flip.Sign > 0 ? "UP · buyers took it" : "DOWN · sellers took it"));
                lines.Add("          crossed " + Stamp(flip.Time) + " on bar " + flip.Bar);
                lines.Add("          confirmed on bar " + flip.ConfirmBar + " at " +
                          Contracts(flip.ConfirmCum));
                lines.Add("          cross price " + Price(mark.Price) + " · " + mark.PriceNote);
                lines.Add("          zero reached " + (int)(flip.ShareOfBar * 100m) +
                          "% through that bar's delta");

                if (RunClusterSearch)
                {
                    lines.Add("CLUSTERS  " + (mark.ClusterCount == 0
                        ? "none qualified in the flip bar"
                        : mark.ClusterCount + " qualified · " + Contracts(mark.ClusterVolume) + " traded"));

                    if (mark.HasBest)
                        lines.Add("          biggest " + Price(mark.Best.Price) + " · " +
                                  Contracts(mark.Best.Delta) + " of " + Contracts(mark.Best.Volume) +
                                  " (" + (int)mark.Best.LeanPercent + "%)");
                }

                lines.Add(string.Empty);
                lines.Add("CROSSES   " + _marks.Count + " this read · " + visible.Count + " drawn");
            }

            if (ShowZones)
            {
                lines.Add(string.Empty);

                if (zones.Count == 0)
                {
                    lines.Add("LEVELS    none kept from the last " + KeepSessions + " sessions");
                }
                else
                {
                    lines.Add("LEVELS    " + zones.Count + " kept from the last " +
                              KeepSessions + " sessions");

                    for (var i = 0; i < zones.Count && i < 4; i++)
                    {
                        var zone = zones[i];

                        var span = zone.FromClusters && zone.High != zone.Low
                            ? Price(zone.Low) + " - " + Price(zone.High)
                            : Price(zone.Price) + " (price only)";

                        lines.Add("          " + (zone.Sign > 0 ? "up   " : "down ") + span +
                                  "  " + DayStamp(zone.SessionStart) +
                                  (zone.Sessions > 1 ? "  x" + zone.Sessions + " sessions" : string.Empty));
                    }

                    if (zones.Count > 4) lines.Add("          ... " + (zones.Count - 4) + " more");
                }
            }

            if (ShowIcebergs)
            {
                lines.Add(string.Empty);

                var found = ice.Found == null ? 0 : ice.Found.Count;

                if (found == 0)
                {
                    lines.Add("SAND      nothing absorbing in the last " + IceWindow + " bars");
                }
                else
                {
                    lines.Add("SAND      " + found + " price" + (found == 1 ? "" : "s") +
                              " absorbing over " + IceWindow + " bars");

                    for (var i = 0; i < found && i < MaxIcebergs && i < 4; i++)
                    {
                        var at = ice.Found[i];

                        lines.Add("          " + (at.Side > 0 ? "bid  " : "offer") + " " +
                                  Price(at.Price) + "  " + Size(at.Volume) + " over " + at.Bars +
                                  " bars  " + (int)at.LeanPercent + "% lean" +
                                  (at.Held ? string.Empty : "  BROKEN"));
                    }

                    lines.Add("          absorption, not an order book - see the README");
                }
            }

            if (RequireCluster && RunClusterSearch)
            {
                lines.Add(string.Empty);
                lines.Add("Unbacked flips are hidden (cluster required).");
            }

            for (var i = 0; i < errors.Count; i++)
            {
                if (i == 0) lines.Add(string.Empty);
                lines.Add("!! " + errors[i]);
            }

            Box(context, region, lines);
        }

        private void Box(RenderContext context, Rectangle region, List<string> lines)
        {
            var width = 0;
            var lineHeight = context.MeasureString("Xg", _boxFont).Height;
            var height = lineHeight * lines.Count;

            for (var i = 0; i < lines.Count; i++)
            {
                var size = context.MeasureString(lines[i], _boxFont);
                if (size.Width > width) width = size.Width;
            }

            var boxWidth = width + 16;
            var boxHeight = height + 12;

            var left = ReadoutCorner == BoxCorner.TopLeft || ReadoutCorner == BoxCorner.BottomLeft
                ? region.Left + 8
                : region.Right - boxWidth - 8;

            var top = ReadoutCorner == BoxCorner.TopLeft || ReadoutCorner == BoxCorner.TopRight
                ? region.Top + 8
                : region.Bottom - boxHeight - 8;

            var box = new Rectangle(left, top, boxWidth, boxHeight);

            context.FillRectangle(Color.FromArgb(232, 12, 14, 19), box);
            context.DrawRectangle(new RenderPen(Color.FromArgb(120, 90, 96, 112), 1f), box);

            var y = top + 6;

            for (var i = 0; i < lines.Count; i++)
            {
                var text = lines[i];

                var colour = text.StartsWith("!!")
                    ? Color.FromArgb(255, 240, 120, 120)
                    : i == 0
                        ? Color.FromArgb(255, 235, 238, 245)
                        : Color.FromArgb(225, 195, 200, 212);

                context.DrawString(text, _boxFont, colour, left + 8, y);
                y += lineHeight;
            }
        }

        private void Status(RenderContext context, Rectangle region, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            context.DrawString(text, _boxFont, Color.FromArgb(195, 195, 205),
                               region.Left + 6, region.Top + 6);
        }

        #endregion

        #region Formatting

        /// <summary>
        /// Resolves the bar clock once and keeps the answer until the settings or the newest bar
        /// change. It never falls back to a guess: a wrong offset does not look wrong, it produces
        /// a plausible time on the wrong bar.
        /// </summary>
        private TimeContext Resolve(int last)
        {
            var key = ZoneId + "|" + Clock;

            // Settled once. Re-run only when the settings change, or when an unsettled answer has
            // had another fifty bars to work with -- the halt test needs several days of them, so
            // a chart that opened short of history gets to settle later rather than never.
            if (_clock != null && _clockKey == key && (_clock.Valid || last - _clockBars < 50))
                return _clock;

            _clockKey = key;
            _clockBars = last;

            // 15 is three in the afternoon: the CME equity index daily halt, on Houston's clock.
            _clock = TimeContext.Create(ZoneId, Clock, new Bars(this, last + 1), DateTime.UtcNow, 15);
            return _clock;
        }

        private string Stamp(DateTime barTime)
        {
            if (barTime == default) return "--:--";
            if (_clock == null || !_clock.Valid) return barTime.ToString("HH:mm") + " (clock unresolved)";

            var local = _clock.ToLocal(barTime);
            return local.ToString("HH:mm") + " " + _clock.Abbrev(local);
        }

        private static string Contracts(decimal value)
        {
            var sign = value > 0m ? "+" : value < 0m ? "-" : string.Empty;
            var size = value < 0m ? -value : value;

            return sign + Math.Round(size, 0).ToString("N0");
        }

        /// <summary>Plain contract count, no sign. Volumes are never negative and a "+" reads wrong.</summary>
        private static string Size(decimal value)
        {
            if (value >= 100000m) return Math.Round(value / 1000m, 0).ToString("N0") + "K";
            if (value >= 10000m) return Math.Round(value / 1000m, 1).ToString("N1") + "K";

            return Math.Round(value, 0).ToString("N0");
        }

        /// <summary>Day of a session, on your clock. "Wed 20".</summary>
        private string DayStamp(DateTime barTime)
        {
            if (barTime == default) return "?";

            var local = _clock == null || !_clock.Valid ? barTime : _clock.ToLocal(barTime);

            return local.ToString("ddd d");
        }

        /// <summary>The stamp on the bar a session opened, for labelling the levels it left.</summary>
        private DateTime SessionStamp(int bar)
        {
            var candle = GetCandle(bar);
            return candle == null ? default : candle.Time;
        }

        private string Price(decimal price)
        {
            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            var digits = tick >= 1m ? 0 : tick >= 0.1m ? 1 : tick >= 0.01m ? 2 : 4;

            return Math.Round(price, digits).ToString("N" + digits);
        }

        private static Color ToColor(MColor c)
        {
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        /// <summary>Bars for the clock resolver, without handing it an ATAS type.</summary>
        private sealed class Bars : IBarWindow
        {
            private readonly OceansDeltaIndicator _owner;

            public Bars(OceansDeltaIndicator owner, int count)
            {
                _owner = owner;
                Count = count < 0 ? 0 : count;
            }

            public int Count { get; }

            public DateTime Time(int bar)
            {
                var candle = _owner.GetCandle(bar);
                return candle == null ? default : candle.Time;
            }

            public decimal Open(int bar)
            {
                var candle = _owner.GetCandle(bar);
                return candle == null ? 0m : candle.Open;
            }

            public decimal High(int bar)
            {
                var candle = _owner.GetCandle(bar);
                return candle == null ? 0m : candle.High;
            }

            public decimal Low(int bar)
            {
                var candle = _owner.GetCandle(bar);
                return candle == null ? 0m : candle.Low;
            }

            public decimal Close(int bar)
            {
                var candle = _owner.GetCandle(bar);
                return candle == null ? 0m : candle.Close;
            }
        }

        #endregion
    }
}
