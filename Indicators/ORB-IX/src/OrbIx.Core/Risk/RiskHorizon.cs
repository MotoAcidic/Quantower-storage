using System;

namespace OrbIx.Core.Risk;

/// <summary>
/// Where the account's loss limits sit IN PRICE for the position being
/// held — the number that was being computed in a chat at 11 PM on
/// 2026-08-28 while the user weighed adding 10 micros.
/// </summary>
/// <param name="DllPrice">The price at which unrealized loss equals the remaining daily loss limit.</param>
/// <param name="MllPrice">Same for the remaining trailing max-loss headroom; null when that figure was not supplied.</param>
/// <param name="NetContracts">Signed position size the levels were computed for.</param>
/// <param name="AverageEntry">The entry the distance is measured from.</param>
/// <param name="Assumption">
/// What the remaining-limit figures assumed, stated verbatim on the chart
/// (e.g. "assumes $0 realized today") — an assumption shown is a caveat,
/// an assumption hidden is a lie.
/// </param>
public readonly record struct RiskHorizonReading(
    double DllPrice,
    double? MllPrice,
    double NetContracts,
    double AverageEntry,
    string Assumption);

/// <summary>
/// Pure arithmetic per pinned rule 3 of the master plan (approved
/// 2026-08-28): for a signed net position q at average entry E with
/// per-contract point value m, the limit L dollars away sits at
/// E − L/(m·|q|) for longs and E + L/(m·|q|) for shorts.
///
/// The account-limit MATH (trailing basis, peak tracking, stop-cost
/// share) belongs to AramidRisk and is deliberately not duplicated here
/// — this class turns already-known remaining-dollar figures into chart
/// prices, nothing more. Flat or unpriceable inputs yield null, never a
/// line at a fabricated price.
/// </summary>
public static class RiskHorizon
{
    /// <param name="averageEntry">Position average entry price.</param>
    /// <param name="netContracts">Signed contracts: positive long, negative short.</param>
    /// <param name="dollarsPerPointPerContract">
    /// Point value per contract (tick value / tick size), from the
    /// instrument spec — never a remembered constant.
    /// </param>
    /// <param name="dllRemainingUsd">Dollars left before the daily loss limit.</param>
    /// <param name="mllRemainingUsd">Dollars left before the trailing max loss, when known.</param>
    /// <param name="assumption">The caveat the chart must display with the line.</param>
    public static RiskHorizonReading? Compute(
        double averageEntry,
        double netContracts,
        double dollarsPerPointPerContract,
        double dllRemainingUsd,
        double? mllRemainingUsd,
        string assumption)
    {
        // Both lines come from Level, so the validation lives in ONE place. An earlier draft
        // repeated the checks here and then asserted, with a throw, that Level could not
        // refuse what this had already accepted — two copies of one rule, and a throw whose
        // correctness depended on them never drifting apart.
        if (Level(averageEntry, netContracts, dollarsPerPointPerContract, dllRemainingUsd)
            is not { } dllPrice)
        {
            return null;
        }

        return new RiskHorizonReading(
            dllPrice,
            Level(averageEntry, netContracts, dollarsPerPointPerContract, mllRemainingUsd),
            netContracts, averageEntry, assumption);
    }

    /// <summary>
    /// Where ONE limit sits in price, or null when it cannot be placed.
    ///
    /// SPLIT OUT SO THE TWO LINES FAIL SEPARATELY. <see cref="Compute"/> answers both at once
    /// and refuses both together, which was harmless while the daily figure was a constant.
    /// It stopped being harmless once the daily allowance started shrinking with the day: a
    /// loss large enough to exhaust it makes the daily line unplaceable — correctly, since
    /// the limit is already reached rather than lying somewhere ahead — and under the joint
    /// refusal that ALSO erased the max-loss line, which is still perfectly placeable and is
    /// the more important of the two at exactly that moment.
    /// </summary>
    /// <param name="averageEntry">Position average entry price.</param>
    /// <param name="netContracts">Signed contracts: positive long, negative short.</param>
    /// <param name="dollarsPerPointPerContract">Point value per contract.</param>
    /// <param name="remainingUsd">
    /// Dollars left before this limit. Null, non-finite or non-positive yields null: a
    /// non-positive remainder means the limit is already reached, and there is no price ahead
    /// at which that becomes true.
    /// </param>
    public static double? Level(
        double averageEntry,
        double netContracts,
        double dollarsPerPointPerContract,
        double? remainingUsd)
    {
        if (remainingUsd is not { } remaining
            || !double.IsFinite(averageEntry) || !double.IsFinite(netContracts)
            || netContracts == 0
            || !double.IsFinite(dollarsPerPointPerContract)
            || dollarsPerPointPerContract <= 0
            || !double.IsFinite(remaining) || remaining <= 0)
        {
            return null;
        }

        double perPoint = dollarsPerPointPerContract * Math.Abs(netContracts);
        double direction = netContracts > 0 ? -1.0 : 1.0;

        return averageEntry + (direction * remaining / perPoint);
    }
}
