using AuctionResponse.Core;

namespace AuctionResponse.Preview;

/// <summary>
/// Drives the real engine with synthetic events to produce a view worth looking at.
///
/// The data is synthetic and the preview says so: every rendered view is marked SIMULATED
/// DATA, exactly as a demo mode must be, so a screenshot of it can never be mistaken for
/// market behaviour.
/// </summary>
internal sealed class PreviewScenario
{
    public const long Second = 1_000_000_000L;
    public const long Millisecond = 1_000_000L;

    public static readonly DateTime BaseUtc = new(2026, 9, 14, 15, 0, 0, DateTimeKind.Utc);

    public static readonly SessionConfig SessionConfig = new()
    {
        Timezone = "America/Chicago",
        WindowsTimezoneEquivalent = "Central Standard Time",
        StartLocal = "08:30:00",
        EndLocalExclusive = "15:00:00"
    };

    private readonly Engine _engine;
    private long _ns = 40 * Second;
    private long _sequence;

    public PreviewScenario(bool setupComplete)
    {
        var instrument = new InstrumentKey("MNQ", "CME", setupComplete ? "202612" : "");

        var config = new Config
        {
            Instrument = new InstrumentConfig { InstrumentKey = instrument, TickSize = 0.25m },
            Session = SessionConfig,
            Baseline = new BaselineConfig { ArtifactPath = setupComplete ? "preview://baseline" : null },
            Recording = new RecordingConfig
            {
                Directory = setupComplete ? "preview://recording" : null,
                PermissionConfirmed = setupComplete,
                RequireRecorderForResearch = true
            }
        };

        _engine = new Engine(config, instrument, 0.25m, feedMode: "Preview", simulatedData: true)
        {
            Capabilities = new CapabilityFlags
            {
                Trades = setupComplete,
                Quotes = setupComplete,
                MarketByPrice = setupComplete,
                MarketByOrderVerified = false,
                MboUnverifiedReason = "snapshot fence not established"
            }
        };

        if (setupComplete) _engine.LoadBaseline(Baseline(instrument));

        _engine.AddLevel(new LevelDefinition
        {
            LevelId = "R1",
            Side = LevelSide.Resistance,
            PriceTicks = 97400,          // 24350.00 at a 0.25 tick
            EffectiveNs = 0,
            ConnectionEpoch = _engine.ConnectionEpoch,
            Source = "preview"
        });
    }

    private static BaselineArtifact Baseline(InstrumentKey instrument)
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
            Instrument = instrument,
            TickSize = 0.25m,
            SessionTimezone = "America/Chicago",
            CalendarVersion = "preview",
            ClockMode = "ReceiveElapsedWithRecordedDecisionTicks",
            FeedMode = "Preview",
            WindowsMs = new[] { 1000, 5000, 30000 },
            BucketMinutes = 30,
            EligibleSessionIds = Enumerable.Range(1, 20).Select(i => "2026-08-" + i.ToString("00")).ToArray(),
            FitEndUtc = BaseUtc.AddDays(-1),
            SourceLogHashes = new[] { "preview" },
            Sha256 = "preview-only-not-a-trained-artifact",
            Buckets = buckets
        };
    }

    public ViewSnapshot Build(bool armed)
    {
        if (armed)
        {
            QuoteStream(6 * Second, 200 * Millisecond, 97398, 97400);
            QuoteStream(1500 * Millisecond, 100 * Millisecond, 97399, 97401);
            QuoteStream(1500 * Millisecond, 100 * Millisecond, 97400, 97402);
            QuoteStream(1500 * Millisecond, 100 * Millisecond, 97399, 97401);
            Trade(AggressorDirection.Buy, 200m, 97400);
            Trade(AggressorDirection.Sell, 50m, 97400);
            _ns += 100 * Millisecond;
            Quote(97399, 100m, 97401, 100m);

            // A few ticks so the pressure-response trail has something in it.
            for (var i = 0; i < 12; i++)
            {
                Tick();
                QuoteStream(240 * Millisecond, 80 * Millisecond, 97399, 97401);
                Trade(i % 2 == 0 ? AggressorDirection.Buy : AggressorDirection.Sell, 20m + i * 3, 97400);
            }
        }
        else
        {
            QuoteStream(3 * Second, 200 * Millisecond, 97399, 97401);
        }

        return Tick().View;
    }

    private void QuoteStream(long durationNs, long intervalNs, long bid, long ask)
    {
        var end = _ns + durationNs;
        while (_ns < end)
        {
            Quote(bid, 100m, ask, 100m);
            _ns += intervalNs;
        }
    }

    private void Quote(long bidTicks, decimal bidQty, long askTicks, decimal askQty)
        => _engine.Accept(new MarketEvent
        {
            Kind = EventKind.BestQuote,
            EventSequence = ++_sequence,
            ConnectionEpoch = _engine.ConnectionEpoch,
            ReceiveElapsedNs = _ns,
            ReceiveUtc = BaseUtc.AddTicks(_ns / 100),
            BidTicks = bidTicks, BidQuantity = bidQty,
            AskTicks = askTicks, AskQuantity = askQty
        });

    private void Trade(AggressorDirection dir, decimal qty, long priceTicks)
        => _engine.Accept(new MarketEvent
        {
            Kind = EventKind.Trade,
            EventSequence = ++_sequence,
            ConnectionEpoch = _engine.ConnectionEpoch,
            ReceiveElapsedNs = _ns,
            ReceiveUtc = BaseUtc.AddTicks(_ns / 100),
            Direction = dir,
            OriginalQuantity = qty,
            PriceTicks = priceTicks
        });

    private DecisionResult Tick()
        => _engine.Decide(new DecisionTick
        {
            EventSequence = ++_sequence,
            ConnectionEpoch = _engine.ConnectionEpoch,
            ElapsedNs = _ns,
            ReceiveUtc = BaseUtc.AddTicks(_ns / 100)
        });
}
