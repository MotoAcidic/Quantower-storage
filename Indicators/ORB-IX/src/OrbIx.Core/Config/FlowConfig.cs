using System;
using System.Collections.Generic;
using System.Globalization;

using OrbIx.Core.Features;
using OrbIx.Core.Flow;

namespace OrbIx.Core.Config;

/// <summary>
/// Every setting the twelve absorbed Aramid Flow tools need, except the twelve on/off switches.
///
/// WHY THIS BLOCK EXISTS AT ALL. The requirement was "everything from Aramid Flow built into
/// ORB-IX without clutter ... but also don't take away from ORB-IX". Aramid Flow carried 146 chart
/// inputs and ORB-IX already carries 116; a straight merge would have produced a settings dialog
/// of 262 rows, which is the clutter the requirement names. So the switch for each tool stayed on
/// the chart — twelve rows — and every threshold, ratio, look-back, colour and alert distance moved
/// here, into the document ORB-IX already loads, validates and deploys.
///
/// NOTHING HERE DEFAULTS SILENTLY. The loader's rule holds inside this block exactly as it does
/// outside it: a missing key is a reported fault, never an invented value. The shipped document
/// reproduces Aramid Flow's own defaults, and a test asserts that number by number, so absorbing
/// the tools did not quietly re-tune them.
///
/// NOTHING HERE CLAIMS AN EDGE, and that is inherited rather than assumed. Aramid Flow's README
/// says "Display only. Nothing here claims an edge", absorption and imbalance are MEASURED NULLS
/// on this instrument (trial 008, 25,745 episodes), and the volume floors below are a frequency
/// calibration — they decide how often a display speaks, never whether it is worth acting on.
/// </summary>
public sealed class FlowConfig
{
    public required FlowIngestionConfig Ingestion { get; init; }

    /// <summary>The span a calibrated target rate is quoted over. See FlowCalibrationConfig.</summary>
    public required FlowCalibrationConfig Calibration { get; init; }
    public required FlowClusterStatisticsConfig ClusterStatistics { get; init; }
    public required FlowClusterSearchConfig ClusterSearch { get; init; }
    public required FlowStackedImbalanceConfig StackedImbalance { get; init; }
    public required FlowAbsorptionConfig Absorption { get; init; }
    public required FlowVolumeAbsorptionConfig VolumeAbsorption { get; init; }
    public required FlowAuctionConfig UnfinishedAuction { get; init; }
    public required FlowBigTradesConfig BigTrades { get; init; }
    public required FlowLiveCounterConfig LiveCounter { get; init; }
    public required FlowDomConfig DomLevels { get; init; }
    public required FlowFanConfig FibFan { get; init; }
    public required FlowTrendLinesConfig TrendLines { get; init; }
    public required FlowVolumeProfileConfig VolumeProfile { get; init; }
    public required FlowGexConfig Gex { get; init; }
}

/// <summary>
/// How the footprint store reads the tape and where its session figures reset.
/// </summary>
public sealed class FlowIngestionConfig
{
    /// <summary>How a print's side is decided. See <see cref="AggressorSource"/> for why it varies.</summary>
    public required AggressorSource Aggressor { get; init; }

    /// <summary>
    /// How long after a bar's clock close the store waits before closing it without a print.
    ///
    /// Covers the lag between a print's exchange stamp and its arrival. A print for a bucket
    /// already closed is COUNTED as late rather than re-opening the bucket — measured on Aramid
    /// Flow's first live attach, 2026-09-11.
    /// </summary>
    public required int CloseGraceMs { get; init; }

    /// <summary>
    /// How much history to rebuild on attach.
    ///
    /// DISTINCT FROM daysLookBack, which is how long a level stays VISIBLE once it exists. How far
    /// back to rebuild the footprints that CREATE levels is a different question, and deriving one
    /// from the other asked the platform for five days of tick history to draw lines that are
    /// mostly stale. Aramid Flow carried the two as separate numbers for that reason.
    /// </summary>
    public required int SeedHoursBack { get; init; }

    /// <summary>Where session-cumulative figures reset.</summary>
    public required SessionResetMode SessionReset { get; init; }

