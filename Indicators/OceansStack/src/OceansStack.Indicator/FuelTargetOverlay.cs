using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer.Chart;

namespace OceansStack;

/// <summary>The fuel pool a currently-ARMED zone is targeting — only present while armed and not yet
/// fired, matching `ocean.pine`'s own `label.new`/`label.delete` lifecycle for `lFuelT`/`lFuelB`
/// (deleted every bar and only recreated when armed).</summary>
internal readonly record struct FuelTargetDraw(double Price, string PoolName, bool IsTopZone);

/// <summary>Immutable paint snapshot: the poll writes it, the paint reads it.</summary>
internal sealed record FuelTargetDrawable(FuelTargetDraw[] Targets)
{
    public static readonly FuelTargetDrawable Empty = new(Array.Empty<FuelTargetDraw>());
}

/// <summary>
/// Generalizes the label-only half of Finch-Lite's own <c>PocOverlay</c> — a price label with no
/// accompanying line, since `ocean.pine` draws no line for a fuel target either, just a label at the
/// pool's own price.
/// </summary>
internal sealed class FuelTargetOverlay : IDisposable
{
    internal readonly record struct Options(Color Color);

    private readonly Font font = new(FontFamily.GenericSansSerif, 8f, FontStyle.Bold);
    private readonly SolidBrush labelBack = new(Color.FromArgb(190, 16, 18, 24));
    private readonly SolidBrush labelBrush;
    private bool disposed;

    public FuelTargetOverlay() => this.labelBrush = new SolidBrush(Color.Black);

    public void Draw(
        Graphics graphics, IChartWindow window, FuelTargetDrawable drawable, in Options options,
        List<RectangleF> labelRegistry)
    {
        if (this.disposed || drawable.Targets.Length == 0)
            return;

        var converter = window.CoordinatesConverter;
        var pane = window.ClientRectangle;

        if (converter is null || pane.Width <= 0f || pane.Height <= 0f)
            return;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);
        this.labelBrush.Color = options.Color;

        try
        {
            foreach (var target in drawable.Targets)
            {
                if (!TryY(converter, target.Price, pane.Top, pane.Bottom, out var y))
                    continue;

                var text = $"fuel {target.PoolName} {target.Price:0.####}";
                var size = graphics.MeasureString(text, this.font);
                var rect = new RectangleF(
                    pane.Right - size.Width - 10f, y - size.Height - 1f, size.Width + 6f, size.Height);

                if (TryReserve(labelRegistry, rect))
                {
                    graphics.FillRectangle(this.labelBack, rect);
                    graphics.DrawString(text, this.font, this.labelBrush, rect.Left + 3f, rect.Top);
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

        if (double.IsNaN(price) || double.IsInfinity(price))
            return false;

        var raw = converter.GetChartY(price);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        y = (float)Math.Clamp(raw, top, bottom);
        return true;
    }

    private static bool TryReserve(List<RectangleF> registry, RectangleF rect)
    {
        foreach (var placed in registry)
        {
            if (placed.IntersectsWith(rect))
                return false;
        }

        registry.Add(rect);
        return true;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.font.Dispose();
        this.labelBack.Dispose();
        this.labelBrush.Dispose();
    }
}
