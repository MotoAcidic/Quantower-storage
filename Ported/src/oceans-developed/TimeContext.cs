using System;
using System.Collections.Generic;

namespace OceansDeveloped
{
    /// <summary>How to read the clock stamped on each chart bar.</summary>
    public enum BarClock
    {
        /// <summary>Work it out from the data, then show the answer on the chart.</summary>
        Auto = 0,

        /// <summary>Bars are stamped UTC and must be converted.</summary>
        Utc = 1,

        /// <summary>Bars are already stamped in your time zone; use as-is.</summary>
        AlreadyLocal = 2
    }

    /// <summary>Which signal established the bar clock.</summary>
    public enum ResolveMethod
    {
        None = 0,
        SetByHand,
        WallClock,
        DailyHalt
    }

    /// <summary>
    /// Puts every bar on your clock -- one time zone, Houston, everywhere. Session windows are
    /// entered in it, the math runs in it, and the labels print it, so nothing ever has to be
    /// shifted by hand.
    ///
    /// Every level this indicator draws is anchored to a clock time, so a wrong offset does not
    /// look wrong: it produces a clean, plausible, silently misplaced line. So this never falls
    /// back to a guess. It either establishes the bar clock from evidence or reports
    /// <see cref="Error"/>, and the indicator draws that message instead of levels.
    /// </summary>
    public sealed class TimeContext
    {
        private TimeContext() { }

        public TimeZoneInfo Zone { get; private set; }
        public BarClock Clock { get; private set; }
        public ResolveMethod Method { get; private set; }
        public string Error { get; private set; }

        public bool Valid => Error == null;

        /// <summary>Plain-language account of how the bar clock was settled, for the readout.</summary>
        public string Explain
        {
            get
            {
                switch (Method)
                {
                    case ResolveMethod.SetByHand: return "set by hand";
                    case ResolveMethod.WallClock: return "matched to the clock";
                    case ResolveMethod.DailyHalt: return "found the daily halt";
                    default: return "unresolved";
                }
            }
        }

        private static TimeContext Fail(string error) => new TimeContext { Error = error };

        public static TimeContext Create(string zoneId, BarClock requested, IBarWindow bars,
                                         DateTime nowUtc, int haltHour)
        {
            TimeZoneInfo zone;

            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (Exception ex)
            {
                return Fail($"Ocean Developed: no such time zone '{zoneId}' ({ex.Message}). " +
                            "Houston is 'Central Standard Time'.");
            }

            var ctx = new TimeContext { Zone = zone };

            if (requested != BarClock.Auto)
            {
                ctx.Clock = requested;
                ctx.Method = ResolveMethod.SetByHand;
                return ctx;
            }

            if (bars == null || bars.Count == 0)
                return Fail("Ocean Developed: no bars loaded yet.");

            var found = ByWallClock(bars, zone, nowUtc, out var method)
                     ?? ByDailyHalt(bars, zone, haltHour, out method);

            if (found == null)
                return Fail("Ocean Developed: could not tell whether bar times are UTC or " +
                            "already Houston time. Set 'Bar clock' by hand, then check the " +
                            "last-bar time in this line against your own clock.");

            ctx.Clock = found.Value;
            ctx.Method = method;
            return ctx;
        }

        /// <summary>
        /// On a live chart the newest bar is minutes old, and the two readings are five or six
        /// hours apart, so this settles it outright. Returns null on a stale chart -- a weekend,
        /// a replay, loaded history -- where the comparison proves nothing.
        /// </summary>
        private static BarClock? ByWallClock(IBarWindow bars, TimeZoneInfo zone,
                                             DateTime nowUtc, out ResolveMethod method)
        {
            method = ResolveMethod.WallClock;

            var last = bars.Time(bars.Count - 1);
            if (last == default || nowUtc == default) return null;

            // A bar's stamp is its OPEN, so the newest bar legitimately trails now by up to one
            // bar. Two hours is far tighter than the 5-6 h offset this has to tell apart.
            const double Tolerance = 2.0;

            var asUtc = Math.Abs((nowUtc - DateTime.SpecifyKind(last, DateTimeKind.Utc)).TotalHours);

            double asLocal;
            try
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(last, DateTimeKind.Unspecified), zone);
                asLocal = Math.Abs((nowUtc - utc).TotalHours);
            }
            catch
            {
                // Invalid local time (the spring-forward gap); this signal cannot decide.
                return null;
            }

