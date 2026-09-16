using AuctionResponse.Core;

namespace AuctionResponse.Tests;

/// <summary>
/// Section 19 UI and safety invariants, plus the health, config and unit-level guards that
/// the rest of the suite depends on being true.
/// </summary>
public static class InvariantTests
{
    public static void Run()
    {
        Harness.Section("Units, grid and configuration");
        TickGridRejectsOffGrid();
        ConfigValidation();
        MissingInputsKeepSetupRequired();
        ConfigHashStability();

        Harness.Section("Health and ingress");
        IngressOverflowFlagSurvivesTheQueue();
        WindowAvailabilityIsNotZero();
        DepthIntegralCoversTheWindow();

        Harness.Section("Section 19 - UI and safety invariants");
        FrozenAndLiveEvidenceDiffer();
        UnavailableValuesCarryReasons();
        OverflowArrowsOnClippedCoordinates();
        NoMarkerBeforeItsDetectionTime();
        NoPercentagesInBaseline();
        DescriptiveLabelDwell();
        ReadOnlyByConstruction();
        PlotScaleDoesNotChangeSignals();
    }

    private static void TickGridRejectsOffGrid()
    {
        var grid = new TickGrid(0.25m);

        Harness.Equal("grid: exact conversion", 97401L, grid.ToTicks(24350.25m));
        Harness.Equal("grid: round trip", 24350.25m, grid.ToPrice(97401));
        Harness.Check("grid: on-grid detection", grid.IsOnGrid(24350.50m));
        Harness.Check("grid: off-grid detection", !grid.IsOnGrid(24350.30m));

        // Rounding an off-grid price into validity would silently move a level.
        Harness.Throws<ArgumentException>("grid: off-grid price is rejected, not rounded",
            () => grid.ToTicks(24350.30m));
        Harness.Check("grid: TryToTicks reports failure", !grid.TryToTicks(24350.30m, out _));

        Harness.Throws<ArgumentOutOfRangeException>("grid: zero tick size is refused", () => new TickGrid(0m));
        Harness.Throws<ArgumentOutOfRangeException>("grid: negative tick size is refused", () => new TickGrid(-0.25m));

        // Half-tick midpoints stay exact.
        Harness.Equal("grid: half-tick midpoint", 24350.375m, grid.ToPriceFromHalfTicks(194803));
    }

    private static void ConfigValidation()
    {
        Harness.Equal("config: defaults validate", 0, new Config().Validate().Count);

        var badQuantile = new Config { Candidate = new CandidateConfig { AttackerVolumeQuantile = 1.0 } };
        Harness.Check("config: percentile must be in the open interval (0,1)",
            badQuantile.Validate().Any(e => e.Contains("attackerVolumeQuantile")));

        var badFraction = new Config { Candidate = new CandidateConfig { MinimumZoneVolumeFraction = 1.5 } };
        Harness.Check("config: fractions must lie in [0,1]",
            badFraction.Validate().Any(e => e.Contains("minimumZoneVolumeFraction")));

        var badDwell = new Config { Candidate = new CandidateConfig { ConfirmationPriceDwellMs = 40000 } };
        Harness.Check("config: a dwell longer than the timeout is rejected as incompatible",
            badDwell.Validate().Any(e => e.Contains("confirmationPriceDwellMs")));

        var noFiveSecond = new Config { Timing = new TimingConfig { WindowsMs = new[] { 1000, 30000 } } };
        Harness.Check("config: the 5-second window must be present",
            noFiveSecond.Validate().Any(e => e.Contains("5-second")));

        var trading = new Config { Alerts = new AlertsConfig { AutomaticTradingEnabled = true } };
        Harness.Check("config: automatic trading cannot be enabled at all",
            trading.Validate().Any(e => e.Contains("read-only")));

        var negativeTick = new Config { Instrument = new InstrumentConfig { TickSize = -1m } };
        Harness.Check("config: tick size must be positive",
            negativeTick.Validate().Any(e => e.Contains("tickSize")));
    }

