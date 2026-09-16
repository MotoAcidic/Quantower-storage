using System.Drawing;
using AuctionResponse.Core;
using AuctionResponse.Ui;

namespace AuctionResponse.Tests;

/// <summary>
/// Readability as a tested property.
///
/// The first build shipped an unreadable panel because row spacing was a pixel constant
/// while the fonts were sized in points. No unit test could have caught that, because the
/// layout could not be exercised without ATAS. It can now: the layout draws to a recording
/// surface, and these tests assert that no two pieces of text ever overlap and that nothing
/// escapes its panel, across panel sizes and Windows display scalings.
/// </summary>
public static class LayoutTests
{
    // 100%, 125%, 150%, 175% and 200% Windows display scaling.
    private static readonly float[] Scales = { 1.0f, 1.25f, 1.5f, 1.75f, 2.0f };

    private static readonly (int W, int H, string Name)[] Panels =
    {
        (1740, 900, "wide chart"),
        (1200, 800, "laptop"),
        (900, 620, "half screen"),
        (620, 480, "small window"),
        (460, 360, "narrow"),
        (320, 300, "very narrow"),
        (1600, 240, "short and wide")
    };

    public static void Run()
    {
        Harness.Section("Layout - readability at every panel size and display scaling");

        NoOverlapAcrossSizesAndScales();
        NothingEscapesThePanel();
        MetricsComeFromMeasuredText();
        TierDegradesByDroppingSections();
        SetupGuidanceIsShown();
        UnavailableValuesSurviveEveryWidth();
        ColumnsAlign();
        OverlayLabelsNeverCollide();
        HealthIsNeverSqueezedOutByThePlot();
        TextFitting();
        ClipsAreBalanced();
    }

    private static ViewSnapshot BuildView(bool armed, bool setupComplete)
    {
        var s = setupComplete ? new Scenario() : new Scenario(config: new Config
        {
            Instrument = new InstrumentConfig
            {
                InstrumentKey = new InstrumentKey("MNQ", "Cme Mercantile Exchange", ""),
                TickSize = 0.25m
            },
            Session = Scenario.ReadyConfig().Session
            // deliberately no baseline artifact and no recording approval
        }, withBaseline: setupComplete);

        s.DeclareResistance(400);
        if (armed) s.ApproachResistance();
        else s.QuoteStream(2 * Scenario.Second, 100 * Scenario.Millisecond, 399, 401);

        return s.Tick().View;
    }

    private static SidebarInput Input(ViewSnapshot view, bool diagnostics) =>
        new(view, 0.25m, new SessionCalendar(Scenario.ReadyConfig().Session), diagnostics);

    private static void NoOverlapAcrossSizesAndScales()
    {
        var views = new[]
        {
            ("armed", BuildView(armed: true, setupComplete: true)),
            ("idle", BuildView(armed: false, setupComplete: true)),
            ("setup required", BuildView(armed: false, setupComplete: false))
        };

        var totalCases = 0;
        var failures = new List<string>();

        foreach (var (viewName, view) in views)
        foreach (var scale in Scales)
        foreach (var (w, h, panelName) in Panels)
        foreach (var diagnostics in new[] { false, true })
        {
            var surface = new RecordingSurface(scale);
            var metrics = new Metrics(surface, scale);

            var sidebarWidth = SidebarLayout.PreferredWidth(w, metrics);
            if (sidebarWidth <= 0) continue;

            var bounds = new Rectangle(w - sidebarWidth - metrics.Margin, metrics.Margin,
                sidebarWidth, h - 2 * metrics.Margin);
            if (bounds.Height <= 0) continue;

            totalCases++;
            SidebarLayout.Draw(surface, bounds, metrics, Input(view, diagnostics));

            var overlaps = surface.Overlaps();
            if (overlaps.Count > 0)
                failures.Add(viewName + " @" + scale + "x " + panelName + " (" + w + "x" + h + ")" +
                             (diagnostics ? " +diag" : "") + ": " + surface.Describe(overlaps[0]));
        }

        Harness.Check("layout: a meaningful number of cases were exercised", totalCases >= 100,
            "only " + totalCases + " cases ran");
        Harness.Check("layout: no text overlaps, anywhere", failures.Count == 0,
            failures.Count + " failing cases, first: " + (failures.FirstOrDefault() ?? ""));
    }

