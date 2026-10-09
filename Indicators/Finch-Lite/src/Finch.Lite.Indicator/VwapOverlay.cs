using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>One session's VWAP + band readout, ready to draw.</summary>
/// <param name="SessionStartUtc">Line origin — same "draws from session start to now" convention
/// as every other session-anchored overlay in this codebase.</param>
/// <param name="Vwap">Session VWAP.</param>
/// <param name="Upper1">Upper band 1/2/3 and Lower band 1/2/3 — null entries (e.g. a zero
/// multiplier) are simply not drawn.</param>
internal readonly record struct VwapDraw(
    DateTime SessionStartUtc, double Vwap, bool IsBull, bool IsBear,
    double? Upper1, double? Upper2, double? Upper3,
    double? Lower1, double? Lower2, double? Lower3,
    double? PriorVwap);

/// <summary>A band-rejection marker — source script's own "wicks through the band and closes back
/// inside with an opposite-colour body."</summary>
internal readonly record struct VwapRejection(DateTime TimeUtc, double Price, bool IsUpperRejection);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record VwapDrawable(VwapDraw? Session, VwapRejection[] Rejections)
{
    public static readonly VwapDrawable Empty = new(null, Array.Empty<VwapRejection>());
}

/// <summary>Draws the session VWAP line, up to 3 bands each side, the prior-session VWAP as a dim
/// reference line, and recent band-rejection markers — adapted from PocOverlay's own line-drawing
/// pattern (dashed reference lines, right-edge label with collision avoidance) plus
/// StructureBoxOverlay's own cached-brush/pen dictionary shape.</summary>
internal sealed class VwapOverlay : IDisposable
{
    internal readonly record struct Options(
        Color BullColor, Color BearColor, Color NeutralColor,
        Color Upper1Color, Color Upper2Color, Color Upper3Color,
        Color Lower1Color, Color Lower2Color, Color Lower3Color,
        Color PriorSessionColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> solidPens = new();
    private readonly Dictionary<int, Pen> dottedPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, VwapDrawable drawable, in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed) return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        if (converter is null || pane.Width <= 0f || pane.Height <= 0f) return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            if (drawable.Session is { } s)
                this.DrawSession(graphics, converter, pane, s, options, labelRegistry);

            foreach (var rej in drawable.Rejections)
                this.DrawRejection(graphics, converter, pane, rej, options);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawSession(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, VwapDraw s, in Options options, List<RectangleF> labelRegistry)
    {
        if (!TryX(converter, s.SessionStartUtc, pane.Left, pane.Right, out var startX)) return;

        var vwapColor = s.IsBull ? options.BullColor : s.IsBear ? options.BearColor : options.NeutralColor;

        this.DrawLine(graphics, converter, pane, startX, s.Vwap, vwapColor, 2f, solid: true, label: "VWAP", labelColor: vwapColor, labelRegistry);

        this.DrawBandIfPresent(graphics, converter, pane, startX, s.Upper1, options.Upper1Color, "+1σ", labelRegistry);
        this.DrawBandIfPresent(graphics, converter, pane, startX, s.Upper2, options.Upper2Color, "+2σ", labelRegistry);
        this.DrawBandIfPresent(graphics, converter, pane, startX, s.Upper3, options.Upper3Color, "+3σ", labelRegistry);
        this.DrawBandIfPresent(graphics, converter, pane, startX, s.Lower1, options.Lower1Color, "-1σ", labelRegistry);
        this.DrawBandIfPresent(graphics, converter, pane, startX, s.Lower2, options.Lower2Color, "-2σ", labelRegistry);
        this.DrawBandIfPresent(graphics, converter, pane, startX, s.Lower3, options.Lower3Color, "-3σ", labelRegistry);

        if (s.PriorVwap is { } pv)
            this.DrawLine(graphics, converter, pane, pane.Left, pv, options.PriorSessionColor, 1f, solid: false, label: "Prior VWAP", labelColor: options.PriorSessionColor, labelRegistry);
    }

    private void DrawBandIfPresent(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, float startX, double? price, Color color, string tag, List<RectangleF> labelRegistry)
    {
        if (price is { } p)
            this.DrawLine(graphics, converter, pane, startX, p, color, 1f, solid: true, label: tag, labelColor: color, labelRegistry);
    }

    private void DrawLine(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, float startX, double price, Color color, float width, bool solid, string label, Color labelColor, List<RectangleF> labelRegistry)
    {
        if (!TryY(converter, price, pane.Top, pane.Bottom, out var y)) return;

        graphics.DrawLine(solid ? this.SolidPen(color, width) : this.DottedPen(color, width), startX, y, pane.Right, y);

        var text = $"{label} {price:0.####}";
        var size = graphics.MeasureString(text, this.font);
        var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

        if (TryReserve(labelRegistry, rect))
        {
            graphics.FillRectangle(this.labelBack, rect);
            graphics.DrawString(text, this.font, this.LabelBrush(labelColor), rect.Left + 3f, rect.Top);
        }
    }

    private void DrawRejection(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, VwapRejection rej, in Options options)
    {
        if (!TryY(converter, rej.Price, pane.Top, pane.Bottom, out var y)) return;
        if (!TryX(converter, rej.TimeUtc, pane.Left, pane.Right, out var x)) return;

        var color = rej.IsUpperRejection ? options.Upper3Color : options.Lower3Color;
        const float r = 3.5f;
        var points = rej.IsUpperRejection
            ? new[] { new PointF(x - r, y - r), new PointF(x + r, y - r), new PointF(x, y + r) }
            : new[] { new PointF(x - r, y + r), new PointF(x + r, y + r), new PointF(x, y - r) };

        graphics.FillPolygon(this.LabelBrush(color), points);
    }

    private static bool TryY(IChartWindowCoordinatesConverter converter, double price, float top, float bottom, out float y)
    {
        y = 0f;
        if (double.IsNaN(price) || double.IsInfinity(price)) return false;
        var raw = converter.GetChartY(price);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
        y = (float)Math.Clamp(raw, top, bottom);
        return true;
    }

    private static bool TryX(IChartWindowCoordinatesConverter converter, DateTime utc, float left, float right, out float x)
    {
        x = left;
        var raw = converter.GetChartX(utc);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
        x = (float)Math.Clamp(raw, left, right);
        return true;
    }

    private static bool TryReserve(List<RectangleF> registry, RectangleF rect)
    {
        foreach (var placed in registry)
            if (placed.IntersectsWith(rect)) return false;

        registry.Add(rect);
        return true;
    }

    private Pen SolidPen(Color colour, float width)
    {
        var key = (colour.ToArgb() * 31) + (int)(width * 10);
        if (!this.solidPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, width);
            this.solidPens[key] = pen;
        }
        return pen;
    }

    private Pen DottedPen(Color colour, float width)
    {
        var key = (colour.ToArgb() * 31) + (int)(width * 10);
        if (!this.dottedPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(150, colour), width) { DashStyle = DashStyle.Dot };
            this.dottedPens[key] = pen;
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
        if (this.disposed) return;
        this.disposed = true;

        this.font.Dispose();
        this.labelBack.Dispose();
        foreach (var p in this.solidPens.Values) p.Dispose();
        foreach (var p in this.dottedPens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();

        this.solidPens.Clear();
        this.dottedPens.Clear();
        this.labelBrushes.Clear();
    }
}
