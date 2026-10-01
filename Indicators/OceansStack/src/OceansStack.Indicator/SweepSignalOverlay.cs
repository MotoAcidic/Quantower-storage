using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace OceansStack;

/// <summary>One sweep-and-fail signal — the moment `SweepZoneTracker.OnBarClosed` returned
/// `FiredThisBar`. Kept as its own overlay, separate from <see cref="AbsorptionMarkerOverlay"/>,
/// because `ocean.pine` keeps `plotshape(sigShort/sigLong, ...)` visually and semantically distinct
/// from the plain `absRes`/`absSup` absorption prints — this is the actual trigger, not evidence
/// feeding the score.</summary>
internal readonly record struct SweepSignalDraw(DateTime BarOpenUtc, double Price, bool IsLong);

/// <summary>Immutable paint snapshot: the poll appends to this as signals fire, the paint reads it.</summary>
internal sealed record SweepSignalDrawable(SweepSignalDraw[] Signals)
{
    public static readonly SweepSignalDrawable Empty = new(Array.Empty<SweepSignalDraw>());
}

/// <summary>
/// Reuses Finch-Lite's own <c>BigTradeOverlay</c> marker+adjacent-label composition — a filled
/// circle at the signal bar's own close, with a fixed "SF" (sweep-fail) label beside it. Rarest and
/// loudest of every overlay this indicator draws, so it paints last, on top of everything else (see
/// `OceansStackIndicator.OnPaintChart`'s own layering order).
/// </summary>
internal sealed class SweepSignalOverlay : IDisposable
{
    internal readonly record struct Options(Color ShortColor, Color LongColor);

    private const float Radius = 6f;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Pen markerBorderPen = new(Color.FromArgb(210, 16, 18, 24), 1f);
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly Dictionary<int, SolidBrush> markerBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, SweepSignalDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Signals.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var signal in drawable.Signals)
            {
                if (!TryY(converter, signal.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var colour = signal.IsLong ? options.LongColor : options.ShortColor;
                var hasMarker = TryX(converter, signal.BarOpenUtc, out var x) && x >= pane.Left && x <= pane.Right;
                var labelAnchorX = pane.Left + 4f;

                if (hasMarker)
                {
                    var diameter = Radius * 2f;
                    graphics.FillEllipse(this.MarkerBrush(colour), x - Radius, y - Radius, diameter, diameter);
                    graphics.DrawEllipse(this.markerBorderPen, x - Radius, y - Radius, diameter, diameter);
                    labelAnchorX = x + Radius + 4f;
                }

                const string text = "SF";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(
                    Math.Min(labelAnchorX, pane.Right - size.Width - 6f), y - size.Height - 1f,
                    size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    graphics.DrawString(text, this.font, this.LabelBrush(colour), rect.Left + 3f, rect.Top);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private static bool TryY(
        IChartWindowCoordinatesConverter converter, double price, float top, float bottom, out float y)
    {
        y = 0f;

        if (double.IsNaN(price) || double.IsInfinity(price))
            return false;

        var raw = converter.GetChartY(price);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        y = (float)Math.Clamp(raw, top, bottom);
        return true;
    }

    private static bool TryX(IChartWindowCoordinatesConverter converter, DateTime utc, out float x)
    {
        x = 0f;

        var raw = converter.GetChartX(utc);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        x = (float)raw;
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

    private SolidBrush LabelBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.labelBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(colour);
            this.labelBrushes[key] = brush;
        }

        return brush;
    }

    private SolidBrush MarkerBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.markerBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(215, colour));
            this.markerBrushes[key] = brush;
        }

        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();
        this.markerBorderPen.Dispose();

        foreach (var brush in this.labelBrushes.Values) brush.Dispose();
        foreach (var brush in this.markerBrushes.Values) brush.Dispose();

        this.labelBrushes.Clear();
        this.markerBrushes.Clear();
    }
}
