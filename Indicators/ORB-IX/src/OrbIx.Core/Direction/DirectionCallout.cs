using System;

namespace OrbIx.Core.Direction;

/// <summary>Whether a directional read looks like a quick scalp or a move worth holding.</summary>
public enum DirectionCalloutKind
{
    /// <summary>No directional verdict, or the vote tied — nothing to call out.</summary>
    None,

    /// <summary>
    /// Directional, but without multi-timeframe structural confirmation, or with most of the
    /// day's typical range already spent — the kind of read that is as likely to snap back as
    /// to continue, and is treated as a quick scalp rather than something to hold.
    /// </summary>
    ScalpLong,

    /// <summary>The short-side counterpart of <see cref="ScalpLong"/>.</summary>
    ScalpShort,

    /// <summary>
    /// Directional, AND the higher-timeframe structure lanes confirm it, AND there is still
    /// room left in the day's typical range — the kind of read with more than one input's
    /// worth of reason to expect it keeps going, worth holding for a bigger play.
    /// </summary>
    HoldLong,

    /// <summary>The short-side counterpart of <see cref="HoldLong"/>.</summary>
    HoldShort,
}

/// <summary>One callout: "POSSIBLE LONG SCALP", "POSSIBLE HOLD SHORT", or nothing.</summary>
/// <param name="Kind">Which of the five states this is.</param>
/// <param name="Text">Already formatted for the chart, or empty when <see cref="Kind"/> is None.</param>
public readonly record struct DirectionCallout(DirectionCalloutKind Kind, string Text)
{
    public static readonly DirectionCallout None = new(DirectionCalloutKind.None, string.Empty);

    /// <summary>Whether this callout is a long side of any kind.</summary>
    public bool IsLong => this.Kind is DirectionCalloutKind.ScalpLong or DirectionCalloutKind.HoldLong;

    /// <summary>Whether this callout is a short side of any kind.</summary>
    public bool IsShort => this.Kind is DirectionCalloutKind.ScalpShort or DirectionCalloutKind.HoldShort;

    /// <summary>
    /// Builds the callout from readings this project already takes elsewhere on the same
    /// panel — never a new prediction, only a louder rendering of an existing one, plus one
    /// extra question: does the higher-timeframe structure confirm it, and is there still
    /// room left in the day.
    /// </summary>
    /// <remarks>
    /// THE SPLIT IS A TRADING JUDGEMENT, NOT A MEASURED THRESHOLD — exactly like the flat
    /// bands in <see cref="DirectionSettings"/>. "Structure confirms" and "room left" are
    /// both defensible proxies for durability: a move several timeframes agree on, that
    /// hasn't yet eaten most of the day's typical range, is more likely to keep going than
    /// one where only the fastest inputs (location, flow) are voting and the range is
    /// already spent. NEITHER PROXY HAS BEEN MEASURED HERE. This callout carries no claim
    /// that it predicts anything; it only names, loudly, what the panel already measured
    /// quietly.
    ///
    /// ADR UNKNOWN READS AS ROOM SPENT, NOT ROOM LEFT. Defaulting the unmeasurable case to
    /// the optimistic answer would let a callout claim room to run on the one input that
    /// could not be checked; defaulting to the conservative answer means a HOLD callout is
    /// only ever shown when room was actually confirmed left, never assumed.
    /// </remarks>
    /// <param name="read">The overall verdict, already counted over every vote.</param>
    /// <param name="structureLanes">The structure lanes alone — not location, not flow.</param>
    /// <param name="sessionRange">Today's high-to-low range so far, in price.</param>
    /// <param name="averageDailyRange">The average daily range, in price, or NaN if unknown.</param>
    /// <param name="minConfirmingLanes">
    /// How many structure lanes must agree with the verdict, at minimum, for that to count as
    /// confirmation. One lane agreeing on an otherwise-quiet structure row is one timeframe's
    /// opinion, not multi-timeframe confirmation.
    /// </param>
    /// <param name="roomSpentShare">
    /// Share of the average daily range at or above which the day is considered to have spent
    /// its room. Matches the boundary <see cref="DirectionInputs.Regime"/> already draws
    /// ("expanded" vs "inside average") by default, so the callout and the regime row on the
    /// panel do not disagree about which side of the line the day is on unless deliberately
    /// configured to.
    /// </param>
    public static DirectionCallout From(
        DirectionRead read,
        System.Collections.Generic.IReadOnlyList<DirectionVote> structureLanes,
        double sessionRange,
        double averageDailyRange,
        int minConfirmingLanes,
        double roomSpentShare)
    {
        ArgumentNullException.ThrowIfNull(structureLanes);

        if (read.Verdict != DirectionVerdict.Up && read.Verdict != DirectionVerdict.Down)
            return None;

        bool up = read.Verdict == DirectionVerdict.Up;

        int lanesUp = 0;
        int lanesDown = 0;

        foreach (var lane in structureLanes)
        {
            if (lane.State == DirectionState.Up) lanesUp++;
            else if (lane.State == DirectionState.Down) lanesDown++;
        }

        int confirming = up ? lanesUp : lanesDown;
        int opposing = up ? lanesDown : lanesUp;
        bool structureConfirms = confirming >= minConfirmingLanes && confirming > opposing;

        bool roomLeft = double.IsFinite(sessionRange) && double.IsFinite(averageDailyRange)
            && averageDailyRange > 0 && (sessionRange / averageDailyRange) < roomSpentShare;

        bool hold = structureConfirms && roomLeft;

        if (up)
        {
            return hold
                ? new DirectionCallout(DirectionCalloutKind.HoldLong, "POSSIBLE HOLD LONG")
                : new DirectionCallout(DirectionCalloutKind.ScalpLong, "POSSIBLE LONG SCALP");
        }

        return hold
            ? new DirectionCallout(DirectionCalloutKind.HoldShort, "POSSIBLE HOLD SHORT")
            : new DirectionCallout(DirectionCalloutKind.ScalpShort, "POSSIBLE SHORT SCALP");
    }
}
