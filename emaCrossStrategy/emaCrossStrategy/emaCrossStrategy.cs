using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace emaCrossStrategy
{
    /// <summary>
    /// EMA Cross Strategy — mirrors the TradingView "3 Fib EMAs – XO edition" Pine Script.
    ///
    /// Parameters (matching TradingView):
    ///   Micro EMA     — fast EMA (Pine: emaFastLen, default 5)
    ///   Mid EMA       — slow EMA (Pine: emaSlowLen, default 29)
    ///   Weakness Bars — how many consecutive bars the EMA gap must shrink to signal an exit
    ///
    /// Entry (bar close only, no look-ahead bias):
    ///   LONG  — Micro EMA crosses above Mid EMA.
    ///   SHORT — Micro EMA crosses below Mid EMA.
    ///
    /// Exit — three mechanisms (evaluated in priority order):
    ///   1. Hard Stop Loss   — bracket order attached at entry (always active).
    ///   2. Take Profit      — bracket order attached at entry (optional, 0 = disabled).
    ///   3. Smart Trail      — code-managed tick-level trailing stop.
    ///        • Activates only after profit reaches TrailActivationTicks.
    ///        • Once active, tracks the best price seen and closes if price pulls back
    ///          more than TrailingStopTicks from that peak (0 = disabled).
    ///   4. Weakness Bars    — bar-close: gap shrinking N consecutive bars → close.
    ///   5. Reverse cross    — bar-close: cross other way → close and flip direction.
    /// </summary>
    public sealed class EmaCrossStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Instrument ────────────────────────────────────────────────────────
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        // ── EMA settings (match TradingView "Choose Your EMA" group) ─────────
        [InputParameter("Micro EMA", 2, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int MicroEmaLen { get; set; }

        [InputParameter("Mid EMA", 3, minimum: 2, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int MidEmaLen { get; set; }

        [InputParameter("Weakness Bars", 4, minimum: 1, maximum: 20, increment: 1, decimalPlaces: 0)]
        public int WeaknessBars { get; set; }

        // ── Chart / history ───────────────────────────────────────────────────
        [InputParameter("Period", 5)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 6)]
        public DateTime StartPoint { get; set; }

        // ── Trade settings ────────────────────────────────────────────────────
        [InputParameter("Quantity", 7)]
        public int Quantity { get; set; }

        [InputParameter("Stop Loss (ticks, safety net)", 8)]
        public int StopLossTicks { get; set; }

        // ── Profit management ─────────────────────────────────────────────────
        [InputParameter("Take Profit (ticks, 0 = disabled)", 9)]
        public int TakeProfitTicks { get; set; }

        // Smart trailing: activates only once profit >= TrailActivationTicks.
        // Tracks the best price reached and closes if price pulls back more than
        // TrailingStopTicks from that peak. Set either to 0 to disable trailing.
        [InputParameter("Trail Activation (ticks profit to start trailing, 0 = disabled)", 10)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0 = disabled)", 11)]
        public int TrailingStopTicks { get; set; }
        // ─────────────────────────────────────────────────────────────────────────

        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        private Indicator microEma;
        private Indicator midEma;
        private HistoricalData hdm;
        private string orderTypeId;

        private int longPositionsCount;
        private int shortPositionsCount;

        private bool waitOpenPosition;
        private bool waitClosePositions;
        private bool inPosition;

        // When a reverse cross closes the current position, this queues the new direction
        // so it fires inside Core_PositionRemoved once the close confirms.
        private Side? pendingEntrySide;

        // ── Smart trailing state ──────────────────────────────────────────────
        // Tracks whether the profit threshold has been hit and the best price seen.
        private bool   trailingActivated;
        private double bestPrice;          // peak price for longs, trough for shorts
        private Side   currentSide;        // direction of the open position

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public EmaCrossStrategy() : base()
        {
            this.Name        = "EMA Cross Strategy";
            this.Description = "Mirrors the TradingView 3 Fib EMAs XO Pine Script. " +
                               "Enters on Micro/Mid EMA crossovers. Exits on EMA gap " +
                               "weakness (Weakness Bars) or a reverse cross (which also " +
                               "flips into the new direction). All signals on bar close only.";

            this.MicroEmaLen   = 5;
            this.MidEmaLen     = 29;
            this.WeaknessBars  = 2;
            this.Period        = Period.MIN1;
            this.StartPoint    = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity      = 1;
            this.StopLossTicks = 100;
            this.TakeProfitTicks     = 0;   // disabled by default
            this.TrailActivationTicks = 30; // start trailing after 30 ticks of profit
            this.TrailingStopTicks   = 15;  // trail 15 ticks behind the peak
        }

        protected override void OnRun()
        {
            this.totalNetPl          = 0;
            this.totalGrossPl         = 0;
            this.totalFee             = 0;
            this.inPosition           = false;
            this.waitOpenPosition     = false;
            this.waitClosePositions   = false;
            this.pendingEntrySide     = null;
            this.trailingActivated    = false;
            this.bestPrice            = 0;

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

            if (this.MicroEmaLen >= this.MidEmaLen)
            {
                this.Log($"Micro EMA ({this.MicroEmaLen}) must be smaller than Mid EMA ({this.MidEmaLen}).", StrategyLoggingLevel.Error);
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

            this.microEma = Core.Instance.Indicators.BuiltIn.EMA(this.MicroEmaLen, PriceType.Close);
            this.midEma   = Core.Instance.Indicators.BuiltIn.EMA(this.MidEmaLen,   PriceType.Close);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);
            this.hdm.AddIndicator(this.microEma);
            this.hdm.AddIndicator(this.midEma);

            Core.PositionAdded      += this.Core_PositionAdded;
            Core.PositionRemoved    += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded         += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem     += this.Hdm_OnNewHistoryItem;

            this.Log($"Started — Micro:{MicroEmaLen}  Mid:{MidEmaLen}  " +
                     $"WeaknessBars:{WeaknessBars}  SL:{StopLossTicks}t  " +
                     $"TP:{(TakeProfitTicks > 0 ? $"{TakeProfitTicks}t" : "off")}  " +
                     $"Trail:{(TrailingStopTicks > 0 ? $"activate@{TrailActivationTicks}t trail@{TrailingStopTicks}t" : "off")}",
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

                // Cancel any leftover SL orders so they don't re-open positions
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

                // Reset trailing state for the next position
                this.trailingActivated = false;
                this.bestPrice         = 0;

                // Reverse-cross flip: immediately enter the new direction
                if (this.pendingEntrySide.HasValue)
                {
                    var side = this.pendingEntrySide.Value;
                    this.pendingEntrySide = null;
                    this.PlaceEntry(side);
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
        }

        // Fires every price tick — manages the smart trailing stop.
        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            if (this.TrailingStopTicks <= 0 || this.TrailActivationTicks <= 0)
                return;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            if (!positions.Any())
                return;

            double pnlTicks = positions.Sum(x => x.GrossPnLTicks);
            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);

            // Step 1: activate trailing once profit threshold is reached
            if (!this.trailingActivated && pnlTicks >= this.TrailActivationTicks)
            {
                this.trailingActivated = true;
                this.bestPrice         = currentPrice;
                this.Log($"Trail activated at {pnlTicks:F1} ticks profit. Best price set to {currentPrice:F4}", StrategyLoggingLevel.Trading);
            }

            if (!this.trailingActivated)
                return;

            // Step 2: update the best price seen since activation
            if (this.currentSide == Side.Buy)
                this.bestPrice = Math.Max(this.bestPrice, currentPrice);
            else
                this.bestPrice = Math.Min(this.bestPrice, currentPrice);

            // Step 3: compute trail level and check if price breached it
            double tickSize   = this.CurrentSymbol.TickSize;
            double trailDist  = this.TrailingStopTicks * tickSize;

            bool trailHit = this.currentSide == Side.Buy
                ? currentPrice <= this.bestPrice - trailDist
                : currentPrice >= this.bestPrice + trailDist;

            if (trailHit)
            {
                this.Log($"Trail stop hit — best:{this.bestPrice:F4}  current:{currentPrice:F4}  dist:{TrailingStopTicks}t", StrategyLoggingLevel.Trading);
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

        // Fires when a bar closes — this is the ONLY place signals are evaluated.
        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.OnBarClose();
        }

        // ── Core logic (runs on every bar close) ──────────────────────────────

        private void OnBarClose()
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // EMA values:
            //   GetValue(1) = bar that just closed (safe, fully formed)
            //   GetValue(2) = bar before that
            double micro1 = this.microEma.GetValue(1);
            double mid1   = this.midEma.GetValue(1);
            double micro2 = this.microEma.GetValue(2);
            double mid2   = this.midEma.GetValue(2);

            // Crossover detection — mirrors Pine's ta.crossover / ta.crossunder
            bool bullishCross = micro1 > mid1 && micro2 <= mid2;
            bool bearishCross = micro1 < mid1 && micro2 >= mid2;

            // Gap weakness — mirrors Pine's ta.falling(math.abs(emaFast - emaSlow), weakBars)
            bool gapWeak = this.IsGapFalling(this.WeaknessBars);

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            if (positions.Any())
            {
                // ── Exit logic (mirrors Pine exitLong / exitShort) ─────────────
                bool inLong  = positions.Any(p => p.Side == Side.Buy);
                bool inShort = positions.Any(p => p.Side == Side.Sell);

                bool exitLong  = inLong  && (gapWeak || bearishCross);
                bool exitShort = inShort && (gapWeak || bullishCross);

                if (exitLong || exitShort)
                {
                    string reason = gapWeak ? $"gap weakness ({WeaknessBars} bars)" : "reverse cross";
                    this.Log($"Exit {(exitLong ? "LONG" : "SHORT")} — {reason}", StrategyLoggingLevel.Trading);

                    // Reverse cross → queue flip into new direction once close confirms
                    if (!gapWeak)
                    {
                        Side newSide = bullishCross ? Side.Buy : Side.Sell;
                        this.pendingEntrySide = newSide;
                    }

                    this.waitClosePositions = true;
                    foreach (var pos in positions)
                    {
                        var r = pos.Close();
                        if (r.Status == TradingOperationResultStatus.Failure)
                        {
                            this.Log($"Close failed: {r.Message}", StrategyLoggingLevel.Error);
                            this.ProcessTradingRefuse();
                        }
                    }
                    return; // don't evaluate entries on the same bar as an exit
                }
            }
            else
            {
                // ── Entry logic ───────────────────────────────────────────────
                if (this.inPosition)
                    return;

                if (!bullishCross && !bearishCross)
                    return;

                Side entrySide = bullishCross ? Side.Buy : Side.Sell;
                this.PlaceEntry(entrySide);
            }
        }

        private void PlaceEntry(Side side)
        {
            this.Log($"Signal: {side} | Micro:{this.microEma.GetValue(1):F4}  Mid:{this.midEma.GetValue(1):F4}  " +
                     $"SL:{StopLossTicks}t" +
                     (TakeProfitTicks > 0 ? $"  TP:{TakeProfitTicks}t" : "") +
                     (TrailingStopTicks > 0 ? $"  Trail:activate@{TrailActivationTicks}t+{TrailingStopTicks}t" : ""),
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

        /// <summary>
        /// Mirrors Pine's <c>ta.falling(math.abs(emaFast - emaSlow), bars)</c>.
        /// Returns true when the EMA gap has been strictly decreasing for
        /// <paramref name="bars"/> consecutive closed bars.
        /// bar index 1 = just-closed bar; index bars+1 = oldest bar in the window.
        /// </summary>
        private bool IsGapFalling(int bars)
        {
            for (int i = 1; i <= bars; i++)
            {
                double gapRecent = Math.Abs(this.microEma.GetValue(i)     - this.midEma.GetValue(i));
                double gapOlder  = Math.Abs(this.microEma.GetValue(i + 1) - this.midEma.GetValue(i + 1));
                if (gapRecent >= gapOlder)
                    return false;
            }
            return true;
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
            this.pendingEntrySide   = null;
        }
    }
}
