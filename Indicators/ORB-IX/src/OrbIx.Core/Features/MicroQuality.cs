using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Features;

/// <summary>
/// The gate's verdict, with every reason it refused.
/// </summary>
/// <param name="Tradeable">Whether a new entry is permitted.</param>
/// <param name="Reasons">Every breach found. Empty when tradeable.</param>
/// <param name="Warnings">Conditions worth surfacing that did not themselves refuse.</param>
/// <param name="SpreadTicks">Current spread in ticks, or NaN when unknown.</param>
/// <param name="MedianSpreadTicks">Median of the retained sample, or NaN below the minimum.</param>
/// <param name="TouchDepth">Smaller of the two touch sizes, or NaN when depth is absent.</param>
public sealed record MicroQualityVerdict(
    bool Tradeable,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    double SpreadTicks,
    double MedianSpreadTicks,
    double TouchDepth);

/// <summary>
/// M14. The gatekeeper.
///
/// Spread against its own median, depth at the touch, quote staleness, and event blackout.
/// A breach of any one refuses a new entry regardless of the confluence score, because a
/// ninety-point signal computed on a broken feed is a ninety-point mistake.
///
/// Two design points worth stating. Spread is judged relative to the instrument's own
/// recent median rather than against an absolute tick count, so one threshold serves a
/// product with a one-tick spread and one with a five-tick spread. And below the minimum
/// sample count the gate reports insufficient evidence rather than passing — a median of
/// three observations is not a median.
///
/// Handlers only record. All judgement happens in <see cref="Evaluate"/>, called from the
/// fold.
/// </summary>
public sealed class MicroQuality : IFeatureModule
{
    private readonly MicroQualityConfig config;
    private readonly SymbolConfig symbolConfig;
    private readonly InstrumentSpec instrument;
    private readonly BlackoutPolicy blackout;
    private readonly double[] spreadSamples;

    private int sampleCount;
    private int sampleCursor;
    private double lastBid = double.NaN;
    private double lastAsk = double.NaN;
    private double lastBidSize = double.NaN;
    private double lastAskSize = double.NaN;
    private DateTime lastQuoteUtc = DateTime.MinValue;
    private bool depthObserved;
    private bool touchWithdrawn;

    public MicroQuality(
        MicroQualityConfig config,
        SymbolConfig symbolConfig,
        InstrumentSpec instrument,
        BlackoutPolicy blackout)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.symbolConfig = symbolConfig ?? throw new ArgumentNullException(nameof(symbolConfig));
        this.blackout = blackout ?? throw new ArgumentNullException(nameof(blackout));
        this.instrument = instrument;

        instrument.RequirePriceScale(nameof(instrument));

