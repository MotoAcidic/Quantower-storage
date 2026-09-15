using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using OrbIx.Core.Flow;

namespace OrbIx.Core.Config;

/// <summary>
/// The <c>flow</c> section: settings for the twelve display tools absorbed from Aramid Flow.
///
/// A SEPARATE FILE, THE SAME LOADER. These are <c>partial</c> members of
/// <see cref="OrbIxConfigLoader"/> rather than a second loader, because the whole point of the
/// existing one is that there is exactly one validator and one problem list. A "FlowConfigLoader"
/// would be a second set of rules, and the two would drift.
///
/// EVERY KEY IS REQUIRED. A missing threshold is reported with its path, the same as everywhere
/// else in this document. The one conditional key is the fixed volume floor, which must be present
/// under <see cref="ImbalanceFloorSource.Fixed"/> and ABSENT under
/// <see cref="ImbalanceFloorSource.Measured"/> — a number nothing reads is a number that will one
/// day be believed.
/// </summary>
public static partial class OrbIxConfigLoader
{
    private static FlowConfig ReadFlow(Reader flow) => new()
    {
        Ingestion = ReadFlowIngestion(flow.Object("ingestion")),
        Calibration = new FlowCalibrationConfig
        {
            SessionMinutes = flow.Object("calibration").Int("sessionMinutes"),
        },
        ClusterStatistics = ReadFlowClusterStatistics(flow.Object("clusterStatistics")),
        ClusterSearch = ReadFlowClusterSearch(flow.Object("clusterSearch")),
        StackedImbalance = ReadFlowStackedImbalance(flow.Object("stackedImbalance")),
        Absorption = ReadFlowAbsorption(flow.Object("absorption")),
        VolumeAbsorption = ReadFlowVolumeAbsorption(flow.Object("volumeAbsorption")),
        UnfinishedAuction = ReadFlowAuction(flow.Object("unfinishedAuction")),
        BigTrades = ReadFlowBigTrades(flow.Object("bigTrades")),
        LiveCounter = ReadFlowLiveCounter(flow.Object("liveCounter")),
        DomLevels = ReadFlowDom(flow.Object("domLevels")),
        FibFan = ReadFlowFan(flow.Object("fibFan")),
        TrendLines = ReadFlowTrendLines(flow.Object("trendLines")),
        VolumeProfile = ReadFlowVolumeProfile(flow.Object("volumeProfile")),
        Gex = ReadFlowGex(flow.Object("gex")),
    };

    private static FlowIngestionConfig ReadFlowIngestion(Reader r) => new()
    {
        Aggressor = r.Enum<AggressorSource>("aggressor"),
        CloseGraceMs = r.Int("closeGraceMs"),
        SeedHoursBack = r.Int("seedHoursBack"),
        SessionReset = r.Enum<SessionResetMode>("sessionReset"),
        ResetSession = r.String("resetSession"),
    };

    private static FlowClusterStatisticsConfig ReadFlowClusterStatistics(Reader r) => new()
    {
        Rows = r.EnumArray<StatRow>("rows"),
        ProportionByVisible = r.Bool("proportionByVisible"),
        RowHeightPx = r.Int("rowHeightPx"),
        AskColour = r.Colour("askColour"),
        BidColour = r.Colour("bidColour"),
        VolumeColour = r.Colour("volumeColour"),
        ShowRowHeaders = r.Bool("showRowHeaders"),
        Alerts = r.EnumKeyedDoubleMap<StatRow>("alerts"),
    };

