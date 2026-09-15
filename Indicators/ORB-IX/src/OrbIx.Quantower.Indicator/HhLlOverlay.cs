// Port of the Pine Script v4 study "Higher High Lower Low Strategy",
// (c) LonesomeThecolor.blue, Mozilla Public License 2.0
// (https://mozilla.org/MPL/2.0/). This file remains under MPL 2.0.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OrbIx.Core.Structure;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>The script's line-style input: solid, dashed or dotted.</summary>
public enum HhLlLineStyle
{
    Dotted,
    Dashed,
    Solid,
}

/// <summary>One label chip, resolved to a drawable anchor by the fold.</summary>
internal readonly record struct HhLlLabelDraw(
    DateTime BarOpenUtc, HhLlLabelKind Kind, double AnchorPrice, bool Above);

/// <summary>One support/resistance line segment; EndUtc null = still extending right.</summary>
internal readonly record struct HhLlSegmentDraw(
    DateTime StartUtc, DateTime? EndUtc, double Price, bool IsResistance);

/// <summary>Immutable paint snapshot: the fold writes it, the paint reads it.</summary>
internal sealed record HhLlDrawable(HhLlLabelDraw[] Labels, HhLlSegmentDraw[] Segments)
{
    public static readonly HhLlDrawable Empty =
        new(Array.Empty<HhLlLabelDraw>(), Array.Empty<HhLlSegmentDraw>());
}

/// <summary>
/// Renders the HH/LL structure port: label chips on the pivot bars
/// (plotshape's labelup/labeldown at location.belowbar/abovebar, back-shifted
/// by rightBars — the engine already did the shifting) and the
/// support/resistance segments with the script's style/width/colour inputs.
///
/// Same disciplines as <see cref="ChartOverlay"/>: coordinates go through the
/// shared guarded <see cref="ChartOverlay.TryX"/>/<see cref="ChartOverlay.TryY"/>,
/// everything is clipped to the pane, and pens/brushes are cached — rebuilt
/// only when the inputs actually changed, never per frame.
///
/// Chip colours are the SCRIPT'S plotshape constants, not the sup/res inputs:
/// Pine hardcodes lime chips with black text for HL/HH and red chips with
/// white text for LL/LH (official v4 constants: lime #00E676, red #FF5252).
/// </summary>
internal sealed class HhLlOverlay : IDisposable
{
    internal readonly record struct Options(
        bool ShowSupRes, Color SupportColor, Color ResistanceColor,
        HhLlLineStyle LineStyle, int LineWidth, double BarsWidth);

    private const float ChipGap = 4f;
    private const float ChipPadX = 3f;

    private static readonly Color PineLime = Color.FromArgb(0x00, 0xE6, 0x76);
    private static readonly Color PineRed = Color.FromArgb(0xFF, 0x52, 0x52);

    private readonly Font chipFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush limeChip = new(PineLime);
    private readonly SolidBrush redChip = new(PineRed);
    private readonly SolidBrush blackText = new(Color.Black);
    private readonly SolidBrush whiteText = new(Color.White);

    private Pen? supPen;
    private Pen? resPen;
    private (Color Sup, Color Res, HhLlLineStyle Style, int Width) penKey;
    private bool disposed;

    /// <param name="labelRegistry">
    /// The frame's shared collision registry (ChartOverlay.BeginFrame). Chips reserve
    /// their rectangles here and SKIP when the pixels are taken, so session tags and
    /// chips never print through each other — a deliberate deviation from plotshape,
    /// which never skips (README's HH/LL deviations table).
    /// </param>
    public void Draw(Graphics graphics, IChartWindow window,
                     HhLlDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed
            || (drawable.Labels.Length == 0 && drawable.Segments.Length == 0))
        {
            return;
        }

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        float top = pane.Top;
        float bottom = pane.Bottom;
        float halfBar = (float)(options.BarsWidth / 2.0);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            if (options.ShowSupRes)
            {
                this.EnsurePens(options);

                foreach (var segment in drawable.Segments)
                {
                    if (!ChartOverlay.TryY(converter, segment.Price, top, bottom, out var y))
                        continue;

                    // Pinned to a pane edge means off-scale (ChartOverlay's rule).
                    if (y <= top || y >= bottom)
                        continue;

                    if (!ChartOverlay.TryX(converter, segment.StartUtc, out var x1))
                        continue;

                    float x2;
                    if (segment.EndUtc is { } endUtc)
                    {
                        if (!ChartOverlay.TryX(converter, endUtc, out x2))
                            continue;
                        x2 += halfBar;
                    }
                    else
                    {
                        x2 = pane.Right;    // line.extend.right
                    }

                    x1 += halfBar;
                    if (x2 <= pane.Left || x1 >= pane.Right || x2 <= x1)
                        continue;

                    var pen = segment.IsResistance ? this.resPen! : this.supPen!;
                    graphics.DrawLine(pen, x1, y, x2, y);
                }
            }

            foreach (var label in drawable.Labels)
            {
                if (!ChartOverlay.TryX(converter, label.BarOpenUtc, out var x))
                    continue;

                x += halfBar;
                if (x < pane.Left - 40f || x > pane.Right + 40f)
                    continue;

                if (!ChartOverlay.TryY(converter, label.AnchorPrice, top, bottom, out var y))
                    continue;

                string text = label.Kind switch
                {
                    HhLlLabelKind.HigherHigh => "HH",
                    HhLlLabelKind.HigherLow => "HL",
                    HhLlLabelKind.LowerLow => "LL",
                    _ => "LH",
                };
                bool bullish = label.Kind is HhLlLabelKind.HigherHigh
                    or HhLlLabelKind.HigherLow;

                var size = graphics.MeasureString(text, this.chipFont);
                float chipY = label.Above
                    ? y - ChipGap - size.Height
                    : y + ChipGap;
                var rect = new RectangleF(
                    x - (size.Width / 2f) - ChipPadX, chipY,
                    size.Width + (2f * ChipPadX), size.Height);

                if (!ChartOverlay.TryReserve(labelRegistry, rect))
                    continue;

                graphics.FillRectangle(bullish ? this.limeChip : this.redChip, rect);
                graphics.DrawString(text, this.chipFont,
                                    bullish ? this.blackText : this.whiteText,
                                    rect.Left + ChipPadX, rect.Top);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void EnsurePens(in Options options)
    {
        var key = (options.SupportColor, options.ResistanceColor,
                   options.LineStyle, options.LineWidth);

        if (this.supPen is not null && key == this.penKey)
            return;

        this.supPen?.Dispose();
        this.resPen?.Dispose();

        var dash = options.LineStyle switch
        {
            HhLlLineStyle.Solid => DashStyle.Solid,
            HhLlLineStyle.Dashed => DashStyle.Dash,
            _ => DashStyle.Dot,
        };

        this.supPen = new Pen(options.SupportColor, options.LineWidth) { DashStyle = dash };
        this.resPen = new Pen(options.ResistanceColor, options.LineWidth) { DashStyle = dash };
        this.penKey = key;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.chipFont.Dispose();
        this.limeChip.Dispose();
        this.redChip.Dispose();
        this.blackText.Dispose();
        this.whiteText.Dispose();
        this.supPen?.Dispose();
        this.resPen?.Dispose();
    }
}
