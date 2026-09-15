using System;
using System.Collections.Generic;
using System.Drawing;
using OrbIx.Core.Direction;

namespace OrbIx.Quantower.Shared;

/// <summary>
/// Paints the direction panel: a verdict, then the components it came from.
/// </summary>
/// <remarks>
/// COMPILED INTO BOTH INDICATORS BY SOURCE, NEVER SHARED AS A DLL.
///     Quantower loads scripts with <c>Assembly.Load(bytes)</c> and caches them by
///     assembly FullName, so one shared DLL under two script folders has a single
///     cache key and whichever copy loads first serves the whole process. That has
///     already happened in this project once and the chart went blank. Both the
///     standalone indicator and ORB-IX therefore compile this file in, producing
///     two distinct assembly names and nothing shared to collide.
///
/// IT IS SELF-CONTAINED ON PURPOSE.
///     It owns its own font and brushes and depends on no other overlay type, so
///     that adding it to a second project is a one-line csproj change rather than a
///     refactor of ORB-IX's whole render layer.
///
/// NOTHING HERE PAINTS A WARNING, A WATERMARK, OR A DISCLAIMER. The operator has
/// asked for that explicitly and repeatedly; the panel reports what it measured
/// and nothing else.
/// </remarks>
public sealed class DirectionOverlay : IDisposable
{
    private readonly Font headlineFont = new(FontFamily.GenericSansSerif, 11f, FontStyle.Bold);
    private readonly Font rowFont = new(FontFamily.GenericSansSerif, 8.5f, FontStyle.Regular);

    private readonly SolidBrush background = new(Color.FromArgb(200, 18, 20, 26));
    private readonly SolidBrush label = new(Color.FromArgb(150, 155, 165));

    // Up, down, and the two that are NOT a direction. Undecided and unavailable
    // share the muted colour because neither is a claim about the market; what
    // separates them is the text, which says "—" or names why it could not look.
    private readonly SolidBrush up = new(Color.FromArgb(90, 210, 140));
    private readonly SolidBrush down = new(Color.FromArgb(235, 90, 90));
    private readonly SolidBrush muted = new(Color.FromArgb(170, 170, 175));

    private bool disposed;

    /// <summary>
    /// Draws the panel at the given corner.
    /// </summary>
    /// <param name="graphics">Target surface.</param>
    /// <param name="area">The pane's rectangle.</param>
    /// <param name="content">What to show. A null content draws nothing at all.</param>
    /// <param name="offsetX">Left inset, in pixels.</param>
    /// <param name="offsetY">Top inset, in pixels.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="graphics"/> is null.</exception>
    public void Draw(
        Graphics graphics, Rectangle area, DirectionPanelContent? content,
        int offsetX, int offsetY)
    {
        ArgumentNullException.ThrowIfNull(graphics);

        if (content is null)
            return;

        float rowHeight = this.rowFont.Height + 2f;
        float headlineHeight = this.headlineFont.Height + 4f;

        // Measured before anything is drawn, so the background is sized to the
        // real text rather than to a guess that clips the longest row.
        float width = graphics.MeasureString(content.Headline, this.headlineFont).Width;

        foreach (DirectionRow row in content.Rows)
        {
            float w = graphics.MeasureString(row.Label, this.rowFont).Width
                    + graphics.MeasureString(row.Value, this.rowFont).Width
                    + LabelColumn;

            if (w > width)
                width = w;
        }

        float height = headlineHeight + (content.Rows.Count * rowHeight) + (Padding * 2);
        var rect = new RectangleF(
            area.Left + offsetX, area.Top + offsetY, width + (Padding * 2), height);

        // A NaN or infinite rectangle poisons GDI+ for the whole frame, taking
        // every other overlay down with it, so it is checked before it is used.
        if (!IsDrawable(rect))
            return;

        graphics.FillRectangle(this.background, rect);

        float x = rect.Left + Padding;
        float y = rect.Top + Padding;

        graphics.DrawString(
            content.Headline, this.headlineFont, this.VerdictBrush(content.Verdict), x, y);

        y += headlineHeight;

        foreach (DirectionRow row in content.Rows)
        {
            graphics.DrawString(row.Label, this.rowFont, this.label, x, y);
            graphics.DrawString(row.Value, this.rowFont, this.StateBrush(row.State),
                                x + LabelColumn, y);
            y += rowHeight;
        }
    }

    /// <summary>Frees the fonts and brushes. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        this.headlineFont.Dispose();
        this.rowFont.Dispose();
        this.background.Dispose();
        this.label.Dispose();
        this.up.Dispose();
        this.down.Dispose();
        this.muted.Dispose();
    }

    private const float Padding = 6f;

    /// <summary>Left column width, so the values line up under each other.</summary>
    private const float LabelColumn = 62f;

    private Brush VerdictBrush(DirectionVerdict verdict) => verdict switch
    {
        DirectionVerdict.Up => this.up,
        DirectionVerdict.Down => this.down,

        // Mixed and Undecided are both muted: neither is a direction, and giving
        // either one a side's colour would read as a call the count did not make.
        _ => this.muted,
    };

    private Brush StateBrush(DirectionState state) => state switch
    {
        DirectionState.Up => this.up,
        DirectionState.Down => this.down,
        _ => this.muted,
    };

    private static bool IsDrawable(RectangleF rect) =>
        float.IsFinite(rect.X) && float.IsFinite(rect.Y)
        && float.IsFinite(rect.Width) && float.IsFinite(rect.Height)
        && rect.Width > 0 && rect.Height > 0;
}
