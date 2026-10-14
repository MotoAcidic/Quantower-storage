using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace FinchLite;

/// <summary>Which recurring range this period tracks — Asia/London/NY share the same machinery as
/// Week/Month, just with a different budget formula and window-detection rule.</summary>
internal enum SrmPeriodKind
{
    Asia,
    London,
    Ny,
    Week,
    Month,
}

/// <summary>A finished period's own frozen POC/VAH/VAL/High/Low — null until at least one bar's
/// worth of data has been fed.</summary>
internal sealed class SrmLevels
{
    public double? Poc;
    public double? Vah;
    public double? Val;
    public double? High;
    public double? Low;
    public DateTime FinishedUtc;
}

/// <summary>One grouped row of a volume-by-price histogram, ready to draw as a box — grouping
/// keeps the number of drawn boxes bounded regardless of how fine the underlying bin resolution is
/// (same purpose as `srm.pine`'s own `profMaxRows` grouping in `drawProf`).</summary>
internal readonly record struct SrmHistogramRow(double Lo, double Hi, double RelativeVolume, bool InValueArea, bool IsPoc);

/// <summary>One naked (untested) session/week/month high or low — ported from `srm.pine`'s own
/// `Lvl` type and its "extend until touched" behavior. Presentation-free by design (no colour/label
/// here) — <see cref="FinchLiteIndicator"/> maps <see cref="PeriodKind"/> to a colour the same way
/// it already does for every other SRM line, same "pure engine" split as the rest of this file.</summary>
internal sealed class SrmNakedLevel
{
    public SrmPeriodKind PeriodKind { get; }
    public bool IsHigh { get; }
    public double Price { get; }
    public DateTime StartUtc { get; }
    public DateTime? TouchedUtc { get; private set; }

    public SrmNakedLevel(SrmPeriodKind periodKind, bool isHigh, double price, DateTime startUtc)
    {
        this.PeriodKind = periodKind;
        this.IsHigh = isHigh;
        this.Price = price;
        this.StartUtc = startUtc;
    }

    public void MarkTouched(DateTime utc) => this.TouchedUtc ??= utc;
}

/// <summary>
/// One Asia/London/NY/Week/Month range — ported from `srm.pine`'s own `Sess` type and its
/// `start`/`update`/`finish` methods. Volume-by-price binning distributes each CLOSED BAR's own
/// volume evenly across the price bins its high-low range spans — the same approach the source
/// script itself uses (it has no tick-level access inside this indicator either; `volume` there is
/// bar volume, not a trade-print stream), so this is a faithful port, not a simplification forced
/// by Finch-Lite's own lack of historical tick backfill.
/// </summary>
internal sealed class SrmPeriod
{
    public readonly SrmPeriodKind Kind;
    public readonly string Name;

    public bool Active { get; private set; }
    public DateTime StartUtc { get; private set; }
    public DateTime ExpectedEndUtc { get; private set; }
    public double OpenPrice { get; private set; }
    public double High { get; private set; }
    public double Low { get; private set; }
    public double BudgetPts { get; private set; }
    public double BinWidth { get; private set; }

    /// <summary>Updated every bar while active — the adaptive (or fixed) completion projection,
    /// same "never sits inside the range" guarantee as the source script.</summary>
    public double ProjUp { get; private set; }
    public double ProjDn { get; private set; }

    /// <summary>The MOST RECENTLY finished period's own levels — stays populated after Finish()
    /// until the next Start(), so a caller can still show "prior week POC" etc. after the period
    /// itself has closed, same as the source script's own `pwPoc`/`pmPoc` carry-through.</summary>
    public SrmLevels LastFinished { get; } = new();

    private readonly Dictionary<int, double> volumeByPrice = new();

    public SrmPeriod(SrmPeriodKind kind, string name)
    {
        this.Kind = kind;
        this.Name = name;
    }

