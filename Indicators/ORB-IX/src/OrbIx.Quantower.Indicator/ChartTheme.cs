using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OrbIx.Core.Features;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// How the overlay draws: the §10 palette, the typeface, and a colour per trading session.
///
/// The palette values are converted from the specification's oklch tokens to sRGB rather than
/// eyeballed — the design states its colours in oklch and <see cref="Color"/> takes sRGB, so
/// each value here is the arithmetic result of that conversion. Approximating them by eye would
/// drift the whole chart a shade at a time.
///
/// This lives apart from any renderer so that deleting one does not take the chart's appearance
/// with it, which is exactly what happened when the palette lived inside the panel.
/// </summary>
internal static class ChartTheme
{
    /// <summary>
    /// A configured colour as GDI+ wants it.
    ///
    /// THE DOCUMENT CARRIES sRGB BYTES, NOT A PLATFORM TYPE, because OrbIx.Core has no reference
    /// to System.Drawing by design — that is what lets the palette and the configuration be tested
    /// on a machine with no chart. This is the one place the two meet, so a colour cannot be
    /// converted two different ways in two different overlays.
    /// </summary>
    public static Color From(Rgb colour)
        => Color.FromArgb(colour.R, colour.G, colour.B);

    /// <summary>The same colour at a chosen opacity, for a band under its own line.</summary>
    public static Color From(Rgb colour, int alpha)
        => Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    public static readonly Color Ground = Color.FromArgb(7, 8, 11);            // oklch(0.135 0.008 260)
    public static readonly Color Panel = Color.FromArgb(17, 20, 24);           // oklch(0.19 0.01 260)
    public static readonly Color PanelRaised = Color.FromArgb(33, 36, 42);     // oklch(0.26 0.012 260)
    public static readonly Color Hairline = Color.FromArgb(42, 46, 52);        // oklch(0.30 0.012 260)
    public static readonly Color Accent = Color.FromArgb(73, 211, 161);        // oklch(0.78 0.14 165)
    public static readonly Color AccentDim = Color.FromArgb(33, 69, 55);       // oklch(0.36 0.05 165)
    public static readonly Color Alert = Color.FromArgb(242, 128, 88);         // oklch(0.72 0.15 40)
    public static readonly Color AlertDim = Color.FromArgb(84, 52, 41);        // oklch(0.36 0.05 40)
    public static readonly Color TextPrimary = Color.FromArgb(229, 232, 236);  // oklch(0.93 0.006 260)
    public static readonly Color TextSecondary = Color.FromArgb(187, 190, 195);// oklch(0.80 0.008 260)
    public static readonly Color TextMuted = Color.FromArgb(131, 134, 139);    // oklch(0.62 0.008 260)

    /// <summary>
    /// The design's typeface, with a fallback chain.
    ///
    /// IBM Plex Mono is what the specification uses and may not be installed on a trading host,
    /// so the chain ends at a face Windows always has. Every candidate is monospaced: falling
    /// back silently to a proportional font would misalign every price the chart writes.
    /// </summary>
    public static readonly string[] MonoFamilies =
    {
        "IBM Plex Mono", "Cascadia Mono", "Consolas", "Courier New",
    };

    /// <summary>
    /// Picks the first installed family from the design's chain.
    ///
    /// Checked against the installed families rather than assumed: constructing a
    /// <see cref="Font"/> with an absent family silently substitutes one, and the substitute is
    /// usually proportional — which would misalign every numeric label while looking like it
    /// worked.
    /// </summary>
    public static string ResolveMonoFamily()
    {
        var installed = FontFamily.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in MonoFamilies)
        {
            if (installed.Contains(candidate))
                return candidate;
        }

        return FontFamily.GenericMonospace.Name;
    }

    /// <summary>
    /// The colour for a session.
    ///
    /// The RULE lives in OrbIx.Core's SessionPalette so it can be tested — the indicator targets
    /// net10.0-windows and the suite runs on Linux. This only converts the result to the GDI
    /// type the chart draws with, and holds no second copy of the mapping that could drift from
    /// the one under test.
    /// </summary>
    public static Color ForSession(string sessionName)
    {
        var hue = SessionPalette.ForSession(sessionName);

        return Color.FromArgb(hue.R, hue.G, hue.B);
    }


}
