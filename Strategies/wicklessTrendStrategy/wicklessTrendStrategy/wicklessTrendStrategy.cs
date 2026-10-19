using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;
using SwingLevels;

namespace wicklessTrendStrategy;

/// <summary>
/// Auto-trades the operator's own manual setup (2026-10-16): "waiting for a wickless candle to
/// form that aligns with the current trend so if a trend is bearish and a bearish candle forms
/// with a wickless top and the next 1min candle doesnt fill the wickless candle like close above
/// the wickless end it enters a short and puts a stop at the top of that current move and targets
/// either a session level that hasnt been tested or a bullish wickless candle that hasnt been
/// tested yet." Scoped via AskUserQuestion before writing any order-placing code (all four answers
/// were the recommended option):
///
/// TREND — higher-timeframe swing structure (lower highs + lower lows = bearish, higher highs +
/// higher lows = bullish), reusing `SwingLevelEngine` from the `SwingLevels` indicator rather than
/// a moving-average slope.
///
/// WICKLESS CANDLE + CONFIRMATION — both on the 1-minute series. A candle is "wickless" using the
/// exact same tolerance-based definition already proven in `srmAutoStrategy`/`WicklessLevelEngine`
/// (body-extreme within `WicklessTolerancePoints` of the bar's own high/low). A qualifying candle
/// (trend-aligned colour + wickless on the trend-facing side) arms a ONE-SHOT pending signal; the
/// VERY NEXT 1-minute candle's own CLOSE decides it — stays on the correct side (doesn't "fill" the
/// wickless extreme) = enter, closes back through it = the signal is simply dropped, no entry.
///
/// STOP — the nearest confirmed swing point AGAINST the trade (the "top of that current move" for
/// a short, the "bottom" for a long), from the SAME swing engine as the trend read, plus a small
/// buffer. Capped at `MaxStopDistanceAtrMult` × ATR — this is the exact failure mode the operator's
/// own `finchDomScalpStrategy` NY-open blackout request was about (a swing-based stop 68+ points
/// away right at the open): a pure swing lookup with no ceiling can hand back an enormous stop
/// whenever the nearest countertrend swing happens to be old/far away, so this applies the same
/// "fraction is a suggestion within a ceiling, not an override" discipline `srmAutoStrategy` already
/// uses for its own structural stops.
///
/// TARGET — nearest of (a) the nearest untested session high/low (`SessionLevelEngine`, same
/// Asia/London/NY AM/NY PM windows as the `SwingLevels` indicator) or (b) the nearest untested
/// OPPOSITE-type wickless level (`WicklessLevelEngine` — a short targets an untested BULLISH
/// wickless-bottom candle's own low, a long targets an untested BEARISH wickless-top candle's own
/// high), whichever is closer — same "nearest of two candidates, capped" convention `srmAutoStrategy`
/// already uses for its own structural targets. Falls back to a simple R-multiple of the stop
/// distance only when neither candidate qualifies.
///
/// RISK MANAGEMENT — operator chose the full scaffold (not a leaner version): account-balance-based
/// daily-loss/floor limits persisted to a local state file (restart-immune), a minimum reward:risk
/// gate checked before any order is placed, an active trailing stop + thesis-invalidation review
/// every poll tick, and a post-exit cooldown — all ported near-verbatim from `srmAutoStrategy`'s own
/// proven implementation of each, substituting an ATR-based distance measure for that strategy's
/// implied-move (`emDay`) one, since this strategy has no volatility/options dependency at all.
///
/// THREE ENGINES, THREE INDEPENDENT BAR STREAMS, reused via `&lt;Compile Include&gt;` from the
/// `SwingLevels` indicator's own folder (see this project's own .csproj for the reach-direction
/// reasoning): `SwingLevelEngine` off a higher timeframe (1-hour default) for trend + stop,
/// `SessionLevelEngine` off a 5-minute (default) stream for targets, `WicklessLevelEngine` off the
/// SAME 1-minute stream the entry signal itself watches (one feed, two uses: entry-signal detection
/// is a small inline check in THIS file rather than threading candle colour through the shared
/// engine, which only ever needed wick absence — see `IsWicklessTop`/`IsWicklessBottom` below).
///
/// ORDER-PLACEMENT SAFETY carried over verbatim from `srmAutoStrategy`'s own hard-won lessons:
/// separate stop/target orders (never a bracket), tick-rounded before either reaches the broker, an
/// adopted position gets a sane fallback stop/target from its real fill, an async order refusal is
/// self-healed, one position at a time.
/// </summary>
public class wicklessTrendStrategy : Strategy, ICurrentAccount, ICurrentSymbol
{
    private const string StrategyTag = "WicklessTrend";
    private const string DailyStateFileName = "wickless_trend_daily_risk_state.txt";

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [InputParameter("Account", 0)]
    public Account CurrentAccount { get; set; }

