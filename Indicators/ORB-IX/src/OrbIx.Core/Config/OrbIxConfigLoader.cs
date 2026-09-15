using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using OrbIx.Core.Features;

using OrbIx.Core.Playbooks;

using OrbIx.Core.Sessions;

namespace OrbIx.Core.Config;

/// <summary>
/// Raised when a configuration document cannot be turned into a usable
/// <see cref="OrbIxConfig"/>. Carries every problem found, not just the first, because a
/// loader that stops at the first fault turns one bad edit into several edit-run cycles.
/// </summary>
public sealed class OrbIxConfigException : Exception
{
    public OrbIxConfigException(IReadOnlyList<string> problems)
        : base("Configuration is not usable:" + Environment.NewLine
               + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)))
    {
        this.Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// Reads the §12 configuration document into <see cref="OrbIxConfig"/>.
///
/// Two deliberate properties. Comments survive: the document is the readable contract the
/// specification intended, so the reader skips comments rather than rejecting them.
/// And nothing silently defaults to a trading-relevant value — a missing threshold is an
/// error, not an invitation to invent one. Genuinely optional keys are the ones whose
/// absence has a single unambiguous meaning, and each is documented on the model.
/// </summary>
public static partial class OrbIxConfigLoader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static OrbIxConfig LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Configuration path must be supplied.", nameof(path));

        if (!File.Exists(path))
            throw new OrbIxConfigException(new[] { $"Configuration file not found: {path}" });

        return Load(File.ReadAllText(path));
    }

    /// <summary>
    /// Reads configuration from a stream — the embedded default, or any other source that is
    /// not a file on disk.
    ///
    /// Delegates to <see cref="Load(string)"/> so there is exactly one validator. A second
    /// parser for the embedded copy would be a second set of rules, and the two would drift.
    /// </summary>
    /// <param name="stream">The document. Read to the end; not disposed by this method.</param>
    /// <param name="originLabel">
    /// How to name this source in any problem report, since there is no path to quote.
    /// </param>
    public static OrbIxConfig LoadStream(Stream stream, string originLabel)
    {
        if (stream is null)
            throw new ArgumentNullException(nameof(stream));

        if (string.IsNullOrWhiteSpace(originLabel))
            throw new ArgumentException("An origin label is required so faults can be attributed.", nameof(originLabel));

        string json;

        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            json = reader.ReadToEnd();
        }

        if (json.Length == 0)
            throw new OrbIxConfigException(new[] { $"Configuration from {originLabel} is empty." });

        try
        {
            return Load(json);
        }
        catch (OrbIxConfigException ex)
        {
            // Re-attributed, not swallowed: every problem is preserved and gains the origin,
            // because "mode: unknown value" with no source named is unactionable when three
            // configuration sources are possible.
            var attributed = new List<string> { $"Configuration from {originLabel} is not usable:" };
            attributed.AddRange(ex.Problems);
            throw new OrbIxConfigException(attributed);
        }
    }

    public static OrbIxConfig Load(string json)
    {
        if (json is null)
            throw new ArgumentNullException(nameof(json));

        using var document = ParseOrThrow(json);
        var root = document.RootElement;
        var problems = new List<string>();
        var r = new Reader(root, problems, path: string.Empty);

        var config = new OrbIxConfig
        {
            Mode = r.Enum<EngineMode>("mode"),
            L3Source = r.Enum<L3Source>("l3Source"),
            LadderSplit = r.Enum<LadderSplitMode>("ladderSplit"),
            TrailMode = r.Enum<TrailMode>("trailMode"),
            ExchangeTimeZone = r.String("exchangeTimeZone"),
            SessionTimeZone = r.String("sessionTimeZone"),
            TradingWeek = ReadTradingWeek(r.Object("tradingWeek"), problems),
            Symbols = ReadSymbols(r.Object("symbols"), problems),
            Sessions = ReadSessions(r.Object("sessions"), problems),
            Or = ReadOr(r.Object("or"), problems),
            Scoring = ReadScoring(r.Object("scoring"), problems),
            Playbooks = ReadPlaybooks(r.Object("playbooks"), problems),
            Risk = ReadRisk(r.Object("risk"), problems),
            Accounts = ReadAccounts(r.Array("accounts"), problems),
            Targets = ReadTargets(r.Object("targets"), problems),
            Trail = ReadTrail(r.Object("trail"), problems),
            Data = ReadData(r.Object("data"), problems),
            Kill = ReadKill(r.Object("kill"), problems),
            Stops = ReadStops(r.Object("stops"), problems),
            Retest = ReadRetest(r.Object("retest"), problems),
            Levels = ReadLevels(r.Object("levels"), problems),
            MicroQuality = ReadMicroQuality(r.Object("microQuality"), problems),
            Imbalance = ReadImbalance(r.Object("imbalance"), problems),
            Absorption = ReadAbsorption(r.Object("absorption"), problems),
            Validation = ReadValidation(r.Object("validation"), problems),
            Recorder = ReadRecorder(r.Object("recorder"), problems),
            Flow = ReadFlow(r.Object("flow")),
        };

        Validate(config, problems);

        if (problems.Count > 0)
            throw new OrbIxConfigException(problems);

        return config;
    }

    private static JsonDocument ParseOrThrow(string json)
    {
        try
        {
            return JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new OrbIxConfigException(new[] { "Document is not valid JSON: " + ex.Message });
        }
    }

    // ---- section readers -------------------------------------------------------------

    private static IReadOnlyDictionary<string, SymbolConfig> ReadSymbols(
        Reader symbols, List<string> problems)
    {
        var result = new Dictionary<string, SymbolConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in symbols.Properties())
        {
            var s = new Reader(element, problems, symbols.Path + "." + name);
            result[name] = new SymbolConfig
            {
                Profile = s.String("profile"),
                Tiers = s.StringArray("tiers"),
                MaxRiskTicks = s.Int("maxRiskTicks"),
                EntryTf = s.String("entryTf"),
                MaxMinis = s.Int("maxMinis"),
                MinTouchDepth = s.Int("minTouchDepth"),
                RoundNumberStep = s.Double("roundNumberStep"),
                DomWeightScale = s.OptionalDouble("domWeightScale") ?? 1.0,
            };
        }

        return result;
    }

    private static SessionsConfig ReadSessions(Reader sessions, List<string> problems)
    {
        var named = new Dictionary<string, SessionConfig>(StringComparer.OrdinalIgnoreCase);
        var custom = new List<CustomSessionConfig>();

        foreach (var (name, element) in sessions.Properties())
        {
            var path = sessions.Path + "." + name;

            if (string.Equals(name, "custom", StringComparison.OrdinalIgnoreCase))
            {
                if (element.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{path}: expected an array of custom session objects.");
                    continue;
                }

                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var c = new Reader(item, problems, $"{path}[{index}]");
                    custom.Add(new CustomSessionConfig
                    {
                        Name = c.String("name"),
                        Open = c.String("open"),
                        Or = c.String("or"),
                        Enabled = c.Bool("enabled"),
                        Budget = c.OptionalInt("budget"),
                        EntriesAllowed = c.OptionalBool("entriesAllowed") ?? true,
                        Symbols = c.OptionalStringArray("symbols") ?? Array.Empty<string>(),
                    });
                    index++;
                }

                continue;
            }

            var s = new Reader(element, problems, path);
            named[name] = new SessionConfig
            {
                Open = s.String("open"),
                Or = s.String("or"),
                Enabled = s.Bool("enabled"),
                Budget = s.OptionalInt("budget"),
                EntriesAllowed = s.OptionalBool("entriesAllowed") ?? true,
                Symbols = s.OptionalStringArray("symbols") ?? Array.Empty<string>(),
            };
        }

        return new SessionsConfig { Named = named, Custom = custom };
    }

    private static OpeningRangeConfig ReadOr(Reader or, List<string> problems)
    {
        var adaptive = or.Object("adaptive");
        var grades = or.Object("grades");

        return new OpeningRangeConfig
        {
            Adaptive = new AdaptiveOrConfig
            {
                Enabled = adaptive.Bool("enabled"),
                Kappa = adaptive.Double("kappa"),
                MinSec = adaptive.Int("minSec"),
                MaxMin = adaptive.Int("maxMin"),
                MaxWidthAdrPct = adaptive.Double("maxWidthAdrPct"),
                OpenVolLookbackDays = adaptive.Int("openVolLookbackDays"),
            },
            Grades = new OrGradeConfig
            {
                CompressedMaxOrw = grades.Double("compressedMaxOrw"),
                ExhaustedMinOrw = grades.Double("exhaustedMinOrw"),
            },
            AdrPeriod = or.Int("adrPeriod"),
            ExtensionMultiples = or.DoubleArray("extensionMultiples"),
            Lengths = or.StringArray("lengths"),
        };
    }

    private static ScoringConfig ReadScoring(Reader scoring, List<string> problems)
    {
        var thresholds = scoring.Object("gradeThresholds");
        var weights = scoring.Object("weights");

        return new ScoringConfig
        {
            GradeThresholds = new GradeThresholds
            {
                APlus = thresholds.Double("aPlus"),
                A = thresholds.Double("a"),
                B = thresholds.Double("b"),
            },
            Weights = new ScoreWeightsConfig
            {
                Structure = weights.Double("structure"),
                OrderFlow = weights.Double("orderFlow"),
                Book = weights.Double("book"),
                Micro = weights.Double("micro"),
                Positioning = weights.Double("positioning"),
            },
            RedistributeOnMissingTier = scoring.Bool("redistributeOnMissingTier"),
        };
    }

    private static IReadOnlyDictionary<string, PlaybookConfig> ReadPlaybooks(
        Reader playbooks, List<string> problems)
    {
        var result = new Dictionary<string, PlaybookConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, element) in playbooks.Properties())
        {
            var p = new Reader(element, problems, playbooks.Path + "." + name);
            result[name] = new PlaybookConfig
            {
                On = p.Bool("on"),
                SizeMult = p.Double("sizeMult"),
                BreakBufferTicks = p.OptionalInt("breakBufferTicks"),
                MaxRetestDepthPct = p.OptionalDouble("maxRetestDepthPct"),
                Provenance = ReadProvenance(p.OptionalObject("provenance"), problems),
            };
        }

        return result;
    }

    /// <summary>
    /// A playbook's measured record, or null when it has none.
    ///
    /// NULL IS A MEANINGFUL ANSWER HERE and is returned rather than an empty record: the
    /// difference between "measured, and here is the interval" and "never measured" is exactly
    /// what the label on a drawn signal has to convey, and an empty record would collapse it.
    ///
    /// A product whose numbers are incoherent — no trades, or an interval whose low exceeds its
    /// high — is REFUSED with a problem raised rather than silently dropped, because a label
    /// quietly reverting to "no measured record" would look identical to never having
    /// configured one.
    /// </summary>
    private static PlaybookProvenance? ReadProvenance(Reader? provenance, List<string> problems)
    {
        if (provenance is not { } reader)
            return null;

        var byProduct = new Dictionary<string, MeasuredRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var (product, element) in reader.Object("products").Properties())
        {
            var r = new Reader(element, problems, reader.Path + ".products." + product);
            var record = new MeasuredRecord(
                r.Int("trades"), r.Double("meanR"), r.Double("ciLowR"), r.Double("ciHighR"));

            if (!record.IsUsable)
            {
                problems.Add(
                    $"{reader.Path}.products.{product}: trades must be positive and the interval "
                    + "must be real with its low at or below its high.");
                continue;
            }

            byProduct[product] = record;
        }

        return new PlaybookProvenance
        {
            Ledger = reader.Enum<LedgerStatus>("ledger"),
            Source = reader.String("source"),
            ByProduct = byProduct,
        };
    }

    private static RiskConfig ReadRisk(Reader risk, List<string> problems) => new()
    {
        RiskPct = risk.Double("riskPct"),
        LossesSurvivableDaily = risk.Int("lossesSurvivableDaily"),
        LossesSurvivableDd = risk.Int("lossesSurvivableDd"),
        SessionRiskCapPct = risk.Double("sessionRiskCapPct"),
        CooldownMinAfterTwoLosses = risk.Int("cooldownMinAfterTwoLosses"),
        CorrelationNetting = risk.Object("correlationNetting").DoubleMap(),
    };

    private static IReadOnlyList<AccountConfig> ReadAccounts(
        IReadOnlyList<Reader> accounts, List<string> problems)
    {
        var result = new List<AccountConfig>();
        foreach (var a in accounts)
        {
            var trailing = a.OptionalObject("trailingLimit");
            result.Add(new AccountConfig
            {
                Id = a.String("id"),
                Policy = a.String("policy"),
                Equity = a.OptionalDouble("equity"),
                DailyLimit = a.OptionalDouble("dailyLimit"),
                TrailingLimit = trailing is null
                    ? null
                    : new TrailingLimitConfig
                    {
                        Type = trailing.Value.String("type"),
                        Amount = trailing.Value.Double("amount"),
                    },
                ConsistencyTarget = a.OptionalDouble("consistencyTarget"),
                FlatByTime = a.OptionalString("flatByTime"),
                NewsRule = a.Enum<NewsRule>("newsRule"),
                RiskPctOverride = a.OptionalDouble("riskPctOverride"),
            });
        }

        return result;
    }

    private static TargetsConfig ReadTargets(Reader targets, List<string> problems) => new()
    {
        Tp1 = targets.String("tp1"),
        Tp2 = targets.String("tp2"),
        Tp3 = targets.String("tp3"),
        Tp4 = targets.String("tp4"),
        SnapAheadTicks = targets.Int("snapAheadTicks"),
        Allocation = targets.DoubleArray("allocation"),
        Tp1RMin = targets.Double("tp1RMin"),
        Tp1RMax = targets.Double("tp1RMax"),
        Tp3SnapOrFraction = targets.Double("tp3SnapOrFraction"),
        SmallSizeLadder = ReadSmallSizeLadder(targets.Object("smallSizeLadder"), problems),
    };

    private static IReadOnlyDictionary<int, IReadOnlyList<int>> ReadSmallSizeLadder(
        Reader ladder, List<string> problems)
    {
        var result = new Dictionary<int, IReadOnlyList<int>>();

        foreach (var (key, element) in ladder.Properties())
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size <= 0)
            {
                problems.Add($"{ladder.Path}.{key}: keys are position sizes and must be positive whole numbers.");
                continue;
            }

            if (element.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                problems.Add($"{ladder.Path}.{key}: expected an array of four quantities.");
                continue;
            }

            var quantities = new List<int>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Number && item.TryGetInt32(out var q) && q >= 0)
                    quantities.Add(q);
                else
                    problems.Add($"{ladder.Path}.{key}: quantities must be whole numbers of zero or more.");
            }

            if (quantities.Count != 4)
            {
                problems.Add($"{ladder.Path}.{key}: expected exactly four quantities, one per target; got {quantities.Count}.");
                continue;
            }

            if (quantities.Sum() != size)
            {
                problems.Add($"{ladder.Path}.{key}: quantities total {quantities.Sum()} but the size is {size}; "
                             + "the connector rejects a bracket whose legs do not sum to the order quantity.");
                continue;
            }

            result[size] = quantities;
        }

        return result;
    }

    private static TrailConfig ReadTrail(Reader trail, List<string> problems) => new()
    {
        Mode = trail.Enum<TrailGeometry>("mode"),
        BeTicksAfterTp1 = trail.Object("beTicksAfterTp1").IntMap(),
        AfterTp2Ticks = trail.Object("afterTp2Ticks").IntMap(),
        AfterTp2TicksTightened = trail.Object("afterTp2TicksTightened").IntMap(),
        AfterTp3Ticks = trail.Object("afterTp3Ticks").IntMap(),
        StructureSwingBufferTicks = trail.Object("structureSwingBufferTicks").IntMap(),
        EvaluateOn = trail.String("evaluateOn"),
        FlowOverride = trail.Bool("flowOverride"),
    };

    private static DataConfig ReadData(Reader data, List<string> problems)
    {
        var l3 = data.Object("l3");
        var calendar = data.Object("calendar");

        return new DataConfig
        {
            RequireTier = data.Enum<DataTier>("requireTier"),
            L3 = new L3Config { Source = l3.String("source"), Pipe = l3.String("pipe") },
            Calendar = new CalendarConfig
            {
                Source = calendar.String("source"),
                Path = calendar.String("path"),
                BlockMinBefore = calendar.Int("blockMinBefore"),
                BlockMinAfter = calendar.Int("blockMinAfter"),
                WhenUnavailable = calendar.Enum<CalendarUnavailablePolicy>("whenUnavailable"),
                ImpactByType = calendar.Object("impactByType").EnumMap<EventImpact>(),
                ImpactDefault = calendar.Enum<EventImpact>("impactDefault"),
                Tier1 = ReadTier1(calendar.Object("tier1")),
            },
        };
    }

    private static Tier1Config ReadTier1(Reader tier1) => new()
    {
        Types = tier1.StringArray("types"),
        NamePatterns = tier1.StringArray("namePatterns"),
        BlockMinBefore = tier1.Int("blockMinBefore"),
        BlockMinAfter = tier1.Int("blockMinAfter"),
    };

    private static KillConfig ReadKill(Reader kill, List<string> problems) => new()
    {
        OrderRatePerMin = kill.Int("orderRatePerMin"),
        SlippageDriftTicks = kill.Int("slippageDriftTicks"),
        StaleQuoteMs = kill.Int("staleQuoteMs"),
        ExpectancyWindow = kill.Int("expectancyWindow"),
    };

    private static StopsConfig ReadStops(Reader stops, List<string> problems) => new()
    {
        AtrMultiple = stops.Double("atrMultiple"),
        AtrPeriod = stops.Int("atrPeriod"),
        BufferMinTicks = stops.Int("bufferMinTicks"),
        BufferSpreadMultiple = stops.Double("bufferSpreadMultiple"),
        BufferOrRangeFraction = stops.Double("bufferOrRangeFraction"),
        StructuralFloorOrFraction = stops.Double("structuralFloorOrFraction"),
        NudgePastLevelTicks = stops.Int("nudgePastLevelTicks"),
    };

    private static RetestConfig ReadRetest(Reader retest, List<string> problems) => new()
    {
        TouchToleranceTicks = retest.Int("touchToleranceTicks"),
        HoldConfirmTicks = retest.Int("holdConfirmTicks"),
        FailureBeyondTicks = retest.Int("failureBeyondTicks"),
        MaxSecondsToRetest = retest.Int("maxSecondsToRetest"),
    };

    private static LevelsConfig ReadLevels(Reader levels, List<string> problems) => new()
    {
        KindStrength = levels.Object("kindStrength").DoubleMap(),
        Overnight = ReadOvernight(levels.Object("overnight")),
        InitialBalanceSession = levels.String("initialBalanceSession"),
        AgeHalfLifeHours = levels.Double("ageHalfLifeHours"),
        ClusterToleranceTicks = levels.Int("clusterToleranceTicks"),
        MaxVisible = levels.Int("maxVisible"),
        MaxRetainedSessions = levels.Int("maxRetainedSessions"),
    };

    private static OvernightConfig ReadOvernight(Reader overnight)
    {
        var byRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, element) in overnight.Object("endSessionByRoot").Properties())
        {
            if (element.ValueKind == JsonValueKind.String)
                byRoot[root] = element.GetString() ?? string.Empty;
        }

        return new OvernightConfig
        {
            StartSession = overnight.String("startSession"),
            EndSession = overnight.String("endSession"),
            EndSessionByRoot = byRoot,
        };
    }

    private static MicroQualityConfig ReadMicroQuality(Reader micro, List<string> problems) => new()
    {
        MaxSpreadMedianMultiple = micro.Double("maxSpreadMedianMultiple"),
        SpreadSampleWindow = micro.Int("spreadSampleWindow"),
        MinSpreadSamples = micro.Int("minSpreadSamples"),
        MaxQuoteAgeMs = micro.Int("maxQuoteAgeMs"),
    };

    private static AbsorptionConfig ReadAbsorption(Reader absorption, List<string> problems) => new()
    {
        WindowSeconds = absorption.Double("windowSeconds"),
        CancelledBelow = absorption.Double("cancelledBelow"),
        AbsorbedAtOrAbove = absorption.Double("absorbedAtOrAbove"),
        GateEntries = absorption.Bool("gateEntries"),
        MaxPricesPerSide = absorption.Int("maxPricesPerSide"),
    };

    private static ImbalanceConfig ReadImbalance(Reader imbalance, List<string> problems) => new()
    {
        Ratio = imbalance.Double("ratio"),
        MinVolume = imbalance.Double("minVolume"),
        MinRun = imbalance.Int("minRun"),
        GateEntries = imbalance.Bool("gateEntries"),
        FootprintHistory = imbalance.Int("footprintHistory"),
    };

    private static ValidationConfig ReadValidation(Reader validation, List<string> problems) => new()
    {
        MinOutOfSampleTrades = validation.Int("minOutOfSampleTrades"),
        MinArmedForwardSessions = validation.Int("minArmedForwardSessions"),
        AttestationFile = validation.String("attestationFile"),
    };

    private static RecorderConfig ReadRecorder(Reader recorder, List<string> problems) => new()
    {
        Enabled = recorder.Bool("enabled"),
        Directory = recorder.OptionalString("directory") ?? string.Empty,
        RecordTrades = recorder.Bool("recordTrades"),
        RecordBook = recorder.Bool("recordBook"),
        FlushIntervalMs = recorder.Int("flushIntervalMs"),
    };

    // ---- cross-field validation ------------------------------------------------------

    private static void Validate(OrbIxConfig c, List<string> problems)
    {
        RequireTimeZone(c.ExchangeTimeZone, "exchangeTimeZone", problems);
        RequireTimeZone(c.SessionTimeZone, "sessionTimeZone", problems);
        RequireTradingWeek(c.TradingWeek, problems);

        if (c.Symbols.Count == 0)
            problems.Add("symbols: at least one product must be configured.");

        foreach (var (root, s) in c.Symbols)
        {
            if (s.Tiers.Count == 0)
                problems.Add($"symbols.{root}.tiers: at least one contract tier is required.");
            if (s.MaxRiskTicks <= 0)
                problems.Add($"symbols.{root}.maxRiskTicks: must be positive.");
            if (s.MaxMinis < 0)
                problems.Add($"symbols.{root}.maxMinis: cannot be negative.");
            if (s.MinTouchDepth < 0)
                problems.Add($"symbols.{root}.minTouchDepth: cannot be negative.");
            if (s.DomWeightScale < 0)
                problems.Add($"symbols.{root}.domWeightScale: cannot be negative.");
            if (!Duration.TryParse(s.EntryTf, out _))
                problems.Add($"symbols.{root}.entryTf: '{s.EntryTf}' is not a duration such as '15s'.");
        }

        foreach (var (name, s) in c.Sessions.Named)
            ValidateSession($"sessions.{name}", name, s, c, problems);

        for (var i = 0; i < c.Sessions.Custom.Count; i++)
        {
            var s = c.Sessions.Custom[i];
            ValidateSession($"sessions.custom[{i}]", s.Name, s, c, problems);
        }

        if (c.Sessions.Named.Count + c.Sessions.Custom.Count == 0)
            problems.Add("sessions: at least one session must be configured.");

        var a = c.Or.Adaptive;
        if (a.Kappa <= 0) problems.Add("or.adaptive.kappa: must be positive.");
        if (a.MinSec < 0) problems.Add("or.adaptive.minSec: cannot be negative.");
        if (a.MaxMin <= 0) problems.Add("or.adaptive.maxMin: must be positive.");
        if (a.MinSec > a.MaxMin * 60)
            problems.Add($"or.adaptive: minSec ({a.MinSec}) exceeds maxMin ({a.MaxMin} min); the range could never close.");
        if (a.MaxWidthAdrPct is <= 0 or > 5)
            problems.Add("or.adaptive.maxWidthAdrPct: expected a fraction of average daily range.");
        if (a.OpenVolLookbackDays <= 0)
            problems.Add("or.adaptive.openVolLookbackDays: must be positive.");
        if (c.Or.AdrPeriod <= 0)
            problems.Add("or.adrPeriod: must be positive.");

        if (c.Or.ExtensionMultiples.Count == 0)
        {
            problems.Add("or.extensionMultiples: at least one projection multiple is required.");
        }
        else
        {
            for (var i = 0; i < c.Or.ExtensionMultiples.Count; i++)
            {
                if (c.Or.ExtensionMultiples[i] <= 0)
                    problems.Add($"or.extensionMultiples[{i}]: projection multiples must be positive.");
                else if (i > 0 && c.Or.ExtensionMultiples[i] <= c.Or.ExtensionMultiples[i - 1])
                    problems.Add($"or.extensionMultiples: must ascend; [{i}] is not beyond [{i - 1}].");
            }
        }

        if (c.Or.Grades.CompressedMaxOrw >= c.Or.Grades.ExhaustedMinOrw)
            problems.Add("or.grades: compressedMaxOrw must be below exhaustedMinOrw, leaving a Normal band between them.");

        foreach (var length in c.Or.Lengths)
        {
            if (!IsAdaptive(length) && !Duration.TryParse(length, out _))
                problems.Add($"or.lengths: '{length}' is neither a duration nor 'adaptive'.");
        }

        var t = c.Scoring.GradeThresholds;
        if (!(t.B < t.A && t.A < t.APlus))
            problems.Add($"scoring.gradeThresholds: expected b < a < aPlus, got {t.B} / {t.A} / {t.APlus}.");

        var w = c.Scoring.Weights;
        var weightTotal = w.Structure + w.OrderFlow + w.Book + w.Micro + w.Positioning;
        if (Math.Abs(weightTotal - 100d) > 1e-9)
            problems.Add($"scoring.weights: must total 100 so the score is on a 0-100 scale; got {weightTotal.ToString("0.###", CultureInfo.InvariantCulture)}.");
        if (t.APlus > weightTotal)
            problems.Add($"scoring: the A+ threshold ({t.APlus}) exceeds the maximum attainable score ({weightTotal}).");

        foreach (var (id, p) in c.Playbooks)
        {
            if (p.SizeMult <= 0)
                problems.Add($"playbooks.{id}.sizeMult: must be positive.");
            if (p.MaxRetestDepthPct is { } depth && depth is <= 0 or > 1)
                problems.Add($"playbooks.{id}.maxRetestDepthPct: expected a fraction of the range above 0 and at most 1.");
            if (p.BreakBufferTicks is { } buffer && buffer < 0)
                problems.Add($"playbooks.{id}.breakBufferTicks: cannot be negative.");
        }

        if (c.Risk.RiskPct is <= 0 or > 1)
            problems.Add("risk.riskPct: expected a fraction of equity above 0 and at most 1.");
        if (c.Risk.SessionRiskCapPct is <= 0 or > 1)
            problems.Add("risk.sessionRiskCapPct: expected a fraction of equity above 0 and at most 1.");
        if (c.Risk.LossesSurvivableDaily <= 0)
            problems.Add("risk.lossesSurvivableDaily: must be positive; dividing by it is what stops one trade consuming the day.");
        if (c.Risk.LossesSurvivableDd <= 0)
            problems.Add("risk.lossesSurvivableDd: must be positive.");
        if (c.Risk.CooldownMinAfterTwoLosses < 0)
            problems.Add("risk.cooldownMinAfterTwoLosses: cannot be negative.");

        foreach (var (pair, coefficient) in c.Risk.CorrelationNetting)
        {
            if (coefficient is < -1 or > 1)
                problems.Add($"risk.correlationNetting.{pair}: expected a correlation between -1 and 1.");
        }

        if (c.Accounts.Count == 0)
            problems.Add("accounts: at least one account policy must be configured.");

        var seenAccountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in c.Accounts)
        {
            if (!seenAccountIds.Add(account.Id))
                problems.Add($"accounts: duplicate account id '{account.Id}'.");
            if (account.Equity is <= 0)
                problems.Add($"accounts.{account.Id}.equity: must be positive when present.");
            if (account.DailyLimit is <= 0)
                problems.Add($"accounts.{account.Id}.dailyLimit: must be positive when present.");
            if (account.ConsistencyTarget is { } pct && pct is <= 0 or > 1)
                problems.Add($"accounts.{account.Id}.consistencyTarget: expected a fraction above 0 and at most 1.");
            if (account.RiskPctOverride is { } over && over is <= 0 or > 1)
                problems.Add($"accounts.{account.Id}.riskPctOverride: expected a fraction above 0 and at most 1.");
            if (account.FlatByTime is { } flat && !TryParseWallClock(flat, out _))
                problems.Add($"accounts.{account.Id}.flatByTime: '{flat}' is not an HH:mm wall-clock time.");
            if (account.TrailingLimit is { } dd)
            {
                if (dd.Amount <= 0)
                    problems.Add($"accounts.{account.Id}.trailingLimit.amount: must be positive.");
                if (!string.Equals(dd.Type, "intraday", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dd.Type, "eod", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"accounts.{account.Id}.trailingLimit.type: expected 'intraday' or 'eod', got '{dd.Type}'.");
            }
        }

        if (c.Targets.Allocation.Count != 4)
            problems.Add($"targets.allocation: expected exactly four fractions, one per target; got {c.Targets.Allocation.Count}.");
        else
        {
            var allocationTotal = c.Targets.Allocation.Sum();
            if (Math.Abs(allocationTotal - 1d) > 1e-9)
                problems.Add($"targets.allocation: must total 1.0 so the whole position is allocated; got {allocationTotal.ToString("0.####", CultureInfo.InvariantCulture)}.");
            if (c.Targets.Allocation.Any(x => x <= 0))
                problems.Add("targets.allocation: every fraction must be positive; a zero target is a target that does not exist.");
        }

        if (c.Targets.SnapAheadTicks < 0)
            problems.Add("targets.snapAheadTicks: cannot be negative.");

        RequireTrailMapCoversTiers(c, c.Trail.BeTicksAfterTp1, "beTicksAfterTp1", problems);
        RequireTrailMapCoversTiers(c, c.Trail.AfterTp2Ticks, "afterTp2Ticks", problems);
        RequireTrailMapCoversTiers(c, c.Trail.AfterTp2TicksTightened, "afterTp2TicksTightened", problems);
        RequireTrailMapCoversTiers(c, c.Trail.AfterTp3Ticks, "afterTp3Ticks", problems);
        RequireTrailMapCoversTiers(c, c.Trail.StructureSwingBufferTicks, "structureSwingBufferTicks", problems);

        foreach (var key in c.Trail.AfterTp2Ticks.Keys)
        {
            if (c.Trail.AfterTp2TicksTightened.TryGetValue(key, out var tightened)
                && c.Trail.AfterTp2Ticks.TryGetValue(key, out var initial)
                && tightened > initial)
            {
                problems.Add($"trail: {key} tightened distance ({tightened}) exceeds the initial distance ({initial}); a trail only ever moves toward profit.");
            }
        }

        if (!string.Equals(c.Trail.EvaluateOn, "barClose", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.Trail.EvaluateOn, "tick", StringComparison.OrdinalIgnoreCase))
            problems.Add($"trail.evaluateOn: expected 'barClose' or 'tick', got '{c.Trail.EvaluateOn}'.");

        if (c.Data.Calendar.BlockMinBefore < 0 || c.Data.Calendar.BlockMinAfter < 0)
            problems.Add("data.calendar: blackout windows cannot be negative.");

        if (c.Kill.OrderRatePerMin <= 0)
            problems.Add("kill.orderRatePerMin: must be positive; the fuse exists to catch a feedback loop.");
        if (c.Kill.SlippageDriftTicks <= 0)
            problems.Add("kill.slippageDriftTicks: must be positive.");
        if (c.Kill.StaleQuoteMs <= 0)
            problems.Add("kill.staleQuoteMs: must be positive.");
        if (c.Kill.ExpectancyWindow <= 0)
            problems.Add("kill.expectancyWindow: must be positive.");

        if (c.Stops.AtrMultiple <= 0)
            problems.Add("stops.atrMultiple: must be positive.");
        if (c.Stops.AtrPeriod <= 0)
            problems.Add("stops.atrPeriod: must be positive.");
        if (c.Stops.BufferMinTicks <= 0)
            problems.Add("stops.bufferMinTicks: must be positive; the obvious stop price is the most hunted one.");
        if (c.Stops.BufferSpreadMultiple <= 0)
            problems.Add("stops.bufferSpreadMultiple: must be positive.");
        if (c.Stops.BufferOrRangeFraction is <= 0 or > 1)
            problems.Add("stops.bufferOrRangeFraction: expected a fraction of the range above 0 and at most 1.");
        if (c.Stops.StructuralFloorOrFraction is <= 0 or > 1)
            problems.Add("stops.structuralFloorOrFraction: expected a fraction of the range above 0 and at most 1.");
        if (c.Stops.NudgePastLevelTicks < 0)
            problems.Add("stops.nudgePastLevelTicks: cannot be negative.");

        if (c.Targets.Tp1RMin <= 0 || c.Targets.Tp1RMax <= c.Targets.Tp1RMin)
            problems.Add($"targets: expected 0 < tp1RMin < tp1RMax, got {c.Targets.Tp1RMin} and {c.Targets.Tp1RMax}.");
        if (c.Targets.Tp3SnapOrFraction is <= 0 or > 1)
            problems.Add("targets.tp3SnapOrFraction: expected a fraction of the range above 0 and at most 1.");

        if (c.Retest.TouchToleranceTicks <= 0)
            problems.Add("retest.touchToleranceTicks: must be positive; a zero tolerance sees no retest at all.");
        if (c.Retest.HoldConfirmTicks <= 0)
            problems.Add("retest.holdConfirmTicks: must be positive.");
        if (c.Retest.FailureBeyondTicks <= 0)
            problems.Add("retest.failureBeyondTicks: must be positive.");
        if (c.Retest.FailureBeyondTicks <= c.Retest.TouchToleranceTicks)
            problems.Add($"retest: failureBeyondTicks ({c.Retest.FailureBeyondTicks}) must exceed touchToleranceTicks ({c.Retest.TouchToleranceTicks}), or touching the level would itself be a failure.");
        if (c.Retest.MaxSecondsToRetest <= 0)
            problems.Add("retest.maxSecondsToRetest: must be positive.");

        if (c.Levels.AgeHalfLifeHours <= 0)
            problems.Add("levels.ageHalfLifeHours: must be positive; a non-positive half-life makes strength meaningless.");
        if (c.Levels.ClusterToleranceTicks < 0)
            problems.Add("levels.clusterToleranceTicks: cannot be negative.");
        if (c.Levels.MaxVisible <= 0)
            problems.Add("levels.maxVisible: must be positive.");

        if (c.Levels.MaxRetainedSessions <= 0)
            problems.Add("levels.maxRetainedSessions: must be positive; retaining no sessions draws nothing.");
        if (c.Levels.KindStrength.Count == 0)
            problems.Add("levels.kindStrength: at least one level kind must carry a strength.");

        foreach (var (kind, strength) in c.Levels.KindStrength)
        {
            if (strength is <= 0 or > 1)
                problems.Add($"levels.kindStrength.{kind}: expected a strength above 0 and at most 1.");

            // AN UNKNOWN KEY IS A TYPO OR A STALE NAME, and silence about it is how a rename
            // disables a level. The configuration would keep the old key, no LevelKind would
            // ever match it, and the level it was meant to weight would score zero forever.
            if (!System.Enum.TryParse<Features.LevelKind>(kind, ignoreCase: false, out _))
            {
                problems.Add(
                    $"levels.kindStrength.{kind}: no level kind by that name. Known kinds: "
                    + string.Join(", ", System.Enum.GetNames<Features.LevelKind>()) + ".");
            }
        }

        // EVERY KIND MUST BE WEIGHTED, because LevelGraph.StrengthOf returns 0 for one that is
        // not — silently. A level with no configured strength stops clustering, stops nudging
        // stops clear of itself, and vanishes from the chart, while every test still passes and
        // nothing is logged. That is exactly how SessionVwap could sit mislabelled for months
        // and how OvernightHigh/Low could carry a strength of 0.85 while no code built them.
        //
        // Refusing here turns that from an invisible behaviour change into a start-up failure
        // naming the kind.
        var unweighted = System.Enum.GetNames<Features.LevelKind>()
            .Where(name => !c.Levels.KindStrength.ContainsKey(name))
            .ToList();

        if (unweighted.Count > 0)
        {
            problems.Add(
                "levels.kindStrength: every level kind must carry a strength, because one that "
                + "does not is silently scored zero and disappears. Missing: "
                + string.Join(", ", unweighted) + ".");
        }

        // A SESSION NAME THAT MATCHES NOTHING PRODUCES NO OVERNIGHT LEVELS, SILENTLY — which is
        // the same failure mode as the unweighted kind above, and exactly how these levels came
        // to be configured-but-absent in the first place.
        // Resolved the same way SessionClock resolves them, so a name this check accepts is a
        // name the clock will actually find — including the custom list.
        var sessionNames = SessionDefinition.ResolveAll(c)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (setting, named) in new[]
                 {
                     ("levels.overnight.startSession", c.Levels.Overnight.StartSession),
                     ("levels.overnight.endSession", c.Levels.Overnight.EndSession),
                 })
        {
            if (string.IsNullOrWhiteSpace(named) || !sessionNames.Contains(named))
            {
                problems.Add(
                    $"{setting}: '{named}' names no configured session. Known sessions: "
                    + string.Join(", ", sessionNames.OrderBy(n => n, StringComparer.Ordinal)) + ".");
            }
        }

        foreach (var (root, named) in c.Levels.Overnight.EndSessionByRoot)
        {
            if (!sessionNames.Contains(named))
            {
                problems.Add(
                    $"levels.overnight.endSessionByRoot.{root}: '{named}' names no configured session.");
            }
        }

        // THE SAME RULE FOR THE INITIAL BALANCE, with one deliberate difference: an EMPTY name
        // is accepted, because empty is how the mapping is turned off. Only a name that was
        // actually written and matches nothing is refused — otherwise InitialBalanceHigh/Low
        // would go back to being weighted at 0.75 while nothing built them, which is the exact
        // condition this whole check exists to end.
        if (!string.IsNullOrWhiteSpace(c.Levels.InitialBalanceSession)
            && !sessionNames.Contains(c.Levels.InitialBalanceSession))
        {
            problems.Add(
                $"levels.initialBalanceSession: '{c.Levels.InitialBalanceSession}' names no "
                + "configured session. Known sessions: "
                + string.Join(", ", sessionNames.OrderBy(n => n, StringComparer.Ordinal)) + ".");
        }

        foreach (var (root, symbol) in c.Symbols)
        {
            if (symbol.RoundNumberStep <= 0)
                problems.Add($"symbols.{root}.roundNumberStep: must be positive.");
        }

        if (c.MicroQuality.MaxSpreadMedianMultiple <= 1)
            problems.Add("microQuality.maxSpreadMedianMultiple: must exceed 1; a threshold at or below the median rejects normal conditions.");
        if (c.MicroQuality.SpreadSampleWindow <= 0)
            problems.Add("microQuality.spreadSampleWindow: must be positive.");
        if (c.MicroQuality.MinSpreadSamples <= 0)
            problems.Add("microQuality.minSpreadSamples: must be positive.");
        if (c.MicroQuality.MinSpreadSamples > c.MicroQuality.SpreadSampleWindow)
            problems.Add($"microQuality: minSpreadSamples ({c.MicroQuality.MinSpreadSamples}) exceeds spreadSampleWindow ({c.MicroQuality.SpreadSampleWindow}); the gate could never satisfy itself.");
        if (c.MicroQuality.MaxQuoteAgeMs <= 0)
            problems.Add("microQuality.maxQuoteAgeMs: must be positive.");

        if (c.Imbalance.Ratio <= 1)
            problems.Add("imbalance.ratio: must exceed 1; at or below it the larger side of every diagonal is 'imbalanced'.");
        if (c.Imbalance.MinVolume <= 0)
            problems.Add("imbalance.minVolume: must be positive; a minimum of zero judges diagonals nobody traded on.");
        if (c.Imbalance.MinRun < 1)
            problems.Add("imbalance.minRun: must be at least 1; a run of zero is satisfied by a bar that never traded.");
        if (c.Absorption.WindowSeconds <= 0)
            problems.Add("absorption.windowSeconds: must be positive; over a zero span nothing is ever absorbed.");
        if (c.Absorption.CancelledBelow <= 0)
            problems.Add("absorption.cancelledBelow: must be positive; a threshold at or below zero can never be met.");
        if (c.Absorption.AbsorbedAtOrAbove <= c.Absorption.CancelledBelow)
            problems.Add($"absorption: absorbedAtOrAbove ({c.Absorption.AbsorbedAtOrAbove}) must exceed cancelledBelow ({c.Absorption.CancelledBelow}); otherwise a level satisfies both states at once.");
        if (c.Absorption.MaxPricesPerSide < 2)
            problems.Add("absorption.maxPricesPerSide: must be at least 2; a book needs room for a price on each side of the touch.");

        if (c.Imbalance.FootprintHistory < 1)
            problems.Add("imbalance.footprintHistory: must be at least 1; retaining no closed bars leaves the break bar unreadable.");

        if (string.IsNullOrWhiteSpace(c.Data.Calendar.Path))
            problems.Add("data.calendar.path: a path is required, even if the file does not exist yet.");

        if (c.Validation.MinOutOfSampleTrades <= 0)
            problems.Add("validation.minOutOfSampleTrades: must be positive; Auto mode is gated on it.");
        if (c.Validation.MinArmedForwardSessions <= 0)
            problems.Add("validation.minArmedForwardSessions: must be positive; Auto mode is gated on it.");
        if (string.IsNullOrWhiteSpace(c.Validation.AttestationFile))
            problems.Add("validation.attestationFile: a path is required, even if the file does not exist yet.");

        if (c.Recorder.FlushIntervalMs <= 0)
            problems.Add("recorder.flushIntervalMs: must be positive.");

        if (c.Recorder.Enabled && !c.Recorder.RecordTrades && !c.Recorder.RecordBook)
            problems.Add("recorder: enabled but recording neither trades nor book; disable it or choose a stream.");

        ValidateFlow(c, problems);
    }

    private static void ValidateSession(
        string path, string name, SessionConfig s, OrbIxConfig c, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
            problems.Add($"{path}: a session must be named.");

        if (!TryParseWallClock(s.Open, out _))
            problems.Add($"{path}.open: '{s.Open}' is not an HH:mm wall-clock time.");

        if (!IsAdaptive(s.Or) && !Duration.TryParse(s.Or, out var length))
            problems.Add($"{path}.or: '{s.Or}' is neither a duration such as '15m' nor 'adaptive'.");
        else if (!IsAdaptive(s.Or) && Duration.TryParse(s.Or, out length) && length <= TimeSpan.Zero)
            problems.Add($"{path}.or: opening-range length must be positive.");

        if (s.Budget is <= 0)
            problems.Add($"{path}.budget: must be positive when present; disable the session instead of budgeting it to zero.");

        foreach (var root in s.Symbols)
        {
            if (!c.Symbols.ContainsKey(root))
                problems.Add($"{path}.symbols: '{root}' is not a configured product.");
        }

        if (IsAdaptive(s.Or) && !c.Or.Adaptive.Enabled)
            problems.Add($"{path}.or: adaptive is selected but or.adaptive.enabled is false.");
    }

    private static void RequireTrailMapCoversTiers(
        OrbIxConfig c, IReadOnlyDictionary<string, int> map, string key, List<string> problems)
    {
        foreach (var (_, symbol) in c.Symbols)
        {
            foreach (var tier in symbol.Tiers)
            {
                if (!map.ContainsKey(tier) && !map.ContainsKey(TrailKeyFor(tier, c)))
                {
                    problems.Add($"trail.{key}: no entry covers contract tier '{tier}'.");
                }
            }
        }

        foreach (var (_, value) in map)
        {
            if (value < 0)
                problems.Add($"trail.{key}: tick distances cannot be negative.");
        }
    }

    /// <summary>
    /// Trail distances are quoted per product family, not per contract tier: an NQ and an
    /// MNQ move the same number of ticks. A tier falls back to the family root it belongs
    /// to, so "MNQ" is covered by an "NQ" entry.
    /// </summary>
    private static string TrailKeyFor(string tier, OrbIxConfig c)
    {
        foreach (var (root, symbol) in c.Symbols)
        {
            if (symbol.Tiers.Contains(tier, StringComparer.OrdinalIgnoreCase))
                return root;
        }

        return tier;
    }

    /// <summary>
    /// The trading week. Read as strings and validated separately, like every other block
    /// here, so a bad value is reported with its key rather than thrown from deep inside
    /// the session clock.
    /// </summary>
    private static TradingWeekConfig ReadTradingWeek(Reader r, List<string> problems)
        => new()
        {
            OpenDay = r.String("openDay"),
            OpenTime = r.String("openTime"),
            CloseDay = r.String("closeDay"),
            CloseTime = r.String("closeTime"),
        };

    /// <summary>
    /// A trading week must name two real days and two real times, and must not open and
    /// close at the same instant — which would describe a market that is either never open
    /// or never shut.
    /// </summary>
    private static void RequireTradingWeek(TradingWeekConfig week, List<string> problems)
    {
        if (week is null)
        {
            problems.Add("tradingWeek: missing.");
            return;
        }

        var ok = true;

        if (!Enum.TryParse<DayOfWeek>(week.OpenDay, ignoreCase: true, out var openDay))
        {
            problems.Add($"tradingWeek.openDay: '{week.OpenDay}' is not a day of the week.");
            ok = false;
        }

        if (!Enum.TryParse<DayOfWeek>(week.CloseDay, ignoreCase: true, out var closeDay))
        {
            problems.Add($"tradingWeek.closeDay: '{week.CloseDay}' is not a day of the week.");
            ok = false;
        }

        if (!TryParseWallClock(week.OpenTime, out var openTime))
        {
            problems.Add($"tradingWeek.openTime: '{week.OpenTime}' is not an HH:mm wall-clock time.");
            ok = false;
        }

        if (!TryParseWallClock(week.CloseTime, out var closeTime))
        {
            problems.Add($"tradingWeek.closeTime: '{week.CloseTime}' is not an HH:mm wall-clock time.");
            ok = false;
        }

        if (ok && openDay == closeDay && openTime == closeTime)
        {
            problems.Add(
                "tradingWeek: opens and closes at the same instant, which describes either a "
                + "market that never opens or one that never closes.");
        }
    }

    private static void RequireTimeZone(string id, string key, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            problems.Add($"{key}: a time zone identifier is required.");
            return;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            problems.Add($"{key}: '{id}' is not a time zone this system knows.");
        }
        catch (InvalidTimeZoneException ex)
        {
            problems.Add($"{key}: '{id}' is corrupt in this system's time zone data: {ex.Message}");
        }
    }

    internal static bool IsAdaptive(string value)
        => string.Equals(value, "adaptive", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseWallClock(string value, out TimeSpan time)
        => TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time);

    // ---- element reader --------------------------------------------------------------

    /// <summary>
    /// A thin reader over a <see cref="JsonElement"/> that records a descriptive problem
    /// instead of throwing, so one pass collects every fault in the document.
    /// </summary>
    private readonly struct Reader
    {
        private readonly JsonElement element;
        private readonly List<string> problems;

        internal Reader(JsonElement element, List<string> problems, string path)
        {
            this.element = element;
            this.problems = problems;
            this.Path = path;
        }

        internal string Path { get; }

        private string PathTo(string key) => this.Path.Length == 0 ? key : this.Path + "." + key;

        private bool TryGet(string key, JsonValueKind expected, out JsonElement value)
        {
            value = default;

            if (this.element.ValueKind != JsonValueKind.Object)
            {
                this.problems.Add($"{(this.Path.Length == 0 ? "<root>" : this.Path)}: expected an object.");
                return false;
            }

            if (!this.element.TryGetProperty(key, out var found))
            {
                this.problems.Add($"{this.PathTo(key)}: required key is missing.");
                return false;
            }

            if (expected == JsonValueKind.True)
            {
                if (found.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    this.problems.Add($"{this.PathTo(key)}: expected true or false.");
                    return false;
                }
            }
            else if (found.ValueKind != expected)
            {
                this.problems.Add($"{this.PathTo(key)}: expected {Describe(expected)}, found {Describe(found.ValueKind)}.");
                return false;
            }

            value = found;
            return true;
        }

        private static string Describe(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "true or false",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant(),
        };

        /// <summary>
        /// Records a problem against one of this object's keys.
        ///
        /// Exists so a rule that needs two keys at once — a bar count that is required only under
        /// one extent — can be enforced where both are in hand, and still report at the same path
        /// and in the same pass as every other fault.
        /// </summary>
        internal void Problem(string key, string message)
            => this.problems.Add($"{this.PathTo(key)}: {message}");

        internal string String(string key)
            => this.TryGet(key, JsonValueKind.String, out var v) ? v.GetString() ?? string.Empty : string.Empty;

        internal string? OptionalString(string key)
            => this.element.ValueKind == JsonValueKind.Object
               && this.element.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        internal bool Bool(string key)
            => this.TryGet(key, JsonValueKind.True, out var v) && v.GetBoolean();

        internal bool? OptionalBool(string key)
            => this.element.ValueKind == JsonValueKind.Object
               && this.element.TryGetProperty(key, out var v)
               && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;

        internal double Double(string key)
            => this.TryGet(key, JsonValueKind.Number, out var v) ? v.GetDouble() : 0d;

        internal double? OptionalDouble(string key)
            => this.element.ValueKind == JsonValueKind.Object
               && this.element.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble()
                : null;

        internal int Int(string key)
        {
            if (!this.TryGet(key, JsonValueKind.Number, out var v))
                return 0;

            if (v.TryGetInt32(out var i))
                return i;

            this.problems.Add($"{this.PathTo(key)}: expected a whole number.");
            return 0;
        }

        internal int? OptionalInt(string key)
        {
            if (this.element.ValueKind != JsonValueKind.Object
                || !this.element.TryGetProperty(key, out var v)
                || v.ValueKind != JsonValueKind.Number)
                return null;

            if (v.TryGetInt32(out var i))
                return i;

            this.problems.Add($"{this.PathTo(key)}: expected a whole number.");
            return null;
        }

        internal TEnum Enum<TEnum>(string key) where TEnum : struct, Enum
        {
            if (!this.TryGet(key, JsonValueKind.String, out var v))
                return default;

            var raw = v.GetString();
            if (System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed))
                return parsed;

            this.problems.Add(
                $"{this.PathTo(key)}: '{raw}' is not one of {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
            return default;
        }

        internal Reader Object(string key)
            => this.TryGet(key, JsonValueKind.Object, out var v)
                ? new Reader(v, this.problems, this.PathTo(key))
                : new Reader(default, this.problems, this.PathTo(key));

        internal Reader? OptionalObject(string key)
            => this.element.ValueKind == JsonValueKind.Object
               && this.element.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.Object
                ? new Reader(v, this.problems, this.PathTo(key))
                : null;

        internal IReadOnlyList<Reader> Array(string key)
        {
            var result = new List<Reader>();
            if (!this.TryGet(key, JsonValueKind.Array, out var v))
                return result;

            var index = 0;
            foreach (var item in v.EnumerateArray())
            {
                result.Add(new Reader(item, this.problems, $"{this.PathTo(key)}[{index}]"));
                index++;
            }

            return result;
        }

        internal IReadOnlyList<string> StringArray(string key)
        {
            var result = new List<string>();
            if (!this.TryGet(key, JsonValueKind.Array, out var v))
                return result;

            var index = 0;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    result.Add(item.GetString() ?? string.Empty);
                else
                    this.problems.Add($"{this.PathTo(key)}[{index}]: expected a string.");
                index++;
            }

            return result;
        }

        internal IReadOnlyList<string>? OptionalStringArray(string key)
            => this.element.ValueKind == JsonValueKind.Object && this.element.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.Array
                ? this.StringArray(key)
                : null;

        internal IReadOnlyList<double> DoubleArray(string key)
        {
            var result = new List<double>();
            if (!this.TryGet(key, JsonValueKind.Array, out var v))
                return result;

            var index = 0;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                    result.Add(item.GetDouble());
                else
                    this.problems.Add($"{this.PathTo(key)}[{index}]: expected a number.");
                index++;
            }

            return result;
        }

        internal IEnumerable<(string Name, JsonElement Value)> Properties()
        {
            if (this.element.ValueKind != JsonValueKind.Object)
            {
                this.problems.Add($"{(this.Path.Length == 0 ? "<root>" : this.Path)}: expected an object.");
                yield break;
            }

            foreach (var property in this.element.EnumerateObject())
                yield return (property.Name, property.Value);
        }

        internal IReadOnlyDictionary<string, int> IntMap()
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in this.Properties())
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
                    result[name] = i;
                else
                    this.problems.Add($"{this.PathTo(name)}: expected a whole number.");
            }

            return result;
        }

        /// <summary>
        /// Reads an object whose values name enum members.
        ///
        /// Keys are compared case-insensitively, which matters here rather than being
        /// tidiness: Unusual Whales' own specification declares the event type enum as
        /// <c>fomc</c> while the live API returns <c>FOMC</c>, so a case-sensitive map would
        /// silently fail to classify every FOMC event.
        /// </summary>
        internal IReadOnlyDictionary<string, TEnum> EnumMap<TEnum>() where TEnum : struct, System.Enum
        {
            var result = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in this.Properties())
            {
                // System.Enum is qualified because Reader has its own member named Enum,
                // which shadows the type inside this class.
                if (value.ValueKind == JsonValueKind.String
                    && System.Enum.TryParse<TEnum>(value.GetString(), ignoreCase: true, out var parsed))
                {
                    result[name] = parsed;
                }
                else
                {
                    this.problems.Add(
                        $"{this.PathTo(name)}: expected one of {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
                }
            }

            return result;
        }

        internal IReadOnlyDictionary<string, double> DoubleMap()
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in this.Properties())
            {
                if (value.ValueKind == JsonValueKind.Number)
                    result[name] = value.GetDouble();
                else
                    this.problems.Add($"{this.PathTo(name)}: expected a number.");
            }

            return result;
        }

        /// <summary>
        /// Reads a colour written as <c>#RRGGBB</c>.
        ///
        /// A colour that will not parse is reported and read as black rather than as some default
        /// hue, because a wrong colour that looks deliberate is harder to notice than one that
        /// looks broken — and the fault is reported either way, so nobody has to notice it.
        /// </summary>
        internal Rgb Colour(string key)
        {
            if (!this.TryGet(key, JsonValueKind.String, out var v))
                return default;

            if (RgbHex.TryParse(v.GetString(), out var colour))
                return colour;

            this.problems.Add(
                $"{this.PathTo(key)}: '{v.GetString()}' is not a colour. Write it as #RRGGBB.");
            return default;
        }

        /// <summary>Reads an array whose entries name enum members.</summary>
        internal IReadOnlyList<TEnum> EnumArray<TEnum>(string key) where TEnum : struct, Enum
        {
            var result = new List<TEnum>();

            if (!this.TryGet(key, JsonValueKind.Array, out var v))
                return result;

            var index = 0;

            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && System.Enum.TryParse<TEnum>(item.GetString(), ignoreCase: true, out var parsed))
                {
                    result.Add(parsed);
                }
                else
                {
                    this.problems.Add(
                        $"{this.PathTo(key)}[{index}]: expected one of {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
                }

                index++;
            }

            return result;
        }

        /// <summary>Reads an object whose KEYS name enum members and whose values are numbers.</summary>
        internal IReadOnlyDictionary<TEnum, double> EnumKeyedDoubleMap<TEnum>(string key)
            where TEnum : struct, Enum
        {
            var result = new Dictionary<TEnum, double>();

            if (!this.TryGet(key, JsonValueKind.Object, out var v))
                return result;

            foreach (var property in v.EnumerateObject())
            {
                if (!System.Enum.TryParse<TEnum>(property.Name, ignoreCase: true, out var parsed))
                {
                    this.problems.Add(
                        $"{this.PathTo(key)}.{property.Name}: expected one of {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Number)
                {
                    this.problems.Add($"{this.PathTo(key)}.{property.Name}: expected a number.");
                    continue;
                }

                result[parsed] = property.Value.GetDouble();
            }

            return result;
        }

        /// <summary>Reads an <c>HH:mm</c> wall-clock time, in whatever zone its section names.</summary>
        internal TimeOnly WallClock(string key)
        {
            if (!this.TryGet(key, JsonValueKind.String, out var v))
                return default;

            if (TryParseWallClock(v.GetString() ?? string.Empty, out var time))
                return TimeOnly.FromTimeSpan(time);

            this.problems.Add($"{this.PathTo(key)}: '{v.GetString()}' is not an HH:mm wall-clock time.");
            return default;
        }
    }
}
