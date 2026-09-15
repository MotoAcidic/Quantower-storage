using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>One imbalanced price row of one bar's footprint.</summary>
/// <param name="Price">The row.</param>
/// <param name="BuySide">Which diagonal was imbalanced.</param>
/// <param name="InStack">
/// Whether this row belongs to a run long enough to count as stacked. Drawn stronger,
/// because an isolated imbalanced row and a stack of four are different observations and a
/// chart that paints them identically says they are the same one.
/// </param>
internal readonly record struct ImbalanceMark(double Price, bool BuySide, bool InStack);

/// <summary>The imbalanced rows of one closed bar.</summary>
internal readonly record struct ImbalanceBarDraw(DateTime OpenUtc, ImbalanceMark[] Marks);

/// <summary>
/// Immutable paint snapshot: the fold writes it, the paint reads it.
///
/// Built per BAR rather than as one flat list of marks so the paint can convert a bar's
/// time once and skip every row of an off-screen bar together. A session of retained
/// footprints is tens of thousands of rows, and converting each one per frame would cost
/// more than everything else the indicator draws combined.
/// </summary>
internal sealed record ImbalanceDrawable(ImbalanceBarDraw[] Bars, string Status)
{
    public static readonly ImbalanceDrawable Empty =
        new(Array.Empty<ImbalanceBarDraw>(), string.Empty);
}

/// <summary>
/// Draws diagonal footprint imbalance: aggressive buying at a price against aggressive
/// selling one tick below it, and the mirror for selling.
///
/// DISPLAY OF A MEASUREMENT, NOT OF AN EDGE. Stacked imbalance is a standard footprint read
/// and it is UNVERIFIED on this stack — every microstructure family tested here has come
/// back null. The status line carries that sentence onto the chart for the same reason the
/// zone and delta overlays carry theirs: so the drawing can never outrun the evidence.
///
/// Same disciplines as the other overlays: guarded coordinate conversion through
/// <see cref="ChartOverlay.TryX"/> and <see cref="ChartOverlay.TryY"/>, pane clipping
/// restored in a finally, brushes cached rather than allocated per row.
/// </summary>
internal sealed class ImbalanceOverlay : IDisposable
{
    internal readonly record struct Options(
        Color BuyColor, Color SellColor, double BarsWidth, int MarkWidthPx);

    /// <summary>Isolated imbalanced rows: present, but not the pattern.</summary>
    private const int LooseAlpha = 55;

    /// <summary>Rows inside a stacked run.</summary>
    private const int StackedAlpha = 190;

    /// <summary>Candidate rows for the caption, tried in order. See the note at the call site.</summary>
    private static readonly float[] CaptionRows = { 48f, 34f, 132f, 146f, 160f };

    private readonly Dictionary<(Color, int), SolidBrush> fills = new();
    private readonly Font tagFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush tagText = new(Color.Gainsboro);
    private readonly SolidBrush tagBack = new(Color.FromArgb(160, 24, 26, 32));
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window,
                     ImbalanceDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed || (drawable.Bars.Length == 0 && drawable.Status.Length == 0))
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            // The mark sits on the bar it belongs to, width-limited so a wide bar does not
            // produce a band that hides the candle underneath it.
            var barWidth = Math.Max((float)options.BarsWidth, 1f);
            var markWidth = Math.Min(barWidth, Math.Max(options.MarkWidthPx, 1));

            // A row is one tick tall in price and can be sub-pixel when zoomed out. Below one
            // pixel it is drawn AS one pixel rather than skipped: a row that rounds away is
            // an imbalance that silently did not exist.
            const float MinRowHeight = 1f;

            foreach (var bar in drawable.Bars)
            {
                if (bar.Marks.Length == 0)
                    continue;

                if (!ChartOverlay.TryX(converter, bar.OpenUtc, out var x))
                    continue;

                // Whole bar off the pane: skip its rows without converting any of them.
                if (x + barWidth < pane.Left || x > pane.Right)
                    continue;

                foreach (var mark in bar.Marks)
                {
                    if (!ChartOverlay.TryY(converter, mark.Price, pane.Top, pane.Bottom, out var y))
                        continue;

                    var color = mark.BuySide ? options.BuyColor : options.SellColor;
                    var alpha = mark.InStack ? StackedAlpha : LooseAlpha;

                    graphics.FillRectangle(
                        this.Fill(color, alpha),
                        x, y - (MinRowHeight / 2f), markWidth, MinRowHeight);
                }
            }

            // THE STATUS CARRIES THE EVIDENCE ONTO THE CHART, the same posture the zone and
            // delta overlays take. Marks on a chart imply a read worth acting on; this line
            // is what stops the drawing outrunning what has actually been measured.
            if (drawable.Status.Length != 0)
            {
                var size = graphics.MeasureString(drawable.Status, this.tagFont);

                // SEVERAL ROWS ARE TRIED, NOT ONE. Measured on the operator's chart
                // 2026-09-09: this caption asked for a single row, the transgression block and
                // the VWAP line already held it, the reservation failed and the caption
                // silently never drew — so the UNVERIFIED label it exists to carry was absent
                // from the very frame the marks were on. Skipping still beats overlapping, but
                // skipping EVERY time is an invisible feature rather than a tidy one.
                foreach (var row in CaptionRows)
                {
                    var rect = new RectangleF(
                        pane.Left + 3f, pane.Bottom - row, size.Width + 6f, size.Height);

                    if (!ChartOverlay.TryReserve(labelRegistry, rect))
                        continue;

                    graphics.FillRectangle(this.tagBack, rect);
                    graphics.DrawString(
                        drawable.Status, this.tagFont, this.tagText, pane.Left + 6f, rect.Y);
                    break;
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    /// <summary>
    /// A cached brush per colour and alpha.
    ///
    /// Cached because a busy session paints tens of thousands of rows per frame, and a brush
    /// allocated per row is a per-frame allocation storm in a GDI+ path.
    /// </summary>
    private SolidBrush Fill(Color color, int alpha)
    {
        var key = (color, alpha);

        if (!this.fills.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(alpha, color));
            this.fills[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        foreach (var brush in this.fills.Values)
            brush.Dispose();

        this.fills.Clear();
        this.tagFont.Dispose();
        this.tagText.Dispose();
        this.tagBack.Dispose();
    }
}
