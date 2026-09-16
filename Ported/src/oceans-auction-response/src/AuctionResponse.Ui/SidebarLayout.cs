using System.Drawing;
using AuctionResponse.Core;

namespace AuctionResponse.Ui;

/// <summary>How much the panel can show at its current size.</summary>
public enum LayoutTier
{
    /// <summary>Too narrow for the sidebar at all; the caller should suppress it.</summary>
    None,
    /// <summary>Status and one health line only. Still fully readable.</summary>
    Compact,
    /// <summary>Status, live evidence, health. No frozen column, no plot.</summary>
    Standard,
    /// <summary>Everything: frozen and live columns, pressure plot, history.</summary>
    Full
}

public sealed record SidebarInput(
    ViewSnapshot View,
    decimal TickSize,
    SessionCalendar Calendar,
    bool ShowDiagnostics)
{
    /// <summary>
    /// What the host reported for DPI and what scale was actually applied. Surfaced in the
    /// diagnostics so display-scaling behaviour is observable rather than guessed at.
    /// </summary>
    public string RenderScaleNote { get; init; } = "";
}

/// <summary>
/// The companion sidebar (Section 3), laid out from measured text.
///
/// Every section gets a bounded rectangle and a <see cref="Cursor"/> that refuses to write
/// past it, so sections cannot overlap each other and rows cannot overlap each other. The
/// layout degrades by dropping whole sections, never by squeezing labels into unreadable
/// text, which is the one thing Section 3 explicitly forbids.
/// </summary>
public static class SidebarLayout
{
    /// <summary>Minimum readable content width, in logical pixels, before the sidebar is dropped.</summary>
    public const float MinimumContentLogicalWidth = 150f;
    public const float StandardContentLogicalWidth = 210f;
    public const float FullContentLogicalWidth = 300f;

    public static LayoutTier TierFor(int contentWidth, Metrics m)
    {
        var logical = contentWidth / m.Scale;
        if (logical < MinimumContentLogicalWidth) return LayoutTier.None;
        if (logical < StandardContentLogicalWidth) return LayoutTier.Compact;
        if (logical < FullContentLogicalWidth) return LayoutTier.Standard;
        return LayoutTier.Full;
    }

    /// <summary>
    /// Preferred sidebar width for a given panel: about 30 percent, clamped so it is never
    /// unreadably narrow and never swallows the chart.
    /// </summary>
    public static int PreferredWidth(int panelWidth, Metrics m)
    {
        var ideal = (int)(panelWidth * 0.30);
        var minimum = m.Px(MinimumContentLogicalWidth) + 2 * m.Margin;
        var comfortable = m.Px(FullContentLogicalWidth) + 2 * m.Margin;
        var maximum = Math.Max(minimum, panelWidth / 2);

        var width = Math.Clamp(ideal, minimum, Math.Min(comfortable, maximum));
        return width + 2 * m.Margin > panelWidth ? 0 : width;
    }

    public static void Draw(ISurface surface, Rectangle bounds, Metrics m, SidebarInput input)
    {
        var contentWidth = bounds.Width - 2 * m.Margin;
        var tier = TierFor(contentWidth, m);
        if (tier == LayoutTier.None) return;

        surface.PushClip(bounds);
        try
        {
            surface.FillRect(Theme.Alpha(Theme.Panel, 238), bounds);
            surface.StrokeRect(Theme.Grid, bounds);

            var view = input.View;
            var left = bounds.Left + m.Margin;
            var top = bounds.Top + m.Margin;
            var bottom = bounds.Bottom - m.Margin;

            // Data health is ANCHORED to the bottom of the panel and its space is taken out
            // before anything optional is drawn. It explains whether the indicator is working
            // at all, so it must survive every panel size; the plot and history are what give
            // way. Health squeezed off the bottom is the failure mode this prevents.
            var healthCap = input.ShowDiagnostics ? (bottom - top) * 2 / 3 : (bottom - top) / 2;
            var healthHeight = Math.Min(HealthBlockHeight(surface, m, input, tier, contentWidth), healthCap);
            var healthTop = bottom - healthHeight;

            var cursor = new Cursor(left, top, contentWidth, Math.Max(healthTop - m.SectionGap, top));

            DrawHeader(surface, ref cursor, m, input);
            DrawStatus(surface, ref cursor, m, input, tier);

            // A blocked setup is the whole story: say what is missing and how to supply it,
            // rather than burying it under rows of diagnostics the reader has to decode.
            if (view.Health.State == DataState.SetupRequired)
                DrawSetupBlock(surface, ref cursor, m, input, tier);
            else if (tier >= LayoutTier.Standard)
                DrawEvidence(surface, ref cursor, m, input, tier);

            if (tier == LayoutTier.Full && view.Health.State != DataState.SetupRequired)
                DrawPlot(surface, ref cursor, m, input);

            if (tier == LayoutTier.Full) DrawHistory(surface, ref cursor, m, input);

            var healthCursor = new Cursor(left, healthTop, contentWidth, bottom);
            DrawHealth(surface, ref healthCursor, m, input, tier);
            DrawModelFooter(surface, ref healthCursor, m, input);
        }
        finally
        {
            surface.PopClip();
        }
    }

