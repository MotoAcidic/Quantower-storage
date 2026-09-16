using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuctionResponse.Core;

namespace AuctionResponse.Replay;

/// <summary>
/// Reads and writes the frozen baseline artifact (Section 17).
///
/// The artifact's own SHA256 is recomputed on load and compared. A baseline that has been
/// edited by hand is refused rather than silently trusted: the quantile in it is the single
/// number that decides whether any candidate can arm at all.
/// </summary>
public static class BaselineArtifactIo
{
    public static BaselineArtifact Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Baseline artifact not found.", path);

        var json = File.ReadAllText(path, Encoding.UTF8);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var declaredHash = r.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
        var computed = HashWithoutSelf(json);
        if (!string.IsNullOrEmpty(declaredHash) &&
            !string.Equals(declaredHash, computed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Baseline artifact hash mismatch: declared " + declaredHash + ", computed " + computed +
                ". The file has been modified since it was frozen; it is refused rather than trusted.");

        var buckets = new List<BaselineBucket>();
        if (r.TryGetProperty("buckets", out var bucketArray))
            foreach (var b in bucketArray.EnumerateArray())
                buckets.Add(new BaselineBucket
                {
                    BucketStartMinute = b.GetProperty("bucketStartMinute").GetInt32(),
                    WindowMs = b.GetProperty("windowMs").GetInt32(),
                    SampleCount = b.GetProperty("sampleCount").GetInt32(),
                    BuyVolumeQuantiles = ReadQuantiles(b, "buyVolumeQuantiles"),
                    SellVolumeQuantiles = ReadQuantiles(b, "sellVolumeQuantiles"),
                    AbsoluteDeltaQ75 = ReadNullableDouble(b, "absoluteDeltaQ75"),
                    AbsoluteResponseQ75 = ReadNullableDouble(b, "absoluteResponseQ75"),
                    SortedBuyVolumes = ReadDoubles(b, "sortedBuyVolumes"),
                    SortedSellVolumes = ReadDoubles(b, "sortedSellVolumes")
                });

        return new BaselineArtifact
        {
            Instrument = new InstrumentKey(
                r.GetProperty("instrumentSymbol").GetString() ?? "",
                r.GetProperty("instrumentExchange").GetString() ?? "",
                r.GetProperty("instrumentExpiry").GetString() ?? ""),
            TickSize = decimal.Parse(r.GetProperty("tickSize").GetString()!, CultureInfo.InvariantCulture),
            SessionTimezone = r.GetProperty("sessionTimezone").GetString()!,
            CalendarVersion = r.GetProperty("calendarVersion").GetString()!,
            ClockMode = r.GetProperty("clockMode").GetString()!,
            FeedMode = r.GetProperty("feedMode").GetString()!,
            ResponseScaleSource = r.TryGetProperty("responseScaleSource", out var rss) ? rss.GetString()! : "midpoint",
            WindowsMs = r.GetProperty("windowsMs").EnumerateArray().Select(v => v.GetInt32()).ToArray(),
            BucketMinutes = r.GetProperty("bucketMinutes").GetInt32(),
            EligibleSessionIds = r.GetProperty("eligibleSessionIds").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            FitEndUtc = DateTime.Parse(r.GetProperty("fitEndUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            SourceLogHashes = r.GetProperty("sourceLogHashes").EnumerateArray().Select(v => v.GetString()!).ToArray(),
            Sha256 = declaredHash,
            FeatureFormulaVersion = r.TryGetProperty("featureFormulaVersion", out var ffv) ? ffv.GetString()! : "",
            Buckets = buckets
        };
    }

    public static void Save(BaselineArtifact artifact, string path)
    {
        var withoutHash = Serialize(artifact, hash: "");
        var hash = HashWithoutSelf(withoutHash);
        File.WriteAllText(path, Serialize(artifact, hash), Encoding.UTF8);
    }

    private static string Serialize(BaselineArtifact a, string hash)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", a.SchemaVersion);
            w.WriteString("featureFormulaVersion", a.FeatureFormulaVersion);
            w.WriteString("instrumentSymbol", a.Instrument.Symbol);
            w.WriteString("instrumentExchange", a.Instrument.Exchange);
            w.WriteString("instrumentExpiry", a.Instrument.Expiry);
            w.WriteString("tickSize", a.TickSize.ToString(CultureInfo.InvariantCulture));
            w.WriteString("sessionTimezone", a.SessionTimezone);
            w.WriteString("calendarVersion", a.CalendarVersion);
            w.WriteString("clockMode", a.ClockMode);
            w.WriteString("feedMode", a.FeedMode);
            w.WriteString("responseScaleSource", a.ResponseScaleSource);
            w.WriteNumber("bucketMinutes", a.BucketMinutes);
            w.WriteString("fitEndUtc", a.FitEndUtc.ToString("O", CultureInfo.InvariantCulture));

            w.WriteStartArray("windowsMs");
            foreach (var m in a.WindowsMs) w.WriteNumberValue(m);
            w.WriteEndArray();

            w.WriteStartArray("eligibleSessionIds");
            foreach (var s in a.EligibleSessionIds) w.WriteStringValue(s);
            w.WriteEndArray();

            w.WriteStartArray("sourceLogHashes");
            foreach (var s in a.SourceLogHashes) w.WriteStringValue(s);
            w.WriteEndArray();

            w.WriteStartArray("buckets");
            foreach (var b in a.Buckets)
            {
                w.WriteStartObject();
                w.WriteNumber("bucketStartMinute", b.BucketStartMinute);
                w.WriteNumber("windowMs", b.WindowMs);
                w.WriteNumber("sampleCount", b.SampleCount);
                WriteQuantiles(w, "buyVolumeQuantiles", b.BuyVolumeQuantiles);
                WriteQuantiles(w, "sellVolumeQuantiles", b.SellVolumeQuantiles);
                WriteNullableDouble(w, "absoluteDeltaQ75", b.AbsoluteDeltaQ75);
                WriteNullableDouble(w, "absoluteResponseQ75", b.AbsoluteResponseQ75);
                WriteDoubles(w, "sortedBuyVolumes", b.SortedBuyVolumes);
                WriteDoubles(w, "sortedSellVolumes", b.SortedSellVolumes);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteString("sha256", hash);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Hashes the document with its own sha256 field blanked, so the field can carry the result.</summary>
    private static string HashWithoutSelf(string json)
    {
        var marker = "\"sha256\"";
        var index = json.LastIndexOf(marker, StringComparison.Ordinal);
        var normalised = index < 0 ? json : json[..index];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised))).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<double, double> ReadQuantiles(JsonElement parent, string name)
    {
        var result = new Dictionary<double, double>();
        if (!parent.TryGetProperty(name, out var obj)) return result;
        foreach (var p in obj.EnumerateObject())
            result[double.Parse(p.Name, CultureInfo.InvariantCulture)] = p.Value.GetDouble();
        return result;
    }

    private static void WriteQuantiles(Utf8JsonWriter w, string name, IReadOnlyDictionary<double, double> values)
    {
        w.WriteStartObject(name);
        foreach (var kv in values.OrderBy(k => k.Key))
            w.WriteNumber(kv.Key.ToString("0.##", CultureInfo.InvariantCulture), kv.Value);
        w.WriteEndObject();
    }

    private static double? ReadNullableDouble(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetDouble() : null;

    private static void WriteNullableDouble(Utf8JsonWriter w, string name, double? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteNumber(name, value.Value);
    }

    private static IReadOnlyList<double> ReadDoubles(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var a)
            ? a.EnumerateArray().Select(v => v.GetDouble()).ToArray()
            : Array.Empty<double>();

    private static void WriteDoubles(Utf8JsonWriter w, string name, IReadOnlyList<double> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
}
