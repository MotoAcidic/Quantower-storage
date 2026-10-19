using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer.Chart;

namespace SwingLevels;

/// <summary>The currently-open session's live range box — drawn from the session's start to the
/// pane's right edge, top/bottom tracking the running high/low.</summary>
internal readonly record struct SessionBoxDraw(string Name, double High, double Low, DateTime StartUtc, Color Color);

/// <summary>One finalized session high or low, ready to draw — same "untested until touched"
/// semantics as `SwingLevelDraw`, plus which session it came from for the label.</summary>
internal readonly record struct SessionLevelLineDraw(bool IsHigh, double Price, DateTime SinceUtc, bool Untested, string SessionName, Color Color);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record SessionLevelDrawable(SessionBoxDraw[] Boxes, SessionLevelLineDraw[] Lines)
{
    public static readonly SessionLevelDrawable Empty = new(Array.Empty<SessionBoxDraw>(), Array.Empty<SessionLevelLineDraw>());
}

/// <summary>Renderer for the ported `untestedLevels.pine` session ranges — a live box for whichever
/// session is currently open, plus extending lines for every untested session high/low. Visually
/// distinct from `SwingLevelOverlay`'s own lines per the operator's own choice: each session keeps
/// its own colour (so Asia/London/NY AM/NY PM levels are tellable apart from each other and from
/// swing S/R at a glance), and session lines use a dash pattern even while untested (vs swing
/// levels' solid-while-untested convention) as a second visual cue of which engine produced them.</summary>
internal sealed class SessionLevelOverlay : IDisposable
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> boxPens = new();
    private readonly Dictionary<int, SolidBrush> boxFills = new();
    private readonly Dictionary<int, Pen> brightPens = new();
    private readonly Dictionary<int, Pen> dimPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly Dictionary<int, SolidBrush> dimLabelBrushes = new();
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window, SessionLevelDrawable drawable, float lineThickness, List<RectangleF> labelRegistry)
    {
        if (this.disposed || (drawable.Boxes.Length == 0 && drawable.Lines.Length == 0))
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var box in drawable.Boxes)
            {
                if (!TryY(converter, box.High, pane.Top, pane.Bottom, out var yTop)) continue;
                if (!TryY(converter, box.Low, pane.Top, pane.Bottom, out var yBot)) continue;

                var startX = (float)pane.Left;
                TryX(converter, box.StartUtc, pane.Left, pane.Right, out startX);

                var rect = new RectangleF(startX, Math.Min(yTop, yBot), Math.Max(1f, pane.Right - startX), Math.Max(1f, Math.Abs(yBot - yTop)));
                graphics.FillRectangle(this.BoxFill(box.Color), rect);
                graphics.DrawRectangle(this.BoxPen(box.Color), rect.X, rect.Y, rect.Width, rect.Height);

                var labelRect = new RectangleF(rect.X + 4f, rect.Y + 2f, 90f, 14f);
                if (TryReserve(labelRegistry, labelRect))
                {
                    graphics.FillRectangle(this.labelBack, labelRect);
                    graphics.DrawString(box.Name, this.font, this.LabelBrush(box.Color), labelRect.Left + 2f, labelRect.Top);
                }
            }

            foreach (var line in drawable.Lines)
            {
                if (!TryY(converter, line.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var startX = (float)pane.Left;
                TryX(converter, line.SinceUtc, pane.Left, pane.Right, out startX);

                var pen = line.Untested ? this.BrightPen(line.Color, lineThickness) : this.DimPen(line.Color, lineThickness);
                graphics.DrawLine(pen, startX, y, pane.Right, y);

                var kind = line.IsHigh ? "High" : "Low";
                var text = line.Untested ? $"{line.SessionName} {kind} {line.Price:0.####}" : $"{line.SessionName} {kind} {line.Price:0.####} (tested)";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Right - size.Width - 10f, y + 2f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    var brush = line.Untested ? this.LabelBrush(line.Color) : this.DimLabelBrush(line.Color);
                    graphics.DrawString(text, this.font, brush, rect.Left + 3f, rect.Top);
                }
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
        if (double.IsNaN(price) || double.IsInfinity(price)) return false;
        var raw = converter.GetChartY(price);
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
        y = (float)Math.Clamp(raw, top, bottom);
        return true;
    }

    private static bool TryX(
        IChartWindowCoordinatesConverter converter, DateTime utc, float left, float right, out float x)
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

    private Pen BoxPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.boxPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(160, colour), 1f) { DashStyle = DashStyle.Dot };
            this.boxPens[key] = pen;
        }
        return pen;
    }

    private SolidBrush BoxFill(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.boxFills.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(22, colour));
            this.boxFills[key] = brush;
        }
        return brush;
    }

    private Pen BrightPen(Color colour, float thickness)
    {
        var key = (colour.ToArgb() * 32) + (int)(thickness * 10);
        if (!this.brightPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(colour, thickness) { DashStyle = DashStyle.Dash };
            this.brightPens[key] = pen;
        }
        return pen;
    }

    private Pen DimPen(Color colour, float thickness)
    {
        var key = (colour.ToArgb() * 32) + (int)(thickness * 10);
        if (!this.dimPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(110, colour), Math.Max(1f, thickness - 0.5f)) { DashStyle = DashStyle.Dot };
            this.dimPens[key] = pen;
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

    private SolidBrush DimLabelBrush(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.dimLabelBrushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(160, colour));
            this.dimLabelBrushes[key] = brush;
        }
        return brush;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;

        this.font.Dispose();
        this.labelBack.Dispose();
        foreach (var p in this.boxPens.Values) p.Dispose();
        foreach (var b in this.boxFills.Values) b.Dispose();
        foreach (var p in this.brightPens.Values) p.Dispose();
        foreach (var p in this.dimPens.Values) p.Dispose();
        foreach (var b in this.labelBrushes.Values) b.Dispose();
        foreach (var b in this.dimLabelBrushes.Values) b.Dispose();

        this.boxPens.Clear();
        this.boxFills.Clear();
        this.brightPens.Clear();
        this.dimPens.Clear();
        this.labelBrushes.Clear();
        this.dimLabelBrushes.Clear();
    }
}
