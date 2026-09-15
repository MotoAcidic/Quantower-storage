using System;
using System.Collections.Generic;
using System.Drawing;
using OrbIx.Core.Features;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>Immutable paint snapshot for absorption: the fold writes it, the paint reads it.</summary>
/// <param name="BidPrice">Best bid, or NaN when unknown.</param>
/// <param name="AskPrice">Best ask, or NaN when unknown.</param>
/// <param name="BidAbsorbing">Whether the bid held under aggression.</param>
/// <param name="AskAbsorbing">Whether the ask held under aggression.</param>
/// <param name="Status">The caption, carrying the measured record with it.</param>
internal sealed record AbsorptionDrawable(
    double BidPrice,
    double AskPrice,
    bool BidAbsorbing,
    bool AskAbsorbing,
    string Status)
{
    public static readonly AbsorptionDrawable Empty =
        new(double.NaN, double.NaN, false, false, string.Empty);

    /// <summary>Whether there is anything at all to draw.</summary>
    public bool HasMarks => this.BidAbsorbing || this.AskAbsorbing;
}

/// <summary>
/// Draws absorption at the touch — a bracket at the resting price that is holding under
/// aggression, and a caption naming what the measure has actually been shown to do.
///
/// THE TOUCH ONLY, AND ONLY NOW. Absorption is a rolling-window question about the price
/// currently resting at the top of book; it is not a property of a bar, so nothing here
/// paints history. Retaining and drawing a past verdict would imply the level was still
/// holding after the window that judged it had closed.
///
/// DISPLAY OF A MEASURED NULL. Trial 008 measured absorption on MNQ across 25,745 episodes
/// at +1.006 ticks against matched-random — failing Bonferroni and below the 2.76-tick cost
/// floor. The caption says so on the chart, for the same reason the zone and delta overlays
/// carry theirs: a mark on a chart implies a read worth acting on, and this one has not
/// earned that implication.
/// </summary>
internal sealed class AbsorptionOverlay : IDisposable
{
    internal readonly record struct Options(Color BidColor, Color AskColor, double BarsWidth);

    /// <summary>
    /// Candidate rows for the caption, tried in order.
    ///
    /// A SINGLE FIXED ROW IS NOT ENOUGH, measured on the operator's own chart 2026-09-09: the
    /// imbalance caption claimed one row, the transgression block and the VWAP line were
    /// already there, the reservation failed and the caption silently never drew. Skipping
    /// beats overlapping — but skipping every time is just an invisible feature, so several
    /// rows are offered before giving up.
    /// </summary>
    private static readonly float[] CaptionRows = { 62f, 76f, 90f, 104f, 118f };

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush text = new(Color.Gainsboro);
    private readonly SolidBrush back = new(Color.FromArgb(160, 24, 26, 32));
    private readonly Dictionary<Color, Pen> pens = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window,
                     AbsorptionDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed || (!drawable.HasMarks && drawable.Status.Length == 0))
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            var width = Math.Max((float)options.BarsWidth, 6f);

            if (drawable.BidAbsorbing)
                this.Mark(graphics, converter, pane, drawable.BidPrice, options.BidColor, width);

            if (drawable.AskAbsorbing)
                this.Mark(graphics, converter, pane, drawable.AskPrice, options.AskColor, width);

            if (drawable.Status.Length != 0)
                this.Caption(graphics, pane, drawable.Status, labelRegistry);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    /// <summary>
    /// A short bracket at the right edge, at the price that is holding.
    ///
    /// Drawn at the LIVE edge rather than across the pane: this is a statement about now, and
    /// a full-width line would read as a level that has held all session.
    /// </summary>
    private void Mark(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        double price, Color color, float width)
    {
        if (double.IsNaN(price)
            || !ChartOverlay.TryY(converter, price, pane.Top, pane.Bottom, out var y))
        {
            return;
        }

        var right = pane.Right - 2f;
        var left = Math.Max(pane.Left, right - (width * 3f));

        graphics.DrawLine(this.Pen(color), left, y, right, y);
        graphics.DrawLine(this.Pen(color), right, y - 4f, right, y + 4f);
    }

    private void Caption(
        Graphics graphics, RectangleF pane, string status, List<RectangleF> labelRegistry)
    {
        var size = graphics.MeasureString(status, this.font);

        foreach (var row in CaptionRows)
        {
            var rect = new RectangleF(pane.Left + 3f, pane.Bottom - row, size.Width + 6f, size.Height);

            if (!ChartOverlay.TryReserve(labelRegistry, rect))
                continue;

            graphics.FillRectangle(this.back, rect);
            graphics.DrawString(status, this.font, this.text, pane.Left + 6f, rect.Y);
            return;
        }
    }

    private Pen Pen(Color color)
    {
        if (!this.pens.TryGetValue(color, out var pen))
        {
            pen = new Pen(color, 2f);
            this.pens[color] = pen;
        }

        return pen;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        foreach (var pen in this.pens.Values)
            pen.Dispose();

        this.pens.Clear();
        this.font.Dispose();
        this.text.Dispose();
        this.back.Dispose();
    }
}
