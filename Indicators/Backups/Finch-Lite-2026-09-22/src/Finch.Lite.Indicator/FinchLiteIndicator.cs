using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using TradingPlatform.BusinessLayer;
using Qt = TradingPlatform.BusinessLayer;

namespace FinchLite;

/// <summary>
/// Finch-Lite: a deliberately minimal, single-purpose indicator, built completely fresh — not a
/// copy of, or port from, ORB-IX or Finch-Scalping, and no OrbIx.Core dependency at all (see the
/// project's own .csproj comment for why). Features are added ONE AT A TIME, each verified
/// working before the next starts, specifically so nothing here can ever accumulate the kind of
/// always-running-regardless-of-display-state machinery that made Finch-Scalping's chart slow.
///
/// FEATURE 1 (2026-09-22) — large resting orders: a horizontal line at any price currently
/// carrying a resting bid or ask size at or above a configurable threshold. Red for a bid (a
/// seller would have to hit it), green for an ask (a buyer would have to lift it) — the
/// operator's own explicit colour choice, not this codebase's usual bullish/bearish convention.
///
/// PULLED, NOT STREAMED. ORB-IX already measured (documented in its own OrbIxIndicator.cs,
/// PullBook's doc comment) that this connector's live Level2 EVENT stream arrives stamped
/// "generated_from_level1" — fabricated from the top-of-book touch because the connector has
/// nothing else to publish on that stream (19,762 such updates observed, zero real depth). The
/// platform's own PULL API, DepthOfMarket.GetDepthOfMarketAggregatedCollections, returns the
/// real book on the same connection. This indicator uses that pull from the start rather than
/// re-discovering the same trap independently.
///
/// Draws only. Places no orders, reads no account.
/// </summary>
public sealed class FinchLiteIndicator : Qt.Indicator
{
    // ---- lifecycle scaffolding --------------------------------------------------------------

    private Timer? retryTimer;
    private Timer? pollTimer;
    private Qt.Symbol? symbol;
    private string? overlayFault;

    private const int RetryIntervalMs = 1000;

    [InputParameter("Poll interval (ms)", 1, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; } = 250;

    // ---- large resting orders -----------------------------------------------------------------

    /// <summary>
    /// TWO thresholds instead of one, so a single fixed number does not have to fit both a thin
    /// overnight book and a busy NY day session (the operator's own request, 2026-09-22 —
    /// "normal size" on one is "nothing" on the other). Session windows are read in
    /// America/New_York wall-clock time, matching the boundaries ORB-IX's own configuration
    /// already established for these same names (config/orbix.share.json: RTH opens 09:30,
    /// ASIA opens 20:00) rather than inventing a different convention for this indicator alone.
    ///
    /// THE SPLIT IS EXHAUSTIVE, NOT THREE-WAY: "NY session" is 09:30-18:00 ET (RTH through to
    /// GLOBEX's own 18:00 reopen, per that same config), and "Asia session" is everything else —
    /// the overnight/Asia/London/Frankfurt stretch, 18:00-09:30 ET. The operator asked for two
    /// named options, not a third "other" bucket to also configure.
    /// </summary>
    [InputParameter("Large order: minimum size, NY session (contracts)", 10, 1, 100000, 1, 0)]
    public int LargeOrderMinSizeNy { get; set; } = 100;

    [InputParameter("Large order: minimum size, Asia session (contracts)", 11, 1, 100000, 1, 0)]
    public int LargeOrderMinSizeAsia { get; set; } = 50;

    private static readonly TimeSpan NySessionOpen = new(9, 30, 0);
    private static readonly TimeSpan NySessionClose = new(18, 0, 0);
    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>The threshold for whichever of the two named sessions the given UTC instant
    /// falls in — NY session is 09:30-18:00 ET, Asia/overnight is the rest of the day.</summary>
    private int ThresholdFor(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, SessionZone).TimeOfDay;
        var isNySession = local >= NySessionOpen && local < NySessionClose;
        return isNySession ? this.LargeOrderMinSizeNy : this.LargeOrderMinSizeAsia;
    }

