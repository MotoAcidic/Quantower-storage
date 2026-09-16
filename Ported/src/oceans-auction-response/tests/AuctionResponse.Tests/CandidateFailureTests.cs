using AuctionResponse.Core;

namespace AuctionResponse.Tests;

/// <summary>
/// Section 19: every candidate condition must be defeatable INDEPENDENTLY, and none of
/// these may arm. Each case drives real trades and quotes through the engine and then
/// asserts both that nothing armed and that the RIGHT gate is the one that refused.
///
/// Asserting the specific gate matters: a test that only checks "did not arm" passes even
/// when the candidate was blocked for an unrelated reason, which is how a broken gate hides.
/// </summary>
public static class CandidateFailureTests
{
    public static void Run()
    {
        Harness.Section("Section 19 - candidate failure, one condition at a time");

        Baseline();
        VolumeAtQuantile();
        WrongSignDelta();
        ZeroDelta();
        PriceOutsideZone();
        ProgressTooLarge();
        ExcursionThenReturn();
        NegativeProgress();
        InsufficientZoneExecutions();
        ExcessiveUnknownSide();
        MissingBaseline();
        LevelCreatedInsideWindow();
        CooldownBlocksRearm();
    }

    /// <summary>The control: the canonical approach DOES arm, so the failures below mean something.</summary>
    private static void Baseline()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        var r = s.Tick();

