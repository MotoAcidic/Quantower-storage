using System;
using System.Collections.Generic;
using System.Linq;

namespace OceansOrbBreakout.Tests
{
    /// <summary>
    /// Exercises the model on synthetic MNQ-shaped bars. The generator works in Houston wall
    /// time and knows the truth; the builder only ever sees UTC stamps and has to recover it.
    ///
    /// The point of this harness is the failure mode that costs money: a signal that looks
    /// perfectly reasonable on the chart but was measured against the wrong range, the wrong
    /// average, or a VWAP that was never really there. So every condition is checked in both
    /// directions -- it fires when it should, and it stays silent when any one leg is missing.
    ///
    /// Every time in this file is Houston time, same as the indicator.
    /// </summary>
    internal static class Program
    {
        private static readonly TimeZoneInfo Houston =
            TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

        private static int _failed;

        private static TimeSpan T(int h, int m) => new TimeSpan(h, m, 0);

        // Published CME hours for MNQ: Sunday 5 PM Houston open, Friday 4 PM close, and a
        // maintenance halt 4-5 PM every weekday. The generator encodes THESE, not the
        // indicator's settings, so a wrong setting cannot agree with itself into a pass.
        private static readonly TimeSpan HaltStart = T(16, 0);
        private static readonly TimeSpan HaltEnd = T(17, 0);
        private static readonly TimeSpan FridayClose = T(16, 0);
        private static readonly TimeSpan SundayOpen = T(17, 0);

        // The fixture day: Tuesday evening reopen through Wednesday's cash close.
        private static readonly DateTime EveningOpen = new DateTime(2026, 8, 11, 17, 0, 0);
        private static readonly DateTime DayClose = new DateTime(2026, 8, 12, 15, 0, 0);

        private const decimal Base = 20000m;
        private const decimal RangeHigh = 20010m;
        private const decimal RangeLow = 19990m;
        private const decimal BaseVolume = 100m;

        private static readonly TimeSpan OrTop = T(8, 30);      // exists on 1, 5 and 15 minute
        private static readonly TimeSpan OrBottom = T(8, 45);   // charts alike
        private static readonly TimeSpan BreakBar = T(9, 15);
        private static readonly TimeSpan LateBar = T(10, 0);    // still inside the window
        private static readonly TimeSpan PastCutoff = T(11, 0); // outside it

        private static int Main()
        {
            TradingDayTests();
            OpeningRangeTests();
            BreakoutTests();
            VolumeTests();
            VwapTests();
            WindowTests();
            RepaintTests();
            DegradedFeedTests();
            AlignmentTests();
            DstTests();
            ClockTests();

            Console.WriteLine();
            Console.WriteLine(_failed == 0 ? "all tests passed" : _failed + " TEST(S) FAILED");
            return _failed == 0 ? 0 : 1;
        }

        #region Tests

        private static void TradingDayTests()
        {
            Section("trading day rollover");

            var roll = T(17, 0);

            Check("a morning bar belongs to its own date",
                OrbModel.TradingDay(new DateTime(2026, 8, 12, 8, 30, 0), roll)
                    == new DateTime(2026, 8, 12));

            Check("an afternoon bar belongs to its own date",
                OrbModel.TradingDay(new DateTime(2026, 8, 12, 14, 59, 0), roll)
                    == new DateTime(2026, 8, 12));

            Check("the 5 PM reopen belongs to the NEXT day",
                OrbModel.TradingDay(new DateTime(2026, 8, 11, 17, 0, 0), roll)
                    == new DateTime(2026, 8, 12));

            Check("so does the bar before midnight",
                OrbModel.TradingDay(new DateTime(2026, 8, 11, 23, 55, 0), roll)
                    == new DateTime(2026, 8, 12));

            Check("and the small hours are still that day",
                OrbModel.TradingDay(new DateTime(2026, 8, 12, 3, 0, 0), roll)
                    == new DateTime(2026, 8, 12));
        }

