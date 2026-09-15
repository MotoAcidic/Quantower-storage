using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using OrbIx.Core.Features;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// Everything this overlay draws, assembled by the indicator and handed over whole.
/// </summary>
/// <param name="Flips">
/// Session delta flip levels, oldest first. Already filtered and capped by the caller: the
/// renderer decides nothing about which levels matter.
/// </param>
/// <param name="Shelves">Absorption shelves, heaviest first, already capped.</param>
/// <param name="Status">
/// One line naming what these measurements have and have not been shown to do. Empty draws
/// nothing.
/// </param>
internal sealed record DeltaLevelsDrawable(
    IReadOnlyList<DeltaFlip> Flips,
    IReadOnlyList<AbsorptionShelf> Shelves,
    string Status)
{
    public static readonly DeltaLevelsDrawable Empty =
        new(Array.Empty<DeltaFlip>(), Array.Empty<AbsorptionShelf>(), string.Empty);

    public bool HasLevels => this.Flips.Count > 0 || this.Shelves.Count > 0;
}

/// <summary>
/// Two families of horizontal reference level: where a session's cumulative delta turned over,
/// and prices that repeatedly absorbed one-sided aggression.
///
/// HORIZONTAL ONLY, AND THAT IS A DESIGN CONSTRAINT RATHER THAN AN OMISSION. ORB-IX already
/// carries an information panel, a delta panel and — on the operator's charts — a CRT overlay
/// competing for the same corner. Anything that reserved panel height here would be paid for by
/// the chart it is meant to explain, so these cost ink and nothing else.
///
/// LEVELS START WHERE THEY WERE MADE. Each line runs from the bar that established it to the right
/// edge, never across the whole pane: a line drawn to the left of its own origin claims the level
/// existed before the event that created it.
///
/// DISPLAY OF TWO MEASURED NULLS, and the caption says so on the chart. Absorption is a measured
/// null on MNQ — trial 008, 25,745 episodes, +1.006 ticks against matched-random, failing
/// Bonferroni and below the 2.76-tick cost floor. Cumulative-delta divergence is a measured null —
/// trial 025, 6 cells, zero survivors, the best-powered cell at net −19.83 ticks with the entire
/// 95% interval below zero. A flip LEVEL is a price rather than a prediction and has never been
/// measured at all, which is a different statement from having been measured and passed. A mark on
/// a chart implies a read worth acting on; neither of these has earned that implication, and the
/// same rule <c>AbsorptionOverlay</c> already follows applies here.
/// </summary>
internal sealed class DeltaLevelsOverlay : IDisposable
{
    internal readonly record struct Options(
        Color FlipUpColor,
        Color FlipDownColor,
        Color ShelfColor,
        bool Labels);

