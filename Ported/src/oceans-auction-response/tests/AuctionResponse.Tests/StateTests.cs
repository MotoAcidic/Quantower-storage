using AuctionResponse.Core;

namespace AuctionResponse.Tests;

/// <summary>
/// Section 19 state tests: exact 30-second confirmation, confirmation after 30 seconds,
/// no-confirmation timeout, boundary touch equality, dwell reset by an intermediate quote,
/// stale intervals during dwell, reconnect, level deletion, session exit, configuration
/// change, cooldown isolation across levels, and repeated render calls.
/// </summary>
public static class StateTests
{
    public static void Run()
    {
        Harness.Section("Section 19 - state machine");

        DwellUnit();
        BoundaryTouchEquality();
        ConfirmExactlyAtDeadline();
        ConfirmationAfterDeadlineRefused();
        NoConfirmationTimeout();
        DwellResetByIntermediateQuote();
        StaleIntervalDuringDwell();
        InvalidationUsesFailureBoundary();
        ReconnectInterrupts();
        LevelDeletionExpires();
        SessionExitExpires();
        ConfigurationChangeExpires();
        CooldownIsolationAcrossLevels();
        ConfirmedIsImmutable();
        SupportMirror();
    }

    private static void DwellUnit()
    {
        var d = new PriceDwellTracker(1000);
        d.Observe(0, true);
        Harness.Check("dwell: not complete before the required span", !d.IsComplete(999 * Scenario.Millisecond));
        Harness.Check("dwell: complete exactly at the required span", d.IsComplete(1000 * Scenario.Millisecond));

        d.Observe(1200 * Scenario.Millisecond, false);
        Harness.Check("dwell: a violating quote clears it", !d.IsComplete(5000 * Scenario.Millisecond));

        d.Observe(1300 * Scenario.Millisecond, true);
        Harness.Check("dwell: restarts from the new quote, not the old one",
            !d.IsComplete(2299 * Scenario.Millisecond) && d.IsComplete(2300 * Scenario.Millisecond));

        d.Observe(2400 * Scenario.Millisecond, true);
        d.Break();
        Harness.Check("dwell: a stale interval breaks it as surely as a bad quote",
            !d.IsComplete(9000 * Scenario.Millisecond));
    }

    private static void BoundaryTouchEquality()
    {
        // C = 397: u(t) <= C is an inclusive test, so a midpoint EXACTLY at 397 counts.
        var s = ArmedResistance(out var boundaries);
        Harness.Equal("boundary: C is 397", 397d, boundaries.ActualConfirmationTicks);

        s.AdvanceMs(100);
        s.Quote(396, 100m, 398, 100m);   // midpoint exactly 397
        s.Trade(AggressorDirection.Sell, 300m, 397);
        Hold(s, 396, 398, 1000);

        var r = s.Tick();
        Harness.Check("boundary: touch equality confirms",
            r.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "midpoint exactly at C should satisfy u(t) <= C");
    }

    private static void ConfirmExactlyAtDeadline()
    {
        var s = ArmedResistance(out _);
        var armedNs = s.Ns;

        // Hold inside the zone until one second before the deadline.
        HoldInZone(s, armedNs + 29_000 * Scenario.Millisecond);

        // Cross below C and hold for exactly the dwell, landing on the deadline tick.
        s.Quote(395, 100m, 397, 100m);   // midpoint 396, below C = 397
        s.Trade(AggressorDirection.Sell, 400m, 396);
        while (s.Ns < armedNs + 30_000 * Scenario.Millisecond)
        {
            s.AdvanceMs(100);
            s.Quote(395, 100m, 397, 100m);
        }

        var r = s.Tick();
        var age = r.View.AllSetups.First(x => x.LevelId == "R1");
        Harness.Check("exact deadline: confirms at age 30000ms",
            r.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(r));
    }

    private static void ConfirmationAfterDeadlineRefused()
    {
        var s = ArmedResistance(out _);
        var armedNs = s.Ns;

        HoldInZone(s, armedNs + 30_250 * Scenario.Millisecond);

        // Everything a confirmation needs, but the candidate is already past its deadline.
        s.Quote(395, 100m, 397, 100m);
        s.Trade(AggressorDirection.Sell, 400m, 396);
        Hold(s, 395, 397, 1100);

        var r = s.Tick();
        Harness.Check("past deadline: expires, never confirms",
            r.Transitions.Any(t => t.NewState == SetupState.Expired) &&
            !r.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(r));

        // And a failed candidate cannot come back later under the same id.
        Hold(s, 395, 397, 2000);
        var later = s.Tick();
        Harness.Check("past deadline: no confirmation emitted later under the same id",
            !later.Transitions.Any(t => t.NewState == SetupState.Confirmed));
    }

