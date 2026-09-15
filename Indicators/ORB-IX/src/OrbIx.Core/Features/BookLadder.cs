using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Features;

/// <summary>
/// Resting size at each price, and enough of its recent past to answer what it was.
///
/// WHAT THIS IS FOR. Absorption asks whether size HELD at a price while volume traded
/// through it, and that question cannot be answered from the book's current state alone —
/// it needs the size as it was one window ago. This keeps exactly that much history and no
/// more.
///
/// THE TOUCH, AND NOTHING DEEPER. Two measured facts force this and neither is a
/// preference. Live Level 2 supplies NO level index — <c>OrbIxIndicator.OnLevel2</c> passes
/// -1 deliberately, because "reporting a fabricated index would let a deep level be read as
/// the touch". And the captured <c>book_level</c> table is top-of-book only: 93,257,059
/// MNQU6 rows, every one carrying <c>level_index = 0</c>. A ladder built from deeper levels
/// would therefore exist on the chart and be unreproducible in the replay, which is the
/// "result for a system nobody runs" divergence the shared evaluator exists to prevent.
///
/// So the best bid and best ask are DERIVED from the prices this ladder holds, rather than
/// trusted from an index the feed does not send.
/// </summary>
public sealed class BookLadder
{
    /// <summary>
    /// Prices retained per side.
    ///
    /// MEASURED, NOT CHOSEN. This was 512 and that number was a guess, which is exactly how
    /// it went wrong: one MNQU6 session on 2026-08-05 visits 5,344 distinct bid prices and
    /// 5,131 ask prices, so the cap bound continuously and the ladder thrashed. Absorption
    /// read "no baseline" on 81.7% of sides; with pruning disabled on the same session it
    /// was 0.3%. The eviction rule below is the real fix; this number is now simply set
    /// above what a session actually uses so the bound is not reached in normal operation.
    /// </summary>
    public const int DefaultMaxPricesPerSide = 8192;

    private readonly Dictionary<double, LevelHistory> bids = new();
    private readonly Dictionary<double, LevelHistory> asks = new();
    private readonly TimeSpan retain;
    private readonly int maxPricesPerSide;

    private DateTime lastUpdateUtc = DateTime.MinValue;

