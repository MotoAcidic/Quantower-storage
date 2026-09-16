using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Strategies;
using ATAS.Strategies.Chart;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Utils.Common.Logging;
using Color = System.Drawing.Color;

namespace OceansAsiaWick
{
    /// <summary>
    /// AsiaWick Overnight -- Phase 2. Trades the signals AsiaWickLevels marks, one attempt a
    /// night, short only.
    ///
    /// PHASE GATE: this ships with "Signal only" ON. It does everything -- signals, stop maths,
    /// guards, logging -- and sends no orders. Do not turn it off until the Python/TradingView
    /// backtest of this exact logic shows PF > 1, beats the unconditional-short baseline, and
    /// survives MAE against the stop and the nightly cutoff. See README.md.
    ///
    /// The signal core is shared verbatim with the indicator (AsiaWickMath.cs), so what is
    /// traded is what was marked.
    /// </summary>
    [DisplayName("AsiaWick Overnight")]
    [Category("Ocean")]
    public class AsiaWickStrategy : ChartStrategy
    {
        private const int MaxBarMinutes = 60;

        private readonly AsiaWickSettings _cfg = new AsiaWickSettings();
        private readonly RiskSettings _risk = new RiskSettings();

        private AsiaWickEngine _engine;
        private SessionClock _clock;
        private BarWindow _bars;

        private BarClock _requestedClock = BarClock.Auto;
        private string _zoneId = "Central Standard Time";

        private int _next;
        private bool _ready;
        private bool _historyDone;
        private int _barMinutes;
        private string _problem;
        private string _status = "";

        private HashSet<DateTime> _tier1 = new HashSet<DateTime>();

        // --- per-night execution state ---
        private DateTime _night = DateTime.MinValue;
        private bool _enteredTonight;
        private bool _disabledTonight;
        private bool _lossHitTonight;
        private decimal _pnlAtNightStart;
        private decimal _pendingStopRef;

        private Order _entryOrder;
        private Order _stopOrder;

        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        public AsiaWickStrategy() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
        }

        // ------------------------------------------------------------------ 01 Sessions

        [Display(Name = "Time zone id", GroupName = "01 Sessions", Order = 100)]
        public string ZoneId
        {
            get { return _zoneId; }
            set { _zoneId = value; Redo(); }
        }

        [Display(Name = "Bar clock", GroupName = "01 Sessions", Order = 110,
                 Description = "Auto resolves UTC vs Central from the data and refuses to guess.")]
        public BarClock ClockMode
        {
            get { return _requestedClock; }
            set { _requestedClock = value; Redo(); }
        }

        [Display(Name = "Asia window start", GroupName = "01 Sessions", Order = 120)]
        public TimeSpan AsiaStart
        {
            get { return _cfg.AsiaStart; }
            set { _cfg.AsiaStart = value; Redo(); }
        }

        [Display(Name = "Asia window end", GroupName = "01 Sessions", Order = 130)]
        public TimeSpan AsiaEnd
        {
            get { return _cfg.AsiaEnd; }
            set { _cfg.AsiaEnd = value; Redo(); }
        }

        [Display(Name = "RTH start (prior day high)", GroupName = "01 Sessions", Order = 140)]
        public TimeSpan RthStart
        {
            get { return _cfg.RthStart; }
            set { _cfg.RthStart = value; Redo(); }
        }

        [Display(Name = "RTH end (prior day high)", GroupName = "01 Sessions", Order = 150)]
        public TimeSpan RthEnd
        {
            get { return _cfg.RthEnd; }
            set { _cfg.RthEnd = value; Redo(); }
        }

        [Display(Name = "Morning exit", GroupName = "01 Sessions", Order = 160,
                 Description = "First closed bar at or after this flattens the position.")]
        public ExitTime Exit
        {
            get { return _cfg.Exit; }
            set { _cfg.Exit = value; Redo(); }
        }

        // ------------------------------------------------------------------ 02 Signals

        [Display(Name = "PDH sweep", GroupName = "02 Signals", Order = 200)]
        public bool EnablePdhSweep
        {
            get { return _cfg.EnablePdhSweep; }
            set { _cfg.EnablePdhSweep = value; Redo(); }
        }

