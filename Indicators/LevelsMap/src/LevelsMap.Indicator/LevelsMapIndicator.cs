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
/// NEW TERRITORY FOR AN INDICATOR, UNVERIFIED: pulling a SECOND symbol's live price (QQQ and/or
/// SPY) from inside an Indicator. Every existing indicator in this codebase (Finch-Lite, ORB-IX,
/// OrbLevels) only ever reads `this.Symbol` — the chart's own attached instrument. A Strategy has
/// already done this successfully (oceansStackStrategy's own QQQ cross-check), but no Indicator
/// has. Wrapped in its own try/catch, degrading to "ratio unavailable, Chart-unit levels only"
/// rather than failing the whole indicator if it doesn't resolve the way a Strategy's does.
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
    private Qt.Symbol? qqqSymbol;
    private Qt.Symbol? spySymbol;
    private string? overlayFault;

    [InputParameter("Poll interval (ms)", 1, 100, 5000, 50, 0)]
    public int PollIntervalMs { get; set; } = 1000;

    [InputParameter("Reference: QQQ symbol (for QQQ-unit levels)", 2)]
    public Qt.Symbol? QqqSymbolInput { get; set; }

    [InputParameter("Reference: SPY symbol (for SPY-unit levels)", 3)]
    public Qt.Symbol? SpySymbolInput { get; set; }

    [InputParameter("Levels are quoted in", 4, variants: new object[]
    {
        "Chart's own price (no conversion)", LevelsUnit.Chart,
        "QQQ", LevelsUnit.Qqq,
        "SPY", LevelsUnit.Spy,
    })]
    public LevelsUnit LevelsUnitSetting { get; set; } = LevelsUnit.Chart;

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

    /// <summary>Only hard precondition is the chart's own symbol being attached. QQQ/SPY
    /// resolution (needed only when LevelsUnitSetting isn't Chart) is best-effort — see the class
    /// doc comment's "NEW TERRITORY" note.</summary>
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
            this.qqqSymbol = this.QqqSymbolInput;
            this.spySymbol = this.SpySymbolInput;

            var interval = Math.Max(this.PollIntervalMs, 50);
            this.pollTimer = new Timer(this.OnPollTimer, null, 0, interval);
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
        this.overlay.Dispose();
        base.Dispose();
    }

    // ---- poll / ratio --------------------------------------------------------------------------

    /// <summary>Live ratio: chart price ÷ reference price. Null (and every QQQ/SPY-unit level
    /// simply doesn't draw) whenever the reference symbol isn't set, hasn't resolved, or has no
    /// live price yet — same "degrade, don't crash" discipline as every other best-effort fetch
    /// in this codebase.</summary>
    private double? currentRatio;

    private void OnPollTimer(object? state)
    {
        try
        {
            this.currentRatio = this.ComputeRatio();
            this.RebuildDrawable();
        }
        catch (Exception ex)
        {
            this.overlayFault = $"LevelsMap poll error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private double? ComputeRatio()
    {
        if (this.LevelsUnitSetting == LevelsUnit.Chart || this.symbol is null) return 1.0;

        var reference = this.LevelsUnitSetting == LevelsUnit.Qqq ? this.qqqSymbol : this.spySymbol;
        if (reference is null) return null;

        var refPrice = reference.Last;
        var chartPrice = this.symbol.Last;
        if (refPrice <= 0 || chartPrice <= 0 || double.IsNaN(refPrice) || double.IsNaN(chartPrice)) return null;

        return chartPrice / refPrice;
    }

    private readonly LevelsOverlay overlay = new();
    private volatile LevelsDrawable drawable = LevelsDrawable.Empty;

    private void RebuildDrawable()
    {
        var ratio = this.currentRatio;
        if (ratio is null && this.LevelsUnitSetting != LevelsUnit.Chart)
        {
            this.overlayFault = $"Waiting for a live {this.LevelsUnitSetting} price to compute the ratio...";
            this.drawable = LevelsDrawable.Empty;
            return;
        }

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

        if (ratio is not null) this.overlayFault = null;
        this.drawable = new LevelsDrawable(lines.ToArray(), zones.ToArray());
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
                zones.Add(new LevelZoneDraw(lv.Lo * r, lv.Hi * r, color, label));
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
            zones.Add(new LevelZoneDraw(a, b, this.DpZoneColor, label));
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
            this.overlay.Draw(graphics, window, this.drawable, registry);
        }
        catch (Exception ex)
        {
            this.overlayFault = $"LevelsMap overlay failed to draw: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
