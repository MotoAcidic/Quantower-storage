using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace futuresProStrategy
{
    /// <summary>
    /// Futures Pro Strategy — Multi-factor trend-following for MES / NQ / ES
    ///
    /// Combines the top futures trading approaches:
    ///   1. Trend EMA filter (200) — only trade in direction of the major trend
    ///   2. Fast/Slow EMA cross (9/21) — entry timing signal
    ///   3. RSI filter — reject entries at overbought/oversold extremes
    ///   4. MACD histogram — momentum confirmation (optional)
    ///   5. RTH session filter — optionally restrict to regular trading hours
    ///
    /// Entry (bar close, ALL conditions must pass):
    ///   LONG  — Fast crosses above Slow, price above Trend EMA,
    ///           RSI &lt; overbought, MACD histogram &gt; 0 (if enabled)
    ///   SHORT — Fast crosses below Slow, price below Trend EMA,
    ///           RSI &gt; oversold, MACD histogram &lt; 0 (if enabled)
    ///
    /// Exit:
    ///   - Hard Stop Loss (bracket)
    ///   - Take Profit (bracket, optional)
    ///   - Code-managed trailing stop
    ///   - Reverse cross: flip ONLY if trend EMA agrees, otherwise just close
    ///   - Daily loss cutoff
    /// </summary>
    public sealed class FuturesProStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Instrument ────────────────────────────────────────────────────────
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        // ── EMA settings ──────────────────────────────────────────────────────
        [InputParameter("Fast EMA", 2, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int FastEmaLen { get; set; }

        [InputParameter("Slow EMA", 3, minimum: 2, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int SlowEmaLen { get; set; }

        [InputParameter("Trend EMA (major trend filter)", 4, minimum: 10, maximum: 1000, increment: 10, decimalPlaces: 0)]
        public int TrendEmaLen { get; set; }

        // ── RSI filter ────────────────────────────────────────────────────────
        [InputParameter("RSI Period", 5, minimum: 2, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int RsiPeriod { get; set; }

        [InputParameter("RSI Overbought (block longs above this)", 6, minimum: 50, maximum: 100, increment: 5, decimalPlaces: 0)]
        public int RsiOverbought { get; set; }

        [InputParameter("RSI Oversold (block shorts below this)", 7, minimum: 0, maximum: 50, increment: 5, decimalPlaces: 0)]
        public int RsiOversold { get; set; }

        // ── MACD filter (optional) ────────────────────────────────────────────
        // 0 = MACD filter disabled; 1 = MACD histogram must agree with direction
        [InputParameter("Use MACD filter (0=off, 1=on)", 8, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int UseMacd { get; set; }

        [InputParameter("MACD Fast Period", 9, minimum: 2, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int MacdFastLen { get; set; }

        [InputParameter("MACD Slow Period", 10, minimum: 2, maximum: 200, increment: 1, decimalPlaces: 0)]
        public int MacdSlowLen { get; set; }

        [InputParameter("MACD Signal Period", 11, minimum: 2, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int MacdSignalLen { get; set; }

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

        [InputParameter("Take Profit (ticks, 0 = disabled)", 16, minimum: 0, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int TakeProfitTicks { get; set; }

        // ── Trailing stop ─────────────────────────────────────────────────────
        [InputParameter("Trail Activation (ticks profit to start, 0 = disabled)", 17, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0 = disabled)", 18, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailingStopTicks { get; set; }

        // ── Session filter ────────────────────────────────────────────────────
        // 0 = trade 24h; 1 = new entries only during RTH window
        [InputParameter("RTH Only (0=24h, 1=RTH entries only)", 19, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int RthOnly { get; set; }

        [InputParameter("RTH Start Hour (EST, e.g. 9 = 9:30 AM)", 20, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthStartHour { get; set; }

        [InputParameter("RTH End Hour (EST, e.g. 16 = 4:00 PM)", 21, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthEndHour { get; set; }

        // ── Daily risk ────────────────────────────────────────────────────────
        // Maximum loss in dollars per trading day (resets at 6 PM EST). 0 = disabled.
        [InputParameter("Max Daily Loss ($ amount, 0 = disabled)", 22, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDailyLoss { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        // Indicators
        private Indicator fastEma;
        private Indicator slowEma;
        private Indicator trendEma;
        private Indicator rsi;
        private Indicator macd;

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

        // Daily P&L tracking (in currency)
        private double dailyPnl;
        private int    lastResetDay; // day-of-year of last daily reset
        private bool   dailyLimitHit;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public FuturesProStrategy() : base()
        {
            this.Name        = "Futures Pro Strategy";
            this.Description = "Multi-factor trend strategy for MES/NQ/ES. " +
                               "EMA cross + trend filter + RSI + MACD + RTH session. " +
                               "Reverse cross flips ONLY with trend, otherwise just closes.";

            // EMA defaults: 9/21 cross with 200 trend filter
            this.FastEmaLen   = 9;
            this.SlowEmaLen   = 21;
            this.TrendEmaLen  = 200;

            // RSI defaults
            this.RsiPeriod      = 14;
            this.RsiOverbought  = 70;
            this.RsiOversold    = 30;

            // MACD defaults (standard 12/26/9), disabled by default
            this.UseMacd       = 0;
            this.MacdFastLen   = 12;
            this.MacdSlowLen   = 26;
            this.MacdSignalLen = 9;

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
            this.dailyPnl           = 0;
            this.lastResetDay       = -1;
            this.dailyLimitHit      = false;

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

            if (this.FastEmaLen >= this.SlowEmaLen)
            {
                this.Log($"Fast EMA ({this.FastEmaLen}) must be smaller than Slow EMA ({this.SlowEmaLen}).", StrategyLoggingLevel.Error);
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

            // Create indicators
            this.fastEma  = Core.Instance.Indicators.BuiltIn.EMA(this.FastEmaLen,  PriceType.Close);
            this.slowEma  = Core.Instance.Indicators.BuiltIn.EMA(this.SlowEmaLen,  PriceType.Close);
            this.trendEma = Core.Instance.Indicators.BuiltIn.EMA(this.TrendEmaLen, PriceType.Close);
            this.rsi      = Core.Instance.Indicators.BuiltIn.RSI(this.RsiPeriod, PriceType.Close, RSIMode.Exponential, MaMode.SMA, this.RsiPeriod, IndicatorCalculationType.AllAvailableData);

            if (this.UseMacd == 1)
                this.macd = Core.Instance.Indicators.BuiltIn.MACD(this.MacdFastLen, this.MacdSlowLen, this.MacdSignalLen, IndicatorCalculationType.AllAvailableData);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);
            this.hdm.AddIndicator(this.fastEma);
            this.hdm.AddIndicator(this.slowEma);
            this.hdm.AddIndicator(this.trendEma);
            this.hdm.AddIndicator(this.rsi);
            if (this.macd != null)
                this.hdm.AddIndicator(this.macd);

            Core.PositionAdded      += this.Core_PositionAdded;
            Core.PositionRemoved    += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded         += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem     += this.Hdm_OnNewHistoryItem;

            this.Log($"Started — Fast:{FastEmaLen}  Slow:{SlowEmaLen}  Trend:{TrendEmaLen}  " +
                     $"RSI:{RsiPeriod} (OB:{RsiOverbought} OS:{RsiOversold})  " +
                     $"MACD:{(UseMacd == 1 ? $"{MacdFastLen}/{MacdSlowLen}/{MacdSignalLen}" : "off")}  " +
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

                // Reverse-cross flip: open new direction only if trend filter agrees
                if (this.pendingEntrySide.HasValue)
                {
                    var side = this.pendingEntrySide.Value;
                    this.pendingEntrySide = null;

                    // Final safety check: trend filter and daily limit
                    double trend1 = this.trendEma.GetValue(1);
                    double close1 = HistoricalDataExtensions.Close(this.hdm, 1);
                    bool trendOk = side == Side.Buy ? close1 > trend1 : close1 < trend1;

                    if (trendOk && !this.dailyLimitHit)
                    {
                        this.PlaceEntry(side);
                    }
                    else
                    {
                        this.Log($"Reverse flip to {side} blocked — " +
                                 (!trendOk ? $"price {(side == Side.Buy ? "below" : "above")} Trend EMA" : "daily loss limit hit"),
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
                this.dailyPnl += obj.GrossPnl.Value;
        }

        // Fires every price tick — manages trailing stop
        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            if (this.TrailActivationTicks <= 0 || this.TrailingStopTicks <= 0)
                return;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

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
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // ── Daily P&L reset at 6 PM EST ──────────────────────────────────
            this.CheckDailyReset();

            if (this.dailyLimitHit)
                return;

            // EMA values: GetValue(1) = last fully closed bar
            double fast1  = this.fastEma.GetValue(1);
            double slow1  = this.slowEma.GetValue(1);
            double fast2  = this.fastEma.GetValue(2);
            double slow2  = this.slowEma.GetValue(2);
            double trend1 = this.trendEma.GetValue(1);
            double close1 = HistoricalDataExtensions.Close(this.hdm, 1);

            // Cross detection
            bool bullishCross = fast1 > slow1 && fast2 <= slow2;
            bool bearishCross = fast1 < slow1 && fast2 >= slow2;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            if (positions.Any())
            {
                // ── Exit logic ────────────────────────────────────────────────
                bool inLong  = positions.Any(p => p.Side == Side.Buy);
                bool inShort = positions.Any(p => p.Side == Side.Sell);

                bool reverseLong  = inLong  && bearishCross;
                bool reverseShort = inShort && bullishCross;

                if (reverseLong || reverseShort)
                {
                    Side newSide = bullishCross ? Side.Buy : Side.Sell;

                    // Only queue the flip if ALL entry filters pass for the new direction
                    bool trendOk = newSide == Side.Buy ? close1 > trend1 : close1 < trend1;
                    bool rsiOk   = this.CheckRsiFilter(newSide);
                    bool macdOk  = this.CheckMacdFilter(newSide);
                    bool rthOk   = this.IsInRth();

                    if (trendOk && rsiOk && macdOk && (this.RthOnly == 0 || rthOk))
                        this.pendingEntrySide = newSide;

                    this.waitClosePositions = true;
                    this.Log($"Reverse cross — closing {(reverseLong ? "LONG" : "SHORT")}" +
                             (this.pendingEntrySide.HasValue ? $", flipping to {newSide}" : $", {newSide} blocked by filters"),
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
                }
            }
            else
            {
                // ── Entry logic — requires all filters to pass ────────────────
                if (this.inPosition)
                    return;

                if (!bullishCross && !bearishCross)
                    return;

                Side entrySide = bullishCross ? Side.Buy : Side.Sell;

                // Filter 1: Trend EMA — price must be on the right side
                bool trendFilter = entrySide == Side.Buy ? close1 > trend1 : close1 < trend1;
                if (!trendFilter)
                {
                    this.Log($"Entry {entrySide} blocked — price {close1:F2} {(entrySide == Side.Buy ? "below" : "above")} Trend EMA {trend1:F2}",
                             StrategyLoggingLevel.Trading);
                    return;
                }

                // Filter 2: RSI — don't chase overbought/oversold
                if (!this.CheckRsiFilter(entrySide))
                {
                    double rsiVal = this.rsi.GetValue(1);
                    this.Log($"Entry {entrySide} blocked — RSI {rsiVal:F1} {(entrySide == Side.Buy ? $">= OB {RsiOverbought}" : $"<= OS {RsiOversold}")}",
                             StrategyLoggingLevel.Trading);
                    return;
                }

                // Filter 3: MACD histogram must agree (if enabled)
                if (!this.CheckMacdFilter(entrySide))
                {
                    this.Log($"Entry {entrySide} blocked — MACD histogram disagrees",
                             StrategyLoggingLevel.Trading);
                    return;
                }

                // Filter 4: RTH session window (if enabled)
                if (this.RthOnly == 1 && !this.IsInRth())
                {
                    this.Log($"Entry {entrySide} blocked — outside RTH window ({RthStartHour}:00-{RthEndHour}:00 EST)",
                             StrategyLoggingLevel.Trading);
                    return;
                }

                this.PlaceEntry(entrySide);
            }
        }

        // ── Filter helpers ────────────────────────────────────────────────────

        /// <summary>
        /// RSI filter: for longs, RSI must be below overbought level.
        /// For shorts, RSI must be above oversold level.
        /// This prevents chasing moves that are already exhausted.
        /// </summary>
        private bool CheckRsiFilter(Side side)
        {
            double rsiVal = this.rsi.GetValue(1);
            if (side == Side.Buy)
                return rsiVal < this.RsiOverbought;
            else
                return rsiVal > this.RsiOversold;
        }

        /// <summary>
        /// MACD histogram filter: histogram must be positive for longs,
        /// negative for shorts. Returns true if MACD is disabled.
        /// </summary>
        private bool CheckMacdFilter(Side side)
        {
            if (this.UseMacd != 1 || this.macd == null)
                return true;

            // MACD line index: 0=MACD, 1=Signal, 2=Histogram
            double histogram = this.macd.GetValue(1, 2);
            return side == Side.Buy ? histogram > 0 : histogram < 0;
        }

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

        private void PlaceEntry(Side side)
        {
            double rsiVal = this.rsi.GetValue(1);
            string macdStr = this.macd != null ? $"  MACD-H:{this.macd.GetValue(1, 2):F2}" : "";

            this.Log($"Entry: {side} | Fast:{this.fastEma.GetValue(1):F2}  Slow:{this.slowEma.GetValue(1):F2}  " +
                     $"Trend:{this.trendEma.GetValue(1):F2}  RSI:{rsiVal:F1}{macdStr}  " +
                     $"SL:{StopLossTicks}t" +
                     (TakeProfitTicks > 0 ? $"  TP:{TakeProfitTicks}t" : "") +
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
                StopLoss    = SlTpHolder.CreateSL(this.StopLossTicks, PriceMeasurement.Offset),
                TakeProfit  = this.TakeProfitTicks > 0
                    ? SlTpHolder.CreateTP(this.TakeProfitTicks, PriceMeasurement.Offset)
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
                this.Log($"{side} position opened", StrategyLoggingLevel.Trading);
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
