using System;
using System.Collections.Generic;
using System.Linq;

namespace OceansMarketView.Tests
{
    /// <summary>
    /// Exercises the model on synthetic MNQ-shaped bars. The generator works in Houston wall
    /// time and knows the truth; the builder only ever sees UTC stamps and has to recover it.
    /// So this checks the clock resolution, the session windows, the opening ranges and the
    /// level arithmetic end to end -- the places where a bug would draw a clean, plausible,
    /// silently wrong line.
    ///
    /// Every time in this file is Houston time, same as the indicator.
    /// </summary>
    internal static class Program
    {
        private static readonly TimeZoneInfo Houston = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        private static int _failed;

        private static readonly TimeSpan WeekOpen = T(17, 0);
        private static readonly TimeSpan RthOpen = T(8, 30);
        private static readonly TimeSpan RthClose = T(15, 0);
        private static readonly TimeSpan IbClose = T(9, 30);
        private static readonly TimeSpan PowerOpen = T(14, 0);
        private static readonly TimeSpan AsiaOpen = T(18, 0);
        private static readonly TimeSpan AsiaClose = T(3, 0);
        private static readonly TimeSpan LondonOpen = T(2, 0);
        private static readonly TimeSpan LondonClose = T(10, 30);

        private static TimeSpan T(int h, int m) => new TimeSpan(h, m, 0);

        // Published CME hours for MNQ: Sunday 5:00 PM CT open, Friday 4:00 PM CT close, and a
        // maintenance halt 4:00-5:00 PM CT every weekday. The generator encodes THESE, not the
        // indicator's settings. When the two were allowed to agree, a wrong halt hour in the
        // indicator sailed through the whole suite unnoticed.
        private const int HaltHour = 16;
        private static readonly TimeSpan HaltStart = T(16, 0);
        private static readonly TimeSpan HaltEnd = T(17, 0);
        private static readonly TimeSpan FridayClose = T(16, 0);
        private static readonly TimeSpan SundayOpen = T(17, 0);

        private static int Main()
        {
            // Sunday 2026-08-09 5 PM Houston reopen through Thursday 2026-08-13 3 PM.
            var bars = Generate(new DateTime(2026, 8, 9, 17, 0, 0), new DateTime(2026, 8, 13, 15, 0, 0), 15);
            Console.WriteLine($"generated {bars.Count} bars  {bars[0].Local:ddd yyyy-MM-dd h:mm tt} .. " +
                              $"{bars[^1].Local:ddd yyyy-MM-dd h:mm tt} Houston\n");

            CalendarTests(bars);
            ClockTests(bars);
            var model = BuildUtc(bars, out var time);

            WeeklyOpenTests(bars, model);
            SessionTests(bars, model);
            OpeningRangeTests(bars, model);
            InitialBalanceTests(bars, model);
            PowerHourTests(bars, model);
            ClockLabelTests(time);
            DstTests();

            Console.WriteLine(_failed == 0 ? "\nALL PASS" : $"\n{_failed} FAILURE(S)");
            return _failed == 0 ? 0 : 1;
        }

        #region Tests

        /// <summary>
        /// Pins the generated tape to CME's published calendar. If this drifts, every other
        /// test in the file is measuring against the wrong market.
        /// </summary>
        private static void CalendarTests(List<Bar> bars)
        {
            Section("cme calendar");

            Check("nothing trades during the 4-5 PM halt",
                  !bars.Any(b => b.Local.TimeOfDay >= HaltStart && b.Local.TimeOfDay < HaltEnd));
            Check("the 3-4 PM hour DOES trade (it is the cash close, not the halt)",
                  bars.Any(b => b.Local.TimeOfDay >= T(15, 0) && b.Local.TimeOfDay < HaltStart));
            Check("the 5-6 PM hour does trade",
                  bars.Any(b => b.Local.TimeOfDay >= HaltEnd && b.Local.TimeOfDay < T(18, 0)));
            Check("nothing trades on Saturday",
                  !bars.Any(b => b.Local.DayOfWeek == DayOfWeek.Saturday));
            Check("Sunday opens at 5 PM",
                  bars.Where(b => b.Local.DayOfWeek == DayOfWeek.Sunday)
                      .All(b => b.Local.TimeOfDay >= SundayOpen));

            var byHour = Enumerable.Range(0, 24)
                .Select(h => bars.Count(b => b.Local.Hour == h)).ToList();
            Check($"the emptiest hour of the day is {HaltHour}:00",
                  byHour.IndexOf(byHour.Min()) == HaltHour);
        }