    public void Start(DateTime startUtc, DateTime expectedEndUtc, double openPrice, double budgetPts, int rows, double tickSize)
    {
        this.Active = true;
        this.StartUtc = startUtc;
        this.ExpectedEndUtc = expectedEndUtc;
        this.OpenPrice = openPrice;
        this.High = openPrice;
        this.Low = openPrice;
        this.BudgetPts = budgetPts;
        this.BinWidth = Math.Max(tickSize, budgetPts / Math.Max(1, rows));
        this.volumeByPrice.Clear();
        this.ProjUp = openPrice + budgetPts;
        this.ProjDn = openPrice - budgetPts;
    }

    public void Update(DateTime barCloseUtc, double high, double low, double volume, double kProj, double extMult, bool adaptive)
    {
        if (!this.Active) return;

        if (high > this.High) this.High = high;
        if (low < this.Low) this.Low = low;

        if (volume > 0 && this.BinWidth > 0)
        {
            var b0 = (int)Math.Floor((low - this.OpenPrice) / this.BinWidth);
            var b1 = (int)Math.Floor((high - this.OpenPrice) / this.BinWidth);
            if (b1 < b0) (b0, b1) = (b1, b0);
            var vpb = volume / (b1 - b0 + 1);
            for (var k = b0; k <= b1; k++)
                this.volumeByPrice[k] = this.volumeByPrice.TryGetValue(k, out var existing) ? existing + vpb : vpb;
        }

        var up = this.Low + kProj * this.BudgetPts;
        var dn = this.High - kProj * this.BudgetPts;

        if (adaptive && this.ExpectedEndUtc > this.StartUtc)
        {
            var lenMs = (this.ExpectedEndUtc - this.StartUtc).TotalMilliseconds;
            var f = Math.Max(0.0, Math.Min(1.0, (barCloseUtc - this.StartUtc).TotalMilliseconds / lenMs));
            var e = SrmAdaptiveCurves.ExtAllow(this.Kind, f) * extMult * this.BudgetPts;
            up = Math.Max(up, this.High + e);
            dn = Math.Min(dn, this.Low - e);
        }

        this.ProjUp = up;
        this.ProjDn = dn;
    }

    /// <summary>Volume-area expansion from the heaviest bin outward until <paramref name="vaPct"/>
    /// of total volume is enclosed — identical algorithm to `srm.pine`'s own `drawProf`.</summary>
    public (double? Poc, double? Vah, double? Val) ComputeProfile(double vaPct)
    {
        if (this.volumeByPrice.Count == 0) return (null, null, null);

        var keys = new List<int>(this.volumeByPrice.Keys);
        keys.Sort();
        var vols = new double[keys.Count];
        double total = 0, max = 0;
        var pi = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            var v = this.volumeByPrice[keys[i]];
            vols[i] = v;
            total += v;
            if (v > max) { max = v; pi = i; }
        }

        var a = pi;
        var b = pi;
        var acc = vols[pi];
        var last = keys.Count - 1;
        while (acc < total * vaPct / 100.0 && (a > 0 || b < last))
        {
            var upV = b < last ? vols[b + 1] : -1.0;
            var dnV = a > 0 ? vols[a - 1] : -1.0;
            if (upV >= dnV) { b++; acc += upV; }
            else { a--; acc += dnV; }
        }

