namespace AuctionResponse.Core;

/// <summary>
/// Displayed-quantity accounting over a reconciled interval (Section 10).
///
/// The honest limit is built into the type: from endpoint quantities and executed volume
/// you recover the NET addition G = A - C, never gross additions and gross cancellations
/// separately. Nothing here claims "replenishment".
/// </summary>
public readonly record struct NetDisplayedAddition(
    decimal? NetAddition,
    decimal? AdditionLowerBound,
    decimal? RemovalLowerBound,
    bool IntervalComplete,
    string? Reason)
{
    /// <summary>
    /// G = Q_post - Q_pre + E. Requires a COMPLETE reconciled interval and a known executed
    /// resting quantity; otherwise every output is unavailable rather than guessed.
    /// </summary>
    public static NetDisplayedAddition Compute(decimal quantityBefore, decimal quantityAfter, decimal? executedResting, bool intervalComplete)
    {
        if (!intervalComplete)
            return new NetDisplayedAddition(null, null, null, false, "reconciliation interval incomplete");
        if (executedResting is null)
            return new NetDisplayedAddition(null, null, null, false, "executed resting quantity unknown");

        var g = quantityAfter - quantityBefore + executedResting.Value;
        return new NetDisplayedAddition(g, Math.Max(g, 0m), Math.Max(-g, 0m), true, null);
    }
}

/// <summary>Why an execution could not be linked to a resting order.</summary>
public enum LinkageOutcome { Linked, Unknown, NotAttempted }

/// <summary>
/// One tracked resting order (Section 10). Keyed by instrument, connection epoch and
/// exchange order id. Snapshot orders carry left-censored age: the arrival of a snapshot
/// is not the creation time of the order, and this type refuses to pretend otherwise.
/// </summary>
public sealed class RestingOrder
{
    public required long ExchangeOrderId { get; init; }
    public required BookSide Side { get; init; }
    public required long PriceTicks { get; init; }
    public decimal DisplayedQuantity { get; set; }
    public long Priority { get; set; }
    public required long FirstSeenNs { get; init; }
    public long LastUpdateNs { get; set; }

    /// <summary>False when the order was first seen in a snapshot: its age is a lower bound only.</summary>
    public required bool BirthObserved { get; init; }

    public bool AgeIsLeftCensored => !BirthObserved;
    public long? ObservedAgeNs(long nowNs) => BirthObserved ? nowNs - FirstSeenNs : null;
}

/// <summary>
/// Descriptive zone-level MBO evidence (Section 10). These are labelled net-addition and
/// net-removal evidence. They are not probabilities, not iceberg sizes, and never create a
/// confirmation on their own.
/// </summary>
public sealed record ZoneOrderEvidence(
    decimal LinkedExecutedVolume,
    decimal AdditionLowerBound,
    decimal RemovalLowerBound,
    decimal ZoneQuantityAtStart,
    int ReconciledIntervals,
    int UnknownAssociations,
    int LeftCensoredOrders)
{
    /// <summary>r_add = A_lower / max(E, 1) — net-addition evidence.</summary>
    public Measure NetAdditionEvidence(long atNs, int windowMs)
        => Measure.Of((double)(AdditionLowerBound / Math.Max(LinkedExecutedVolume, 1m)), "ratio", windowMs, atNs);

    /// <summary>r_remove = C_lower / max(Q_start + A_lower, 1) — net-removal evidence.</summary>
    public Measure NetRemovalEvidence(long atNs, int windowMs)
        => Measure.Of((double)(RemovalLowerBound / Math.Max(ZoneQuantityAtStart + AdditionLowerBound, 1m)), "ratio", windowMs, atNs);

    /// <summary>
    /// True only when every reconciliation interval in the zone resolved its executions.
    /// An unresolved association is Unknown, never a guessed cancellation.
    /// </summary>
    public bool IsTrustworthy => UnknownAssociations == 0 && ReconciledIntervals > 0;
}

/// <summary>
/// Order-level evidence tracker. Stays disabled until the adapter proves BOTH a snapshot
/// fence and passive execution linkage (Section 13, Section 21 Phase Zero).
/// </summary>
public sealed class OrderBookEvidenceTracker
{
    private readonly Dictionary<(int Epoch, long OrderId), RestingOrder> _orders = new();

    public bool Enabled { get; private set; }
    public string DisabledReason { get; private set; } = "MBO module disabled by configuration";

    public int TrackedOrderCount => _orders.Count;
    public int UnknownAssociations { get; private set; }
    public int LeftCensoredOrders { get; private set; }

    /// <summary>
    /// Enables order-level evidence. Both gates must be demonstrated by the adapter on the
    /// live feed; neither can be assumed from the presence of an API.
    /// </summary>
    public void Enable(bool snapshotFenceProven, bool executionLinkageProven)
    {
        if (!snapshotFenceProven) { Enabled = false; DisabledReason = "snapshot/change fence not established — MBO Unverified"; return; }
        if (!executionLinkageProven) { Enabled = false; DisabledReason = "passive execution linkage not verified on this feed"; return; }
        Enabled = true;
        DisabledReason = "";
    }

    public void Disable(string reason) { Enabled = false; DisabledReason = reason; }

    /// <summary>
    /// Applies an order-level update. Unknown changes, duplicate New ids and negative
    /// quantities are explicit faults in this path: they disable the module rather than
    /// being upserted as if history were complete.
    /// </summary>
    public bool Apply(int epoch, long orderId, BookSide side, long priceTicks, decimal quantity, MboUpdateKind kind, long ns)
    {
        if (!Enabled) return false;
        if (quantity < 0m) { Disable("negative order quantity for id " + orderId); return false; }

        var key = (epoch, orderId);
        switch (kind)
        {
            case MboUpdateKind.Snapshot:
                _orders[key] = new RestingOrder
                {
                    ExchangeOrderId = orderId, Side = side, PriceTicks = priceTicks,
                    DisplayedQuantity = quantity, FirstSeenNs = ns, LastUpdateNs = ns,
                    BirthObserved = false
                };
                LeftCensoredOrders++;
                return true;

            case MboUpdateKind.New:
                if (_orders.ContainsKey(key)) { Disable("duplicate New for order id " + orderId); return false; }
                _orders[key] = new RestingOrder
                {
                    ExchangeOrderId = orderId, Side = side, PriceTicks = priceTicks,
                    DisplayedQuantity = quantity, FirstSeenNs = ns, LastUpdateNs = ns,
                    BirthObserved = true
                };
                return true;

            case MboUpdateKind.Change:
                if (!_orders.TryGetValue(key, out var existing)) { Disable("change for unknown order id " + orderId); return false; }
                existing.DisplayedQuantity = quantity;
                existing.LastUpdateNs = ns;
                return true;

            case MboUpdateKind.Delete:
                if (!_orders.Remove(key)) { Disable("delete for unknown order id " + orderId); return false; }
                return true;

            default:
                Disable("unknown MBO update kind " + (int)kind);
                return false;
        }
    }

    public void RecordUnknownAssociation() => UnknownAssociations++;

    public void ResetEpoch()
    {
        _orders.Clear();
        UnknownAssociations = 0;
        LeftCensoredOrders = 0;
    }

    public decimal QuantityAt(int epoch, BookSide side, long priceTicks)
    {
        decimal sum = 0m;
        foreach (var kv in _orders)
            if (kv.Key.Epoch == epoch && kv.Value.Side == side && kv.Value.PriceTicks == priceTicks)
                sum += kv.Value.DisplayedQuantity;
        return sum;
    }
}

public enum MboUpdateKind { Snapshot = 0, New = 1, Change = 2, Delete = 3 }