    /// <summary>
    /// Raised from an original default of 50 (2026-09-22) — "what i need is to see all the
    /// levels of the order book not just a small snap shot" (the operator's own words). 500 is
    /// comfortably past what `GetDepthOfMarketAggregatedCollections` actually returns on this
    /// connector (ORB-IX measured 50 real prices a side at LevelsCount=50; asking for more than
    /// the platform has to give is harmless, it just returns what exists), so this reads as
    /// "give me everything" rather than a second, smaller cap layered on top of the platform's
    /// own ceiling.
    /// </summary>
    [InputParameter("Large order: levels to scan per side", 12, 5, 2000, 5, 0)]
    public int LevelsToScan { get; set; } = 500;

    // SWAPPED 2026-09-22 ("the colors are swaped the red should be on top and green on the
    // bottom") — asks always rest ABOVE price and bids always rest BELOW it; that ordering is the
    // order book's own structure, not something this indicator can rearrange. What the operator
    // actually wanted flipped was the COLOUR MEANING to match this codebase's usual convention
    // (green = bullish, red = bearish) instead of the original "red = bid, green = ask" the
    // operator specified when this feature was first built: ask (top, resistance) now defaults
    // red, bid (bottom, support) now defaults green.
    [InputParameter("Large order: bid colour", 13)]
    public Color BidColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Large order: ask colour", 14)]
    public Color AskColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    /// <summary>
    /// Session boundary for the "remembered for the whole trading day" behaviour below — 18:00
    /// America/New_York, matching GLOBEX's own reopen and the same convention this project
    /// already uses for the NY/Asia threshold split above.
    /// </summary>
    private static readonly TimeSpan TradingDayOpen = new(18, 0, 0);

