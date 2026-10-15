using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
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
/// Draws only. Places no orders, reads no account.
/// </summary>
public sealed class SwingLevelsIndicator : Qt.Indicator
{
    private const int RetryIntervalMs = 1000;

    private Timer? retryTimer;
    private Timer? pollTimer;
    private Qt.Symbol? symbol;
    private string? overlayFault;
    private HistoricalData? history;
    private int barsSeen;

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

    public override void Dispose()
    {
        this.retryTimer?.Dispose();
        this.pollTimer?.Dispose();
        this.history?.Dispose();
        this.overlay.Dispose();
        base.Dispose();
    }

    private void OnPollTimer(object? state)
    {
        try
        {
            this.DrainNewBars();
            this.RebuildDrawable();
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
    }

    private void DrainNewBars()
    {
        if (this.engine is not { } eng || this.history is not { Count: > 1 } h) return;

        var closedUpTo = h.Count - 1;
        for (var i = this.barsSeen; i < closedUpTo; i++)
        {
            if (TryReadBar(h, i, out var timeUtc, out var high, out var low))
                eng.FeedBar(timeUtc, high, low);
        }

        this.barsSeen = closedUpTo;
    }

    // ---- drawable rebuild ------------------------------------------------------------------------

    private void RebuildDrawable()
    {
        if (this.engine is not { } eng) return;

        var lines = new List<SwingLevelDraw>(eng.Levels.Count);
        foreach (var lvl in eng.Levels)
        {
            var untested = lvl.TouchedUtc is null;
            if (!untested && !this.ShowTestedLevels) continue;

            var colour = lvl.IsHigh ? this.ResistanceColor : this.SupportColor;
            lines.Add(new SwingLevelDraw(lvl.IsHigh, lvl.Price, lvl.ConfirmedUtc, untested, colour));
        }

        this.drawable = new SwingLevelDrawable(lines.ToArray());
    }

    // ---- paint -------------------------------------------------------------------------------

    private readonly Font faultFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush faultBrush = new(Color.FromArgb(0xFF, 0xC1, 0x07));
    private readonly SolidBrush faultBack = new(Color.FromArgb(190, 16, 18, 24));

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);

        var graphics = args?.Graphics;
        var window = this.CurrentChart?.MainWindow;
        if (graphics is null || window is null) return;

        if (this.overlayFault is { Length: > 0 } fault)
        {
            var pane = window.ClientRectangle;
            var size = graphics.MeasureString(fault, this.faultFont);
            var rect = new RectangleF(pane.Left + 4f, pane.Top + 4f, size.Width + 8f, size.Height + 4f);
            graphics.FillRectangle(this.faultBack, rect);
            graphics.DrawString(fault, this.faultFont, this.faultBrush, rect.Left + 4f, rect.Top + 2f);
        }

        var registry = new List<RectangleF>();

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
