using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbIx.Core.Direction;

/// <summary>One line of the panel.</summary>
/// <param name="Label">Left column, e.g. "structure".</param>
/// <param name="Value">Right column, already formatted.</param>
/// <param name="State">
/// What the row says, for colouring. <see cref="DirectionState.Undecided"/> for a row
/// that carries no direction of its own, such as the regime line.
/// </param>
public readonly record struct DirectionRow(string Label, string Value, DirectionState State);

/// <summary>
/// The panel's content, as data.
/// </summary>
/// <param name="Headline">The verdict line, e.g. "UP 4 of 6".</param>
/// <param name="Verdict">The verdict itself, for colouring the headline.</param>
/// <param name="Rows">The component rows, in display order.</param>
public sealed record DirectionPanelContent(
    string Headline, DirectionVerdict Verdict, IReadOnlyList<DirectionRow> Rows);

/// <summary>
/// Builds what the panel shows, without drawing any of it.
/// </summary>
/// <remarks>
/// WHY THE CONTENT IS DATA AND THE DRAWING IS SOMEWHERE ELSE.
///     Two renderers show this panel — the standalone Direction indicator and
///     ORB-IX. If each formatted its own rows they would drift, and the first
///     anyone would know of it is two charts on two monitors disagreeing about
///     the same market with no way to tell which was right.
///
///     One tested function produces the text; the renderers only paint it.
/// </remarks>
public static class DirectionPanel
{
    /// <summary>
    /// Assembles the panel.
    /// </summary>
    /// <param name="structure">The per-timeframe structure votes, in display order.</param>
    /// <param name="location">Price against VWAP.</param>
    /// <param name="flow">Cumulative delta's reading.</param>
    /// <param name="regime">Volatility context, from <see cref="DirectionInputs.Regime"/>.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="structure"/> is null.</exception>
    public static DirectionPanelContent Build(
        IReadOnlyList<DirectionVote> structure,
        DirectionVote location,
        DirectionVote flow,
        string regime)
    {
        ArgumentNullException.ThrowIfNull(structure);

        // The verdict counts the structure lanes AND the two others. The regime is
        // absent from this list on purpose: it has no direction to contribute.
        var voting = new List<DirectionVote>(structure.Count + 2);
        voting.AddRange(structure);
        voting.Add(location);
        voting.Add(flow);

        DirectionRead read = DirectionRead.From(voting);

        var rows = new List<DirectionRow>(4)
        {
            new("structure", string.Join("  ", structure.Select(Describe)), Aggregate(structure)),
            new("location", location.Detail, location.State),
            new("flow", flow.Detail, flow.State),

            // Undecided, always: the regime row is context and must never read as a
            // direction, however the renderer colours the other rows.
            new("regime", regime ?? "unknown", DirectionState.Undecided),
        };

        return new DirectionPanelContent(read.Headline, read.Verdict, rows);
    }

    /// <summary>
    /// One timeframe, as it appears on the structure row: <c>5m UP</c>, or <c>15m —</c>.
    /// </summary>
    private static string Describe(DirectionVote vote) => vote.State switch
    {
        DirectionState.Up => $"{vote.Name} UP",
        DirectionState.Down => $"{vote.Name} DN",

        // An em dash for measured-but-undecided, and a question mark for
        // could-not-measure. They look different because they ARE different, and a
        // reader who cannot tell them apart cannot tell a flat market from a blind
        // indicator.
        DirectionState.Undecided => $"{vote.Name} —",
        _ => $"{vote.Name} ?",
    };

    /// <summary>
    /// What the structure row as a whole says, for its colour only.
    /// </summary>
    /// <remarks>
    /// This is a colour hint, NOT a second verdict. The real count happens once, in
    /// <see cref="DirectionRead.From"/>, over the individual lane votes — computing
    /// a per-row winner and then counting rows would weight a four-lane structure
    /// group the same as one VWAP reading.
    /// </remarks>
    private static DirectionState Aggregate(IReadOnlyList<DirectionVote> votes)
    {
        int up = votes.Count(v => v.State == DirectionState.Up);
        int down = votes.Count(v => v.State == DirectionState.Down);

        if (up > down) return DirectionState.Up;
        if (down > up) return DirectionState.Down;

        return DirectionState.Undecided;
    }
}
