using System;
using System.Collections.Generic;
using System.Drawing;
using OrbIx.Core.Features;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>One zone resolved to real chart coordinates; the fold writes it, the paint reads it.</summary>
/// <param name="StartUtc">Open of the triggering bar.</param>
/// <param name="EndUtc">Where the zone stops extending — start + the configured bar count.</param>
/// <param name="Top">Upper edge of the zone box.</param>
/// <param name="Bottom">Lower edge of the zone box.</param>
/// <param name="Side">Selling (red, drawn from a bar's high) or Buying (green, from a bar's low).</param>
/// <param name="Strength">volume_strength * wick_strength — shown in the label.</param>
internal readonly record struct WickAbsorptionZoneDraw(
    DateTime StartUtc, DateTime EndUtc, double Top, double Bottom, WickAbsorptionSide Side, double Strength);

/// <summary>Immutable paint snapshot for a frame's worth of zones.</summary>
internal sealed record WickAbsorptionDrawable(WickAbsorptionZoneDraw[] Zones)
{
    public static readonly WickAbsorptionDrawable Empty = new(Array.Empty<WickAbsorptionZoneDraw>());
}

/// <summary>
/// Draws absorption.pine's zones as actual FILLED, BORDERED BOXES — not another dotted line
/// competing with every other dotted line already on the chart (HH/LL support/resistance, the
/// footprint shelf scan, session levels). The user's own complaint was that a chart full of thin
/// dotted lines makes it impossible to tell which one is which; a solid box with its own colour
/// per side reads as its own thing at a glance, matching how absorption.pine itself draws on
/// TradingView (box.new, not a plotted line).
/// </summary>
internal sealed class WickAbsorptionOverlay : IDisposable
{
    internal readonly record struct Options(Color SellingColor, Color BuyingColor, bool ShowLabels);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly Dictionary<Color, SolidBrush> fills = new();
    private readonly Dictionary<Color, Pen> borders = new();
    private readonly Dictionary<Color, SolidBrush> labelBrushes = new();
    private readonly SolidBrush labelBack = new(Color.FromArgb(170, 16, 18, 24));
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, WickAbsorptionDrawable drawable,
        in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Zones.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var zone in drawable.Zones)
            {
                var colour = zone.Side == WickAbsorptionSide.Selling ? options.SellingColor : options.BuyingColor;

                if (!ChartOverlay.TryY(converter, zone.Top, pane.Top, pane.Bottom, out var yTop)
                    || !ChartOverlay.TryY(converter, zone.Bottom, pane.Top, pane.Bottom, out var yBottom)
                    || !ChartOverlay.TryX(converter, zone.StartUtc, out var x1)
                    || !ChartOverlay.TryX(converter, zone.EndUtc, out var x2))
                {
                    continue;
                }

                var left = Math.Max(x1, pane.Left);
                var right = Math.Min(x2, pane.Right);

                if (right <= pane.Left || left >= pane.Right || right <= left)
                    continue;

                var top = Math.Min(yTop, yBottom);
                var height = Math.Max(Math.Abs(yBottom - yTop), 2f);

                graphics.FillRectangle(this.Fill(colour), left, top, right - left, height);
                graphics.DrawRectangle(this.Border(colour), left, top, right - left, height);

                if (options.ShowLabels)
                    this.Label(graphics, zone, colour, left, top, height, labelRegistry);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void Label(
        Graphics graphics, in WickAbsorptionZoneDraw zone, Color colour,
        float left, float top, float height, List<RectangleF> labelRegistry)
    {
        var text = zone.Side == WickAbsorptionSide.Selling
            ? $"SELLING ABSORPTION ({zone.Strength:F1})"
            : $"BUYING ABSORPTION ({zone.Strength:F1})";

        var size = graphics.MeasureString(text, this.font);
        var centreY = zone.Side == WickAbsorptionSide.Selling ? top - size.Height - 2f : top + height + 2f;
        var rect = new RectangleF(left, centreY, size.Width + 6f, size.Height);

        if (!ChartOverlay.TryReserve(labelRegistry, rect))
            return;

        graphics.FillRectangle(this.labelBack, rect);
        graphics.DrawString(text, this.font, this.LabelBrush(colour), rect.Left + 3f, rect.Top);
    }

    private SolidBrush Fill(Color colour)
    {
        if (!this.fills.TryGetValue(colour, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(60, colour));
            this.fills[colour] = brush;
        }

        return brush;
    }

    private Pen Border(Color colour)
    {
        if (!this.borders.TryGetValue(colour, out var pen))
        {
            pen = new Pen(Color.FromArgb(200, colour), 1.5f);
            this.borders[colour] = pen;
        }

        return pen;
    }

    private SolidBrush LabelBrush(Color colour)
    {
        if (!this.labelBrushes.TryGetValue(colour, out var brush))
        {
            brush = new SolidBrush(colour);
            this.labelBrushes[colour] = brush;
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

        foreach (var brush in this.fills.Values)
            brush.Dispose();

        foreach (var pen in this.borders.Values)
            pen.Dispose();

        foreach (var brush in this.labelBrushes.Values)
            brush.Dispose();

        this.fills.Clear();
        this.borders.Clear();
        this.labelBrushes.Clear();
    }
}
