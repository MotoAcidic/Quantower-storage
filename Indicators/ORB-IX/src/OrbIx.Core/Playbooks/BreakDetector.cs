using System;
using System.Globalization;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Features;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Playbooks;

/// <summary>
/// Where the session's range stands relative to price.
/// </summary>
/// <param name="Broken">Whether the range has been broken at all.</param>
/// <param name="Direction">Which way, when broken.</param>
/// <param name="Level">The edge that was broken.</param>
/// <param name="BrokeAtUtc">When the break confirmed.</param>
/// <param name="BreakPrice">The price at which it confirmed.</param>
/// <param name="ExcursionTicks">Furthest travel beyond the edge since the break.</param>
/// <param name="VolumeOnBreak">Volume traded during the breaking bar.</param>
/// <param name="Velocity">
/// Ticks per second on the break, against the session's own median bar velocity. Above one
/// is faster than this session has been moving.
/// </param>
/// <param name="ReEntered">Whether price has traded back inside the range since.</param>
/// <param name="BreakCount">Breaks confirmed this session, in either direction.</param>
/// <param name="BarLow">Low of the bar that confirmed the break.</param>
/// <param name="BarHigh">High of the bar that confirmed the break.</param>
public sealed record BreakState(
    bool Broken,
    BreakDirection Direction,
    double Level,
    DateTime BrokeAtUtc,
    double BreakPrice,
    double ExcursionTicks,
    double VolumeOnBreak,
    double Velocity,
    bool ReEntered,
    int BreakCount,
    double BarLow,
    double BarHigh)
{
    /// <summary>Whether this is the session's first break, which P1 requires.</summary>
    public bool IsFirstBreak => this.BreakCount == 1;

    public TradeDirection AsTradeDirection =>
        this.Direction == BreakDirection.Up ? TradeDirection.Long : TradeDirection.Short;

    /// <summary>
    /// The price beyond which the break bar itself is invalidated — the far side of the bar
    /// that confirmed it. §5 measures P1's stop from here rather than from the opposite
    /// range edge: the range edge is the whole width of the range away, which produces a
    /// stop so wide that one unit of risk overshoots every projection the ladder is
    /// built on.
    /// </summary>
    public double StructuralInvalidation =>
        this.Direction == BreakDirection.Up ? this.BarLow : this.BarHigh;

    public static BreakState None => new(
        false, BreakDirection.Up, double.NaN, default, double.NaN, 0d, 0d, 0d, false, 0,
        double.NaN, double.NaN);
}

/// <summary>
/// Detects a break of the session's opening range.
///
/// This lives with the playbooks rather than among the feature modules because "what counts
/// as a break" is a trigger definition, and §5 puts trigger definitions in the playbook. It
/// is separated into its own type so P1 and P2 share one definition — a retest of a break
/// P1 declined must be a retest of the same break P1 would have taken.
///
/// A break confirms on a bar CLOSING beyond the edge by the configured buffer, not on a
/// trade touching it. The distinction is the difference between a breakout and a wick, and
/// it is the single most consequential parameter in an opening-range system.
/// </summary>
public sealed class BreakDetector
{
    private readonly InstrumentSpec instrument;
    private readonly int breakBufferTicks;

    private OrSnapshot? range;
    private BreakState state = BreakState.None;
    private double barVolume;
    private double sessionVelocitySum;
    private int sessionVelocityCount;

