using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OceansAnchor;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// One zone resolved to a drawable state, ready to paint as a box.
/// </summary>
/// <param name="StartUtc">Open time of the bar the zone's own HH/LL level formed on.</param>
/// <param name="Top">Top edge of the zone's buffer-widened band (<c>zone.Top</c>).</param>
/// <param name="Bottom">Bottom edge of the same band (<c>zone.Bottom</c>).</param>
/// <param name="Price">The zone's own level (HH/LL price this indicator built the zone from) —
/// still carried for the state label and the reversal triangle's anchor.</param>
/// <param name="IsLong">Whether this is the support/long-side zone or the resistance/short one.</param>
/// <param name="State">Dormant is never passed here — see <see cref="AnchorGateOverlay"/>.</param>
/// <param name="ClusterLow">The confirming print's stop-reference low, once one has fired.</param>
/// <param name="ClusterHigh">The confirming print's stop-reference high, once one has fired.</param>
/// <param name="IsReversalSignal">
/// True when this zone is the OPPOSITE side of the current direction-callout bias, price has
/// run at least the configured distance from the bias line in the trend's own direction (not a
/// violation of it), and this zone has reached Confirmed — the operator's own "reversal
/// incoming" ask (2026-09-16). Swaps the label text AND draws a triangle marker; the zone's own
/// Dormant/Armed/Triggered/Confirmed/Expired/Broken lifecycle is unaffected either way.
/// </param>
/// <param name="TargetPrice">
/// Set only while this zone is Confirmed AND the direction callout's bias agrees with its side
/// — the same two-signal bar the strategy uses before it would actually enter. Null otherwise.
/// Draws "ENTER LONG/SHORT NOW" at the zone and a target line/label at this price.
/// </param>
/// <param name="TargetLabel">Which level supplied <see cref="TargetPrice"/> — HH/LL, VWAP, a
/// prior day/week level, a volume node, or "fallback" when nothing qualified.</param>
internal readonly record struct AnchorGateZoneDraw(
    DateTime StartUtc, double Top, double Bottom, double Price, bool IsLong, SignalState State,
    double? ClusterLow, double? ClusterHigh, bool IsReversalSignal = false,
    double? TargetPrice = null, string? TargetLabel = null);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
/// <param name="ConflictWarning">
/// True when the Anchor Gate has zones active on BOTH sides at once, or the bias line's own
/// verdict is Mixed/Undecided — the signals disagree with themselves, so entering either
/// direction right now is a coin flip regardless of price. The operator's own "dont enter in
/// this area" ask (2026-09-16), the half of it that isn't tied to a volume-node price band —
/// see VolumeNodeDraw.IsNoEntry for that half. Drawn as a standing banner, not a price zone.
/// </param>
internal sealed record AnchorGateDrawable(AnchorGateZoneDraw[] Zones, bool ConflictWarning = false)
{
    public static readonly AnchorGateDrawable Empty = new(Array.Empty<AnchorGateZoneDraw>());
}

/// <summary>
/// Draws Ocean's Anchor's absorption gate: a filled+bordered box over the zone's own price band
/// (2026-09-16 — was a full-width line; the operator wanted absorption levels drawn as boxes,
/// same as everything else on this chart that means "a zone", not "a level"), coloured and
/// labelled by its current state (Armed/Triggered/Confirmed), with the confirming print's stop
/// reference shown once one has fired, and a triangle marker on a reversal-incoming read. See
/// Ported/src/oceans-anchor for the state machine this reads (AnchorState.cs's SignalEngine);
/// this type only paints whatever it is handed.
/// </summary>
internal sealed class AnchorGateOverlay : IDisposable
{
    internal readonly record struct Options(Color LongColor, Color ShortColor, Color NoEntryColor);

    private const float TriangleSize = 7f;

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly Font enterFont = new(FontFamily.GenericSansSerif, 10f, FontStyle.Bold);
    private readonly Font warningFont = new(FontFamily.GenericSansSerif, 9f, FontStyle.Bold);
    private readonly Dictionary<int, Pen> borders = new();
    private readonly Dictionary<int, Pen> dashedLines = new();
    private readonly Dictionary<int, SolidBrush> fills = new();
    private readonly Dictionary<int, SolidBrush> labelBrushes = new();
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private bool disposed;

