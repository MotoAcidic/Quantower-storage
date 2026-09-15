using System;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Sessions;

/// <summary>
/// Builds fixed-duration bars from prints.
///
/// The engine needs these because a break confirms on a CLOSED BAR, not on a print:
/// <see cref="Core.Playbooks.BreakDetector.OnBar"/> is explicit that "a bar still forming has
/// not established that price accepted the level, and treating a forming bar's excursion as a
/// break is how an opening-range system ends up buying every wick". A replay that fed only
/// ticks would never confirm a break at all, and would report a null that was an artefact of
/// the harness rather than a fact about the market.
///
/// TWO PROPERTIES MATTER MORE THAN THEY LOOK.
///
/// Bars are aligned to absolute wall-clock boundaries, not to the first print seen. A 15-second
/// bar starts at :00, :15, :30, :45 of every minute regardless of when the session's first
/// trade happened to arrive. Anchoring to the first print would give a different bar grid on
/// every session, and a break that confirmed in one replay would not confirm in another over
/// the same data — which would destroy determinism and make results incomparable across days.
///
/// A bar is emitted only when a LATER print proves it closed — UNLESS a host deliberately asks
/// the clock, through <see cref="CloseIfElapsed"/>. That method did not exist while this class
/// said "nothing is inferred from the clock", and the sentence is now conditional rather than
/// absolute, so the property is stated where it is actually decided: by whether a host calls it.
///
/// NOTHING CALLS IT UNLESS IT CHOOSES TO, which is what keeps the change off every other
/// consumer. The offline replay and the Direction lanes drive ticks only and are byte-identical
/// to before; only the live chart asks the clock, and only because a display absorbed from
/// another indicator closed its bars that way and would otherwise fall silent on a quiet tape.
///
/// EVEN THEN, EMPTY PERIODS STILL PRODUCE NO BAR. The clock can only close a bar that prints
/// opened; it never manufactures one for a period nothing traded in. A stretch with no prints
/// is an absence, and inventing flat closes through it would feed the break detector a stream
/// of bars that never happened.
///
/// Empty periods produce NO bar. A stretch with no prints is not a bar with zero volume at the
/// previous close — it is an absence, and manufacturing bars through it would feed the break
/// detector a stream of flat closes that never happened.
/// </summary>
public sealed class BarAggregator
{
    private readonly TimeSpan period;

    private DateTime openTimeUtc;
    private double open;
    private double high;
    private double low;
    private double close;
    private double volume;
    private double delta;
    private bool forming;

    /// <summary>
    /// The bucket <see cref="CloseIfElapsed"/> last closed, or MinValue when the clock has never
    /// been asked. This is what makes the late-print guard apply only to hosts that use it.
    /// </summary>
    private DateTime clockClosedBucketUtc = DateTime.MinValue;

