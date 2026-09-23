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
internal readonly record struct BigTradeDraw(double Price, double Size, bool IsBuy);

/// <summary>Immutable paint snapshot: the poll drains the tick queue into this, the paint reads it.</summary>
internal sealed record BigTradeDrawable(BigTradeDraw[] Trades)
{
    public static readonly BigTradeDrawable Empty = new(Array.Empty<BigTradeDraw>());
}

/// <summary>
/// "A long bar that comes out and makes a line on the chart... so i have a super clean line
/// knowing were price would react off of" (the operator's own ask, 2026-09-22) — a full-pane
/// horizontal line at the price of every recent print at or above the size threshold. Reads the
/// TAPE (what already traded), unlike <see cref="RestingOrderOverlay"/> and
/// <see cref="DomLadderOverlay"/> above it, which both read the resting book. Same colour
/// language as those: green for a buy (lifted the ask), red for a sell (hit the bid).
/// </summary>
internal sealed class BigTradeOverlay : IDisposable
{
    internal readonly record struct Options(Color BuyColor, Color SellColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> pens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
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
                graphics.DrawLine(this.Pen(colour), pane.Left, y, pane.Right, y);

                var text = $"{(trade.IsBuy ? "BUY" : "SELL")} {trade.Size:N0}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Left + 4f, y - size.Height - 1f, size.Width + 6f, size.Height);

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
            pen = new Pen(Color.FromArgb(200, colour), 1.5f);
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
