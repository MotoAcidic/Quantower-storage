using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Features;

/// <summary>
/// Absorption at the touch on both sides, over a rolling window.
/// </summary>
/// <param name="Bid">The resting bid at the touch, absorbing aggressive selling.</param>
/// <param name="Ask">The resting ask at the touch, absorbing aggressive buying.</param>
/// <param name="BookKnown">
/// Whether the book could be vouched for at all. False after a withdrawal, and distinct
/// from both sides reading <see cref="AbsorptionState.Quiet"/>.
/// </param>
public sealed record AbsorptionSnapshot(
    AbsorptionReading Bid,
    AbsorptionReading Ask,
    bool BookKnown)
{
    /// <summary>Nothing observed yet.</summary>
    public static AbsorptionSnapshot Empty { get; } = new(
        AbsorptionReading.Unmeasured(double.NaN, BookSide.Bid),
        AbsorptionReading.Unmeasured(double.NaN, BookSide.Ask),
        BookKnown: false);

    /// <summary>
    /// The side favourable to an UP move, per trial 008's convention: resting BIDS being hit
    /// by aggressive SELLERS and holding is what supports price from below.
    /// </summary>
    public AbsorptionReading For(bool longSide) => longSide ? this.Bid : this.Ask;
}

/// <summary>
/// Why the module could not speak, counted.
///
/// UNMEASURED HAS SEVERAL CAUSES AND THEY CALL FOR DIFFERENT FIXES. A dark depth tier, a
/// touch the ladder cannot name, and a touch with no observation one window ago are three
/// different faults; a single Unmeasured count cannot tell them apart, and guessing between
/// them is exactly what produced two refuted explanations before this existed.
/// </summary>
/// <param name="Reads">Times the module was asked.</param>
/// <param name="BookUnknown">Asked while the book could not be vouched for.</param>
/// <param name="NoTouch">Sides where the ladder held no price with size on it.</param>
/// <param name="NoBaseline">Sides where the touch had no observation at or before the cutoff.</param>
/// <param name="NoCurrentSize">Sides where the touch had no current size. Should not occur.</param>
/// <param name="Measured">Sides that produced a real verdict.</param>
public sealed record AbsorptionDiagnostics(
    long Reads,
    long BookUnknown,
    long NoTouch,
    long NoBaseline,
    long NoCurrentSize,
    long Measured)
{
    /// <summary>Sides examined across all reads: two per read where the book was known.</summary>
    public long Sides => this.NoTouch + this.NoBaseline + this.NoCurrentSize + this.Measured;

    /// <summary>One line, for a run summary.</summary>
    public override string ToString()
        => $"reads {this.Reads:N0} · book unknown {this.BookUnknown:N0} · "
           + $"sides: measured {this.Measured:N0}, no touch {this.NoTouch:N0}, "
           + $"no baseline {this.NoBaseline:N0}, no current size {this.NoCurrentSize:N0}";
}

/// <summary>
/// M06. Did resting size HOLD at the touch while volume traded through it.
///
/// TWO STREAMS, ONE QUESTION. The tape says how much traded at a price; the book says how
/// much size was there before and after. Neither alone answers it, which is why this module
/// requires the depth tier and reports Unmeasured rather than a number when depth is dark.
///
/// AGGREGATE, BECAUSE PER-ORDER IS MEASURED INAPPLICABLE HERE. On MNQ the median resting
/// order is ONE lot, 98.7% of hit orders are fully consumed and 75.9% die inside 100 ms —
/// there is no large order to watch being absorbed, only hundreds of one-lots eaten and
/// replaced. Trial 008 concluded "aggregate measures only", and this is one.
///
/// THE TOUCH AND NOTHING DEEPER, for the reason stated on <see cref="BookLadder"/>: live
/// Level 2 sends no level index and the capture is top-of-book only, so anything deeper
/// would exist on the chart and be unreproducible offline.
///
/// STANDING AGAINST-EVIDENCE: absorption is a MEASURED NULL on MNQ (trial 008, 25,745
/// episodes, below the 2.76-tick cost floor even taken at face value). This module ships
/// because the operator chose to gate on it after being shown that; it records its verdict
/// and its components on every signal so the question is answerable from forward sessions.
/// </summary>
public sealed class AbsorptionEngine : IFeatureModule
{
    private readonly BookLadder ladder;
    private readonly TimeSpan window;
    private readonly double cancelledBelow;
    private readonly double absorbedAtOrAbove;
    private readonly InstrumentSpec instrument;

