using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;

namespace OrbIx.Core.Sessions;

/// <summary>
/// M02. Builds one session's opening range and emits an immutable
/// <see cref="OrSnapshot"/> the instant it closes.
///
/// The builder is fed raw prints rather than bars so that a five-minute range on a
/// release morning is measured from what actually traded, and so the same code produces
/// the same range in a replay as it does live.
///
/// Nothing here reads a clock. Every decision is a function of the events supplied and the
/// instant passed in, which is what makes V1's bit-identical replay achievable.
/// </summary>
public sealed class OrBuilder
{
    private readonly SessionWindow window;
    private readonly InstrumentSpec instrument;
    private readonly OpeningRangeConfig config;
    private readonly double adr;
    private readonly double medianOpenVolume;
    private readonly TimeSpan subBarLength;

    private readonly Dictionary<double, double> volumeByPrice = new();

    private double high = double.NaN;
    private double low = double.NaN;
    private double lastPrice = double.NaN;
    private double volume;
    private double delta;
    private double notional;

    private int highTests;
    private int lowTests;
    private bool awayFromHigh = true;
    private bool awayFromLow = true;

    private bool ticked;
    private bool seeded;
    private TimeSpan seedBarSpan;

    private double bookImbalanceSum;
    private int bookSamples;
    private double lastBidSize;
    private double lastAskSize;

    private DateTime subBarOpenUtc = DateTime.MinValue;
    private double subBarHigh;
    private double subBarLow;
    private double previousSubBarClose = double.NaN;
    private double trueRangeSum;
    private int trueRangeCount;

    private OrSnapshot? snapshot;

    public OrBuilder(
        SessionWindow window,
        InstrumentSpec instrument,
        OpeningRangeConfig config,
        double averageDailyRange,
        double medianOpenVolume,
        TimeSpan subBarLength)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.instrument = instrument;

        if (!instrument.IsUsable)
        {
            throw new ArgumentException(
                "Instrument specifications must come from the platform and be positive.", nameof(instrument));
        }

