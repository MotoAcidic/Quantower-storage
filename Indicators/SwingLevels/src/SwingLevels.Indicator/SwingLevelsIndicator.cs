using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using FinchLite;
using TradingPlatform.BusinessLayer;
using Qt = TradingPlatform.BusinessLayer;

namespace SwingLevels;

/// <summary>
/// Higher-timeframe support/resistance — operator's own ask (2026-10-15): "lets make a new
/// indicator i want to trade on a higher time frame with less risk, i want to trade support and
/// resistance levels." Confirmed via AskUserQuestion: support/resistance defined as confirmed
/// swing highs/lows ON the higher timeframe itself (not prior-period H/L or volume-profile
/// levels), 1-hour by default, visual only — draws levels, places no orders, reads no account
/// (unlike `srmAutoStrategy`, this is purely a manual-trading aid, by the operator's own choice).
///
/// SWING DETECTION is `SwingLevelEngine.cs`'s own fractal pivot rule — see that file's doc
/// comment for why it's a fresh, independent implementation rather than reused from
/// `goldOrbStrategy`'s own `SwingLevelTracker.cs` despite the identical core pivot math.
///
/// Each confirmed swing extends forward (solid, bright) from the bar that confirmed it until a
/// later bar trades through that exact price, then fades (dim, dotted, "(tested)" label suffix) —
/// same "untested until touched" convention as `OrbLevels`' own session highs/lows, which this
/// indicator's own lifecycle/overlay shape is directly adapted from.
///
/// SWING LEVELS CAN RENDER AS REACTION ZONES, not bare lines — operator's own charting habit
/// (screenshot, 2026-10-15): "the box were its were price keeps touching as a support and
/// resistance level so i know when to see the reaction to the box." `ShowSwingZoneBox` +
/// `SwingZoneHalfHeightPoints` switch the line to a filled price-band box, same fill+border shape
/// as `LevelsMap`'s own dark-pool zone boxes.
///
/// SESSION LEVELS (Asia/London/NY AM/NY PM) are a second, independent engine
/// (`SessionLevelEngine.cs`/`SessionLevelOverlay.cs`) ported from the operator's own
/// `TradingView/untestedLevels.pine` ("Sessions Suite [claysul]") — same ask, "i have my
/// untestedLevels.pine file that i use to mark out untested session levels i want added into this
/// as well with the settings options," scoped via AskUserQuestion to the four session range boxes
/// specifically (not that script's prior-day/week/month H/L or opening-gap features). Each
/// session's high/low is tracked live (the box) while its ET window is open, then finalized into
/// two untested levels the instant the window closes — same "untested until touched" semantics as
/// the swing engine, deliberately drawn in a visually distinct per-session colour + dash style so
/// the two engines' output is tellable apart at a glance.
///
/// HISTORICAL STATISTICS (`SwingHistoryStats.cs`) — operator's own ask: "keep that logic that
/// shows if its been retested and probability of a reversal on that level." A real counted backtest
/// over this instrument's own swing-timeframe history (not an invented heuristic, matching the
/// rigor this codebase already holds `srm.pine`'s own adaptive curves to): the aggregate reversal
/// rate across every confirmed swing. Recomputed once per NEW closed swing-timeframe bar (not every
/// poll tick — the underlying bar set doesn't change any faster), rendered as a small text panel
/// bottom-left of the pane. A companion trend-reach distance-bucket statistic was built and shipped
/// the same day, then removed at the operator's own request shortly after ("remove the historical
/// bucket part in the indicator i dont need it") — deleted outright, not just hidden.
///
/// WICKLESS-CANDLE LEVELS (`WicklessLevelEngine.cs`/`WicklessLevelOverlay.cs`) — operator's own ask:
/// "i also have an indicator that marks out untested wickless candles but i dont have the source to
/// it." No source to port, so this reuses the one "wickless" definition already verified against
/// live trading in this codebase: `srmAutoStrategy.cs`'s own `TryFindWicklessTarget` (a candle's
/// body-extreme sits within a small tolerance of its own high/low on that side). A separate
/// timeframe (15-minute default, independent of the swing timeframe) feeds its own engine; each
/// wickless high/low extends forward as an untested level, same semantics as swing and session
/// levels, drawn as a thin dash-dot line (its own colour pair) since — like `srmAutoStrategy`'s own
/// usage of it — a wickless level is one exact price, not a reaction zone.
///
/// SRM VOL-STEP RANGE (S1-S3/R1-R3) — operator's own ask: "i need the srm portion from the
/// finch-lite that shows just the s1 s2 s3 and r1 r2 r3." Reuses `Finch-Lite`'s own `SrmEngine`/
/// `CboeIndexClient` wholesale via `&lt;Compile Include&gt;` (same cross-project reuse direction
/// `srmAutoStrategy` already established for these two files) rather than re-deriving the implied-
/// move math, but feeds it a `SrmSettings` with every session/weekly/monthly/IB/naked-level feature
/// disabled — ONLY `VolStepEnabled` on — so none of that other work runs. Finch-Lite's own version
/// of this feature only ever drew two levels each side (R1/R2, S1/S2); extended to three here
/// (R1-R3/S1-S3) to match the operator's own literal ask. Full-pane-width lines (not anchored to a
/// confirmation bar — the midpoint only moves once price has travelled a full step away, same
/// "implied-move mode only" limitation Finch-Lite's own doc comment already states).
///
/// Draws only. Places no orders, reads no account.
/// </summary>
public sealed class SwingLevelsIndicator : Qt.Indicator
{
    private const int RetryIntervalMs = 1000;

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private Timer? retryTimer;
    private Timer? pollTimer;
    private Qt.Symbol? symbol;
    private string? overlayFault;
    private HistoricalData? history;
    private int barsSeen;