    /// <summary>
    /// Which configured session marks the reset when <see cref="SessionReset"/> is
    /// <see cref="SessionResetMode.Custom"/>.
    ///
    /// A SESSION NAME RATHER THAN A TIME AND A ZONE, which is how the overnight range already
    /// names its boundaries. Aramid Flow carried its own "18:00" and "America/New_York" inputs;
    /// ORB-IX's GLOBEX session is 18:00 in sessionTimeZone America/New_York, so naming it
    /// reproduces Flow's default exactly while removing a second place for the boundary to be
    /// declared — and a second place for the two to disagree.
    /// </summary>
    public required string ResetSession { get; init; }
}

/// <summary>
/// The numeric rows drawn per bar (ATAS Cluster Statistics, article 72000602624).
/// </summary>
public sealed class FlowClusterStatisticsConfig
{
    /// <summary>Which rows to show, in the order they are drawn.</summary>
    public required IReadOnlyList<StatRow> Rows { get; init; }

    /// <summary>
    /// Whether a row's shading is scaled against the visible part of the chart rather than the
    /// whole loaded history. ATAS "Proportion by the visible part of the chart".
    /// </summary>
    public required bool ProportionByVisible { get; init; }

    public required int RowHeightPx { get; init; }
    public required Rgb AskColour { get; init; }
    public required Rgb BidColour { get; init; }
    public required Rgb VolumeColour { get; init; }
    public required bool ShowRowHeaders { get; init; }

    /// <summary>
    /// Rows that alert once a bar's value reaches the threshold. An empty map is alerts off.
    ///
    /// A MAP RATHER THAN THE COMMA STRING THE CHART INPUT HAD TO BE. "Delta=2000,Volume=8000"
    /// was a string only because an input row cannot hold a structure; a document can, so the
    /// threshold is a number the loader validates instead of text parsed at render time.
    /// </summary>
    public required IReadOnlyDictionary<StatRow, double> Alerts { get; init; }
}

/// <summary>
/// Clusters matching a filter set, marked on the chart (ATAS Cluster Search, article 72000602240).
/// </summary>
public sealed class FlowClusterSearchConfig
{
    public required ClusterMode Mode { get; init; }

    /// <summary>
    /// The value a cluster must reach in the selected mode.
    ///
    /// The presenter demonstrated 2,000 and said "for MNQ it would be something like 5,000"
    /// (44:55). The shipped default is his demonstrated number, not his aside, because the aside
    /// is not a measurement.
    /// </summary>
    public required double MinValue { get; init; }

    /// <summary>Upper bound; 0 is no upper bound.</summary>
    public required double MaxValue { get; init; }

    public required double MinAverageTrade { get; init; }
    public required double MaxAverageTrade { get; init; }
    public required double MinVolumePercent { get; init; }
    public required double MaxVolumePercent { get; init; }
    public required double BidAskImbalancePercent { get; init; }

    /// <summary>Signed delta filter; 0 is off.</summary>
    public required double DeltaFilter { get; init; }

    public required CandleDirection Direction { get; init; }
    public required int BarsRange { get; init; }
    public required int PriceRange { get; init; }
    public required int PipsFromHigh { get; init; }
    public required int PipsFromLow { get; init; }
    public required PriceLocation Location { get; init; }
    public required int MinCandleHeight { get; init; }
    public required int MaxCandleHeight { get; init; }
    public required int MinBodyHeight { get; init; }
    public required int MaxBodyHeight { get; init; }

    public required bool UseTimeFilter { get; init; }
    public required TimeOnly TimeFrom { get; init; }
    public required TimeOnly TimeTo { get; init; }

    /// <summary>Judge closed bars only. ATAS "Use previous close".</summary>
    public required bool UsePreviousClose { get; init; }

    public required bool OnlyOnePerBar { get; init; }
    public required Rgb Colour { get; init; }

    /// <summary>Draw every marker at <see cref="Size"/> rather than scaling it by strength.</summary>
    public required bool FixedSizes { get; init; }

    public required int Size { get; init; }
    public required int MinSize { get; init; }
    public required int MaxSize { get; init; }

    /// <summary>Leave a price line where a cluster was found. The presenter's "X marks the spot".</summary>
    public required bool ShowPriceLevel { get; init; }

    public required LevelVisibility Visibility { get; init; }
    public required AlertRule Alerts { get; init; }

