using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using OrbIx.Core.Features;
using OrbIx.Core.Playbooks;
using OrbIx.Core.Sessions;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// Draws opening ranges in price space: the range box, its high, low and midpoint, and the
/// projections §2 defines.
///
/// Everything here is geometry the engine already computed. The overlay reads an immutable
/// array of <see cref="OrSnapshot"/> and converts price and time to pixels; it decides
/// nothing, so it cannot disagree with the engine that produced the ranges.
///
/// Coordinates come from <c>IChartWindow.CoordinatesConverter</c> — <c>GetChartX(DateTime)</c>
/// and <c>GetChartY(double)</c> — which is a <c>[Published]</c> interface on the installed
/// assembly.
///
/// Two disciplines are structural rather than stylistic:
///
/// Pens and brushes are built once and reused, because <c>OnPaintChart</c> runs on every
/// frame and per-frame GDI object churn is the classic way to make a chart stutter.
///
/// Every coordinate is clamped to the pane before it is drawn. A price far outside the
/// visible scale converts to an enormous pixel value, and GDI+ given such a value does not
/// clip politely — it can throw, or draw a line across the entire screen.
/// </summary>
internal sealed class ChartOverlay : IDisposable
{
    /// <summary>
    /// Pixels of headroom beyond the pane that geometry is allowed to reach.
    ///
    /// Not zero: a line clipped exactly at the edge shows a visible stub end, whereas one
    /// clipped just outside reads as continuing past the edge, which is what it does.
    /// </summary>
    private const float ClampMargin = 4f;

    /// <summary>Below this pixel height a range box is drawn as a single line, not a box.</summary>
    private const float MinimumBoxHeight = 2f;

    /// <summary>
    /// The top-left area the platform reserves for indicator names, in pixels.
    ///
    /// Measured from a live chart rather than guessed: the platform stacks one line per
    /// indicator there, and a range label drawn into it is unreadable and makes the platform's
    /// own text unreadable too.
    /// </summary>
    private const float PlatformCornerWidth = 340f;

    private const float PlatformCornerHeight = 96f;

    private readonly SolidBrush labelBackground = new(Color.FromArgb(215, ChartTheme.Panel));

    private readonly Pen primaryExtensionPen;
    private readonly Pen minorExtensionPen;

    // THE SETUP'S OWN THREE, allocated once. Entry, stop and targets are the only prices on
    // this chart that describe a decision rather than an observation, so they are separated
    // from the level palette by weight as well as by hue: solid and heavier, because a line
    // that says "enter here" should not look like a line that says "yesterday closed here".
    private readonly Pen entryPen;
    private readonly Pen stopPen;
    private readonly Pen targetPen;
    private readonly SolidBrush entryText;
    private readonly SolidBrush stopText;
    private readonly SolidBrush targetText;

    /// <summary>
    /// The provenance label's brush — MUTED ON PURPOSE.
    ///
    /// It has to be readable and it must not compete with the price it qualifies. A record
    /// shouting for attention beside a signal is a different kind of dishonesty from hiding it.
    /// </summary>
    private readonly SolidBrush provenanceText;


    private readonly Font labelFont;
    private readonly Font smallFont;

    /// <summary>
    /// Label rectangles already drawn this frame, so two labels cannot land on each other.
    ///
    /// Reused across frames rather than allocated per frame: this runs on the paint path.
    /// </summary>
    private readonly List<RectangleF> placedLabels = new();

    /// <summary>
    /// Session names that are CONTEXT rather than tradeable — the initial balance and anything
    /// else configured with entries disallowed.
    ///
    /// Taken from configuration rather than matched against the literal "IB", so a window the
    /// operator renames or adds keeps the right treatment instead of quietly being drawn as an
    /// ordinary opening range.
    /// </summary>
    private readonly HashSet<string> contextSessions;

    /// <summary>
    /// One set of pens and brushes per session, built on demand and kept.
    ///
    /// Built lazily and cached because this is the paint path: allocating a pen per range per
    /// frame is how a four-millisecond budget disappears. Keyed by session and by whether the
    /// range was seeded, which are the only two things that change the styling.
    /// </summary>
    private readonly Dictionary<(string Session, bool Seeded), RangeStyle> styles = new();

    /// <summary>Pens and label brushes per level kind, built on demand and kept.</summary>
    private readonly Dictionary<LevelKind, Pen> levelPens = new();

