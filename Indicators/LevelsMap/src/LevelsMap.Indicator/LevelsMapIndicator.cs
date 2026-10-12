using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using TradingPlatform.BusinessLayer;
using Qt = TradingPlatform.BusinessLayer;

namespace LevelsMap;

/// <summary>Which reference price a manually-typed level is quoted in.</summary>
public enum LevelsUnit
{
    Chart,
    Qqq,
    Spy,
}

/// <summary>
/// "for those two lets built it into a seperate indicator this one is getting crowded" (the
/// operator's own ask, 2026-10-08) — `priceLevels.pine` and `srm.pine` get their own home instead
/// of piling onto Finch-Lite. This first pass covers `priceLevels.pine`: manually-typed options-
/// flow and dark-pool levels (Zero Gamma, Vol Skew, Call Wall, Put Wall, MVC, dark pool zones and
/// lines), each optionally converted from a QQQ or SPY price into the chart's own price via a
/// live ratio — confirmed via AskUserQuestion: BOTH QQQ and SPY, not either/or, since this whole
/// project trades MES/MGC (SPY-equivalent) as well as NQ (QQQ-equivalent) days.
///
/// SIMPLIFIED FROM THE SOURCE: one global `LevelsUnit` instead of a per-level unit toggle — the
/// source script lets each of its ~13 levels pick its own unit independently, but a realistic
/// day's data (your friend's paste, or a service's dashboard) is quoted in ONE reference all at
/// once, not mixed. Also dropped for this pass: the big reference price grid (every integer QQQ
/// price across a range) and the "Levels Code" paste-parser — the real trading value here is the
/// dark-pool/options levels themselves, not the grid or the paste convenience; both can be added
/// later if wanted.
///
/// `srm.pine` (Session Range Map) is its own separate effort, not started yet — see this file's
/// own Dispose/paint structure for where it would plug in once that pass starts.
///
/// RATIO SOURCE, changed 2026-10-11: originally pulled QQQ/SPY's own live price from a second
/// Quantower `Symbol` the operator supplied — but the operator's own prop-firm futures feed
/// (Rithmic) doesn't carry equity/ETF symbols like QQQ or SPY at all ("i dont have qqq from my
/// prop firm data feed"), so that symbol could never resolve no matter what was typed into it.
/// Switched to the SAME free, no-auth CBOE endpoint already used for live GEX (`CboeOptionsClient`)
/// — it returns QQQ's/SPY's own `current_price` directly from CBOE's own CDN, with no broker feed
/// subscription required at all. See <see cref="SyncReferenceSpotTimer"/>/<see cref="OnReferenceSpotTimer"/>.
///
/// Draws only. Places no orders, reads no account.
/// </summary>
public sealed class LevelsMapIndicator : Qt.Indicator
{
    private static readonly TimeZoneInfo SessionZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const int RetryIntervalMs = 1000;

    private Timer? retryTimer;
    private Timer? pollTimer;
    private Qt.Symbol? symbol;
    private string? overlayFault;

    /// <summary>Informational (not an error) — e.g. the live Vol Skew readout, which isn't a
    /// price level to draw as a line. Kept SEPARATE from <see cref="overlayFault"/> so a routine
    /// status update can never silently overwrite a real fault, or vice versa — both render, one
    /// above the other.</summary>
    private string? statusText;

    private readonly Font faultFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush faultBrush = new(Color.FromArgb(0xFF, 0xC1, 0x07));
    private readonly SolidBrush statusBrush = new(Color.FromArgb(0xB0, 0xBE, 0xC5));
    private readonly SolidBrush faultBack = new(Color.FromArgb(190, 16, 18, 24));

