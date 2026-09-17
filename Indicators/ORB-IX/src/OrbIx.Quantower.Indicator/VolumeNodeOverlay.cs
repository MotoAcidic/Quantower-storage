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
/// <param name="IsNoEntry">
/// True when the CURRENT price sits inside this shelf (computed in BuildVolumeNodeDrawable
/// against this.lastPrice each fold) — the operator's own "don't enter in this area" ask
/// (2026-09-16): heavy two-way volume already traded here, a worse spot to open a fresh
/// position than a clean level. Only the shelf price is actually inside gets flagged, not
/// every shelf on the chart.
/// </param>
internal readonly record struct VolumeNodeDraw(double Bottom, double Top, double Peak, bool IsNoEntry = false);

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
    internal readonly record struct Options(Color Colour, Color NoEntryColour, bool NoEntryEnabled);

    private readonly Font font = new(FontFamily.GenericSansSerif, 7.5f, FontStyle.Regular);
    private readonly Font noEntryFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(170, 16, 18, 24));
    private Color cachedColour;
    private Color cachedNoEntryColour;
    private SolidBrush? fill;
    private Pen? border;
    private SolidBrush? labelBrush;
    private SolidBrush? noEntryFill;
    private Pen? noEntryBorder;
    private SolidBrush? noEntryLabelBrush;
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

        this.EnsureBrushes(options.Colour, options.NoEntryColour);

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
                var flagged = shelf.IsNoEntry && options.NoEntryEnabled;

                graphics.FillRectangle(flagged ? this.noEntryFill! : this.fill!, pane.Left, top, pane.Width, height);
                graphics.DrawRectangle(flagged ? this.noEntryBorder! : this.border!, pane.Left, top, pane.Width, height);

                if (!ChartOverlay.TryY(converter, shelf.Peak, pane.Top, pane.Bottom, out var peakY))
                    continue;

                var text = flagged ? "DON'T ENTER HERE — VOLUME NODE" : "VOLUME NODE";
                var font = flagged ? this.noEntryFont : this.font;
                var brush = flagged ? this.noEntryLabelBrush! : this.labelBrush!;
                var size = graphics.MeasureString(text, font);
                var rect = new RectangleF(pane.Left + 4f, peakY - (size.Height / 2f), size.Width + 6f, size.Height);

                if (!ChartOverlay.TryReserve(labelRegistry, rect))
                    continue;

                graphics.FillRectangle(this.labelBack, rect);
                graphics.DrawString(text, font, brush, rect.Left + 3f, rect.Top);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void EnsureBrushes(Color colour, Color noEntryColour)
    {
        if (this.fill is null || this.cachedColour != colour)
        {
            this.fill?.Dispose();
            this.border?.Dispose();
            this.labelBrush?.Dispose();

            this.cachedColour = colour;
            this.fill = new SolidBrush(Color.FromArgb(35, colour));
            this.border = new Pen(Color.FromArgb(150, colour), 1f) { DashStyle = DashStyle.Dash };
            this.labelBrush = new SolidBrush(colour);
        }

        if (this.noEntryFill is null || this.cachedNoEntryColour != noEntryColour)
        {
            this.noEntryFill?.Dispose();
            this.noEntryBorder?.Dispose();
            this.noEntryLabelBrush?.Dispose();

            this.cachedNoEntryColour = noEntryColour;
            this.noEntryFill = new SolidBrush(Color.FromArgb(60, noEntryColour));
            this.noEntryBorder = new Pen(Color.FromArgb(210, noEntryColour), 1.5f) { DashStyle = DashStyle.Solid };
            this.noEntryLabelBrush = new SolidBrush(noEntryColour);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.noEntryFont.Dispose();
        this.labelBack.Dispose();
        this.fill?.Dispose();
        this.border?.Dispose();
        this.labelBrush?.Dispose();
        this.noEntryFill?.Dispose();
        this.noEntryBorder?.Dispose();
        this.noEntryLabelBrush?.Dispose();
    }
}
