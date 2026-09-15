using System.Drawing;
using Qt = TradingPlatform.BusinessLayer;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// Draws the one line that reports something actually wrong, and nothing otherwise.
///
/// WHY IT PAINTS FIRST. Every overlay shares one collision registry per frame, and
/// <see cref="ChartOverlay.TryReserve"/> refuses a rectangle that overlaps one already
/// taken — the caller then silently skips drawing. "Skipping beats overlapping" is right
/// for a price label, and wrong for a fault report: a problem that vanishes because an
/// HH/LL chip happened to reserve those pixels first is the same class of silent failure
/// this whole overlay exists to end. Painting before anything else means it reserves
/// against an empty registry, so it cannot lose.
///
/// WHEN THERE IS NO PROBLEM IT RESERVES NOTHING, so a healthy chart is laid out exactly
/// as it was before this type existed.
/// </summary>
internal sealed class StatusOverlay
{
    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);

    // Matches the other overlays' tag treatment, so the line reads as part of the same
    // instrument rather than a foreign element.
    private readonly SolidBrush back = new(Color.FromArgb(160, 24, 26, 32));
    private readonly SolidBrush text = new(Color.Gainsboro);

    // The marker, and only the marker, carries the warning colour. Colouring the whole
    // line would make it shout on a chart the operator may be trading from.
    private readonly SolidBrush marker = new(Color.FromArgb(232, 163, 61));

    /// <summary>
    /// Paints the problems line at the foot of the pane. A blank line draws nothing at
    /// all — that is the healthy state, not a missing message.
    /// </summary>
    public void Draw(
        Graphics graphics, Qt.Chart.IChartWindow window, string status,
        System.Collections.Generic.List<RectangleF> labelRegistry)
    {
        if (status.Length == 0)
            return;

        var pane = window.ClientRectangle;
        var size = graphics.MeasureString(status, this.font);
        var rect = new RectangleF(
            pane.Left + 3f, pane.Bottom - 95f, size.Width + 6f, size.Height);

        // Reserved so later overlays dodge it, but drawn regardless of the answer: this
        // paints first, so a refusal could only mean the frame is already unusable.
        ChartOverlay.TryReserve(labelRegistry, rect);

        graphics.FillRectangle(this.back, rect);

        // The marker is measured and drawn separately so the rest of the line keeps the
        // ordinary tag colour; MeasureString on the marker alone gives its advance width.
        var markerText = Core.Diagnostics.StatusBlock.ProblemMarker;
        var markerWidth = graphics.MeasureString(markerText, this.font).Width;

        graphics.DrawString(markerText, this.font, this.marker, rect.Left + 3f, rect.Top);
        graphics.DrawString(
            status.Substring(markerText.Length), this.font, this.text,
            rect.Left + 3f + markerWidth, rect.Top);
    }
}