    /// <summary>
    /// The engine's own settings record, built from these.
    ///
    /// THE ZONE IS ORB-IX'S, PASSED IN RATHER THAN DECLARED HERE. The time filter needs the zone
    /// its times are in, and ORB-IX already declares one document-wide as sessionTimeZone. Aramid
    /// Flow declared a second; identical by default, and one careless edit from disagreeing.
    /// </summary>
    public ClusterSearchSettings ToSettings(TimeZoneInfo sessionZone)
    {
        ArgumentNullException.ThrowIfNull(sessionZone);

        return new ClusterSearchSettings(
            this.Mode, this.MinValue, this.MaxValue, this.MinAverageTrade, this.MaxAverageTrade,
            this.MinVolumePercent, this.MaxVolumePercent, this.BidAskImbalancePercent, this.DeltaFilter,
            this.Direction, this.BarsRange, this.PriceRange, this.PipsFromHigh, this.PipsFromLow,
            this.Location, this.MinCandleHeight, this.MaxCandleHeight, this.MinBodyHeight,
            this.MaxBodyHeight, this.UseTimeFilter, this.TimeFrom, this.TimeTo, sessionZone,
            this.OnlyOnePerBar);
    }
}

/// <summary>
/// A volume floor, and where the number came from — or why there is no number.
/// </summary>
/// <param name="MinVolume">
/// Contracts the diagonal must carry to be judged at all. Meaningless unless
/// <paramref name="Resolved"/> is true.
/// </param>
/// <param name="Explanation">
/// Where the number came from, in words, for the log. A floor is the single setting that decides
/// how much the display speaks, so a reader who sees too many or too few marks should be able to
/// find out why without reading code. When the floor did not resolve, this says what would settle
/// it.
/// </param>
/// <param name="Resolved">
/// False when a measured floor was asked for on a chart whose bars are not TIME bars — a tick or
/// range-bar chart, which reports no period at all, or a Renko, Kagi, Line Break or
/// Points-and-Figures chart, which reports the SOURCE period its shapes are built from and is the
/// more dangerous case of the two. The measurement is per time-bar period, so neither yields a
/// number to use. See <see cref="TimeBarAggregation"/>, which carries the four type names read from
/// the installed assembly.
///
/// THIS IS WHY THE FLAG EXISTS RATHER THAN A DEFAULT: a silent fallback to the one-minute floor
/// would draw marks calibrated for a chart the operator is not looking at.
/// </param>
public readonly record struct ResolvedFloor(double MinVolume, string Explanation, bool Resolved);

/// <summary>
/// How long the span is that a calibrated target rate is quoted over.
///
/// STATED, NOT DERIVED, because the configuration does not contain it. A session here carries
/// an OPEN time and no length, so any duration inferred from one would be invented — the gap to
/// the next session's open is not the same thing, and the trading day is not either. The target
/// is "marks per session", so what a session means for that purpose has to be written down.
///
/// 390 minutes is the US equity-index regular session, which is the span the 37-session sweep
/// this default's rate came from was measured over. A venue or product with a different session
/// needs a different number here, and the rate it produces will mean what this says it means
/// either way.
/// </summary>
public sealed class FlowCalibrationConfig
{
    public required int SessionMinutes { get; init; }

    public TimeSpan SessionLength => TimeSpan.FromMinutes(this.SessionMinutes);
}

/// <summary>
/// The diagonal-imbalance thresholds shared by the stacked-imbalance and absorption displays.
/// </summary>
public sealed class FlowStackThresholds
{
    /// <summary>ATAS "Imbalance Ratio" as a multiple: 3.0 is 300%.</summary>
    public required double Ratio { get; init; }

    /// <summary>ATAS "Imbalance Range": consecutive imbalanced rows that make a stack.</summary>
    public required int MinLevels { get; init; }

    /// <summary>Where the volume floor comes from.</summary>
    public required ImbalanceFloorSource MinVolumeSource { get; init; }

    /// <summary>
    /// How often the display should speak, in marks per session, when the floor is
    /// <see cref="ImbalanceFloorSource.Calibrated"/>.
    ///
    /// THE SETTING IS THE RATE, NOT THE FLOOR, and that is the point of it. A volume floor is
    /// a number about one contract's tape that means nothing on another; "about seven marks a
    /// session" is a statement about how busy a reader wants the chart, and it transfers. The
    /// operator picked 7.2 from a sweep because that was the rate the sweep produced — this
    /// lets somebody ask for more or fewer without knowing what a diagonal is.
    /// </summary>
    public required double TargetMarksPerSession { get; init; }

