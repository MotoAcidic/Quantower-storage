using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OceansAsiaWick;

namespace OceansAsiaWick.Tests
{
    /// <summary>
    /// Math harness for the AsiaWick signal core, session clock and risk rules. Nothing here
    /// touches ATAS. deploy.ps1 runs this first and refuses to deploy on a non-zero exit.
    ///
    /// Acceptance tests 1-4 from the build spec live here. Test 5 (cross-validation against the
    /// Python backtest) and test 6 (Market Replay) are manual and are tracked in README.md.
    /// </summary>
    internal static class Program
    {
        private static int _pass;
        private static readonly List<string> _fail = new List<string>();

        private static readonly TimeZoneInfo Ct =
            TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

        private static int Main(string[] args)
        {
            // Cross-validation mode (acceptance test 5). Emits the signal set for a bar CSV
            // exported from ATAS so it can be diffed against asia_wick_backtest.py on the very
            // same bars. Every mismatch gets investigated -- none of it gets averaged away.
            //
            //   dotnet run -c Release -- bars.csv [utc|local]
            if (args != null && args.Length > 0)
                return CrossValidate(args);

            Case("trade date mapping", TradeDateMapping);
            Case("DST boundaries", DstBoundaries);
            Case("session edges", SessionEdges);
            Case("bar interval", BarIntervalDetection);

            Case("PDH sweep", PdhSweepVariant);
            Case("Asia-high sweep", AsiaHighSweepVariant);
            Case("wick rejection", WickRejectionVariant);
            Case("wick % boundary", WickPercentBoundary);
            Case("level predates bar", LevelMustPredateBar);
            Case("one per night", OnePerNightPerVariant);
            Case("night roll", NightRollResetsState);
            Case("half day PDH", PdhSurvivesHalfDay);

            Case("line segmenting", LineSegmenting);

            Case("idempotency", Idempotency);

            Case("tier-1 forced flat", Tier1ForcedFlat);
            Case("exit time", ExitTimeDoesNotFireInTheEvening);
            Case("Friday and weekend", FridayAndWeekend);
            Case("loss limit", LossLimitAndDisable);
            Case("stop price and dates", StopPriceAndDateParsing);

            Console.WriteLine();
            Console.WriteLine("passed " + _pass + ", failed " + _fail.Count);

            foreach (var f in _fail) Console.WriteLine("  FAIL  " + f);

            // deploy.ps1 gates on this.
            return _fail.Count == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ cross-validation

        /// <summary>
        /// Reads a bar CSV and prints one line per signal. Deliberately strict about the clock:
        /// the caller says whether the stamps are UTC or already Central, because a CSV carries
        /// no evidence either way and a guess here would produce a plausible, wrong signal set.
        /// </summary>
        private static int CrossValidate(string[] args)
        {
            var path = args[0];
            var mode = args.Length > 1 ? args[1].ToLowerInvariant() : "local";

            if (!File.Exists(path))
            {
                Console.Error.WriteLine("no such file: " + path);
                return 2;
            }

            if (mode != "utc" && mode != "local")
            {
                Console.Error.WriteLine("second argument must be 'utc' or 'local'.");
                return 2;
            }

            Bars bars;
            try
            {
                bars = ReadCsv(path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("could not read " + path + ": " + ex.Message);
                return 2;
            }

            if (bars.Count == 0)
            {
                Console.Error.WriteLine("no bars parsed from " + path);
                return 2;
            }

            var clock = SessionClock.Fixed(Ct, mode == "utc" ? BarClock.Utc : BarClock.AlreadyLocal);
            var eng = new AsiaWickEngine(Stock(), clock);
            var all = new List<WickSignal>();

            for (var i = 0; i < bars.Count; i++)
                eng.Fold(i, bars.T[i], bars.O[i], bars.H[i], bars.L[i], bars.C[i], all);

            Console.WriteLine("# bars " + bars.Count + "  " + bars.T[0].ToString("yyyy-MM-dd HH:mm") +
                              " .. " + bars.T[bars.Count - 1].ToString("yyyy-MM-dd HH:mm") +
                              "  (stamps read as " + mode + ", " +
                              AsiaWickUtil.BarMinutes(bars) + "m)");
            Console.WriteLine("# trade_date,time_ct,variant,signal_price,stop_reference");

            foreach (var s in all)
                Console.WriteLine(string.Join(",",
                    s.TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    s.TimeCt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    s.Variant.ToString(),
                    s.SignalPrice.ToString(CultureInfo.InvariantCulture),
                    s.StopReference.ToString(CultureInfo.InvariantCulture)));

            Console.WriteLine("# signals " + all.Count);
            return 0;
        }

        /// <summary>
        /// Tolerant of the usual ATAS/pandas export shapes: finds the columns by header name and
        /// accepts either one datetime column or a separate date and time.
        /// </summary>
        private static Bars ReadCsv(string path)
        {
            var bars = new Bars();
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0) return bars;

            var head = lines[0].Split(',').Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToArray();

            Func<string[], int> find = names =>
            {
                for (var i = 0; i < head.Length; i++)
                    if (names.Contains(head[i])) return i;
                return -1;
            };

            var iDate = find(new[] { "date", "datetime", "time", "timestamp", "date/time" });
            var iTime = find(new[] { "time" });
            var iOpen = find(new[] { "open", "o" });
            var iHigh = find(new[] { "high", "h" });
            var iLow = find(new[] { "low", "l" });
            var iClose = find(new[] { "close", "c" });

            if (iDate < 0 || iOpen < 0 || iHigh < 0 || iLow < 0 || iClose < 0)
                throw new Exception("need date/open/high/low/close columns; header was: " +
                                    string.Join("|", head));

            // Only a genuinely separate time column counts.
            if (iTime == iDate) iTime = -1;

            var inv = CultureInfo.InvariantCulture;

            for (var r = 1; r < lines.Length; r++)
            {
                var line = lines[r].Trim();
                if (line.Length == 0) continue;

                var f = line.Split(',').Select(x => x.Trim().Trim('"')).ToArray();
                if (f.Length <= iClose) continue;

                var stamp = iTime >= 0 && f.Length > iTime ? f[iDate] + " " + f[iTime] : f[iDate];

                DateTime t;
                if (!DateTime.TryParse(stamp, inv, DateTimeStyles.None, out t)) continue;

                decimal o, h, l, c;
                if (!decimal.TryParse(f[iOpen], NumberStyles.Any, inv, out o)) continue;
                if (!decimal.TryParse(f[iHigh], NumberStyles.Any, inv, out h)) continue;
                if (!decimal.TryParse(f[iLow], NumberStyles.Any, inv, out l)) continue;
                if (!decimal.TryParse(f[iClose], NumberStyles.Any, inv, out c)) continue;

                bars.Add(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), o, h, l, c);
            }

            return bars;
        }

