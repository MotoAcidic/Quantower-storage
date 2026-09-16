namespace AuctionResponse.Core;

/// <summary>
/// Ordered ingress (Section 5). A callback takes the lock only long enough to stamp a
/// monotonic sequence and enqueue an already-immutable record; it never performs I/O
/// while holding it.
///
/// Overflow deliberately sets a flag that lives OUTSIDE the queue, so the evidence of
/// loss cannot be lost along with the events it describes (Section 13).
/// </summary>
public sealed class EventIngress
{
    private readonly object _gate = new();
    private readonly Queue<MarketEvent> _queue;
    private readonly int _capacity;

    private long _sequence;
    private int _overflowCount;
    private bool _overflowed;
    private long _firstOverflowSequence;

    public EventIngress(int capacityEvents)
    {
        if (capacityEvents <= 0) throw new ArgumentOutOfRangeException(nameof(capacityEvents));
        _capacity = capacityEvents;
        _queue = new Queue<MarketEvent>(Math.Min(capacityEvents, 4096));
    }

    public int Capacity => _capacity;

    /// <summary>Set once the queue has ever overflowed. Never cleared by draining.</summary>
    public bool HasOverflowed { get { lock (_gate) return _overflowed; } }
    public int OverflowCount { get { lock (_gate) return _overflowCount; } }
    public long FirstOverflowSequence { get { lock (_gate) return _firstOverflowSequence; } }
    public int Backlog { get { lock (_gate) return _queue.Count; } }
    public long LastAssignedSequence { get { lock (_gate) return _sequence; } }

    /// <summary>
    /// Stamps the next sequence onto <paramref name="factory"/>'s record and enqueues it
    /// atomically. Returns false when the queue is full: the event is dropped, the fault
    /// flag is raised, and the caller must stop emitting alerts and resynchronise.
    /// </summary>
    public bool TryEnqueue(Func<long, MarketEvent> factory, out MarketEvent? enqueued)
    {
        lock (_gate)
        {
            var seq = ++_sequence;
            if (_queue.Count >= _capacity)
            {
                if (!_overflowed) { _overflowed = true; _firstOverflowSequence = seq; }
                _overflowCount++;
                enqueued = null;
                return false;
            }
            var ev = factory(seq);
            _queue.Enqueue(ev);
            enqueued = ev;
            return true;
        }
    }

    /// <summary>
    /// Allocates the next sequence without enqueuing anything. Used for DecisionTicks, which
    /// must travel in the SAME ingress sequence as market events so that "which events
    /// preceded this decision" is a property of the log rather than of a wall clock.
    /// </summary>
    public bool TryAllocateSequence(out long sequence)
    {
        lock (_gate) { sequence = ++_sequence; }
        return true;
    }

    /// <summary>Drains up to <paramref name="max"/> events in sequence order.</summary>
    public int Drain(List<MarketEvent> into, int max = int.MaxValue)
    {
        var taken = 0;
        lock (_gate)
        {
            while (taken < max && _queue.Count > 0) { into.Add(_queue.Dequeue()); taken++; }
        }
        return taken;
    }

    /// <summary>Clears the queue after an overflow fault. The overflow flag is retained.</summary>
    public void Resynchronise()
    {
        lock (_gate) _queue.Clear();
    }
}

/// <summary>
/// Monotonic feature clock for one connection epoch (Section 5). A regressing or unknown
/// elapsed reading invalidates the epoch rather than being sorted back into order.
/// </summary>
public sealed class EpochClock
{
    private readonly Func<long> _readNs;
    private long _last;

    public EpochClock(Func<long> readNanoseconds) { _readNs = readNanoseconds; }

    public int Epoch { get; private set; } = 1;
    public bool Invalidated { get; private set; }
    public string? InvalidReason { get; private set; }

    public long Read()
    {
        var now = _readNs();
        if (now < _last)
        {
            Invalidated = true;
            InvalidReason = "Elapsed clock regressed from " + _last + "ns to " + now + "ns.";
            return _last;
        }
        _last = now;
        return now;
    }

    public long LastRead => _last;

    /// <summary>Starts a new epoch. Callers must terminate candidates as DataInterrupted first.</summary>
    public void StartNewEpoch()
    {
        Epoch++;
        _last = 0;
        Invalidated = false;
        InvalidReason = null;
    }
}
