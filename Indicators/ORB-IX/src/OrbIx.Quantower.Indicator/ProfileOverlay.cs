using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using OrbIx.Core.Features;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// One computed profile ready to paint. <see cref="EndUtc"/> is the publish
/// instant for an open-ended (anchored) profile, whose right edge follows the
/// pane instead.
/// </summary>
internal sealed record ProfileDraw(
    string Title,
    DateTime StartUtc,
    DateTime EndUtc,
    bool OpenEnded,
    VolumeProfileResult Result,
    double RowHeight,
    double ValueAreaPercent,
    string Source);

/// <summary>Immutable Wave-2 paint snapshot: the fold writes it, the paint reads it.</summary>
/// <summary>
/// The profiles to paint. It carries NO status string: each profile states its own
/// coverage beside its title, the measured-null label is a constant that goes to the log,
/// and anything actually wrong is drawn by <see cref="StatusOverlay"/> on the single
/// problems line.
/// </summary>
internal sealed record ProfileDrawable(ProfileDraw[] Profiles)
{
    public static readonly ProfileDrawable Empty = new(Array.Empty<ProfileDraw>());
}

/// <summary>
/// Renders the master plan's Wave 2: the fixed-range and anchored volume
/// profiles — per-row buy/sell/unclassified bars, value-area emphasis, and
/// the POC/VAH/VAL lines. There is no pending-click marker: a range commits on
/// ONE click, so no half-selected state exists to draw. Same disciplines as
/// every overlay here: guarded conversion
/// via <see cref="ChartOverlay.TryX"/>/<see cref="ChartOverlay.TryY"/>, pane
/// clipping restored in a finally, cached pens/brushes, labels through the
/// frame's shared collision registry.
///
/// DISPLAY ONLY, stated on the chart: profile-derived levels measured
/// non-predictive here (levels-null, 572 candidates over 31 sessions).
/// </summary>
internal sealed class ProfileOverlay : IDisposable
{
    internal readonly record struct Options(
        Color UpColor, Color DownColor, Color PocColor, Color ValueAreaLineColor,
        int WidthPercent);

    private const int ValueAreaAlpha = 150;
    private const int OutsideValueAreaAlpha = 55;
    private const int UnclassifiedAlpha = 90;

    private readonly Font tagFont = new(FontFamily.GenericSansSerif, 8f, FontStyle.Regular);
    private readonly SolidBrush tagText = new(Color.Gainsboro);
    private readonly SolidBrush tagBack = new(Color.FromArgb(160, 24, 26, 32));
    private readonly SolidBrush unclassifiedInVa = new(Color.FromArgb(ValueAreaAlpha, 120, 124, 134));
    private readonly SolidBrush unclassifiedOutVa = new(Color.FromArgb(OutsideValueAreaAlpha, 120, 124, 134));
    private SolidBrush? upInVa;
    private SolidBrush? upOutVa;
    private SolidBrush? downInVa;
    private SolidBrush? downOutVa;
    private Pen? pocPen;
    private Pen? vaPen;
    private (Color Up, Color Down, Color Poc, Color Va) resourceKey;
    private bool disposed;

    public void Draw(Graphics graphics, IChartWindow window,
                     ProfileDrawable drawable, in Options options,
                     List<RectangleF> labelRegistry)
    {
        if (this.disposed)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;
        this.EnsureResources(options);

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        try
        {
            foreach (var profile in drawable.Profiles)
                this.DrawProfile(graphics, converter, pane, profile, options, labelRegistry);

        }
        finally
        {
            graphics.Clip = previousClip;
        }
    }

    private void DrawProfile(
        Graphics graphics, IChartWindowCoordinatesConverter converter,
        RectangleF pane, ProfileDraw profile, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.upInVa is null || this.downInVa is null || this.upOutVa is null
            || this.downOutVa is null || this.pocPen is null || this.vaPen is null)
        {
            return;
        }

        if (!ChartOverlay.TryX(converter, profile.StartUtc, out var startX))
            return;

        float left = Math.Max(startX, pane.Left);
        float right;
        if (profile.OpenEnded)
        {
            right = pane.Right;
        }
        else
        {
            if (!ChartOverlay.TryX(converter, profile.EndUtc, out var endX))
                return;
            right = Math.Min(endX, pane.Right);
        }

        if (right <= left + 2f)
            return;

        var result = profile.Result;
        double maxTotal = 0;
        foreach (var row in result.Rows)
            maxTotal = Math.Max(maxTotal, row.TotalVolume);
        if (maxTotal <= 0)
            return;

        float maxLength = (right - left) * (Math.Clamp(options.WidthPercent, 1, 100) / 100f);