    /// <summary>
    /// The floor to use when <see cref="MinVolumeSource"/> is <see cref="ImbalanceFloorSource.Fixed"/>.
    ///
    /// Null under <see cref="ImbalanceFloorSource.Measured"/>, and required under Fixed — enforced
    /// by the loader's cross-field validation rather than by a default, so a document cannot carry
    /// a stale number that nothing reads.
    /// </summary>
    public double? MinVolumeFixed { get; init; }

    /// <summary>ATAS "Ignore zero values": leave a diagonal unjudged when the opposing side is empty.</summary>
    public required bool IgnoreZero { get; init; }

    /// <summary>
    /// The floor for a bar period, and where it came from.
    /// </summary>
    /// <param name="barPeriod">
    /// The chart's bar period, or null on a chart that has none. A fixed floor does not need one;
    /// a measured floor cannot be had without one.
    /// </param>
    /// <param name="calibrated">
    /// What <see cref="OrbIx.Core.Features.InstrumentCalibration"/> measured on this chart's own
    /// bars, or null when nothing has been measured yet. Only consulted under
    /// <see cref="ImbalanceFloorSource.Calibrated"/>, and ignored when it reports it could not
    /// measure — a calibration that refused is not a floor.
    /// </param>
    public ResolvedFloor ResolveFloor(
        TimeSpan? barPeriod, OrbIx.Core.Features.InstrumentFloor? calibrated = null)
    {
        if (this.MinVolumeSource == ImbalanceFloorSource.Fixed)
        {
            var fixedFloor = this.MinVolumeFixed
                ?? throw new InvalidOperationException(
                    "A fixed volume floor has no number. Configuration validation should have "
                    + "refused this document before it reached the chart.");

            return new ResolvedFloor(
                fixedFloor,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"min volume {fixedFloor:N0}, fixed in the configuration"),
                Resolved: true);
        }

        if (this.MinVolumeSource == ImbalanceFloorSource.Calibrated)
        {
            if (calibrated is { Measured: true } onThisTape)
                return new ResolvedFloor(onThisTape.MinVolume, onThisTape.Explanation, Resolved: true);

            // NOT YET, RATHER THAN NEVER. Calibration needs bars, and on a fresh attach there
            // are none until the seed lands. Falling through to the swept table keeps the
            // display drawing meanwhile, and the explanation says the number is a stand-in so
            // nobody reads it as this instrument's own.
            var fallback = barPeriod is { } waiting && waiting > TimeSpan.Zero
                ? ImbalanceVolumeFloor.For(waiting)
                : (VolumeFloor?)null;

            var why = calibrated?.Explanation ?? "no tape measured yet";

            return fallback is { } stand
                ? new ResolvedFloor(
                    stand.MinVolume,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"min volume {stand.MinVolume:N0}, STAND-IN while calibrating ({why})"),
                    Resolved: true)
                : new ResolvedFloor(
                    0d,
                    $"no floor: calibration has nothing yet ({why}), and this chart has no bar "
                    + "period to borrow a swept number for",
                    Resolved: false);
        }

        if (barPeriod is not { } period || period <= TimeSpan.Zero)
        {
            return new ResolvedFloor(
                0d,
                "no measured volume floor: the floors were measured on time bars, per period, "
                + "and this chart's bars are not time bars. Use a time-bar chart, or set "
                + "minVolumeSource to Fixed.",
                Resolved: false);
        }

        var measured = ImbalanceVolumeFloor.For(period);
        return new ResolvedFloor(measured.MinVolume, measured.Explain(), Resolved: true);
    }

    /// <summary>
    /// The scan's settings for a bar period.
    ///
    /// Refuses rather than invents when the floor did not resolve. A caller reaching here with an
    /// unresolved floor has ignored <see cref="ResolvedFloor.Resolved"/>, which is a defect in the
    /// caller and not a condition to paper over with a borrowed number.
    /// </summary>
    public ImbalanceSettings ToSettings(
        TimeSpan? barPeriod, OrbIx.Core.Features.InstrumentFloor? calibrated = null)
        => this.ToSettings(this.ResolveFloor(barPeriod, calibrated));

    /// <summary>
    /// The scan's settings for a floor SOMEBODY ELSE ALREADY RESOLVED.
    ///
    /// THIS EXISTS SO THE GATE AND THE FLOOR CANNOT DISAGREE. They used to be resolved
    /// independently: FlowDisplay decided whether a tool may draw, and the builder separately
    /// re-resolved the number it drew at from its own calibration field. Two resolution points
    /// over a value that moves is two answers, and the chart showed one while the status line
    /// showed the other. Handing the display's already-resolved floor straight to the scan makes
    /// the disagreement unrepresentable rather than merely unlikely.
    /// </summary>
    public ImbalanceSettings ToSettings(ResolvedFloor floor)
    {
        if (!floor.Resolved)
            throw new InvalidOperationException(floor.Explanation);

        return new ImbalanceSettings(this.Ratio, floor.MinVolume, this.IgnoreZero, this.MinLevels);
    }
}

