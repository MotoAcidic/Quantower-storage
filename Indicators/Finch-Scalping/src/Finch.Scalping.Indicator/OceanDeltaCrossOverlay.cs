using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OceansDelta;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// One confirmed session-delta flip, resolved to a drawable cross.
/// </summary>
/// <param name="CrossedBarCloseUtc">
/// When the bar that CROSSED closed — the vertical sits here, not on the (often later) bar that
/// confirmed the flip, per Ocean Delta Cross's own rule: the confirmation is evidence about a
/// moment that already passed, and anchoring the mark there would put it on the wrong bar.
/// </param>
/// <param name="Up">True if buyers took the session, false if sellers did.</param>
/// <param name="Price">Where the horizontal arm draws — see <see cref="PriceNote"/> for what it
/// actually is.</param>
/// <param name="FromCluster">
/// True when <see cref="Price"/> came from a genuine one-sided cluster inside the crossing bar
/// (the price that was actually PAID for the flip); false means it fell back to the bar's own
/// close. An unbacked flip still draws — dashed, not hidden — per Ocean Delta Cross's own design
/// note: "a flip nobody paid for is still information."
/// </param>
/// <param name="PriceNote">The honest label for where <see cref="Price"/> came from — "close",
/// "flip cluster", or "close — no cluster carried it". Never shortened to just "close" when a
/// cluster search actually ran and came up empty; that distinction is the whole point of the
/// wording, per Ported/src/oceans-delta's own CLAUDE.md.</param>
internal readonly record struct OceanCrossDraw(
    DateTime CrossedBarCloseUtc, bool Up, double Price, bool FromCluster, string PriceNote);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record OceanCrossDrawable(OceanCrossDraw[] Crosses)
{
    public static readonly OceanCrossDrawable Empty = new(Array.Empty<OceanCrossDraw>());
}

/// <summary>
/// "Ocean Delta Cross" (the operator's own ATAS indicator, ported 2026-09-22): a vertical mark on
/// the bar where SESSION cumulative delta confirmed a change of side, plus a horizontal at the
/// price the flip actually happened at — the crossing bar's close by default, or (when a genuine
/// one-sided print inside that bar qualifies) the CLUSTER that carried the flip, which is the
/// price the reversal was actually paid for rather than merely the price it was recorded at.
///
/// Ported from <c>Ported/src/oceans-delta/DeltaMath.cs</c> (this indicator's own
/// `DeltaEngine`/`ClusterSearch`/`CrossMath`, 123 tests + a mutation pass in that bundle's own
/// suite, platform-free) — see <see cref="OrbIxIndicator"/>'s own fold sequence for how it is fed
/// footprint cells from THIS indicator's own <c>FootprintEngine</c> rather than ATAS's
/// `candle.GetAllPriceLevels()`. Only the cross itself is ported — the richer ATAS original also
/// draws multi-session flip-zone bands and "lines in the sand" iceberg levels, deliberately left
/// for later per this project's own "one feature at a time" discipline.
/// </summary>
internal sealed class OceanDeltaCrossOverlay : IDisposable
{
    internal readonly record struct Options(Color UpColor, Color DownColor);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly Dictionary<int, Pen> verticalPens = new();
    private readonly Dictionary<int, Pen> solidHorizontalPens = new();
    private readonly Dictionary<int, Pen> dashedHorizontalPens = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, OceanCrossDrawable drawable,
        in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Crosses.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var cross in drawable.Crosses)
            {
                var colour = cross.Up ? options.UpColor : options.DownColor;

                if (ChartOverlay.TryX(converter, cross.CrossedBarCloseUtc, out var x)
                    && x >= pane.Left && x <= pane.Right)
                {
                    graphics.DrawLine(this.VerticalPen(colour), x, pane.Top, x, pane.Bottom);

                    var vText = cross.Up ? "FLIP UP" : "FLIP DOWN";
                    var vSize = graphics.MeasureString(vText, this.font);
                    var vRect = new RectangleF(x + 3f, pane.Top + 4f, vSize.Width + 6f, vSize.Height + 2f);

                    if (ChartOverlay.TryReserve(labelRegistry, vRect))
                    {
                        graphics.FillRectangle(this.labelBack, vRect);
                        graphics.DrawString(vText, this.font, this.LabelBrush(colour), vRect.Left + 3f, vRect.Top + 1f);
                    }
                }

                if (!ChartOverlay.TryY(converter, cross.Price, pane.Top, pane.Bottom, out var y)
                    || y <= pane.Top || y >= pane.Bottom)
                {
                    continue;
                }

                var pen = cross.FromCluster ? this.SolidHorizontalPen(colour) : this.DashedHorizontalPen(colour);
                graphics.DrawLine(pen, pane.Left, y, pane.Right, y);

                var hText = $"Δ cross ({cross.PriceNote})";
                var hSize = graphics.MeasureString(hText, this.font);
                var hRect = new RectangleF(pane.Right - hSize.Width - 10f, y - hSize.Height - 1f, hSize.Width + 6f, hSize.Height);

                if (ChartOverlay.TryReserve(labelRegistry, hRect))
                {
                    graphics.FillRectangle(this.labelBack, hRect);
                    graphics.DrawString(hText, this.font, this.LabelBrush(colour), hRect.Left + 3f, hRect.Top);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private Pen VerticalPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.verticalPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(200, colour), 1.5f);
            this.verticalPens[key] = pen;
        }

        return pen;
    }

    private Pen SolidHorizontalPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.solidHorizontalPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(200, colour), 1.5f);
            this.solidHorizontalPens[key] = pen;
        }

        return pen;
    }

    private Pen DashedHorizontalPen(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.dashedHorizontalPens.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(170, colour), 1.25f) { DashStyle = DashStyle.Dash };
            this.dashedHorizontalPens[key] = pen;
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

        foreach (var pen in this.verticalPens.Values) pen.Dispose();
        foreach (var pen in this.solidHorizontalPens.Values) pen.Dispose();
        foreach (var pen in this.dashedHorizontalPens.Values) pen.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.verticalPens.Clear();
        this.solidHorizontalPens.Clear();
        this.dashedHorizontalPens.Clear();
        this.labelBrushes.Clear();
    }
}
