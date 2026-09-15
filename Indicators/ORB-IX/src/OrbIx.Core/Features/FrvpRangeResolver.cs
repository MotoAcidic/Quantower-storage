using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Features;

/// <summary>Which input actually supplied the fixed-range profile's range.</summary>
public enum FrvpRangeSource
{
    /// <summary>Nothing supplied one. <see cref="FrvpRange.Failure"/> says which step ran out.</summary>
    None,

    /// <summary>One committed chart click, anchoring the range to now.</summary>
    Clicks,

    /// <summary>A typed UTC start, running to now.</summary>
    TypedTimes,

    /// <summary>Neither was given, so the most recent session open was used.</summary>
    SessionDefault,
}

/// <summary>
/// Why no range could be established. Each value is a DIFFERENT fault with a different
/// fix, which is the whole point of not collapsing them into one message: "no range"
/// told the operator nothing about whether to type a time, check the product root, or
/// wait for a session to open.
/// </summary>
public enum FrvpRangeFailure
{
    /// <summary>A range was established.</summary>
    None,

    /// <summary>A start time was typed but could not be parsed as a UTC instant.</summary>
    TypedStartUnparsable,

    /// <summary>An end time was typed but could not be parsed as a UTC instant.</summary>
    TypedEndUnparsable,

    /// <summary>The typed pair parsed but the end is not after the start.</summary>
    TypedRangeNotAscending,

    /// <summary>The session clock has not been built yet, so no default exists.</summary>
    NoSessionClock,

    /// <summary>
    /// The product root is unknown, so sessions cannot be selected for it. Asking the
    /// clock anyway throws — <c>WindowsAround</c> rejects a blank root by contract.
    /// </summary>
    ProductRootUnknown,

    /// <summary>The clock holds no enabled session definition applying to this product.</summary>
    NoSessionApplies,

    /// <summary>Definitions apply, but none of them has opened at or before this instant.</summary>
    NoSessionOpenedYet,

    /// <summary>
    /// A CLOSED range was asked for, and this connector cannot serve one.
    ///
    /// Measured 2026-08-28: Quantower on one data vendor supplies per-price volume levels
    /// for no charted period (declares "1 - Minute", delivers 0 covered), and
    /// refuses tick computation outright — "Volume analysis calculation from ticks
    /// history is not allowed for one data vendor". The only remaining source is our own
    /// live accumulation, which is ONE running engine with no per-tick times and
    /// can therefore describe exactly one window: [liveSince, now]. A range with a
    /// fixed end lies outside what any available source can answer.
    /// </summary>
    ClosedRangeUnavailable,
}

/// <summary>
/// What the shell could observe about session windows at the instant it asked. Modelled
/// explicitly because "no windows" and "could not ask" are different faults, and a null
/// or empty list cannot tell them apart.
/// </summary>
public enum SessionWindowAvailability
{
    /// <summary>The engine, and therefore the clock, has not been constructed yet.</summary>
    ClockNotBuilt,

    /// <summary>The instrument root is blank, so the clock cannot be queried at all.</summary>
    ProductRootUnknown,

    /// <summary>The clock answered; the accompanying list is what it returned.</summary>
    Available,
}

/// <summary>A resolved fixed-range window, or the reason there isn't one.</summary>
/// <param name="StartUtc">Inclusive start; <c>default</c> when unresolved.</param>
/// <param name="EndUtc">Exclusive end; <c>default</c> when the range runs to now.</param>
/// <param name="Source">Which input supplied it.</param>
/// <param name="Failure">Why nothing did, when <see cref="Source"/> is <see cref="FrvpRangeSource.None"/>.</param>
public readonly record struct FrvpRange(
    DateTime StartUtc, DateTime EndUtc, FrvpRangeSource Source, FrvpRangeFailure Failure)
{
    /// <summary>True when a usable start instant was established.</summary>
    public bool Resolved => this.Source != FrvpRangeSource.None;
}