        for (var i = 0; i < result.Rows.Length; i++)
        {
            var row = result.Rows[i];
            if (row.TotalVolume <= 0)
                continue;

            if (!ChartOverlay.TryY(converter, row.Price, pane.Top, pane.Bottom, out var yBottom)
                || !ChartOverlay.TryY(converter, row.Price + profile.RowHeight,
                                      pane.Top, pane.Bottom, out var yTop))
            {
                continue;
            }

            var height = Math.Max(1f, yBottom - yTop - 1f);
            if (yTop > pane.Bottom || yBottom < pane.Top)
                continue;

            var inValueArea = i >= result.ValueAreaLowIndex && i <= result.ValueAreaHighIndex;
            float length = (float)(maxLength * (row.TotalVolume / maxTotal));
            float buyLength = (float)(length * (row.BuyVolume / row.TotalVolume));
            float sellLength = (float)(length * (row.SellVolume / row.TotalVolume));
            float unclassifiedLength = Math.Max(0f, length - buyLength - sellLength);

            float x = left;
            if (buyLength > 0f)
            {
                graphics.FillRectangle(inValueArea ? this.upInVa : this.upOutVa,
                                       x, yTop, buyLength, height);
                x += buyLength;
            }

            if (sellLength > 0f)
            {
                graphics.FillRectangle(inValueArea ? this.downInVa : this.downOutVa,
                                       x, yTop, sellLength, height);
                x += sellLength;
            }

            if (unclassifiedLength > 0f)
            {
                graphics.FillRectangle(
                    inValueArea ? this.unclassifiedInVa : this.unclassifiedOutVa,
                    x, yTop, unclassifiedLength, height);
            }
        }

        this.DrawLevelLine(graphics, converter, pane, labelRegistry, left, right,
                           result.PocPrice + (profile.RowHeight / 2.0), this.pocPen,
                           $"{profile.Title} POC {result.PocPrice + (profile.RowHeight / 2.0):N2}");
        this.DrawLevelLine(graphics, converter, pane, labelRegistry, left, right,
                           result.VahPrice + profile.RowHeight, this.vaPen,
                           $"{profile.Title} VAH {result.VahPrice + profile.RowHeight:N2}");
        this.DrawLevelLine(graphics, converter, pane, labelRegistry, left, right,
                           result.ValPrice, this.vaPen,
                           $"{profile.Title} VAL {result.ValPrice:N2}");

        var title = $"{profile.Title} {profile.ValueAreaPercent:N0}% · {profile.Source}";
        var titleSize = graphics.MeasureString(title, this.tagFont);
        if (ChartOverlay.TryY(converter, result.Rows[result.Rows.Length - 1].Price
                                          + profile.RowHeight,
                              pane.Top, pane.Bottom, out var titleY))
        {
            var rect = new RectangleF(left + 2f, Math.Max(pane.Top + 2f, titleY - titleSize.Height - 2f),
                                      titleSize.Width + 6f, titleSize.Height);
            if (ChartOverlay.TryReserve(labelRegistry, rect))
            {
                graphics.FillRectangle(this.tagBack, rect);
                graphics.DrawString(title, this.tagFont, this.tagText, rect.Left + 3f, rect.Top);
            }
        }
    }

    private void DrawLevelLine(
        Graphics graphics, IChartWindowCoordinatesConverter converter,
        RectangleF pane, List<RectangleF> labelRegistry,
        float left, float right, double price, Pen pen, string tag)
    {
        if (!ChartOverlay.TryY(converter, price, pane.Top, pane.Bottom, out var y)
            || y <= pane.Top || y >= pane.Bottom)
        {
            return;
        }

        graphics.DrawLine(pen, left, y, right, y);

        var size = graphics.MeasureString(tag, this.tagFont);
        var rect = new RectangleF(right - size.Width - 8f, y - size.Height - 1f,
                                  size.Width + 6f, size.Height);
        if (ChartOverlay.TryReserve(labelRegistry, rect))
        {
            graphics.FillRectangle(this.tagBack, rect);
            graphics.DrawString(tag, this.tagFont, this.tagText, rect.Left + 3f, rect.Top);
        }
    }


    private void EnsureResources(in Options options)
    {
        var key = (options.UpColor, options.DownColor, options.PocColor,
                   options.ValueAreaLineColor);
        if (this.upInVa is not null && key == this.resourceKey)
            return;

        this.upInVa?.Dispose();
        this.upOutVa?.Dispose();
        this.downInVa?.Dispose();
        this.downOutVa?.Dispose();
        this.pocPen?.Dispose();
        this.vaPen?.Dispose();

        this.upInVa = new SolidBrush(Color.FromArgb(ValueAreaAlpha, options.UpColor));
        this.upOutVa = new SolidBrush(Color.FromArgb(OutsideValueAreaAlpha, options.UpColor));
        this.downInVa = new SolidBrush(Color.FromArgb(ValueAreaAlpha, options.DownColor));
        this.downOutVa = new SolidBrush(Color.FromArgb(OutsideValueAreaAlpha, options.DownColor));
        this.pocPen = new Pen(options.PocColor, 1.6f);
        this.vaPen = new Pen(Color.FromArgb(170, options.ValueAreaLineColor), 1.2f)
        { DashStyle = DashStyle.Dash };
        this.resourceKey = key;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.tagFont.Dispose();
        this.tagText.Dispose();
        this.tagBack.Dispose();
        this.unclassifiedInVa.Dispose();
        this.unclassifiedOutVa.Dispose();
        this.upInVa?.Dispose();
        this.upOutVa?.Dispose();
        this.downInVa?.Dispose();
        this.downOutVa?.Dispose();
        this.pocPen?.Dispose();
        this.vaPen?.Dispose();
    }
}
