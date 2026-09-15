using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Flow;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The resting book: the largest levels on each side, and a depth profile down the right edge.
///
/// A FABRICATED BOOK IS SAID, NOT DRAWN. A connector with no real depth publishes one price per
/// side under its own placeholder id. Drawn without comment that reads as "the largest resting size
/// in the book sits at the touch" — a statement about a book nobody published. The frame decides
/// whether that is the case (<see cref="DomReading.Caveat"/>); this prints the sentence instead of
/// the lines.
///
/// THE LINES START MID-PANE, NOT AT THE LEFT EDGE. Resting size is a fact about NOW; a line drawn
/// across the whole chart would imply it was resting there for the whole history on screen.
///
/// THE LARGEST SINGLE ORDER IS DASHED AND SEPARATE, and appears only on a per-order feed. On an
/// aggregated book the figure does not exist — the frame returns null — and drawing the level total
/// in its place would present a hundred orders as one.
/// </summary>
internal sealed class FlowDomOverlay : IDisposable
{
    internal readonly record struct Options(FlowConfig Flow, double TickSize);

    /// <summary>Where the level lines begin, as a fraction of pane width from the left.</summary>
    private const float LineStartFraction = 0.6f;

    /// <summary>Opacity of a depth-profile row.</summary>
    private const int ProfileAlpha = 110;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush labelBack = new(Color.FromArgb(150, 16, 18, 24));
    private readonly SolidBrush caveatText = new(Color.Gainsboro);
    private readonly Dictionary<(Color, float, DashStyle), Pen> pens = new();
    private readonly Dictionary<Color, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics,
        IChartWindow window,
        DomReading dom,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed)
            return;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            if (dom.Caveat is { } caveat)
            {
                this.Caveat(graphics, pane, caveat, labelRegistry);
                return;
            }

            var config = options.Flow.DomLevels;
            var bid = ChartTheme.From(config.BidColour);
            var ask = ChartTheme.From(config.AskColour);

            if (config.Profile)
                this.Profile(graphics, converter, pane, dom, options, bid, ask);

            var lineStart = pane.Left + (pane.Width * LineStartFraction);

            foreach (var level in dom.LargestBids)
                this.Level(graphics, converter, pane, level, bid, lineStart, labelRegistry);

            foreach (var level in dom.LargestAsks)
                this.Level(graphics, converter, pane, level, ask, lineStart, labelRegistry);

            if (dom.LargestBidOrder is { } bidOrder)
                this.Order(graphics, converter, pane, bidOrder, bid, lineStart, labelRegistry);

            if (dom.LargestAskOrder is { } askOrder)
                this.Order(graphics, converter, pane, askOrder, ask, lineStart, labelRegistry);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    /// <summary>
    /// The depth profile down the right edge, both sides scaled against ONE maximum.
    ///
    /// One scale rather than one per side, deliberately: the point of the profile is which side is
    /// heavier, and two independent scales would make a thin side look as deep as a thick one.
    /// </summary>
    private void Profile(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        DomReading dom, in Options options, Color bid, Color ask)
    {
        var max = 0d;

        foreach (var level in dom.ProfileBids)
            max = Math.Max(max, level.Size);

        foreach (var level in dom.ProfileAsks)
            max = Math.Max(max, level.Size);

        if (max <= 0)
            return;

        var width = Math.Clamp(options.Flow.DomLevels.ProfileWidthPx, 20, 400);

        this.ProfileSide(graphics, converter, pane, dom.ProfileBids, max, width, bid, options.TickSize);
        this.ProfileSide(graphics, converter, pane, dom.ProfileAsks, max, width, ask, options.TickSize);
    }

    private void ProfileSide(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        DepthLevel[] levels, double max, int width, Color colour, double tickSize)
    {
        var brush = this.Brush(Color.FromArgb(ProfileAlpha, colour));

        foreach (var level in levels)
        {
            // The row's HEIGHT is the distance to the next tick, taken from the converter rather
            // than assumed: it changes with the vertical zoom, and a fixed height would overlap at
            // one scale and leave gaps at another.
            if (!ChartOverlay.TryY(converter, level.Price, pane.Top, pane.Bottom, out var y)
                || !ChartOverlay.TryY(converter, level.Price + tickSize, pane.Top, pane.Bottom, out var above))
            {
                continue;
            }

            var rowHeight = Math.Max(Math.Abs(y - above) - 1f, 1f);
            var barWidth = (float)(level.Size / max * width);

            graphics.FillRectangle(
                brush, pane.Right - barWidth, Math.Min(y, above) + 0.5f, barWidth, rowHeight);
        }
    }

    private void Level(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        in DepthLevel level, Color colour, float lineStart, List<RectangleF> labelRegistry)
    {
        if (!ChartOverlay.TryY(converter, level.Price, pane.Top, pane.Bottom, out var y))
            return;

        graphics.DrawLine(this.Pen(colour, 2f, DashStyle.Solid), lineStart, y, pane.Right, y);

        // The order COUNT is carried beside the size, because a hundred lots in one order and a
        // hundred lots in fifty are different things standing at the same price.
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{FlowDisplayText.Compact(level.Size)} × {level.Orders}");

        this.Tag(graphics, text, lineStart, y, above: true, colour, labelRegistry);
    }

    private void Order(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        in RestingOrder order, Color colour, float lineStart, List<RectangleF> labelRegistry)
    {
        if (!ChartOverlay.TryY(converter, order.Price, pane.Top, pane.Bottom, out var y))
            return;

        graphics.DrawLine(this.Pen(colour, 1f, DashStyle.Dash), lineStart, y, pane.Right, y);

        this.Tag(
            graphics, "order " + FlowDisplayText.Compact(order.Size),
            lineStart, y, above: false, colour, labelRegistry);
    }

    private void Tag(
        Graphics graphics, string text, float right, float y, bool above, Color colour,
        List<RectangleF> labelRegistry)
    {
        var measured = graphics.MeasureString(text, this.font);

        var rect = new RectangleF(
            right - measured.Width - 4f,
            above ? y - measured.Height - 1f : y + 1f,
            measured.Width + 4f,
            measured.Height);

        if (!ChartOverlay.TryReserve(labelRegistry, rect))
            return;

        graphics.FillRectangle(this.labelBack, rect);
        graphics.DrawString(text, this.font, this.Brush(colour), rect.Left + 2f, rect.Top);
    }

    /// <summary>
    /// What the book cannot say, said. Drawn at the right edge where the levels would have been,
    /// so a reader looking for them finds the reason instead of nothing.
    /// </summary>
    private void Caveat(
        Graphics graphics, RectangleF pane, string caveat, List<RectangleF> labelRegistry)
    {
        var measured = graphics.MeasureString(caveat, this.font);
        var rect = new RectangleF(
            pane.Right - measured.Width - 8f,
            pane.Top + (pane.Height / 2f),
            measured.Width + 6f,
            measured.Height);

        if (!ChartOverlay.TryReserve(labelRegistry, rect))
            return;

        graphics.FillRectangle(this.labelBack, rect);
        graphics.DrawString(caveat, this.font, this.caveatText, rect.Left + 3f, rect.Top);
    }

    private Pen Pen(Color colour, float width, DashStyle dash)
    {
        var key = (colour, width, dash);

        if (this.pens.TryGetValue(key, out var pen))
            return pen;

        pen = new Pen(colour, width) { DashStyle = dash };
        this.pens[key] = pen;
        return pen;
    }

    private SolidBrush Brush(Color colour)
    {
        if (this.brushes.TryGetValue(colour, out var brush))
            return brush;

        brush = new SolidBrush(colour);
        this.brushes[colour] = brush;
        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();
        this.caveatText.Dispose();

        foreach (var pen in this.pens.Values)
            pen.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.pens.Clear();
        this.brushes.Clear();
    }
}
