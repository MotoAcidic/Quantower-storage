using System;
using System.Collections.Generic;

using OrbIx.Core.Abstractions;
using OrbIx.Core.Features;
using OrbIx.Core.Sessions;
using OrbIx.Core.Config;

namespace OrbIx.Core.Flow;

/// <summary>
/// Drives the absorbed Aramid Flow engines and assembles what the chart draws.
///
/// THIS IS THE PIECE THAT LIVED IN A SHELL AND COULD NOT BE TESTED. Aramid Flow ran exactly this
/// logic — scan a closed bar, publish its levels, mark touches, collect hits, assemble a snapshot
/// — inside its Quantower indicator. The settings were checkable and the engines were checkable;
/// what the two produced TOGETHER was not, because reaching it needed a chart. Here it is a plain
/// object driven by plain events, so the suite asserts the assembly and not only the pieces.
///
/// ONE TAPE, ONE BOOK, ONE HISTORY. The builder owns the footprint history and the depth ladder
/// and is fed the same events the rest of the engine already receives. It never subscribes to
/// anything and never reads a clock of its own: every instant it uses is one the caller passed.
///
/// A TOOL THAT IS SWITCHED OFF DOES NO WORK. Every scan is gated on <see cref="FlowDisplay"/>
/// rather than run and then discarded, because these scans walk every price in a bar and the
/// chart folds several times a second.
///
/// NOTHING HERE CLAIMS AN EDGE. Absorption and imbalance are measured nulls on this instrument
/// (trial 008, 25,745 episodes). This assembles a display.
/// </summary>
public sealed class FlowFrameBuilder
{
    /// <summary>
    /// Cluster-search hits kept for drawing.
    ///
    /// Bounded because the search can match on every bar of a multi-day look-back, and an
    /// unbounded list would grow for as long as the chart stayed open. The oldest go first: the
    /// display is about what the search has found recently.
    /// </summary>
    private const int MaxRecentHits = 2000;

    private static readonly AlertRule Silent = new(OnSignal: false, ApproachTicks: 0);

    private readonly FlowConfig config;
    private readonly ISessionBoundary boundary;
    private readonly IBarBoundaries boundaries;
    private readonly FootprintHistory history;
    private readonly DepthLadder ladder;
    private readonly AlertPolicy alerts = new();
    private readonly List<ClusterHit> recentHits = new();
    private readonly List<FlowLevel> provisionalAbsorption = new();
    private readonly List<AlertMessage> pendingAlerts = new();

    private readonly Dictionary<LevelFeature, LevelBook> books = new()
    {
        [LevelFeature.StackedImbalance] = new LevelBook(),
        [LevelFeature.Absorption] = new LevelBook(),
        [LevelFeature.UnfinishedAuction] = new LevelBook(),
        [LevelFeature.ClusterSearch] = new LevelBook(),
        [LevelFeature.VolumeAbsorptionModerate] = new LevelBook(),
        [LevelFeature.VolumeAbsorptionHeavy] = new LevelBook(),
    };

    /// <summary>
    /// The BOOK reading of absorption, built only when the document asks for it.
    ///
    /// IT IS ORB-IX'S OWN ENGINE, NOT A SECOND COPY OF THE RULE. AbsorptionEngine already keeps
    /// the rolling per-price volume and the per-price size history the question needs, and
    /// AbsorptionRule already classifies. Two implementations of one rule is two chances for the
    /// chart to mark an episode the gate never saw, which is the mistake this merge made once
    /// with DiagonalImbalance and is not making again.
    ///
    /// ITS OWN INSTANCE, NOT THE INDICATOR'S. The gate's engine is nulled when the absorption
    /// gate is switched off, and a display that vanished with a gate nobody was looking at is
    /// the same fault as reusing hhllEngine for the trend lines — caught in Phase 5a and caught
    /// by writing this comment rather than by running it.
    /// </summary>
    private AbsorptionEngine? bookAbsorption;

    /// <summary>
    /// The floor measured from this chart's own bars, or null until enough have accumulated.
    ///
    /// RECOMPUTED AS THE SAMPLE GROWS, NOT ONCE. The seed lands a few hundred bars on attach
    /// and the session adds more all day; a calibration taken once at attach would keep a
    /// number measured on the quietest tape available and never improve. It is also not
    /// recomputed per fold — the scan is thirty candidates across every bar held, which is
    /// milliseconds but not free, and the answer does not move between one bar and the next.
    /// </summary>
    private InstrumentFloor? calibration;

    /// <summary>Closed bars held when the calibration above was taken.</summary>
    private int calibratedAt;

    private BigTradeDetector? bigTrades;
    private BigTradeSettings? bigTradeSettings;
    private FootprintBar? forming;
    private DateTime lastClosedBucketUtc = DateTime.MinValue;

