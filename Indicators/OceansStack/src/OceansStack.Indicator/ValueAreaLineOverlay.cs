using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace OceansStack;

/// <summary>Which group a line belongs to — drives colour only; every group behaves identically
/// otherwise (full-pane-width when <see cref="ValueAreaLineDraw.EndUtc"/> is null, a fixed end
/// point for a retained historical day when it isn't).</summary>
internal enum LineKind
{
    DailyVah, DailyVal, DailyPoc,
    PriorDayHL, OvernightHL, AsiaHL,
    WeeklyVah, WeeklyVal, WeeklyPoc, WeeklyHL,
    QqqVah, QqqVal,
}

/// <summary>One reference line ready to draw.</summary>
/// <param name="EndUtc">Null = still today's own line, extends to the pane's right edge (Pine's own
/// `extend.right`). Set = a RETAINED historical day's own frozen line (see
/// <see cref="OceansStackIndicator.KeepPriorDays"/>) — ends at that day's own next-session
/// boundary instead of the live edge.</param>
internal readonly record struct ValueAreaLineDraw(double Price, string Label, LineKind Kind, DateTime? EndUtc, DateTime StartUtc);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record ValueAreaLineDrawable(ValueAreaLineDraw[] Lines)
{
    public static readonly ValueAreaLineDrawable Empty = new(Array.Empty<ValueAreaLineDraw>());
}

/// <summary>
/// Generalizes Finch-Lite's own <c>PocOverlay</c> from "one line kind, three colour variants" to
/// "many line kinds, one colour per group" — every daily/weekly/QQQ value-area and pool line
/// `ocean.pine` draws is the same visual shape (a dashed reference line with a right-aligned
/// price+name label), so one overlay draws all of them, told apart only by <see cref="LineKind"/>.
/// </summary>
internal sealed class ValueAreaLineOverlay : IDisposable
{
    internal readonly record struct Options(
        Color DailyColor, Color DailyPocColor, Color PriorDayColor, Color OvernightColor,
        Color AsiaColor, Color WeeklyColor, Color QqqColor, bool ShowHistoricalLabels);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> pens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, ValueAreaLineDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Lines.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var line in drawable.Lines)
            {
                if (!TryY(converter, line.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var startX = (float)pane.Left;
                TryX(converter, line.StartUtc, pane.Left, pane.Right, out startX);

                var endX = (float)pane.Right;
                if (line.EndUtc is { } fixedEnd)
                    TryX(converter, fixedEnd, pane.Left, pane.Right, out endX);

                if (endX <= startX)
                    continue; // a retained day fully scrolled past the left edge — nothing to draw

                var colour = ColourFor(line.Kind, options);
                var historical = line.EndUtc is not null;
                graphics.DrawLine(this.Pen(colour, historical), startX, y, endX, y);

                if (historical && !options.ShowHistoricalLabels)
                    continue;

                var text = $"{line.Label} {line.Price:0.####}";
                var size = graphics.MeasureString(text, this.font);
                var anchorRight = historical ? endX : pane.Right;
                var rect = new RectangleF(
                    anchorRight - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

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

    private static Color ColourFor(LineKind kind, in Options options) => kind switch
    {
        LineKind.DailyVah or LineKind.DailyVal => options.DailyColor,
        LineKind.DailyPoc => options.DailyPocColor,
        LineKind.PriorDayHL => options.PriorDayColor,
        LineKind.OvernightHL => options.OvernightColor,
        LineKind.AsiaHL => options.AsiaColor,
        LineKind.WeeklyVah or LineKind.WeeklyVal or LineKind.WeeklyPoc or LineKind.WeeklyHL => options.WeeklyColor,
        LineKind.QqqVah or LineKind.QqqVal => options.QqqColor,
        _ => options.DailyColor,
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

    private Pen Pen(Color colour, bool historical)
    {
        // Historical (retained-day) lines get their own, fainter pen — same colour family, lower
        // alpha — so today's own live edges stay visually dominant, matching the "quiet background,
        // loud live signal" ordering this codebase's overlays already use between separate features.
        var key = colour.ToArgb() ^ (historical ? 1 : 0);

        if (!this.pens.TryGetValue(key, out var pen))
        {
            var penColour = historical ? Color.FromArgb(110, colour) : colour;
            pen = new Pen(penColour, 1.25f) { DashStyle = DashStyle.Dash };
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