    // ------------------------------------------------------------------ sections

    private static void DrawHeader(ISurface surface, ref Cursor c, Metrics m, SidebarInput input)
    {
        var view = input.View;

        if (c.TryTake(m.Row(FontRole.Heading), out var y))
            TextUtil.TextLeft(surface, "AUCTION RESPONSE MONITOR", FontRole.Heading, Theme.Text, c.X, y, c.Width);

        if (c.TryTake(m.Row(FontRole.Small), out y))
            TextUtil.TextLeft(surface, InstrumentLabel(view.Instrument), FontRole.Small, Theme.Muted, c.X, y, c.Width);

        if (c.TryTake(m.Row(FontRole.Small), out y))
        {
            var badge = view.IsSimulatedData ? "SIMULATED DATA" : view.ModeLabel;
            TextUtil.TextLeft(surface, "READ ONLY", FontRole.Small, Theme.Muted, c.X, y, c.Width);
            TextUtil.TextRight(surface, badge, FontRole.Small,
                view.IsSimulatedData ? Theme.Amber : Theme.Muted, c.Right, y, c.Width / 2);
        }

        if (c.TryTake(m.Row(FontRole.Small), out y))
            TextUtil.TextLeft(surface, input.Calendar.LabelWithZone(view.PublishedAtUtc),
                FontRole.Small, Theme.Muted, c.X, y, c.Width);

        c.Skip(m.SectionGap);
    }

    /// <summary>Exactly ONE current status line, in a bounded card.</summary>
    private static void DrawStatus(ISurface surface, ref Cursor c, Metrics m, SidebarInput input, LayoutTier tier)
    {
        var setup = input.View.Current;
        var state = setup?.State ?? SetupState.Idle;
        var colour = StateColour(state, setup?.Orientation ?? 1);

        var headline = setup?.Headline ?? "No active setup";
        var detail = StatusDetail(setup, input);

        var headlineLines = TextUtil.Wrap(surface, headline, FontRole.Strong, c.Width - 2 * m.Pad, 2);
        var detailLines = tier == LayoutTier.Compact
            ? new List<string>()
            : TextUtil.Wrap(surface, detail, FontRole.Small, c.Width - 2 * m.Pad, 2);

        var cardHeight = 2 * m.Pad
            + headlineLines.Count * m.Row(FontRole.Strong)
            + detailLines.Count * m.Row(FontRole.Small);

        if (!c.TryTake(cardHeight, out var top)) return;

        var card = new Rectangle(c.X, top, c.Width, cardHeight);
        surface.FillRect(Theme.Alpha(colour, 34), card);
        surface.StrokeRect(colour, card);

        var y = top + m.Pad;
        foreach (var line in headlineLines)
        {
            surface.Text(line, FontRole.Strong, colour, c.X + m.Pad, y);
            y += m.Row(FontRole.Strong);
        }
        foreach (var line in detailLines)
        {
            surface.Text(line, FontRole.Small, Theme.Muted, c.X + m.Pad, y);
            y += m.Row(FontRole.Small);
        }

        c.Skip(m.SectionGap);
    }