    private static FlowClusterSearchConfig ReadFlowClusterSearch(Reader r) => new()
    {
        Mode = r.Enum<ClusterMode>("mode"),
        MinValue = r.Double("minValue"),
        MaxValue = r.Double("maxValue"),
        MinAverageTrade = r.Double("minAverageTrade"),
        MaxAverageTrade = r.Double("maxAverageTrade"),
        MinVolumePercent = r.Double("minVolumePercent"),
        MaxVolumePercent = r.Double("maxVolumePercent"),
        BidAskImbalancePercent = r.Double("bidAskImbalancePercent"),
        DeltaFilter = r.Double("deltaFilter"),
        Direction = r.Enum<CandleDirection>("direction"),
        BarsRange = r.Int("barsRange"),
        PriceRange = r.Int("priceRange"),
        PipsFromHigh = r.Int("pipsFromHigh"),
        PipsFromLow = r.Int("pipsFromLow"),
        Location = r.Enum<PriceLocation>("location"),
        MinCandleHeight = r.Int("minCandleHeight"),
        MaxCandleHeight = r.Int("maxCandleHeight"),
        MinBodyHeight = r.Int("minBodyHeight"),
        MaxBodyHeight = r.Int("maxBodyHeight"),
        UseTimeFilter = r.Bool("useTimeFilter"),
        TimeFrom = r.WallClock("timeFrom"),
        TimeTo = r.WallClock("timeTo"),
        UsePreviousClose = r.Bool("usePreviousClose"),
        OnlyOnePerBar = r.Bool("onlyOnePerBar"),
        Colour = r.Colour("colour"),
        FixedSizes = r.Bool("fixedSizes"),
        Size = r.Int("size"),
        MinSize = r.Int("minSize"),
        MaxSize = r.Int("maxSize"),
        ShowPriceLevel = r.Bool("showPriceLevel"),
        Visibility = ReadFlowVisibility(r.Object("visibility")),
        Alerts = ReadFlowAlerts(r.Object("alerts")),
    };

    private static FlowStackedImbalanceConfig ReadFlowStackedImbalance(Reader r) => new()
    {
        Thresholds = ReadFlowStackThresholds(r.Object("thresholds")),
        Visibility = ReadFlowVisibility(r.Object("visibility")),
        BullishColour = r.Colour("bullishColour"),
        BearishColour = r.Colour("bearishColour"),
        LineWidth = r.Int("lineWidth"),
        Alerts = ReadFlowAlerts(r.Object("alerts")),
    };

    private static FlowVolumeAbsorptionConfig ReadFlowVolumeAbsorption(Reader r) => new()
    {
        DrawTier1 = r.Bool("drawTier1"),
        DrawTier2 = r.Bool("drawTier2"),
        MinVolume = r.Double("minVolume"),
        Tier1Ratio = r.Double("tier1Ratio"),
        Tier2Ratio = r.Double("tier2Ratio"),
        ZoneTicks = r.Double("zoneTicks"),
        DaysLookBack = r.Int("daysLookBack"),
        Tier1BullishColour = r.Colour("tier1BullishColour"),
        Tier1BearishColour = r.Colour("tier1BearishColour"),
        Tier2BullishColour = r.Colour("tier2BullishColour"),
        Tier2BearishColour = r.Colour("tier2BearishColour"),
        LineWidth = r.Int("lineWidth"),
        Opacity = r.Int("opacity"),
    };

    private static FlowAbsorptionConfig ReadFlowAbsorption(Reader r) => new()
    {
        Source = r.Enum<AbsorptionSource>("source"),
        BookMinVolume = r.Double("bookMinVolume"),
        BookWindowSeconds = r.Double("bookWindowSeconds"),
        BookCancelledBelow = r.Double("bookCancelledBelow"),
        BookAbsorbedAtOrAbove = r.Double("bookAbsorbedAtOrAbove"),
        Thresholds = ReadFlowStackThresholds(r.Object("thresholds")),
        JudgeFormingBar = r.Bool("judgeFormingBar"),
        Visibility = ReadFlowVisibility(r.Object("visibility")),
        BullishColour = r.Colour("bullishColour"),
        BearishColour = r.Colour("bearishColour"),
        LineWidth = r.Int("lineWidth"),
        Alerts = ReadFlowAlerts(r.Object("alerts")),
    };

