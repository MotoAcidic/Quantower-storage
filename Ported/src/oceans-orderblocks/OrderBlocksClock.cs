using System;
using System.Collections.Generic;

namespace OceansOrderBlocks
{
    /// <summary>
    /// Bars, abstracted away from ATAS so everything below can be exercised in _test on
    /// synthetic series. Only the indicator adapter on the other side of the line knows
    /// what an IndicatorCandle is.
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

    /// <summary>How the platform stamps its bars.</summary>
    public enum BarClock
    {
        /// <summary>Settle it from evidence.</summary>
        Auto = 0,

        /// <summary>Stamps are already in the configured zone.</summary>
        AlreadyLocal = 1,

        /// <summary>Stamps are UTC and need converting.</summary>
        Utc = 2
    }

    public static class BarMath
    {
        /// <summary>
        /// How long one bar covers, as the MEDIAN gap between consecutive stamps. The median
        /// rather than the mean, and rather than parsing the chart's timeframe string:
        /// overnight breaks, weekends and holidays put huge gaps in the series that drag an
        /// average to nonsense, and the timeframe string does not exist for range, tick or
        /// volume bars at all.
        ///
        /// Zero when there are too few bars to tell, which every caller reads as "unknown".
        /// </summary>
        public static TimeSpan Duration(IBarWindow bars, int sample)
        {
            if (bars == null || bars.Count < 3) return TimeSpan.Zero;

            var gaps = new List<double>();
            var first = Math.Max(1, bars.Count - sample);

            for (var i = first; i < bars.Count; i++)
            {
                var span = (bars.Time(i) - bars.Time(i - 1)).TotalSeconds;
                if (span > 0) gaps.Add(span);
            }

            if (gaps.Count == 0) return TimeSpan.Zero;

            gaps.Sort();
            return TimeSpan.FromSeconds(gaps[gaps.Count / 2]);
        }

        /// <summary>Length of a window, wrap-aware.</summary>
        public static TimeSpan Length(TimeSpan start, TimeSpan end)
        {
            var span = end - start;
            if (span <= TimeSpan.Zero) span += TimeSpan.FromDays(1);
            return span;
        }

        /// <summary>Readable bar size for the readout: "5m", "1h", "1d".</summary>
        public static string Describe(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) return "unknown";
            if (duration.TotalDays >= 1) return Trim(duration.TotalDays) + "d";
            if (duration.TotalHours >= 1) return Trim(duration.TotalHours) + "h";
            if (duration.TotalMinutes >= 1) return Trim(duration.TotalMinutes) + "m";
            return Trim(duration.TotalSeconds) + "s";
        }

