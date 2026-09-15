using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Flow;

/// <summary>One Level 2 update as the feed delivers it, already stripped of platform types.</summary>
/// <param name="Utc">The feed's timestamp.</param>
/// <param name="Side">Bid or ask.</param>
/// <param name="Price">The level. NaN is refused by <see cref="DepthLadder.Apply"/>.</param>
/// <param name="Size">Resting size — the level total on an aggregated feed, one order's size on a per-order feed.</param>
/// <param name="Closed">The platform's removal flag.</param>
/// <param name="OrderId">The exchange order id when the feed is per-order; empty otherwise.</param>
/// <param name="OrderCount">Orders at the level, when the aggregated feed says.</param>
/// <param name="Priority">Queue position on a per-order feed; zero otherwise.</param>
public readonly record struct DepthUpdate(
    DateTime Utc, BookSide Side, double Price, double Size, bool Closed,
    string OrderId, int OrderCount, long Priority);

/// <summary>Resting size at one price on one side.</summary>
public readonly record struct DepthLevel(BookSide Side, double Price, double Size, int Orders);

/// <summary>One resting order, known only on a per-order feed.</summary>
public readonly record struct RestingOrder(string Id, BookSide Side, double Price, double Size, long Priority);

/// <summary>One resting order inside a snapshot level, as the platform reports it.</summary>
/// <param name="Id">The order's identity. Empty when the feed numbers orders but does not name them.</param>
/// <param name="Size">The order's size.</param>
/// <param name="Priority">Queue position.</param>
public readonly record struct DepthSnapshotOrder(string Id, double Size, long Priority);

/// <summary>One price in a snapshot: the level total, and the orders behind it when known.</summary>
public readonly record struct DepthSnapshotLevel(
    double Price, double Size, int Orders, DepthSnapshotOrder[] Detail);

/// <summary>
/// The whole book at an instant, as the platform's depth-of-market call returns it.
///
/// A SNAPSHOT, NOT A DELTA, AND THAT IS THE POINT. The event stream this ladder was built for
/// is FABRICATED on some connections — every update stamped
/// <see cref="DepthLadder.SyntheticLevelOneId"/>, one price per side, which is the touch and not
/// depth. Measured on the operator's chart 2026-09-14: 19,762 such updates on one instance.
/// The same platform's pull API returned 50 prices a side over a 25-point span on that very
/// chart, and 679 per-order levels with the MBO flag set. The depth was there; the stream was
/// not carrying it.
/// </summary>
/// <param name="Utc">The instant the book was read.</param>
/// <param name="Bids">Bid prices, any order.</param>
/// <param name="Asks">Ask prices, any order.</param>
public sealed record DepthSnapshot(
    DateTime Utc, DepthSnapshotLevel[] Bids, DepthSnapshotLevel[] Asks)
{
    public static readonly DepthSnapshot Empty = new(
        DateTime.MinValue, Array.Empty<DepthSnapshotLevel>(), Array.Empty<DepthSnapshotLevel>());

    public int Prices => this.Bids.Length + this.Asks.Length;
}

/// <summary>
/// The full resting book, deep on both sides, from Level 2 updates.
///
/// THIS SITS BESIDE <see cref="OrbIx.Core.Features.BookLadder"/> RATHER THAN REPLACING IT, and the plan that
/// absorbed these tools said the opposite until the two surfaces were read:
///
///     BookLadder    CurrentSize(price, side) · SizeAt(price, side, atUtc) · BestBid/BestAsk
///     DepthLadder   Levels(side, max) · Largest(side, count, within) · LargestOrder · PerOrderMode
///
/// BookLadder answers "how much was resting at THIS price at THIS time" — a per-price time
/// history, which is the question absorption asks. This answers "what does the ladder look
/// like": where, ANYWHERE in it, the largest resting size sits ("the largest buy order and the
/// largest sell order", 4:17–4:26; "the highest order in the order book for shorts", 22:14).
/// Neither can answer the other's question, so both exist and neither is derived from the
/// other's state.
///
/// ONE FEED, TWO VIEWS. Both are fed from the same Level 2 event; the book is read once. That
/// is the rule that keeps two ladders honest, and it is the same distinction Phase 2 got wrong
/// in the other direction when FootprintStore was called a duplicate of FootprintEngine.
///
/// TWO FEED SHAPES, DETECTED FROM THE DATA. On the one data vendor connector with MBO enabled the
/// platform delivers one <c>Level2Quote</c> PER ORDER carrying an exchange order id
/// (memory: quantower-one data vendor-connector-mbo, measured 2026-08-06); every other feed
/// delivers one quote per PRICE whose size is the level total. An update carrying an id
/// switches the ladder to per-order mode for good; the status line reports which mode is
/// live so "largest order" is never read off a feed that only knows level totals.
/// </summary>
public sealed class DepthLadder
{
    private readonly SortedDictionary<long, LevelState> bids = new();
    private readonly SortedDictionary<long, LevelState> asks = new();
    private readonly Dictionary<string, RestingOrder> orders = new(StringComparer.Ordinal);
    private readonly double tickSize;

