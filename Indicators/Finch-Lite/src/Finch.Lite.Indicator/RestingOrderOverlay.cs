using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One resting order level currently being tracked, ready to draw.</summary>
/// <param name="Price">The book price this size is resting at.</param>
/// <param name="Size">The number to SHOW — the peak size ever seen here while the level reads as
/// a normal large order, or the size STILL REMAINING once it has started being filled (see
/// <see cref="IsUnfinished"/>). Never shown once the level empties out entirely — a fully filled
/// level is removed from the drawable altogether, not shown at zero.</param>
/// <param name="IsBid">True for a resting bid (a seller would have to hit it to clear it), false
/// for a resting ask (a buyer would have to lift it).</param>
/// <param name="IsUnfinished">
/// True once SOME of the size originally seen here has been consumed but the level has not
/// emptied out completely — "if its an unfinished auction it needs to label that and show how
/// many more contracts are at that unfinished auction" (the operator's own ask, 2026-09-22).
/// False for a level still sitting at its full original size.
/// </param>
/// <param name="Absorbed">
/// Running total of contracts that have traded THROUGH this level while it kept standing —
/// every poll where the size drops but the level does not disappear entirely adds that drop
/// here (a refill counts too: the level was defended, not just slow to finish clearing). Drives
/// the line's colour STRENGTH — "the strength of the color of the bids based on asorbstion were
/// lets say sellers are defending or an area were buyers are defending" (the operator's own
/// ask, 2026-09-22). Zero for a level that has never yet had size taken off it.
/// </param>
/// <param name="FirstSeenUtc">
/// When this level was first flagged. For an unfinished auction, its line and label draw from
/// THIS point forward instead of spanning the whole pane — "the unfished auctions need to be
/// centered just to the right of the candle it comes off of" (the operator's own ask,
/// 2026-09-22), rather than a fixed-screen-edge anchor that says nothing about when the level
/// was actually noticed.
/// </param>
internal readonly record struct RestingOrderDraw(
    double Price, double Size, bool IsBid, bool IsUnfinished, double Absorbed, DateTime FirstSeenUtc);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record RestingOrderDrawable(RestingOrderDraw[] Levels)
{
    public static readonly RestingOrderDrawable Empty = new(Array.Empty<RestingOrderDraw>());
}

/// <summary>
/// Draws a line at every current resting bid/ask at or above the operator's own size threshold —
/// "an area marked out that has a lot of large resting orders" (the operator's own phrase,
/// 2026-09-22). Colour is the operator's own explicit choice, not this codebase's usual
/// bullish/bearish convention: red for a bid (a seller would have to hit it to clear it), green
/// for an ask (a buyer would have to lift it).
///
/// CHANGED 2026-09-22 (same day) — "lets shorten the ask and bids for large orders as well
/// instead of going all the way across the chart lets only go across like the ua does": every
/// line now starts at the level's own origin time (FirstSeenUtc) and extends right to "now",
/// rather than spanning the whole pane from the fixed left edge.
///
/// FIXED 2026-09-22 — "some of the bid are a little hard to view": once levels started
/// persisting for the whole trading day (see FinchLiteIndicator.restingOrderMemory), a busy
/// price band could accumulate many close-together lines whose LABELS drew directly on top of
/// each other, and whose LINES packed tightly enough to read as one solid band rather than
/// distinct levels. Two fixes: labels now reserve their own space and a lower-priority label is
/// dropped rather than drawn illegibly on top of one already placed (same discipline every other
/// overlay in this codebase already uses for label collisions); and a line within
/// <see cref="MinLineGapPx"/> pixels of an already-drawn line on the SAME side is skipped
/// entirely, largest first, so a dense cluster reads as its few biggest levels rather than a
/// solid wall of near-identical adjacent lines.
/// </summary>
internal sealed class RestingOrderOverlay : IDisposable
{
    /// <param name="AbsorptionStrongContracts">How many contracts a level must show as
    /// <see cref="RestingOrderDraw.Absorbed"/> before its line reaches the top absorption tier —
    /// see <see cref="AbsorptionTier"/> for the tier boundaries, all measured as a fraction of
    /// this number.</param>
    /// <param name="LabelInsetPx">Extra right-margin the label reserves before it starts drawing,
    /// so it does not sit on top of the DOM ladder strip — "move that text over some so i can see
    /// the dom more clearer" (the operator's own ask, 2026-09-22).</param>
    ///
    /// REVERTED 2026-09-22 (same day) — "lets color the unfinished auction line the acording
    /// color like we do for the large orders": the white-for-unfinished distinction from earlier
    /// today is gone again; an unfinished auction now draws in the same bid/ask colour as an
    /// ordinary large order (see the class-level colour convention above), distinguished instead
    /// by its shortened "UA" label text and its own line origin (see FirstSeenUtc).
    internal readonly record struct Options(
        Color BidColor, Color AskColor, double AbsorptionStrongContracts, float LabelInsetPx);

    /// <summary>Lines on the same side closer together than this many pixels are treated as one
    /// cluster — only the largest in the cluster draws.</summary>
    private const float MinLineGapPx = 6f;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<(int Argb, float Width), Pen> pens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly List<float> drawnBidY = new();
    private readonly List<float> drawnAskY = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, RestingOrderDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Levels.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        this.drawnBidY.Clear();
        this.drawnAskY.Clear();

