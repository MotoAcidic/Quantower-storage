using System;
using System.Collections.Generic;

namespace OceansAsiaWick
{
    /// <summary>Why an entry was refused, or a flatten forced. Printed, logged, and asserted on.</summary>
    public enum GuardReason
    {
        None = 0,

        /// <summary>Clock unresolved -- every level would be plausible and wrong.</summary>
        ClockUnresolved,

        /// <summary>Chart timeframe outside the designed range.</summary>
        BadTimeframe,

        /// <summary>Friday trade date: no new exposure heading into the weekend.</summary>
        FridayTradeDate,

        /// <summary>Weekly close is near; nothing carries over the weekend.</summary>
        WeekendClose,

        /// <summary>Tier-1 release this morning -- The funded-account rules require flat before it.</summary>
        Tier1News,

        /// <summary>Nightly loss cutoff hit.</summary>
        LossLimit,

        /// <summary>The configured exit time has arrived.</summary>
        ExitTime,

        /// <summary>Already traded (or already lost) tonight.</summary>
        NightSpent,

        /// <summary>Order rejected earlier tonight; stand down.</summary>
        Disabled,

        /// <summary>Signal-only mode: everything runs, nothing is sent.</summary>
        SignalOnly
    }

    /// <summary>
    /// Execution limits. Separate from <see cref="AsiaWickSettings"/> because the indicator has
    /// no use for any of it, and because these are the rules that cost money when wrong -- they
    /// are tested on their own.
    /// </summary>
    public sealed class RiskSettings
    {
        public int Quantity = 1;
        public int StopTicks = 40;

        /// <summary>Ships ON. Everything runs; no order is ever sent.</summary>
        public bool SignalOnly = true;

        public bool BlockFriday = true;

        /// <summary>
        /// Belt and braces. Every shipped exit (02:00 / 08:30 / 15:00) already lands before the
        /// Friday 16:00 weekly close, so under stock settings this never fires -- it is here so
        /// that a widened exit cannot quietly create weekend exposure.
        /// </summary>
        public TimeSpan WeekendFlatAt = new TimeSpan(15, 55, 0);

        public bool Tier1Guard = true;

        /// <summary>The funded-account rules want flat 2 minutes out; the 07:30 CT releases give 07:28.</summary>
        public TimeSpan Tier1FlatAt = new TimeSpan(7, 28, 0);

        /// <summary>Comma-separated yyyy-MM-dd, each the calendar date of a release MORNING.</summary>
        public string Tier1Dates = "";

        public decimal NightlyLossLimit = 500m;
    }

    /// <summary>
    /// Every "may I?" and "must I get out?" decision, as pure functions of the bar clock and the
    /// night's state. The strategy holds the position; this holds the rules.
    /// </summary>
    public static class NightGuard
    {
        /// <summary>
        /// A tier-1 release lands on the MORNING of a night's session. The night of trade date D
        /// runs into the morning of D+1, so that morning is the date to look up.
        /// </summary>
        public static bool IsTier1Morning(DateTime ct, HashSet<DateTime> tier1)
        {
            return tier1 != null && tier1.Contains(ct.Date);
        }

        /// <summary>Does the night of this trade date run into a listed release morning?</summary>
        public static bool NightRunsIntoTier1(DateTime tradeDate, HashSet<DateTime> tier1)
        {
            return tier1 != null && tier1.Contains(tradeDate.Date.AddDays(1));
        }

        /// <summary>
        /// Must the position be flat as of this closed bar? Checked before anything else on
        /// every bar, whether or not a position is actually open.
        /// </summary>
        public static GuardReason FlattenReason(DateTime ct, TimeSpan exitAt, RiskSettings r,
                                                HashSet<DateTime> tier1, bool lossHit)
        {
            if (lossHit) return GuardReason.LossLimit;

            if (r.Tier1Guard && IsTier1Morning(ct, tier1) && ct.TimeOfDay >= r.Tier1FlatAt)
                return GuardReason.Tier1News;

            if (r.BlockFriday && ct.DayOfWeek == DayOfWeek.Friday &&
                ct.TimeOfDay >= r.WeekendFlatAt)
                return GuardReason.WeekendClose;

            // The exit lives in the morning, after the Asia window has closed. Comparing on
            // time-of-day alone would also match the same clock time during the evening session
            // of the NEXT night, so the Asia window is excluded explicitly.
            if (ct.TimeOfDay >= exitAt && !InEveningLeg(ct.TimeOfDay, exitAt))
                return GuardReason.ExitTime;

            return GuardReason.None;
        }

        /// <summary>
        /// True for the stretch from the 17:00 roll to midnight -- the part of a trade date that
        /// sits AFTER the morning exit on the clock but BEFORE it in the session.
        /// </summary>
        private static bool InEveningLeg(TimeSpan tod, TimeSpan exitAt)
        {
            return tod >= AsiaWickEngine.TradeDateRoll && exitAt < AsiaWickEngine.TradeDateRoll;
        }

        /// <summary>May a fresh entry be sent on this closed bar?</summary>
        public static GuardReason EntryBlock(DateTime ct, DateTime tradeDate, TimeSpan exitAt,
                                             RiskSettings r, HashSet<DateTime> tier1,
                                             bool armed, bool lossHit, bool disabled)
        {
            if (disabled) return GuardReason.Disabled;
            if (!armed) return GuardReason.NightSpent;

            // Ahead of the flatten reasons, which would otherwise answer WeekendClose for every
            // Friday-trade-date bar (they all fall after 17:00 Friday) and bury the real reason
            // in the log. Both block; only the more specific one explains why.
            if (r.BlockFriday && tradeDate.DayOfWeek == DayOfWeek.Friday)
                return GuardReason.FridayTradeDate;

            var flat = FlattenReason(ct, exitAt, r, tier1, lossHit);
            if (flat != GuardReason.None) return flat;

            if (r.SignalOnly) return GuardReason.SignalOnly;

            return GuardReason.None;
        }

        /// <summary>Short stop price for a signal. Above the signal bar's high by stopTicks.</summary>
        public static decimal StopPrice(decimal stopReference, int stopTicks, decimal tickSize)
        {
            return stopReference + stopTicks * tickSize;
        }
    }
}
