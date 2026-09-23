using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>One confirmed swing pivot, ready to draw.</summary>
/// <param name="BarUtc">The pivot bar's own open time.</param>
/// <param name="Price">The pivot's own high (for a swing high) or low (for a swing low).</param>
/// <param name="IsHigh">True for a swing high (arrow drawn above, pointing down), false for a
/// swing low (arrow drawn below, pointing up) — matching the operator's own ATAS reference.</param>
internal readonly record struct SwingPointDraw(DateTime BarUtc, double Price, bool IsHigh);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record SwingPointDrawable(SwingPointDraw[] Points)
{
    public static readonly SwingPointDrawable Empty = new(Array.Empty<SwingPointDraw>());
}

/// <summary>
/// "Swing High and Low" (the operator's own ATAS indicator, ported 2026-09-22 as a fresh,
/// standalone detector — not a re-skin of this indicator's own HH/LL structure engine, which
/// uses a different confirmation rule (Pine's leftBars/rightBars) and a different visual
/// language (boxed "HH"/"HL" tags). This one is the simple, symmetric N-BAR PIVOT definition the
/// operator's own settings screenshot describes: a bar is a swing high/low once PERIOD bars on
/// EACH side confirm it is the extreme of that window, with ties settled by "Include Equal".
/// Arrows only, matching the reference exactly (down arrow above a swing high, up arrow below a
/// swing low) — no attempt made to reverse-engineer ATAS's own undocumented internal algorithm
/// beyond what the settings panel and the reference chart actually show.
/// </summary>
internal sealed class SwingPointOverlay : IDisposable
{
    internal readonly record struct Options(Color HighColor, Color LowColor, float Size);

    private readonly Dictionary<int, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, SwingPointDrawable drawable, in Options options)
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
                if (!ChartOverlay.TryX(converter, point.BarUtc, out var x) || x < pane.Left || x > pane.Right)
                    continue;

                if (!ChartOverlay.TryY(converter, point.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var brush = this.Brush(point.IsHigh ? options.HighColor : options.LowColor);
                var half = options.Size;

                // Highest: a down-pointing triangle sitting ABOVE the pivot, tip touching it.
                // Lowest: an up-pointing triangle sitting BELOW the pivot, tip touching it.
                PointF[] triangle = point.IsHigh
                    ? new[]
                    {
                        new PointF(x - half, y - (half * 2.4f)), new PointF(x + half, y - (half * 2.4f)),
                        new PointF(x, y - half),
                    }
                    : new[]
                    {
                        new PointF(x - half, y + (half * 2.4f)), new PointF(x + half, y + (half * 2.4f)),
                        new PointF(x, y + half),
                    };

                graphics.FillPolygon(brush, triangle);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private SolidBrush Brush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(colour);
            this.brushes[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        foreach (var brush in this.brushes.Values) brush.Dispose();
        this.brushes.Clear();
    }
}
