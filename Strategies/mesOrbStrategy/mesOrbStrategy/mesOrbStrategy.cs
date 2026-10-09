using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace mesOrbStrategy;

/// <summary>
/// A port of the operator's own discretionary MES opening-range-breakout play (their own words,
/// 2026-10-05): mark the 8:00-8:15 ET high/low, wait for a 5-min close beyond either side, wait
/// for price to pull back to ~50% of the range (the midpoint) or close to it, then confirm a
/// rejection off that zone on the 1-min chart in the direction of the original break before
/// entering — fixed ~5pt stop, 15-20pt target, stop ALWAYS a fixed distance (never box-relative;
/// confirmed via AskUserQuestion rather than guessed, since the operator's own description named
/// both "other side of the box" and "~5pt stop", which conflict on a wide-range day).
///
/// SECOND-CHANCE REVERSAL (operator's own words): if the original setup's midpoint rejection gets
/// "disrespected" and price instead closes beyond the OPPOSITE side of the box entirely, that's
/// treated as a full invalidation — the strategy follows the reversal directly (same fixed stop/
/// target, no second retest-and-rejection wait required this time; the full-range move already
/// demonstrated the conviction a second retest would otherwise be checking for).
///
/// One trade per day, including the reversal case (taking either one ends the day).
///
/// SESSION LEVELS (the operator's own "other part of my strategy", 2026-10-05) — a second,
/// INDEPENDENT setup running in parallel: marks each of the Asia/London/NY sessions' own high and
/// low (frozen the moment that session ends, same accumulate-and-freeze pattern as
/// oceansStackStrategy's own SessionPoolTracker), and watches each of the six levels until a 5-min
/// bar touches it. A touch consumes that level's "untested" status immediately, whether or not a
/// trade follows (classic liquidity-sweep semantics, confirmed via AskUserQuestion) — each level
/// only ever gets ONE look per cycle. Once touched, the 1-min chart decides which of two reads
/// plays out: a REJECTION right there (fade, enter opposite the approach direction), or a clean
/// BREAK through the level followed by "a little pullback" and a 1-min rejection candle off THAT
/// pullback, confirming a move WITH the breakout instead. Same fixed stop/target as the ORB play
/// (the operator's own words: "the same 5pt stop and 15-20pt tp"). Up to six of these can fire in
/// a day (one per level), entirely independent of the ORB play's own one-trade-per-day cap — the
/// only shared constraint is the ordinary "never more than one position open at once" guard.
///
/// ORDER-PLACEMENT SAFETY — carried over from finchDomScalpStrategy's own hard-won lessons this
/// week rather than re-learned from scratch on a second live strategy: separate stop/target orders
/// (never a bracket), tick-rounded before either reaches the broker (2026-10-05, "this position
/// never put a take profit" — an off-tick-grid price got silently refused by the exchange ~2ms
/// after reporting success), a position this instance didn't itself open gets a sane fallback
/// stop/target from its real fill instead of the zeroed defaults, an async order refusal is
/// self-healed (missing target re-placed, missing stop closes the position immediately — this
/// strategy never knowingly runs with no stop at all), and daily-loss/account-floor risk limits
/// are checked against the REAL broker-reported Account.Balance, persisted in a small local file
/// so they survive a mid-session restart rather than resetting with it.
///
/// RESTART LIMITATION, by design, not yet solved: on <see cref="OnRun"/>, the ORB box itself is
/// always correctly reconstructed from history for today (a simple min/max over today's window
/// bars, order-independent, always safe). But the breakout/retest/rejection SEQUENCE is not
/// retroactively replayed — a restart after the window closes always resumes as "awaiting a
/// breakout" from that moment forward. Given this strategy takes at most one trade a day, check
/// the log after any restart during active hours to see whether today's setup already played out
/// before concluding the strategy missed something.
/// </summary>
/// <summary>Session-level stop placement, operator's own ask 2026-10-06 ("let me control the
/// stop by a setting"). Fixed keeps the already-confirmed fixed-point behavior unchanged by
/// default; BeyondTestedLevel places the stop a small buffer past the level that was actually
/// tested, for the operator to experiment with via the settings panel. Scoped to the
/// session-level plays only — the ORB play's own stop stays always-fixed, a separate decision
/// already confirmed earlier and untouched by this.</summary>
public enum SessionStopMode
{
    Fixed,
    BeyondTestedLevel,
}

