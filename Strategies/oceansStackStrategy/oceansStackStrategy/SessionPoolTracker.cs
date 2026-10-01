using System;

namespace oceansStackStrategy;

/// <summary>
/// The four "fuel" pools `ocean.pine` tracks — overnight H/L, Asia H/L, prior-day H/L, prior-week
/// H/L — each a running high/low accumulated over its own session window, fresh-started on window
/// ENTRY (not a running max bleeding across days) and frozen at window exit for the rest of the
/// strategy's lookahead to read. Direct port of the Pine script's own `if inOvn`/`if inAsia`/
/// `if newRTH`/`if newWeek` accumulation blocks. Fed once per newly-closed 1-minute bar; the
/// caller (the main strategy's poll loop) is responsible for deciding which windows the bar falls
/// into (see `IsInWindow`/session InputParameters) — this class only owns the accumulation state.
/// </summary>
internal sealed class SessionPoolTracker
{
    private bool wasInOvernight;
    private double? ovnH, ovnL;

    private bool wasInAsia;
    private double? asiaH, asiaL;

    private double? rthH, rthL;
    private double? pdH, pdL;

    private double? weekH, weekL;
    private double? pwH, pwL;

    public double? OvernightHigh => this.ovnH;
    public double? OvernightLow => this.ovnL;
    public double? AsiaHigh => this.asiaH;
    public double? AsiaLow => this.asiaL;
    public double? PriorDayHigh => this.pdH;
    public double? PriorDayLow => this.pdL;
    public double? PriorWeekHigh => this.pwH;
    public double? PriorWeekLow => this.pwL;

    /// <param name="isNewRth">True only on the first bar of a newly-started RTH session.</param>
    /// <param name="isNewWeek">True only on the first RTH bar of a new ISO week (a subset of
    /// <paramref name="isNewRth"/> bars).</param>
    public void FeedBar(
        double high, double low, bool inOvernight, bool inAsia, bool inRth, bool isNewRth, bool isNewWeek)
    {
        // Prior-day freeze uses YESTERDAY's running rthH/rthL, then this bar becomes day one of
        // the new accumulation — matches Pine's `if newRTH: pdH := rthH; ...; rthH := high`
        // exactly (a single mutually-exclusive branch, not reset-then-separately-extend).
        if (isNewRth)
        {
            this.pdH = this.rthH;
            this.pdL = this.rthL;
            this.rthH = high;
            this.rthL = low;
        }
        else if (inRth)
        {
            this.rthH = this.rthH is { } rh ? Math.Max(rh, high) : high;
            this.rthL = this.rthL is { } rl ? Math.Min(rl, low) : low;
        }

        // Weekly freeze uses the just-completed week's running weekH/weekL, then resets to null
        // so the unconditional RTH-accumulation step below re-seeds it fresh from this same bar —
        // matches Pine's `if newWeek: pwH := cwH; ...; cwH := na` followed by the separate
        // `if inRTH: cwH := na(cwH) ? high : max(cwH, high)`.
        if (isNewWeek)
        {
            this.pwH = this.weekH;
            this.pwL = this.weekL;
            this.weekH = null;
            this.weekL = null;
        }

        if (inRth)
        {
            this.weekH = this.weekH is { } wh ? Math.Max(wh, high) : high;
            this.weekL = this.weekL is { } wl ? Math.Min(wl, low) : low;
        }

        // Overnight/Asia both cross midnight and have no equivalent "newRTH" boundary flag — the
        // fresh-start-on-entry behavior is detected here directly from "was I in this window on
        // the PREVIOUS bar," matching Pine's own `inOvn[1]` / `inAsia[1]` lookback exactly.
        if (inOvernight)
        {
            this.ovnH = this.wasInOvernight && this.ovnH is { } oh ? Math.Max(oh, high) : high;
            this.ovnL = this.wasInOvernight && this.ovnL is { } ol ? Math.Min(ol, low) : low;
        }
        this.wasInOvernight = inOvernight;

        if (inAsia)
        {
            this.asiaH = this.wasInAsia && this.asiaH is { } ah ? Math.Max(ah, high) : high;
            this.asiaL = this.wasInAsia && this.asiaL is { } al ? Math.Min(al, low) : low;
        }
        this.wasInAsia = inAsia;
    }
}