        var poc = this.OpenPrice + (keys[pi] + 0.5) * this.BinWidth;
        var vah = this.OpenPrice + (keys[b] + 1) * this.BinWidth;
        var val = this.OpenPrice + keys[a] * this.BinWidth;
        return (poc, vah, val);
    }

    public void ForceInactive() => this.Active = false;

    public void Finish(double vaPct, DateTime finishedUtc)
    {
        if (!this.Active) return;

        var (poc, vah, val) = this.ComputeProfile(vaPct);
        this.LastFinished.Poc = poc;
        this.LastFinished.Vah = vah;
        this.LastFinished.Val = val;
        this.LastFinished.High = this.High;
        this.LastFinished.Low = this.Low;
        this.LastFinished.FinishedUtc = finishedUtc;
        this.Active = false;
    }

    /// <summary>Groups the fine-resolution volume-by-price bins into at most
    /// <paramref name="maxGroups"/> rows for DRAWING — math stays at full `BinWidth` resolution
    /// (<see cref="ComputeProfile"/> reads the ungrouped bins), only the visual box count is capped,
    /// same split `srm.pine`'s own `drawProf` makes between its math rows and `profMaxRows`.</summary>
    public List<SrmHistogramRow> GetHistogramRows(double vaPct, int maxGroups)
    {
        var rows = new List<SrmHistogramRow>();
        if (this.volumeByPrice.Count == 0) return rows;

        var keys = new List<int>(this.volumeByPrice.Keys);
        keys.Sort();
        var vols = new double[keys.Count];
        double total = 0, max = 0;
        var pi = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            var v = this.volumeByPrice[keys[i]];
            vols[i] = v;
            total += v;
            if (v > max) { max = v; pi = i; }
        }

        var a = pi;
        var b = pi;
        var acc = vols[pi];
        var last = keys.Count - 1;
        while (acc < total * vaPct / 100.0 && (a > 0 || b < last))
        {
            var upV = b < last ? vols[b + 1] : -1.0;
            var dnV = a > 0 ? vols[a - 1] : -1.0;
            if (upV >= dnV) { b++; acc += upV; }
            else { a--; acc += dnV; }
        }

        var groupSize = Math.Max(1, (int)Math.Ceiling((last + 1) / (double)maxGroups));
        var groupMax = 0.0;
        for (var i0 = 0; i0 <= last; i0 += groupSize)
        {
            var sv = 0.0;
            for (var j = i0; j <= Math.Min(last, i0 + groupSize - 1); j++) sv += vols[j];
            groupMax = Math.Max(groupMax, sv);
        }
        if (groupMax <= 0) return rows;

        for (var i0 = 0; i0 <= last; i0 += groupSize)
        {
            var i1 = Math.Min(last, i0 + groupSize - 1);
            var sv = 0.0;
            for (var j = i0; j <= i1; j++) sv += vols[j];

            var lo = this.OpenPrice + (keys[i0] * this.BinWidth);
            var hi = this.OpenPrice + ((keys[i1] + 1) * this.BinWidth);
            var mid = (i0 + i1) / 2;
            var isPoc = pi >= i0 && pi <= i1;
            var inVa = mid >= a && mid <= b;

            rows.Add(new SrmHistogramRow(lo, hi, sv / groupMax, inVa, isPoc));
        }

        return rows;
    }
}

/// <summary>Adaptive completion-projection calibration curves — exact copies of `srm.pine`'s own
/// `extAllow` tables (85th-percentile remaining per-side extension vs elapsed fraction of the
/// period, fit on 2-yr NQ hourly for sessions / 12-yr daily for week/month — see that script's own
/// "Evidence behind the defaults" footer for the methodology).</summary>
internal static class SrmAdaptiveCurves
{
    private static readonly double[] MonthlyX = { 0.0, 0.048, 0.095, 0.143, 0.19, 0.238, 0.286, 0.333, 0.381, 0.429, 0.476, 0.524, 0.571, 0.619, 0.667, 0.714, 0.762, 0.81, 0.857, 0.905, 0.952, 1.0 };
    private static readonly double[] MonthlyY = { 0.89, 0.77, 0.74, 0.69, 0.67, 0.6, 0.55, 0.52, 0.5, 0.41, 0.39, 0.34, 0.33, 0.27, 0.27, 0.21, 0.15, 0.12, 0.07, 0.02, 0.0, 0.0 };
    private static readonly double[] WeeklyX = { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 };
    private static readonly double[] WeeklyY = { 0.96, 0.62, 0.45, 0.32, 0.11, 0.0 };
    private static readonly double[] AsiaX = { 0.0, 0.111, 0.222, 0.333, 0.444, 0.556, 0.667, 0.778, 0.889, 1.0 };
    private static readonly double[] AsiaY = { 1.03, 0.68, 0.56, 0.4, 0.32, 0.25, 0.19, 0.14, 0.08, 0.0 };
    private static readonly double[] LondonX = { 0.0, 0.167, 0.333, 0.5, 0.667, 0.833, 1.0 };
    private static readonly double[] LondonY = { 0.97, 0.67, 0.54, 0.46, 0.36, 0.24, 0.0 };
    private static readonly double[] NyX = { 0.0, 0.143, 0.286, 0.429, 0.571, 0.714, 0.857, 1.0 };
    private static readonly double[] NyY = { 1.06, 0.69, 0.45, 0.35, 0.25, 0.14, 0.05, 0.0 };

