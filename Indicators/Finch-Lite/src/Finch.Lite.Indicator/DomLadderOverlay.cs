using System;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One resting level, whatever its size — unlike <see cref="RestingOrderDraw"/> this
/// carries EVERY scanned level, not just the ones above the large-order threshold.</summary>
internal readonly record struct DomBarDraw(double Price, double Size, bool IsBid);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
/// <param name="Bars">Every level currently scanned, both sides.</param>
/// <param name="MaxSize">The single largest size across BOTH sides — bar length is a comparison,
/// so every bar shares one scale rather than each side scaling against its own max (which would
/// make an equally-sized bid and ask draw as different lengths).</param>
internal sealed record DomLadderDrawable(DomBarDraw[] Bars, double MaxSize)
{
    public static readonly DomLadderDrawable Empty = new(Array.Empty<DomBarDraw>(), 0);
}

/// <summary>
/// "Is there a way to show like a dom on the right hand side thats a bar so i can tell all
/// resting orders" (the operator's own ask, 2026-09-22) — a bar-length ladder along the pane's
/// right edge, one bar per scanned book level, so the FULL depth picture reads at a glance
/// instead of only the levels that cleared the large-order threshold. Same red-bid/green-ask
/// colouring as the large-order lines, at lower opacity so the highlighted large-order lines
/// still read as the louder signal sitting on top of this quieter backdrop.
/// </summary>
internal sealed class DomLadderOverlay : IDisposable
{
    internal readonly record struct Options(Color BidColor, Color AskColor, float StripWidth, float RowHeight);

    private SolidBrush? bidBrush;
    private SolidBrush? askBrush;
    private (Color Bid, Color Ask) brushKey;
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, DomLadderDrawable drawable, in Options options)
    {
        if (this.disposed || drawable.Bars.Length == 0 || drawable.MaxSize <= 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        this.EnsureBrushes(options.BidColor, options.AskColor);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            var halfRow = options.RowHeight / 2f;
            var maxWidth = Math.Min(options.StripWidth, pane.Width);

            foreach (var bar in drawable.Bars)
            {
                if (!TryY(converter, bar.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var width = (float)(bar.Size / drawable.MaxSize) * maxWidth;
                if (width < 1f)
                    width = 1f;

                var brush = bar.IsBid ? this.bidBrush! : this.askBrush!;
                graphics.FillRectangle(brush, pane.Right - width, y - halfRow, width, options.RowHeight);
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

    private void EnsureBrushes(Color bid, Color ask)
    {
        if (this.bidBrush is not null && this.brushKey == (bid, ask))
            return;

        this.bidBrush?.Dispose();
        this.askBrush?.Dispose();

        // Lower opacity than the large-order lines' own colour — this is the quiet backdrop,
        // not the highlighted signal. LOWERED again 2026-09-22 ("some of the bid are a little
        // hard to view"): a busy price band can stack many adjacent rows on top of each other,
        // and GDI+ does not cap accumulated alpha — several semi-transparent rectangles drawn on
        // top of one another get MORE opaque, not less, so a dense cluster was washing out into
        // one solid, undifferentiated block. 70 leaves individual rows still visibly distinct
        // even where several overlap.
        this.bidBrush = new SolidBrush(Color.FromArgb(70, bid));
        this.askBrush = new SolidBrush(Color.FromArgb(70, ask));
        this.brushKey = (bid, ask);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.bidBrush?.Dispose();
        this.askBrush?.Dispose();
    }
}