    private static void NoConfirmationTimeout()
    {
        var s = ArmedResistance(out _);
        var armedNs = s.Ns;

        HoldInZone(s, armedNs + 30_000 * Scenario.Millisecond);
        var r = s.Tick();

        Harness.Check("timeout: expires with no terminal condition",
            r.Transitions.Any(t => t.NewState == SetupState.Expired && t.Reason.Contains("timeout")),
            "transitions were: " + Describe(r));
    }

    private static void DwellResetByIntermediateQuote()
    {
        var s = ArmedResistance(out _);

        s.AdvanceMs(200);
        s.Quote(395, 100m, 397, 100m);   // below C, dwell starts
        s.AdvanceMs(600);
        s.Quote(399, 100m, 401, 100m);   // back above C: the dwell must restart from zero
        s.AdvanceMs(600);
        s.Quote(395, 100m, 397, 100m);   // below C again, but only 0ms of dwell so far
        s.Trade(AggressorDirection.Sell, 400m, 396);
        s.AdvanceMs(200);
        s.Quote(395, 100m, 397, 100m);

        var r = s.Tick();
        Harness.Check("dwell reset: an intervening quote above C prevents confirmation",
            !r.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(r));

        // Now let the restarted dwell actually complete.
        Hold(s, 395, 397, 1000);
        var later = s.Tick();
        Harness.Check("dwell reset: confirms once the restarted dwell completes",
            later.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(later));
    }

    private static void StaleIntervalDuringDwell()
    {
        var s = ArmedResistance(out _);

        s.AdvanceMs(200);
        s.Quote(395, 100m, 397, 100m);

        // Silence past the freshness guard. A decision tick cannot infer uninterrupted
        // dwell across an interval it never observed.
        s.AdvanceMs(2500);
        s.Quote(395, 100m, 397, 100m);
        s.Trade(AggressorDirection.Sell, 400m, 396);

        var r = s.Tick();
        Harness.Check("stale during dwell: does not confirm on unobserved time",
            !r.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(r));
        Harness.Check("stale during dwell: reported as a freshness guard, not a dead feed",
            r.View.Health.State is DataState.Stale or DataState.Ready or DataState.Degraded,
            "state was " + r.View.Health.State + ": " + r.View.Health.StateReason);
    }

    private static void InvalidationUsesFailureBoundary()
    {
        var s = ArmedResistance(out var b);
        Harness.Equal("invalidation: F is 406", 406d, b.ActualFailureTicks);

        // A return through the confirmation line is NOT invalidation. Only the frozen
        // failure boundary is.
        s.AdvanceMs(200);
        s.Quote(401, 100m, 403, 100m);   // midpoint 402, above the zone but below F
        Hold(s, 401, 403, 1200);
        var notYet = s.Tick();
        Harness.Check("invalidation: above the zone but below F does not invalidate",
            !notYet.Transitions.Any(t => t.NewState == SetupState.Invalidated),
            "transitions were: " + Describe(notYet));

        s.AdvanceMs(100);
        s.Quote(405, 100m, 407, 100m);   // midpoint 406 = F exactly
        Hold(s, 405, 407, 1100);
        var invalid = s.Tick();
        Harness.Check("invalidation: fires beyond the frozen failure boundary",
            invalid.Transitions.Any(t => t.NewState == SetupState.Invalidated),
            "transitions were: " + Describe(invalid));
    }

    private static void ReconnectInterrupts()
    {
        var s = ArmedResistance(out _);

        s.AdvanceMs(500);
        s.Reconnect();
        var r = s.Tick();

        Harness.Check("reconnect: candidate terminates as DataInterrupted",
            r.Transitions.Any(t => t.NewState == SetupState.DataInterrupted),
            "transitions were: " + Describe(r));
        Harness.Equal("reconnect: a new epoch begins", 2, r.View.Health.ConnectionEpoch);
        Harness.Check("reconnect: warm-up is required again",
            r.View.Health.State is DataState.Warmup or DataState.SetupRequired,
            "state was " + r.View.Health.State);
    }

