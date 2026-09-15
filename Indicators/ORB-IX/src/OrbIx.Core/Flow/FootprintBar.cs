using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Features;

namespace OrbIx.Core.Flow;

/// <summary>Where a bar's cells came from. Stated on the chart, never blended silently.</summary>
public enum FootprintSource
{
    /// <summary>Accumulated live from prints classified against the prevailing quote.</summary>
    LiveTicks,

    /// <summary>Seeded from the platform's per-price volume-analysis levels.</summary>
    VendorLevels,

    /// <summary>Rebuilt from tick history served by a connection (possibly a borrowed one).</summary>
    TickHistory,
}

/// <summary>
/// One bar's footprint: OHLC, the per-price cells, and the running statistics ATAS's
/// Cluster Statistics rows are built from (article 72000602624).
///
/// PRICES ARE KEYED BY TICK INDEX, NOT BY DOUBLE. Two prints one tick apart must never
/// land in the same cell, and two prints at the same price must never land in different
/// ones; integer keys make both impossible, where float keys make both merely unlikely.
///
/// MIN AND MAX DELTA ARE RUNNING EXTREMES OF THE BAR'S CUMULATIVE DELTA — "Maximum Delta
/// value within the candle" — and therefore exist only for bars built from prints in
/// order. A bar seeded from vendor levels carries the vendor's own figures where the
/// platform supplies them, and otherwise reports them as not measured
/// (<see cref="HasDeltaExtremes"/>) rather than as zero.
/// </summary>
public sealed class FootprintBar
{
    private readonly Dictionary<long, FootprintEngine.PriceCell> cells = new();
    private readonly double tickSize;

    private double runningDelta;
    private long lowIndex = long.MaxValue;
    private long highIndex = long.MinValue;