    private static void NothingEscapesThePanel()
    {
        var view = BuildView(armed: true, setupComplete: true);
        var failures = new List<string>();

        foreach (var scale in Scales)
        foreach (var (w, h, panelName) in Panels)
        {
            var surface = new RecordingSurface(scale);
            var metrics = new Metrics(surface, scale);
            var sidebarWidth = SidebarLayout.PreferredWidth(w, metrics);
            if (sidebarWidth <= 0) continue;

            var bounds = new Rectangle(w - sidebarWidth - metrics.Margin, metrics.Margin,
                sidebarWidth, h - 2 * metrics.Margin);
            if (bounds.Height <= 0) continue;

            SidebarLayout.Draw(surface, bounds, metrics, Input(view, true));

            var escaping = surface.Escaping(bounds);
            if (escaping.Count > 0)
                failures.Add(panelName + " @" + scale + "x: \"" + escaping[0].Text + "\" at " + escaping[0].Bounds +
                             " outside " + bounds);
        }

        Harness.Check("layout: no text escapes the panel bounds", failures.Count == 0,
            failures.Count + " failing cases, first: " + (failures.FirstOrDefault() ?? ""));
    }

    private static void MetricsComeFromMeasuredText()
    {
        // The regression that caused the unreadable build: a row advance smaller than the
        // measured line box. Assert the relationship directly at every scaling.
        foreach (var scale in Scales)
        {
            var surface = new RecordingSurface(scale);
            var m = new Metrics(surface, scale);

            var measured = surface.Measure("Hg", FontRole.Small).Height;
            Harness.Check("metrics @" + scale + "x: small line height matches measurement",
                m.LineSmall == measured, "metrics said " + m.LineSmall + ", measurement says " + measured);
            Harness.Check("metrics @" + scale + "x: a row is strictly taller than its line box",
                m.Row(FontRole.Small) > m.LineSmall);
            Harness.Check("metrics @" + scale + "x: heights scale with display scaling",
                m.LineSmall >= (int)(11 * (96f / 72f) * 1.35f * scale) - 1);
        }
    }

    private static void TierDegradesByDroppingSections()
    {
        var view = BuildView(armed: true, setupComplete: true);
        var surface = new RecordingSurface(1f);
        var m = new Metrics(surface, 1f);

        Harness.Equal("tier: a wide panel gets the full layout",
            LayoutTier.Full, SidebarLayout.TierFor(m.Px(340), m));
        Harness.Equal("tier: a medium panel drops to standard",
            LayoutTier.Standard, SidebarLayout.TierFor(m.Px(240), m));
        Harness.Equal("tier: a narrow panel drops to compact",
            LayoutTier.Compact, SidebarLayout.TierFor(m.Px(170), m));
        Harness.Equal("tier: below the readable minimum the sidebar is dropped entirely",
            LayoutTier.None, SidebarLayout.TierFor(m.Px(120), m));

        // Compact must still be READABLE, not a squeezed version of full.
        var compact = new RecordingSurface(1f);
        var cm = new Metrics(compact, 1f);
        var bounds = new Rectangle(0, 0, cm.Px(SidebarLayout.MinimumContentLogicalWidth) + 2 * cm.Margin, 400);
        SidebarLayout.Draw(compact, bounds, cm, Input(view, false));

        Harness.Check("tier: compact still draws something", compact.Texts.Count > 0);
        Harness.Equal("tier: compact overlaps nothing", 0, compact.Overlaps().Count);

        var full = new RecordingSurface(1f);
        var fm = new Metrics(full, 1f);
        SidebarLayout.Draw(full, new Rectangle(0, 0, fm.Px(360) + 2 * fm.Margin, 900), fm, Input(view, true));

        Harness.Check("tier: full shows strictly more than compact",
            full.Texts.Count > compact.Texts.Count,
            "full " + full.Texts.Count + " vs compact " + compact.Texts.Count);
    }

