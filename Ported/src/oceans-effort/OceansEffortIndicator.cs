using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansEffort
{
    public enum ReadoutCorner
    {
        [Display(Name = "Top left")] TopLeft,
        [Display(Name = "Top right")] TopRight,
        [Display(Name = "Bottom left")] BottomLeft,
        [Display(Name = "Bottom right")] BottomRight
    }

    public enum ProfileSide
    {
        [Display(Name = "Right")] Right,
        [Display(Name = "Left")] Left
    }

    public enum RibbonPlace
    {
        [Display(Name = "Bottom of the panel")] Bottom,
        [Display(Name = "Top of the panel")] Top
    }

    /// <summary>
    /// Ocean Effort -- three measurements of who is winning the auction, drawn on the bars they
    /// came from, and a setup marked only where all three agree.
    ///
    /// 1. VALUE MIGRATION. Each bar's own value area and point of control against the bar before
    ///    it, with a track through the value midpoints so the drift is readable at a glance.
    /// 2. EFFORT. Over a rolling window, contracts traded per tick of progress each way, drawn as
    ///    a ribbon along the edge of the panel. The cheaper direction is where price has been
    ///    travelling more easily. This is a measurement of what happened, not a reading of the
    ///    order book -- resting size is not visible here and is not being inferred.
    /// 3. ABSORPTION. A bar where one side aggressed hard and the close went nowhere or the other
    ///    way. Effort with no result: someone passive was filled on the other side.
    ///
    /// A setup is marked only where all three agree, and only one runs at a time: while a setup's
    /// stop is still standing, the bars that keep agreeing with it are the same idea, not new
    /// ones. The stop sits the far side of that bar's value area and the target is the same
    /// distance again.
    /// </summary>
    [DisplayName("Oceans Effort MNQ")]
    [Category("Ocean")]
    public class OceansEffortIndicator : Indicator
    {
        private const int MaxBarTicks = 4000;
        private const int MaxProfileTicks = 20000;
        private const int MaxVisibleBars = 3000;

        private readonly List<BarFacts> _facts = new List<BarFacts>();
        private readonly List<Side> _migration = new List<Side>();
        private readonly List<EffortVerdict> _effort = new List<EffortVerdict>();
        private readonly List<Side> _absorbed = new List<Side>();
        private readonly List<Side> _aggression = new List<Side>();
        private readonly List<decimal> _cumulative = new List<decimal>();
        private readonly List<Divergence> _divergence = new List<Divergence>();
        private readonly List<AbsorptionMark> _marks = new List<AbsorptionMark>();

        private readonly SetupTracker _setups = new SetupTracker();

        // The profile the eye reads, rebuilt from whatever is on screen. Signed by the bars in
        // view and the newest bar's volume so scrolling rebuilds it and a still chart does not.
        private RangeProfile _view;
        private Cluster[] _viewClusters = new Cluster[0];
        private decimal _viewVwap;
        private int _viewFirst = -1;
        private int _viewLast = -1;
        private decimal _viewSignature = -1m;
        private string _viewProblem;
        private string _trackProblem;
        private RegimeRead _regime;
        private decimal _gap;
        private int _riskCap;
        private bool _gapMeasured;
        private bool _riskMeasured;

        private LevelSet _levels;
        private DateTime _levelsStamp;
        private string _levelsPath;
        private string _levelsNote;

        private readonly RenderFont _font = new RenderFont("Arial", 8f);
        private readonly RenderFont _readoutFont = new RenderFont("Arial", 9f);
        private readonly RenderFont _sizeFont = new RenderFont("Arial", 9f, FontStyle.Bold);

        private MColor _buyColor = MColor.FromRgb(60, 210, 130);
        private MColor _sellColor = MColor.FromRgb(230, 70, 90);
        private MColor _flatColor = MColor.FromRgb(120, 125, 140);
        private MColor _absorbColor = MColor.FromRgb(255, 205, 70);
        private MColor _trailColor = MColor.FromRgb(200, 160, 255);
        private MColor _pocColor = MColor.FromRgb(255, 205, 70);
        private MColor _valueColor = MColor.FromRgb(110, 135, 210);
        private MColor _vwapColor = MColor.FromRgb(190, 150, 255);

        public OceansEffortIndicator() : base(true)
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
        }

        #region Settings -- order track

        [Display(Name = "Show the order track", GroupName = "01 Order track", Order = 90,
                 Description = "Each candle's own volume profile drawn beside it, with its value " +
                               "area. This is the read: value migrating bar to bar, in the shape " +
                               "of the profile that moved it.")]
        public bool ShowOrderTrack { get; set; } = true;

        [Display(Name = "Track width (% of the bar slot)", GroupName = "01 Order track", Order = 92)]
        [Range(20, 95)]
        public int TrackWidthPercent { get; set; } = 70;

        [Display(Name = "Needs bars this wide (pixels)", GroupName = "01 Order track", Order = 94,
                 Description = "Below this the profile cannot be read, so it is not drawn and the " +
                               "box says so rather than printing mush.")]
        [Range(4, 200)]
        public int TrackMinBarWidth { get; set; } = 9;

        [Display(Name = "Mark value on the track", GroupName = "01 Order track", Order = 96,
                 Description = "Dashed lines at each candle's value high and low, solid at its " +
                               "point of control. This is what migrates.")]
        public bool ShowTrackValue { get; set; } = true;

        [Display(Name = "Colour a level from (lean %)", GroupName = "01 Order track", Order = 98,
                 Description = "How one-sided a price has to be before it is coloured rather than " +
                               "left neutral.")]
        [Range(5, 100)]
        public decimal TrackLeanPercent { get; set; } = 35m;

        #endregion

        #region Settings -- outside levels

        [Display(Name = "Read a level file", GroupName = "10 Outside levels", Order = 1000,
                 Description = "Levels you keep outside the tape -- option walls, gamma flip, " +
                               "anything. One per line: price, label, support/resistance/pivot. " +
                               "No live options feed exists on the current data plans, so this is " +
                               "the honest way in: you own the file, the model reads it.")]
        public bool UseOutsideLevels { get; set; } = true;

        [Display(Name = "Level file", GroupName = "10 Outside levels", Order = 1005,
                 Description = "Left empty this is %APPDATA%\\ATAS\\Ocean\\levels.csv. " +
                               "Re-read automatically whenever the file changes.")]
        public string LevelFile { get; set; } = "";

        [Display(Name = "Draw them", GroupName = "10 Outside levels", Order = 1010,
                 Description = "Off by default: you already have these on your chart, and this is " +
                               "here to USE them, not to draw a second copy over the first.")]
        public bool DrawOutsideLevels { get; set; } = false;

        [Display(Name = "Standing on one within (ticks)", GroupName = "10 Outside levels", Order = 1015,
                 Description = "How close price has to be for a level to count as underfoot.")]
        [Range(0, 500)]
        public int LevelReach { get; set; } = 8;

        [Display(Name = "Refuse a trade into a wall within (ticks)", GroupName = "10 Outside levels", Order = 1020,
                 Description = "No long just under resistance, no short just above support. This is " +
                               "the one thing these levels are unambiguously good for: naming where " +
                               "a scalp has no room. Zero switches it off.")]
        [Range(0, 500)]
        public int WallVetoTicks { get; set; } = 12;

        [Display(Name = "Refuse when the target is behind a wall", GroupName = "10 Outside levels", Order = 1025,
                 Description = "A one-to-one target on the far side of a wall is a target you were " +
                               "never going to be paid.")]
        public bool VetoBlockedTarget { get; set; } = true;

        #endregion

        #region Settings -- costs

        [Display(Name = "Round-turn cost per contract", GroupName = "11 Costs", Order = 1100,
                 Description = "Commission plus fees, in money, for one contract in and out. At " +
                               "one-to-one this decides everything. Zero means the record is shown " +
                               "before costs and says so -- it does not mean trading is free.")]
        [Range(0.0, 1000.0)]
        public decimal RoundTurnCost { get; set; } = 0m;

        #endregion

        #region Settings -- regime

        [Display(Name = "Stand aside in balance", GroupName = "00 Regime", Order = 10,
                 Description = "The cage: price rotating inside value with nobody in control. A " +
                               "trend-following model loses there by design, so no setup is taken " +
                               "while it lasts. This is the single biggest filter in the model.")]
        public bool AvoidBalance { get; set; } = true;

        [Display(Name = "Read the regime over (bars)", GroupName = "00 Regime", Order = 15)]
        [Range(5, 500)]
        public int RegimeBars { get; set; } = 40;

        [Display(Name = "Balance: closes inside value over %", GroupName = "00 Regime", Order = 20,
                 Description = "How much of the window has to sit inside the value area before " +
                               "this calls it balanced.")]
        [Range(30, 100)]
        public decimal BalanceSharePercent { get; set; } = 65m;

        [Display(Name = "Balance: keeps under % of its range", GroupName = "00 Regime", Order = 25,
                 Description = "How little of the distance travelled turned into progress. A market " +
                               "that covered two hundred ticks and finished ten from where it " +
                               "started kept nothing.")]
        [Range(1, 90)]
        public decimal BalanceEfficiencyPercent { get; set; } = 30m;

        [Display(Name = "Trend: keeps over % of its range", GroupName = "00 Regime", Order = 30)]
        [Range(10, 100)]
        public decimal TrendEfficiencyPercent { get; set; } = 50m;

        [Display(Name = "Draw the cage", GroupName = "00 Regime", Order = 35,
                 Description = "While balanced, the value area it is rotating in, so it is obvious " +
                               "why nothing is being taken.")]
        public bool ShowCage { get; set; } = true;

        [Display(Name = "Show the status line", GroupName = "00 Regime", Order = 40,
                 Description = "One line: the regime, and how the trades on screen actually did. " +
                               "Everything the trade box says about performance, without the box.")]
        public bool ShowStatusLine { get; set; } = true;

        #endregion

        #region Settings -- delta divergence

        [Display(Name = "Mark delta divergence", GroupName = "09 Delta divergence", Order = 900,
                 Description = "A new low the selling did not pay for, or a new high the buying " +
                               "did not. Price found a worse price on less aggression than the " +
                               "swing behind it.")]
        public bool ShowDivergence { get; set; } = true;

        [Display(Name = "Trade divergence", GroupName = "09 Delta divergence", Order = 905,
                 Description = "Take it as a setup in its own right, stop beyond the extreme it " +
                               "just made. This is the scalper's trigger; the continuation read is " +
                               "the slower one.")]
        public bool TradeDivergence { get; set; } = true;

        [Display(Name = "Trade the continuation read", GroupName = "09 Delta divergence", Order = 908,
                 Description = "Value migration, effort and the bar all agreeing. Slower and rarer " +
                               "than divergence, and it needs a trend to exist at all.")]
        public bool TradeContinuation { get; set; } = true;

        [Display(Name = "Swing looks back (bars)", GroupName = "09 Delta divergence", Order = 910,
                 Description = "How far back the extreme being broken is measured.")]
        [Range(2, 200)]
        public int DivergenceLookback { get; set; } = 12;

        [Display(Name = "Delta must hold back by", GroupName = "09 Delta divergence", Order = 915,
                 Description = "In contracts. ZERO MEASURES IT: twice the typical bar delta on this " +
                               "instrument, which is how one setting means the same thing on MNQ " +
                               "and on Bitcoin. The status line shows what it worked out to.")]
        [Range(0, 100000)]
        public int DivergenceMinGap { get; set; } = 0;

        #endregion

        #region Settings -- where the size is

        [Display(Name = "Mark the biggest size", GroupName = "08 Where the size is", Order = 800,
                 Description = "The largest single-price prints ON SCREEN, ranked against each " +
                               "other, so only the genuinely biggest are marked. It is size at one " +
                               "price -- one order or many is a market-by-order question and this " +
                               "does not pretend to answer it.")]
        public bool ShowTopSize { get; set; } = true;

        [Display(Name = "How many to mark", GroupName = "08 Where the size is", Order = 805,
                 Description = "Ranked across everything in view. A handful is what makes it a " +
                               "glance instead of a search.")]
        [Range(1, 40)]
        public int TopSizeCount { get; set; } = 8;

        [Display(Name = "Ignore anything under", GroupName = "08 Where the size is", Order = 810,
                 Description = "A floor in contracts, so a quiet screen does not rank its way to " +
                               "eight tiny prints. Zero marks the biggest whatever they are.")]
        [Range(0, 100000)]
        public int TopSizeFloor { get; set; } = 0;

        [Display(Name = "Mark the biggest delta levels", GroupName = "08 Where the size is", Order = 820,
                 Description = "The prices carrying the most net aggression, as a narrow strip at " +
                               "the edge. Takes a sliver of the chart, not a column.")]
        public bool ShowDeltaLevels { get; set; } = true;

        [Display(Name = "How many delta levels", GroupName = "08 Where the size is", Order = 825)]
        [Range(1, 30)]
        public int DeltaLevelCount { get; set; } = 6;

        [Display(Name = "Delta strip width (pixels)", GroupName = "08 Where the size is", Order = 830)]
        [Range(20, 300)]
        public int DeltaStripWidth { get; set; } = 78;

        #endregion

        #region Settings -- value migration

        // Renamed from ShowValueBoxes when the default flipped to off: ATAS serves the saved
        // value back whenever the property name still matches, so the new default would never
        // have reached a chart that already had this indicator on it.
        [Display(Name = "Shade each bar's value area", GroupName = "01 Value migration", Order = 100,
                 Description = "Each bar's own value area, tinted by where it moved relative to the " +
                               "bar before it. Off by default: the box says the same thing in words.")]
        public bool ShadeValueAreas { get; set; } = false;

        [Display(Name = "Draw the value track", GroupName = "01 Value migration", Order = 105,
                 Description = "A line joining the value midpoints bar to bar. Off by default: it " +
                               "lands on the candles and reads as noise on a fast chart.")]
        public bool ShowValueTrack { get; set; } = false;

        [Display(Name = "Value area %", GroupName = "01 Value migration", Order = 110,
                 Description = "Share of the bar's volume inside the value area. 70 is the convention.")]
        [Range(20, 100)]
        public decimal BarValuePercent { get; set; } = 70m;

        [Display(Name = "Migration needs (ticks)", GroupName = "01 Value migration", Order = 120,
                 Description = "How far the value area has to move before it counts as migrating. " +
                               "Below this the bar is drawn as balanced.")]
        [Range(0, 200)]
        public int MigrationTicks { get; set; } = 2;

        #endregion

        #region Settings -- effort

        [Display(Name = "Show effort ribbon", GroupName = "02 Effort", Order = 200,
                 Description = "A strip along the edge of the panel, one cell per bar, coloured by " +
                               "which direction is currently the cheaper one to travel and brighter " +
                               "the wider the gap. It never covers price.")]
        public bool ShowRibbon { get; set; } = true;

        [Display(Name = "Ribbon sits at", GroupName = "02 Effort", Order = 205)]
        public RibbonPlace RibbonAt { get; set; } = RibbonPlace.Bottom;

        [Display(Name = "Ribbon height (pixels)", GroupName = "02 Effort", Order = 208)]
        [Range(3, 40)]
        public int RibbonHeight { get; set; } = 9;

        [Display(Name = "Window (bars)", GroupName = "02 Effort", Order = 210,
                 Description = "How many bars back the contracts-per-tick comparison looks.")]
        [Range(3, 500)]
        public int EffortWindow { get; set; } = 20;

        [Display(Name = "One side must be this much cheaper", GroupName = "02 Effort", Order = 220,
                 Description = "A multiple. At 1.35 the dearer direction has to cost 35% more per " +
                               "tick before this calls a side at all; anything closer is noise.")]
        [Range(1.0, 10.0)]
        public decimal EffortSkew { get; set; } = 1.35m;

        [Display(Name = "Minimum progress (ticks)", GroupName = "02 Effort", Order = 230,
                 Description = "A direction that moved less than this over the window has not gone " +
                               "anywhere, so its cost per tick is not a real number.")]
        [Range(1, 500)]
        public int EffortMinProgress { get; set; } = 8;

        [Display(Name = "Show one-sided windows", GroupName = "02 Effort", Order = 240,
                 Description = "Windows where only one direction made progress, so there were never " +
                               "two costs to compare. Off means only true comparisons are drawn.")]
        public bool ShowOneSided { get; set; } = true;

        [Display(Name = "Also box the bars", GroupName = "02 Effort", Order = 250,
                 Description = "An outline around each run of bars holding one direction, drawn " +
                               "around their value areas rather than their extremes. Off by default: " +
                               "on a busy chart it competes with the candles.")]
        public bool OutlineEffortRuns { get; set; } = false;

        #endregion

        #region Settings -- absorption

        [Display(Name = "Show absorption", GroupName = "03 Absorption", Order = 300,
                 Description = "Bars where one side aggressed hard and the close went nowhere, or " +
                               "the other way. Marked at the extreme they pushed into.")]
        public bool ShowAbsorption { get; set; } = true;

        [Display(Name = "Volume vs recent average %", GroupName = "03 Absorption", Order = 310,
                 Description = "How heavy the bar has to be against the average of the bars before it.")]
        [Range(50, 1000)]
        public decimal AbsorbVolumePercent { get; set; } = 140m;

        [Display(Name = "Aggression at least %", GroupName = "03 Absorption", Order = 320,
                 Description = "How one-sided the bar's delta has to be, as a share of its volume.")]
        [Range(5, 100)]
        public decimal AbsorbLeanPercent { get; set; } = 25m;

        [Display(Name = "Result no more than (ticks)", GroupName = "03 Absorption", Order = 330,
                 Description = "How little the bar has to achieve to count as absorbed. A close " +
                               "against the aggression always counts, however far it went.")]
        [Range(0, 100)]
        public int AbsorbMaxResult { get; set; } = 2;

        [Display(Name = "Average over (bars)", GroupName = "03 Absorption", Order = 340,
                 Description = "The yardstick for a heavy bar. Nothing is flagged until this many " +
                               "bars have loaded.")]
        [Range(5, 500)]
        public int AverageWindow { get; set; } = 30;

        [Display(Name = "Print size on the mark", GroupName = "03 Absorption", Order = 350)]
        public bool ShowAbsorbSize { get; set; } = true;

        #endregion

        #region Settings -- setups

        [Display(Name = "Show setups", GroupName = "04 Setups", Order = 400,
                 Description = "Marked only where value migration, the bar's own aggression and the " +
                               "cheaper direction all agree. One runs at a time.")]
        public bool ShowSetups { get; set; } = true;

        [Display(Name = "Most risk worth taking (ticks)", GroupName = "04 Setups", Order = 405,
                 Description = "A setup whose stop is further away than this is still marked, but " +
                               "greyed out: the reading was real, the trade was not there. The stop " +
                               "is never pulled in to fit. ZERO MEASURES IT: three times the typical " +
                               "bar range on this instrument.")]
        [Range(0, 2000)]
        public int MaxRiskTicks { get; set; } = 0;

        [Display(Name = "Stop buffer (ticks)", GroupName = "04 Setups", Order = 410,
                 Description = "How far beyond the bar's value area the stop sits.")]
        [Range(0, 100)]
        public int StopBufferTicks { get; set; } = 2;

        // Renamed from ShowTradeZones: three thin lines became two filled zones, which is a
        // different thing wearing the same switch.
        [Display(Name = "Draw trades as zones", GroupName = "04 Setups", Order = 420,
                 Description = "Every trade as a red block from entry to stop and a green one from " +
                               "entry to target, running from the entry bar to where it ended. " +
                               "Meant to be seen from across the room.")]
        public bool ShowTradeZones { get; set; } = true;

        [Display(Name = "Label the last N trades", GroupName = "04 Setups", Order = 427,
                 Description = "Only the most recent few carry a written label; the rest keep their " +
                               "zones. A screen of overlapping labels is a screen you cannot read.")]
        [Range(0, 40)]
        public int LabelledTrades { get; set; } = 3;

        [Display(Name = "Zone runs on for (bars)", GroupName = "04 Setups", Order = 425,
                 Description = "How far a still-open trade's zones extend past the last bar.")]
        [Range(1, 200)]
        public int ZoneBars { get; set; } = 10;

        [Display(Name = "Show trailing stop", GroupName = "04 Setups", Order = 440,
                 Description = "A stop stepping to the far side of each new aggression bar in the " +
                               "trade's direction. It never loosens, and it ends at the first bar " +
                               "that traded through it.")]
        public bool ShowTrail { get; set; } = true;

        [Display(Name = "Trail aggression at least %", GroupName = "04 Setups", Order = 450,
                 Description = "How one-sided a bar has to be to move the trail.")]
        [Range(5, 100)]
        public decimal TrailLeanPercent { get; set; } = 25m;

        #endregion

        #region Settings -- profile

        // Renamed from ShowProfile when the default went to off: ATAS serves the saved value back
        // whenever the property name matches, so the new default would never reach a live chart.
        [Display(Name = "Show the full profile column", GroupName = "06 Profile", Order = 600,
                 Description = "The whole volume profile of the bars in view, at the edge of the " +
                               "panel. Off by default -- it is a lot of chart for something the " +
                               "value lines and the delta strip already answer.")]
        public bool ShowProfileColumn { get; set; } = false;

        [Display(Name = "Profile sits at", GroupName = "06 Profile", Order = 605)]
        public ProfileSide ProfileAt { get; set; } = ProfileSide.Right;

        [Display(Name = "Profile width (% of panel)", GroupName = "06 Profile", Order = 610)]
        [Range(5, 60)]
        public int ProfileWidthPercent { get; set; } = 10;

        [Display(Name = "Value area %", GroupName = "06 Profile", Order = 615,
                 Description = "Share of the profile's volume inside the value area.")]
        [Range(20, 100)]
        public decimal RangeValuePercent { get; set; } = 70m;

        // Renamed when the defaults went to off. Lines across a chart are the cheapest thing to
        // add and the most expensive thing to read past, and these say what the cluster boxes and
        // the trade box already say.
        [Display(Name = "Draw value lines", GroupName = "06 Profile", Order = 620,
                 Description = "Point of control, value high and value low as lines over the bars. " +
                               "Off by default: the boxes and the trade box carry the same levels " +
                               "without another three lines on the chart.")]
        public bool DrawValueLines { get; set; } = false;

        [Display(Name = "Draw VWAP", GroupName = "06 Profile", Order = 625,
                 Description = "Volume weighted average price over the anchored bars.")]
        public bool DrawVwap { get; set; } = false;

        [Display(Name = "Show cluster areas", GroupName = "07 Clusters", Order = 700,
                 Description = "Bands of adjacent prices that behaved the same way: size absorbed, " +
                               "or a side driving through. These are the levels the model engages from.")]
        public bool ShowClusters { get; set; } = true;

        [Display(Name = "Clusters to mark", GroupName = "07 Clusters", Order = 705,
                 Description = "The heaviest only. Marking every one turns signal into wallpaper.")]
        [Range(1, 20)]
        public int ClustersToShow { get; set; } = 4;

        [Display(Name = "Heavy is this % of the busiest", GroupName = "07 Clusters", Order = 710)]
        [Range(10, 100)]
        public decimal ClusterHeavyPercent { get; set; } = 55m;

        [Display(Name = "Absorbed under this lean %", GroupName = "07 Clusters", Order = 715,
                 Description = "How balanced the aggression has to be for heavy trade to count as " +
                               "absorbed rather than driven.")]
        [Range(1, 60)]
        public decimal ClusterBalancedPercent { get; set; } = 22m;

        [Display(Name = "Driven over this lean %", GroupName = "07 Clusters", Order = 720)]
        [Range(30, 100)]
        public decimal ClusterDrivenPercent { get; set; } = 55m;

        [Display(Name = "A cluster is at least (ticks)", GroupName = "07 Clusters", Order = 725,
                 Description = "One heavy price is a print. A band of them is a level.")]
        [Range(1, 50)]
        public int ClusterMinTicks { get; set; } = 2;

        [Display(Name = "At a level within (ticks)", GroupName = "07 Clusters", Order = 730,
                 Description = "How close price has to be to a cluster or a value edge to count " +
                               "as standing on it.")]
        [Range(0, 50)]
        public int LevelReachTicks { get; set; } = 3;

        [Display(Name = "Setups must come from a level", GroupName = "07 Clusters", Order = 735,
                 Description = "The fourth layer: the entry has to sit on a cluster or an edge of " +
                               "value that argues the same way. Off takes the three-layer read alone.")]
        public bool RequireLevel { get; set; } = true;

        [Display(Name = "Setup profile looks back (bars)", GroupName = "07 Clusters", Order = 740,
                 Description = "The profile a SETUP is judged against. Fixed, not the visible " +
                               "range, so scrolling never changes which setups exist.")]
        [Range(20, 2000)]
        public int LevelLookback { get; set; } = 150;

        #endregion

        #region Settings -- readout

        [Display(Name = "Show the trade box", GroupName = "05 Trade box", Order = 500,
                 Description = "Every number the model has, in one block: what the three layers " +
                               "read, and the trade with its stop, target, trail and result.")]
        public bool ShowTradeBox { get; set; } = true;

        [Display(Name = "Corner", GroupName = "05 Trade box", Order = 510)]
        public ReadoutCorner Corner { get; set; } = ReadoutCorner.TopLeft;

        [Display(Name = "Text size", GroupName = "05 Trade box", Order = 515)]
        [Range(7, 20)]
        public int BoxTextSize { get; set; } = 10;

        [Display(Name = "Read the forming bar", GroupName = "05 Trade box", Order = 520,
                 Description = "The bar in progress changes with every trade. Off means every layer " +
                               "waits for the close, which is where the model is measured.")]
        public bool IncludeFormingBar { get; set; } = false;

        #endregion

        #region Calculation

        protected override void OnRecalculate()
        {
            _facts.Clear();
            _migration.Clear();
            _effort.Clear();
            _absorbed.Clear();
            _aggression.Clear();
            _cumulative.Clear();
            _divergence.Clear();
            _marks.Clear();
            _setups.Reset();
            _view = null;
            _viewClusters = new Cluster[0];
            _viewSignature = -1m;
            _viewProblem = null;
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m) return;

            Grow(bar);

            var facts = Read(bar, tick);
            _facts[bar] = facts;

            _migration[bar] = bar > 0
                ? EffortMath.Migration(_facts[bar - 1], facts, tick, MigrationTicks)
                : Side.None;

            _effort[bar] = EffortMath.Effort(_facts, bar - EffortWindow + 1, bar, tick,
                                             EffortSkew, EffortMinProgress);

            // The yardstick deliberately excludes the bar being judged: a single huge bar must not
            // raise the average it is measured against and hide itself.
            var average = bar > 0
                ? EffortMath.AverageVolume(_facts, bar - AverageWindow, bar - 1)
                : 0m;

            var enoughHistory = bar >= AverageWindow;

            AbsorptionMark mark;
            if (enoughHistory && EffortMath.Absorption(facts, average, AbsorbVolumePercent,
                                                       AbsorbLeanPercent, tick, AbsorbMaxResult, out mark))
            {
                _absorbed[bar] = mark.Absorbed;
                Remember(mark);
            }
            else
            {
                _absorbed[bar] = Side.None;
                Forget(bar);
            }

            _aggression[bar] = enoughHistory
                ? EffortMath.Aggression(facts, average, AbsorbVolumePercent, TrailLeanPercent)
                : Side.None;

            _cumulative[bar] = DeltaSignals.Cumulate(_facts, bar, bar > 0 ? _cumulative[bar - 1] : 0m);

            // Calibration is re-measured as bars arrive, so an instrument that changes character
            // through the session is not being judged against this morning.
            if (enoughHistory && bar % 25 == 0) Calibrate(bar, tick);

            _divergence[bar] = ShowDivergence || TradeDivergence
                ? DeltaSignals.Find(_facts, _cumulative, bar, DivergenceLookback, _gap)
                : new Divergence();

            // Bar - 1 has just become final. Setups are judged there and nowhere else: the bar in
            // progress can satisfy every condition on one trade and drop them on the next, and a
            // setup that appears and vanishes is worse than one that arrives a bar late.
            var closed = bar - 1;
            if (closed >= 1)
            {
                // The profile is only built where the three cheap layers already agree. Building
                // one at every bar is a full pass over the lookback to answer a question that is
                // almost always already "no".
                // Two triggers can produce a setup, so the profile is built when EITHER of them
                // is live rather than only for the continuation read.
                var continuation = TradeContinuation &&
                                   EffortMath.MightBeSetup(_migration[closed], _facts[closed],
                                                           _effort[closed]) != Side.None;

                var divergence = TradeDivergence && _divergence[closed].Found;

                var where = new Location();
                if (continuation || divergence) where = LevelAt(closed, tick);

                ReadLevels();

                var setup = new Setup();
                var found = false;

                // The regime is read from the anchored value area, so it is the same answer at
                // every bar regardless of what is on screen.
                var regime = RegimeAt(closed, tick);
                var allowed = MarketRegime.Allows(regime, AvoidBalance);

                // Divergence first: it is the faster read, and when both fire on one bar they are
                // saying the same thing from opposite ends.
                if (divergence && allowed)
                {
                    found = DeltaSignals.ToSetup(_divergence[closed], _facts[closed], where, RequireLevel,
                                                 tick, StopBufferTicks, _riskCap, out setup);
                }

                if (!found && continuation && allowed)
                {
                    found = EffortMath.Confluence(_migration[closed], _facts[closed], _effort[closed],
                                                  _absorbed[closed], where, RequireLevel, tick,
                                                  StopBufferTicks, _riskCap, out setup);
                }

                // Outside levels do not pick trades -- they refuse them. A long into resistance
                // twelve ticks overhead has nowhere to go, and a one-to-one target on the far side
                // of a wall is a target you were never going to be paid.
                if (found && _levels != null && _levels.Usable)
                {
                    OptionLevel wall;

                    if (OptionLevels.Blocks(_levels, setup.Side, setup.Entry, tick, WallVetoTicks, out wall))
                        found = false;

                    else if (VetoBlockedTarget &&
                             OptionLevels.BlocksTarget(_levels, setup.Side, setup.Entry, setup.Target, out wall))
                        found = false;
                }

                _setups.Advance(closed, _facts[closed], _aggression[closed], found, setup);
            }
        }

        /// <summary>
        /// Where the bar's close stood in the profile of the bars behind it. Fixed lookback on
        /// purpose: a setup judged against the VISIBLE range would appear and vanish as the chart
        /// is scrolled, and a signal that depends on the scrollbar is not a signal.
        /// </summary>
        private Location LevelAt(int bar, decimal tick)
        {
            var from = bar - LevelLookback + 1;
            if (from < 0) from = 0;

            var profile = BuildProfile(from, bar, tick);
            if (profile == null) return new Location();

            RangeMath.ComputeValueArea(profile, RangeValuePercent);
            var clusters = Clusters(profile);

            return RangeMath.Where(profile, _facts[bar].Close, clusters, LevelReachTicks);
        }

        /// <summary>
        /// The regime as of a closed bar, against the value area of the bars behind it. Built on
        /// the same anchored profile the level gate uses, so one lookback setting moves the whole
        /// model together.
        /// </summary>
        private RegimeRead RegimeAt(int bar, decimal tick)
        {
            var from = bar - LevelLookback + 1;
            if (from < 0) from = 0;

            var profile = BuildProfile(from, bar, tick);
            if (profile == null) return new RegimeRead();

            RangeMath.ComputeValueArea(profile, RangeValuePercent);

            var window = bar - RegimeBars + 1;
            if (window < 0) window = 0;

            return MarketRegime.Read(_facts, window, bar, tick,
                                     profile.ValueHigh, profile.ValueLow,
                                     profile.VahIndex >= 0 && profile.ValIndex >= 0,
                                     BalanceSharePercent / 100m,
                                     BalanceEfficiencyPercent / 100m,
                                     TrendEfficiencyPercent / 100m);
        }

        private RangeProfile BuildProfile(int from, int to, decimal tick)
        {
            if (tick <= 0m || to < from) return null;

            var builder = new RangeProfileBuilder();

            for (var bar = from; bar <= to; bar++)
            {
                var candle = GetCandle(bar);
                builder.NoteBar(bar);

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null) continue;

                    builder.Add(level.Price, level.Volume, level.Bid, level.Ask);
                }
            }

            return builder.Build(tick, MaxProfileTicks);
        }

        private Cluster[] Clusters(RangeProfile profile)
        {
            var all = RangeMath.FindClusters(profile, ClusterHeavyPercent,
                                             ClusterBalancedPercent / 100m,
                                             ClusterDrivenPercent / 100m,
                                             ClusterMinTicks);

            return RangeMath.Heaviest(all, ClustersToShow);
        }

        /// <summary>
        /// Works the two absolute thresholds out from what this instrument actually does, when
        /// they are left at zero. Measured, never assumed: a contract count that suits MNQ is
        /// nonsense on Bitcoin, and a fixed default would be quietly wrong on one of them.
        /// </summary>
        private void Calibrate(int bar, decimal tick)
        {
            var from = bar - LevelLookback + 1;
            if (from < 0) from = 0;

            if (DivergenceMinGap > 0)
            {
                _gap = DivergenceMinGap;
                _gapMeasured = false;
            }
            else
            {
                _gap = EffortMath.TypicalDelta(_facts, from, bar) * 2m;
                _gapMeasured = true;
            }

            if (MaxRiskTicks > 0)
            {
                _riskCap = MaxRiskTicks;
                _riskMeasured = false;
            }
            else
            {
                var typical = EffortMath.TypicalRangeTicks(_facts, from, bar, tick);
                _riskCap = (int)Math.Round(typical * 3m);
                _riskMeasured = true;
            }
        }

        /// <summary>
        /// Re-reads the level file when it changes on disk, and not otherwise.
        ///
        /// Every failure is kept and shown. A missing file, a locked one, six lines that would not
        /// parse -- all of them end up on the chart, because a level file that silently produced
        /// nothing would look exactly like a market with no levels in it.
        /// </summary>
        private void ReadLevels()
        {
            if (!UseOutsideLevels)
            {
                _levels = null;
                _levelsNote = null;
                return;
            }

            var path = LevelFile;
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "ATAS", "Ocean", "levels.csv");
            }

            try
            {
                if (!File.Exists(path))
                {
                    if (_levelsPath != path || _levels != null)
                    {
                        _levels = null;
                        _levelsPath = path;
                        _levelsNote = "no level file at " + path;
                    }

                    return;
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (_levels != null && _levelsPath == path && _levelsStamp == stamp) return;

                _levelsPath = path;
                _levelsStamp = stamp;
                _levels = OptionLevels.Parse(File.ReadAllLines(path));

                _levelsNote = _levels.BadLines > 0
                    ? _levels.BadLines + " line(s) in the level file did not parse and were dropped"
                    : null;
            }
            catch (Exception ex)
            {
                _levels = null;
                _levelsNote = "level file could not be read -- " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private void Grow(int bar)
        {
            while (_facts.Count <= bar)
            {
                _facts.Add(new BarFacts());
                _migration.Add(Side.None);
                _effort.Add(new EffortVerdict());
                _absorbed.Add(Side.None);
                _aggression.Add(Side.None);
                _cumulative.Add(0m);
                _divergence.Add(new Divergence());
            }
        }

        private void Remember(AbsorptionMark mark)
        {
            for (var i = _marks.Count - 1; i >= 0; i--)
            {
                if (_marks[i].Bar < mark.Bar) break;

                if (_marks[i].Bar == mark.Bar)
                {
                    _marks[i] = mark;
                    return;
                }

                _marks.RemoveAt(i);
            }

            _marks.Add(mark);
        }

        private void Forget(int bar)
        {
            for (var i = _marks.Count - 1; i >= 0; i--)
            {
                if (_marks[i].Bar < bar) return;
                if (_marks[i].Bar == bar) _marks.RemoveAt(i);
            }
        }

        /// <summary>
        /// Pulls one bar's facts off the candle. Volume and delta come from the candle itself;
        /// the value area is built from the per-price data, and when a bar carries none the value
        /// layer is marked unknown rather than being faked from the high and low.
        /// </summary>
        private BarFacts Read(int bar, decimal tick)
        {
            var candle = GetCandle(bar);

            var facts = new BarFacts();
            facts.Bar = bar;
            facts.Open = candle.Open;
            facts.High = candle.High;
            facts.Low = candle.Low;
            facts.Close = candle.Close;
            facts.Volume = candle.Volume;
            facts.Delta = candle.Delta;

            var levels = new List<PriceVolume>();
            foreach (var level in candle.GetAllPriceLevels())
            {
                if (level == null) continue;

                var item = new PriceVolume();
                item.Price = level.Price;
                item.Volume = level.Volume;
                item.Bid = level.Bid;
                item.Ask = level.Ask;
                levels.Add(item);
            }

            decimal poc, high, low;
            facts.HasValue = EffortMath.ValueArea(levels, tick, BarValuePercent, MaxBarTicks,
                                                  out poc, out high, out low);
            facts.Poc = poc;
            facts.ValueHigh = high;
            facts.ValueLow = low;

            return facts;
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

            if (_facts.Count < 3)
            {
                Status(context, region, "Ocean Effort: no bars loaded yet.");
                return;
            }

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m)
            {
                Status(context, region, "Ocean Effort: waiting for the instrument.");
                return;
            }

            var first = FirstVisibleBarNumber;
            var last = LastVisibleBarNumber;
            if (first < 0) first = 0;
            if (last > _facts.Count - 1) last = _facts.Count - 1;
            if (last < first) return;

            if (!IncludeFormingBar && last > 0 && last == _facts.Count - 1) last--;
            if (last < first) return;

            if (last - first + 1 > MaxVisibleBars)
            {
                Status(context, region, "Ocean Effort: " + (last - first + 1) + " bars in view. Zoom in.");
                return;
            }

            context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);
            context.SetClip(region);

            // Every layer is caught on its own. A layer that threw used to take down every layer
            // after it -- including the box, which is the one thing that could have said something
            // was wrong. Now the failure lands IN the box instead of hiding it.
            var errors = new List<string>();
            var record = _setups.At(last);
            _regime = RegimeAt(last, tick);

            try
            {
                Layer(errors, "profile", () => ViewProfile(container, first, last, tick), ShowProfileColumn || ShowClusters || DrawValueLines);
                Layer(errors, "clusters", () => RenderClusters(context, container, region), ShowClusters);
                Layer(errors, "value lines", () => RenderValueLines(context, container, region), DrawValueLines || DrawVwap);
                Layer(errors, "profile column", () => RenderProfile(context, container, region), ShowProfileColumn);
                Layer(errors, "effort boxes", () => RenderEffortBoxes(context, container, first, last), OutlineEffortRuns);
                Layer(errors, "value", () => RenderValue(context, container, first, last), ShadeValueAreas || ShowValueTrack);
                Layer(errors, "order track", () => RenderOrderTrack(context, container, region, first, last), ShowOrderTrack);
                Layer(errors, "delta levels", () => RenderDeltaLevels(context, container, region), ShowDeltaLevels);
                Layer(errors, "biggest size", () => RenderTopSize(context, container, region, first, last), ShowTopSize);
                Layer(errors, "absorption", () => RenderAbsorption(context, container, first, last), ShowAbsorption);
                Layer(errors, "trade zones", () => RenderTradeZones(context, container, region, first, last, tick), ShowTradeZones);
                Layer(errors, "divergence", () => RenderDivergence(context, container, region, first, last), ShowDivergence);
                Layer(errors, "outside levels", () => RenderOutsideLevels(context, container, region),
                      DrawOutsideLevels);
                Layer(errors, "cage", () => RenderCage(context, container, region, last), ShowCage);
                Layer(errors, "status", () => RenderStatusLine(context, container, region, first, last, tick), ShowStatusLine);
                Layer(errors, "trail", () => RenderTrail(context, container, record), ShowTrail);
                Layer(errors, "setups", () => RenderSetups(context, container, first, last, tick), ShowSetups);
                Layer(errors, "ribbon", () => RenderRibbon(context, container, region, first, last), ShowRibbon);

                if (!ShowTradeBox && !ShowStatusLine && !string.IsNullOrEmpty(_trackProblem))
                    Status(context, region, "Ocean Effort: " + _trackProblem);

                if (ShowTradeBox)
                {
                    try
                    {
                        RenderBox(context, region, first, last, tick, record, errors);
                    }
                    catch (Exception ex)
                    {
                        // The box is the last thing standing, so its own failure is reported as
                        // plainly as possible rather than being allowed to disappear.
                        Status(context, region, "Ocean Effort: the trade box failed -- " +
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
        /// Rebuilds the profile from a FIXED number of bars ending at the newest one -- never from
        /// what happens to be on screen.
        ///
        /// The visible range was the wrong anchor and it showed: value high, value low and VWAP
        /// moved every time the chart was scrolled. A reference level that depends on the
        /// scrollbar is not a reference level.
        /// </summary>
        private void ViewProfile(IChartContainer container, int first, int last, decimal tick)
        {
            var to = _facts.Count - 1;
            var from = to - LevelLookback + 1;
            if (from < 0) from = 0;

            var signature = _facts[to].Volume;

            if (_view != null && _viewFirst == from && _viewLast == to && _viewSignature == signature)
                return;

            _viewFirst = from;
            _viewLast = to;
            _viewSignature = signature;
            _viewProblem = null;

            _view = BuildProfile(from, to, tick);

            if (_view == null)
            {
                _viewClusters = new Cluster[0];
                _viewVwap = 0m;
                _viewProblem = "the last " + LevelLookback + " bars span more than " + MaxProfileTicks +
                               " ticks, so no profile is drawn rather than a trimmed one";
                return;
            }

            RangeMath.ComputeValueArea(_view, RangeValuePercent);
            _viewClusters = Clusters(_view);
            _viewVwap = RangeMath.Vwap(_view);
        }

        /// <summary>
        /// The profile as a column at the edge of the panel: a grey bar for everything that traded
        /// and a coloured bar for how much of it leaned, on the same scale. The GAP between them
        /// is the part that changed hands and went nowhere -- absorption stops being a subtle hue
        /// and becomes a long grey bar with no colour in it.
        /// </summary>
        private void RenderProfile(RenderContext context, IChartContainer container, Rectangle region)
        {
            if (_view == null || _view.MaxLevelVolume <= 0m) return;

            var width = region.Width * ProfileWidthPercent / 100;
            if (width < 20) width = 20;

            var right = ProfileAt == ProfileSide.Right;
            var left = right ? region.Right - width : region.Left;

            var ticksPerRow = RangeMath.TicksPerRow(container.PriceRowHeight, 2);
            var rowPixels = (int)Math.Round(container.PriceRowHeight * ticksPerRow);
            if (rowPixels < 1) rowPixels = 1;

            var grey = Color.FromArgb(90, 120, 126, 142);

            for (var i = 0; i < _view.Count; i += ticksPerRow)
            {
                var volume = 0m;
                var delta = 0m;

                for (var j = i; j < i + ticksPerRow && j < _view.Count; j++)
                {
                    volume += _view.Levels[j].Volume;
                    delta += _view.Levels[j].Delta;
                }

                if (volume <= 0m) continue;

                var mid = _view.PriceAt(i) + _view.TickSize * (ticksPerRow - 1) / 2m;
                var y = container.GetYByPrice(mid, false) - rowPixels / 2;
                if (y + rowPixels < region.Top || y > region.Bottom) continue;

                var full = (int)(width * volume / _view.MaxLevelVolume);
                if (full < 1) full = 1;

                var lean = delta < 0m ? -delta : delta;
                var part = (int)(width * lean / _view.MaxLevelVolume);
                if (part > full) part = full;

                var height = rowPixels > 1 ? rowPixels - 1 : 1;

                var x = right ? region.Right - full : left;
                context.FillRectangle(grey, new Rectangle(x, y, full, height));

                if (part < 1) continue;

                var rgb = ToColor(delta >= 0m ? _buyColor : _sellColor);
                var px = right ? region.Right - part : left;
                context.FillRectangle(Color.FromArgb(210, rgb.R, rgb.G, rgb.B),
                                      new Rectangle(px, y, part, height));
            }
        }

        /// <summary>
        /// The cluster areas as bands across the chart. Green where sellers were absorbed and the
        /// level held, red where buyers were. These are the prices the model is willing to engage
        /// from, so they are drawn where price will meet them rather than only on the column.
        /// </summary>
        private void RenderClusters(RenderContext context, IChartContainer container, Rectangle region)
        {
            if (_view == null || _viewClusters == null) return;

            for (var i = 0; i < _viewClusters.Length; i++)
            {
                var cluster = _viewClusters[i];

                var top = container.GetYByPrice(cluster.High, false);
                var bottom = container.GetYByPrice(cluster.Low, false);
                if (bottom < top) { var swap = top; top = bottom; bottom = swap; }

                bottom += (int)Math.Round(container.PriceRowHeight);
                if (bottom <= top) bottom = top + 1;
                if (bottom < region.Top || top > region.Bottom) continue;

                var favours = cluster.Favours;
                var rgb = ToColor(favours == Side.Buy ? _buyColor
                                : favours == Side.Sell ? _sellColor : _flatColor);

                // The box starts where the area PRINTED and runs forward to the right edge: it is
                // a level that was made once and then waited for price to come back to it.
                var left = cluster.FirstBar >= 0
                    ? container.GetXByBar(cluster.FirstBar, true)
                    : region.Left;

                if (left < region.Left) left = region.Left;

                var rect = new Rectangle(left, top, region.Right - left, bottom - top);

                // Absorption is where size was filled and price stayed; aggression is where a side
                // drove. The first is a wall, the second a trail -- so they are drawn differently.
                var fill = cluster.Kind == ClusterKind.Absorption ? 42 : 20;
                context.FillRectangle(Color.FromArgb(fill, rgb.R, rgb.G, rgb.B), rect);
                context.DrawRectangle(new RenderPen(Color.FromArgb(180, rgb.R, rgb.G, rgb.B), 1f), rect);

                var label = Color.FromArgb(235, rgb.R, rgb.G, rgb.B);
                var high = ChartInfo.GetPriceString(cluster.High);
                var low = ChartInfo.GetPriceString(cluster.Low);

                context.DrawString(high, _font, label, left + 2, top - context.MeasureString(high, _font).Height);
                context.DrawString(low, _font, label, left + 2, bottom + 1);

                var text = (cluster.Kind == ClusterKind.Absorption ? "absorbed " : "drove ") +
                           (cluster.Side == Side.Buy ? "buyers" : "sellers") + "  " +
                           EffortMath.Compact(cluster.Volume);

                var size = context.MeasureString(text, _font);
                if (bottom - top > size.Height + 2)
                    context.DrawString(text, _font, label, left + 4, top + 2);
            }
        }

        private void RenderValueLines(RenderContext context, IChartContainer container, Rectangle region)
        {
            if (_view == null) return;

            if (DrawValueLines)
            {
                Level(context, container, region, _view.Poc, "POC", ToColor(_pocColor), false);
                Level(context, container, region, _view.ValueHigh, "VAH", ToColor(_valueColor), true);
                Level(context, container, region, _view.ValueLow, "VAL", ToColor(_valueColor), true);
            }

            if (DrawVwap && _viewVwap > 0m)
                Level(context, container, region, _viewVwap, "VWAP", ToColor(_vwapColor), true);
        }

        private void Level(RenderContext context, IChartContainer container, Rectangle region,
                           decimal price, string tag, Color color, bool dashed)
        {
            if (price <= 0m) return;

            var y = container.GetYByPrice(price, false);
            if (y < region.Top || y > region.Bottom) return;

            var pen = dashed
                ? new RenderPen(color, 1f, System.Drawing.Drawing2D.DashStyle.Dash)
                : new RenderPen(color, 1f);

            context.DrawLine(pen, region.Left, y, region.Right, y);

            var text = tag + " " + ChartInfo.GetPriceString(price);
            var size = context.MeasureString(text, _font);
            var x = region.Left + 4;

            context.FillRectangle(Color.FromArgb(190, 15, 17, 22),
                                  new Rectangle(x - 2, y - size.Height - 1, size.Width + 4, size.Height));
            context.DrawString(text, _font, color, x, y - size.Height - 1);
        }

        /// <summary>
        /// Each candle's own volume profile, drawn in the bar slot beside it, with that candle's
        /// value area marked. This is the order track: the shape of what traded inside the bar,
        /// and where its value sat, so the migration from one bar to the next is something you
        /// can see rather than something a line asserts.
        /// </summary>
        private void RenderOrderTrack(RenderContext context, IChartContainer container, Rectangle region,
                                      int first, int last)
        {
            var slot = (int)container.BarsWidth;
            if (slot < TrackMinBarWidth)
            {
                _trackProblem = "bars are " + slot + " pixels wide; the order track needs " +
                                TrackMinBarWidth + ". Zoom in or turn it off.";
                return;
            }

            _trackProblem = null;

            var width = slot * TrackWidthPercent / 100;
            if (width < 2) width = 2;

            var ticksPerRow = RangeMath.TicksPerRow(container.PriceRowHeight, 2);
            var rowPixels = (int)Math.Round(container.PriceRowHeight * ticksPerRow);
            if (rowPixels < 1) rowPixels = 1;

            var tick = InstrumentInfo.TickSize;
            var lean = TrackLeanPercent / 100m;

            for (var bar = first; bar <= last; bar++)
            {
                var candle = GetCandle(bar);

                var max = 0m;
                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level != null && level.Volume > max) max = level.Volume;
                }

                if (max <= 0m) continue;

                // The track sits to the RIGHT of the candle, in the same slot, so it never covers
                // the price action it is describing.
                var left = container.GetXByBar(bar, true) + (slot - width);

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null || level.Volume <= 0m) continue;

                    var y = container.GetYByPrice(level.Price, false) - rowPixels / 2;
                    if (y + rowPixels < region.Top || y > region.Bottom) continue;

                    var length = (int)(width * level.Volume / max);
                    if (length < 1) length = 1;

                    var delta = level.Ask - level.Bid;
                    var side = delta < 0m ? -delta : delta;
                    var oneSided = level.Volume > 0m && side / level.Volume >= lean;

                    var colour = !oneSided
                        ? Color.FromArgb(190, 70, 110, 165)
                        : delta > 0m ? Color.FromArgb(220, ToColor(_buyColor).R, ToColor(_buyColor).G, ToColor(_buyColor).B)
                                     : Color.FromArgb(220, ToColor(_sellColor).R, ToColor(_sellColor).G, ToColor(_sellColor).B);

                    context.FillRectangle(colour, new Rectangle(left, y, length, Math.Max(1, rowPixels - 1)));
                }

                if (!ShowTrackValue || !_facts[bar].HasValue) continue;

                var edge = new RenderPen(Color.FromArgb(150, 235, 238, 245), 1f,
                                         System.Drawing.Drawing2D.DashStyle.Dot);

                TrackLine(context, container, region, left, width, _facts[bar].ValueHigh, edge);
                TrackLine(context, container, region, left, width, _facts[bar].ValueLow, edge);
                TrackLine(context, container, region, left, width, _facts[bar].Poc,
                          new RenderPen(ToColor(_pocColor), 1f));
            }
        }

        private void TrackLine(RenderContext context, IChartContainer container, Rectangle region,
                               int left, int width, decimal price, RenderPen pen)
        {
            var y = container.GetYByPrice(price, false);
            if (y < region.Top || y > region.Bottom) return;

            context.DrawLine(pen, left, y, left + width, y);
        }

        /// <summary>
        /// The biggest single-price prints on screen, ranked against each other and marked with a
        /// solid tag carrying the count. Ranking is the whole point: a per-bar cap marks the
        /// busiest price of a quiet bar as loudly as a real one, and then nothing stands out.
        ///
        /// It is size at ONE price inside one bar. Whether it was one participant or twenty is a
        /// market-by-order question, and nothing here can answer it -- so nothing here claims to.
        /// </summary>
        private void RenderTopSize(RenderContext context, IChartContainer container, Rectangle region,
                                   int first, int last)
        {
            var prints = new List<PricePrint>();

            for (var bar = first; bar <= last; bar++)
            {
                var candle = GetCandle(bar);

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null || level.Volume <= 0m) continue;

                    var print = new PricePrint();
                    print.Bar = bar;
                    print.Price = level.Price;
                    print.Volume = level.Volume;
                    print.Delta = level.Ask - level.Bid;
                    prints.Add(print);
                }
            }

            var top = RangeMath.Biggest(prints, TopSizeCount, TopSizeFloor);
            if (top.Length == 0) return;

            var biggest = top[0].Volume;
            var slot = (int)container.BarsWidth;
            if (slot < 1) slot = 1;

            for (var i = 0; i < top.Length; i++)
            {
                var print = top[i];

                var y = container.GetYByPrice(print.Price, false);
                if (y < region.Top || y > region.Bottom) continue;

                var rgb = ToColor(print.Delta >= 0m ? _buyColor : _sellColor);
                var text = EffortMath.Compact(print.Volume);
                var size = context.MeasureString(text, _sizeFont);

                // The tag is solid and the rank is in its height, so the largest reads first from
                // across the room. The line back to the price keeps it honest about where it was.
                var rank = biggest <= 0m ? 0m : print.Volume / biggest;
                var pad = 3 + (int)(rank * 3m);

                var width = size.Width + pad * 2;
                var height = size.Height + 2;

                var x = container.GetXByBar(print.Bar, true) + slot / 2;
                var box = new Rectangle(x - width / 2, y - height / 2, width, height);

                context.DrawLine(new RenderPen(Color.FromArgb(150, rgb.R, rgb.G, rgb.B), 1f),
                                 region.Left, y, region.Right, y);

                context.FillRectangle(Color.FromArgb(255, rgb.R, rgb.G, rgb.B), box, 3);
                context.DrawString(text, _sizeFont, Color.FromArgb(255, 12, 14, 18),
                                   x - size.Width / 2, y - size.Height / 2);
            }
        }

        /// <summary>
        /// The prices carrying the most net aggression, as a narrow strip at the edge: a bar out
        /// from a centre line, right for buyers and left for sellers, with the number beside it.
        ///
        /// A sliver rather than a column. The question "which prices did the aggression pile up
        /// at" does not need the whole profile drawn to answer it.
        /// </summary>
        private void RenderDeltaLevels(RenderContext context, IChartContainer container, Rectangle region)
        {
            if (_view == null || _view.Count == 0) return;

            var picked = RangeMath.BiggestDelta(_view, DeltaLevelCount);
            if (picked.Length == 0) return;

            var strip = DeltaStripWidth;
            if (strip > region.Width / 3) strip = region.Width / 3;
            if (strip < 20) strip = 20;

            var right = region.Right - 2;
            var left = right - strip;
            var middle = left + strip / 2;

            var biggest = 0m;
            for (var i = 0; i < picked.Length; i++)
            {
                var size = Math.Abs(_view.Levels[picked[i]].Delta);
                if (size > biggest) biggest = size;
            }

            if (biggest <= 0m) return;

            context.FillRectangle(Color.FromArgb(70, 16, 18, 24),
                                  new Rectangle(left, region.Top, strip, region.Height));

            context.DrawLine(new RenderPen(Color.FromArgb(60, 130, 136, 152), 1f),
                             middle, region.Top, middle, region.Bottom);

            var rowHeight = (int)Math.Round(container.PriceRowHeight);
            if (rowHeight < 3) rowHeight = 3;
            if (rowHeight > 14) rowHeight = 14;

            for (var i = 0; i < picked.Length; i++)
            {
                var index = picked[i];
                var delta = _view.Levels[index].Delta;
                var price = _view.PriceAt(index);

                var y = container.GetYByPrice(price, false);
                if (y < region.Top || y > region.Bottom) continue;

                var rgb = ToColor(delta > 0m ? _buyColor : _sellColor);
                var length = (int)((strip / 2 - 2) * Math.Abs(delta) / biggest);
                if (length < 2) length = 2;

                var x = delta > 0m ? middle : middle - length;

                context.FillRectangle(Color.FromArgb(230, rgb.R, rgb.G, rgb.B),
                                      new Rectangle(x, y - rowHeight / 2, length, rowHeight));

                var text = TradeBox.Signed(delta);
                var size = context.MeasureString(text, _font);

                // The number sits outside the strip so it never lands on the bar it is measuring.
                var tx = left - size.Width - 4;
                if (tx < region.Left) tx = region.Left + 2;

                context.FillRectangle(Color.FromArgb(200, 14, 16, 21),
                                      new Rectangle(tx - 2, y - size.Height / 2, size.Width + 4, size.Height));
                context.DrawString(text, _font, Color.FromArgb(255, rgb.R, rgb.G, rgb.B),
                                   tx, y - size.Height / 2);
            }

            context.DrawString("DELTA", _font, Color.FromArgb(140, 150, 150, 165),
                               left + 2, region.Top + 2);
        }

        /// <summary>
        /// The effort verdict as drawn: a one-sided window is only a fact about the window, so it
        /// is shown or hidden on its own setting.
        /// </summary>
        private Side EffortSide(int bar)
        {
            var verdict = _effort[bar];
            if (verdict.Basis == EffortBasis.OneSided && !ShowOneSided) return Side.None;

            return verdict.Side;
        }

        /// <summary>
        /// The effort layer as a strip along the edge of the panel: one cell per bar, coloured by
        /// the cheaper direction and brighter the wider the gap between the two costs. It sits
        /// clear of the candles, which is the whole point -- the bias is readable without any of
        /// it landing on the price action.
        /// </summary>
        private void RenderRibbon(RenderContext context, IChartContainer container, Rectangle region,
                                  int first, int last)
        {
            var height = RibbonHeight;
            if (height < 3) height = 3;

            var top = RibbonAt == RibbonPlace.Bottom ? region.Bottom - height - 2 : region.Top + 2;

            context.FillRectangle(Color.FromArgb(60, 20, 22, 28),
                                  new Rectangle(region.Left, top, region.Width, height));

            var width = (int)container.BarsWidth;
            if (width < 1) width = 1;

            for (var bar = first; bar <= last; bar++)
            {
                var side = EffortSide(bar);
                if (side == Side.None) continue;

                var verdict = _effort[bar];

                // A one-sided window has no ratio to scale by -- it was never a comparison -- so it
                // is drawn at a fixed middling weight rather than ranked against ones that were.
                var alpha = 150;
                if (verdict.Basis == EffortBasis.Both)
                {
                    var over = verdict.Ratio - 1m;
                    if (over < 0m) over = 0m;
                    if (over > 2m) over = 2m;

                    alpha = 70 + (int)(over * 92m);
                }

                var rgb = ToColor(side == Side.Buy ? _buyColor : _sellColor);
                var left = container.GetXByBar(bar, true);

                context.FillRectangle(Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B),
                                      new Rectangle(left, top, width, height));
            }

            context.DrawString("EFFORT", _font, Color.FromArgb(140, 150, 150, 165),
                               region.Left + 3, top - 11);
        }

        /// <summary>
        /// Each bar's value area, tinted by where it went relative to the bar before it, with a
        /// track joining the value midpoints. The band alone was too quiet to read against the
        /// candles; the track is what makes a drift of value visible as a drift.
        /// </summary>
        private void RenderValue(RenderContext context, IChartContainer container, int first, int last)
        {
            var width = (int)container.BarsWidth;
            if (width < 1) width = 1;

            var inner = width - 2;
            if (inner < 1) inner = 1;

            if (ShadeValueAreas)
            {
                for (var bar = first; bar <= last; bar++)
                {
                    var facts = _facts[bar];
                    if (!facts.HasValue) continue;

                    var top = container.GetYByPrice(facts.ValueHigh, false);
                    var bottom = container.GetYByPrice(facts.ValueLow, false);
                    if (bottom < top) { var swap = top; top = bottom; bottom = swap; }

                    var x = container.GetXByBar(bar, true) + 1;
                    var rgb = ToColor(Tint(_migration[bar]));

                    context.FillRectangle(Color.FromArgb(45, rgb.R, rgb.G, rgb.B),
                                          new Rectangle(x, top, inner, Math.Max(1, bottom - top)));
                }
            }

            if (!ShowValueTrack) return;

            var previousX = int.MinValue;
            var previousY = 0;

            for (var bar = first; bar <= last; bar++)
            {
                var facts = _facts[bar];
                if (!facts.HasValue) { previousX = int.MinValue; continue; }

                var x = container.GetXByBar(bar, true) + width / 2;
                var y = container.GetYByPrice(facts.ValueMid, false);

                if (previousX != int.MinValue)
                {
                    var rgb = ToColor(Tint(_migration[bar]));
                    context.DrawLine(new RenderPen(Color.FromArgb(225, rgb.R, rgb.G, rgb.B), 2f),
                                     previousX, previousY, x, y);
                }

                previousX = x;
                previousY = y;
            }
        }

        /// <summary>
        /// One outline per run of bars holding the same direction, around the run's VALUE areas
        /// rather than its extremes. Drawn to the extremes it becomes a wall across the candles on
        /// any bar with a long wick, which is what it did the first time.
        /// </summary>
        private void RenderEffortBoxes(RenderContext context, IChartContainer container, int first, int last)
        {
            var bar = first;
            while (bar <= last)
            {
                var side = EffortSide(bar);
                if (side == Side.None)
                {
                    bar++;
                    continue;
                }

                var from = bar;
                while (bar + 1 <= last && EffortSide(bar + 1) == side) bar++;
                var to = bar;
                bar++;

                var high = decimal.MinValue;
                var low = decimal.MaxValue;
                for (var i = from; i <= to; i++)
                {
                    if (!_facts[i].HasValue) continue;

                    if (_facts[i].ValueHigh > high) high = _facts[i].ValueHigh;
                    if (_facts[i].ValueLow < low) low = _facts[i].ValueLow;
                }

                if (high == decimal.MinValue) continue;

                var left = container.GetXByBar(from, true);
                var right = container.GetXByBar(to, true) + (int)container.BarsWidth;
                if (right <= left) right = left + 1;

                var top = container.GetYByPrice(high, false);
                var bottom = container.GetYByPrice(low, false);
                if (bottom < top) { var swap = top; top = bottom; bottom = swap; }

                var rgb = ToColor(side == Side.Buy ? _buyColor : _sellColor);

                context.DrawRectangle(new RenderPen(Color.FromArgb(90, rgb.R, rgb.G, rgb.B), 1f),
                                      new Rectangle(left, top, right - left, Math.Max(1, bottom - top)));
            }
        }

        private void RenderAbsorption(RenderContext context, IChartContainer container, int first, int last)
        {
            var width = (int)container.BarsWidth;
            if (width < 1) width = 1;

            var rgb = ToColor(_absorbColor);
            var color = Color.FromArgb(230, rgb.R, rgb.G, rgb.B);
            var pen = new RenderPen(color, 1f);

            for (var i = 0; i < _marks.Count; i++)
            {
                var mark = _marks[i];
                if (mark.Bar < first || mark.Bar > last) continue;

                var x = container.GetXByBar(mark.Bar, true) + width / 2;
                var y = container.GetYByPrice(mark.Price, false);
                var up = mark.Absorbed == Side.Buy;

                // A flat bar against the extreme the losing side pushed into, with a stub pointing
                // the way price refused to go.
                var half = Math.Max(3, width / 2);
                var edge = up ? y - 2 : y + 2;

                context.DrawLine(pen, x - half, edge, x + half, edge);
                context.DrawLine(pen, x, edge, x, up ? edge - 5 : edge + 5);

                if (!ShowAbsorbSize) continue;

                var text = EffortMath.Compact(mark.Volume);
                var size = context.MeasureString(text, _font);
                var ty = up ? edge - 6 - size.Height : edge + 6;
                context.DrawString(text, _font, color, x - size.Width / 2, ty);
            }
        }

        /// <summary>
        /// The arrow, a short tag, and the risk lines. Deliberately terse: the full numbers live
        /// in the readout, and three lines of price text per setup was most of what made the first
        /// version unreadable.
        /// </summary>
        private void RenderSetups(RenderContext context, IChartContainer container, int first, int last,
                                  decimal tick)
        {
            var width = (int)container.BarsWidth;
            if (width < 1) width = 1;

            for (var i = 0; i < _setups.Runs.Count; i++)
            {
                var setup = _setups.Runs[i].Setup;
                if (setup.Bar < first || setup.Bar > last) continue;

                var buy = setup.Side == Side.Buy;
                var over = setup.OverCap;

                var rgb = over ? ToColor(_flatColor) : ToColor(buy ? _buyColor : _sellColor);
                var color = Color.FromArgb(over ? 150 : 255, rgb.R, rgb.G, rgb.B);

                var x = container.GetXByBar(setup.Bar, true) + width / 2;
                var y = container.GetYByPrice(setup.Entry, false);

                var pen = new RenderPen(color, over ? 1f : 2f);
                var tip = buy ? y + 14 : y - 14;
                context.DrawLine(pen, x, tip, x, y);
                context.DrawLine(pen, x - 4, buy ? y + 5 : y - 5, x, y);
                context.DrawLine(pen, x + 4, buy ? y + 5 : y - 5, x, y);

                var ticks = Math.Round(setup.Risk / tick);
                var label = (buy ? "L " : "S ") + ticks + "t" + (over ? " over cap" : "");
                var size = context.MeasureString(label, _font);
                context.DrawString(label, _font, color, x + 5, buy ? tip - size.Height : tip);
            }
        }

        /// <summary>
        /// Every trade as two filled blocks: entry down to the stop in red, entry up to the target
        /// in green, running from the entry bar to wherever it ended.
        ///
        /// Three thin lines were what this used to be, and they were not obvious. A block of
        /// colour with the direction written on it is readable without looking for it, which is
        /// the whole job -- you should not have to hunt your own chart for your own signal.
        /// </summary>
        private void RenderTradeZones(RenderContext context, IChartContainer container, Rectangle region,
                                      int first, int last, decimal tick)
        {
            var slot = (int)container.BarsWidth;
            if (slot < 1) slot = 1;

            for (var i = 0; i < _setups.Runs.Count; i++)
            {
                var run = _setups.Runs[i];
                var setup = run.Setup;

                if (setup.Bar > last) continue;

                var endBar = run.Live ? last + ZoneBars : run.ExitBar;
                if (endBar < first) continue;

                var left = container.GetXByBar(setup.Bar, true);
                var right = container.GetXByBar(endBar > last ? last : endBar, true) + slot;
                if (endBar > last) right = region.Right;
                if (right <= left) right = left + slot;

                var entryY = container.GetYByPrice(setup.Entry, false);
                var stopY = container.GetYByPrice(setup.Stop, false);
                var targetY = container.GetYByPrice(setup.Target, false);

                var buy = setup.Side == Side.Buy;
                var win = ToColor(buy ? _buyColor : _sellColor);

                if (setup.OverCap)
                {
                    // Not a trade. It gets an outline so the reading is still visible, and no fill
                    // at all, because a filled block is this indicator saying "take this".
                    var grey = ToColor(_flatColor);
                    context.DrawRectangle(new RenderPen(Color.FromArgb(120, grey.R, grey.G, grey.B), 1f,
                                                        System.Drawing.Drawing2D.DashStyle.Dot),
                                          Rect(left, right, entryY, stopY));
                    continue;
                }

                context.FillRectangle(Color.FromArgb(46, 225, 65, 85), Rect(left, right, entryY, stopY));
                context.FillRectangle(Color.FromArgb(46, win.R, win.G, win.B), Rect(left, right, entryY, targetY));

                context.DrawRectangle(new RenderPen(Color.FromArgb(120, 225, 65, 85), 1f),
                                      Rect(left, right, entryY, stopY));
                context.DrawRectangle(new RenderPen(Color.FromArgb(120, win.R, win.G, win.B), 1f),
                                      Rect(left, right, entryY, targetY));

                context.DrawLine(new RenderPen(Color.FromArgb(255, win.R, win.G, win.B), 2f),
                                 left, entryY, right, entryY);

                // Labels stack on top of each other the moment trades cluster, which is exactly
                // when you most need to read them. Only the newest few get words.
                if (i >= _setups.Runs.Count - LabelledTrades)
                    Tag(context, container, region, setup, run, left, entryY, tick, win);
            }
        }

        /// <summary>The label on a trade: what it is, where, and what it risks, in that order.</summary>
        private void Tag(RenderContext context, IChartContainer container, Rectangle region, Setup setup,
                         SetupRun run, int left, int entryY, decimal tick, Color colour)
        {
            var buy = setup.Side == Side.Buy;
            var ticks = Math.Round(setup.Risk / tick);

            var text = (buy ? "LONG " : "SHORT ") + ChartInfo.GetPriceString(setup.Entry) +
                       "   " + ticks + "t" +
                       (setup.Kind == SetupKind.Divergence ? "   divergence" : "   continuation");

            if (!run.Live)
            {
                var moved = buy ? run.ExitLevel - setup.Entry : setup.Entry - run.ExitLevel;
                text += "   " + TradeBox.Signed(Math.Round(moved / tick)) + "t";
            }

            var size = context.MeasureString(text, _sizeFont);
            var y = buy ? entryY - size.Height - 3 : entryY + 3;

            var x = left;
            if (x + size.Width + 8 > region.Right) x = region.Right - size.Width - 8;
            if (x < region.Left) x = region.Left;

            context.FillRectangle(Color.FromArgb(240, colour.R / 4, colour.G / 4, colour.B / 4),
                                  new Rectangle(x, y, size.Width + 8, size.Height + 2), 2);
            context.DrawRectangle(new RenderPen(Color.FromArgb(200, colour.R, colour.G, colour.B), 1f),
                                  new Rectangle(x, y, size.Width + 8, size.Height + 2), 2);
            context.DrawString(text, _sizeFont, Color.FromArgb(255, colour.R, colour.G, colour.B), x + 4, y + 1);
        }

        private static Rectangle Rect(int left, int right, int a, int b)
        {
            var top = a < b ? a : b;
            var height = a < b ? b - a : a - b;

            return new Rectangle(left, top, right - left, height < 1 ? 1 : height);
        }

        /// <summary>
        /// The levels from the file, if you want a second copy of them. Off by default because you
        /// already have them drawn; this exists so you can check the model is reading what you
        /// think it is reading.
        /// </summary>
        private void RenderOutsideLevels(RenderContext context, IChartContainer container, Rectangle region)
        {
            if (_levels == null || _levels.Count == 0) return;

            for (var i = 0; i < _levels.Levels.Count; i++)
            {
                var level = _levels.Levels[i];

                var y = container.GetYByPrice(level.Price, false);
                if (y < region.Top || y > region.Bottom) continue;

                var rgb = level.Kind == LevelKind.Support ? ToColor(_buyColor)
                        : level.Kind == LevelKind.Resistance ? ToColor(_sellColor)
                        : ToColor(_flatColor);

                context.DrawLine(new RenderPen(Color.FromArgb(150, rgb.R, rgb.G, rgb.B), 1f,
                                               System.Drawing.Drawing2D.DashStyle.Dot),
                                 region.Left, y, region.Right, y);

                var text = level.Label + "  " + ChartInfo.GetPriceString(level.Price);
                var size = context.MeasureString(text, _font);

                context.DrawString(text, _font, Color.FromArgb(220, rgb.R, rgb.G, rgb.B),
                                   region.Left + 4, y - size.Height - 1);
            }
        }

        /// <summary>
        /// The cage: while the auction is balanced, the value area it is rotating inside, so it is
        /// obvious why nothing is being taken. Drawn only in balance -- a box that is always there
        /// says nothing.
        /// </summary>
        private void RenderCage(RenderContext context, IChartContainer container, Rectangle region,
                                int last)
        {
            if (_regime.Regime != Regime.Balance || _view == null) return;
            if (_view.VahIndex < 0 || _view.ValIndex < 0) return;

            var slot = (int)container.BarsWidth;
            if (slot < 1) slot = 1;

            var from = _regime.From;
            var left = container.GetXByBar(from, true);
            if (left < region.Left) left = region.Left;

            var top = container.GetYByPrice(_view.ValueHigh, false);
            var bottom = container.GetYByPrice(_view.ValueLow, false);
            if (bottom < top) { var swap = top; top = bottom; bottom = swap; }

            var rect = new Rectangle(left, top, region.Right - left, Math.Max(1, bottom - top));
            var grey = ToColor(_flatColor);

            context.FillRectangle(Color.FromArgb(26, grey.R, grey.G, grey.B), rect);
            context.DrawRectangle(new RenderPen(Color.FromArgb(120, grey.R, grey.G, grey.B), 1f,
                                                System.Drawing.Drawing2D.DashStyle.Dash), rect);

            var text = "CAGE  balanced, standing aside";
            context.DrawString(text, _sizeFont, Color.FromArgb(210, grey.R, grey.G, grey.B),
                               left + 4, top + 2);
        }

        /// <summary>
        /// One line: what kind of market this is, and how the trades on screen actually did.
        ///
        /// It exists because the trade box can be switched off and the two things worth knowing at
        /// a glance should not go with it. The record is the model's own arithmetic on its own
        /// rules -- it is not a backtest and does not include costs, and it says so.
        /// </summary>
        private void RenderStatusLine(RenderContext context, IChartContainer container, Rectangle region,
                                      int first, int last, decimal tick)
        {
            var won = 0;
            var lost = 0;
            var open = 0;
            var net = 0m;

            for (var i = 0; i < _setups.Runs.Count; i++)
            {
                var run = _setups.Runs[i];
                if (run.Setup.Bar < first || run.Setup.Bar > last) continue;
                if (run.Setup.OverCap) continue;

                if (run.Live) { open++; continue; }

                var moved = run.Setup.Side == Side.Buy
                    ? run.ExitLevel - run.Setup.Entry
                    : run.Setup.Entry - run.ExitLevel;

                var ticks = Math.Round(moved / tick);
                net += ticks;

                if (ticks > 0m) won++;
                else lost++;
            }

            // Costs are applied to the RECORD, not to individual trades: a per-trade figure would
            // imply this knows your fill quality, and it does not. At one-to-one, commission is
            // most of the answer, so a record without it is not a record.
            var cost = 0m;
            var tickCost = TickValue();

            if (RoundTurnCost > 0m && tickCost > 0m)
                cost = Math.Round(RoundTurnCost * (won + lost) / tickCost);

            var regime = MarketRegime.Word(_regime.Regime);
            var colour = _regime.Regime == Regime.Balance ? ToColor(_flatColor)
                       : _regime.Regime == Regime.Trend ? ToColor(_buyColor)
                       : Color.FromArgb(255, 200, 200, 210);

            var text = "OCEAN EFFORT   " + regime;

            if (_regime.Regime != Regime.Unknown)
            {
                text += "  (kept " + Math.Round(_regime.Efficiency * 100m) + "% of " +
                        _regime.SpanTicks + "t, " + Math.Round(_regime.InsideShare * 100m) +
                        "% inside value)";
            }

            // What the model measured for itself, so a threshold is never a number you cannot see.
            if (_gapMeasured || _riskMeasured)
            {
                text += "   |   measured:";
                if (_gapMeasured) text += " delta gap " + EffortMath.Compact(_gap);
                if (_riskMeasured) text += " risk cap " + _riskCap + "t";
            }

            if (_levels != null && _levels.Usable)
                text += "   |   " + _levels.Count + " outside levels";

            var traded = won + lost;
            if (traded > 0 || open > 0)
            {
                text += "   |   " + traded + " closed  " + won + "W " + lost + "L  " +
                        TradeBox.Signed(net) + "t";

                text += cost > 0m
                    ? "  ->  " + TradeBox.Signed(net - cost) + "t after " + cost + "t of cost"
                    : "  (before costs)";

                if (open > 0) text += "  (" + open + " open)";
            }
            else
            {
                text += "   |   no trades on these bars";
            }

            var size = context.MeasureString(text, _sizeFont);
            var x = region.Left + 8;
            var y = region.Top + 6;

            context.FillRectangle(Color.FromArgb(225, 14, 16, 21),
                                  new Rectangle(x, y, size.Width + 12, size.Height + 4), 2);
            context.DrawRectangle(new RenderPen(Color.FromArgb(140, colour.R, colour.G, colour.B), 1f),
                                  new Rectangle(x, y, size.Width + 12, size.Height + 4), 2);
            context.DrawString(text, _sizeFont, colour, x + 6, y + 2);
        }

        /// <summary>
        /// A new extreme the aggression did not follow, marked at the extreme itself with how far
        /// the delta held back. For a delta scalper this is the read, so it is drawn whether or
        /// not it became a trade.
        /// </summary>
        private void RenderDivergence(RenderContext context, IChartContainer container, Rectangle region,
                                      int first, int last)
        {
            var slot = (int)container.BarsWidth;
            if (slot < 1) slot = 1;

            for (var bar = first; bar <= last; bar++)
            {
                var divergence = _divergence[bar];
                if (!divergence.Found) continue;

                var y = container.GetYByPrice(divergence.Extreme, false);
                if (y < region.Top || y > region.Bottom) continue;

                var buy = divergence.Side == Side.Buy;
                var rgb = ToColor(buy ? _buyColor : _sellColor);
                var colour = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

                var x = container.GetXByBar(bar, true) + slot / 2;
                var tip = buy ? y + 3 : y - 3;
                var tail = buy ? tip + 11 : tip - 11;

                // A chevron pointing the way the aggression says price should go from here.
                context.DrawLine(new RenderPen(colour, 2f), x - 5, tail, x, tip);
                context.DrawLine(new RenderPen(colour, 2f), x + 5, tail, x, tip);

                var text = TradeBox.Signed(divergence.Gap);
                var size = context.MeasureString(text, _font);
                var ty = buy ? tail + 1 : tail - size.Height - 1;

                context.DrawString(text, _font, colour, x - size.Width / 2, ty);
            }
        }

        private void RenderTrail(RenderContext context, IChartContainer container, SetupRun record)
        {
            if (record == null || record.Setup.OverCap || record.Levels.Count == 0) return;

            var width = (int)container.BarsWidth;
            if (width < 1) width = 1;

            var rgb = ToColor(_trailColor);
            var pen = new RenderPen(Color.FromArgb(210, rgb.R, rgb.G, rgb.B), 1f);

            var previousY = int.MinValue;

            for (var i = 0; i < record.Levels.Count; i++)
            {
                var bar = record.Setup.Bar + i;
                var left = container.GetXByBar(bar, true);
                var right = left + width;
                var y = container.GetYByPrice(record.Levels[i], false);

                if (previousY != int.MinValue && previousY != y)
                    context.DrawLine(pen, left, previousY, left, y);

                context.DrawLine(pen, left, y, right, y);
                previousY = y;
            }

            if (record.ExitBar < 0) return;

            // A cross where the trade came off, so the line does not just stop for no visible reason.
            var exitX = container.GetXByBar(record.ExitBar, true) + width / 2;
            var exitY = container.GetYByPrice(record.ExitLevel, false);
            context.DrawLine(pen, exitX - 4, exitY - 4, exitX + 4, exitY + 4);
            context.DrawLine(pen, exitX - 4, exitY + 4, exitX + 4, exitY - 4);
        }

        /// <summary>
        /// Gathers everything the box says and hands it to <see cref="TradeBox"/>, which owns the
        /// wording and the numbers. Nothing here decides what anything means.
        /// </summary>
        private void RenderBox(RenderContext context, Rectangle region, int first, int last,
                               decimal tick, SetupRun record, List<string> errors)
        {
            var missing = 0;
            for (var bar = first; bar <= last; bar++)
            {
                if (!_facts[bar].HasValue) missing++;
            }

            var mark = LastMarkAt(last);

            var input = new BoxInputs();
            input.LastBar = last;
            input.Facts = _facts[last];
            input.Migration = _migration[last];
            input.Effort = _effort[last];
            input.EffortWindowBars = EffortWindow;
            input.HasAbsorption = mark.HasValue;
            if (mark.HasValue) input.Absorption = mark.Value;
            input.Run = record;
            input.Tick = tick;
            input.TickCost = TickValue();
            input.Price = p => ChartInfo.GetPriceString(p);
            input.Profile = _view;
            input.Clusters = _viewClusters;
            input.ProfileProblem = _viewProblem;
            input.TrackProblem = _trackProblem;
            input.RequireLevel = RequireLevel;

            if (_view != null)
                input.Where = RangeMath.Where(_view, _facts[last].Close, _viewClusters, LevelReachTicks);

            input.BarsMissingValue = missing;
            input.BarsInView = last - first + 1;
            input.Errors = errors;

            Draw(context, region, TradeBox.Build(input));
        }

        /// <summary>
        /// What one tick is worth per contract, straight from the platform. Zero when it has not
        /// said -- a guessed value would be wrong on every instrument but the one it was guessed
        /// for, and the box prints ticks only rather than money that might be a lie.
        /// </summary>
        private decimal TickValue()
        {
            try
            {
                var provider = DataProvider;
                var trading = provider == null ? null : provider.TradingManager;
                var security = trading == null ? null : trading.Security;

                return security == null ? 0m : security.TickCost;
            }
            catch
            {
                return 0m;
            }
        }

        /// <summary>
        /// Lays the box out: a headline strip, label and value columns sized to their contents, a
        /// status strip, and any warnings underneath.
        /// </summary>
        private void Draw(RenderContext context, Rectangle region, BoxModel box)
        {
            var size = BoxTextSize;
            if (size < 7) size = 7;

            var font = new RenderFont("Arial", size);
            var bold = new RenderFont("Arial", size, FontStyle.Bold);

            var line = context.MeasureString("Ag", font).Height + 3;
            var pad = 8;
            var gap = 12;

            var labelWidth = 0;
            var columns = new List<int>();

            for (var i = 0; i < box.Rows.Count; i++)
            {
                var row = box.Rows[i];
                if (row.Section) continue;

                var width = context.MeasureString(row.Label, bold).Width;
                if (width > labelWidth) labelWidth = width;

                for (var c = 0; c < row.Cells.Length; c++)
                {
                    var cell = context.MeasureString(row.Cells[c], font).Width;
                    while (columns.Count <= c) columns.Add(0);
                    if (cell > columns[c]) columns[c] = cell;
                }
            }

            var inner = labelWidth;
            for (var c = 0; c < columns.Count; c++) inner += gap + columns[c];

            var headline = "OCEAN EFFORT   " + (box.Headline ?? "");
            var headlineWidth = context.MeasureString(headline, bold).Width;
            if (headlineWidth > inner) inner = headlineWidth;

            if (box.Status != null)
            {
                var statusWidth = context.MeasureString(box.Status, bold).Width;
                if (statusWidth > inner) inner = statusWidth;
            }

            for (var i = 0; i < box.Warnings.Count; i++)
            {
                var warnWidth = context.MeasureString("! " + box.Warnings[i], font).Width;
                if (warnWidth > inner) inner = warnWidth;
            }

            var height = pad + line;                                   // headline strip
            for (var i = 0; i < box.Rows.Count; i++) height += line;
            height += pad / 2 + line;                                  // status strip
            height += box.Warnings.Count * line;
            height += pad;

            var width2 = inner + pad * 2;

            var x = Corner == ReadoutCorner.TopLeft || Corner == ReadoutCorner.BottomLeft
                ? region.Left + 8
                : region.Right - width2 - 8;

            var y = Corner == ReadoutCorner.TopLeft || Corner == ReadoutCorner.TopRight
                ? region.Top + 8
                : region.Bottom - height - 8;

            context.FillRectangle(Color.FromArgb(238, 14, 16, 21), new Rectangle(x, y, width2, height), 3);
            context.DrawRectangle(new RenderPen(Color.FromArgb(150, 70, 76, 92), 1f),
                                  new Rectangle(x, y, width2, height), 3);

            var head = Paint(box.HeadlineTone);
            context.FillRectangle(Color.FromArgb(38, head.R, head.G, head.B),
                                  new Rectangle(x + 1, y + 1, width2 - 2, line + pad / 2));

            context.DrawString("OCEAN EFFORT", bold, Color.FromArgb(255, 150, 156, 172), x + pad, y + pad / 2);

            var headText = box.Headline ?? "";
            var headSize = context.MeasureString(headText, bold);
            context.DrawString(headText, bold, head, x + width2 - pad - headSize.Width, y + pad / 2);

            var textY = y + pad / 2 + line + pad / 2;

            for (var i = 0; i < box.Rows.Count; i++)
            {
                var row = box.Rows[i];

                if (row.Section)
                {
                    var ruleY = textY + line / 2;
                    var labelSize = context.MeasureString(row.Label, bold);

                    context.DrawString(row.Label, bold, Color.FromArgb(255, 120, 126, 142), x + pad, textY);
                    context.DrawLine(new RenderPen(Color.FromArgb(70, 90, 96, 112), 1f),
                                     x + pad + labelSize.Width + 6, ruleY, x + width2 - pad, ruleY);

                    textY += line;
                    continue;
                }

                context.DrawString(row.Label, bold, Paint(row.LabelTone), x + pad, textY);

                var cellX = x + pad + labelWidth + gap;
                for (var c = 0; c < row.Cells.Length && c < columns.Count; c++)
                {
                    var tone = c < row.Tones.Length ? row.Tones[c] : Tone.Neutral;
                    context.DrawString(row.Cells[c], font, Paint(tone), cellX, textY);
                    cellX += columns[c] + gap;
                }

                textY += line;
            }

            var status = Paint(box.StatusTone);
            context.FillRectangle(Color.FromArgb(34, status.R, status.G, status.B),
                                  new Rectangle(x + 1, textY - 2, width2 - 2, line + 2));
            context.DrawString(box.Status ?? "", bold, status, x + pad, textY);
            textY += line + 2;

            for (var i = 0; i < box.Warnings.Count; i++)
            {
                context.DrawString("! " + box.Warnings[i], font, Color.FromArgb(255, 150, 156, 172),
                                   x + pad, textY);
                textY += line;
            }
        }

        private Color Paint(Tone tone)
        {
            switch (tone)
            {
                case Tone.Bullish: return ToColor(_buyColor);
                case Tone.Bearish: return ToColor(_sellColor);
                case Tone.Warning: return ToColor(_absorbColor);
                case Tone.Muted: return Color.FromArgb(255, 132, 138, 155);
                default: return Color.FromArgb(255, 226, 229, 238);
            }
        }

        private AbsorptionMark? LastMarkAt(int bar)
        {
            for (var i = _marks.Count - 1; i >= 0; i--)
            {
                if (_marks[i].Bar <= bar) return _marks[i];
            }

            return null;
        }

        private static string Word(Side side, string buy, string sell, string none)
        {
            if (side == Side.Buy) return buy;
            return side == Side.Sell ? sell : none;
        }

        private MColor Tint(Side side)
        {
            if (side == Side.Buy) return _buyColor;
            return side == Side.Sell ? _sellColor : _flatColor;
        }

        private static KeyValuePair<string, Color> Line(string text, MColor color)
        {
            return Line(text, ToColor(color));
        }

        private static KeyValuePair<string, Color> Line(string text, Color color)
        {
            return new KeyValuePair<string, Color>(text, color);
        }

        private static Color ToColor(MColor color)
        {
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        private void Status(RenderContext context, Rectangle region, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            context.DrawString(text, _readoutFont, Color.FromArgb(195, 195, 205),
                               region.Left + 6, region.Top + 6);
        }

        #endregion
    }
}
