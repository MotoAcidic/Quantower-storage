using System;
using System.Diagnostics.Metrics;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace emaCrossStrategy
{
    /// <summary>
    /// EMA Cross Strategy — mirrors the TradingView "3 Fib EMAs – XO edition" Pine Script.
    ///
    /// Parameters (matching TradingView):
    ///   Micro EMA     — fast EMA (Pine: emaFastLen, default 5)
    ///   Mid EMA       — slow EMA (Pine: emaSlowLen, default 29)
    ///   Weakness Bars — how many consecutive bars the EMA gap must shrink to signal an exit
    ///
    /// Entry (bar close only, no look-ahead bias):
    ///   LONG  — Micro EMA crosses above Mid EMA.
    ///   SHORT — Micro EMA crosses below Mid EMA.
    ///
    /// Exit — three mechanisms (evaluated in priority order):
    ///   1. Hard Stop Loss   — bracket order attached at entry (always active).
    ///   2. Take Profit      — bracket order attached at entry (optional, 0 = disabled).
    ///   3. Smart Trail      — code-managed tick-level trailing stop.
    ///        • Activates only after profit reaches TrailActivationTicks.
    ///        • Once active, tracks the best price seen and closes if price pulls back
    ///          more than TrailingStopTicks from that peak (0 = disabled).
    ///   4. Weakness Bars    — bar-close: gap shrinking N consecutive bars → close.
    ///   5. Reverse cross    — bar-close: cross other way → close and flip direction.
    /// </summary>
    public sealed class EmaCrossStrategy : Strategy, ICurrentAccount, ICurrentSymbol
    {
        // ── Instrument ────────────────────────────────────────────────────────
        [InputParameter("Symbol", 0)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Account", 1)]
        public Account CurrentAccount { get; set; }

        // ── EMA settings (match TradingView "Choose Your EMA" group) ─────────
        [InputParameter("Micro EMA", 2, minimum: 1, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int MicroEmaLen { get; set; }

        [InputParameter("Mid EMA", 3, minimum: 2, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int MidEmaLen { get; set; }

        [InputParameter("Weakness Bars", 4, minimum: 1, maximum: 20, increment: 1, decimalPlaces: 0)]
        public int WeaknessBars { get; set; }

        // ── Chart / history ───────────────────────────────────────────────────
        [InputParameter("Period", 5)]
        public Period Period { get; set; }

        [InputParameter("Start Point", 6)]
        public DateTime StartPoint { get; set; }

        // ── Trade settings ────────────────────────────────────────────────────
        [InputParameter("Quantity", 7)]
        public int Quantity { get; set; }

        [InputParameter("Stop Loss (ticks, safety net)", 8)]
        public int StopLossTicks { get; set; }

        // ── Profit management ─────────────────────────────────────────────────
        [InputParameter("Take Profit (ticks, 0 = disabled)", 9)]
        public int TakeProfitTicks { get; set; }

        // Smart trailing: activates only once profit >= TrailActivationTicks.
        // Tracks the best price reached and closes if price pulls back more than
        // TrailingStopTicks from that peak. Set either to 0 to disable trailing.
        [InputParameter("Trail Activation (ticks profit to start trailing, 0 = disabled)", 10)]
        public int TrailActivationTicks { get; set; }

        [InputParameter("Trailing Stop (ticks from peak, 0 = disabled)", 11)]
        public int TrailingStopTicks { get; set; }

        // ── Exit mode ────────────────────────────────────────────────────────
        // Controls which exit mechanism fires while in a position.
        // 0 = Weakness Bars only  |  1 = Trailing Stop only  |  2 = Both
        // Reverse-cross exit always fires regardless of this setting.
        [InputParameter("Exit Mode (0=WeaknessBars, 1=TrailingStop, 2=Both)", 12, minimum: 0, maximum: 2, increment: 1, decimalPlaces: 0)]
        public int ExitMode { get; set; }
        // When a cross fires on a candle whose body exceeds this threshold, entry
        // is deferred one bar. The next bar must still hold the same EMA alignment
        // before the position opens. Set 0 to disable (enter on every cross).
        [InputParameter("Impulse Filter (min candle body ticks to defer entry, 0 = off)", 13, minimum: 0, maximum: 500, increment: 1, decimalPlaces: 0)]
        public int ImpulseFilterTicks { get; set; }

        // ── Higher-timeframe 29 EMA touch re-entry ────────────────────────────
        // After exiting a position (non-reverse), watch for price to pull back
        // and touch the Mid EMA on the higher timeframe, then bounce back in
        // the original trend direction. The HTF is auto-selected based on the
        // cross period: 1m→3m, 3m→5m, 5m→15m, 15m→1hr. Set HtfTouchTicks to 0 to disable.
        [InputParameter("HTF Mid EMA Touch (ticks from HTF EMA to arm, 0 = off)", 14, minimum: 0, maximum: 200, increment: 1, decimalPlaces: 0)]
        public int HtfTouchTicks { get; set; }
        // ─────────────────────────────────────────────────────────────────────────

        public override string[] MonitoringConnectionsIds => new[]
        {
            this.CurrentSymbol?.ConnectionId,
            this.CurrentAccount?.ConnectionId
        };

        private Indicator microEma;
        private Indicator midEma;
        private HistoricalData hdm;
        private string orderTypeId;

        // Higher-timeframe feed for Mid EMA touch re-entry
        private Indicator      htfMidEma;
        private HistoricalData htfHdm;
        private Period         htfPeriod;  // auto-derived from cross Period

        private int longPositionsCount;
        private int shortPositionsCount;

        private bool waitOpenPosition;
        private bool waitClosePositions;
        private bool inPosition;

        // When a reverse cross closes the current position, this queues the new direction
        // so it fires inside Core_PositionRemoved once the close confirms.
        private Side? pendingEntrySide;

        // When an impulse candle is detected on a cross, defer entry by one bar.
        // On the next bar close we verify the EMA alignment still holds.
        private Side? pendingConfirmSide;

        // HTF Mid EMA touch re-entry tracking.
        // lastExitSide : direction of the last non-reverse-cross exit.
        // htfTouchArmed: set once price has come within HtfTouchTicks of the HTF Mid EMA after exit.
        private Side? lastExitSide;
        private bool  htfTouchArmed;

        // ── Smart trailing state ──────────────────────────────────────────────
        // Tracks whether the profit threshold has been hit and the best price seen.
        private bool   trailingActivated;
        private double bestPrice;          // peak price for longs, trough for shorts
        private Side   currentSide;        // direction of the open position

        private double totalNetPl;
        private double totalGrossPl;
        private double totalFee;

        public EmaCrossStrategy() : base()
        {
            this.Name        = "EMA Cross Strategy";
            this.Description = "Mirrors the TradingView 3 Fib EMAs XO Pine Script. " +
                               "Enters on Micro/Mid EMA crossovers. Exits on EMA gap " +
                               "weakness (Weakness Bars) or a reverse cross (which also " +
                               "flips into the new direction). All signals on bar close only.";

            this.MicroEmaLen   = 5;
            this.MidEmaLen     = 29;
            this.WeaknessBars  = 2;
            this.Period        = Period.MIN1;
            this.StartPoint    = Core.TimeUtils.DateTimeUtcNow.AddDays(-30);
            this.Quantity      = 1;
            this.StopLossTicks = 100;
            this.TakeProfitTicks      = 0;   // disabled by default
            this.TrailActivationTicks = 30;  // start trailing after 30 ticks of profit
            this.TrailingStopTicks    = 15;  // trail 15 ticks behind the peak
            this.ExitMode             = 2;  // 0=WeaknessBars 1=TrailingStop 2=Both
            this.ImpulseFilterTicks   = 20;  // skip entry if cross candle body > 20 ticks
            this.HtfTouchTicks        = 5;   // arm re-entry when price within 5t of HTF 29 EMA
        }

        protected override void OnRun()
        {
            this.totalNetPl          = 0;
            this.totalGrossPl         = 0;
            this.totalFee             = 0;
            this.inPosition           = false;
            this.waitOpenPosition     = false;
            this.waitClosePositions   = false;
            this.pendingEntrySide     = null;
            this.pendingConfirmSide   = null;
            this.lastExitSide         = null;
            this.htfTouchArmed        = false;
            this.trailingActivated    = false;
            this.bestPrice            = 0;

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

            // HTF feed for Mid EMA touch re-entry
            if (this.HtfTouchTicks > 0)
            {
                this.htfPeriod = this.DeriveHtfPeriod();
                if (this.htfPeriod == default)
                {
                    this.Log($"No HTF mapping for cross period {this.Period} — HTF EMA touch re-entry disabled.", StrategyLoggingLevel.Trading);
                }
                else
                {
                    this.htfMidEma = Core.Instance.Indicators.BuiltIn.EMA(this.MidEmaLen, PriceType.Close);
                    this.htfHdm    = this.CurrentSymbol.GetHistory(this.htfPeriod, this.CurrentSymbol.HistoryType, this.StartPoint);
                    this.htfHdm.AddIndicator(this.htfMidEma);
                    this.Log($"HTF EMA touch re-entry: cross={this.Period} → HTF={this.htfPeriod}  Mid EMA={this.MidEmaLen}  threshold={this.HtfTouchTicks}t",
                             StrategyLoggingLevel.Trading);
                }
            }

            Core.PositionAdded      += this.Core_PositionAdded;
            Core.PositionRemoved    += this.Core_PositionRemoved;
            Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
            Core.TradeAdded         += this.Core_TradeAdded;

            this.hdm.HistoryItemUpdated += this.Hdm_HistoryItemUpdated;
            this.hdm.NewHistoryItem     += this.Hdm_OnNewHistoryItem;

            this.Log($"Started — Micro:{MicroEmaLen}  Mid:{MidEmaLen}  " +
                     $"WeaknessBars:{WeaknessBars}  SL:{StopLossTicks}t  " +
                     $"TP:{(TakeProfitTicks > 0 ? $"{TakeProfitTicks}t" : "off")}  " +
                     $"Trail:{(TrailingStopTicks > 0 ? $"activate@{TrailActivationTicks}t trail@{TrailingStopTicks}t" : "off")}",
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

            this.htfHdm?.Dispose();

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

                // Cancel any leftover SL orders so they don't re-open positions
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

                // Reset trailing state for the next position
                this.trailingActivated = false;
                this.bestPrice         = 0;

                // Reverse-cross flip: immediately enter the new direction
                if (this.pendingEntrySide.HasValue)
                {
                    var side = this.pendingEntrySide.Value;
                    this.pendingEntrySide = null;
                    this.PlaceEntry(side);
                }
                else
                {
                    // Non-reverse exit (weakness bar, SL, TP, trailing stop).
                    // Record the direction so we can watch for the HTF EMA touch re-entry.
                    this.lastExitSide  = this.currentSide;
                    this.htfTouchArmed = false;
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

        // Fires every price tick — manages the smart trailing stop.
        private void Hdm_HistoryItemUpdated(object sender, HistoryEventArgs e)
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // Trailing stop is only active in TrailingStop (1) or Both (2) modes
            if (this.ExitMode == 0)
                return;

            if (this.TrailingStopTicks <= 0 || this.TrailActivationTicks <= 0)
                return;

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            if (!positions.Any())
                return;

            double pnlTicks = positions.Sum(x => x.GrossPnLTicks);
            double currentPrice = HistoricalDataExtensions.Close(this.hdm, 0);

            // Step 1: activate trailing once profit threshold is reached
            if (!this.trailingActivated && pnlTicks >= this.TrailActivationTicks)
            {
                this.trailingActivated = true;
                this.bestPrice         = currentPrice;
                this.Log($"Trail activated at {pnlTicks:F1} ticks profit. Best price set to {currentPrice:F4}", StrategyLoggingLevel.Trading);
            }

            if (!this.trailingActivated)
                return;

            // Step 2: update the best price seen since activation
            if (this.currentSide == Side.Buy)
                this.bestPrice = Math.Max(this.bestPrice, currentPrice);
            else
                this.bestPrice = Math.Min(this.bestPrice, currentPrice);

            // Step 3: compute trail level and check if price breached it
            double tickSize   = this.CurrentSymbol.TickSize;
            double trailDist  = this.TrailingStopTicks * tickSize;

            bool trailHit = this.currentSide == Side.Buy
                ? currentPrice <= this.bestPrice - trailDist
                : currentPrice >= this.bestPrice + trailDist;

            if (trailHit)
            {
                this.Log($"Trail stop hit — best:{this.bestPrice:F4}  current:{currentPrice:F4}  dist:{TrailingStopTicks}t", StrategyLoggingLevel.Trading);
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

        // Fires when a bar closes — this is the ONLY place signals are evaluated.
        private void Hdm_OnNewHistoryItem(object sender, HistoryEventArgs args)
        {
            this.OnBarClose();
        }

        // ── Core logic (runs on every bar close) ──────────────────────────────

        private void OnBarClose()
        {
            if (this.waitOpenPosition || this.waitClosePositions)
                return;

            // EMA values:
            //   GetValue(1) = bar that just closed (safe, fully formed)
            //   GetValue(2) = bar before that
            double micro1 = this.microEma.GetValue(1);
            double mid1   = this.midEma.GetValue(1);
            double micro2 = this.microEma.GetValue(2);
            double mid2   = this.midEma.GetValue(2);

            // Crossover detection — mirrors Pine's ta.crossover / ta.crossunder
            bool bullishCross = micro1 > mid1 && micro2 <= mid2;
            bool bearishCross = micro1 < mid1 && micro2 >= mid2;

            // Gap weakness — mirrors Pine's ta.falling(math.abs(emaFast - emaSlow), weakBars)
            // Only evaluated in WeaknessBars (0) or Both (2) modes.
            bool gapWeak = this.ExitMode != 1 && this.IsGapFalling(this.WeaknessBars);

            var positions = Core.Instance.Positions
                .Where(x => x.Symbol == this.CurrentSymbol && x.Account == this.CurrentAccount)
                .ToArray();

            if (positions.Any())
            {
                // ── Exit logic (mirrors Pine exitLong / exitShort) ─────────────
                bool inLong  = positions.Any(p => p.Side == Side.Buy);
                bool inShort = positions.Any(p => p.Side == Side.Sell);

                bool exitLong  = inLong  && (gapWeak || bearishCross);
                bool exitShort = inShort && (gapWeak || bullishCross);

                if (exitLong || exitShort)
                {
                    string reason = gapWeak ? $"gap weakness ({WeaknessBars} bars)" : "reverse cross";
                    this.Log($"Exit {(exitLong ? "LONG" : "SHORT")} — {reason}", StrategyLoggingLevel.Trading);

                    // Reverse cross → queue flip into new direction once close confirms
                    if (!gapWeak)
                    {
                        Side newSide = bullishCross ? Side.Buy : Side.Sell;
                        this.pendingEntrySide = newSide;
                    }

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
                    return; // don't evaluate entries on the same bar as an exit
                }
            }
            else
            {
                // ── Entry logic ───────────────────────────────────────────────
                if (this.inPosition)
                    return;

                // ── Impulse confirmation: previous bar was an impulse cross ───
                // Check whether the EMA alignment still holds before entering.
                if (this.pendingConfirmSide.HasValue)
                {
                    Side confirm = this.pendingConfirmSide.Value;
                    this.pendingConfirmSide = null;

                    bool crossStillHolds = confirm == Side.Buy
                        ? micro1 > mid1
                        : micro1 < mid1;

                    if (crossStillHolds)
                    {
                        this.Log($"Impulse confirmed — {confirm} EMA alignment still holds, entering.", StrategyLoggingLevel.Trading);
                        this.PlaceEntry(confirm);
                    }
                    else
                    {
                        // EMA reversed on the next bar — potential liquidity sweep.
                        // If a clean cross occurred in the opposite direction, enter that reversal.
                        Side reverseSide    = confirm == Side.Buy ? Side.Sell : Side.Buy;
                        bool reverseCrossed = confirm == Side.Buy ? bearishCross : bullishCross;

                        if (reverseCrossed)
                        {
                            this.Log($"Liquidity sweep \u2014 impulse {confirm} reversed to {reverseSide} cross, entering reversal.", StrategyLoggingLevel.Trading);
                            this.PlaceEntry(reverseSide);
                        }
                        else
                        {
                            this.Log($"Impulse stalled \u2014 {confirm} EMA lost but no reverse cross, skipping.", StrategyLoggingLevel.Trading);
                        }
                    }
                    return; // don't also process a fresh cross on this same bar
                }

                if (!bullishCross && !bearishCross)
                {
                    // ── HTF Mid EMA touch re-entry ────────────────────────────────────
                    // After a non-reverse exit, watch for price to touch the Mid EMA on
                    // the higher timeframe, then close the next 1m bar moving away from
                    // it in the original trend direction.
                    if (this.HtfTouchTicks > 0 && this.htfMidEma != null && this.lastExitSide.HasValue)
                    {
                        Side   exitDir   = this.lastExitSide.Value;
                        double close1    = HistoricalDataExtensions.Close(this.hdm, 1);
                        double close2    = HistoricalDataExtensions.Close(this.hdm, 2);
                        double htfEma    = this.htfMidEma.GetValue(0); // current HTF bar (updating)
                        double distTicks = Math.Abs(close1 - htfEma) / this.CurrentSymbol.TickSize;

                        // Abort if EMA alignment completely broke (actual cross on 1m)
                        bool alignmentHolds = exitDir == Side.Buy ? micro1 > mid1 : micro1 < mid1;
                        if (!alignmentHolds)
                        {
                            this.lastExitSide  = null;
                            this.htfTouchArmed = false;
                            return;
                        }

                        // Phase 1: arm once price is within threshold of the HTF EMA
                        if (!this.htfTouchArmed && distTicks <= this.HtfTouchTicks)
                        {
                            this.htfTouchArmed = true;
                            this.Log($"HTF EMA touch armed ({exitDir}) — price {close1:F2} within {distTicks:F1}t of {this.htfPeriod} Mid EMA {htfEma:F2}",
                                     StrategyLoggingLevel.Trading);
                        }

                        // Phase 2: once armed, enter when price closes back away from the HTF EMA in trend direction
                        if (this.htfTouchArmed)
                        {
                            double prevDistTicks = Math.Abs(close2 - htfEma) / this.CurrentSymbol.TickSize;
                            bool bouncingAway = exitDir == Side.Buy
                                ? close1 > htfEma && distTicks > prevDistTicks  // bouncing up
                                : close1 < htfEma && distTicks > prevDistTicks; // bouncing down

                            if (bouncingAway)
                            {
                                this.Log($"HTF EMA bounce re-entry ({exitDir}) — price {close1:F2} moving away from {this.htfPeriod} Mid EMA {htfEma:F2} ({prevDistTicks:F1}t → {distTicks:F1}t)",
                                         StrategyLoggingLevel.Trading);
                                this.lastExitSide  = null;
                                this.htfTouchArmed = false;
                                this.PlaceEntry(exitDir);
                            }
                        }
                    }
                    return;
                }

                Side entrySide = bullishCross ? Side.Buy : Side.Sell;

                // A fresh cross overrides any pending re-entry tracking
                this.lastExitSide  = null;
                this.htfTouchArmed = false;

                // ── Impulse candle filter ─────────────────────────────────────
                // If the cross bar's body is too large, wait one bar to confirm
                // direction before entering — avoids chasing spike reversals.
                if (this.ImpulseFilterTicks > 0)
                {
                    double open1     = HistoricalDataExtensions.Open(this.hdm, 1);
                    double close1    = HistoricalDataExtensions.Close(this.hdm, 1);
                    double bodyTicks = Math.Abs(close1 - open1) / this.CurrentSymbol.TickSize;

                    if (bodyTicks >= this.ImpulseFilterTicks)
                    {
                        this.pendingConfirmSide = entrySide;
                        this.Log($"Impulse candle on {entrySide} cross ({bodyTicks:F1}t body >= {ImpulseFilterTicks}t threshold) — waiting one bar to confirm.",
                                 StrategyLoggingLevel.Trading);
                        return;
                    }
                }

                this.PlaceEntry(entrySide);
            }
        }

        private void PlaceEntry(Side side)
        {
            this.Log($"Signal: {side} | Micro:{this.microEma.GetValue(1):F4}  Mid:{this.midEma.GetValue(1):F4}  " +
                     $"SL:{StopLossTicks}t" +
                     (TakeProfitTicks > 0 ? $"  TP:{TakeProfitTicks}t" : "") +
                     (TrailingStopTicks > 0 ? $"  Trail:activate@{TrailActivationTicks}t+{TrailingStopTicks}t" : ""),
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

        /// <summary>
        /// Auto-selects the higher timeframe based on the cross period.
        /// 1m → 3m | 3m → 5m | 5m → 15m | 15m → 1hr
        /// Returns default(Period) if the cross period is not in the known map.
        /// </summary>
        private Period DeriveHtfPeriod()
        {
            if (this.Period == Period.MIN1)  return Period.MIN3;
            if (this.Period == Period.MIN3)  return Period.MIN5;
            if (this.Period == Period.MIN5)  return Period.MIN15;
            if (this.Period == Period.HOUR1) return Period.HOUR4;
            // Check for 15-minute by value since there may not be a Period.MIN15 constant
            // Quantower Period equality works on type+value
            if (this.Period.ToString() == "15 Min" ||
                this.Period.ToString() == "MIN15"  ||
                this.Period.ToString() == "15m")
                return Period.HOUR1;
            return default;
        }

        /// <summary>
        /// Mirrors Pine's <c>ta.falling(math.abs(emaFast - emaSlow), bars)</c>.
        /// Returns true when the EMA gap has been strictly decreasing for
        /// <paramref name="bars"/> consecutive closed bars.
        /// bar index 1 = just-closed bar; index bars+1 = oldest bar in the window.
        /// </summary>
        private bool IsGapFalling(int bars)
        {
            for (int i = 1; i <= bars; i++)
            {
                double gapRecent = Math.Abs(this.microEma.GetValue(i)     - this.midEma.GetValue(i));
                double gapOlder  = Math.Abs(this.microEma.GetValue(i + 1) - this.midEma.GetValue(i + 1));
                if (gapRecent >= gapOlder)
                    return false;
            }
            return true;
        }

        private void ProcessTradingRefuse()
        {
            this.waitOpenPosition   = false;
            this.waitClosePositions = false;
            this.pendingEntrySide   = null;
            this.pendingConfirmSide = null;
            this.lastExitSide       = null;
            this.htfTouchArmed      = false;
        }
    }
}
