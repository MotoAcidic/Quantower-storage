using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One delta bar the panel renders.</summary>
/// <param name="OpenUtc">The bar's own open time, on the chart's own bar period.</param>
/// <param name="Volume">Total size traded in this bar (buy + sell) — its own row, "volume ...
/// like in the finch-scalping" (the operator's own ask, 2026-09-23).</param>
/// <param name="Delta">Buy volume minus sell volume for this bar alone.</param>
/// <param name="CumulativeAfter">Session cumulative delta after this bar closed (or, for the
/// still-forming bar, as of right now) — anchored at the trading day's own open, 18:00
/// America/New_York, same boundary the large-order feature uses.</param>
internal readonly record struct DeltaBarDraw(DateTime OpenUtc, double Volume, double Delta, double CumulativeAfter);

/// <summary>Immutable paint snapshot: the poll drains ticks into this, the paint reads it.</summary>
/// <param name="Bars">Every kept bar plus the still-forming one.</param>
/// <param name="FlipUtc">The newest bar whose close made session cumulative delta cross zero, or
/// null if no flip has happened yet this session — "so i can tell when the delta flips" (the
/// operator's own ask, 2026-09-23).</param>
/// <param name="FlipIsUp">True if the flip was negative-to-positive.</param>
internal sealed record DeltaDrawable(DeltaBarDraw[] Bars, DateTime? FlipUtc, bool FlipIsUp)
{
    public static readonly DeltaDrawable Empty = new(Array.Empty<DeltaBarDraw>(), null, false);
}

/// <summary>
/// "now i need to see the session delta and delta and volume like in the finch-scalping" (the
/// operator's own ask, 2026-09-23) — three stacked, separately-scaled rows, top to bottom:
/// **VOLUME** (total size traded per bar, one neutral colour, since volume itself has no
/// direction), **DELTA** (buy-minus-sell per bar, the up/down histogram with its own zero line),
/// **SESSION DELTA** (the running session total, drawn as bars from a zero baseline coloured by
/// current sign — "session delta should also be in bars not a white line", the operator's own
/// follow-up, 2026-09-23). This is the SAME three-row split this indicator built once before,
/// then abandoned for a single-band ORB-IX-ported design at the operator's own request — now
/// asked for again by name, so it's back. The flip marker added
/// earlier this same day (a full-pane-height line at the newest sign change of session
/// cumulative delta) is unchanged and independent of the row layout — it still spans the whole
/// pane, not just this band.
/// </summary>
internal sealed class DeltaPanelOverlay : IDisposable
{
    internal readonly record struct Options(
        int HeightPx, Color VolumeColor, Color UpColor, Color DownColor, double BarsWidth,
        bool ShowFlipMarker);

    // Near-opaque background + top border — kept from this project's own earlier fix ("the delta
    // looks very weird and not like my old one"): at low alpha the candles behind the band show
    // through and blend with the histogram — this indicator draws everything on the one price
    // pane by design, so a solid background is what stands in for a genuinely separate panel.
    private readonly SolidBrush background = new(Color.FromArgb(235, 12, 14, 20));
    private readonly Pen borderPen = new(Color.FromArgb(150, Color.Gray), 1f);
    private readonly Pen rowSeparatorPen = new(Color.FromArgb(90, Color.Gray), 1f);
    private readonly Pen zeroPen = new(Color.FromArgb(130, Color.Gray), 1f);
    private readonly Font rowFont = new(FontFamily.GenericSansSerif, 7f, FontStyle.Bold);
    private readonly SolidBrush rowLabelBrush = new(Color.FromArgb(200, Color.Gainsboro));
    private readonly Font flipFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush flipLabelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> flipPens = new();
    private readonly Dictionary<int, SolidBrush> flipLabelBrushes = new();
    private SolidBrush? volumeBrush;
    private SolidBrush? upBrush;
    private SolidBrush? downBrush;
    private (Color Volume, Color Up, Color Down) brushKey;
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, DeltaDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Bars.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var height = Math.Clamp(options.HeightPx, 60, (int)(pane.Height / 2f));
        var band = new RectangleF(pane.Left, pane.Bottom - height, pane.Width, height);
        var rowHeight = band.Height / 3f;
        var volumeRow = new RectangleF(band.Left, band.Top, band.Width, rowHeight);
        var deltaRow = new RectangleF(band.Left, band.Top + rowHeight, band.Width, rowHeight);
        var cumRow = new RectangleF(band.Left, band.Top + (2f * rowHeight), band.Width, rowHeight);