        if (subBarLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subBarLength), subBarLength, "Sub-bar length must be positive.");
        }

        if (averageDailyRange < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(averageDailyRange), averageDailyRange, "Average daily range cannot be negative.");
        }

        if (medianOpenVolume < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(medianOpenVolume), medianOpenVolume, "Median opening volume cannot be negative.");
        }

        this.adr = averageDailyRange;
        this.medianOpenVolume = medianOpenVolume;
        this.subBarLength = subBarLength;
    }

    /// <summary>The closed range, or null while it is still building.</summary>
    public OrSnapshot? Snapshot => this.snapshot;

    public bool IsClosed => this.snapshot is not null;

    /// <summary>Prints seen so far. Zero means the range is empty, not that it is narrow.</summary>
    public bool HasPrints => this.volume > 0;

    /// <summary>
    /// Records a print. Ignores anything outside the session's own window so a late-
    /// arriving event from the previous session cannot widen this one's range.
    /// </summary>
    public void OnTick(in TickEvent tick)
    {
        if (this.snapshot is not null)
            return;

        if (this.seeded)
        {
            throw new InvalidOperationException(
                "This range was seeded from bars and cannot also take ticks. A range that mixed "
                + "the two would report a delta and a point of control measured over only part of "
                + "its own window, which is worse than reporting none.");
        }

        if (tick.TimestampUtc < this.window.OpenUtc || tick.TimestampUtc >= this.window.OrHardCloseUtc)
            return;

        if (tick.Size <= 0 || double.IsNaN(tick.Price))
            return;

        this.ticked = true;

        this.UpdateExtremes(tick.Price);

        this.lastPrice = tick.Price;
        this.volume += tick.Size;
        this.delta += tick.SignedSize;
        this.notional += tick.Price * tick.Size;

        var bucket = this.instrument.RoundToTick(tick.Price);
        this.volumeByPrice[bucket] = this.volumeByPrice.TryGetValue(bucket, out var existing)
            ? existing + tick.Size
            : tick.Size;

        this.AccumulateSubBar(tick.TimestampUtc, tick.Price);
    }

    /// <summary>
    /// Records a top-of-book change so the range can report the depth imbalance that
    /// prevailed while it built. Absent depth leaves
    /// <see cref="OrSnapshot.BookObserved"/> false rather than reporting a balanced book.
    /// </summary>
    public void OnBook(in BookDelta delta)
    {
        if (this.snapshot is not null)
            return;

        if (delta.TimestampUtc < this.window.OpenUtc || delta.TimestampUtc >= this.window.OrHardCloseUtc)
            return;

        if (delta.LevelIndex > 0)
            return;

        // A SIZE THAT IS NOT A NUMBER IS REFUSED BEFORE IT IS STORED, not after.
        //
        // The guard below used to be the only one, and "total <= 0" does not catch NaN —
        // every comparison against NaN is false, so it passed straight through. The
        // accumulator became NaN, no later good sample could restore it, and the range
        // reported BookObserved true beside an imbalance that was not a number.
        //
        // Measured 2026-08-22: every Level 2 subscription delivers two sentinel messages
        // carrying a NaN price and size, so this fired at every indicator load.
        //
        // Zero is NOT refused. Zero removes a level and is a real state of the book; sweeping
        // it up with NaN would discard genuine observations.
        //
        // THIS IS ALSO WHAT CATCHES A WITHDRAWAL, and there is deliberately no second guard for
        // one. BookDelta.IsReset events carry a NaN size by construction, so an explicit
        // `if (delta.IsReset) return;` above this line was verified by mutation to change no
        // behaviour and no test outcome — two guards where the narrower one can never fire
        // first. The general guard stays because it also covers a malformed size that carries
        // no reset flag at all.
        //
        // The retained sizes are deliberately LEFT ALONE across a withdrawal. What this builder
        // reports is a statistic gathered over the whole range window, and one withdrawal does
        // not make the samples already taken untrue. Invalidating the touch is MicroQuality's
        // job, because MicroQuality is what decides whether the market is tradeable RIGHT NOW.
        if (double.IsNaN(delta.Size) || double.IsInfinity(delta.Size))
            return;

        if (delta.Side == BookSide.Bid)
            this.lastBidSize = delta.Size;
        else
            this.lastAskSize = delta.Size;

        var total = this.lastBidSize + this.lastAskSize;

        // Still guarded, and now reachable only by real numbers. Written as an explicit
        // positive test so a NaN arriving by some future path cannot slip through it either.
        if (!(total > 0))
            return;

        this.bookImbalanceSum += (this.lastBidSize - this.lastAskSize) / total;
        this.bookSamples++;
    }

    /// <summary>
    /// Builds the range from a completed bar instead of from prints.
    ///
    /// This exists so a session that closed before the indicator loaded can still be drawn.
    /// A bar carries a high, a low, a close and a volume, and those are recorded exactly as
    /// given. It carries no aggressor side and no intra-bar sequence, so delta, the point of
    /// control, the volume-weighted price and the edge-test counts are NOT derived here and
    /// the resulting snapshot reports <see cref="OrSnapshot.FlowObserved"/> false. Synthesising
    /// four ticks per bar would produce all five of those numbers and every one would be an
    /// invention wearing a real bar's credibility.
    ///
    /// Seeding and live ticks cannot be mixed on one builder — the second call of the other
    /// kind throws. A range half-measured from flow and half from bars would report figures
    /// covering only part of its own window while looking complete.
    /// </summary>
    /// <param name="barOpenUtc">Bar open, used to place the bar in the window and to size the ATR basis.</param>
    /// <param name="barCloseUtc">Bar close.</param>
    /// <param name="high">Bar high.</param>
    /// <param name="low">Bar low.</param>
    /// <param name="close">Bar close price.</param>
    /// <param name="volume">Bar volume.</param>
    /// <returns>True when the bar fell inside the range window and was recorded.</returns>
    public bool SeedFromBar(
        DateTime barOpenUtc, DateTime barCloseUtc, double high, double low, double close, double volume)
    {
        if (this.snapshot is not null)
            return false;

        if (this.ticked)
        {
            throw new InvalidOperationException(
                "This range is being built from live ticks and cannot also be seeded from bars. "
                + "See SeedFromBar's remarks: a mixed range misreports its own window.");
        }

        // A bar belongs to the range when it starts inside the window. Judging by the bar's
        // close would pull in the bar that straddles the open, whose high and low largely
        // describe the minutes before the session began.
        if (barOpenUtc < this.window.OpenUtc || barOpenUtc >= this.window.OrHardCloseUtc)
            return false;

        if (double.IsNaN(high) || double.IsNaN(low) || high < low)
            return false;

        this.seeded = true;

        if (double.IsNaN(this.high) || high > this.high)
            this.high = high;

        if (double.IsNaN(this.low) || low < this.low)
            this.low = low;

        if (!double.IsNaN(close))
            this.lastPrice = close;

        if (volume > 0)
            this.volume += volume;

        // The bar's own range is a true range, measured rather than invented, so ATR is
        // accumulated. Its basis is the bar period, not the configured sub-bar length, which
        // is why the snapshot carries AtrBasis: the two are different units and a consumer
        // that confused them would size stops against the wrong noise scale.
        var span = barCloseUtc - barOpenUtc;

        if (this.seedBarSpan == TimeSpan.Zero && span > TimeSpan.Zero)
            this.seedBarSpan = span;

        var trueRange = high - low;

        if (!double.IsNaN(this.previousSubBarClose))
        {
            trueRange = Math.Max(
                trueRange,
                Math.Max(
                    Math.Abs(high - this.previousSubBarClose),
                    Math.Abs(low - this.previousSubBarClose)));
        }

        this.trueRangeSum += trueRange;
        this.trueRangeCount++;
        this.previousSubBarClose = close;

        return true;
    }

    /// <summary>
    /// Closes a seeded range at the window's own opening-range end.
    ///
    /// Separate from <see cref="TryClose"/> because the adaptive closure conditions read
    /// participation against median opening volume, which a bar-seeded range cannot evaluate
    /// tick by tick. A seeded range closes where the session says it closes.
    /// </summary>
    public OrSnapshot? CloseSeeded()
    {
        if (this.snapshot is not null)
            return this.snapshot;

        if (!this.seeded || !this.HasPrints)
            return null;

        var definition = this.window.Definition;

        var closeUtc = definition.OrKind == OrLengthKind.Fixed
            ? this.window.OpenUtc + definition.OrLength
            : this.window.OrHardCloseUtc;

        return this.Close(
            closeUtc,
            definition.OrKind == OrLengthKind.Fixed ? OrCloseReason.FixedLength : OrCloseReason.TimeCap);
    }

    /// <summary>
    /// Closes the range if any closure condition is satisfied at <paramref name="utcNow"/>,
    /// and returns the snapshot when it does. Returns null while the range is still
    /// building, and the existing snapshot once it has closed.
    ///
    /// A fixed range closes on the clock. An adaptive one closes on the first of:
    /// participation reaching the configured multiple of median opening volume; the hard
    /// time cap; or the width reaching the configured fraction of average daily range,
    /// meaning the day's likely movement is already spent. None of them may fire before
    /// the minimum duration, which is what stops a single sweep print at the open from
    /// defining the whole range.
    /// </summary>
    public OrSnapshot? TryClose(DateTime utcNow)
    {
        if (this.snapshot is not null)
            return this.snapshot;

        // A range with no prints has no high, no low and no width. It never closes: there
        // is nothing to close around, and emitting a snapshot of NaN extremes would give
        // every downstream module a range-shaped object describing nothing. The engine
        // distinguishes "still forming" from "nothing traded" through HasPrints.
        if (!this.HasPrints)
            return null;

        var elapsed = utcNow - this.window.OpenUtc;
        var definition = this.window.Definition;

        if (definition.OrKind == OrLengthKind.Fixed)
        {
            return elapsed >= definition.OrLength
                ? this.Close(utcNow, OrCloseReason.FixedLength)
                : null;
        }

        var adaptive = this.config.Adaptive;

        if (elapsed < TimeSpan.FromSeconds(adaptive.MinSec))
            return null;

        if (this.medianOpenVolume > 0 && this.volume >= adaptive.Kappa * this.medianOpenVolume)
            return this.Close(utcNow, OrCloseReason.VolumeThreshold);

        if (this.adr > 0 && this.Width >= adaptive.MaxWidthAdrPct * this.adr)
            return this.Close(utcNow, OrCloseReason.RangeSpent);

        if (elapsed >= TimeSpan.FromMinutes(adaptive.MaxMin) || utcNow >= this.window.OrHardCloseUtc)
            return this.Close(utcNow, OrCloseReason.TimeCap);

        return null;
    }

    private double Width => double.IsNaN(this.high) || double.IsNaN(this.low) ? 0d : this.high - this.low;

    /// <summary>
    /// Tracks the extremes and counts edge tests.
    ///
    /// A test is counted when price returns to within one tick of an extreme having first
    /// moved more than one tick away from it. Counting every print at the high instead
    /// would make test count a proxy for volume, which is a different measurement wearing
    /// the same name.
    /// </summary>
    private void UpdateExtremes(double price)
    {
        var tolerance = this.instrument.TickSize;

        if (double.IsNaN(this.high) || price > this.high)
        {
            this.high = price;
            this.highTests = Math.Max(this.highTests, 1);
            this.awayFromHigh = false;
        }
        else if (price >= this.high - tolerance)
        {
            if (this.awayFromHigh)
            {
                this.highTests++;
                this.awayFromHigh = false;
            }
        }
        else
        {
            this.awayFromHigh = true;
        }

        if (double.IsNaN(this.low) || price < this.low)
        {
            this.low = price;
            this.lowTests = Math.Max(this.lowTests, 1);
            this.awayFromLow = false;
        }
        else if (price <= this.low + tolerance)
        {
            if (this.awayFromLow)
            {
                this.lowTests++;
                this.awayFromLow = false;
            }
        }
        else
        {
            this.awayFromLow = true;
        }
    }

    /// <summary>
    /// Maintains fixed-length sub-bars inside the range so the snapshot can report the
    /// noise scale of the session that just happened, rather than of an earlier one.
    /// </summary>
    private void AccumulateSubBar(DateTime timestampUtc, double price)
    {
        if (this.subBarOpenUtc == DateTime.MinValue)
        {
            this.subBarOpenUtc = this.window.OpenUtc;
            this.subBarHigh = price;
            this.subBarLow = price;
            return;
        }

        while (timestampUtc >= this.subBarOpenUtc + this.subBarLength)
        {
            this.CloseSubBar();
            this.subBarOpenUtc += this.subBarLength;
            this.subBarHigh = price;
            this.subBarLow = price;
        }

        if (price > this.subBarHigh) this.subBarHigh = price;
        if (price < this.subBarLow) this.subBarLow = price;
    }

    private void CloseSubBar()
    {
        if (this.subBarHigh < this.subBarLow)
            return;

        var trueRange = this.subBarHigh - this.subBarLow;

        if (!double.IsNaN(this.previousSubBarClose))
        {
            trueRange = Math.Max(
                trueRange,
                Math.Max(
                    Math.Abs(this.subBarHigh - this.previousSubBarClose),
                    Math.Abs(this.subBarLow - this.previousSubBarClose)));
        }

        this.trueRangeSum += trueRange;
        this.trueRangeCount++;
        this.previousSubBarClose = this.lastPrice;
    }

    private OrSnapshot Close(DateTime utcNow, OrCloseReason reason)
    {
        // Fold the sub-bar in progress so a short range still reports a noise scale.
        if (this.subBarOpenUtc != DateTime.MinValue)
            this.CloseSubBar();

        var poc = this.lastPrice;
        var pocVolume = -1d;
        foreach (var (price, priceVolume) in this.volumeByPrice)
        {
            // Ties resolve to the lower price, deterministically, so a replay agrees with
            // the live run rather than depending on dictionary iteration order.
            if (priceVolume > pocVolume || (priceVolume == pocVolume && price < poc))
            {
                poc = price;
                pocVolume = priceVolume;
            }
        }

        var atrTicks = this.trueRangeCount > 0 && this.instrument.TickSize > 0
            ? this.trueRangeSum / this.trueRangeCount / this.instrument.TickSize
            : 0d;

        // Flow-derived figures are emitted only when flow was actually seen. On a seeded
        // range they are left at zero and FlowObserved says so, rather than shipping a
        // point of control that is really just the last bar's close.
        var flowObserved = this.ticked;

        var width = this.Width;
        var orw = this.adr > 0 ? width / this.adr : 0d;

        this.snapshot = new OrSnapshot
        {
            SessionName = this.window.Definition.Name,
            SymbolRoot = this.instrument.Root,
            OpenUtc = this.window.OpenUtc,
            CloseUtc = utcNow,
            CloseReason = reason,
            SessionEndUtc = this.window.EndUtc,
            Orh = this.high,
            Orl = this.low,
            Vwap = flowObserved && this.volume > 0 ? this.notional / this.volume : 0d,
            Poc = flowObserved ? poc : 0d,
            Delta = flowObserved ? this.delta : 0d,
            HighTests = flowObserved ? this.highTests : 0,
            LowTests = flowObserved ? this.lowTests : 0,
            Close = this.lastPrice,
            Volume = this.volume,
            AtrTicks = atrTicks,
            BookImbalance = this.bookSamples > 0 ? this.bookImbalanceSum / this.bookSamples : 0d,
            BookObserved = this.bookSamples > 0,
            FlowObserved = flowObserved,
            AtrBasis = flowObserved ? this.subBarLength : this.seedBarSpan,
            Adr = this.adr,
            TickSize = this.instrument.TickSize,
            Grade = OrSnapshot.GradeFor(orw, this.config.Grades),
            ExtensionMultiples = new EquatableArray<double>(this.config.ExtensionMultiples),
        };

        return this.snapshot;
    }
}