    public BreakDetector(InstrumentSpec instrument, int breakBufferTicks)
    {
        this.instrument = instrument;
        this.breakBufferTicks = breakBufferTicks;

        if (!instrument.IsUsable)
            throw new ArgumentException("Instrument specifications must be usable.", nameof(instrument));

        if (breakBufferTicks < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(breakBufferTicks), breakBufferTicks, "The break buffer cannot be negative.");
        }
    }

    public BreakState State => this.state;

    /// <summary>
    /// Supplies the closed range. Nothing can break before this is known, which is why the
    /// engine only calls it once the opening range has closed.
    /// </summary>
    public void SetRange(OrSnapshot closedRange)
    {
        this.range = closedRange ?? throw new ArgumentNullException(nameof(closedRange));
    }

    /// <summary>
    /// Records a print. Tracks excursion beyond a confirmed break and whether price has
    /// come back inside; the break itself confirms on a bar close.
    /// </summary>
    public void OnTick(in TickEvent tick)
    {
        if (this.range is null || double.IsNaN(tick.Price))
            return;

        this.barVolume += tick.Size;

        if (!this.state.Broken)
            return;

        var beyond = this.state.Direction == BreakDirection.Up
            ? tick.Price - this.state.Level
            : this.state.Level - tick.Price;

        var beyondTicks = beyond / this.instrument.TickSize;

        var excursion = Math.Max(this.state.ExcursionTicks, beyondTicks);
        var reEntered = this.state.ReEntered || beyondTicks < 0;

        if (excursion != this.state.ExcursionTicks || reEntered != this.state.ReEntered)
            this.state = this.state with { ExcursionTicks = excursion, ReEntered = reEntered };
    }

    /// <summary>
    /// Evaluates a closed bar for a break, returning the new state when one confirmed and
    /// null otherwise.
    ///
    /// Only a closed bar can confirm: a bar still forming has not established that price
    /// accepted the level, and treating a forming bar's excursion as a break is how an
    /// opening-range system ends up buying every wick.
    /// </summary>
    public BreakState? OnBar(in BarEvent bar, TimeSpan barDuration)
    {
        if (this.range is null || !bar.IsClosed)
            return null;

        var volume = this.barVolume > 0 ? this.barVolume : bar.Volume;
        var velocity = this.RecordVelocity(bar, barDuration);
        this.barVolume = 0d;

        var buffer = this.breakBufferTicks * this.instrument.TickSize;

        var direction =
            bar.Close >= this.range.Orh + buffer ? BreakDirection.Up
            : bar.Close <= this.range.Orl - buffer ? BreakDirection.Down
            : (BreakDirection?)null;

        if (direction is null)
            return null;

        // A break in the same direction as the one already tracked is continuation, not a
        // new break. Counting every bar that closes beyond the edge would make "first break
        // of the session" meaningless within a minute.
        if (this.state.Broken && this.state.Direction == direction && !this.state.ReEntered)
            return null;

        var level = direction == BreakDirection.Up ? this.range.Orh : this.range.Orl;

        this.state = new BreakState(
            Broken: true,
            Direction: direction.Value,
            Level: level,
            BrokeAtUtc: bar.CloseTimeUtc,
            BreakPrice: bar.Close,
            ExcursionTicks: Math.Abs(bar.Close - level) / this.instrument.TickSize,
            VolumeOnBreak: volume,
            Velocity: velocity,
            ReEntered: false,
            BreakCount: this.state.BreakCount + 1,
            BarLow: bar.Low,
            BarHigh: bar.High);

        return this.state;
    }

    /// <summary>
    /// Bar velocity in ticks per second, expressed against the session's own median so far.
    /// Above one is faster than this session has been moving; the comparison is relative
    /// because a velocity that is explosive for gold is ordinary for the Nasdaq.
    /// </summary>
    private double RecordVelocity(in BarEvent bar, TimeSpan barDuration)
    {
        if (barDuration <= TimeSpan.Zero || this.instrument.TickSize <= 0)
            return 0d;

        var ticksPerSecond = bar.Range / this.instrument.TickSize / barDuration.TotalSeconds;

        this.sessionVelocitySum += ticksPerSecond;
        this.sessionVelocityCount++;

        var average = this.sessionVelocitySum / this.sessionVelocityCount;

        return average > 0 ? ticksPerSecond / average : 0d;
    }

    public void Reset()
    {
        this.range = null;
        this.state = BreakState.None;
        this.barVolume = 0d;
        this.sessionVelocitySum = 0d;
        this.sessionVelocityCount = 0;
    }

    public override string ToString() => this.state.Broken
        ? string.Format(
            CultureInfo.InvariantCulture,
            "break {0} at {1:N2}, excursion {2:N1} ticks, velocity {3:N2}x{4}",
            this.state.Direction, this.state.Level, this.state.ExcursionTicks,
            this.state.Velocity, this.state.ReEntered ? ", re-entered" : string.Empty)
        : "no break";
}
