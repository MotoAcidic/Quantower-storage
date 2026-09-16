using System.Drawing;

namespace AuctionResponse.Ui;

/// <summary>Text fitting. Nothing is allowed to run past the width it was given.</summary>
public static class TextUtil
{
    public const string Ellipsis = "…";

    /// <summary>
    /// Truncates to fit <paramref name="maxWidth"/>, appending an ellipsis. Returns an empty
    /// string when not even one character fits, so a caller can skip the row entirely rather
    /// than emit an unreadable stub.
    /// </summary>
    public static string Fit(ISurface surface, string text, FontRole role, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return "";
        if (surface.Measure(text, role).Width <= maxWidth) return text;

        var ellipsisWidth = surface.Measure(Ellipsis, role).Width;
        if (ellipsisWidth > maxWidth) return "";

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (surface.Measure(text[..mid], role).Width + ellipsisWidth <= maxWidth) low = mid;
            else high = mid - 1;
        }

        return low == 0 ? "" : text[..low].TrimEnd() + Ellipsis;
    }

    /// <summary>Wraps at word boundaries against a measured width.</summary>
    public static List<string> Wrap(ISurface surface, string text, FontRole role, int maxWidth, int maxLines = 6)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0) return lines;

        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (surface.Measure(candidate, role).Width <= maxWidth) { line = candidate; continue; }

            if (line.Length > 0) lines.Add(line);
            if (lines.Count == maxLines) return Truncate(surface, lines, role, maxWidth);

            // A single word wider than the column is clipped rather than allowed to overrun.
            line = surface.Measure(word, role).Width <= maxWidth ? word : Fit(surface, word, role, maxWidth);
        }

        if (line.Length > 0) lines.Add(line);
        if (lines.Count > maxLines) return Truncate(surface, lines, role, maxWidth);
        return lines;
    }

    private static List<string> Truncate(ISurface surface, List<string> lines, FontRole role, int maxWidth)
    {
        var kept = lines.Take(Math.Max(lines.Count - 1, 1)).ToList();
        if (kept.Count > 0)
            kept[^1] = Fit(surface, kept[^1] + " " + Ellipsis, role, maxWidth);
        return kept;
    }

    /// <summary>Draws text right-aligned to <paramref name="right"/>, clipped to the column.</summary>
    public static void TextRight(ISurface surface, string text, FontRole role, Color color, int right, int y, int columnWidth)
    {
        var fitted = Fit(surface, text, role, columnWidth);
        if (fitted.Length == 0) return;
        var w = surface.Measure(fitted, role).Width;
        surface.Text(fitted, role, color, right - w, y);
    }

    /// <summary>Draws text left-aligned at <paramref name="x"/>, clipped to the column.</summary>
    public static void TextLeft(ISurface surface, string text, FontRole role, Color color, int x, int y, int columnWidth)
    {
        var fitted = Fit(surface, text, role, columnWidth);
        if (fitted.Length > 0) surface.Text(fitted, role, color, x, y);
    }
}

/// <summary>
/// A vertical writing cursor bounded by its section. It refuses to draw past the bottom, so
/// a section can never bleed into the one beneath it no matter how small the panel gets.
/// </summary>
public struct Cursor
{
    public Cursor(int x, int y, int width, int bottom)
    {
        X = x; Y = y; Width = width; Bottom = bottom; Truncated = false;
    }

    public int X { get; }
    public int Y { get; private set; }
    public int Width { get; }
    public int Bottom { get; }
    public bool Truncated { get; private set; }

    public int Right => X + Width;
    public int Remaining => Bottom - Y;

    public bool Fits(int height) => Y + height <= Bottom;

    /// <summary>Reserves vertical space, or reports that it does not fit and marks truncation.</summary>
    public bool TryTake(int height, out int y)
    {
        if (!Fits(height)) { Truncated = true; y = Y; return false; }
        y = Y;
        Y += height;
        return true;
    }

    public void Skip(int height) => Y = Math.Min(Y + height, Bottom);
}
