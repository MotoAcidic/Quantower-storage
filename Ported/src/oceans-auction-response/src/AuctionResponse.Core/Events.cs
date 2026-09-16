namespace AuctionResponse.Core;

public enum EventKind
{
    Trade,
    BestQuote,
    DepthChange,
    DepthSnapshot,
    MboChange,
    MboSnapshot,
    Connection,
    Configuration,
    LevelDefinition,
    Health,
    DecisionTick
}

/// <summary>Aggressor direction. 0 is genuinely unknown, never coerced to a side.</summary>
public enum AggressorDirection { Unknown = 0, Buy = 1, Sell = -1 }

/// <summary>Which side of the book a quote/depth/order record refers to.</summary>
public enum BookSide { Bid, Ask }

[Flags]
public enum QualityFlag
{
    None = 0,
    /// <summary>Price did not lie on the instrument tick grid; the record is not usable for math.</summary>
    OffGrid = 1 << 0,
    /// <summary>Arrived while the adapter was still acquiring its initial snapshot.</summary>
    DuringSnapshotAcquisition = 1 << 1,
    /// <summary>Part of a snapshot: initialises state, contributes no flow, OFI or volume.</summary>
    SnapshotInitialisation = 1 << 2,
    /// <summary>Local elapsed clock regressed or could not be trusted.</summary>
    ClockSuspect = 1 << 3,
    /// <summary>Enum value from the host was outside the known set.</summary>
    UnknownHostEnum = 1 << 4,
    /// <summary>Quantity was negative — an explicit fault, not clamped.</summary>
    NegativeQuantity = 1 << 5,
    /// <summary>Arrived after an ingress overflow, so history is incomplete.</summary>
    AfterOverflow = 1 << 6
}

/// <summary>
/// Instrument identity including expiry (Section 5). Expired contracts never merge with
/// current ones, in baselines or anywhere else.
/// </summary>
public readonly record struct InstrumentKey(string Symbol, string Exchange, string Expiry)
{
    public override string ToString() => Symbol + "@" + Exchange + "/" + Expiry;
    public static InstrumentKey Unknown => new("", "", "");
}

/// <summary>
/// One canonical, immutable event record (Section 5). Every field the spec requires is
/// present; fields that do not apply to a kind stay null rather than defaulting to zero.
/// </summary>
public sealed record MarketEvent
{
    public string SchemaVersion { get; init; } = Versioning.SchemaVersion;
    public string EngineVersion { get; init; } = Versioning.EngineVersion;
    public InstrumentKey Instrument { get; init; } = InstrumentKey.Unknown;

    /// <summary>Increments on every (re)connection. Windows and candidates never span epochs.</summary>
    public int ConnectionEpoch { get; init; }

    /// <summary>Monotonic ingress sequence, assigned under the ingress lock.</summary>
    public long EventSequence { get; init; }

    public EventKind Kind { get; init; }

    /// <summary>Wall-clock stamp of receipt. Diagnostic; never used as feature time.</summary>
    public DateTime ReceiveUtc { get; init; }

    /// <summary>Monotonic elapsed nanoseconds within the epoch. This IS the feature clock.</summary>
    public long ReceiveElapsedNs { get; init; }

    /// <summary>Feed/exchange timestamp when supplied. Diagnostic metadata only (Section 5).</summary>
    public DateTime? ExchangeUtc { get; init; }

    /// <summary>Feed sequence when supplied. Null on feeds that do not provide one.</summary>
    public long? SourceSequence { get; init; }

    /// <summary>Host enum values exactly as received, before any interpretation.</summary>
    public int? RawDirection { get; init; }
    public int? RawDataType { get; init; }
    public int? RawUpdateType { get; init; }

    public decimal? OriginalPrice { get; init; }
    public decimal? OriginalQuantity { get; init; }

    /// <summary>Price as an exact integer tick index. Null when off-grid or not applicable.</summary>
    public long? PriceTicks { get; init; }

    public AggressorDirection Direction { get; init; } = AggressorDirection.Unknown;
    public BookSide? Side { get; init; }

    /// <summary>Serialized as strings: JSON consumers lose precision on 64-bit integers.</summary>
    public string? OrderId { get; init; }
    public string? AggressorId { get; init; }

    /// <summary>Best bid/ask carried on a BestQuote record, in integer ticks.</summary>
    public long? BidTicks { get; init; }
    public long? AskTicks { get; init; }
    public decimal? BidQuantity { get; init; }
    public decimal? AskQuantity { get; init; }

    public QualityFlag Quality { get; init; } = QualityFlag.None;

    /// <summary>Free-text detail for Connection / Configuration / Health / LevelDefinition kinds.</summary>
    public string? Detail { get; init; }

    public override string ToString() =>
        "#" + EventSequence + " " + Kind + " @" + ReceiveElapsedNs + "ns";
}

/// <summary>
/// A recorded decision point (Section 5). Ticks travel in the same ingress sequence as
/// market events, so "what was available at the decision" is a property of the log, not
/// of a wall clock read later.
/// </summary>
public sealed record DecisionTick
{
    public long EventSequence { get; init; }
    public int ConnectionEpoch { get; init; }
    public long ElapsedNs { get; init; }
    public DateTime ReceiveUtc { get; init; }
    /// <summary>True when the timer fired late and this tick stands in for several nominal ones.</summary>
    public bool WasLate { get; init; }
    public long ProcessingLagNs { get; init; }
}