    private readonly Dictionary<LevelKind, SolidBrush> levelText = new();

    private bool disposed;

    public ChartOverlay(string monoFamily, IEnumerable<string> contextSessions)
    {
        if (string.IsNullOrWhiteSpace(monoFamily))
            throw new ArgumentException("A font family is required.", nameof(monoFamily));

        this.contextSessions = new HashSet<string>(
            contextSessions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        this.labelFont = new Font(monoFamily, 8.0f, FontStyle.Bold, GraphicsUnit.Point);
        this.smallFont = new Font(monoFamily, 7.5f, FontStyle.Regular, GraphicsUnit.Point);

        // The 1.0x and 1.618x projections are where §7 selects targets, so they are drawn
        // more strongly than the intermediate ones. The distinction is visual weight only;
        // every multiple comes from configuration.
        this.primaryExtensionPen = new Pen(Color.FromArgb(150, ChartTheme.Alert), 1.2f)
        {
            DashStyle = DashStyle.Dash,
            DashPattern = new[] { 8f, 5f },
        };

        this.minorExtensionPen = new Pen(Color.FromArgb(90, ChartTheme.TextMuted), 1f)
        {
            DashStyle = DashStyle.Dot,
        };

        this.entryPen = new Pen(ChartTheme.Accent, 1.8f);
        this.stopPen = new Pen(ChartTheme.Alert, 1.6f);
        this.targetPen = new Pen(Color.FromArgb(170, ChartTheme.Accent), 1.1f)
        {
            DashStyle = DashStyle.Dash,
            DashPattern = new[] { 6f, 4f },
        };

        this.entryText = new SolidBrush(ChartTheme.Accent);
        this.stopText = new SolidBrush(ChartTheme.Alert);
        this.targetText = new SolidBrush(ChartTheme.TextSecondary);
        this.provenanceText = new SolidBrush(ChartTheme.TextMuted);
    }

    /// <summary>What the operator has chosen to see. Every element is individually optional.</summary>
    /// <param name="Box">The shaded range rectangle.</param>
    /// <param name="Edges">The range high and low, extended right.</param>
    /// <param name="Midline">The range midpoint.</param>
    /// <param name="Extensions">The projections at the configured multiples.</param>
    /// <param name="Labels">Text beside each level.</param>
    public readonly record struct Options(bool Box, bool Edges, bool Midline, bool Extensions, bool Labels);

    /// <summary>
    /// Draws every supplied range.
    /// </summary>
    /// <param name="graphics">From <c>PaintChartEventArgs.Graphics</c>.</param>
    /// <param name="window">The chart window supplying the converter and the pane rectangle.</param>
    /// <param name="ranges">Completed ranges, oldest first.</param>
    /// <param name="options">Which elements to draw.</param>
    /// <returns>How many ranges actually produced visible geometry.</returns>
    public int Draw(
        Graphics graphics,
        IChartWindow window,
        IReadOnlyList<OrSnapshot> ranges,
        Options options,
        DateTime nowUtc)
    {
        if (graphics is null)
            throw new ArgumentNullException(nameof(graphics));

        if (window is null || ranges is null || ranges.Count == 0)
            return 0;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return 0;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0 || pane.Height <= 0)
            return 0;

        var left = pane.Left;
        var right = pane.Right;
        var top = pane.Top - ClampMargin;
        var bottom = pane.Bottom + ClampMargin;

        var previousClip = graphics.Clip;
        var previousSmoothing = graphics.SmoothingMode;

        // Clipping to the pane is belt and braces alongside the clamping below: the clamp
        // keeps coordinates finite, the clip keeps a legitimately large box off the axis.
        graphics.SetClip(pane);
        graphics.SmoothingMode = SmoothingMode.None;

        var drawn = 0;

        // The registry is cleared in BeginFrame, which OnPaintChart calls before ANY
        // overlay draws — the HH/LL chips register first, so tags here dodge them.

        try
        {
            foreach (var range in ranges)
            {
                if (range is null)
                    continue;

                if (this.DrawOne(graphics, converter, range, options, nowUtc, left, right, top, bottom))
                    drawn++;
            }
        }
        finally
        {
            graphics.Clip = previousClip;
            graphics.SmoothingMode = previousSmoothing;
        }

        return drawn;
    }

