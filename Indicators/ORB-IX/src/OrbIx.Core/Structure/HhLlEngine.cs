// Port of the Pine Script v4 study "Higher High Lower Low Strategy",
// (c) LonesomeThecolor.blue, Mozilla Public License 2.0
// (https://mozilla.org/MPL/2.0/). This file remains under MPL 2.0.

using System;
using System.Collections.Generic;

namespace OrbIx.Core.Structure;

/// <summary>Pine's HH / HL / LL / LH label classes, with their chip text.</summary>
public enum HhLlLabelKind
{
    HigherHigh,
    HigherLow,
    LowerLow,
    LowerHigh,
}

/// <summary>
/// One structure label. <see cref="Bar"/> is the PIVOT bar (the confirmation
/// bar minus rightBars — Pine plots with <c>offset = -rb</c>), so a label is
/// only ever emitted rightBars after the extreme it names: a swing is not
/// known when it forms.
/// </summary>
public readonly record struct HhLlLabel(int Bar, HhLlLabelKind Kind, double Price);

/// <summary>
/// One support/resistance line. Mirrors the script's line lifecycle: created
/// at the pivot bar (<see cref="StartBar"/> = confirmation bar − rightBars)
/// extending right (<see cref="EndBar"/> null); frozen at the bar where the
/// level changed (line.set_x2(bar_index) + extend.none).
/// </summary>
public sealed class HhLlSegment
{
    public HhLlSegment(bool isResistance, int startBar, double price)
    {
        this.IsResistance = isResistance;
        this.StartBar = startBar;
        this.Price = price;
    }

    public bool IsResistance { get; }

    public int StartBar { get; }

    public double Price { get; }

    /// <summary>Null while the line still extends right.</summary>
    public int? EndBar { get; internal set; }
}

/// <summary>
/// Statement-for-statement port of the Pine v4 study "Higher High Lower Low
/// Strategy": pivot-filtered zigzag, HH/HL/LL/LH classification from the
/// last five alternating pivots, support/resistance from confirmed
/// structure, and a close-through-level trend state.
///
/// Fed one CLOSED bar at a time; statement order inside <see cref="Feed"/>
/// follows the script exactly, because valuewhen reads the series state as
/// of each statement (a mutation earlier in the bar changes what a later
/// valuewhen sees on the current bar).
///
/// FLAGGED CONVENTIONS — the official Pine v4 reference is silent on all
/// four; each is arbitrated by the TradingView side-by-side, never assumed
/// settled (tools/hhll_reference.py carries the same four):
///   (a) pivot comparison is STRICT (&gt; / &lt;) against both sides;
///   (b) valuewhen counts the current bar as occurrence 0 — the only
///       reading consistent with the script's findprevious starting at x=1;
///   (c) `for x = xx to 1000` with xx &gt; 1000 runs zero iterations;
///   (d) Pine comparison with na yields na (falsy), so the rechange /
///       suchange `!=` is FALSE when either side is na — IEEE `!=` would
///       say true and is guarded in <see cref="PineNeq"/>.
/// </summary>
public sealed class HhLlEngine
{
    /// <summary>study(..., max_lines_count = 500): only the last 500 lines survive.</summary>
    public const int MaxSegments = 500;

    /// <summary>findprevious() bounds every scan at 1000 bars back.</summary>
    private const int ScanCap = 1000;

    private readonly int leftBars;
    private readonly int rightBars;

    private readonly List<double> highs = new();
    private readonly List<double> lows = new();
    private readonly List<double> hlHist = new();
    private readonly List<double> zzHist = new();
    private readonly List<HhLlLabel> labels = new();
    private readonly List<HhLlSegment> segments = new();
    private readonly List<double> trendSeries = new();
    private readonly List<double> resSeries = new();
    private readonly List<double> supSeries = new();

    private double res = double.NaN;
    private double sup = double.NaN;
    private double trend = double.NaN;

