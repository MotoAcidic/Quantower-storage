using System;
using System.Collections.Generic;

namespace OrbIx.Core.Risk;

/// <summary>
/// What a stop distance costs, and how many contracts a loss limit permits.
/// </summary>
/// <param name="StopTicks">The stop distance this advice was computed for.</param>
/// <param name="RiskPerLot">Dollars lost per contract if the stop is hit.</param>
/// <param name="MaxLots">
/// The largest whole number of contracts whose loss at <paramref name="StopTicks"/>
/// stays within the risk budget. Zero means even one contract exceeds it.
/// </param>
/// <param name="RiskAtMaxLots">What <paramref name="MaxLots"/> actually risks.</param>
public readonly record struct SizingAdvice(
    int StopTicks, double RiskPerLot, int MaxLots, double RiskAtMaxLots);

/// <summary>
/// Converts a loss limit and a stop distance into a contract count.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS, WITH THE NUMBERS THAT BOUGHT IT
///     On 2026-08-12 a 14-lot MNQ long was held with a stop 57 ticks from entry
///     against $1,500 of remaining loss limit. That is $399, or 26.6% of the
///     account's whole remaining life, on one trade. The measured median
///     5-minute range at that moment was 84 ticks, so the stop sat INSIDE
///     ordinary movement rather than outside it. It was hit.
///
///     Every number needed to see that was available before entry. None of it
///     was on the screen. This puts it there.
///
/// WHY THE TICK COST IS PASSED IN
///     Dollars per tick is not derivable from tick SIZE — they are different
///     quantities, and for instruments with a variable tick table the cost
///     depends on the price. The platform computes it via
///     Symbol.GetTickCost(price), established by reflecting the shipped
///     assembly. This type takes the answer rather than reproducing a
///     multiplier from memory, which is exactly the kind of recalled constant
///     the charter forbids.
///
/// ROUNDING IS DOWN, ALWAYS
///     3.6 permitted contracts means 3. Rounding to nearest would let 3.5
///     become 4 and quietly breach the limit this type exists to enforce.
///
/// PORTED INTO ORB-IX 2026-09-03. Logic unchanged; MedianRangeTicks now takes (High, Low)
/// pairs rather than the CRT Candle type, so it carries no dependency across.
///
/// IT WAS NEVER ON A CHART. Written 2026-08-12 for the CRT indicator, which is installed on
/// neither trading host (verified by direct check on ryzen-pc). The 14-lot trade described
/// above happened, and every number needed to see it was already computable -- by code that
/// then sat in an artefact nobody loads.
/// </remarks>
public static class PositionSizing
{
    /// <summary>
    /// The largest position whose stop-out stays inside the risk budget.
    /// </summary>
    /// <param name="lossLimit">Remaining loss limit in account currency.</param>
    /// <param name="riskFraction">
    /// Fraction of <paramref name="lossLimit"/> this trade may risk, as 0..1.
    /// </param>
    /// <param name="stopTicks">Stop distance in ticks. Must be positive.</param>
    /// <param name="tickCost">
    /// Account currency lost per tick per contract, from the platform.
    /// </param>
    /// <returns>
    /// The advice. <see cref="SizingAdvice.MaxLots"/> is 0 when even a single
    /// contract exceeds the budget — a real answer, and the one most worth
    /// seeing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If any input is non-finite, if <paramref name="stopTicks"/> is not
    /// positive, if <paramref name="tickCost"/> is not positive, or if
    /// <paramref name="riskFraction"/> is outside 0..1.
    /// </exception>
    /// <remarks>
    /// A non-positive loss limit is NOT an error: an account with nothing left
    /// to lose is a real state, and the honest answer for it is zero contracts
    /// rather than an exception the caller must special-case.
    /// </remarks>
    public static SizingAdvice Evaluate(
        double lossLimit, double riskFraction, int stopTicks, double tickCost)
    {
        if (!double.IsFinite(lossLimit))
            throw new ArgumentOutOfRangeException(nameof(lossLimit), lossLimit,
                "A non-finite loss limit cannot bound a position.");

        if (!double.IsFinite(riskFraction) || riskFraction < 0 || riskFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(riskFraction), riskFraction,
                "The risk fraction is a share of the loss limit, so it lies in 0..1.");

        if (stopTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(stopTicks), stopTicks,
                "A stop at zero ticks has no distance to lose over, and a negative "
                + "one is not a stop. Distance is unsigned here.");

        if (!double.IsFinite(tickCost) || tickCost <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickCost), tickCost,
                "Dollars per tick must be a positive, known number; without it no "
                + "position size can be computed rather than guessed.");

        double riskPerLot = stopTicks * tickCost;
        double budget = Math.Max(0, lossLimit) * riskFraction;

        // Floor, never round. See the type remarks.
        int maxLots = (int)Math.Floor(budget / riskPerLot);

        return new SizingAdvice(stopTicks, riskPerLot, maxLots, maxLots * riskPerLot);
    }

    /// <summary>
    /// The median bar range, in ticks, over the most recent bars.
    /// </summary>
    /// <param name="bars">Bars to measure, oldest first.</param>
    /// <param name="tickSize">The instrument's price increment.</param>
    /// <param name="lookback">How many of the most recent bars to use.</param>
    /// <returns>
    /// The median high-to-low range in ticks, or NaN when there are no bars to
    /// measure. NaN means UNKNOWN and must not be rendered as zero: a stop
    /// compared against a zero noise floor would look generously wide.
    /// </returns>
    /// <exception cref="ArgumentNullException">If <paramref name="bars"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="tickSize"/> is not positive, or <paramref name="lookback"/>
    /// is not positive.
    /// </exception>
    /// <remarks>
    /// THE MEDIAN, NOT THE MEAN
    ///     One violent bar drags a mean far above what a typical bar does, and a
    ///     stop sized against that inflated figure is wider — and more
    ///     expensive — than the market warrants. The same reasoning already
    ///     governs the volume-ratio reading in the CRT work this came from.
    ///
    /// RANGE IS NOT ADVERSE EXCURSION
    ///     This measures how far a bar travels, not how far it travels AGAINST
    ///     a position opened inside it. A bar with an 84-tick range may have
    ///     gone entirely in one direction. It is a floor for "how much movement
    ///     is ordinary here", which is the question a stop distance has to
    ///     answer, and it is deliberately not presented as a probability.
    /// </remarks>
    public static double MedianRangeTicks(
        IReadOnlyList<(double High, double Low)> bars, double tickSize, int lookback)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (!double.IsFinite(tickSize) || tickSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize,
                "Ticks cannot be counted against a non-positive increment.");

        if (lookback <= 0)
            throw new ArgumentOutOfRangeException(nameof(lookback), lookback,
                "A lookback of zero bars measures nothing.");

        int take = Math.Min(lookback, bars.Count);

        if (take == 0)
            return double.NaN;

        var ranges = new List<double>(take);

        for (int i = bars.Count - take; i < bars.Count; i++)
        {
            (double high, double low) = bars[i];

            if (!double.IsFinite(high) || !double.IsFinite(low) || high < low)
            {
                throw new ArgumentException(
                    $"Bar {i} has an unusable range (high {high}, low {low}). A bar the "
                    + "platform will not describe must not be averaged into a noise floor "
                    + "that a stop distance is then judged against.",
                    nameof(bars));
            }

            ranges.Add((high - low) / tickSize);
        }

        ranges.Sort();

        int mid = ranges.Count / 2;

        return ranges.Count % 2 == 1
            ? ranges[mid]
            : (ranges[mid - 1] + ranges[mid]) / 2.0;
    }
}