    private static FlowStackThresholds ReadFlowStackThresholds(Reader r) => new()
    {
        Ratio = r.Double("ratio"),
        MinLevels = r.Int("minLevels"),
        MinVolumeSource = r.Enum<ImbalanceFloorSource>("minVolumeSource"),
        TargetMarksPerSession = r.Double("targetMarksPerSession"),
        MinVolumeFixed = r.OptionalDouble("minVolumeFixed"),
        IgnoreZero = r.Bool("ignoreZero"),
    };

    private static FlowAuctionConfig ReadFlowAuction(Reader r) => new()
    {
        BidFilter = r.Double("bidFilter"),
        AskFilter = r.Double("askFilter"),
        Visibility = ReadFlowVisibility(r.Object("visibility")),
        LowColour = r.Colour("lowColour"),
        HighColour = r.Colour("highColour"),
        LineWidth = r.Int("lineWidth"),
        Alerts = ReadFlowAlerts(r.Object("alerts")),
    };

    private static FlowBigTradesConfig ReadFlowBigTrades(Reader r) => new()
    {
        Mode = r.Enum<BigTradeMode>("mode"),
        MinVolume = r.Double("minVolume"),
        MaxVolume = r.Double("maxVolume"),
        ExecutionPrice = r.Enum<ExecutionPrice>("executionPrice"),
        AggregationWindowMs = r.Int("aggregationWindowMs"),
        Location = r.Enum<PriceLocation>("location"),
        ShowValue = r.Bool("showValue"),
        BuyColour = r.Colour("buyColour"),
        SellColour = r.Colour("sellColour"),
        UnclassifiedColour = r.Colour("unclassifiedColour"),
        FixedSizes = r.Bool("fixedSizes"),
        Size = r.Int("size"),
        MinSize = r.Int("minSize"),
        MaxSize = r.Int("maxSize"),
        Zones = r.Bool("zones"),
        ZonesBiggestOnly = r.Bool("zonesBiggestOnly"),
        ZoneBars = r.Int("zoneBars"),
        Alert = r.Bool("alert"),
    };

    private static FlowLiveCounterConfig ReadFlowLiveCounter(Reader r) => new()
    {
        FontSize = r.Int("fontSize"),
        OffsetX = r.Int("offsetX"),
        OffsetY = r.Int("offsetY"),
    };

    private static FlowDomConfig ReadFlowDom(Reader r) => new()
    {
        WithinLevels = r.Int("withinLevels"),
        TopPerSide = r.Int("topPerSide"),
        VolumeFilter = r.Double("volumeFilter"),
        BidColour = r.Colour("bidColour"),
        AskColour = r.Colour("askColour"),
        Profile = r.Bool("profile"),
        ProfileWidthPx = r.Int("profileWidthPx"),
        LargestOrder = r.Bool("largestOrder"),
        SnapshotMs = r.Int("snapshotMs"),
    };

    private static FlowFanConfig ReadFlowFan(Reader r) => new()
    {
        Anchors = r.Enum<FanAnchor>("anchors"),
        LookBackBars = r.Int("lookBackBars"),
        Ratios = r.DoubleArray("ratios"),
        Colour = r.Colour("colour"),
    };

    private static FlowTrendLinesConfig ReadFlowTrendLines(Reader r) => new()
    {
        PivotLeftBars = r.Int("pivotLeftBars"),
        PivotRightBars = r.Int("pivotRightBars"),
        Colour = r.Colour("colour"),
    };

    private static FlowVolumeProfileConfig ReadFlowVolumeProfile(Reader r) => new()
    {
        LookBackBars = r.Int("lookBackBars"),
        Colour = r.Colour("colour"),
    };

