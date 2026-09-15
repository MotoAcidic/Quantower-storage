using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using OrbIx.Core.Structure;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>One retracement line, resolved by the fold into something paintable.</summary>
internal readonly record struct FibLineDraw(double Ratio, double Price, bool IsPocketEdge);

/// <summary>
/// Immutable paint snapshot: the fold writes it, the paint reads it.
///
/// Same split as <see cref="HhLlDrawable"/>, and for the same reason — the fold runs on one
/// thread and the paint on another, so the paint must never walk a list the fold is
/// rebuilding underneath it.
/// </summary>
internal sealed record FibDrawable(
    FibLineDraw[] Lines,
    double PocketLow,
    double PocketHigh,
    bool HasPocket,
    DateTime OriginUtc)
{
    public static readonly FibDrawable Empty =
        new(Array.Empty<FibLineDraw>(), 0, 0, false, default);
}

/// <summary>
/// Draws the Fibonacci retracement: one line per ratio, the golden pocket as a filled
/// region, each line tagged with its ratio and price.
///
/// IT DRAWS LEVELS AND PRICES. There is no watermark, no disclaimer, no caveat line and no
/// "not a signal" chip, by decision — the operator asked for the grid, not for commentary
/// on it, and the research record lives in the trial documents where it belongs rather than
/// on the screen of someone holding a position.
///
/// Same disciplines as <see cref="ChartOverlay"/> and <see cref="HhLlOverlay"/>: coordinates
/// go through the shared guarded <see cref="ChartOverlay.TryX"/>/<see cref="ChartOverlay.TryY"/>,
/// everything is clipped to the pane, pens and brushes are cached and rebuilt only when the
/// inputs actually change, and price tags reserve their rectangles in the frame's shared
/// collision registry so they cannot print through the session tags or the HH/LL chips.
/// </summary>
internal sealed class FibOverlay : IDisposable
{
    internal readonly record struct Options(
        Color LineColor, Color PocketColor, HhLlLineStyle LineStyle, int LineWidth,
        bool ShowPocket, bool ShowPrices, double BarsWidth);

    private const float TagPadX = 3f;
    private const float TagGap = 2f;

    /// <summary>
    /// How opaque the pocket fill is.
    ///
    /// A FILL SITS OVER THE CANDLES, so it has to be light enough to read price through.
    /// The alpha is applied here rather than asked of the operator: a colour picker that
    /// can produce an opaque band over the bars is a setting whose wrong value looks like
    /// a broken chart.
    /// </summary>
    private const int PocketAlpha = 48;

    private readonly Font tagFont = new(FontFamily.GenericSansSerif, 7.5f, FontStyle.Regular);
    private SolidBrush? tagBrush;
    private SolidBrush? pocketBrush;
    private Pen? linePen;
    private (Color Line, Color Pocket, HhLlLineStyle Style, int Width) penKey;
    private bool disposed;

    /// <param name="labelRegistry">
    /// The frame's shared collision registry (ChartOverlay.BeginFrame). A price tag that
    /// cannot claim its pixels is SKIPPED rather than drawn over something else — the same
    /// rule the HH/LL chips follow.
    /// </param>
    public void Draw(Graphics graphics, IChartWindow window,
                     FibDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Lines.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        float top = pane.Top;
        float bottom = pane.Bottom;

        // The grid begins at the leg's origin and runs to the right edge. Anchoring the
        // left end to the origin bar is what makes it read as a measurement OF something
        // rather than as a set of floating horizontals.
        float left = pane.Left;

        if (ChartOverlay.TryX(converter, drawable.OriginUtc, out var originX))
            left = Math.Max(pane.Left, originX + (float)(options.BarsWidth / 2.0));

        if (left >= pane.Right)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            this.EnsurePens(options);

            // THE BAND FIRST, so the lines land on top of their own fill rather than
            // underneath it.
            if (options.ShowPocket && drawable.HasPocket
                && ChartOverlay.TryY(converter, drawable.PocketHigh, top, bottom, out var pocketTop)
                && ChartOverlay.TryY(converter, drawable.PocketLow, top, bottom, out var pocketBottom))
            {
                // Y grows downward, so the HIGHER price is the SMALLER y. Ordered here
                // rather than trusted, because a negative height draws nothing at all and
                // would look like the pocket had silently stopped working.
                float bandTop = Math.Min(pocketTop, pocketBottom);
                float bandHeight = Math.Abs(pocketBottom - pocketTop);

                if (bandHeight > 0 && bandTop < bottom && bandTop + bandHeight > top)
                {
                    graphics.FillRectangle(
                        this.pocketBrush!, left, bandTop, pane.Right - left, bandHeight);
                }
            }

            foreach (var line in drawable.Lines)
            {
                if (!ChartOverlay.TryY(converter, line.Price, top, bottom, out var y))
                    continue;

                // Pinned to a pane edge means off-scale — ChartOverlay's rule, followed so
                // an off-screen level does not draw as a line along the top of the chart.
                if (y <= top || y >= bottom)
                    continue;

                graphics.DrawLine(this.linePen!, left, y, pane.Right, y);

                if (!options.ShowPrices)
                    continue;

                string text = string.Create(
                    CultureInfo.InvariantCulture, $"{line.Ratio:0.###}  {line.Price:N2}");

                var size = graphics.MeasureString(text, this.tagFont);
                var rect = new RectangleF(
                    left + TagPadX, y - size.Height - TagGap,
                    size.Width + (2f * TagPadX), size.Height);

                if (!ChartOverlay.TryReserve(labelRegistry, rect))
                    continue;

                graphics.DrawString(text, this.tagFont, this.tagBrush!,
                                    rect.Left + TagPadX, rect.Top);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void EnsurePens(in Options options)
    {
        var key = (options.LineColor, options.PocketColor,
                   options.LineStyle, options.LineWidth);

        if (this.linePen is not null && key == this.penKey)
            return;

        this.linePen?.Dispose();
        this.tagBrush?.Dispose();
        this.pocketBrush?.Dispose();

        var dash = options.LineStyle switch
        {
            HhLlLineStyle.Solid => DashStyle.Solid,
            HhLlLineStyle.Dashed => DashStyle.Dash,
            _ => DashStyle.Dot,
        };

        this.linePen = new Pen(options.LineColor, options.LineWidth) { DashStyle = dash };
        this.tagBrush = new SolidBrush(options.LineColor);
        this.pocketBrush = new SolidBrush(
            Color.FromArgb(PocketAlpha, options.PocketColor));
        this.penKey = key;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.tagFont.Dispose();
        this.linePen?.Dispose();
        this.tagBrush?.Dispose();
        this.pocketBrush?.Dispose();
    }
}