        this.EnsureBrushes(options.VolumeColor, options.UpColor, options.DownColor);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            graphics.FillRectangle(this.background, band);
            graphics.DrawLine(this.borderPen, band.Left, band.Top, band.Right, band.Top);
            graphics.DrawLine(this.rowSeparatorPen, band.Left, deltaRow.Top, band.Right, deltaRow.Top);
            graphics.DrawLine(this.rowSeparatorPen, band.Left, cumRow.Top, band.Right, cumRow.Top);

            var barWidth = Math.Max((float)options.BarsWidth - 1f, 1f);
            var halfBar = (float)(options.BarsWidth / 2.0);
            var visible = new List<(float X, double Volume, double Delta, double Cum)>(drawable.Bars.Length);
            var maxVolume = 0d;
            var maxAbsDelta = 0d;
            var cumMin = double.MaxValue;
            var cumMax = double.MinValue;

            foreach (var bar in drawable.Bars)
            {
                if (!TryX(converter, bar.OpenUtc, out var x))
                    continue;

                x += halfBar;
                if (x < band.Left - barWidth || x > band.Right + barWidth)
                    continue;

                visible.Add((x, bar.Volume, bar.Delta, bar.CumulativeAfter));
                maxVolume = Math.Max(maxVolume, bar.Volume);
                maxAbsDelta = Math.Max(maxAbsDelta, Math.Abs(bar.Delta));
                cumMin = Math.Min(cumMin, bar.CumulativeAfter);
                cumMax = Math.Max(cumMax, bar.CumulativeAfter);
            }

            if (visible.Count > 0)
            {
                // ---- row 1: VOLUME — one neutral-coloured bar per bar, height = total size traded ----
                if (maxVolume > 0)
                {
                    var rowBottom = volumeRow.Bottom - 2f;
                    var rowUsable = volumeRow.Height - 4f;

                    foreach (var (x, volume, _, _) in visible)
                    {
                        var h = (float)(volume / maxVolume * rowUsable);
                        graphics.FillRectangle(
                            this.volumeBrush!, x - (barWidth / 2f), rowBottom - Math.Max(h, 1f),
                            barWidth, Math.Max(h, 1f));
                    }
                }

                // ---- row 2: DELTA — symmetric up/down histogram around this row's OWN zero line ----
                var deltaMid = deltaRow.Top + (deltaRow.Height / 2f);
                var deltaHalf = (deltaRow.Height / 2f) - 4f;
                graphics.DrawLine(this.zeroPen, band.Left, deltaMid, band.Right, deltaMid);

                if (maxAbsDelta > 0)
                {
                    foreach (var (x, _, delta, _) in visible)
                    {
                        var h = (float)(Math.Abs(delta) / maxAbsDelta * deltaHalf);
                        var brush = delta >= 0 ? this.upBrush! : this.downBrush!;
                        graphics.FillRectangle(
                            brush, x - (barWidth / 2f), delta >= 0 ? deltaMid - h : deltaMid,
                            barWidth, Math.Max(h, 1f));
                    }
                }

                // ---- row 3: SESSION DELTA — bars from a zero baseline, coloured by current sign,
                // not a line ("session delta should also be in bars not a white line", the
                // operator's own ask, 2026-09-23) — same visual language as the DELTA row above,
                // just against this row's own [cumMin, cumMax] scale instead of a symmetric one.
                var cumSpan = cumMax - cumMin;
                if (cumSpan > 0)
                {
                    var cumUsable = cumRow.Height - 8f;
                    var zeroY = Math.Clamp(
                        CumY(0), cumRow.Top + 4f, cumRow.Bottom - 4f);

                    graphics.DrawLine(this.zeroPen, band.Left, zeroY, band.Right, zeroY);

                    foreach (var (x, _, _, cum) in visible)
                    {
                        var y = CumY(cum);
                        var top = Math.Min(y, zeroY);
                        var barHeight = Math.Max(Math.Abs(y - zeroY), 1f);
                        var brush = cum >= 0 ? this.upBrush! : this.downBrush!;
                        graphics.FillRectangle(brush, x - (barWidth / 2f), top, barWidth, barHeight);
                    }

                    float CumY(double value) => cumRow.Bottom - 4f
                        - (float)((value - cumMin) / cumSpan * cumUsable);
                }

                graphics.DrawString("VOL", this.rowFont, this.rowLabelBrush, band.Left + 3f, volumeRow.Top + 1f);
                graphics.DrawString("DELTA", this.rowFont, this.rowLabelBrush, band.Left + 3f, deltaRow.Top + 1f);
                graphics.DrawString(
                    "SESSION DELTA", this.rowFont, this.rowLabelBrush, band.Left + 3f, cumRow.Top + 1f);
            }