        private static void OpeningRangeTests()
        {
            Section("opening range, across chart timeframes");

            foreach (var step in new[] { 1, 5, 15 })
            {
                var day = Only(Build(Standard(step)));
                var label = step + " minute chart";

                Check(label + ": range high is the high of the first 30 minutes",
                    day.OrHigh == RangeHigh);

                Check(label + ": range low is the low of the first 30 minutes",
                    day.OrLow == RangeLow);

                Check(label + ": range holds 30 minutes of bars",
                    day.OrBars == 30 / step);

                Check(label + ": average bar volume is the range volume over its bars",
                    day.OrAverageVolume == BaseVolume);

                Check(label + ": the range opens at 8:30",
                    day.OrStartLocal.TimeOfDay == T(8, 30));

                Check(label + ": the range closes at 9:00",
                    day.OrEndLocal.TimeOfDay == T(9, 0));

                Check(label + ": bars divide the range",
                    day.Aligned);

                Check(label + ": the range is settled",
                    day.RangeComplete);
            }

            Section("a 30 minute chart makes the range a single bar");

            var coarse = Only(Build(Standard(30)));
            Check("the range is one bar", coarse.OrBars == 1);
            Check("and says so, rather than passing it off as an average", coarse.ThinRange);
        }

        private static void BreakoutTests()
        {
            Section("condition 1 -- close above the range high");

            foreach (var step in new[] { 1, 5, 15 })
            {
                var day = Only(Build(Standard(step)));
                Check(step + " minute chart: exactly one signal", day.Signals.Count == 1);

                if (day.Signals.Count != 1) continue;
                Check(step + " minute chart: it is the 9:15 bar",
                    day.Signals[0].Local.TimeOfDay == BreakBar);
            }

            var wick = Build(Shape(Fixture(5), b =>
            {
                // Pierces the high by 40 points and closes back inside it.
                At(b, BreakBar, x => { x.High = 20050m; x.Close = Base; x.Volume = 400m; });
            }));

            Check("a wick through the high with a close back inside does not signal",
                Only(wick).Signals.Count == 0);

            var equal = Build(Shape(Fixture(5), b =>
                At(b, BreakBar, x => { x.Close = RangeHigh; x.High = RangeHigh; x.Volume = 400m; })));

            Check("a close exactly ON the high is not above it",
                Only(equal).Signals.Count == 0);

            var justOver = Build(Shape(Fixture(5), b =>
                At(b, BreakBar, x => { x.Close = RangeHigh + 0.25m; x.High = RangeHigh + 0.5m; x.Volume = 400m; })));

            Check("one tick above it is",
                Only(justOver).Signals.Count == 1);

            var inside = Build(Shape(Fixture(5), b =>
            {
                // A bar INSIDE the range that would pass every other test. Nothing may fire
                // before the range is settled -- it is still being drawn.
                At(b, OrBottom, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 400m; });
            }));

            Check("nothing signals while the range is still forming",
                Only(inside).Signals.All(s => s.Local.TimeOfDay >= T(9, 0)));
        }

        private static void VolumeTests()
        {
            Section("condition 2 -- volume against the range average");

            // The range averages 100, so the 1.5 multiple asks for 150.
            var under = Build(Shape(Fixture(5), b =>
                At(b, BreakBar, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 149m; })));

            Check("1.49x does not clear the 1.5 bar", Only(under).Signals.Count == 0);

