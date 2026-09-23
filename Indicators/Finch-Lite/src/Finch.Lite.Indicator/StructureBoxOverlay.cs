using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One box to draw — an order block or an inverse fair value gap; both are "a price
/// range, live since it formed, coloured by which side it favours."</summary>
/// <param name="StartUtc">When the zone itself began — the order-block candle's own open, or the
/// fair-value-gap's own first candle's open. The box draws from here forward, not from a fixed
/// screen edge, same "still extending" convention this project already uses for resting-order
/// lines.</param>
/// <param name="Top">Upper edge of the zone.</param>
/// <param name="Bottom">Lower edge of the zone.</param>
/// <param name="IsBullish">True if the zone currently favours longs (a bullish order block, or a
/// gap that has inverted INTO a bullish role) — drives colour.</param>
/// <param name="Label">Pre-built text, e.g. "15m OB — BULLISH" or "IFVG — BEARISH" — built by the
/// caller so this overlay stays generic to both features rather than knowing either one's own
/// naming.</param>
internal readonly record struct StructureBoxDraw(
    DateTime StartUtc, double Top, double Bottom, bool IsBullish, string Label);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record StructureBoxDrawable(StructureBoxDraw[] Boxes)
{
    public static readonly StructureBoxDrawable Empty = new(Array.Empty<StructureBoxDraw>());
}

/// <summary>
/// Shared box renderer for both order blocks (Feature 5) and inverse fair value gaps (Feature 6,
/// both 2026-09-23) — the same visual shape (a filled, bordered, labelled rectangle live since it
/// formed) applies to both, so one overlay type draws either, told apart only by the colours and
/// label text the caller supplies per box.
/// </summary>
internal sealed class StructureBoxOverlay : IDisposable
{
    internal readonly record struct Options(Color BullishColor, Color BearishColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, SolidBrush> fillBrushes = new();
    private readonly Dictionary<int, Pen> borderPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, StructureBoxDrawable drawable, in Options options,
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
                if (!TryY(converter, box.Top, pane.Top, pane.Bottom, out var topY))
                    continue;

                if (!TryY(converter, box.Bottom, pane.Top, pane.Bottom, out var bottomY))
                    continue;

                var startX = (float)pane.Left;
                TryX(converter, box.StartUtc, pane.Left, pane.Right, out startX);

                var colour = box.IsBullish ? options.BullishColor : options.BearishColor;
                var top = Math.Min(topY, bottomY);
                var height = Math.Max(Math.Abs(bottomY - topY), 1f);
                var rect = new RectangleF(startX, top, Math.Max(pane.Right - startX, 1f), height);

                graphics.FillRectangle(this.FillBrush(colour), rect);
                graphics.DrawRectangle(this.BorderPen(colour), rect.X, rect.Y, rect.Width, rect.Height);

                var size = graphics.MeasureString(box.Label, this.font);
                var labelRect = new RectangleF(startX + 3f, rect.Y + 1f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, labelRect))
                {
                    graphics.FillRectangle(this.labelBack, labelRect);
                    graphics.DrawString(box.Label, this.font, this.LabelBrush(colour), labelRect.Left + 3f, labelRect.Top);
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
            pen = new Pen(Color.FromArgb(160, colour), 1f);
            this.borderPens[key] = pen;
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

        foreach (var brush in this.fillBrushes.Values) brush.Dispose();
        foreach (var pen in this.borderPens.Values) pen.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.fillBrushes.Clear();
        this.borderPens.Clear();
        this.labelBrushes.Clear();
    }
}