    // current-bar working values, visible to series access at x = 0
    private double curHl = double.NaN;
    private double curZz = double.NaN;

    public HhLlEngine(int leftBars, int rightBars)
    {
        // input(..., minval = 1) on both
        if (leftBars < 1)
            throw new ArgumentOutOfRangeException(nameof(leftBars), leftBars, "Pine input minval is 1.");
        if (rightBars < 1)
            throw new ArgumentOutOfRangeException(nameof(rightBars), rightBars, "Pine input minval is 1.");

        this.leftBars = leftBars;
        this.rightBars = rightBars;
    }

    public int BarsFed => this.highs.Count;

    public IReadOnlyList<HhLlLabel> Labels => this.labels;

    public IReadOnlyList<HhLlSegment> Segments => this.segments;

    /// <summary>Per-bar trend: 1 up, −1 down, 0 before the first breakout.</summary>
    public IReadOnlyList<double> TrendSeries => this.trendSeries;

    public IReadOnlyList<double> ResSeries => this.resSeries;

    public IReadOnlyList<double> SupSeries => this.supSeries;

    private static bool Truthy(double value)
        // iff reference: "Zero value (0 and also NaN, ...) is considered to be false"
        => !double.IsNaN(value) && value != 0;

    private static bool PineNeq(double x, double y)
        // flag (d): na on either side makes the comparison na -> falsy
        => !double.IsNaN(x) && !double.IsNaN(y) && x != y;

    // -- series access: x = 0 is the current bar's working value ----------
    private double SeriesHl(int x)
    {
        if (x == 0)
            return this.curHl;
        int i = this.hlHist.Count - x;
        return i >= 0 ? this.hlHist[i] : double.NaN;
    }

    private double SeriesZz(int x)
    {
        if (x == 0)
            return this.curZz;
        int i = this.zzHist.Count - x;
        return i >= 0 ? this.zzHist[i] : double.NaN;
    }

    /// <summary>valuewhen(hl, hl, occurrence) — current bar inclusive (flag b).</summary>
    private double ValuewhenHl(int occurrence)
    {
        int seen = 0;
        for (int x = 0; x <= this.hlHist.Count; x++)
        {
            double v = this.SeriesHl(x);
            if (Truthy(v))
            {
                if (seen == occurrence)
                    return v;
                seen++;
            }
        }

        return double.NaN;
    }

    /// <summary>valuewhen(zz, zz, occurrence) — current bar inclusive (flag b).</summary>
    private double ValuewhenZz(int occurrence)
    {
        int seen = 0;
        for (int x = 0; x <= this.zzHist.Count; x++)
        {
            double v = this.SeriesZz(x);
            if (Truthy(v))
            {
                if (seen == occurrence)
                    return v;
                seen++;
            }
        }

        return double.NaN;
    }

    /// <summary>ph = pivothigh(lb, rb): STRICT against both sides (flag a).</summary>
    private double PivotHigh(int n)
    {
        if (n < this.leftBars + this.rightBars)
            return double.NaN;

        int p = n - this.rightBars;
        double candidate = this.highs[p];

        for (int j = p - this.leftBars; j <= p + this.rightBars; j++)
        {
            if (j == p)
                continue;
            if (!(candidate > this.highs[j]))
                return double.NaN;
        }

        return candidate;
    }

    /// <summary>pl = pivotlow(lb, rb): STRICT against both sides (flag a).</summary>
    private double PivotLow(int n)
    {
        if (n < this.leftBars + this.rightBars)
            return double.NaN;

        int p = n - this.rightBars;
        double candidate = this.lows[p];

        for (int j = p - this.leftBars; j <= p + this.rightBars; j++)
        {
            if (j == p)
                continue;
            if (!(candidate < this.lows[j]))
                return double.NaN;
        }

        return candidate;
    }

