using System;
using System.ComponentModel.DataAnnotations;

namespace OceansRead
{
    /// <summary>Which slice of the futures day the session profile is cut from.</summary>
    public enum SessionScope
    {
        /// <summary>The whole futures day: 5 PM reopen to the 4 PM halt.</summary>
        [Display(Name = "Whole futures day (17:00-16:00)")] FuturesDay,

        /// <summary>Cash hours only, where the volume that sets value actually trades.</summary>
        [Display(Name = "Cash hours only (08:30-15:00)")] RegularHours
    }

    /// <summary>The anchors a top-down VWAP stack is measured from, longest first.</summary>
    public enum VwapAnchor
    {
        [Display(Name = "Yearly")] Year,
        [Display(Name = "Quarterly")] Quarter,
        [Display(Name = "Monthly")] Month,
        [Display(Name = "Weekly")] Week,
        [Display(Name = "Daily")] Day,
        [Display(Name = "Session")] Session
    }

    /// <summary>
    /// Futures dates and session windows, in one time zone. Everything here takes a time that
    /// is already on the trader's clock -- Central, always -- because a boundary compared in
    /// the wrong zone does not fail, it silently profiles the wrong hours.
    ///
    /// The one-hour CME maintenance break is 16:00-17:00 Central. 15:00 is the cash close, when
    /// trading carries straight on; using it as the halt has shipped wrong three times across
    /// these projects and the symptom is an indicator that draws nothing at all.
    /// </summary>
    public static class ReadClock
    {
        /// <summary>The futures day rolls at 5 PM Central -- that is when CME reopens.</summary>
        public static readonly TimeSpan DayRoll = new TimeSpan(17, 0, 0);

        /// <summary>The hour the daily maintenance break starts, Central.</summary>
        public const int HaltHour = 16;

        /// <summary>Cash open, Central. Houston time, never quoted in Eastern.</summary>
        public static readonly TimeSpan RegularOpen = new TimeSpan(8, 30, 0);

        /// <summary>Cash close, Central.</summary>
        public static readonly TimeSpan RegularClose = new TimeSpan(15, 0, 0);

        /// <summary>The last hour of cash trade, where the day's positioning is settled.</summary>
        public static readonly TimeSpan PowerHour = new TimeSpan(14, 0, 0);

        /// <summary>
        /// The trade date a bar belongs to. A bar at or after 5 PM is already in the session
        /// that settles the NEXT calendar day, so it carries that date. Cutting at midnight
        /// instead splits every overnight session in two.
        /// </summary>
        public static DateTime TradeDate(DateTime local)
        {
            return local.TimeOfDay >= DayRoll ? local.Date.AddDays(1) : local.Date;
        }

        /// <summary>The instant a trade date opens: 5 PM on the previous calendar day.</summary>
        public static DateTime OpenOf(DateTime tradeDate) => tradeDate.Date.AddDays(-1).Add(DayRoll);

        /// <summary>The instant a trade date closes: 4 PM on the trade date itself.</summary>
        public static DateTime CloseOf(DateTime tradeDate) => tradeDate.Date.AddHours(HaltHour);

        /// <summary>Cash open on a trade date.</summary>
        public static DateTime RegularOpenOf(DateTime tradeDate) => tradeDate.Date.Add(RegularOpen);

        /// <summary>Cash close on a trade date.</summary>
        public static DateTime RegularCloseOf(DateTime tradeDate) => tradeDate.Date.Add(RegularClose);

        /// <summary>Whether a bar time falls inside the chosen session scope.</summary>
        public static bool InScope(DateTime local, SessionScope scope)
        {
            if (scope == SessionScope.FuturesDay)
                return local.TimeOfDay < new TimeSpan(HaltHour, 0, 0) || local.TimeOfDay >= DayRoll;

            return local.TimeOfDay >= RegularOpen && local.TimeOfDay < RegularClose;
        }

        /// <summary>Cash hours, regardless of the profile scope -- the initial balance needs them.</summary>
        public static bool InRegularHours(DateTime local)
        {
            return local.TimeOfDay >= RegularOpen && local.TimeOfDay < RegularClose;
        }

        /// <summary>The overnight run-up to a trade date: its 5 PM reopen to the cash open.</summary>
        public static bool InOvernight(DateTime local)
        {
            return local.TimeOfDay >= DayRoll || local.TimeOfDay < RegularOpen;
        }

        /// <summary>The last hour of cash trade.</summary>
        public static bool InPowerHour(DateTime local)
        {
            return local.TimeOfDay >= PowerHour && local.TimeOfDay < RegularClose;
        }

        /// <summary>
        /// The Monday of the week a trade date falls in. The futures week opens Sunday at 5 PM,
        /// which already carries Monday's trade date, so a Monday-based week needs no special
        /// case for the Sunday reopen -- it lands in the right week by construction.
        /// </summary>
        public static DateTime WeekOf(DateTime tradeDate)
        {
            var date = tradeDate.Date;
            var offset = ((int)date.DayOfWeek + 6) % 7;   // Monday = 0

            return date.AddDays(-offset);
        }

        public static DateTime MonthOf(DateTime tradeDate) => new DateTime(tradeDate.Year, tradeDate.Month, 1);

        public static DateTime QuarterOf(DateTime tradeDate)
        {
            var first = ((tradeDate.Month - 1) / 3) * 3 + 1;

            return new DateTime(tradeDate.Year, first, 1);
        }

        public static DateTime YearOf(DateTime tradeDate) => new DateTime(tradeDate.Year, 1, 1);

        /// <summary>
        /// The key that says which instance of an anchor period a trade date belongs to. A new
        /// key means the accumulator resets: two bars share a VWAP exactly when this matches.
        /// </summary>
        public static DateTime AnchorKey(DateTime tradeDate, VwapAnchor anchor)
        {
            switch (anchor)
            {
                case VwapAnchor.Year: return YearOf(tradeDate);
                case VwapAnchor.Quarter: return QuarterOf(tradeDate);
                case VwapAnchor.Month: return MonthOf(tradeDate);
                case VwapAnchor.Week: return WeekOf(tradeDate);
                default: return tradeDate.Date;
            }
        }

        /// <summary>Two or three characters, because these print against a price on a busy chart.</summary>
        public static string Tag(VwapAnchor anchor)
        {
            switch (anchor)
            {
                case VwapAnchor.Year: return "yV";
                case VwapAnchor.Quarter: return "qV";
                case VwapAnchor.Month: return "mV";
                case VwapAnchor.Week: return "wV";
                case VwapAnchor.Day: return "dV";
                default: return "sV";
            }
        }
    }
}
