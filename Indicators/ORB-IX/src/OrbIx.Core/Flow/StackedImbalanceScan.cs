using System;
using System.Collections.Generic;
using System.Globalization;
using OrbIx.Core.Features;

namespace OrbIx.Core.Flow;

/// <summary>
/// The ATAS imbalance parameters, named as the platform names them.
/// </summary>
/// <param name="Ratio">
/// ATAS "Imbalance Ratio" / "Imbalance Rate (%)" as a multiple: 3.0 is 300%. The
/// footprint-settings article's worked example is the definition — "if the ask volume equals 30
/// and the imbalance threshold is set to 350%, then the corresponding bid volume must exceed
/// 105".
/// </param>
/// <param name="MinVolume">
/// ATAS "Imbalance Volume" / "Volume filter": volume across the diagonal below which the row is
/// left unjudged rather than called balanced.
/// </param>
/// <param name="IgnoreZero">
/// ATAS "Ignore zero values". See <see cref="ImbalanceRule.Evaluate"/> — a ratio against an
/// empty opposing side is undefined, and this picks which of the two defensible answers is
/// wanted.
/// </param>
/// <param name="MinLevels">
/// ATAS "Imbalance Range": consecutive imbalanced rows required before the run is a stack.
/// </param>
public readonly record struct ImbalanceSettings(
    double Ratio, double MinVolume, bool IgnoreZero, int MinLevels)
{
    public void Validate()
    {
        if (!(this.Ratio > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.Ratio), this.Ratio,
                "A ratio at or below 1 marks the larger side of every diagonal as imbalanced.");
        }

        if (!(this.MinVolume > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.MinVolume), this.MinVolume,
                "A minimum of zero judges diagonals nobody traded on.");
        }

        if (this.MinLevels < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.MinLevels), this.MinLevels, "A stack needs at least one level.");
        }
    }
}

/// <summary>
/// Runs of consecutive imbalanced diagonals in one bar, turned into levels.
///
/// THE RULE IS ORB-IX'S, NOT A SECOND COPY OF IT. This scan arrived with its own
/// <c>DiagonalImbalance</c>, written — by its own commit message — to match
/// <see cref="ImbalanceRule"/>. Two implementations of one rule is two chances for the chart to
/// mark a stack the gate never saw, so the copy was deleted and this calls the rule ORB-IX
/// already tests. The two run-builders were compared line by line first: both require TICK
/// contiguity rather than mere adjacency in a list, both end a run on the first balanced row,
/// and both close the open run at the end. Only their return shapes differed.
///
/// WHERE THE LINE SITS IS CARRIED OVER UNCHANGED, NOT DECIDED HERE. ATAS does not state it, so
/// Aramid Flow made the call and recorded it under "Stated choices and unresolved items" in its
/// README: the bullish line is the stack's LOWEST row — the first price a retest from above
/// reaches — the bearish line its highest, and the whole run is shaded. That is what has been
/// drawing on both charts, so reproducing it is preserving behaviour; changing it would be the
/// decision, and would need asking first.
/// </summary>
public static class StackedImbalanceScan
{
    public static IReadOnlyList<FlowLevel> Scan(
        FootprintBar bar, ImbalanceSettings settings, LevelFeature feature)
    {
        ArgumentNullException.ThrowIfNull(bar);
        settings.Validate();

        if (feature is not (LevelFeature.StackedImbalance or LevelFeature.Absorption))
        {
            throw new ArgumentOutOfRangeException(
                nameof(feature), feature,
                "This scan produces stacked-imbalance and absorption levels only.");
        }

        // ATAS SEMANTICS, EXPLICITLY. This scan reproduces the ATAS toolkit, so it asks for the
        // reading ATAS publishes — strict ratio, volume floor against the dominant cell — rather
        // than inheriting ORB-IX's, which is the default everywhere else and stays that way.
        //
        // The floor choice is the one that matters: across 37 sessions the two readings differ by
        // roughly 2x in stacks drawn. See docs/IMBALANCE-VOLUME-FLOOR.md.
        var levels = ImbalanceRule.Evaluate(
            bar.ToPriceCells(), bar.TickSize, settings.Ratio, settings.MinVolume,
            ImbalanceSemantics.Atas with { IgnoreZeroOpposing = settings.IgnoreZero });

        if (levels.Count == 0)
            return Array.Empty<FlowLevel>();

        var found = new List<FlowLevel>();

        // ATAS "Ask" is volume the BUYER aggressed into, which is ORB-IX's buy side, and a stack
        // of it argues bullish. Bid mirrors it exactly.
        Collect(bar, levels, settings, feature, buySide: true, LevelSide.Bullish, found);
        Collect(bar, levels, settings, feature, buySide: false, LevelSide.Bearish, found);

        return found;
    }

    private static void Collect(
        FootprintBar bar,
        IReadOnlyList<ImbalanceLevel> levels,
        in ImbalanceSettings settings,
        LevelFeature feature,
        bool buySide,
        LevelSide side,
        List<FlowLevel> into)
    {
        foreach (var run in ImbalanceRule.Runs(levels, bar.TickSize, buySide))
        {
            if (!run.IsStacked(settings.MinLevels))
                continue;

            // The levels list is built from a SortedDictionary keyed by tick, so it ascends in
            // price, and a run is tick-contiguous. The run's first entry is therefore its lowest
            // price and its last its highest — the property the line placement below depends on.
            var low = levels[run.StartIndex].Price;
            var high = levels[run.EndIndex - 1].Price;

            var dominant = 0d;

            for (var i = run.StartIndex; i < run.EndIndex; i++)
            {
                dominant += buySide
                    ? levels[i].BuySide.Dominant
                    : levels[i].SellSide.Dominant;
            }

            into.Add(ToLevel(bar, feature, side, low, high, run.Length, dominant));
        }
    }

    private static FlowLevel ToLevel(
        FootprintBar bar, LevelFeature feature, LevelSide side,
        double low, double high, int levels, double dominant)
    {
        var line = side == LevelSide.Bullish ? low : high;
        var prefix = feature == LevelFeature.Absorption ? "abs" : "si";
        var kind = side == LevelSide.Bullish ? "ask" : "bid";

        // The identity is keyed on the bar, the side and the LOW of the run, so the same stack
        // re-scanned on a later fold is the same level rather than a second one beside it.
        return new FlowLevel(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}:{bar.OpenUtc:O}:{kind}:{bar.IndexOf(low)}"),
            feature,
            side,
            line,
            low,
            high,
            bar.OpenUtc,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{(side == LevelSide.Bullish ? "Ask" : "Bid")} ×{levels} {dominant:N0}"),
            levels);
    }
}
