namespace OrbIx.Core.Diagnostics;

/// <summary>
/// What the chart says while the currency lines are waiting for a tick cost.
///
/// WHY THIS EXISTS. Start-up no longer blocks on the tick cost: the price grid alone is
/// enough to draw everything that measures in ticks, and on 2026-08-31 demanding both cost
/// the operator 178 seconds of blank chart. But the two features that genuinely convert
/// ticks to money — the risk horizon and the fee half of the cost meter — still cannot be
/// computed, and a number that is simply absent is indistinguishable from a broken one.
///
/// So the absence is stated. The operator's instruction was explicit: absence must never be
/// mistakable for a bug.
///
/// IT SAYS NOTHING WHEN THERE IS NOTHING TO SAY. Both features are independently switchable,
/// and a line about a feature that is switched off is the wallpaper this status block exists
/// to avoid — see the note on <c>PublishStatus</c>. An empty string is the healthy result and
/// the common one.
///
/// Lives in Core with no platform reference (§11), so the wording is asserted offline rather
/// than read off a screenshot.
/// </summary>
public static class PendingCostNotice
{
    /// <summary>
    /// The problems-line text, or empty when the chart should stay quiet.
    ///
    /// Empty in three separate cases, which are not the same thing: the cost has arrived and
    /// nothing is pending; neither currency feature is switched on, so nothing is being
    /// withheld; or both.
    /// </summary>
    /// <param name="hasTickCost">True once the platform has priced a tick.</param>
    /// <param name="riskLinesEnabled">Whether the risk-horizon lines are switched on.</param>
    /// <param name="costMeterEnabled">Whether the cost meter is switched on.</param>
    public static string ChartText(bool hasTickCost, bool riskLinesEnabled, bool costMeterEnabled)
    {
        if (hasTickCost)
            return string.Empty;

        var affected = Describe(riskLinesEnabled, costMeterEnabled);

        return affected.Length == 0
            ? string.Empty
            : $"{affected}: waiting for tick cost — everything priced in ticks is already drawing";
    }

    /// <summary>
    /// The full wording for orbix-startup.log, which keeps the reason as well as the effect.
    ///
    /// The chart line is read at a glance and stays short; the log is read afterwards, often
    /// by someone reconstructing a start-up, and there the cause is the useful half.
    /// </summary>
    public static string LogText(bool hasTickCost, bool riskLinesEnabled, bool costMeterEnabled)
    {
        if (hasTickCost)
            return string.Empty;

        var affected = Describe(riskLinesEnabled, costMeterEnabled);

        if (affected.Length == 0)
            return string.Empty;

        return $"{affected} deferred: the platform has published a tick size but not yet a tick "
            + "cost, which needs a reference price. Nothing is assumed; the lines appear when "
            + "the first usable price arrives.";
    }

    /// <summary>
    /// Names the features actually being withheld, so the line is true rather than generic.
    ///
    /// A message that said "currency lines" while only the cost meter was on would send a
    /// reader looking for risk lines that were never switched on in the first place.
    /// </summary>
    private static string Describe(bool riskLinesEnabled, bool costMeterEnabled)
        => (riskLinesEnabled, costMeterEnabled) switch
        {
            (true, true) => "risk lines and fee estimate",
            (true, false) => "risk lines",
            (false, true) => "fee estimate",
            (false, false) => string.Empty,
        };
}