    /// <summary>
    /// What is missing, and how to supply it. This replaces the wall of diagnostics that made
    /// the first build unreadable; the diagnostics are still available, just collapsed.
    /// </summary>
    private static void DrawSetupBlock(ISurface surface, ref Cursor c, Metrics m, SidebarInput input, LayoutTier tier)
    {
        var missing = input.View.Health.MissingInputs;
        if (missing.Count == 0) return;

        if (c.TryTake(m.Row(FontRole.Strong), out var y))
            TextUtil.TextLeft(surface, "SETUP REQUIRED", FontRole.Strong, Theme.Amber, c.X, y, c.Width);

        var detailLimit = tier == LayoutTier.Compact ? 1 : missing.Count;

        for (var i = 0; i < missing.Count && i < detailLimit; i++)
        {
            var item = missing[i];

            if (!c.TryTake(m.Row(FontRole.Small), out y)) return;
            TextUtil.TextLeft(surface, "• " + item.What, FontRole.Small, Theme.Text, c.X, y, c.Width);

            if (tier == LayoutTier.Compact) continue;

            var indent = m.Px(10);
            foreach (var line in TextUtil.Wrap(surface, item.HowToSupply, FontRole.Small, c.Width - indent, 6))
            {
                if (!c.TryTake(m.Row(FontRole.Small), out y)) return;
                surface.Text(line, FontRole.Small, Theme.Muted, c.X + indent, y);
            }
        }

        if (missing.Count > detailLimit && c.TryTake(m.Row(FontRole.Small), out y))
            TextUtil.TextLeft(surface, "and " + (missing.Count - detailLimit) + " more",
                FontRole.Small, Theme.Muted, c.X, y, c.Width);

        c.Skip(m.SectionGap);
    }

    private static readonly (string Label, Func<FeatureSnapshot, Measure> Pick)[] EvidenceRows =
    {
        ("Buy volume", f => f.BuyVolume),
        ("Sell volume", f => f.SellVolume),
        ("Delta", f => f.Delta),
        ("Price progress", f => f.Response),
        ("Zone execution", f => f.ZoneAttackerVolume),
        ("Queue imbalance", f => f.QueueImbalance),
        ("Depth-norm OFI", f => f.DepthNormalizedOfi)
    };

    private static void DrawEvidence(ISurface surface, ref Cursor c, Metrics m, SidebarInput input, LayoutTier tier)
    {
        var frozen = input.View.Current?.Candidate?.FrozenEvidence;
        var live = input.View.Live;
        var showFrozen = tier == LayoutTier.Full;

        if (c.TryTake(m.Row(FontRole.Strong), out var y))
            TextUtil.TextLeft(surface, "EVIDENCE", FontRole.Strong, Theme.Muted, c.X, y, c.Width);

        // Columns are computed once from the measured width and every row uses them, so the
        // values line up instead of landing wherever the label happened to end.
        var gap = m.Px(8);
        var valueWidth = showFrozen ? (c.Width - gap * 2) * 28 / 100 : (c.Width - gap) * 40 / 100;
        var labelWidth = c.Width - gap - valueWidth - (showFrozen ? gap + valueWidth : 0);
        var liveRight = c.Right;
        var frozenRight = showFrozen ? liveRight - valueWidth - gap : liveRight;

        if (showFrozen && c.TryTake(m.Row(FontRole.Small), out y))
        {
            TextUtil.TextRight(surface, "at detection", FontRole.Small, Theme.Muted, frozenRight, y, valueWidth);
            TextUtil.TextRight(surface, "live", FontRole.Small, Theme.Muted, liveRight, y, valueWidth);
        }

        foreach (var (label, pick) in EvidenceRows)
        {
            if (!c.TryTake(m.Row(FontRole.Small), out y)) { c.Skip(m.SectionGap); return; }

            TextUtil.TextLeft(surface, label, FontRole.Small, Theme.Muted, c.X, y, labelWidth);

            if (showFrozen)
            {
                var f = frozen is null ? "—" : MeasureText.Short(pick(frozen));
                var frozenColour = frozen is not null && pick(frozen).IsAvailable ? Theme.Text : Theme.Muted;
                TextUtil.TextRight(surface, f, FontRole.Small, frozenColour, frozenRight, y, valueWidth);
            }

            var l = pick(live);
            TextUtil.TextRight(surface, MeasureText.Short(l), FontRole.Small,
                l.IsAvailable ? Theme.Text : Theme.Muted, liveRight, y, valueWidth);
        }

        c.Skip(m.SectionGap);
    }

