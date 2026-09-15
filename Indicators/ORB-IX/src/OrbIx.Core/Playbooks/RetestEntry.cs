using System;
using System.Collections.Generic;
using System.Globalization;
using OrbIx.Core.Config;
using OrbIx.Core.Features;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Playbooks;

/// <summary>
/// P2 — Retest Entry. Continuation, with the regime.
///
/// §5 calls this the best risk-adjusted setup in the book, and its edge is patience: the
/// same break, entered from the retest price, with the tightest stop in the system, so the
/// same targets sit at structurally larger multiples of risk.
///
/// Two of §5's confirmation clauses need data phase 1 does not have — absorption on the
/// correct side at the retest price, and the nested fair-value gap being only partially
/// filled. They are named in <see cref="UnavailableClauses"/> rather than dropped or
/// invented, and their weight is redistributed by the scorer.
/// </summary>
public sealed class RetestEntry : IPlaybook
{
    private readonly PlaybookConfig config;
    private readonly StopsConfig stops;
    private readonly TargetsConfig targets;
    private readonly int maxRiskTicks;

    public RetestEntry(
        PlaybookConfig config, StopsConfig stops, TargetsConfig targets, int maxRiskTicks)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.stops = stops ?? throw new ArgumentNullException(nameof(stops));
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));

        if (maxRiskTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRiskTicks), maxRiskTicks, "The product's risk cap must be positive.");
        }

        this.maxRiskTicks = maxRiskTicks;
    }

    public string Id => "P2";

    public string Name => "Retest Entry";

    public double SizeMultiplier => this.config.SizeMult;

    /// <summary>
    /// The §5 clauses this playbook cannot evaluate in the phase-1 data environment.
    ///
    /// ABSORPTION LEFT THIS LIST, and it was not dropped for convenience. It read
    /// "absorption on the correct side at the retest price (needs depth)" and that statement
    /// became FALSE once <see cref="Features.AbsorptionEngine"/> shipped: the depth exists,
    /// the clause is evaluable, and leaving it here would tell a reader of the journal that a
    /// check was impossible while it was in fact running. When depth happens to be dark the
    /// gate reports Unmeasured and contributes
    /// <see cref="Features.AbsorptionGate.UnevaluatedClause"/> for that signal alone —
    /// which is the honest shape, because it is a per-signal fact and not a permanent one.
    /// </summary>
    public static IReadOnlyList<string> UnavailableClauses { get; } = new[]
    {
        "the nested fair-value gap is only partially filled (needs the gap engine)",
    };

    public Eligibility IsEligible(SessionContext context, PlaybookInputs inputs)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        if (!this.config.On)
            return Eligibility.No("P2 is disabled in configuration.");

        if (context.Range is null)
            return Eligibility.No("The opening range has not closed.");

        if (!inputs.Break.Broken)
            return Eligibility.No("Nothing has been broken, so there is nothing to retest.");

        if (inputs.Retest.Class == RetestClass.None)
            return Eligibility.No("No retest is being tracked.");

        return Eligibility.Yes(
            $"Tracking a retest of the {inputs.Break.Direction} break at {inputs.Break.Level:N2}.");
    }

    public TriggerResult? CheckTrigger(SessionContext context, PlaybookInputs inputs)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        if (!this.IsEligible(context, inputs).Eligible)
            return null;

        var retest = inputs.Retest;

        // Only the two tradeable classifications. A reclaim failure is not a worse version
        // of this setup; it is the absence of one.
        if (!retest.IsTradeable)
            return null;

        // §5: the depth limit, expressed as a fraction of the range that was broken.
        if (this.config.MaxRetestDepthPct is { } limit && retest.DepthFraction > limit)
            return null;

        var direction = retest.Direction == BreakDirection.Up
            ? TradeDirection.Long
            : TradeDirection.Short;

        // The stop sits beyond the retest's own extreme rather than beyond the range: that
        // is what makes this the tightest stop in the system and the reason the same
        // targets are larger multiples of risk from here.
        var sign = direction == TradeDirection.Long ? -1d : 1d;
        var structural = retest.LevelPrice
                         + (sign * retest.DepthTicks * context.Instrument.TickSize);

        return new TriggerResult(
            direction,
            retest.LevelPrice,
            retest.LevelPrice,
            // A passive limit at the broken edge. §7 accepts that this misses roughly a
            // third of the moves; the fills on the rest are what pays for it.
            EntryStyle.LimitAtRetest,
            structural,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} retest of {1:N2}, {2:N1} ticks deep ({3:P0} of the range){4}.",
                retest.Class, retest.LevelPrice, retest.DepthTicks, retest.DepthFraction,
                double.IsNaN(retest.VolumeRatio)
                    ? string.Empty
                    : $", retest volume {retest.VolumeRatio:P0} of the break"));
    }

    public IReadOnlyList<string> Vetoes(SessionContext context, PlaybookInputs inputs)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var vetoes = new List<string>();

        vetoes.AddRange(inputs.Quality.Reasons);

        if (!context.CanEnter(inputs.NowUtc, out var sessionReason))
            vetoes.Add(sessionReason);

        // §5: reclaim failure.
        if (inputs.Retest.Class == RetestClass.ReclaimFailure)
            vetoes.Add("The reclaim failed; price traded back through the level and stayed.");

        // §5: retest volume exceeding break volume. A return louder than the move that
        // created it is a different animal from a quiet pullback.
        if (!double.IsNaN(inputs.Retest.VolumeRatio) && inputs.Retest.VolumeRatio > 1d)
        {
            vetoes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Retest volume is {0:P0} of the break's; the return is heavier than the move.",
                inputs.Retest.VolumeRatio));
        }

        // §5: cumulative delta must hold its structure in the direction of the break.
        if (inputs.Flow is { } flow && flow.Classified > 0)
        {
            var direction = inputs.Retest.Direction == BreakDirection.Up
                ? TradeDirection.Long
                : TradeDirection.Short;

            var deltaHolds = direction == TradeDirection.Long
                ? flow.CumulativeDelta > 0
                : flow.CumulativeDelta < 0;

            if (!deltaHolds)
            {
                vetoes.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Cumulative delta {0:N0} no longer holds the {1} structure.",
                    flow.CumulativeDelta, direction));
            }
        }

        return vetoes;
    }

    public StopPlan BuildStop(TriggerResult trigger, SessionContext context, PlaybookInputs inputs)
    {
        if (trigger is null) throw new ArgumentNullException(nameof(trigger));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var range = context.Range
            ?? throw new InvalidOperationException("A stop cannot be placed before the range has closed.");

        return TradeGeometry.BuildStop(
            trigger.Direction, trigger.ReferencePrice, trigger.StructuralPrice, range,
            inputs.AtrTicks, inputs.MedianSpreadTicks, context.Instrument, this.stops,
            this.maxRiskTicks, inputs.Levels, inputs.NowUtc);
    }

    public IReadOnlyList<TargetPlan> BuildTargets(
        TriggerResult trigger, StopPlan stop, SessionContext context, PlaybookInputs inputs)
    {
        if (trigger is null) throw new ArgumentNullException(nameof(trigger));
        if (stop is null) throw new ArgumentNullException(nameof(stop));
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var range = context.Range
            ?? throw new InvalidOperationException("Targets cannot be placed before the range has closed.");

        // §5: the same ladder as P1, measured from the retest price. Because the entry is
        // nearer the edge and the stop is tighter, every rung is a larger multiple of risk
        // without a single target having moved.
        return TradeGeometry.BuildTargets(
            trigger.Direction, trigger.ReferencePrice, stop, range, context.Instrument,
            this.targets, inputs.Levels, inputs.NowUtc, armFourthTarget: true);
    }
}