        private static void ClockTests(List<Bar> bars)
        {
            Section("bar clock");

            var stale = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var live = TimeZoneInfo.ConvertTimeToUtc(bars[^1].Local, Houston).AddMinutes(5);

            var utcStale = TimeContext.Create("Central Standard Time", BarClock.Auto, new Feed(bars, true), stale, HaltHour);
            Check("utc stamps, stale clock -> Utc", utcStale.Valid && utcStale.Clock == BarClock.Utc);
            Check("  ...found via the daily halt", utcStale.Method == ResolveMethod.DailyHalt);

            var localStale = TimeContext.Create("Central Standard Time", BarClock.Auto, new Feed(bars, false), stale, HaltHour);
            Check("local stamps, stale clock -> AlreadyLocal",
                  localStale.Valid && localStale.Clock == BarClock.AlreadyLocal);

            var utcLive = TimeContext.Create("Central Standard Time", BarClock.Auto, new Feed(bars, true), live, HaltHour);
            Check("utc stamps, live clock -> Utc", utcLive.Valid && utcLive.Clock == BarClock.Utc);
            Check("  ...found via the wall clock", utcLive.Method == ResolveMethod.WallClock);

            var badTz = TimeContext.Create("Nowhere/Nothing", BarClock.Auto, new Feed(bars, true), stale, HaltHour);
            Check("bad time zone is an error, not a fallback", !badTz.Valid);

            var noBars = TimeContext.Create("Central Standard Time", BarClock.Auto, new Feed(new List<Bar>(), true), stale, HaltHour);
            Check("no bars is an error, not a guess", !noBars.Valid);

            // The halt hour I originally shipped was 15 -- the cash close, not the halt.
            // A wrong hour must refuse to decide, never pick the wrong reading.
            var wrongHour = TimeContext.Create("Central Standard Time", BarClock.Auto, new Feed(bars, true), stale, 15);
            Check("a wrong halt hour refuses to detect rather than guessing", !wrongHour.Valid);

            var oneDay = bars.Where(b => b.Local.Date == new DateTime(2026, 8, 11)).ToList();
            var thin = TimeContext.Create("Central Standard Time", BarClock.Auto, new Feed(oneDay, true), stale, HaltHour);
            Check("one day of history refuses to auto-detect", !thin.Valid);
        }

        private static void WeeklyOpenTests(List<Bar> bars, MarketModel model)
        {
            Section("weekly open");

            Check("one trading week in range", model.Weeks.Count == 1);

            var w = model.Weeks[0];
            var expected = bars.First(b => b.Local >= new DateTime(2026, 8, 9, 17, 0, 0));

            Check("week anchored to Sunday 5:00 PM Houston",
                  w.WeekStartLocal == new DateTime(2026, 8, 9, 17, 0, 0));
            Check($"open is the first bar's open ({w.Price} == {expected.Open})", w.Price == expected.Open);
            Check("week start bar is bar 0", w.StartBar == 0);
            Check("week is current", w.Current);

            // Sunday 4 PM is before the 5 PM reopen, so it belongs to the PREVIOUS week.
            var before = MarketModel.WeekAnchor(new DateTime(2026, 8, 9, 16, 0, 0), DayOfWeek.Sunday, WeekOpen);
            Check("Sunday 4 PM rolls back a week", before == new DateTime(2026, 8, 2, 17, 0, 0));

            var friday = MarketModel.WeekAnchor(new DateTime(2026, 8, 14, 14, 0, 0), DayOfWeek.Sunday, WeekOpen);
            Check("Friday sits in the Sunday-anchored week", friday == new DateTime(2026, 8, 9, 17, 0, 0));
        }

