using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Config;

namespace OrbIx.Core.Scoring;

/// <summary>
/// How much of the confluence score actually has something behind it.
///
/// THE SCORER REDISTRIBUTES THE WEIGHT OF ANY GROUP THAT HAS NO LIVE MODULE, which is the right
/// behaviour and a dangerous one to leave unsaid. A chart reporting 80 out of 100 reads as
/// eighty points of agreement across five kinds of evidence; if two of the five had nothing in
/// them, it is eighty points of agreement across three, rescaled. The number looks identical
/// either way, and the rescaling is exactly the sort of thing that is obvious on the day it is
/// written and invisible a month later.
///
/// DERIVED FROM THE REGISTERED SET, never from a list of which modules exist. A hardcoded
/// "Book and Positioning are empty" would be a claim about the codebase written down in a
/// renderer, and it would go on being displayed after someone added a Book module. Counting
/// what was actually handed to the scorer cannot drift, because it IS the thing that decides
/// the score.
///
/// Pure, and in Core, so the offline suite asserts it rather than a person reading a chart.
/// </summary>
public static class ScoreCoverage
{
    /// <summary>
    /// One line describing the score's basis, for display beside it.
    /// </summary>
    /// <param name="entries">Exactly what was handed to the scorer.</param>
    /// <param name="weights">The configured group weights.</param>
    public static string Describe(IReadOnlyList<ScoreEntry> entries, ScoreWeightsConfig weights)
    {
        if (entries is null)
            throw new ArgumentNullException(nameof(entries));

        if (weights is null)
            throw new ArgumentNullException(nameof(weights));

        var byGroup = Weights(weights);
        var total = byGroup.Values.Sum();

        var backed = entries
            .Where(e => e is not null)
            .Select(e => e.Group)
            .Distinct()
            .ToHashSet();

        if (backed.Count == 0)
        {
            // NOT "0 of 5 backed", which invites the reader to imagine a score of zero. No
            // score is computed at all, and saying so is the whole point of the line.
            return "Confluence OFF - no module is registered into the score; geometry and vetoes only.";
        }

        var unbacked = byGroup
            .Where(kv => !backed.Contains(kv.Key) && kv.Value > 0)
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .ToList();

        if (unbacked.Count == 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Confluence ON - all {0} groups backed.",
                byGroup.Count);
        }

        var orphaned = unbacked.Sum(kv => kv.Value);

        return string.Format(
            CultureInfo.InvariantCulture,
            "Confluence ON - {0} of {1} groups backed; {2} unbacked ({3:N0} of {4:N0} pts redistributed).",
            backed.Count,
            byGroup.Count,
            string.Join(", ", unbacked.Select(kv => kv.Key.ToString())),
            orphaned,
            total);
    }

    /// <summary>
    /// The configured weight of every group.
    ///
    /// Enumerating <see cref="ScoreGroup"/> rather than listing the five by hand, so a group
    /// added to the enum cannot be silently omitted from the coverage report — which would
    /// make the report wrong in precisely the direction it exists to prevent.
    /// </summary>
    private static Dictionary<ScoreGroup, double> Weights(ScoreWeightsConfig weights)
        => Enum.GetValues<ScoreGroup>().ToDictionary(g => g, g => WeightOf(weights, g));

    private static double WeightOf(ScoreWeightsConfig weights, ScoreGroup group) => group switch
    {
        ScoreGroup.Structure => weights.Structure,
        ScoreGroup.OrderFlow => weights.OrderFlow,
        ScoreGroup.Book => weights.Book,
        ScoreGroup.Micro => weights.Micro,
        ScoreGroup.Positioning => weights.Positioning,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown score group."),
    };
}
