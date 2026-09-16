namespace AuctionResponse.Core;

/// <summary>Each gate of the baseline candidate rule, reported individually (Section 19).</summary>
public enum CandidateGate
{
    Data,
    Context,
    Location,
    StrongAttack,
    Direction,
    LimitedProgress,
    NoHiddenExcursion,
    ExecutionAtLevel,
    Lifecycle
}

public readonly record struct GateResult(CandidateGate Gate, bool Passed, string Detail);

/// <summary>
/// The measured inputs to the candidate rule at one decision tick. Everything is oriented
/// where the rule is oriented, and everything that could be missing is nullable.
/// </summary>
public sealed record CandidateInputs
{
    public required int Orientation { get; init; }
    public required long LevelTicks { get; init; }
    public required long ZoneLowTicks { get; init; }
    public required long ZoneHighTicks { get; init; }

    /// <summary>Current midpoint in half ticks, or null when no coherent quote exists.</summary>
    public long? MidHalfTicks { get; init; }

    /// <summary>Attacker volume V_o over the candidate window: B5 at resistance, S5 at support.</summary>
    public decimal? AttackerVolume { get; init; }

    /// <summary>Frozen attacker-volume quantile from the baseline artifact. Null blocks arming.</summary>
    public double? AttackerVolumeQuantile { get; init; }

    /// <summary>Signed volume delta D5 over the candidate window.</summary>
    public decimal? Delta { get; init; }

    /// <summary>Oriented net progress r_o in HALF ticks.</summary>
    public long? OrientedProgressHalfTicks { get; init; }

    /// <summary>Oriented maximum forward excursion M_o in HALF ticks.</summary>
    public long? OrientedExcursionHalfTicks { get; init; }

    /// <summary>Attacker volume executed inside the inclusive zone during the same window.</summary>
    public decimal? ZoneAttackerVolume { get; init; }

    /// <summary>Oriented minimum K over the window path, in HALF ticks.</summary>
    public long? OrientedMinimumHalfTicks { get; init; }

    public bool DataHealthy { get; init; }
    public string? DataReason { get; init; }
    public bool SessionEligible { get; init; }
    public bool LevelExistedBeforeWindow { get; init; }
    public bool LifecycleClear { get; init; }
    public string? LifecycleReason { get; init; }
    public Measure SideQuality { get; init; }
}

/// <summary>Outcome of evaluating the baseline rule, gate by gate.</summary>
public sealed record CandidateEvaluation(bool Arms, IReadOnlyList<GateResult> Gates)
{
    public IEnumerable<GateResult> Failures => Gates.Where(g => !g.Passed);
    public string FailureSummary => Arms ? "" : string.Join("; ", Failures.Select(f => f.Gate + ": " + f.Detail));
}

/// <summary>
/// Boundaries frozen at arming time (Section 11), all in HALF ticks so a half-tick midpoint
/// threshold stays exact:
///
///   K = min over [t0-W, t0] of o*m,   C = K - buffer,   F = max over Z of (o*p) + beyond
///
/// These are SIGNAL thresholds. They are not executable order prices.
/// </summary>
public readonly record struct CandidateBoundaries(
    long OrientedMinimumHalfTicks,
    long ConfirmationHalfTicks,
    long FailureHalfTicks,
    int Orientation)
{
    /// <summary>Actual midpoint threshold in half ticks: multiply the oriented value by o.</summary>
    public long ActualConfirmationHalfTicks => Orientation * ConfirmationHalfTicks;
    public long ActualFailureHalfTicks => Orientation * FailureHalfTicks;

    public double ConfirmationTicks => ConfirmationHalfTicks / 2d;
    public double FailureTicks => FailureHalfTicks / 2d;
    public double ActualConfirmationTicks => ActualConfirmationHalfTicks / 2d;
    public double ActualFailureTicks => ActualFailureHalfTicks / 2d;

    /// <summary>Confirmation and failure boundaries are disjoint by construction.</summary>
    public bool AreDisjoint => ConfirmationHalfTicks < FailureHalfTicks;
}

public static class CandidateRule
{
    /// <summary>
    /// Freezes K, C and F. Called once, at arming time.
    /// </summary>
    public static CandidateBoundaries Boundaries(
        int orientation, long orientedMinimumHalfTicks, long zoneLowTicks, long zoneHighTicks,
        int confirmationBufferTicks, int failureBeyondZoneTicks)
    {
        // max over p in Z of (o * p): the far zone edge in the attacking direction.
        var maxOrientedZoneTicks = orientation > 0 ? zoneHighTicks : -zoneLowTicks;

        var c = orientedMinimumHalfTicks - 2L * confirmationBufferTicks;
        var f = 2L * maxOrientedZoneTicks + 2L * failureBeyondZoneTicks;
        return new CandidateBoundaries(orientedMinimumHalfTicks, c, f, orientation);
    }