    public BarAggregator(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period), period, "A bar period must be positive.");
        }

        this.period = period;
    }

    /// <summary>The series identity, for modules that key state per timeframe.</summary>
    public TimeFrame TimeFrame => TimeFrame.OfTime(this.period);

    /// <summary>Whether a bar is currently accumulating.</summary>
    public bool HasOpenBar => this.forming;

    /// <summary>
    /// Adds a print.
    /// </summary>
    /// <param name="tick">The print.</param>
    /// <param name="closed">
    /// The bar this print CLOSED, when it fell into a later period than the one accumulating.
    /// </param>
    /// <returns>True when a closed bar was produced.</returns>
    public bool Add(in TickEvent tick, out BarEvent closed)
    {
        var bucket = this.BucketOf(tick.TimestampUtc);

        // A PRINT FOR A BUCKET THE CLOCK ALREADY CLOSED NEVER REOPENS IT. Without this the
        // branch below would Start() a bar back at the old bucket and emit it a second time,
        // and every consumer of OnBar assumes a bucket closes once. The print is counted rather
        // than silently dropped, so a feed that does this often is visible instead of inferred.
        //
        // GUARDED ON THE CLOCK CLOSE, NOT ON EVERY CLOSE, deliberately. A tick-driven close
        // leaves this at MinValue, so a host that never asks the clock — the offline replay,
        // the Direction lanes — behaves exactly as it did before this method existed.
        if (this.clockClosedBucketUtc > DateTime.MinValue && bucket <= this.clockClosedBucketUtc)
        {
            this.LatePrints++;
            this.LateVolume += tick.Size;
            closed = default;
            return false;
        }

        if (this.forming && bucket == this.openTimeUtc)
        {
            this.Accumulate(tick);
            closed = default;
            return false;
        }

        var hadBar = this.forming;
        var completed = this.Snapshot(isClosed: true);

        this.Start(bucket, tick);

        closed = completed;
        return hadBar;
    }

    /// <summary>
    /// Closes the bar still accumulating once its period has elapsed by the clock, plus a grace.
    ///
    /// WHY THE CLOCK IS ASKED AT ALL, given this class was built not to. Bars here close when a
    /// LATER print arrives, so on a quiet tape a bar that ended at 13:31 stays open until
    /// something trades — which may be twenty minutes later. Everything that reads a closed bar
    /// waits with it. The displays absorbed from Aramid Flow closed on the clock and would
    /// otherwise fall silent exactly when the tape did.
    ///
    /// THE GRACE IS WHY THIS IS NOT JUST A COMPARISON. A print's exchange stamp and its arrival
    /// are not the same instant, so closing the moment the period ends would cut off prints
    /// still in flight. The grace is how long to wait for them, and it is configuration rather
    /// than a constant because it is a property of the feed, not of the market.
    ///
    /// A bar closed this way is indistinguishable downstream from one a print closed: same
    /// event, same shape, same consumers. What differs is only WHEN it arrives.
    /// </summary>
    /// <param name="nowUtc">The instant to judge against.</param>
    /// <param name="grace">How long past the period's end to wait for prints still in flight.</param>
    /// <param name="closed">The bar that closed, when one did.</param>
    /// <returns>True when a bar closed.</returns>
    public bool CloseIfElapsed(DateTime nowUtc, TimeSpan grace, out BarEvent closed)
    {
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grace), grace, "A grace period cannot be negative.");
        }

        closed = default;

        if (!this.forming || nowUtc < this.openTimeUtc + this.period + grace)
            return false;

        closed = this.Snapshot(isClosed: true);
        this.forming = false;
        this.clockClosedBucketUtc = this.openTimeUtc;
        return true;
    }

    /// <summary>Prints refused because the clock had already closed their bucket.</summary>
    public long LatePrints { get; private set; }

    /// <summary>Volume on those prints. Carried so the size of what was refused is visible.</summary>
    public double LateVolume { get; private set; }

    /// <summary>
    /// Closes the bar still accumulating, if any.
    ///
    /// Called when the prints run out. The bar is real — it holds trades that happened — so
    /// discarding it would drop the end of every session.
    /// </summary>
    public bool Flush(out BarEvent closed)
    {
        if (!this.forming)
        {
            closed = default;
            return false;
        }

        closed = this.Snapshot(isClosed: true);
        this.forming = false;
        return true;
    }

    /// <summary>
    /// The bar as it currently stands, still forming.
    ///
    /// Offered separately from <see cref="Add"/> and never marked closed, so a caller cannot
    /// accidentally treat a partial bar as a confirmation.
    /// </summary>
    public bool TryPeek(out BarEvent forming)
    {
        if (!this.forming)
        {
            forming = default;
            return false;
        }

        forming = this.Snapshot(isClosed: false);
        return true;
    }

    /// <summary>
    /// The absolute period a print belongs to.
    ///
    /// Computed from ticks since the epoch so the grid is the same in every run and every
    /// session, rather than depending on when the first print arrived.
    /// </summary>
    private DateTime BucketOf(DateTime instant)
    {
        var periodTicks = this.period.Ticks;
        return new DateTime(instant.Ticks - (instant.Ticks % periodTicks), DateTimeKind.Utc);
    }

    private void Start(DateTime bucket, in TickEvent tick)
    {
        this.openTimeUtc = bucket;
        this.open = tick.Price;
        this.high = tick.Price;
        this.low = tick.Price;
        this.close = tick.Price;
        this.volume = tick.Size;
        this.delta = tick.SignedSize;
        this.forming = true;
    }

    private void Accumulate(in TickEvent tick)
    {
        if (tick.Price > this.high) this.high = tick.Price;
        if (tick.Price < this.low) this.low = tick.Price;

        this.close = tick.Price;
        this.volume += tick.Size;

        // SignedSize is zero for an unclassifiable print, so an unknown aggressor contributes
        // nothing to delta rather than being guessed onto one side.
        this.delta += tick.SignedSize;
    }

    private BarEvent Snapshot(bool isClosed)
        => new(
            this.openTimeUtc, this.openTimeUtc + this.period,
            this.open, this.high, this.low, this.close,
            this.volume, this.delta, isClosed);
}
