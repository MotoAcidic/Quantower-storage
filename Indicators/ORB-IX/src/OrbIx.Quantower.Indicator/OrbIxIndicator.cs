using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Buffers;
using OrbIx.Core.Config;
using OrbIx.Core.Diagnostics;
using OrbIx.Core.Direction;
using OrbIx.Quantower.Shared;
using OrbIx.Core.Features;
using OrbIx.Core.Playbooks;
using OrbIx.Core.Risk;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;
using OrbIx.Core.Structure;
using OrbIx.Core.Telemetry;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;
// Both names collide: the SDK has a global "Platform" type, and "Core" is both this
// project's root namespace and a Quantower class. Aliased to names neither side uses.
using OrbCore = OrbIx.Core;
using Qt = TradingPlatform.BusinessLayer;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The ORB-IX indicator: an adapter and a renderer, and nothing else.
///
/// §11 puts every decision in OrbIx.Core, which has no reference to this platform. This
/// class translates Quantower's events into the engine's own types, drives the periodic
/// fold, and paints the snapshot the fold produces. It contains no rule about when to
/// trade, which is what makes the engine replayable offline and this file uninteresting.
///
/// The threading discipline is §10's, and it is a build requirement rather than an
/// optimisation. The Level 2 handler fires thousands of times a second on an active
/// instrument, so handlers translate and write into a pre-sized ring and return; a timer
/// folds the rings into module state; painting reads an immutable snapshot from that fold.
/// No allocation, no computation, and no locking happens on the market-data path.
/// </summary>
public sealed class OrbIxIndicator : Qt.Indicator
{
    private readonly EventRing<TickEvent> tickRing = new(8192);

    // The direction read, and the renderer it shares with the standalone Direction
    // indicator. Built in OnInit once the symbol is known, because the lane set
    // depends on the chart's own period.
    private DirectionEngine? direction;
    private readonly DirectionOverlay directionOverlay = new();
    private DirectionPanelContent? directionPanel;
    private readonly EventRing<BookDelta> bookRing = new(16384);

    /// <summary>
    /// The same Level 2 events, in the shape the depth LADDER needs.
    ///
    /// A SECOND VIEW OF ONE FEED, NOT A SECOND FEED. BookDelta carries no order id or queue
    /// priority — the absorption engine has no use for them — while the ladder needs both to tell
    /// a per-order feed from an aggregated one, and to recognise a book the connector fabricated
    /// from level 1. Both are built from the SAME Level2Quote in OnLevel2, so neither can observe
    /// an event the other did not.
    ///
    /// Ringed rather than applied in the handler for the reason every other feed here is: the
    /// ladder is read under the fold gate, and writing it from the platform's thread would race.
    /// </summary>
    private readonly EventRing<OrbCore.Flow.DepthUpdate> depthRing = new(16384);
    private readonly object foldGate = new();

    /// <summary>The configuration document's file name, on disk and as an embedded resource.</summary>
    private const string EmbeddedConfigName = "orbix.default.json";

    /// <summary>Which of the three configuration sources actually supplied the running config.</summary>
    private string configOrigin = "none";

    /// <summary>
    /// A short handle for THIS instance, on every line it writes.
    ///
    /// WHY: every instance appends to one shared orbix-startup.log and, until this, nothing
    /// distinguished them. Measured 2026-08-31 on that file: "Wave1 params" appears twice at
    /// 18:09:44Z and twice at 18:11:29Z, "HH/LL" twice at 18:11:33Z — two instances, one
    /// stream, indistinguishable. A five-minute load and two interleaved two-minute loads
    /// read identically, so no timing added to that file could have been attributed.
    ///
    /// A process-wide counter rather than a hash code: it yields 0001, 0002, 0003 — readable,
    /// ordered by creation, and collision-free within the process, where a hash would be an
    /// arbitrary four characters that happen not to clash. Interlocked because Quantower may
    /// construct indicators on more than one thread.
    ///
    /// readonly and NEVER reset by Clear(): the object is the instance, so a chart cleared and
    /// re-added keeps its tag. A tag that churned per load would make the log worse than none.
    /// </summary>
    private static int instanceCounter;

    private readonly string instanceTag =
        LoadTiming.Tag(Interlocked.Increment(ref instanceCounter));

    /// <summary>
    /// The most recent overlay drawing fault, or null.
    ///
    /// A single volatile reference rather than a field the fold reads, because
    /// this is the one message written from the PAINT thread while the fold thread copies
    /// that list — appending to a List while another thread enumerates it throws. A reference
    /// assignment is atomic and volatile makes it visible; the fold folds it into the
    /// snapshot's warnings.
    /// </summary>
    private volatile string? overlayFault;

    /// <summary>
    /// What the overlay currently has to draw, updated by the fold and shown on the panel.
    ///
    /// A volatile reference for the same reason as <see cref="overlayFault"/>: written off the
    /// initialisation thread. It exists so a chart that draws nothing says WHY — no bars, no
    /// session in range, or ranges held and drawn — rather than leaving it to be guessed from
    /// the absence of lines.
    /// </summary>
    private volatile string? drawStatus;

    /// <summary>
    /// The most recent guard readings, published by the fold for the paint thread.
    ///
    /// An immutable array swapped in wholesale, for the same reason the drawable ranges are:
    /// paint must never walk a collection the fold is still writing.
    /// </summary>

    /// <summary>
    /// Drives the periodic fold.
    ///
    /// The class documentation has always said "a timer folds the rings into module state",
    /// but the fold was driven only by <see cref="OnUpdate"/>, which fires on market data. On
    /// a closed or quiet market nothing folded, so nothing seeded and nothing was drawn —
    /// the chart stayed blank precisely when there was most history to show.
    /// </summary>
    private Timer? foldTimer;

    /// <summary>
    /// Drives re-initialisation while the platform has not yet published contract
    /// specifications. Disposed as soon as it succeeds or gives up.
    /// </summary>
    private Timer? retryTimer;

    /// <summary>
    /// The symbol, available to <see cref="BuildEngine"/> before event subscription happens.
    /// Separate from <c>subscribed</c>, which means "handlers are attached" and must not be
    /// set until they are.
    /// </summary>
    private Qt.Symbol? subscribedForHistory;

    /// <summary>The configured account whose rules are in force.</summary>
    private AccountConfig? account;

    /// <summary>
    /// The account- and feed-level guards.
    ///
    /// Evaluated here for DISPLAY. An indicator has no <c>Account</c> — that is a Strategy
    /// concept — so it cannot read realised profit, equity or its high-water mark, and the
    /// guards that need those honestly report themselves unevaluable. Enforcement lives in
    /// the strategy, where orders are actually sent and an account exists.
    /// </summary>

    /// <summary>When a quote last arrived, for the stale-quote guard.</summary>
    private DateTime lastQuoteUtc = DateTime.MinValue;

    private OrbIxConfig? config;
    private SessionClock? clock;
    private OrBuilder? rangeBuilder;

    /// <summary>
    /// Range builders for windows that contain this instant but are not the active one — the
    /// initial balance, and any other window configured with entries disallowed.
    /// </summary>
    private readonly Dictionary<SessionWindow, OrBuilder> contextBuilders = new();
    private LevelGraph? levels;
    private FootprintEngine? footprint;
    private AbsorptionEngine? absorption;

    /// <summary>Decides when the book is due to be pulled. Built on first fold, from the document.</summary>
    private OrbCore.Flow.BookPullClock? bookPullClock;

    /// <summary>When the book pull last reported its size and cost.</summary>
    private DateTime bookPullReportUtc = DateTime.MinValue;

    /// <summary>Milliseconds spent in the platform's depth call since the last report.</summary>
    private double bookPullMsTotal;

    /// <summary>Pulls since the last report, the denominator for the mean above.</summary>
    private int bookPullCount;

    /// <summary>The last book-pull fault reported, so a repeating one is said once. See ReportBookPullOnce.</summary>
    private string? bookPullFault;
    private MicroQuality? quality;

    // THE FOUR THAT WERE NEVER BUILT. Without them the chart evaluated no setup at all: it
    // drew ranges and levels and called that the engine. SetupEvaluator is the shared decision
    // implementation the replay uses, deliberately — a second copy on the chart would let it
    // signal something the study never measured.
    private RetestEngine? retest;
    private SetupEvaluator? evaluator;
    private BarAggregator? bars;
    private BreakDetector? breaker;

    /// <summary>
    /// The order every module is driven in, shared with the offline replay.
    ///
    /// The sequence is NOT written out here. It lives in OrbIx.Core so the chart and the study
    /// cannot drift apart into two systems that both look correct.
    /// </summary>
    private EngineFold? engine;

    /// <summary>
    /// The latest evaluation, published for the paint thread.
    ///
    /// An immutable record assigned under <see cref="foldGate"/> and only read outside it, the
    /// same discipline <see cref="drawable"/> and <see cref="drawableLevels"/> already follow.
    /// Painting must never walk state this thread is still building.
    /// </summary>
    private SetupEvaluation? drawableSetup;

    /// <summary>What the drawn signal has been measured to do. Empty when nothing is drawn.</summary>
    private string drawableSignalLabel = string.Empty;

    /// <summary>Why no entry is possible at all, when that is the reason nothing is drawn.</summary>
    private string drawableBlockReason = string.Empty;

    /// <summary>Standing statement of what backs the confluence score. Built once at start-up.</summary>
    private string drawableScoreCoverage = string.Empty;
    private SessionContext? context;
    private StreamRecorder? recorder;
    private TradeJournal? journal;

    private Qt.Symbol? subscribed;
    private InstrumentSpec instrument;
    private SessionWindow? window;
    private DateTime lastFoldUtc = DateTime.MinValue;

    /// <summary>
    /// The most recent traded price the fold saw. Zero until the first print.
    ///
    /// Taken from the fold rather than from Symbol.Last on the paint thread, so the levels
    /// selected as "near price" are near the same price the modules saw.
    /// </summary>
    /// <summary>
    /// Per-price volume from tick history, fetched once at initialisation.
    ///
    /// NOT REFRESHED ON THE FOLD, and that is a threading rule rather than a preference: the
    /// load calls the platform's history API, which reloads synchronously on the calling
    /// thread. It therefore covers the chart's loaded span as it stood at attach; ranges
    /// beyond it fall through to the sources that were always there.
    /// </summary>
    private ProfileTickCache? profileTickCache;

    /// <summary>
    /// Bumped whenever <see cref="profileTickCache"/> is replaced.
    ///
    /// IT IS IN THE REBUILD SIGNATURE, and it has to be. The load now completes on its own
    /// thread, after the first profiles have already been drawn and refused; without a term in
    /// the signature that changes when the data arrives, the rebuild would never re-run and the
    /// profile would stay refused with its answer sitting in memory.
    /// </summary>
    private int profileTickGeneration;

    /// <summary>
    /// How long to keep re-reading the platform's symbol list before giving up on it.
    ///
    /// The observed gap between an empty list and a populated one was roughly two minutes, so
    /// this is generous enough to cover it and bounded so a genuinely absent instrument is
    /// reported rather than waited on forever.
    /// </summary>
    private static readonly TimeSpan SymbolListWait = TimeSpan.FromMinutes(3);

    /// <summary>Gap between reads of the symbol list while it is still filling.</summary>
    private const int SymbolListPollMs = 2000;

    /// <summary>Cancels an in-flight tick load when the indicator is cleared.</summary>
    private CancellationTokenSource? profileTickCts;

    private double lastPrice;

    /// <summary>
    /// Levels published by the fold for the paint thread, already filtered to those near price.
    ///
    /// An immutable array swapped in wholesale, for the same reason the drawable ranges are:
    /// paint must never walk a collection the fold is still writing.
    /// </summary>
    private Level[] drawableLevels = Array.Empty<Level>();

    // ---- drawing state -------------------------------------------------------------------
    //
    // The store is written by the fold and read by the paint, so paint reads an immutable
    // array taken under the fold's own gate rather than walking the live collection.
    private ChartOverlay? overlay;
    private SessionRangeStore? ranges;
    private OrSnapshot[] drawable = Array.Empty<OrSnapshot>();

    // Average daily range, loaded once. Zero means it could not be measured, which the
    // engine reports as UNGRADED rather than grading against a number nobody produced.
    private double averageDailyRange;
    private int seededSessions;
    private int lastSeedBarCount = -1;

    [InputParameter("Configuration file (blank = beside this assembly)", 10)]
    public string ConfigPath { get; set; } = string.Empty;

    [InputParameter("Fold interval, milliseconds", 20, 20, 1000, 1, 0)]
    public int FoldIntervalMs { get; set; } = 100;

    [InputParameter("Product root override (blank = derive from the symbol)", 30)]
    public string SymbolRootOverride { get; set; } = string.Empty;

    /// <summary>
    /// Which configured account's rules apply.
    ///
    /// The account decides the news restriction, and the firm's restrictions differ by account
    /// class — Tier 1 events may be traded on an evaluation and may not on a sim-funded
    /// account. Blank selects the first configured account; a value matching nothing is a
    /// startup problem naming the configured ids, never a silent fall-back to whichever
    /// account happened to be first.
    /// </summary>
    [InputParameter("Account (blank = first configured)", 35)]
    public string AccountId { get; set; } = string.Empty;

    [InputParameter("Draw the opening-range box", 40)]
    public bool DrawBox { get; set; } = true;

    [InputParameter("Draw the range high and low", 50)]
    public bool DrawEdges { get; set; } = true;

    [InputParameter("Draw the range midpoint", 60)]
    public bool DrawMidline { get; set; } = true;

    [InputParameter("Draw the extension targets", 70)]
    public bool DrawExtensions { get; set; } = true;

    [InputParameter("Draw level labels", 80)]
    public bool DrawLabels { get; set; } = true;

    [InputParameter("Draw key levels near price", 84)]
    public bool DrawKeyLevels { get; set; } = true;

    /// <summary>
    /// How far either side of price a level is still worth drawing, as a percentage of the
    /// average daily range.
    ///
    /// This is the whole anti-clutter mechanism: the engine tracks prior day and week extremes,
    /// overnight extremes, an initial balance, an opening range per session and round numbers,
    /// which on an eight-session product is well over thirty prices. A level far from price
    /// cannot be reached today and costs attention for nothing.
    /// </summary>
    [InputParameter("Level band, % of average daily range", 86, 5, 200, 5, 0)]
    public int LevelBandPercent { get; set; } = 35;

    [InputParameter("Draw sessions that closed before this indicator loaded", 90)]
    public bool SeedFromHistory { get; set; } = true;

    // ---- HH/LL structure port ------------------------------------------------------------
    //
    // Port of the Pine v4 study "Higher High Lower Low Strategy"
    // ((c) LonesomeThecolor.blue, MPL 2.0); the engine is
    // OrbIx.Core.Structure.HhLlEngine, the same class trial-tested against the
    // independent Python reference. The inputs mirror the script's eight, with
    // Pine v4's own named-constant defaults (official v4 reference: lime
    // #00E676, red #FF5252, blue #2196F3, black #363A45).

    [InputParameter("HH/LL structure: enable", 100)]
    public bool HhLlEnabled { get; set; } = true;

    [InputParameter("HH/LL: left bars", 101, 1, 500, 1, 0)]
    public int HhLlLeftBars { get; set; } = 5;

    [InputParameter("HH/LL: right bars", 102, 1, 500, 1, 0)]
    public int HhLlRightBars { get; set; } = 5;

    [InputParameter("HH/LL: show support/resistance", 103)]
    public bool HhLlShowSupRes { get; set; } = true;