    private SessionLevelEngine? sessionEngine;
    private HistoricalData? sessionHistory;
    private int sessionBarsSeen;
    private string? sessionFault;

    private WicklessLevelEngine? wicklessEngine;
    private HistoricalData? wicklessHistory;
    private int wicklessBarsSeen;
    private string? wicklessFault;

    private readonly SrmEngine srmEngine = new();
    private HistoricalData? srmBarHistory;
    private HistoricalData? srmDailyHistory;
    private int srmBarsSeen;
    private Timer? srmVolTimer;
    private int srmVolTimerIntervalMs;
    private double? srmVolLast;
    private string? srmFault;

    [InputParameter("Poll interval (ms)", 1, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; } = 1000;

    [InputParameter("Swing timeframe", 10)]
    public Period SwingTimeframe { get; set; } = Period.HOUR1;

    [InputParameter("Pivot lookback (bars each side)", 11, 1, 20, 1, 0)]
    public int PivotLookback { get; set; } = 3;

    [InputParameter("Max levels kept", 12, 5, 200, 1, 0)]
    public int MaxLevelsKept { get; set; } = 30;

    [InputParameter("History lookback (days, for finding swings on attach)", 13, 5, 365, 1, 0)]
    public int HistoryLookbackDays { get; set; } = 60;

    [InputParameter("Show tested (touched) levels", 14)]
    public bool ShowTestedLevels { get; set; } = true;

    [InputParameter("Resistance (swing high) colour", 20)]
    public Color ResistanceColor { get; set; } = Color.FromArgb(0xF2, 0x36, 0x45);

    [InputParameter("Support (swing low) colour", 21)]
    public Color SupportColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Line thickness (px)", 22, 0.5, 10, 0.5, 1)]
    public double LineThickness { get; set; } = 2.0;

    [InputParameter("Show swing levels as reaction zones (box)", 23)]
    public bool ShowSwingZoneBox { get; set; } = true;

    [InputParameter("Swing zone half-height (points)", 24, 0.25, 100, 0.25, 2)]
    public double SwingZoneHalfHeightPoints { get; set; } = 3.0;

    // ---- Session levels (ported from untestedLevels.pine's own "Sessions Suite") -------------

    [InputParameter("Session levels: enabled", 30)]
    public bool SessionLevelsEnabled { get; set; } = true;

    [InputParameter("Session levels: bar resolution", 31)]
    public Period SessionBarResolution { get; set; } = Period.MIN5;

    [InputParameter("Session levels: max kept", 32, 4, 200, 1, 0)]
    public int MaxSessionLevelsKept { get; set; } = 24;

    [InputParameter("Session levels: show live box", 33)]
    public bool ShowLiveSessionBox { get; set; } = true;

    [InputParameter("Session levels: show tested (touched) levels", 34)]
    public bool ShowTestedSessionLevels { get; set; } = true;

    [InputParameter("Session levels: line thickness (px)", 35, 0.5, 10, 0.5, 1)]
    public double SessionLineThickness { get; set; } = 1.5;

    [InputParameter("Asia session: enabled", 40)]
    public bool EnableAsiaSession { get; set; } = true;

    [InputParameter("Asia session: start hour (ET)", 41, 0, 23, 1, 0)]
    public int AsiaStartHour { get; set; } = 18;

    [InputParameter("Asia session: start minute (ET)", 42, 0, 59, 1, 0)]
    public int AsiaStartMinute { get; set; } = 0;

    [InputParameter("Asia session: end hour (ET)", 43, 0, 23, 1, 0)]
    public int AsiaEndHour { get; set; } = 0;

    [InputParameter("Asia session: end minute (ET)", 44, 0, 59, 1, 0)]
    public int AsiaEndMinute { get; set; } = 0;

    [InputParameter("Asia session: colour", 45)]
    public Color AsiaColor { get; set; } = Color.FromArgb(0xE0, 0x9B, 0x2D);

    [InputParameter("London session: enabled", 50)]
    public bool EnableLondonSession { get; set; } = true;

    [InputParameter("London session: start hour (ET)", 51, 0, 23, 1, 0)]
    public int LondonStartHour { get; set; } = 0;

    [InputParameter("London session: start minute (ET)", 52, 0, 59, 1, 0)]
    public int LondonStartMinute { get; set; } = 0;

    [InputParameter("London session: end hour (ET)", 53, 0, 23, 1, 0)]
    public int LondonEndHour { get; set; } = 6;

    [InputParameter("London session: end minute (ET)", 54, 0, 59, 1, 0)]
    public int LondonEndMinute { get; set; } = 0;

    [InputParameter("London session: colour", 55)]
    public Color LondonColor { get; set; } = Color.FromArgb(0x4E, 0xA6, 0xE0);

    [InputParameter("NY AM session: enabled", 60)]
    public bool EnableNyAmSession { get; set; } = true;

    [InputParameter("NY AM session: start hour (ET)", 61, 0, 23, 1, 0)]
    public int NyAmStartHour { get; set; } = 6;

    [InputParameter("NY AM session: start minute (ET)", 62, 0, 59, 1, 0)]
    public int NyAmStartMinute { get; set; } = 0;

    [InputParameter("NY AM session: end hour (ET)", 63, 0, 23, 1, 0)]
    public int NyAmEndHour { get; set; } = 12;

    [InputParameter("NY AM session: end minute (ET)", 64, 0, 59, 1, 0)]
    public int NyAmEndMinute { get; set; } = 0;

    [InputParameter("NY AM session: colour", 65)]
    public Color NyAmColor { get; set; } = Color.FromArgb(0x1F, 0xB0, 0x8A);

    [InputParameter("NY PM session: enabled", 70)]
    public bool EnableNyPmSession { get; set; } = true;

    [InputParameter("NY PM session: start hour (ET)", 71, 0, 23, 1, 0)]
    public int NyPmStartHour { get; set; } = 12;

    [InputParameter("NY PM session: start minute (ET)", 72, 0, 59, 1, 0)]
    public int NyPmStartMinute { get; set; } = 0;

    [InputParameter("NY PM session: end hour (ET)", 73, 0, 23, 1, 0)]
    public int NyPmEndHour { get; set; } = 18;

    [InputParameter("NY PM session: end minute (ET)", 74, 0, 59, 1, 0)]
    public int NyPmEndMinute { get; set; } = 0;

    [InputParameter("NY PM session: colour", 75)]
    public Color NyPmColor { get; set; } = Color.FromArgb(0xE0, 0x5A, 0x6E);

    // ---- Historical statistics (reversal rate) -----------------------------------------------

    [InputParameter("Statistics: show panel", 36)]
    public bool ShowStatsPanel { get; set; } = true;

    [InputParameter("Statistics: min forward bars for a fair read", 37, 1, 100, 1, 0)]
    public int StatsMinForwardBars { get; set; } = 5;

    // ---- Wickless-candle levels (no source indicator to port — reuses srmAutoStrategy's own
    // already-verified "wickless" definition, see WicklessLevelEngine.cs's own doc comment) ------

    [InputParameter("Wickless levels: enabled", 80)]
    public bool WicklessLevelsEnabled { get; set; } = true;

    [InputParameter("Wickless levels: timeframe", 81)]
    public Period WicklessTimeframe { get; set; } = Period.MIN15;

    [InputParameter("Wickless levels: tolerance (points, 0 = no wick at all)", 82, 0, 10, 0.1, 2)]
    public double WicklessTolerancePoints { get; set; } = 0.25;

    [InputParameter("Wickless levels: max kept", 83, 4, 300, 1, 0)]
    public int MaxWicklessLevelsKept { get; set; } = 40;

    [InputParameter("Wickless levels: show tested (touched) levels", 84)]
    public bool ShowTestedWicklessLevels { get; set; } = true;

    [InputParameter("Wickless levels: line thickness (px)", 85, 0.5, 10, 0.5, 1)]
    public double WicklessLineThickness { get; set; } = 1.5;

    [InputParameter("Wickless high (no upper wick) colour", 86)]
    public Color WicklessHighColor { get; set; } = Color.FromArgb(0xD8, 0x5C, 0xE0);

    [InputParameter("Wickless low (no lower wick) colour", 87)]
    public Color WicklessLowColor { get; set; } = Color.FromArgb(0x5C, 0xC8, 0xE0);

    // ---- SRM Vol-Step range (S1-S3 / R1-R3) — ported from Finch-Lite's own SRM feature, this one
    // sub-piece only ("i need the srm portion from the finch-lite that shows just the s1 s2 s3 and
    // r1 r2 r3"). Reuses Finch-Lite's own SrmEngine/CboeIndexClient via <Compile Include>. --------

    [InputParameter("SRM Vol-Step: enabled", 90)]
    public bool SrmVolStepEnabled { get; set; } = true;

    [InputParameter("SRM Vol-Step: implied-vol ticker (CBOE index)", 91)]
    public string SrmVolTicker { get; set; } = "VXN";

    [InputParameter("SRM Vol-Step: vol poll interval (seconds)", 92, 15, 600, 5, 0)]
    public int SrmVolPollIntervalSeconds { get; set; } = 60;

    [InputParameter("SRM Vol-Step: bar resolution", 93)]
    public Period SrmBarResolution { get; set; } = Period.MIN5;

    [InputParameter("SRM Vol-Step: step multiplier (x daily expected move)", 94, 0.05, 3, 0.05, 2)]
    public double SrmVolStepMult { get; set; } = 0.5;

    [InputParameter("SRM Vol-Step: daily lookback (days, for prior close / ATR fallback)", 95, 5, 120, 1, 0)]
    public int SrmDailyLookbackDays { get; set; } = 40;

    [InputParameter("SRM Vol-Step: line thickness (px)", 96, 0.5, 10, 0.5, 1)]
    public double SrmVolStepLineThickness { get; set; } = 1.5;

    [InputParameter("SRM Vol-Step: resistance (R1-R3) colour", 97)]
    public Color SrmVolStepUpColor { get; set; } = Color.FromArgb(0xE0, 0x5A, 0x6E);

    [InputParameter("SRM Vol-Step: support (S1-S3) colour", 98)]
    public Color SrmVolStepDownColor { get; set; } = Color.FromArgb(0x4E, 0xA6, 0xE0);

    [InputParameter("SRM Vol-Step: mid colour", 99)]
    public Color SrmVolStepMidColor { get; set; } = Color.FromArgb(0x9A, 0x9A, 0x9A);

    public SwingLevelsIndicator()
    {
        this.Name = "Swing S/R";
        this.Description =
            "Higher-timeframe support/resistance from confirmed swing highs/lows (1-hour by "
            + "default) — extends untested, fades once price trades through it. Visual only, "
            + "places no orders.";
        this.SeparateWindow = false;
    }

    private SwingLevelEngine? engine;
    private readonly SwingLevelOverlay overlay = new();
    private volatile SwingLevelDrawable drawable = SwingLevelDrawable.Empty;

    private readonly SessionLevelOverlay sessionOverlay = new();
    private volatile SessionLevelDrawable sessionDrawable = SessionLevelDrawable.Empty;

    private readonly WicklessLevelOverlay wicklessOverlay = new();
    private volatile WicklessLevelDrawable wicklessDrawable = WicklessLevelDrawable.Empty;

    private readonly SrmVolStepOverlay srmVolStepOverlay = new();
    private volatile SrmVolStepDrawable srmVolStepDrawable = SrmVolStepDrawable.Empty;

    private volatile ReversalStats? reversalStats;
    private volatile string[] statsLines = Array.Empty<string>();

    // ---- lifecycle ---------------------------------------------------------------------------

    protected override void OnInit()
    {
        if (!this.TryInitialise())
            this.retryTimer = new Timer(this.OnRetryTimer, null, RetryIntervalMs, RetryIntervalMs);
    }

    private readonly object initGate = new();

    private void OnRetryTimer(object? _)
    {
        lock (this.initGate)
        {
            if (this.pollTimer is not null) return;

            if (this.TryInitialise())
            {
                this.retryTimer?.Dispose();
                this.retryTimer = null;
            }
        }
    }

    /// <summary>Only precondition is the symbol being attached. Same retry-on-empty-backlog
    /// pattern as `OrbLevels`' own `TryInitialise` — `GetHistory()` can return a handle that's
    /// still populating in the background; reading `.Count` immediately after can see it empty.</summary>
    private bool TryInitialise()
    {
        try
        {
            var symbol = this.Symbol;
            if (symbol is null)
            {
                this.overlayFault = "No symbol is attached yet — waiting.";
                return false;
            }

            this.overlayFault = null;
            this.symbol = symbol;

            var lookback = DateTime.UtcNow.AddDays(-Math.Max(5, this.HistoryLookbackDays));
            var hist = symbol.GetHistory(this.SwingTimeframe, symbol.HistoryType, lookback);

            if (hist.Count < (2 * this.PivotLookback) + 10)
            {
                hist.Dispose();
                this.overlayFault = $"Waiting for history to populate ({hist.Count} bars so far)...";
                return false;
            }

            this.history = hist;
            this.barsSeen = 0;
            this.engine = new SwingLevelEngine(this.PivotLookback, this.MaxLevelsKept);

            this.ReplayHistory();

            this.TryInitialiseSessionLevels(symbol);
            this.TryInitialiseWicklessLevels(symbol);
            this.TryInitialiseSrmVolStep(symbol);

            var interval = Math.Max(this.PollIntervalMs, 50);
            this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);
            return true;
        }
        catch (Exception ex)
        {
            this.overlayFault = $"SwingLevels failed to start: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Best-effort — a failure here (e.g. the broker has no 5-minute history for this
    /// symbol) disables session levels and reports <see cref="sessionFault"/>, but never blocks the
    /// swing-level feature the indicator started life as. Fed from its own bar stream
    /// (<see cref="SessionBarResolution"/>), independent of <see cref="SwingTimeframe"/> — a
    /// session's true high/low needs resolution finer than the window itself.</summary>
    private void TryInitialiseSessionLevels(Qt.Symbol symbol)
    {
        try
        {
            this.sessionHistory?.Dispose();
            this.sessionHistory = null;
            this.sessionEngine = null;

            if (!this.SessionLevelsEnabled)
            {
                this.sessionFault = null;
                return;
            }

            var lookback = DateTime.UtcNow.AddDays(-Math.Max(2, Math.Min(this.HistoryLookbackDays, 10)));
            var hist = symbol.GetHistory(this.SessionBarResolution, symbol.HistoryType, lookback);

            if (hist.Count < 10)
            {
                hist.Dispose();
                this.sessionFault = $"Session levels: waiting for {this.SessionBarResolution} history to populate...";
                return;
            }

            this.sessionHistory = hist;
            this.sessionBarsSeen = 0;
            this.sessionEngine = new SessionLevelEngine(this.MaxSessionLevelsKept);
            this.sessionFault = null;

            this.ReplaySessionHistory();
        }
        catch (Exception ex)
        {
            this.sessionFault = $"Session levels failed to start: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Same best-effort shape as <see cref="TryInitialiseSessionLevels"/> — a failure here
    /// disables wickless levels and reports <see cref="wicklessFault"/> without touching anything
    /// else the indicator already does.</summary>
    private void TryInitialiseWicklessLevels(Qt.Symbol symbol)
    {
        try
        {
            this.wicklessHistory?.Dispose();
            this.wicklessHistory = null;
            this.wicklessEngine = null;

            if (!this.WicklessLevelsEnabled)
            {
                this.wicklessFault = null;
                return;
            }

            var lookback = DateTime.UtcNow.AddDays(-Math.Max(2, Math.Min(this.HistoryLookbackDays, 20)));
            var hist = symbol.GetHistory(this.WicklessTimeframe, symbol.HistoryType, lookback);

            if (hist.Count < 5)
            {
                hist.Dispose();
                this.wicklessFault = $"Wickless levels: waiting for {this.WicklessTimeframe} history to populate...";
                return;
            }

            this.wicklessHistory = hist;
            this.wicklessBarsSeen = 0;
            this.wicklessEngine = new WicklessLevelEngine(this.WicklessTolerancePoints, this.MaxWicklessLevelsKept);
            this.wicklessFault = null;

            this.ReplayWicklessHistory();
        }
        catch (Exception ex)
        {
            this.wicklessFault = $"Wickless levels failed to start: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static bool TryReadBarOhlc(
        HistoricalData data, int index, out DateTime timeUtc, out double open, out double high, out double low, out double close)
    {
        timeUtc = default; open = high = low = close = 0;
        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item) return false;
        timeUtc = item.TimeLeft; open = item.Open; high = item.High; low = item.Low; close = item.Close;
        return true;
    }

    private void ReplayWicklessHistory()
    {
        if (this.wicklessEngine is not { } eng || this.wicklessHistory is not { } h) return;

        var closedUpTo = Math.Max(0, h.Count - 1);
        for (var i = 0; i < closedUpTo; i++)
        {
            if (TryReadBarOhlc(h, i, out var timeUtc, out var open, out var high, out var low, out var close))
                eng.FeedBar(timeUtc, open, high, low, close);
        }

        this.wicklessBarsSeen = closedUpTo;
    }

    private void DrainWicklessBars()
    {
        if (this.wicklessEngine is not { } eng || this.wicklessHistory is not { Count: > 1 } h) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.wicklessBarsSeen; i < closedUpTo; i++)
        {
            if (TryReadBarOhlc(h, i, out var timeUtc, out var open, out var high, out var low, out var close))
                eng.FeedBar(timeUtc, open, high, low, close);
        }

        this.wicklessBarsSeen = closedUpTo;
    }

    private void RebuildWicklessDrawable()
    {
        if (this.wicklessEngine is not { } eng)
        {
            this.wicklessDrawable = WicklessLevelDrawable.Empty;
            return;
        }

        var lines = new List<WicklessLevelDraw>(eng.Levels.Count);
        foreach (var lvl in eng.Levels)
        {
            var untested = lvl.TouchedUtc is null;
            if (!untested && !this.ShowTestedWicklessLevels) continue;

            var colour = lvl.IsHigh ? this.WicklessHighColor : this.WicklessLowColor;
            lines.Add(new WicklessLevelDraw(lvl.IsHigh, lvl.Price, lvl.ConfirmedUtc, untested, colour));
        }

        this.wicklessDrawable = new WicklessLevelDrawable(lines.ToArray());
    }

    // ---- SRM Vol-Step range -------------------------------------------------------------------

    /// <summary>Best-effort, same shape as <see cref="TryInitialiseSessionLevels"/>/
    /// <see cref="TryInitialiseWicklessLevels"/> — a failure disables this one feature and reports
    /// <see cref="srmFault"/> without touching anything else. Starts the daily history fetch (prior
    /// close + ATR14 fallback) and the bar stream; the volatility-quote timer itself is
    /// started/stopped reactively from <see cref="SyncSrmVolTimer"/>, called every poll tick, same
    /// self-healing shape Finch-Lite's own SRM feature already uses — a later settings-panel toggle
    /// must take effect without a reattach.</summary>
    private void TryInitialiseSrmVolStep(Qt.Symbol symbol)
    {
        try
        {
            this.srmBarHistory?.Dispose();
            this.srmBarHistory = null;
            this.srmDailyHistory?.Dispose();
            this.srmDailyHistory = null;
            this.srmEngine.Reset();

            if (!this.SrmVolStepEnabled)
            {
                this.srmFault = null;
                return;
            }

            var dailyLookback = DateTime.UtcNow.AddDays(-Math.Max(5, this.SrmDailyLookbackDays));
            this.srmDailyHistory = symbol.GetHistory(Period.DAY1, symbol.HistoryType, dailyLookback);

            var barLookback = DateTime.UtcNow.AddDays(-Math.Max(2, Math.Min(this.HistoryLookbackDays, 10)));
            var hist = symbol.GetHistory(this.SrmBarResolution, symbol.HistoryType, barLookback);

            if (hist.Count < 5)
            {
                hist.Dispose();
                this.srmFault = $"SRM Vol-Step: waiting for {this.SrmBarResolution} history to populate...";
                return;
            }

            this.srmBarHistory = hist;
            this.srmBarsSeen = 0;
            this.srmFault = null;
        }
        catch (Exception ex)
        {
            this.srmFault = $"SRM Vol-Step failed to start: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void SyncSrmVolTimer()
    {
        if (!this.SrmVolStepEnabled)
        {
            if (this.srmVolTimer is null) return;
            this.srmVolTimer.Dispose();
            this.srmVolTimer = null;
            return;
        }

        var wantedInterval = Math.Max(this.SrmVolPollIntervalSeconds, 15) * 1000;
        if (this.srmVolTimer is not null && this.srmVolTimerIntervalMs == wantedInterval) return;

        this.srmVolTimer?.Dispose();
        this.srmVolTimerIntervalMs = wantedInterval;
        this.srmVolTimer = new Timer(this.OnSrmVolTimer, null, 0, wantedInterval);
    }

    /// <summary>Only the one ticker this feature actually needs — unlike Finch-Lite's own SRM
    /// feature, nothing here reads the "Expanded" regime flag (VolStep's own step size is never
    /// multiplied by it), so VIX/VIX3M aren't fetched at all.</summary>
    private void OnSrmVolTimer(object? state)
    {
        try
        {
            var ticker = string.IsNullOrWhiteSpace(this.SrmVolTicker) ? "VXN" : this.SrmVolTicker.Trim().ToUpperInvariant();
            this.srmVolLast = CboeIndexClient.FetchQuote(ticker).CurrentPrice;
        }
        catch (Exception ex)
        {
            this.srmFault = $"SRM Vol-Step volatility fetch failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private SrmSettings BuildSrmVolStepSettings() => new(
        AsiaEnabled: false, AsiaShare: 0,
        LondonEnabled: false, LondonShare: 0,
        NyEnabled: false, NyShare: 0,
        WeeklyEnabled: false, WeeklyBudgetMult: 0, WeekKProj: 0, WeekRows: 1,
        MonthlyEnabled: false, MonthlyBudgetMult: 0, MonthKProj: 0, MonthRows: 1,
        KProj: 0, AdaptiveProjection: false, ExtMult: 0,
        ExpandedScaleEnabled: false, ExpandedMultiplier: 1,
        SessionRows: 1, ValueAreaPercent: 0.7,
        IbEnabled: false, IbMinutes: 60,
        VolStepEnabled: true, VolStepMult: this.SrmVolStepMult);

    private void DrainSrm()
    {
        if (!this.SrmVolStepEnabled || this.srmBarHistory is not { Count: > 1 } h) return;

        double? priorClose = null;
        var atrFallback = 0d;
        if (this.srmDailyHistory is { Count: > 1 } daily && SrmEngine.TryComputeDailyVolInputs(daily, out var pc, out var atr))
        {
            priorClose = pc;
            atrFallback = atr;
        }

        this.srmEngine.SetVolatility(priorClose, this.srmVolLast, null, null, atrFallback);

        var tickSize = this.symbol?.TickSize ?? 0d;
        var settings = this.BuildSrmVolStepSettings();

        var closedUpTo = h.Count - 1;
        for (var i = this.srmBarsSeen; i < closedUpTo; i++)
        {
            if (h[i, SeekOriginHistory.Begin] is not HistoryItemBar item) continue;
            this.srmEngine.FeedBar(item.TimeLeft, item.TimeLeft, item.Open, item.High, item.Low, item.Volume, tickSize, settings);
        }
        this.srmBarsSeen = closedUpTo;

        this.RebuildSrmVolStepDrawable();
    }

    private void RebuildSrmVolStepDrawable()
    {
        if (this.srmEngine.VsMid is not { } vsMid || this.srmEngine.VsHalf is not { } vsHalf)
        {
            this.srmVolStepDrawable = SrmVolStepDrawable.Empty;
            return;
        }

        var lines = new[]
        {
            new SrmVolStepLineDraw(vsMid + (3 * vsHalf), this.SrmVolStepUpColor, "R3"),
            new SrmVolStepLineDraw(vsMid + (2 * vsHalf), this.SrmVolStepUpColor, "R2"),
            new SrmVolStepLineDraw(vsMid + vsHalf, this.SrmVolStepUpColor, "R1"),
            new SrmVolStepLineDraw(vsMid, this.SrmVolStepMidColor, "Mid"),
            new SrmVolStepLineDraw(vsMid - vsHalf, this.SrmVolStepDownColor, "S1"),
            new SrmVolStepLineDraw(vsMid - (2 * vsHalf), this.SrmVolStepDownColor, "S2"),
            new SrmVolStepLineDraw(vsMid - (3 * vsHalf), this.SrmVolStepDownColor, "S3"),
        };

        this.srmVolStepDrawable = new SrmVolStepDrawable(lines);
    }

    private List<SessionDef> BuildSessionDefs() => new()
    {
        new SessionDef
        {
            Name = "Asia", Enabled = this.EnableAsiaSession, Color = this.AsiaColor,
            StartHour = this.AsiaStartHour, StartMinute = this.AsiaStartMinute,
            EndHour = this.AsiaEndHour, EndMinute = this.AsiaEndMinute,
        },
        new SessionDef
        {
            Name = "London", Enabled = this.EnableLondonSession, Color = this.LondonColor,
            StartHour = this.LondonStartHour, StartMinute = this.LondonStartMinute,
            EndHour = this.LondonEndHour, EndMinute = this.LondonEndMinute,
        },
        new SessionDef
        {
            Name = "NY AM", Enabled = this.EnableNyAmSession, Color = this.NyAmColor,
            StartHour = this.NyAmStartHour, StartMinute = this.NyAmStartMinute,
            EndHour = this.NyAmEndHour, EndMinute = this.NyAmEndMinute,
        },
        new SessionDef
        {
            Name = "NY PM", Enabled = this.EnableNyPmSession, Color = this.NyPmColor,
            StartHour = this.NyPmStartHour, StartMinute = this.NyPmStartMinute,
            EndHour = this.NyPmEndHour, EndMinute = this.NyPmEndMinute,
        },
    };

    public override void Dispose()
    {
        this.retryTimer?.Dispose();
        this.pollTimer?.Dispose();
        this.srmVolTimer?.Dispose();
        this.history?.Dispose();
        this.sessionHistory?.Dispose();
        this.wicklessHistory?.Dispose();
        this.srmBarHistory?.Dispose();
        this.srmDailyHistory?.Dispose();
        this.overlay.Dispose();
        this.sessionOverlay.Dispose();
        this.wicklessOverlay.Dispose();
        this.srmVolStepOverlay.Dispose();
        base.Dispose();
    }

    private void OnPollTimer(object? state)
    {
        try
        {
            this.DrainNewBars();
            this.RebuildDrawable();

            this.DrainSessionBars();
            this.RebuildSessionDrawable();

            this.DrainWicklessBars();
            this.RebuildWicklessDrawable();

            this.SyncSrmVolTimer();
            this.DrainSrm();
        }
        catch (Exception ex)
        {
            // An exception escaping a Timer callback is unhandled and crashes the whole platform
            // process — same lesson finchDomScalpStrategy already learned.
            this.overlayFault = $"SwingLevels poll error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ---- history replay / draining ----------------------------------------------------------

    private static bool TryReadBar(HistoricalData data, int index, out DateTime timeUtc, out double high, out double low)
    {
        timeUtc = default; high = low = 0;
        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item) return false;
        timeUtc = item.TimeLeft; high = item.High; low = item.Low;
        return true;
    }

    private void ReplayHistory()
    {
        if (this.engine is not { } eng || this.history is not { } h) return;

        var closedUpTo = Math.Max(0, h.Count - 1);
        for (var i = 0; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var timeUtc, out var high, out var low))
                eng.FeedBar(timeUtc, high, low);
        }

        this.barsSeen = closedUpTo;
        this.RecomputeStats();
    }

    private void DrainNewBars()
    {
        if (this.engine is not { } eng || this.history is not { Count: > 1 } h) return;

        var closedUpTo = h.Count - 1;
        if (closedUpTo <= this.barsSeen) return;

        for (var i = this.barsSeen; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var timeUtc, out var high, out var low))
                eng.FeedBar(timeUtc, high, low);
        }

        this.barsSeen = closedUpTo;
        this.RecomputeStats();
    }

    // ---- historical statistics (reversal rate / trend-reach probability) --------------------

    /// <summary>Bounded to the same lookback the swing engine itself keeps (`SwingLevelEngine.MaxBarsKept`)
    /// — a real, counted sample over everything the engine has already seen, not a separate larger
    /// pull. Re-derives from `this.history` directly rather than threading bar storage out of the
    /// swing engine, which only ever needed OHLC for its own pivot math, not a reusable bar list.</summary>
    private List<StatsBar> BuildStatsBars()
    {
        if (this.history is not { } h) return new List<StatsBar>();

        var closedUpTo = Math.Max(0, h.Count - 1);
        var start = Math.Max(0, closedUpTo - 2000);
        var bars = new List<StatsBar>(closedUpTo - start);
        for (var i = start; i < closedUpTo; i++)
        {
            if (TryReadBarOhlc(h, i, out var timeUtc, out _, out var high, out var low, out var close))
                bars.Add(new StatsBar(timeUtc, high, low, close));
        }
        return bars;
    }

    /// <summary>Real historical backtest, recomputed each time a new swing-timeframe bar closes (not
    /// every poll tick — the underlying bar set only changes that often). Operator's own ask
    /// (2026-10-15): "keep that logic that shows if its been retested and probability of a reversal
    /// on that level," scoped via AskUserQuestion to a real counted sample on this instrument's own
    /// history (not an invented heuristic) — same rigor this codebase already holds `srm.pine`'s own
    /// adaptive curves to. The companion trend-reach bucket statistic was removed the same day at
    /// the operator's own request — see `SwingHistoryStats.cs`'s own doc comment.</summary>
    private void RecomputeStats()
    {
        var bars = this.BuildStatsBars();

        this.reversalStats = SwingHistoryStats.ComputeReversalStats(bars, this.PivotLookback, this.StatsMinForwardBars);
        this.statsLines = this.BuildStatsLines();
    }

    private string[] BuildStatsLines()
    {
        if (!this.ShowStatsPanel) return Array.Empty<string>();

        var lines = new List<string>();

        if (this.reversalStats is { } rs)
        {
            lines.Add(rs.Total > 0
                ? $"Swing reversal rate: {rs.Rate!.Value * 100:0.#}% held (n={rs.Total})"
                : "Swing reversal rate: insufficient history yet");
        }

        return lines.ToArray();
    }

    // ---- session bar replay / draining ------------------------------------------------------

    private void ReplaySessionHistory()
    {
        if (this.sessionEngine is not { } eng || this.sessionHistory is not { } h) return;

        var defs = this.BuildSessionDefs();
        var closedUpTo = Math.Max(0, h.Count - 1);
        for (var i = 0; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var timeUtc, out var high, out var low))
            {
                var localEt = TimeZoneInfo.ConvertTimeFromUtc(timeUtc, SessionZone);
                eng.FeedBar(timeUtc, localEt, high, low, defs);
            }
        }

        this.sessionBarsSeen = closedUpTo;
    }

    private void DrainSessionBars()
    {
        if (this.sessionEngine is not { } eng || this.sessionHistory is not { Count: > 1 } h) return;

        var defs = this.BuildSessionDefs();
        var closedUpTo = h.Count - 1;
        for (var i = this.sessionBarsSeen; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var timeUtc, out var high, out var low))
            {
                var localEt = TimeZoneInfo.ConvertTimeFromUtc(timeUtc, SessionZone);
                eng.FeedBar(timeUtc, localEt, high, low, defs);
            }
        }

