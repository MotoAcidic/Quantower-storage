using System;
using System.Collections.Generic;
using System.Linq;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;

namespace OrbIx.Core.Sessions;

/// <summary>
/// One occurrence of a session: the concrete instants a definition maps to on a particular
/// local date.
/// </summary>
/// <param name="Definition">The configured session this occurrence belongs to.</param>
/// <param name="LocalDate">The date in the session time zone whose open time produced it.</param>
/// <param name="OpenUtc">The instant the session opened.</param>
/// <param name="OrHardCloseUtc">Latest instant the opening range may still be building.</param>
/// <param name="EndUtc">The instant this session stops being the active one.</param>
public sealed record SessionWindow(
    SessionDefinition Definition,
    DateOnly LocalDate,
    DateTime OpenUtc,
    DateTime OrHardCloseUtc,
    DateTime EndUtc)
{
    public bool Contains(DateTime utc) => utc >= this.OpenUtc && utc < this.EndUtc;

    /// <summary>
    /// Phase at an instant. The opening range is building until its hard close; after that
    /// the session is armed until its trading window ends.
    /// </summary>
    public SessionPhase PhaseAt(DateTime utc)
    {
        if (utc < this.OpenUtc || utc >= this.EndUtc)
            return SessionPhase.Idle;

        return utc < this.OrHardCloseUtc ? SessionPhase.OrForming : SessionPhase.Armed;
    }
}

/// <summary>
/// M01. Turns configured wall-clock session times into instants, correctly across daylight
/// saving transitions.
///
/// Boundaries are stored as a time of day in a named zone and converted at use. They are
/// never stored as a UTC offset, because an offset is wrong on one side of every
/// transition — the specification calls this the single most common source of silent
/// breakage, and it is the reason gold's 08:20 open drifts in naive implementations.
///
/// Two transition cases are handled explicitly rather than left to the default behaviour
/// of <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>:
///
///   Skipped times. On a spring-forward date a configured open such as 02:00 may not exist.
///   The session opens at the first instant the local clock reaches, which is when trading
///   actually resumes — not on the previous day's offset, and not by throwing.
///
///   Repeated times. On an autumn-back date a configured open occurs twice. The first
///   occurrence is taken. Either choice is defensible; an undocumented one is not, and an
///   inconsistent one produces two different ranges for the same session.
/// </summary>
public sealed class SessionClock
{
    /// <summary>
    /// How far past a skipped local time to search for the instant the clock resumes. No
    /// civil daylight-saving shift approaches this, so exceeding it means the zone data is
    /// not what this code assumes rather than that a larger window would help.
    /// </summary>
    private static readonly TimeSpan MaxTransitionSearch = TimeSpan.FromHours(6);

    private readonly TimeZoneInfo sessionZone;
    private readonly IReadOnlyList<SessionDefinition> definitions;
    private readonly TradingWeek tradingWeek;

    public SessionClock(OrbIxConfig config)
        : this(ResolveZone(config), SessionDefinition.ResolveAll(config), ResolveTradingWeek(config))
    {
    }

    /// <param name="sessionZone">Zone the session open times are written in.</param>
    /// <param name="definitions">The session definitions this clock enumerates.</param>
    /// <param name="tradingWeek">
    /// When the market is open. REQUIRED rather than defaulted: a clock that silently assumed
    /// a week would be free to invent Saturday sessions again, which is the defect this
    /// parameter exists to make impossible.
    /// </param>
    public SessionClock(
        TimeZoneInfo sessionZone,
        IReadOnlyList<SessionDefinition> definitions,
        TradingWeek tradingWeek)
    {
        this.sessionZone = sessionZone ?? throw new ArgumentNullException(nameof(sessionZone));
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.tradingWeek = tradingWeek ?? throw new ArgumentNullException(nameof(tradingWeek));
    }

    /// <summary>When the market is open, as this clock was configured.</summary>
    public TradingWeek Week => this.tradingWeek;

