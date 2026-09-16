using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TradingPlatform.BusinessLayer;

using OrbIx.Core.Abstractions;
using OrbIx.Core.Direction;
using OrbIx.Core.Structure;
using OrbIx.Core.Features;
using OceansAnchor;
using AnchorZone = OceansAnchor.Zone;
using AnchorZoneKind = OceansAnchor.ZoneKind;
using OrbAggressor = OrbIx.Core.Abstractions.Aggressor;

namespace directionAbsorptionScalpStrategy
{
    /// <summary>
    /// Trades the ORB-IX "possible long/short" callout (OrbIx.Core.Direction.DirectionEngine)
    /// against a real structural level, using Ocean's Anchor's absorption state machine
    /// (Dormant -> Armed -> Triggered -> Confirmed/Expired/Broken) as the entry TIMING and the
    /// stop-loss reference, and OrbIx.Core's HH/LL, VWAP and prior-day/week levels as the
    /// take-profit candidates.
    ///
    /// WHAT DECIDES WHICH WAY: DirectionEngine's callout (structure votes across configured
    /// timeframes, price vs. VWAP, cumulative delta) -- the same read the ORB-IX indicator draws
    /// as a bright line on the chart.
    ///
    /// WHAT DECIDES WHEN: a real HH/LL support/resistance level is treated as an Anchor Zone.
    /// Price testing it arms the zone; a qualifying absorption print (Ocean's tape-based
    /// TapeAbsorption/DisplacementWatch test, OR the bar-shape ClusterAbsorption test when no
    /// tape event fires) triggers it; a delta flip or a CVD higher-low/lower-high in the trade's
    /// favour on a LATER bar confirms it, inside a bar-count clock, or the zone expires or
    /// breaks. ENTRY FIRES ONLY ON THE Confirmed TRANSITION, and only when DirectionCallout's
    /// current side agrees with the zone's side -- two independent readings, both required.
    ///
    /// WHAT DECIDES THE STOP: the confirming absorption print's own price extreme
    /// (Zone.ClusterLow/ClusterHigh) -- the trade is wrong the moment price trades through the
    /// size that was supposedly absorbing it. See AnchorAbsorption.cs / AnchorState.cs for the
    /// tested math this reuses verbatim; see the csproj for what was deliberately NOT ported
    /// (the footprint-based HVN zone BUILDER -- this strategy supplies its own zones from
    /// HH/LL structure instead).
    ///
    /// AUTO-TRADE, BUT ONLY EVER MEANT FOR A SIM/EVAL ACCOUNT -- see ConfirmSimOrEvalAccount and
    /// DryRun below. Position isolation follows srChannelBreakStrategy's StrategyTag/Comment
    /// pattern exactly, because this can run on the same account+symbol as other strategies.
    /// </summary>
    public sealed class DirectionAbsorptionScalpStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        private const string StrategyTag = "DirectionAbsorptionScalp";

        // ---- basic wiring, matching every existing strategy template ---------------------

        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Period", 2)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 3)]
        public DateTime StartPoint { get; set; }