            if (options.ShowFlipMarker && drawable.FlipUtc is { } flipUtc
                && TryX(converter, flipUtc, out var flipX) && flipX >= pane.Left && flipX <= pane.Right)
            {
                var colour = drawable.FlipIsUp ? options.UpColor : options.DownColor;
                graphics.DrawLine(this.FlipPen(colour), flipX, pane.Top, flipX, pane.Bottom);

                var text = drawable.FlipIsUp ? "FLIP UP" : "FLIP DOWN";
                var size = graphics.MeasureString(text, this.flipFont);
                var rect = new RectangleF(flipX + 3f, pane.Top + 4f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.flipLabelBack, rect);
                    graphics.DrawString(text, this.flipFont, this.FlipLabelBrush(colour), rect.Left + 3f, rect.Top);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private static bool TryX(IChartWindowCoordinatesConverter converter, DateTime utc, out float x)
    {
        x = 0f;

        var raw = converter.GetChartX(utc);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        x = (float)Math.Clamp(raw, -1e6, 1e6);
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

    private void EnsureBrushes(Color volume, Color up, Color down)
    {
        if (this.volumeBrush is not null && this.brushKey == (volume, up, down))
            return;

        this.volumeBrush?.Dispose();
        this.upBrush?.Dispose();
        this.downBrush?.Dispose();

        this.volumeBrush = new SolidBrush(Color.FromArgb(170, volume));
        this.upBrush = new SolidBrush(Color.FromArgb(170, up));
        this.downBrush = new SolidBrush(Color.FromArgb(170, down));
        this.brushKey = (volume, up, down);
    }

    private Pen FlipPen(Color colour)
    {
        var key = colour.ToArgb();

        if (!this.flipPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(200, colour), 1.5f) { DashStyle = DashStyle.Dash };
            this.flipPens[key] = pen;
        }

        return pen;
    }

    private SolidBrush FlipLabelBrush(Color colour)
    {
        var key = colour.ToArgb();

        if (!this.flipLabelBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(colour);
            this.flipLabelBrushes[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.background.Dispose();
        this.borderPen.Dispose();
        this.rowSeparatorPen.Dispose();
        this.zeroPen.Dispose();
        this.rowFont.Dispose();
        this.rowLabelBrush.Dispose();
        this.flipFont.Dispose();
        this.flipLabelBack.Dispose();
        this.volumeBrush?.Dispose();
        this.upBrush?.Dispose();
        this.downBrush?.Dispose();

        foreach (var pen in this.flipPens.Values) pen.Dispose();
        foreach (var brush in this.flipLabelBrushes.Values) brush.Dispose();

        this.flipPens.Clear();
        this.flipLabelBrushes.Clear();
    }
}
