using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace LevelsMap;

/// <summary>One fetched chain: the underlying's own live price plus every parsed contract.</summary>
internal readonly record struct CboeChain(double Spot, DateTime AsOfUtc, IReadOnlyList<OptionContract> Contracts);

/// <summary>
/// Pulls a free, unauthenticated, delayed options chain snapshot from CBOE's own public CDN —
/// the SAME endpoint github.com/itsfabtrading/gex-multi's `cboe_data.py` fetcher uses (confirmed
/// live, 2026-10-10: `https://cdn.cboe.com/api/global/delayed_quotes/options/{symbol}.json`, no
/// API key, no auth header, just a real browser User-Agent — a bare curl without one gets a 307
/// redirect loop rather than data). Chosen over Massive's paid options tier per the operator's
/// own ask: "thats why i posted that github link will that work for free data."
///
/// CBOE's own JSON gives delta/gamma/theta/vega/iv PER CONTRACT already — but only reliably for
/// contracts near the money; deep ITM/OTM entries routinely come back with gamma/iv exactly 0.0
/// (CBOE just didn't bother computing them, not that they're truly zero). <see cref="GexEngine"/>
/// already treats a contract with no usable IV as "skip it" — true gamma for a deep ITM/OTM
/// contract is negligible anyway, so this doesn't meaningfully bias the result.
///
/// Strike/expiration/call-or-put are NOT separate JSON fields — only encoded in the option's own
/// OCC-format symbol string (e.g. "QQQ261009C00450000"), so <see cref="OccSymbol.TryParse"/>
/// decodes that directly. OCC symbol format itself is a public, industry-standard convention, not
/// proprietary to CBOE.
/// </summary>
internal static class CboeOptionsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static CboeOptionsClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    /// <summary>Fetches and parses the full chain for one underlying (e.g. "QQQ", "SPY"),
    /// filtered to expirations within <paramref name="maxDaysToExpiry"/> of now — the far-dated
    /// contributes negligible gamma and this codebase has no reason to pay the parse/compute cost
    /// for all 10,000+ contracts a full multi-year chain can carry.</summary>
    public static CboeChain FetchChain(string underlying, int maxDaysToExpiry)
    {
        var url = $"https://cdn.cboe.com/api/global/delayed_quotes/options/{Uri.EscapeDataString(underlying)}.json";
        var response = Http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var data = root.GetProperty("data");
        var spot = data.GetProperty("current_price").GetDouble();

        var asOfUtc = DateTime.UtcNow;
        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTs))
        {
            asOfUtc = parsedTs;
        }

        var cutoff = asOfUtc.Date.AddDays(maxDaysToExpiry);
        var contracts = new List<OptionContract>();

        foreach (var opt in data.GetProperty("options").EnumerateArray())
        {
            if (!opt.TryGetProperty("option", out var symbolProp) || symbolProp.ValueKind != JsonValueKind.String) continue;
            if (!OccSymbol.TryParse(symbolProp.GetString(), out var isCall, out var strike, out var expiration)) continue;
            if (expiration > cutoff) continue;

            var openInterest = GetDoubleOrZero(opt, "open_interest");
            var iv = GetDoubleOrZero(opt, "iv");

            if (openInterest <= 0 || iv <= 0) continue; // nothing to contribute to GEX without both

            contracts.Add(new OptionContract(strike, isCall, openInterest, iv, expiration));
        }

        return new CboeChain(spot, asOfUtc, contracts);
    }

    private static double GetDoubleOrZero(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
}

/// <summary>OCC option symbol decoder — ROOT + YYMMDD + C/P + 8-digit strike (strike × 1000). The
/// root's own length varies (padded to 6 chars in the strictest OCC spec, but CBOE's own feed
/// just uses the bare ticker with no padding), so this parses from the FIXED-WIDTH END of the
/// string backward (15 characters: 6 date + 1 type + 8 strike) rather than assuming where the
/// root ends.</summary>
internal static class OccSymbol
{
    private const int SuffixLength = 15; // YYMMDD(6) + C/P(1) + strike(8)

    public static bool TryParse(string? symbol, out bool isCall, out double strike, out DateTime expirationUtc)
    {
        isCall = false;
        strike = 0;
        expirationUtc = default;

        if (string.IsNullOrEmpty(symbol) || symbol.Length <= SuffixLength) return false;

        var suffix = symbol[^SuffixLength..];
        var dateStr = suffix[..6];
        var typeChar = suffix[6];
        var strikeStr = suffix[7..15];

        if (typeChar != 'C' && typeChar != 'P') return false;
        if (!int.TryParse(dateStr[..2], out var yy) || !int.TryParse(dateStr[2..4], out var mm) || !int.TryParse(dateStr[4..6], out var dd)) return false;
        if (!long.TryParse(strikeStr, NumberStyles.None, CultureInfo.InvariantCulture, out var strikeThousandths)) return false;

        var year = 2000 + yy;
        if (mm < 1 || mm > 12 || dd < 1 || dd > 31) return false;

        isCall = typeChar == 'C';
        strike = strikeThousandths / 1000.0;

        try
        {
            expirationUtc = new DateTime(year, mm, dd, 0, 0, 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return true;
    }
}
