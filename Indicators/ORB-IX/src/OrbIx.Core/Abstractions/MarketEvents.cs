using System;

namespace OrbIx.Core.Abstractions;

/// <summary>Which side initiated a trade.</summary>
public enum Aggressor
{
    /// <summary>Could not be classified against a prevailing quote.</summary>
    Unknown,

    /// <summary>Traded at or above the prevailing ask.</summary>
    Buy,

    /// <summary>Traded at or below the prevailing bid.</summary>
    Sell,
}

/// <summary>Side of the book.</summary>
public enum BookSide
{
    Bid,
    Ask,
}

/// <summary>What happened to a resting order.</summary>
public enum L3Action
{
    Add,
    Modify,
    Cancel,
    Trade,
}

/// <summary>
/// A single print. Carries the quote that prevailed at the instant of the trade so
/// classification is a property of the event rather than of whatever the book happened to
/// look like when a consumer got round to reading it — the ordering bug that makes delta
/// quietly wrong.
/// </summary>
public readonly struct TickEvent
{
    public TickEvent(
        DateTime timestampUtc, double price, double size, Aggressor aggressor, double bid, double ask)
    {
        this.TimestampUtc = timestampUtc;
        this.Price = price;
        this.Size = size;
        this.Aggressor = aggressor;
        this.Bid = bid;
        this.Ask = ask;
    }

    public DateTime TimestampUtc { get; }
    public double Price { get; }
    public double Size { get; }
    public Aggressor Aggressor { get; }

    /// <summary>Best bid prevailing at the print. <see cref="double.NaN"/> when unknown.</summary>
    public double Bid { get; }

    /// <summary>Best ask prevailing at the print. <see cref="double.NaN"/> when unknown.</summary>
    public double Ask { get; }

    /// <summary>Signed size: positive for buy-initiated, negative for sell-initiated, zero when unclassified.</summary>
    public double SignedSize => this.Aggressor switch
    {
        Aggressor.Buy => this.Size,
        Aggressor.Sell => -this.Size,
        _ => 0d,
    };
}

/// <summary>
/// How a bar series is aggregated. Time is the common case; the specification also calls
/// for tick and volume bars on the execution tier, because entry timing keyed to
/// participation beats entry timing keyed to the clock.
/// </summary>
public enum TimeFrameKind
{
    Time,
    Tick,
    Volume,
}

/// <summary>
/// A bar series identity. Comparable and hashable so modules can key state per series
/// without allocating.
/// </summary>
public readonly struct TimeFrame : IEquatable<TimeFrame>
{
    private TimeFrame(TimeFrameKind kind, TimeSpan period, int count)
    {
        this.Kind = kind;
        this.Period = period;
        this.Count = count;
    }

    public TimeFrameKind Kind { get; }

    /// <summary>Bar duration for <see cref="TimeFrameKind.Time"/>; otherwise zero.</summary>
    public TimeSpan Period { get; }

    /// <summary>Ticks or contracts per bar for the non-time kinds; otherwise zero.</summary>
    public int Count { get; }

    public static TimeFrame OfTime(TimeSpan period)
        => period > TimeSpan.Zero
            ? new TimeFrame(TimeFrameKind.Time, period, 0)
            : throw new ArgumentOutOfRangeException(nameof(period), period, "A time frame's period must be positive.");

    public static TimeFrame OfTicks(int count)
        => count > 0
            ? new TimeFrame(TimeFrameKind.Tick, TimeSpan.Zero, count)
            : throw new ArgumentOutOfRangeException(nameof(count), count, "A tick bar must contain at least one tick.");

    public static TimeFrame OfVolume(int count)
        => count > 0
            ? new TimeFrame(TimeFrameKind.Volume, TimeSpan.Zero, count)
            : throw new ArgumentOutOfRangeException(nameof(count), count, "A volume bar must contain at least one contract.");

    /// <summary>
    /// Parses the configuration forms: a duration such as "15s" or "5m", "500tick", or
    /// "1000vol".
    /// </summary>
    public static bool TryParse(string? text, out TimeFrame timeFrame)
    {
        timeFrame = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        if (trimmed.EndsWith("tick", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(trimmed[..^4], System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out var ticks) && ticks > 0)
            {
                timeFrame = OfTicks(ticks);
                return true;
            }

            return false;
        }

        if (trimmed.EndsWith("vol", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(trimmed[..^3], System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out var volume) && volume > 0)
            {
                timeFrame = OfVolume(volume);
                return true;
            }

            return false;
        }

        if (Duration.TryParse(trimmed, out var period) && period > TimeSpan.Zero)
        {
            timeFrame = OfTime(period);
            return true;
        }

        return false;
    }

    public bool Equals(TimeFrame other)
        => this.Kind == other.Kind && this.Period == other.Period && this.Count == other.Count;

    public override bool Equals(object? obj) => obj is TimeFrame other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Kind, this.Period, this.Count);

    public static bool operator ==(TimeFrame left, TimeFrame right) => left.Equals(right);

    public static bool operator !=(TimeFrame left, TimeFrame right) => !left.Equals(right);

    public override string ToString() => this.Kind switch
    {
        TimeFrameKind.Time => Duration.Format(this.Period),
        TimeFrameKind.Tick => this.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "tick",
        TimeFrameKind.Volume => this.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "vol",
        _ => this.Kind.ToString(),
    };
}

