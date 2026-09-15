using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Flow;

/// <summary>ATAS Big Trades "Calculation Mode" (article 72000602332).</summary>
public enum BigTradeMode
{
    /// <summary>"Cumulative Trades - aggregated trades mode."</summary>
    Cumulative,

    /// <summary>"Separate Trades - individual trades mode."</summary>
    Separate,
}

/// <summary>ATAS "Execution Price defines which price is used to place an aggregated trade on the chart."</summary>
public enum ExecutionPrice
{
    /// <summary>"Last - the price of the last execution in the aggregated trade."</summary>
    Last,

    /// <summary>"Start - the price of the first execution in the aggregated trade."</summary>
    Start,

    /// <summary>"Weighted Average - the average price weighted by volume."</summary>
    WeightedAverage,
}

/// <summary>
/// Big Trades settings under the article's names. <see cref="AggregationWindow"/> is the
/// one parameter the article does not define — it says an aggregated trade is "a group of
/// similar trades" and no more — so it is a stated choice, exposed rather than buried.
/// </summary>
public sealed record BigTradeSettings(
    BigTradeMode Mode,
    double MinVolume,
    double MaxVolume,
    ExecutionPrice ExecutionPrice,
    TimeSpan AggregationWindow)
{
    public void Validate()
    {
        if (!(this.MinVolume > 0) || double.IsInfinity(this.MinVolume))
            throw new ArgumentOutOfRangeException(nameof(this.MinVolume), this.MinVolume, "Min Volume must be positive; it is what makes a trade big.");

        if (this.MaxVolume < 0 || double.IsNaN(this.MaxVolume))
            throw new ArgumentOutOfRangeException(nameof(this.MaxVolume), this.MaxVolume, "Max Volume cannot be negative (0 = no upper limit).");

        if (this.Mode == BigTradeMode.Cumulative && this.AggregationWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(this.AggregationWindow), this.AggregationWindow, "Cumulative mode needs a positive aggregation window.");
    }
}

/// <summary>One large trade, single or aggregated.</summary>
/// <param name="StartUtc">First print's time.</param>
/// <param name="EndUtc">Last print's time (equal to the start for a single print).</param>
/// <param name="Aggressor">Buy, Sell, or Unknown for a print the feed did not classify.</param>
/// <param name="Volume">Total size.</param>
/// <param name="LowPrice">Lowest print price.</param>
/// <param name="HighPrice">Highest print price.</param>
/// <param name="StartPrice">First print's price.</param>
/// <param name="LastPrice">Last print's price.</param>
/// <param name="WeightedPrice">Volume-weighted average price.</param>
/// <param name="Prints">Prints aggregated.</param>
public readonly record struct BigTrade(
    DateTime StartUtc, DateTime EndUtc, Aggressor Aggressor, double Volume,
    double LowPrice, double HighPrice, double StartPrice, double LastPrice, double WeightedPrice, int Prints)
{
    /// <summary>The price the marker is placed at under the chosen execution-price rule.</summary>
    public double PriceUnder(ExecutionPrice rule) => rule switch
    {
        ExecutionPrice.Last => this.LastPrice,
        ExecutionPrice.Start => this.StartPrice,
        ExecutionPrice.WeightedAverage => this.WeightedPrice,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown execution price rule."),
    };
}

/// <summary>
/// ATAS Big Trades: "displays large market buys and sells directly on the chart […] the
/// time, price, direction, and volume of a large trade or an aggregated group of similar
/// trades" (article 72000602332).
///
/// SEPARATE MODE judges each print on its own. CUMULATIVE MODE merges consecutive prints on
/// the same side while each arrives within <see cref="BigTradeSettings.AggregationWindow"/>
/// of the previous one; a print on the other side, an unclassified print, or a gap longer
/// than the window closes the group. A group is judged when it closes, so the detector must
/// be <see cref="Flush"/>ed from the clock as well as fed from the tape — a market that goes
/// quiet after a large group would otherwise never report it.
///
/// AUTO FILTER IS NOT IMPLEMENTED. The article describes it as a threshold ATAS derives
/// from "the instrument and market activity" without stating how; the derivation is
/// theirs, and copying a guess of it would be the fabrication the settings page refuses.
/// </summary>
public sealed class BigTradeDetector
{
    private readonly BigTradeSettings settings;
    private readonly List<BigTrade> trades = new();
    private readonly int maxTrades;

    private Group? open;

