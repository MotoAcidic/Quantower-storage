using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace goldOrbStrategy
{
    public enum BreakoutMode
    {
        FirstBreakout,
        RetestHigh,
        Retest50Percent,
        CandleClosure
    }

    public enum StopLossMode
    {
        FullOrbRange,
        FiftyPercentOrb,
        FixedPriceDistance,
        FixedTickAmount
    }

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

        // Hard-coded ORB time frame: 8:00-8:05 PM EST (5 minute window)
        private readonly int orbStartHour = 20; // 8 PM EST
        private readonly int orbStartMinute = 0; // :00
        private readonly int orbEndHour = 20; // 8 PM EST  
        private readonly int orbEndMinute = 5; // :05 (5 minute ORB window)

        [InputParameter("Breakout Entry Mode", 7, variants: new object[]
        {
            "First Breakout", BreakoutMode.FirstBreakout,
            "Retest High", BreakoutMode.RetestHigh,
            "Retest 50% Zone", BreakoutMode.Retest50Percent,
            "Candle Closure", BreakoutMode.CandleClosure
        })]
        public BreakoutMode entryMode = BreakoutMode.FirstBreakout;

        [InputParameter("Confirmation Wait Time (minutes)", 8)]
        public int confirmationMinutes = 1;

        [InputParameter("Risk:Reward Ratio", 9)]
        public double riskRewardRatio = 2.0;

        [InputParameter("ORB Buffer Distance (ticks)", 10)]
        public int orbBufferTicks = 0;

        [InputParameter("Enable Trailing Stop", 11)]
        public bool enableTrailingStop = true;

        [InputParameter("Trailing Stop Distance (points)", 12)]
        public double trailingStopDistance = 2.0;

        [InputParameter("Max Daily Trades", 13)]
        public int maxTrades = 10;

        [InputParameter("Daily Profit Target", 14)]
        public int maxProfit = 1000;

        [InputParameter("Daily Loss Limit", 15)]
        public int maxLoss = 500;

        [InputParameter("Use Stop Orders (vs Market)", 16)]
        public bool useStopOrders = true;

        [InputParameter("EST Timezone Offset (hours)", 17)]
        public double estTimezoneOffset = -5.0; // EST is UTC-5 (change to -4 for EDT)

        [InputParameter("Stop Loss Mode", 18, variants: new object[]
        {
            "Full ORB Range", StopLossMode.FullOrbRange,
            "50% ORB Range", StopLossMode.FiftyPercentOrb,
            "Fixed Price Distance", StopLossMode.FixedPriceDistance,
            "Fixed Tick Amount", StopLossMode.FixedTickAmount
        })]
        public StopLossMode stopLossMode = StopLossMode.FullOrbRange;

        [InputParameter("Fixed Stop Loss (Price Distance)", 19)]
        public double fixedStopLossDollar = 5.0;

        [InputParameter("Fixed Stop Loss (Ticks)", 20)]
        public int fixedStopLossTicks = 20;

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

        // Retest tracking
        private bool waitingForRetest = false;
        private double retestLevel = 0.0;
        private string retestDirection = "";

        // Trailing stop tracking
        private double currentTrailingStop = 0.0;
        private bool trailingStopActive = false;
        private double highestProfitPrice = 0.0;
        private double lowestProfitPrice = 0.0;

        public goldOrbStrategy()
            : base()
        {
            this.Name = "Gold ORB Strategy";
            this.Description = "Gold Opening Range Breakout Strategy - 8:00-8:05 PM EST (5 min window)";

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

                // Reset retest tracking
                this.waitingForRetest = false;
                this.retestLevel = 0.0;
                this.retestDirection = "";

                // Reset trailing stop tracking
                this.currentTrailingStop = 0.0;
                this.trailingStopActive = false;
                this.highestProfitPrice = 0.0;
                this.lowestProfitPrice = 0.0;

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
            
            // Convert to EST using configurable offset
            DateTime estTime = currentTime.AddHours(this.estTimezoneOffset);
            DateTime currentDate = estTime.Date;
            
            this.Log($"Current UTC Time: {currentTime:yyyy-MM-dd HH:mm:ss}, EST Time: {estTime:yyyy-MM-dd HH:mm:ss}");

            // Reset ORB on new day
            if (this.lastOrbDate != currentDate)
            {
                this.ResetOrbForNewDay();
                this.lastOrbDate = currentDate;
                this.Log($"New trading day detected: {currentDate:yyyy-MM-dd}");
            }

            // Calculate ORB session times for current day in EST (8:00-8:05 PM)
            this.orbSessionStart = currentDate.AddHours(this.orbStartHour).AddMinutes(this.orbStartMinute);
            this.orbSessionEnd = currentDate.AddHours(this.orbEndHour).AddMinutes(this.orbEndMinute);

            this.Log($"ORB Session Window: {this.orbSessionStart:HH:mm} to {this.orbSessionEnd:HH:mm} EST (5 minute window)");
            
            // Check if we're in ORB session
            bool previousInSession = this.inOrbSession;
            this.inOrbSession = estTime >= this.orbSessionStart && estTime < this.orbSessionEnd;
            
            if (this.inOrbSession != previousInSession)
            {
                if (this.inOrbSession)
                {
                    this.Log($"🟢 ORB SESSION STARTED at {estTime:HH:mm:ss} EST - Capturing 5-minute range...", StrategyLoggingLevel.Trading);
                }
                else
                {
                    this.Log($"🔴 ORB SESSION ENDED at {estTime:HH:mm:ss} EST - CAPTURED RANGE: High: {this.orbHigh}, Low: {this.orbLow}, Size: {this.orbHigh - this.orbLow:F2}", StrategyLoggingLevel.Trading);
                }
            }

            if (!this.inOrbSession && this.orbHigh > double.MinValue && this.orbLow < double.MaxValue)
            {
                if (!this.orbComplete)
                {
                    this.orbComplete = true;
                    this.rangeSize = this.orbHigh - this.orbLow;
                    this.Log($"✅ ORB RANGE READY FOR TRADING - High: {this.orbHigh}, Low: {this.orbLow}, Range: {this.rangeSize:F2} points", StrategyLoggingLevel.Trading);
                }
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
                double previousHigh = this.orbHigh;
                double previousLow = this.orbLow;
                
                this.orbHigh = Math.Max(this.orbHigh, currentHigh);
                this.orbLow = Math.Min(this.orbLow, currentLow);
                
                // Log when range expands
                if (this.orbHigh != previousHigh || this.orbLow != previousLow)
                {
                    this.Log($"📊 ORB RANGE EXPANDING - High: {this.orbHigh} (↑{this.orbHigh - previousHigh:F2}), Low: {this.orbLow} (↓{previousLow - this.orbLow:F2}), Size: {this.orbHigh - this.orbLow:F2}", StrategyLoggingLevel.Trading);
                }
                return;
            }

            // Process breakouts after ORB completion
            if (!this.orbComplete || this.rangeSize <= 0)
            {
                if (!this.orbComplete)
                    this.Log($"Waiting for ORB completion. InSession: {this.inOrbSession}, Complete: {this.orbComplete}");
                else
                    this.Log($"ORB range too small: {this.rangeSize}");
                return;
            }
            
            this.Log($"Processing breakout logic - ORB High: {this.orbHigh}, Low: {this.orbLow}, Current: {currentClose}");
            this.ProcessBreakoutLogic(currentTime, currentClose);
        }

        private void ProcessBreakoutLogic(DateTime currentTime, double currentClose)
        {
            double previousClose = HistoricalDataExtensions.Close(this.hdm, 1);
            double currentHigh = HistoricalDataExtensions.High(this.hdm, 0);
            double currentLow = HistoricalDataExtensions.Low(this.hdm, 0);

            // Detect bullish breakout
            bool bullishBreakout = currentClose > this.orbHigh && previousClose <= this.orbHigh;
            
            // Detect bearish breakout  
            bool bearishBreakout = currentClose < this.orbLow && previousClose >= this.orbLow;

            // Handle strategy modes
            switch (this.entryMode)
            {
                case BreakoutMode.FirstBreakout:
                    this.Log($"First Breakout Mode - Checking breakouts. Bullish: {bullishBreakout}, Bearish: {bearishBreakout}");
                    if (bullishBreakout && !this.buyOrderPlaced)
                    {
                        this.Log($"BULLISH BREAKOUT DETECTED - Price {currentClose} > ORB High {this.orbHigh}");
                        this.PlaceBuyOrder(currentClose);
                    }
                    else if (bearishBreakout && !this.sellOrderPlaced)
                    {
                        this.Log($"BEARISH BREAKOUT DETECTED - Price {currentClose} < ORB Low {this.orbLow}");
                        this.PlaceSellOrder(currentClose);
                    }
                    break;

                case BreakoutMode.RetestHigh:
                    this.HandleRetestHighMode(bullishBreakout, bearishBreakout, currentClose, currentHigh, currentLow);
                    break;

                case BreakoutMode.Retest50Percent:
                    this.HandleRetest50PercentMode(bullishBreakout, bearishBreakout, currentClose, currentHigh, currentLow);
                    break;

                case BreakoutMode.CandleClosure:
                    if (bullishBreakout && !this.bullishBreakoutDetected && !this.buyOrderPlaced)
                    {
                        this.bullishBreakoutDetected = true;
                        this.breakoutTime = currentTime;
                        this.breakoutPrice = currentClose;
                        this.Log($"Bullish breakout detected at {currentClose}. Waiting for confirmation...");
                    }
                    else if (bearishBreakout && !this.bearishBreakoutDetected && !this.sellOrderPlaced)
                    {
                        this.bearishBreakoutDetected = true;
                        this.breakoutTime = currentTime;
                        this.breakoutPrice = currentClose;
                        this.Log($"Bearish breakout detected at {currentClose}. Waiting for confirmation...");
                    }

                    // Check for confirmation
                    this.CheckForConfirmation(currentTime, currentClose);
                    break;
            }

            // Update trailing stop if positions are open
            this.UpdateTrailingStop();
        }

        private void CheckForConfirmation(DateTime currentTime, double currentClose)
        {
            TimeSpan timeSinceBreakout = currentTime - this.breakoutTime;
            
            if (timeSinceBreakout.TotalMinutes >= this.confirmationMinutes)
            {
                if (this.bullishBreakoutDetected && !this.buyOrderPlaced)
                {
                    this.Log($"Bullish breakout confirmed after {this.confirmationMinutes} minute(s). Placing buy order at {currentClose}");
                    this.PlaceBuyOrder(currentClose);
                    this.bullishBreakoutDetected = false;
                }
                else if (this.bearishBreakoutDetected && !this.sellOrderPlaced)
                {
                    this.Log($"Bearish breakout confirmed after {this.confirmationMinutes} minute(s). Placing sell order at {currentClose}");
                    this.PlaceSellOrder(currentClose);
                    this.bearishBreakoutDetected = false;
                }
            }
        }

        private void PlaceBuyOrder(double entryPrice)
        {
            double stopPrice = this.CalculateStopLoss(entryPrice, Side.Buy);
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
            double stopPrice = this.CalculateStopLoss(entryPrice, Side.Sell);
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

            // Reset retest tracking
            this.waitingForRetest = false;
            this.retestLevel = 0.0;
            this.retestDirection = "";

            // Reset trailing stop tracking
            this.currentTrailingStop = 0.0;
            this.trailingStopActive = false;
            this.highestProfitPrice = 0.0;
            this.lowestProfitPrice = 0.0;

            this.Log("ORB reset for new trading day");
        }

        private void HandleRetestHighMode(bool bullishBreakout, bool bearishBreakout, double currentClose, double currentHigh, double currentLow)
        {
            if (!this.waitingForRetest)
            {
                if (bullishBreakout && !this.buyOrderPlaced)
                {
                    this.waitingForRetest = true;
                    this.retestLevel = this.orbHigh;
                    this.retestDirection = "Long";
                    this.Log($"Bullish breakout detected. Waiting for retest of ORB high: {this.orbHigh}");
                }
                else if (bearishBreakout && !this.sellOrderPlaced)
                {
                    this.waitingForRetest = true;
                    this.retestLevel = this.orbLow;
                    this.retestDirection = "Short";
                    this.Log($"Bearish breakout detected. Waiting for retest of ORB low: {this.orbLow}");
                }
            }
            else
            {
                if (this.retestDirection == "Long" && currentLow <= this.retestLevel)
                {
                    this.PlaceBuyOrder(currentClose);
                    this.waitingForRetest = false;
                    this.Log($"Retest of ORB high completed. Entering long at {currentClose}");
                }
                else if (this.retestDirection == "Short" && currentHigh >= this.retestLevel)
                {
                    this.PlaceSellOrder(currentClose);
                    this.waitingForRetest = false;
                    this.Log($"Retest of ORB low completed. Entering short at {currentClose}");
                }
            }
        }

        private void HandleRetest50PercentMode(bool bullishBreakout, bool bearishBreakout, double currentClose, double currentHigh, double currentLow)
        {
            if (!this.waitingForRetest)
            {
                if (bullishBreakout && !this.buyOrderPlaced)
                {
                    this.waitingForRetest = true;
                    this.retestLevel = this.orbHigh + ((currentClose - this.orbHigh) * 0.5);
                    this.retestDirection = "Long";
                    this.Log($"Bullish breakout detected. Waiting for 50% retest to: {this.retestLevel}");
                }
                else if (bearishBreakout && !this.sellOrderPlaced)
                {
                    this.waitingForRetest = true;
                    this.retestLevel = this.orbLow + ((currentClose - this.orbLow) * 0.5);
                    this.retestDirection = "Short";
                    this.Log($"Bearish breakout detected. Waiting for 50% retest to: {this.retestLevel}");
                }
            }
            else
            {
                if (this.retestDirection == "Long" && currentLow <= this.retestLevel)
                {
                    this.PlaceBuyOrder(currentClose);
                    this.waitingForRetest = false;
                    this.Log($"50% retest completed. Entering long at {currentClose}");
                }
                else if (this.retestDirection == "Short" && currentHigh >= this.retestLevel)
                {
                    this.PlaceSellOrder(currentClose);
                    this.waitingForRetest = false;
                    this.Log($"50% retest completed. Entering short at {currentClose}");
                }
            }
        }

        private void UpdateTrailingStop()
        {
            if (!this.enableTrailingStop)
                return;

            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            
            if (positions.Length == 0)
            {
                // No positions, reset trailing stop
                this.trailingStopActive = false;
                this.currentTrailingStop = 0.0;
                this.highestProfitPrice = 0.0;
                this.lowestProfitPrice = 0.0;
                return;
            }

            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);

            foreach (var position in positions)
            {
                if (position.Side == Side.Buy)
                {
                    // Long position - track highest price for trailing stop
                    if (currentPrice > this.highestProfitPrice)
                    {
                        this.highestProfitPrice = currentPrice;
                        this.currentTrailingStop = currentPrice - this.trailingStopDistance;
                        this.trailingStopActive = true;
                        this.Log($"Updated trailing stop for LONG: {this.currentTrailingStop}");
                    }
                    
                    // Check if price hits trailing stop
                    if (this.trailingStopActive && currentPrice <= this.currentTrailingStop)
                    {
                        this.ClosePosition(position, "Trailing Stop Hit");
                    }
                }
                else if (position.Side == Side.Sell)
                {
                    // Short position - track lowest price for trailing stop
                    if (this.lowestProfitPrice == 0.0 || currentPrice < this.lowestProfitPrice)
                    {
                        this.lowestProfitPrice = currentPrice;
                        this.currentTrailingStop = currentPrice + this.trailingStopDistance;
                        this.trailingStopActive = true;
                        this.Log($"Updated trailing stop for SHORT: {this.currentTrailingStop}");
                    }
                    
                    // Check if price hits trailing stop
                    if (this.trailingStopActive && currentPrice >= this.currentTrailingStop)
                    {
                        this.ClosePosition(position, "Trailing Stop Hit");
                    }
                }
            }
        }

        private void ClosePosition(Position position, string reason)
        {
            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                Side = position.Side == Side.Buy ? Side.Sell : Side.Buy,
                OrderTypeId = OrderType.Market,
                Quantity = position.Quantity
            });

            if (result.Status == TradingOperationResultStatus.Success)
            {
                this.Log($"Position closed: {reason} at {HistoricalDataExtensions.Close(this.hdm, 0)}");
            }
            else
            {
                this.Log($"Failed to close position: {result.Message}", StrategyLoggingLevel.Error);
            }
        }
        private double CalculateStopLoss(double entryPrice, Side side)
        {
            double stopPrice;
            string stopDescription;

            switch (this.stopLossMode)
            {
                case StopLossMode.FullOrbRange:
                    if (side == Side.Buy)
                    {
                        stopPrice = this.orbLow - (this.orbBufferTicks * 0.25);
                        stopDescription = "Full ORB Range (ORB Low)";
                    }
                    else
                    {
                        stopPrice = this.orbHigh + (this.orbBufferTicks * 0.25);
                        stopDescription = "Full ORB Range (ORB High)";
                    }
                    break;

                case StopLossMode.FiftyPercentOrb:
                    double orbMidpoint = (this.orbHigh + this.orbLow) / 2.0;
                    if (side == Side.Buy)
                    {
                        stopPrice = orbMidpoint - (this.orbBufferTicks * 0.25);
                        stopDescription = "50% ORB Range (Midpoint)";
                    }
                    else
                    {
                        stopPrice = orbMidpoint + (this.orbBufferTicks * 0.25);
                        stopDescription = "50% ORB Range (Midpoint)";
                    }
                    break;

                case StopLossMode.FixedPriceDistance:
                    if (side == Side.Buy)
                    {
                        stopPrice = entryPrice - this.fixedStopLossDollar;
                        stopDescription = $"Fixed Price Distance ({this.fixedStopLossDollar} points)";
                    }
                    else
                    {
                        stopPrice = entryPrice + this.fixedStopLossDollar;
                        stopDescription = $"Fixed Price Distance ({this.fixedStopLossDollar} points)";
                    }
                    break;

                case StopLossMode.FixedTickAmount:
                    if (side == Side.Buy)
                    {
                        stopPrice = entryPrice - (this.fixedStopLossTicks * this.CurrentSymbol.TickSize);
                        stopDescription = $"Fixed Tick Amount ({this.fixedStopLossTicks} ticks)";
                    }
                    else
                    {
                        stopPrice = entryPrice + (this.fixedStopLossTicks * this.CurrentSymbol.TickSize);
                        stopDescription = $"Fixed Tick Amount ({this.fixedStopLossTicks} ticks)";
                    }
                    break;

                default:
                    // Fallback to full ORB range
                    if (side == Side.Buy)
                    {
                        stopPrice = this.orbLow - (this.orbBufferTicks * 0.25);
                        stopDescription = "Default Full ORB Range";
                    }
                    else
                    {
                        stopPrice = this.orbHigh + (this.orbBufferTicks * 0.25);
                        stopDescription = "Default Full ORB Range";
                    }
                    break;
            }

            this.Log($"Stop Loss Mode: {stopDescription} - Stop Price: {stopPrice}");
            return stopPrice;
        }
        private void ProcessTradingRefuse()
        {
            this.Log("Strategy received refuse for trading action. Stopping strategy.", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}