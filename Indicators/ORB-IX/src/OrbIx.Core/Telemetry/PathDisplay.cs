using System;
using System.Collections.Generic;

namespace OrbIx.Core.Telemetry;

/// <summary>
/// Renders file paths safe to show on screen.
///
/// The panel is screenshotted and shared. A Windows path under a user profile contains the
/// account name, so any message quoting one publishes it to whoever sees the picture — and
/// the messages that quote paths are exactly the ones an operator screenshots, because they
/// are the ones reporting a problem.
///
/// Every user-specific prefix is replaced with the environment variable that produced it, so
/// the path stays actionable — it can be pasted into a file dialog or a shell and still
/// resolve — while naming nobody.
///
/// Applied at the display boundary rather than at each call site: a redaction that has to be
/// remembered eighteen times is a redaction that will be forgotten once.
/// </summary>
public static class PathDisplay
{
    /// <summary>
    /// Replaces user-specific path prefixes with their environment-variable names.
    ///
    /// Longest prefix first, so the local application data folder is not half-replaced by the
    /// user profile that contains it.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = text;

        foreach (var (value, token) in Replacements())
        {
            if (value.Length == 0)
                continue;

            result = result.Replace(value, token, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// The substitutions, longest value first.
    ///
    /// The bare user name is included last as a backstop: a path assembled by something other
    /// than these folder APIs still contains it, and the whole point is that the name never
    /// reaches the screen. It is only substituted when it is long enough not to collide with
    /// ordinary words — a two-character account name appearing inside "product" would corrupt
    /// every message, which is a worse outcome than the one being prevented.
    /// </summary>
    private static IEnumerable<(string Value, string Token)> Replacements()
    {
        var candidates = new List<(string Value, string Token)>
        {
            (Safe(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Safe(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Safe(Environment.SpecialFolder.MyDocuments), "%USERPROFILE%\\Documents"),
            (Safe(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        };

        candidates.Sort(static (a, b) => b.Value.Length.CompareTo(a.Value.Length));

        foreach (var candidate in candidates)
            yield return candidate;

        var userName = Environment.UserName;

        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 4)
            yield return (userName, "%USERNAME%");
    }

    /// <summary>
    /// Reads a special folder, treating a failure as "no such prefix to redact".
    ///
    /// A folder the platform cannot resolve produces an empty string, which is skipped. It
    /// never produces a partial path, which would replace the wrong substring.
    /// </summary>
    private static string Safe(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch (PlatformNotSupportedException)
        {
            return string.Empty;
        }
    }
}
