using System;
using System.Globalization;

namespace OrbIx.Core.Telemetry;

/// <summary>
/// How a load reports which instance produced it and how long its blocking calls took.
///
/// THIS EXISTS BECAUSE THE LOG COULD NOT ANSWER "WHY DID THAT TAKE FIVE MINUTES".
/// A chart took over five minutes to draw on 2026-08-31. The startup log showed two
/// silent gaps, 75s and 105s, each bracketed by a history request — and the history
/// loader writes nothing at all, so the duration was real and the cause was not
/// attributable to anything. A measurement with no owner is where an invented cause
/// gets attached, which is the most damaging thing this project produces.
///
/// AND THE LOG COULD NOT EVEN SAY WHO WAS SPEAKING. Reading that same file showed
/// lines duplicated at identical timestamps — "Wave1 params" twice at 18:09:44Z,
/// "HH/LL" twice at 18:11:33Z. At least two indicator instances append to one shared
/// file with nothing distinguishing them, so a two-instance interleave and one slow
/// load are indistinguishable. Timing numbers added without a tag would have been one
/// more stream of unattributable lines: the tag is what makes the timing legible, not
/// a separate nicety.
///
/// Both functions are pure and live in Core (§11) so the offline suite asserts the
/// rendering rather than someone checking a log afterwards — which is how the
/// AttemptReporting flood came to ship in the first place.
/// </summary>
public static class LoadTiming
{
    /// <summary>
    /// Characters of the instance tag. Four hex digits is 65,536 values, which is far
    /// more than the handful of charts that can share a log, while staying short enough
    /// to sit at the head of every line without crowding the message.
    ///
    /// A GUID would be 32 characters of noise per line in an already dense file and buy
    /// nothing: this distinguishes concurrent instances, it does not identify them
    /// across machines or restarts.
    /// </summary>
    public const int TagLength = 4;

    /// <summary>
    /// A short handle for one indicator instance, derived from a value the caller
    /// guarantees is unique per instance.
    ///
    /// Takes the value rather than generating one, so the caller decides the lifetime.
    /// That matters: the tag must survive a clear-and-re-add of the same object,
    /// because that is the same instance and a tag that churned would make the log
    /// worse rather than better.
    /// </summary>
    /// <param name="seed">
    /// Any value unique to the instance for the life of the process — an object hash
    /// code is sufficient and needs no allocation.
    /// </param>
    public static string Tag(int seed)
    {
        // Masked to the low bits rather than taken modulo, so a negative hash code —
        // which is entirely ordinary — does not produce a sign in the middle of a line.
        var masked = seed & 0xFFFF;

        return masked.ToString("x4", CultureInfo.InvariantCulture);
    }

    /// <summary>Prefixes a message with its instance tag.</summary>
    public static string Prefix(string tag, string message)
        => $"[{tag}] {message}";

    /// <summary>
    /// An elapsed duration, in the coarsest unit that still says something useful.
    ///
    /// THE UNIT CHANGES BECAUSE THE QUESTION CHANGES. Under a second, the call is not
    /// what anyone is waiting for and two decimals say so without inviting a hunt.
    /// Over a minute, seconds alone stop being readable at a glance — "95.2s" and
    /// "1m 35s" are the same number, and only one of them is immediately obviously
    /// most of the complaint.
    ///
    /// Rendered to a fixed shape so two runs can be compared by eye, and always with
    /// the invariant culture: a decimal comma on one machine and a point on another
    /// would make the same duration look like different ones.
    /// </summary>
    public static string Elapsed(TimeSpan elapsed)
    {
        // A negative span means the clock moved backwards under us. Reporting it as a
        // duration would be inventing a measurement; naming it is the honest answer.
        if (elapsed < TimeSpan.Zero)
            return "elapsed unavailable (negative interval)";

        if (elapsed.TotalSeconds < 1.0)
            return elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";

        if (elapsed.TotalSeconds < 60.0)
            return elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        var minutes = (int)elapsed.TotalMinutes;
        var seconds = elapsed.Seconds;

        return string.Create(
            CultureInfo.InvariantCulture, $"{minutes}m {seconds:00}s");
    }

    /// <summary>
    /// A blocking call's outcome and cost in one clause, appended to the report the
    /// call site already makes.
    ///
    /// Appended rather than written as its own line so a load gains ONE line, not four.
    /// The startup log is the indicator's only diagnostic channel, and the last time
    /// something wrote per-attempt it produced 130 lines in a session.
    ///
    /// A TRAILING FULL STOP IS TRIMMED, because the statuses this wraps are written as
    /// complete sentences and appending to one produced "…daily bars for MNQU6. in
    /// 0.43s" in the first live run. The clause belongs inside the sentence, not after
    /// its end.
    /// </summary>
    /// <summary>
    /// One instant, rendered the one way this project renders instants in a log.
    ///
    /// HOISTED BECAUSE IT WAS ABOUT TO BE A FOURTH COPY. The same format string stood in
    /// ProfileResolution and twice in the replay sources, and a tick-history loader was
    /// adding another. Four independent copies is how a log ends up mixing conventions
    /// across lines that a reader is trying to order in time.
    ///
    /// Invariant culture, always: a decimal or calendar convention that differs between two
    /// machines makes the same instant look like two, in a file read across hosts.
    /// </summary>
    public static string Stamp(DateTime utc)
        => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static string Took(string what, TimeSpan elapsed)
        => $"{what.TrimEnd().TrimEnd('.')} in {Elapsed(elapsed)}";
}
