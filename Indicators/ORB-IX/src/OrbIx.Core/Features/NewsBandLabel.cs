using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OrbIx.Core.Features;

/// <summary>
/// What a news band says, written to be read in one glance mid-session.
///
/// WHAT IT REPLACED, AND WHY. The band used to read
/// "Employment Situation (Employment Report) · rule: Standard". Every word of that was
/// deliberate: it is the Bureau of Labor Statistics' own title for the release everyone
/// calls NFP, extended so the firm's Tier-1 substring match fires without colliding with
/// the ADP release, a false match already recorded as a trap. It is load-bearing for
/// whether this account may trade the window.
///
/// It also answered neither question a person actually has at 08:30 — WHEN, in my own
/// clock, and HOW MUCH does this matter — and it was read off a live chart as noise. The
/// name was a matcher key doing a label's job. It stays a matcher key;
/// <see cref="EconomicEvent.Label"/> is the half written for a human.
///
/// THE DOTS ARE A CLASSIFICATION, NEVER A MEASUREMENT. They render
/// <see cref="EconomicEvent.Impact"/>, which is resolved from CONFIGURATION — an explicit
/// value, else impactByType for the source type, else High as the fail-safe — alongside
/// whether the firm calls the event Tier 1. NOTHING HERE SAYS HOW FAR THE MARKET MOVES,
/// and nothing in this repository has measured that. A measured version was considered and
/// refused with a reason: the tick archive spans about four months, so there are three or
/// four instances of any single release, and n=3 is not an estimate. If a later reader is
/// tempted to cite these dots as expected range, this paragraph is the answer.
///
/// THIS LIVES IN CORE SO IT CAN BE TESTED. The test project references OrbIx.Core and
/// OrbIx.Replay and NOT the indicator, which is why the fill-journal guards have to read
/// the indicator's source as text to assert anything about it. Pure string and time logic
/// belongs on this side of that line.
/// </summary>
public static class NewsBandLabel
{
    /// <summary>Composes the label for one event.</summary>
    /// <param name="economicEvent">The release.</param>
    /// <param name="zone">
    /// The zone to show the time in — the SESSION CLOCK's, never a second lookup, so this
    /// cannot disagree with the rest of the panel about which day an instant belongs to.
    /// Null omits the time rather than guessing UTC, because a time shown in the wrong
    /// zone is worse than no time at all: one is unreadable, the other is misleading.
    /// </param>
    /// <param name="nowUtc">Now, for the countdown.</param>
    /// <param name="newsRule">The account's news rule, or empty when unresolved.</param>
    public static string Compose(
        EconomicEvent economicEvent, TimeZoneInfo? zone, DateTime nowUtc, string newsRule)
    {
        ArgumentNullException.ThrowIfNull(economicEvent);

        var parts = new List<string> { economicEvent.Label };

        if (zone is not null)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(economicEvent.TimeUtc, zone);

            parts.Add(string.Concat(
                local.ToString("HH:mm", CultureInfo.InvariantCulture),
                " ",
                Abbreviate(zone, economicEvent.TimeUtc)));
        }

        // ONLY AHEAD OF THE RELEASE. A countdown still running afterwards would be
        // counting up from something that has already happened, and the band stays on
        // screen for NewsMinutesAfter beyond the event.
        var until = economicEvent.TimeUtc - nowUtc;

        if (until > TimeSpan.Zero)
        {
            parts.Add(until.TotalHours >= 1
                ? FormattableString.Invariant(
                    $"in {(int)until.TotalHours}h {until.Minutes:00}m")
                : FormattableString.Invariant($"in {until.Minutes}m"));
        }

        var dots = economicEvent.Impact switch
        {
            EventImpact.High => "●●●",
            EventImpact.Medium => "●●◦",
            _ => "●◦◦",
        };

        parts.Add(economicEvent.Tier1 ? dots + " Tier-1" : dots);
        parts.Add("rule: " + (newsRule.Length > 0 ? newsRule : "unknown"));

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A short name for a zone, taken from the platform's own naming.
    ///
    /// DERIVED, NEVER HARDCODED. "ET" is right for a chart configured to New York and
    /// wrong for one configured to London, and sessionTimeZone is a setting rather than a
    /// constant. The initials of the zone's own daylight or standard name yield EDT, EST,
    /// GMT and so on without this code knowing any geography.
    ///
    /// A zone whose name gives fewer than two initials falls back to the numeric offset,
    /// which is uglier and always true. Reporting "UTC+05:45" is a worse label and a
    /// better answer than confidently printing the wrong three letters.
    /// </summary>
    public static string Abbreviate(TimeZoneInfo zone, DateTime instantUtc)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTimeFromUtc(instantUtc, zone);
        var name = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;

        var initials = new string((name ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => char.IsUpper(word[0]))
            .Select(word => word[0])
            .ToArray());

        if (initials.Length >= 2)
            return initials;

        var offset = zone.GetUtcOffset(instantUtc);

        return FormattableString.Invariant(
            $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}");
    }
}
