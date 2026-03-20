using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace emaSimpleStrategy
{
    /// <summary>
    /// Simple EMA Cross Strategy
    ///
    /// - Enters LONG when Micro EMA crosses above Mid EMA (bar close)
    /// - Enters SHORT when Micro EMA crosses below Mid EMA (bar close)
    /// - On reverse cross: closes current position and immediately opens opposite direction
    /// - Hard Stop Loss attached to every entry bracket
    /// - Take Profit attached to every entry bracket (0 = disabled)
    /// - Trailing stop: activates once profit >= TrailActivationTicks, closes if price
    ///   pulls back more than TrailingStopTicks from peak (0 = disabled)
    /// </summary>
    public sealed class EmaSimpleStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Instrument ────────────────────────────────────────────────────────
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        // ── EMA settings ──────────────────────────────────────────────────────
        [InputParameter("Micro EMA (fast)", 2, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int MicroEmaLen { get; set; }

        [InputParameter("Mid EMA (slow)", 3, minimum: 2, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int MidEmaLen { get; set; }

        // ── Chart / history ───────────────────────────────────────────────────
        [InputParameter("Period", 4)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 5)]
        public DateTime StartPoint { get; set; }

        // ── Trade settings ────────────────────────────────────────────────────
        [InputParameter("Quantity", 6)]
        public int Quantity { get; set; }

        [InputParameter("Stop Loss (ticks)", 7, minimum: 1, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int StopLossTicks { get; set; }

        [InputParameter("Take Profit (ticks, 0 = disabled)", 8, minimum: 0, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int TakeProfitTicks { get; set; }

        // ── Trailing stop ─────────────────────────────────────────────────────
        // Activates once profit reaches TrailActivationTicks, then trails from peak.
        // Set either to 0 to disable trailing.
        [InputParameter("Trail Activation (ticks profit to start, 0 = disabled)", 9, minimum: 0, maximum: 1000, increment: 5, decimalPlaces: 0)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0 = disabled)", 10, minimum: 0, maximum: 1000, increment: 5, decimalPlaces: 0)]
        public int TrailingStopTicks { get; set; }

        // ─────────────────────────────────────────────────────────────────────
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

        // Queued direction for reverse-cross flip — fires in Core_PositionRemoved
        private Side? pendingEntrySide;

        // Smart trailing state
        private bool   trailingActivated;
        private double bestPrice;
        private Side   currentSide;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public EmaSimpleStrategy() : base()
        {
            this.Name        = "EMA Simple Strategy";
            this.Description = "Enters on EMA crossover, reverses on opposite cross. " +
                               "Hard SL/TP bracket + optional trailing stop. No filters.";

            this.MicroEmaLen          = 5;
            this.MidEmaLen            = 29;
            this.Period               = Period.MIN1;
            this.StartPoint           = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity             = 1;
            this.StopLossTicks        = 100;
            this.TakeProfitTicks      = 0;
            this.TrailActivationTicks = 30;
            this.TrailingStopTicks    = 15;
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
                     $"SL:{StopLossTicks}t  " +
                     $"TP:{(TakeProfitTicks > 0 ? $"{TakeProfitTicks}t" : "off")}  " +
                     $"Trail:{(TrailingStopTicks > 0 && TrailActivationTicks > 0 ? $"activate@{TrailActivationTicks}t / trail@{TrailingStopTicks}t" : "off")}",
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

                // Cancel any leftover SL/TP bracket orders
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

                // Reverse-cross flip: immediately open the new direction
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

        // Fires every price tick — manages the trailing stop
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
                this.Log($"Trail activated at {pnlTicks:F1}t profit. Best price: {currentPrice:F4}",
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

        // Fires when a bar closes — signals evaluated here only
        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.OnBarClose();
        }

        private void OnBarClose()
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // GetValue(1) = last fully closed bar, GetValue(2) = bar before that
            double micro1 = this.microEma.GetValue(1);
            double mid1   = this.midEma.GetValue(1);
            double micro2 = this.microEma.GetValue(2);
            double mid2   = this.midEma.GetValue(2);

            bool bullishCross = micro1 > mid1 && micro2 <= mid2;
            bool bearishCross = micro1 < mid1 && micro2 >= mid2;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            if (positions.Any())
            {
                bool inLong  = positions.Any(p => p.Side == Side.Buy);
                bool inShort = positions.Any(p => p.Side == Side.Sell);

                bool reverseLong  = inLong  && bearishCross;
                bool reverseShort = inShort && bullishCross;

                if (reverseLong || reverseShort)
                {
                    Side newSide = bullishCross ? Side.Buy : Side.Sell;
                    this.pendingEntrySide   = newSide;
                    this.waitClosePositions = true;

                    this.Log($"Reverse cross — closing {(reverseLong ? "LONG" : "SHORT")}, flipping to {newSide}",
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
                if (this.inPosition)
                    return;

                if (bullishCross)
                    this.PlaceEntry(Side.Buy);
                else if (bearishCross)
                    this.PlaceEntry(Side.Sell);
            }
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
            this.pendingEntrySide   = null;
        }

        private void PlaceEntry(Side side)
        {
            this.Log($"Entry: {side} | Micro:{this.microEma.GetValue(1):F4}  Mid:{this.midEma.GetValue(1):F4}  " +
                     $"SL:{StopLossTicks}t" +
                     (TakeProfitTicks > 0 ? $"  TP:{TakeProfitTicks}t" : "") +
                     (TrailingStopTicks > 0 && TrailActivationTicks > 0
                         ? $"  Trail:on@{TrailActivationTicks}t / {TrailingStopTicks}t"
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
    }
}
