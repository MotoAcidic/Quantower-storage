using System;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace esOrbStrategy
{
    public enum EntryMode
    {
        [Description("Immediate Breakout")]
        ImmediateBreakout,

        [Description("Wait for 50% Retest")]
        WaitFor50PercentRetest,

        [Description("Wait for Any Retest")]
        WaitForAnyRetest
    }

    public enum StopLossMode
    {
        [Description("Full Range")]
        FullRange,

        [Description("50% Range")]
        FiftyPercentRange,

        [Description("Fixed Points")]
        FixedPoints
    }

    public class esOrbStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        [InputParameter("Account", 0)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Symbol", 1)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Period", 2)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 3)]
        public DateTime StartPoint { get; set; }

        [InputParameter("Entry Mode", 4, variants: new object[]
        {
            "Immediate Breakout", EntryMode.ImmediateBreakout,
            "Wait for 50% Retest", EntryMode.WaitFor50PercentRetest,
            "Wait for Any Retest", EntryMode.WaitForAnyRetest,
        })]
        public EntryMode SelectedEntryMode = EntryMode.ImmediateBreakout;

        [InputParameter("Stop Loss Mode", 5, variants: new object[]
        {
            "Full Range", StopLossMode.FullRange,
            "50% Range", StopLossMode.FiftyPercentRange,
            "Fixed Points", StopLossMode.FixedPoints,
        })]
        public StopLossMode SelectedStopLossMode = StopLossMode.FullRange;

        [InputParameter("Risk Reward Ratio", 6)]
        public double RiskRewardRatio = 2.0;

        [InputParameter("Minimum Breakout Distance (Points)", 7)]
        public double MinBreakoutDistance = 1.0;

        [InputParameter("Minimum Volume Before Market Open", 8)]
        public double MinVolumeBeforeOpen = 1000.0;

        [InputParameter("Fixed Stop Loss (Points)", 9)]
        public double FixedStopLoss = 5.0;

        [InputParameter("1 Lot Position Size", 10)]
        public double PositionSize = 1;

        [InputParameter("Max Daily Trades", 11)]
        public int MaxTrades = 5;

        [InputParameter("Max Daily Profit ($)", 12)]
        public double MaxProfit = 1000;

        [InputParameter("Max Daily Loss ($)", 13)]
        public double MaxLoss = 500;

        [InputParameter("Show Midpoint Line", 14)]
        public bool ShowMidpointLine = true;

        [InputParameter("Enable Trailing Stop", 15)]
        public bool enableTrailingStop = true;

        [InputParameter("Trailing Stop Distance (points)", 16)]
        public double trailingStopDistance = 2.0;

        [InputParameter("Use Fixed Profit Target", 17)]
        public bool useFixedProfitTarget = false;

        [InputParameter("Profit Target (points)", 18)]
        public double profitTargetPoints = 15.0;

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        private HistoricalData hdm;

        // ES ORB specific variables
        private double orbHigh = double.MinValue;
        private double orbLow = double.MaxValue;
        private double orbMidpoint;
        private bool orbSessionActive;
        private bool orbCaptured;
        private bool waitingForRetest;
        private double retestLevel;
        private string lastTradeDirection = "";

        // Risk Management
        private int tradeCount = 0;
        private double totalPnL = 0;
        private double totalNetPl = 0;
        private double totalGrossPl = 0;
        private double totalFee = 0;
        private int tradeCounter = 0;
        private int longPositionsCount = 0;
        private int shortPositionsCount = 0;

        // Trailing stop tracking
        private double currentTrailingStop = 0.0;
        private bool trailingStopActive = false;
        private double highestProfitPrice = 0.0;
        private double lowestProfitPrice = 0.0;

        // Session times constants (EST) - Fixed for ES ORB: 8:00-8:15 AM
        private readonly int orbStartHour = 8;
        private readonly int orbStartMinute = 0;
        private readonly int orbEndHour = 8;
        private readonly int orbEndMinute = 15;
        private readonly int marketOpenHour = 9;
        private readonly int marketOpenMinute = 30;

        public esOrbStrategy()
            : base()
        {
            this.Name = "ES ORB Strategy";
            this.Description = "E-mini S&P 500 Opening Range Breakout Strategy";

            this.Period = Period.SECOND30;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-1);
        }

        protected override void OnRun()
        {
            this.totalPnL = 0D;

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

            // Get historical data
            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            // Subscribe to events
            Core.Instance.PositionAdded += this.Core_PositionAdded;
            Core.Instance.PositionRemoved += this.Core_PositionRemoved;
            Core.Instance.TradeAdded += this.Core_TradeAdded;
            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

            this.InitializeOrbSession();
        }

        protected override void OnStop()
        {
            // Unsubscribe from events
            if (this.hdm != null)
            {
                this.hdm.HistoryItemUpdated -= this.Hdm_HistoryItemUpdated;
                this.hdm.NewHistoryItem -= this.Hdm_OnNewHistoryItem;
            }

            Core.Instance.PositionAdded -= this.Core_PositionAdded;
            Core.Instance.PositionRemoved -= this.Core_PositionRemoved;
            Core.Instance.TradeAdded -= this.Core_TradeAdded;
            Core.TradeAdded -= this.Core_TradeAdded;

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
            meter.CreateObservableGauge("orb-range", () => this.orbHigh != double.MinValue && this.orbLow != double.MaxValue ? this.orbHigh - this.orbLow : 0, description: "ORB Range Size");
        }

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            this.tradeCount += 1;
            this.tradeCounter += 1;
            this.Log($"Position added. Trade count: {this.tradeCount}");
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            var orders = Core.Instance.Orders.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (positions.Length == 0)
            {
                waitingForRetest = false;
                lastTradeDirection = "";

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

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount)
                return;
            
            if (obj.NetPnl != null)
            {
                this.totalPnL += obj.NetPnl.Value;
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

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            this.OnUpdate();
        }

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.OnUpdate();
        }

        private void OnUpdate()
        {
            this.CheckOrbSession();

            if (orbCaptured && !HasActivePositions() && !IsMaxTradesReached() && !IsMaxPnLExceeded())
            {
                this.CheckForEntry();
            }

            // Update trailing stop and check profit targets for active positions
            if (HasActivePositions())
            {
                UpdateTrailingStop();
                CheckProfitTargets();
            }
        }

        private void InitializeOrbSession()
        {
            orbHigh = double.MinValue;
            orbLow = double.MaxValue;
            orbSessionActive = false;
            orbCaptured = false;
            waitingForRetest = false;
            retestLevel = 0;
        }

        private void CheckOrbSession()
        {
            DateTime currentTime = HistoricalDataExtensions.Time(this.hdm, 0);
            var currentHour = currentTime.Hour;
            var currentMinute = currentTime.Minute;

            // Check if we're in ORB session (8:00-8:15 AM EST)
            bool inOrbWindow = (currentHour == orbStartHour && currentMinute >= orbStartMinute) &&
                              (currentHour < orbEndHour || (currentHour == orbEndHour && currentMinute < orbEndMinute));

            if (inOrbWindow && !orbSessionActive)
            {
                orbSessionActive = true;
                orbCaptured = false;
                orbHigh = HistoricalDataExtensions.High(this.hdm, 0);
                orbLow = HistoricalDataExtensions.Low(this.hdm, 0);
                this.Log($"ORB session started. Initial high: {orbHigh}, low: {orbLow}");
            }
            else if (inOrbWindow && orbSessionActive)
            {
                // Update ORB levels during session
                var currentHigh = HistoricalDataExtensions.High(this.hdm, 0);
                var currentLow = HistoricalDataExtensions.Low(this.hdm, 0);

                if (currentHigh > orbHigh)
                    orbHigh = currentHigh;
                if (currentLow < orbLow)
                    orbLow = currentLow;
            }
            else if (!inOrbWindow && orbSessionActive)
            {
                // ORB session ended
                orbSessionActive = false;
                orbCaptured = true;
                orbMidpoint = (orbHigh + orbLow) / 2.0;
                this.Log($"ORB captured! High: {orbHigh}, Low: {orbLow}, Midpoint: {orbMidpoint}, Range: {orbHigh - orbLow}");
            }
        }

        private void CheckForEntry()
        {
            var close = HistoricalDataExtensions.Close(this.hdm, 0);
            var high = HistoricalDataExtensions.High(this.hdm, 0);
            var low = HistoricalDataExtensions.Low(this.hdm, 0);
            var currentTime = HistoricalDataExtensions.Time(this.hdm, 0);

            bool bullishBreakout = high > (orbHigh + MinBreakoutDistance);
            bool bearishBreakout = low < (orbLow - MinBreakoutDistance);

            // Check volume requirement before market open
            bool hasVolume = true;
            if (currentTime.Hour < marketOpenHour || (currentTime.Hour == marketOpenHour && currentTime.Minute < marketOpenMinute))
            {
                var currentVolume = HistoricalDataExtensions.Volume(this.hdm, 0);
                hasVolume = currentVolume >= MinVolumeBeforeOpen;
            }

            switch (SelectedEntryMode)
            {
                case EntryMode.ImmediateBreakout:
                    HandleImmediateBreakout(bullishBreakout, bearishBreakout, close, hasVolume);
                    break;

                case EntryMode.WaitFor50PercentRetest:
                    Handle50PercentRetest(bullishBreakout, bearishBreakout, close, high, low, hasVolume);
                    break;

                case EntryMode.WaitForAnyRetest:
                    HandleAnyRetest(bullishBreakout, bearishBreakout, close, high, low, hasVolume);
                    break;
            }
        }

        private void HandleImmediateBreakout(bool bullishBreakout, bool bearishBreakout, double close, bool hasVolume)
        {
            if (bullishBreakout && hasVolume && lastTradeDirection != "Long")
            {
                PlaceLongTrade(close);
            }
            else if (bearishBreakout && hasVolume && lastTradeDirection != "Short")
            {
                PlaceShortTrade(close);
            }
        }

        private void Handle50PercentRetest(bool bullishBreakout, bool bearishBreakout, double close, double high, double low, bool hasVolume)
        {
            if (!waitingForRetest)
            {
                if (bullishBreakout && hasVolume)
                {
                    waitingForRetest = true;
                    retestLevel = orbHigh + (close - orbHigh) * 0.5;
                    lastTradeDirection = "WaitingLong";
                    this.Log($"Bullish breakout detected. Waiting for 50% retest to {retestLevel}");
                }
                else if (bearishBreakout && hasVolume)
                {
                    waitingForRetest = true;
                    retestLevel = orbLow + (close - orbLow) * 0.5;
                    lastTradeDirection = "WaitingShort";
                    this.Log($"Bearish breakout detected. Waiting for 50% retest to {retestLevel}");
                }
            }
            else
            {
                if (lastTradeDirection == "WaitingLong" && low <= retestLevel)
                {
                    PlaceLongTrade(close);
                    waitingForRetest = false;
                }
                else if (lastTradeDirection == "WaitingShort" && high >= retestLevel)
                {
                    PlaceShortTrade(close);
                    waitingForRetest = false;
                }
            }
        }

        private void HandleAnyRetest(bool bullishBreakout, bool bearishBreakout, double close, double high, double low, bool hasVolume)
        {
            if (!waitingForRetest)
            {
                if (bullishBreakout && hasVolume)
                {
                    waitingForRetest = true;
                    retestLevel = orbHigh;
                    lastTradeDirection = "WaitingLong";
                    this.Log($"Bullish breakout detected. Waiting for any retest to {retestLevel}");
                }
                else if (bearishBreakout && hasVolume)
                {
                    waitingForRetest = true;
                    retestLevel = orbLow;
                    lastTradeDirection = "WaitingShort";
                    this.Log($"Bearish breakout detected. Waiting for any retest to {retestLevel}");
                }
            }
            else
            {
                if (lastTradeDirection == "WaitingLong" && low <= retestLevel)
                {
                    PlaceLongTrade(close);
                    waitingForRetest = false;
                }
                else if (lastTradeDirection == "WaitingShort" && high >= retestLevel)
                {
                    PlaceShortTrade(close);
                    waitingForRetest = false;
                }
            }
        }

        private void PlaceLongTrade(double entryPrice)
        {
            double stopLoss = CalculateStopLoss(entryPrice, true);
            double takeProfit = entryPrice + (entryPrice - stopLoss) * RiskRewardRatio;

            var orderParams = new PlaceOrderRequestParameters()
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                Side = Side.Buy,
                OrderTypeId = OrderType.Market,
                Quantity = PositionSize,
                TimeInForce = TimeInForce.GTC,
                StopLoss = SlTpHolder.CreateSL(stopLoss, PriceMeasurement.Absolute),
                TakeProfit = SlTpHolder.CreateTP(takeProfit, PriceMeasurement.Absolute)
            };

            var result = Core.Instance.PlaceOrder(orderParams);

            if (result.Status == TradingOperationResultStatus.Success)
            {
                lastTradeDirection = "Long";
                this.Log($"Long trade placed at {entryPrice}. SL: {stopLoss}, TP: {takeProfit}");
            }
            else
            {
                this.Log($"Failed to place long trade: {result.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void PlaceShortTrade(double entryPrice)
        {
            double stopLoss = CalculateStopLoss(entryPrice, false);
            double takeProfit = entryPrice - (stopLoss - entryPrice) * RiskRewardRatio;

            var orderParams = new PlaceOrderRequestParameters()
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                Side = Side.Sell,
                OrderTypeId = OrderType.Market,
                Quantity = PositionSize,
                TimeInForce = TimeInForce.GTC,
                StopLoss = SlTpHolder.CreateSL(stopLoss, PriceMeasurement.Absolute),
                TakeProfit = SlTpHolder.CreateTP(takeProfit, PriceMeasurement.Absolute)
            };

            var result = Core.Instance.PlaceOrder(orderParams);

            if (result.Status == TradingOperationResultStatus.Success)
            {
                lastTradeDirection = "Short";
                this.Log($"Short trade placed at {entryPrice}. SL: {stopLoss}, TP: {takeProfit}");
            }
            else
            {
                this.Log($"Failed to place short trade: {result.Message}", StrategyLoggingLevel.Error);
            }
        }

        private double CalculateStopLoss(double entryPrice, bool isLong)
        {
            switch (SelectedStopLossMode)
            {
                case StopLossMode.FullRange:
                    return isLong ? orbLow : orbHigh;

                case StopLossMode.FiftyPercentRange:
                    double halfRange = (orbHigh - orbLow) * 0.5;
                    return isLong ? entryPrice - halfRange : entryPrice + halfRange;

                case StopLossMode.FixedPoints:
                    return isLong ? entryPrice - FixedStopLoss : entryPrice + FixedStopLoss;

                default:
                    return isLong ? orbLow : orbHigh;
            }
        }

        private bool HasActivePositions()
        {
            return Core.Instance.Positions.Any(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount);
        }

        private bool IsMaxTradesReached()
        {
            return tradeCount >= MaxTrades;
        }

        private bool IsMaxPnLExceeded()
        {
            return totalPnL >= MaxProfit || totalPnL <= -MaxLoss;
        }

        private void UpdateTrailingStop()
        {
            if (!enableTrailingStop)
                return;

            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            
            if (positions.Length == 0)
            {
                // No positions, reset trailing stop
                trailingStopActive = false;
                currentTrailingStop = 0.0;
                highestProfitPrice = 0.0;
                lowestProfitPrice = 0.0;
                return;
            }

            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);

            foreach (var position in positions)
            {
                if (position.Side == Side.Buy)
                {
                    // Long position - track highest price for trailing stop
                    if (highestProfitPrice == 0.0 || currentPrice > highestProfitPrice)
                    {
                        highestProfitPrice = currentPrice;
                        currentTrailingStop = currentPrice - trailingStopDistance;
                        trailingStopActive = true;
                        this.Log($"Updated trailing stop for LONG: {currentTrailingStop}");
                    }
                    
                    // Check if price hits trailing stop
                    if (trailingStopActive && currentPrice <= currentTrailingStop)
                    {
                        ClosePosition(position, "Trailing Stop Hit");
                    }
                }
                else if (position.Side == Side.Sell)
                {
                    // Short position - track lowest price for trailing stop
                    if (lowestProfitPrice == 0.0 || currentPrice < lowestProfitPrice)
                    {
                        lowestProfitPrice = currentPrice;
                        currentTrailingStop = currentPrice + trailingStopDistance;
                        trailingStopActive = true;
                        this.Log($"Updated trailing stop for SHORT: {currentTrailingStop}");
                    }
                    
                    // Check if price hits trailing stop
                    if (trailingStopActive && currentPrice >= currentTrailingStop)
                    {
                        ClosePosition(position, "Trailing Stop Hit");
                    }
                }
            }
        }

        private void CheckProfitTargets()
        {
            if (!useFixedProfitTarget)
                return;

            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            
            if (positions.Length == 0)
                return;

            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);

            foreach (var position in positions)
            {
                double profitTarget = 0.0;
                bool targetHit = false;

                if (position.Side == Side.Buy)
                {
                    // Long position - check if price reached target above entry (using ORB high as reference)
                    profitTarget = orbHigh + profitTargetPoints;
                    targetHit = currentPrice >= profitTarget;
                }
                else if (position.Side == Side.Sell)
                {
                    // Short position - check if price reached target below entry (using ORB low as reference)
                    profitTarget = orbLow - profitTargetPoints;
                    targetHit = currentPrice <= profitTarget;
                }

                if (targetHit)
                {
                    ClosePosition(position, $"Profit Target Hit: {profitTargetPoints} points");
                }
            }
        }

        private void ClosePosition(Position position, string reason)
        {
            try
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
            catch (Exception ex)
            {
                this.Log($"Error closing position: {ex.Message}");
            }
        }
    }
}