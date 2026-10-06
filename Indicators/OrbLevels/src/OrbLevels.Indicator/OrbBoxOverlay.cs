using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbLevels;

/// <summary>One ORB box to draw — the range itself (a fixed-width rectangle from
/// <see cref="StartUtc"/> to <see cref="EndUtc"/>), plus three rays (high/low/mid) extended from
/// <see cref="EndUtc"/> to the pane's right edge, since those are the levels the strategy actually
/// trades off for the rest of the day.</summary>
internal readonly record struct OrbBoxDraw(DateTime StartUtc, DateTime EndUtc, double High, double Low, double Mid);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record OrbBoxDrawable(OrbBoxDraw[] Boxes)
{
    public static readonly OrbBoxDrawable Empty = new(Array.Empty<OrbBoxDraw>());
}

/// <summary>Box renderer adapted from Finch-Lite's own StructureBoxOverlay — the one difference is
/// a FIXED end time for the box itself (an ORB window is a specific, closed time range, not a
/// "still live" zone), with the high/low/mid levels then extended onward as separate rays once
/// the window closes.</summary>
internal sealed class OrbBoxOverlay : IDisposable
{
    internal readonly record struct Options(Color BoxColor, Color HighColor, Color LowColor, Color MidColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, SolidBrush> fillBrushes = new();
    private readonly Dictionary<int, Pen> borderPens = new();
    private readonly Dictionary<int, Pen> rayPens = new();
    private readonly Dictionary<int, Pen> midPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, OrbBoxDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Boxes.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var box in drawable.Boxes)
            {
                if (!TryY(converter, box.High, pane.Top, pane.Bottom, out var highY)) continue;
                if (!TryY(converter, box.Low, pane.Top, pane.Bottom, out var lowY)) continue;
                if (!TryY(converter, box.Mid, pane.Top, pane.Bottom, out var midY)) continue;

                var startX = (float)pane.Left;
                TryX(converter, box.StartUtc, pane.Left, pane.Right, out startX);
                var endX = (float)pane.Right;
                TryX(converter, box.EndUtc, pane.Left, pane.Right, out endX);

                var top = Math.Min(highY, lowY);
                var height = Math.Max(Math.Abs(lowY - highY), 1f);
                var boxWidth = Math.Max(endX - startX, 1f);
                var boxRect = new RectangleF(startX, top, boxWidth, height);

                graphics.FillRectangle(this.FillBrush(options.BoxColor), boxRect);
                graphics.DrawRectangle(this.BorderPen(options.BoxColor), boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height);

                if (endX < pane.Right)
                {
                    graphics.DrawLine(this.RayPen(options.HighColor), endX, highY, pane.Right, highY);
                    graphics.DrawLine(this.RayPen(options.LowColor), endX, lowY, pane.Right, lowY);
                    graphics.DrawLine(this.MidPen(options.MidColor), endX, midY, pane.Right, midY);

                    this.DrawLabel(graphics, labelRegistry, $"ORB High {box.High:0.####}", pane.Right, highY, options.HighColor);
                    this.DrawLabel(graphics, labelRegistry, $"ORB Low {box.Low:0.####}", pane.Right, lowY, options.LowColor);
                    this.DrawLabel(graphics, labelRegistry, $"ORB Mid {box.Mid:0.####}", pane.Right, midY, options.MidColor);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawLabel(Graphics graphics, List<RectangleF> labelRegistry, string text, float paneRight, float y, Color colour)
    {
        var size = graphics.MeasureString(text, this.font);
        var rect = new RectangleF(paneRight - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

        if (TryReserve(labelRegistry, rect))
        {
            graphics.FillRectangle(this.labelBack, rect);
            graphics.DrawString(text, this.font, this.LabelBrush(colour), rect.Left + 3f, rect.Top);
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

    private SolidBrush FillBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.fillBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(40, colour));
            this.fillBrushes[key] = brush;
        }
        return brush;
    }

    private Pen BorderPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.borderPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(180, colour), 1f);
            this.borderPens[key] = pen;
        }
        return pen;
    }

    private Pen RayPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.rayPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, 1.25f);
            this.rayPens[key] = pen;
        }
        return pen;
    }

    private Pen MidPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.midPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, 1f) { DashStyle = DashStyle.Dash };
            this.midPens[key] = pen;
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
        foreach (var p in this.borderPens.Values) p.Dispose();
        foreach (var p in this.rayPens.Values) p.Dispose();
        foreach (var p in this.midPens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();

        this.fillBrushes.Clear();
        this.borderPens.Clear();
        this.rayPens.Clear();
        this.midPens.Clear();
        this.labelBrushes.Clear();
    }
}
