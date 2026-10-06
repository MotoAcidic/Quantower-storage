using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbLevels;

/// <summary>One Asia/London/NY high or low level, ready to draw. <see cref="Untested"/> drives
/// both line style (solid+bright while live to watch, dim+dashed once consumed) and the label's
/// own "(tested)" suffix — matches mesOrbStrategy's own "a touch marks it tested, period"
/// semantics exactly, so what this indicator shows is what the strategy is actually watching.</summary>
internal readonly record struct LevelLineDraw(string Name, double Price, DateTime SinceUtc, bool Untested, Color Color);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record LevelLineDrawable(LevelLineDraw[] Lines)
{
    public static readonly LevelLineDrawable Empty = new(Array.Empty<LevelLineDraw>());
}

/// <summary>Line renderer adapted from Finch-Lite's own PocOverlay — extends from the moment the
/// level froze to the pane's right edge, same "still live" convention as that overlay's own POC
/// lines.</summary>
internal sealed class LevelLineOverlay : IDisposable
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> solidPens = new();
    private readonly Dictionary<int, Pen> dimPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly Dictionary<int, SolidBrush> dimLabelBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, LevelLineDrawable drawable, List<RectangleF> labelRegistry)
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

                var startX = (float)pane.Left;
                TryX(converter, line.SinceUtc, pane.Left, pane.Right, out startX);

                var pen = line.Untested ? this.SolidPen(line.Color) : this.DimPen(line.Color);
                graphics.DrawLine(pen, startX, y, pane.Right, y);

                var text = line.Untested ? $"{line.Name} {line.Price:0.####}" : $"{line.Name} {line.Price:0.####} (tested)";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    var brush = line.Untested ? this.LabelBrush(line.Color) : this.DimLabelBrush(line.Color);
                    graphics.DrawString(text, this.font, brush, rect.Left + 3f, rect.Top);
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

    private static bool TryX(
        IChartWindowCoordinatesConverter converter, DateTime utc, float left, float right, out float x)
    {
        x = left;
        var raw = converter.GetChartX(utc);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
        x = (float)Math.Clamp(raw, left, right);
        return true;
    }

    private static bool TryReserve(List<RectangleF> registry, RectangleF rect)
    {
        foreach (var placed in registry)
            if (placed.IntersectsWith(rect)) return false;

        registry.Add(rect);
        return true;
    }

    private Pen SolidPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.solidPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, 1.5f);
            this.solidPens[key] = pen;
        }
        return pen;
    }

    private Pen DimPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.dimPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(110, colour), 1f) { DashStyle = DashStyle.Dot };
            this.dimPens[key] = pen;
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

    private SolidBrush DimLabelBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.dimLabelBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(160, colour));
            this.dimLabelBrushes[key] = brush;
        }
        return brush;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;

        this.font.Dispose();
        this.labelBack.Dispose();
        foreach (var p in this.solidPens.Values) p.Dispose();
        foreach (var p in this.dimPens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();
        foreach (var b in this.dimLabelBrushes.Values) b.Dispose();

        this.solidPens.Clear();
        this.dimPens.Clear();
        this.labelBrushes.Clear();
        this.dimLabelBrushes.Clear();
    }
}
