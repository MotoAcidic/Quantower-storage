using System;
using OrbIx.Core.Features;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Flow;

/// <summary>ATAS Unfinished Auction settings (article 72000602495): "Bid Filter — Bid volumes that are less than the value set in this filter will be ignored" and the mirror for Ask.</summary>
public readonly record struct UnfinishedAuctionSettings(double BidFilter, double AskFilter);

/// <summary>
/// ATAS Unfinished Auction, read verbatim (article 72000602495, 2026-09-11):
///
///   "This term is used to describe a situation where the extreme price level (upper,
///    lower, or both) contains both buys and sells at the same time."
///
///   "if you are looking for a highlight at the low level, you need to have more bids at
///    the low than the bid filter, and the number of asks should be greater than zero."
///
///   "If you are looking for a highlight at the high level, you need to have more asks at
///    the high than the ask filter, and the number of bids should be greater than zero."
///
/// "Bids at the low" is volume traded AT THE BID at the bar's lowest price — the seller
/// aggressed — and "asks" is volume traded at the ask. Both comparisons are STRICT
/// ("more than", "greater than zero"), so a bid volume exactly equal to the filter does not
/// qualify; the test pins that boundary.
///
/// The presenter's use (46:40–49:40): a level "we have to come back and fill", which is
/// the touch lifecycle in <see cref="LevelBook"/>.
/// </summary>
public static class UnfinishedAuctionScan
{
    public static IReadOnlyList<FlowLevel> Scan(FootprintBar bar, UnfinishedAuctionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (!bar.IsClosed)
            throw new ArgumentException("An auction is unfinished only once the bar has closed.", nameof(bar));

        if (!bar.HasPrints)
            return Array.Empty<FlowLevel>();

        var levels = new List<FlowLevel>(2);

        var lowCell = bar.CellAt(bar.LowIndex);

        if (lowCell.BidVolume > settings.BidFilter && lowCell.AskVolume > 0)
        {
            var price = bar.PriceOf(bar.LowIndex);
            levels.Add(new FlowLevel(
                Id(bar, "low"), LevelFeature.UnfinishedAuction, LevelSide.Bullish,
                price, price, price, bar.OpenUtc,
                Label("UA low", lowCell), lowCell.BidVolume));
        }

        var highCell = bar.CellAt(bar.HighIndex);

        if (highCell.AskVolume > settings.AskFilter && highCell.BidVolume > 0)
        {
            var price = bar.PriceOf(bar.HighIndex);
            levels.Add(new FlowLevel(
                Id(bar, "high"), LevelFeature.UnfinishedAuction, LevelSide.Bearish,
                price, price, price, bar.OpenUtc,
                Label("UA high", highCell), highCell.AskVolume));
        }

        return levels;
    }

    private static string Id(FootprintBar bar, string edge)
        => string.Create(CultureInfo.InvariantCulture, $"ua:{bar.OpenUtc:O}:{edge}");

    private static string Label(string prefix, in FootprintEngine.PriceCell cell)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix} {cell.AskVolume:N0}×{cell.BidVolume:N0}");
}
