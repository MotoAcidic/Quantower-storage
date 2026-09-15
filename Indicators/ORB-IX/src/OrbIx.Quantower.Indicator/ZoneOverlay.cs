using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OrbIx.Core.Structure;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>One HTF zone, resolved to time anchors by the fold.</summary>
internal readonly record struct ZoneDraw(
    ZoneKind Kind, bool IsBullish, DateTime StartUtc, DateTime? EndUtc,
    double Top, double Bottom, ZoneState State);

/// <summary>One rejection block on a chart-TF bar.</summary>
internal readonly record struct RbDraw(
    DateTime BarOpenUtc, bool IsBullish, double Top, double Bottom,
    double Mid, double? Delta);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record ZoneDrawable(ZoneDraw[] Zones, RbDraw[] Blocks, string Status)
{
    public static readonly ZoneDrawable Empty =
        new(Array.Empty<ZoneDraw>(), Array.Empty<RbDraw>(), string.Empty);
}

/// <summary>One delta bar the panel renders.</summary>
internal readonly record struct DeltaBarDraw(DateTime OpenUtc, double Delta, double Cum);

/// <summary>Immutable paint snapshot for the delta panel.</summary>
internal sealed record DeltaDrawable(
    DeltaBarDraw[] Bars, DateTime[] BearishDivergences,
    DateTime[] BullishDivergences, string Status)
{
    public static readonly DeltaDrawable Empty = new(
        Array.Empty<DeltaBarDraw>(), Array.Empty<DateTime>(),
        Array.Empty<DateTime>(), string.Empty);
}

/// <summary>
/// Renders the HTF zone engine (FVG / order blocks, pinned plan
/// 2026-08-28) and the chart-TF rejection blocks. Same disciplines as
/// <see cref="HhLlOverlay"/>: guarded coordinate conversion through
/// <see cref="ChartOverlay.TryX"/>/<see cref="ChartOverlay.TryY"/>, pane
/// clipping restored in a finally, cached pens/brushes, labels through the
/// frame's shared collision registry (skipping beats overlapping).
///
/// DISPLAY ONLY: the level-retest entry class is measured negative here
/// (levels-null library; ORB-IX replay P2). The status line carries that
/// sentence onto the chart so the drawing can never outrun the evidence.
/// </summary>
internal sealed class ZoneOverlay : IDisposable
{
    internal readonly record struct Options(
        Color BullColor, Color BearColor, bool ShowRejectionBlocks,
        double BarsWidth);

    private const int FreshAlpha = 70;
    private const int TouchedAlpha = 35;
    private const int MitigatedAlpha = 16;
    private const float RbMidTailPx = 48f;

