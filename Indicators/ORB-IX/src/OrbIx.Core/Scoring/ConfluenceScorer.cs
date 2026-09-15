using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Scoring;

/// <summary>Which side a setup proposes.</summary>
public enum TradeDirection
{
    Long,
    Short,
}

/// <summary>The five scoring groups of §6.</summary>
public enum ScoreGroup
{
    Structure,
    OrderFlow,
    Book,
    Micro,
    Positioning,
}

/// <summary>
/// A module's place in the score: which group it belongs to and what share of that group's
/// points it can earn.
/// </summary>
public sealed record ScoreEntry
{
    /// <param name="module">The module itself.</param>
    /// <param name="group">Which group its points come from.</param>
    /// <param name="shareOfGroup">
    /// Its share within the group. Shares are normalised within a group, so they need not
    /// sum to one — only their ratio matters.
    /// </param>
    public ScoreEntry(IFeatureModule module, ScoreGroup group, double shareOfGroup)
    {
        if (shareOfGroup <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shareOfGroup), shareOfGroup,
                "A module with no share of its group earns nothing. Remove it rather than "
                + "registering it at zero, so the registration says what is true.");
        }

        this.Module = module ?? throw new ArgumentNullException(nameof(module));
        this.Group = group;
        this.ShareOfGroup = shareOfGroup;
    }

    public IFeatureModule Module { get; }

    public ScoreGroup Group { get; }

    public double ShareOfGroup { get; }
}

/// <summary>What one module actually contributed, and why.</summary>
/// <param name="ModuleId">Which module.</param>
/// <param name="Group">Its group.</param>
/// <param name="Available">Whether its data tier was present.</param>
/// <param name="RawScore">Its directional score, before alignment.</param>
/// <param name="Confidence">How much it trusted itself.</param>
/// <param name="MaxPoints">Points it could have earned.</param>
/// <param name="Points">Points it did earn.</param>
/// <param name="Detail">Its typed detail, for the panel and the journal.</param>
public sealed record ModuleContribution(
    string ModuleId,
    ScoreGroup Group,
    bool Available,
    double RawScore,
    double Confidence,
    double MaxPoints,
    double Points,
    object? Detail);

/// <summary>The fused score, with everything needed to explain it.</summary>
/// <param name="Direction">The side that was scored.</param>
/// <param name="Score">Total, on a 0-100 scale.</param>
/// <param name="Grade">Which band it falls in.</param>
/// <param name="Vetoed">Whether a hard veto blocks entry regardless of score.</param>
/// <param name="Vetoes">Every veto that fired.</param>
/// <param name="Contributions">Per-module breakdown, highest first.</param>
/// <param name="GroupPoints">Points earned per group.</param>
/// <param name="GroupCeilings">Points available per group after any redistribution.</param>
/// <param name="RedistributedFrom">Groups whose weight was redistributed because their tier was absent.</param>
public sealed record ConfluenceResult(
    TradeDirection Direction,
    double Score,
    SignalGrade Grade,
    bool Vetoed,
    IReadOnlyList<string> Vetoes,
    IReadOnlyList<ModuleContribution> Contributions,
    IReadOnlyDictionary<ScoreGroup, double> GroupPoints,
    IReadOnlyDictionary<ScoreGroup, double> GroupCeilings,
    IReadOnlyList<ScoreGroup> RedistributedFrom)
{
    /// <summary>
    /// Whether this setup may be taken: it reached at least the B band and nothing vetoed
    /// it. A C-grade result is logged, never traded.
    /// </summary>
    public bool Tradeable => !this.Vetoed && this.Grade != SignalGrade.C;

    public string Explain() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1:N1}/100 grade {2}{3}. {4}",
        this.Direction,
        this.Score,
        this.Grade,
        this.Vetoed ? " VETOED" : string.Empty,
        this.Vetoed
            ? string.Join("; ", this.Vetoes)
            : string.Join(", ", this.Contributions
                .Where(c => c.Points > 0)
                .Select(c => $"{c.ModuleId} {c.Points:N1}")));
}

/// <summary>
/// M15. Weighted aggregation with hard vetoes.
///
/// Three properties from §6, each deliberate.
///
/// Points are additive and never negative. A module that disagrees with the proposed
/// direction contributes nothing rather than subtracting, because the mechanism for
/// stopping a trade is a veto, not a low score. Letting a strong reading in one group talk
/// the total back up past a serious objection in another is exactly what a weighted average
/// does and exactly what must not happen here.
///
/// Vetoes are boolean and absolute. No score, however high, survives one. A ninety-point
/// signal on a stale feed is a ninety-point mistake.
///
/// Absent tiers redistribute rather than score zero. If the depth tier is missing, its
/// group's weight is shared proportionally among the groups that can still speak, so the
/// total stays on a 0-100 scale and the grade thresholds never need retuning per
/// environment. Scoring an absent module as zero would silently make every signal worse in
/// a degraded environment, which reads as "the market got worse" rather than "we can see
/// less".
/// </summary>
public sealed class ConfluenceScorer
{
    private readonly ScoringConfig config;

