using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansPivotDecoder
{
    #region Enums

    /// <summary>Which prior session's H/L/C feeds the formulas.</summary>
    public enum SessionMode
    {
        /// <summary>Prior regular-hours window only (08:30-15:00 Houston by default).</summary>
        Rth = 0,

        /// <summary>The whole prior trading day (17:00 previous day -> 16:00 Houston).</summary>
        Eth24h = 1
    }

    /// <summary>Where the C in (H+L+C)/3 comes from.</summary>
    public enum CloseSourceMode
    {
        /// <summary>Close of the last bar inside the session window.</summary>
        SessionClose = 0,

        /// <summary>Exchange settlement, typed in by hand -- the feed does not reliably carry it.</summary>
        SettlementPrice = 1
    }

    /// <summary>
    /// Which third-level convention to emit.
    ///
    /// READ THIS BEFORE TOUCHING ANY R3/S3 CODE. The two conventions usually quoted as different
    /// are the same formula written two ways:
    ///
    ///     Standard  R3 = H + 2*(PP - L)   = 2PP + H - 2L
    ///     Narrow    R3 = R1 + (H - L)     = (2PP - L) + H - L = 2PP + H - 2L
    ///
    ///     Standard  S3 = L - 2*(H - PP)   = 2PP - 2H + L
    ///     Narrow    S3 = S1 - (H - L)     = (2PP - H) - H + L = 2PP - 2H + L
    ///
    /// They are algebraically identical and always agree to the cent. Choosing "Both" therefore
    /// plots ONE line, labelled R3sn / S3sn, not two -- see BuildFloor.
    ///
    /// The convention that genuinely differs -- and the one worth testing a caller against -- is
    /// the PP-anchored pair, exposed separately by the "wide" toggle:
    ///
    ///     Wide      R3 = PP + 2*(H - L)
    ///     Wide      S3 = PP - 2*(H - L)
    ///
    /// On the validation case the classic pair gives S3 = 28870.58 and the wide pair 28749.42:
    /// 121 points apart, far outside any sane tolerance. Confusing the two is the bug that makes
    /// a decoder report NO MATCH against a caller who is in fact using floor pivots.
    /// </summary>
    public enum R3S3Variant
    {
        Standard = 0,
        Narrow = 1,
        Both = 2
    }

    /// <summary>How to read the clock stamped on each bar.</summary>
    public enum BarClock
    {
        /// <summary>Work it out from the data.</summary>
        Auto = 0,

        /// <summary>Bars are stamped UTC and must be converted.</summary>
        Utc = 1,

        /// <summary>Bars are already stamped in your zone; use as-is.</summary>
        AlreadyLocal = 2
    }

    public enum Verdict { Exact = 0, Near = 1, NoMatch = 2 }

    /// <summary>Which way the caller meant a level to be traded.</summary>
    public enum CallDirection { Unknown = 0, Long = 1, Short = 2 }

    /// <summary>What happened after price first touched a matched level.</summary>
    public enum ReactionOutcome
    {
        /// <summary>Price never reached the level this session.</summary>
        Untouched = 0,

        /// <summary>Touched; neither target nor stop hit yet.</summary>
        Open = 1,

        /// <summary>Target reached first.</summary>
        Target = 2,

        /// <summary>Stop reached first.</summary>
        Stop = 3,

        /// <summary>Both inside one bar -- the bar cannot say which came first.</summary>
        Ambiguous = 4
    }

    #endregion

    #region Bar access

    /// <summary>
    /// The slice of the chart the model needs. Keeping this an interface is what lets every line
    /// of arithmetic below run in the test harness, off-platform, on synthetic bars.
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

    #endregion

    #region Bar size

    public static class BarMath
    {
        /// <summary>
        /// How long one bar covers, taken as the MEDIAN gap between consecutive stamps.
        ///
        /// The median rather than the mean, and rather than parsing the chart's timeframe string:
        /// overnight breaks, the weekend and holidays put huge gaps in the series that would drag
        /// an average to nonsense, and the timeframe string does not exist for range, tick or
        /// volume bars at all. The median of a few hundred samples survives all of it.
        ///
        /// Returns zero when there are too few bars to tell, which every caller treats as
        /// "unknown" rather than as "instant".
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

        /// <summary>Readable bar size for the readout: "15m", "1h", "1d".</summary>
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

    #endregion

    #region Bar clock

    /// <summary>
    /// Puts every bar on Houston time. One zone everywhere: session windows are typed in it, the
    /// math runs in it, the labels print it.
    ///
    /// This matters more here than in most indicators. Every level is anchored to a clock time,
    /// so a wrong UTC/local reading does not look wrong -- it produces a clean, plausible,
    /// silently misplaced set of pivots, and the whole point of this tool is deciding whether a
    /// 1.5-point difference is a match. So it never guesses: it either settles the clock from
    /// evidence or reports <see cref="Error"/>, and the indicator draws that instead of levels.
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
                                             DateTime nowUtc, DateTime marketNow, int haltHourLocal)
        {
            return Create(zoneId, requested, bars, nowUtc, marketNow, haltHourLocal, TimeSpan.Zero);
        }

        /// <summary>
        /// <paramref name="barDuration"/> is what makes coarse charts work. Every signal below has
        /// a tolerance or a resolution that only holds for intraday bars; on a 4-hour or daily
        /// chart they all came back undecided and the indicator drew nothing at all.
        /// </summary>
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
                return Fail("Pivot Decoder: no such time zone " + zoneId + " (" + ex.Message +
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
                return Fail("Pivot Decoder: no bars loaded yet.");

            string how;
            var found = ByMarketClock(bars, zone, nowUtc, marketNow, barDuration, out how);
            if (found == null) found = ByWallClock(bars, zone, nowUtc, barDuration, out how);
            if (found == null) found = ByWeekendGap(bars, zone, barDuration, out how);
            if (found == null) found = ByDailyHalt(bars, zone, haltHourLocal, out how);

            // Last resort, and only for bars a day or longer. At that size the reading cannot
            // change which BARS make up the prior session -- it is still the previous daily bar
            // either way -- it can only shift the date printed next to it, and at most by one.
            // The levels are identical, so refusing to draw them would be pedantry rather than
            // safety. The caveat is stated in How, and the readout prints it.
            if (found == null && barDuration >= TimeSpan.FromHours(20))
            {
                found = BarClock.AlreadyLocal;
                how = "bars are " + BarMath.Describe(barDuration) +
                      "; the clock cannot be settled but does not change the levels, only the date label";
            }

            if (found == null) return Fail(Explain(bars, zone, nowUtc, barDuration));

            ctx.Clock = found.Value;
            ctx.How = how;
            return ctx;
        }

        /// <summary>
        /// When all three signals come up short, say what was seen and what to do about it. The
        /// old message just said "set it by hand", which is useless without knowing which way.
        /// </summary>
        private static string Explain(IBarWindow bars, TimeZoneInfo zone, DateTime nowUtc,
                                      TimeSpan barDuration)
        {
            var last = bars.Time(bars.Count - 1);

            var asLocal = "?";
            try
            {
                asLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(last, DateTimeKind.Utc), zone).ToString("ddd HH:mm");
            }
            catch { }

            var msg = "Pivot Decoder: cannot settle the bar clock. Newest bar is stamped " +
                      last.ToString("ddd HH:mm") + "; read as UTC that is " + asLocal +
                      " Houston. Set Bar clock to whichever matches the session you are looking " +
                      "at (AlreadyLocal is the usual answer).";

            // A coarse timeframe has too few bars an hour for the halt to show up, and that is a
            // different problem with a different fix.
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
        /// The strongest signal, and the one that needs no history at all: ATAS keeps its own
        /// market clock. When that clock is genuinely not UTC, and it agrees with the configured
        /// zone -- which it does for CME equity index, where exchange time IS Central -- then
        /// whichever of the two the newest bar sits next to settles the question outright.
        ///
        /// Both guards matter. Without the first, a platform reporting market time as UTC would
        /// make the test vacuous; without the second, a non-CME instrument on some other exchange
        /// clock would be read as Houston time and every level would land in the wrong place.
        /// </summary>
        private static BarClock? ByMarketClock(IBarWindow bars, TimeZoneInfo zone, DateTime nowUtc,
                                               DateTime marketNow, TimeSpan barDuration, out string how)
        {
            how = "matched to the platform's market clock";

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
        /// hours apart, so this settles it outright. Returns null on a stale chart -- weekend,
        /// replay, loaded history -- where the comparison proves nothing.
        /// </summary>
        private static BarClock? ByWallClock(IBarWindow bars, TimeZoneInfo zone, DateTime nowUtc,
                                             TimeSpan barDuration, out string how)
        {
            how = "matched to the wall clock";

            var last = bars.Time(bars.Count - 1);
            if (last == default(DateTime) || nowUtc == default(DateTime)) return null;

            // A bar stamp is its OPEN, so the newest bar legitimately trails now by up to one bar.
            // The allowance has to grow with the bar or an hourly chart's newest bar looks stale;
            // but past about four hours it exceeds the 5-6 h offset being told apart and stops
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
        /// Works at any intraday bar size, including the 4-hour where the one-hour maintenance
        /// halt is invisible because no bar fits inside it.
        ///
        /// The signal is the weekend. Futures stop Friday at 16:00 Houston and do not restart
        /// until Sunday evening, so the last bar before every multi-day gap ENDS at the Friday
        /// close. Under the wrong reading that same moment lands late Friday evening or Friday
        /// morning, and neither is a time the market closes.
        ///
        /// It is the bar's END that is tested, not its stamp, and that detail is the whole signal.
        /// A bar's opening time depends on its size -- the last Friday bar opens at 15:00 on an
        /// hourly chart and at 12:00 on a four-hour one -- so a window drawn around the open has
        /// to be wide enough to cover both, and at that width it starts matching the wrong reading
        /// too. Every bar ends at the close regardless of size, so the window can stay narrow.
        ///
        /// Testing merely "is it Friday" would pass under both readings: a five-hour shift rarely
        /// crosses a day boundary.
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

            // A clear majority, not a single lucky weekend: holidays put multi-day gaps in the
            // middle of the week that fit neither reading.
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
        /// The emptiest hour, but only when it is decisively empty -- under a tenth of the median
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

    #endregion

    #region Session scanning

    /// <summary>One named intraday window, in Houston time.</summary>
    public sealed class NamedWindow
    {
        public string Name;
        public TimeSpan Start;
        public TimeSpan End;

        public NamedWindow(string name, TimeSpan start, TimeSpan end)
        {
            Name = name;
            Start = start;
            End = end;
        }
    }

    /// <summary>Session windows, all in Houston time. There is no second zone anywhere.</summary>
    public sealed class SessionConfig
    {
        /// <summary>
        /// The three intraday sessions whose POCs and extremes get read. Defaults are the CME
        /// equity-index day in Houston time: Asia wraps midnight, which is why every window test
        /// here is wrap-aware.
        /// </summary>
        public List<NamedWindow> Intraday = new List<NamedWindow>
        {
            new NamedWindow("Asia", new TimeSpan(17, 0, 0), new TimeSpan(2, 0, 0)),
            new NamedWindow("London", new TimeSpan(2, 0, 0), new TimeSpan(8, 30, 0)),
            new NamedWindow("NY", new TimeSpan(8, 30, 0), new TimeSpan(15, 0, 0))
        };

        public TimeSpan RthStart = new TimeSpan(8, 30, 0);
        public TimeSpan RthEnd = new TimeSpan(15, 0, 0);

        /// <summary>Globex reopen. Bars at or after this belong to the NEXT trade date.</summary>
        public TimeSpan EthStart = new TimeSpan(17, 0, 0);

        /// <summary>Globex close. Bars before this belong to their own day's trade date.</summary>
        public TimeSpan EthEnd = new TimeSpan(16, 0, 0);
    }

    /// <summary>High/low/close of one window, plus where it sits in the bar array.</summary>
    public struct Window
    {
        public bool Valid;
        public decimal Open, High, Low, Close;
        public int FirstBar, LastBar;

        /// <summary>Which bar printed the extreme. Needed to read the auction at that price.</summary>
        public int HighBar, LowBar;

        /// <summary>
        /// How many bars printed the exact extreme. Two or more is a ledge -- a flat top or
        /// bottom, where price was stopped by something rather than by the auction finishing.
        /// </summary>
        public int BarsAtHigh, BarsAtLow;

        /// <summary>
        /// The extremes from bars lying WHOLLY inside the window.
        ///
        /// On a chart whose bars do not divide the session boundary -- an hourly chart against an
        /// 08:30 open -- the first and last bars straddle it, and their high or low may have
        /// printed on the wrong side. These are the values that are beyond question, and the gap
        /// between them and the headline extreme is the exact size of the doubt.
        /// </summary>
        public bool HasInside;
        public decimal InsideHigh, InsideLow;

        /// <summary>True when a straddling bar, not a clean one, set the extreme.</summary>
        public bool HighFromEdge, LowFromEdge;

        /// <summary>
        /// How far the high could be overstated: zero when a bar wholly inside the window set it,
        /// so there is nothing to doubt.
        /// </summary>
        public decimal HighDoubt { get { return HighFromEdge && HasInside ? High - InsideHigh : 0m; } }

        public decimal LowDoubt { get { return LowFromEdge && HasInside ? InsideLow - Low : 0m; } }

        /// <summary>The larger of the two doubts -- the one that governs whether this is usable.</summary>
        public decimal WorstDoubt { get { return Math.Max(HighDoubt, LowDoubt); } }

        /// <summary>
        /// Whether this window's high and low were actually MEASURED, or merely bracketed.
        ///
        /// The test is the doubt as a share of the range, because that is the number that
        /// propagates: every level here is built from H, L and C, so a low that could be 35% of
        /// the range too low drags the pivot 57 points and the third levels several hundred --
        /// against a match tolerance of three. At that point the levels are not approximate, they
        /// are fictional, and publishing them with a caution attached would be worse than not
        /// publishing them, because they look exactly like the real thing.
        ///
        /// The case this catches: a 6.5-hour session on a 4-hour chart. Both edge bars straddle
        /// the boundary and at most one bar lies wholly inside, so the only thing beyond question
        /// is a single bar's range. A window with NO clean bar at all is never trustworthy,
        /// whatever the arithmetic says, because there is nothing to bound it with.
        /// </summary>
        public bool Trustworthy(decimal doubtLimitPercent)
        {
            if (!Valid || !HasInside) return false;
            if (Range <= 0m) return true;

            return WorstDoubt * 100m / Range <= doubtLimitPercent;
        }

        public DateTime FirstLocal, LastLocal;

        public decimal Range { get { return High - Low; } }

        public void Add(int bar, DateTime local, decimal o, decimal h, decimal l, decimal c)
        {
            Add(bar, local, o, h, l, c, true);
        }

        /// <summary>
        /// <paramref name="wholly"/> is false for a bar that only overlaps the window, so its
        /// extreme may have printed outside it.
        /// </summary>
        public void Add(int bar, DateTime local, decimal o, decimal h, decimal l, decimal c,
                        bool wholly)
        {
            if (!Valid)
            {
                Valid = true;
                Open = o; High = h; Low = l;
                FirstBar = bar; FirstLocal = local;
                HighBar = bar; LowBar = bar;
                BarsAtHigh = 1; BarsAtLow = 1;
                HighFromEdge = !wholly; LowFromEdge = !wholly;
            }
            else
            {
                if (h > High) { High = h; HighBar = bar; BarsAtHigh = 1; HighFromEdge = !wholly; }
                else if (h == High) { BarsAtHigh++; if (wholly) HighFromEdge = false; }

                if (l < Low) { Low = l; LowBar = bar; BarsAtLow = 1; LowFromEdge = !wholly; }
                else if (l == Low) { BarsAtLow++; if (wholly) LowFromEdge = false; }
            }

            if (wholly)
            {
                if (!HasInside) { HasInside = true; InsideHigh = h; InsideLow = l; }
                else
                {
                    if (h > InsideHigh) InsideHigh = h;
                    if (l < InsideLow) InsideLow = l;
                }
            }

            Close = c;
            LastBar = bar;
            LastLocal = local;
        }
    }

    /// <summary>One CME trade date, with its regular-hours slice and its full-day slice.</summary>
    public sealed class TradingDay
    {
        public DateTime TradeDate;
        public Window Rth;
        public Window Eth;

        /// <summary>Asia / London / NY, by name.</summary>
        public Dictionary<string, Window> Intraday = new Dictionary<string, Window>();

        public Window Named(string name)
        {
            Window window;
            return Intraday.TryGetValue(name, out window) ? window : default(Window);
        }
    }

    public static class SessionScan
    {
        /// <summary>
        /// Groups every bar into CME trade dates and accumulates both windows in one pass.
        /// Nothing here reads exchange session metadata: SDK versions disagree about what that
        /// contains, and a chart loaded from another feed can carry none at all.
        /// </summary>
        public static List<TradingDay> Scan(IBarWindow bars, BarClockContext clock, SessionConfig cfg)
        {
            return Scan(bars, clock, cfg, TimeSpan.Zero);
        }

        /// <summary>
        /// <paramref name="barDuration"/> lets a bar be assigned to every window its span touches,
        /// not just the one its opening stamp lands in. Pass Zero for the old stamp-only behaviour.
        /// </summary>
        public static List<TradingDay> Scan(IBarWindow bars, BarClockContext clock, SessionConfig cfg,
                                            TimeSpan barDuration)
        {
            var days = new List<TradingDay>();
            if (bars == null || clock == null || !clock.Valid) return days;

            var index = new Dictionary<DateTime, TradingDay>();

            for (var i = 0; i < bars.Count; i++)
            {
                var raw = bars.Time(i);
                if (raw == default(DateTime)) continue;

                DateTime local;
                try { local = clock.ToLocal(raw); }
                catch { continue; }

                var date = TradeDateOf(local, cfg);
                if (date == null) continue;

                TradingDay day;
                if (!index.TryGetValue(date.Value, out day))
                {
                    day = new TradingDay { TradeDate = date.Value };
                    index[date.Value] = day;
                    days.Add(day);
                }

                var o = bars.Open(i);
                var h = bars.High(i);
                var l = bars.Low(i);
                var c = bars.Close(i);

                day.Eth.Add(i, local, o, h, l, c);

                // A bar coarser than the window cannot describe it at all: a daily bar "inside"
                // an 08:30-15:00 session is just the whole day wearing an RTH label. Left invalid
                // so the caller reports the window as unavailable rather than serving full-day
                // numbers under a name that means something narrower.
                if (Resolvable(barDuration, cfg.RthStart, cfg.RthEnd) &&
                    Overlaps(local.TimeOfDay, barDuration, cfg.RthStart, cfg.RthEnd))
                    day.Rth.Add(i, local, o, h, l, c,
                                Contains(local.TimeOfDay, barDuration, cfg.RthStart, cfg.RthEnd));

                if (cfg.Intraday == null) continue;

                foreach (var named in cfg.Intraday)
                {
                    if (!Resolvable(barDuration, named.Start, named.End)) continue;
                    if (!Overlaps(local.TimeOfDay, barDuration, named.Start, named.End)) continue;

                    Window window;
                    day.Intraday.TryGetValue(named.Name, out window);
                    window.Add(i, local, o, h, l, c,
                               Contains(local.TimeOfDay, barDuration, named.Start, named.End));
                    day.Intraday[named.Name] = window;
                }
            }

            days.Sort(delegate (TradingDay a, TradingDay b)
            {
                return a.TradeDate.CompareTo(b.TradeDate);
            });

            return days;
        }

        /// <summary>
        /// Which CME trade date a Houston timestamp belongs to. 17:00 Sunday is Monday's session;
        /// 16:00-17:00 is the daily break and belongs to neither, so those bars are dropped rather
        /// than smeared into a neighbouring day.
        /// </summary>
        public static DateTime? TradeDateOf(DateTime local, SessionConfig cfg)
        {
            DateTime date;

            if (local.TimeOfDay >= cfg.EthStart) date = local.Date.AddDays(1);
            else if (local.TimeOfDay < cfg.EthEnd) date = local.Date;
            else return null;

            // A Saturday or Sunday trade date can only come from bad data or a broken feed clock;
            // roll it onto Monday rather than inventing a weekend session.
            if (date.DayOfWeek == DayOfWeek.Saturday) date = date.AddDays(2);
            else if (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);

            return date;
        }

        /// <summary>
        /// Time-of-day containment, wrap-aware so a custom overnight RTH window still works.
        /// A wrapping window is grouped by each bar's ETH trade date, which is what you want for
        /// anything anchored to the Globex day.
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
        /// This is the difference between a 15-minute chart and a 1-hour chart. A bar carries one
        /// timestamp -- its open -- and testing only that stamp throws away every bar that starts
        /// before the window and runs into it. RTH opening at 08:30 meant the 1-hour bar stamped
        /// 08:00 was discarded whole, so the hourly chart lost 08:30-09:00: the cash open, the
        /// highest-volume half hour of the day, and very often the session high or low. The pivots
        /// then disagreed between timeframes for a reason that had nothing to do with the market.
        ///
        /// A bar is counted when any part of it falls inside. On a coarse chart that also drags in
        /// prints from just outside the window, which is a real approximation and is why the
        /// caller is told to say so on screen -- but losing half the cash open is the larger error
        /// by far, and this at least makes the timeframes converge instead of diverge.
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
        /// Whether a window is meaningful at this bar size. One bar has one high and one low, so a
        /// window shorter than a bar cannot have its own.
        /// </summary>
        public static bool Resolvable(TimeSpan barDuration, TimeSpan start, TimeSpan end)
        {
            if (barDuration <= TimeSpan.Zero) return true;
            return barDuration < BarMath.Length(start, end);
        }

        /// <summary>The most recent completed day before <paramref name="current"/> with RTH data.</summary>
        public static TradingDay PriorWithRth(List<TradingDay> days, DateTime current)
        {
            for (var i = days.Count - 1; i >= 0; i--)
                if (days[i].TradeDate < current && days[i].Rth.Valid) return days[i];
            return null;
        }

        /// <summary>The most recent completed day before <paramref name="current"/> with full-day data.</summary>
        public static TradingDay PriorWithEth(List<TradingDay> days, DateTime current)
        {
            for (var i = days.Count - 1; i >= 0; i--)
                if (days[i].TradeDate < current && days[i].Eth.Valid) return days[i];
            return null;
        }
    }

    #endregion

    #region Levels

    /// <summary>One computed price line.</summary>
    public sealed class PivotLevel
    {
        /// <summary>Display group, e.g. FLOOR-RTH, CAM-ETH, VP-NY.</summary>
        public string Group;

        /// <summary>Short name used in the panel, e.g. PP, cR4, pRTH-L, nPOC, POOR-H.</summary>
        public string Name;

        public decimal Price;

        /// <summary>RTH or ETH -- carried through to the CSV as sessionModeOfMatch.</summary>
        public string Session;

        /// <summary>What the level IS. Decides its weight in a zone score.</summary>
        public LevelFamily Family;

        /// <summary>True for a price the market actually traded, rather than a formula output.</summary>
        public bool IsReference
        {
            get { return Family != LevelFamily.Floor && Family != LevelFamily.Camarilla && Family != LevelFamily.Mid; }
        }

        /// <summary>A glyph for the label: the poor-extreme marker, mostly.</summary>
        public string Marker;

        /// <summary>Extra context for the readout, e.g. why an extreme was called poor.</summary>
        public string Detail;

        public string Label
        {
            get
            {
                return "[" + Group + "] " + Name + " " +
                       Price.ToString("F2", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Every formula, in one place, in decimal, with no dependency on ATAS or on any session
    /// scanning. This is the class the test harness pins the validation case against.
    /// </summary>
    public static class PivotMath
    {
        #region Classic floor pivots

        public static decimal Pp(decimal h, decimal l, decimal c)
        {
            return (h + l + c) / 3m;
        }

        public static decimal R1(decimal pp, decimal l) { return 2m * pp - l; }
        public static decimal S1(decimal pp, decimal h) { return 2m * pp - h; }

        public static decimal R2(decimal pp, decimal h, decimal l) { return pp + (h - l); }
        public static decimal S2(decimal pp, decimal h, decimal l) { return pp - (h - l); }

        /// <summary>R3 = H + 2*(PP - L). Identical to <see cref="R3Narrow"/> -- see R3S3Variant.</summary>
        public static decimal R3Standard(decimal pp, decimal h, decimal l)
        {
            return h + 2m * (pp - l);
        }

        /// <summary>S3 = L - 2*(H - PP). Identical to <see cref="S3Narrow"/> -- see R3S3Variant.</summary>
        public static decimal S3Standard(decimal pp, decimal h, decimal l)
        {
            return l - 2m * (h - pp);
        }

        /// <summary>R3 = R1 + (H - L). Identical to <see cref="R3Standard"/> -- see R3S3Variant.</summary>
        public static decimal R3Narrow(decimal pp, decimal h, decimal l)
        {
            return R1(pp, l) + (h - l);
        }

        /// <summary>S3 = S1 - (H - L). Identical to <see cref="S3Standard"/> -- see R3S3Variant.</summary>
        public static decimal S3Narrow(decimal pp, decimal h, decimal l)
        {
            return S1(pp, h) - (h - l);
        }

        /// <summary>R3 = PP + 2*(H - L). The genuinely different, PP-anchored convention.</summary>
        public static decimal R3Wide(decimal pp, decimal h, decimal l)
        {
            return pp + 2m * (h - l);
        }

        /// <summary>S3 = PP - 2*(H - L). The genuinely different, PP-anchored convention.</summary>
        public static decimal S3Wide(decimal pp, decimal h, decimal l)
        {
            return pp - 2m * (h - l);
        }

        #endregion

        #region Camarilla

        // Multiply the range before dividing: (h-l)*1.1m/12m keeps full decimal precision,
        // where (1.1m/12m) alone would round at the 28th digit before it ever meets the range.
        private static decimal CamOffset(decimal h, decimal l, int n)
        {
            switch (n)
            {
                case 1: return (h - l) * 1.1m / 12m;
                case 2: return (h - l) * 1.1m / 6m;
                case 3: return (h - l) * 1.1m / 4m;
                case 4: return (h - l) * 1.1m / 2m;
                default: throw new ArgumentOutOfRangeException("n", "Camarilla level must be 1-4.");
            }
        }

        public static decimal CamR(decimal h, decimal l, decimal c, int n)
        {
            return c + CamOffset(h, l, n);
        }

        public static decimal CamS(decimal h, decimal l, decimal c, int n)
        {
            return c - CamOffset(h, l, n);
        }

        #endregion

        #region Level set builders

        /// <summary>
        /// The classic floor set. When <paramref name="variant"/> is Both the standard and narrow
        /// third levels are emitted ONCE, named R3sn / S3sn, because they are the same number --
        /// drawing them twice would put two lines on the same pixel and imply a choice that does
        /// not exist. If a future edit ever makes them differ, they separate automatically.
        /// </summary>
        public static List<PivotLevel> BuildFloor(string session, decimal h, decimal l, decimal c,
                                                  R3S3Variant variant, bool includeWide)
        {
            var group = "FLOOR-" + session;
            var pp = Pp(h, l, c);

            var levels = new List<PivotLevel>
            {
                Level(group, session, "PP", pp),
                Level(group, session, "R1", R1(pp, l)),
                Level(group, session, "S1", S1(pp, h)),
                Level(group, session, "R2", R2(pp, h, l)),
                Level(group, session, "S2", S2(pp, h, l))
            };

            var rs = R3Standard(pp, h, l);
            var ss = S3Standard(pp, h, l);
            var rn = R3Narrow(pp, h, l);
            var sn = S3Narrow(pp, h, l);

            if (variant == R3S3Variant.Standard)
            {
                levels.Add(Level(group, session, "R3s", rs));
                levels.Add(Level(group, session, "S3s", ss));
            }
            else if (variant == R3S3Variant.Narrow)
            {
                levels.Add(Level(group, session, "R3n", rn));
                levels.Add(Level(group, session, "S3n", sn));
            }
            else
            {
                if (rs == rn)
                {
                    levels.Add(Level(group, session, "R3sn", rs));
                }
                else
                {
                    levels.Add(Level(group, session, "R3s", rs));
                    levels.Add(Level(group, session, "R3n", rn));
                }

                if (ss == sn)
                {
                    levels.Add(Level(group, session, "S3sn", ss));
                }
                else
                {
                    levels.Add(Level(group, session, "S3s", ss));
                    levels.Add(Level(group, session, "S3n", sn));
                }
            }

            if (includeWide)
            {
                levels.Add(Level(group, session, "R3w", R3Wide(pp, h, l)));
                levels.Add(Level(group, session, "S3w", S3Wide(pp, h, l)));
            }

            return AllFamily(levels, LevelFamily.Floor);
        }

        public static List<PivotLevel> BuildCamarilla(string session, decimal h, decimal l, decimal c)
        {
            var group = "CAM-" + session;
            var levels = new List<PivotLevel>();

            for (var n = 1; n <= 4; n++)
            {
                levels.Add(Level(group, session, "cR" + n, CamR(h, l, c, n)));
                levels.Add(Level(group, session, "cS" + n, CamS(h, l, c, n)));
            }

            return AllFamily(levels, LevelFamily.Camarilla);
        }

        /// <summary>
        /// Midpoints between adjacent floor levels, in price order. Fed the floor set so it never
        /// drifts out of step with whichever R3/S3 variant is switched on.
        /// </summary>
        public static List<PivotLevel> BuildMids(string session, List<PivotLevel> floor)
        {
            var group = "MID-" + session;
            var mids = new List<PivotLevel>();
            if (floor == null || floor.Count < 2) return mids;

            var sorted = new List<PivotLevel>(floor);
            sorted.Sort(delegate (PivotLevel a, PivotLevel b) { return a.Price.CompareTo(b.Price); });

            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var lo = sorted[i];
                var hi = sorted[i + 1];
                if (lo.Price == hi.Price) continue;

                mids.Add(Level(group, session, "M " + lo.Name + "/" + hi.Name,
                               (lo.Price + hi.Price) / 2m));
            }

            return AllFamily(mids, LevelFamily.Mid);
        }

        /// <summary>
        /// The prior session's actual high, low and close. Free -- the scan already has them --
        /// and the only levels here that are not a formula. For an overnight hold these usually
        /// matter more than the pivots do.
        /// </summary>
        public static List<PivotLevel> BuildReference(string session, decimal h, decimal l, decimal c)
        {
            var group = "REF-" + session;
            var prefix = session == "RTH" ? "pRTH-" : "pETH-";

            return new List<PivotLevel>
            {
                Reference(group, session, prefix + "H", h),
                Reference(group, session, prefix + "L", l),
                Reference(group, session, prefix + "C", c)
            };
        }

        /// <summary>The POC and value-area edges of a named session window.</summary>
        public static List<PivotLevel> BuildSessionProfile(string window, string session,
                                                           Profile profile, bool edgesToo)
        {
            var levels = new List<PivotLevel>();
            if (profile == null || !profile.Valid) return levels;

            var group = "VP-" + window;

            levels.Add(Make(group, session, window + "-POC", profile.Poc, LevelFamily.SessionPoc, null));

            if (edgesToo)
            {
                levels.Add(Make(group, session, window + "-VAH", profile.Vah, LevelFamily.SessionPoc, null));
                levels.Add(Make(group, session, window + "-VAL", profile.Val, LevelFamily.SessionPoc, null));
            }

            return levels;
        }

        /// <summary>A daily POC price has never traded back through. A magnet.</summary>
        public static PivotLevel BuildNakedPoc(NakedPoc poc)
        {
            return Make("NPOC", "RTH", "nPOC", poc.Price, LevelFamily.NakedPoc,
                        "naked, " + poc.AgeDays + "d");
        }

        /// <summary>A poor high or low: an auction cut off rather than exhausted.</summary>
        public static PivotLevel BuildPoorExtreme(PoorExtreme extreme)
        {
            var level = Make("POOR-" + extreme.Window, extreme.Window,
                             extreme.IsHigh ? "POOR-H" : "POOR-L",
                             extreme.Price, LevelFamily.PoorExtreme, extreme.Reason);

            level.Marker = PoorMarker;
            return level;
        }

        /// <summary>The marker a poor extreme carries on the chart.</summary>
        public const string PoorMarker = "\u00ac";

        /// <summary>Session VWAP, prior or developing.</summary>
        public static PivotLevel BuildVwap(string window, string session, decimal vwap, bool developing)
        {
            return Make("VWAP-" + window, session,
                        window + (developing ? "-VWAPd" : "-VWAP"),
                        vwap, LevelFamily.Vwap, developing ? "developing" : "prior");
        }

        public static PivotLevel Make(string group, string session, string name, decimal price,
                                      LevelFamily family, string detail)
        {
            return new PivotLevel
            {
                Group = group,
                Session = session,
                Name = name,
                Price = price,
                Family = family,
                Detail = detail
            };
        }

        /// <summary>Stamps a family onto every level in a set. Called by the pivot builders.</summary>
        private static List<PivotLevel> AllFamily(List<PivotLevel> levels, LevelFamily family)
        {
            foreach (var level in levels) level.Family = family;
            return levels;
        }

        private static PivotLevel Reference(string group, string session, string name, decimal price)
        {
            var level = Level(group, session, name, price);
            level.Family = LevelFamily.PriorHlc;
            return level;
        }

        private static PivotLevel Level(string group, string session, string name, decimal price)
        {
            return new PivotLevel { Group = group, Session = session, Name = name, Price = price };
        }

        #endregion

        #region Validation case

        // Validation case, from a real prior RTH session:
        //
        //     H = 29211.75   L = 29017.25   C = 29186.25   range = 194.50
        //
        //     floor PP      = 87415.25 / 3        = 29138.416666...  -> 29138.42
        //     classic S3    = 2PP - 2H + L        = 28870.583333...  -> 28870.58
        //     Camarilla R4  = C + 194.50 * 1.1/2  = 29293.225        -> 29293.23
        //
        // Two things this pins down, both of which have bitten this kind of tool before:
        //
        // 1. S3 is 28870.58, not 28870.59. 28870.59 is what you get by rounding PP to 2 dp FIRST
        //    (29138.42 -> S1 = 29065.09 -> S3 = 28870.59). A caller quoting .59 is rounding early;
        //    that is a one-cent artefact, invisible against any tolerance above 0.01, and it is
        //    NOT evidence of a different formula. Do not "fix" the math to reproduce it.
        //
        // 2. 29293.225 rounds to 29293.23 only under MidpointRounding.AwayFromZero. Banker's
        //    rounding -- the .NET default -- gives 29293.22. Every display path here therefore
        //    goes through Round2.
        //
        // If a build cannot reproduce these three numbers, the fault is the session filtering or
        // the formula variant, not the tolerance.

        public const decimal CaseHigh = 29211.75m;
        public const decimal CaseLow = 29017.25m;
        public const decimal CaseClose = 29186.25m;

        /// <summary>
        /// Debug assertion path. Returns false and fills <paramref name="report"/> when the
        /// arithmetic has drifted. The indicator can surface this on the chart, and the test
        /// harness fails the build on it before anything is deployed.
        /// </summary>
        public static bool SelfTest(out string report)
        {
            var lines = new List<string>();
            var ok = true;

            var h = CaseHigh;
            var l = CaseLow;
            var c = CaseClose;

            var pp = Pp(h, l, c);
            ok &= Check(lines, "floor PP", Round2(pp), 29138.42m);
            ok &= Check(lines, "classic S3", Round2(S3Standard(pp, h, l)), 28870.58m);
            ok &= Check(lines, "Camarilla R4", Round2(CamR(h, l, c, 4)), 29293.23m);

            // The identity the R3S3Variant comment rests on. If this ever fails, the two
            // "variants" have stopped being the same formula and the labelling must change.
            ok &= Check(lines, "S3 standard == narrow", S3Standard(pp, h, l), S3Narrow(pp, h, l));
            ok &= Check(lines, "R3 standard == narrow", R3Standard(pp, h, l), R3Narrow(pp, h, l));

            // The wide pair must stay far away, or the decoder cannot tell the conventions apart.
            ok &= Check(lines, "wide S3", Round2(S3Wide(pp, h, l)), 28749.42m);

            // The one-cent early-rounding artefact, pinned so nobody re-derives it by accident.
            var ppRounded = Round2(pp);
            ok &= Check(lines, "S3 via pre-rounded PP",
                        Round2(2m * ppRounded - 2m * h + l), 28870.59m);

            report = string.Join(Environment.NewLine, lines.ToArray());
            return ok;
        }

        private static bool Check(List<string> lines, string name, decimal got, decimal want)
        {
            var ok = got == want;
            lines.Add((ok ? "ok   " : "FAIL ") + name +
                      "  got " + got.ToString(CultureInfo.InvariantCulture) +
                      "  want " + want.ToString(CultureInfo.InvariantCulture));
            return ok;
        }

        #endregion

        /// <summary>
        /// Half-up rounding. The .NET default is banker's rounding, which turns 29293.225 into
        /// 29293.22 and quietly breaks the validation case.
        /// </summary>
        public static decimal Round2(decimal v)
        {
            return Math.Round(v, 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Nearest tradeable price. A pivot lands wherever the arithmetic puts it -- 28870.58 on
        /// the validation case -- and that is not a price any exchange will accept: MNQ trades in
        /// quarter points. The line stays at the exact level and the MATCHING stays exact, because
        /// decoding a caller's formula needs full precision; only the label a limit order gets
        /// typed from is snapped.
        /// </summary>
        public static decimal SnapToTick(decimal price, decimal tick)
        {
            if (tick <= 0m) return price;

            return Math.Round(price / tick, 0, MidpointRounding.AwayFromZero) * tick;
        }
    }

    #endregion

    #region Caller levels

    /// <summary>One price the caller quoted, as typed and as resolved.</summary>
    public sealed class CallerLevel
    {
        /// <summary>Exactly what was typed, e.g. "872".</summary>
        public string Raw;

        public decimal Price;
        public bool Resolved;

        /// <summary>Why it could not be resolved, when it could not.</summary>
        public string Problem;

        public CallDirection Direction = CallDirection.Unknown;

        /// <summary>True when Direction came from CallerDirections rather than from the open.</summary>
        public bool DirectionOverridden;
    }

    public static class CallerParse
    {
        /// <summary>
        /// Parses the caller list. Accepts full prices ("28872") and last-three-digit shorthand
        /// ("872", "872.5"), resolving shorthand against the thousands of a reference price --
        /// the last close.
        ///
        /// Shorthand is decided by the DIGITS TYPED, not by magnitude: a token whose integer part
        /// is three digits or fewer is shorthand. Candidates are the same three digits in the
        /// thousand below, at, and above the reference; the nearest one wins, and only if it lands
        /// within <paramref name="maxDistance"/>.
        ///
        /// Note what that guard can and cannot do. The candidates sit 1000 apart with the
        /// reference between two of them, so the nearest is ALWAYS within 500 -- at the default
        /// setting nothing is ever rejected. It is a tightening knob for the case that really is
        /// ambiguous: "500" quoted with spot at 29100 could mean 29500 or 28500, and this picks
        /// the nearer one. Drop the range below that distance and it reports the ambiguity
        /// instead. Nothing is ever silently placed on a guess -- a mis-resolved caller level
        /// would make the whole match table lie.
        /// </summary>
        public static List<CallerLevel> Parse(string raw, decimal reference, decimal maxDistance)
        {
            var list = new List<CallerLevel>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            var tokens = raw.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                var t = token.Trim();
                if (t.Length == 0) continue;

                var level = new CallerLevel { Raw = t };

                decimal v;
                if (!decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                {
                    level.Problem = "not a number";
                    list.Add(level);
                    continue;
                }

                if (!IsShorthand(t))
                {
                    level.Price = v;
                    level.Resolved = true;
                    list.Add(level);
                    continue;
                }

                if (reference <= 0m)
                {
                    level.Problem = "shorthand needs a price to resolve against";
                    list.Add(level);
                    continue;
                }

                var basis = Math.Floor(reference / 1000m) * 1000m;
                var best = 0m;
                var bestDist = decimal.MaxValue;

                for (var k = -1; k <= 1; k++)
                {
                    var candidate = basis + k * 1000m + v;
                    var dist = Math.Abs(candidate - reference);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = candidate;
                    }
                }

                if (bestDist > maxDistance)
                {
                    level.Problem = "no candidate within " +
                                    maxDistance.ToString(CultureInfo.InvariantCulture) + " pts";
                    list.Add(level);
                    continue;
                }

                level.Price = best;
                level.Resolved = true;
                list.Add(level);
            }

            return list;
        }

        /// <summary>Three or fewer digits before the decimal point means last-three shorthand.</summary>
        private static bool IsShorthand(string token)
        {
            var t = token.Trim();
            if (t.StartsWith("+") || t.StartsWith("-")) t = t.Substring(1);

            var dot = t.IndexOf('.');
            var intPart = dot < 0 ? t : t.Substring(0, dot);

            return intPart.Length <= 3;
        }

        /// <summary>
        /// Parses the direction override, "872:L,293:S". Keys are matched against the caller token
        /// exactly as typed, and failing that against the last three digits of the resolved price,
        /// so "872:L" works whether the level was entered as 872 or as 28872.
        /// </summary>
        public static void ApplyDirections(List<CallerLevel> levels, string spec)
        {
            if (levels == null || string.IsNullOrWhiteSpace(spec)) return;

            var map = new Dictionary<string, CallDirection>(StringComparer.OrdinalIgnoreCase);

            var entries = spec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var parts = entry.Split(':');
                if (parts.Length != 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();
                if (key.Length == 0 || value.Length == 0) continue;

                var dir = char.ToUpperInvariant(value[0]) == 'L' ? CallDirection.Long
                        : char.ToUpperInvariant(value[0]) == 'S' ? CallDirection.Short
                        : CallDirection.Unknown;

                if (dir != CallDirection.Unknown) map[key] = dir;
            }

            foreach (var level in levels)
            {
                CallDirection dir;

                if (map.TryGetValue(level.Raw, out dir) ||
                    (level.Resolved && map.TryGetValue(LastThree(level.Price), out dir)))
                {
                    level.Direction = dir;
                    level.DirectionOverridden = true;
                }
            }
        }

        /// <summary>Last three digits of the integer part, as the caller would say them.</summary>
        public static string LastThree(decimal price)
        {
            var whole = (long)Math.Floor(Math.Abs(price));
            return (whole % 1000).ToString("000", CultureInfo.InvariantCulture);
        }
    }

    #endregion

    #region Matching

    /// <summary>One caller level measured against the nearest level of one family.</summary>
    public sealed class FamilyMatch
    {
        public string Family;
        public PivotLevel Level;

        /// <summary>Computed level minus caller level. Positive: the computed level sits above.</summary>
        public decimal Delta;

        public Verdict Verdict;

        public decimal AbsDelta { get { return Math.Abs(Delta); } }
    }

    /// <summary>Everything known about one caller level this session.</summary>
    public sealed class CallerReport
    {
        public CallerLevel Caller;

        /// <summary>Nearest level in every enabled family, sorted by absolute delta.</summary>
        public List<FamilyMatch> Matches = new List<FamilyMatch>();

        public FamilyMatch Best { get { return Matches.Count > 0 ? Matches[0] : null; } }

        public Reaction Reaction;
    }

    public static class Matcher
    {
        /// <summary>
        /// For each caller level, the nearest computed level in every family, sorted nearest
        /// first. Families are kept separate on purpose: the whole point is seeing that FLOOR-RTH
        /// lands on the number while CAM-ETH is 60 points away.
        /// </summary>
        public static List<CallerReport> Build(List<CallerLevel> callers, List<PivotLevel> levels,
                                               decimal tolerance, decimal loose)
        {
            var reports = new List<CallerReport>();
            if (callers == null) return reports;

            var byFamily = new Dictionary<string, List<PivotLevel>>();
            var order = new List<string>();

            if (levels != null)
            {
                foreach (var level in levels)
                {
                    List<PivotLevel> bucket;
                    if (!byFamily.TryGetValue(level.Group, out bucket))
                    {
                        bucket = new List<PivotLevel>();
                        byFamily[level.Group] = bucket;
                        order.Add(level.Group);
                    }
                    bucket.Add(level);
                }
            }

            foreach (var caller in callers)
            {
                var report = new CallerReport { Caller = caller };

                if (caller.Resolved)
                {
                    foreach (var family in order)
                    {
                        var nearest = Nearest(byFamily[family], caller.Price);
                        if (nearest == null) continue;

                        var delta = nearest.Price - caller.Price;

                        report.Matches.Add(new FamilyMatch
                        {
                            Family = family,
                            Level = nearest,
                            Delta = delta,
                            Verdict = Judge(Math.Abs(delta), tolerance, loose)
                        });
                    }

                    report.Matches.Sort(delegate (FamilyMatch a, FamilyMatch b)
                    {
                        return a.AbsDelta.CompareTo(b.AbsDelta);
                    });
                }

                reports.Add(report);
            }

            return reports;
        }

        public static Verdict Judge(decimal absDelta, decimal tolerance, decimal loose)
        {
            if (absDelta <= tolerance) return Verdict.Exact;
            if (absDelta <= loose) return Verdict.Near;
            return Verdict.NoMatch;
        }

        private static PivotLevel Nearest(List<PivotLevel> levels, decimal price)
        {
            PivotLevel best = null;
            var bestDist = decimal.MaxValue;

            foreach (var level in levels)
            {
                var dist = Math.Abs(level.Price - price);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = level;
                }
            }

            return best;
        }
    }

    #endregion

    #region Reaction tracking

    /// <summary>Thresholds for the verification protocol, all in points.</summary>
    public sealed class ReactionConfig
    {
        /// <summary>How far price must travel from the level to count as a reaction at all.</summary>
        public decimal AwayPts = 30m;

        /// <summary>How long after the first touch that move has to happen.</summary>
        public double WindowHours = 2.0;

        /// <summary>Favourable excursion that counts as the call working.</summary>
        public decimal TargetPts = 50m;

        /// <summary>Adverse excursion that counts as the call failing.</summary>
        public decimal StopPts = 30m;
    }

    /// <summary>What price did after first touching a level.</summary>
    public sealed class Reaction
    {
        public bool Touched;
        public int TouchBar = -1;
        public DateTime TouchLocal;

        /// <summary>Largest move in either direction inside the window, in points.</summary>
        public decimal MaxAwayInWindow;

        public bool MovedAwayInWindow;

        public decimal MaxFavorable;
        public decimal MaxAdverse;

        public CallDirection Direction = CallDirection.Unknown;
        public ReactionOutcome Outcome = ReactionOutcome.Untouched;

        public int ResolvedBar = -1;
    }

    public static class ReactionTracker
    {
        /// <summary>
        /// Replays the current session's bars against one level. Deliberately a pure scan rather
        /// than incremental state: it is re-run from scratch on every rebuild, so a repaint, a
        /// history reload, or a reconnect cannot leave half-updated tracking behind.
        ///
        /// Direction, when not overridden, is inferred the way the caller's own note implies: a
        /// level BELOW the session open is support, so it is a long; above the open is a short.
        /// </summary>
        public static Reaction Track(IBarWindow bars, BarClockContext clock, int firstBar, int lastBar,
                                     decimal level, decimal sessionOpen, CallDirection forced,
                                     ReactionConfig cfg)
        {
            var r = new Reaction();

            r.Direction = forced != CallDirection.Unknown
                ? forced
                : level < sessionOpen ? CallDirection.Long : CallDirection.Short;

            if (bars == null || firstBar < 0 || lastBar >= bars.Count || firstBar > lastBar) return r;

            for (var i = firstBar; i <= lastBar; i++)
            {
                var high = bars.High(i);
                var low = bars.Low(i);

                if (!r.Touched)
                {
                    if (low > level || high < level) continue;

                    r.Touched = true;
                    r.TouchBar = i;
                    r.Outcome = ReactionOutcome.Open;

                    try { r.TouchLocal = clock != null ? clock.ToLocal(bars.Time(i)) : bars.Time(i); }
                    catch { r.TouchLocal = default(DateTime); }
                }

                var favorable = r.Direction == CallDirection.Long ? high - level : level - low;
                var adverse = r.Direction == CallDirection.Long ? level - low : high - level;

                if (favorable > r.MaxFavorable) r.MaxFavorable = favorable;
                if (adverse > r.MaxAdverse) r.MaxAdverse = adverse;

                var away = Math.Max(high - level, level - low);
                if (InWindow(clock, bars, i, r.TouchLocal, cfg.WindowHours))
                {
                    if (away > r.MaxAwayInWindow) r.MaxAwayInWindow = away;
                    if (r.MaxAwayInWindow >= cfg.AwayPts) r.MovedAwayInWindow = true;
                }

                if (r.Outcome == ReactionOutcome.Open)
                {
                    var hitTarget = favorable >= cfg.TargetPts;
                    var hitStop = adverse >= cfg.StopPts;

                    // Both inside one bar: the bar has no sequence, so this is recorded as
                    // ambiguous rather than guessed. Guessing here would quietly inflate the
                    // win rate of the very protocol this indicator exists to measure.
                    if (hitTarget && hitStop) { r.Outcome = ReactionOutcome.Ambiguous; r.ResolvedBar = i; }
                    else if (hitStop) { r.Outcome = ReactionOutcome.Stop; r.ResolvedBar = i; }
                    else if (hitTarget) { r.Outcome = ReactionOutcome.Target; r.ResolvedBar = i; }
                }
            }

            return r;
        }

        private static bool InWindow(BarClockContext clock, IBarWindow bars, int bar,
                                     DateTime touchLocal, double hours)
        {
            if (touchLocal == default(DateTime)) return false;

            try
            {
                var local = clock != null ? clock.ToLocal(bars.Time(bar)) : bars.Time(bar);
                return (local - touchLocal).TotalHours <= hours;
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion
}
