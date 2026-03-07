using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace goldOrbStrategy
{
    public sealed class goldOrbStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        /// <summary>
        /// Account to place orders
        /// </summary>
        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Quantity")]
        public int Quantity = 1;

        /// <summary>
        /// Period to load history
        /// </summary>
        [InputParameter("Period", 5)]
        public Period Period { get; set; }

        /// <summary>
        /// Start point to load history
        /// </summary>
        [InputParameter("Start point", 6)]
        public DateTime StartPoint { get; set; }

        [InputParameter("ORB Session Start Hour (24h format)")]
        public int orbSessionStartHour = 20; // 8 PM

        [InputParameter("ORB Session Start Minute")]
        public int orbSessionStartMinute = 0; // 8:00 PM

        [InputParameter("ORB Session End Hour (24h format)")]
        public int orbSessionEndHour = 20; // 8 PM

        [InputParameter("ORB Session End Minute")]
        public int orbSessionEndMinute = 5; // 8:05 PM

        [InputParameter("Strategy Mode")]
        public string strategyMode = "Confirmed Breakout"; // Options: "Aggressive Breakout", "Confirmed Breakout"

        [InputParameter("Confirmation Candle Minutes")]
        public int confirmationCandleMinutes = 1; // Minutes for confirmation candle

        [InputParameter("Risk Reward Ratio")]
        public double riskRewardRatio = 2.0;

        [InputParameter("Range Offset (in ticks)")]
        public int rangeOffsetTicks = 0;

        [InputParameter("Max Trades")]
        public int maxTrades = 10;

        [InputParameter("Max Profit")]
        public int maxProfit = 1000;

        [InputParameter("Max Loss")]
        public int maxLoss = 500;

        [InputParameter("Use Stop Orders")]
        public bool useStopOrders = true;

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        private HistoricalData hdm;
        private string orderTypeId;

        private int longPositionsCount;
        private int shortPositionsCount;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        private int tradeCounter = 0;

        // ORB specific variables
        private double orbHigh = double.MinValue;
        private double orbLow = double.MaxValue;
        private bool orbComplete = false;
        private bool inOrbSession = false;
        private DateTime orbSessionStart;
        private DateTime orbSessionEnd;
        private DateTime lastOrbDate = DateTime.MinValue;
        private double rangeSize = 0.0;

        // Breakout tracking
        private bool bullishBreakoutDetected = false;
        private bool bearishBreakoutDetected = false;
        private DateTime breakoutTime = DateTime.MinValue;
        private double breakoutPrice = 0.0;

        // Position state tracking
        private bool buyOrderPlaced = false;
        private bool sellOrderPlaced = false;

        public goldOrbStrategy()
            : base()
        {
            this.Name = "Gold ORB Strategy";
            this.Description = "Gold Opening Range Breakout Strategy with confirmation candles";

            this.Period = Period.SECOND30;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-1);
        }

        protected override void OnRun()
        {
            this.totalNetPl = 0D;

            // Restore symbol object from active connection
            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
            {
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());
            }

            if (this.CurrentSymbol == null)
            {
                this.Log("Incorrect input parameters... Symbol have not specified.", StrategyLoggingLevel.Error);
                return;
            }

            // Restore account object from active connection
            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
            {
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());
            }

            if (this.CurrentAccount == null)
            {
                this.Log("Incorrect input parameters... Account have not specified.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
            {
                this.Log("Incorrect input parameters... Symbol and Account from different connections.", StrategyLoggingLevel.Error);
                return;
            }

            // Get appropriate order type
            if (this.useStopOrders)
            {
                this.orderTypeId = Core.OrderTypes.FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Stop)?.Id;
            }
            else
            {
                this.orderTypeId = Core.OrderTypes.FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market)?.Id;
            }

            if (string.IsNullOrEmpty(this.orderTypeId))
            {
                this.Log($"Connection of selected symbol does not support {(this.useStopOrders ? "stop" : "market")} orders", StrategyLoggingLevel.Error);
                return;
            }

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;
        }

        protected override void OnStop()
        {
            Core.PositionAdded -= this.Core_PositionAdded;
            Core.PositionRemoved -= this.Core_PositionRemoved;
            Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
            Core.TradeAdded -= this.Core_TradeAdded;

            if (this.hdm != null)
            {
                this.hdm.HistoryItemUpdated -= this.Hdm_HistoryItemUpdated;
                this.hdm.NewHistoryItem -= this.Hdm_OnNewHistoryItem;
                this.hdm.Dispose();
            }

            base.OnStop();
        }

        protected override void OnInitializeMetrics(Meter meter)
        {
            base.OnInitializeMetrics(meter);

            meter.CreateObservableCounter("total-long-positions", () => this.longPositionsCount, description: "Total long positions");
            meter.CreateObservableCounter("total-short-positions", () => this.shortPositionsCount, description: "Total short positions");
            meter.CreateObservableCounter("total-pl-net", () => this.totalNetPl, description: "Total Net profit/loss");
            meter.CreateObservableCounter("total-pl-gross", () => this.totalGrossPl, description: "Total Gross profit/loss");
            meter.CreateObservableCounter("total-fee", () => this.totalFee, description: "Total fee");
            meter.CreateObservableCounter("trade-count", () => this.tradeCounter, description: "Trade Count");
            meter.CreateObservableGauge("orb-high", () => this.orbHigh, description: "Opening Range High");
            meter.CreateObservableGauge("orb-low", () => this.orbLow, description: "Opening Range Low");
        }

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double currentPositionsQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);

            this.tradeCounter += 1;
            this.Log($"Position added. Trade count: {this.tradeCounter}");
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            var orders = Core.Instance.Orders.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (positions.Length == 0)
            {
                this.buyOrderPlaced = false;
                this.sellOrderPlaced = false;

                // Cancel any pending orders
                foreach (var order in orders)
                {
                    var result = order.Cancel();
                }

                this.Log("All positions closed. Ready for new signals.");
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount)
            {
                return;
            }

            if (obj.Status == OrderStatus.Refused)
            {
                this.ProcessTradingRefuse();
            }
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.NetPnl != null)
            {
                this.totalNetPl += obj.NetPnl.Value;
            }

            if (obj.GrossPnl != null)
            {
                this.totalGrossPl += obj.GrossPnl.Value;
            }

            if (obj.Fee != null)
            {
                this.totalFee += obj.Fee.Value;
            }
        }

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.ProcessOrbLogic();
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            this.OnUpdate();
        }

        private void OnUpdate()
        {
            this.UpdateOrbSession();
            this.ProcessOrbLogic();
        }

        private void UpdateOrbSession()
        {
            DateTime currentTime = HistoricalDataExtensions.Time(this.hdm, 0);
            DateTime currentDate = currentTime.Date;

            // Reset ORB on new day
            if (this.lastOrbDate != currentDate)
            {
                this.ResetOrbForNewDay();
                this.lastOrbDate = currentDate;
            }

            // Calculate ORB session times for current day
            this.orbSessionStart = currentDate.AddHours(this.orbSessionStartHour).AddMinutes(this.orbSessionStartMinute);
            this.orbSessionEnd = currentDate.AddHours(this.orbSessionEndHour).AddMinutes(this.orbSessionEndMinute);

            // Handle session spanning midnight
            if (this.orbSessionEnd <= this.orbSessionStart)
            {
                this.orbSessionEnd = this.orbSessionEnd.AddDays(1);
            }

            // Check if we're in ORB session
            this.inOrbSession = currentTime >= this.orbSessionStart && currentTime <= this.orbSessionEnd;

            if (!this.inOrbSession && this.orbHigh > double.MinValue && this.orbLow < double.MaxValue)
            {
                this.orbComplete = true;
                this.rangeSize = this.orbHigh - this.orbLow;
            }
        }

        private void ProcessOrbLogic()
        {
            // Safety checks
            if (this.tradeCounter >= this.maxTrades)
                return;

            if (this.totalGrossPl >= this.maxProfit || this.totalGrossPl <= -this.maxLoss)
                return;

            if (this.buyOrderPlaced && this.sellOrderPlaced)
                return;

            DateTime currentTime = HistoricalDataExtensions.Time(this.hdm, 0);
            double currentHigh = HistoricalDataExtensions.High(this.hdm, 0);
            double currentLow = HistoricalDataExtensions.Low(this.hdm, 0);
            double currentClose = HistoricalDataExtensions.Close(this.hdm, 0);

            // Calculate opening range during session
            if (this.inOrbSession)
            {
                this.orbHigh = Math.Max(this.orbHigh, currentHigh);
                this.orbLow = Math.Min(this.orbLow, currentLow);
                this.Log($"ORB Session Active - High: {this.orbHigh}, Low: {this.orbLow}");
                return;
            }

            // Process breakouts after ORB completion
            if (!this.orbComplete || this.rangeSize <= 0)
                return;

            this.ProcessBreakoutLogic(currentTime, currentClose);
        }

        private void ProcessBreakoutLogic(DateTime currentTime, double currentClose)
        {
            double previousClose = HistoricalDataExtensions.Close(this.hdm, 1);

            // Detect bullish breakout
            bool bullishBreakout = currentClose > this.orbHigh && previousClose <= this.orbHigh;
            
            // Detect bearish breakout  
            bool bearishBreakout = currentClose < this.orbLow && previousClose >= this.orbLow;

            // Handle strategy modes
            if (this.strategyMode == "Aggressive Breakout")
            {
                if (bullishBreakout && !this.buyOrderPlaced)
                {
                    this.PlaceBuyOrder(currentClose);
                }
                else if (bearishBreakout && !this.sellOrderPlaced)
                {
                    this.PlaceSellOrder(currentClose);
                }
            }
            else if (this.strategyMode == "Confirmed Breakout")
            {
                // For confirmed breakouts, wait for confirmation candle
                if (bullishBreakout && !this.bullishBreakoutDetected)
                {
                    this.bullishBreakoutDetected = true;
                    this.breakoutTime = currentTime;
                    this.breakoutPrice = currentClose;
                    this.Log($"Bullish breakout detected at {currentClose}. Waiting for confirmation...");
                }
                else if (bearishBreakout && !this.bearishBreakoutDetected)
                {
                    this.bearishBreakoutDetected = true;
                    this.breakoutTime = currentTime;
                    this.breakoutPrice = currentClose;
                    this.Log($"Bearish breakout detected at {currentClose}. Waiting for confirmation...");
                }

                // Check for confirmation
                this.CheckForConfirmation(currentTime, currentClose);
            }
        }

        private void CheckForConfirmation(DateTime currentTime, double currentClose)
        {
            TimeSpan timeSinceBreakout = currentTime - this.breakoutTime;
            
            if (timeSinceBreakout.TotalMinutes >= this.confirmationCandleMinutes)
            {
                if (this.bullishBreakoutDetected && currentClose > this.orbHigh && !this.buyOrderPlaced)
                {
                    this.PlaceBuyOrder(currentClose);
                    this.bullishBreakoutDetected = false;
                }
                else if (this.bearishBreakoutDetected && currentClose < this.orbLow && !this.sellOrderPlaced)
                {
                    this.PlaceSellOrder(currentClose);
                    this.bearishBreakoutDetected = false;
                }
                else
                {
                    // Confirmation failed, reset breakout detection
                    this.bullishBreakoutDetected = false;
                    this.bearishBreakoutDetected = false;
                    this.Log("Breakout confirmation failed. Reset signals.");
                }
            }
        }

        private void PlaceBuyOrder(double entryPrice)
        {
            double stopPrice = this.orbLow - (this.rangeOffsetTicks * 0.25);
            double riskAmount = entryPrice - stopPrice;
            double targetPrice = entryPrice + (riskAmount * this.riskRewardRatio);

            this.Log($"Placing Buy Order - Entry: {entryPrice}, Stop: {stopPrice}, Target: {targetPrice}");

            var orderParams = new PlaceOrderRequestParameters()
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                Quantity = this.Quantity,
                Side = Side.Buy,
                StopLoss = SlTpHolder.CreateSL(stopPrice, PriceMeasurement.Absolute),
                TakeProfit = SlTpHolder.CreateTP(targetPrice, PriceMeasurement.Absolute)
            };

            if (this.useStopOrders)
            {
                orderParams.OrderTypeId = OrderType.Stop;
                orderParams.TriggerPrice = entryPrice;
            }
            else
            {
                orderParams.OrderTypeId = OrderType.Market;
            }

            var result = Core.Instance.PlaceOrder(orderParams);

            if (result.Status == TradingOperationResultStatus.Failure)
            {
                this.Log($"Buy order failed: {result.Message ?? result.Status.ToString()}", StrategyLoggingLevel.Trading);
                this.ProcessTradingRefuse();
            }
            else
            {
                this.Log($"Buy order placed successfully: {result.Status}", StrategyLoggingLevel.Trading);
                this.buyOrderPlaced = true;
            }
        }

        private void PlaceSellOrder(double entryPrice)
        {
            double stopPrice = this.orbHigh + (this.rangeOffsetTicks * 0.25);
            double riskAmount = stopPrice - entryPrice;
            double targetPrice = entryPrice - (riskAmount * this.riskRewardRatio);

            this.Log($"Placing Sell Order - Entry: {entryPrice}, Stop: {stopPrice}, Target: {targetPrice}");

            var orderParams = new PlaceOrderRequestParameters()
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                Quantity = this.Quantity,
                Side = Side.Sell,
                StopLoss = SlTpHolder.CreateSL(stopPrice, PriceMeasurement.Absolute),
                TakeProfit = SlTpHolder.CreateTP(targetPrice, PriceMeasurement.Absolute)
            };

            if (this.useStopOrders)
            {
                orderParams.OrderTypeId = OrderType.Stop;
                orderParams.TriggerPrice = entryPrice;
            }
            else
            {
                orderParams.OrderTypeId = OrderType.Market;
            }

            var result = Core.Instance.PlaceOrder(orderParams);

            if (result.Status == TradingOperationResultStatus.Failure)
            {
                this.Log($"Sell order failed: {result.Message ?? result.Status.ToString()}", StrategyLoggingLevel.Trading);
                this.ProcessTradingRefuse();
            }
            else
            {
                this.Log($"Sell order placed successfully: {result.Status}", StrategyLoggingLevel.Trading);
                this.sellOrderPlaced = true;
            }
        }

        private void ResetOrbForNewDay()
        {
            this.orbHigh = double.MinValue;
            this.orbLow = double.MaxValue;
            this.orbComplete = false;
            this.inOrbSession = false;
            this.rangeSize = 0.0;
            
            this.bullishBreakoutDetected = false;
            this.bearishBreakoutDetected = false;
            this.breakoutTime = DateTime.MinValue;
            this.breakoutPrice = 0.0;
            
            this.buyOrderPlaced = false;
            this.sellOrderPlaced = false;

            this.Log("ORB reset for new trading day");
        }

        private void ProcessTradingRefuse()
        {
            this.Log("Strategy received refuse for trading action. Stopping strategy.", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}