    [InputParameter("Poll interval (ms)", 1, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; } = 1000;

    [InputParameter("Levels are quoted in", 4, variants: new object[]
    {
        "Chart's own price (no conversion)", LevelsUnit.Chart,
        "QQQ", LevelsUnit.Qqq,
        "SPY", LevelsUnit.Spy,
    })]
    public LevelsUnit LevelsUnitSetting { get; set; } = LevelsUnit.Chart;

    /// <summary>How often the QQQ/SPY reference price (needed only when <see cref="LevelsUnitSetting"/>
    /// isn't Chart) is refetched from CBOE — a slower, independent timer since this is a network
    /// call, not something that needs sub-second freshness.</summary>
    [InputParameter("Reference price: refresh interval (seconds)", 7, 15, 3600, 15, 0)]
    public int ReferencePollIntervalSeconds { get; set; } = 30;

    /// <summary>Operator's own ask (2026-10-08): "is there a way to auto fill these levels
    /// instead of me doing them one by one." Paste a friend's/service's Levels Code here — same
    /// format priceLevels.pine's own paste feature accepts (see LevelsCodeParser's own doc
    /// comment for a confirmed-working example) — and it REPLACES every individually-typed level
    /// below, same "paste wins" behavior as the source script. Leave empty to use the individual
    /// fields instead.</summary>
    [InputParameter("Levels Code (paste here to auto-fill — replaces the fields below)", 6)]
    public string LevelsCode { get; set; } = "";

    [InputParameter("Band half-width (chart points)", 5, 0, 50, 0.25, 2)]
    public double BandHalfWidth { get; set; } = 0.5;

    /// <summary>Operator's own ask (2026-10-12): "i just want the lines and have them as thick
    /// lines instead of boxes." Set "Band half-width" above to 0 to remove the soft fill entirely
    /// (every single-price level AND a dark-pool zone collapsed to lines already skips the fill
    /// when its half-width is 0) — this controls the weight of the solid line/s that remain.</summary>
    [InputParameter("Line thickness (px)", 8, 0.5, 10, 0.5, 1)]
    public double LineThickness { get; set; } = 3.0;

    // ---- option levels (single price each) ------------------------------------------------------

    [InputParameter("Zero Gamma: enabled", 10)]
    public bool ZeroGammaEnabled { get; set; } = true;

    [InputParameter("Zero Gamma: price", 11, 0, 1000000, 0.01, 2)]
    public double ZeroGammaPrice { get; set; }

    [InputParameter("Zero Gamma: colour", 12)]
    public Color ZeroGammaColor { get; set; } = Color.FromArgb(0xFF, 0xD6, 0x00);

    [InputParameter("Vol Skew: enabled", 14)]
    public bool VolSkewEnabled { get; set; } = true;

    [InputParameter("Vol Skew: price", 15, 0, 1000000, 0.01, 2)]
    public double VolSkewPrice { get; set; }

    [InputParameter("Vol Skew: colour", 16)]
    public Color VolSkewColor { get; set; } = Color.FromArgb(0xFF, 0x69, 0xB4);

    [InputParameter("Call Wall: enabled", 18)]
    public bool CallWallEnabled { get; set; } = true;

    [InputParameter("Call Wall: price", 19, 0, 1000000, 0.01, 2)]
    public double CallWallPrice { get; set; }

    [InputParameter("Call Wall: colour", 20)]
    public Color CallWallColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Put Wall: enabled", 22)]
    public bool PutWallEnabled { get; set; } = true;

    [InputParameter("Put Wall: price", 23, 0, 1000000, 0.01, 2)]
    public double PutWallPrice { get; set; }

    [InputParameter("Put Wall: colour", 24)]
    public Color PutWallColor { get; set; } = Color.FromArgb(0xF2, 0x36, 0x45);

    [InputParameter("MVC: enabled", 26)]
    public bool MvcEnabled { get; set; } = true;

    [InputParameter("MVC: price", 27, 0, 1000000, 0.01, 2)]
    public double MvcPrice { get; set; }

    [InputParameter("MVC: colour", 28)]
    public Color MvcColor { get; set; } = Color.White;

    // ---- dark pool zones (low-high range + $B notional) -------------------------------------------

    /// <summary>Operator's own ask (2026-10-12), from their TradingView reference: "i can hide the
    /// purple boxes for the dark pools and just make the line." Ported from `priceLevels.pine`'s
    /// own `showPoolBox` — off draws two thin lines at the zone's low/high instead of a filled box,
    /// same "DARK POOL" label at the midpoint either way.</summary>
    [InputParameter("Dark pool zones: show as filled box (off = two thin lines instead)", 38)]
    public bool DpZoneShowBox { get; set; } = true;

    [InputParameter("Dark pool zone colour", 39)]
    public Color DpZoneColor { get; set; } = Color.FromArgb(0x50, 0xC8, 0xE6);

    [InputParameter("Dark pool zone 1: enabled", 40)]
    public bool DpZone1Enabled { get; set; } = true;
    [InputParameter("Dark pool zone 1: low", 41, 0, 1000000, 0.01, 2)]
    public double DpZone1Low { get; set; }
    [InputParameter("Dark pool zone 1: high", 42, 0, 1000000, 0.01, 2)]
    public double DpZone1High { get; set; }
    [InputParameter("Dark pool zone 1: $B notional", 43, 0, 100000, 0.01, 2)]
    public double DpZone1Notional { get; set; }

    [InputParameter("Dark pool zone 2: enabled", 44)]
    public bool DpZone2Enabled { get; set; } = true;
    [InputParameter("Dark pool zone 2: low", 45, 0, 1000000, 0.01, 2)]
    public double DpZone2Low { get; set; }
    [InputParameter("Dark pool zone 2: high", 46, 0, 1000000, 0.01, 2)]
    public double DpZone2High { get; set; }
    [InputParameter("Dark pool zone 2: $B notional", 47, 0, 100000, 0.01, 2)]
    public double DpZone2Notional { get; set; }

    [InputParameter("Dark pool zone 3: enabled", 48)]
    public bool DpZone3Enabled { get; set; } = true;
    [InputParameter("Dark pool zone 3: low", 49, 0, 1000000, 0.01, 2)]
    public double DpZone3Low { get; set; }
    [InputParameter("Dark pool zone 3: high", 50, 0, 1000000, 0.01, 2)]
    public double DpZone3High { get; set; }
    [InputParameter("Dark pool zone 3: $B notional", 51, 0, 100000, 0.01, 2)]
    public double DpZone3Notional { get; set; }

    // ---- dark pool lines (single price + $B notional) ----------------------------------------------

    [InputParameter("Dark pool line colour", 59)]
    public Color DpLineColor { get; set; } = Color.FromArgb(0x00, 0xE5, 0xFF);

    [InputParameter("Dark pool line 1: enabled", 60)]
    public bool DpLine1Enabled { get; set; } = true;
    [InputParameter("Dark pool line 1: price", 61, 0, 1000000, 0.01, 2)]
    public double DpLine1Price { get; set; }
    [InputParameter("Dark pool line 1: $B notional", 62, 0, 100000, 0.01, 2)]
    public double DpLine1Notional { get; set; }

    [InputParameter("Dark pool line 2: enabled", 63)]
    public bool DpLine2Enabled { get; set; } = true;
    [InputParameter("Dark pool line 2: price", 64, 0, 1000000, 0.01, 2)]
    public double DpLine2Price { get; set; }
    [InputParameter("Dark pool line 2: $B notional", 65, 0, 100000, 0.01, 2)]
    public double DpLine2Notional { get; set; }

    [InputParameter("Dark pool line 3: enabled", 66)]
    public bool DpLine3Enabled { get; set; } = true;
    [InputParameter("Dark pool line 3: price", 67, 0, 1000000, 0.01, 2)]
    public double DpLine3Price { get; set; }
    [InputParameter("Dark pool line 3: $B notional", 68, 0, 100000, 0.01, 2)]
    public double DpLine3Notional { get; set; }

    // ---- live GEX (Zero Gamma / Call Wall / Put Wall / Magnet / Vol Skew) — operator's own ask,
    // 2026-10-09: free CBOE data (github.com/itsfabtrading/gex-multi's own public endpoint),
    // computed locally via GexEngine rather than paying for Massive's options tier. See
    // GexEngine's own doc comment for the methodology/attribution.

    [InputParameter("Live GEX: enabled", 70)]
    public bool GexEnabled { get; set; }

    [InputParameter("Live GEX: underlying ticker (e.g. QQQ, SPY)", 71)]
    public string GexUnderlying { get; set; } = "QQQ";

    [InputParameter("Live GEX: max days to expiry included", 72, 1, 365, 1, 0)]
    public int GexMaxDaysToExpiry { get; set; } = 45;

    [InputParameter("Live GEX: refresh interval (seconds)", 73, 15, 3600, 15, 0)]
    public int GexPollIntervalSeconds { get; set; } = 60;

    [InputParameter("Live GEX: risk-free rate (for Black-Scholes fallback)", 74, 0, 0.2, 0.001, 3)]
    public double GexRiskFreeRate { get; set; } = 0.05;

    [InputParameter("Live GEX: show 0DTE-only sublevels", 75)]
    public bool GexShowZeroDte { get; set; } = true;

    [InputParameter("Live GEX: Zero Gamma colour", 76)]
    public Color GexZeroGammaColor { get; set; } = Color.FromArgb(0xFF, 0xD6, 0x00);

    [InputParameter("Live GEX: Call Wall colour", 77)]
    public Color GexCallWallColor { get; set; } = Color.FromArgb(0x00, 0xE6, 0x76);

    [InputParameter("Live GEX: Put Wall colour", 78)]
    public Color GexPutWallColor { get; set; } = Color.FromArgb(0xF2, 0x36, 0x45);

    [InputParameter("Live GEX: Magnet colour", 79)]
    public Color GexMagnetColor { get; set; } = Color.FromArgb(0xB0, 0x80, 0xFF);

    public LevelsMapIndicator()
    {
        this.Name = "Levels Map";
        this.Description =
            "Manually-typed options-flow and dark-pool levels (Zero Gamma, Vol Skew, Call Wall, "
            + "Put Wall, MVC, dark pool zones/lines), optionally converted from QQQ or SPY price "
            + "through a live ratio. Ported from priceLevels.pine. Draws only, places no orders.";
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
            if (this.pollTimer is not null) return;

            if (this.TryInitialise())
            {
                this.retryTimer?.Dispose();
                this.retryTimer = null;
            }
        }
    }

