using System;
using System.Collections.Generic;
using OrbIx.Core.Execution;

namespace OrbIx.Core.Abstractions;

/// <summary>What actually happened to an order.</summary>
public enum RoutingDisposition
{
    /// <summary>
    /// Built and described but deliberately not sent. This is what Armed mode produces,
    /// and it is a successful outcome rather than a failure.
    /// </summary>
    Staged,

    /// <summary>Accepted by the broker.</summary>
    Sent,

    /// <summary>Not sent, and why.</summary>
    Refused,
}

/// <summary>
/// The result of routing one instrument's order.
/// </summary>
/// <param name="Disposition">Staged, sent, or refused.</param>
/// <param name="Instrument">Which contract.</param>
/// <param name="Description">
/// The exact request, rendered in full. Present for a staged order as much as for a sent
/// one: the whole point of staging is that a human can read precisely what would go.
/// </param>
/// <param name="BrokerOrderId">Set only when the broker accepted it.</param>
/// <param name="Error">Set only on refusal.</param>
public sealed record RoutedOrder(
    RoutingDisposition Disposition,
    string Instrument,
    string Description,
    string? BrokerOrderId,
    string? Error);

/// <summary>
/// Sends orders to a broker, or stages them for a human.
///
/// The <c>send</c> argument is required at every call site rather than being a property of
/// the router. A router configured once as "live" and then called from several places is
/// one refactor away from sending something nobody meant to send; making the intent
/// explicit per call means the decision is visible in the code that makes it.
///
/// Implementations must not send when <c>send</c> is false, whatever their configuration.
/// </summary>
public interface IOrderRouter
{
    /// <summary>Whether the router could send at all — connected, account resolved, permitted.</summary>
    bool CanRoute { get; }

    /// <summary>Why <see cref="CanRoute"/> is what it is. Shown to the operator.</summary>
    string Status { get; }

    /// <summary>
    /// Routes one order.
    /// </summary>
    /// <param name="order">The order and its bracket.</param>
    /// <param name="send">
    /// True to transmit. False to build and describe it without transmitting, which is what
    /// every mode below Auto does.
    /// </param>
    RoutedOrder Route(InstrumentOrder order, bool send);

    /// <summary>
    /// Flattens everything this router is responsible for, unconditionally.
    ///
    /// Separate from <see cref="Route"/> and deliberately without a <c>send</c> flag: a
    /// kill-switch that could be staged is not a kill-switch. Callers reach this only when
    /// the position must go regardless of mode.
    /// </summary>
    IReadOnlyList<RoutedOrder> FlattenAll(string reason);
}
