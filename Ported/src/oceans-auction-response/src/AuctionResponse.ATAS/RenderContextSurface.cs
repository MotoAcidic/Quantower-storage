using System.Drawing;
using System.Drawing.Drawing2D;
using AuctionResponse.Ui;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace AuctionResponse.Atas;

/// <summary>
/// Adapts the host's <see cref="RenderContext"/> to <see cref="ISurface"/>.
///
/// This is the only place ATAS rendering types appear. Everything above it is the
/// host-independent layout, which is what lets the layout be tested for overlap without a
/// platform.
///
/// Fonts and pens are created once by <see cref="Palette"/> and reused: OnRender runs on the
/// render thread at the host's frame rate, and allocating drawing resources per frame is how
/// a chart starts stuttering.
/// </summary>
public sealed class RenderContextSurface : ISurface
{
    private readonly Palette _palette;
    private readonly Stack<Rectangle> _clips = new();
    private RenderContext _context;

    public RenderContextSurface(Palette palette) { _palette = palette; }

    /// <summary>Rebinds to the frame's context. The surface itself is reused across frames.</summary>
    public void Begin(RenderContext context)
    {
        _context = context;
        _clips.Clear();
    }

    private RenderFont Font(FontRole role) => role switch
    {
        FontRole.Small => _palette.Small,
        FontRole.Body => _palette.Body,
        FontRole.Strong => _palette.Strong,
        _ => _palette.Heading
    };

    public Size Measure(string text, FontRole role)
    {
        if (_context is null || string.IsNullOrEmpty(text)) return Size.Empty;
        return _context.MeasureString(text, Font(role));
    }

    public void Text(string text, FontRole role, Color color, int x, int y)
    {
        if (_context is null || string.IsNullOrEmpty(text)) return;
        _context.DrawString(text, Font(role), color, x, y);
    }

    public void FillRect(Color color, Rectangle rect)
    {
        if (_context is null || rect.Width <= 0 || rect.Height <= 0) return;
        _context.FillRectangle(color, rect);
    }

    public void StrokeRect(Color color, Rectangle rect, float width = 1f)
    {
        if (_context is null || rect.Width <= 0 || rect.Height <= 0) return;
        _context.DrawRectangle(_palette.Pen(color, width), rect);
    }

    public void Line(Color color, int x1, int y1, int x2, int y2, float width = 1f, bool dashed = false)
    {
        if (_context is null) return;
        _context.DrawLine(_palette.Pen(color, width, dashed ? DashStyle.Dash : DashStyle.Solid), x1, y1, x2, y2);
    }

    public void FillEllipse(Color color, Rectangle rect)
    {
        if (_context is null || rect.Width <= 0 || rect.Height <= 0) return;
        _context.FillEllipse(color, rect);
    }

    public void FillPolygon(Color color, Point[] points)
    {
        if (_context is null || points is null || points.Length < 3) return;
        _context.FillPolygon(color, points);
    }

    public void PushClip(Rectangle rect)
    {
        if (_context is null) return;
        _clips.Push(rect);
        _context.SetClip(rect);
    }

    public void PopClip()
    {
        if (_context is null || _clips.Count == 0) return;
        _clips.Pop();
        _context.ResetClip();
        // Restore the enclosing clip, if any, so nested sections stay bounded.
        if (_clips.Count > 0) _context.SetClip(_clips.Peek());
    }
}