        // ------------------------------------------------------------------ assert helpers

        /// <summary>
        /// Runs one group and turns a throw into a reported failure. Without this a broken
        /// assumption aborts the whole suite on the first bad index -- which is exactly what a
        /// regression looks like, and it would take every later test down with it unread.
        /// </summary>
        private static void Case(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                _fail.Add(name + " THREW: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>
        /// Indexing a signal list straight after asserting its count turns a real regression
        /// into a throw at the wrong line. This keeps the follow-up assertions readable by
        /// failing them individually instead.
        /// </summary>
        private static WickSignal At(List<WickSignal> got, int i)
        {
            return i >= 0 && i < got.Count ? got[i] : new WickSignal();
        }

        private static void Ok(bool cond, string what)
        {
            if (cond) _pass++;
            else _fail.Add(what);
        }

        private static void Eq<T>(T actual, T expected, string what)
        {
            var good = EqualityComparer<T>.Default.Equals(actual, expected);
            if (good) _pass++;
            else _fail.Add(what + "  (got " + actual + ", wanted " + expected + ")");
        }

        // ------------------------------------------------------------------ fixtures

        private sealed class Bars : IBarWindow
        {
            public readonly List<DateTime> T = new List<DateTime>();
            public readonly List<decimal> O = new List<decimal>();
            public readonly List<decimal> H = new List<decimal>();
            public readonly List<decimal> L = new List<decimal>();
            public readonly List<decimal> C = new List<decimal>();

            public int Count { get { return T.Count; } }
            public DateTime Time(int b) { return T[b]; }
            public decimal Open(int b) { return O[b]; }
            public decimal High(int b) { return H[b]; }
            public decimal Low(int b) { return L[b]; }
            public decimal Close(int b) { return C[b]; }

            public Bars Add(DateTime t, decimal o, decimal h, decimal l, decimal c)
            {
                T.Add(t); O.Add(o); H.Add(h); L.Add(l); C.Add(c);
                return this;
            }

            /// <summary>A flat, uneventful bar -- scaffolding, never the thing under test.</summary>
            public Bars Quiet(DateTime t, decimal price)
            {
                return Add(t, price, price + 1m, price - 1m, price);
            }
        }

        /// <summary>Bars are already Central here; the UTC path is tested separately.</summary>
        private static SessionClock LocalClock()
        {
            return SessionClock.Fixed(Ct, BarClock.AlreadyLocal);
        }

        private static AsiaWickSettings Stock()
        {
            return new AsiaWickSettings();
        }

        /// <summary>Runs a bar series through a fresh engine and returns every signal.</summary>
        private static List<WickSignal> Run(Bars b, AsiaWickSettings s)
        {
            var eng = new AsiaWickEngine(s, LocalClock());
            var fresh = new List<WickSignal>();

            for (var i = 0; i < b.Count; i++)
                eng.Fold(i, b.T[i], b.O[i], b.H[i], b.L[i], b.C[i], fresh);

            return fresh;
        }

        /// <summary>
        /// An RTH window on <paramref name="day"/> topping out at <paramref name="high"/>, then
        /// a bar past 15:00 so the window is COMPLETE and the high commits to PDH.
        /// </summary>
        private static Bars RthDay(Bars b, DateTime day, decimal high)
        {
            b.Quiet(day.AddHours(9), high - 50m);
            b.Add(day.AddHours(12), high - 20m, high, high - 40m, high - 30m);
            b.Quiet(day.AddHours(14), high - 25m);
            b.Quiet(day.AddHours(15).AddMinutes(30), high - 60m);   // past RTH end: commits PDH
            return b;
        }

        // ------------------------------------------------------------------ 1. time & session

        private static void TradeDateMapping()
        {
            var mon = new DateTime(2026, 9, 14);

            // The Globex roll is 17:00 Central.
            Eq(AsiaWickEngine.TradeDateOf(mon.AddHours(16).AddMinutes(59)), mon.AddDays(-1),
               "16:59 belongs to the previous trade date");
            Eq(AsiaWickEngine.TradeDateOf(mon.AddHours(17)), mon,
               "17:00 starts the new trade date");
            Eq(AsiaWickEngine.TradeDateOf(mon.AddHours(18)), mon,
               "18:00 Asia open is the same trade date");
            Eq(AsiaWickEngine.TradeDateOf(mon.AddDays(1).AddHours(1)), mon,
               "01:00 the next morning is still the same trade date");
            Eq(AsiaWickEngine.TradeDateOf(mon.AddDays(1).AddHours(9)), mon,
               "the RTH morning after the night belongs to that night's trade date");

            // Sunday 18:00 is a valid Asia start and rolls to the Sunday trade date.
            var sun = new DateTime(2026, 9, 13);
            Eq(sun.DayOfWeek, DayOfWeek.Sunday, "fixture Sunday is actually a Sunday");
            Eq(AsiaWickEngine.TradeDateOf(sun.AddHours(18)), sun,
               "Sunday reopen maps to the Sunday trade date");
        }

        private static void DstBoundaries()
        {
            // The trap this guards: a bar arrives as a UTC instant, and 18:00 Central is 23:00Z
            // in summer but 00:00Z in winter. Anything that pins a fixed offset drifts an hour
            // across these dates and silently shifts the whole Asia window.
            var clock = SessionClock.Fixed(Ct, BarClock.Utc);

            var springFwd = new DateTime(2026, 3, 8);    // 2nd Sunday of March
            var fallBack = new DateTime(2026, 11, 1);    // 1st Sunday of November

            Ok(Ct.IsDaylightSavingTime(springFwd.AddDays(1).AddHours(18)),
               "the day after spring-forward is DST");
            Ok(!Ct.IsDaylightSavingTime(fallBack.AddDays(1).AddHours(18)),
               "the day after fall-back is standard time");

            foreach (var day in new[] { springFwd.AddDays(1), fallBack.AddDays(1) })
            {
                var ctWanted = day.AddHours(18);

                var utc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(ctWanted, DateTimeKind.Unspecified), Ct);

                var back = clock.ToLocal(utc);

                Eq(back, ctWanted, "18:00 CT round-trips through UTC on " + day.ToString("MMM d"));
                Eq(AsiaWickEngine.TradeDateOf(back), day,
                   "Asia open keeps its trade date on " + day.ToString("MMM d"));
            }

            // And the offset really did differ between the two, or the test above proved nothing.
            var sOff = Ct.GetUtcOffset(springFwd.AddDays(1).AddHours(18));
            var fOff = Ct.GetUtcOffset(fallBack.AddDays(1).AddHours(18));
            Ok(sOff != fOff, "the two DST fixtures genuinely straddle a UTC offset change");
        }

        private static void SessionEdges()
        {
            var asiaStart = new TimeSpan(18, 0, 0);
            var asiaEnd = new TimeSpan(2, 0, 0);

            Ok(!AsiaWickEngine.InWindow(new TimeSpan(17, 59, 0), asiaStart, asiaEnd), "17:59 outside Asia");
            Ok(AsiaWickEngine.InWindow(new TimeSpan(18, 0, 0), asiaStart, asiaEnd), "18:00 inside Asia");
            Ok(AsiaWickEngine.InWindow(new TimeSpan(1, 59, 0), asiaStart, asiaEnd), "01:59 inside Asia");
            Ok(!AsiaWickEngine.InWindow(new TimeSpan(2, 0, 0), asiaStart, asiaEnd), "02:00 outside Asia");
            Ok(AsiaWickEngine.InWindow(new TimeSpan(23, 30, 0), asiaStart, asiaEnd), "23:30 inside Asia");
            Ok(!AsiaWickEngine.InWindow(new TimeSpan(12, 0, 0), asiaStart, asiaEnd), "noon outside Asia");

            var rthStart = new TimeSpan(8, 30, 0);
            var rthEnd = new TimeSpan(15, 0, 0);

            Ok(!AsiaWickEngine.InWindow(new TimeSpan(8, 29, 0), rthStart, rthEnd), "08:29 outside RTH");
            Ok(AsiaWickEngine.InWindow(new TimeSpan(8, 30, 0), rthStart, rthEnd), "08:30 inside RTH");
            Ok(AsiaWickEngine.InWindow(new TimeSpan(14, 59, 0), rthStart, rthEnd), "14:59 inside RTH");
            Ok(!AsiaWickEngine.InWindow(new TimeSpan(15, 0, 0), rthStart, rthEnd), "15:00 outside RTH");
        }

        private static void BarIntervalDetection()
        {
            var b = new Bars();
            var t = new DateTime(2026, 9, 14, 18, 0, 0);

            for (var i = 0; i < 20; i++) b.Quiet(t.AddMinutes(30 * i), 20000m);

            // A session break must not drag the median.
            b.Quiet(t.AddMinutes(30 * 20).AddHours(6), 20000m);

            Eq(AsiaWickUtil.BarMinutes(b), 30, "30m bars measured from the stamps");

            var five = new Bars();
            for (var i = 0; i < 20; i++) five.Quiet(t.AddMinutes(5 * i), 20000m);
            Eq(AsiaWickUtil.BarMinutes(five), 5, "5m bars measured from the stamps");
        }

        // ------------------------------------------------------------------ 2. signal variants

        private static void PdhSweepVariant()
        {
            var s = Stock();
            s.EnableAsiaHighSweep = false;
            s.EnableWickRejection = false;

            var mon = new DateTime(2026, 9, 14);

            // Sweeps 20000 and closes back under.
            var hit = new Bars();
            RthDay(hit, mon, 20000m);
            hit.Quiet(mon.AddHours(18), 19950m);
            hit.Add(mon.AddHours(19), 19960m, 20010m, 19950m, 19980m);

            var got = Run(hit, s);
            Eq(got.Count, 1, "PDH sweep fires once");
            Eq(At(got, 0).Variant, SignalVariant.PdhSweep, "PDH sweep variant");
            Eq(At(got, 0).StopReference, 20010m, "stop reference is the signal bar high");

            // Traded through but CLOSED ABOVE: not a sweep.
            var closedAbove = new Bars();
            RthDay(closedAbove, mon, 20000m);
            closedAbove.Quiet(closedAbove.T[0].Date.AddHours(18), 19950m);
            closedAbove.Add(mon.AddHours(19), 19960m, 20010m, 19950m, 20005m);
            Eq(Run(closedAbove, s).Count, 0, "close back above the PDH is not a sweep");

            // Never reached the level.
            var never = new Bars();
            RthDay(never, mon, 20000m);
            never.Quiet(mon.AddHours(18), 19950m);
            never.Add(mon.AddHours(19), 19960m, 19990m, 19950m, 19970m);
            Eq(Run(never, s).Count, 0, "not reaching the PDH is not a sweep");

            // No completed RTH at all -> the variant stays silent rather than guessing.
            var noRth = new Bars();
            noRth.Quiet(mon.AddHours(18), 19950m);
            noRth.Add(mon.AddHours(19), 19960m, 20010m, 19950m, 19980m);
            Eq(Run(noRth, s).Count, 0, "no prior RTH means no PDH signal");
        }

        private static void AsiaHighSweepVariant()
        {
            var s = Stock();
            s.EnablePdhSweep = false;
            s.EnableWickRejection = false;

            var mon = new DateTime(2026, 9, 14);

            var b = new Bars();
            b.Add(mon.AddHours(18), 19900m, 19950m, 19890m, 19940m);   // sets Asia high 19950
            b.Quiet(mon.AddHours(19), 19920m);
            b.Add(mon.AddHours(20), 19930m, 19960m, 19920m, 19935m);   // sweeps, closes under

            var got = Run(b, s);
            Eq(got.Count, 1, "Asia-high sweep fires once");
            Eq(At(got, 0).Variant, SignalVariant.AsiaHighSweep, "Asia-high sweep variant");
            Eq(At(got, 0).TimeCt, mon.AddHours(20), "fires on the sweeping bar");

            // Closing above the old Asia high is a breakout, not a sweep.
            var up = new Bars();
            up.Add(mon.AddHours(18), 19900m, 19950m, 19890m, 19940m);
            up.Add(mon.AddHours(20), 19930m, 19960m, 19920m, 19958m);
            Eq(Run(up, s).Count, 0, "closing above the Asia high is not a sweep");
        }

        private static void WickRejectionVariant()
        {
            var s = Stock();
            s.EnablePdhSweep = false;
            s.EnableAsiaHighSweep = false;

            var mon = new DateTime(2026, 9, 14);

            // O 105 / H 110 / L 100 / C 101 -> wick 5 of range 10 = 0.50, close in lower half.
            var b = new Bars();
            b.Add(mon.AddHours(18), 105m, 110m, 100m, 101m);

            var got = Run(b, s);
            Eq(got.Count, 1, "wick rejection fires on the first Asia bar");
            Eq(At(got, 0).Variant, SignalVariant.WickRejection, "wick rejection variant");

            // A doji-ish bar with no range cannot be a rejection (and must not divide by zero).
            var flat = new Bars();
            flat.Add(mon.AddHours(18), 100m, 100m, 100m, 100m);
            Eq(Run(flat, s).Count, 0, "a zero-range bar is not a rejection");

            // Bottom-heavy bar: the wick is on the wrong side.
            var down = new Bars();
            down.Add(mon.AddHours(18), 105m, 106m, 100m, 105.5m);
            Eq(Run(down, s).Count, 0, "a lower wick is not a top rejection");

            // Not a new Asia high -> not a rejection, however good the wick looks.
            var notHigh = new Bars();
            notHigh.Add(mon.AddHours(18), 100m, 200m, 100m, 100m);      // sets a high of 200
            notHigh.Add(mon.AddHours(19), 105m, 110m, 100m, 101m);      // perfect wick, but low
            Eq(Run(notHigh, s).Count, 1, "wick rejection needs a NEW Asia high");
        }

        private static void WickPercentBoundary()
        {
            var s = Stock();
            s.EnablePdhSweep = false;
            s.EnableAsiaHighSweep = false;

            var mon = new DateTime(2026, 9, 14);

            // Exactly 0.50 -> fires (the threshold is >=).
            var at = new Bars();
            at.Add(mon.AddHours(18), 105m, 110m, 100m, 101m);
            Eq(Run(at, s).Count, 1, "wick of exactly 50% fires");

            // 0.49 -> does not.
            var under = new Bars();
            under.Add(mon.AddHours(18), 105.1m, 110m, 100m, 101m);
            Eq(Run(under, s).Count, 0, "wick of 49% does not fire");

            // The close clause is strict, so a close exactly at the midpoint is refused even
            // though the wick qualifies.
            var mid = new Bars();
            mid.Add(mon.AddHours(18), 100m, 110m, 100m, 105m);
            Eq(Run(mid, s).Count, 0, "a close exactly at the midpoint does not fire");

            // And the threshold is honoured when moved.
            s.WickPct = 0.70m;
            Eq(Run(at, s).Count, 0, "a 50% wick fails a 70% threshold");
        }

        private static void LevelMustPredateBar()
        {
            var s = Stock();
            s.EnablePdhSweep = false;
            s.EnableWickRejection = false;

            var mon = new DateTime(2026, 9, 14);

            // The very first Asia bar has no prior Asia high, so it cannot sweep one. If the
            // current bar folded in before evaluation, every bar would sweep itself.
            var b = new Bars();
            b.Add(mon.AddHours(18), 19900m, 19990m, 19890m, 19900m);
            Eq(Run(b, s).Count, 0, "the first Asia bar cannot sweep the Asia high");
        }

        private static void OnePerNightPerVariant()
        {
            var s = Stock();
            s.EnablePdhSweep = false;
            s.EnableWickRejection = false;

            var mon = new DateTime(2026, 9, 14);

            var b = new Bars();
            b.Add(mon.AddHours(18), 19900m, 19950m, 19890m, 19940m);
            b.Add(mon.AddHours(19), 19930m, 19960m, 19920m, 19935m);   // sweep 1
            b.Add(mon.AddHours(20), 19930m, 19970m, 19920m, 19935m);   // sweep 2, same night
            b.Add(mon.AddHours(21), 19930m, 19980m, 19920m, 19935m);   // sweep 3, same night

            var got = Run(b, s);
            Eq(got.Count, 1, "only the first Asia-high sweep of the night fires");
            Eq(At(got, 0).TimeCt, mon.AddHours(19), "and it is the earliest one");
        }

        private static void NightRollResetsState()
        {
            var s = Stock();
            s.EnablePdhSweep = false;
            s.EnableWickRejection = false;

            var mon = new DateTime(2026, 9, 14);
            var tue = mon.AddDays(1);

            var b = new Bars();
            // Night one.
            b.Add(mon.AddHours(18), 19900m, 19950m, 19890m, 19940m);
            b.Add(mon.AddHours(19), 19930m, 19960m, 19920m, 19935m);
            // Past the 17:00 roll, night two -- a fresh Asia high and a fresh allowance.
            b.Add(tue.AddHours(18), 19900m, 19950m, 19890m, 19940m);
            b.Add(tue.AddHours(19), 19930m, 19960m, 19920m, 19935m);

            var got = Run(b, s);
            Eq(got.Count, 2, "each night gets its own signal");
            Eq(At(got, 0).TradeDate, mon, "first signal keyed to Monday");
            Eq(At(got, 1).TradeDate, tue, "second signal keyed to Tuesday");
            Ok(At(got, 0).Key != At(got, 1).Key, "per-night dedupe keys differ across nights");
        }

        private static void PdhSurvivesHalfDay()
        {
            var s = Stock();
            s.EnableAsiaHighSweep = false;
            s.EnableWickRejection = false;

            var mon = new DateTime(2026, 9, 14);

            // A short session: one bar, then out of the window. Whatever it made IS the PDH.
            var b = new Bars();
            b.Add(mon.AddHours(9), 19000m, 19100m, 18990m, 19050m);
            b.Quiet(mon.AddHours(15).AddMinutes(30), 19000m);          // commits PDH = 19100
            b.Quiet(mon.AddHours(18), 19050m);
            b.Add(mon.AddHours(19), 19060m, 19110m, 19050m, 19080m);   // sweeps 19100

            var got = Run(b, s);
            Eq(got.Count, 1, "a half-day still produces a usable PDH");
            Eq(At(got, 0).StopReference, 19110m, "half-day PDH sweep stop reference");
        }

        // ------------------------------------------------------------------ segmenting

        private static void LineSegmenting()
        {
            // Two runs with a gap either side. Zero means "window shut, draw nothing here".
            var levels = new decimal[] { 0m, 0m, 5m, 5m, 5m, 0m, 0m, 7m, 7m, 0m };

            var seg = new LineSegmenter();
            var breaks = new List<int>();
            var values = new List<int>();

            for (var i = 0; i < levels.Length; i++)
            {
                var step = seg.Step(i, levels[i]);
                if (step.BreakBefore) breaks.Add(i - 1);
                if (step.HasValue) values.Add(i);
            }

            // Values only inside the runs.
            Eq(string.Join(",", values), "2,3,4,7,8", "level is set only inside a run");

            // THE regression: a run must be cut before it opens as well as after it closes.
            // Breaking only at the end leaves the preceding zero bar joined to the first real
            // value, which draws a vertical line off the bottom of the chart at every segment's
            // left edge. Bars 1 and 6 are the "before", bars 4 and 8 the "after".
            Eq(string.Join(",", breaks), "1,4,6,8", "every run is broken at BOTH ends");

            // A run that opens on bar 0 has nothing behind it to cut.
            var atZero = new LineSegmenter();
            var first = atZero.Step(0, 5m);
            Ok(!first.BreakBefore, "a run starting at bar 0 does not break behind itself");
            Ok(first.HasValue, "a run starting at bar 0 still sets its value");

            // A level that never opens produces no instructions at all.
            var quiet = new LineSegmenter();
            var any = false;
            for (var i = 0; i < 5; i++)
            {
                var st = quiet.Step(i, 0m);
                if (st.BreakBefore || st.HasValue) any = true;
            }
            Ok(!any, "a level that never opens emits nothing");
            Ok(!quiet.Open, "and reports itself closed");

            // Reset must forget an open run, or the first bar of a recalculation breaks a line
            // that no longer exists.
            var re = new LineSegmenter();
            re.Step(0, 5m);
            Ok(re.Open, "a run is open after a value");
            re.Reset();
            Ok(!re.Open, "Reset closes an open run");
        }

        // ------------------------------------------------------------------ 3. idempotency

        private static void Idempotency()
        {
            var s = Stock();
            var mon = new DateTime(2026, 9, 14);

            var b = new Bars();
            RthDay(b, mon, 20000m);
            b.Add(mon.AddHours(18), 19950m, 19990m, 19940m, 19945m);
            b.Add(mon.AddHours(19), 19960m, 20010m, 19950m, 19980m);
            b.Add(mon.AddHours(20), 19970m, 20020m, 19960m, 19965m);
            b.Add(mon.AddHours(21), 105m, 110m, 100m, 101m);
            RthDay(b, mon.AddDays(1), 20100m);
            b.Add(mon.AddDays(1).AddHours(18), 20050m, 20090m, 20040m, 20045m);
            b.Add(mon.AddDays(1).AddHours(19), 20060m, 20110m, 20050m, 20080m);

            var first = Run(b, s);
            var second = Run(b, s);

            Ok(first.Count > 0, "the idempotency fixture actually produces signals");
            Eq(second.Count, first.Count, "a second pass produces the same signal count");

            for (var i = 0; i < Math.Min(first.Count, second.Count); i++)
            {
                Eq(second[i].Key, first[i].Key, "signal " + i + " has the same dedupe key");
                Eq(second[i].Bar, first[i].Bar, "signal " + i + " lands on the same bar");
                Eq(second[i].StopReference, first[i].StopReference,
                   "signal " + i + " has the same stop reference");
            }

            // Every key is distinct: this is what makes per-night, per-variant alert dedupe work.
            var keys = first.Select(x => x.Key).ToList();
            Eq(keys.Distinct().Count(), keys.Count, "no duplicate signal keys in one pass");

            // Reset() must leave nothing behind -- PDH included.
            var eng = new AsiaWickEngine(s, LocalClock());
            var sink = new List<WickSignal>();
            for (var i = 0; i < b.Count; i++) eng.Fold(i, b.T[i], b.O[i], b.H[i], b.L[i], b.C[i], sink);

            var beforeReset = eng.Signals.Count;
            eng.Reset();
            Eq(eng.Signals.Count, 0, "Reset clears the signal list");
            Ok(!eng.Pdh.HasValue, "Reset clears the carried-over PDH");

            sink.Clear();
            for (var i = 0; i < b.Count; i++) eng.Fold(i, b.T[i], b.O[i], b.H[i], b.L[i], b.C[i], sink);
            Eq(eng.Signals.Count, beforeReset, "a reset engine reproduces the same signal set");
        }

        // ------------------------------------------------------------------ 4. risk rules

        private static void Tier1ForcedFlat()
        {
            var r = new RiskSettings();
            r.SignalOnly = false;
            r.Tier1Dates = "2026-09-15, 2026-10-13";

            var bad = new List<string>();
            var tier1 = AsiaWickUtil.ParseDates(r.Tier1Dates, bad);
            Eq(bad.Count, 0, "the tier-1 fixture parses cleanly");
            Eq(tier1.Count, 2, "two tier-1 dates parsed");

            var exit = new TimeSpan(8, 30, 0);
            var morning = new DateTime(2026, 9, 15);            // the listed release morning
            var tradeDate = morning.AddDays(-1);                // the night that runs into it

            Ok(NightGuard.NightRunsIntoTier1(tradeDate, tier1),
               "the night before a listed morning is a tier-1 night");

            // Before 07:28 the guard is quiet.
            Eq(NightGuard.FlattenReason(morning.AddHours(7).AddMinutes(27), exit, r, tier1, false),
               GuardReason.None, "07:27 on a tier-1 morning is not yet a forced flat");

            // At 07:28 it forces flat, two minutes ahead of the 07:30 release.
            Eq(NightGuard.FlattenReason(morning.AddHours(7).AddMinutes(28), exit, r, tier1, false),
               GuardReason.Tier1News, "07:28 forces flat on a tier-1 morning");
            Eq(NightGuard.FlattenReason(morning.AddHours(7).AddMinutes(45), exit, r, tier1, false),
               GuardReason.Tier1News, "still forced flat after the release");

            // And no re-entry that morning.
            Eq(NightGuard.EntryBlock(morning.AddHours(7).AddMinutes(30), tradeDate, exit, r,
                                     tier1, true, false, false),
               GuardReason.Tier1News, "no re-entry after the tier-1 flatten");

            // A morning that is NOT listed behaves normally.
            var clear = new DateTime(2026, 9, 16);
            Eq(NightGuard.FlattenReason(clear.AddHours(7).AddMinutes(28), exit, r, tier1, false),
               GuardReason.None, "07:28 on an unlisted morning is not a forced flat");

            // Switching the guard off must actually switch it off.
            r.Tier1Guard = false;
            Eq(NightGuard.FlattenReason(morning.AddHours(7).AddMinutes(28), exit, r, tier1, false),
               GuardReason.None, "the tier-1 guard can be disabled");
        }

        private static void ExitTimeDoesNotFireInTheEvening()
        {
            var r = new RiskSettings();
            var tier1 = new HashSet<DateTime>();
            var mon = new DateTime(2026, 9, 14);

            var nyOpen = new TimeSpan(8, 30, 0);

            // 18:00 is numerically past 08:30 but is the START of the session, not the exit.
            Eq(NightGuard.FlattenReason(mon.AddHours(18), nyOpen, r, tier1, false),
               GuardReason.None, "the Asia open is not read as the morning exit");
            Eq(NightGuard.FlattenReason(mon.AddHours(23), nyOpen, r, tier1, false),
               GuardReason.None, "late evening is not the morning exit");
            Eq(NightGuard.FlattenReason(mon.AddDays(1).AddHours(1), nyOpen, r, tier1, false),
               GuardReason.None, "01:00 is before the morning exit");
            Eq(NightGuard.FlattenReason(mon.AddDays(1).AddHours(8).AddMinutes(30), nyOpen, r, tier1, false),
               GuardReason.ExitTime, "08:30 is the morning exit");

            var london = new TimeSpan(2, 0, 0);
            Eq(NightGuard.FlattenReason(mon.AddHours(20), london, r, tier1, false),
               GuardReason.None, "London exit does not fire during the evening leg");
            Eq(NightGuard.FlattenReason(mon.AddDays(1).AddHours(2), london, r, tier1, false),
               GuardReason.ExitTime, "London exit fires at 02:00");

            var nyClose = new TimeSpan(15, 0, 0);
            Eq(NightGuard.FlattenReason(mon.AddHours(18), nyClose, r, tier1, false),
               GuardReason.None, "NY-close exit does not fire during the evening leg");
            Eq(NightGuard.FlattenReason(mon.AddDays(1).AddHours(15), nyClose, r, tier1, false),
               GuardReason.ExitTime, "NY-close exit fires at 15:00");
        }

        private static void FridayAndWeekend()
        {
            var r = new RiskSettings();
            r.SignalOnly = false;

            var tier1 = new HashSet<DateTime>();
            var exit = new TimeSpan(8, 30, 0);

            var fri = new DateTime(2026, 9, 18);
            var thu = new DateTime(2026, 9, 17);
            Eq(fri.DayOfWeek, DayOfWeek.Friday, "fixture Friday is actually a Friday");

            Eq(NightGuard.EntryBlock(fri.AddHours(19), fri, exit, r, tier1, true, false, false),
               GuardReason.FridayTradeDate, "no entry on a Friday trade date");

            // Thursday's night is fine -- it exits Friday morning, well before the close.
            Eq(NightGuard.EntryBlock(thu.AddHours(19), thu, exit, r, tier1, true, false, false),
               GuardReason.None, "Thursday night is tradable");

            // Nothing may be carried over the weekly close.
            Eq(NightGuard.FlattenReason(fri.AddHours(15).AddMinutes(55), exit, r, tier1, false),
               GuardReason.WeekendClose, "flat before the Friday weekly close");

            r.BlockFriday = false;
            Eq(NightGuard.EntryBlock(fri.AddHours(19), fri, exit, r, tier1, true, false, false),
               GuardReason.None, "the Friday block can be disabled");
        }

        private static void LossLimitAndDisable()
        {
            var r = new RiskSettings();
            r.SignalOnly = false;

            var tier1 = new HashSet<DateTime>();
            var exit = new TimeSpan(8, 30, 0);
            var mon = new DateTime(2026, 9, 14);

            Eq(NightGuard.FlattenReason(mon.AddHours(20), exit, r, tier1, true),
               GuardReason.LossLimit, "the loss cutoff forces flat");
            Eq(NightGuard.EntryBlock(mon.AddHours(20), mon, exit, r, tier1, true, true, false),
               GuardReason.LossLimit, "the loss cutoff blocks entry");

            Eq(NightGuard.EntryBlock(mon.AddHours(20), mon, exit, r, tier1, true, false, true),
               GuardReason.Disabled, "a rejected order stands the night down");
            Eq(NightGuard.EntryBlock(mon.AddHours(20), mon, exit, r, tier1, false, false, false),
               GuardReason.NightSpent, "a spent night takes no second entry");

            // Signal-only ships ON and must be the thing that stops the order.
            r.SignalOnly = true;
            Eq(NightGuard.EntryBlock(mon.AddHours(20), mon, exit, r, tier1, true, false, false),
               GuardReason.SignalOnly, "signal-only mode blocks the order");

            Ok(new RiskSettings().SignalOnly, "signal-only is ON by default");
            Eq(new RiskSettings().NightlyLossLimit, 500m, "nightly loss cutoff defaults to $500");
            Eq(new RiskSettings().StopTicks, 40, "stop defaults to 40 ticks");
        }

        private static void StopPriceAndDateParsing()
        {
            // MNQ tick is 0.25; 40 ticks is 10 points above the signal bar high.
            Eq(NightGuard.StopPrice(20010m, 40, 0.25m), 20020m, "40-tick stop above a 20010 high");
            Eq(NightGuard.StopPrice(20010m, 8, 0.25m), 20012m, "8-tick stop above a 20010 high");

            var bad = new List<string>();
            var set = AsiaWickUtil.ParseDates("2026-09-15,  2026-10-13 ,\n2026-11-05", bad);
            Eq(set.Count, 3, "whitespace and newlines are tolerated in the date list");
            Eq(bad.Count, 0, "clean list reports no bad entries");

            // A malformed date must be REPORTED, never silently dropped -- a missed news date is
            // a rule breach on a funded account.
            bad.Clear();
            var partial = AsiaWickUtil.ParseDates("2026-09-15, 15/09/2026, tomorrow", bad);
            Eq(partial.Count, 1, "only the well-formed date is accepted");
            Eq(bad.Count, 2, "both malformed entries are reported back");

            Eq(AsiaWickUtil.ParseDates("", bad).Count, 0, "an empty list is empty, not an error");
            Eq(AsiaWickUtil.ParseDates(null, bad).Count, 0, "a null list is empty, not a crash");
        }
    }
}