        private static string Trim(double v)
        {
            return Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Puts every bar on Houston time. One zone everywhere: session windows are typed in it,
    /// the math runs in it, the labels print it.
    ///
    /// Ported from oceans-pivot-decoder's BarClockContext, which the build spec names as the
    /// verified answer to candle-time-vs-exchange-time on this feed. Do not replace it with
    /// hardcoded clock math.
    ///
    /// It matters as much here as it did there. Every zone is anchored to a session window, so
    /// a wrong UTC/local reading does not look wrong -- it produces a clean, plausible,
    /// silently misplaced set of order blocks, and a zone drawn at a fictional level is
    /// worse than no signal at all. So it never guesses: it either settles the clock from
    /// evidence or reports Error, and the indicator draws that instead of blocks.
    /// </summary>
    public sealed class BarClockContext
    {
        private BarClockContext() { }

        public TimeZoneInfo Zone { get; private set; }
        public BarClock Clock { get; private set; }
        public string How { get; private set; }
        public string Error { get; private set; }

        public bool Valid { get { return Error == null; } }

        private static BarClockContext Fail(string error)
        {
            return new BarClockContext { Error = error };
        }

        public static BarClockContext Create(string zoneId, BarClock requested, IBarWindow bars,
                                             DateTime nowUtc, DateTime marketNow, int haltHourLocal,
                                             TimeSpan barDuration)
        {
            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (Exception ex)
            {
                return Fail("Ocean's Order Blocks: no such time zone " + zoneId + " (" + ex.Message +
                            "). Houston is Central Standard Time.");
            }

            var ctx = new BarClockContext { Zone = zone };

            if (requested != BarClock.Auto)
            {
                ctx.Clock = requested;
                ctx.How = "set by hand";
                return ctx;
            }

            if (bars == null || bars.Count == 0)
                return Fail("Ocean's Order Blocks: no bars loaded yet.");

            string how;
            var found = ByMarketClock(bars, zone, nowUtc, marketNow, barDuration, out how);
            if (found == null) found = ByWallClock(bars, zone, nowUtc, barDuration, out how);
            if (found == null) found = ByWeekendGap(bars, zone, barDuration, out how);
            if (found == null) found = ByDailyHalt(bars, zone, haltHourLocal, out how);

            if (found == null) return Fail(Explain(bars, zone, barDuration));

            ctx.Clock = found.Value;
            ctx.How = how;
            return ctx;
        }

        /// <summary>When every signal comes up short, say what was seen and what to do.</summary>
        private static string Explain(IBarWindow bars, TimeZoneInfo zone, TimeSpan barDuration)
        {
            var last = bars.Time(bars.Count - 1);

            var asLocal = "?";
            try
            {
                asLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(last, DateTimeKind.Utc), zone).ToString("ddd HH:mm");
            }
            catch { }

            var msg = "Ocean's Order Blocks: cannot settle the bar clock. Newest bar is stamped " +
                      last.ToString("ddd HH:mm") + "; read as UTC that is " + asLocal +
                      " Houston. Set Bar clock to whichever matches the session on screen " +
                      "(AlreadyLocal is the usual answer).";

            if (BarsPerDay(bars) < 24)
                msg += " These are " + BarMath.Describe(barDuration) + " bars, too coarse to find " +
                       "the daily halt; load more history so the weekend gaps can settle it, or " +
                       "set the clock by hand.";

            return msg;
        }

        private static double BarsPerDay(IBarWindow bars)
        {
            var days = new HashSet<DateTime>();
            for (var i = 0; i < bars.Count; i++) days.Add(bars.Time(i).Date);

            return days.Count == 0 ? 0 : (double)bars.Count / days.Count;
        }

        /// <summary>
        /// The strongest signal, and the one that needs no history: ATAS keeps its own market
        /// clock. When that clock is genuinely not UTC and it agrees with the configured zone --
        /// which it does for CME equity index, where exchange time IS Central -- then whichever
        /// of the two the newest bar sits next to settles the question outright.
        ///
        /// Both guards matter. Without the first, a platform reporting market time as UTC makes
        /// the test vacuous; without the second, a non-CME instrument on some other exchange
        /// clock would be read as Houston time and every zone would land in the wrong place.
        /// </summary>
        private static BarClock? ByMarketClock(IBarWindow bars, TimeZoneInfo zone, DateTime nowUtc,
                                               DateTime marketNow, TimeSpan barDuration, out string how)
        {
            how = "matched to the platform market clock";

            if (marketNow == default(DateTime) || nowUtc == default(DateTime)) return null;
            if (Math.Abs((marketNow - nowUtc).TotalHours) < 1.0) return null;

            try
            {
                var zoneNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
                if (Math.Abs((marketNow - zoneNow).TotalHours) > 1.0) return null;
            }
            catch
            {
                return null;
            }

            var last = bars.Time(bars.Count - 1);
            if (last == default(DateTime)) return null;

            var tolerance = Math.Max(2.0, barDuration.TotalHours);
            if (tolerance > 4.0) return null;

            var fitsMarket = Math.Abs((marketNow - last).TotalHours) <= tolerance;
            var fitsUtc = Math.Abs((nowUtc - last).TotalHours) <= tolerance;

            if (fitsMarket == fitsUtc) return null;

            return fitsMarket ? BarClock.AlreadyLocal : BarClock.Utc;
        }

        /// <summary>
        /// On a live chart the newest bar is minutes old and the two readings sit five or six
        /// hours apart, so this settles it outright. Null on a stale chart -- weekend, replay,
        /// loaded history -- where the comparison proves nothing.
        /// </summary>
        private static BarClock? ByWallClock(IBarWindow bars, TimeZoneInfo zone, DateTime nowUtc,
                                             TimeSpan barDuration, out string how)
        {
            how = "matched to the wall clock";

            var last = bars.Time(bars.Count - 1);
            if (last == default(DateTime) || nowUtc == default(DateTime)) return null;

            // A bar stamp is its OPEN, so the newest bar legitimately trails now by up to one
            // bar. The allowance grows with the bar or an hourly chart's newest bar looks stale;
            // past about four hours it exceeds the 5-6h offset being told apart and stops
            // discriminating, so beyond that this signal declines to answer.
            var tolerance = Math.Max(2.0, barDuration.TotalHours);
            if (tolerance > 4.0) return null;

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
                return null; // spring-forward gap; this signal cannot decide
            }

            var utcFits = asUtc <= tolerance;
            var localFits = asLocal <= tolerance;
            if (utcFits == localFits) return null;

            return utcFits ? BarClock.Utc : BarClock.AlreadyLocal;
        }