    [InputParameter("Symbol", 1)]
    public Symbol CurrentSymbol { get; set; }

    [InputParameter("Quantity", 2)]
    public int Quantity { get; set; }

    // ---- trend / swing structure ---------------------------------------------------------------

    [InputParameter("Trend: swing timeframe", 10)]
    public Period TrendTimeframe { get; set; }

    [InputParameter("Trend: history lookback (days)", 11, 5, 365, 1, 0)]
    public int TrendLookbackDays { get; set; }

    [InputParameter("Trend: pivot lookback (bars each side)", 12, 1, 20, 1, 0)]
    public int PivotLookback { get; set; }

    [InputParameter("Trend: max swing levels kept", 13, 5, 200, 1, 0)]
    public int MaxSwingLevelsKept { get; set; }

    // ---- wickless candle (1-minute) ------------------------------------------------------------

    [InputParameter("Wickless: 1-minute history lookback (days)", 20, 1, 30, 1, 0)]
    public int WicklessLookbackDays { get; set; }

    [InputParameter("Wickless: tolerance (points, 0 = no wick at all)", 21, 0, 10, 0.1, 2)]
    public double WicklessTolerancePoints { get; set; }

    [InputParameter("Wickless: max levels kept (for targeting)", 22, 5, 300, 1, 0)]
    public int MaxWicklessLevelsKept { get; set; }

    // ---- session levels (targets) --------------------------------------------------------------

    [InputParameter("Session levels: bar resolution", 30)]
    public Period SessionBarResolution { get; set; }

    [InputParameter("Session levels: history lookback (days)", 31, 1, 30, 1, 0)]
    public int SessionLookbackDays { get; set; }

    [InputParameter("Session levels: max kept", 32, 4, 200, 1, 0)]
    public int MaxSessionLevelsKept { get; set; }

    [InputParameter("Asia session: enabled", 40)]
    public bool EnableAsiaSession { get; set; }

    [InputParameter("Asia session: start hour (ET)", 41, 0, 23, 1, 0)]
    public int AsiaStartHour { get; set; }

    [InputParameter("Asia session: start minute (ET)", 42, 0, 59, 1, 0)]
    public int AsiaStartMinute { get; set; }

    [InputParameter("Asia session: end hour (ET)", 43, 0, 23, 1, 0)]
    public int AsiaEndHour { get; set; }

    [InputParameter("Asia session: end minute (ET)", 44, 0, 59, 1, 0)]
    public int AsiaEndMinute { get; set; }

    [InputParameter("London session: enabled", 50)]
    public bool EnableLondonSession { get; set; }

    [InputParameter("London session: start hour (ET)", 51, 0, 23, 1, 0)]
    public int LondonStartHour { get; set; }

    [InputParameter("London session: start minute (ET)", 52, 0, 59, 1, 0)]
    public int LondonStartMinute { get; set; }

    [InputParameter("London session: end hour (ET)", 53, 0, 23, 1, 0)]
    public int LondonEndHour { get; set; }

    [InputParameter("London session: end minute (ET)", 54, 0, 59, 1, 0)]
    public int LondonEndMinute { get; set; }

    [InputParameter("NY AM session: enabled", 60)]
    public bool EnableNyAmSession { get; set; }

    [InputParameter("NY AM session: start hour (ET)", 61, 0, 23, 1, 0)]
    public int NyAmStartHour { get; set; }

    [InputParameter("NY AM session: start minute (ET)", 62, 0, 59, 1, 0)]
    public int NyAmStartMinute { get; set; }

    [InputParameter("NY AM session: end hour (ET)", 63, 0, 23, 1, 0)]
    public int NyAmEndHour { get; set; }

    [InputParameter("NY AM session: end minute (ET)", 64, 0, 59, 1, 0)]
    public int NyAmEndMinute { get; set; }

    [InputParameter("NY PM session: enabled", 70)]
    public bool EnableNyPmSession { get; set; }

    [InputParameter("NY PM session: start hour (ET)", 71, 0, 23, 1, 0)]
    public int NyPmStartHour { get; set; }

    [InputParameter("NY PM session: start minute (ET)", 72, 0, 59, 1, 0)]
    public int NyPmStartMinute { get; set; }

    [InputParameter("NY PM session: end hour (ET)", 73, 0, 23, 1, 0)]
    public int NyPmEndHour { get; set; }

    [InputParameter("NY PM session: end minute (ET)", 74, 0, 59, 1, 0)]
    public int NyPmEndMinute { get; set; }

    // ---- stop / target resolution ------------------------------------------------------------