    public ConfluenceScorer(ScoringConfig config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Scores a proposed direction.
    /// </summary>
    /// <param name="direction">The side being proposed.</param>
    /// <param name="entries">Registered modules and their places in the score.</param>
    /// <param name="context">The session being scored.</param>
    /// <param name="vetoes">Hard vetoes already determined. Any one blocks entry.</param>
    public ConfluenceResult Score(
        TradeDirection direction,
        IReadOnlyList<ScoreEntry> entries,
        SessionContext context,
        IReadOnlyList<string> vetoes)
    {
        if (entries is null)
            throw new ArgumentNullException(nameof(entries));
        if (context is null)
            throw new ArgumentNullException(nameof(context));
        if (vetoes is null)
            throw new ArgumentNullException(nameof(vetoes));

        var baseWeights = this.BaseWeights();
        var live = entries
            .Where(e => e.Module.Enabled && context.Tiers.Has(e.Module.Requires))
            .ToList();

        var liveGroups = live.Select(e => e.Group).Distinct().ToHashSet();

        var absentGroups = baseWeights.Keys
            .Where(g => !liveGroups.Contains(g) && baseWeights[g] > 0)
            .OrderBy(g => g)
            .ToList();

        var ceilings = this.Redistribute(baseWeights, liveGroups, absentGroups);
        var contributions = new List<ModuleContribution>();
        var groupPoints = baseWeights.Keys.ToDictionary(g => g, _ => 0d);

        foreach (var group in liveGroups)
        {
            var members = live.Where(e => e.Group == group).ToList();
            var shareTotal = members.Sum(m => m.ShareOfGroup);

            foreach (var entry in members)
            {
                var output = entry.Module.Fold(context);
                var maxPoints = ceilings[group] * (entry.ShareOfGroup / shareTotal);

                // Alignment: how much this module argues for the proposed side. A module
                // arguing the other way contributes nothing; it does not subtract.
                var sign = direction == TradeDirection.Long ? 1d : -1d;
                var alignment = Math.Max(0d, output.Score * sign);
                var points = maxPoints * alignment * output.Confidence;

                groupPoints[group] += points;

                contributions.Add(new ModuleContribution(
                    entry.Module.Id, group, Available: true,
                    output.Score, output.Confidence, maxPoints, points, output.Detail));
            }
        }

        // Modules whose tier is absent are reported so the panel can show what is dark,
        // with zero available points rather than zero earned points — the distinction
        // between "said nothing" and "could not speak".
        foreach (var entry in entries.Except(live))
        {
            contributions.Add(new ModuleContribution(
                entry.Module.Id, entry.Group, Available: false,
                RawScore: 0d, Confidence: 0d, MaxPoints: 0d, Points: 0d, Detail: null));
        }

        var score = groupPoints.Values.Sum();

        return new ConfluenceResult(
            direction,
            score,
            this.GradeFor(score),
            vetoes.Count > 0,
            vetoes,
            contributions.OrderByDescending(c => c.Points)
                         .ThenBy(c => c.ModuleId, StringComparer.Ordinal)
                         .ToList(),
            groupPoints,
            ceilings,
            absentGroups);
    }

    private Dictionary<ScoreGroup, double> BaseWeights() => new()
    {
        [ScoreGroup.Structure] = this.config.Weights.Structure,
        [ScoreGroup.OrderFlow] = this.config.Weights.OrderFlow,
        [ScoreGroup.Book] = this.config.Weights.Book,
        [ScoreGroup.Micro] = this.config.Weights.Micro,
        [ScoreGroup.Positioning] = this.config.Weights.Positioning,
    };

    /// <summary>
    /// Shares the weight of absent groups across the surviving ones, in proportion to what
    /// they already carry.
    ///
    /// Disabling redistribution is a supported choice: it makes the score comparable across
    /// environments at the cost of being unable to reach the higher grades in a degraded
    /// one. What is not supported is silently scoring an absent module as zero, which looks
    /// like a weak signal rather than a partial view.
    /// </summary>
    private Dictionary<ScoreGroup, double> Redistribute(
        Dictionary<ScoreGroup, double> baseWeights,
        IReadOnlyCollection<ScoreGroup> liveGroups,
        IReadOnlyCollection<ScoreGroup> absentGroups)
    {
        var ceilings = baseWeights.ToDictionary(kv => kv.Key, kv => liveGroups.Contains(kv.Key) ? kv.Value : 0d);

        if (!this.config.RedistributeOnMissingTier || absentGroups.Count == 0)
            return ceilings;

        var surviving = ceilings.Where(kv => kv.Value > 0).ToList();

        if (surviving.Count == 0)
            return ceilings;

        var orphaned = absentGroups.Sum(g => baseWeights[g]);
        var survivingTotal = surviving.Sum(kv => kv.Value);

        foreach (var (group, weight) in surviving)
            ceilings[group] = weight + (orphaned * weight / survivingTotal);

        return ceilings;
    }

    private SignalGrade GradeFor(double score)
    {
        var t = this.config.GradeThresholds;

        if (score >= t.APlus) return SignalGrade.APlus;
        if (score >= t.A) return SignalGrade.A;
        return score >= t.B ? SignalGrade.B : SignalGrade.C;
    }

    /// <summary>
    /// Size multiplier for a grade, per §6: full size at A+, reduced at A, half at B, and
    /// nothing at C.
    /// </summary>
    public static double SizeMultiplierFor(SignalGrade grade) => grade switch
    {
        SignalGrade.APlus => 1.0d,
        SignalGrade.A => 0.8d,
        SignalGrade.B => 0.5d,
        SignalGrade.C => 0d,
        _ => throw new ArgumentOutOfRangeException(nameof(grade), grade, "Unknown grade."),
    };

    /// <summary>
    /// Whether a grade may arm the fourth target. §6 arms it at A+ only; below that the
    /// ladder stops at the third.
    /// </summary>
    public static bool ArmsFourthTarget(SignalGrade grade) => grade == SignalGrade.APlus;
}