    /// <summary>
    /// Draws the reference levels, each labelled, right-aligned against the price axis.
    ///
    /// These are the levels <see cref="LevelSelection"/> already chose — prior day and week
    /// extremes, overnight extremes, the initial balance, session opening ranges and round
    /// numbers near price. Which ones survive is decided in Core; this only paints them.
    ///
    /// It shares <c>placedLabels</c> with the range drawing, so a level's tag will not land on
    /// a session title that was already put down. Levels are painted after ranges for exactly
    /// that reason: the session boxes are the primary object and claim their space first.
    /// </summary>
    /// <param name="levels">Already filtered and ordered nearest-first.</param>
    /// <param name="labels">Whether to write the level's name beside it.</param>
    public int DrawLevels(
        Graphics graphics, IChartWindow window, IReadOnlyList<Level> levels, bool labels)
    {
        if (graphics is null)
            throw new ArgumentNullException(nameof(graphics));

        if (window is null || levels is null || levels.Count == 0)
            return 0;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return 0;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0 || pane.Height <= 0)
            return 0;

        var top = pane.Top - ClampMargin;
        var bottom = pane.Bottom + ClampMargin;

        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        var drawn = 0;

        try
        {
            foreach (var level in levels)
            {
                if (!TryY(converter, level.Price, top, bottom, out var y))
                    continue;

                // Clamped to a pane edge means it is off-scale. A line pinned to the top or the
                // bottom of the chart describes a price that is not on it.
                if (y <= top || y >= bottom)
                    continue;

                var pen = this.PenFor(level.Kind);
                graphics.DrawLine(pen, pane.Left, y, pane.Right, y);
                drawn++;

                if (labels)
                {
                    this.DrawTag(
                        graphics,
                        level.Label,
                        pane.Right - LevelLabelInset,
                        y - (this.smallFont.Height / 2f),
                        this.LabelBrushFor(level.Kind),
                        this.smallFont);
                }
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }

        return drawn;
    }

    /// <summary>How far in from the right axis a level's tag sits.</summary>
    private const float LevelLabelInset = 118f;

    /// <summary>
    /// The setup the engine currently proposes: entry, stop and every target.
    ///
    /// DRAWN AS LINES AND TAGS, NOT AS A PANEL. The information box that used to sit on this
    /// chart was removed at the operator's instruction, and re-introducing one to carry veto
    /// text would be putting it back under another name. Everything here is anchored to the
    /// price it describes, which is where a price belongs.
    ///
    /// THE PROVENANCE LABEL IS DRAWN WITH THE ENTRY AND IS NOT OPTIONAL. A drawn entry implies
    /// an edge; neither playbook has established one, and a line without its record beside it
    /// is the chart making a claim the evidence does not support. If the label cannot be
    /// placed, the whole setup is skipped rather than drawn bare - see the return below.
    /// </summary>
    /// <param name="provenance">
    /// From <see cref="OrbIx.Core.Playbooks.SignalProvenance"/>. Never assembled here: a label
    /// built in a renderer is one the offline suite cannot check.
    /// </param>
    public int DrawSetup(
        Graphics graphics,
        IChartWindow window,
        SetupEvaluation? evaluation,
        string provenance,
        bool labels)
    {
        if (graphics is null)
            throw new ArgumentNullException(nameof(graphics));

        if (window is null || evaluation?.Plan is not { } plan)
            return 0;

        var converter = window.CoordinatesConverter;

        if (converter is null)
            return 0;

        var pane = window.ClientRectangle;

        if (pane.Width <= 0 || pane.Height <= 0)
            return 0;

        var top = pane.Top - ClampMargin;
        var bottom = pane.Bottom + ClampMargin;
        var previousClip = graphics.Clip;
        graphics.SetClip(pane);

        var drawn = 0;

        try
        {
            drawn += this.DrawSetupLine(
                graphics, converter, pane, top, bottom,
                plan.EntryPrice,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} @ {2:N2}", plan.PlaybookId, plan.Direction, plan.EntryPrice),
                this.entryPen, this.entryText, labels);

            drawn += this.DrawSetupLine(
                graphics, converter, pane, top, bottom,
                plan.Stop.Price,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "STOP {0:N2}  {1}t", plan.Stop.Price, plan.RiskTicks),
                this.stopPen, this.stopText, labels);

