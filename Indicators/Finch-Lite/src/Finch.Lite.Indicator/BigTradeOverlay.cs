using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One executed large trade, ready to draw as a reference level.</summary>
/// <param name="Price">Where the print actually traded.</param>
/// <param name="Size">Contracts in the print.</param>
/// <param name="IsBuy">True if the aggressor bought (lifted the ask), false if they sold (hit
/// the bid) — unclassified prints are never queued in the first place, see
/// <see cref="FinchLiteIndicator.OnLast"/>.</param>
/// <param name="TimeUtc">When the print actually happened — where the marker (and its label)
/// anchor. See the class-level doc comment for why there's no persisting line anymore.</param>
internal readonly record struct BigTradeDraw(double Price, double Size, bool IsBuy, DateTime TimeUtc);

/// <summary>Immutable paint snapshot: the poll drains the tick queue into this, the paint reads it.</summary>
internal sealed record BigTradeDrawable(BigTradeDraw[] Trades)
{
    public static readonly BigTradeDrawable Empty = new(Array.Empty<BigTradeDraw>());
}

/// <summary>
/// "A long bar that comes out and makes a line on the chart... so i have a super clean line
/// knowing were price would react off of" (the operator's own ask, 2026-09-22) — marks every
/// recent print at or above the size threshold. Reads the TAPE (what already traded), unlike
/// <see cref="RestingOrderOverlay"/> and <see cref="DomLadderOverlay"/> above it, which both read
/// the resting book. Same colour language as those: green for a buy (lifted the ask), red for a
/// sell (hit the bid).
///
/// ADDED 2026-09-22 (same day) — "mark out were big trades happened": a filled circle drops at
/// the exact (time, price) the print traded at, sized by how far the print cleared the minimum
/// threshold.
///
/// REMOVED 2026-09-23 — "i love the bubble it makes and text next to it but i dont want the line
/// that goes all the way through my chart": the original design also drew a full-pane-width
/// standing reference line at the print's price (the very first version of this feature, before
/// the marker existed); the operator liked the marker+label but explicitly did not want the line
/// alongside it. Gone now, along with the now-unused per-colour `Pen` cache that only that line
/// used.
/// </summary>
internal sealed class BigTradeOverlay : IDisposable
{
    internal readonly record struct Options(Color BuyColor, Color SellColor, double MinSize);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Pen markerBorderPen = new(Color.FromArgb(210, 16, 18, 24), 1f);
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly Dictionary<int, SolidBrush> markerBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, BigTradeDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Trades.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var trade in drawable.Trades)
            {
                if (!TryY(converter, trade.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var colour = trade.IsBuy ? options.BuyColor : options.SellColor;

                // FIXED 2026-09-23 — "lets make this text more in the center instead of the far
                // left": the label used to always anchor at the pane's fixed left edge, however
                // far that was from where the print actually happened. Now that there's a marker
                // AT the print's own location, the label anchors just to the right of that marker
                // instead — falling back to the old left-edge anchor only when the print's own
                // time cannot be placed on screen (e.g. scrolled out of view).
                var hasMarker = TryX(converter, trade.TimeUtc, out var x) && x >= pane.Left && x <= pane.Right;
                var labelAnchorX = pane.Left + 4f;

                if (hasMarker)
                {
                    var radius = MarkerRadius(trade.Size, options.MinSize);
                    var diameter = radius * 2f;
                    graphics.FillEllipse(this.MarkerBrush(colour), x - radius, y - radius, diameter, diameter);
                    graphics.DrawEllipse(this.markerBorderPen, x - radius, y - radius, diameter, diameter);
                    labelAnchorX = x + radius + 4f;
                }

                var text = $"{(trade.IsBuy ? "BUY" : "SELL")} {trade.Size:N0}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(
                    Math.Min(labelAnchorX, pane.Right - size.Width - 6f), y - size.Height - 1f,
                    size.Width + 6f, size.Height);

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

    /// <summary>Radius grows with size relative to the qualifying minimum, on a square-root
    /// curve so a 10x-threshold print reads as noticeably bigger without needing a 10x-wider
    /// circle to say so. Clamped so one enormous print can't swallow the chart.</summary>
    private static float MarkerRadius(double size, double minSize)
    {
        var ratio = minSize > 0 ? size / minSize : 1.0;
        return (float)Math.Clamp(3.0 + (Math.Sqrt(Math.Max(ratio, 1.0)) * 2.0), 3.0, 14.0);
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

    /// <summary>Converts a UTC time to a pixel column. False on a coordinate the converter could
    /// not produce a finite value for.</summary>
    private static bool TryX(IChartWindowCoordinatesConverter converter, DateTime utc, out float x)
    {
        x = 0f;

        var raw = converter.GetChartX(utc);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        x = (float)raw;
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

    private SolidBrush MarkerBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.markerBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(215, colour));
            this.markerBrushes[key] = brush;
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
        this.markerBorderPen.Dispose();

        foreach (var brush in this.labelBrushes.Values) brush.Dispose();
        foreach (var brush in this.markerBrushes.Values) brush.Dispose();

        this.labelBrushes.Clear();
        this.markerBrushes.Clear();
    }
}