    // Prints inside the window, oldest first. At the measured MNQ rate of roughly a hundred
    // prints a second this holds a few hundred entries, so the linear walk below is cheaper
    // than the bookkeeping a keyed structure would need.
    private readonly Queue<Print> prints = new();

    private DateTime lastTickUtc = DateTime.MinValue;

    private long reads;
    private long bookUnknown;
    private long noTouch;
    private long noBaseline;
    private long noCurrentSize;
    private long measured;

    public AbsorptionEngine(
        InstrumentSpec instrument,
        TimeSpan? window = null,
        double cancelledBelow = AbsorptionRule.DefaultCancelledBelow,
        double absorbedAtOrAbove = AbsorptionRule.DefaultAbsorbedAtOrAbove,
        int maxPricesPerSide = BookLadder.DefaultMaxPricesPerSide)
    {
        instrument.RequirePriceScale(nameof(instrument));

        this.window = window ?? AbsorptionRule.DefaultWindow;

        if (this.window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window), this.window,
                "Absorption asks whether size held over a SPAN: over a millisecond nothing is "
                + "ever absorbed and over an hour everything is.");
        }

        if (!(cancelledBelow > 0) || !(absorbedAtOrAbove > cancelledBelow))
        {
            throw new ArgumentOutOfRangeException(
                nameof(absorbedAtOrAbove), absorbedAtOrAbove,
                "The absorbed threshold must sit above the cancelled one, and both above zero.");
        }

        this.instrument = instrument;
        this.cancelledBelow = cancelledBelow;
        this.absorbedAtOrAbove = absorbedAtOrAbove;

        // THE LADDER RETAINS LONGER THAN THE WINDOW IT SERVES. Retaining exactly the window
        // would leave the sample that answers "what was the size when this window opened"
        // eligible for trimming at the very instant it is needed.
        this.ladder = new BookLadder(this.window + this.window, maxPricesPerSide);
    }

    public string Id => "M06";

    /// <summary>Price-aggregated depth. Without it this module reports Unmeasured, never zero.</summary>
    public DataTier Requires => DataTier.T3;

    public bool Enabled { get; set; } = true;

    /// <summary>The book as this module has reconstructed it, for diagnostics and display.</summary>
    public BookLadder Book => this.ladder;

    /// <summary>Why this module could not speak, counted rather than inferred.</summary>
    public AbsorptionDiagnostics Diagnostics => new(
        this.reads, this.bookUnknown, this.noTouch,
        this.noBaseline, this.noCurrentSize, this.measured);

    public void OnTick(in TickEvent tick)
    {
        // Guarded for being a NUMBER, not merely positive: every comparison against NaN is
        // false, so "size <= 0" alone lets one through — the trap already paid for in
        // OrBuilder.OnBook and FootprintEngine.OnTick.
        if (!(tick.Size > 0) || double.IsNaN(tick.Price) || double.IsInfinity(tick.Price))
            return;

        // UNCLASSIFIED PRINTS STILL COUNT AS VOLUME THROUGH THE LEVEL. Absorption asks how
        // much traded at a price, not who initiated it; the aggressor decides which SIDE of
        // the book was resting there, and that comes from the price against the touch rather
        // than from this flag.
        this.lastTickUtc = tick.TimestampUtc;
        this.prints.Enqueue(new Print(tick.TimestampUtc, this.instrument.RoundToTick(tick.Price), tick.Size));

        this.TrimTo(tick.TimestampUtc - this.window);
    }

    public void OnBar(in BarEvent bar, TimeFrame timeFrame)
    {
        // Absorption is a question about a rolling span, not about a bar. Bars neither open
        // nor close a window here.
    }

    public void OnBook(in BookDelta delta) => this.ladder.OnBook(delta);

    public void OnL3(in L3Event l3)
    {
        // Order-level data would allow per-order absorption, which trial 008 measured
        // inapplicable on this instrument. Nothing here consumes it.
    }

    public void OnSessionPhase(SessionPhase phase)
    {
        // The window is rolling and short; a phase change does not invalidate it.
    }

    /// <summary>
    /// Absorption at each side's touch as of an instant.
    /// </summary>
    /// <param name="nowUtc">
    /// The instant the window ends. REQUIRED, and not defaulted to a wall clock: nothing in
    /// Core reads the clock directly, because a module that did would produce different
    /// output on a second replay of one recording.
    /// </param>
    public AbsorptionSnapshot Read(DateTime nowUtc)
    {
        this.reads++;

        if (!this.ladder.IsKnown)
        {
            this.bookUnknown++;
            return AbsorptionSnapshot.Empty;
        }

        var cutoff = nowUtc - this.window;

        return new AbsorptionSnapshot(
            this.At(this.ladder.BestBid(), BookSide.Bid, cutoff),
            this.At(this.ladder.BestAsk(), BookSide.Ask, cutoff),
            BookKnown: true);
    }

    public FeatureOutput Fold(SessionContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        // The fold has no instant of its own, so the module speaks as of its last print.
        // Reading a wall clock here would make a replay disagree with itself.
        if (this.lastTickUtc == DateTime.MinValue)
            return FeatureOutput.Silent(AbsorptionSnapshot.Empty);

        var snapshot = this.Read(this.lastTickUtc);

        if (!snapshot.BookKnown)
            return FeatureOutput.Silent(snapshot);

        // Bids holding under aggressive selling support price; asks holding under aggressive
        // buying cap it. Both at once is a genuinely balanced book, which scores zero at full
        // confidence — distinct from Silent, which is having nothing to say.
        var score = (snapshot.Bid.IsAbsorbing ? 1f : 0f) - (snapshot.Ask.IsAbsorbing ? 1f : 0f);

        var measured = (snapshot.Bid.IsMeasured ? 1 : 0) + (snapshot.Ask.IsMeasured ? 1 : 0);

        return new FeatureOutput(score, measured / 2f, snapshot);
    }

    public void Reset()
    {
        this.ladder.Reset();
        this.prints.Clear();
        this.lastTickUtc = DateTime.MinValue;

        // THE DIAGNOSTIC COUNTS SURVIVE A RESET. They describe what this module could and
        // could not answer over its whole life, and a session boundary does not make the
        // earlier reads untrue.
    }

    private AbsorptionReading At(double? price, BookSide side, DateTime cutoff)
    {
        if (price is not { } level)
        {
            this.noTouch++;
            return AbsorptionReading.Unmeasured(double.NaN, side);
        }

        var baseline = this.ladder.SizeAt(level, side, cutoff);
        var current = this.ladder.CurrentSize(level, side);

        // COUNTED BEFORE THE RULE IS ASKED, so the reason survives even though the rule
        // collapses every one of them into the same Unmeasured verdict.
        if (baseline is null)
            this.noBaseline++;
        else if (current is null)
            this.noCurrentSize++;
        else
            this.measured++;

        return AbsorptionRule.Evaluate(
            level, side,
            this.VolumeAt(level, cutoff),
            baseline,
            current,
            this.cancelledBelow, this.absorbedAtOrAbove);
    }

    /// <summary>Volume traded at one price at or after the cutoff.</summary>
    private double VolumeAt(double price, DateTime cutoff)
    {
        var total = 0d;

        foreach (var print in this.prints)
        {
            if (print.AtUtc < cutoff)
                continue;

            // Prices are on the instrument's grid on both sides of this comparison — the
            // print was rounded on the way in and the level came from the book — so equality
            // is exact rather than approximate.
            if (print.Price == price)
                total += print.Size;
        }

        return total;
    }

    private void TrimTo(DateTime cutoff)
    {
        while (this.prints.Count > 0 && this.prints.Peek().AtUtc < cutoff)
            this.prints.Dequeue();
    }

    private readonly record struct Print(DateTime AtUtc, double Price, double Size);
}
