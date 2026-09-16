using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// One high-volume shelf, resolved to a drawable band. Live-built from ticks since the current
/// session opened — never from this connector's refused historical volume-analysis API. See
/// OrbIx.Core.Features.WickVolumeAbsorption's own doc comment and Quantower-storage/CLAUDE.md
/// for the documented refusal this deliberately avoids.
/// </summary>
/// <param name="Bottom">Bottom edge of the shelf.</param>
/// <param name="Top">Top edge of the shelf.</param>
/// <param name="Peak">The shelf's own volume peak — where the label anchors.</param>
internal readonly record struct VolumeNodeDraw(double Bottom, double Top, double Peak);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record VolumeNodeDrawable(VolumeNodeDraw[] Shelves)
{
    public static readonly VolumeNodeDrawable Empty = new(Array.Empty<VolumeNodeDraw>());
}

/// <summary>
/// Draws each live-built high-volume shelf as a filled+bordered box spanning the full pane width
/// — "areas where a lot of orders were placed" (the operator's own phrase, 2026-09-16), meant as
/// bounce-zone reference, not a signal. One colour for every shelf: unlike the Anchor Gate or
/// HH/LL, a volume node has no long/short side of its own — it's a price the tape spent time at,
/// nothing more.
/// </summary>
internal sealed class VolumeNodeOverlay : IDisposable
{
    internal readonly record struct Options(Color Colour);

    private readonly Font font = new(FontFamily.GenericSansSerif, 7.5f, FontStyle.Regular);
    private readonly SolidBrush labelBack = new(Color.FromArgb(170, 16, 18, 24));
    private Color cachedColour;
    private SolidBrush? fill;
    private Pen? border;
    private SolidBrush? labelBrush;
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, VolumeNodeDrawable drawable,
        in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Shelves.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        this.EnsureBrushes(options.Colour);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var shelf in drawable.Shelves)
            {
                if (!ChartOverlay.TryY(converter, shelf.Top, pane.Top, pane.Bottom, out var yTop)
                    || !ChartOverlay.TryY(converter, shelf.Bottom, pane.Top, pane.Bottom, out var yBottom))
                {
                    continue;
                }

                var top = Math.Min(yTop, yBottom);
                var height = Math.Max(Math.Abs(yBottom - yTop), 2f);

                graphics.FillRectangle(this.fill!, pane.Left, top, pane.Width, height);
                graphics.DrawRectangle(this.border!, pane.Left, top, pane.Width, height);

                if (!ChartOverlay.TryY(converter, shelf.Peak, pane.Top, pane.Bottom, out var peakY))
                    continue;

                const string text = "VOLUME NODE";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Left + 4f, peakY - (size.Height / 2f), size.Width + 6f, size.Height);

                if (!ChartOverlay.TryReserve(labelRegistry, rect))
                    continue;

                graphics.FillRectangle(this.labelBack, rect);
                graphics.DrawString(text, this.font, this.labelBrush!, rect.Left + 3f, rect.Top);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void EnsureBrushes(Color colour)
    {
        if (this.fill is not null && this.cachedColour == colour)
            return;

        this.fill?.Dispose();
        this.border?.Dispose();
        this.labelBrush?.Dispose();

        this.cachedColour = colour;
        this.fill = new SolidBrush(Color.FromArgb(35, colour));
        this.border = new Pen(Color.FromArgb(150, colour), 1f) { DashStyle = DashStyle.Dash };
        this.labelBrush = new SolidBrush(colour);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();
        this.fill?.Dispose();
        this.border?.Dispose();
        this.labelBrush?.Dispose();
    }
}