        this.sessionBarsSeen = closedUpTo;
    }

    // ---- drawable rebuild ------------------------------------------------------------------------

    private void RebuildDrawable()
    {
        if (this.engine is not { } eng) return;

        var zoneHalfHeight = this.ShowSwingZoneBox ? Math.Max(0.0, this.SwingZoneHalfHeightPoints) : 0.0;

        var lines = new List<SwingLevelDraw>(eng.Levels.Count);
        foreach (var lvl in eng.Levels)
        {
            var untested = lvl.TouchedUtc is null;
            if (!untested && !this.ShowTestedLevels) continue;

            var colour = lvl.IsHigh ? this.ResistanceColor : this.SupportColor;
            lines.Add(new SwingLevelDraw(lvl.IsHigh, lvl.Price, lvl.ConfirmedUtc, untested, colour, zoneHalfHeight));
        }

        this.drawable = new SwingLevelDrawable(lines.ToArray());
    }

    private void RebuildSessionDrawable()
    {
        if (this.sessionEngine is not { } eng)
        {
            this.sessionDrawable = SessionLevelDrawable.Empty;
            return;
        }

        var defs = this.BuildSessionDefs();

        var boxes = new List<SessionBoxDraw>();
        if (this.ShowLiveSessionBox)
        {
            foreach (var def in defs)
            {
                if (!def.Enabled) continue;
                if (eng.States.TryGetValue(def.Name, out var st) && st.Active)
                    boxes.Add(new SessionBoxDraw(def.Name, st.High, st.Low, st.StartTimeUtc, def.Color));
            }
        }

        var colorByName = defs.ToDictionary(d => d.Name, d => d.Color);
        var lines = new List<SessionLevelLineDraw>(eng.Levels.Count);
        foreach (var lvl in eng.Levels)
        {
            var untested = lvl.TouchedUtc is null;
            if (!untested && !this.ShowTestedSessionLevels) continue;

            var colour = colorByName.TryGetValue(lvl.SessionName, out var c) ? c : Color.Gray;
            lines.Add(new SessionLevelLineDraw(lvl.IsHigh, lvl.Price, lvl.SinceUtc, untested, lvl.SessionName, colour));
        }

        this.sessionDrawable = new SessionLevelDrawable(boxes.ToArray(), lines.ToArray());
    }

    // ---- paint -------------------------------------------------------------------------------

    private readonly Font faultFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush faultBrush = new(Color.FromArgb(0xFF, 0xC1, 0x07));
    private readonly SolidBrush faultBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly SolidBrush statsBrush = new(Color.FromArgb(0xD8, 0xDE, 0xE8));

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);

        var graphics = args?.Graphics;
        var window = this.CurrentChart?.MainWindow;
        if (graphics is null || window is null) return;

        var pane = window.ClientRectangle;
        var faultY = pane.Top + 4f;
        foreach (var fault in new[] { this.overlayFault, this.sessionFault, this.wicklessFault, this.srmFault })
        {
            if (fault is not { Length: > 0 }) continue;

            var size = graphics.MeasureString(fault, this.faultFont);
            var rect = new RectangleF(pane.Left + 4f, faultY, size.Width + 8f, size.Height + 4f);
            graphics.FillRectangle(this.faultBack, rect);
            graphics.DrawString(fault, this.faultFont, this.faultBrush, rect.Left + 4f, rect.Top + 2f);
            faultY += size.Height + 6f;
        }

        var lines = this.statsLines;
        if (lines.Length > 0)
        {
            var sizes = new SizeF[lines.Length];
            var totalHeight = 0f;
            for (var i = 0; i < lines.Length; i++)
            {
                sizes[i] = graphics.MeasureString(lines[i], this.faultFont);
                totalHeight += sizes[i].Height + 2f;
            }

            var statY = pane.Bottom - totalHeight - 4f;
            for (var i = 0; i < lines.Length; i++)
            {
                var rect = new RectangleF(pane.Left + 4f, statY, sizes[i].Width + 8f, sizes[i].Height + 2f);
                graphics.FillRectangle(this.faultBack, rect);
                graphics.DrawString(lines[i], this.faultFont, this.statsBrush, rect.Left + 4f, rect.Top + 1f);
                statY += sizes[i].Height + 2f;
            }
        }

        var registry = new List<RectangleF>();

        try
        {
            this.sessionOverlay.Draw(graphics, window, this.sessionDrawable, (float)this.SessionLineThickness, registry);
        }
        catch (Exception ex)
        {
            this.sessionFault = $"Session levels overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.wicklessOverlay.Draw(graphics, window, this.wicklessDrawable, (float)this.WicklessLineThickness, registry);
        }
        catch (Exception ex)
        {
            this.wicklessFault = $"Wickless levels overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.srmVolStepOverlay.Draw(graphics, window, this.srmVolStepDrawable, (float)this.SrmVolStepLineThickness, registry);
        }
        catch (Exception ex)
        {
            this.srmFault = $"SRM Vol-Step overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.overlay.Draw(graphics, window, this.drawable, (float)this.LineThickness, registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"SwingLevels overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
