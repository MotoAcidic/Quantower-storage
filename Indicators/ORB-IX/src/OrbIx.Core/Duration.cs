using System;
using System.Globalization;

namespace OrbIx.Core;

/// <summary>
/// Parses the compact duration strings the configuration and the specification use for
/// timeframes and opening-range lengths — "15s", "5m", "1h", "1d".
///
/// This exists rather than <see cref="TimeSpan.Parse(string)"/> because "15m" means
/// fifteen minutes everywhere in this system, while <c>TimeSpan.Parse("15")</c> means
/// fifteen days. A silent factor of 1,440 in a range length is not a class of bug worth
/// leaving reachable.
/// </summary>
public static class Duration
{
    /// <summary>
    /// Attempts to parse a compact duration. Returns false for null, empty, an unknown
    /// unit, a non-numeric magnitude, or a negative value.
    /// </summary>
    public static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var unit = text[^1];
        var magnitudeText = text[..^1];

        if (magnitudeText.Length == 0)
            return false;

        if (!double.TryParse(magnitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude))
            return false;

        if (magnitude < 0 || double.IsNaN(magnitude) || double.IsInfinity(magnitude))
            return false;

        switch (char.ToLowerInvariant(unit))
        {
            case 's': duration = TimeSpan.FromSeconds(magnitude); return true;
            case 'm': duration = TimeSpan.FromMinutes(magnitude); return true;
            case 'h': duration = TimeSpan.FromHours(magnitude); return true;
            case 'd': duration = TimeSpan.FromDays(magnitude); return true;
            default: return false;
        }
    }

    /// <summary>
    /// Parses a compact duration, throwing with the offending text when it cannot.
    /// </summary>
    public static TimeSpan Parse(string value)
        => TryParse(value, out var duration)
            ? duration
            : throw new FormatException(
                $"'{value}' is not a duration. Expected a magnitude and a unit, such as '15s', '5m', '1h' or '1d'.");

    /// <summary>
    /// Renders a duration back to the compact form, choosing the largest unit that keeps
    /// the magnitude whole so a round trip through configuration is stable.
    /// </summary>
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Durations are not negative.");

        var totalSeconds = (long)duration.TotalSeconds;
        if (totalSeconds == 0)
            return "0s";

        if (totalSeconds % 86_400 == 0) return (totalSeconds / 86_400).ToString(CultureInfo.InvariantCulture) + "d";
        if (totalSeconds % 3_600 == 0) return (totalSeconds / 3_600).ToString(CultureInfo.InvariantCulture) + "h";
        if (totalSeconds % 60 == 0) return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture) + "m";
        return totalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
    }
}
