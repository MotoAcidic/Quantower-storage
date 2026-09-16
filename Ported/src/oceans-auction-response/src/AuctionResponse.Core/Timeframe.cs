namespace AuctionResponse.Core;

/// <summary>
/// Parses a host timeframe string into seconds.
///
/// The baseline builder depends on this: one candle must be exactly one non-overlapping
/// 5-second window, and a misread timeframe would silently change what every sample means.
/// Anything not clearly time-based returns null so the caller refuses rather than assumes.
/// </summary>
public static class Timeframe
{
    public static int? ParseSeconds(string? timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe)) return null;
        var t = timeframe.Trim();

        // Leading-digit forms: "5 Seconds", "5s", "1 Minute", "15Min".
        var digits = new string(t.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length > 0)
        {
            if (!int.TryParse(digits, out var value) || value <= 0) return null;
            var rest = t[digits.Length..].Trim().ToLowerInvariant();
            if (rest.StartsWith("sec") || rest == "s") return value;
            if (rest.StartsWith("min") || rest == "m") return value * 60;
            if (rest.StartsWith("hour") || rest == "h" || rest.StartsWith("hr")) return value * 3600;
            return null;
        }

        // Leading-unit forms: "S5", "M1", "H4".
        var unit = t[..1].ToLowerInvariant();
        var number = new string(t.Skip(1).TakeWhile(char.IsDigit).ToArray());
        if (number.Length == 0 || !int.TryParse(number, out var v) || v <= 0) return null;

        // Only accept a pure unit+number token: "Ticks100" must not read as a timeframe.
        if (t.Length != 1 + number.Length) return null;

        return unit switch { "s" => v, "m" => v * 60, "h" => v * 3600, _ => null };
    }
}
