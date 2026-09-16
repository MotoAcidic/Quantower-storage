using System;
using System.ComponentModel.DataAnnotations;

namespace OceansDeveloped
{
    /// <summary>
    /// The completed periods this indicator draws. Every one of them is finished business --
    /// the period in progress is never profiled, which is the whole point of a "developed"
    /// profile: the levels stop moving, so they are worth marking.
    /// </summary>
    public enum PeriodKind
    {
        [Display(Name = "Previous day")] PrevDay,
        [Display(Name = "Previous week")] PrevWeek,
        [Display(Name = "Previous month")] PrevMonth,
        [Display(Name = "Rolling days (A)")] RollingA,
        [Display(Name = "Rolling days (B)")] RollingB
    }

    /// <summary>
    /// Turns bar times into futures trade dates, and trade dates into the week and month keys
    /// the composite periods are cut on.
    ///
    /// Times in are already on the trader's clock -- Central, one zone everywhere. Whether ATAS
    /// stamps bars UTC or local is resolved from the data by <see cref="TimeContext"/>, never
    /// assumed, because a whole-day shift here does not look wrong: it produces a clean,
    /// plausible profile of the wrong session.
    /// </summary>
    public static class DevelopedClock
    {
        /// <summary>The futures day rolls at 5 PM Central -- that is when CME reopens.</summary>
        public static readonly TimeSpan DayRoll = new TimeSpan(17, 0, 0);

        /// <summary>
        /// The trade date a bar belongs to, as a date at midnight. A bar at or after 5 PM is
        /// already in the session that settles the NEXT calendar day, so it carries that date.
        ///
        /// Cutting at midnight instead would split every overnight session in two and put the
        /// hours either side of it in different profiles.
        /// </summary>
        public static DateTime TradeDate(DateTime local)
        {
            return local.TimeOfDay >= DayRoll
                 ? local.Date.AddDays(1)
                 : local.Date;
        }

        /// <summary>The instant a trade date opens: 5 PM on the previous calendar day.</summary>
        public static DateTime OpenOf(DateTime tradeDate)
        {
            return tradeDate.Date.AddDays(-1).Add(DayRoll);
        }

        /// <summary>The instant a trade date closes: 5 PM on the trade date itself.</summary>
        public static DateTime CloseOf(DateTime tradeDate)
        {
            return tradeDate.Date.Add(DayRoll);
        }

        /// <summary>
        /// The Monday of the week a trade date falls in. The futures week opens Sunday at 5 PM,
        /// which is already Monday's trade date, so a Monday-based week needs no special case
        /// for the Sunday reopen -- it lands in the right week by construction.
        /// </summary>
        public static DateTime WeekOf(DateTime tradeDate)
        {
            var date = tradeDate.Date;
            var offset = ((int)date.DayOfWeek + 6) % 7;   // Monday = 0

            return date.AddDays(-offset);
        }

        /// <summary>The first of the month a trade date falls in.</summary>
        public static DateTime MonthOf(DateTime tradeDate)
        {
            return new DateTime(tradeDate.Year, tradeDate.Month, 1);
        }

        /// <summary>
        /// The grouping key for a trade date under a calendar-anchored period. Two trade dates
        /// belong to the same instance of that period exactly when this returns the same value,
        /// so it doubles as the cache key.
        ///
        /// The rolling kinds have no key -- they are a window of the last N trade dates, not a
        /// partition of the calendar -- and are handled separately.
        /// </summary>
        public static DateTime KeyOf(DateTime tradeDate, PeriodKind kind)
        {
            switch (kind)
            {
                case PeriodKind.PrevWeek: return WeekOf(tradeDate);
                case PeriodKind.PrevMonth: return MonthOf(tradeDate);
                default: return tradeDate.Date;
            }
        }

        /// <summary>Whether a period kind partitions the calendar rather than rolling.</summary>
        public static bool IsCalendar(PeriodKind kind)
        {
            return kind == PeriodKind.PrevDay
                || kind == PeriodKind.PrevWeek
                || kind == PeriodKind.PrevMonth;
        }

        /// <summary>
        /// The first instant a calendar period covers -- used to decide whether the loaded
        /// history actually reaches back far enough to have profiled all of it.
        /// </summary>
        public static DateTime StartInstant(DateTime key, PeriodKind kind)
        {
            switch (kind)
            {
                case PeriodKind.PrevWeek: return OpenOf(key);              // Sunday 5 PM
                case PeriodKind.PrevMonth: return OpenOf(key);             // 5 PM before the 1st
                default: return OpenOf(key);                               // 5 PM before the day
            }
        }

        /// <summary>
        /// The instant a calendar period stops covering, exclusive -- which is the next
        /// period's opening reopen, not the last session's close. The weekend and the month-end
        /// evening hold no trade dates either way, so both readings contain exactly the same
        /// sessions; running to the reopen is what makes consecutive periods tile with no gap
        /// and no overlap, so no session can fall outside all of them or inside two.
        /// </summary>
        public static DateTime EndInstant(DateTime key, PeriodKind kind)
        {
            switch (kind)
            {
                case PeriodKind.PrevWeek: return CloseOf(key.AddDays(6));
                case PeriodKind.PrevMonth: return CloseOf(key.AddMonths(1).AddDays(-1));
                default: return CloseOf(key);
            }
        }

        /// <summary>
        /// A short label for one instance, for the lane header and the ray tags. Kept to a few
        /// characters because it is printed against a price on a busy chart.
        /// </summary>
        public static string Tag(PeriodKind kind, int rollingDays)
        {
            switch (kind)
            {
                case PeriodKind.PrevDay: return "PD";
                case PeriodKind.PrevWeek: return "PW";
                case PeriodKind.PrevMonth: return "PM";
                default: return rollingDays + "D";
            }
        }

        /// <summary>The instance's own dates, spelled out for the lane header.</summary>
        public static string Describe(DateTime key, PeriodKind kind, int days)
        {
            switch (kind)
            {
                case PeriodKind.PrevWeek:
                    return key.ToString("d MMM") + "-" + key.AddDays(4).ToString("d MMM");

                case PeriodKind.PrevMonth:
                    return key.ToString("MMM yyyy");

                case PeriodKind.PrevDay:
                    return key.ToString("ddd d MMM");

                default:
                    return days + " days to " + key.ToString("d MMM");
            }
        }
    }
}
