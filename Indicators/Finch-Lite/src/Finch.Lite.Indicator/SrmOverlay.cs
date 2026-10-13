using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace FinchLite;

/// <summary>Which role a line plays — drives dash style/weight, not color (color comes from the
/// owning period/line itself).</summary>
internal enum SrmLineKind
{
    Budget,
    Projection,
    Poc,
    Vah,
    Val,
    Naked,
    InitialBalance,
    VolStep,
}

/// <summary>One bounded horizontal segment from <see cref="X1Utc"/> to <see cref="X2Utc"/> — unless
/// <see cref="FullWidth"/> is set, in which case it spans the whole pane instead (used only for
/// Vol-Step Ranges, which `srm.pine` itself draws as a continuous `plot()`, not a bounded
/// `line.new`, so a bounded X1/X2 pair would misrepresent it).</summary>
internal readonly record struct SrmLineDraw(DateTime X1Utc, DateTime X2Utc, double Price, SrmLineKind Kind, Color Color, string Label, bool FullWidth = false);

/// <summary>One grouped row of a session/week/month's volume-by-price histogram, ready to draw as
/// a box whose WIDTH is proportional to that row's own volume relative to the busiest row in the
/// same profile — same visual shape as `srm.pine`'s own `drawProf` boxes.</summary>
internal readonly record struct SrmProfileBoxDraw(DateTime X1Utc, DateTime MaxX2Utc, double Lo, double Hi, double RelativeVolume, Color Color);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record SrmDrawable(SrmLineDraw[] Lines, SrmProfileBoxDraw[] Boxes)
{
    public static readonly SrmDrawable Empty = new(Array.Empty<SrmLineDraw>(), Array.Empty<SrmProfileBoxDraw>());
}

/// <summary>Renders Session Range Map lines and profile boxes — adapted from `OrbLevels`' own
/// `OrbBoxOverlay` (same time→X / price→Y conversion helpers), generalized to a single kind-tagged
/// line type since SRM has several visually-distinct line roles sharing one draw path rather than
/// ORB's box+three-rays shape.</summary>
internal sealed class SrmOverlay : IDisposable
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> pens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly Dictionary<int, SolidBrush> boxBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, SrmDrawable drawable, List<RectangleF> labelRegistry)
    {
        if (this.disposed || (drawable.Lines.Length == 0 && drawable.Boxes.Length == 0)) return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        if (converter is null || pane.Width <= 0f || pane.Height <= 0f) return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var box in drawable.Boxes)
                this.DrawBox(graphics, converter, pane, box);

            foreach (var line in drawable.Lines)
                this.DrawOne(graphics, converter, pane, line, labelRegistry);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawBox(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, SrmProfileBoxDraw box)
    {
        if (!TryY(converter, box.Hi, pane.Top, pane.Bottom, out var topY)) return;
        if (!TryY(converter, box.Lo, pane.Top, pane.Bottom, out var botY)) return;
        if (!TryX(converter, box.X1Utc, pane.Left, pane.Right, out var x1)) return;
        if (!TryX(converter, box.MaxX2Utc, pane.Left, pane.Right, out var maxX2)) return;
        if (maxX2 <= x1) return;

        var width = Math.Max(1f, (maxX2 - x1) * (float)Math.Clamp(box.RelativeVolume, 0, 1));
        var top = Math.Min(topY, botY);
        var height = Math.Max(Math.Abs(botY - topY) - 1f, 1f);

        graphics.FillRectangle(this.BoxBrush(box.Color), new RectangleF(x1, top, width, height));
    }

    private void DrawOne(Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane, SrmLineDraw line, List<RectangleF> labelRegistry)
    {
        if (!TryY(converter, line.Price, pane.Top, pane.Bottom, out var y)) return;

        float x1, x2;
        if (line.FullWidth)
        {
            x1 = pane.Left;
            x2 = pane.Right;
        }
        else
        {
            if (!TryX(converter, line.X1Utc, pane.Left, pane.Right, out x1)) return;
            if (!TryX(converter, line.X2Utc, pane.Left, pane.Right, out x2)) return;
            if (x2 <= x1) return;
        }

        graphics.DrawLine(this.Pen(line.Kind, line.Color), x1, y, x2, y);

        if (line.Label.Length > 0)
            this.DrawLabel(graphics, pane, x2, y, line.Label, line.Color, labelRegistry);
    }

    private void DrawLabel(Graphics graphics, RectangleF pane, float x2, float y, string text, Color color, List<RectangleF> labelRegistry)
    {
        var size = graphics.MeasureString(text, this.font);
        var anchorRight = x2 >= pane.Right - 2f;
        var left = anchorRight ? pane.Right - size.Width - 10f : x2 + 4f;
        var rect = new RectangleF(left, y - size.Height - 1f, size.Width + 6f, size.Height);

        if (!TryReserve(labelRegistry, rect)) return;

        graphics.FillRectangle(this.labelBack, rect);
        graphics.DrawString(text, this.font, this.LabelBrush(color), rect.Left + 3f, rect.Top);
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

    private Pen Pen(SrmLineKind kind, Color colour)
    {
        var key = (colour.ToArgb() * 16) + (int)kind;
        if (!this.pens.TryGetValue(key, out var pen))
        {
            pen = kind switch
            {
                SrmLineKind.Budget => new Pen(Color.FromArgb(160, colour), 1f) { DashStyle = DashStyle.Dash },
                SrmLineKind.Projection => new Pen(Color.FromArgb(200, colour), 1.5f) { DashStyle = DashStyle.Dot },
                SrmLineKind.Poc => new Pen(colour, 2f),
                SrmLineKind.Naked => new Pen(Color.FromArgb(170, colour), 1f) { DashStyle = DashStyle.Dot },
                SrmLineKind.InitialBalance => new Pen(colour, 2f),
                SrmLineKind.VolStep => new Pen(Color.FromArgb(180, colour), 1f) { DashStyle = DashStyle.Dash },
                _ => new Pen(Color.FromArgb(190, colour), 1f) { DashStyle = DashStyle.Dash }, // Vah/Val
            };
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

    private SolidBrush BoxBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.boxBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(150, colour));
            this.boxBrushes[key] = brush;
        }
        return brush;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;

        this.font.Dispose();
        this.labelBack.Dispose();
        foreach (var p in this.pens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();
        foreach (var b in this.boxBrushes.Values) b.Dispose();

        this.pens.Clear();
        this.labelBrushes.Clear();
        this.boxBrushes.Clear();
    }
}