    private readonly Font tagFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly Dictionary<(Color, int), SolidBrush> fills = new();
    private readonly Dictionary<Color, Pen> borders = new();
    private Pen? rbMidPen;
    private Color rbMidPenColor;
    private readonly SolidBrush tagText = new(Color.Gainsboro);
    private readonly SolidBrush tagBack = new(Color.FromArgb(160, 24, 26, 32));
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window,
                     ZoneDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed
            || (drawable.Zones.Length == 0 && drawable.Blocks.Length == 0))
        {
            return;
        }

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        float halfBar = (float)(options.BarsWidth / 2.0);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var zone in drawable.Zones)
            {
                var color = zone.IsBullish ? options.BullColor : options.BearColor;
                int alpha = zone.State switch
                {
                    ZoneState.Fresh => FreshAlpha,
                    ZoneState.Touched => TouchedAlpha,
                    _ => MitigatedAlpha,
                };

                if (!this.TryZoneRect(converter, pane, zone.StartUtc, zone.EndUtc,
                                      zone.Top, zone.Bottom, halfBar, out var rect))
                {
                    continue;
                }

                graphics.FillRectangle(this.Fill(color, alpha), rect);

                // Order blocks read as bordered frames; FVGs as pure fills.
                if (zone.Kind == ZoneKind.OrderBlock)
                    graphics.DrawRectangle(this.Border(color), rect.X, rect.Y, rect.Width, rect.Height);

                if (zone.State == ZoneState.Fresh)
                {
                    string text = zone.Kind == ZoneKind.FairValueGap ? "FVG" : "OB";
                    this.DrawTag(graphics, labelRegistry, text,
                                 rect.Left + 2f, rect.Top + 1f);
                }
            }

            if (options.ShowRejectionBlocks)
            {
                foreach (var block in drawable.Blocks)
                {
                    var color = block.IsBullish ? options.BullColor : options.BearColor;

                    // A rejection block is a POINT-IN-TIME object: when its
                    // bar is off the pane it is SKIPPED, never clamped — the
                    // zone-style clamp turned every scrolled-off block into a
                    // phantom box pinned to the left edge (measured on the
                    // 2026-08-28 first-deploy screenshots).
                    if (!ChartOverlay.TryX(converter, block.BarOpenUtc, out var xBar))
                        continue;

                    float barWidth = Math.Max((float)options.BarsWidth, 2f);
                    if (xBar + barWidth < pane.Left || xBar > pane.Right)
                        continue;

                    if (!ChartOverlay.TryY(converter, block.Top, pane.Top, pane.Bottom, out var yTop)
                        || !ChartOverlay.TryY(converter, block.Bottom, pane.Top, pane.Bottom, out var yBottom))
                    {
                        continue;
                    }

                    if ((yTop <= pane.Top && yBottom <= pane.Top)
                        || (yTop >= pane.Bottom && yBottom >= pane.Bottom))
                    {
                        continue;
                    }

                    var rect = new RectangleF(
                        xBar, yTop, barWidth, Math.Max(yBottom - yTop, 2f));
                    graphics.FillRectangle(this.Fill(color, FreshAlpha), rect);
                    graphics.DrawRectangle(this.Border(color), rect.X, rect.Y, rect.Width, rect.Height);

                    if (ChartOverlay.TryY(converter, block.Mid, pane.Top, pane.Bottom, out var yMid)
                        && yMid > pane.Top && yMid < pane.Bottom)
                    {
                        float x2 = Math.Min(rect.Right + RbMidTailPx, pane.Right);
                        graphics.DrawLine(this.RbMidPen(color), rect.Left, yMid, x2, yMid);

                        string tag = block.Delta is { } delta
                            ? $"RB 50% Δ{delta:+0;-0}"
                            : "RB 50%";
                        this.DrawTag(graphics, labelRegistry, tag, x2 + 2f, yMid - 7f);
                    }
                }
            }

            if (drawable.Status.Length != 0)
            {
                this.DrawTag(graphics, labelRegistry, drawable.Status,
                             pane.Left + 6f, pane.Bottom - 34f);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private bool TryZoneRect(
        IChartWindowCoordinatesConverter converter, RectangleF pane,
        DateTime startUtc, DateTime? endUtc, double top, double bottom,
        float halfBar, out RectangleF rect)
    {
        rect = default;

        if (!ChartOverlay.TryY(converter, top, pane.Top, pane.Bottom, out var yTop)
            || !ChartOverlay.TryY(converter, bottom, pane.Top, pane.Bottom, out var yBottom))
        {
            return false;
        }

        // Wholly off-scale (ChartOverlay.DrawOne's rule).
        if ((yTop <= pane.Top && yBottom <= pane.Top)
            || (yTop >= pane.Bottom && yBottom >= pane.Bottom))
        {
            return false;
        }

        if (!ChartOverlay.TryX(converter, startUtc, out var xStart))
            return false;

        float xEnd;
        if (endUtc is { } end)
        {
            if (!ChartOverlay.TryX(converter, end, out xEnd))
                return false;
            xEnd += halfBar;
        }
        else
        {
            xEnd = pane.Right;
        }

        float left = Math.Max(xStart - halfBar, pane.Left);
        float right = Math.Min(Math.Max(xEnd, left + 1f), pane.Right);

        if (right <= pane.Left || left >= pane.Right)
            return false;

        rect = new RectangleF(
            left, yTop, right - left, Math.Max(yBottom - yTop, 2f));
        return true;
    }

    private void DrawTag(Graphics graphics, List<RectangleF> registry,
                         string text, float x, float y)
    {
        var size = graphics.MeasureString(text, this.tagFont);
        var rect = new RectangleF(x - 3f, y, size.Width + 6f, size.Height);

        if (!ChartOverlay.TryReserve(registry, rect))
            return;

        graphics.FillRectangle(this.tagBack, rect);
        graphics.DrawString(text, this.tagFont, this.tagText, x, y);
    }

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

    private Pen Border(Color color)
    {
        if (!this.borders.TryGetValue(color, out var pen))
        {
            pen = new Pen(Color.FromArgb(150, color), 1f);
            this.borders[color] = pen;
        }

        return pen;
    }

    private Pen RbMidPen(Color color)
    {
        if (this.rbMidPen is null || this.rbMidPenColor != color)
        {
            this.rbMidPen?.Dispose();
            this.rbMidPen = new Pen(color, 1f) { DashStyle = DashStyle.Dash };
            this.rbMidPenColor = color;
        }

        return this.rbMidPen;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.tagFont.Dispose();
        this.tagText.Dispose();
        this.tagBack.Dispose();
        this.rbMidPen?.Dispose();
        foreach (var brush in this.fills.Values)
            brush.Dispose();
        foreach (var pen in this.borders.Values)
            pen.Dispose();
        this.fills.Clear();
        this.borders.Clear();
    }
}

/// <summary>
/// The delta panel: a reserved band at the bottom of the main pane with a
/// per-bar delta histogram, the session-anchored cumulative delta
/// polyline, divergence markers, and the status line naming the data
/// source and its measured limits. DISPLAY ONLY — trial 018 measured
/// delta-agreement anti-predictive on MNQ 1m, and the status line says so
/// on the chart itself.
/// </summary>
internal sealed class DeltaPanelOverlay : IDisposable
{
    internal readonly record struct Options(
        int HeightPx, Color UpColor, Color DownColor, double BarsWidth);