        [InputParameter("Quantity", 4, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int Quantity { get; set; }

        // ---- safety gates, checked before ANY order-placement logic runs ------------------

        /// <summary>
        /// Must be explicitly set true before OnRun proceeds past its safety check. Not
        /// security theatre: it is a deliberate, auditable step the operator must take before
        /// this strategy can place a single order. There is no reliable, general way to ask the
        /// Quantower SDK "is this a sim/eval account" — this is the substitute. Independent of
        /// this flag, OnRun always logs the account's identity loudly, every run.
        /// </summary>
        [InputParameter("I confirm this account is sim/eval, not live", 5)]
        public bool ConfirmSimOrEvalAccount { get; set; }

        /// <summary>
        /// When true (the default), every place where this strategy would call
        /// Core.Instance.PlaceOrder instead logs the fully computed decision and returns — no
        /// order is sent. Meant to run for hours or days against live data before ever turning
        /// this off, so the decision log can be reviewed with zero capital at risk.
        /// </summary>
        [InputParameter("Dry run (log decisions, place no orders)", 6)]
        public bool DryRun { get; set; }

        // ---- direction callout (OrbIx.Core.Direction.DirectionEngine) ---------------------

        [InputParameter("Direction: include the chart's own timeframe", 10)]
        public bool DirectionIncludeChartTimeframe { get; set; }

        [InputParameter("Direction: higher timeframes (minutes, comma list)", 11)]
        public string DirectionHigherTimeframes { get; set; }

        [InputParameter("Direction: VWAP flat band (ticks)", 12, 0, 100, 0, 0)]
        public int DirectionVwapFlatTicks { get; set; }

        [InputParameter("Direction: delta flat band (contracts)", 13, 0, 1000000, 0, 0)]
        public int DirectionDeltaFlatContracts { get; set; }

        [InputParameter("Direction: callout min structure lanes to call HOLD", 14, 1, 10, 1, 0)]
        public int DirectionCalloutMinLanes { get; set; }

        [InputParameter("Direction: callout ADR% considered room spent", 15, 10, 300, 5, 0)]
        public int DirectionCalloutRoomSpentPercent { get; set; }

        // ---- HH/LL structure -> zone source (OrbIx.Core.Structure.HhLlEngine) -------------

        [InputParameter("HH/LL: left bars", 20, 1, 500, 1, 0)]
        public int HhLlLeftBars { get; set; }

        [InputParameter("HH/LL: right bars", 21, 1, 500, 1, 0)]
        public int HhLlRightBars { get; set; }

        // ---- Ocean's Anchor absorption + zone state machine -------------------------------

        [InputParameter("Absorption: min print size (contracts)", 30, 1, 100000, 1, 0)]
        public int AbsorptionSizeFloor { get; set; }

        [InputParameter("Absorption: zone buffer (ticks)", 31, 1, 100, 1, 0)]
        public int ZoneBufferTicks { get; set; }

        [InputParameter("Absorption: max displacement after print (ticks)", 32, 1, 100, 1, 0)]
        public int MaxDisplacementTicks { get; set; }

        [InputParameter("Absorption: displacement window (ms)", 33, 100, 30000, 100, 0)]
        public int DisplacementWindowMs { get; set; }

        [InputParameter("Absorption: cluster delta lookback (bars)", 34, 5, 200, 1, 0)]
        public int ClusterDeltaLookback { get; set; }

        [InputParameter("Absorption: cluster delta percentile", 35, 1, 50, 1, 0)]
        public int ClusterDeltaPercentile { get; set; }

        [InputParameter("Absorption: cluster extreme band (ticks)", 36, 1, 100, 1, 0)]
        public int ClusterExtremeTicks { get; set; }

        [InputParameter("Absorption: cluster extreme volume share (%)", 37, 1, 100, 1, 0)]
        public int ClusterExtremePct { get; set; }

        [InputParameter("Absorption: cluster min wick (ticks)", 38, 1, 100, 1, 0)]
        public int ClusterMinWickTicks { get; set; }

        [InputParameter("Absorption: cluster close position (%)", 39, 1, 100, 1, 0)]
        public int ClusterClosePosPct { get; set; }

        [InputParameter("Absorption: cluster tests required (of 4)", 40, 1, 4, 1, 0)]
        public int ClusterMinScore { get; set; }

        [InputParameter("Zone: confirmation clock (bars)", 41, 1, 50, 1, 0)]
        public int ZoneClockBars { get; set; }

        [InputParameter("Zone: break buffer (ticks)", 42, 1, 100, 1, 0)]
        public int ZoneBreakBufferTicks { get; set; }

        [InputParameter("Zone: one-timeframing guard (bars, 0=off)", 43, 0, 50, 1, 0)]
        public int ZoneOtfBars { get; set; }

        [InputParameter("Zone: max distance from session open (x ADR, 0=off)", 44, 0, 10, 1, 1)]
        public double ZoneMaxDistanceAdr { get; set; }

        // ---- target (take-profit) ----------------------------------------------------------

        [InputParameter("Target: use nearest HH/LL level", 50)]
        public bool TargetUseHhLl { get; set; }

        [InputParameter("Target: use VWAP", 51)]
        public bool TargetUseVwap { get; set; }

        [InputParameter("Target: use prior day high/low/close", 52)]
        public bool TargetUsePriorDay { get; set; }

        [InputParameter("Target: use prior week high/low", 53)]
        public bool TargetUsePriorWeek { get; set; }

        [InputParameter("Target: minimum distance (ticks)", 54, 1, 1000, 1, 0)]
        public int MinTargetDistanceTicks { get; set; }

        [InputParameter("Target: fallback distance (ticks, used only if no level qualifies)", 55, 1, 1000, 1, 0)]
        public int FallbackTargetTicks { get; set; }

        // ---- stop ---------------------------------------------------------------------------

        [InputParameter("Stop: min distance (ticks)", 60, 1, 1000, 1, 0)]
        public int MinStopTicks { get; set; }

        [InputParameter("Stop: max distance (ticks)", 61, 1, 5000, 1, 0)]
        public int MaxStopTicks { get; set; }

        // ---- risk / session management -------------------------------------------------------

        [InputParameter("Max daily loss ($, 0=off)", 70, 0, 100000, 50, 0)]
        public int MaxDailyLoss { get; set; }

        [InputParameter("Max drawdown ($, 0=off)", 71, 0, 100000, 50, 0)]
        public int MaxDrawdown { get; set; }

        [InputParameter("Max trades per session (0=off)", 72, 0, 200, 1, 0)]
        public int MaxTradesPerSession { get; set; }

        [InputParameter("Cooldown between entries (bars)", 73, 0, 500, 1, 0)]
        public int MinBarsBetweenEntries { get; set; }

        [InputParameter("RTH only (0=24h, 1=RTH only)", 74, 0, 1, 1, 0)]
        public int RthOnly { get; set; }

        [InputParameter("RTH start hour (EST)", 75, 0, 23, 1, 0)]
        public int RthStartHour { get; set; }

        [InputParameter("RTH end hour (EST)", 76, 0, 23, 1, 0)]
        public int RthEndHour { get; set; }

        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        public DirectionAbsorptionScalpStrategy() : base()
        {
            this.Name = "Direction + Absorption Scalp Strategy";
            this.Description = "Trades ORB-IX's direction callout against an HH/LL level, timed by "
                + "Ocean's Anchor's absorption state machine, targeting VWAP/HH-LL/prior day-week levels.";

            this.Period = Period.MIN1;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-5);
            this.Quantity = 1;

            this.ConfirmSimOrEvalAccount = false;
            this.DryRun = true;

            this.DirectionIncludeChartTimeframe = true;
            this.DirectionHigherTimeframes = "5,15,60";
            this.DirectionVwapFlatTicks = 2;
            this.DirectionDeltaFlatContracts = 100;
            this.DirectionCalloutMinLanes = 2;
            this.DirectionCalloutRoomSpentPercent = 100;

            this.HhLlLeftBars = 5;
            this.HhLlRightBars = 5;

            // NQ-family guesses, stated as guesses -- exactly per AnchorAbsorption.cs's own
            // doc comment ("An NQ number -- MNQ prints run 2-2.5x fatter and need their own").
            // Nothing here has been calibrated against this instrument.
            this.AbsorptionSizeFloor = 150;
            this.ZoneBufferTicks = 8;
            this.MaxDisplacementTicks = 6;
            this.DisplacementWindowMs = 2000;
            this.ClusterDeltaLookback = 20;
            this.ClusterDeltaPercentile = 10;
            this.ClusterExtremeTicks = 8;
            this.ClusterExtremePct = 30;
            this.ClusterMinWickTicks = 6;
            this.ClusterClosePosPct = 50;
            this.ClusterMinScore = 3;
            this.ZoneClockBars = 3;
            this.ZoneBreakBufferTicks = 3;
            this.ZoneOtfBars = 5;
            this.ZoneMaxDistanceAdr = 1.5;

            this.TargetUseHhLl = true;
            this.TargetUseVwap = true;
            this.TargetUsePriorDay = true;
            this.TargetUsePriorWeek = true;
            this.MinTargetDistanceTicks = 10;
            this.FallbackTargetTicks = 20;

            this.MinStopTicks = 6;
            this.MaxStopTicks = 40;

            this.MaxDailyLoss = 0;
            this.MaxDrawdown = 2000;
            this.MaxTradesPerSession = 10;
            this.MinBarsBetweenEntries = 5;

            this.RthOnly = 0;
            this.RthStartHour = 9;
            this.RthEndHour = 16;
        }

        // ==== engines ========================================================================

        private HistoricalData hdm;
        private HistoricalData dailyHdm;
        private string orderTypeId;

        private DirectionEngine direction;
        private HhLlEngine hhll;
        private VwapEngine vwapSession;

        private readonly DisplacementWatch displacementWatch = new DisplacementWatch();
        private readonly AbsorptionRules absorptionRules = new AbsorptionRules();
        private readonly ClusterRules clusterRules = new ClusterRules();
        private readonly StateRules zoneStateRules = new StateRules();
        private readonly SignalEngine longSignal = new SignalEngine();
        private readonly SignalEngine shortSignal = new SignalEngine();

        private AnchorZone longZone;   // SupportLong — tested by longs
        private AnchorZone shortZone;  // ResistanceShort — tested by shorts
        private int longZoneFromSegmentBar = int.MinValue;
        private int shortZoneFromSegmentBar = int.MinValue;

        private readonly List<double> recentDeltasLong = new List<double>();
        private readonly List<double> recentDeltasShort = new List<double>();
        private readonly List<BarFacts> recentBarsForOtf = new List<BarFacts>();

        // Ticks accumulated during the currently-forming bar, consumed and cleared at its close.
        private readonly List<(double Price, double Size, double SignedSize)> formingBarTicks
            = new List<(double, double, double)>();

        private int barCounter = -1;
        private int lastCalloutSide;
        private int lastEntryBarIndex = int.MinValue;

        private IReadOnlyList<Level> referenceLevels = Array.Empty<Level>();
        private readonly List<DailySessionBar> dailyBarsCache = new List<DailySessionBar>();
        private double averageDailyRange = double.NaN;
        private double sessionOpenPrice = double.NaN;
        private double lastPrice = double.NaN;
        private int lastDailyBarCount = -1;

        private bool waitOpenPosition;
        private bool waitClosePositions;

        private double dailyPnl;
        private int lastResetDay = -1;
        private bool dailyLimitHit;

        private double totalRealizedPnl;
        private double peakEquity;
        private bool drawdownLimitHit;

        private int tradesThisSession;
        private int lastTradeSessionDay = -1;
        private bool tradesLimitHit;

        // ==== lifecycle ======================================================================

        protected override void OnRun()
        {
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
            this.dailyPnl = 0;
            this.lastResetDay = -1;
            this.dailyLimitHit = false;
            this.totalRealizedPnl = 0;
            this.peakEquity = 0;
            this.drawdownLimitHit = false;
            this.tradesThisSession = 0;
            this.lastTradeSessionDay = -1;
            this.tradesLimitHit = false;
            this.barCounter = -1;
            this.lastCalloutSide = 0;
            this.lastEntryBarIndex = int.MinValue;
            this.longZone = null;
            this.shortZone = null;
            this.longZoneFromSegmentBar = int.MinValue;
            this.shortZoneFromSegmentBar = int.MinValue;

            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());
            if (this.CurrentSymbol == null) { this.Log("Symbol not specified.", StrategyLoggingLevel.Error); return; }

            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());
            if (this.CurrentAccount == null) { this.Log("Account not specified.", StrategyLoggingLevel.Error); return; }