    private static void SetupGuidanceIsShown()
    {
        var view = BuildView(armed: false, setupComplete: false);
        Harness.Equal("setup: the view really is SetupRequired", DataState.SetupRequired, view.Health.State);
        Harness.Check("setup: missing inputs are carried on the snapshot", view.Health.MissingInputs.Count > 0);

        var surface = new RecordingSurface(1f);
        var m = new Metrics(surface, 1f);
        SidebarLayout.Draw(surface, new Rectangle(0, 0, m.Px(360) + 2 * m.Margin, 900), m, Input(view, false));

        var all = string.Join(" | ", surface.Texts.Select(t => t.Text));

        Harness.Check("setup: the panel says SETUP REQUIRED", all.Contains("SETUP REQUIRED"), all);
        Harness.Check("setup: it names what is actually missing",
            all.Contains("recording", StringComparison.OrdinalIgnoreCase), all);
        Harness.Check("setup: it says HOW to supply it",
            all.Contains("Recording directory", StringComparison.OrdinalIgnoreCase), all);
        Harness.Check("setup: diagnostics are collapsed by default and say so",
            all.Contains("Diagnostics hidden"), all);
        Harness.Equal("setup: nothing overlaps", 0, surface.Overlaps().Count);

        // And with diagnostics on, the counters that explain "no trades" are present.
        var verbose = new RecordingSurface(1f);
        var vm = new Metrics(verbose, 1f);
        SidebarLayout.Draw(verbose, new Rectangle(0, 0, vm.Px(360) + 2 * vm.Margin, 1400), vm, Input(view, true));
        var verboseText = string.Join(" | ", verbose.Texts.Select(t => t.Text));
        Harness.Check("setup: diagnostics expose the observation counters",
            verboseText.Contains("Events seen") && verboseText.Contains("Trades"), verboseText);
    }

    private static void UnavailableValuesSurviveEveryWidth()
    {
        // A missing input must read N/A at any column width. It must never be shortened into
        // a zero, and it must never be dropped in favour of a tidier row.
        var unavailable = Measure.Unavailable("ratio", 5000, 0, "no displayed size at the best level");
        Harness.Equal("unavailable: renders as N/A", "N/A", MeasureText.Short(unavailable));
        Harness.Check("unavailable: keeps its reason", MeasureText.Reason(unavailable).Length > 0);

        Harness.Equal("available zero stays a zero", "0", MeasureText.Short(Measure.Of(0d, "qty", 5000, 0)));
        Harness.Check("a zero and an unavailable value are never the same string",
            MeasureText.Short(Measure.Of(0d, "qty", 5000, 0)) != MeasureText.Short(unavailable));

        var view = BuildView(armed: false, setupComplete: true);
        foreach (var scale in Scales)
        {
            var surface = new RecordingSurface(scale);
            var m = new Metrics(surface, scale);
            var width = m.Px(SidebarLayout.FullContentLogicalWidth) + 2 * m.Margin;
            SidebarLayout.Draw(surface, new Rectangle(0, 0, width, 1000), m, Input(view, true));

            var hasNa = surface.Texts.Any(t => t.Text == MeasureText.Unavailable);
            Harness.Check("unavailable @" + scale + "x: N/A is actually rendered", hasNa,
                "no N/A appeared even though inputs are missing");
        }
    }

    private static void ColumnsAlign()
    {
        var view = BuildView(armed: true, setupComplete: true);
        var surface = new RecordingSurface(1f);
        var m = new Metrics(surface, 1f);
        var width = m.Px(360) + 2 * m.Margin;
        SidebarLayout.Draw(surface, new Rectangle(0, 0, width, 1200), m, Input(view, false));

        // Every evidence label starts at the same x, and every live value ends at the same x.
        var labels = new[] { "Buy volume", "Sell volume", "Delta", "Price progress", "Zone execution" };
        var drawn = surface.Texts.Where(t => labels.Contains(t.Text)).ToList();

        Harness.Check("columns: the evidence rows were drawn", drawn.Count >= 4,
            "found " + drawn.Count + " of " + labels.Length);
        Harness.Equal("columns: labels share a left edge", 1, drawn.Select(t => t.Bounds.Left).Distinct().Count());

        var rowTops = drawn.Select(t => t.Bounds.Top).OrderBy(v => v).ToList();
        var spacings = rowTops.Zip(rowTops.Skip(1), (a, b) => b - a).Distinct().ToList();
        Harness.Equal("columns: rows are evenly spaced", 1, spacings.Count);
        Harness.Check("columns: row spacing exceeds the line height",
            spacings.Count == 1 && spacings[0] >= m.LineSmall,
            "spacing " + (spacings.FirstOrDefault()) + " vs line " + m.LineSmall);
    }