        // Largest first: when a dense cluster has to give something up, the level that gives up
        // the least information is the smallest one nobody would have picked out anyway.
        var ordered = new List<RestingOrderDraw>(drawable.Levels);
        ordered.Sort(static (a, b) => b.Size.CompareTo(a.Size));

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var level in ordered)
            {
                if (!TryY(converter, level.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var drawnY = level.IsBid ? this.drawnBidY : this.drawnAskY;
                var tooClose = false;

                foreach (var existing in drawnY)
                {
                    if (Math.Abs(existing - y) < MinLineGapPx)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (tooClose)
                    continue;

                drawnY.Add(y);

                // Same bid/ask colour convention for both categories now — see the Options
                // doc comment for why the earlier white-for-unfinished distinction is gone.
                var colour = level.IsBid ? options.BidColor : options.AskColor;

                var fraction = options.AbsorptionStrongContracts > 0
                    ? level.Absorbed / options.AbsorptionStrongContracts
                    : 0.0;
                var (tierAlpha, tierWidth) = AbsorptionTier(fraction);

                // FIXED 2026-09-22 — first for unfinished auctions only ("the unfished auctions
                // need to be centered just to the right of the candle it comes off of"), now
                // extended to EVERY large-order line ("lets shorten the ask and bids for large
                // orders as well instead of going all the way across the chart lets only go
                // across like the ua does"): every line now starts at its own origin time
                // (FirstSeenUtc) and extends right to "now" — the same "still extending"
                // convention this codebase already uses for live zones elsewhere — instead of
                // spanning the whole pane from the fixed left edge.
                var lineStartX = (float)pane.Left;
                if (TryX(converter, level.FirstSeenUtc, pane.Left, pane.Right, out var originX))
                    lineStartX = originX;

                graphics.DrawLine(this.Pen(colour, tierAlpha, tierWidth), lineStartX, y, pane.Right, y);

                // SHORTENED 2026-09-22 — "lets shorten the words for unfinished auctions of UA -
                // Bid and what not" (the operator's own ask): "UNFINISHED AUCTION — BID N LEFT"
                // is now just "UA - BID N".
                var text = level.IsUnfinished
                    ? $"UA - {(level.IsBid ? "BID" : "ASK")} {level.Size:N0}"
                    : $"{(level.IsBid ? "BID" : "ASK")} {level.Size:N0}";
                var size = graphics.MeasureString(text, this.font);

                // An unfinished auction's label sits just to the right of where its own line
                // starts (off the candle it came from), clamped so it never runs past the pane's
                // own right edge; an ordinary large-order label keeps anchoring near the right
                // edge, inset clear of the DOM ladder (fixed earlier this same day).
                var rect = level.IsUnfinished
                    ? new RectangleF(
                        Math.Min(lineStartX + 4f, pane.Right - size.Width - 6f), y - size.Height - 1f,
                        size.Width + 6f, size.Height)
                    : new RectangleF(
                        pane.Right - options.LabelInsetPx - size.Width - 10f, y - size.Height - 1f,
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

    /// <summary>Converts a price to a pixel row, clamped to the pane. False on a coordinate the
    /// converter could not produce a finite value for (a chart still initialising).</summary>
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

    /// <summary>Converts a UTC time to a pixel column, clamped to the pane. False on a coordinate
    /// the converter could not produce a finite value for.</summary>
    private static bool TryX(
        IChartWindowCoordinatesConverter converter, DateTime utc, float left, float right, out float x)
    {
        x = left;

        var raw = converter.GetChartX(utc);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        x = (float)Math.Clamp(raw, left, right);
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

    /// <summary>
    /// TIERED 2026-09-22 — "lets work on adding tiered absorbtion that is color cordinated": the
    /// smooth continuous fade this used to be made two adjacent levels differ by an alpha value
    /// nobody could actually distinguish at a glance. Five fixed tiers now, each a visibly
    /// distinct step of the SAME bid/ask hue (side stays unambiguous — this project already
    /// settled bid=green/ask=red earlier today and a tiering scheme has no reason to muddy that):
    /// fresh (never absorbed anything) is faint, each quarter of
    /// <see cref="Options.AbsorptionStrongContracts"/> the level has absorbed steps the line to
    /// the next tier, and reaching the full configured amount both maxes the colour AND thickens
    /// the line — the one tier that gets an extra visual cue, since "this level has fully proven
    /// itself" is worth more than one more alpha step can say on its own.
    /// </summary>
    private static (int Alpha, float Width) AbsorptionTier(double fraction) => fraction switch
    {
        >= 1.00 => (255, 2.5f), // maxed out — proven, thicker line
        >= 0.75 => (210, 1.5f), // strong
        >= 0.50 => (170, 1.5f), // moderate
        >= 0.25 => (120, 1.5f), // light
        _ => (70, 1.5f),        // fresh
    };

    private Pen Pen(Color baseColour, int alpha, float width)
    {
        var blended = Color.FromArgb(alpha, baseColour);
        var key = (blended.ToArgb(), width);

        if (!this.pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(blended, width);
            this.pens[key] = pen;
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

        foreach (var pen in this.pens.Values) pen.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.pens.Clear();
        this.labelBrushes.Clear();
    }
}
