using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One resting order level at or above the size threshold, ready to draw.</summary>
/// <param name="Price">The book price this size is resting at.</param>
/// <param name="Size">Contracts resting there, right now — this is a live snapshot, not a
/// historical record; the line moves and disappears as the book itself changes.</param>
/// <param name="IsBid">True for a resting bid (a seller would have to hit it to clear it), false
/// for a resting ask (a buyer would have to lift it).</param>
internal readonly record struct RestingOrderDraw(double Price, double Size, bool IsBid);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record RestingOrderDrawable(RestingOrderDraw[] Levels)
{
    public static readonly RestingOrderDrawable Empty = new(Array.Empty<RestingOrderDraw>());
}

/// <summary>
/// Draws a full-pane-width line at every current resting bid/ask at or above the operator's own
/// size threshold — "an area marked out that has a lot of large resting orders" (the operator's
/// own phrase, 2026-09-22). Colour is the operator's own explicit choice, not this codebase's
/// usual bullish/bearish convention: red for a bid (a seller would have to hit it to clear it),
/// green for an ask (a buyer would have to lift it).
///
/// FIXED 2026-09-22 — "some of the bid are a little hard to view": once levels started
/// persisting for the whole trading day (see FinchLiteIndicator.restingOrderMemory), a busy
/// price band could accumulate many close-together lines whose LABELS drew directly on top of
/// each other, and whose LINES packed tightly enough to read as one solid band rather than
/// distinct levels. Two fixes: labels now reserve their own space and a lower-priority label is
/// dropped rather than drawn illegibly on top of one already placed (same discipline every other
/// overlay in this codebase already uses for label collisions); and a line within
/// <see cref="MinLineGapPx"/> pixels of an already-drawn line on the SAME side is skipped
/// entirely, largest first, so a dense cluster reads as its few biggest levels rather than a
/// solid wall of near-identical adjacent lines.
/// </summary>
internal sealed class RestingOrderOverlay : IDisposable
{
    internal readonly record struct Options(Color BidColor, Color AskColor);

    /// <summary>Lines on the same side closer together than this many pixels are treated as one
    /// cluster — only the largest in the cluster draws.</summary>
    private const float MinLineGapPx = 6f;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> pens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly List<float> drawnBidY = new();
    private readonly List<float> drawnAskY = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, RestingOrderDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Levels.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        this.drawnBidY.Clear();
        this.drawnAskY.Clear();

        // Largest first: when a dense cluster has to give something up, the level that gives up
        // the least information is the smallest one nobody would have picked out anyway.
        var ordered = new List<RestingOrderDraw>(drawable.Levels);
        ordered.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var level in ordered)
            {
                if (!TryY(converter, level.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var drawnY = level.IsBid ? this.drawnBidY : this.drawnAskY;
                var tooClose = false;

                foreach (var existing in drawnY)
                {
                    if (Math.Abs(existing - y) < MinLineGapPx)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (tooClose)
                    continue;

                drawnY.Add(y);

                var colour = level.IsBid ? options.BidColor : options.AskColor;
                graphics.DrawLine(this.Pen(colour), pane.Left, y, pane.Right, y);

                var text = $"{(level.IsBid ? "BID" : "ASK")} {level.Size:N0}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

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

    /// <summary>Converts a price to a pixel row, clamped to the pane. False on a coordinate the
    /// converter could not produce a finite value for (a chart still initialising).</summary>
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
