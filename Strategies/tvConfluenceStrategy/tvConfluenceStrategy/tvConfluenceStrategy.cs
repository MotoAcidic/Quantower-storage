using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace tvConfluenceStrategy
{
    /// <summary>
    /// TradingView Confluence Strategy — combines three of your own TradingView
    /// indicators (Tradingview/*.pine) into a single Quantower confluence-gated
    /// entry system:
    ///
    ///   1. Trendlines.pine (LuxAlgo "Trendlines with Breaks") — dynamic,
    ///      ATR-sloped trendline drawn from the most recent confirmed swing
    ///      high/low. A close breaking through it is a momentum/breakout signal.
    ///   2. keltnerChannel.pine (Keltner Channel) — EMA basis +/- ATR band.
    ///      Price stretched beyond a band edge is a mean-reversion signal back
    ///      toward the basis.
    ///   3. supportResistanceChannels.pine (LonesomeTheBlue "Support Resistance
    ///      Channels") — clusters nearby swing pivots into multi-touch S/R
    ///      zones; a close breaking through a zone is a breakout signal.
    ///
    /// Each one votes LONG, SHORT, or nothing on every bar close. An entry only
    /// fires once at least MinConfluencesRequired of the enabled detectors agree
    /// on the same direction — same "confluence" philosophy (independent
    /// detectors combined by a vote count, not one hard-coded rule) already
    /// proven out in the DayTrader-Portable project's confluences.ts, which
    /// this file's math is a direct, verified port of.
    ///
    /// Design choice: entries are evaluated ONLY on bar close (Hdm_OnNewHistoryItem),
    /// not intrabar. Swing pivots and S/R zones are inherently a "did the last
    /// N bars form a confirmed shape" question — recomputing them on every tick
    /// would just add noise, not signal. Once in a position, this strategy does
    /// NOT flip on a fresh opposite-direction confluence (unlike the EMA-cross
    /// style strategies elsewhere in this repo) — it manages the trade purely
    /// via stop loss / take profit / trailing stop / daily-loss / max-drawdown
    /// until flat again. This keeps a first version simple and predictable;
    /// reverse-on-signal can be added later if you want it.
    /// </summary>
    public sealed class TvConfluenceStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Instrument ────────────────────────────────────────────────────────
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

        // ── Trendline Break (from Trendlines.pine) ───────────────────────────
        [InputParameter("Use Trendline Break (0=off, 1=on)", 5, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int UseTrendlineBreak { get; set; }

        [InputParameter("Trendline Swing Lookback (bars)", 6, minimum: 5, maximum: 40, increment: 1, decimalPlaces: 0)]
        public int TrendlineSwingLookback { get; set; }

        [InputParameter("Trendline Slope Multiplier", 7, minimum: 0.1, maximum: 5, increment: 0.1, decimalPlaces: 1)]
        public double TrendlineSlopeMultiplier { get; set; }

        // ── Keltner Channel Reversion (from keltnerChannel.pine) ─────────────
        [InputParameter("Use Keltner Reversion (0=off, 1=on)", 8, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int UseKeltnerReversion { get; set; }

        [InputParameter("Keltner EMA Basis Length", 9, minimum: 5, maximum: 200, increment: 1, decimalPlaces: 0)]
        public int KcBasisLength { get; set; }

        [InputParameter("Keltner ATR Length", 10, minimum: 5, maximum: 200, increment: 1, decimalPlaces: 0)]
        public int KcAtrLength { get; set; }

        [InputParameter("Keltner ATR Multiplier", 11, minimum: 0.5, maximum: 5, increment: 0.1, decimalPlaces: 1)]
        public double KcAtrMultiplier { get; set; }

        // ── S/R Channel Break (from supportResistanceChannels.pine) ─────────
        [InputParameter("Use S/R Channel Break (0=off, 1=on)", 12, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int UseSrChannelBreak { get; set; }

        [InputParameter("SR Pivot Lookback (bars)", 13, minimum: 4, maximum: 30, increment: 1, decimalPlaces: 0)]
        public int SrPivotLookback { get; set; }

        [InputParameter("SR Lookback Window (bars)", 14, minimum: 50, maximum: 300, increment: 10, decimalPlaces: 0)]
        public int SrLookbackBars { get; set; }

        [InputParameter("SR Max Channel Width (ticks)", 15, minimum: 5, maximum: 100, increment: 1, decimalPlaces: 0)]
        public int SrChannelWidthTicks { get; set; }

        [InputParameter("SR Min Touches", 16, minimum: 2, maximum: 6, increment: 1, decimalPlaces: 0)]
        public int SrMinTouches { get; set; }

        // ── Confluence gate ───────────────────────────────────────────────────
        [InputParameter("Min Confluences Required", 17, minimum: 1, maximum: 3, increment: 1, decimalPlaces: 0)]
        public int MinConfluencesRequired { get; set; }

        // ── Risk management ───────────────────────────────────────────────────
        [InputParameter("Stop Loss (ticks)", 18, minimum: 1, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int StopLossTicks { get; set; }

        [InputParameter("Take Profit (ticks, 0=off)", 19, minimum: 0, maximum: 10000, increment: 1, decimalPlaces: 0)]
        public int TakeProfitTicks { get; set; }

        [InputParameter("Trail Activate At (ticks profit, 0=off)", 20, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0=off)", 21, minimum: 0, maximum: 2000, increment: 5, decimalPlaces: 0)]
        public int TrailingStopTicks { get; set; }

        // ── Session filter ────────────────────────────────────────────────────
        [InputParameter("RTH Only (0=24h, 1=RTH only)", 22, minimum: 0, maximum: 1, increment: 1, decimalPlaces: 0)]
        public int RthOnly { get; set; }

        [InputParameter("RTH Start Hour (EST, 9=9AM)", 23, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthStartHour { get; set; }

        [InputParameter("RTH End Hour (EST, 16=4PM)", 24, minimum: 0, maximum: 23, increment: 1, decimalPlaces: 0)]
        public int RthEndHour { get; set; }

        // ── Daily risk ────────────────────────────────────────────────────────
        [InputParameter("Max Daily Loss ($, 0=off)", 25, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDailyLoss { get; set; }

        [InputParameter("Max Drawdown ($, 0=off)", 26, minimum: 0, maximum: 100000, increment: 50, decimalPlaces: 0)]
        public int MaxDrawdown { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        private struct Bar { public double H, L, C; }

        private struct ConfluenceResult
        {
            public bool Met;
            public int Direction; // 1 = LONG, -1 = SHORT, 0 = neutral/not met
            public double Strength; // 0-100, informational only (shown in logs)
            public string Detail;
        }

        private HistoricalData hdm;
        private string orderTypeId;
        private int barsNeeded;

        private int longPositionsCount;
        private int shortPositionsCount;

        private bool waitOpenPosition;
        private bool waitClosePositions;

        // Trailing stop state
        private bool trailingActivated;
        private double bestPrice;
        private Side currentSide;

        // Daily P&L tracking
        private double dailyPnl;
        private int lastResetDay;
        private bool dailyLimitHit;

        // Total drawdown tracking
        private double totalRealizedPnl;
        private double peakEquity;
        private bool drawdownLimitHit;

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public TvConfluenceStrategy() : base()
        {
            this.Name = "TV Confluence Strategy";
            this.Description = "Trendline Break + Keltner Channel Reversion + S/R Channel Break confluence " +
                                "entries. Ported from Tradingview/Trendlines.pine, keltnerChannel.pine, " +
                                "and supportResistanceChannels.pine.";

            this.Period = Period.MIN5;
            this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity = 1;

            this.UseTrendlineBreak = 1;
            this.TrendlineSwingLookback = 14;
            this.TrendlineSlopeMultiplier = 1.0;

            this.UseKeltnerReversion = 1;
            this.KcBasisLength = 34;
            this.KcAtrLength = 34;
            this.KcAtrMultiplier = 1.5;

            this.UseSrChannelBreak = 1;
            this.SrPivotLookback = 10;
            this.SrLookbackBars = 200;
            this.SrChannelWidthTicks = 20;
            this.SrMinTouches = 2;

            this.MinConfluencesRequired = 2;

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

            int enabledConfluenceCount = (this.UseTrendlineBreak == 1 ? 1 : 0)
                                        + (this.UseKeltnerReversion == 1 ? 1 : 0)
                                        + (this.UseSrChannelBreak == 1 ? 1 : 0);

            if (enabledConfluenceCount == 0)
            {
                this.Log("All three confluences are disabled - nothing would ever fire. Enable at least one.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.MinConfluencesRequired > enabledConfluenceCount)
            {
                this.Log($"Min Confluences Required ({this.MinConfluencesRequired}) is higher than the number of " +
                         $"enabled detectors ({enabledConfluenceCount}) - no combination of signals could ever reach " +
                         $"that count, so no trade would ever fire. Lower Min Confluences Required to at most " +
                         $"{enabledConfluenceCount}, or enable more detectors.",
                         StrategyLoggingLevel.Error);
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

            // Enough bars for the hungriest enabled detector, plus warmup slack.
            this.barsNeeded = Math.Max(
                this.UseSrChannelBreak == 1 ? this.SrLookbackBars : 0,
                Math.Max(
                    this.UseTrendlineBreak == 1 ? this.TrendlineSwingLookback * 2 + 20 : 0,
                    this.UseKeltnerReversion == 1 ? Math.Max(this.KcBasisLength, this.KcAtrLength) * 3 : 0
                )
            ) + 10;

            this.hdm = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

            Core.PositionAdded += this.Core_PositionAdded;
            Core.PositionRemoved += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem += this.Hdm_OnNewHistoryItem;

            this.Log($"Started — Trendline:{(UseTrendlineBreak == 1 ? "on" : "off")} " +
                     $"Keltner:{(UseKeltnerReversion == 1 ? "on" : "off")} " +
                     $"SRChannel:{(UseSrChannelBreak == 1 ? "on" : "off")} " +
                     $"MinConfluences:{MinConfluencesRequired}  " +
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

        // ── Event handlers ────────────────────────────────────────────────────

        private void Core_PositionAdded(Position obj)
        {
            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
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

            this.longPositionsCount = positions.Count(x => x.Side == Side.Buy);
            this.shortPositionsCount = positions.Count(x => x.Side == Side.Sell);

            if (!positions.Any())
            {
                this.waitClosePositions = false;

                if (this.totalRealizedPnl > this.peakEquity)
                    this.peakEquity = this.totalRealizedPnl;

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

                this.trailingActivated = false;
                this.bestPrice = 0;
            }
        }

        private void Core_OrdersHistoryAdded(OrderHistory obj)
        {
            if (obj.Symbol != this.CurrentSymbol) return;
            if (obj.Account != this.CurrentAccount) return;

            if (obj.Status == OrderStatus.Refused)
                this.ProcessTradingRefuse();
        }

        private void Core_TradeAdded(Trade obj)
        {
            if (obj.Symbol != this.CurrentSymbol) return;
            if (obj.Account != this.CurrentAccount) return;

            if (obj.NetPnl != null) this.totalNetPl += obj.NetPnl.Value;
            if (obj.GrossPnl != null) this.totalGrossPl += obj.GrossPnl.Value;
            if (obj.Fee != null) this.totalFee += obj.Fee.Value;

            if (obj.GrossPnl != null)
            {
                this.dailyPnl += obj.GrossPnl.Value;
                this.totalRealizedPnl += obj.GrossPnl.Value;
            }
        }

        // Fires every price tick — daily loss / drawdown / trailing stop only.
        // Confluence evaluation and entries happen on bar close (see Hdm_OnNewHistoryItem).
        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            this.CheckDailyReset();

            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

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
                        this.Log($"DAILY LOSS LIMIT HIT — realized: ${this.dailyPnl:F2} + unrealized: ${unrealizedPnl:F2} = ${totalDailyPnl:F2} >= -${this.MaxDailyLoss} cutoff. CLOSING ALL.",
                                 StrategyLoggingLevel.Trading);
                        foreach (var pos in positions) pos.Close();
                        return;
                    }
                }

                if (this.MaxDrawdown > 0 && !this.drawdownLimitHit)
                {
                    double currentEquity = this.totalRealizedPnl + unrealizedPnl;
                    if (currentEquity > this.peakEquity)
                        this.peakEquity = currentEquity;

                    double drawdown = this.peakEquity - currentEquity;
                    if (drawdown >= this.MaxDrawdown)
                    {
                        this.drawdownLimitHit = true;
                        this.waitClosePositions = true;
                        this.Log($"MAX DRAWDOWN LIMIT HIT — peak: ${this.peakEquity:F2}  current: ${currentEquity:F2}  drawdown: ${drawdown:F2} >= ${this.MaxDrawdown}. CLOSING ALL.",
                                 StrategyLoggingLevel.Trading);
                        foreach (var pos in positions) pos.Close();
                        return;
                    }
                }
            }

            if (this.TrailActivationTicks <= 0 || this.TrailingStopTicks <= 0)
                return;

            if (!positions.Any())
                return;

            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);
            double pnlTicks = positions.Sum(x => x.GrossPnLTicks);

            if (!this.trailingActivated && pnlTicks >= this.TrailActivationTicks)
            {
                this.trailingActivated = true;
                this.bestPrice = currentPrice;
                this.Log($"Trail activated at {pnlTicks:F1}t profit. Best: {currentPrice:F4}", StrategyLoggingLevel.Trading);
            }

            if (!this.trailingActivated)
                return;

            if (this.currentSide == Side.Buy)
                this.bestPrice = Math.Max(this.bestPrice, currentPrice);
            else
                this.bestPrice = Math.Min(this.bestPrice, currentPrice);

            double tickSize = this.CurrentSymbol.TickSize;
            double trailDist = this.TrailingStopTicks * tickSize;

            bool trailHit = this.currentSide == Side.Buy
                ? currentPrice <= this.bestPrice - trailDist
                : currentPrice >= this.bestPrice + trailDist;

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

        // Fires when a bar closes — confluence detectors evaluated here only.
        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.EvaluateConfluencesAndMaybeEnter();
        }

        private void EvaluateConfluencesAndMaybeEnter()
        {
            if (this.waitOpenPosition || this.waitClosePositions) return;
            if (this.dailyLimitHit || this.drawdownLimitHit) return;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();
            if (positions.Any()) return; // flat-only entries - see class doc comment

            if (this.RthOnly == 1 && !this.IsInRth())
                return;

            int available = this.hdm.Count - 1; // exclude the still-forming bar
            if (available < 20)
                return;

            var bars = this.BuildBarArray(Math.Min(this.barsNeeded, available));
            if (bars.Length < 20)
                return;

            double tickSize = this.CurrentSymbol.TickSize;
            double lastClose = bars[bars.Length - 1].C;

            var results = new List<(string Name, ConfluenceResult R)>();
            if (this.UseTrendlineBreak == 1) results.Add(("Trendline Break", this.EvaluateTrendlineBreak(bars, tickSize)));
            if (this.UseKeltnerReversion == 1) results.Add(("Keltner Reversion", this.EvaluateKeltnerReversion(bars, tickSize, lastClose)));
            if (this.UseSrChannelBreak == 1) results.Add(("S/R Channel Break", this.EvaluateSrChannelBreak(bars, tickSize)));

            foreach (var (name, r) in results)
                this.Log($"{(r.Met ? "✅" : "⬜")} {name}: {r.Detail}", StrategyLoggingLevel.Trading);

            var met = results.Where(x => x.R.Met).ToArray();
            if (met.Length < this.MinConfluencesRequired)
                return;

            int longVotes = met.Count(x => x.R.Direction > 0);
            int shortVotes = met.Count(x => x.R.Direction < 0);
            if (longVotes == shortVotes)
                return; // no clear direction this bar

            Side side = longVotes > shortVotes ? Side.Buy : Side.Sell;
            this.Log($"Confluence entry: {side} ({met.Length}/{results.Count} met, {longVotes}L/{shortVotes}S — " +
                     $"{string.Join(", ", met.Select(x => x.Name))})", StrategyLoggingLevel.Trading);
            this.PlaceEntry(side);
        }

        // ── Bar array + confluence detectors (ported from DayTrader-Portable's ─
        // confluences.ts trendline_break / keltner_reversion / sr_channel_break,
        // same math, same edge-case handling, adapted to Quantower's
        // HistoricalDataExtensions bar-offset API instead of an in-memory Bar[].)

        private Bar[] BuildBarArray(int count)
        {
            var bars = new Bar[count];
            for (int i = 0; i < count; i++)
            {
                int offset = count - i; // offset count..1 (1 = last closed bar)
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
            {
                double tr = Math.Max(b[i].H - b[i].L,
                            Math.Max(Math.Abs(b[i].H - b[i - 1].C), Math.Abs(b[i].L - b[i - 1].C)));
                sum += tr;
            }
            return length > 0 ? sum / length : 0;
        }

        private static double[] ComputeEmaSeries(double[] series, int length)
        {
            double k = 2.0 / (length + 1);
            var outArr = new double[series.Length];
            outArr[0] = series[0];
            for (int i = 1; i < series.Length; i++)
                outArr[i] = series[i] * k + outArr[i - 1] * (1 - k);
            return outArr;
        }

        /// <summary>From Trendlines.pine (LuxAlgo): a confirmed swing high/low anchors a
        /// trendline that decays by an ATR-derived slope each bar; a close crossing
        /// back through it (the opposite direction from where the swing formed) is
        /// the breakout signal.</summary>
        private ConfluenceResult EvaluateTrendlineBreak(Bar[] b, double tickSize)
        {
            int len = this.TrendlineSwingLookback;
            if (b.Length < len * 2 + 3)
                return new ConfluenceResult { Met = false, Direction = 0, Detail = "Not enough bars yet" };

            double slope = (ComputeAtr(b, len, b.Length) / len) * this.TrendlineSlopeMultiplier;

            (double Price, int Index)? FindPivot(bool high)
            {
                for (int i = b.Length - 1 - len; i >= len; i--)
                {
                    bool isPivot = true;
                    for (int j = i - len; j <= i + len; j++)
                    {
                        if (j == i) continue;
                        if (high ? b[j].H >= b[i].H : b[j].L <= b[i].L) { isPivot = false; break; }
                    }
                    if (isPivot) return (high ? b[i].H : b[i].L, i);
                }
                return null;
            }

            var pivotHigh = FindPivot(true);
            var pivotLow = FindPivot(false);
            double lastClose = b[b.Length - 1].C;
            double prevClose = b[b.Length - 2].C;

            if (pivotHigh.HasValue)
            {
                int barsSince = (b.Length - 1) - pivotHigh.Value.Index;
                double lineNow = pivotHigh.Value.Price - slope * barsSince;
                double linePrev = pivotHigh.Value.Price - slope * (barsSince - 1);
                if (prevClose <= linePrev && lastClose > lineNow)
                {
                    double strength = Math.Min(100, 50 + ((lastClose - lineNow) / tickSize) * 10);
                    return new ConfluenceResult
                    {
                        Met = true, Direction = 1, Strength = strength,
                        Detail = $"Broke above descending trendline from swing high {pivotHigh.Value.Price:F2}",
                    };
                }
            }

            if (pivotLow.HasValue)
            {
                int barsSince = (b.Length - 1) - pivotLow.Value.Index;
                double lineNow = pivotLow.Value.Price + slope * barsSince;
                double linePrev = pivotLow.Value.Price + slope * (barsSince - 1);
                if (prevClose >= linePrev && lastClose < lineNow)
                {
                    double strength = Math.Min(100, 50 + ((lineNow - lastClose) / tickSize) * 10);
                    return new ConfluenceResult
                    {
                        Met = true, Direction = -1, Strength = strength,
                        Detail = $"Broke below ascending trendline from swing low {pivotLow.Value.Price:F2}",
                    };
                }
            }

            return new ConfluenceResult { Met = false, Direction = 0, Detail = "No fresh trendline break" };
        }

        /// <summary>From keltnerChannel.pine: EMA basis +/- ATR*multiplier band. Price
        /// stretched at/beyond a band edge signals reversion back toward the
        /// basis (opposite direction of the stretch).</summary>
        private ConfluenceResult EvaluateKeltnerReversion(Bar[] b, double tickSize, double currentPrice)
        {
            int basisLen = this.KcBasisLength;
            int atrLen = this.KcAtrLength;
            if (b.Length < Math.Max(basisLen, atrLen) + 1)
                return new ConfluenceResult { Met = false, Direction = 0, Detail = "Not enough bars yet" };

            int warmup = Math.Min(b.Length, basisLen * 3);
            var closes = b.Skip(b.Length - warmup).Select(x => x.C).ToArray();
            double ema = ComputeEmaSeries(closes, basisLen).Last();
            double range = ComputeAtr(b, atrLen, b.Length);

            double upper = ema + range * this.KcAtrMultiplier;
            double lower = ema - range * this.KcAtrMultiplier;

            if (currentPrice >= upper)
            {
                double stretchTicks = (currentPrice - upper) / tickSize;
                return new ConfluenceResult
                {
                    Met = true, Direction = -1, Strength = Math.Min(100, 50 + stretchTicks * 5),
                    Detail = $"Price {currentPrice:F2} at/above upper Keltner band {upper:F2} - reversion short",
                };
            }
            if (currentPrice <= lower)
            {
                double stretchTicks = (lower - currentPrice) / tickSize;
                return new ConfluenceResult
                {
                    Met = true, Direction = 1, Strength = Math.Min(100, 50 + stretchTicks * 5),
                    Detail = $"Price {currentPrice:F2} at/below lower Keltner band {lower:F2} - reversion long",
                };
            }
            return new ConfluenceResult { Met = false, Direction = 0, Detail = $"Price {currentPrice:F2} inside Keltner band ({lower:F2}-{upper:F2})" };
        }

        /// <summary>From supportResistanceChannels.pine (LonesomeTheBlue): clusters
        /// nearby confirmed swing pivots (within SrChannelWidthTicks of each
        /// other) into zones; a zone with at least SrMinTouches pivots is a
        /// validated S/R channel, and a close breaking through one is the
        /// signal.</summary>
        private ConfluenceResult EvaluateSrChannelBreak(Bar[] allBars, double tickSize)
        {
            int prd = this.SrPivotLookback;
            int window = Math.Max(50, this.SrLookbackBars);
            Bar[] b = allBars.Length > window ? allBars.Skip(allBars.Length - window).ToArray() : allBars;
            if (b.Length < prd * 2 + 10)
                return new ConfluenceResult { Met = false, Direction = 0, Detail = "Not enough bars yet" };

            var pivots = new List<(double Price, int Index)>();
            for (int i = prd; i < b.Length - prd; i++)
            {
                bool isHigh = true, isLow = true;
                for (int j = i - prd; j <= i + prd; j++)
                {
                    if (j == i) continue;
                    if (b[j].H >= b[i].H) isHigh = false;
                    if (b[j].L <= b[i].L) isLow = false;
                }
                if (isHigh) pivots.Add((b[i].H, i));
                if (isLow) pivots.Add((b[i].L, i));
            }

            if (pivots.Count < this.SrMinTouches)
                return new ConfluenceResult { Met = false, Direction = 0, Detail = "Not enough swing points found yet" };

            double channelWidth = this.SrChannelWidthTicks * tickSize;

            var zones = new List<(double Lo, double Hi, int Touches)>();
            var used = new bool[pivots.Count];
            for (int i = 0; i < pivots.Count; i++)
            {
                if (used[i]) continue;
                double lo = pivots[i].Price, hi = pivots[i].Price;
                int touches = 1;
                used[i] = true;
                for (int j = 0; j < pivots.Count; j++)
                {
                    if (used[j]) continue;
                    double p = pivots[j].Price;
                    double newLo = Math.Min(lo, p), newHi = Math.Max(hi, p);
                    if (newHi - newLo <= channelWidth)
                    {
                        lo = newLo; hi = newHi; touches++;
                        used[j] = true;
                    }
                }
                if (touches >= this.SrMinTouches) zones.Add((lo, hi, touches));
            }

            if (zones.Count == 0)
                return new ConfluenceResult { Met = false, Direction = 0, Detail = "No validated S/R channels in range" };

            double lastClose = b[b.Length - 1].C;
            double prevClose = b[b.Length - 2].C;

            foreach (var zone in zones)
            {
                if (prevClose <= zone.Hi && lastClose > zone.Hi)
                    return new ConfluenceResult
                    {
                        Met = true, Direction = 1, Strength = Math.Min(100, 40 + zone.Touches * 15),
                        Detail = $"Broke above {zone.Touches}-touch resistance zone {zone.Lo:F2}-{zone.Hi:F2}",
                    };
                if (prevClose >= zone.Lo && lastClose < zone.Lo)
                    return new ConfluenceResult
                    {
                        Met = true, Direction = -1, Strength = Math.Min(100, 40 + zone.Touches * 15),
                        Detail = $"Broke below {zone.Touches}-touch support zone {zone.Lo:F2}-{zone.Hi:F2}",
                    };
            }

            return new ConfluenceResult { Met = false, Direction = 0, Detail = $"{zones.Count} S/R zone(s) tracked, no break this bar" };
        }

        // ── Filter helpers ────────────────────────────────────────────────────

        private bool IsInRth()
        {
            var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            int estHour = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, easternZone).Hour;
            return estHour >= this.RthStartHour && estHour < this.RthEndHour;
        }

        private void CheckDailyReset()
        {
            if (this.MaxDailyLoss <= 0)
                return;

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
                this.Log($"DAILY LOSS LIMIT HIT — P&L: ${this.dailyPnl:F2} >= -${this.MaxDailyLoss} cutoff. No new trades until next session.",
                         StrategyLoggingLevel.Trading);
            }
        }

        // ── Execution ─────────────────────────────────────────────────────────

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
                this.Log($"{side} position opened — SL: {StopLossTicks}t" +
                         (TakeProfitTicks > 0 ? $"  TP: {TakeProfitTicks}t" : ""),
                         StrategyLoggingLevel.Trading);
            }
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
        }
    }
}
