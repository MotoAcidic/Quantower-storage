using AuctionResponse.Core;
using AuctionResponse.Replay;
using AuctionResponse.Research;

namespace AuctionResponse.Tests;

/// <summary>
/// The offline research surfaces: baseline construction and its gates, the artifact file
/// format and its tamper check, ridge regression, and the economic identity.
///
/// Everything here is OFF in the shipped baseline. It is tested anyway, because the moment
/// someone turns it on is exactly the moment a silent arithmetic error becomes expensive.
/// </summary>
public static class ResearchTests
{
    public static void Run()
    {
        Harness.Section("Research - baseline artifact");
        BaselineGates();
        ArtifactRoundTrip();
        ArtifactTamperIsRefused();
        ArtifactCompatibility();

        CrossExpiryIsExplicit();
        LiveBaselineIsCausalAndHonest();
        TimeframeParsing();

        Harness.Section("Research - regression and economics");
        RidgeRecoversKnownCoefficients();
        RidgeShrinksTowardZero();
        RidgeLambdaSelectionIsChronological();
        ActivationGates();
        EconomicIdentity();
        ModelManifestContract();
    }

    private static IReadOnlyList<BaselineSample> SyntheticSamples(int sessions, int perBucket)
    {
        var rng = new Random(20260912);
        var list = new List<BaselineSample>();
        for (var s = 0; s < sessions; s++)
            for (var minute = 0; minute < 60; minute += 30)
                for (var i = 0; i < perBucket; i++)
                    list.Add(new BaselineSample(
                        "2026-08-" + (s + 1).ToString("00"),
                        minute, 5000,
                        BuyVolume: rng.Next(10, 200),
                        SellVolume: rng.Next(10, 200),
                        AbsoluteDelta: rng.Next(0, 120),
                        AbsoluteResponse: rng.Next(0, 8)));
        return list;
    }

    private static void BaselineGates()
    {
        var config = Scenario.ReadyConfig();
        var builder = new BaselineBuilder(config);
        var instrument = new InstrumentKey("MNQ", "CME", "202612");

        // Too few sessions AND too few samples: both gates must be reported, not one.
        var thin = builder.Build(SyntheticSamples(sessions: 3, perBucket: 10), instrument, 0.25m,
            "Test", "cal-1", DateTime.UtcNow, new[] { "hash" });

        Harness.Check("baseline gates: too few sessions is reported",
            thin.Warnings.Any(w => w.Contains("eligible sessions")), string.Join(" | ", thin.Warnings));
        Harness.Check("baseline gates: thin buckets are reported",
            thin.Warnings.Any(w => w.Contains("below the minimum")), string.Join(" | ", thin.Warnings));
        Harness.Check("baseline gates: the artifact is not marked as meeting them", !thin.MeetsGates);

        // A thin artifact is still emitted with its REAL counts, so the resolver shows
        // Warmup rather than the builder quietly padding it.
        var bucket = thin.Artifact.Find(0, 5000);
        Harness.Check("baseline gates: real sample counts are preserved",
            bucket is { SampleCount: 30 }, "count was " + (bucket?.SampleCount.ToString() ?? "missing"));

        var resolver = new BaselineResolver(config);
        resolver.Load(thin.Artifact, instrument, 0.25m, "Test");
        var lookup = resolver.AttackerVolumeQuantile(0, 5000, 1);
        Harness.Equal("baseline gates: a thin bucket resolves to Warmup", BaselineStatus.Warmup, lookup.Status);
        Harness.Null("baseline gates: and yields no quantile", lookup.Value);

        // A properly sized build passes.
        var full = builder.Build(SyntheticSamples(sessions: 12, perBucket: 300), instrument, 0.25m,
            "Test", "cal-1", DateTime.UtcNow, new[] { "hash" });
        Harness.Check("baseline gates: a sufficient build has no warnings", full.MeetsGates,
            string.Join(" | ", full.Warnings));
        Harness.Equal("baseline gates: lookback caps the sessions used",
            Math.Min(12, config.Baseline.LookbackSessions), full.SessionsUsed);

        resolver.Load(full.Artifact, instrument, 0.25m, "Test");
        Harness.Equal("baseline gates: a full bucket is Ready",
            BaselineStatus.Ready, resolver.AttackerVolumeQuantile(0, 5000, 1).Status);

        // Nearest-rank, against the artifact's own retained sample.
        var q = resolver.AttackerVolumeQuantile(0, 5000, 1);
        var sample = full.Artifact.Find(0, 5000)!.SortedBuyVolumes;
        Harness.Equal("baseline gates: quantile matches nearest rank on the retained sample",
            Statistics.Quantile(sample, 0.9) ?? -1, q.Value, 1e-9);
    }

