using System;
using System.Drawing;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// The rich "ABSORPTION" panel: a standing box (fixed pixel offset, not anchored to a price)
/// showing whichever Anchor Gate zone is currently most active, with the effort-vs-result read
/// behind its gate verdict spelled out in full. Designed from a screenshot of a friend's chart
/// (2026-09-16) — searched the whole Ported/ bundle for its exact wording first and found no
/// match, so this is built fresh here, informed by that screenshot and by oceans-effort's own
/// "effort vs. result" idea: a genuine move has price and the order flow agreeing; absorption is
/// the divergence between them. See BuildAnchorAbsorptionPanelDrawable in OrbIxIndicator.cs for
/// the classifier itself — this type only paints whatever it is handed.
/// </summary>
/// <param name="Show">False draws nothing — no zone is Armed or later right now.</param>
/// <param name="IsLong">Which side the ACTIVE zone is testing (support/long or resistance/short).</param>
/// <param name="IsVeto">
/// True = "Veto": price and delta agree, this reads as a genuine directional move, not
/// absorption. False = "Confirm": they disagree — absorption is the more likely reading.
/// </param>
/// <param name="DeltaPrice">Price change since this zone's CURRENT arming began.</param>
/// <param name="SigmaDelta">Net delta since this zone's CURRENT arming began.</param>
/// <param name="BarsSinceArm">Bars elapsed since arming, for the progress row.</param>
/// <param name="ClockBars">The confirmation clock's own bar count (ZoneClockBars) — the
/// progress row's denominator.</param>
/// <param name="Reasoning">One templated sentence explaining the verdict.</param>
/// <param name="LongsText">The LONGS guidance box's text.</param>
/// <param name="ShortsText">The SHORTS guidance box's text.</param>
/// <param name="LongsGood">Whether the LONGS box reads as "in your favour" (green) or "stand
/// aside" (muted) — drives its colour, independent of <see cref="IsLong"/>.</param>
/// <param name="ShortsGood">The SHORTS box's equivalent.</param>
internal sealed record AnchorAbsorptionPanelDrawable(
    bool Show, bool IsLong, bool IsVeto, double DeltaPrice, decimal SigmaDelta,
    int BarsSinceArm, int ClockBars, string Reasoning, string LongsText, string ShortsText,
    bool LongsGood, bool ShortsGood)
{
    public static readonly AnchorAbsorptionPanelDrawable Empty = new(
        false, false, false, 0, 0m, 0, 0, string.Empty, string.Empty, string.Empty, false, false);
}

/// <summary>Paints the ABSORPTION panel described above, at a fixed pixel offset from the pane's corner.</summary>
internal sealed class AnchorAbsorptionPanelOverlay : IDisposable
{
    internal readonly record struct Options(
        int OffsetX, int OffsetY, Color LongColor, Color ShortColor, Color GoodColor, Color MutedColor);

    private const float PanelWidth = 320f;
    private const float Padding = 10f;