    /// <param name="openUtc">When the bar opened.</param>
    /// <param name="closeUtc">When it closes, or the best answer so far when provisional.</param>
    /// <param name="tickSize">The instrument's price grid.</param>
    /// <param name="source">Where the bar's contents came from.</param>
    /// <param name="closeIsProvisional">
    /// True when the END OF THIS BAR IS NOT YET KNOWN, which is the normal state of a forming
    /// bar on a chart whose bars have no fixed duration.
    ///
    /// A time bar knows its close the moment it opens — open plus the period — so nothing about
    /// it is provisional. A tick, range or Renko bar ends when the market says so, and until the
    /// next bar opens there is no answer. The alternative was to invent one, and inventing it is
    /// not harmless: <see cref="VolumePerSecond"/> clamps elapsed time to the bar's duration, so
    /// a bar constructed with a close of "now" would report a duration near zero and a volume
    /// rate to match, on every forming bar, forever.
    /// </param>
    public FootprintBar(
        DateTime openUtc,
        DateTime closeUtc,
        double tickSize,
        FootprintSource source,
        bool closeIsProvisional = false)
    {
        if (closeUtc <= openUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closeUtc), closeUtc, "A bar must close after it opens.");
        }

        if (!(tickSize > 0) || double.IsInfinity(tickSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickSize), tickSize, "Cells are keyed by tick, so the price grid is required.");
        }

        this.OpenUtc = openUtc;
        this.CloseUtc = closeUtc;
        this.CloseIsProvisional = closeIsProvisional;
        this.tickSize = tickSize;
        this.Source = source;
        this.Open = double.NaN;
        this.High = double.NaN;
        this.Low = double.NaN;
        this.Close = double.NaN;
        this.MinDelta = double.NaN;
        this.MaxDelta = double.NaN;
    }

    public DateTime OpenUtc { get; }

    /// <summary>
    /// When the bar closes. Final unless <see cref="CloseIsProvisional"/> says otherwise.
    /// </summary>
    public DateTime CloseUtc { get; private set; }

    /// <summary>
    /// True while the bar's end is unknown — a forming bar on a chart whose bars have no fixed
    /// duration. Cleared by <see cref="Freeze(DateTime)"/> when the true end arrives.
    /// </summary>
    public bool CloseIsProvisional { get; private set; }

    public TimeSpan Duration => this.CloseUtc - this.OpenUtc;

    public FootprintSource Source { get; }

    public double TickSize => this.tickSize;

    public double Open { get; private set; }

    public double High { get; private set; }

    public double Low { get; private set; }

    public double Close { get; private set; }

    public double Volume { get; private set; }

    public double BuyVolume { get; private set; }

    public double SellVolume { get; private set; }

    public double UnclassifiedVolume { get; private set; }

    public int Trades { get; private set; }

    /// <summary>
    /// Whether <see cref="Trades"/> is a COUNT or an absence.
    ///
    /// A bar rebuilt from tick history carries volume per price and no print counts — the source
    /// aggregates them per minute, not per price. Reporting zero there would be a measurement
    /// where there is none, and the statistics band would render it as a real, bright zero. Same
    /// distinction the delta extremes already make, for the same reason.
    /// </summary>
    public bool HasTrades { get; private set; } = true;

    /// <summary>Marks this bar's trade count as unknown rather than zero.</summary>
    public void MarkTradesUnmeasured() => this.HasTrades = false;

    /// <summary>Ask volume minus Bid volume over the whole bar.</summary>
    public double Delta => this.BuyVolume - this.SellVolume;

    /// <summary>Lowest value the bar's cumulative delta reached; NaN when not measured.</summary>
    public double MinDelta { get; private set; }

    /// <summary>Highest value the bar's cumulative delta reached; NaN when not measured.</summary>
    public double MaxDelta { get; private set; }

    public bool HasDeltaExtremes => !double.IsNaN(this.MinDelta) && !double.IsNaN(this.MaxDelta);

    public bool IsClosed { get; private set; }

    public bool HasPrints => this.cells.Count > 0;

    /// <summary>Share of volume that carried an aggressor, in [0,1]; zero for an empty bar.</summary>
    public double ClassifiedFraction
        => this.Volume > 0 ? (this.BuyVolume + this.SellVolume) / this.Volume : 0d;

    /// <summary>Cells keyed by tick index. Empty for a bar that saw no prints.</summary>
    public IReadOnlyDictionary<long, FootprintEngine.PriceCell> Cells => this.cells;

    public long LowIndex => this.lowIndex;

    public long HighIndex => this.highIndex;

    public double PriceOf(long tickIndex) => tickIndex * this.tickSize;

    public long IndexOf(double price) => (long)Math.Round(price / this.tickSize, MidpointRounding.AwayFromZero);

    /// <summary>The cell at a tick index, or an empty cell where nothing traded there.</summary>
    public FootprintEngine.PriceCell CellAt(long tickIndex) => this.cells.TryGetValue(tickIndex, out var cell) ? cell : default;

    /// <summary>Adds one live print. Refused after the bar has closed.</summary>
    public void Add(in TickEvent tick)
    {
        if (this.IsClosed)
            throw new InvalidOperationException("A closed bar accepts no further prints.");

        // NaN and non-positive sizes are refused explicitly: "size <= 0" alone does not
        // reject NaN, and one NaN print would poison every total in the bar permanently
        // (ORB-IX measured exactly this in OrBuilder.OnBook, 2026-08-22).
        if (!(tick.Size > 0) || !double.IsFinite(tick.Price))
            return;

        var index = this.IndexOf(tick.Price);
        var price = this.PriceOf(index);

        var cell = this.CellAt(index);
        cell.Add(tick.Size, tick.Aggressor);
        this.cells[index] = cell;

        this.Track(index);
        this.TrackPrice(price);

        this.Volume += tick.Size;
        this.Trades++;

        switch (tick.Aggressor)
        {
            case Aggressor.Buy:
                this.BuyVolume += tick.Size;
                this.runningDelta += tick.Size;
                break;

            case Aggressor.Sell:
                this.SellVolume += tick.Size;
                this.runningDelta -= tick.Size;
                break;

            default:
                this.UnclassifiedVolume += tick.Size;
                break;
        }

        if (double.IsNaN(this.MinDelta) || this.runningDelta < this.MinDelta)
            this.MinDelta = this.runningDelta;

        if (double.IsNaN(this.MaxDelta) || this.runningDelta > this.MaxDelta)
            this.MaxDelta = this.runningDelta;
    }

    /// <summary>
    /// Adds a whole price level from a historical source. The bar's OHLC must be supplied
    /// through <see cref="SetRange"/> by the caller, because a level carries no order of
    /// events and so cannot say where the bar opened or closed.
    /// </summary>
    public void AddLevel(
        double price, double buy, double sell, double unclassified,
        int buyTrades, int sellTrades, int unclassifiedTrades, double maxOneTrade)
    {
        if (this.IsClosed)
            throw new InvalidOperationException("A closed bar accepts no further levels.");

        if (!double.IsFinite(price))
            return;

        var index = this.IndexOf(price);
        var cell = this.CellAt(index);
        cell.AddLevel(buy, sell, unclassified, buyTrades, sellTrades, unclassifiedTrades, maxOneTrade);
        this.cells[index] = cell;

        this.Track(index);

        this.Volume += buy + sell + unclassified;
        this.BuyVolume += buy;
        this.SellVolume += sell;
        this.UnclassifiedVolume += unclassified;
        this.Trades += buyTrades + sellTrades + unclassifiedTrades;
    }

    /// <summary>Sets the bar's OHLC from the source that knows it (a chart bar, or the print stream).</summary>
    public void SetRange(double open, double high, double low, double close)
    {
        if (!double.IsFinite(open) || !double.IsFinite(high) || !double.IsFinite(low) || !double.IsFinite(close))
            throw new ArgumentException("A bar range must be finite.");

        if (high < low)
            throw new ArgumentException("A bar's high cannot be below its low.");

        this.Open = open;
        this.High = high;
        this.Low = low;
        this.Close = close;
    }

    /// <summary>Records the vendor's own intra-bar delta extremes when a historical source supplies them.</summary>
    public void SetDeltaExtremes(double minDelta, double maxDelta)
    {
        if (!double.IsFinite(minDelta) || !double.IsFinite(maxDelta) || maxDelta < minDelta)
            throw new ArgumentException("Delta extremes must be finite and ordered.");

        this.MinDelta = minDelta;
        this.MaxDelta = maxDelta;
    }

    /// <summary>Freezes the bar. Prints arriving after the close can no longer rewrite it.</summary>
    public void Freeze() => this.IsClosed = true;

    /// <summary>
    /// Closes the bar at the instant it actually ended.
    ///
    /// For a bar whose end could not be known while it was forming. The close recorded at
    /// construction was the best answer available then; this is the real one, and it is taken
    /// rather than estimated — on a tick chart the true end of a bar is the open of the next.
    /// </summary>
    public void Freeze(DateTime closeUtc)
    {
        if (closeUtc <= this.OpenUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closeUtc), closeUtc, "A bar must close after it opens.");
        }

        this.CloseUtc = closeUtc;
        this.CloseIsProvisional = false;
        this.IsClosed = true;
    }

    /// <summary>
    /// One price level of a bar assembled from aggregates rather than watched print by print.
    /// </summary>
    /// <param name="Price">The level.</param>
    /// <param name="Buy">Volume the buyer aggressed for.</param>
    /// <param name="Sell">Volume the seller aggressed for.</param>
    /// <param name="Unclassified">Volume the source did not attribute to a side.</param>
    /// <param name="BuyTrades">Prints on the buy side.</param>
    /// <param name="SellTrades">Prints on the sell side.</param>
    /// <param name="UnclassifiedTrades">Prints the source did not attribute.</param>
    /// <param name="MaxOneTrade">The largest single print at this level.</param>
    public readonly record struct SeededLevel(
        double Price, double Buy, double Sell, double Unclassified,
        int BuyTrades, int SellTrades, int UnclassifiedTrades, double MaxOneTrade);

    /// <summary>
    /// Builds a closed bar from per-price aggregates — the shape a platform's volume analysis
    /// publishes for a historical bar.
    ///
    /// WHY SEEDING EXISTS AT ALL: the level displays look back whole days. Without history they
    /// would show nothing until bars formed under a chart that was already open, and a look-back
    /// of three days would mean nothing on attach.
    ///
    /// THE DELTA EXTREMES ARE TAKEN, NOT INVENTED. A bar assembled from aggregates has no print
    /// order, so the running delta's path through it is unknowable from the cells alone — but the
    /// source that aggregated them watched that path and publishes its extremes. Passing NaN
    /// leaves them UNMEASURED, which the statistics display renders as such rather than as zero.
    /// Zero is a reading; absent is not, and the two must not look alike.
    /// </summary>
    public static FootprintBar FromLevels(
        DateTime openUtc,
        DateTime closeUtc,
        double tickSize,
        IEnumerable<SeededLevel> levels,
        double open,
        double high,
        double low,
        double close,
        double minDelta,
        double maxDelta,
        FootprintSource source,
        bool tradesMeasured = true)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var built = new FootprintBar(openUtc, closeUtc, tickSize, source);

        foreach (var level in levels)
        {
            built.AddLevel(
                level.Price, level.Buy, level.Sell, level.Unclassified,
                level.BuyTrades, level.SellTrades, level.UnclassifiedTrades, level.MaxOneTrade);
        }

        built.SetRange(open, high, low, close);

        if (!double.IsNaN(minDelta) && !double.IsNaN(maxDelta))
            built.SetDeltaExtremes(minDelta, maxDelta);

        if (!tradesMeasured)
            built.MarkTradesUnmeasured();

        built.Freeze();
        return built;
    }

    /// <summary>Volume per second over the bar's duration (ATAS "Volume/sec").</summary>
    public double VolumePerSecond(DateTime nowUtc)
    {
        var elapsed = this.IsClosed ? this.Duration : nowUtc - this.OpenUtc;

        // THE CLAMP ONLY APPLIES WHERE THE DURATION IS A REAL CEILING. A forming TIME bar cannot
        // run longer than its period, so elapsed beyond it is a clock artefact. A forming bar on
        // a chart with no fixed duration has no ceiling at all, and clamping to a provisional
        // close would hold its volume rate at whatever it was the moment the bar was created.
        if (!this.CloseIsProvisional && elapsed > this.Duration)
            elapsed = this.Duration;

        return elapsed.TotalSeconds > 0 ? this.Volume / elapsed.TotalSeconds : 0d;
    }

    /// <summary>Height in price (High − Low); zero for a bar with no range yet.</summary>
    public double Height => double.IsNaN(this.High) ? 0d : this.High - this.Low;

    /// <summary>The traded price with the most volume, and how much. Ties resolve to the lower price.</summary>
    public (long Index, double Volume) PointOfControl()
    {
        var bestIndex = long.MinValue;
        var bestVolume = -1d;

        foreach (var (index, cell) in this.cells)
        {
            var total = cell.Total;

            if (total > bestVolume || (total == bestVolume && index < bestIndex))
            {
                bestIndex = index;
                bestVolume = total;
            }
        }

        return (bestIndex, bestVolume < 0 ? 0d : bestVolume);
    }

    /// <summary>
    /// The same cells keyed by PRICE, which is the shape <see cref="ImbalanceRule"/> and the
    /// rest of ORB-IX read.
    ///
    /// A RE-KEY, NOT A CONVERSION. This once mapped a second cell type field by field; there is
    /// only one cell type now, so nothing here can drop a field by forgetting to copy it.
    /// </summary>
    public Dictionary<double, FootprintEngine.PriceCell> ToPriceCells()
    {
        var result = new Dictionary<double, FootprintEngine.PriceCell>(this.cells.Count);

        foreach (var (index, cell) in this.cells)
            result[this.PriceOf(index)] = cell;

        return result;
    }

    private void Track(long index)
    {
        if (index < this.lowIndex) this.lowIndex = index;
        if (index > this.highIndex) this.highIndex = index;
    }

    private void TrackPrice(double price)
    {
        if (double.IsNaN(this.Open))
        {
            this.Open = price;
            this.High = price;
            this.Low = price;
        }

        if (price > this.High) this.High = price;
        if (price < this.Low) this.Low = price;
        this.Close = price;
    }
}
