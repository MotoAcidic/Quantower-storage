using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Flow;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// Two families of mark placed at a price and a time: clusters the search matched, and prints
/// large enough to be worth seeing on their own.
///
/// ONE RENDERER FOR TWO TOOLS, because both are the same drawing — a shape at (bar, price), sized
/// by how far past its own threshold the thing that made it went, optionally captioned. They
/// differ in shape and in colour, and a second renderer would be a second place for the size
/// scaling and the label reservation to drift.
///
/// SHAPE CARRIES THE SIDE, NOT ONLY COLOUR. A big buy is an up triangle, a sell a down one, and a
/// print the feed never classified is a circle. Hue alone is not distinguishable under every
/// colour-vision deficiency, and it survives no greyscale screenshot — the same reason the session
/// palette pairs colour with a dash pattern.
///
/// AN UNCLASSIFIED PRINT IS DRAWN, NEVER GUESSED ONTO A SIDE. It happened; which side wanted it is
/// unknown, and a triangle would invent the answer.
/// </summary>
internal sealed class FlowMarkersOverlay : IDisposable
{
    /// <param name="Flow">The document's settings for these tools.</param>
    /// <param name="Boundaries">
    /// Where this chart's bars begin and end, or null before any have been read.
    ///
    /// NOT A BAR PERIOD. A big-trade zone runs a number of BARS to the right, and multiplying a
    /// period to get there only works where bars have one. On a tick chart the zone walks the
    /// chart's own bars and stops at the newest, growing as more arrive.
    /// </param>
    /// <param name="BarsWidth">The chart's current bar width in pixels.</param>
    internal readonly record struct Options(FlowConfig Flow, IBarBoundaries? Boundaries, float BarsWidth);

    /// <summary>Opacity of a marker's fill. The outline is drawn solid over it.</summary>
    private const int MarkerAlpha = 180;