    private readonly Font headerFont = new(FontFamily.GenericSansSerif, 9f, FontStyle.Bold);
    private readonly Font headlineFont = new(FontFamily.GenericSansSerif, 13f, FontStyle.Bold);
    private readonly Font statFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly Font statValueFont = new(FontFamily.GenericSansSerif, 12f, FontStyle.Bold);
    private readonly Font bodyFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Italic);
    private readonly Font guidanceFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);

    private readonly SolidBrush panelBack = new(Color.FromArgb(235, 14, 16, 22));
    private readonly SolidBrush statBack = new(Color.FromArgb(255, 22, 25, 34));
    private readonly SolidBrush headerText = new(Color.FromArgb(255, 200, 205, 215));
    private readonly SolidBrush badgeBack = new(Color.FromArgb(255, 30, 34, 46));
    private readonly SolidBrush white = new(Color.White);
    private bool disposed;

    public void Draw(Graphics graphics, RectangleF area, AnchorAbsorptionPanelDrawable panel, in Options options)
    {
        if (this.disposed || !panel.Show)
            return;

        var sideColour = panel.IsLong ? options.LongColor : options.ShortColor;
        var x = area.Left + options.OffsetX;
        var y = area.Top + options.OffsetY;

        using var sideBrush = new SolidBrush(sideColour);
        using var goodBrush = new SolidBrush(options.GoodColor);
        using var mutedBrush = new SolidBrush(options.MutedColor);

        // Height is measured up front from fixed row heights, so the background is sized to the
        // real content rather than a guess that clips the last row.
        const float headerRowH = 22f, headlineRowH = 26f, dotsRowH = 14f, statsRowH = 44f,
                    reasoningRowH = 30f, guidanceRowH = 38f, gap = 8f;
        var height = headerRowH + headlineRowH + dotsRowH + gap + statsRowH + gap + reasoningRowH + gap + guidanceRowH + (Padding * 2f);

        var panelRect = new RectangleF(x, y, PanelWidth, height);
        if (!float.IsFinite(panelRect.X) || !float.IsFinite(panelRect.Y) || panelRect.Width <= 0 || panelRect.Height <= 0)
            return;

        graphics.FillRectangle(this.panelBack, panelRect);

        var cx = x + Padding;
        var cy = y + Padding;
        var contentWidth = PanelWidth - (Padding * 2f);

        // Header: "ABSORPTION" left, "gate: Veto/Confirm" badge right.
        graphics.DrawString("ABSORPTION", this.headerFont, this.headerText, cx, cy);

        var gateText = panel.IsVeto ? "gate: Veto" : "gate: Confirm";
        var gateSize = graphics.MeasureString(gateText, this.statFont);
        var gateRect = new RectangleF(cx + contentWidth - gateSize.Width - 12f, cy - 1f, gateSize.Width + 10f, gateSize.Height + 4f);
        graphics.FillRectangle(this.badgeBack, gateRect);
        graphics.DrawString(gateText, this.statFont, this.white, gateRect.Left + 5f, gateRect.Top + 2f);

        cy += headerRowH;

        // Headline: "GEN BUY · 4 bars" / "GEN SELL · 4 bars" (Veto) or an absorption-side
        // headline (Confirm).
        var headline = panel.IsVeto
            ? (panel.DeltaPrice >= 0 ? "GEN BUY" : "GEN SELL")
            : (panel.IsLong ? "ABSORPTION" : "ABSORPTION");
        graphics.DrawString($"{headline}", this.headlineFont, sideBrush, cx, cy);

        var barsText = $" · {panel.BarsSinceArm} bar{(panel.BarsSinceArm == 1 ? string.Empty : "s")}";
        var headlineWidth = graphics.MeasureString(headline, this.headlineFont).Width;
        graphics.DrawString(barsText, this.statFont, this.headerText, cx + headlineWidth + 2f, cy + 8f);

        cy += headlineRowH;

        // Bars-since-arm progress, as a row of filled/empty pills against the confirmation clock.
        if (panel.ClockBars > 0)
        {
            const float dot = 16f, dotGap = 3f;
            var filled = Math.Min(panel.BarsSinceArm, panel.ClockBars);

            for (var i = 0; i < panel.ClockBars; i++)
            {
                var dx = cx + (i * (dot + dotGap));
                var dotRect = new RectangleF(dx, cy, dot, dotsRowH - 4f);
                graphics.FillRectangle(i < filled ? sideBrush : this.statBack, dotRect);
            }
        }

        cy += dotsRowH + gap;

        // Two stat boxes: deltaPRICE / sigmaDELTA.
        var statBoxWidth = (contentWidth - gap) / 2f;
        this.DrawStatBox(graphics, new RectangleF(cx, cy, statBoxWidth, statsRowH),
            "ΔPRICE", $"{panel.DeltaPrice:+0.0;-0.0;0}", sideBrush);
        this.DrawStatBox(graphics, new RectangleF(cx + statBoxWidth + gap, cy, statBoxWidth, statsRowH),
            "ΣDELTA", $"{panel.SigmaDelta:+#,0;-#,0;0}", sideBrush);

        cy += statsRowH + gap;

        // Reasoning, wrapped by GDI+ inside its own box.
        var reasoningRect = new RectangleF(cx, cy, contentWidth, reasoningRowH);
        graphics.DrawString(panel.Reasoning, this.bodyFont, this.headerText, reasoningRect);

        cy += reasoningRowH + gap;

        // LONGS / SHORTS guidance boxes.
        var guidanceBoxWidth = (contentWidth - gap) / 2f;
        this.DrawGuidanceBox(graphics, new RectangleF(cx, cy, guidanceBoxWidth, guidanceRowH),
            "▲ LONGS", panel.LongsText, panel.LongsGood ? goodBrush : mutedBrush);
        this.DrawGuidanceBox(graphics, new RectangleF(cx + guidanceBoxWidth + gap, cy, guidanceBoxWidth, guidanceRowH),
            "▼ SHORTS", panel.ShortsText, panel.ShortsGood ? goodBrush : mutedBrush);
    }

    private void DrawStatBox(Graphics graphics, RectangleF rect, string label, string value, Brush valueBrush)
    {
        graphics.FillRectangle(this.statBack, rect);
        graphics.DrawString(label, this.statFont, this.headerText, rect.Left + 6f, rect.Top + 4f);
        graphics.DrawString(value, this.statValueFont, valueBrush, rect.Left + 6f, rect.Top + 16f);
    }

    private void DrawGuidanceBox(Graphics graphics, RectangleF rect, string header, string body, Brush bodyBrush)
    {
        graphics.FillRectangle(this.statBack, rect);
        graphics.DrawString(header, this.guidanceFont, bodyBrush, rect.Left + 6f, rect.Top + 3f);
        graphics.DrawString(body, this.statFont, this.headerText, new RectangleF(rect.Left + 6f, rect.Top + 15f, rect.Width - 10f, rect.Height - 15f));
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.headerFont.Dispose();
        this.headlineFont.Dispose();
        this.statFont.Dispose();
        this.statValueFont.Dispose();
        this.bodyFont.Dispose();
        this.guidanceFont.Dispose();
        this.panelBack.Dispose();
        this.statBack.Dispose();
        this.headerText.Dispose();
        this.badgeBack.Dispose();
        this.white.Dispose();
    }
}
