using System.Drawing;
using System.Drawing.Drawing2D;
using AuctionResponse.Ui;
using OFT.Rendering.Tools;

namespace AuctionResponse.Atas;

/// <summary>
/// Host drawing resources, sized for the current display scaling.
///
/// Font POINT sizes live here; the layout never sees them. It asks the surface to measure
/// text and spaces rows from the answer. That separation is what fixed the unreadable first
/// build, where row heights were pixel constants and the fonts were points.
/// </summary>
public sealed class Palette : IDisposable
{
    private readonly Dictionary<(int Argb, int Width, DashStyle Dash), RenderPen> _pens = new();

    public Palette(float dpiScale)
    {
        Scale = dpiScale <= 0f ? 1f : dpiScale;

        // Minimum readable text is 12 LOGICAL pixels, so physical size follows the scaling.
        Small = new RenderFont("Segoe UI", 11f * Scale);
        Body = new RenderFont("Segoe UI", 12f * Scale);
        Strong = new RenderFont("Segoe UI", 12f * Scale, FontStyle.Bold);
        Heading = new RenderFont("Segoe UI", 13f * Scale, FontStyle.Bold);
    }

    public float Scale { get; }

    public RenderFont Small { get; }
    public RenderFont Body { get; }
    public RenderFont Strong { get; }
    public RenderFont Heading { get; }

    /// <summary>Pens are cached per colour and width rather than allocated per draw call.</summary>
    public RenderPen Pen(Color color, float width = 1f, DashStyle dash = DashStyle.Solid)
    {
        var key = (color.ToArgb(), (int)MathF.Round(Math.Max(width, 1f) * 10f), dash);
        if (_pens.TryGetValue(key, out var pen)) return pen;

        pen = new RenderPen(color, Math.Max(width, 1f)) { DashStyle = dash };
        _pens[key] = pen;
        return pen;
    }

    public void Dispose()
    {
        // RenderFont and RenderPen are value-like wrappers in this SDK and hold nothing
        // unmanaged. Clearing the cache keeps ownership explicit and gives disposal one
        // place to grow into if that ever changes.
        _pens.Clear();
    }
}
