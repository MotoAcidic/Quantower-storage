using System;
using System.Collections.Generic;

namespace OrbIx.Core.Flow;

/// <summary>Which feature produced a level. Decides its colour and its lifecycle settings.</summary>
public enum LevelFeature
{
    Absorption,
    StackedImbalance,
    UnfinishedAuction,
    ClusterSearch,

    /// <summary>
    /// Heavy volume at one price while the bar went almost nowhere, at the MODERATE threshold.
    ///
    /// SEPARATE FROM <see cref="Absorption"/> ON PURPOSE. That one is the footprint diagonal
    /// scan, which was measured across 37 sessions to be the stacked-imbalance display in a
    /// second colour -- at floor 45, 0 of 267 marks differed. This is a different question:
    /// volume against DISPLACEMENT rather than against the opposing diagonal.
    /// </summary>
    VolumeAbsorptionModerate,

    /// <summary>The same reading at the HEAVY threshold, drawn brighter.</summary>
    VolumeAbsorptionHeavy,
}

/// <summary>
/// The direction a level speaks for. A bullish stacked imbalance is one "the bulls need to
/// come retest" (the presenter's words, 35:06); a bearish one is the mirror. An unfinished
/// auction at a bar LOW is carried as bullish and at a bar HIGH as bearish, which is only a
/// colour assignment — the ATAS article colours them "Low" and "High" and claims nothing
/// about direction.
/// </summary>
public enum LevelSide
{
    Bullish,
    Bearish,
}

/// <summary>
/// How long a level's line extends. ATAS Stacked Imbalance offers "Line till touch" and
/// "Print line for X bars" (article 72000602474); the presenter kept Cluster Search
/// levels on the chart indefinitely ("X marks the spot"), which is the third option.
/// </summary>
public enum LevelExtent
{
    UntilTouch,
    FixedBars,
    Forever,
}

/// <summary>Per-feature visibility rules.</summary>
/// <param name="Extent">How the line ends.</param>
/// <param name="PrintBars">Bars a <see cref="LevelExtent.FixedBars"/> line lasts.</param>
/// <param name="DaysLookBack">Levels created more than this many days ago are not shown. Zero shows all retained.</param>
public readonly record struct LevelVisibility(LevelExtent Extent, int PrintBars, int DaysLookBack);

/// <summary>
/// One level. Immutable except for the touch, which is the one fact about it that arrives
/// later than its creation.
/// </summary>
public sealed class FlowLevel
{
    public FlowLevel(
        string id, LevelFeature feature, LevelSide side,
        double price, double bandLow, double bandHigh,
        DateTime createdUtc, string label, double strength)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A level needs an identity so alerts and touches can refer to it.", nameof(id));

        if (!double.IsFinite(price) || !double.IsFinite(bandLow) || !double.IsFinite(bandHigh))
            throw new ArgumentException("Level prices must be finite.");

        if (bandHigh < bandLow)
            throw new ArgumentException("A level's band must be ordered low to high.");

        this.Id = id;
        this.Feature = feature;
        this.Side = side;
        this.Price = price;
        this.BandLow = bandLow;
        this.BandHigh = bandHigh;
        this.CreatedUtc = createdUtc;
        this.Label = label ?? string.Empty;
        this.Strength = strength;
    }

    public string Id { get; }

    public LevelFeature Feature { get; }

    public LevelSide Side { get; }

    /// <summary>The line price — the edge of the band the level is judged at.</summary>
    public double Price { get; }

    public double BandLow { get; }

    public double BandHigh { get; }

    /// <summary>Open instant of the bar that created the level.</summary>
    public DateTime CreatedUtc { get; }

    public string Label { get; }

    /// <summary>Feature-specific magnitude (volume for a cluster hit, levels for a stack).</summary>
    public double Strength { get; }

    /// <summary>Open instant of the bar that first traded through <see cref="Price"/>, or null.</summary>
    public DateTime? TouchedUtc { get; private set; }

    public bool IsTouched => this.TouchedUtc.HasValue;

    internal void MarkTouched(DateTime barOpenUtc) => this.TouchedUtc ??= barOpenUtc;
}

/// <summary>A level with the span it is drawn over. <see cref="EndUtc"/> null means "extend right".</summary>
public readonly record struct LevelSpan(FlowLevel Level, DateTime StartUtc, DateTime? EndUtc);

/// <summary>
/// Holds every level a feature has produced, records touches from closed bars, and
/// answers which are visible under a feature's lifecycle rules.
///
/// A TOUCH IS A CLOSED BAR WHOSE RANGE INCLUDES THE LINE PRICE, and only a bar that opened
/// AFTER the level was created — the bar that produced a level always contains it and
/// would otherwise touch every level the instant it was born. The forming bar is never
/// consulted: a forming bar's range can still change, so a touch read from it could be
/// un-touched a second later, and a level that flickers is worse than one that is late.
/// </summary>
public sealed class LevelBook
{
    private readonly Dictionary<string, FlowLevel> byId = new();
    private readonly List<FlowLevel> ordered = new();
    private readonly int maxLevels;

    public LevelBook(int maxLevels = 4000)
    {
        if (maxLevels < 1)
            throw new ArgumentOutOfRangeException(nameof(maxLevels), maxLevels, "At least one level must be retained.");

        this.maxLevels = maxLevels;
    }

