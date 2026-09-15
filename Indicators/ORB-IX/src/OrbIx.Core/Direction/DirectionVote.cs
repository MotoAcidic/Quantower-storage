using System;

namespace OrbIx.Core.Direction;

/// <summary>
/// What one input says about direction right now.
/// </summary>
/// <remarks>
/// FOUR STATES, AND THE LAST TWO ARE NOT THE SAME THING.
///     <see cref="Undecided"/> means the input was measured and its answer is "no
///     direction" — a structure trend of zero, a price sitting exactly on VWAP.
///     <see cref="Unavailable"/> means it could not be measured at all, because the
///     history to measure it does not exist yet.
///
///     Collapsing them loses the distinction between "the market has no direction
///     here" and "I cannot see". The chart says <c>—</c> for the first and names the
///     reason for the second, and neither ever counts toward a verdict.
/// </remarks>
public enum DirectionState
{
    /// <summary>Measured, and pointing up.</summary>
    Up = 1,

    /// <summary>Measured, and pointing down.</summary>
    Down = -1,

    /// <summary>Measured, and pointing neither way.</summary>
    Undecided = 0,

    /// <summary>Not measurable yet. Distinct from Undecided and never a vote.</summary>
    Unavailable = 2,
}

/// <summary>
/// One input's reading, with the number it came from.
/// </summary>
/// <param name="Name">Short label for the panel, e.g. "5m" or "VWAP".</param>
/// <param name="State">What this input says.</param>
/// <param name="Detail">
/// The measurement behind the state, in words — "+2,431", "above by 12.5". Carried so
/// the panel can show WHY without recomputing it, and so a journalled row is
/// reconstructable afterwards rather than merely re-assertable.
/// </param>
public readonly record struct DirectionVote(string Name, DirectionState State, string Detail)
{
    /// <summary>Whether this vote counts toward a verdict. Only Up and Down do.</summary>
    public bool IsDirectional =>
        this.State is DirectionState.Up or DirectionState.Down;

    /// <summary>A vote that could not be taken, naming why.</summary>
    public static DirectionVote NotAvailable(string name, string why) =>
        new(name, DirectionState.Unavailable, why);
}
