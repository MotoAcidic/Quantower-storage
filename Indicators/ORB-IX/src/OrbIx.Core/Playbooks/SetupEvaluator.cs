using System;
using System.Collections.Generic;
using System.Linq;
using OrbIx.Core.Config;
using OrbIx.Core.Features;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Playbooks;

/// <summary>What asking the playbooks produced.</summary>
/// <param name="Plan">The setup that fired, or null when none did.</param>
/// <param name="Playbook">Which playbook produced it.</param>
/// <param name="Vetoes">
/// Every reason a triggered setup was refused, across all playbooks. Non-empty with a null
/// plan is the interesting case and the one the panel exists to show: something set up, and
/// here is exactly what stopped it.
/// </param>
/// <param name="Imbalance">
/// What the imbalance gate concluded about the triggering setup, or null when nothing
/// triggered.
///
/// PRESENT EVEN WHEN THE GATE DID NOT ACT. It is recorded for a setup the gate passed, for
/// one it blocked, and for one it would have blocked had it been enforcing. That last case
/// is the whole point: it is the counterfactual that makes "should this gate be on?"
/// answerable from forward sessions instead of from a backtest.
/// </param>
/// <param name="Absorption">
/// What the absorption gate concluded, or null when nothing triggered. Present even when
/// the gate did not act, for the same reason as <paramref name="Imbalance"/> — and more
/// sharply here, because absorption is already a measured null on this instrument and the
/// counterfactual rows are the only way that decision can be settled against outcomes.
/// </param>
public sealed record SetupEvaluation(
    TradePlan? Plan,
    IPlaybook? Playbook,
    IReadOnlyList<string> Vetoes,
    ImbalanceGateVerdict? Imbalance = null,
    AbsorptionGateVerdict? Absorption = null)
{
    /// <summary>Nothing was eligible and nothing triggered.</summary>
    public static SetupEvaluation Nothing { get; } =
        new(null, null, Array.Empty<string>());
}

/// <summary>
/// Asks every enabled playbook whether it fires, and builds the plan when one does.
///
/// THIS IS THE ONE IMPLEMENTATION OF THAT QUESTION, and it is shared deliberately. The offline
/// replay, the chart indicator and the strategy all need to answer it, and three copies of a
/// decision rule is three chances for the chart to signal something the study never measured —
/// which would make every result in docs/REPLAY-RESULTS.md a statement about a system nobody
/// runs. Extracting it makes the agreement structural instead of something a test has to keep
/// re-establishing.
///
/// It decides and it does not act: no order is built here, no state is mutated on the caller's
/// behalf, and recording an entry against the session budget stays with the caller, because
/// only the caller knows whether the plan it got back was actually taken.
/// </summary>
public sealed class SetupEvaluator
{
    private readonly ConfluenceScorer scorer;
    private readonly IReadOnlyList<IPlaybook> playbooks;
    private readonly ImbalanceConfig imbalance;
    private readonly AbsorptionConfig absorption;

    /// <param name="config">The running configuration; playbooks come from its own list.</param>
    /// <param name="symbolConfig">The product, for its risk cap.</param>
    public SetupEvaluator(OrbIxConfig config, SymbolConfig symbolConfig)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (symbolConfig is null) throw new ArgumentNullException(nameof(symbolConfig));