    private static void ArtifactRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "auction-response-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "baseline.json");
            var original = Scenario.SyntheticBaseline(0.25m) with { Sha256 = "" };

            BaselineArtifactIo.Save(original, path);
            var loaded = BaselineArtifactIo.Load(path);

            Harness.Equal("artifact: instrument survives", original.Instrument.ToString(), loaded.Instrument.ToString());
            Harness.Equal("artifact: tick size survives exactly", original.TickSize, loaded.TickSize);
            Harness.Equal("artifact: bucket count survives", original.Buckets.Count, loaded.Buckets.Count);
            Harness.Check("artifact: a hash is stamped on save", loaded.Sha256.Length == 64,
                "hash was: " + loaded.Sha256);

            var b = loaded.Find(90, 5000);
            Harness.Check("artifact: a bucket round-trips", b is not null);
            Harness.Equal("artifact: the quantile round-trips", 150d, b!.BuyVolumeQuantiles[0.9], 1e-9);
            Harness.Equal("artifact: the plot scale round-trips", 80d, b.AbsoluteDeltaQ75, 1e-9);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void ArtifactTamperIsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "auction-response-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "baseline.json");
            BaselineArtifactIo.Save(Scenario.SyntheticBaseline(0.25m) with { Sha256 = "" }, path);

            // Hand-edit the single number that decides whether anything can arm.
            var text = File.ReadAllText(path).Replace("150", "5");
            File.WriteAllText(path, text);