            var exact = Build(Shape(Fixture(5), b =>
                At(b, BreakBar, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 150m; })));

            Check("1.5x exactly does", Only(exact).Signals.Count == 1);

            if (Only(exact).Signals.Count == 1)
                Check("and the recorded multiple is 1.5",
                    Only(exact).Signals[0].VolumeRatio == 1.5m);

            var raised = Build(Standard(5), cfg => cfg.VolumeMultiple = 2.5m);
            Check("raising the multiple to 2.5 silences the 2x bar",
                Only(raised).Signals.Count == 0);

            var lowered = Build(Standard(5), cfg => cfg.VolumeMultiple = 1.0m);
            Check("dropping it to 1.0 does not", Only(lowered).Signals.Count == 1);
        }

        private static void VwapTests()
        {
            Section("condition 3 -- close above the session VWAP");

            var day = Only(Build(Standard(5)));

            Check("the VWAP series starts at the 8:30 open",
                day.VwapStartBar >= 0 &&
                day.Vwap.Count > 0);

            Check("the VWAP sits inside the range it was built from",
                day.VwapAt(day.OrEndBar) > RangeLow && day.VwapAt(day.OrEndBar) < RangeHigh);

            Check("the signal records the VWAP it cleared",
                day.Signals.Count == 1 && day.Signals[0].Vwap > 0m &&
                day.Signals[0].Close > day.Signals[0].Vwap);

            // A gap-down open: the evening traded 600 points higher on heavy volume, so the
            // overnight VWAP is far above the morning breakout while the session VWAP is not.
            // Same bars, same break -- only the anchor differs.
            var gap = Shape(Fixture(5), b =>
            {
                foreach (var bar in b)
                {
                    if (bar.Local >= EveningOpen && bar.Local.TimeOfDay >= T(17, 0))
                    {
                        bar.Open = bar.High = bar.Low = bar.Close = 20600m;
                        bar.Volume = 5000m;
                    }
                }
            });

            var session = Only(Build(gap, cfg => cfg.Anchor = VwapAnchor.SessionOpen));
            var overnight = Only(Build(gap, cfg => cfg.Anchor = VwapAnchor.OvernightOpen));

            Check("anchored at the session open, the evening does not move the VWAP",
                session.VwapAt(session.Signals.Count > 0 ? session.Signals[0].Bar : session.OrEndBar) < RangeHigh);

            Check("so the breakout still signals", session.Signals.Count == 1);

            Check("anchored overnight, the VWAP is up where the evening traded",
                overnight.VwapAt(overnight.OrEndBar) > RangeHigh);

            Check("so the same breakout is below VWAP and does not signal",
                overnight.Signals.Count == 0);

            Check("and switching the VWAP test off lets it through again",
                Only(Build(gap, cfg =>
                {
                    cfg.Anchor = VwapAnchor.OvernightOpen;
                    cfg.RequireAboveVwap = false;
                })).Signals.Count == 1);
        }

        private static void WindowTests()
        {
            Section("condition 4 -- inside the first two hours");

            var late = Build(Shape(Fixture(5), b =>
            {
                Quiet(b, BreakBar);
                At(b, PastCutoff, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 400m; });
            }));

            Check("a qualifying bar at 11:00 is past the 10:30 cutoff and does not signal",
                Only(late).Signals.Count == 0);

            var inWindow = Build(Shape(Fixture(5), b =>
            {
                Quiet(b, BreakBar);
                At(b, LateBar, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 400m; });
            }));

            Check("the same bar at 10:00 does",
                Only(inWindow).Signals.Count == 1);

            var stretched = Build(
                Shape(Fixture(5), b =>
                {
                    Quiet(b, BreakBar);
                    At(b, PastCutoff, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 400m; });
                }),
                cfg => cfg.Cutoff = T(12, 0));

            Check("moving the cutoff to noon brings the 11:00 bar back",
                Only(stretched).Signals.Count == 1);

            Section("first signal only");

            var twice = Shape(Fixture(5), b =>
                At(b, LateBar, x => { x.Close = 20030m; x.High = 20031m; x.Volume = 400m; }));

            Check("first-only keeps the 9:15 break and drops the 10:00 one",
                Only(Build(twice)).Signals.Count == 1 &&
                Only(Build(twice)).Signals[0].Local.TimeOfDay == BreakBar);

            var all = Only(Build(twice, cfg => cfg.FirstSignalOnly = false));

            Check("switching it off marks both",
                all.Signals.Count == 2 &&
                all.Signals[0].Local.TimeOfDay == BreakBar &&
                all.Signals[1].Local.TimeOfDay == LateBar);
        }

        private static void RepaintTests()
        {
            Section("the forming bar");

            // History that STOPS on the breakout bar, so it is the live, half-built one.
            var truncated = Shape(Fixture(5), null);
            var cut = truncated.FindIndex(b => b.Local.TimeOfDay == BreakBar);
            truncated = truncated.Take(cut + 1).ToList();

            Check("confirming on close ignores the bar still forming",
                Only(Build(truncated)).Signals.Count == 0);

            Check("switching that off signals on it intrabar",
                Only(Build(truncated, cfg => cfg.ConfirmOnClose = false)).Signals.Count == 1);

            Check("and once the next bar has printed, the closed bar signals either way",
                Only(Build(Standard(5))).Signals.Count == 1);
        }

        private static void DegradedFeedTests()
        {
            Section("a feed with no volume must not signal");

            var noVolume = Shape(Fixture(5), b =>
            {
                foreach (var bar in b) bar.Volume = 0m;
                At(b, BreakBar, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 0m; });
            });

            var day = Only(Build(noVolume));

            Check("the range average is zero, not something invented",
                day.OrAverageVolume == 0m);

            Check("there is no VWAP rather than a typical-price stand-in",
                !day.HasVwap);

            Check("and nothing signals -- 1.5 times nothing is not a pass",
                day.Signals.Count == 0);

            Section("a day whose opening range never printed");

            // History that begins after the cash open, so there is no range to break.
            var partial = Shape(Fixture(5), null)
                .Where(b => b.Local >= new DateTime(2026, 8, 12, 9, 30, 0))
                .ToList();

            var built = Build(partial);
            var latest = built.Days[built.Days.Count - 1];

            Check("no range is recorded", !latest.HasRange);
            Check("the range is not marked settled", !latest.RangeComplete);
            Check("and nothing is compared against a high of zero", latest.Signals.Count == 0);
        }

        private static void AlignmentTests()
        {
            Section("bars that do not divide the range");

            // A 7 minute chart from 8:00 steps straight over 8:30 and 9:00.
            var odd = Fixture(7);
            var day = Only(Build(Shape(odd, null)));

            Check("no bar opens on the range boundary, and it says so", !day.Aligned);
            Check("the range still records the bars it did find", day.OrBars > 0);

            Check("a 5 minute chart is aligned for comparison", Only(Build(Standard(5))).Aligned);
        }

        private static void DstTests()
        {
            Section("daylight saving");

            // Central and Eastern shift on the same dates, so the cash open stays 8:30 Houston
            // all year. The check is that the WINTER range lands on 8:30 too, not 7:30.
            var winter = BuildWindow(
                Generate(new DateTime(2026, 1, 13, 17, 0, 0), new DateTime(2026, 1, 14, 15, 0, 0), 5,
                         b => Standardise(b)),
                out var ctx);

            var model = OrbModel.Build(winter, Config(ctx, null));
            var day = model.Days.Last(d => d.HasRange);

            Check("the January range opens at 8:30 Houston", day.OrStartLocal.TimeOfDay == T(8, 30));
            Check("the January range high is right", day.OrHigh == RangeHigh);
            Check("and the January breakout still fires", day.Signals.Count == 1);

            Check("the August range opens at 8:30 Houston too",
                Only(Build(Standard(5))).OrStartLocal.TimeOfDay == T(8, 30));
        }

        private static void ClockTests()
        {
            Section("resolving the bar clock from the data");

            // Sunday reopen through Thursday's close: enough days for the daily halt to show.
            var bars = Generate(new DateTime(2026, 8, 9, 17, 0, 0), new DateTime(2026, 8, 13, 15, 0, 0), 15,
                                b => Standardise(b));

            var window = new Window(bars);

            // default nowUtc, so the wall-clock signal abstains and the halt has to decide.
            var auto = TimeContext.Create("Central Standard Time", BarClock.Auto, window,
                                          default, 16);

            Check("auto resolves", auto.Valid);
            Check("it reads the stamps as UTC", auto.Clock == BarClock.Utc);
            Check("by finding the daily halt", auto.Method == ResolveMethod.DailyHalt);

            var model = OrbModel.Build(window, Config(auto, null));
            var days = model.Days.Where(d => d.HasRange).ToList();

            Check("every modelled day opens its range at 8:30 Houston",
                days.Count > 0 && days.All(d => d.OrStartLocal.TimeOfDay == T(8, 30)));

            Check("and finds the same range high each day",
                days.All(d => d.OrHigh == RangeHigh));

            var bad = TimeContext.Create("No Such Zone", BarClock.Auto, window, default, 16);
            Check("a bad time zone reports rather than guesses", !bad.Valid && bad.Error != null);

            Check("an unresolved clock builds nothing at all",
                OrbModel.Build(window, Config(bad, null)).Days.Count == 0);

            Section("how many days are modelled");

            Check("two days asked for, two days built",
                OrbModel.Build(window, Config(auto, cfg => cfg.Days = 2)).Days.Count == 2);

            Check("one day asked for, one day built",
                OrbModel.Build(window, Config(auto, cfg => cfg.Days = 1)).Days.Count == 1);
        }

        #endregion

        #region Fixture

        private sealed class Bar
        {
            public DateTime Local;
            public decimal Open, High, Low, Close, Volume;
        }

        /// <summary>Feeds the model UTC stamps, the way ATAS does.</summary>
        private sealed class Window : IBarWindow
        {
            private readonly List<Bar> _bars;
            public Window(List<Bar> bars) { _bars = bars; }

            public int Count => _bars.Count;

            public DateTime Time(int bar) => TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(_bars[bar].Local, DateTimeKind.Unspecified), Houston);

            public decimal Open(int bar) => _bars[bar].Open;
            public decimal High(int bar) => _bars[bar].High;
            public decimal Low(int bar) => _bars[bar].Low;
            public decimal Close(int bar) => _bars[bar].Close;
            public decimal Volume(int bar) => _bars[bar].Volume;
        }

        /// <summary>The fixture day at a given bar size, with the standard shape applied.</summary>
        private static List<Bar> Standard(int stepMinutes) => Shape(Fixture(stepMinutes), null);

        private static List<Bar> Fixture(int stepMinutes) =>
            Generate(EveningOpen, DayClose, stepMinutes, null);

        /// <summary>
        /// The shape every test starts from: a flat market, a 20 point opening range, and one
        /// clean 2x-volume break above it at 9:15. Times chosen to exist on 1, 5 and 15 minute
        /// charts alike, so the same fixture drives all three.
        /// </summary>
        private static List<Bar> Shape(List<Bar> bars, Action<List<Bar>> extra)
        {
            Standardise(bars);
            extra?.Invoke(bars);
            return bars;
        }

        private static void Standardise(List<Bar> bars)
        {
            At(bars, OrTop, x => x.High = RangeHigh);
            At(bars, OrBottom, x => x.Low = RangeLow);
            At(bars, BreakBar, x => { x.Close = 20015m; x.High = 20016m; x.Volume = 200m; });
        }

        /// <summary>Puts a bar back to the flat baseline.</summary>
        private static void Quiet(List<Bar> bars, TimeSpan tod) =>
            At(bars, tod, x =>
            {
                x.Open = Base;
                x.High = Base + 1m;
                x.Low = Base - 1m;
                x.Close = Base;
                x.Volume = BaseVolume;
            });

        private static void At(List<Bar> bars, TimeSpan tod, Action<Bar> edit)
        {
            foreach (var b in bars)
                if (b.Local.TimeOfDay == tod) edit(b);
        }

        private static List<Bar> Generate(DateTime start, DateTime end, int stepMinutes,
                                          Action<List<Bar>> shape)
        {
            var bars = new List<Bar>();

            for (var t = start; t < end; t = t.AddMinutes(stepMinutes))
            {
                if (!IsOpen(t)) continue;

                bars.Add(new Bar
                {
                    Local = t,
                    Open = Base,
                    High = Base + 1m,
                    Low = Base - 1m,
                    Close = Base,
                    Volume = BaseVolume
                });
            }

            shape?.Invoke(bars);
            return bars;
        }

        private static bool IsOpen(DateTime local)
        {
            var tod = local.TimeOfDay;
            if (tod >= HaltStart && tod < HaltEnd) return false;

            switch (local.DayOfWeek)
            {
                case DayOfWeek.Saturday: return false;
                case DayOfWeek.Sunday: return tod >= SundayOpen;
                case DayOfWeek.Friday: return tod < FridayClose;
                default: return true;
            }
        }

        private static IBarWindow BuildWindow(List<Bar> bars, out TimeContext ctx)
        {
            var window = new Window(bars);
            ctx = TimeContext.Create("Central Standard Time", BarClock.Utc, window, default, 16);
            return window;
        }

        private static OrbConfig Config(TimeContext ctx, Action<OrbConfig> tweak)
        {
            var cfg = new OrbConfig { Time = ctx, Days = 5 };
            tweak?.Invoke(cfg);
            return cfg;
        }

        private static OrbModel Build(List<Bar> bars, Action<OrbConfig> tweak = null)
        {
            var window = BuildWindow(bars, out var ctx);
            return OrbModel.Build(window, Config(ctx, tweak));
        }

        /// <summary>The one trading day the single-day fixture produces.</summary>
        private static DayOrb Only(OrbModel model) =>
            model.Days.LastOrDefault(d => d.HasRange) ?? new DayOrb();

        private static void Section(string name) => Console.WriteLine("-- " + name);

        private static void Check(string what, bool ok)
        {
            Console.WriteLine("   " + (ok ? "ok  " : "FAIL") + "  " + what);
            if (!ok) _failed++;
        }

        #endregion
    }
}