    /// <param name="retain">
    /// How much history each price keeps. Must cover the absorption window, or the size a
    /// window ago is already gone by the time it is asked for.
    /// </param>
    /// <param name="maxPricesPerSide">Prices retained per side before the furthest are dropped.</param>
    public BookLadder(TimeSpan retain, int maxPricesPerSide = DefaultMaxPricesPerSide)
    {
        if (retain <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retain), retain,
                "A ladder that retains no history cannot say what the size was, which is the "
                + "only question it exists to answer.");
        }

        if (maxPricesPerSide < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPricesPerSide), maxPricesPerSide,
                "A book needs room for at least one price on each side of the touch.");
        }

        this.retain = retain;
        this.maxPricesPerSide = maxPricesPerSide;
    }

    /// <summary>
    /// Whether the current book state is known.
    ///
    /// FALSE AFTER A RESET, AND THAT IS NOT THE SAME AS AN EMPTY BOOK. The platform
    /// withdraws state without naming a level, and the shipped documentation "does not say
    /// whether a priceless removal marker clears one level or the whole book"
    /// (<see cref="BookDelta.TryFromFeed"/>). Reporting an empty book would be a claim; this
    /// reports that nothing can be vouched for until a priced update arrives, which is true
    /// under either reading.
    /// </summary>
    public bool IsKnown { get; private set; }

    /// <summary>Instant of the most recent priced update.</summary>
    public DateTime LastUpdateUtc => this.lastUpdateUtc;

    /// <summary>Prices currently held on each side, for diagnostics.</summary>
    public int BidPrices => this.bids.Count;

    /// <summary>Prices currently held on each side, for diagnostics.</summary>
    public int AskPrices => this.asks.Count;

    /// <summary>
    /// Folds one book event.
    ///
    /// A size of ZERO removes the price. That is a real state of the book, not a fault, and
    /// sweeping it up with the malformed values would discard genuine observations — the
    /// distinction <see cref="Sessions.OrBuilder.OnBook"/> already draws.
    /// </summary>
    public void OnBook(in BookDelta delta)
    {
        if (delta.IsReset)
        {
            // The state goes UNKNOWN rather than empty, and the history is deliberately kept:
            // what the size was five seconds ago remains a true measurement of the past, and
            // a withdrawal now does not make it false. What cannot be vouched for is the
            // CURRENT size, which is what IsKnown gates.
            this.IsKnown = false;
            return;
        }

        // Deeper levels are ignored, matching OrBuilder and MicroQuality. Live sends -1
        // (unknown) and the capture sends 0; both are the touch or near it, and both pass.
        if (delta.LevelIndex > 0)
            return;

        // TryFromFeed guarantees a priced event carries real numbers, but this type is also
        // constructed directly by the replay and by tests through the public constructor,
        // which adjudicates nothing. The guard states what it needs rather than assuming an
        // upstream that may not be the one calling.
        if (double.IsNaN(delta.Price) || double.IsInfinity(delta.Price)
            || double.IsNaN(delta.Size) || double.IsInfinity(delta.Size)
            || delta.Size < 0)
        {
            return;
        }

        var book = delta.Side == BookSide.Bid ? this.bids : this.asks;

        this.IsKnown = true;
        this.lastUpdateUtc = delta.TimestampUtc;

        if (!book.TryGetValue(delta.Price, out var history))
        {
            if (delta.Size <= 0)
                return;         // Removing a price that was never held is a no-op, not an entry.

            history = new LevelHistory();
            book[delta.Price] = history;
        }

        history.Record(delta.TimestampUtc, delta.Size);
        history.Trim(delta.TimestampUtc - this.retain);

        this.Prune(book, delta.Side, delta.TimestampUtc);
    }

    /// <summary>Current resting size at a price, or null when that price is not held.</summary>
    public double? CurrentSize(double price, BookSide side)
    {
        var book = side == BookSide.Bid ? this.bids : this.asks;

        return book.TryGetValue(price, out var history) ? history.Current : null;
    }

    /// <summary>
    /// Resting size at a price as of an instant, or null when nothing was recorded at or
    /// before it.
    ///
    /// Null means "not observed", never zero. A level this ladder had not yet seen and a
    /// level that was genuinely empty are different facts, and the caller must be able to
    /// refuse the question rather than compute a reduction from an invented baseline.
    /// </summary>
    public double? SizeAt(double price, BookSide side, DateTime atUtc)
    {
        var book = side == BookSide.Bid ? this.bids : this.asks;

        return book.TryGetValue(price, out var history) ? history.SizeAt(atUtc) : null;
    }

    /// <summary>
    /// Best bid, or null when none is held or the book is unknown.
    ///
    /// DERIVED, NOT REPORTED. The feed does not send a level index live, so the touch is the
    /// highest bid price this ladder currently holds with size on it.
    /// </summary>
    public double? BestBid() => this.Best(this.bids, highest: true);

    /// <summary>Best ask, derived the same way: the lowest ask price holding size.</summary>
    public double? BestAsk() => this.Best(this.asks, highest: false);

    /// <summary>Drops every price and marks the book unknown.</summary>
    public void Reset()
    {
        this.bids.Clear();
        this.asks.Clear();
        this.IsKnown = false;
        this.lastUpdateUtc = DateTime.MinValue;
    }

    private double? Best(Dictionary<double, LevelHistory> book, bool highest)
    {
        if (!this.IsKnown)
            return null;

        var best = double.NaN;

        foreach (var (price, history) in book)
        {
            if (history.Current <= 0)
                continue;

            if (double.IsNaN(best) || (highest ? price > best : price < best))
                best = price;
        }

        return double.IsNaN(best) ? null : best;
    }

    /// <summary>
    /// Drops prices furthest from the touch once the cap is exceeded.
    ///
    /// FURTHEST, NOT OLDEST. Evicting by age would drop a quiet level sitting right at the
    /// touch — precisely the level absorption is asked about — while keeping a busy one far
    /// away that nothing will ever ask about.
    ///
    /// EVICTION MAY NEVER DESTROY HISTORY THE WINDOW STILL NEEDS, and that is not a
    /// refinement — it is the fix for a measured defect. Evicting a price deletes its
    /// samples, so when the touch later arrived at an evicted price there was no size to
    /// compare against and absorption reported Unmeasured. Isolated on 2026-08-05 by
    /// changing this one variable: with the cap binding, 81.7% of sides had no baseline;
    /// with it unreachable, 0.3%. A price whose most recent sample falls inside the retained
    /// span is therefore never evicted, however far from the touch it sits.
    ///
    /// The cap can consequently be EXCEEDED, and that is deliberate. Bounding memory must
    /// never corrupt the measurement it exists to serve — the same reason a price still
    /// holding size was already exempt.
    /// </summary>
    private void Prune(Dictionary<double, LevelHistory> book, BookSide side, DateTime nowUtc)
    {
        if (book.Count <= this.maxPricesPerSide)
            return;

        var touch = side == BookSide.Bid ? this.BestBid() : this.BestAsk();

        if (touch is not { } reference)
            return;

        var protectedSince = nowUtc - this.retain;

        while (book.Count > this.maxPricesPerSide)
        {
            var furthestPrice = double.NaN;
            var furthestDistance = -1d;

            foreach (var (price, history) in book)
            {
                // Still holding size, or still inside the retained span: not evictable.
                if (history.Current > 0 || history.LastSampleUtc >= protectedSince)
                    continue;

                var distance = Math.Abs(price - reference);

                if (distance > furthestDistance)
                {
                    furthestDistance = distance;
                    furthestPrice = price;
                }
            }

            // Nothing evictable left. The cap stands unmet rather than a live or still-needed
            // level being discarded.
            if (double.IsNaN(furthestPrice))
                return;

            book.Remove(furthestPrice);
        }
    }

    /// <summary>
    /// One price's size over time.
    ///
    /// Samples are appended in arrival order and trimmed from the front, so the series is
    /// sorted by construction and a lookup is a walk rather than a sort.
    /// </summary>
    private sealed class LevelHistory
    {
        private readonly List<(DateTime AtUtc, double Size)> samples = new();

        public double Current { get; private set; }

        /// <summary>When this price was last written. Drives the eviction guard above.</summary>
        public DateTime LastSampleUtc { get; private set; } = DateTime.MinValue;

        public void Record(DateTime atUtc, double size)
        {
            this.Current = size;

            if (atUtc > this.LastSampleUtc)
                this.LastSampleUtc = atUtc;

            // AN OUT-OF-ORDER SAMPLE IS DROPPED FROM THE SERIES, NOT FROM THE CURRENT SIZE.
            // Live Level 2 timestamps are not monotonic — about 45% arrive stamped 1970 and
            // are replaced by the receive instant, which can interleave. Inserting one out of
            // order would break the walk below; ignoring the event entirely would discard a
            // real observation of the current size.
            if (this.samples.Count > 0 && atUtc < this.samples[^1].AtUtc)
                return;

            this.samples.Add((atUtc, size));
        }

        public void Trim(DateTime cutoffUtc)
        {
            // ONE SAMPLE AT OR BEFORE THE CUTOFF IS KEPT. Trimming everything older would
            // discard the very sample that answers "what was the size a window ago" for a
            // level that has not changed since — which is exactly a level that is holding,
            // and exactly what absorption is looking for.
            var keepFrom = 0;

            for (var i = 0; i < this.samples.Count; i++)
            {
                if (this.samples[i].AtUtc <= cutoffUtc)
                    keepFrom = i;
                else
                    break;
            }

            if (keepFrom > 0)
                this.samples.RemoveRange(0, keepFrom);
        }

        public double? SizeAt(DateTime atUtc)
        {
            double? found = null;

            foreach (var (sampleAt, size) in this.samples)
            {
                if (sampleAt > atUtc)
                    break;

                found = size;
            }

            return found;
        }
    }
}
