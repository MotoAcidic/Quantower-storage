using System;

namespace OceansAnchor
{
    /// <summary>
    /// Session windows, all in Houston time. There is no second zone anywhere in this
    /// indicator -- not in the settings, not in the math, not on a label.
    ///
    /// Safe for CME equity index because Central and Eastern shift for DST on the same dates,
    /// so a Central-pinned RTH window tracks the exchange all year.
    /// </summary>
    public sealed class SessionConfig
    {
        /// <summary>Regular hours. The profile that produces the prior-RTH POC.</summary>
        public TimeSpan RthStart = new TimeSpan(8, 30, 0);
        public TimeSpan RthEnd = new TimeSpan(15, 0, 0);

        /// <summary>Globex reopen. Bars at or after this belong to the NEXT trade date.</summary>
        public TimeSpan EthStart = new TimeSpan(17, 0, 0);

        /// <summary>Globex close. Bars before this belong to their own day's trade date.</summary>
        public TimeSpan EthEnd = new TimeSpan(16, 0, 0);
    }

    public static class SessionScan
    {
        /// <summary>
        /// Which CME trade date a Houston timestamp belongs to. 17:00 Sunday is Monday's
        /// session; 16:00-17:00 is the daily break and belongs to neither, so those bars are
        /// dropped rather than smeared into a neighbouring day.
        ///
        /// Ported from oceans-pivot-decoder.
        /// </summary>
        public static DateTime? TradeDateOf(DateTime local, SessionConfig cfg)
        {
            DateTime date;

            if (local.TimeOfDay >= cfg.EthStart) date = local.Date.AddDays(1);
            else if (local.TimeOfDay < cfg.EthEnd) date = local.Date;
            else return null;

            // A Saturday or Sunday trade date can only come from bad data or a broken feed
            // clock; roll it onto Monday rather than inventing a weekend session.
            if (date.DayOfWeek == DayOfWeek.Saturday) date = date.AddDays(2);
            else if (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);

            return date;
        }

        /// <summary>
        /// Time-of-day containment, wrap-aware so an overnight window still works.
        /// </summary>
        public static bool InWindow(TimeSpan tod, TimeSpan start, TimeSpan end)
        {
            if (end > start) return tod >= start && tod < end;
            return tod >= start || tod < end;
        }

        /// <summary>
        /// Whether a bar spanning <paramref name="duration"/> lies WHOLLY inside the window, so
        /// its high and low are beyond question.
        /// </summary>
        public static bool Contains(TimeSpan tod, TimeSpan duration, TimeSpan start, TimeSpan end)
        {
            if (duration <= TimeSpan.Zero) return InWindow(tod, start, end);
            if (duration > BarMath.Length(start, end)) return false;

            var day = TimeSpan.FromDays(1);
            var last = TimeSpan.FromTicks((tod + duration - TimeSpan.FromTicks(1)).Ticks % day.Ticks);

            return InWindow(tod, start, end) && InWindow(last, start, end);
        }

        /// <summary>
        /// Whether a bar SPANNING <paramref name="duration"/> touches the window at all.
        ///
        /// This is the difference between a 5-minute chart and a 1-hour chart. A bar carries one
        /// timestamp -- its open -- and testing only that stamp throws away every bar that
        /// starts before the window and runs into it. RTH opening at 08:30 means the 1-hour bar
        /// stamped 08:00 would be discarded whole, losing 08:30-09:00: the cash open, the
        /// highest-volume half hour of the day, and very often the session POC itself.
        ///
        /// Ported from oceans-pivot-decoder.
        /// </summary>
        public static bool Overlaps(TimeSpan tod, TimeSpan duration, TimeSpan start, TimeSpan end)
        {
            if (duration <= TimeSpan.Zero) return InWindow(tod, start, end);

            var day = TimeSpan.FromDays(1);
            if (duration >= day) return true;

            // Walk the bar in slices no coarser than the window, so a bar longer than the window
            // cannot straddle it undetected.
            var step = BarMath.Length(start, end);
            if (step > duration) step = duration;
            if (step <= TimeSpan.Zero) return InWindow(tod, start, end);

            for (var offset = TimeSpan.Zero; offset < duration; offset += step)
            {
                var at = TimeSpan.FromTicks((tod + offset).Ticks % day.Ticks);
                if (InWindow(at, start, end)) return true;
            }

            var last = TimeSpan.FromTicks((tod + duration - TimeSpan.FromTicks(1)).Ticks % day.Ticks);
            return InWindow(last, start, end);
        }

        /// <summary>
        /// Whether a window is meaningful at this bar size. One bar has one high and one low, so
        /// a window shorter than a bar cannot have its own profile.
        /// </summary>
        public static bool Resolvable(TimeSpan barDuration, TimeSpan start, TimeSpan end)
        {
            if (barDuration <= TimeSpan.Zero) return true;
            return barDuration < BarMath.Length(start, end);
        }

        /// <summary>
        /// Overnight is everything in the trade date that is not regular hours: the Globex
        /// reopen through the cash open. Wrap-aware by construction because it is the
        /// complement of RTH inside a trade date that already wrapped.
        /// </summary>
        public static bool IsOvernight(TimeSpan tod, SessionConfig cfg)
        {
            return !InWindow(tod, cfg.RthStart, cfg.RthEnd);
        }
    }
}
