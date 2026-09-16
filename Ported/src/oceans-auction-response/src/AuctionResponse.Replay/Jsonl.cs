using System.Globalization;
using System.Text;
using System.Text.Json;
using AuctionResponse.Core;

namespace AuctionResponse.Replay;

/// <summary>
/// JSONL serialization for the event log (Section 17): one complete record per line,
/// schema-versioned, decimal-safe.
///
/// Decimals are written as invariant-culture STRINGS and optional 64-bit ids as nullable
/// strings. Both choices exist because a JSON consumer that reads numbers as doubles would
/// silently corrupt a price or an order id, and a corrupted log is worse than no log.
/// </summary>
public static class Jsonl
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    public static string Serialize(MarketEvent e)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", e.SchemaVersion);
            w.WriteString("engineVersion", e.EngineVersion);
            w.WriteString("instrumentSymbol", e.Instrument.Symbol);
            w.WriteString("instrumentExchange", e.Instrument.Exchange);
            w.WriteString("instrumentExpiry", e.Instrument.Expiry);
            w.WriteNumber("connectionEpoch", e.ConnectionEpoch);
            w.WriteNumber("eventSequence", e.EventSequence);
            w.WriteString("kind", e.Kind.ToString());
            w.WriteString("receiveUtc", e.ReceiveUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("receiveElapsedNs", e.ReceiveElapsedNs);
            WriteNullableString(w, "exchangeUtc", e.ExchangeUtc?.ToString("O", CultureInfo.InvariantCulture));
            WriteNullableNumber(w, "sourceSequence", e.SourceSequence);
            WriteNullableNumber(w, "rawDirection", e.RawDirection);
            WriteNullableNumber(w, "rawDataType", e.RawDataType);
            WriteNullableNumber(w, "rawUpdateType", e.RawUpdateType);
            WriteNullableDecimal(w, "originalPrice", e.OriginalPrice);
            WriteNullableDecimal(w, "originalQuantity", e.OriginalQuantity);
            WriteNullableNumber(w, "priceTicks", e.PriceTicks);
            w.WriteString("direction", e.Direction.ToString());
            WriteNullableString(w, "side", e.Side?.ToString());
            WriteNullableString(w, "orderId", e.OrderId);
            WriteNullableString(w, "aggressorId", e.AggressorId);
            WriteNullableNumber(w, "bidTicks", e.BidTicks);
            WriteNullableNumber(w, "askTicks", e.AskTicks);
            WriteNullableDecimal(w, "bidQuantity", e.BidQuantity);
            WriteNullableDecimal(w, "askQuantity", e.AskQuantity);
            w.WriteNumber("quality", (int)e.Quality);
            WriteNullableString(w, "detail", e.Detail);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static MarketEvent DeserializeEvent(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        return new MarketEvent
        {
            SchemaVersion = r.GetProperty("schemaVersion").GetString()!,
            EngineVersion = r.GetProperty("engineVersion").GetString()!,
            Instrument = new InstrumentKey(
                r.GetProperty("instrumentSymbol").GetString()!,
                r.GetProperty("instrumentExchange").GetString()!,
                r.GetProperty("instrumentExpiry").GetString()!),
            ConnectionEpoch = r.GetProperty("connectionEpoch").GetInt32(),
            EventSequence = r.GetProperty("eventSequence").GetInt64(),
            Kind = Enum.Parse<EventKind>(r.GetProperty("kind").GetString()!),
            ReceiveUtc = DateTime.Parse(r.GetProperty("receiveUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ReceiveElapsedNs = r.GetProperty("receiveElapsedNs").GetInt64(),
            ExchangeUtc = ReadNullableDate(r, "exchangeUtc"),
            SourceSequence = ReadNullableLong(r, "sourceSequence"),
            RawDirection = ReadNullableInt(r, "rawDirection"),
            RawDataType = ReadNullableInt(r, "rawDataType"),
            RawUpdateType = ReadNullableInt(r, "rawUpdateType"),
            OriginalPrice = ReadNullableDecimal(r, "originalPrice"),
            OriginalQuantity = ReadNullableDecimal(r, "originalQuantity"),
            PriceTicks = ReadNullableLong(r, "priceTicks"),
            Direction = Enum.Parse<AggressorDirection>(r.GetProperty("direction").GetString()!),
            Side = ReadNullableString(r, "side") is { } s ? Enum.Parse<BookSide>(s) : null,
            OrderId = ReadNullableString(r, "orderId"),
            AggressorId = ReadNullableString(r, "aggressorId"),
            BidTicks = ReadNullableLong(r, "bidTicks"),
            AskTicks = ReadNullableLong(r, "askTicks"),
            BidQuantity = ReadNullableDecimal(r, "bidQuantity"),
            AskQuantity = ReadNullableDecimal(r, "askQuantity"),
            Quality = (QualityFlag)r.GetProperty("quality").GetInt32(),
            Detail = ReadNullableString(r, "detail")
        };
    }

    public static string Serialize(TransitionRecord t)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", t.SchemaVersion);
            w.WriteString("engineVersion", t.EngineVersion);
            w.WriteString("transitionId", t.TransitionId);
            w.WriteString("candidateId", t.CandidateId);
            w.WriteString("levelId", t.LevelId);
            w.WriteString("detectionUtc", t.DetectionUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("detectionElapsedNs", t.DetectionElapsedNs);
            w.WriteNumber("sourceSequence", t.SourceSequence);
            w.WriteString("instrument", t.Instrument.ToString());
            w.WriteNumber("connectionEpoch", t.ConnectionEpoch);
            w.WriteNumber("orientation", t.Orientation);
            w.WriteString("oldState", t.OldState.ToString());
            w.WriteString("newState", t.NewState.ToString());
            w.WriteString("reason", t.Reason);
            w.WriteNumber("levelTicks", t.LevelTicks);
            w.WriteNumber("zoneLowTicks", t.ZoneLowTicks);
            w.WriteNumber("zoneHighTicks", t.ZoneHighTicks);
            w.WriteNumber("confirmationHalfTicks", t.ConfirmationHalfTicks);
            w.WriteNumber("failureHalfTicks", t.FailureHalfTicks);
            w.WriteString("dataQuality", t.DataQuality.ToString());
            w.WriteString("configurationHash", t.ConfigurationHash);
            WriteNullableString(w, "baselineHash", t.BaselineHash);
            WriteNullableString(w, "modelHash", t.ModelHash);
            w.WritePropertyName("features");
            WriteFeatures(w, t.Features);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteFeatures(Utf8JsonWriter w, FeatureSnapshot f)
    {
        w.WriteStartObject();
        w.WriteNumber("atNs", f.AtNs);
        w.WriteString("atUtc", f.AtUtc.ToString("O", CultureInfo.InvariantCulture));
        w.WriteNumber("atSequence", f.AtSequence);
        WriteMeasure(w, "buyVolume", f.BuyVolume);
        WriteMeasure(w, "sellVolume", f.SellVolume);
        WriteMeasure(w, "unknownVolume", f.UnknownVolume);
        WriteMeasure(w, "totalVolume", f.TotalVolume);
        WriteMeasure(w, "delta", f.Delta);
        WriteMeasure(w, "normalizedDelta", f.NormalizedDelta);
        WriteMeasure(w, "sideQuality", f.SideQuality);
        WriteMeasure(w, "response", f.Response);
        WriteMeasure(w, "queueImbalance", f.QueueImbalance);
        WriteMeasure(w, "ofi", f.Ofi);
        WriteMeasure(w, "meanDepth", f.MeanDepth);
        WriteMeasure(w, "depthNormalizedOfi", f.DepthNormalizedOfi);
        WriteMeasure(w, "spread", f.Spread);
        WriteMeasure(w, "zoneAttackerVolume", f.ZoneAttackerVolume);
        WriteMeasure(w, "zoneVolumeFraction", f.ZoneVolumeFraction);
        WriteMeasure(w, "cumulativeDelta", f.CumulativeDelta);
        WriteMeasure(w, "residualEvidence", f.ResidualEvidence);
        WriteMeasure(w, "netAdditionEvidence", f.NetAdditionEvidence);
        WriteMeasure(w, "netRemovalEvidence", f.NetRemovalEvidence);
        w.WriteEndObject();
    }

    private static void WriteMeasure(Utf8JsonWriter w, string name, Measure m)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        if (m.Value is { } v) w.WriteString("value", v.ToString("R", CultureInfo.InvariantCulture));
        else w.WriteNull("value");
        w.WriteString("unit", m.Unit ?? "");
        w.WriteNumber("windowMs", m.WindowMs);
        w.WriteString("quality", m.Quality.ToString());
        WriteNullableString(w, "reason", m.Reason);
        w.WriteEndObject();
    }

    public static string Serialize(DecisionTick t)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("kind", "DecisionTick");
            w.WriteNumber("eventSequence", t.EventSequence);
            w.WriteNumber("connectionEpoch", t.ConnectionEpoch);
            w.WriteNumber("elapsedNs", t.ElapsedNs);
            w.WriteString("receiveUtc", t.ReceiveUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteBoolean("wasLate", t.WasLate);
            w.WriteNumber("processingLagNs", t.ProcessingLagNs);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static DecisionTick DeserializeTick(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        return new DecisionTick
        {
            EventSequence = r.GetProperty("eventSequence").GetInt64(),
            ConnectionEpoch = r.GetProperty("connectionEpoch").GetInt32(),
            ElapsedNs = r.GetProperty("elapsedNs").GetInt64(),
            ReceiveUtc = DateTime.Parse(r.GetProperty("receiveUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            WasLate = r.GetProperty("wasLate").GetBoolean(),
            ProcessingLagNs = r.GetProperty("processingLagNs").GetInt64()
        };
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, long? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    private static void WriteNullableDecimal(Utf8JsonWriter w, string name, decimal? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string? ReadNullableString(JsonElement r, string name)
        => r.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetString() : null;

    private static long? ReadNullableLong(JsonElement r, string name)
        => r.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetInt64() : null;

    private static int? ReadNullableInt(JsonElement r, string name)
        => r.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetInt32() : null;

    private static decimal? ReadNullableDecimal(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return null;
        return p.ValueKind == JsonValueKind.String
            ? decimal.Parse(p.GetString()!, CultureInfo.InvariantCulture)
            : p.GetDecimal();
    }

    private static DateTime? ReadNullableDate(JsonElement r, string name)
        => ReadNullableString(r, name) is { } s
            ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
}
