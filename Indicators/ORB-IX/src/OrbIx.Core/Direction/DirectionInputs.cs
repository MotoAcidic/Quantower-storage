using System;
using System.Globalization;

namespace OrbIx.Core.Direction;

/// <summary>
/// Turns the non-structural measurements into votes.
/// </summary>
/// <remarks>
/// EACH READS ONE NUMBER AND SAYS WHAT IT SAYS. No smoothing, no thresholds
/// beyond the flat bands below, and no combining — combining is
/// <see cref="DirectionRead"/>'s job and it does it by counting.
///
/// THE FLAT BANDS ARE OPERATOR INPUTS WITH STATED DEFAULTS, NOT FITTED CONSTANTS.
///     Price is never exactly on VWAP and cumulative delta is never exactly zero,
///     so without a band both would vote on the last tick of noise. Where the band
///     belongs is a trading judgement; nothing here has measured it, and the value
///     is passed in rather than baked so that changing it is visible.
/// </remarks>
public static class DirectionInputs
{
    /// <summary>
    /// Price against VWAP.
    /// </summary>
    /// <param name="price">Last traded price.</param>
    /// <param name="vwap">Session VWAP, or <see cref="double.NaN"/> when unanchored.</param>
    /// <param name="tickSize">The instrument's price increment.</param>
    /// <param name="flatTicks">
    /// Half-width of the band, in ticks, inside which price counts as ON vwap rather
    /// than above or below.
    /// </param>
    public static DirectionVote Location(
        double price, double vwap, double tickSize, double flatTicks)
    {
        if (!double.IsFinite(vwap))
            return DirectionVote.NotAvailable("location", "no VWAP yet");

        if (!double.IsFinite(price))
            return DirectionVote.NotAvailable("location", "no price");

        if (!double.IsFinite(tickSize) || tickSize <= 0)
            return DirectionVote.NotAvailable("location", "no tick size");

        double distance = price - vwap;
        double band = Math.Abs(flatTicks) * tickSize;
        double ticks = distance / tickSize;

        if (Math.Abs(distance) <= band)
        {
            return new DirectionVote(
                "location", DirectionState.Undecided,
                string.Create(CultureInfo.InvariantCulture, $"on VWAP ({ticks:+0.0;-0.0;0}t)"));
        }

        return new DirectionVote(
            "location",
            distance > 0 ? DirectionState.Up : DirectionState.Down,
            string.Create(CultureInfo.InvariantCulture,
                $"{(distance > 0 ? "above" : "below")} VWAP by {Math.Abs(ticks):0.0}t"));
    }

    /// <summary>
    /// Cumulative delta's sign.
    /// </summary>
    /// <param name="cumulativeDelta">Session cumulative delta, in contracts.</param>
    /// <param name="classified">
    /// How many prints carried a usable aggressor. Zero means the delta is a sum of
    /// nothing and must not be read as balance.
    /// </param>
    /// <param name="flatContracts">
    /// Magnitude inside which delta counts as flat rather than directional.
    /// </param>
    public static DirectionVote Flow(
        double cumulativeDelta, long classified, double flatContracts)
    {
        // A cumulative delta of zero over zero classified prints is not "balanced
        // flow", it is no flow information at all. Reporting it as Undecided would
        // put an active judgement where there is an absent one.
        if (classified <= 0)
            return DirectionVote.NotAvailable("flow", "no classified prints");

        if (!double.IsFinite(cumulativeDelta))
            return DirectionVote.NotAvailable("flow", "delta unavailable");

        string detail = string.Create(
            CultureInfo.InvariantCulture, $"CVD {cumulativeDelta:+#,0;-#,0;0}");

        if (Math.Abs(cumulativeDelta) <= Math.Abs(flatContracts))
            return new DirectionVote("flow", DirectionState.Undecided, detail + " (flat)");

        return new DirectionVote(
            "flow",
            cumulativeDelta > 0 ? DirectionState.Up : DirectionState.Down,
            detail);
    }

    /// <summary>
    /// The volatility regime, in words.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT A VOTE AND IT NEVER BECOMES ONE. Expansion and compression have
    /// no direction — a range expanding says nothing about which way — so letting it
    /// count toward an up-or-down verdict would be a category error. It is context,
    /// displayed beside the verdict and excluded from it.
    /// </remarks>
    /// <param name="todayRange">Today's high-to-low range so far, in price.</param>
    /// <param name="averageDailyRange">The average daily range, in price.</param>
    public static string Regime(double todayRange, double averageDailyRange)
    {
        if (!double.IsFinite(todayRange) || !double.IsFinite(averageDailyRange)
            || averageDailyRange <= 0)
        {
            return "unknown";
        }

        double share = todayRange / averageDailyRange;

        // The boundary is the average itself, which needs no fitting: at or above
        // its own average the day is running wide, below it narrow.
        string shape = share >= 1.0 ? "expanded" : "inside average";

        return string.Create(CultureInfo.InvariantCulture, $"{share:P0} of ADR, {shape}");
    }
}
