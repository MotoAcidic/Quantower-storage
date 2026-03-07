using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace priceSlopeChangeStrategy
{
    public sealed class priceSlopeChangeStrategy : Strategy, ICurrentAccount, ICurrentSymbol
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

        //[InputParameter("Stop Loss")]
        //public int stopLoss = 10;

        //[InputParameter("Take Profit")]
        //public int takeProfit = 5;

        [InputParameter("Lead SMA")]
        public int leadValue = 20;

        [InputParameter("Base SMA")]
        public int baseValue = 20;

        [InputParameter("Max Trades")]
        public int maxTrades = 20;

        [InputParameter("Max Profit")]
        public int maxProfit = 1000;

        [InputParameter("Max Loss")]
        public int maxLoss = 500;

        public override string[] MonitoringConnectionsIds => new string[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

        private HistoricalData hdm;

        private Indicator indicatorBaseSMA;
        private Indicator indicatorLeadSMA;

        private int longPositionsCount;
        private int shortPositionsCount;
        //private string orderTypeId;

        private bool waitOpenPosition;
        private bool waitClosePositions;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        private int tradeCounter = 0;
        private double baseSlopeChange = 0.0;
        private double baseSlopeChangePrev = 0.0;
        private int buyCounter = 0;
        private int sellCounter = 0;
        private bool sellReady = false;
        private bool buyReady = false;
        //private bool inPosition = false;
        private bool buyPlaced = false;
        private bool sellPlaced = false;
        private string lastCross = "none";
        private bool sessionStart = false;

        public priceSlopeChangeStrategy()
            : base()
        {
            this.Name = "Price Slope Change";
            this.Description = "Price Slope Change Strategy";

            this.Period = Period.SECOND30;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow;
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

            //this.orderTypeId = Core.OrderTypes.FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market).Id;

            //if (string.IsNullOrEmpty(this.orderTypeId))
            //{
            //    this.Log("Connection of selected symbol has not support market orders", StrategyLoggingLevel.Error);
            //    return;
            //}

            this.indicatorBaseSMA = Core.Instance.Indicators.BuiltIn.SMA(this.baseValue, PriceType.Close);
            this.indicatorLeadSMA = Core.Instance.Indicators.BuiltIn.SMA(this.leadValue, PriceType.Close);

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;

            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;

            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.LastDateTime);
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

            this.hdm.AddIndicator(this.indicatorBaseSMA);
            this.hdm.AddIndicator(this.indicatorLeadSMA);
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
        }

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double currentPositionsQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);

            if (Math.Abs(currentPositionsQty) == this.Quantity)
            {
                this.waitOpenPosition = false;
            }
            //this.inPosition = true;
            this.tradeCounter += 1;
        }

        private void Core_PositionRemoved(Position obj)
        {
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            var orders = Core.Instance.Orders.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (positions.Length == 0)
            {
                //this.waitClosePositions = false;
                //this.inPosition = false;
                this.sellPlaced = false;
                this.buyPlaced = false;
                //this.halfSellPlaced = false;
                //this.halfBuyPlaced = false;
                foreach (var items in orders)
                {
                    var result = items.Cancel();
                }
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol == this.CurrentSymbol)
            {
                return;
            }

            if (obj.Account == this.CurrentAccount)
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
            //////////////////// Time Window Code \\\\\\\\\\\\\\\\\\\\\\\\
            var timeUtc = Core.TimeUtils.DateTimeUtcNow;
            var hours = timeUtc.Hour;
            var minutes = timeUtc.Minute;
            var finalTime = (hours * 100) + minutes;
            //this.Log($"{finalTime}");

            // CDT 830 == UTC 1330
            // CDT 958 == UTC 1458
            // For testing on campus: finalTime < 1415 || finalTime > 1420
            //if (finalTime < 1435 || finalTime > 1440)
            if (finalTime < 1330 || finalTime > 1455)
            {
                if (this.sessionStart)
                {
                    this.sessionStart = false;
                    //this.sessionStop = true; // Do not believe this is needed
                    // Add logic to reset trade count here
                    this.Log("Session is now closing...");
                }
                return;
            }
            else if (!this.sessionStart)
            {
                this.sessionStart = true;
                this.Log("Session is now starting...");
            }
            //////////////////// Time Window Code \\\\\\\\\\\\\\\\\\\\\\\\


            if (this.tradeCounter >= this.maxTrades)
            {
                return;
            }
            if (this.totalGrossPl >= this.maxProfit || this.totalGrossPl <= -this.maxLoss)
            {
                return;
            }
            var positions = Core.Instance.Positions.Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount).ToArray();

            //double baseSMA0 = indicatorBaseSMA.GetValue(0);
            //double baseSMA1 = indicatorBaseSMA.GetValue(1);
            //double baseSMA2 = indicatorBaseSMA.GetValue(2);
            //double baseSMA3 = indicatorBaseSMA.GetValue(3);
            ////double baseSMA4 = indicatorBaseSMA.GetValue(4);
            //double leadSMA0 = indicatorLeadSMA.GetValue(0);

            //this.Log($"Base SMA {baseSMA0}");
            //this.Log($"Lead SMA {leadSMA0}");

            //double baseSlope = Math.Round(baseSMA1 - baseSMA2, 2);
            //double baseSlopePrev = Math.Round(baseSMA2 - baseSMA3, 2);
            //this.baseSlopeChangePrev = this.baseSlopeChange;
            //this.baseSlopeChange = Math.Round(baseSlope - baseSlopePrev, 2);

            //this.Log($"Base Slope: {baseSlope}");
            //this.Log($"Base Slope Prev: {baseSlopePrev}");
            //this.Log($"Slope Change: {this.baseSlopeChange}");

            if (this.baseSlopeChange > this.baseSlopeChangePrev)
            {
                //this.Log($"Buy Counter Increment");
                this.buyCounter++;
                this.sellCounter = 0;
            }
            if (this.baseSlopeChange < this.baseSlopeChangePrev)
            {
                //this.Log($"Sell Counter Increment");
                this.sellCounter++;
                this.buyCounter = 0;
            }


            
            if (positions.Length != 0)
            {
                if ((this.buyPlaced && this.buyCounter == 0) || (this.sellPlaced && this.sellCounter == 0))
                {
                    this.waitClosePositions = true;
                    this.Log($"Start close positions ({positions.Length})");
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
                        }
                    }
                }
                else
                {
                    return;
                }
            }
            

            if (this.baseSlopeChange > .5 && this.buyReady) //(this.buyCounter > 1 && this.buyReady) // && leadSMA0 > baseSMA0
            {
                // Place market buy
                this.waitOpenPosition = true;
                this.Log("Start open buy position");
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = this.CurrentAccount,
                    Symbol = this.CurrentSymbol,
                    OrderTypeId = OrderType.Market,
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
                    //this.inPosition = true;
                    this.buyPlaced = true;
                    this.buyReady = false;
                    this.lastCross = "buy";
                }
            }

            if (this.baseSlopeChange < -.5 && this.sellReady) //(this.sellCounter > 1 && this.sellReady) //&& leadSMA0 < baseSMA0 
            {
                // Place market sell
                this.waitOpenPosition = true;
                this.Log("Start open sell position");
                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
                {
                    Account = this.CurrentAccount,
                    Symbol = this.CurrentSymbol,
                    OrderTypeId = OrderType.Market,
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
                    //this.inPosition = true;
                    this.sellPlaced = true;
                    this.sellReady = false;
                    this.lastCross = "sell";
                }
            }
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e) => this.OnUpdate();

        private void OnUpdate()
        {
            double baseSMA0 = indicatorBaseSMA.GetValue(0);
            double baseSMA1 = indicatorBaseSMA.GetValue(1);
            double baseSMA2 = indicatorBaseSMA.GetValue(2);
            double baseSMA3 = indicatorBaseSMA.GetValue(3);
            //double baseSMA4 = indicatorBaseSMA.GetValue(4);
            double leadSMA0 = indicatorLeadSMA.GetValue(0);

            double baseSlope = Math.Round(baseSMA1 - baseSMA2, 2);
            double baseSlopePrev = Math.Round(baseSMA2 - baseSMA3, 2);
            this.baseSlopeChangePrev = this.baseSlopeChange;
            this.baseSlopeChange = Math.Round(baseSlope - baseSlopePrev, 2);

            this.Log($"Base SMA {baseSMA0}");
            this.Log($"Lead SMA {leadSMA0}");

            this.Log($"Base Slope: {baseSlope}");
            this.Log($"Base Slope Prev: {baseSlopePrev}");
            this.Log($"Slope Change: {this.baseSlopeChange}");

            if (this.lastCross == "none")
            {
                if (leadSMA0 >  baseSMA0)
                {
                    this.lastCross = "buy";
                    //this.buyReady = false;
                    //this.sellReady = false;
                }
                else if (leadSMA0 < baseSMA0)
                {
                    this.lastCross = "sell";
                    //this.sellReady = true;
                    //this.buyReady = false;
                }
            }
            if (this.lastCross == "buy" && leadSMA0 < baseSMA0 - 1.0)
            {
                this.sellReady = true;
            }
            if (this.lastCross == "sell" && leadSMA0 > baseSMA0 + 1.0)
            {
                this.buyReady = true;
            }
            if (this.sellReady && leadSMA0 > baseSMA0)
            {
                this.sellReady = false;
                this.lastCross = "buffer";
            }
            if (this.buyReady && leadSMA0 < baseSMA0)
            {
                this.buyReady = false;
                this.lastCross = "buffer";
            }
            if (this.lastCross == "buffer")
            {
                if (leadSMA0 < baseSMA0 - 1.0)
                {
                    this.sellReady = true;
                    this.lastCross = "buy";
                }
                if (leadSMA0 > baseSMA0 + 1.0)
                {
                    this.buyReady = true;
                    this.lastCross = "sell";
                }
            }
        }

        private void ProcessTradingRefuse()
        {
            this.Log("Strategy have received refuse for trading action. It should be stopped", StrategyLoggingLevel.Error);
            this.Stop();
        }
    }
}