    private readonly Font statusFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush background = new(Color.FromArgb(140, 16, 18, 24));
    private readonly SolidBrush statusText = new(Color.Gainsboro);
    private readonly SolidBrush bearMark = new(Color.FromArgb(0xFF, 0x52, 0x52));
    private readonly SolidBrush bullMark = new(Color.FromArgb(0x00, 0xE6, 0x76));
    private SolidBrush? upBrush;
    private SolidBrush? downBrush;
    private Pen? cumPen;
    private Pen? zeroPen;
    private (Color Up, Color Down) brushKey;
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window,
                     DeltaDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Bars.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        float height = Math.Clamp(options.HeightPx, 40, (int)(pane.Height / 2f));
        var band = new RectangleF(pane.Left, pane.Bottom - height, pane.Width, height);

        this.EnsureResources(options);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            graphics.FillRectangle(this.background, band);
            // The band's pixels are spoken for: chips and tags must dodge it.
            ChartOverlay.TryReserve(labelRegistry, band);

            // Visible bars and their scales, one pass.
            float barWidth = Math.Max((float)options.BarsWidth - 1f, 1f);
            float halfBar = (float)(options.BarsWidth / 2.0);
            var visible = new List<(float X, double Delta, double Cum)>(drawable.Bars.Length);
            double maxAbsDelta = 0;
            double cumMin = double.MaxValue, cumMax = double.MinValue;

            foreach (var bar in drawable.Bars)
            {
                if (!ChartOverlay.TryX(converter, bar.OpenUtc, out var x))
                    continue;
                x += halfBar;
                if (x < band.Left - barWidth || x > band.Right + barWidth)
                    continue;

                visible.Add((x, bar.Delta, bar.Cum));
                maxAbsDelta = Math.Max(maxAbsDelta, Math.Abs(bar.Delta));
                cumMin = Math.Min(cumMin, bar.Cum);
                cumMax = Math.Max(cumMax, bar.Cum);
            }

            if (visible.Count == 0)
                return;

            float mid = band.Top + (band.Height / 2f);
            float histHalf = (band.Height / 2f) - 12f;
            graphics.DrawLine(this.zeroPen!, band.Left, mid, band.Right, mid);

            if (maxAbsDelta > 0)
            {
                foreach (var (x, delta, _) in visible)
                {
                    float h = (float)(Math.Abs(delta) / maxAbsDelta * histHalf);
                    var brush = delta >= 0 ? this.upBrush! : this.downBrush!;
                    graphics.FillRectangle(
                        brush, x - (barWidth / 2f),
                        delta >= 0 ? mid - h : mid, barWidth, Math.Max(h, 1f));
                }
            }

            double cumSpan = cumMax - cumMin;
            if (visible.Count >= 2 && cumSpan > 0)
            {
                var points = new PointF[visible.Count];
                for (int i = 0; i < visible.Count; i++)
                {
                    float y = band.Bottom - 6f
                        - (float)((visible[i].Cum - cumMin) / cumSpan * (band.Height - 12f));
                    points[i] = new PointF(visible[i].X, y);
                }

                graphics.DrawLines(this.cumPen!, points);
            }

            this.DrawDivergences(graphics, converter, band, halfBar,
                                 drawable.BearishDivergences, this.bearMark, band.Top + 2f);
            this.DrawDivergences(graphics, converter, band, halfBar,
                                 drawable.BullishDivergences, this.bullMark, band.Bottom - 8f);

            if (drawable.Status.Length != 0)
            {
                graphics.DrawString(drawable.Status, this.statusFont,
                                    this.statusText, band.Left + 6f, band.Top + 2f);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawDivergences(
        Graphics graphics, IChartWindowCoordinatesConverter converter,
        RectangleF band, float halfBar, DateTime[] instants,
        SolidBrush brush, float y)
    {
        foreach (var instant in instants)
        {
            if (!ChartOverlay.TryX(converter, instant, out var x))
                continue;
            x += halfBar;
            if (x < band.Left || x > band.Right)
                continue;

            graphics.FillRectangle(brush, x - 3f, y, 6f, 6f);
        }
    }

    private void EnsureResources(in Options options)
    {
        var key = (options.UpColor, options.DownColor);
        if (this.upBrush is not null && key == this.brushKey)
            return;

        this.upBrush?.Dispose();
        this.downBrush?.Dispose();
        this.cumPen?.Dispose();
        this.zeroPen?.Dispose();

        this.upBrush = new SolidBrush(Color.FromArgb(170, options.UpColor));
        this.downBrush = new SolidBrush(Color.FromArgb(170, options.DownColor));
        this.cumPen = new Pen(Color.Gainsboro, 1.5f);
        this.zeroPen = new Pen(Color.FromArgb(90, Color.Gray), 1f);
        this.brushKey = key;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.statusFont.Dispose();
        this.background.Dispose();
        this.statusText.Dispose();
        this.bearMark.Dispose();
        this.bullMark.Dispose();
        this.upBrush?.Dispose();
        this.downBrush?.Dispose();
        this.cumPen?.Dispose();
        this.zeroPen?.Dispose();
    }
}
