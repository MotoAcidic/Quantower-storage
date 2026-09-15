using System;
using OrbIx.Core.Features;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Flow;

/// <summary>ATAS Cluster Search "Calculation Mode" (article 72000602240).</summary>
public enum ClusterMode
{
    /// <summary>"Bid — volume traded at Bid."</summary>
    Bid,

    /// <summary>"Ask — volume traded at Ask."</summary>
    Ask,

    /// <summary>"Delta — Ask volume minus Bid volume."</summary>
    Delta,

    /// <summary>"Volume — total traded volume at a price level."</summary>
    Volume,

    /// <summary>"Ticks — number of trades executed at a price level."</summary>
    Ticks,

    /// <summary>"POC Level — the price level with the highest volume inside a candle."</summary>
    PocLevel,
}

/// <summary>ATAS "Candle Direction — Bullish, Bearish, Neutral, or Any."</summary>
public enum CandleDirection
{
    Any,
    Bullish,
    Bearish,
    Neutral,
}

/// <summary>ATAS "Price Location" options, in the article's order.</summary>
public enum PriceLocation
{
    Any,
    AtHigh,
    AtLow,
    AtHighOrLow,
    Body,
    UpperWick,
    LowerWick,
    AnyWick,
}

/// <summary>
/// Every Cluster Search filter the article lists, under the article's own names. A zero
/// maximum means "no upper bound"; a zero minimum means "no lower bound"; a zero pip or
/// height limit means "off". Percentages are 0–100.
/// </summary>
public sealed record ClusterSearchSettings(
    ClusterMode Mode,
    double MinValue,
    double MaxValue,
    double MinAverageTrade,
    double MaxAverageTrade,
    double MinVolumePercent,
    double MaxVolumePercent,
    double BidAskImbalancePercent,
    double DeltaFilter,
    CandleDirection Direction,
    int BarsRange,
    int PriceRange,
    int PipsFromHigh,
    int PipsFromLow,
    PriceLocation Location,
    int MinCandleHeight,
    int MaxCandleHeight,
    int MinBodyHeight,
    int MaxBodyHeight,
    bool UseTimeFilter,
    TimeOnly TimeFrom,
    TimeOnly TimeTo,
    TimeZoneInfo? TimeZone,
    bool OnlyOnePerBar)
{
    public void Validate()
    {
        if (this.BarsRange < 1)
            throw new ArgumentOutOfRangeException(nameof(this.BarsRange), this.BarsRange, "Bars Range combines at least one bar.");

        if (this.PriceRange < 1)
            throw new ArgumentOutOfRangeException(nameof(this.PriceRange), this.PriceRange, "Price Range combines at least one level.");

        if (this.UseTimeFilter && this.TimeZone is null)
            throw new ArgumentException("A time filter needs the zone its times are in.", nameof(this.TimeZone));

        foreach (var (name, value) in new[]
        {
            (nameof(this.MinValue), this.MinValue), (nameof(this.MaxValue), this.MaxValue),
            (nameof(this.MinAverageTrade), this.MinAverageTrade), (nameof(this.MaxAverageTrade), this.MaxAverageTrade),
            (nameof(this.MinVolumePercent), this.MinVolumePercent), (nameof(this.MaxVolumePercent), this.MaxVolumePercent),
            (nameof(this.BidAskImbalancePercent), this.BidAskImbalancePercent),
        })
        {
            if (value < 0 || double.IsNaN(value))
                throw new ArgumentOutOfRangeException(name, value, "Filters cannot be negative.");
        }
    }
}

