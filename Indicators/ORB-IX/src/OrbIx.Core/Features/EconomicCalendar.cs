using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using OrbIx.Core.Config;

namespace OrbIx.Core.Features;

/// <summary>How much a release is expected to move the market.</summary>
public enum EventImpact
{
    Low,
    Medium,
    High,
}

/// <summary>One scheduled release.</summary>
/// <param name="TimeUtc">When it publishes.</param>
/// <param name="Name">What it is, for the panel and the journal.</param>
/// <param name="Impact">Expected disturbance.</param>
/// <param name="SymbolRoots">Products affected; empty means all of them.</param>
/// <param name="Display">
/// What a person should read on the chart, when it differs from <paramref name="Name"/>.
///
/// THE NAME IS A MATCHER KEY AND WAS NEVER A LABEL. My Funded Futures enumerates its
/// Tier 1 events by title, so "Employment Situation (Employment Report)" is worded to make
/// the Tier-1 substring match fire while avoiding the recorded ADP false-match. That
/// string decides whether this account may trade the window; it is also what appeared on
/// the chart, where it read as bureaucratic noise at exactly the moment it mattered.
///
/// Empty means there is no separate label and <paramref name="Name"/> is shown, which is
/// what every event did before this existed.
/// </param>
/// <param name="Tier1">
/// Whether this is one of the firm's Tier 1 events.
///
/// My Funded Futures enumerates them by name — "For All Traders: FOMC Meetings, FOMC Minutes,
/// Employment Report, CPI" — and treats them differently from other releases: on Rapid Sim
/// Funded and Pro Sim Funded accounts, trading them is prohibited rather than merely
/// windowed. The feed carries no tier of its own, so this is resolved from configuration.
/// </param>
public sealed record EconomicEvent(
    DateTime TimeUtc,
    string Name,
    EventImpact Impact,
    IReadOnlyList<string> SymbolRoots,
    bool Tier1 = false,
    string Display = "")
{
    /// <summary>What to show a person: the display name when set, otherwise the title.</summary>
    public string Label => this.Display.Length > 0 ? this.Display : this.Name;

    public bool AppliesTo(string symbolRoot)
        => this.SymbolRoots.Count == 0
           || this.SymbolRoots.Contains(symbolRoot, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Whether a moment sits inside a blackout, and why.
/// </summary>
/// <param name="Blocked">Whether an entry is refused.</param>
/// <param name="CalendarAvailable">
/// Whether any calendar was read at all. False with <see cref="Blocked"/> false means
/// "no events known", which is a different statement from "no events".
/// </param>
/// <param name="Reason">Human-readable explanation, always populated.</param>
/// <param name="NextEvent">The next applicable release, when one is known.</param>
public sealed record BlackoutState(
    bool Blocked,
    bool CalendarAvailable,
    string Reason,
    EconomicEvent? NextEvent);

/// <summary>Supplies scheduled releases.</summary>
public interface IEconomicCalendar
{
    /// <summary>Whether a calendar was successfully loaded.</summary>
    bool Available { get; }

    /// <summary>Why <see cref="Available"/> is false, or how the calendar was loaded.</summary>
    string Status { get; }

    IReadOnlyList<EconomicEvent> Events { get; }
}

/// <summary>
/// Reads scheduled releases from a JSON file.
///
/// The file format, deliberately small enough to maintain by hand:
///
/// <code>
/// {
///   "events": [
///     { "time": "2026-08-20T12:30:00Z", "name": "Nonfarm payrolls",
///       "impact": "High", "symbols": [] },
///     { "time": "2026-08-20T14:00:00Z", "name": "Crude inventories",
///       "impact": "Medium", "symbols": ["GC"] }
///   ]
/// }
/// </code>
///
/// A missing file is reported as unavailable rather than as an empty calendar. The two are
/// not the same, and conflating them is how a system ends up trading through a payrolls
/// print because nobody supplied a file.
/// </summary>
public sealed class FileEconomicCalendar : IEconomicCalendar
{
    private FileEconomicCalendar(bool available, string status, IReadOnlyList<EconomicEvent> events)
    {
        this.Available = available;
        this.Status = status;
        this.Events = events;
    }

    public bool Available { get; }

    public string Status { get; }

    public IReadOnlyList<EconomicEvent> Events { get; }

    /// <summary>
    /// Loads a calendar. Never throws for an absent or malformed file: the failure is
    /// carried in <see cref="Status"/> so the engine can surface it and apply the
    /// configured policy, rather than the indicator dying on a missing data file.
    /// </summary>
    /// <summary>
    /// Loads a calendar, judging its coverage against an instant.
    /// </summary>
    /// <param name="path">The calendar file.</param>
    /// <param name="asOfUtc">
    /// The instant to judge coverage against. Supplied rather than read from the clock so a
    /// replay loads a calendar exactly as the live run did, and so the behaviour is testable
    /// without waiting.
    /// </param>
    /// <param name="classification">
    /// Supplies the impact mapping and the Tier 1 definition. Both are decisions rather than
    /// data — the feed carries neither an impact rating nor a tier — so both live in
    /// configuration. Null leaves every event at the parser's own defaults unless the event
    /// states an impact explicitly.
    /// </param>
    public static FileEconomicCalendar Load(
        string path, DateTime asOfUtc, CalendarConfig? classification = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FileEconomicCalendar(false, "No calendar path configured.", Array.Empty<EconomicEvent>());

        if (!File.Exists(path))
        {
            return new FileEconomicCalendar(
                false,
                $"No calendar at {path}. No events are known — which is not the same as no events.",
                Array.Empty<EconomicEvent>());
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileEconomicCalendar(
                false, $"Calendar at {path} could not be read: {ex.Message}", Array.Empty<EconomicEvent>());
        }

        try
        {
            using var document = JsonDocument.Parse(
                text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!document.RootElement.TryGetProperty("events", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return new FileEconomicCalendar(
                    false, $"Calendar at {path} has no 'events' array.", Array.Empty<EconomicEvent>());
            }

            var events = new List<EconomicEvent>();
            var index = 0;

            foreach (var item in array.EnumerateArray())
            {
                if (!TryReadEvent(item, index, classification, out var parsed, out var problem))
                {
                    return new FileEconomicCalendar(
                        false, $"Calendar at {path} is not usable: {problem}", Array.Empty<EconomicEvent>());
                }

                events.Add(parsed);
                index++;
            }

            events.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));

            var generated = ReadInstant(document.RootElement, "generatedUtc");
            var coversUntil = ReadInstant(document.RootElement, "coversUntilUtc");

            // A calendar that has aged out of its own coverage is the dangerous case. It parses
            // cleanly, every event in it has passed, and the engine sees nothing near now — so
            // it does not block. That is indistinguishable from "no releases scheduled", which
            // is the exact confusion this whole policy exists to prevent. An expired calendar
            // is therefore reported as UNAVAILABLE, so whenUnavailable applies just as it does
            // to a file that was never there.
            if (coversUntil is { } until && asOfUtc > until)
            {
                return new FileEconomicCalendar(
                    false,
                    $"Calendar at {path} covers only until {until:u} and it is now {asOfUtc:u}. "
                    + $"Its {events.Count} events are stale, so no events are known — which is "
                    + "not the same as no events.",
                    Array.Empty<EconomicEvent>());
            }

            var age = generated is { } stamp
                ? $" Generated {FormatAge(asOfUtc - stamp)} ago."
                : string.Empty;

            var coverage = coversUntil is { } end
                ? $" Covers until {end:u}."
                : string.Empty;

            return new FileEconomicCalendar(
                true, $"{events.Count} events loaded from {path}.{age}{coverage}", events);
        }
        catch (JsonException ex)
        {
            return new FileEconomicCalendar(
                false, $"Calendar at {path} is not valid JSON: {ex.Message}", Array.Empty<EconomicEvent>());
        }
    }

    /// <summary>
    /// Reads an optional ISO-8601 instant from the document root.
    ///
    /// Absent is a valid answer, not a fault: a hand-written calendar carrying neither stamp
    /// still loads. Only a coverage end that is present AND passed makes a calendar
    /// unavailable, so adding the fields can never make a working file stop working.
    /// </summary>
    private static DateTime? ReadInstant(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return null;

        return DateTime.TryParse(
            element.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Renders an age the way an operator reads one, at a glance.</summary>
    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            return "0m";

        return age.TotalDays >= 1
            ? $"{(int)age.TotalDays}d {age.Hours}h"
            : age.TotalHours >= 1
                ? $"{(int)age.TotalHours}h {age.Minutes}m"
                : $"{(int)age.TotalMinutes}m";
    }

    /// <summary>Builds a calendar directly, for replay and for tests.</summary>
    public static FileEconomicCalendar FromEvents(IEnumerable<EconomicEvent> events, string status)
    {
        var ordered = events.OrderBy(e => e.TimeUtc).ToList();
        return new FileEconomicCalendar(true, status, ordered);
    }

    /// <summary>
    /// Reads one event.
    ///
    /// An explicit <c>impact</c> wins over the configured mapping, so a hand-written calendar
    /// keeps working unchanged.
    /// </summary>
    /// <param name="element">The event object.</param>
    /// <param name="index">Its position, so a fault names the offending entry.</param>
    /// <param name="classification">Impact mapping and Tier 1 definition, or null.</param>
    /// <param name="parsed">The event, when this returns true.</param>
    /// <param name="problem">Why it could not be read, when this returns false.</param>
    private static bool TryReadEvent(
        JsonElement element,
        int index,
        CalendarConfig? classification,
        out EconomicEvent parsed,
        out string problem)
    {
        parsed = new EconomicEvent(default, string.Empty, EventImpact.Low, Array.Empty<string>(), false);

        if (element.ValueKind != JsonValueKind.Object)
        {
            problem = $"events[{index}] is not an object.";
            return false;
        }

        if (!element.TryGetProperty("time", out var timeElement)
            || timeElement.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(
                timeElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
        {
            problem = $"events[{index}].time is missing or is not an ISO-8601 instant.";
            return false;
        }

        if (!element.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            problem = $"events[{index}].name is missing.";
            return false;
        }

        // Precedence, and the default, both matter. An explicit impact wins; failing that the
        // configured mapping classifies the feed's own type; failing that High, because the
        // failure that costs money is trading through an unclassified release rather than
        // pausing for one.
        var impact = EventImpact.High;
        var explicitImpact = element.TryGetProperty("impact", out var impactElement)
                             && impactElement.ValueKind == JsonValueKind.String;

        if (explicitImpact)
        {
            if (!Enum.TryParse(impactElement.GetString(), ignoreCase: true, out impact))
            {
                problem = $"events[{index}].impact is not one of {string.Join(", ", Enum.GetNames<EventImpact>())}.";
                return false;
            }
        }
        var sourceType = element.TryGetProperty("sourceType", out var typeElement)
                         && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        if (!explicitImpact && classification is not null)
            impact = classification.ImpactFor(sourceType);

        var symbols = new List<string>();
        if (element.TryGetProperty("symbols", out var symbolsElement)
            && symbolsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var symbol in symbolsElement.EnumerateArray())
            {
                if (symbol.ValueKind != JsonValueKind.String)
                {
                    problem = $"events[{index}].symbols contains a non-string entry.";
                    return false;
                }

                symbols.Add(symbol.GetString() ?? string.Empty);
            }
        }

        var name = nameElement.GetString() ?? string.Empty;

        // An explicit tier1 on the event wins, so a hand-written calendar can mark one the
        // configured patterns do not reach — an EIA release, say, for an energy trader.
        var tier1 = element.TryGetProperty("tier1", out var tierElement)
                    && tierElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? tierElement.GetBoolean()
            : classification?.Tier1.Matches(sourceType, name) ?? false;

        // READ AFTER tier1, and deliberately not used by it. Tier-1 matching keys off
        // `name` alone, so a display label can be changed, translated or shortened without
        // any risk of unprotecting the account -- the one way this field could have cost
        // money. A test pins that independence.
        var display = element.TryGetProperty("display", out var displayElement)
                      && displayElement.ValueKind == JsonValueKind.String
            ? displayElement.GetString() ?? string.Empty
            : string.Empty;

        parsed = new EconomicEvent(
            DateTime.SpecifyKind(time, DateTimeKind.Utc),
            name,
            impact,
            symbols,
            tier1,
            display);

        problem = string.Empty;
        return true;
    }
}

/// <summary>
/// Decides whether an instant sits inside an event blackout.
///
/// Only high-impact releases blackout by default, because blacking out for every low-impact
/// datapoint would close the session. The window is asymmetric — the specification blocks
/// less time before a release than after it — because the disturbance outlasts the print.
/// </summary>
public sealed class BlackoutPolicy
{
    private readonly CalendarConfig config;
    private readonly IEconomicCalendar calendar;
    private readonly NewsRule rule;

    /// <param name="config">Blackout windows and the Tier 1 definition.</param>
    /// <param name="calendar">The scheduled events.</param>
    /// <param name="rule">
    /// The active account's news restriction. Required rather than defaulted: which rule an
    /// account carries is the whole question, and a default would quietly apply one account's
    /// obligations to another.
    /// </param>
    public BlackoutPolicy(CalendarConfig config, IEconomicCalendar calendar, NewsRule rule)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        this.rule = rule;
    }

    /// <summary>
    /// Evaluates the blackout at an instant.
    ///
    /// <paramref name="autoMode"/> overrides the configured policy when no calendar is
    /// available: unattended, an unknown calendar always blocks, because there is no human
    /// present to act on a warning.
    /// </summary>
    public BlackoutState Evaluate(DateTime utcNow, string symbolRoot, bool autoMode)
    {
        if (string.IsNullOrWhiteSpace(symbolRoot))
            throw new ArgumentException("A product root is required.", nameof(symbolRoot));

        // An account with no news restriction has nothing to evaluate, including when the
        // calendar is missing: refusing to trade for want of a calendar that governs nothing
        // would be a rule nobody imposed.
        if (this.rule == NewsRule.None)
        {
            return new BlackoutState(
                false,
                CalendarAvailable: this.calendar.Available,
                Reason: "This account carries no news restriction.",
                NextEvent: null);
        }

        if (!this.calendar.Available)
        {
            var block = autoMode || this.config.WhenUnavailable == CalendarUnavailablePolicy.Block;

            return new BlackoutState(
                block,
                CalendarAvailable: false,
                // The loader's own status is deliberately NOT repeated here. It is reported
                // once where the calendar is loaded; embedding it again produces the same
                // sentence twice on the panel, nested inside itself.
                Reason: block
                    ? "No economic calendar available, so entries are refused."
                      + (autoMode ? " Auto mode always blocks on an unknown calendar." : string.Empty)
                    : "No economic calendar available; proceeding on the operator's judgement.",
                NextEvent: null);
        }

        var before = TimeSpan.FromMinutes(this.config.BlockMinBefore);
        var after = TimeSpan.FromMinutes(this.config.BlockMinAfter);
        var tier1Before = TimeSpan.FromMinutes(this.config.Tier1.BlockMinBefore);
        var tier1After = TimeSpan.FromMinutes(this.config.Tier1.BlockMinAfter);

        EconomicEvent? next = null;

        foreach (var scheduled in this.calendar.Events)
        {
            if (!scheduled.AppliesTo(symbolRoot))
                continue;

            // A Tier 1 event on a restricted account uses its own window, because the firm
            // prohibits trading those events rather than merely requiring a flat book around
            // them. On an unrestricted account Tier 1 is just another release.
            var restricted = this.rule == NewsRule.ProhibitTier1 && scheduled.Tier1;
            var windowBefore = restricted ? tier1Before : before;
            var windowAfter = restricted ? tier1After : after;

            if (scheduled.Impact == EventImpact.High
                && utcNow >= scheduled.TimeUtc - windowBefore
                && utcNow <= scheduled.TimeUtc + windowAfter)
            {
                var minutesBefore = restricted ? this.config.Tier1.BlockMinBefore : this.config.BlockMinBefore;
                var minutesAfter = restricted ? this.config.Tier1.BlockMinAfter : this.config.BlockMinAfter;

                return new BlackoutState(
                    true,
                    CalendarAvailable: true,
                    Reason: (restricted ? "TIER 1 blackout for " : "Blackout for ")
                            + $"{scheduled.Name} at {scheduled.TimeUtc:HH:mm} UTC "
                            + $"({minutesBefore}m before, {minutesAfter}m after"
                            + (restricted ? "; this account may not trade Tier 1 events)." : ")."),
                    NextEvent: scheduled);
            }

            if (scheduled.TimeUtc > utcNow && next is null)
                next = scheduled;
        }

        return new BlackoutState(
            false,
            CalendarAvailable: true,
            Reason: next is null
                ? "No further scheduled releases."
                : $"Clear. Next: {next.Name} at {next.TimeUtc:HH:mm} UTC.",
            NextEvent: next);
    }
}