            Harness.Throws<InvalidDataException>("artifact: an edited baseline is refused, not trusted",
                () => BaselineArtifactIo.Load(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void ArtifactCompatibility()
    {
        var config = Scenario.ReadyConfig();
        var artifact = Scenario.SyntheticBaseline(0.25m);
        var instrument = new InstrumentKey("MNQ", "CME", "202612");

        Harness.Check("compatibility: a matching artifact is accepted",
            artifact.IncompatibleReason(config, instrument, 0.25m, "Test") is null);

        // An expired contract must never merge into a current one.
        var otherExpiry = new InstrumentKey("MNQ", "CME", "202609");
        Harness.Check("compatibility: a different expiry is refused",
            artifact.IncompatibleReason(config, otherExpiry, 0.25m, "Test") is not null);

        Harness.Check("compatibility: a different tick size is refused",
            artifact.IncompatibleReason(config, instrument, 0.5m, "Test") is not null);
        Harness.Check("compatibility: a different feed mode is refused",
            artifact.IncompatibleReason(config, instrument, 0.25m, "Live") is not null);

        var staleFormula = artifact with { FeatureFormulaVersion = "0.9.0" };
        Harness.Check("compatibility: a stale feature formula version is refused",
            staleFormula.IncompatibleReason(config, instrument, 0.25m, "Test") is not null);

        // And an incompatible artifact means UNAVAILABLE, not a fallback to something else.
        var resolver = new BaselineResolver(config);
        resolver.Load(staleFormula, instrument, 0.25m, "Test");
        var lookup = resolver.AttackerVolumeQuantile(90, 5000, 1);
        Harness.Equal("compatibility: resolves to Incompatible", BaselineStatus.Incompatible, lookup.Status);
        Harness.Null("compatibility: and yields no value", lookup.Value);
    }

    /// <summary>
    /// The self-observed baseline is the thing that removes the setup burden, so its honesty
    /// matters more than most: it must never score a session using that session's own data,
    /// and it must refuse to serve a quantile until real prior sessions exist.
    /// </summary>
    private static void LiveBaselineIsCausalAndHonest()
    {
        var config = Scenario.ReadyConfig();
        var instrument = new InstrumentKey("MNQ", "CME", "202612");
        var live = new LiveBaseline();

        // One session of data, and it is the session being scored.
        for (var i = 0; i < 400; i++)
            live.Record("2026-09-01", 90, 5000, 100 + i, 50, 50 + i, 1);

        Harness.Check("live baseline: a session cannot score itself",
            live.BuildExcluding("2026-09-01", config, instrument, 0.25m, "Test", "cal") is null,
            "the only session present was the one being scored, so there is nothing to score against");

        Harness.Check("live baseline: the same data DOES serve a different session",
            live.BuildExcluding("2026-09-02", config, instrument, 0.25m, "Test", "cal") is not null);

        Harness.Equal("live baseline: prior session count excludes today", 0, live.PriorSessionCount("2026-09-01"));
        Harness.Equal("live baseline: and counts it for another day", 1, live.PriorSessionCount("2026-09-02"));

        // Ten sessions of 400 samples clears both Section 8 gates.
        for (var day = 2; day <= 11; day++)
            for (var i = 0; i < 400; i++)
                live.Record("2026-09-" + day.ToString("00"), 90, 5000, 100 + i, 50, 50 + i, 1);

        var artifact = live.BuildExcluding("2026-09-20", config, instrument, 0.25m, "Test", "cal");
        Harness.Check("live baseline: builds an artifact once sessions exist", artifact is not null);

        var resolver = new BaselineResolver(config);
        resolver.Load(artifact, instrument, 0.25m, "Test");
        var lookup = resolver.AttackerVolumeQuantile(90, 5000, 1);
        Harness.Equal("live baseline: resolves Ready once the gates are met", BaselineStatus.Ready, lookup.Status);
        Harness.Check("live baseline: and yields a real quantile", lookup.Value is > 0);

        // Thin evidence still refuses, rather than serving a quantile off a handful of samples.
        var thin = new LiveBaseline();
        for (var day = 1; day <= 12; day++)
            for (var i = 0; i < 5; i++)
                thin.Record("2026-09-" + day.ToString("00"), 90, 5000, 100, 50, 50, 1);

        var thinResolver = new BaselineResolver(config);
        thinResolver.Load(thin.BuildExcluding("2026-09-20", config, instrument, 0.25m, "Test", "cal"),
            instrument, 0.25m, "Test");
        Harness.Equal("live baseline: thin buckets stay in Warmup",
            BaselineStatus.Warmup, thinResolver.AttackerVolumeQuantile(90, 5000, 1).Status);

        // Progress is reported in sessions, which is the thing a person can act on.
        Harness.Check("live baseline: progress is stated in sessions",
            live.ProgressLabel("2026-09-20", config).Contains("of " + config.Baseline.MinimumSessions),
            live.ProgressLabel("2026-09-20", config));

        // Persistence round trip: what it learned must survive a restart.
        var restored = new LiveBaseline();
        restored.Import(live.Export());
        Harness.Equal("live baseline: sample count survives a round trip", live.TotalSamples, restored.TotalSamples);
        Harness.Equal("live baseline: session count survives a round trip", live.SessionCount, restored.SessionCount);

        var a = live.BuildExcluding("2026-09-20", config, instrument, 0.25m, "Test", "cal")!;
        var b = restored.BuildExcluding("2026-09-20", config, instrument, 0.25m, "Test", "cal")!;
        Harness.Equal("live baseline: the quantile is identical after a round trip",
            a.Find(90, 5000)!.BuyVolumeQuantiles[0.9], b.Find(90, 5000)!.BuyVolumeQuantiles[0.9], 1e-9);
    }

    /// <summary>
    /// A baseline from a different dated contract is refused by default and permitted only
    /// by an explicit switch. Symbol and exchange are never relaxed.
    /// </summary>
    private static void CrossExpiryIsExplicit()
    {
        var strict = Scenario.ReadyConfig();
        var relaxed = strict with { Baseline = strict.Baseline with { AllowCrossExpiry = true } };

        var artifact = Scenario.SyntheticBaseline(0.25m);          // built on 202612
        var rolled = new InstrumentKey("MNQ", "CME", "202703");    // chart has rolled

        Harness.Check("cross-expiry: refused by default",
            artifact.IncompatibleReason(strict, rolled, 0.25m, "Test") is not null);
        Harness.Check("cross-expiry: the refusal names the switch",
            (artifact.IncompatibleReason(strict, rolled, 0.25m, "Test") ?? "").Contains("cross-expiry"),
            artifact.IncompatibleReason(strict, rolled, 0.25m, "Test") ?? "");
        Harness.Check("cross-expiry: permitted when explicitly enabled",
            artifact.IncompatibleReason(relaxed, rolled, 0.25m, "Test") is null);

        // The relaxation is for the SAME instrument only.
        Harness.Check("cross-expiry: a different symbol is still refused",
            artifact.IncompatibleReason(relaxed, new InstrumentKey("MES", "CME", "202612"), 0.25m, "Test") is not null);
        Harness.Check("cross-expiry: a different exchange is still refused",
            artifact.IncompatibleReason(relaxed, new InstrumentKey("MNQ", "EUREX", "202612"), 0.25m, "Test") is not null);
        Harness.Check("cross-expiry: a different tick size is still refused",
            artifact.IncompatibleReason(relaxed, rolled, 0.5m, "Test") is not null);

        // And using one is visible, not silent.
        var resolver = new BaselineResolver(relaxed);
        resolver.Load(artifact, rolled, 0.25m, "Test");
        Harness.Check("cross-expiry: the resolver flags that it is in use", resolver.UsingCrossExpiryBaseline);
        Harness.Check("cross-expiry: and names both contracts",
            (resolver.CrossExpiryNote ?? "").Contains("202612") && (resolver.CrossExpiryNote ?? "").Contains("202703"),
            resolver.CrossExpiryNote ?? "");

        var same = new BaselineResolver(strict);
        same.Load(artifact, new InstrumentKey("MNQ", "CME", "202612"), 0.25m, "Test");
        Harness.Check("cross-expiry: not flagged when the contracts match", !same.UsingCrossExpiryBaseline);
    }

    /// <summary>
    /// The builder refuses any timeframe that is not exactly 5 seconds, so this parser has to
    /// be right: a misread timeframe silently changes what every baseline sample means.
    /// </summary>
    private static void TimeframeParsing()
    {
        Harness.Equal("timeframe: \"5 Seconds\"", 5, Timeframe.ParseSeconds("5 Seconds") ?? -1);
        Harness.Equal("timeframe: \"5s\"", 5, Timeframe.ParseSeconds("5s") ?? -1);
        Harness.Equal("timeframe: \"S5\"", 5, Timeframe.ParseSeconds("S5") ?? -1);
        Harness.Equal("timeframe: \"1 Minute\"", 60, Timeframe.ParseSeconds("1 Minute") ?? -1);
        Harness.Equal("timeframe: \"15Min\"", 900, Timeframe.ParseSeconds("15Min") ?? -1);
        Harness.Equal("timeframe: \"M1\"", 60, Timeframe.ParseSeconds("M1") ?? -1);
        Harness.Equal("timeframe: \"4 Hours\"", 14400, Timeframe.ParseSeconds("4 Hours") ?? -1);

        // Non-time charts must NOT be mistaken for a timeframe.
        Harness.Null("timeframe: tick charts are not time-based", Timeframe.ParseSeconds("100 Ticks"));
        Harness.Null("timeframe: volume charts are not time-based", Timeframe.ParseSeconds("1000 Volume"));
        Harness.Null("timeframe: range charts are not time-based", Timeframe.ParseSeconds("4 Range"));
        Harness.Null("timeframe: \"Ticks100\" is not S/M/H", Timeframe.ParseSeconds("Ticks100"));
        Harness.Null("timeframe: empty", Timeframe.ParseSeconds(""));
        Harness.Null("timeframe: null", Timeframe.ParseSeconds(null));
        Harness.Null("timeframe: zero is not a timeframe", Timeframe.ParseSeconds("0s"));

        // The exact acceptance the builder gates on.
        Harness.Check("timeframe: only 5 seconds passes the builder gate",
            Timeframe.ParseSeconds("5 Seconds") == 5 && Timeframe.ParseSeconds("10 Seconds") != 5
            && Timeframe.ParseSeconds("1 Minute") != 5);
    }

    private static void RidgeRecoversKnownCoefficients()
    {
        // y = 2 + 3*x1 - 1.5*x2 + 0.5*x3, noiseless. With a negligible penalty the fit must
        // return the generating coefficients.
        var rng = new Random(7);
        var n = 400;
        var design = new double[n][];
        var y = new double[n];

        for (var i = 0; i < n; i++)
        {
            var x1 = rng.NextDouble() * 4 - 2;
            var x2 = rng.NextDouble() * 4 - 2;
            var x3 = rng.NextDouble() * 4 - 2;
            design[i] = new[] { 1d, x1, x2, x3 };
            y[i] = 2d + 3d * x1 - 1.5d * x2 + 0.5d * x3;
        }

        var beta = RidgeRegression.Fit(design, y, 1e-10);
        Harness.Equal("ridge: intercept recovered", 2d, beta[0], 1e-6);
        Harness.Equal("ridge: b1 recovered", 3d, beta[1], 1e-6);
        Harness.Equal("ridge: b2 recovered", -1.5d, beta[2], 1e-6);
        Harness.Equal("ridge: b3 recovered", 0.5d, beta[3], 1e-6);
    }

    private static void RidgeShrinksTowardZero()
    {
        var rng = new Random(11);
        var n = 300;
        var design = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var x1 = rng.NextDouble() * 2 - 1;
            design[i] = new[] { 1d, x1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1 };
            y[i] = 5d + 4d * x1;
        }

        var light = RidgeRegression.Fit(design, y, 1e-8);
        var heavy = RidgeRegression.Fit(design, y, 10d);

        Harness.Check("ridge: a heavier penalty shrinks the slope",
            Math.Abs(heavy[1]) < Math.Abs(light[1]),
            "light " + light[1].ToString("0.###") + " vs heavy " + heavy[1].ToString("0.###"));

        // The intercept is NOT penalised, so it must not be dragged toward zero with them.
        Harness.Check("ridge: the intercept is not penalised",
            Math.Abs(heavy[0] - 5d) < 0.5d,
            "intercept under a heavy penalty was " + heavy[0].ToString("0.###"));
    }

    private static void RidgeLambdaSelectionIsChronological()
    {
        var rng = new Random(23);
        var n = 400;
        var design = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var x1 = rng.NextDouble() * 2 - 1;
            design[i] = new[] { 1d, x1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1 };
            y[i] = 1d + 2d * x1 + (rng.NextDouble() - 0.5) * 0.1;
        }

        var lambda = RidgeRegression.SelectLambda(design, y, new[] { 0.01, 0.1, 1d, 10d });
        Harness.Check("ridge: lambda comes from the offered set",
            new[] { 0.01, 0.1, 1d, 10d }.Contains(lambda), "selected " + lambda);
        Harness.Check("ridge: a nearly noiseless signal selects light regularisation",
            lambda <= 0.1, "selected " + lambda);
    }

