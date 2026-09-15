using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>
/// One minute of trading, with its volume already split by price and side.
/// </summary>
/// <param name="MinuteUtc">The minute this covers, truncated to the minute, UTC.</param>
/// <param name="Prints">Trade prints that contributed to it.</param>
/// <param name="Levels">Per-price volume for that minute, price-ascending.</param>
public readonly record struct ProfileMinuteLevels(
    DateTime MinuteUtc, int Prints, IReadOnlyList<ProfileLevel> Levels);

/// <summary>
/// Per-price volume for a span of history, held per minute so any sub-range can be summed.
///
/// WHY PER MINUTE, AND NOT ONE AGGREGATE. A profile is a histogram, and a histogram cannot be
/// filtered by time after the fact — once prints are summed into prices, the clock is gone.
/// But the ranges asked of it MOVE: the anchored profile re-anchors, the fixed-range profile
/// takes clicked bounds, and an open-ended range grows every five seconds. Aggregating once
/// for one range would serve exactly that range and nothing else.
///
/// WHY IT IS A CACHE AT ALL. The load that fills it is a blocking platform call, allowed only
/// during initialisation — never the fold, never the paint path. The profile rebuild runs on
/// the fold. Those two facts together mean the data has to be fetched once, up front, and
/// sliced cheaply thereafter, which is exactly what this is.
///
/// A MINUTE IS NOT AN ARBITRARY CHOICE. It is the granularity the connector itself uses for
/// bar volume-analysis, so this holds precisely the shape the bars should have carried and
/// did not. A range boundary falling mid-minute therefore has the same resolution here as it
/// would have had from the bars — no better, and no worse.
/// </summary>
public sealed class ProfileTickCache
{
    private readonly IReadOnlyList<ProfileMinuteLevels> minutes;

    /// <summary>
    /// Builds a cache over minutes that are already aggregated and ordered.
    /// </summary>
    /// <param name="minutes">Per-minute levels, ascending by minute.</param>
    /// <param name="fromUtc">Start of the span the load actually asked for.</param>
    /// <param name="toUtc">End of the span the load actually asked for.</param>
    /// <param name="served">Whether the connector serves tick history for this symbol at all.</param>
    /// <param name="status">The loader's sentence, carried for the log.</param>
    /// <param name="sourceLabel">
    /// Provenance for the chart. Empty when the chart's own connection served this; otherwise
    /// names the connection borrowed from, so a profile built on another broker's feed is never
    /// presented as the chart's own.
    /// </param>
    /// <exception cref="ArgumentNullException">The minutes list is null.</exception>
    public ProfileTickCache(
        IReadOnlyList<ProfileMinuteLevels> minutes,
        DateTime fromUtc,
        DateTime toUtc,
        bool served,
        string status,
        string sourceLabel)
    {
        this.minutes = minutes ?? throw new ArgumentNullException(nameof(minutes));
        this.FromUtc = fromUtc;
        this.ToUtc = toUtc;
        this.Served = served;
        this.Status = status ?? throw new ArgumentNullException(nameof(status));
        this.SourceLabel = sourceLabel ?? throw new ArgumentNullException(nameof(sourceLabel));
    }

    /// <summary>Start of the span this holds.</summary>
    public DateTime FromUtc { get; }

    /// <summary>End of the span this holds.</summary>
    public DateTime ToUtc { get; }

    /// <summary>
    /// Whether the connector serves tick history for this symbol.
    ///
    /// False is a statement about the CONNECTOR and survives into the profile's verdict, where
    /// it is the difference between "nothing traded" and "this can never be drawn here".
    /// </summary>
    public bool Served { get; }

    /// <summary>The loader's own sentence. The platform history API has no error channel, so
    /// this is the only place its reason exists.</summary>
    public string Status { get; }

    /// <summary>
    /// Provenance for the chart: empty when the chart's own connection served this, otherwise
    /// naming the connection it was borrowed from.
    /// </summary>
    public string SourceLabel { get; }

    /// <summary>Minutes held, however many of them carried trades.</summary>
    public int MinuteCount => this.minutes.Count;

    /// <summary>
    /// Sums the range into per-price levels, and reports what contributed.
    ///
    /// A minute is included when its own start falls inside the range — the same bar-open
    /// convention the chart-bar scan uses, so the two sources agree about a boundary rather
    /// than disagreeing by one minute at each end.
    ///
    /// Returns levels price-ascending so two runs over the same data sum in the same order and
    /// produce bit-identical totals; the golden fixture asserts value equality, not tolerance.
    /// </summary>
    /// <param name="startUtc">Range start, inclusive.</param>
    /// <param name="endUtc">Range end, exclusive.</param>
    public ProfileTickSlice Slice(DateTime startUtc, DateTime endUtc)
    {
        var byPrice = new Dictionary<double, (double Buy, double Sell, double Unclassified)>(256);
        var prints = 0;

        foreach (var minute in this.minutes)
        {
            if (minute.MinuteUtc < startUtc || minute.MinuteUtc >= endUtc)
                continue;

            prints += minute.Prints;

            foreach (var level in minute.Levels)
            {
                byPrice.TryGetValue(level.Price, out var slot);
                slot.Buy += level.Buy;
                slot.Sell += level.Sell;
                slot.Unclassified += level.Unclassified;
                byPrice[level.Price] = slot;
            }
        }

        var levels = new List<ProfileLevel>(byPrice.Count);

        foreach (var pair in byPrice)
        {
            levels.Add(new ProfileLevel(
                pair.Key, pair.Value.Buy, pair.Value.Sell, pair.Value.Unclassified));
        }

        levels.Sort(static (a, b) => a.Price.CompareTo(b.Price));

        return new ProfileTickSlice(levels, prints);
    }

    /// <summary>
    /// Projects this cache into the verdict's view of it, for one range.
    ///
    /// The verdict decides whether a profile is drawn at all, so it must be told what this
    /// source offers BEFORE it decides — a source discovered afterwards could never be used.
    /// </summary>
    /// <param name="startUtc">Range start, inclusive.</param>
    /// <param name="endUtc">Range end, exclusive.</param>
    /// <param name="slice">The summed levels, so the caller does not sum twice.</param>
    public ProfileTickSource Describe(DateTime startUtc, DateTime endUtc, out ProfileTickSlice slice)
    {
        slice = this.Slice(startUtc, endUtc);

        // COVERAGE IS PART OF THE ANSWER, NOT AN ASIDE.
        //
        // The connector caps one request at a span of its own choosing — an hour on one data vendor —
        // and a cache holding less than the range asked for describes a DIFFERENT window. The
        // first live run drew exactly that: a 61-minute cache rendered as a profile labelled
        // from 13:30Z, with FRVP and AVP reporting byte-identical counts for ranges an hour
        // apart, which is only possible when both are being handed the whole cache.
        //
        // The rule is the one this file's neighbour already applies to the live accumulation:
        // a source that cannot cover the range is REFUSED rather than allowed to understate it
        // silently. Same reasoning, same consequence — an understated profile is not a smaller
        // truth, it is a wrong POC and a wrong value area.
        var covers = startUtc >= this.FromUtc;

        return new ProfileTickSource(
            slice.Prints, slice.Levels.Count, this.Served, this.Status, covers, this.FromUtc,
            this.SourceLabel);
    }
}

/// <summary>Per-price volume for one requested range, and the prints behind it.</summary>
/// <param name="Levels">Per-price volume, price-ascending.</param>
/// <param name="Prints">Trade prints that contributed.</param>
public readonly record struct ProfileTickSlice(IReadOnlyList<ProfileLevel> Levels, int Prints);