        private static void SessionTests(List<Bar> bars, MarketModel model)
        {
            Section("market sessions");

            var tuesday = new DateTime(2026, 8, 11);

            var ny = model.Sessions.Single(s => s.Name == "New York" && s.AnchorDate == tuesday);
            var nyRef = Between(bars, tuesday, RthOpen, RthClose);
            Check("New York is 8:30 AM - 3:00 PM Houston",
                  ny.StartLocal == tuesday + RthOpen && ny.EndLocal == tuesday + RthClose);
            Check($"NY high {ny.High} == {nyRef.Max(b => b.High)}", ny.High == nyRef.Max(b => b.High));
            Check($"NY low {ny.Low} == {nyRef.Min(b => b.Low)}", ny.Low == nyRef.Min(b => b.Low));
            Check("NY session complete", ny.Complete);

            var london = model.Sessions.Single(s => s.Name == "London" && s.AnchorDate == tuesday);
            var lonRef = Between(bars, tuesday, LondonOpen, LondonClose);
            Check("London is 2:00 AM - 10:30 AM Houston",
                  london.StartLocal == tuesday + LondonOpen && london.EndLocal == tuesday + LondonClose);
            Check($"London high {london.High} == {lonRef.Max(b => b.High)}", london.High == lonRef.Max(b => b.High));

            // The overnight leg is the one that can silently land on the wrong day.
            var asia = model.Sessions.Single(s => s.Name == "Asia" && s.AnchorDate == tuesday);
            var asiaRef = bars.Where(b => (b.Local.Date == tuesday.AddDays(-1) && b.Local.TimeOfDay >= AsiaOpen) ||
                                          (b.Local.Date == tuesday && b.Local.TimeOfDay < AsiaClose)).ToList();
            Check("Asia runs Mon 6:00 PM -> Tue 3:00 AM Houston",
                  asia.StartLocal == tuesday.AddDays(-1) + AsiaOpen && asia.EndLocal == tuesday + AsiaClose);
            Check("Asia is labelled with the day it ENDS on", asia.AnchorDate.DayOfWeek == DayOfWeek.Tuesday);
            Check($"Asia high {asia.High} == {asiaRef.Max(b => b.High)}", asia.High == asiaRef.Max(b => b.High));
            Check($"Asia low {asia.Low} == {asiaRef.Min(b => b.Low)}", asia.Low == asiaRef.Min(b => b.Low));

            var monday = model.Sessions.Single(s => s.Name == "Asia" && s.AnchorDate == new DateTime(2026, 8, 10));
            Check("Sunday evening opens Monday's Asia session",
                  monday.StartLocal == new DateTime(2026, 8, 9, 18, 0, 0));

            var lastNy = model.Sessions.Where(s => s.Name == "New York").OrderBy(s => s.AnchorDate).Last();
            Check("a session still running is not marked complete",
                  lastNy.AnchorDate == new DateTime(2026, 8, 13) && !lastNy.Complete);
        }

        private static void OpeningRangeTests(List<Bar> bars, MarketModel model)
        {
            Section("session opening ranges (15 min)");

            var tuesday = new DateTime(2026, 8, 11);

            foreach (var (name, open) in new[] { ("New York", RthOpen), ("London", LondonOpen) })
            {
                var box = model.Sessions.Single(s => s.Name == name && s.AnchorDate == tuesday);
                var orbRef = Between(bars, tuesday, open, open + TimeSpan.FromMinutes(15));

                Check($"{name} opening range is one 15m bar", orbRef.Count == 1);
                Check($"{name} range window ends 15 min after the open",
                      box.OpenRangeEndLocal == box.StartLocal.AddMinutes(15));
                Check($"{name} OR high {box.OpenRangeHigh} == {orbRef.Max(b => b.High)}",
                      box.OpenRangeHigh == orbRef.Max(b => b.High));
                Check($"{name} OR low {box.OpenRangeLow} == {orbRef.Min(b => b.Low)}",
                      box.OpenRangeLow == orbRef.Min(b => b.Low));
                Check($"{name} OR mid is halfway between them",
                      box.OpenRangeMid == (box.OpenRangeHigh + box.OpenRangeLow) / 2m);
                Check($"{name} OR sits inside the session range",
                      box.OpenRangeHigh <= box.High && box.OpenRangeLow >= box.Low);
                Check($"{name} OR complete", box.OpenRangeComplete);
            }

            // The overnight session's opening range starts the evening BEFORE its anchor date.
            var asia = model.Sessions.Single(s => s.Name == "Asia" && s.AnchorDate == tuesday);
            var asiaRef = Between(bars, tuesday.AddDays(-1), AsiaOpen, AsiaOpen + TimeSpan.FromMinutes(15));
            Check("Asia OR is taken from Monday evening, not Tuesday morning",
                  asia.OpenRangeHigh == asiaRef.Max(b => b.High) && asia.OpenRangeLow == asiaRef.Min(b => b.Low));
            Check("Asia OR window ends at 6:15 PM Monday",
                  asia.OpenRangeEndLocal == tuesday.AddDays(-1) + AsiaOpen + TimeSpan.FromMinutes(15));

            // A 30 min range must cover the 15 min one, never less.
            var wide = MarketModel.Build(new Feed(bars, true), Config(true, 30));
            var wideNy = wide.Sessions.Single(s => s.Name == "New York" && s.AnchorDate == tuesday);
            var narrowNy = model.Sessions.Single(s => s.Name == "New York" && s.AnchorDate == tuesday);
            Check("a 30 min opening range contains the 15 min one",
                  wideNy.OpenRangeHigh >= narrowNy.OpenRangeHigh && wideNy.OpenRangeLow <= narrowNy.OpenRangeLow);

            var off = MarketModel.Build(new Feed(bars, true), Config(true, 0));
            Check("zero minutes switches the opening range off",
                  off.Sessions.All(s => !s.HasOpenRange));

            var live = model.Sessions.Where(s => s.Name == "New York").OrderBy(s => s.AnchorDate).Last();
            Check("Thursday's NY opening range is complete even though the session is not",
                  live.OpenRangeComplete && !live.Complete);
        }

