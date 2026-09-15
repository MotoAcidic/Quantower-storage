using System;
using System.Globalization;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Features;

/// <summary>Which way a level was broken.</summary>
public enum BreakDirection
{
    /// <summary>Price left the range through the top.</summary>
    Up,

    /// <summary>Price left the range through the bottom.</summary>
    Down,
}

/// <summary>How a return to a broken level classified.</summary>
public enum RetestClass
{
    /// <summary>No break is being tracked.</summary>
    None,

    /// <summary>A break is armed but price has not yet come back to the level.</summary>
    AwaitingReturn,

    /// <summary>Price is at the level and the outcome is not yet decided.</summary>
    InProgress,

    /// <summary>Shallow, and the level held. The tightest stop in the system.</summary>
    Clean,

    /// <summary>Price traded back through the level but reclaimed it. A wider stop.</summary>
    DeepButReclaimed,

    /// <summary>Price went back through and stayed. The setup is dead.</summary>
    ReclaimFailure,

    /// <summary>Price never returned inside the permitted window.</summary>
    Expired,
}

/// <summary>
/// The current state of a tracked retest.
/// </summary>
/// <param name="Class">How it classified.</param>
/// <param name="LevelPrice">The broken level being retested.</param>
/// <param name="Direction">Which way the break went.</param>
/// <param name="DepthTicks">
/// Deepest travel back through the level, in ticks. Zero means price never went beyond it.
/// </param>
/// <param name="DepthFraction">
/// Depth as a fraction of the range that was broken, which is what the playbook's depth
/// limit is expressed in.
/// </param>
/// <param name="HoldConfirmed">Whether price moved back in the break direction far enough to confirm.</param>
/// <param name="BreakVolume">Volume on the break.</param>
/// <param name="RetestVolume">Volume traded during the return.</param>
public sealed record RetestState(
    RetestClass Class,
    double LevelPrice,
    BreakDirection Direction,
    double DepthTicks,
    double DepthFraction,
    bool HoldConfirmed,
    double BreakVolume,
    double RetestVolume)
{
    /// <summary>
    /// Whether the retest is one of the two tradeable classifications. A reclaim failure
    /// and an expiry are not setups that got worse; they are not setups.
    /// </summary>
    public bool IsTradeable => this.Class is RetestClass.Clean or RetestClass.DeepButReclaimed;

    /// <summary>
    /// Retest volume below break volume is the confirmation the specification asks for:
    /// the return should be quieter than the move that created it. NaN when either is
    /// unmeasured.
    /// </summary>
    public double VolumeRatio => this.BreakVolume > 0 ? this.RetestVolume / this.BreakVolume : double.NaN;

    public static RetestState Idle => new(
        RetestClass.None, double.NaN, BreakDirection.Up, 0d, 0d, false, 0d, 0d);
}

/// <summary>
/// M11. Classifies every return to a broken level.
///
/// Three outcomes matter and they are not the same trade. A clean retest is shallow and
/// held, and carries the tightest stop in the system. A deep retest traded back into the
/// range and reclaimed, which is still tradeable but needs a wider stop because the
/// structure that invalidates it is further away. A reclaim failure is not a worse setup —
/// it is the absence of one, and the specification's P3 treats it as evidence for the
/// other side.
///
/// The engine is armed explicitly when a break is confirmed rather than inferring breaks
/// itself, so the definition of "a break" lives in one place — the playbook — and this
/// module only answers what happened afterwards.
/// </summary>
public sealed class RetestEngine : IFeatureModule
{
    private readonly RetestConfig config;
    private readonly InstrumentSpec instrument;

    private RetestClass state = RetestClass.None;
    private double levelPrice = double.NaN;
    private double rangeWidth;
    private BreakDirection direction;
    private DateTime brokeAtUtc;
    private double breakVolume;
    private double retestVolume;
    private double deepestBeyond;
    private bool touched;
    private bool holdConfirmed;

    public RetestEngine(RetestConfig config, InstrumentSpec instrument)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.instrument = instrument;

