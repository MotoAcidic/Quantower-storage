using System.Drawing;
using AuctionResponse.Core;
using AuctionResponse.Ui;
using OFT.Rendering.Context;

namespace AuctionResponse.Atas;

/// <summary>
/// Draws one frame from an immutable <see cref="ViewSnapshot"/>.
///
/// This class is now deliberately thin: it owns the surface, decides how the panel splits
/// between chart and sidebar, and delegates all layout to the host-independent code in
/// AuctionResponse.Ui, which is covered by overlap tests.
///
/// Each region draws inside its own try/catch. On the render thread there is no debugger,
/// and a throwing region must not take down the health panel that would have reported it.
/// </summary>
public sealed class ChartRenderer
{
    private readonly Palette _palette;
    private readonly RenderContextSurface _surface;
    private Metrics _metrics;
    private string _lastError;

    public ChartRenderer(Palette palette)
    {
        _palette = palette;
        _surface = new RenderContextSurface(palette);
    }

    public void Draw(
        RenderContext context, Rectangle region, ViewSnapshot view,
        OverlayLayout.PriceToY priceToY, decimal tickSize, SessionCalendar calendar,
        bool showOverlay, bool showSidebar, bool showDiagnostics, string renderScaleNote = "")
    {
        if (view is null || region.Width <= 0 || region.Height <= 0) return;

        _surface.Begin(context);

        // Metrics depend on measured text, so they are built once the surface has a context.
        _metrics ??= new Metrics(_surface, _palette.Scale);

        var sidebarWidth = showSidebar ? SidebarLayout.PreferredWidth(region.Width, _metrics) : 0;

        if (showOverlay)
            Safe("overlay", () => OverlayLayout.Draw(
                _surface, region, _metrics, view, priceToY, tickSize,
                sidebarWidth > 0 ? sidebarWidth + 2 * _metrics.Margin : 0));

        if (sidebarWidth > 0)
        {
            var bounds = new Rectangle(
                region.Right - sidebarWidth - _metrics.Margin,
                region.Top + _metrics.Margin,
                sidebarWidth,
                Math.Max(region.Height - 2 * _metrics.Margin, 1));

            Safe("sidebar", () => SidebarLayout.Draw(
                _surface, bounds, _metrics,
                new SidebarInput(view, tickSize, calendar, showDiagnostics)
                {
                    RenderScaleNote = renderScaleNote
                }));
        }

        if (_lastError is not null)
            try
            {
                context.DrawString("Auction Response render error: " + _lastError,
                    _palette.Small, Theme.Coral, region.Left + _metrics.Margin, region.Bottom - _metrics.LineSmall - 2);
            }
            catch { }
    }

    private void Safe(string name, Action draw)
    {
        try { draw(); }
        catch (Exception ex) { _lastError = name + ": " + ex.GetType().Name + " " + ex.Message; }
    }
}
