using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using OrbIx.Core.Config;
using OrbIx.Core.Flow;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// Reference geometry over the chart's own bars: Fibonacci fans, two-tap trend lines, and gamma
/// walls.
///
/// LAID OUT IN BAR INDEX, DRAWN IN TIME. Fans and trend lines are computed in index space, because
/// that is the space a straight line is straight in — a line laid out in clock time bends wherever
/// the market was closed. The frame carries the bar times the geometry was computed over, and this
/// maps index back through THAT array. Taking the mapping from anywhere else would shift every line
/// by however much the two disagreed.
///
/// LINES EXTEND RIGHT AND NEVER LEFT. A fan ray or a trend line runs from its origin forward; drawn
/// backwards it would claim the structure existed before the swings that define it.
///
/// THE WALLS SAY THEIR AGE. The options source behind the file is gone, so a file that still exists
/// is exactly as stale as its last write, and a wall drawn without its age reads as current. The
/// status comes from the frame; this prints it beside the levels rather than deciding it.
/// </summary>
internal sealed class FlowBiasOverlay : IDisposable
{
    internal readonly record struct Options(FlowConfig Flow, float BarsWidth);

    /// <summary>Opacity of a fan's own trend line — the leg its rays are measured from.</summary>
    private const int FanLegAlpha = 120;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush labelBack = new(Color.FromArgb(150, 16, 18, 24));
    private readonly Dictionary<(Color, float, DashStyle), Pen> pens = new();
    private readonly Dictionary<Color, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics,
        IChartWindow window,
        FlowBias bias,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || !bias.HasGeometry)
            return;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            this.Fans(graphics, converter, pane, bias, options, labelRegistry);
            this.TrendLines(graphics, converter, pane, bias, options);
            this.Walls(graphics, converter, pane, bias, options, labelRegistry);
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothing;
            graphics.Clip = previousClip;
        }
    }

    private void Fans(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        FlowBias bias, in Options options, List<RectangleF> labelRegistry)
    {
        var colour = ChartTheme.From(options.Flow.FibFan.Colour);

        foreach (var fan in bias.Fans)
        {
            if (!this.TryPoint(converter, pane, bias, options, fan.OriginBar, fan.OriginPrice, out var origin)
                || !this.TryPoint(converter, pane, bias, options, fan.SecondBar, fan.SecondPrice, out var second))
            {
                continue;
            }

            // The leg itself, dotted: it is the measurement the rays come from, not a level.
            graphics.DrawLine(
                this.Pen(Color.FromArgb(FanLegAlpha, colour), 1f, DashStyle.Dot), origin, second);

            foreach (var line in fan.Lines)
            {
                if (!this.TryPoint(converter, pane, bias, options, line.LevelBar, line.LevelPrice, out var through))
                    continue;

                var end = ExtendToRight(origin, through, pane.Right);
                graphics.DrawLine(this.Pen(colour, 1f, DashStyle.Solid), origin, end);

                var label = line.Ratio.ToString("0.###", CultureInfo.InvariantCulture);
                var measured = graphics.MeasureString(label, this.font);
                var rect = new RectangleF(
                    end.X - measured.Width - 2f, end.Y - measured.Height, measured.Width, measured.Height);

                if (ChartOverlay.TryReserve(labelRegistry, rect))
                    graphics.DrawString(label, this.font, this.Brush(colour), rect.Left, rect.Top);
            }
        }
    }

    private void TrendLines(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        FlowBias bias, in Options options)
    {
        var colour = ChartTheme.From(options.Flow.TrendLines.Colour);

        foreach (var line in bias.TrendLines)
        {
            if (!this.TryPoint(converter, pane, bias, options, line.StartBar, line.StartPrice, out var start)
                || !this.TryPoint(converter, pane, bias, options, line.EndBar, line.EndPrice, out var end))
            {
                continue;
            }

            graphics.DrawLine(
                this.Pen(colour, 1.5f, DashStyle.Solid), start, ExtendToRight(start, end, pane.Right));
        }
    }

    /// <summary>
    /// The gamma walls, each named, with the source's age said once beneath the last of them.
    /// </summary>
    private void Walls(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        FlowBias bias, in Options options, List<RectangleF> labelRegistry)
    {
        if (bias.Gex.Length == 0)
            return;

        var gex = options.Flow.Gex;
        var lowest = float.MinValue;

        foreach (var level in bias.Gex)
        {
            if (!ChartOverlay.TryY(converter, level.ChartPrice, pane.Top, pane.Bottom, out var y))
                continue;

            var colour = ChartTheme.From(level.Name switch
            {
                "call_wall" => gex.CallColour,
                "put_wall" => gex.PutColour,
                _ => gex.OtherColour,
            });

            graphics.DrawLine(this.Pen(colour, 1f, DashStyle.DashDot), pane.Left, y, pane.Right, y);

            var measured = graphics.MeasureString(level.Name, this.font);
            var rect = new RectangleF(pane.Left + 4f, y - measured.Height - 1f, measured.Width + 4f, measured.Height);

            if (ChartOverlay.TryReserve(labelRegistry, rect))
            {
                graphics.FillRectangle(this.labelBack, rect);
                graphics.DrawString(level.Name, this.font, this.Brush(colour), rect.Left + 2f, rect.Top);
            }

            lowest = Math.Max(lowest, y);
        }

        // SAID ONCE, UNDER THE LOWEST WALL. The source is dead; a wall without its age reads as
        // current, and repeating the sentence per level would bury the levels it is about.
        if (bias.GexStatus.Length == 0 || lowest <= float.MinValue)
            return;

        var status = graphics.MeasureString(bias.GexStatus, this.font);
        var statusRect = new RectangleF(pane.Left + 4f, lowest + 2f, status.Width + 4f, status.Height);

        if (!ChartOverlay.TryReserve(labelRegistry, statusRect))
            return;

        graphics.FillRectangle(this.labelBack, statusRect);
        graphics.DrawString(
            bias.GexStatus, this.font, this.Brush(ChartTheme.From(gex.OtherColour)),
            statusRect.Left + 2f, statusRect.Top);
    }

    /// <summary>
    /// A bar index and a price, as a point on the pane.
    ///
    /// THE INDEX IS RESOLVED THROUGH THE FRAME'S OWN TIMES. An index outside that array cannot be
    /// placed at all — it belongs to bars the geometry was not computed over — so it is refused
    /// rather than clamped to the nearest one, which would draw a line to a bar it does not touch.
    /// </summary>
    private bool TryPoint(
        IChartWindowCoordinatesConverter converter, RectangleF pane, FlowBias bias,
        in Options options, int bar, double price, out PointF point)
    {
        point = default;

        if (bar < 0 || bar >= bias.BarTimes.Length)
            return false;

        if (!ChartOverlay.TryX(converter, bias.BarTimes[bar], out var x)
            || !ChartOverlay.TryY(converter, price, pane.Top, pane.Bottom, out var y))
        {
            return false;
        }

        point = new PointF(x + (options.BarsWidth / 2f), y);
        return true;
    }

    /// <summary>
    /// The point where the ray through two points meets the right edge.
    ///
    /// A vertical pair has no rightward extension, so the second point stands — the alternative is
    /// a division by zero and a NaN coordinate, which throws inside GDI+ and takes the whole frame
    /// with it.
    /// </summary>
    private static PointF ExtendToRight(PointF a, PointF b, float right)
    {
        if (Math.Abs(b.X - a.X) < 0.0001f)
            return b;

        var slope = (b.Y - a.Y) / (b.X - a.X);
        return new PointF(right, a.Y + (slope * (right - a.X)));
    }

    private Pen Pen(Color colour, float width, DashStyle dash)
    {
        var key = (colour, width, dash);

        if (this.pens.TryGetValue(key, out var pen))
            return pen;

        pen = new Pen(colour, width) { DashStyle = dash };
        this.pens[key] = pen;
        return pen;
    }

    private SolidBrush Brush(Color colour)
    {
        if (this.brushes.TryGetValue(colour, out var brush))
            return brush;

        brush = new SolidBrush(colour);
        this.brushes[colour] = brush;
        return brush;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();

        foreach (var pen in this.pens.Values)
            pen.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.pens.Clear();
        this.brushes.Clear();
    }
}
