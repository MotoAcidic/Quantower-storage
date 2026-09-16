using AuctionResponse.Core;
using AuctionResponse.Replay;

namespace AuctionResponse.Tests;

/// <summary>
/// Section 19 replay tests: identical transitions, features and ids on repeated playback,
/// at different playback speeds and render frequencies; appended future events must leave
/// prior output byte-equivalent; duplicate price/size/time trades stay distinct; snapshots
/// contribute no flow.
/// </summary>
public static class ReplayTests
{
    public static void Run()
    {
        Harness.Section("Section 19 - recording and replay");

        RoundTripSerialization();
        DeterministicReplay();
        RenderFrequencyDoesNotChangeSignals();
        AppendingFutureEventsPreservesPriorOutput();
        DuplicateTradesStayDistinct();
        SnapshotContributesNoFlow();
        IncompleteTailIsRecoverable();
    }

    private static (IReadOnlyList<object> Records, Scenario Source) RecordCanonicalRun()
    {
        var s = new Scenario();
        s.DeclareResistance(400);
        s.ApproachResistance();
        s.Tick();

        s.AdvanceMs(200);
        s.Quote(395, 100m, 397, 100m);
        s.Trade(AggressorDirection.Sell, 400m, 396);
        s.AdvanceMs(1100);
        s.Quote(395, 100m, 397, 100m);
        s.Tick();

        // Interleave events and ticks back into one ingress-ordered stream.
        var records = s.EventLog.Cast<object>()
            .Concat(s.TickLog.Cast<object>())
            .OrderBy(o => o is MarketEvent e ? e.EventSequence : ((DecisionTick)o).EventSequence)
            .ToList();

        return (records, s);
    }

    private static Engine FreshEngine()
    {
        var scenario = new Scenario();
        scenario.DeclareResistance(400);
        return scenario.Engine;
    }

    private static void RoundTripSerialization()
    {
        var (records, _) = RecordCanonicalRun();

        var mismatches = 0;
        foreach (var record in records)
        {
            if (record is MarketEvent e)
            {
                var line = Jsonl.Serialize(e);
                var back = Jsonl.DeserializeEvent(line);
                if (Jsonl.Serialize(back) != line) mismatches++;
            }
            else if (record is DecisionTick t)
            {
                var line = Jsonl.Serialize(t);
                if (Jsonl.Serialize(Jsonl.DeserializeTick(line)) != line) mismatches++;
            }
        }

        Harness.Equal("serialization: every record round-trips byte-for-byte", 0, mismatches);

        // Decimals must survive as decimals. 0.1 + 0.2 is the canonical way to prove it.
        var priced = new MarketEvent
        {
            Kind = EventKind.Trade,
            OriginalPrice = 24350.25m,
            OriginalQuantity = 0.3m,
            PriceTicks = 97401,
            EventSequence = 1,
            ReceiveUtc = Scenario.SessionBaseUtc
        };
        var restored = Jsonl.DeserializeEvent(Jsonl.Serialize(priced));
        Harness.Equal("serialization: decimal price is exact", 24350.25m, restored.OriginalPrice ?? 0m);
        Harness.Equal("serialization: decimal quantity is exact", 0.3m, restored.OriginalQuantity ?? 0m);

        // 64-bit ids go out as strings so a JSON consumer cannot round them.
        var withId = priced with { OrderId = "9007199254740993" };
        Harness.Check("serialization: large order ids survive",
            Jsonl.DeserializeEvent(Jsonl.Serialize(withId)).OrderId == "9007199254740993",
            "an id beyond 2^53 must not be written as a JSON number");
    }

    private static void DeterministicReplay()
    {
        var (records, _) = RecordCanonicalRun();

        var first = new ReplayRunner(FreshEngine()).Run(records);
        var second = new ReplayRunner(FreshEngine()).Run(records);

        var a = ReplayRunner.SemanticDigest(first.Transitions);
        var b = ReplayRunner.SemanticDigest(second.Transitions);

        Harness.Check("replay: repeated playback produces identical transitions", a == b,
            "digests differed");
        Harness.Check("replay: the run actually produced transitions", first.Transitions.Count > 0,
            "a replay that produces nothing proves nothing");
        Harness.Check("replay: candidate ids are deterministic, not random",
            first.Transitions.Select(t => t.CandidateId).SequenceEqual(second.Transitions.Select(t => t.CandidateId)));
    }

