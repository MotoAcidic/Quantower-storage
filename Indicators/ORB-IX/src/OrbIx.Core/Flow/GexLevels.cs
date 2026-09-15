using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrbIx.Core.Flow;

/// <summary>
/// One published options level on the INDEX scale, as the AramidGamma publisher writes it
/// (<c>the research repository: quantower-gamma/AramidGammaLevels.cs</c>, <c>GammaLevel</c>).
/// </summary>
public sealed class GexLevel
{
    /// <summary>One of call_wall, put_wall, gamma_flip, gamma_magnet — the publisher's names.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("index_price")]
    public double IndexPrice { get; set; }
}

/// <summary>The AramidGamma <c>levels-*.json</c> document, same field names, same meaning.</summary>
public sealed class GexSnapshot
{
    [JsonPropertyName("generated_utc")]
    public DateTime GeneratedUtc { get; set; }

    [JsonPropertyName("index")]
    public string Index { get; set; } = string.Empty;

    [JsonPropertyName("applies_to")]
    public string[]? AppliesTo { get; set; }

    [JsonPropertyName("index_spot")]
    public double IndexSpot { get; set; }

    [JsonPropertyName("index_spot_time")]
    public string? IndexSpotTime { get; set; }

    [JsonPropertyName("levels")]
    public GexLevel[]? Levels { get; set; }
}

/// <summary>A level translated onto the chart's own price scale.</summary>
public readonly record struct ChartGexLevel(string Name, double IndexPrice, double ChartPrice);

/// <summary>
/// GEX walls for the presenter's bias read (30:56–32:00: "we're below max pain and we were
/// approaching the put wall […] if you're above the call wall").
///
/// NOTHING IS COMPUTED HERE. Computing dealer gamma needs an options feed; the operator's
/// options source is dead (memory: unusual-whales-economic-calendar) and the publisher's
/// timer is inactive (observed 2026-09-11). So the levels come from two places only: the
/// AramidGamma file if one exists, in its own schema, and the operator's manual inputs.
///
/// THE BASIS IS ADDITIVE, exactly as AramidGamma applies it: chart price = index price +
/// (chart last − index spot). A stale spot shifts every level by the same few points; the
/// snapshot's spot time is carried so the shell can say how old it is.
/// </summary>
public static class GexLevels
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>Parses a levels document. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static GexSnapshot? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var snapshot = JsonSerializer.Deserialize<GexSnapshot>(json, Options);

        if (snapshot?.Levels is null || snapshot.Levels.Length == 0)
            return null;

        return snapshot;
    }

    /// <summary>Translates the snapshot's levels onto the chart scale using the chart's last price.</summary>
    public static IReadOnlyList<ChartGexLevel> Translate(GexSnapshot snapshot, double chartLastPrice)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!(snapshot.IndexSpot > 0) || !double.IsFinite(chartLastPrice) || chartLastPrice <= 0)
            return Array.Empty<ChartGexLevel>();

        var basis = chartLastPrice - snapshot.IndexSpot;
        var result = new List<ChartGexLevel>(snapshot.Levels?.Length ?? 0);

        foreach (var level in snapshot.Levels ?? Array.Empty<GexLevel>())
        {
            if (!double.IsFinite(level.IndexPrice) || level.IndexPrice <= 0)
                continue;

            result.Add(new ChartGexLevel(level.Name, level.IndexPrice, level.IndexPrice + basis));
        }

        return result;
    }

    /// <summary>Manual levels typed by the operator, already on the chart scale. Zero means "not set".</summary>
    public static IReadOnlyList<ChartGexLevel> Manual(double callWall, double putWall, double maxPain)
    {
        var result = new List<ChartGexLevel>(3);

        if (callWall > 0 && double.IsFinite(callWall))
            result.Add(new ChartGexLevel("call_wall", callWall, callWall));

        if (putWall > 0 && double.IsFinite(putWall))
            result.Add(new ChartGexLevel("put_wall", putWall, putWall));

        if (maxPain > 0 && double.IsFinite(maxPain))
            result.Add(new ChartGexLevel("max_pain", maxPain, maxPain));

        return result;
    }
}
