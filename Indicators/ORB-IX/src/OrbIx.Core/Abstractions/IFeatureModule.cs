using System;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Abstractions;

/// <summary>
/// A module's contribution to the confluence score, plus whatever typed detail the HUD and
/// the journal need to explain it.
/// </summary>
public readonly struct FeatureOutput
{
    /// <summary>
    /// Directional score in [-1, +1]. Positive favours long, negative favours short, zero
    /// is genuinely neutral rather than "no opinion" — for that, see
    /// <see cref="Confidence"/>.
    /// </summary>
    public float Score { get; }

    /// <summary>
    /// How much the module trusts its own score, in [0, 1]. A module whose data tier is
    /// absent, or whose sample is too small, reports low confidence rather than a
    /// confident zero, because the scorer treats the two differently.
    /// </summary>
    public float Confidence { get; }

    /// <summary>
    /// Module-specific detail for display and journalling. Never consumed by another
    /// module: modules compose only through the scorer, which is the constraint that keeps
    /// any of this testable.
    /// </summary>
    public object? Detail { get; }

    public FeatureOutput(float score, float confidence, object? detail = null)
    {
        if (float.IsNaN(score) || score < -1f || score > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(score), score, "A feature score must be a number in [-1, +1].");
        }

        if (float.IsNaN(confidence) || confidence < 0f || confidence > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence), confidence, "Confidence must be a number in [0, 1].");
        }

        this.Score = score;
        this.Confidence = confidence;
        this.Detail = detail;
    }

    /// <summary>
    /// The output of a module that has nothing to say — because its tier is absent, or it
    /// has not seen enough to form a view. Distinct from a score of zero at full
    /// confidence, which is an active judgement of "balanced".
    /// </summary>
    public static FeatureOutput Silent(object? detail = null) => new(0f, 0f, detail);
}

/// <summary>
/// Where a session currently is. Modules receive transitions rather than inferring them
/// from timestamps, so a replay and a live run agree by construction.
/// </summary>
public enum SessionPhase
{
    /// <summary>Outside any configured window.</summary>
    Idle,

    /// <summary>The opening range is building. No entry is permitted.</summary>
    OrForming,

    /// <summary>The range has closed and been graded. Both edges are watched.</summary>
    Armed,

    /// <summary>Past the session's entry window but still holding state.</summary>
    Closing,
}

/// <summary>
/// The one interface every feature module obeys.
///
/// The handler methods are called from the market-data path and must not compute: they
/// record. <see cref="Fold"/> is called from the periodic fold and is where work happens.
/// That separation is what keeps a quote handler allocation-free on an instrument that
/// prints thousands of times a second.
/// </summary>
public interface IFeatureModule
{
    /// <summary>Stable identifier, matching the specification's module numbering.</summary>
    string Id { get; }

    /// <summary>
    /// The data tier this module needs. When it is absent the module is disabled and its
    /// share of the score is redistributed rather than counted as zero.
    /// </summary>
    DataTier Requires { get; }

    bool Enabled { get; set; }

    void OnTick(in TickEvent tick);

    void OnBar(in BarEvent bar, TimeFrame timeFrame);

    void OnBook(in BookDelta delta);

    /// <summary>No-op for modules that do not use order-level data, or when it is absent.</summary>
    void OnL3(in L3Event l3);

    void OnSessionPhase(SessionPhase phase);

    /// <summary>
    /// Called by the periodic fold, never from a handler. Returns the module's current
    /// view given the session context.
    /// </summary>
    FeatureOutput Fold(SessionContext context);

    /// <summary>
    /// Drops all per-session state. Called at session boundaries so one session's range
    /// never leaks into the next.
    /// </summary>
    void Reset();
}

/// <summary>
/// Supplies order-level events, however they were obtained. Implementations exist for the
/// native per-order feed, for reconstruction from aggregated book deltas, and for an
/// external side-car process; consumers cannot tell them apart except through
/// <see cref="L3Event.IsReconstructed"/> and <see cref="Available"/>.
/// </summary>
public interface IL3Provider
{
    /// <summary>Which source is actually in use.</summary>
    L3Source Source { get; }

    /// <summary>
    /// Whether order-level data is genuinely flowing. False disables the modules that need
    /// it; it never causes them to invent a value.
    /// </summary>
    bool Available { get; }

    /// <summary>Human-readable explanation of <see cref="Available"/>, shown on the HUD.</summary>
    string Status { get; }

    event Action<L3Event>? Event;
}

/// <summary>
/// Time, injectable so a replay is deterministic. Nothing in Core reads the wall clock
/// directly — a module that did would produce different output on a second replay of the
/// same recording and break V1.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
