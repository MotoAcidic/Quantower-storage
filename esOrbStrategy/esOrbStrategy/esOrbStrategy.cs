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

        [Description("Dynamic Multi-Level Retest")]
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
            "Dynamic Multi-Level Retest", EntryMode.WaitFor50PercentRetest,
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

        [InputParameter("Strong Volume Immediate Entry Threshold", 20)]
        public double strongVolumeThreshold = 1000.0;

        [InputParameter("Retest Timeout (minutes)", 21)] 
        public int retestTimeoutMinutes = 15;

        [InputParameter("Enable Immediate Entry on Strong Volume", 22)]
        public bool enableImmediateStrongVolumeEntry = true;

        [InputParameter("EST Timezone Offset (hours)", 23)]
        public double estTimezoneOffset = -5.0; // EST is UTC-5 (change to -4 for EDT)

        [InputParameter("Enable Session High/Low Reversal Trading", 24)]
        public bool enableSessionReversalTrading = false;

        [InputParameter("Session Reversal Volume Threshold", 25)]
        public double sessionReversalVolumeThreshold = 500.0;

        [InputParameter("Session Reversal Timeout (minutes)", 26)]
        public int sessionReversalTimeoutMinutes = 30;

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

        // Enhanced 50% retest tracking
        private bool retestLevelReached = false;
        private bool retestRespected = false;
        private bool retestInvalidated = false;
        private double previousClose = 0.0;
        private DateTime waitingForRetestStartTime = DateTime.MinValue;

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

        // Session High/Low tracking for reversals
        private double asiaSessionHigh = double.MinValue;
        private double asiaSessionLow = double.MaxValue;
        private double londonSessionHigh = double.MinValue;
        private double londonSessionLow = double.MaxValue;
        private double nySessionHigh = double.MinValue;
        private double nySessionLow = double.MaxValue;
        
        private bool asiaHighTested = false;
        private bool asiaLowTested = false;
        private bool londonHighTested = false;
        private bool londonLowTested = false;
        private bool nyHighTested = false;
        private bool nyLowTested = false;
        
        private DateTime lastSessionUpdate = DateTime.MinValue;
        private string currentSession = "";
        private bool waitingForSessionReversal = false;
        private double sessionReversalLevel = 0.0;
        private string sessionReversalDirection = "";
        private DateTime sessionReversalStartTime = DateTime.MinValue;

        // ORB session tracking
        private DateTime orbSessionStart;
        private DateTime orbSessionEnd;
        private DateTime lastOrbDate = DateTime.MinValue;

        public esOrbStrategy()
            : base()
        {
            this.Name = "ES ORB Strategy";
            this.Description = "ES ORB Strategy - Dynamic Multi-Level Retest: ORB Boundaries + Midpoint + Volume Confirmation";

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
            
            // Initialize session tracking
            if (enableSessionReversalTrading)
            {
                this.Log("📊 Session High/Low Reversal Trading ENABLED", StrategyLoggingLevel.Trading);
                this.InitializeSessionTracking();
            }
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
                // Use centralized reset method
                ResetRetestState();

                // Reset enhanced retest tracking variables  
                retestLevelReached = false;
                retestRespected = false;
                retestInvalidated = false;
                previousClose = 0.0;

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
            
            // Reset 50% retest tracking
            retestLevelReached = false;
            retestRespected = false;
            retestInvalidated = false;
            previousClose = 0.0;
        }

        private void CheckOrbSession()
        {
            DateTime currentTime = HistoricalDataExtensions.Time(this.hdm, 0);
            
            // Convert to EST using configurable offset
            DateTime estTime = currentTime.AddHours(this.estTimezoneOffset);
            DateTime currentDate = estTime.Date;
            
            this.Log($"Current UTC Time: {currentTime:HH:mm:ss}, EST Time: {estTime:HH:mm:ss}");

            // Reset ORB on new day
            if (this.lastOrbDate != currentDate)
            {
                this.InitializeOrbSession();
                
                // Reset daily PnL and trade tracking variables
                this.tradeCount = 0;
                this.totalPnL = 0.0;
                this.totalNetPl = 0.0;
                this.totalGrossPl = 0.0;
                this.totalFee = 0.0;
                this.tradeCounter = 0;
                
                this.lastOrbDate = currentDate;
                this.Log($"🗓️ NEW TRADING DAY: {currentDate:yyyy-MM-dd} - Daily PnL and trade limits RESET", StrategyLoggingLevel.Trading);
            }

            // Calculate ORB session times for current day in EST
            this.orbSessionStart = currentDate.AddHours(this.orbStartHour).AddMinutes(this.orbStartMinute);
            this.orbSessionEnd = currentDate.AddHours(this.orbEndHour).AddMinutes(this.orbEndMinute);

            this.Log($"ORB Session Window: {this.orbSessionStart:HH:mm} to {this.orbSessionEnd:HH:mm} EST");
            
            // Check if we're in ORB session
            bool previousInSession = this.orbSessionActive;
            bool inOrbWindow = estTime >= this.orbSessionStart && estTime < this.orbSessionEnd;
            orbSessionActive = inOrbWindow;
            
            if (orbSessionActive != previousInSession)
            {
                if (orbSessionActive)
                {
                    orbCaptured = false;
                    orbHigh = HistoricalDataExtensions.High(this.hdm, 0);
                    orbLow = HistoricalDataExtensions.Low(this.hdm, 0);
                    this.Log($"ORB SESSION STARTED at {estTime:HH:mm:ss} EST - Initial high: {orbHigh}, low: {orbLow}");
                }
                else
                {
                    orbCaptured = true;
                    orbMidpoint = (orbHigh + orbLow) / 2.0;
                    this.Log($"ORB SESSION ENDED at {estTime:HH:mm:ss} EST - Final ORB: High: {orbHigh}, Low: {orbLow}, Range: {orbHigh - orbLow}");
                }
            }
            
            // Update ORB levels during active session
            if (orbSessionActive)
            {
                var currentHigh = HistoricalDataExtensions.High(this.hdm, 0);
                var currentLow = HistoricalDataExtensions.Low(this.hdm, 0);

                if (currentHigh > orbHigh)
                    orbHigh = currentHigh;
                if (currentLow < orbLow)
                    orbLow = currentLow;
                    
                this.Log($"ORB Session Active - Current High: {orbHigh}, Low: {orbLow}, Range: {orbHigh - orbLow}");
            }
        }

        private void CheckForEntry()
        {
            var close = HistoricalDataExtensions.Close(this.hdm, 0);
            var high = HistoricalDataExtensions.High(this.hdm, 0);
            var low = HistoricalDataExtensions.Low(this.hdm, 0);
            var currentTime = HistoricalDataExtensions.Time(this.hdm, 0);

            // Convert to EST for proper market open check
            DateTime estTime = currentTime.AddHours(this.estTimezoneOffset);
            bool isAfterMarketOpen = estTime.Hour >= marketOpenHour && 
                                   (estTime.Hour > marketOpenHour || estTime.Minute >= marketOpenMinute);

            bool bullishBreakout = high > (orbHigh + MinBreakoutDistance);
            bool bearishBreakout = low < (orbLow - MinBreakoutDistance);

            // Check volume requirement - now only after 9:30 AM EST
            bool hasVolume = true;
            if (!isAfterMarketOpen)
            {
                var currentVolume = HistoricalDataExtensions.Volume(this.hdm, 0);
                hasVolume = currentVolume >= MinVolumeBeforeOpen;
                if (!hasVolume)
                {
                    this.Log($"Volume requirement not met before market open. Current: {currentVolume}, Required: {MinVolumeBeforeOpen}");
                }
            }

            // Only enter trades after 9:30 AM EST with proper volume
            if (!isAfterMarketOpen || !hasVolume)
            {
                return;
            }

            switch (SelectedEntryMode)
            {
                case EntryMode.ImmediateBreakout:
                    HandleImmediateBreakout(bullishBreakout, bearishBreakout, close);
                    break;

                case EntryMode.WaitFor50PercentRetest:
                    Handle50PercentRespectRetest(bullishBreakout, bearishBreakout, close, high, low);
                    break;

                case EntryMode.WaitForAnyRetest:
                    HandleAnyRetest(bullishBreakout, bearishBreakout, close, high, low);
                    break;
            }
            
            this.previousClose = close;
            
            // Update session highs/lows and check for reversals if enabled
            if (enableSessionReversalTrading)
            {
                UpdateSessionHighsLows();
                CheckForSessionReversals();
            }
        }
        
        private void InitializeSessionTracking()
        {
            // Reset all session data daily
            asiaSessionHigh = double.MinValue;
            asiaSessionLow = double.MaxValue;
            londonSessionHigh = double.MinValue;
            londonSessionLow = double.MaxValue;
            nySessionHigh = double.MinValue;
            nySessionLow = double.MaxValue;
            
            asiaHighTested = false;
            asiaLowTested = false;
            londonHighTested = false;
            londonLowTested = false;
            nyHighTested = false;
            nyLowTested = false;
            
            this.Log("📊 Session tracking initialized for Asia/London/NY reversals", StrategyLoggingLevel.Trading);
        }
        
        private string GetCurrentSession(DateTime estTime)
        {
            int hour = estTime.Hour;
            int minute = estTime.Minute;
            int totalMinutes = hour * 60 + minute;
            
            // EST Session Times:
            // Asia: 6:00 PM - 2:00 AM (18:00 - 02:00 next day)
            // London: 3:00 AM - 12:00 PM (03:00 - 12:00)  
            // New York: 8:00 AM - 5:00 PM (08:00 - 17:00)
            
            if ((totalMinutes >= 18 * 60) || (totalMinutes < 2 * 60)) // 6 PM to 2 AM
            {
                return "Asia";
            }
            else if (totalMinutes >= 3 * 60 && totalMinutes < 12 * 60) // 3 AM to 12 PM
            {
                return "London";
            }
            else if (totalMinutes >= 8 * 60 && totalMinutes < 17 * 60) // 8 AM to 5 PM
            {
                return "NewYork";
            }
            
            return "Overlap"; // During overlap periods
        }
        
        private void UpdateSessionHighsLows()
        {
            DateTime currentTime = HistoricalDataExtensions.Time(this.hdm, 0);
            DateTime estTime = currentTime.AddHours(this.estTimezoneOffset);
            double high = HistoricalDataExtensions.High(this.hdm, 0);
            double low = HistoricalDataExtensions.Low(this.hdm, 0);
            
            string session = GetCurrentSession(estTime);
            
            // Check if we've moved to a new day - reset all sessions
            if (lastSessionUpdate.Date != estTime.Date)
            {
                InitializeSessionTracking();
                lastSessionUpdate = estTime;
                this.Log($"🌅 NEW DAY - Session highs/lows reset for {estTime:yyyy-MM-dd}", StrategyLoggingLevel.Trading);
            }
            
            // Update session-specific highs/lows
            switch (session)
            {
                case "Asia":
                    if (high > asiaSessionHigh || asiaSessionHigh == double.MinValue)
                    {
                        asiaSessionHigh = high;
                        asiaHighTested = false;
                        this.Log($"🌏 ASIA Session High updated: {asiaSessionHigh:F2} (UNTESTED)", StrategyLoggingLevel.Trading);
                    }
                    if (low < asiaSessionLow || asiaSessionLow == double.MaxValue)
                    {
                        asiaSessionLow = low;
                        asiaLowTested = false;
                        this.Log($"🌏 ASIA Session Low updated: {asiaSessionLow:F2} (UNTESTED)", StrategyLoggingLevel.Trading);
                    }
                    break;
                    
                case "London":
                    if (high > londonSessionHigh || londonSessionHigh == double.MinValue)
                    {
                        londonSessionHigh = high;
                        londonHighTested = false;
                        this.Log($"🇬🇧 LONDON Session High updated: {londonSessionHigh:F2} (UNTESTED)", StrategyLoggingLevel.Trading);
                    }
                    if (low < londonSessionLow || londonSessionLow == double.MaxValue)
                    {
                        londonSessionLow = low;
                        londonLowTested = false;
                        this.Log($"🇬🇧 LONDON Session Low updated: {londonSessionLow:F2} (UNTESTED)", StrategyLoggingLevel.Trading);
                    }
                    break;
                    
                case "NewYork":
                    if (high > nySessionHigh || nySessionHigh == double.MinValue)
                    {
                        nySessionHigh = high;
                        nyHighTested = false;
                        this.Log($"🇺🇸 NEW YORK Session High updated: {nySessionHigh:F2} (UNTESTED)", StrategyLoggingLevel.Trading);
                    }
                    if (low < nySessionLow || nySessionLow == double.MaxValue)
                    {
                        nySessionLow = low;
                        nyLowTested = false;
                        this.Log($"🇺🇸 NEW YORK Session Low updated: {nySessionLow:F2} (UNTESTED)", StrategyLoggingLevel.Trading);
                    }
                    break;
            }
            
            currentSession = session;
        }

        private void CheckForSessionReversals()
        {
            double close = HistoricalDataExtensions.Close(this.hdm, 0);
            double high = HistoricalDataExtensions.High(this.hdm, 0);
            double low = HistoricalDataExtensions.Low(this.hdm, 0);
            double currentVolume = HistoricalDataExtensions.Volume(this.hdm, 0);
            double tolerance = this.CurrentSymbol.TickSize * 3;
            
            // Check for tests of untested session highs/lows and look for reversals
            
            // Asia Session High Reversal (Short)
            if (!asiaHighTested && asiaSessionHigh > double.MinValue && high >= (asiaSessionHigh - tolerance))
            {
                asiaHighTested = true;
                this.Log($"🎯 ASIA HIGH TESTED at {asiaSessionHigh:F2} - Watching for REVERSAL SHORT", StrategyLoggingLevel.Trading);
                
                if (high >= asiaSessionHigh && close < (asiaSessionHigh - tolerance/2) && currentVolume >= sessionReversalVolumeThreshold)
                {
                    this.Log($"✅ ASIA HIGH REVERSAL CONFIRMED! Volume: {currentVolume} - Entering SHORT", StrategyLoggingLevel.Trading);
                    PlaceShortTrade(close);
                    return;
                }
            }
            
            // Asia Session Low Reversal (Long)
            if (!asiaLowTested && asiaSessionLow < double.MaxValue && low <= (asiaSessionLow + tolerance))
            {
                asiaLowTested = true;
                this.Log($"🎯 ASIA LOW TESTED at {asiaSessionLow:F2} - Watching for REVERSAL LONG", StrategyLoggingLevel.Trading);
                
                if (low <= asiaSessionLow && close > (asiaSessionLow + tolerance/2) && currentVolume >= sessionReversalVolumeThreshold)
                {
                    this.Log($"✅ ASIA LOW REVERSAL CONFIRMED! Volume: {currentVolume} - Entering LONG", StrategyLoggingLevel.Trading);
                    PlaceLongTrade(close);
                    return;
                }
            }
            
            // London Session High Reversal (Short)
            if (!londonHighTested && londonSessionHigh > double.MinValue && high >= (londonSessionHigh - tolerance))
            {
                londonHighTested = true;
                this.Log($"🎯 LONDON HIGH TESTED at {londonSessionHigh:F2} - Watching for REVERSAL SHORT", StrategyLoggingLevel.Trading);
                
                if (high >= londonSessionHigh && close < (londonSessionHigh - tolerance/2) && currentVolume >= sessionReversalVolumeThreshold)
                {
                    this.Log($"✅ LONDON HIGH REVERSAL CONFIRMED! Volume: {currentVolume} - Entering SHORT", StrategyLoggingLevel.Trading);
                    PlaceShortTrade(close);
                    return;
                }
            }
            
            // London Session Low Reversal (Long)
            if (!londonLowTested && londonSessionLow < double.MaxValue && low <= (londonSessionLow + tolerance))
            {
                londonLowTested = true;
                this.Log($"🎯 LONDON LOW TESTED at {londonSessionLow:F2} - Watching for REVERSAL LONG", StrategyLoggingLevel.Trading);
                
                if (low <= londonSessionLow && close > (londonSessionLow + tolerance/2) && currentVolume >= sessionReversalVolumeThreshold)
                {
                    this.Log($"✅ LONDON LOW REVERSAL CONFIRMED! Volume: {currentVolume} - Entering LONG", StrategyLoggingLevel.Trading);
                    PlaceLongTrade(close);
                    return;
                }
            }
            
            // NY Session High Reversal (Short)
            if (!nyHighTested && nySessionHigh > double.MinValue && high >= (nySessionHigh - tolerance))
            {
                nyHighTested = true;
                this.Log($"🎯 NY HIGH TESTED at {nySessionHigh:F2} - Watching for REVERSAL SHORT", StrategyLoggingLevel.Trading);
                
                if (high >= nySessionHigh && close < (nySessionHigh - tolerance/2) && currentVolume >= sessionReversalVolumeThreshold)
                {
                    this.Log($"✅ NY HIGH REVERSAL CONFIRMED! Volume: {currentVolume} - Entering SHORT", StrategyLoggingLevel.Trading);
                    PlaceShortTrade(close);
                    return;
                }
            }
            
            // NY Session Low Reversal (Long)
            if (!nyLowTested && nySessionLow < double.MaxValue && low <= (nySessionLow + tolerance))
            {
                nyLowTested = true;
                this.Log($"🎯 NY LOW TESTED at {nySessionLow:F2} - Watching for REVERSAL LONG", StrategyLoggingLevel.Trading);
                
                if (low <= nySessionLow && close > (nySessionLow + tolerance/2) && currentVolume >= sessionReversalVolumeThreshold)
                {
                    this.Log($"✅ NY LOW REVERSAL CONFIRMED! Volume: {currentVolume} - Entering LONG", StrategyLoggingLevel.Trading);
                    PlaceLongTrade(close);
                    return;
                }
            }
        }

        private void HandleImmediateBreakout(bool bullishBreakout, bool bearishBreakout, double close)
        {
            if (bullishBreakout && lastTradeDirection != "Long")
            {
                PlaceLongTrade(close);
            }
            else if (bearishBreakout && lastTradeDirection != "Short")
            {
                PlaceShortTrade(close);
            }
        }

        private void Handle50PercentRespectRetest(bool bullishBreakout, bool bearishBreakout, double close, double high, double low)
        {
            if (orbHigh <= double.MinValue || orbLow >= double.MaxValue) return;
            
            double orbMidpoint = (orbHigh + orbLow) / 2.0;
            double tolerance = this.CurrentSymbol.TickSize * 2;
            double currentVolume = HistoricalDataExtensions.Volume(this.hdm, 0);
            DateTime currentTime = HistoricalDataExtensions.Time(this.hdm, 0);
            
            // Check for any new breakout (can change direction)
            if (bullishBreakout || bearishBreakout)
            {
                if (bullishBreakout)
                {
                    this.Log($"🔥 BULLISH BREAKOUT above {orbHigh:F2} at {close:F2} - Volume: {currentVolume}", StrategyLoggingLevel.Trading);
                    
                    // Check for immediate entry on strong volume
                    if (enableImmediateStrongVolumeEntry && currentVolume >= strongVolumeThreshold)
                    {
                        this.Log($"⚡ STRONG VOLUME ({currentVolume} >= {strongVolumeThreshold}) - IMMEDIATE LONG ENTRY!", StrategyLoggingLevel.Trading);
                        PlaceLongTrade(close);
                        return;
                    }
                    
                    // If we were waiting for a short retest, this invalidates it
                    if (waitingForRetest && lastTradeDirection == "WaitingShort")
                    {
                        this.Log($"🔄 DIRECTION CHANGE - Bullish volume override! Entering LONG immediately", StrategyLoggingLevel.Trading);
                        PlaceLongTrade(close);
                        waitingForRetest = false;
                        return;
                    }
                    
                    // Start waiting for potential retest of ORB high as support
                    waitingForRetest = true;
                    retestLevel = orbHigh;
                    lastTradeDirection = "WaitingLong";
                    waitingForRetestStartTime = currentTime;
                    this.Log($"📊 Moderate volume ({currentVolume}) - Waiting for RETEST of ORB HIGH ({orbHigh:F2}) as support", StrategyLoggingLevel.Trading);
                }
                else if (bearishBreakout)
                {
                    this.Log($"🔥 BEARISH BREAKOUT below {orbLow:F2} at {close:F2} - Volume: {currentVolume}", StrategyLoggingLevel.Trading);
                    
                    // Check for immediate entry on strong volume
                    if (enableImmediateStrongVolumeEntry && currentVolume >= strongVolumeThreshold)
                    {
                        this.Log($"⚡ STRONG VOLUME ({currentVolume} >= {strongVolumeThreshold}) - IMMEDIATE SHORT ENTRY!", StrategyLoggingLevel.Trading);
                        PlaceShortTrade(close);
                        return;
                    }
                    
                    // If we were waiting for a long retest, this invalidates it
                    if (waitingForRetest && lastTradeDirection == "WaitingLong")
                    {
                        this.Log($"🔄 DIRECTION CHANGE - Bearish volume override! Entering SHORT immediately", StrategyLoggingLevel.Trading);
                        PlaceShortTrade(close);
                        waitingForRetest = false;
                        return;
                    }
                    
                    // Start waiting for potential retest of ORB low as resistance
                    waitingForRetest = true;
                    retestLevel = orbLow;
                    lastTradeDirection = "WaitingShort";
                    waitingForRetestStartTime = currentTime;
                    this.Log($"📊 Moderate volume ({currentVolume}) - Waiting for RETEST of ORB LOW ({orbLow:F2}) as resistance", StrategyLoggingLevel.Trading);
                }
                return;
            }
            
            // Check for retest timeout
            if (waitingForRetest && waitingForRetestStartTime != DateTime.MinValue)
            {
                double minutesWaiting = (currentTime - waitingForRetestStartTime).TotalMinutes;
                if (minutesWaiting >= retestTimeoutMinutes)
                {
                    this.Log($"⏰ RETEST TIMEOUT after {minutesWaiting:F1} minutes - Entering {lastTradeDirection.Replace("Waiting", "").ToUpper()} position anyway!", StrategyLoggingLevel.Trading);
                    
                    if (lastTradeDirection == "WaitingLong")
                    {
                        PlaceLongTrade(close);
                    }
                    else if (lastTradeDirection == "WaitingShort")
                    {
                        PlaceShortTrade(close);
                    }
                    
                    waitingForRetest = false;
                    return;
                }
            }
            
            // Monitor for retests of key levels
            if (waitingForRetest && !retestInvalidated)
            {
                // Check for retest of multiple levels: ORB boundaries AND midpoint
                bool retestingOrbHigh = Math.Abs(close - orbHigh) <= tolerance || 
                                       (low <= orbHigh + tolerance && high >= orbHigh - tolerance);
                bool retestingOrbLow = Math.Abs(close - orbLow) <= tolerance || 
                                      (high >= orbLow - tolerance && low <= orbLow + tolerance);
                bool retestingMidpoint = Math.Abs(close - orbMidpoint) <= tolerance || 
                                       (low <= orbMidpoint + tolerance && high >= orbMidpoint - tolerance);
                
                if (lastTradeDirection == "WaitingLong")
                {
                    // For long setups: Watch for retests of ORB high or midpoint as support
                    if ((retestingOrbHigh || retestingMidpoint) && !retestLevelReached)
                    {
                        retestLevelReached = true;
                        string levelName = retestingOrbHigh ? "ORB HIGH" : "MIDPOINT";
                        double actualLevel = retestingOrbHigh ? orbHigh : orbMidpoint;
                        this.Log($"🎯 {levelName} RETEST in progress at {actualLevel:F2} - Watching for support/rejection", StrategyLoggingLevel.Trading);
                        retestLevel = actualLevel;
                    }
                    
                    if (retestLevelReached)
                    {
                        // Look for clear respect/support of the level
                        if (low <= retestLevel && close > retestLevel && !retestRespected)
                        {
                            retestRespected = true;
                            waitingForRetest = false;
                            this.Log($"✅ LEVEL RESPECTED! Touched {retestLevel:F2} (low: {low:F2}) but CLOSED ABOVE at {close:F2}", StrategyLoggingLevel.Trading);
                            this.Log($"🚀 Volume: {currentVolume} - Entering LONG position", StrategyLoggingLevel.Trading);
                            PlaceLongTrade(close);
                        }
                        // Invalidation: Close below the retest level
                        else if (close < (retestLevel - tolerance) && !retestRespected)
                        {
                            this.Log($"❌ LONG SETUP INVALIDATED - Closed below {retestLevel:F2} at {close:F2}", StrategyLoggingLevel.Trading);
                            
                            // Check if this turns into a bearish breakout
                            if (close < orbLow - tolerance)
                            {
                                this.Log($"🔄 SWITCHING TO SHORT - Price broke below ORB LOW! Volume: {currentVolume}", StrategyLoggingLevel.Trading);
                                PlaceShortTrade(close);
                                waitingForRetest = false;
                            }
                            else
                            {
                                // Reset and wait for new setup
                                ResetRetestState();
                            }
                        }
                    }
                }
                else if (lastTradeDirection == "WaitingShort")
                {
                    // For short setups: Watch for retests of ORB low or midpoint as resistance
                    if ((retestingOrbLow || retestingMidpoint) && !retestLevelReached)
                    {
                        retestLevelReached = true;
                        string levelName = retestingOrbLow ? "ORB LOW" : "MIDPOINT";
                        double actualLevel = retestingOrbLow ? orbLow : orbMidpoint;
                        this.Log($"🎯 {levelName} RETEST in progress at {actualLevel:F2} - Watching for resistance/rejection", StrategyLoggingLevel.Trading);
                        retestLevel = actualLevel;
                    }
                    
                    if (retestLevelReached)
                    {
                        // Look for clear respect/resistance at the level
                        if (high >= retestLevel && close < retestLevel && !retestRespected)
                        {
                            retestRespected = true;
                            waitingForRetest = false;
                            this.Log($"✅ LEVEL RESPECTED! Touched {retestLevel:F2} (high: {high:F2}) but CLOSED BELOW at {close:F2}", StrategyLoggingLevel.Trading);
                            this.Log($"📉 Volume: {currentVolume} - Entering SHORT position", StrategyLoggingLevel.Trading);
                            PlaceShortTrade(close);
                        }
                        // Invalidation: Close above the retest level
                        else if (close > (retestLevel + tolerance) && !retestRespected)
                        {
                            this.Log($"❌ SHORT SETUP INVALIDATED - Closed above {retestLevel:F2} at {close:F2}", StrategyLoggingLevel.Trading);
                            
                            // Check if this turns into a bullish breakout
                            if (close > orbHigh + tolerance)
                            {
                                this.Log($"🔄 SWITCHING TO LONG - Price broke above ORB HIGH! Volume: {currentVolume}", StrategyLoggingLevel.Trading);
                                PlaceLongTrade(close);
                                waitingForRetest = false;
                            }
                            else
                            {
                                // Reset and wait for new setup
                                ResetRetestState();
                            }
                        }
                    }
                }
            }
        }
        
        private void ResetRetestState()
        {
            waitingForRetest = false;
            retestLevelReached = false;
            retestRespected = false;
            retestInvalidated = false;
            lastTradeDirection = "";
            waitingForRetestStartTime = DateTime.MinValue;
            this.Log($"🔄 RETEST STATE RESET - Ready for new setup", StrategyLoggingLevel.Trading);
        }

        private void HandleAnyRetest(bool bullishBreakout, bool bearishBreakout, double close, double high, double low)
        {
            if (!waitingForRetest)
            {
                if (bullishBreakout)
                {
                    waitingForRetest = true;
                    retestLevel = orbHigh;
                    lastTradeDirection = "WaitingLong";
                    this.Log($"Bullish breakout detected. Waiting for any retest to {retestLevel}");
                }
                else if (bearishBreakout)
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
            bool profitExceeded = totalPnL >= MaxProfit;
            bool lossExceeded = totalPnL <= -MaxLoss;
            
            if (profitExceeded)
            {
                this.Log($"💰 DAILY PROFIT TARGET REACHED: ${totalPnL:F2} >= ${MaxProfit} - No more trades today", StrategyLoggingLevel.Trading);
            }
            else if (lossExceeded)
            {
                this.Log($"🚫 DAILY LOSS LIMIT REACHED: ${totalPnL:F2} <= -${MaxLoss} - No more trades today", StrategyLoggingLevel.Trading);
            }
            
            return profitExceeded || lossExceeded;
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