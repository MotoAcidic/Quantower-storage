using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbIx.Core.Sessions;

/// <summary>
/// The completed opening ranges currently held for drawing, newest last, bounded.
///
/// A chart showing three days of one-minute bars spans two dozen session windows; one showing
/// three weeks of fifteen-minute bars spans far more. Holding every range a long-lived chart
/// ever produced is an unbounded collection on the paint path, so the store has a cap and the
/// oldest range leaves first — the newest sessions are the ones on screen.
///
/// Keyed by session name and open instant together. Name alone would let today's regular
/// session evict yesterday's, which is exactly the history the overlay exists to show.
/// </summary>
public sealed class SessionRangeStore
{
    private readonly Dictionary<(string Session, DateTime OpenUtc), OrSnapshot> ranges = new();
    private readonly int capacity;

    /// <param name="capacity">
    /// How many ranges to retain. From <c>levels.maxRetainedSessions</c>; must be positive,
    /// because a store that retains nothing draws nothing.
    /// </param>
    public SessionRangeStore(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "A store that retains no ranges would draw nothing.");
        }

        this.capacity = capacity;
    }

    public int Count => this.ranges.Count;

    public int Capacity => this.capacity;

    /// <summary>
    /// Adds or replaces a range, evicting the oldest if that takes the store over capacity.
    ///
    /// Replacement matters: a range seeded from bars is superseded by the same session's
    /// live-measured range once it forms, and the live one must win. Since the key is the
    /// session and its open instant, that replacement is exact rather than approximate.
    /// </summary>
    /// <returns>True when a range was evicted to make room.</returns>
    public bool Add(OrSnapshot range)
    {
        if (range is null)
            throw new ArgumentNullException(nameof(range));

        this.ranges[(range.SessionName, range.OpenUtc)] = range;

        if (this.ranges.Count <= this.capacity)
            return false;

        // Oldest by open instant, with the name as a deterministic tie-break so two sessions
        // opening together evict in an order a replay reproduces.
        var oldest = this.ranges.Keys
            .OrderBy(k => k.OpenUtc)
            .ThenBy(k => k.Session, StringComparer.Ordinal)
            .First();

        this.ranges.Remove(oldest);
        return true;
    }

    /// <summary>Whether a range is already held for this session occurrence.</summary>
    public bool Contains(string sessionName, DateTime openUtc)
        => this.ranges.ContainsKey((sessionName, openUtc));

    /// <summary>The range for one session occurrence, or null.</summary>
    public OrSnapshot? Get(string sessionName, DateTime openUtc)
        => this.ranges.TryGetValue((sessionName, openUtc), out var range) ? range : null;

    /// <summary>
    /// Every retained range, oldest first.
    ///
    /// Materialised into an array rather than returned lazily because the caller is the paint
    /// path: enumerating the live dictionary while the data thread adds to it would throw
    /// mid-frame.
    /// </summary>
    public OrSnapshot[] Snapshot()
        => this.ranges.Values
            .OrderBy(r => r.OpenUtc)
            .ThenBy(r => r.SessionName, StringComparer.Ordinal)
            .ToArray();

    public void Clear() => this.ranges.Clear();
}