            var utcFits = asUtc <= Tolerance;
            var localFits = asLocal <= Tolerance;

            // Exactly one reading may fit, or the test has told us nothing.
            if (utcFits == localFits) return null;

            return utcFits ? BarClock.Utc : BarClock.AlreadyLocal;
        }

        /// <summary>
        /// Independent of the wall clock: MNQ shuts for one hour every weekday afternoon
        /// (4-5 PM Houston). Under the right reading that hour is empty on every single day,
        /// while every other hour has bars. Under the wrong one the empty hour lands five or
        /// six hours off.
        /// </summary>
        private static BarClock? ByDailyHalt(IBarWindow bars, TimeZoneInfo zone,
                                             int haltHour, out ResolveMethod method)
        {
            method = ResolveMethod.DailyHalt;

            var utcHist = new int[24];
            var localHist = new int[24];
            var days = new HashSet<DateTime>();

            for (var i = 0; i < bars.Count; i++)
            {
                var t = bars.Time(i);
                if (t == default) continue;

                localHist[t.Hour]++;

                try
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.SpecifyKind(t, DateTimeKind.Utc), zone);
                    utcHist[local.Hour]++;
                    days.Add(local.Date);
                }
                catch { }
            }

            // Needs enough days for "empty every day" to mean anything.
            if (days.Count < 3) return null;

            var utcHit = EmptiestHour(utcHist) == haltHour;
            var localHit = EmptiestHour(localHist) == haltHour;

            if (utcHit == localHit) return null;

            return utcHit ? BarClock.Utc : BarClock.AlreadyLocal;
        }

        /// <summary>
        /// The emptiest hour, but only when it is decisively empty -- under a tenth of the
        /// median hour. A merely quiet hour is not a halt and must not be read as one.
        /// </summary>
        private static int EmptiestHour(int[] hist)
        {
            var sorted = (int[])hist.Clone();
            Array.Sort(sorted);
            var median = sorted[12];
            if (median <= 0) return -1;

            var minHour = 0;
            for (var h = 1; h < 24; h++)
                if (hist[h] < hist[minHour]) minHour = h;

            return hist[minHour] * 10 < median ? minHour : -1;
        }

        /// <summary>Bar stamp -> your clock. Session windows are compared here.</summary>
        public DateTime ToLocal(DateTime barTime)
        {
            if (Clock == BarClock.AlreadyLocal) return barTime;

            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(barTime, DateTimeKind.Utc), Zone);
        }

        /// <summary>Short zone name for a local timestamp, e.g. "CDT" in summer.</summary>
        public string Abbrev(DateTime local)
        {
            try
            {
                var daylight = Zone.IsDaylightSavingTime(
                    DateTime.SpecifyKind(local, DateTimeKind.Unspecified));

                var name = daylight ? Zone.DaylightName : Zone.StandardName;

                // "Central Daylight Time" -> "CDT"
                var initials = string.Empty;
                foreach (var word in name.Split(' '))
                    if (word.Length > 0 && char.IsUpper(word[0])) initials += word[0];

                return initials.Length >= 2 ? initials : name;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// The slice of the chart the model builder needs. Keeps the builder and the clock resolver
    /// free of ATAS types so both can be exercised off-platform.
    /// </summary>
    public interface IBarWindow
    {
        int Count { get; }
        DateTime Time(int bar);
        decimal Open(int bar);
        decimal High(int bar);
        decimal Low(int bar);
        decimal Close(int bar);
    }
}