    private static FlowGexConfig ReadFlowGex(Reader r) => new()
    {
        LevelsFile = r.String("levelsFile"),
        ManualCallWall = r.Double("manualCallWall"),
        ManualPutWall = r.Double("manualPutWall"),
        ManualMaxPain = r.Double("manualMaxPain"),
        CallColour = r.Colour("callColour"),
        PutColour = r.Colour("putColour"),
        OtherColour = r.Colour("otherColour"),
    };

    /// <summary>
    /// How long a feature's lines last.
    ///
    /// THE BAR COUNT IS PRESENT ONLY WHEN IT IS READ, which is the same rule the fixed volume floor
    /// follows and for the same reason: a key that sits in the document doing nothing is a key
    /// somebody will eventually change and then trust. Under
    /// <see cref="LevelExtent.FixedBars"/> the count is required; under either of the others it is
    /// refused.
    /// </summary>
    private static LevelVisibility ReadFlowVisibility(Reader r)
    {
        var extent = r.Enum<LevelExtent>("extent");
        var printBars = r.OptionalInt("printBars");

        // ONLY THE HALF THAT NEEDS THE KEY'S PRESENCE IS CHECKED HERE. That a fixed-bar extent
        // needs a positive count is checked in ValidateFlowVisibility, which sees the resolved
        // rule; an absent key arrives there as zero and is refused by the same rule that refuses
        // an explicit zero. Checking it in both places was a second copy of one rule, and a
        // mutation that deleted this one changed nothing observable — which is how it was found.
        if (extent != LevelExtent.FixedBars && printBars is not null)
        {
            r.Problem(
                "printBars",
                $"remove it, or set extent to FixedBars. Under {extent} the line's end does not "
                + "depend on a bar count and this number is never read.");
        }

        return new LevelVisibility(extent, printBars ?? 0, r.Int("daysLookBack"));
    }

    private static AlertRule ReadFlowAlerts(Reader r)
        => new(r.Bool("onSignal"), r.Int("approachTicks"));

    // ---- validation ------------------------------------------------------------------

    /// <summary>
    /// The rules that need more than one key to check.
    ///
    /// These are reported rather than thrown, like every other rule in this loader, so one bad edit
    /// produces one list of problems on the panel instead of a sequence of edit-run cycles. The
    /// engines' own <c>Validate</c> methods still throw — they are the backstop for a caller that
    /// built settings some other way — but a configured chart should never reach them.
    /// </summary>
    private static void ValidateFlow(OrbIxConfig c, List<string> problems)
    {
        var f = c.Flow;

        ValidateFlowIngestion(c, f.Ingestion, problems);
        ValidateFlowClusterStatistics(f.ClusterStatistics, problems);
        ValidateFlowClusterSearch(f.ClusterSearch, problems);

        ValidateFlowStackThresholds("flow.stackedImbalance.thresholds", f.StackedImbalance.Thresholds, problems);
        ValidateFlowVisibility("flow.stackedImbalance.visibility", f.StackedImbalance.Visibility, problems);
        RequireInRange(problems, "flow.stackedImbalance.lineWidth", f.StackedImbalance.LineWidth, 1, 6);
        ValidateFlowAlerts("flow.stackedImbalance.alerts", f.StackedImbalance.Alerts, problems);

        ValidateFlowStackThresholds("flow.absorption.thresholds", f.Absorption.Thresholds, problems);
        ValidateFlowVisibility("flow.absorption.visibility", f.Absorption.Visibility, problems);
        RequireInRange(problems, "flow.absorption.lineWidth", f.Absorption.LineWidth, 1, 6);
        ValidateFlowAlerts("flow.absorption.alerts", f.Absorption.Alerts, problems);

        // The BOOK reading's own floor. Validated whatever the source is: a document that turns
        // the source over later must not discover then that its floor was never usable.
        if (!(f.Absorption.BookMinVolume > 0))
        {
            problems.Add(
                "flow.absorption.bookMinVolume: must be positive — a floor of zero would draw a "
                + "line for every print that touched a level.");
        }

        if (!(f.Absorption.BookWindowSeconds > 0))
        {
            problems.Add(
                "flow.absorption.bookWindowSeconds: must be positive — absorption asks whether "
                + "size held over a SPAN.");
        }

        if (!(f.Absorption.BookCancelledBelow > 0)
            || !(f.Absorption.BookAbsorbedAtOrAbove > f.Absorption.BookCancelledBelow))
        {
            problems.Add(
                "flow.absorption.bookAbsorbedAtOrAbove: must sit above bookCancelledBelow, and "
                + "both above zero — otherwise a level is judged absorbed and cancelled at once.");
        }

        ValidateFlowAuction(f.UnfinishedAuction, problems);
        ValidateFlowBigTrades(f.BigTrades, problems);
        ValidateFlowLiveCounter(f.LiveCounter, problems);
        ValidateFlowDom(f.DomLevels, problems);
        ValidateFlowFan(f.FibFan, problems);
        ValidateFlowTrendLines(f.TrendLines, problems);

        RequireAtLeast(problems, "flow.volumeProfile.lookBackBars", f.VolumeProfile.LookBackBars, 10);

        ValidateFlowGex(f.Gex, problems);
    }

