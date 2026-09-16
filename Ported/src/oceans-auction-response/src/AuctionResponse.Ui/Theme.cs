using System.Drawing;

namespace AuctionResponse.Ui;

/// <summary>The Section 3 palette.</summary>
public static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#101A22");
    public static readonly Color Panel = ColorTranslator.FromHtml("#14222D");
    public static readonly Color Grid = ColorTranslator.FromHtml("#293A46");
    public static readonly Color Text = ColorTranslator.FromHtml("#E6EDF3");
    public static readonly Color Muted = ColorTranslator.FromHtml("#93A7B7");
    public static readonly Color Teal = ColorTranslator.FromHtml("#35C9B8");
    public static readonly Color Amber = ColorTranslator.FromHtml("#EAB84D");
    public static readonly Color Coral = ColorTranslator.FromHtml("#F17872");

    public static Color Alpha(Color c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}

/// <summary>
/// Spacing derived from MEASURED text, not from pixel constants.
///
/// The first build advanced rows by hardcoded pixels while the fonts were sized in points;
/// at 96 DPI an 11pt line is about 15px tall, so every row overran the 13px it was given and
/// the panel rendered as overlapping mush. Every vertical step here comes from
/// <see cref="ISurface.Measure"/>, so it stays correct at any DPI and any font substitution.
/// </summary>
public sealed class Metrics
{
    public Metrics(ISurface surface, float scale)
    {
        Scale = scale <= 0f ? 1f : scale;

        // "Hg" spans a capital and a descender, so it measures the true line box.
        LineSmall = surface.Measure("Hg", FontRole.Small).Height;
        LineBody = surface.Measure("Hg", FontRole.Body).Height;
        LineStrong = surface.Measure("Hg", FontRole.Strong).Height;
        LineHeading = surface.Measure("Hg", FontRole.Heading).Height;

        Leading = Math.Max(2, (int)MathF.Round(3f * Scale));
        Margin = Math.Max(6, (int)MathF.Round(10f * Scale));
        SectionGap = Math.Max(6, (int)MathF.Round(10f * Scale));
        Pad = Math.Max(4, (int)MathF.Round(7f * Scale));
    }

    public float Scale { get; }

    /// <summary>Measured line box heights, excluding leading.</summary>
    public int LineSmall { get; }
    public int LineBody { get; }
    public int LineStrong { get; }
    public int LineHeading { get; }

    /// <summary>Vertical space between consecutive rows of the same role.</summary>
    public int Leading { get; }

    /// <summary>Outer margin inside the panel.</summary>
    public int Margin { get; }

    /// <summary>Vertical space between sections.</summary>
    public int SectionGap { get; }

    /// <summary>Internal padding inside a boxed section.</summary>
    public int Pad { get; }

    public int Row(FontRole role) => Line(role) + Leading;

    public int Line(FontRole role) => role switch
    {
        FontRole.Small => LineSmall,
        FontRole.Body => LineBody,
        FontRole.Strong => LineStrong,
        _ => LineHeading
    };

    public int Px(float logical) => Math.Max(1, (int)MathF.Round(logical * Scale));
}
