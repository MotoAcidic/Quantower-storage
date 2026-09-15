using System;
using System.Collections.Generic;
using System.Text;

namespace OrbIx.Core.Diagnostics;

/// <summary>
/// What a status entry IS, which decides where it can appear.
///
/// The distinction is the fix. Both kinds used to be concatenated into one string with
/// the same separator and drawn in the same colour, so "display only — measured
/// non-predictive" (a deliberate statement about a working overlay) and "no range" (a
/// feature that is not working) were indistinguishable to a reader. The operator asked
/// why the corner looked like an error; it looked like an error because a working
/// feature and a broken one were rendered identically.
/// </summary>
public enum StatusKind
{
    /// <summary>
    /// A standing statement about a working feature — that it measured null and is drawn
    /// for reference only. Constant across every ORB-IX overlay, so it goes to the log
    /// and never to the chart: a constant carries information once and is wallpaper after.
    /// </summary>
    Label,

    /// <summary>
    /// Something is not working. Rare by construction, and therefore worth drawing.
    /// </summary>
    Problem,
}

/// <summary>One status entry: what it says, and what kind of thing it is.</summary>
public readonly record struct StatusEntry(StatusKind Kind, string Text);

/// <summary>
/// Collects status entries and renders the two audiences separately: the chart gets
/// problems only, the log gets everything.
///
/// A HEALTHY BLOCK RENDERS AN EMPTY CHART STRING, and that is the design rather than a
/// missing message — the chart shows nothing at all when nothing is wrong, so anything
/// that does appear is known to matter. §11: this lives in OrbIx.Core, so the rule is
/// provable without a platform.
/// </summary>
public sealed class StatusBlock
{
    /// <summary>
    /// Marks the problems line.
    ///
    /// MEASURED, NOT CHOSEN BY EYE. The chart draws through GDI+, whose
    /// <c>FontFamily.GenericSansSerif</c> resolves to Microsoft Sans Serif on the
    /// deployment hosts, and that font's character map was queried directly
    /// (2026-08-28, ryzen-pc, System.Windows.Media.GlyphTypeface):
    ///
    ///   U+25B2 ▲ black up triangle  — ABSENT
    ///   U+26A0 ⚠ warning sign       — ABSENT
    ///   U+203C ‼ double exclamation — PRESENT
    ///   U+00B7 · middle dot         — PRESENT (which is why the separator has always worked)
    ///
    /// Both obvious warning glyphs would have rendered as a missing-glyph box — a fault
    /// report that itself fails to render, which is precisely the class of silent failure
    /// this type exists to end. Any replacement must be checked the same way.
    /// </summary>
    public const string ProblemMarker = "‼";

    private const string Separator = " · ";

    private readonly List<StatusEntry> entries = new();

    /// <summary>Every entry, in the order added.</summary>
    public IReadOnlyList<StatusEntry> Entries => this.entries;

    /// <summary>How many entries report something not working.</summary>
    public int ProblemCount
    {
        get
        {
            var count = 0;
            foreach (var entry in this.entries)
            {
                if (entry.Kind == StatusKind.Problem)
                    count++;
            }

            return count;
        }
    }

    /// <summary>True when the chart has something to say.</summary>
    public bool HasProblems => this.ProblemCount > 0;

    /// <summary>
    /// Records an entry. Blank text is refused rather than stored: an empty problem would
    /// draw a marker with nothing beside it, and an empty label would pad the log with a
    /// separator and no content.
    /// </summary>
    public void Add(StatusKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        this.entries.Add(new StatusEntry(kind, text.Trim()));
    }

    /// <summary>Records a standing statement about a working feature.</summary>
    public void AddLabel(string text) => this.Add(StatusKind.Label, text);

    /// <summary>Records something that is not working.</summary>
    public void AddProblem(string text) => this.Add(StatusKind.Problem, text);

    /// <summary>
    /// What the chart draws: the marker and the problems, or an empty string when there
    /// are none. Labels can never reach this — that is enforced here, not by the caller.
    /// </summary>
    public string ChartText()
    {
        if (!this.HasProblems)
            return string.Empty;

        var builder = new StringBuilder(ProblemMarker).Append(' ');
        var first = true;

        foreach (var entry in this.entries)
        {
            if (entry.Kind != StatusKind.Problem)
                continue;

            if (!first)
                builder.Append(Separator);

            builder.Append(entry.Text);
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// What the log records: everything, with each section named so a reader knows which
    /// kind they are looking at. Returns an empty string only when nothing was added.
    /// </summary>
    public string LogText()
    {
        if (this.entries.Count == 0)
            return string.Empty;

        var labels = new StringBuilder();
        var problems = new StringBuilder();

        foreach (var entry in this.entries)
        {
            var target = entry.Kind == StatusKind.Problem ? problems : labels;

            if (target.Length != 0)
                target.Append(Separator);

            target.Append(entry.Text);
        }

        var builder = new StringBuilder();

        if (problems.Length != 0)
            builder.Append("problems: ").Append(problems);

        if (labels.Length != 0)
        {
            if (builder.Length != 0)
                builder.Append(" | ");

            builder.Append("display-only: ").Append(labels);
        }

        return builder.ToString();
    }
}
