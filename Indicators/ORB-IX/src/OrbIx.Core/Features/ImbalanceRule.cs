using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>
/// Whether one diagonal could be judged, and what it said.
///
/// THREE STATES, NOT TWO. "Nobody traded enough here to have an opinion" and "both sides
/// traded and neither dominated" are different facts, and collapsing them would let a
/// silent tape read as a balanced one. The same distinction
/// <see cref="FootprintEngine"/> already draws between unclassified volume and zero delta.
/// </summary>
public enum DiagonalState
{
    /// <summary>
    /// Too little volume on the pair to judge. NOT a claim that the diagonal was balanced.
    /// </summary>
    Unjudged = 0,

    /// <summary>Judged, and the dominant side did not clear the ratio.</summary>
    Balanced = 1,

    /// <summary>Judged, and the dominant side cleared the ratio.</summary>
    Imbalanced = 2,
}

/// <summary>
/// One diagonal: the aggressive volume on one side against the passive-facing volume it is
/// compared with, one tick away.
/// </summary>
/// <param name="State">Whether it could be judged, and the verdict if so.</param>
/// <param name="Dominant">Aggressive volume at this price on the side being judged.</param>
/// <param name="Opposing">Aggressive volume one tick away on the other side.</param>
public readonly record struct DiagonalVerdict(
    DiagonalState State,
    double Dominant,
    double Opposing)
{
    /// <summary>The dominant side cleared the ratio on a diagonal that could be judged.</summary>
    public bool IsImbalanced => this.State == DiagonalState.Imbalanced;

    /// <summary>There was enough volume on the pair to hold any opinion at all.</summary>
    public bool IsJudged => this.State != DiagonalState.Unjudged;
}

/// <summary>
/// One price row of a bar's footprint, with both of its diagonals evaluated independently.
///
/// BOTH, BECAUSE A ROW GENUINELY CAN BE BOTH. The buy diagonal at P compares against the
/// price BELOW; the sell diagonal at P compares against the price ABOVE. They are different
/// comparisons against different neighbours, so a row can be imbalanced on one, the other,
/// neither or both. Forcing a single winning side per row would have to invent a tie rule
/// that no footprint convention actually has.
/// </summary>
/// <param name="Price">The price row, on the instrument's tick grid.</param>
/// <param name="BuySide">
/// Aggressive buying at <paramref name="Price"/> against aggressive selling one tick below.
/// </param>
/// <param name="SellSide">
/// Aggressive selling at <paramref name="Price"/> against aggressive buying one tick above.
/// </param>
public readonly record struct ImbalanceLevel(
    double Price,
    DiagonalVerdict BuySide,
    DiagonalVerdict SellSide);

/// <summary>
/// What the diagonals of one bar's footprint add up to.
/// </summary>
/// <param name="BuyLevels">Price rows imbalanced toward aggressive buyers.</param>
/// <param name="SellLevels">Price rows imbalanced toward aggressive sellers.</param>
/// <param name="LongestBuyRun">
/// Longest run of CONSECUTIVE tick rows imbalanced toward buyers. This is the number a
/// stacked-imbalance read is actually about; <paramref name="BuyLevels"/> counts rows that
/// may be scattered through the bar.
/// </param>
/// <param name="LongestSellRun">The same, toward sellers.</param>
/// <param name="JudgedDiagonals">Diagonals carrying enough volume to judge.</param>
/// <param name="TotalDiagonals">
/// Diagonals examined. Judged over total is the fraction of the bar this reading actually
/// speaks for, in the same spirit as <see cref="FootprintReading.Classified"/>.
/// </param>
public sealed record ImbalanceReading(
    int BuyLevels,
    int SellLevels,
    int LongestBuyRun,
    int LongestSellRun,
    int JudgedDiagonals,
    int TotalDiagonals)
{
    /// <summary>A reading over a footprint with no prints in it.</summary>
    public static ImbalanceReading Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Fraction of the examined diagonals that carried enough volume to judge. Zero when
    /// nothing was examined — which is an absence of measurement, not a balanced bar.
    /// </summary>
    public double Coverage
        => this.TotalDiagonals > 0 ? (double)this.JudgedDiagonals / this.TotalDiagonals : 0d;
}