            if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
            {
                this.Log("Symbol and Account are from different connections.", StrategyLoggingLevel.Error);
                return;
            }

            // LOUD AND UNCONDITIONAL — every run, whether or not the sim/eval flag is set, so
            // the account identity is always in the log.
            this.Log(
                $"[Account] name='{this.CurrentAccount.Name}' id={this.CurrentAccount.Id} "
                + $"connection={this.CurrentAccount.ConnectionId} — "
                + $"ConfirmSimOrEvalAccount={this.ConfirmSimOrEvalAccount}, DryRun={this.DryRun}",
                StrategyLoggingLevel.Trading);

            if (!this.DryRun && !this.ConfirmSimOrEvalAccount)
            {
                this.Log(
                    "Refusing to run with DryRun off and ConfirmSimOrEvalAccount unchecked. "
                    + "This strategy places real orders and is meant for a sim/eval account only — "
                    + "either turn DryRun back on, or explicitly confirm the account above.",
                    StrategyLoggingLevel.Error);
                return;
            }

            this.orderTypeId = Core.OrderTypes
                .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market)
                ?.Id;
            if (string.IsNullOrEmpty(this.orderTypeId))
            {
                this.Log("Connection does not support market orders.", StrategyLoggingLevel.Error);
                return;
            }

            this.absorptionRules.SizeFloor = this.AbsorptionSizeFloor;
            this.absorptionRules.ZoneBufferTicks = this.ZoneBufferTicks;
            this.absorptionRules.MaxDisplacementTicks = this.MaxDisplacementTicks;
            this.absorptionRules.DisplacementWindowMs = this.DisplacementWindowMs;

            this.clusterRules.DeltaLookback = this.ClusterDeltaLookback;
            this.clusterRules.DeltaPercentile = this.ClusterDeltaPercentile;
            this.clusterRules.ExtremeTicks = this.ClusterExtremeTicks;
            this.clusterRules.ExtremePct = this.ClusterExtremePct;
            this.clusterRules.MinWickTicks = this.ClusterMinWickTicks;
            this.clusterRules.ClosePosPct = this.ClusterClosePosPct;
            this.clusterRules.MinScore = this.ClusterMinScore;

            this.zoneStateRules.ClockBars = this.ZoneClockBars;
            this.zoneStateRules.BreakBufferTicks = this.ZoneBreakBufferTicks;
            this.zoneStateRules.ZoneBufferTicks = this.ZoneBufferTicks;
            this.zoneStateRules.OtfBars = this.ZoneOtfBars;
            this.zoneStateRules.OtfGuardOn = this.ZoneOtfBars > 0;
            this.longSignal.Rules = this.zoneStateRules;
            this.shortSignal.Rules = this.zoneStateRules;

            var directionLanes = new List<(string Name, TimeSpan Period)>();
            if (this.DirectionIncludeChartTimeframe && this.Period.Duration is { } chartDuration && chartDuration > TimeSpan.Zero)
                directionLanes.Add((DescribeMinutes(chartDuration.TotalMinutes), chartDuration));

            foreach (var token in (this.DirectionHigherTimeframes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
                {
                    var period = TimeSpan.FromMinutes(minutes);
                    var name = DescribeMinutes(minutes);
                    if (!directionLanes.Any(l => l.Name == name))
                        directionLanes.Add((name, period));
                }
            }

            if (directionLanes.Count == 0)
                directionLanes.Add(("1m", TimeSpan.FromMinutes(1)));

            this.direction = new DirectionEngine(
                directionLanes,
                Math.Max(1, this.HhLlLeftBars),
                Math.Max(1, this.HhLlRightBars),
                new DirectionSettings(
                    this.DirectionVwapFlatTicks, this.DirectionDeltaFlatContracts,
                    Math.Max(1, this.DirectionCalloutMinLanes),
                    this.DirectionCalloutRoomSpentPercent / 100d));

            this.hhll = new HhLlEngine(Math.Max(1, this.HhLlLeftBars), Math.Max(1, this.HhLlRightBars));
            this.vwapSession = new VwapEngine();
            this.vwapSession.Anchor(Core.TimeUtils.DateTimeUtcNow);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);
            this.dailyHdm = this.CurrentSymbol.GetHistory(
                Period.DAY1, this.CurrentSymbol.HistoryType, Core.TimeUtils.DateTimeUtcNow.AddDays(-60));

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded += this.Core_TradeAdded;

            this.CurrentSymbol.NewLast += this.OnLast;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;
            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.dailyHdm.NewHistoryItem += this.DailyHdm_OnNewHistoryItem;

            this.RefreshDailyLevels();

            this.Log(
                $"Started [{StrategyTag}] — lanes: {string.Join(", ", directionLanes.Select(l => l.Name))} — "
                + "FRVP/AVP POC target: not implemented (this connector's historical volume-analysis "
                + "gap makes it unavailable most of the time — see ORB-IX CLAUDE.md).",
                StrategyLoggingLevel.Trading);
        }

        protected override void OnStop()
        {
            Core.PositionAdded -= this.Core_PositionAdded;
            Core.PositionRemoved -= this.Core_PositionRemoved;
            Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
            Core.TradeAdded -= this.Core_TradeAdded;

            if (this.CurrentSymbol != null)
                this.CurrentSymbol.NewLast -= this.OnLast;

            if (this.hdm != null)
            {
                this.hdm.NewHistoryItem -= this.Hdm_OnNewHistoryItem;
                this.hdm.HistoryItemUpdated -= this.Hdm_HistoryItemUpdated;
                this.hdm.Dispose();
            }

            if (this.dailyHdm != null)
            {
                this.dailyHdm.NewHistoryItem -= this.DailyHdm_OnNewHistoryItem;
                this.dailyHdm.Dispose();
            }

            base.OnStop();
        }

        private static string DescribeMinutes(double minutes)
        {
            if (minutes >= 60 && minutes % 60 == 0)
                return $"{(int)(minutes / 60)}h";
            return $"{(int)minutes}m";
        }

        // ==== position isolation, StrategyTag/Comment pattern from srChannelBreakStrategy =====

        private Position[] MyPositions() => Core.Instance.Positions
            .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount && x.Comment == StrategyTag)
            .ToArray();

        private Order[] MyOrders() => Core.Instance.Orders
            .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount && x.Comment == StrategyTag)
            .ToArray();

        private void Core_PositionAdded(Position obj)
        {
            if (obj.Comment != StrategyTag) return;
            var positions = this.MyPositions();
            double netQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);
            if (Math.Abs(netQty) >= this.Quantity)
                this.waitOpenPosition = false;
        }

        private void Core_PositionRemoved(Position obj)
        {
            if (obj.Comment != StrategyTag) return;
            var positions = this.MyPositions();

            if (!positions.Any())
            {
                this.waitClosePositions = false;
                if (this.totalRealizedPnl > this.peakEquity) this.peakEquity = this.totalRealizedPnl;

                foreach (var order in this.MyOrders())
                {
                    var r = order.Cancel();
                    if (r.Status != TradingOperationResultStatus.Success)
                        this.Log($"[Order] failed to cancel leftover order: {r.Message}", StrategyLoggingLevel.Error);
                }
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount || obj.Comment != StrategyTag) return;
            if (obj.Status == OrderStatus.Refused)
            {
                this.waitOpenPosition = false;
                this.waitClosePositions = false;
            }
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount || obj.Comment != StrategyTag) return;

            if (obj.GrossPnl != null)
            {
                this.dailyPnl += obj.GrossPnl.Value;
                this.totalRealizedPnl += obj.GrossPnl.Value;
            }
        }

        // ==== risk / session resets, EST-18:00 rollover per the existing templates ============

        private static int EstSessionDayKey(DateTime utcNow)
        {
            var est = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            return est.Hour >= 18 ? est.DayOfYear + 1 : est.DayOfYear;
        }

        private void CheckSessionReset()
        {
            var dayKey = EstSessionDayKey(Core.TimeUtils.DateTimeUtcNow);

            if (dayKey != this.lastResetDay)
            {
                this.lastResetDay = dayKey;
                this.dailyPnl = 0;
                this.dailyLimitHit = false;
                this.Log("[Risk] daily P&L reset (new EST session).", StrategyLoggingLevel.Trading);
            }

            if (dayKey != this.lastTradeSessionDay)
            {
                this.lastTradeSessionDay = dayKey;
                this.tradesThisSession = 0;
                this.tradesLimitHit = false;
            }
        }

        private bool IsInRth()
        {
            var est = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            return est.Hour >= this.RthStartHour && est.Hour < this.RthEndHour;
        }

        // ==== tick feed: DirectionEngine, VWAP, tape absorption ==============================

        private void OnLast(Symbol symbol, Last last)
        {
            if (last is null || this.direction is null) return;

            this.lastPrice = last.Price;

            var tick = new TickEvent(
                last.Time, last.Price, last.Size,
                last.AggressorFlag switch
                {
                    AggressorFlag.Buy => OrbAggressor.Buy,
                    AggressorFlag.Sell => OrbAggressor.Sell,
                    _ => OrbAggressor.Unknown,
                },
                symbol.Bid, symbol.Ask);

            this.direction.OnTick(tick);
            this.vwapSession.Add(tick.Price, tick.Size);

            this.formingBarTicks.Add((tick.Price, tick.Size, tick.SignedSize));

            this.FeedTapeAbsorption(last);
            this.ResolveDisplacementWatch();
        }

        private void FeedTapeAbsorption(Last last)
        {
            var snapshot = new TradeSnapshot
            {
                Time = last.Time,
                FirstPrice = (decimal)last.Price,
                LastPrice = (decimal)last.Price,
                Volume = (decimal)last.Size,
                Direction = last.AggressorFlag switch
                {
                    AggressorFlag.Buy => OceansAnchor.Aggressor.Buy,
                    AggressorFlag.Sell => OceansAnchor.Aggressor.Sell,
                    _ => OceansAnchor.Aggressor.Between,
                },
            };

            var tickSize = (decimal)(this.CurrentSymbol?.TickSize ?? 0d);
            if (tickSize <= 0m) return;

            this.displacementWatch.NotePrint(snapshot.LastPrice);

            foreach (var (zone, side) in this.ArmedZones())
            {
                if (zone.Side != side) continue;
                if (!TapeAbsorption.Qualifies(snapshot, zone, tickSize, this.absorptionRules)) continue;

                var evt = new AbsorptionEvent
                {
                    Time = snapshot.Time,
                    Price = snapshot.LastPrice,
                    Volume = snapshot.Volume,
                    Direction = snapshot.Direction,
                    Path = EventPath.Tape,
                    Bar = this.barCounter + 1,
                };

                this.displacementWatch.Add(evt, zone, this.absorptionRules);
                this.Log(
                    $"[Absorption] tape print qualified: {side} {snapshot.Volume} @ {snapshot.LastPrice} — "
                    + $"watching for displacement over {this.DisplacementWindowMs}ms",
                    StrategyLoggingLevel.Trading);
            }
        }

        private void ResolveDisplacementWatch()
        {
            var tickSize = (decimal)(this.CurrentSymbol?.TickSize ?? 0d);
            if (tickSize <= 0m || this.displacementWatch.Count == 0) return;

            foreach (var pair in this.displacementWatch.Resolve(Core.TimeUtils.DateTimeUtcNow, tickSize, this.absorptionRules))
            {
                var evt = pair.Key;
                var zone = pair.Value;

                zone.NoteCluster(evt.Price, evt.Price);

                var promoted = (zone.Side == TestSide.SupportLong ? this.longSignal : this.shortSignal)
                    .Promote(zone, evt, evt.Bar);

                if (promoted)
                {
                    this.Log(
                        $"[Absorption] TAPE CONFIRMED — {zone.Side} zone at {zone.Poc} triggered "
                        + $"(displacement {evt.DisplacementTicks}t <= {this.MaxDisplacementTicks}t).",
                        StrategyLoggingLevel.Trading);
                }
            }
        }

        private IEnumerable<(AnchorZone Zone, TestSide Side)> ArmedZones()
        {
            if (this.longZone is { State: SignalState.Armed })
                yield return (this.longZone, TestSide.SupportLong);
            if (this.shortZone is { State: SignalState.Armed })
                yield return (this.shortZone, TestSide.ResistanceShort);
        }

        // ==== daily reference levels, refreshed once per new daily bar =======================

        private void DailyHdm_OnNewHistoryItem(object sender, HistoryEventArgs e)
        {
            this.RefreshDailyLevels();
        }

        private void RefreshDailyLevels()
        {
            if (this.dailyHdm is null) return;

            var count = this.dailyHdm.Count;
            if (count == this.lastDailyBarCount) return;
            this.lastDailyBarCount = count;

            this.dailyBarsCache.Clear();

            for (var i = 0; i < count; i++)
            {
                if (this.dailyHdm[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                    continue;

                var date = DateOnly.FromDateTime(bar.TimeLeft);
                this.dailyBarsCache.Add(new DailySessionBar(date, bar.High, bar.Low, bar.Close));
            }

            if (this.dailyBarsCache.Count == 0) return;

            var asOf = DateOnly.FromDateTime(Core.TimeUtils.DateTimeUtcNow);
            this.referenceLevels = ReferenceLevels.FromDailyBars(this.dailyBarsCache, asOf);

            var priorRanges = this.dailyBarsCache
                .Where(b => b.Date < asOf && b.High >= b.Low)
                .OrderByDescending(b => b.Date)
                .Take(20)
                .Select(b => b.High - b.Low)
                .ToList();

            this.averageDailyRange = priorRanges.Count > 0 ? priorRanges.Average() : double.NaN;
        }

        // ==== bar-close: feed engines, manage zones, evaluate entries =========================

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs e)
        {
            this.CheckSessionReset();
            if (this.hdm is null || this.CurrentSymbol is null) return;

            double o = HistoricalDataExtensions.Open(this.hdm, 1);
            double h = HistoricalDataExtensions.High(this.hdm, 1);
            double l = HistoricalDataExtensions.Low(this.hdm, 1);
            double c = HistoricalDataExtensions.Close(this.hdm, 1);
            double v = HistoricalDataExtensions.Volume(this.hdm, 1);
            DateTime barTime = HistoricalDataExtensions.Time(this.hdm, 1);
            double tickSize = this.CurrentSymbol.TickSize;

            this.barCounter++;

            if (double.IsNaN(this.sessionOpenPrice))
                this.sessionOpenPrice = o;

            // 1. Bar-close engines, THIS closed bar only.
            this.hhll.Feed(h, l, c);
            this.vwapSession.SampleBar(barTime);

            // 2. Per-bar delta + extreme-band volume/delta, from ticks buffered during the bar
            //    that just closed (never from a later tick — the buffer is cleared right after).
            var barDelta = this.formingBarTicks.Sum(t => t.SignedSize);
            var extremeBand = this.ClusterExtremeTicks * tickSize;

            double lowVolume = 0, lowDelta = 0, highVolume = 0, highDelta = 0;
            foreach (var t in this.formingBarTicks)
            {
                if (Math.Abs(t.Price - l) <= extremeBand) { lowVolume += t.Size; lowDelta += t.SignedSize; }
                if (Math.Abs(t.Price - h) <= extremeBand) { highVolume += t.Size; highDelta += t.SignedSize; }
            }

            this.formingBarTicks.Clear();

            var facts = new BarFacts
            {
                Open = (decimal)o, High = (decimal)h, Low = (decimal)l, Close = (decimal)c,
                Volume = (decimal)v, Delta = (decimal)barDelta,
            };

            this.recentDeltasLong.Add((double)facts.Delta);
            if (this.recentDeltasLong.Count > this.ClusterDeltaLookback) this.recentDeltasLong.RemoveAt(0);
            this.recentDeltasShort.Add((double)facts.Delta);
            if (this.recentDeltasShort.Count > this.ClusterDeltaLookback) this.recentDeltasShort.RemoveAt(0);

            this.recentBarsForOtf.Add(facts);
            if (this.recentBarsForOtf.Count > Math.Max(2, this.ZoneOtfBars + 1)) this.recentBarsForOtf.RemoveAt(0);

            this.longSignal.NoteBarDelta(facts.Delta);
            this.shortSignal.NoteBarDelta(facts.Delta);

            // 3. Refresh the zone SOURCE: the current nearest open HH/LL segment per side. A
            //    fresh Zone object only when the underlying segment actually changed — otherwise
            //    the SAME Zone carries its State/TraversalCount forward, which is the whole
            //    point of a state machine.
            this.RefreshZone(isResistance: false, ref this.longZone, ref this.longZoneFromSegmentBar, tickSize);
            this.RefreshZone(isResistance: true, ref this.shortZone, ref this.shortZoneFromSegmentBar, tickSize);

            var otf = this.ZoneOtfBars > 0 ? SignalGate.OneTimeframing(this.recentBarsForOtf, this.ZoneOtfBars) : 0;

            this.AdvanceZone(this.longZone, TestSide.SupportLong, this.longSignal, facts, this.recentDeltasLong, tickSize, otf, c);
            this.AdvanceZone(this.shortZone, TestSide.ResistanceShort, this.shortSignal, facts, this.recentDeltasShort, tickSize, otf, c);

            // 4. Direction callout, computed from THIS closed bar's own close.
            var vwapKnown = this.vwapSession.TryCurrent(out var vwap, out _);
            var panel = this.direction.Panel(c, vwapKnown ? vwap : double.NaN, tickSize, this.averageDailyRange);

            this.lastCalloutSide = panel.Callout.Kind switch
            {
                DirectionCalloutKind.ScalpLong or DirectionCalloutKind.HoldLong => 1,
                DirectionCalloutKind.ScalpShort or DirectionCalloutKind.HoldShort => -1,
                _ => 0,
            };
        }

        private void RefreshZone(bool isResistance, ref AnchorZone zone, ref int fromSegmentBar, double tickSize)
        {
            var segment = this.hhll.Segments.LastOrDefault(s => s.IsResistance == isResistance);
            if (segment is null) return;

            if (zone != null && segment.StartBar == fromSegmentBar) return;

            fromSegmentBar = segment.StartBar;

            var buffer = this.ZoneBufferTicks * tickSize;
            var tooFar = this.ZoneMaxDistanceAdr > 0 && !double.IsNaN(this.averageDailyRange)
                && !double.IsNaN(this.sessionOpenPrice) && this.averageDailyRange > 0
                && Math.Abs(segment.Price - this.sessionOpenPrice) > this.ZoneMaxDistanceAdr * this.averageDailyRange;

            zone = new AnchorZone
            {
                Bottom = (decimal)(segment.Price - buffer),
                Top = (decimal)(segment.Price + buffer),
                Poc = (decimal)segment.Price,
                Kind = AnchorZoneKind.CompositeHvn, // stand-in label — built from HH/LL structure, not a volume profile
                Rank = 1,
                Naked = true,
                TooFar = tooFar,
                StartBar = segment.StartBar,
                BornSession = Core.TimeUtils.DateTimeUtcNow,
            };

            this.Log(
                $"[Zone] new {(isResistance ? "resistance" : "support")} zone at {segment.Price:F2} "
                + $"(from HH/LL bar {segment.StartBar}){(tooFar ? " — TOO FAR from session open, will not arm" : string.Empty)}",
                StrategyLoggingLevel.Trading);
        }

        private void AdvanceZone(
            AnchorZone zone, TestSide side, SignalEngine engine, BarFacts facts,
            List<double> recentDeltas, double tickSize, int otf, double lastClose)
        {
            if (zone is null) return;

            var priorDeltas = recentDeltas.Select(d => (decimal)d).ToList();
            var before = zone.State;

            var transition = engine.Advance(zone, this.barCounter, facts, Core.TimeUtils.DateTimeUtcNow, (decimal)tickSize, otf);

            // Bar-shape (cluster) absorption test — the reconstruction path, tried whenever the
            // zone is Armed and the tape path has not already triggered it this bar.
            if (zone.State == SignalState.Armed
                && ClusterAbsorption.Touches(facts, zone, side, (decimal)tickSize, this.absorptionRules))
            {
                var score = ClusterAbsorption.Score(facts, priorDeltas, side, zone, (decimal)tickSize, this.clusterRules);

                if (score.Total >= this.clusterRules.MinScore)
                {
                    zone.NoteCluster(facts.Low, facts.High);

                    var evt = new AbsorptionEvent
                    {
                        Time = Core.TimeUtils.DateTimeUtcNow,
                        Price = zone.Poc,
                        Volume = facts.Volume,
                        Direction = side == TestSide.SupportLong ? OceansAnchor.Aggressor.Sell : OceansAnchor.Aggressor.Buy,
                        Path = EventPath.Cluster,
                        Bar = this.barCounter,
                    };

                    if (engine.Promote(zone, evt, this.barCounter))
                    {
                        this.Log(
                            $"[Absorption] CLUSTER CONFIRMED — {side} zone at {zone.Poc} triggered "
                            + $"({score.Total}/4 tests: delta={score.DeltaOutlier} close={score.ClosesBackInside} "
                            + $"extreme={score.ExtremeConcentration} wick={score.Wick}).",
                            StrategyLoggingLevel.Trading);
                    }
                }
                else if (score.Total > 0)
                {
                    this.Log(
                        $"[Absorption] {side} zone touched but only {score.Total}/{this.clusterRules.MinScore} "
                        + "tests passed — not promoted.",
                        StrategyLoggingLevel.Trading);
                }
            }

            if (before != SignalState.Confirmed && transition.To == SignalState.Confirmed)
                this.OnZoneConfirmed(zone, side, lastClose, tickSize);
        }

        // ==== entry: fires only on a zone's Confirmed transition, gated on DirectionCallout ===

        private void OnZoneConfirmed(AnchorZone zone, TestSide side, double price, double tickSize)
        {
            var wantSide = side == TestSide.SupportLong ? 1 : -1;

            if (this.lastCalloutSide != wantSide)
            {
                this.Log(
                    $"[Signal] {side} zone at {zone.Poc} CONFIRMED but DirectionCallout disagrees "
                    + $"(callout side={this.lastCalloutSide}) — skipped.",
                    StrategyLoggingLevel.Trading);
                return;
            }

            this.TryEnter(wantSide, zone, price, tickSize);
        }

        private void TryEnter(int side, AnchorZone zone, double price, double tickSize)
        {
            if (this.waitOpenPosition || this.waitClosePositions) return;
            if (this.dailyLimitHit || this.drawdownLimitHit || this.tradesLimitHit) return;
            if (this.MyPositions().Any()) return;
            if (this.RthOnly == 1 && !this.IsInRth()) return;
            if (this.barCounter - this.lastEntryBarIndex < this.MinBarsBetweenEntries) return;

            if (!this.TryComputeTarget(side, price, tickSize, out var targetPrice, out var targetSource))
            {
                targetPrice = price + (side * this.FallbackTargetTicks * tickSize);
                targetSource = "FALLBACK";
            }

            if (!this.TryComputeStop(side, zone, price, tickSize, out var stopPrice, out var stopSource))
            {
                this.Log("[Signal] no usable stop reference — skipped.", StrategyLoggingLevel.Trading);
                return;
            }

            this.Log(
                $"[Signal] {(side == 1 ? "LONG" : "SHORT")} @ {price:F2} — "
                + $"target {targetPrice:F2} ({targetSource}, {Math.Abs(targetPrice - price) / tickSize:F0}t), "
                + $"stop {stopPrice:F2} ({stopSource}, {Math.Abs(price - stopPrice) / tickSize:F0}t), "
                + $"zone={zone.Poc:F2}",
                StrategyLoggingLevel.Trading);

            this.lastEntryBarIndex = this.barCounter;

            if (this.DryRun)
            {
                this.Log("[Order] DryRun — no order placed.", StrategyLoggingLevel.Trading);
                return;
            }

            this.waitOpenPosition = true;

            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                OrderTypeId = this.orderTypeId,
                Quantity = this.Quantity,
                Side = side == 1 ? Side.Buy : Side.Sell,
                Comment = StrategyTag,
                StopLoss = SlTpHolder.CreateSL(stopPrice, PriceMeasurement.Absolute),
                TakeProfit = SlTpHolder.CreateTP(targetPrice, PriceMeasurement.Absolute),
            });

            if (result.Status == TradingOperationResultStatus.Failure)
            {
                this.Log($"[Order] failed: {result.Message}", StrategyLoggingLevel.Error);
                this.waitOpenPosition = false;
            }
            else
            {
                this.tradesThisSession++;
                if (this.MaxTradesPerSession > 0 && this.tradesThisSession >= this.MaxTradesPerSession)
                {
                    this.tradesLimitHit = true;
                    this.Log($"[Risk] max trades per session ({this.MaxTradesPerSession}) reached.", StrategyLoggingLevel.Trading);
                }
            }
        }

        // ==== target: nearest qualifying level ahead of price in the trade's direction =======

        private static bool IsAhead(double candidate, double price, int side) =>
            side == 1 ? candidate > price : candidate < price;

        private bool TryComputeTarget(int side, double price, double tickSize, out double targetPrice, out string source)
        {
            var candidates = new List<(double Price, string Label)>();

            if (this.TargetUseHhLl)
            {
                var opposing = this.hhll.Segments.LastOrDefault(s => s.IsResistance == (side == 1));
                if (opposing != null && IsAhead(opposing.Price, price, side))
                    candidates.Add((opposing.Price, side == 1 ? "resistance" : "support"));
            }

            if (this.TargetUseVwap && this.vwapSession.TryCurrent(out var vwap, out _) && IsAhead(vwap, price, side))
                candidates.Add((vwap, "VWAP"));

            foreach (var level in this.referenceLevels)
            {
                var relevantDay = side == 1
                    ? level.Kind is LevelKind.PriorDayHigh or LevelKind.PriorDayClose
                    : level.Kind is LevelKind.PriorDayLow or LevelKind.PriorDayClose;
                var relevantWeek = side == 1 ? level.Kind == LevelKind.PriorWeekHigh : level.Kind == LevelKind.PriorWeekLow;

                if (((this.TargetUsePriorDay && relevantDay) || (this.TargetUsePriorWeek && relevantWeek))
                    && IsAhead(level.Price, price, side))
                {
                    candidates.Add((level.Price, level.Label));
                }
            }

            var minDistance = this.MinTargetDistanceTicks * tickSize;
            var qualified = candidates.Where(c => Math.Abs(c.Price - price) >= minDistance).ToList();

            if (qualified.Count == 0) { targetPrice = default; source = null; return false; }

            var nearest = qualified.OrderBy(c => Math.Abs(c.Price - price)).First();
            targetPrice = nearest.Price;
            source = nearest.Label;
            return true;
        }

        // ==== stop: the confirming absorption print's own boundary, clamped =================

        private bool TryComputeStop(int side, AnchorZone zone, double price, double tickSize, out double stopPrice, out string source)
        {
            var minDist = this.MinStopTicks * tickSize;
            var maxDist = this.MaxStopTicks * tickSize;

            double structural;
            if (zone.HasCluster)
            {
                structural = side == 1 ? (double)zone.ClusterLow : (double)zone.ClusterHigh;
            }
            else
            {
                // Should not normally happen — a Confirmed zone was promoted via an absorption
                // event, which always calls NoteCluster first. Falls back to the zone price
                // itself if it ever does.
                structural = (double)zone.Poc;
            }

            var distance = Math.Abs(price - structural);

            if (distance < minDist)
            {
                stopPrice = price - (side * minDist);
                source = "floor (structural stop too tight)";
            }
            else if (distance > maxDist)
            {
                stopPrice = price - (side * maxDist);
                source = "cap (structural stop too wide)";
            }
            else
            {
                stopPrice = structural;
                source = "absorption zone boundary";
            }

            return true;
        }

        // ==== tick-level: daily loss / drawdown checks, mirroring srChannelBreakStrategy ======

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            this.CheckSessionReset();
            if (this.waitOpenPosition || this.waitClosePositions) return;

            var positions = this.MyPositions();
            if (!positions.Any()) return;

            double unrealizedPnlTicks = positions.Sum(x => x.GrossPnLTicks);
            double tickValue = this.CurrentSymbol.TickSize > 0 ? this.CurrentSymbol.GetTickCost(1) : 0;
            double unrealizedPnl = unrealizedPnlTicks * tickValue;

            if (this.MaxDailyLoss > 0 && !this.dailyLimitHit)
            {
                double totalDailyPnl = this.dailyPnl + unrealizedPnl;
                if (totalDailyPnl <= -this.MaxDailyLoss)
                {
                    this.dailyLimitHit = true;
                    this.waitClosePositions = true;
                    this.Log($"[Risk] DAILY LOSS LIMIT HIT — ${totalDailyPnl:F2} <= -${this.MaxDailyLoss}. Closing all.", StrategyLoggingLevel.Trading);
                    foreach (var pos in positions) pos.Close();
                    return;
                }
            }

            if (this.MaxDrawdown > 0 && !this.drawdownLimitHit)
            {
                double currentEquity = this.totalRealizedPnl + unrealizedPnl;
                if (currentEquity > this.peakEquity) this.peakEquity = currentEquity;
                double drawdown = this.peakEquity - currentEquity;

                if (drawdown >= this.MaxDrawdown)
                {
                    this.drawdownLimitHit = true;
                    this.waitClosePositions = true;
                    this.Log($"[Risk] MAX DRAWDOWN HIT — ${drawdown:F2} >= ${this.MaxDrawdown}. Closing all.", StrategyLoggingLevel.Trading);
                    foreach (var pos in positions) pos.Close();
                }
            }
        }
    }
}
