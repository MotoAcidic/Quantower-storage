using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using TradingPlatform.BusinessLayer;
using Qt = TradingPlatform.BusinessLayer;

namespace OrbLevels;

/// <summary>
/// "can you make me an indicator that works for painting out the orb for mes and mgc and marks
/// out the highs and lows like the strategy does" (the operator's own ask, 2026-10-06) — a visual
/// companion to `mesOrbStrategy`/`goldOrbStrategy`: paints the ORB box (high/low/midpoint) for
/// whatever symbol/window it's attached to, plus the Asia/London/NY untested session highs and
/// lows exactly as `mesOrbStrategy`'s own SESSION LEVELS setup tracks them.
///
/// NOT a shared-code port of either strategy — an indicator can't inherit from `Strategy`, and
/// the strategies' own ORB/session-level logic lives as private methods on their own classes, not
/// standalone files that could be `&lt;Compile Include&gt;`'d in. This is a deliberate, hand-
/// synced DUPLICATE of the box-building and session-freeze-and-touch logic, stripped of
/// everything entry/stop/target/risk related (an indicator draws, it never places an order or
/// reads the account) — same accepted tradeoff already documented for the (still-unbuilt)
/// OceansStack indicator plan: a future tuning change to either strategy's own ORB/session logic
/// does not reach this indicator until hand-applied here too.
///
/// Works on MES or MGC (or anything else) unmodified — reads `this.Symbol`'s own TickSize
/// wherever needed rather than assuming either instrument's contract specs, and every session
/// window (including the ORB window itself) is a plain InputParameter. `mesOrbStrategy`'s own ORB
/// is 8:00-8:15 AM ET; `goldOrbStrategy`'s is 8:00-8:05 PM ET — the defaults here match MES's own
/// morning window since that strategy also trades session levels; switch to 20:00-20:05 when
/// attaching to a gold chart.
///
/// Draws only. Places no orders, reads no account.
/// </summary>
public sealed class OrbLevelsIndicator : Qt.Indicator
{
    // ---- lifecycle scaffolding --------------------------------------------------------------

    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const int RetryIntervalMs = 1000;

    private Timer? retryTimer;
    private Timer? pollTimer;
    private Qt.Symbol? symbol;
    private string? overlayFault;
    private HistoricalData? history5m;
    private HistoricalData? history15m;
    private int barsSeen5m;
    private int barsSeen15m;