    /// <summary>Only hard precondition is the chart's own symbol being attached. The QQQ/SPY
    /// reference price (needed only when LevelsUnitSetting isn't Chart) comes from CBOE, not a
    /// broker symbol — see the class doc comment's "RATIO SOURCE" note.</summary>
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

            var interval = Math.Max(this.PollIntervalMs, 50);
            this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);

            // gexTimer / refSpotTimer are started/stopped reactively from OnPollTimer's own
            // Sync*Timer() calls, not here — see those methods' doc comments for why.

            return true;
        }
        catch (Exception ex)
        {
            this.overlayFault = $"LevelsMap failed to start: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public override void Dispose()
    {
        this.retryTimer?.Dispose();
        this.pollTimer?.Dispose();
        this.gexTimer?.Dispose();
        this.refSpotTimer?.Dispose();
        this.overlay.Dispose();
        this.faultFont.Dispose();
        this.faultBrush.Dispose();
        this.statusBrush.Dispose();
        this.faultBack.Dispose();
        base.Dispose();
    }

    // ---- live GEX fetch/compute (its own, slower timer — options chain fetches are heavier than
    // the main poll and don't need sub-second freshness; open interest itself only updates once a
    // day) ------------------------------------------------------------------------------------

    private Timer? gexTimer;
    private volatile GexResult? gexResult;
    private double gexSpot;

    /// <summary>Own field, deliberately NOT <see cref="overlayFault"/> — <see cref="RebuildDrawable"/>
    /// runs on a separate ~1s timer and used to stomp a GEX fetch error back to null almost
    /// immediately after this set it, making a persistent fetch failure invisible in practice.</summary>
    private string? gexFault;

    /// <summary>Non-blocking — set only while <see cref="RebuildDrawable"/>'s own ratio dependency
    /// hasn't resolved yet; see that method's doc comment for the bug this replaced.</summary>
    private string? ratioFault;

    private void OnGexTimer(object? state)
    {
        try
        {
            var underlying = this.GexUnderlying?.Trim();
            if (string.IsNullOrEmpty(underlying))
            {
                this.gexFault = "Live GEX: underlying ticker is empty.";
                return;
            }

            var chain = CboeOptionsClient.FetchChain(underlying, Math.Max(1, this.GexMaxDaysToExpiry));
            this.gexSpot = chain.Spot;
            this.gexResult = GexEngine.Compute(chain.Contracts, chain.Spot, chain.AsOfUtc, this.GexRiskFreeRate);
            this.gexFault = null;
        }
        catch (Exception ex)
        {
            this.gexFault = $"Live GEX fetch failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ---- poll / ratio --------------------------------------------------------------------------

    /// <summary>Null (every QQQ/SPY-unit level simply doesn't draw) until the first successful
    /// CBOE reference fetch. For Chart-unit, fixed at 1.0. For QQQ/SPY-unit, set ONLY inside
    /// <see cref="OnReferenceSpotTimer"/> — see that method's doc comment for why this must NOT be
    /// recomputed every poll tick.</summary>
    private double? currentRatio;

    private void OnPollTimer(object? state)
    {
        try
        {
            this.SyncGexTimer();
            this.SyncReferenceSpotTimer();
            if (this.LevelsUnitSetting == LevelsUnit.Chart) this.currentRatio = 1.0;
            this.RebuildDrawable();
        }
        catch (Exception ex)
        {
            this.overlayFault = $"LevelsMap poll error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ---- QQQ/SPY reference price, for the ratio only — its own slower CBOE-backed timer, same
    // reactive start/stop shape as SyncGexTimer below (and for the same reason: this must react to
    // LevelsUnitSetting changing on an already-running instance, not just at attach time). See the
    // class doc comment's "RATIO SOURCE" note for why this isn't a broker Symbol anymore.

    private Timer? refSpotTimer;
    private int refSpotTimerIntervalMs;
    private double? referenceSpot; // double? can't be volatile (CS0677) — same tolerance as gexSpot above
    private string? refSpotFault;

    private void SyncReferenceSpotTimer()
    {
        if (this.LevelsUnitSetting == LevelsUnit.Chart)
        {
            if (this.refSpotTimer is null) return;
            this.refSpotTimer.Dispose();
            this.refSpotTimer = null;
            this.referenceSpot = null;
            this.refSpotFault = null;
            return;
        }

        var wantedInterval = Math.Max(this.ReferencePollIntervalSeconds, 15) * 1000;
        if (this.refSpotTimer is not null && this.refSpotTimerIntervalMs == wantedInterval) return;

        this.refSpotTimer?.Dispose();
        this.refSpotTimerIntervalMs = wantedInterval;
        this.refSpotTimer = new Timer(this.OnReferenceSpotTimer, null, 0, wantedInterval);
    }

    /// <summary>Reuses <see cref="CboeOptionsClient.FetchChain"/> rather than a dedicated
    /// price-only endpoint — CBOE's CDN doesn't expose one, it returns the full chain either way,
    /// so a 1-day expiry filter just keeps the CLIENT-side parse cheap (almost every contract gets
    /// skipped immediately on the expiration check) without a second code path to maintain.
    ///
    /// Operator report, 2026-10-12: "the zero gamma and dark pools keep moving though is that
    /// normal" — they were NOT supposed to drift, but <see cref="currentRatio"/> used to be
    /// recomputed every ~1s poll tick as `this.symbol.Last / this.referenceSpot`: the chart's own
    /// price updates every tick while `referenceSpot` only refreshes once per
    /// <see cref="ReferencePollIntervalSeconds"/> (default 30s), so every single MNQ tick nudged
    /// the ratio — and with it every pasted level — even though nothing about the real QQQ price
    /// had changed. Fixed by sampling BOTH prices at the SAME instant, right here, only when a
    /// fresh reference price actually arrives, and holding that ratio perfectly fixed until the
    /// next refresh instead of re-deriving it from two time-mismatched prices every tick.</summary>
    private void OnReferenceSpotTimer(object? state)
    {
        try
        {
            var ticker = this.LevelsUnitSetting switch
            {
                LevelsUnit.Qqq => "QQQ",
                LevelsUnit.Spy => "SPY",
                _ => null,
            };
            if (ticker is null) return;

            var chain = CboeOptionsClient.FetchChain(ticker, 1);
            if (chain.Spot <= 0)
            {
                this.refSpotFault = $"CBOE returned no usable {ticker} price.";
                return;
            }

            var chartPrice = this.symbol?.Last ?? 0;
            if (chartPrice <= 0 || double.IsNaN(chartPrice))
            {
                this.refSpotFault = "Waiting for a live chart price to pair with the reference fetch.";
                return;
            }

            this.referenceSpot = chain.Spot;
            this.currentRatio = chartPrice / chain.Spot;
            this.refSpotFault = null;
        }
        catch (Exception ex)
        {
            this.refSpotFault = $"Reference price ({this.LevelsUnitSetting}) fetch failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>`TryInitialise()` only starts <see cref="gexTimer"/> if `GexEnabled` was already
    /// true AT ATTACH TIME — flipping "Live GEX: enabled" on later via the settings panel on an
    /// already-running instance never re-runs `TryInitialise()`, so the timer would otherwise never
    /// start and `gexResult` would stay null forever (root cause of "why are none of the levels
    /// populated", 2026-10-11). Called every poll tick (already ~1s) so a later settings change is
    /// picked up within one poll interval, same self-healing/reactive discipline used elsewhere in
    /// this codebase. Also tears the timer down if the setting is switched back off, and restarts
    /// it if the refresh-interval setting changes while running.</summary>
    private int gexTimerIntervalMs;

    private void SyncGexTimer()
    {
        if (!this.GexEnabled)
        {
            if (this.gexTimer is null) return;
            this.gexTimer.Dispose();
            this.gexTimer = null;
            this.gexResult = null;
            return;
        }

        var wantedInterval = Math.Max(this.GexPollIntervalSeconds, 15) * 1000;
        if (this.gexTimer is not null && this.gexTimerIntervalMs == wantedInterval) return;

        this.gexTimer?.Dispose();
        this.gexTimerIntervalMs = wantedInterval;
        this.gexTimer = new Timer(this.OnGexTimer, null, 0, wantedInterval);
    }

    private readonly LevelsOverlay overlay = new();
    private volatile LevelsDrawable drawable = LevelsDrawable.Empty;

    /// <summary>Operator report, 2026-10-11: "im not seeing any gex levels or anything regarding
    /// levels other than vwap" — i.e. NOTHING from this indicator drew, not just GEX. Root cause:
    /// this method used to `return` BEFORE building anything (manual fields, pasted code, AND GEX)
    /// whenever `LevelsUnitSetting` wasn't Chart and the QQQ/SPY ratio hadn't resolved yet — one
    /// unresolved dependency silently blanked the entire indicator. Now every source always builds
    /// (falling back to a 1:1 ratio when the real one isn't available yet), and the ratio-missing
    /// state surfaces as its own non-blocking status line instead of hiding everything.</summary>
    private void RebuildDrawable()
    {
        var ratio = this.currentRatio;
        var ratioMissing = ratio is null && this.LevelsUnitSetting != LevelsUnit.Chart;
        this.ratioFault = ratioMissing
            ? $"Waiting for a live {this.LevelsUnitSetting} price to compute the ratio — levels below use a 1:1 fallback until it resolves."
            : null;

        var r = ratio ?? 1.0;
        var half = this.BandHalfWidth * r;
        var lines = new List<LevelLineDraw>();
        var zones = new List<LevelZoneDraw>();

        var pastedCode = this.LevelsCode;
        if (!string.IsNullOrWhiteSpace(pastedCode))
        {
            this.BuildFromParsedCode(pastedCode, r, half, lines, zones);
        }
        else
        {
            this.BuildFromFields(r, half, lines, zones);
        }

        if (this.GexEnabled)
            this.AddGexLines(half, lines);

        this.drawable = new LevelsDrawable(lines.ToArray(), zones.ToArray());
    }

    /// <summary>GEX levels are already computed in the underlying's own real price (CBOE's
    /// `current_price` for the SAME ticker the chain was fetched for) — unlike the manual
    /// fields/pasted code, these do NOT go through the QQQ/SPY→chart ratio a second time; they
    /// already resolve to a usable chart-relative distance via the chain's own spot, converted
    /// through the SAME ratio path as everything else by construction (GexUnderlying should match
    /// LevelsUnitSetting's own QQQ/SPY choice for this to land in the right place on the chart).</summary>
    private void AddGexLines(double half, List<LevelLineDraw> lines)
    {
        var result = this.gexResult;
        if (result is not { } r) return;

        var ratio = this.currentRatio ?? 1.0;

        void Add(double? price, Color color, string name)
        {
            if (price is not { } p) return;
            lines.Add(new LevelLineDraw(p * ratio, half, color, $"{name} {p:0.##}"));
        }

        Add(r.FullChain.ZeroGamma, this.GexZeroGammaColor, "ZERO GAMMA");
        Add(r.FullChain.CallWall, this.GexCallWallColor, "CALL WALL");
        Add(r.FullChain.PutWall, this.GexPutWallColor, "PUT WALL");
        Add(r.FullChain.Magnet, this.GexMagnetColor, "MAGNET");

        if (this.GexShowZeroDte)
        {
            Add(r.ZeroDte.ZeroGamma, this.GexZeroGammaColor, "0DTE ZERO GAMMA");
            Add(r.ZeroDte.CallWall, this.GexCallWallColor, "0DTE CALL WALL");
            Add(r.ZeroDte.PutWall, this.GexPutWallColor, "0DTE PUT WALL");
            Add(r.ZeroDte.Magnet, this.GexMagnetColor, "0DTE MAGNET");
        }

        this.statusText = r.VolSkew is { } skew
            ? $"Vol Skew (live, {this.GexUnderlying}): {skew * 100:+0.##;-0.##}%"
            : null;
    }

    /// <summary>Operator's own ask: "is there a way to auto fill these levels instead of me doing
    /// them one by one." A pasted Levels Code REPLACES every individually-typed field below, same
    /// "paste wins" behavior as the source script — and, unlike the fixed 5+3+3 individual fields,
    /// draws AS MANY levels as were actually pasted, no cap. Each entry's own "@" (IsChart)
    /// overrides the global ratio for just that one entry, same as the source.</summary>
    private void BuildFromParsedCode(string code, double globalRatio, double globalHalf, List<LevelLineDraw> lines, List<LevelZoneDraw> zones)
    {
        var parsed = LevelsCodeParser.Parse(code);

        if (parsed.Count == 0)
        {
            this.overlayFault = "Levels Code pasted but not recognized — check the format.";
            return;
        }

        foreach (var lv in parsed)
        {
            var r = lv.IsChart ? 1.0 : globalRatio;
            var half = lv.IsChart ? this.BandHalfWidth : globalHalf;

            var (color, name) = lv.Kind switch
            {
                "ZG" => (this.ZeroGammaColor, "ZERO GAMMA"),
                "VS" => (this.VolSkewColor, "VOL SKEW"),
                "CW" => (this.CallWallColor, "CALL WALL"),
                "PW" => (this.PutWallColor, "PUT WALL"),
                "MVC" => (this.MvcColor, "MVC"),
                "DL" => (this.DpLineColor, "DP"),
                _ => (this.DpZoneColor, "DARK POOL"),
            };

            if (lv.Kind == "DZ")
            {
                var label = lv.Notional > 0 ? $"{name} {lv.Notional:0.##}B" : name;
                zones.Add(new LevelZoneDraw(lv.Lo * r, lv.Hi * r, color, label, this.DpZoneShowBox));
            }
            else
            {
                var price = lv.Lo; // Lo == Hi for every non-DZ kind, per the parser's own contract
                var label = lv.Notional > 0 ? $"{name} {price:0.##} · {lv.Notional:0.##}B" : $"{name} {price:0.##}";
                lines.Add(new LevelLineDraw(price * r, half, color, label));
            }
        }
    }

    private void BuildFromFields(double r, double half, List<LevelLineDraw> lines, List<LevelZoneDraw> zones)
    {
        void AddLine(bool enabled, double price, Color color, string name, double notional)
        {
            if (!enabled || price <= 0) return;
            var converted = price * r;
            var label = notional > 0 ? $"{name} {price:0.##} · {notional:0.##}B" : $"{name} {price:0.##}";
            lines.Add(new LevelLineDraw(converted, half, color, label));
        }

        AddLine(this.ZeroGammaEnabled, this.ZeroGammaPrice, this.ZeroGammaColor, "ZERO GAMMA", 0);
        AddLine(this.VolSkewEnabled, this.VolSkewPrice, this.VolSkewColor, "VOL SKEW", 0);
        AddLine(this.CallWallEnabled, this.CallWallPrice, this.CallWallColor, "CALL WALL", 0);
        AddLine(this.PutWallEnabled, this.PutWallPrice, this.PutWallColor, "PUT WALL", 0);
        AddLine(this.MvcEnabled, this.MvcPrice, this.MvcColor, "MVC", 0);

        AddLine(this.DpLine1Enabled, this.DpLine1Price, this.DpLineColor, "DP", this.DpLine1Notional);
        AddLine(this.DpLine2Enabled, this.DpLine2Price, this.DpLineColor, "DP", this.DpLine2Notional);
        AddLine(this.DpLine3Enabled, this.DpLine3Price, this.DpLineColor, "DP", this.DpLine3Notional);

        void AddZone(bool enabled, double lo, double hi, double notional)
        {
            if (!enabled || lo <= 0 || hi <= 0) return;
            var a = Math.Min(lo, hi) * r;
            var b = Math.Max(lo, hi) * r;
            var label = notional > 0 ? $"DARK POOL {notional:0.##}B" : "DARK POOL";
            zones.Add(new LevelZoneDraw(a, b, this.DpZoneColor, label, this.DpZoneShowBox));
        }

        AddZone(this.DpZone1Enabled, this.DpZone1Low, this.DpZone1High, this.DpZone1Notional);
        AddZone(this.DpZone2Enabled, this.DpZone2Low, this.DpZone2High, this.DpZone2Notional);
        AddZone(this.DpZone3Enabled, this.DpZone3Low, this.DpZone3High, this.DpZone3Notional);
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
            this.overlay.Draw(graphics, window, this.drawable, registry, (float)this.LineThickness);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"LevelsMap overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }

        var pane = window.ClientRectangle;
        var y = pane.Top + 4f;

        void DrawLine(string? text, SolidBrush brush)
        {
            if (text is not { Length: > 0 }) return;
            var size = graphics.MeasureString(text, this.faultFont);
            var rect = new RectangleF(pane.Left + 4f, y, size.Width + 8f, size.Height + 4f);
            graphics.FillRectangle(this.faultBack, rect);
            graphics.DrawString(text, this.faultFont, brush, rect.Left + 4f, rect.Top + 2f);
            y += rect.Height + 2f;
        }

        DrawLine(this.overlayFault, this.faultBrush);
        DrawLine(this.gexFault, this.faultBrush);
        DrawLine(this.refSpotFault, this.faultBrush);
        DrawLine(this.ratioFault, this.statusBrush);
        DrawLine(this.statusText, this.statusBrush);
    }
}