/// <summary>
/// A run of consecutive imbalanced rows on one side.
/// </summary>
/// <param name="StartIndex">Index of the lowest row of the run in the evaluated list.</param>
/// <param name="Length">Consecutive rows in the run.</param>
/// <param name="BuySide">Which side the run is on.</param>
public readonly record struct ImbalanceRun(int StartIndex, int Length, bool BuySide)
{
    /// <summary>Index one past the highest row of the run.</summary>
    public int EndIndex => this.StartIndex + this.Length;

    /// <summary>Whether this run is long enough to count as stacked.</summary>
    public bool IsStacked(int minRun) => this.Length >= minRun;
}

/// <summary>
/// Diagonal footprint imbalance, and the stacked runs it forms.
///
/// WHY THE COMPARISON IS DIAGONAL AND NOT HORIZONTAL. Aggressive buying at price P lifts an
/// offer resting at P. Aggressive selling at P-1 tick hits a bid resting at P-1. Those two
/// resting orders form one 1-tick spread, so they are the pair that actually competed. The
/// horizontal comparison — buys against sells at the SAME price — pairs volume that never
/// met the same resting order, which is why no footprint convention uses it.
///
/// So the two diagonals evaluated at each price P are:
///
///     buy  diagonal:  BuyVolume[P]  against  SellVolume[P - 1 tick]
///     sell diagonal:  SellVolume[P] against  BuyVolume[P + 1 tick]
///
/// UNCLASSIFIED VOLUME IS NEVER BORROWED BY EITHER SIDE.
/// <see cref="FootprintEngine.PriceCell.UnclassifiedVolume"/> is volume whose aggressor
/// could not be established; splitting it, or attributing it to the nearer side, would
/// invent the very number an imbalance is read for.
///
/// NO EDGE HAS BEEN MEASURED FOR THIS. Stacked imbalance is a widely used footprint read
/// and it is UNVERIFIED on this stack — every microstructure family tested here so far has
/// come back null. This type computes what it says it computes; it makes no claim that the
/// number predicts anything, and the journal records its verdict beside the outcome so that
/// question can be answered from forward evidence rather than asserted now.
/// </summary>
/// <summary>
/// How a diagonal's thresholds are applied. Two published readings disagree, and both are kept
/// because both have a claim.
/// </summary>
/// <param name="StrictRatio">
/// True compares <c>dominant &gt; ratio * opposing</c>; false <c>&gt;=</c>. ATAS's
/// footprint-settings article states the strict form — ask 30 at 350% means the bid "must exceed
/// 105", so 105 is not an imbalance and 106 is.
///
/// MEASURED AS A PHANTOM ON REAL TAPE: across 237,816 diagonals over five RTH sessions, this
/// difference explained ZERO disagreements. It requires exact float equality between the
/// dominant side and ratio times the opposing side, which is measure-zero on live prints. It is
/// carried for conformance, not because it changes what gets drawn.
/// </param>
/// <param name="FilterOnDominantAlone">
/// True measures the volume floor against the DOMINANT cell only, which is ATAS's reading —
/// "volumes that are less than the value set in this filter will be ignored" is about the level.
/// False measures it against the PAIR's summed volume, which is what ORB-IX has always done.
///
/// THIS ONE IS NOT A PHANTOM. It is the whole of the measured difference between the two rules,
/// and across 37 sessions the pair-sum reading draws about TWICE as many stacks at every usable
/// floor and every bar period — 1m at floor 50: 166 against 399. See
/// docs/IMBALANCE-VOLUME-FLOOR.md.
/// </param>
/// <param name="IgnoreZeroOpposing">
/// ATAS "Ignore zero values". A ratio against an empty opposing side is undefined rather than
/// infinite; true leaves those rows unjudged, false reports them imbalanced.
/// </param>
public readonly record struct ImbalanceSemantics(
    bool StrictRatio, bool FilterOnDominantAlone, bool IgnoreZeroOpposing)
{
    /// <summary>
    /// What ORB-IX has always done, and the default everywhere.
    ///
    /// This is a named configuration rather than a set of parameter defaults ON PURPOSE. The
    /// imbalance gate, both playbooks and every trial that measured them ran against exactly
    /// these three choices, so they need to be one thing that can be pointed at — not three
    /// booleans that could drift apart one careless default at a time.
    /// </summary>
    public static ImbalanceSemantics OrbIx { get; } = new(
        StrictRatio: false, FilterOnDominantAlone: false, IgnoreZeroOpposing: false);

    /// <summary>
    /// The reading ATAS publishes, which the ported footprint toolkit reproduces.
    ///
    /// <c>IgnoreZeroOpposing</c> is a user setting there, so it is left false here and set by
    /// the caller with <c>with</c>.
    /// </summary>
    public static ImbalanceSemantics Atas { get; } = new(
        StrictRatio: true, FilterOnDominantAlone: true, IgnoreZeroOpposing: false);
}