        if (config.SpreadSampleWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config), config.SpreadSampleWindow, "The spread sample window must be positive.");
        }

        this.spreadSamples = new double[config.SpreadSampleWindow];
    }

    public string Id => "M14";

    /// <summary>
    /// Quotes. Depth deepens the check but its absence only removes the depth clause; the
    /// spread and staleness clauses still apply.
    /// </summary>
    public DataTier Requires => DataTier.T1;

    public bool Enabled { get; set; } = true;

    public void OnTick(in TickEvent tick)
    {
        // A print carries the quote that prevailed at it, which keeps the gate fed on
        // instruments whose quote stream is sparser than their trade stream.
        if (double.IsNaN(tick.Bid) || double.IsNaN(tick.Ask))
            return;

        this.RecordQuote(tick.TimestampUtc, tick.Bid, tick.Ask);
    }

    public void OnBar(in BarEvent bar, TimeFrame timeFrame)
    {
        // Bars carry no quote information; the gate is about the live top of book.
    }

    public void OnBook(in BookDelta delta)
    {
        // A WITHDRAWAL INVALIDATES THE TOUCH RATHER THAN LEAVING IT STANDING.
        //
        // The platform marks some updates as existing only to remove something from the depth,
        // and those carry no price. Keeping the last bid, ask and sizes across one would make
        // this gate describe a book state that has been taken away — and the gate's entire job
        // is to refuse when the market's state at the touch is not known.
        //
        // WHAT IS KEPT AND WHY. The spread samples survive: a retained median is a historical
        // measurement of how wide this instrument trades, and a withdrawal does not make that
        // measurement wrong. depthObserved survives for the same reason — depth HAS been seen,
        // and clearing it would silently drop the minimum-depth clause instead of enforcing it.
        //
        // The touch does not survive, and the flag is what makes the difference visible.
        // Blanking the four values alone would read downstream as "no depth information", which
        // SKIPS the depth clause and would leave this gate more permissive after a withdrawal
        // than before it — the exact opposite of the intent.
        if (delta.IsReset)
        {
            this.touchWithdrawn = true;
            this.lastBid = double.NaN;
            this.lastAsk = double.NaN;
            this.lastBidSize = double.NaN;
            this.lastAskSize = double.NaN;
            return;
        }

        if (delta.LevelIndex > 0)
            return;

        this.depthObserved = true;
        this.touchWithdrawn = false;
        this.lastQuoteUtc = delta.TimestampUtc;

        if (delta.Side == BookSide.Bid)
        {
            this.lastBid = delta.Price;
            this.lastBidSize = delta.Size;
        }
        else
        {
            this.lastAsk = delta.Price;
            this.lastAskSize = delta.Size;
        }

        if (!double.IsNaN(this.lastBid) && !double.IsNaN(this.lastAsk))
            this.RecordSpread(this.lastAsk - this.lastBid);
    }

    public void OnL3(in L3Event l3)
    {
        // Order-level data does not change whether the market is tradeable at the touch.
    }

    public void OnSessionPhase(SessionPhase phase)
    {
        // Spread statistics carry across phases within a session; Reset clears them at the
        // session boundary.
    }

    /// <summary>
    /// Records a top-of-book quote. Called from the market-data path: it stores and
    /// returns.
    /// </summary>
    public void RecordQuote(DateTime timestampUtc, double bid, double ask)
    {
        if (double.IsNaN(bid) || double.IsNaN(ask) || ask <= bid)
            return;

        this.lastBid = bid;
        this.lastAsk = ask;
        this.lastQuoteUtc = timestampUtc;
        this.touchWithdrawn = false;
        this.RecordSpread(ask - bid);
    }

    private void RecordSpread(double spreadPrice)
    {
        if (spreadPrice <= 0 || this.instrument.TickSize <= 0)
            return;

        this.spreadSamples[this.sampleCursor] = spreadPrice / this.instrument.TickSize;
        this.sampleCursor = (this.sampleCursor + 1) % this.spreadSamples.Length;

        if (this.sampleCount < this.spreadSamples.Length)
            this.sampleCount++;
    }

    public FeatureOutput Fold(SessionContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        var verdict = this.Evaluate(context.Window.OpenUtc, context.SymbolRoot, autoMode: false);

        // The gate is not directional: it never argues for a side. It contributes nothing
        // to the score and blocks through the veto path instead, which is the whole point
        // of separating a gate from a signal.
        return new FeatureOutput(0f, verdict.Tradeable ? 1f : 0f, verdict);
    }

    /// <summary>
    /// Evaluates every clause and returns the full verdict.
    /// </summary>
    public MicroQualityVerdict Evaluate(DateTime utcNow, string symbolRoot, bool autoMode)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();

        var spreadTicks = this.CurrentSpreadTicks();
        var medianSpread = this.MedianSpreadTicks();
        var touchDepth = this.TouchDepth();

        if (this.lastQuoteUtc == DateTime.MinValue)
        {
            reasons.Add("No quote has been seen, so the market's state at the touch is unknown.");
        }
        else if (this.touchWithdrawn)
        {
            // A DISTINCT REASON, not a reuse of the one above. A quote HAS been seen; what is
            // unknown is whether it still stands. Saying "no quote has been seen" instead would
            // be false, and it is the kind of false that survives because it reads plausibly.
            reasons.Add(
                "The platform withdrew the depth at the touch and nothing has replaced it, so "
                + "the current bid and ask are not known.");
        }
        else
        {
            var age = utcNow - this.lastQuoteUtc;

            if (age > TimeSpan.FromMilliseconds(this.config.MaxQuoteAgeMs))
            {
                reasons.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Quote is {0:N0} ms old against a {1:N0} ms limit.",
                    age.TotalMilliseconds, this.config.MaxQuoteAgeMs));
            }
        }

        if (double.IsNaN(medianSpread))
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Only {0} of {1} spread samples collected; the median is not yet meaningful and the spread clause is not applied.",
                this.sampleCount, this.config.MinSpreadSamples));
        }
        else if (!double.IsNaN(spreadTicks)
                 && spreadTicks > medianSpread * this.config.MaxSpreadMedianMultiple)
        {
            reasons.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Spread {0:N2} ticks is more than {1:N2}x its {2:N2}-tick median.",
                spreadTicks, this.config.MaxSpreadMedianMultiple, medianSpread));
        }

        if (!this.depthObserved)
        {
            warnings.Add("No depth has been seen; the minimum-depth clause is not applied.");
        }
        else if (!double.IsNaN(touchDepth) && touchDepth < this.symbolConfig.MinTouchDepth)
        {
            reasons.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Touch depth {0:N0} is below the {1:N0} required for {2}.",
                touchDepth, this.symbolConfig.MinTouchDepth, symbolRoot));
        }

        var blackoutState = this.blackout.Evaluate(utcNow, symbolRoot, autoMode);

        if (blackoutState.Blocked)
            reasons.Add(blackoutState.Reason);
        else if (!blackoutState.CalendarAvailable)
            warnings.Add(blackoutState.Reason);

        return new MicroQualityVerdict(
            reasons.Count == 0, reasons, warnings, spreadTicks, medianSpread, touchDepth);
    }

    private double CurrentSpreadTicks()
        => double.IsNaN(this.lastBid) || double.IsNaN(this.lastAsk) || this.instrument.TickSize <= 0
            ? double.NaN
            : (this.lastAsk - this.lastBid) / this.instrument.TickSize;

    /// <summary>
    /// Median of the retained spread sample, or NaN below the configured minimum.
    ///
    /// A median rather than a mean because one violent widening should not move the
    /// baseline that widening is judged against.
    /// </summary>
    private double MedianSpreadTicks()
    {
        if (this.sampleCount < this.config.MinSpreadSamples)
            return double.NaN;

        var sorted = new double[this.sampleCount];
        Array.Copy(this.spreadSamples, sorted, this.sampleCount);
        Array.Sort(sorted);

        var middle = this.sampleCount / 2;

        return this.sampleCount % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    /// <summary>
    /// The smaller of the two touch sizes. The thin side is the one that gives way, so an
    /// average would hide exactly the condition this clause exists to catch.
    /// </summary>
    private double TouchDepth()
        => double.IsNaN(this.lastBidSize) || double.IsNaN(this.lastAskSize)
            ? double.NaN
            : Math.Min(this.lastBidSize, this.lastAskSize);

    public void Reset()
    {
        Array.Clear(this.spreadSamples);
        this.sampleCount = 0;
        this.sampleCursor = 0;
        this.lastBid = double.NaN;
        this.lastAsk = double.NaN;
        this.lastBidSize = double.NaN;
        this.lastAskSize = double.NaN;
        this.lastQuoteUtc = DateTime.MinValue;
        this.depthObserved = false;
        this.touchWithdrawn = false;
    }
}