    private static void ValidateFlowIngestion(
        OrbIxConfig c, FlowIngestionConfig i, List<string> problems)
    {
        if (i.CloseGraceMs < 0)
            problems.Add("flow.ingestion.closeGraceMs: cannot be negative.");

        RequireAtLeast(problems, "flow.ingestion.seedHoursBack", i.SeedHoursBack, 1);

        // THE SESSION IS NAMED, SO THE NAME HAS TO EXIST. Aramid Flow carried its own start time
        // and zone; naming one of this document's sessions instead removes the second declaration,
        // and this is what stops the reference dangling after a session is renamed or removed.
        if (i.SessionReset != SessionResetMode.Custom)
            return;

        if (string.IsNullOrWhiteSpace(i.ResetSession))
        {
            problems.Add(
                "flow.ingestion.resetSession: a custom session reset must name the session it "
                + "resets at.");
            return;
        }

        var known = c.Sessions.Named.ContainsKey(i.ResetSession)
                    || c.Sessions.Custom.Any(
                        s => string.Equals(s.Name, i.ResetSession, StringComparison.OrdinalIgnoreCase));

        if (!known)
        {
            problems.Add(
                $"flow.ingestion.resetSession: '{i.ResetSession}' is not a configured session. "
                + $"Known: {string.Join(", ", c.Sessions.Named.Keys.Concat(c.Sessions.Custom.Select(s => s.Name)))}.");
        }
    }

    private static void ValidateFlowClusterStatistics(
        FlowClusterStatisticsConfig s, List<string> problems)
    {
        // An empty row list draws an empty panel, which reads as a broken tool rather than a
        // disabled one. The switch on the chart is how it is turned off.
        if (s.Rows.Count == 0)
        {
            problems.Add(
                "flow.clusterStatistics.rows: name at least one row, or turn the tool off on the "
                + "chart.");
        }

        foreach (var duplicate in s.Rows.GroupBy(row => row).Where(g => g.Count() > 1))
            problems.Add($"flow.clusterStatistics.rows: '{duplicate.Key}' appears more than once.");

        RequireInRange(problems, "flow.clusterStatistics.rowHeightPx", s.RowHeightPx, 10, 60);

        foreach (var (row, threshold) in s.Alerts)
        {
            if (!(threshold > 0) || double.IsInfinity(threshold))
            {
                problems.Add(
                    $"flow.clusterStatistics.alerts.{row}: a threshold must be a positive number; "
                    + "remove the entry to turn the alert off.");
            }
        }
    }

