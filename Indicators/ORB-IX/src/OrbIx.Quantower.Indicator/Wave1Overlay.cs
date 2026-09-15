using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using OrbIx.Core.Features;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The anchor choices shared by the anchored VWAP and the anchored profile; Off disables the line.
///
/// TWO OF THESE ARE OFFERED TO ONLY ONE OF THE TWO INPUTS, and that is deliberate rather than an
/// oversight: they are the two tools absorbed from Aramid Flow that ORB-IX already had its own
/// version of. Flow's WEEKLY VWAP returns as <see cref="WeekOpen"/> on the anchored VWAP, and its
/// look-back volume profile as <see cref="LookBackHigh"/> on the anchored profile. Each input
/// lists only the variants that mean something for it, so no dropdown offers an anchor its own
/// tool has no use for — while the resolver handles every member, because a saved chart template
/// can carry one the dropdown no longer shows.
/// </summary>
public enum VwapAnchorChoice
{
    Off,
    OrbClose,
    LastHigherHigh,
    LastLowerLow,
    CustomTime,

    /// <summary>
    /// The most recent TRADING week open — Sunday evening, not Monday midnight. The calendar week
    /// would miss the Sunday session and restart the line in the middle of Monday's.
    /// </summary>
    WeekOpen,

    /// <summary>The highest high over the configured look-back, which Aramid Flow's profile ran from.</summary>
    LookBackHigh,
}

/// <summary>One shaded news window, resolved by the fold.</summary>
internal readonly record struct NewsBandDraw(DateTime StartUtc, DateTime EndUtc, string Label);

/// <summary>Immutable Wave-1 paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record Wave1Drawable(
    VwapSample[] SessionVwap,
    VwapSample[] AnchoredVwap,
    double[] BandKs,
    NewsBandDraw[] NewsBands,
    double? RiskDllPrice,
    double? RiskMllPrice,
    string RiskDllTag,
    string RiskMllTag,
    string[] CostLines,
    string[] OperatorLines,
    string Status)
{
    public static readonly Wave1Drawable Empty = new(
        Array.Empty<VwapSample>(), Array.Empty<VwapSample>(),
        Array.Empty<double>(), Array.Empty<NewsBandDraw>(),
        null, null, string.Empty, string.Empty, Array.Empty<string>(),
        Array.Empty<string>(), string.Empty);
}

/// <summary>
/// Renders the master plan's Wave 1: VWAP with ±kσ bands (session and
/// anchored), Tier-1 news-window shading, the risk horizon lines, and
/// the execution cost meter. Same disciplines as every overlay here:
/// guarded conversion via <see cref="ChartOverlay.TryX"/>/<see cref="ChartOverlay.TryY"/>,
/// pane clipping restored in a finally, cached pens/brushes, labels
/// through the frame's shared collision registry.
///
/// DISPLAY ONLY, stated on the chart: VWAP gates measured net-negative
/// out of sample (trial 022); the risk lines show where the account's
/// own limits sit, they recommend nothing.
/// </summary>
internal sealed class Wave1Overlay : IDisposable
{
    internal readonly record struct Options(
        Color VwapColor, Color AnchoredColor, Color NewsColor,
        Color RiskColor, double BarsWidth);

    private const int NewsAlpha = 26;
    private const int BandAlpha = 60;

    private readonly Font tagFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly Font riskFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush tagText = new(Color.Gainsboro);
    private readonly SolidBrush tagBack = new(Color.FromArgb(160, 24, 26, 32));
    private SolidBrush? newsFill;
    private Pen? vwapPen;
    private Pen? anchoredPen;
    private Pen? bandPen;
    private Pen? anchoredBandPen;
    private Pen? riskDllPen;
    private Pen? riskMllPen;
    private (Color Vwap, Color Anchored, Color News, Color Risk) resourceKey;
    private bool disposed;