        private static void InitialBalanceTests(List<Bar> bars, MarketModel model)
        {
            Section("initial balance");

            var tuesday = new DateTime(2026, 8, 11);
            var d = model.Days.Single(x => x.Date == tuesday);
            var ibRef = Between(bars, tuesday, RthOpen, IbClose);

            Check("IB covers four 15m bars", ibRef.Count == 4);
            Check($"IB high {d.IbHigh} == {ibRef.Max(b => b.High)}", d.IbHigh == ibRef.Max(b => b.High));
            Check($"IB low {d.IbLow} == {ibRef.Min(b => b.Low)}", d.IbLow == ibRef.Min(b => b.Low));
            Check("IB complete", d.IbComplete);
            Check("IB runs 8:30 - 9:30 AM Houston",
                  bars[d.IbStartBar].Local.TimeOfDay == RthOpen &&
                  bars[d.IbEndBar].Local.TimeOfDay == IbClose - TimeSpan.FromMinutes(15));

            Check($"IB mid {d.IbMid} is halfway between high and low",
                  d.IbMid == (d.IbHigh + d.IbLow) / 2m);
            Check("IB mid sits strictly inside the balance",
                  d.IbMid < d.IbHigh && d.IbMid > d.IbLow);
            Check("IB mid is half the range off each edge",
                  d.IbHigh - d.IbMid == d.IbRange / 2m && d.IbMid - d.IbLow == d.IbRange / 2m);

            // The middle line is measured inward; the 0.5x extensions are measured outward.
            // Confusing the two would put the mid on top of an extension.
            var half = MarketModel.IbLevels(d, new[] { 0.5m });
            Check("IB mid is not the same level as either 0.5x extension",
                  half.All(l => l.Price != d.IbMid));

            var lastRth = Between(bars, tuesday, RthOpen, RthClose).Last();
            Check("session end bar is the 2:45 PM bar", bars[d.RthEndBar].Local == lastRth.Local);

            var all = new[] { 0m, 0.5m, 1m, 1.5m, 2m, 2.5m, 3m, 3.5m };
            var levels = MarketModel.IbLevels(d, all);
            Check("full ladder is 8 steps x 2 sides", levels.Count == 16);

            var r = d.IbRange;
            var up = levels.Where(l => l.Above).OrderBy(l => l.Multiple).ToList();
            var dn = levels.Where(l => !l.Above).OrderByDescending(l => l.Multiple).ToList();

            Check("above 0 sits exactly on the IB high", up[0].Price == d.IbHigh && up[0].IsZero && up[0].Label == "0");
            Check("below 0 sits exactly on the IB low", dn[0].Price == d.IbLow && dn[0].IsZero && dn[0].Label == "0");
            Check("above 0.5 == IB high + 0.5R", up[1].Price == d.IbHigh + r * 0.5m && up[1].Label == "0.5");
            Check("above 1 == IB high + 1R", up[2].Price == d.IbHigh + r && up[2].Label == "1");
            Check("above 3.5 == IB high + 3.5R", up[7].Price == d.IbHigh + r * 3.5m && up[7].Label == "3.5");
            Check("below 3.5 == IB low - 3.5R", dn[7].Price == d.IbLow - r * 3.5m && dn[7].Label == "-3.5");
            Check("extensions never straddle the wrong side",
                  up.All(l => l.Price >= d.IbHigh) && dn.All(l => l.Price <= d.IbLow));

            // Switching levels off must drop them, not renumber the survivors.
            var some = MarketModel.IbLevels(d, new[] { 0m, 1m, 2m });
            Check("switched-off levels are absent", some.Count == 6);
            Check("survivors keep their own multiples",
                  some.Where(l => l.Above).Select(l => l.Label).SequenceEqual(new[] { "0", "1", "2" }));
            Check("survivor prices are unchanged by the ones removed",
                  some.Single(l => l.Above && l.Label == "2").Price == d.IbHigh + r * 2m);

            var none = MarketModel.IbLevels(d, Array.Empty<decimal>());
            Check("every level off draws nothing", none.Count == 0);

            var flat = new DayLevels { HasIb = true, IbHigh = 100, IbLow = 100 };
            Check("a flat IB yields no ladder", MarketModel.IbLevels(flat, all).Count == 0);
        }

