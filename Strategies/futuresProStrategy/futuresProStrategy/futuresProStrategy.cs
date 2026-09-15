using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace futuresProStrategy
{
    /// <summary>
    /// Futures Pro Strategy — EMA9 + VWAP crossover with Keltner Channel filter
    ///
    /// Trading logic based on TradingView indicators:
    ///   1. EMA9 (9-period EMA) — trend direction and momentum
    ///   2. VWAP — intraday anchor and dynamic support/resistance
    ///   3. Keltner Channel (34 EMA base, ATR 88, 1.5x/3.5x multipliers) — filter
    ///
    /// Entry (bar close, ALL conditions must pass):
    ///   LONG  — Close above EMA9 AND above VWAP (new high above both)
    ///           AND price within Keltner Channel (not overextended)
    ///   SHORT — Close below EMA9 AND below VWAP (new low below both)
    ///           AND price within Keltner Channel (not overextended)
    ///
    /// Exit:
    ///   - Hard Stop Loss (bracket)
    ///   - Take Profit (bracket, optional)
    ///   - Code-managed trailing stop
    ///   - Reverse cross: close and flip when EMA9/VWAP cross reverses
    ///   - Daily loss cutoff
    /// </summary>
    public sealed class FuturesProStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Instrument ────────────────────────────────────────────────────────
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        // ── EMA9 + VWAP settings (from vwap.pine) ────────────────────────────
        [InputParameter("EMA Length", 2, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int EmaLength { get; set; }

        // ── Keltner Channel settings (from keltnerChannel.pine) ──────────────
        [InputParameter("KC MA Length", 3, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int KcMaLength { get; set; }

        [InputParameter("KC ATR Length", 4, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int KcAtrLength { get; set; }

        [InputParameter("KC ATR Multiplier Min", 5, minimum: 0, maximum: 10, increment: 0.1, decimalPlaces: 1)]
        public double KcAtrMultMin { get; set; }

        [InputParameter("KC ATR Multiplier Max", 6, minimum: 0, maximum: 10, increment: 0.1, decimalPlaces: 1)]
        public double KcAtrMultMax { get; set; }

        // ── Keltner Channel filter: only trade when price is within the channel ─
        // 0 = no KC filter; 1 = only trade when price within KC top/bottom bands
        [InputParameter("Use KC Filter (0=off, 1=on)", 7, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int UseKcFilter { get; set; }

        // ── Chart / history ───────────────────────────────────────────────────
        [InputParameter("Period", 12)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 13)]
        public DateTime StartPoint { get; set; }

        // ── Trade settings ────────────────────────────────────────────────────
        [InputParameter("Quantity", 14)]
        public int Quantity { get; set; }

        [InputParameter("Stop Loss (ticks)", 15, minimum: 1, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int StopLossTicks { get; set; }

        [InputParameter("Take Profit (ticks, 0=off)", 16, minimum: 0, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int TakeProfitTicks { get; set; }

        // ── Trailing stop ─────────────────────────────────────────────────────
        [InputParameter("Trail Activate At (ticks profit, 0=off)", 17, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0=off)", 18, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailingStopTicks { get; set; }

        // ── Session filter ────────────────────────────────────────────────────
        // 0 = trade 24h; 1 = new entries only during RTH window
        [InputParameter("RTH Only (0=24h, 1=RTH only)", 19, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int RthOnly { get; set; }

        [InputParameter("RTH Start Hour (EST, 9=9AM)", 20, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthStartHour { get; set; }

        [InputParameter("RTH End Hour (EST, 16=4PM)", 21, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthEndHour { get; set; }

        // ── Daily risk ────────────────────────────────────────────────────────
        // Maximum loss in dollars per trading day (resets at 6 PM EST). 0 = disabled.
        [InputParameter("Max Daily Loss ($, 0=off)", 22, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDailyLoss { get; set; }
        // Maximum total drawdown in dollars before strategy shuts down completely.
        // For prop firms: typically $2000. 0 = disabled.
        [InputParameter("Max Drawdown ($, 0=off)", 23, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDrawdown { get; set; }

        // ── Intrabar cross debounce ──────────────────────────────────────────
        // Require EMA/VWAP spread to be at least this many ticks before treating
        // relation changes as tradable crosses. 0 = no debounce.
        [InputParameter("Cross Min Gap (ticks, 0=off)", 8, minimum: 0, maximum: 20, increment: 1, decimalPlaces: 0)]
        public int CrossMinGapTicks { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        // Indicators
        private Indicator ema9;
        private Indicator vwap;
        private Indicator kcMid;
        private Indicator kcAtr;

        private HistoricalData hdm;
        private string orderTypeId;

        private int longPositionsCount;
        private int shortPositionsCount;

        private bool waitOpenPosition;
        private bool waitClosePositions;
        private bool inPosition;

        // Queued direction for reverse-cross flip
        private Side? pendingEntrySide;

        // Smart trailing state
        private bool   trailingActivated;
        private double bestPrice;
        private Side   currentSide;

        // Intrabar EMA/VWAP relation cache: 1=both above, -1=both below, 0=other
        private int lastEmaVwapRelation;
        private DateTime lastCrossActionBarTime;

        // Daily P&L tracking (in currency)
        private double dailyPnl;
        private int    lastResetDay; // day-of-year of last daily reset
        private bool   dailyLimitHit;

        // Total drawdown tracking
        private double totalRealizedPnl;  // cumulative realized P&L since strategy start
        private double peakEquity;        // highest total equity seen (realized + unrealized)
        private bool   drawdownLimitHit;  // true once max drawdown is breached

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public FuturesProStrategy() : base()
        {
            this.Name        = "Futures Pro Strategy";
            this.Description = "EMA9 + VWAP crossover strategy with Keltner Channel filter. " +
                               "Based on TradingView vwap.pine and keltnerChannel.pine.";

            // EMA9 + VWAP defaults (from vwap.pine)
            this.EmaLength = 9;

            // Keltner Channel defaults (from keltnerChannel.pine)
            this.KcMaLength    = 34;
            this.KcAtrLength   = 88;
            this.KcAtrMultMin  = 1.5;
            this.KcAtrMultMax  = 3.5;
            this.UseKcFilter   = 1; // on by default

            this.Period    = Period.MIN5;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity  = 1;

            this.StopLossTicks        = 80;   // 80 ticks = 20 pts on MES
            this.TakeProfitTicks      = 0;    // disabled, let trailing handle it
            this.TrailActivationTicks = 40;   // 10 pts on MES
            this.TrailingStopTicks    = 20;   // 5 pts on MES

            // RTH filter: 9 AM – 4 PM EST
            this.RthOnly     = 0;
            this.RthStartHour = 9;
            this.RthEndHour   = 16;

            this.MaxDailyLoss = 0; // disabled by default
            this.MaxDrawdown  = 2000; // $2000 default for prop firm compliance

            this.CrossMinGapTicks = 1;
        }

        protected override void OnRun()
        {
            this.totalNetPl         = 0;
            this.totalGrossPl       = 0;
            this.totalFee           = 0;
            this.inPosition         = false;
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
            this.pendingEntrySide   = null;
            this.trailingActivated  = false;
            this.bestPrice          = 0;
            this.lastEmaVwapRelation = 0;
            this.lastCrossActionBarTime = DateTime.MinValue;
            this.dailyPnl           = 0;
            this.lastResetDay       = -1;
            this.dailyLimitHit      = false;
            this.totalRealizedPnl   = 0;
            this.peakEquity         = 0;
            this.drawdownLimitHit   = false;

            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());

            if (this.CurrentSymbol == null)
            {
                this.Log("Symbol not specified.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());

            if (this.CurrentAccount == null)
            {
                this.Log("Account not specified.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
            {
                this.Log("Symbol and Account are from different connections.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.EmaLength < 1)
            {
                this.Log($"EMA Length ({this.EmaLength}) must be >= 1.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.KcMaLength < 1)
            {
                this.Log($"KC MA Length ({this.KcMaLength}) must be >= 1.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.KcAtrLength < 1)
            {
                this.Log($"KC ATR Length ({this.KcAtrLength}) must be >= 1.", StrategyLoggingLevel.Error);
                return;
            }

            this.orderTypeId = Core.OrderTypes
                .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId
                                  && x.Behavior == OrderTypeBehavior.Market)?.Id;

            if (string.IsNullOrEmpty(this.orderTypeId))
            {
                this.Log("Connection does not support market orders.", StrategyLoggingLevel.Error);
                return;
            }

            // Create indicators: EMA9, VWAP, Keltner Channel (34 EMA base, ATR 88)
            this.ema9   = Core.Instance.Indicators.BuiltIn.EMA(this.EmaLength, PriceType.Close);
            this.vwap   = Core.Instance.Indicators.BuiltIn.VWAP();
            this.kcMid  = Core.Instance.Indicators.BuiltIn.EMA(this.KcMaLength, PriceType.Close);
            this.kcAtr  = Core.Instance.Indicators.BuiltIn.ATR(this.KcAtrLength);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);
            this.hdm.AddIndicator(this.ema9);
            this.hdm.AddIndicator(this.vwap);
            this.hdm.AddIndicator(this.kcMid);
            this.hdm.AddIndicator(this.kcAtr);

            this.lastEmaVwapRelation = this.GetEmaVwapRelation();

            Core.PositionAdded      += this.Core_PositionAdded;
            Core.PositionRemoved    += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded         += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem     += this.Hdm_OnNewHistoryItem;

            this.Log($"Started — EMA:{EmaLength}  VWAP:on  " +
                     $"KC:{KcMaLength}/{KcAtrLength} ({KcAtrMultMin}x/{KcAtrMultMax}x)  " +
                     $"KCFilter:{(UseKcFilter == 1 ? "on" : "off")}  " +
                     $"CrossGap:{CrossMinGapTicks}t  " +
                     $"SL:{StopLossTicks}t  TP:{(TakeProfitTicks > 0 ? $"{TakeProfitTicks}t" : "off")}  " +
                     $"Trail:{(TrailingStopTicks > 0 && TrailActivationTicks > 0 ? $"{TrailActivationTicks}t/{TrailingStopTicks}t" : "off")}  " +
                     $"RTH:{(RthOnly == 1 ? $"{RthStartHour}-{RthEndHour}" : "24h")}",
                     StrategyLoggingLevel.Trading);
        }

        protected override void OnStop()
        {
            Core.PositionAdded      -= this.Core_PositionAdded;
            Core.PositionRemoved    -= this.Core_PositionRemoved;
            Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
            Core.TradeAdded         -= this.Core_TradeAdded;

            if (this.hdm != null)
            {
                this.hdm.HistoryItemUpdated -= this.Hdm_HistoryItemUpdated;
                this.hdm.NewHistoryItem     -= this.Hdm_OnNewHistoryItem;
                this.hdm.Dispose();
            }

            base.OnStop();
        }

        protected override void OnInitializeMetrics(Meter meter)
        {
            base.OnInitializeMetrics(meter);
            meter.CreateObservableCounter("total-long-positions",  () => this.longPositionsCount,  description: "Total long positions");
            meter.CreateObservableCounter("total-short-positions", () => this.shortPositionsCount, description: "Total short positions");
            meter.CreateObservableCounter("total-pl-net",          () => this.totalNetPl,           description: "Total Net P&L");
            meter.CreateObservableCounter("total-pl-gross",        () => this.totalGrossPl,         description: "Total Gross P&L");
            meter.CreateObservableCounter("total-fee",             () => this.totalFee,             description: "Total Fees");
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            this.longPositionsCount  = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double netQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);
            if (Math.Abs(netQty) == this.Quantity)
                this.waitOpenPosition = false;
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            this.longPositionsCount  = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (!positions.Any())
            {
                this.waitClosePositions = false;
                this.inPosition         = false;

                // Update peak equity when flat (all unrealized is now realized)
                if (this.totalRealizedPnl > this.peakEquity)
                    this.peakEquity = this.totalRealizedPnl;

                // Cancel any leftover bracket orders
                var orders = Core.Instance.Orders
                    .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                    .ToArray();

                foreach (var order in orders)
                {
                    var r = order.Cancel();
                    if (r.Status == TradingOperationResultStatus.Success)
                        this.Log($"Cancelled leftover order: {order.OrderTypeId}", StrategyLoggingLevel.Trading);
                    else
                        this.Log($"Failed to cancel order: {r.Message}", StrategyLoggingLevel.Error);
                }

                // Reset trailing state
                this.trailingActivated = false;
                this.bestPrice         = 0;

                // Reverse-cross flip: open queued direction immediately after flat.
                if (this.pendingEntrySide.HasValue)
                {
                    var side = this.pendingEntrySide.Value;
                    this.pendingEntrySide = null;

                    if (!this.dailyLimitHit && !this.drawdownLimitHit)
                    {
                        this.PlaceEntry(side);
                    }
                    else
                    {
                        this.Log($"Reverse flip to {side} blocked — " +
                                 (this.drawdownLimitHit ? "max drawdown limit hit" : "daily loss limit hit"),
                                 StrategyLoggingLevel.Trading);
                    }
                }
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol  != this.CurrentSymbol)  return;
            if (obj.Account != this.CurrentAccount) return;

            if (obj.Status == OrderStatus.Refused)
                this.ProcessTradingRefuse();
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.Symbol  != this.CurrentSymbol)  return;
            if (obj.Account != this.CurrentAccount) return;

            if (obj.NetPnl   != null) this.totalNetPl   += obj.NetPnl.Value;
            if (obj.GrossPnl != null) this.totalGrossPl += obj.GrossPnl.Value;
            if (obj.Fee      != null) this.totalFee      += obj.Fee.Value;

            // Track daily P&L in currency for the daily loss cutoff
            if (obj.GrossPnl != null)
            {
                this.dailyPnl += obj.GrossPnl.Value;
                this.totalRealizedPnl += obj.GrossPnl.Value;
            }
        }

        // Fires every price tick — manages intrabar EMA cross entries/flips, risk and trailing.
        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            this.CheckDailyReset();

            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            if (this.dailyLimitHit || this.drawdownLimitHit)
                return;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            // ── Real-time drawdown + daily loss monitoring (includes unrealized P&L) ──
            if (positions.Any())
            {
                double unrealizedPnlTicks = positions.Sum(x => x.GrossPnLTicks);
                double tickValue = this.CurrentSymbol.TickSize > 0 ? this.CurrentSymbol.GetTickCost(1) : 0;
                // GrossPnLTicks already represents position-level ticks in Quantower.
                double unrealizedPnl = unrealizedPnlTicks * tickValue;

                // Daily loss check including unrealized
                if (this.MaxDailyLoss > 0 && !this.dailyLimitHit)
                {
                    double totalDailyPnl = this.dailyPnl + unrealizedPnl;
                    if (totalDailyPnl <= -this.MaxDailyLoss)
                    {
                        this.dailyLimitHit = true;
                        this.waitClosePositions = true;
                        this.Log($"DAILY LOSS LIMIT HIT (real-time) — realized: ${this.dailyPnl:F2} + unrealized: ${unrealizedPnl:F2} = ${totalDailyPnl:F2} >= -${this.MaxDailyLoss} cutoff. CLOSING ALL.",
                                 StrategyLoggingLevel.Trading);
                        foreach (var pos in positions)
                            pos.Close();
                        return;
                    }
                }

                // Max drawdown check (total equity from strategy start)
                if (this.MaxDrawdown > 0 && !this.drawdownLimitHit)
                {
                    double currentEquity = this.totalRealizedPnl + unrealizedPnl;
                    if (currentEquity > this.peakEquity)
                        this.peakEquity = currentEquity;

                    double drawdown = this.peakEquity - currentEquity;
                    if (drawdown >= this.MaxDrawdown)
                    {
                        this.drawdownLimitHit = true;
                        this.waitClosePositions = true;
                        this.Log($"MAX DRAWDOWN LIMIT HIT — peak: ${this.peakEquity:F2}  current: ${currentEquity:F2}  drawdown: ${drawdown:F2} >= ${this.MaxDrawdown}. CLOSING ALL.",
                                 StrategyLoggingLevel.Trading);
                        foreach (var pos in positions)
                            pos.Close();
                        return;
                    }
                }
            }

            this.ProcessIntrabarEmaCross(positions);

            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // ── Trailing stop logic ──
            if (this.TrailActivationTicks <= 0 || this.TrailingStopTicks <= 0)
                return;

            if (!positions.Any())
                return;

            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);
            double pnlTicks     = positions.Sum(x => x.GrossPnLTicks);

            // Step 1: activate once profit threshold is reached
            if (!this.trailingActivated && pnlTicks >= this.TrailActivationTicks)
            {
                this.trailingActivated = true;
                this.bestPrice         = currentPrice;
                this.Log($"Trail activated at {pnlTicks:F1}t profit. Best: {currentPrice:F4}",
                         StrategyLoggingLevel.Trading);
            }

            if (!this.trailingActivated)
                return;

            // Step 2: update best price
            if (this.currentSide == Side.Buy)
                this.bestPrice = Math.Max(this.bestPrice, currentPrice);
            else
                this.bestPrice = Math.Min(this.bestPrice, currentPrice);

            // Step 3: check trail breach
            double tickSize  = this.CurrentSymbol.TickSize;
            double trailDist = this.TrailingStopTicks * tickSize;

            bool trailHit = this.currentSide == Side.Buy
                ? currentPrice <= this.bestPrice - trailDist
                : currentPrice >= this.bestPrice + trailDist;

            if (trailHit)
            {
                this.Log($"Trail stop hit — best:{this.bestPrice:F4}  current:{currentPrice:F4}  dist:{this.TrailingStopTicks}t",
                         StrategyLoggingLevel.Trading);
                this.waitClosePositions = true;
                foreach (var pos in positions)
                {
                    var r = pos.Close();
                    if (r.Status == TradingOperationResultStatus.Failure)
                    {
                        this.Log($"Trail close failed: {r.Message}", StrategyLoggingLevel.Error);
                        this.ProcessTradingRefuse();
                    }
                }
            }
        }

        // Fires when a bar closes — all signals evaluated here
        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.OnBarClose();
        }

        private void OnBarClose()
        {
            // Intentionally unused for signal generation.
            // Strategy entries and exits are managed intrabar in Hdm_HistoryItemUpdated.
        }

        private int GetEmaVwapRelation()
        {
            double ema9_0 = this.ema9.GetValue(0);
            double vwap0  = this.vwap.GetValue(0);
            double close0 = HistoricalDataExtensions.Close(this.hdm, 0);

            // From vwap.pine: aboveBoth = close > ema9 and close > vwapValue
            //                 belowBoth = close < ema9 and close < vwapValue
            bool aboveBoth = close0 > ema9_0 && close0 > vwap0;
            bool belowBoth = close0 < ema9_0 && close0 < vwap0;

            // Apply cross min gap debounce: require EMA9 and VWAP to be separated by at least CrossMinGapTicks
            double spread = Math.Abs(ema9_0 - vwap0);
            double minGap = this.CrossMinGapTicks > 0
                ? this.CrossMinGapTicks * this.CurrentSymbol.TickSize
                : 0;

            if (minGap > 0 && spread < minGap)
                return 0;

            if (aboveBoth)
                return 1;
            if (belowBoth)
                return -1;
            return 0;
        }

        /// <summary>
        /// Checks if price is within the Keltner Channel (not overextended).
        /// From keltnerChannel.pine: KC top = mid + ATR * mult, KC bottom = mid - ATR * mult
        /// </summary>
        private bool IsWithinKeltnerChannel()
        {
            if (this.UseKcFilter != 1)
                return true;

            double kcMid0  = this.kcMid.GetValue(0);
            double kcAtr0  = this.kcAtr.GetValue(0);
            double close0  = HistoricalDataExtensions.Close(this.hdm, 0);

            double kcTop    = kcMid0 + kcAtr0 * this.KcAtrMultMin;
            double kcBottom = kcMid0 - kcAtr0 * this.KcAtrMultMin;

            return close0 <= kcTop && close0 >= kcBottom;
        }

        private void ProcessIntrabarEmaCross(Position[] positions)
        {
            int relation = this.GetEmaVwapRelation();
            if (relation == 0)
                return;

            if (this.lastEmaVwapRelation == 0)
            {
                this.lastEmaVwapRelation = relation;
                return;
            }

            if (relation == this.lastEmaVwapRelation)
                return;

            this.lastEmaVwapRelation = relation;
            Side crossSide = relation > 0 ? Side.Buy : Side.Sell;
            DateTime barTime = HistoricalDataExtensions.Time(this.hdm, 0);

            // Prevent machine-gun flips on repeated intrabar recrosses inside the same candle.
            if (this.lastCrossActionBarTime == barTime)
                return;

            bool inLong  = positions.Any(p => p.Side == Side.Buy);
            bool inShort = positions.Any(p => p.Side == Side.Sell);

            if ((crossSide == Side.Buy && inLong) || (crossSide == Side.Sell && inShort))
                return;

            // Keltner Channel filter: only enter if price is within the channel
            if (!this.IsWithinKeltnerChannel())
            {
                this.Log($"Entry blocked — price outside Keltner Channel (EMA9/VWAP cross: {crossSide})",
                         StrategyLoggingLevel.Trading);
                return;
            }

            if (positions.Any())
            {
                this.lastCrossActionBarTime = barTime;
                this.pendingEntrySide = crossSide;
                this.waitClosePositions = true;
                this.Log($"Intrabar EMA9/VWAP reverse cross — closing {(inLong ? "LONG" : "SHORT")}, flipping to {crossSide}",
                         StrategyLoggingLevel.Trading);

                foreach (var pos in positions)
                {
                    var r = pos.Close();
                    if (r.Status == TradingOperationResultStatus.Failure)
                    {
                        this.Log($"Close failed: {r.Message}", StrategyLoggingLevel.Error);
                        this.ProcessTradingRefuse();
                    }
                }

                return;
            }

            if (this.inPosition)
                return;

            this.lastCrossActionBarTime = barTime;
            this.Log($"Intrabar EMA9/VWAP cross entry: {crossSide}", StrategyLoggingLevel.Trading);
            this.PlaceEntry(crossSide);
        }

        // ── Filter helpers ────────────────────────────────────────────────────

        /// <summary>Returns true if the current EST hour is within the RTH window.</summary>
        private bool IsInRth()
        {
            var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            int estHour = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, easternZone).Hour;
            return estHour >= this.RthStartHour && estHour < this.RthEndHour;
        }

        /// <summary>
        /// Resets daily P&L at 6 PM EST (futures trading day boundary).
        /// Uses the EST day-of-year after 6 PM as the key to detect a new day.
        /// </summary>
        private void CheckDailyReset()
        {
            if (this.MaxDailyLoss <= 0)
                return;

            var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var estNow = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, easternZone);

            // Futures "trading day" starts at 6 PM EST. Use an offset day key:
            // before 6 PM → same calendar day; at/after 6 PM → next calendar day
            int dayKey = estNow.Hour >= 18 ? estNow.DayOfYear + 1 : estNow.DayOfYear;

            if (dayKey != this.lastResetDay)
            {
                this.lastResetDay  = dayKey;
                this.dailyPnl = 0;
                this.dailyLimitHit = false;
                this.Log($"Daily P&L reset (new trading day, EST: {estNow:HH:mm})", StrategyLoggingLevel.Trading);
            }

            // Check if daily loss limit is breached
            if (!this.dailyLimitHit && this.dailyPnl <= -this.MaxDailyLoss)
            {
                this.dailyLimitHit = true;
                this.Log($"DAILY LOSS LIMIT HIT — P&L: ${this.dailyPnl:F2} >= -${this.MaxDailyLoss} cutoff. No new trades until next session.",
                         StrategyLoggingLevel.Trading);
            }
        }

        // ── Execution ─────────────────────────────────────────────────────────

        // tpTicksOverride: if >= 0, overrides TakeProfitTicks
        private void PlaceEntry(Side side, int tpTicksOverride = -1)
        {
            int effectiveTpTicks = tpTicksOverride >= 0 ? tpTicksOverride : this.TakeProfitTicks;
            double ema9Val = this.ema9.GetValue(1);
            double vwapVal = this.vwap.GetValue(1);

            this.Log($"Entry: {side} | EMA9:{ema9Val:F2}  VWAP:{vwapVal:F2}  " +
                     $"SL:{StopLossTicks}t" +
                     (effectiveTpTicks > 0 ? $"  TP:{effectiveTpTicks}t" : "") +
                     (TrailingStopTicks > 0 && TrailActivationTicks > 0
                         ? $"  Trail:{TrailActivationTicks}t/{TrailingStopTicks}t"
                         : ""),
                     StrategyLoggingLevel.Trading);

            this.waitOpenPosition  = true;
            this.currentSide       = side;
            this.trailingActivated = false;
            this.bestPrice         = 0;

            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
            {
                Account     = this.CurrentAccount,
                Symbol      = this.CurrentSymbol,
                OrderTypeId = this.orderTypeId,
                Quantity    = this.Quantity,
                Side        = side,
                // PriceMeasurement.Offset expects a price-unit distance, not a raw tick count.
                // Multiply by TickSize so e.g. 60 ticks on MES (TickSize=0.25) → 15.0 price offset = 60 ticks.
                StopLoss    = SlTpHolder.CreateSL(this.StopLossTicks * this.CurrentSymbol.TickSize, PriceMeasurement.Offset),
                TakeProfit  = effectiveTpTicks > 0
                    ? SlTpHolder.CreateTP(effectiveTpTicks * this.CurrentSymbol.TickSize, PriceMeasurement.Offset)
                    : null,
            });

            if (result.Status == TradingOperationResultStatus.Failure)
            {
                this.Log($"Order failed: {result.Message}", StrategyLoggingLevel.Error);
                this.ProcessTradingRefuse();
            }
            else
            {
                this.inPosition = true;
                this.Log($"{side} position opened — SL: {StopLossTicks}t" +
                         (effectiveTpTicks > 0 ? $"  TP: {effectiveTpTicks}t" : "") +
                         $"  (bracket SL/TP should appear on chart)",
                         StrategyLoggingLevel.Trading);
            }
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
            this.pendingEntrySide   = null;
        }
    }
}
