using System;
using System.Collections.Generic;
using System.Drawing;
using OrbIx.Core.Config;
using OrbIx.Core.Flow;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The numeric rows under the chart: one column per bar, one row per configured statistic, each
/// cell shaded by how large its value is against the rest of the band.
///
/// THE BAND RESERVES ITS OWN RECTANGLE. It is the one absorbed display that takes pane HEIGHT
/// rather than only ink, so it goes into the shared label registry as a whole — otherwise a level
/// caption would land inside it and be unreadable against the shading.
///
/// AN UNMEASURED CELL IS SHADED GREY AND LEFT BLANK, never drawn as zero. A bar seeded from
/// aggregates has no print order, so its delta EXTREMES are unknowable — and a zero there would be
/// a reading where there is none. The distinction is the whole reason StatColumn carries
/// IsMeasured.
///
/// THE HEADER COLUMN IS MEASURED, NOT FIXED. "Session Delta" was clipped to "Session Del" at a
/// fixed width on Aramid Flow's first live capture; the column is now as wide as its longest name.
/// </summary>
internal sealed class FlowStatisticsOverlay : IDisposable
{
    internal readonly record struct Options(FlowConfig Flow, float BarsWidth);

    /// <summary>Below this cell width the numbers cannot be read, so only the shading is drawn.</summary>
    private const float MinWidthForText = 22f;

    /// <summary>Shading range: even a zero cell is faintly visible, so an empty row reads as empty.</summary>
    private const int MinAlpha = 30;

    private const int AlphaRange = 200;

    private readonly SolidBrush band = new(Color.FromArgb(150, 16, 18, 24));
    private readonly SolidBrush header = new(Color.FromArgb(200, 60, 64, 72));
    private readonly SolidBrush unmeasured = new(Color.FromArgb(60, 90, 90, 90));
    private readonly SolidBrush headerText = new(Color.Gainsboro);
    private readonly SolidBrush cellText = new(Color.White);
    private readonly Dictionary<float, Font> fonts = new();
    private readonly Dictionary<Color, SolidBrush> brushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics,
        IChartWindow window,
        FlowFrame frame,
        in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || frame.StatRows.Length == 0 || frame.Columns.Length == 0)
            return;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0f || pane.Height <= 0f)
            return;

        var config = options.Flow.ClusterStatistics;
        var rows = frame.StatRows;
        var rowHeight = Math.Clamp(config.RowHeightPx, 10, 60);
        var height = rowHeight * rows.Length;

        if (height >= pane.Height)
            return;

        var area = new RectangleF(pane.Left, pane.Bottom - height, pane.Width, height);
        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            var font = this.Font(Math.Max(7f, rowHeight * 0.5f));
            var headerWidth = 0f;

            if (config.ShowRowHeaders)
            {
                foreach (var row in rows)
                {
                    headerWidth = Math.Max(
                        headerWidth,
                        graphics.MeasureString(FlowDisplayText.RowName(row), font).Width + 8f);
                }
            }

            graphics.FillRectangle(this.band, area);
            labelRegistry.Add(area);

            // One pass for the x positions and the visible span. The scale is taken over exactly
            // the columns the operator chose to scale by, so a band scaled to the visible chart
            // re-scales as they pan and one scaled to everything does not.
            var xs = new float[frame.Columns.Length];
            var first = -1;
            var last = -1;

            for (var i = 0; i < frame.Columns.Length; i++)
            {
                xs[i] = float.NaN;

                if (!ChartOverlay.TryX(converter, frame.Columns[i].OpenUtc, out var x))
                    continue;

                xs[i] = x;

                if (x + options.BarsWidth < area.Left + headerWidth || x > area.Right)
                    continue;

                if (first < 0)
                    first = i;

                last = i;
            }

            if (first < 0)
                return;

            var from = config.ProportionByVisible ? first : 0;
            var to = config.ProportionByVisible ? last : frame.Columns.Length - 1;
            var cellWidth = Math.Max(options.BarsWidth - 1f, 1f);
            var showText = cellWidth >= MinWidthForText;

            for (var r = 0; r < rows.Length; r++)
            {
                var row = rows[r];
                var top = area.Top + (r * rowHeight);
                var scale = ClusterStatisticsEngine.ScaleMax(frame.Columns, row, from, to);

                if (config.ShowRowHeaders)
                {
                    graphics.FillRectangle(this.header, area.Left, top, headerWidth, rowHeight);
                    graphics.DrawString(
                        FlowDisplayText.RowName(row), font, this.headerText, area.Left + 3f, top + 2f);
                }

                for (var i = first; i <= last; i++)
                {
                    if (float.IsNaN(xs[i]))
                        continue;

                    var column = frame.Columns[i];
                    var cell = new RectangleF(xs[i] + 0.5f, top + 0.5f, cellWidth, rowHeight - 1f);

                    if (!column.IsMeasured(row))
                    {
                        graphics.FillRectangle(this.unmeasured, cell);
                        continue;
                    }

                    var value = column.Value(row);
                    var intensity = ClusterStatisticsEngine.Intensity(value, scale);
                    var colour = ChartTheme.From(
                        FlowDisplayText.RowColour(row, value, config),
                        (int)Math.Round(MinAlpha + (AlphaRange * intensity)));

                    graphics.FillRectangle(this.Brush(colour), cell);

                    if (showText)
                    {
                        graphics.DrawString(
                            FlowDisplayText.CellText(row, column), font, this.cellText,
                            cell.Left + 1f, cell.Top + 1f);
                    }
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private Font Font(float size)
    {
        if (this.fonts.TryGetValue(size, out var font))
            return font;

        font = new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular);
        this.fonts[size] = font;
        return font;
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
        this.band.Dispose();
        this.header.Dispose();
        this.unmeasured.Dispose();
        this.headerText.Dispose();
        this.cellText.Dispose();

        foreach (var font in this.fonts.Values)
            font.Dispose();

        foreach (var brush in this.brushes.Values)
            brush.Dispose();

        this.fonts.Clear();
        this.brushes.Clear();
    }
}
