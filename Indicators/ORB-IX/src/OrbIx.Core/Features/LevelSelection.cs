using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbIx.Core.Features;

/// <summary>
/// Which levels are worth drawing right now, and in what order.
///
/// THE POINT OF THIS TYPE IS THAT A CHART STAYS READABLE. The engine tracks prior day and week
/// extremes, overnight extremes, an initial balance, an opening range for every session window,
/// and round numbers — on a product with eight sessions that is well over thirty prices, and
/// drawing all of them turns a chart into a grid. A level far from price cannot be reached
/// today and costs the operator attention for nothing.
///
/// So the rule is distance: a level is drawn only when it sits within a band of the last price.
/// Nothing is discarded — <see cref="LevelGraph"/> keeps every level and keeps reasoning about
/// them — they simply are not painted, and they reappear as price travels toward them.
///
/// It lives in Core and is pure so the rule is covered by the offline suite. A drawing rule
/// that can only be checked by looking at a chart is a drawing rule nobody checks.
/// </summary>
public static class LevelSelection
{
    /// <summary>
    /// The half-width of the band around price, in PRICE.
    /// </summary>
    /// <param name="averageDailyRange">
    /// The product's average daily range, or zero when it could not be measured.
    /// </param>
    /// <param name="bandFraction">Fraction of that range to draw either side of price.</param>
    /// <param name="fallbackTicks">
    /// What to use when the average daily range is UNMEASURED. There has to be a number here
    /// and it must not be "everything": falling back to no filter at all would produce exactly
    /// the unreadable chart this exists to prevent, on precisely the days when history failed
    /// to load. It comes from configuration rather than being invented at the call site.
    /// </param>
    /// <param name="tickSize">The product's minimum increment.</param>
    public static double BandWidth(
        double averageDailyRange, double bandFraction, int fallbackTicks, double tickSize)
    {
        if (bandFraction <= 0 || double.IsNaN(bandFraction))
            throw new ArgumentOutOfRangeException(nameof(bandFraction), bandFraction, "The band fraction must be positive.");

        if (fallbackTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(fallbackTicks), fallbackTicks, "The fallback band must be positive.");

        if (tickSize <= 0 || double.IsNaN(tickSize))
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "A tick size must be positive.");

        return averageDailyRange > 0 && !double.IsNaN(averageDailyRange)
            ? averageDailyRange * bandFraction
            : fallbackTicks * tickSize;
    }

    /// <summary>Whether the average daily range supplied the band, or the fallback did.</summary>
    public static bool BandIsMeasured(double averageDailyRange)
        => averageDailyRange > 0 && !double.IsNaN(averageDailyRange);

    /// <summary>
    /// The levels to draw, nearest to price first.
    ///
    /// Ordered by distance so that when a caller caps how many it will paint, what survives is
    /// the closest rather than whatever the graph happened to enumerate first.
    ///
    /// INCLUSIVE AT THE BOUNDARY. A level exactly at the band edge is drawn: the band is "within
    /// this far", and a strict comparison would make a level flicker in and out as price moved
    /// by one tick around it.
    /// </summary>
    /// <param name="levels">Every level held, in any order.</param>
    /// <param name="lastPrice">The reference. Not a valid price means nothing is drawn.</param>
    /// <param name="bandWidth">From <see cref="BandWidth"/>.</param>
    /// <param name="limit">
    /// The most to return. A cap is not a substitute for the band — it is the guard against a
    /// pathological case where hundreds of round numbers fall inside one band.
    /// </param>
    public static IReadOnlyList<Level> NearPrice(
        IEnumerable<Level> levels, double lastPrice, double bandWidth, int limit)
    {
        if (levels is null)
            throw new ArgumentNullException(nameof(levels));

        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The limit must be positive.");

        // No price means no reference, and a band around zero would select whatever sits
        // nearest to zero — which on any real instrument is nothing, but on a mis-fed one
        // could be anything. Refusing to draw is the honest answer to "where is price?".
        if (double.IsNaN(lastPrice) || lastPrice <= 0)
            return Array.Empty<Level>();

        if (bandWidth <= 0 || double.IsNaN(bandWidth))
            return Array.Empty<Level>();

        return levels
            .Where(l => l is not null && !double.IsNaN(l.Price) && l.Price > 0)
            .Where(l => Math.Abs(l.Price - lastPrice) <= bandWidth)
            .OrderBy(l => Math.Abs(l.Price - lastPrice))
            .ThenBy(l => l.Label, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Round numbers inside the band.
    ///
    /// Generated rather than stored, because they are infinite and only the ones near price
    /// have ever mattered. The step comes from the product's configuration; a non-positive one
    /// means the product does not use them and yields none rather than a division by zero.
    /// </summary>
    public static IReadOnlyList<Level> RoundNumbers(
        double lastPrice, double bandWidth, double step, DateTime asOfUtc)
    {
        if (step <= 0 || double.IsNaN(step) || double.IsNaN(lastPrice) || lastPrice <= 0)
            return Array.Empty<Level>();

        if (bandWidth <= 0 || double.IsNaN(bandWidth))
            return Array.Empty<Level>();

        var first = Math.Ceiling((lastPrice - bandWidth) / step) * step;
        var levels = new List<Level>();

        // Bounded by the band, and additionally by a count, so a misconfigured step of one tick
        // cannot spin here.
        for (var price = first; price <= lastPrice + bandWidth && levels.Count < MaxRoundNumbers; price += step)
        {
            levels.Add(new Level(
                price,
                LevelKind.RoundNumber,
                asOfUtc,
                price.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return levels;
    }

    /// <summary>
    /// A ceiling on generated round numbers.
    ///
    /// A step small relative to the band would otherwise generate thousands, and the loop that
    /// makes them would be the slowest thing on the paint path.
    /// </summary>
    private const int MaxRoundNumbers = 24;
}