    /// <summary>
    /// The zone this clock reckons wall-clock times in.
    ///
    /// EXPOSED SO NOBODY LOOKS IT UP TWICE. A caller needing to show a time to a person
    /// could call TimeZoneInfo.FindSystemTimeZoneById on the same config string, and would
    /// then hold a SECOND source of truth for which day an instant belongs to. Two
    /// disagreeing day boundaries inside one indicator is a defect class this project has
    /// already paid for more than once.
    /// </summary>
    public TimeZoneInfo SessionZone => this.sessionZone;

    private static TimeZoneInfo ResolveZone(OrbIxConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        return TimeZoneInfo.FindSystemTimeZoneById(config.SessionTimeZone);
    }

    private static TradingWeek ResolveTradingWeek(OrbIxConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        var week = config.TradingWeek;

        return new TradingWeek(week.OpenDayOfWeek, week.OpenLocalTime,
                               week.CloseDayOfWeek, week.CloseLocalTime);
    }

    /// <summary>
    /// Does this window's open fall inside the trading week?
    ///
    /// The OPEN decides, not the range or the end: a session either happened or it did not,
    /// and a window opening after Friday's close never happened however far its range would
    /// have run. Sessions outside the week are DROPPED rather than moved — shifting one would
    /// invent a session on a day the market was shut.
    /// </summary>
    private bool Occurs(SessionWindow window)
        => this.tradingWeek.Contains(this.ToLocal(window.OpenUtc));

    public IReadOnlyList<SessionDefinition> Definitions => this.definitions;

    /// <summary>
    /// The UTC instant a session opens on a given local date, resolving daylight-saving
    /// transitions as documented on the type.
    /// </summary>
    public DateTime OpenInstantUtc(SessionDefinition definition, DateOnly localDate)
    {
        if (definition is null)
            throw new ArgumentNullException(nameof(definition));

        var wanted = localDate.ToDateTime(TimeOnly.FromTimeSpan(definition.OpenLocalTime));
        return this.ToUtc(wanted);
    }

    /// <summary>
    /// Converts a wall-clock instant in the session zone to UTC, resolving skipped and
    /// repeated local times deterministically.
    /// </summary>
    public DateTime ToUtc(DateTime localWallClock)
    {
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        if (this.sessionZone.IsInvalidTime(unspecified))
        {
            // The local clock skipped this time. Walk forward to the first instant that
            // exists; that is when the session's clock time actually arrives.
            var probe = unspecified;
            var limit = unspecified + MaxTransitionSearch;

            while (this.sessionZone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);

                if (probe > limit)
                {
                    throw new InvalidTimeZoneException(
                        $"Local time {unspecified:yyyy-MM-dd HH:mm} in {this.sessionZone.Id} remains invalid "
                        + $"{MaxTransitionSearch.TotalHours:0} hours later. The zone data is not what this code assumes.");
                }
            }

            return TimeZoneInfo.ConvertTimeToUtc(probe, this.sessionZone);
        }

        if (this.sessionZone.IsAmbiguousTime(unspecified))
        {
            // The local clock repeated this time. Take the first occurrence: the larger UTC
            // offset is the pre-transition one, and subtracting it gives the earlier instant.
            var offsets = this.sessionZone.GetAmbiguousTimeOffsets(unspecified);
            var firstOccurrence = offsets.Max();
            return DateTime.SpecifyKind(unspecified - firstOccurrence, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, this.sessionZone);
    }