public static class ImbalanceRule
{
    /// <summary>
    /// Multiple of the opposing side the dominant side must reach. 2.0 matches
    /// <c>phase1/orderflow_features.py::FOOTPRINT_RATIO</c>, so the C# and Python readings
    /// of the same tape agree.
    /// </summary>
    public const double DefaultRatio = 2.0d;

    /// <summary>
    /// Volume a diagonal must carry across BOTH its cells before it is judged at all.
    /// 10 matches <c>phase1/orderflow_features.py::FOOTPRINT_MIN_PRINTS</c>, whose rule is
    /// to return None below it rather than a verdict.
    /// </summary>
    public const double DefaultMinVolume = 10d;

    /// <summary>
    /// Consecutive imbalanced rows before a run is called stacked. Three is the common
    /// footprint convention and is UNVERIFIED here, which is why it is configurable.
    /// </summary>
    public const int DefaultMinRun = 3;

    /// <summary>
    /// Evaluates both diagonals at every price present in the footprint.
    ///
    /// Rows are returned in ascending price order so a caller painting them, or scanning
    /// them for runs, does not depend on dictionary iteration order — which would make a
    /// replay disagree with the live run it is meant to reproduce.
    /// </summary>
    /// <param name="cells">Volume at each price, split by aggressor.</param>
    /// <param name="tickSize">The instrument's price grid. Must be positive.</param>
    /// <param name="ratio">Multiple of the opposing side the dominant side must reach.</param>
    /// <param name="minVolume">Volume across the pair below which the diagonal is unjudged.</param>
    /// <param name="semantics">
    /// Which reading of the thresholds to apply. Null means
    /// <see cref="ImbalanceSemantics.OrbIx"/> — chosen explicitly rather than by relying on a
    /// struct's all-false default, so that flipping the sense of a flag one day cannot silently
    /// move what every existing caller gets.
    /// </param>
    public static IReadOnlyList<ImbalanceLevel> Evaluate(
        IReadOnlyDictionary<double, FootprintEngine.PriceCell> cells,
        double tickSize,
        double ratio = DefaultRatio,
        double minVolume = DefaultMinVolume,
        ImbalanceSemantics? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (!(tickSize > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickSize), tickSize,
                "Imbalance is a comparison between adjacent ticks, so it needs the price grid.");
        }

