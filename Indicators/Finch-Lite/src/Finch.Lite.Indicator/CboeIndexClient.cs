using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace FinchLite;

/// <summary>One fetched index quote — just the two numbers <see cref="SrmEngine"/> needs.</summary>
internal readonly record struct CboeIndexQuote(double CurrentPrice, double PrevDayClose, DateTime AsOfUtc);

/// <summary>
/// Pulls a free, unauthenticated index level (VIX / VIX3M / VXN) from CBOE's own public CDN —
/// a SEPARATE, much lighter endpoint than the options-chain one `LevelsMap`'s own
/// `CboeOptionsClient` uses (that one returns an entire chain; this one is just the index's own
/// current price and prior close). Confirmed live, 2026-10-12:
/// `https://cdn.cboe.com/api/global/delayed_quotes/quotes/{symbol}.json`, needs the ticker's own
/// leading underscore for an INDEX (CBOE's own convention — "_VIX", "_VIX3M", "_VXN"; a bare
/// "VIX.json" 403s), and the same real browser User-Agent the options endpoint needs (a bare
/// request without one gets a 307 redirect loop, not data).
///
/// Deliberately a SEPARATE small client file rather than extracting a shared one with LevelsMap's
/// `CboeOptionsClient` — different project (no established cross-project `&lt;Compile Include&gt;`
/// in this direction; see `srm.pine` port's own doc comment in `SrmEngine.cs`), and the two fetch
/// genuinely different endpoint shapes. Matches this codebase's "small, independently-readable"
/// convention over a premature shared abstraction.
/// </summary>
internal static class CboeIndexClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static CboeIndexClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
    }

    /// <summary>Fetches one index's current level. <paramref name="ticker"/> should be the bare
    /// symbol WITHOUT the underscore (e.g. "VIX", "VIX3M", "VXN") — the underscore is added here.</summary>
    public static CboeIndexQuote FetchQuote(string ticker)
    {
        var url = $"https://cdn.cboe.com/api/global/delayed_quotes/quotes/_{Uri.EscapeDataString(ticker)}.json";
        var response = Http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var data = root.GetProperty("data");

        var current = data.GetProperty("current_price").GetDouble();
        var prevClose = data.TryGetProperty("prev_day_close", out var pc) && pc.ValueKind == JsonValueKind.Number
            ? pc.GetDouble()
            : current;

        var asOfUtc = DateTime.UtcNow;
        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTs))
        {
            asOfUtc = parsedTs;
        }

        return new CboeIndexQuote(current, prevClose, asOfUtc);
    }
}
