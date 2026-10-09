using System;
using System.Collections.Generic;
using System.Globalization;

namespace LevelsMap;

/// <summary>One parsed entry from a pasted Levels Code string.</summary>
/// <param name="Kind">ZG · VS · CW · PW · MVC · DZ (dark pool zone) · DL (dark pool line).</param>
/// <param name="IsChart">True if the entry was prefixed with "@" — already in the chart's own
/// price, skip the QQQ/SPY ratio for this ONE entry even if the global unit setting isn't
/// Chart.</param>
internal readonly record struct ParsedLevel(string Kind, double Lo, double Hi, double Notional, bool IsChart);

/// <summary>
/// Ported from priceLevels.pine's own `parseCode`/`kindOf` — the operator's own ask (2026-10-08):
/// "is there a way to auto fill these levels instead of me doing them one by one." Same format the
/// source script accepts, so a friend's existing paste (confirmed working example: "DP ZONE
/// 759.53-759.53 1.80B | DP ZONE 756.30-757.10 1.20B | ZERO GAMMA 755.05 | VOL SKEW 754.00 |
/// CALL WALL 760.00 | MVC 760.00") drops in unchanged — no new format to learn.
///
/// Entries split on | ; , or newlines. Each entry: a name (one or more words), a price or a
/// low-high range, and an optional trailing "N B" notional. A lo==hi "DP ZONE" collapses to a
/// dark pool LINE (kind "DL") rather than a zero-width zone — same as the source script.
/// </summary>
internal static class LevelsCodeParser
{
    public static List<ParsedLevel> Parse(string? raw)
    {
        var result = new List<ParsedLevel>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var s = raw.ToUpperInvariant();

        // Anything before a "]" is dropped, so a line copied with its own timestamp still reads.
        var cut = s.IndexOf(']');
        if (cut >= 0) s = s[(cut + 1)..];

        s = s.Replace('\n', ';').Replace('|', ';').Replace(',', ';').Replace(':', ' ').Replace('=', ' ').Replace("$", "");

        foreach (var entry in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = "";
            double? lo = null;
            double? hi = null;
            var notional = 0.0;
            var isChart = false;
            var rangePending = false;

            foreach (var raw0 in entry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw0;
                if (t.StartsWith('@'))
                {
                    isChart = true;
                    t = t[1..];
                }

                if (t.Length == 0) continue;

                var isNumber = double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);

                double? bNum = null;
                if (t.EndsWith('B') && t.Length > 1 && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bv))
                    bNum = bv;

                if (bNum is { } nb)
                {
                    notional = nb;
                }
                else if (t == "-" || t == "TO")
                {
                    rangePending = true;
                }
                else if (!isNumber && t.Contains('-'))
                {
                    var parts = t.Split('-');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pa)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pb))
                    {
                        lo = pa;
                        hi = pb;
                    }
                }
                else if (isNumber)
                {
                    if (lo is null)
                    {
                        lo = v;
                        hi = v;
                    }
                    else if (rangePending)
                    {
                        hi = v;
                        rangePending = false;
                    }
                    else
                    {
                        notional = v;
                    }
                }
                else
                {
                    key = key.Length == 0 ? t : key + " " + t;
                }
            }

            var kind = KindOf(key);
            if (kind.Length == 0 || lo is not { } loV || hi is not { } hiV || loV <= 0 || hiV <= 0) continue;

            var resolvedKind = kind == "DP" ? (Math.Abs(hiV - loV) > double.Epsilon ? "DZ" : "DL") : kind;
            result.Add(new ParsedLevel(resolvedKind, Math.Min(loV, hiV), Math.Max(loV, hiV), notional, isChart));
        }

        return result;
    }

    private static string KindOf(string key)
    {
        if (key.StartsWith("ZG") || key.Contains("ZERO") || key.Contains("FLIP")) return "ZG";
        if (key.StartsWith("VS") || key.Contains("SKEW")) return "VS";
        if (key.StartsWith("CW") || key.Contains("CALL")) return "CW";
        if (key.StartsWith("PW") || key.Contains("PUT")) return "PW";
        if (key.Contains("MVC")) return "MVC";
        if (key.StartsWith("DP") || key.Contains("DARK")) return "DP";
        return "";
    }
}
