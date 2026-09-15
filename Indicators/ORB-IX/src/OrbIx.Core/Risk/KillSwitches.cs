using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrbIx.Core.Config;

namespace OrbIx.Core.Risk;

/// <summary>
/// The account-level and feed-level guards that refuse an entry.
///
/// These sit above the per-session gates already in
/// <see cref="Sessions.SessionContext.CanEnter"/> — phase, budget, cooldown, range — and above
/// <see cref="Features.BlackoutPolicy"/>. Those answer "should this setup trade"; these answer
/// "should this ACCOUNT be trading at all right now".
///
/// Until this type existed the configured thresholds were parsed, validated, and never read:
/// `orderRatePerMin`, `slippageDriftTicks`, `staleQuoteMs` and `expectancyWindow` were a
/// settings page with nothing behind it. On a funded account that is the difference between a
/// bad day and a failed evaluation.
///
/// TWO PROPERTIES CARRY THE WHOLE DESIGN.
///
/// A trip LATCHES, and the latch lives here rather than in the caller. A guard whose caller
/// decides when to forgive a breach is a guard that will eventually forgive one it should not
/// have — and the specific failure that would make this worthless is a daily-loss trip that
/// clears because equity ticked back up.
///
/// A guard that cannot be evaluated reports <see cref="KillState.Unknown"/> and BLOCKS. The
/// day the equity feed goes quiet is exactly the day the daily-loss guard must not report that
/// everything is fine. This matches the calendar policy already shipped.
///
/// No threshold is invented here. Every one is read from configuration that already existed.
/// </summary>
public sealed class KillSwitches
{
    /// <summary>The trailing window the order-rate guard counts over.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly KillConfig config;
    private readonly RiskConfig risk;
    private readonly AccountConfig account;

    /// <summary>
    /// Guards currently latched, with when and why. Keyed by guard name; a guard absent from
    /// this map has not tripped within its scope.
    /// </summary>
    private readonly Dictionary<string, (DateTime AtUtc, string Reason, KillScope Scope)> latched =
        new(StringComparer.Ordinal);