    /// <summary>
    /// The pressure-response panel. Given the full content width rather than a fixed small
    /// box, because reading the quadrant is the entire point of the plot.
    /// </summary>
    /// <summary>
    /// Exact height the anchored health block needs: heading, its reason, either the
    /// diagnostic rows or the collapsed hint, and the model footer.
    /// </summary>
    private static int HealthBlockHeight(ISurface surface, Metrics m, SidebarInput input, LayoutTier tier, int contentWidth)
    {
        var height = m.Row(FontRole.Strong);                       // heading

        // The reason is WRAPPED and counted, not assumed. Guessing high leaves dead space at
        // the bottom of the panel and starves the sections above it of room they could use.
        if (input.View.Health.State != DataState.SetupRequired)
        {
            var reasonLines = TextUtil.Wrap(surface, input.View.Health.StateReason, FontRole.Small,
                contentWidth, tier == LayoutTier.Compact ? 2 : 3).Count;
            height += reasonLines * m.Row(FontRole.Small);
        }

        height += input.ShowDiagnostics
            ? DiagnosticRowCount(input) * m.Row(FontRole.Small)
            : m.Row(FontRole.Small);                               // collapsed hint
        height += m.SectionGap + m.Row(FontRole.Small);            // model footer
        return height;
    }

    private static int DiagnosticRowCount(SidebarInput input)
    {
        var rows = 12;
        if (!string.IsNullOrEmpty(input.RenderScaleNote)) rows++;
        if (input.View.Health.IngressOverflowed) rows++;
        if (input.View.Health.RecorderFaulted) rows++;
        return rows;
    }

    private static void DrawPlot(ISurface surface, ref Cursor c, Metrics m, SidebarInput input)
    {
        var titleRow = m.Row(FontRole.Strong);
        var labelRow = m.Row(FontRole.Small);

        // Health is already carved out of the panel, so whatever is left here is genuinely spare.
        var available = c.Remaining - titleRow - labelRow - m.SectionGap;
        var side = Math.Min(c.Width, available);

        // The plot shrinks before it disappears, because a small quadrant scatter still
        // answers the question it exists for: which way is pressure pushing, and is price
        // going with it. Below the floor it is genuinely unreadable, so it is dropped.
        if (side < m.Px(70)) return;

        if (!c.TryTake(titleRow, out var y)) return;
        TextUtil.TextLeft(surface, "PRESSURE vs RESPONSE", FontRole.Strong, Theme.Muted, c.X, y, c.Width);

        if (!c.TryTake(side, out var top)) return;
        var plot = new Rectangle(c.X, top, side, side);

        surface.FillRect(Theme.Alpha(Theme.Background, 210), plot);
        surface.StrokeRect(Theme.Grid, plot);

        var cx = plot.Left + plot.Width / 2;
        var cy = plot.Top + plot.Height / 2;
        surface.Line(Theme.Grid, plot.Left, cy, plot.Right, cy);
        surface.Line(Theme.Grid, cx, plot.Top, cx, plot.Bottom);

        // Visual de-emphasis only; this band defines no alert.
        var band = (int)(plot.Width * (PlotMath.NeutralBand / (2 * PlotMath.Clip)));
        if (band > 1) surface.StrokeRect(Theme.Alpha(Theme.Grid, 160), new Rectangle(cx - band, cy - band, band * 2, band * 2));

        var trail = input.View.Trail;
        for (var i = 0; i < trail.Count; i++)
        {
            var point = trail[i];
            var (px, py) = PlotMath.ToScreen(point, plot.Left, plot.Top, plot.Width, plot.Height);
            var newest = i == trail.Count - 1;
            var radius = newest ? m.Px(5) : m.Px(2.5f);
            var alpha = newest ? 255 : 40 + (int)(150.0 * i / Math.Max(trail.Count - 1, 1));
            var colour = Theme.Alpha(point.X >= 0 ? Theme.Teal : Theme.Coral, alpha);

            surface.FillEllipse(colour, new Rectangle((int)px - radius, (int)py - radius, radius * 2, radius * 2));

            if (newest && (point.OverflowX || point.OverflowY))
                surface.Text("▸", FontRole.Small, Theme.Amber, (int)px + radius + m.Px(2), (int)py - m.LineSmall / 2);
        }

        // Axis labels need room to sit inside a quadrant without touching the dots. On a
        // small plot they are dropped rather than overlaid on the data.
        if (side >= m.Px(130))
        {
            var inset = m.Px(4);
            var quadrant = plot.Width / 2 - 2 * inset;
            TextUtil.TextLeft(surface, "sell", FontRole.Small, Theme.Muted, plot.Left + inset, cy + inset, quadrant);
            TextUtil.TextRight(surface, "buy", FontRole.Small, Theme.Muted, plot.Right - inset, cy + inset, quadrant);
            TextUtil.TextLeft(surface, "up", FontRole.Small, Theme.Muted, cx + inset, plot.Top + inset, quadrant);
            TextUtil.TextLeft(surface, "down", FontRole.Small, Theme.Muted, cx + inset,
                plot.Bottom - m.LineSmall - inset, quadrant);
        }

        if (c.TryTake(labelRow, out y))
        {
            // Captioned, so a bare "Unavailable" cannot be misread as the setup state.
            var captionGap = m.Px(8);
            var valueWidth = (c.Width - captionGap) * 55 / 100;
            TextUtil.TextLeft(surface, "Flow", FontRole.Small, Theme.Muted, c.X, y, c.Width - captionGap - valueWidth);
            TextUtil.TextRight(surface, MeasureText.Label(input.View.Label), FontRole.Small,
                input.View.Label == DescriptiveLabel.Unavailable ? Theme.Muted : Theme.Text,
                c.Right, y, valueWidth);
        }

        c.Skip(m.SectionGap);
    }