/// <summary>
/// Lines left by runs of stacked imbalance (ATAS Stacked Imbalance, article 72000602474).
/// </summary>
/// <summary>
/// Heavy volume at a price the bar could not leave, in two tiers.
///
/// SEPARATE FROM <see cref="FlowAbsorptionConfig"/> BECAUSE IT ASKS A DIFFERENT QUESTION. That
/// one reads the footprint diagonals, which was measured over 37 MNQ sessions to be the
/// stacked-imbalance display in a second colour. This one reads volume against the bar's own
/// DISPLACEMENT.
/// </summary>
public sealed class FlowVolumeAbsorptionConfig
{
    /// <summary>Draw the moderate tier.</summary>
    public required bool DrawTier1 { get; init; }

    /// <summary>Draw the heavy tier.</summary>
    public required bool DrawTier2 { get; init; }

    /// <summary>Contracts a price must carry before it is considered at all.</summary>
    public required double MinVolume { get; init; }

    /// <summary>The moderate threshold, in volume per tick of the bar's travel.</summary>
    public required double Tier1Ratio { get; init; }

    /// <summary>The heavy threshold. Cannot sit below the moderate one.</summary>
    public required double Tier2Ratio { get; init; }

    /// <summary>How near two absorption prices must be to count as one zone.</summary>
    public required double ZoneTicks { get; init; }

    /// <summary>How many days of retired levels are kept.</summary>
    public required int DaysLookBack { get; init; }

    public required Rgb Tier1BullishColour { get; init; }
    public required Rgb Tier1BearishColour { get; init; }
    public required Rgb Tier2BullishColour { get; init; }
    public required Rgb Tier2BearishColour { get; init; }

    /// <summary>Line thickness in pixels.</summary>
    public required int LineWidth { get; init; }

    /// <summary>Line opacity, 0 transparent to 255 opaque.</summary>
    public required int Opacity { get; init; }

    /// <summary>The thresholds as the scan takes them.</summary>
    public AbsorptionTierSettings ToSettings() => new(
        this.MinVolume, this.Tier1Ratio, this.Tier2Ratio, this.ZoneTicks);
}

public sealed class FlowStackedImbalanceConfig
{
    public required FlowStackThresholds Thresholds { get; init; }
    public required LevelVisibility Visibility { get; init; }
    public required Rgb BullishColour { get; init; }
    public required Rgb BearishColour { get; init; }
    public required int LineWidth { get; init; }
    public required AlertRule Alerts { get; init; }
}

/// <summary>
/// Lines left where a stack was absorbed (ATAS Absorption, article 72000641183).
/// </summary>
/// <summary>Which measurement the absorption display draws.</summary>
public enum AbsorptionSource
{
    /// <summary>
    /// The footprint diagonals, at their own volume floor — what Aramid Flow drew.
    ///
    /// MEASURED TO BE THE STACKED-IMBALANCE DISPLAY IN A SECOND COLOUR. Across 37 sessions of
    /// MNQ tick history it puts at most TWO lines on the chart that stacked imbalance is not
    /// already drawing, at any floor that draws anything at all; at floor 45 it is that display
    /// exactly, 0 of 267 differing. Kept because it is what Flow shipped and removing it would
    /// take something away, not because the two readings are different tools.
    /// </summary>
    Footprint = 0,

    /// <summary>
    /// The BOOK: did resting size hold while volume traded through it.
    ///
    /// A question the footprint cannot answer at any floor — it knows what traded, never whether
    /// the size standing there held, was consumed, or was pulled before it was hit.
    /// </summary>
    Book = 1,
}

public sealed class FlowAbsorptionConfig
{
    /// <summary>
    /// Which measurement to draw. See <see cref="AbsorptionSource"/> — the two are not two
    /// settings of one tool, they are different questions about the same tape.
    /// </summary>
    public required AbsorptionSource Source { get; init; }