    private static void MissingInputsKeepSetupRequired()
    {
        // A JSON file that parsed is not permission to alert.
        var bare = new Config();
        var missing = bare.MissingRequiredInputs();
        Harness.Check("setup: bare defaults are missing the instrument", missing.Any(m => m.Contains("instrumentKey")));
        Harness.Check("setup: bare defaults are missing the tick size", missing.Any(m => m.Contains("tickSize")));
        // The baseline is NOT an owner input any more: the engine observes its own. Its
        // absence is Warmup, not SetupRequired, which is what Section 8 actually calls for.
        Harness.Check("setup: the baseline is not demanded as an owner input",
            !missing.Any(m => m.Contains("artifactPath")), string.Join(", ", missing));
        Harness.Check("setup: bare defaults are missing recording approval", missing.Any(m => m.Contains("permissionConfirmed")));

        Harness.Equal("setup: a fully resolved config is missing nothing",
            0, Scenario.ReadyConfig().MissingRequiredInputs().Count);

        // And the engine actually holds SetupRequired when something is missing.
        var s = new Scenario(config: new Config
        {
            Instrument = new InstrumentConfig { InstrumentKey = new InstrumentKey("MNQ", "CME", "202612"), TickSize = 0.25m },
            Session = Scenario.ReadyConfig().Session,
            // A recording directory with no approval: the one thing still genuinely required.
            Recording = new RecordingConfig { Directory = "x", PermissionConfirmed = false, RequireRecorderForResearch = true }
        });
        s.QuoteStream(1 * Scenario.Second, 100 * Scenario.Millisecond, 399, 401);
        s.Trade(AggressorDirection.Buy, 10m, 400);
        var r = s.Tick();

        Harness.Equal("setup: engine holds SetupRequired", DataState.SetupRequired, r.View.Health.State);

        // The displayed reason is written for a person, so it names the thing in plain
        // language; the machine-readable key travels alongside it on the snapshot.
        Harness.Check("setup: the reason names the missing input in plain language",
            r.View.Health.StateReason.Contains("record", StringComparison.OrdinalIgnoreCase),
            "reason was: " + r.View.Health.StateReason);
        Harness.Check("setup: the structured list carries the config key",
            r.View.Health.MissingInputs.Any(mi => mi.Key.StartsWith("recording.")),
            "keys were: " + string.Join(", ", r.View.Health.MissingInputs.Select(mi => mi.Key)));
        Harness.Check("setup: every missing input explains how to supply it",
            r.View.Health.MissingInputs.All(mi => !string.IsNullOrWhiteSpace(mi.HowToSupply)));
    }

    private static void ConfigHashStability()
    {
        var a = Scenario.ReadyConfig();
        var b = Scenario.ReadyConfig();
        Harness.Check("config hash: identical configs hash identically", a.Hash() == b.Hash());

        var changed = a with { Candidate = a.Candidate with { MaximumProgressTicks = 3 } };
        Harness.Check("config hash: a feature change changes the hash", a.Hash() != changed.Hash());
        Harness.Check("config hash: reported as a feature change", changed.IsFeatureChangeFrom(a));

        var cosmetic = a with { Display = a.Display with { MaximumRequestedFramesPerSecond = 5 } };
        Harness.Check("config hash: a display-only change is not a feature change",
            !cosmetic.IsFeatureChangeFrom(a),
            "changing the frame rate must not expire live candidates");
    }

    private static void IngressOverflowFlagSurvivesTheQueue()
    {
        var ingress = new EventIngress(4);
        MarketEvent Make(long seq) => new()
        {
            Kind = EventKind.Trade, EventSequence = seq, ReceiveElapsedNs = seq,
            ReceiveUtc = Scenario.SessionBaseUtc, OriginalQuantity = 1m, PriceTicks = 400
        };

        for (var i = 0; i < 4; i++)
            Harness.Check("ingress: accepts up to capacity (" + i + ")", ingress.TryEnqueue(Make, out _));

        Harness.Check("ingress: refuses past capacity", !ingress.TryEnqueue(Make, out _));
        Harness.Check("ingress: overflow is flagged", ingress.HasOverflowed);
        Harness.Equal("ingress: overflow is counted", 1, ingress.OverflowCount);

        // Drain everything. The flag must NOT drain away with the events it describes.
        var drained = new List<MarketEvent>();
        ingress.Drain(drained);
        Harness.Equal("ingress: drained in order", 4, drained.Count);
        Harness.Check("ingress: sequence is monotonic",
            drained.Select(e => e.EventSequence).SequenceEqual(new long[] { 1, 2, 3, 4 }));
        Harness.Check("ingress: the overflow flag survives the drain", ingress.HasOverflowed,
            "the evidence of loss must not be lost with the events");

        ingress.Resynchronise();
        Harness.Check("ingress: resynchronise keeps the fault flag", ingress.HasOverflowed);

        // Sequence numbers are assigned even to refused events, so a gap is detectable.
        Harness.Equal("ingress: refused events still consumed a sequence", 5, ingress.LastAssignedSequence);
    }

