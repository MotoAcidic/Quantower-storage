using System;
using System.Collections.Generic;

namespace OrbIx.Core.Structure;

/// <summary>Which way the impulse leg ran.</summary>
public enum FibDirection
{
    /// <summary>Up: the leg rose from the origin low to the far high.</summary>
    Up,

    /// <summary>Down: the leg fell from the origin high to the far low.</summary>
    Down,
}

/// <summary>
/// Where the far end of the leg comes from.
/// </summary>
public enum FibAnchorMode
{
    /// <summary>
    /// The running extreme since the origin swing, including the bar forming now.
    ///
    /// THE REASON THE GRID IS LIVE. <see cref="HhLlEngine"/> emits a label rightBars AFTER
    /// the extreme it names — its own documentation says a swing is not known when it forms
    /// — so a fib anchored on confirmed swings at both ends sits still for rightBars at a
    /// time and then jumps. Tracking the running extreme at the far end lets the grid
    /// stretch with price while the origin stays put.
    /// </summary>
    LiveExtreme,

    /// <summary>
    /// The last confirmed swing at both ends. The grid then moves only when structure
    /// changes, which is steadier and is behind price by rightBars.
    /// </summary>
    ConfirmedSwings,
}

/// <summary>One retracement level.</summary>
/// <param name="Ratio">The fraction of the leg retraced. 0 is the far end, 1 the origin.</param>
/// <param name="Price">The price that ratio lands on.</param>
public readonly record struct FibLevel(double Ratio, double Price);

/// <summary>
/// A price region rather than a line — the golden pocket.
/// </summary>
/// <param name="LowPrice">The lower edge, whichever ratio produced it.</param>
/// <param name="HighPrice">The upper edge.</param>
/// <remarks>
/// EMITTED AS A BAND, NOT INFERRED FROM TWO LINES. A renderer that filled between "the
/// 0.618 line and the 0.65 line" would have to know which is above the other, and that
/// flips with the leg's direction. Ordered here once, where the direction is known.
/// </remarks>
public readonly record struct FibBand(double LowPrice, double HighPrice);

/// <summary>
/// The leg and everything measured on it.
/// </summary>
/// <param name="Direction">Which way the impulse ran.</param>
/// <param name="OriginPrice">Where the leg started. Ratio 1.0.</param>
/// <param name="OriginBar">The bar the origin swing sits on.</param>
/// <param name="FarPrice">The leg's extreme. Ratio 0.</param>
/// <param name="FarBar">The bar the extreme sits on.</param>
/// <param name="Levels">Every requested ratio, in the order given.</param>
/// <param name="GoldenPocket">The 0.618-0.65 region, or null when not requested.</param>
public readonly record struct FibGrid(
    FibDirection Direction,
    double OriginPrice,
    int OriginBar,
    double FarPrice,
    int FarBar,
    IReadOnlyList<FibLevel> Levels,
    FibBand? GoldenPocket);

/// <summary>
/// Fibonacci retracement over the structure <see cref="HhLlEngine"/> already finds.
///
/// WHAT IT IS. Two points make a leg; a retracement is arithmetic between them. The hard
/// half — deciding which swing highs and lows are real — is already done, golden-tested
/// against 17,454 MNQ 5m bars, and is consumed here unchanged.
///
/// PURE, AND WITHOUT PLATFORM TYPES, like the other Core reducers: it takes bars and labels
/// and returns prices, so every property below is testable without a chart.
///
/// NOTHING HERE PRODUCES A SIGNAL. It reports where the levels are. Entries, exits and
/// alerts are not its business and it has no opinion about what price does at a level.
/// </summary>
public static class FibLevels
{
    /// <summary>The two edges of the golden pocket.</summary>
    public const double GoldenPocketLow = 0.618;

    /// <summary>The far edge of the golden pocket.</summary>
    public const double GoldenPocketHigh = 0.65;

    /// <summary>
    /// The retracement set drawn by default.
    ///
    /// 0 and 1.0 are included because the leg's own ends are levels — a grid that stopped at
    /// 0.786 would leave the operator to find the extreme by eye.
    /// </summary>
    public static readonly IReadOnlyList<double> DefaultRatios =
        new[] { 0d, 0.236, 0.382, 0.5, GoldenPocketLow, GoldenPocketHigh, 0.786, 1d };