    /// <summary>
    /// Aggressive volume an episode must carry before the BOOK reading draws it. Unused by the
    /// footprint reading, which has its own floor on <see cref="Thresholds"/>.
    /// </summary>
    public required double BookMinVolume { get; init; }

    /// <summary>
    /// The span the BOOK reading asks over, in seconds.
    ///
    /// A SPAN IS REQUIRED AND IS A STATED CHOICE, NOT A DISCOVERED ONE. Over a millisecond
    /// nothing is ever absorbed and over an hour everything is, and NO OFFICIAL SOURCE DEFINES
    /// IT — Quantower's documentation defines delta as traded volume and says nothing about
    /// absorption. Five seconds is the number phase1/microstructure.py used and trial 008
    /// measured against.
    /// </summary>
    public required double BookWindowSeconds { get; init; }

    /// <summary>Ratio below which size left faster than it traded: pulled, not filled.</summary>
    public required double BookCancelledBelow { get; init; }

    /// <summary>Ratio at or above which far more traded than left: the level was refilled.</summary>
    public required double BookAbsorbedAtOrAbove { get; init; }

    public required FlowStackThresholds Thresholds { get; init; }

    /// <summary>
    /// Judge the forming bar as well as closed ones. ATAS "Last bar".
    ///
    /// Off by default, and that is Aramid Flow's default rather than a preference: a level drawn
    /// from a bar still filling can appear and vanish within the bar.
    /// </summary>
    public required bool JudgeFormingBar { get; init; }

    public required LevelVisibility Visibility { get; init; }

    public required Rgb BullishColour { get; init; }
    public required Rgb BearishColour { get; init; }
    public required int LineWidth { get; init; }
    public required AlertRule Alerts { get; init; }
}

/// <summary>
/// Bar extremes where one side traded and the other did not (ATAS Unfinished Auction,
/// article 72000602495).
/// </summary>
public sealed class FlowAuctionConfig
{
    /// <summary>Volume the bar's low must carry on the bid before it counts.</summary>
    public required double BidFilter { get; init; }

    /// <summary>Volume the bar's high must carry on the ask before it counts.</summary>
    public required double AskFilter { get; init; }

    public required LevelVisibility Visibility { get; init; }
    public required Rgb LowColour { get; init; }
    public required Rgb HighColour { get; init; }
    public required int LineWidth { get; init; }
    public required AlertRule Alerts { get; init; }

    public UnfinishedAuctionSettings ToSettings() => new(this.BidFilter, this.AskFilter);
}

/// <summary>
/// Large prints, marked where they happened (ATAS Big Trades, article 72000602332).
/// </summary>
public sealed class FlowBigTradesConfig
{
    public required BigTradeMode Mode { get; init; }
    public required double MinVolume { get; init; }

    /// <summary>Upper bound; 0 is no upper bound.</summary>
    public required double MaxVolume { get; init; }

    public required ExecutionPrice ExecutionPrice { get; init; }

    /// <summary>
    /// How long consecutive prints are aggregated in cumulative mode.
    ///
    /// A STATED CHOICE, NOT A DOCUMENTED DEFAULT: the ATAS article names the mode but not the
    /// window.
    /// </summary>
    public required int AggregationWindowMs { get; init; }

    public required PriceLocation Location { get; init; }
    public required bool ShowValue { get; init; }
    public required Rgb BuyColour { get; init; }
    public required Rgb SellColour { get; init; }

    /// <summary>For a print the feed did not classify — drawn, not dropped.</summary>
    public required Rgb UnclassifiedColour { get; init; }

    public required bool FixedSizes { get; init; }
    public required int Size { get; init; }
    public required int MinSize { get; init; }
    public required int MaxSize { get; init; }

    /// <summary>Shade the price band a big trade covered.</summary>
    public required bool Zones { get; init; }

    public required bool ZonesBiggestOnly { get; init; }

    /// <summary>Bars a zone extends for; 0 runs it to the right edge.</summary>
    public required int ZoneBars { get; init; }

    public required bool Alert { get; init; }

    public BigTradeSettings ToSettings()
        => new(
            this.Mode, this.MinVolume, this.MaxVolume, this.ExecutionPrice,
            TimeSpan.FromMilliseconds(this.AggregationWindowMs));
}

/// <summary>
/// The forming bar's buy and sell contracts, in the corner of the pane.
/// </summary>
public sealed class FlowLiveCounterConfig
{
    public required int FontSize { get; init; }