    private static void WindowAvailabilityIsNotZero()
    {
        var w = new TradeWindow(5000);
        var empty = w.NormalizedDelta(0);
        Harness.Check("availability: empty window has no normalized delta", !empty.IsAvailable);
        Harness.Null("availability: the value is null", empty.Value);
        Harness.Check("availability: it explains itself", !string.IsNullOrEmpty(empty.Reason));

        var i = BookFlow.QueueImbalance(0m, 0m, 0);
        Harness.Check("availability: zero depth is unavailable, not balanced", !i.IsAvailable);
        Harness.Check("availability: N/A renders with a reason", i.ToString().StartsWith("N/A"),
            "rendered as: " + i);

        // Unknown-only volume: total exists, but there is no signed flow to normalize.
        var unknownOnly = new TradeWindow(5000);
        unknownOnly.Add(1, AggressorDirection.Unknown, 50m, 400);
        Harness.Check("availability: unknown-only volume gives no normalized delta",
            !unknownOnly.NormalizedDelta(1).IsAvailable);
        Harness.Equal("availability: but it does count toward side quality", 0d, unknownOnly.SideQuality(1).Value);
    }

    private static void DepthIntegralCoversTheWindow()
    {
        var flow = new BookFlowWindow(1000);

        // One state in effect for the whole window: mean depth is that state's depth.
        flow.Accept(new QuoteState(0, 400, 402, 100m, 60m, 0));
        flow.Advance(2000 * Scenario.Millisecond);
        var mean = flow.MeanDepth(2000 * Scenario.Millisecond);
        Harness.Equal("depth: a carried state covers the whole window", 80d, mean.Value, 1e-9);

        // Half the window at 80, half at 40.
        var f2 = new BookFlowWindow(1000);
        f2.Accept(new QuoteState(0, 400, 402, 100m, 60m, 0));                             // avg 80
        f2.Accept(new QuoteState(1500 * Scenario.Millisecond, 400, 402, 50m, 30m, 1));    // avg 40
        f2.Advance(2000 * Scenario.Millisecond);
        Harness.Equal("depth: time-weighted across a mid-window change", 60d,
            f2.MeanDepth(2000 * Scenario.Millisecond).Value, 1e-9);

        // No state spanning the window start: representative depth is unavailable.
        var f3 = new BookFlowWindow(1000);
        f3.Accept(new QuoteState(1800 * Scenario.Millisecond, 400, 402, 100m, 60m, 0));
        f3.Advance(2000 * Scenario.Millisecond);
        Harness.Check("depth: an uncovered window is unavailable, not extrapolated",
            !f3.MeanDepth(2000 * Scenario.Millisecond).IsAvailable);

        Harness.Check("depth: missing depth makes F unavailable",
            !f3.DepthNormalizedOfi(2000 * Scenario.Millisecond, 1m).IsAvailable);
    }

    private static void FrozenAndLiveEvidenceDiffer()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        var armed = s.Tick();

        var armedCandidate = armed.View.AllSetups.FirstOrDefault(x => x.LevelId == "R1")?.Candidate;
        if (armedCandidate is null)
        {
            Harness.Check("frozen evidence: scenario armed", false,
                armed.Diagnostics.GateResults.TryGetValue("R1", out var e) ? e.FailureSummary : "no evaluation recorded");
            return;
        }
        var frozen = armedCandidate.FrozenEvidence;

        // Move the market decisively the other way, then look again.
        for (var i = 0; i < 20; i++)
        {
            s.AdvanceMs(100);
            s.Quote(395, 100m, 397, 100m);
        }
        s.Trade(AggressorDirection.Sell, 900m, 396);
        var later = s.Tick();

        var stillFrozen = later.View.AllSetups.First(x => x.LevelId == "R1").Candidate!.FrozenEvidence;

