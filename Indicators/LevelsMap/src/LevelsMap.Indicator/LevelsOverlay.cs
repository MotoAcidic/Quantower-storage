using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace LevelsMap;

/// <summary>One single-price level (an option level or a dark pool line) — a soft band + a solid
/// center line + a right-edge label, same visual shape as the source script's own
/// `makeBandLine`.</summary>
internal readonly record struct LevelLineDraw(double Price, double HalfWidth, Color Color, string Label);

/// <summary>One dark pool zone (a low-high range) — a filled box + a label, same shape as the
/// source script's own `makePool`.</summary>
internal readonly record struct LevelZoneDraw(double Low, double High, Color Color, string Label);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record LevelsDrawable(LevelLineDraw[] Lines, LevelZoneDraw[] Zones)
{
    public static readonly LevelsDrawable Empty = new(Array.Empty<LevelLineDraw>(), Array.Empty<LevelZoneDraw>());
}

/// <summary>Draws every line and zone full-width across the pane — simpler than the source
/// script's own "span % of visible bars" layout control, since a manually-typed reference level
/// is relevant for the whole visible chart, not just a slice of it.</summary>
internal sealed class LevelsOverlay : IDisposable
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, SolidBrush> fillBrushes = new();
    private readonly Dictionary<int, Pen> linePens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, LevelsDrawable drawable, List<RectangleF> labelRegistry)
    {
        if (this.disposed) return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        if (converter is null || pane.Width <= 0f || pane.Height <= 0f) return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var zone in drawable.Zones)
                this.DrawZone(graphics, converter, pane, zone, labelRegistry);

            foreach (var line in drawable.Lines)
                this.DrawLine(graphics, converter, pane, line, labelRegistry);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawZone(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, LevelZoneDraw zone, List<RectangleF> labelRegistry)
    {
        if (!TryY(converter, zone.High, pane.Top, pane.Bottom, out var topY)) return;
        if (!TryY(converter, zone.Low, pane.Top, pane.Bottom, out var botY)) return;

        var top = Math.Min(topY, botY);
        var height = Math.Max(Math.Abs(botY - topY), 1f);
        var rect = new RectangleF(pane.Left, top, pane.Width, height);

        graphics.FillRectangle(this.FillBrush(zone.Color), rect);
        graphics.DrawRectangle(this.LinePen(zone.Color, 1f), rect.X, rect.Y, rect.Width, rect.Height);

        this.DrawLabel(graphics, pane, (topY + botY) / 2f, zone.Label, zone.Color, labelRegistry);
    }

    private void DrawLine(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, LevelLineDraw line, List<RectangleF> labelRegistry)
    {
        if (!TryY(converter, line.Price, pane.Top, pane.Bottom, out var y)) return;

        if (line.HalfWidth > 0)
        {
            TryY(converter, line.Price + line.HalfWidth, pane.Top, pane.Bottom, out var topY);
            TryY(converter, line.Price - line.HalfWidth, pane.Top, pane.Bottom, out var botY);
            var top = Math.Min(topY, botY);
            var height = Math.Max(Math.Abs(botY - topY), 1f);
            graphics.FillRectangle(this.FillBrush(line.Color), new RectangleF(pane.Left, top, pane.Width, height));
        }

        graphics.DrawLine(this.LinePen(line.Color, 1.5f), pane.Left, y, pane.Right, y);
        this.DrawLabel(graphics, pane, y, line.Label, line.Color, labelRegistry);
    }

    private void DrawLabel(Graphics graphics, RectangleF pane, float y, string text, Color color, List<RectangleF> labelRegistry)
    {
        var size = graphics.MeasureString(text, this.font);
        var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

        if (!TryReserve(labelRegistry, rect)) return;

        graphics.FillRectangle(this.labelBack, rect);
        graphics.DrawString(text, this.font, this.LabelBrush(color), rect.Left + 3f, rect.Top);
    }

    private static bool TryY(IChartWindowCoordinatesConverter converter, double price, float top, float bottom, out float y)
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

    private SolidBrush FillBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.fillBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(45, colour));
            this.fillBrushes[key] = brush;
        }
        return brush;
    }

    private Pen LinePen(Color colour, float width)
    {
        var key = (colour.ToArgb() * 31) + (int)(width * 10);
        if (!this.linePens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(180, colour), width);
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
        foreach (var b in this.fillBrushes.Values) b.Dispose();
        foreach (var p in this.linePens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();

        this.fillBrushes.Clear();
        this.linePens.Clear();
        this.labelBrushes.Clear();
    }
}