    private static void ValidateFlowClusterSearch(FlowClusterSearchConfig s, List<string> problems)
    {
        const string P = "flow.clusterSearch";

        RequireAtLeast(problems, P + ".barsRange", s.BarsRange, 1);
        RequireAtLeast(problems, P + ".priceRange", s.PriceRange, 1);

        foreach (var (key, value) in new[]
        {
            (".minValue", s.MinValue), (".maxValue", s.MaxValue),
            (".minAverageTrade", s.MinAverageTrade), (".maxAverageTrade", s.MaxAverageTrade),
            (".minVolumePercent", s.MinVolumePercent), (".maxVolumePercent", s.MaxVolumePercent),
            (".bidAskImbalancePercent", s.BidAskImbalancePercent),
        })
        {
            if (value < 0 || double.IsNaN(value))
                problems.Add($"{P}{key}: a filter cannot be negative (0 turns it off).");
        }

        RequireUpperAboveLower(problems, P, "value", s.MinValue, s.MaxValue);
        RequireUpperAboveLower(problems, P, "averageTrade", s.MinAverageTrade, s.MaxAverageTrade);
        RequireUpperAboveLower(problems, P, "volumePercent", s.MinVolumePercent, s.MaxVolumePercent);

        foreach (var (key, value) in new[]
        {
            (".minVolumePercent", s.MinVolumePercent), (".maxVolumePercent", s.MaxVolumePercent),
        })
        {
            if (value > 100)
                problems.Add($"{P}{key}: a share of a bar's volume cannot exceed 100 percent.");
        }

        foreach (var (key, value) in new[]
        {
            (".pipsFromHigh", s.PipsFromHigh), (".pipsFromLow", s.PipsFromLow),
            (".minCandleHeight", s.MinCandleHeight), (".maxCandleHeight", s.MaxCandleHeight),
            (".minBodyHeight", s.MinBodyHeight), (".maxBodyHeight", s.MaxBodyHeight),
        })
        {
            if (value < 0)
                problems.Add($"{P}{key}: cannot be negative (0 turns it off).");
        }

        RequireUpperAboveLower(problems, P, "candleHeight", s.MinCandleHeight, s.MaxCandleHeight);
        RequireUpperAboveLower(problems, P, "bodyHeight", s.MinBodyHeight, s.MaxBodyHeight);

        ValidateFlowMarkerSizes(P, s.FixedSizes, s.Size, s.MinSize, s.MaxSize, problems);
        ValidateFlowVisibility(P + ".visibility", s.Visibility, problems);
        ValidateFlowAlerts(P + ".alerts", s.Alerts, problems);

        // A window that starts and ends at the same minute admits nothing, which is a filter
        // nobody means to set. Times that wrap past midnight are legitimate and are not refused.
        if (s.UseTimeFilter && s.TimeFrom == s.TimeTo)
        {
            problems.Add(
                $"{P}.timeFrom/timeTo: a time filter from and to the same minute matches nothing; "
                + "widen it or set useTimeFilter false.");
        }
    }

    private static void ValidateFlowStackThresholds(
        string path, FlowStackThresholds t, List<string> problems)
    {
        // At or below 1 every diagonal's larger side is imbalanced, which is not a threshold.
        if (!(t.Ratio > 1))
            problems.Add($"{path}.ratio: must exceed 1 — at 1 the larger side of every diagonal is imbalanced.");

        RequireAtLeast(problems, path + ".minLevels", t.MinLevels, 1);

        switch (t.MinVolumeSource)
        {
            case ImbalanceFloorSource.Fixed when t.MinVolumeFixed is null:
                problems.Add(
                    $"{path}.minVolumeFixed: required when minVolumeSource is Fixed.");
                break;

            case ImbalanceFloorSource.Fixed when !(t.MinVolumeFixed > 0):
                problems.Add(
                    $"{path}.minVolumeFixed: must be positive — a floor of zero judges diagonals "
                    + "nobody traded on.");
                break;

            // A NUMBER NOTHING READS IS A NUMBER THAT WILL ONE DAY BE BELIEVED. Under Measured the
            // floor comes from the bar period, so a fixed value here would sit in the document
            // looking authoritative and changing nothing.
            case ImbalanceFloorSource.Measured when t.MinVolumeFixed is not null:
                problems.Add(
                    $"{path}.minVolumeFixed: remove it, or set minVolumeSource to Fixed. Under "
                    + "Measured the floor comes from the bar period and this number is never read.");
                break;

            default:
                break;
        }
    }