    /// <summary>
    /// Extension ratios, beyond the leg rather than inside it. Off unless asked for.
    /// </summary>
    public static readonly IReadOnlyList<double> DefaultExtensions =
        new[] { 1.272, 1.618 };

    /// <summary>
    /// Reads <see cref="HhLlEngine.TrendSeries"/>'s per-bar value as a direction.
    /// </summary>
    /// <param name="trendValue">The engine's value: 1 up, -1 down, 0 before the first breakout.</param>
    /// <returns>The direction, or null when no trend has been established.</returns>
    /// <remarks>
    /// ZERO IS NOT UP. The engine documents 0 as "before the first breakout" — genuinely no
    /// trend yet, not a weak one — and folding it into either direction would anchor the grid
    /// on a leg chosen by a coin toss for as long as the session had not broken out.
    ///
    /// A THREE-LINE MAPPING IN ITS OWN METHOD, because the alternative is three lines inside
    /// the indicator where no test can reach them: a mutation that mapped 0 to Up passed the
    /// entire suite, and the only guard available there would have read source text — which
    /// has been hollow three separate times in this codebase today.
    /// </remarks>
    public static FibDirection? TrendFrom(double trendValue)
        => trendValue switch
        {
            > 0 => FibDirection.Up,
            < 0 => FibDirection.Down,
            _ => null,
        };

    /// <summary>
    /// Builds the grid for the current leg, or null when there is no usable one.
    /// </summary>
    /// <param name="labels">
    /// Confirmed structure from <see cref="HhLlEngine.Labels"/>, oldest first.
    /// </param>
    /// <param name="highs">Bar highs, indexed by bar, as the engine was fed them.</param>
    /// <param name="lows">Bar lows, same indexing.</param>
    /// <param name="ratios">Which ratios to compute. <see cref="DefaultRatios"/> if null.</param>
    /// <param name="mode">Where the far end comes from.</param>
    /// <param name="includeGoldenPocket">Whether to emit the pocket band.</param>
    /// <param name="trend">
    /// Which way the market is trending, from <see cref="HhLlEngine.TrendSeries"/>, or null
    /// when there is no trend to follow.
    ///
    /// WHY THE LEG IS CHOSEN BY TREND AND NOT BY RECENCY. Anchoring on the newest completed
    /// leg means anchoring on whatever happened last, and in a trend what happened last is
    /// usually the counter-trend BOUNCE. Measured on MNQ 1m at 2026-09-04T20:59Z: the
    /// structure was plainly down — 29,547 to 29,514 through two lower lows — and the newest
    /// completed leg was the 29,514 to 29,532.50 bounce, so the grid put ratio 0 at the top
    /// of a rally inside a downtrend. Arithmetically correct, and the wrong leg to be
    /// measuring; it reads as an inverted fib because the ends sit where the opposite move
    /// would put them.
    ///
    /// With a trend supplied, the newest swing IN THE TREND'S DIRECTION anchors the far end —
    /// a high in an uptrend, a low in a downtrend — and the opposite-kind swing before it
    /// becomes the origin. The grid then measures the impulse and the retracement of it,
    /// which is what a retracement is for, and a bounce no longer flips it.
    ///
    /// NULL MEANS NO TREND, NOT UP. <see cref="HhLlEngine.TrendSeries"/> reports 0 before the
    /// first breakout, and there is genuinely no trend to follow then; the newest leg is used
    /// instead, which is the best available answer rather than a guess dressed as one.
    /// </param>
    /// <returns>
    /// The grid, or NULL when no leg can be measured — no confirmed swing yet, or an
    /// origin and extreme at the same price. A zero-height leg would divide the price range
    /// by nothing and put every ratio on one line, which draws as a single thick smear and
    /// says nothing; refusing is the honest answer.
    /// </returns>
    /// <exception cref="ArgumentNullException">The labels, highs or lows are null.</exception>
    public static FibGrid? Build(
        IReadOnlyList<HhLlLabel>? labels,
        IReadOnlyList<double>? highs,
        IReadOnlyList<double>? lows,
        IReadOnlyList<double>? ratios = null,
        FibAnchorMode mode = FibAnchorMode.LiveExtreme,
        bool includeGoldenPocket = true,
        FibDirection? trend = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);

        if (labels.Count < 2 || highs.Count == 0 || lows.Count == 0)
            return null;