/// <summary>
/// Resolves the fixed-range profile's window from the three inputs that can supply one.
///
/// WHY THIS IS NOT IN THE INDICATOR. §11 puts every decision in OrbIx.Core, and this is
/// the decision that shipped wrong: the profile was enabled by default with no range, so
/// an untouched chart drew a failure line on every attach, for every user, forever. The
/// rule below is what makes an untouched chart draw a profile instead — and being here,
/// it is provable without a running platform.
///
/// PRECEDENCE, most specific first: a committed click beats typed times, and typed times
/// beat the session default. A deliberate act always outranks an inferred one.
///
/// EVERY RANGE IS OPEN-ENDED — [start, now]. A closed window cannot be served on this
/// connector by any source, so asking for one is refused BY NAME rather than answered
/// with an empty profile (FrvpRangeFailure.ClosedRangeUnavailable).
/// </summary>
public static class FrvpRangeResolver
{
    /// <summary>
    /// Picks the window. <paramref name="sessionOpensUtc"/> is scanned for the latest
    /// open at or before <paramref name="nowUtc"/> rather than indexed from the end, so
    /// the result does not depend on the caller having sorted it.
    /// </summary>
    /// <param name="clickStartUtc">The committed click, or <c>default</c>.</param>
    /// <param name="typedStartUtc">The typed start input, possibly empty.</param>
    /// <param name="typedEndUtc">The typed end input, possibly empty.</param>
    /// <param name="availability">Whether the clock could be asked at all.</param>
    /// <param name="sessionOpensUtc">Session opens the clock returned; order irrelevant.</param>
    /// <param name="nowUtc">The instant being resolved for.</param>
    public static FrvpRange Resolve(
        DateTime clickStartUtc,
        string typedStartUtc,
        string typedEndUtc,
        SessionWindowAvailability availability,
        IReadOnlyList<DateTime> sessionOpensUtc,
        DateTime nowUtc)
    {
        // 1. A committed click is an explicit act and outranks everything.
        //
        // ONE CLICK, NOT TWO, AND THE RANGE IS OPEN-ENDED. A click pair would
        // define a CLOSED range, which no available source can serve here (see
        // ClosedRangeUnavailable). One click anchors the profile at that bar and
        // runs it to now — the same shape the anchored profile already uses, and
        // the only shape the live accumulation can answer.
        if (clickStartUtc != default)
            return new FrvpRange(clickStartUtc, default, FrvpRangeSource.Clicks,
                                 FrvpRangeFailure.None);

        // 2. Typed times. A value that was typed and cannot be read is an error to
        //    report, never a reason to silently fall through to the default — the
        //    operator asked for something specific and would never learn it was ignored.
        var hasTypedStart = !string.IsNullOrWhiteSpace(typedStartUtc);
        var hasTypedEnd = !string.IsNullOrWhiteSpace(typedEndUtc);

        if (hasTypedStart || hasTypedEnd)
        {
            if (!hasTypedStart || !TryParseUtc(typedStartUtc, out var parsedStart))
                return Unresolved(FrvpRangeFailure.TypedStartUnparsable);

            // An end is optional: a typed start alone runs to now, which is what an
            // operator watching the current session means by it.
            if (!hasTypedEnd)
                return new FrvpRange(parsedStart, default, FrvpRangeSource.TypedTimes, FrvpRangeFailure.None);

            if (!TryParseUtc(typedEndUtc, out var parsedEnd))
                return Unresolved(FrvpRangeFailure.TypedEndUnparsable);

            if (parsedEnd <= parsedStart)
                return Unresolved(FrvpRangeFailure.TypedRangeNotAscending);

            // The times parse and ascend, and the range is still refused —
            // because a CLOSED window is not answerable on this connector at
            // all. Refusing here, by name, is why the operator sees the reason
            // rather than an empty profile.
            return Unresolved(FrvpRangeFailure.ClosedRangeUnavailable);
        }

        // 3. The session default: the most recent open at or before now, running to now.
        //    It re-resolves on every rebuild, so it rolls into the next session on its
        //    own rather than going stale at a boundary.
        switch (availability)
        {
            case SessionWindowAvailability.ClockNotBuilt:
                return Unresolved(FrvpRangeFailure.NoSessionClock);
            case SessionWindowAvailability.ProductRootUnknown:
                return Unresolved(FrvpRangeFailure.ProductRootUnknown);
        }

        if (sessionOpensUtc is null || sessionOpensUtc.Count == 0)
            return Unresolved(FrvpRangeFailure.NoSessionApplies);

        var latest = default(DateTime);
        foreach (var open in sessionOpensUtc)
        {
            if (open <= nowUtc && open > latest)
                latest = open;
        }

        if (latest == default)
            return Unresolved(FrvpRangeFailure.NoSessionOpenedYet);

        return new FrvpRange(latest, default, FrvpRangeSource.SessionDefault, FrvpRangeFailure.None);
    }

    /// <summary>Human wording for a failure, used by the problems line and the log.</summary>
    public static string Describe(FrvpRangeFailure failure, bool clickSelectEnabled) => failure switch
    {
        FrvpRangeFailure.None => string.Empty,
        FrvpRangeFailure.TypedStartUnparsable =>
            "start time is not a readable UTC instant (expected yyyy-MM-ddTHH:mm)",
        FrvpRangeFailure.TypedEndUnparsable =>
            "end time is not a readable UTC instant (expected yyyy-MM-ddTHH:mm)",
        FrvpRangeFailure.TypedRangeNotAscending =>
            "end time is not after the start time",
        FrvpRangeFailure.ClosedRangeUnavailable =>
            "a closed range needs per-price history this connector does not "
            + "serve — leave the end time empty to run from the start to now",
        FrvpRangeFailure.NoSessionClock =>
            "no session clock yet, and no range was set",
        FrvpRangeFailure.ProductRootUnknown =>
            "product root unknown, so no session default could be chosen",
        FrvpRangeFailure.NoSessionApplies =>
            "no configured session applies to this product",
        FrvpRangeFailure.NoSessionOpenedYet => clickSelectEnabled
            ? "no session has opened yet — two left clicks set a range"
            : "no session has opened yet — set start/end times or enable click-select",
        _ => "no range",
    };

    private static FrvpRange Unresolved(FrvpRangeFailure failure)
        => new(default, default, FrvpRangeSource.None, failure);

    /// <summary>
    /// Parses a typed instant as UTC. <see cref="DateTimeStyles.AssumeUniversal"/> is
    /// what makes an unsuffixed "2026-08-27T13:45" mean 13:45Z rather than 13:45 in
    /// whatever zone the host happens to sit in — the input is labelled UTC, so reading
    /// it as local would silently shift every range by the host's offset.
    /// </summary>
    private static bool TryParseUtc(string text, out DateTime utc)
        => DateTime.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out utc);
}
