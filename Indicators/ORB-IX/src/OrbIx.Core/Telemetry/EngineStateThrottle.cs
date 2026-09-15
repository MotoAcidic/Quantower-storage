using System;
using System.Collections.Generic;

namespace OrbIx.Core.Telemetry;

/// <summary>
/// Whether a decision is worth a journal row, and how many were folded into it.
/// </summary>
/// <param name="ShouldRecord">Whether to write a row for this evaluation.</param>
/// <param name="EvaluationsSincePrevious">
/// How many evaluations happened between the previous recorded row and this one, the
/// suppressed ones included. Zero on the first row of a run.
///
/// THIS FIELD IS WHAT MAKES THE COLLAPSE LOSSLESS. A reader knows the previous row's decision
/// held for exactly this many further evaluations, because a change would have written a row.
/// Without it, "one row" and "one row standing for eight thousand" are indistinguishable.
/// </param>
public readonly record struct EngineStateEmission(bool ShouldRecord, int EvaluationsSincePrevious);

/// <summary>
/// Collapses a run of identical engine decisions into one journal row.
///
/// WHAT THIS FIXES, measured on the live host 2026-09-03: the journal held 8,972 EngineState
/// rows of which 8,970 said "no plan" and 2 carried a setup, with 413 carrying vetoes. Ninety
/// five per cent of the file recorded that nothing happened, on every fold, forever, and the
/// 415 rows that matter were buried in it.
///
/// THE ROW IS NOT SIMPLY DROPPED, and that distinction is the whole design. Its own comment at
/// the call site states why it exists: "the same session driven through the replay must reach
/// the same decision, and only a record of what the chart decided makes that checkable after
/// the fact." Deleting it would trade a noisy file for an unfalsifiable one.
///
/// So the run is COLLAPSED rather than discarded. A row is written when the decision changes,
/// carrying how many evaluations the previous decision survived, which is strictly more
/// information than the repeated rows conveyed and takes one line instead of thousands.
///
/// A HEARTBEAT BOUNDS THE QUIET. Without it a chart that decides nothing all day leaves a
/// single row with no evidence it kept running, and "evaluated five thousand times" would look
/// identical to "evaluated once and stopped". At most one row per interval settles that:
/// around ninety-six a day against the eight and a half thousand measured.
/// </summary>
public sealed class EngineStateThrottle
{
    /// <summary>
    /// How long an unchanged decision may go unrecorded.
    ///
    /// Fifteen minutes: long enough that a quiet session costs about a hundred rows instead of
    /// thousands, short enough that a gap in the file is never ambiguous for long. A constant
    /// rather than a setting, because a knob nobody turns is a knob that goes stale.
    /// </summary>
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromMinutes(15);

    private readonly TimeSpan heartbeat;

    private string? lastDetail;
    private string? lastVetoes;
    private DateTime lastRecordedUtc;
    private bool haveRecorded;
    private int sincePrevious;

    /// <param name="heartbeat">
    /// How long an unchanged decision may go unrecorded, or null for
    /// <see cref="DefaultHeartbeat"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The heartbeat is not positive.</exception>
    public EngineStateThrottle(TimeSpan? heartbeat = null)
    {
        var interval = heartbeat ?? DefaultHeartbeat;

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeat),
                $"The heartbeat must be positive; got {interval}. A zero or negative interval "
                + "would record every evaluation, which is the behaviour this type exists to end.");
        }

        this.heartbeat = interval;
    }

    /// <summary>
    /// Observes one evaluation and says whether it earns a row.
    /// </summary>
    /// <param name="detail">The decision, as the journal would record it.</param>
    /// <param name="vetoes">
    /// Why it was refused. PART OF THE IDENTITY: the same "no plan" with a different veto set
    /// is a different decision, and treating them as one would hide exactly the transitions
    /// this record exists to witness.
    ///
    /// ANNOTATED NULLABLE at this boundary deliberately: the guard exists so a null arrives as
    /// a named exception naming this parameter, rather than as a NullReferenceException from
    /// inside the comparison.
    /// </param>
    /// <param name="nowUtc">When the evaluation happened.</param>
    /// <exception cref="ArgumentNullException">The veto list is null.</exception>
    public EngineStateEmission Observe(
        string detail, IReadOnlyList<string>? vetoes, DateTime nowUtc)
    {
        if (vetoes is null)
            throw new ArgumentNullException(nameof(vetoes));

        var signature = string.Join("", vetoes);
        var changed = !this.haveRecorded
                      || !string.Equals(this.lastDetail, detail, StringComparison.Ordinal)
                      || !string.Equals(this.lastVetoes, signature, StringComparison.Ordinal);

        // Time is compared against the LAST RECORDED row, not the last observation, so a run of
        // suppressed evaluations cannot keep pushing the heartbeat out of reach.
        var stale = this.haveRecorded && nowUtc - this.lastRecordedUtc >= this.heartbeat;

        if (!changed && !stale)
        {
            this.sincePrevious++;
            return new EngineStateEmission(false, this.sincePrevious);
        }

        var folded = this.sincePrevious;

        this.lastDetail = detail;
        this.lastVetoes = signature;
        this.lastRecordedUtc = nowUtc;
        this.haveRecorded = true;
        this.sincePrevious = 0;

        return new EngineStateEmission(true, folded);
    }
}