    /// <summary>Converts a UTC instant to the session zone's wall clock.</summary>
    public DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), this.sessionZone);

    /// <summary>
    /// Every session occurrence that could contain <paramref name="utc"/> for a product,
    /// ordered by open instant.
    ///
    /// Three local dates are considered because sessions opening in the evening belong to
    /// the following trading day, and a window may still be running from the previous one.
    /// </summary>
    public IReadOnlyList<SessionWindow> WindowsAround(DateTime utc, string symbolRoot)
    {
        if (string.IsNullOrWhiteSpace(symbolRoot))
            throw new ArgumentException("A product root is required.", nameof(symbolRoot));

        var applicable = this.definitions
            .Where(d => d.Enabled && d.AppliesTo(symbolRoot))
            .ToList();

        if (applicable.Count == 0)
            return Array.Empty<SessionWindow>();

        var centre = DateOnly.FromDateTime(this.ToLocal(utc));
        var windows = new List<SessionWindow>();

        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var date = centre.AddDays(dayOffset);
            foreach (var definition in applicable)
            {
                var window = this.BuildWindow(definition, date, applicable);

                if (this.Occurs(window))
                    windows.Add(window);
            }
        }

        return windows.OrderBy(w => w.OpenUtc).ToList();
    }

    /// <summary>
    /// Every session occurrence whose opening range falls inside a span, ascending by open.
    ///
    /// This is what the chart overlay asks for: given the visible time range, which opening
    /// ranges should be on screen. It is built on the same <see cref="BuildWindow"/> as
    /// <see cref="WindowsAround"/>, so a window enumerated here is identical to the one the
    /// live engine would select at that instant — the overlay and the engine cannot drift.
    ///
    /// A window is included when its opening range overlaps the span, not merely when its
    /// open does. A range still forming at the left edge of the view is part of that view.
    ///
    /// The day loop runs one day beyond each end because a session opening in the evening
    /// belongs to the following trading day, and its range can extend past midnight.
    /// </summary>
    /// <param name="fromUtc">Start of the span, inclusive.</param>
    /// <param name="toUtc">End of the span, inclusive.</param>
    /// <param name="symbolRoot">Product root, so product-specific sessions apply correctly.</param>
    public IReadOnlyList<SessionWindow> WindowsBetween(DateTime fromUtc, DateTime toUtc, string symbolRoot)
    {
        if (string.IsNullOrWhiteSpace(symbolRoot))
            throw new ArgumentException("A product root is required.", nameof(symbolRoot));

        if (toUtc < fromUtc)
            throw new ArgumentException("The end of the span cannot precede its start.", nameof(toUtc));

        var applicable = this.definitions
            .Where(d => d.Enabled && d.AppliesTo(symbolRoot))
            .ToList();

        if (applicable.Count == 0)
            return Array.Empty<SessionWindow>();

        var first = DateOnly.FromDateTime(this.ToLocal(fromUtc)).AddDays(-1);
        var last = DateOnly.FromDateTime(this.ToLocal(toUtc)).AddDays(1);

        var windows = new List<SessionWindow>();

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            foreach (var definition in applicable)
            {
                var window = this.BuildWindow(definition, date, applicable);

                if (!this.Occurs(window))
                    continue;

                // Overlap, not containment: a range straddling either edge is still visible.
                if (window.OrHardCloseUtc >= fromUtc && window.OpenUtc <= toUtc)
                    windows.Add(window);
            }
        }

        return windows
            .OrderBy(w => w.OpenUtc)
            .ThenBy(w => w.Definition.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The session occurrence that is current at an instant, or null when the instant falls
    /// outside every configured window.
    ///
    /// Windows genuinely overlap: the initial-balance window shares its open with the
    /// regular session and runs twelve times longer. The precedence is therefore explicit
    /// rather than incidental, and every step is deterministic so a replay and a live run
    /// select the same window:
    ///
    ///   1. A window that permits entries outranks one that does not. The initial balance
    ///      supplies extension targets and a day type; it is not the session a signal
    ///      belongs to, and treating it as one would attribute every regular-session trade
    ///      to it and hold the range open for an hour.
    ///   2. The most recent open wins, so a later session supersedes an earlier one.
    ///   3. The shorter opening range wins, as the more specific description of the same
    ///      instant.
    ///   4. Name, ordinally — never reached in the shipped configuration, present so the
    ///      result cannot depend on dictionary ordering.
    /// </summary>
    /// <summary>
    /// The most recent open of a NAMED session at or before an instant, or null when none
    /// falls in the search window.
    ///
    /// WHY THIS EXISTS: A TRADING DAY IS NOT A CALENDAR DAY. Anything that counts "today" —
    /// the operator's trades, the day's realised profit, a per-day limit — needs the instant
    /// the futures day began, and for these products that is the evening open, not midnight
    /// in any zone. Deriving it from the configured session rather than hard-coding a time
    /// means the answer moves with the clock that draws everything else on the chart, and
    /// cannot silently disagree with it.
    ///
    /// The caller names the session, because which one begins the day is a decision about
    /// the product, not a fact this type can settle.
    ///
    /// Searched over four days back, which covers the weekend: a Friday evening open is
    /// still the most recent one throughout Saturday and until Sunday's.
    /// </summary>
    /// <param name="sessionName">The session whose open begins the day. Matched case-insensitively.</param>
    /// <param name="utc">The instant to look back from.</param>
    /// <param name="symbolRoot">Product root, so per-product session lists apply.</param>
    public DateTime? MostRecentOpenUtc(string sessionName, DateTime utc, string symbolRoot)
    {
        DateTime? mostRecent = null;

        foreach (var window in this.WindowsBetween(utc.AddDays(-4), utc, symbolRoot))
        {
            if (!string.Equals(window.Definition.Name, sessionName,
                               StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (window.OpenUtc > utc)
                continue;

            if (mostRecent is null || window.OpenUtc > mostRecent.Value)
                mostRecent = window.OpenUtc;
        }

        return mostRecent;
    }

    public SessionWindow? ActiveWindow(DateTime utc, string symbolRoot)
        => this.ContainingWindows(utc, symbolRoot).FirstOrDefault();

    /// <summary>
    /// Every window containing an instant, most significant first, by the precedence
    /// documented on <see cref="ActiveWindow"/>.
    ///
    /// Context windows do not disappear because they lost precedence — the initial balance
    /// still supplies its extension targets while the regular session is the active one —
    /// so callers that need the full picture ask for it here.
    /// </summary>
    public IReadOnlyList<SessionWindow> ContainingWindows(DateTime utc, string symbolRoot)
        => this.WindowsAround(utc, symbolRoot)
            .Where(w => w.Contains(utc))
            .OrderByDescending(w => w.Definition.EntriesAllowed)
            .ThenByDescending(w => w.OpenUtc)
            .ThenBy(w => w.OrHardCloseUtc - w.OpenUtc)
            .ThenBy(w => w.Definition.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Builds one occurrence. The trading window runs until the next applicable session
    /// opens.
    ///
    /// The specification defines session opens and opening-range lengths but never states
    /// when a session stops being current. Rather than invent a duration, the window is
    /// closed by the next configured open for the same product, which is self-consistent
    /// and introduces no number that is not already in the configuration. Where a session
    /// is the only one configured for a product, its window runs a full day.
    /// </summary>
    private SessionWindow BuildWindow(
        SessionDefinition definition, DateOnly localDate, IReadOnlyList<SessionDefinition> applicable)
    {
        var openUtc = this.OpenInstantUtc(definition, localDate);
        var orHardCloseUtc = openUtc + definition.OrLength;
        var endUtc = this.NextOpenAfter(openUtc, definition, localDate, applicable);

        // A range can never outlive the session that owns it, however the two were configured.
        if (orHardCloseUtc > endUtc)
            orHardCloseUtc = endUtc;

        return new SessionWindow(definition, localDate, openUtc, orHardCloseUtc, endUtc);
    }

    private DateTime NextOpenAfter(
        DateTime openUtc,
        SessionDefinition current,
        DateOnly localDate,
        IReadOnlyList<SessionDefinition> applicable)
    {
        var best = DateTime.MaxValue;

        for (var dayOffset = 0; dayOffset <= 1; dayOffset++)
        {
            var date = localDate.AddDays(dayOffset);

            foreach (var candidate in applicable)
            {
                if (ReferenceEquals(candidate, current))
                    continue;

                var candidateOpen = this.OpenInstantUtc(candidate, date);

                if (candidateOpen > openUtc && candidateOpen < best)
                    best = candidateOpen;
            }

            // The same session recurring the next day always bounds the window.
            if (dayOffset == 1)
            {
                var nextOwnOpen = this.OpenInstantUtc(current, date);
                if (nextOwnOpen > openUtc && nextOwnOpen < best)
                    best = nextOwnOpen;
            }
        }

        return best == DateTime.MaxValue ? openUtc.AddDays(1) : best;
    }
}