        instrument.RequirePriceScale(nameof(instrument));
    }

    public string Id => "M11";

    /// <summary>Prices alone. A retest is geometry, not order flow.</summary>
    public DataTier Requires => DataTier.T0;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Arms the engine on a confirmed break.
    ///
    /// Re-arming discards any retest in progress: a second break supersedes the first, and
    /// carrying the old one forward would classify a return to the new level against the
    /// old level's geometry.
    /// </summary>
    public void ArmBreak(
        double brokenLevel, BreakDirection breakDirection, double rangeWidthPrice,
        double volumeOnBreak, DateTime brokeAtUtc)
    {
        if (double.IsNaN(brokenLevel))
            throw new ArgumentException("A broken level must have a price.", nameof(brokenLevel));

        if (rangeWidthPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rangeWidthPrice), rangeWidthPrice,
                "Retest depth is expressed as a fraction of the range that was broken, so the range must have width.");
        }

        this.state = RetestClass.AwaitingReturn;
        this.levelPrice = brokenLevel;
        this.direction = breakDirection;
        this.rangeWidth = rangeWidthPrice;
        this.breakVolume = volumeOnBreak;
        this.brokeAtUtc = brokeAtUtc;
        this.retestVolume = 0d;
        this.deepestBeyond = 0d;
        this.touched = false;
        this.holdConfirmed = false;
    }

    public void OnTick(in TickEvent tick)
    {
        if (this.state is RetestClass.None or RetestClass.ReclaimFailure or RetestClass.Expired)
            return;

        if (double.IsNaN(tick.Price))
            return;

        var tolerance = this.config.TouchToleranceTicks * this.instrument.TickSize;
        var failureDistance = this.config.FailureBeyondTicks * this.instrument.TickSize;
        var holdDistance = this.config.HoldConfirmTicks * this.instrument.TickSize;

        // Signed so that positive is always "back through the level, into the old range",
        // whichever way the break went. Everything below reads the same for both directions.
        var beyond = this.direction == BreakDirection.Up
            ? this.levelPrice - tick.Price
            : tick.Price - this.levelPrice;

        if (!this.touched)
        {
            if (beyond < -tolerance)
            {
                // Still travelling away from the level; nothing to classify yet.
                if (tick.TimestampUtc - this.brokeAtUtc > TimeSpan.FromSeconds(this.config.MaxSecondsToRetest))
                    this.state = RetestClass.Expired;

                return;
            }

            this.touched = true;
            this.state = RetestClass.InProgress;
        }

        this.retestVolume += tick.Size;

        if (beyond > this.deepestBeyond)
            this.deepestBeyond = beyond;

        if (this.deepestBeyond > failureDistance)
        {
            // Through the level and beyond the tolerance for a wick. This is not a worse
            // setup, it is the absence of one.
            this.state = RetestClass.ReclaimFailure;
            return;
        }

        // Movement back in the break direction, measured from the level, confirms the hold.
        if (-beyond >= holdDistance)
        {
            this.holdConfirmed = true;

            this.state = this.deepestBeyond > tolerance
                ? RetestClass.DeepButReclaimed
                : RetestClass.Clean;
        }
    }

    public void OnBar(in BarEvent bar, TimeFrame timeFrame)
    {
        // Classification is a tick-level question; a bar cannot say where within it price
        // went first, and a retest that failed and recovered inside one bar is not a hold.
    }

    public void OnBook(in BookDelta delta)
    {
        // Geometry only.
    }

    public void OnL3(in L3Event l3)
    {
        // Geometry only.
    }

    public void OnSessionPhase(SessionPhase phase)
    {
        if (phase is SessionPhase.Idle or SessionPhase.OrForming)
            this.Reset();
    }

    /// <summary>The current classification.</summary>
    public RetestState Read()
    {
        if (this.state == RetestClass.None)
            return RetestState.Idle;

        var depthTicks = this.instrument.TickSize > 0
            ? Math.Max(this.deepestBeyond, 0d) / this.instrument.TickSize
            : 0d;

        var depthFraction = this.rangeWidth > 0
            ? Math.Max(this.deepestBeyond, 0d) / this.rangeWidth
            : 0d;

        return new RetestState(
            this.state, this.levelPrice, this.direction,
            depthTicks, depthFraction, this.holdConfirmed,
            this.breakVolume, this.retestVolume);
    }

    public FeatureOutput Fold(SessionContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        var reading = this.Read();

        if (!reading.IsTradeable)
            return FeatureOutput.Silent(reading);

        // A tradeable retest argues for the break's direction.
        var score = this.direction == BreakDirection.Up ? 1f : -1f;

        // A clean retest is more convincing than a deep one, and a return quieter than the
        // break is more convincing than one that is not.
        var depthConfidence = reading.Class == RetestClass.Clean ? 1.0d : 0.7d;

        var volumeConfidence = double.IsNaN(reading.VolumeRatio)
            ? 0.8d
            : Math.Clamp(1.2d - reading.VolumeRatio, 0.3d, 1.0d);

        return new FeatureOutput(
            score, (float)Math.Clamp(depthConfidence * volumeConfidence, 0d, 1d), reading);
    }

    public void Reset()
    {
        this.state = RetestClass.None;
        this.levelPrice = double.NaN;
        this.rangeWidth = 0d;
        this.breakVolume = 0d;
        this.retestVolume = 0d;
        this.deepestBeyond = 0d;
        this.touched = false;
        this.holdConfirmed = false;
        this.brokeAtUtc = default;
    }

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "M11 {0} at {1:N2} depth {2:N1} ticks",
        this.state, this.levelPrice, this.Read().DepthTicks);
}
