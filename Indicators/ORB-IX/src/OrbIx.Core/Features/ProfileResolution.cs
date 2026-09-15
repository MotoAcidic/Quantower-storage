using System;
using System.Globalization;
using OrbIx.Core.Telemetry;

namespace OrbIx.Core.Features;

/// <summary>
/// What a profile rebuild found while scanning the chart's bars.
///
/// The three counts are DISJOINT: every bar whose open fell inside the range lands in
/// exactly one of them, so <see cref="BarsInRange"/> is their sum and never a fourth
/// number that has to be kept in step.
/// </summary>
/// <param name="Covered">Bars in range that carried per-price volume-analysis levels.</param>
/// <param name="WithoutLevels">Bars in range whose level dictionary was null or empty.</param>
/// <param name="ReadRaces">Bars in range abandoned because the platform mutated the dictionary mid-read.</param>
public readonly record struct ProfileScanCounts(int Covered, int WithoutLevels, int ReadRaces)
{
    /// <summary>Bars whose open fell inside the range, however they turned out.</summary>
    public int BarsInRange => this.Covered + this.WithoutLevels + this.ReadRaces;
}

/// <summary>
/// What a tick-history load offered this rebuild.
///
/// Passed INTO the verdict rather than consulted after it, because the verdict decides
/// whether a profile is drawn at all — a source discovered afterwards could never be used.
/// The platform type that produces this lives in the indicator shell; this is the §11
/// projection of it, carrying only what the decision needs.
/// </summary>
/// <param name="Prints">Trade prints that contributed. Zero with <paramref name="Served"/>
/// true means the connector would have answered and the range held no trades.</param>
/// <param name="Levels">Distinct prices the prints aggregated to.</param>
/// <param name="Served">
/// Whether the connector serves tick history for this symbol at all, established by a
/// pre-flight capability check rather than inferred from an empty answer — the platform
/// returns an empty list for both, so nothing downstream could tell them apart.
/// </param>
/// <param name="Status">The loader's own sentence, carried verbatim into the log.</param>
/// <param name="CoversRange">
/// Whether the load reaches back to the range's start. False means it holds a LATER window
/// than the one asked for, which would understate the profile — refused for the same reason
/// a late-attaching live accumulation is refused.
/// </param>
/// <param name="CoveredFromUtc">The earliest instant the load actually covers.</param>
/// <param name="SourceLabel">
/// Provenance for the CHART. Empty when the chart's own connection served the data —
/// there is nothing to disclose. Non-empty names the connection it was borrowed from,
/// because a profile built from another broker's feed must never be presented as the
/// chart's own.
/// </param>
public readonly record struct ProfileTickSource(
    int Prints, int Levels, bool Served, string Status, bool CoversRange,
    DateTime CoveredFromUtc, string SourceLabel)
{
    /// <summary>
    /// True when this source can actually supply the profile.
    ///
    /// BOTH CONDITIONS, and the second was learned the hard way: having levels is not the same
    /// as having the RIGHT levels. A cache that starts after the range does is full of real
    /// prints from the wrong window.
    /// </summary>
    public bool Usable => this.Levels > 0 && this.CoversRange;
}

/// <summary>How a profile rebuild ended.</summary>
public enum ProfileOutcome
{
    /// <summary>The feature is switched off. Nothing to draw and nothing to report.</summary>
    Disabled,

    /// <summary>A profile was computed.</summary>
    Drawn,

    /// <summary>No range could be established. Fixed-range only.</summary>
    NoRangeConfigured,

    /// <summary>The anchor choice has not produced an instant yet. Anchored only.</summary>
    AnchorUnresolved,

    /// <summary>
    /// The range resolved but not one chart bar fell inside it. A range or timestamp
    /// fault in our own code — the data is not implicated.
    /// </summary>
    NoBarsInRange,

    /// <summary>
    /// Bars fell inside the range and none carried per-price levels. The data source
    /// supplies bar totals without a per-price breakdown, which no change to this code
    /// can conjure.
    /// </summary>
    BarsCarryNoPriceLevels,