    [InputParameter("Stop/target: ATR period (bars, on the trend timeframe)", 80, 2, 100, 1, 0)]
    public int AtrPeriod { get; set; }

    [InputParameter("Stop: buffer beyond the countertrend swing point (points)", 81, 0, 50, 0.25, 2)]
    public double StructureStopBufferPoints { get; set; }

    [InputParameter("Stop: maximum distance (x ATR) — the \"68pt swing stop\" guard", 82, 0.5, 10, 0.1, 2)]
    public double MaxStopDistanceAtrMult { get; set; }

    [InputParameter("Target: minimum distance (points, avoids a too-close target)", 83, 0, 100, 0.5, 2)]
    public double MinTargetDistancePoints { get; set; }

    [InputParameter("Target: maximum distance (x ATR, avoids a too-far target)", 84, 0.5, 20, 0.1, 2)]
    public double MaxTargetDistanceAtrMult { get; set; }

    [InputParameter("Target: fallback reward:risk multiple (used only if no level/wickless target qualifies)", 85, 0.5, 10, 0.1, 2)]
    public double TargetFallbackRMultiple { get; set; }

    // ---- risk ------------------------------------------------------------------------------

    [InputParameter("Max daily loss ($, 0 = off)", 90, 0, 1000000, 1, 2)]
    public double MaxDailyLoss { get; set; }

    [InputParameter("Account balance floor ($, sticky, 0 = off)", 91, 0, 1000000, 1, 2)]
    public double AccountBalanceFloor { get; set; }

    [InputParameter("Minimum reward:risk ratio (0 = off)", 92, 0, 5, 0.1, 2)]
    public double MinRewardToRiskRatio { get; set; }

