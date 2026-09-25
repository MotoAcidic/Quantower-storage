using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace FinchLite;

/// <summary>
/// EXTRACTED 2026-09-25 out of <c>FinchLiteIndicator.ReconcileRestingLevels</c> — Finch-Lite's
/// own most mature, most-iterated feature (large resting orders, tiered absorption strength, and
/// unfinished-auction detection, all unified in one reconciliation pass), pulled into a
/// standalone, platform-light class so `finchDomScalpStrategy` can trade off the EXACT SAME
/// detection logic the indicator draws — one engine, not two independently-maintained copies
/// that could quietly drift apart. This is a pure extraction: the body below is unchanged from
/// the indicator's own method, just with `midPrice`/`distanceThresholdPrice`/`dayStart` taken as
/// PARAMETERS instead of read off `this.symbol`/`this.UnfinishedDistanceTicks`/a private
/// `TradingDayStart` helper — the caller (indicator or strategy) computes those from its own
/// `Symbol`, keeping this class free of any platform `Symbol`/`Indicator`/`Strategy` dependency.
///
/// See the individual field doc comments for the FULL history of each design decision (poll-miss
/// grace period, sub-threshold removal, price-distance unfinished-auction trigger, etc.) — none
/// of that reasoning changed in this extraction, only where the code lives.
/// </summary>
internal sealed class RestingOrderEngine
{
    /// <summary>One tracked resting level, ready for either a paint drawable or a trading
    /// decision.</summary>
    /// <param name="Price">The book price this level rests at.</param>
    /// <param name="IsBid">True for a resting bid, false for a resting ask.</param>
    /// <param name="Current">The LIVE current resting size — never a remembered peak.</param>
    /// <param name="Peak">The largest size ever seen at this level since it was first tracked —
    /// bookkeeping only (raising the bar, absorption-delta comparisons); a consumer wanting "how
    /// big is this level right now" should use <see cref="Current"/>, not this.</param>
    /// <param name="Absorbed">Running total of contracts traded through this level while it kept
    /// standing — drives absorption colour/strength tiers.</param>
    /// <param name="IsUnfinished">True once current market price has moved past this level by the
    /// caller's own configured distance while it is still resting.</param>
    /// <param name="FirstSeenUtc">When this level was first flagged.</param>
    internal readonly record struct RestingLevel(
        double Price, bool IsBid, double Current, double Peak, double Absorbed, bool IsUnfinished,
        DateTime FirstSeenUtc);

    /// <summary>Consecutive polls a level may go missing from the returned book before it is
    /// treated as genuinely gone — the platform's own pull is not perfectly stable poll to poll
    /// for deeper levels, independent of anything actually trading.</summary>
    private const int MissingPollGrace = 2;

    private readonly Dictionary<(double Price, bool IsBid), double> peaks = new();
    private readonly Dictionary<(double Price, bool IsBid), double> lastSize = new();
    private readonly Dictionary<(double Price, bool IsBid), double> absorbed = new();
    private readonly Dictionary<(double Price, bool IsBid), DateTime> firstSeenUtc = new();
    private readonly Dictionary<(double Price, bool IsBid), int> missingPolls = new();
    private DateTime dayStartUtc;

    public void Reset()
    {
        this.peaks.Clear();
        this.lastSize.Clear();
        this.absorbed.Clear();
        this.firstSeenUtc.Clear();
        this.missingPolls.Clear();
        this.dayStartUtc = default;
    }