        private static void PowerHourTests(List<Bar> bars, MarketModel model)
        {
            Section("power hour");

            var tuesday = new DateTime(2026, 8, 11);
            var d = model.Days.Single(x => x.Date == tuesday);
            var phRef = Between(bars, tuesday, PowerOpen, RthClose);

            Check("power hour covers four 15m bars", phRef.Count == 4);
            Check("power hour runs 2:00 - 3:00 PM Houston",
                  bars[d.PhStartBar].Local.TimeOfDay == PowerOpen &&
                  bars[d.PhEndBar].Local.TimeOfDay == RthClose - TimeSpan.FromMinutes(15));
            Check($"PH high {d.PhHigh} == {phRef.Max(b => b.High)}", d.PhHigh == phRef.Max(b => b.High));
            Check($"PH low {d.PhLow} == {phRef.Min(b => b.Low)}", d.PhLow == phRef.Min(b => b.Low));
            Check("PH complete", d.PhComplete);

            // Synthetic prices ramp upward, so the break is the first bar after the halt.
            Check("break resolved upward", d.PhBreakDir == 1);
            Check("break price is the PH high", d.PhBreakPrice == d.PhHigh);

            var breakBar = bars[d.PhBreakBar];
            var firstAfter = bars.First(b => b.Local > phRef.Last().Local && b.Close > d.PhHigh);
            Check($"break is the first close beyond ({breakBar.Local:ddd h:mm tt})", breakBar.Local == firstAfter.Local);
            Check("break is after the power hour", d.PhBreakBar > d.PhEndBar);

            var wick = MarketModel.Build(new Feed(bars, true), Config(false, 15));
            var wd = wick.Days.Single(x => x.Date == tuesday);
            Check("wick mode breaks no later than close mode", wd.PhBreakBar <= d.PhBreakBar);

            var last = model.Days.Last();
            Check("the day still in progress has no completed power hour",
                  last.Date == new DateTime(2026, 8, 13) && !last.PhComplete && last.PhBreakBar < 0);
        }

        private static void ClockLabelTests(TimeContext time)
        {
            Section("clock labels");

            Check("August is daylight time", time.Abbrev(new DateTime(2026, 8, 11, 9, 30, 0)) == "CDT");
            Check("December is standard time", time.Abbrev(new DateTime(2026, 12, 15, 9, 30, 0)) == "CST");
        }

