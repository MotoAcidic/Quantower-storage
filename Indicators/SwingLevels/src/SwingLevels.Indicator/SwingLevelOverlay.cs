using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace SwingLevels;

/// <summary>One confirmed higher-timeframe swing high/low, ready to draw. <see cref="Untested"/>
/// drives both line style (solid+bright while live, dim+dotted once price has traded through it)
/// and the label's own "(tested)" suffix — same semantics as `OrbLevels`' own `LevelLineOverlay`,
/// which this is adapted from. <see cref="ZoneHalfHeight"/> &gt; 0 switches the render from a bare
/// line to a filled reaction-zone box (price &#177; half-height) — the operator's own charting habit
/// (screenshot, 2026-10-15): "the box were its were price keeps touching as a support and
/// resistance level so i know when to see the reaction to the box." Same fill+border shape as
/// `LevelsMap`'s own `LevelZoneDraw`/`DrawZone`, reused here rather than re-derived.</summary>
internal readonly record struct SwingLevelDraw(bool IsHigh, double Price, DateTime SinceUtc, bool Untested, Color Color, double ZoneHalfHeight = 0);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record SwingLevelDrawable(SwingLevelDraw[] Lines)
{
    public static readonly SwingLevelDrawable Empty = new(Array.Empty<SwingLevelDraw>());
}

/// <summary>Line renderer adapted from `OrbLevels`' own `LevelLineOverlay` — extends from the bar
/// that confirmed the swing to the pane's right edge while untested. Thickness is configurable
/// (operator's own established preference from the LevelsMap work: clean, adjustable-weight lines,
/// no fixed-size soft fill band).</summary>
internal sealed class SwingLevelOverlay : IDisposable
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> solidPens = new();
    private readonly Dictionary<int, Pen> dimPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly Dictionary<int, SolidBrush> dimLabelBrushes = new();
    private readonly Dictionary<int, SolidBrush> zoneFills = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, SwingLevelDrawable drawable, float lineThickness, List<RectangleF> labelRegistry)
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

                var pen = line.Untested ? this.SolidPen(line.Color, lineThickness) : this.DimPen(line.Color, lineThickness);

                if (line.ZoneHalfHeight > 0
                    && TryY(converter, line.Price + line.ZoneHalfHeight, pane.Top, pane.Bottom, out var topY)
                    && TryY(converter, line.Price - line.ZoneHalfHeight, pane.Top, pane.Bottom, out var botY))
                {
                    var top = Math.Min(topY, botY);
                    var height = Math.Max(Math.Abs(botY - topY), 1f);
                    var zoneRect = new RectangleF(startX, top, Math.Max(1f, pane.Right - startX), height);

                    graphics.FillRectangle(this.ZoneFill(line.Color, line.Untested), zoneRect);
                    graphics.DrawRectangle(pen, zoneRect.X, zoneRect.Y, zoneRect.Width, zoneRect.Height);
                    graphics.DrawLine(pen, startX, y, pane.Right, y);
                }
                else
                {
                    graphics.DrawLine(pen, startX, y, pane.Right, y);
                }

                var kind = line.IsHigh ? "R" : "S";
                var text = line.Untested ? $"{kind} {line.Price:0.####}" : $"{kind} {line.Price:0.####} (tested)";
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

    private Pen SolidPen(Color colour, float thickness)
    {
        var key = (colour.ToArgb() * 32) + (int)(thickness * 10);
        if (!this.solidPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, thickness);
            this.solidPens[key] = pen;
        }
        return pen;
    }

    private Pen DimPen(Color colour, float thickness)
    {
        var key = (colour.ToArgb() * 32) + (int)(thickness * 10);
        if (!this.dimPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(110, colour), Math.Max(1f, thickness - 1f)) { DashStyle = DashStyle.Dot };
            this.dimPens[key] = pen;
        }
        return pen;
    }

    private SolidBrush ZoneFill(Color colour, bool untested)
    {
        var key = (colour.ToArgb() * 2) + (untested ? 1 : 0);
        if (!this.zoneFills.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(untested ? 45 : 22, colour));
            this.zoneFills[key] = brush;
        }
        return brush;
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
        foreach (var b in this.zoneFills.Values) b.Dispose();

        this.solidPens.Clear();
        this.dimPens.Clear();
        this.labelBrushes.Clear();
        this.dimLabelBrushes.Clear();
        this.zoneFills.Clear();
    }
}