        /// <summary>
        /// Works at any intraday bar size. The signal is the weekend: futures stop Friday at
        /// 16:00 Houston and do not restart until Sunday evening, so the last bar before every
        /// multi-day gap ENDS at the Friday close. Under the wrong reading that same moment
        /// lands late Friday evening or Friday morning, and neither is a time the market closes.
        ///
        /// It is the bar's END that is tested, not its stamp, and that detail is the whole
        /// signal: a bar's opening time depends on its size, but every bar ends at the close
        /// regardless of size, so the window can stay narrow.
        /// </summary>
        private static BarClock? ByWeekendGap(IBarWindow bars, TimeZoneInfo zone,
                                              TimeSpan barDuration, out string how)
        {
            how = "found the weekend gap";

            var utcFit = 0;
            var localFit = 0;
            var gaps = 0;

            for (var i = 1; i < bars.Count; i++)
            {
                var previous = bars.Time(i - 1);
                var current = bars.Time(i);

                if (previous == default(DateTime) || current == default(DateTime)) continue;
                if ((current - previous).TotalHours < 24) continue;

                gaps++;

                if (ClosesTheWeek(previous + barDuration)) localFit++;

                try
                {
                    if (ClosesTheWeek(TimeZoneInfo.ConvertTimeFromUtc(
                            DateTime.SpecifyKind(previous, DateTimeKind.Utc), zone) + barDuration))
                        utcFit++;
                }
                catch { }
            }

            if (gaps < 2) return null;

            // A clear majority, not one lucky weekend: holidays put multi-day gaps in the middle
            // of the week that fit neither reading.
            var localWins = localFit * 2 > gaps;
            var utcWins = utcFit * 2 > gaps;

            if (localWins == utcWins) return null;

            return utcWins ? BarClock.Utc : BarClock.AlreadyLocal;
        }

        /// <summary>The Friday close, with slack for holiday early closes and a partial last bar.</summary>
        private static bool ClosesTheWeek(DateTime endOfBar)
        {
            return endOfBar.DayOfWeek == DayOfWeek.Friday &&
                   endOfBar.Hour >= 14 && endOfBar.Hour < 19;
        }

        /// <summary>
        /// Independent of the wall clock: MNQ shuts for one hour every weekday afternoon. Under
        /// the right reading that hour is empty on every day; under the wrong one the empty hour
        /// lands five or six hours off.
        /// </summary>
        private static BarClock? ByDailyHalt(IBarWindow bars, TimeZoneInfo zone, int haltHour,
                                             out string how)
        {
            how = "found the daily halt";

            var utcHist = new int[24];
            var localHist = new int[24];
            var days = new HashSet<DateTime>();

            for (var i = 0; i < bars.Count; i++)
            {
                var t = bars.Time(i);
                if (t == default(DateTime)) continue;

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

            if (days.Count < 2) return null; // "empty every day" needs days to mean anything

            var utcHit = EmptiestHour(utcHist) == haltHour;
            var localHit = EmptiestHour(localHist) == haltHour;
            if (utcHit == localHit) return null;

            return utcHit ? BarClock.Utc : BarClock.AlreadyLocal;
        }

        /// <summary>
        /// The emptiest hour, but only when decisively empty -- under a tenth of the median
        /// hour. A merely quiet hour is not a halt and must not be read as one.
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

        /// <summary>Bar stamp -> Houston. Every session window is compared here.</summary>
        public DateTime ToLocal(DateTime barTime)
        {
            if (Clock == BarClock.AlreadyLocal) return barTime;

            return TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(barTime, DateTimeKind.Utc), Zone);
        }

        /// <summary>
        /// Bar stamp -> UTC. Only the alert freshness gate uses it, to tell a live print from a
        /// bar replayed during a chart load. Null inside a spring-forward gap.
        /// </summary>
        public DateTime? ToUtc(DateTime barTime)
        {
            if (Clock == BarClock.Utc) return DateTime.SpecifyKind(barTime, DateTimeKind.Utc);

            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified), Zone);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Short zone name for a local timestamp, e.g. CDT in summer.</summary>
        public string Abbrev(DateTime local)
        {
            try
            {
                var daylight = Zone.IsDaylightSavingTime(
                    DateTime.SpecifyKind(local, DateTimeKind.Unspecified));

                var name = daylight ? Zone.DaylightName : Zone.StandardName;

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
}
