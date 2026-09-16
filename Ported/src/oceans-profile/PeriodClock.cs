using System;
using System.ComponentModel.DataAnnotations;

namespace OceansProfile
{
    /// <summary>
    /// The higher timeframe the profiles are cut on. A real clock period, so it means the same
    /// thing whichever chart timeframe the indicator is dropped on.
    /// </summary>
    public enum ProfilePeriod
    {
        [Display(Name = "15 minutes")] M15,
        [Display(Name = "30 minutes")] M30,
        [Display(Name = "1 hour")] H1,
        [Display(Name = "2 hours")] H2,
        [Display(Name = "4 hours")] H4,
        [Display(Name = "Cash session / overnight")] Rth,
        [Display(Name = "Futures day (5 PM to 5 PM)")] Day
    }

    /// <summary>
    /// Cuts a stream of bars into higher-timeframe periods. Every period is a true partition --
    /// a bar belongs to exactly one -- so profiles never double-count a trade.
    ///
    /// Times in are already on the trader's clock. See <see cref="TimeContext"/>: whether ATAS
    /// stamps bars UTC or local is resolved from the data, never assumed.
    /// </summary>
    public static class PeriodClock
    {
        /// <summary>The futures day rolls at 5 PM, not midnight -- that is when CME reopens.</summary>
        public static readonly TimeSpan DayRoll = new TimeSpan(17, 0, 0);

        /// <summary>Cash session, Houston time.</summary>
        public static readonly TimeSpan RthOpen = new TimeSpan(8, 30, 0);
        public static readonly TimeSpan RthClose = new TimeSpan(15, 0, 0);

        /// <summary>
        /// Whether the period's boundaries move with the time zone. Quarter-hours, half-hours
        /// and hours land on the same instant whatever whole-hour offset is applied, so they are
        /// safe to cut even when the bar clock could not be established. Everything coarser is
        /// not, and the indicator refuses to guess rather than drawing a shifted profile.
        /// </summary>
        public static bool NeedsTimeZone(ProfilePeriod period)
        {
            return period != ProfilePeriod.M15
                && period != ProfilePeriod.M30
                && period != ProfilePeriod.H1;
        }

        /// <summary>
        /// The start of the period a time falls in. Two bars share a period exactly when this
        /// returns the same instant for both, so it doubles as the grouping key.
        /// </summary>
        public static DateTime StartOf(DateTime local, ProfilePeriod period)
        {
            switch (period)
            {
                case ProfilePeriod.M15: return FloorMinutes(local, 15);
                case ProfilePeriod.M30: return FloorMinutes(local, 30);
                case ProfilePeriod.H1: return FloorMinutes(local, 60);
                case ProfilePeriod.H2: return FloorMinutes(local, 120);
                case ProfilePeriod.H4: return FloorMinutes(local, 240);
                case ProfilePeriod.Rth: return StartOfSession(local);
                case ProfilePeriod.Day: return StartOfDay(local);
            }

            return FloorMinutes(local, 60);
        }

        private static DateTime FloorMinutes(DateTime local, int minutes)
        {
            var date = local.Date;
            var since = (int)(local - date).TotalMinutes;
            var floored = since - since % minutes;

            return date.AddMinutes(floored);
        }

        /// <summary>
        /// The futures day runs 5 PM to 5 PM. A bar at or after 5 PM belongs to the session that
        /// is only just opening, which is the NEXT calendar day's trade date.
        /// </summary>
        private static DateTime StartOfDay(DateTime local)
        {
            var date = local.Date;

            return local.TimeOfDay >= DayRoll
                 ? date.Add(DayRoll)
                 : date.AddDays(-1).Add(DayRoll);
        }

        /// <summary>
        /// Two blocks that tile the day without overlapping: the cash session, and the overnight
        /// stretch either side of it. An overnight block is keyed to the close that opened it,
        /// so the hours before and after midnight land in one profile rather than two.
        /// </summary>
        private static DateTime StartOfSession(DateTime local)
        {
            var date = local.Date;
            var time = local.TimeOfDay;

            if (time >= RthOpen && time < RthClose) return date.Add(RthOpen);
            if (time >= RthClose) return date.Add(RthClose);

            return date.AddDays(-1).Add(RthClose);
        }

        /// <summary>Whether a time falls inside the cash session.</summary>
        public static bool IsCash(DateTime local)
        {
            var time = local.TimeOfDay;
            return time >= RthOpen && time < RthClose;
        }

        /// <summary>A short label for the period, for the column header.</summary>
        public static string Label(DateTime start, ProfilePeriod period)
        {
            switch (period)
            {
                case ProfilePeriod.Day:
                    return start.AddDays(1).ToString("ddd d MMM");

                case ProfilePeriod.Rth:
                    return IsCash(start.Add(new TimeSpan(0, 1, 0)))
                         ? start.ToString("ddd") + " cash"
                         : start.ToString("ddd") + " o/n";

                default:
                    return start.ToString("HH:mm");
            }
        }
    }
}
