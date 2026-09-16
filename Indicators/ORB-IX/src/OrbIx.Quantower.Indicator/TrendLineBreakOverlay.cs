using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// One confirmed trend-line break: a bar's close crossed the "Flow: trend lines" two-tap line
/// AND the direction callout already agreed with that side on the same bar — the operator's own
/// "all signs show break of the line in that direction" ask (2026-09-16).
/// </summary>
/// <param name="BarUtc">Open time of the bar that broke the line.</param>
/// <param name="Price">The line's own price at that bar — where the flag is anchored.</param>
/// <param name="BreakUp">True for a break above the high line, false for below the low line.</param>
internal readonly record struct TrendLineBreakDraw(DateTime BarUtc, double Price, bool BreakUp);

/// <summary>Immutable paint snapshot: the fold appends to a capped history, the paint only reads it.</summary>
internal sealed record TrendLineBreakDrawable(TrendLineBreakDraw[] Flags)
{
    public static readonly TrendLineBreakDrawable Empty = new(Array.Empty<TrendLineBreakDraw>());
}

/// <summary>Draws a small triangle + label at each confirmed trend-line break.</summary>
internal sealed class TrendLineBreakOverlay : IDisposable
{
    internal readonly record struct Options(Color UpColor, Color DownColor);

    private const float TriangleSize = 6f;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, TrendLineBreakDrawable drawable,
        in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Flags.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var flag in drawable.Flags)
            {
                if (!ChartOverlay.TryX(converter, flag.BarUtc, out var x)
                    || !ChartOverlay.TryY(converter, flag.Price, pane.Top, pane.Bottom, out var y))
                {
                    continue;
                }

                if (x < pane.Left - 20f || x > pane.Right + 20f || y <= pane.Top || y >= pane.Bottom)
                    continue;

                var colour = flag.BreakUp ? options.UpColor : options.DownColor;
                var brush = this.Brush(colour);

                // Up-break points up (price broke out above); down-break points down.
                PointF[] triangle = flag.BreakUp
                    ? new[]
                    {
                        new PointF(x, y - TriangleSize - 4f), new PointF(x - TriangleSize, y - 4f),
                        new PointF(x + TriangleSize, y - 4f),
                    }
                    : new[]
                    {
                        new PointF(x, y + TriangleSize + 4f), new PointF(x - TriangleSize, y + 4f),
                        new PointF(x + TriangleSize, y + 4f),
                    };

                graphics.FillPolygon(brush, triangle);

                const string text = "TREND BREAK";
                var size = graphics.MeasureString(text, this.font);
                var labelY = flag.BreakUp ? y - TriangleSize - size.Height - 6f : y + TriangleSize + 6f;
                var rect = new RectangleF(x - (size.Width / 2f) - 3f, labelY, size.Width + 6f, size.Height);

                if (!ChartOverlay.TryReserve(labelRegistry, rect))
                    continue;

                graphics.FillRectangle(this.labelBack, rect);
                graphics.DrawString(text, this.font, brush, rect.Left + 3f, rect.Top);
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
        this.font.Dispose();
        this.labelBack.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.brushes.Clear();
    }
}