/// <summary>A completed or forming bar.</summary>
public readonly struct BarEvent
{
    public BarEvent(
        DateTime openTimeUtc, DateTime closeTimeUtc,
        double open, double high, double low, double close,
        double volume, double delta, bool isClosed)
    {
        this.OpenTimeUtc = openTimeUtc;
        this.CloseTimeUtc = closeTimeUtc;
        this.Open = open;
        this.High = high;
        this.Low = low;
        this.Close = close;
        this.Volume = volume;
        this.Delta = delta;
        this.IsClosed = isClosed;
    }

    public DateTime OpenTimeUtc { get; }
    public DateTime CloseTimeUtc { get; }
    public double Open { get; }
    public double High { get; }
    public double Low { get; }
    public double Close { get; }
    public double Volume { get; }

    /// <summary>Buy-initiated minus sell-initiated volume. Zero when the tier cannot classify.</summary>
    public double Delta { get; }

    /// <summary>
    /// Whether the bar is final. Modules that act on a forming bar and modules that act on
    /// a closed one are doing different things, and conflating them is how a backtest
    /// acquires lookahead.
    /// </summary>
    public bool IsClosed { get; }

    public double Range => this.High - this.Low;
}

/// <summary>
/// One change to one price level of the aggregated book. A size of zero removes the level.
///
/// <see cref="TimestampUtc"/> IS ALWAYS A USABLE INSTANT, and that invariant is the point of
/// this type carrying three time-related members instead of one.
///
/// MEASURED 2026-08-21 on a live session: 8,933 of the first 20,000 Level 2 updates arrived
/// stamped 1970-01-01T00:00:00Z — the Unix epoch — while every one of 20,000 trades carried a
/// real time. That value reached two decisions and corrupted both. MicroQuality assigns it to
/// its last-quote instant, and because 1970 is not DateTime.MinValue it was not caught as
/// "no quote seen" but computed as an age of about fifty-six years, so the gate reported a
/// catastrophically stale quote and refused entries. OrBuilder discards any delta before its
/// window opens, and 1970 always is, so nearly half the book updates were dropped from the
/// range's statistics with nothing saying so.
///
/// WHERE IT COMES FROM, traced through the installed binaries rather than guessed. The shipped
/// SDK documentation describes Level2Quote's price, size, id, broker and order count and says
/// nothing about its time at all, so the assemblies were decompiled. Time is declared on the
/// base MessageQuote as a plain property with no initialiser — default(DateTime) is 0001-01-01,
/// not 1970 — so something assigns it. the vendor connector.dll's depth handlers do, unconditionally:
///
///     DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(P_0.Ssboe)
///                             .UtcDateTime.AddMicroseconds(P_0.Usecs);
///
/// and pass that straight into new Level2Quote(..., dateTime). A depth message carrying
/// Ssboe = 0 therefore becomes exactly 1970-01-01T00:00:00Z, and the connector validates
/// nothing before converting.
///
/// WHY one data vendor SENDS Ssboe = 0 on those messages is upstream of every binary on this machine
/// and remains UNVERIFIED. What is established is that nothing between the wire and this type
/// will stop the value, which is why the boundary here has to.
///
/// So a source time that is not plausible is replaced by the instant the event was received —
/// a real measurement rather than an invention — and the original is preserved so the
/// platform's behaviour stays visible in a recording.
///
/// THE PRICE INVARIANT HAS EXACTLY ONE EXCEPTION, and it is named rather than implicit.
/// <see cref="Price"/> and <see cref="Size"/> are real numbers on every event except a
/// <see cref="IsReset"/> one, which is the platform withdrawing book state without naming a
/// level. A consumer that reads either value branches on <see cref="IsReset"/> first.
/// </summary>
public readonly struct BookDelta
{
    /// <summary>
    /// Builds a delta whose timestamp is already known to be usable.
    ///
    /// Used by callers that generate their own events — tests and the offline replay — where
    /// the time is theirs and needs no adjudication.
    /// </summary>
    public BookDelta(DateTime timestampUtc, BookSide side, double price, double size, int levelIndex, int orderCount)
    {
        this.TimestampUtc = timestampUtc;
        this.SourceTimestampUtc = timestampUtc;
        this.SourceTimeUsable = true;
        this.Side = side;
        this.Price = price;
        this.Size = size;
        this.LevelIndex = levelIndex;
        this.OrderCount = orderCount;
        this.Closed = false;
        this.IsReset = false;
    }

    private BookDelta(
        DateTime timestampUtc, DateTime sourceTimestampUtc, bool sourceTimeUsable,
        BookSide side, double price, double size, int levelIndex, int orderCount,
        bool closed, bool isReset)
    {
        this.TimestampUtc = timestampUtc;
        this.SourceTimestampUtc = sourceTimestampUtc;
        this.SourceTimeUsable = sourceTimeUsable;
        this.Side = side;
        this.Price = price;
        this.Size = size;
        this.LevelIndex = levelIndex;
        this.OrderCount = orderCount;
        this.Closed = closed;
        this.IsReset = isReset;
    }

    /// <summary>
    /// Builds a delta from a platform event, adjudicating everything the platform supplied.
    ///
    /// THE ONLY WAY A FEED EVENT ENTERS THE ENGINE. Everything downstream may then read
    /// <see cref="TimestampUtc"/>, <see cref="Price"/> and <see cref="Size"/> without checking
    /// them, because there is no path by which an implausible timestamp or a non-numeric price
    /// reaches them.
    /// </summary>
    /// <param name="sourceTimestampUtc">What the platform said. May be anything.</param>
    /// <param name="receivedUtc">When the handler got it. Ours, and always real.</param>
    /// <param name="side">Bid or ask.</param>
    /// <param name="price">The level's price.</param>
    /// <param name="size">Resting size; zero removes the level.</param>
    /// <param name="levelIndex">Distance from the touch; negative when the feed does not say.</param>
    /// <param name="orderCount">Orders at the level; zero when the feed does not report it.</param>
    /// <param name="closed">
    /// The platform's removal flag. The shipped SDK documents Level2Quote.Closed as "shows
    /// whether Level2 quote is using only for removing from depth".
    /// </param>
    /// <param name="delta">The event, when it is usable.</param>
    /// <returns>
    /// False when the feed supplied no usable price or size AND did not mark the event as a
    /// removal. A removal marker with no price is admitted as a <see cref="IsReset"/> event.
    /// </returns>
    public static bool TryFromFeed(
        DateTime sourceTimestampUtc, DateTime receivedUtc,
        BookSide side, double price, double size, int levelIndex, int orderCount, bool closed,
        out BookDelta delta)
    {
        delta = default;

        // AN EVENT WITH NO PRICE CANNOT IDENTIFY A LEVEL. Measured 2026-08-22: every Level 2
        // subscription delivers two sentinel messages — one Ask, one Bid, microseconds apart —
        // whose price and size are both NaN, followed by the book snapshot proper. Ten such
        // pairs appear in a single day's recording, one at each indicator load.
        //
        // Folding them as data cost more than a strange line in a file: OrBuilder's guard read
        // "total <= 0", every comparison against NaN is false, and one sentinel turned the
        // range's book imbalance into NaN permanently.
        //
        // ZERO IS NOT REFUSED. A size of zero removes a level and is a real state of the
        // book; only a value that is not a number at all is rejected.
        var priced = IsReal(price) && IsReal(size);

        // BUT DROPPING IT ENTIRELY IS ALSO WRONG WHEN THE PLATFORM SAID "Closed". Measured
        // 2026-08-22 on a live session, those sentinels arrive with Closed set, and the SDK
        // documents that flag as meaning the quote exists only to REMOVE something from the
        // depth. Silently discarding a withdrawal leaves every consumer describing a book
        // state that has been taken away.
        //
        // WHAT IT REMOVES IS NOT ESTABLISHED. The documentation does not say whether a
        // priceless removal marker clears one level or the whole book, and the vendor's
        // intent is not guessed here. It is therefore admitted as a RESET — an event that
        // carries no level and no size, only the fact that something was withdrawn — and each
        // consumer decides what it can no longer vouch for. That reading is true under either
        // interpretation; continuing to report the withdrawn state is false under both.
        if (!priced && !closed)
            return false;

        var usable = IsPlausible(sourceTimestampUtc, receivedUtc);

        delta = new BookDelta(
            usable ? sourceTimestampUtc : receivedUtc,
            sourceTimestampUtc,
            usable,
            side,
            priced ? price : double.NaN,
            priced ? size : double.NaN,
            levelIndex,
            orderCount,
            closed,
            isReset: !priced);

        return true;
    }

    /// <summary>A price or size that can be arithmetic. NaN and the infinities cannot.</summary>
    public static bool IsReal(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// Whether a feed's timestamp can be believed, judged against when it arrived.
    ///
    /// A RULE, NOT A TEST FOR 1970. Matching the epoch specifically would fix the one bad value
    /// observed and let the next one through — a connector that emits a zero today can emit a
    /// wrong-by-a-year value tomorrow, and that would be far harder to notice.
    ///
    /// The bounds are an engineering choice and are labelled as one: THE SHIPPED DOCUMENTATION
    /// SAYS NOTHING about this field, so there is nothing to cite. A little slack in the future
    /// because the exchange's clock and this machine's are not the same one, and a bounded past
    /// because a book update older than that is not describing the book anyone is trading.
    /// </summary>
    public static bool IsPlausible(DateTime sourceTimestampUtc, DateTime receivedUtc)
    {
        if (sourceTimestampUtc == default || receivedUtc == default)
            return false;

        var skew = sourceTimestampUtc - receivedUtc;

        return skew <= MaxFutureSkew && skew >= -MaxAge;
    }

    /// <summary>Clock disagreement allowed in the future direction.</summary>
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(5);

    /// <summary>Beyond this, an update is not describing the current book.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When this happened, always usable. Equal to <see cref="SourceTimestampUtc"/> when the
    /// platform supplied a plausible one, and the receive instant when it did not.
    /// </summary>
    public DateTime TimestampUtc { get; }

    /// <summary>
    /// Exactly what the platform said, preserved even when it is nonsense.
    ///
    /// Kept so a recording still shows what arrived. The platform's behaviour here is
    /// unexplained, and replacing the evidence would make it unexplainable.
    /// </summary>
    public DateTime SourceTimestampUtc { get; }

    /// <summary>Whether the platform's own timestamp was believable.</summary>
    public bool SourceTimeUsable { get; }

    public BookSide Side { get; }

    /// <summary>
    /// The level's price — REAL UNLESS <see cref="IsReset"/>, in which case it is
    /// <see cref="double.NaN"/> because a withdrawal names no level.
    /// </summary>
    public double Price { get; }

    /// <summary>
    /// Resting size — REAL UNLESS <see cref="IsReset"/>, in which case it is
    /// <see cref="double.NaN"/> because a withdrawal reports no size.
    /// </summary>
    public double Size { get; }

    /// <summary>
    /// The platform's removal flag, exactly as it arrived.
    ///
    /// Recorded even when the event also carries a real price, so a recording shows the
    /// vendor's own marking rather than this type's interpretation of it.
    /// </summary>
    public bool Closed { get; }

    /// <summary>
    /// True when the event withdraws book state without naming a level.
    ///
    /// THE ONE CASE WHERE <see cref="Price"/> AND <see cref="Size"/> ARE NOT NUMBERS. Every
    /// consumer that reads either must branch on this first; the three that do not read them
    /// need no branch. What a consumer does about it is its own decision — the shared rule is
    /// only that it may no longer vouch for what it held.
    /// </summary>
    public bool IsReset { get; }

    /// <summary>Distance from the touch, zero-based. Negative when the feed does not say.</summary>
    public int LevelIndex { get; }

    /// <summary>Orders resting at the level; zero when the feed does not report it.</summary>
    public int OrderCount { get; }
}

/// <summary>
/// One order-lifecycle event. Produced natively where the feed carries per-order
/// identifiers, or reconstructed from book deltas where it does not — the two paths are
/// interchangeable behind <see cref="IL3Provider"/>, and
/// <see cref="L3Event.IsReconstructed"/> records which one a consumer is looking at so a
/// reconstruction is never mistaken for an observation.
/// </summary>
public readonly struct L3Event
{
    public L3Event(
        DateTime timestampUtc, string orderId, BookSide side, double price, double size,
        L3Action action, long priority, bool isReconstructed)
    {
        this.TimestampUtc = timestampUtc;
        this.OrderId = orderId;
        this.Side = side;
        this.Price = price;
        this.Size = size;
        this.Action = action;
        this.Priority = priority;
        this.IsReconstructed = isReconstructed;
    }

    public DateTime TimestampUtc { get; }
    public string OrderId { get; }
    public BookSide Side { get; }
    public double Price { get; }
    public double Size { get; }
    public L3Action Action { get; }

    /// <summary>Queue priority where the feed supplies it; zero otherwise.</summary>
    public long Priority { get; }

    /// <summary>
    /// True when this event was inferred from aggregated book deltas rather than observed
    /// per order. Reconstruction cannot recover queue position, order age, or the
    /// distinction between one large order and many small ones.
    /// </summary>
    public bool IsReconstructed { get; }
}