    /// <summary>
    /// Evaluates every gate of Section 11. ALL must hold at a decision tick for a candidate
    /// to arm. Each gate is reported separately so a failure can be attributed exactly, and
    /// so the acceptance tests can defeat one gate at a time.
    /// </summary>
    public static CandidateEvaluation Evaluate(CandidateInputs i, CandidateConfig cfg, double minimumKnownSideFraction)
    {
        var gates = new List<GateResult>(9);

        // 1. Data — both trades and quotes, and a side quality that clears the guard.
        var sideQualityOk = i.SideQuality.IsAvailable && i.SideQuality.Value!.Value >= minimumKnownSideFraction;
        var dataOk = i.DataHealthy && sideQualityOk;
        gates.Add(new GateResult(CandidateGate.Data, dataOk,
            !i.DataHealthy ? (i.DataReason ?? "required inputs unhealthy")
            : !i.SideQuality.IsAvailable ? "side quality unavailable"
            : !sideQualityOk ? "side quality " + i.SideQuality.Value!.Value.ToString("0.###") + " below " + minimumKnownSideFraction
            : "fresh complete quote path, usable trades"));

        // 2. Context — eligible session, and a level that predates the window.
        var contextOk = i.SessionEligible && i.LevelExistedBeforeWindow;
        gates.Add(new GateResult(CandidateGate.Context, contextOk,
            !i.SessionEligible ? "session not eligible"
            : !i.LevelExistedBeforeWindow ? "level was not declared before the candidate window opened"
            : "eligible"));

        // 3. Location — the current midpoint lies inside the frozen zone.
        bool locationOk;
        string locationDetail;
        if (i.MidHalfTicks is not { } mid) { locationOk = false; locationDetail = "no coherent midpoint"; }
        else
        {
            locationOk = mid >= 2 * i.ZoneLowTicks && mid <= 2 * i.ZoneHighTicks;
            locationDetail = locationOk
                ? "midpoint " + (mid / 2d) + " inside [" + i.ZoneLowTicks + "," + i.ZoneHighTicks + "]"
                : "midpoint " + (mid / 2d) + " outside [" + i.ZoneLowTicks + "," + i.ZoneHighTicks + "]";
        }
        gates.Add(new GateResult(CandidateGate.Location, locationOk, locationDetail));

        // 4. Strong attack — strictly greater than the frozen quantile, so a flat
        //    distribution full of ties cannot arm a candidate on equality.
        bool attackOk;
        string attackDetail;
        if (i.AttackerVolumeQuantile is not { } q) { attackOk = false; attackDetail = "no frozen baseline quantile (warmup or missing artifact)"; }
        else if (i.AttackerVolume is not { } v) { attackOk = false; attackDetail = "attacker volume unavailable"; }
        else
        {
            attackOk = (double)v > q;
            attackDetail = attackOk ? v + " exceeds Q" + cfg.AttackerVolumeQuantile + "=" + q : v + " does not exceed Q" + cfg.AttackerVolumeQuantile + "=" + q;
        }
        gates.Add(new GateResult(CandidateGate.StrongAttack, attackOk, attackDetail));

        // 5. Direction — o * D5 strictly positive.
        bool directionOk;
        string directionDetail;
        if (i.Delta is not { } d) { directionOk = false; directionDetail = "delta unavailable"; }
        else
        {
            var oriented = i.Orientation * d;
            directionOk = oriented > 0m;
            directionDetail = "oriented delta " + oriented;
        }
        gates.Add(new GateResult(CandidateGate.Direction, directionOk, directionDetail));

        // 6. Limited progress — 0 <= r_o <= 2 ticks. Negative progress fails too: the level
        //    must be under attack, not already giving way in the attacker's favour.
        bool progressOk;
        string progressDetail;
        if (i.OrientedProgressHalfTicks is not { } rh) { progressOk = false; progressDetail = "oriented progress unavailable"; }
        else
        {
            progressOk = rh >= 2L * cfg.MinimumProgressTicks && rh <= 2L * cfg.MaximumProgressTicks;
            progressDetail = "r_o = " + (rh / 2d) + " ticks";
        }
        gates.Add(new GateResult(CandidateGate.LimitedProgress, progressOk, progressDetail));

        // 7. No hidden excursion — a move away and back must not look like a defended level.
        bool excursionOk;
        string excursionDetail;
        if (i.OrientedExcursionHalfTicks is not { } mh) { excursionOk = false; excursionDetail = "oriented excursion unavailable"; }
        else
        {
            excursionOk = mh <= 2L * cfg.MaximumForwardExcursionTicks;
            excursionDetail = "M_o = " + (mh / 2d) + " ticks";
        }
        gates.Add(new GateResult(CandidateGate.NoHiddenExcursion, excursionOk, excursionDetail));

        // 8. Execution at level — volume must have traded IN the zone, not merely nearby.
        bool zoneOk;
        string zoneDetail;
        if (i.ZoneAttackerVolume is not { } ez || i.AttackerVolume is not { } vo) { zoneOk = false; zoneDetail = "zone execution unavailable"; }
        else if (vo <= 0m) { zoneOk = false; zoneDetail = "no attacker volume to divide by"; }
        else
        {
            var fraction = (double)(ez / vo);
            zoneOk = ez > 0m && fraction >= cfg.MinimumZoneVolumeFraction;
            zoneDetail = "E_Z = " + ez + ", fraction " + fraction.ToString("0.###");
        }
        gates.Add(new GateResult(CandidateGate.ExecutionAtLevel, zoneOk, zoneDetail));

        // 9. Lifecycle — one candidate per level id, and no cooldown outstanding.
        gates.Add(new GateResult(CandidateGate.Lifecycle, i.LifecycleClear, i.LifecycleReason ?? "clear"));

        return new CandidateEvaluation(gates.All(g => g.Passed), gates);
    }
}