    private static void ActivationGates()
    {
        var config = Scenario.ReadyConfig();

        var blocked = RidgeRegression.ActivationBlockers(
            trainingWindows: 100, validationWindows: 10, validationMae: 5d, frozenMeanBaselineMae: 1d, config);
        Harness.Equal("activation: every unmet gate is reported", 3, blocked.Count);

        var stillBlocked = RidgeRegression.ActivationBlockers(
            trainingWindows: 5000, validationWindows: 1000, validationMae: 1.0d, frozenMeanBaselineMae: 1.0d, config);
        Harness.Equal("activation: merely MATCHING the baseline is not beating it", 1, stillBlocked.Count);

        var clear = RidgeRegression.ActivationBlockers(
            trainingWindows: 5000, validationWindows: 1000, validationMae: 0.8d, frozenMeanBaselineMae: 1.0d, config);
        Harness.Equal("activation: all gates passed", 0, clear.Count);
    }

    private static void EconomicIdentity()
    {
        // 2 MNQ long, $2/point, 10 points, $1.40 round-turn fees, slippage in the fills.
        var pnl = TradeEconomics.NetPnL(1, 2, 2m, 24000m, 24010m, 1.40m, 0m, slippageAlreadyInFills: true);
        Harness.Equal("economics: long PnL", 38.60m, pnl);

        var shortPnl = TradeEconomics.NetPnL(-1, 2, 2m, 24010m, 24000m, 1.40m, 0m, slippageAlreadyInFills: true);
        Harness.Equal("economics: short PnL mirrors", 38.60m, shortPnl);

        var losing = TradeEconomics.NetPnL(1, 1, 2m, 24000m, 23995m, 0.70m, 0m, slippageAlreadyInFills: true);
        Harness.Equal("economics: a loss stays a loss after fees", -10.70m, losing);

        // Double-counting slippage is refused rather than quietly halving the reported edge.
        Harness.Throws<ArgumentException>("economics: slippage cannot be counted twice",
            () => TradeEconomics.NetPnL(1, 1, 2m, 24000m, 24010m, 0.70m, 5m, slippageAlreadyInFills: true));

        Harness.Throws<ArgumentOutOfRangeException>("economics: direction must be +1 or -1",
            () => TradeEconomics.NetPnL(0, 1, 2m, 24000m, 24010m, 0m, 0m, false));
    }

