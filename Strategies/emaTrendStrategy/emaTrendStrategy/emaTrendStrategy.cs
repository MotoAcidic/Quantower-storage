using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace emaTrendStrategy
{
    /// <summary>
    /// EMA Trend Strategy
    ///
    /// Entry logic (evaluated on bar CLOSE only — no look-ahead bias):
    ///   1. EMA crossover: Fast EMA crosses above/below Slow EMA on the just-completed bar.
    ///   2. Trend filter: Price must be above Trend EMA for longs, below for shorts.
    ///      Set Trend EMA to 0 to disable.
    ///   3. Momentum filter: The spread between Fast/Slow EMA at the crossover bar must
    ///      exceed the average spread of the prior 4 bars * Momentum Multiplier.
    ///      Set Momentum Multiplier to 1.0 to disable.
    ///
    /// Exit logic:
    ///   Mode 0 — Bar Push: close when the current price ticks below the prior bar's low
    ///            (long) or above the prior bar's high (short). Fixed SL as safety net.
    ///   Mode 1 — SL/TP + Trailing: bracket orders placed at entry manage the full exit.
    ///   Mode 2 — TV Match: mirrors the TradingView Pine Script exactly. Exit when the
    ///            EMA gap has been shrinking for WeaknessBars consecutive closed bars, OR
    ///            when a reverse EMA crossover occurs (which also queues a re-entry in
    ///            the new direction). Fixed SL is placed as a hard safety net.
    /// </summary>
    public sealed class EmaTrendStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Core inputs ───────────────────────────────────────────────────────
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Fast EMA", 2, minimum: 1, maximum: 200, increment: 1, decimalPlaces: 0)]
        public int FastEMA { get; set; }

        [InputParameter("Slow EMA", 3, minimum: 2, maximum: 200, increment: 1, decimalPlaces: 0)]
        public int SlowEMA { get; set; }

        [InputParameter("Trend EMA (0 = disabled)", 4, minimum: 0, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int TrendEMA { get; set; }

        [InputParameter("Period", 5)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 6)]
        public DateTime StartPoint { get; set; }

        [InputParameter("Quantity", 7)]
        public int Quantity = 1;

        [InputParameter("Momentum Multiplier (1.0 = disabled)", 8)]
        public double Multiplicative = 1.0;

        // ── Exit settings ─────────────────────────────────────────────────────
        // Mode 0: bar-push exit (code-managed). Fixed SL as hard safety net.
        // Mode 1: trailing SL + TP bracket attached at entry. Bar-push logic skipped.
        // Mode 2: TV Match — exit on EMA gap weakness (N bars) OR reverse cross.
        [InputParameter("Exit Mode (0=Bar Push, 1=SL/TP+Trailing, 2=TV Match)", 10, 0, 2, 1, 0)]
        public int ExitMode = 2;

        [InputParameter("Stop Loss (ticks, all modes)", 11)]
        public int StopLossTicks = 100;

        [InputParameter("Trailing Stop (ticks, Exit Mode 1 only)", 12)]
        public int TrailingStopTicks = 40;

        [InputParameter("Take Profit (ticks, Exit Mode 1 only)", 13)]
        public int TakeProfitTicks = 80;

        [InputParameter("Weakness Bars (Exit Mode 2 only)", 14, minimum: 1, maximum: 10, increment: 1, decimalPlaces: 0)]
        public int WeaknessBars = 2;
        // ─────────────────────────────────────────────────────────────────────

        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        private Indicator fastEmaIndicator;
        private Indicator slowEmaIndicator;
        private Indicator trendEmaIndicator;
        private HistoricalData hdm;
        private string orderTypeId;

        private int longPositionsCount;
        private int shortPositionsCount;

        private bool waitOpenPosition;
        private bool waitClosePositions;
        private bool inPosition;

        // Prevents re-entering on the same crossover; resets when EMAs cross back
        private string prevSide = "none";

        // Set true by Hdm_OnNewHistoryItem (bar close); consumed once by entry logic
        private bool newBar;

        // Mode 2: when a reverse cross triggers an exit, queue entry in the new direction
        // so it fires as soon as Core_PositionRemoved confirms the close.
        private Side? pendingEntrySide;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public EmaTrendStrategy() : base()
        {
            this.Name        = "EMA Trend Strategy";
            this.Description = "EMA crossover with trend filter + momentum confirmation. " +
                               "Signals evaluated on bar close only — backtest matches live exactly.";

            this.FastEMA        = 5;
            this.SlowEMA        = 29;
            this.TrendEMA       = 233;
            this.Period         = Period.MIN1;
            this.StartPoint     = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity       = 1;
            this.Multiplicative = 1.0;   // disabled — matches TradingView script
            this.ExitMode       = 2;     // TV Match by default
            this.StopLossTicks  = 100;
            this.TrailingStopTicks = 40;
            this.TakeProfitTicks   = 80;
            this.WeaknessBars      = 2;  // matches Pine's weakBars default
        }

        protected override void OnRun()
        {
            this.totalNetPl         = 0;
            this.totalGrossPl       = 0;
            this.totalFee           = 0;
            this.inPosition         = false;
            this.prevSide           = "none";
            this.newBar             = false;
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
            this.pendingEntrySide   = null;

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

            this.orderTypeId = Core.OrderTypes
                .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId
                                  && x.Behavior == OrderTypeBehavior.Market)?.Id;

            if (string.IsNullOrEmpty(this.orderTypeId))
            {
                this.Log("Connection does not support market orders.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.FastEMA >= this.SlowEMA)
            {
                this.Log($"Fast EMA ({this.FastEMA}) must be smaller than Slow EMA ({this.SlowEMA}).", StrategyLoggingLevel.Error);
                return;
            }

            this.fastEmaIndicator = Core.Instance.Indicators.BuiltIn.EMA(this.FastEMA, PriceType.Close);
            this.slowEmaIndicator = Core.Instance.Indicators.BuiltIn.EMA(this.SlowEMA, PriceType.Close);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            this.hdm.AddIndicator(this.fastEmaIndicator);
            this.hdm.AddIndicator(this.slowEmaIndicator);

            if (this.TrendEMA > 0)
            {
                this.trendEmaIndicator = Core.Instance.Indicators.BuiltIn.EMA(this.TrendEMA, PriceType.Close);
                this.hdm.AddIndicator(this.trendEmaIndicator);
            }

            Core.PositionAdded       += this.Core_PositionAdded;
            Core.PositionRemoved     += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded  += this.Core_OrdersHistoryAdded;
            Core.TradeAdded          += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem     += this.Hdm_OnNewHistoryItem;

            string modeStr = ExitMode == 0 ? "Bar Push" : ExitMode == 1 ? "SL/TP+Trailing" : $"TV Match (weak:{WeaknessBars}bars)";
            this.Log($"Started — Fast EMA:{FastEMA}  Slow EMA:{SlowEMA}  " +
                     $"Trend EMA:{(TrendEMA > 0 ? TrendEMA.ToString() : "off")}  " +
                     $"Momentum:{(Multiplicative > 1.0 ? $"x{Multiplicative}" : "off")}  " +
                     $"Mode:{modeStr}  SL:{StopLossTicks}t" +
                     (ExitMode == 1 ? $"  Trail:{TrailingStopTicks}t  TP:{TakeProfitTicks}t" : ""),
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
            meter.CreateObservableCounter("total-short-positions",  () => this.shortPositionsCount, description: "Total short positions");
            meter.CreateObservableCounter("total-pl-net",           () => this.totalNetPl,           description: "Total Net P&L");
            meter.CreateObservableCounter("total-pl-gross",         () => this.totalGrossPl,         description: "Total Gross P&L");
            meter.CreateObservableCounter("total-fee",              () => this.totalFee,             description: "Total Fees");
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

                // Cancel any bracket SL/TP orders left over so they don't re-open positions
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

                // Mode 2: if a reverse cross triggered the exit, immediately enter the new direction
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

        // Fires every price tick — used for real-time Mode 0 exit monitoring
        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e) => this.OnUpdate();

        // Fires when a bar closes — gates all entry logic
        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.newBar = true;
            this.OnUpdate();
        }

        // ── Core logic ────────────────────────────────────────────────────────

        private void OnUpdate()
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // Keep prevSide in sync every tick so if EMAs cross back the flag resets
            double fastNow = this.fastEmaIndicator.GetValue(0);
            double slowNow = this.slowEmaIndicator.GetValue(0);
            if (this.prevSide == "buy"  && fastNow <= slowNow) this.prevSide = "none";
            if (this.prevSide == "sell" && fastNow >= slowNow) this.prevSide = "none";

            // ── Mode 0: tick-level bar-push exit ──────────────────────────────
            if (this.ExitMode == 0)
            {
                var positionsNow = Core.Instance.Positions
                    .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                    .ToArray();

                if (positionsNow.Any())
                {
                    double closeNow = HistoricalDataExtensions.Close(this.hdm, 0);
                    double prevLow  = HistoricalDataExtensions.Low(this.hdm, 1);
                    double prevHigh = HistoricalDataExtensions.High(this.hdm, 1);

                    bool exitLong  = fastNow > slowNow && closeNow < prevLow;
                    bool exitShort = fastNow < slowNow && closeNow > prevHigh;

                    if (exitLong || exitShort)
                    {
                        this.waitClosePositions = true;
                        this.Log($"Bar Push exit — {(exitLong ? "LONG" : "SHORT")} triggered", StrategyLoggingLevel.Trading);
                        foreach (var pos in positionsNow)
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
                }
            }
            // Mode 1: bracket orders placed at entry manage the exit — nothing to do here tick-by-tick

            // ── All bar-close logic: Mode 2 exits + all entries ───────────────
            if (!this.newBar)
                return;
            this.newBar = false;

            // EMA values from the bar that just closed (1) and the bar before it (2)
            double fast1 = this.fastEmaIndicator.GetValue(1);
            double slow1 = this.slowEmaIndicator.GetValue(1);
            double fast2 = this.fastEmaIndicator.GetValue(2);
            double slow2 = this.slowEmaIndicator.GetValue(2);

            bool bullishCross = fast1 > slow1 && fast2 <= slow2;
            bool bearishCross = fast1 < slow1 && fast2 >= slow2;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            // ── Mode 2: TV Match exit (bar-close only, mirrors Pine ta.falling logic) ──
            if (this.ExitMode == 2 && positions.Any())
            {
                bool inLong  = positions.Any(p => p.Side == Side.Buy);
                bool inShort = positions.Any(p => p.Side == Side.Sell);

                // ta.falling(emaGap, WeaknessBars): gap shrinking for N consecutive bars
                bool gapWeak   = this.CheckGapFalling(this.WeaknessBars);
                bool exitLong  = inLong  && (gapWeak || bearishCross);
                bool exitShort = inShort && (gapWeak || bullishCross);

                if (exitLong || exitShort)
                {
                    string reason = gapWeak ? $"gap weakness ({WeaknessBars} bars)" : "reverse cross";
                    this.Log($"TV exit — {(exitLong ? "LONG" : "SHORT")} closing ({reason})", StrategyLoggingLevel.Trading);

                    // Reverse cross: queue a re-entry in the new direction once close confirms
                    if (!gapWeak)
                        this.pendingEntrySide = bullishCross ? Side.Buy : Side.Sell;

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
                    return; // don't run entry logic on the same bar as an exit
                }
            }

            // ── Entry logic ───────────────────────────────────────────────────
            if (this.inPosition)
                return;

            if (!bullishCross && !bearishCross)
                return;

            // ── Trend filter ──────────────────────────────────────────────────
            if (this.TrendEMA > 0 && this.trendEmaIndicator != null)
            {
                double trend1 = this.trendEmaIndicator.GetValue(1);
                double close1 = HistoricalDataExtensions.Close(this.hdm, 1);

                if (bullishCross && close1 < trend1)
                {
                    this.Log($"Trend filter blocked LONG — price {close1:F2} below Trend EMA {trend1:F2}", StrategyLoggingLevel.Trading);
                    return;
                }
                if (bearishCross && close1 > trend1)
                {
                    this.Log($"Trend filter blocked SHORT — price {close1:F2} above Trend EMA {trend1:F2}", StrategyLoggingLevel.Trading);
                    return;
                }
            }

            // ── Momentum filter (set Multiplicative = 1.0 to disable) ─────────
            if (this.Multiplicative > 1.0)
            {
                double spread1   = Math.Abs(fast1 - slow1);
                double spread2   = Math.Abs(this.fastEmaIndicator.GetValue(2) - this.slowEmaIndicator.GetValue(2));
                double spread3   = Math.Abs(this.fastEmaIndicator.GetValue(3) - this.slowEmaIndicator.GetValue(3));
                double spread4   = Math.Abs(this.fastEmaIndicator.GetValue(4) - this.slowEmaIndicator.GetValue(4));
                double spread5   = Math.Abs(this.fastEmaIndicator.GetValue(5) - this.slowEmaIndicator.GetValue(5));
                double spreadAvg = (spread2 + spread3 + spread4 + spread5) / 4.0;

                if (spread1 <= spreadAvg * this.Multiplicative)
                {
                    this.Log($"Momentum filter blocked — spread {spread1:F4} not > avg {spreadAvg:F4} x {Multiplicative}", StrategyLoggingLevel.Trading);
                    return;
                }
            }

            // Don't re-enter in the same direction we just left
            if (bullishCross && this.prevSide == "buy")  return;
            if (bearishCross && this.prevSide == "sell") return;

            if (bullishCross) this.PlaceEntry(Side.Buy);
            else              this.PlaceEntry(Side.Sell);
        }

        /// <summary>
        /// Mirrors Pine's <c>ta.falling(emaGap, bars)</c>: returns true when the absolute
        /// EMA gap has been strictly decreasing for <paramref name="bars"/> consecutive
        /// closed bars. Uses bar indices 1..bars+1 (GetValue(1) = just-closed bar).
        /// </summary>
        private bool CheckGapFalling(int bars)
        {
            for (int i = 1; i <= bars; i++)
            {
                double gapRecent = Math.Abs(this.fastEmaIndicator.GetValue(i)     - this.slowEmaIndicator.GetValue(i));
                double gapOlder  = Math.Abs(this.fastEmaIndicator.GetValue(i + 1) - this.slowEmaIndicator.GetValue(i + 1));
                if (gapRecent >= gapOlder) return false;
            }
            return true;
        }

        private void PlaceEntry(Side side)
        {
            string entryModeStr = ExitMode == 0 ? "Bar Push" : ExitMode == 1 ? "SL/TP+Trailing" : "TV Match";
            this.Log($"Signal: {side} crossover | " +
                     $"Fast EMA:{this.fastEmaIndicator.GetValue(1):F4}  " +
                     $"Slow EMA:{this.slowEmaIndicator.GetValue(1):F4}  " +
                     $"Mode:{entryModeStr}  SL:{StopLossTicks}t" +
                     (ExitMode == 1 ? $"  Trail:{TrailingStopTicks}t  TP:{TakeProfitTicks}t" : ""),
                     StrategyLoggingLevel.Trading);

            this.waitOpenPosition = true;

            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
            {
                Account     = this.CurrentAccount,
                Symbol      = this.CurrentSymbol,
                OrderTypeId = this.orderTypeId,
                Quantity    = this.Quantity,
                Side        = side,
                // Mode 0 & 2: fixed SL safety net (code manages actual exits)
                // Mode 1: trailing SL + TP bracket
                StopLoss    = ExitMode == 1
                    ? SlTpHolder.CreateSL(TrailingStopTicks, PriceMeasurement.Offset, true)  // trailing SL
                    : SlTpHolder.CreateSL(StopLossTicks, PriceMeasurement.Offset),           // fixed SL
                TakeProfit  = ExitMode == 1
                    ? SlTpHolder.CreateTP(TakeProfitTicks, PriceMeasurement.Offset)
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
                this.prevSide   = side == Side.Buy ? "buy" : "sell";
                this.Log($"{side} position opened", StrategyLoggingLevel.Trading);
            }
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
        }
    }
}
