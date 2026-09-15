using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Features;

/// <summary>
/// What kind of reference a level is. The kind determines its base strength, because a
/// prior-day extreme and a round number are not the same sort of object even when they
/// share a price.
/// </summary>
public enum LevelKind
{
    PriorDayHigh,
    PriorDayLow,
    PriorDayClose,
    PriorWeekHigh,
    PriorWeekLow,
    OvernightHigh,
    OvernightLow,
    InitialBalanceHigh,
    InitialBalanceLow,
    /// <summary>
    /// The opening range's volume-weighted average price, frozen when the range closed.
    ///
    /// NAMED FOR WHAT IT IS. It was called SessionVwap, which described something this engine
    /// does not compute: OrBuilder accumulates notional and volume across the OPENING RANGE
    /// WINDOW only and stops at range close. A session VWAP moves all session; this is one
    /// number, fixed, and reading it as a live VWAP would be reading a different line entirely.
    /// </summary>
    OpeningRangeVwap,

    /// <summary>
    /// The heaviest-traded price of the opening range, frozen when the range closed.
    ///
    /// Was PointOfControl, which by convention means the session's — or the profile's — point
    /// of control. This one is scoped to the opening range, for the same reason as above.
    /// </summary>
    OpeningRangePoc,
    OpeningRangeHigh,
    OpeningRangeLow,
    OpeningRangeMid,
    RoundNumber,
}

/// <summary>One reference price.</summary>
/// <param name="Price">Where it sits.</param>
/// <param name="Kind">What it is.</param>
/// <param name="EstablishedUtc">When it was made. Drives age decay.</param>
/// <param name="Label">Human-readable name for the panel and the journal.</param>
public sealed record Level(double Price, LevelKind Kind, DateTime EstablishedUtc, string Label);

/// <summary>
/// Several levels close enough together to act as one.
///
/// This is the object the rest of the system reasons about. A price that is simultaneously
/// yesterday's high, the overnight high and a round number is one strong band, not three
/// weak lines, and treating it as three would triple-count the same evidence.
/// </summary>
/// <param name="Price">Strength-weighted centre of the band.</param>
/// <param name="Low">Lowest member price.</param>
/// <param name="High">Highest member price.</param>
/// <param name="Strength">Combined strength, after age decay.</param>
/// <param name="Members">What went into it, strongest first.</param>
public sealed record LevelCluster(
    double Price, double Low, double High, double Strength, IReadOnlyList<Level> Members)
{
    public double WidthTicks(double tickSize) => tickSize > 0 ? (this.High - this.Low) / tickSize : 0d;

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0:N2} strength {1:N2} from {2}",
        this.Price, this.Strength, string.Join(" + ", this.Members.Select(m => m.Label)));
}

/// <summary>
/// M03. A ranked confluence graph of reference prices.
///
/// Two properties make this a graph rather than a list. Strength: each kind carries a
/// configured weight, decayed by age, so the engine can tell a level price reliably reacts
/// to from one that merely exists. And clustering: levels within a configured distance
/// merge into a single band, so confluence counts once and is measured, rather than being
/// implied by lines happening to overlap on a chart.
///
/// Round numbers are generated on demand around the current price rather than stored,
/// because there are infinitely many of them and only the near ones matter.
/// </summary>
public sealed class LevelGraph : IFeatureModule
{
    private readonly LevelsConfig config;
    private readonly SymbolConfig symbolConfig;
    private readonly InstrumentSpec instrument;
    private readonly List<Level> levels = new();

    private double lastPrice = double.NaN;

    public LevelGraph(LevelsConfig config, SymbolConfig symbolConfig, InstrumentSpec instrument)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.symbolConfig = symbolConfig ?? throw new ArgumentNullException(nameof(symbolConfig));
        this.instrument = instrument;

        instrument.RequirePriceScale(nameof(instrument));