    /// <summary>Opacity of the band a big trade covered, when zones are on.</summary>
    private const int ZoneAlpha = 35;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly Dictionary<(Color, float, DashStyle), Pen> pens = new();
    private readonly Dictionary<Color, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics,
        IChartWindow window,
        FlowFrame frame,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || (frame.Hits.Length == 0 && frame.BigTrades.Length == 0))
            return;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            // Big trades first: a cluster hit marks a whole bar's level and a big trade marks one
            // print, so the finer mark sits on top where they share a price.
            this.BigTrades(graphics, converter, pane, frame, options, labelRegistry);
            this.Hits(graphics, converter, pane, frame, options, labelRegistry);
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.Clip = previousClip;
        }
    }

    /// <summary>Clusters the search matched, each an X over a circle — "X marks the spot".</summary>
    private void Hits(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        FlowFrame frame, in Options options, List<RectangleF> labelRegistry)
    {
        var search = options.Flow.ClusterSearch;
        var colour = ChartTheme.From(search.Colour);

        foreach (var hit in frame.Hits)
        {
            if (!ChartOverlay.TryX(converter, hit.BarOpenUtc, out var x)
                || !ChartOverlay.TryY(converter, hit.Price, pane.Top, pane.Bottom, out var y))
            {
                continue;
            }

            // Centred on the bar rather than hung off its open, so the mark sits over the candle
            // it describes at every zoom level.
            x += options.BarsWidth / 2f;

            if (x < pane.Left || x > pane.Right)
                continue;

            var size = search.FixedSizes
                ? search.Size
                : FlowDisplayText.MarkerSize(hit.Strength, search.MinSize, search.MaxSize);

            var half = size / 2f;

            graphics.FillEllipse(this.Brush(Color.FromArgb(MarkerAlpha, colour)), x - half, y - half, size, size);
            graphics.DrawEllipse(this.Pen(colour, 1f, DashStyle.Solid), x - half, y - half, size, size);

            var cross = this.Pen(Color.Black, 1.5f, DashStyle.Solid);
            graphics.DrawLine(cross, x - half + 2f, y - half + 2f, x + half - 2f, y + half - 2f);
            graphics.DrawLine(cross, x - half + 2f, y + half - 2f, x + half - 2f, y - half + 2f);

            this.Caption(
                graphics, FlowDisplayText.Compact(hit.Value), x + half + 2f, y, colour, labelRegistry);
        }
    }

    /// <summary>Large prints, marked where they happened.</summary>
    private void BigTrades(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        FlowFrame frame, in Options options, List<RectangleF> labelRegistry)
    {
        var big = options.Flow.BigTrades;

        // The biggest is found once rather than per trade: "zones, biggest only" asks which single
        // print was largest, and asking that inside the loop would be quadratic on a busy tape.
        BigTrade? biggest = null;

        foreach (var trade in frame.BigTrades)
        {
            if (biggest is null || trade.Volume > biggest.Value.Volume)
                biggest = trade;
        }

        foreach (var trade in frame.BigTrades)
        {
            var price = trade.PriceUnder(big.ExecutionPrice);

            if (!ChartOverlay.TryX(converter, trade.StartUtc, out var x)
                || !ChartOverlay.TryY(converter, price, pane.Top, pane.Bottom, out var y))
            {
                continue;
            }

            if (x < pane.Left - 40f || x > pane.Right)
                continue;

            var colour = ChartTheme.From(trade.Aggressor switch
            {
                Aggressor.Buy => big.BuyColour,
                Aggressor.Sell => big.SellColour,
                _ => big.UnclassifiedColour,
            });

            if (big.Zones
                && (!big.ZonesBiggestOnly
                    || (biggest is { } b && b.StartUtc == trade.StartUtc && b.Volume == trade.Volume)))
            {
                this.Zone(graphics, converter, pane, trade, x, colour, options);
            }

            var strength = big.MinVolume > 0 ? trade.Volume / big.MinVolume : 1d;
            var size = big.FixedSizes
                ? big.Size
                : FlowDisplayText.MarkerSize(strength, big.MinSize, big.MaxSize);

            this.Marker(graphics, trade.Aggressor, x, y, size, colour);

            if (big.ShowValue)
            {
                this.Caption(
                    graphics, FlowDisplayText.Compact(trade.Volume),
                    x + (size / 2f) + 2f, y, colour, labelRegistry);
            }
        }
    }

    /// <summary>
    /// The price band one big trade covered, shaded.
    ///
    /// A CUMULATIVE TRADE IS SEVERAL PRINTS, so its band is the range they swept rather than a
    /// single price. On a separate-trade feed the high and low are the same and the band collapses
    /// to a line, which is the honest drawing of a print that happened at one price.
    /// </summary>
    private void Zone(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        in BigTrade trade, float x, Color colour, in Options options)
    {
        if (!ChartOverlay.TryY(converter, trade.HighPrice, pane.Top, pane.Bottom, out var top)
            || !ChartOverlay.TryY(converter, trade.LowPrice, pane.Top, pane.Bottom, out var bottom))
        {
            return;
        }

        var right = pane.Right;

        if (options.Flow.BigTrades.ZoneBars > 0
            && options.Boundaries is { } bars
            && ChartOverlay.TryX(
                converter,
                bars.OpenAfter(trade.StartUtc, options.Flow.BigTrades.ZoneBars),
                out var xz))
        {
            right = Math.Min(xz, pane.Right);
        }

        var zone = new RectangleF(
            x, top - 2f, Math.Max(right - x, 1f), Math.Max(bottom - top, 1f) + 4f);

        graphics.FillRectangle(this.Brush(Color.FromArgb(ZoneAlpha, colour)), zone);
        graphics.DrawRectangle(
            this.Pen(Color.FromArgb(120, colour), 1f, DashStyle.Dot),
            zone.X, zone.Y, zone.Width, zone.Height);
    }

    private void Marker(Graphics graphics, Aggressor aggressor, float x, float y, int size, Color colour)
    {
        var half = size / 2f;
        var fill = this.Brush(Color.FromArgb(MarkerAlpha, colour));

        if (aggressor == Aggressor.Unknown)
        {
            graphics.FillEllipse(fill, x - half, y - half, size, size);
            return;
        }

        var points = aggressor == Aggressor.Sell
            ? new[] { new PointF(x - half, y - half), new PointF(x + half, y - half), new PointF(x, y + half) }
            : new[] { new PointF(x - half, y + half), new PointF(x + half, y + half), new PointF(x, y - half) };

        graphics.FillPolygon(fill, points);
    }

    /// <summary>
    /// A marker's caption, reserved through the shared registry.
    ///
    /// Losing the reservation drops the text and keeps the mark: where it happened is the point,
    /// how much is the courtesy.
    /// </summary>
    private void Caption(
        Graphics graphics, string text, float left, float centreY, Color colour,
        List<RectangleF> labelRegistry)
    {
        if (text.Length == 0)
            return;

        var measured = graphics.MeasureString(text, this.font);
        var rect = new RectangleF(left, centreY - (measured.Height / 2f), measured.Width, measured.Height);

        if (ChartOverlay.TryReserve(labelRegistry, rect))
            graphics.DrawString(text, this.font, this.Brush(colour), rect.Left, rect.Top);
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

        foreach (var pen in this.pens.Values)
            pen.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.pens.Clear();
        this.brushes.Clear();
    }
}
