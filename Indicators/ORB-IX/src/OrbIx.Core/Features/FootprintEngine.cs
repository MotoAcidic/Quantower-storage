using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Features;

/// <summary>
/// What the order flow currently says.
/// </summary>
/// <param name="CumulativeDelta">Signed aggressor volume since the session opened.</param>
/// <param name="BarDelta">Signed aggressor volume in the bar in progress.</param>
/// <param name="DivergenceScore">
/// Agreement between price direction and delta direction over the recent window, in
/// [-1, +1]. Negative means price and flow disagree.
/// </param>
/// <param name="EffortResult">
/// Price movement per unit of volume, normalised against the session's own average. Low
/// values are effort without result — volume going in with no progress.
/// </param>
/// <param name="Classified">
/// Fraction of volume that could be classified against a quote. Below one, delta is a
/// partial measurement and confidence falls accordingly.
/// </param>
/// <param name="Imbalance">
/// Diagonal imbalance across the LAST CLOSED BAR, or null when imbalance was not measured.
///
/// THE LAST CLOSED BAR, NOT THE FORMING ONE, because that is the bar a decision is about:
/// <see cref="OrbIx.Core.Playbooks.BreakDetector"/> confirms a break on a bar close, in the
/// same <see cref="OrbIx.Core.Sessions.EngineFold.OnBar"/> call that folds this module, so
/// at the moment a break exists the bar that made it is the one just closed. Reading the
/// forming bar instead would judge a break on whatever has traded since it happened.
///
/// Null is "not measured" and is distinct from <see cref="ImbalanceReading.Empty"/>, which
/// is a bar that was measured and had nothing in it.
/// </param>
public sealed record FootprintReading(
    double CumulativeDelta,
    double BarDelta,
    double DivergenceScore,
    double EffortResult,
    double Classified,
    ImbalanceReading? Imbalance = null);

/// <summary>
/// M05. Bid/ask volume per price per bar, and the flow statistics that come from it.
///
/// Delta is only meaningful where prints can be classified against the quote that prevailed
/// at them. <see cref="TickEvent"/> carries that quote, so classification is a property of
/// the event rather than of whatever the book looked like when a consumer got round to
/// reading it — the ordering mistake that makes delta quietly wrong.
///
/// Where the feed cannot classify, the volume is counted but excluded from delta and the
/// unclassified fraction is reported. A delta computed from half the tape is not the same
/// number as a delta computed from all of it, and the difference belongs in the output
/// rather than in a footnote.
/// </summary>
public sealed class FootprintEngine : IFeatureModule
{
    private readonly InstrumentSpec instrument;
    private readonly int divergenceWindow;

    private readonly Dictionary<double, PriceCell> cells = new();
    private readonly Queue<BarFlow> recentBars = new();

    // PER-BAR CELLS ARE SEPARATE FROM THE SESSION'S, NOT A VIEW OVER THEM. Diagonal
    // imbalance is a statement about one bar: buying at a price against selling one tick
    // below it, WITHIN the same bar. Evaluating it over session-cumulative volume would
    // compare a print from the open against one from an hour later and call the pair a
    // diagonal, which is not what a footprint imbalance is.
    private readonly Dictionary<double, PriceCell> formingBarCells = new();
    private readonly Queue<BarFootprint> closedBarFootprints = new();

    private readonly double imbalanceRatio;
    private readonly double imbalanceMinVolume;
    private readonly int footprintHistory;

    private Dictionary<double, PriceCell> lastClosedBarCells = new();

    private double cumulativeDelta;
    private double barDelta;
    private double barVolume;
    private double classifiedVolume;
    private double totalVolume;
    private double sessionPriceMovement;
    private double sessionVolume;

    /// <summary>Running extremes of the forming bar's delta. NaN until a print lands.</summary>
    private double barMinDelta = double.NaN;
    private double barMaxDelta = double.NaN;

    /// <summary>The closed bar's extremes, held until the next one closes over them.</summary>
    private double lastClosedMinDelta = double.NaN;
    private double lastClosedMaxDelta = double.NaN;

    /// <summary>Prints stamped into a bucket at or before the last closed bar.</summary>
    private long latePrints;
    private double lateVolume;
    private DateTime lastClosedBarCloseUtc = DateTime.MinValue;