    public static double ExtAllow(SrmPeriodKind kind, double f)
    {
        var (xs, ys) = kind switch
        {
            SrmPeriodKind.Month => (MonthlyX, MonthlyY),
            SrmPeriodKind.Week => (WeeklyX, WeeklyY),
            SrmPeriodKind.Asia => (AsiaX, AsiaY),
            SrmPeriodKind.London => (LondonX, LondonY),
            _ => (NyX, NyY),
        };
        return Interp(xs, ys, f);
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        var r = ys[^1];
        if (x <= xs[0]) return ys[0];
        for (var i = 1; i < xs.Length; i++)
        {
            if (x <= xs[i])
            {
                var x0 = xs[i - 1];
                var x1 = xs[i];
                r = ys[i - 1] + ((ys[i] - ys[i - 1]) * (x - x0) / (x1 - x0));
                break;
            }
        }
        return r;
    }
}

/// <summary>
/// Session Range Map — ported from the operator's own `srm.pine` (2026-10-12: "i have updated the
/// srm.pine... please add this into the finch-lite"; "those look good right now lets finish this").
/// Now covers: volatility-implied session/weekly/monthly budgets with the adaptive completion
/// projection, session/weekly/monthly POC/VAH/VAL (both as lines AND, via
/// <see cref="SrmPeriod.GetHistogramRows"/>, the full profile histogram), naked (untested)
/// session/weekly/monthly highs and lows that extend until touched, NY initial balance, and
/// Vol-Step Ranges (implied-move mode only — see that region's own doc comment). NOT ported:
/// `alert()` calls (no verified Quantower indicator-side alert API exists anywhere in this
/// codebase — checked before starting this pass rather than guessing at one; alert-worthy moments
/// instead just change a line's own colour/style), naked-level STACKING/merge labels (each naked
/// level draws on its own rather than being grouped when several sit close together), and custom/
/// editable session windows (Asia/London/NY still use the source script's own DEFAULT hours).
///
/// SESSION WINDOWS are the source script's own DEFAULTS (Asia 18:00-03:00 ET, London 03:00-09:30
/// ET, NY 09:30-16:00 ET) — not yet exposed as custom/editable InputParameters the way VWAP's own
/// session picker is; can be added later if wanted.
///
/// WEEK/MONTH BOUNDARY: the source script anchors the futures week to Sunday 18:00 ET exactly;
/// this port instead detects a week rollover via ISO-8601 (week, year) changing between
/// consecutive bars' own ET calendar dates — a few hours different around Sunday evening, which
/// does not meaningfully change a budget spanning multiple DAYS. Month rollover is an exact
/// calendar-month match (no approximation needed there).
///
/// VOLATILITY SOURCE: VIX/VIX3M (expanded-regime detection) and the operator's chosen implied-move
/// index (VXN for NQ/MNQ/QQQ, VIX for ES/MES/SPY) come from CBOE's own free public quotes endpoint
/// (<see cref="CboeIndexClient"/>) — no broker feed symbol needed, same reasoning as `LevelsMap`'s
/// own QQQ/SPY ratio fix (the operator's prop-firm futures feed doesn't carry index symbols either).
/// Prior day close and the ATR(14) fallback come from a dedicated daily `HistoricalData` fetch.
/// </summary>
internal sealed class SrmEngine
{
    public SrmPeriod Asia { get; } = new(SrmPeriodKind.Asia, "ASIA");
    public SrmPeriod London { get; } = new(SrmPeriodKind.London, "LDN");
    public SrmPeriod Ny { get; } = new(SrmPeriodKind.Ny, "NY");
    public SrmPeriod Week { get; } = new(SrmPeriodKind.Week, "WK");
    public SrmPeriod Month { get; } = new(SrmPeriodKind.Month, "MO");