    public KillSwitches(KillConfig config, RiskConfig risk, AccountConfig account)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.risk = risk ?? throw new ArgumentNullException(nameof(risk));
        this.account = account ?? throw new ArgumentNullException(nameof(account));
    }

    // ---- guard names, used by the panel, the journal and the tests -------------------------

    public const string StaleQuote = "StaleQuote";
    public const string OrderRate = "OrderRate";
    public const string SlippageDrift = "SlippageDrift";
    public const string Expectancy = "Expectancy";
    public const string DailyLoss = "DailyLoss";
    public const string TrailingDrawdown = "TrailingDrawdown";
    public const string FlatByTime = "FlatByTime";
    public const string SessionRiskCap = "SessionRiskCap";

    /// <summary>
    /// Evaluates every guard, applying and updating the latches.
    ///
    /// Order is stable so the panel does not reshuffle between frames.
    /// </summary>
    public IReadOnlyList<KillReading> Evaluate(in KillObservations observed)
    {
        var readings = new List<KillReading>(8)
        {
            this.Latch(this.EvaluateStaleQuote(observed)),
            this.Latch(this.EvaluateOrderRate(observed)),
            this.Latch(this.EvaluateSlippage(observed)),
            this.Latch(this.EvaluateExpectancy(observed)),
            this.Latch(this.EvaluateDailyLoss(observed)),
            this.Latch(this.EvaluateTrailingDrawdown(observed)),
            this.Latch(this.EvaluateFlatByTime(observed)),
            this.Latch(this.EvaluateSessionRiskCap(observed)),
        };

        return readings;
    }

    /// <summary>
    /// Whether any guard refuses an entry, and which.
    /// </summary>
    public bool Blocks(IReadOnlyList<KillReading> readings, out string reason)
    {
        if (readings is null)
            throw new ArgumentNullException(nameof(readings));

        var blocking = readings.Where(r => r.Blocks).ToList();

        if (blocking.Count == 0)
        {
            reason = string.Empty;
            return false;
        }

        reason = string.Join("; ", blocking.Select(r => $"{r.Name}: {r.Reason}"));
        return true;
    }

    /// <summary>
    /// Clears latches whose scope has ended.
    ///
    /// Called at a session boundary with <see cref="KillScope.Session"/>, and at a trading-day
    /// boundary with <see cref="KillScope.TradingDay"/>. <see cref="KillScope.Process"/> is
    /// never cleared here by design — reloading the engine is the only reset, because those
    /// guards indicate a defect rather than a market condition.
    /// </summary>
    /// <returns>The names cleared, so the transition can be journalled.</returns>
    public IReadOnlyList<string> ReleaseScope(KillScope scope)
    {
        if (scope == KillScope.Process)
        {
            throw new ArgumentException(
                "Process-scoped guards are released only by reloading the engine. They mark a "
                + "defect — a runaway order rate, fills drifting from their intended price — and "
                + "a defect that clears itself is a defect that recurs unobserved.",
                nameof(scope));
        }

        var cleared = this.latched
            .Where(pair => pair.Value.Scope == scope)
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        foreach (var name in cleared)
            this.latched.Remove(name);

        return cleared;
    }

    /// <summary>Whether a named guard is currently latched.</summary>
    public bool IsLatched(string name) => this.latched.ContainsKey(name);

    /// <summary>
    /// Applies the latch: a guard that has tripped stays tripped until its scope is released,
    /// whatever the fresh reading says.
    /// </summary>
    private KillReading Latch(KillReading fresh)
    {
        // Transient guards are the deliberate exception. A stale-quote latch would block
        // trading for the rest of the session over one feed hiccup, which is a different
        // failure rather than a safer one.
        if (fresh.Scope == KillScope.Transient)
        {
            this.latched.Remove(fresh.Name);
            return fresh;
        }

        if (this.latched.TryGetValue(fresh.Name, out var held))
        {
            return fresh with
            {
                State = KillState.Tripped,
                Reason = held.Reason + $" (latched at {held.AtUtc:HH:mm:ss} UTC, held for the {held.Scope.ToString().ToLowerInvariant()})",
                TrippedAtUtc = held.AtUtc,
            };
        }

        if (fresh.State == KillState.Tripped)
        {
            var at = fresh.TrippedAtUtc ?? DateTime.UtcNow;
            this.latched[fresh.Name] = (at, fresh.Reason, fresh.Scope);
        }

        return fresh;
    }

    // ---- the guards -------------------------------------------------------------------------

    private KillReading EvaluateStaleQuote(in KillObservations o)
    {
        if (o.LastQuoteUtc is not { } last)
        {
            return new KillReading(
                StaleQuote, KillState.Unknown, KillScope.Transient,
                "No quote has arrived yet, so quote age cannot be judged.", null);
        }

        var age = o.UtcNow - last;
        var limit = TimeSpan.FromMilliseconds(this.config.StaleQuoteMs);

        return age > limit
            ? new KillReading(
                StaleQuote, KillState.Tripped, KillScope.Transient,
                $"Last quote {age.TotalMilliseconds:N0}ms ago, limit {this.config.StaleQuoteMs}ms.",
                o.UtcNow)
            : new KillReading(
                StaleQuote, KillState.Armed, KillScope.Transient,
                $"Quote {age.TotalMilliseconds:N0}ms old.", null);
    }

    private KillReading EvaluateOrderRate(in KillObservations o)
    {
        // Counted over a trailing minute by the caller. Tripping latches for the process: a
        // rate breach means something is emitting orders in a loop, and resuming automatically
        // would let it resume too.
        return o.OrdersInLastMinute > this.config.OrderRatePerMin
            ? new KillReading(
                OrderRate, KillState.Tripped, KillScope.Process,
                $"{o.OrdersInLastMinute} orders in the last minute, limit {this.config.OrderRatePerMin}.",
                o.UtcNow)
            : new KillReading(
                OrderRate, KillState.Armed, KillScope.Process,
                $"{o.OrdersInLastMinute} of {this.config.OrderRatePerMin} orders per minute.", null);
    }

    private KillReading EvaluateSlippage(in KillObservations o)
    {
        if (o.MeanSlippageTicks is not { } drift)
        {
            return new KillReading(
                SlippageDrift, KillState.Armed, KillScope.Process,
                "No fill has been measured, so there is no drift to judge.", null);
        }

        return drift > this.config.SlippageDriftTicks
            ? new KillReading(
                SlippageDrift, KillState.Tripped, KillScope.Process,
                $"Mean fill drift {drift:N2} ticks, limit {this.config.SlippageDriftTicks}.",
                o.UtcNow)
            : new KillReading(
                SlippageDrift, KillState.Armed, KillScope.Process,
                $"Mean fill drift {drift:N2} of {this.config.SlippageDriftTicks} ticks.", null);
    }

    private KillReading EvaluateExpectancy(in KillObservations o)
    {
        var window = this.config.ExpectancyWindow;
        var trades = o.ClosedTradeRMultiples ?? Array.Empty<double>();

        // An untested guard must not read as a passing one. Below the window there is nothing
        // to conclude, and saying so is the honest answer — it does not block, because refusing
        // to trade until 30 trades exist would prevent the 30 trades.
        if (trades.Count < window)
        {
            return new KillReading(
                Expectancy, KillState.Armed, KillScope.Session,
                $"Insufficient sample: {trades.Count} of {window} closed trades.", null);
        }

        var recent = trades.Skip(trades.Count - window).ToList();
        var mean = recent.Average();

        // At or below zero. Exactly zero is a system that has paid its costs and returned
        // nothing, which is not a system worth risking an evaluation on.
        return mean <= 0
            ? new KillReading(
                Expectancy, KillState.Tripped, KillScope.Session,
                $"Mean {mean:N3}R over the last {window} closed trades, net of costs.",
                o.UtcNow)
            : new KillReading(
                Expectancy, KillState.Armed, KillScope.Session,
                $"Mean {mean:N3}R over the last {window} closed trades.", null);
    }

    private KillReading EvaluateDailyLoss(in KillObservations o)
    {
        if (this.account.DailyLimit is not { } limit)
        {
            return new KillReading(
                DailyLoss, KillState.Armed, KillScope.TradingDay,
                $"No daily loss limit is configured for {this.account.Id}.", null);
        }

        if (o.RealisedPnl is not { } realised)
        {
            return new KillReading(
                DailyLoss, KillState.Unknown, KillScope.TradingDay,
                "Realised profit and loss is not available, so the daily loss cannot be judged.",
                null);
        }

        // Open loss counts. A limit measured on closed trades alone is one an open position can
        // walk straight through.
        var total = realised + (o.OpenPnl ?? 0d);
        var loss = total < 0 ? -total : 0d;

        return loss >= limit
            ? new KillReading(
                DailyLoss, KillState.Tripped, KillScope.TradingDay,
                $"Day loss {loss:C} of {limit:C} limit"
                + (o.OpenPnl is { } open ? $" (realised {realised:C}, open {open:C}).": "."),
                o.UtcNow)
            : new KillReading(
                DailyLoss, KillState.Armed, KillScope.TradingDay,
                $"Day loss {loss:C} of {limit:C}.", null);
    }

    private KillReading EvaluateTrailingDrawdown(in KillObservations o)
    {
        if (this.account.TrailingLimit is not { } dd)
        {
            return new KillReading(
                TrailingDrawdown, KillState.Armed, KillScope.TradingDay,
                $"No trailing drawdown is configured for {this.account.Id}.", null);
        }

        if (o.Equity is not { } equity || o.EquityHighWaterMark is not { } peak)
        {
            return new KillReading(
                TrailingDrawdown, KillState.Unknown, KillScope.TradingDay,
                "Equity or its high-water mark is not available, so the drawdown cannot be judged.",
                null);
        }

        var behind = peak - equity;

        return behind >= dd.Amount
            ? new KillReading(
                TrailingDrawdown, KillState.Tripped, KillScope.TradingDay,
                $"{behind:C} below the {dd.Type} high-water mark {peak:C}, limit {dd.Amount:C}.",
                o.UtcNow)
            : new KillReading(
                TrailingDrawdown, KillState.Armed, KillScope.TradingDay,
                $"{behind:C} of {dd.Amount:C} below the {dd.Type} high-water mark.", null);
    }

    private KillReading EvaluateFlatByTime(in KillObservations o)
    {
        if (string.IsNullOrWhiteSpace(this.account.FlatByTime))
        {
            return new KillReading(
                FlatByTime, KillState.Armed, KillScope.TradingDay,
                $"No flatten time is configured for {this.account.Id}.", null);
        }

        if (o.PastFlattenTime is not { } past)
        {
            return new KillReading(
                FlatByTime, KillState.Unknown, KillScope.TradingDay,
                $"Whether {this.account.FlatByTime} has passed could not be determined.", null);
        }

        return past
            ? new KillReading(
                FlatByTime, KillState.Tripped, KillScope.TradingDay,
                $"Past the configured flatten time {this.account.FlatByTime}.", o.UtcNow)
            : new KillReading(
                FlatByTime, KillState.Armed, KillScope.TradingDay,
                $"Before the configured flatten time {this.account.FlatByTime}.", null);
    }

    private KillReading EvaluateSessionRiskCap(in KillObservations o)
    {
        if (o.AccountEquityBase is not { } equityBase || equityBase <= 0)
        {
            return new KillReading(
                SessionRiskCap, KillState.Unknown, KillScope.Session,
                "Account equity is not available, so the session risk cap cannot be judged.",
                null);
        }

        if (o.SessionRiskCommitted is not { } committed)
        {
            return new KillReading(
                SessionRiskCap, KillState.Unknown, KillScope.Session,
                "Committed session risk is not available, so the cap cannot be judged.", null);
        }

        var cap = equityBase * this.risk.SessionRiskCapPct;

        return committed >= cap
            ? new KillReading(
                SessionRiskCap, KillState.Tripped, KillScope.Session,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Session risk {0:C} of {1:C} cap ({2:P2} of {3:C}).",
                    committed, cap, this.risk.SessionRiskCapPct, equityBase),
                o.UtcNow)
            : new KillReading(
                SessionRiskCap, KillState.Armed, KillScope.Session,
                string.Format(
                    CultureInfo.CurrentCulture, "Session risk {0:C} of {1:C} cap.", committed, cap),
                null);
    }
}
