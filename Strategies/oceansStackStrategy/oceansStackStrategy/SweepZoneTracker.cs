using System;
using TradingPlatform.BusinessLayer;

namespace oceansStackStrategy;

/// <summary>One instance per zone (top/short = direction +1, bottom/long = direction -1) — avoids
/// `ocean.pine`'s own duplicated `swT`/`swB` variable pairs by parameterizing on direction instead.
/// `FiredThisBar`'s other fields are meaningless when false.</summary>
internal readonly record struct SweepResult(
    bool FiredThisBar, Side Side, double Entry, double Stop, double? PocInformational, double Target,
    bool AbsorptionAtSweep);

/// <summary>
/// The actual trading trigger — a direct port of `ocean.pine`'s own `if inOpen: if armedT and not
/// firedT: if high > fuelT: ...; if swT and close &lt; pVAH and swHiDelta &lt;= 0: ...` state machine
/// (mirrored for the bottom/long zone). Owns ONLY the sweep/fire state itself — score/armed are
/// recomputed fresh every bar by the caller from `ValueAreaEngine`/`AbsorptionTracker`/
/// `FuelPoolSelector` and passed IN, matching how Pine keeps `scoreT`/`armedT` as plain per-bar
/// expressions separate from the persistent `var` sweep state.
///
/// Must only be fed bars that closed LIVE after this run attached, and only while inside the
/// configured sweep window — the caller is responsible for both gates (same
/// `pocRejectionCheckedUpTo`-style cursor discipline `finchDomScalpStrategy` already uses for its
/// own "never react to backlog with no dry-run gate" safety net).
/// </summary>
internal sealed class SweepZoneTracker
{
    private readonly int direction; // +1 = top/short zone, -1 = bottom/long zone

    private bool swept;
    private double? sweepExtreme;
    private double cumulativeDelta;
    private bool absorptionAtSweep;
    private bool fired;

    public SweepZoneTracker(int direction) => this.direction = direction;

    public bool IsSwept => this.swept;
    public double? SweepExtreme => this.sweepExtreme;
    public double CumulativeDeltaSinceSweep => this.cumulativeDelta;
    public bool Fired => this.fired;
    public int LatchedScore { get; private set; }

    /// <summary>Call once per newly-started RTH session — clears armed/swept/fired state for a
    /// fresh day, matching Pine's own `if newRTH: swT := false; swHiExt := na; ...`.</summary>
    public void OnRthReset()
    {
        this.swept = false;
        this.sweepExtreme = null;
        this.cumulativeDelta = 0d;
        this.absorptionAtSweep = false;
        this.fired = false;
        this.LatchedScore = 0;
    }

    /// <param name="ownEdge">VAH for the top/short zone, VAL for the bottom/long zone — price must
    /// close back beyond this to count as a fail-and-reclaim.</param>
    /// <param name="opposingEdge">VAL for the top/short zone, VAH for the bottom/long zone — the
    /// actual traded target (T2, per the operator's own "single contract, T2 only" decision).</param>
    /// <param name="pocInformational">Prior-day POC — carried through only for the signal's own log
    /// line (T1, informational, never traded).</param>
    public SweepResult OnBarClosed(
        Bar bar, double barDelta, bool armed, int currentScore, double? fuelPrice,
        double ownEdge, double opposingEdge, double? pocInformational, double stopBufferPoints,
        bool absorptionThisBar)
    {
        if (!armed || this.fired || fuelPrice is not { } fuel)
            return default;

        var isTop = this.direction > 0;
        var sweptThisBar = isTop ? bar.High > fuel : bar.Low < fuel;

        // Two SEPARATE checks below, not else-if — a single bar can both extend/begin the sweep
        // AND fire the reversal in the same close, exactly as Pine allows (a spike-and-reject
        // candle), since the reversal check below already includes this bar's own just-added delta.
        if (sweptThisBar)
        {
            if (!this.swept) this.LatchedScore = currentScore;
            this.swept = true;
            this.sweepExtreme = isTop
                ? (this.sweepExtreme is { } eh ? Math.Max(eh, bar.High) : bar.High)
                : (this.sweepExtreme is { } el ? Math.Min(el, bar.Low) : bar.Low);
            this.cumulativeDelta += barDelta;
            this.absorptionAtSweep = this.absorptionAtSweep || absorptionThisBar;
        }

        if (!this.swept) return default;

        var reclaimed = isTop ? bar.Close < ownEdge : bar.Close > ownEdge;
        var deltaConfirms = isTop ? this.cumulativeDelta <= 0 : this.cumulativeDelta >= 0;

        if (!reclaimed || !deltaConfirms) return default;

        this.fired = true;
        this.swept = false;

        var side = isTop ? Side.Sell : Side.Buy;
        var stop = isTop ? this.sweepExtreme!.Value + stopBufferPoints : this.sweepExtreme!.Value - stopBufferPoints;

        return new SweepResult(true, side, bar.Close, stop, pocInformational, opposingEdge, this.absorptionAtSweep);
    }
}