        Harness.Check("control: canonical approach arms", Armed(r), Why(r, "R1"));
        var status = r.View.AllSetups.First(x => x.LevelId == "R1");
        Harness.Equal("control: boundaries C", 397d, status.Candidate!.Boundaries.ActualConfirmationTicks);
        Harness.Equal("control: boundaries F", 406d, status.Candidate!.Boundaries.ActualFailureTicks);
    }

    private static void VolumeAtQuantile()
    {
        // EXACTLY at Q0.90. "Strictly greater" exists so a flat distribution full of ties
        // cannot arm a candidate on equality.
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance(buyVolume: 150m, sellVolume: 50m);
        var r = s.Tick();

        AssertRefused("volume equal to the quantile", r, CandidateGate.StrongAttack);
    }

    private static void WrongSignDelta()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance(buyVolume: 200m, sellVolume: 260m);
        var r = s.Tick();

        AssertRefused("wrong-sign delta at resistance", r, CandidateGate.Direction);
    }

    private static void ZeroDelta()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance(buyVolume: 200m, sellVolume: 200m);
        var r = s.Tick();

        AssertRefused("zero delta", r, CandidateGate.Direction);
    }

    private static void PriceOutsideZone()
    {
        var s = new Scenario();
        s.DeclareResistance(420);        // zone [418,422]; the approach sits at midpoint 400
        s.ApproachResistance();
        var r = s.Tick();

        AssertRefused("midpoint outside the zone", r, CandidateGate.Location);
    }

    private static void ProgressTooLarge()
    {
        var s = new Scenario();
        s.DeclareResistance(400);

        s.QuoteStream(6 * Scenario.Second, 200 * Scenario.Millisecond, 394, 396);  // carried mid 395
        s.QuoteStream(4500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401); // mid 400
        s.Trade(AggressorDirection.Buy, 200m, 400);
        s.Trade(AggressorDirection.Sell, 50m, 400);
        s.AdvanceMs(100);
        s.Quote(399, 100m, 401, 100m);

        var r = s.Tick();
        // r_o = 400 - 395 = 5 ticks: the level gave way, it was not defended.
        AssertRefused("net progress beyond two ticks", r, CandidateGate.LimitedProgress);
    }

    private static void ExcursionThenReturn()
    {
        var s = new Scenario();
        s.DeclareResistance(400);

        s.QuoteStream(6 * Scenario.Second, 200 * Scenario.Millisecond, 398, 400);  // carried mid 399
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 402, 404); // mid 403 - excursion 4
        s.QuoteStream(3000 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401); // back to mid 400
        s.Trade(AggressorDirection.Buy, 200m, 400);
        s.Trade(AggressorDirection.Sell, 50m, 400);
        s.AdvanceMs(100);
        s.Quote(399, 100m, 401, 100m);

        var r = s.Tick();
        // Net progress still looks like +1 tick; only the excursion check catches the move
        // away and back. This is the case the excursion gate exists for.
        var eval = r.Diagnostics.GateResults["R1"];
        Harness.Check("excursion: net progress alone still looks benign",
            eval.Gates.First(g => g.Gate == CandidateGate.LimitedProgress).Passed,
            "expected the progress gate to pass so the excursion gate is what refuses");
        AssertRefused("forward excursion above two then return", r, CandidateGate.NoHiddenExcursion);
    }

    private static void NegativeProgress()
    {
        var s = new Scenario();
        s.DeclareResistance(400);

        s.QuoteStream(6 * Scenario.Second, 200 * Scenario.Millisecond, 400, 402);  // carried mid 401
        s.QuoteStream(4500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401); // mid 400
        s.Trade(AggressorDirection.Buy, 200m, 400);
        s.Trade(AggressorDirection.Sell, 50m, 400);
        s.AdvanceMs(100);
        s.Quote(399, 100m, 401, 100m);

        var r = s.Tick();
        // r_o = -1: price moved AGAINST the attacker. Not limited progress, wrong direction.
        AssertRefused("negative net progress", r, CandidateGate.LimitedProgress);
    }

    private static void InsufficientZoneExecutions()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        // All the aggression happened somewhere else; volume near a level is not volume at it.
        s.ApproachResistance(buyVolume: 200m, sellVolume: 50m, zonePriceTicks: 410);
        var r = s.Tick();

        AssertRefused("volume executed outside the zone", r, CandidateGate.ExecutionAtLevel);
    }

    private static void ExcessiveUnknownSide()
    {
        var s = new Scenario();
        s.DeclareResistance(400);

        s.QuoteStream(6 * Scenario.Second, 200 * Scenario.Millisecond, 398, 400);
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401);
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 400, 402);
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401);
        s.Trade(AggressorDirection.Buy, 200m, 400);
        s.Trade(AggressorDirection.Sell, 50m, 400);
        s.Trade(AggressorDirection.Unknown, 100m, 400);   // 250/350 = 0.714, below 0.95
        s.AdvanceMs(100);
        s.Quote(399, 100m, 401, 100m);

        var r = s.Tick();
        AssertRefused("side quality below the guard", r, CandidateGate.Data);
    }

    private static void MissingBaseline()
    {
        var s = new Scenario(withBaseline: false);
        s.DeclareResistance(400);
        s.ApproachResistance();
        var r = s.Tick();

        Harness.Check("missing baseline: nothing arms", !Armed(r), Why(r, "R1"));
        Harness.Equal("missing baseline: state is Warmup", DataState.Warmup, r.View.Health.State);
        Harness.Check("missing baseline: reason names the artifact",
            (r.View.Health.BaselineReason ?? "").Contains("baseline", StringComparison.OrdinalIgnoreCase),
            "reason was: " + (r.View.Health.BaselineReason ?? "<none>"));

        var eval = r.Diagnostics.GateResults["R1"];
        Harness.Check("missing baseline: strong-attack gate refuses for want of a quantile",
            !eval.Gates.First(g => g.Gate == CandidateGate.StrongAttack).Passed);
    }

    private static void LevelCreatedInsideWindow()
    {
        var s = new Scenario();
        s.ApproachResistance();

        // Declared with an effective time inside the very window it would be judged on.
        s.Engine.AddLevel(new LevelDefinition
        {
            LevelId = "R1",
            Side = LevelSide.Resistance,
            PriceTicks = 400,
            EffectiveNs = s.Ns - 2 * Scenario.Second,
            ConnectionEpoch = s.Engine.ConnectionEpoch,
            Source = "test"
        });

        var r = s.Tick();
        AssertRefused("level created during its own candidate window", r, CandidateGate.Context);
    }

    private static void CooldownBlocksRearm()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        var armed = s.Tick();
        Harness.Check("cooldown: candidate armed first", Armed(armed), Why(armed, "R1"));

        // Let it time out with the feed HEALTHY throughout, so the terminal reason is the
        // timeout rather than a data interruption caused by test silence.
        s.QuoteStream(31 * Scenario.Second, 100 * Scenario.Millisecond, 399, 401);
        var expired = s.Tick();
        Harness.Check("cooldown: candidate terminated after the timeout",
            expired.Transitions.Any(t => t.NewState == SetupState.Expired),
            "expected an Expired transition");

        s.ApproachResistance();
        var reArm = s.Tick();
        AssertRefused("cooldown blocks an immediate re-arm at the same level", reArm, CandidateGate.Lifecycle);
    }

    // ------------------------------------------------------------------ helpers

    private static bool Armed(DecisionResult r) =>
        r.Transitions.Any(t => t.NewState == SetupState.Candidate);

    private static string Why(DecisionResult r, string levelId) =>
        r.Diagnostics.GateResults.TryGetValue(levelId, out var e) ? e.FailureSummary : "no gate evaluation recorded";

    private static void AssertRefused(string name, DecisionResult r, CandidateGate expectedGate)
    {
        Harness.Check(name + ": does not arm", !Armed(r), "a candidate armed when it should not have");

        if (!r.Diagnostics.GateResults.TryGetValue("R1", out var eval))
        {
            Harness.Check(name + ": gate recorded", false, "no gate evaluation for level R1");
            return;
        }

        var gate = eval.Gates.FirstOrDefault(g => g.Gate == expectedGate);
        Harness.Check(name + ": refused by " + expectedGate, !gate.Passed,
            "the " + expectedGate + " gate passed; refusal was: " + eval.FailureSummary);
    }
}