    /// <summary>Daily expected move — null until a real prior close + either a live vol index or
    /// an ATR fallback has been supplied via <see cref="SetVolatility"/>.</summary>
    public double? EmDay { get; private set; }
    public bool Expanded { get; private set; }

    /// <summary>Untested session/weekly/monthly highs and lows — pushed on every
    /// <see cref="SrmPeriod.Finish"/>, marked touched the moment price trades back through them.
    /// Capped at <see cref="MaxNakedLevels"/>, oldest dropped first, matching `srm.pine`'s own
    /// `nakedMax` default.</summary>
    public IReadOnlyList<SrmNakedLevel> NakedLevels => this.nakedLevels;
    private const int MaxNakedLevels = 150;
    private readonly List<SrmNakedLevel> nakedLevels = new();

    /// <summary>NY first-hour high/low — "one side of the first hour holds as the session extreme
    /// ~80% of days" (the source script's own stated evidence). Null until the first NY session
    /// this engine has seen completes its own initial-balance window.</summary>
    public double? IbHigh { get; private set; }
    public double? IbLow { get; private set; }
    public bool IbDone { get; private set; }
    public bool IbBrokenUp { get; private set; }
    public bool IbBrokenDn { get; private set; }
    private DateTime ibStartUtc;
    private bool ibActive;

    /// <summary>Vol-Step Ranges — IMPLIED-MOVE MODE ONLY (the source script's "ATR (classic)" mode
    /// needs its own separate ATR computed off CHART bars, not the daily series this engine already
    /// fetches for `emDay`; deferred rather than adding a second ATR pipeline for a mode the
    /// operator's own `vsSource` default doesn't even select). Step size = <c>vsMult * emDay</c>;
    /// the midpoint only moves once price has travelled a FULL step away, gap-aware (jumps as many
    /// steps as price actually moved, not one step per bar).</summary>
    public double? VsMid { get; private set; }
    public double? VsHalf { get; private set; }

    private int? lastIsoWeekKey;
    private int? lastMonthKey;
    private bool wasInAsia, wasInLondon, wasInNy;

    /// <summary>Looks up the owning <see cref="SrmPeriod"/> for a <see cref="SrmNakedLevel"/>'s own
    /// <see cref="SrmNakedLevel.PeriodKind"/> — used only for its `Name` when labelling a naked
    /// level, so a caller doesn't need its own switch over every period kind.</summary>
    public SrmPeriod PeriodFor(SrmPeriodKind kind) => kind switch
    {
        SrmPeriodKind.Asia => this.Asia,
        SrmPeriodKind.London => this.London,
        SrmPeriodKind.Ny => this.Ny,
        SrmPeriodKind.Week => this.Week,
        _ => this.Month,
    };

    public void Reset()
    {
        this.nakedLevels.Clear();
        this.IbHigh = null;
        this.IbLow = null;
        this.IbDone = false;
        this.IbBrokenUp = false;
        this.IbBrokenDn = false;
        this.ibActive = false;
        this.VsMid = null;
        this.VsHalf = null;
        this.EmDay = null;
        this.Expanded = false;
        this.lastIsoWeekKey = null;
        this.lastMonthKey = null;
        this.wasInAsia = false;
        this.wasInLondon = false;
        this.wasInNy = false;
        this.Asia.ForceInactive();
        this.London.ForceInactive();
        this.Ny.ForceInactive();
        this.Week.ForceInactive();
        this.Month.ForceInactive();
    }

