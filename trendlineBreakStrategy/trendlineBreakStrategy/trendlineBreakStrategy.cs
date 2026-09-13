using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace trendlineBreakStrategy
{
    /// <summary>
    /// Trendline Break Strategy — split out of tvConfluenceStrategy 2026-09-12
    /// per the user: rather than one strategy voting across all three of
    /// their TradingView indicators, each gets its own independent strategy
    /// so it can be tuned, backtested, and evaluated on its own.
    ///
    /// From Trendlines.pine (LuxAlgo "Trendlines with Breaks"): a confirmed
    /// swing high/low anchors a trendline that decays by an ATR-derived slope
    /// each bar; a close crossing back through it is the breakout signal.
    /// Same math as tvConfluenceStrategy's EvaluateTrendlineBreak - not
    /// re-derived.
    ///
    /// IMPORTANT - running alongside keltnerReversionStrategy/
    /// srChannelBreakStrategy on the SAME account+contract: see
    /// keltnerReversionStrategy.cs's class doc comment for the full
    /// explanation of why every Position/Order/Trade/OrderHistory query
    /// here is filtered by StrategyTag (Quantower positions belong to an
    /// account+symbol pair, not to a specific strategy instance) and the
    /// caveat that this hasn't been verified against a live session.
    /// </summary>
    public sealed class TrendlineBreakStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        private const string StrategyTag = "TrendlineBreak";

        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Period", 2)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 3)]
        public DateTime StartPoint { get; set; }

        [InputParameter("Quantity", 4, minimum: 1, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int Quantity { get; set; }

        [InputParameter("Swing Lookback (bars)", 5, minimum: 5, maximum: 40, increment: 1, decimalPlaces: 0)]
        public int SwingLookback { get; set; }

        [InputParameter("Slope Multiplier", 6, minimum: 0.1, maximum: 5, increment: 0.1, decimalPlaces: 1)]
        public double SlopeMultiplier { get; set; }

        [InputParameter("Stop Loss (ticks)", 7, minimum: 1, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int StopLossTicks { get; set; }

        [InputParameter("Take Profit (ticks, 0=off)", 8, minimum: 0, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int TakeProfitTicks { get; set; }

        [InputParameter("Trail Activate At (ticks profit, 0=off)", 9, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0=off)", 10, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailingStopTicks { get; set; }

        [InputParameter("RTH Only (0=24h, 1=RTH only)", 11, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int RthOnly { get; set; }

        [InputParameter("RTH Start Hour (EST, 9=9AM)", 12, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthStartHour { get; set; }

        [InputParameter("RTH End Hour (EST, 16=4PM)", 13, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthEndHour { get; set; }

        [InputParameter("Max Daily Loss ($, 0=off)", 14, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDailyLoss { get; set; }

        [InputParameter("Max Drawdown ($, 0=off)", 15, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDrawdown { get; set; }

        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        private struct Bar { public double H, L, C; }

        private HistoricalData hdm;
        private string orderTypeId;
        private int barsNeeded;

        private int longPositionsCount;
        private int shortPositionsCount;
        private bool waitOpenPosition;
        private bool waitClosePositions;

        private bool trailingActivated;
        private double bestPrice;
        private Side currentSide;

        private double dailyPnl;
        private int lastResetDay;
        private bool dailyLimitHit;

        private double totalRealizedPnl;
        private double peakEquity;
        private bool drawdownLimitHit;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public TrendlineBreakStrategy() : base()
        {
            this.Name = "Trendline Break Strategy";
            this.Description = "ATR-sloped trendline breakout off the nearest confirmed swing high/low. Ported from Tradingview/Trendlines.pine.";

            this.Period = Period.MIN5;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity = 1;

            this.SwingLookback = 14;
            this.SlopeMultiplier = 1.0;

            this.StopLossTicks = 80;
            this.TakeProfitTicks = 0;
            this.TrailActivationTicks = 40;
            this.TrailingStopTicks = 20;

            this.RthOnly = 0;
            this.RthStartHour = 9;
            this.RthEndHour = 16;

            this.MaxDailyLoss = 0;
            this.MaxDrawdown = 2000;
        }

        protected override void OnRun()
        {
            this.totalNetPl = 0;
            this.totalGrossPl = 0;
            this.totalFee = 0;
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
            this.trailingActivated = false;
            this.bestPrice = 0;
            this.dailyPnl = 0;
            this.lastResetDay = -1;
            this.dailyLimitHit = false;
            this.totalRealizedPnl = 0;
            this.peakEquity = 0;
            this.drawdownLimitHit = false;

            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());
            if (this.CurrentSymbol == null) { this.Log("Symbol not specified.", StrategyLoggingLevel.Error); return; }

            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());
            if (this.CurrentAccount == null) { this.Log("Account not specified.", StrategyLoggingLevel.Error); return; }

            if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
            {
                this.Log("Symbol and Account are from different connections.", StrategyLoggingLevel.Error);
                return;
            }

            this.orderTypeId = Core.OrderTypes
                .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market)?.Id;
            if (string.IsNullOrEmpty(this.orderTypeId)) { this.Log("Connection does not support market orders.", StrategyLoggingLevel.Error); return; }

            this.barsNeeded = this.SwingLookback * 2 + 20;

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

            this.Log($"Started [{StrategyTag}] — SwingLookback:{SwingLookback}  SlopeMult:{SlopeMultiplier}  " +
                     $"SL:{StopLossTicks}t  TP:{(TakeProfitTicks > 0 ? $"{TakeProfitTicks}t" : "off")}  " +
                     $"Trail:{(TrailingStopTicks > 0 && TrailActivationTicks > 0 ? $"{TrailActivationTicks}t/{TrailingStopTicks}t" : "off")}  " +
                     $"RTH:{(RthOnly == 1 ? $"{RthStartHour}-{RthEndHour}" : "24h")}",
                     StrategyLoggingLevel.Trading);
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
            meter.CreateObservableCounter("total-pl-net", () => this.totalNetPl, description: "Total Net P&L");
            meter.CreateObservableCounter("total-pl-gross", () => this.totalGrossPl, description: "Total Gross P&L");
            meter.CreateObservableCounter("total-fee", () => this.totalFee, description: "Total Fees");
        }

        private Position[] MyPositions() => Core.Instance.Positions
            .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount && x.Comment == StrategyTag)
            .ToArray();

        private Order[] MyOrders() => Core.Instance.Orders
            .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount && x.Comment == StrategyTag)
            .ToArray();

        private void Core_PositionAdded(Position obj)
        {
            if (obj.Comment != StrategyTag) return;
            var positions = this.MyPositions();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            double netQty = positions.Sum(x => x.Side == Side.Buy ? x.Quantity : -x.Quantity);
            if (Math.Abs(netQty) == this.Quantity)
                this.waitOpenPosition = false;
        }

        private void Core_PositionRemoved(Position obj)
        {
            if (obj.Comment != StrategyTag) return;
            var positions = this.MyPositions();
            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (!positions.Any())
            {
                this.waitClosePositions = false;
                if (this.totalRealizedPnl > this.peakEquity) this.peakEquity = this.totalRealizedPnl;

                foreach (var order in this.MyOrders())
                {
                    var r = order.Cancel();
                    if (r.Status == TradingOperationResultStatus.Success)
                        this.Log($"Cancelled leftover order: {order.OrderTypeId}", StrategyLoggingLevel.Trading);
                    else
                        this.Log($"Failed to cancel order: {r.Message}", StrategyLoggingLevel.Error);
                }

                this.trailingActivated = false;
                this.bestPrice = 0;
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount || obj.Comment != StrategyTag) return;
            if (obj.Status == OrderStatus.Refused) this.ProcessTradingRefuse();
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.Symbol != this.CurrentSymbol || obj.Account != this.CurrentAccount || obj.Comment != StrategyTag) return;

            if (obj.NetPnl != null) this.totalNetPl += obj.NetPnl.Value;
            if (obj.GrossPnl != null) this.totalGrossPl += obj.GrossPnl.Value;
            if (obj.Fee != null) this.totalFee += obj.Fee.Value;

            if (obj.GrossPnl != null)
            {
                this.dailyPnl += obj.GrossPnl.Value;
                this.totalRealizedPnl += obj.GrossPnl.Value;
            }
        }

        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            this.CheckDailyReset();
            if (this.waitOpenPosition || this.waitClosePositions) return;

            var positions = this.MyPositions();

            if (positions.Any())
            {
                double unrealizedPnlTicks = positions.Sum(x => x.GrossPnLTicks);
                double tickValue = this.CurrentSymbol.TickSize > 0 ? this.CurrentSymbol.GetTickCost(1) : 0;
                double unrealizedPnl = unrealizedPnlTicks * tickValue;

                if (this.MaxDailyLoss > 0 && !this.dailyLimitHit)
                {
                    double totalDailyPnl = this.dailyPnl + unrealizedPnl;
                    if (totalDailyPnl <= -this.MaxDailyLoss)
                    {
                        this.dailyLimitHit = true;
                        this.waitClosePositions = true;
                        this.Log($"DAILY LOSS LIMIT HIT — realized: ${this.dailyPnl:F2} + unrealized: ${unrealizedPnl:F2} = ${totalDailyPnl:F2} >= -${this.MaxDailyLoss} cutoff. CLOSING ALL.", StrategyLoggingLevel.Trading);
                        foreach (var pos in positions) pos.Close();
                        return;
                    }
                }

                if (this.MaxDrawdown > 0 && !this.drawdownLimitHit)
                {
                    double currentEquity = this.totalRealizedPnl + unrealizedPnl;
                    if (currentEquity > this.peakEquity) this.peakEquity = currentEquity;
                    double drawdown = this.peakEquity - currentEquity;
                    if (drawdown >= this.MaxDrawdown)
                    {
                        this.drawdownLimitHit = true;
                        this.waitClosePositions = true;
                        this.Log($"MAX DRAWDOWN LIMIT HIT — peak: ${this.peakEquity:F2}  current: ${currentEquity:F2}  drawdown: ${drawdown:F2} >= ${this.MaxDrawdown}. CLOSING ALL.", StrategyLoggingLevel.Trading);
                        foreach (var pos in positions) pos.Close();
                        return;
                    }
                }
            }

            if (this.TrailActivationTicks <= 0 || this.TrailingStopTicks <= 0) return;
            if (!positions.Any()) return;

            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);
            double pnlTicks = positions.Sum(x => x.GrossPnLTicks);

            if (!this.trailingActivated && pnlTicks >= this.TrailActivationTicks)
            {
                this.trailingActivated = true;
                this.bestPrice = currentPrice;
                this.Log($"Trail activated at {pnlTicks:F1}t profit. Best: {currentPrice:F4}", StrategyLoggingLevel.Trading);
            }
            if (!this.trailingActivated) return;

            this.bestPrice = this.currentSide == Side.Buy ? Math.Max(this.bestPrice, currentPrice) : Math.Min(this.bestPrice, currentPrice);
            double tickSize = this.CurrentSymbol.TickSize;
            double trailDist = this.TrailingStopTicks * tickSize;
            bool trailHit = this.currentSide == Side.Buy ? currentPrice <= this.bestPrice - trailDist : currentPrice >= this.bestPrice + trailDist;

            if (trailHit)
            {
                this.Log($"Trail stop hit — best:{this.bestPrice:F4}  current:{currentPrice:F4}  dist:{this.TrailingStopTicks}t", StrategyLoggingLevel.Trading);
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

        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args) => this.EvaluateAndMaybeEnter();

        /// <summary>Same detector as tvConfluenceStrategy's EvaluateTrendlineBreak - a
        /// confirmed swing high/low anchors a trendline that decays by an
        /// ATR-derived slope each bar; a close crossing back through it is the
        /// breakout signal.</summary>
        private void EvaluateAndMaybeEnter()
        {
            if (this.waitOpenPosition || this.waitClosePositions) return;
            if (this.dailyLimitHit || this.drawdownLimitHit) return;
            if (this.MyPositions().Any()) return;
            if (this.RthOnly == 1 && !this.IsInRth()) return;

            int available = this.hdm.Count - 1;
            if (available < 20) return;

            var bars = this.BuildBarArray(Math.Min(this.barsNeeded, available));
            int len = this.SwingLookback;
            if (bars.Length < len * 2 + 3) return;

            double slope = (ComputeAtr(bars, len, bars.Length) / len) * this.SlopeMultiplier;

            (double Price, int Index)? FindPivot(bool high)
            {
                for (int i = bars.Length - 1 - len; i >= len; i--)
                {
                    bool isPivot = true;
                    for (int j = i - len; j <= i + len; j++)
                    {
                        if (j == i) continue;
                        if (high ? bars[j].H >= bars[i].H : bars[j].L <= bars[i].L) { isPivot = false; break; }
                    }
                    if (isPivot) return (high ? bars[i].H : bars[i].L, i);
                }
                return null;
            }

            var pivotHigh = FindPivot(true);
            var pivotLow = FindPivot(false);
            double lastClose = bars[bars.Length - 1].C;
            double prevClose = bars[bars.Length - 2].C;

            if (pivotHigh.HasValue)
            {
                int barsSince = (bars.Length - 1) - pivotHigh.Value.Index;
                double lineNow = pivotHigh.Value.Price - slope * barsSince;
                double linePrev = pivotHigh.Value.Price - slope * (barsSince - 1);
                if (prevClose <= linePrev && lastClose > lineNow)
                {
                    this.Log($"Trendline Break: Broke above descending trendline from swing high {pivotHigh.Value.Price:F2}", StrategyLoggingLevel.Trading);
                    this.PlaceEntry(Side.Buy);
                    return;
                }
            }

            if (pivotLow.HasValue)
            {
                int barsSince = (bars.Length - 1) - pivotLow.Value.Index;
                double lineNow = pivotLow.Value.Price + slope * barsSince;
                double linePrev = pivotLow.Value.Price + slope * (barsSince - 1);
                if (prevClose >= linePrev && lastClose < lineNow)
                {
                    this.Log($"Trendline Break: Broke below ascending trendline from swing low {pivotLow.Value.Price:F2}", StrategyLoggingLevel.Trading);
                    this.PlaceEntry(Side.Sell);
                }
            }
        }

        private Bar[] BuildBarArray(int count)
        {
            var bars = new Bar[count];
            for (int i = 0; i < count; i++)
            {
                int offset = count - i;
                bars[i] = new Bar
                {
                    H = HistoricalDataExtensions.High(this.hdm, offset),
                    L = HistoricalDataExtensions.Low(this.hdm, offset),
                    C = HistoricalDataExtensions.Close(this.hdm, offset),
                };
            }
            return bars;
        }

        private static double ComputeAtr(Bar[] b, int length, int endExclusive)
        {
            double sum = 0;
            int start = Math.Max(1, endExclusive - length);
            for (int i = start; i < endExclusive; i++)
                sum += Math.Max(b[i].H - b[i].L, Math.Max(Math.Abs(b[i].H - b[i - 1].C), Math.Abs(b[i].L - b[i - 1].C)));
            return length > 0 ? sum / length : 0;
        }

        private bool IsInRth()
        {
            var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            int estHour = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, easternZone).Hour;
            return estHour >= this.RthStartHour && estHour < this.RthEndHour;
        }

        private void CheckDailyReset()
        {
            if (this.MaxDailyLoss <= 0) return;
            var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var estNow = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, easternZone);
            int dayKey = estNow.Hour >= 18 ? estNow.DayOfYear + 1 : estNow.DayOfYear;

            if (dayKey != this.lastResetDay)
            {
                this.lastResetDay = dayKey;
                this.dailyPnl = 0;
                this.dailyLimitHit = false;
                this.Log($"Daily P&L reset (new trading day, EST: {estNow:HH:mm})", StrategyLoggingLevel.Trading);
            }

            if (!this.dailyLimitHit && this.dailyPnl <= -this.MaxDailyLoss)
            {
                this.dailyLimitHit = true;
                this.Log($"DAILY LOSS LIMIT HIT — P&L: ${this.dailyPnl:F2} >= -${this.MaxDailyLoss} cutoff. No new trades until next session.", StrategyLoggingLevel.Trading);
            }
        }

        private void PlaceEntry(Side side)
        {
            this.Log($"Entry: {side} | SL:{StopLossTicks}t" +
                     (TakeProfitTicks > 0 ? $"  TP:{TakeProfitTicks}t" : "") +
                     (TrailingStopTicks > 0 && TrailActivationTicks > 0 ? $"  Trail:{TrailActivationTicks}t/{TrailingStopTicks}t" : ""),
                     StrategyLoggingLevel.Trading);

            this.waitOpenPosition = true;
            this.currentSide = side;
            this.trailingActivated = false;
            this.bestPrice = 0;

            var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                OrderTypeId = this.orderTypeId,
                Quantity = this.Quantity,
                Side = side,
                Comment = StrategyTag,
                StopLoss = SlTpHolder.CreateSL(this.StopLossTicks * this.CurrentSymbol.TickSize, PriceMeasurement.Offset),
                TakeProfit = this.TakeProfitTicks > 0
                    ? SlTpHolder.CreateTP(this.TakeProfitTicks * this.CurrentSymbol.TickSize, PriceMeasurement.Offset)
                    : null,
            });

            if (result.Status == TradingOperationResultStatus.Failure)
            {
                this.Log($"Order failed: {result.Message}", StrategyLoggingLevel.Error);
                this.ProcessTradingRefuse();
            }
            else
            {
                this.Log($"{side} position opened — SL: {StopLossTicks}t" + (TakeProfitTicks > 0 ? $"  TP: {TakeProfitTicks}t" : ""), StrategyLoggingLevel.Trading);
            }
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
        }
    }
}