    /// <summary>
    /// No source can serve this range on this connector: the bars carry no per-price
    /// levels, the connector refuses tick history, and the live accumulation started too
    /// late or does not exist.
    ///
    /// DISTINCT FROM <see cref="BarsCarryNoPriceLevels"/>, which describes only the bars and
    /// leaves open that another source might fill the gap. This one says every source has
    /// been asked. Waiting will not change it and re-attaching will not change it — the
    /// answer is a connector capability, and the operator should stop looking for a fault.
    /// </summary>
    NoSourceAvailable,

    /// <summary>Rows were collected but the value-area computation refused them.</summary>
    ComputeFailed,
}

/// <summary>
/// The verdict on one profile rebuild, and the exact words for it.
///
/// WHY THIS TYPE EXISTS. The indicator used to compose these strings inline, and the
/// message for a zero-coverage scan read "no per-price volume-analysis data in range"
/// whether zero bars had been examined or four thousand had been examined and rejected.
/// Those are opposite faults — one is ours, one is the feed's — and the operator could
/// not tell which had happened. Nothing could: the counts were computed, then discarded.
///
/// Being in OrbIx.Core with no reference to the trading platform (§11), the rule below
/// is provable by unit test. The indicator shell supplies observations and renders the
/// result; it decides nothing.
/// </summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="ChartText">
/// Short wording for the on-chart problems line. EMPTY whenever there is no problem —
/// the chart draws nothing at all in that case, so an empty string here is the healthy
/// state, not a missing message.
/// </param>
/// <param name="LogText">Full wording, with every count, for orbix-startup.log. Never empty.</param>
/// <param name="SourceText">
/// Short coverage description drawn beside the profile's own title — where its numbers
/// came from. Empty unless a profile was actually drawn.
/// </param>
public sealed record ProfileResolution(
    ProfileOutcome Outcome, string ChartText, string LogText, string SourceText)
{
    /// <summary>True when the operator needs to see something on the chart.</summary>
    public bool IsProblem => this.ChartText.Length != 0;

    /// <summary>
    /// Whether the live-tick accumulation may describe this range.
    ///
    /// PUBLIC BECAUSE THE CALLER MUST NOT RESTATE IT. The verdict below and the caller's
    /// choice of which engine to draw are the same decision; written twice they would
    /// eventually disagree, and the symptom would be a profile reported as drawn while
    /// the chart stayed empty.
    ///
    /// An accumulation that began after the range started cannot un-count the ticks it
    /// missed, so it is refused rather than silently understating the profile.
    /// </summary>
    public static bool LiveFallbackQualifies(
        bool openEnded, DateTime liveSinceUtc, DateTime startUtc)
        => openEnded && liveSinceUtc != default && liveSinceUtc <= startUtc;

    /// <summary>The feature is off.</summary>
    public static ProfileResolution Disabled(string title)
        => new(ProfileOutcome.Disabled, string.Empty, $"{title} off", string.Empty);

    /// <summary>Rows were collected but the value-area computation refused them.</summary>
    public static ProfileResolution ComputeFailed(string title, string reason)
        => new(
            ProfileOutcome.ComputeFailed,
            $"{title}: {reason}",
            $"{title} compute failed: {reason}",
            string.Empty);

    /// <summary>No range could be established for a fixed-range profile.</summary>
    public static ProfileResolution NoRange(
        string title, FrvpRangeFailure failure, bool clickSelectEnabled)
    {
        var reason = FrvpRangeResolver.Describe(failure, clickSelectEnabled);

        return new ProfileResolution(
            ProfileOutcome.NoRangeConfigured,
            $"{title}: no range — {reason}",
            $"{title} no range: {failure} — {reason}",
            string.Empty);
    }

    /// <summary>The anchored profile's anchor has not resolved to an instant yet.</summary>
    public static ProfileResolution AnchorUnresolved(string title, string anchorChoice)
        => new(
            ProfileOutcome.AnchorUnresolved,
            $"{title}: anchor unresolved",
            $"{title} anchor unresolved: {anchorChoice} has produced no instant yet",
            string.Empty);

    /// <summary>
    /// The verdict once the range resolved and the bars were scanned.
    ///
    /// The live-tick fallback is offered only for an open-ended range and only when the
    /// accumulation began at or before the start, because it cannot un-count ticks from
    /// before it. That comparison is made here rather than by the caller so that it, too,
    /// is covered by a test.
    /// </summary>
    /// <param name="title">"FRVP" or "AVP".</param>
    /// <param name="startUtc">Resolved start of the range.</param>
    /// <param name="rangeSource">Where the range came from, for the log.</param>
    /// <param name="counts">What the bar scan found.</param>
    /// <param name="openEnded">True when the range runs to now and the fallback may apply.</param>
    /// <param name="liveSinceUtc">When live accumulation began, or <c>default</c> if there is none.</param>
    /// <param name="tickSource">
    /// What a tick-history load offered, or null when it was not attempted. Ranked ABOVE the
    /// live accumulation because it covers the range as it actually was, rather than only the
    /// part that happened after this indicator attached.
    /// </param>
    public static ProfileResolution FromScan(
        string title,
        DateTime startUtc,
        string rangeSource,
        ProfileScanCounts counts,
        bool openEnded,
        DateTime liveSinceUtc,
        ProfileTickSource? tickSource = null)
    {
        var races = counts.ReadRaces > 0
            ? $", {counts.ReadRaces} read-races"
            : string.Empty;

        if (counts.Covered > 0)
        {
            var source = $"{rangeSource}, volume-analysis "
                + $"{counts.Covered}/{counts.Covered + counts.WithoutLevels} bars{races}";

            return new ProfileResolution(
                ProfileOutcome.Drawn,
                string.Empty,
                $"{title} drawn: {source}, from {Stamp(startUtc)}",
                source);
        }

        // TICKS RANK ABOVE THE LIVE ACCUMULATION, and the order is the whole point. The live
        // engine only ever holds what arrived after this indicator attached, so for any range
        // that began earlier it is refused as an understatement. A tick load is bounded by the
        // range that was ASKED FOR, so it describes the same window the operator drew.
        if (tickSource is { Usable: true } ticks)
        {
            // The provenance rides on the SOURCE line, beside the profile's own title, because
            // that is the line a reader checks when asking where a number came from.
            var borrowed = string.IsNullOrEmpty(ticks.SourceLabel)
                ? string.Empty
                : $" {ticks.SourceLabel}";

            var tickSourceText = $"{rangeSource}, tick history{borrowed} "
                + $"{ticks.Prints} print(s) over {ticks.Levels} price(s)";

            return new ProfileResolution(
                ProfileOutcome.Drawn,
                string.Empty,
                $"{title} drawn: {tickSourceText}, from {Stamp(startUtc)} "
                + $"(no bar in range carried levels){races}",
                tickSourceText);
        }

        var hasLive = liveSinceUtc != default;

        if (LiveFallbackQualifies(openEnded, liveSinceUtc, startUtc))
        {
            var source = $"{rangeSource}, live ticks since {Stamp(liveSinceUtc)}";

            return new ProfileResolution(
                ProfileOutcome.Drawn,
                string.Empty,
                $"{title} drawn: {source} (no bar in range carried levels){races}",
                source);
        }

        // The live accumulation exists but started too late to describe this range.
        // Stated wherever it applies, because it is the difference between "there is no
        // fallback" and "there is one and it would have lied".
        var lateLive = openEnded && hasLive;

        var liveNote = lateLive
            ? $" — live accumulation started {Stamp(liveSinceUtc)}, after the range began"
            : string.Empty;

        // THE CHART SAYS THE SAME THING THE LOG DOES, WHICH IT DID NOT BEFORE.
        //
        //   The count below describes the FEED: "0 of 155 in-range bars carry per-price
        //   levels" says the connector serves no per-price breakdown, which is permanent and
        //   which no waiting or re-attaching changes. When the accumulation merely started
        //   late that sentence is FALSE about the cause, and it was the only sentence the
        //   chart had. Measured 2026-08-31: 372 log lines carried the real reason while the
        //   chart showed the other one, and it sent this session's own diagnosis the wrong
        //   way twice.
        //
        //   BOTH INSTANTS, because the gap between them is the whole finding and it is what
        //   an operator can act on.
        //
        //   IT DOES NOT SAY "ACCUMULATING". For a fixed anchor — a session default, an ORB
        //   close — startUtc never moves, so the accumulation can never come to precede it
        //   and waiting does NOT fill this range in. It resolves at the next anchor that
        //   falls after the attach. A word implying patience would be false for exactly the
        //   anchors in use.
        var lateLiveChartText = lateLive
            ? $"{title}: attached {ShortStamp(liveSinceUtc)}, after this range began "
              + $"{ShortStamp(startUtc)}"
            : string.Empty;

        // The tick load's own sentence, kept verbatim. It is the only place the connector's
        // reason survives — the platform's history API has no error channel whatsoever, so if
        // this is dropped here it is gone.
        var tickNote = tickSource is { } attempted
            ? $" — {attempted.Status}"
            : string.Empty;

        // EVERY SOURCE HAS BEEN ASKED, AND ALL OF THEM SAID NO. That is a different sentence
        // from "these bars lack levels", which leaves open that something else might fill the
        // gap. Reached only when the connector itself refused tick history, so it reports a
        // capability rather than a condition — nothing the operator does will change it, and
        // the useful next step is a question to the vendor, not another restart.
        // SERVED, REAL PRINTS, WRONG WINDOW. The connector answered and the data is genuine —
        // it simply starts after this range does, so drawing it would put a correct-looking POC
        // over the wrong hours. Stated in the same shape as the late-live message, because it is
        // the same failure: a source that cannot reach back far enough.
        if (tickSource is { Served: true, Levels: > 0, CoversRange: false } shortCache)
        {
            return new ProfileResolution(
                ProfileOutcome.NoSourceAvailable,
                $"{title}: tick history reaches back only to {ShortStamp(shortCache.CoveredFromUtc)}, "
                + $"after this range began {ShortStamp(startUtc)}",
                $"{title} tick history too short: {rangeSource}, from {Stamp(startUtc)}, "
                + $"covered from {Stamp(shortCache.CoveredFromUtc)} — {shortCache.Status}"
                + liveNote,
                string.Empty);
        }

        if (tickSource is { Served: false } refused)
        {
            return new ProfileResolution(
                ProfileOutcome.NoSourceAvailable,
                lateLive
                    ? lateLiveChartText
                    : $"{title}: no source can serve this range on this connector",
                $"{title} no source available: {rangeSource}, from {Stamp(startUtc)}, "
                + $"{counts.BarsInRange} bars in range carried no levels, and {refused.Status}"
                + liveNote,
                string.Empty);
        }

        // THE DISCRIMINATION. Nothing examined at all, versus everything examined and
        // rejected. One is a fault in our range arithmetic; the other says the feed
        // carries no per-price breakdown on this connector.
        if (counts.BarsInRange == 0)
        {
            return new ProfileResolution(
                ProfileOutcome.NoBarsInRange,
                lateLive ? lateLiveChartText : $"{title}: no bars in range",
                $"{title} no bars in range: {rangeSource}, from {Stamp(startUtc)}, "
                + $"0 bars examined{liveNote}",
                string.Empty);
        }

        return new ProfileResolution(
            ProfileOutcome.BarsCarryNoPriceLevels,
            lateLive
                ? lateLiveChartText
                : $"{title}: 0 of {counts.BarsInRange} in-range bars carry per-price levels",
            $"{title} no per-price levels: {rangeSource}, from {Stamp(startUtc)}, "
            + $"{counts.BarsInRange} bars in range — {counts.Covered} covered, "
            + $"{counts.WithoutLevels} without levels, {counts.ReadRaces} read-races"
            + $"{tickNote}{liveNote}",
            string.Empty);
    }

    private static string Stamp(DateTime utc) => LoadTiming.Stamp(utc);

    /// <summary>
    /// An instant for the CHART, where space is the constraint and the date is not.
    ///
    /// The log keeps the full <see cref="Stamp"/>: it is read later, possibly across days,
    /// and a bare clock time there would be ambiguous. The chart line is read now, about
    /// today, beside a time axis already showing the date — so minutes are the useful
    /// resolution and seconds are noise. Both are UTC and both say so, because a mixed
    /// convention in one message is worse than either alone.
    /// </summary>
    private static string ShortStamp(DateTime utc)
        => utc.ToString("HH:mm'Z'", CultureInfo.InvariantCulture);
}