        if (symbolConfig.RoundNumberStep <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(symbolConfig), symbolConfig.RoundNumberStep, "The round-number step must be positive.");
        }
    }

    public string Id => "M03";

    /// <summary>Bars and traded volume. Levels need no quote or depth.</summary>
    public DataTier Requires => DataTier.T0;

    public bool Enabled { get; set; } = true;

    /// <summary>Explicitly recorded levels, excluding generated round numbers.</summary>
    public IReadOnlyList<Level> Levels => this.levels;

    /// <summary>
    /// Records a level. A level of the same kind replaces the previous one: there is only
    /// one prior-day high, and keeping the stale one would let yesterday's yesterday
    /// compete with yesterday.
    /// </summary>
    public void Add(Level level)
    {
        if (level is null)
            throw new ArgumentNullException(nameof(level));

        if (double.IsNaN(level.Price) || double.IsInfinity(level.Price))
        {
            throw new ArgumentException(
                $"Level '{level.Label}' has price {level.Price}, which is not a price.", nameof(level));
        }

        this.levels.RemoveAll(existing => existing.Kind == level.Kind);
        this.levels.Add(level);
    }

    /// <summary>Records every primitive an opening range contributes.</summary>
    /// <summary>
    /// Drops every level of the given kinds.
    ///
    /// THE ONE THING Add CANNOT DO: CLEAR WITHOUT REPLACING.
    ///
    /// An earlier version of this comment claimed levels would otherwise pile up. That was
    /// wrong, and a mutation proved it: Add already removes every existing level of the same
    /// kind, so recomputing a level replaces it whether this method exists or not.
    ///
    /// What Add cannot do is remove an answer when there is no new one. When an overnight
    /// window yields nothing — no bars in it yet, or a capture gap — yesterday's overnight high
    /// would otherwise survive into today wearing today's meaning. That is the stale-level case,
    /// and it is what this is for.
    /// </summary>
    public void RemoveKinds(params LevelKind[] kinds)
    {
        if (kinds is null)
            throw new ArgumentNullException(nameof(kinds));

        if (kinds.Length == 0)
            return;

        this.levels.RemoveAll(level => Array.IndexOf(kinds, level.Kind) >= 0);
    }

    public void AddFrom(OrSnapshot range)
    {
        if (range is null)
            throw new ArgumentNullException(nameof(range));

        // THE INITIAL BALANCE'S EXTREMES ARE NOT AN OPENING RANGE'S, and until this mapping
        // existed they were recorded as though they were. Add replaces by kind, so IB's high
        // and low — recorded as OpeningRangeHigh/Low — were destroyed by the next session's
        // range, while InitialBalanceHigh/Low were weighted at 0.75 and never constructed.
        //
        // Only the two extremes move. Mid, POC and VWAP of the IB window stay OpeningRange*:
        // there are no InitialBalanceMid/Poc/Vwap kinds, and inventing them here would put
        // three more unweighted kinds into the graph — the very defect above.
        var initialBalance = this.IsInitialBalance(range.SessionName);

        var highKind = initialBalance ? LevelKind.InitialBalanceHigh : LevelKind.OpeningRangeHigh;
        var lowKind = initialBalance ? LevelKind.InitialBalanceLow : LevelKind.OpeningRangeLow;

        this.Add(new Level(range.Orh, highKind, range.CloseUtc, $"{range.SessionName} ORH"));
        this.Add(new Level(range.Orl, lowKind, range.CloseUtc, $"{range.SessionName} ORL"));
        this.Add(new Level(range.Orm, LevelKind.OpeningRangeMid, range.CloseUtc, $"{range.SessionName} ORM"));
        this.Add(new Level(range.Poc, LevelKind.OpeningRangePoc, range.CloseUtc, $"{range.SessionName} OR POC"));
        this.Add(new Level(range.Vwap, LevelKind.OpeningRangeVwap, range.CloseUtc, $"{range.SessionName} OR VWAP"));
    }

    /// <summary>
    /// Whether a closed range is the configured initial balance.
    ///
    /// Compared case-insensitively, matching how the loader resolves the same name against the
    /// session list — so a name the loader accepted is a name this recognises.
    /// </summary>
    private bool IsInitialBalance(string sessionName)
        => !string.IsNullOrWhiteSpace(this.config.InitialBalanceSession)
           && string.Equals(
                  sessionName, this.config.InitialBalanceSession, StringComparison.OrdinalIgnoreCase);

    public void OnTick(in TickEvent tick)
    {
        if (!double.IsNaN(tick.Price))
            this.lastPrice = tick.Price;
    }

    public void OnBar(in BarEvent bar, TimeFrame timeFrame)
    {
        if (!double.IsNaN(bar.Close))
            this.lastPrice = bar.Close;
    }

    public void OnBook(in BookDelta delta)
    {
        // Levels are structural; resting size does not create or destroy one.
    }

    public void OnL3(in L3Event l3)
    {
        // As above.
    }

    public void OnSessionPhase(SessionPhase phase)
    {
        // Levels outlive session phases by design; that is what makes them references.
    }

    /// <summary>
    /// Strength of one level at an instant, after age decay.
    ///
    /// Exponential half-life rather than a cliff: a level does not stop mattering at a
    /// particular hour, it fades. A level with no configured strength contributes nothing
    /// rather than defaulting to something, because an unconfigured kind is an unanswered
    /// question about how much it should count.
    /// </summary>
    public double StrengthOf(Level level, DateTime asOfUtc)
    {
        if (level is null)
            throw new ArgumentNullException(nameof(level));

        if (!this.config.KindStrength.TryGetValue(level.Kind.ToString(), out var baseStrength))
            return 0d;

        var ageHours = (asOfUtc - level.EstablishedUtc).TotalHours;

        if (ageHours <= 0)
            return baseStrength;

        return baseStrength * Math.Pow(0.5d, ageHours / this.config.AgeHalfLifeHours);
    }

    /// <summary>
    /// The level graph as bands, strongest first, capped at the configured maximum.
    ///
    /// <paramref name="aroundPrice"/> seeds round-number generation; pass NaN to use the
    /// last observed price, and no round numbers are generated when neither is known.
    /// </summary>
    public IReadOnlyList<LevelCluster> Clusters(DateTime asOfUtc, double aroundPrice = double.NaN)
    {
        var anchor = double.IsNaN(aroundPrice) ? this.lastPrice : aroundPrice;

        var candidates = this.levels
            .Concat(this.RoundNumbersAround(anchor, asOfUtc))
            .Select(level => (Level: level, Strength: this.StrengthOf(level, asOfUtc)))
            .Where(x => x.Strength > 0)
            .OrderBy(x => x.Level.Price)
            .ToList();

        if (candidates.Count == 0)
            return Array.Empty<LevelCluster>();

        var tolerance = this.config.ClusterToleranceTicks * this.instrument.TickSize;
        var clusters = new List<LevelCluster>();
        var current = new List<(Level Level, double Strength)> { candidates[0] };

        for (var i = 1; i < candidates.Count; i++)
        {
            // Compared against the band's lowest member, not its running centre, so a long
            // chain of near-misses cannot drift into one implausibly wide band.
            if (candidates[i].Level.Price - current[0].Level.Price <= tolerance)
            {
                current.Add(candidates[i]);
                continue;
            }

            clusters.Add(Build(current));
            current = new List<(Level, double)> { candidates[i] };
        }

        clusters.Add(Build(current));

        return clusters
            .OrderByDescending(c => c.Strength)
            .ThenBy(c => c.Price)
            .Take(this.config.MaxVisible)
            .ToList();
    }

    private static LevelCluster Build(IReadOnlyList<(Level Level, double Strength)> members)
    {
        var totalStrength = members.Sum(m => m.Strength);

        // Strength-weighted centre: a band containing yesterday's high and a round number
        // sits nearer the high, because that is the price participants are watching.
        var centre = totalStrength > 0
            ? members.Sum(m => m.Level.Price * m.Strength) / totalStrength
            : members.Average(m => m.Level.Price);

        return new LevelCluster(
            centre,
            members.Min(m => m.Level.Price),
            members.Max(m => m.Level.Price),
            totalStrength,
            members.OrderByDescending(m => m.Strength)
                   .ThenBy(m => m.Level.Kind.ToString(), StringComparer.Ordinal)
                   .Select(m => m.Level)
                   .ToList());
    }

    /// <summary>
    /// The nearest band to a price, or null when the graph is empty. Distance is measured
    /// to the band's centre.
    /// </summary>
    public LevelCluster? Nearest(double price, DateTime asOfUtc)
        => this.Clusters(asOfUtc, price)
            .OrderBy(c => Math.Abs(c.Price - price))
            .ThenByDescending(c => c.Strength)
            .FirstOrDefault();

    /// <summary>
    /// The strongest band lying strictly between two prices, or null when there is none.
    /// Used to move a target in front of a wall rather than behind it.
    /// </summary>
    public LevelCluster? StrongestBetween(double from, double to, DateTime asOfUtc)
    {
        var low = Math.Min(from, to);
        var high = Math.Max(from, to);

        return this.Clusters(asOfUtc, (low + high) / 2d)
            .Where(c => c.Price > low && c.Price < high)
            .OrderByDescending(c => c.Strength)
            .ThenBy(c => c.Price)
            .FirstOrDefault();
    }

    /// <summary>
    /// Round numbers near a price. Generated rather than stored: there are infinitely many
    /// and only the near ones are relevant. They are treated as established now, so age
    /// decay never erodes them — a round number is as round today as it was last year.
    /// </summary>
    private IEnumerable<Level> RoundNumbersAround(double price, DateTime asOfUtc)
    {
        if (double.IsNaN(price))
            yield break;

        var step = this.symbolConfig.RoundNumberStep;
        var centre = Math.Round(price / step, MidpointRounding.AwayFromZero) * step;

        for (var offset = -2; offset <= 2; offset++)
        {
            var value = centre + (offset * step);

            if (value <= 0)
                continue;

            yield return new Level(
                value, LevelKind.RoundNumber, asOfUtc,
                value.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    public FeatureOutput Fold(SessionContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        var clusters = this.Clusters(context.Window.OrHardCloseUtc);

        if (clusters.Count == 0 || double.IsNaN(this.lastPrice))
            return FeatureOutput.Silent(clusters);

        // Levels are context, not a direction: the graph does not argue for a side. It
        // reports how much structure sits nearby, which the scorer uses as a confluence
        // term rather than as a directional vote.
        var nearest = this.Nearest(this.lastPrice, context.Window.OrHardCloseUtc);

        if (nearest is null)
            return FeatureOutput.Silent(clusters);

        var distanceTicks = Math.Abs(nearest.Price - this.lastPrice) / this.instrument.TickSize;
        var proximity = 1d / (1d + (distanceTicks / Math.Max(this.config.ClusterToleranceTicks, 1)));

        return new FeatureOutput(0f, (float)Math.Clamp(proximity * nearest.Strength, 0d, 1d), clusters);
    }

    public void Reset()
    {
        this.levels.Clear();
        this.lastPrice = double.NaN;
    }
}
