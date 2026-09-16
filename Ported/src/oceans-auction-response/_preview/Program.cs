using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuctionResponse.Core;
using AuctionResponse.Ui;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace AuctionResponse.Preview;

/// <summary>
/// Renders the REAL layout code to PNG files, offline.
///
/// This exists because "is it readable?" was previously only answerable by deploying,
/// restarting ATAS and squinting at a screenshot. The layout is host-independent, so it can
/// be drawn through WPF instead and inspected directly. What this proves is the layout; what
/// it cannot prove is how ATAS itself measures text, which is why the panel also reports the
/// DPI it was given.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(outDir);

        var cases = new (string Name, int W, int H, float Scale, bool Diagnostics, string State)[]
        {
            ("primary-1707x960-150pct-setup",    1707, 960, 1.5f, false, "setup"),
            ("primary-1707x960-150pct-diag",     1707, 960, 1.5f, true,  "setup"),
            ("primary-1707x960-150pct-armed",    1707, 960, 1.5f, false, "armed"),
            ("primary-2560x1440-100pct-armed",   2560, 1440, 1.0f, false, "armed"),
            ("half-screen-850x900-150pct-armed", 850, 900, 1.5f, false, "armed"),
            ("narrow-520x700-150pct-setup",      520, 700, 1.5f, false, "setup")
        };

        foreach (var c in cases)
        {
            var view = BuildView(c.State);
            var path = Path.Combine(outDir, c.Name + ".png");
            Render(path, c.W, c.H, c.Scale, view, c.Diagnostics);
            Console.WriteLine("wrote " + path);
        }

        Console.WriteLine("PREVIEW OK");
        return 0;
    }

    private static ViewSnapshot BuildView(string state)
    {
        var harness = new PreviewScenario(state != "setup");
        return harness.Build(state == "armed");
    }

    private static void Render(string path, int width, int height, float scale, ViewSnapshot view, bool diagnostics)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Media(Theme.Background)), null, new Rect(0, 0, width, height));

            var surface = new WpfSurface(dc, scale);
            var metrics = new Metrics(surface, scale);
            var region = new Rectangle(0, 0, width, height);

            var sidebarWidth = SidebarLayout.PreferredWidth(width, metrics);
            var calendar = new SessionCalendar(PreviewScenario.SessionConfig);

            // Draw a faint price grid so the overlay has something to sit against.
            for (var y = 40; y < height; y += 60)
                surface.Line(Theme.Alpha(Theme.Grid, 70), 0, y, Math.Max(width - sidebarWidth - 20, 0), y);

            OverlayLayout.Draw(surface, region, metrics, view,
                price => (int)(height / 2 - (double)(price - 24350m) * 8),
                0.25m, sidebarWidth > 0 ? sidebarWidth + 2 * metrics.Margin : 0);

            if (sidebarWidth > 0)
            {
                var bounds = new Rectangle(
                    region.Right - sidebarWidth - metrics.Margin,
                    region.Top + metrics.Margin,
                    sidebarWidth,
                    Math.Max(region.Height - 2 * metrics.Margin, 1));

                SidebarLayout.Draw(surface, bounds, metrics,
                    new SidebarInput(view, 0.25m, calendar, diagnostics)
                    {
                        RenderScaleNote = "DPI " + (96 * scale).ToString("0", CultureInfo.InvariantCulture) +
                                          " · " + scale.ToString("0.##", CultureInfo.InvariantCulture) + "x auto"
                    });
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static System.Windows.Media.Color Media(Color c)
        => System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
}

/// <summary>WPF implementation of the drawing surface, used only for offline previews.</summary>
internal sealed class WpfSurface : ISurface
{
    private readonly DrawingContext _dc;
    private readonly float _scale;
    private readonly Stack<Rectangle> _clips = new();
    private readonly Dictionary<FontRole, Typeface> _typefaces = new();

    public WpfSurface(DrawingContext dc, float scale)
    {
        _dc = dc;
        _scale = scale;
        _typefaces[FontRole.Small] = new Typeface("Segoe UI");
        _typefaces[FontRole.Body] = new Typeface("Segoe UI");
        _typefaces[FontRole.Strong] = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _typefaces[FontRole.Heading] = _typefaces[FontRole.Strong];
    }

    private double EmSize(FontRole role) => role switch
    {
        FontRole.Small => 11d * 96d / 72d * _scale,
        FontRole.Body => 12d * 96d / 72d * _scale,
        FontRole.Strong => 12d * 96d / 72d * _scale,
        _ => 13d * 96d / 72d * _scale
    };

    private FormattedText Format(string text, FontRole role, Color color) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typefaces[role], EmSize(role), new SolidColorBrush(Program.Media(color)), 96d);

    public Size Measure(string text, FontRole role)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;
        var ft = Format(text, role, Color.White);
        return new Size((int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace), (int)Math.Ceiling(ft.Height));
    }

    public void Text(string text, FontRole role, Color color, int x, int y)
    {
        if (string.IsNullOrEmpty(text)) return;
        _dc.DrawText(Format(text, role, color), new System.Windows.Point(x, y));
    }

    public void FillRect(Color color, Rectangle rect)
        => _dc.DrawRectangle(new SolidColorBrush(Program.Media(color)), null, Rect(rect));

    public void StrokeRect(Color color, Rectangle rect, float width = 1f)
        => _dc.DrawRectangle(null, new Pen(new SolidColorBrush(Program.Media(color)), width), Rect(rect));

    public void Line(Color color, int x1, int y1, int x2, int y2, float width = 1f, bool dashed = false)
    {
        var pen = new Pen(new SolidColorBrush(Program.Media(color)), width);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
        _dc.DrawLine(pen, new System.Windows.Point(x1, y1), new System.Windows.Point(x2, y2));
    }

    public void FillEllipse(Color color, Rectangle rect)
        => _dc.DrawEllipse(new SolidColorBrush(Program.Media(color)), null,
            new System.Windows.Point(rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0),
            rect.Width / 2.0, rect.Height / 2.0);

    public void FillPolygon(Color color, Point[] points)
    {
        if (points.Length < 3) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(points[0].X, points[0].Y), true, true);
            for (var i = 1; i < points.Length; i++)
                ctx.LineTo(new System.Windows.Point(points[i].X, points[i].Y), true, false);
        }
        _dc.DrawGeometry(new SolidColorBrush(Program.Media(color)), null, geometry);
    }

    public void PushClip(Rectangle rect)
    {
        _clips.Push(rect);
        _dc.PushClip(new RectangleGeometry(Rect(rect)));
    }

    public void PopClip()
    {
        if (_clips.Count == 0) return;
        _clips.Pop();
        _dc.Pop();
    }

    private static Rect Rect(Rectangle r) => new(r.X, r.Y, Math.Max(r.Width, 0), Math.Max(r.Height, 0));
}