    [InputParameter("Poll interval (ms)", 93, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; }

    // ---- active in-trade management (same discipline as srmAutoStrategy) -----------------------

    [InputParameter("Trailing stop: enabled", 100)]
    public bool UseTrailingStop { get; set; }

    [InputParameter("Trailing stop: activate after this much profit (points)", 101, 0, 200, 0.5, 2)]
    public double TrailingActivationPoints { get; set; }

    [InputParameter("Trailing stop: distance behind the best price reached (points)", 102, 0, 100, 0.5, 2)]
    public double TrailingBufferPoints { get; set; }

    [InputParameter("Trailing stop: minimum re-trail step (points)", 103, 0, 20, 0.25, 2)]
    public double TrailingMinStepPoints { get; set; }

    [InputParameter("Thesis invalidation: enabled", 110)]
    public bool UseThesisInvalidation { get; set; }

    [InputParameter("Thesis invalidation: minimum favorable excursion before it applies (points)", 111, 0, 200, 0.5, 2)]
    public double ThesisInvalidationMinFavorablePoints { get; set; }

    [InputParameter("Thesis invalidation: give-back from best price that triggers a review (points)", 112, 0, 100, 0.5, 2)]
    public double ThesisInvalidationGiveBackPoints { get; set; }

    [InputParameter("Cooldown after any exit (minutes, 0 = off)", 115, 0, 120, 0.5, 2)]
    public double CooldownAfterExitMinutes { get; set; }

    public wicklessTrendStrategy()
    {
        this.Name = "Wickless Trend";
        this.Description =
            "Trend-aligned wickless-candle reversal entries: a wickless candle against the "
            + "prevailing higher-timeframe swing trend, confirmed by the next 1-minute candle not "
            + "filling it. Stop at the nearest countertrend swing point, target the nearest untested "
            + "session level or opposite-type wickless candle.";

        this.Quantity = 1;

        this.TrendTimeframe = Period.HOUR1;
        this.TrendLookbackDays = 20;
        this.PivotLookback = 3;
        this.MaxSwingLevelsKept = 30;

        this.WicklessLookbackDays = 5;
        this.WicklessTolerancePoints = 0.25;
        this.MaxWicklessLevelsKept = 40;

        this.SessionBarResolution = Period.MIN5;
        this.SessionLookbackDays = 5;
        this.MaxSessionLevelsKept = 24;

        this.EnableAsiaSession = true;
        this.AsiaStartHour = 18; this.AsiaStartMinute = 0; this.AsiaEndHour = 0; this.AsiaEndMinute = 0;

        this.EnableLondonSession = true;
        this.LondonStartHour = 0; this.LondonStartMinute = 0; this.LondonEndHour = 6; this.LondonEndMinute = 0;

        this.EnableNyAmSession = true;
        this.NyAmStartHour = 6; this.NyAmStartMinute = 0; this.NyAmEndHour = 12; this.NyAmEndMinute = 0;

        this.EnableNyPmSession = true;
        this.NyPmStartHour = 12; this.NyPmStartMinute = 0; this.NyPmEndHour = 18; this.NyPmEndMinute = 0;

        this.AtrPeriod = 14;
        this.StructureStopBufferPoints = 2.0;
        this.MaxStopDistanceAtrMult = 3.0;
        this.MinTargetDistancePoints = 5.0;
        this.MaxTargetDistanceAtrMult = 8.0;
        this.TargetFallbackRMultiple = 2.0;

        this.MaxDailyLoss = 0;
        this.AccountBalanceFloor = 0;
        this.MinRewardToRiskRatio = 1.0;
        this.PollIntervalMs = 1000;

        this.UseTrailingStop = true;
        this.TrailingActivationPoints = 15.0;
        this.TrailingBufferPoints = 10.0;
        this.TrailingMinStepPoints = 2.0;

        this.UseThesisInvalidation = true;
        this.ThesisInvalidationMinFavorablePoints = 12.0;
        this.ThesisInvalidationGiveBackPoints = 8.0;

        this.CooldownAfterExitMinutes = 5.0;
    }

    // ---- state -----------------------------------------------------------------------------

    private enum TrendDirection { None, Bullish, Bearish }

    private readonly record struct PendingWicklessSignal(bool IsShort, double WicklessExtreme, DateTime FormedUtc);

    private SwingLevelEngine? swingEngine;
    private SessionLevelEngine? sessionEngine;
    private WicklessLevelEngine? wicklessEngine;

    private Timer? pollTimer;

    private HistoricalData? minuteHistory;
    private HistoricalData? trendHistory;
    private HistoricalData? sessionHistory;
    private int minuteBarsSeen = -1;
    private int trendBarsSeen = -1;
    private int sessionBarsSeen = -1;

    private readonly System.Collections.Generic.List<(double High, double Low, double Close)> trendAtrBuffer = new();
    private HistoryItemBar? lastMinuteBar;
    private PendingWicklessSignal? pendingSignal;

    private string? orderTypeId;
    private string? stopOrderTypeId;
    private string? limitOrderTypeId;
    private string? resolvedSymbolId;
    private bool protectiveOrdersPlaced;
    private double pendingStopPrice;
    private double pendingTargetPrice;

    private bool inTradeManaged;
    private Side openSide;
    private double openEntryPrice;
    private double openBestPrice;
    private double currentStopPrice;
    private DateTime lastExitUtc = DateTime.MinValue;

    private int balanceDayKey = -1;
    private double dayStartBalance;
    private bool accountFloorBreached;

    private DateTime lastHeartbeatUtc = DateTime.MinValue;
    private const int HeartbeatIntervalMinutes = 5;

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
        this.inTradeManaged = false;
        this.openEntryPrice = 0;
        this.openBestPrice = 0;
        this.currentStopPrice = 0;
        this.lastExitUtc = DateTime.MinValue;
        this.pendingSignal = null;
        this.lastMinuteBar = null;
        this.trendAtrBuffer.Clear();

        this.swingEngine = new SwingLevelEngine(this.PivotLookback, this.MaxSwingLevelsKept);
        this.sessionEngine = new SessionLevelEngine(this.MaxSessionLevelsKept);
        this.wicklessEngine = new WicklessLevelEngine(this.WicklessTolerancePoints, this.MaxWicklessLevelsKept);

        try
        {
            var trendLookback = Core.TimeUtils.DateTimeUtcNow.AddDays(-Math.Max(5, this.TrendLookbackDays));
            this.trendHistory = this.CurrentSymbol.GetHistory(this.TrendTimeframe, this.CurrentSymbol.HistoryType, trendLookback);

            var sessionLookback = Core.TimeUtils.DateTimeUtcNow.AddDays(-Math.Max(1, this.SessionLookbackDays));
            this.sessionHistory = this.CurrentSymbol.GetHistory(this.SessionBarResolution, this.CurrentSymbol.HistoryType, sessionLookback);

            var minuteLookback = Core.TimeUtils.DateTimeUtcNow.AddDays(-Math.Max(1, this.WicklessLookbackDays));
            this.minuteHistory = this.CurrentSymbol.GetHistory(Period.MIN1, this.CurrentSymbol.HistoryType, minuteLookback);

            var deadline = Core.TimeUtils.DateTimeUtcNow.AddSeconds(5);
            while (this.minuteHistory.Count < 100 && Core.TimeUtils.DateTimeUtcNow < deadline)
                Thread.Sleep(100);

            if (this.minuteHistory.Count < 100)
                this.Log($"[History] 1-minute history only has {this.minuteHistory.Count} bars after a 5s wait.", StrategyLoggingLevel.Error);
        }
        catch (Exception ex)
        {
            this.Log($"Failed to fetch historical data: {ex.GetType().Name}: {ex.Message}", StrategyLoggingLevel.Error);
            return;
        }

        // Replay all three histories to reconstruct swing/session/wickless state before going
        // live — same "feed the engines, don't evaluate signals" discipline as srmAutoStrategy's
        // own startup replay.
        var trendClosedUpTo = Math.Max(0, this.trendHistory.Count - 1);
        for (var i = 0; i < trendClosedUpTo; i++)
        {
            if (this.trendHistory[i, SeekOriginHistory.Begin] is HistoryItemBar item)
                this.FeedTrendBar(item);
        }
        this.trendBarsSeen = trendClosedUpTo;

        var sessionClosedUpTo = Math.Max(0, this.sessionHistory.Count - 1);
        for (var i = 0; i < sessionClosedUpTo; i++)
        {
            if (this.sessionHistory[i, SeekOriginHistory.Begin] is HistoryItemBar item)
                this.FeedSessionBar(item);
        }
        this.sessionBarsSeen = sessionClosedUpTo;

        var minuteClosedUpTo = Math.Max(0, this.minuteHistory.Count - 1);
        for (var i = 0; i < minuteClosedUpTo; i++)
        {
            if (this.minuteHistory[i, SeekOriginHistory.Begin] is HistoryItemBar item)
            {
                this.wicklessEngine.FeedBar(item.TimeLeft, item.Open, item.High, item.Low, item.Close);
                this.lastMinuteBar = item;
            }
        }
        this.minuteBarsSeen = minuteClosedUpTo;

        this.Log(
            $"[Replay] trend bars={trendClosedUpTo}, session bars={sessionClosedUpTo}, minute bars={minuteClosedUpTo} "
            + "replayed before going live.",
            StrategyLoggingLevel.Trading);

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

        this.minuteHistory?.Dispose();
        this.trendHistory?.Dispose();
        this.sessionHistory?.Dispose();
        this.minuteHistory = null;
        this.trendHistory = null;
        this.sessionHistory = null;

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
        this.ManagePosition();
        this.DrainTrendBars();
        this.DrainSessionBars();
        this.DrainMinuteBars();
    }