    /// <summary>
    /// Zone edges, the confirmation line and the failure line can land within a few pixels of
    /// each other on a zoomed-out chart. Their labels must never stack.
    /// </summary>
    private static void OverlayLabelsNeverCollide()
    {
        var view = BuildView(armed: true, setupComplete: true);
        var failures = new List<string>();

        // Sweep the price-to-pixel density from very zoomed out to very zoomed in. At the
        // tightest scale every feature maps to nearly the same y.
        foreach (var pixelsPerTick in new[] { 0.2, 0.5, 1.0, 2.0, 6.0, 20.0 })
        foreach (var scale in Scales)
        {
            var surface = new RecordingSurface((float)scale);
            var m = new Metrics(surface, (float)scale);
            var region = new Rectangle(0, 0, 1200, 800);

            OverlayLayout.Draw(surface, region, m, view,
                price => 400 - (int)((double)(price - 100m) * pixelsPerTick),
                0.25m, 300);

            var overlaps = surface.Overlaps();
            if (overlaps.Count > 0)
                failures.Add(pixelsPerTick + "px/tick @" + scale + "x: " + surface.Describe(overlaps[0]));
        }

        Harness.Check("overlay: labels never collide at any zoom or scaling", failures.Count == 0,
            failures.Count + " failing cases, first: " + (failures.FirstOrDefault() ?? ""));

        // A label that cannot be placed is dropped, but its LINE still gets drawn, so the
        // chart loses decoration rather than meaning.
        var tight = new RecordingSurface(1f);
        var tm = new Metrics(tight, 1f);
        OverlayLayout.Draw(tight, new Rectangle(0, 0, 1200, 800), tm, view,
            _ => 400, 0.25m, 300);
        Harness.Equal("overlay: even with every feature on one line, nothing overlaps", 0, tight.Overlaps().Count);
        Harness.Check("overlay: the features are still drawn", tight.Fills.Count > 0);
    }

    /// <summary>
    /// The plot is decoration; data health explains whether the thing is working at all.
    /// The plot must never consume the space health needs.
    /// </summary>
    private static void HealthIsNeverSqueezedOutByThePlot()
    {
        var view = BuildView(armed: true, setupComplete: true);
        var failures = new List<string>();

        foreach (var scale in Scales)
        foreach (var height in new[] { 420, 520, 640, 760, 900, 1100 })
        foreach (var diagnostics in new[] { false, true })
        {
            var surface = new RecordingSurface(scale);
            var m = new Metrics(surface, scale);
            var width = m.Px(SidebarLayout.FullContentLogicalWidth) + 2 * m.Margin;

            SidebarLayout.Draw(surface, new Rectangle(0, 0, width, height), m, Input(view, diagnostics));

            var drawn = surface.Texts.Select(t => t.Text).ToList();
            if (!drawn.Contains("DATA HEALTH"))
                failures.Add("h=" + height + " @" + scale + "x" + (diagnostics ? " +diag" : "") + ": health section missing");
        }

        Harness.Check("layout: data health survives at every panel height", failures.Count == 0,
            failures.Count + " failing cases, first: " + (failures.FirstOrDefault() ?? ""));
    }

    private static void TextFitting()
    {
        var surface = new RecordingSurface(1f);

        var wide = surface.Measure("Cme Mercantile Exchange", FontRole.Small).Width;
        var fitted = TextUtil.Fit(surface, "Cme Mercantile Exchange", FontRole.Small, wide / 2);
        Harness.Check("fitting: long text is truncated with an ellipsis", fitted.EndsWith(TextUtil.Ellipsis), fitted);
        Harness.Check("fitting: the result actually fits",
            surface.Measure(fitted, FontRole.Small).Width <= wide / 2, fitted);

        Harness.Equal("fitting: text that fits is untouched", "MNQ",
            TextUtil.Fit(surface, "MNQ", FontRole.Small, wide));
        Harness.Equal("fitting: an impossible width yields nothing to draw", "",
            TextUtil.Fit(surface, "MNQ", FontRole.Small, 1));

        // An empty expiry must not render as a dangling separator.
        Harness.Check("fitting: an empty expiry is omitted from the instrument label",
            !SidebarLayout.InstrumentLabel(new InstrumentKey("MNQ", "CME", "")).TrimEnd().EndsWith("·"),
            SidebarLayout.InstrumentLabel(new InstrumentKey("MNQ", "CME", "")));
        Harness.Check("fitting: a present expiry is included",
            SidebarLayout.InstrumentLabel(new InstrumentKey("MNQ", "CME", "202612")).Contains("202612"));
        Harness.Equal("fitting: an unset instrument says so", "instrument not set",
            SidebarLayout.InstrumentLabel(InstrumentKey.Unknown));
    }

    private static void ClipsAreBalanced()
    {
        var view = BuildView(armed: true, setupComplete: true);
        var surface = new RecordingSurface(1.5f);
        var m = new Metrics(surface, 1.5f);
        SidebarLayout.Draw(surface, new Rectangle(0, 0, m.Px(360), 900), m, Input(view, true));

        // An unbalanced clip stack leaves the whole chart clipped to a stale rectangle.
        Harness.Check("clips: every push is matched by a pop", surface.ClipBalanced,
            "clip depth left at " + surface.ClipDepth);
    }
}
