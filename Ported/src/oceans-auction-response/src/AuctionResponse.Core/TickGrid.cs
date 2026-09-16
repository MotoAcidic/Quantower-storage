using System.Globalization;

namespace AuctionResponse.Core;

/// <summary>
/// Exact price-grid arithmetic (Section 6). Prices arrive as decimals and are stored as
/// integer tick indices. Off-grid prices are rejected, never rounded into validity: a
/// rounded price would silently move a level, a zone edge or a boundary.
/// </summary>
public sealed class TickGrid
{
    public decimal TickSize { get; }

    public TickGrid(decimal tickSize)
    {
        if (tickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be positive; it comes from instrument metadata and is never assumed.");
        TickSize = tickSize;
    }

    /// <summary>True when <paramref name="price"/> lies exactly on the grid.</summary>
    public bool IsOnGrid(decimal price) => decimal.Remainder(price, TickSize) == 0m;

    /// <summary>Exact conversion to an integer tick index, or false when off-grid.</summary>
    public bool TryToTicks(decimal price, out long ticks)
    {
        if (!IsOnGrid(price)) { ticks = 0; return false; }
        ticks = (long)decimal.Divide(price, TickSize);
        return true;
    }

    public long ToTicks(decimal price) =>
        TryToTicks(price, out var t)
            ? t
            : throw new ArgumentException(
                string.Format(CultureInfo.InvariantCulture, "Price {0} is not on the {1} tick grid.", price, TickSize),
                nameof(price));

    public decimal ToPrice(long ticks) => ticks * TickSize;

    /// <summary>Half-tick quantities (midpoints, boundaries) back to a price.</summary>
    public decimal ToPriceFromHalfTicks(long halfTicks) => halfTicks * TickSize / 2m;
}