        [Display(Name = "Asia-high sweep", GroupName = "02 Signals", Order = 210)]
        public bool EnableAsiaHighSweep
        {
            get { return _cfg.EnableAsiaHighSweep; }
            set { _cfg.EnableAsiaHighSweep = value; Redo(); }
        }

        [Display(Name = "Wick rejection", GroupName = "02 Signals", Order = 220)]
        public bool EnableWickRejection
        {
            get { return _cfg.EnableWickRejection; }
            set { _cfg.EnableWickRejection = value; Redo(); }
        }

        [Display(Name = "Wick % of range", GroupName = "02 Signals", Order = 230)]
        [Range(0.01, 1.0)]
        public decimal WickPct
        {
            get { return _cfg.WickPct; }
            set { _cfg.WickPct = value; Redo(); }
        }

        // ------------------------------------------------------------------ 03 Execution

        [Display(Name = "Signal only (no orders)", GroupName = "03 Execution", Order = 300,
                 Description = "Ships ON. Everything runs and nothing is sent. Turn off only " +
                               "after the backtest gate and a run of sim nights.")]
        public bool SignalOnly
        {
            get { return _risk.SignalOnly; }
            set { _risk.SignalOnly = value; }
        }

        [Display(Name = "Quantity", GroupName = "03 Execution", Order = 310,
                 Description = "Also the hard position cap. This never adds and never averages.")]
        [Range(1, 100)]
        public int Quantity
        {
            get { return _risk.Quantity; }
            set { _risk.Quantity = value; }
        }

        [Display(Name = "Stop (ticks above signal high)", GroupName = "03 Execution", Order = 320)]
        [Range(1, 1000)]
        public int StopTicks
        {
            get { return _risk.StopTicks; }
            set { _risk.StopTicks = value; }
        }

        // ------------------------------------------------------------------ 04 Risk

        [Display(Name = "Tier-1 news guard", GroupName = "04 Risk", Order = 400,
                 Description = "Funded-account rule: flat at least 2 minutes before CPI / Employment / FOMC.")]
        public bool Tier1Guard
        {
            get { return _risk.Tier1Guard; }
            set { _risk.Tier1Guard = value; }
        }

        [Display(Name = "Tier-1 dates", GroupName = "04 Risk", Order = 410,
                 Description = "Comma-separated yyyy-MM-dd. Each is the calendar date of the " +
                               "release MORNING, not the night before. Bad entries are logged " +
                               "loudly, never skipped quietly.")]
        public string Tier1Dates
        {
            get { return _risk.Tier1Dates; }
            set { _risk.Tier1Dates = value; ReloadTier1(); }
        }

        [Display(Name = "Flat by (tier-1 mornings)", GroupName = "04 Risk", Order = 420)]
        public TimeSpan Tier1FlatAt
        {
            get { return _risk.Tier1FlatAt; }
            set { _risk.Tier1FlatAt = value; }
        }

        [Display(Name = "No Friday / weekend exposure", GroupName = "04 Risk", Order = 430)]
        public bool BlockFriday
        {
            get { return _risk.BlockFriday; }
            set { _risk.BlockFriday = value; }
        }

        [Display(Name = "Nightly loss cutoff ($)", GroupName = "04 Risk", Order = 440,
                 Description = "Flatten and stand down for the night once the night is this far " +
                               "down. Zero disables.")]
        public decimal NightlyLossLimit
        {
            get { return _risk.NightlyLossLimit; }
            set { _risk.NightlyLossLimit = value; }
        }

        [Display(Name = "Status line", GroupName = "04 Risk", Order = 450)]
        public bool ShowStatus { get; set; } = true;

        // ------------------------------------------------------------------ lifecycle

        private void Redo()
        {
            _ready = false;
            _next = 0;
            RecalculateValues();
        }

        private void ReloadTier1()
        {
            var bad = new List<string>();
            _tier1 = AsiaWickUtil.ParseDates(_risk.Tier1Dates, bad);

            // A news date that silently failed to parse is a rule breach waiting to happen.
            foreach (var b in bad)
                this.LogWarn("AsiaWick: tier-1 date '{0}' is not yyyy-MM-dd and is being IGNORED. " +
                             "Fix it before trading a news night.", b);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
            {
                Setup();
                return;
            }

            if (!_ready) return;

            while (_next <= bar - 1)
            {
                FoldOne(_next);
                _next++;
            }
        }