    /// <summary>Prior day close + ATR(14), recomputed fresh from the whole held daily series each
    /// call — cheap (the lookback is a few dozen bars), so no incremental rolling-window state is
    /// worth the complexity. Shared by `FinchLiteIndicator`'s own `DrainSrm` and any strategy
    /// built on this engine (e.g. `srmAutoStrategy`) rather than duplicated in each.</summary>
    public static bool TryComputeDailyVolInputs(HistoricalData daily, out double priorClose, out double atr14)
    {
        priorClose = 0;
        atr14 = 0;

        var closedUpTo = daily.Count - 1; // exclude the still-forming day
        if (closedUpTo < 1) return false;
        if (daily[closedUpTo - 1, SeekOriginHistory.Begin] is not HistoryItemBar priorBar) return false;

        priorClose = priorBar.Close;

        var trs = new List<double>();
        var start = Math.Max(1, closedUpTo - 14);
        for (var i = start; i < closedUpTo; i++)
        {
            if (daily[i, SeekOriginHistory.Begin] is not HistoryItemBar bar) continue;
            if (daily[i - 1, SeekOriginHistory.Begin] is not HistoryItemBar prevBar) continue;

            var tr = Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - prevBar.Close), Math.Abs(bar.Low - prevBar.Close)));
            trs.Add(tr);
        }

        if (trs.Count > 0) atr14 = trs.Average();
        return true;
    }

    public void SetVolatility(double? prevClose, double? impliedVolIndex, double? vix, double? vix3m, double atrFallback)
    {
        var usingVol = prevClose is > 0 && impliedVolIndex is > 0;
        this.EmDay = usingVol
            ? prevClose!.Value * impliedVolIndex!.Value / 100.0 / Math.Sqrt(252)
            : atrFallback > 0 ? atrFallback * 0.87 : null;

        this.Expanded = vix is > 0 && vix3m is > 0 && vix.Value >= vix3m.Value;
    }

    /// <summary>Feed one CLOSED bar from the chart's own already-loaded series (whatever timeframe
    /// that happens to be) — drives every period's start/update/finish transition.</summary>
    public void FeedBar(
        DateTime barOpenUtc, DateTime barCloseUtc, double open, double high, double low, double volume,
        double tickSize, SrmSettings settings)
    {
        if (this.EmDay is not { } emDay) return;

        var regMult = this.Expanded && settings.ExpandedScaleEnabled ? settings.ExpandedMultiplier : 1.0;
        var et = TimeZoneInfo.ConvertTimeFromUtc(barCloseUtc, SrmClock.EasternZone);

        this.RunSession(this.Asia, settings.AsiaEnabled, SrmClock.InWindow(et, 18, 0, 3, 0), ref this.wasInAsia,
            emDay * settings.AsiaShare * regMult, settings, barOpenUtc, barCloseUtc, open, high, low, volume, tickSize, et, 3, 0);

        this.RunSession(this.London, settings.LondonEnabled, SrmClock.InWindow(et, 3, 0, 9, 30), ref this.wasInLondon,
            emDay * settings.LondonShare * regMult, settings, barOpenUtc, barCloseUtc, open, high, low, volume, tickSize, et, 9, 30);

        var nyWasIn = this.wasInNy;
        this.RunSession(this.Ny, settings.NyEnabled, SrmClock.InWindow(et, 9, 30, 16, 0), ref this.wasInNy,
            emDay * settings.NyShare * regMult, settings, barOpenUtc, barCloseUtc, open, high, low, volume, tickSize, et, 16, 0);
        this.UpdateInitialBalance(settings, !nyWasIn && this.wasInNy, barOpenUtc, barCloseUtc, open, high, low);

        if (settings.WeeklyEnabled)
            this.RunCalendarPeriod(this.Week, barOpenUtc, barCloseUtc, open, high, low, volume, tickSize, settings,
                emDay * Math.Sqrt(5) * settings.WeeklyBudgetMult * regMult, IsoWeekKey(et), ref this.lastIsoWeekKey,
                settings.WeekRows, settings.WeekKProj, TimeSpan.FromMinutes(7140));

        if (settings.MonthlyEnabled)
            this.RunCalendarPeriod(this.Month, barOpenUtc, barCloseUtc, open, high, low, volume, tickSize, settings,
                emDay * Math.Sqrt(21) * settings.MonthlyBudgetMult * regMult, (et.Year * 12) + et.Month, ref this.lastMonthKey,
                settings.MonthRows, settings.MonthKProj, TimeSpan.FromDays(DateTime.DaysInMonth(et.Year, et.Month)));

        this.UpdateNakedTouches(barCloseUtc, high, low);

        if (settings.VolStepEnabled)
            this.UpdateVolStep(barCloseUtc, close: (high + low) / 2.0, step: settings.VolStepMult * emDay);
    }

    private void RunSession(
        SrmPeriod period, bool enabled, bool inWindow, ref bool wasIn, double budgetPts, SrmSettings settings,
        DateTime barOpenUtc, DateTime barCloseUtc, double open, double high, double low, double volume, double tickSize,
        DateTime et, int endH, int endM)
    {
        if (!enabled)
        {
            if (period.Active) this.FinishAndPushNaked(period, settings.ValueAreaPercent, barCloseUtc);
            wasIn = false;
            return;
        }

        if (inWindow && !wasIn)
        {
            var expectedEnd = SrmClock.NextBoundaryUtc(et, endH, endM, barCloseUtc);
            period.Start(barOpenUtc, expectedEnd, open, budgetPts, settings.SessionRows, tickSize);
        }
        else if (!inWindow && wasIn)
        {
            this.FinishAndPushNaked(period, settings.ValueAreaPercent, barCloseUtc);
        }

        wasIn = inWindow;

        if (period.Active)
            period.Update(barCloseUtc, high, low, volume, settings.KProj, settings.ExtMult, settings.AdaptiveProjection);
    }

    private void RunCalendarPeriod(
        SrmPeriod period, DateTime barOpenUtc, DateTime barCloseUtc, double open, double high, double low,
        double volume, double tickSize, SrmSettings settings, double budgetPts, int key, ref int? lastKey, int rows,
        double kProj, TimeSpan expectedLength)
    {
        if (lastKey is null || key != lastKey)
        {
            if (lastKey is not null) this.FinishAndPushNaked(period, settings.ValueAreaPercent, barCloseUtc);
            period.Start(barOpenUtc, barOpenUtc + expectedLength, open, budgetPts, rows, tickSize);
            lastKey = key;
        }

        period.Update(barCloseUtc, high, low, volume, kProj, settings.ExtMult, settings.AdaptiveProjection);
    }

    private void FinishAndPushNaked(SrmPeriod period, double vaPct, DateTime finishedUtc)
    {
        period.Finish(vaPct, finishedUtc);

        if (period.LastFinished.High is { } h) this.nakedLevels.Add(new SrmNakedLevel(period.Kind, true, h, finishedUtc));
        if (period.LastFinished.Low is { } l) this.nakedLevels.Add(new SrmNakedLevel(period.Kind, false, l, finishedUtc));

        while (this.nakedLevels.Count > MaxNakedLevels)
            this.nakedLevels.RemoveAt(0);
    }

    private void UpdateNakedTouches(DateTime barCloseUtc, double high, double low)
    {
        foreach (var lvl in this.nakedLevels)
        {
            if (lvl.TouchedUtc is null && high >= lvl.Price && low <= lvl.Price)
                lvl.MarkTouched(barCloseUtc);
        }
    }

    /// <summary>NY first-hour high/low, ported from `srm.pine`'s own initial-balance block — resets
    /// the moment a fresh NY session starts, freezes once <see cref="SrmSettings.IbMinutes"/> has
    /// elapsed, then just watches for the first break each side for the rest of the session.</summary>
    private void UpdateInitialBalance(SrmSettings settings, bool nyJustStarted, DateTime barOpenUtc, DateTime barCloseUtc, double open, double high, double low)
    {
        if (!settings.IbEnabled) return;

        if (nyJustStarted)
        {
            this.IbHigh = open;
            this.IbLow = open;
            this.ibStartUtc = barOpenUtc;
            this.ibActive = true;
            this.IbDone = false;
            this.IbBrokenUp = false;
            this.IbBrokenDn = false;
        }

        if (!this.ibActive) return;

        if (!this.IbDone)
        {
            this.IbHigh = Math.Max(this.IbHigh ?? high, high);
            this.IbLow = Math.Min(this.IbLow ?? low, low);

            if ((barCloseUtc - this.ibStartUtc).TotalMinutes >= settings.IbMinutes)
                this.IbDone = true;

            return;
        }

        if (this.IbHigh is { } ibH && high > ibH) this.IbBrokenUp = true;
        if (this.IbLow is { } ibL && low < ibL) this.IbBrokenDn = true;
    }

    /// <summary>See the class's own "Vol-Step Ranges" doc comment — implied-move mode only.</summary>
    private void UpdateVolStep(DateTime barCloseUtc, double close, double step)
    {
        if (step <= 0)
        {
            this.VsMid = null;
            this.VsHalf = null;
            return;
        }

        if (this.VsMid is null)
        {
            this.VsMid = close;
            this.VsHalf = step / 2;
            return;
        }

        var dv = close - this.VsMid.Value;
        if (Math.Abs(dv) > step)
        {
            var n = Math.Floor(Math.Abs(dv) / step);
            this.VsMid = this.VsMid.Value + (Math.Sign(dv) * n * step);
            this.VsHalf = step / 2;
        }
    }

    private static int IsoWeekKey(DateTime et) => (et.Year * 100) + ISOWeek.GetWeekOfYear(et);
}

