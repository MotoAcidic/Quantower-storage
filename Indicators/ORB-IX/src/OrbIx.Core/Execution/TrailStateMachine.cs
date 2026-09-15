using System;
using System.Globalization;
using OrbIx.Core.Config;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Execution;

/// <summary>How far the position has progressed through its ladder.</summary>
public enum TrailStage
{
    /// <summary>Nothing has come off. The stop is where the plan put it.</summary>
    Initial,

    /// <summary>The first target filled. The stop moves to breakeven plus a buffer.</summary>
    BreakevenAfterFirst,

    /// <summary>The second target filled. The trail engages at its initial distance.</summary>
    TrailingAfterSecond,

    /// <summary>The third target filled. The trail tightens for the runner.</summary>
    TrailingAfterThird,

    /// <summary>Flat.</summary>
    Closed,
}

/// <summary>Where the stop currently sits and why.</summary>
/// <param name="Stage">How far through the ladder the position is.</param>
/// <param name="StopPrice">The stop price the engine wants.</param>
/// <param name="Reason">Why it is there.</param>
/// <param name="Moved">Whether this evaluation moved it.</param>
public sealed record TrailState(TrailStage Stage, double StopPrice, string Reason, bool Moved);

/// <summary>
/// Manages the stop as the ladder fills: breakeven after the first target, trailing after
/// the second, tighter after the third.
///
/// Two invariants, both load-bearing.
///
/// The stop only ever moves toward profit. A trail that can loosen is not a trail, and the
/// configuration loader refuses a tightened distance wider than the initial one so the
/// impossible case cannot even be expressed.
///
/// It re-evaluates on bar close, not on tick. Trailing on every print takes the position
/// out on a single spike, which looks like a stop-out and is really a measurement artefact.
/// The evaluation cadence is configuration, so a replay and a live run agree.
///
/// This is the strategy-managed path. Whether it is used at all is
/// <see cref="TrailMode"/>'s decision: managing the stop here gives finer behaviour and
/// loses it the moment the platform disconnects, which is exactly what K4 warns about.
/// </summary>
public sealed class TrailStateMachine
{
    private readonly TrailConfig config;
    private readonly InstrumentSpec instrument;
    private readonly TradeDirection direction;
    private readonly double entryPrice;
    private readonly string trailKey;

    private TrailStage stage = TrailStage.Initial;
    private double stopPrice;

    public TrailStateMachine(
        TrailConfig config,
        InstrumentSpec instrument,
        TradeDirection direction,
        double entryPrice,
        double initialStopPrice,
        string trailKey)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.instrument = instrument;
        this.direction = direction;
        this.entryPrice = entryPrice;
        this.stopPrice = initialStopPrice;

        if (string.IsNullOrWhiteSpace(trailKey))
            throw new ArgumentException("A trail key is required to look up per-product distances.", nameof(trailKey));

        if (!instrument.IsUsable)
            throw new ArgumentException("Instrument specifications must be usable.", nameof(instrument));

        this.trailKey = trailKey;
    }

    public TrailStage Stage => this.stage;

    public double StopPrice => this.stopPrice;

    /// <summary>
    /// Records a target filling and moves the stop if the new stage calls for it.
    /// </summary>
    /// <param name="targetIndex">Which target filled, one-based.</param>
    /// <param name="favourablePrice">
    /// The best price reached so far, which the trail is measured back from.
    /// </param>
    public TrailState OnTargetFilled(int targetIndex, double favourablePrice)
    {
        if (targetIndex < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetIndex), targetIndex, "Targets are numbered from one.");
        }

        var newStage = targetIndex switch
        {
            1 => TrailStage.BreakevenAfterFirst,
            2 => TrailStage.TrailingAfterSecond,
            >= 3 => TrailStage.TrailingAfterThird,
            _ => this.stage,
        };

        // A ladder cannot un-fill. A later target filling never returns the position to an
        // earlier stage, however the fills arrive.
        if (newStage > this.stage)
            this.stage = newStage;

        return this.Evaluate(favourablePrice);
    }

    /// <summary>
    /// Re-evaluates the stop for the current stage. Called on bar close.
    /// </summary>
    /// <param name="favourablePrice">Best price reached in the position's favour so far.</param>
    public TrailState Evaluate(double favourablePrice)
    {
        var wanted = this.stage switch
        {
            TrailStage.Initial => this.stopPrice,
            TrailStage.BreakevenAfterFirst => this.BreakevenStop(),
            TrailStage.TrailingAfterSecond => this.TrailedStop(favourablePrice, this.config.AfterTp2Ticks),
            TrailStage.TrailingAfterThird => this.TrailedStop(favourablePrice, this.config.AfterTp3Ticks),
            TrailStage.Closed => this.stopPrice,
            _ => this.stopPrice,
        };

        // Toward profit only. This is the invariant that makes it a trail rather than an
        // adjustable stop, and it is enforced here rather than trusted to the callers.
        var improves = this.direction == TradeDirection.Long
            ? wanted > this.stopPrice
            : wanted < this.stopPrice;

        if (!improves)
        {
            return new TrailState(
                this.stage, this.stopPrice,
                $"{this.stage}: stop held at {this.stopPrice.ToString("N2", CultureInfo.InvariantCulture)}; "
                + "a trail never moves away from profit.",
                Moved: false);
        }

        this.stopPrice = this.instrument.RoundToTick(wanted);

        return new TrailState(
            this.stage, this.stopPrice,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}: stop moved to {1:N2}.", this.stage, this.stopPrice),
            Moved: true);
    }

    /// <summary>Marks the position flat. The stop stops moving.</summary>
    public void Close() => this.stage = TrailStage.Closed;

    /// <summary>
    /// Breakeven plus a per-product buffer.
    ///
    /// Plus a buffer rather than exactly at entry because a stop resting on the entry price
    /// is a stop resting where the position was just filled, and the spread alone will take
    /// it out on a retest that was always going to happen.
    /// </summary>
    private double BreakevenStop()
    {
        var ticks = this.Lookup(this.config.BeTicksAfterTp1);
        var sign = this.direction == TradeDirection.Long ? 1d : -1d;

        return this.entryPrice + (sign * ticks * this.instrument.TickSize);
    }

    private double TrailedStop(double favourablePrice, System.Collections.Generic.IReadOnlyDictionary<string, int> distances)
    {
        if (double.IsNaN(favourablePrice))
            return this.stopPrice;

        var ticks = this.Lookup(distances);
        var sign = this.direction == TradeDirection.Long ? -1d : 1d;

        return favourablePrice + (sign * ticks * this.instrument.TickSize);
    }

    /// <summary>
    /// Looks up a per-product distance, trying the contract tier and then the family root.
    /// Trail distances are quoted per family — a mini and a micro move the same number of
    /// ticks — so a tier with no entry of its own falls back to its family rather than to a
    /// number nobody chose.
    /// </summary>
    private int Lookup(System.Collections.Generic.IReadOnlyDictionary<string, int> distances)
    {
        if (distances.TryGetValue(this.instrument.Tier, out var byTier))
            return byTier;

        if (distances.TryGetValue(this.trailKey, out var byFamily))
            return byFamily;

        throw new InvalidOperationException(
            $"No trail distance is configured for tier '{this.instrument.Tier}' or family "
            + $"'{this.trailKey}'. The configuration loader checks this at load time, so "
            + "reaching here means the trail key does not match the configured products.");
    }
}