            for (var i = 0; i < plan.Targets.Count; i++)
            {
                var target = plan.Targets[i];

                drawn += this.DrawSetupLine(
                    graphics, converter, pane, top, bottom,
                    target.Price,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "TP{0} {1:N2}  {2:N2}R", i + 1, target.Price, target.RMultiple),
                    this.targetPen, this.targetText, labels);
            }

            // Sits just under the entry, so the record and the price it qualifies are read
            // together rather than in different corners of the chart.
            if (!string.IsNullOrEmpty(provenance)
                && TryY(converter, plan.EntryPrice, top, bottom, out var entryY)
                && entryY > top && entryY < bottom)
            {
                this.DrawTag(
                    graphics, provenance,
                    pane.Left + SetupLabelInset, entryY + 2f,
                    this.provenanceText, this.smallFont);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
        }

        return drawn;
    }

    /// <summary>One priced line of a setup, with its tag on the left so it clears the levels.</summary>
    private int DrawSetupLine(
        Graphics graphics,
        IChartWindowCoordinatesConverter converter,
        RectangleF pane,
        float top,
        float bottom,
        double price,
        string tag,
        Pen pen,
        Brush brush,
        bool labels)
    {
        if (!TryY(converter, price, top, bottom, out var y))
            return 0;

        // Clamped to a pane edge means it is off-scale, and a line pinned to the top or bottom
        // of the chart describes a price that is not on it.
        if (y <= top || y >= bottom)
            return 0;

        graphics.DrawLine(pen, pane.Left, y, pane.Right, y);

        if (labels)
        {
            this.DrawTag(
                graphics, tag, pane.Left + SetupLabelInset,
                y - (this.smallFont.Height / 2f), brush, this.smallFont);
        }

        return 1;
    }

    /// <summary>How far in from the left axis a setup's tag sits, clear of the level tags.</summary>
    private const float SetupLabelInset = 8f;

    /// <summary>
    /// The pen for a level kind, cached.
    ///
    /// Reference levels are drawn in one restrained colour and separated by LINE STYLE rather
    /// than by hue, because the session ranges already own the palette — giving levels their
    /// own eight colours would make the chart a rainbow and destroy the session identity that
    /// is the point of colouring ranges at all.
    /// </summary>
    private Pen PenFor(LevelKind kind)
    {
        if (this.levelPens.TryGetValue(kind, out var existing))
            return existing;

        var (colour, width, dash) = kind switch
        {
            LevelKind.PriorDayHigh or LevelKind.PriorDayLow
                => (ChartTheme.TextPrimary, 1.3f, DashStyle.Solid),
            LevelKind.PriorDayClose
                => (ChartTheme.TextPrimary, 1.1f, DashStyle.Dash),
            LevelKind.PriorWeekHigh or LevelKind.PriorWeekLow
                => (ChartTheme.TextSecondary, 1.2f, DashStyle.DashDot),
            LevelKind.OvernightHigh or LevelKind.OvernightLow
                => (ChartTheme.TextSecondary, 1f, DashStyle.Dash),
            LevelKind.InitialBalanceHigh or LevelKind.InitialBalanceLow
                => (ChartTheme.Alert, 1.2f, DashStyle.Dash),
            LevelKind.RoundNumber
                => (ChartTheme.TextMuted, 1f, DashStyle.Dot),
            _
                => (ChartTheme.TextMuted, 1f, DashStyle.Dot),
        };

        var pen = new Pen(Color.FromArgb(kind == LevelKind.RoundNumber ? 70 : 165, colour), width)
        {
            DashStyle = dash,
        };

        this.levelPens[kind] = pen;

        return pen;
    }

    private SolidBrush LabelBrushFor(LevelKind kind)
    {
        if (this.levelText.TryGetValue(kind, out var existing))
            return existing;

        var colour = kind switch
        {
            LevelKind.PriorDayHigh or LevelKind.PriorDayLow or LevelKind.PriorDayClose
                => ChartTheme.TextPrimary,
            LevelKind.InitialBalanceHigh or LevelKind.InitialBalanceLow => ChartTheme.Alert,
            LevelKind.RoundNumber => ChartTheme.TextMuted,
            _ => ChartTheme.TextSecondary,
        };

        var brush = new SolidBrush(colour);
        this.levelText[kind] = brush;

        return brush;
    }

    /// <summary>Everything one range is drawn with.</summary>
    private readonly record struct RangeStyle(
        SolidBrush Fill, Pen Border, Pen Edge, Pen Mid, SolidBrush Text);

    /// <summary>
    /// The pens for a range, by session and provenance.
    ///
    /// AN INITIAL BALANCE DIFFERS ON TWO CHANNELS, not one: its own hue AND a dashed edge,
    /// against the opening range's solid. Colour alone would be indistinguishable on a
    /// greyscale screenshot and for a reader with a colour-vision deficiency, and telling an
    /// ORB from an IB is a distinction the operator asked for twice.
    ///
    /// A seeded range keeps its session's HUE and loses opacity. The previous styling replaced
    /// it with grey, which meant every reconstructed session looked like every other one — the
    /// provenance was visible and the identity was not.
    /// </summary>
    private RangeStyle StyleFor(OrSnapshot range)
    {
        var key = (range.SessionName ?? string.Empty, !range.FlowObserved);

        if (this.styles.TryGetValue(key, out var existing))
            return existing;

        var seeded = key.Item2;

        // Colour AND dash come from one rule, in Core, which is where they are tested. Deciding
        // either of them here would be a second definition free to drift from the one the suite
        // asserts against.
        var resolved = SessionPalette.StyleFor(key.Item1, this.contextSessions.Contains(key.Item1));
        var hue = Color.FromArgb(resolved.Hue.R, resolved.Hue.G, resolved.Hue.B);
        var context = resolved.Dashed;

        var fillAlpha = seeded ? 14 : 30;
        var borderAlpha = seeded ? 90 : 150;
        var edgeAlpha = seeded ? 150 : 235;

        var edge = new Pen(Color.FromArgb(edgeAlpha, hue), context ? 1.3f : 1.7f);
        var border = new Pen(Color.FromArgb(borderAlpha, hue), 1f);

        if (context)
        {
            // The initial balance is the same market seen over a longer window, so it is drawn
            // as a dashed relative of the same colour rather than as a separate object.
            edge.DashStyle = DashStyle.Dash;
            edge.DashPattern = new[] { 9f, 5f };
            border.DashStyle = DashStyle.Dot;
        }

        var mid = new Pen(Color.FromArgb(seeded ? 90 : 150, hue), 1f)
        {
            DashStyle = DashStyle.Dash,
            DashPattern = new[] { 6f, 4f },
        };

        var style = new RangeStyle(
            new SolidBrush(Color.FromArgb(fillAlpha, hue)),
            border,
            edge,
            mid,
            new SolidBrush(Color.FromArgb(seeded ? 190 : 255, hue)));

        this.styles[key] = style;

        return style;
    }

    private bool DrawOne(
        Graphics graphics,
        IChartWindowCoordinatesConverter converter,
        OrSnapshot range,
        Options options,
        DateTime nowUtc,
        float left, float right, float top, float bottom)
    {
        if (!TryY(converter, range.Orh, top, bottom, out var yHigh)
            || !TryY(converter, range.Orl, top, bottom, out var yLow))
        {
            return false;
        }

        // Both edges clamped to the same side means the whole range is off-scale. Drawing it
        // would put a flat line at the top or bottom of the pane describing nothing.
        if ((yHigh <= top && yLow <= top) || (yHigh >= bottom && yLow >= bottom))
            return false;

        if (!TryX(converter, range.OpenUtc, out var xOpen)
            || !TryX(converter, range.CloseUtc, out var xClose))
        {
            return false;
        }

        var boxLeft = Math.Max(xOpen, left);
        var boxRight = Math.Min(Math.Max(xClose, boxLeft + 1f), right);

        if (boxRight <= left || boxLeft >= right)
            return false;

        var seeded = !range.FlowObserved;
        var style = this.StyleFor(range);

        if (options.Box)
        {
            var height = Math.Max(yLow - yHigh, MinimumBoxHeight);

            graphics.FillRectangle(style.Fill, boxLeft, yHigh, boxRight - boxLeft, height);
            graphics.DrawRectangle(style.Border, boxLeft, yHigh, boxRight - boxLeft, height);
        }

        // Levels start where the range closed: an opening-range level is only meaningful once
        // the range that defined it is finished.
        var levelLeft = Math.Max(boxRight, left);

        // And they STOP when the session does. A session's opening range defines levels for
        // that session, not for every day after it. Running every level to the right edge —
        // which is what this did — turns a month of history into a grid of two hundred lines
        // and buries the session actually being traded.
        var live = nowUtc < range.SessionEndUtc;
        var levelRight = right;

        if (!live)
        {
            if (!TryX(converter, range.SessionEndUtc, out var xEnd))
                return true;

            levelRight = Math.Min(Math.Max(xEnd, levelLeft), right);

            // Entirely off the left of the view: the box was drawn, the levels are history.
            if (levelRight <= left)
                return true;
        }

        if (options.Edges)
        {
            graphics.DrawLine(style.Edge, levelLeft, yHigh, levelRight, yHigh);
            graphics.DrawLine(style.Edge, levelLeft, yLow, levelRight, yLow);
        }

        if (options.Midline && TryY(converter, range.Orm, top, bottom, out var yMid))
            graphics.DrawLine(style.Mid, levelLeft, yMid, levelRight, yMid);

        // Extension targets belong to the session that is trading. A finished session's
        // projections are not levels anyone is working, and eight dashed lines per session
        // across a month of history is the single biggest source of clutter.
        if (options.Extensions && live)
            this.DrawExtensions(graphics, converter, range, levelLeft, levelRight, top, bottom);

        if (options.Labels)
            this.DrawLabels(graphics, range, seeded, live, style, xOpen, left, yHigh, yLow, levelRight);

        return true;
    }

    private void DrawExtensions(
        Graphics graphics,
        IChartWindowCoordinatesConverter converter,
        OrSnapshot range,
        float levelLeft, float right, float top, float bottom)
    {
        foreach (var (multiple, above, below) in range.Extensions())
        {
            // 1.0x and 1.618x are the target multiples §7 draws from, so they carry more
            // weight. Comparison is against the configured value, not a literal ladder.
            var pen = multiple >= 1.0d ? this.primaryExtensionPen : this.minorExtensionPen;

            if (TryY(converter, above, top, bottom, out var yAbove) && yAbove > top && yAbove < bottom)
                graphics.DrawLine(pen, levelLeft, yAbove, right, yAbove);

            if (TryY(converter, below, top, bottom, out var yBelow) && yBelow > top && yBelow < bottom)
                graphics.DrawLine(pen, levelLeft, yBelow, right, yBelow);
        }
    }

    private void DrawLabels(
        Graphics graphics,
        OrSnapshot range,
        bool seeded,
        bool live,
        RangeStyle style,
        float boxLeft, float paneLeft, float yHigh, float yLow, float right)
    {
        // A range whose box starts off the left of the view gets no title. Every such label
        // would otherwise be clamped to the pane's left edge, and several of them stack into
        // an illegible pile — which is exactly what happened with ASIA, LONDON and FRANKFURT
        // all landing on the same few pixels. Their levels still draw; the range they belong
        // to is identified by the box when it is scrolled into view.
        if (boxLeft < paneLeft)
            return;

        var grade = range.Gradeable
            ? range.Grade.ToString().ToUpperInvariant()
            : "UNGRADED";

        // The kind is spelled out. Line style already separates an opening range from an
        // initial balance, but a label that says which is the difference between a chart the
        // operator can read and one they have to remember a convention for.
        var kind = this.contextSessions.Contains(range.SessionName ?? string.Empty) ? "IB" : "ORB";

        var title = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2}{3}",
            range.SessionName,
            kind,
            grade,
            seeded ? " · SEEDED" : string.Empty);

        // Anchored to the range's own box, so several sessions on screen stay individually
        // readable, and skipped outright if it would land on something already drawn. Drawn in
        // the session's own colour, so the label and its lines are visibly the same object.
        this.DrawTag(graphics, title, boxLeft + 4f, yHigh - this.labelFont.Height - 2f,
            style.Text, this.labelFont);

        // Price tags only for a session still trading. On a finished one they would stack a
        // dozen deep against the right axis, all of them describing prices nobody is working.
        if (!live)
            return;

        this.DrawTag(
            graphics,
            string.Format(CultureInfo.InvariantCulture, "{0} {1}H {2:N2}", range.SessionName, kind, range.Orh),
            right - 150f, yHigh - this.smallFont.Height - 1f, style.Text, this.smallFont);

        this.DrawTag(
            graphics,
            string.Format(CultureInfo.InvariantCulture, "{0} {1}L {2:N2}", range.SessionName, kind, range.Orl),
            right - 150f, yLow + 1f, style.Text, this.smallFont);
    }

    /// <summary>
    /// Draws text over a small backing panel so it stays readable against candles.
    /// </summary>
    /// <summary>
    /// Draws text over a small backing panel, unless it would land on something already drawn
    /// this frame.
    ///
    /// Skipping beats overlapping. Two labels on the same pixels leave both unreadable and
    /// tell the reader less than one label would.
    /// </summary>
    /// <returns>True when the text was drawn.</returns>
    private bool DrawTag(Graphics graphics, string text, float x, float y, Brush brush, Font font)
    {
        var size = graphics.MeasureString(text, font);
        var rect = new RectangleF(x - 3f, y, size.Width + 6f, size.Height);

        if (!TryReserve(this.placedLabels, rect))
            return false;

        graphics.FillRectangle(this.labelBackground, rect);
        graphics.DrawString(text, font, brush, x, y);
        return true;
    }

    /// <summary>
    /// Starts a paint frame: clears the shared label-collision registry and reserves the
    /// platform's own top-left corner, then hands the registry out so EVERY overlay this
    /// indicator paints (the HH/LL chips included) reserves against the same list.
    /// Grew out of a real screenshot: HH/LL chips and session-level tags printing through
    /// each other at the same prices.
    /// </summary>
    internal List<RectangleF> BeginFrame(RectangleF pane)
    {
        this.placedLabels.Clear();

        // The platform writes its own indicator names down the top-left corner. Reserving that
        // band stops labels being drawn over "ORB-IX" and over other indicators' names,
        // which is what happened: three range labels piled on top of each other and on top of
        // another indicator's title.
        this.placedLabels.Add(new RectangleF(
            pane.Left, pane.Top, PlatformCornerWidth, PlatformCornerHeight));

        return this.placedLabels;
    }

    /// <summary>
    /// Reserves a label rectangle in a frame's registry, or refuses because something
    /// already occupies those pixels. Skipping beats overlapping.
    /// </summary>
    internal static bool TryReserve(List<RectangleF> registry, RectangleF rect)
    {
        foreach (var placed in registry)
        {
            if (placed.IntersectsWith(rect))
                return false;
        }

        registry.Add(rect);
        return true;
    }

    /// <summary>
    /// Converts a price to a pixel row, clamped to the pane.
    ///
    /// Returns false for a coordinate the converter could not produce a finite value for,
    /// which happens while a chart is still initialising. Drawing at NaN throws inside GDI+
    /// and takes the whole frame with it.
    /// </summary>
    // internal: shared with HhLlOverlay so the guarded conversions exist once.
    internal static bool TryY(
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

    /// <summary>
    /// Converts an instant to a pixel column. Unclamped, because the caller clamps against
    /// the box and the level separately — an open far to the left is legitimate.
    /// </summary>
    // internal: shared with HhLlOverlay so the guarded conversions exist once.
    internal static bool TryX(IChartWindowCoordinatesConverter converter, DateTime utc, out float x)
    {
        x = 0f;

        var raw = converter.GetChartX(utc);

        if (double.IsNaN(raw) || double.IsInfinity(raw))
            return false;

        // Bounded well outside any real pane before the cast: converting a double of 1e18 to
        // float and handing it to GDI+ is undefined behaviour in practice.
        x = (float)Math.Clamp(raw, -1e6, 1e6);
        return true;
    }

    public void Dispose()
    {
        this.entryPen?.Dispose();
        this.stopPen?.Dispose();
        this.targetPen?.Dispose();
        this.entryText?.Dispose();
        this.stopText?.Dispose();
        this.targetText?.Dispose();
        this.provenanceText?.Dispose();

        if (this.disposed)
            return;

        this.disposed = true;

        this.labelBackground.Dispose();
        this.primaryExtensionPen.Dispose();
        this.minorExtensionPen.Dispose();
        this.labelFont.Dispose();
        this.smallFont.Dispose();

        // The per-session pens are built lazily and cached, so they are disposed here rather
        // than being fields. Leaking a GDI pen per session per reload would eventually exhaust
        // the handle table on a chart that is opened and closed all day.
        foreach (var style in this.styles.Values)
        {
            style.Fill.Dispose();
            style.Border.Dispose();
            style.Edge.Dispose();
            style.Mid.Dispose();
            style.Text.Dispose();
        }

        this.styles.Clear();

        foreach (var pen in this.levelPens.Values)
            pen.Dispose();

        foreach (var brush in this.levelText.Values)
            brush.Dispose();

        this.levelPens.Clear();
        this.levelText.Clear();
    }
}