    /// <summary>
    /// Reconciles tracked levels against the CURRENT book on both sides and returns the resolved
    /// list — see the class doc comment and each field's own doc comment for the full design.
    /// Collect-then-apply throughout rather than mutating a dictionary mid-enumeration.
    /// </summary>
    /// <param name="nowUtc">Current poll time.</param>
    /// <param name="dayStart">The caller's own trading-day boundary for `nowUtc` (e.g. 18:00
    /// America/New_York) — a NEW value versus the last call clears all tracked state, same
    /// "remembers nothing across a session rollover" design as before extraction.</param>
    /// <param name="bids">Current bid levels.</param>
    /// <param name="asks">Current ask levels.</param>
    /// <param name="threshold">Minimum size for a level to newly qualify as "large."</param>
    /// <param name="midPrice">Current book mid price (NaN if unknown) — used only for the
    /// unfinished-auction distance check.</param>
    /// <param name="distanceThresholdPrice">How far (in price units) market price must have moved
    /// past a level before it counts as "left behind" (NaN disables the check).</param>
    public IReadOnlyList<RestingLevel> Reconcile(
        DateTime nowUtc, DateTime dayStart, Level2Item[]? bids, Level2Item[]? asks, int threshold,
        double midPrice, double distanceThresholdPrice)
    {
        if (dayStart != this.dayStartUtc)
        {
            this.Reset();
            this.dayStartUtc = dayStart;
        }

        var currentBid = ToLookup(bids);
        var currentAsk = ToLookup(asks);

        var toRemove = new List<(double Price, bool IsBid)>();
        var toRaise = new List<((double Price, bool IsBid) Key, double NewPeak)>();
        var toAbsorb = new List<((double Price, bool IsBid) Key, double Delta)>();
        var toMiss = new List<((double Price, bool IsBid) Key, int Missed)>();
        var toSeenAgain = new List<(double Price, bool IsBid)>();

        foreach (var kvp in this.peaks)
        {
            var (price, isBid) = kvp.Key;
            var lookup = isBid ? currentBid : currentAsk;

            if (!lookup.TryGetValue(price, out var currentSize) || currentSize <= 0)
            {
                // A price missing from ONE poll's snapshot is not necessarily filled or
                // cancelled. Only past MissingPollGrace CONSECUTIVE misses is it treated as
                // genuinely gone; a shorter blip keeps the level (and its origin time) intact.
                var missed = this.missingPolls.TryGetValue(kvp.Key, out var m) ? m + 1 : 1;

                if (missed > MissingPollGrace)
                    toRemove.Add(kvp.Key);
                else
                    toMiss.Add((kvp.Key, missed));

                continue;
            }

            if (this.missingPolls.ContainsKey(kvp.Key))
                toSeenAgain.Add(kvp.Key);

            // A level that qualified at its PEAK, then shrank well below the qualifying
            // threshold, would otherwise keep being tracked (and shown at its live current size)
            // indefinitely as long as price never moved past it. UNLESS price has already left it
            // behind (the same distance check the unfinished-auction flag uses) — that is a
            // deliberate exception: a level price ran through IS still worth marking as
            // unfinished even far below the general threshold. Only a level BELOW threshold and
            // NOT left behind is dropped here.
            var isLeftBehindNow = !double.IsNaN(midPrice) && !double.IsNaN(distanceThresholdPrice)
                && (isBid ? midPrice < price - distanceThresholdPrice : midPrice > price + distanceThresholdPrice);

            if (currentSize < threshold && !isLeftBehindNow)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            // Dropped since last poll but still standing = something traded through it while it
            // held its ground. A refill afterward does not erase this; the level still had to
            // absorb that flow to still be here.
            if (this.lastSize.TryGetValue(kvp.Key, out var last) && last > currentSize)
                toAbsorb.Add((kvp.Key, last - currentSize));

            if (currentSize > kvp.Value)
                toRaise.Add((kvp.Key, currentSize));
        }

        foreach (var key in toRemove)
        {
            this.peaks.Remove(key);
            this.lastSize.Remove(key);
            this.absorbed.Remove(key);
            this.firstSeenUtc.Remove(key);
            this.missingPolls.Remove(key);
        }

        foreach (var (key, missed) in toMiss)
            this.missingPolls[key] = missed;

        foreach (var key in toSeenAgain)
            this.missingPolls.Remove(key);

        foreach (var (key, peak) in toRaise)
            this.peaks[key] = peak;

        foreach (var (key, delta) in toAbsorb)
        {
            this.absorbed[key] = this.absorbed.TryGetValue(key, out var existing) ? existing + delta : delta;
        }

        AddNewLevels(currentBid, isBid: true);
        AddNewLevels(currentAsk, isBid: false);

        var result = new List<RestingLevel>(this.peaks.Count);

        foreach (var kvp in this.peaks)
        {
            var (price, isBid) = kvp.Key;
            var peak = kvp.Value;
            var lookup = isBid ? currentBid : currentAsk;
            var current = lookup.TryGetValue(price, out var size) ? size : 0d;

            // Recorded here, not up above — this loop already touches every currently-tracked
            // level once, and next poll's absorption comparison needs THIS poll's observed size,
            // not the peak.
            this.lastSize[kvp.Key] = current;

            // Unfinished = current market price has moved past this level's own price by the
            // configured distance, in the direction that would have consumed it — a bid left
            // behind once price fell below it, an ask left behind once price rose above it. Still
            // resting (current > 0) is the only size requirement.
            var isUnfinished = current > 0 && !double.IsNaN(midPrice) && !double.IsNaN(distanceThresholdPrice)
                && (isBid ? midPrice < price - distanceThresholdPrice : midPrice > price + distanceThresholdPrice);

            var absorbedAmount = this.absorbed.TryGetValue(kvp.Key, out var abs) ? abs : 0d;
            var seen = this.firstSeenUtc.TryGetValue(kvp.Key, out var s) ? s : nowUtc;

            // ALWAYS the live current size, never the peak — peak is bookkeeping only.
            result.Add(new RestingLevel(price, isBid, current, peak, absorbedAmount, isUnfinished, seen));
        }

        return result;

        void AddNewLevels(Dictionary<double, double> lookup, bool isBid)
        {
            foreach (var (price, size) in lookup)
            {
                if (size < threshold)
                    continue;

                var key = (price, isBid);
                if (!this.peaks.ContainsKey(key))
                {
                    this.peaks[key] = size;
                    this.firstSeenUtc[key] = nowUtc;
                }
            }
        }

        static Dictionary<double, double> ToLookup(Level2Item[]? items)
        {
            var map = new Dictionary<double, double>();

            if (items is null)
                return map;

            foreach (var item in items)
                map[item.Price] = item.Size;

            return map;
        }
    }
}
