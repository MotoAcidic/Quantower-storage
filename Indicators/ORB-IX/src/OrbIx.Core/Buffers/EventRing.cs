using System;
using System.Threading;

namespace OrbIx.Core.Buffers;

/// <summary>
/// A fixed-capacity ring buffer for one producer and one consumer.
///
/// §10 makes this a build requirement rather than an optimisation: the Level 2 handler
/// fires thousands of times a second on an active instrument, and any allocation in a quote
/// handler is a bug. The handler writes a struct into a pre-sized array and returns; the
/// periodic fold drains it. Nothing is allocated after construction.
///
/// The capacity is a power of two so the wrap is a mask rather than a modulo, and full is
/// handled by dropping the OLDEST entry rather than the newest. That direction matters: a
/// consumer that has fallen behind is better served by recent market state than by stale
/// state, and a drop that silently kept the wrong end would make the fold act on a market
/// that no longer exists. Drops are counted so the condition is visible rather than
/// invisible.
///
/// Each index has exactly one writer. The producer owns the write index and the drop count;
/// the consumer owns the read index and skips forward itself when it finds it has been
/// lapped. An earlier version had the producer advance the READ index on overflow, which
/// raced with the consumer's own store to it — the consumer's stale value won, un-dropping
/// entries and handing the same slot out twice. A concurrency test caught it as an
/// accounting overflow. Single ownership per variable is what prevents that, not tighter
/// interlocking around a shared one.
///
/// Drops are counted by the producer, at the moment it overwrites an entry the consumer
/// never took. Counting them on the consumer's side instead would report zero for as long
/// as nobody read, which is precisely when the number matters most.
/// </summary>
public sealed class EventRing<T>
    where T : struct
{
    private readonly T[] items;

    /// <summary>
    /// Publication marker per slot, holding the one-based index of the entry currently in
    /// it, or a negative value while a write is in progress.
    ///
    /// This exists because a struct copy is not atomic. Without it the consumer can catch a
    /// slot mid-write and see some fields from the new entry and others from the old — a
    /// tick with a new price and a stale quote, which is not a tick that ever existed. A
    /// concurrency test found 2,215 such reads in 200,000 writes, so this is a measured
    /// failure mode rather than a theoretical one. The consumer reads the marker, copies,
    /// and reads the marker again: a change between the two means the copy may be torn and
    /// it is discarded rather than returned.
    /// </summary>
    private readonly long[] sequences;

    private readonly int mask;

    private long writeIndex;
    private long readIndex;
    private long dropped;

    /// <param name="capacity">
    /// Retained entries, rounded up to a power of two. Sized for the burst the fold interval
    /// must absorb, not for the session.
    /// </param>
    public EventRing(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "A ring must have capacity.");
        }

        var rounded = RoundUpToPowerOfTwo(capacity);
        this.items = new T[rounded];
        this.sequences = new long[rounded];
        this.mask = rounded - 1;
    }

    /// <summary>Entries the ring can hold.</summary>
    public int Capacity => this.items.Length;

    /// <summary>
    /// Entries written but not yet drained. A snapshot: with a producer running it is the
    /// count at the moment of reading and not a moment later.
    /// </summary>
    public int Count
    {
        get
        {
            var behind = Volatile.Read(ref this.writeIndex) - Volatile.Read(ref this.readIndex);
            return (int)Math.Min(behind, this.items.Length);
        }
    }

    /// <summary>
    /// Entries lost because the consumer fell behind. Non-zero means the fold is not keeping
    /// up, which is a fact about the system worth surfacing rather than a detail to hide.
    ///
    /// Approximate while a producer is running, and deliberately so. Making it exact would
    /// mean the producer and consumer agreeing on a shared count at every write — a
    /// compare-and-swap in the quote handler — which is the cost §10 exists to avoid. It is
    /// a health indicator, read as "is the fold keeping up", not an audited total. What the
    /// ring does guarantee exactly is that no entry is ever returned twice and no entry is
    /// ever returned torn; those are the properties correctness rests on, and both are
    /// tested under concurrency.
    /// </summary>
    public long Dropped => Volatile.Read(ref this.dropped);

    /// <summary>
    /// Writes an entry. Called from the market-data path: it copies and returns, and does
    /// not allocate, lock, or compute.
    ///
    /// The producer never reads or writes the read index. Overflow is the consumer's to
    /// notice and account for.
    /// </summary>
    public void Write(in T item)
    {
        var write = Volatile.Read(ref this.writeIndex);
        var slot = (int)(write & this.mask);

        // Count the loss here, where it happens. The producer knows it is about to overwrite
        // an entry the consumer never took; the consumer can only infer it later, and while
        // it is inferring, the count reads zero even though data has already gone. Reading
        // the consumer's index is safe — only writing it was the race.
        if (write - Volatile.Read(ref this.readIndex) >= this.items.Length)
            this.dropped++;

        // Mark the slot in progress, fill it, then publish. A consumer that reads the marker
        // either side of its copy can tell whether the copy spanned this write.
        Volatile.Write(ref this.sequences[slot], -1);
        this.items[slot] = item;
        Volatile.Write(ref this.sequences[slot], write + 1);

        Volatile.Write(ref this.writeIndex, write + 1);
    }

    /// <summary>
    /// Takes the next entry, or returns false when the ring is empty.
    ///
    /// If the producer has lapped the consumer, the oldest entries are skipped and counted
    /// before reading. The value is re-validated after the copy: a producer that laps during
    /// the copy itself could otherwise hand back a half-overwritten struct, and a torn entry
    /// is worse than a counted drop.
    /// </summary>
    public bool TryRead(out T item)
    {
        while (true)
        {
            var write = Volatile.Read(ref this.writeIndex);
            var read = Volatile.Read(ref this.readIndex);

            if (read >= write)
            {
                item = default;
                return false;
            }

            // Lapped: skip to the oldest entry still intact. The producer already counted
            // these as it overwrote them, so skipping must not count them a second time.
            var behind = write - read;

            if (behind > this.items.Length)
            {
                read = write - this.items.Length;
                Volatile.Write(ref this.readIndex, read);
                continue;
            }

            var slot = (int)(read & this.mask);
            var before = Volatile.Read(ref this.sequences[slot]);

            // The slot must hold exactly the entry being asked for. A different marker means
            // it is mid-write or has already been recycled; either way there is nothing here
            // to return.
            if (before != read + 1)
            {
                Volatile.Write(ref this.readIndex, read + 1);
                continue;
            }

            var candidate = this.items[slot];

            // If the marker moved while the copy was in flight, the copy may have caught the
            // slot half-written. Discard rather than return a value that never existed.
            if (Volatile.Read(ref this.sequences[slot]) != before)
            {
                Volatile.Write(ref this.readIndex, read + 1);
                continue;
            }

            item = candidate;
            Volatile.Write(ref this.readIndex, read + 1);
            return true;
        }
    }

    /// <summary>
    /// Drains everything currently buffered into <paramref name="handler"/>, returning how
    /// many were handled.
    ///
    /// Bounded by what was present when the drain began rather than by "until empty": a
    /// producer faster than the consumer would otherwise keep this running forever and the
    /// fold would never return.
    /// </summary>
    public int Drain(RingHandler<T>? handler)
    {
        // Annotated nullable because the guard exists for a reason: this is reachable from
        // callers that are not nullable-aware, so null genuinely can arrive and is refused
        // rather than dereferenced.
        if (handler is null)
            throw new ArgumentNullException(nameof(handler));

        var available = this.Count;
        var handled = 0;

        while (handled < available && this.TryRead(out var item))
        {
            handler(in item);
            handled++;
        }

        return handled;
    }

    /// <summary>
    /// Discards everything buffered without handling it, and forgets the drop count. Called
    /// from the consumer side, at a session boundary.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref this.readIndex, Volatile.Read(ref this.writeIndex));
        this.dropped = 0;
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        var result = 1;

        while (result < value)
        {
            result <<= 1;

            if (result <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value), value, "Capacity is too large to round to a power of two.");
            }
        }

        return result;
    }
}

/// <summary>
/// Handles one drained entry. Takes it by reference so draining a large struct does not copy
/// it a second time.
/// </summary>
public delegate void RingHandler<T>(in T item)
    where T : struct;