    [InputParameter("HH/LL: support colour", 104)]
    public Color HhLlSupportColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("HH/LL: resistance colour", 105)]
    public Color HhLlResistanceColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    [InputParameter("HH/LL: line style", 106, variants: new object[]
    {
        "Dotted", HhLlLineStyle.Dotted,
        "Dashed", HhLlLineStyle.Dashed,
        "Solid", HhLlLineStyle.Solid,
    })]
    public HhLlLineStyle HhLlLineStyle { get; set; } = HhLlLineStyle.Dotted;

    [InputParameter("HH/LL: line width", 107, 1, 5, 1, 0)]
    public int HhLlLineWidth { get; set; } = 3;

    [InputParameter("HH/LL: colour the bars by trend", 108)]
    public bool HhLlChangeBarColor { get; set; } = true;

    [InputParameter("HH/LL: up-trend bar colour", 109)]
    public Color HhLlUpColor { get; set; } = Color.FromArgb(0x21, 0x96, 0xF3);

    [InputParameter("HH/LL: down-trend bar colour", 110)]
    public Color HhLlDownColor { get; set; } = Color.FromArgb(0x36, 0x3A, 0x45);

    /// <summary>
    /// Bars whose structure trend is UNDECIDED — the engine's third state, which
    /// until now was painted with the DOWN colour.
    ///
    /// That was not cosmetic. HhLlEngine emits +1, -1 and 0, and the paint read
    /// `trend == 1 ? up : down`, so every bar the engine had NOT yet resolved was
    /// shown to the operator as bearish. A chart that reports "undecided" as
    /// "down" is not a neutral omission; it is the chart asserting a direction the
    /// engine never claimed.
    /// </summary>
    [InputParameter("HH/LL undecided bar colour", 385)]
    public Color HhLlUndecidedColor { get; set; } = Color.FromArgb(0x6B, 0x72, 0x80);

    /// <summary>Whether the direction panel is drawn.</summary>
    [InputParameter("Direction panel", 390)]
    public bool DirectionPanelOn { get; set; } = true;

    /// <summary>Whether the chart's own period is read as a structure lane.</summary>
    [InputParameter("Direction: include the chart's timeframe", 391)]
    public bool DirectionIncludeChartTimeframe { get; set; } = true;

    /// <summary>Higher timeframes the direction panel reads, in minutes.</summary>
    [InputParameter("Direction: higher timeframes (minutes)", 392)]
    public string DirectionHigherTimeframes { get; set; } = "5,15,60";

    /// <summary>Half-width of the flat band around VWAP, in ticks.</summary>
    [InputParameter("Direction: VWAP flat band (ticks)", 393, 0, 100, 0, 0)]
    public int DirectionVwapFlatTicks { get; set; } = 2;

    /// <summary>Cumulative delta inside which flow counts as flat.</summary>
    [InputParameter("Direction: delta flat band (contracts)", 394, 0, 1000000, 0, 0)]
    public int DirectionDeltaFlatContracts { get; set; } = 100;

    [InputParameter("Direction: panel left offset (px)", 395, 0, 4000, 0, 0)]
    public int DirectionPanelOffsetX { get; set; } = 12;

    /// <summary>
    /// Top inset of the direction panel, in pixels.
    ///
    /// DEFAULTED CLEAR OF THE CRT PANEL, WHICH IS WHY IT IS NOT 12. The CRT HTF
    /// Context indicator draws its own panel from `area.Top + 32` (its default
    /// PanelTopOffset) and runs to roughly y=164 at the panel font's height across
    /// its eight lines. A direction panel at 12 lands straight through it, and two
    /// overlapping panels are worse than either alone because neither reads.
    ///
    /// 220 clears that with margin at ordinary DPI. It is an INPUT, not a constant:
    /// a scaled display moves the CRT panel's real extent, so this is a sensible
    /// starting point rather than a measurement that holds everywhere.
    /// </summary>
    [InputParameter("Direction: panel top offset (px)", 396, 0, 4000, 0, 0)]
    public int DirectionPanelOffsetY { get; set; } = 220;

    // ---- Fibonacci retracement -------------------------------------------
    //
    // Measured on the structure the HH/LL port already finds, so the fib and the
    // swing chips can never disagree about where a swing is -- they read one
    // engine. Changing HhLlLeftBars/HhLlRightBars therefore moves the fib too,
    // which is intended: one structure, one set of knobs.

    [InputParameter("Fib: enable", 120)]
    public bool FibEnabled { get; set; } = true;

    [InputParameter("Fib: anchor", 121, variants: new object[]
    {
        "Live extreme", FibAnchorMode.LiveExtreme,
        "Confirmed swings", FibAnchorMode.ConfirmedSwings,
    })]
    public FibAnchorMode FibAnchorMode { get; set; } = FibAnchorMode.LiveExtreme;

    [InputParameter("Fib: show golden pocket", 122)]
    public bool FibShowGoldenPocket { get; set; } = true;

    [InputParameter("Fib: show extensions (1.272, 1.618)", 123)]
    public bool FibShowExtensions { get; set; }

    [InputParameter("Fib: show prices", 124)]
    public bool FibShowPrices { get; set; } = true;

    [InputParameter("Fib: line colour", 125)]
    public Color FibLineColor { get; set; } = Color.FromArgb(0xB0, 0xBE, 0xC5);

    [InputParameter("Fib: golden pocket colour", 126)]
    public Color FibPocketColor { get; set; } = Color.FromArgb(0xFF, 0xC1, 0x07);

    [InputParameter("Fib: line style", 127, variants: new object[]
    {
        "Dotted", HhLlLineStyle.Dotted,
        "Dashed", HhLlLineStyle.Dashed,
        "Solid", HhLlLineStyle.Solid,
    })]
    public HhLlLineStyle FibLineStyle { get; set; } = HhLlLineStyle.Dotted;

    [InputParameter("Fib: line width", 128, 1, 5, 1, 0)]
    public int FibLineWidth { get; set; } = 1;

    // ---- HTF zones + delta panel (approved plan 2026-08-28) --------------
    //
    // DISPLAY features, never signals: the level-retest entry class is
    // measured negative here (levels-null; ORB-IX replay P2) and trial 018
    // measured delta-agreement anti-predictive on MNQ 1m. The engines are
    // OrbIx.Core (ZoneEngine / RejectionBlockEngine / HtfBarBuilder /
    // DeltaSeriesEngine), golden-tested against tools/zone_reference.py.

    [InputParameter("Zones: enable (HTF FVG / order blocks)", 120)]
    public bool ZonesEnabled { get; set; } = true;

    [InputParameter("Zones: timeframe (e.g. 4h, 1h)", 121)]
    public string ZoneTimeframe { get; set; } = "4h";

    [InputParameter("Zones: bullish colour", 122)]
    public Color ZoneBullColor { get; set; } = Color.FromArgb(0x26, 0xA6, 0x9A);

    [InputParameter("Zones: bearish colour", 123)]
    public Color ZoneBearColor { get; set; } = Color.FromArgb(0xEF, 0x53, 0x50);

    [InputParameter("Zones: rejection blocks", 124)]
    public bool ZoneRejectionBlocks { get; set; } = true;

    /// <summary>
    /// Display filter (amendment approved 2026-08-28, "fix both"): a zone
    /// older than this many HTF bars stops drawing — 24 ever-extending
    /// bands striped the whole chart on the first deployed session.
    /// Detection is untouched; the skipped count shows in the status line.
    /// </summary>
    [InputParameter("Zones: max age, HTF bars", 125, 1, 500, 1, 0)]
    public int ZoneMaxAgeHtfBars { get; set; } = 30;

    /// <summary>
    /// Display filter (same amendment): skip drawing zones taller than
    /// this share of the average daily range — a 4H displacement body can
    /// be a 150-point box that swallows a 1m chart. When ADR is
    /// unmeasured the filter is off and the status line says so.
    /// </summary>
    [InputParameter("Zones: max height, % of ADR", 126, 5, 200, 5, 0)]
    public int ZoneMaxHeightAdrPercent { get; set; } = 50;

    [InputParameter("Delta: enable panel", 140)]
    public bool DeltaEnabled { get; set; } = true;

    [InputParameter("Delta: panel height, px", 141, 40, 300, 10, 0)]
    public int DeltaPanelHeightPx { get; set; } = 110;

    [InputParameter("Delta: up colour", 142)]
    public Color DeltaUpColor { get; set; } = Color.FromArgb(0x26, 0xA6, 0x9A);

    [InputParameter("Delta: down colour", 143)]
    public Color DeltaDownColor { get; set; } = Color.FromArgb(0xEF, 0x53, 0x50);

    // ---- imbalance ------------------------------------------------------------------------
    //
    // Diagonal footprint imbalance. NOTHING HERE HAS BEEN MEASURED TO PREDICT ANYTHING ON
    // THIS STACK; the thresholds live in orbix.default.json under "imbalance" and these
    // inputs govern the drawing only. The gate's own on/off is configuration rather than a
    // chart input, because a per-chart toggle on something that decides entries would make
    // two charts of one instrument disagree about what the system does.

    [InputParameter("Imbalance: draw on chart", 150)]
    public bool ImbalanceDrawEnabled { get; set; } = true;

    /// <summary>
    /// Draw isolated imbalanced rows as well as stacked ones. Off by default: the loose rows
    /// are numerous and the stack is the pattern anyone is reading for.
    /// </summary>
    [InputParameter("Imbalance: include unstacked rows", 151)]
    public bool ImbalanceDrawLoose { get; set; }

    [InputParameter("Imbalance: mark width, px", 152, 1, 40, 1, 0)]
    public int ImbalanceMarkWidthPx { get; set; } = 6;

    [InputParameter("Imbalance: buy colour", 153)]
    public Color ImbalanceBuyColor { get; set; } = Color.FromArgb(0x4F, 0xC3, 0xF7);

    [InputParameter("Imbalance: sell colour", 154)]
    public Color ImbalanceSellColor { get; set; } = Color.FromArgb(0xFF, 0xB3, 0x4D);

    // ---- absorption -----------------------------------------------------------------------
    //
    // ABSORPTION IS A MEASURED NULL ON MNQ (trial 008, 25,745 episodes, below the 2.76-tick
    // cost floor) and its ENTRY GATE is RECORDING ONLY as of 2026-09-09 -- it journals its
    // verdict and counterfactual but cannot veto, by the operator's decision. That switch
    // lives in orbix.default.json, not here: a per-chart toggle on something that decides
    // entries would let two charts of one instrument disagree about what the system does.
    // These inputs govern the drawing only.

    [InputParameter("Absorption: draw on chart", 160)]
    public bool AbsorptionDrawEnabled { get; set; } = true;

    [InputParameter("Absorption: bid-holding colour", 161)]
    public Color AbsorptionBidColor { get; set; } = Color.FromArgb(0x66, 0xBB, 0x6A);

    [InputParameter("Absorption: ask-holding colour", 162)]
    public Color AbsorptionAskColor { get; set; } = Color.FromArgb(0xE5, 0x73, 0x73);

    /// <summary>
    /// Research instrumentation, off by default: one NDJSON line per closed
    /// chart bar and per source into the output directory, for the
    /// registered parity study against the QuestDB research feed. Turning
    /// it on changes nothing on the chart.
    /// </summary>
    /// <summary>
    /// Where a session's CUMULATIVE delta changed sign, kept as a price level.
    ///
    /// "The delta flipped" is a statement about the running sum since the session opened, not
    /// about one bar printing against the last. The price at which it happened is the only part
    /// of the event still worth anything in a later session, which is why the output is a level.
    ///
    /// A REFERENCE PRICE, NEVER A SIGNAL. Nothing about flip levels has been measured, and
    /// cumulative-delta divergence is a measured null on this instrument (trial 025). The chart
    /// caption says so.
    /// </summary>
    [InputParameter("Flip levels: draw", 1000)]
    public bool ShowDeltaFlips { get; set; } = true;

    /// <summary>
    /// How far beyond zero cumulative delta must travel before a crossing counts, in contracts.
    ///
    /// A crossing that comes back before then leaves no level: the level is anchored to the bar
    /// that CROSSED, and confirmation is what decides whether that bar is drawn at all. Zero marks
    /// every change of sign, which is a legitimate setting and a noisy one.
    /// </summary>
    [InputParameter("Flip levels: confirm after (contracts past zero)", 1005, 0, 1000000, 1, 0)]
    public int FlipConfirmContracts { get; set; } = 250;

    /// <summary>
    /// Whether the session's first move off zero counts as a flip.
    ///
    /// Cumulative delta starts each session at zero, so the first move away from it is the session
    /// CHOOSING a side rather than reversing one. Off by default, or every session opens with a
    /// level on it.
    /// </summary>
    [InputParameter("Flip levels: mark the session's first side", 1010)]
    public bool FlipMarkFirstSide { get; set; }

    /// <summary>Newest kept when the cap bites. Older levels are drawn fainter, not dropped.</summary>
    [InputParameter("Flip levels: most to draw", 1015, 1, 40, 1, 0)]
    public int MaxDeltaFlips { get; set; } = 6;

    /// <summary>
    /// Restricts flip levels to the session in progress.
    ///
    /// OFF BY DEFAULT, because keeping earlier sessions is the point of keeping levels at all: a
    /// price that two separate sessions both turned on has done it twice.
    /// </summary>
    [InputParameter("Flip levels: this session only", 1020)]
    public bool DeltaFlipsThisSessionOnly { get; set; }

    [InputParameter("Flip levels: up colour", 1025)]
    public Color DeltaFlipUpColor { get; set; } = Color.FromArgb(90, 200, 140);

    [InputParameter("Flip levels: down colour", 1030)]
    public Color DeltaFlipDownColor { get; set; } = Color.FromArgb(225, 105, 120);

    /// <summary>
    /// Prices that repeatedly absorbed one-sided aggression across the footprint -- the "lines in
    /// the sand".
    ///
    /// THIS IS NOT AN IDENTIFIED ICEBERG, and there is no order-book data behind it. A genuine
    /// refreshing iceberg leaves this footprint; so does one large resting order that was never
    /// replenished; so does a price that simply kept attracting business. What is measured is
    /// repeat one-sided absorption at a single price.
    ///
    /// AND ABSORPTION IS A MEASURED NULL ON MNQ: trial 008, 25,745 episodes, +1.006 ticks against
    /// matched-random, failing Bonferroni and below the 2.76-tick cost floor. Reference only; this
    /// feeds no playbook and gates no entry.
    /// </summary>
    [InputParameter("Shelves: draw (lines in the sand)", 1100)]
    public bool ShowAbsorptionShelves { get; set; } = true;

    /// <summary>
    /// How many closed bars the scan reads, ending at the newest.
    ///
    /// A FIXED window, never the visible range: a level that moved when the chart scrolled would
    /// not be a level.
    /// </summary>
    [InputParameter("Shelves: look back (bars)", 1105, 5, 480, 5, 0)]
    public int ShelfWindowBars { get; set; } = 60;

    /// <summary>Contracts traded at the price across the whole window.</summary>
    [InputParameter("Shelves: needs volume of", 1110, 0, 10000000, 10, 0)]
    public int ShelfMinVolume { get; set; } = 400;

    /// <summary>
    /// The same volume as a share of everything the window traded, so a quiet overnight hour and a
    /// news bar stay comparable.
    /// </summary>
    [InputParameter("Shelves: needs a share of the window (%)", 1115, 0, 100, 1, 0)]
    public int ShelfMinSharePercent { get; set; } = 3;

    /// <summary>
    /// How one-sided the aggression into it was, measured against CLASSIFIED volume only.
    ///
    /// Volume that could not be attributed to a side is evidence about nothing, and folding it into
    /// the denominator would make a well-classified price look less one-sided than a poorly
    /// classified one.
    /// </summary>
    [InputParameter("Shelves: needs a lean of (%)", 1120, 0, 100, 1, 0)]
    public int ShelfMinLeanPercent { get; set; } = 40;

    /// <summary>
    /// Separate bars that traded there. One enormous print is a large trade, not a price that kept
    /// reloading, and this is the condition that tells them apart.
    /// </summary>
    [InputParameter("Shelves: needs this many bars", 1125, 1, 200, 1, 0)]
    public int ShelfMinBars { get; set; } = 4;

    /// <summary>
    /// Refuses a price whose largest single print exceeds this share of its whole volume.
    ///
    /// The bar count above can be satisfied by one huge print plus a scattering of small ones; this
    /// closes that door. 100 disables the check.
    /// </summary>
    [InputParameter("Shelves: refuse if one print exceeds (% of volume)", 1130, 1, 100, 1, 0)]
    public int ShelfMaxOnePrintPercent { get; set; } = 60;

    /// <summary>
    /// A CLOSE this far beyond the shelf, against the passive side, counts as it going.
    ///
    /// Closes, not wicks: price reaching through a level and coming back is the level working, and
    /// counting that would discard every shelf that ever did its job.
    /// </summary>
    [InputParameter("Shelves: broken after (ticks)", 1135, 0, 200, 1, 0)]
    public int ShelfThroughTicks { get; set; } = 4;

    /// <summary>
    /// Keeps shelves that were closed through, drawn dashed and faint.
    ///
    /// Where the size defending a price got run over is worth seeing; hiding it would leave the
    /// chart claiming the level never existed.
    /// </summary>
    [InputParameter("Shelves: keep the ones that broke", 1140)]
    public bool ShelfKeepBroken { get; set; }

    /// <summary>Heaviest kept when the cap bites, so the cap keeps what matters.</summary>
    [InputParameter("Shelves: most to draw", 1145, 1, 40, 1, 0)]
    public int MaxAbsorptionShelves { get; set; } = 5;

    [InputParameter("Shelves: colour", 1150)]
    public Color AbsorptionShelfColor { get; set; } = Color.FromArgb(235, 185, 90);

    /// <summary>Labels on the flip and shelf lines. The chart caption is separate and always drawn.</summary>
    [InputParameter("Levels: label the flip and shelf lines", 1155)]
    public bool LabelDeltaLevels { get; set; } = true;


    // ── The twelve switches for the tools absorbed from Aramid Flow ──
    //
    // TWELVE IS THE WHOLE BUDGET AND IT IS THE REQUIREMENT, not a preference. Aramid Flow
    // carried 146 inputs against ORB-IX's 116; merging them as inputs would have produced a
    // 262-row dialog, which is the clutter "without clutter" ruled out. So each tool keeps one
    // switch here and everything else it had — thresholds, ratios, look-backs, colours, alert
    // distances — lives in the "flow" block of orbix.default.json.
    //
    // TWO OF ARAMID FLOW'S TOOLS HAVE NO SWITCH HERE AND ARE NOT LOST. Its volume profile and
    // its VWAP are concepts ORB-IX already draws; the rule for this merge is that where both
    // implement a concept, ORB-IX's wins. They return as anchors on the profile and VWAP
    // inputs above rather than as a second pair of overlays.
    //
    // NOTHING HERE CLAIMS AN EDGE. Aramid Flow's README: "Display only. Nothing here claims an
    // edge." Absorption and imbalance are MEASURED NULLS on this instrument (trial 008).
    //
    // DEFAULTS ARE ARAMID FLOW'S OWN, tool for tool, so a chart that had both indicators loaded
    // shows the same things afterwards as it did before.

    /// <summary>
    /// Silences all eleven at once without losing which of them were configured on.
    ///
    /// The one control that makes the other eleven safe to leave set up: there is a
    /// focus-mode precedent in the operator overlay for exactly this, and clearing the chart
    /// by unticking eleven boxes and re-ticking them later is how settings get lost.
    /// </summary>
    [InputParameter("Flow: draw the absorbed tools", 1200)]
    public bool FlowEnabled { get; set; } = true;

    [InputParameter("Flow: cluster statistics (numeric rows per bar)", 1201)]
    public bool FlowClusterStatistics { get; set; } = true;

    [InputParameter("Flow: cluster search (mark clusters matching the filters)", 1202)]
    public bool FlowClusterSearch { get; set; } = true;

    [InputParameter("Flow: stacked imbalance lines", 1203)]
    public bool FlowStackedImbalance { get; set; } = true;

    [InputParameter("Flow: absorption stack lines", 1204)]
    public bool FlowAbsorption { get; set; } = true;

    [InputParameter("Flow: unfinished auction lines", 1205)]
    public bool FlowUnfinishedAuction { get; set; } = true;

    [InputParameter("Flow: big trades", 1206)]
    public bool FlowBigTrades { get; set; } = true;

    [InputParameter("Flow: live counter (forming bar's buy/sell contracts)", 1207)]
    public bool FlowLiveCounter { get; set; } = true;

    [InputParameter("Flow: DOM levels (largest resting size)", 1208)]
    public bool FlowDomLevels { get; set; } = true;

    // OFF, as Aramid Flow shipped them. These three draw across the whole pane, and a chart
    // that opens covered in diagonals is one the operator turns off rather than reads.
    [InputParameter("Flow: trend lines (two-tap highs and lows)", 1209)]
    public bool FlowTrendLines { get; set; }

    [InputParameter("Flow: fib fan", 1210)]
    public bool FlowFibFan { get; set; }

    /// <summary>
    /// Gamma-exposure walls, read from an AramidGamma file or set by hand in the document.
    ///
    /// OFF, and not only because Aramid Flow shipped it off: the options feed behind that file
    /// is gone, so a file that still exists is exactly as stale as its last write.
    /// </summary>
    [InputParameter("Flow: GEX walls", 1211)]
    public bool FlowGex { get; set; }

    /// <summary>
    /// Heavy volume at a price the bar could not leave, MODERATE tier. On by default: the
    /// thresholds were solved to draw roughly ten to fifteen marks a session, which is a
    /// readable chart rather than a wall of lines.
    /// </summary>
    [InputParameter("Flow: absorption tier 1 (moderate)", 1212)]
    public bool FlowVolumeAbsorptionTier1 { get; set; } = true;

    /// <summary>The same reading at the HEAVY threshold, drawn brighter. Three to six a session.</summary>
    [InputParameter("Flow: absorption tier 2 (heavy)", 1213)]
    public bool FlowVolumeAbsorptionTier2 { get; set; } = true;

    /// <summary>
    /// Drives the absorbed engines and assembles what they draw. Null until a chart with a
    /// measurable time-bar period exists — see <see cref="MeasurableBarPeriod"/>.
    /// </summary>
    private OrbCore.Flow.FlowFrameBuilder? flowBuilder;

    /// <summary>The period the builder was made for, so a chart re-aggregated rebuilds it.</summary>

    /// <summary>The newest assembled frame. Read by paint; written only under the fold gate.</summary>
    private OrbCore.Flow.FlowFrame flowFrame = OrbCore.Flow.FlowFrame.Empty;

    /// <summary>
    /// The swing engine the absorbed trend lines run on.
    ///
    /// ITS OWN INSTANCE, NOT ORB-IX'S. Sharing <c>hhllEngine</c> looked obvious and is a trap: that
    /// one is set to NULL and its bars dropped whenever the HH/LL switch is off, so the absorbed
    /// trend lines would have vanished with an unrelated toggle and nothing would have said why.
    /// Flow also carries its own pivot settings, which sharing would have left reading nothing.
    ///
    /// This is ONE implementation used twice with different parameters — the same shape as the
    /// Direction lanes running several BarAggregators at different periods — not two
    /// implementations of one concept.
    /// </summary>
    private HhLlEngine? flowStructure;

    private (int Left, int Right) flowStructureParams;

    /// <summary>Bars already fed to <see cref="flowStructure"/>, so feeding stays incremental.</summary>
    private int flowStructureBars;

    /// <summary>The fold the chart-bar cache was filled for, so it is read once per fold.</summary>
    private DateTime flowBarsFoldUtc = DateTime.MinValue;

    /// <summary>Whether the absorbed displays' history has been seeded for this builder.</summary>
    private bool flowSeeded;

    /// <summary>
    /// Whether the profile tick load has finished, however it finished.
    ///
    /// THE SEEDING WAITS ON THIS RATHER THAN ON A TIMER. It used to wait thirty seconds and then
    /// seed regardless; measured on the operator's chart 2026-09-13, the load took 35.0 seconds to
    /// return 1,806,076 prints — so the seeding fired five seconds early, found no cache, and
    /// seeded nothing from a source that was about to be ready. A deadline cannot know how long a
    /// platform call will take; the call itself can.
    /// </summary>
    private volatile bool profileTickLoadFinished;

    /// <summary>
    /// Why the footprint tools are not running, or empty when they are.
    ///
    /// A PROBLEM RATHER THAN SILENCE. A frame that is empty because the chart cannot carry
    /// footprints looks exactly like one that is empty because the market is quiet, and the two
    /// need different actions from the reader.
    /// </summary>
    private string flowPeriodProblem = string.Empty;

    /// <summary>The parsed gamma snapshot, or null when there is no readable file.</summary>
    private OrbCore.Flow.GexSnapshot? flowGex;

    /// <summary>Where the gamma walls came from and how old they are. Always said when they draw.</summary>
    private string flowGexStatus = string.Empty;

    /// <summary>When the gamma file was last read, so it is not re-read on every fold.</summary>
    private DateTime flowGexReadUtc = DateTime.MinValue;

    private readonly List<DateTime> flowBarOpen = new();
    private readonly List<double> flowBarHigh = new();
    private readonly List<double> flowBarLow = new();
    private readonly List<double> flowBarClose = new();

    /// <summary>
    /// Drives the absorbed displays for one fold: depth in, clock forward, frame out.
    ///
    /// RUNS UNDER THE FOLD GATE, like everything else that reads shared engine state. The builder
    /// owns a footprint history and a depth ladder; both are written here and read by paint, and
    /// the gate is what stops paint seeing a half-built frame.
    ///
    /// NOTHING HERE DECIDES A TRADE. Every engine it drives is a display, and the frame it
    /// produces reaches the chart and the log and nothing else.
    /// </summary>
    private void FoldFlow(DateTime nowUtc, OrbCore.Flow.FlowDisplay? display)
    {
        if (display is null || this.config is not { } loaded)
        {
            this.flowFrame = OrbCore.Flow.FlowFrame.Empty;
            return;
        }

        // THE BIAS GEOMETRY NEEDS NO BAR PERIOD AT ALL, so it is built before the gate below.
        // Fans and trend lines are laid out in bar INDEX and the gamma walls are prices; none of
        // the three cares how long a bar lasts. Leaving them behind the gate meant a chart with no
        // time period lost all eleven tools when only two of them needed one — observed on the
        // operator's chart 2026-09-13, where "chart period none (not a time chart)" silently
        // emptied the whole frame.
        var bias = this.BuildFlowBias(loaded, display);

        // REBUILT WHEN THE CHART IS RE-AGGREGATED. The builder buckets footprints on the chart's
        // period, so a builder made for a one-minute chart is the wrong object for a five-minute
        // one — its history would be a mixture of both.
        //
        // A CHART WITH NO PERIOD CARRIES FOOTPRINTS TOO, and it did not until now. The engine
        // required a bar PERIOD, so a tick, range or Renko chart never built one at all --
        // measured on the operator's MNQ tick chart 2026-09-14: 1,413 prints judged by the
        // aggressor check while the frame reported "closed bars 0, stat columns 0, counter
        // none". Prints were arriving and nothing was built from them. The chart knows where
        // its own bars begin, so the engine asks instead of calculating.
        //
        // Reaching the refusal below now means only one thing: a TIME chart whose period the
        // platform has not reported yet, which is a transient state rather than a kind of chart.
        if (this.FlowBoundaries(nowUtc, out var boundariesKind) is not { } boundaries)
        {
            // THE BUILDER IS KEPT, NOT DESTROYED. The period reads as absent while the platform is
            // still handing over history — measured on the operator's chart 2026-09-13, where one
            // instance logged "chart period none" at 06:06:15 and the next read 1m at 06:07:16 on
            // the SAME 1-minute chart. Nulling the builder on that transient reading threw away a
            // seeded history and every footprint accumulated since, then rebuilt and re-seeded
            // from nothing on the next fold.
            this.flowFrame = OrbCore.Flow.FlowFrame.Empty with { Bias = bias };
            // THE WORDING NARROWED WITH THE CONDITION. This used to be reached by every
            // non-time chart, so "waiting for the bar period" described a permanent state as
            // though it were a delay. A chart-owned bar no longer needs a period, so the only
            // way here is a chart whose bars this indicator has not been able to read yet.
            this.flowPeriodProblem = this.flowBuilder is null
                ? "flow: waiting for the chart's bars — the footprint tools start once the "
                  + "platform has served them."
                : "flow: the chart's bars are momentarily unreadable; the footprint tools "
                  + "resume with their history intact when they return.";
            return;
        }

        this.flowPeriodProblem = string.Empty;

        // KIND, NOT PERIOD. The builder buckets footprints on the chart's own bars, so a builder
        // made for a one-minute chart is the wrong object for a five-minute one -- its history
        // would be a mixture of both. The same is true across KINDS: a builder made for a tick
        // chart must not survive a switch to time bars. The kind string carries both.
        if (this.flowBuilder is null || this.flowBoundariesKind != boundariesKind)
        {
            this.flowBuilder = new OrbCore.Flow.FlowFrameBuilder(
                loaded.Flow, this.FlowSessionBoundary(loaded), this.instrument.TickSize, boundaries)
            {
                SessionZone = ResolveZone(loaded.SessionTimeZone),
            };

            this.flowBoundariesKind = boundariesKind;
            this.flowSeeded = false;
        }


        // SEEDED WHEN THE DATA EXISTS, NOT WHEN THE BUILDER IS MADE. Measured 2026-09-12: the
        // profile tick cache finishes loading TWO SECONDS after the builder is constructed, so
        // seeding at construction found no tick history and — on a connection that serves no
        // per-price vendor levels either — seeded nothing at all. That is what made the first
        // deploy draw nothing.
        //
        // The wait is bounded so a connection that never serves tick history still seeds from
        // whatever vendor levels it does have, rather than waiting forever for a source that is
        // not coming.
        if (!this.flowSeeded
            && (this.profileTickCache is not null || this.profileTickLoadFinished))
        {
            this.flowSeeded = true;
            this.SeedFlowHistory(this.flowBuilder, loaded, display);
        }

        var builder = this.flowBuilder;

        this.depthRing.Drain((in OrbCore.Flow.DepthUpdate update) => builder.OnBook(update));

        builder.OnClock(
            nowUtc, TimeSpan.FromMilliseconds(loaded.Flow.Ingestion.CloseGraceMs), display);

        builder.OnPrice(this.lastPrice, display);
        builder.ScanFormingBar(display);

        this.PullBook(nowUtc, loaded.Flow);

        this.flowFrame = builder.Build(display, nowUtc, bias);

        // CORE SAYS WHAT TO RAISE; THE SHELL DECIDES HOW. These go to the log rather than to a
        // platform alert: an alert the operator did not ask for is an interruption, and every
        // alert switch in the document ships off.
        foreach (var alert in builder.DrainAlerts())
            this.Report($"flow alert: {alert.Text}");
    }

    /// <summary>
    /// The reference geometry over the chart's own bars: fans, trend lines, gamma walls.
    ///
    /// READ FROM THE CHART EACH FOLD rather than accumulated, because the geometry is a function of
    /// the visible history and the history is re-served whenever the chart is re-aggregated. The
    /// FORMING bar is excluded: a swing that has not closed is not a swing.
    /// </summary>
    private OrbCore.Flow.FlowBias BuildFlowBias(OrbIxConfig loaded, OrbCore.Flow.FlowDisplay display)
    {
        if (!display.FibFan && !display.TrendLines && !display.Gex)
            return OrbCore.Flow.FlowBias.Empty;

        if (display.Gex)
            this.RefreshGexFile(loaded);

        if (!this.EnsureFlowBars())
            return OrbCore.Flow.FlowBias.Empty;

        var wanted = (
            Math.Max(loaded.Flow.TrendLines.PivotLeftBars, 1),
            Math.Max(loaded.Flow.TrendLines.PivotRightBars, 1));

        // REBUILT WHEN THE PIVOTS MOVE, OR WHEN THE CHART RE-SERVES ITS HISTORY. The engine's
        // labels are a function of its parameters, so one built for a five-bar pivot is the wrong
        // object for a ten-bar one; and a bar list that got SHORTER means the platform handed back
        // a different history, which the engine cannot be walked backwards through.
        if (this.flowStructure is null
            || this.flowStructureParams != wanted
            || this.flowStructureBars > this.flowBarHigh.Count)
        {
            this.flowStructure = new HhLlEngine(wanted.Item1, wanted.Item2);
            this.flowStructureParams = wanted;
            this.flowStructureBars = 0;
        }

        // FED INCREMENTALLY. The engine appends, so re-feeding a bar it has already seen would
        // double-count it — and re-feeding the whole history several times a second would walk
        // thousands of bars per fold for no new information.
        for (var i = this.flowStructureBars; i < this.flowBarHigh.Count; i++)
            this.flowStructure.Feed(this.flowBarHigh[i], this.flowBarLow[i], this.flowBarClose[i]);

        this.flowStructureBars = this.flowBarHigh.Count;

        return OrbCore.Flow.FlowBiasBuilder.Build(
            this.flowBarOpen, this.flowBarHigh, this.flowBarLow, this.flowBarClose,
            this.flowStructure.Labels, loaded.Flow, display,
            this.flowGex, this.lastPrice, this.flowGexStatus);
    }

    /// <summary>
    /// Fills in the bars the vendor served no per-price levels for, from tick history.
    ///
    /// WHY THIS EXISTS: on the operator's connection the platform serves no per-price levels for
    /// this symbol at all, so seeding on them produced ZERO bars and every level display drew
    /// nothing. ORB-IX's own profiles already solved this — the tick cache holds per-minute
    /// per-price volume for the loaded span, and it served 1,099,969 prints on this chart.
    ///
    /// A BAR SHORTER THAN A MINUTE CANNOT BE FILLED THIS WAY. The cache's granularity is one
    /// minute because that is the granularity the connector aggregates at; slicing a fifteen-second
    /// bar out of it would hand back the whole minute's volume four times over. Those bars are
    /// skipped and counted rather than filled with a number that looks right and is four times too
    /// large.
    ///
    /// TRADE COUNTS ARE NOT AVAILABLE and the bar says so. The cache aggregates prints per minute,
    /// not per price, so a bar rebuilt from it carries volume and an UNMEASURED trade count —
    /// which the statistics band shades rather than printing as a bright zero.
    /// </summary>
    /// <returns>How many bars were filled from tick history.</returns>
    private int SeedFlowFromTickHistory(
        List<OrbCore.Flow.FootprintBar> seeded,
        List<(DateTime OpenUtc, DateTime CloseUtc, double Open, double High, double Low, double Close)> bars,
        out int tooShortForCache)
    {
        tooShortForCache = 0;

        if (this.profileTickCache is not { } cache || bars.Count == 0)
            return 0;

        // SUB-MINUTE BARS CANNOT BE SEEDED FROM THIS CACHE, whatever the chart's aggregation.
        // The bars the vendor already covered keep their vendor levels: those carry real trade
        // counts and real delta extremes, which tick history cannot supply.
        var already = new HashSet<DateTime>(seeded.Count);

        foreach (var bar in seeded)
            already.Add(bar.OpenUtc);

        var filled = 0;
        var staged = new List<OrbCore.Flow.FootprintBar.SeededLevel>(256);

        foreach (var (openUtc, closeUtc, open, high, low, close) in bars)
        {
            if (already.Contains(openUtc))
                continue;

            // A BAR SHORTER THAN A MINUTE CANNOT BE SLICED FROM THIS CACHE, and the decision is
            // PER BAR because that is what it is about.
            //
            // THE REGRESSION THIS FIXES, MEASURED ON THE OPERATOR'S OWN HOST. This began as a
            // single guard over the whole set -- find the shortest bar, and if it was under a
            // minute abandon the entire seed. That is a global abort driven by a per-bar
            // property: ONE short bar anywhere in twenty-four hours of history, a partial bar at
            // a session boundary or across a data gap, silently cost every other bar its seed.
            // ryzen-pc seeded 277 bars from tick history at 00:13Z and 0 at 01:22Z on the build
            // that introduced it, with no problem line to say why.
            //
            // The cache is keyed by MINUTE, so a shorter bar would be handed the whole minute
            // containing it. Skipping that bar is the answer; abandoning its neighbours is not.
            if (closeUtc - openUtc < TimeSpan.FromMinutes(1))
            {
                tooShortForCache++;
                continue;
            }

            var slice = cache.Slice(openUtc, closeUtc);

            if (slice.Levels.Count == 0)
                continue;

            staged.Clear();

            foreach (var level in slice.Levels)
            {
                staged.Add(new OrbCore.Flow.FootprintBar.SeededLevel(
                    level.Price, level.Buy, level.Sell, level.Unclassified,
                    BuyTrades: 0, SellTrades: 0, UnclassifiedTrades: 0, MaxOneTrade: 0));
            }

            seeded.Add(OrbCore.Flow.FootprintBar.FromLevels(
                openUtc, closeUtc, this.instrument.TickSize, staged,
                open, high, low, close,
                // Tick history gives no intra-bar delta path, so the extremes stay UNMEASURED
                // rather than becoming zero.
                double.NaN, double.NaN,
                OrbCore.Flow.FootprintSource.TickHistory,
                tradesMeasured: false));

            filled++;
        }

        // Seeding requires ascending order, and the two sources interleave by bar.
        seeded.Sort(static (a, b) => a.OpenUtc.CompareTo(b.OpenUtc));

        return filled;
    }

    /// <summary>
    /// The open time of the newest CLOSED chart bar, or null when the chart has none.
    ///
    /// SEEDING IS MEASURED IN TRADING TIME, NOT WALL-CLOCK TIME, and this is what makes that
    /// possible. Anchoring a look-back on DateTime.UtcNow asks for the last N hours of the CLOCK:
    /// run on a Sunday morning, a 24-hour window covers Saturday, in which nothing traded.
    /// Measured on the operator's chart 2026-09-13 — "MNQU6 returned no usable prints over 25
    /// request(s) for 2026-09-12T06:07Z to 2026-09-13T06:07Z", and zero bars examined, because
    /// Friday's session had ended some nine hours before the window even opened.
    ///
    /// Anchored on the newest bar instead, "24 hours of history" means the 24 hours up to the last
    /// thing that happened, which is what a reader means by it and what a weekend cannot empty.
    /// </summary>
    private DateTime? NewestClosedBarUtc()
    {
        if (this.HistoricalData is not { Count: > 1 } bars)
            return null;

        // The forming bar is excluded, as everywhere else: it has not closed.
        for (var i = bars.Count - 2; i >= 0; i--)
        {
            if (bars[i, SeekOriginHistory.Begin] is HistoryItemBar bar)
                return bar.TimeLeft;
        }

        return null;
    }

    /// <summary>
    /// Caches this fold's CLOSED chart bars, and says whether there are any.
    ///
    /// HOISTED OUT OF THE BIAS BUILD ON PURPOSE. Two things need these bars — the bias geometry
    /// and the look-back-high anchor — and reading them only inside the bias build would have made
    /// the anchor depend on whether the fan or the trend lines happened to be switched on. That is
    /// the same hidden coupling that reusing ORB-IX's own structure engine would have introduced,
    /// and it fails the same way: a setting stops working because of an unrelated switch and
    /// nothing says why.
    ///
    /// READ ONCE PER FOLD. The bars do not change between two calls in one fold, and a chart with
    /// thousands of them is not worth walking twice several times a second.
    ///
    /// THE FORMING BAR IS EXCLUDED. A swing that has not closed is not a swing, and an anchor at a
    /// bar still filling would move under the line it anchors.
    /// </summary>
    private bool EnsureFlowBars()
    {
        if (this.flowBarsFoldUtc == this.lastFoldUtc && this.flowBarOpen.Count > 0)
            return true;

        this.flowBarsFoldUtc = this.lastFoldUtc;
        this.flowBarOpen.Clear();
        this.flowBarHigh.Clear();
        this.flowBarLow.Clear();
        this.flowBarClose.Clear();

        if (this.HistoricalData is not { Count: > 1 } bars)
            return false;

        for (var i = 0; i < bars.Count - 1; i++)
        {
            if (bars[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                continue;

            this.flowBarOpen.Add(bar.TimeLeft);
            this.flowBarHigh.Add(bar.High);
            this.flowBarLow.Add(bar.Low);
            this.flowBarClose.Add(bar.Close);
        }

        return this.flowBarOpen.Count > 0;
    }

    /// <summary>
    /// Re-reads the gamma levels file, at most once a minute.
    ///
    /// THE SOURCE IS DEAD AND THE STATUS SAYS SO EVERY TIME. Nothing writes this file any more —
    /// the options feed behind it is gone — so a file that still exists is exactly as stale as its
    /// last write. The age is carried to the chart rather than inferred, because a wall drawn
    /// without it reads as current.
    ///
    /// A READ THAT FAILS LEAVES THE LAST GOOD SNAPSHOT AND SAYS WHY. Dropping the levels on a
    /// transient read error would make the walls flicker; pretending the read succeeded would be
    /// worse.
    /// </summary>
    private void RefreshGexFile(OrbIxConfig loaded)
    {
        var path = loaded.Flow.Gex.LevelsFile;

        if (string.IsNullOrWhiteSpace(path))
        {
            this.flowGex = null;
            this.flowGexStatus = "gamma: manual prices only, no file configured";
            return;
        }

        var nowUtc = DateTime.UtcNow;

        if (nowUtc - this.flowGexReadUtc < TimeSpan.FromMinutes(1))
            return;

        this.flowGexReadUtc = nowUtc;

        try
        {
            if (!File.Exists(path))
            {
                this.flowGex = null;
                this.flowGexStatus = "gamma: no levels file, manual prices only";
                return;
            }

            var written = File.GetLastWriteTimeUtc(path);
            var parsed = OrbCore.Flow.GexLevels.Parse(File.ReadAllText(path));

            if (parsed is null)
            {
                this.flowGex = null;
                this.flowGexStatus = "gamma: levels file carries no levels";
                return;
            }

            this.flowGex = parsed;
            this.flowGexStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"gamma: file written {(nowUtc - written).TotalHours:N1}h ago — the options source is gone, so it is not refreshed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            this.flowGexStatus = PathDisplay.Redact(
                $"gamma: levels file could not be read ({ex.GetType().Name}); showing the last good read");
        }
    }

    /// <summary>
    /// Fills the absorbed displays' history from the chart's own volume analysis.
    ///
    /// WITHOUT THIS A LOOK-BACK OF THREE DAYS MEANS NOTHING. The level displays look back whole
    /// days; on a fresh attach they would draw nothing until bars formed under a chart that was
    /// already open, which is less than the indicator they replace does today.
    ///
    /// THE SOURCE IS THE ONE ORB-IX ALREADY READS. The volume profiles take per-bar price levels
    /// from exactly these bars, so this adds a reader rather than a request — no extra platform
    /// call, no second definition of what a historical bar's per-price volume is.
    ///
    /// WHAT IS TAKEN AND WHAT IS NOT. Trade counts, the largest single print and the bar's delta
    /// extremes all come from the platform, which watched the print order this bar no longer has.
    /// A bar whose levels the platform has not computed is COUNTED and skipped, never filled with
    /// zeroes: an empty bar and an uncomputed one look identical afterwards and mean opposite
    /// things.
    /// </summary>
    private void SeedFlowHistory(
        OrbCore.Flow.FlowFrameBuilder builder,
        OrbIxConfig loaded,
        OrbCore.Flow.FlowDisplay display)
    {
        if (!this.SeedFromHistory || this.HistoricalData is not { Count: > 1 } bars)
            return;

        // HOW MUCH HISTORY TO REBUILD, which is not how long a line stays visible. Deriving this
        // from daysLookBack asked for five days of tick history to draw lines that are mostly
        // stale; Aramid Flow carried the two as separate numbers and was right to.
        var hours = Math.Max(loaded.Flow.Ingestion.SeedHoursBack, 1);

        // TRADING TIME, NOT WALL-CLOCK TIME. See NewestClosedBarUtc: anchored on "now", a 24-hour
        // window run on a Sunday covers a Saturday and finds nothing.
        var anchor = this.NewestClosedBarUtc() ?? DateTime.UtcNow;
        var from = anchor.AddHours(-hours);
        var seeded = new List<OrbCore.Flow.FootprintBar>();
        var fromBars = new List<(DateTime OpenUtc, DateTime CloseUtc, double Open, double High, double Low, double Close)>();
        var withoutLevels = 0;
        var readRaces = 0;
        var staged = new List<OrbCore.Flow.FootprintBar.SeededLevel>(64);

        // The FORMING bar is excluded: it is still filling, and seeding it would publish levels
        // for a bar that can still change.
        for (var i = 0; i < bars.Count - 1; i++)
        {
            if (bars[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                continue;

            if (bar.TimeLeft < from)
                continue;

            fromBars.Add((bar.TimeLeft, bar.TimeRight, bar.Open, bar.High, bar.Low, bar.Close));

            var analysis = bar.VolumeAnalysisData;
            var levels = analysis?.PriceLevels;

            if (analysis is null || levels is null || levels.Count == 0)
            {
                withoutLevels++;
                continue;
            }

            staged.Clear();

            try
            {
                foreach (var level in levels)
                {
                    var item = level.Value;
                    var unclassified = Math.Max(0, item.Volume - item.BuyVolume - item.SellVolume);

                    staged.Add(new OrbCore.Flow.FootprintBar.SeededLevel(
                        level.Key, item.BuyVolume, item.SellVolume, unclassified,
                        item.BuyTrades, item.SellTrades,
                        Math.Max(0, item.Trades - item.BuyTrades - item.SellTrades),
                        item.MaxOneTradeVolume));
                }
            }
            catch (InvalidOperationException)
            {
                // The platform's calculation thread mutated the dictionary mid-read. The bar is
                // counted and skipped; a later attach sees it settled. Exactly the race the
                // profile scan already handles this way.
                readRaces++;
                continue;
            }

            seeded.Add(OrbCore.Flow.FootprintBar.FromLevels(
                bar.TimeLeft, bar.TimeRight, this.instrument.TickSize, staged,
                bar.Open, bar.High, bar.Low, bar.Close,
                analysis.Total.MinDelta, analysis.Total.MaxDelta,
                OrbCore.Flow.FootprintSource.VendorLevels));
        }

        // THE FALLBACK ORB-IX ALREADY USES, AND WHICH THIS MISSED. On the operator's own
        // connection the platform serves NO per-price volume-analysis levels for this symbol
        // (ALLOW_VOLUME_ANALYSIS_FROM_TICK_HISTORY=NotAllowed, observed 2026-09-12: 2,755 bars,
        // every one without levels). The profiles hit the same wall and read TICK HISTORY
        // instead — a million prints on this very chart. Seeding on the vendor levels alone
        // meant the level displays had no history at all and drew nothing, which is exactly what
        // the first deploy showed.
        var fromTicks = this.SeedFlowFromTickHistory(seeded, fromBars, out var tooShortForCache);

        var (accepted, refused) = builder.Seed(seeded, display);

        this.Report(
            $"flow history: {accepted} bar(s) seeded over {hours} hour(s) "
            + $"({accepted - fromTicks} from vendor levels, {fromTicks} from tick history), "
            + $"{refused} refused, {withoutLevels} bar(s) carried no vendor levels, "
            + $"{readRaces} read race(s)."
            // NAMED ONLY WHEN IT HAPPENED, so the line does not grow on a chart where it never
            // will -- and so that a seed of zero can never again be reported without its reason.
            + (tooShortForCache > 0
                ? $" {tooShortForCache} bar(s) shorter than the minute-keyed tick cache can slice."
                : string.Empty));
    }

    /// <summary>
    /// Where the absorbed displays' session-cumulative figures reset.
    ///
    /// The document names one of ORB-IX's OWN sessions rather than carrying a second start time
    /// and zone, so this resolves that name against the session clock the rest of the engine
    /// already runs on.
    /// </summary>
    private OrbCore.Flow.ISessionBoundary FlowSessionBoundary(OrbIxConfig loaded)
    {
        if (loaded.Flow.Ingestion.SessionReset != OrbCore.Flow.SessionResetMode.Custom
            || this.clock is not { } sessionClock)
        {
            return OrbCore.Flow.NoSessionBoundary.Instance;
        }

        var named = loaded.Flow.Ingestion.ResetSession;

        // The clock knows every configured window; a name the loader already validated cannot be
        // absent here, and if it somehow were, no reset is the honest answer rather than a guess.
        return new OrbCore.Flow.DelegateSessionBoundary(
            utc => sessionClock.MostRecentOpenUtc(named, utc, this.instrument.Root) ?? DateTime.MinValue);
    }

    private static TimeZoneInfo? ResolveZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // The loader already validated this id against this machine's database, so reaching
            // here means the database changed under a running chart. No zone leaves the time
            // filter unusable rather than silently filtering on the wrong one.
            return null;
        }
    }

    /// <summary>The fourteen switches as one value, so nothing reads them individually.</summary>
    private OrbCore.Flow.FlowToggles FlowSwitches => new(
        this.FlowEnabled,
        this.FlowClusterStatistics,
        this.FlowClusterSearch,
        this.FlowStackedImbalance,
        this.FlowAbsorption,
        this.FlowUnfinishedAuction,
        this.FlowBigTrades,
        this.FlowLiveCounter,
        this.FlowDomLevels,
        this.FlowTrendLines,
        this.FlowFibFan,
        this.FlowGex,
        this.FlowVolumeAbsorptionTier1,
        this.FlowVolumeAbsorptionTier2);

    /// <summary>
    /// What the absorbed tools are doing on this chart: the switches, the document, and the
    /// chart's bar period resolved together.
    ///
    /// BUILT FRESH RATHER THAN CACHED. The switches change under the operator's hand and the
    /// bar period changes when the chart's aggregation does; a cached display would keep
    /// reporting the settings of a chart that no longer exists. It is a handful of
    /// property reads.
    ///
    /// Null before the configuration has loaded, which is the one state in which nothing here
    /// can be answered at all.
    /// </summary>
    /// <remarks>
    /// THE CALIBRATION COMES FROM THE BUILDER BECAUSE THAT IS WHERE IT IS MEASURED. It is not a
    /// setting and cannot be read from the document: it is taken from the bars this chart has
    /// actually accumulated, and it changes as they do. Reading it here, on a display rebuilt
    /// every fold, is what keeps the status line reporting the floor the marks were drawn at.
    /// Null before the builder exists, which is the same state in which nothing has been scanned.
    /// </remarks>
    private OrbCore.Flow.FlowDisplay? FlowState
        => this.config is { } loaded
            ? new OrbCore.Flow.FlowDisplay(
                this.FlowSwitches,
                loaded.Flow,
                this.MeasurableBarPeriod(),
                this.flowBuilder?.Calibration)
            : null;

    /// <summary>
    /// The chart's bar period, but only when its bars are TIME bars.
    ///
    /// NOT THE SAME QUESTION AS "what period is this chart", and the difference was measured rather
    /// than assumed. Renko, Kagi, Line Break and Points-and-Figures all inherit the platform's
    /// period-carrying aggregation type — read from the installed assembly with ilspycmd — so
    /// <see cref="DirectionLanes.ChartPeriod"/> answers on those charts with the SOURCE period the
    /// shapes are built from. That is the right answer for naming a Direction lane and the wrong one
    /// for a threshold measured on time bars, which is why this asks separately instead of reusing
    /// it. See <see cref="OrbCore.Flow.TimeBarAggregation"/> for the four type names and the rule.
    /// </summary>
    /// <summary>How many of the chart's most recent bar opens are held for bucketing.</summary>
    private const int ChartBarOpensHeld = 2_000;

    private readonly OrbCore.Flow.PlatformBarBoundaries platformBars = new();
    private string flowBoundariesKind = string.Empty;
    private int platformBarsSeen = -1;

    /// <summary>
    /// Where this chart's bars begin and end, or null when that cannot be answered yet.
    ///
    /// TIME BARS TAKE THE PATH THEY ALWAYS DID. The boundaries are the same truncation and
    /// addition this engine performed inline, so nothing about a time chart changes.
    ///
    /// EVERY OTHER CHART READS ITS OWN BARS. The opens come from HistoricalData, oldest first,
    /// on the fold thread -- the same thread that drains the prints they bucket. The platform
    /// also raises an event when a bar starts, and consuming that instead would be the obvious
    /// design and the wrong one: prints arrive on a feed thread and that event on the
    /// calculation thread, so a boundary processed out of order against the prints around it
    /// puts those prints in the wrong bar, silently and unreproducibly.
    /// </summary>
    private OrbCore.Flow.IBarBoundaries? FlowBoundaries(DateTime nowUtc, out string kind)
    {
        if (this.MeasurableBarPeriod() is { } period)
        {
            kind = string.Create(CultureInfo.InvariantCulture, $"time:{period.Ticks}");
            return new OrbCore.Flow.TimeBarBoundaries(period);
        }

        kind = "chart";

        if (this.HistoricalData is not { Count: > 0 } bars)
            return null;

        // RE-READ ONLY WHEN THE BAR COUNT MOVED. Folds are far more frequent than bars, and
        // reading a thousand opens several times a second to find none of them changed buys
        // nothing. When the count is unchanged the only thing that moved is how far the newest
        // bar has run.
        if (bars.Count == this.platformBarsSeen)
        {
            this.platformBars.Touch(nowUtc);
            return this.platformBars.Known ? this.platformBars : null;
        }

        var first = Math.Max(0, bars.Count - ChartBarOpensHeld);
        var opens = new List<DateTime>(bars.Count - first);

        try
        {
            for (var i = first; i < bars.Count; i++)
            {
                // Begin, STATED. The indexer's default origin is End, where index 0 is the
                // newest bar and the sequence runs backwards -- and a reversed sequence does
                // not fail, it buckets every print into the wrong bar. PlatformBarBoundaries
                // refuses one, which is the second half of the same guard.
                if (bars[i, SeekOriginHistory.Begin] is HistoryItemBar item)
                    opens.Add(item.TimeLeft);
            }
        }
        catch (InvalidOperationException)
        {
            // The platform's calculation thread grew the series mid-read. Keep what was already
            // held and take the fresh read on the next fold, exactly as the profile scan and the
            // flow seeder already handle this. Not a swallowed error: the previous boundaries
            // remain valid and the count is left unrecorded so the next fold retries.
            return this.platformBars.Known ? this.platformBars : null;
        }

        if (opens.Count == 0)
            return null;

        this.platformBars.Refresh(opens, nowUtc > opens[^1] ? nowUtc : opens[^1]);
        this.platformBarsSeen = bars.Count;
        return this.platformBars;
    }

    private TimeSpan? MeasurableBarPeriod()
        => OrbCore.Flow.TimeBarAggregation.MeasurablePeriod(
            this.HistoricalData?.Aggregation?.GetType().FullName,
            DirectionLanes.ChartPeriod(this.HistoricalData));

    [InputParameter("Delta: parity dump (research)", 144)]
    public bool DeltaParityDump { get; set; }

    /// <summary>
    /// Wave 0. Records what each connection publishes about the account, once a minute,
    /// to NDJSON beside the startup log. Draws nothing and decides nothing.
    ///
    /// OFF BY DEFAULT because it is research, and because the file it writes is about an
    /// account rather than a market. The account's identity is fingerprinted, never
    /// recorded — see <see cref="OrbCore.Diagnostics.ConnectionProbe.Fingerprint"/>.
    /// </summary>
    [InputParameter("Probe: connection & account dump (research)", 206)]
    public bool ConnectionProbeDump { get; set; }

    private readonly ZoneOverlay zoneOverlay = new();
    private readonly DeltaPanelOverlay deltaOverlay = new();
    private readonly ImbalanceOverlay imbalanceOverlay = new();
    private readonly AbsorptionOverlay absorptionOverlay = new();
    private readonly DeltaLevelsOverlay deltaLevelsOverlay = new();
    private readonly FlowLevelsOverlay flowLevelsOverlay = new();
    private readonly FlowMarkersOverlay flowMarkersOverlay = new();
    private readonly FlowStatisticsOverlay flowStatisticsOverlay = new();
    private readonly FlowCounterOverlay flowCounterOverlay = new();
    private readonly FlowDomOverlay flowDomOverlay = new();
    private readonly FlowBiasOverlay flowBiasOverlay = new();

    /// <summary>
    /// Session delta flip levels. Fed CLOSED delta bars only, so there is no forming-bar state to
    /// rewind: a confirmed flip is confirmed against data that cannot change.
    /// </summary>
    private readonly DeltaFlipEngine deltaFlips = new();

    private volatile DeltaLevelsDrawable deltaLevelsDrawable = DeltaLevelsDrawable.Empty;

    /// <summary>The last shelf scan, and the newest footprint it covered.</summary>
    private ShelfScan shelfScan = ShelfScan.Empty;
    private DateTime shelfScanNewest;
    private string shelfScanKey = string.Empty;
    private ZoneEngine? zoneEngine;
    private RejectionBlockEngine? rbEngine;
    private HtfBarBuilder? htfBuilder;
    private readonly List<DateTime> htfBarOpen = new();
    private readonly List<DateTime> zoneChartBarOpen = new();
    private TimeSpan zoneHtfPeriod;
    private volatile string zoneFault = string.Empty;
    private volatile ZoneDrawable zoneDrawable = ZoneDrawable.Empty;

    private DeltaSeriesEngine? deltaEngine;
    private TimeSpan deltaChartPeriod;
    private DateTime deltaSessionOpen;
    private string deltaHistoryStatus = "history: none — delta since attach.";
    private volatile DeltaDrawable deltaDrawable = DeltaDrawable.Empty;
    private volatile ImbalanceDrawable imbalanceDrawable = ImbalanceDrawable.Empty;
    private volatile AbsorptionDrawable absorptionDrawable = AbsorptionDrawable.Empty;

    /// <summary>Chart-bar open → that bar's live delta, for RB annotation.</summary>
    private readonly Dictionary<DateTime, double> deltaByBarOpen = new();
    private readonly Queue<DateTime> deltaByBarOpenOrder = new();
    private const int DeltaByBarOpenCap = 4096;

    private StreamWriter? parityWriter;
    private string parityStatus = string.Empty;

    // ---- Wave 0: the connection & account probe (research only) -----------
    //
    // Answers what cannot be answered by reading: does Account.Balance move intraday on
    // these connections, and does a prop firm publish its own loss limits in
    // AdditionalInfo? The chart's daily-loss line is currently computed from a STATIC
    // configured limit and says "assumes $0 realized today" — true at the open, and
    // further from the truth with every losing trade after it.
    private StreamWriter? probeWriter;
    private string probeStatus = string.Empty;

    /// <summary>
    /// Order ids already judged today, so one order cannot be counted twice.
    ///
    /// The vendor documents OrderAdded only as "will be triggered when new Order placed" and
    /// says nothing about whether an amended order raises it again. Rather than assume either
    /// way, the id is remembered: a repeat is ignored and a genuinely new order is not.
    /// Touched only on the fold, which is why it needs no lock.
    /// </summary>
    private readonly HashSet<string> judgedOrderIds = new(StringComparer.Ordinal);
    /// <summary>
    /// Collapses runs of identical engine decisions so the journal records transitions rather
    /// than heartbeats. Measured before it existed: 8,970 of 8,972 rows said "no plan".
    /// </summary>
    private readonly OrbIx.Core.Telemetry.EngineStateThrottle engineStateThrottle = new();

    /// <summary>
    /// The stop distance of the most recent proposed plan, for the sizing line.
    ///
    /// Null when the engine has no plan, which is most folds. Kept rather than recomputed so
    /// the sizing line describes the SAME plan the panel is showing.
    /// </summary>
    private int? lastPlanRiskTicks;

    /// <summary>
    /// Every distinct account id that offered a fill this seed. Printed on the status line
    /// so the operator can read the valid values instead of guessing them.
    /// </summary>
    private readonly SortedSet<string> accountsOffered = new(StringComparer.Ordinal);

    private DateTime probeLastSampleUtc = DateTime.MinValue;
    private readonly List<OrbCore.Diagnostics.AccountProbe> probeSamples = new();

    /// <summary>
    /// One sample a minute. Fast enough to catch a balance moving on a fill, slow enough
    /// that a session produces a file a person can read rather than a stream.
    /// </summary>
    private static readonly TimeSpan ProbeSampleInterval = TimeSpan.FromMinutes(1);

    // ---- Wave 1: VWAP · risk horizon · news shading · cost meter ---------
    //
    // Master plan approved 2026-08-28. DISPLAY ONLY: VWAP gates measured
    // net-negative OOS (trial 022); the risk lines locate the account's
    // own limits; nothing here signals.

    [InputParameter("VWAP: session line", 150)]
    public bool VwapEnabled { get; set; } = true;

    [InputParameter("VWAP: band multipliers (comma list)", 151)]
    public string VwapBandKs { get; set; } = "1,2";

    [InputParameter("VWAP: session colour", 152)]
    public Color VwapColor { get; set; } = Color.FromArgb(0xFF, 0xB7, 0x4D);

    [InputParameter("VWAP: anchored line", 153, variants: new object[]
    {
        "Off", VwapAnchorChoice.Off,
        "ORB close", VwapAnchorChoice.OrbClose,
        "Last HH", VwapAnchorChoice.LastHigherHigh,
        "Last LL", VwapAnchorChoice.LastLowerLow,
        // Aramid Flow's WEEKLY VWAP, returned as an anchor rather than a thirteenth switch.
        "Week open", VwapAnchorChoice.WeekOpen,
        "Custom time (UTC input below)", VwapAnchorChoice.CustomTime,
    })]
    public VwapAnchorChoice VwapAnchored { get; set; } = VwapAnchorChoice.Off;

    [InputParameter("VWAP: custom anchor, UTC (yyyy-MM-ddTHH:mm)", 154)]
    public string VwapCustomAnchor { get; set; } = string.Empty;

    [InputParameter("VWAP: anchored colour", 155)]
    public Color VwapAnchoredColor { get; set; } = Color.FromArgb(0xCE, 0x93, 0xD8);

    [InputParameter("Risk: horizon lines (DLL/MLL at current size)", 160)]
    public bool RiskLinesEnabled { get; set; } = true;

    [InputParameter("Risk: line colour", 161)]
    public Color RiskColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    [InputParameter("News: shade Tier-1 windows", 170)]
    public bool NewsShadingEnabled { get; set; } = true;

    [InputParameter("News: minutes before", 171, 0, 240, 5, 0)]
    public int NewsMinutesBefore { get; set; } = 30;

    [InputParameter("News: minutes after", 172, 0, 240, 5, 0)]
    public int NewsMinutesAfter { get; set; } = 15;

    [InputParameter("News: shade colour", 173)]
    public Color NewsColor { get; set; } = Color.FromArgb(0xFF, 0xA7, 0x26);

    [InputParameter("Cost meter: show", 180)]
    public bool CostMeterEnabled { get; set; } = true;

    // ── Wave 2 of the master plan: fixed-range + anchored volume profiles ──
    //
    // The value-area construction is transcribed from TradingView support
    // article 43000502040 (raw page fetched 2026-08-28); the POC tie rule is
    // a RATIFIED convention (operator, 2026-08-28) documented on
    // VolumeProfileEngine. DISPLAY ONLY:
    // profile-derived levels measured non-predictive here (levels-null,
    // 572 candidates over 31 sessions).

    [InputParameter("FRVP: fixed-range profile", 190)]
    public bool FrvpEnabled { get; set; } = true;

    [InputParameter("FRVP: click-select anchor (one left click, runs to now)", 191)]
    public bool FrvpClickSelect { get; set; }

    [InputParameter("FRVP: start, UTC (yyyy-MM-ddTHH:mm)", 192)]
    public string FrvpStartUtc { get; set; } = string.Empty;

    [InputParameter("FRVP: end, UTC — unavailable on this connector, leave empty", 193)]
    public string FrvpEndUtc { get; set; } = string.Empty;

    [InputParameter("AVP: anchored profile", 194)]
    public bool AvpEnabled { get; set; } = true;

    [InputParameter("AVP: anchor", 195, variants: new object[]
    {
        "Off", VwapAnchorChoice.Off,
        "ORB close", VwapAnchorChoice.OrbClose,
        "Last HH", VwapAnchorChoice.LastHigherHigh,
        "Last LL", VwapAnchorChoice.LastLowerLow,
        // Aramid Flow's look-back volume profile, returned as an anchor rather than a second
        // profile engine. Its look-back is flow.volumeProfile.lookBackBars in the document.
        "Look-back high", VwapAnchorChoice.LookBackHigh,
        "Custom time (UTC input below)", VwapAnchorChoice.CustomTime,
    })]
    public VwapAnchorChoice AvpAnchor { get; set; } = VwapAnchorChoice.OrbClose;

    [InputParameter("AVP: custom anchor, UTC (yyyy-MM-ddTHH:mm)", 196)]
    public string AvpCustomAnchor { get; set; } = string.Empty;

    /// <summary>
    /// Ask the platform to compute volume analysis from ticks instead of taking the
    /// vendor's precomputed figures.
    ///
    /// DEFAULT FALSE, and this is now a MEASURED default rather than a cautious one.
    ///
    /// It was flipped to true on 2026-08-28 as a deliberate experiment and flipped back
    /// the same day, because the platform refused the path outright. On a 1m MNQU6 chart
    /// with this input on, Quantower raised a warning toast reading verbatim:
    ///
    ///     "Volume analysis calculation from ticks history is not allowed for one data vendor"
    ///
    /// The refusal is a VENDOR capability limit, not a fault in this code and not a gate
    /// we mis-modelled. Both routes to per-price levels are therefore closed on this
    /// connector: the vendor's precomputed path declares levels for "1 - Minute" and
    /// delivers `0 covered` on every scan, and the tick path is declined by the platform.
    ///
    /// IT ALSO COST US SOMETHING, which is the reason the default must stay false rather
    /// than merely being unhelpful. Forcing the tick path lost the BAR TOTALS the delta
    /// panel seeds from: the history line went from "6072 bars seeded" to
    /// "none — volume analysis finished but no bar carried data (4140 without totals)",
    /// leaving delta as since-attach only. Turning this on trades a working feature for
    /// one the vendor will not serve.
    ///
    /// The input remains so the refusal can be reproduced on demand; nothing here is a
    /// reason to remove it.
    /// </summary>
    [InputParameter("Volume analysis: force tick data", 204)]
    public bool VolumeAnalysisForceTickData { get; set; }

    /// <summary>
    /// Which delta calculation the volume-analysis request asks for.
    ///
    /// The platform diverts the vendor path when this differs from what the symbol itself
    /// uses, and the parameterless call hardcoded <c>AggressorFlag</c> — so a symbol
    /// wanting tick direction has been mismatching on every request. "Match the symbol"
    /// asks the symbol and sends that; the two explicit choices are for isolating the gate
    /// deliberately. Default is AggressorFlag, which is what was being sent before.
    /// </summary>
    [InputParameter("Volume analysis: delta calculation", 205, variants: new object[]
    {
        "Aggressor flag (previous behaviour)", VolumeAnalysisDeltaChoice.AggressorFlag,
        "Tick direction", VolumeAnalysisDeltaChoice.TickDirection,
        "Match the symbol", VolumeAnalysisDeltaChoice.MatchSymbol,
    })]
    public VolumeAnalysisDeltaChoice VolumeAnalysisDelta { get; set; }
        = VolumeAnalysisDeltaChoice.AggressorFlag;

    [InputParameter("Profiles: row size (ticks)", 197, 1, 400, 1, 0)]
    public int ProfileRowTicks { get; set; } = 4;

    [InputParameter("Profiles: value area %", 198, 1, 100, 1, 0)]
    public int ProfileValueAreaPercent { get; set; } = 70;

    [InputParameter("Profiles: max width (% of range)", 199, 1, 100, 1, 0)]
    public int ProfileWidthPercent { get; set; } = 25;

    [InputParameter("Profiles: buy colour", 200)]
    public Color ProfileUpColor { get; set; } = Color.FromArgb(0x26, 0xA6, 0x9A);

    [InputParameter("Profiles: sell colour", 201)]
    public Color ProfileDownColor { get; set; } = Color.FromArgb(0xEF, 0x53, 0x50);

    [InputParameter("Profiles: POC colour", 202)]
    public Color ProfilePocColor { get; set; } = Color.FromArgb(0xFF, 0xD5, 0x4F);

    [InputParameter("Profiles: VAH/VAL colour", 203)]
    public Color ProfileVaColor { get; set; } = Color.FromArgb(0x90, 0xCA, 0xF9);

    private readonly Wave1Overlay wave1Overlay = new();
    private readonly VwapEngine vwapSession = new();
    private readonly VwapEngine vwapAnchored = new();
    private (VwapAnchorChoice Choice, DateTime AnchorUtc) vwapAnchorState;
    private volatile Wave1Drawable wave1Drawable = Wave1Drawable.Empty;
    private IEconomicCalendar? newsCalendar;
    private AccountRuntimeParams? accountParams;
    private FeesRuntime? feesRuntime;

    // Wave-2 state. The click fields cross threads (UI handler writes, fold
    // reads) and go under profileClickLock; everything else is fold-owned,
    // with the drawable the usual volatile snapshot for the paint.
    private readonly ProfileOverlay profileOverlay = new();
    private readonly StatusOverlay statusOverlay = new();
    private readonly object profileClickLock = new();
    private volatile ProfileDrawable profileDrawable = ProfileDrawable.Empty;
    private VolumeProfileEngine? profileLiveEngine;
    private int profileLiveRowTicks;
    private DateTime profileLiveSinceUtc;
    private DateTime profileClickStart;
    private int profileClickGeneration;
    private Qt.Chart.IChart? mouseChart;
    private ProfileDraw? frvpDraw;
    private ProfileDraw? avpDraw;
    private ProfileResolution frvpResolution = ProfileResolution.Disabled("FRVP");
    private ProfileResolution avpResolution = ProfileResolution.Disabled("AVP");
    private bool profileTieSeen;

    // The last evidence line actually written, so the log records every CHANGE of
    // outcome rather than only the first. Logging once per attach recorded "anchor
    // unresolved" and never showed that the anchor later resolved and the rebuild then
    // failed for a different reason — the transition was the part that mattered.
    private string profileEvidenceLogged = string.Empty;
    private string statusLogged = string.Empty;

    // What the platform declared about volume analysis at the last history load. Null
    // until the delta series has been seeded at least once.
    private VolumeAnalysisCapability? volumeAnalysisCapability;
    private Level2RuleReport? level2Rules;

    // What the problems line draws, or empty when nothing is wrong. Volatile because the
    // fold writes it and the paint thread reads it, like every other drawable here.
    private volatile string statusChartText = string.Empty;
    private DateTime profileOpenEndedRebuiltUtc;
    private (bool FrvpOn, bool AvpOn, int RowTicks, int VaPercent, int WidthPercent,
             DateTime FrvpStart, DateTime FrvpEnd, VwapAnchorChoice AvpChoice,
             DateTime AvpAnchorUtc, int BarCount, int ClickGen, int TickGen) profileSignature;

    private double lastBid = double.NaN;
    private double lastAsk = double.NaN;

    private readonly HhLlOverlay hhllOverlay = new();
    private readonly List<DateTime> hhllBarOpen = new();
    private readonly List<double> hhllBarHigh = new();
    private readonly List<double> hhllBarLow = new();
    private HhLlEngine? hhllEngine;
    private (int Left, int Right) hhllParams;
    private volatile HhLlDrawable hhllDrawable = HhLlDrawable.Empty;

    private readonly FibOverlay fibOverlay = new();

    private volatile FibDrawable fibDrawable = FibDrawable.Empty;
    /// <summary>Closed bars whose trend colour has been applied via SetBarColor.</summary>
    private int hhllColored;

    private bool hhllColorsApplied;

    /// <summary>One evidence line per engine build — see the FeedHhLl report.</summary>
    private bool hhllSeedReported;

    public OrbIxIndicator()
    {
        this.Name = "ORB-IX";
        this.Description =
            "Institutional opening-range engine. Session-aware range construction, order-flow "
            + "confirmation, and a four-target execution ladder. Draws and journals only — "
            + "this indicator places no orders.";
        this.SeparateWindow = false;
    }

    // ---- lifecycle ---------------------------------------------------------------------

    protected override void OnInit()
    {
        // Wrapped because an initialisation fault would otherwise be invisible. The platform
        // catches whatever escapes here and the indicator simply renders nothing, which is
        // indistinguishable from "it drew but there was nothing to draw" — a distinction that
        // cost a full round-trip to establish once already.
        //
        // A type-load failure happens before any of this runs and cannot be caught from
        // inside; everything after it now leaves a trace on disk as well as on the panel.
        try
        {
            this.initState = this.Initialise();

            // The fold timer is only started by a successful Initialise, so a Retry outcome has
            // nothing driving it. A slow timer does that, and stops itself the moment the
            // engine is up — one attempt a second, not one per fold, because what is being
            // waited on is a market-data connection rather than anything this end controls.
            if (this.initState == InitOutcome.Retry)
                this.retryTimer = new Timer(this.OnRetryTimer, null, RetryIntervalMs, RetryIntervalMs);
        }
        catch (Exception ex)
        {
            var message = $"ORB-IX failed to start: {ex.GetType().Name}: {ex.Message}";

            this.Report(message);
            this.Report(message + Environment.NewLine + ex.StackTrace);
        }
    }

    /// <summary>
    /// Re-attempts initialisation while the only thing missing is data.
    ///
    /// Guarded by the same gate the fold uses, so an attempt cannot run beside a fold and see
    /// half-built module state.
    /// </summary>
    private void OnRetryTimer(object? _)
    {
        lock (this.foldGate)
        {
            if (this.initState != InitOutcome.Retry)
                return;

            try
            {
                this.initState = this.Initialise();
            }
            catch (Exception ex)
            {
                this.initState = InitOutcome.Fatal;

                var message = $"ORB-IX failed to start: {ex.GetType().Name}: {ex.Message}";

                this.Report(message);
                this.Report(message + Environment.NewLine + ex.StackTrace);
            }

            if (this.initState == InitOutcome.Retry)
                return;

            // Ready or Fatal: either way there is nothing left to wait for.
            this.retryTimer?.Dispose();
            this.retryTimer = null;
        }
    }

    /// <summary>
    /// How often to re-attempt while waiting for contract specifications.
    ///
    /// A second, not a fold interval. What is being waited on is the platform publishing a
    /// price, which happens on a market-data connection rather than on anything this end can
    /// hurry.
    /// </summary>
    private const int RetryIntervalMs = 1000;

    /// <summary>
    /// Records a startup fault beside the user configuration.
    ///
    /// Appended rather than overwritten, and never allowed to throw: this runs on a path that
    /// is already handling a failure, and a logger that fails there would replace a readable
    /// fault with an unreadable one.
    /// </summary>
    private static void WriteStartupLog(string message)
    {
        try
        {
            var path = Path.Combine(
                Path.GetDirectoryName(UserConfigPath()) ?? Path.GetTempPath(),
                "orbix-startup.log");

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.GetTempPath());

            File.AppendAllText(
                path,
                $"{DateTime.UtcNow:u}  {PathDisplay.Redact(message)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            // The panel still carries the message; losing the file copy is not worth
            // compounding the failure that is already being reported.
        }
    }

    /// <summary>
    /// Why an initialisation attempt ended, and whether attempting again could help.
    ///
    /// THE DISTINCTION IS THE FIX. Initialisation used to run exactly once, from OnInit, and
    /// return on any failure — including "the platform has not published a price yet", which is
    /// not a failure at all but a race. Observed 2026-08-21T23:48:54Z: tick size NaN because
    /// last, ask and bid were all NaN that early. Fold then returns forever on an unusable
    /// instrument, so the indicator was dead until it was re-added by hand.
    ///
    /// Retrying everything would be as wrong in the other direction: a misconfigured product
    /// would re-report its one actionable message on every fold and bury it.
    /// </summary>
    private enum InitOutcome
    {
        /// <summary>The engine is built and running.</summary>
        Ready,

        /// <summary>Nothing is wrong except the timing. Try again on the next fold.</summary>
        Retry,

        /// <summary>Something must be changed by a human. Never retried.</summary>
        Fatal,
    }

    /// <summary>Set once the engine is built, so the fold stops attempting.</summary>
    private InitOutcome initState = InitOutcome.Retry;

    /// <summary>
    /// How many times <see cref="Initialise"/> has run since this load, counting from one.
    ///
    /// A COUNT RATHER THAN A "HAVE I SAID THIS YET" FLAG. The flag it replaces was cleared by
    /// OnClear, and across a whole live session the log held 16 "Configuration loaded" lines,
    /// 6 "has not published contract specifications" lines and NOT ONE report that the wait had
    /// ended — so not once in six waits did the operator learn the outcome. Exactly why is
    /// still UNVERIFIED; rather than assert a cause that was not isolated, the design stops
    /// depending on a flag that teardown can clear, and the OnClear diagnostic below records
    /// the load/teardown sequence so the next session settles it.
    ///
    /// A count also carries information a flag cannot: how long the wait actually was.
    /// </summary>
    private int initAttempts;

    /// <summary>When the first attempt of this load ran, so a resolved wait can be measured.</summary>
    private DateTime firstAttemptUtc;

    private InitOutcome Initialise()
    {
        this.initAttempts++;

        if (this.initAttempts == 1)
            this.firstAttemptUtc = DateTime.UtcNow;

        var symbol = this.Symbol;

        if (symbol is null)
        {
            this.Report("No symbol is attached, so there is nothing to analyse.");
            return InitOutcome.Fatal;
        }

        if (!this.TryLoadConfig(out var loaded))
            return InitOutcome.Fatal;

        this.config = loaded;

        // The platform reports the CONTRACT root — MNQ for MNQU6 — while configuration is
        // keyed by product FAMILY, NQ. Resolving through the family's own declared tier list
        // is what lets a micro chart find its configuration at all.
        var contractRoot = ResolveContractRoot(symbol, this.SymbolRootOverride);

        if (!loaded.TryResolveProduct(contractRoot, out var familyRoot, out var symbolConfig))
        {
            this.Report(
                $"Contract root '{contractRoot}' (from {symbol.Name}) belongs to no configured "
                + $"product. Configured products and the contracts each covers: "
                + loaded.DescribeProducts()
                + $". Add '{contractRoot}' to a product's \"tiers\" list, or set the product "
                + "root override input.");
            return InitOutcome.Fatal;
        }

        var reading = this.ReadInstrument(symbol, familyRoot, contractRoot);

        this.instrument = reading.Spec;

        // ONE THROTTLE FOR THE WHOLE RETRY PATH, applied where the attempt number is known.
        // Every message below is on a path that runs once a second until it resolves, so each
        // of them is a flood waiting to happen and none of them is exempt.
        var report = AttemptReporting.ShouldReport(this.initAttempts);

        if (report && reading.Diagnostic is not null)
            this.Report(reading.Diagnostic);

        // The PRICE GRID is the precondition to start, not the tick cost. Cost needs a
        // reference price and therefore a live quote or loaded history; the grid does not.
        // Waiting for both withheld fourteen price-domain features for 178 seconds.
        if (!this.instrument.HasPriceScale)
        {
            // NOT a failure — the platform has not published specifications yet. Nothing is
            // assumed and nothing is invented; the engine simply waits, and the retry timer
            // tries again.
            if (report)
            {
                this.Report(
                    $"{symbol.Name} has not published a tick size yet (tick size "
                    + $"{this.instrument.TickSize}), attempt {this.initAttempts}. Waiting for "
                    + "the price grid; nothing downstream may assume it. Tick COST is not "
                    + "required to start and is filled in when the first price arrives.");
            }

            return InitOutcome.Retry;
        }

        // Reported ONLY when there was a wait to resolve, and carrying its length — so the line
        // means something when it appears, instead of being a start-up banner the eye skips.
        var resolution = AttemptReporting.DescribeResolution(
            this.initAttempts, DateTime.UtcNow - this.firstAttemptUtc);

        if (resolution is not null)
        {
            this.Report(
                $"{symbol.Name} published contract specifications {resolution}: tick size "
                + $"{this.instrument.TickSize}, tick cost {this.instrument.TickValue}. Starting.");
        }

        this.subscribedForHistory = symbol;
        this.BuildEngine(loaded, symbolConfig);

        // Delta history is seeded BEFORE the tick subscription exists:
        // SeedHistorical refuses to splice under live accumulation, and the
        // ordering here is what makes that refusal unreachable.
        this.InitialiseDelta();

        // Level 2 arrives on a feed thread. The handler translates and writes; that is all.
        // Built before the subscription so the first print already has somewhere to
        // go. The lane set comes from the SHARED builder, so this panel and the
        // standalone Direction indicator cannot disagree about which timeframes
        // they read.
        var directionLanes = DirectionLanes.Build(
            this.HistoricalData,
            this.DirectionIncludeChartTimeframe,
            this.DirectionHigherTimeframes);

        // ONE LINE NAMING THE LANE SET, AND WHY IT EXISTS.
        //
        // A live DirectionRead row showed a lane set of exactly {15m, 1h} where
        // the defaults looked like they should give four. It took a reproduction
        // in Core to establish that this is CORRECT on a 15-minute chart -- "5"
        // is below the chart and "15" duplicates the chart lane, so both drop by
        // design -- but the chart's own period could only be INFERRED from the
        // journal, never read.
        //
        // The lane set is now self-reporting. A reader can see the chart period,
        // the requested higher timeframes, and what survived, without deducing any
        // of it from row spacing.
        this.Report(string.Create(
            CultureInfo.InvariantCulture,
            $"Direction: chart period {DescribePeriod(DirectionLanes.ChartPeriod(this.HistoricalData))}, "
            + $"includeChart={this.DirectionIncludeChartTimeframe}, "
            + $"requested=[{this.DirectionHigherTimeframes}] -> "
            + $"{directionLanes.Count} lane(s): {string.Join(", ", directionLanes.Select(l => l.Name))}"));

        this.direction = new DirectionEngine(
            directionLanes,
            Math.Max(1, this.HhLlLeftBars),
            Math.Max(1, this.HhLlRightBars),
            new DirectionSettings(
                this.DirectionVwapFlatTicks, this.DirectionDeltaFlatContracts));

        symbol.NewLevel2 += this.OnLevel2;
        symbol.NewLast += this.OnLast;
        this.subscribed = symbol;

        // Started last, once every field it touches is built.
        var period = Math.Max(this.FoldIntervalMs, 20);
        this.foldTimer = new Timer(this.OnFoldTimer, null, period, period);

        return InitOutcome.Ready;
    }

    protected override void OnClear()
    {
        // Both stopped before anything they read is torn down. The retry timer especially:
        // it calls Initialise, which builds the very fields being released here.
        this.foldTimer?.Dispose();
        this.foldTimer = null;

        this.retryTimer?.Dispose();
        this.retryTimer = null;

        // Released with the rest of the GDI handles. The engine goes too: a stale
        // panel outliving the symbol it described would keep rendering a verdict
        // for an instrument the chart is no longer showing.
        this.directionOverlay.Dispose();
        this.direction = null;
        this.directionPanel = null;

        // THE DIAGNOSTIC THAT SETTLES THE OPEN QUESTION. Why a resolved wait never got
        // reported is not established, and the mechanism suspected — this method running
        // between the wait and the success — has NOT been observed. One line here makes the
        // load/teardown sequence readable after the fact. It is evidence rather than noise
        // because teardown is rare: 16 loads across an entire session.
        this.Report(
            $"Cleared after {this.initAttempts} initialisation attempt(s), state "
            + $"{this.initState}.");

        // So a chart that is cleared and re-added waits and reports afresh rather than
        // inheriting the previous attempt's state. A fresh load is a fresh wait.
        this.initState = InitOutcome.Retry;
        this.initAttempts = 0;
        this.firstAttemptUtc = default;
        this.reportedRefusedBookEvent = false;

        // The engine goes too. A stale plan surviving a teardown would be painted against the
        // next load's session, and a plan drawn for a session that is no longer on the chart is
        // worse than nothing on the chart.
        this.retest = null;
        this.evaluator = null;
        this.bars = null;
        this.breaker = null;
        this.engine = null;
        this.drawableSetup = null;
        this.drawableSignalLabel = string.Empty;
        this.drawableBlockReason = string.Empty;
        this.drawableScoreCoverage = string.Empty;

        if (this.subscribed is not null)
        {
            this.subscribed.NewLevel2 -= this.OnLevel2;
            this.subscribed.NewLast -= this.OnLast;
            this.subscribed = null;
        }

        this.recorder?.Dispose();
        this.recorder = null;

        this.journal?.Dispose();
        this.journal = null;

        // The HH/LL port starts over on the next load, like everything else
        // here: a stale structure read against a different chart's bars would
        // be wrong in a way that looks right. The platform tears down its own
        // per-bar colour state with the chart.
        this.hhllEngine = null;
        this.hhllBarOpen.Clear();
        this.hhllBarHigh.Clear();
        this.hhllBarLow.Clear();
        this.hhllDrawable = HhLlDrawable.Empty;
        this.fibDrawable = FibDrawable.Empty;
        this.hhllColored = 0;
        this.hhllColorsApplied = false;
        this.hhllSeedReported = false;

        // The zone and delta features start over on the next load, same
        // reasoning as the HH/LL block above.
        this.ResetZoneState();
        this.zoneFault = string.Empty;
        this.deltaEngine = null;
        this.deltaFlips.Reset();
        this.shelfScan = ShelfScan.Empty;
        this.shelfScanKey = string.Empty;
        this.shelfScanNewest = default;
        this.deltaLevelsDrawable = DeltaLevelsDrawable.Empty;
        this.deltaByBarOpen.Clear();
        this.deltaByBarOpenOrder.Clear();
        this.deltaSessionOpen = default;
        this.deltaDrawable = DeltaDrawable.Empty;
        this.deltaHistoryStatus = "history: none — delta since attach.";
        this.parityWriter?.Dispose();
        this.parityWriter = null;
        this.probeWriter?.Dispose();
        this.probeWriter = null;
        this.parityStatus = string.Empty;

        // Wave-2 state starts over on the next load, and the mouse hook is
        // released with the chart it belonged to.
        if (this.mouseChart is { } hookedChart)
        {
            hookedChart.MouseClick -= this.OnChartMouseClick;
            this.mouseChart = null;
        }

        lock (this.profileClickLock)
        {
            this.profileClickStart = default;
            this.profileClickGeneration = 0;
        }

        this.profileLiveEngine = null;
        this.profileTickCts?.Cancel();
        this.profileTickCts?.Dispose();
        this.profileTickCts = null;
        this.profileTickCache = null;
        this.profileLiveRowTicks = 0;
        this.profileLiveSinceUtc = default;
        this.frvpDraw = null;
        this.avpDraw = null;
        this.frvpResolution = ProfileResolution.Disabled("FRVP");
        this.avpResolution = ProfileResolution.Disabled("AVP");
        this.profileTieSeen = false;
        this.profileEvidenceLogged = string.Empty;
        this.statusLogged = string.Empty;
        this.statusChartText = string.Empty;
        this.volumeAnalysisCapability = null;
        this.profileOpenEndedRebuiltUtc = default;
        this.profileSignature = default;
        this.profileDrawable = ProfileDrawable.Empty;

        // Wave-1 state starts over on the next load, same reasoning as
        // every block above.
        this.vwapSession.Anchor(default);
        this.vwapAnchored.Anchor(default);
        this.vwapAnchorState = default;
        this.wave1Drawable = Wave1Drawable.Empty;
        this.newsCalendar = null;
        this.accountParams = null;
        this.feesRuntime = null;
        this.lastBid = double.NaN;
        this.lastAsk = double.NaN;

        this.tickRing.Clear();
        this.bookRing.Clear();
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        // Historical bars carry no live flow, so there is nothing to fold from them. They are
        // still the material the overlay is seeded from, but seeding happens on the periodic
        // fold: doing it here would rescan the whole history once per arriving bar.
        if (args.Reason == UpdateReason.HistoricalBar)
            return;

        this.Fold(DateTime.UtcNow);
    }

    // ---- market data: translate and record, nothing else --------------------------------

    private void OnLevel2(Qt.Symbol symbol, Level2Quote level2, DOMQuote dom)
    {
        if (level2 is null)
            return;

        // THE LADDER'S VIEW, built from the same message. Id and Priority are public on
        // Level2Quote in the installed assembly (read with ilspycmd, 2026-09-12) and ORB-IX has
        // discarded both until now, because nothing it ran had any use for them.
        this.depthRing.Write(new OrbCore.Flow.DepthUpdate(
            level2.Time,
            level2.PriceType == QuotePriceType.Bid ? BookSide.Bid : BookSide.Ask,
            level2.Price,
            level2.Size,
            level2.Closed,
            level2.Id ?? string.Empty,
            level2.NumberOrders,
            level2.Priority));

        // FromFeed adjudicates the platform's timestamp against the instant we received it.
        // Measured 2026-08-21: about 45% of live Level 2 updates arrive stamped 1970-01-01,
        // and that value used to become MicroQuality's last-quote instant — where, not being
        // DateTime.MinValue, it read as a quote fifty-six years old and vetoed every entry.
        // The boundary adjudicates and may refuse outright. Every Level 2 subscription
        // delivers two sentinel messages whose price and size are NaN, and one of those used
        // to turn the opening range's book imbalance into NaN permanently.
        if (BookDelta.TryFromFeed(
                level2.Time,
                DateTime.UtcNow,
                level2.PriceType == QuotePriceType.Bid ? BookSide.Bid : BookSide.Ask,
                level2.Price,
                level2.Size,
                // The platform supplies no level index on a per-quote update, and the touch is
                // what the engine needs. Reporting a fabricated index would let a deep level be
                // read as the touch, so it is reported as unknown and the modules that need the
                // touch reconcile against the aggregated book instead.
                levelIndex: -1,
                orderCount: level2.NumberOrders,
                // The platform's removal marker. Without it the boundary cannot tell a
                // withdrawal from a malformed message, and every one of these was being
                // dropped in silence while MicroQuality went on reporting the touch they
                // withdrew.
                closed: level2.Closed,
                out var delta))
        {
            this.bookRing.Write(delta);
            return;
        }

        // REFUSED OUTRIGHT — no usable price AND no removal marker, so there is nothing here
        // the engine can either fold or invalidate. A withdrawal no longer reaches this point:
        // it is admitted as a reset and recorded, which is the whole of the third fix.
        //
        // Described ONCE per load. What the platform means by a message that is neither is
        // still unknown, so its full shape is written where it can be read after the fact. One
        // line, not a stream: the log is the only diagnostic channel and filling it would
        // destroy what it is for.
        if (this.reportedRefusedBookEvent)
            return;

        this.reportedRefusedBookEvent = true;

        this.Report(
            $"A Level 2 update was refused: price {level2.Price}, size {level2.Size}, "
            + $"closed {level2.Closed}, orders {level2.NumberOrders}, "
            + $"type {level2.PriceType}, time {level2.Time:O}. It carries no usable price, so it "
            + "cannot identify a level and is not folded. Further refusals are not reported.");
    }

    /// <summary>
    /// Whether the first refused Level 2 update has been described this load.
    ///
    /// Every subscription delivers two of them — measured 2026-08-22, ten pairs in one day,
    /// one pair per indicator load — so reporting each would be a stream rather than a signal.
    /// </summary>
    private bool reportedRefusedBookEvent;

    /// <summary>
    /// Checks the feed's aggressor flag against the geometry of its own prints. Never reset by
    /// a fold: the verdict is about the CONNECTION, and it gets stronger the longer it runs.
    /// </summary>
    private readonly OrbCore.Features.AggressorConvention aggressorConvention = new();

    private void OnLast(Qt.Symbol symbol, Last last)
    {
        if (last is null)
            return;

        // One assignment, no computation: §10 forbids work on the market-data path. The
        // stale-quote guard reads this on the fold.
        this.lastQuoteUtc = DateTime.UtcNow;

        this.tickRing.Write(new TickEvent(
            last.Time,
            last.Price,
            last.Size,
            last.AggressorFlag switch
            {
                AggressorFlag.Buy => Aggressor.Buy,
                AggressorFlag.Sell => Aggressor.Sell,
                _ => Aggressor.Unknown,
            },
            symbol.Bid,
            symbol.Ask));
    }

    // ---- the fold ------------------------------------------------------------------------

    /// <summary>
    /// The periodic fold.
    ///
    /// Wrapped because this runs on a thread-pool thread: an exception escaping a timer
    /// callback is unhandled and terminates the process, which here means taking the whole
    /// trading platform down over an indicator fault. The reason is surfaced on the panel.
    /// </summary>
    private void OnFoldTimer(object? state)
    {
        try
        {
            this.Fold(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The periodic fold failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drains the rings into module state and rebuilds the panel snapshot.
    ///
    /// Everything that computes happens here, on the update thread, never on the feed.
    /// </summary>
    private void Fold(DateTime nowUtc)
    {
        lock (this.foldGate)
        {
            // HasPriceScale, NOT IsUsable. The fold drives every price-domain feature on the
            // chart; gating it on tick COST withheld all of them for 178 seconds on 2026-08-31
            // while the platform waited on a stalled history feed. Money-domain lines do their
            // own IsUsable check where they are computed, and say when they are waiting.
            if (this.config is null || this.clock is null || this.instrument.HasPriceScale is false)
                return;

            // The interval is enforced inside the gate, so the update thread and the timer
            // share one throttle. Held outside, the two would each keep their own view of when
            // the last fold happened and could fold twice in a row.
            if (nowUtc - this.lastFoldUtc < TimeSpan.FromMilliseconds(this.FoldIntervalMs))
                return;

            this.lastFoldUtc = nowUtc;

            // Start-up no longer waits for the tick cost, so something has to finish the job
            // once a price finally exists. The retry timer cannot: it disposes itself the moment
            // Initialise returns Ready, which now happens with the cost still missing.
            this.CompleteTickCostIfPending();

            this.EnsureSession(nowUtc);

            // Session roll re-anchors the cumulative delta (pinned rule 5).
            var sessionOpen = this.window?.OpenUtc ?? default;
            if (sessionOpen != this.deltaSessionOpen)
            {
                this.deltaEngine?.OnSessionOpen();
                this.deltaSessionOpen = sessionOpen;
                if (sessionOpen != default)
                {
                    // The session VWAP re-anchors and replays this
                    // session's already-held bars (VA-seeded history on a
                    // mid-session attach), so the line starts at the
                    // session open rather than at whenever the chart
                    // happened to load. Bar close×volume is the seeding
                    // approximation, stated in the status line.
                    this.vwapSession.Anchor(sessionOpen);

                    // The per-session half of the direction read rolls with the
                    // VWAP: cumulative delta and the day's range are session
                    // quantities. The STRUCTURE lanes are deliberately untouched --
                    // a 60-minute trend that forgot itself at every session open
                    // would never hold enough closed bars to form a pivot and would
                    // read Undecided forever.
                    this.direction?.OnSessionOpen();

                    // Flip levels are a per-session quantity for the same reason the VWAP and the
                    // direction read are: cumulative delta restarts, so a side carried across the
                    // boundary would be this session wearing yesterday's verdict.
                    this.deltaFlips.OnSessionOpen(sessionOpen);
                    if (this.deltaEngine is { } engineForVwap)
                    {
                        foreach (var bar in engineForVwap.Bars)
                        {
                            if (bar.OpenTimeUtc < sessionOpen)
                                continue;
                            this.vwapSession.Add(bar.Close, bar.Volume);
                            this.vwapSession.SampleBar(bar.OpenTimeUtc);
                        }
                    }
                }
            }

            var recorderNow = nowUtc;

            // ORDERED EXACTLY AS SessionReplay.Fold ORDERS IT, and that is not cosmetic.
            // SetupEvaluator is shared between the chart and the study so the two answer the
            // same question; feeding it in a different order would let them disagree about a
            // session while both looked correct, and every figure in docs/REPLAY-RESULTS.md
            // would become a statement about a system nobody runs.
            // RESOLVED ONCE PER FOLD, not per print. FlowDisplay is built fresh each read so the
            // switches and the bar period cannot go stale — which is right per fold and wasteful
            // per tick, and the tape delivers a great many more ticks than folds.
            var flowDisplay = this.FlowState;

            this.tickRing.Drain((in TickEvent tick) =>
            {
                this.lastPrice = tick.Price;

                // WHAT THE FEED CLAIMS, AGAINST WHERE THE PRINT LANDED. Every footprint,
                // imbalance, delta and absorption reading in this indicator is signed by this
                // one flag, and an inverted feed produces a chart that is coherent and
                // backwards — the hardest kind of wrong to notice. The quote at the instant of
                // the print is already on the event, so this costs one comparison and needs no
                // second source. See AggressorConvention.
                this.aggressorConvention.Add(tick);

                // Fed the same print as everything else, in the same order, so the
                // panel and the playbooks can never describe different tapes.
                this.direction?.OnTick(tick);

                // A bar closes on the tick that closes it, not at the end of whatever batch the
                // drain happened to collect. Everything the evaluation reads is then the state
                // that prevailed at the close.
                if (this.engine is not null
                    && this.engine.OnTick(tick, this.rangeBuilder, this.breaker, out var closedBar))
                {
                    this.OnBarClosed(closedBar);
                }

                // PER TICK, as the replay does it. The range closes on a tick, and a bar
                // closing in the same drain as the range would otherwise be evaluated against a
                // range this fold had not yet closed.
                this.AdvanceRange(tick.TimestampUtc);

                foreach (var builder in this.contextBuilders.Values)
                    builder.OnTick(tick);
                this.recorder?.OnTick(tick, recorderNow);

                this.lastBid = tick.Bid;
                this.lastAsk = tick.Ask;
                this.vwapSession.Add(tick.Price, tick.Size);
                this.vwapAnchored.Add(tick.Price, tick.Size);
                this.FeedProfileLiveTick(in tick);

                // The absorbed displays accumulate on the CHART grid, which is not the entry
                // timeframe the footprint engine above uses. Same print, same order, two grids.
                if (this.flowBuilder is { } flowTicks && flowDisplay is not null)
                    flowTicks.OnTick(in tick, flowDisplay);

                if (this.deltaEngine is not null
                    && this.deltaEngine.Add(tick, out var closedDelta))
                {
                    this.RememberBarDelta(closedDelta.OpenTimeUtc, closedDelta.Delta);

                    // Read from the inputs on every bar rather than cached at attach: these are
                    // live settings, and a threshold that only took effect after a reload would
                    // look like the feature ignoring it.
                    this.deltaFlips.ConfirmContracts = this.FlipConfirmContracts;
                    this.deltaFlips.MarkFirstSide = this.FlipMarkFirstSide;
                    this.deltaFlips.OnClosedBar(closedDelta, out _);
                    this.WriteParityLine(closedDelta, "live-ticks");
                    this.vwapSession.SampleBar(closedDelta.OpenTimeUtc);
                    this.vwapAnchored.SampleBar(closedDelta.OpenTimeUtc);
                }
            });

            // THE CLOCK CLOSES A BAR THE TAPE LEFT OPEN, and it runs AFTER the tick drain so a
            // print that would have closed the bar properly always wins. Bars here close when a
            // LATER print arrives, so on a quiet tape a bar that ended at 13:31 stayed open until
            // something traded — and everything reading a closed bar waited with it.
            //
            // THIS MOVES ORB-IX'S OWN BEHAVIOUR, not only the absorbed displays', and that was
            // the operator's explicit choice (2026-09-12) over running a second bar-closing rule
            // for the Flow tools alone. Two rules over one tape would agree except when the tape
            // went quiet — which is exactly when they would disagree, and silently.
            //
            // The grace is configuration because it is a property of the FEED: a print's exchange
            // stamp and its arrival are not the same instant.
            if (this.engine is { } clockEngine
                && this.config is { } clockConfig
                && clockEngine.OnClock(
                    nowUtc,
                    TimeSpan.FromMilliseconds(clockConfig.Flow.Ingestion.CloseGraceMs),
                    out var clockClosedBar))
            {
                this.OnBarClosed(clockClosedBar);
            }

            this.FoldFlow(nowUtc, flowDisplay);

            this.bookRing.Drain((in BookDelta delta) =>
            {
                this.quality?.OnBook(delta);
                this.rangeBuilder?.OnBook(delta);
                this.engine?.OnBook(delta);

                foreach (var builder in this.contextBuilders.Values)
                    builder.OnBook(delta);
                this.recorder?.OnBook(delta, recorderNow);
            });

            this.AdvancePhase(nowUtc);
            this.AdvanceContextRanges(nowUtc);
            this.SeedClosedSessionsFromBars(nowUtc);
            this.FeedHhLl();
            this.FeedZones();
            this.PublishDelta();
            this.PublishImbalance();
            this.PublishAbsorption(nowUtc);
            this.PublishDeltaLevels();
            this.EnsureAnchoredVwap();
            this.PublishWave1(nowUtc);
            this.PublishProfiles(nowUtc);
            this.PublishDirection();

            this.SampleConnectionProbe(nowUtc);

            // Last, so it sees the final state of every feature above it.
            this.PublishStatus();

            // Published for the paint thread as an immutable array. Painting must never walk
            // the live store, which this thread is still adding to.
            this.drawable = this.ranges?.Snapshot() ?? Array.Empty<OrSnapshot>();
            this.drawableLevels = this.SelectLevels(nowUtc);
            this.drawStatus = PathDisplay.Redact(this.DescribeDrawState());
        }
    }

    /// <summary>
    /// Feeds the HH/LL structure engine every closed chart bar exactly once
    /// (Pine's barstate.isconfirmed as a cursor — offset 0 is the forming
    /// bar, so there are Count-1 closed bars) and applies the script's
    /// barcolor. Runs inside the fold gate.
    ///
    /// SetBarColor's per-bar list is grown by the platform's own new-bar
    /// hook alongside every LineSeries (read from the installed assembly),
    /// so historical offsets are addressable; the loop is still bounded by
    /// both bar counts because the exact equality of the two lists is a
    /// mechanism read from decompiled code, not a stated contract.
    /// </summary>
    /// <summary>
    /// Rebuilds the direction panel from the current reading.
    /// </summary>
    /// <remarks>
    /// ON THE FOLD, NOT THE FEED THREAD, like every other Publish here: the panel
    /// is assembled once per fold rather than once per print.
    ///
    /// The VWAP is the SESSION one, not the anchored one. The anchored VWAP follows
    /// an operator-chosen anchor and answers "how is price doing against that
    /// event"; the direction panel asks "where is price against today", and those
    /// are different questions that would silently disagree.
    /// </remarks>
    /// <summary>
    /// A chart period for the evidence line, naming the no-period case explicitly.
    /// </summary>
    /// <remarks>
    /// A TICK, RENKO or RANGE-BAR chart has none. Printing an empty string there
    /// would read as a failure to look rather than as a real answer.
    /// </remarks>
    private static string DescribePeriod(TimeSpan? period) =>
        period is { } p ? DirectionLanes.Label(p) : "none (not a time chart)";

    private void PublishDirection()
    {
        DirectionEngine? current = this.direction;

        if (current is null || !this.DirectionPanelOn)
        {
            this.directionPanel = null;
            return;
        }

        double tick = this.Symbol?.TickSize ?? double.NaN;
        double vwap = this.vwapSession.TryCurrent(out double v, out _) ? v : double.NaN;

        this.directionPanel = current.Panel(
            this.lastPrice, vwap, tick, this.averageDailyRange);
    }

    private void FeedHhLl()
    {
        var bars = this.HistoricalData;

        if (bars is null)
            return;

        if (!this.HhLlEnabled)
        {
            // Off means OFF: colours cleared once, state dropped, nothing drawn.
            if (this.hhllColorsApplied)
                this.ClearHhLlColors(bars.Count);

            if (this.hhllEngine is not null)
            {
                this.hhllEngine = null;
                this.hhllBarOpen.Clear();
                this.hhllBarHigh.Clear();
                this.hhllBarLow.Clear();
                this.hhllDrawable = HhLlDrawable.Empty;
        this.fibDrawable = FibDrawable.Empty;
            }

            return;
        }

        var wanted = (this.HhLlLeftBars, this.HhLlRightBars);
        var closed = bars.Count - 1;

        if (closed < 0)
            return;

        // Fresh start, re-parameterization, or a shrunken series: a partial
        // recompute cannot be honest, so the engine restarts from bar zero.
        if (this.hhllEngine is null || this.hhllParams != wanted
            || this.hhllBarOpen.Count > closed)
        {
            if (this.hhllColorsApplied)
                this.ClearHhLlColors(bars.Count);

            this.hhllEngine = new HhLlEngine(wanted.Item1, wanted.Item2);
            this.hhllParams = wanted;
            this.hhllBarOpen.Clear();
            this.hhllBarHigh.Clear();
            this.hhllBarLow.Clear();
            this.hhllColored = 0;
            this.hhllSeedReported = false;
        }

        var advanced = false;

        while (this.hhllBarOpen.Count < closed)
        {
            if (!this.TryReadBar(bars, this.hhllBarOpen.Count, out var bar))
                break;

            this.hhllEngine.Feed(bar.High, bar.Low, bar.Close);
            this.hhllBarOpen.Add(bar.OpenUtc);
            this.hhllBarHigh.Add(bar.High);
            this.hhllBarLow.Add(bar.Low);
            advanced = true;
        }

        // barcolor(iff(changebarcol, iff(trend == 1, bcolup, bcoldn), na)):
        // a bar's trend never changes once its bar has closed, so each bar is
        // coloured exactly once.
        if (this.HhLlChangeBarColor)
        {
            var colorable = Math.Min(this.hhllBarOpen.Count,
                                     Math.Min(bars.Count, this.Count));
            var trends = this.hhllEngine.TrendSeries;

            for (var i = this.hhllColored; i < colorable; i++)
            {
                // THREE STATES, THREE COLOURS. The engine's trend is +1, -1 or 0,
                // and folding 0 in with -1 told the operator a market the engine
                // had not resolved was falling.
                Color barColor = trends[i] switch
                {
                    > 0 => this.HhLlUpColor,
                    < 0 => this.HhLlDownColor,
                    _ => this.HhLlUndecidedColor,
                };

                this.SetBarColor(barColor, bars.Count - 1 - i);
            }

            this.hhllColored = colorable;
            this.hhllColorsApplied = this.hhllColored > 0;
        }
        else if (this.hhllColorsApplied)
        {
            this.ClearHhLlColors(bars.Count);
        }

        if (advanced || this.hhllDrawable.Labels.Length == 0)
            this.hhllDrawable = this.BuildHhLlDrawable();

        // EVERY FOLD, UNCONDITIONALLY -- unlike the HH/LL drawable above, which only
        // changes when a bar closes. The fib's far end follows the RUNNING extreme
        // including the bar forming right now, so rebuilding it only on a bar close is
        // exactly the staleness the live anchor exists to remove.
        this.fibDrawable = this.BuildFibDrawable(bars, closed);

        // ONE evidence line per engine build, written once the seed has caught
        // up. Exists because a live chart showed default candle colours with
        // the port otherwise rendering, and whether SetBarColor even RAN with
        // valid indices could not be established from a screenshot. This line
        // settles it: how many bars were fed, how many were coloured, and what
        // the two bar counts were at that moment. The cause of any mismatch
        // stays UNVERIFIED until this log is read from the platform host.
        if (!this.hhllSeedReported && this.hhllBarOpen.Count >= closed)
        {
            this.hhllSeedReported = true;
            var evidence =
                $"HH/LL: fed {this.hhllBarOpen.Count} closed bars, coloured "
                + $"{this.hhllColored} (colourBars={this.HhLlChangeBarColor}, "
                + $"Count={this.Count}, bars.Count={bars.Count}, "
                + $"labels={this.hhllEngine.Labels.Count}).";
            this.Report(evidence);
        }
    }

    /// <summary>barcolor(na): every colour this port applied, removed.</summary>
    private void ClearHhLlColors(int barsCount)
    {
        var cap = Math.Min(this.hhllColored, Math.Min(barsCount, this.Count));

        for (var i = 0; i < cap; i++)
            this.SetBarColor(null, barsCount - 1 - i);

        this.hhllColored = 0;
        this.hhllColorsApplied = false;
    }

    // ---- HTF zones (approved plan 2026-08-28) ----------------------------

    /// <summary>
    /// Feeds the zone pipeline every closed chart bar exactly once, in the
    /// golden-tested order: the HTF builder first (a chart bar in a later
    /// bucket closes the previous HTF bar, which reaches the zone engine
    /// BEFORE this chart bar's rejection check), then the rejection-block
    /// engine against the zones as they stand. Runs inside the fold gate;
    /// same cursor/full-recompute discipline as <see cref="FeedHhLl"/>.
    /// </summary>
    private void FeedZones()
    {
        var bars = this.HistoricalData;

        if (bars is null)
            return;

        if (!this.ZonesEnabled)
        {
            if (this.zoneEngine is not null)
                this.ResetZoneState();
            return;
        }

        if (!OrbCore.Duration.TryParse(this.ZoneTimeframe, out var period)
            || period <= TimeSpan.Zero)
        {
            this.zoneDrawable = ZoneDrawable.Empty with
            {
                Status = $"zones: timeframe '{this.ZoneTimeframe}' unparseable — nothing drawn",
            };
            return;
        }

        var closed = bars.Count - 1;

        if (closed < 0)
            return;

        if (this.zoneEngine is null || this.rbEngine is null
            || this.htfBuilder is null || this.zoneHtfPeriod != period
            || this.zoneChartBarOpen.Count > closed)
        {
            this.ResetZoneState();
            this.zoneEngine = new ZoneEngine();
            this.rbEngine = new RejectionBlockEngine();
            this.htfBuilder = new HtfBarBuilder(period);
            this.zoneHtfPeriod = period;
        }

        var zoneEngine = this.zoneEngine;
        var rbEngine = this.rbEngine;
        var htfBuilder = this.htfBuilder;

        try
        {
            while (this.zoneChartBarOpen.Count < closed)
            {
                if (!this.TryReadBar(bars, this.zoneChartBarOpen.Count, out var bar))
                    break;

                if (htfBuilder.Add(
                        bar.OpenUtc, bar.CloseUtc, this.OpenOf(bars, this.zoneChartBarOpen.Count),
                        bar.High, bar.Low, bar.Close, bar.Volume, out var closedHtf))
                {
                    zoneEngine.Feed(
                        closedHtf.Open, closedHtf.High, closedHtf.Low, closedHtf.Close);
                    this.htfBarOpen.Add(closedHtf.OpenTimeUtc);
                }

                double? barDelta = this.deltaByBarOpen.TryGetValue(bar.OpenUtc, out var d)
                    ? d : null;
                rbEngine.Feed(
                    this.OpenOf(bars, this.zoneChartBarOpen.Count),
                    bar.High, bar.Low, bar.Close, barDelta, zoneEngine.Zones);
                this.zoneChartBarOpen.Add(bar.OpenUtc);
            }
        }
        catch (ArgumentException ex)
        {
            // HtfBarBuilder refuses a chart bar that straddles the HTF
            // bucket: the chart timeframe does not divide the zone
            // timeframe. Surfaced, engines dropped, nothing half-drawn.
            this.ResetZoneState();
            this.zoneFault = PathDisplay.Redact(
                $"zones: chart timeframe does not divide {this.ZoneTimeframe} "
                + $"({ex.Message})");
            this.zoneDrawable = ZoneDrawable.Empty with { Status = this.zoneFault };
            return;
        }

        this.zoneDrawable = this.BuildZoneDrawable();
    }

    /// <summary>
    /// The chart bar's OPEN price. <see cref="Bar"/> deliberately omits it
    /// (nothing else here needed opens); read directly, same guarded
    /// indexer as <see cref="TryReadBar"/>.
    /// </summary>
    private double OpenOf(HistoricalData bars, int index)
        => bars[index, SeekOriginHistory.Begin] is HistoryItemBar item ? item.Open : double.NaN;

    private void ResetZoneState()
    {
        this.zoneEngine = null;
        this.rbEngine = null;
        this.htfBuilder = null;
        this.htfBarOpen.Clear();
        this.zoneChartBarOpen.Clear();
        this.zoneDrawable = ZoneDrawable.Empty;
    }

    /// <summary>Engine indices resolved to time anchors for the paint thread.</summary>
    private ZoneDrawable BuildZoneDrawable()
    {
        var engine = this.zoneEngine;
        var rb = this.rbEngine;

        if (engine is null || rb is null)
            return ZoneDrawable.Empty;

        var zones = new List<ZoneDraw>(engine.Zones.Count);
        var live = 0;
        var agedOut = 0;
        var oversize = 0;
        // ADR is measured at start-up; zero means unmeasured, and an
        // unmeasured ADR must not silently become "no height filter" —
        // it becomes a stated one.
        double maxHeight = this.averageDailyRange > 0
            ? this.averageDailyRange * this.ZoneMaxHeightAdrPercent / 100.0
            : double.PositiveInfinity;

        foreach (var zone in engine.Zones)
        {
            if (zone.StartBar >= this.htfBarOpen.Count)
                continue;

            if (engine.BarsFed - zone.StartBar > this.ZoneMaxAgeHtfBars)
            {
                agedOut++;
                continue;
            }

            if (zone.Top - zone.Bottom > maxHeight)
            {
                oversize++;
                continue;
            }

            DateTime? endUtc = null;
            if (zone.MitigatedBar is { } mitigated && mitigated < this.htfBarOpen.Count)
                endUtc = this.htfBarOpen[mitigated] + this.zoneHtfPeriod;
            else
                live++;

            zones.Add(new ZoneDraw(
                zone.Kind, zone.IsBullish, this.htfBarOpen[zone.StartBar],
                endUtc, zone.Top, zone.Bottom, zone.State));
        }

        var blocks = new List<RbDraw>(rb.Blocks.Count);

        foreach (var block in rb.Blocks)
        {
            if (block.Bar >= this.zoneChartBarOpen.Count)
                continue;

            blocks.Add(new RbDraw(
                this.zoneChartBarOpen[block.Bar], block.IsBullish,
                block.Top, block.Bottom, block.Mid, block.BarDelta));
        }

        var heightNote = double.IsPositiveInfinity(maxHeight)
            ? " · height filter off (ADR unmeasured)"
            : oversize > 0 ? $", {oversize} oversize hidden" : string.Empty;
        var ageNote = agedOut > 0 ? $", {agedOut} aged out" : string.Empty;
        // READOUT ONLY. The measured-null label that used to be appended here is a
        // constant, so it states nothing after its first reading and now goes to the log
        // via PublishStatus. What is left changes, and is therefore worth chart space.
        var status =
            $"zones {this.ZoneTimeframe}: {live} live drawn{ageNote}{heightNote} "
            + $"· {blocks.Count} RB";
        return new ZoneDrawable(zones.ToArray(), blocks.ToArray(), status);
    }

    // ---- delta series (approved plan 2026-08-28) -------------------------

    /// <summary>
    /// Builds the delta engine on the chart's own bar period and seeds it
    /// from the platform's volume-analysis backfill when that completes.
    /// Runs during initialisation, BEFORE the tick subscription, so the
    /// seed-before-live contract holds by construction. When the chart has
    /// no readable bars yet the panel stays live-less for this attach,
    /// with the reason on record.
    /// </summary>
    /// <summary>
    /// Chooses which connection's tick history feeds the profile, retrying while the platform
    /// is still filling its symbol list.
    ///
    /// THE LIST IS EMPTY AT FIRST AND THAT IS NOT AN ANSWER. `Core.Symbols` was observed
    /// returning 0 entries twice and 1,072 roughly two minutes later, on one install with no
    /// configuration change. Resolving once and believing the first reply would report "no
    /// connection carries this contract" for the opening minutes of every session — a sentence
    /// indistinguishable from a genuinely absent instrument.
    ///
    /// Runs on the load's own thread, so the polling costs the chart nothing.
    /// </summary>
    /// <param name="chartSymbol">The symbol the chart is on.</param>
    /// <param name="token">Cancelled when the indicator is cleared.</param>
    /// <returns>The verdict, and the platform symbol to load from when there is one.</returns>
    private (ProfileSourceChoice Choice, Qt.Symbol? Source) ResolveProfileSource(
        Qt.Symbol chartSymbol, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + SymbolListWait;
        var chartRoot = ResolveContractRoot(chartSymbol, this.SymbolRootOverride);
        var choice = new ProfileSourceChoice(
            ProfileSourceOutcome.NotPopulatedYet, null,
            "symbol list was never read", string.Empty);

        while (!token.IsCancellationRequested)
        {
            var candidates = this.ProjectSymbols(out var byKey, out var unreadable);

            choice = ProfileSourceSelection.Select(
                chartRoot, chartSymbol.Name ?? string.Empty,
                chartSymbol.ExpirationDate, chartSymbol.Last,
                chartSymbol.ConnectionId, DateTime.UtcNow, candidates);

            if (unreadable > 0)
            {
                // Counted, never hidden: a symbol the platform refused to describe is a symbol
                // this choice could not consider, and a silent skip would look like absence.
                this.Report(
                    $"Profile source: {unreadable} symbol(s) could not be read while choosing a "
                    + "tick-history connection and were not considered.");
            }

            // A REFUSAL IS ONLY FINAL WHEN EVERY CONNECTION HAS SETTLED.
            //
            // Measured 2026-09-01T02:33Z: one instance saw a PARTIALLY populated list — a
            // single connection, the one that happened to be up — concluded "none serves tick
            // history" in 0.01s, and never looked again, while one connection was still connecting and
            // would have served it. An empty list was already treated as provisional; a list
            // that is merely incomplete is the same race one level subtler, and treating it as
            // an answer is how a working source gets reported as absent.
            var settling = this.AnyConnectionSettling();

            if (choice.Outcome != ProfileSourceOutcome.NotPopulatedYet
                && !(choice.Outcome == ProfileSourceOutcome.NoCapableConnection && settling))
            {
                return (choice, choice.Chosen is { } picked
                    ? byKey.GetValueOrDefault((picked.ConnectionId, picked.Name))
                    : null);
            }

            if (DateTime.UtcNow >= deadline)
                break;

            Thread.Sleep(SymbolListPollMs);
        }

        return (choice with
        {
            Status = $"{choice.Status} (waited {SymbolListWait.TotalSeconds:N0}s)",
        }, null);
    }

    /// <summary>
    /// Whether any connection is still coming up.
    ///
    /// Only <see cref="Qt.ConnectionState.Connecting"/> counts. A connection that is
    /// Disconnected, ConnectionLost or Fail has finished doing whatever it is going to do, and
    /// waiting on it would turn a permanent absence into an indefinite wait.
    /// </summary>
    private bool AnyConnectionSettling()
    {
        try
        {
            foreach (var connection in Qt.Core.Instance.Connections.All)
            {
                if (connection.State == Qt.ConnectionState.Connecting)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            // Unable to tell: treat the estate as settled rather than waiting forever on a
            // question that cannot be asked.
            return false;
        }
    }

    /// <summary>
    /// Projects every symbol the platform is offering into the §11 shape the choice reads.
    ///
    /// Each symbol is read defensively and INDIVIDUALLY. The platform hands back a live object
    /// per row and a single unreadable one must not cost the whole list — but nor may it vanish
    /// silently, so the count comes back for reporting rather than being swallowed.
    /// </summary>
    /// <param name="byKey">Maps a chosen candidate back to the platform symbol to load from.</param>
    /// <param name="unreadable">How many symbols could not be described.</param>
    private IReadOnlyList<SymbolCandidate> ProjectSymbols(
        out Dictionary<(string, string), Qt.Symbol> byKey, out int unreadable)
    {
        byKey = new Dictionary<(string, string), Qt.Symbol>();
        unreadable = 0;

        var offered = Qt.Core.Instance.Symbols;
        var candidates = new List<SymbolCandidate>(offered?.Length ?? 0);

        foreach (var candidate in offered ?? Array.Empty<Qt.Symbol>())
        {
            try
            {
                var connectionId = candidate.ConnectionId ?? string.Empty;
                var name = candidate.Name ?? string.Empty;

                candidates.Add(new SymbolCandidate(
                    name,
                    connectionId,
                    this.ConnectionName(connectionId),
                    string.IsNullOrWhiteSpace(candidate.Root) ? name : candidate.Root,
                    candidate.ExpirationDate,
                    QuantowerHistoryLoader.ServesTickHistory(candidate),
                    candidate.Last));

                byKey[(connectionId, name)] = candidate;
            }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException
                                          or ArgumentException or NotSupportedException)
            {
                unreadable++;
            }
        }

        return candidates;
    }

    /// <summary>
    /// A connection's user-facing name, or its id when the platform will not name it.
    ///
    /// The id is a fallback rather than a blank, because the chart label built from this is the
    /// operator's only statement of where a borrowed profile came from.
    /// </summary>
    private string ConnectionName(string connectionId)
    {
        try
        {
            var connection = Qt.Core.Instance.Connections[connectionId];
            return string.IsNullOrWhiteSpace(connection?.Name) ? connectionId : connection.Name;
        }
        catch (Exception ex) when (ex is NullReferenceException or KeyNotFoundException
                                      or InvalidOperationException)
        {
            return connectionId;
        }
    }

    /// <summary>
    /// Fetches the profile's tick-history source, off the path that draws the chart.
    ///
    /// TWO DEFECTS FROM THE FIRST LIVE RUNS ARE ANSWERED HERE, and both were mine.
    ///
    /// IT ASKED FOR THREE DAYS. The span came from the chart's OLDEST bar, which on a 1-minute
    /// chart reaches back days: measured 2026-08-31, a request for 2026-08-28T20:27Z onward
    /// needed 73 one-hour chunks, completed 5, and spent its budget. The profiles never wanted
    /// that. FRVP's default range is the current session and the anchored profile sits inside
    /// it, so the session open is the earliest instant either can ask for — seven chunks, not
    /// seventy-three. A range typed or clicked further back is refused BY COVERAGE, with both
    /// instants named, which is the honest outcome rather than a silent partial answer.
    ///
    /// IT BLOCKED THE CHART. Run inline it sat in front of the fold timer, so the load's cost
    /// was chart-blank time — reintroducing through the back door the exact fault this round
    /// set out to remove. It now runs on its own thread and publishes when it is ready; the
    /// chart draws immediately and the profile fills in behind it.
    ///
    /// THREADING, STATED PLAINLY: whether the platform supports GetHistory from a thread it did
    /// not create is UNVERIFIED — the vendor documentation is silent on it, so this is not a
    /// claim that it is supported. It is bounded rather than assumed: the loader's catch is
    /// broad and returns a stated refusal, so the worst observed outcome is a profile that does
    /// not draw and says why, never a thrown indicator. The next cold start settles it.
    ///
    /// The assignment takes foldGate so the fold cannot observe a half-published cache, and the
    /// generation bump inside that lock is what makes the waiting rebuild re-run.
    /// </summary>
    private void LoadProfileTickSource()
    {
        // THE ABSORBED DISPLAYS ARE A FIRST-CLASS REASON TO LOAD THIS, NOT A PASSENGER. They
        // rebuild footprints from per-price volume, and on this connection the platform serves no
        // per-price VOLUME-ANALYSIS levels at all (ALLOW_VOLUME_ANALYSIS_FROM_TICK_HISTORY =
        // NotAllowed) — so tick history is their only source. Until this, the load ran only when a
        // PROFILE wanted it, and on a Sunday with no session open yet it reported "not requested"
        // and the flow seeding found nothing. Observed on the operator's chart 2026-09-13.
        // THE SEEDING WAITS FOR THIS, NOT FOR A CLOCK. Every return below marks the load
        // finished — a load that was never requested, or that failed, is still an ANSWER, and a
        // seeding that waited for a cache nobody was going to fill would never run at all.
        this.profileTickLoadFinished = false;

        var flowWantsHistory = this.FlowEnabled
            && (this.FlowStackedImbalance || this.FlowAbsorption
                || this.FlowUnfinishedAuction || this.FlowClusterSearch
                || this.FlowClusterStatistics);

        if (!this.FrvpEnabled && !this.AvpEnabled && !flowWantsHistory)
        {
            this.profileTickLoadFinished = true;
            return;
        }

        if (this.Symbol is not { } symbol)
        {
            this.profileTickLoadFinished = true;
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var opens = this.ObserveSessionOpens(nowUtc, out var availability);

        if (availability != SessionWindowAvailability.Available)
        {
            this.Report(
                $"Profile tick source: not requested — session opens unavailable ({availability}), "
                + "so the span the profiles need cannot be established.");
            this.profileTickLoadFinished = true;
            return;
        }

        // The most recent open at or before now: FRVP's own default range, and the earliest
        // instant an anchored profile inside this session can name.
        var fromUtc = default(DateTime);

        foreach (var open in opens)
        {
            if (open <= nowUtc && open > fromUtc)
                fromUtc = open;
        }

        // The absorbed displays need a FIXED LOOK-BACK rather than "since the session opened",
        // because they rebuild history rather than describing the current session. The span is
        // the EARLIER of the two so one load serves both — asking twice for overlapping tick
        // history would be two blocking platform calls for one set of prints.
        if (flowWantsHistory && this.config is { } flowConfig)
        {
            // The same trading-time anchor the seeding uses, so the load covers the span the
            // seeding will ask the cache for rather than a window of empty weekend.
            var flowAnchor = this.NewestClosedBarUtc() ?? nowUtc;
            var flowFrom = flowAnchor.AddHours(-Math.Max(flowConfig.Flow.Ingestion.SeedHoursBack, 1));

            if (fromUtc == default || flowFrom < fromUtc)
                fromUtc = flowFrom;
        }

        if (fromUtc == default)
        {
            this.Report(
                "Profile tick source: not requested — no session has opened at or before now, "
                + "so there is no range for a profile to cover.");
            this.profileTickLoadFinished = true;
            return;
        }

        var cts = new CancellationTokenSource();
        this.profileTickCts = cts;
        var token = cts.Token;

        // A TASK THAT THROWS IS A TASK NOBODY HEARS. An unobserved exception inside Task.Run
        // is swallowed by the runtime: the work stops, nothing is logged, and the chart shows
        // the same blank profile it would show if the load had simply found nothing. Observed
        // 2026-09-01 — an instance ran for four minutes and never emitted a source line at all,
        // which no path in this method can otherwise produce. The catch below reports and
        // rethrows nothing; it exists so a failure is legible rather than invisible.
        _ = Task.Run(
            () =>
            {
                try
                {
                    this.LoadProfileTickSourceCore(symbol, fromUtc, token);
                }
                catch (Exception ex)
                {
                    this.Report(
                        $"Profile tick source: FAILED — {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    // A FAILED LOAD IS STILL AN ANSWER. Without this the seeding would wait for a
                    // cache that is never coming, and the absorbed displays would stay empty with
                    // nothing but a FAILED line far above to explain it.
                    this.profileTickLoadFinished = true;
                }
            },
            token);
    }

    /// <summary>
    /// The tick-source load itself, separated so its caller can report a failure rather than
    /// let the task die unheard.
    /// </summary>
    /// <param name="symbol">The chart's symbol.</param>
    /// <param name="fromUtc">Earliest instant the profiles can ask for.</param>
    /// <param name="token">Cancelled when the indicator is cleared.</param>
    private void LoadProfileTickSourceCore(
        Qt.Symbol symbol, DateTime fromUtc, CancellationToken token)
    {
        {
            {
                var clock = Stopwatch.StartNew();

                // WHICH CONNECTION, BEFORE WHICH DATA. The chart's own connection may not serve
                // tick history at all — another connection/another connection reports available=False permanently —
                // so the source is chosen first and the load follows.
                var (choice, source) = this.ResolveProfileSource(symbol, token);

                if (source is null)
                {
                    this.Report(LoadTiming.Took(
                        $"Profile tick source: {choice.Status}", clock.Elapsed));
                    return;
                }

                // Generous, because nothing is waiting on it any more. Still bounded: an
                // unbounded request would hold a thread and a vendor connection indefinitely.
                var loaded = QuantowerHistoryLoader.LoadTickLevels(
                    source, fromUtc, DateTime.UtcNow, TimeSpan.FromSeconds(90),
                    choice.SourceLabel);

                clock.Stop();

                if (token.IsCancellationRequested)
                    return;

                lock (this.foldGate)
                {
                    if (token.IsCancellationRequested)
                        return;

                    this.profileTickCache = loaded.Cache;
                    this.profileTickGeneration++;
                    this.profileTickLoadFinished = true;
                }

                this.Report(LoadTiming.Took(
                    $"Profile tick source: {choice.Status}; {loaded.Status}", clock.Elapsed));
            }
        }
    }

    private void InitialiseDelta()
    {
        this.deltaEngine = null;
        this.deltaFlips.Reset();
        this.shelfScan = ShelfScan.Empty;
        this.shelfScanKey = string.Empty;
        this.shelfScanNewest = default;
        this.deltaLevelsDrawable = DeltaLevelsDrawable.Empty;
        this.deltaByBarOpen.Clear();
        this.deltaByBarOpenOrder.Clear();
        this.deltaSessionOpen = default;

        var bars = this.HistoricalData;

        if (bars is null || bars.Count < 2
            || !this.TryReadBar(bars, 0, out var first))
        {
            this.deltaHistoryStatus = "history: none — chart bars not readable "
                + "at start; delta since attach.";
            return;
        }

        // MEASURED on ryzen-pc 2026-08-28 (orbix-startup.log): the chart
        // reports TimeRight − TimeLeft as 00:04:59.9999999 — one tick shy
        // of the bar period. Fed raw into the wall-clock modulo grid that
        // shortfall misaligns every live delta bucket, so the period is
        // rounded to the whole second.
        var rawPeriod = first.CloseUtc - first.OpenUtc;
        this.deltaChartPeriod = TimeSpan.FromSeconds(Math.Round(rawPeriod.TotalSeconds));

        if (this.deltaChartPeriod <= TimeSpan.Zero)
        {
            this.deltaHistoryStatus = "history: none — chart bar period "
                + "unreadable; delta panel disabled for this attach.";
            return;
        }

        var engine = new DeltaSeriesEngine(this.deltaChartPeriod);
        this.TryOpenParityWriter();

        // The parameters are built ONCE and used for both the observation and the request,
        // so what the capability report describes is exactly what was asked for — a report
        // about different parameters than the ones sent would be worse than no report.
        var vaParameters = QuantowerHistoryLoader.BuildParameters(
            this.VolumeAnalysisDelta, this.VolumeAnalysisForceTickData, bars.Symbol);

        this.volumeAnalysisCapability = QuantowerHistoryLoader.ObserveCapability(
            bars, vaParameters, this.deltaChartPeriod);

        // READ BESIDE THE CAPABILITY PROBE because it answers the neighbouring question:
        // the capability says what the vendor serves, these say what the platform believes
        // about the SHAPE of the Level 2 stream. Nothing branches on them yet — see
        // Level2RuleReport for why the meaning is not yet established.
        //
        // bars.Symbol, NOT this.subscribed. Both hosts logged "none could be read" on the
        // first run while the volume-analysis rule read fine two lines above — and the only
        // difference between the two reads was the symbol reference. Using the one already
        // proven to work in this exact path removes the variable; the SymbolSeen flag now
        // reports whether that was in fact the cause rather than leaving it assumed.
        this.level2Rules = QuantowerHistoryLoader.ReadLevel2Rules(bars.Symbol);

        // TIMED for the same reason as the daily-range call: it blocks, the loader is silent,
        // and its cost was previously visible only as a gap between unrelated lines. The 10s
        // argument is the loader's own wait budget, not a measurement — what actually elapsed
        // is what gets reported.
        var volumeClock = Stopwatch.StartNew();

        var backfill = QuantowerHistoryLoader.LoadVolumeAnalysis(
            bars, TimeSpan.FromSeconds(10), vaParameters);

        volumeClock.Stop();
        this.deltaHistoryStatus = backfill.Status;

        // The one new line this change adds. The seed loop below reports bars seeded, but
        // only AFTER seeding them, so it cannot say how long the fetch itself cost.
        this.Report(LoadTiming.Took(
            $"Volume-analysis backfill: {backfill.Status}", volumeClock.Elapsed));

        this.LoadProfileTickSource();

        try
        {
            foreach (var seeded in backfill.Bars)
            {
                engine.SeedHistorical(seeded);
                this.RememberBarDelta(seeded.OpenTimeUtc, seeded.Delta);
                this.WriteParityLine(seeded, "va");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A malformed backfill (out of order, duplicate) must not
            // poison the series: start over live-only with the reason on
            // record.
            engine = new DeltaSeriesEngine(this.deltaChartPeriod);
            this.deltaHistoryStatus =
                $"history: none — backfill refused ({ex.Message}); delta since attach.";
        }

        this.deltaEngine = engine;
        // The parity state goes to the startup log too: the 2026-08-28
        // 0-byte-file investigation had to reconstruct writer state from
        // timestamps because only the panel showed it.
        // ONE line, not two. This wrote the same fact twice -- a superset via Report and
        // a subset direct -- which is half of why the log looked like two instances.
        this.Report($"Delta series: period {this.deltaChartPeriod}, "
                    + $"{this.deltaHistoryStatus} {this.parityStatus}".TrimEnd());
    }

    private void RememberBarDelta(DateTime barOpenUtc, double delta)
    {
        if (this.deltaByBarOpen.TryAdd(barOpenUtc, delta))
        {
            this.deltaByBarOpenOrder.Enqueue(barOpenUtc);

            while (this.deltaByBarOpenOrder.Count > DeltaByBarOpenCap)
                this.deltaByBarOpen.Remove(this.deltaByBarOpenOrder.Dequeue());
        }
    }

    /// <summary>
    /// Publishes the imbalance snapshot; runs inside the fold gate.
    ///
    /// EVALUATED HERE, NOT IN PAINT. The rule walks every retained bar's footprint, and paint
    /// runs on the UI thread on every frame — doing this there would recompute an unchanged
    /// answer sixty times a second while the market did nothing. It also reads engine state
    /// the market-data path is writing, which is only safe inside this gate.
    /// </summary>
    private void PublishImbalance()
    {
        var engine = this.footprint;

        if (engine is null || !this.ImbalanceDrawEnabled || this.config is not { } cfg)
        {
            this.imbalanceDrawable = ImbalanceDrawable.Empty;
            return;
        }

        var minRun = cfg.Imbalance.MinRun;
        var tickSize = this.instrument.TickSize;

        if (!(tickSize > 0))
        {
            // The price grid is not known yet. Reported rather than drawn against a guessed
            // tick, which would place every row at the wrong price.
            this.imbalanceDrawable = new ImbalanceDrawable(
                Array.Empty<ImbalanceBarDraw>(),
                "imbalance: waiting for the instrument's tick size");
            return;
        }

        var bars = new List<ImbalanceBarDraw>();
        var stackedBars = 0;

        foreach (var footprint in engine.ClosedBarFootprints)
        {
            var levels = engine.Evaluate(footprint.Cells);

            if (levels.Count == 0)
                continue;

            var marks = BuildMarks(levels, tickSize, minRun, this.ImbalanceDrawLoose);

            if (marks.Length == 0)
                continue;

            bars.Add(new ImbalanceBarDraw(footprint.OpenTimeUtc, marks));

            foreach (var mark in marks)
            {
                if (mark.InStack)
                {
                    stackedBars++;
                    break;
                }
            }
        }

        // READOUT ONLY — the same posture as the zone and delta status lines. The counts
        // change every bar; the unverified label does not.
        var gate = cfg.Imbalance.GateEntries ? "gate ON" : "gate recording only";
        var status =
            $"imbalance {cfg.Imbalance.Ratio:0.##}x / min {cfg.Imbalance.MinVolume:0} / "
            + $"stack {minRun} · {stackedBars} of {bars.Count} drawn bars stacked · {gate} · "
            + "UNVERIFIED on this stack";

        this.imbalanceDrawable = new ImbalanceDrawable(bars.ToArray(), status);
    }

    /// <summary>
    /// Turns one bar's evaluated rows into paint marks.
    ///
    /// Stack membership comes from <see cref="ImbalanceRule.Runs"/> rather than being
    /// recomputed here, so the rows drawn strong are exactly the rows the gate counts.
    /// </summary>
    private static ImbalanceMark[] BuildMarks(
        IReadOnlyList<ImbalanceLevel> levels, double tickSize, int minRun, bool includeLoose)
    {
        // ONE ARRAY PER SIDE, NOT ONE SHARED. A row can sit inside a buy stack while its sell
        // diagonal is imbalanced on its own, and a shared flag would paint that sell mark as
        // part of a stack that does not include it — a chart asserting a pattern nobody made.
        var stackedBuy = new bool[levels.Count];
        var stackedSell = new bool[levels.Count];

        Mark(ImbalanceRule.Runs(levels, tickSize, buySide: true), stackedBuy);
        Mark(ImbalanceRule.Runs(levels, tickSize, buySide: false), stackedSell);

        var marks = new List<ImbalanceMark>();

        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i].BuySide.IsImbalanced && (stackedBuy[i] || includeLoose))
                marks.Add(new ImbalanceMark(levels[i].Price, BuySide: true, stackedBuy[i]));

            if (levels[i].SellSide.IsImbalanced && (stackedSell[i] || includeLoose))
                marks.Add(new ImbalanceMark(levels[i].Price, BuySide: false, stackedSell[i]));
        }

        return marks.Count == 0 ? Array.Empty<ImbalanceMark>() : marks.ToArray();

        void Mark(IReadOnlyList<ImbalanceRun> runs, bool[] into)
        {
            foreach (var run in runs)
            {
                if (!run.IsStacked(minRun))
                    continue;

                for (var i = run.StartIndex; i < run.EndIndex; i++)
                    into[i] = true;
            }
        }
    }

    /// <summary>
    /// Publishes the delta flip levels and the absorption shelves; runs inside the fold gate.
    ///
    /// ASSEMBLED HERE RATHER THAN DURING PAINT, for the reason on <see cref="PublishAbsorption"/>:
    /// the shelf scan walks up to several hundred footprints, and doing that on the UI thread once
    /// per frame would spend most of a redraw re-deriving a set that only changes when a bar closes.
    ///
    /// THE SCAN IS CACHED ON THE NEWEST CLOSED FOOTPRINT AND THE SETTINGS, and this method takes no
    /// clock at all. A cache keyed on time would re-scan a still market; one keyed on the visible
    /// range would move the levels when the chart scrolled.
    /// </summary>
    private void PublishDeltaLevels()
    {
        var wantFlips = this.ShowDeltaFlips;
        var wantShelves = this.ShowAbsorptionShelves;

        if (!wantFlips && !wantShelves)
        {
            this.deltaLevelsDrawable = DeltaLevelsDrawable.Empty;
            return;
        }

        var flips = wantFlips ? this.PickFlips() : Array.Empty<DeltaFlip>();
        var shelves = Array.Empty<AbsorptionShelf>() as IReadOnlyList<AbsorptionShelf>;
        var shelfNote = string.Empty;

        if (wantShelves)
        {
            var scan = this.ScanShelves();

            if (scan.Problem is { } problem)
            {
                shelfNote = problem;
            }
            else
            {
                shelves = scan.Found.Count > this.MaxAbsorptionShelves
                    ? Subset(scan.Found, this.MaxAbsorptionShelves)
                    : scan.Found;

                // The cap is stated rather than applied silently: "5 shelves" and "5 of 23
                // shelves" are different facts about the tape.
                shelfNote = scan.Found.Count > shelves.Count
                    ? $"{shelves.Count} of {scan.Found.Count} shelves"
                    : $"{shelves.Count} shelf{(shelves.Count == 1 ? string.Empty : "s")}";
            }
        }

        // THE CAPTION CARRIES BOTH MEASURED RECORDS, on the chart, every frame. A line on a chart
        // implies a read worth acting on; neither of these has earned that, and the place that has
        // to say so is the chart rather than a source comment nobody reading the chart can see.
        var status = string.Concat(
            $"Δ flip levels {flips.Count}",
            wantShelves ? " · " + shelfNote : string.Empty,
            string.Empty);

        this.deltaLevelsDrawable = new DeltaLevelsDrawable(flips, shelves, status);
    }

    /// <summary>
    /// The flip levels that pass the drawing filters, oldest first so the renderer can fade all but
    /// the last.
    /// </summary>
    private IReadOnlyList<DeltaFlip> PickFlips()
    {
        var all = this.deltaFlips.Flips;

        if (all.Count == 0)
            return Array.Empty<DeltaFlip>();

        var session = this.deltaFlips.Read.SessionOpenUtc;
        var picked = new List<DeltaFlip>();

        for (var i = 0; i < all.Count; i++)
        {
            if (this.DeltaFlipsThisSessionOnly && all[i].SessionOpenUtc != session)
                continue;

            picked.Add(all[i]);
        }

        if (picked.Count > this.MaxDeltaFlips)
            picked.RemoveRange(0, picked.Count - this.MaxDeltaFlips);

        return picked;
    }

    /// <summary>
    /// Runs the absorption shelf scan over the newest closed footprints.
    ///
    /// THE CLOSE COMES FROM THE DELTA SERIES, MATCHED BY BAR OPEN. A footprint records volume at
    /// each price and not the bar's own close, and the break test needs a close. Both series are
    /// built on the chart's bar period, so their opens land on one grid — but that is an alignment
    /// this method VERIFIES rather than assumes: a footprint with no matching delta bar ABANDONS
    /// the scan with a stated reason. Inventing a close from the footprint's traded prices would
    /// silently decide which shelves broke.
    /// </summary>
    private ShelfScan ScanShelves()
    {
        if (this.footprint is not { } prints || this.deltaEngine is not { } series)
            return ShelfScan.Empty;

        var tickSize = this.instrument.TickSize;

        if (tickSize <= 0d)
            return ShelfScan.Empty;

        var footprints = prints.ClosedBarFootprints;

        if (footprints.Count == 0)
            return ShelfScan.Empty;

        var key = string.Join(
            '|',
            footprints.Count,
            this.ShelfWindowBars,
            this.ShelfMinVolume,
            this.ShelfMinSharePercent,
            this.ShelfMinLeanPercent,
            this.ShelfMinBars,
            this.ShelfMaxOnePrintPercent,
            this.ShelfThroughTicks,
            this.ShelfKeepBroken);

        var newest = default(DateTime);

        foreach (var print in footprints)
        {
            if (print.OpenTimeUtc > newest)
                newest = print.OpenTimeUtc;
        }

        if (this.shelfScanKey == key && this.shelfScanNewest == newest)
            return this.shelfScan;

        var closes = new Dictionary<DateTime, double>();

        foreach (var bar in series.Bars)
            closes[bar.OpenTimeUtc] = bar.Close;

        var ordered = new List<FootprintEngine.BarFootprint>(footprints);
        ordered.Sort(static (a, b) => a.OpenTimeUtc.CompareTo(b.OpenTimeUtc));

        var from = Math.Max(0, ordered.Count - this.ShelfWindowBars);
        var window = new List<ShelfBar>(ordered.Count - from);

        for (var i = from; i < ordered.Count; i++)
        {
            var print = ordered[i];

            if (!closes.TryGetValue(print.OpenTimeUtc, out var close))
            {
                var refused = new ShelfScan(
                    Array.Empty<AbsorptionShelf>(),
                    "absorption shelves: the footprint at "
                    + print.OpenTimeUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + " has no matching delta bar, so its close is unknown and no shelf can be "
                    + "judged broken. Scan abandoned rather than guessed.",
                    0d,
                    0);

                this.shelfScan = refused;
                this.shelfScanKey = key;
                this.shelfScanNewest = newest;
                return refused;
            }

            window.Add(new ShelfBar(print.OpenTimeUtc, close, print.Cells));
        }

        var filter = new ShelfFilter
        {
            MinVolume = this.ShelfMinVolume,
            MinSharePercent = this.ShelfMinSharePercent,
            MinLeanPercent = this.ShelfMinLeanPercent,
            MinBars = this.ShelfMinBars,
            MaxOnePrintSharePercent = this.ShelfMaxOnePrintPercent,
            ThroughTicks = this.ShelfThroughTicks,
            KeepBroken = this.ShelfKeepBroken,
        };

        this.shelfScan = AbsorptionShelfScan.Scan(window, tickSize, filter, MaxShelfPrices);
        this.shelfScanKey = key;
        this.shelfScanNewest = newest;

        return this.shelfScan;
    }

    /// <summary>The first <paramref name="count"/> of a list, without an allocation per frame.</summary>
    private static IReadOnlyList<AbsorptionShelf> Subset(
        IReadOnlyList<AbsorptionShelf> source, int count)
    {
        var taken = new List<AbsorptionShelf>(count);

        for (var i = 0; i < count && i < source.Count; i++)
            taken.Add(source[i]);

        return taken;
    }

    /// <summary>
    /// Distinct prices the shelf scan will hold before abandoning the window.
    ///
    /// Sized well above a normal session's traded range on these products, so it bounds a runaway
    /// rather than trimming ordinary work.
    /// </summary>
    private const int MaxShelfPrices = 4000;

    /// <summary>
    /// Publishes the absorption snapshot; runs inside the fold gate.
    ///
    /// Read as of the fold's instant rather than a wall clock read during paint: the window
    /// is what defines the measure, and a caption computed on the UI thread would describe a
    /// different span every frame while the market did nothing.
    /// </summary>
    private void PublishAbsorption(DateTime nowUtc)
    {
        var engine = this.absorption;

        if (engine is null || !this.AbsorptionDrawEnabled || this.config is not { } cfg)
        {
            this.absorptionDrawable = AbsorptionDrawable.Empty;
            return;
        }

        var snapshot = engine.Read(nowUtc);

        // READOUT ONLY, and the label is the point. The counts change every fold; the measured
        // record does not.
        var gate = cfg.Absorption.GateEntries ? "gate ON" : "gate recording only";

        var status = snapshot.BookKnown
            ? $"absorption {cfg.Absorption.WindowSeconds:0}s · bid {snapshot.Bid.State} · "
              + $"ask {snapshot.Ask.State} · {gate}"
            : $"absorption: the book cannot be vouched for, so the touch is UNMEASURED · {gate}";

        this.absorptionDrawable = new AbsorptionDrawable(
            snapshot.Bid.Price, snapshot.Ask.Price,
            snapshot.Bid.IsAbsorbing, snapshot.Ask.IsAbsorbing, status);
    }

    /// <summary>Publishes the delta panel snapshot; runs inside the fold gate.</summary>
    private void PublishDelta()
    {
        var engine = this.deltaEngine;

        if (engine is null || !this.DeltaEnabled)
        {
            this.deltaDrawable = DeltaDrawable.Empty;
            return;
        }

        var source = engine.Bars.Count > 0 && engine.Bars[0].Source == DeltaSource.VolumeAnalysis
            ? "va+live"
            : "live";
        long unknowns = 0, classified = 0;

        var bars = new DeltaBarDraw[engine.Bars.Count];
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = engine.Bars[i];
            bars[i] = new DeltaBarDraw(bar.OpenTimeUtc, bar.Delta, bar.CumulativeAfter);
            unknowns += bar.Unknowns;
            classified += bar.Buys + bar.Sells;
        }

        var bearish = new List<DateTime>();
        var bullish = new List<DateTime>();
        foreach (var divergence in engine.Divergences)
        {
            (divergence.BearishFlow ? bearish : bullish).Add(divergence.BarOpenUtc);
        }

        var total = unknowns + classified;
        var unknownPct = total > 0 ? (double)unknowns / total * 100.0 : 0.0;
        // READOUT ONLY — see the note on the zone status. The cumulative delta and the
        // unclassified share change every bar; the measured-null label did not.
        var status =
            $"Δ {source} · cum {engine.CumulativeDelta:+0;-0} · unclassified "
            + $"{unknownPct:N0}% of prints · {this.deltaHistoryStatus}"
            + (this.parityStatus.Length != 0 ? $" · {this.parityStatus}" : string.Empty);

        this.deltaDrawable = new DeltaDrawable(
            bars, bearish.ToArray(), bullish.ToArray(), status);
    }

    /// <summary>
    /// Opens the parity NDJSON stream when the research input asks for it.
    /// Same failure posture as <see cref="WriteStartupLog"/>: an
    /// unwritable file is a reported reason, never a fault — the chart is
    /// unaffected.
    /// </summary>
    private void TryOpenParityWriter()
    {
        this.parityWriter?.Dispose();
        this.parityWriter = null;

        this.parityWriter = this.OpenResearchWriter(
            this.DeltaParityDump, "orbix-delta-parity", "parity dump", out this.parityStatus);
    }
    /// <summary>
    /// When the current trading day began, from the session clock rather than a constant.
    ///
    /// A FUTURES DAY IS NOT A CALENDAR DAY. The boundary is the evening open -- 18:00 New
    /// York, which is also the 5 PM Central the operator's prop rules name as the point new
    /// trades are allowed again after a daily-loss halt. Taken from the session that opens
    /// the overnight range, because that session is already configured as the one that
    /// begins the product's day, so this cannot disagree with the clock drawing the chart.
    /// </summary>
    private DateTime? TradingDayStartUtc(DateTime nowUtc)
    {
        if (this.clock is null || this.config is null)
            return null;

        return this.clock.MostRecentOpenUtc(
            this.config.Levels.Overnight.StartSession, nowUtc, this.instrument.Root);
    }
    /// <summary>
    /// The connection the chart itself is on, for scoping fill and position matching.
    ///
    /// Blank is survivable and NOT an error: <see cref="InstrumentMatch.Matches"/> skips the
    /// connection test rather than failing it, and the status line says the reading was
    /// unscoped. A chart that cannot name its connection matching nothing at all is the
    /// failure being fixed, not an acceptable fallback.
    /// </summary>
    private string ChartConnectionId => this.Symbol?.ConnectionId ?? string.Empty;
    /// <summary>
    /// Every platform symbol id that is this chart's contract, the chart's own included.
    ///
    /// The trade-history request can filter on nothing but symbol ids, and the same contract
    /// carries a different id on every connection and a different one again as a continuous
    /// symbol. Asking for one id asks for one vendor's spelling of the instrument.
    /// </summary>
    /// <summary>
    /// The connections that are actually CONNECTED, as (id, name) pairs.
    /// </summary>
    /// <param name="fault">
    /// Empty when the list was read. Otherwise it names what threw, and the caller falls
    /// back to the account-wide history call.
    /// </param>
    /// <remarks>
    /// CONNECTED, NOT ALL. From the installed assembly, Connections.All returns every
    /// CONFIGURED connection whatever its state, while Connections.Connected filters on
    /// Connection.Connected. Asking a dead connection for trade history is what threw, so
    /// the dead ones are never asked.
    /// </remarks>
    private (string Id, string Name)[] ConnectedConnections(out string fault)
    {
        fault = string.Empty;

        try
        {
            var live = new List<(string, string)>();

            foreach (var connection in Qt.Core.Instance.Connections.Connected
                                       ?? Array.Empty<Qt.Connection>())
            {
                if (connection?.Id is { Length: > 0 } id)
                    live.Add((id, connection.Name ?? id));
            }

            return live.ToArray();
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException
                                      or NotSupportedException)
        {
            fault = OrbCore.Diagnostics.FaultDescription.Of(ex);
            return Array.Empty<(string, string)>();
        }
    }

    /// <param name="fault">
    /// Empty when the platform's symbol list was enumerated. Otherwise it names what threw,
    /// and the returned array still carries the chart's own id.
    /// </param>
    private string[] TradedSymbolIds(out string fault)
    {
        fault = string.Empty;

        // THE CHART'S OWN ID IS NEVER AT RISK. It comes from configuration this process
        // already resolved, so it is added before anything belonging to the platform is
        // touched, and it survives whatever happens below.
        var ids = new List<string>();

        if (this.instrument.SymbolId is { Length: > 0 } own)
            ids.Add(own);

        try
        {
            // Core.Symbols IS A THROWING PROPERTY, and this is the measured reason this
            // method now has a try around it. Read from the installed assembly on
            // 2026-09-04, it is:
            //
            //     public Symbol[] Symbols => Connections.Connected
            //         .SelectMany(c => c.BusinessObjects.Symbols).ToArray();
            //
            // BusinessObjects is dereferenced per connected connection with no null guard,
            // so enumerating it can throw from inside the vendor's own getter for reasons
            // this process neither causes nor can prevent -- a connection that is listed as
            // connected while its business objects are still being built, for one.
            //
            // WHAT MAKES THAT WORTH CATCHING HERE rather than one level up: this call only
            // WIDENS the id list. The chart's own id is already in hand, so a failure costs
            // some of the answer and none of it is needed to ask the question. Until now it
            // took the entire day's seed with it and the operator was told trade history was
            // unavailable -- about a call that had not been made yet.
            foreach (var symbol in Qt.Core.Instance.Symbols ?? Array.Empty<Qt.Symbol>())
            {
                if (symbol?.Id is not { Length: > 0 } id || !this.IsThisChart(symbol))
                    continue;

                if (!ids.Contains(id, StringComparer.Ordinal))
                    ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException
                                      or NotSupportedException)
        {
            fault = OrbCore.Diagnostics.FaultDescription.Of(ex);
        }

        return ids.ToArray();
    }


    /// <summary>Describes a platform symbol in the terms the matcher judges.</summary>
    private static SymbolIdentity IdentityOf(Qt.Symbol? symbol)
        => symbol is null
            ? default
            : new SymbolIdentity(symbol.Id, symbol.Root, symbol.Name, symbol.ConnectionId);

    /// <summary>
    /// Whether a platform symbol is the one this chart is judging.
    ///
    /// ONE PREDICATE, THREE CALLERS — the history request, the position list and the live fill
    /// handler. It was written inline three times and all three were wrong in the same way on
    /// 2026-09-03; sharing it means a future correction cannot reach two of them and miss the
    /// third.
    /// </summary>
    private bool IsThisChart(Qt.Symbol? symbol)
        => InstrumentMatch.Matches(this.instrument, this.ChartConnectionId, IdentityOf(symbol));
    private (double NetContracts, double AverageEntry, double? OpenPnl) ReadOpenPosition()
    {
        double netContracts = 0;
        double weightedEntry = 0;
        double openPnl = 0;
        var everyPositionPriced = true;

        foreach (var position in Qt.Core.Instance.Positions)
        {
            if (!this.IsThisChart(position.Symbol))
                continue;

            double signed = position.Side == Side.Buy
                ? position.Quantity : -position.Quantity;
            netContracts += signed;
            weightedEntry += signed * position.OpenPrice;

            if (position.NetPnL is { } pnl)
                openPnl += pnl.Value;
            else
                everyPositionPriced = false;
        }

        return (netContracts,
                netContracts == 0 ? 0 : weightedEntry / netContracts,
                everyPositionPriced ? openPnl : null);
    }
    /// <summary>
    /// Records what every connection is publishing about its accounts, once a minute.
    ///
    /// WAVE 0, AND IT DECIDES NOTHING. Before the chart's daily-loss line is rebuilt on
    /// Account.Balance, two things have to be MEASURED rather than assumed: whether that
    /// balance moves intraday on these connections, and whether it moves on realised
    /// trades only — `balance - start` double-counts if it already carries open profit.
    /// Neither is answerable by reading the vendor's documentation, which says only
    /// "Gets current balance of the account".
    ///
    /// The account's identity never reaches the file. On these brokers the account name is
    /// the holder's surname and the id is a live account number, and neither question above
    /// needs to know whose account it is.
    /// </summary>
    private void SampleConnectionProbe(DateTime nowUtc)
    {
        if (!this.ConnectionProbeDump)
            return;

        // Opened on first use rather than at attach, so enabling the input mid-session
        // starts recording without a reload. A status already set means the open was
        // tried and failed; retrying every fold would spin on an unwritable directory.
        if (this.probeWriter is null && this.probeStatus.Length == 0)
        {
            this.probeWriter = this.OpenResearchWriter(
                true, "orbix-connection-probe", "connection probe", out this.probeStatus);
        }

        var writer = this.probeWriter;

        if (writer is null || nowUtc - this.probeLastSampleUtc < ProbeSampleInterval)
            return;

        this.probeLastSampleUtc = nowUtc;

        try
        {
            foreach (var connection in Qt.Core.Instance.Connections.All)
            {
                var id = connection.Id ?? string.Empty;
                writer.WriteLine(OrbCore.Diagnostics.ConnectionProbe.ConnectionLine(
                    nowUtc, id, this.ConnectionName(id), connection.State.ToString(),
                    servesTickHistory: false, symbolsOffered: 0));
            }

            foreach (var account in Qt.Core.Instance.Accounts)
            {
                var fields = new List<OrbCore.Diagnostics.ProbeField>();

                foreach (var item in account.AdditionalInfo?.Items
                                     ?? Enumerable.Empty<Qt.AdditionalInfoItem>())
                {
                    fields.Add(new OrbCore.Diagnostics.ProbeField(
                        item.Id ?? string.Empty,
                        item.NameKey ?? string.Empty,
                        item.Value?.ToString() ?? string.Empty));
                }

                var sample = new OrbCore.Diagnostics.AccountProbe(
                    account.Id ?? string.Empty,
                    account.Name ?? string.Empty,
                    account.ConnectionId ?? string.Empty,
                    this.ConnectionName(account.ConnectionId ?? string.Empty),
                    account.Balance,
                    account.AccountCurrency?.Name ?? string.Empty,
                    fields);

                this.probeSamples.Add(sample);
                writer.WriteLine(
                    OrbCore.Diagnostics.ConnectionProbe.AccountLine(nowUtc, sample));
            }

            writer.Flush();

            // The answer, computed rather than left for someone to eyeball out of a file
            // of hundreds of lines. It is the whole point of the exercise.
            this.probeStatus = "connection probe: "
                + OrbCore.Diagnostics.ConnectionProbe.DescribeBalanceMovement(this.probeSamples);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                      or NullReferenceException or InvalidOperationException)
        {
            // Reported, never swallowed: a probe that quietly stops recording would be read
            // afterwards as "the balance never moved", which is the wrong answer to the
            // question it exists to ask.
            this.probeStatus = $"connection probe: sampling failed ({ex.GetType().Name})";
            this.probeWriter = null;
            writer.Dispose();
        }
    }

    /// <summary>
    /// Opens an append-mode NDJSON file for a research-only dump, or explains why not.
    ///
    /// SHARED BY EVERY RESEARCH DUMP, and extracted rather than copied when the second one
    /// arrived. The numbered-fallback rule below was paid for once and must not be
    /// re-derived per caller: a second copy would drift, and the failure it prevents is
    /// silent — a live-held zero-byte file beside an instance with nowhere to write.
    ///
    /// Numbered fallback, the TradeJournal pattern: two ORB-IX instances on one host share
    /// the per-root name, and the second open was refused by the first's share mode —
    /// MEASURED on ryzen-pc 2026-08-28. Each instance now claims its own file.
    ///
    /// Same failure posture as <see cref="WriteStartupLog"/>: an unwritable file is a
    /// reported reason, never a fault. The chart is unaffected either way.
    /// </summary>
    /// <param name="enabled">Whether the operator asked for this dump at all.</param>
    /// <param name="fileStem">File name stem, before the product root and any number.</param>
    /// <param name="label">How this dump names itself in the status line.</param>
    /// <param name="status">The sentence describing what happened. Always assigned.</param>
    /// <returns>The writer, or null when disabled or unwritable.</returns>
    private StreamWriter? OpenResearchWriter(
        bool enabled, string fileStem, string label, out string status)
    {
        status = string.Empty;

        if (!enabled)
            return null;

        var directory = this.ResolveOutputDirectory();

        if (directory is null)
        {
            status = $"{label}: no writable directory";
            return null;
        }

        Exception? lastRefusal = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var name = attempt == 1
                ? $"{fileStem}-{this.instrument.Root}.ndjson"
                : $"{fileStem}-{this.instrument.Root}-{attempt}.ndjson";
            var path = Path.Combine(directory, name);

            try
            {
                var writer = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
                status = attempt == 1 ? $"{label}: on" : $"{label}: on (file #{attempt})";
                return writer;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                   or NotSupportedException or ArgumentException)
            {
                lastRefusal = ex;
            }
        }

        status = PathDisplay.Redact(
            $"{label}: unwritable after 5 name attempts ({lastRefusal?.GetType().Name})");
        return null;
    }

    // ---- Wave 1 assembly (fold thread) -----------------------------------

    /// <summary>
    /// Keeps the anchored VWAP consistent with its input: on a choice or
    /// resolved-anchor change, re-anchors and replays the delta engine's
    /// held bars from that instant (close×volume, the stated seeding
    /// approximation), then live prints continue the series.
    /// </summary>
    private void EnsureAnchoredVwap()
    {
        var choice = this.VwapAnchored;
        var anchor = this.ResolveAnchorChoice(choice, this.VwapCustomAnchor);

        if (this.vwapAnchorState == (choice, anchor))
            return;

        this.vwapAnchorState = (choice, anchor);

        if (choice == VwapAnchorChoice.Off || anchor == default)
        {
            // A fresh unanchored engine draws nothing.
            this.vwapAnchored.Anchor(default);
            this.vwapAnchorState = (choice, anchor);
            return;
        }

        this.vwapAnchored.Anchor(anchor);
        if (this.deltaEngine is { } bars)
        {
            foreach (var bar in bars.Bars)
            {
                if (bar.OpenTimeUtc < anchor)
                    continue;
                this.vwapAnchored.Add(bar.Close, bar.Volume);
                this.vwapAnchored.SampleBar(bar.OpenTimeUtc);
            }
        }
    }

    /// <summary>
    /// Resolves an anchor input to an instant, shared by the anchored VWAP and
    /// the anchored profile so "last HH" means the same bar on both. Default
    /// means unresolved — the caller shows nothing rather than guessing.
    /// </summary>
    private DateTime ResolveAnchorChoice(VwapAnchorChoice choice, string customText)
    {
        DateTime anchor = default;

        switch (choice)
        {
            case VwapAnchorChoice.Off:
                break;
            case VwapAnchorChoice.OrbClose:
                foreach (var snapshot in this.drawable)
                {
                    if (snapshot.CloseUtc > anchor)
                        anchor = snapshot.CloseUtc;
                }

                break;
            case VwapAnchorChoice.LastHigherHigh:
            case VwapAnchorChoice.LastLowerLow:
                var wanted = choice == VwapAnchorChoice.LastHigherHigh
                    ? HhLlLabelKind.HigherHigh : HhLlLabelKind.LowerLow;
                var engine = this.hhllEngine;
                if (engine is not null)
                {
                    for (var i = engine.Labels.Count - 1; i >= 0; i--)
                    {
                        if (engine.Labels[i].Kind == wanted
                            && engine.Labels[i].Bar < this.hhllBarOpen.Count)
                        {
                            anchor = this.hhllBarOpen[engine.Labels[i].Bar];
                            break;
                        }
                    }
                }

                break;
            case VwapAnchorChoice.WeekOpen:
                // THE TRADING WEEK, NOT THE CALENDAR WEEK. The rule and its wall-clock handling
                // live on TradingWeek, where a test can call them; this converts through the
                // session clock the rest of the engine already runs on.
                if (this.clock is { } weekClock)
                {
                    anchor = weekClock.ToUtc(
                        weekClock.Week.MostRecentOpenLocal(weekClock.ToLocal(DateTime.UtcNow)));
                }

                break;
            case VwapAnchorChoice.LookBackHigh:
                if (this.config is { } lookBackConfig && this.EnsureFlowBars())
                {
                    var lookBack = Math.Max(lookBackConfig.Flow.VolumeProfile.LookBackBars, 2);

                    if (OrbCore.Flow.FibFan.Extremes(this.flowBarHigh, this.flowBarLow, lookBack)
                        is { } extremes
                        && extremes.HighBar >= 0
                        && extremes.HighBar < this.flowBarOpen.Count)
                    {
                        anchor = this.flowBarOpen[extremes.HighBar];
                    }
                }

                break;
            case VwapAnchorChoice.CustomTime:
                DateTime.TryParse(
                    customText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out anchor);
                break;
        }

        return anchor;
    }

    /// <summary>
    /// The always-on live accumulation the plan pins as the profiles'
    /// fallback source. Runs in the fold's tick drain; a row-size change
    /// resets it, which the fallback's source line states via the since-time.
    /// </summary>
    private void FeedProfileLiveTick(in TickEvent tick)
    {
        if (!this.FrvpEnabled && !this.AvpEnabled)
            return;
        // A profile is a histogram over the price grid — it converts nothing to money.
        if (!this.instrument.HasPriceScale)
            return;

        var rowTicks = Math.Max(1, this.ProfileRowTicks);
        if (this.profileLiveEngine is null || this.profileLiveRowTicks != rowTicks)
        {
            this.profileLiveEngine = new VolumeProfileEngine(this.instrument.TickSize, rowTicks);
            this.profileLiveRowTicks = rowTicks;
            this.profileLiveSinceUtc = tick.TimestampUtc;
        }

        var side = tick.Aggressor switch
        {
            Aggressor.Buy => PrintSide.Buy,
            Aggressor.Sell => PrintSide.Sell,
            _ => PrintSide.Unknown,
        };
        this.profileLiveEngine.Add(tick.Price, tick.Size, side);
    }

    /// <summary>
    /// Publishes the Wave-2 snapshot; runs inside the fold gate. Engine
    /// rebuilds happen only when an input, the resolved range, the click
    /// generation, or the closed-bar count changed — plus a five-second
    /// refresh for the anchored profile, whose forming bar keeps moving.
    /// </summary>
    private void PublishProfiles(DateTime nowUtc)
    {
        if (!this.FrvpEnabled && !this.AvpEnabled)
        {
            this.profileDrawable = ProfileDrawable.Empty;
            return;
        }

        // Same reason as the tick feed above: the ladder is priced in ticks, not dollars.
        if (!this.instrument.HasPriceScale)
            return;

        var rowTicks = Math.Max(1, this.ProfileRowTicks);
        var vaPercent = Math.Clamp(this.ProfileValueAreaPercent, 1, 100);

        // The committed click pair beats the time inputs while click-select
        // is in use; otherwise the typed times govern. Whichever produced
        // the range is named in the profile's source line.
        DateTime frvpStart = default, frvpEnd = default;
        int clickGeneration;
        lock (this.profileClickLock)
        {
            clickGeneration = this.profileClickGeneration;
            if (clickGeneration > 0)
                frvpStart = this.profileClickStart;
        }

        // §11: the precedence rule — clicks, then typed times, then the most recent
        // session open — and every way it can fail live in OrbIx.Core, where they are
        // provable without a platform. This supplies observations and nothing else.
        //
        // The session default is what stops an untouched chart reporting a failure it
        // was shipped with: FRVP is enabled by default, and before this it had no range
        // by default, so every fresh attach drew "no range" and always would have.
        var sessionOpens = this.ObserveSessionOpens(nowUtc, out var sessionAvailability);

        var frvpRange = FrvpRangeResolver.Resolve(
            frvpStart, this.FrvpStartUtc, this.FrvpEndUtc,
            sessionAvailability, sessionOpens, nowUtc);

        frvpStart = frvpRange.StartUtc;
        frvpEnd = frvpRange.EndUtc;

        // A range with no end runs to now, and the bar scan must be told so — comparing
        // against an unset end would exclude every bar and look exactly like the fault
        // this wave exists to diagnose.
        var frvpOpenEnded = frvpRange.EndUtc == default;

        var avpAnchorUtc = this.AvpEnabled
            ? this.ResolveAnchorChoice(this.AvpAnchor, this.AvpCustomAnchor)
            : default;

        var barCount = this.HistoricalData?.Count ?? 0;
        var signature = (this.FrvpEnabled, this.AvpEnabled, rowTicks, vaPercent,
                         Math.Clamp(this.ProfileWidthPercent, 1, 100),
                         frvpStart, frvpEnd, this.AvpAnchor, avpAnchorUtc,
                         barCount, clickGeneration, this.profileTickGeneration);
        var avpRefreshDue = this.AvpEnabled && avpAnchorUtc != default
            && nowUtc - this.profileOpenEndedRebuiltUtc >= TimeSpan.FromSeconds(5);

        if (signature != this.profileSignature || avpRefreshDue)
        {
            this.profileSignature = signature;
            this.profileTieSeen = false;

            this.frvpDraw = null;
            this.frvpResolution = ProfileResolution.Disabled("FRVP");
            if (this.FrvpEnabled)
            {
                if (!frvpRange.Resolved)
                {
                    this.frvpResolution = ProfileResolution.NoRange(
                        "FRVP", frvpRange.Failure, this.FrvpClickSelect);
                }
                else
                {
                    this.frvpDraw = this.BuildProfileFromChartBars(
                        "FRVP", frvpStart, frvpEnd, frvpOpenEnded, nowUtc,
                        rowTicks, vaPercent, DescribeRangeSource(frvpRange.Source),
                        out this.frvpResolution);
                }
            }

            this.avpDraw = null;
            this.avpResolution = ProfileResolution.Disabled("AVP");
            if (this.AvpEnabled && this.AvpAnchor != VwapAnchorChoice.Off)
            {
                if (avpAnchorUtc == default)
                {
                    this.avpResolution = ProfileResolution.AnchorUnresolved(
                        "AVP", this.AvpAnchor.ToString());
                }
                else
                {
                    this.avpDraw = this.BuildProfileFromChartBars(
                        "AVP", avpAnchorUtc, nowUtc, openEnded: true, nowUtc,
                        rowTicks, vaPercent, $"{this.AvpAnchor} anchor",
                        out this.avpResolution);
                }
            }

            this.profileOpenEndedRebuiltUtc = nowUtc;

            // WRITTEN ON EVERY CHANGE OF OUTCOME, not once per attach. The old
            // once-per-attach line recorded "AVP: anchor unresolved" and then went
            // silent — it never showed that the anchor resolved minutes later and the
            // rebuild failed again for an entirely different reason. The transition was
            // the whole diagnosis, and the log could not express it.
            var evidence =
                $"Wave2 profiles: {this.frvpResolution.LogText}; {this.avpResolution.LogText}";

            if (!string.Equals(evidence, this.profileEvidenceLogged, StringComparison.Ordinal))
            {
                this.profileEvidenceLogged = evidence;
                this.Report(evidence);
            }
        }

        var profiles = new List<ProfileDraw>(2);
        if (this.frvpDraw is { } frvp)
            profiles.Add(frvp);
        if (this.avpDraw is { } avp)
            profiles.Add(avp);

        // No status line of its own any more. Each profile already draws its own
        // coverage readout beside its title, the measured-null label is a constant that
        // now goes to the log, and anything actually wrong is collected by
        // PublishStatus into the one marked problems line.
        this.profileDrawable = new ProfileDrawable(profiles.ToArray());
    }

    /// <summary>
    /// The session opens the clock can see around an instant, and whether it could be
    /// asked at all.
    ///
    /// THE ROOT IS CHECKED HERE, NOT DISCOVERED AS AN EXCEPTION.
    /// <c>SessionClock.WindowsAround</c> refuses a blank product root by contract, and
    /// this runs inside the fold — so an unusable instrument would throw on a background
    /// thread rather than becoming a stated reason the operator can read.
    /// </summary>
    private IReadOnlyList<DateTime> ObserveSessionOpens(
        DateTime nowUtc, out SessionWindowAvailability availability)
    {
        if (this.clock is not { } sessionClock)
        {
            availability = SessionWindowAvailability.ClockNotBuilt;
            return Array.Empty<DateTime>();
        }

        var root = this.instrument.Root;

        if (string.IsNullOrWhiteSpace(root))
        {
            availability = SessionWindowAvailability.ProductRootUnknown;
            return Array.Empty<DateTime>();
        }

        availability = SessionWindowAvailability.Available;

        var windows = sessionClock.WindowsAround(nowUtc, root);
        var opens = new List<DateTime>(windows.Count);

        foreach (var window in windows)
            opens.Add(window.OpenUtc);

        return opens;
    }

    /// <summary>Names the input that supplied the fixed range, for the log.</summary>
    private static string DescribeRangeSource(FrvpRangeSource source) => source switch
    {
        FrvpRangeSource.Clicks => "clicked range",
        FrvpRangeSource.TypedTimes => "typed range",
        FrvpRangeSource.SessionDefault => "session default",
        _ => "no range",
    };

    /// <summary>
    /// Composes the one status block the chart and the log share, after every feature has
    /// published. Runs last in the fold, so it sees the final state of all of them.
    ///
    /// LABELS ARE CONSTANTS AND GO ONLY TO THE LOG. Every ORB-IX overlay is display-only,
    /// so the label says the same thing on every frame forever; on screen that is
    /// wallpaper, and wallpaper is what made the corner unreadable. Problems are rare by
    /// construction, so a line that appears at all is known to matter.
    /// </summary>
    private void PublishStatus()
    {
        var block = new StatusBlock();

        // NO RESEARCH COMMENTARY ON THIS BLOCK. Five standing labels opened it, naming trial
        // numbers and null results for delta, profiles, zones, the initial balance and VWAP.
        // They were constants -- identical on every fold, on every chart, forever -- and they
        // pushed the part a reader needs past the width of anything that shows a line. What a
        // measurement says about a feature belongs in that feature's documentation, where it
        // still is. Nothing about any FEATURE changed here: only these five strings went.

        // THE ABSORBED FLOW TOOLS SAY WHAT THEY ARE DOING. The line names which of the eleven
        // are drawing and, when the stacked-imbalance display is one of them, what its volume
        // floor resolved to — which bar period the number was measured on, and whether this
        // chart's period borrowed it from a neighbouring one. A reader seeing more or fewer
        // marks than they expected can settle why from the log rather than from the source.
        //
        // A LABEL, because a healthy chart says the same thing on every frame. The one case
        // that reaches the CHART is a tool switched on that cannot draw: a measured floor
        // asked for on a chart with no bar period has no number, and silence there would read
        // as a quiet market rather than as a display that never ran.
        if (this.FlowState is { } flow)
        {
            block.AddLabel(flow.Describe());

            // WHAT THE SWITCHES SAY IS NOT WHAT THE CHART SHOWS. The line above reports which
            // tools are ON; this one reports what they FOUND. Without it a display that is off,
            // one that is on and empty, and one that is broken all read identically in the log —
            // which is exactly how a deploy that drew nothing survived a review.
            block.AddLabel(this.flowFrame.Describe());

            // THE FLIP ENGINE SAYS WHAT IT IS DOING. It said nothing at all until now: a direct
            // check of the operator's log on 2026-09-14 found ZERO mentions of it, so "no flip
            // line on the chart" and "the engine never ran" read identically and the question
            // could only be settled from a screenshot. The state it separates that nothing else
            // can show is ARMING — a crossing that has happened and is waiting out its
            // confirmation distance, which may yet retrace and leave no level by design.
            block.AddLabel(this.deltaFlips.Read.Describe());

            // THE SIGN UNDER EVERYTHING ELSE ON THIS LINE. Reported beside the flow tools
            // because it decides whether any of them mean what they say.
            block.AddLabel(this.aggressorConvention.Describe());

            if (this.flowPeriodProblem.Length != 0)
                block.AddProblem(this.flowPeriodProblem);

            foreach (var problem in flow.Problems)
                block.AddProblem(problem);
        }

        // Research only, and only while it is switched on. A LABEL rather than a Problem:
        // the probe working as intended is not a fault, and its finding is what the
        // operator asked the question to get.
        if (this.ConnectionProbeDump && this.probeStatus.Length != 0)
            block.AddLabel(this.probeStatus);

        // The currency lines are the one thing start-up no longer waits for, so they are the
        // one thing that can be legitimately missing from a chart that is otherwise complete.
        // Said out loud, because an unexplained gap reads as a fault.
        var pendingCost = PendingCostNotice.ChartText(
            this.instrument.IsUsable, this.RiskLinesEnabled, this.CostMeterEnabled);

        if (pendingCost.Length != 0)
            block.AddProblem(pendingCost);

        if (this.frvpResolution.IsProblem)
            block.AddProblem(this.frvpResolution.ChartText);

        if (this.avpResolution.IsProblem)
            block.AddProblem(this.avpResolution.ChartText);

        // A LABEL SINCE THE CONVENTION WAS RATIFIED (operator, 2026-08-28). It was a
        // problem while the tie rule was an open question; now that it is settled, a tie
        // firing is information rather than something needing attention.
        //
        // The flag itself is now accurate — it reports only a genuine tie on the chosen
        // POC — but a settled convention doing its job is not a fault, so this belongs in
        // the log rather than on the problems line.
        if (this.profileTieSeen)
            block.AddLabel("POC tie decided by the ratified convention this build");

        // The capability report is evidence, so it always reaches the log. It reaches the
        // CHART only when it explains a live failure — a profile that found bars and none
        // of them carrying levels. Naming a platform gate on a chart whose profile drew
        // perfectly well would be noise of exactly the kind this wave removed.
        if (this.level2Rules is { } level2)
            block.AddLabel(level2.LogText());

        if (this.volumeAnalysisCapability is { } capability)
        {
            block.AddLabel(capability.LogText());

            var levelsMissing =
                this.frvpResolution.Outcome == ProfileOutcome.BarsCarryNoPriceLevels
                || this.avpResolution.Outcome == ProfileOutcome.BarsCarryNoPriceLevels;

            if (levelsMissing)
            {
                // ExplainMissingLevels, NOT ChartText. This site already knows the levels
                // are absent, so the question is "why", and an empty gate verdict is not an
                // answer to it — the platform's licence check precedes every gate we can
                // observe and its refusal produces exactly this symptom. The wording lives
                // on the capability so the chart and the log cannot drift apart.
                block.AddProblem($"volume analysis: {capability.ExplainMissingLevels()}");
            }
        }

        this.statusChartText = block.ChartText();

        var log = block.LogText();

        if (!string.Equals(log, this.statusLogged, StringComparison.Ordinal))
        {
            this.statusLogged = log;
            this.Report(log);
        }
    }

    /// <summary>
    /// Feeds one profile engine from the chart bars' per-price volume-analysis
    /// levels and computes it. A bar belongs to the range when its OPEN time is
    /// inside — the same bar-granular convention the VWAP session replay uses.
    /// Per-bar level dictionaries are staged into a local list before any
    /// engine add, so a platform-thread mutation mid-enumeration costs that
    /// bar this rebuild (counted, stated) rather than a half-counted bar.
    ///
    /// When no bar in range carries levels, the anchored profile falls back to
    /// the live-tick accumulation — but only when the accumulation started at
    /// or before the anchor, because it cannot un-count ticks from before it;
    /// each impossibility is its own stated failure, never an empty profile.
    /// </summary>
    private ProfileDraw? BuildProfileFromChartBars(
        string title, DateTime startUtc, DateTime endUtc, bool openEnded,
        DateTime nowUtc, int rowTicks, int vaPercent, string rangeSource,
        out ProfileResolution resolution)
    {
        var bars = this.HistoricalData;
        var engine = new VolumeProfileEngine(this.instrument.TickSize, rowTicks);
        var covered = 0;
        var withoutLevels = 0;
        var readRaces = 0;
        var staged = new List<(double Price, double Buy, double Sell, double Unclassified)>(64);

        if (bars is not null)
        {
            for (var i = 0; i < bars.Count; i++)
            {
                if (bars[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                    continue;
                if (bar.TimeLeft < startUtc || (!openEnded && bar.TimeLeft >= endUtc))
                    continue;

                var levels = bar.VolumeAnalysisData?.PriceLevels;
                if (levels is null || levels.Count == 0)
                {
                    withoutLevels++;
                    continue;
                }

                staged.Clear();
                try
                {
                    foreach (var level in levels)
                    {
                        var buy = level.Value.BuyVolume;
                        var sell = level.Value.SellVolume;
                        staged.Add((level.Key, buy, sell,
                                    Math.Max(0, level.Value.Volume - buy - sell)));
                    }
                }
                catch (InvalidOperationException)
                {
                    // The platform's calculation thread mutated the
                    // dictionary mid-read; nothing was added, the bar is
                    // counted, and the next rebuild sees it settled.
                    readRaces++;
                    continue;
                }

                foreach (var (price, buy, sell, unclassified) in staged)
                {
                    engine.Add(price, buy, PrintSide.Buy);
                    engine.Add(price, sell, PrintSide.Sell);
                    engine.Add(price, unclassified, PrintSide.Unknown);
                }

                covered++;
            }
        }

        // §11: the verdict is OrbIx.Core's, computed from counts this method observed.
        // The counts used to be discarded here, which is why "no per-price data" read
        // the same whether nothing had been examined or four thousand bars had been.
        var counts = new ProfileScanCounts(covered, withoutLevels, readRaces);
        var liveEngine = this.profileLiveEngine;
        var liveSince = liveEngine is null ? default : this.profileLiveSinceUtc;

        // The tick source is asked ONLY when the bars came back empty-handed, because slicing
        // it costs a pass over the cached minutes and the bars are already in hand when they
        // carry levels. Its answer must reach the verdict BEFORE the verdict is taken: the
        // outcome gates everything below, so a source discovered afterwards could never be used.
        var rangeEndUtc = openEnded ? nowUtc : endUtc;
        ProfileTickSource? tickSource = null;
        var tickSlice = default(ProfileTickSlice);

        if (counts.Covered == 0 && this.profileTickCache is { } tickCache)
            tickSource = tickCache.Describe(startUtc, rangeEndUtc, out tickSlice);

        resolution = ProfileResolution.FromScan(
            title, startUtc, rangeSource, counts, openEnded, liveSince, tickSource);

        if (resolution.Outcome != ProfileOutcome.Drawn)
            return null;

        // Which engine supplies it. The qualifying rule is asked of Core rather than
        // restated here: written twice, the two copies would eventually disagree and the
        // symptom would be a profile reported as drawn over an empty chart.
        // ORDERED AS Core RANKED THEM, and only ever one of them. Ticks first: they describe
        // the range that was asked for, where the live accumulation only ever holds what
        // arrived after this indicator attached.
        // Reached only when the bars carried nothing; when they did, the engine already holds
        // their levels and neither fallback is consulted.
        if (counts.Covered == 0)
        {
            if (tickSource is { Usable: true })
            {
                // Three adds per price, exactly as the bar path stages them, so the two sources
                // reach the engine through one code path and cannot drift apart.
                foreach (var level in tickSlice.Levels)
                {
                    engine.Add(level.Price, level.Buy, PrintSide.Buy);
                    engine.Add(level.Price, level.Sell, PrintSide.Sell);
                    engine.Add(level.Price, level.Unclassified, PrintSide.Unknown);
                }
            }
            else if (ProfileResolution.LiveFallbackQualifies(openEnded, liveSince, startUtc)
                     && liveEngine is not null)
            {
                engine = liveEngine;
            }
        }

        var source = resolution.SourceText;

        if (!engine.TryCompute(vaPercent, out var result, out var computeFailure)
            || result is null)
        {
            resolution = ProfileResolution.ComputeFailed(title, computeFailure);
            return null;
        }

        if (result.PocTieBroken)
            this.profileTieSeen = true;

        return new ProfileDraw(
            title, startUtc, openEnded ? nowUtc : endUtc, openEnded, result,
            rowTicks * this.instrument.TickSize, vaPercent, source);
    }

    /// <summary>
    /// FRVP click capture, on the platform's UI thread. Two left clicks on the
    /// main window commit a range (order-free); a second click on the same
    /// instant is ignored so a double-click cannot commit an empty range.
    ///
    /// MouseClick, NOT MouseDown — a RATIFIED DEVIATION from the approved plan
    /// (operator, 2026-08-28). The plan specified MouseDown; MouseDown also fires
    /// at the start of a PAN, so every chart drag would have committed a range
    /// nobody asked for, and the operator would have discovered it as a profile
    /// appearing over garbage bounds rather than as an error. MouseClick fires
    /// only on a completed press-and-release in place.
    ///
    /// The deviation was made during the build, disclosed at the time, and is
    /// recorded here rather than solely in a commit message so it is visible to
    /// anyone reading the handler.
    ///
    /// NOT YET EXERCISED ON A LIVE CHART: FRVP's range currently comes from the
    /// session default, so this path has never captured a real click. That is a
    /// gap in evidence, not a claim that it works.
    /// </summary>
    private void OnChartMouseClick(object? sender, Qt.Chart.ChartMouseNativeEventArgs e)
    {
        try
        {
            if (!this.FrvpClickSelect
                || e.Button != Qt.Native.NativeMouseButtons.Left)
            {
                return;
            }

            var window = this.mouseChart?.MainWindow;
            if (window is null || !ReferenceEquals(e.Window, window))
                return;

            var clicked = window.CoordinatesConverter.GetTime(e.X);
            if (clicked == default)
                return;

            lock (this.profileClickLock)
            {
                // ONE CLICK COMMITS. It used to take two, defining a closed
                // range — which no source on this connector can serve. The
                // click now anchors the profile at that bar and runs it to now,
                // so there is no pending state and no half-entered range to
                // draw or abandon.
                this.profileClickStart = clicked;
                this.profileClickGeneration++;
            }
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"FRVP click capture failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Publishes the Wave-1 snapshot; runs inside the fold gate.</summary>
    private void PublishWave1(DateTime nowUtc)
    {
        var bandKs = ParseBandKs(this.VwapBandKs);
        var sessionSamples = this.VwapEnabled
            ? ToArraySnapshot(this.vwapSession.Samples)
            : Array.Empty<VwapSample>();
        var anchoredSamples =
            this.VwapEnabled && this.VwapAnchored != VwapAnchorChoice.Off
                ? ToArraySnapshot(this.vwapAnchored.Samples)
                : Array.Empty<VwapSample>();

        var newsBands = Array.Empty<NewsBandDraw>();
        if (this.NewsShadingEnabled
            && this.newsCalendar is { Available: true } calendar)
        {
            var bands = new List<NewsBandDraw>();
            foreach (var economicEvent in calendar.Events)
            {
                if (!economicEvent.Tier1
                    || !economicEvent.AppliesTo(this.instrument.Root))
                {
                    continue;
                }

                bands.Add(new NewsBandDraw(
                    economicEvent.TimeUtc.AddMinutes(-this.NewsMinutesBefore),
                    economicEvent.TimeUtc.AddMinutes(this.NewsMinutesAfter),
                    OrbCore.Features.NewsBandLabel.Compose(
                        economicEvent,
                        this.clock?.SessionZone,
                        DateTime.UtcNow,
                        this.account?.NewsRule.ToString() ?? string.Empty)));
            }

            newsBands = bands.ToArray();
        }

        double? dllPrice = null;
        double? mllPrice = null;
        var riskDllTag = string.Empty;
        var riskMllTag = string.Empty;
        if (this.RiskLinesEnabled && this.instrument.IsUsable
            && this.accountParams is { Available: true } account)
        {
            var open = this.ReadOpenPosition();

            if (open.NetContracts != 0)
            {
                double perPoint = this.instrument.TickValue / this.instrument.TickSize;

                // THE DAILY LINE NOW MOVES WITH THE DAY. It used to be drawn from the full
                // configured limit under a printed caveat of "assumes $0 realized today" —
                // true at the open and progressively false after it, always in the direction
                // that draws the line FURTHER AWAY than where the day actually ends. That is
                // the number a trader leans on mid-position.
                //
                // The remaining allowance is the limit plus the day's realised result, signed:
                // a loss shrinks it, a profit enlarges it. THAT RULE IS NOT INVENTED HERE — it
                // is today_net = realised + unrealised with no floor at zero, which is what
                // the engine's own daily-loss guard applies and what the stack's account model
                // computes. An earlier draft assumed the opposite and was deleted for it.
                //
                // THE DAY'S REALISED FIGURE IS NO LONGER AVAILABLE, and the line says so
                // rather than implying it was counted. It came from the operator tape, which
                // was removed at the operator's request; a fabricated zero presented as a
                // measurement would be the one thing worse than the stated assumption.
                var dllRemaining = account.DailyLossLimitUsd;
                var dllNote = "assumes $0 realized today";

                // ASKED FOR SEPARATELY. A day's loss big enough to exhaust the daily
                // allowance makes the daily line unplaceable, which is correct — the limit is
                // already reached rather than lying ahead — but it says nothing about the
                // max-loss line, which is still placeable and matters more at that moment.
                dllPrice = RiskHorizon.Level(
                    open.AverageEntry, open.NetContracts, perPoint, dllRemaining);

                // NOT adjusted by the day's result. The max-loss limit TRAILS on end-of-day
                // balance and locks at the starting balance, and neither the peak it trails
                // from nor whether it has locked is available here. Applying the day's
                // realised figure would look like the same correction and would be arithmetic
                // on a basis nobody measured.
                mllPrice = RiskHorizon.Level(
                    open.AverageEntry, open.NetContracts, perPoint,
                    account.MaximumLossLimitUsd);

                var position = $"{Math.Abs(open.NetContracts):N0} @ {open.AverageEntry:N2}";
                riskDllTag = $"{position} — day ends here ({dllNote})";
                // The basis is the firm's own word for what this limit trails on, so the line
                // says WHICH thing is not modelled rather than leaving a reader to assume it
                // is the same kind of limit as the daily one.
                riskMllTag = $"{position} — configured limit, "
                    + $"{account.MaxLossBasis} not modelled";
            }
        }

        var costLines = Array.Empty<string>();
        if (this.CostMeterEnabled && this.instrument.IsUsable
            && double.IsFinite(this.lastBid) && double.IsFinite(this.lastAsk)
            && this.lastAsk > this.lastBid)
        {
            double spreadTicks = (this.lastAsk - this.lastBid) / this.instrument.TickSize;
            var lines = new List<string>
            {
                $"spread {spreadTicks:N1}t",
            };
            // Keyed by TIER ("MNQ"), never the family root ("NQ"): the fees
            // file carries one prop firm product codes, which are per-contract —
            // the micro and the mini differ ($0.72 vs $2.78 measured), so a
            // family-level lookup would be wrong even if it matched.
            // OBSERVED 2026-08-28 on ryzen: the root lookup missed and fell
            // into the file-unavailable message while the log said
            // "7 symbol(s) loaded" — the two cases are now told apart below.
            if (this.feesRuntime is { Available: true } fees)
            {
                if (!fees.Symbols.TryGetValue(this.instrument.Tier, out var economics))
                {
                    lines.Add($"no fee entry for {this.instrument.Tier}");
                }
                else
                {
                    if (economics.RoundTurnUsd is { } fee)
                    {
                        double feeTicks = fee / this.instrument.TickValue;
                        lines.Add($"cross rt ≈ {spreadTicks + feeTicks:N1}t "
                                  + $"(fee {feeTicks:N2}t measured)");
                    }
                    else
                    {
                        lines.Add("fee UNMEASURED for this product");
                    }

                    if (economics.OptimalRestSeconds is { } rest)
                        lines.Add($"measured rest optimum {rest:N0}s");
                }
            }
            else
            {
                // The file genuinely failed to load — its loader's own
                // status says which file and why, never a generic shrug.
                lines.Add(this.feesRuntime?.Status ?? "fees file unavailable");
            }

            costLines = lines.ToArray();
        }

        // READOUT ONLY — the measured-null label moved to the log. What remains says
        // which source seeded the history, which differs between attaches.
        var status = this.VwapEnabled
            ? "VWAP: history bars seeded close×volume"
            : string.Empty;

        // The pace line: always on when the layer is, no threshold, by the operator's own
        // choice. Empty on an untouched day, so a chart with nothing to say draws nothing.
        var operatorLines = Array.Empty<string>();

        this.wave1Drawable = new Wave1Drawable(
            sessionSamples, anchoredSamples, bandKs, newsBands,
            dllPrice, mllPrice, riskDllTag, riskMllTag, costLines,
            operatorLines, status);
    }

    private static VwapSample[] ToArraySnapshot(IReadOnlyList<VwapSample> samples)
    {
        var copy = new VwapSample[samples.Count];
        for (var i = 0; i < copy.Length; i++)
            copy[i] = samples[i];
        return copy;
    }

    /// <summary>
    /// "1,2" → [1.0, 2.0]. Unparseable or non-positive entries are
    /// dropped; an empty result means no bands, which is a legitimate
    /// setting rather than an error.
    /// </summary>
    private static double[] ParseBandKs(string input)
    {
        var ks = new List<double>();
        foreach (var part in (input ?? string.Empty).Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var k)
                && double.IsFinite(k) && k > 0)
            {
                ks.Add(k);
            }
        }

        return ks.ToArray();
    }

    /// <summary>One NDJSON line per closed bar per source, for the parity study.</summary>
    private void WriteParityLine(in DeltaBar bar, string source)
    {
        var writer = this.parityWriter;

        if (writer is null)
            return;

        try
        {
            // periodSeconds names the bar's own timeframe: the 2026-08-28
            // scoring round found a shared file holding 1m and 5m rows
            // with no way to tell them apart.
            var periodSeconds = (bar.CloseTimeUtc - bar.OpenTimeUtc).TotalSeconds;
            writer.WriteLine(
                "{\"barOpenUtc\":\"" + bar.OpenTimeUtc.ToString("o", CultureInfo.InvariantCulture)
                + "\",\"source\":\"" + source
                + "\",\"periodSeconds\":" + periodSeconds.ToString(CultureInfo.InvariantCulture)
                + ",\"delta\":" + bar.Delta.ToString(CultureInfo.InvariantCulture)
                + ",\"volume\":" + bar.Volume.ToString(CultureInfo.InvariantCulture)
                + ",\"buys\":" + bar.Buys.ToString(CultureInfo.InvariantCulture)
                + ",\"sells\":" + bar.Sells.ToString(CultureInfo.InvariantCulture)
                + ",\"unknowns\":" + bar.Unknowns.ToString(CultureInfo.InvariantCulture) + "}");
            writer.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            this.parityStatus = $"parity dump: write failed ({ex.GetType().Name})";
            this.parityWriter = null;
        }
    }

    /// <summary>
    /// Resolves engine bar indices to times and label anchors (plotshape
    /// location.abovebar hangs on the bar high, belowbar on the bar low) into
    /// an immutable snapshot the paint thread reads.
    /// </summary>
    /// <summary>
    /// Resolves the current retracement into something the paint pass can draw.
    /// </summary>
    /// <param name="bars">The chart series, for the bar still forming.</param>
    /// <param name="closed">How many bars have closed.</param>
    /// <remarks>
    /// THE FORMING BAR IS INCLUDED, and that is the whole point of the live anchor. The
    /// HH/LL engine is fed CLOSED bars only -- correctly, since a pivot cannot be judged
    /// from a bar still moving -- so its high/low lists stop one bar short. Passing only
    /// those would leave the grid a full bar behind price on every timeframe, which on a
    /// 5-minute chart is five minutes of being wrong.
    ///
    /// The forming bar is appended to COPIES. Mutating the engine's own lists would feed a
    /// moving bar back into structure that must only ever see closed ones.
    /// </remarks>
    private FibDrawable BuildFibDrawable(
        TradingPlatform.BusinessLayer.HistoricalData bars, int closed)
    {
        var engine = this.hhllEngine;

        if (!this.FibEnabled || engine is null || engine.Labels.Count == 0
            || this.hhllBarOpen.Count == 0)
        {
            return FibDrawable.Empty;
        }

        var highs = new List<double>(this.hhllBarHigh);
        var lows = new List<double>(this.hhllBarLow);

        if (this.FibAnchorMode == FibAnchorMode.LiveExtreme
            && this.hhllBarOpen.Count == closed
            && this.TryReadBar(bars, closed, out var forming))
        {
            highs.Add(forming.High);
            lows.Add(forming.Low);
        }

        var ratios = this.FibShowExtensions
            ? FibLevels.DefaultRatios.Concat(FibLevels.DefaultExtensions).ToList()
            : FibLevels.DefaultRatios;

        // THE TREND THE STRUCTURE ENGINE ALREADY REPORTS: 1 up, -1 down, 0 before the first
        // breakout. Passing it makes the grid anchor on the impulse instead of on whatever
        // leg happened last, which in a downtrend is the counter-trend bounce — measured on
        // MNQ 1m at 2026-09-04T20:59Z, where a plainly bearish structure produced a grid
        // measured off a rally. Zero is passed through as NO trend rather than as "up".
        var trendNow = engine.TrendSeries.Count > 0
            ? engine.TrendSeries[engine.TrendSeries.Count - 1]
            : 0d;

        var trend = FibLevels.TrendFrom(trendNow);

        if (FibLevels.Build(engine.Labels, highs, lows, ratios, this.FibAnchorMode,
                            this.FibShowGoldenPocket, trend) is not { } grid)
        {
            return FibDrawable.Empty;
        }

        var lines = new FibLineDraw[grid.Levels.Count];

        for (var i = 0; i < grid.Levels.Count; i++)
        {
            var level = grid.Levels[i];
            lines[i] = new FibLineDraw(
                level.Ratio, level.Price,
                level.Ratio is FibLevels.GoldenPocketLow or FibLevels.GoldenPocketHigh);
        }

        // The origin's bar index came from the engine, whose lists are the closed bars, so
        // it is always addressable here. Clamped anyway rather than trusted: an index that
        // walked off the end would throw inside a paint path.
        var originBar = Math.Clamp(grid.OriginBar, 0, this.hhllBarOpen.Count - 1);

        return new FibDrawable(
            lines,
            grid.GoldenPocket?.LowPrice ?? 0,
            grid.GoldenPocket?.HighPrice ?? 0,
            grid.GoldenPocket is not null,
            this.hhllBarOpen[originBar]);
    }

    private HhLlDrawable BuildHhLlDrawable()
    {
        var engine = this.hhllEngine;

        if (engine is null)
            return HhLlDrawable.Empty;

        var labels = new HhLlLabelDraw[engine.Labels.Count];

        for (var i = 0; i < labels.Length; i++)
        {
            var label = engine.Labels[i];
            var above = label.Kind is HhLlLabelKind.HigherHigh
                or HhLlLabelKind.LowerHigh;
            labels[i] = new HhLlLabelDraw(
                this.hhllBarOpen[label.Bar], label.Kind,
                above ? this.hhllBarHigh[label.Bar] : this.hhllBarLow[label.Bar],
                above);
        }

        var segments = new HhLlSegmentDraw[engine.Segments.Count];

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = engine.Segments[i];
            segments[i] = new HhLlSegmentDraw(
                this.hhllBarOpen[segment.StartBar],
                segment.EndBar is { } end ? this.hhllBarOpen[end] : null,
                segment.Price, segment.IsResistance);
        }

        return new HhLlDrawable(labels, segments);
    }

    /// <summary>
    /// One line saying what the overlay holds and, when it holds nothing, what is missing.
    ///
    /// Written because the previous round of this bug was diagnosed from a screenshot that
    /// could only say "nothing is drawn". Each clause names a specific, checkable cause.
    /// </summary>
    private string DescribeDrawState()
    {
        var held = this.drawable.Length;

        if (held > 0)
        {
            var seeded = 0;

            foreach (var range in this.drawable)
            {
                if (!range.FlowObserved)
                    seeded++;
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "draw {0} range(s), {1} seeded from bars, product {2}/{3}",
                held, seeded, this.instrument.Root, this.instrument.Tier);
        }

        if (!this.SeedFromHistory)
            return "draw 0 ranges: seeding from history is switched off in the inputs.";

        var bars = this.HistoricalData;
        var barCount = bars?.Count ?? 0;

        if (barCount == 0)
            return "draw 0 ranges: the chart has supplied no bars yet.";

        if (this.clock is null)
            return "draw 0 ranges: no session clock was built.";

        var windows = this.clock.WindowsBetween(
            DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, this.instrument.Root);

        return string.Format(
            CultureInfo.InvariantCulture,
            "draw 0 ranges: {0} bars loaded, {1} session window(s) in the last 7 days for {2}, "
            + "none produced a closed range yet",
            barCount, windows.Count, this.instrument.Root);
    }

    /// <summary>
    /// Builds ranges for sessions that closed before this indicator loaded, from the chart's
    /// own bars.
    ///
    /// Without this the overlay draws nothing until the next session opens, which on a chart
    /// showing three days of history is almost all of it. The live path is unaffected: a
    /// session whose opening range is still forming is left to the tick-fed builder, and when
    /// that closes it replaces whatever was seeded.
    ///
    /// Runs on the fold thread, not the paint thread, and only when the bar count has changed
    /// — the chart's history grows when the user scrolls back, and re-seeding every 100ms
    /// would walk thousands of bars for nothing.
    /// </summary>
    private void SeedClosedSessionsFromBars(DateTime nowUtc)
    {
        if (!this.SeedFromHistory || this.config is null || this.clock is null || this.ranges is null)
            return;

        var bars = this.HistoricalData;

        if (bars is null || bars.Count == 0)
            return;

        var barCount = bars.Count;

        if (barCount == this.lastSeedBarCount)
            return;

        if (!this.TryReadBar(bars, 0, out var firstBar)
            || !this.TryReadBar(bars, barCount - 1, out var lastBar))
        {
            return;
        }

        if (!this.config.Symbols.TryGetValue(this.instrument.Root, out var symbolConfig))
            return;

        var subBar = OrbCore.Duration.TryParse(symbolConfig.EntryTf, out var entry)
            ? entry
            : TimeSpan.FromSeconds(15);

        var windows = this.clock.WindowsBetween(firstBar.OpenUtc, lastBar.CloseUtc, this.instrument.Root);

        foreach (var candidate in windows)
        {
            // A session whose opening range has not finished belongs to the live builder.
            var orEnd = candidate.Definition.OrKind == OrLengthKind.Fixed
                ? candidate.OpenUtc + candidate.Definition.OrLength
                : candidate.OrHardCloseUtc;

            if (orEnd > nowUtc)
                continue;

            if (this.ranges.Contains(candidate.Definition.Name, candidate.OpenUtc))
                continue;

            var builder = new OrBuilder(
                candidate, this.instrument, this.config.Or,
                this.averageDailyRange, medianOpenVolume: 0d, subBar);

            var fed = 0;

            for (var i = 0; i < bars.Count; i++)
            {
                if (!this.TryReadBar(bars, i, out var bar))
                    continue;

                if (bar.OpenUtc >= candidate.OrHardCloseUtc)
                    break;

                if (bar.OpenUtc < candidate.OpenUtc)
                    continue;

                if (builder.SeedFromBar(bar.OpenUtc, bar.CloseUtc, bar.High, bar.Low, bar.Close, bar.Volume))
                    fed++;
            }

            if (fed == 0)
                continue;

            var seededRange = builder.CloseSeeded();

            if (seededRange is null)
                continue;

            this.ranges.Add(seededRange);
            this.levels?.AddFrom(seededRange);
            this.seededSessions++;
        }

        // Recorded only after the pass completes. If reading the chart's history threw part
        // way through — the platform's threading contract for HistoricalData is not stated in
        // the shipped documentation, so concurrent growth during a read is UNVERIFIED — the
        // count stays stale and the next fold simply tries again.
        this.lastSeedBarCount = barCount;

        this.RefreshOvernightLevels(nowUtc, bars, barCount);
    }

    /// <summary>
    /// Rebuilds the overnight high and low from the chart's own bars.
    ///
    /// THESE WERE CONFIGURED AND NEVER BUILT. LevelKind.OvernightHigh/Low existed, the config
    /// weighted them at 0.85, ChartOverlay had a colour for them, and no code anywhere
    /// constructed one — so the configuration described a level the engine did not compute.
    ///
    /// Rebuilt rather than added once, because the chart's history grows: a chart opened
    /// mid-session may hold only part of the overnight window, and more of it arrives later.
    /// Replacing on each pass keeps the level equal to the best view of the window currently
    /// held, instead of freezing whatever was visible on the first fold.
    ///
    /// It runs on the fold, alongside the seeding it follows, so it is already inside the fold
    /// gate and off both the paint and market-data threads.
    /// </summary>
    private void RefreshOvernightLevels(DateTime nowUtc, HistoricalData bars, int barCount)
    {
        if (this.config is null || this.clock is null || this.levels is null)
            return;

        // The session DATE in the session's own zone, not in UTC. At 02:00 UTC the New York
        // date is still the previous day, and taking the UTC date would ask for tomorrow's
        // overnight window and find nothing in it.
        var localDate = DateOnly.FromDateTime(this.clock.ToLocal(nowUtc));

        if (!OvernightRange.TryWindow(
                this.clock, this.config.Levels.Overnight, localDate, this.instrument.Root,
                out var fromUtc, out var toUtc))
        {
            return;
        }

        var window = new List<BarEvent>();

        for (var i = 0; i < barCount; i++)
        {
            if (!this.TryReadBar(bars, i, out var bar))
                continue;

            if (bar.OpenUtc < fromUtc || bar.OpenUtc >= toUtc)
                continue;

            window.Add(new BarEvent(
                bar.OpenUtc, bar.CloseUtc,
                open: bar.Close, high: bar.High, low: bar.Low, close: bar.Close,
                volume: bar.Volume, delta: 0d, isClosed: true));
        }

        var overnight = OvernightRange.FromBars(window, fromUtc, toUtc, establishedUtc: toUtc);

        if (overnight.Count == 0)
        {
            // CLEARED, not left alone. Today's window produced nothing — no bars in it yet, or a
            // gap in history — and yesterday's overnight high would otherwise stay on the chart
            // wearing today's meaning. Add cannot express this, because there is no new level to
            // replace it with.
            this.levels.RemoveKinds(LevelKind.OvernightHigh, LevelKind.OvernightLow);
            return;
        }

        // Add replaces by kind, so the previous answer goes as the new one lands.
        foreach (var level in overnight)
            this.levels.Add(level);
    }

    /// <summary>One chart bar, in the engine's own terms.</summary>
    private readonly record struct Bar(
        DateTime OpenUtc, DateTime CloseUtc, double High, double Low, double Close, double Volume);

    /// <summary>
    /// Reads one bar, oldest first.
    ///
    /// <see cref="SeekOriginHistory.Begin"/> is stated rather than defaulted: the indexer's
    /// default origin is End, where index 0 is the newest bar and the sequence runs backwards.
    /// A seeding loop that ran backwards without knowing it would attribute every bar to the
    /// wrong session.
    /// </summary>
    private bool TryReadBar(HistoricalData bars, int index, out Bar bar)
    {
        bar = default;

        if (index < 0 || index >= bars.Count)
            return false;

        if (bars[index, SeekOriginHistory.Begin] is not HistoryItemBar item)
            return false;

        bar = new Bar(item.TimeLeft, item.TimeRight, item.High, item.Low, item.Close, item.Volume);
        return true;
    }

    /// <summary>
    /// Rolls to the session that is current, resetting per-session state at the boundary.
    /// </summary>
    private void EnsureSession(DateTime nowUtc)
    {
        if (this.config is null || this.clock is null)
            return;

        var active = this.clock.ActiveWindow(nowUtc, this.instrument.Root);

        if (active is null)
        {
            this.window = null;
            this.context = null;
            this.rangeBuilder = null;
            return;
        }

        if (this.window is not null && this.window.OpenUtc == active.OpenUtc
            && this.window.Definition.Name == active.Definition.Name)
        {
            return;
        }

        this.window = active;

        if (!this.config.Symbols.TryGetValue(this.instrument.Root, out var symbolConfig))
            return;

        this.context = new SessionContext(active, this.instrument, symbolConfig);
        this.context.SetTiers(this.ObserveTiers());

        // THE LEVEL GRAPH IS DELIBERATELY NOT RESET HERE.
        //
        // It used to be, and that quietly erased every reference level at every session
        // boundary — including the first fold, before anything could use one. ReferenceLevels
        // is called once at start-up, so prior-day and prior-week levels were cleared and never
        // re-added: they have never been drawn and NudgeClear has never seen one.
        //
        // LevelGraph states the contract itself: "Levels outlive session phases by design; that
        // is what makes them references." The three modules below genuinely need clearing —
        // they accumulate volume, spread samples and an armed break, all of which belong to one
        // session. Levels were swept in with them by pattern.
        //
        // Nothing goes stale as a result: Add REPLACES BY KIND, so a new session's range
        // overwrites the previous one's opening-range levels when it closes, and the age
        // half-life handles relevance in between.
        this.footprint?.Reset();
        this.quality?.Reset();
        this.retest?.Reset();

        // A DETECTOR PER WINDOW, because a break is a break of THIS session's range. Carrying
        // one across a boundary would let the previous session's levels arm this session's
        // retest. What counts as a break is configuration and is not guessed.
        this.breaker = this.config.Playbooks.TryGetValue("P1", out var p1)
                       && p1.BreakBufferTicks is { } buffer
            ? new BreakDetector(this.instrument, buffer)
            : null;

        if (this.breaker is null)
        {
            this.Report(
                "P1's breakBufferTicks is not configured, so what counts as a break is "
                + "undefined. No break is detected and no setup is evaluated this session; "
                + "ranges and levels still draw.");
        }

        this.drawableSetup = null;
        this.drawableSignalLabel = string.Empty;
        this.drawableBlockReason = string.Empty;

        // The sub-bar length is the timeframe entries are actually taken on, so the range
        // reports the noise scale of the decision being made rather than of some other one.
        var subBar = OrbCore.Duration.TryParse(symbolConfig.EntryTf, out var entry)
            ? entry
            : TimeSpan.FromSeconds(15);

        // Average daily range comes from the daily history loaded at init. It is zero when
        // that load produced nothing, and zero means "not known" — OrBuilder then reports the
        // range as ungradeable rather than grading it against a number nobody measured.
        //
        // Median opening volume is still zero, so the adaptive range closes on its time cap
        // rather than on participation. That is a real limitation, stated rather than hidden.
        this.rangeBuilder = new OrBuilder(
            active, this.instrument, this.config.Or,
            this.averageDailyRange, medianOpenVolume: 0d, subBar);

        // AND A BUILDER FOR EVERY CONTEXT WINDOW THAT IS NOT THE ACTIVE ONE.
        //
        // ActiveWindow returns ContainingWindows(...).FirstOrDefault(), and that ordering puts
        // EntriesAllowed first — so at 09:30 the regular session wins and the initial balance
        // is never the active window. Its range therefore never formed live: it appeared only
        // after a later reload seeded it from bars, which is why today's IB was missing from
        // the chart while every previous day's was there.
        //
        // ContainingWindows exists for exactly this, and says so: "the initial balance still
        // supplies its extension targets while the regular session is the active one".
        this.contextBuilders.Clear();

        foreach (var other in this.clock.ContainingWindows(nowUtc, this.instrument.Root))
        {
            if (other.OpenUtc == active.OpenUtc && other.Definition.Name == active.Definition.Name)
                continue;

            this.contextBuilders.Add(
                other,
                new OrBuilder(
                    other, this.instrument, this.config.Or,
                    this.averageDailyRange, medianOpenVolume: 0d, subBar));
        }
    }

    /// <summary>
    /// Closes any context range whose window has finished forming, and stores it.
    ///
    /// Kept apart from <see cref="AdvancePhase"/> because a context window has no session
    /// context and takes no entries — it contributes a drawn range and its levels, nothing
    /// more.
    /// </summary>
    private void AdvanceContextRanges(DateTime nowUtc)
    {
        if (this.contextBuilders.Count == 0)
            return;

        foreach (var (window, builder) in this.contextBuilders)
        {
            if (builder.IsClosed)
                continue;

            if (builder.TryClose(nowUtc) is not { } closed)
                continue;

            this.levels?.AddFrom(closed);
            this.ranges?.Add(closed);
        }
    }

    /// <summary>
    /// A bar has closed. Mirrors SessionReplay.OnBar exactly.
    ///
    /// A BREAK CONFIRMS HERE AND NOWHERE ELSE, and a setup is evaluated here and nowhere else,
    /// because that is where the replay does both. Evaluating more often on the chart than in
    /// the study would make the chart a different system from the one that was measured.
    /// </summary>
    private void OnBarClosed(in BarEvent bar)
    {
        if (this.engine is null)
            return;

        if (this.context is not { } context)
        {
            // No session. The modules are still driven so their state does not develop a hole
            // where a session boundary fell, but there is no range to measure a retest against
            // and no context to evaluate.
            this.engine.OnBar(bar, breaker: null, rangeWidthPrice: 0d);
            return;
        }

        this.engine.OnBar(bar, this.breaker, context.Range is { } range ? range.Width : 0d);

        // BEFORE the breaker guard, deliberately. Placed after it, a null breaker
        // would silently suppress every direction row while the panel carried on
        // rendering -- the dataset would have holes exactly where the engine was
        // least ready, and nothing would say so.
        this.JournalDirection(context, bar.CloseTimeUtc);

        if (this.breaker is null)
            return;

        this.EvaluateSetup(context, bar.CloseTimeUtc);
    }

    /// <summary>
    /// Writes the direction read for a bar that just closed.
    /// </summary>
    /// <remarks>
    /// ONE ROW PER CLOSED BAR. That is the cadence that makes the read a dataset:
    /// the rate the chart itself moves at, and the rate at which "what did price do
    /// next" can be joined to it later.
    ///
    /// A KNOWN AND STATED LIMIT: this sits inside the branch that requires a session
    /// context, so bars that close OUTSIDE any configured session are not recorded.
    /// The journal's rows are keyed by session and symbol, and inventing a session
    /// name for an overnight bar would put a fabricated key into the dataset. The
    /// consequence is that the record covers configured sessions only, which is
    /// written here rather than discovered later by someone counting rows.
    /// </remarks>
    private void JournalDirection(SessionContext context, DateTime atUtc)
    {
        if (this.journal is null || this.direction is not { } current)
            return;

        double tick = this.Symbol?.TickSize ?? double.NaN;
        double vwap = this.vwapSession.TryCurrent(out double v, out _) ? v : double.NaN;

        this.journal.RecordDirection(
            context, atUtc, current.Read(this.lastPrice, vwap, tick));
    }

    /// <summary>
    /// Asks the shared evaluator what, if anything, this bar's close proposes.
    ///
    /// IT DECIDES AND IT DOES NOT ACT. Nothing here routes an order, records an entry against
    /// the session budget, or moves a stop — the indicator draws, and that is the whole of its
    /// job. What it produces is published for the paint thread and written to the journal so
    /// the same session replayed offline can be checked against it.
    ///
    /// SCORING IS OFF, and passing an empty set is the decision rather than an oversight.
    /// Book and Positioning have no module at all and MicroQuality is a gate that scores zero
    /// by design, so at most 56 of 100 points could ever be earned and the scorer would
    /// silently redistribute the rest. A grade computed that way is the most persuasive number
    /// on the chart and the least supported. <see cref="ScoreCoverage"/> states the absence
    /// instead.
    /// </summary>
    private void EvaluateSetup(SessionContext context, DateTime atUtc)
    {
        if (this.evaluator is null || this.retest is null || this.levels is null
            || this.quality is null || this.footprint is null || context.Range is null)
        {
            return;
        }

        // The replay returns here too, so the decision matches. The REASON is kept for display
        // only: on a chart "no entry is possible and here is why" is the useful state, and
        // showing it changes nothing the evaluator would have decided.
        if (!context.CanEnter(atUtc, out var blocked))
        {
            this.drawableSetup = null;
            this.drawableSignalLabel = string.Empty;
            this.drawableBlockReason = blocked;
            return;
        }

        this.drawableBlockReason = string.Empty;

        var verdict = this.quality.Evaluate(atUtc, this.instrument.Root, autoMode: false);

        var inputs = new PlaybookInputs(
            atUtc,
            this.lastPrice,
            this.footprint.Read(),
            this.retest.Read(),
            this.levels,
            verdict,
            this.breaker is { } detector ? detector.State : BreakState.None,
            context.Range.AtrTicks,
            verdict.MedianSpreadTicks,
            this.absorption?.Read(atUtc));

        var evaluation = this.evaluator.Evaluate(context, inputs, NoScoreEntries);

        this.drawableSetup = evaluation;

        // Keyed on the CONTRACT root, not the family. The study measured MNQ and NQ separately
        // and they are different sample sizes, so keying on the family would print one
        // product's evidence under the other's name.
        this.drawableSignalLabel = evaluation.Playbook is { } playbook
            ? SignalProvenance.Describe(
                playbook.Id,
                this.instrument.Tier,
                this.config?.Playbooks.TryGetValue(playbook.Id, out var pc) == true
                    ? pc.Provenance
                    : null)
            : string.Empty;

        // WITHOUT THIS THE WIRING IS UNFALSIFIABLE. The same session driven through the replay
        // must reach the same decision, and only a record of what the chart decided makes that
        // checkable after the fact.
        // COLLAPSED, NOT DROPPED. This row's purpose is stated above and it still holds: only
        // a record of what the chart decided makes the replay comparison checkable. What it
        // did NOT need was to say the same thing on every fold forever. Measured on the live
        // host 2026-09-03: 8,970 of 8,972 rows read "no plan", burying the 415 that mattered.
        // A row is now written when the decision changes, or once per heartbeat, carrying how
        // many evaluations it stands for.
        var decision = evaluation.Plan is { } plan ? plan.Summarise() : "no plan";

        // Captured for the sizing line, which answers "how many contracts does THIS stop
        // permit" -- a question that only exists while a plan does.
        this.lastPlanRiskTicks = evaluation.Plan is { } sized ? sized.RiskTicks : null;
        var emission = this.engineStateThrottle.Observe(decision, evaluation.Vetoes, atUtc);

        if (emission.ShouldRecord)
        {
            this.journal?.RecordEngineState(
                context, atUtc, decision, evaluation.Vetoes,
                emission.EvaluationsSincePrevious);
        }

        // AND THE SAME DECISION IN A SHAPE THAT CAN BE QUERIED. The line above records it as
        // free text — "no plan", or a one-line summary — which is enough to check the replay
        // reached the same verdict and useless for asking anything about the verdicts
        // themselves. Measured on the live host: 8,972 of those rows, of which 8,970 said "no
        // plan" and 2 carried a setup, with 413 carrying vetoes reachable only by parsing.
        //
        // These rows add nothing on a quiet bar: a refusal with no vetoes writes nothing, so
        // the 95% of folds where neither a plan nor a veto exists stay silent.
        if (evaluation.Plan is { } proposed)
        {
            this.journal?.RecordChartSignal(
                context, proposed, evaluation.Vetoes, atUtc,
                evaluation.Imbalance, evaluation.Absorption);
        }
        else
        {
            this.journal?.RecordChartRejection(
                context, evaluation.Vetoes, atUtc,
                evaluation.Imbalance, evaluation.Absorption);
        }
    }

    /// <summary>
    /// The empty score set, allocated once.
    ///
    /// Named rather than written as <c>Array.Empty&lt;ScoreEntry&gt;()</c> at each call site so
    /// that "the chart registers no module into the score" is a single stated fact with one
    /// place to change when a Book or Positioning module finally exists.
    /// </summary>
    private static readonly ScoreEntry[] NoScoreEntries = Array.Empty<ScoreEntry>();

    /// <summary>
    /// The once-per-fold part: what data tiers are observable, and adopting a seeded range.
    ///
    /// SPLIT FROM <see cref="AdvanceRange"/> BECAUSE THE TWO HAVE DIFFERENT CLOCKS. A range
    /// closes on the tick that closes it, so that part runs per tick inside the drain. Which
    /// tiers are live is a property of the connection rather than of any one print, and
    /// ObserveTiers allocates a footprint reading — running it per tick would put an
    /// allocation on every print for an answer that cannot change that fast.
    ///
    /// It still calls <see cref="AdvanceRange"/> itself, because a session with no prints must
    /// still close its range and advance its phase. Doing so is idempotent: TryClose returns
    /// early once closed, and the phase is a setter.
    /// </summary>
    private void AdvancePhase(DateTime nowUtc)
    {
        if (this.context is null || this.window is null || this.rangeBuilder is null)
            return;

        this.context.SetTiers(this.ObserveTiers());

        this.AdvanceRange(nowUtc);

        if (this.rangeBuilder.IsClosed || this.context.Range is not null || this.ranges is null)
            return;

        // The live builder saw no prints — the indicator was added after this session's
        // range had already formed. A range seeded from bars for this exact occurrence is
        // the same session, so the panel adopts it rather than reporting "OR —" while the
        // chart plainly shows that session's range drawn beside it. The two disagreeing
        // about one session is worse than either being incomplete.
        var seeded = this.ranges.Get(this.window.Definition.Name, this.window.OpenUtc);

        if (seeded is not null)
            this.context.SetRange(seeded);
    }

    /// <summary>
    /// The per-tick part: the session's phase, and closing the opening range.
    ///
    /// Mirrors SessionReplay.AdvancePhase, which the replay calls on every print. The break
    /// detector is handed the closed range HERE and nowhere else — a detector still holding
    /// the previous session's levels would arm this session's retest against them.
    /// </summary>
    private void AdvanceRange(DateTime atUtc)
    {
        if (this.context is null || this.window is null || this.rangeBuilder is null)
            return;

        this.context.SetPhase(this.window.PhaseAt(atUtc));

        if (this.rangeBuilder.IsClosed || this.rangeBuilder.TryClose(atUtc) is not { } closed)
            return;

        this.context.SetRange(closed);
        this.levels?.AddFrom(closed);
        this.breaker?.SetRange(closed);
        this.journal?.RecordRange(this.context, closed);

        // Replaces any range seeded from bars for this same session occurrence: a measured
        // range always supersedes an inferred one.
        this.ranges?.Add(closed);
    }

    /// <summary>
    /// Reports which tiers are actually delivering, from what has been observed rather than
    /// from what the connection claims.
    ///
    /// The order-level tier stays dark here. The capability probe established that the
    /// one data vendor feed populates per-order identifiers and queue priority, but this indicator
    /// has no module consuming them yet, and reporting a tier as live when nothing reads it
    /// would inflate every score through the scorer's redistribution.
    /// </summary>
    private DataTierAvailability ObserveTiers()
    {
        var symbol = this.subscribed;

        var bars = this.HistoricalData is { Count: > 0 };
        var quotes = symbol is not null && symbol.Bid > 0 && symbol.Ask > 0;
        var classified = this.footprint is not null && this.footprint.Read().Classified > 0;
        var depth = this.bookRing.Count > 0 || this.bookRing.Dropped > 0;

        return new DataTierAvailability(bars, quotes, classified, depth, OrderLevel: false, Options: false);
    }

    // ---- construction --------------------------------------------------------------------

    private void BuildEngine(OrbIxConfig loaded, SymbolConfig symbolConfig)
    {
        this.clock = new SessionClock(loaded);
        // CONSTRUCTED INTO LOCALS, then assigned. The fields are nullable because they do not
        // exist before an engine is built, and EngineFold takes its modules non-null on
        // purpose. Reading them back off the fields would need a null-forgiving operator to
        // compile — asserting what the code already knows instead of carrying it.
        var builtLevels = new LevelGraph(loaded.Levels, symbolConfig, this.instrument);
        var builtFootprint = new FootprintEngine(
            this.instrument, divergenceWindow: 5,
            imbalanceRatio: loaded.Imbalance.Ratio,
            imbalanceMinVolume: loaded.Imbalance.MinVolume,
            footprintHistory: loaded.Imbalance.FootprintHistory);
        var builtRetest = new RetestEngine(loaded.Retest, this.instrument);
        var builtAbsorption = new AbsorptionEngine(
            this.instrument,
            TimeSpan.FromSeconds(loaded.Absorption.WindowSeconds),
            loaded.Absorption.CancelledBelow,
            loaded.Absorption.AbsorbedAtOrAbove,
            loaded.Absorption.MaxPricesPerSide);

        this.levels = builtLevels;
        this.footprint = builtFootprint;
        this.retest = builtRetest;
        this.absorption = builtAbsorption;
        this.evaluator = new SetupEvaluator(loaded, symbolConfig);

        // NOT GUESSED WHEN IT IS MISSING. The bar timeframe is the one entries are decided on,
        // and defaulting it would mean the chart evaluated setups on a timeframe nobody chose
        // while the replay used the configured one — the two would disagree and neither would
        // say why.
        var builtBars = new BarAggregator(
            OrbCore.Duration.TryParse(symbolConfig.EntryTf, out var entryTf)
                ? entryTf
                : throw new OrbIxConfigException(new[]
                {
                    $"entryTf '{symbolConfig.EntryTf}' for {this.instrument.Root} is not a "
                    + "duration, and the bar timeframe cannot be guessed.",
                }));

        this.bars = builtBars;

        // Stated once at start-up rather than recomputed per fold: the registered set does not
        // change while the engine runs, so neither does the answer.
        this.drawableScoreCoverage =
            ScoreCoverage.Describe(NoScoreEntries, loaded.Scoring.Weights);


        this.ranges = new SessionRangeStore(loaded.Levels.MaxRetainedSessions);
        // Which windows are CONTEXT rather than tradeable comes from configuration, so a
        // renamed or added window keeps the right treatment instead of being matched against
        // the literal "IB".
        this.overlay = new ChartOverlay(
            ChartTheme.ResolveMonoFamily(),
            this.clock.Definitions.Where(d => !d.EntriesAllowed).Select(d => d.Name));
        this.drawable = Array.Empty<OrSnapshot>();
        this.seededSessions = 0;
        this.lastSeedBarCount = -1;

        this.Report($"Configuration loaded from {this.configOrigin}.");

        // Daily history once, on the init thread: it is a blocking request and must never
        // happen on the paint path or the market-data path.
        if (this.subscribedForHistory is not null)
        {
            // TIMED because this is one of the two blocking history calls, and the loader
            // itself is silent. Before this, the only evidence of how long it took was the
            // gap between two timestamps — which is unreadable once two instances interleave.
            var dailyClock = Stopwatch.StartNew();

            var history = QuantowerHistoryLoader.LoadAverageDailyRange(
                this.subscribedForHistory, loaded.Or.AdrPeriod, DateTime.UtcNow);

            dailyClock.Stop();

            this.averageDailyRange = history.Adr;

            // Appended to the report this site already makes, so a load gains no new line.
            this.Report(LoadTiming.Took(history.Status, dailyClock.Elapsed));

            // The same bars, used a second time. The average-daily-range calculation reads the
            // high and the low and discards the rest, so prior-day high, low and close and the
            // prior week's extremes were one field away from being drawn all along.
            //
            // With no history there are simply no such levels — never a zero, which on a chart
            // is a line at the bottom of the axis that looks like a real price.
            foreach (var level in ReferenceLevels.FromDailyBars(
                         history.Bars, DateOnly.FromDateTime(DateTime.UtcNow)))
            {
                this.levels?.Add(level);
            }
        }

        var calendarPath = this.ResolveBesideConfig(loaded.Data.Calendar.Path);
        // The impact mapping comes from configuration, so a feed that supplies no rating of
        // its own is classified by a rule the operator can read and change.
        var calendar = FileEconomicCalendar.Load(
            calendarPath, DateTime.UtcNow, loaded.Data.Calendar);

        if (!calendar.Available)
            this.Report(calendar.Status);

        // Captured for the Wave-1 news shading: the overlay renders the
        // same events the blackout policy enforces, never a second feed.
        this.newsCalendar = calendar;

        // Wave-1 runtime params, pushed by the deploy script beside the
        // config; absent files are stated statuses, and the features that
        // need them show nothing rather than a guessed number.
        this.accountParams = AccountRuntimeParams.Load(
            this.ResolveBesideConfig("account-params.json"));
        this.feesRuntime = FeesRuntime.Load(
            this.ResolveBesideConfig("fees.json"));
        this.Report($"Wave1 params: {this.accountParams.Status}; {this.feesRuntime.Status}");

        this.account = ResolveAccount(loaded, this.AccountId);

        if (this.account is null)
        {
            this.Report(
                $"No configured account matches '{this.AccountId}'. Configured accounts: "
                + string.Join(", ", loaded.Accounts.Select(a => $"{a.Id} ({a.NewsRule})")) + ".");
            return;
        }

        this.Report($"Account {this.account.Id}: news rule {this.account.NewsRule}.");

        var builtQuality = new MicroQuality(
            loaded.MicroQuality, symbolConfig, this.instrument,
            new BlackoutPolicy(loaded.Data.Calendar, calendar, this.account.NewsRule));

        this.quality = builtQuality;

        // BUILT HERE BECAUSE THIS IS WHERE THE LAST OF ITS MODULES EXISTS. EngineFold takes
        // them non-null and keeps them, which is what makes the ordering a property of the
        // type rather than of whichever call site remembered to null-check.
        this.engine = new EngineFold(
            builtLevels, builtFootprint, builtQuality, builtRetest, builtAbsorption, builtBars);

        // TELEMETRY IS AN OUTPUT, NOT A PRECONDITION, and everything below this line reflects
        // that. On 2026-08-21 a second chart on the same product found the journal file held by
        // the first, the IOException left OnInit, and the indicator drew NOTHING AT ALL —
        // because a log file was locked. A chart that will not draw for that reason is the
        // wrong trade every time, so from here a failure is reported and the engine runs on.
        var outputDirectory = this.ResolveOutputDirectory();

        if (outputDirectory is null)
        {
            this.Report(
                "No writable directory could be found for the journal or the recording, so "
                + "neither is being written. Set the configuration path input to a writable "
                + "folder. The chart is unaffected.");

            return;
        }

        this.journal = TradeJournal.TryOpenFile(
            outputDirectory, $"orbix-journal-{this.instrument.Root}.ndjson", out var journalReason);

        if (this.journal is null)
            this.Report($"No trade journal is being written: {journalReason}. The chart is unaffected.");

        if (loaded.Recorder.Enabled)
        {
            this.recorder = StreamRecorder.TryOpenFiles(
                loaded.Recorder,
                string.IsNullOrWhiteSpace(loaded.Recorder.Directory)
                    ? Path.Combine(outputDirectory, "recordings")
                    : loaded.Recorder.Directory,
                this.instrument.SymbolId,
                DateOnly.FromDateTime(DateTime.UtcNow),
                out var recorderReason);

            if (this.recorder is null)
                this.Report($"No stream recording is being written: {recorderReason}. The chart is unaffected.");
        }

    }

    /// <summary>
    /// Reads contract specifications from the platform.
    ///
    /// Never assumed and never compiled in: a tick value baked into source is wrong the
    /// moment an exchange lists a new contract size, and this system is expected to pick up
    /// a newly listed tier the day it starts trading.
    /// </summary>
    /// <summary>
    /// Records a startup problem where the operator can find it.
    ///
    /// THIS USED TO GO TO THE PANEL, AND THE PANEL IS GONE. Left as it was, every one of these
    /// — a configuration that will not load, a contract root belonging to no product, an
    /// account id matching nothing — would be appended to a list nothing reads and lost. A
    /// display can be removed; the diagnosis cannot.
    ///
    /// It goes to orbix-startup.log beside the user configuration, which already existed for
    /// fatal startup faults and already redacts user-specific path prefixes — a panel gets
    /// screenshotted and a Windows path under a user profile carries the account name, so the
    /// redaction sits at the one boundary every message crosses.
    /// </summary>
    /// <summary>
    /// One line into the startup log, tagged with the instance that wrote it.
    ///
    /// Tagged HERE because this is the single funnel every diagnostic passes through, so all
    /// 31 call sites gain the tag without one of them changing — and none can be forgotten.
    /// </summary>
    /// <summary>
    /// Pulls the whole book from the platform and installs it, instead of accumulating the
    /// Level 2 event stream.
    ///
    /// WHY THE EVENT STREAM IS NOT USED FOR THIS. On the operator's connection every Level 2
    /// update arrives stamped "generated_from_level1" — the connector FABRICATING a book from
    /// the touch because it has nothing else to publish on that stream. Measured 2026-09-14:
    /// 19,762 such updates, so the DOM display drew nothing and had no depth to draw.
    ///
    /// The subscription was never the fault. NewLevel2 += does call SubscribeAction(Level2) —
    /// read in the decompiled assembly, Symbol line 1823. The platform ALSO exposes a pull,
    /// DepthOfMarket.GetDepthOfMarketAggregatedCollections, and on that same chart in that same
    /// minute it returned 50 prices a side spanning 25 points, and 679 per-order levels with
    /// GetMBOItems set. The depth was there; the stream was not carrying it.
    ///
    /// THAT MEASUREMENT ALSO REFUTES THE APPROVED PLAN, which put per-order DOM out of scope
    /// because "largestOrder needs an MBO subscription … a separate conversation". It needs no
    /// new subscription; it needs this call.
    ///
    /// THE EVENT STREAM IS LEFT ALONE FOR EVERYONE ELSE. bookRing still feeds the modules that
    /// read it; only the flow ladder switches source, and DepthLadder refuses to mix the two.
    ///
    /// NOT EVERY FOLD. The fold timer runs at 20ms; the cadence is configuration, and the cost
    /// of the call is measured and reported rather than assumed to be small.
    /// </summary>
    private void PullBook(DateTime nowUtc, OrbCore.Config.FlowConfig flow)
    {
        var cadence = flow.DomLevels.SnapshotMs;

        // THE CADENCE DECISION IS CORE'S, NOT THIS FILE'S. It was a subtraction here, in a
        // project no test can load; BookPullClock is the same decision where it can be called.
        this.bookPullClock ??= new OrbCore.Flow.BookPullClock(cadence);

        if (this.subscribed is not { } symbol || !this.bookPullClock.ShouldPull(nowUtc))
            return;

        try
        {
            var market = symbol.DepthOfMarket;

            if (market is null)
            {
                this.ReportBookPullOnce("the symbol exposes no DepthOfMarket");
                return;
            }

            var started = Stopwatch.GetTimestamp();

            var book = market.GetDepthOfMarketAggregatedCollections(
                new GetDepthOfMarketParameters
                {
                    GetLevel2ItemsParameters = new GetLevel2ItemsParameters
                    {
                        // 0 means "the whole book" to the display; the platform wants a count,
                        // and its own DOM panel default is the one used when nothing is asked for.
                        LevelsCount = flow.DomLevels.WithinLevels > 0 ? flow.DomLevels.WithinLevels : 50,
                        GetMBOItems = flow.DomLevels.LargestOrder,
                    },
                });

            if (book is null)
            {
                this.ReportBookPullOnce("the depth-of-market call returned null");
                return;
            }

            var snapshot = new OrbCore.Flow.DepthSnapshot(
                nowUtc, Convert(book.Bids), Convert(book.Asks));

            this.flowBuilder?.OnBookSnapshot(snapshot);

            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            this.bookPullMsTotal += elapsedMs;
            this.bookPullCount++;

            // ONCE A MINUTE, AND CARRYING THE COST. A pull on the fold thread that turned out to
            // be expensive would show up as a chart that stutters and nothing that says why.
            if (nowUtc - this.bookPullReportUtc >= TimeSpan.FromMinutes(1))
            {
                this.bookPullReportUtc = nowUtc;

                this.Report(string.Create(
                    CultureInfo.InvariantCulture,
                    $"book pull: {snapshot.Bids.Length}+{snapshot.Asks.Length} price(s) every "
                    + $"{cadence}ms, mbo={flow.DomLevels.LargestOrder}, "
                    + $"{this.bookPullMsTotal / Math.Max(1, this.bookPullCount):N2}ms mean over "
                    + $"{this.bookPullCount:N0} pull(s)"));

                this.bookPullMsTotal = 0d;
                this.bookPullCount = 0;
            }
        }
        catch (Exception error)
        {
            // REPORTED, NEVER SWALLOWED. A pull that fails silently reads as a feed with no
            // depth, which is the very thing this was built to tell apart.
            this.ReportBookPullOnce($"the call threw ({error.GetType().Name}: {error.Message})");
        }

        static OrbCore.Flow.DepthSnapshotLevel[] Convert(Level2Item[]? items)
        {
            if (items is null || items.Length == 0)
                return Array.Empty<OrbCore.Flow.DepthSnapshotLevel>();

            var result = new OrbCore.Flow.DepthSnapshotLevel[items.Length];

            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var detail = item.DetailedLevels;
                var orders = Array.Empty<OrbCore.Flow.DepthSnapshotOrder>();

                if (detail is { Length: > 0 })
                {
                    orders = new OrbCore.Flow.DepthSnapshotOrder[detail.Length];

                    for (var j = 0; j < detail.Length; j++)
                    {
                        orders[j] = new OrbCore.Flow.DepthSnapshotOrder(
                            detail[j].Id ?? string.Empty, detail[j].Size, detail[j].Priority);
                    }
                }

                result[i] = new OrbCore.Flow.DepthSnapshotLevel(
                    item.Price, item.Size, item.NumberOrders, orders);
            }

            return result;
        }
    }

    /// <summary>
    /// Reports a book-pull fault the FIRST time it is seen and then stays quiet.
    ///
    /// The pull runs four times a second; a fault that logged every time would bury the rest of
    /// the file within minutes, and a fault that logged once and changed would be invisible. The
    /// text itself is the key, so a different fault still speaks.
    /// </summary>
    private void ReportBookPullOnce(string reason)
    {
        if (string.Equals(this.bookPullFault, reason, StringComparison.Ordinal))
            return;

        this.bookPullFault = reason;
        this.Report($"book pull: {reason}");
    }

    private void Report(string message)
        => WriteStartupLog(LoadTiming.Prefix(this.instanceTag, message));

    private void ReportAll(IEnumerable<string> messages)
    {
        foreach (var message in messages)
            this.Report(message);
    }

    /// <summary>
    /// Reads the contract specifications the platform supplies.
    /// </summary>
    /// <param name="symbol">The traded symbol.</param>
    /// <param name="familyRoot">
    /// The configured product family, e.g. <c>NQ</c>. Drives configuration and session
    /// selection, since a session restricted to <c>["GC"]</c> must apply to MGC too.
    /// </param>
    /// <param name="contractRoot">
    /// The contract's own root, e.g. <c>MNQ</c>. Drives per-tier lookups such as trail
    /// distances, which are keyed by tier with a fall-back to the family.
    /// </param>
    /// <returns>
    /// The specifications, and the one diagnostic worth writing about how they were read — or
    /// null when there is nothing to say.
    ///
    /// IT NO LONGER WRITES THE MESSAGE ITSELF. This method has no idea whether it is being
    /// called for the first time or the hundred-and-thirtieth, and when it logged directly the
    /// retry loop turned "Tick cost could not be read" into 130 identical lines in one session.
    /// Initialise knows the attempt number, so Initialise decides.
    /// </returns>
    private InstrumentReading ReadInstrument(Qt.Symbol symbol, string familyRoot, string contractRoot)
    {
        // GetTickCost needs a reference price, and on a closed or quiet market Last and Ask
        // are both zero — at which point the cost reads zero, the instrument is judged
        // unusable, and initialisation returns before building anything. That is the state
        // with the MOST history to draw, so every real price the platform can offer is tried
        // in turn. Each candidate is an observed price; none is invented.
        var tickCost = 0d;
        var reference = 0d;

        // CAPTURED AS THEY ARE TRIED, not re-read afterwards. The old diagnostic read
        // symbol.Last/Ask/Bid AGAIN when composing its message, so a quote arriving between
        // the loop and the string made it report values no candidate was ever tested
        // against — a diagnostic lying about its own evidence.
        var last = symbol.Last;
        var ask = symbol.Ask;
        var bid = symbol.Bid;

        var fallback = BarFallbackOutcome.NoSeries;
        var barCount = 0;
        var barClose = 0d;

        foreach (var candidate in new[] { last, ask, bid })
        {
            // NOT `candidate <= 0`, which is FALSE for NaN and therefore admits it. OBSERVED
            // 2026-08-22 on a live MNQ chart with the market closed: symbol.Last read NaN, so
            // NaN reached GetTickCost below and only the catch stopped it — an exception doing
            // a guard's job. The rule is in Core so the offline suite asserts it.
            if (!PriceValue.IsUsable(candidate))
                continue;

            if (this.TryPriceTick(symbol, candidate, out var liveCost))
            {
                tickCost = liveCost;
                reference = candidate;
                fallback = BarFallbackOutcome.NotNeeded;
                break;
            }
        }

        // THE FOURTH CANDIDATE, whose outcome used to be reported nowhere. The last bar's
        // close is the one price that survives a closed market, and on a cold start it is
        // also the one that has not arrived yet — states this now tells apart.
        if (tickCost <= 0)
        {
            var bars = this.HistoricalData;

            if (bars is null)
            {
                fallback = BarFallbackOutcome.NoSeries;
            }
            else if (bars.Count == 0)
            {
                fallback = BarFallbackOutcome.NoBars;
            }
            else
            {
                barCount = bars.Count;

                if (!this.TryReadBar(bars, bars.Count - 1, out var lastBar))
                {
                    fallback = BarFallbackOutcome.BarUnreadable;
                }
                else
                {
                    barClose = lastBar.Close;

                    if (!PriceValue.IsUsable(barClose))
                    {
                        fallback = BarFallbackOutcome.CloseNotUsable;
                    }
                    else if (this.TryPriceTick(symbol, barClose, out var barCost))
                    {
                        tickCost = barCost;
                        reference = barClose;
                        fallback = BarFallbackOutcome.NotNeeded;
                    }
                    else
                    {
                        fallback = BarFallbackOutcome.CloseRejected;
                    }
                }
            }
        }

        string? diagnostic = null;

        if (tickCost <= 0)
        {
            var outcome = new ReferencePriceOutcome(
                last, ask, bid, fallback, barCount, barClose);

            diagnostic =
                $"Tick cost could not be read for {symbol.Name} from any available price "
                + $"({outcome.Describe()}). Contract "
                + "specifications come from the platform and are never assumed.";
        }
        else if (reference != symbol.Last)
        {
            diagnostic =
                $"Tick cost {tickCost} priced from {reference} rather than the last trade, "
                + $"which read {symbol.Last}.";
        }

        // Tier is the contract root, not the contract NAME. Trail distances are looked up by
        // tier and fall back to the family; "MNQU6" matches neither, so passing the name here
        // would make every trail lookup fail.
        return new InstrumentReading(
            new InstrumentSpec(
                symbol.Id ?? string.Empty, familyRoot, contractRoot, symbol.TickSize, tickCost),
            diagnostic);
    }

    /// <summary>What the platform's specifications read, and anything notable about reading them.</summary>
    private readonly record struct InstrumentReading(InstrumentSpec Spec, string? Diagnostic);

    /// <summary>
    /// Picks the configured account whose rules apply.
    ///
    /// Blank selects the first configured account, which is the ordinary case. A named account
    /// that does not exist returns null rather than falling back — running one account's
    /// obligations against another is exactly the mistake this selection exists to prevent,
    /// and it would be invisible.
    /// </summary>
    private static AccountConfig? ResolveAccount(OrbIxConfig loaded, string requested)
    {
        if (loaded.Accounts.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(requested))
            return loaded.Accounts[0];

        return loaded.Accounts.FirstOrDefault(
            a => string.Equals(a.Id, requested.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asks the platform to price one tick from a candidate price.
    ///
    /// EXTRACTED so the live-quote candidates and the last-bar fallback ask the same way.
    /// They used to share one loop; separating them was what made the fallback's outcome
    /// reportable, and duplicating this guard across both would have been the cost of that.
    ///
    /// The caller has already applied <see cref="PriceValue.IsUsable"/>. The catch remains
    /// because GetTickCost may still refuse a perfectly real price — which is a FINDING, not
    /// an error, and the caller records it as one.
    /// </summary>
    /// <summary>
    /// Fills in the tick cost after start-up went ahead without it.
    ///
    /// WHY THIS IS NOT THE RETRY TIMER'S JOB. That timer exists to re-run initialisation and
    /// disposes itself once initialisation succeeds. Since the price grid alone is now enough
    /// to succeed, the timer is gone long before a price arrives on a cold start — so the cost
    /// would stay 0 forever and the risk lines would never appear. This runs on the fold
    /// instead, which is already ticking.
    ///
    /// IT COSTS NOTHING WHILE IT CANNOT SUCCEED. Every candidate is screened by
    /// <see cref="PriceValue.IsUsable"/> before the platform is asked anything, and during the
    /// window this exists for, all of them are NaN. So the pending state makes no platform
    /// calls at all; the first real price both ends the wait and answers the question.
    ///
    /// Runs inside foldGate, so the write to <see cref="instrument"/> is ordered against every
    /// reader on the fold thread. The money-domain lines read the field directly, so updating
    /// it is the whole handover — no engine holds a stale copy, because the four that take an
    /// InstrumentSpec never read its cost.
    /// </summary>
    private void CompleteTickCostIfPending()
    {
        if (this.instrument.IsUsable || !this.instrument.HasPriceScale)
            return;

        if (this.Symbol is not { } symbol)
            return;

        if (!this.TryPriceTickFrom(symbol, symbol.Last, out var cost)
            && !this.TryPriceTickFrom(symbol, symbol.Ask, out cost)
            && !this.TryPriceTickFrom(symbol, symbol.Bid, out cost)
            && !this.TryPriceTickFrom(symbol, this.lastPrice, out cost))
        {
            return;
        }

        this.instrument = this.instrument with { TickValue = cost };

        this.Report(
            $"Tick cost resolved to {cost.ToString(CultureInfo.InvariantCulture)} after start-up "
            + "proceeded without it. Currency lines (risk horizon, fees) are live from here; "
            + "everything price-domain has been drawing since the tick size arrived.");
    }

    /// <summary>
    /// One candidate, screened then priced.
    ///
    /// The screen is <see cref="PriceValue.IsUsable"/> and not <c>candidate &gt; 0</c>, because
    /// every comparison against NaN is false and so <c>&lt;= 0</c> ADMITS it — the trap that
    /// doc records as having been found three times in this file already.
    /// </summary>
    private bool TryPriceTickFrom(Qt.Symbol symbol, double candidate, out double tickCost)
    {
        tickCost = 0d;
        return PriceValue.IsUsable(candidate) && this.TryPriceTick(symbol, candidate, out tickCost);
    }

    private bool TryPriceTick(Qt.Symbol symbol, double candidate, out double tickCost)
    {
        tickCost = 0d;

        try
        {
            var cost = symbol.GetTickCost(candidate);

            if (cost <= 0)
                return false;

            tickCost = cost;
            return true;
        }
        catch (ArgumentException)
        {
            // This particular price was not acceptable. Reported by the caller only when
            // every candidate has failed, where it is actionable.
            return false;
        }
    }

    /// <summary>
    /// Derives the product root from the platform's own field where it supplies one, falling
    /// back to the contract name with its month and year removed.
    ///
    /// The platform's root is preferred because it is authoritative; the fallback exists
    /// because it is not always populated, and a wrong root silently selects the wrong
    /// product configuration.
    /// </summary>
    /// <summary>
    /// The configured product root for the attached symbol.
    ///
    /// EVERYTHING THE PLATFORM SUPPLIES IS NORMALISED, including Symbol.Root, which used to be
    /// trusted verbatim. OBSERVED 2026-08-23 at the Sunday open: on the My Funded Futures
    /// connection Root returns "/MNQ:XCME" — a leading slash and an exchange — and the product
    /// lookup matches on "MNQ", so it matched nothing and the indicator went Fatal on twelve
    /// consecutive loads without ever subscribing. The rule lives in OrbIx.Core so the offline
    /// suite asserts it against the real configuration.
    /// </summary>
    private static string ResolveContractRoot(Qt.Symbol symbol, string overrideValue)
    {
        // Normalised too. Someone reading "/MNQ:XCME" off the chart and pasting it into the
        // override input has done a reasonable thing and should not be punished for it.
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return OrbCore.Abstractions.ContractRoot.Normalise(overrideValue);

        // Root is already a root, so only its decoration comes off — never an expiry it does
        // not carry.
        if (!string.IsNullOrWhiteSpace(symbol.Root))
            return OrbCore.Abstractions.ContractRoot.Normalise(symbol.Root);

        return OrbCore.Abstractions.ContractRoot.FromContractName(symbol.Name);
    }

    /// <summary>
    /// Loads the configuration, or reports every fault in it.
    ///
    /// The out parameter is annotated nullable rather than suppressed: on failure there
    /// genuinely is no configuration, and saying otherwise would hand the caller a
    /// reference the compiler believes is safe and the runtime knows is not.
    /// </summary>
    /// <summary>
    /// Resolves configuration, in a way that cannot fail because a file went missing.
    ///
    /// The order is: the explicit path in the inputs, then a file in the per-user data
    /// folder, then the copy embedded in this assembly. The embedded copy is why the first
    /// two may be absent without consequence.
    ///
    /// This replaced a resolution that looked beside the assembly, which cannot work here:
    /// Quantower loads scripts with <c>Assembly.Load(File.ReadAllBytes(path))</c>, so
    /// <see cref="Assembly.Location"/> is an empty string and the indicator has no way to
    /// find its own folder. The configuration shipped next to the DLL was therefore never
    /// readable, and the panel spent its life reporting a file-not-found.
    ///
    /// Whichever source wins is reported, because three possible sources with no statement
    /// of which one is live is a configuration system nobody can reason about.
    /// </summary>
    private bool TryLoadConfig([NotNullWhen(true)] out OrbIxConfig? loaded)
    {
        var explicitPath = this.ConfigPath;

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // An explicit path that does not load is an error, never a reason to fall back:
            // silently running the embedded defaults when the operator named a file would
            // trade a configuration they did not choose.
            return this.TryLoadFile(explicitPath, required: true, out loaded);
        }

        var userPath = UserConfigPath();

        if (File.Exists(userPath))
        {
            if (this.TryLoadFile(userPath, required: false, out loaded))
            {
                this.configOrigin = userPath;
                return true;
            }

            // The file exists but does not load — most often after an upgrade added a key an
            // older copy does not carry. Falling back keeps the indicator working, but the
            // operator is running settings they did not choose, so it is said outright rather
            // than left to be inferred from the problem list above.
            this.Report(
                $"THAT FILE IS BEING IGNORED. Running the embedded defaults instead. Fix the "
                + $"problems above in {userPath}, or delete it to have a current copy written.");
        }

        return this.TryLoadEmbedded(userPath, out loaded);
    }

    private bool TryLoadFile(string path, bool required, [NotNullWhen(true)] out OrbIxConfig? loaded)
    {
        try
        {
            loaded = OrbIxConfigLoader.LoadFile(path);
            this.configOrigin = path;
            return true;
        }
        catch (OrbIxConfigException ex)
        {
            // A configuration fault is reported in full on the panel rather than reduced to
            // "failed to load": the loader already knows every problem, and hiding them
            // turns one bad edit into a guessing game.
            this.Report($"Configuration at {path} is not usable:");
            this.ReportAll(ex.Problems);
            loaded = null;
            return false;
        }
        catch (Exception ex)
        {
            if (required)
                this.Report($"Configuration at {path} could not be read: {ex.GetType().Name}: {ex.Message}");

            loaded = null;
            return false;
        }
    }

    /// <summary>
    /// Loads the configuration compiled into this assembly, and writes a copy out for editing.
    ///
    /// The embedded document is the same file the repository ships and the tests validate, so
    /// this path cannot drift from the one on disk.
    /// </summary>
    private bool TryLoadEmbedded(string writeCopyTo, [NotNullWhen(true)] out OrbIxConfig? loaded)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(EmbeddedConfigName, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            this.Report(
                $"No configuration file was found and this build carries no embedded '{EmbeddedConfigName}'. "
                + "Set the configuration path input to a valid file.");
            loaded = null;
            return false;
        }

        using var stream = assembly.GetManifestResourceStream(name);

        if (stream is null)
        {
            this.Report($"The embedded resource '{name}' could not be opened.");
            loaded = null;
            return false;
        }

        try
        {
            loaded = OrbIxConfigLoader.LoadStream(stream, "the configuration embedded in this build");
            this.configOrigin = "embedded default";
        }
        catch (OrbIxConfigException ex)
        {
            this.ReportAll(ex.Problems);
            loaded = null;
            return false;
        }

        this.WriteEditableCopy(assembly, name, writeCopyTo);
        return true;
    }

    /// <summary>
    /// Writes the embedded default to disk so there is something to edit, without ever
    /// overwriting a file the operator may have already changed.
    /// </summary>
    private void WriteEditableCopy(Assembly assembly, string resourceName, string path)
    {
        if (File.Exists(path))
            return;

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);

            using var source = assembly.GetManifestResourceStream(resourceName);

            if (source is null)
                return;

            using var destination = File.Create(path);
            source.CopyTo(destination);

            this.Report(
                $"Running the embedded defaults. An editable copy has been written to {path}; "
                + "edit it and reload the indicator to use it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            // Not being able to write a convenience copy must not stop the indicator, which
            // is already running correctly on the embedded configuration. The reason is
            // still surfaced so it is not a silent failure.
            this.Report(
                $"Running the embedded defaults; an editable copy could not be written to {path} "
                + $"({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Where a user-editable configuration lives, per user, outside the install.</summary>
    private static string UserConfigPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbIx",
            EmbeddedConfigName);

    private string ResolveBesideConfig(string fileName)
        => Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(Path.GetDirectoryName(UserConfigPath()) ?? string.Empty, fileName);

    /// <summary>
    /// The first directory that can actually be written to, established by writing rather
    /// than by inspecting permissions.
    /// </summary>
    private string? ResolveOutputDirectory()
    {
        // Assembly.Location is deliberately not a candidate. Quantower loads scripts with
        // Assembly.Load(File.ReadAllBytes(path)), so it is an empty string here — this was
        // established by observing the resolved path the panel printed, not assumed.
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(this.ConfigPath)
                ? string.Empty
                : Path.GetDirectoryName(this.ConfigPath) ?? string.Empty,
            Path.GetDirectoryName(UserConfigPath()) ?? string.Empty,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                Directory.CreateDirectory(candidate);

                // A PROBE NAME UNIQUE TO THIS ATTEMPT. It used to be the constant
                // ".orbix-write-probe", shared by every instance in the process, and on
                // 2026-09-05 six charts initialised together on one host: one deleted the
                // probe while another was writing it, the write threw IOException, the
                // candidate was skipped, and that instance ended up with NO OUTPUT DIRECTORY
                // AT ALL -- so it wrote no journal, and its breaches were never recorded.
                // The directory was writable the whole time; the probe collided with itself.
                //
                // Guid rather than a counter or the instance id: it needs to be unique across
                // processes as well as within one, and both hosts run the platform while a
                // deploy script and a log reader touch the same folder.
                var probe = Path.Combine(
                    candidate,
                    $".orbix-write-probe-{Guid.NewGuid():N}");

                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or NotSupportedException or ArgumentException)
            {
                // A GENUINE refusal: this candidate cannot be written by THIS instance, for
                // a reason no longer confusable with another instance's probe.
                continue;
            }
        }

        return null;
    }

    // ---- painting ------------------------------------------------------------------------

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        base.OnPaintChart(args);

        var graphics = args?.Graphics;

        if (graphics is null)
            return;

        // The chart, and nothing else. There is no panel: the operator asked for it gone, and
        // every diagnostic that used to appear on it now goes to orbix-startup.log instead, so
        // removing the display did not remove the diagnosis.
        //
        // One collision registry per frame, shared by every overlay: HH/LL chips
        // reserve first, then the range/level/setup tags dodge them (and vice
        // versa next frame). HH/LL draws first so the opening-range geometry and
        // especially the setup's decision lines stay on top of it.
        var paintWindow = this.CurrentChart?.MainWindow;
        var registry = paintWindow is not null
            ? this.overlay?.BeginFrame(paintWindow.ClientRectangle)
              ?? new List<RectangleF>()
            : new List<RectangleF>();

        // The mouse hook attaches here because paint is the first moment the
        // chart object reliably exists (the symbol-list-empty-at-OnInit lesson,
        // applied to the chart); += is idempotent-guarded by the field.
        if (this.mouseChart is null && this.CurrentChart is { } chartForMouse)
        {
            this.mouseChart = chartForMouse;
            chartForMouse.MouseClick += this.OnChartMouseClick;

            // THE ACCOUNT HOOK RIDES THE SAME LATCH, for the same documented reason: paint
            // is the first moment the chart object reliably exists. IChart.AccountChanged is
            // documented in the shipped XML as occurring "when the account was changed".
        }

        // FIRST, so the problems line reserves against an empty registry and cannot be
        // silently skipped by a chip that got there before it. It reserves nothing when
        // there is no problem, leaving a healthy frame laid out exactly as before.
        this.DrawStatus(graphics, registry);

        // OUTSIDE THE FOCUS GATE, deliberately. Focus mode strips context; the transgression
        // block is not context, it is the breach surface -- the one thing that must survive
        // the moment a rule broke.

        // The direction panel, painted from the SAME shared overlay the standalone
        // Direction indicator uses. Outside the focus gate is wrong for it -- it is
        // context, not a breach -- so it draws only when focus is not engaged, and
        // that check happens below where `focus` is read.
        if (this.DirectionPanelOn)
        {
            this.directionOverlay.Draw(
                graphics, args!.Rectangle, this.directionPanel,
                this.DirectionPanelOffsetX, this.DirectionPanelOffsetY);
        }

        // FOCUS MODE STRIPS EVERYTHING BUT THE BREACH, THE TAPE AND THE LIMITS. The status
        // line above is the breach itself and always draws; DrawWave1Geometry keeps the risk
        // lines and the operator's own line and drops the rest. Everything skipped here is
        // context, and context is what competes for attention at the moment a rule broke.
        // FOCUS MODE IS GONE. It hid every context overlay while a rule breach was live,
        // and the rules it keyed on were removed at the operator's request. Nothing suppresses
        // these passes any more, so they run unconditionally rather than behind a flag that
        // could never become true.
        {
            this.DrawWave1News(graphics);
            this.DrawProfiles(graphics, registry);
            // BEFORE the HH/LL pass, so the swing chips and support/resistance lines
            // land on top of the grid rather than under it.
            this.DrawFib(graphics, registry);
            this.DrawHhLl(graphics, registry);
            this.DrawZones(graphics, registry);
            // BEFORE the ranges and the setup geometry: imbalance is tape context and must sit
            // under the decision lines, never over them.
            this.DrawImbalance(graphics, registry);
            this.DrawAbsorption(graphics, registry);
            this.DrawDeltaLevels(graphics, registry);

            // BEFORE the ranges and the setup geometry, with the rest of the tape context. The
            // absorbed displays describe what the tape did; the opening range and the setup's
            // lines are what a decision was made against, and a display drawn over them would
            // put the least load-bearing marks on top of the most.
            //
            // THE ORDER WITHIN THE GROUP IS BOTTOM-UP BY HOW MUCH SPACE EACH TAKES. The bias
            // geometry spans the whole pane, so it goes under everything; levels are horizontal
            // and long; markers are small and specific; the book is a right-edge strip; the
            // statistics band owns the bottom of the pane and reserves it. The counter is last of
            // all because it is the only mark that changes several times a second, and it takes
            // its corner rather than negotiating for it.
            this.DrawFlowBias(graphics, registry);
            this.DrawFlowLevels(graphics, registry);
            this.DrawFlowMarkers(graphics, registry);
            this.DrawFlowDom(graphics, registry);
            this.DrawFlowStatistics(graphics, registry);
            this.DrawFlowCounter(graphics, registry);

            this.DrawRanges(graphics);
        }

        this.DrawWave1Geometry(graphics, registry);

        this.DrawDelta(graphics, registry);
    }

    /// <summary>News shading paints FIRST — the lowest layer of the frame.</summary>
    private void DrawWave1News(Graphics graphics)
    {
        var window = this.CurrentChart?.MainWindow;
        var chart = this.CurrentChart;
        var drawable = this.wave1Drawable;

        if (window is null || chart is null || drawable.NewsBands.Length == 0)
            return;

        try
        {
            this.wave1Overlay.DrawNewsBands(graphics, window, drawable,
                new Wave1Overlay.Options(
                    this.VwapColor, this.VwapAnchoredColor, this.NewsColor,
                    this.RiskColor, chart.BarsWidth));
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The news shading failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The volume profiles — above the news shading, under the HH/LL chips
    /// and the opening-range geometry, so structure stays readable over them.
    /// </summary>
    /// <summary>
    /// The problems line — the only thing this indicator draws that says something is
    /// wrong, and the first thing it draws so nothing can take its pixels.
    /// </summary>
    private void DrawStatus(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var status = this.statusChartText;

        if (window is null || status.Length == 0)
            return;

        try
        {
            this.statusOverlay.Draw(graphics, window, status, labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The status overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawProfiles(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var drawable = this.profileDrawable;

        if (window is null
            || drawable.Profiles.Length == 0)
        {
            return;
        }

        try
        {
            this.profileOverlay.Draw(graphics, window, drawable,
                new ProfileOverlay.Options(
                    this.ProfileUpColor, this.ProfileDownColor,
                    this.ProfilePocColor, this.ProfileVaColor,
                    this.ProfileWidthPercent),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The profile overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>VWAP, risk lines and the cost meter — above the ranges.</summary>
    private void DrawWave1Geometry(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var chart = this.CurrentChart;
        var drawable = this.wave1Drawable;

        if (window is null || chart is null)
            return;

        // OperatorLines BELONGS IN THIS GUARD and was missing from it. The pace line was added
        // to this drawable without extending the "is there anything to draw" test, so on a
        // chart with no VWAP, no open position and no cost meter it returned early and the
        // operator's own line never drew at all. Focus mode made that reachable in the worst
        // case: it engages on a breach, and a daily-loss or re-entry breach can happen while
        // FLAT, which is exactly when RiskDllPrice is null.
        if (drawable.SessionVwap.Length == 0 && drawable.AnchoredVwap.Length == 0
            && drawable.RiskDllPrice is null && drawable.CostLines.Length == 0
            && drawable.OperatorLines.Length == 0)
        {
            return;
        }

        try
        {
            this.wave1Overlay.DrawGeometry(graphics, window, drawable,
                new Wave1Overlay.Options(
                    this.VwapColor, this.VwapAnchoredColor, this.NewsColor,
                    this.RiskColor, chart.BarsWidth),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The Wave-1 overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The HTF zone overlay's paint pass, guarded exactly as
    /// <see cref="DrawRanges"/> is. Drawn after HH/LL (chips keep their
    /// priority) and before the ranges, so the opening-range geometry and
    /// the setup's decision lines stay on top of the zones.
    /// </summary>
    private void DrawZones(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var chart = this.CurrentChart;
        var drawable = this.zoneDrawable;

        if (window is null || chart is null
            || (drawable.Zones.Length == 0 && drawable.Blocks.Length == 0))
        {
            return;
        }

        try
        {
            this.zoneOverlay.Draw(graphics, window, drawable,
                new ZoneOverlay.Options(
                    this.ZoneBullColor, this.ZoneBearColor,
                    this.ZoneRejectionBlocks, chart.BarsWidth),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The zone overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The delta panel's paint pass, drawn LAST: the band reserves its own
    /// pixels in the registry, and everything price-space has already had
    /// its chance at them.
    /// </summary>
    /// <summary>
    /// Draws the footprint imbalance marks.
    ///
    /// Wrapped for the reason stated on <see cref="DrawRanges"/>: an exception escaping
    /// OnPaintChart leaves the chart blank with no explanation, which is worse than a missing
    /// overlay. The reason is surfaced on the status line, which still renders.
    /// </summary>
    private void DrawImbalance(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var chart = this.CurrentChart;
        var drawable = this.imbalanceDrawable;

        if (window is null || chart is null)
            return;

        try
        {
            this.imbalanceOverlay.Draw(graphics, window, drawable,
                new ImbalanceOverlay.Options(
                    this.ImbalanceBuyColor, this.ImbalanceSellColor,
                    chart.BarsWidth, this.ImbalanceMarkWidthPx),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The imbalance overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Draws absorption at the touch. Wrapped for the reason on <see cref="DrawRanges"/>.</summary>
    private void DrawAbsorption(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var chart = this.CurrentChart;
        var drawable = this.absorptionDrawable;

        if (window is null || chart is null)
            return;

        try
        {
            this.absorptionOverlay.Draw(graphics, window, drawable,
                new AbsorptionOverlay.Options(
                    this.AbsorptionBidColor, this.AbsorptionAskColor, chart.BarsWidth),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The absorption overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Draws the session delta flip levels and the absorption shelves. Wrapped for the reason on
    /// <see cref="DrawRanges"/>: a layer that throws takes down every layer after it.
    /// </summary>
    private void DrawDeltaLevels(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var drawable = this.deltaLevelsDrawable;

        if (window is null)
            return;

        try
        {
            this.deltaLevelsOverlay.Draw(graphics, window, drawable,
                new DeltaLevelsOverlay.Options(
                    this.DeltaFlipUpColor, this.DeltaFlipDownColor, this.AbsorptionShelfColor,
                    this.LabelDeltaLevels),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The delta levels overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawDelta(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var chart = this.CurrentChart;
        var drawable = this.deltaDrawable;

        if (window is null || chart is null || drawable.Bars.Length == 0)
            return;

        try
        {
            this.deltaOverlay.Draw(graphics, window, drawable,
                new DeltaPanelOverlay.Options(
                    this.DeltaPanelHeightPx, this.DeltaUpColor,
                    this.DeltaDownColor, chart.BarsWidth),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The delta panel failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Draws the opening ranges in price space.
    ///
    /// Wrapped because a drawing fault must not take the frame with it: an exception escaping
    /// OnPaintChart leaves the chart blank with no explanation, which is a worse failure than
    /// a missing overlay. The reason is surfaced on the panel, which still renders.
    /// </summary>
    /// <summary>
    /// The levels worth drawing right now.
    ///
    /// Chosen on the FOLD, not on the paint path: selection reads module state, and paint must
    /// never do that. The rule itself is <see cref="LevelSelection"/> in Core, so what counts
    /// as "near price" is covered by the offline suite rather than by looking at a chart.
    /// </summary>
    private Level[] SelectLevels(DateTime nowUtc)
    {
        if (!this.DrawKeyLevels || this.levels is null || this.config is null || this.lastPrice <= 0)
            return Array.Empty<Level>();

        if (!this.config.Symbols.TryGetValue(this.instrument.Root, out var symbolConfig))
            return Array.Empty<Level>();

        var band = LevelSelection.BandWidth(
            this.averageDailyRange,
            this.LevelBandPercent / 100d,
            LevelBandFallbackTicks,
            this.instrument.TickSize);

        var candidates = new List<Level>(this.levels.Levels);

        candidates.AddRange(LevelSelection.RoundNumbers(
            this.lastPrice, band, symbolConfig.RoundNumberStep, nowUtc));

        return LevelSelection.NearPrice(candidates, this.lastPrice, band, MaxDrawnLevels).ToArray();
    }

    /// <summary>
    /// The band to use when the average daily range could not be measured.
    ///
    /// There has to be a number and it must not be "no filter": falling back to drawing
    /// everything would produce the unreadable chart the filter exists to prevent, on exactly
    /// the days daily history failed to load.
    /// </summary>
    private const int LevelBandFallbackTicks = 400;

    /// <summary>A ceiling, for the pathological case where a band holds a great many levels.</summary>
    private const int MaxDrawnLevels = 14;

    private void DrawRanges(Graphics graphics)
    {
        var window = this.CurrentChart?.MainWindow;
        var ranges = this.drawable;

        if (this.overlay is null || window is null || ranges.Length == 0)
            return;

        try
        {
            this.overlay.Draw(
                graphics, window, ranges,
                new ChartOverlay.Options(
                    this.DrawBox, this.DrawEdges, this.DrawMidline, this.DrawExtensions, this.DrawLabels),
                DateTime.UtcNow);

            this.overlay.DrawLevels(graphics, window, this.drawableLevels, this.DrawLabels);

            // Drawn LAST so the setup's lines sit above the reference levels. When both are on
            // the chart the one describing a decision is the one that must be legible.
            this.overlay.DrawSetup(
                graphics, window, this.drawableSetup, this.drawableSignalLabel, this.DrawLabels);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The range overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The HH/LL structure port's paint pass. Guarded exactly as
    /// <see cref="DrawRanges"/> is: a drawing fault is surfaced, never thrown
    /// into the platform's paint loop.
    /// </summary>
    /// <summary>
    /// The Fibonacci grid's paint pass. Guarded exactly as <see cref="DrawHhLl"/> is: a
    /// drawing fault is surfaced, never thrown into the platform's paint loop.
    /// </summary>
    private void DrawFib(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var chart = this.CurrentChart;
        var window = chart?.MainWindow;
        var drawable = this.fibDrawable;

        if (window is null || chart is null || drawable.Lines.Length == 0)
            return;

        try
        {
            this.fibOverlay.Draw(graphics, window, drawable,
                new FibOverlay.Options(
                    this.FibLineColor, this.FibPocketColor, this.FibLineStyle,
                    this.FibLineWidth, this.FibShowGoldenPocket, this.FibShowPrices,
                    chart.BarsWidth),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The Fibonacci overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawHhLl(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var chart = this.CurrentChart;
        var window = chart?.MainWindow;
        var drawable = this.hhllDrawable;

        if (window is null || chart is null
            || (drawable.Labels.Length == 0 && drawable.Segments.Length == 0))
        {
            return;
        }

        try
        {
            this.hhllOverlay.Draw(graphics, window, drawable,
                new HhLlOverlay.Options(
                    this.HhLlShowSupRes, this.HhLlSupportColor,
                    this.HhLlResistanceColor, this.HhLlLineStyle,
                    this.HhLlLineWidth, chart.BarsWidth),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The HH/LL overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The four level families absorbed from Aramid Flow.
    ///
    /// Guarded exactly as every other overlay pass is: a drawing fault is surfaced on the panel
    /// rather than thrown into the platform's paint loop, where it would take the rest of the
    /// frame with it.
    /// </summary>
    private void DrawFlowLevels(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var frame = this.flowFrame;

        if (window is null || this.config is not { } loaded)
            return;

        if (frame.Spans.Count == 0 && frame.ProvisionalAbsorption.Length == 0)
            return;

        try
        {
            this.flowLevelsOverlay.Draw(
                graphics, window, frame,
                new FlowLevelsOverlay.Options(
                    loaded.Flow, this.flowBuilder?.Boundaries, this.LabelDeltaLevels),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The flow levels overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawFlowMarkers(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var frame = this.flowFrame;

        if (window is null || this.config is not { } loaded)
            return;

        if (frame.Hits.Length == 0 && frame.BigTrades.Length == 0)
            return;

        try
        {
            this.flowMarkersOverlay.Draw(
                graphics, window, frame,
                new FlowMarkersOverlay.Options(
                    loaded.Flow, this.flowBuilder?.Boundaries, (float)(this.CurrentChart?.BarsWidth ?? 1d)),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The flow markers overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawFlowStatistics(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var frame = this.flowFrame;

        if (window is null || this.config is not { } loaded || frame.Columns.Length == 0)
            return;

        try
        {
            this.flowStatisticsOverlay.Draw(
                graphics, window, frame,
                new FlowStatisticsOverlay.Options(
                    loaded.Flow, (float)(this.CurrentChart?.BarsWidth ?? 1d)),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The flow statistics overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawFlowCounter(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;

        if (window is null || this.config is not { } loaded || !this.flowFrame.Counter.HasBar)
            return;

        try
        {
            this.flowCounterOverlay.Draw(
                graphics, window, this.flowFrame.Counter,
                new FlowCounterOverlay.Options(loaded.Flow), labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The flow counter overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawFlowDom(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var dom = this.flowFrame.Dom;

        // Nothing at all to say: the tool is off, so the frame carries the empty reading and even
        // its caveat is absent.
        if (window is null || this.config is not { } loaded || ReferenceEquals(dom, OrbCore.Flow.DomReading.Empty))
            return;

        try
        {
            this.flowDomOverlay.Draw(
                graphics, window, dom,
                new FlowDomOverlay.Options(loaded.Flow, this.instrument.TickSize), labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The flow DOM overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DrawFlowBias(Graphics graphics, List<RectangleF> labelRegistry)
    {
        var window = this.CurrentChart?.MainWindow;
        var bias = this.flowFrame.Bias;

        if (window is null || this.config is not { } loaded || !bias.HasGeometry)
            return;

        try
        {
            this.flowBiasOverlay.Draw(
                graphics, window, bias,
                new FlowBiasOverlay.Options(
                    loaded.Flow, (float)(this.CurrentChart?.BarsWidth ?? 1d)),
                labelRegistry);
        }
        catch (Exception ex)
        {
            this.overlayFault = PathDisplay.Redact(
                $"The flow bias overlay failed to draw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void Dispose()
    {
        this.overlay?.Dispose();
        this.overlay = null;
        this.hhllOverlay.Dispose();
        this.fibOverlay.Dispose();
        this.zoneOverlay.Dispose();
        this.deltaOverlay.Dispose();
        this.imbalanceOverlay.Dispose();
        this.absorptionOverlay.Dispose();
        this.deltaLevelsOverlay.Dispose();
        this.flowLevelsOverlay.Dispose();
        this.flowMarkersOverlay.Dispose();
        this.flowStatisticsOverlay.Dispose();
        this.flowCounterOverlay.Dispose();
        this.flowDomOverlay.Dispose();
        this.flowBiasOverlay.Dispose();
        this.wave1Overlay.Dispose();
        this.profileOverlay.Dispose();
        if (this.mouseChart is { } hookedChart)
        {
            hookedChart.MouseClick -= this.OnChartMouseClick;
            this.mouseChart = null;
        }

        this.parityWriter?.Dispose();
        this.parityWriter = null;

        // probeWriter was released in OnClear but NOT here, so a chart removed without a
        // clear leaked its file handle. OnClear is not guaranteed to run first.
        this.probeWriter?.Dispose();
        this.probeWriter = null;

        base.Dispose();
    }
}
