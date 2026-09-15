using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Execution;
using OrbIx.Core.Features;
using OrbIx.Core.Scoring;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Playbooks;

/// <summary>
/// How an entry is placed. §7 makes this a choice rather than a fixed property, because the
/// three carry different costs and different miss rates.
/// </summary>
public enum EntryStyle
{
    /// <summary>
    /// A resting stop order beyond the edge. Guarantees participation, pays slippage.
    /// </summary>
    StopThrough,

    /// <summary>
    /// A passive limit at the broken edge. Best fills, misses roughly a third of the moves,
    /// and that trade-off is priced into the expectancy model rather than wished away.
    /// </summary>
    LimitAtRetest,

    /// <summary>
    /// Fires on the close that satisfies the confirmation set. Used where being late is
    /// cheaper than being wrong.
    /// </summary>
    MarketOnConfirm,
}

/// <summary>Whether a playbook may look at this session at all, and why not when it may not.</summary>
/// <param name="Eligible">Whether the playbook applies.</param>
/// <param name="Reason">Always populated, including when eligible.</param>
public readonly record struct Eligibility(bool Eligible, string Reason)
{
    public static Eligibility Yes(string reason) => new(true, reason);

    public static Eligibility No(string reason) => new(false, reason);
}

/// <summary>
/// A playbook's trigger having fired: the direction, the reference price, and the level the
/// setup is built around.
/// </summary>
/// <param name="Direction">Which side.</param>
/// <param name="ReferencePrice">
/// The price the plan is measured from — the break price, or the retest price where the
/// playbook enters on a return.
/// </param>
/// <param name="Level">The level the setup is built around, typically a range edge.</param>
/// <param name="Style">How the entry should be placed.</param>
/// <param name="StructuralPrice">
/// The price beyond which the setup is structurally wrong — the swing the stop is measured
/// against.
/// </param>
/// <param name="Reason">Human-readable description of what fired.</param>
public sealed record TriggerResult(
    TradeDirection Direction,
    double ReferencePrice,
    double Level,
    EntryStyle Style,
    double StructuralPrice,
    string Reason);

/// <summary>Where the stop goes, and how it was arrived at.</summary>
/// <param name="Price">The stop price, already nudged clear of round numbers and clusters.</param>
/// <param name="DistanceTicks">Distance from the entry reference, in ticks.</param>
/// <param name="StructuralCandidate">The structural candidate before the buffer.</param>
/// <param name="VolatilityCandidate">The volatility candidate before the buffer.</param>
/// <param name="BufferTicks">The buffer applied.</param>
/// <param name="WithinCap">Whether the result is inside the product's maximum risk.</param>
/// <param name="Reason">How it was arrived at, for the journal.</param>
public sealed record StopPlan(
    double Price,
    int DistanceTicks,
    double StructuralCandidate,
    double VolatilityCandidate,
    int BufferTicks,
    bool WithinCap,
    string Reason);

/// <summary>One target: where it is, in ticks from entry, and why it is there.</summary>
public sealed record TargetPlan(double Price, int Ticks, double RMultiple, string Basis);

/// <summary>
/// A complete, checkable proposal. Everything a human or an order router needs, with the
/// reasoning attached.
/// </summary>
public sealed record TradePlan(
    string PlaybookId,
    TradeDirection Direction,
    EntryStyle Style,
    double EntryPrice,
    StopPlan Stop,
    IReadOnlyList<TargetPlan> Targets,
    ConfluenceResult Score,
    string Reason)
{
    /// <summary>Risk per contract in ticks — the unit every target is measured in.</summary>
    public int RiskTicks => this.Stop.DistanceTicks;

    public string Summarise() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1} {2} entry {3:N2} stop {4:N2} ({5} ticks) targets {6} — {7}",
        this.PlaybookId,
        this.Direction,
        this.Style,
        this.EntryPrice,
        this.Stop.Price,
        this.RiskTicks,
        string.Join(" / ", this.Targets.Select(t => $"{t.Price:N2}@{t.Ticks}t")),
        this.Reason);
}

/// <summary>
/// Everything a playbook reads. Bundled so a playbook cannot reach into a module's
/// internals: modules compose only through their published readings, which is the
/// constraint that keeps them independently testable.
/// </summary>
/// <param name="NowUtc">The instant being evaluated.</param>
/// <param name="LastPrice">Most recent trade price.</param>
/// <param name="Flow">M05's reading, or null when the trade tier is dark.</param>
/// <param name="Retest">M11's classification.</param>
/// <param name="Levels">M03's graph.</param>
/// <param name="Quality">M14's verdict.</param>
/// <param name="Break">The state of the session's range break.</param>
/// <param name="AtrTicks">Average true range on the execution timeframe, in ticks.</param>
/// <param name="MedianSpreadTicks">Prevailing spread against its own median, in ticks.</param>
/// <param name="Absorption">
/// M06's reading of both touches, or null when the depth tier is dark. Null is "not
/// measured" and never means "no absorption".
/// </param>
public sealed record PlaybookInputs(
    DateTime NowUtc,
    double LastPrice,
    FootprintReading? Flow,
    RetestState Retest,
    LevelGraph Levels,
    MicroQualityVerdict Quality,
    BreakState Break,
    double AtrTicks,
    double MedianSpreadTicks,
    AbsorptionSnapshot? Absorption = null);

/// <summary>
/// One setup. Each is a distinct object with its own eligibility, trigger, confirmation,
/// vetoes and geometry — not one indicator with six settings.
/// </summary>
public interface IPlaybook
{
    string Id { get; }

    string Name { get; }

    /// <summary>Size multiplier, per §5.</summary>
    double SizeMultiplier { get; }

    /// <summary>Whether this playbook applies to the session at all.</summary>
    Eligibility IsEligible(SessionContext context, PlaybookInputs inputs);

    /// <summary>The trigger, or null when it has not fired.</summary>
    TriggerResult? CheckTrigger(SessionContext context, PlaybookInputs inputs);

    /// <summary>
    /// Hard vetoes. Any one blocks entry regardless of score, so this returns every reason
    /// rather than the first.
    /// </summary>
    IReadOnlyList<string> Vetoes(SessionContext context, PlaybookInputs inputs);

    /// <summary>Where the stop goes.</summary>
    StopPlan BuildStop(TriggerResult trigger, SessionContext context, PlaybookInputs inputs);

    /// <summary>The four targets, or fewer when the structure does not support four.</summary>
    IReadOnlyList<TargetPlan> BuildTargets(
        TriggerResult trigger, StopPlan stop, SessionContext context, PlaybookInputs inputs);
}