        if (!(ratio > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ratio), ratio,
                "A ratio at or below 1 marks the larger side of every diagonal as imbalanced.");
        }

        if (!(minVolume > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minVolume), minVolume,
                "A minimum of zero judges diagonals nobody traded on.");
        }

        var rules = semantics ?? ImbalanceSemantics.OrbIx;

        var byTick = IndexByTick(cells, tickSize);

        if (byTick.Count == 0)
            return Array.Empty<ImbalanceLevel>();

        var levels = new List<ImbalanceLevel>(byTick.Count);

        foreach (var (tick, cell) in byTick)
        {
            // The neighbour's absence is a real zero: no aggressive volume traded there.
            // That is different from the pair being untradeable, and the minimum-volume
            // rule below is what decides whether the pair is worth judging.
            var sellBelow = byTick.TryGetValue(tick - 1, out var below) ? below.SellVolume : 0d;
            var buyAbove = byTick.TryGetValue(tick + 1, out var above) ? above.BuyVolume : 0d;

            levels.Add(new ImbalanceLevel(
                tick * tickSize,
                Judge(cell.BuyVolume, sellBelow, ratio, minVolume, rules),
                Judge(cell.SellVolume, buyAbove, ratio, minVolume, rules)));
        }

        return levels;
    }

    /// <summary>
    /// Summarises evaluated rows, including the longest consecutive run on each side.
    /// </summary>
    public static ImbalanceReading Summarise(IReadOnlyList<ImbalanceLevel> levels, double tickSize)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (!(tickSize > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickSize), tickSize, "Runs are counted in ticks, so they need the grid.");
        }

        if (levels.Count == 0)
            return ImbalanceReading.Empty;

        var buyLevels = 0;
        var sellLevels = 0;
        var judged = 0;

        foreach (var level in levels)
        {
            if (level.BuySide.IsImbalanced) buyLevels++;
            if (level.SellSide.IsImbalanced) sellLevels++;
            if (level.BuySide.IsJudged) judged++;
            if (level.SellSide.IsJudged) judged++;
        }

        return new ImbalanceReading(
            buyLevels,
            sellLevels,
            LongestRun(levels, tickSize, buySide: true),
            LongestRun(levels, tickSize, buySide: false),
            judged,
            levels.Count * 2);
    }

    /// <summary>
    /// Every run of CONSECUTIVE tick rows imbalanced toward one side.
    ///
    /// A gap in price breaks a run, and so does a row that could not be judged. Carrying a
    /// run through an unjudged row would report a stack that was never observed — the run
    /// length is the whole claim, so it cannot be built over rows that made no claim.
    ///
    /// THE ONE IMPLEMENTATION OF "WHAT IS A RUN". <see cref="LongestRun"/> and the chart's
    /// stacked highlight both read it, because two copies of this walk is how the number the
    /// gate acts on and the marks a trader sees would come to disagree.
    /// </summary>
    /// <returns>Runs in ascending price order. Indices refer to <paramref name="levels"/>.</returns>
    public static IReadOnlyList<ImbalanceRun> Runs(
        IReadOnlyList<ImbalanceLevel> levels, double tickSize, bool buySide)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (!(tickSize > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickSize), tickSize, "Runs are counted in ticks, so they need the grid.");
        }

        var runs = new List<ImbalanceRun>();
        var start = -1;
        var length = 0;
        var previousTick = long.MinValue;

        for (var i = 0; i < levels.Count; i++)
        {
            var tick = ToTick(levels[i].Price, tickSize);
            var imbalanced = buySide
                ? levels[i].BuySide.IsImbalanced
                : levels[i].SellSide.IsImbalanced;

            if (!imbalanced)
            {
                if (length > 0)
                    runs.Add(new ImbalanceRun(start, length, buySide));

                start = -1;
                length = 0;
                previousTick = tick;
                continue;
            }

            if (length > 0 && tick == previousTick + 1)
            {
                length++;
            }
            else
            {
                if (length > 0)
                    runs.Add(new ImbalanceRun(start, length, buySide));

                start = i;
                length = 1;
            }

            previousTick = tick;
        }

        if (length > 0)
            runs.Add(new ImbalanceRun(start, length, buySide));

        return runs;
    }

    /// <summary>
    /// Longest run of CONSECUTIVE tick rows imbalanced toward one side.
    /// </summary>
    public static int LongestRun(
        IReadOnlyList<ImbalanceLevel> levels, double tickSize, bool buySide)
    {
        var longest = 0;

        foreach (var run in Runs(levels, tickSize, buySide))
        {
            if (run.Length > longest)
                longest = run.Length;
        }

        return longest;
    }

    /// <summary>
    /// One diagonal's verdict.
    ///
    /// The comparison is a MULTIPLICATION, never a division: <c>dominant / opposing</c> is
    /// infinite whenever the opposing cell is empty, which is both a common case and a
    /// value that cannot be journalled as JSON.
    /// </summary>
    /// <param name="dominant">Volume on the side being tested.</param>
    /// <param name="opposing">Volume on the diagonal's other side.</param>
    /// <param name="ratio">Multiple of the opposing side the dominant side must reach.</param>
    /// <param name="minVolume">Volume across the pair below which the diagonal is unjudged.</param>
    /// <param name="rules">Which reading of the thresholds to apply.</param>
    private static DiagonalVerdict Judge(
        double dominant, double opposing, double ratio, double minVolume,
        in ImbalanceSemantics rules)
    {
        // NaN fails every comparison, so a diagonal carrying one lands on Unjudged by the
        // same path as a quiet one. That is the correct answer for both: no opinion.
        // ATAS measures the floor against the dominant cell; ORB-IX against the pair. This is
        // the whole of the measured difference between the two rules — see ImbalanceSemantics.
        var measured = rules.FilterOnDominantAlone ? dominant : dominant + opposing;

        if (!(measured >= minVolume))
            return new DiagonalVerdict(DiagonalState.Unjudged, dominant, opposing);

        // A ratio against an empty side is undefined rather than infinite, and the caller
        // says which answer it wants. See the parameter note above.
        if (rules.IgnoreZeroOpposing && opposing <= 0)
            return new DiagonalVerdict(DiagonalState.Unjudged, dominant, opposing);

        // The dominant side must have actually traded. Without this, two empty cells at a
        // minimum of zero would satisfy "0 >= ratio * 0" and report an imbalance nobody made.
        var imbalanced = dominant > 0
                         && (rules.StrictRatio
                             ? dominant > ratio * opposing
                             : dominant >= ratio * opposing);

        return new DiagonalVerdict(
            imbalanced ? DiagonalState.Imbalanced : DiagonalState.Balanced, dominant, opposing);
    }

    /// <summary>
    /// Re-keys the footprint by integer tick index.
    ///
    /// WHY NOT LOOK UP price ± tickSize DIRECTLY. Those are floating-point keys, and the
    /// neighbour computed by subtraction is not always bit-identical to the neighbour the
    /// footprint stored — the lookup then misses and the diagonal silently reads zero on a
    /// price that traded. An integer index makes adjacency exact.
    /// </summary>
    private static SortedDictionary<long, FootprintEngine.PriceCell> IndexByTick(
        IReadOnlyDictionary<double, FootprintEngine.PriceCell> cells, double tickSize)
    {
        var byTick = new SortedDictionary<long, FootprintEngine.PriceCell>();

        foreach (var (price, cell) in cells)
        {
            if (double.IsNaN(price) || double.IsInfinity(price))
                continue;

            var tick = ToTick(price, tickSize);

            if (!byTick.TryGetValue(tick, out var existing))
            {
                byTick[tick] = cell;
                continue;
            }

            // Two prices landing on one tick means the caller supplied an unrounded
            // footprint. Merging is the only non-lossy answer; last-wins would discard
            // volume that traded.
            //
            // THROUGH Combine, NOT A FIELD LIST WRITTEN OUT HERE. This was a hand-written
            // list of four assignments, and it went stale the moment the cell gained trade
            // counts: the merged cell silently carried volume with a count of zero, which
            // reads downstream as "seeded from history" rather than "we lost the number".
            // A merge that names its fields has to be revisited every time a field is added,
            // and the revisit is exactly what does not happen.
            var merged = existing;
            merged.Combine(cell);
            byTick[tick] = merged;
        }

        return byTick;
    }

    private static long ToTick(double price, double tickSize)
        => (long)Math.Round(price / tickSize, MidpointRounding.AwayFromZero);
}