    [InputParameter("Poll interval (ms)", 1, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; } = 1000;

    // ---- ORB box -------------------------------------------------------------------------------

    [InputParameter("ORB window: start hour (ET)", 10, 0, 23, 1, 0)]
    public int OrbStartHour { get; set; } = 8;

    [InputParameter("ORB window: start minute (ET)", 11, 0, 59, 1, 0)]
    public int OrbStartMinute { get; set; } = 0;

    [InputParameter("ORB window: end hour (ET)", 12, 0, 23, 1, 0)]
    public int OrbEndHour { get; set; } = 8;

    [InputParameter("ORB window: end minute (ET)", 13, 0, 59, 1, 0)]
    public int OrbEndMinute { get; set; } = 15;

    [InputParameter("Keep prior days' ORB boxes on chart", 14, 0, 10, 1, 0)]
    public int KeepPriorDays { get; set; } = 1;

    [InputParameter("ORB box fill color", 15)]
    public Color OrbBoxColor { get; set; } = Color.DodgerBlue;

    [InputParameter("ORB high line color", 16)]
    public Color OrbHighColor { get; set; } = Color.OrangeRed;

    [InputParameter("ORB low line color", 17)]
    public Color OrbLowColor { get; set; } = Color.OrangeRed;

    [InputParameter("ORB midpoint line color", 18)]
    public Color OrbMidColor { get; set; } = Color.Gold;

    // ---- session levels (Asia/London/NY untested high/low) -------------------------------------

    [InputParameter("Session levels: enabled", 30)]
    public bool SessionLevelsEnabled { get; set; } = true;

    /// <summary>CHANGED 2026-10-06 to match mesOrbStrategy's own same-day update (source TikTok:
    /// "he goes to the 15min time frame and marks out all the untested highs and lows") — session
    /// levels now read from their OWN dedicated series, independent of the ORB box's 5-minute one,
    /// so what this indicator draws matches what the strategy is actually watching.</summary>
    [InputParameter("Session levels: timeframe", 47)]
    public Period SessionLevelPeriod { get; set; } = Period.MIN15;

    [InputParameter("Asia session start hour (ET)", 31, 0, 23, 1, 0)]
    public int AsiaStartHour { get; set; } = 18;

    [InputParameter("Asia session start minute (ET)", 32, 0, 59, 1, 0)]
    public int AsiaStartMinute { get; set; } = 0;

    [InputParameter("Asia session end hour (ET)", 33, 0, 23, 1, 0)]
    public int AsiaEndHour { get; set; } = 3;

    [InputParameter("Asia session end minute (ET)", 34, 0, 59, 1, 0)]
    public int AsiaEndMinute { get; set; } = 0;

    [InputParameter("London session start hour (ET)", 35, 0, 23, 1, 0)]
    public int LondonStartHour { get; set; } = 3;

    [InputParameter("London session start minute (ET)", 36, 0, 59, 1, 0)]
    public int LondonStartMinute { get; set; } = 0;

    [InputParameter("London session end hour (ET)", 37, 0, 23, 1, 0)]
    public int LondonEndHour { get; set; } = 11;

    [InputParameter("London session end minute (ET)", 38, 0, 59, 1, 0)]
    public int LondonEndMinute { get; set; } = 0;

    [InputParameter("NY session start hour (ET)", 39, 0, 23, 1, 0)]
    public int NySessionStartHour { get; set; } = 8;

    [InputParameter("NY session start minute (ET)", 40, 0, 59, 1, 0)]
    public int NySessionStartMinute { get; set; } = 0;

    [InputParameter("NY session end hour (ET)", 41, 0, 23, 1, 0)]
    public int NySessionEndHour { get; set; } = 17;

    [InputParameter("NY session end minute (ET)", 42, 0, 59, 1, 0)]
    public int NySessionEndMinute { get; set; } = 0;

    [InputParameter("Level touch tolerance (points)", 43, 0, 10, 0.25, 2)]
    public double LevelTouchTolerancePoints { get; set; } = 0.5;

    [InputParameter("Asia level color", 44)]
    public Color AsiaColor { get; set; } = Color.MediumPurple;

    [InputParameter("London level color", 45)]
    public Color LondonColor { get; set; } = Color.DeepSkyBlue;

    [InputParameter("NY level color", 46)]
    public Color NyColor { get; set; } = Color.LimeGreen;

    public OrbLevelsIndicator()
    {
        this.Name = "ORB + Session Levels";
        this.Description =
            "Paints the ORB box (high/low/midpoint) and the Asia/London/NY untested session "
            + "highs/lows exactly as mesOrbStrategy/goldOrbStrategy track them. Works on MES, "
            + "MGC, or anything else. Draws only, places no orders.";
        this.SeparateWindow = false;
    }

    // ---- ORB state (today's in-progress/completed range, plus a bounded history) ---------------

    private readonly record struct CompletedOrbBox(DateTime Date, DateTime StartUtc, DateTime EndUtc, double High, double Low);

    private readonly List<CompletedOrbBox> completedOrbBoxes = new();
    private DateTime orbLastDate = DateTime.MinValue;
    private bool orbBuilding;
    private bool orbCaptured;
    private double orbHigh;
    private double orbLow;
    private DateTime orbStartUtc;
    private DateTime orbEndUtc;

    // ---- session level state — mirrors mesOrbStrategy's own SessionLevel/UpdateSession, minus
    // the reaction/pullback entry machinery (an indicator only needs Price + Untested to draw) ---

    private sealed class SessionLevel
    {
        public SessionLevel(string name, bool isHighLevel) { this.Name = name; this.IsHighLevel = isHighLevel; }
        public string Name { get; }
        public bool IsHighLevel { get; }
        public double Price;
        public bool Untested;
        public bool HasValue;
        public DateTime SinceUtc;
    }

    private readonly SessionLevel asiaHigh = new("Asia High", true);
    private readonly SessionLevel asiaLow = new("Asia Low", false);
    private readonly SessionLevel londonHigh = new("London High", true);
    private readonly SessionLevel londonLow = new("London Low", false);
    private readonly SessionLevel nyHigh = new("NY High", true);
    private readonly SessionLevel nyLow = new("NY Low", false);

    private bool wasInAsia;
    private double asiaRunningHigh, asiaRunningLow;
    private bool wasInLondon;
    private double londonRunningHigh, londonRunningLow;
    private bool wasInNy;
    private double nyRunningHigh, nyRunningLow;

    private SessionLevel[] AllSessionLevels() => new[]
    {
        this.asiaHigh, this.asiaLow, this.londonHigh, this.londonLow, this.nyHigh, this.nyLow,
    };

    // ---- paint snapshots (poll writes, paint reads) ---------------------------------------------

    private readonly OrbBoxOverlay orbOverlay = new();
    private volatile OrbBoxDrawable orbDrawable = OrbBoxDrawable.Empty;

    private readonly LevelLineOverlay levelOverlay = new();
    private volatile LevelLineDrawable levelDrawable = LevelLineDrawable.Empty;

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

    /// <summary>Only precondition is the symbol being attached — unlike Finch-Lite, this never
    /// reads the DOM/depth, so there's nothing else to wait on.</summary>
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

            var lookback = DateTime.UtcNow.AddDays(-(Math.Max(0, this.KeepPriorDays) + 4));
            var history = symbol.GetHistory(Period.MIN5, symbol.HistoryType, lookback);
            var sessionHistory = symbol.GetHistory(this.SessionLevelPeriod, symbol.HistoryType, lookback);

            // FOUND 2026-10-06 (mesOrbStrategy's own identical bug, same day: "why did it not
            // have a orb box drawn... it should look at historical bars to build it if it started
            // after the time frame") — GetHistory() can return a handle that's still populating
            // its own backlog in the background; reading .Count synchronously right after the
            // call returns can see it empty or near-empty, well before the real data has actually
            // arrived. Replaying an empty/near-empty backlog here would reconstruct nothing (no
            // ORB box, no session levels) and then silently sit there, since nothing afterward
            // ever re-triggers a replay. Unlike the strategy (no retry mechanism, so it blocks
            // briefly instead), this indicator already has a retry timer built for exactly this
            // "dependency not ready yet" shape (see TryInitialise's own DepthOfMarket-style
            // precondition pattern in Finch-Lite) — reusing it here is cleaner than blocking: if
            // the backlog isn't populated yet, dispose this attempt and let OnRetryTimer call
            // TryInitialise again a second later, which re-fetches and re-checks from scratch.
            if (history.Count < 100 || sessionHistory.Count < 50)
            {
                history.Dispose();
                sessionHistory.Dispose();
                this.overlayFault = $"Waiting for history to populate ({history.Count}/100, {sessionHistory.Count}/50 bars so far)...";
                return false;
            }

            this.history5m = history;
            this.history15m = sessionHistory;
            this.barsSeen5m = 0;
            this.barsSeen15m = 0;

            this.ReplayHistory();

            var interval = Math.Max(this.PollIntervalMs, 50);
            this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);
            return true;
        }
        catch (Exception ex)
        {
            this.overlayFault = $"OrbLevels failed to start: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public override void Dispose()
    {
        this.retryTimer?.Dispose();
        this.pollTimer?.Dispose();
        this.history5m?.Dispose();
        this.history15m?.Dispose();
        this.orbOverlay.Dispose();
        this.levelOverlay.Dispose();
        base.Dispose();
    }

    private void OnPollTimer(object? state)
    {
        try
        {
            this.DrainNewBars();
            this.RebuildDrawables();
        }
        catch (Exception ex)
        {
            // An exception escaping a Timer callback is unhandled and crashes the whole platform
            // process — same lesson finchDomScalpStrategy already learned.
            this.overlayFault = $"OrbLevels poll error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ---- history replay / draining ----------------------------------------------------------

    private static bool TryReadBar(HistoricalData data, int index, out DateTime openUtc, out double open, out double high, out double low, out double close)
    {
        openUtc = default; open = high = low = close = 0;
        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item) return false;
        openUtc = item.TimeLeft; open = item.Open; high = item.High; low = item.Low; close = item.Close;
        return true;
    }

    /// <summary>Full backlog replay on attach — reconstructs completed ORB boxes and the current
    /// session-level state all at once, through the exact same per-bar methods the live poll uses
    /// (zero risk of the replay path drifting from the live path, since it IS the live path). ORB
    /// (5-min) and session levels (own dedicated timeframe, 15-min by default) are independent
    /// series since 2026-10-06, matching mesOrbStrategy's own same-day split.</summary>
    private void ReplayHistory()
    {
        if (this.history5m is { } h5)
        {
            var closedUpTo = Math.Max(0, h5.Count - 1);
            for (var i = 0; i < closedUpTo; i++)
            {
                if (TryReadBar(h5, i, out var openUtc, out var open, out var high, out var low, out var close))
                    this.ProcessOrbBar(openUtc, TimeZoneInfo.ConvertTimeFromUtc(openUtc, SessionZone), high, low);
            }

            this.barsSeen5m = closedUpTo;
        }

        if (this.SessionLevelsEnabled && this.history15m is { } h15)
        {
            var closedUpTo = Math.Max(0, h15.Count - 1);
            for (var i = 0; i < closedUpTo; i++)
            {
                if (TryReadBar(h15, i, out var openUtc, out var open, out var high, out var low, out var close))
                    this.ProcessSessionLevelsBar(openUtc, TimeZoneInfo.ConvertTimeFromUtc(openUtc, SessionZone), high, low);
            }

            this.barsSeen15m = closedUpTo;
        }
    }

    private void DrainNewBars()
    {
        if (this.history5m is { } h5 && h5.Count > 1)
        {
            var closedUpTo = h5.Count - 1;
            for (var i = this.barsSeen5m; i < closedUpTo; i++)
            {
                if (TryReadBar(h5, i, out var openUtc, out var open, out var high, out var low, out var close))
                    this.ProcessOrbBar(openUtc, TimeZoneInfo.ConvertTimeFromUtc(openUtc, SessionZone), high, low);
            }

            this.barsSeen5m = closedUpTo;
        }

        if (this.SessionLevelsEnabled && this.history15m is { } h15 && h15.Count > 1)
        {
            var closedUpTo = h15.Count - 1;
            for (var i = this.barsSeen15m; i < closedUpTo; i++)
            {
                if (TryReadBar(h15, i, out var openUtc, out var open, out var high, out var low, out var close))
                    this.ProcessSessionLevelsBar(openUtc, TimeZoneInfo.ConvertTimeFromUtc(openUtc, SessionZone), high, low);
            }

            this.barsSeen15m = closedUpTo;
        }
    }

    // ---- ORB box building — mirrors mesOrbStrategy's own ProcessClosed5mBar/FinalizeOrbRange,
    // minus the breakout/retest/rejection trading state machine (this only needs the box itself) --

    private void ProcessOrbBar(DateTime openUtc, DateTime barEt, double high, double low)
    {
        if (barEt.Date != this.orbLastDate)
        {
            this.orbLastDate = barEt.Date;
            this.orbBuilding = false;
            this.orbCaptured = false;
        }

        var inWindow = IsAtOrAfter(barEt, this.OrbStartHour, this.OrbStartMinute) && IsBefore(barEt, this.OrbEndHour, this.OrbEndMinute);

        if (inWindow)
        {
            if (!this.orbBuilding)
            {
                this.orbBuilding = true;
                this.orbHigh = high;
                this.orbLow = low;
                this.orbStartUtc = openUtc;
            }
            else
            {
                if (high > this.orbHigh) this.orbHigh = high;
                if (low < this.orbLow) this.orbLow = low;
            }

            return;
        }

        if (this.orbBuilding && !this.orbCaptured)
        {
            this.orbCaptured = true;
            this.orbEndUtc = openUtc;

            this.completedOrbBoxes.Add(new CompletedOrbBox(this.orbLastDate, this.orbStartUtc, this.orbEndUtc, this.orbHigh, this.orbLow));

            var keep = Math.Max(0, this.KeepPriorDays) + 1;
            if (this.completedOrbBoxes.Count > keep)
                this.completedOrbBoxes.RemoveRange(0, this.completedOrbBoxes.Count - keep);
        }
    }

    // ---- session levels — mirrors mesOrbStrategy's own UpdateSession/CheckLevelTouch -----------

    private void ProcessSessionLevelsBar(DateTime openUtc, DateTime barEt, double high, double low)
    {
        this.UpdateSession(barEt, openUtc, high, low, ref this.wasInAsia, ref this.asiaRunningHigh, ref this.asiaRunningLow,
            this.AsiaStartHour, this.AsiaStartMinute, this.AsiaEndHour, this.AsiaEndMinute, this.asiaHigh, this.asiaLow);

        this.UpdateSession(barEt, openUtc, high, low, ref this.wasInLondon, ref this.londonRunningHigh, ref this.londonRunningLow,
            this.LondonStartHour, this.LondonStartMinute, this.LondonEndHour, this.LondonEndMinute, this.londonHigh, this.londonLow);

        this.UpdateSession(barEt, openUtc, high, low, ref this.wasInNy, ref this.nyRunningHigh, ref this.nyRunningLow,
            this.NySessionStartHour, this.NySessionStartMinute, this.NySessionEndHour, this.NySessionEndMinute, this.nyHigh, this.nyLow);

        foreach (var level in this.AllSessionLevels())
            this.CheckLevelTouch(level, high, low);
    }

    private void UpdateSession(
        DateTime barEt, DateTime openUtc, double high, double low,
        ref bool wasIn, ref double runningHigh, ref double runningLow,
        int startHour, int startMinute, int endHour, int endMinute,
        SessionLevel highLevel, SessionLevel lowLevel)
    {
        var inSession = IsInWindow(barEt, startHour, startMinute, endHour, endMinute);

        if (inSession)
        {
            runningHigh = wasIn ? Math.Max(runningHigh, high) : high;
            runningLow = wasIn ? Math.Min(runningLow, low) : low;
        }
        else if (wasIn)
        {
            highLevel.Price = runningHigh;
            highLevel.Untested = true;
            highLevel.HasValue = true;
            highLevel.SinceUtc = openUtc;

            lowLevel.Price = runningLow;
            lowLevel.Untested = true;
            lowLevel.HasValue = true;
            lowLevel.SinceUtc = openUtc;
        }

        wasIn = inSession;
    }

    private void CheckLevelTouch(SessionLevel level, double high, double low)
    {
        if (!level.HasValue || !level.Untested) return;

        var tolerance = this.LevelTouchTolerancePoints;
        var touched = level.IsHighLevel ? high >= level.Price - tolerance : low <= level.Price + tolerance;
        if (touched) level.Untested = false;
    }

    // ---- window-check helpers ------------------------------------------------------------------

    private static int MinutesOfDay(DateTime t) => (t.Hour * 60) + t.Minute;
    private static bool IsAtOrAfter(DateTime t, int hour, int minute) => MinutesOfDay(t) >= (hour * 60) + minute;
    private static bool IsBefore(DateTime t, int hour, int minute) => MinutesOfDay(t) < (hour * 60) + minute;

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

    // ---- drawable rebuild ------------------------------------------------------------------------

    private void RebuildDrawables()
    {
        var boxes = new List<OrbBoxDraw>(this.completedOrbBoxes.Count + 1);
        foreach (var box in this.completedOrbBoxes)
            boxes.Add(new OrbBoxDraw(box.StartUtc, box.EndUtc, box.High, box.Low, (box.High + box.Low) / 2.0));

        if (this.orbBuilding && !this.orbCaptured && this.history5m is { } h && h.Count > 0)
        {
            // Today's box is still forming — extend its "end" to the latest bar so the box keeps
            // growing live instead of only appearing once the window closes.
            var nowEndUtc = h[0, SeekOriginHistory.Begin] is HistoryItemBar latest ? latest.TimeLeft : this.orbStartUtc;
            boxes.Add(new OrbBoxDraw(this.orbStartUtc, nowEndUtc, this.orbHigh, this.orbLow, (this.orbHigh + this.orbLow) / 2.0));
        }

        this.orbDrawable = new OrbBoxDrawable(boxes.ToArray());

        var lines = new List<LevelLineDraw>(6);
        void AddLine(SessionLevel level, Color colour)
        {
            if (!level.HasValue) return;
            lines.Add(new LevelLineDraw(level.Name, level.Price, level.SinceUtc, level.Untested, colour));
        }

        AddLine(this.asiaHigh, this.AsiaColor);
        AddLine(this.asiaLow, this.AsiaColor);
        AddLine(this.londonHigh, this.LondonColor);
        AddLine(this.londonLow, this.LondonColor);
        AddLine(this.nyHigh, this.NyColor);
        AddLine(this.nyLow, this.NyColor);

        this.levelDrawable = new LevelLineDrawable(lines.ToArray());
    }

    // ---- paint -------------------------------------------------------------------------------

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);

        var graphics = args?.Graphics;
        var window = this.CurrentChart?.MainWindow;
        if (graphics is null || window is null) return;

        var registry = new List<RectangleF>();

        try
        {
            this.orbOverlay.Draw(
                graphics, window, this.orbDrawable,
                new OrbBoxOverlay.Options(this.OrbBoxColor, this.OrbHighColor, this.OrbLowColor, this.OrbMidColor),
                registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"ORB box paint error: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.levelOverlay.Draw(graphics, window, this.levelDrawable, registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"Session level paint error: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
