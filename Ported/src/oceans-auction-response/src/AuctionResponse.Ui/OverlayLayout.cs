using System.Drawing;
using AuctionResponse.Core;

namespace AuctionResponse.Ui;

/// <summary>
/// Places overlay labels without letting them collide.
///
/// Zone edges, the confirmation line and the failure line can all land within a few ticks of
/// one another, which on a zoomed-out chart means a few pixels. Stacking their labels makes
/// all three unreadable, so each one is offered a series of candidate slots and takes the
/// first that is free. A label with nowhere free is dropped; its LINE is still drawn, so the
/// chart stays truthful rather than cluttered.
/// </summary>
public sealed class LabelPlacer
{
    private readonly List<Rectangle> _used = new();
    private readonly Rectangle _bounds;

    public LabelPlacer(Rectangle bounds) { _bounds = bounds; }

    public int DroppedCount { get; private set; }

    public bool TryPlace(Rectangle candidate, out Rectangle placed)
    {
        placed = candidate;
        if (!_bounds.Contains(candidate)) return false;
        foreach (var r in _used) if (r.IntersectsWith(candidate)) return false;
        _used.Add(candidate);
        return true;
    }

    /// <summary>Tries each candidate position in order and returns the first that is free.</summary>
    public bool TryPlaceAny(IEnumerable<Rectangle> candidates, out Rectangle placed)
    {
        foreach (var candidate in candidates)
            if (TryPlace(candidate, out placed)) return true;

        DroppedCount++;
        placed = Rectangle.Empty;
        return false;
    }
}

/// <summary>
/// The price-chart overlay: frozen zone, candidate dot, confirmation arrow, failure line.
///
/// Anchored to exact prices via the host's own coordinate conversion, and to the DETECTION
/// timestamp — never to the earlier extreme, which would be a marker claiming to have
/// existed before the engine could have known anything.
/// </summary>
public static class OverlayLayout
{
    public delegate int PriceToY(decimal price);

    public static void Draw(
        ISurface surface, Rectangle region, Metrics m, ViewSnapshot view,
        PriceToY priceToY, decimal tickSize, int reservedRight)
    {
        var area = new Rectangle(region.Left, region.Top,
            Math.Max(region.Width - reservedRight, m.Px(40)), region.Height);
        if (area.Width <= 0 || area.Height <= 0) return;

        surface.PushClip(area);
        try
        {
            var placer = new LabelPlacer(area);

            // The candidate's own boundaries are labelled first: they are the most specific
            // information on the chart and so win any contest for space.
            var setup = view.Current;
            if (setup?.Candidate is { } candidate)
            {
                var confirmation = candidate.Boundaries.ActualConfirmationHalfTicks * tickSize / 2m;
                var failure = candidate.Boundaries.ActualFailureHalfTicks * tickSize / 2m;

                DrawBoundary(surface, area, m, placer, priceToY(failure), Theme.Muted,
                    "Failure " + MeasureText.Price(failure));
                DrawBoundary(surface, area, m, placer, priceToY(confirmation), Theme.Coral,
                    "Confirmation " + MeasureText.Price(confirmation));
            }

            // Each level carries its own live evidence, so a line you drew says something
            // useful from the first minute — long before any reference distribution exists.
            foreach (var level in view.AllSetups)
                DrawLevel(surface, area, m, placer, priceToY, tickSize, level);

            if (setup?.Candidate is { } c) DrawMarker(surface, area, m, priceToY, tickSize, setup, c);
        }
        finally
        {
            surface.PopClip();
        }
    }

    private static void DrawLevel(ISurface surface, Rectangle area, Metrics m, LabelPlacer placer,
        PriceToY priceToY, decimal tickSize, SetupStatus setup)
    {
        var yHigh = priceToY(setup.ZoneHighTicks * tickSize);
        var yLow = priceToY(setup.ZoneLowTicks * tickSize);

        var top = Math.Min(yHigh, yLow);
        var height = Math.Max(Math.Abs(yLow - yHigh), m.Px(3));
        if (top + height < area.Top || top > area.Bottom) return;

        // An armed level is coloured by its state; an idle one is amber, and brightens when
        // price is actually inside its zone. That alone tells you where to look.
        var colour = setup.State == SetupState.Idle
            ? Theme.Amber
            : SidebarLayout.StateColour(setup.State, setup.Orientation);
        var fillAlpha = setup.MidpointInZone ? 46 : 22;

        var rect = new Rectangle(area.Left + m.Px(4), top, Math.Max(area.Width - m.Px(8), 1), height);
        surface.FillRect(Theme.Alpha(colour, fillAlpha), rect);
        surface.StrokeRect(colour, rect);

        PlaceLabel(surface, area, m, placer, LevelLabel(setup), colour, rect.Left + m.Px(5), top, height);
    }

