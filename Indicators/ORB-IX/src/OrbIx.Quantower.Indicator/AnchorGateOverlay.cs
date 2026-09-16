using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OceansAnchor;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// One zone resolved to a drawable state, at the price it's anchored to.
/// </summary>
/// <param name="Price">The zone's own level (HH/LL price this indicator built the zone from).</param>
/// <param name="IsLong">Whether this is the support/long-side zone or the resistance/short one.</param>
/// <param name="State">Dormant is never passed here — see <see cref="AnchorGateOverlay"/>.</param>
/// <param name="ClusterLow">The confirming print's stop-reference low, once one has fired.</param>
/// <param name="ClusterHigh">The confirming print's stop-reference high, once one has fired.</param>
internal readonly record struct AnchorGateZoneDraw(
    double Price, bool IsLong, SignalState State, double? ClusterLow, double? ClusterHigh);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record AnchorGateDrawable(AnchorGateZoneDraw[] Zones)
{
    public static readonly AnchorGateDrawable Empty = new(Array.Empty<AnchorGateZoneDraw>());
}

/// <summary>
/// Draws Ocean's Anchor's absorption gate: a line at the zone's level, coloured and labelled by
/// its current state (Armed/Triggered/Confirmed), with the confirming print's stop reference
/// shown once one has fired. See OrbIx.Core... no — see Ported/src/oceans-anchor for the state
/// machine this reads (AnchorState.cs's SignalEngine); this type only paints whatever it is
/// handed, same discipline as every other overlay here.
/// </summary>
internal sealed class AnchorGateOverlay : IDisposable
{
    internal readonly record struct Options(Color LongColor, Color ShortColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly Dictionary<int, Pen> solidLines = new();
    private readonly Dictionary<int, Pen> dashedLines = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, AnchorGateDrawable drawable,
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
                if (zone.State == SignalState.Dormant)
                    continue;

                if (!ChartOverlay.TryY(converter, zone.Price, pane.Top, pane.Bottom, out var y)
                    || y <= pane.Top || y >= pane.Bottom)
                {
                    continue;
                }

                var colour = zone.IsLong ? options.LongColor : options.ShortColor;
                var live = zone.State is SignalState.Armed or SignalState.Triggered or SignalState.Confirmed;
                var pen = live ? this.Solid(colour) : this.Dashed(colour);

                graphics.DrawLine(pen, pane.Left, y, pane.Right, y);

                var text = $"{(zone.IsLong ? "LONG" : "SHORT")} {DescribeState(zone.State)}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 2f, size.Width + 6f, size.Height + 2f);

                if (!ChartOverlay.TryReserve(labelRegistry, rect))
                    continue;

                graphics.FillRectangle(this.labelBack, rect);
                graphics.DrawString(text, this.font, this.LabelBrush(colour), rect.Left + 3f, rect.Top + 1f);

                // The stop reference, once a print has actually confirmed one — this is the
                // price the trade's premise breaks at, not a cosmetic extra.
                if (zone.State == SignalState.Confirmed && zone.ClusterLow is { } lo && zone.ClusterHigh is { } hi)
                {
                    var stopPrice = zone.IsLong ? lo : hi;

                    if (ChartOverlay.TryY(converter, stopPrice, pane.Top, pane.Bottom, out var stopY)
                        && stopY > pane.Top && stopY < pane.Bottom)
                    {
                        graphics.DrawLine(this.Dashed(colour), pane.Right - 120f, stopY, pane.Right, stopY);
                    }
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private static string DescribeState(SignalState state) => state switch
    {
        SignalState.Armed => "ARMED",
        SignalState.Triggered => "TRIGGERED",
        SignalState.Confirmed => "CONFIRMED",
        SignalState.Expired => "expired",
        SignalState.Broken => "broken",
        _ => "—",
    };

    private Pen Solid(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.solidLines.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, 1.5f) { DashStyle = DashStyle.Solid };
            this.solidLines[key] = pen;
        }

        return pen;
    }

    private Pen Dashed(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.dashedLines.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(140, colour), 1f) { DashStyle = DashStyle.Dot };
            this.dashedLines[key] = pen;
        }

        return pen;
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

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();

        foreach (var pen in this.solidLines.Values) pen.Dispose();
        foreach (var pen in this.dashedLines.Values) pen.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.solidLines.Clear();
        this.dashedLines.Clear();
        this.labelBrushes.Clear();
    }
}