        protected override void OnFinishRecalculate()
        {
            _historyDone = true;
        }

        private void Setup()
        {
            _problem = null;
            _historyDone = false;
            _next = 0;

            _bars = new BarWindow(this);
            _barMinutes = AsiaWickUtil.BarMinutes(_bars);

            ReloadTier1();

            if (_barMinutes > MaxBarMinutes)
            {
                _problem = "AsiaWick: " + _barMinutes + "m chart. Designed for 15m-30m.";
                this.LogWarn(_problem);
                _ready = false;
                return;
            }

            _clock = SessionClock.Create(_zoneId, _requestedClock, _bars,
                                         DateTime.UtcNow, SessionClock.MnqHaltHourCt);

            if (!_clock.Valid)
            {
                _problem = _clock.Error;
                this.LogWarn(_problem);
                _ready = false;
                return;
            }

            _engine = new AsiaWickEngine(_cfg, _clock);
            _engine.Reset();
            _ready = true;
        }

        private void FoldOne(int bar)
        {
            var c = GetCandle(bar);
            var fresh = new List<WickSignal>();

            var st = _engine.Fold(bar, c.Time, c.Open, c.High, c.Low, c.Close, fresh);

            RollNightIfNeeded(st.TradeDate);

            _status = BuildStatus(st, GuardReason.None);

            // History is for building state only. Nothing is ever sent for a bar that already
            // happened, and ATAS replays history on every recalculation.
            if (!_historyDone) return;
            if (bar < CurrentBar - 2) return;
            if (State != StrategyStates.Started) return;
            if (!CanProcess(bar)) return;

            CheckLossLimit();

            // 1. Get out if anything says we must be flat, whether or not we think we are.
            var flat = NightGuard.FlattenReason(st.TimeCt, _cfg.ExitAt, _risk, _tier1, _lossHitTonight);

            if (flat != GuardReason.None)
            {
                _status = BuildStatus(st, flat);
                if (CurrentPosition != 0m || WorkingOrders().Any()) FlattenNow(flat);
                return;
            }

            // 2. Only then consider getting in.
            if (fresh.Count == 0) return;

            var block = NightGuard.EntryBlock(st.TimeCt, st.TradeDate, _cfg.ExitAt, _risk,
                                              _tier1, !_enteredTonight, _lossHitTonight,
                                              _disabledTonight);

            _status = BuildStatus(st, block);

            var sig = fresh[0];

            if (block != GuardReason.None)
            {
                this.LogInfo("AsiaWick: {0} at {1:HH:mm} CT NOT taken -- {2}.",
                             sig.Variant, sig.TimeCt, block);
                return;
            }

            // Hard cap: never add, never average, never reverse.
            if (CurrentPosition != 0m)
            {
                this.LogWarn("AsiaWick: {0} ignored -- position is already {1}.",
                             sig.Variant, CurrentPosition);
                return;
            }

            Enter(sig);
        }

        private void RollNightIfNeeded(DateTime tradeDate)
        {
            if (tradeDate == _night) return;

            _night = tradeDate;
            _enteredTonight = false;
            _disabledTonight = false;
            _lossHitTonight = false;
            _pnlAtNightStart = TotalPnL;
            _pendingStopRef = 0m;
        }

        private decimal TotalPnL
        {
            get { return ClosedPnL + OpenPnL; }
        }

        private void CheckLossLimit()
        {
            if (_lossHitTonight) return;
            if (_risk.NightlyLossLimit <= 0m) return;

            var nightPnl = TotalPnL - _pnlAtNightStart;

            if (nightPnl <= -_risk.NightlyLossLimit)
            {
                _lossHitTonight = true;
                this.LogWarn("AsiaWick: nightly loss cutoff hit ({0:0.00} vs limit {1:0.00}). " +
                             "Flat and stood down for the night.",
                             nightPnl, _risk.NightlyLossLimit);
            }
        }

        // ------------------------------------------------------------------ orders

        private IEnumerable<Order> WorkingOrders()
        {
            var all = Orders;
            if (all == null) return Enumerable.Empty<Order>();

            return all.Where(o => o != null && o.State == OrderStates.Active);
        }

