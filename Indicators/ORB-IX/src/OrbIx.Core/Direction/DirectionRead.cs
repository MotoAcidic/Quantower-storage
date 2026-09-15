using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbIx.Core.Direction;

/// <summary>The single answer to "which way is it going right now".</summary>
public enum DirectionVerdict
{
    /// <summary>More directional votes point up than down.</summary>
    Up,

    /// <summary>More point down than up.</summary>
    Down,

    /// <summary>Votes were cast and they tie. NOT a coin flip and never resolved as one.</summary>
    Mixed,

    /// <summary>No directional vote was cast at all.</summary>
    Undecided,
}

/// <summary>
/// Combines the votes into one verdict, by counting them.
/// </summary>
/// <remarks>
/// THE RULE IS A COUNT, AND THAT IS DELIBERATE.
///     A weighted blend would need weights, and NOTHING IN THIS PROJECT HAS
///     MEASURED WHAT THEY SHOULD BE. Inventing them would dress an arbitrary
///     choice as an analytical one and hide it inside a number that looks
///     precise. Counting is transparent, reproducible, and honest about how
///     little it assumes: four of six, and you can see which four.
///
/// A TIE IS Mixed, NEVER A COIN FLIP.
///     Three up and three down is information — the inputs disagree — and
///     resolving it to a side would destroy exactly the information the reader
///     needs. Mixed is a real answer.
///
/// Undecided AND Mixed ARE DIFFERENT ANSWERS.
///     Undecided means nothing could vote. Mixed means everything voted and
///     they split. A reader who cannot tell those apart cannot tell "no
///     information" from "conflicting information".
/// </remarks>
public sealed record DirectionRead(
    DirectionVerdict Verdict,
    int Agreeing,
    int Cast,
    IReadOnlyList<DirectionVote> Votes)
{
    /// <summary>How many votes were offered in total, including non-directional ones.</summary>
    public int Offered => this.Votes.Count;

    /// <summary>
    /// Reads the votes.
    /// </summary>
    /// <param name="votes">Every input's reading, directional or not.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="votes"/> is null.</exception>
    public static DirectionRead From(IReadOnlyList<DirectionVote> votes)
    {
        ArgumentNullException.ThrowIfNull(votes);

        int up = 0;
        int down = 0;

        for (int i = 0; i < votes.Count; i++)
        {
            switch (votes[i].State)
            {
                case DirectionState.Up: up++; break;
                case DirectionState.Down: down++; break;

                // Undecided and Unavailable are counted by neither side. They are
                // shown on the panel and they never move the verdict -- an input
                // that cannot see must not be able to vote.
                case DirectionState.Undecided:
                case DirectionState.Unavailable:
                    break;
            }
        }

        int cast = up + down;

        if (cast == 0)
            return new DirectionRead(DirectionVerdict.Undecided, 0, 0, votes);

        if (up > down)
            return new DirectionRead(DirectionVerdict.Up, up, cast, votes);

        if (down > up)
            return new DirectionRead(DirectionVerdict.Down, down, cast, votes);

        return new DirectionRead(DirectionVerdict.Mixed, up, cast, votes);
    }

    /// <summary>
    /// The headline line, e.g. <c>UP 4 of 6</c>.
    /// </summary>
    /// <remarks>
    /// The count is part of the headline rather than a footnote: "UP" alone reads
    /// as certainty, and "UP 4 of 6" reads as what it actually is.
    /// </remarks>
    public string Headline => this.Verdict switch
    {
        DirectionVerdict.Undecided => "UNDECIDED",
        DirectionVerdict.Mixed => $"MIXED {this.Agreeing}-{this.Cast - this.Agreeing}",
        _ => $"{this.Verdict.ToString().ToUpperInvariant()} {this.Agreeing} of {this.Cast}",
    };
}
