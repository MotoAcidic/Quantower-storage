using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace SwingLevels;

/// <summary>One SRM Vol-Step line (S1-S3, R1-R3, or the mid) — full pane width, same shape as
/// `Finch-Lite`'s own `FullWidth: true` `SrmLineDraw` usage for this exact feature: these levels
/// aren't anchored to a confirmation bar like swing/session/wickless levels, they're a live
/// reference frame that only moves when price travels a full step away.</summary>
internal readonly record struct SrmVolStepLineDraw(double Price, Color Color, string Label);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record SrmVolStepDrawable(SrmVolStepLineDraw[] Lines)
{
    public static readonly SrmVolStepDrawable Empty = new(Array.Empty<SrmVolStepLineDraw>());
}

/// <summary>Full-pane-width line renderer, adapted from `LevelsMap`'s own `LevelsOverlay.DrawLine`
/// (minus its optional soft-band fill — a vol-step level is a single reference price, not a zone).</summary>
internal sealed class SrmVolStepOverlay : IDisposable
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> linePens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, SrmVolStepDrawable drawable, float lineThickness, List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Lines.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var line in drawable.Lines)
            {
                if (!TryY(converter, line.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                graphics.DrawLine(this.LinePen(line.Color, lineThickness), pane.Left, y, pane.Right, y);

                var text = $"{line.Label}  {line.Price:0.####}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    graphics.DrawString(text, this.font, this.LabelBrush(line.Color), rect.Left + 3f, rect.Top);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private static bool TryY(
        IChartWindowCoordinatesConverter converter, double price, float top, float bottom, out float y)
    {
        y = 0f;
        if (double.IsNaN(price) || double.IsInfinity(price)) return false;
        var raw = converter.GetChartY(price);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
        y = (float)Math.Clamp(raw, top, bottom);
        return true;
    }

    private static bool TryReserve(List<RectangleF> registry, RectangleF rect)
    {
        foreach (var placed in registry)
            if (placed.IntersectsWith(rect)) return false;

        registry.Add(rect);
        return true;
    }

    private Pen LinePen(Color colour, float width)
    {
        var key = (colour.ToArgb() * 31) + (int)(width * 10);
        if (!this.linePens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(190, colour), width);
            this.linePens[key] = pen;
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
        if (this.disposed) return;
        this.disposed = true;

        this.font.Dispose();
        this.labelBack.Dispose();
        foreach (var p in this.linePens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();

        this.linePens.Clear();
        this.labelBrushes.Clear();
    }
}