        private void Enter(WickSignal sig)
        {
            if (Portfolio == null || Security == null)
            {
                this.LogWarn("AsiaWick: no portfolio/security yet -- entry skipped.");
                return;
            }

            // Marked spent before the order is sent, not after. If the send throws or the fill
            // never confirms, the night is still over -- one attempt means one attempt.
            _enteredTonight = true;
            _pendingStopRef = sig.StopReference;

            var order = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = OrderDirections.Sell,
                Type = OrderTypes.Market,
                QuantityToFill = _risk.Quantity,
                Comment = "AsiaWick " + sig.Variant
            };

            _entryOrder = order;

            this.LogInfo("AsiaWick: SHORT {0} on {1} at {2:HH:mm} CT, signal {3}, stop-ref {4}.",
                         _risk.Quantity, sig.Variant, sig.TimeCt, sig.SignalPrice,
                         sig.StopReference);

            OpenOrder(order);
        }

        /// <summary>
        /// The stop goes on only once a fill is confirmed. Placing it off the entry SEND would
        /// leave a naked stop working if the entry was rejected.
        /// </summary>
        protected override void OnNewMyTrade(MyTrade myTrade)
        {
            base.OnNewMyTrade(myTrade);

            if (_pendingStopRef <= 0m) return;
            if (CurrentPosition >= 0m) return;      // not short yet
            if (_stopOrder != null) return;

            PlaceStop(_pendingStopRef);
            _pendingStopRef = 0m;
        }

        private void PlaceStop(decimal stopRef)
        {
            if (Portfolio == null || Security == null) return;

            // InstrumentInfo.TickSize, not the deprecated Indicator.TickSize. A zero here would
            // silently price the stop at the signal high itself, so it is checked, never defaulted.
            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;

            if (tick <= 0m)
            {
                this.LogError("AsiaWick: tick size is {0} -- cannot price the stop. FLATTENING.",
                              tick);
                FlattenNow(GuardReason.Disabled);
                _disabledTonight = true;
                return;
            }

            var raw = NightGuard.StopPrice(stopRef, _risk.StopTicks, tick);
            var price = ShrinkPrice(raw);

            var stop = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = OrderDirections.Buy,
                Type = OrderTypes.Stop,
                QuantityToFill = Math.Abs(CurrentPosition),
                TriggerPrice = price,
                Comment = "AsiaWick stop"
            };

            _stopOrder = stop;

            this.LogInfo("AsiaWick: protective stop at {0} ({1} ticks above {2}).",
                         price, _risk.StopTicks, stopRef);

            OpenOrder(stop);
        }

        private void FlattenNow(GuardReason why)
        {
            foreach (var o in WorkingOrders().ToList())
            {
                try { CancelOrder(o); }
                catch (Exception ex) { this.LogError("AsiaWick: cancel failed: " + ex.Message); }
            }

            _stopOrder = null;

            var pos = CurrentPosition;
            if (pos == 0m) return;

            if (Portfolio == null || Security == null)
            {
                this.LogError("AsiaWick: MUST FLATTEN ({0}) but portfolio/security is null. " +
                              "Position {1} is NOT closed -- close it by hand.", why, pos);
                return;
            }

            var exit = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = pos < 0m ? OrderDirections.Buy : OrderDirections.Sell,
                Type = OrderTypes.Market,
                QuantityToFill = Math.Abs(pos),
                Comment = "AsiaWick flat: " + why
            };

            this.LogInfo("AsiaWick: flattening {0} -- {1}.", pos, why);

            OpenOrder(exit);
        }

        protected override void OnOrderRegisterFailed(Order order, string message)
        {
            base.OnOrderRegisterFailed(order, message);

            _disabledTonight = true;

            this.LogError("AsiaWick: order rejected ({0}): {1}. Stood down for the night.",
                          order == null ? "?" : order.Comment, message);

            if (order != null && _stopOrder != null && ReferenceEquals(order, _stopOrder))
            {
                // The protective stop is the one rejection we cannot sit on.
                _stopOrder = null;
                this.LogError("AsiaWick: the PROTECTIVE STOP was rejected -- flattening now.");
                FlattenNow(GuardReason.Disabled);
            }
        }

        protected override void OnOrderCancelFailed(Order order, string message)
        {
            base.OnOrderCancelFailed(order, message);
            this.LogWarn("AsiaWick: cancel failed ({0}): {1}.",
                         order == null ? "?" : order.Comment, message);
        }

        protected override void OnCurrentPositionChanged()
        {
            base.OnCurrentPositionChanged();

            if (CurrentPosition != 0m) return;

            // Flat again: the stop, if any, has done its job or been pulled.
            foreach (var o in WorkingOrders().ToList())
            {
                if (o.Comment != null && o.Comment.StartsWith("AsiaWick stop"))
                {
                    try { CancelOrder(o); } catch { }
                }
            }

            _stopOrder = null;
            _pendingStopRef = 0m;
        }

        protected override void OnStopping()
        {
            // ATAS resets the strategy-internal position on stop. Anything left working here
            // would be orphaned against a position the strategy no longer believes it has.
            try
            {
                foreach (var o in WorkingOrders().ToList())
                {
                    try { CancelOrder(o); } catch { }
                }

                if (CurrentPosition != 0m)
                {
                    this.LogWarn("AsiaWick: stopping with position {0} open -- flattening.",
                                 CurrentPosition);
                    FlattenNow(GuardReason.Disabled);
                }
            }
            catch (Exception ex)
            {
                this.LogError("AsiaWick: OnStopping: " + ex.Message);
            }

            _stopOrder = null;
            _entryOrder = null;
            _pendingStopRef = 0m;

            base.OnStopping();
        }

        protected override void OnStarted()
        {
            base.OnStarted();

            // Reconnects and restarts land here with whatever the account actually holds.
            if (CurrentPosition != 0m)
                this.LogWarn("AsiaWick: started with an existing position of {0}. It will not be " +
                             "managed by this strategy until it is flat.", CurrentPosition);

            if (_risk.SignalOnly)
                this.LogInfo("AsiaWick: SIGNAL ONLY. Signals are logged; no order will be sent.");
        }

        // ------------------------------------------------------------------ status

        private string BuildStatus(BarState st, GuardReason why)
        {
            var mode = _risk.SignalOnly ? "SIGNAL-ONLY" : "LIVE";
            var night = _enteredTonight ? "spent" : (_disabledTonight ? "stood down" : "armed");

            return string.Format(CultureInfo.InvariantCulture,
                "AsiaWick {0}  {1:yyyy-MM-dd} {2}  |  pos {3}  night P/L {4:0.00}  |  {5}  |  {6}m, clock {7}",
                mode, st.TradeDate, night, CurrentPosition, TotalPnL - _pnlAtNightStart,
                why == GuardReason.None ? (st.InAsia ? "in Asia" : "outside") : why.ToString(),
                _barMinutes, _clock == null ? "?" : _clock.Clock.ToString());
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            try
            {
                if (_problem != null)
                {
                    context.DrawString(_problem, _statusFont, Color.OrangeRed,
                                       ChartArea.Left + 6, ChartArea.Top + 20);
                    return;
                }

                if (ShowStatus && _ready && !string.IsNullOrEmpty(_status))
                    context.DrawString(_status, _statusFont,
                                       _risk.SignalOnly ? Color.Gainsboro : Color.Gold,
                                       ChartArea.Left + 6, ChartArea.Top + 20);
            }
            catch (Exception ex)
            {
                try
                {
                    context.DrawString("AsiaWick render: " + ex.Message, _statusFont,
                                       Color.OrangeRed, ChartArea.Left + 6, ChartArea.Top + 20);
                }
                catch { }
            }
        }

        private sealed class BarWindow : IBarWindow
        {
            private readonly AsiaWickStrategy _o;

            public BarWindow(AsiaWickStrategy o) { _o = o; }

            public int Count { get { return _o.CurrentBar; } }
            public DateTime Time(int b) { return _o.GetCandle(b).Time; }
            public decimal Open(int b) { return _o.GetCandle(b).Open; }
            public decimal High(int b) { return _o.GetCandle(b).High; }
            public decimal Low(int b) { return _o.GetCandle(b).Low; }
            public decimal Close(int b) { return _o.GetCandle(b).Close; }
        }
    }
}