        this.scorer = new ConfluenceScorer(config.Scoring);
        this.playbooks = Build(config, symbolConfig);
        this.imbalance = config.Imbalance;
        this.absorption = config.Absorption;
    }

    /// <summary>The enabled playbooks, in a stable order.</summary>
    public IReadOnlyList<IPlaybook> Playbooks => this.playbooks;

    /// <summary>
    /// Constructs the enabled playbooks.
    ///
    /// Ordered by id so two runs over the same configuration ask them in the same sequence —
    /// which matters because the first one to fire takes the session's budget.
    ///
    /// A playbook that is enabled in configuration but has no implementation is an ERROR rather
    /// than a silent omission: running with it absent would report a result for a configuration
    /// nobody ran.
    /// </summary>
    private static IReadOnlyList<IPlaybook> Build(OrbIxConfig config, SymbolConfig symbolConfig)
    {
        var built = new List<IPlaybook>();

        foreach (var (id, playbook) in config.Playbooks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!playbook.On)
                continue;

            built.Add(id switch
            {
                "P1" => new BreakAndGo(playbook, config.Stops, config.Targets, symbolConfig.MaxRiskTicks),
                "P2" => new RetestEntry(playbook, config.Stops, config.Targets, symbolConfig.MaxRiskTicks),
                _ => throw new ArgumentException(
                    $"Playbook '{id}' is enabled in configuration but there is no implementation "
                    + "for it. Running with it silently absent would produce a result for a "
                    + "configuration nobody ran.",
                    nameof(config)),
            });
        }

        return built;
    }

    /// <summary>
    /// Asks each playbook in turn, stopping at the first that produces a plan.
    /// </summary>
    /// <param name="context">The live session. Read, never mutated.</param>
    /// <param name="inputs">Everything the playbooks read, frozen at one instant.</param>
    /// <param name="scoreEntries">
    /// Module contributions to the confluence score. Pass an empty set to score the GEOMETRY
    /// alone — which is what the replay does, so that whether the break and retest structure
    /// has an edge is settled before any weighting is layered on top of it. Scoring an empty
    /// set as though modules had voted would invent the most persuasive number in the output.
    /// </param>
    public SetupEvaluation Evaluate(
        SessionContext context,
        PlaybookInputs inputs,
        IReadOnlyList<ScoreEntry> scoreEntries)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));
        if (scoreEntries is null) throw new ArgumentNullException(nameof(scoreEntries));

        if (context.Range is null)
            return SetupEvaluation.Nothing;

        var vetoes = new List<string>();

        // The verdict of the most recent trigger examined. Kept outside the loop so it
        // survives a setup that was refused, which is a row worth journalling.
        ImbalanceGateVerdict? gate = null;
        AbsorptionGateVerdict? absorbed = null;

        foreach (var playbook in this.playbooks)
        {
            if (!playbook.IsEligible(context, inputs).Eligible)
                continue;

            if (playbook.CheckTrigger(context, inputs) is not { } trigger)
                continue;

            // EVALUATED BEFORE THE PLAYBOOK'S OWN VETOES, so a setup stopped by something
            // else still records what imbalance said about it. Filtering the evidence down
            // to setups that got past every other check would leave the journal unable to
            // answer what the gate does on the ones that did not.
            gate = ImbalanceGate.Evaluate(
                inputs.Flow?.Imbalance, trigger.Direction,
                this.imbalance.MinRun, this.imbalance.GateEntries);

            absorbed = AbsorptionGate.Evaluate(
                inputs.Absorption, trigger.Direction, this.absorption.GateEntries);

            var refusals = playbook.Vetoes(context, inputs);

            if (refusals.Count > 0)
            {
                vetoes.AddRange(refusals);
                continue;
            }

            if (gate.Blocks)
            {
                vetoes.Add(gate.Reason);
                continue;
            }

            if (absorbed.Blocks)
            {
                vetoes.Add(absorbed.Reason);
                continue;
            }

            var stop = playbook.BuildStop(trigger, context, inputs);

            if (!stop.WithinCap)
            {
                // The distance travels with the refusal. "Beyond the cap" alone cannot tell a
                // reader whether the cap is one tick too tight or four times too tight, and
                // that difference decides whether the cap is worth revisiting.
                vetoes.Add(
                    $"Stop of {stop.DistanceTicks}t is beyond this product's risk cap; "
                    + "a stop is never tightened to fit a size.");
                continue;
            }

            var targets = playbook.BuildTargets(trigger, stop, context, inputs);

            if (targets.Count == 0)
            {
                vetoes.Add("No target could be placed.");
                continue;
            }

            var scored = this.scorer.Score(trigger.Direction, scoreEntries, context, Array.Empty<string>());

            return new SetupEvaluation(
                new TradePlan(
                    playbook.Id,
                    trigger.Direction,
                    trigger.Style,
                    trigger.ReferencePrice,
                    stop,
                    targets,
                    scored,
                    trigger.Reason),
                playbook,
                vetoes,
                gate,
                absorbed);
        }

        return new SetupEvaluation(null, null, vetoes, gate, absorbed);
    }
}
