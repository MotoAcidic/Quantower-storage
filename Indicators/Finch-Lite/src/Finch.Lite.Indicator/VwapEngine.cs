using System;

namespace FinchLite;

/// <summary>
/// Session-anchored VWAP + volume-weighted standard deviation — ported from the operator's own
/// `oceanVwap.pine` (2026-10-08: "i use the oceanVwap.pine... and would like my indicator updated
/// to reflect it"). Same core math as the source script's per-bar contribution
/// (Σ(price·volume), Σ(price²·volume), Σ(volume), true volume-weighted σ — not price stdev, same
/// "institutional standard" the script's own header calls out), but fed differently:
///
/// DELIBERATE IMPROVEMENT OVER THE SOURCE: the Pine script has an "Intrabar precision" toggle that
/// rebuilds from 1-minute sub-bars because chart-timeframe bars are too coarse above 1m. This
/// engine has no such toggle because it doesn't need one — <see cref="FeedTrade"/> is fed from
/// Finch-Lite's own live tick stream (the same one already driving the delta panel and big-trade
/// markers), so every live contribution is already tick-precise regardless of chart timeframe.
///
/// SEEDING GAP, same shape as <see cref="PocEngine"/>'s own stated limitation: Finch-Lite has no
/// historical tick/time-and-sales backfill, so the portion of a session that happened before this
/// indicator attached can't be reconstructed from ticks. <see cref="SeedFromBar"/> closes most of
/// that gap using the SAME per-bar formula the source script itself uses when it can't get
/// sub-bars (hlc3 × bar volume) — called once per historical bar covering "since session start"
/// on attach, then <see cref="FeedTrade"/> takes over tick-by-tick for anything live-forward. Both
/// feed the exact same accumulators, so there's no seam between the seeded and live portions.
/// </summary>
internal sealed class VwapEngine
{
    private double sumPV;
    private double sumP2V;
    private double sumV;

    public double? Vwap { get; private set; }
    public double? StdDev { get; private set; }
    public bool HasSession { get; private set; }
    public DateTime SessionStartUtc { get; private set; }

    /// <summary>Last session's frozen VWAP/σ, carried as a "naked" reference until the NEXT
    /// session starts (then THAT session becomes "prior" in turn) — same as the source script's
    /// own prior-session lines.</summary>
    public double? PriorVwap { get; private set; }
    public double? PriorStdDev { get; private set; }

    public void StartSession(DateTime startUtc)
    {
        if (this.HasSession)
        {
            this.PriorVwap = this.Vwap;
            this.PriorStdDev = this.StdDev;
        }

        this.sumPV = 0;
        this.sumP2V = 0;
        this.sumV = 0;
        this.Vwap = null;
        this.StdDev = null;
        this.SessionStartUtc = startUtc;
        this.HasSession = true;
    }

    public void Reset()
    {
        this.sumPV = 0;
        this.sumP2V = 0;
        this.sumV = 0;
        this.Vwap = null;
        this.StdDev = null;
        this.HasSession = false;
        this.PriorVwap = null;
        this.PriorStdDev = null;
    }

    /// <summary>Historical-bar seeding, source script's own per-bar formula (hlc3 × bar volume) —
    /// only meaningful for the portion of the session before this indicator was watching live.</summary>
    public void SeedFromBar(double hlc3, double volume)
    {
        if (!this.HasSession || volume <= 0) return;
        this.Accumulate(hlc3, volume);
    }

    /// <summary>Live tick — strictly more precise than a bar-based contribution, the whole reason
    /// this engine doesn't need the source script's own "intrabar precision" toggle.</summary>
    public void FeedTrade(double price, double size)
    {
        if (!this.HasSession || size <= 0) return;
        this.Accumulate(price, size);
    }

    private void Accumulate(double price, double size)
    {
        this.sumPV += price * size;
        this.sumP2V += price * price * size;
        this.sumV += size;

        if (this.sumV <= 0) return;

        var vwap = this.sumPV / this.sumV;
        var variance = Math.Max((this.sumP2V / this.sumV) - (vwap * vwap), 0.0);
        this.Vwap = vwap;
        this.StdDev = Math.Sqrt(variance);
    }
}
