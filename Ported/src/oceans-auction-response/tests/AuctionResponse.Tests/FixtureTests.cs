using System.Globalization;
using System.Text.Json;
using AuctionResponse.Core;

namespace AuctionResponse.Tests;

/// <summary>
/// Every numerical fixture in TEST-VECTORS.json, driven from the file itself rather than
/// retyped, so a fixture cannot drift away from the specification unnoticed.
///
/// These are arithmetic and lifecycle checks. They are NOT performance evidence.
/// </summary>
public static class FixtureTests
{
    public static void Run(string vectorsPath)
    {
        Harness.Section("Numerical fixtures (TEST-VECTORS.json)");

        using var doc = JsonDocument.Parse(File.ReadAllText(vectorsPath));
        var root = doc.RootElement;
        var tolerance = root.GetProperty("floatTolerance").GetDouble();
        var cases = root.GetProperty("cases");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases.EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            seen.Add(id);
            var input = c.GetProperty("input");
            var expected = c.GetProperty("expected");
            RunCase(id, c.GetProperty("function").GetString()!, input, expected, tolerance);
        }

        // A fixture silently skipped is a fixture that proves nothing.
        var required = new[]
        {
            "trade_sums", "queue_imbalance", "empty_book", "ofi_same_prices", "ofi_bid_up", "ofi_bid_down",
            "ofi_ask_down", "ofi_ask_up", "net_addition", "net_removal", "unknown_execution", "plot_sign",
            "nearest_rank", "residual", "window_boundary", "resistance_candidate", "support_candidate",
            "confirm_at_deadline", "confirm_too_late", "health_precedence", "survival_two_steps"
        };
        foreach (var r in required)
            Harness.Check("fixture present: " + r, seen.Contains(r), "TEST-VECTORS.json is missing this case");
    }

    private static void RunCase(string id, string function, JsonElement input, JsonElement expected, double tol)
    {
        switch (function)
        {
            case "TradeWindow": TradeSums(id, input, expected, tol); break;
            case "QueueImbalance": Imbalance(id, input, expected, tol); break;
            case "BestQuoteOFI": Ofi(id, input, expected); break;
            case "NetDisplayedAddition": NetAddition(id, input, expected); break;
            case "PlotCoordinates": Plot(id, input, expected, tol); break;
            case "Quantile": Quantile(id, input, expected, tol); break;
            case "Residual": Residual(id, input, expected, tol); break;
            case "WindowMembership": WindowBoundary(id, input, expected); break;
            case "CandidateFromFeatures": Candidate(id, input, expected); break;
            case "TerminalTransition": Terminal(id, input, expected); break;
            case "CompetingRisks": Survival(id, input, expected, tol); break;
            default: Harness.Check(id, false, "no implementation bound to function " + function); break;
        }
    }

    private static void TradeSums(string id, JsonElement input, JsonElement expected, double tol)
    {
        var w = new TradeWindow(5000);
        w.Add(1, AggressorDirection.Buy, input.GetProperty("buy").GetDecimal(), 400);
        w.Add(2, AggressorDirection.Sell, input.GetProperty("sell").GetDecimal(), 400);
        w.Add(3, AggressorDirection.Unknown, input.GetProperty("unknown").GetDecimal(), 400);

        Harness.Equal(id + ": delta", expected.GetProperty("delta").GetDecimal(), w.Delta);
        Harness.Equal(id + ": normalized delta", expected.GetProperty("normalizedDelta").GetDouble(), w.NormalizedDelta(3).Value, tol);
        Harness.Equal(id + ": side quality", expected.GetProperty("knownSideFraction").GetDouble(), w.SideQuality(3).Value, tol);

        // Unknown volume counts toward total and side quality, never toward signed flow.
        Harness.Equal(id + ": unknown excluded from delta", 40m, w.Delta);
        Harness.Equal(id + ": unknown included in total", 210m, w.Total);
    }

    private static void Imbalance(string id, JsonElement input, JsonElement expected, double tol)
    {
        var m = BookFlow.QueueImbalance(
            input.GetProperty("bidSize").GetDecimal(),
            input.GetProperty("askSize").GetDecimal(), 0);

        var available = expected.GetProperty("available").GetBoolean();
        Harness.Equal(id + ": availability", available, m.IsAvailable);

        if (available) Harness.Equal(id + ": value", expected.GetProperty("value").GetDouble(), m.Value, tol);
        else
        {
            Harness.Null(id + ": value is null, NOT zero", m.Value);
            Harness.Check(id + ": carries a reason", !string.IsNullOrEmpty(m.Reason), "unavailable measures must say why");
        }
    }

    private static void Ofi(string id, JsonElement input, JsonElement expected)
    {
        var o = input.GetProperty("old");
        var n = input.GetProperty("new");
        var e = BookFlow.Increment(
            o.GetProperty("bid").GetInt64(), o.GetProperty("bidSize").GetDecimal(),
            o.GetProperty("ask").GetInt64(), o.GetProperty("askSize").GetDecimal(),
            n.GetProperty("bid").GetInt64(), n.GetProperty("bidSize").GetDecimal(),
            n.GetProperty("ask").GetInt64(), n.GetProperty("askSize").GetDecimal());

        Harness.Equal(id + ": e", expected.GetProperty("value").GetDecimal(), e);
    }

    private static void NetAddition(string id, JsonElement input, JsonElement expected)
    {
        decimal? executed = input.GetProperty("executed").ValueKind == JsonValueKind.Null
            ? null : input.GetProperty("executed").GetDecimal();

        var g = NetDisplayedAddition.Compute(
            input.GetProperty("before").GetDecimal(),
            input.GetProperty("after").GetDecimal(),
            executed,
            input.GetProperty("complete").GetBoolean());

        CompareNullableDecimal(id + ": net addition", expected.GetProperty("netAddition"), g.NetAddition);
        CompareNullableDecimal(id + ": addition lower bound", expected.GetProperty("additionLowerBound"), g.AdditionLowerBound);
        CompareNullableDecimal(id + ": removal lower bound", expected.GetProperty("removalLowerBound"), g.RemovalLowerBound);
    }

    private static void CompareNullableDecimal(string name, JsonElement expected, decimal? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
            Harness.Check(name + " is null", actual is null, actual is null ? "" : "expected null, got " + actual);
        else
            Harness.Equal(name, expected.GetDecimal(), actual ?? decimal.MinValue);
    }

    private static void Plot(string id, JsonElement input, JsonElement expected, double tol)
    {
        var p = PlotMath.Coordinates(
            input.GetProperty("delta").GetDouble(), input.GetProperty("deltaScale").GetDouble(),
            input.GetProperty("response").GetDouble(), input.GetProperty("responseScale").GetDouble(), 0);

        Harness.Equal(id + ": x", expected.GetProperty("x").GetDouble(), p.X, tol);
        Harness.Equal(id + ": y", expected.GetProperty("y").GetDouble(), p.Y, tol);
        Harness.Check(id + ": sign preserved", Math.Sign(p.X) == 1 && Math.Sign(p.Y) == -1,
            "buying with a downward response must land in the lower-right quadrant");
    }

    private static void Quantile(string id, JsonElement input, JsonElement expected, double tol)
    {
        var values = input.GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Array.Sort(values);
        var q = Statistics.Quantile(values, input.GetProperty("p").GetDouble());
        Harness.Equal(id + ": nearest-rank quantile", expected.GetProperty("value").GetDouble(), q, tol);

        // One-based nearest rank: ceil(pN). p=1.0 must land on the last element, not past it.
        Harness.Equal(id + ": p=1 is the maximum", 10d, Statistics.Quantile(values, 1.0), tol);
        Harness.Throws<ArgumentOutOfRangeException>(id + ": p=0 is undefined", () => Statistics.Quantile(values, 0d));
    }

    private static void Residual(string id, JsonElement input, JsonElement expected, double tol)
    {
        var (eps, z, evidence) = ResponseResidual.Evaluate(
            input.GetProperty("actual").GetDouble(),
            input.GetProperty("predicted").GetDouble(),
            input.GetProperty("validationCenter").GetDouble(),
            input.GetProperty("validationScale").GetDouble(),
            input.GetProperty("orientation").GetInt32());

        Harness.Equal(id + ": residual", expected.GetProperty("residual").GetDouble(), eps, tol);
        Harness.Equal(id + ": z residual", expected.GetProperty("zResidual").GetDouble(), z, tol);
        Harness.Equal(id + ": resistance evidence", expected.GetProperty("resistanceEvidence").GetDouble(), evidence, tol);
    }

    private static void WindowBoundary(string id, JsonElement input, JsonElement expected)
    {
        var nowMs = input.GetProperty("nowMs").GetInt64();
        var windowMs = input.GetProperty("windowMs").GetInt32();
        var events = input.GetProperty("eventsMs").EnumerateArray().Select(v => v.GetInt64()).ToArray();

        var w = new TradeWindow(windowMs);
        for (var i = 0; i < events.Length; i++)
            if (events[i] <= nowMs)   // events after the tick are not yet available to it
                w.Add(events[i] * 1_000_000L, AggressorDirection.Buy, 1m, 400);
        w.Advance(nowMs * 1_000_000L);

        var expectedIndices = expected.GetProperty("includedEventIndices").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        Harness.Equal(id + ": included count", (decimal)expectedIndices.Length, w.Total);

        // Left-open, right-closed: t-W is excluded, t is included.
        var boundary = new TradeWindow(windowMs);
        boundary.Add(5000L * 1_000_000L, AggressorDirection.Buy, 1m, 400);
        boundary.Advance(nowMs * 1_000_000L);
        Harness.Equal(id + ": event exactly at t-W is excluded", 0m, boundary.Total);

        var inclusive = new TradeWindow(windowMs);
        inclusive.Add(nowMs * 1_000_000L, AggressorDirection.Buy, 1m, 400);
        inclusive.Advance(nowMs * 1_000_000L);
        Harness.Equal(id + ": event exactly at t is included", 1m, inclusive.Total);
    }

    private static void Candidate(string id, JsonElement input, JsonElement expected)
    {
        var o = input.GetProperty("orientation").GetInt32();
        var level = input.GetProperty("levelTicks").GetInt64();
        var cfg = new CandidateConfig();
        var (zoneLow, zoneHigh) = (level - cfg.ZoneHalfWidthTicks, level + cfg.ZoneHalfWidthTicks);

        var startMid = input.GetProperty("startMid").GetInt64();
        var minMid = input.GetProperty("minimumMid").GetInt64();
        var maxMid = input.GetProperty("maximumMid").GetInt64();
        var endMid = input.GetProperty("endMid").GetInt64();
        var buy = input.GetProperty("buy").GetDecimal();
        var sell = input.GetProperty("sell").GetDecimal();
        var unknown = input.GetProperty("unknown").GetDecimal();

        var orientedMinHalf = o > 0 ? 2 * minMid : -2 * maxMid;

        var inputs = new CandidateInputs
        {
            Orientation = o,
            LevelTicks = level,
            ZoneLowTicks = zoneLow,
            ZoneHighTicks = zoneHigh,
            MidHalfTicks = 2 * endMid,
            AttackerVolume = o > 0 ? buy : sell,
            AttackerVolumeQuantile = input.GetProperty("attackerQ90").GetDouble(),
            Delta = buy - sell,
            OrientedProgressHalfTicks = o * 2 * (endMid - startMid),
            OrientedExcursionHalfTicks = o > 0 ? 2 * (maxMid - startMid) : 2 * (startMid - minMid),
            ZoneAttackerVolume = input.GetProperty("zoneAttackVolume").GetDecimal(),
            OrientedMinimumHalfTicks = orientedMinHalf,
            DataHealthy = true,
            SessionEligible = true,
            LevelExistedBeforeWindow = true,
            LifecycleClear = true,
            SideQuality = Measure.Of((double)((buy + sell) / (buy + sell + unknown)), "fraction", 5000, 0)
        };

        var evaluation = CandidateRule.Evaluate(inputs, cfg, 0.95);
        Harness.Equal(id + ": arms", expected.GetProperty("arms").GetBoolean(), evaluation.Arms);
        if (!evaluation.Arms) Harness.Check(id + ": gate detail", false, evaluation.FailureSummary);

        var b = CandidateRule.Boundaries(o, orientedMinHalf, zoneLow, zoneHigh,
            cfg.ConfirmationBufferTicks, cfg.FailureBeyondZoneTicks);

        Harness.Equal(id + ": oriented confirmation", expected.GetProperty("orientedConfirmation").GetDouble(), b.ConfirmationTicks);
        Harness.Equal(id + ": oriented failure", expected.GetProperty("orientedFailure").GetDouble(), b.FailureTicks);
        Harness.Equal(id + ": actual confirmation", expected.GetProperty("actualConfirmation").GetDouble(), b.ActualConfirmationTicks);
        Harness.Equal(id + ": actual failure", expected.GetProperty("actualFailure").GetDouble(), b.ActualFailureTicks);
        Harness.Check(id + ": boundaries disjoint", b.AreDisjoint, "confirmation and failure must not overlap");

        // Section 18: the mirror exists to catch sign errors, so assert the derived values too.
        var expectedProgress = 1d;
        Harness.Equal(id + ": r_o", expectedProgress, inputs.OrientedProgressHalfTicks!.Value / 2d);
        Harness.Equal(id + ": M_o", 2d, inputs.OrientedExcursionHalfTicks!.Value / 2d);
        Harness.Equal(id + ": zone fraction", 0.5d, (double)(inputs.ZoneAttackerVolume!.Value / inputs.AttackerVolume!.Value));
    }

    private static void Terminal(string id, JsonElement input, JsonElement expected)
    {
        var inputs = new TransitionInputs(
            AgeMs: input.GetProperty("ageMs").GetInt64(),
            Healthy: input.GetProperty("healthy").GetBoolean(),
            SessionEligible: input.GetProperty("sessionEligible").GetBoolean(),
            LevelStillDeclared: true,
            ConfigurationUnchanged: true,
            FailureDwellComplete: input.GetProperty("failureDwellComplete").GetBoolean(),
            ConfirmationDwellComplete: input.GetProperty("confirmationDwellComplete").GetBoolean(),
            OppositeFlow: input.GetProperty("oppositeFlow").GetBoolean());

        var decision = TerminalTransition.Resolve(inputs, 30000);
        Harness.Equal(id + ": state", expected.GetProperty("state").GetString()!, decision.State.ToString());
    }

    private static void Survival(string id, JsonElement input, JsonElement expected, double tol)
    {
        var hb = input.GetProperty("breakHazards").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var hr = input.GetProperty("rejectHazards").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var (b, r, s) = CompetingRisks.Accumulate(hb, hr);

        Harness.Equal(id + ": break probability", expected.GetProperty("breakProbability").GetDouble(), b, tol);
        Harness.Equal(id + ": reject probability", expected.GetProperty("rejectProbability").GetDouble(), r, tol);
        Harness.Equal(id + ": survival probability", expected.GetProperty("survivalProbability").GetDouble(), s, tol);
        Harness.Equal(id + ": the three sum to one", 1d, b + r + s, tol);
    }
}
