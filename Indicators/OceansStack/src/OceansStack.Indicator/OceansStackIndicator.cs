using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using TradingPlatform.BusinessLayer;
using FinchLite;
using oceansStackStrategy;
using Qt = TradingPlatform.BusinessLayer;

namespace OceansStack;

/// <summary>
/// The visual companion to `Strategies/oceansStackStrategy` — a full port of `ocean.pine`'s own
/// drawing output (see `Quantower-storage/TradingView/ocean.pine`), so the operator can see the
/// SAME state the strategy trades off of, the way Finch-Lite already does for
/// `finchDomScalpStrategy`. Confirmed via `AskUserQuestion` (2026-09-30): a FULL visual port, not a
/// stripped-down subset — daily/weekly value-area lines, zone boxes with live status text, fuel-
/// target labels, absorption dot markers, sweep "SF" signals, and QQQ comparison lines.
///
/// Reuses the EXACT SAME five detection engines the strategy trades off of
/// (`ValueAreaEngine`/`SessionPoolTracker`/`AbsorptionTracker`/`FuelPoolSelector`/
/// `SweepZoneTracker`) plus its own `Bar`/`DeltaTracker`, compiled in from the strategy's own
/// project folder (see this project's own .csproj comment for why the direction is REVERSED from
/// Finch-Lite's own `<Compile Include>` precedent). The `ProcessBar`/`EvaluateZone`/`PollQqq`/
/// `EvaluateQqq` orchestration below is a deliberate, HAND-SYNCED DUPLICATE of
/// `oceansStackStrategy.cs`'s own copy of the same logic — a future scoring/arming rule change must
/// be applied to both files by hand (see the .csproj comment's own "TRADE-OFF, STATED").
///
/// Draws only. Places no orders, reads no account — no `Quantity`/risk-management/stop-target-floor
/// InputParameters exist here at all, unlike the strategy.
///
/// TWO deliberate simplifications versus a byte-for-byte visual port:
/// - No Pine "Dalton open type" classification — nothing in `oceansStackStrategy.cs` ever ported
///   that logic from the Pine script in the first place, and inventing NEW, never-reviewed
///   detection logic here would go beyond "visualize what the strategy already computes."
/// - Retained-day history (`KeepPriorDays`) covers only the DAILY value-area lines (VAH/VAL/POC),
///   not pools/absorption — the value-area lines are what actually matters for seeing day-over-day
///   context; a fully retained multi-day pool/absorption history would multiply this file's
///   complexity for a feature nothing asked for by name.
///
/// QQQ comparison lines are the single highest-risk piece of this indicator: no indicator anywhere
/// in this codebase has ever pulled a second symbol's history before, and it is UNCONFIRMED that
/// Quantower's `Indicator` base class resolves an `InputParameter Symbol` to live market data the
/// same way `Strategy` does. Verify this pathway in isolation (attach with `UseQqq=true` and
/// nothing else configured) before trusting anything else on this indicator — see the project's own
/// `SETTINGS.md`.
/// </summary>
public sealed class OceansStackIndicator : Qt.Indicator
{
    // ---- lifecycle scaffolding --------------------------------------------------------------

    private const int RetryIntervalMs = 1000;
    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private Timer? retryTimer;
    private Timer? pollTimer;
    private readonly object initGate = new();
    private Qt.Symbol? symbol;
    private string? overlayFault;
    private string? lastPollFault;

    [InputParameter("QQQ Symbol (informational only, optional)", 2)]
    public Qt.Symbol? QqqSymbol { get; set; }

    [InputParameter("Start Point", 4)]
    public DateTime StartPoint { get; set; }