/// <summary>One matching cluster.</summary>
/// <param name="BarOpenUtc">The bar (the newest, when bars were combined) the hit belongs to.</param>
/// <param name="Price">The row's price (the group's lowest row when levels were combined).</param>
/// <param name="PriceHigh">The group's highest row.</param>
/// <param name="Value">The value in the selected mode.</param>
/// <param name="Strength">Value over Minimum Value; 1 when no minimum is set. Sizes the marker.</param>
/// <param name="Mode">The calculation mode the value was read in.</param>
/// <param name="Cell">The cell (or combined cell) that matched.</param>
public readonly record struct ClusterHit(
    DateTime BarOpenUtc, double Price, double PriceHigh, double Value, double Strength, ClusterMode Mode, FootprintEngine.PriceCell Cell)
{
    /// <summary>The persistent "X marks the spot" level the presenter keeps from a hit (42:50–44:40).</summary>
    public FlowLevel ToLevel(LevelSide side)
        => new(
            string.Create(CultureInfo.InvariantCulture, $"cs:{this.BarOpenUtc:O}:{this.Price:F8}"),
            LevelFeature.ClusterSearch, side, this.Price, this.Price, this.PriceHigh, this.BarOpenUtc,
            string.Create(CultureInfo.InvariantCulture, $"{this.Mode} {this.Value:N0}"), this.Value);
}

/// <summary>
/// ATAS Cluster Search: "analyzes footprint data inside each candle and compares every
/// price level against the selected filter conditions" (article 72000602240). Every
/// filter the article names is applied here, in the article's terms; the two choices the
/// article leaves open are stated on the settings that make them.
///
/// The presenter's use (37:30–46:00): mode Volume, minimum "2K" on the demo and "for MNQ
/// it would be something like 5,000", any direction, and a notification when it fires.
/// </summary>
public static class ClusterSearchEngine
{
    /// <summary>
    /// Scans the newest bar of <paramref name="bars"/> (or the newest
    /// <see cref="ClusterSearchSettings.BarsRange"/> bars combined). Bars must be ascending.
    /// </summary>
    public static IReadOnlyList<ClusterHit> Scan(IReadOnlyList<FootprintBar> bars, ClusterSearchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (bars.Count == 0)
            return Array.Empty<ClusterHit>();

        var candle = Combine(bars, settings.BarsRange);

        if (!candle.HasPrints || double.IsNaN(candle.High))
            return Array.Empty<ClusterHit>();

        if (!PassesCandleFilters(candle, settings))
            return Array.Empty<ClusterHit>();

        var groups = Group(candle, settings.PriceRange);
        var hits = new List<ClusterHit>();

        if (settings.Mode == ClusterMode.PocLevel)
        {
            // "POC Level — the price level with the highest volume inside a candle." Only that
            // level is a candidate; the value judged is its volume. Ties go to the lower price.
            var poc = -1;
            var pocVolume = -1d;

            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Cell.Total > pocVolume)
                {
                    poc = i;
                    pocVolume = groups[i].Cell.Total;
                }
            }