    /// <summary>
    /// findprevious(): the four alternating backward scans, transcribed with
    /// their quirks kept — loc defaults are 0.0 (NOT na), xx only advances on
    /// a hit, and an exhausted first scan leaves xx = 0 so the second scan
    /// starts at the CURRENT bar and finds the current pivot itself.
    /// </summary>
    private (double Loc1, double Loc2, double Loc3, double Loc4) FindPrevious(double hl)
    {
        double loc1 = 0.0, loc2 = 0.0, loc3 = 0.0, loc4 = 0.0;
        double ehl = hl == 1 ? -1.0 : 1.0;
        int xx = 0;

        for (int x = 1; x <= ScanCap; x++)
        {
            if (this.SeriesHl(x) == ehl && !double.IsNaN(this.SeriesZz(x)))
            {
                loc1 = this.SeriesZz(x);
                xx = x + 1;
                break;
            }
        }

        ehl = hl;
        for (int x = xx; x <= ScanCap; x++)                 // flag (c)
        {
            if (this.SeriesHl(x) == ehl && !double.IsNaN(this.SeriesZz(x)))
            {
                loc2 = this.SeriesZz(x);
                xx = x + 1;
                break;
            }
        }

        ehl = hl == 1 ? -1.0 : 1.0;
        for (int x = xx; x <= ScanCap; x++)
        {
            if (this.SeriesHl(x) == ehl && !double.IsNaN(this.SeriesZz(x)))
            {
                loc3 = this.SeriesZz(x);
                xx = x + 1;
                break;
            }
        }

        ehl = hl;
        for (int x = xx; x <= ScanCap; x++)
        {
            if (this.SeriesHl(x) == ehl && !double.IsNaN(this.SeriesZz(x)))
            {
                loc4 = this.SeriesZz(x);
                break;
            }
        }

        return (loc1, loc2, loc3, loc4);
    }

    /// <summary>
    /// rechange/suchange block: freeze the newest still-extending line of
    /// this kind at the current bar, open a new one at the pivot bar, and
    /// let the study's 500-line pool evict the oldest.
    /// </summary>
    private void FreezeAndOpen(bool isResistance, int n, double price)
    {
        for (int i = this.segments.Count - 1; i >= 0; i--)
        {
            var seg = this.segments[i];
            if (seg.IsResistance == isResistance && seg.EndBar is null)
            {
                seg.EndBar = n;
                break;
            }
        }

        this.segments.Add(new HhLlSegment(isResistance, n - this.rightBars, price));

        while (this.segments.Count > MaxSegments)
            this.segments.RemoveAt(0);
    }