    public BigTradeDetector(BigTradeSettings settings, int maxTrades = 5000)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (maxTrades < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTrades), maxTrades, "At least one trade must be retained.");

        this.settings = settings;
        this.maxTrades = maxTrades;
    }

    /// <summary>Detected trades, oldest first.</summary>
    public IReadOnlyList<BigTrade> Trades => this.trades;

    /// <summary>Feeds one print. Returns the trade it completed, if any.</summary>
    public BigTrade? Add(in TickEvent tick)
    {
        if (!(tick.Size > 0) || !double.IsFinite(tick.Price))
            return null;

        if (this.settings.Mode == BigTradeMode.Separate)
        {
            var single = new BigTrade(
                tick.TimestampUtc, tick.TimestampUtc, tick.Aggressor, tick.Size,
                tick.Price, tick.Price, tick.Price, tick.Price, tick.Price, 1);

            return this.Qualifies(single.Volume) ? this.Record(single) : null;
        }

        BigTrade? completed = null;

        if (this.open is { } group)
        {
            var sameSide = group.Aggressor == tick.Aggressor && tick.Aggressor != Aggressor.Unknown;
            var withinWindow = tick.TimestampUtc - group.LastUtc <= this.settings.AggregationWindow
                               && tick.TimestampUtc >= group.LastUtc;

            if (sameSide && withinWindow)
            {
                group.Extend(tick);
                this.open = group;
                return null;
            }

            completed = this.Close(group);
        }

        this.open = tick.Aggressor == Aggressor.Unknown ? null : Group.Start(tick);

        // An unclassified print can neither join nor start a group; on its own it is judged
        // as a single trade so its size is not lost.
        if (tick.Aggressor == Aggressor.Unknown && this.Qualifies(tick.Size))
        {
            var single = new BigTrade(
                tick.TimestampUtc, tick.TimestampUtc, Aggressor.Unknown, tick.Size,
                tick.Price, tick.Price, tick.Price, tick.Price, tick.Price, 1);
            this.Record(single);
            return completed ?? single;
        }

        return completed;
    }

    /// <summary>Closes an open group whose window has elapsed by the clock. Returns the trade it completed, if any.</summary>
    public BigTrade? Flush(DateTime nowUtc)
    {
        if (this.open is not { } group || nowUtc - group.LastUtc <= this.settings.AggregationWindow)
            return null;

        this.open = null;
        return this.Close(group);
    }

    public void Clear()
    {
        this.trades.Clear();
        this.open = null;
    }

    /// <summary>ATAS "Price Location" for a trade against the bar it printed in.</summary>
    public static bool MatchesLocation(in BigTrade trade, ExecutionPrice rule, FootprintBar bar, PriceLocation location)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (location == PriceLocation.Any)
            return true;

        if (double.IsNaN(bar.High))
            return false;

        var index = bar.IndexOf(trade.PriceUnder(rule));
        var highIndex = bar.IndexOf(bar.High);
        var lowIndex = bar.IndexOf(bar.Low);
        var bodyTop = bar.IndexOf(Math.Max(bar.Open, bar.Close));
        var bodyBottom = bar.IndexOf(Math.Min(bar.Open, bar.Close));

        var atHigh = index >= highIndex;
        var atLow = index <= lowIndex;
        var inBody = index >= bodyBottom && index <= bodyTop;
        var upperWick = index > bodyTop;
        var lowerWick = index < bodyBottom;

        return location switch
        {
            PriceLocation.AtHigh => atHigh,
            PriceLocation.AtLow => atLow,
            PriceLocation.AtHighOrLow => atHigh || atLow,
            PriceLocation.Body => inBody,
            PriceLocation.UpperWick => upperWick,
            PriceLocation.LowerWick => lowerWick,
            PriceLocation.AnyWick => upperWick || lowerWick,
            _ => throw new ArgumentOutOfRangeException(nameof(location), location, "Unknown price location."),
        };
    }

    private bool Qualifies(double volume)
        => volume >= this.settings.MinVolume
           && (this.settings.MaxVolume <= 0 || volume <= this.settings.MaxVolume);

    private BigTrade? Close(in Group group)
    {
        var trade = group.ToTrade();
        return this.Qualifies(trade.Volume) ? this.Record(trade) : null;
    }

    private BigTrade Record(in BigTrade trade)
    {
        this.trades.Add(trade);

        while (this.trades.Count > this.maxTrades)
            this.trades.RemoveAt(0);

        return trade;
    }

    private struct Group
    {
        public DateTime StartUtc;
        public DateTime LastUtc;
        public Aggressor Aggressor;
        public double Volume;
        public double Low;
        public double High;
        public double StartPrice;
        public double LastPrice;
        public double PriceVolume;
        public int Prints;

        public static Group Start(in TickEvent tick) => new()
        {
            StartUtc = tick.TimestampUtc,
            LastUtc = tick.TimestampUtc,
            Aggressor = tick.Aggressor,
            Volume = tick.Size,
            Low = tick.Price,
            High = tick.Price,
            StartPrice = tick.Price,
            LastPrice = tick.Price,
            PriceVolume = tick.Price * tick.Size,
            Prints = 1,
        };

        public void Extend(in TickEvent tick)
        {
            this.LastUtc = tick.TimestampUtc;
            this.Volume += tick.Size;
            this.Low = Math.Min(this.Low, tick.Price);
            this.High = Math.Max(this.High, tick.Price);
            this.LastPrice = tick.Price;
            this.PriceVolume += tick.Price * tick.Size;
            this.Prints++;
        }

        public readonly BigTrade ToTrade() => new(
            this.StartUtc, this.LastUtc, this.Aggressor, this.Volume,
            this.Low, this.High, this.StartPrice, this.LastPrice,
            this.Volume > 0 ? this.PriceVolume / this.Volume : this.LastPrice, this.Prints);
    }
}
