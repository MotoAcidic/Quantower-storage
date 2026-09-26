using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One point of control ready to draw.</summary>
/// <param name="Price">The price this move's volume-by-price accumulator says traded the most.</param>
/// <param name="MoveStartUtc">When the current move started — the line draws from here to "now",
/// same "still extending" convention already used for large-order/unfinished-auction lines,
/// rather than spanning the whole pane.</param>
/// <param name="IsHigherTimeframe">False for the chart-timeframe "current move" POC, true for the
/// 15-minute higher-timeframe one — drives both colour and label text.</param>
internal readonly record struct PocDraw(double Price, DateTime MoveStartUtc, bool IsHigherTimeframe);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record PocDrawable(PocDraw[] Points)
{
    public static readonly PocDrawable Empty = new(Array.Empty<PocDraw>());
}

/// <summary>
/// Draws a dashed reference line at each active point of control — dashed specifically to read as
/// a reference level rather than a live order-book signal, distinct from the solid lines
/// <see cref="RestingOrderOverlay"/> already uses for that purpose. See <see cref="PocEngine"/>
/// for what "point of control" and "current move" mean here.
/// </summary>
internal sealed class PocOverlay : IDisposable
{
    internal readonly record struct Options(Color CurrentMoveColor, Color HigherTimeframeColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> pens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, PocDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Points.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var point in drawable.Points)
            {
                if (!TryY(converter, point.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var lineStartX = (float)pane.Left;
                if (TryX(converter, point.MoveStartUtc, pane.Left, pane.Right, out var originX))
                    lineStartX = originX;

                var colour = point.IsHigherTimeframe ? options.HigherTimeframeColor : options.CurrentMoveColor;

                graphics.DrawLine(this.Pen(colour), lineStartX, y, pane.Right, y);

                var text = point.IsHigherTimeframe
                    ? $"POC 15m {point.Price:0.####}"
                    : $"POC {point.Price:0.####}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(
                    pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    graphics.DrawString(text, this.font, this.LabelBrush(colour), rect.Left + 3f, rect.Top);
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

        if (double.IsNaN(price) || double.IsInfinity(price))
            return false;

        var raw = converter.GetChartY(price);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        y = (float)Math.Clamp(raw, top, bottom);
        return true;
    }

    private static bool TryX(
        IChartWindowCoordinatesConverter converter, DateTime utc, float left, float right, out float x)
    {
        x = left;

        var raw = converter.GetChartX(utc);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        x = (float)Math.Clamp(raw, left, right);
        return true;
    }

    private static bool TryReserve(List<RectangleF> registry, RectangleF rect)
    {
        foreach (var placed in registry)
        {
            if (placed.IntersectsWith(rect))
                return false;
        }

        registry.Add(rect);
        return true;
    }

    private Pen Pen(Color colour)
    {
        var key = colour.ToArgb();

        if (!this.pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, 1.5f) { DashStyle = DashStyle.Dash };
            this.pens[key] = pen;
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

        foreach (var pen in this.pens.Values) pen.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.pens.Clear();
        this.labelBrushes.Clear();
    }
}