    /// <summary>News bands paint FIRST — the lowest layer under everything.</summary>
    public void DrawNewsBands(Graphics graphics, IChartWindow window,
                              Wave1Drawable drawable, in Options options)
    {
        if (this.disposed || drawable.NewsBands.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        this.EnsureResources(options);
        var fill = this.newsFill;

        if (fill is null)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var band in drawable.NewsBands)
            {
                if (!ChartOverlay.TryX(converter, band.StartUtc, out var x1)
                    || !ChartOverlay.TryX(converter, band.EndUtc, out var x2))
                {
                    continue;
                }

                float left = Math.Max(x1, pane.Left);
                float right = Math.Min(x2, pane.Right);
                if (right <= pane.Left || left >= pane.Right || right <= left)
                    continue;

                graphics.FillRectangle(fill, left, pane.Top, right - left, pane.Height);
                graphics.DrawString(band.Label, this.tagFont, this.tagText,
                                    left + 2f, pane.Top + 2f);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    /// <summary>
    /// VWAP lines/bands, risk lines, and the cost meter — above the ranges.
    /// </summary>
    /// <remarks>
    /// THERE IS NO LONGER A FOCUS MODE. This took a focusOnly flag that dropped VWAP, its
    /// bands, the cost meter and the readout while a rule breach was live. The rules it keyed
    /// on were removed at the operator's request, so the flag could never again be true and
    /// every branch behind it was unreachable. Removed rather than left defaulted to false.
    /// </remarks>
    public void DrawGeometry(Graphics graphics, IChartWindow window,
                             Wave1Drawable drawable, in Options options,
                             List<RectangleF> labelRegistry)
    {
        if (this.disposed)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        this.EnsureResources(options);
        float halfBar = (float)(options.BarsWidth / 2.0);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            this.DrawVwapSeries(graphics, converter, pane, halfBar,
                                drawable.SessionVwap, drawable.BandKs,
                                this.vwapPen, this.bandPen);
            this.DrawVwapSeries(graphics, converter, pane, halfBar,
                                drawable.AnchoredVwap, drawable.BandKs,
                                this.anchoredPen, this.anchoredBandPen);

            // SEPARATE TAGS, because the two lines no longer rest on the same footing. The
            // daily line is drawn from a realised figure when one can be read; the max-loss
            // line is the configured number and does NOT model the trail, and saying so on
            // the line itself is the only place a reader will see it.
            this.DrawRiskLine(graphics, converter, pane, labelRegistry,
                              drawable.RiskDllPrice, this.riskDllPen,
                              $"DLL {drawable.RiskDllTag}");
            this.DrawRiskLine(graphics, converter, pane, labelRegistry,
                              drawable.RiskMllPrice, this.riskMllPen,
                              $"MLL {drawable.RiskMllTag}");

            // The operator's own pace sits ABOVE the cost meter and shares its cursor, so
            // the two stacks cannot overlap when both are switched on. It is drawn in the
            // plain tag style, NOT the problem marker: a count is not a fault, and putting
            // it through the fault channel would leave a warning glyph on a healthy chart
            // permanently — which is how a marker stops meaning anything.
            var stack = drawable.OperatorLines.Concat(drawable.CostLines).ToArray();

            float y = pane.Top + 6f;
            foreach (var line in stack)
            {
                var size = graphics.MeasureString(line, this.tagFont);
                var rect = new RectangleF(
                    pane.Right - size.Width - 12f, y, size.Width + 6f, size.Height);
                if (ChartOverlay.TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.tagBack, rect);
                    graphics.DrawString(line, this.tagFont, this.tagText,
                                        rect.Left + 3f, rect.Top);
                }

                y += 15f;
            }

            if (drawable.Status.Length != 0)
            {
                var size = graphics.MeasureString(drawable.Status, this.tagFont);
                var rect = new RectangleF(pane.Left + 3f, pane.Bottom - 50f,
                                          size.Width + 6f, size.Height);
                if (ChartOverlay.TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.tagBack, rect);
                    graphics.DrawString(drawable.Status, this.tagFont, this.tagText,
                                        rect.Left + 3f, rect.Top);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawVwapSeries(
        Graphics graphics, IChartWindowCoordinatesConverter converter,
        RectangleF pane, float halfBar, VwapSample[] samples,
        double[] bandKs, Pen? linePen, Pen? bandPenLocal)
    {
        if (samples.Length < 2 || linePen is null)
            return;

        var line = new List<PointF>(samples.Length);
        // one polyline per band edge: bandKs.Length × {upper, lower}
        var bands = new List<PointF>[bandKs.Length * 2];
        for (var b = 0; b < bands.Length; b++)
            bands[b] = new List<PointF>(samples.Length);

        foreach (var sample in samples)
        {
            if (!ChartOverlay.TryX(converter, sample.BarOpenUtc, out var x))
                continue;
            x += halfBar;
            if (x < pane.Left - 4f || x > pane.Right + 4f)
                continue;

            if (ChartOverlay.TryY(converter, sample.Vwap, pane.Top, pane.Bottom, out var yv))
                line.Add(new PointF(x, yv));

            for (var k = 0; k < bandKs.Length; k++)
            {
                if (ChartOverlay.TryY(converter, sample.Vwap + (bandKs[k] * sample.Sigma),
                                      pane.Top, pane.Bottom, out var yUp))
                    bands[k * 2].Add(new PointF(x, yUp));
                if (ChartOverlay.TryY(converter, sample.Vwap - (bandKs[k] * sample.Sigma),
                                      pane.Top, pane.Bottom, out var yDown))
                    bands[(k * 2) + 1].Add(new PointF(x, yDown));
            }
        }

        if (bandPenLocal is not null)
        {
            foreach (var band in bands)
            {
                if (band.Count >= 2)
                    graphics.DrawLines(bandPenLocal, band.ToArray());
            }
        }

        if (line.Count >= 2)
            graphics.DrawLines(linePen, line.ToArray());
    }

    private void DrawRiskLine(
        Graphics graphics, IChartWindowCoordinatesConverter converter,
        RectangleF pane, List<RectangleF> labelRegistry,
        double? price, Pen? pen, string tag)
    {
        if (price is not { } level || pen is null)
            return;

        if (!ChartOverlay.TryY(converter, level, pane.Top, pane.Bottom, out var y)
            || y <= pane.Top || y >= pane.Bottom)
        {
            return;
        }

        graphics.DrawLine(pen, pane.Left, y, pane.Right, y);

        var size = graphics.MeasureString(tag, this.riskFont);
        var rect = new RectangleF(pane.Left + 40f, y - size.Height - 2f,
                                  size.Width + 6f, size.Height);
        if (ChartOverlay.TryReserve(labelRegistry, rect))
        {
            graphics.FillRectangle(this.tagBack, rect);
            graphics.DrawString(tag, this.riskFont, this.tagText,
                                rect.Left + 3f, rect.Top);
        }
    }

    private void EnsureResources(in Options options)
    {
        var key = (options.VwapColor, options.AnchoredColor,
                   options.NewsColor, options.RiskColor);
        if (this.vwapPen is not null && key == this.resourceKey)
            return;

        this.newsFill?.Dispose();
        this.vwapPen?.Dispose();
        this.anchoredPen?.Dispose();
        this.bandPen?.Dispose();
        this.anchoredBandPen?.Dispose();
        this.riskDllPen?.Dispose();
        this.riskMllPen?.Dispose();

        this.newsFill = new SolidBrush(Color.FromArgb(NewsAlpha, options.NewsColor));
        this.vwapPen = new Pen(options.VwapColor, 1.6f);
        this.anchoredPen = new Pen(options.AnchoredColor, 1.6f) { DashStyle = DashStyle.Dash };
        this.bandPen = new Pen(Color.FromArgb(BandAlpha, options.VwapColor), 1f)
        { DashStyle = DashStyle.Dot };
        this.anchoredBandPen = new Pen(Color.FromArgb(BandAlpha, options.AnchoredColor), 1f)
        { DashStyle = DashStyle.Dot };
        this.riskDllPen = new Pen(options.RiskColor, 2f) { DashStyle = DashStyle.Dash };
        this.riskMllPen = new Pen(Color.FromArgb(150, options.RiskColor), 1.4f)
        { DashStyle = DashStyle.Dot };
        this.resourceKey = key;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.tagFont.Dispose();
        this.riskFont.Dispose();
        this.tagText.Dispose();
        this.tagBack.Dispose();
        this.newsFill?.Dispose();
        this.vwapPen?.Dispose();
        this.anchoredPen?.Dispose();
        this.bandPen?.Dispose();
        this.anchoredBandPen?.Dispose();
        this.riskDllPen?.Dispose();
        this.riskMllPen?.Dispose();
    }
}
