using System;
using System.Collections.Generic;
using System.Drawing;
using OrbIx.Core.Config;
using OrbIx.Core.Flow;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The forming bar's buy and sell contracts, in the corner of the pane — the presenter's
/// "flickering number".
///
/// IT RESERVES ITS CORNER FIRST, before anything else that wants that space, because it is the one
/// mark on the chart that changes several times a second. A caption that landed under it would be
/// unreadable and would never be noticed as missing.
///
/// THE OFFSETS ARE MEASURED, NOT CHOSEN. The defaults clear the platform's own overlay strip along
/// the top of the pane — measured on the operator's chart 2026-09-11, where a counter at +12 px sat
/// inside that strip and was invisible.
///
/// VOLUME AND DELTA ARE BOTH SHOWN because they answer different questions: an unclassified print
/// is real volume and no delta, so a bar can be busy and directionless at once.
/// </summary>
internal sealed class FlowCounterOverlay : IDisposable
{
    internal readonly record struct Options(FlowConfig Flow);

    /// <summary>Gap between the three readings, and the padding inside the plate.</summary>
    private const float Gap = 10f;

    private const float Padding = 8f;

    private readonly SolidBrush plate = new(Color.FromArgb(170, 16, 18, 24));
    private readonly Dictionary<float, Font> fonts = new();
    private readonly Dictionary<Color, SolidBrush> brushes = new();
    private bool disposed;

    /// <summary>Draws the counter, and returns the left edge it took — the pane's right edge when it drew nothing.</summary>
    public float Draw(
        Graphics graphics,
        IChartWindow window,
        in CounterReading counter,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        var pane = window.ClientRectangle;

        if (this.disposed || !counter.HasBar || pane.Width <= 0f || pane.Height <= 0f)
            return pane.Right;

        var config = options.Flow.LiveCounter;
        var font = this.Font(Math.Clamp(config.FontSize, 8, 40));
        var (buy, sell, delta) = FlowDisplayText.Counter(counter);

        var buySize = graphics.MeasureString(buy, font);
        var sellSize = graphics.MeasureString(sell, font);
        var deltaSize = graphics.MeasureString(delta, font);

        var width = buySize.Width + sellSize.Width + deltaSize.Width + (Gap * 3f) + (Padding * 2f);
        var height = buySize.Height + Padding;

        var rect = new RectangleF(
            Math.Max(pane.Right - width - config.OffsetX, pane.Left),
            Math.Min(pane.Top + config.OffsetY, pane.Bottom - height),
            width,
            height);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            // Added rather than TryReserve'd: the counter is not negotiable for its corner, and a
            // frame where it lost the reservation would silently stop showing the one figure that
            // moves continuously.
            labelRegistry.Add(rect);

            graphics.FillRectangle(this.plate, rect);

            var stats = options.Flow.ClusterStatistics;
            var buyColour = ChartTheme.From(stats.AskColour);
            var sellColour = ChartTheme.From(stats.BidColour);

            var x = rect.Left + Padding;
            var y = rect.Top + (Padding / 2f);

            graphics.DrawString(buy, font, this.Brush(buyColour), x, y);
            x += buySize.Width + Gap;

            graphics.DrawString(sell, font, this.Brush(sellColour), x, y);
            x += sellSize.Width + Gap;

            graphics.DrawString(
                delta, font, this.Brush(counter.Delta >= 0 ? buyColour : sellColour), x, y);

            return rect.Left;
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private Font Font(float size)
    {
        if (this.fonts.TryGetValue(size, out var font))
            return font;

        font = new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold);
        this.fonts[size] = font;
        return font;
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
        this.plate.Dispose();

        foreach (var font in this.fonts.Values)
            font.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.fonts.Clear();
        this.brushes.Clear();
    }
}