    /// <summary>
    /// Where the counter sits, from the pane's top-right corner.
    ///
    /// MEASURED OFFSETS, NOT CHOSEN ONES. The defaults clear the platform's own overlay strip
    /// along the top of the pane — measured on the operator's chart 2026-09-11, where a counter at
    /// +12 px sat inside that strip.
    /// </summary>
    public required int OffsetX { get; init; }

    public required int OffsetY { get; init; }
}

/// <summary>
/// The largest resting levels in the book (ATAS MBO DOM, article 72000633231).
/// </summary>
public sealed class FlowDomConfig
{
    /// <summary>How far from the touch to look; 0 reads the whole book.</summary>
    public required int WithinLevels { get; init; }

    public required int TopPerSide { get; init; }

    /// <summary>Levels below this size are not drawn.</summary>
    public required double VolumeFilter { get; init; }

    public required Rgb BidColour { get; init; }
    public required Rgb AskColour { get; init; }

    /// <summary>Draw the depth profile at the right edge.</summary>
    public required bool Profile { get; init; }

    public required int ProfileWidthPx { get; init; }

    /// <summary>
    /// Mark the largest single order at a level.
    ///
    /// Only a per-order feed can answer this. On an aggregated book the figure does not exist, and
    /// the overlay says so rather than drawing the level total and letting it read as one order.
    /// </summary>
    public required bool LargestOrder { get; init; }

    /// <summary>
    /// How often to PULL the whole book from the platform, in milliseconds. 0 leaves the event
    /// stream in charge.
    ///
    /// THE PULL EXISTS BECAUSE THE EVENT STREAM IS FABRICATED ON THIS CONNECTION. Measured on
    /// the operator's chart 2026-09-14: 19,762 Level 2 updates, all stamped
    /// <see cref="OrbIx.Core.Flow.DepthLadder.SyntheticLevelOneId"/>. The platform's own
    /// depth-of-market call returned 50 prices a side and 679 per-order levels on that same
    /// chart in that same minute.
    /// </summary>
    public required int SnapshotMs { get; init; }
}

/// <summary>A Fibonacci fan over the look-back's extremes.</summary>
public sealed class FlowFanConfig
{
    public required FanAnchor Anchors { get; init; }
    public required int LookBackBars { get; init; }

    /// <summary>The fan's levels. MetaTrader's three by default — see <see cref="FibFan"/>.</summary>
    public required IReadOnlyList<double> Ratios { get; init; }

    public required Rgb Colour { get; init; }
}

/// <summary>Two-tap trend lines through pivot highs and lows.</summary>
public sealed class FlowTrendLinesConfig
{
    public required int PivotLeftBars { get; init; }
    public required int PivotRightBars { get; init; }
    public required Rgb Colour { get; init; }
}

/// <summary>
/// The look-back volume profile.
///
/// ROW SIZE, VALUE AREA AND WIDTH ARE NOT HERE. ORB-IX already carries those as chart inputs for
/// its own fixed-range and anchored profiles, and one profile engine reading two sets of geometry
/// settings is two profiles that look different for no stated reason. Only what is specific to this
/// profile — how far back it starts, and its colour — lives here.
/// </summary>
public sealed class FlowVolumeProfileConfig
{
    public required int LookBackBars { get; init; }
    public required Rgb Colour { get; init; }
}

/// <summary>
/// Gamma-exposure walls.
///
/// THE SOURCE IS DEAD AND THIS IS NOT A REVIVAL. The levels are read from an AramidGamma file and
/// never computed here; the options feed behind that file is gone (memory: Unusual Whales is dead),
/// so a file that still exists is as stale as its last write. The manual prices are what make the
/// display usable without it, and the overlay states the file's age rather than presenting an old
/// level as a current one.
/// </summary>
public sealed class FlowGexConfig
{
    /// <summary>The AramidGamma levels file; blank uses the manual prices alone.</summary>
    public required string LevelsFile { get; init; }

    /// <summary>Manual call wall in chart price; 0 is off.</summary>
    public required double ManualCallWall { get; init; }

    /// <summary>Manual put wall in chart price; 0 is off.</summary>
    public required double ManualPutWall { get; init; }

    /// <summary>Manual max pain in chart price; 0 is off.</summary>
    public required double ManualMaxPain { get; init; }

    public required Rgb CallColour { get; init; }
    public required Rgb PutColour { get; init; }
    public required Rgb OtherColour { get; init; }
}