/// <summary>Plain-value settings bag passed into <see cref="SrmEngine.FeedBar"/> each call — avoids
/// the engine reaching back into the indicator's own InputParameter properties, same "pure engine,
/// caller supplies config" discipline as every other engine in this codebase.</summary>
internal readonly record struct SrmSettings(
    bool AsiaEnabled, double AsiaShare,
    bool LondonEnabled, double LondonShare,
    bool NyEnabled, double NyShare,
    bool WeeklyEnabled, double WeeklyBudgetMult, double WeekKProj, int WeekRows,
    bool MonthlyEnabled, double MonthlyBudgetMult, double MonthKProj, int MonthRows,
    double KProj, bool AdaptiveProjection, double ExtMult,
    bool ExpandedScaleEnabled, double ExpandedMultiplier,
    int SessionRows, double ValueAreaPercent,
    bool IbEnabled, int IbMinutes,
    bool VolStepEnabled, double VolStepMult);

/// <summary>Small, self-contained ET clock helpers — deliberately NOT shared with
/// `FinchLiteIndicator.cs`'s own `IsInVwapWindow` (same "small, independently-readable" convention
/// already used throughout this codebase rather than a premature shared utility).</summary>
internal static class SrmClock
{
    public static readonly TimeZoneInfo EasternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>True if <paramref name="local"/>'s time-of-day falls in [start, end), handling a
    /// window that crosses midnight (e.g. Asia 18:00-03:00).</summary>
    public static bool InWindow(DateTime local, int startHour, int startMinute, int endHour, int endMinute)
    {
        var t = local.TimeOfDay;
        var start = new TimeSpan(startHour, startMinute, 0);
        var end = new TimeSpan(endHour, endMinute, 0);
        return start <= end ? t >= start && t < end : t >= start || t < end;
    }

    /// <summary>The next ET wall-clock moment matching (endHour:endMinute), converted to UTC —
    /// used as a session's own expected-end for the adaptive projection's elapsed-fraction calc.</summary>
    public static DateTime NextBoundaryUtc(DateTime etNow, int endHour, int endMinute, DateTime fallbackUtc)
    {
        try
        {
            var candidate = new DateTime(etNow.Year, etNow.Month, etNow.Day, endHour, endMinute, 0, DateTimeKind.Unspecified);
            if (candidate <= etNow) candidate = candidate.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(candidate, EasternZone);
        }
        catch
        {
            return fallbackUtc.AddHours(8);
        }
    }
}