    public void Draw(
        Graphics graphics, IChartWindow window, AnchorGateDrawable drawable,
        in Options options, List<RectangleF> labelRegistry)
    {
        if (this.disposed)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            // Standing banner, not a price zone — the "signals disagree with each other" half
            // of "dont enter in this area". Fixed pixel position (top-centre) since there is no
            // single price this warning is about; see AnchorGateDrawable.ConflictWarning.
            if (drawable.ConflictWarning)
            {
                const string warnText = "⚠ DON'T ENTER — SIGNALS CONFLICTING";
                var warnSize = graphics.MeasureString(warnText, this.warningFont);
                var warnRect = new RectangleF(
                    pane.Left + ((pane.Width - warnSize.Width) / 2f) - 6f, pane.Top + 8f,
                    warnSize.Width + 12f, warnSize.Height + 6f);

                using var warnText_ = new SolidBrush(options.NoEntryColor);
                using var warnBorder = new Pen(Color.FromArgb(255, options.NoEntryColor), 1.5f);
                graphics.FillRectangle(this.labelBack, warnRect);
                graphics.DrawRectangle(warnBorder, warnRect.X, warnRect.Y, warnRect.Width, warnRect.Height);
                graphics.DrawString(warnText, this.warningFont, warnText_, warnRect.Left + 6f, warnRect.Top + 3f);
            }

            if (drawable.Zones.Length == 0)
                return;

            foreach (var zone in drawable.Zones)
            {
                if (zone.State == SignalState.Dormant)
                    continue;

                if (!ChartOverlay.TryY(converter, zone.Top, pane.Top, pane.Bottom, out var yTop)
                    || !ChartOverlay.TryY(converter, zone.Bottom, pane.Top, pane.Bottom, out var yBottom)
                    || !ChartOverlay.TryX(converter, zone.StartUtc, out var x1))
                {
                    continue;
                }

                var colour = zone.IsLong ? options.LongColor : options.ShortColor;
                var live = zone.State is SignalState.Armed or SignalState.Triggered or SignalState.Confirmed;

                var left = Math.Max(x1, pane.Left);
                var right = pane.Right; // still extending: same "carry to now" convention WickAbsorptionOverlay uses
                var top = Math.Min(yTop, yBottom);
                var height = Math.Max(Math.Abs(yBottom - yTop), 2f);

                if (right > left)
                {
                    graphics.FillRectangle(this.Fill(colour), left, top, right - left, height);
                    graphics.DrawRectangle(live ? this.Border(colour) : this.Dashed(colour), left, top, right - left, height);
                }

                if (!ChartOverlay.TryY(converter, zone.Price, pane.Top, pane.Bottom, out var y)
                    || y <= pane.Top || y >= pane.Bottom)
                {
                    continue;
                }

                var text = zone.IsReversalSignal
                    ? $"POSSIBLE REVERSAL INCOMING — {(zone.IsLong ? "LONG" : "SHORT")}"
                    : $"{(zone.IsLong ? "LONG" : "SHORT")} {DescribeState(zone.State)}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(pane.Right - size.Width - 10f, y - size.Height - 2f, size.Width + 6f, size.Height + 2f);

                if (ChartOverlay.TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    graphics.DrawString(text, this.font, this.LabelBrush(colour), rect.Left + 3f, rect.Top + 1f);
                }

                // The reversal triangle: points toward the bias line (up for a long reversal —
                // price would need to climb back to it; down for a short one), at the CONFIRMING
                // zone's own price, not the bias line's — this marks WHERE the reversal evidence
                // was found, the bias line itself is drawn elsewhere.
                if (zone.IsReversalSignal)
                {
                    var brush = this.LabelBrush(colour);
                    PointF[] triangle = zone.IsLong
                        ? new[]
                        {
                            new PointF(rect.Left - 10f, y - TriangleSize), new PointF(rect.Left - 10f - (TriangleSize * 2f), y),
                            new PointF(rect.Left - 10f, y + TriangleSize),
                        }
                        : new[]
                        {
                            new PointF(rect.Left - 10f, y - TriangleSize), new PointF(rect.Left - 10f - (TriangleSize * 2f), y),
                            new PointF(rect.Left - 10f, y + TriangleSize),
                        };

                    graphics.FillPolygon(brush, triangle);
                }

                // The stop reference, once a print has actually confirmed one — this is the
                // price the trade's premise breaks at, not a cosmetic extra.
                if (zone.State == SignalState.Confirmed && zone.ClusterLow is { } lo && zone.ClusterHigh is { } hi)
                {
                    var stopPrice = zone.IsLong ? lo : hi;

                    if (ChartOverlay.TryY(converter, stopPrice, pane.Top, pane.Bottom, out var stopY)
                        && stopY > pane.Top && stopY < pane.Bottom)
                    {
                        graphics.DrawLine(this.Dashed(colour), pane.Right - 120f, stopY, pane.Right, stopY);
                    }
                }

                // ENTER LONG/SHORT NOW + target — only set at all when Confirmed AND the bias
                // line agrees (see OrbIxIndicator's own entry-marking gate), so its mere
                // presence here already means both signals agreed. Drawn LOUDER than every other
                // label here (bigger font, solid fill instead of translucent) — the operator's
                // own ask (2026-09-16): a clear go signal, not another line of small text among
                // the rest ("ENTER ... NOW" replacing the original "... HERE" wording).
                if (zone.TargetPrice is { } target)
                {
                    var enterText = $"ENTER {(zone.IsLong ? "LONG" : "SHORT")} NOW";
                    var enterSize = graphics.MeasureString(enterText, this.enterFont);
                    var enterRect = new RectangleF(left + 4f, top - enterSize.Height - 8f, enterSize.Width + 14f, enterSize.Height + 6f);

                    if (ChartOverlay.TryReserve(labelRegistry, enterRect))
                    {
                        using var enterFill = new SolidBrush(Color.FromArgb(235, colour));
                        graphics.FillRectangle(enterFill, enterRect);
                        graphics.DrawRectangle(this.Border(colour), enterRect.X, enterRect.Y, enterRect.Width, enterRect.Height);
                        graphics.DrawString(enterText, this.enterFont, Brushes.Black, enterRect.Left + 7f, enterRect.Top + 3f);
                    }

                    if (ChartOverlay.TryY(converter, target, pane.Top, pane.Bottom, out var targetY)
                        && targetY > pane.Top && targetY < pane.Bottom)
                    {
                        graphics.DrawLine(this.Border(colour), pane.Left, targetY, pane.Right, targetY);

                        var targetText = $"TARGET ({zone.TargetLabel})";
                        var targetSize = graphics.MeasureString(targetText, this.font);
                        var targetRect = new RectangleF(pane.Left + 4f, targetY - targetSize.Height - 1f, targetSize.Width + 6f, targetSize.Height);

                        if (ChartOverlay.TryReserve(labelRegistry, targetRect))
                        {
                            graphics.FillRectangle(this.labelBack, targetRect);
                            graphics.DrawString(targetText, this.font, this.LabelBrush(colour), targetRect.Left + 3f, targetRect.Top);
                        }
                    }
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private static string DescribeState(SignalState state) => state switch
    {
        SignalState.Armed => "ARMED",
        SignalState.Triggered => "TRIGGERED",
        SignalState.Confirmed => "CONFIRMED",
        SignalState.Expired => "expired",
        SignalState.Broken => "broken",
        _ => "—",
    };

    private SolidBrush Fill(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.fills.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(Color.FromArgb(50, colour));
            this.fills[key] = brush;
        }

        return brush;
    }

    private Pen Border(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.borders.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(200, colour), 1.5f) { DashStyle = DashStyle.Solid };
            this.borders[key] = pen;
        }

        return pen;
    }

    private Pen Dashed(Color colour)
    {
        var key = colour.ToArgb();
        if (!this.dashedLines.TryGetValue(key, out var pen))
        {
            pen = new Pen(Color.FromArgb(140, colour), 1f) { DashStyle = DashStyle.Dot };
            this.dashedLines[key] = pen;
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
        this.enterFont.Dispose();
        this.warningFont.Dispose();
        this.labelBack.Dispose();

        foreach (var pen in this.borders.Values) pen.Dispose();
        foreach (var pen in this.dashedLines.Values) pen.Dispose();
        foreach (var brush in this.fills.Values) brush.Dispose();
        foreach (var brush in this.labelBrushes.Values) brush.Dispose();

        this.borders.Clear();
        this.dashedLines.Clear();
        this.fills.Clear();
        this.labelBrushes.Clear();
    }
}