    private static void ModelManifestContract()
    {
        var instrument = new InstrumentKey("MNQ", "CME", "202612");
        var manifest = new ModelManifest
        {
            ModelKind = "ridge",
            ModelVersion = "0.1.0",
            FeatureNames = new[] { "zF", "zs", "zSigma" },
            Coefficients = new[] { 0.1, 0.2, 0.3 },
            Instrument = instrument,
            ClockMode = "ReceiveElapsedWithRecordedDecisionTicks",
            FeedMode = "Test",
            HorizonMs = 30000
        };

        Harness.Check("manifest: a matching contract is accepted",
            manifest.IncompatibleReason(instrument, "ReceiveElapsedWithRecordedDecisionTicks", "Test", 30000) is null);
        Harness.Check("manifest: a different instrument is refused",
            manifest.IncompatibleReason(new InstrumentKey("MES", "CME", "202612"), "ReceiveElapsedWithRecordedDecisionTicks", "Test", 30000) is not null);
        Harness.Check("manifest: a different horizon is refused",
            manifest.IncompatibleReason(instrument, "ReceiveElapsedWithRecordedDecisionTicks", "Test", 60000) is not null);
        Harness.Check("manifest: a coefficient/feature mismatch is refused",
            (manifest with { Coefficients = new[] { 0.1, 0.2 } })
                .IncompatibleReason(instrument, "ReceiveElapsedWithRecordedDecisionTicks", "Test", 30000) is not null);

        // Softmax and Brier, since the probability module would depend on both.
        var p = Multinomial.Softmax(new[] { 1d, 2d, 3d });
        Harness.Equal("multinomial: probabilities sum to one", 1d, p.Sum(), 1e-12);
        Harness.Check("multinomial: ordering is preserved", p[2] > p[1] && p[1] > p[0]);

        var huge = Multinomial.Softmax(new[] { 1000d, 1001d, 1002d });
        Harness.Check("multinomial: stable against overflow", huge.All(double.IsFinite) && Math.Abs(huge.Sum() - 1d) < 1e-12);

        // A perfect prediction scores 0; the worst possible three-class score is 2.
        Harness.Equal("multinomial: Brier of a perfect prediction", 0d,
            Multinomial.BrierScore(new[] { new[] { 1d, 0d, 0d } }, new[] { 0 }), 1e-12);
        Harness.Equal("multinomial: Brier of a confidently wrong prediction", 2d,
            Multinomial.BrierScore(new[] { new[] { 1d, 0d, 0d } }, new[] { 1 }), 1e-12);
    }
}