        // THE LEG IS THE LAST COMPLETED SWING LEG, older end to newer end.
        //
        // THE BUG THIS REPLACED, measured on real bars 2026-09-04. The origin used to be the
        // LAST confirmed swing, which is wrong whenever that swing is the END of the move
        // rather than its start -- and half the time it is. At bar 62 of the 5m set the
        // impulse ran 29,569.00 -> 29,628.75; the old code took the 29,628.75 HIGH as the
        // origin and hunted for a running low, so it measured the PULLBACK as though it were
        // the impulse and put 0.618 at 29,614.04 where the answer is 29,591.82. The two ends
        // of the grid were swapped and the leg was the wrong leg.
        //
        // A swing high ENDS an up-impulse and a swing low ENDS a down one, so the newer swing
        // is the far end (ratio 0) and the opposite-kind swing before it is the origin
        // (ratio 1.0). That is the leg a person means by "the move", and the retracement is
        // measured back from its extreme.
        static bool IsHigh(HhLlLabelKind kind)
            => kind is HhLlLabelKind.HigherHigh or HhLlLabelKind.LowerHigh;

        // THE FAR END IS THE NEWEST SWING IN THE TREND'S DIRECTION — a high in an uptrend, a
        // low in a downtrend — so the grid measures the impulse rather than the bounce that
        // followed it. Without a trend it is simply the newest swing, which is the best
        // available answer when no direction has been established.
        var newerIndex = -1;

        if (trend is { } direction)
        {
            var wantHigh = direction == FibDirection.Up;

            for (var i = labels.Count - 1; i >= 0; i--)
            {
                if (IsHigh(labels[i].Kind) == wantHigh)
                {
                    newerIndex = i;
                    break;
                }
            }
        }
        else
        {
            newerIndex = labels.Count - 1;
        }

        // A trend with no swing of its own kind yet has no leg to measure. Null rather than
        // the other direction's leg, which would draw the very thing this parameter exists
        // to stop.
        if (newerIndex <= 0)
            return null;

        var newer = labels[newerIndex];
        var newerIsHigh = IsHigh(newer.Kind);
        var older = -1;

        for (var i = newerIndex - 1; i >= 0; i--)
        {
            if (IsHigh(labels[i].Kind) != newerIsHigh)
            {
                older = i;
                break;
            }
        }

        if (older < 0)
            return null;

        // A high at the newer end means the impulse ran UP into it.
        var up = newerIsHigh;
        var origin = labels[older];

        if (origin.Bar < 0 || origin.Bar >= highs.Count || origin.Bar >= lows.Count)
            return null;

        var originPrice = origin.Price;
        var farPrice = newer.Price;
        var farBar = newer.Bar;

        if (mode == FibAnchorMode.LiveExtreme)
        {
            // EXTENDED BEYOND THE CONFIRMED EXTREME, never behind it. Price can push past a
            // confirmed swing before the next label appears -- HhLlEngine only confirms
            // rightBars later -- and the grid must follow that rather than sit at a high
            // price has already left. A retracement never moves the far end BACK, which is
            // why this only ever takes a more extreme value.
            var last = Math.Min(highs.Count, lows.Count);

            for (var bar = newer.Bar; bar < last; bar++)
            {
                if (up && highs[bar] > farPrice)
                {
                    farPrice = highs[bar];
                    farBar = bar;
                }
                else if (!up && lows[bar] < farPrice)
                {
                    farPrice = lows[bar];
                    farBar = bar;
                }
            }
        }

        var height = farPrice - originPrice;

        if (height == 0 || !double.IsFinite(height))
            return null;

        var wanted = ratios ?? DefaultRatios;
        var levels = new List<FibLevel>(wanted.Count);

        foreach (var ratio in wanted)
        {
            // Ratio 0 sits at the FAR end and 1.0 at the origin, which is the direction a
            // charting package draws a retracement: the grid measures how far price has
            // come back from the extreme.
            levels.Add(new FibLevel(ratio, farPrice - (height * ratio)));
        }

        FibBand? pocket = null;

        if (includeGoldenPocket)
        {
            var a = farPrice - (height * GoldenPocketLow);
            var b = farPrice - (height * GoldenPocketHigh);

            pocket = new FibBand(Math.Min(a, b), Math.Max(a, b));
        }

        return new FibGrid(
            up ? FibDirection.Up : FibDirection.Down,
            originPrice,
            origin.Bar,
            farPrice,
            farBar,
            levels,
            pocket);
    }
}
