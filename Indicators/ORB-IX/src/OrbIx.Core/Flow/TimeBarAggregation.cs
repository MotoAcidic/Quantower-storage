using System;

namespace OrbIx.Core.Flow;

/// <summary>
/// Whether a chart's bars are TIME bars, decided by the platform aggregation's exact type name.
///
/// WHY THIS EXISTS, AND IT IS NOT A TIDINESS CONCERN. The stacked-imbalance volume floors were
/// measured on time bars, per period. Asking the platform "what is this chart's period?" gets an
/// answer on charts whose bars are not time bars at all, and the answer is the SOURCE period the
/// shapes are built from — so a Renko chart built from one-minute data reports one minute, and a
/// floor measured for one-minute bars would be applied to bricks whose volume distribution is
/// nothing like a minute's.
///
/// FOUR AGGREGATIONS INHERIT THE PERIOD-CARRYING TYPE WITHOUT BEING TIME BARS. Read from the
/// installed assembly with ilspycmd (lib/TradingPlatform.BusinessLayer.dll, 2026-09-12), not
/// recalled:
///
///     public sealed class HistoryAggregationRenko            : HistoryAggregationTime
///     public sealed class HistoryAggregationKagi             : HistoryAggregationTime
///     public sealed class HistoryAggregationLineBreak        : HistoryAggregationTime
///     public sealed class HistoryAggregationPointsAndFigures : HistoryAggregationTime
///
/// and Renko's constructor is <c>(Period period, HistoryType, int brickSize, ...)</c>, whose
/// <c>Period</c> its own code reads as the source — <c>base.Period.BasePeriod == BasePeriod.Tick</c>.
/// So an <c>is HistoryAggregationTime</c> test MATCHES on all four and hands back a period that
/// means something else. That was the defect this type was written to close.
///
/// THE EXACT TYPE, NOT AN INHERITANCE TEST, AND IT FAILS TOWARDS SILENCE. An aggregation this rule
/// does not recognise is treated as "not time bars", so an SDK that adds a fifth subclass produces
/// a display that says it cannot resolve its floor — visible and correctable — rather than one that
/// draws marks calibrated for a different chart. Heiken Ashi is refused for a different reason: its
/// bars ARE time buckets, but its period is reachable only through a member the vendor marks
/// <c>[NotPublished]</c>, and a threshold is not the place to depend on that.
///
/// A NAME RATHER THAN A TYPE so the rule lives in Core and is tested by CALLING it. Core carries no
/// reference to the platform assembly by design, and a rule that could only be exercised inside the
/// chart shell is a rule this suite could not check at all.
/// </summary>
public static class TimeBarAggregation
{
    /// <summary>The one aggregation whose bars are time bars.</summary>
    public const string TimeBars = "TradingPlatform.BusinessLayer.HistoryAggregationTime";

    /// <summary>
    /// True when bars of this aggregation are time bars, and a measured per-period floor therefore
    /// applies to them.
    /// </summary>
    /// <param name="aggregationTypeName">
    /// The full name of the aggregation's runtime type — <c>data.Aggregation.GetType().FullName</c>.
    /// Null or blank when there is no chart yet, which is not time bars either.
    /// </param>
    public static bool IsTimeBars(string? aggregationTypeName)
        => string.Equals(aggregationTypeName, TimeBars, StringComparison.Ordinal);

    /// <summary>
    /// The bar period to resolve a measured floor against: the period the platform reported, but
    /// only on a chart whose bars are time bars.
    ///
    /// Takes the period rather than reading it so Core stays free of platform types; the caller has
    /// already asked the platform, and this decides whether the answer means what the measurement
    /// needs it to mean.
    /// </summary>
    public static TimeSpan? MeasurablePeriod(string? aggregationTypeName, TimeSpan? reportedPeriod)
        => IsTimeBars(aggregationTypeName) && reportedPeriod is { } period && period > TimeSpan.Zero
            ? period
            : null;
}