    /// <summary>One CLOSED bar, in the script's statement order.</summary>
    public void Feed(double high, double low, double close)
    {
        this.highs.Add(high);
        this.lows.Add(low);
        int n = this.highs.Count - 1;

        double ph = this.PivotHigh(n);
        double pl = this.PivotLow(n);

        // hl = iff(ph, 1, iff(pl, -1, na)) ; zz = iff(ph, ph, iff(pl, pl, na))
        this.curHl = Truthy(ph) ? 1.0 : Truthy(pl) ? -1.0 : double.NaN;
        this.curZz = Truthy(ph) ? ph : Truthy(pl) ? pl : double.NaN;

        // zz := iff(pl and hl==-1 and valuewhen(hl,hl,1)==-1 and pl > valuewhen(zz,zz,1), na, zz)
        if (Truthy(pl) && this.curHl == -1 && this.ValuewhenHl(1) == -1
            && pl > this.ValuewhenZz(1))
        {
            this.curZz = double.NaN;
        }

        // zz := iff(ph and hl==1 and valuewhen(hl,hl,1)==1 and ph < valuewhen(zz,zz,1), na, zz)
        if (Truthy(ph) && this.curHl == 1 && this.ValuewhenHl(1) == 1
            && ph < this.ValuewhenZz(1))
        {
            this.curZz = double.NaN;
        }

        // hl := iff(hl==-1 and valuewhen(hl,hl,1)==1 and zz > valuewhen(zz,zz,1), na, hl)
        if (this.curHl == -1 && this.ValuewhenHl(1) == 1
            && this.curZz > this.ValuewhenZz(1))
        {
            this.curHl = double.NaN;
        }

        // hl := iff(hl==1 and valuewhen(hl,hl,1)==-1 and zz < valuewhen(zz,zz,1), na, hl)
        if (this.curHl == 1 && this.ValuewhenHl(1) == -1
            && this.curZz < this.ValuewhenZz(1))
        {
            this.curHl = double.NaN;
        }

        // zz := iff(na(hl), na, zz)
        if (double.IsNaN(this.curHl))
            this.curZz = double.NaN;

        // if not na(hl): [loc1..loc4] = findprevious(); a := zz; b..e := loc1..loc4
        double a = double.NaN, b = double.NaN, c = double.NaN,
               d = double.NaN, e = double.NaN;
        if (!double.IsNaN(this.curHl))
        {
            var (loc1, loc2, loc3, loc4) = this.FindPrevious(this.curHl);
            a = this.curZz;
            b = loc1;
            c = loc2;
            d = loc3;
            e = loc4;
        }

        double zz = this.curZz;
        bool hh = Truthy(zz) && a > b && a > c && c > b && c > d;
        bool ll = Truthy(zz) && a < b && a < c && c < b && c < d;
        bool hlLabel = Truthy(zz) && ((a >= c && b > c && b > d && d > c && d > e)
                                      || (a < b && a > c && b < d));
        bool lh = Truthy(zz) && ((a <= c && b < c && b < d && d < c && d < e)
                                 || (a > b && a < c && b > d));

        // plotshape(..., offset = -rb): labels land on the pivot bar, in the
        // script's plot order (HL, HH, LL, LH)
        if (hlLabel)
            this.labels.Add(new HhLlLabel(n - this.rightBars, HhLlLabelKind.HigherLow, zz));
        if (hh)
            this.labels.Add(new HhLlLabel(n - this.rightBars, HhLlLabelKind.HigherHigh, zz));
        if (ll)
            this.labels.Add(new HhLlLabel(n - this.rightBars, HhLlLabelKind.LowerLow, zz));
        if (lh)
            this.labels.Add(new HhLlLabel(n - this.rightBars, HhLlLabelKind.LowerHigh, zz));

        double prevRes = this.res;
        double prevSup = this.sup;

        // res := iff(_lh, zz, res[1]) ; sup := iff(_hl, zz, sup[1])
        if (lh)
            this.res = zz;
        if (hlLabel)
            this.sup = zz;

        // trend := iff(close > res, 1, iff(close < sup, -1, nz(trend[1])))
        if (close > this.res)
            this.trend = 1.0;
        else if (close < this.sup)
            this.trend = -1.0;
        else if (double.IsNaN(this.trend))
            this.trend = 0.0;

        // res := iff((trend==1 and _hh) or (trend==-1 and _lh), zz, res)
        if ((this.trend == 1 && hh) || (this.trend == -1 && lh))
            this.res = zz;

        // sup := iff((trend==1 and _hl) or (trend==-1 and _ll), zz, sup)
        if ((this.trend == 1 && hlLabel) || (this.trend == -1 && ll))
            this.sup = zz;

        // rechange = res != res[1] ; suchange = sup != sup[1]   (flag d)
        if (PineNeq(this.res, prevRes))
            this.FreezeAndOpen(isResistance: true, n, this.res);
        if (PineNeq(this.sup, prevSup))
            this.FreezeAndOpen(isResistance: false, n, this.sup);

        this.hlHist.Add(this.curHl);
        this.zzHist.Add(this.curZz);
        this.trendSeries.Add(this.trend);
        this.resSeries.Add(this.res);
        this.supSeries.Add(this.sup);
    }
}
