using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace OceansStack;

/// <summary>One bar `AbsorptionTracker` flagged as an absorption print, ready to mark.</summary>
/// <param name="IsResistanceSide">True = a resistance-side print (big buy-side aggression absorbed,
/// closed back down — Pine's own `absRes`, marked above the bar). False = support-side (Pine's own
/// `absSup`, marked below the bar).</param>
internal readonly record struct AbsorptionMarkerDraw(DateTime BarOpenUtc, double Price, bool IsResistanceSide);

/// <summary>Immutable paint snapshot: the poll appends to this as bars close, the paint reads it.</summary>
internal sealed record AbsorptionMarkerDrawable(AbsorptionMarkerDraw[] Markers)
{
    public static readonly AbsorptionMarkerDrawable Empty = new(Array.Empty<AbsorptionMarkerDraw>());
}

/// <summary>
/// Generalizes Finch-Lite's own <c>BigTradeOverlay</c> marker (a filled circle at a print's own
/// time/price) down to its simplest form: fixed small radius, no label at all — matches
/// `ocean.pine`'s own bare `plotshape` calls for `absRes`/`absSup`, which carry no text.
/// </summary>
internal sealed class AbsorptionMarkerOverlay : IDisposable
{
    internal readonly record struct Options(Color ResistanceColor, Color SupportColor);

    private const float Radius = 3.5f;

    private readonly Pen markerBorderPen = new(Color.FromArgb(210, 16, 18, 24), 1f);
    private readonly Dictionary<int, SolidBrush> markerBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, AbsorptionMarkerDrawable drawable, in Options options)
    {
        if (this.disposed || drawable.Markers.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var marker in drawable.Markers)
            {
                if (!TryY(converter, marker.Price, pane.Top, pane.Bottom, out var y))
                    continue;
                if (!TryX(converter, marker.BarOpenUtc, out var x) || x < pane.Left || x > pane.Right)
                    continue;

                var colour = marker.IsResistanceSide ? options.ResistanceColor : options.SupportColor;
                var diameter = Radius * 2f;
                graphics.FillEllipse(this.MarkerBrush(colour), x - Radius, y - Radius, diameter, diameter);
                graphics.DrawEllipse(this.markerBorderPen, x - Radius, y - Radius, diameter, diameter);
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
        this.markerBorderPen.Dispose();

        foreach (var brush in this.markerBrushes.Values) brush.Dispose();
        this.markerBrushes.Clear();
    }
}
