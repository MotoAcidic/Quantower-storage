using System;
using System.Globalization;

namespace OrbIx.Core.Abstractions;

/// <summary>
/// How the Level 2 stream was classified.
/// </summary>
public enum MboClassification
{
    /// <summary>No event arrived. Says nothing about the feed.</summary>
    NoData,

    /// <summary>
    /// Too few events to separate per-order identifiers from per-level ones. A refusal to
    /// answer, not a negative finding.
    /// </summary>
    Inconclusive,

    /// <summary>Events carried no identifier at all.</summary>
    AggregatedNoIdentifiers,

    /// <summary>Identifiers recycle at roughly the rate of price levels.</summary>
    AggregatedByPrice,

    /// <summary>Identifiers greatly outnumber price levels — only a per-order feed does that.</summary>
    PerOrder,
}

/// <summary>
/// An immutable count of what the Level 2 stream carried. Every field is raw; no field
/// asserts a conclusion. The verdict is derived separately so the counts stay readable if
/// the rule ever changes.
/// </summary>
public sealed record Level2Evidence(
    long TotalEvents,
    long BidEvents,
    long AskEvents,
    long EventsWithNonEmptyId,
    int DistinctIds,
    bool DistinctIdsSaturated,
    int DistinctPrices,
    long EventsWithNonZeroPriority,
    long EventsWithClosedTrue,
    long EventsWithNumberOrders,
    double MaxNumberOrders,
    long EventsWithSizeOne,
    double MaxSize,
    double ObservedSeconds);

/// <summary>
/// Turns raw counts into a classification, keeping the rule in one readable place.
/// Deliberately conservative: it answers "inconclusive" rather than guessing, because the
/// order-level modules stay disabled on anything except a positive
/// <see cref="MboClassification.PerOrder"/>.
/// </summary>
public static class MboVerdict
{
    /// <summary>
    /// Below this many events the ratio is dominated by how long the probe happened to run
    /// rather than by the feed's nature.
    /// </summary>
    public const long MinimumEventsForVerdict = 5_000;

    /// <summary>
    /// How many times more distinct identifiers than distinct prices are needed before the
    /// identifier count alone calls a feed per-order.
    ///
    /// Lowered from four after the first live comparison. A market-by-price feed cannot
    /// exceed one identifier per level, so anything above one is already evidence; four was
    /// a margin picked before any feed had been measured, and it misclassified a one data vendor
    /// stream observed at 3.64 that was unambiguously per-order by every other measure.
    /// Two keeps a real margin over the 1.004 a market-by-price feed actually produced.
    /// </summary>
    public const double PerOrderIdentifierRatio = 2.0;

    /// <summary>
    /// Fraction of events that must carry a non-zero queue priority for the feed to be
    /// called per-order on that basis alone.
    ///
    /// This is the dispositive signal and it took a live comparison to see why. Queue
    /// priority is a property of an order's place in a queue: a market-by-price feed
    /// aggregates that away and has nothing to put in the field. Measured, a market-by-price
    /// feed returned zero non-zero priorities across 39,573 events, while a market-by-order
    /// feed returned them on 78% of 17,538. No ratio of identifiers to prices separates the
    /// two that cleanly.
    /// </summary>
    public const double PerOrderPriorityFraction = 0.10;

    public static MboClassification Classify(Level2Evidence evidence)
    {
        if (evidence is null)
            throw new ArgumentNullException(nameof(evidence));

        if (evidence.TotalEvents == 0)
            return MboClassification.NoData;

        if (evidence.EventsWithNonEmptyId == 0)
            return MboClassification.AggregatedNoIdentifiers;

        if (evidence.TotalEvents < MinimumEventsForVerdict || evidence.DistinctPrices == 0)
            return MboClassification.Inconclusive;

        // Queue priority first. A feed that reports where an order sits in a queue is
        // reporting orders, and nothing else explains the field being populated.
        var priorityFraction = (double)evidence.EventsWithNonZeroPriority / evidence.TotalEvents;

        if (priorityFraction >= PerOrderPriorityFraction)
            return MboClassification.PerOrder;

        // Otherwise fall back to identifier distinctness, which catches a per-order feed
        // that happens not to publish priority.
        var ratio = (double)evidence.DistinctIds / evidence.DistinctPrices;

        return ratio >= PerOrderIdentifierRatio
            ? MboClassification.PerOrder
            : MboClassification.AggregatedByPrice;
    }

    /// <summary>
    /// A one-line explanation carrying the numbers the verdict rests on, so no reader has
    /// to take the classification on trust.
    /// </summary>
    public static string Explain(Level2Evidence evidence)
    {
        if (evidence is null)
            throw new ArgumentNullException(nameof(evidence));

        var ratio = evidence.DistinctPrices == 0
            ? 0d
            : (double)evidence.DistinctIds / evidence.DistinctPrices;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1:N0} events over {2:N1}s; {3:N0} carried an identifier; "
            + "{4:N0} distinct identifiers{5} across {6:N0} distinct prices "
            + "(ratio {7:N2} against a {8:N2} threshold); {9:N0} had non-zero Priority; "
            + "{10:N0} were marked Closed; {11:N0} reported NumberOrders (max {12:N0}); "
            + "{13:N0} had size 1 (max size {14:N0}).",
            Classify(evidence),
            evidence.TotalEvents,
            evidence.ObservedSeconds,
            evidence.EventsWithNonEmptyId,
            evidence.DistinctIds,
            evidence.DistinctIdsSaturated ? " (capped)" : string.Empty,
            evidence.DistinctPrices,
            ratio,
            PerOrderIdentifierRatio,
            evidence.EventsWithNonZeroPriority,
            evidence.EventsWithClosedTrue,
            evidence.EventsWithNumberOrders,
            evidence.MaxNumberOrders,
            evidence.EventsWithSizeOne,
            evidence.MaxSize);
    }
}
