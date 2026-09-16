using System;
using System.Collections.Generic;
using System.Globalization;
using OceansOrderBlocks;

namespace OceansOrderBlocks.Tests
{
    /// <summary>
    /// Math harness. deploy.ps1 runs this first and refuses to deploy on a failure.
    ///
    /// Where a threshold decides something, the same data is run on both sides of it; where a
    /// direction decides something, both directions are asserted. A test that would pass with
    /// the comparison inverted is not a test, it is coverage.
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        // Monday 14 September 2026, asserted below rather than trusted.
        private static readonly DateTime Mon = new DateTime(2026, 9, 14);

        private static int Main()
        {
            Calendar();
            CmeBuckets();
            Windows();
            Builder();
            Atr();
            Detector();
            Displacement();
            BreakerDelta();
            Touches();
            Mitigation();
            Caps();
            Flow();
            Cvd();
            EngineHourly();
            EngineSession();
            Formatting();
            BarClockResolution();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "ALL PASS (" + _checks + " checks)"
                : _failures + " FAILURE(S) of " + _checks + " checks");

            return _failures == 0 ? 0 : 1;
        }

        private static void Calendar()
        {
            Section("calendar");
            True(Mon.DayOfWeek == DayOfWeek.Monday, "the fixture date is a Monday");
        }

        #region Session arithmetic

        private static void CmeBuckets()
        {
            Section("CME buckets");

            var sun = Mon.AddDays(-1);
            var tue = Mon.AddDays(1);

            // The trade date rolls at the 17:00 reopen, not at midnight and not at 16:00.
            Eq(Mon, Cme.TradeDate(Mon.At(16, 59)), "16:59 Monday is Monday's trade date");
            Eq(tue, Cme.TradeDate(Mon.At(17, 0)), "17:00 Monday is Tuesday's trade date");
            Eq(Mon, Cme.TradeDate(sun.At(17, 30)), "Sunday evening is Monday's trade date");

            Eq(Mon.At(9, 0), Cme.BucketStart(ObTf.H1, Mon.At(9, 59)), "1H bucket of 09:59");
            Eq(Mon.At(10, 0), Cme.BucketEnd(ObTf.H1, Mon.At(9, 0)), "1H bucket ends on the hour");

            // 4H anchored to the 17:00 reopen: 17, 21, 01, 05, 09, 13.
            Eq(Mon.At(9, 0), Cme.BucketStart(ObTf.H4, Mon.At(9, 15)), "4H bucket of 09:15 opens 09:00");
            Eq(Mon.At(5, 0), Cme.BucketStart(ObTf.H4, Mon.At(8, 59)), "4H bucket of 08:59 opens 05:00");
            Eq(Mon.At(13, 0), Cme.BucketStart(ObTf.H4, Mon.At(13, 30)), "4H bucket of 13:30 opens 13:00");
            Eq(Mon.At(16, 0), Cme.BucketEnd(ObTf.H4, Mon.At(13, 0)),
               "the 13:00 4H bar is cut short by the 16:00 halt, not run to 17:00");
            Eq(Mon.At(21, 0), Cme.BucketStart(ObTf.H4, Mon.At(21, 30)), "4H bucket of 21:30 opens 21:00");
            Eq(tue.At(1, 0), Cme.BucketEnd(ObTf.H4, Mon.At(21, 0)), "the 21:00 4H bar ends 01:00");
            Eq(Mon.At(21, 0), Cme.BucketStart(ObTf.H4, tue.At(0, 30)), "00:30 still sits in the 21:00 bar");
            Eq(sun.At(17, 0), Cme.BucketStart(ObTf.H4, sun.At(17, 5)), "Sunday reopen starts a 4H bar");

            Eq(Mon.At(17, 0), Cme.BucketStart(ObTf.Daily, tue.At(10, 0)), "Tuesday's daily bar opened Monday 17:00");
            Eq(tue.At(16, 0), Cme.BucketEnd(ObTf.Daily, Mon.At(17, 0)), "and ends at Tuesday's 16:00 halt");
            Eq(Mon.At(17, 0), Cme.BucketStart(ObTf.Daily, Mon.At(17, 0)), "17:00 is the first bar of the new day");

            Eq(sun.At(17, 0), Cme.BucketStart(ObTf.Weekly, Mon.AddDays(2).At(10, 0)), "Wednesday's week opened Sunday 17:00");
            Eq(sun.At(17, 0), Cme.BucketStart(ObTf.Weekly, sun.At(18, 0)), "Sunday evening is in the new week");
            Eq(sun.At(17, 0), Cme.BucketStart(ObTf.Weekly, Mon.AddDays(4).At(15, 59)), "Friday 15:59 is still in it");
            Eq(Mon.AddDays(4).At(16, 0), Cme.BucketEnd(ObTf.Weekly, sun.At(17, 0)), "the week ends Friday 16:00");
            Eq(Mon.AddDays(6).At(17, 0), Cme.BucketStart(ObTf.Weekly, Mon.AddDays(6).At(17, 0)),
               "the next Sunday reopen starts the next week");
        }

        private static void Windows()
        {
            Section("session window");

            var s = new TimeSpan(8, 30, 0);
            var e = new TimeSpan(15, 0, 0);

            True(Window.Contains(s, e, Mon.At(8, 30)), "08:30 is inside");
            False(Window.Contains(s, e, Mon.At(8, 29)), "08:29 is outside");
            True(Window.Contains(s, e, Mon.At(14, 59)), "14:59 is inside");
            False(Window.Contains(s, e, Mon.At(15, 0)), "15:00 is outside");

            var ws = new TimeSpan(17, 0, 0);
            var we = new TimeSpan(2, 0, 0);
            True(Window.Contains(ws, we, Mon.At(23, 0)), "wrapped window holds 23:00");
            True(Window.Contains(ws, we, Mon.At(1, 0)), "wrapped window holds 01:00");
            False(Window.Contains(ws, we, Mon.At(3, 0)), "wrapped window excludes 03:00");
        }

        #endregion

        #region Bars

        private static void Builder()
        {
            Section("HTF builder");

            var minute = TimeSpan.FromMinutes(1);

            // A clean hour of 1m bars closes on its last bar, by time -- not one bar late when
            // the next hour starts.
            var b = new HtfBuilder(ObTf.H1);
            var closed = new List<HtfBar>();
            for (var m = 0; m < 60; m++)
            {
                var price = 100m + m * 0.25m;
                b.Push(Cb(m, Mon.At(9, m), price, price + 1m, price - 0.5m, price + 0.25m, 10m, m % 2 == 0 ? 3m : -1m),
                       minute, closed);

                if (m == 58) Eq(0, closed.Count, "nothing closed before the hour's last bar");
            }

            Eq(1, closed.Count, "the hour closes on its 09:59 bar");
            var h = closed[0];
            Eq(Mon.At(9, 0), h.Start, "bar start");
            Eq(100m, h.Open, "open is the first bar's open");
            Eq(100m + 59 * 0.25m + 1m, h.High, "high is the max high");
            Eq(99.5m, h.Low, "low is the min low");
            Eq(100m + 59 * 0.25m + 0.25m, h.Close, "close is the last bar's close");
            Eq(600m, h.Volume, "volume sums");
            Eq(30 * 3m - 30 * 1m, h.Delta, "delta sums");
            Eq(0, h.FirstBar, "first chart bar");
            Eq(59, h.LastBar, "last chart bar");
            False(h.Partial, "an hour loaded from its first minute is complete");
            Eq(1, b.CompleteBars, "counted as complete");

            // Unknown bar size (tick/range charts): closes when the next bucket arrives.
            var k = new HtfBuilder(ObTf.H1);
            var kc = new List<HtfBar>();
            for (var m = 0; m < 60; m++) k.Push(Cb(m, Mon.At(9, m), 100m, 101m, 99m, 100m), TimeSpan.Zero, kc);
            Eq(0, kc.Count, "no bar size: still open after 09:59");
            k.Push(Cb(60, Mon.At(10, 0), 100m, 101m, 99m, 100m), TimeSpan.Zero, kc);
            Eq(1, kc.Count, "no bar size: closes when 10:00 arrives");

            // History that starts mid-bucket gives a bar whose OHLC is not the real one.
            var p = new HtfBuilder(ObTf.H1);
            var pc = new List<HtfBar>();
            for (var m = 17; m < 60; m++) p.Push(Cb(m, Mon.At(9, m), 100m, 101m, 99m, 100m), minute, pc);
            for (var m = 0; m < 60; m++) p.Push(Cb(60 + m, Mon.At(10, m), 100m, 101m, 99m, 100m), minute, pc);
            Eq(2, pc.Count, "both buckets close");
            True(pc[0].Partial, "a first bucket loaded from 09:17 is partial");
            False(pc[1].Partial, "the next one is not");
            Eq(1, p.CompleteBars, "only the complete one counts");

            // One minute missing is still missing.
            var q = new HtfBuilder(ObTf.H1);
            var qc = new List<HtfBar>();
            for (var m = 1; m < 60; m++) q.Push(Cb(m, Mon.At(9, m), 100m, 101m, 99m, 100m), minute, qc);
            True(qc.Count == 1 && qc[0].Partial, "a first bucket loaded from 09:01 is partial");
        }

        private static void Atr()
        {
            Section("Wilder ATR");

            var a = new WilderAtr(3);
            a.Push(10m, 8m, 9m);      // TR 2
            a.Push(11m, 9m, 10m);     // TR 2
            False(a.Ready, "not ready before the period");
            Eq(0m, a.Value, "and reads zero, not a partial average");

            a.Push(14m, 10m, 13m);    // TR max(4, |14-10|, |10-10|) = 4
            True(a.Ready, "ready at the period");
            Eq(8m / 3m, a.Value, "seed is the simple average of the first TRs");

            a.Push(20m, 19m, 19.5m);  // gap: TR max(1, |20-13|, |19-13|) = 7
            Eq((8m / 3m * 2m + 7m) / 3m, a.Value, "RMA step, and the gap to the prior close counts");
        }

        private static void Detector()
        {
            Section("OB detector");

            var wick = new ObRules();
            var body = new ObRules { Mode = ZoneMode.BodyOnly };

            // Bull: bearish candle, then a bullish body closing above its high.
            var s = Pair(H(105m, 106m, 99m, 100m, 0, 59), H(100m, 108m, 99.5m, 107m, 60, 119, 42m), wick);
            True(s != null && s.Bull, "bearish candle closed through by a bullish body is a bull OB");
            if (s != null)
            {
                Eq(106m, s.Top, "wick zone top is the OB high");
                Eq(99m, s.Bottom, "wick zone bottom is the OB low");
                Eq(0, s.StartBar, "zone starts at the OB candle");
                Eq(119, s.LiveFrom, "zone is live after the breaker's last bar");
                Eq(42m, s.BreakerDelta, "breaker delta carried");
            }

            var sb = Pair(H(105m, 106m, 99m, 100m, 0, 59), H(100m, 108m, 99.5m, 107m, 60, 119), body);
            if (sb != null)
            {
                Eq(105m, sb.Top, "body zone top is the OB open");
                Eq(100m, sb.Bottom, "body zone bottom is the OB close");
            }
            else True(false, "body mode fires on the same data");

            // The close has to be strictly beyond the extreme -- both sides of it.
            Null(Pair(H(105m, 106m, 99m, 100m), H(100m, 108m, 99.5m, 106m), wick),
                 "a close AT the OB high is not a break");
            NotNull(Pair(H(105m, 106m, 99m, 100m), H(100m, 108m, 99.5m, 106.25m), wick),
                    "a close one tick above the OB high is");

            Null(Pair(H(100m, 106m, 99m, 105m), H(105m, 108m, 99.5m, 107m), wick),
                 "a bullish candle cannot be a bull OB");
            Null(Pair(H(105m, 106m, 99m, 100m), H(107m, 108m, 99.5m, 106.5m), wick),
                 "a bearish breaker cannot make a bull OB, even closing above");

            // Bear mirror.
            var bear = Pair(H(100m, 106m, 99m, 105m), H(105m, 105.5m, 98m, 98.75m), wick);
            True(bear != null && !bear.Bull, "bullish candle closed through by a bearish body is a bear OB");
            if (bear != null)
            {
                Eq(106m, bear.Top, "bear zone top");
                Eq(99m, bear.Bottom, "bear zone bottom");
            }

            Null(Pair(H(100m, 106m, 99m, 105m), H(105m, 105.5m, 98m, 99m), wick),
                 "a close AT the OB low is not a break");
            Null(Pair(H(105m, 106m, 99m, 100m), H(100m, 100.5m, 98m, 98.75m), wick),
                 "a bearish candle cannot be a bear OB");

            // A partial first bucket is not a real candle.
            var partial = H(105m, 106m, 99m, 100m);
            partial.Partial = true;
            Null(Pair(partial, H(100m, 108m, 99.5m, 107m), wick), "a partial bar is never an OB candle");
        }

        private static void Displacement()
        {
            Section("displacement filter");

            // Thirteen dojis with TR 4, then a bearish OB candle with TR 4 -> ATR exactly 4 at
            // the OB, and a breaker with TR 4 keeps it there. Body 4 = 1.0 ATR.
            Func<decimal, ObSignal> run = mult =>
            {
                var d = new ObDetector();
                var r = new ObRules { MinDisplacementAtr = mult };
                for (var i = 0; i < 13; i++) d.OnClose(H(100m, 102m, 98m, 100m), r);
                d.OnClose(H(101m, 102m, 98m, 99m), r);
                return d.OnClose(H(99m, 103m, 99m, 103m), r);
            };

            NotNull(run(1.0m), "body of exactly 1.0 ATR passes a 1.0 filter");
            Null(run(1.01m), "the same body fails a 1.01 filter");

            // ATR not ready: the filter blocks rather than comparing against zero.
            var cold = new ObDetector();
            var on = new ObRules { MinDisplacementAtr = 0.5m };
            cold.OnClose(H(105m, 106m, 99m, 100m), on);
            Null(cold.OnClose(H(100m, 108m, 99.5m, 107m), on), "filter on, ATR cold: no block");

            var off = new ObDetector();
            var none = new ObRules();
            off.OnClose(H(105m, 106m, 99m, 100m), none);
            NotNull(off.OnClose(H(100m, 108m, 99.5m, 107m), none), "filter off, ATR cold: block");
        }

        private static void BreakerDelta()
        {
            Section("breaker delta");

            var agree = new ObRules { BreakerDeltaAgrees = true };
            var free = new ObRules();

            NotNull(Pair(H(105m, 106m, 99m, 100m), H(100m, 108m, 99.5m, 107m, 0, 0, 50m), agree),
                    "bull breaker with buyers (+50) passes");
            Null(Pair(H(105m, 106m, 99m, 100m), H(100m, 108m, 99.5m, 107m, 0, 0, -50m), agree),
                 "bull breaker with sellers (-50) is blocked");
            Null(Pair(H(105m, 106m, 99m, 100m), H(100m, 108m, 99.5m, 107m, 0, 0, 0m), agree),
                 "bull breaker with flat delta is blocked");
            NotNull(Pair(H(105m, 106m, 99m, 100m), H(100m, 108m, 99.5m, 107m, 0, 0, -50m), free),
                    "without the rule the same breaker passes");

            NotNull(Pair(H(100m, 106m, 99m, 105m), H(105m, 105.5m, 98m, 98.75m, 0, 0, -50m), agree),
                    "bear breaker with sellers passes");
            Null(Pair(H(100m, 106m, 99m, 105m), H(105m, 105.5m, 98m, 98.75m, 0, 0, 50m), agree),
                 "bear breaker with buyers is blocked");
            Null(Pair(H(100m, 106m, 99m, 105m), H(105m, 105.5m, 98m, 98.75m, 0, 0, 0m), agree),
                 "bear breaker with flat delta is blocked");
        }

        #endregion

        #region Zones

        private static Zone BullZone(ZoneBook book, int liveFrom)
        {
            return book.Add(ObTf.H1, new ObSignal { Bull = true, Top = 106m, Bottom = 99m, LiveFrom = liveFrom });
        }

        private static Zone BearZone(ZoneBook book, int liveFrom)
        {
            return book.Add(ObTf.H1, new ObSignal { Bull = false, Top = 106m, Bottom = 99m, LiveFrom = liveFrom });
        }

        private static void Touches()
        {
            Section("touches");

            var book = new ZoneBook();
            var z = BullZone(book, 10);

            book.Advance(Cb(10, Mon, 100m, 101m, 95m, 100m));
            Eq(0, z.Touches, "the breaker bar itself is never a touch");
            True(book.Active.Contains(z), "and never mitigates, even trading through");

            book.Advance(Cb(11, Mon, 108m, 110m, 107m, 109m));
            Eq(0, z.Touches, "a bar above the zone is not a touch");
            True(z.Status == ZoneStatus.Fresh, "still fresh");
            True(z.FreshTouch(105m, 108m, 12), "a bar reaching 105 would be its first touch");
            False(z.FreshTouch(106.25m, 108m, 12), "a bar holding 106.25 would not");
            False(z.FreshTouch(105m, 108m, 10), "nor would the breaker bar");

            book.Advance(Cb(12, Mon, 107m, 108m, 106m, 107m));
            Eq(1, z.Touches, "a low exactly at the top edge touches");
            True(z.Status == ZoneStatus.Tested, "first touch makes it tested");
            False(z.FreshTouch(105m, 108m, 13), "a tested zone does not alert again");

            book.Advance(Cb(13, Mon, 105m, 106m, 104m, 105m));
            Eq(1, z.Touches, "staying inside is the same touch");

            book.Advance(Cb(14, Mon, 107m, 108m, 106.25m, 107m));
            book.Advance(Cb(15, Mon, 104m, 105m, 100m, 104m));
            Eq(2, z.Touches, "leaving and coming back is a second touch");
        }

        private static void Mitigation()
        {
            Section("mitigation");

            // Wick trigger, bull: strictly below the bottom.
            var w = new ZoneBook();
            var bz = BullZone(w, 0);
            w.Advance(Cb(1, Mon, 100m, 101m, 99m, 100m));
            True(w.Active.Contains(bz), "bull: a low AT the bottom is not mitigation");
            w.Advance(Cb(2, Mon, 100m, 101m, 98.75m, 100m));
            False(w.Active.Contains(bz), "bull: a low one tick below is");
            Eq(2, bz.EndBar, "end bar recorded");
            True(bz.Status == ZoneStatus.Mitigated, "status mitigated");
            Eq(0, w.Faded.Count, "not kept when keep-mitigated is off");

            // Wick trigger, bear: strictly above the top.
            var wb = new ZoneBook();
            var sz = BearZone(wb, 0);
            wb.Advance(Cb(1, Mon, 105m, 106m, 104m, 105m));
            True(wb.Active.Contains(sz), "bear: a high AT the top is not mitigation");
            wb.Advance(Cb(2, Mon, 105m, 106.25m, 104m, 105m));
            False(wb.Active.Contains(sz), "bear: a high one tick above is");

            // Close trigger: a wick through is not enough, a close through is.
            var c = new ZoneBook { Trigger = MitigationTrigger.Close };
            var cz = BullZone(c, 0);
            c.Advance(Cb(1, Mon, 100m, 101m, 97m, 99.5m));
            True(c.Active.Contains(cz), "close trigger, bull: wick through, close inside survives");
            c.Advance(Cb(2, Mon, 100m, 101m, 97m, 98.75m));
            False(c.Active.Contains(cz), "close trigger, bull: close below mitigates");

            var cb = new ZoneBook { Trigger = MitigationTrigger.Close };
            var cbz = BearZone(cb, 0);
            cb.Advance(Cb(1, Mon, 105m, 110m, 104m, 105m));
            True(cb.Active.Contains(cbz), "close trigger, bear: wick through, close inside survives");
            cb.Advance(Cb(2, Mon, 105m, 110m, 104m, 106.5m));
            False(cb.Active.Contains(cbz), "close trigger, bear: close above mitigates");

            // Direction: a bull zone is not mitigated by trading through its TOP.
            var d = new ZoneBook();
            var dz = BullZone(d, 0);
            d.Advance(Cb(1, Mon, 105m, 120m, 104m, 119m));
            True(d.Active.Contains(dz), "bull zone survives a rally through its top");
            var e = new ZoneBook();
            var ez = BearZone(e, 0);
            e.Advance(Cb(1, Mon, 100m, 101m, 80m, 81m));
            True(e.Active.Contains(ez), "bear zone survives a selloff through its bottom");
        }

        private static void Caps()
        {
            Section("caps");

            // Faded list is bounded and drops the oldest.
            var f = new ZoneBook { KeepMitigated = true, MaxFaded = 2 };
            var a = BullZone(f, 0);
            var b = BullZone(f, 0);
            var c = BullZone(f, 0);
            f.Advance(Cb(1, Mon, 100m, 101m, 90m, 100m));
            Eq(0, f.Active.Count, "all three mitigated");
            Eq(2, f.Faded.Count, "faded list capped at 2");
            False(f.Faded.Contains(a), "the oldest faded block is the one dropped");
            True(f.Faded.Contains(b) && f.Faded.Contains(c), "the newest two are kept");

            // Per timeframe AND side.
            var p = new ZoneBook { MaxPerSide = 2 };
            var weekly = p.Add(ObTf.Weekly, new ObSignal { Bull = true, Top = 50m, Bottom = 40m });
            var h1Bear = p.Add(ObTf.H1, new ObSignal { Bull = false, Top = 200m, Bottom = 190m });
            var first = BullZone(p, 0);
            BullZone(p, 0);
            BullZone(p, 0);

            var h1Bull = 0;
            foreach (var z in p.Active) if (z.Tf == ObTf.H1 && z.Bull) h1Bull++;

            Eq(2, h1Bull, "1H bull capped at 2");
            False(p.Active.Contains(first), "the oldest 1H bull is the one dropped");
            True(p.Active.Contains(weekly), "a busy 1H does not push the weekly block off");
            True(p.Active.Contains(h1Bear), "nor the 1H bear block");
        }

        private static void Flow()
        {
            Section("in-zone flow");

            var z = new Zone { Top = 106m, Bottom = 99m };
            ZoneFlow.Add(z, new List<Level>
            {
                new Level(98.75m, 100m, 50m),   // below: excluded
                new Level(99m, 10m, -4m),       // bottom edge: included
                new Level(102m, 30m, 6m),
                new Level(106m, 5m, -1m),       // top edge: included
                new Level(106.25m, 200m, -90m)  // above: excluded
            });

            Eq(45m, z.FlowVolume, "only levels inside the zone, edges inclusive");
            Eq(1m, z.FlowDelta, "delta of those levels only");
            Eq(102m, z.Poc, "in-zone POC");

            ZoneFlow.Add(z, new List<Level> { new Level(99m, 20m, 0m) });
            Eq(102m, z.Poc, "a tie keeps the level that got there first");
            ZoneFlow.Add(z, new List<Level> { new Level(99m, 1m, 0m) });
            Eq(99m, z.Poc, "one more lot and it moves");

            decimal v, dl;
            ZoneFlow.Measure(z, new List<Level> { new Level(100m, 7m, -3m), new Level(110m, 9m, 9m) }, out v, out dl);
            Eq(7m, v, "measure: forming bar volume inside");
            Eq(-3m, dl, "measure: forming bar delta inside");
            Eq(66m, z.FlowVolume, "measure does not commit");
        }

        private static void Cvd()
        {
            Section("CVD");

            var s = new CvdTracker(CvdAnchor.Session, new TimeSpan(8, 30, 0), new TimeSpan(15, 0, 0));
            s.Push(Mon.At(8, 0), 50m);
            Eq(0m, s.Value, "overnight before the first session is not counted");
            s.Push(Mon.At(8, 30), 10m);
            Eq(10m, s.Value, "session open resets to the bar's delta");
            s.Push(Mon.At(8, 31), 5m);
            Eq(15m, s.Value, "accumulates inside");
            s.Push(Mon.At(15, 0), 100m);
            Eq(15m, s.Value, "frozen from 15:00");
            s.Push(Mon.At(17, 0), -40m);
            Eq(15m, s.Value, "still frozen overnight -- it reads RTH, not RTH plus Globex");
            s.Push(Mon.AddDays(1).At(8, 30), 7m);
            Eq(7m, s.Value, "resets at the next open");
            Eq(10m, s.Peek(Mon.AddDays(1).At(8, 31), 3m), "peek includes the forming bar");
            Eq(7m, s.Value, "peek does not commit");
            s.Push(Mon.AddDays(1).At(15, 0), 1m);
            Eq(4m, s.Peek(Mon.AddDays(2).At(8, 30), 4m), "peek on the first bar of a new session resets");

            var d = new CvdTracker(CvdAnchor.Daily, new TimeSpan(8, 30, 0), new TimeSpan(15, 0, 0));
            d.Push(Mon.At(16, 59), 5m);
            d.Push(Mon.At(15, 0), 1m);
            Eq(6m, d.Value, "daily accumulates across the cash close");
            d.Push(Mon.At(17, 0), 3m);
            Eq(3m, d.Value, "daily resets at the 17:00 reopen");
            d.Push(Mon.AddDays(1).At(8, 30), 2m);
            Eq(5m, d.Value, "and does not reset at the RTH open");
        }

        #endregion

        #region Engine

        private static void EngineHourly()
        {
            Section("engine: 1H block end to end");

            var cfg = new EngineConfig { BarDuration = TimeSpan.FromMinutes(1) };
            cfg.Enabled[(int)ObTf.H1] = true;
            var eng = new ObEngine(cfg);

            var bars = new List<ChartBar>();
            Hour(bars, Mon, 9, 100m, 103m, 99m, 102m, 0m);    // bullish
            // Bearish, and closes ABOVE hour 9's low (99) -- closing below it would make hour 9
            // a bear OB and put a second zone in play.
            Hour(bars, Mon, 10, 102m, 104m, 97m, 99.5m, 0m);  // the OB candle
            Hour(bars, Mon, 11, 98m, 108m, 97.5m, 107m, 1m);  // bullish, closes above 104: breaker

            var calls = 0;
            var levels = new List<Level>();
            Func<IList<Level>> feed = () => { calls++; return levels; };

            for (var i = 0; i < bars.Count; i++)
            {
                eng.Fold(bars[i], feed);
                if (i == bars.Count - 2) Eq(0, eng.Book.Active.Count, "no block before the breaker hour closes");
            }

            Eq(1, eng.Book.Active.Count, "a block on the breaker hour's last bar -- not a bar later");
            var z = eng.Book.Active.Count > 0 ? eng.Book.Active[0] : new Zone();
            True(z.Tf == ObTf.H1 && z.Bull, "1H demand");
            Eq(104m, z.Top, "top is the 10:00 hour's high");
            Eq(97m, z.Bottom, "bottom is the 10:00 hour's low");
            Eq(60, z.StartBar, "drawn from the OB hour's first chart bar");
            Eq(179, z.LiveFrom, "live from after 11:59");
            Eq(60m, z.BreakerDelta, "breaker delta is the hour's real delta");
            Eq(3, eng.CompleteBars(ObTf.H1), "all three hours complete -- 9 is first but loaded from 09:00");
            Eq(0, calls, "no zone was live during the build, so no footprint was read");

            // Above the zone: no footprint read at all.
            var idx = bars.Count;
            eng.Fold(Cb(idx++, Mon.At(12, 0), 105m, 105.25m, 104.75m, 105m), feed);
            Eq(0, calls, "a bar nowhere near a zone never reads its footprint");

            levels.Add(new Level(103.5m, 8m, -6m));
            levels.Add(new Level(104m, 4m, -2m));
            levels.Add(new Level(104.25m, 20m, 10m));
            eng.Fold(Cb(idx++, Mon.At(12, 1), 105m, 105m, 103.5m, 104.5m), feed);
            Eq(1, calls, "a bar into the zone reads it once");
            Eq(12m, z.FlowVolume, "in-zone volume");
            Eq(-8m, z.FlowDelta, "in-zone delta: sellers hitting inside demand");
            Eq(103.5m, z.Poc, "in-zone POC");
            True(z.Status == ZoneStatus.Tested, "tested");

            eng.Fold(Cb(idx++, Mon.At(12, 2), 104m, 104m, 96.75m, 97m), feed);
            Eq(0, eng.Book.Active.Count, "a wick through the bottom mitigates it");

            var none = new ObEngine(new EngineConfig { BarDuration = TimeSpan.FromMinutes(1) });
            foreach (var b in bars) none.Fold(b, null);
            Eq(0, none.Book.Active.Count, "a disabled timeframe detects nothing");
        }

        private static void EngineSession()
        {
            Section("engine: session blocks");

            Func<DateTime, int> run = start =>
            {
                var cfg = new EngineConfig { BarDuration = TimeSpan.FromMinutes(1) };
                cfg.Enabled[(int)ObTf.Session] = true;
                var eng = new ObEngine(cfg);
                eng.Fold(Cb(0, start, 100m, 100.5m, 99m, 99.25m), null);
                eng.Fold(Cb(1, start.AddMinutes(1), 99.25m, 101m, 99.25m, 101m), null);
                return eng.Book.Active.Count;
            };

            Eq(1, run(Mon.At(9, 0)), "both candles in the window: block");
            Eq(0, run(Mon.At(7, 0)), "both before the window: none");
            Eq(0, run(Mon.At(8, 29)), "OB candle at 08:29, one minute early: none");
            Eq(1, run(Mon.At(8, 30)), "OB candle at 08:30: block");
            Eq(0, run(Mon.At(14, 59)), "breaker at 15:00, after the window: none");

            // RTH-only chart: yesterday's last bar next to today's first.
            var cfg2 = new EngineConfig { BarDuration = TimeSpan.FromMinutes(1) };
            cfg2.Enabled[(int)ObTf.Session] = true;
            var e2 = new ObEngine(cfg2);
            e2.Fold(Cb(0, Mon.At(14, 59), 100m, 100.5m, 99m, 99.25m), null);
            e2.Fold(Cb(1, Mon.AddDays(1).At(8, 30), 99.25m, 101m, 99.25m, 101m), null);
            Eq(0, e2.Book.Active.Count, "never pairs candles across two trade dates");
        }

        #endregion

        private static void Formatting()
        {
            Section("formatting");

            EqS("950", Fmt.Compact(950m), "under a thousand");
            EqS("12.3K", Fmt.Compact(12345m), "thousands");
            EqS("-1.25M", Fmt.Compact(-1250000m), "millions, negative");
            EqS("+1.2K", Fmt.Signed(1200m), "signed positive");
            EqS("-40", Fmt.Signed(-40m), "signed negative");
            EqS("0", Fmt.Signed(0m), "zero has no sign");
            EqS("21460.25", Fmt.Price(21460.25m), "price");
        }

        #region Clock (ported from oceans-anchor, same resolver)

        private static void BarClockResolution()
        {
            Section("bar clock");

            var zone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

            var local = MakeBars(new DateTime(2026, 8, 17), 15, zone, false);
            var utc = MakeBars(new DateTime(2026, 8, 17), 15, zone, true);

            var localCtx = BarClockContext.Create("Central Standard Time", BarClock.Auto, local,
                                                  default(DateTime), default(DateTime), 16,
                                                  TimeSpan.FromMinutes(30));
            True(localCtx.Valid, "local-stamped bars settle: " + localCtx.Error);
            True(localCtx.Clock == BarClock.AlreadyLocal, "local stamps read as local");

            var utcCtx = BarClockContext.Create("Central Standard Time", BarClock.Auto, utc,
                                                default(DateTime), default(DateTime), 16,
                                                TimeSpan.FromMinutes(30));
            True(utcCtx.Valid, "UTC-stamped bars settle: " + utcCtx.Error);
            True(utcCtx.Clock == BarClock.Utc, "UTC stamps read as UTC");

            var bad = BarClockContext.Create("Nowhere/Nothing", BarClock.Auto, local,
                                             default(DateTime), default(DateTime), 16,
                                             TimeSpan.FromMinutes(30));
            False(bad.Valid, "an unknown time zone fails loudly");

            // ToUtc, which the alert freshness gate relies on. September is CDT, UTC-5.
            var lu = localCtx.ToUtc(Mon.At(9, 0));
            True(lu.HasValue && lu.Value == Mon.At(14, 0), "local 09:00 CDT is 14:00 UTC");
            var uu = utcCtx.ToUtc(Mon.At(14, 0));
            True(uu.HasValue && uu.Value == Mon.At(14, 0), "a UTC stamp passes straight through");
            True(localCtx.ToLocal(Mon.At(9, 0)) == Mon.At(9, 0), "local stamps are not shifted");
            True(utcCtx.ToLocal(Mon.At(14, 0)) == Mon.At(9, 0), "UTC stamps shift to Houston");
        }

        private static SyntheticBars MakeBars(DateTime start, int days, TimeZoneInfo zone, bool asUtc)
        {
            var bars = new SyntheticBars();
            var price = 20000m;

            for (var d = 0; d < days; d++)
            {
                var day = start.AddDays(d);
                if (day.DayOfWeek == DayOfWeek.Saturday) continue;

                for (var slot = 0; slot < 48; slot++)
                {
                    var t = day.AddMinutes(30 * slot);
                    if (t.Hour == 16) continue;
                    if (t.DayOfWeek == DayOfWeek.Sunday && t.Hour < 17) continue;
                    if (t.DayOfWeek == DayOfWeek.Friday && t.Hour >= 16) continue;

                    var stamp = t;
                    if (asUtc)
                    {
                        try
                        {
                            stamp = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone);
                        }
                        catch { continue; }
                    }

                    price += (slot % 7) - 3;
                    bars.Add(stamp, price, price + 5m, price - 5m, price + 1m);
                }
            }

            return bars;
        }

        private sealed class SyntheticBars : IBarWindow
        {
            private readonly List<DateTime> _t = new List<DateTime>();
            private readonly List<decimal[]> _p = new List<decimal[]>();

            public void Add(DateTime time, decimal o, decimal h, decimal l, decimal c)
            {
                _t.Add(time);
                _p.Add(new[] { o, h, l, c });
            }

            public int Count { get { return _t.Count; } }
            public DateTime Time(int bar) { return _t[bar]; }
            public decimal Open(int bar) { return _p[bar][0]; }
            public decimal High(int bar) { return _p[bar][1]; }
            public decimal Low(int bar) { return _p[bar][2]; }
            public decimal Close(int bar) { return _p[bar][3]; }
        }

        #endregion

        #region Fixtures

        private static DateTime At(this DateTime day, int hour, int minute)
        {
            return day.Date.AddHours(hour).AddMinutes(minute);
        }

        private static ChartBar Cb(int index, DateTime local, decimal o, decimal h, decimal l, decimal c,
                                   decimal volume = 10m, decimal delta = 0m)
        {
            return new ChartBar
            {
                Index = index, Local = local, Open = o, High = h, Low = l, Close = c,
                Volume = volume, Delta = delta
            };
        }

        private static HtfBar H(decimal o, decimal h, decimal l, decimal c, int first = 0, int last = 0,
                                decimal delta = 0m)
        {
            return new HtfBar
            {
                Start = Mon, FirstBar = first, LastBar = last,
                Open = o, High = h, Low = l, Close = c, Delta = delta
            };
        }

        private static ObSignal Pair(HtfBar ob, HtfBar breaker, ObRules r)
        {
            var d = new ObDetector();
            d.OnClose(ob, r);
            return d.OnClose(breaker, r);
        }

        /// <summary>
        /// Sixty 1m bars that aggregate to exactly (o, h, l, c): a straight walk from open to
        /// close, with the hour's high and low both printed on the 30th minute.
        /// </summary>
        private static void Hour(List<ChartBar> bars, DateTime day, int hour, decimal o, decimal h,
                                 decimal l, decimal c, decimal deltaPerBar)
        {
            var prev = o;
            for (var m = 0; m < 60; m++)
            {
                var close = m == 59 ? c : o + (c - o) * (m + 1) / 60m;
                var hi = Math.Max(prev, close);
                var lo = Math.Min(prev, close);
                if (m == 30) { hi = h; lo = l; }

                bars.Add(Cb(bars.Count, day.At(hour, m), prev, hi, lo, close, 10m, deltaPerBar));
                prev = close;
            }
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("-- " + name);
        }

        private static void True(bool condition, string what)
        {
            _checks++;
            if (condition) return;

            _failures++;
            Console.WriteLine("   FAIL  " + what);
        }

        private static void False(bool condition, string what) { True(!condition, what); }

        private static void Null(object o, string what) { True(o == null, what); }

        private static void NotNull(object o, string what) { True(o != null, what); }

        private static void Eq(decimal expected, decimal actual, string what)
        {
            _checks++;
            if (expected == actual) return;

            _failures++;
            Console.WriteLine("   FAIL  " + what + " (expected " +
                              expected.ToString(CultureInfo.InvariantCulture) + ", got " +
                              actual.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private static void Eq(int expected, int actual, string what) { Eq((decimal)expected, (decimal)actual, what); }

        private static void Eq(DateTime expected, DateTime actual, string what)
        {
            _checks++;
            if (expected == actual) return;

            _failures++;
            Console.WriteLine("   FAIL  " + what + " (expected " + expected.ToString("ddd MM-dd HH:mm") +
                              ", got " + actual.ToString("ddd MM-dd HH:mm") + ")");
        }

        private static void EqS(string expected, string actual, string what)
        {
            _checks++;
            if (expected == actual) return;

            _failures++;
            Console.WriteLine("   FAIL  " + what + " (expected '" + expected + "', got '" + actual + "')");
        }

        #endregion
    }
}