public class mesOrbStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    private const string StrategyTag = "MesOrb";
    private const string DailyStateFileName = "mes_orb_daily_risk_state.txt";

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [InputParameter("Account", 0)]
    public Account CurrentAccount { get; set; }

    [InputParameter("Symbol", 1)]
    public Symbol CurrentSymbol { get; set; }

    [InputParameter("Quantity", 2)]
    public int Quantity { get; set; }

    [InputParameter("ORB window: start hour (ET)", 10, 0, 23, 1, 0)]
    public int OrbStartHour { get; set; }

    [InputParameter("ORB window: start minute (ET)", 11, 0, 59, 1, 0)]
    public int OrbStartMinute { get; set; }

    [InputParameter("ORB window: end hour (ET)", 12, 0, 23, 1, 0)]
    public int OrbEndHour { get; set; }

    [InputParameter("ORB window: end minute (ET)", 13, 0, 59, 1, 0)]
    public int OrbEndMinute { get; set; }

    /// <summary>ALWAYS a fixed distance from entry, never box-relative — confirmed via
    /// AskUserQuestion 2026-10-05. The operator's own description named both "stop on the other
    /// side of the box" and "~5pt stop loss", which conflict whenever the box is wider than this;
    /// the operator chose the fixed distance as the tie-breaker.</summary>
    [InputParameter("Stop loss (points, fixed)", 20, 0.25, 100, 0.25, 2)]
    public double StopLossPoints { get; set; }

    [InputParameter("Profit target (points)", 21, 0.25, 200, 0.25, 2)]
    public double ProfitTargetPoints { get; set; }

    /// <summary>How close to the exact midpoint counts as "the zone" — the operator's own words
    /// were "a 50% retest of the midpoint or close to it", so this is a band, not an exact price.
    /// Expressed as a percent of the day's own ORB range so it scales with a wide vs. narrow
    /// range day instead of a fixed point count.</summary>
    [InputParameter("Retest zone tolerance (% of ORB range)", 30, 1, 100, 1, 0)]
    public double RetestZoneTolerancePercent { get; set; }

    [InputParameter("Min ORB range to trade (points, 0 = no floor)", 31, 0, 500, 0.25, 2)]
    public double MinOrbRangePoints { get; set; }

    [InputParameter("Max ORB range to trade (points, 0 = no cap)", 32, 0, 500, 0.25, 2)]
    public double MaxOrbRangePoints { get; set; }

    [InputParameter("Stop watching for a NEW breakout after (hour, ET)", 40, 0, 23, 1, 0)]
    public int SetupCutoffHour { get; set; }

    [InputParameter("Stop watching for a NEW breakout after (minute, ET)", 41, 0, 59, 1, 0)]
    public int SetupCutoffMinute { get; set; }

    /// <summary>FOUND 2026-10-06, from the source TikTok: "marks out the 8-8:15 candle as the
    /// order and then waits till 9:30 when volume comes in and waits for a break." The ORB box
    /// still captures at 8:15 as always — this only gates WHEN breakout detection starts, so the
    /// real NY cash-open volume (not thin pre-market drift) is what has to produce the close
    /// beyond the range.</summary>
    [InputParameter("Start watching for a breakout after (hour, ET)", 43, 0, 23, 1, 0)]
    public int BreakoutWatchStartHour { get; set; }

    [InputParameter("Start watching for a breakout after (minute, ET)", 44, 0, 59, 1, 0)]
    public int BreakoutWatchStartMinute { get; set; }

    /// <summary>The operator's own second-chance case: if the midpoint rejection gets
    /// "disrespected" and price closes beyond the OPPOSITE side of the box, follow that reversal
    /// directly instead of calling the day over.</summary>
    [InputParameter("Allow reversal entry if the box fully invalidates", 42)]
    public bool AllowReversalEntry { get; set; }

    [InputParameter("Max daily loss ($, real account balance, 0 = off)", 50, 0, 1000000, 1, 2)]
    public double MaxDailyLoss { get; set; }

    [InputParameter("Account balance floor ($, sticky, 0 = off)", 51, 0, 1000000, 1, 2)]
    public double AccountBalanceFloor { get; set; }

    [InputParameter("Poll interval (ms)", 60, 50, 5000, 50, 0)]
    public int PollIntervalMs { get; set; }

    // ---- session levels (Asia/London/NY untested high/low) — the operator's own "other part of
    // my strategy" (2026-10-05): independent of the ORB play above, watches each session's own
    // high/low for a touch, then the same rejection-or-break-and-pullback read, same fixed
    // stop/target. See the class doc comment's "SESSION LEVELS" section.

    [InputParameter("Session levels: enabled", 70)]
    public bool SessionLevelsEnabled { get; set; }

    /// <summary>FOUND 2026-10-06, from the TikTok the operator built this setup from: "he goes
    /// to the 15min time frame and marks out all the untested highs and lows for asia london and
    /// ny sessions." This had been riding the ORB's own 5-minute series (sharing bar-drain
    /// infrastructure) — given its own dedicated series instead, matching the source method
    /// exactly and keeping the ORB box itself unaffected (it stays on 5-minute bars, unrelated to
    /// this change).</summary>
    [InputParameter("Session levels: timeframe", 86)]
    public Period SessionLevelPeriod { get; set; }

    [InputParameter("Asia session start hour (ET)", 71, 0, 23, 1, 0)]
    public int AsiaStartHour { get; set; }

    [InputParameter("Asia session start minute (ET)", 72, 0, 59, 1, 0)]
    public int AsiaStartMinute { get; set; }

    [InputParameter("Asia session end hour (ET)", 73, 0, 23, 1, 0)]
    public int AsiaEndHour { get; set; }

    [InputParameter("Asia session end minute (ET)", 74, 0, 59, 1, 0)]
    public int AsiaEndMinute { get; set; }

    [InputParameter("London session start hour (ET)", 75, 0, 23, 1, 0)]
    public int LondonStartHour { get; set; }

    [InputParameter("London session start minute (ET)", 76, 0, 59, 1, 0)]
    public int LondonStartMinute { get; set; }

    [InputParameter("London session end hour (ET)", 77, 0, 23, 1, 0)]
    public int LondonEndHour { get; set; }

    [InputParameter("London session end minute (ET)", 78, 0, 59, 1, 0)]
    public int LondonEndMinute { get; set; }

    [InputParameter("NY session start hour (ET)", 79, 0, 23, 1, 0)]
    public int NySessionStartHour { get; set; }

    [InputParameter("NY session start minute (ET)", 80, 0, 59, 1, 0)]
    public int NySessionStartMinute { get; set; }

    [InputParameter("NY session end hour (ET)", 81, 0, 23, 1, 0)]
    public int NySessionEndHour { get; set; }

    [InputParameter("NY session end minute (ET)", 82, 0, 59, 1, 0)]
    public int NySessionEndMinute { get; set; }

    [InputParameter("Level touch tolerance (points)", 83, 0, 10, 0.25, 2)]
    public double LevelTouchTolerancePoints { get; set; }

    [InputParameter("Min break distance beyond level (points)", 84, 0, 10, 0.25, 2)]
    public double MinBreakDistancePoints { get; set; }

    [InputParameter("Pullback tolerance beyond level (points)", 85, 0, 20, 0.25, 2)]
    public double PullbackTolerancePoints { get; set; }

    // ---- 9 EMA confluence (operator's own ask, 2026-10-06) — see UpdateEma1m/EmaConfirms' own
    // doc comment for the incident this closes.

    [InputParameter("EMA confluence: enabled", 90)]
    public bool EmaConfluenceEnabled { get; set; }

    [InputParameter("EMA confluence: period (1-min bars)", 91, 2, 200, 1, 0)]
    public int EmaPeriod { get; set; }

    // ---- session-level target/stop (operator's own ask, 2026-10-06, from the source TikTok:
    // "waited for a valid rejection and then targeted the opposing high") — session-level plays
    // ONLY; the ORB play's own stop/target are untouched by any of this.

    [InputParameter("Session target: use nearest opposing level", 92)]
    public bool SessionTargetUseOpposingLevel { get; set; }

    [InputParameter("Session stop mode", 93, variants: new object[]
    {
        "Fixed Points", SessionStopMode.Fixed,
        "Beyond Tested Level", SessionStopMode.BeyondTestedLevel,
    })]
    public SessionStopMode SessionStopMode { get; set; }

    [InputParameter("Session stop: buffer beyond tested level (points)", 94, 0, 20, 0.25, 2)]
    public double StopBufferBeyondLevelPoints { get; set; }

    public override string[] MonitoringConnectionsIds => new[] { this.CurrentSymbol?.ConnectionId, this.CurrentAccount?.ConnectionId };

    private enum OrbPhase
    {
        AwaitingWindow,
        BuildingRange,
        AwaitingBreakout,
        AwaitingRetest,
        AwaitingRejection,
        DoneForDay,
    }

    private readonly record struct Ohlc(DateTime OpenUtc, double Open, double High, double Low, double Close);

    private enum LevelPhase
    {
        Idle,
        AwaitingReaction,
        AwaitingPullback,
    }

    /// <summary>One of the six Asia/London/NY high/low levels. A reference type deliberately —
    /// mutated in place by <see cref="UpdateSession"/>/<see cref="CheckLevelTouch"/>/
    /// <see cref="ProcessLevelReaction"/> rather than copied around as a struct.</summary>
    private sealed class SessionLevel
    {
        public SessionLevel(string name, bool isHighLevel)
        {
            this.Name = name;
            this.IsHighLevel = isHighLevel;
        }

        public string Name { get; }
        public bool IsHighLevel { get; }
        public double Price;
        public bool Untested;
        public LevelPhase Phase;
    }

    private Timer? pollTimer;
    private HistoricalData? history5m;
    private HistoricalData? history1m;
    private HistoricalData? history15m;
    private int barsSeen5m;
    private int barsSeen1m;
    private int barsSeen15m;

    private string? orderTypeId;
    private string? stopOrderTypeId;
    private string? limitOrderTypeId;
    private string? resolvedSymbolId;

    private DateTime lastProcessedDate = DateTime.MinValue;
    private OrbPhase phase = OrbPhase.AwaitingWindow;
    private double orbHigh;
    private double orbLow;
    private double orbMidpoint;
    private Side breakoutSide;

    private bool protectiveOrdersPlaced;
    private double pendingStopPrice;
    private double pendingTargetPrice;

    private readonly SessionLevel asiaHigh = new("Asia high", isHighLevel: true);
    private readonly SessionLevel asiaLow = new("Asia low", isHighLevel: false);
    private readonly SessionLevel londonHigh = new("London high", isHighLevel: true);
    private readonly SessionLevel londonLow = new("London low", isHighLevel: false);
    private readonly SessionLevel nyHigh = new("NY high", isHighLevel: true);
    private readonly SessionLevel nyLow = new("NY low", isHighLevel: false);

    private bool wasInAsia;
    private double asiaRunningHigh;
    private double asiaRunningLow;
    private bool wasInLondon;
    private double londonRunningHigh;
    private double londonRunningLow;
    private bool wasInNy;
    private double nyRunningHigh;
    private double nyRunningLow;

    private int balanceDayKey = -1;
    private double dayStartBalance;
    private bool accountFloorBreached;

    public mesOrbStrategy()
        : base()
    {
        this.Name = "mesOrbStrategy";
        this.Description =
            "MES opening-range breakout: marks the 8:00-8:15 ET high/low, waits for a 5-min "
            + "close beyond either side, waits for a ~50% retest of the midpoint, confirms a "
            + "1-min rejection in the break direction, then enters with a fixed stop/target. "
            + "Also follows a full-range reversal if the midpoint rejection fails outright.";

        this.Quantity = 1;
        this.OrbStartHour = 8;
        this.OrbStartMinute = 0;
        this.OrbEndHour = 8;
        this.OrbEndMinute = 15;
        this.StopLossPoints = 5.0;
        this.ProfitTargetPoints = 15.0;
        this.RetestZoneTolerancePercent = 15.0;
        this.MinOrbRangePoints = 3.0;
        this.MaxOrbRangePoints = 40.0;
        this.SetupCutoffHour = 11;
        this.SetupCutoffMinute = 0;
        this.BreakoutWatchStartHour = 9;
        this.BreakoutWatchStartMinute = 30;
        this.AllowReversalEntry = true;
        this.MaxDailyLoss = 0;
        this.AccountBalanceFloor = 0;
        this.PollIntervalMs = 500;

        this.SessionLevelsEnabled = true;
        this.SessionLevelPeriod = Period.MIN15;
        this.AsiaStartHour = 18; this.AsiaStartMinute = 0; this.AsiaEndHour = 3; this.AsiaEndMinute = 0;
        this.LondonStartHour = 3; this.LondonStartMinute = 0; this.LondonEndHour = 11; this.LondonEndMinute = 0;
        this.NySessionStartHour = 8; this.NySessionStartMinute = 0; this.NySessionEndHour = 17; this.NySessionEndMinute = 0;
        this.LevelTouchTolerancePoints = 0.5;
        this.MinBreakDistancePoints = 1.0;
        this.PullbackTolerancePoints = 3.0;

        this.EmaConfluenceEnabled = true;
        this.EmaPeriod = 9;

        this.SessionTargetUseOpposingLevel = true;
        this.SessionStopMode = SessionStopMode.Fixed;
        this.StopBufferBeyondLevelPoints = 1.0;
    }

    // ---- lifecycle ------------------------------------------------------------------------------

    protected override void OnRun()
    {
        // FOUND 2026-10-06 ("why did it not have a orb box drawn... even though i had this
        // turned on before the market opened") — root-caused to a missing re-resolution step,
        // not just the history-population race fixed above. A saved [InputParameter] Symbol/
        // Account can come back in a Fake (serialized placeholder) state on reload/restart —
        // GetHistory() on a Fake symbol returns a handle that NEVER populates, no matter how
        // long you wait (confirmed live: "[ORB] history5m only has 0 bars after a 5s wait" on a
        // restart where the earlier, now-also-fixed race wasn't even the issue). Every other
        // strategy in this codebase (finchDomScalpStrategy included) already carries this exact
        // re-resolution step — missed here only because mesOrbStrategy was built fresh rather
        // than copied from an existing OnRun.
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

        try
        {
            this.history5m = this.CurrentSymbol.GetHistory(Period.MIN5, this.CurrentSymbol.HistoryType, Core.TimeUtils.DateTimeUtcNow.AddDays(-3));
            this.history1m = this.CurrentSymbol.GetHistory(Period.MIN1, this.CurrentSymbol.HistoryType, Core.TimeUtils.DateTimeUtcNow.AddDays(-1));
            this.history15m = this.CurrentSymbol.GetHistory(this.SessionLevelPeriod, this.CurrentSymbol.HistoryType, Core.TimeUtils.DateTimeUtcNow.AddDays(-4));

            // FOUND 2026-10-06 ("why did it not take this short" -> traced to "[ORB] could not
            // reconstruct today's ORB range from history after a restart" firing on EVERY
            // restart, including one taken a full hour after the ORB window closed, with that
            // window's own bars unquestionably real and settled by then). GetHistory() can return
            // a handle that's still populating its own backlog in the background; reading .Count
            // synchronously the instant it returns saw it empty or near-empty on every observed
            // restart. Reconstruction then found nothing, and — because ProcessClosed5mBar's own
            // switch has no DoneForDay case to recover from — locked the ORB phase into
            // DoneForDay for the rest of the day, with no live bar ever able to undo it. Waits
            // here, briefly, for the data to actually arrive before trusting it for anything.
            var deadline = Core.TimeUtils.DateTimeUtcNow.AddSeconds(5);
            while ((this.history5m.Count < 100 || this.history15m.Count < 50) && Core.TimeUtils.DateTimeUtcNow < deadline)
                Thread.Sleep(100);

            if (this.history5m.Count < 100)
                this.Log($"[ORB] history5m only has {this.history5m.Count} bars after a 5s wait — reconstruction may be incomplete.", StrategyLoggingLevel.Error);

            if (this.history15m.Count < 50)
                this.Log($"[Session] history15m only has {this.history15m.Count} bars after a 5s wait — session-level reconstruction may be incomplete.", StrategyLoggingLevel.Error);
        }
        catch (Exception ex)
        {
            this.Log($"Failed to fetch historical data: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
            return;
        }

        this.ReconstructTodayState();
        this.ReconstructSessionLevels();

        // Everything ReconstructTodayState/ReconstructSessionLevels already accounted for is
        // skipped — only bars that close AFTER this point drive the live state machine from here on.
        this.barsSeen5m = Math.Max(0, this.history5m.Count - 1);
        this.barsSeen1m = Math.Max(0, this.history1m.Count - 1);
        this.barsSeen15m = Math.Max(0, this.history15m.Count - 1);

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

        Core.PositionAdded -= this.Core_PositionAdded;
        Core.PositionRemoved -= this.Core_PositionRemoved;
        Core.OrdersHistoryAdded -= this.Core_OrdersHistoryAdded;

        this.history5m?.Dispose();
        this.history1m?.Dispose();
        this.history15m?.Dispose();
        this.history5m = null;
        this.history1m = null;
        this.history15m = null;

        this.Log("Strategy stopped.", StrategyLoggingLevel.Trading);
    }

    private void OnPollTimer(object? state)
    {
        try
        {
            this.RunPoll();
        }
        catch (Exception ex)
        {
            // An exception escaping a Timer callback is unhandled and crashes the whole platform
            // process — same lesson finchDomScalpStrategy already learned. Log and move on.
            this.Log($"[Poll] {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
        }
    }

    private void RunPoll()
    {
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return;

        // Risk limits checked FIRST, before anything else, every poll — same precedence
        // finchDomScalpStrategy uses, so a breach is caught even while flat.
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
        this.Drain5m();
        this.Drain1m();
        this.Drain15m();
    }

    private DateTime lastHeartbeatUtc = DateTime.MinValue;
    private const int HeartbeatIntervalMinutes = 5;

    /// <summary>FOUND 2026-10-06 ("why did it not take this short here") — the operator's own
    /// per-instance log showed literally nothing past the three startup lines for hours, across a
    /// move that should have produced at least "[ORB] range captured" and almost certainly a
    /// "[Signal skipped]"/"[Session]" line too. No exception anywhere (per-instance log OR the
    /// platform's own Serilog) — the poll loop appears to have simply gone silent with zero trace,
    /// which made "is this strategy even alive" unanswerable from the log alone. This guarantees
    /// SOME line appears every few minutes whenever RunPoll is genuinely still executing — if this
    /// line itself goes missing after a restart, that's now unambiguous proof the poll Timer died,
    /// rather than "it's alive but had nothing worth logging."</summary>
    private void MaybeLogHeartbeat()
    {
        var now = Core.TimeUtils.DateTimeUtcNow;
        if (now - this.lastHeartbeatUtc < TimeSpan.FromMinutes(HeartbeatIntervalMinutes)) return;
        this.lastHeartbeatUtc = now;

        var untested = string.Join(", ", this.AllSessionLevels().Where(l => l.Untested).Select(l => l.Name));
        this.Log(
            $"[Heartbeat] still polling. ORB phase={this.phase} high={this.orbHigh:0.##} low={this.orbLow:0.##} "
            + $"mid={this.orbMidpoint:0.##}. Untested session levels: {(untested.Length > 0 ? untested : "none")}.",
            StrategyLoggingLevel.Trading);
    }

    // ---- startup reconstruction ------------------------------------------------------------------

    /// <summary>Reconstructs today's ORB box from history (always safe — a simple min/max over
    /// today's window bars, order-independent) and sets the phase accordingly. Deliberately does
    /// NOT attempt to replay whether a breakout/retest/rejection already completed earlier today —
    /// see the class doc comment's "RESTART LIMITATION" section for why.</summary>
    private void ReconstructTodayState()
    {
        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(Core.TimeUtils.DateTimeUtcNow, SessionZone);
        this.lastProcessedDate = nowEt.Date;

        this.orbHigh = double.MinValue;
        this.orbLow = double.MaxValue;

        if (this.history5m is { } h)
        {
            var closedUpTo = Math.Max(0, h.Count - 1);
            for (var i = 0; i < closedUpTo; i++)
            {
                if (!TryReadBar(h, i, out var bar)) continue;
                var barEt = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);
                if (barEt.Date != nowEt.Date) continue;
                if (!IsAtOrAfter(barEt, this.OrbStartHour, this.OrbStartMinute) || !IsBefore(barEt, this.OrbEndHour, this.OrbEndMinute)) continue;

                if (bar.High > this.orbHigh) this.orbHigh = bar.High;
                if (bar.Low < this.orbLow) this.orbLow = bar.Low;
            }
        }

        if (!IsAtOrAfter(nowEt, this.OrbStartHour, this.OrbStartMinute))
        {
            this.phase = OrbPhase.AwaitingWindow;
            return;
        }

        if (IsBefore(nowEt, this.OrbEndHour, this.OrbEndMinute))
        {
            this.phase = OrbPhase.BuildingRange;
            if (this.orbHigh == double.MinValue)
            {
                this.orbHigh = this.CurrentSymbol!.Last;
                this.orbLow = this.CurrentSymbol!.Last;
            }
            return;
        }

        if (this.orbHigh > double.MinValue && this.orbLow < double.MaxValue)
        {
            this.FinalizeOrbRange();
            if (this.phase == OrbPhase.AwaitingBreakout)
            {
                this.Log(
                    "[ORB] resumed after a restart, mid-day — watching for a FRESH breakout from "
                    + "now forward. Does not retroactively detect a breakout/retest/rejection that "
                    + "may have already completed earlier today; check the log if in doubt.",
                    StrategyLoggingLevel.Trading);
            }
            return;
        }

        this.phase = OrbPhase.DoneForDay;
        this.Log("[ORB] could not reconstruct today's ORB range from history after a restart — skipping today.", StrategyLoggingLevel.Trading);
    }

    /// <summary>Replays the SAME live per-bar method (<see cref="ProcessSessionLevelsBar"/>)
    /// across all of history15m — reconstructs each level's Price/Untested status correctly (a
    /// touch anywhere in the backlog is still a touch) with zero risk of drifting from the live
    /// logic, since it IS the live logic. Deliberately does NOT resume a mid-reaction/pullback
    /// watch afterward — same restart limitation as the ORB's own breakout/retest/rejection
    /// sequence, and for the same reason (a replayed bar would be checked against whatever phase
    /// the state machine landed on at the END of the replay, not the phase that was actually live
    /// when that bar historically closed).</summary>
    private void ReconstructSessionLevels()
    {
        if (!this.SessionLevelsEnabled || this.history15m is not { } h) return;

        var closedUpTo = Math.Max(0, h.Count - 1);
        for (var i = 0; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var bar))
                this.ProcessSessionLevelsBar(bar);
        }

        foreach (var level in this.AllSessionLevels())
            level.Phase = LevelPhase.Idle;

        this.Log("[Session] reconstructed Asia/London/NY levels from history.", StrategyLoggingLevel.Trading);
    }

    // ---- bar draining ---------------------------------------------------------------------------

    private void Drain5m()
    {
        if (this.history5m is not { } h || h.Count <= 1) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.barsSeen5m; i < closedUpTo; i++)
        {
            if (!TryReadBar(h, i, out var bar)) continue;

            this.ProcessClosed5mBar(bar);
        }

        this.barsSeen5m = closedUpTo;
    }

    /// <summary>FOUND 2026-10-06, from the source TikTok: "he goes to the 15min time frame and
    /// marks out all the untested highs and lows for asia london and ny sessions" — session-level
    /// tracking now runs off its OWN dedicated 15-minute series, independent of the ORB's own
    /// 5-minute one.</summary>
    private void Drain15m()
    {
        if (!this.SessionLevelsEnabled || this.history15m is not { } h || h.Count <= 1) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.barsSeen15m; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var bar))
                this.ProcessSessionLevelsBar(bar);
        }

        this.barsSeen15m = closedUpTo;
    }

    /// <summary>Counter always advances regardless of phase (otherwise re-entering
    /// AwaitingRejection later would replay a stale backlog of 1-min bars that have nothing to do
    /// with the live retest zone) — only the ACTION is gated behind the current phase.</summary>
    private void Drain1m()
    {
        if (this.history1m is not { } h || h.Count <= 1) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.barsSeen1m; i < closedUpTo; i++)
        {
            if (!TryReadBar(h, i, out var bar)) continue;

            // Unconditional, every closed 1-min bar, regardless of phase — the EMA has to already
            // be current whenever a rejection signal fires, not computed on demand from that point.
            this.UpdateEma1m(bar.Close);

            if (this.phase == OrbPhase.AwaitingRejection)
                this.ProcessClosed1mBar(bar);

            if (this.SessionLevelsEnabled)
                this.ProcessSessionLevels1mBar(bar);
        }

        this.barsSeen1m = closedUpTo;
    }

    // ---- 9 EMA confluence (operator's own ask, 2026-10-06, after a session-level rejection short
    // got stopped out going into a rally): "there wasnt enough confluence to determine the actual
    // short lets add in the closure below the 9ema as a confluence to show the direction is
    // actually changing." Applied to every "wick-into-the-level, close-back-through" rejection
    // entry (ORB midpoint rejection, session-level reject, session-level pullback-reject) — NOT
    // to the ORB reversal-breakout play, which already requires a full 5-min close beyond the
    // entire opposite side of the box, a materially stronger confirmation on its own.

    private double? ema9;

    private void UpdateEma1m(double close)
    {
        if (this.EmaPeriod <= 0) return;

        var multiplier = 2.0 / (this.EmaPeriod + 1);
        this.ema9 = this.ema9 is { } prev ? ((close - prev) * multiplier) + prev : close;
    }

    /// <summary>True when the EMA confluence isn't required, or the given side's own direction is
    /// actually confirmed by it (a Buy needs the close above the EMA; a Sell needs it below).
    /// Degrades to "not yet confirmed" (false) rather than "skip the check" if no EMA value exists
    /// yet (e.g. right after attach, before enough 1-min bars have closed) — a missing confluence
    /// reading should never be treated as a passing one.</summary>
    private bool EmaConfirms(Side side, double closePrice)
    {
        if (!this.EmaConfluenceEnabled) return true;
        if (this.ema9 is not { } ema) return false;

        return side == Side.Buy ? closePrice > ema : closePrice < ema;
    }

    private static bool TryReadBar(HistoricalData data, int index, out Ohlc bar)
    {
        bar = default;

        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item)
            return false;

        bar = new Ohlc(item.TimeLeft, item.Open, item.High, item.Low, item.Close);
        return true;
    }

    private static int MinutesOfDay(DateTime t) => (t.Hour * 60) + t.Minute;
    private static bool IsAtOrAfter(DateTime t, int hour, int minute) => MinutesOfDay(t) >= (hour * 60) + minute;
    private static bool IsBefore(DateTime t, int hour, int minute) => MinutesOfDay(t) < (hour * 60) + minute;

    /// <summary>Minute-precision, midnight-wraparound-safe window check — ported from
    /// oceansStackStrategy's own helper of the same name. Needed here (unlike the ORB window,
    /// which never crosses midnight) because the Asia session does.</summary>
    private static bool IsInWindow(DateTime local, int startHour, int startMinute, int endHour, int endMinute)
    {
        var startTotal = (startHour * 60) + startMinute;
        var endTotal = (endHour * 60) + endMinute;
        var nowTotal = (local.Hour * 60) + local.Minute;

        if (startTotal == endTotal) return true;
        return startTotal < endTotal
            ? nowTotal >= startTotal && nowTotal < endTotal
            : nowTotal >= startTotal || nowTotal < endTotal;
    }

    // ---- the ORB state machine -------------------------------------------------------------------

    private void CheckNewDay(DateTime barDateEt)
    {
        if (barDateEt == this.lastProcessedDate) return;

        this.lastProcessedDate = barDateEt;
        this.phase = OrbPhase.AwaitingWindow;
        this.orbHigh = double.MinValue;
        this.orbLow = double.MaxValue;
        this.orbMidpoint = 0;

        this.Log($"[ORB] new trading day ({barDateEt:yyyy-MM-dd} ET) — state reset.", StrategyLoggingLevel.Trading);
    }

    private void ProcessClosed5mBar(Ohlc bar)
    {
        var barEt = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);
        this.CheckNewDay(barEt.Date);

        switch (this.phase)
        {
            case OrbPhase.AwaitingWindow:
                if (IsAtOrAfter(barEt, this.OrbStartHour, this.OrbStartMinute) && IsBefore(barEt, this.OrbEndHour, this.OrbEndMinute))
                {
                    this.phase = OrbPhase.BuildingRange;
                    this.orbHigh = bar.High;
                    this.orbLow = bar.Low;
                    this.Log($"[ORB] range building started at {barEt:HH:mm} ET — high={bar.High:0.##} low={bar.Low:0.##}", StrategyLoggingLevel.Trading);
                }
                break;

            case OrbPhase.BuildingRange:
                if (IsBefore(barEt, this.OrbEndHour, this.OrbEndMinute))
                {
                    if (bar.High > this.orbHigh) this.orbHigh = bar.High;
                    if (bar.Low < this.orbLow) this.orbLow = bar.Low;
                }
                else
                {
                    this.FinalizeOrbRange();
                }
                break;

            case OrbPhase.AwaitingBreakout:
                if (IsAtOrAfter(barEt, this.SetupCutoffHour, this.SetupCutoffMinute))
                {
                    this.phase = OrbPhase.DoneForDay;
                    this.Log($"[ORB] no breakout by the {this.SetupCutoffHour:00}:{this.SetupCutoffMinute:00} ET cutoff — done for today.", StrategyLoggingLevel.Trading);
                    break;
                }

                // FOUND 2026-10-06, from the source TikTok: "waits till 9:30 when volume comes
                // in." The box still captured at 8:15 as always; this just holds off EVALUATING
                // a breakout until real NY cash-open volume is behind it, rather than reacting to
                // a thin pre-market drift beyond the range.
                if (!IsAtOrAfter(barEt, this.BreakoutWatchStartHour, this.BreakoutWatchStartMinute))
                    break;

                if (bar.Close > this.orbHigh)
                {
                    this.breakoutSide = Side.Buy;
                    this.phase = OrbPhase.AwaitingRetest;
                    this.Log($"[ORB] bullish breakout confirmed — 5m close {bar.Close:0.##} > ORB high {this.orbHigh:0.##}. Watching for a retest of the midpoint ({this.orbMidpoint:0.##}).", StrategyLoggingLevel.Trading);
                }
                else if (bar.Close < this.orbLow)
                {
                    this.breakoutSide = Side.Sell;
                    this.phase = OrbPhase.AwaitingRetest;
                    this.Log($"[ORB] bearish breakout confirmed — 5m close {bar.Close:0.##} < ORB low {this.orbLow:0.##}. Watching for a retest of the midpoint ({this.orbMidpoint:0.##}).", StrategyLoggingLevel.Trading);
                }
                break;

            case OrbPhase.AwaitingRetest:
            case OrbPhase.AwaitingRejection:
                this.CheckReversalBreakout(bar);
                if (this.phase == OrbPhase.AwaitingRetest)
                    this.CheckRetestTouch(bar);
                break;
        }
    }

    private void ProcessClosed1mBar(Ohlc bar)
    {
        if (this.MyPositions().Length > 0) return;

        var (zoneBottom, zoneTop) = this.RetestZone();

        if (this.breakoutSide == Side.Buy)
        {
            var wickedIn = bar.Low <= zoneTop;
            var closedBackThrough = bar.Close > this.orbMidpoint;
            if (wickedIn && closedBackThrough && this.EmaConfirms(Side.Buy, bar.Close))
            {
                this.Log($"[ORB] 1-min rejection confirmed — wicked to {bar.Low:0.##}, closed back above the midpoint at {bar.Close:0.##} (above the {this.EmaPeriod}-EMA). Entering long.", StrategyLoggingLevel.Trading);
                this.EnterTrade(Side.Buy, bar.Close);
                this.phase = OrbPhase.DoneForDay; // one trade per day, regardless of outcome
                return;
            }

            // FOUND 2026-10-06 ("since the retest already played out it should not enter again
            // if it comes back down and touches it again — that break and retest already played
            // out for today") — ONE retest opportunity per breakout. Once a bar's CLOSE has
            // fully left the (now box-wide) zone without having confirmed a rejection, this
            // attempt is resolved — stop watching rather than stay armed for an unrelated, later
            // touch of the same box.
            if (bar.Close < zoneBottom || bar.Close > zoneTop)
            {
                this.Log($"[ORB] retest attempt resolved without a confirmed rejection (close {bar.Close:0.##} left the zone {zoneBottom:0.##}-{zoneTop:0.##}) — done watching for today.", StrategyLoggingLevel.Trading);
                this.phase = OrbPhase.DoneForDay;
            }
        }
        else
        {
            var wickedIn = bar.High >= zoneBottom;
            var closedBackThrough = bar.Close < this.orbMidpoint;
            if (wickedIn && closedBackThrough && this.EmaConfirms(Side.Sell, bar.Close))
            {
                this.Log($"[ORB] 1-min rejection confirmed — wicked to {bar.High:0.##}, closed back below the midpoint at {bar.Close:0.##} (below the {this.EmaPeriod}-EMA). Entering short.", StrategyLoggingLevel.Trading);
                this.EnterTrade(Side.Sell, bar.Close);
                this.phase = OrbPhase.DoneForDay; // one trade per day, regardless of outcome
                return;
            }

            if (bar.Close < zoneBottom || bar.Close > zoneTop)
            {
                this.Log($"[ORB] retest attempt resolved without a confirmed rejection (close {bar.Close:0.##} left the zone {zoneBottom:0.##}-{zoneTop:0.##}) — done watching for today.", StrategyLoggingLevel.Trading);
                this.phase = OrbPhase.DoneForDay;
            }
        }
    }

    private void FinalizeOrbRange()
    {
        this.orbMidpoint = (this.orbHigh + this.orbLow) / 2.0;
        var range = this.orbHigh - this.orbLow;

        if (this.MinOrbRangePoints > 0 && range < this.MinOrbRangePoints)
        {
            this.phase = OrbPhase.DoneForDay;
            this.Log($"[ORB] range too small ({range:0.##} pts < {this.MinOrbRangePoints} min) — skipping today.", StrategyLoggingLevel.Trading);
            return;
        }

        if (this.MaxOrbRangePoints > 0 && range > this.MaxOrbRangePoints)
        {
            this.phase = OrbPhase.DoneForDay;
            this.Log($"[ORB] range too wide ({range:0.##} pts > {this.MaxOrbRangePoints} max) — skipping today.", StrategyLoggingLevel.Trading);
            return;
        }

        this.phase = OrbPhase.AwaitingBreakout;
        this.Log($"[ORB] range captured: high={this.orbHigh:0.##} low={this.orbLow:0.##} mid={this.orbMidpoint:0.##} range={range:0.##} pts. Watching for a breakout.", StrategyLoggingLevel.Trading);
    }

    /// <summary>FOUND 2026-10-06 ("it played out perfect on the retest of the orb but this was
    /// never actually placed an order for the long... we need to show a retest of the orb not
    /// just at the 50% mark") — a live pullback touched well into the box (not just the tight
    /// midpoint+/-tolerance band this used to return) with a clear 1-min rejection, but
    /// `CheckRetestTouch` never armed because the touch never reached the narrow zone. Widened to
    /// the WHOLE box (orbLow..orbHigh), with `RetestZoneTolerancePercent` repurposed as a small
    /// buffer BEYOND the box's own edges rather than a band around the midpoint — "a retest of
    /// the ORB" now means anywhere in the original range, not specifically its center. The
    /// rejection confirmation itself still references the midpoint as the directional reclaim
    /// line (see ProcessClosed1mBar) — only how FAR price has to come back now follows the
    /// operator's own broader definition.</summary>
    private (double Bottom, double Top) RetestZone()
    {
        var range = this.orbHigh - this.orbLow;
        var tolerance = range * (this.RetestZoneTolerancePercent / 100.0);
        return (this.orbLow - tolerance, this.orbHigh + tolerance);
    }

    private void CheckRetestTouch(Ohlc bar)
    {
        var (zoneBottom, zoneTop) = this.RetestZone();

        var touchedZone = this.breakoutSide == Side.Buy
            ? bar.Low <= zoneTop // pulled back down into/through the zone
            : bar.High >= zoneBottom; // pulled back up into/through the zone

        if (!touchedZone) return;

        this.phase = OrbPhase.AwaitingRejection;
        this.Log($"[ORB] price is in the retest zone ({zoneBottom:0.##}-{zoneTop:0.##}) — watching the 1-min chart for a rejection.", StrategyLoggingLevel.Trading);
    }

    /// <summary>The operator's own second-chance case: the midpoint rejection gets "disrespected"
    /// and price closes beyond the OPPOSITE side of the box entirely — follow that reversal
    /// directly, no second retest-and-rejection wait (the full-range move already showed the
    /// conviction that wait would otherwise be checking for).</summary>
    private void CheckReversalBreakout(Ohlc bar)
    {
        if (!this.AllowReversalEntry) return;
        if (this.MyPositions().Length > 0) return;

        var reversed = this.breakoutSide == Side.Buy
            ? bar.Close < this.orbLow // the "up" play fully invalidated — closed back out the bottom
            : bar.Close > this.orbHigh; // the "down" play fully invalidated — closed back out the top

        if (!reversed) return;

        var reversalSide = this.breakoutSide == Side.Buy ? Side.Sell : Side.Buy;
        this.Log($"[ORB] original {this.breakoutSide} setup fully invalidated — 5m close {bar.Close:0.##} broke the opposite side of the box. Following the reversal {reversalSide}.", StrategyLoggingLevel.Trading);

        this.EnterTrade(reversalSide, bar.Close);
        this.phase = OrbPhase.DoneForDay; // one trade per day, regardless of outcome
    }

    // ---- session levels (Asia/London/NY untested high/low) ----------------------------------------

    private SessionLevel[] AllSessionLevels() => new[]
    {
        this.asiaHigh, this.asiaLow, this.londonHigh, this.londonLow, this.nyHigh, this.nyLow,
    };

    private void ProcessSessionLevelsBar(Ohlc bar)
    {
        var barEt = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);

        this.UpdateSession(
            barEt, bar, ref this.wasInAsia, ref this.asiaRunningHigh, ref this.asiaRunningLow,
            this.AsiaStartHour, this.AsiaStartMinute, this.AsiaEndHour, this.AsiaEndMinute,
            this.asiaHigh, this.asiaLow, "Asia");

        this.UpdateSession(
            barEt, bar, ref this.wasInLondon, ref this.londonRunningHigh, ref this.londonRunningLow,
            this.LondonStartHour, this.LondonStartMinute, this.LondonEndHour, this.LondonEndMinute,
            this.londonHigh, this.londonLow, "London");

        this.UpdateSession(
            barEt, bar, ref this.wasInNy, ref this.nyRunningHigh, ref this.nyRunningLow,
            this.NySessionStartHour, this.NySessionStartMinute, this.NySessionEndHour, this.NySessionEndMinute,
            this.nyHigh, this.nyLow, "NY");

        foreach (var level in this.AllSessionLevels())
            this.CheckLevelTouch(level, bar);
    }

    private void ProcessSessionLevels1mBar(Ohlc bar)
    {
        foreach (var level in this.AllSessionLevels())
            this.ProcessLevelReaction(level, bar);
    }

    /// <summary>FOUND 2026-10-06, from the source TikTok: "waited for a valid rejection and then
    /// targeted the opposing high" — confirmed via AskUserQuestion: the nearest still-UNTESTED
    /// level in the trade's own direction, from ANY session (not specifically the same session's
    /// paired level) — "nearest resting liquidity ahead of price," not a same-session pairing.
    /// Null if none qualify (price already past every marked level on that side, or none have
    /// frozen yet) — the caller falls back to the fixed-point target in that case.</summary>
    private double? FindNearestOpposingLevel(bool wantHigh, double price)
    {
        double? best = null;

        foreach (var level in this.AllSessionLevels())
        {
            if (!level.Untested || level.IsHighLevel != wantHigh) continue;

            if (wantHigh)
            {
                if (level.Price <= price) continue;
                if (best is null || level.Price < best) best = level.Price;
            }
            else
            {
                if (level.Price >= price) continue;
                if (best is null || level.Price > best) best = level.Price;
            }
        }

        return best;
    }

    /// <summary>Session-level stop, per <see cref="mesOrbStrategy.SessionStopMode"/> (operator's
    /// own ask, 2026-10-06: "let me control the stop by a setting") — Fixed (default, unchanged)
    /// or a small buffer beyond the level that was actually tested.</summary>
    private double ComputeSessionStopPrice(Side side, double referencePrice, double levelPrice)
    {
        if (this.SessionStopMode == SessionStopMode.BeyondTestedLevel)
        {
            return side == Side.Buy
                ? levelPrice - this.StopBufferBeyondLevelPoints
                : levelPrice + this.StopBufferBeyondLevelPoints;
        }

        return side == Side.Buy ? referencePrice - this.StopLossPoints : referencePrice + this.StopLossPoints;
    }

    /// <summary>Accumulates one session's running high/low while inside its window (fresh-started
    /// on window ENTRY, same "was I in this window on the previous bar" detection
    /// oceansStackStrategy's own SessionPoolTracker uses for its Asia/overnight pools — needed
    /// because these sessions cross midnight and have no simpler "new day" boundary flag to key
    /// off). Freezes into a fresh, untested level pair the moment the session ends — whatever
    /// happened to the PREVIOUS cycle's level (touched or not) is simply superseded.</summary>
    private void UpdateSession(
        DateTime barEt, Ohlc bar,
        ref bool wasIn, ref double runningHigh, ref double runningLow,
        int startHour, int startMinute, int endHour, int endMinute,
        SessionLevel highLevel, SessionLevel lowLevel, string sessionName)
    {
        var inSession = IsInWindow(barEt, startHour, startMinute, endHour, endMinute);

        if (inSession)
        {
            runningHigh = wasIn ? Math.Max(runningHigh, bar.High) : bar.High;
            runningLow = wasIn ? Math.Min(runningLow, bar.Low) : bar.Low;
        }
        else if (wasIn)
        {
            highLevel.Price = runningHigh;
            highLevel.Untested = true;
            highLevel.Phase = LevelPhase.Idle;

            lowLevel.Price = runningLow;
            lowLevel.Untested = true;
            lowLevel.Phase = LevelPhase.Idle;

            this.Log(
                $"[Session] {sessionName} session ended — high={runningHigh:0.##} low={runningLow:0.##}, "
                + "both untested and live to watch.",
                StrategyLoggingLevel.Trading);
        }

        wasIn = inSession;
    }

    /// <summary>A touch consumes the level's "untested" status immediately, regardless of whether
    /// anything is later traded off it — confirmed via AskUserQuestion, 2026-10-05: classic
    /// liquidity-sweep semantics, the resting liquidity is spent the moment price trades through
    /// it, whether or not THIS strategy acts on that pass.</summary>
    private void CheckLevelTouch(SessionLevel level, Ohlc bar)
    {
        if (!level.Untested || level.Phase != LevelPhase.Idle) return;

        var tolerance = this.LevelTouchTolerancePoints;
        var touched = level.IsHighLevel ? bar.High >= level.Price - tolerance : bar.Low <= level.Price + tolerance;
        if (!touched) return;

        level.Untested = false;
        level.Phase = LevelPhase.AwaitingReaction;

        this.Log(
            $"[Session] {level.Name} ({level.Price:0.##}) touched — watching the 1-min chart for a rejection or a break.",
            StrategyLoggingLevel.Trading);
    }

    /// <summary>The operator's own two-sided read of a touched level, mirroring the ORB's own
    /// rejection-vs-reversal structure: either price rejects right there (fade, enter opposite the
    /// approach direction), or it breaks clean through and — after "a little pullback" — a 1-min
    /// rejection candle off THAT pullback confirms following the breakout instead.</summary>
    private void ProcessLevelReaction(SessionLevel level, Ohlc bar)
    {
        switch (level.Phase)
        {
            case LevelPhase.AwaitingReaction:
                if (level.IsHighLevel)
                {
                    if (bar.High >= level.Price - this.LevelTouchTolerancePoints && bar.Close < level.Price && this.EmaConfirms(Side.Sell, bar.Close))
                    {
                        var stop = this.ComputeSessionStopPrice(Side.Sell, bar.Close, level.Price);
                        var target = this.SessionTargetUseOpposingLevel ? this.FindNearestOpposingLevel(wantHigh: false, bar.Close) : null;
                        this.Log($"[Session] {level.Name} rejection confirmed — wicked to {bar.High:0.##}, closed back below {level.Price:0.##} (below the {this.EmaPeriod}-EMA). Entering short{(target is { } t ? $", targeting opposing level {t:0.##}" : "")}.", StrategyLoggingLevel.Trading);
                        this.EnterTrade(Side.Sell, bar.Close, stop, target);
                        level.Phase = LevelPhase.Idle;
                    }
                    else if (bar.Close > level.Price + this.MinBreakDistancePoints)
                    {
                        level.Phase = LevelPhase.AwaitingPullback;
                        this.Log($"[Session] {level.Name} broken — 1m close {bar.Close:0.##} > {level.Price:0.##}. Watching for a pullback to follow the breakout.", StrategyLoggingLevel.Trading);
                    }
                }
                else
                {
                    if (bar.Low <= level.Price + this.LevelTouchTolerancePoints && bar.Close > level.Price && this.EmaConfirms(Side.Buy, bar.Close))
                    {
                        var stop = this.ComputeSessionStopPrice(Side.Buy, bar.Close, level.Price);
                        var target = this.SessionTargetUseOpposingLevel ? this.FindNearestOpposingLevel(wantHigh: true, bar.Close) : null;
                        this.Log($"[Session] {level.Name} rejection confirmed — wicked to {bar.Low:0.##}, closed back above {level.Price:0.##} (above the {this.EmaPeriod}-EMA). Entering long{(target is { } t ? $", targeting opposing level {t:0.##}" : "")}.", StrategyLoggingLevel.Trading);
                        this.EnterTrade(Side.Buy, bar.Close, stop, target);
                        level.Phase = LevelPhase.Idle;
                    }
                    else if (bar.Close < level.Price - this.MinBreakDistancePoints)
                    {
                        level.Phase = LevelPhase.AwaitingPullback;
                        this.Log($"[Session] {level.Name} broken — 1m close {bar.Close:0.##} < {level.Price:0.##}. Watching for a pullback to follow the breakout.", StrategyLoggingLevel.Trading);
                    }
                }
                break;

            case LevelPhase.AwaitingPullback:
                if (level.IsHighLevel)
                {
                    if (bar.Low <= level.Price + this.PullbackTolerancePoints && bar.Close > level.Price && this.EmaConfirms(Side.Buy, bar.Close))
                    {
                        var stop = this.ComputeSessionStopPrice(Side.Buy, bar.Close, level.Price);
                        var target = this.SessionTargetUseOpposingLevel ? this.FindNearestOpposingLevel(wantHigh: true, bar.Close) : null;
                        this.Log($"[Session] {level.Name} pullback rejection confirmed — wicked to {bar.Low:0.##}, closed back above {level.Price:0.##} (above the {this.EmaPeriod}-EMA). Following the breakout long{(target is { } t ? $", targeting opposing level {t:0.##}" : "")}.", StrategyLoggingLevel.Trading);
                        this.EnterTrade(Side.Buy, bar.Close, stop, target);
                        level.Phase = LevelPhase.Idle;
                    }
                }
                else
                {
                    if (bar.High >= level.Price - this.PullbackTolerancePoints && bar.Close < level.Price && this.EmaConfirms(Side.Sell, bar.Close))
                    {
                        var stop = this.ComputeSessionStopPrice(Side.Sell, bar.Close, level.Price);
                        var target = this.SessionTargetUseOpposingLevel ? this.FindNearestOpposingLevel(wantHigh: false, bar.Close) : null;
                        this.Log($"[Session] {level.Name} pullback rejection confirmed — wicked to {bar.High:0.##}, closed back below {level.Price:0.##} (below the {this.EmaPeriod}-EMA). Following the breakout short{(target is { } t ? $", targeting opposing level {t:0.##}" : "")}.", StrategyLoggingLevel.Trading);
                        this.EnterTrade(Side.Sell, bar.Close, stop, target);
                        level.Phase = LevelPhase.Idle;
                    }
                }
                break;
        }
    }

    // ---- entry -----------------------------------------------------------------------------------

    /// <summary>Shared by both playbooks (ORB and session levels) — does NOT touch
    /// <see cref="phase"/> or any <see cref="SessionLevel"/> itself; each caller marks its OWN
    /// setup consumed right after calling this, regardless of the return value (an attempt that
    /// fails to place still shouldn't retry the same stale signal every bar).</summary>
    /// <summary><paramref name="explicitStopPrice"/>/<paramref name="explicitTargetPrice"/> let a
    /// caller override the default fixed-point stop/target — used by the session-level plays for
    /// the opposing-level target (see FindNearestOpposingLevel) and the optional
    /// beyond-tested-level stop mode (see ComputeSessionStopPrice). The ORB play's own call sites
    /// pass neither, keeping its stop/target exactly as originally confirmed (always fixed).</summary>
    private bool EnterTrade(Side side, double referencePrice, double? explicitStopPrice = null, double? explicitTargetPrice = null)
    {
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return false;
        if (this.MyPositions().Length > 0) return false;
        if (this.accountFloorBreached) return false;

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0) return false;

        var isLong = side == Side.Buy;

        this.pendingStopPrice = explicitStopPrice ?? (isLong ? referencePrice - this.StopLossPoints : referencePrice + this.StopLossPoints);
        this.pendingTargetPrice = explicitTargetPrice ?? (isLong ? referencePrice + this.ProfitTargetPoints : referencePrice - this.ProfitTargetPoints);

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
            this.Log($"[Order] entry failed: {result.Message}", StrategyLoggingLevel.Error);
            return false;
        }

        this.Log($"[Signal] {side} entry placed (stop={this.pendingStopPrice:0.##} target={this.pendingTargetPrice:0.##}, reference={referencePrice:0.##}).", StrategyLoggingLevel.Trading);
        return true;
    }

    // ---- protective orders — ported from finchDomScalpStrategy's own hardened version ------------

    private void Core_PositionAdded(Position obj)
    {
        if (obj is null || !this.IsMyPosition(obj.Symbol, obj.Account, obj.Comment)) return;
        if (this.protectiveOrdersPlaced) return;

        this.protectiveOrdersPlaced = true;

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

        this.protectiveOrdersPlaced = false;

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
            this.RepairMissingProtectiveOrders();
    }

    private void PlaceProtectiveOrders(Position position)
    {
        if (this.CurrentAccount is null || this.CurrentSymbol is null) return;

        var isLong = position.Side == Side.Buy;
        var closingSide = isLong ? Side.Sell : Side.Buy;
        var tickSize = this.CurrentSymbol.TickSize;

        // An ADOPTED position (this instance never itself signaled it — e.g. after a restart)
        // leaves pendingStopPrice/pendingTargetPrice at their OnRun-reset default of 0. Fall back
        // to a sane stop/target from the real fill rather than place a protective order at price
        // zero — same fix finchDomScalpStrategy already proved out live.
        if (this.pendingStopPrice == 0 && this.pendingTargetPrice == 0 && tickSize > 0)
        {
            this.pendingStopPrice = isLong ? position.OpenPrice - this.StopLossPoints : position.OpenPrice + this.StopLossPoints;
            this.pendingTargetPrice = isLong ? position.OpenPrice + this.ProfitTargetPoints : position.OpenPrice - this.ProfitTargetPoints;
            this.Log(
                "[Risk] ADOPTED a position this instance didn't itself open — no real signal data "
                + "to protect it with, so placed a fallback stop/target from its real fill instead.",
                StrategyLoggingLevel.Trading);
        }

        var stopPrice = this.pendingStopPrice;
        var targetPrice = this.pendingTargetPrice;

        // Tick-rounded before either price reaches the broker — see the class doc comment's
        // "ORDER-PLACEMENT SAFETY" section for the live incident this closes.
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
            // A trade must never run with no stop at all — close immediately rather than leave it
            // unprotected, same philosophy finchDomScalpStrategy already established.
            this.Log(
                $"[Order] protective stop failed: {stopResult.Message}. Closing position "
                + "immediately — a trade must never run with no stop at all.",
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
            $"[Order] protective stop={stopPrice:0.##} target={targetPrice:0.##} "
            + $"(fill={position.OpenPrice:0.##}) placed as separate orders.",
            StrategyLoggingLevel.Trading);
    }

    /// <summary>Reacts to ANY refusal on our account by checking what's ACTUALLY resting right now
    /// and repairing whichever leg is missing, rather than trusting a one-time placement result —
    /// a broker can accept a protective order and refuse it asynchronously a couple ms later (see
    /// the class doc comment). Self-correcting by design: if both legs are genuinely resting, this
    /// does nothing.</summary>
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
            var fallbackTarget = isLong ? position.OpenPrice + this.ProfitTargetPoints : position.OpenPrice - this.ProfitTargetPoints;
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

    // ---- position/order identification — ported verbatim from finchDomScalpStrategy -------------

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

    // ---- account-balance-based risk, restart-immune — ported from finchDomScalpStrategy ----------

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