    private static void ValidateFlowAuction(FlowAuctionConfig a, List<string> problems)
    {
        if (a.BidFilter < 0)
            problems.Add("flow.unfinishedAuction.bidFilter: cannot be negative (0 turns it off).");

        if (a.AskFilter < 0)
            problems.Add("flow.unfinishedAuction.askFilter: cannot be negative (0 turns it off).");

        RequireInRange(problems, "flow.unfinishedAuction.lineWidth", a.LineWidth, 1, 6);
        ValidateFlowVisibility("flow.unfinishedAuction.visibility", a.Visibility, problems);
        ValidateFlowAlerts("flow.unfinishedAuction.alerts", a.Alerts, problems);
    }

    private static void ValidateFlowBigTrades(FlowBigTradesConfig b, List<string> problems)
    {
        const string P = "flow.bigTrades";

        if (!(b.MinVolume > 0) || double.IsInfinity(b.MinVolume))
            problems.Add($"{P}.minVolume: must be positive — it is what makes a trade big.");

        if (b.MaxVolume < 0 || double.IsNaN(b.MaxVolume))
            problems.Add($"{P}.maxVolume: cannot be negative (0 means no upper limit).");

        RequireUpperAboveLower(problems, P, "volume", b.MinVolume, b.MaxVolume);

        if (b.Mode == BigTradeMode.Cumulative)
            RequireAtLeast(problems, P + ".aggregationWindowMs", b.AggregationWindowMs, 1);

        if (b.ZoneBars < 0)
            problems.Add($"{P}.zoneBars: cannot be negative (0 runs the zone to the right edge).");

        ValidateFlowMarkerSizes(P, b.FixedSizes, b.Size, b.MinSize, b.MaxSize, problems);
    }

    private static void ValidateFlowLiveCounter(FlowLiveCounterConfig l, List<string> problems)
    {
        RequireInRange(problems, "flow.liveCounter.fontSize", l.FontSize, 8, 40);

        if (l.OffsetX < 0)
            problems.Add("flow.liveCounter.offsetX: cannot be negative.");

        if (l.OffsetY < 0)
            problems.Add("flow.liveCounter.offsetY: cannot be negative.");
    }

    private static void ValidateFlowDom(FlowDomConfig d, List<string> problems)
    {
        if (d.WithinLevels < 0)
            problems.Add("flow.domLevels.withinLevels: cannot be negative (0 reads the whole book).");

        RequireInRange(problems, "flow.domLevels.topPerSide", d.TopPerSide, 1, 10);

        if (d.VolumeFilter < 0)
            problems.Add("flow.domLevels.volumeFilter: cannot be negative (0 turns it off).");

        RequireInRange(problems, "flow.domLevels.profileWidthPx", d.ProfileWidthPx, 20, 400);

        // 0 is off. Above that, a floor of 20ms because the fold timer's own floor is 20 and a
        // pull asked for more often than the fold that reads it cannot be delivered; a ceiling of
        // a minute because a book read less often than that is not a book anyone is watching.
        if (d.SnapshotMs != 0)
            RequireInRange(problems, "flow.domLevels.snapshotMs", d.SnapshotMs, 20, 60_000);
    }