    /// <summary>
    /// What the level is, and what is happening at it right now. Percentile appears only once
    /// a reference distribution exists; until then the raw volume still tells you something,
    /// and nothing is invented to fill the gap.
    /// </summary>
    internal static string LevelLabel(SetupStatus setup)
    {
        var side = setup.Orientation > 0 ? "resistance" : "support";
        var text = setup.LevelId + "  " + side;

        if (!setup.MidpointInZone) return text;

        text += "  ·  in zone";

        if (setup.AttackerVolume.IsAvailable && setup.AttackerVolume.Value!.Value > 0)
        {
            var attacker = setup.Orientation > 0 ? "buy" : "sell";
            text += "  ·  " + attacker + " " + MeasureText.Short(setup.AttackerVolume);

            if (setup.AttackerPercentile.IsAvailable)
                text += " (" + (setup.AttackerPercentile.Value!.Value * 100).ToString("0") + "th pct)";
        }

        return text;
    }

    private static void DrawBoundary(ISurface surface, Rectangle area, Metrics m, LabelPlacer placer,
        int y, Color colour, string label)
    {
        if (y < area.Top || y > area.Bottom) return;

        surface.Line(colour, area.Left + m.Px(4), y, area.Right - m.Px(4), y, 1f, true);
        PlaceLabel(surface, area, m, placer, label, colour, area.Left + m.Px(6), y, 0);
    }

    /// <summary>
    /// Offers the label a slot above the feature, then below it, then further out on each
    /// side. The line is already drawn, so dropping the text loses decoration, not meaning.
    /// </summary>
    private static void PlaceLabel(ISurface surface, Rectangle area, Metrics m, LabelPlacer placer,
        string text, Color colour, int x, int anchorY, int anchorHeight)
    {
        // There is normally plenty of empty chart to the left; a level label earns the room.
        var maxWidth = Math.Max(area.Width * 2 / 3, m.Px(80));
        var fitted = TextUtil.Fit(surface, text, FontRole.Small, maxWidth);
        if (fitted.Length == 0) return;

        var size = surface.Measure(fitted, FontRole.Small);
        var gap = m.Px(2);

        var candidates = new List<Rectangle>
        {
            new(x, anchorY - size.Height - gap, size.Width, size.Height),                    // above
            new(x, anchorY + anchorHeight + gap, size.Width, size.Height),                   // below
            new(x, anchorY - 2 * size.Height - 2 * gap, size.Width, size.Height),            // higher
            new(x, anchorY + anchorHeight + size.Height + 2 * gap, size.Width, size.Height)  // lower
        };

        if (!placer.TryPlaceAny(candidates, out var placed)) return;
        surface.Text(fitted, FontRole.Small, colour, placed.X, placed.Y);
    }

    private static void DrawMarker(ISurface surface, Rectangle area, Metrics m,
        PriceToY priceToY, decimal tickSize, SetupStatus setup, CandidateSnapshot candidate)
    {
        var mid = candidate.StartMidHalfTicks * tickSize / 2m;
        var y = priceToY(mid);
        if (y < area.Top || y > area.Bottom) return;

        var x = area.Right - m.Px(22);
        var colour = SidebarLayout.StateColour(setup.State, candidate.Orientation);

        var r = m.Px(5);
        surface.FillEllipse(colour, new Rectangle(x - r, y - r, r * 2, r * 2));

        if (setup.State != SetupState.Confirmed) return;

        // Shape as well as colour: accessibility must not depend on colour alone.
        var dir = candidate.Orientation > 0 ? 1 : -1;
        var tip = y + dir * m.Px(18);
        var w = m.Px(6);
        surface.FillPolygon(colour, new[]
        {
            new Point(x, tip),
            new Point(x - w, tip - dir * m.Px(9)),
            new Point(x + w, tip - dir * m.Px(9))
        });
    }
}
