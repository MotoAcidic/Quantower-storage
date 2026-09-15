using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OrbIx.Core.Config;
using OrbIx.Core.Flow;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The four level families absorbed from Aramid Flow: stacked imbalance, absorption, unfinished
/// auction, and the cluster search's price lines.
///
/// ONE RENDERER FOR FOUR TOOLS, because they are one drawing. Each is a horizontal span that
/// starts at the bar that made it, ends where its own visibility rule says, and carries a label —
/// they differ in colour, width and what the label says, and in nothing else. Four near-identical
/// renderers would be four places for the span geometry to drift apart.
///
/// A LEVEL STARTS WHERE IT WAS MADE. A span runs from its creating bar, never from the left edge:
/// a line drawn to the left of its own origin claims the level existed before the event that
/// created it. That is the same rule <c>DeltaLevelsOverlay</c> follows for flips and shelves.
///
/// A TOUCHED LINE IS FROZEN, NOT REMOVED. Where price went through a level is worth seeing; a
/// display that deleted it would leave the chart claiming the level simply never existed.
///
/// DISPLAY ONLY, AND THE CAPTION SAYS SO. Absorption and imbalance are MEASURED NULLS on this
/// instrument — trial 008, 25,745 episodes, +1.006 ticks against matched-random, failing
/// Bonferroni and below the 2.76-tick cost floor. A mark on a chart implies a read worth acting
/// on, and neither of these has earned that implication.
/// </summary>
internal sealed class FlowLevelsOverlay : IDisposable
{
    /// <summary>
    /// What this renderer needs beyond the frame: the document it takes styles from, the bar
    /// period a frozen line's end is measured in, and whether to draw labels.
    ///
    /// THE STYLES ARE NOT HELD HERE. <see cref="FlowLevelStyle.For"/> in Core decides which
    /// configured colour belongs to which feature and side, because that is a decision and this
    /// project cannot be tested — it targets net10.0-windows and the suite runs on Linux.
    /// </summary>
    /// <param name="Flow">The document's settings for these tools.</param>
    /// <param name="Boundaries">
    /// Where this chart's bars begin and end, or null before any have been read.
    ///
    /// NOT A BAR PERIOD. The line below ends at the CLOSE of the bar that ended it, which on a
    /// time chart is that bar's open plus the period and on a tick or Renko chart is the next
    /// bar's open. Only the chart knows the second one.
    /// </param>
    /// <param name="Labels">Whether to name each line.</param>
    internal readonly record struct Options(FlowConfig Flow, IBarBoundaries? Boundaries, bool Labels);

    /// <summary>Opacity of the band shaded behind a stack, under its own line.</summary>
    private const int BandAlpha = 40;

    /// <summary>
    /// Opacity of a PROVISIONAL level — one found on the bar still forming.
    ///
    /// Fainter and dashed, because it is not a level yet: the bar can still change and it can
    /// vanish before the close. Drawing it like a settled one would promise something the tape has
    /// not said.
    /// </summary>
    private const int ProvisionalAlpha = 160;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush labelBack = new(Color.FromArgb(150, 16, 18, 24));
    private readonly Dictionary<(Color, int, DashStyle), Pen> pens = new();
    private readonly Dictionary<Color, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics,
        IChartWindow window,
        FlowFrame frame,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || frame.Spans.Count == 0 && frame.ProvisionalAbsorption.Length == 0)
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
            foreach (var (feature, spans) in frame.Spans)
            {
                var style = FlowLevelStyle.For(options.Flow, feature);

                foreach (var span in spans)
                {
                    this.Span(
                        graphics, converter, pane, span.Level, span.StartUtc, span.EndUtc,
                        // ALPHA COMES FROM THE STYLE, NOT FROM A CONSTANT. Every other level
                        // family draws opaque and says so by leaving Alpha at 255; the
                        // absorption tiers ship near-opaque so a line over a candle still lets
                        // the candle read through it.
                        Color.FromArgb(style.Alpha, ChartTheme.From(style.For(span.Level.Side))),
                        style.Width, DashStyle.Solid,
                        options, labelRegistry);
                }
            }

            // Last, so a provisional line sits over the settled ones sharing its price rather than
            // under them — it is the thing that is changing.
            var provisional = FlowLevelStyle.For(options.Flow, LevelFeature.Absorption);

            foreach (var level in frame.ProvisionalAbsorption)
            {
                this.Span(
                    graphics, converter, pane, level, level.CreatedUtc, endUtc: null,
                    ChartTheme.From(provisional.For(level.Side), ProvisionalAlpha),
                    provisional.Width, DashStyle.Dash, options, labelRegistry);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    /// <summary>
    /// One span: the shaded band it covered, the line at its price, and its label.
    /// </summary>
    private void Span(
        Graphics graphics,
        IChartWindowCoordinatesConverter converter,
        RectangleF pane,
        FlowLevel level,
        DateTime startUtc,
        DateTime? endUtc,
        Color colour,
        int width,
        DashStyle dash,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        if (!ChartOverlay.TryX(converter, startUtc, out var x1)
            || !ChartOverlay.TryY(converter, level.Price, pane.Top, pane.Bottom, out var y))
        {
            return;
        }

        // THE END IS THE BAR'S CLOSE, NOT ITS OPEN. A line frozen at the touching bar must cover
        // that bar, or it stops one bar short of the event that ended it.
        var x2 = pane.Right;

        if (endUtc is { } end
            && options.Boundaries is { } bars
            && ChartOverlay.TryX(converter, bars.CloseOf(end), out var xe))
        {
            x2 = xe;
        }

        if (x2 < pane.Left || x1 > pane.Right)
            return;

        x1 = Math.Max(x1, pane.Left);
        x2 = Math.Min(x2, pane.Right);

        // The band is the whole run the stack covered, shaded under its own line. A one-row level
        // has no band, and drawing a minimum-height one would invent a range it never had.
        if (level.BandHigh > level.BandLow
            && ChartOverlay.TryY(converter, level.BandHigh, pane.Top, pane.Bottom, out var yTop)
            && ChartOverlay.TryY(converter, level.BandLow, pane.Top, pane.Bottom, out var yBottom))
        {
            graphics.FillRectangle(
                this.Brush(Color.FromArgb(BandAlpha, colour)),
                x1, yTop, Math.Max(x2 - x1, 1f), Math.Max(yBottom - yTop, 1f));
        }

        graphics.DrawLine(this.Pen(colour, width, dash), x1, y, x2, y);

        if (!options.Labels || level.Label.Length == 0)
            return;

        var size = graphics.MeasureString(level.Label, this.font);
        var rect = new RectangleF(x2 - size.Width - 4f, y - size.Height - 1f, size.Width + 4f, size.Height);

        // Reserved through the SHARED registry, so a label cannot land on the status line, an
        // HH/LL chip, or another feature's tag. Losing the reservation drops the text and keeps
        // the line: the price is the point, the caption is the courtesy.
        if (!ChartOverlay.TryReserve(labelRegistry, rect))
            return;

        graphics.FillRectangle(this.labelBack, rect);
        graphics.DrawString(level.Label, this.font, this.Brush(colour), rect.Left + 2f, rect.Top);
    }

    private Pen Pen(Color colour, int width, DashStyle dash)
    {
        var key = (colour, width, dash);

        if (this.pens.TryGetValue(key, out var pen))
            return pen;

        pen = new Pen(colour, Math.Max(width, 1)) { DashStyle = dash };
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

        foreach (var pen in this.pens.Values)
            pen.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.pens.Clear();
        this.brushes.Clear();
    }
}
