using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace OceansStack;

/// <summary>Where a zone currently sits in the sweep-and-fail state machine — drives box fill/border
/// colour. <see cref="Fired"/> reads directionally (top=short=red family, bottom=long=green family)
/// via <see cref="ZoneStatusDraw.IsTop"/>; the other four states share one colour regardless of
/// direction, matching how `ocean.pine`'s own zone colouring reads as "how close is this to firing,"
/// not "which side."</summary>
internal enum ZoneState { EdgeOnly, ScoreTwoPlus, Armed, Swept, Fired }

/// <summary>One zone (top/short or bottom/long) ready to draw, for the CURRENT day only — a zone
/// resets every RTH session, so unlike the value-area lines there is no retained-day history here.</summary>
/// <param name="EdgePrice">VAH for the top zone, VAL for the bottom zone.</param>
/// <param name="ZoneHalfHeight">Pine's own `zoneTol` — the box's Y half-height around
/// <paramref name="EdgePrice"/>, purely cosmetic (the strategy has no equivalent; its own
/// `AbsNearTicks`/`LvnBandTicks` are unrelated distance checks, not a drawn box height).</param>
/// <param name="StatusText">Pre-built by the caller (score, held-abs/LVN components, armed/swept/
/// fired state, fuel) — same "caller builds the label, overlay just draws it" split
/// `StructureBoxOverlay` already uses for its own single-line `Label`.</param>
internal readonly record struct ZoneStatusDraw(
    double EdgePrice, double ZoneHalfHeight, ZoneState State, bool IsTop, string StatusText, DateTime DayStartUtc);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record ZoneBoxDrawable(ZoneStatusDraw[] Zones)
{
    public static readonly ZoneBoxDrawable Empty = new(Array.Empty<ZoneStatusDraw>());
}

/// <summary>
/// Generalizes Finch-Lite's own <c>StructureBoxOverlay</c>: a filled, bordered, labelled rectangle
/// live since it formed — but Y-range here is <c>EdgePrice ± ZoneHalfHeight</c> (a band around one
/// price) rather than an arbitrary top/bottom, X-range always extends live to the pane's own right
/// edge (a zone is always "today, still live" — it never has a retained-day history the way value
/// areas do), and colour comes from a 5-way <see cref="ZoneState"/> rather than a bullish/bearish
/// bool.
/// </summary>
internal sealed class ZoneBoxOverlay : IDisposable
{
    internal readonly record struct Options(
        Color EdgeOnlyColor, Color ScoreTwoPlusColor, Color ArmedColor, Color SweptColor,
        Color ShortColor, Color LongColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, SolidBrush> fillBrushes = new();
    private readonly Dictionary<int, Pen> borderPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, ZoneBoxDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
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
                if (!TryY(converter, zone.EdgePrice + zone.ZoneHalfHeight, pane.Top, pane.Bottom, out var topY))
                    continue;
                if (!TryY(converter, zone.EdgePrice - zone.ZoneHalfHeight, pane.Top, pane.Bottom, out var bottomY))
                    continue;

                var startX = (float)pane.Left;
                TryX(converter, zone.DayStartUtc, pane.Left, pane.Right, out startX);

                var colour = ColourFor(zone, options);
                var top = Math.Min(topY, bottomY);
                var height = Math.Max(Math.Abs(bottomY - topY), 1f);
                var rect = new RectangleF(startX, top, Math.Max(pane.Right - startX, 1f), height);

                graphics.FillRectangle(this.FillBrush(colour), rect);
                graphics.DrawRectangle(this.BorderPen(colour), rect.X, rect.Y, rect.Width, rect.Height);

                var lines = zone.StatusText.Split('\n');
                var y = rect.Y + 1f;
                var maxWidth = 0f;

                foreach (var lineText in lines)
                {
                    var size = graphics.MeasureString(lineText, this.font);
                    maxWidth = Math.Max(maxWidth, size.Width);
                }

                var blockRect = new RectangleF(startX + 3f, y, maxWidth + 6f, lines.Length * (graphics.MeasureString("Ag", this.font).Height));

                if (TryReserve(labelRegistry, blockRect))
                {
                    graphics.FillRectangle(this.labelBack, blockRect);
                    var lineY = blockRect.Top;

                    foreach (var lineText in lines)
                    {
                        var size = graphics.MeasureString(lineText, this.font);
                        graphics.DrawString(lineText, this.font, this.LabelBrush(colour), blockRect.Left + 3f, lineY);
                        lineY += size.Height;
                    }
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private static Color ColourFor(in ZoneStatusDraw zone, in Options options) => zone.State switch
    {
        ZoneState.Fired => zone.IsTop ? options.ShortColor : options.LongColor,
        ZoneState.Swept => options.SweptColor,
        ZoneState.Armed => options.ArmedColor,
        ZoneState.ScoreTwoPlus => options.ScoreTwoPlusColor,
        _ => options.EdgeOnlyColor,
    };

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

    private SolidBrush FillBrush(Color colour)
    {
        var key = colour.ToArgb();

        if (!this.fillBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(32, colour));
            this.fillBrushes[key] = brush;
        }

        return brush;
    }

    private Pen BorderPen(Color colour)
    {
        var key = colour.ToArgb();

        if (!this.borderPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(160, colour), 1f);
            this.borderPens[key] = pen;
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

        foreach (var brush in this.fillBrushes.Values) brush.Dispose();
        foreach (var pen in this.borderPens.Values) pen.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.fillBrushes.Clear();
        this.borderPens.Clear();
        this.labelBrushes.Clear();
    }
}