    private static void LevelDeletionExpires()
    {
        var s = ArmedResistance(out _);

        s.AdvanceMs(500);
        s.Quote(399, 100m, 401, 100m);
        s.Engine.ChangeLevel(new LevelChange(LevelChangeKind.Removed, Level("R1"), "user removed the level"));

        var r = s.Tick();
        Harness.Check("level deletion: candidate expires",
            r.Transitions.Any(t => t.NewState == SetupState.Expired),
            "transitions were: " + Describe(r));
    }

    private static void SessionExitExpires()
    {
        var s = ArmedResistance(out _);

        // Step past 15:00 Central, the session end.
        s.AdvanceMs((long)TimeSpan.FromHours(5.5).TotalMilliseconds);
        s.Quote(399, 100m, 401, 100m);
        var r = s.Tick();

        Harness.Check("session exit: candidate expires with that reason",
            r.Transitions.Any(t => t.NewState == SetupState.Expired && t.Reason.Contains("session")),
            "transitions were: " + Describe(r));
    }

    private static void ConfigurationChangeExpires()
    {
        var s = ArmedResistance(out _);

        s.AdvanceMs(500);
        s.Quote(399, 100m, 401, 100m);

        var changed = s.Engine.Config with
        {
            Candidate = s.Engine.Config.Candidate with { MaximumProgressTicks = 3 },
            ConfigurationVersion = s.Engine.Config.ConfigurationVersion + 1
        };
        var errors = s.Engine.ChangeConfiguration(new ConfigChange(changed, "test"));
        Harness.Equal("configuration change: validates cleanly", 0, errors.Count);

        var r = s.Tick();
        Harness.Check("configuration change: active candidate expires",
            r.Transitions.Any(t => t.NewState == SetupState.Expired && t.Reason.Contains("configuration")),
            "transitions were: " + Describe(r));
    }

    private static void CooldownIsolationAcrossLevels()
    {
        var s = new Scenario();
        s.DeclareResistance(400, "R1");
        s.DeclareResistance(400, "R2");   // same price, independent identity
        s.ApproachResistance();
        var armed = s.Tick();

        Harness.Check("cooldown isolation: both levels arm independently",
            armed.Transitions.Count(t => t.NewState == SetupState.Candidate) == 2,
            "armed: " + Describe(armed));

        // Expire R1 only, by removing its level; R2 must be untouched.
        s.AdvanceMs(500);
        s.Quote(399, 100m, 401, 100m);
        s.Engine.ChangeLevel(new LevelChange(LevelChangeKind.Removed, Level("R1"), "removed"));
        var r = s.Tick();

        Harness.Check("cooldown isolation: only R1 terminates",
            r.Transitions.Count(t => t.LevelId == "R1" && t.NewState.IsTerminal()) == 1 &&
            !r.Transitions.Any(t => t.LevelId == "R2" && t.NewState.IsTerminal()),
            "transitions were: " + Describe(r));

        var r2 = r.View.AllSetups.First(x => x.LevelId == "R2");
        Harness.Equal("cooldown isolation: R2 is still a live candidate", SetupState.Candidate, r2.State);
    }

    private static void ConfirmedIsImmutable()
    {
        var s = ArmedResistance(out _);

        s.AdvanceMs(100);
        s.Quote(395, 100m, 397, 100m);
        s.Trade(AggressorDirection.Sell, 400m, 396);
        Hold(s, 395, 397, 1100);
        var confirmed = s.Tick();
        Harness.Check("immutability: confirms", confirmed.Transitions.Any(t => t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(confirmed));

        var frozen = confirmed.View.AllSetups.First(x => x.LevelId == "R1").Candidate!;

        // Now the market does the opposite. The confirmed candidate must not be rewritten.
        s.AdvanceMs(100);
        s.Quote(410, 100m, 412, 100m);
        Hold(s, 410, 412, 1500);
        var after = s.Tick();

        Harness.Check("immutability: a later move does not rewrite it as invalidated",
            !after.Transitions.Any(t => t.LevelId == "R1" && t.NewState == SetupState.Invalidated),
            "transitions were: " + Describe(after));

        var still = after.View.AllSetups.First(x => x.LevelId == "R1");
        Harness.Equal("immutability: frozen evidence is unchanged",
            frozen.FrozenEvidence.BuyVolume.Value ?? -1, still.Candidate!.FrozenEvidence.BuyVolume.Value ?? -2);
    }

    private static void SupportMirror()
    {
        // The mirror exists to catch sign errors, so it is run at event level too.
        var s = new Scenario();
        s.DeclareSupport(400, "S1");

        s.QuoteStream(6 * Scenario.Second, 200 * Scenario.Millisecond, 400, 402);  // carried mid 401
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401); // mid 400
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 398, 400); // mid 399
        s.QuoteStream(1500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401); // mid 400
        s.Trade(AggressorDirection.Sell, 200m, 400);
        s.Trade(AggressorDirection.Buy, 50m, 400);
        s.AdvanceMs(100);
        s.Quote(399, 100m, 401, 100m);