            if (poc >= 0 && TryJudge(candle, groups[poc], pocVolume, settings, out var hit))
                hits.Add(hit);
        }
        else
        {
            foreach (var group in groups)
            {
                var value = ValueOf(group.Cell, settings.Mode);

                if (TryJudge(candle, group, value, settings, out var hit))
                    hits.Add(hit);
            }
        }

        if (settings.OnlyOnePerBar && hits.Count > 1)
        {
            var best = hits[0];

            foreach (var hit in hits)
            {
                if (hit.Strength > best.Strength || (hit.Strength == best.Strength && hit.Price < best.Price))
                    best = hit;
            }

            return new[] { best };
        }

        return hits;
    }

    /// <summary>The value the mode reads from a cell. Delta is signed; its magnitude is what the min/max compare against.</summary>
    public static double ValueOf(in FootprintEngine.PriceCell cell, ClusterMode mode) => mode switch
    {
        ClusterMode.Bid => cell.BidVolume,
        ClusterMode.Ask => cell.AskVolume,
        ClusterMode.Delta => cell.Delta,
        ClusterMode.Volume => cell.Total,
        ClusterMode.Ticks => cell.Trades,
        ClusterMode.PocLevel => cell.Total,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown calculation mode."),
    };

    private static bool TryJudge(FootprintBar candle, in PriceGroup group, double value, ClusterSearchSettings settings, out ClusterHit hit)
    {
        hit = default;
        var cell = group.Cell;

        // "Minimum Value / Maximum Value". Delta is a signed quantity; the min/max bound its
        // MAGNITUDE and the signed Delta Filter below is where direction is expressed —
        // the article does not say which, and this reading keeps both filters meaningful.
        var magnitude = Math.Abs(value);

        if (settings.MinValue > 0 && magnitude < settings.MinValue)
            return false;

        if (settings.MaxValue > 0 && magnitude > settings.MaxValue)
            return false;

        var average = cell.AverageTrade;

        if (settings.MinAverageTrade > 0 && average < settings.MinAverageTrade)
            return false;

        if (settings.MaxAverageTrade > 0 && average > settings.MaxAverageTrade)
            return false;

        // "Min / Max Volume Percent — percentage of candle or range volume."
        var share = candle.Volume > 0 ? cell.Total / candle.Volume * 100d : 0d;

        if (settings.MinVolumePercent > 0 && share < settings.MinVolumePercent)
            return false;

        if (settings.MaxVolumePercent > 0 && share > settings.MaxVolumePercent)
            return false;

        // "Bid Ask Imbalance (%)": the larger side over the smaller, as a percentage, must reach the filter.
        if (settings.BidAskImbalancePercent > 0)
        {
            var larger = Math.Max(cell.AskVolume, cell.BidVolume);
            var smaller = Math.Min(cell.AskVolume, cell.BidVolume);
            var ratioPercent = smaller > 0 ? larger / smaller * 100d : (larger > 0 ? double.PositiveInfinity : 0d);

            if (ratioPercent < settings.BidAskImbalancePercent)
                return false;
        }

        // "Delta Filter": positive asks for at least that much positive delta, negative for at least that much negative.
        if (settings.DeltaFilter > 0 && cell.Delta < settings.DeltaFilter)
            return false;

        if (settings.DeltaFilter < 0 && cell.Delta > settings.DeltaFilter)
            return false;

        if (!PassesLocation(candle, group, settings))
            return false;

        var strength = settings.MinValue > 0 ? magnitude / settings.MinValue : 1d;

        hit = new ClusterHit(
            candle.OpenUtc, candle.PriceOf(group.LowIndex), candle.PriceOf(group.HighIndex),
            value, strength, settings.Mode, cell);
        return true;
    }

    private static bool PassesCandleFilters(FootprintBar candle, ClusterSearchSettings settings)
    {
        var direction = candle.Close > candle.Open ? CandleDirection.Bullish
            : candle.Close < candle.Open ? CandleDirection.Bearish
            : CandleDirection.Neutral;

        if (settings.Direction != CandleDirection.Any && settings.Direction != direction)
            return false;

        var heightTicks = candle.Height / candle.TickSize;
        var bodyTicks = Math.Abs(candle.Close - candle.Open) / candle.TickSize;

        if (settings.MinCandleHeight > 0 && heightTicks < settings.MinCandleHeight) return false;
        if (settings.MaxCandleHeight > 0 && heightTicks > settings.MaxCandleHeight) return false;
        if (settings.MinBodyHeight > 0 && bodyTicks < settings.MinBodyHeight) return false;
        if (settings.MaxBodyHeight > 0 && bodyTicks > settings.MaxBodyHeight) return false;

        // Validate() has already refused a time filter without a zone, so a null zone here
        // means the filter is off.
        if (settings.UseTimeFilter && settings.TimeZone is { } zone)
        {
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(candle.OpenUtc, DateTimeKind.Utc), zone));

            if (!local.IsBetween(settings.TimeFrom, settings.TimeTo))
                return false;
        }

        return true;
    }

    private static bool PassesLocation(FootprintBar candle, in PriceGroup group, ClusterSearchSettings settings)
    {
        var highIndex = candle.IndexOf(candle.High);
        var lowIndex = candle.IndexOf(candle.Low);
        var bodyTop = candle.IndexOf(Math.Max(candle.Open, candle.Close));
        var bodyBottom = candle.IndexOf(Math.Min(candle.Open, candle.Close));

        if (settings.PipsFromHigh > 0 && highIndex - group.HighIndex > settings.PipsFromHigh)
            return false;

        if (settings.PipsFromLow > 0 && group.LowIndex - lowIndex > settings.PipsFromLow)
            return false;

        var atHigh = group.HighIndex >= highIndex;
        var atLow = group.LowIndex <= lowIndex;
        var inBody = group.LowIndex >= bodyBottom && group.HighIndex <= bodyTop;
        var upperWick = group.LowIndex > bodyTop;
        var lowerWick = group.HighIndex < bodyBottom;

        return settings.Location switch
        {
            PriceLocation.Any => true,
            PriceLocation.AtHigh => atHigh,
            PriceLocation.AtLow => atLow,
            PriceLocation.AtHighOrLow => atHigh || atLow,
            PriceLocation.Body => inBody,
            PriceLocation.UpperWick => upperWick,
            PriceLocation.LowerWick => lowerWick,
            PriceLocation.AnyWick => upperWick || lowerWick,
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Location, "Unknown price location."),
        };
    }

    /// <summary>
    /// "Bars Range — combines multiple bars during analysis": the newest N bars merged into
    /// one candle whose open is the oldest's, close the newest's, range the union.
    /// </summary>
    private static FootprintBar Combine(IReadOnlyList<FootprintBar> bars, int barsRange)
    {
        var newest = bars[^1];

        if (barsRange == 1)
            return newest;

        var first = Math.Max(0, bars.Count - barsRange);
        var oldest = bars[first];
        var combined = new FootprintBar(oldest.OpenUtc, newest.CloseUtc, newest.TickSize, newest.Source);

        var high = double.NegativeInfinity;
        var low = double.PositiveInfinity;

        for (var i = first; i < bars.Count; i++)
        {
            var bar = bars[i];

            foreach (var (index, cell) in bar.Cells)
            {
                combined.AddLevel(
                    bar.PriceOf(index), cell.BuyVolume, cell.SellVolume, cell.UnclassifiedVolume,
                    cell.BuyTrades, cell.SellTrades, cell.UnclassifiedTrades, cell.MaxOneTradeVolume);
            }

            if (!double.IsNaN(bar.High))
            {
                high = Math.Max(high, bar.High);
                low = Math.Min(low, bar.Low);
            }
        }

        if (double.IsFinite(high) && !double.IsNaN(oldest.Open) && !double.IsNaN(newest.Close))
            combined.SetRange(oldest.Open, high, low, newest.Close);

        return combined;
    }

    /// <summary>
    /// "Price Range — combines multiple price levels into one search range": rows bucketed
    /// in groups of N ticks on the absolute tick grid, so a group never straddles two bars'
    /// readings of the same price.
    /// </summary>
    private static List<PriceGroup> Group(FootprintBar candle, int priceRange)
    {
        var groups = new SortedDictionary<long, PriceGroup>();

        foreach (var (index, cell) in candle.Cells)
        {
            var key = priceRange == 1 ? index : FloorDiv(index, priceRange);

            if (!groups.TryGetValue(key, out var group))
            {
                var low = priceRange == 1 ? index : key * priceRange;
                group = new PriceGroup(low, low + priceRange - 1, default);
            }

            var merged = group.Cell;
            merged.Combine(cell);
            groups[key] = group with { Cell = merged };
        }

        return new List<PriceGroup>(groups.Values);
    }

    private static long FloorDiv(long value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
    }

    private readonly record struct PriceGroup(long LowIndex, long HighIndex, FootprintEngine.PriceCell Cell);
}
