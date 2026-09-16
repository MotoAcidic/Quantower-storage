using AuctionResponse.Core;

namespace AuctionResponse.Tests;

/// <summary>
/// Builds event-level scenarios: every candidate is armed by real constituent trades and
/// quotes travelling through the ingress, not by hand-poking feature values. Section 18
/// requires exactly that for anything beyond the pure feature fixtures.
///
/// The baseline artifact here is SYNTHETIC and lives only in the test project. It is never
/// shipped and the indicator cannot load it; a synthetic quantile must never be mistaken
/// for a trained market baseline.
/// </summary>
public sealed class Scenario
{
    public const long Second = 1_000_000_000L;
    public const long Millisecond = 1_000_000L;

    /// <summary>2026-09-14 10:00:00 America/Chicago — inside the RTH session, 90 minutes in.</summary>
    public static readonly DateTime SessionBaseUtc = new(2026, 9, 14, 15, 0, 0, DateTimeKind.Utc);

    private long _sequence;
    private readonly List<MarketEvent> _log = new();
    private readonly List<DecisionTick> _tickLog = new();

    public Scenario(Config? config = null, bool withBaseline = true, decimal tickSize = 0.25m)
    {
        Config = config ?? ReadyConfig();
        Instrument = new InstrumentKey("MNQ", "CME", "202612");
        Engine = new Engine(Config, Instrument, tickSize, feedMode: "Test", simulatedData: true)
        {
            Capabilities = new CapabilityFlags
            {
                Trades = true,
                Quotes = true,
                MarketByPrice = true,
                MarketByOrderVerified = false,
                ExecutionLinkage = false,
                MboUnverifiedReason = "test harness does not establish a snapshot fence"
            }
        };
        if (withBaseline) Engine.LoadBaseline(SyntheticBaseline(tickSize));

        // Start well past warmup so the scenarios exercise the rule, not the warm-up gate.
        Ns = 40 * Second;
    }

    public Config Config { get; }
    public Engine Engine { get; }
    public InstrumentKey Instrument { get; }
    public long Ns { get; private set; }

    public IReadOnlyList<MarketEvent> EventLog => _log;
    public IReadOnlyList<DecisionTick> TickLog => _tickLog;

    /// <summary>A config with every owner input resolved, so the engine can reach Ready.</summary>
    public static Config ReadyConfig() => new()
    {
        Instrument = new InstrumentConfig
        {
            InstrumentKey = new InstrumentKey("MNQ", "CME", "202612"),
            TickSize = 0.25m
        },
        // Central everywhere: settings entered in Central, math done in Central, labels
        // printed in Central. RTH 08:30-15:00 is the same session the spec names in ET.
        Session = new SessionConfig
        {
            Timezone = "America/Chicago",
            WindowsTimezoneEquivalent = "Central Standard Time",
            StartLocal = "08:30:00",
            EndLocalExclusive = "15:00:00"
        },
        Baseline = new BaselineConfig { ArtifactPath = "synthetic://test" },
        Recording = new RecordingConfig { Directory = "synthetic://test", PermissionConfirmed = true }
    };

    public static BaselineArtifact SyntheticBaseline(decimal tickSize)
    {
        var buckets = new List<BaselineBucket>();
        foreach (var window in new[] { 1000, 5000, 30000 })
            for (var minute = 0; minute < 390; minute += 30)
                buckets.Add(new BaselineBucket
                {
                    BucketStartMinute = minute,
                    WindowMs = window,
                    SampleCount = 300,
                    BuyVolumeQuantiles = new Dictionary<double, double> { [0.9] = 150d },
                    SellVolumeQuantiles = new Dictionary<double, double> { [0.9] = 150d },
                    AbsoluteDeltaQ75 = 80d,
                    AbsoluteResponseQ75 = 4d,
                    SortedBuyVolumes = new double[] { 10, 40, 80, 120, 150, 180 },
                    SortedSellVolumes = new double[] { 10, 40, 80, 120, 150, 180 }
                });

        return new BaselineArtifact
        {
            Instrument = new InstrumentKey("MNQ", "CME", "202612"),
            TickSize = tickSize,
            SessionTimezone = "America/Chicago",
            CalendarVersion = "test-1",
            ClockMode = "ReceiveElapsedWithRecordedDecisionTicks",
            FeedMode = "Test",
            WindowsMs = new[] { 1000, 5000, 30000 },
            BucketMinutes = 30,
            EligibleSessionIds = Enumerable.Range(1, 20).Select(i => "2026-08-" + i.ToString("00")).ToArray(),
            FitEndUtc = SessionBaseUtc.AddDays(-1),
            SourceLogHashes = new[] { "synthetic" },
            Sha256 = "synthetic-test-baseline-not-a-trained-market-artifact",
            Buckets = buckets
        };
    }