        var r = s.Tick();
        Harness.Check("support mirror: arms",
            r.Transitions.Any(t => t.LevelId == "S1" && t.NewState == SetupState.Candidate),
            r.Diagnostics.GateResults.TryGetValue("S1", out var e) ? e.FailureSummary : "no evaluation");

        var c = r.View.AllSetups.First(x => x.LevelId == "S1").Candidate;
        if (c is null) { Harness.Check("support mirror: snapshot present", false); return; }

        Harness.Equal("support mirror: oriented C is -403", -403d, c.Boundaries.ConfirmationTicks);
        Harness.Equal("support mirror: oriented F is -394", -394d, c.Boundaries.FailureTicks);
        Harness.Equal("support mirror: actual confirmation midpoint is 403", 403d, c.Boundaries.ActualConfirmationTicks);
        Harness.Equal("support mirror: actual failure midpoint is 394", 394d, c.Boundaries.ActualFailureTicks);

        // And it confirms UPWARD: bullish confirmation at support.
        s.AdvanceMs(100);
        s.Quote(402, 100m, 404, 100m);   // midpoint 403, u = -403 <= C
        s.Trade(AggressorDirection.Buy, 400m, 403);
        Hold(s, 402, 404, 1100);
        var confirmed = s.Tick();

        Harness.Check("support mirror: confirms upward",
            confirmed.Transitions.Any(t => t.LevelId == "S1" && t.NewState == SetupState.Confirmed),
            "transitions were: " + Describe(confirmed));

        var status = confirmed.View.AllSetups.First(x => x.LevelId == "S1");
        Harness.Equal("support mirror: headline reads bullish", "Bullish confirmation", status.Headline);
    }

    // ------------------------------------------------------------------ helpers

    private static LevelDefinition Level(string id) => new()
    {
        LevelId = id,
        Side = LevelSide.Resistance,
        PriceTicks = 400,
        EffectiveNs = 0,
        ConnectionEpoch = 1
    };

    /// <summary>Arms the canonical resistance candidate and returns the scenario at that instant.</summary>
    private static Scenario ArmedResistance(out CandidateBoundaries boundaries)
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        var r = s.Tick();

        var status = r.View.AllSetups.FirstOrDefault(x => x.LevelId == "R1");
        if (status?.Candidate is null)
            throw new InvalidOperationException("Scenario failed to arm: " +
                (r.Diagnostics.GateResults.TryGetValue("R1", out var e) ? e.FailureSummary : "no evaluation"));

        boundaries = status.Candidate.Boundaries;
        return s;
    }

    /// <summary>
    /// Holds a fixed quote for a span, emitting it every 100ms. A dwell can only be claimed
    /// over OBSERVED time, so a test that wants a dwell to complete has to supply the quotes
    /// that make it observed.
    /// </summary>
    private static void Hold(Scenario s, long bidTicks, long askTicks, long ms)
    {
        var end = s.Ns + ms * Scenario.Millisecond;
        while (s.Ns < end)
        {
            s.AdvanceMs(100);
            s.Quote(bidTicks, 100m, askTicks, 100m);
        }
    }

    /// <summary>Holds the midpoint inside the zone, ticking every 250ms as the host would.</summary>
    private static void HoldInZone(Scenario s, long untilNs)
    {
        while (s.Ns < untilNs)
        {
            s.AdvanceMs(100);
            s.Quote(399, 100m, 401, 100m);
            if (s.Ns % (250 * Scenario.Millisecond) == 0) s.Tick();
        }
    }

    private static string Describe(DecisionResult r) =>
        r.Transitions.Count == 0
            ? "<none>"
            : string.Join(", ", r.Transitions.Select(t => t.LevelId + " " + t.OldState + "->" + t.NewState + " (" + t.Reason + ")"));
}