    /// <param name="instrument">Contract specification; the price grid is required.</param>
    /// <param name="divergenceWindow">Bars compared when scoring price against flow.</param>
    /// <param name="imbalanceRatio">
    /// Multiple of the opposing side a diagonal must reach. See <see cref="ImbalanceRule"/>.
    /// </param>
    /// <param name="imbalanceMinVolume">
    /// Volume a diagonal must carry before it is judged rather than left unjudged.
    /// </param>
    /// <param name="footprintHistory">
    /// Closed bars whose footprints are retained for display. Bounded because a session's
    /// footprints are otherwise unbounded in a process that runs all day.
    /// </param>
    public FootprintEngine(
        InstrumentSpec instrument,
        int divergenceWindow,
        double imbalanceRatio = ImbalanceRule.DefaultRatio,
        double imbalanceMinVolume = ImbalanceRule.DefaultMinVolume,
        int footprintHistory = DefaultFootprintHistory)
    {
        instrument.RequirePriceScale(nameof(instrument));

        if (divergenceWindow < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(divergenceWindow), divergenceWindow,
                "Divergence needs at least two bars to compare price direction against flow direction.");
        }

        if (!(imbalanceRatio > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(imbalanceRatio), imbalanceRatio,
                "A ratio at or below 1 marks the larger side of every diagonal as imbalanced.");
        }

        if (!(imbalanceMinVolume > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(imbalanceMinVolume), imbalanceMinVolume,
                "A minimum of zero judges diagonals nobody traded on.");
        }

