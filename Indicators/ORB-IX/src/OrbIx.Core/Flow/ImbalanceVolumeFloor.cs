using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OrbIx.Core.Flow;

/// <summary>
/// The volume floor a stacked-imbalance scan should use on a given bar period, and whether that
/// number was measured for this period or borrowed from a neighbouring one.
/// </summary>
/// <param name="MinVolume">Contracts the dominant cell must carry.</param>
/// <param name="MeasuredPeriod">The period the number was measured on.</param>
/// <param name="MarksPerSession">
/// Stacks per RTH session this setting produced in the study, under the ATAS volume-filter
/// semantics. Carried so the panel can say what the setting BUYS rather than only what it is.
/// </param>
/// <param name="Borrowed">
/// True when <see cref="MeasuredPeriod"/> is not the period asked for. The number is then a
/// neighbour's, and the rate above is that neighbour's rate, not a prediction for this chart.
/// </param>
public readonly record struct VolumeFloor(
    double MinVolume, TimeSpan MeasuredPeriod, double MarksPerSession, bool Borrowed)
{
    public string Explain() => this.Borrowed
        ? string.Format(
            CultureInfo.InvariantCulture,
            "min volume {0:N0}, borrowed from the {1:N0}-minute measurement ({2:N1}/session there)",
            this.MinVolume, this.MeasuredPeriod.TotalMinutes, this.MarksPerSession)
        : string.Format(
            CultureInfo.InvariantCulture,
            "min volume {0:N0}, measured on {1:N0}-minute bars ({2:N1}/session)",
            this.MinVolume, this.MeasuredPeriod.TotalMinutes, this.MarksPerSession);
}

/// <summary>
/// Where the stacked-imbalance volume floor comes from.
///
/// MEASURED, NOT INHERITED. Aramid Flow shipped a floor of 300, which is the presenter's number
/// from the source video. On MNQ that draws essentially NOTHING: across 37 clean sessions of
/// one data vendor tick history (2026-06-23 .. 2026-08-31, ~74 million prints) it produced one single
/// stack in three months, at every period tested. The reason is structural rather than a bad
/// guess — 300 sits above the 99th percentile of dominant-cell volume at every period, and a
/// stack needs THREE CONSECUTIVE levels to clear it.
///
///     dominant cell volume per diagonal, 37 sessions
///         1-minute    p50  19   p90  56   p99 105
///         5-minute    p50  44   p90 120   p99 213
///         15-minute   p50  76   p90 204   p99 354
///
/// THE FLOOR FALLS AS THE PERIOD RISES, WHICH LOOKS BACKWARDS AND IS NOT. Longer bars carry more
/// volume per level, so a fixed rate of marks would suggest a HIGHER floor. But longer bars are
/// also far fewer, and the count of marks per session is what a reader actually experiences. Held
/// to a roughly constant rate, the floor has to come down as the bars get longer.
///
/// These three were chosen by the operator from the measured sweep and then verified to deliver
/// what they promise:
///
///         period   floor   stacks/session (measured)
///         1m         45        7.2
///         5m         40        7.9
///         15m        30        9.2
///
/// A NARROW SAMPLE WOULD HAVE SET THESE ~40% TOO HIGH. The same sweep over five late-August
/// sessions put the 1-minute rate at 9.6/session where 37 sessions say 4.5 at the same floor:
/// that week was busier than the June-August norm. The wide sample is the one these come from.
///
/// WHAT THIS IS NOT. A frequency calibration, and nothing more. Absorption and imbalance are
/// MEASURED NULLS on this instrument (trial 008, 25,745 episodes, +1.006 ticks against
/// matched-random, failing Bonferroni and below the 2.76-tick cost floor). The floor decides how
/// often a display speaks, never whether what it says is worth acting on.
///
/// MNQ ONLY. Every number here is MNQU6. Another product has its own volume scale and would need
/// its own sweep; nothing here should be carried to one by analogy.
/// </summary>
public static class ImbalanceVolumeFloor
{
    /// <summary>The measured floors, by bar period.</summary>
    private static readonly (TimeSpan Period, double MinVolume, double MarksPerSession)[] Measured =
    {
        (TimeSpan.FromMinutes(1), 45d, 7.2d),
        (TimeSpan.FromMinutes(5), 40d, 7.9d),
        (TimeSpan.FromMinutes(15), 30d, 9.2d),
    };

    /// <summary>Periods the sweep actually covered.</summary>
    public static IReadOnlyList<TimeSpan> MeasuredPeriods
        => Measured.Select(m => m.Period).ToArray();

    /// <summary>
    /// The floor for a bar period.
    ///
    /// AN UNMEASURED PERIOD BORROWS ITS NEAREST NEIGHBOUR AND SAYS SO. It does not interpolate:
    /// three points do not establish the shape of the curve between them, and a number produced
    /// by fitting one would be an invention wearing the authority of the measurement. Borrowing
    /// is visibly approximate, which is the honest failure mode — <see cref="VolumeFloor.Borrowed"/>
    /// is carried to the status line so a reader on a 3-minute chart knows the floor was chosen
    /// for a 1-minute one.
    ///
    /// Nearest is by RATIO, not by difference. Periods are multiplicative — 30 minutes is as far
    /// from 15 as 15 is from 7.5 — and a linear nearest would send every long period to the 15
    /// minute row by default.
    /// </summary>
    public static VolumeFloor For(TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period), period, "A bar period must be positive.");
        }

        var best = Measured[0];
        var bestDistance = double.MaxValue;
        var exact = false;

        foreach (var candidate in Measured)
        {
            if (candidate.Period == period)
            {
                best = candidate;
                exact = true;
                break;
            }

            var distance = Math.Abs(Math.Log(period.TotalMinutes / candidate.Period.TotalMinutes));

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return new VolumeFloor(best.MinVolume, best.Period, best.MarksPerSession, !exact);
    }
}
