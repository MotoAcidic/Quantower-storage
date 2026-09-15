using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>A colour, as sRGB bytes. Deliberately not System.Drawing — Core has no graphics.</summary>
public readonly record struct Rgb(int R, int G, int B);

/// <summary>
/// How a session's range is drawn: its colour, and whether it is dashed.
/// </summary>
/// <param name="Hue">The session's colour.</param>
/// <param name="Dashed">
/// True for a CONTEXT window — the initial balance and anything else configured with entries
/// disallowed.
/// </param>
public readonly record struct SessionStyle(Rgb Hue, bool Dashed);

/// <summary>
/// Which colour each trading session is drawn in, and how an initial balance is told apart from
/// an opening range.
///
/// THE RULE LIVES IN CORE SO IT CAN BE TESTED. The indicator targets net10.0-windows and the
/// suite runs on Linux, so a palette living in the platform shell is one nobody can assert
/// against — and the two properties that matter here are exactly the kind that break silently.
///
/// KEYED BY NAME, NEVER BY POSITION. An index into a list would re-colour every session below
/// one that was disabled in configuration, so a chart the operator had learned to read would
/// change meaning after an unrelated edit.
///
/// COLOUR IS NOT THE ONLY CHANNEL. An opening range draws solid and an initial balance dashed,
/// and both carry the session's name as text. Evenly-spaced hues are not automatically
/// distinguishable under a colour-vision deficiency, and a chart that leaned on hue alone would
/// be unreadable for some readers and for every greyscale screenshot.
/// </summary>
public static class SessionPalette
{
    /// <summary>The sessions the palette names, in hue order.</summary>
    public static readonly IReadOnlyList<string> Sessions = new[]
    {
        "GLOBEX", "ASIA", "FRANKFURT", "LONDON", "NYPRE", "COMEX", "RTH", "IB",
    };

    /// <summary>Saturation and lightness chosen to read against a near-black chart ground.</summary>
    private const double Saturation = 0.62;

    private const double Lightness = 0.62;

    private static readonly Dictionary<string, Rgb> Hues = Build();

    private static Dictionary<string, Rgb> Build()
    {
        var map = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Sessions.Count; i++)
            map[Sessions[i]] = FromHsl(360.0 * i / Sessions.Count, Saturation, Lightness);

        return map;
    }

    /// <summary>
    /// A session's colour.
    ///
    /// A session the table does not know — a custom window the operator added — gets a neutral
    /// grey rather than one of the eight, so it reads as "not one of the standard sessions"
    /// instead of impersonating whichever one a hash happened to land on.
    /// </summary>
    public static Rgb ForSession(string sessionName)
        => !string.IsNullOrWhiteSpace(sessionName) && Hues.TryGetValue(sessionName, out var hue)
            ? hue
            : Neutral;

    /// <summary>The colour for a session the palette does not name.</summary>
    public static Rgb Neutral { get; } = new(187, 190, 195);

    /// <summary>Colour and line style together.</summary>
    public static SessionStyle StyleFor(string sessionName, bool isContextWindow)
        => new(ForSession(sessionName), isContextWindow);

    /// <summary>
    /// HSL to sRGB.
    ///
    /// Computed rather than hand-picked so the hues really are evenly spaced. A hand-picked set
    /// drifts, and "these look different enough" is not something a test can check.
    /// </summary>
    public static Rgb FromHsl(double hueDegrees, double saturation, double lightness)
    {
        var h = ((hueDegrees % 360d) + 360d) % 360d / 360d;
        var s = Math.Clamp(saturation, 0d, 1d);
        var l = Math.Clamp(lightness, 0d, 1d);

        if (s == 0d)
        {
            var grey = (int)Math.Round(l * 255d);
            return new Rgb(grey, grey, grey);
        }

        var q = l < 0.5d ? l * (1d + s) : l + s - (l * s);
        var p = (2d * l) - q;

        static double Channel(double p, double q, double t)
        {
            if (t < 0d) t += 1d;
            if (t > 1d) t -= 1d;

            if (t < 1d / 6d) return p + ((q - p) * 6d * t);
            if (t < 1d / 2d) return q;
            if (t < 2d / 3d) return p + ((q - p) * ((2d / 3d) - t) * 6d);

            return p;
        }

        return new Rgb(
            (int)Math.Round(Channel(p, q, h + (1d / 3d)) * 255d),
            (int)Math.Round(Channel(p, q, h) * 255d),
            (int)Math.Round(Channel(p, q, h - (1d / 3d)) * 255d));
    }
}
