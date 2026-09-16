using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OrbIx.Core.Direction;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>One callout resolved to a real price, ready to paint.</summary>
/// <param name="Price">Last traded price — the line follows price, same as any live level.</param>
/// <param name="Text">Already formatted, e.g. "POSSIBLE HOLD LONG".</param>
/// <param name="IsLong">Which colour applies.</param>
internal readonly record struct DirectionCalloutDraw(double Price, string Text, bool IsLong);

/// <summary>
/// Draws the "POSSIBLE LONG SCALP" / "POSSIBLE HOLD SHORT" callout as one bright, hard-to-miss
/// line at the current price plus its label — the loud rendering of the same verdict the
/// Direction panel already prints quietly as its headline row. What decides scalp vs. hold, and
/// why that split is a stated trading judgement rather than a measured edge, lives entirely in
/// OrbIx.Core.Direction.DirectionCallout; this type only paints whatever it is handed.
/// </summary>
internal sealed class DirectionCalloutOverlay : IDisposable
{
    internal readonly record struct Options(Color LongColor, Color ShortColor, float LineWidth, bool Dashed = false);

    private readonly Font font = new(FontFamily.GenericSansSerif, 11f, FontStyle.Bold);
    private readonly Dictionary<(int Argb, float Width, bool Dashed), Pen> lines = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly SolidBrush labelBack = new(Color.FromArgb(215, 16, 18, 24));
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, DirectionCalloutDraw? callout,
        in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed || callout is not { } draw || string.IsNullOrEmpty(draw.Text))
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        if (!ChartOverlay.TryY(converter, draw.Price, pane.Top, pane.Bottom, out var y)
            || y <= pane.Top || y >= pane.Bottom)
        {
            return;
        }

        var colour = draw.IsLong ? options.LongColor : options.ShortColor;
        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            graphics.DrawLine(this.Line(colour, options.LineWidth, options.Dashed), pane.Left, y, pane.Right, y);

            var size = graphics.MeasureString(draw.Text, this.font);
            var rect = new RectangleF(
                pane.Left + ((pane.Width - size.Width) / 2f), y - size.Height - 6f,
                size.Width + 10f, size.Height + 4f);

            // NOT gated on the result: this line is meant to be the loudest thing on the
            // chart, and letting an earlier, quieter label's claim on these pixels silently
            // suppress it would defeat the entire point. It still registers the rectangle so
            // labels drawn AFTER this one dodge it instead.
            ChartOverlay.TryReserve(labelRegistry, rect);

            graphics.FillRectangle(this.labelBack, rect);
            graphics.DrawString(draw.Text, this.font, this.LabelBrush(colour), rect.Left + 5f, rect.Top + 2f);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private Pen Line(Color colour, float width, bool dashed)
    {
        var key = (colour.ToArgb(), width, dashed);

        if (!this.lines.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, width) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            this.lines[key] = pen;
        }

        return pen;
    }

    private SolidBrush LabelBrush(Color colour)
    {
        var key = colour.ToArgb();

        if (!this.labelBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(colour);
            this.labelBrushes[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();

        foreach (var pen in this.lines.Values)
            pen.Dispose();

        foreach (var brush in this.labelBrushes.Values)
            brush.Dispose();

        this.lines.Clear();
        this.labelBrushes.Clear();
    }
}