    [InputParameter("Poll interval (ms)", 6, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; }

    // ---- profile ------------------------------------------------------------------------------

    [InputParameter("NQ bin size (ticks)", 10, 1, 100000, 1, 0)]
    public int NqBinTicks { get; set; }

    [InputParameter("QQQ bin size ($, informational only)", 11, 0.01, 10, 0.01, 2)]
    public double QqqBinDollars { get; set; }

    [InputParameter("Value area (%)", 12, 50, 95, 1, 0)]
    public int ValueAreaPercent { get; set; }

    // ---- sessions (all America/New_York) — same names/defaults as oceansStackStrategy's own ---

    [InputParameter("RTH: start hour (ET)", 15, 0, 23, 1, 0)]
    public int RthStartHour { get; set; }
    [InputParameter("RTH: start minute", 16, 0, 59, 1, 0)]
    public int RthStartMinute { get; set; }
    [InputParameter("RTH: end hour (ET)", 17, 0, 23, 1, 0)]
    public int RthEndHour { get; set; }
    [InputParameter("RTH: end minute", 18, 0, 59, 1, 0)]
    public int RthEndMinute { get; set; }

    [InputParameter("Overnight: start hour (ET)", 19, 0, 23, 1, 0)]
    public int OvernightStartHour { get; set; }
    [InputParameter("Overnight: start minute", 20, 0, 59, 1, 0)]
    public int OvernightStartMinute { get; set; }
    [InputParameter("Overnight: end hour (ET)", 21, 0, 23, 1, 0)]
    public int OvernightEndHour { get; set; }
    [InputParameter("Overnight: end minute", 22, 0, 59, 1, 0)]
    public int OvernightEndMinute { get; set; }

    [InputParameter("Asia: start hour (ET)", 23, 0, 23, 1, 0)]
    public int AsiaStartHour { get; set; }
    [InputParameter("Asia: start minute", 24, 0, 59, 1, 0)]
    public int AsiaStartMinute { get; set; }
    [InputParameter("Asia: end hour (ET)", 25, 0, 23, 1, 0)]
    public int AsiaEndHour { get; set; }
    [InputParameter("Asia: end minute", 26, 0, 59, 1, 0)]
    public int AsiaEndMinute { get; set; }

    [InputParameter("Sweep window: start hour (ET)", 27, 0, 23, 1, 0)]
    public int SweepStartHour { get; set; }
    [InputParameter("Sweep window: start minute", 28, 0, 59, 1, 0)]
    public int SweepStartMinute { get; set; }
    [InputParameter("Sweep window: end hour (ET)", 29, 0, 23, 1, 0)]
    public int SweepEndHour { get; set; }
    [InputParameter("Sweep window: end minute", 30, 0, 59, 1, 0)]
    public int SweepEndMinute { get; set; }

    [InputParameter("QQQ RTH: start hour (ET)", 31, 0, 23, 1, 0)]
    public int QqqRthStartHour { get; set; }
    [InputParameter("QQQ RTH: start minute", 32, 0, 59, 1, 0)]
    public int QqqRthStartMinute { get; set; }
    [InputParameter("QQQ RTH: end hour (ET)", 33, 0, 23, 1, 0)]
    public int QqqRthEndHour { get; set; }
    [InputParameter("QQQ RTH: end minute", 34, 0, 59, 1, 0)]
    public int QqqRthEndMinute { get; set; }

    // ---- absorption proxy — REAL tick-classified delta, matching the strategy's own choice ----

    [InputParameter("Absorption: bar volume >= x * 50-bar SMA", 40, 0.1, 10, 0.1, 1)]
    public double AbsVolumeMultiplier { get; set; }

    [InputParameter("Absorption: |delta| >= x * bar volume", 41, 0.01, 1, 0.01, 2)]
    public double AbsDeltaFraction { get; set; }

    [InputParameter("Absorption: must hold intraday", 42)]
    public bool AbsMustHold { get; set; }

    [InputParameter("Absorption: hold-break tolerance (ticks)", 43, 0, 100000, 1, 0)]
    public int AbsHoldToleranceTicks { get; set; }

    [InputParameter("Absorption: counts near edge within (ticks)", 44, 1, 100000, 1, 0)]
    public int AbsNearTicks { get; set; }

    // ---- LVN ledge ----------------------------------------------------------------------------

    [InputParameter("LVN: bin volume <= % of max bin", 50, 1, 100, 1, 0)]
    public int LvnMaxPercentOfMaxBin { get; set; }

    [InputParameter("LVN: band beyond edge (ticks)", 51, 1, 100000, 1, 0)]
    public int LvnBandTicks { get; set; }

    // ---- fuel pools -----------------------------------------------------------------------------

    [InputParameter("Fuel: pool minimum distance beyond edge (ticks)", 55, 1, 100000, 1, 0)]
    public int PoolMinTicks { get; set; }

    [InputParameter("Fuel: pool maximum distance beyond edge (ticks)", 56, 1, 100000, 1, 0)]
    public int PoolMaxTicks { get; set; }

    [InputParameter("Fuel: overnight H/L enable", 57)]
    public bool UseOvernightPool { get; set; }

    [InputParameter("Fuel: Asia H/L enable", 58)]
    public bool UseAsiaPool { get; set; }

    [InputParameter("Fuel: prior-day H/L enable", 59)]
    public bool UsePriorDayPool { get; set; }

    // ---- QQQ -> NQ (informational — draws comparison lines only, never gates anything) ---------

    [InputParameter("QQQ->NQ: enable", 65)]
    public bool UseQqq { get; set; }

    [InputParameter("QQQ->NQ: use live ratio (vs. prior RTH close)", 66)]
    public bool QqqRatioUseLive { get; set; }

    // ---- weekly overlay -------------------------------------------------------------------------

    [InputParameter("Weekly: enable", 70)]
    public bool WeeklyEnabled { get; set; }

    [InputParameter("Weekly: prior-week H/L counts as fuel", 71)]
    public bool WeeklyHighLowAsFuel { get; set; }

    // ---- signal ---------------------------------------------------------------------------------

    [InputParameter("Signal: minimum score to arm (1-3)", 75, 1, 3, 1, 0)]
    public int MinScoreToArm { get; set; }

    [InputParameter("Signal: stop buffer beyond sweep extreme (ticks)", 76, 0, 100000, 1, 0)]
    public int StopBufferTicks { get; set; }

    // ---- display ------------------------------------------------------------------------------

    [InputParameter("Display: keep prior days on chart", 90, 0, 30, 1, 0)]
    public int KeepPriorDays { get; set; }

    [InputParameter("Display: keep labels on prior days", 91)]
    public bool KeepPriorDayLabels { get; set; }

    [InputParameter("Display: zone half-height (ticks)", 92, 1, 100000, 1, 0)]
    public int ZoneHalfHeightTicks { get; set; }

    [InputParameter("Display: show value-area lines", 93)]
    public bool ShowValueAreaLines { get; set; }

    [InputParameter("Display: show zone boxes", 94)]
    public bool ShowZoneBoxes { get; set; }

    [InputParameter("Display: show fuel target labels", 95)]
    public bool ShowFuelTargets { get; set; }

    [InputParameter("Display: show absorption markers", 96)]
    public bool ShowAbsorptionMarkers { get; set; }

    [InputParameter("Display: show sweep signals", 97)]
    public bool ShowSweepSignals { get; set; }

    // ---- colours ------------------------------------------------------------------------------

    [InputParameter("Colour: daily VAH/VAL", 100)]
    public Color DailyLineColor { get; set; } = Color.FromArgb(0x40, 0xC4, 0xFF);

    [InputParameter("Colour: daily POC", 101)]
    public Color DailyPocColor { get; set; } = Color.White;

    [InputParameter("Colour: prior-day H/L", 102)]
    public Color PriorDayLineColor { get; set; } = Color.FromArgb(0x9E, 0x9E, 0x9E);

    [InputParameter("Colour: overnight H/L", 103)]
    public Color OvernightLineColor { get; set; } = Color.FromArgb(0xFF, 0x8A, 0xD8);

    [InputParameter("Colour: Asia H/L", 104)]
    public Color AsiaLineColor { get; set; } = Color.FromArgb(0xFF, 0xA5, 0x00);

    [InputParameter("Colour: weekly value area/H/L", 105)]
    public Color WeeklyLineColor { get; set; } = Color.FromArgb(0xB0, 0x00, 0xE6);

    [InputParameter("Colour: QQQ comparison lines", 106)]
    public Color QqqLineColor { get; set; } = Color.FromArgb(0x00, 0xBF, 0xA5);

    [InputParameter("Colour: fuel target label", 107)]
    public Color FuelLabelColor { get; set; } = Color.FromArgb(0xFF, 0xD7, 0x00);

    [InputParameter("Colour: zone — edge only (score < 2)", 108)]
    public Color ZoneEdgeOnlyColor { get; set; } = Color.FromArgb(0x9E, 0x9E, 0x9E);

    [InputParameter("Colour: zone — scored (score >= 2)", 109)]
    public Color ZoneScoreTwoPlusColor { get; set; } = Color.FromArgb(0xFF, 0xEB, 0x3B);

    [InputParameter("Colour: zone — armed", 110)]
    public Color ZoneArmedColor { get; set; } = Color.FromArgb(0x40, 0xC4, 0xFF);

    [InputParameter("Colour: zone — swept (awaiting reclaim)", 111)]
    public Color ZoneSweptColor { get; set; } = Color.FromArgb(0xFF, 0xA5, 0x00);

    /// <summary>Also drives: a fired TOP zone's own box, the "SF" short signal, and a resistance-
    /// side absorption marker — one colour for "short/resistance" everywhere on this indicator,
    /// matching this codebase's usual green/red directional convention rather than Pine's own
    /// white/cyan absorption-marker colours (a deliberate simplification for consistency).</summary>
    [InputParameter("Colour: short / resistance signals", 112)]
    public Color ShortColor { get; set; } = Color.FromArgb(0xFF, 0x52, 0x52);

    [InputParameter("Colour: long / support signals", 113)]
    public Color LongColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    // ---- engines --------------------------------------------------------------------------------

    private HistoricalData? hdmNq;
    private HistoricalData? hdmQqq;

    private ValueAreaEngine? dailyVa;
    private ValueAreaEngine? weeklyVa;
    private ValueAreaEngine? qqqVa;
    private SessionPoolTracker? pools;
    private AbsorptionTracker? absorption;
    private SweepZoneTracker? topZone;
    private SweepZoneTracker? bottomZone;
    private DeltaTracker? deltaTracker;
    private readonly ConcurrentQueue<(DateTime TimeUtc, double Size, bool IsBuy)> deltaTickQueue = new();

    private int barsSeenNq = -1;
    private bool wasInRthNq;
    private int lastWeekKeyNq = -1;
    private DateTime? currentDayStartUtc;

    private int barsSeenQqq = -1;
    private bool wasInRthQqq;
    private double? qqqCloseAtRthEnd;
    private double? nqLastClose;
    private double? qqqRatioFixed;
    private double? lastQqqVahNq;
    private double? lastQqqValNq;

    private double? priorDayPoc, priorDayVah, priorDayVal;
    private double? priorWeekPoc, priorWeekVah, priorWeekVal;

    private readonly record struct HistoricalDayRecord(DateTime StartUtc, DateTime EndUtc, double Poc, double Vah, double Val);
    private readonly List<HistoricalDayRecord> dailyHistory = new();

    // last-evaluated score/armed/fuel state, one side each — used to build the zone box's own
    // status text and the fuel-target label at RebuildDrawables time, without re-running
    // EvaluateZone a second time outside the bar-close path.
    private bool lastTopArmed, lastBottomArmed;
    private int lastTopScore, lastBottomScore;
    private double? lastTopFuel, lastBottomFuel;
    private string lastTopFuelName = string.Empty, lastBottomFuelName = string.Empty;
    private bool lastTopHeldNear, lastTopLvn, lastBottomHeldNear, lastBottomLvn;
    private bool lastInSweepWindow;

    private const int MaxAbsorptionMarkersKept = 300;
    private const int MaxSweepSignalsKept = 50;
    private readonly List<AbsorptionMarkerDraw> absorptionMarkers = new();
    private readonly List<SweepSignalDraw> sweepSignals = new();

    // ---- overlays -----------------------------------------------------------------------------

    private readonly ValueAreaLineOverlay lineOverlay = new();
    private readonly ZoneBoxOverlay zoneOverlay = new();
    private readonly FuelTargetOverlay fuelOverlay = new();
    private readonly AbsorptionMarkerOverlay absorptionOverlay = new();
    private readonly SweepSignalOverlay sweepSignalOverlay = new();

    private volatile ValueAreaLineDrawable lineDrawable = ValueAreaLineDrawable.Empty;
    private volatile ZoneBoxDrawable zoneDrawable = ZoneBoxDrawable.Empty;
    private volatile FuelTargetDrawable fuelDrawable = FuelTargetDrawable.Empty;
    private volatile AbsorptionMarkerDrawable absorptionDrawable = AbsorptionMarkerDrawable.Empty;
    private volatile SweepSignalDrawable sweepSignalDrawable = SweepSignalDrawable.Empty;

    private readonly Font faultFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush faultBrush = new(Color.FromArgb(0xFF, 0xC1, 0x07));
    private readonly SolidBrush faultBack = new(Color.FromArgb(190, 16, 18, 24));

    public OceansStackIndicator()
    {
        this.Name = "Ocean's Stack";
        this.Description =
            "Visual companion to oceansStackStrategy -- full port of ocean.pine's own drawing "
            + "output (value-area lines, zone boxes, fuel targets, absorption markers, sweep "
            + "signals, QQQ comparison lines). Draws only, places no orders.";
        this.SeparateWindow = false;

        this.QqqSymbol = null;
        this.StartPoint = DateTime.UtcNow.AddDays(-21);
        this.PollIntervalMs = 250;

        this.NqBinTicks = 10;
        this.QqqBinDollars = 0.05;
        this.ValueAreaPercent = 70;

        this.RthStartHour = 9; this.RthStartMinute = 30; this.RthEndHour = 16; this.RthEndMinute = 0;
        this.OvernightStartHour = 18; this.OvernightStartMinute = 0; this.OvernightEndHour = 9; this.OvernightEndMinute = 30;
        this.AsiaStartHour = 20; this.AsiaStartMinute = 0; this.AsiaEndHour = 2; this.AsiaEndMinute = 0;
        this.SweepStartHour = 9; this.SweepStartMinute = 30; this.SweepEndHour = 10; this.SweepEndMinute = 30;
        this.QqqRthStartHour = 9; this.QqqRthStartMinute = 30; this.QqqRthEndHour = 16; this.QqqRthEndMinute = 0;

        this.AbsVolumeMultiplier = 1.2;
        this.AbsDeltaFraction = 0.15;
        this.AbsMustHold = true;
        this.AbsHoldToleranceTicks = 20;
        this.AbsNearTicks = 60;

        this.LvnMaxPercentOfMaxBin = 30;
        this.LvnBandTicks = 40;

        this.PoolMinTicks = 20;
        this.PoolMaxTicks = 320;
        this.UseOvernightPool = true;
        this.UseAsiaPool = true;
        this.UsePriorDayPool = true;

        this.UseQqq = false; // off by default here — the strategy defaults it on, but this is the higher-risk, unverified pathway (see class doc comment); opt in deliberately
        this.QqqRatioUseLive = false;

        this.WeeklyEnabled = true;
        this.WeeklyHighLowAsFuel = true;

        this.MinScoreToArm = 2;
        this.StopBufferTicks = 16;

        this.KeepPriorDays = 3;
        this.KeepPriorDayLabels = false;
        this.ZoneHalfHeightTicks = 10;
        this.ShowValueAreaLines = true;
        this.ShowZoneBoxes = true;
        this.ShowFuelTargets = true;
        this.ShowAbsorptionMarkers = true;
        this.ShowSweepSignals = true;
    }

    // ---- lifecycle ----------------------------------------------------------------------------

    protected override void OnInit()
    {
        if (!this.TryInitialise())
            this.retryTimer = new Timer(this.OnRetryTimer, null, RetryIntervalMs, RetryIntervalMs);
    }

    private void OnRetryTimer(object? _)
    {
        lock (this.initGate)
        {
            if (this.pollTimer is not null)
                return;

            if (this.TryInitialise())
            {
                this.retryTimer?.Dispose();
                this.retryTimer = null;
            }
        }
    }

    /// <summary>Unlike Finch-Lite's own `TryInitialise`, this needs no DOM check at all — this
    /// indicator never reads the order book, only historical 1-minute bars and the tape (via
    /// `TickClassifier`, which only needs `symbol.Bid`/`Ask`, already live once a symbol is
    /// attached).</summary>
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

            this.symbol = symbol;
            this.overlayFault = null;

            var tickSize = symbol.TickSize;
            this.dailyVa = new ValueAreaEngine(this.NqBinTicks * tickSize);
            this.weeklyVa = new ValueAreaEngine(this.NqBinTicks * tickSize);
            this.pools = new SessionPoolTracker();
            this.absorption = new AbsorptionTracker(this.AbsHoldToleranceTicks * tickSize, this.AbsMustHold);
            this.topZone = new SweepZoneTracker(+1);
            this.bottomZone = new SweepZoneTracker(-1);
            this.deltaTracker = new DeltaTracker(TimeSpan.FromMinutes(1));

            // This indicator's OWN dedicated 1-minute series, independent of whatever period the
            // chart itself is showing — same reasoning as Finch-Lite's own dedicated 5m/15m POC/
            // order-block series (`TryStartPoc`/`TryStartOrderBlocks`). Every engine here assumes
            // 1-minute bars (see `ValueAreaEngine`'s own doc comment), so unlike the strategy this
            // indicator exposes no configurable `Period` at all — there is no value it could be
            // set to other than 1 minute that wouldn't silently break the profile math.
            this.hdmNq = symbol.GetHistory(Period.MIN1, symbol.HistoryType, this.StartPoint);

            this.TryStartQqq(symbol);

            symbol.NewLast -= this.OnLast;
            symbol.NewLast += this.OnLast;

            var interval = Math.Max(this.PollIntervalMs, 50);
            this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);
            return true;
        }
        catch (Exception ex)
        {
            this.overlayFault = $"Ocean's Stack failed to start: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Best-effort, wrapped so a QQQ failure never blocks the NQ-only core of this
    /// indicator from drawing. See the class doc comment — this is the single highest-risk,
    /// least-precedented pathway in this file.</summary>
    private void TryStartQqq(Qt.Symbol symbol)
    {
        if (!this.UseQqq || this.QqqSymbol is null)
            return;

        try
        {
            var qqqSymbol = this.QqqSymbol;
            if (qqqSymbol.State == BusinessObjectState.Fake)
                qqqSymbol = Core.Instance.GetSymbol(qqqSymbol.CreateInfo());

            if (qqqSymbol is not null)
            {
                this.qqqVa = new ValueAreaEngine(this.QqqBinDollars);
                this.hdmQqq = qqqSymbol.GetHistory(Period.MIN1, qqqSymbol.HistoryType, this.StartPoint);
            }
        }
        catch (Exception ex)
        {
            this.overlayFault = $"[QQQ] unavailable at attach: {ex.GetType().Name}: {ex.Message}";
            this.qqqVa = null;
            this.hdmQqq = null;
        }
    }

    /// <summary>§10-style discipline: no work on the market-data thread beyond classifying and
    /// enqueueing.</summary>
    private void OnLast(Qt.Symbol symbol, Last last)
    {
        if (last is null || last.Size <= 0)
            return;

        if (TickClassifier.TryClassify(symbol, last, out var isBuy))
            this.deltaTickQueue.Enqueue((last.Time, last.Size, isBuy));
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

        this.hdmNq?.Dispose();
        this.hdmNq = null;
        this.hdmQqq?.Dispose();
        this.hdmQqq = null;

        this.dailyVa = null;
        this.weeklyVa = null;
        this.qqqVa = null;
        this.pools = null;
        this.absorption = null;
        this.topZone = null;
        this.bottomZone = null;
        this.deltaTracker = null;
        this.deltaTickQueue.Clear();

        this.barsSeenNq = -1;
        this.wasInRthNq = false;
        this.lastWeekKeyNq = -1;
        this.currentDayStartUtc = null;

        this.barsSeenQqq = -1;
        this.wasInRthQqq = false;
        this.qqqCloseAtRthEnd = null;
        this.nqLastClose = null;
        this.qqqRatioFixed = null;
        this.lastQqqVahNq = null;
        this.lastQqqValNq = null;

        this.priorDayPoc = this.priorDayVah = this.priorDayVal = null;
        this.priorWeekPoc = this.priorWeekVah = this.priorWeekVal = null;
        this.dailyHistory.Clear();

        this.lastTopArmed = this.lastBottomArmed = false;
        this.lastTopScore = this.lastBottomScore = 0;
        this.lastTopFuel = this.lastBottomFuel = null;
        this.lastTopFuelName = this.lastBottomFuelName = string.Empty;
        this.lastTopHeldNear = this.lastTopLvn = this.lastBottomHeldNear = this.lastBottomLvn = false;
        this.lastInSweepWindow = false;

        this.absorptionMarkers.Clear();
        this.sweepSignals.Clear();

        this.lineDrawable = ValueAreaLineDrawable.Empty;
        this.zoneDrawable = ZoneBoxDrawable.Empty;
        this.fuelDrawable = FuelTargetDrawable.Empty;
        this.absorptionDrawable = AbsorptionMarkerDrawable.Empty;
        this.sweepSignalDrawable = SweepSignalDrawable.Empty;

        this.overlayFault = null;
        this.lastPollFault = null;
    }

    // ---- the poll -----------------------------------------------------------------------------

    private void OnPollTimer(object? state)
    {
        try
        {
            this.RunPoll();
        }
        catch (Exception ex)
        {
            var reason = $"Poll threw ({ex.GetType().Name}: {ex.Message})";
            if (!string.Equals(this.lastPollFault, reason, StringComparison.Ordinal))
            {
                this.lastPollFault = reason;
                this.overlayFault = reason;
            }
        }
    }

    private void RunPoll()
    {
        var symbol = this.symbol;
        var hdm = this.hdmNq;
        var dailyVa = this.dailyVa;
        var weeklyVa = this.weeklyVa;
        var pools = this.pools;
        var absorption = this.absorption;
        var topZone = this.topZone;
        var bottomZone = this.bottomZone;
        var deltaTracker = this.deltaTracker;

        if (symbol is null || hdm is null || dailyVa is null || weeklyVa is null || pools is null
            || absorption is null || topZone is null || bottomZone is null || deltaTracker is null)
            return;

        var tickSize = symbol.TickSize;
        if (tickSize <= 0)
            return;

        while (this.deltaTickQueue.TryDequeue(out var tick))
            deltaTracker.FeedTick(tick.TimeUtc, tick.Size, tick.IsBuy);

        var barsChanged = false;

        if (hdm.Count > 1)
        {
            var closedUpTo = hdm.Count - 1; // Count - 1 is the still-forming bar

            if (this.barsSeenNq < 0)
                this.barsSeenNq = 0; // backfill everything — today's picture must be complete on attach

            for (var i = this.barsSeenNq; i < closedUpTo; i++)
            {
                if (!TryReadBar(hdm, i, out var bar))
                    continue;

                this.ProcessBar(bar, dailyVa, weeklyVa, pools, absorption, topZone, bottomZone, deltaTracker, tickSize);
                barsChanged = true;
            }

            this.barsSeenNq = closedUpTo;
        }

        this.PollQqq();

        this.lastPollFault = null;
        this.overlayFault = null;
        this.RebuildDrawables(tickSize);

        if (barsChanged)
            this.RebuildEventDrawables();
    }

    /// <summary>Hand-synced duplicate of `oceansStackStrategy.cs`'s own `ProcessNqBar` — feeds every
    /// accumulator and evaluates score/armed/fuel for BOTH zones on every closed bar, including
    /// backlog (unlike the strategy's own `isLive` gate, which exists only to avoid TRADING on
    /// backlog — a purely visual indicator has no equivalent concern; the current day's picture
    /// must be complete and correct even when most of it comes from bars that closed before this
    /// indicator attached).</summary>
    private void ProcessBar(
        Bar bar, ValueAreaEngine dailyVa, ValueAreaEngine weeklyVa, SessionPoolTracker pools,
        AbsorptionTracker absorption, SweepZoneTracker topZone, SweepZoneTracker bottomZone,
        DeltaTracker deltaTracker, double tickSize)
    {
        var etTime = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);

        var inRth = IsInWindow(etTime, this.RthStartHour, this.RthStartMinute, this.RthEndHour, this.RthEndMinute);
        var inOvernight = IsInWindow(etTime, this.OvernightStartHour, this.OvernightStartMinute, this.OvernightEndHour, this.OvernightEndMinute);
        var inAsia = IsInWindow(etTime, this.AsiaStartHour, this.AsiaStartMinute, this.AsiaEndHour, this.AsiaEndMinute);
        var inSweepWindow = IsInWindow(etTime, this.SweepStartHour, this.SweepStartMinute, this.SweepEndHour, this.SweepEndMinute);

        var isNewRth = inRth && !this.wasInRthNq;
        var weekKey = IsoWeekKey(etTime);
        var isNewWeek = this.WeeklyEnabled && isNewRth && weekKey != this.lastWeekKeyNq;
        if (isNewRth) this.lastWeekKeyNq = weekKey;

        if (isNewRth)
        {
            var (poc, vah, val) = dailyVa.Finalize(this.ValueAreaPercent / 100.0);
            if (poc is { } p)
            {
                if (this.currentDayStartUtc is { } prevStart)
                {
                    var keep = Math.Max(1, this.KeepPriorDays);
                    this.dailyHistory.Add(new HistoricalDayRecord(prevStart, bar.OpenUtc, p, vah ?? p, val ?? p));
                    if (this.dailyHistory.Count > keep)
                        this.dailyHistory.RemoveRange(0, this.dailyHistory.Count - keep);
                }

                this.priorDayPoc = p; this.priorDayVah = vah; this.priorDayVal = val;
            }

            this.currentDayStartUtc = bar.OpenUtc;
            dailyVa.Reset();
            absorption.OnRthReset();
            topZone.OnRthReset();
            bottomZone.OnRthReset();
        }

        if (isNewWeek)
        {
            var (wpoc, wvah, wval) = weeklyVa.Finalize(this.ValueAreaPercent / 100.0);
            if (wpoc is { } wp) { this.priorWeekPoc = wp; this.priorWeekVah = wvah; this.priorWeekVal = wval; }
            weeklyVa.Reset();
        }

        if (inRth)
        {
            var hl2 = (bar.High + bar.Low) / 2.0;
            dailyVa.AddSample(hl2, bar.Volume);
            if (this.WeeklyEnabled) weeklyVa.AddSample(hl2, bar.Volume);
        }

        pools.FeedBar(bar.High, bar.Low, inOvernight, inAsia, inRth, isNewRth, isNewWeek);
        this.wasInRthNq = inRth;
        this.nqLastClose = bar.Close;

        deltaTracker.CloseBar(bar.OpenUtc);
        var barDelta = deltaTracker.LastBarDelta;
        absorption.OnBarClosed(bar, barDelta, inRth, this.AbsVolumeMultiplier, this.AbsDeltaFraction);

        if (absorption.LastBarWasAbsorption(1))
            this.AddAbsorptionMarker(new AbsorptionMarkerDraw(bar.OpenUtc, bar.High, IsResistanceSide: true));
        if (absorption.LastBarWasAbsorption(-1))
            this.AddAbsorptionMarker(new AbsorptionMarkerDraw(bar.OpenUtc, bar.Low, IsResistanceSide: false));

        this.lastInSweepWindow = inSweepWindow;

        if (this.priorDayVah is { } vahEdge)
        {
            var (armedTop, scoreTop, fuelTop, fuelTopName, heldTop, lvnTop) =
                this.EvaluateZone(vahEdge, +1, dailyVa, absorption, pools, tickSize, inSweepWindow);

            var result = topZone.OnBarClosed(
                bar, barDelta, armedTop, scoreTop, fuelTop, vahEdge, this.priorDayVal ?? vahEdge,
                this.priorDayPoc, this.StopBufferTicks * tickSize, absorption.LastBarWasAbsorption(1));

            this.lastTopArmed = armedTop; this.lastTopScore = scoreTop;
            this.lastTopFuel = fuelTop; this.lastTopFuelName = fuelTopName;
            this.lastTopHeldNear = heldTop; this.lastTopLvn = lvnTop;

            if (result.FiredThisBar)
                this.AddSweepSignal(new SweepSignalDraw(bar.OpenUtc, bar.Close, IsLong: false));
        }

        if (this.priorDayVal is { } valEdge)
        {
            var (armedBottom, scoreBottom, fuelBottom, fuelBottomName, heldBottom, lvnBottom) =
                this.EvaluateZone(valEdge, -1, dailyVa, absorption, pools, tickSize, inSweepWindow);

            var result = bottomZone.OnBarClosed(
                bar, barDelta, armedBottom, scoreBottom, fuelBottom, valEdge, this.priorDayVah ?? valEdge,
                this.priorDayPoc, this.StopBufferTicks * tickSize, absorption.LastBarWasAbsorption(-1));

            this.lastBottomArmed = armedBottom; this.lastBottomScore = scoreBottom;
            this.lastBottomFuel = fuelBottom; this.lastBottomFuelName = fuelBottomName;
            this.lastBottomHeldNear = heldBottom; this.lastBottomLvn = lvnBottom;

            if (result.FiredThisBar)
                this.AddSweepSignal(new SweepSignalDraw(bar.OpenUtc, bar.Close, IsLong: true));
        }
    }

    /// <summary>Hand-synced duplicate of `oceansStackStrategy.cs`'s own `EvaluateZone` — extended to
    /// also return the held-absorption/LVN component flags, needed for the zone box's own status
    /// text (the strategy has no equivalent need, since it never draws anything).</summary>
    private (bool Armed, int Score, double? Fuel, string FuelName, bool HeldNear, bool Lvn) EvaluateZone(
        double edge, int dir, ValueAreaEngine dailyVa, AbsorptionTracker absorption, SessionPoolTracker pools,
        double tickSize, bool inSweepWindow)
    {
        var heldNear = absorption.IsHeldNear(edge, dir, this.AbsNearTicks * tickSize);
        var lvn = dailyVa.IsLvnLedge(edge, dir, this.LvnBandTicks * tickSize, this.LvnMaxPercentOfMaxBin);
        var score = 1 + (heldNear ? 1 : 0) + (lvn ? 1 : 0);

        var (fuel, fuelName) = FuelPoolSelector.SelectNearest(
            edge, dir, this.PoolMinTicks * tickSize, this.PoolMaxTicks * tickSize,
            this.UseOvernightPool, pools.OvernightHigh, pools.OvernightLow,
            this.UseAsiaPool, pools.AsiaHigh, pools.AsiaLow,
            this.UsePriorDayPool, pools.PriorDayHigh, pools.PriorDayLow,
            this.WeeklyEnabled && this.WeeklyHighLowAsFuel, pools.PriorWeekHigh, pools.PriorWeekLow);

        var armed = inSweepWindow && score >= this.MinScoreToArm && fuel is not null;
        return (armed, score, fuel, fuelName, heldNear, lvn);
    }

    /// <summary>Hand-synced duplicate of `oceansStackStrategy.cs`'s own `PollQqq` — entirely
    /// independent cursor/session tracking from NQ's, wrapped in its own try/catch so a QQQ failure
    /// can never affect the real NQ-only drawing.</summary>
    private void PollQqq()
    {
        if (!this.UseQqq || this.hdmQqq is null || this.qqqVa is null || this.QqqSymbol is null)
            return;

        try
        {
            var hdmQ = this.hdmQqq;
            if (hdmQ.Count <= 1) return;

            var closedUpTo = hdmQ.Count - 1;
            if (this.barsSeenQqq < 0) this.barsSeenQqq = 0;

            for (var i = this.barsSeenQqq; i < closedUpTo; i++)
            {
                if (!TryReadBar(hdmQ, i, out var bar)) continue;

                var etTime = TimeZoneInfo.ConvertTimeFromUtc(bar.OpenUtc, SessionZone);
                var inRth = IsInWindow(etTime, this.QqqRthStartHour, this.QqqRthStartMinute, this.QqqRthEndHour, this.QqqRthEndMinute);
                var isNewRth = inRth && !this.wasInRthQqq;
                var justLeftRth = !inRth && this.wasInRthQqq;

                if (isNewRth)
                    this.qqqVa.Reset();

                if (inRth)
                {
                    var hl2 = (bar.High + bar.Low) / 2.0;
                    this.qqqVa.AddSample(hl2, bar.Volume);
                }

                if (justLeftRth)
                {
                    this.qqqCloseAtRthEnd = bar.Close;
                    if (!this.QqqRatioUseLive && this.qqqCloseAtRthEnd is { } qc && qc > 0 && this.nqLastClose is { } nqc)
                        this.qqqRatioFixed = nqc / qc;
                }

                this.wasInRthQqq = inRth;
            }

            this.barsSeenQqq = closedUpTo;
            this.EvaluateQqq();
        }
        catch (Exception ex)
        {
            this.overlayFault = $"[QQQ] poll failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Hand-synced duplicate of `oceansStackStrategy.cs`'s own `EvaluateQqq` — computes the
    /// SAME converted VAH/VAL-in-NQ-terms the strategy's own comparison logs, but stores them for
    /// drawing instead of logging a disagreement.</summary>
    private void EvaluateQqq()
    {
        this.lastQqqVahNq = null;
        this.lastQqqValNq = null;

        if (this.qqqVa is null) return;

        var ratio = this.QqqRatioUseLive
            ? (this.nqLastClose is { } nq && this.qqqCloseAtRthEnd is { } q && q > 0 ? nq / q : (double?)null)
            : this.qqqRatioFixed;
        if (ratio is not { } r) return;

        var (_, qVah, qVal) = this.qqqVa.Finalize(this.ValueAreaPercent / 100.0);
        if (qVah is not { } qv || qVal is not { } ql) return;

        this.lastQqqVahNq = qv * r;
        this.lastQqqValNq = ql * r;
    }

    private void AddAbsorptionMarker(AbsorptionMarkerDraw marker)
    {
        this.absorptionMarkers.Add(marker);
        if (this.absorptionMarkers.Count > MaxAbsorptionMarkersKept)
            this.absorptionMarkers.RemoveRange(0, this.absorptionMarkers.Count - MaxAbsorptionMarkersKept);
    }

    private void AddSweepSignal(SweepSignalDraw signal)
    {
        this.sweepSignals.Add(signal);
        if (this.sweepSignals.Count > MaxSweepSignalsKept)
            this.sweepSignals.RemoveRange(0, this.sweepSignals.Count - MaxSweepSignalsKept);
    }

    private void RebuildEventDrawables()
    {
        this.absorptionDrawable = new AbsorptionMarkerDrawable(this.absorptionMarkers.ToArray());
        this.sweepSignalDrawable = new SweepSignalDrawable(this.sweepSignals.ToArray());
    }

    /// <summary>Fully re-derives every state-driven drawable from current engine state — cheap,
    /// since these are all tiny value objects (a handful of lines/zones/labels), so there is no
    /// need for incremental diffing the way the append-only event lists (absorption markers, sweep
    /// signals) use instead.</summary>
    private void RebuildDrawables(double tickSize)
    {
        var todayStart = this.currentDayStartUtc ?? DateTime.UtcNow;
        var lines = new List<ValueAreaLineDraw>();

        if (this.priorDayVah is { } vah) lines.Add(new ValueAreaLineDraw(vah, "VAH", LineKind.DailyVah, null, todayStart));
        if (this.priorDayVal is { } val) lines.Add(new ValueAreaLineDraw(val, "VAL", LineKind.DailyVal, null, todayStart));
        if (this.priorDayPoc is { } poc) lines.Add(new ValueAreaLineDraw(poc, "POC", LineKind.DailyPoc, null, todayStart));

        if (this.pools is { } pools)
        {
            if (pools.PriorDayHigh is { } pdh) lines.Add(new ValueAreaLineDraw(pdh, "PDH", LineKind.PriorDayHL, null, todayStart));
            if (pools.PriorDayLow is { } pdl) lines.Add(new ValueAreaLineDraw(pdl, "PDL", LineKind.PriorDayHL, null, todayStart));
            if (pools.OvernightHigh is { } onh) lines.Add(new ValueAreaLineDraw(onh, "ovnH", LineKind.OvernightHL, null, todayStart));
            if (pools.OvernightLow is { } onl) lines.Add(new ValueAreaLineDraw(onl, "ovnL", LineKind.OvernightHL, null, todayStart));
            if (pools.AsiaHigh is { } ah) lines.Add(new ValueAreaLineDraw(ah, "asiaH", LineKind.AsiaHL, null, todayStart));
            if (pools.AsiaLow is { } al) lines.Add(new ValueAreaLineDraw(al, "asiaL", LineKind.AsiaHL, null, todayStart));

            if (this.WeeklyEnabled)
            {
                if (pools.PriorWeekHigh is { } pwh) lines.Add(new ValueAreaLineDraw(pwh, "PWH", LineKind.WeeklyHL, null, todayStart));
                if (pools.PriorWeekLow is { } pwl) lines.Add(new ValueAreaLineDraw(pwl, "PWL", LineKind.WeeklyHL, null, todayStart));
            }
        }

        if (this.WeeklyEnabled)
        {
            if (this.priorWeekVah is { } wvah) lines.Add(new ValueAreaLineDraw(wvah, "wVAH", LineKind.WeeklyVah, null, todayStart));
            if (this.priorWeekVal is { } wval) lines.Add(new ValueAreaLineDraw(wval, "wVAL", LineKind.WeeklyVal, null, todayStart));
            if (this.priorWeekPoc is { } wpoc) lines.Add(new ValueAreaLineDraw(wpoc, "wPOC", LineKind.WeeklyPoc, null, todayStart));
        }

        if (this.UseQqq)
        {
            if (this.lastQqqVahNq is { } qvah) lines.Add(new ValueAreaLineDraw(qvah, "QQQ->VAH", LineKind.QqqVah, null, todayStart));
            if (this.lastQqqValNq is { } qval) lines.Add(new ValueAreaLineDraw(qval, "QQQ->VAL", LineKind.QqqVal, null, todayStart));
        }

        if (this.KeepPriorDays > 0)
        {
            foreach (var day in this.dailyHistory)
            {
                lines.Add(new ValueAreaLineDraw(day.Vah, "VAH", LineKind.DailyVah, day.EndUtc, day.StartUtc));
                lines.Add(new ValueAreaLineDraw(day.Val, "VAL", LineKind.DailyVal, day.EndUtc, day.StartUtc));
                lines.Add(new ValueAreaLineDraw(day.Poc, "POC", LineKind.DailyPoc, day.EndUtc, day.StartUtc));
            }
        }

        this.lineDrawable = new ValueAreaLineDrawable(lines.ToArray());

        var zoneHalfHeight = this.ZoneHalfHeightTicks * tickSize;
        var zones = new List<ZoneStatusDraw>();
        var fuels = new List<FuelTargetDraw>();

        if (this.priorDayVah is { } vahEdge && this.topZone is { } top)
        {
            var state = StateFor(top, this.lastTopArmed, this.lastTopScore);
            var text = BuildZoneStatusText(
                "VAH", vahEdge, this.lastTopScore, this.lastTopHeldNear, this.lastTopLvn, top,
                this.lastTopArmed, this.lastTopFuel, this.lastTopFuelName, this.lastInSweepWindow);
            zones.Add(new ZoneStatusDraw(vahEdge, zoneHalfHeight, state, IsTop: true, text, todayStart));

            if (this.lastTopArmed && !top.Fired && this.lastTopFuel is { } topFuel)
                fuels.Add(new FuelTargetDraw(topFuel, this.lastTopFuelName, IsTopZone: true));
        }

        if (this.priorDayVal is { } valEdge && this.bottomZone is { } bottom)
        {
            var state = StateFor(bottom, this.lastBottomArmed, this.lastBottomScore);
            var text = BuildZoneStatusText(
                "VAL", valEdge, this.lastBottomScore, this.lastBottomHeldNear, this.lastBottomLvn, bottom,
                this.lastBottomArmed, this.lastBottomFuel, this.lastBottomFuelName, this.lastInSweepWindow);
            zones.Add(new ZoneStatusDraw(valEdge, zoneHalfHeight, state, IsTop: false, text, todayStart));

            if (this.lastBottomArmed && !bottom.Fired && this.lastBottomFuel is { } bottomFuel)
                fuels.Add(new FuelTargetDraw(bottomFuel, this.lastBottomFuelName, IsTopZone: false));
        }

        this.zoneDrawable = new ZoneBoxDrawable(zones.ToArray());
        this.fuelDrawable = new FuelTargetDrawable(fuels.ToArray());
    }

    private static ZoneState StateFor(SweepZoneTracker zone, bool armed, int score)
    {
        if (zone.Fired) return ZoneState.Fired;
        if (zone.IsSwept) return ZoneState.Swept;
        if (armed) return ZoneState.Armed;
        if (score >= 2) return ZoneState.ScoreTwoPlus;
        return ZoneState.EdgeOnly;
    }

    private static string BuildZoneStatusText(
        string edgeLabel, double edge, int score, bool heldNear, bool lvn, SweepZoneTracker zone,
        bool armed, double? fuel, string fuelName, bool inSweepWindow)
    {
        var line1 = $"{edgeLabel} {edge:0.####}  score={score}/3";
        var line2 = $"{(heldNear ? "✓" : "✗")} held-abs  {(lvn ? "✓" : "✗")} LVN";

        string line3;
        if (zone.Fired)
            line3 = "FIRED";
        else if (zone.IsSwept)
            line3 = $"swept, cumΔ={zone.CumulativeDeltaSinceSweep:F0}";
        else if (armed)
            line3 = fuel is { } f ? $"armed, fuel={fuelName} {f:0.####}" : "armed, no fuel in range";
        else if (!inSweepWindow)
            line3 = "outside sweep window";
        else
            line3 = "not armed";

        return $"{line1}\n{line2}\n{line3}";
    }

    // ---- shared helpers -------------------------------------------------------------------------

    private static bool TryReadBar(HistoricalData data, int index, out Bar bar)
    {
        bar = default;

        if (data[index, SeekOriginHistory.Begin] is not HistoryItemBar item)
            return false;

        bar = new Bar(item.TimeLeft, item.Open, item.High, item.Low, item.Close, item.Volume);
        return true;
    }

    private static bool IsInWindow(DateTime local, int startHour, int startMinute, int endHour, int endMinute)
    {
        var startTotal = startHour * 60 + startMinute;
        var endTotal = endHour * 60 + endMinute;
        var nowTotal = local.Hour * 60 + local.Minute;

        if (startTotal == endTotal) return true;
        return startTotal < endTotal
            ? nowTotal >= startTotal && nowTotal < endTotal
            : nowTotal >= startTotal || nowTotal < endTotal;
    }

    private static int IsoWeekKey(DateTime local) => ISOWeek.GetYear(local) * 100 + ISOWeek.GetWeekOfYear(local);

    // ---- paint --------------------------------------------------------------------------------

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

        // ONE shared registry for every overlay's labels, so e.g. a value-area label and a fuel
        // label landing at the same spot respect each other too — same pattern as FinchLiteIndicator.
        var registry = new List<RectangleF>();

        // Layering order: quiet background (value-area lines) first, zone boxes and fuel labels
        // next (state context), absorption dots after that (routine evidence), sweep signals last
        // (rarest, loudest — the actual trigger) — same "quiet backdrop, loud live signal on top"
        // ordering FinchLiteIndicator already establishes between its own features.
        if (this.ShowValueAreaLines)
        {
            try
            {
                this.lineOverlay.Draw(
                    graphics, window, this.lineDrawable,
                    new ValueAreaLineOverlay.Options(
                        this.DailyLineColor, this.DailyPocColor, this.PriorDayLineColor, this.OvernightLineColor,
                        this.AsiaLineColor, this.WeeklyLineColor, this.QqqLineColor, this.KeepPriorDayLabels),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The value-area line overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.ShowZoneBoxes)
        {
            try
            {
                this.zoneOverlay.Draw(
                    graphics, window, this.zoneDrawable,
                    new ZoneBoxOverlay.Options(
                        this.ZoneEdgeOnlyColor, this.ZoneScoreTwoPlusColor, this.ZoneArmedColor, this.ZoneSweptColor,
                        this.ShortColor, this.LongColor),
                    registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The zone-box overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.ShowFuelTargets)
        {
            try
            {
                this.fuelOverlay.Draw(
                    graphics, window, this.fuelDrawable, new FuelTargetOverlay.Options(this.FuelLabelColor), registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The fuel-target overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.ShowAbsorptionMarkers)
        {
            try
            {
                this.absorptionOverlay.Draw(
                    graphics, window, this.absorptionDrawable,
                    new AbsorptionMarkerOverlay.Options(this.ShortColor, this.LongColor));
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The absorption-marker overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (this.ShowSweepSignals)
        {
            try
            {
                this.sweepSignalOverlay.Draw(
                    graphics, window, this.sweepSignalDrawable,
                    new SweepSignalOverlay.Options(this.ShortColor, this.LongColor), registry);
            }
            catch (Exception ex)
            {
                this.overlayFault = $"The sweep-signal overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    public override void Dispose()
    {
        this.retryTimer?.Dispose();
        this.retryTimer = null;
        this.pollTimer?.Dispose();
        this.pollTimer = null;

        this.lineOverlay.Dispose();
        this.zoneOverlay.Dispose();
        this.fuelOverlay.Dispose();
        this.absorptionOverlay.Dispose();
        this.sweepSignalOverlay.Dispose();

        this.hdmNq?.Dispose();
        this.hdmQqq?.Dispose();

        this.faultFont.Dispose();
        this.faultBrush.Dispose();
        this.faultBack.Dispose();

        base.Dispose();
    }
}