    /// <summary>The most recent 18:00 ET at or before the given UTC instant.</summary>
    private static DateTime TradingDayStart(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, SessionZone);
        var openToday = local.Date + TradingDayOpen;
        var open = local.TimeOfDay >= TradingDayOpen ? openToday : openToday.AddDays(-1);
        return TimeZoneInfo.ConvertTimeToUtc(open, SessionZone);
    }

    /// <summary>
    /// Remembers every level that EVER cleared the threshold today, keyed by price+side, so a
    /// large order still draws its line for the rest of the trading day even after it is pulled
    /// or filled — "for the large orders i define it makes the line extend so i can see them
    /// easily" (the operator's own words, 2026-09-22). The size KEPT is the largest ever seen at
    /// that level, not whatever it currently is — a level remembered because it once carried 800
    /// contracts should still say 800 after it thins out, not silently relabel itself with
    /// whatever is left.
    /// </summary>
    private readonly Dictionary<(double Price, bool IsBid), RestingOrderDraw> restingOrderMemory = new();
    private DateTime tradingDayStartUtc;

    private readonly RestingOrderOverlay restingOrderOverlay = new();
    private volatile RestingOrderDrawable restingOrderDrawable = RestingOrderDrawable.Empty;

    // ---- DOM ladder (feature 2, 2026-09-22) ---------------------------------------------------
    //
    // "is there a way to show like a dom on the right hand side thats a bar so i can tell all
    // resting orders" — the large-order lines above only ever show levels that cleared the
    // threshold; this shows EVERY level currently scanned, as a bar-length comparison, so the
    // operator can read the full depth picture rather than only the highlighted extremes.
    [InputParameter("DOM ladder: enable", 20)]
    public bool DomLadderEnabled { get; set; } = true;

    [InputParameter("DOM ladder: strip width (px)", 21, 20, 400, 10, 0)]
    public int DomLadderWidth { get; set; } = 150;

    [InputParameter("DOM ladder: row height (px)", 22, 1, 20, 1, 0)]
    public int DomLadderRowHeight { get; set; } = 4;

    private readonly DomLadderOverlay domLadderOverlay = new();
    private volatile DomLadderDrawable domLadderDrawable = DomLadderDrawable.Empty;

    // ---- big trades (feature 3, 2026-09-22) ---------------------------------------------------
    //
    // "a long bar that comes out and makes a line on the chart were on the specified value for
    // large trades so i have a super clean line knowing were price would react off of" — a
    // DIFFERENT trigger from features 1/2 above: those read the resting book (what's SITTING
    // there right now); this reads the TAPE (what already TRADED) and marks the price a big fill
    // actually went off at, as a persisting reference level. Same red/green language as the
    // resting-order lines — green for a buy (lifted the ask), red for a sell (hit the bid) — so
    // one colour convention covers this whole indicator.
    [InputParameter("Big trade: minimum size (contracts)", 30, 1, 100000, 1, 0)]
    public int BigTradeMinSize { get; set; } = 50;

    /// <summary>How many big-trade lines to keep on screen at once — "a super clean line" was the
    /// operator's own ask, and a line that never expires eventually stops being clean.</summary>
    [InputParameter("Big trade: max lines kept", 31, 1, 100, 1, 0)]
    public int BigTradeMaxKept { get; set; } = 10;

    // OWN colours, deliberately NOT reusing BidColor/AskColor above — those two govern the
    // resting-book side (which can be swapped independently, as they were 2026-09-22) and buy/
    // sell here is a different axis (the aggressor, not which side of the book it rested on).
    // Green = buy (bullish), red = sell (bearish) — this codebase's usual convention.
    [InputParameter("Big trade: buy colour", 32)]
    public Color BigTradeBuyColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Big trade: sell colour", 33)]
    public Color BigTradeSellColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    private readonly ConcurrentQueue<BigTradeDraw> bigTradeQueue = new();
    private readonly List<BigTradeDraw> bigTrades = new();
    private readonly BigTradeOverlay bigTradeOverlay = new();
    private volatile BigTradeDrawable bigTradeDrawable = BigTradeDrawable.Empty;

    public FinchLiteIndicator()
    {
        this.Name = "Finch-Lite";
        this.Description =
            "Minimal, single-purpose indicator built from scratch (2026-09-22) to stay fast — "
            + "one feature at a time, nothing running that isn't currently shown. First feature: "
            + "large resting bid/ask orders, pulled from the platform's own depth-of-market "
            + "snapshot. Draws only, places no orders.";
        this.SeparateWindow = false;
    }

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
            if (this.pollTimer is not null)
                return; // already succeeded on an earlier tick of this same timer

            if (this.TryInitialise())
            {
                this.retryTimer?.Dispose();
                this.retryTimer = null;
            }
        }
    }

    /// <summary>
    /// True once the symbol is attached AND its DepthOfMarket is available — both can be
    /// unpopulated for a moment right after attach, the same startup race ORB-IX's own OnInit
    /// already documented and Finch-Scalping's rebuild had to relearn the hard way (2026-09-18):
    /// trying exactly once and giving up permanently reads as "isn't displaying anything" with no
    /// diagnostic to go on, for what is actually just a timing race that resolves on its own.
    /// </summary>
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

            if (symbol.DepthOfMarket is null)
            {
                this.overlayFault = "Waiting for the symbol to publish a depth-of-market feed...";
                return false;
            }

            this.overlayFault = null;
            this.symbol = symbol;

            symbol.NewLast += this.OnLast;

            var interval = Math.Max(this.PollIntervalMs, 50);
            this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);
            return true;
        }
        catch (Exception ex)
        {
            this.overlayFault = $"Finch-Lite failed to start: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// §10-style discipline: no work on the market-data thread beyond a comparison and an
    /// enqueue. The queue is drained on the poll timer, which already runs periodically for the
    /// DOM pull, rather than building a second timer just for this.
    /// </summary>
    private void OnLast(Qt.Symbol symbol, Last last)
    {
        if (last is null || last.Size < this.BigTradeMinSize)
            return;

        var isBuy = last.AggressorFlag == AggressorFlag.Buy;

        // Unclassified prints are not marked — a line implying "buyers did this" or "sellers did
        // this" for a fill the feed itself could not attribute to a side would be a guess dressed
        // up as a fact.
        if (last.AggressorFlag != AggressorFlag.Buy && last.AggressorFlag != AggressorFlag.Sell)
            return;

        this.bigTradeQueue.Enqueue(new BigTradeDraw(last.Price, last.Size, isBuy));
    }

    protected override void OnClear()
    {
        this.retryTimer?.Dispose();
        this.retryTimer = null;

        this.pollTimer?.Dispose();
        this.pollTimer = null;

        if (this.symbol is { } subscribed)
            subscribed.NewLast -= this.OnLast;

        this.symbol = null;
        this.restingOrderDrawable = RestingOrderDrawable.Empty;
        this.restingOrderMemory.Clear();
        this.tradingDayStartUtc = default;
        this.domLadderDrawable = DomLadderDrawable.Empty;
        this.bigTradeQueue.Clear();
        this.bigTrades.Clear();
        this.bigTradeDrawable = BigTradeDrawable.Empty;
        this.overlayFault = null;
    }

    // ---- the poll -----------------------------------------------------------------------------

    /// <summary>
    /// Reported once per distinct fault, not every poll — the poll runs several times a second,
    /// and a fault that logged every tick would say nothing a single line does not already say.
    /// </summary>
    private string? lastPollFault;

    private void OnPollTimer(object? state)
    {
        var symbol = this.symbol;
        if (symbol is null)
            return;

        this.DrainBigTrades();

        try
        {
            var market = symbol.DepthOfMarket;

            if (market is null)
            {
                this.ReportPollFault("the symbol no longer exposes a depth-of-market feed");
                return;
            }

            var book = market.GetDepthOfMarketAggregatedCollections(
                new GetDepthOfMarketParameters
                {
                    GetLevel2ItemsParameters = new GetLevel2ItemsParameters
                    {
                        LevelsCount = this.LevelsToScan,
                        GetMBOItems = false,
                    },
                });

            if (book is null)
            {
                this.ReportPollFault("the depth-of-market call returned nothing");
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var threshold = this.ThresholdFor(nowUtc);
            this.RememberLargeLevels(nowUtc, book.Bids, threshold, isBid: true);
            this.RememberLargeLevels(nowUtc, book.Asks, threshold, isBid: false);

            this.lastPollFault = null;
            this.overlayFault = null;
            this.restingOrderDrawable = new RestingOrderDrawable(
                new List<RestingOrderDraw>(this.restingOrderMemory.Values).ToArray());
            this.domLadderDrawable = BuildLadder(book.Bids, book.Asks);
        }
        catch (Exception ex)
        {
            this.ReportPollFault($"the poll threw ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Rolls <see cref="restingOrderMemory"/) over at the trading-day boundary, then folds this
    /// poll's qualifying levels into it — keeping whichever size is LARGER between what is
    /// already remembered for that price+side and what was just seen, per the "remembers the
    /// peak, not the current" design stated on the field itself.
    /// </summary>
    private void RememberLargeLevels(DateTime nowUtc, Level2Item[]? items, int threshold, bool isBid)
    {
        var dayStart = TradingDayStart(nowUtc);
        if (dayStart != this.tradingDayStartUtc)
        {
            this.tradingDayStartUtc = dayStart;
            this.restingOrderMemory.Clear();
        }

        if (items is null)
            return;

        foreach (var item in items)
        {
            if (item.Size < threshold)
                continue;

            var key = (item.Price, isBid);

            if (this.restingOrderMemory.TryGetValue(key, out var existing) && existing.Size >= item.Size)
                continue;

            this.restingOrderMemory[key] = new RestingOrderDraw(item.Price, item.Size, isBid);
        }
    }

    /// <summary>Every scanned level on both sides, normalised against the single largest size
    /// currently on the book — bar LENGTH is a comparison, so it needs one shared scale rather
    /// than a scale per side (which would make an equal bid and ask look different lengths).</summary>
    private DomLadderDrawable BuildLadder(Level2Item[]? bids, Level2Item[]? asks)
    {
        if (!this.DomLadderEnabled)
            return DomLadderDrawable.Empty;

        var count = (bids?.Length ?? 0) + (asks?.Length ?? 0);
        if (count == 0)
            return DomLadderDrawable.Empty;

        var bars = new DomBarDraw[count];
        var i = 0;
        var maxSize = 0d;

        if (bids is not null)
        {
            foreach (var item in bids)
            {
                bars[i++] = new DomBarDraw(item.Price, item.Size, IsBid: true);
                if (item.Size > maxSize) maxSize = item.Size;
            }
        }

        if (asks is not null)
        {
            foreach (var item in asks)
            {
                bars[i++] = new DomBarDraw(item.Price, item.Size, IsBid: false);
                if (item.Size > maxSize) maxSize = item.Size;
            }
        }

        return new DomLadderDrawable(bars, maxSize);
    }

    /// <summary>Moves whatever the tick handler queued onto the list the paint actually reads,
    /// on the poll timer rather than the market-data thread — the queue exists exactly so
    /// <see cref="OnLast"/> never has to touch <see cref="bigTrades"/> directly.</summary>
    private void DrainBigTrades()
    {
        var changed = false;

        while (this.bigTradeQueue.TryDequeue(out var trade))
        {
            this.bigTrades.Add(trade);
            changed = true;
        }

        if (!changed)
            return;

        if (this.bigTrades.Count > this.BigTradeMaxKept)
            this.bigTrades.RemoveRange(0, this.bigTrades.Count - this.BigTradeMaxKept);

        this.bigTradeDrawable = new BigTradeDrawable(this.bigTrades.ToArray());
    }

    private void ReportPollFault(string reason)
    {
        if (string.Equals(this.lastPollFault, reason, StringComparison.Ordinal))
            return;

        this.lastPollFault = reason;
        this.overlayFault = reason;
    }

    // ---- paint --------------------------------------------------------------------------------

    private readonly Font faultFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush faultBrush = new(Color.FromArgb(0xFF, 0xC1, 0x07));
    private readonly SolidBrush faultBack = new(Color.FromArgb(190, 16, 18, 24));

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);

        var graphics = args?.Graphics;
        var window = this.CurrentChart?.MainWindow;

        if (graphics is null || window is null)
            return;

        if (this.overlayFault is { Length: > 0 } fault)
        {
            var pane = window.ClientRectangle;
            var size = graphics.MeasureString(fault, this.faultFont);
            var rect = new RectangleF(pane.Left + 4f, pane.Top + 24f, size.Width + 8f, size.Height + 4f);
            graphics.FillRectangle(this.faultBack, rect);
            graphics.DrawString(fault, this.faultFont, this.faultBrush, rect.Left + 4f, rect.Top + 2f);
        }

        // ONE shared registry for every overlay's labels below, so a resting-order label and a
        // big-trade label landing at the same spot also respect each other, not just labels
        // within one overlay's own set.
        var registry = new List<RectangleF>();

        // The ladder draws FIRST, underneath — it is the quiet full-depth backdrop, and the
        // large-order lines are the louder, highlighted signal that should sit on top of it.
        try
        {
            this.domLadderOverlay.Draw(
                graphics, window, this.domLadderDrawable,
                new DomLadderOverlay.Options(
                    this.BidColor, this.AskColor, this.DomLadderWidth, this.DomLadderRowHeight));
        }
        catch (Exception ex)
        {
            this.overlayFault = $"The DOM ladder overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.restingOrderOverlay.Draw(
                graphics, window, this.restingOrderDrawable,
                new RestingOrderOverlay.Options(this.BidColor, this.AskColor), registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"The resting-order overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.bigTradeOverlay.Draw(
                graphics, window, this.bigTradeDrawable,
                new BigTradeOverlay.Options(this.BigTradeBuyColor, this.BigTradeSellColor), registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"The big-trade overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public override void Dispose()
    {
        this.retryTimer?.Dispose();
        this.retryTimer = null;

        this.pollTimer?.Dispose();
        this.pollTimer = null;

        this.restingOrderOverlay.Dispose();
        this.domLadderOverlay.Dispose();
        this.bigTradeOverlay.Dispose();
        this.faultFont.Dispose();
        this.faultBrush.Dispose();
        this.faultBack.Dispose();

        base.Dispose();
    }
}