    /// <param name="config">The document's settings for these tools.</param>
    /// <param name="boundary">Where session-cumulative statistics reset.</param>
    /// <param name="tickSize">The instrument's price grid.</param>
    /// <param name="barPeriod">
    /// The chart's bar period, for a chart whose bars have one. Builds the same fixed-duration
    /// boundaries this engine derived by arithmetic before <see cref="IBarBoundaries"/> existed.
    /// </param>
    public FlowFrameBuilder(
        FlowConfig config, ISessionBoundary boundary, double tickSize, TimeSpan barPeriod)
        : this(config, boundary, tickSize, new TimeBarBoundaries(barPeriod))
    {
    }

    /// <param name="config">The document's settings for these tools.</param>
    /// <param name="boundary">Where session-cumulative statistics reset.</param>
    /// <param name="tickSize">The instrument's price grid.</param>
    /// <param name="boundaries">
    /// Where this chart's bars begin and end.
    ///
    /// TAKEN RATHER THAN CALCULATED, and that is the whole point. A bar period can only describe
    /// a time-bar chart, so requiring one meant this engine could not be built at all on a tick,
    /// range or Renko chart — measured on the operator's MNQ tick chart 2026-09-14, where 1,413
    /// prints were judged by the aggressor check while the flow frame reported "closed bars 0".
    /// </param>
    public FlowFrameBuilder(
        FlowConfig config, ISessionBoundary boundary, double tickSize, IBarBoundaries boundaries)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        this.boundaries = boundaries ?? throw new ArgumentNullException(nameof(boundaries));