    /// <summary>
    /// Candidate rows for the caption, tried in order.
    ///
    /// Copied in intent from <c>AbsorptionOverlay</c>, where a single fixed row was measured
    /// failing on the operator's own chart: the reservation lost to overlays already holding that
    /// row and the caption silently never drew. These rows sit below that block's range so the two
    /// captions do not compete for the same first choice.
    /// </summary>
    private static readonly float[] CaptionRows = { 132f, 146f, 160f, 174f, 188f };

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush text = new(Color.Gainsboro);
    private readonly SolidBrush back = new(Color.FromArgb(170, 24, 26, 32));
    private readonly Dictionary<Color, Pen> solid = new();
    private readonly Dictionary<Color, Pen> dashed = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, DeltaLevelsDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || (!drawable.HasLevels && drawable.Status.Length == 0))
            return;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            // Shelves first, so a flip level sharing a price is the one that stays legible: the
            // flip is a dated event and the shelf is a standing condition.
            for (var i = 0; i < drawable.Shelves.Count; i++)
                this.Shelf(graphics, converter, pane, drawable.Shelves[i], options, labelRegistry);

            for (var i = 0; i < drawable.Flips.Count; i++)
            {
                var newest = i == drawable.Flips.Count - 1;
                this.Flip(graphics, converter, pane, drawable.Flips[i], newest, options, labelRegistry);
            }

            if (drawable.Status.Length != 0)
                this.Caption(graphics, pane, drawable.Status, labelRegistry);
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    /// <summary>
    /// One flip level, from the bar that crossed to the right edge.
    ///
    /// Older flips are drawn fainter than the newest. They are not less true — the price is the
    /// price — but a session's most recent turn is the one being traded around, and four equally
    /// weighted lines read as noise.
    /// </summary>
    private void Flip(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        DeltaFlip flip, bool newest, in Options options, List<RectangleF> labelRegistry)
    {
        if (!ChartOverlay.TryY(converter, flip.Price, pane.Top, pane.Bottom, out var y))
            return;

        var colour = flip.Sign > 0 ? options.FlipUpColor : options.FlipDownColor;
        var faded = Color.FromArgb(newest ? 235 : 120, colour);

        var left = pane.Left;

        if (ChartOverlay.TryX(converter, flip.CrossedBarCloseUtc, out var x))
            left = Math.Max(pane.Left, Math.Min(x, pane.Right));

        graphics.DrawLine(this.Pen(faded, dash: false), left, y, pane.Right, y);

        if (!options.Labels)
            return;

        var label = string.Format(
            CultureInfo.InvariantCulture,
            "Δflip {0} {1:HH:mm} {2:N0}",
            flip.Sign > 0 ? "up" : "down",
            flip.CrossedBarCloseUtc,
            flip.ConfirmCumulative);

        this.Tag(graphics, pane, label, left, y, faded, labelRegistry);
    }

    /// <summary>
    /// One absorption shelf, from the bar it first traded to the right edge. Broken shelves are
    /// dashed and faint rather than dropped — where the size defending a price got run over is
    /// worth seeing, and hiding it would leave the chart claiming the level simply never existed.
    /// </summary>
    private void Shelf(
        Graphics graphics, IChartWindowCoordinatesConverter converter, RectangleF pane,
        AbsorptionShelf shelf, in Options options, List<RectangleF> labelRegistry)
    {
        if (!ChartOverlay.TryY(converter, shelf.Price, pane.Top, pane.Bottom, out var y))
            return;

        var colour = Color.FromArgb(shelf.Held ? 225 : 110, options.ShelfColor);
        var left = pane.Left;

        if (ChartOverlay.TryX(converter, shelf.FirstBarOpenUtc, out var x))
            left = Math.Max(pane.Left, Math.Min(x, pane.Right));

        graphics.DrawLine(this.Pen(colour, dash: !shelf.Held), left, y, pane.Right, y);

        if (!options.Labels)
            return;

        // The arrow is the PASSIVE side — the one standing there — not the aggressor. A bid that
        // kept absorbing sellers holds price up, so it points up.
        var label = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} x{2} {3:N0} {4:N0}%{5}",
            shelf.PassiveSide > 0 ? "▲" : "▼",
            shelf.PassiveSide > 0 ? "bid" : "ask",
            shelf.Bars,
            shelf.Volume,
            shelf.LeanPercent,
            shelf.Held ? string.Empty : " broken");

        this.Tag(graphics, pane, label, left, y, colour, labelRegistry);
    }

    /// <summary>
    /// A label just above its line, skipped rather than overlapped when the row is taken.
    ///
    /// Skipping is the right failure here: two captions on top of each other are unreadable and,
    /// worse, ambiguous about which level they name.
    /// </summary>
    private void Tag(
        Graphics graphics, RectangleF pane, string label, float left, float y, Color colour,
        List<RectangleF> labelRegistry)
    {
        var size = graphics.MeasureString(label, this.font);
        var x = Math.Min(left + 4f, pane.Right - size.Width - 4f);

        if (x < pane.Left)
            x = pane.Left;

        var rect = new RectangleF(x, y - size.Height - 1f, size.Width + 4f, size.Height);

        if (rect.Y < pane.Top || rect.Bottom > pane.Bottom)
            return;

        if (!ChartOverlay.TryReserve(labelRegistry, rect))
            return;

        graphics.FillRectangle(this.back, rect);

        using var brush = new SolidBrush(colour);
        graphics.DrawString(label, this.font, brush, x + 2f, rect.Y);
    }

    private void Caption(
        Graphics graphics, RectangleF pane, string status, List<RectangleF> labelRegistry)
    {
        var size = graphics.MeasureString(status, this.font);

        foreach (var row in CaptionRows)
        {
            var rect = new RectangleF(pane.Left + 3f, pane.Bottom - row, size.Width + 6f, size.Height);

            if (!ChartOverlay.TryReserve(labelRegistry, rect))
                continue;

            graphics.FillRectangle(this.back, rect);
            graphics.DrawString(status, this.font, this.text, pane.Left + 6f, rect.Y);
            return;
        }
    }

    private Pen Pen(Color color, bool dash)
    {
        var cache = dash ? this.dashed : this.solid;

        if (!cache.TryGetValue(color, out var pen))
        {
            pen = new Pen(color, dash ? 1f : 2f);

            if (dash)
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;

            cache[color] = pen;
        }

        return pen;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        this.font.Dispose();
        this.text.Dispose();
        this.back.Dispose();

        foreach (var pen in this.solid.Values)
            pen.Dispose();

        foreach (var pen in this.dashed.Values)
            pen.Dispose();

        this.solid.Clear();
        this.dashed.Clear();
    }
}
