using System;

namespace OrbIx.Core.Sessions;

/// <summary>
/// When the market is open across the week, as a wall-clock span in the session time zone.
///
/// WHY THIS EXISTS
///   <see cref="SessionClock"/> built a window for EVERY calendar date. Nothing in it knew
///   that Saturday is not a trading day, so it produced Saturday opens, Sunday-morning opens
///   and Friday-evening opens after the close, and the fixed-range profile then defaulted to
///   one of them and found no bars. Measured on 2026-08-29: the profile's default resolved to
///   2026-08-29T13:30:00Z and 2026-08-29T22:00:00Z — both a Saturday.
///
/// WHERE THE BOUNDARIES COME FROM — MEASURED, not assumed. CME's own pages answered 403 to a
/// direct fetch and timed out through the summarising fetcher, so the week was derived from
/// data already on this machine:
///
///   * 28,848,885 MNQU6 prints, 2026-08-04 to 2026-08-28, counted by weekday:
///       Saturday  0 trades.
///       Sunday    trades only from 22:00Z.
///       Friday    trades end at 20:59Z.
///       Mon-Thu   every hour.
///   * Independently, 96 populated one data vendor history session files across three products
///     contain NO Saturday. That second source is what removes the doubt the live capture's
///     known outages would otherwise leave.
///
///   In the session zone (America/New_York, from sessionTimeZone) those instants are
///   Sunday 18:00 and Friday 17:00 — which is exactly the configured GLOBEX open of 18:00.
///
/// WHY WALL-CLOCK RATHER THAN UTC
///   Expressed as local times the rule needs no daylight-saving special case: 18:00 local is
///   the open in both halves of the year. The measurement above is entirely from DST months,
///   so the winter behaviour is reasoned rather than observed — which is precisely why the
///   values are configuration and not literals.
///
/// WHAT THIS DOES NOT DO
///   HOLIDAYS. A Thanksgiving or Christmas session is still emitted, because no exchange
///   holiday calendar exists here — the economic calendar in artifacts/ is FRED
///   macro releases and carries no market closures. Weekends are deterministic and need no
///   data; holidays need data we do not have. Stated here so the gap is visible in the code
///   rather than assumed handled, and pinned by a test.
/// </summary>
public sealed class TradingWeek
{
    private readonly int openMinuteOfWeek;
    private readonly int closeMinuteOfWeek;

    /// <param name="openDay">Day the trading week begins.</param>
    /// <param name="openTime">Wall-clock open on that day, in the session zone.</param>
    /// <param name="closeDay">Day the trading week ends.</param>
    /// <param name="closeTime">Wall-clock close on that day, in the session zone.</param>
    public TradingWeek(DayOfWeek openDay, TimeSpan openTime, DayOfWeek closeDay, TimeSpan closeTime)
    {
        if (openTime < TimeSpan.Zero || openTime >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(openTime), openTime, "Not a time of day.");

        if (closeTime < TimeSpan.Zero || closeTime >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(closeTime), closeTime, "Not a time of day.");

        this.OpenDay = openDay;
        this.OpenTime = openTime;
        this.CloseDay = closeDay;
        this.CloseTime = closeTime;

        this.openMinuteOfWeek = MinuteOfWeek(openDay, openTime);
        this.closeMinuteOfWeek = MinuteOfWeek(closeDay, closeTime);

        if (this.openMinuteOfWeek == this.closeMinuteOfWeek)
        {
            throw new ArgumentException(
                "The trading week opens and closes at the same instant, which describes either "
                + "a market that never opens or one that never closes. Neither is a week.",
                nameof(closeTime));
        }
    }

    public DayOfWeek OpenDay { get; }

    public TimeSpan OpenTime { get; }

    public DayOfWeek CloseDay { get; }

    public TimeSpan CloseTime { get; }

    /// <summary>
    /// Is this session-zone wall-clock instant inside the trading week?
    ///
    /// The comparison is on minute-of-week so a week that wraps past Sunday midnight —
    /// which the CME schedule does, opening Sunday evening — needs no separate branch.
    /// </summary>
    public bool Contains(DateTime sessionLocal)
    {
        var minute = MinuteOfWeek(sessionLocal.DayOfWeek, sessionLocal.TimeOfDay);

        return this.openMinuteOfWeek < this.closeMinuteOfWeek
            ? minute >= this.openMinuteOfWeek && minute < this.closeMinuteOfWeek
            : minute >= this.openMinuteOfWeek || minute < this.closeMinuteOfWeek;
    }

    /// <summary>
    /// The most recent week open at or before a session-zone wall-clock instant.
    ///
    /// WHAT IT IS FOR: anchoring a weekly VWAP. The line has to start where the trading week
    /// started, not where the calendar week did — the CME week opens Sunday evening, so a
    /// calendar-week anchor would miss the Sunday session entirely and restart the line in the
    /// middle of Monday's.
    ///
    /// WALL-CLOCK IN, WALL-CLOCK OUT. The caller converts, exactly as every other boundary here
    /// does: 18:00 local is the open in both halves of the year, so expressing the rule in local
    /// time needs no daylight-saving special case. Converting inside would need a zone this type
    /// deliberately does not hold.
    ///
    /// AN INSTANT EXACTLY ON THE OPEN RETURNS ITSELF, because the open belongs to the week it
    /// opens. Rounding it back a week would put the first print of a session on the previous
    /// week's line.
    ///
    /// HOLIDAYS ARE NOT HANDLED, for the reason stated on this type: weekends are deterministic
    /// and holidays need a calendar that does not exist here. A holiday Sunday still reports an
    /// open, and the line simply has nothing to accumulate until trading resumes.
    /// </summary>
    /// <param name="sessionLocal">A session-zone wall-clock instant.</param>
    public DateTime MostRecentOpenLocal(DateTime sessionLocal)
    {
        // Days back to the open DAY, then the time of day decides whether that is this week's
        // open or last week's. Both are computed on the same date so the subtraction cannot
        // cross a boundary the comparison then re-crosses.
        var daysBack = ((int)sessionLocal.DayOfWeek - (int)this.OpenDay + 7) % 7;
        var candidate = sessionLocal.Date.AddDays(-daysBack) + this.OpenTime;

        return candidate <= sessionLocal ? candidate : candidate.AddDays(-7);
    }

    /// <summary>Wording for a log line or a test failure, so a rejection can be read.</summary>
    public string Describe()
        => $"{this.OpenDay} {this.OpenTime:hh\\:mm} to {this.CloseDay} {this.CloseTime:hh\\:mm}"
           + " session-zone time";

    private static int MinuteOfWeek(DayOfWeek day, TimeSpan timeOfDay)
        => ((int)day * 24 * 60) + (int)timeOfDay.TotalMinutes;
}
