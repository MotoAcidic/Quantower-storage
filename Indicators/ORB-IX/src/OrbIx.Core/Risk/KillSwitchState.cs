using System;

namespace OrbIx.Core.Risk;

/// <summary>
/// Whether one guard permits trading.
///
/// Three states rather than two, and the third is the important one. A guard that cannot be
/// evaluated has NOT been satisfied, and collapsing that into "armed" is how a safety check
/// becomes decoration: the day the account equity feed goes quiet is exactly the day the
/// daily-loss guard must not report that everything is fine.
/// </summary>
public enum KillState
{
    /// <summary>Evaluated, and trading is permitted.</summary>
    Armed,

    /// <summary>Evaluated, and trading is refused.</summary>
    Tripped,

    /// <summary>
    /// Could not be evaluated — the data it needs is absent. Treated as blocking, the same
    /// way an unreadable economic calendar is.
    /// </summary>
    Unknown,
}

/// <summary>
/// How long a trip lasts.
///
/// A latching guard must never re-arm inside its own scope. The scope is the guard's, not the
/// caller's, because a caller that decides when to forgive a breach is a caller that will
/// eventually forgive one it should not have.
/// </summary>
public enum KillScope
{
    /// <summary>
    /// Re-arms when quotes resume or the condition passes. Only for conditions that are
    /// genuinely transient, where holding a latch would block trading over a hiccup.
    /// </summary>
    Transient,

    /// <summary>Holds until the next session opens.</summary>
    Session,

    /// <summary>
    /// Holds for the rest of the trading day. Account limits live here: a daily-loss breach
    /// that cleared when equity ticked back up would not be a limit at all.
    /// </summary>
    TradingDay,

    /// <summary>
    /// Holds until the engine is reloaded. For conditions that indicate a DEFECT rather than
    /// a market state — a runaway order rate, fills drifting from their intended price. A
    /// defect that clears itself is a defect that recurs unobserved.
    /// </summary>
    Process,
}

/// <summary>
/// One guard's current reading.
/// </summary>
/// <param name="Name">Which guard, for the panel and the journal.</param>
/// <param name="State">Armed, tripped, or unevaluable.</param>
/// <param name="Scope">How long a trip persists.</param>
/// <param name="Reason">
/// Why it reads as it does, in words. Always populated, including when armed — an operator
/// asking "why did it stop" and an operator asking "why did it not stop" both need an answer.
/// </param>
/// <param name="TrippedAtUtc">When it first tripped, or null.</param>
public readonly record struct KillReading(
    string Name,
    KillState State,
    KillScope Scope,
    string Reason,
    DateTime? TrippedAtUtc)
{
    /// <summary>
    /// Whether this reading refuses an entry. Unknown blocks as firmly as tripped.
    /// </summary>
    public bool Blocks => this.State is KillState.Tripped or KillState.Unknown;
}

/// <summary>
/// What the engine has observed, for the guards to judge.
///
/// Every figure is nullable and null means "not measured". That distinction is the whole
/// point: a daily loss of zero and a daily loss nobody could read are different facts, and a
/// type that cannot tell them apart guarantees they will be confused.
/// </summary>
/// <param name="UtcNow">The instant being evaluated.</param>
/// <param name="LastQuoteUtc">When a quote last arrived, or null if none ever has.</param>
/// <param name="OrdersInLastMinute">Orders routed in the trailing 60 seconds.</param>
/// <param name="MeanSlippageTicks">
/// Mean absolute difference between intended and achieved fill price, in ticks, or null when
/// no fill has been measured.
/// </param>
/// <param name="ClosedTradeRMultiples">
/// Net R per closed trade, most recent last. Costs are the caller's to subtract; a guard
/// judging gross returns would pass a system that loses money on fees.
/// </param>
/// <param name="RealisedPnl">Realised profit and loss for the trading day, or null.</param>
/// <param name="OpenPnl">Unrealised profit and loss, or null.</param>
/// <param name="Equity">Current account equity, or null.</param>
/// <param name="EquityHighWaterMark">Highest equity seen, or null.</param>
/// <param name="SessionRiskCommitted">Currency risk committed this session, or null.</param>
/// <param name="AccountEquityBase">Equity the percentage caps are taken against, or null.</param>
/// <param name="PastFlattenTime">
/// Whether the account's flatten time has passed. Null when no flatten time is configured,
/// which is a legitimate configuration rather than an unknown.
/// </param>
public readonly record struct KillObservations(
    DateTime UtcNow,
    DateTime? LastQuoteUtc = null,
    int OrdersInLastMinute = 0,
    double? MeanSlippageTicks = null,
    System.Collections.Generic.IReadOnlyList<double>? ClosedTradeRMultiples = null,
    double? RealisedPnl = null,
    double? OpenPnl = null,
    double? Equity = null,
    double? EquityHighWaterMark = null,
    double? SessionRiskCommitted = null,
    double? AccountEquityBase = null,
    bool? PastFlattenTime = null);