    private static void DrawHealth(ISurface surface, ref Cursor c, Metrics m, SidebarInput input, LayoutTier tier)
    {
        var h = input.View.Health;
        var colour = h.State switch
        {
            DataState.Ready => Theme.Teal,
            DataState.Warmup or DataState.SetupRequired => Theme.Amber,
            _ => Theme.Coral
        };

        if (c.TryTake(m.Row(FontRole.Strong), out var y))
        {
            TextUtil.TextLeft(surface, "DATA HEALTH", FontRole.Strong, Theme.Muted, c.X, y, c.Width * 55 / 100);
            TextUtil.TextRight(surface, StateLabel(h.State), FontRole.Strong, colour, c.Right, y, c.Width * 45 / 100);
        }

        // One honest line always; the rest only when the reader asks for it.
        if (h.State != DataState.SetupRequired)
            foreach (var line in TextUtil.Wrap(surface, h.StateReason, FontRole.Small, c.Width, tier == LayoutTier.Compact ? 2 : 3))
            {
                if (!c.TryTake(m.Row(FontRole.Small), out y)) return;
                surface.Text(line, FontRole.Small, Theme.Muted, c.X, y);
            }

        if (!input.ShowDiagnostics)
        {
            if (c.TryTake(m.Row(FontRole.Small), out y))
                TextUtil.TextLeft(surface,
                    "Diagnostics hidden — see settings",
                    FontRole.Small, Theme.Alpha(Theme.Muted, 190), c.X, y, c.Width);
            c.Skip(m.SectionGap);
            return;
        }

        var rows = new List<(string Label, string Value, bool Ok)>
        {
            ("Events seen", h.EventsAccepted.ToString(), h.EventsAccepted > 0),
            ("Trades", h.TradeEvents > 0 ? h.TradeEvents.ToString() : "none yet", h.Capabilities.Trades),
            ("Quotes", h.QuoteEvents > 0 ? h.QuoteEvents.ToString() : "none yet", h.Capabilities.Quotes),
            ("Depth (MBP)", h.DepthEvents > 0 ? h.DepthEvents.ToString() : "none yet", h.Capabilities.MarketByPrice),
            ("Rejected", h.RejectedEvents.ToString(), h.RejectedEvents == 0),
            ("MBO", h.Capabilities.MarketByOrderVerified ? "verified" : "N/A", h.Capabilities.MarketByOrderVerified),
            ("Bid age", MeasureText.Short(h.BidQuoteAge), h.BidQuoteAge.IsAvailable),
            ("Ask age", MeasureText.Short(h.AskQuoteAge), h.AskQuoteAge.IsAvailable),
            ("Spread", MeasureText.Short(h.Spread), h.Spread.IsAvailable),
            ("Side quality", MeasureText.Short(h.SideQuality), h.SideQuality.IsAvailable),
            ("Backlog", h.Backlog + " / " + h.IngressCapacity, !h.IngressOverflowed),
            ("Epoch", h.ConnectionEpoch + " (" + h.EpochAgeMs / 1000 + "s)", true)
        };

        if (!string.IsNullOrEmpty(input.RenderScaleNote))
            rows.Add(("Render scale", input.RenderScaleNote, true));

        var gap = m.Px(8);
        var valueWidth = (c.Width - gap) * 45 / 100;
        var labelWidth = c.Width - gap - valueWidth;

        foreach (var (label, value, ok) in rows)
        {
            if (!c.TryTake(m.Row(FontRole.Small), out y)) { c.Skip(m.SectionGap); return; }
            TextUtil.TextLeft(surface, label, FontRole.Small, Theme.Muted, c.X, y, labelWidth);
            TextUtil.TextRight(surface, value, FontRole.Small, ok ? Theme.Text : Theme.Muted, c.Right, y, valueWidth);
        }

        if (h.IngressOverflowed && c.TryTake(m.Row(FontRole.Small), out y))
            TextUtil.TextLeft(surface, "INGRESS OVERFLOW — alerts suppressed", FontRole.Small, Theme.Coral, c.X, y, c.Width);
        if (h.RecorderFaulted && c.TryTake(m.Row(FontRole.Small), out y))
            TextUtil.TextLeft(surface, "RECORDER FAULTED — research invalid", FontRole.Small, Theme.Coral, c.X, y, c.Width);

        c.Skip(m.SectionGap);
    }

