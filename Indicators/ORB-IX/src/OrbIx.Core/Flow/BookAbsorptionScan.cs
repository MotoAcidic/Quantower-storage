using System;
using System.Collections.Generic;
using System.Globalization;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Features;

namespace OrbIx.Core.Flow;

/// <summary>
/// How much aggressive volume an episode must carry before it is drawn.
/// </summary>
/// <param name="MinVolume">
/// Contracts traded into the level inside the window. MEASURED: volume at one price in a
/// five-second window runs p50 16, p75 29, p90 46, p99 94 across an RTH session of MNQ
/// (82,807 price-windows, 2026-08-27). At 40 roughly the busiest sixth of episodes survive.
/// The ratio is what decides whether absorption HAPPENED; this only decides whether it was
/// big enough to be worth a line.
/// </param>
public readonly record struct BookAbsorptionSettings(double MinVolume)
{
    public void Validate()
    {
        if (!(this.MinVolume > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.MinVolume), this.MinVolume,
                "A floor of zero draws a line for every print that touched a level.");
        }
    }
}

/// <summary>
/// Absorption read from the BOOK — did resting size hold while volume traded through it —
/// turned into levels.
///
/// WHY THIS EXISTS ALONGSIDE THE FOOTPRINT READING, MEASURED RATHER THAN ASSERTED. The
/// absorption display ORB-IX absorbed from Aramid Flow is the stacked-imbalance scan with a
/// different volume floor, and across 37 sessions of MNQ tick history it puts at most TWO lines
/// on the chart that the stacked-imbalance display is not already drawing, at any floor that
/// draws at all:
///
///     absorption floor   levels/session   on a line stacked imbalance does not draw
///         80                  0.3              1  of 12   over 37 sessions
///         60                  1.6              1  of 61
///         50                  4.5              2  of 166
///         45                  7.2              0  of 267   -- the same display exactly
///
/// A floor cannot make two readings of one measurement into two tools. This is a DIFFERENT
/// MEASUREMENT: the footprint knows only what traded, and can never answer whether the size
/// standing there held, was consumed, or was pulled before it was hit.
///
/// THE SIGN IS OPPOSITE TO THE FOOTPRINT READING, AND THAT IS THE WHOLE POINT. A stack of
/// buy-side diagonals argues BULLISH because it is initiative: buyers lifting offers. Buying
/// that is ABSORBED argues BEARISH, because the same aggression met resting asks that held and
/// refilled — the buyers spent and did not move it. Two tools that disagree at the same price
/// are carrying different information; two tools that agree everywhere were one tool.
///
/// IT IS A MEASURED NULL AND IS BUILT ANYWAY, BY DECISION. Trial 008: 25,745 episodes over 6
/// sessions, +1.006 ticks against matched-random, failing Bonferroni AND below the 2.76-tick
/// cost floor even if it were real. Nothing here overturns that. It computes what it says it
/// computes; whether it is worth acting on is settled elsewhere and the answer so far is no.
/// </summary>
public static class BookAbsorptionScan
{
    /// <summary>
    /// Levels for the sides that are absorbing, or nothing.
    /// </summary>
    /// <param name="snapshot">Absorption at each touch, from <see cref="AbsorptionEngine.Read"/>.</param>
    /// <param name="barOpenUtc">
    /// The bar the episode is attributed to. It is part of the level's identity, so one episode
    /// re-read on later folds inside the same bar stays ONE level rather than accumulating a new
    /// line four times a second.
    /// </param>
    /// <param name="settings">The volume floor.</param>
    public static IReadOnlyList<FlowLevel> Scan(
        AbsorptionSnapshot snapshot, DateTime barOpenUtc, in BookAbsorptionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        settings.Validate();

        // A book nobody could read says nothing. Reporting "no absorption" from an unknown book
        // would be a finding manufactured from an absence of evidence.
        if (!snapshot.BookKnown)
            return Array.Empty<FlowLevel>();

        var found = new List<FlowLevel>(2);

        Collect(snapshot.Bid, barOpenUtc, settings, found);
        Collect(snapshot.Ask, barOpenUtc, settings, found);

        return found;
    }

    private static void Collect(
        in AbsorptionReading reading,
        DateTime barOpenUtc,
        in BookAbsorptionSettings settings,
        List<FlowLevel> into)
    {
        // IsAbsorbing is Absorbed OR Replenished. Replenished is the STRONGER case and has no
        // ratio at all -- the size did not fall, so the denominator is zero or negative and the
        // components carry the claim. Requiring a ratio here would silently drop it.
        if (!reading.IsAbsorbing || reading.AbsorbedVolume < settings.MinVolume)
            return;

        // Resting BIDS held while sellers hit them: the buyers defended, so bullish. Asks
        // holding under buying is the mirror. This is the inverse of the footprint reading at
        // the same price, which is what makes the two tools worth drawing together.
        var side = reading.Side == BookSide.Bid ? LevelSide.Bullish : LevelSide.Bearish;

        into.Add(new FlowLevel(
            string.Create(
                CultureInfo.InvariantCulture,
                $"babs:{barOpenUtc:O}:{reading.Side}:{reading.Price}"),
            LevelFeature.Absorption,
            side,
            reading.Price,
            reading.Price,
            reading.Price,
            barOpenUtc,
            Label(reading),
            reading.AbsorbedVolume));
    }

    private static string Label(in AbsorptionReading reading)
    {
        var what = reading.State == AbsorptionState.Replenished ? "refilled" : "absorbed";

        // The ratio is stated when there is one and omitted when there is not, rather than
        // printed as a zero that would read as "no absorption at all" on the strongest case.
        return reading.Ratio is { } ratio
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(reading.Side == BookSide.Bid ? "Bid" : "Ask")} {what} {reading.AbsorbedVolume:N0} ×{ratio:N1}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{(reading.Side == BookSide.Bid ? "Bid" : "Ask")} {what} {reading.AbsorbedVolume:N0}, size held");
    }
}
