using System;
using System.Globalization;

namespace OrbIx.Core.Features;

/// <summary>
/// Reads and writes <see cref="Rgb"/> as the hex string a configuration document carries.
///
/// WHY A COLOUR IS IN THE CONFIGURATION AT ALL. Every colour the Aramid Flow tools used was a
/// chart input, and moving twelve tools in that way would have added about twenty-five colour
/// pickers to a dialog that already carries over a hundred rows. The operator's requirement was
/// "without clutter", so the colours moved to the document and the on/off switches stayed on the
/// chart.
///
/// PARSING IS STRICT AND REFUSES RATHER THAN GUESSES. A mistyped colour is reported by name and
/// position, because the alternative — falling back to some default — paints a chart the operator
/// did not ask for while telling them nothing. Alpha is deliberately NOT accepted: the overlays
/// choose their own opacity per shape (a shaded band and its border are drawn from one configured
/// hue), so an alpha in the document would be silently ignored, which is worse than refused.
/// </summary>
public static class RgbHex
{
    /// <summary>
    /// Parses <c>#RRGGBB</c> or <c>RRGGBB</c>.
    ///
    /// The three-digit CSS shorthand is not accepted. It is unambiguous but it is also the form
    /// most easily produced by deleting half a colour by accident, and every colour this project
    /// ships is written in full.
    /// </summary>
    public static bool TryParse(string? text, out Rgb colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan().Trim();

        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6)
            return false;

        if (!byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(span[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        colour = new Rgb(r, g, b);
        return true;
    }

    /// <summary>
    /// Writes <c>#RRGGBB</c>, upper case.
    ///
    /// Channels outside 0..255 are refused rather than clamped. <see cref="Rgb"/> holds ints, so
    /// an out-of-range channel is a bug in whatever built it, and clamping would hide it behind a
    /// colour that renders.
    /// </summary>
    public static string Format(Rgb colour)
    {
        foreach (var (name, channel) in new[] { ("R", colour.R), ("G", colour.G), ("B", colour.B) })
        {
            if (channel is < 0 or > 255)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(colour), channel, $"Channel {name} is outside 0..255 and cannot be written as hex.");
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");
    }
}