        if (footprintHistory < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(footprintHistory), footprintHistory,
                "Retaining no closed bars would leave the last closed bar unreadable.");
        }

        this.instrument = instrument;
        this.divergenceWindow = divergenceWindow;
        this.imbalanceRatio = imbalanceRatio;
        this.imbalanceMinVolume = imbalanceMinVolume;
        this.footprintHistory = footprintHistory;
    }

    /// <summary>
    /// Closed bars retained for display. One RTH session of one-minute bars, so a chart can
    /// paint the session it is showing without the queue growing without bound.
    /// </summary>
    public const int DefaultFootprintHistory = 480;

    public string Id => "M05";

    /// <summary>Trades classified against the quote.</summary>
    public DataTier Requires => DataTier.T2;

    public bool Enabled { get; set; } = true;

    /// <summary>Volume traded at each price this SESSION, split by aggressor.</summary>
    public IReadOnlyDictionary<double, PriceCell> Cells => this.cells;

    /// <summary>Volume at each price in the bar currently forming.</summary>
    public IReadOnlyDictionary<double, PriceCell> FormingBarCells => this.formingBarCells;

    /// <summary>Volume at each price in the most recently closed bar.</summary>
    public IReadOnlyDictionary<double, PriceCell> LastClosedBarCells => this.lastClosedBarCells;

    /// <summary>
    /// Lowest and highest the last closed bar's cumulative delta reached, or NaN when the bar
    /// was not built from prints in order.
    ///
    /// A RANGE WITHIN THE BAR, NOT ITS ENDPOINTS. A bar that closes at delta zero having been
    /// +400 and then −400 is a different bar from one where nothing happened, and only these
    /// two numbers can tell them apart.
    /// </summary>
    public double LastClosedBarMinDelta => this.lastClosedMinDelta;

    public double LastClosedBarMaxDelta => this.lastClosedMaxDelta;

    /// <summary>
    /// Prints that arrived stamped for a bar that had already closed.
    ///
    /// OBSERVED, NEVER ACTED ON. The print is still counted exactly where it would have been
    /// counted before this counter existed — into the cells in progress — because changing
    /// where a print lands would change every number ORB-IX already reports. This says only
    /// how often it happened, so a feed that arrives out of order is visible rather than
    /// silently folded into the wrong bar.
    /// </summary>
    public long LatePrints => this.latePrints;

    public double LateVolume => this.lateVolume;

    /// <summary>Retained closed-bar footprints, oldest first.</summary>
    public IReadOnlyCollection<BarFootprint> ClosedBarFootprints => this.closedBarFootprints;

    /// <summary>
    /// Diagonal imbalance across the last closed bar.
    ///
    /// Returns <see cref="ImbalanceReading.Empty"/> before any bar has closed, which says
    /// "measured, nothing there" — correct, because no bar has yet traded anything this
    /// module could be imbalanced about.
    /// </summary>
    public ImbalanceReading LastClosedBarImbalance()
        => ImbalanceRule.Summarise(this.EvaluateLastClosedBar(), this.instrument.TickSize);

    /// <summary>Per-price verdicts for the last closed bar, ascending.</summary>
    public IReadOnlyList<ImbalanceLevel> EvaluateLastClosedBar()
        => this.Evaluate(this.lastClosedBarCells);

    /// <summary>Per-price verdicts for the bar currently forming, ascending.</summary>
    public IReadOnlyList<ImbalanceLevel> EvaluateFormingBar()
        => this.Evaluate(this.formingBarCells);

    /// <summary>Per-price verdicts for an arbitrary footprint, ascending.</summary>
    public IReadOnlyList<ImbalanceLevel> Evaluate(IReadOnlyDictionary<double, PriceCell> footprint)
        => ImbalanceRule.Evaluate(
            footprint, this.instrument.TickSize, this.imbalanceRatio, this.imbalanceMinVolume);

    public void OnTick(in TickEvent tick)
    {
        // THE SIZE IS TESTED FOR BEING A NUMBER, not merely for being positive.
        //
        // "tick.Size <= 0" alone does not reject NaN — every comparison against NaN is false —
        // so a size that was not a number passed straight through and totalVolume became NaN
        // permanently, since no later addition can restore it. That is exactly the trap found
        // in OrBuilder.OnBook on 2026-08-22, where a NaN book size poisoned the range's
        // imbalance at every indicator load.
        //
        // LATENT HERE, NOT OBSERVED: 478,424 recorded MNQU6 trade lines carry no null price
        // and no null size. The trade path has never been seen to deliver one — but nothing
        // adjudicates it on the way in either, so the guard states what it means.
        if (!(tick.Size > 0) || double.IsNaN(tick.Price) || double.IsInfinity(tick.Price))
            return;

        // Measured against the last closed bar's CLOSE, not its open: a print stamped INSIDE
        // that bar is just as late as one stamped before it, and both belong to a bar that has
        // already been handed out. Counted before anything else, and it changes nothing
        // downstream — see LatePrints.
        if (this.lastClosedBarCloseUtc > DateTime.MinValue
            && tick.TimestampUtc < this.lastClosedBarCloseUtc)
        {
            this.latePrints++;
            this.lateVolume += tick.Size;
        }

        var price = this.instrument.RoundToTick(tick.Price);

        if (!this.cells.TryGetValue(price, out var cell))
            cell = new PriceCell();

        this.totalVolume += tick.Size;
        this.barVolume += tick.Size;

        // The cell owns how a print lands in a cell; this method owns what it does to the
        // engine's running totals. The two were the same switch written twice — once here and
        // once for the forming bar — and a change to one was a change the other did not get.
        cell.Add(tick.Size, tick.Aggressor);

        switch (tick.Aggressor)
        {
            case Aggressor.Buy:
                this.cumulativeDelta += tick.Size;
                this.barDelta += tick.Size;
                this.classifiedVolume += tick.Size;
                break;

            case Aggressor.Sell:
                this.cumulativeDelta -= tick.Size;
                this.barDelta -= tick.Size;
                this.classifiedVolume += tick.Size;
                break;

            // An unclassified print is counted toward volume and toward the unclassified
            // fraction by the cell, and toward delta by nothing. Guessing a side from the price
            // change alone is a different measurement and would be reported as if it were this
            // one.
        }

        // The bar's running delta extremes, tracked where the running delta already is. These
        // exist only for bars built from prints IN ORDER, which is why a bar seeded from
        // historical levels reports them as not measured rather than as zero.
        if (double.IsNaN(this.barMinDelta) || this.barDelta < this.barMinDelta)
            this.barMinDelta = this.barDelta;

        if (double.IsNaN(this.barMaxDelta) || this.barDelta > this.barMaxDelta)
            this.barMaxDelta = this.barDelta;

        this.cells[price] = cell;

        // The same print, recorded again against the bar in progress. Recorded, not
        // computed: the imbalance itself is evaluated on the fold, never on this path.
        if (!this.formingBarCells.TryGetValue(price, out var barCell))
            barCell = new PriceCell();

        barCell.Add(tick.Size, tick.Aggressor);

        this.formingBarCells[price] = barCell;
    }

    public void OnBar(in BarEvent bar, TimeFrame timeFrame)
    {
        if (!bar.IsClosed)
            return;

        // Price change comes from the bar, which is the authoritative aggregation of what
        // traded over that span. Delta and volume come from the prints this module counted.
        // Deriving price change from the ticks instead would disagree with the bar whenever
        // the tick stream is sparser than the aggregation, and the two sources are for
        // different things: the bar knows where price went, this module knows who pushed it.
        //
        // The bar is folded in only when it closes; comparing a forming bar's delta against
        // a closed bar's price change compares two different spans of time.
        var priceChange = bar.Close - bar.Open;

        this.recentBars.Enqueue(new BarFlow(priceChange, this.barDelta, this.barVolume));

        while (this.recentBars.Count > this.divergenceWindow)
            this.recentBars.Dequeue();

        this.sessionPriceMovement += Math.Abs(priceChange);
        this.sessionVolume += this.barVolume;

        this.barDelta = 0d;
        this.barVolume = 0d;

        // THE FORMING FOOTPRINT IS HANDED OVER, NOT COPIED OUT AND CLEARED. A new dictionary
        // is installed for the next bar so the retained one can never be mutated by a print
        // that arrives after the close — which would silently rewrite the bar a break was
        // already judged on.
        this.lastClosedBarCells = this.formingBarCells.Count > 0
            ? new Dictionary<double, PriceCell>(this.formingBarCells)
            : new Dictionary<double, PriceCell>();

        this.lastClosedMinDelta = this.barMinDelta;
        this.lastClosedMaxDelta = this.barMaxDelta;
        this.lastClosedBarCloseUtc = bar.CloseTimeUtc;

        this.barMinDelta = double.NaN;
        this.barMaxDelta = double.NaN;

        this.closedBarFootprints.Enqueue(new BarFootprint(bar.OpenTimeUtc, this.lastClosedBarCells));

        while (this.closedBarFootprints.Count > this.footprintHistory)
            this.closedBarFootprints.Dequeue();

        this.formingBarCells.Clear();
    }

    public void OnBook(in BookDelta delta)
    {
        // The footprint is built from prints, not from resting size.
    }

    public void OnL3(in L3Event l3)
    {
        // Order-level detail refines absorption elsewhere; it does not change the footprint.
    }

    public void OnSessionPhase(SessionPhase phase)
    {
        // Flow accumulates across the whole session; Reset clears it at the boundary.
    }

    public FeatureOutput Fold(SessionContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        var reading = this.Read();

        if (this.totalVolume <= 0)
            return FeatureOutput.Silent(reading);

        // The score is the direction flow argues for: cumulative delta relative to the
        // volume that produced it, so a large delta on enormous volume is not treated as
        // more convincing than the same delta on modest volume.
        var normalised = this.classifiedVolume > 0
            ? this.cumulativeDelta / this.classifiedVolume
            : 0d;

        var score = (float)Math.Clamp(normalised, -1d, 1d);

        // Confidence is the classified fraction: an unclassifiable tape is a tape this
        // module cannot speak about, and it says so rather than lowering its voice.
        var confidence = (float)Math.Clamp(reading.Classified, 0d, 1d);

        return new FeatureOutput(score, confidence, reading);
    }

    /// <summary>Current flow statistics.</summary>
    public FootprintReading Read()
    {
        var classified = this.totalVolume > 0 ? this.classifiedVolume / this.totalVolume : 0d;

        return new FootprintReading(
            this.cumulativeDelta,
            this.barDelta,
            this.Divergence(),
            this.EffortVersusResult(),
            classified,
            this.LastClosedBarImbalance());
    }

    /// <summary>
    /// Agreement between price direction and flow direction across the recent window.
    ///
    /// Each bar contributes the sign agreement between its price change and its delta,
    /// weighted by its share of volume. A bar that moved up on selling flow contributes
    /// negatively; a bar that did nothing contributes nothing either way.
    /// </summary>
    private double Divergence()
    {
        if (this.recentBars.Count < 2)
            return 0d;

        var weighted = 0d;
        var weight = 0d;

        foreach (var bar in this.recentBars)
        {
            if (bar.Volume <= 0)
                continue;

            var priceSign = Math.Sign(bar.PriceChange);
            var deltaSign = Math.Sign(bar.Delta);

            if (priceSign == 0 || deltaSign == 0)
                continue;

            weighted += priceSign == deltaSign ? bar.Volume : -bar.Volume;
            weight += bar.Volume;
        }

        return weight > 0 ? weighted / weight : 0d;
    }

    /// <summary>
    /// Price movement per unit of volume in the recent window, against the session's own
    /// average.
    ///
    /// One means the recent tape is moving as far per contract as the session has been.
    /// Well below one is effort without result — volume going in without progress, which
    /// is what absorption looks like from the trade tape alone.
    /// </summary>
    private double EffortVersusResult()
    {
        if (this.sessionVolume <= 0 || this.sessionPriceMovement <= 0 || this.recentBars.Count == 0)
            return 1d;

        var recentMovement = this.recentBars.Sum(b => Math.Abs(b.PriceChange));
        var recentVolume = this.recentBars.Sum(b => b.Volume);

        if (recentVolume <= 0)
            return 1d;

        var sessionRate = this.sessionPriceMovement / this.sessionVolume;

        if (sessionRate <= 0)
            return 1d;

        return recentMovement / recentVolume / sessionRate;
    }

    /// <summary>
    /// The price that traded the most volume, and how much. Ties resolve to the lower
    /// price so a replay agrees with the live run rather than depending on iteration order.
    /// </summary>
    public (double Price, double Volume) PointOfControl()
    {
        var bestPrice = double.NaN;
        var bestVolume = -1d;

        foreach (var (price, cell) in this.cells)
        {
            var total = cell.Total;

            if (total > bestVolume || (total == bestVolume && price < bestPrice))
            {
                bestPrice = price;
                bestVolume = total;
            }
        }

        return (bestPrice, bestVolume < 0 ? 0d : bestVolume);
    }

    public void Reset()
    {
        this.cells.Clear();
        this.recentBars.Clear();
        this.formingBarCells.Clear();
        this.closedBarFootprints.Clear();
        this.lastClosedBarCells = new Dictionary<double, PriceCell>();
        this.cumulativeDelta = 0d;
        this.barDelta = 0d;
        this.barVolume = 0d;
        this.classifiedVolume = 0d;
        this.totalVolume = 0d;
        this.sessionPriceMovement = 0d;
        this.sessionVolume = 0d;
        this.barMinDelta = double.NaN;
        this.barMaxDelta = double.NaN;
        this.lastClosedMinDelta = double.NaN;
        this.lastClosedMaxDelta = double.NaN;
        this.latePrints = 0;
        this.lateVolume = 0d;
        this.lastClosedBarCloseUtc = DateTime.MinValue;
    }

    /// <summary>Volume at one price, split by which side initiated it.</summary>
    public struct PriceCell
    {
        public double BuyVolume;
        public double SellVolume;

        /// <summary>Volume that could not be classified against a quote.</summary>
        public double UnclassifiedVolume;

        /// <summary>Largest single print at this price.</summary>
        public double MaxOneTradeVolume;

        /// <summary>Prints that lifted the offer here. NOT contracts — see <see cref="Trades"/>.</summary>
        public int BuyTrades;

        /// <summary>Prints that hit the bid here.</summary>
        public int SellTrades;

        /// <summary>Prints that could not be attributed to a side.</summary>
        public int UnclassifiedTrades;

        /// <summary>
        /// Prints at this price, counted rather than summed.
        ///
        /// A DIFFERENT QUESTION FROM VOLUME, AND THE PAIR IS THE POINT. One 500-lot print and
        /// five hundred 1-lots are the same <see cref="Total"/> and a different market: the
        /// first is one participant, the second is a queue. Volume alone cannot separate them
        /// and neither can <see cref="MaxOneTradeVolume"/> on its own.
        ///
        /// ZERO ON SEEDED BARS, AND THAT IS NOT THE SAME AS "NOTHING TRADED THERE". Historical
        /// per-price volume arrives as volume by side with no trade count, so a bar seeded from
        /// history carries volume with a zero count. Readers must treat a zero count beside a
        /// non-zero volume as UNKNOWN rather than as one enormous print.
        /// </summary>
        public readonly int Trades => this.BuyTrades + this.SellTrades + this.UnclassifiedTrades;

        public readonly double Total => this.BuyVolume + this.SellVolume + this.UnclassifiedVolume;

        public readonly double Delta => this.BuyVolume - this.SellVolume;

        /// <summary>Volume that carried an aggressor. Excludes the unclassified fraction.</summary>
        public readonly double ClassifiedVolume => this.BuyVolume + this.SellVolume;

        /// <summary>
        /// ATAS "Ask": volume where the BUYER aggressed.
        ///
        /// The vocabulary is stated here rather than left to each reader, because "ask volume"
        /// reads naturally as "volume resting on the ask" and means the opposite — volume that
        /// traded INTO the ask. Everything ported from the ATAS toolkit speaks this way.
        /// </summary>
        public readonly double AskVolume => this.BuyVolume;

        /// <summary>ATAS "Bid": volume where the SELLER aggressed.</summary>
        public readonly double BidVolume => this.SellVolume;

        /// <summary>
        /// Mean print size, or zero where nothing traded.
        ///
        /// Zero on a cell seeded from history, where volume arrives without a count — which is
        /// UNKNOWN rather than "no trades". See <see cref="Trades"/>.
        /// </summary>
        public readonly double AverageTrade => this.Trades > 0 ? this.Total / this.Trades : 0d;

        /// <summary>
        /// Adds one print.
        ///
        /// The size and price guards belong to the CALLER: <see cref="FootprintEngine.OnTick"/>
        /// rejects a non-finite price and a size that is not positive before it gets here, and
        /// duplicating that check would put the reason for it in two places.
        /// </summary>
        public void Add(double size, Aggressor aggressor)
        {
            switch (aggressor)
            {
                case Aggressor.Buy:
                    this.BuyVolume += size;
                    this.BuyTrades++;
                    break;

                case Aggressor.Sell:
                    this.SellVolume += size;
                    this.SellTrades++;
                    break;

                default:
                    this.UnclassifiedVolume += size;
                    this.UnclassifiedTrades++;
                    break;
            }

            if (size > this.MaxOneTradeVolume)
                this.MaxOneTradeVolume = size;
        }

        /// <summary>
        /// Adds a whole price level from a historical source.
        ///
        /// Trade counts are taken as given and NOT inferred: a vendor level that carries volume
        /// with no count contributes zero to <see cref="Trades"/>, which is the shape that says
        /// "unknown". Inventing a count of one would claim the level was a single print.
        /// </summary>
        public void AddLevel(
            double buy, double sell, double unclassified,
            int buyTrades, int sellTrades, int unclassifiedTrades, double maxOneTrade)
        {
            this.BuyVolume += buy;
            this.SellVolume += sell;
            this.UnclassifiedVolume += unclassified;
            this.BuyTrades += buyTrades;
            this.SellTrades += sellTrades;
            this.UnclassifiedTrades += unclassifiedTrades;

            if (maxOneTrade > this.MaxOneTradeVolume)
                this.MaxOneTradeVolume = maxOneTrade;
        }

        /// <summary>
        /// Sums another cell into this one, for combining bars or price rows.
        ///
        /// MaxOneTradeVolume takes the larger of the two rather than the sum: it is the biggest
        /// single print seen, and adding two of them would describe a print that never happened.
        /// </summary>
        public void Combine(in PriceCell other)
            => this.AddLevel(
                other.BuyVolume, other.SellVolume, other.UnclassifiedVolume,
                other.BuyTrades, other.SellTrades, other.UnclassifiedTrades, other.MaxOneTradeVolume);

        public override readonly string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "buy {0:N0} sell {1:N0} delta {2:N0}", this.BuyVolume, this.SellVolume, this.Delta);
    }

    private readonly record struct BarFlow(double PriceChange, double Delta, double Volume);

    /// <summary>One closed bar's footprint, kept for display.</summary>
    /// <param name="OpenTimeUtc">Open of the bar these cells belong to.</param>
    /// <param name="Cells">Volume at each price, split by aggressor.</param>
    public readonly record struct BarFootprint(
        DateTime OpenTimeUtc,
        IReadOnlyDictionary<double, PriceCell> Cells);
}