    private static void ValidateFlowFan(FlowFanConfig f, List<string> problems)
    {
        RequireAtLeast(problems, "flow.fibFan.lookBackBars", f.LookBackBars, 10);

        if (f.Ratios.Count == 0)
        {
            problems.Add(
                "flow.fibFan.ratios: name at least one ratio, or turn the fan off on the chart.");
        }

        foreach (var ratio in f.Ratios)
        {
            // Zero puts the line on the origin and a non-finite ratio has no line at all.
            if (!double.IsFinite(ratio) || ratio <= 0)
                problems.Add($"flow.fibFan.ratios: '{ratio}' is not a positive ratio.");
        }

        foreach (var duplicate in f.Ratios.GroupBy(ratio => ratio).Where(g => g.Count() > 1))
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"flow.fibFan.ratios: {duplicate.Key} appears more than once, which draws one line twice."));
        }
    }

    private static void ValidateFlowTrendLines(FlowTrendLinesConfig t, List<string> problems)
    {
        RequireInRange(problems, "flow.trendLines.pivotLeftBars", t.PivotLeftBars, 1, 100);
        RequireInRange(problems, "flow.trendLines.pivotRightBars", t.PivotRightBars, 1, 100);
    }

    private static void ValidateFlowGex(FlowGexConfig g, List<string> problems)
    {
        foreach (var (key, value) in new[]
        {
            (".manualCallWall", g.ManualCallWall), (".manualPutWall", g.ManualPutWall),
            (".manualMaxPain", g.ManualMaxPain),
        })
        {
            if (value < 0 || double.IsNaN(value))
                problems.Add($"flow.gex{key}: cannot be negative (0 turns the level off).");
        }
    }

    private static void ValidateFlowVisibility(
        string path, LevelVisibility v, List<string> problems)
    {
        if (v.DaysLookBack < 0)
            problems.Add($"{path}.daysLookBack: cannot be negative (0 shows everything retained).");

        if (v.Extent == LevelExtent.FixedBars && v.PrintBars < 1)
            problems.Add($"{path}.printBars: must be at least 1 when extent is FixedBars.");
    }

    private static void ValidateFlowAlerts(string path, AlertRule rule, List<string> problems)
    {
        if (rule.ApproachTicks < 0)
            problems.Add($"{path}.approachTicks: cannot be negative (0 turns the approach alert off).");
    }

    /// <summary>
    /// Marker sizes, whether or not they are currently scaled.
    ///
    /// The bounds are checked even under a fixed size, because turning scaling back on should not
    /// be how an operator discovers the bounds were nonsense all along.
    /// </summary>
    private static void ValidateFlowMarkerSizes(
        string path, bool fixedSizes, int size, int minSize, int maxSize, List<string> problems)
    {
        RequireInRange(problems, path + ".size", size, 2, 60);
        RequireInRange(problems, path + ".minSize", minSize, 2, 60);
        RequireInRange(problems, path + ".maxSize", maxSize, 2, 60);

        if (minSize > maxSize)
        {
            problems.Add(
                $"{path}.minSize/maxSize: the smallest marker cannot be larger than the largest.");
        }

        if (!fixedSizes && (size < minSize || size > maxSize))
        {
            problems.Add(
                $"{path}.size: {size} sits outside minSize..maxSize, so a scaled marker can never "
                + "reach it. Set fixedSizes true, or bring it inside the range.");
        }
    }

    /// <summary>
    /// A maximum of zero means "no upper bound"; any other maximum has to be above its minimum.
    /// </summary>
    private static void RequireUpperAboveLower(
        List<string> problems, string path, string pair, double lower, double upper)
    {
        if (upper > 0 && upper <= lower)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{path}.max{char.ToUpperInvariant(pair[0])}{pair[1..]}: {upper} is not above min{char.ToUpperInvariant(pair[0])}{pair[1..]} {lower}, so nothing can match. Use 0 for no upper bound."));
        }
    }

    private static void RequireAtLeast(List<string> problems, string path, double value, double min)
    {
        if (value < min)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture, $"{path}: must be at least {min:G}."));
        }
    }

    private static void RequireInRange(
        List<string> problems, string path, double value, double min, double max)
    {
        if (value < min || value > max)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture, $"{path}: must be between {min:G} and {max:G}."));
        }
    }
}