    public DepthLadder(double tickSize)
    {
        if (!(tickSize > 0) || double.IsInfinity(tickSize))
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "Levels are keyed by tick, so the price grid is required.");

        this.tickSize = tickSize;
    }

    /// <summary>
    /// The id every Quantower connector stamps on a book it FABRICATED from level 1 — one
    /// price per side, priority 0 — when it has no real depth (memory:
    /// quantower-one data vendor-connector-mbo; the literal was found in the Quantower, CQG, dxFeed
    /// and one data vendor vendor assemblies). Measured on the first live attach 2026-09-11: the
    /// another connection feed delivered exactly two such ids and the ladder read them as MBO.
    /// </summary>
    public const string SyntheticLevelOneId = "generated_from_level1";

    /// <summary>True once any update carried a real order id.</summary>
    public bool PerOrderMode { get; private set; }

    /// <summary>True once any update carried <see cref="SyntheticLevelOneId"/>: the book is level 1 only.</summary>
    public bool SyntheticLevelOne { get; private set; }

    public int OrderCount => this.orders.Count;

    public int BidLevels => this.bids.Count;

    public int AskLevels => this.asks.Count;

    public DateTime LastUpdateUtc { get; private set; } = DateTime.MinValue;

    public long UpdatesApplied { get; private set; }

    /// <summary>Highest bid price with size, or NaN when the bid side is empty.</summary>
    public double BestBid => this.bids.Count > 0 ? this.PriceOf(LastKey(this.bids)) : double.NaN;

    /// <summary>Lowest ask price with size, or NaN when the ask side is empty.</summary>
    public double BestAsk => this.asks.Count > 0 ? this.PriceOf(FirstKey(this.asks)) : double.NaN;

    /// <summary>True once a snapshot has been applied: the ladder is driven by the pull API.</summary>
    public bool SnapshotDriven { get; private set; }

    /// <summary>
    /// Event-stream updates refused because a snapshot owns this ladder. Counted rather than
    /// dropped in silence, so a feed still pushing a fabricated book is visible rather than
    /// merely absent from the result.
    /// </summary>
    public long UpdatesIgnored { get; private set; }

    /// <summary>
    /// Replaces the book with what the platform's depth-of-market call returned.
    ///
    /// A SNAPSHOT TAKES OWNERSHIP, AND MIXING THE TWO SOURCES IS REFUSED RATHER THAN MERGED.
    /// On the connection this was written for the event stream is fabricated from level 1 while
    /// the pull API returns real depth; folding both into one ladder would seat invented prices
    /// beside measured ones with nothing to tell them apart, and would leave
    /// <see cref="SyntheticLevelOne"/> latched true over a book that is not synthetic at all.
    /// Refusals are counted so the fabricated stream stays visible.
    ///
    /// THE WHOLE BOOK, EVERY TIME. A snapshot is the state, not a change to it, so prices absent
    /// from it are gone rather than stale — which is precisely what a delta ladder cannot know
    /// and why a level pulled from the book used to linger.
    /// </summary>
    public void ApplySnapshot(DepthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        this.SnapshotDriven = true;
        this.bids.Clear();
        this.asks.Clear();
        this.orders.Clear();

        // A real book replaces a fabricated one outright: the flag describes where the CURRENT
        // contents came from, and these did not come from the connector's placeholder.
        this.SyntheticLevelOne = false;
        this.PerOrderMode = false;

        this.Install(snapshot.Bids, BookSide.Bid, this.bids);
        this.Install(snapshot.Asks, BookSide.Ask, this.asks);

        this.UpdatesApplied++;
        this.LastUpdateUtc = snapshot.Utc;
    }

    private void Install(
        DepthSnapshotLevel[] levels, BookSide side, SortedDictionary<long, LevelState> into)
    {
        foreach (var level in levels)
        {
            // Same refusal as the event path: a NaN price cannot identify a level and a NaN size
            // cannot describe one.
            if (!double.IsFinite(level.Price) || !double.IsFinite(level.Size) || level.Size <= 0)
                continue;

            var index = this.IndexOf(level.Price);
            var price = this.PriceOf(index);

            // Two platform prices can round onto one tick; their sizes add rather than one
            // winning, exactly as the footprint merges cells.
            into.TryGetValue(index, out var existing);

            var detail = level.Detail ?? Array.Empty<DepthSnapshotOrder>();
            var named = 0;

            foreach (var order in detail)
            {
                if (string.IsNullOrEmpty(order.Id) || !double.IsFinite(order.Size) || order.Size <= 0)
                    continue;

                this.PerOrderMode = true;
                this.orders[order.Id] = new RestingOrder(order.Id, side, price, order.Size, order.Priority);
                named++;
            }

            // Orders counted from the detail when it named any, and from the platform's own
            // count otherwise -- a feed that says "12 orders here" without listing them is still
            // telling the truth about the count.
            var orders = named > 0 ? named : Math.Max(level.Orders, 0);

            into[index] = new LevelState(existing.Size + level.Size, existing.Orders + orders);
        }
    }

    /// <summary>Applies one update. Returns false when the update carried no usable price or size.</summary>
    public bool Apply(in DepthUpdate update)
    {
        // A snapshot owns this ladder; see ApplySnapshot.
        if (this.SnapshotDriven)
        {
            this.UpdatesIgnored++;
            return false;
        }

        // A NaN price cannot identify a level and a NaN size cannot describe one. Every
        // Level 2 subscription opens with two such sentinels (ORB-IX measured 2026-08-22);
        // they are refused here rather than folded as a level at "NaN".
        if (!double.IsFinite(update.Price) || double.IsNaN(update.Size))
            return false;

        this.UpdatesApplied++;
        this.LastUpdateUtc = update.Utc;

        var index = this.IndexOf(update.Price);
        var side = update.Side == BookSide.Bid ? this.bids : this.asks;

        if (string.Equals(update.OrderId, SyntheticLevelOneId, StringComparison.Ordinal))
        {
            // Not an order: the connector's placeholder for the touch. Kept as an aggregated
            // level so best bid/ask still read, and flagged so nothing downstream calls it depth.
            this.SyntheticLevelOne = true;
        }
        else if (!string.IsNullOrEmpty(update.OrderId))
        {
            this.PerOrderMode = true;
            this.ApplyOrder(update, index, side);
            return true;
        }

        if (update.Closed || update.Size <= 0)
        {
            side.Remove(index);
            return true;
        }

        side[index] = new LevelState(update.Size, Math.Max(update.OrderCount, 0));
        return true;
    }

    /// <summary>Levels on one side, nearest the touch first, up to a count (0 = all).</summary>
    public IReadOnlyList<DepthLevel> Levels(BookSide side, int maxLevels = 0)
    {
        var result = new List<DepthLevel>();
        var map = side == BookSide.Bid ? this.bids : this.asks;

        foreach (var (index, state) in Walk(map, side))
        {
            if (maxLevels > 0 && result.Count >= maxLevels)
                break;

            result.Add(new DepthLevel(side, this.PriceOf(index), state.Size, state.Orders));
        }

        return result;
    }

    /// <summary>The level with the most resting size on one side within N levels of the touch (0 = whole side).</summary>
    public DepthLevel? LargestResting(BookSide side, int withinLevels = 0)
    {
        DepthLevel? best = null;

        foreach (var level in this.Levels(side, withinLevels))
        {
            if (best is null || level.Size > best.Value.Size)
                best = level;
        }

        return best;
    }

    /// <summary>The K largest levels on one side within N of the touch, largest first.</summary>
    public IReadOnlyList<DepthLevel> Largest(BookSide side, int count, int withinLevels = 0)
    {
        var levels = new List<DepthLevel>(this.Levels(side, withinLevels));
        levels.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        if (count > 0 && levels.Count > count)
            levels.RemoveRange(count, levels.Count - count);

        return levels;
    }

    /// <summary>The single largest resting ORDER on one side. Null off a per-order feed, because level totals say nothing about it.</summary>
    public RestingOrder? LargestOrder(BookSide side)
    {
        if (!this.PerOrderMode)
            return null;

        RestingOrder? best = null;

        foreach (var order in this.orders.Values)
        {
            if (order.Side != side)
                continue;

            if (best is null || order.Size > best.Value.Size)
                best = order;
        }

        return best;
    }

    /// <summary>
    /// Forgets the book entirely, counters included.
    ///
    /// THE COUNTERS GO TOO, and leaving them was a defect a test caught: UpdatesApplied and
    /// LastUpdateUtc describe the book that was just discarded, so a cleared ladder went on
    /// reporting that depth had been received and had moved recently. A display reading those to
    /// decide whether it has a book would have believed it, which is the whole reason they exist.
    /// </summary>
    public void Clear()
    {
        this.bids.Clear();
        this.asks.Clear();
        this.orders.Clear();
        this.PerOrderMode = false;
        this.SyntheticLevelOne = false;
        this.SnapshotDriven = false;
        this.UpdatesApplied = 0;
        this.UpdatesIgnored = 0;
        this.LastUpdateUtc = DateTime.MinValue;
    }

    private void ApplyOrder(in DepthUpdate update, long index, SortedDictionary<long, LevelState> side)
    {
        if (this.orders.TryGetValue(update.OrderId, out var previous))
        {
            var previousIndex = this.IndexOf(previous.Price);
            var previousSide = previous.Side == BookSide.Bid ? this.bids : this.asks;
            Subtract(previousSide, previousIndex, previous.Size);
            this.orders.Remove(update.OrderId);
        }

        if (update.Closed || update.Size <= 0)
            return;

        this.orders[update.OrderId] = new RestingOrder(
            update.OrderId, update.Side, this.PriceOf(index), update.Size, update.Priority);

        side.TryGetValue(index, out var state);
        side[index] = new LevelState(state.Size + update.Size, state.Orders + 1);
    }

    private static void Subtract(SortedDictionary<long, LevelState> side, long index, double size)
    {
        if (!side.TryGetValue(index, out var state))
            return;

        var remaining = new LevelState(state.Size - size, state.Orders - 1);

        if (remaining.Size <= 0 || remaining.Orders <= 0)
            side.Remove(index);
        else
            side[index] = remaining;
    }

    private static IEnumerable<KeyValuePair<long, LevelState>> Walk(
        SortedDictionary<long, LevelState> map, BookSide side)
    {
        if (side == BookSide.Ask)
        {
            foreach (var pair in map)
                yield return pair;

            yield break;
        }

        // Bids walk from the highest price down — nearest the touch first.
        var keys = new List<long>(map.Keys);

        for (var i = keys.Count - 1; i >= 0; i--)
            yield return new KeyValuePair<long, LevelState>(keys[i], map[keys[i]]);
    }

    private static long FirstKey(SortedDictionary<long, LevelState> map)
    {
        foreach (var key in map.Keys)
            return key;

        throw new InvalidOperationException("The map is empty.");
    }

    private static long LastKey(SortedDictionary<long, LevelState> map)
    {
        var last = long.MinValue;

        foreach (var key in map.Keys)
            last = key;

        return last;
    }

    private long IndexOf(double price) => (long)Math.Round(price / this.tickSize, MidpointRounding.AwayFromZero);

    private double PriceOf(long index) => index * this.tickSize;

    private readonly record struct LevelState(double Size, int Orders);
}
