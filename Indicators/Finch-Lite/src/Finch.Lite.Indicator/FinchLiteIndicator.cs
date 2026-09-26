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
    /// how many contracts <see cref="RestingOrderEngine.RestingLevel.Absorbed"/> has to reach before a level's line
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

    /// <summary>The chart's bar period, or null when it has none yet (a tick/Renko/range-bar
    /// chart genuinely has no time period, or the platform hasn't published
    /// <see cref="HistoricalData.Aggregation"/> yet — the two are indistinguishable from here, so
    /// the delta panel just keeps checking every poll rather than gating startup on it).</summary>
    private static TimeSpan? ChartPeriod(HistoricalData? data) =>
        data?.Aggregation is HistoryAggregationTime time && time.Period.Duration > TimeSpan.Zero
            ? time.Period.Duration
            : null;

    /// <summary>
    /// EXTRACTED 2026-09-25 into <c>RestingOrderEngine.cs</c> — Finch-Lite's own large-order/
    /// absorption/unfinished-auction detection, now a standalone class so `finchDomScalpStrategy`
    /// can trade off the EXACT SAME logic this indicator draws, not a second copy that could
    /// drift. See that file's own doc comment for the full design history (peak-vs-current,
    /// poll-miss grace, sub-threshold removal, price-distance unfinished-auction trigger, etc.) —
    /// none of that reasoning changed, only where it lives.
    /// </summary>
    private readonly RestingOrderEngine restingOrderEngine = new();

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

    // ---- delta panel (feature 7, 2026-09-23 — REBUILT; first removed same week) ----------------
    //
    // "i really need the delta that was in that finch-scalping at the bottom of my chart so i can
    // tell when the delta flips" — this indicator's own delta panel was fully torn out earlier
    // this same week ("its not helpful at all"); it's back now for a narrower, specific reason
    // (spotting a flip), not just "show delta" again. See DeltaPanelOverlay's own doc comment for
    // the flip-marker design.
    [InputParameter("Delta panel: enable", 70)]
    public bool DeltaPanelEnabled { get; set; } = true;

    // RAISED 2026-09-23 (same day) from 110 to 150, min 40 to 60 — "now i need to see the
    // session delta and delta and volume like in the finch-scalping": the panel is back to
    // three stacked rows (this indicator built the same split once before), and 110px split
    // three ways left each row too thin to read.
    [InputParameter("Delta panel: height (px)", 71, 60, 300, 10, 0)]
    public int DeltaPanelHeightPx { get; set; } = 150;

    [InputParameter("Delta panel: up colour", 72)]
    public Color DeltaUpColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Delta panel: down colour", 73)]
    public Color DeltaDownColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    [InputParameter("Delta panel: show flip marker", 74)]
    public bool DeltaFlipMarkerEnabled { get; set; } = true;

    /// <summary>The volume row's own colour — volume itself has no direction, so it gets one
    /// neutral colour rather than the up/down pair the delta row below it uses.</summary>
    [InputParameter("Delta panel: volume colour", 75)]
    public Color DeltaVolumeColor { get; set; } = Color.FromArgb(0x64, 0x95, 0xED);

    /// <summary>The chart's own bar period — resolved lazily on the poll timer (see
    /// DrainDeltaTicks) rather than gating the whole indicator's startup on it the way the
    /// original delta panel once did; a connector slow to publish the chart's period should not
    /// hold up Features 1-3, same "additive, never fatal" discipline order blocks already use.
    /// </summary>
    private TimeSpan deltaBarPeriod;

    /// <summary>Raw classified ticks, queued off the market-data thread exactly like
    /// <see cref="bigTradeQueue"/> — bucketing into bars happens on the poll timer, never in
    /// <see cref="OnLast"/> itself.</summary>
    private readonly ConcurrentQueue<(DateTime TimeUtc, double Size, bool IsBuy)> deltaTickQueue = new();

    /// <summary>
    /// FIXED 2026-09-23 — "i need it to continue with the delta for each candle not just static
    /// on 1 candle": the original design closed a bar only when a NEW classified tick arrived
    /// with a different bucket time than the one currently open — so a candle that happened to
    /// receive zero classified prints (this connector's aggressor flag is already known to be
    /// sparse; see `TryClassify`'s own doc comment) got no bar at all, not even a zero one,
    /// leaving the panel with real gaps and looking frozen through any quiet stretch. Classified
    /// ticks now accumulate here, keyed by which BAR they belong to, and are only ever consumed
    /// when that bar ACTUALLY CLOSES on the chart (see <see cref="DrainDeltaTicks"/>) — so every
    /// chart candle gets exactly one delta bar, zero-delta if it received no classified prints,
    /// never skipped.
    /// </summary>
    private readonly Dictionary<DateTime, (double Buy, double Sell)> deltaPending = new();

    /// <summary>-1 means "not yet seeded" — same first-poll backlog cap as `chartBarsSeen` uses
    /// for the same reason (a long-running chart could have years of already-loaded history).
    /// </summary>
    private int deltaChartBarsSeen = -1;

    private readonly List<DeltaBarDraw> deltaBars = new();
    private double deltaCumulative;
    private DateTime deltaDayStartUtc;

    /// <summary>Null until the first non-zero session cumulative delta is seen; +1/-1 afterward.
    /// A flip is a sign change against this, so an exactly-zero bar in between two same-signed
    /// bars is not mistaken for two flips.</summary>
    private int? deltaLastCumulativeSign;

    private DateTime? deltaFlipUtc;
    private bool deltaFlipIsUp;

    private readonly DeltaPanelOverlay deltaPanelOverlay = new();
    private volatile DeltaDrawable deltaDrawable = DeltaDrawable.Empty;

    /// <summary>Bars kept on screen at once — bounded so a long session does not grow this list
    /// forever; old bars scroll off the left edge of any chart a reader would actually be looking
    /// at anyway.</summary>
    private const int MaxDeltaBarsKept = 2000;

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

    // ---- point of control (feature 7, 2026-09-25) ----------------------------------------------
    //
    // "the poc of the current move and a higher time frame poc of like the 15min" — two
    // PocEngine instances (see its own doc comment for the "current move"/lag/tick-volume design,
    // all explicit choices made via AskUserQuestion): one fed the CHART's own closed bars for
    // "the current move", a second fed a dedicated 15-minute series (independent of the 15m order
    // blocks above — disabling one must not silently starve the other of history) for the
    // higher-timeframe read. Both share the SAME live tick stream already driving the delta panel
    // and big-trade markers; POC just doesn't care which side was the aggressor.
    [InputParameter("POC: enable current-move", 80)]
    public bool PocCurrentMoveEnabled { get; set; } = true;

    [InputParameter("POC: enable 15m higher-timeframe", 81)]
    public bool Poc15mEnabled { get; set; } = true;

    /// <summary>Bars required on EACH side of a candidate before it counts as a confirmed swing
    /// high/low, on WHICHEVER timeframe each PocEngine instance is fed — same fractal rule as
    /// order blocks, shared by both the current-move and 15m engines rather than exposing two
    /// near-identical sliders for one concept.</summary>
    [InputParameter("POC: swing pivot lookback (bars)", 82, 1, 20, 1, 0)]
    public int PocSwingPivotLookback { get; set; } = 3;

    [InputParameter("POC: current-move colour", 83)]
    public Color PocCurrentMoveColor { get; set; } = Color.FromArgb(0xFF, 0xD7, 0x00);

    [InputParameter("POC: 15m colour", 84)]
    public Color Poc15mColor { get; set; } = Color.FromArgb(0x40, 0xC4, 0xFF);

    /// <summary>How far back the dedicated 15-minute series loads on attach — only needs enough
    /// bars to locate the current swing structure, not a long trading history, since the profile
    /// itself only ever accumulates LIVE ticks going forward (see PocEngine's own "known lag"
    /// doc comment) — no historical volume to backfill regardless of how far back this reaches.</summary>
    [InputParameter("POC: 15m history lookback (days)", 85, 1, 90, 1, 0)]
    public int PocLookbackDays { get; set; } = 5;

    /// <summary>
    /// Constructed lazily in <see cref="TryStartPoc"/>, not here — same reason
    /// <see cref="ob15mEngine"/>/<see cref="ob1hEngine"/> are lazy: an eager field initializer
    /// would bake in whatever <see cref="PocSwingPivotLookback"/> happens to equal at object
    /// construction time, BEFORE Quantower has applied any saved InputParameter override, and
    /// (being readonly-in-spirit here, never reassigned once built) would then silently ignore an
    /// operator's own configured pivot lookback for the rest of the attach.
    /// </summary>
    private PocEngine? pocCurrentMoveEngine;

    private PocEngine? poc15mEngine;
    private HistoricalData? poc15mHistory;
    private int poc15mBarsSeen;

    /// <summary>Own cursor into the SAME chart HistoricalData the IFVG section above reads —
    /// deliberately not shared with <see cref="chartBarsSeen"/>, since that one stops advancing
    /// entirely whenever Inverse FVG is disabled (DrainStructure returns early), which must not
    /// also silently stall POC's own view of the chart's bars.</summary>
    private int pocChartBarsSeen = -1;

    private readonly ConcurrentQueue<(double Price, double Size)> pocTickQueue = new();
    private readonly PocOverlay pocOverlay = new();
    private volatile PocDrawable pocDrawable = PocDrawable.Empty;

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
            this.TryStartPoc(symbol);

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
    /// Best-effort, same reasoning as <see cref="TryStartOrderBlocks"/> — a connector unable to
    /// supply the dedicated 15m series should not take DOM/tape reading down with it. The
    /// current-move engine needs no history fetch of its own (it reads the CHART's own already-
    /// loaded series in <see cref="DrainPoc"/>, same as the IFVG engine does), so it is
    /// constructed here purely to pick up the operator's actual configured pivot lookback rather
    /// than whatever <see cref="PocSwingPivotLookback"/> equalled at object-construction time.
    /// </summary>
    private void TryStartPoc(Qt.Symbol symbol)
    {
        if (this.PocCurrentMoveEnabled && this.pocCurrentMoveEngine is null)
            this.pocCurrentMoveEngine = new PocEngine(this.PocSwingPivotLookback);

        if (this.Poc15mEnabled && this.poc15mHistory is null)
        {
            try
            {
                var lookback = DateTime.UtcNow.AddDays(-Math.Max(1, this.PocLookbackDays));
                this.poc15mHistory = symbol.GetHistory(Period.MIN15, symbol.HistoryType, lookback);
                this.poc15mEngine = new PocEngine(this.PocSwingPivotLookback);
                this.poc15mBarsSeen = 0;
            }
            catch (Exception ex)
            {
                this.overlayFault = $"15m POC unavailable: {ex.GetType().Name}: {ex.Message}";
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

        // POC counts every trade's own volume toward its own price regardless of which side was
        // the aggressor, so this queues unconditionally rather than after the classification
        // gate below — gated only on whether either POC feature is actually on, so the queue
        // never grows once both are switched off (same "nothing running that isn't currently
        // shown" discipline as everything else in this indicator).
        if (last.Size > 0 && (this.PocCurrentMoveEnabled || this.Poc15mEnabled))
            this.pocTickQueue.Enqueue((last.Price, last.Size));

        // Prints that carry no evidence either way (unusable quote, or strictly inside the
        // spread) feed NEITHER queue below — a line implying "buyers did this" or "sellers did
        // this" for a fill nothing could attribute to a side would be a guess dressed up as a
        // fact.
        if (!TryClassify(symbol, last, out var isBuy))
            return;

        if (last.Size >= this.BigTradeMinSize)
            this.bigTradeQueue.Enqueue(new BigTradeDraw(last.Price, last.Size, isBuy, last.Time));

        this.deltaTickQueue.Enqueue((last.Time, last.Size, isBuy));
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
        this.restingOrderEngine.Reset();
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

        this.pocCurrentMoveEngine = null;
        this.poc15mEngine = null;
        this.poc15mHistory?.Dispose();
        this.poc15mHistory = null;
        this.poc15mBarsSeen = 0;
        this.pocChartBarsSeen = -1;
        this.pocTickQueue.Clear();
        this.pocDrawable = PocDrawable.Empty;

        this.deltaTickQueue.Clear();
        this.deltaPending.Clear();
        this.deltaChartBarsSeen = -1;
        this.deltaBars.Clear();
        this.deltaCumulative = 0d;
        this.deltaDayStartUtc = default;
        this.deltaLastCumulativeSign = null;
        this.deltaFlipUtc = null;
        this.deltaFlipIsUp = false;
        this.deltaBarPeriod = default;
        this.deltaDrawable = DeltaDrawable.Empty;

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
            this.DrainDeltaTicks();
        }
        catch (Exception ex)
        {
            this.overlayFault = $"Delta panel failed: {ex.GetType().Name}: {ex.Message}";
        }

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
            this.DrainPoc();
        }
        catch (Exception ex)
        {
            this.overlayFault = $"POC failed: {ex.GetType().Name}: {ex.Message}";
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
    /// EXTRACTED 2026-09-25 — the actual reconciliation logic now lives in
    /// <see cref="RestingOrderEngine"/> (see that file for the full design history). This is a
    /// thin wrapper: compute the two platform-facing inputs the engine can't compute itself
    /// (current book mid price, the distance threshold in price units, the trading-day boundary),
    /// call the engine, map its results to the paint drawable, and apply the DISPLAY-only
    /// <see cref="UnfinishedMinRemainingSize"/> filter (a decision about what to SHOW, not what
    /// to track — kept here rather than in the engine so the strategy that reuses the same engine
    /// can decide independently whether to trade a small unfinished-auction remainder).
    /// </summary>
    private RestingOrderDrawable ReconcileRestingLevels(
        DateTime nowUtc, Level2Item[]? bids, Level2Item[]? asks, int threshold)
    {
        var bestBid = bids is { Length: > 0 } ? bids.Max(b => b.Price) : double.NaN;
        var bestAsk = asks is { Length: > 0 } ? asks.Min(a => a.Price) : double.NaN;
        var midPrice = double.IsNaN(bestBid) || double.IsNaN(bestAsk) ? double.NaN : (bestBid + bestAsk) / 2.0;
        var tickSize = this.symbol?.TickSize ?? 0d;
        var distanceThreshold = tickSize > 0 ? this.UnfinishedDistanceTicks * tickSize : double.NaN;
        var dayStart = TradingDayStart(nowUtc);

        var levels = this.restingOrderEngine.Reconcile(
            nowUtc, dayStart, bids, asks, threshold, midPrice, distanceThreshold);

        var draws = new List<RestingOrderDraw>(levels.Count);

        foreach (var level in levels)
        {
            if (level.IsUnfinished && level.Current < this.UnfinishedMinRemainingSize)
                continue; // still tracked by the engine, just not worth showing at this size

            draws.Add(new RestingOrderDraw(
                level.Price, level.Current, level.IsBid, level.IsUnfinished, level.Absorbed,
                level.FirstSeenUtc));
        }

        return new RestingOrderDrawable(draws.ToArray());
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

    /// <summary>
    /// Accumulates queued classified ticks by which CHART BAR they belong to, then emits exactly
    /// one delta bar per chart bar that has actually closed — driven by the chart's own bars
    /// (same `TryReadBar`/`SeekOriginHistory.Begin` reading `DrainStructure` already uses for
    /// IFVG detection), never by tick arrival. See <see cref="deltaPending"/>'s own doc comment
    /// for why that changed. Session boundary matches <see cref="ReconcileRestingLevels"/>'s own
    /// (18:00 America/New_York) — one trading-day convention for the whole indicator. Also
    /// detects a FLIP — session cumulative delta crossing zero — "so i can tell when the delta
    /// flips" (the operator's own ask, 2026-09-23).
    /// </summary>
    private void DrainDeltaTicks()
    {
        if (this.deltaBarPeriod <= TimeSpan.Zero)
        {
            var period = ChartPeriod(this.HistoricalData);

            if (period is null || period.Value <= TimeSpan.Zero)
                return; // chart hasn't published its own bar period yet; try again next poll

            this.deltaBarPeriod = period.Value;
        }

        while (this.deltaTickQueue.TryDequeue(out var tick))
        {
            var bucketUtc = Bucket(tick.TimeUtc, this.deltaBarPeriod);
            (double Buy, double Sell) acc = this.deltaPending.TryGetValue(bucketUtc, out var existing) ? existing : (0d, 0d);

            this.deltaPending[bucketUtc] = tick.IsBuy
                ? (acc.Buy + tick.Size, acc.Sell)
                : (acc.Buy, acc.Sell + tick.Size);
        }

        var chartData = this.HistoricalData;

        if (chartData is null || chartData.Count <= 1)
            return;

        var closedUpTo = chartData.Count - 1; // Count - 1 is the still-forming bar

        // First run after attach: cap how far back this backfills, same reasoning
        // `chartBarsSeen`/`MaxInitialChartBacklogBars` already established for IFVG.
        if (this.deltaChartBarsSeen < 0)
            this.deltaChartBarsSeen = Math.Max(0, closedUpTo - MaxInitialChartBacklogBars);

        var changed = false;

        for (var i = this.deltaChartBarsSeen; i < closedUpTo; i++)
        {
            if (!TryReadBar(chartData, i, out var bar))
                continue;

            CloseBar(bar.OpenUtc);
            changed = true;
        }

        this.deltaChartBarsSeen = closedUpTo;

        // The still-forming bar draws too, as a live, updating bar — waiting for a bar to close
        // before it appears at all would make the panel look a whole bar behind the candles.
        if (TryReadBar(chartData, closedUpTo, out var formingBar))
        {
            var dayStart = TradingDayStart(formingBar.OpenUtc);
            if (dayStart != this.deltaDayStartUtc)
            {
                // A fresh trading day started on the forming bar itself — roll over now rather
                // than waiting for it to close, so the live bar reads against the NEW day's
                // zeroed cumulative instead of the old day's leftover total.
                this.deltaDayStartUtc = dayStart;
                this.deltaCumulative = 0d;
                this.deltaBars.Clear();
                this.deltaLastCumulativeSign = null;
                this.deltaFlipUtc = null;
            }

            (double Buy, double Sell) acc = this.deltaPending.TryGetValue(formingBar.OpenUtc, out var pending) ? pending : (0d, 0d);
            var liveVolume = acc.Buy + acc.Sell;
            var liveDelta = acc.Buy - acc.Sell;

            var bars = new List<DeltaBarDraw>(this.deltaBars)
            {
                new(formingBar.OpenUtc, liveVolume, liveDelta, this.deltaCumulative + liveDelta),
            };

            this.deltaDrawable = new DeltaDrawable(bars.ToArray(), this.deltaFlipUtc, this.deltaFlipIsUp);
        }
        else if (changed)
        {
            this.deltaDrawable = new DeltaDrawable(this.deltaBars.ToArray(), this.deltaFlipUtc, this.deltaFlipIsUp);
        }

        void CloseBar(DateTime openUtc)
        {
            var dayStart = TradingDayStart(openUtc);
            if (dayStart != this.deltaDayStartUtc)
            {
                this.deltaDayStartUtc = dayStart;
                this.deltaCumulative = 0d;
                this.deltaBars.Clear();
                this.deltaLastCumulativeSign = null;
                this.deltaFlipUtc = null;
            }

            (double Buy, double Sell) acc = this.deltaPending.Remove(openUtc, out var pending) ? pending : (0d, 0d);
            var volume = acc.Buy + acc.Sell;
            var delta = acc.Buy - acc.Sell;
            var newCumulative = this.deltaCumulative + delta;
            var newSign = Math.Sign(newCumulative);

            // A flip is a SIGN CHANGE against the last known non-zero sign — an exactly-zero bar
            // in between two same-signed bars is not mistaken for two flips (or for none).
            if (this.deltaLastCumulativeSign is { } lastSign && newSign != 0 && newSign != lastSign)
            {
                this.deltaFlipUtc = openUtc;
                this.deltaFlipIsUp = newSign > 0;
            }

            if (newSign != 0)
                this.deltaLastCumulativeSign = newSign;

            this.deltaCumulative = newCumulative;
            this.deltaBars.Add(new DeltaBarDraw(openUtc, volume, delta, this.deltaCumulative));

            if (this.deltaBars.Count > MaxDeltaBarsKept)
                this.deltaBars.RemoveRange(0, this.deltaBars.Count - MaxDeltaBarsKept);
        }

        static DateTime Bucket(DateTime timeUtc, TimeSpan period)
            => new(timeUtc.Ticks - (timeUtc.Ticks % period.Ticks), DateTimeKind.Utc);
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

    /// <summary>
    /// Feeds the chart's own closed bars into the current-move engine, the dedicated 15-minute
    /// series into the higher-timeframe engine, and every queued trade print into whichever
    /// engine(s) are enabled, then rebuilds the paint drawable. Own history-reading cursor
    /// (<see cref="pocChartBarsSeen"/>), independent of the IFVG cursor above — see that field's
    /// own doc comment for why.
    /// </summary>
    private void DrainPoc()
    {
        var changed = false;

        if (this.PocCurrentMoveEnabled && this.pocCurrentMoveEngine is { } cm)
        {
            var chartData = this.HistoricalData;

            if (chartData is { Count: > 1 })
            {
                var closedUpTo = chartData.Count - 1;

                if (this.pocChartBarsSeen < 0)
                    this.pocChartBarsSeen = Math.Max(0, closedUpTo - MaxInitialChartBacklogBars);

                for (var i = this.pocChartBarsSeen; i < closedUpTo; i++)
                {
                    if (TryReadBar(chartData, i, out var bar))
                    {
                        cm.FeedBar(bar);
                        changed = true;
                    }
                }

                this.pocChartBarsSeen = closedUpTo;
            }
        }

        if (this.Poc15mEnabled && this.poc15mHistory is { } h15 && this.poc15mEngine is { } htf && h15.Count > 1)
        {
            var closedUpTo = h15.Count - 1;

            for (var i = this.poc15mBarsSeen; i < closedUpTo; i++)
            {
                if (TryReadBar(h15, i, out var bar))
                {
                    htf.FeedBar(bar);
                    changed = true;
                }
            }

            this.poc15mBarsSeen = closedUpTo;
        }

        while (this.pocTickQueue.TryDequeue(out var tick))
        {
            changed = true;

            if (this.PocCurrentMoveEnabled)
                this.pocCurrentMoveEngine?.FeedTrade(tick.Price, tick.Size);

            if (this.Poc15mEnabled)
                this.poc15mEngine?.FeedTrade(tick.Price, tick.Size);
        }

        if (!changed)
            return;

        var points = new List<PocDraw>(2);

        if (this.PocCurrentMoveEnabled && this.pocCurrentMoveEngine?.Poc is { } cmPoc)
            points.Add(new PocDraw(cmPoc, this.pocCurrentMoveEngine.MoveStartUtc, IsHigherTimeframe: false));

        if (this.Poc15mEnabled && this.poc15mEngine?.Poc is { } htfPoc)
            points.Add(new PocDraw(htfPoc, this.poc15mEngine.MoveStartUtc, IsHigherTimeframe: true));

        this.pocDrawable = new PocDrawable(points.ToArray());
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

        if (this.PocCurrentMoveEnabled || this.Poc15mEnabled)
        {
            try
            {
                this.pocOverlay.Draw(
                    graphics, window, this.pocDrawable,
                    new PocOverlay.Options(this.PocCurrentMoveColor, this.Poc15mColor),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The POC overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.DeltaPanelEnabled)
        {
            try
            {
                this.deltaPanelOverlay.Draw(
                    graphics, window, this.deltaDrawable,
                    new DeltaPanelOverlay.Options(
                        this.DeltaPanelHeightPx, this.DeltaVolumeColor, this.DeltaUpColor, this.DeltaDownColor,
                        this.CurrentChart?.BarsWidth ?? 1d, this.DeltaFlipMarkerEnabled),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The delta panel overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
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
        this.deltaPanelOverlay.Dispose();
        this.pocOverlay.Dispose();
        this.ob15mHistory?.Dispose();
        this.ob1hHistory?.Dispose();
        this.poc15mHistory?.Dispose();
        this.faultFont.Dispose();
        this.faultBrush.Dispose();
        this.faultBack.Dispose();

        base.Dispose();
    }
}
