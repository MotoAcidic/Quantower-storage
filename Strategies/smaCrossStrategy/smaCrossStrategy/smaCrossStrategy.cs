using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace SimpleMACross
{
    public sealed class SimpleMACross : Strategy, ICurrentAccount, ICurrentSymbol
    {
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        /// <summary>
        /// Account to place orders
        /// </summary>
        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        /// <summary>
        /// Period for Fast SMA indicator
        /// </summary>
        [InputParameter("Fast SMA", 2, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int FastMA { get; set; }

        /// <summary>
        /// Period for Slow SMA indicator
        /// </summary>
        [InputParameter("Slow SMA", 3, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int SlowMA { get; set; }

        /// <summary>
        /// Quantity to open order
        /// </summary>
        //[InputParameter("Quantity", 4, 0.1, 99999, 0.1, 2)]
        //public double Quantity { get; set; }

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

        [InputParameter("Multiplicative")]
        public double multiplicative = 2.0;

        // ── Exit Settings ────────────────────────────────────────────────────
        // 0 = Bar Push: close when price ticks past the previous bar's low/high.
        // 1 = SL/TP + Trailing: bracket orders manage the exit; bar-push logic is skipped.
        [InputParameter("Exit Mode (0=Bar Push, 1=SL/TP+Trailing)", 10, 0, 1, 1, 0)]
        public int exitMode = 0;

        [InputParameter("Stop Loss (ticks, both modes)", 11)]
        public int stoploss = 100;

        [InputParameter("Trailing Stop (ticks, Exit Mode 1 only)", 12)]
        public int trailingStop = 30;

        [InputParameter("Take Profit (ticks, Exit Mode 1 only)", 13)]
        public int takeProfit = 50;
        // ─────────────────────────────────────────────────────────────────────

        [InputParameter("Prop Firm Account (0=TopStep Eval, 1=TopStep Funded, 2=Lucid Eval, 3=Lucid Funded, 4=Live Account)", 19, 0, 4, 1, 0)]
        public int propFirmAccountSelection = 0;

        [InputParameter("Daily Profit Cap (for Eval accounts)", 20)]
        public double dailyProfitCap = 1500.0;

        [InputParameter("Enable Daily Profit Cap", 21)]
        public bool enableDailyProfitCap = true;

        [InputParameter("Enable Auto-Close at Cutoff", 22)]
        public bool enableAutoClose = true;

        [InputParameter("Respect Market Hours", 23)]
        public bool respectMarketHours = true;

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        private Indicator indicatorFastMA;
        private Indicator indicatorSlowMA;
        //private Indicator indicatorClass;
        //private Position currentPosition;
        private HistoricalData hdm;

        private int longPositionsCount;
        private int shortPositionsCount;
        private string orderTypeId;

        private bool waitOpenPosition;
        private bool waitClosePositions;

        private bool inPosition = false;
        private string prevSide = "none";

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        // Prop firm daily tracking
        private double dailyNetPl = 0.0;
        private DateTime currentTradingDate = DateTime.MinValue;
        private bool dailyProfitCapReached = false;
        private bool autoCloseTriggered = false;
        private DateTime lastAutoCloseDate = DateTime.MinValue;
        private DateTime capReachedDate = DateTime.MinValue;
        private TimeSpan capReachedTime = TimeSpan.Zero;

        // Bar time - updated from historical data so backtesting uses simulated time, not real clock
        private DateTime currentBarTime = DateTime.MinValue;
        private DateTime Now => currentBarTime != DateTime.MinValue ? currentBarTime : DateTime.Now;

        // Set true on every new completed bar; entry logic consumes and resets it
        private bool newBar = false;

        public SimpleMACross()
            : base()
        {
            this.Name = "SMA Cross Strategy - Prop Firm Edition";
            this.Description = "SMA Cross with prop firm daily profit management";

            this.FastMA = 10;
            this.SlowMA = 20;
            this.Period = Period.SECOND30;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-100);
            this.propFirmAccountSelection = 0; // Default to TopStep Eval
            this.dailyProfitCap = 1500.0;
            this.enableDailyProfitCap = true;
        }

        protected override void OnRun()
        {
            this.totalNetPl = 0D;
            
            // Initialize prop firm settings
            InitializePropFirmSettings();

            // Restore symbol object from active connection
            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());

            if (this.CurrentSymbol == null)
            {
                this.Log("Incorrect input parameters... Symbol have not specified.", StrategyLoggingLevel.Error);
                return;
            }

            // Restore account object from active connection
            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());

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

            this.orderTypeId = Core.OrderTypes.FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market).Id;

            if (string.IsNullOrEmpty(this.orderTypeId))
            {
                this.Log("Connection of selected symbol has not support market orders", StrategyLoggingLevel.Error);
                return;
            }

            this.indicatorFastMA = Core.Instance.Indicators.BuiltIn.SMA(this.FastMA, PriceType.Close);
            this.indicatorSlowMA = Core.Instance.Indicators.BuiltIn.SMA(this.SlowMA, PriceType.Close);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;

            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

            this.hdm.AddIndicator(this.indicatorFastMA);
            this.hdm.AddIndicator(this.indicatorSlowMA);
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
        }

        /// <summary>
        /// Initialize prop firm settings and daily tracking
        /// </summary>
        private void InitializePropFirmSettings()
        {
            DateTime now = Now;
            DateTime tradingDate = GetForexTradingDate();
            
            currentTradingDate = tradingDate;
            dailyNetPl = 0.0;
            dailyProfitCapReached = false;
            autoCloseTriggered = false;
            lastAutoCloseDate = DateTime.MinValue;
            
            string accountTypeText = GetAccountTypeDescription();
            this.Log($"?? INITIALIZED - Trading Date: {tradingDate:yyyy-MM-dd}, Time: {now:HH:mm:ss} EST", StrategyLoggingLevel.Trading);
            this.Log($"?? Account: {accountTypeText} (Selection: {propFirmAccountSelection}), Daily Cap: ${dailyProfitCap:F0}, Cap Enabled: {enableDailyProfitCap}", StrategyLoggingLevel.Trading);
            
            if (IsEvalAccount() && enableDailyProfitCap)
            {
                this.Log($"? Evaluation account - Daily profit cap active, resets at 6pm EST", StrategyLoggingLevel.Trading);
            }
        }

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double currentPositionsQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);

            if (Math.Abs(currentPositionsQty) == this.Quantity)
                this.waitOpenPosition = false;
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (!positions.Any())
            {
                this.waitClosePositions = false;
                this.inPosition = false;
                
                // Cancel any remaining stop loss or take profit orders
                var orders = Core.Instance.Orders.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
                foreach (var order in orders)
                {
                    var cancelResult = order.Cancel();
                    if (cancelResult.Status == TradingOperationResultStatus.Success)
                    {
                        this.Log($"Cancelled remaining order: {order.OrderTypeId}", StrategyLoggingLevel.Trading);
                    }
                }
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            // Only process orders for our symbol and account
            if (obj.Symbol != this.CurrentSymbol)
                return;

            if (obj.Account != this.CurrentAccount)
                return;

            if (obj.Status == OrderStatus.Refused)
                this.ProcessTradingRefuse();
        }

        private void Core_TradeAdded(Trade obj)
        {
            // Only process trades for our symbol and account
            if (obj.Symbol != this.CurrentSymbol)
                return;
                
            if (obj.Account != this.CurrentAccount)
                return;
                
            if (obj.NetPnl != null)
            {
                this.totalNetPl += obj.NetPnl.Value;
                
                // Track daily P&L for prop firm management
                UpdateDailyProfit(obj.NetPnl.Value);
                
                // Log every trade for debugging daily cap
                this.Log($"?? TRADE: P&L: ${obj.NetPnl.Value:F2}, Daily Total: ${dailyNetPl:F2} / ${dailyProfitCap:F0}", StrategyLoggingLevel.Trading);
            }

            if (obj.GrossPnl != null)
                this.totalGrossPl += obj.GrossPnl.Value;

            if (obj.Fee != null)
                this.totalFee += obj.Fee.Value;
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            // Use the bar's own timestamp - works correctly during backtesting
            // DateTime.Now returns real machine time, not the simulated backtest time
            this.currentBarTime = e.HistoryItem.TimeLeft.ToLocalTime();
            this.OnUpdate();
        }

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            // A bar just completed. Signal the entry logic to evaluate this bar.
            this.newBar = true;
            this.OnUpdate();
        }


        private void OnUpdate()
        {
            // ULTRA AGGRESSIVE: Force check for 6pm EST reset multiple times
            ForceCheckTradingDayReset();
            
            // Check daily profit status for prop firm accounts
            CheckDailyProfitStatus();
            
            // DEBUG: Log current status every few minutes when cap is reached
            DateTime now = Now;
            if (dailyProfitCapReached && now.TimeOfDay.Minutes % 5 == 0 && now.TimeOfDay.Seconds < 10)
            {
                this.Log($"?? STATUS CHECK at {now:HH:mm:ss} EST - Cap: {dailyProfitCapReached}, Trading Date: {currentTradingDate:yyyy-MM-dd}, Current Date: {GetForexTradingDate():yyyy-MM-dd}", StrategyLoggingLevel.Trading);
            }
            
            // Check market hours (5pm-6pm EST daily break + weekends)
            if (IsMarketClosed())
            {
                return;
            }
            
            // Check prop firm trading hours and auto-close if needed
            if (CheckPropFirmTradingHours())
            {
                return;
            }
            
            // Skip trading if daily profit cap reached (for Eval accounts)
            if (ShouldStopTradingDueToProfit())
            {
                return;
            }

            /// Previous Open and Close potential code
            //lookback= 1
            //historicaldata.GetPrice(PriceType.Close, lookback)
            //double lastLow = historicaldata.GetPrice(PriceType.Low, 1);
            //double lastHigh = historicaldata.GetPrice(PriceType.High, 1);
            /// Previous Open and Close potential code

            /////////////// SMA Spread Variables \\\\\\\\\\\\\\\

            //double sma_10 = this.indicatorFastMA.GetValue(0);
            //double sma_20 = this.indicatorSlowMA.GetValue(0);
            //double diff_0 = Math.Abs(sma_10 - sma_20);

            //double sma_10_3 = this.indicatorFastMA.GetValue(3);
            //double sma_20_3 = this.indicatorSlowMA.GetValue(3);
            //double sma_10_4 = this.indicatorFastMA.GetValue(4);
            //double sma_20_4 = this.indicatorSlowMA.GetValue(4);
            //double sma_10_5 = this.indicatorFastMA.GetValue(5);
            //double sma_20_5 = this.indicatorSlowMA.GetValue(5);
            //double sma_10_6 = this.indicatorFastMA.GetValue(6);
            //double sma_20_6 = this.indicatorSlowMA.GetValue(6);
            //double sma_10_7 = this.indicatorFastMA.GetValue(7);
            //double sma_20_7 = this.indicatorSlowMA.GetValue(7);

            //double diff_3 = Math.Abs(sma_10_3 - sma_20_3);
            //double diff_4 = Math.Abs(sma_10_4 - sma_20_4);
            //double diff_5 = Math.Abs(sma_10_5 - sma_20_5);
            //double diff_6 = Math.Abs(sma_10_6 - sma_20_6);
            //double diff_7 = Math.Abs(sma_10_7 - sma_20_7);

            //double diff_sum = diff_3 + diff_4 + diff_5 + diff_6 + diff_7;
            //double diff_avg = diff_sum / 5;

            //this.Log($"{sma_10_3}");
            //this.Log($"{this.indicatorFastMA}");
            //this.Log($"{prevSide}");

            /////////////// SMA Spread Variables \\\\\\\\\\\\\\\

            //double price = HistoricalDataExtensions.Close(this.hdm, 0);
            ////double currentPrice = Core.Instance.Positions.Any(currentPrice);
            //double lastLow = HistoricalDataExtensions.Low(this.hdm, 1);
            //double lastHigh = HistoricalDataExtensions.High(this.hdm, 1);

            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            //double pnlTicks = positions.Sum(x => x.GrossPnLTicks);

            double sma_10_x = this.indicatorFastMA.GetValue(0);
            double sma_20_x = this.indicatorSlowMA.GetValue(0);
            double diff_0_x = Math.Abs(sma_10_x - sma_20_x);




            if (this.waitOpenPosition)
            {
                return;
            }

            if (this.waitClosePositions)
            {
                return;
            }

            if (this.prevSide == "buy")
            {
                if (sma_10_x <= sma_20_x)
                {
                    this.prevSide = "none";
                }
            }
            else if (this.prevSide == "sell")
            {
                if (sma_10_x >= sma_20_x)
                {
                    this.prevSide = "none";
                }
            }

            

            if (positions.Any())
            {
                // CRITICAL: Check profit cap IMMEDIATELY when we have open positions
                if (IsEvalAccount() && enableDailyProfitCap)
                {
                    double currentOpenProfit = positions.Sum(x => x.GrossPnLTicks * this.CurrentSymbol.TickSize);
                    double totalDailyProfit = dailyNetPl + currentOpenProfit;
                    
                    // Only force close if we've actually exceeded the cap (not just approaching)
                    if (totalDailyProfit > dailyProfitCap)
                    {
                        this.Log($"?? EMERGENCY CLOSE - Daily cap EXCEEDED with open positions: ${totalDailyProfit:F2} > ${dailyProfitCap:F0}", StrategyLoggingLevel.Trading);
                        this.Log($"?? Breakdown - Closed: ${dailyNetPl:F2}, Open: ${currentOpenProfit:F2}, Total: ${totalDailyProfit:F2}", StrategyLoggingLevel.Trading);
                        
                        dailyProfitCapReached = true;
                        capReachedDate = Now.Date;
                        capReachedTime = Now.TimeOfDay;
                        this.waitClosePositions = true;
                        
                        foreach (var emergency_close_position in positions)
                        {
                            var result = emergency_close_position.Close();
                            if (result.Status == TradingOperationResultStatus.Success)
                            {
                                this.Log($"? Emergency position closed - cap exceeded", StrategyLoggingLevel.Trading);
                            }
                            else
                            {
                                this.Log($"? Emergency close failed: {result.Message}", StrategyLoggingLevel.Error);
                            }
                        }
                        return; // Exit immediately after emergency close
                    }
                    
                    // Log warning when approaching cap but allow position to continue
                    else if (totalDailyProfit >= (dailyProfitCap * 0.95)) // 95% threshold
                    {
                        this.Log($"?? Position approaching cap: ${totalDailyProfit:F2} (${dailyProfitCap - totalDailyProfit:F0} remaining)", StrategyLoggingLevel.Trading);
                    }
                }
                
                //this.Log("Open Positions");
                //return;
                //var pnl = currentPosition.GrossPnLTicks;
                //// Closing Positions
                ////if (this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1) || this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1)) 
                double pnlTicks = positions.Sum(x => x.GrossPnLTicks);
                //if (pnlTicks > 29 || pnlTicks < -9)
                //{
                //    this.waitClosePositions = true;
                //    this.Log($"Start close positions ({positions.Length})");

                //    foreach (var item in positions)
                //    {
                //        var result = item.Close();

                //        if (result.Status == TradingOperationResultStatus.Failure)
                //        {
                //            this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                //            this.ProcessTradingRefuse();
                //        }
                //        else
                //            this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                //    }
                //}

                // Mode 0: exit when per-tick price breaks past the previous completed bar's extreme.
                // Mode 1: SL/TP/trailing orders placed at entry handle the exit - nothing to do here.
                if (exitMode == 0)
                {
                    double price_x = HistoricalDataExtensions.Close(this.hdm, 0);
                    double lastLow_x = HistoricalDataExtensions.Low(this.hdm, 1);
                    double lastHigh_x = HistoricalDataExtensions.High(this.hdm, 1);

                    if (sma_10_x > sma_20_x)
                    {
                        if (price_x < lastLow_x)
                        {
                            this.waitClosePositions = true;
                            this.Log($"Start close positions - Bar Push exit triggered ({positions.Length})");

                            foreach (var item in positions)
                            {
                                var result = item.Close();

                                if (result.Status == TradingOperationResultStatus.Failure)
                                {
                                    this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                                    this.ProcessTradingRefuse();
                                }
                                else
                                {
                                    this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                                    this.inPosition = false;
                                }
                            }
                        }
                    }
                    else if (sma_10_x < sma_20_x)
                    {
                        if (price_x > lastHigh_x)
                        {
                            this.waitClosePositions = true;
                            this.Log($"Start close positions - Bar Push exit triggered ({positions.Length})");

                            foreach (var item in positions)
                            {
                                var result = item.Close();

                                if (result.Status == TradingOperationResultStatus.Failure)
                                {
                                    this.Log($"Close positions refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                                    this.ProcessTradingRefuse();
                                }
                                else
                                {
                                    this.Log($"Position was close: {result.Status}", StrategyLoggingLevel.Trading);
                                    this.inPosition = false;
                                }
                            }
                        }
                    }
                }
            }
            else // Opening New Positions
            {
                // Only act on completed bars to match backtesting behaviour.
                // newBar is set by Hdm_OnNewHistoryItem when a bar closes.
                if (!this.newBar)
                    return;
                this.newBar = false;

                double testSMA = this.indicatorFastMA.GetValue(3); // index 1 = just-completed bar; 3 = 2 bars before that

                // GetValue(1) = the bar that just completed (was GetValue(0) = forming bar)
                double sma_10 = this.indicatorFastMA.GetValue(1);
                double sma_20 = this.indicatorSlowMA.GetValue(1);
                double diff_0 = Math.Abs(sma_10 - sma_20);

                // Lookback bars shifted +1 to stay relative to the just-completed bar
                double sma_10_3 = this.indicatorFastMA.GetValue(4);
                double sma_20_3 = this.indicatorSlowMA.GetValue(4);
                double sma_10_4 = this.indicatorFastMA.GetValue(5);
                double sma_20_4 = this.indicatorSlowMA.GetValue(5);
                double sma_10_5 = this.indicatorFastMA.GetValue(6);
                double sma_20_5 = this.indicatorSlowMA.GetValue(6);
                double sma_10_6 = this.indicatorFastMA.GetValue(7);
                double sma_20_6 = this.indicatorSlowMA.GetValue(7);
                double sma_10_7 = this.indicatorFastMA.GetValue(8);
                double sma_20_7 = this.indicatorSlowMA.GetValue(8);

                double diff_3 = Math.Abs(sma_10_3 - sma_20_3);
                double diff_4 = Math.Abs(sma_10_4 - sma_20_4);
                double diff_5 = Math.Abs(sma_10_5 - sma_20_5);
                double diff_6 = Math.Abs(sma_10_6 - sma_20_6);
                double diff_7 = Math.Abs(sma_10_7 - sma_20_7);

                double diff_sum = diff_3 + diff_4 + diff_5 + diff_6 + diff_7;
                double diff_avg = diff_sum / 5;

                //this.Log($"Test SMA : {testSMA}");

                //this.Log($"diff_0: {diff_0}");
                //this.Log($"diff_avg: {diff_avg}");
                //this.Log($"sma_10: {sma_10}");
                //this.Log($"prevSide: {this.prevSide}");
                //this.Log($"inPosition: {this.inPosition}");

                if (diff_0 > diff_avg * multiplicative && this.inPosition == false && prevSide == "none")
                {
                    // TRIPLE CHECK: Profit cap enforcement before any new trades
                    if (IsEvalAccount() && enableDailyProfitCap)
                    {
                        double totalCurrentProfit = GetTotalDailyProfit();
                        if (totalCurrentProfit >= dailyProfitCap)
                        {
                            this.Log($"?? BLOCKING NEW TRADE - Already at cap: ${totalCurrentProfit:F2} >= ${dailyProfitCap:F0}", StrategyLoggingLevel.Trading);
                            dailyProfitCapReached = true;
                            capReachedDate = Now.Date;
                            capReachedTime = Now.TimeOfDay;
                            return;
                        }
                        
                        // Allow trading closer to cap for eval accounts (only warn at $10 remaining)
                        if (totalCurrentProfit >= (dailyProfitCap - 10))
                        {
                            this.Log($"?? CLOSE TO CAP - Proceed with caution: ${totalCurrentProfit:F2} (${dailyProfitCap - totalCurrentProfit:F0} remaining)", StrategyLoggingLevel.Trading);
                        }
                    }
                    
                    // Additional safety checks before opening positions
                    if (IsMarketClosed())
                    {
                        this.Log("?? Skipping trade signal - market is closed", StrategyLoggingLevel.Trading);
                        return;
                    }
                    
                    if (ShouldStopTradingDueToProfit())
                    {
                        this.Log("?? Skipping trade signal - daily profit cap reached", StrategyLoggingLevel.Trading);
                        return;
                    }
                    
                    if (CheckPropFirmTradingHours())
                    {
                        this.Log("?? Skipping trade signal - past trading cutoff time", StrategyLoggingLevel.Trading);
                        return;
                    }
                    
                    //this.Log("Arrow Signal");
                    //if (this.indicatorFastMA.GetValue(2) < this.indicatorSlowMA.GetValue(2) && this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1))
                    if (sma_10 > sma_20)
                    {
                        this.waitOpenPosition = true;
                        this.Log($"Start open buy position (Exit Mode: {(exitMode == 0 ? "Bar Push" : "SL/TP+Trailing")})");
                        if (exitMode == 0)
                            this.Log($"  SL: {stoploss} ticks (fixed)", StrategyLoggingLevel.Trading);
                        else
                            this.Log($"  Trailing SL: {trailingStop} ticks | TP: {takeProfit} ticks", StrategyLoggingLevel.Trading);
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            StopLoss = exitMode == 0
                                ? SlTpHolder.CreateSL(stoploss, PriceMeasurement.Offset)          // fixed SL
                                : SlTpHolder.CreateSL(trailingStop, PriceMeasurement.Offset, true), // trailing SL
                            TakeProfit = exitMode == 1
                                ? SlTpHolder.CreateTP(takeProfit, PriceMeasurement.Offset)
                                : null,
                            OrderTypeId = this.orderTypeId,
                            Quantity = this.Quantity,
                            Side = Side.Buy,
                        });

                        if (result.Status == TradingOperationResultStatus.Failure)
                        {
                            this.Log($"Place buy order refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                            this.ProcessTradingRefuse();
                        }
                        else
                        {
                            this.Log($"Position open: {result.Status}", StrategyLoggingLevel.Trading);
                            this.inPosition = true;
                            this.prevSide = "buy";
                        }
                    }
                    //else if (this.indicatorFastMA.GetValue(2) > this.indicatorSlowMA.GetValue(2) && this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1))
                    else if (sma_10 < sma_20)
                    {
                        this.waitOpenPosition = true;
                        this.Log($"Start open sell position (Exit Mode: {(exitMode == 0 ? "Bar Push" : "SL/TP+Trailing")})");
                        if (exitMode == 0)
                            this.Log($"  SL: {stoploss} ticks (fixed)", StrategyLoggingLevel.Trading);
                        else
                            this.Log($"  Trailing SL: {trailingStop} ticks | TP: {takeProfit} ticks", StrategyLoggingLevel.Trading);
                        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                        {
                            Account = this.CurrentAccount,
                            Symbol = this.CurrentSymbol,
                            StopLoss = exitMode == 0
                                ? SlTpHolder.CreateSL(stoploss, PriceMeasurement.Offset)          // fixed SL
                                : SlTpHolder.CreateSL(trailingStop, PriceMeasurement.Offset, true), // trailing SL
                            TakeProfit = exitMode == 1
                                ? SlTpHolder.CreateTP(takeProfit, PriceMeasurement.Offset)
                                : null,
                            OrderTypeId = this.orderTypeId,
                            Quantity = this.Quantity,
                            Side = Side.Sell,
                        });

                        if (result.Status == TradingOperationResultStatus.Failure)
                        {
                            this.Log($"Place sell order refuse: {(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}", StrategyLoggingLevel.Trading);
                            this.ProcessTradingRefuse();
                        }
                        else
                        {
                            this.Log($"Position open: {result.Status}", StrategyLoggingLevel.Trading);
                            this.inPosition = true;
                            this.prevSide = "sell";
                        }
                    }
                }
            }
        }

        #region Prop Firm Daily Profit Management
        
        private bool IsEvalAccount()
        {
            // 0=TopStep Eval, 2=Lucid Eval (both are eval accounts with daily caps)
            return propFirmAccountSelection == 0 || propFirmAccountSelection == 2;
        }
        
        private bool IsTopStepX()
        {
            // 0=TopStep Eval, 1=TopStep Funded (both use TopStep rules)
            return propFirmAccountSelection == 0 || propFirmAccountSelection == 1;
        }
        
        private bool IsLucidTrading()
        {
            // 2=Lucid Eval, 3=Lucid Funded (both use Lucid rules)
            return propFirmAccountSelection == 2 || propFirmAccountSelection == 3;
        }
        
        /// <summary>
        /// Aggressive check for trading day reset - called every update to catch 6pm EST transitions
        /// </summary>
        private void ForceCheckTradingDayReset()
        {
            if (!dailyProfitCapReached || !IsEvalAccount() || !enableDailyProfitCap)
                return;

            DateTime now = Now;
            TimeSpan currentTime = now.TimeOfDay;

            // The reset happens at 6pm EST each day.
            // We track which calendar date the cap was hit on.
            // Only reset when a NEW 6pm crosses AFTER the cap was reached.
            // i.e. the current date:time has passed 6pm on a date AFTER capReachedDate.
            DateTime resetMoment = capReachedDate.AddDays(1).Date + new TimeSpan(18, 0, 0);
            // If cap was reached before 6pm today, resetMoment is today at 6pm
            if (capReachedTime < new TimeSpan(18, 0, 0))
                resetMoment = capReachedDate.Date + new TimeSpan(18, 0, 0);

            if (now >= resetMoment)
            {
                double oldNetPl = dailyNetPl;
                dailyNetPl = 0.0;
                dailyProfitCapReached = false;
                autoCloseTriggered = false;
                capReachedDate = DateTime.MinValue;
                capReachedTime = TimeSpan.Zero;
                this.Log($"? 6PM EST RESET - Trading resumed. Previous daily P&L: ${oldNetPl:F2}", StrategyLoggingLevel.Trading);
            }
        }
        
        /// <summary>
        /// Perform actual daily reset with comprehensive logging
        /// </summary>
        private void PerformDailyReset(DateTime now, DateTime newTradingDate)
        {
            bool wasCapReached = dailyProfitCapReached;
            double oldNetPl = dailyNetPl;
            
            currentTradingDate = newTradingDate;
            dailyNetPl = 0.0;
            dailyProfitCapReached = false;
            autoCloseTriggered = false;
            
            string accountTypeText = GetAccountTypeDescription();
            this.Log($"?? NEW FOREX TRADING DAY at {now:HH:mm:ss} EST - Trading Date: {newTradingDate:yyyy-MM-dd} - Account: {accountTypeText}", StrategyLoggingLevel.Trading);
            
            if (wasCapReached)
            {
                this.Log($"?? PROFIT CAP RESET! Previous P&L: ${oldNetPl:F2} - New limit: ${dailyProfitCap:F0}", StrategyLoggingLevel.Trading);
                this.Log($"? TRADING RESUMED - Fresh daily cap for new Forex session", StrategyLoggingLevel.Trading);
            }
            
            if (IsEvalAccount() && enableDailyProfitCap)
            {
                this.Log($"?? Daily Profit Cap: ${dailyProfitCap:F0} (Eval Account) - Resets at 6pm EST", StrategyLoggingLevel.Trading);
            }
            else
            {
                this.Log($"?? No Daily Profit Cap ({accountTypeText}) - Cap Enabled: {enableDailyProfitCap}", StrategyLoggingLevel.Trading);
            }
        }

        /// <summary>
        /// Get the current Forex trading date (changes at 6pm EST, not midnight)
        /// Forex trading days: Sunday 6pm-Monday 6pm = "Tuesday trading day"
        /// </summary>
        private DateTime GetForexTradingDate()
        {
            DateTime now = Now;
            DateTime today = now.Date;
            TimeSpan cutoffTime = new TimeSpan(18, 0, 0); // 6:00 PM EST
            
            // Simplified logic: the trading day "label" advances at 6pm
            DateTime tradingDay;
            if (now.TimeOfDay >= cutoffTime)
            {
                // After 6pm = next day's trading session
                tradingDay = today.AddDays(1);
            }
            else
            {
                // Before 6pm = current day's trading session
                tradingDay = today;
            }
            
            // ENHANCED DEBUG: Log detailed date calculation during evening hours
            if (now.TimeOfDay.Hours >= 18 && now.TimeOfDay.Hours <= 23)
            {
                this.Log($"??? DATE CALC DEBUG - Now: {now:yyyy-MM-dd HH:mm:ss}, Today: {today:yyyy-MM-dd}, TimeOfDay: {now.TimeOfDay}, >= 18:00? {now.TimeOfDay >= cutoffTime}, Result: {tradingDay:yyyy-MM-dd}", StrategyLoggingLevel.Trading);
            }
            
            return tradingDay;
        }
        
        private void CheckDailyProfitStatus()
        {
            DateTime forexTradingDate = GetForexTradingDate();
            DateTime now = Now;
            
            // This method now mainly handles the initial setup
            // The actual reset logic is handled by ForceCheckTradingDayReset()
            if (currentTradingDate == DateTime.MinValue)
            {
                // First time initialization
                PerformDailyReset(now, forexTradingDate);
            }
            
            // Continuous monitoring of profit cap INCLUDING open positions
            if (IsEvalAccount() && enableDailyProfitCap && !dailyProfitCapReached)
            {
                double totalDailyProfit = GetTotalDailyProfit();
                
                // Check if we've exceeded the cap with open positions
                if (totalDailyProfit >= dailyProfitCap)
                {
                    dailyProfitCapReached = true;
                    capReachedDate = Now.Date;
                    capReachedTime = Now.TimeOfDay;
                    this.Log($"?? PROFIT CAP TRIGGERED by open positions: ${totalDailyProfit:F2} >= ${dailyProfitCap:F0}", StrategyLoggingLevel.Trading);
                    this.Log($"?? Breakdown - Closed trades: ${dailyNetPl:F2}, Open positions: ${totalDailyProfit - dailyNetPl:F2}", StrategyLoggingLevel.Trading);
                    CloseAllPositionsForProfitCap();
                }
            }
        }
        
        private void UpdateDailyProfit(double tradeProfit)
        {
            dailyNetPl += tradeProfit;
            
            string accountTypeText = GetAccountTypeDescription();
            
            // Check if daily profit cap reached for Eval accounts
            if (IsEvalAccount() && enableDailyProfitCap)
            {
                this.Log($"?? Updated Daily P&L: ${dailyNetPl:F2} (+${tradeProfit:F2}) - Cap: ${dailyProfitCap:F0} ({accountTypeText})", StrategyLoggingLevel.Trading);
                
                if (dailyNetPl >= dailyProfitCap && !dailyProfitCapReached)
                {
                    dailyProfitCapReached = true;
                    capReachedDate = Now.Date;
                    capReachedTime = Now.TimeOfDay;
                    this.Log($"?? DAILY PROFIT CAP REACHED: ${dailyNetPl:F2} >= ${dailyProfitCap:F0} ({accountTypeText})", StrategyLoggingLevel.Trading);
                    this.Log($"? Trading STOPPED until 6pm EST (next Forex trading day)", StrategyLoggingLevel.Trading);
                    this.Log($"?? Cap reached at: {Now:HH:mm:ss} EST - Will reset at next 6pm EST", StrategyLoggingLevel.Trading);
                }
                else if (dailyNetPl > (dailyProfitCap * 0.8)) // 80% warning
                {
                    this.Log($"?? Daily profit at ${dailyNetPl:F2} (${(dailyProfitCap - dailyNetPl):F0} until cap) - {accountTypeText}", StrategyLoggingLevel.Trading);
                }
            }
            else
            {
                this.Log($"?? Daily P&L: ${dailyNetPl:F2} (+${tradeProfit:F2}) (No Cap - {accountTypeText}) - Cap Enabled: {enableDailyProfitCap}", StrategyLoggingLevel.Trading);
            }
        }
        
        private double GetTotalDailyProfit()
        {
            // Start with closed trades profit
            double totalDaily = dailyNetPl;
            
            // Add current open positions profit
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            double openPositionsProfit = positions.Sum(x => x.GrossPnLTicks * this.CurrentSymbol.TickSize);
            
            return totalDaily + openPositionsProfit;
        }

        private bool ShouldStopTradingDueToProfit()
        {
            DateTime now = Now;
            TimeSpan currentTime = now.TimeOfDay;
            
            // DO NOT reset based on time when cap is reached - let ForceCheckTradingDayReset handle proper 6pm boundary
            // The cap should stay in effect until the actual trading day changes at 6pm EST
            
            // Only apply daily profit cap to Eval accounts
            if (IsEvalAccount() && enableDailyProfitCap)
            {
                // Calculate total daily profit including open positions
                double totalDailyProfit = GetTotalDailyProfit();
                
                if (dailyProfitCapReached)
                {
                    // Log current status for debugging every 10 minutes
                    if (currentTime.Minutes % 10 == 0)
                    {
                        this.Log($"?? Trading blocked - Profit cap reached. Total: ${totalDailyProfit:F2}, Cap: ${dailyProfitCap:F0}, Time: {now:HH:mm:ss}", StrategyLoggingLevel.Trading);
                        this.Log($"? Cap will reset at 6pm EST (next Forex trading day boundary)", StrategyLoggingLevel.Trading);
                    }
                    // Close any open positions when profit cap is reached
                    CloseAllPositionsForProfitCap();
                    return true;
                }
                
                // Check if we're about to exceed the cap (including open positions)
                if (totalDailyProfit >= dailyProfitCap)
                {
                    dailyProfitCapReached = true;
                    capReachedDate = Now.Date;
                    capReachedTime = Now.TimeOfDay;
                    this.Log($"?? Daily profit cap reached: ${totalDailyProfit:F2} >= ${dailyProfitCap:F0} - Trading stopped until 6pm EST!", StrategyLoggingLevel.Trading);
                    this.Log($"?? Breakdown - Closed trades: ${dailyNetPl:F2}, Open positions: ${totalDailyProfit - dailyNetPl:F2}", StrategyLoggingLevel.Trading);
                    CloseAllPositionsForProfitCap();
                    return true;
                }
                
                // Warning when approaching cap (80% threshold)
                if (totalDailyProfit > (dailyProfitCap * 0.8))
                {
                    this.Log($"?? Approaching daily cap: ${totalDailyProfit:F2} (${(dailyProfitCap - totalDailyProfit):F0} remaining)", StrategyLoggingLevel.Trading);
                }
            }
            
            // Express Funded and Live accounts have no daily profit cap
            return false;
        }
        
        private bool CheckPropFirmTradingHours()
        {
            if (!enableAutoClose)
                return false;
            
            DateTime now = Now;
            DateTime forexTradingDate = GetForexTradingDate();
            TimeSpan currentTime = now.TimeOfDay;
            
            // Reset auto-close trigger for new Forex trading day (6pm EST reset)
            if (lastAutoCloseDate != forexTradingDate)
            {
                autoCloseTriggered = false;
                lastAutoCloseDate = forexTradingDate;
            }
            
            TimeSpan cutoffTime;
            string firmName;
            
            if (IsTopStepX())
            {
                cutoffTime = new TimeSpan(16, 10, 0); // 4:10 PM EST
                firmName = "TopStepX";
            }
            else if (IsLucidTrading())
            {
                cutoffTime = new TimeSpan(16, 45, 0); // 4:45 PM EST
                firmName = "LucidTrading";
            }
            else
            {
                // No auto-close for other firms
                return false;
            }
            
            // Check if we've reached the cutoff time and haven't closed today
            if (currentTime >= cutoffTime && !autoCloseTriggered)
            {
                autoCloseTriggered = true;
                this.Log($"?? {firmName} AUTO-CLOSE triggered at {now:HH:mm:ss} EST", StrategyLoggingLevel.Trading);
                CloseAllPositionsForAutoClose(firmName);
                // Don't block further trading, just close positions once
            }
            
            // Return false to allow trading to continue (auto-close only closes positions once)
            return false;
        }
        
        private void CloseAllPositionsForProfitCap()
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            
            if (positions.Any())
            {
                this.Log($"?? Closing {positions.Length} position(s) due to daily profit cap", StrategyLoggingLevel.Trading);
                
                foreach (var position in positions)
                {
                    var result = position.Close();
                    if (result.Status == TradingOperationResultStatus.Success)
                    {
                        this.Log($"? Position closed successfully due to profit cap", StrategyLoggingLevel.Trading);
                    }
                    else
                    {
                        this.Log($"? Failed to close position: {result.Message}", StrategyLoggingLevel.Error);
                    }
                }
                
                // Cancel any pending orders
                CancelAllPendingOrders("profit cap reached");
            }
        }
        
        private void CloseAllPositionsForAutoClose(string firmName)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            
            if (positions.Any())
            {
                this.Log($"?? Closing {positions.Length} position(s) for {firmName} daily cutoff", StrategyLoggingLevel.Trading);
                
                foreach (var position in positions)
                {
                    var result = position.Close();
                    if (result.Status == TradingOperationResultStatus.Success)
                    {
                        this.Log($"? Position closed for daily cutoff", StrategyLoggingLevel.Trading);
                    }
                    else
                    {
                        this.Log($"? Failed to close position: {result.Message}", StrategyLoggingLevel.Error);
                    }
                }
                
                // Cancel any pending orders
                CancelAllPendingOrders($"{firmName} daily cutoff");
            }
        }
        
        private void CancelAllPendingOrders(string reason)
        {
            var orders = Core.Instance.Orders.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            
            foreach (var order in orders)
            {
                var cancelResult = order.Cancel();
                if (cancelResult.Status == TradingOperationResultStatus.Success)
                {
                    this.Log($"?? Cancelled order due to {reason}", StrategyLoggingLevel.Trading);
                }
            }
        }
        
        private bool IsMarketClosed()
        {
            if (!respectMarketHours)
                return false;
                
            DateTime now = Now;
            DayOfWeek dayOfWeek = now.DayOfWeek;
            TimeSpan currentTime = now.TimeOfDay;
            
            // Market hours: Sunday 6pm EST - Friday 5pm EST
            // Daily break: 5pm - 6pm EST (Monday-Thursday)
            
            // Weekend closure (Friday 5pm - Sunday 6pm)
            if (dayOfWeek == DayOfWeek.Friday)
            {
                TimeSpan fridayClose = new TimeSpan(17, 0, 0); // 5:00 PM EST
                if (currentTime >= fridayClose)
                {
                    // Only log once per hour to avoid spam
                    if (currentTime.Minutes < 5)
                    {
                        this.Log("?? Market closed for weekend (Friday 5pm+)", StrategyLoggingLevel.Trading);
                    }
                    return true;
                }
            }
            
            if (dayOfWeek == DayOfWeek.Saturday)
            {
                // Only log once per hour to avoid spam
                if (currentTime.Hours % 4 == 0 && currentTime.Minutes < 5)
                {
                    this.Log("?? Market closed (Saturday)", StrategyLoggingLevel.Trading);
                }
                return true;
            }
            
            if (dayOfWeek == DayOfWeek.Sunday)
            {
                TimeSpan sundayOpen = new TimeSpan(18, 0, 0); // 6:00 PM EST
                if (currentTime < sundayOpen)
                {
                    // Only log once per hour to avoid spam
                    if (currentTime.Minutes < 5)
                    {
                        this.Log("?? Market closed (Sunday before 6pm)", StrategyLoggingLevel.Trading);
                    }
                    return true;
                }
            }
            
            // Daily session break (5pm - 6pm EST, Monday-Thursday)
            if (dayOfWeek >= DayOfWeek.Monday && dayOfWeek <= DayOfWeek.Thursday)
            {
                TimeSpan dailyClose = new TimeSpan(17, 0, 0); // 5:00 PM EST
                TimeSpan dailyOpen = new TimeSpan(18, 0, 0);  // 6:00 PM EST
                
                if (currentTime >= dailyClose && currentTime < dailyOpen)
                {
                    // Only log once during the break to avoid spam
                    if (currentTime.Minutes < 5)
                    {
                        this.Log($"?? Market closed for daily break (5pm-6pm EST on {dayOfWeek})", StrategyLoggingLevel.Trading);
                    }
                    return true;
                }
            }
            
            return false;
        }
        
        private string GetAccountTypeDescription()
        {
            switch (propFirmAccountSelection)
            {
                case 0:
                    return "TopStep Evaluation";
                case 1:
                    return "TopStep Funded";
                case 2:
                    return "Lucid Evaluation";
                case 3:
                    return "Lucid Funded";
                case 4:
                    return "Live Account";
                default:
                    return "Unknown";
            }
        }
        
        #endregion

        private void ProcessTradingRefuse()
        {
            this.Log("Strategy have received refuse for trading action. It should be stopped", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}