        this.history = new FootprintHistory(tickSize, boundaries);
        this.ladder = new DepthLadder(tickSize);
    }

    /// <summary>Where this chart's bars begin and end.</summary>
    public IBarBoundaries Boundaries => this.boundaries;

    /// <summary>
    /// The fixed duration of every bar, or NULL on a chart whose bars have none.
    ///
    /// Null is an answer, not a missing value: a tick chart's bars have no duration. The one
    /// reader that legitimately needs a duration is the swept stacked-imbalance floor, which was
    /// measured per bar period and therefore applies only where this is non-null.
    /// </summary>
    public TimeSpan? BarPeriod => this.boundaries.FixedPeriod;

    /// <summary>Closed bars held. Exposed so a host can report what it is drawing from.</summary>
    public int ClosedBars => this.history.Closed.Count;

    /// <summary>Whether live bars have arrived, and seeding is therefore closed.</summary>
    public bool LiveStarted => this.history.LiveStarted;

    // ---- feeding ---------------------------------------------------------------------

    /// <summary>
    /// Adds a bar from history, before live bars begin.
    ///
    /// SEEDED BARS RAISE NO ALERTS, which is the whole reason this is a separate method rather
    /// than a flag on the live one. Seeding three days of history would otherwise announce every
    /// level it found, all at once, for things that happened days ago.
    /// </summary>
    /// <returns>How many bars were accepted, and how many refused.</returns>
    public (int Accepted, int Refused) Seed(IReadOnlyList<FootprintBar> bars, FlowDisplay display)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(display);

        var (accepted, refused) = this.history.BackfillClosed(bars);

        // The seed is the largest single jump the sample ever takes, so it is the one moment
        // most worth measuring at rather than waiting for the next fold.
        this.Recalibrate();

        // AND THE SCAN BELOW MUST SEE WHAT WAS JUST MEASURED. The display arrived from before
        // this call; scanning behind its gate would refuse every seeded bar on a chart with no
        // bar period, because there is no swept number there to stand in with.
        display = display.WithCalibration(this.calibration);

        foreach (var bar in this.history.Closed)
            this.ScanClosedBar(bar, display, Silent);

        return (accepted, refused);
    }

    /// <summary>
    /// Takes one print: accumulates it into the forming bar, and closes the previous one when the
    /// print belongs to a later bucket.
    ///
    /// THIS ACCUMULATES A SECOND TIME, ON A DIFFERENT GRID, AND THAT IS NOT THE DUPLICATION PHASE
    /// 2 REFUSED. ORB-IX's FootprintEngine accumulates on the ENTRY timeframe — 15 seconds for the
    /// index products — because that is the timeframe entries are decided on. These displays draw
    /// one column per CHART candle, which is whatever the operator set. Feeding them the entry
    /// grid would put twenty columns under every five-minute candle. The two grids are different
    /// questions about one tape, not two answers to one question, and nothing here feeds anything
    /// that decides a trade.
    ///
    /// A PRINT FOR A BUCKET ALREADY CLOSED IS COUNTED, NEVER FOLDED BACKWARDS. Reopening a closed
    /// bar would republish its levels under a second identity; dropping it silently would leave a
    /// feed that does this often indistinguishable from a quiet one.
    /// </summary>
    public void OnTick(in TickEvent tick, FlowDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);

        if (display.BigTrades)
        {
            this.EnsureBigTrades();
            this.bigTrades?.Add(tick);
        }

        if (!display.AnyEnabled)
            return;

        if (display.Absorption)
            this.BookAbsorptionEngine()?.OnTick(tick);

        var bucket = this.history.BucketOf(tick.TimestampUtc);

        if (bucket <= this.lastClosedBucketUtc)
        {
            this.LatePrints++;
            this.LateVolume += tick.Size;
            return;
        }

        if (this.forming is { } open && open.OpenUtc != bucket)
            this.CloseForming(display);

        this.forming ??= new FootprintBar(
            bucket,
            this.boundaries.CloseOf(bucket),
            this.history.TickSize,
            FootprintSource.LiveTicks,
            // ON A CHART WITH NO FIXED BAR DURATION THE CLOSE IS NOT KNOWN YET. The market
            // decides when a tick or range bar ends, so the best answer available while it
            // fills is how far it has run, and it is marked as such rather than passed off as
            // final. CloseForming replaces it with the real one the moment the bar ends.
            closeIsProvisional: this.boundaries.FixedPeriod is null);

        this.forming.Add(tick);
    }

    /// <summary>The bar still filling, or null when none is.</summary>
    public FootprintBar? Forming => this.forming;

    /// <summary>Prints refused because their bucket had already closed.</summary>
    public long LatePrints { get; private set; }

    /// <summary>Volume on those prints, so the size of what was refused is visible.</summary>
    public double LateVolume { get; private set; }

    /// <summary>
    /// Replaces the book with one the platform was asked for, rather than one accumulated from
    /// its event stream. See <see cref="DepthLadder.ApplySnapshot"/> for why both paths exist.
    /// </summary>
    public void OnBookSnapshot(DepthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.ladder.ApplySnapshot(snapshot);

        // BUILT HERE TOO, NOT ONLY ON THE FIRST PRINT. Creating it lazily on ticks alone threw
        // away every book sample that arrived before the first trade — and those are exactly the
        // ones absorption needs, because the baseline it compares against is the size at the
        // START of the window. On a quiet open the book is read four times a second for a while
        // before anything trades, and all of it was being discarded.
        if (this.BookAbsorptionEngine() is not { } engine)
            return;

        // THE TOUCH, AND ONLY THE TOUCH. BookLadder ignores level indices above zero by design,
        // matching OrBuilder and MicroQuality — and that is right here rather than a limitation
        // to work around: absorption is a question about size being HIT, and a level away from
        // the touch is not being hit by anybody. As price travels, different prices take their
        // turn as the touch, so a session accumulates readings across the range.
        this.FeedTouch(engine, snapshot.Utc, this.ladder.BestBid, BookSide.Bid);
        this.FeedTouch(engine, snapshot.Utc, this.ladder.BestAsk, BookSide.Ask);
    }

    private void FeedTouch(AbsorptionEngine engine, DateTime utc, double price, BookSide side)
    {
        if (!double.IsFinite(price))
            return;

        var size = 0d;

        foreach (var level in this.ladder.Levels(side, 1))
        {
            if (level.Price == price)
                size = level.Size;
        }

        engine.OnBook(new BookDelta(utc, side, price, size, levelIndex: 0, orderCount: 0));
    }

    /// <summary>
    /// The book-reading engine, built on demand, or null when the document asks for the
    /// footprint reading instead.
    ///
    /// ONE DECISION IN ONE PLACE. The source was checked at the tick, at the snapshot AND at the
    /// read, and a mutation removing any one of them survived the suite — because the other two
    /// still happened to cover it. Three guards nobody can fail independently are one guard and
    /// two pieces of decoration. The engine's EXISTENCE is now the decision, and everything
    /// downstream asks only whether it exists.
    /// </summary>
    private AbsorptionEngine? BookAbsorptionEngine()
    {
        if (this.config.Absorption.Source != AbsorptionSource.Book)
            return null;

        if (this.bookAbsorption is { } existing)
            return existing;

        // ONLY THE PRICE GRID IS USED. The engine touches RoundToTick and the price-scale guard
        // and nothing else on this spec — checked, not assumed — so the identity fields are left
        // empty rather than filled with a symbol this type does not know and would have to
        // invent.
        var grid = new InstrumentSpec(
            SymbolId: string.Empty, Root: string.Empty, Tier: string.Empty,
            TickSize: this.history.TickSize, TickValue: 0d);

        this.bookAbsorption = new AbsorptionEngine(
            grid,
            TimeSpan.FromSeconds(this.config.Absorption.BookWindowSeconds),
            this.config.Absorption.BookCancelledBelow,
            this.config.Absorption.BookAbsorbedAtOrAbove);

        return this.bookAbsorption;
    }

    /// <summary>Takes one depth update. Returns false when it carried no usable price or size.</summary>
    public bool OnBook(in DepthUpdate update) => this.ladder.Apply(update);

    /// <summary>
    /// Advances anything that completes on the clock rather than on an event.
    ///
    /// THE CUMULATIVE BIG-TRADE GROUP IS THE ONE THING THAT DOES. In that mode consecutive
    /// same-side prints inside the aggregation window are ONE trade, so a group stays open until
    /// either the other side prints or the window elapses — and on a quiet tape only the clock can
    /// say which. A test caught this: a 250-lot print sat in an open group and never reached the
    /// display at all.
    /// </summary>
    /// <param name="nowUtc">The instant to judge against.</param>
    /// <param name="grace">How long past a bar's end to wait for prints still in flight.</param>
    /// <param name="display">Which tools are drawing.</param>
    public void OnClock(DateTime nowUtc, TimeSpan grace, FlowDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);

        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grace), grace, "A grace period cannot be negative.");
        }

        if (display.BigTrades)
        {
            this.EnsureBigTrades();
            this.bigTrades?.Flush(nowUtc);
        }

        this.Recalibrate();
        display = display.WithCalibration(this.calibration);

        this.ReadBookAbsorption(nowUtc, display);

        // ON A QUIET TAPE ONLY THE CLOCK CAN SAY A BAR ENDED. Without this a bar that finished at
        // 13:31 would stay open until something traded, and every level it contains would wait
        // with it — which is exactly when a display is most worth reading.
        // ONLY A BAR WITH A DURATION CAN BE LATE. This exists because a quiet tape leaves a
        // time bar open past its own period, and the clock closes it so the levels inside it
        // stop waiting for a print that is not coming. A tick, range or Renko bar has no period
        // to be past: it ends when the market says so, and closing it on a clock would cut bars
        // in half whenever the tape went quiet -- which is precisely when it would look like a
        // genuine reading.
        if (this.boundaries.FixedPeriod is not null
            && this.forming is { } open
            && nowUtc >= open.CloseUtc + grace)
        {
            this.CloseForming(display);
        }
    }

    /// <summary>
    /// Reads absorption at both touches and publishes what qualifies.
    ///
    /// ON THE CLOCK RATHER THAN AT BAR CLOSE, because this is a question about a rolling span —
    /// "did size hold over the last five seconds" — and a bar neither opens nor closes that
    /// window. A bar-aligned read would ask it once a minute and miss every episode that opened
    /// and resolved inside one.
    ///
    /// THE LEVEL IS STILL ATTRIBUTED TO A BAR, because the bar is part of its identity: the book
    /// is read four times a second, and without it one episode would put a fresh line on the
    /// chart on every fold until the level book turned the display solid.
    /// </summary>
    private void ReadBookAbsorption(DateTime nowUtc, FlowDisplay display)
    {
        // No source check: the engine exists only when the document asked for this reading.
        if (!display.Absorption || this.bookAbsorption is not { } engine)
            return;

        var levels = BookAbsorptionScan.Scan(
            engine.Read(nowUtc),
            this.history.BucketOf(nowUtc),
            new BookAbsorptionSettings(this.config.Absorption.BookMinVolume));

        if (levels.Count > 0)
            this.Publish(LevelFeature.Absorption, levels, this.config.Absorption.Alerts, "Absorption");
    }

    private void CloseForming(FlowDisplay display)
    {
        if (this.forming is not { } bar)
            return;

        this.forming = null;
        this.lastClosedBucketUtc = bar.OpenUtc;

        // A bar nothing traded in is an absence, not a bar. Publishing it would put an empty
        // statistics column on the chart and offer the scans a bar with no diagonals.
        if (!bar.HasPrints)
            return;

        // THE TRUE END, TAKEN RATHER THAN ESTIMATED. This bar is closing because a print
        // arrived in a LATER bar, so the boundaries already hold that later bar's open -- which
        // is exactly when this one ended. On a fixed-duration chart the close was known at
        // construction and stands.
        if (bar.CloseIsProvisional)
            bar.Freeze(this.boundaries.CloseOf(bar.OpenUtc));
        else
            bar.Freeze();

        if (this.history.Append(bar))
            this.ScanClosedBar(bar, display, silence: null);
    }

    /// <summary>
    /// Alerts raised since the last drain, and the list is emptied by reading it.
    ///
    /// CORE CANNOT RAISE AN ALERT — that is a platform call — so it says WHAT to raise and the
    /// shell decides how. Draining rather than exposing a growing list is what stops an alert
    /// being raised twice by a caller that read the list and forgot to clear it.
    /// </summary>
    public IReadOnlyList<AlertMessage> DrainAlerts()
    {
        if (this.pendingAlerts.Count == 0)
            return Array.Empty<AlertMessage>();

        var drained = this.pendingAlerts.ToArray();
        this.pendingAlerts.Clear();
        return drained;
    }

    /// <summary>Raises approach alerts for levels price has come near.</summary>
    public void OnPrice(double lastPrice, FlowDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);

        if (!double.IsFinite(lastPrice))
            return;

        this.Approach(LevelFeature.StackedImbalance, display.StackedImbalance,
            this.config.StackedImbalance.Alerts, "Stacked imbalance", lastPrice);
        this.Approach(LevelFeature.Absorption, display.Absorption,
            this.config.Absorption.Alerts, "Absorption", lastPrice);
        this.Approach(LevelFeature.UnfinishedAuction, display.UnfinishedAuction,
            this.config.UnfinishedAuction.Alerts, "Unfinished auction", lastPrice);
        this.Approach(LevelFeature.ClusterSearch, display.ClusterSearch,
            this.config.ClusterSearch.Alerts, "Cluster search", lastPrice);
    }

    // ---- assembling ------------------------------------------------------------------

    /// <summary>
    /// Everything the chart draws, as of an instant.
    ///
    /// BUILT FRESH EVERY FOLD rather than kept and patched. The switches change under the
    /// operator's hand and the book moves continuously; a frame that outlived the state it
    /// described would draw a chart that no longer exists.
    /// </summary>
    /// <param name="display">Which tools are drawing, and what their floors resolved to.</param>
    /// <param name="nowUtc">The instant the frame describes.</param>
    /// <param name="bias">
    /// Reference geometry over the chart's bars, which this builder does not own — it holds the
    /// footprint and the book, not the chart. Null carries <see cref="FlowBias.Empty"/>.
    /// </param>
    public FlowFrame Build(FlowDisplay display, DateTime nowUtc, FlowBias? bias = null)
    {
        ArgumentNullException.ThrowIfNull(display);

        var forming = this.forming;

        if (!display.AnyEnabled)
            return FlowFrame.Empty;

        var statRows = display.ClusterStatistics
            ? ToArray(this.config.ClusterStatistics.Rows)
            : Array.Empty<StatRow>();

        var columns = display.ClusterStatistics
            ? ClusterStatisticsEngine.Compute(this.history.Closed, forming, this.boundary, nowUtc)
            : Array.Empty<StatColumn>();

        var spans = new Dictionary<LevelFeature, LevelSpan[]>();

        this.AddSpans(spans, LevelFeature.StackedImbalance, display.StackedImbalance,
            this.config.StackedImbalance.Visibility, nowUtc);
        this.AddSpans(spans, LevelFeature.Absorption, display.Absorption,
            this.config.Absorption.Visibility, nowUtc);
        this.AddSpans(spans, LevelFeature.UnfinishedAuction, display.UnfinishedAuction,
            this.config.UnfinishedAuction.Visibility, nowUtc);

        // EXTENT IS UNTIL-TOUCH BY CONSTRUCTION, NOT BY CONFIGURATION. The operator's rule is
        // that an absorption line runs right "until price trades through", and TouchedUtc is
        // stamped by exactly that -- see RetireTradedThrough. Offering the other extents here
        // would let a setting contradict the rule the levels are retired under.
        var absorptionVisibility = new LevelVisibility(
            LevelExtent.UntilTouch, PrintBars: 0, this.config.VolumeAbsorption.DaysLookBack);

        this.AddSpans(spans, LevelFeature.VolumeAbsorptionModerate, display.VolumeAbsorptionTier1,
            absorptionVisibility, nowUtc);
        this.AddSpans(spans, LevelFeature.VolumeAbsorptionHeavy, display.VolumeAbsorptionTier2,
            absorptionVisibility, nowUtc);

        // The price line is a separate switch from the search itself: the presenter's "X marks
        // the spot" can be off while the markers stay on.
        this.AddSpans(spans, LevelFeature.ClusterSearch,
            display.ClusterSearch && this.config.ClusterSearch.ShowPriceLevel,
            this.config.ClusterSearch.Visibility, nowUtc);

        return new FlowFrame(
            columns,
            statRows,
            spans,
            display.Absorption ? this.provisionalAbsorption.ToArray() : Array.Empty<FlowLevel>(),
            display.ClusterSearch ? this.recentHits.ToArray() : Array.Empty<ClusterHit>(),
            display.BigTrades && this.bigTrades is { } detector
                ? ToArray(detector.Trades)
                : Array.Empty<BigTrade>(),
            display.LiveCounter ? this.CounterAt(forming, nowUtc) : CounterReading.Empty,
            display.DomLevels ? this.BuildDom() : DomReading.Empty,
            this.history.Closed.Count,
            bias ?? FlowBias.Empty,
            this.bookAbsorption?.Diagnostics.ToString());
    }

    /// <summary>
    /// The counter's reading at an instant: the forming bar when one exists, and otherwise the
    /// bucket that is open right now, at zero.
    ///
    /// WHY THE SECOND CASE EXISTS. A bar is created by its first print, and closed either by a
    /// print for a later bucket or by the clock. Between those two events there is no forming
    /// bar, so the counter had nothing to report and the overlay drew nothing — the live delta
    /// vanished at every bar boundary and came back with the next trade. The bucket is open the
    /// whole time; only the prints were missing, and that is what this says.
    ///
    /// A CLOSED BUCKET IS NEVER REOPENED. If the clock's bucket is one the builder has already
    /// closed — which happens when prints run ahead of the fold clock and close a bucket the
    /// clock has not reached — there is no open bucket and the counter stays empty rather than
    /// contradicting the history.
    /// </summary>
    private CounterReading CounterAt(FootprintBar? forming, DateTime nowUtc)
    {
        var reading = CounterReading.From(forming);

        if (reading.HasBar)
            return reading;

        var bucket = this.history.BucketOf(nowUtc);

        return bucket > this.lastClosedBucketUtc ? CounterReading.Idle(bucket) : CounterReading.Empty;
    }

    /// <summary>
    /// Measures this instrument's own volume floor, when the document asks for a calibrated one
    /// and the sample has grown enough to be worth re-measuring.
    ///
    /// THE GROWTH GATE IS A FIFTH. Re-measuring after every bar would spend the scan thirty
    /// times over to move a floor by nothing; waiting for a fixed count would keep a figure
    /// taken on 60 bars while 600 sat available. A proportional step recomputes often while the
    /// sample is small and rarely once it is large, which is where the estimate stops moving.
    /// </summary>
    private void Recalibrate()
    {
        if (this.config.StackedImbalance.Thresholds.MinVolumeSource != ImbalanceFloorSource.Calibrated)
            return;

        var held = this.history.Closed.Count;

        if (held < InstrumentCalibration.MinimumBars)
            return;

        if (this.calibration is { Measured: true } && held < this.calibratedAt + (this.calibratedAt / 5))
            return;

        this.calibratedAt = held;

        this.calibration = InstrumentCalibration.Measure(
            this.history.Closed,
            this.history.TickSize,
            this.config.Calibration.SessionLength,
            this.config.StackedImbalance.Thresholds.Ratio,
            this.config.StackedImbalance.Thresholds.MinLevels,
            this.config.StackedImbalance.Thresholds.IgnoreZero,
            this.config.StackedImbalance.Thresholds.TargetMarksPerSession);
    }

    /// <summary>What was measured from this chart's own bars, for the status line.</summary>
    public InstrumentFloor? Calibration => this.calibration;

    /// <summary>
    /// Absorption on the FORMING bar, when the settings ask for it.
    ///
    /// Kept apart from the level books because these are not levels yet: the bar can still
    /// change and they can vanish before it closes. Publishing them would put a permanent line
    /// on the chart for something that never happened.
    /// </summary>
    public void ScanFormingBar(FlowDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);

        this.provisionalAbsorption.Clear();

        if (!display.Absorption || !this.config.Absorption.JudgeFormingBar || this.forming is not { } forming)
            return;

        this.provisionalAbsorption.AddRange(StackedImbalanceScan.Scan(
            forming,
            this.config.Absorption.Thresholds.ToSettings(display.AbsorptionFloor),
            LevelFeature.Absorption));
    }

    /// <summary>Forgets everything. For a symbol change, where nothing held is about this market.</summary>
    public void Reset()
    {
        this.history.Reset();
        this.ladder.Clear();
        this.forming = null;
        this.lastClosedBucketUtc = DateTime.MinValue;
        this.LatePrints = 0;
        this.LateVolume = 0d;
        this.recentHits.Clear();
        this.provisionalAbsorption.Clear();
        this.pendingAlerts.Clear();
        this.bigTrades?.Clear();
        this.bookAbsorption = null;
        this.calibration = null;
        this.calibratedAt = 0;

        foreach (var book in this.books.Values)
            book.Clear();
    }

    // ---- the scans -------------------------------------------------------------------

    /// <summary>Runs every enabled scan over one closed bar and publishes what it found.</summary>
    /// <param name="bar">The bar that closed.</param>
    /// <param name="display">Which tools are drawing.</param>
    /// <param name="silence">
    /// The rule to use instead of the configured one, or null to use the configured one. Seeded
    /// history passes <see cref="Silent"/>: three days of backfill would otherwise announce every
    /// level it found, all at once, for things that happened days ago.
    /// </param>
    private void ScanClosedBar(FootprintBar bar, FlowDisplay display, AlertRule? silence)
    {
        // ONE SOURCE AT A TIME, because they are answers to different questions that would sit
        // in one colour on one chart. The footprint reading is what Aramid Flow drew and it stays
        // reachable, unchanged, through the document.
        if (display.Absorption && this.config.Absorption.Source == AbsorptionSource.Footprint)
        {
            this.Publish(
                LevelFeature.Absorption,
                StackedImbalanceScan.Scan(
                    bar,
                    this.config.Absorption.Thresholds.ToSettings(display.AbsorptionFloor),
                    LevelFeature.Absorption),
                silence ?? this.config.Absorption.Alerts,
                "Absorption");
        }

        // THE FLOOR COMES FROM THE SAME OBJECT AS THE GATE ABOVE IT, and that is the whole point
        // of reading it from the display rather than re-resolving it here. Re-resolving is an
        // EQUIVALENT MUTANT while the refreshes in Seed and OnClock stand -- both routes end at
        // the same calibration -- so no test kills it and none should be invented to. What it
        // buys is that the pair cannot drift apart again: the previous arrangement resolved the
        // gate in FlowDisplay and the number here, and they disagreed for a fold at a time while
        // each looked correct on its own.
        if (display.StackedImbalance)
        {
            this.Publish(
                LevelFeature.StackedImbalance,
                StackedImbalanceScan.Scan(
                    bar,
                    this.config.StackedImbalance.Thresholds.ToSettings(display.StackedFloor),
                    LevelFeature.StackedImbalance),
                silence ?? this.config.StackedImbalance.Alerts,
                "Stacked imbalance");
        }

        // HEAVY VOLUME AT A PRICE THE BAR COULD NOT LEAVE. Scanned once and published into two
        // books, because a level draws at the highest tier it reached and each tier has its own
        // switch. Scanning once and splitting is not an optimisation: scanning twice at two
        // thresholds would collapse each tier's zones SEPARATELY, so a moderate and a heavy mark
        // two ticks apart would both survive and the chart would show two walls where the bar
        // found one.
        if (display.VolumeAbsorptionTier1 || display.VolumeAbsorptionTier2)
        {
            var absorption = VolumeAbsorptionScan.Scan(
                bar,
                this.config.VolumeAbsorption.ToSettings(),
                LevelFeature.VolumeAbsorptionModerate,
                LevelFeature.VolumeAbsorptionHeavy);

            this.PublishByFeature(absorption, silence, display);
        }

        if (display.UnfinishedAuction)
        {
            this.Publish(
                LevelFeature.UnfinishedAuction,
                UnfinishedAuctionScan.Scan(bar, this.config.UnfinishedAuction.ToSettings()),
                silence ?? this.config.UnfinishedAuction.Alerts,
                "Unfinished auction");
        }

        if (display.ClusterSearch && this.config.ClusterSearch.UsePreviousClose)
            this.ScanClusterSearch(bar, silence);

        // TOUCHES ARE MARKED WHATEVER IS SWITCHED ON. A level's life is a fact about the market,
        // not about the display: a tool switched off for a while and back on must not show a line
        // that price went through in between as though it still stood.
        foreach (var (feature, book) in this.books)
        {
            // ABSORPTION LINES END WHEN PRICE GETS PAST THEM, NOT WHEN IT REACHES THEM. Every
            // other level here marks a place price came back to; an absorption line marks size
            // that held, and it stands until that size stops holding. Retiring it on a touch
            // would end the line at the first retest -- the moment it is most worth seeing.
            if (feature is LevelFeature.VolumeAbsorptionModerate or LevelFeature.VolumeAbsorptionHeavy)
                book.RetireTradedThrough(bar);
            else
                book.MarkTouches(bar);
        }

        if (silence is null && display.ClusterStatistics && this.config.ClusterStatistics.Alerts.Count > 0)
            this.RaiseStatisticsAlerts(bar);
    }

    private void RaiseStatisticsAlerts(FootprintBar closed)
    {
        var thresholds = new List<(StatRow Row, double Threshold)>(this.config.ClusterStatistics.Alerts.Count);

        foreach (var (row, threshold) in this.config.ClusterStatistics.Alerts)
            thresholds.Add((row, threshold));

        var columns = ClusterStatisticsEngine.Compute(
            this.history.Closed, forming: null, this.boundary, closed.CloseUtc);

        if (columns.Length == 0)
            return;

        this.pendingAlerts.AddRange(this.alerts.OnStatistics(columns[^1], thresholds));
    }

    private void ScanClusterSearch(FootprintBar target, AlertRule? silence)
    {
        var settings = this.config.ClusterSearch.ToSettings(this.SearchZone());
        var closed = this.history.Closed;

        // The bars that PRECEDE the target, located by the target's own open — a seeded target
        // sits anywhere in the history, not necessarily at its end.
        var end = target.IsClosed ? this.history.IndexOfClosed(target.OpenUtc) : closed.Count;

        if (target.IsClosed && end < 0)
            return;

        var window = new List<FootprintBar>(settings.BarsRange);

        for (var i = Math.Max(0, end - (settings.BarsRange - 1)); i < end; i++)
            window.Add(closed[i]);

        window.Add(target);

        var hits = ClusterSearchEngine.Scan(window, settings);

        if (hits.Count == 0)
            return;

        var fresh = new List<FlowLevel>();

        foreach (var hit in hits)
        {
            var level = hit.ToLevel(hit.Cell.Delta >= 0 ? LevelSide.Bullish : LevelSide.Bearish);

            if (this.books[LevelFeature.ClusterSearch].Add(level))
            {
                fresh.Add(level);
                this.recentHits.Add(hit);
            }
        }

        while (this.recentHits.Count > MaxRecentHits)
            this.recentHits.RemoveAt(0);

        if (fresh.Count > 0)
        {
            this.pendingAlerts.AddRange(this.alerts.OnNewLevels(
                fresh, silence ?? this.config.ClusterSearch.Alerts, "Cluster search"));
        }
    }

    /// <summary>
    /// Publishes a mixed set of levels into the book each one names.
    ///
    /// A tiered scan returns both tiers at once, and each carries the feature it reached. Sorting
    /// them here keeps the tier decision in the scan, where the thresholds are, instead of
    /// duplicating it at the call site.
    /// </summary>
    private void PublishByFeature(IReadOnlyList<FlowLevel> levels, AlertRule? silence, FlowDisplay display)
    {
        if (levels.Count == 0)
            return;

        var moderate = new List<FlowLevel>();
        var heavy = new List<FlowLevel>();

        foreach (var level in levels)
        {
            if (level.Feature == LevelFeature.VolumeAbsorptionHeavy)
                heavy.Add(level);
            else
                moderate.Add(level);
        }

        // A TIER THAT IS SWITCHED OFF IS NOT RECORDED, so switching it on later does not
        // suddenly produce lines from bars nobody was watching it on.
        if (display.VolumeAbsorptionTier1 && moderate.Count > 0)
            this.Publish(LevelFeature.VolumeAbsorptionModerate, moderate, silence ?? Silent, "Absorption t1");

        if (display.VolumeAbsorptionTier2 && heavy.Count > 0)
            this.Publish(LevelFeature.VolumeAbsorptionHeavy, heavy, silence ?? Silent, "Absorption t2");
    }

    private void Publish(
        LevelFeature feature, IReadOnlyList<FlowLevel> levels, AlertRule rule, string featureName)
    {
        if (levels.Count == 0)
            return;

        var book = this.books[feature];
        var fresh = new List<FlowLevel>();

        foreach (var level in levels)
        {
            if (book.Add(level))
                fresh.Add(level);
        }

        if (fresh.Count > 0)
            this.pendingAlerts.AddRange(this.alerts.OnNewLevels(fresh, rule, featureName));
    }

    private void Approach(
        LevelFeature feature, bool enabled, AlertRule rule, string featureName, double lastPrice)
    {
        if (!enabled || rule.ApproachTicks <= 0)
            return;

        this.pendingAlerts.AddRange(this.alerts.OnPrice(
            this.books[feature].All, lastPrice, this.history.TickSize, rule, featureName));
    }

    private void AddSpans(
        Dictionary<LevelFeature, LevelSpan[]> spans,
        LevelFeature feature,
        bool enabled,
        LevelVisibility visibility,
        DateTime nowUtc)
    {
        if (!enabled)
            return;

        spans[feature] = ToArray(this.books[feature].Visible(feature, visibility, this.boundaries, nowUtc));
    }

    private DomReading BuildDom()
    {
        var dom = this.config.DomLevels;

        // A FABRICATED LEVEL-1 BOOK HAS NO DEPTH TO READ. Its one price per side IS the touch,
        // so "the largest resting level" would be a statement about a book nobody published. The
        // counts still go through, so the display can say what it is looking at.
        if (this.ladder.SyntheticLevelOne && !this.ladder.PerOrderMode)
        {
            return DomReading.Empty with
            {
                SyntheticLevelOne = true,
                BidLevels = this.ladder.BidLevels,
                AskLevels = this.ladder.AskLevels,
                Updates = this.ladder.UpdatesApplied,
                LastUpdateUtc = this.ladder.LastUpdateUtc,
            };
        }

        return new DomReading(
            this.LargestOver(BookSide.Bid, dom),
            this.LargestOver(BookSide.Ask, dom),
            dom.Profile ? ToArray(this.ladder.Levels(BookSide.Bid, dom.WithinLevels)) : Array.Empty<DepthLevel>(),
            dom.Profile ? ToArray(this.ladder.Levels(BookSide.Ask, dom.WithinLevels)) : Array.Empty<DepthLevel>(),
            dom.LargestOrder ? this.ladder.LargestOrder(BookSide.Bid) : null,
            dom.LargestOrder ? this.ladder.LargestOrder(BookSide.Ask) : null,
            this.ladder.PerOrderMode,
            this.ladder.SyntheticLevelOne,
            this.ladder.BidLevels,
            this.ladder.AskLevels,
            this.ladder.UpdatesApplied,
            this.ladder.LastUpdateUtc);
    }

    private DepthLevel[] LargestOver(BookSide side, FlowDomConfig dom)
    {
        var kept = new List<DepthLevel>(dom.TopPerSide);

        foreach (var level in this.ladder.Largest(side, dom.TopPerSide, dom.WithinLevels))
        {
            if (level.Size >= dom.VolumeFilter)
                kept.Add(level);
        }

        return kept.ToArray();
    }

    /// <summary>
    /// The zone the cluster search's time filter is expressed in.
    ///
    /// UTC WHEN THE FILTER IS OFF, and that is not a fallback that could go unnoticed:
    /// <see cref="ClusterSearchSettings.Validate"/> refuses a filter with no zone, and the host
    /// supplies the document's session zone when one is in use. A builder constructed without a
    /// zone therefore cannot silently filter on the wrong one — it can only not filter.
    /// </summary>
    private TimeZoneInfo SearchZone() => this.SessionZone ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The zone wall-clock settings are read in. Set by the host from the document's
    /// sessionTimeZone; null leaves the time filter unusable rather than wrong.
    /// </summary>
    public TimeZoneInfo? SessionZone { get; set; }

    private void EnsureBigTrades()
    {
        var wanted = this.config.BigTrades.ToSettings();

        // REBUILT WHEN THE SETTINGS MOVE, because the detector holds a part-built group whose
        // membership was decided by the old thresholds. Keeping it would mix two rules into one
        // trade.
        if (this.bigTrades is not null && this.bigTradeSettings == wanted)
            return;

        this.bigTrades = new BigTradeDetector(wanted);
        this.bigTradeSettings = wanted;
    }

    private static T[] ToArray<T>(IReadOnlyList<T> list)
    {
        if (list.Count == 0)
            return Array.Empty<T>();

        var array = new T[list.Count];

        for (var i = 0; i < list.Count; i++)
            array[i] = list[i];

        return array;
    }
}
