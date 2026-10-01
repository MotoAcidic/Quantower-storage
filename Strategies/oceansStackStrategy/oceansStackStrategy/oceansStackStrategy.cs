using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FinchLite;
using TradingPlatform.BusinessLayer;

namespace oceansStackStrategy;

/// <summary>
/// PORTED 2026-09-29 from `Quantower-storage/TradingView/ocean.pine` ("Ocean's Stack v2") — a
/// session-based LIQUIDITY SWEEP FAILURE swing system for NQ, handed to the operator as a Pine
/// indicator: build the prior day's value area (VAH/VAL/POC) and a prior-week version, score each
/// value-area edge on absorption + low-volume-ledge evidence, arm a zone when a nearby liquidity
/// pool (overnight/Asia/prior-day/prior-week high-or-low) sits in range, then — only inside a
/// specific morning window — watch for price to sweep that pool and fail back inside the value
/// area on confirming delta. Entry at the failed-sweep bar's close, stop beyond the sweep extreme,
/// target at the opposite value-area edge.
///
/// FIVE decisions confirmed via `AskUserQuestion` before any code was written (see the plan at
/// the time, `starry-petting-pike.md`):
/// - **Real order-flow delta**, not Pine's candle proxy (`sign(close-open)*volume`) — classified
///   from actual tick prints via the shared `TickClassifier` (extracted from `FinchLiteIndicator`/
///   `finchDomScalpStrategy`'s own two independent copies specifically for this new consumer).
/// - **No dry-run/sim-account gate** — matches `finchDomScalpStrategy`'s own explicit policy.
///   Places real orders immediately once attached and enabled.
/// - **Full port, everything at once** — weekly overlay, QQQ-&gt;NQ cross-asset check (informational
///   only, never gates the signal — see `EvaluateQqq`), and LVN ledge scoring all included from
///   day one.
/// - **Instrument: MNQ**, same account as `finchDomScalpStrategy`.
/// - **Exit structure**: Pine names two targets (T1 = prior-day POC, T2 = opposite value-area
///   edge), but nothing in this codebase does partial/scale-out exits. Single contract, target T2
///   ONLY — T1/POC is logged as an informational level on the signal line, never traded.
///
/// ONE deliberate, load-bearing divergence from the "real order flow over Pine's proxies" default:
/// `ValueAreaEngine` is fed from HISTORICAL 1-MINUTE BARS, not live ticks. Quantower has no
/// historical tick/time-and-sales backfill (the same limitation Finch-Lite's own `PocEngine`
/// already documents) — prior-day/prior-week value areas must already be COMPLETE the instant this
/// strategy attaches, which only bar history can supply. See `ValueAreaEngine`'s own doc comment.
///
/// Architecture is modeled directly on `finchDomScalpStrategy.cs` wherever a pattern already
/// exists there: separate stop/target orders (not an embedded bracket), `StrategyTag`-based
/// position isolation via `MyPositions()`/`MyOrders()`, the `CheckSessionReset`/`CheckRiskLimits`
/// daily-loss/drawdown/trade-count circuit breakers, idempotent event subscription, a poll `Timer`
/// wrapped in its own try/catch so nothing can escape and crash the platform, and `[Signal
/// skipped]`/`[Heartbeat]` observability logging from day one rather than bolted on later.
/// </summary>
public sealed class oceansStackStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    private const string StrategyTag = "OceansStack";

    // ---- instrument/account -----------------------------------------------------------------

    [InputParameter("Symbol", 0)]
    public Symbol CurrentSymbol { get; set; }

    [InputParameter("Account", 1)]
    public Account CurrentAccount { get; set; }

    /// <summary>Informational only (see the class doc comment) — the QQQ-&gt;NQ cross-asset check
    /// never gates the score, armed state, or fire trigger, matching the source script's own
    /// design. Likely needs a SEPARATE, equity-capable connection from whatever MNQ trades on;
    /// leave unresolved/unset to disable the whole QQQ pathway (it degrades to a persistent
    /// "unavailable" log line, never blocking the real NQ-only pipeline).</summary>
    [InputParameter("QQQ Symbol (informational only, optional)", 2)]
    public Symbol QqqSymbol { get; set; }

    [InputParameter("Period", 3)]
    public Period Period { get; set; }

    [InputParameter("Start Point", 4)]
    public DateTime StartPoint { get; set; }

    [InputParameter("Quantity", 5, 1, 1000000, 1, 0)]
    public int Quantity { get; set; }

    [InputParameter("Poll interval (ms)", 6, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; }

    // ---- profile ------------------------------------------------------------------------------

    [InputParameter("NQ bin size (ticks)", 10, 1, 100000, 1, 0)]
    public int NqBinTicks { get; set; }

    [InputParameter("QQQ bin size ($, informational only)", 11, 0.01, 10, 0.01, 2)]
    public double QqqBinDollars { get; set; }

    [InputParameter("Value area (%)", 12, 50, 95, 1, 0)]
    public int ValueAreaPercent { get; set; }

    // ---- sessions (all America/New_York, converted from the Pine script's America/Chicago) ----

    [InputParameter("RTH: start hour (ET)", 15, 0, 23, 1, 0)]
    public int RthStartHour { get; set; }
    [InputParameter("RTH: start minute", 16, 0, 59, 1, 0)]
    public int RthStartMinute { get; set; }
    [InputParameter("RTH: end hour (ET)", 17, 0, 23, 1, 0)]
    public int RthEndHour { get; set; }
    [InputParameter("RTH: end minute", 18, 0, 59, 1, 0)]
    public int RthEndMinute { get; set; }

    [InputParameter("Overnight: start hour (ET)", 19, 0, 23, 1, 0)]
    public int OvernightStartHour { get; set; }
    [InputParameter("Overnight: start minute", 20, 0, 59, 1, 0)]
    public int OvernightStartMinute { get; set; }
    [InputParameter("Overnight: end hour (ET)", 21, 0, 23, 1, 0)]
    public int OvernightEndHour { get; set; }
    [InputParameter("Overnight: end minute", 22, 0, 59, 1, 0)]
    public int OvernightEndMinute { get; set; }

    [InputParameter("Asia: start hour (ET)", 23, 0, 23, 1, 0)]
    public int AsiaStartHour { get; set; }
    [InputParameter("Asia: start minute", 24, 0, 59, 1, 0)]
    public int AsiaStartMinute { get; set; }
    [InputParameter("Asia: end hour (ET)", 25, 0, 23, 1, 0)]
    public int AsiaEndHour { get; set; }
    [InputParameter("Asia: end minute", 26, 0, 59, 1, 0)]
    public int AsiaEndMinute { get; set; }

    /// <summary>The ONLY window the sweep-and-fail trigger itself is ever evaluated in — everything
    /// else (value area, pools, absorption, LVN) accumulates all day regardless.</summary>
    [InputParameter("Sweep window: start hour (ET)", 27, 0, 23, 1, 0)]
    public int SweepStartHour { get; set; }
    [InputParameter("Sweep window: start minute", 28, 0, 59, 1, 0)]
    public int SweepStartMinute { get; set; }
    [InputParameter("Sweep window: end hour (ET)", 29, 0, 23, 1, 0)]
    public int SweepEndHour { get; set; }
    [InputParameter("Sweep window: end minute", 30, 0, 59, 1, 0)]
    public int SweepEndMinute { get; set; }

    [InputParameter("QQQ RTH: start hour (ET)", 31, 0, 23, 1, 0)]
    public int QqqRthStartHour { get; set; }
    [InputParameter("QQQ RTH: start minute", 32, 0, 59, 1, 0)]
    public int QqqRthStartMinute { get; set; }
    [InputParameter("QQQ RTH: end hour (ET)", 33, 0, 23, 1, 0)]
    public int QqqRthEndHour { get; set; }
    [InputParameter("QQQ RTH: end minute", 34, 0, 59, 1, 0)]
    public int QqqRthEndMinute { get; set; }

    // ---- absorption proxy — REAL tick-classified delta, not Pine's candle proxy ---------------

    [InputParameter("Absorption: bar volume >= x * 50-bar SMA", 40, 0.1, 10, 0.1, 1)]
    public double AbsVolumeMultiplier { get; set; }

    [InputParameter("Absorption: |delta| >= x * bar volume", 41, 0.01, 1, 0.01, 2)]
    public double AbsDeltaFraction { get; set; }

    [InputParameter("Absorption: must hold intraday", 42)]
    public bool AbsMustHold { get; set; }

    [InputParameter("Absorption: hold-break tolerance (ticks)", 43, 0, 100000, 1, 0)]
    public int AbsHoldToleranceTicks { get; set; }

    [InputParameter("Absorption: counts near edge within (ticks)", 44, 1, 100000, 1, 0)]
    public int AbsNearTicks { get; set; }

    // ---- LVN ledge ----------------------------------------------------------------------------

    [InputParameter("LVN: bin volume <= % of max bin", 50, 1, 100, 1, 0)]
    public int LvnMaxPercentOfMaxBin { get; set; }

    [InputParameter("LVN: band beyond edge (ticks)", 51, 1, 100000, 1, 0)]
    public int LvnBandTicks { get; set; }

    // ---- fuel pools -----------------------------------------------------------------------------

    [InputParameter("Fuel: pool minimum distance beyond edge (ticks)", 55, 1, 100000, 1, 0)]
    public int PoolMinTicks { get; set; }

    [InputParameter("Fuel: pool maximum distance beyond edge (ticks)", 56, 1, 100000, 1, 0)]
    public int PoolMaxTicks { get; set; }

    [InputParameter("Fuel: overnight H/L enable", 57)]
    public bool UseOvernightPool { get; set; }

    [InputParameter("Fuel: Asia H/L enable", 58)]
    public bool UseAsiaPool { get; set; }

    [InputParameter("Fuel: prior-day H/L enable", 59)]
    public bool UsePriorDayPool { get; set; }

    // ---- QQQ -> NQ cross-asset check (informational only, never gates the signal) --------------

    [InputParameter("QQQ->NQ: enable", 65)]
    public bool UseQqq { get; set; }

    /// <summary>False = Pine's "Prior RTH close" anchor (a ratio fixed once QQQ's own RTH session
    /// ends, best-effort against NQ's own last-seen close at that moment — real bar-alignment
    /// imprecision between two different exchanges' 1-minute series is unavoidable here and this
    /// pathway is informational-only). True = Pine's "Live" anchor (always the current-moment
    /// ratio) — simpler, and the one to prefer if the fixed-anchor approximation looks noisy in
    /// practice.</summary>
    [InputParameter("QQQ->NQ: use live ratio (vs. prior RTH close)", 66)]
    public bool QqqRatioUseLive { get; set; }

    [InputParameter("QQQ->NQ: log only if disagreement >= (ticks)", 67, 0, 100000, 1, 0)]
    public int QqqDisagreementTicks { get; set; }

    // ---- weekly overlay -------------------------------------------------------------------------

    [InputParameter("Weekly: enable", 70)]
    public bool WeeklyEnabled { get; set; }

    /// <summary>Pine's own `wkFuel` — unlike the rest of the weekly group (which is purely
    /// informational logging), this ONE toggle is load-bearing: prior-week H/L becomes a real
    /// fourth fuel-pool candidate when on.</summary>
    [InputParameter("Weekly: prior-week H/L counts as fuel", 71)]
    public bool WeeklyHighLowAsFuel { get; set; }

    // ---- signal ---------------------------------------------------------------------------------

    [InputParameter("Signal: minimum score to arm (1-3)", 75, 1, 3, 1, 0)]
    public int MinScoreToArm { get; set; }

    [InputParameter("Signal: stop buffer beyond sweep extreme (ticks)", 76, 0, 100000, 1, 0)]
    public int StopBufferTicks { get; set; }

    [InputParameter("Signal: minimum stop distance (ticks)", 77, 1, 100000, 1, 0)]
    public int MinStopDistanceTicks { get; set; }

    [InputParameter("Signal: minimum target distance (ticks)", 78, 1, 100000, 1, 0)]
    public int MinTargetDistanceTicks { get; set; }

    // ---- risk management — same shape as finchDomScalpStrategy's own --------------------------

    [InputParameter("Max daily loss ($, 0=off)", 80, 0, 100000, 50, 0)]
    public int MaxDailyLoss { get; set; }

    [InputParameter("Max drawdown ($, 0=off)", 81, 0, 100000, 50, 0)]
    public int MaxDrawdown { get; set; }

    [InputParameter("Max trades per session (0=off)", 82, 0, 200, 1, 0)]
    public int MaxTradesPerSession { get; set; }

    [InputParameter("Cooldown between entries (bars)", 83, 0, 500, 1, 0)]
    public int MinBarsBetweenEntries { get; set; }

    // ---- diagnostics ----------------------------------------------------------------------------

    [InputParameter("Heartbeat: log interval (minutes, 0=off)", 90, 0, 1440, 1, 0)]
    public int HeartbeatIntervalMinutes { get; set; }

    // ---- lifecycle state --------------------------------------------------------------------

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private Timer? pollTimer;
    private HistoricalData? hdmNq;
    private HistoricalData? hdmQqq;

    private ValueAreaEngine? dailyVa;
    private ValueAreaEngine? weeklyVa;
    private ValueAreaEngine? qqqVa;
    private SessionPoolTracker? pools;
    private AbsorptionTracker? absorption;
    private SweepZoneTracker? topZone;    // +1, short
    private SweepZoneTracker? bottomZone; // -1, long
    private DeltaTracker? deltaTracker;
    private readonly ConcurrentQueue<(DateTime TimeUtc, double Size, bool IsBuy)> deltaTickQueue = new();

    private int barsSeenNq = -1;
    private int sweepCheckedUpTo = -1;
    private bool wasInRthNq;
    private int lastWeekKeyNq = -1;

    private int barsSeenQqq = -1;
    private bool wasInRthQqq;
    private double? qqqCloseAtRthEnd;
    private double? nqLastClose;
    private double? qqqRatioFixed;

    private double? priorDayPoc, priorDayVah, priorDayVal;
    private double? priorWeekPoc, priorWeekVah, priorWeekVal;

    private int barCounter;
    private int lastEntryBarIndex;

    private bool waitOpenPosition;
    private bool waitClosePositions;
    private bool protectiveOrdersPlaced;
    private double pendingStopPrice;
    private double pendingTargetPrice;

    private string? orderTypeId;
    private string? stopOrderTypeId;
    private string? limitOrderTypeId;
    private string? resolvedSymbolId;

    private double dailyPnl;
    private int lastResetDay = -1;
    private bool dailyLimitHit;
    private double totalRealizedPnl;
    private double peakEquity;
    private bool drawdownLimitHit;
    private int tradesThisSession;
    private int lastTradeSessionDay = -1;
    private bool tradesLimitHit;

    private DateTime runStartUtc;
    private DateTime lastHeartbeatUtc;
    private DateTime? lastTradeOpenedUtc;

    private string? lastPollFault;
    private string? lastQqqFault;

    public oceansStackStrategy() : base()
    {
        this.Name = "oceansStackStrategy";
        this.Description =
            "Ports 'Ocean's Stack v2' (Quantower-storage/TradingView/ocean.pine) -- a session-based "
            + "liquidity sweep failure swing system for NQ. NO dry-run, NO sim/eval confirmation "
            + "gate -- places real orders immediately once attached and enabled. Attach to a "
            + "sim/eval account yourself; nothing in this code checks that for you.";

        this.Period = Period.MIN1;
        this.StartPoint = Core.TimeUtils.DateTimeUtcNow.AddDays(-21); // enough for weekly + absorption context on first attach
        this.Quantity = 1;
        this.PollIntervalMs = 250;

        this.NqBinTicks = 10;       // 2.5 points at MNQ's 0.25 tick size, matching Pine's own binPts default
        this.QqqBinDollars = 0.05;
        this.ValueAreaPercent = 70;

        this.RthStartHour = 9; this.RthStartMinute = 30; this.RthEndHour = 16; this.RthEndMinute = 0;
        this.OvernightStartHour = 18; this.OvernightStartMinute = 0; this.OvernightEndHour = 9; this.OvernightEndMinute = 30;
        this.AsiaStartHour = 20; this.AsiaStartMinute = 0; this.AsiaEndHour = 2; this.AsiaEndMinute = 0;
        this.SweepStartHour = 9; this.SweepStartMinute = 30; this.SweepEndHour = 10; this.SweepEndMinute = 30;
        this.QqqRthStartHour = 9; this.QqqRthStartMinute = 30; this.QqqRthEndHour = 16; this.QqqRthEndMinute = 0;

        this.AbsVolumeMultiplier = 1.2;
        this.AbsDeltaFraction = 0.15;
        this.AbsMustHold = true;
        this.AbsHoldToleranceTicks = 20;   // 5 points
        this.AbsNearTicks = 60;            // 15 points

        this.LvnMaxPercentOfMaxBin = 30;
        this.LvnBandTicks = 40;            // 10 points

        this.PoolMinTicks = 20;            // 5 points
        this.PoolMaxTicks = 320;           // 80 points
        this.UseOvernightPool = true;
        this.UseAsiaPool = true;
        this.UsePriorDayPool = true;

        this.UseQqq = true;
        this.QqqRatioUseLive = false;
        this.QqqDisagreementTicks = 12;    // 3 points

        this.WeeklyEnabled = true;
        this.WeeklyHighLowAsFuel = true;

        this.MinScoreToArm = 2;
        this.StopBufferTicks = 16;         // 4 points
        this.MinStopDistanceTicks = 20;
        this.MinTargetDistanceTicks = 40;

        this.MaxDailyLoss = 0;
        this.MaxDrawdown = 2000;
        this.MaxTradesPerSession = 3;      // swing-sized, deliberately far lower than a scalp strategy's own default
        this.MinBarsBetweenEntries = 5;

        this.HeartbeatIntervalMinutes = 15;
    }

    // ---- lifecycle ----------------------------------------------------------------------------

    protected override void OnRun()
    {
        this.runStartUtc = Core.TimeUtils.DateTimeUtcNow;
        this.lastHeartbeatUtc = this.runStartUtc;
        this.lastTradeOpenedUtc = null;

        this.waitOpenPosition = false;
        this.waitClosePositions = false;
        this.dailyPnl = 0;
        this.lastResetDay = -1;
        this.dailyLimitHit = false;
        this.totalRealizedPnl = 0;
        this.peakEquity = 0;
        this.drawdownLimitHit = false;
        this.tradesThisSession = 0;
        this.lastTradeSessionDay = -1;
        this.tradesLimitHit = false;
        this.barCounter = -1;
        this.lastEntryBarIndex = -1_000_000; // see finchDomScalpStrategy's own doc comment on this exact constant — avoids an overflow in the cooldown check
        this.pendingStopPrice = 0;
        this.pendingTargetPrice = 0;
        this.protectiveOrdersPlaced = false;
        this.resolvedSymbolId = null;

        this.barsSeenNq = -1;
        this.sweepCheckedUpTo = -1;
        this.wasInRthNq = false;
        this.lastWeekKeyNq = -1;
        this.barsSeenQqq = -1;
        this.wasInRthQqq = false;
        this.qqqCloseAtRthEnd = null;
        this.nqLastClose = null;
        this.qqqRatioFixed = null;
        this.priorDayPoc = this.priorDayVah = this.priorDayVal = null;
        this.priorWeekPoc = this.priorWeekVah = this.priorWeekVal = null;

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

        this.Log(
            $"[Account] name='{this.CurrentAccount.Name}' id={this.CurrentAccount.Id} "
            + $"connection={this.CurrentAccount.ConnectionId} — NO DryRun, NO ConfirmSimOrEvalAccount "
            + "gate on this strategy. It will place real orders as soon as a signal confirms.",
            StrategyLoggingLevel.Trading);

        this.orderTypeId = Core.OrderTypes
            .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Market)
            ?.Id;
        if (string.IsNullOrEmpty(this.orderTypeId))
        {
            this.Log("Connection does not support market orders.", StrategyLoggingLevel.Error);
            return;
        }

        this.stopOrderTypeId = Core.OrderTypes
            .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Stop)
            ?.Id;
        if (string.IsNullOrEmpty(this.stopOrderTypeId))
        {
            this.Log("Connection does not support stop orders.", StrategyLoggingLevel.Error);
            return;
        }

        this.limitOrderTypeId = Core.OrderTypes
            .FirstOrDefault(x => x.ConnectionId == this.CurrentSymbol.ConnectionId && x.Behavior == OrderTypeBehavior.Limit)
            ?.Id;
        if (string.IsNullOrEmpty(this.limitOrderTypeId))
        {
            this.Log("Connection does not support limit orders.", StrategyLoggingLevel.Error);
            return;
        }

        var tickSize = this.CurrentSymbol.TickSize;
        this.dailyVa = new ValueAreaEngine(this.NqBinTicks * tickSize);
        this.weeklyVa = new ValueAreaEngine(this.NqBinTicks * tickSize);
        this.pools = new SessionPoolTracker();
        this.absorption = new AbsorptionTracker(this.AbsHoldToleranceTicks * tickSize, this.AbsMustHold);
        this.topZone = new SweepZoneTracker(+1);
        this.bottomZone = new SweepZoneTracker(-1);
        this.deltaTracker = new DeltaTracker(this.Period.Duration);

        this.hdmNq = this.CurrentSymbol.GetHistory(this.Period, this.CurrentSymbol.HistoryType, this.StartPoint);

        // QQQ is entirely optional and informational — resolved the same way as CurrentSymbol, but
        // any failure here just disables the QQQ pathway rather than stopping the strategy.
        if (this.UseQqq && this.QqqSymbol != null)
        {
            try
            {
                if (this.QqqSymbol.State == BusinessObjectState.Fake)
                    this.QqqSymbol = Core.Instance.GetSymbol(this.QqqSymbol.CreateInfo());

                if (this.QqqSymbol != null)
                {
                    this.qqqVa = new ValueAreaEngine(this.QqqBinDollars);
                    this.hdmQqq = this.QqqSymbol.GetHistory(this.Period, this.QqqSymbol.HistoryType, this.StartPoint);
                }
            }
            catch (Exception ex)
            {
                this.Log($"[QQQ] unavailable at attach: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
                this.qqqVa = null;
                this.hdmQqq = null;
            }
        }

        // Tick classification needed for real order-flow delta (absorption + sweep confirmation) —
        // this strategy never reads the DOM at all, so no NewLevel2 subscription is needed.
        this.CurrentSymbol.NewLast -= this.OnLast;
        this.CurrentSymbol.NewLast += this.OnLast;

        Core.PositionAdded -= this.Core_PositionAdded;
        Core.PositionAdded += this.Core_PositionAdded;
        Core.PositionRemoved -= this.Core_PositionRemoved;
        Core.PositionRemoved += this.Core_PositionRemoved;
        Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
        Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;
        Core.TradeAdded -= this.Core_TradeAdded;
        Core.TradeAdded += this.Core_TradeAdded;

        var interval = Math.Max(this.PollIntervalMs, 50);
        this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);

        this.Log($"Started [{StrategyTag}].", StrategyLoggingLevel.Trading);
    }

    protected override void OnStop()
    {
        this.pollTimer?.Dispose();
        this.pollTimer = null;

        Core.PositionAdded -= this.Core_PositionAdded;
        Core.PositionRemoved -= this.Core_PositionRemoved;
        Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;
        Core.TradeAdded -= this.Core_TradeAdded;

        if (this.CurrentSymbol != null)
            this.CurrentSymbol.NewLast -= this.OnLast;

        this.hdmNq?.Dispose();
        this.hdmNq = null;
        this.hdmQqq?.Dispose();
        this.hdmQqq = null;

        this.dailyVa = null;
        this.weeklyVa = null;
        this.qqqVa = null;
        this.pools = null;
        this.absorption = null;
        this.topZone = null;
        this.bottomZone = null;
        this.deltaTracker = null;
        this.deltaTickQueue.Clear();
    }

    /// <summary>§10-style discipline: no work on the market-data thread beyond classifying and
    /// enqueueing.</summary>
    private void OnLast(Symbol symbol, Last last)
    {
        if (last is null || last.Size <= 0) return;

        if (TickClassifier.TryClassify(symbol, last, out var isBuy))
            this.deltaTickQueue.Enqueue((last.Time, last.Size, isBuy));
    }

    // ---- the poll -----------------------------------------------------------------------------

    private void OnPollTimer(object? state)
    {
        try
        {
            this.RunPoll();
        }
        catch (Exception ex)
        {
            // An exception escaping a Timer callback takes down the whole platform process — same
            // lesson finchDomScalpStrategy/Finch-Lite already learned the hard way.
            var reason = $"Poll threw ({ex.GetType().Name}: {ex.Message})";
            if (!string.Equals(this.lastPollFault, reason, StringComparison.Ordinal))
            {
                this.lastPollFault = reason;
                this.Log(reason, StrategyLoggingLevel.Error);
            }
        }
    }

    private void RunPoll()
    {
        var hdm = this.hdmNq;
        var dailyVa = this.dailyVa;
        var weeklyVa = this.weeklyVa;
        var pools = this.pools;
        var absorption = this.absorption;
        var topZone = this.topZone;
        var bottomZone = this.bottomZone;
        var deltaTracker = this.deltaTracker;

        if (this.CurrentSymbol is null || hdm is null || dailyVa is null || weeklyVa is null
            || pools is null || absorption is null || topZone is null || bottomZone is null || deltaTracker is null)
            return;

        this.CheckSessionReset();
        this.CheckRiskLimits();
        this.CheckHeartbeat();

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0) return;

        while (this.deltaTickQueue.TryDequeue(out var tick))
            deltaTracker.FeedTick(tick.TimeUtc, tick.Size, tick.IsBuy);

        if (hdm.Count > 1)
        {
            var closedUpTo = hdm.Count - 1; // Count - 1 is the still-forming bar
            this.barCounter = closedUpTo;

            var isFirstDrain = this.barsSeenNq < 0;
            if (isFirstDrain) this.barsSeenNq = 0; // backfill EVERYTHING — prior-day/prior-week context must be complete on attach

            for (var i = this.barsSeenNq; i < closedUpTo; i++)
            {
                if (!TryReadBar(hdm, i, out var bar)) continue;

                var isLive = !isFirstDrain && i > this.sweepCheckedUpTo;
                this.ProcessNqBar(bar, dailyVa, weeklyVa, pools, absorption, topZone, bottomZone, deltaTracker, isLive);
                this.sweepCheckedUpTo = i;
            }

            this.barsSeenNq = closedUpTo;
        }

        this.PollQqq();
    }

    /// <summary>One newly-closed NQ bar, feeding every accumulator and — only if
    /// <paramref name="isLive"/> — evaluating the actual sweep-and-fail trigger. Backlog bars during
    /// the first-ever drain still feed the profile/pool/absorption state (so day-one scoring is
    /// correct) but never evaluate the trigger itself, same "never react to backlog with no
    /// dry-run gate" discipline finchDomScalpStrategy already established.</summary>
    private void ProcessNqBar(
        Bar bar, ValueAreaEngine dailyVa, ValueAreaEngine weeklyVa, SessionPoolTracker pools,
        AbsorptionTracker absorption, SweepZoneTracker topZone, SweepZoneTracker bottomZone,
        DeltaTracker deltaTracker, bool isLive)
    {
        var tickSize = this.CurrentSymbol.TickSize;
        var etTime = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);

        var inRth = IsInWindow(etTime, this.RthStartHour, this.RthStartMinute, this.RthEndHour, this.RthEndMinute);
        var inOvernight = IsInWindow(etTime, this.OvernightStartHour, this.OvernightStartMinute, this.OvernightEndHour, this.OvernightEndMinute);
        var inAsia = IsInWindow(etTime, this.AsiaStartHour, this.AsiaStartMinute, this.AsiaEndHour, this.AsiaEndMinute);
        var inSweepWindow = IsInWindow(etTime, this.SweepStartHour, this.SweepStartMinute, this.SweepEndHour, this.SweepEndMinute);

        var isNewRth = inRth && !this.wasInRthNq;
        var weekKey = IsoWeekKey(etTime);
        var isNewWeek = this.WeeklyEnabled && isNewRth && weekKey != this.lastWeekKeyNq;
        if (isNewRth) this.lastWeekKeyNq = weekKey;

        if (isNewRth)
        {
            var (poc, vah, val) = dailyVa.Finalize(this.ValueAreaPercent / 100.0);
            if (poc is { } p) { this.priorDayPoc = p; this.priorDayVah = vah; this.priorDayVal = val; }
            dailyVa.Reset();
            absorption.OnRthReset();
            topZone.OnRthReset();
            bottomZone.OnRthReset();
        }

        if (isNewWeek)
        {
            var (wpoc, wvah, wval) = weeklyVa.Finalize(this.ValueAreaPercent / 100.0);
            if (wpoc is { } wp) { this.priorWeekPoc = wp; this.priorWeekVah = wvah; this.priorWeekVal = wval; }
            weeklyVa.Reset();
        }

        if (inRth)
        {
            var hl2 = (bar.High + bar.Low) / 2.0;
            dailyVa.AddSample(hl2, bar.Volume);
            if (this.WeeklyEnabled) weeklyVa.AddSample(hl2, bar.Volume);
        }

        pools.FeedBar(bar.High, bar.Low, inOvernight, inAsia, inRth, isNewRth, isNewWeek);
        this.wasInRthNq = inRth;
        this.nqLastClose = bar.Close;

        deltaTracker.CloseBar(bar.OpenUtc);
        var barDelta = deltaTracker.LastBarDelta;
        absorption.OnBarClosed(bar, barDelta, inRth, this.AbsVolumeMultiplier, this.AbsDeltaFraction);

        if (!isLive) return;

        // ---- score/armed/fuel — recomputed fresh every bar, stateless ----
        if (this.priorDayVah is { } vahEdge)
        {
            var (armedTop, scoreTop, fuelTop, fuelTopName) = this.EvaluateZone(vahEdge, +1, dailyVa, absorption, pools, tickSize, inSweepWindow);
            var result = topZone.OnBarClosed(
                bar, barDelta, armedTop, scoreTop, fuelTop, vahEdge, this.priorDayVal ?? vahEdge,
                this.priorDayPoc, this.StopBufferTicks * tickSize, absorption.LastBarWasAbsorption(1));

            if (result.FiredThisBar)
            {
                this.TryPlaceEntry(result, $"VAH {vahEdge:0.####} sweep-fail ({fuelTopName})");
                return; // one trade at a time — a bottom-zone fire on the same bar is impossible
                        // anyway (mutually exclusive directions), but stay explicit about it
            }
            else if (armedTop && !topZone.Fired)
            {
                this.LogNearMiss("VAH", scoreTop, fuelTop, fuelTopName, topZone);
            }
        }

        if (this.priorDayVal is { } valEdge)
        {
            var (armedBottom, scoreBottom, fuelBottom, fuelBottomName) = this.EvaluateZone(valEdge, -1, dailyVa, absorption, pools, tickSize, inSweepWindow);
            var result = bottomZone.OnBarClosed(
                bar, barDelta, armedBottom, scoreBottom, fuelBottom, valEdge, this.priorDayVah ?? valEdge,
                this.priorDayPoc, this.StopBufferTicks * tickSize, absorption.LastBarWasAbsorption(-1));

            if (result.FiredThisBar)
                this.TryPlaceEntry(result, $"VAL {valEdge:0.####} sweep-fail ({fuelBottomName})");
            else if (armedBottom && !bottomZone.Fired)
                this.LogNearMiss("VAL", scoreBottom, fuelBottom, fuelBottomName, bottomZone);
        }
    }

    /// <summary>Score (0-3), armed state, and the selected fuel pool for one edge — direct port of
    /// Pine's own `scoreT`/`armedT`/`f_fuel` combination. The sweep window gate itself lives here
    /// (armed is forced false outside it) rather than in `SweepZoneTracker`, so the tracker stays a
    /// pure state machine with no session-time knowledge of its own. Takes the sweep-window result
    /// the caller already computed from the BAR's own timestamp, rather than re-deriving "now" —
    /// avoids a mismatch right at the window boundary between when the bar closed and when this
    /// poll happens to run.</summary>
    private (bool Armed, int Score, double? Fuel, string FuelName) EvaluateZone(
        double edge, int dir, ValueAreaEngine dailyVa, AbsorptionTracker absorption, SessionPoolTracker pools,
        double tickSize, bool inSweepWindow)
    {
        var heldNear = absorption.IsHeldNear(edge, dir, this.AbsNearTicks * tickSize);
        var lvn = dailyVa.IsLvnLedge(edge, dir, this.LvnBandTicks * tickSize, this.LvnMaxPercentOfMaxBin);
        var score = 1 + (heldNear ? 1 : 0) + (lvn ? 1 : 0); // edge itself always contributes 1 here — only called when the edge exists

        var (fuel, fuelName) = FuelPoolSelector.SelectNearest(
            edge, dir, this.PoolMinTicks * tickSize, this.PoolMaxTicks * tickSize,
            this.UseOvernightPool, pools.OvernightHigh, pools.OvernightLow,
            this.UseAsiaPool, pools.AsiaHigh, pools.AsiaLow,
            this.UsePriorDayPool, pools.PriorDayHigh, pools.PriorDayLow,
            this.WeeklyEnabled && this.WeeklyHighLowAsFuel, this.priorWeekHighFor(dir), this.priorWeekLowFor(dir));

        var armed = inSweepWindow && score >= this.MinScoreToArm && fuel is not null;
        return (armed, score, fuel, fuelName);
    }

    // pools.PriorWeekHigh/Low don't depend on `dir` themselves, but FuelPoolSelector already picks
    // High vs Low internally from `dir` — these two tiny helpers exist only so EvaluateZone can pass
    // a single pair regardless of which zone is asking, keeping FuelPoolSelector's own signature the
    // single source of truth for the up/down choice.
    private double? priorWeekHighFor(int dir) => this.pools?.PriorWeekHigh;
    private double? priorWeekLowFor(int dir) => this.pools?.PriorWeekLow;

    private void LogNearMiss(string edgeName, int score, double? fuel, string fuelName, SweepZoneTracker zone)
    {
        if (zone.IsSwept)
        {
            this.Log(
                $"[Signal skipped] {edgeName} swept (score={score}) — waiting on reclaim + confirming "
                + $"delta (cumulative={zone.CumulativeDeltaSinceSweep:F0} so far).",
                StrategyLoggingLevel.Trading);
        }
        // Armed-but-not-yet-swept is routine (true on almost every bar inside the window once
        // armed) and deliberately NOT logged — only a genuine in-progress sweep is a near-miss
        // worth surfacing, matching finchDomScalpStrategy's own "only log a real near-miss, never
        // routine noise" philosophy.
        _ = fuel; _ = fuelName;
    }

    /// <summary>QQQ's own bar feed — entirely independent cursor/session tracking from NQ's, and
    /// wrapped in its own try/catch so nothing here can ever stall or crash the real (NQ-only)
    /// trading logic. Informational only: feeds `qqqVa` and logs a comparison, never gates
    /// anything.</summary>
    private void PollQqq()
    {
        if (!this.UseQqq || this.hdmQqq is null || this.qqqVa is null || this.QqqSymbol is null) return;

        try
        {
            var hdmQ = this.hdmQqq;
            if (hdmQ.Count <= 1) return;

            var closedUpTo = hdmQ.Count - 1;
            var isFirstDrain = this.barsSeenQqq < 0;
            if (isFirstDrain) this.barsSeenQqq = 0;

            for (var i = this.barsSeenQqq; i < closedUpTo; i++)
            {
                if (!TryReadBar(hdmQ, i, out var bar)) continue;

                var etTime = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);
                var inRth = IsInWindow(etTime, this.QqqRthStartHour, this.QqqRthStartMinute, this.QqqRthEndHour, this.QqqRthEndMinute);
                var isNewRth = inRth && !this.wasInRthQqq;
                var justLeftRth = !inRth && this.wasInRthQqq;

                if (isNewRth)
                {
                    var (poc, vah, val) = this.qqqVa.Finalize(this.ValueAreaPercent / 100.0);
                    _ = (poc, vah, val); // captured below via qqqVa.Bins-backed state is unnecessary; see comparison block
                    this.qqqVa.Reset();
                }

                if (inRth)
                {
                    var hl2 = (bar.High + bar.Low) / 2.0;
                    this.qqqVa.AddSample(hl2, bar.Volume);
                }

                if (justLeftRth)
                {
                    this.qqqCloseAtRthEnd = bar.Close;
                    if (!this.QqqRatioUseLive && this.qqqCloseAtRthEnd is { } qc && qc > 0 && this.nqLastClose is { } nqc)
                        this.qqqRatioFixed = nqc / qc;
                }

                this.wasInRthQqq = inRth;
            }

            this.barsSeenQqq = closedUpTo;

            this.EvaluateQqq();
        }
        catch (Exception ex)
        {
            var reason = $"[QQQ] poll failed: {ex.GetType().Name}: {ex.Message}";
            if (!string.Equals(this.lastQqqFault, reason, StringComparison.Ordinal))
            {
                this.lastQqqFault = reason;
                this.Log(reason, StrategyLoggingLevel.Error);
            }
        }
    }

    /// <summary>Informational-only comparison — never gates the score/armed/fire pipeline. Logs
    /// only when NQ's own edges disagree with QQQ's converted levels by more than the configured
    /// threshold, same "don't log routine agreement" restraint as the rest of this file's
    /// observability.</summary>
    private void EvaluateQqq()
    {
        if (this.qqqVa is null || this.CurrentSymbol is null) return;
        if (this.priorDayVah is not { } vah || this.priorDayVal is not { } val) return;

        var ratio = this.QqqRatioUseLive
            ? (this.nqLastClose is { } nq && this.qqqCloseAtRthEnd is { } q && q > 0 ? nq / q : (double?)null)
            : this.qqqRatioFixed;
        if (ratio is not { } r) return;

        var (qPoc, qVah, qVal) = this.qqqVa.Finalize(this.ValueAreaPercent / 100.0);
        if (qVah is not { } qv || qVal is not { } ql) return;

        var tickSize = this.CurrentSymbol.TickSize;
        var threshold = this.QqqDisagreementTicks * tickSize;

        var vahNq = qv * r;
        var valNq = ql * r;

        if (Math.Abs(vahNq - vah) >= threshold)
            this.Log($"[QQQ] VAH disagreement: NQ={vah:0.####} QQQ->NQ={vahNq:0.####}", StrategyLoggingLevel.Trading);
        if (Math.Abs(valNq - val) >= threshold)
            this.Log($"[QQQ] VAL disagreement: NQ={val:0.####} QQQ->NQ={valNq:0.####}", StrategyLoggingLevel.Trading);

        _ = qPoc;
    }

    // ---- entry ----------------------------------------------------------------------------------

    private void TryPlaceEntry(SweepResult result, string anchorDescription)
    {
        if (this.waitOpenPosition || this.waitClosePositions) return;
        if (this.dailyLimitHit || this.drawdownLimitHit || this.tradesLimitHit) return;
        if (this.MyPositions().Any()) return;
        if (this.barCounter - this.lastEntryBarIndex < this.MinBarsBetweenEntries) return;

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0) return;

        var isLong = result.Side == Side.Buy;
        var stopPrice = EnforceMinStopDistance(result.Stop, result.Entry, isLong, this.MinStopDistanceTicks * tickSize);
        var targetPrice = EnforceMinTargetDistance(result.Target, result.Entry, isLong, this.MinTargetDistanceTicks * tickSize);

        var pocText = result.PocInformational is { } poc
            ? $" T1 POC {poc:0.####} (informational, not targeted)"
            : string.Empty;
        var absText = result.AbsorptionAtSweep ? " abs@sweep=Y" : string.Empty;

        this.PlaceEntry(result.Side, stopPrice, targetPrice,
            $"{anchorDescription}{pocText}{absText}");
    }

    private void PlaceEntry(Side side, double stopPrice, double targetPrice, string anchorDescription)
    {
        this.waitOpenPosition = true;
        this.pendingStopPrice = stopPrice;
        this.pendingTargetPrice = targetPrice;

        this.Log(
            $"[Signal] {side} anchor={anchorDescription} stop={stopPrice:0.####} target={targetPrice:0.####}",
            StrategyLoggingLevel.Trading);

        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.orderTypeId,
            Quantity = this.Quantity,
            Side = side,
            Comment = StrategyTag,
        });

        if (result.Status == TradingOperationResultStatus.Failure)
        {
            this.Log($"[Order] failed: {result.Message}", StrategyLoggingLevel.Error);
            this.waitOpenPosition = false;
            return;
        }

        this.lastEntryBarIndex = this.barCounter;
        this.tradesThisSession++;

        if (this.MaxTradesPerSession > 0 && this.tradesThisSession >= this.MaxTradesPerSession)
        {
            this.tradesLimitHit = true;
            this.Log($"[Risk] max trades per session ({this.MaxTradesPerSession}) reached.", StrategyLoggingLevel.Trading);
        }
    }

    /// <summary>Same "two separate, explicit orders" pattern as finchDomScalpStrategy — see that
    /// file's own doc comment for the live-fill bug this shape avoids. Single target only, per the
    /// operator's own "T2 only" decision — no scale-out machinery.</summary>
    private void PlaceProtectiveOrders(Position position)
    {
        if (string.IsNullOrEmpty(this.stopOrderTypeId) || string.IsNullOrEmpty(this.limitOrderTypeId))
        {
            this.Log("[Order] cannot place protective stop/target: stop/limit order type unavailable.", StrategyLoggingLevel.Error);
            return;
        }

        var closingSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;
        var isLong = position.Side == Side.Buy;
        var tickSize = this.CurrentSymbol.TickSize;

        var stopPrice = tickSize > 0
            ? EnforceMinStopDistance(this.pendingStopPrice, position.OpenPrice, isLong, this.MinStopDistanceTicks * tickSize)
            : this.pendingStopPrice;
        var targetPrice = tickSize > 0
            ? EnforceMinTargetDistance(this.pendingTargetPrice, position.OpenPrice, isLong, this.MinTargetDistanceTicks * tickSize)
            : this.pendingTargetPrice;

        var stopResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.stopOrderTypeId,
            Quantity = position.Quantity,
            Side = closingSide,
            Comment = StrategyTag,
            TriggerPrice = stopPrice,
        });

        if (stopResult.Status == TradingOperationResultStatus.Failure)
        {
            // Never run a position with no stop at all — same rule, same reasoning as
            // finchDomScalpStrategy's own doc comment on this exact failure path.
            this.Log(
                $"[Order] protective stop failed: {stopResult.Message}. Closing position immediately "
                + "— a trade must never run with no stop at all.",
                StrategyLoggingLevel.Error);

            var closeResult = position.Close();
            if (closeResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] failed to close unprotected position: {closeResult.Message}", StrategyLoggingLevel.Error);

            return;
        }

        var targetResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.limitOrderTypeId,
            Quantity = position.Quantity,
            Side = closingSide,
            Comment = StrategyTag,
            Price = targetPrice,
        });

        if (targetResult.Status == TradingOperationResultStatus.Failure)
            this.Log($"[Order] protective target failed: {targetResult.Message}", StrategyLoggingLevel.Error);

        this.Log(
            $"[Order] protective stop={stopPrice:0.####} target={targetPrice:0.####} (fill={position.OpenPrice:0.####}) placed as separate orders.",
            StrategyLoggingLevel.Trading);
    }

    private static double EnforceMinStopDistance(double stopPrice, double referencePrice, bool isLong, double minDistance)
    {
        var actualDistance = Math.Abs(referencePrice - stopPrice);
        if (actualDistance >= minDistance) return stopPrice;
        return isLong ? referencePrice - minDistance : referencePrice + minDistance;
    }

    private static double EnforceMinTargetDistance(double targetPrice, double referencePrice, bool isLong, double minDistance)
    {
        var actualDistance = Math.Abs(targetPrice - referencePrice);
        if (actualDistance >= minDistance) return targetPrice;
        return isLong ? referencePrice + minDistance : referencePrice - minDistance;
    }

    // ---- position isolation — same StrategyTag/Comment pattern as finchDomScalpStrategy --------

    private bool IsMine(Symbol? symbol, Account? account, string? comment)
    {
        if (comment != StrategyTag) return false;
        if (account is null || this.CurrentAccount is null) return false;
        if (!string.Equals(account.Id, this.CurrentAccount.Id, StringComparison.Ordinal)) return false;
        if (symbol is null || this.CurrentSymbol is null) return false;
        if (!string.Equals(symbol.ConnectionId, this.CurrentSymbol.ConnectionId, StringComparison.Ordinal)) return false;

        if (this.resolvedSymbolId is null)
        {
            this.resolvedSymbolId = symbol.Id;
            return true;
        }

        return string.Equals(symbol.Id, this.resolvedSymbolId, StringComparison.Ordinal);
    }

    private bool IsMyPosition(Symbol? symbol, Account? account, string? comment)
    {
        if (account is null || this.CurrentAccount is null) return false;
        if (!string.Equals(account.Id, this.CurrentAccount.Id, StringComparison.Ordinal)) return false;
        if (symbol is null) return false;

        if (this.resolvedSymbolId is not null)
            return string.Equals(symbol.Id, this.resolvedSymbolId, StringComparison.Ordinal);

        if (comment != StrategyTag) return false;
        if (this.CurrentSymbol is null || !string.Equals(symbol.ConnectionId, this.CurrentSymbol.ConnectionId, StringComparison.Ordinal))
            return false;

        this.resolvedSymbolId = symbol.Id;
        return true;
    }

    private Position[] MyPositions() => Core.Instance.Positions
        .Where(x => this.IsMyPosition(x.Symbol, x.Account, x.Comment))
        .ToArray();

    private Order[] MyOrders() => Core.Instance.Orders
        .Where(x => this.IsMine(x.Symbol, x.Account, x.Comment))
        .ToArray();

    private void Core_PositionAdded(Position obj)
    {
        if (obj is null || !this.IsMyPosition(obj.Symbol, obj.Account, obj.Comment)) return;
        if (this.protectiveOrdersPlaced) return; // duplicate-firing guard — see finchDomScalpStrategy's own doc comment on this exact bug class

        this.protectiveOrdersPlaced = true;
        this.waitOpenPosition = false;
        this.lastTradeOpenedUtc = Core.TimeUtils.DateTimeUtcNow;

        try
        {
            this.PlaceProtectiveOrders(obj);
        }
        catch (Exception ex)
        {
            this.Log($"[Order] failed to place protective stop/target: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void Core_PositionRemoved(Position obj)
    {
        if (obj is null || !this.IsMyPosition(obj.Symbol, obj.Account, obj.Comment)) return;
        if (this.MyPositions().Any()) return;

        this.waitClosePositions = false;
        this.protectiveOrdersPlaced = false;
        this.lastEntryBarIndex = this.barCounter; // cooldown from whichever happened more recently — entry OR close

        var equity = this.totalRealizedPnl;
        if (equity > this.peakEquity) this.peakEquity = equity;

        try
        {
            foreach (var order in this.MyOrders())
            {
                var r = order.Cancel();
                if (r.Status != TradingOperationResultStatus.Success)
                    this.Log($"[Order] failed to cancel leftover order: {r.Message}", StrategyLoggingLevel.Error);
            }
        }
        catch (Exception ex)
        {
            this.Log($"[Order] failed while cancelling leftover orders: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void Core_OrdersHistoryAdded(OrderHistory obj)
    {
        if (obj is null || !this.IsMine(obj.Symbol, obj.Account, obj.Comment)) return;

        if (obj.Status == OrderStatus.Refused)
        {
            this.waitOpenPosition = false;
            this.waitClosePositions = false;
        }
    }

    private void Core_TradeAdded(Trade obj)
    {
        if (obj is null || !this.IsMine(obj.Symbol, obj.Account, obj.Comment)) return;

        if (obj.GrossPnl is { } pnl)
        {
            this.dailyPnl += pnl.Value;
            this.totalRealizedPnl += pnl.Value;
        }
    }

    // ---- risk management — same shape as finchDomScalpStrategy's own --------------------------

    private static int EstSessionDayKey(DateTime utcNow)
    {
        var est = TimeZoneInfo.ConvertTimeFromUtc(utcNow, SessionZone);
        return est.Hour >= 18 ? est.DayOfYear + 1 : est.DayOfYear;
    }

    private void CheckSessionReset()
    {
        var dayKey = EstSessionDayKey(Core.TimeUtils.DateTimeUtcNow);

        if (dayKey != this.lastResetDay)
        {
            this.lastResetDay = dayKey;
            this.dailyPnl = 0;
            this.dailyLimitHit = false;
            this.Log("[Risk] daily P&L reset (new EST session).", StrategyLoggingLevel.Trading);
        }

        if (dayKey != this.lastTradeSessionDay)
        {
            this.lastTradeSessionDay = dayKey;
            this.tradesThisSession = 0;
            this.tradesLimitHit = false;
        }
    }

    private void CheckRiskLimits()
    {
        if (this.waitOpenPosition || this.waitClosePositions) return;

        var positions = this.MyPositions();
        if (!positions.Any()) return;

        var unrealizedPnlTicks = positions.Sum(x => x.GrossPnLTicks);
        var tickValue = this.CurrentSymbol.TickSize > 0 ? this.CurrentSymbol.GetTickCost(1) : 0;
        var unrealizedPnl = unrealizedPnlTicks * tickValue;

        if (this.MaxDailyLoss > 0 && !this.dailyLimitHit)
        {
            var totalDailyPnl = this.dailyPnl + unrealizedPnl;
            if (totalDailyPnl <= -this.MaxDailyLoss)
            {
                this.dailyLimitHit = true;
                this.waitClosePositions = true;
                this.Log($"[Risk] DAILY LOSS LIMIT HIT — ${totalDailyPnl:F2} <= -${this.MaxDailyLoss}. Closing all.", StrategyLoggingLevel.Trading);
                foreach (var pos in positions) pos.Close();
                return;
            }
        }

        if (this.MaxDrawdown > 0 && !this.drawdownLimitHit)
        {
            var currentEquity = this.totalRealizedPnl + unrealizedPnl;
            if (currentEquity > this.peakEquity) this.peakEquity = currentEquity;
            var drawdown = this.peakEquity - currentEquity;

            if (drawdown >= this.MaxDrawdown)
            {
                this.drawdownLimitHit = true;
                this.waitClosePositions = true;
                this.Log($"[Risk] MAX DRAWDOWN HIT — ${drawdown:F2} >= ${this.MaxDrawdown}. Closing all.", StrategyLoggingLevel.Trading);
                foreach (var pos in positions) pos.Close();
            }
        }
    }

    // ---- observability ----------------------------------------------------------------------

    private void CheckHeartbeat()
    {
        if (this.HeartbeatIntervalMinutes <= 0) return;

        var nowUtc = Core.TimeUtils.DateTimeUtcNow;
        if ((nowUtc - this.lastHeartbeatUtc).TotalMinutes < this.HeartbeatIntervalMinutes) return;

        this.lastHeartbeatUtc = nowUtc;
        if (this.MyPositions().Any()) return; // the open position is already proof of life

        static string Fmt(double? v) => v is { } x ? x.ToString("0.####") : "n/a";

        var sinceLastTrade = this.lastTradeOpenedUtc is { } t
            ? $"{(nowUtc - t).TotalMinutes:F0} min since last trade"
            : $"{(nowUtc - this.runStartUtc).TotalMinutes:F0} min since start, no trade yet";

        this.Log(
            $"[Heartbeat] still running, flat, {sinceLastTrade}. "
            + $"Prior-day POC={Fmt(this.priorDayPoc)} VAH={Fmt(this.priorDayVah)} VAL={Fmt(this.priorDayVal)}. "
            + $"Prior-week POC={Fmt(this.priorWeekPoc)} VAH={Fmt(this.priorWeekVah)} VAL={Fmt(this.priorWeekVal)}. "
            + $"Pools: ovnH={Fmt(this.pools?.OvernightHigh)} ovnL={Fmt(this.pools?.OvernightLow)} "
            + $"asiaH={Fmt(this.pools?.AsiaHigh)} asiaL={Fmt(this.pools?.AsiaLow)} "
            + $"pdH={Fmt(this.pools?.PriorDayHigh)} pdL={Fmt(this.pools?.PriorDayLow)}. "
            + $"Top zone: swept={this.topZone?.IsSwept} fired={this.topZone?.Fired}. "
            + $"Bottom zone: swept={this.bottomZone?.IsSwept} fired={this.bottomZone?.Fired}.",
            StrategyLoggingLevel.Trading);
    }

    // ---- shared helpers -------------------------------------------------------------------------

    private static bool TryReadBar(HistoricalData data, int index, out Bar bar)
    {
        bar = default;

        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item)
            return false;

        bar = new Bar(item.TimeLeft, item.Open, item.High, item.Low, item.Close, item.Volume);
        return true;
    }

    /// <summary>Minute-precision session window check — `finchDomScalpStrategy`'s own
    /// `IsInHourWindow` is hour-only, which isn't precise enough for this strategy's 30-minute
    /// boundaries (sweep window ends 10:30 ET, RTH starts 09:30 ET). Kept as a separate function
    /// rather than widening the existing hour-only one, to avoid risking a subtle regression there.
    /// </summary>
    private static bool IsInWindow(DateTime local, int startHour, int startMinute, int endHour, int endMinute)
    {
        var startTotal = startHour * 60 + startMinute;
        var endTotal = endHour * 60 + endMinute;
        var nowTotal = local.Hour * 60 + local.Minute;

        if (startTotal == endTotal) return true; // full 24h window
        return startTotal < endTotal
            ? nowTotal >= startTotal && nowTotal < endTotal
            : nowTotal >= startTotal || nowTotal < endTotal;
    }

    private static int IsoWeekKey(DateTime local) => ISOWeek.GetYear(local) * 100 + ISOWeek.GetWeekOfYear(local);
}
