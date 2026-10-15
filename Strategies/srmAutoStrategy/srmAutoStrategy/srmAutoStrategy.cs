using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;
using FinchLite;
using LevelsMap;

namespace srmAutoStrategy;

/// <summary>
/// Auto-trades the Session Range Map levels (operator's own ask, 2026-10-14: "based on these
/// levels we need to make a strategy to auto trade for me because i get to emotional when trading
/// them"). Confirmed via AskUserQuestion: trades ALL FOUR SRM setups at once (naked level
/// reaction, session budget-band fade, NY initial balance breakout, Vol-Step Range fade),
/// volatility-scaled stop/target (a fraction of the daily expected move, `emDay`), a FIXED
/// contract count (not account-balance-based risk), and NY session only by default.
///
/// SIGNAL PRIORITY — a choice made here, not asked for: only ONE position open at a time across
/// ALL FOUR setups combined (same "never more than one position open at once" discipline as every
/// other strategy in this codebase). Naked-level reaction and budget-band fade are both
/// mean-reversion theses, while NY IB breakout is a continuation thesis, so two setups CAN
/// disagree on direction on the same bar. Checked in a fixed order each bar (naked level -> budget
/// fade -> IB breakout -> Vol-Step fade); the first one that fires wins, the rest are skipped for
/// that bar. Scoped this way for simplicity/safety on a first build, not something the operator
/// was asked to choose — adjust the order or add independent position slots per setup later if
/// that turns out to matter.
///
/// STOP/TARGET: uniformly `referencePrice -/+ (emDay * fraction)` for every setup, regardless of
/// whether a level-anchored stop (e.g. "beyond the budget band") would arguably be sounder for a
/// particular setup — the operator chose "volatility-scaled" over "opposite SRM level" in the
/// scoping questions, so this applies that choice consistently rather than mixing approaches.
///
/// ORDER-PLACEMENT SAFETY — carried over verbatim from mesOrbStrategy/finchDomScalpStrategy's own
/// hard-won lessons, not re-learned from scratch: separate stop/target orders (never a bracket),
/// tick-rounded before either reaches the broker, a position this instance didn't itself open gets
/// a sane fallback stop/target from its real fill, an async order refusal is self-healed (missing
/// target re-placed, missing stop closes the position immediately — this strategy never knowingly
/// runs with no stop at all), and daily-loss/account-floor risk limits are checked against the
/// REAL broker-reported Account.Balance, persisted in a small local file so they survive a
/// mid-session restart. This last part directly serves the operator's own stated reason for
/// wanting this strategy at all ("i get too emotional") — a hard daily-loss circuit breaker is
/// exactly the kind of protection a manual trader can't reliably give themselves under pressure.
///
/// HISTORICAL REPLAY ON STARTUP: `SrmEngine` needs to see roughly `SrmDailyLookbackDays` worth of
/// 1-minute bars to correctly reconstruct the current week/month's own budget and naked levels
/// (not just today's session) — so `OnRun` feeds the WHOLE fetched history through the engine once,
/// WITHOUT evaluating entry signals on any of it, before switching to live, signal-evaluating bars.
/// If the initial synchronous CBOE volatility fetch fails at that exact moment, `EmDay` stays null
/// and the replay silently does nothing (every `SrmEngine.FeedBar` call is a no-op without it) —
/// the recurring volatility timer will recover on its own schedule, but week/month reconstruction
/// from BEFORE that recovery would be incomplete. A narrow, rare edge case, not specially handled.
///
/// SRM ENGINE is shared, not duplicated, from `Indicators/Finch-Lite/src/Finch.Lite.Indicator/
/// SrmEngine.cs` via a `&lt;Compile Include&gt;` in this project's own .csproj — see that file's own
/// comment for why this reach-into-the-older-project direction (not the reverse).
///
/// NOT YET added: per-setup stop/target overrides (all four setups share ONE stop fraction and ONE
/// target fraction of emDay), naked-level stacking, Vol-Step "ATR classic" mode (implied-move
/// only, same limitation `SrmEngine` itself already carries), independent concurrent positions per
/// setup.
/// </summary>
public class srmAutoStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    private const string StrategyTag = "SrmAuto";
    private const string DailyStateFileName = "srm_auto_daily_risk_state.txt";

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [InputParameter("Account", 0)]
    public Account CurrentAccount { get; set; }

    [InputParameter("Symbol", 1)]
    public Symbol CurrentSymbol { get; set; }

    [InputParameter("Quantity", 2)]
    public int Quantity { get; set; }

    // ---- SRM data / volatility -----------------------------------------------------------------

    [InputParameter("SRM: implied-vol ticker (VXN for NQ/MNQ/QQQ, VIX for ES/MES/SPY)", 10)]
    public string SrmVolTicker { get; set; }

    [InputParameter("SRM: daily history lookback (days)", 11, 5, 90, 1, 0)]
    public int SrmDailyLookbackDays { get; set; }

    [InputParameter("SRM: volatility refresh interval (seconds)", 12, 15, 3600, 15, 0)]
    public int SrmVolPollIntervalSeconds { get; set; }

    [InputParameter("Trade Asia session (18:00-03:00 ET)", 13)]
    public bool TradeAsiaSession { get; set; }

    [InputParameter("Trade London session (03:00-09:30 ET)", 14)]
    public bool TradeLondonSession { get; set; }

    [InputParameter("Trade NY session (09:30-16:00 ET)", 15)]
    public bool TradeNySession { get; set; }

    // ---- stop / target (volatility-scaled, shared across all 4 setups) -------------------------

    [InputParameter("Stop = x daily expected move", 20, 0.02, 2, 0.01, 2)]
    public double StopFractionOfEmDay { get; set; }

    [InputParameter("Target = x daily expected move", 21, 0.02, 3, 0.01, 2)]
    public double TargetFractionOfEmDay { get; set; }

    // ---- setup enables ---------------------------------------------------------------------------

    [InputParameter("Setup: naked level reaction enabled", 30)]
    public bool NakedLevelEnabled { get; set; }

    [InputParameter("Setup: session budget-band fade enabled", 31)]
    public bool BudgetFadeEnabled { get; set; }

    [InputParameter("Setup: NY initial balance breakout enabled", 32)]
    public bool IbBreakoutEnabled { get; set; }

    [InputParameter("Setup: Vol-Step Range fade enabled", 33)]
    public bool VolStepFadeEnabled { get; set; }

    [InputParameter("SRM: NY initial balance length (minutes)", 34, 5, 180, 1, 0)]
    public int IbMinutes { get; set; }

    [InputParameter("SRM: Vol-Step size (x daily expected move)", 35, 0.05, 3, 0.05, 2)]
    public double VolStepMult { get; set; }

    [InputParameter("Naked level touch tolerance (points, for rejection confirmation)", 36, 0, 100, 0.25, 2)]
    public double NakedLevelTolerancePoints { get; set; }

    // ---- SRM budgets (shared math with the Finch-Lite indicator's own defaults) -----------------

    [InputParameter("SRM: Asia budget share", 40, 0.05, 3, 0.01, 2)]
    public double AsiaShare { get; set; }

    [InputParameter("SRM: London budget share", 41, 0.05, 3, 0.01, 2)]
    public double LondonShare { get; set; }

    [InputParameter("SRM: NY budget share", 42, 0.05, 3, 0.01, 2)]
    public double NyShare { get; set; }

    [InputParameter("SRM: completion projection (x budget)", 43, 0.5, 5, 0.1, 1)]
    public double KProj { get; set; }

    [InputParameter("SRM: adaptive projection", 44)]
    public bool AdaptiveProjection { get; set; }

    [InputParameter("SRM: adaptive allowance multiplier", 45, 0.1, 3, 0.05, 2)]
    public double ExtMult { get; set; }

    [InputParameter("SRM: widen budgets in EXPANDED regime (VIX >= VIX3M)", 46)]
    public bool ExpandedScaleEnabled { get; set; }

    [InputParameter("SRM: EXPANDED budget multiplier", 47, 1, 3, 0.01, 2)]
    public double ExpandedMultiplier { get; set; }

    [InputParameter("SRM: weekly budget mult", 48, 0.3, 3, 0.01, 2)]
    public double WeeklyBudgetMult { get; set; }

    [InputParameter("SRM: monthly budget mult", 49, 0.3, 3, 0.01, 2)]
    public double MonthlyBudgetMult { get; set; }

    [InputParameter("SRM: profile math resolution (rows per budget)", 50, 6, 300, 1, 0)]
    public int SessionRows { get; set; }

    [InputParameter("SRM: value area %", 51, 50, 95, 1, 0)]
    public double ValueAreaPercent { get; set; }

    // ---- risk --------------------------------------------------------------------------------

    [InputParameter("Max daily loss ($, 0 = off)", 60, 0, 1000000, 1, 2)]
    public double MaxDailyLoss { get; set; }

    [InputParameter("Account balance floor ($, sticky, 0 = off)", 61, 0, 1000000, 1, 2)]
    public double AccountBalanceFloor { get; set; }

    /// <summary>FOUND 2026-10-15, operator, with real log numbers worked out: "it seems like every
    /// single time it enters off gex magnet or ib breakout its always in drawdown right away and
    /// the risk to reward is massive in negative rr." A real trade was 14.75pts reward against
    /// 69.25pts risk (≈1:4.7 against). Checked in <see cref="EnterTrade"/> for EVERY setup, not
    /// just the two named — rejects a trade before it's ever placed if the resolved reward:risk
    /// ratio falls below this.</summary>
    [InputParameter("Minimum reward:risk ratio (0 = off)", 62, 0, 5, 0.1, 2)]
    public double MinRewardToRiskRatio { get; set; }

    [InputParameter("Poll interval (ms)", 70, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; }

    // ---- live GEX (operator's own ask, 2026-10-14: "i need it to trade off those gex levels as
    // well but dont want to paste them") — reuses LevelsMap's own GexEngine/CboeOptionsClient, same
    // free CBOE data, no broker feed symbol needed. ----------------------------------------------

    [InputParameter("GEX: enabled", 80)]
    public bool GexEnabled { get; set; }

    [InputParameter("GEX: underlying ticker (e.g. QQQ, SPY)", 81)]
    public string GexUnderlying { get; set; }

    [InputParameter("GEX: max days to expiry included", 82, 1, 365, 1, 0)]
    public int GexMaxDaysToExpiry { get; set; }

    [InputParameter("GEX: refresh interval (seconds)", 83, 15, 3600, 15, 0)]
    public int GexPollIntervalSeconds { get; set; }

    [InputParameter("GEX: risk-free rate (for Black-Scholes fallback)", 84, 0, 0.2, 0.001, 3)]
    public double GexRiskFreeRate { get; set; }

    [InputParameter("GEX setup: Call Wall fade enabled", 85)]
    public bool CallWallFadeEnabled { get; set; }

    [InputParameter("GEX setup: Put Wall fade enabled", 86)]
    public bool PutWallFadeEnabled { get; set; }

    [InputParameter("GEX setup: Zero Gamma cross enabled", 87)]
    public bool ZeroGammaCrossEnabled { get; set; }

    [InputParameter("GEX setup: Magnet pull enabled", 88)]
    public bool MagnetPullEnabled { get; set; }

    [InputParameter("GEX: wall touch tolerance (points, for rejection confirmation)", 89, 0, 100, 0.25, 2)]
    public double GexWallTolerancePoints { get; set; }

    /// <summary>FOUND 2026-10-15, operator: "why is it only ever trading on the gex magnet pulls" —
    /// this was a flat 5-POINT threshold (`Math.Abs(item.Close - magnet) >= 5`), which on MNQ is
    /// satisfied on nearly every single bar (the close is almost never within 5 points of a
    /// specific option strike) — combined with the one-shot flag resetting roughly every GEX
    /// refresh (~60s) regardless of real price movement, this setup was essentially always "ready"
    /// the instant the strategy went flat, while the other 7 setups need much rarer, more specific
    /// conditions to align. Rescaled to a fraction of the daily expected move, matching every other
    /// distance in this strategy, so "far enough from the magnet to bet on reversion" actually
    /// means something significant again.</summary>
    [InputParameter("GEX: Magnet trigger distance (x daily expected move)", 90, 0.02, 2, 0.02, 2)]
    public double GexMagnetTriggerDistanceEmDayFraction { get; set; }

    /// <summary>FOUND 2026-10-15, operator: "the gex magenet pull trades seem to be very bad on
    /// the rr and never really work out... soon as we enter we are in crazy draw down." Magnet pull
    /// was the only one of the 8 setups with no reaction confirmation at all — see the entry
    /// condition's own doc comment for the full diagnosis. This is how much the bar must close back
    /// from its own extreme (toward the magnet) before that extreme counts as a real rejection
    /// rather than just a wick on an otherwise-trending bar.</summary>
    [InputParameter("GEX: Magnet rejection confirmation (points)", 91, 0, 50, 0.5, 2)]
    public double GexMagnetRejectionPoints { get; set; }

    // ---- structure-based stop/target (operator's own ask, 2026-10-15: "it should be looking at
    // the structure of the candles and going on if it breaks this structure we are not going in
    // that direction for the stop loss and target like wickless candles for take profits or other
    // key levels") — replaces the pure volatility-scaled distance as the PRIMARY stop/target for
    // every setup; the volatility-scaled distance (StopFractionOfEmDay/TargetFractionOfEmDay) is
    // now only the FALLBACK when no structural level/wickless candle is found. ---------------------

    [InputParameter("Structure: use level-anchored stops (off = volatility-scaled only)", 95)]
    public bool UseStructureStops { get; set; }

    [InputParameter("Structure: stop buffer beyond the level (points)", 96, 0, 50, 0.25, 2)]
    public double StructureStopBufferPoints { get; set; }

    [InputParameter("Structure: use key-level / wickless-candle targets (off = volatility-scaled only)", 97)]
    public bool UseStructureTargets { get; set; }

    [InputParameter("Structure: bar lookback for wickless-candle targets", 98, 5, 500, 5, 0)]
    public int StructureLookbackBars { get; set; }

    [InputParameter("Structure: wickless tolerance (points, 0 = no wick at all)", 99, 0, 10, 0.1, 2)]
    public double WicklessTolerancePoints { get; set; }

    [InputParameter("Structure: minimum target distance (points, avoids a too-close target)", 100, 0, 100, 0.5, 2)]
    public double MinTargetDistancePoints { get; set; }

    [InputParameter("Structure: maximum target distance (x daily expected move, avoids a too-far target)", 101, 0.05, 3, 0.05, 2)]
    public double MaxTargetDistanceEmDayFraction { get; set; }

    [InputParameter("Structure: maximum stop distance (x daily expected move, avoids a too-far stop)", 102, 0.02, 2, 0.02, 2)]
    public double MaxStopDistanceEmDayFraction { get; set; }

    // ---- active in-trade management (operator's own ask, 2026-10-15: "it should be always looking
    // at the chart during a trade and moving the stop loss when in profit to secure wins but not so
    // much that it cuts off the actual good entry it still needs room to breath" + "reviews if that
    // trade logic is still valid or not and if its not it exits") — runs every poll tick (already
    // ~1s) while a position is open, not just on bar close. ------------------------------------------

    [InputParameter("Trailing stop: enabled", 105)]
    public bool UseTrailingStop { get; set; }

    [InputParameter("Trailing stop: activate after this much profit (points)", 106, 0, 200, 0.5, 2)]
    public double TrailingActivationPoints { get; set; }

    [InputParameter("Trailing stop: distance behind the best price reached (points, the \"breathing room\")", 107, 0, 100, 0.5, 2)]
    public double TrailingBufferPoints { get; set; }

    [InputParameter("Trailing stop: minimum re-trail step (points, avoids order-replace spam)", 108, 0, 20, 0.25, 2)]
    public double TrailingMinStepPoints { get; set; }

    [InputParameter("Thesis invalidation: enabled", 110)]
    public bool UseThesisInvalidation { get; set; }

    [InputParameter("Thesis invalidation: minimum favorable excursion before it applies (points)", 111, 0, 200, 0.5, 2)]
    public double ThesisInvalidationMinFavorablePoints { get; set; }

    [InputParameter("Thesis invalidation: give-back from best price that triggers a review (points)", 112, 0, 100, 0.5, 2)]
    public double ThesisInvalidationGiveBackPoints { get; set; }

    /// <summary>Operator's own ask, 2026-10-15: "after exiting an order we need a cool down i
    /// think because it just hit take profit in a short and then entered a brand new short and is
    /// in instant drawdown from hitting that level." Blocks ALL new entries (all 8 setups alike)
    /// for this long after ANY exit — stop, target, thesis-invalidation close, or risk-limit close,
    /// whichever happens — not just a repeat of the exact same setup. A level that just got hit and
    /// resolved doesn't mean the next bar is a fresh, independent opportunity; it's often the same
    /// move still finishing.</summary>
    [InputParameter("Cooldown after any exit (minutes, 0 = off)", 115, 0, 120, 0.5, 2)]
    public double CooldownAfterExitMinutes { get; set; }

    public srmAutoStrategy()
    {
        this.Name = "SRM Auto";
        this.Description =
            "Auto-trades Session Range Map levels (naked level reaction, session budget-band fade, "
            + "NY initial balance breakout, Vol-Step Range fade) with volatility-scaled stop/target "
            + "and a fixed contract count. Built from the operator's own srm.pine/Finch-Lite port.";

        this.Quantity = 1;
        this.SrmVolTicker = "VXN";
        this.SrmDailyLookbackDays = 40;
        this.SrmVolPollIntervalSeconds = 60;
        this.TradeAsiaSession = false;
        this.TradeLondonSession = false;
        this.TradeNySession = true;
        // 2026-10-15, operator: "it also never placed stop loss and take profits... oh nevermind
        // it did but they are massive." 0.25x/0.5x of the FULL daily expected move (~400 pts on
        // MNQ at current vol) meant a ~100pt stop / ~200pt target on every reactive touch/rejection
        // trade — way too wide for this kind of setup. Tightened by 5x (same 2:1 ratio kept), now
        // ~20pt stop / ~40pt target at that same volatility. Still fully adjustable via the
        // InputParameters themselves if this still isn't the right size.
        this.StopFractionOfEmDay = 0.05;
        this.TargetFractionOfEmDay = 0.10;
        this.NakedLevelEnabled = true;
        this.BudgetFadeEnabled = true;
        this.IbBreakoutEnabled = true;
        this.VolStepFadeEnabled = true;
        this.IbMinutes = 60;
        this.VolStepMult = 0.5;
        this.NakedLevelTolerancePoints = 1.0;
        this.AsiaShare = 0.43;
        this.LondonShare = 0.48;
        this.NyShare = 0.90;
        this.KProj = 1.3;
        this.AdaptiveProjection = true;
        this.ExtMult = 1.0;
        this.ExpandedScaleEnabled = true;
        this.ExpandedMultiplier = 1.39;
        this.WeeklyBudgetMult = 1.22;
        this.MonthlyBudgetMult = 1.34;
        this.SessionRows = 48;
        this.ValueAreaPercent = 70;
        this.MaxDailyLoss = 0;
        this.AccountBalanceFloor = 0;
        this.MinRewardToRiskRatio = 1.0;
        this.PollIntervalMs = 1000;

        this.GexEnabled = true;
        this.GexUnderlying = "QQQ";
        this.GexMaxDaysToExpiry = 45;
        this.GexPollIntervalSeconds = 60;
        this.GexRiskFreeRate = 0.05;
        this.CallWallFadeEnabled = true;
        this.PutWallFadeEnabled = true;
        this.ZeroGammaCrossEnabled = true;
        this.MagnetPullEnabled = true;
        this.GexWallTolerancePoints = 1.0;
        this.GexMagnetTriggerDistanceEmDayFraction = 0.35;
        this.GexMagnetRejectionPoints = 3.0;

        this.UseStructureStops = true;
        this.StructureStopBufferPoints = 2.0;
        this.UseStructureTargets = true;
        this.StructureLookbackBars = 50;
        this.WicklessTolerancePoints = 0.25;
        this.MinTargetDistancePoints = 5.0;
        this.MaxTargetDistanceEmDayFraction = 0.20;
        this.MaxStopDistanceEmDayFraction = 0.15;

        this.UseTrailingStop = true;
        this.TrailingActivationPoints = 15.0;
        this.TrailingBufferPoints = 10.0;
        this.TrailingMinStepPoints = 2.0;

        this.UseThesisInvalidation = true;
        this.ThesisInvalidationMinFavorablePoints = 12.0;
        this.ThesisInvalidationGiveBackPoints = 8.0;

        this.CooldownAfterExitMinutes = 5.0;
    }

    // ---- state ---------------------------------------------------------------------------------

    private readonly SrmEngine srmEngine = new();
    private Timer? pollTimer;
    private Timer? srmVolTimer;
    private int srmVolTimerIntervalMs;
    private double? srmVolLast;
    private double? srmVixLast;
    private double? srmVix3mLast;

    private HistoricalData? chartHistory;
    private HistoricalData? dailyHistory;
    private int barsSeen = -1;

    private string? orderTypeId;
    private string? stopOrderTypeId;
    private string? limitOrderTypeId;
    private string? resolvedSymbolId;
    private bool protectiveOrdersPlaced;
    private double pendingStopPrice;
    private double pendingTargetPrice;

    // ---- active in-trade management state --------------------------------------------------------
    private bool inTradeManaged;
    private Side openSide;
    private double openEntryPrice;
    private double openBestPrice;
    private double currentStopPrice;
    private DateTime lastExitUtc = DateTime.MinValue;

    private int balanceDayKey = -1;
    private double dayStartBalance;
    private bool accountFloorBreached;

    private DateTime lastNyStartUtc = DateTime.MinValue;
    private DateTime lastActiveSessionStartUtc = DateTime.MinValue;
    private bool budgetFadeUpperDone;
    private bool budgetFadeLowerDone;
    private bool ibUpTraded;
    private bool ibDownTraded;
    private double? lastVsMidSeen;
    private bool volStepUpTraded;
    private bool volStepDownTraded;

    private DateTime lastHeartbeatUtc = DateTime.MinValue;
    private const int HeartbeatIntervalMinutes = 5;

    // ---- live GEX state --------------------------------------------------------------------------
    private Timer? gexTimer;
    private int gexTimerIntervalMs;
    private GexResult? gexResult;

    /// <summary>Chart price ÷ QQQ/SPY spot, sampled from the SAME fetch as <see cref="gexResult"/>
    /// itself (both come out of one `OnGexTimer` call) — deliberately NOT recomputed independently
    /// every poll tick the way an earlier LevelsMap bug did, which caused visible jitter (see that
    /// project's own 2026-10-12 changelog entry) from pairing a live chart price against a stale
    /// reference price.</summary>
    private double? gexRatio;

    private double? lastCallWallSeen;
    private double? lastPutWallSeen;
    private double? lastZeroGammaSeen;
    private double? lastMagnetSeen;
    private bool callWallTraded;
    private bool putWallTraded;
    private bool magnetTraded;
    private bool? zgWasAbove;

    // ---- lifecycle ---------------------------------------------------------------------------

    protected override void OnRun()
    {
        if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
            this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());

        if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
            this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());

        if (this.CurrentSymbol is null || this.CurrentAccount is null)
        {
            this.Log("Symbol/Account not set — cannot start.", StrategyLoggingLevel.Error);
            return;
        }

        this.Log(
            $"[Account] name='{this.CurrentAccount.Name}' id={this.CurrentAccount.Id} "
            + $"connection={this.CurrentAccount.ConnectionId} — NO DryRun gate on this strategy. "
            + "It will place real orders as soon as a signal confirms.",
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

        this.protectiveOrdersPlaced = false;
        this.pendingStopPrice = 0;
        this.pendingTargetPrice = 0;
        this.balanceDayKey = -1;
        this.accountFloorBreached = false;
        this.srmEngine.Reset();
        this.lastNyStartUtc = DateTime.MinValue;
        this.lastActiveSessionStartUtc = DateTime.MinValue;
        this.budgetFadeUpperDone = false;
        this.budgetFadeLowerDone = false;
        this.ibUpTraded = false;
        this.ibDownTraded = false;
        this.lastVsMidSeen = null;
        this.volStepUpTraded = false;
        this.volStepDownTraded = false;
        this.gexResult = null;
        this.gexRatio = null;
        this.lastCallWallSeen = null;
        this.lastPutWallSeen = null;
        this.lastZeroGammaSeen = null;
        this.lastMagnetSeen = null;
        this.callWallTraded = false;
        this.putWallTraded = false;
        this.magnetTraded = false;
        this.zgWasAbove = null;
        this.inTradeManaged = false;
        this.openEntryPrice = 0;
        this.openBestPrice = 0;
        this.currentStopPrice = 0;
        this.lastExitUtc = DateTime.MinValue;

        try
        {
            var lookbackDays = Math.Max(5, this.SrmDailyLookbackDays);
            this.chartHistory = this.CurrentSymbol.GetHistory(Period.MIN1, this.CurrentSymbol.HistoryType, Core.TimeUtils.DateTimeUtcNow.AddDays(-lookbackDays));
            this.dailyHistory = this.CurrentSymbol.GetHistory(Period.DAY1, this.CurrentSymbol.HistoryType, Core.TimeUtils.DateTimeUtcNow.AddDays(-lookbackDays));

            var deadline = Core.TimeUtils.DateTimeUtcNow.AddSeconds(5);
            while (this.chartHistory.Count < 100 && Core.TimeUtils.DateTimeUtcNow < deadline)
                Thread.Sleep(100);

            if (this.chartHistory.Count < 100)
                this.Log($"[SRM] chart history only has {this.chartHistory.Count} bars after a 5s wait — session/week/month reconstruction may be incomplete.", StrategyLoggingLevel.Error);
        }
        catch (Exception ex)
        {
            this.Log($"Failed to fetch historical data: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
            return;
        }

        // Initial, SYNCHRONOUS volatility fetch so the replay below (which needs EmDay to do
        // anything at all) isn't guaranteed to run against a null EmDay — see the class doc
        // comment's "HISTORICAL REPLAY ON STARTUP" note for what happens if this fails anyway.
        try
        {
            var ticker = string.IsNullOrWhiteSpace(this.SrmVolTicker) ? "VXN" : this.SrmVolTicker.Trim().ToUpperInvariant();
            this.srmVolLast = CboeIndexClient.FetchQuote(ticker).CurrentPrice;
            this.srmVixLast = CboeIndexClient.FetchQuote("VIX").CurrentPrice;
            this.srmVix3mLast = CboeIndexClient.FetchQuote("VIX3M").CurrentPrice;
        }
        catch (Exception ex)
        {
            this.Log($"[SRM] initial volatility fetch failed: {ex.GetType().Name}: {ex.Message} — will retry on the recurring volatility timer.", StrategyLoggingLevel.Error);
        }

        this.UpdateSrmVolatility();

        var closedUpToInit = this.chartHistory.Count - 1;
        for (var i = 0; i < closedUpToInit; i++)
        {
            if (this.chartHistory[i, SeekOriginHistory.Begin] is not HistoryItemBar item) continue;
            this.ProcessClosedBar(item, evaluateSignals: false);
        }
        this.barsSeen = closedUpToInit;

        this.Log($"[SRM] replayed {closedUpToInit} historical bars to reconstruct session/week/month state before going live.", StrategyLoggingLevel.Trading);

        Core.PositionAdded += this.Core_PositionAdded;
        Core.PositionRemoved += this.Core_PositionRemoved;
        Core.OrdersHistoryAdded += this.Core_OrdersHistoryAdded;

        var interval = Math.Max(this.PollIntervalMs, 50);
        this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);

        this.Log($"Started [{StrategyTag}].", StrategyLoggingLevel.Trading);
    }

    protected override void OnStop()
    {
        this.pollTimer?.Dispose();
        this.pollTimer = null;
        this.srmVolTimer?.Dispose();
        this.srmVolTimer = null;
        this.gexTimer?.Dispose();
        this.gexTimer = null;

        Core.PositionAdded -= this.Core_PositionAdded;
        Core.PositionRemoved -= this.Core_PositionRemoved;
        Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;

        this.chartHistory?.Dispose();
        this.dailyHistory?.Dispose();
        this.chartHistory = null;
        this.dailyHistory = null;

        this.Log("Strategy stopped.", StrategyLoggingLevel.Trading);
    }

    // ---- poll ----------------------------------------------------------------------------------

    private void OnPollTimer(object? state)
    {
        try
        {
            this.RunPoll();
        }
        catch (Exception ex)
        {
            this.Log($"[Poll] unhandled error: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void RunPoll()
    {
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return;

        if (this.IsAccountFloorBreached() || this.IsDailyLossLimitBreached())
        {
            foreach (var position in this.MyPositions())
            {
                var r = position.Close();
                if (r.Status != TradingOperationResultStatus.Success)
                    this.Log($"[Risk] failed to close on risk-limit breach: {r.Message}", StrategyLoggingLevel.Error);
            }
            return;
        }

        this.MaybeLogHeartbeat();
        this.SyncSrmVolTimer();
        this.SyncGexTimer();
        this.ManagePosition();
        this.DrainChartBars();
    }

    /// <summary>
    /// Active in-trade management — operator's own ask, 2026-10-15: "it should be always looking
    /// at the chart during a trade and moving the stop loss when in profit to secure wins but not
    /// so much that it cuts off the actual good entry it still needs room to breath" + "reviews if
    /// that trade logic is still valid or not and if its not it exits." Runs every poll tick
    /// (already ~1s), not just on bar close, reading the symbol's own live `Last` price — this is
    /// genuinely "always looking," not just reacting once a bar closes the way entry signals do.
    ///
    /// TWO independent pieces, each with its own on/off toggle:
    ///
    /// TRAILING STOP: once unrealized profit (measured from the real entry fill) reaches
    /// <see cref="TrailingActivationPoints"/>, the stop ratchets to <see cref="TrailingBufferPoints"/>
    /// behind the BEST price reached so far — never the current price directly, which would make the
    /// buffer meaningless the moment price pulls back even slightly. The stop only ever MOVES
    /// TOWARD the entry (locks in more, never less) and only replaces the resting order once the
    /// improvement clears <see cref="TrailingMinStepPoints"/>, so ordinary tick noise doesn't spam
    /// cancel/replace calls. Replacement is NEW-STOP-FIRST, OLD-STOP-CANCELLED-SECOND — deliberately
    /// never leaves the position with zero resting stop, even for a moment, the way a cancel-then-
    /// place sequence would.
    ///
    /// THESIS INVALIDATION: a setup-agnostic proxy rather than bespoke logic per setup (which would
    /// need its own "what does invalidated mean" answer for all 8 setups) — if the trade reached at
    /// least <see cref="ThesisInvalidationMinFavorablePoints"/> of favorable excursion and has since
    /// given back at least <see cref="ThesisInvalidationGiveBackPoints"/> of it, AND a fresh
    /// structural candle is now forming AGAINST the position (reusing the same wickless-candle
    /// scanner already built for targets, just checked in the adverse direction), that combination
    /// is treated as "the move this trade was betting on is reversing, not just pausing" and the
    /// position is closed early rather than waiting for either the stop or the target.
    /// </summary>
    private void ManagePosition()
    {
        if (!this.inTradeManaged) return;
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return;

        var positions = this.MyPositions();
        if (positions.Length == 0)
        {
            this.inTradeManaged = false;
            return;
        }

        var position = positions[0];
        var last = this.CurrentSymbol.Last;
        if (last <= 0 || double.IsNaN(last)) return;

        var isLong = this.openSide == Side.Buy;
        if (isLong) this.openBestPrice = Math.Max(this.openBestPrice, last);
        else this.openBestPrice = Math.Min(this.openBestPrice, last);

        var favorableExcursion = isLong ? this.openBestPrice - this.openEntryPrice : this.openEntryPrice - this.openBestPrice;
        var givenBack = isLong ? this.openBestPrice - last : last - this.openBestPrice;

        if (this.UseThesisInvalidation
            && favorableExcursion >= this.ThesisInvalidationMinFavorablePoints
            && givenBack >= this.ThesisInvalidationGiveBackPoints
            && this.TryFindWicklessTarget(!isLong, last, out _))
        {
            this.Log(
                $"[Risk] thesis invalidation — gave back {givenBack:0.##}pts from the best price reached "
                + $"({this.openBestPrice:0.##}) with a fresh structural candle now forming against the "
                + "position; closing early rather than waiting for the stop.",
                StrategyLoggingLevel.Trading);

            var closeResult = position.Close();
            if (closeResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] thesis-invalidation close failed: {closeResult.Message}", StrategyLoggingLevel.Error);
            return;
        }

        if (this.UseTrailingStop)
        {
            var profit = isLong ? last - this.openEntryPrice : this.openEntryPrice - last;
            if (profit >= this.TrailingActivationPoints)
            {
                var candidateStop = isLong ? this.openBestPrice - this.TrailingBufferPoints : this.openBestPrice + this.TrailingBufferPoints;
                this.TryTrailStop(position, isLong, candidateStop);
            }
        }
    }

    /// <summary>NEW-STOP-FIRST, OLD-STOP-CANCELLED-SECOND — see <see cref="ManagePosition"/>'s own
    /// doc comment for why. Uses the same tick-rounding discipline as every other order placement
    /// in this file.</summary>
    private void TryTrailStop(Position position, bool isLong, double candidateStop)
    {
        if (this.CurrentAccount is null || this.CurrentSymbol is null) return;

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0) return;

        candidateStop = Math.Round(candidateStop / tickSize, MidpointRounding.AwayFromZero) * tickSize;

        var improves = isLong
            ? candidateStop >= this.currentStopPrice + this.TrailingMinStepPoints
            : candidateStop <= this.currentStopPrice - this.TrailingMinStepPoints;
        if (!improves) return;

        var oldStopOrder = this.FindProtectiveStopOrder();
        var closingSide = isLong ? Side.Sell : Side.Buy;

        var newStopResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.stopOrderTypeId,
            Quantity = position.Quantity,
            Side = closingSide,
            Comment = StrategyTag,
            TriggerPrice = candidateStop,
        });

        if (newStopResult.Status != TradingOperationResultStatus.Success)
        {
            this.Log($"[Order] trailing stop re-placement failed, leaving the existing stop resting: {newStopResult.Message}", StrategyLoggingLevel.Error);
            return;
        }

        this.currentStopPrice = candidateStop;

        if (oldStopOrder is not null)
        {
            var cancelResult = oldStopOrder.Cancel();
            if (cancelResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] failed to cancel the old stop after trailing — TWO stop orders may now be resting, check manually: {cancelResult.Message}", StrategyLoggingLevel.Error);
        }

        this.Log($"[Order] trailing stop moved to {candidateStop:0.##} (best price {this.openBestPrice:0.##}).", StrategyLoggingLevel.Trading);
    }

    /// <summary>Expanded 2026-10-15, operator: "i turned off the gex magnet in the settings and it
    /// hasnt entered anything and there is no logs other than heartbeat." The old heartbeat only
    /// showed raw level VALUES, not how close current price actually is to any of them — leaving no
    /// way to tell "nothing has happened yet" (several of the 8 setups are deliberately strict —
    /// e.g. budget fade needs price to reach ~90% of the FULL daily expected move) apart from a real
    /// silent bug. Now reports every live level ALONGSIDE its own distance from current price, so
    /// that distinction is visible without guessing or another round of log screenshots.</summary>
    private void MaybeLogHeartbeat()
    {
        var now = Core.TimeUtils.DateTimeUtcNow;
        if (now - this.lastHeartbeatUtc < TimeSpan.FromMinutes(HeartbeatIntervalMinutes)) return;
        this.lastHeartbeatUtc = now;

        var last = this.CurrentSymbol?.Last ?? 0;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[Heartbeat] price={last:0.##} EmDay={this.srmEngine.EmDay:0.##} Expanded={this.srmEngine.Expanded}. ");

        void AddSession(string name, SrmPeriod p, bool enabled)
        {
            if (!enabled) { sb.Append($"{name}=off. "); return; }
            if (!p.Active) { sb.Append($"{name}=not active. "); return; }
            var upper = p.OpenPrice + p.BudgetPts;
            var lower = p.OpenPrice - p.BudgetPts;
            sb.Append($"{name} budget[{lower:0.##}..{upper:0.##}] (Δupper={upper - last:0.##}, Δlower={last - lower:0.##}). ");
        }

        AddSession("Asia", this.srmEngine.Asia, this.TradeAsiaSession);
        AddSession("London", this.srmEngine.London, this.TradeLondonSession);
        AddSession("NY", this.srmEngine.Ny, this.TradeNySession);

        if (this.srmEngine.IbDone)
            sb.Append($"IB[{this.srmEngine.IbLow:0.##}..{this.srmEngine.IbHigh:0.##}] brokenUp={this.srmEngine.IbBrokenUp} brokenDn={this.srmEngine.IbBrokenDn}. ");

        if (this.srmEngine.VsMid is { } vsMid && this.srmEngine.VsHalf is { } vsHalf)
        {
            var r1 = vsMid + vsHalf;
            var s1 = vsMid - vsHalf;
            sb.Append($"VolStep mid={vsMid:0.##} R1={r1:0.##}(Δ{r1 - last:0.##}) S1={s1:0.##}(Δ{last - s1:0.##}). ");
        }

        if (this.GexEnabled)
        {
            if (this.gexResult is { } gex && this.gexRatio is { } ratio)
            {
                var zg = gex.FullChain.ZeroGamma * ratio;
                var cw = gex.FullChain.CallWall * ratio;
                var pw = gex.FullChain.PutWall * ratio;
                var mg = gex.FullChain.Magnet * ratio;
                var magnetNeed = this.srmEngine.EmDay * this.GexMagnetTriggerDistanceEmDayFraction;
                sb.Append(
                    $"GEX ZG={zg:0.##} CW={cw:0.##}(Δ{cw - last:0.##}) PW={pw:0.##}(Δ{last - pw:0.##}) "
                    + $"Magnet={mg:0.##}(Δ{mg - last:0.##}, needs {magnetNeed:0.##}). ");
            }
            else
            {
                sb.Append("GEX=waiting for first fetch. ");
            }
        }
        else
        {
            sb.Append("GEX=disabled. ");
        }

        var nearestNaked = this.srmEngine.NakedLevels
            .Where(l => l.TouchedUtc is null)
            .OrderBy(l => Math.Abs(l.Price - last))
            .FirstOrDefault();
        sb.Append(nearestNaked is not null
            ? $"Nearest untested level: {this.srmEngine.PeriodFor(nearestNaked.PeriodKind).Name} {(nearestNaked.IsHigh ? "H" : "L")} {nearestNaked.Price:0.##} (Δ{Math.Abs(nearestNaked.Price - last):0.##}). "
            : "No untested levels tracked. ");

        sb.Append($"In position: {this.MyPositions().Length > 0}.");

        this.Log(sb.ToString(), StrategyLoggingLevel.Trading);
    }

    private void SyncSrmVolTimer()
    {
        var wantedInterval = Math.Max(this.SrmVolPollIntervalSeconds, 15) * 1000;
        if (this.srmVolTimer is not null && this.srmVolTimerIntervalMs == wantedInterval) return;

        this.srmVolTimer?.Dispose();
        this.srmVolTimerIntervalMs = wantedInterval;
        this.srmVolTimer = new Timer(this.OnSrmVolTimer, null, 0, wantedInterval);
    }

    private void OnSrmVolTimer(object? state)
    {
        try
        {
            var ticker = string.IsNullOrWhiteSpace(this.SrmVolTicker) ? "VXN" : this.SrmVolTicker.Trim().ToUpperInvariant();
            this.srmVolLast = CboeIndexClient.FetchQuote(ticker).CurrentPrice;
            this.srmVixLast = CboeIndexClient.FetchQuote("VIX").CurrentPrice;
            this.srmVix3mLast = CboeIndexClient.FetchQuote("VIX3M").CurrentPrice;
        }
        catch (Exception ex)
        {
            this.Log($"[SRM] volatility fetch failed: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void SyncGexTimer()
    {
        if (!this.GexEnabled)
        {
            if (this.gexTimer is null) return;
            this.gexTimer.Dispose();
            this.gexTimer = null;
            return;
        }

        var wantedInterval = Math.Max(this.GexPollIntervalSeconds, 15) * 1000;
        if (this.gexTimer is not null && this.gexTimerIntervalMs == wantedInterval) return;

        this.gexTimer?.Dispose();
        this.gexTimerIntervalMs = wantedInterval;
        this.gexTimer = new Timer(this.OnGexTimer, null, 0, wantedInterval);
    }

    /// <summary>Samples the chart's own live price in the SAME callback as the GEX fetch, so the
    /// ratio used to convert QQQ/SPY-quoted GEX levels onto this chart's own price scale is always
    /// paired with a reference price from the same instant — see <see cref="gexRatio"/>'s own doc
    /// comment for the jitter bug this avoids by construction.</summary>
    private void OnGexTimer(object? state)
    {
        try
        {
            var underlying = string.IsNullOrWhiteSpace(this.GexUnderlying) ? "QQQ" : this.GexUnderlying.Trim().ToUpperInvariant();
            var chain = CboeOptionsClient.FetchChain(underlying, Math.Max(1, this.GexMaxDaysToExpiry));
            this.gexResult = GexEngine.Compute(chain.Contracts, chain.Spot, chain.AsOfUtc, this.GexRiskFreeRate);

            var chartPrice = this.CurrentSymbol?.Last ?? 0;
            this.gexRatio = chain.Spot > 0 && chartPrice > 0 && !double.IsNaN(chartPrice) ? chartPrice / chain.Spot : null;
        }
        catch (Exception ex)
        {
            this.Log($"[GEX] fetch failed: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void UpdateSrmVolatility()
    {
        double? priorClose = null;
        var atrFallback = 0d;
        if (this.dailyHistory is { Count: > 1 } daily && SrmEngine.TryComputeDailyVolInputs(daily, out var pc, out var atr))
        {
            priorClose = pc;
            atrFallback = atr;
        }

        this.srmEngine.SetVolatility(priorClose, this.srmVolLast, this.srmVixLast, this.srmVix3mLast, atrFallback);
    }

    private void DrainChartBars()
    {
        this.UpdateSrmVolatility();

        if (this.chartHistory is not { Count: > 1 } chart) return;

        var closedUpTo = chart.Count - 1;
        for (var i = this.barsSeen; i < closedUpTo; i++)
        {
            if (chart[i, SeekOriginHistory.Begin] is not HistoryItemBar item) continue;
            this.ProcessClosedBar(item, evaluateSignals: true);
        }

        this.barsSeen = closedUpTo;
    }

    // ---- SRM feed + signal evaluation -----------------------------------------------------------

    private SrmSettings BuildSrmSettings() => new(
        true, this.AsiaShare,
        true, this.LondonShare,
        true, this.NyShare,
        true, this.WeeklyBudgetMult, this.KProj, this.SessionRows,
        true, this.MonthlyBudgetMult, this.KProj, this.SessionRows,
        this.KProj, this.AdaptiveProjection, this.ExtMult,
        this.ExpandedScaleEnabled, this.ExpandedMultiplier,
        this.SessionRows, this.ValueAreaPercent,
        this.IbBreakoutEnabled, this.IbMinutes,
        this.VolStepFadeEnabled, this.VolStepMult);

    private void ProcessClosedBar(HistoryItemBar item, bool evaluateSignals)
    {
        var tickSize = this.CurrentSymbol?.TickSize ?? 0d;
        var settings = this.BuildSrmSettings();

        this.srmEngine.FeedBar(item.TimeLeft, item.TimeLeft, item.Open, item.High, item.Low, item.Volume, tickSize, settings);

        if (!evaluateSignals) return;
        if (this.srmEngine.EmDay is not { } emDay || emDay <= 0) return;
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return;
        if (this.MyPositions().Length > 0 || this.accountFloorBreached) return;

        // Operator, 2026-10-15: "after exiting an order we need a cool down... it just hit take
        // profit in a short and then entered a brand new short and is in instant drawdown." Blocks
        // EVERY setup alike, not just a repeat of the same one — a level that just resolved isn't
        // automatically a fresh, independent opportunity one bar later.
        if (this.CooldownAfterExitMinutes > 0 && (Core.TimeUtils.DateTimeUtcNow - this.lastExitUtc).TotalMinutes < this.CooldownAfterExitMinutes)
            return;

        var et = TimeZoneInfo.ConvertTimeFromUtc(item.TimeLeft, SessionZone);
        var inAsia = IsInWindow(et, 18, 0, 3, 0);
        var inLondon = IsInWindow(et, 3, 0, 9, 30);
        var inNy = IsInWindow(et, 9, 30, 16, 0);
        var sessionAllowed = (inAsia && this.TradeAsiaSession) || (inLondon && this.TradeLondonSession) || (inNy && this.TradeNySession);
        if (!sessionAllowed) return;

        // IB is inherently NY-specific (by definition — "first hour of NY") regardless of which
        // sessions are enabled for trading, so its own one-shot reset stays tied to NY's StartUtc.
        if (this.srmEngine.Ny.StartUtc != this.lastNyStartUtc)
        {
            this.lastNyStartUtc = this.srmEngine.Ny.StartUtc;
            this.ibUpTraded = false;
            this.ibDownTraded = false;
        }

        // Budget-band fade trades whichever ENABLED session is currently active — Asia's own
        // budget while Asia is active and TradeAsiaSession is on, NY's own budget while NY is
        // active and TradeNySession is on, etc. (operator's own ask, 2026-10-15: "i would like to
        // work in asia as well" — this setup was hardcoded to NY only before that).
        SrmPeriod? activeSession = null;
        if (this.TradeAsiaSession && this.srmEngine.Asia.Active) activeSession = this.srmEngine.Asia;
        else if (this.TradeLondonSession && this.srmEngine.London.Active) activeSession = this.srmEngine.London;
        else if (this.TradeNySession && this.srmEngine.Ny.Active) activeSession = this.srmEngine.Ny;

        if (activeSession is null || activeSession.StartUtc != this.lastActiveSessionStartUtc)
        {
            this.lastActiveSessionStartUtc = activeSession?.StartUtc ?? DateTime.MinValue;
            this.budgetFadeUpperDone = false;
            this.budgetFadeLowerDone = false;
        }

        if (this.srmEngine.VsMid != this.lastVsMidSeen)
        {
            this.lastVsMidSeen = this.srmEngine.VsMid;
            this.volStepUpTraded = false;
            this.volStepDownTraded = false;
        }

        var stopDist = emDay * this.StopFractionOfEmDay;
        var targetDist = emDay * this.TargetFractionOfEmDay;
        if (stopDist <= 0 || targetDist <= 0) return;

        // 1) Naked level reaction — any session/week/month level this bar just touched AND
        // rejected (closed back away from it) rather than cleanly broke through.
        if (this.NakedLevelEnabled)
        {
            foreach (var lvl in this.srmEngine.NakedLevels)
            {
                if (lvl.TouchedUtc != item.TimeLeft) continue;

                if (lvl.IsHigh && item.Close <= lvl.Price - this.NakedLevelTolerancePoints)
                {
                    if (this.EnterTrade(Side.Sell, item.Close, lvl.Price, stopDist, targetDist, emDay, $"naked {this.srmEngine.PeriodFor(lvl.PeriodKind).Name} H {lvl.Price:0.##} rejection"))
                        return;
                }
                else if (!lvl.IsHigh && item.Close >= lvl.Price + this.NakedLevelTolerancePoints)
                {
                    if (this.EnterTrade(Side.Buy, item.Close, lvl.Price, stopDist, targetDist, emDay, $"naked {this.srmEngine.PeriodFor(lvl.PeriodKind).Name} L {lvl.Price:0.##} rejection"))
                        return;
                }
            }
        }

        // 2) Session budget-band fade — whichever session is currently active AND enabled for
        // trading (see activeSession above); its own volatility-implied extreme, not NY's
        // specifically. Requires the bar's own CLOSE to have come back inside the band, not just a
        // wick touch — a mere high/low touch with no close confirmation left the reference price
        // free to land on either side of the level used for the stop, which is exactly what
        // produced an immediately-marketable (wrong-side) stop price — operator reports, 2026-10-15:
        // "it just instant sold and closed the position within a second" / "it placed a take profit
        // but not a stop loss." The one-shot flag is still consumed on the mere touch, matching the
        // established "a touch consumes untested status whether or not a trade follows" liquidity-
        // sweep convention elsewhere in this codebase — only the ENTRY itself now additionally
        // requires the close confirmation.
        if (this.BudgetFadeEnabled && activeSession is { } sess)
        {
            var upper = sess.OpenPrice + sess.BudgetPts;
            var lower = sess.OpenPrice - sess.BudgetPts;

            if (!this.budgetFadeUpperDone && item.High >= upper)
            {
                this.budgetFadeUpperDone = true;
                if (item.Close <= upper && this.EnterTrade(Side.Sell, item.Close, upper, stopDist, targetDist, emDay, $"{sess.Name} budget fade upper {upper:0.##}"))
                    return;
            }
            else if (!this.budgetFadeLowerDone && item.Low <= lower)
            {
                this.budgetFadeLowerDone = true;
                if (item.Close >= lower && this.EnterTrade(Side.Buy, item.Close, lower, stopDist, targetDist, emDay, $"{sess.Name} budget fade lower {lower:0.##}"))
                    return;
            }
        }

        // 3) NY initial balance breakout — first break each side only. Requires the CLOSE to have
        // confirmed beyond the IB level too (not just a wick) — same reasoning as the budget fade
        // above, and arguably a better breakout filter anyway (a wick-only break with no close
        // confirmation isn't much of a breakout).
        if (this.IbBreakoutEnabled && this.srmEngine.IbDone)
        {
            if (!this.ibUpTraded && this.srmEngine.IbBrokenUp)
            {
                this.ibUpTraded = true;
                if (this.srmEngine.IbHigh is { } ibH && item.Close > ibH
                    && this.EnterTrade(Side.Buy, item.Close, ibH, stopDist, targetDist, emDay, $"NY IB breakout up {ibH:0.##}"))
                    return;
            }
            else if (!this.ibDownTraded && this.srmEngine.IbBrokenDn)
            {
                this.ibDownTraded = true;
                if (this.srmEngine.IbLow is { } ibL && item.Close < ibL
                    && this.EnterTrade(Side.Sell, item.Close, ibL, stopDist, targetDist, emDay, $"NY IB breakout down {ibL:0.##}"))
                    return;
            }
        }

        // 4) Vol-Step Range fade — first touch of R1/S1 per step only. Same close-confirmation
        // requirement as budget fade/IB breakout above.
        if (this.VolStepFadeEnabled && this.srmEngine.VsMid is { } vsMid && this.srmEngine.VsHalf is { } vsHalf)
        {
            var r1 = vsMid + vsHalf;
            var s1 = vsMid - vsHalf;

            if (!this.volStepUpTraded && item.High >= r1)
            {
                this.volStepUpTraded = true;
                if (item.Close <= r1 && this.EnterTrade(Side.Sell, item.Close, r1, stopDist, targetDist, emDay, $"Vol-Step fade R1 {r1:0.##}"))
                    return;
            }
            else if (!this.volStepDownTraded && item.Low <= s1)
            {
                this.volStepDownTraded = true;
                if (item.Close >= s1 && this.EnterTrade(Side.Buy, item.Close, s1, stopDist, targetDist, emDay, $"Vol-Step fade S1 {s1:0.##}"))
                    return;
            }
        }

        // 5-8) Live GEX levels (Call Wall, Put Wall, Zero Gamma, Magnet) — QQQ/SPY-quoted,
        // converted to this chart's own price via the ratio sampled alongside the GEX fetch
        // itself (see gexRatio's own doc comment). Four genuinely different mechanics, not one
        // shape forced onto all four: Call Wall/Put Wall are touch-and-reject (same shape as the
        // naked-level setup above — these behave like conventional support/resistance). Zero
        // Gamma is a regime flip, not a bounce level — traded as cross-and-continue WITH the new
        // regime's direction instead of faded. Magnet pulls price toward it from a distance rather
        // than reacting when touched — traded as "far enough away, bet on drift toward it."
        if (this.GexEnabled && this.gexResult is { } gex && this.gexRatio is { } ratio && ratio > 0)
        {
            if (this.CallWallFadeEnabled && gex.FullChain.CallWall is { } cw0)
            {
                var callWall = cw0 * ratio;
                if (LevelChanged(this.lastCallWallSeen, callWall)) { this.lastCallWallSeen = callWall; this.callWallTraded = false; }

                if (!this.callWallTraded && item.High >= callWall && item.Close <= callWall - this.GexWallTolerancePoints)
                {
                    this.callWallTraded = true;
                    if (this.EnterTrade(Side.Sell, item.Close, callWall, stopDist, targetDist, emDay, $"GEX Call Wall fade {callWall:0.##}"))
                        return;
                }
            }

            if (this.PutWallFadeEnabled && gex.FullChain.PutWall is { } pw0)
            {
                var putWall = pw0 * ratio;
                if (LevelChanged(this.lastPutWallSeen, putWall)) { this.lastPutWallSeen = putWall; this.putWallTraded = false; }

                if (!this.putWallTraded && item.Low <= putWall && item.Close >= putWall + this.GexWallTolerancePoints)
                {
                    this.putWallTraded = true;
                    if (this.EnterTrade(Side.Buy, item.Close, putWall, stopDist, targetDist, emDay, $"GEX Put Wall fade {putWall:0.##}"))
                        return;
                }
            }

            if (this.ZeroGammaCrossEnabled && gex.FullChain.ZeroGamma is { } zg0)
            {
                var zeroGamma = zg0 * ratio;
                if (LevelChanged(this.lastZeroGammaSeen, zeroGamma)) { this.lastZeroGammaSeen = zeroGamma; this.zgWasAbove = null; }

                var nowAbove = item.Close > zeroGamma;
                if (this.zgWasAbove is { } wasAbove && wasAbove != nowAbove)
                {
                    if (this.EnterTrade(nowAbove ? Side.Buy : Side.Sell, item.Close, zeroGamma, stopDist, targetDist, emDay, $"GEX Zero Gamma cross {(nowAbove ? "up" : "down")} {zeroGamma:0.##}"))
                    {
                        this.zgWasAbove = nowAbove;
                        return;
                    }
                }
                this.zgWasAbove = nowAbove;
            }

            if (this.MagnetPullEnabled && gex.FullChain.Magnet is { } mg0)
            {
                var magnet = mg0 * ratio;
                if (LevelChanged(this.lastMagnetSeen, magnet)) { this.lastMagnetSeen = magnet; this.magnetTraded = false; }

                var distance = item.Close - magnet;
                var magnetTriggerDistance = emDay * this.GexMagnetTriggerDistanceEmDayFraction;

                // FOUND 2026-10-15, operator: "the gex magenet pull trades seem to be very bad on
                // the rr and never really work out... soon as we enter we are in crazy draw down."
                // This was the ONLY one of the 8 setups with ZERO reaction confirmation — every
                // other setup requires an actual rejection (a wick touching a level AND the close
                // coming back the other way) or a confirmed break before entering; Magnet pull fired
                // the instant price was simply far enough away, with no evidence the move was
                // actually exhausted. If price got that far because it's genuinely trending, this
                // was a blind counter-trend bet straight into the trend's own teeth — exactly what
                // "instant drawdown" looks like. Now requires the SAME kind of rejection evidence
                // as everything else: this bar must have pushed to a fresh extreme AWAY from the
                // magnet (its own high if price is above, low if below) and then closed back toward
                // it by at least GexMagnetRejectionPoints — a real sign of exhaustion, not just
                // distance. A bar still trending hard typically closes near its own extreme with
                // little opposing wick and will correctly fail this check and stay out.
                if (!this.magnetTraded && Math.Abs(distance) >= magnetTriggerDistance)
                {
                    var isAbove = distance > 0;
                    var extremeThisBar = isAbove ? item.High : item.Low;
                    var extremeDistance = Math.Abs(extremeThisBar - magnet);
                    var closeDistance = Math.Abs(item.Close - magnet);
                    var rejected = extremeDistance - closeDistance >= this.GexMagnetRejectionPoints;

                    if (rejected)
                    {
                        this.magnetTraded = true;
                        var side = isAbove ? Side.Sell : Side.Buy;
                        // No structural invalidation level for a distance-based pull — null falls
                        // through to the volatility-scaled stop. The target naturally resolves to
                        // the magnet itself anyway: TryFindKeyLevelTarget already considers it a
                        // candidate.
                        if (this.EnterTrade(side, item.Close, null, stopDist, targetDist, emDay, $"GEX Magnet pull toward {magnet:0.##}"))
                            return;
                    }
                }
            }
        }
    }

    /// <summary>True on a genuine change in a GEX level's own chart-converted price, not just
    /// floating-point/ratio noise — used to decide whether a setup's one-shot "already traded this
    /// level" flag should reset. A plain `!=` comparison was resetting these flags on every GEX
    /// refresh (~60s) even when the level hadn't meaningfully moved, which (combined with GEX
    /// Magnet pull's own too-tight trigger distance, fixed separately) left that one setup firing
    /// far more often than the other seven — operator, 2026-10-15: "why is it only ever trading on
    /// the gex magnet pulls."</summary>
    private const double LevelChangeTolerancePoints = 0.5;

    private static bool LevelChanged(double? last, double current) =>
        last is not { } l || Math.Abs(l - current) > LevelChangeTolerancePoints;

    private static int MinutesOfDay(DateTime t) => (t.Hour * 60) + t.Minute;

    /// <summary>Minute-precision, midnight-wraparound-safe window check — same shape as
    /// mesOrbStrategy's own helper of the same name (ported there from oceansStackStrategy).</summary>
    private static bool IsInWindow(DateTime local, int startHour, int startMinute, int endHour, int endMinute)
    {
        var startTotal = (startHour * 60) + startMinute;
        var endTotal = (endHour * 60) + endMinute;
        var nowTotal = MinutesOfDay(local);

        if (startTotal == endTotal) return true;
        return startTotal < endTotal
            ? nowTotal >= startTotal && nowTotal < endTotal
            : nowTotal >= startTotal || nowTotal < endTotal;
    }

    // ---- entry / protective orders ---------------------------------------------------------------

    /// <summary>Stop beyond the structural level that invalidates the trade (plus a small buffer),
    /// falling back to the volatility-scaled distance when structure mode is off, there's no level
    /// for this particular setup (GEX Magnet pull), or the structural stop would land on the WRONG
    /// side of the entry (or too close to it).
    ///
    /// That last case is a real one, not theoretical: three setups (budget-band fade, NY IB
    /// breakout, Vol-Step fade) only confirmed a WICK touching their level, not the bar's own
    /// CLOSE — so the entry's own reference price could land on either side of the level being used
    /// for the stop. A stop requested on the same side as (or too close to) the entry is
    /// immediately marketable and gets refused/instantly closed — operator report, 2026-10-15: "it
    /// just instant sold and closed the position within a second," confirmed via the real log line
    /// ("said protective stop is missing") matching this exact failure mode in
    /// RepairMissingProtectiveOrders. Fixed at BOTH ends: this method now refuses to ever hand back
    /// a wrong-side/too-close stop regardless of which setup calls it (protects any future setup
    /// too), AND the three vulnerable setups themselves were tightened to require a close
    /// confirmation before entering at all (see each one's own entry condition below) — so this
    /// fallback should rarely even need to fire going forward.
    ///
    /// SECOND BUG, same day, caught from a live screenshot: that minimum-distance check above
    /// only ever caught a stop on the WRONG side or too CLOSE — nothing capped a stop that was
    /// simply too FAR. A fast-moving bar can satisfy "closed on the correct side of the level"
    /// (the fix above) while still closing hundreds of points away from it (e.g. a big down-wick
    /// bar touches the budget band with its high, then closes far below) — the close-confirmation
    /// fix guaranteed DIRECTION, never MAGNITUDE. Operator: "it did it again look" + a real
    /// screenshot showing a stop ~493 points from entry (a ~2-contract MNQ position, visibly
    /// "the stop loss is wayup there and take profit is super close" — the target already had this
    /// same cap from the PRIOR fix this same conversation, the stop never did). Now capped the same
    /// way, via <see cref="MaxStopDistanceEmDayFraction"/> × emDay.</summary>
    private double ResolveStopPrice(bool isLong, double? structureLevel, double referencePrice, double fallbackDist, double emDay)
    {
        var maxDistance = emDay * this.MaxStopDistanceEmDayFraction;

        if (this.UseStructureStops && structureLevel is { } level && level > 0)
        {
            var structureStop = isLong ? level - this.StructureStopBufferPoints : level + this.StructureStopBufferPoints;
            var distance = isLong ? referencePrice - structureStop : structureStop - referencePrice;

            if (distance >= this.StructureStopBufferPoints && distance <= maxDistance)
                return structureStop;
        }

        // Clamped to the SAME ceiling as the structure path, regardless of StopFractionOfEmDay —
        // operator report, 2026-10-15, with a real log: a trade's stop landed at exactly
        // emDay × 0.25 (the OLD default, before that fraction was tightened to 0.05 earlier this
        // same conversation) because the structural level was correctly rejected as too far, but
        // the FALLBACK it dropped to had no ceiling of its own. If `StopFractionOfEmDay` is ever
        // left wider than intended (stale settings, a bad manual edit, whatever), this stops it
        // from producing an outsized stop regardless — the fraction is a SUGGESTION within this
        // ceiling, not an override of it.
        var clampedFallback = Math.Min(fallbackDist, maxDistance);
        return isLong ? referencePrice - clampedFallback : referencePrice + clampedFallback;
    }

    /// <summary>Nearest "key level" (untested SRM naked level, session POC/VAH/VAL, or a live GEX
    /// level) or wickless-candle extreme beyond the entry in the trade's own favorable direction —
    /// whichever of the two is CLOSER (same "nearest, not farthest" convention mesOrbStrategy's own
    /// opposing-level targeting already uses). Falls back to the volatility-scaled distance when
    /// structure mode is off, nothing qualifies, the nearest candidate is too close to be worth
    /// targeting (<see cref="MinTargetDistancePoints"/>), OR — operator report, 2026-10-15: "it
    /// placed it at the other call wall which was a massive move away" — the nearest candidate is
    /// too FAR to be a realistic intraday target (<see cref="MaxTargetDistanceEmDayFraction"/> ×
    /// `emDay`). Both sources are genuinely unbounded in how far away they can sit: a GEX wall or a
    /// week/month naked level can easily be hundreds of points off, and the wickless-candle scanner
    /// only looks within <see cref="StructureLookbackBars"/> bars but places no ceiling on price
    /// DISTANCE within that window. Without this cap, "nearest of two candidates" can still mean
    /// "nearest of two candidates that are both far away" whenever nothing closer exists — this
    /// stops that from producing an unrealistic target instead of just admitting nothing nearby
    /// qualifies.</summary>
    private double ResolveTargetPrice(bool isLong, double referencePrice, double fallbackDist, double emDay)
    {
        var maxDistance = emDay * this.MaxTargetDistanceEmDayFraction;

        if (this.UseStructureTargets)
        {
            var haveKey = this.TryFindKeyLevelTarget(isLong, referencePrice, out var keyTarget) && Math.Abs(keyTarget - referencePrice) <= maxDistance;
            var haveWickless = this.TryFindWicklessTarget(isLong, referencePrice, out var wicklessTarget) && Math.Abs(wicklessTarget - referencePrice) <= maxDistance;

            double? chosen = (haveKey, haveWickless) switch
            {
                (true, true) => isLong ? Math.Min(keyTarget, wicklessTarget) : Math.Max(keyTarget, wicklessTarget),
                (true, false) => keyTarget,
                (false, true) => wicklessTarget,
                _ => null,
            };

            if (chosen is { } t && Math.Abs(t - referencePrice) >= this.MinTargetDistancePoints)
                return t;
        }

        // Same clamp as ResolveStopPrice's own fallback — see that method's doc comment for the
        // real log evidence behind it. TargetFractionOfEmDay is a suggestion within this ceiling,
        // not an override of it.
        var clampedFallback = Math.Min(fallbackDist, maxDistance);
        return isLong ? referencePrice + clampedFallback : referencePrice - clampedFallback;
    }

    /// <summary>Nearest untested SRM naked level, session POC/VAH/VAL, or live GEX level beyond
    /// <paramref name="fromPrice"/> in the wanted direction.</summary>
    private bool TryFindKeyLevelTarget(bool wantAbove, double fromPrice, out double target)
    {
        target = 0;
        double? best = null;

        void Consider(double? price)
        {
            if (price is not { } p || p <= 0) return;
            if (wantAbove ? p <= fromPrice : p >= fromPrice) return;
            if (best is null || (wantAbove ? p < best.Value : p > best.Value)) best = p;
        }

        foreach (var lvl in this.srmEngine.NakedLevels)
            if (lvl.TouchedUtc is null) Consider(lvl.Price);

        var (poc, vah, val) = this.srmEngine.Ny.ComputeProfile(this.ValueAreaPercent);
        Consider(poc);
        Consider(vah);
        Consider(val);

        if (this.GexEnabled && this.gexResult is { } gex && this.gexRatio is { } ratio && ratio > 0)
        {
            Consider(gex.FullChain.CallWall * ratio);
            Consider(gex.FullChain.PutWall * ratio);
            Consider(gex.FullChain.ZeroGamma * ratio);
            Consider(gex.FullChain.Magnet * ratio);
        }

        if (best is null) return false;
        target = best.Value;
        return true;
    }

    /// <summary>A "wickless" candle (operator's own term) — no wick at all on the side that would
    /// face the entry, within <see cref="WicklessTolerancePoints"/> — marks a clean, one-directional
    /// move with no rejection recorded at that exact price, used as a target: the nearest such
    /// candle's own extreme beyond <paramref name="fromPrice"/> in the wanted direction, searched
    /// back up to <see cref="StructureLookbackBars"/> CLOSED chart bars.</summary>
    private bool TryFindWicklessTarget(bool wantAbove, double fromPrice, out double target)
    {
        target = 0;
        if (this.chartHistory is not { Count: > 1 } chart) return false;

        var closedUpTo = chart.Count - 1;
        var start = Math.Max(0, closedUpTo - Math.Max(1, this.StructureLookbackBars));

        double? best = null;
        for (var i = closedUpTo - 1; i >= start; i--)
        {
            if (chart[i, SeekOriginHistory.Begin] is not HistoryItemBar bar) continue;

            if (wantAbove)
            {
                var bodyLow = Math.Min(bar.Open, bar.Close);
                if (bodyLow - bar.Low > this.WicklessTolerancePoints) continue; // has a lower wick
                if (bar.Low <= fromPrice) continue;
                if (best is null || bar.Low < best.Value) best = bar.Low;
            }
            else
            {
                var bodyHigh = Math.Max(bar.Open, bar.Close);
                if (bar.High - bodyHigh > this.WicklessTolerancePoints) continue; // has an upper wick
                if (bar.High >= fromPrice) continue;
                if (best is null || bar.High > best.Value) best = bar.High;
            }
        }

        if (best is null) return false;
        target = best.Value;
        return true;
    }

    /// <summary>
    /// <paramref name="structureLevel"/> is the price that INVALIDATES this specific setup's own
    /// thesis if price trades back through it (the level just rejected, the budget band just
    /// touched, the IB side just broken, the wall/Zero Gamma just faded/crossed) — null for setups
    /// with no natural structural anchor (GEX Magnet pull is a distance bet, not a level reaction).
    /// <paramref name="stopFallbackDist"/>/<paramref name="targetFallbackDist"/> are the
    /// volatility-scaled distances used only when structure mode is off or no suitable
    /// level/wickless candle is found — see <see cref="ResolveStopPrice"/>/<see cref="ResolveTargetPrice"/>.
    /// </summary>
    private bool EnterTrade(Side side, double referencePrice, double? structureLevel, double stopFallbackDist, double targetFallbackDist, double emDay, string reason)
    {
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return false;
        if (this.MyPositions().Length > 0) return false;
        if (this.accountFloorBreached) return false;

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0) return false;

        var isLong = side == Side.Buy;
        var stopPrice = this.ResolveStopPrice(isLong, structureLevel, referencePrice, stopFallbackDist, emDay);
        var targetPrice = this.ResolveTargetPrice(isLong, referencePrice, targetFallbackDist, emDay);

        // FOUND 2026-10-15, operator, with real log numbers worked out: "it seems like every single
        // time it enters off gex magnet or ib breakout its always in drawdown right away and the
        // risk to reward is massive in negative rr." A real trade: 14.75pts of reward against
        // 69.25pts of risk (≈1:4.7 AGAINST). Root cause: a setup with no structural stop anchor
        // (GEX Magnet pull, always) falls back to the full volatility-scaled stop distance, while
        // the TARGET logic always grabs the NEAREST qualifying level regardless of how that compares
        // to the stop — nothing previously checked whether the resulting trade was even sane before
        // taking it. Rejects here, before the order is ever placed, rather than trying to force a
        // better target (which would mean picking something farther than "nearest," undermining the
        // achievability that choice was made for in the first place).
        var riskDist = Math.Abs(referencePrice - stopPrice);
        var rewardDist = Math.Abs(targetPrice - referencePrice);
        if (this.MinRewardToRiskRatio > 0 && riskDist > 0 && rewardDist / riskDist < this.MinRewardToRiskRatio)
        {
            this.Log(
                $"[Signal] skipped ({reason}) — reward:risk {rewardDist:0.##}:{riskDist:0.##} "
                + $"({rewardDist / riskDist:0.##}) is below the minimum {this.MinRewardToRiskRatio:0.##}.",
                StrategyLoggingLevel.Trading);
            return false;
        }

        this.pendingStopPrice = stopPrice;
        this.pendingTargetPrice = targetPrice;

        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
        {
            Account = this.CurrentAccount,
            Symbol = this.CurrentSymbol,
            OrderTypeId = this.orderTypeId,
            Quantity = this.Quantity,
            Side = side,
            Comment = StrategyTag,
        });

        if (result.Status != TradingOperationResultStatus.Success)
        {
            this.Log($"[Order] entry failed ({reason}): {result.Message}", StrategyLoggingLevel.Error);
            return false;
        }

        this.Log(
            $"[Signal] {side} entry — {reason} (reference={referencePrice:0.##}, "
            + $"stop={this.pendingStopPrice:0.##}, target={this.pendingTargetPrice:0.##}).",
            StrategyLoggingLevel.Trading);
        return true;
    }

    private bool PlaceProtectiveOrders(Position position)
    {
        if (this.CurrentAccount is null || this.CurrentSymbol is null) return false;

        var isLong = position.Side == Side.Buy;
        var closingSide = isLong ? Side.Sell : Side.Buy;
        var tickSize = this.CurrentSymbol.TickSize;

        // An ADOPTED position (this instance never itself signaled it — e.g. after a restart)
        // leaves pendingStopPrice/pendingTargetPrice at their OnRun-reset default of 0. Fall back
        // to a volatility-scaled stop/target from the real fill rather than place a protective
        // order at price zero — same fix mesOrbStrategy/finchDomScalpStrategy already proved out.
        if (this.pendingStopPrice == 0 && this.pendingTargetPrice == 0 && tickSize > 0)
        {
            var emDay = this.srmEngine.EmDay ?? 20.0; // generic fallback if volatility data isn't available yet
            var stopDist = emDay * this.StopFractionOfEmDay;
            var targetDist = emDay * this.TargetFractionOfEmDay;
            this.pendingStopPrice = isLong ? position.OpenPrice - stopDist : position.OpenPrice + stopDist;
            this.pendingTargetPrice = isLong ? position.OpenPrice + targetDist : position.OpenPrice - targetDist;
            this.Log(
                "[Risk] ADOPTED a position this instance didn't itself open — no real signal data "
                + "to protect it with, so placed a fallback volatility-scaled stop/target from its real fill instead.",
                StrategyLoggingLevel.Trading);
        }

        var stopPrice = this.pendingStopPrice;
        var targetPrice = this.pendingTargetPrice;

        if (tickSize > 0)
        {
            stopPrice = Math.Round(stopPrice / tickSize, MidpointRounding.AwayFromZero) * tickSize;
            targetPrice = Math.Round(targetPrice / tickSize, MidpointRounding.AwayFromZero) * tickSize;
        }

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
            this.Log(
                $"[Order] protective stop failed: {stopResult.Message}. Closing position "
                + "immediately — a trade must never run with no stop at all.",
                StrategyLoggingLevel.Error);

            var closeResult = position.Close();
            if (closeResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] failed to close unprotected position: {closeResult.Message}", StrategyLoggingLevel.Error);
            return false;
        }

        this.currentStopPrice = stopPrice;

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
            $"[Order] protective stop={stopPrice:0.##} target={targetPrice:0.##} "
            + $"(fill={position.OpenPrice:0.##}) placed as separate orders.",
            StrategyLoggingLevel.Trading);

        return true;
    }

    private void Core_PositionAdded(Position obj)
    {
        if (obj is null || !this.IsMyPosition(obj.Symbol, obj.Account, obj.Comment)) return;
        if (this.protectiveOrdersPlaced) return;

        this.protectiveOrdersPlaced = true;

        try
        {
            // Active in-trade management only starts if the stop ACTUALLY landed — a failed stop
            // closes the position itself inside PlaceProtectiveOrders and returns false, in which
            // case there's nothing left to manage.
            if (this.PlaceProtectiveOrders(obj)) // sets this.currentStopPrice itself, tick-rounded
            {
                // Seeds from the REAL fill, not the signal's own reference price — openBestPrice
                // starts at the real entry so the first poll tick after this can only ever see
                // favorable excursion from here forward, never a phantom head start.
                this.openSide = obj.Side;
                this.openEntryPrice = obj.OpenPrice;
                this.openBestPrice = obj.OpenPrice;
                this.inTradeManaged = true;
            }
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

        this.protectiveOrdersPlaced = false;
        this.inTradeManaged = false;
        this.lastExitUtc = Core.TimeUtils.DateTimeUtcNow;

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
            this.Log($"[Order] failed to clean up leftover orders: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void Core_OrdersHistoryAdded(OrderHistory obj)
    {
        if (obj is null || !this.IsMine(obj.Symbol, obj.Account, obj.Comment)) return;

        if (obj.Status == OrderStatus.Refused)
            this.RepairMissingProtectiveOrders();
    }

    /// <summary>Reacts to ANY refusal on our account by checking what's ACTUALLY resting right now
    /// and repairing whichever leg is missing, rather than trusting a one-time placement result —
    /// a broker can accept a protective order and refuse it asynchronously a couple ms later.
    /// Self-correcting by design: if both legs are genuinely resting, this does nothing.</summary>
    private void RepairMissingProtectiveOrders()
    {
        if (!this.protectiveOrdersPlaced) return;

        var positions = this.MyPositions();
        if (positions.Length == 0) return;

        var position = positions[0]; // one position at a time, by design
        var tickSize = this.CurrentSymbol?.TickSize ?? 0;
        if (tickSize <= 0) return;

        var isLong = position.Side == Side.Buy;
        var closingSide = isLong ? Side.Sell : Side.Buy;

        if (this.FindProtectiveStopOrder() is null)
        {
            this.Log(
                "[Risk] protective STOP is missing (refused asynchronously after initially "
                + "reporting success) — closing immediately rather than run unprotected.",
                StrategyLoggingLevel.Error);

            var closeResult = position.Close();
            if (closeResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] failed to close unprotected position: {closeResult.Message}", StrategyLoggingLevel.Error);
            return;
        }

        if (this.FindProtectiveTargetOrder() is null)
        {
            var emDay = this.srmEngine.EmDay ?? 20.0;
            var targetDist = emDay * this.TargetFractionOfEmDay;
            var fallbackTarget = isLong ? position.OpenPrice + targetDist : position.OpenPrice - targetDist;
            fallbackTarget = Math.Round(fallbackTarget / tickSize, MidpointRounding.AwayFromZero) * tickSize;

            this.Log(
                $"[Risk] protective TARGET is missing (refused asynchronously after initially "
                + $"reporting success) — re-placing a fallback target at {fallbackTarget:0.##}.",
                StrategyLoggingLevel.Trading);

            var targetResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Account = this.CurrentAccount,
                Symbol = this.CurrentSymbol,
                OrderTypeId = this.limitOrderTypeId,
                Quantity = position.Quantity,
                Side = closingSide,
                Comment = StrategyTag,
                Price = fallbackTarget,
            });

            if (targetResult.Status != TradingOperationResultStatus.Success)
                this.Log($"[Order] fallback target re-placement failed: {targetResult.Message}", StrategyLoggingLevel.Error);
        }
    }

    private Order? FindProtectiveStopOrder() =>
        this.MyOrders().FirstOrDefault(o => string.Equals(o.OrderTypeId, this.stopOrderTypeId, StringComparison.Ordinal));

    private Order? FindProtectiveTargetOrder() =>
        this.MyOrders().FirstOrDefault(o => string.Equals(o.OrderTypeId, this.limitOrderTypeId, StringComparison.Ordinal));

    // ---- ownership helpers ---------------------------------------------------------------------

    private bool IsMine(Symbol? symbol, Account? account, string? comment)
    {
        if (account is null || this.CurrentAccount is null) return false;
        if (!string.Equals(account.Id, this.CurrentAccount.Id, StringComparison.Ordinal)) return false;
        if (symbol is null || this.CurrentSymbol is null) return false;
        if (!string.Equals(symbol.ConnectionId, this.CurrentSymbol.ConnectionId, StringComparison.Ordinal)) return false;

        if (this.resolvedSymbolId is null)
        {
            var commentMatches = comment == StrategyTag;
            var sameExactContract = string.Equals(symbol.Id, this.CurrentSymbol.Id, StringComparison.Ordinal);
            if (!commentMatches && !sameExactContract) return false;

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

        var commentMatches = comment == StrategyTag;
        var sameExactContract = this.CurrentSymbol is not null && string.Equals(symbol.Id, this.CurrentSymbol.Id, StringComparison.Ordinal);
        if (!commentMatches && !sameExactContract) return false;

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

    // ---- account-balance-based risk limits, restart-immune — ported from mesOrbStrategy ----------

    private static string DailyStateFilePath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DailyStateFileName);

    private static int EstSessionDayKey(DateTime utcNow)
    {
        var est = TimeZoneInfo.ConvertTimeFromUtc(utcNow, SessionZone);
        return (est.Year * 10000) + (est.Month * 100) + est.Day;
    }

    private void EnsureDailyBalanceState(double currentBalance, int todayKey)
    {
        if (this.balanceDayKey == todayKey) return;

        var breachedFromFile = false;
        var haveTodaysBalance = false;

        try
        {
            var path = DailyStateFilePath();
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length >= 3)
                {
                    breachedFromFile = lines[2].Trim() == "1";
                    if (int.TryParse(lines[0], out var savedDayKey)
                        && double.TryParse(lines[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var savedBalance)
                        && savedDayKey == todayKey)
                    {
                        this.dayStartBalance = savedBalance;
                        haveTodaysBalance = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.Log($"[Risk] could not read daily balance state file, starting fresh: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }

        this.accountFloorBreached = this.accountFloorBreached || breachedFromFile;
        if (!haveTodaysBalance) this.dayStartBalance = currentBalance;
        this.balanceDayKey = todayKey;
        this.WriteDailyBalanceState();
    }

    private void WriteDailyBalanceState()
    {
        try
        {
            File.WriteAllLines(DailyStateFilePath(), new[]
            {
                this.balanceDayKey.ToString(CultureInfo.InvariantCulture),
                this.dayStartBalance.ToString("R", CultureInfo.InvariantCulture),
                this.accountFloorBreached ? "1" : "0",
            });
        }
        catch (Exception ex)
        {
            this.Log($"[Risk] could not write daily balance state file: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private bool IsDailyLossLimitBreached()
    {
        if (this.MaxDailyLoss <= 0) return false;
        if (this.CurrentAccount?.Balance is not { } balance) return false;

        this.EnsureDailyBalanceState(balance, EstSessionDayKey(Core.TimeUtils.DateTimeUtcNow));
        return balance - this.dayStartBalance <= -this.MaxDailyLoss;
    }

    private bool IsAccountFloorBreached()
    {
        if (this.CurrentAccount?.Balance is not { } balance) return this.accountFloorBreached;

        this.EnsureDailyBalanceState(balance, EstSessionDayKey(Core.TimeUtils.DateTimeUtcNow));

        if (!this.accountFloorBreached && this.AccountBalanceFloor > 0 && balance <= this.AccountBalanceFloor)
        {
            this.accountFloorBreached = true;
            this.WriteDailyBalanceState();
            this.Log(
                $"[Risk] ACCOUNT BALANCE FLOOR BREACHED — ${balance:F2} <= ${this.AccountBalanceFloor:F2}. "
                + "This is STICKY and does NOT auto-reset, including across a restart — update "
                + "AccountBalanceFloor yourself once you've confirmed the real account state.",
                StrategyLoggingLevel.Trading);
        }

        return this.accountFloorBreached;
    }
}