    private static void DrawHistory(ISurface surface, ref Cursor c, Metrics m, SidebarInput input)
    {
        // Only worth a heading if at least one entry can follow it.
        if (c.Remaining < m.Row(FontRole.Strong) + m.Row(FontRole.Small)) return;

        if (c.TryTake(m.Row(FontRole.Strong), out var y))
            TextUtil.TextLeft(surface, "SETUP HISTORY", FontRole.Strong, Theme.Muted, c.X, y, c.Width);

        if (input.View.History.Count == 0)
        {
            if (c.TryTake(m.Row(FontRole.Small), out y))
                TextUtil.TextLeft(surface, "no transitions yet", FontRole.Small, Theme.Muted, c.X, y, c.Width);
            c.Skip(m.SectionGap);
            return;
        }

        var gap = m.Px(8);
        var stateWidth = (c.Width - gap) * 40 / 100;
        var leftWidth = c.Width - gap - stateWidth;

        foreach (var entry in input.View.History)
        {
            if (!c.TryTake(m.Row(FontRole.Small), out y)) break;
            var colour = entry.State switch
            {
                SetupState.Confirmed => entry.Orientation > 0 ? Theme.Coral : Theme.Teal,
                SetupState.Candidate => Theme.Amber,
                _ => Theme.Muted
            };
            TextUtil.TextLeft(surface, input.Calendar.Label(entry.TransitionUtc) + "  " + entry.LevelId,
                FontRole.Small, Theme.Muted, c.X, y, leftWidth);
            TextUtil.TextRight(surface, entry.State.ToString(), FontRole.Small, colour, c.Right, y, stateWidth);
        }

        c.Skip(m.SectionGap);
    }

    private static void DrawModelFooter(ISurface surface, ref Cursor c, Metrics m, SidebarInput input)
    {
        if (!c.TryTake(m.Row(FontRole.Small), out var y)) return;

        if (input.View.Model is not { } model)
        {
            // No validated model exists, so the panel says so and shows no number at all.
            TextUtil.TextLeft(surface, "Not calibrated — no probability shown",
                FontRole.Small, Theme.Alpha(Theme.Muted, 200), c.X, y, c.Width);
            return;
        }

        TextUtil.TextLeft(surface, model.DisplayTitle + " (" + model.ModelVersion + ")",
            FontRole.Small, Theme.Text, c.X, y, c.Width);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>"SetupRequired" is a machine token; a person reads "Setup required".</summary>
    internal static string StateLabel(DataState state) => state switch
    {
        DataState.SetupRequired => "Setup required",
        _ => state.ToString()
    };

    public static Color StateColour(SetupState state, int orientation) => state switch
    {
        SetupState.Candidate => Theme.Amber,
        SetupState.Confirmed => orientation > 0 ? Theme.Coral : Theme.Teal,
        _ => Theme.Muted
    };

    public static string InstrumentLabel(InstrumentKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Symbol)) return "instrument not set";
        var parts = new List<string> { key.Symbol };
        if (!string.IsNullOrWhiteSpace(key.Exchange)) parts.Add(key.Exchange);
        // An empty expiry is omitted rather than rendered as a dangling separator.
        if (!string.IsNullOrWhiteSpace(key.Expiry)) parts.Add(key.Expiry);
        return string.Join(" · ", parts);
    }

    private static string StatusDetail(SetupStatus? setup, SidebarInput input)
    {
        if (setup is null) return "Declare a level in settings to begin.";

        if (setup.State == SetupState.Candidate && setup.AgeMs is { } age)
            return "level " + setup.LevelId + "  ·  age " + (age / 1000d).ToString("0.0") + "s";

        if (setup.CooldownRemainingMs is > 0)
            return "level " + setup.LevelId + "  ·  cooldown " + (setup.CooldownRemainingMs.Value / 1000d).ToString("0.0") + "s";

        return "level " + setup.LevelId;
    }
}