    public IReadOnlyList<FlowLevel> All => this.ordered;

    public int Count => this.ordered.Count;

    /// <summary>Adds a level unless one with the same id already exists. Returns whether it was new.</summary>
    public bool Add(FlowLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);

        if (!this.byId.TryAdd(level.Id, level))
            return false;

        this.ordered.Add(level);

        while (this.ordered.Count > this.maxLevels)
        {
            var oldest = this.ordered[0];
            this.ordered.RemoveAt(0);
            this.byId.Remove(oldest.Id);
        }

        return true;
    }

    public bool Contains(string id) => this.byId.ContainsKey(id);

    /// <summary>Records touches from one closed bar. Returns the levels newly touched.</summary>
    public IReadOnlyList<FlowLevel> MarkTouches(FootprintBar closedBar)
    {
        ArgumentNullException.ThrowIfNull(closedBar);

        if (!closedBar.IsClosed)
            throw new ArgumentException("Touches are read from closed bars only.", nameof(closedBar));

        if (double.IsNaN(closedBar.High))
            return Array.Empty<FlowLevel>();

        var touched = new List<FlowLevel>();

        foreach (var level in this.ordered)
        {
            if (level.IsTouched || closedBar.OpenUtc <= level.CreatedUtc)
                continue;

            if (closedBar.Low <= level.Price && level.Price <= closedBar.High)
            {
                level.MarkTouched(closedBar.OpenUtc);
                touched.Add(level);
            }
        }

        return touched;
    }

    /// <summary>
    /// Retires levels that price traded STRICTLY THROUGH, side-aware. Returns those newly retired.
    ///
    /// NOT THE SAME AS <see cref="MarkTouches"/>, AND THE DIFFERENCE IS THE WHOLE POINT. A touch
    /// is price reaching a level; through is price passing it. An absorption line marks size that
    /// held, so it stands while price comes back and tests it and stops the moment price gets
    /// past it -- which is exactly when the size stopped holding.
    ///
    /// SIDE DECIDES THE DIRECTION. A bullish level is size that absorbed sellers, so it acts as
    /// support and dies when price trades BELOW it. A bearish one dies above. Using one
    /// direction for both would retire every level the first time price went anywhere.
    ///
    /// STRICT, so a bar whose low exactly equals a support level has tested it, not broken it.
    /// </summary>
    public IReadOnlyList<FlowLevel> RetireTradedThrough(FootprintBar closedBar)
    {
        ArgumentNullException.ThrowIfNull(closedBar);

        if (!closedBar.IsClosed)
        {
            throw new ArgumentException(
                "A level is retired from closed bars only.", nameof(closedBar));
        }

        if (double.IsNaN(closedBar.High) || double.IsNaN(closedBar.Low))
            return Array.Empty<FlowLevel>();

        var retired = new List<FlowLevel>();

        foreach (var level in this.ordered)
        {
            if (level.IsTouched || closedBar.OpenUtc <= level.CreatedUtc)
                continue;

            var through = level.Side == LevelSide.Bullish
                ? closedBar.Low < level.Price
                : closedBar.High > level.Price;

            if (!through)
                continue;

            level.MarkTouched(closedBar.OpenUtc);
            retired.Add(level);
        }

        return retired;
    }

    /// <summary>The spans to draw under a feature's rules, oldest first.</summary>
    public IReadOnlyList<LevelSpan> Visible(
        LevelFeature feature, LevelVisibility visibility, IBarBoundaries boundaries, DateTime nowUtc)
    {
        // THE BOUNDARIES, NOT A DURATION. "Print line for N bars" is a count of BARS, and this
        // used to turn it into a duration by multiplying a bar period. That is the same answer
        // on a time-bar chart and no answer at all on a chart whose bars have no duration, which
        // is why a tick chart could not run this engine. The arithmetic for time bars is
        // unchanged -- TimeBarBoundaries.OpenAfter is the same multiplication -- it has simply
        // moved to the one place that knows how long a bar is.
        ArgumentNullException.ThrowIfNull(boundaries);

        var result = new List<LevelSpan>();
        var oldestShown = visibility.DaysLookBack > 0
            ? nowUtc.AddDays(-visibility.DaysLookBack)
            : DateTime.MinValue;

        foreach (var level in this.ordered)
        {
            if (level.Feature != feature || level.CreatedUtc < oldestShown)
                continue;

            // "Print line for X bars" is a LENGTH, not a lifetime: a line that has run its
            // bars stays on the chart, frozen at its end, exactly as a touched line stays
            // frozen at the touch. Only the look-back removes history.
            DateTime? end = visibility.Extent switch
            {
                LevelExtent.UntilTouch => level.TouchedUtc,
                LevelExtent.FixedBars => boundaries.OpenAfter(level.CreatedUtc, visibility.PrintBars),
                _ => null,
            };

            result.Add(new LevelSpan(level, level.CreatedUtc, end));
        }

        // Oldest first, as promised: books are filled in discovery order, and a backfilled
        // history discovers old bars after live ones.
        result.Sort(static (a, b) => a.Level.CreatedUtc.CompareTo(b.Level.CreatedUtc));
        return result;
    }

    public void Clear()
    {
        this.byId.Clear();
        this.ordered.Clear();
    }
}
