namespace AuctionResponse.Core;

public enum MeasureQuality
{
    /// <summary>Computed from a complete, fresh, valid input window.</summary>
    Good,
    /// <summary>Computed, but an input was degraded; usable for display, not for alerts.</summary>
    Degraded,
    /// <summary>No value exists. Distinct from a value of zero.</summary>
    Unavailable
}

/// <summary>
/// A single feature value with its provenance (Section 16). Null is distinct from zero
/// throughout: a zero-size book is <em>unavailable</em> imbalance, not balanced.
/// </summary>
public readonly record struct Measure(
    double? Value,
    string Unit,
    int WindowMs,
    long AvailableAtNs,
    MeasureQuality Quality,
    string? Reason)
{
    public bool IsAvailable => Value.HasValue && Quality != MeasureQuality.Unavailable;

    public static Measure Of(double value, string unit, int windowMs, long availableAtNs, MeasureQuality quality = MeasureQuality.Good)
        => new(value, unit, windowMs, availableAtNs, quality, null);

    public static Measure Unavailable(string unit, int windowMs, long availableAtNs, string reason)
        => new(null, unit, windowMs, availableAtNs, MeasureQuality.Unavailable, reason);

    public override string ToString() => IsAvailable
        ? Value!.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " " + Unit
        : "N/A (" + (Reason ?? "unavailable") + ")";
}