        Harness.Equal("frozen evidence: unchanged after the market moved",
            frozen.BuyVolume.Value ?? -1, stillFrozen.BuyVolume.Value ?? -2);
        Harness.Check("frozen evidence: visibly differs from live evidence",
            Math.Abs((stillFrozen.Delta.Value ?? 0) - (later.View.Live.Delta.Value ?? 0)) > 1e-9,
            "frozen and live delta were identical, so the UI could not show them apart");
        Harness.Check("frozen evidence: is a separate object from live",
            !ReferenceEquals(stillFrozen, later.View.Live));
    }

    private static void UnavailableValuesCarryReasons()
    {
        var s = new Scenario();
        s.QuoteStream(500 * Scenario.Millisecond, 100 * Scenario.Millisecond, 399, 401);
        var r = s.Tick();

        // The optional modules are OFF, and they say so rather than reporting zero.
        Harness.Check("optional modules: residual is unavailable, not zero",
            !r.View.Live.ResidualEvidence.IsAvailable && r.View.Live.ResidualEvidence.Reason!.Contains("disabled"),
            "was: " + r.View.Live.ResidualEvidence);
        Harness.Check("optional modules: MBO evidence is unavailable, not zero",
            !r.View.Live.NetAdditionEvidence.IsAvailable && r.View.Live.NetAdditionEvidence.Reason!.Contains("disabled"),
            "was: " + r.View.Live.NetAdditionEvidence);
        Harness.Check("health: MBO capability is explicitly unverified",
            !r.View.Health.Capabilities.MarketByOrderVerified &&
            !string.IsNullOrEmpty(r.View.Health.Capabilities.MboUnverifiedReason));
    }

    private static void OverflowArrowsOnClippedCoordinates()
    {
        var inside = PlotMath.Coordinates(40, 80, -2, 4, 0);
        Harness.Check("plot: no overflow inside the clip", !inside.OverflowX && !inside.OverflowY);

        var beyond = PlotMath.Coordinates(4000, 80, -900, 4, 0);
        Harness.Check("plot: overflow is flagged so an arrow can be drawn", beyond.OverflowX && beyond.OverflowY);
        Harness.Equal("plot: drawing coordinate is clipped", 3d, beyond.XClipped);
        Harness.Equal("plot: drawing coordinate is clipped", -3d, beyond.YClipped);
        Harness.Equal("plot: the STORED value is unmodified", 50d, beyond.X);
        Harness.Equal("plot: the STORED value is unmodified", -225d, beyond.Y);

        var neutral = PlotMath.Coordinates(1, 80, 0.1, 4, 0);
        Harness.Check("plot: the neutral band is visual de-emphasis only", neutral.IsNeutral);

        // Screen mapping: centre of the rectangle for the origin, corners at the clip.
        var (cx, cy) = PlotMath.ToScreen(PlotMath.Coordinates(0, 1, 0, 1, 0), 0, 0, 600, 400);
        Harness.Equal("plot: origin maps to the panel centre (x)", 300d, cx, 1e-9);
        Harness.Equal("plot: origin maps to the panel centre (y)", 200d, cy, 1e-9);
    }

    private static void NoMarkerBeforeItsDetectionTime()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        var armed = s.Tick();

        var t = armed.Transitions.First(x => x.NewState == SetupState.Candidate);
        var candidate = armed.View.AllSetups.First(x => x.LevelId == "R1").Candidate!;

        Harness.Check("markers: detection time is the decision tick, not the earlier extreme",
            t.DetectionElapsedNs == armed.View.PublishedAtNs,
            "detection " + t.DetectionElapsedNs + " vs tick " + armed.View.PublishedAtNs);
        Harness.Check("markers: the frozen evidence predates nothing in the future",
            candidate.FrozenEvidence.AtNs <= armed.View.PublishedAtNs);
        Harness.Check("markers: history holds no entry later than the current view",
            armed.View.History.All(h => h.TransitionElapsedNs <= armed.View.PublishedAtNs));
        Harness.Check("markers: history is newest first",
            armed.View.History.Select(h => h.TransitionElapsedNs).SequenceEqual(
                armed.View.History.Select(h => h.TransitionElapsedNs).OrderByDescending(x => x)));
        Harness.Check("markers: event ids are unique",
            armed.View.History.Select(h => h.EventId).Distinct().Count() == armed.View.History.Count);
    }

    private static void NoPercentagesInBaseline()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        var r = s.Tick();

        Harness.Check("baseline: no model output is present", r.View.Model is null,
            "the probability panel must stay hidden or read 'Not calibrated' until a validated model is loaded");

        var status = r.View.AllSetups.First(x => x.LevelId == "R1");
        Harness.Check("baseline: the status headline carries no probability",
            !status.Headline.Contains('%'), "headline was: " + status.Headline);
        Harness.Check("baseline: transitions carry no model hash", r.Transitions.All(t => t.ModelHash is null));
    }

    private static void DescriptiveLabelDwell()
    {
        var tracker = new DescriptiveLabelTracker(5000);

        Harness.Equal("label: starts unavailable", DescriptiveLabel.Unavailable,
            tracker.Update(DescriptiveLabel.BuyingAdvances, 0));
        Harness.Equal("label: still not switched before the dwell", DescriptiveLabel.Unavailable,
            tracker.Update(DescriptiveLabel.BuyingAdvances, 4999 * Scenario.Millisecond));
        Harness.Equal("label: switches once the dwell completes", DescriptiveLabel.BuyingAdvances,
            tracker.Update(DescriptiveLabel.BuyingAdvances, 5000 * Scenario.Millisecond));

        // A flicker restarts the dwell rather than flipping the display.
        tracker.Update(DescriptiveLabel.SellingAdvances, 5100 * Scenario.Millisecond);
        Harness.Equal("label: a brief opposite reading does not flip it", DescriptiveLabel.BuyingAdvances,
            tracker.Update(DescriptiveLabel.BuyingAdvances, 5200 * Scenario.Millisecond));

        // Missing data shows Unavailable IMMEDIATELY, with no dwell.
        Harness.Equal("label: missing data shows unavailable at once", DescriptiveLabel.Unavailable,
            tracker.Update(DescriptiveLabel.Unavailable, 5300 * Scenario.Millisecond));

        // Proposal logic.
        var m = Measure.Of(40, "qty", 5000, 0);
        var up = Measure.Of(2, "ticks", 5000, 0);
        var down = Measure.Of(-2, "ticks", 5000, 0);
        var point = PlotMath.Coordinates(40, 10, 2, 1, 0);
        Harness.Equal("label: buying with upward progress", DescriptiveLabel.BuyingAdvances,
            DescriptiveLabelTracker.Propose(m, up, point));
        Harness.Equal("label: buying with downward progress is Mixed, not a reversal claim",
            DescriptiveLabel.Mixed, DescriptiveLabelTracker.Propose(m, down, PlotMath.Coordinates(40, 10, -2, 1, 0)));
        Harness.Equal("label: missing input is unavailable",
            DescriptiveLabel.Unavailable,
            DescriptiveLabelTracker.Propose(Measure.Unavailable("qty", 5000, 0, "no volume"), up, point));
    }

    private static void ReadOnlyByConstruction()
    {
        // There must be no reachable order-submission path. Assert it structurally: the
        // Core assembly must not reference anything that could place an order.
        var coreTypes = typeof(Engine).Assembly.GetTypes();
        var suspicious = coreTypes
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                                        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                                        | System.Reflection.BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .Where(n => n.Contains("Order", StringComparison.OrdinalIgnoreCase) &&
                       (n.Contains("Place", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Submit", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Cancel", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("Modify", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Harness.Check("read-only: Core exposes no order placement surface", suspicious.Count == 0,
            "found: " + string.Join(", ", suspicious));

        Harness.Check("read-only: automatic trading is refused by configuration",
            new Config { Alerts = new AlertsConfig { AutomaticTradingEnabled = true } }.Validate().Count > 0);

        Harness.Check("read-only: audible alerts are off by default",
            !new Config().Alerts.AudibleEnabled);
    }

    private static void PlotScaleDoesNotChangeSignals()
    {
        // Changing zoom or plot scale must never change a signal. Two engines fed identical
        // events, differing only in display settings, must transition identically.
        var a = new Scenario();
        a.DeclareResistance(400);
        a.ApproachResistance();
        var ra = a.Tick();

        var displayChanged = Scenario.ReadyConfig();
        displayChanged = displayChanged with
        {
            Display = displayChanged.Display with { PlotClip = 8, TrailMs = 5000, MaximumRequestedFramesPerSecond = 2 }
        };
        var b = new Scenario(displayChanged);
        b.DeclareResistance(400);
        b.ApproachResistance();
        var rb = b.Tick();

        Harness.Check("display: a different plot scale produces the same transitions",
            ra.Transitions.Select(t => t.NewState).SequenceEqual(rb.Transitions.Select(t => t.NewState)),
            "display settings changed a signal");

        var ca = ra.View.AllSetups.First(x => x.LevelId == "R1").Candidate!;
        var cb = rb.View.AllSetups.First(x => x.LevelId == "R1").Candidate!;
        Harness.Equal("display: boundaries are identical", ca.Boundaries.ConfirmationHalfTicks, cb.Boundaries.ConfirmationHalfTicks);
    }
}
