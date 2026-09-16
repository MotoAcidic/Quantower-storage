using ATAS.Indicators;
using AuctionResponse.Core;

namespace AuctionResponse.Atas;

/// <summary>
/// Translates the host's callback arguments into canonical events.
///
/// Everything the spec calls "verify before coding against it" was verified by reflection
/// against the installed build 8.0.14.399 (see COMPATIBILITY.md). In particular:
///
///   * TradeDirection is Between=0, Buy=1, Sell=2. Between maps to UNKNOWN, never to a side.
///   * MarketDataType is Bid=0, Ask=1, Trade=2. It is a book side, not an aggressor
///     direction, and is never substituted for one.
///   * MarketDataArg carries NO receive timestamp and NO source sequence. Feature time is
///     therefore stamped here, on callback entry, from a monotonic clock; the arg's Time is
///     kept only as diagnostic ExchangeUtc.
/// </summary>
public static class EventMapper
{
    /// <summary>Raw enum values are preserved so a log can be re-read if the host changes.</summary>
    public static AggressorDirection MapDirection(TradeDirection direction, out bool known) =>
        direction switch
        {
            TradeDirection.Buy => Set(out known, true, AggressorDirection.Buy),
            TradeDirection.Sell => Set(out known, true, AggressorDirection.Sell),
            TradeDirection.Between => Set(out known, true, AggressorDirection.Unknown),
            _ => Set(out known, false, AggressorDirection.Unknown)   // unknown enum: fault, not coerce
        };

    private static AggressorDirection Set(out bool known, bool value, AggressorDirection d)
    {
        known = value;
        return d;
    }

    public static BookSide? MapSide(MarketDataType type) => type switch
    {
        MarketDataType.Bid => BookSide.Bid,
        MarketDataType.Ask => BookSide.Ask,
        _ => null
    };

    /// <summary>
    /// Builds a trade event. An off-grid price is FLAGGED, not rounded: the engine drops it
    /// from the math rather than moving it onto a tick it never traded at.
    /// </summary>
    public static MarketEvent Trade(
        MarketDataArg arg, TickGrid grid, InstrumentKey instrument, int epoch, long elapsedNs, DateTime receiveUtc)
    {
        var direction = MapDirection(arg.Direction, out var directionKnown);
        var quality = QualityFlag.None;

        long? ticks = null;
        if (grid.TryToTicks(arg.Price, out var t)) ticks = t;
        else quality |= QualityFlag.OffGrid;

        if (!directionKnown) quality |= QualityFlag.UnknownHostEnum;
        if (arg.Volume < 0m) quality |= QualityFlag.NegativeQuantity;

        return new MarketEvent
        {
            Kind = EventKind.Trade,
            Instrument = instrument,
            ConnectionEpoch = epoch,
            ReceiveElapsedNs = elapsedNs,
            ReceiveUtc = receiveUtc,
            ExchangeUtc = arg.Time == default ? null : arg.Time.ToUniversalTime(),
            SourceSequence = null,           // this feed supplies none; see COMPATIBILITY.md
            RawDirection = (int)arg.Direction,
            RawDataType = (int)arg.DataType,
            OriginalPrice = arg.Price,
            OriginalQuantity = arg.Volume,
            PriceTicks = ticks,
            Direction = direction,
            // The associated order id is the PASSIVE side on some feeds and the aggressor on
            // others. Until that is verified here it is carried as data and used for nothing.
            OrderId = arg.ExchangeOrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AggressorId = arg.AggressorExchangeOrderId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Quality = quality
        };
    }

    /// <summary>Builds a best bid/ask event from a coherent pair held by the adapter.</summary>
    public static MarketEvent BestQuote(
        TickGrid grid, InstrumentKey instrument, int epoch, long elapsedNs, DateTime receiveUtc,
        decimal bidPrice, decimal bidQty, decimal askPrice, decimal askQty, DateTime? exchangeUtc)
    {
        var quality = QualityFlag.None;

        long? bidTicks = null, askTicks = null;
        if (grid.TryToTicks(bidPrice, out var b)) bidTicks = b; else quality |= QualityFlag.OffGrid;
        if (grid.TryToTicks(askPrice, out var a)) askTicks = a; else quality |= QualityFlag.OffGrid;
        if (bidQty < 0m || askQty < 0m) quality |= QualityFlag.NegativeQuantity;

        return new MarketEvent
        {
            Kind = EventKind.BestQuote,
            Instrument = instrument,
            ConnectionEpoch = epoch,
            ReceiveElapsedNs = elapsedNs,
            ReceiveUtc = receiveUtc,
            ExchangeUtc = exchangeUtc,
            BidTicks = bidTicks,
            AskTicks = askTicks,
            BidQuantity = bidQty,
            AskQuantity = askQty,
            Quality = quality
        };
    }

    public static MarketEvent DepthChange(
        MarketDataArg arg, TickGrid grid, InstrumentKey instrument, int epoch, long elapsedNs, DateTime receiveUtc)
    {
        var quality = QualityFlag.None;
        long? ticks = null;
        if (grid.TryToTicks(arg.Price, out var t)) ticks = t; else quality |= QualityFlag.OffGrid;

        var side = MapSide(arg.DataType);
        if (side is null) quality |= QualityFlag.UnknownHostEnum;

        return new MarketEvent
        {
            Kind = EventKind.DepthChange,
            Instrument = instrument,
            ConnectionEpoch = epoch,
            ReceiveElapsedNs = elapsedNs,
            ReceiveUtc = receiveUtc,
            ExchangeUtc = arg.Time == default ? null : arg.Time.ToUniversalTime(),
            RawDataType = (int)arg.DataType,
            OriginalPrice = arg.Price,
            OriginalQuantity = arg.Volume,
            PriceTicks = ticks,
            Side = side,
            Quality = quality
        };
    }

    public static MarketEvent Connection(InstrumentKey instrument, int newEpoch, long elapsedNs, DateTime receiveUtc, string detail)
        => new()
        {
            Kind = EventKind.Connection,
            Instrument = instrument,
            ConnectionEpoch = newEpoch,
            ReceiveElapsedNs = elapsedNs,
            ReceiveUtc = receiveUtc,
            Detail = detail
        };

    public static MarketEvent Health(InstrumentKey instrument, int epoch, long elapsedNs, DateTime receiveUtc, string detail)
        => new()
        {
            Kind = EventKind.Health,
            Instrument = instrument,
            ConnectionEpoch = epoch,
            ReceiveElapsedNs = elapsedNs,
            ReceiveUtc = receiveUtc,
            Detail = detail
        };
}