    private void DrainTrendBars()
    {
        if (this.trendHistory is not { Count: > 1 } h) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.trendBarsSeen; i < closedUpTo; i++)
        {
            if (h[i, SeekOriginHistory.Begin] is HistoryItemBar item)
                this.FeedTrendBar(item);
        }
        this.trendBarsSeen = closedUpTo;
    }

    private void FeedTrendBar(HistoryItemBar item)
    {
        this.swingEngine?.FeedBar(item.TimeLeft, item.High, item.Low);

        this.trendAtrBuffer.Add((item.High, item.Low, item.Close));
        var keep = Math.Max(2, this.AtrPeriod + 5);
        while (this.trendAtrBuffer.Count > keep)
            this.trendAtrBuffer.RemoveAt(0);
    }

    private void DrainSessionBars()
    {
        if (this.sessionHistory is not { Count: > 1 } h) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.sessionBarsSeen; i < closedUpTo; i++)
        {
            if (h[i, SeekOriginHistory.Begin] is HistoryItemBar item)
                this.FeedSessionBar(item);
        }
        this.sessionBarsSeen = closedUpTo;
    }

    private void FeedSessionBar(HistoryItemBar item)
    {
        if (this.sessionEngine is not { } eng) return;

        var et = TimeZoneInfo.ConvertTimeFromUtc(item.TimeLeft, SessionZone);
        eng.FeedBar(item.TimeLeft, et, item.High, item.Low, this.BuildSessionDefs());
    }

    private System.Collections.Generic.List<SessionDef> BuildSessionDefs() => new()
    {
        new SessionDef
        {
            Name = "Asia", Enabled = this.EnableAsiaSession,
            StartHour = this.AsiaStartHour, StartMinute = this.AsiaStartMinute,
            EndHour = this.AsiaEndHour, EndMinute = this.AsiaEndMinute,
        },
        new SessionDef
        {
            Name = "London", Enabled = this.EnableLondonSession,
            StartHour = this.LondonStartHour, StartMinute = this.LondonStartMinute,
            EndHour = this.LondonEndHour, EndMinute = this.LondonEndMinute,
        },
        new SessionDef
        {
            Name = "NY AM", Enabled = this.EnableNyAmSession,
            StartHour = this.NyAmStartHour, StartMinute = this.NyAmStartMinute,
            EndHour = this.NyAmEndHour, EndMinute = this.NyAmEndMinute,
        },
        new SessionDef
        {
            Name = "NY PM", Enabled = this.EnableNyPmSession,
            StartHour = this.NyPmStartHour, StartMinute = this.NyPmStartMinute,
            EndHour = this.NyPmEndHour, EndMinute = this.NyPmEndMinute,
        },
    };

    private void DrainMinuteBars()
    {
        if (this.minuteHistory is not { Count: > 1 } h) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.minuteBarsSeen; i < closedUpTo; i++)
        {
            if (h[i, SeekOriginHistory.Begin] is HistoryItemBar item)
                this.ProcessClosedMinuteBar(item);
        }
        this.minuteBarsSeen = closedUpTo;
    }

    // ---- signal detection (1-minute) ----------------------------------------------------------

    private bool IsWicklessTop(HistoryItemBar bar) => bar.High - Math.Max(bar.Open, bar.Close) <= this.WicklessTolerancePoints;

    private bool IsWicklessBottom(HistoryItemBar bar) => Math.Min(bar.Open, bar.Close) - bar.Low <= this.WicklessTolerancePoints;

    private static bool IsBearishCandle(HistoryItemBar bar) => bar.Close < bar.Open;

    private static bool IsBullishCandle(HistoryItemBar bar) => bar.Close > bar.Open;

    private TrendDirection ComputeTrend()
    {
        if (this.swingEngine is not { } eng) return TrendDirection.None;

        var highs = eng.Levels.Where(l => l.IsHigh).OrderBy(l => l.ConfirmedUtc).ToList();
        var lows = eng.Levels.Where(l => !l.IsHigh).OrderBy(l => l.ConfirmedUtc).ToList();
        if (highs.Count < 2 || lows.Count < 2) return TrendDirection.None;

        var lastHigh = highs[^1]; var prevHigh = highs[^2];
        var lastLow = lows[^1]; var prevLow = lows[^2];

        if (lastHigh.Price < prevHigh.Price && lastLow.Price < prevLow.Price) return TrendDirection.Bearish;
        if (lastHigh.Price > prevHigh.Price && lastLow.Price > prevLow.Price) return TrendDirection.Bullish;
        return TrendDirection.None;
    }

    /// <summary>Feeds `WicklessLevelEngine` every bar regardless (it needs to keep seeing touches
    /// to mark its own tracked levels tested, whether or not this strategy is in a position), then
    /// — only while flat, not floor-breached, and past cooldown — first resolves any pending signal
    /// from the PREVIOUS bar (one-shot, consumed either way), then checks whether THIS bar itself
    /// arms a new one.</summary>
    private void ProcessClosedMinuteBar(HistoryItemBar bar)
    {
        this.wicklessEngine?.FeedBar(bar.TimeLeft, bar.Open, bar.High, bar.Low, bar.Close);
        this.lastMinuteBar = bar;

        if (this.CurrentSymbol is null || this.CurrentAccount is null) return;
        if (this.MyPositions().Length > 0 || this.accountFloorBreached) return;
        if (this.CooldownAfterExitMinutes > 0 && (Core.TimeUtils.DateTimeUtcNow - this.lastExitUtc).TotalMinutes < this.CooldownAfterExitMinutes)
            return;

        if (this.pendingSignal is { } pending)
        {
            this.pendingSignal = null;

            if (pending.IsShort && bar.Close <= pending.WicklessExtreme)
            {
                this.TryEnterWicklessTrend(Side.Sell, bar.Close, $"bearish wickless top {pending.WicklessExtreme:0.##} confirmed, no fill");
            }
            else if (!pending.IsShort && bar.Close >= pending.WicklessExtreme)
            {
                this.TryEnterWicklessTrend(Side.Buy, bar.Close, $"bullish wickless bottom {pending.WicklessExtreme:0.##} confirmed, no fill");
            }
        }

        if (this.MyPositions().Length > 0) return; // the confirmation above may have just entered

        var trend = this.ComputeTrend();
        if (trend == TrendDirection.Bearish && IsBearishCandle(bar) && this.IsWicklessTop(bar))
        {
            this.pendingSignal = new PendingWicklessSignal(true, bar.High, bar.TimeLeft);
            this.Log($"[Signal] armed — bearish wickless top at {bar.High:0.##}, waiting for next 1min candle to not close above it.", StrategyLoggingLevel.Trading);
        }
        else if (trend == TrendDirection.Bullish && IsBullishCandle(bar) && this.IsWicklessBottom(bar))
        {
            this.pendingSignal = new PendingWicklessSignal(false, bar.Low, bar.TimeLeft);
            this.Log($"[Signal] armed — bullish wickless bottom at {bar.Low:0.##}, waiting for next 1min candle to not close below it.", StrategyLoggingLevel.Trading);
        }
    }

    // ---- stop / target resolution ------------------------------------------------------------

    private double ComputeTrendAtr()
    {
        if (this.trendAtrBuffer.Count < 2) return 0;

        var trs = new System.Collections.Generic.List<double>();
        for (var i = 1; i < this.trendAtrBuffer.Count; i++)
        {
            var (h, l, _) = this.trendAtrBuffer[i];
            var prevClose = this.trendAtrBuffer[i - 1].Close;
            trs.Add(Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose))));
        }

        var take = Math.Min(this.AtrPeriod, trs.Count);
        return trs.Skip(trs.Count - take).Average();
    }

    /// <summary>"Stop at the top of that current move" (short) / "bottom of that current move"
    /// (long) — the most recently CONFIRMED countertrend swing point, plus a small buffer, capped
    /// at <see cref="MaxStopDistanceAtrMult"/> × ATR. See the class doc comment for why that cap
    /// exists — an uncapped swing lookup is exactly the "68pt stop" failure mode that prompted the
    /// NY-open blackout request on a different strategy.</summary>
    private double ResolveStopPrice(bool isLong, double referencePrice, double atr)
    {
        var maxDistance = atr > 0 ? atr * this.MaxStopDistanceAtrMult : double.PositiveInfinity;

        if (this.swingEngine is { } eng)
        {
            var structureLevel = eng.Levels
                .Where(l => isLong ? !l.IsHigh : l.IsHigh)
                .Where(l => isLong ? l.Price < referencePrice : l.Price > referencePrice)
                .OrderByDescending(l => l.ConfirmedUtc)
                .Select(l => (double?)l.Price)
                .FirstOrDefault();

            if (structureLevel is { } level)
            {
                var structureStop = isLong ? level - this.StructureStopBufferPoints : level + this.StructureStopBufferPoints;
                var distance = isLong ? referencePrice - structureStop : structureStop - referencePrice;

                if (distance >= this.StructureStopBufferPoints && distance <= maxDistance)
                    return structureStop;
            }
        }

        var fallbackMult = Math.Min(2.0, this.MaxStopDistanceAtrMult);
        var fallbackDist = atr > 0 ? atr * fallbackMult : 10.0;
        return isLong ? referencePrice - fallbackDist : referencePrice + fallbackDist;
    }

    /// <summary>Nearest untested session high/low in the favorable direction — any session, not
    /// just high or just low, matching the operator's own generic "a session level that hasnt been
    /// tested."</summary>
    private double? NearestUntestedSessionLevel(bool isLong, double referencePrice)
    {
        if (this.sessionEngine is not { } eng) return null;

        var candidates = eng.Levels
            .Where(l => l.TouchedUtc is null)
            .Where(l => isLong ? l.Price > referencePrice : l.Price < referencePrice);

        return isLong
            ? candidates.OrderBy(l => l.Price).Select(l => (double?)l.Price).FirstOrDefault()
            : candidates.OrderByDescending(l => l.Price).Select(l => (double?)l.Price).FirstOrDefault();
    }

    /// <summary>Nearest untested OPPOSITE-type wickless level — a long targets an untested bearish
    /// wickless-top candle's own high (IsHigh=true); a short targets an untested bullish
    /// wickless-bottom candle's own low (IsHigh=false). Matches the operator's own words exactly:
    /// "targets either a session level that hasnt been tested or a bullish wickless candle that
    /// hasnt been tested yet" (said for the short case).</summary>
    private double? NearestUntestedWicklessLevel(bool isLong, double referencePrice)
    {
        if (this.wicklessEngine is not { } eng) return null;

        var candidates = eng.Levels
            .Where(l => l.TouchedUtc is null && l.IsHigh == isLong)
            .Where(l => isLong ? l.Price > referencePrice : l.Price < referencePrice);

        return isLong
            ? candidates.OrderBy(l => l.Price).Select(l => (double?)l.Price).FirstOrDefault()
            : candidates.OrderByDescending(l => l.Price).Select(l => (double?)l.Price).FirstOrDefault();
    }

    private double ResolveTargetPrice(bool isLong, double referencePrice, double stopPrice, double atr)
    {
        var maxDistance = atr > 0 ? atr * this.MaxTargetDistanceAtrMult : double.PositiveInfinity;

        var sessionTarget = this.NearestUntestedSessionLevel(isLong, referencePrice);
        var wicklessTarget = this.NearestUntestedWicklessLevel(isLong, referencePrice);

        var haveSession = sessionTarget is { } st && Math.Abs(st - referencePrice) <= maxDistance;
        var haveWickless = wicklessTarget is { } wt && Math.Abs(wt - referencePrice) <= maxDistance;

        double? chosen = (haveSession, haveWickless) switch
        {
            (true, true) => isLong ? Math.Min(sessionTarget!.Value, wicklessTarget!.Value) : Math.Max(sessionTarget!.Value, wicklessTarget!.Value),
            (true, false) => sessionTarget,
            (false, true) => wicklessTarget,
            _ => null,
        };

        if (chosen is { } t && Math.Abs(t - referencePrice) >= this.MinTargetDistancePoints)
            return t;

        var riskDist = Math.Abs(referencePrice - stopPrice);
        var fallbackDist = riskDist > 0 ? riskDist * this.TargetFallbackRMultiple : (atr > 0 ? atr : 10.0);
        fallbackDist = Math.Min(fallbackDist, maxDistance);
        return isLong ? referencePrice + fallbackDist : referencePrice - fallbackDist;
    }

    // ---- entry / protective orders ---------------------------------------------------------------

    private bool TryEnterWicklessTrend(Side side, double referencePrice, string reason)
    {
        if (this.CurrentSymbol is null || this.CurrentAccount is null) return false;
        if (this.MyPositions().Length > 0) return false;
        if (this.accountFloorBreached) return false;

        var tickSize = this.CurrentSymbol.TickSize;
        if (tickSize <= 0) return false;

        var isLong = side == Side.Buy;
        var atr = this.ComputeTrendAtr();
        var stopPrice = this.ResolveStopPrice(isLong, referencePrice, atr);
        var targetPrice = this.ResolveTargetPrice(isLong, referencePrice, stopPrice, atr);

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
        // to an ATR-based stop/target from the real fill rather than place a protective order at
        // price zero — same fix srmAutoStrategy already proved out (substituting ATR for emDay,
        // since this strategy has no volatility/options dependency at all).
        if (this.pendingStopPrice == 0 && this.pendingTargetPrice == 0 && tickSize > 0)
        {
            var atr = this.ComputeTrendAtr();
            if (atr <= 0) atr = 10.0;
            var fallbackMult = Math.Min(2.0, this.MaxStopDistanceAtrMult);
            var stopDist = atr * fallbackMult;
            var targetDist = stopDist * this.TargetFallbackRMultiple;
            this.pendingStopPrice = isLong ? position.OpenPrice - stopDist : position.OpenPrice + stopDist;
            this.pendingTargetPrice = isLong ? position.OpenPrice + targetDist : position.OpenPrice - targetDist;
            this.Log(
                "[Risk] ADOPTED a position this instance didn't itself open — no real signal data "
                + "to protect it with, so placed a fallback ATR-based stop/target from its real fill instead.",
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
            if (this.PlaceProtectiveOrders(obj))
            {
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

    private void RepairMissingProtectiveOrders()
    {
        if (!this.protectiveOrdersPlaced) return;

        var positions = this.MyPositions();
        if (positions.Length == 0) return;

        var position = positions[0];
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
            var atr = this.ComputeTrendAtr();
            if (atr <= 0) atr = 10.0;
            var targetDist = atr * Math.Min(2.0, this.MaxStopDistanceAtrMult) * this.TargetFallbackRMultiple;
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

    // ---- active in-trade management (ported near-verbatim from srmAutoStrategy) ----------------

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
            && this.HasFreshAdverseWickless(isLong))
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

    /// <summary>Same raw structural check the entry signal itself uses, just evaluated against the
    /// MOST RECENTLY closed 1-minute bar regardless of trend alignment — a fresh candle forming
    /// against the position is treated as evidence the move may be reversing, whether or not it
    /// would also qualify as a fresh ENTRY signal for the other side.</summary>
    private bool HasFreshAdverseWickless(bool isLong) =>
        this.lastMinuteBar is { } bar && (isLong ? IsBearishCandle(bar) && this.IsWicklessTop(bar) : IsBullishCandle(bar) && this.IsWicklessBottom(bar));

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

    private void MaybeLogHeartbeat()
    {
        var now = Core.TimeUtils.DateTimeUtcNow;
        if (now - this.lastHeartbeatUtc < TimeSpan.FromMinutes(HeartbeatIntervalMinutes)) return;
        this.lastHeartbeatUtc = now;

        var last = this.CurrentSymbol?.Last ?? 0;
        var trend = this.ComputeTrend();
        var atr = this.ComputeTrendAtr();

        var sb = new System.Text.StringBuilder();
        sb.Append($"[Heartbeat] price={last:0.##} trend={trend} ATR={atr:0.##}. ");
        sb.Append(this.pendingSignal is { } p
            ? $"Pending signal: {(p.IsShort ? "short" : "long")} wickless extreme {p.WicklessExtreme:0.##}. "
            : "No pending signal. ");
        sb.Append($"In position: {this.MyPositions().Length > 0}.");

        this.Log(sb.ToString(), StrategyLoggingLevel.Trading);
    }

    // ---- ownership helpers (ported verbatim from srmAutoStrategy) -------------------------------

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

    // ---- account-balance-based risk limits, restart-immune (ported verbatim from srmAutoStrategy,
    // itself ported from mesOrbStrategy) ----------------------------------------------------------

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