    private static void RenderFrequencyDoesNotChangeSignals()
    {
        var (records, _) = RecordCanonicalRun();

        var everyTick = new ReplayRunner(FreshEngine()).Run(records, renderEvery: 1);
        var rarely = new ReplayRunner(FreshEngine()).Run(records, renderEvery: 7);

        Harness.Check("replay: render frequency does not change transitions",
            ReplayRunner.SemanticDigest(everyTick.Transitions) == ReplayRunner.SemanticDigest(rarely.Transitions));
        Harness.Check("replay: fewer renders really were captured",
            rarely.Views.Count < everyTick.Views.Count,
            "the test would be vacuous if both captured the same number of views");
    }

    private static void AppendingFutureEventsPreservesPriorOutput()
    {
        var (records, _) = RecordCanonicalRun();

        var prior = new ReplayRunner(FreshEngine()).Run(records);
        var priorDigest = ReplayRunner.SemanticDigest(prior.Transitions);

        // Append events that occur strictly after everything already recorded.
        var lastSeq = records.Max(o => o is MarketEvent e ? e.EventSequence : ((DecisionTick)o).EventSequence);
        var lastNs = records.Max(o => o is MarketEvent e ? e.ReceiveElapsedNs : ((DecisionTick)o).ElapsedNs);
        var extended = records.ToList();

        for (var i = 1; i <= 10; i++)
        {
            var ns = lastNs + i * 100 * Scenario.Millisecond;
            extended.Add(new MarketEvent
            {
                Kind = EventKind.BestQuote,
                EventSequence = lastSeq + i,
                ConnectionEpoch = 1,
                ReceiveElapsedNs = ns,
                ReceiveUtc = Scenario.SessionBaseUtc.AddTicks(ns / 100),
                BidTicks = 395, BidQuantity = 100m, AskTicks = 397, AskQuantity = 100m
            });
        }

        var after = new ReplayRunner(FreshEngine()).Run(extended);
        var afterPrefix = ReplayRunner.SemanticDigest(after.Transitions.Take(prior.Transitions.Count));

        Harness.Check("replay: appending future events leaves prior output byte-equivalent",
            afterPrefix == priorDigest,
            "a later event rewrote an earlier decision, which is repainting");
    }

    private static void DuplicateTradesStayDistinct()
    {
        // Identical price, size and time, with no unique source id. These are genuinely
        // different fills and must NOT be collapsed.
        var w = new TradeWindow(5000);
        w.Add(1000, AggressorDirection.Buy, 5m, 400);
        w.Add(1000, AggressorDirection.Buy, 5m, 400);
        w.Add(1000, AggressorDirection.Buy, 5m, 400);

        Harness.Equal("dedup: identical trades without ids stay distinct", 15m, w.Buy);
        Harness.Equal("dedup: all three are retained", 3, w.Count);
    }

    private static void SnapshotContributesNoFlow()
    {
        var flow = new BookFlowWindow(5000);

        var initial = new QuoteState(0, 400, 402, 100m, 80m, 0);
        flow.Accept(initial, contributesFlow: false);
        flow.Advance(1000 * Scenario.Millisecond);
        Harness.Equal("snapshot: initialising state adds no OFI", 0m, flow.OfiSum);

        // The first real change after the snapshot still measures against it correctly.
        var changed = new QuoteState(500 * Scenario.Millisecond, 400, 402, 130m, 60m, 1);
        flow.Accept(changed);
        flow.Advance(600 * Scenario.Millisecond);
        Harness.Equal("snapshot: the first real change is measured normally", 50m, flow.OfiSum);

        // A snapshot arriving mid-stream also must not be double-counted as new liquidity.
        var s = new Scenario();
        s.QuoteStream(1 * Scenario.Second, 100 * Scenario.Millisecond, 399, 401);
        var before = s.Tick().View.Live.Ofi.Value;
        s.Quote(399, 100m, 401, 100m, QualityFlag.SnapshotInitialisation);
        var after = s.Tick().View.Live.Ofi.Value;
        Harness.Equal("snapshot: a re-snapshot of the same book adds no flow", before ?? 0d, after);
    }

    private static void IncompleteTailIsRecoverable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "auction-response-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "events.jsonl");
            var good = Jsonl.Serialize(new MarketEvent
            {
                Kind = EventKind.Trade, EventSequence = 1, ConnectionEpoch = 1,
                ReceiveUtc = Scenario.SessionBaseUtc, ReceiveElapsedNs = 1,
                OriginalQuantity = 1m, PriceTicks = 400
            });
            File.WriteAllLines(path, new[] { good, good, "{\"schemaVersion\":\"1.0.0\",\"engineVer" });

            var result = LogReader.ReadEvents(path);
            Harness.Equal("incomplete tail: complete records are read", 2, result.Records.Count);
            Harness.Check("incomplete tail: reported, not silently accepted", result.HasIncompleteTail);
            Harness.Check("incomplete tail: the partial text is preserved for diagnosis",
                !string.IsNullOrEmpty(result.IncompleteTailText));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
