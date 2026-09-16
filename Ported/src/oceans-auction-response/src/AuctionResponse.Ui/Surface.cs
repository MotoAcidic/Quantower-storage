using System.Drawing;

namespace AuctionResponse.Ui;

/// <summary>Named text roles. Concrete point sizes belong to the surface, not the layout.</summary>
public enum FontRole { Small, Body, Strong, Heading }

/// <summary>
/// The minimal drawing surface the layout needs.
///
/// This exists so the layout is host-independent and, crucially, TESTABLE: a recording
/// implementation can capture every rectangle the layout emits and assert that none of them
/// overlap and none escape their section. Checking that by eye on a screenshot is how the
/// first version shipped unreadable.
/// </summary>
public interface ISurface
{
    /// <summary>Actual rendered size of the text. Layout spacing is derived from this, never guessed.</summary>
    Size Measure(string text, FontRole role);

    void Text(string text, FontRole role, Color color, int x, int y);
    void FillRect(Color color, Rectangle rect);
    void StrokeRect(Color color, Rectangle rect, float width = 1f);
    void Line(Color color, int x1, int y1, int x2, int y2, float width = 1f, bool dashed = false);
    void FillEllipse(Color color, Rectangle rect);
    void FillPolygon(Color color, Point[] points);

    void PushClip(Rectangle rect);
    void PopClip();
}
