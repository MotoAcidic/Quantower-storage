using System.Drawing;
using AuctionResponse.Ui;

namespace AuctionResponse.Tests;

public sealed record DrawnText(string Text, FontRole Role, Rectangle Bounds);

/// <summary>
/// An <see cref="ISurface"/> that draws nothing and records everything.
///
/// Font metrics are modelled deliberately UNLIKE the pixel constants the first build
/// assumed: line height is 1.35x the point size scaled by DPI, which is roughly what a real
/// font does and is what caught the original overlap. If the layout only works against a
/// flattering fake, it does not work.
/// </summary>
public sealed class RecordingSurface : ISurface
{
    private readonly float _scale;
    private readonly Stack<Rectangle> _clips = new();

    public RecordingSurface(float scale) { _scale = scale; }

    public List<DrawnText> Texts { get; } = new();
    public List<Rectangle> Fills { get; } = new();
    public int ClipDepth => _clips.Count;
    public bool ClipBalanced => _clips.Count == 0;

    private float PointSize(FontRole role) => role switch
    {
        FontRole.Small => 11f,
        FontRole.Body => 12f,
        FontRole.Strong => 12f,
        _ => 13f
    };

    public Size Measure(string text, FontRole role)
    {
        var pt = PointSize(role) * _scale;
        // Points to pixels at 96 DPI, plus ascent/descent leading.
        var height = (int)MathF.Ceiling(pt * (96f / 72f) * 1.35f);
        var width = (int)MathF.Ceiling((text?.Length ?? 0) * pt * (96f / 72f) * 0.52f);
        return new Size(width, height);
    }

    public void Text(string text, FontRole role, Color color, int x, int y)
    {
        if (string.IsNullOrEmpty(text)) return;
        var size = Measure(text, role);
        Texts.Add(new DrawnText(text, role, new Rectangle(x, y, size.Width, size.Height)));
    }

    public void FillRect(Color color, Rectangle rect) => Fills.Add(rect);
    public void StrokeRect(Color color, Rectangle rect, float width = 1f) { }
    public void Line(Color color, int x1, int y1, int x2, int y2, float width = 1f, bool dashed = false) { }
    public void FillEllipse(Color color, Rectangle rect) { }
    public void FillPolygon(Color color, Point[] points) { }

    public void PushClip(Rectangle rect) => _clips.Push(rect);

    public void PopClip()
    {
        if (_clips.Count == 0) throw new InvalidOperationException("PopClip without a matching PushClip.");
        _clips.Pop();
    }

    /// <summary>Text pairs whose boxes intersect. Any hit is a readability bug.</summary>
    public List<(DrawnText A, DrawnText B)> Overlaps()
    {
        var hits = new List<(DrawnText, DrawnText)>();
        for (var i = 0; i < Texts.Count; i++)
            for (var j = i + 1; j < Texts.Count; j++)
                if (Texts[i].Bounds.IntersectsWith(Texts[j].Bounds))
                    hits.Add((Texts[i], Texts[j]));
        return hits;
    }

    /// <summary>Text drawn outside the panel it was supposed to live in.</summary>
    public List<DrawnText> Escaping(Rectangle bounds)
        => Texts.Where(t => !bounds.Contains(t.Bounds)).ToList();

    public string Describe((DrawnText A, DrawnText B) pair)
        => "\"" + pair.A.Text + "\" " + pair.A.Bounds + " overlaps \"" + pair.B.Text + "\" " + pair.B.Bounds;
}
