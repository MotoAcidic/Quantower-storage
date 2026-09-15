using System;
using System.Collections.Generic;
using System.Globalization;
using OrbIx.Core.Config;

namespace OrbIx.Core.Risk;

/// <summary>
/// What a playbook has actually been shown to do, on a given session and product, out of
/// sample. This is the evidence <see cref="EngineModeGate"/> consults; it is never
/// inferred from the fact that code exists.
/// </summary>
/// <param name="OutOfSampleTrades">Trades recorded out of sample under §13's V4 protocol.</param>
/// <param name="ArmedForwardSessions">Sessions run in Armed mode on a live evaluation, per V6.</param>
/// <param name="ExecutionRealismChecked">Whether V5 compared simulated fills to actual ones.</param>
/// <param name="PropRuleSimulationPassed">Whether V6's rule simulation ran without violations.</param>
public readonly record struct ValidationEvidence(
    int OutOfSampleTrades,
    int ArmedForwardSessions,
    bool ExecutionRealismChecked,
    bool PropRuleSimulationPassed)
{
    /// <summary>
    /// The state of a combination nothing has been recorded against. Distinct from a
    /// failing record: this says "not measured", which is why the gate refuses rather
    /// than assumes.
    /// </summary>
    public static ValidationEvidence None => new(0, 0, false, false);
}

/// <summary>
/// Supplies <see cref="ValidationEvidence"/> per playbook, session and product.
/// </summary>
public interface IValidationAttestation
{
    /// <summary>
    /// Evidence for one combination. Implementations return
    /// <see cref="ValidationEvidence.None"/> when nothing has been recorded — absence of a
    /// record is a truthful answer, not an error.
    /// </summary>
    ValidationEvidence For(string playbookId, string sessionName, string symbolRoot);
}

/// <summary>The mode actually applied, and why it differs from the one requested.</summary>
/// <param name="Requested">The mode named in configuration.</param>
/// <param name="Effective">The mode the engine will run in.</param>
/// <param name="Downgraded">Whether the request was refused.</param>
/// <param name="Reason">Human-readable justification, carried onto the HUD and into the journal.</param>
public sealed record EngineModeDecision(
    EngineMode Requested,
    EngineMode Effective,
    bool Downgraded,
    string Reason);

/// <summary>
/// Decides whether the engine may act unattended.
///
/// §13 is explicit that no configuration should reach a funded account before its
/// validation protocol has been run in full, and §14 places Auto mode behind V4-V6. That
/// is enforced here rather than left to discipline: asking for Auto in configuration is a
/// request, and this gate is what answers it. A refusal always states which specific piece
/// of evidence is missing and by how much, so the path to satisfying it is visible.
///
/// The gate never upgrades. Requesting Signal yields Signal even with perfect evidence.
/// </summary>
public sealed class EngineModeGate
{
    private readonly ValidationConfig config;
    private readonly IValidationAttestation attestation;

    public EngineModeGate(ValidationConfig config, IValidationAttestation attestation)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.attestation = attestation ?? throw new ArgumentNullException(nameof(attestation));
    }

    /// <summary>
    /// Resolves the effective mode for one playbook on one session and product.
    /// </summary>
    public EngineModeDecision Resolve(
        EngineMode requested, string playbookId, string sessionName, string symbolRoot)
    {
        if (string.IsNullOrWhiteSpace(playbookId))
            throw new ArgumentException("A playbook id is required.", nameof(playbookId));
        if (string.IsNullOrWhiteSpace(sessionName))
            throw new ArgumentException("A session name is required.", nameof(sessionName));
        if (string.IsNullOrWhiteSpace(symbolRoot))
            throw new ArgumentException("A product root is required.", nameof(symbolRoot));

        if (requested != EngineMode.Auto)
        {
            return new EngineModeDecision(
                requested, requested, Downgraded: false,
                $"{requested} requested; no validation gate applies below Auto.");
        }

        var evidence = this.attestation.For(playbookId, sessionName, symbolRoot);
        var shortfalls = new List<string>();

        if (evidence.OutOfSampleTrades < this.config.MinOutOfSampleTrades)
        {
            shortfalls.Add(string.Format(
                CultureInfo.InvariantCulture,
                "V4 out-of-sample trades {0} of {1} required",
                evidence.OutOfSampleTrades, this.config.MinOutOfSampleTrades));
        }

        if (evidence.ArmedForwardSessions < this.config.MinArmedForwardSessions)
        {
            shortfalls.Add(string.Format(
                CultureInfo.InvariantCulture,
                "V6 Armed forward sessions {0} of {1} required",
                evidence.ArmedForwardSessions, this.config.MinArmedForwardSessions));
        }

        if (!evidence.ExecutionRealismChecked)
            shortfalls.Add("V5 execution-realism comparison has not been run");

        if (!evidence.PropRuleSimulationPassed)
            shortfalls.Add("V6 account-rule simulation has not passed");

        if (shortfalls.Count == 0)
        {
            return new EngineModeDecision(
                EngineMode.Auto, EngineMode.Auto, Downgraded: false,
                $"Auto permitted for {playbookId} on {sessionName}/{symbolRoot}: V4-V6 satisfied.");
        }

        return new EngineModeDecision(
            EngineMode.Auto, EngineMode.Armed, Downgraded: true,
            $"Auto refused for {playbookId} on {sessionName}/{symbolRoot}, running Armed instead — "
            + string.Join("; ", shortfalls) + ".");
    }
}