    public DateTime UtcAt(long ns) => SessionBaseUtc.AddTicks(ns / 100);

    public void Advance(long deltaNs) => Ns += deltaNs;
    public void AdvanceMs(long ms) => Ns += ms * Millisecond;

    private MarketEvent Stamp(MarketEvent template) => template with
    {
        EventSequence = ++_sequence,
        Instrument = Instrument,
        ConnectionEpoch = Engine.ConnectionEpoch,
        ReceiveElapsedNs = Ns,
        ReceiveUtc = UtcAt(Ns)
    };

    public MarketEvent Quote(long bidTicks, decimal bidQty, long askTicks, decimal askQty, QualityFlag quality = QualityFlag.None)
    {
        var ev = Stamp(new MarketEvent
        {
            Kind = EventKind.BestQuote,
            BidTicks = bidTicks, BidQuantity = bidQty,
            AskTicks = askTicks, AskQuantity = askQty,
            Quality = quality
        });
        _log.Add(ev);
        Engine.Accept(ev);
        return ev;
    }

    public MarketEvent Trade(AggressorDirection dir, decimal qty, long priceTicks)
    {
        var ev = Stamp(new MarketEvent
        {
            Kind = EventKind.Trade,
            Direction = dir,
            OriginalQuantity = qty,
            PriceTicks = priceTicks,
            RawDirection = (int)dir
        });
        _log.Add(ev);
        Engine.Accept(ev);
        return ev;
    }

    public MarketEvent Reconnect()
    {
        // Stamp() carries the CURRENT epoch, so the new epoch is set after stamping.
        var ev = Stamp(new MarketEvent { Kind = EventKind.Connection, Detail = "reconnect" })
                 with { ConnectionEpoch = Engine.ConnectionEpoch + 1 };
        _log.Add(ev);
        Engine.Accept(ev);
        return ev;
    }

    public DecisionResult Tick()
    {
        var tick = new DecisionTick
        {
            EventSequence = ++_sequence,
            ConnectionEpoch = Engine.ConnectionEpoch,
            ElapsedNs = Ns,
            ReceiveUtc = UtcAt(Ns)
        };
        _tickLog.Add(tick);
        return Engine.Decide(tick);
    }

    /// <summary>Emits a steady quote stream so the freshness guard is never tripped by silence.</summary>
    public void QuoteStream(long durationNs, long intervalNs, long bidTicks, long askTicks, decimal bidQty = 100m, decimal askQty = 100m)
    {
        var end = Ns + durationNs;
        while (Ns < end)
        {
            Quote(bidTicks, bidQty, askTicks, askQty);
            Advance(intervalNs);
        }
    }

    public LevelDefinition DeclareResistance(long priceTicks, string id = "R1")
    {
        var level = new LevelDefinition
        {
            LevelId = id,
            Side = LevelSide.Resistance,
            PriceTicks = priceTicks,
            EffectiveNs = 0,
            ConnectionEpoch = Engine.ConnectionEpoch,
            Source = "test"
        };
        Engine.AddLevel(level);
        return level;
    }

    public LevelDefinition DeclareSupport(long priceTicks, string id = "S1")
    {
        var level = new LevelDefinition
        {
            LevelId = id,
            Side = LevelSide.Support,
            PriceTicks = priceTicks,
            EffectiveNs = 0,
            ConnectionEpoch = Engine.ConnectionEpoch,
            Source = "test"
        };
        Engine.AddLevel(level);
        return level;
    }

    /// <summary>
    /// Drives the canonical resistance approach from Section 18: midpoint path starting at
    /// 399, minimum 399, maximum 401, ending at 400, with buy volume executed in the zone.
    /// Leaves the clock at the moment a decision tick should arm the candidate.
    /// </summary>
    public void ApproachResistance(decimal buyVolume = 200m, decimal sellVolume = 50m, long zonePriceTicks = 400)
    {
        // Carried state before the window: midpoint 399 (bid 398 / ask 400).
        QuoteStream(6 * Second, 200 * Millisecond, 398, 400);

        // Inside the 5-second window: rise to 401, settle at 400.
        QuoteStream(1500 * Millisecond, 100 * Millisecond, 399, 401);  // mid 400
        QuoteStream(1500 * Millisecond, 100 * Millisecond, 400, 402);  // mid 401
        QuoteStream(1500 * Millisecond, 100 * Millisecond, 399, 401);  // mid 400

        Trade(AggressorDirection.Buy, buyVolume, zonePriceTicks);
        Trade(AggressorDirection.Sell, sellVolume, zonePriceTicks);

        AdvanceMs(100);
        Quote(399, 100m, 401, 100m);
    }
}