        private static void DstTests()
        {
            Section("clocks-change weekend");

            // Central goes to UTC-6 on Sunday 2026-11-01. Bars are stamped UTC either side.
            var bars = Generate(new DateTime(2026, 10, 29, 17, 0, 0), new DateTime(2026, 11, 4, 15, 0, 0), 15);
            var model = BuildUtc(bars, out _);

            foreach (var date in new[] { new DateTime(2026, 10, 30), new DateTime(2026, 11, 3) })
            {
                var ny = model.Sessions.Single(s => s.Name == "New York" && s.AnchorDate == date);
                Check($"{date:MMM d} New York still opens 8:30 AM Houston", ny.StartLocal == date + RthOpen);

                var orbRef = Between(bars, date, RthOpen, RthOpen + TimeSpan.FromMinutes(15));
                Check($"{date:MMM d} opening range unaffected by the clock change",
                      ny.OpenRangeHigh == orbRef.Max(b => b.High) && ny.OpenRangeLow == orbRef.Min(b => b.Low));

                var d = model.Days.Single(x => x.Date == date);
                var ibRef = Between(bars, date, RthOpen, IbClose);
                Check($"{date:MMM d} initial balance unaffected by the clock change",
                      d.IbHigh == ibRef.Max(b => b.High) && d.IbLow == ibRef.Min(b => b.Low));
            }

            Check("the clocks-change weekend does not split the week", model.Weeks.Count == 2);
        }

        #endregion

        #region Harness

        private static List<Bar> Between(List<Bar> bars, DateTime date, TimeSpan from, TimeSpan to) =>
            bars.Where(b => b.Local.Date == date && b.Local.TimeOfDay >= from && b.Local.TimeOfDay < to).ToList();

        private static ModelConfig Config(bool breakOnClose, int orbMinutes) => new ModelConfig
        {
            Time = TimeContext.Create("Central Standard Time", BarClock.Utc, null, default, HaltHour),
            WeekStartDay = DayOfWeek.Sunday,
            WeekStartTime = WeekOpen,
            IbStart = RthOpen,
            IbMinutes = 60,
            RthStart = RthOpen,
            RthEnd = RthClose,
            PhStart = PowerOpen,
            PhEnd = RthClose,
            BreakoutOnClose = breakOnClose,
            Sessions = new[]
            {
                new SessionDef { Name = "Asia", Start = AsiaOpen, End = AsiaClose, OpeningRangeMinutes = orbMinutes },
                new SessionDef { Name = "London", Start = LondonOpen, End = LondonClose, OpeningRangeMinutes = orbMinutes },
                new SessionDef { Name = "New York", Start = RthOpen, End = RthClose, OpeningRangeMinutes = orbMinutes }
            }
        };

        private static MarketModel BuildUtc(List<Bar> bars, out TimeContext time)
        {
            var cfg = Config(true, 15);
            time = cfg.Time;
            return MarketModel.Build(new Feed(bars, true), cfg);
        }

        private sealed class Bar
        {
            public DateTime Local;
            public decimal Open, High, Low, Close;
        }

        /// <summary>
        /// Presents the generated bars to the builder as either a UTC-stamped or an
        /// already-local feed. The builder is never told which.
        /// </summary>
        private sealed class Feed : IBarWindow
        {
            private readonly List<Bar> _bars;
            private readonly bool _utc;

            public Feed(List<Bar> bars, bool utc) { _bars = bars; _utc = utc; }

            public int Count => _bars.Count;
            public DateTime Time(int b) => _utc ? TimeZoneInfo.ConvertTimeToUtc(_bars[b].Local, Houston) : _bars[b].Local;
            public decimal Open(int b) => _bars[b].Open;
            public decimal High(int b) => _bars[b].High;
            public decimal Low(int b) => _bars[b].Low;
            public decimal Close(int b) => _bars[b].Close;
        }

        /// <summary>
        /// Bars on the MNQ calendar in Houston time: continuous except the 3-4 PM daily halt
        /// and the Friday 4 PM -> Sunday 5 PM weekend.
        /// </summary>
        private static List<Bar> Generate(DateTime start, DateTime end, int stepMinutes)
        {
            var bars = new List<Bar>();
            var n = 0;

            for (var t = start; t < end; t = t.AddMinutes(stepMinutes))
            {
                if (!IsOpen(t)) continue;

                // A steady ramp, so the power hour always resolves upward and every window's
                // high and low are distinct.
                var mid = 20000m + n++;
                bars.Add(new Bar { Local = t, Open = mid - 1, High = mid + 2, Low = mid - 2, Close = mid + 1 });
            }

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

        private static void Section(string name) => Console.WriteLine($"-- {name}");

        private static void Check(string what, bool ok)
        {
            Console.WriteLine($"   {(ok ? "ok  " : "FAIL")}  {what}");
            if (!ok) _failed++;
        }

        #endregion
    }
}
