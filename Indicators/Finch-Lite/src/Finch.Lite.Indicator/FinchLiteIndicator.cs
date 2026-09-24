using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
    /// "the strength of the color of the bids based on asorbstion were lets say sellers are
    /// defending or an area were buyers are defending" (the operator's own ask, 2026-09-22) —
    /// how many contracts <see cref="restingOrderAbsorbed"/> has to reach before a level's line
    /// draws at full colour strength. A level that just qualified (nothing traded through it
    /// yet) draws faint; one that has stood through this many contracts while still holding
    /// draws fully saturated — the stronger the colour, the harder that side is defending.
    /// </summary>
    [InputParameter("Large order: absorption colour-strength scale (contracts)", 15, 1, 1000000, 1, 0)]
    public int AbsorptionStrongContracts { get; set; } = 200;

    // REVERTED 2026-09-22 (same day) — "lets color the unfinished auction line the acording
    // color like we do for the large orders": the dedicated white UnfinishedAuctionColor from
    // earlier today is gone; unfinished auctions now draw in the same BidColor/AskColor as an
    // ordinary large order (index 16 retired rather than reused, so any saved workspace that
    // still has a value there just gets ignored, not silently reinterpreted as something else).

    /// <summary>
    /// REDEFINED 2026-09-22 (same day) — "unfinished auctions work were price moved past a price
    /// fast and left orders behind that is what is considered a unfinished auction" (the
    /// operator's own definition, replacing an earlier "current size dropped below its own peak"
    /// trigger that flagged near-constant book noise as unfinished — see the screenshot review
    /// that led here: dozens of 2-19-contract "UA" levels while price sat still). A level now
    /// reads as unfinished once CURRENT MARKET PRICE has moved at least this many ticks past the
    /// level's own price, in the direction that would have consumed it (below a bid, above an
    /// ask) — while the level is STILL RESTING (any size &gt; 0). The size no longer has to have
    /// dropped at all: "left orders behind" means exactly that, orders still sitting there,
    /// untouched or not, once price has moved on. See <see cref="ReconcileRestingLevels"/> for
    /// the actual distance calculation, using the same current-book snapshot the poll already
    /// pulled.
    /// </summary>
    [InputParameter("Unfinished auction: price must move past by (ticks)", 17, 1, 100000, 1, 0)]
    public int UnfinishedDistanceTicks { get; set; } = 8;

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
    /// The PEAK size ever seen at each level that cleared the threshold today, keyed by
    /// price+side — a large order still draws its line for the rest of the trading day even
    /// after some of it trades off, per "for the large orders i define it makes the line extend
    /// so i can see them easily" (the operator's own words, 2026-09-22).
    ///
    /// REDESIGNED 2026-09-22 (same day) to also resolve two follow-up asks: "once a bid has been
    /// filled i need it to disapear from the chart" and "if its an unfinished auction it needs
    /// to label that and show how many more contracts are at that unfinished auction". This
    /// dictionary now holds ONLY the peak — never what to display — because "unfinished" status
    /// is a comparison between this peak and the level's CURRENT size in the live book, redone
    /// fresh every poll in <see cref="ReconcileRestingLevels"/>: gone entirely from the book (or
    /// down to zero) removes the entry outright (filled/cancelled — the book alone cannot tell
    /// those apart, and either way nothing is left resting there to mark); still present but
    /// below peak draws as UNFINISHED with the REMAINING size; still present at or above peak
    /// draws normally and the peak is raised to match.
    /// </summary>
    private readonly Dictionary<(double Price, bool IsBid), double> restingOrderPeaks = new();

    /// <summary>The size actually observed at each tracked level on the PREVIOUS poll — the only
    /// way to tell "this level just got hit" from "this level just got bigger" is to compare
    /// against what was there last time, not against the peak.</summary>
    private readonly Dictionary<(double Price, bool IsBid), double> restingOrderLastSize = new();

    /// <summary>Running total of contracts that have traded through each tracked level while it
    /// kept standing — see <see cref="RestingOrderDraw.Absorbed"/> for the full design. Cleared
    /// alongside <see cref="restingOrderPeaks"/> at the trading-day boundary and whenever a level
    /// disappears from the book entirely (a level that comes back later is a NEW level, not a
    /// continuation of one already fully filled).</summary>
    private readonly Dictionary<(double Price, bool IsBid), double> restingOrderAbsorbed = new();

    /// <summary>The poll timestamp each currently-tracked level was FIRST flagged — "centered
    /// just to the right of the candle it comes off of" (the operator's own ask, 2026-09-22, for
    /// unfinished auctions specifically): a line drawn full-pane-width from a fixed screen edge
    /// says nothing about WHEN the level was noticed, while a line starting at its own origin
    /// time and extending right (the same "still extending" convention this codebase already
    /// uses for zones that remain live) reads as "this began here." Cleared alongside
    /// <see cref="restingOrderPeaks"/> for the same reasons.</summary>
    private readonly Dictionary<(double Price, bool IsBid), DateTime> restingOrderFirstSeenUtc = new();

    /// <summary>
    /// FIXED 2026-09-22 — "why is the unflished auction levels moving they should be static
    /// lines": a level briefly missing from ONE poll's returned depth snapshot (the platform's
    /// own pull is not perfectly stable poll to poll for deeper levels, independent of anything
    /// actually trading) was removed outright and, the moment it reappeared, re-added as a brand
    /// new level — resetting <see cref="restingOrderFirstSeenUtc"/> to that instant, which walked
    /// the line's own origin rightward every time it happened. Counts CONSECUTIVE misses per
    /// level; only removal past <see cref="MissingPollGrace"/> counts as genuinely gone. Cleared
    /// on removal, and whenever the level is seen again (a miss streak does not carry across a
    /// good poll in between).
    /// </summary>
    private readonly Dictionary<(double Price, bool IsBid), int> restingOrderMissingPolls = new();

    /// <summary>Consecutive polls a level may go missing from the returned book before it is
    /// actually removed — see <see cref="restingOrderMissingPolls"/>.</summary>
    private const int MissingPollGrace = 2;

    /// <summary>
    /// "i also need a setting to allow me to filter out the size of the unfinished auctions so if
    /// they are low then i dont need to see them like the large bid and ask orders" (the
    /// operator's own ask, 2026-09-22) — a DISPLAY-only floor on the REMAINING size once a level
    /// reads as unfinished. The level keeps being tracked (peak, absorption, origin time) even
    /// while filtered out here; this only decides whether it is worth drawing at its current,
    /// possibly small, remaining size. 0 (default) shows every unfinished auction, same as
    /// before this setting existed.
    /// </summary>
    [InputParameter("Large order: hide unfinished auctions below (contracts)", 18, 0, 100000, 1, 0)]
    public int UnfinishedMinRemainingSize { get; set; } = 0;

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

    /// <summary>
    /// FIXED 2026-09-23 — "now my dom on the right is super small on the order size": bars used to
    /// scale against the single largest size anywhere in the scanned book that poll, so one rare
    /// outlier (a 500+ contract level) squashed every ordinary level down to a sliver. Fixed
    /// reference size now — a level at or above this fills the strip fully; anything bigger just
    /// clips instead of shrinking everyone else's scale.
    /// </summary>
    [InputParameter("DOM ladder: size that fills the strip (contracts)", 23, 1, 1000000, 1, 0)]
    public int DomLadderFillSize { get; set; } = 150;

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

    // ---- order blocks (feature 5, 2026-09-23) --------------------------------------------------
    //
    // "i would like to see the 15minute order blocks and 1hr order blocks that are labeled" — TWO
    // independent higher-timeframe historical pulls, live-subscribed via Symbol.GetHistory (kept
    // up to date by the platform itself, not by re-polling), completely separate from whatever
    // period the CHART itself is showing. See OrderBlockEngine's own doc comment for the detection
    // and invalidation rules — both explicit choices the operator made when asked.
    [InputParameter("Order blocks: enable 15m", 50)]
    public bool OrderBlock15mEnabled { get; set; } = true;

    [InputParameter("Order blocks: enable 1h", 51)]
    public bool OrderBlock1hEnabled { get; set; } = true;

    /// <summary>Bars required on EACH side of a candidate before it counts as a confirmed swing
    /// high/low — see OrderBlockEngine. Higher = fewer, more significant swings; lower = more,
    /// noisier ones.</summary>
    [InputParameter("Order blocks: swing pivot lookback (bars)", 52, 1, 20, 1, 0)]
    public int OrderBlockPivotLookback { get; set; } = 2;

    [InputParameter("Order blocks: bullish colour", 53)]
    public Color OrderBlockBullishColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Order blocks: bearish colour", 54)]
    public Color OrderBlockBearishColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    /// <summary>How far back each higher-timeframe pull loads on attach — bounds both API load and
    /// how much backlog gets fed into the engine in one burst on the first poll after init.</summary>
    [InputParameter("Order blocks: lookback (days)", 55, 1, 90, 1, 0)]
    public int OrderBlockLookbackDays { get; set; } = 10;

    private HistoricalData? ob15mHistory;
    private HistoricalData? ob1hHistory;
    private int ob15mBarsSeen;
    private int ob1hBarsSeen;
    private OrderBlockEngine? ob15mEngine;
    private OrderBlockEngine? ob1hEngine;
    private readonly StructureBoxOverlay orderBlockOverlay = new();
    private volatile StructureBoxDrawable orderBlockDrawable = StructureBoxDrawable.Empty;

    // ---- inverse fair value gaps (feature 6, 2026-09-23) ---------------------------------------
    //
    // "the ability to see inverse fairvalue gaps" — fed the CHART's own closed bars (the
    // operator's own choice, independent of the 15m/1h order blocks above). See
    // FairValueGapEngine's own doc comment for the detection/inversion rules.
    [InputParameter("Inverse FVG: enable", 60)]
    public bool InverseFvgEnabled { get; set; } = true;

    [InputParameter("Inverse FVG: bullish colour", 61)]
    public Color InverseFvgBullishColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Inverse FVG: bearish colour", 62)]
    public Color InverseFvgBearishColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    private readonly FairValueGapEngine fvgEngine = new();

    /// <summary>-1 means "not yet seeded" — the first poll after attach caps how far back into
    /// the chart's OWN already-loaded history it backfills (see DrainStructure), rather than
    /// walking however many bars the chart happens to have loaded, which could be years of data
    /// on a long-running chart. Everything after that first seed processes incrementally, same as
    /// the 15m/1h series.</summary>
    private int chartBarsSeen = -1;

    private const int MaxInitialChartBacklogBars = 500;

    private readonly StructureBoxOverlay inverseFvgOverlay = new();
    private volatile StructureBoxDrawable inverseFvgDrawable = StructureBoxDrawable.Empty;

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

    /// <summary>
    /// A do-nothing handler, subscribed purely for its SIDE EFFECT.
    ///
    /// FOUND 2026-09-22: "the dom on the right some how got removed" after the operator removed
    /// ORB-IX from the same chart, restored the instant they added ORB-IX back — even though
    /// Finch-Lite shares zero code or data with it. ORB-IX's own OnLevel2 handler subscribes to
    /// `Symbol.NewLevel2` for reasons that have nothing to do with Finch-Lite (ORB-IX's own flow
    /// ladder), but `NewLevel2 +=` itself calls `SubscribeAction(Level2)` on the platform — read
    /// in the decompiled assembly by ORB-IX's own investigation into this exact API (see
    /// OrbIxIndicator.cs's own PullBook doc comment, 2026-09-14) — and THAT subscription is what
    /// tells Quantower to keep requesting/maintaining live depth for the symbol at all. The PULL
    /// API this indicator relies on (GetDepthOfMarketAggregatedCollections) reads from that same
    /// maintained depth, not an independent source: with no Level2 subscriber anywhere on the
    /// chart, the platform had nothing to keep polling from the connector, and the pull started
    /// returning a genuinely empty book — not a bug in this indicator's own logic, but Finch-Lite
    /// depending on another indicator's subscription as an unstated, invisible prerequisite.
    ///
    /// The event's OWN payload is still not trusted for anything (ORB-IX separately measured
    /// this connector's live Level2 EVENT stream as fabricated from the top-of-book touch,
    /// "generated_from_level1" — see RestingOrderOverlay/DomLadderOverlay's own doc comments).
    /// This handler exists ONLY to hold the subscription open; it deliberately reads nothing
    /// from `level2`/`dom`.
    /// </summary>
    private void OnLevel2(Qt.Symbol symbol, Level2Quote level2, DOMQuote dom)
    {
    }

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
            symbol.NewLevel2 += this.OnLevel2;

            this.TryStartOrderBlocks(symbol);

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
    /// Best-effort — deliberately does NOT propagate a failure up to fail the whole indicator's
    /// init. Order blocks are additive on top of Features 1-4; a connector that cannot supply
    /// 15m/1h aggregated history for some reason should not take DOM/tape reading down with it.
    /// A failure here is surfaced (briefly, on the next fault report) but never retried forever
    /// the way TryInitialise's own required preconditions are.
    /// </summary>
    private void TryStartOrderBlocks(Qt.Symbol symbol)
    {
        var lookback = DateTime.UtcNow.AddDays(-Math.Max(1, this.OrderBlockLookbackDays));

        if (this.OrderBlock15mEnabled && this.ob15mHistory is null)
        {
            try
            {
                this.ob15mHistory = symbol.GetHistory(Period.MIN15, symbol.HistoryType, lookback);
                this.ob15mEngine = new OrderBlockEngine(this.OrderBlockPivotLookback);
                this.ob15mBarsSeen = 0;
            }
            catch (Exception ex)
            {
                this.overlayFault = $"15m order blocks unavailable: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.OrderBlock1hEnabled && this.ob1hHistory is null)
        {
            try
            {
                this.ob1hHistory = symbol.GetHistory(Period.HOUR1, symbol.HistoryType, lookback);
                this.ob1hEngine = new OrderBlockEngine(this.OrderBlockPivotLookback);
                this.ob1hBarsSeen = 0;
            }
            catch (Exception ex)
            {
                this.overlayFault = $"1h order blocks unavailable: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// §10-style discipline: no work on the market-data thread beyond a comparison and an
    /// enqueue. The queue is drained on the poll timer, which already runs periodically for the
    /// DOM pull, rather than building a second timer just for this.
    /// </summary>
    private void OnLast(Qt.Symbol symbol, Last last)
    {
        if (last is null)
            return;

        // Prints that carry no evidence either way (unusable quote, or strictly inside the
        // spread) feed NEITHER queue below — a line implying "buyers did this" or "sellers did
        // this" for a fill nothing could attribute to a side would be a guess dressed up as a
        // fact.
        if (!TryClassify(symbol, last, out var isBuy))
            return;

        if (last.Size >= this.BigTradeMinSize)
            this.bigTradeQueue.Enqueue(new BigTradeDraw(last.Price, last.Size, isBuy, last.Time));
    }

    /// <summary>
    /// Classifies a print's aggressor side for <see cref="BigTradeOverlay"/> — trusting only
    /// <see cref="Last.AggressorFlag"/> and dropping everything else left prints unclassified far
    /// more often than expected on some feeds (found 2026-09-22 while this fed a now-removed
    /// delta panel too, on MGC/Rithmic — most prints on that feed apparently don't come flagged
    /// Buy/Sell at all). This codebase already found and named this exact class of problem:
    /// ORB-IX's own `AggressorConvention` (`OrbIx.Core/Features/AggressorConvention.cs`) measured
    /// that a vendor's aggressor flag can be missing, sparse, or even INVERTED, and the fix is to
    /// classify a print from its own GEOMETRY against the quote rather than trust a flag that
    /// might not be there — a print at or above the ask was taken by a buyer lifting the offer, a
    /// print at or below the bid was hit into a resting bid, inclusive on both touches (the
    /// overwhelmingly common case; a strict cross would discard nearly every print). The feed's
    /// own flag is still trusted FIRST when it says Buy or Sell outright; this fallback only fires
    /// when it says neither. A print strictly inside the spread, or a quote that is missing/
    /// locked/crossed, still carries no evidence and is still dropped, not guessed.
    /// </summary>
    private static bool TryClassify(Qt.Symbol symbol, Last last, out bool isBuy)
    {
        if (last.AggressorFlag == AggressorFlag.Buy) { isBuy = true; return true; }
        if (last.AggressorFlag == AggressorFlag.Sell) { isBuy = false; return true; }

        isBuy = false;

        var bid = symbol.Bid;
        var ask = symbol.Ask;

        if (!double.IsFinite(bid) || !double.IsFinite(ask) || bid <= 0 || ask <= 0 || bid >= ask)
            return false;

        var takenByBuyer = last.Price >= ask;
        var takenBySeller = last.Price <= bid;

        if (takenByBuyer == takenBySeller)
            return false; // strictly inside the spread (or the touch matched neither) — no evidence

        isBuy = takenByBuyer;
        return true;
    }

    protected override void OnClear()
    {
        this.retryTimer?.Dispose();
        this.retryTimer = null;

        this.pollTimer?.Dispose();
        this.pollTimer = null;

        if (this.symbol is { } subscribed)
        {
            subscribed.NewLast -= this.OnLast;
            subscribed.NewLevel2 -= this.OnLevel2;
        }

        this.symbol = null;
        this.restingOrderDrawable = RestingOrderDrawable.Empty;
        this.restingOrderPeaks.Clear();
        this.restingOrderLastSize.Clear();
        this.restingOrderAbsorbed.Clear();
        this.restingOrderFirstSeenUtc.Clear();
        this.restingOrderMissingPolls.Clear();
        this.tradingDayStartUtc = default;
        this.domLadderDrawable = DomLadderDrawable.Empty;
        this.bigTradeQueue.Clear();
        this.bigTrades.Clear();
        this.bigTradeDrawable = BigTradeDrawable.Empty;

        this.ob15mHistory?.Dispose();
        this.ob15mHistory = null;
        this.ob1hHistory?.Dispose();
        this.ob1hHistory = null;
        this.ob15mEngine = null;
        this.ob1hEngine = null;
        this.ob15mBarsSeen = 0;
        this.ob1hBarsSeen = 0;
        this.orderBlockDrawable = StructureBoxDrawable.Empty;

        this.fvgEngine.Reset();
        this.chartBarsSeen = -1;
        this.inverseFvgDrawable = StructureBoxDrawable.Empty;

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

        // Own try/catch, deliberately separate from the DOM-pull block below: an exception
        // escaping a Timer callback entirely is unhandled and terminates the whole platform
        // process (the same reason ORB-IX's own fold is wrapped) — order blocks/IFVG touch more
        // platform history-data surface than anything else on this timer, and a failure here must
        // never take Features 1-4's own DOM/tape reading down with it.
        try
        {
            this.DrainStructure();
        }
        catch (Exception ex)
        {
            this.overlayFault = $"Order blocks/IFVG failed: {ex.GetType().Name}: {ex.Message}";
        }

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

            // FIXED 2026-09-22: a call that succeeds but comes back with an EMPTY book (zero
            // bids AND zero asks) previously produced empty drawables with no diagnostic at
            // all — indistinguishable from "the market is quiet" and from "the feed dried up",
            // and from "something in this indicator broke silently". Reported the same way
            // every other poll problem is, so at least this case is visible instead of a chart
            // that just looks clean with nothing on it and no way to tell why.
            var bidCount = book.Bids?.Length ?? 0;
            var askCount = book.Asks?.Length ?? 0;

            if (bidCount == 0 && askCount == 0)
            {
                this.ReportPollFault("the depth-of-market call returned an empty book (0 bids, 0 asks)");
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var threshold = this.ThresholdFor(nowUtc);
            this.restingOrderDrawable = this.ReconcileRestingLevels(nowUtc, book.Bids, book.Asks, threshold);

            this.lastPollFault = null;
            this.overlayFault = null;
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
    /// <summary>
    /// Reconciles <see cref="restingOrderPeaks"/> against the CURRENT book on both sides, then
    /// builds the drawable from the result — see the field's own doc comment for the full
    /// design. Collect-then-apply throughout rather than mutating a `Dictionary` mid-enumeration
    /// (updating an existing key's VALUE is safe to do during enumeration in practice, but this
    /// does not rely on that — removals are never safe, so neither path does).
    /// </summary>
    private RestingOrderDrawable ReconcileRestingLevels(
        DateTime nowUtc, Level2Item[]? bids, Level2Item[]? asks, int threshold)
    {
        var dayStart = TradingDayStart(nowUtc);
        if (dayStart != this.tradingDayStartUtc)
        {
            this.tradingDayStartUtc = dayStart;
            this.restingOrderPeaks.Clear();
            this.restingOrderLastSize.Clear();
            this.restingOrderAbsorbed.Clear();
            this.restingOrderFirstSeenUtc.Clear();
            this.restingOrderMissingPolls.Clear();
        }

        var currentBid = ToLookup(bids);
        var currentAsk = ToLookup(asks);

        // "unfinished auctions work were price moved past a price fast and left orders behind"
        // (the operator's own definition, 2026-09-22) — current market price, read from the SAME
        // book snapshot this poll already pulled, no extra call needed. Computed HERE, before the
        // removal decision below, rather than only later when building draws — the removal
        // decision needs to know whether a shrunk level has been "left behind" by price too (see
        // FIXED 2026-09-23 note below).
        var bestBid = currentBid.Count > 0 ? currentBid.Keys.Max() : double.NaN;
        var bestAsk = currentAsk.Count > 0 ? currentAsk.Keys.Min() : double.NaN;
        var midPrice = double.IsNaN(bestBid) || double.IsNaN(bestAsk) ? double.NaN : (bestBid + bestAsk) / 2.0;
        var tickSize = this.symbol?.TickSize ?? 0d;
        var distanceThreshold = tickSize > 0 ? this.UnfinishedDistanceTicks * tickSize : double.NaN;

        var toRemove = new List<(double Price, bool IsBid)>();
        var toRaise = new List<((double Price, bool IsBid) Key, double NewPeak)>();
        var toAbsorb = new List<((double Price, bool IsBid) Key, double Delta)>();
        var toMiss = new List<((double Price, bool IsBid) Key, int Missed)>();
        var toSeenAgain = new List<(double Price, bool IsBid)>();

        foreach (var kvp in this.restingOrderPeaks)
        {
            var (price, isBid) = kvp.Key;
            var lookup = isBid ? currentBid : currentAsk;

            if (!lookup.TryGetValue(price, out var currentSize) || currentSize <= 0)
            {
                // FIXED 2026-09-22 — "why is the unflished auction levels moving they should be
                // static lines": the platform's own pull is not perfectly stable poll to poll for
                // deeper levels — a price missing from ONE poll's snapshot is not necessarily
                // filled or cancelled. Only past MissingPollGrace CONSECUTIVE misses is it treated
                // as genuinely gone ("once a bid has been filled i need it to disapear from the
                // chart"); a shorter blip keeps the level (and its origin time) intact.
                var missed = this.restingOrderMissingPolls.TryGetValue(kvp.Key, out var m) ? m + 1 : 1;

                if (missed > MissingPollGrace)
                    toRemove.Add(kvp.Key);
                else
                    toMiss.Add((kvp.Key, missed));

                continue;
            }

            if (this.restingOrderMissingPolls.ContainsKey(kvp.Key))
                toSeenAgain.Add(kvp.Key);

            // FIXED 2026-09-23 — "these orders dont disapear or correlate... super small orders
            // when i have my filter set to 50": a level that qualified at its PEAK, then shrank
            // well below the qualifying threshold, used to keep being tracked (and shown, at its
            // live current size per yesterday's stale-peak fix) indefinitely as long as price
            // never moved past it — "ASK 4", "ASK 7", "BID 9" while the filter reads 50. A fresh
            // restart never showed these at all, because AddNewLevels below never tracks a level
            // under the threshold in the first place; the running indicator should not either.
            // UNLESS price has already left it behind (the same distance check the unfinished-
            // auction label uses) — that is a deliberate exception: a level price ran through IS
            // still worth marking as "UA" even far below the general threshold, filterable
            // separately via UnfinishedMinRemainingSize. Only a level BELOW threshold and NOT
            // left behind is dropped here.
            var isLeftBehindNow = !double.IsNaN(midPrice) && !double.IsNaN(distanceThreshold)
                && (isBid ? midPrice < price - distanceThreshold : midPrice > price + distanceThreshold);

            if (currentSize < threshold && !isLeftBehindNow)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            // Dropped since last poll but still standing = something traded through it while it
            // held its ground — "sellers are defending" / "buyers are defending" (the operator's
            // own words, 2026-09-22). A refill afterward does not erase this; the level still had
            // to absorb that flow to still be here.
            if (this.restingOrderLastSize.TryGetValue(kvp.Key, out var lastSize) && lastSize > currentSize)
                toAbsorb.Add((kvp.Key, lastSize - currentSize));

            if (currentSize > kvp.Value)
                toRaise.Add((kvp.Key, currentSize));
        }

        foreach (var key in toRemove)
        {
            this.restingOrderPeaks.Remove(key);
            this.restingOrderLastSize.Remove(key);
            this.restingOrderAbsorbed.Remove(key);
            this.restingOrderFirstSeenUtc.Remove(key);
            this.restingOrderMissingPolls.Remove(key);
        }

        foreach (var (key, missed) in toMiss)
            this.restingOrderMissingPolls[key] = missed;

        foreach (var key in toSeenAgain)
            this.restingOrderMissingPolls.Remove(key);

        foreach (var (key, peak) in toRaise)
            this.restingOrderPeaks[key] = peak;

        foreach (var (key, delta) in toAbsorb)
        {
            this.restingOrderAbsorbed[key] =
                this.restingOrderAbsorbed.TryGetValue(key, out var existing) ? existing + delta : delta;
        }

        AddNewLevels(currentBid, isBid: true);
        AddNewLevels(currentAsk, isBid: false);

        // midPrice/distanceThreshold already computed above, before the removal-decision loop.
        var draws = new List<RestingOrderDraw>(this.restingOrderPeaks.Count);

        foreach (var kvp in this.restingOrderPeaks)
        {
            var (price, isBid) = kvp.Key;
            var peak = kvp.Value;
            var lookup = isBid ? currentBid : currentAsk;
            var current = lookup.TryGetValue(price, out var size) ? size : 0d;

            // Recorded here, not up above — this loop already touches every currently-tracked
            // level once, and next poll's absorption comparison needs THIS poll's observed size,
            // not the peak.
            this.restingOrderLastSize[kvp.Key] = current;

            // "unfinished auctions should only happen on the large order lines that appear
            // because that would mean price moved through that large amount of orders and didnt
            // fill them all" (the operator's own words) — this loop only ever visits entries in
            // restingOrderPeaks, i.e. levels that already cleared the large-order threshold, so
            // that half is true by construction. The other half: price (the current book's own
            // mid) has to have moved past this level's own price by the configured distance, in
            // the direction that would have consumed it — a bid left behind once price fell
            // below it, an ask left behind once price rose above it. Still resting (current > 0)
            // is the only size requirement — "left orders behind" means the orders are still
            // there, whether or not any of them actually got taken.
            var isUnfinished = current > 0 && !double.IsNaN(midPrice) && !double.IsNaN(distanceThreshold)
                && (isBid ? midPrice < price - distanceThreshold : midPrice > price + distanceThreshold);

            if (isUnfinished && current < this.UnfinishedMinRemainingSize)
                continue; // still tracked, just not worth showing at this remaining size

            var absorbed = this.restingOrderAbsorbed.TryGetValue(kvp.Key, out var abs) ? abs : 0d;
            var firstSeenUtc = this.restingOrderFirstSeenUtc.TryGetValue(kvp.Key, out var seen) ? seen : nowUtc;

            // FIXED 2026-09-23 — "these orders need to update on the dom because i dont see these
            // large orders still on the dom": this used to show PEAK for anything not flagged
            // unfinished, which was correct back when "unfinished" meant exactly "current < peak"
            // — the two conditions covered each other. Since yesterday's redesign, "unfinished"
            // depends on PRICE having moved past the level, so a level far from price can shrink
            // a great deal without ever being flagged unfinished — and was still showing its
            // stale ORIGINAL peak number while the DOM ladder (built fresh from the same book
            // every poll) correctly showed the smaller live size right next to it. ALWAYS show
            // the live current size now; peak is bookkeeping only (raising the bar, absorption
            // tracking), never what gets displayed.
            draws.Add(new RestingOrderDraw(price, current, isBid, isUnfinished, absorbed, firstSeenUtc));
        }

        return new RestingOrderDrawable(draws.ToArray());

        void AddNewLevels(Dictionary<double, double> lookup, bool isBid)
        {
            foreach (var (price, size) in lookup)
            {
                if (size < threshold)
                    continue;

                var key = (price, isBid);
                if (!this.restingOrderPeaks.ContainsKey(key))
                {
                    this.restingOrderPeaks[key] = size;
                    this.restingOrderFirstSeenUtc[key] = nowUtc;
                }
            }
        }

        static Dictionary<double, double> ToLookup(Level2Item[]? items)
        {
            var map = new Dictionary<double, double>();

            if (items is null)
                return map;

            foreach (var item in items)
                map[item.Price] = item.Size;

            return map;
        }
    }

    /// <summary>Every scanned level on both sides — bar LENGTH scales against the configured
    /// <see cref="DomLadderFillSize"/> at paint time now, not a per-poll max (see
    /// <see cref="DomLadderOverlay"/>'s own doc comment for why that changed).</summary>
    private DomLadderDrawable BuildLadder(Level2Item[]? bids, Level2Item[]? asks)
    {
        if (!this.DomLadderEnabled)
            return DomLadderDrawable.Empty;

        var count = (bids?.Length ?? 0) + (asks?.Length ?? 0);
        if (count == 0)
            return DomLadderDrawable.Empty;

        var bars = new DomBarDraw[count];
        var i = 0;

        if (bids is not null)
        {
            foreach (var item in bids)
                bars[i++] = new DomBarDraw(item.Price, item.Size, IsBid: true);
        }

        if (asks is not null)
        {
            foreach (var item in asks)
                bars[i++] = new DomBarDraw(item.Price, item.Size, IsBid: false);
        }

        return new DomLadderDrawable(bars);
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

    /// <summary>Reads one closed bar out of a platform history series at the given Begin-indexed
    /// position. False for a bar this indicator's own view of the series does not (yet) hold.</summary>
    private static bool TryReadBar(HistoricalData data, int index, out Bar bar)
    {
        bar = default;

        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item)
            return false;

        bar = new Bar(item.TimeLeft, item.Open, item.High, item.Low, item.Close);
        return true;
    }

    /// <summary>
    /// Feeds newly closed bars from the 15m/1h order-block series and the chart's own series into
    /// their respective engines, then rebuilds the two paint drawables from whatever is currently
    /// active. Runs on the poll timer alongside everything else — each series' own "how far have
    /// we processed" index means a series with no new closed bar this cycle (15m/1h bars close far
    /// less often than the poll interval) does no work at all.
    /// </summary>
    private void DrainStructure()
    {
        var obChanged = false;

        if (this.OrderBlock15mEnabled && this.ob15mHistory is { } h15 && this.ob15mEngine is { } e15 && h15.Count > 1)
        {
            var closedUpTo = h15.Count - 1; // Count - 1 is the still-forming bar; never fed

            for (var i = this.ob15mBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(h15, i, out var bar))
                {
                    e15.Feed(bar);
                    obChanged = true;
                }
            }

            this.ob15mBarsSeen = closedUpTo;
        }

        if (this.OrderBlock1hEnabled && this.ob1hHistory is { } h1h && this.ob1hEngine is { } e1h && h1h.Count > 1)
        {
            var closedUpTo = h1h.Count - 1;

            for (var i = this.ob1hBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(h1h, i, out var bar))
                {
                    e1h.Feed(bar);
                    obChanged = true;
                }
            }

            this.ob1hBarsSeen = closedUpTo;
        }

        if (obChanged)
        {
            var boxes = new List<StructureBoxDraw>();

            if (this.ob15mEngine is not null)
            {
                foreach (var z in this.ob15mEngine.Active)
                {
                    boxes.Add(new StructureBoxDraw(
                        z.StartUtc, z.Top, z.Bottom, z.IsBullish,
                        $"15m OB - {(z.IsBullish ? "BULLISH" : "BEARISH")}"));
                }
            }

            if (this.ob1hEngine is not null)
            {
                foreach (var z in this.ob1hEngine.Active)
                {
                    boxes.Add(new StructureBoxDraw(
                        z.StartUtc, z.Top, z.Bottom, z.IsBullish,
                        $"1H OB - {(z.IsBullish ? "BULLISH" : "BEARISH")}"));
                }
            }

            this.orderBlockDrawable = new StructureBoxDrawable(boxes.ToArray());
        }

        if (!this.InverseFvgEnabled)
            return;

        var chartData = this.HistoricalData;

        if (chartData is null || chartData.Count <= 1)
            return;

        var chartClosedUpTo = chartData.Count - 1;

        // First run after attach: cap how far back into whatever the chart already has loaded
        // this backfills, rather than walking years of history on a long-running chart.
        if (this.chartBarsSeen < 0)
            this.chartBarsSeen = Math.Max(0, chartClosedUpTo - MaxInitialChartBacklogBars);

        var fvgChanged = false;

        for (var i = this.chartBarsSeen; i < chartClosedUpTo; i++)
        {
            if (TryReadBar(chartData, i, out var bar))
            {
                this.fvgEngine.Feed(bar);
                fvgChanged = true;
            }
        }

        this.chartBarsSeen = chartClosedUpTo;

        if (fvgChanged)
        {
            var boxes = new StructureBoxDraw[this.fvgEngine.Active.Count];

            for (var i = 0; i < boxes.Length; i++)
            {
                var z = this.fvgEngine.Active[i];
                boxes[i] = new StructureBoxDraw(
                    z.StartUtc, z.Top, z.Bottom, z.IsBullish, $"IFVG - {(z.IsBullish ? "BULLISH" : "BEARISH")}");
            }

            this.inverseFvgDrawable = new StructureBoxDrawable(boxes);
        }
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

        // FIXED 2026-09-23 — "this is showing but it removed my large orders on the dom": order
        // blocks and inverse FVGs used to draw LAST, on top of everything — their boxes span the
        // full width out to the pane's own right edge, the exact same screen region the DOM
        // ladder occupies, so a wide box sitting over it visually washed the ladder's colours out
        // even though the ladder was still technically drawing underneath. Structure boxes now
        // draw FIRST, as quiet background context, with every live signal (ladder, resting
        // orders, big trades) layered on top of them — same "quiet backdrop, louder signal on
        // top" ordering already used between the ladder and the resting-order lines below.
        if (this.OrderBlock15mEnabled || this.OrderBlock1hEnabled)
        {
            try
            {
                this.orderBlockOverlay.Draw(
                    graphics, window, this.orderBlockDrawable,
                    new StructureBoxOverlay.Options(this.OrderBlockBullishColor, this.OrderBlockBearishColor),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The order-block overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.InverseFvgEnabled)
        {
            try
            {
                this.inverseFvgOverlay.Draw(
                    graphics, window, this.inverseFvgDrawable,
                    new StructureBoxOverlay.Options(this.InverseFvgBullishColor, this.InverseFvgBearishColor),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The inverse-FVG overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        // The ladder draws next, still underneath the resting-order/big-trade signals — it is the
        // quiet full-depth backdrop, and the large-order lines are the louder, highlighted signal
        // that should sit on top of it.
        try
        {
            this.domLadderOverlay.Draw(
                graphics, window, this.domLadderDrawable,
                new DomLadderOverlay.Options(
                    this.BidColor, this.AskColor, this.DomLadderWidth, this.DomLadderRowHeight,
                    this.DomLadderFillSize));
        }
        catch (Exception ex)
        {
            this.overlayFault = $"The DOM ladder overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            // Labels reserve extra right-margin equal to the DOM ladder's own width whenever the
            // ladder is showing, so an ordinary large-order label never sits on top of it —
            // "move that text over some so i can see the dom more clearer" (the operator's own
            // ask, 2026-09-22).
            var labelInset = this.DomLadderEnabled ? (float)this.DomLadderWidth : 0f;

            this.restingOrderOverlay.Draw(
                graphics, window, this.restingOrderDrawable,
                new RestingOrderOverlay.Options(this.BidColor, this.AskColor, this.AbsorptionStrongContracts, labelInset),
                registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"The resting-order overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            this.bigTradeOverlay.Draw(
                graphics, window, this.bigTradeDrawable,
                new BigTradeOverlay.Options(this.BigTradeBuyColor, this.BigTradeSellColor, this.BigTradeMinSize),
                registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"The big-trade overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        if (this.OrderBlock15mEnabled || this.OrderBlock1hEnabled)
        {
            try
            {
                this.orderBlockOverlay.Draw(
                    graphics, window, this.orderBlockDrawable,
                    new StructureBoxOverlay.Options(this.OrderBlockBullishColor, this.OrderBlockBearishColor),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The order-block overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.InverseFvgEnabled)
        {
            try
            {
                this.inverseFvgOverlay.Draw(
                    graphics, window, this.inverseFvgDrawable,
                    new StructureBoxOverlay.Options(this.InverseFvgBullishColor, this.InverseFvgBearishColor),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The inverse-FVG overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
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
        this.orderBlockOverlay.Dispose();
        this.inverseFvgOverlay.Dispose();
        this.ob15mHistory?.Dispose();
        this.ob1hHistory?.Dispose();
        this.faultFont.Dispose();
        this.faultBrush.Dispose();
        this.faultBack.Dispose();

        base.Dispose();
    }
}
