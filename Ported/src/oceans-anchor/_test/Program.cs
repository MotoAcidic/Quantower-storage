using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OceansAnchor;

namespace OceansAnchor.Tests
{
    /// <summary>
    /// Math harness. deploy.ps1 runs this first and refuses to deploy on a failure.
    ///
    /// The cases below are built so a BROKEN rule fails rather than quietly passing. Where a
    /// threshold decides something, the same data is run on both sides of it; where a direction
    /// decides something, both directions are asserted. A test that would pass with the
    /// comparison inverted is not a test, it is coverage.
    /// </summary>
    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        private const decimal Tick = 0.25m;

        private static int Main()
        {
            ProfileMaths();
            ValueAreaExpansion();
            ProfileSmoothing();
            HvnOutlierFilter();
            ShelfWidthCap();
            DoubleDistribution();
            TradeDates();
            WindowOverlap();
            BarClockResolution();
            Percentiles();
            TapeSideMatching();
            TradeIdentity();
            DisplacementResolution();
            ClusterScoring();
            OneTimeframingGuard();
            AlertWindow();
            NakedPocs();
            ZoneTraversal();
            ZoneBuilding();
            StateMachine();
            CvdDivergence();
            CsvRoundTrip();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "ALL PASS (" + _checks + " checks)"
                : _failures + " FAILURE(S) of " + _checks + " checks");

            return _failures == 0 ? 0 : 1;
        }

        #region Profile maths

        private static void ProfileMaths()
        {
            Section("profile maths");

            var p = new SessionProfile();
            p.NoteBar(0, 100.00m, 99.00m);

            // A clean single distribution peaking at 99.50.
            AddLadder(p, 99.00m, new[] { 10m, 20m, 60m, 140m, 60m, 20m, 10m });

            p.ComputePoc();

            Eq(99.75m, p.Poc, "POC is the argmax bin");
            Eq(140m, p.PocVol, "POC volume");
            Eq(320m, p.TotalVol, "total volume");

            // Ties resolve toward the middle of the range, deterministically -- not by whichever
            // bin the dictionary happened to hand over last. A POC that moves between two equal
            // bins from session to session drags its whole zone with it.
            //
            // Both cases use the SAME tied ladder and differ only in where the range centre
            // sits, so a rule that just took the first or the last maximum fails one of them.
            var lowMid = new SessionProfile();
            lowMid.NoteBar(0, 100.75m, 99.75m);            // mid 100.25, nearer the lower bin
            AddLadder(lowMid, 100.00m, new[] { 50m, 10m, 10m, 50m });
            lowMid.ComputePoc();
            Eq(100.00m, lowMid.Poc, "a tie resolves to the bin nearer the range centre");

            var highMid = new SessionProfile();
            highMid.NoteBar(0, 101.75m, 100.75m);          // mid 101.25, nearer the upper bin
            AddLadder(highMid, 100.75m, new[] { 50m, 10m, 10m, 50m });
            highMid.ComputePoc();
            Eq(101.50m, highMid.Poc, "and the other way when the centre moves");
        }

        private static void ValueAreaExpansion()
        {
            Section("value area");

            // Heavy BELOW the POC. This is the case that pins the expansion rule: an algorithm
            // that always grows upward, or that picks a side by anything other than which side
            // holds more volume, gets this backwards.
            var below = new SessionProfile();
            below.NoteBar(0, 101.25m, 100.00m);
            AddLadder(below, 100.00m, new[] { 60m, 70m, 80m, 100m, 1m, 1m, 1m });
            below.ComputePoc();
            below.ComputeValueArea(Tick, 0.70m);

            Eq(100.75m, below.Poc, "POC of the heavy-below profile");
            True(below.Poc - below.Val > below.Vah - below.Poc,
                 "the value area expands DOWN toward the volume (" + below.Val + "-" + below.Vah + ")");

            Covers(below, 0.70m, "heavy-below value area holds 70% of the volume");

            // The mirror image, so the rule is not just "always go down" either.
            var above = new SessionProfile();
            above.NoteBar(0, 101.50m, 100.25m);
            AddLadder(above, 100.25m, new[] { 1m, 1m, 1m, 100m, 80m, 70m, 60m });
            above.ComputePoc();
            above.ComputeValueArea(Tick, 0.70m);

            Eq(101.00m, above.Poc, "POC of the heavy-above profile");
            True(above.Vah - above.Poc > above.Poc - above.Val,
                 "the value area expands UP toward the volume (" + above.Val + "-" + above.Vah + ")");

            Covers(above, 0.70m, "heavy-above value area holds 70% of the volume");

            // Invariants that must hold whatever the shape.
            var plain = new SessionProfile();
            plain.NoteBar(0, 102.00m, 99.00m);
            AddLadder(plain, 100.00m, new[] { 5m, 10m, 20m, 100m, 20m, 10m, 5m });
            plain.ComputePoc();
            plain.ComputeValueArea(Tick, 0.70m);

            True(plain.Vah >= plain.Poc && plain.Val <= plain.Poc, "the value area brackets the POC");
            True(plain.Vah <= plain.High && plain.Val >= plain.Low,
                 "the value area stays inside the profile");
            Covers(plain, 0.70m, "symmetric value area holds 70% of the volume");

            // A tighter target must not produce a WIDER area.
            var tight = new SessionProfile();
            tight.NoteBar(0, 102.00m, 99.00m);
            AddLadder(tight, 100.00m, new[] { 5m, 10m, 20m, 100m, 20m, 10m, 5m });
            tight.ComputePoc();
            tight.ComputeValueArea(Tick, 0.50m);

            True(tight.Vah - tight.Val <= plain.Vah - plain.Val,
                 "a 50% area is no wider than a 70% one");
        }

        /// <summary>Sums the ladder inside the computed area and checks it really covers the target.</summary>
        private static void Covers(SessionProfile p, decimal target, string what)
        {
            var inside = 0m;
            foreach (var kv in p.VolByPrice)
                if (kv.Key >= p.Val && kv.Key <= p.Vah) inside += kv.Value;

            var share = p.TotalVol > 0m ? inside / p.TotalVol : 0m;
            True(share >= target, what + " (got " + Math.Round(share * 100m, 1) + "%)");
        }

        private static void ProfileSmoothing()
        {
            Section("profile smoothing");

            // A single spike, so where it bleeds to is visible. The window is centred: the bin
            // ABOVE the spike must pick up as much of it as the bin below. A one-sided window
            // drags every shelf edge toward one end of the ladder, which moves zones.
            var raw = new[] { 0m, 0m, 30m, 0m, 0m };
            var smoothed = ProfileMath.Smooth(raw, 1);

            Eq(5, smoothed.Length, "smoothing preserves the ladder length");
            Eq(10m, smoothed[1], "the bin below the spike takes a third of it");
            Eq(10m, smoothed[2], "the spike itself averages with both neighbours");
            Eq(10m, smoothed[3], "and the bin ABOVE takes the same third");
            True(smoothed[1] == smoothed[3], "the smoothing window is symmetric, not one-sided");

            // Edges clamp rather than wrapping or padding with zeroes.
            Eq(0m, smoothed[0], "an edge bin averages only what exists");

            // k = 0 is a no-op, not a divide by zero.
            var untouched = ProfileMath.Smooth(raw, 0);
            Eq(30m, untouched[2], "zero smoothing leaves the ladder alone");

            // Smoothing conserves roughly the total: it spreads volume, it does not create it.
            var sum = 0m;
            foreach (var v in smoothed) sum += v;
            True(sum <= 30m, "smoothing does not manufacture volume, got " + sum);
        }

        private static void HvnOutlierFilter()
        {
            Section("HVN outlier filter");

            // A dominant node at 100.75 and a small bump at 103.00. The bump is a genuine local
            // maximum, so only the OUTLIER test can reject it -- which is the point of the test.
            var ladder = new Dictionary<decimal, decimal>();
            Fill(ladder, 100.00m, new[] { 20m, 60m, 140m, 200m, 140m, 60m, 20m });
            Fill(ladder, 102.00m, new[] { 5m, 5m, 5m, 5m });
            Fill(ladder, 103.00m, new[] { 8m, 20m, 30m, 20m, 8m });
            Fill(ladder, 104.25m, new[] { 5m, 5m, 5m });

            var strict = new HvnSettings
            {
                SmoothTicks = 1,
                PeakWindowTicks = 3,
                NodePeakPct = 0.70m,
                ShelfEdgePct = 0.50m,
                MaxShelfTicks = 100
            };

            var kept = ProfileMath.ExtractShelves(ladder, Tick, strict);

            Eq(1, kept.Count, "the squint test throws the small node away");
            True(kept[0].Contains(100.75m), "the surviving shelf holds the POC");

            // Same data, loosened: the bump must come back. Without this half, a filter that
            // rejected everything would pass the half above.
            var loose = new HvnSettings
            {
                SmoothTicks = 1,
                PeakWindowTicks = 3,
                NodePeakPct = 0.10m,
                ShelfEdgePct = 0.50m,
                MaxShelfTicks = 100
            };

            var all = ProfileMath.ExtractShelves(ladder, Tick, loose);
            True(all.Count >= 2, "loosening the outlier test admits the small node, got " + all.Count);

            // 103.50, not 103.00: the bump's ladder is {8,20,30,20,8} from 103.00, so its peak
            // bin is the third one. The shelf centres on the peak, not on where the node starts.
            var foundBump = false;
            foreach (var s in all) if (s.Contains(103.50m)) foundBump = true;
            True(foundBump, "the small node is the one that came back");

            // And the dominant node is still there, so loosening added a shelf rather than
            // shifting the one that was already right.
            var stillHavePoc = false;
            foreach (var s in all) if (s.Contains(100.75m)) stillHavePoc = true;
            True(stillHavePoc, "loosening does not lose the POC shelf");
        }

        private static void ShelfWidthCap()
        {
            Section("shelf width cap");

            // A perfectly flat ladder: the walk-out would run the whole range without the cap.
            var flat = new Dictionary<decimal, decimal>();
            var bars = new decimal[400];
            for (var i = 0; i < bars.Length; i++) bars[i] = 100m;
            Fill(flat, 100.00m, bars);

            var s = new HvnSettings
            {
                SmoothTicks = 1,
                PeakWindowTicks = 2,
                NodePeakPct = 0.70m,
                ShelfEdgePct = 0.50m,
                MaxShelfTicks = 40
            };

            var shelves = ProfileMath.ExtractShelves(flat, Tick, s);
            True(shelves.Count > 0, "a flat ladder still yields a shelf");

            foreach (var shelf in shelves)
            {
                var width = (shelf.Top - shelf.Bottom) / Tick;
                True(width <= 41m, "shelf width capped, got " + width + " ticks");
            }
        }

        private static void DoubleDistribution()
        {
            Section("double distribution");

            var s = new HvnSettings
            {
                SmoothTicks = 1,
                PeakWindowTicks = 3,
                NodePeakPct = 0.70m,
                ShelfEdgePct = 0.50m,
                MaxShelfTicks = 100,
                LvnValleyPct = 0.30m
            };

            // Two equal peaks with a real hole between them.
            var split = new Dictionary<decimal, decimal>();
            Fill(split, 100.00m, new[] { 40m, 120m, 200m, 120m, 40m });
            Fill(split, 101.25m, new[] { 4m, 2m, 2m, 4m });
            Fill(split, 102.25m, new[] { 40m, 120m, 200m, 120m, 40m });

            var two = ProfileMath.ExtractShelves(split, Tick, s);
            Eq(2, two.Count, "two shelves out of a split profile");
            True(ProfileMath.IsDoubleDistribution(split, Tick, s, two[0], two[1]),
                 "a deep valley is a double distribution");

            // Same two peaks, but the trough between them is full. Not a double distribution:
            // that is one wide auction, and treating it as two invents a level.
            var filled = new Dictionary<decimal, decimal>();
            Fill(filled, 100.00m, new[] { 40m, 120m, 200m, 120m, 40m });
            Fill(filled, 101.25m, new[] { 120m, 110m, 110m, 120m });
            Fill(filled, 102.25m, new[] { 40m, 120m, 200m, 120m, 40m });

            var peaks = ProfileMath.ExtractShelves(filled, Tick, s);
            if (peaks.Count >= 2)
                False(ProfileMath.IsDoubleDistribution(filled, Tick, s, peaks[0], peaks[1]),
                      "a full trough is not a double distribution");
            else
                True(true, "a full trough merged into one shelf, which is also correct");
        }

        #endregion

        #region Sessions and clock

        private static void TradeDates()
        {
            Section("trade dates");

            var cfg = new SessionConfig();

            // Monday 2026-09-07 is Labor Day but the arithmetic is calendar-only, so use a
            // plain week: Tue 2026-09-08 onward.
            var tue = new DateTime(2026, 9, 8);

            Eq(tue, SessionScan.TradeDateOf(tue.AddHours(9), cfg).Value,
               "09:00 belongs to its own day");

            Eq(tue.AddDays(1), SessionScan.TradeDateOf(tue.AddHours(17), cfg).Value,
               "17:00 opens the NEXT trade date");

            Eq(tue.AddDays(1), SessionScan.TradeDateOf(tue.AddHours(23), cfg).Value,
               "23:00 still belongs to tomorrow");

            True(SessionScan.TradeDateOf(tue.AddHours(16).AddMinutes(30), cfg) == null,
                 "the maintenance break belongs to no trade date");

            // Friday 17:00 would be Saturday; it must roll to Monday, not invent a weekend.
            var fri = new DateTime(2026, 9, 11);
            Eq(new DateTime(2026, 9, 14), SessionScan.TradeDateOf(fri.AddHours(17), cfg).Value,
               "Friday evening rolls to Monday");
        }

        private static void WindowOverlap()
        {
            Section("window overlap");

            var rthStart = new TimeSpan(8, 30, 0);
            var rthEnd = new TimeSpan(15, 0, 0);

            var hour = TimeSpan.FromHours(1);

            // The case the overlap test exists for: an hourly bar stamped 08:00 runs into the
            // cash open. Testing only its stamp throws away the busiest half hour of the day.
            True(SessionScan.Overlaps(new TimeSpan(8, 0, 0), hour, rthStart, rthEnd),
                 "the 08:00 hourly bar touches RTH");

            False(SessionScan.Contains(new TimeSpan(8, 0, 0), hour, rthStart, rthEnd),
                  "but it does not lie wholly inside");

            True(SessionScan.Contains(new TimeSpan(9, 0, 0), hour, rthStart, rthEnd),
                 "the 09:00 hourly bar does");

            False(SessionScan.Overlaps(new TimeSpan(6, 0, 0), hour, rthStart, rthEnd),
                  "06:00 is nowhere near it");

            // Wrap-aware: the overnight window crosses midnight.
            var onStart = new TimeSpan(17, 0, 0);
            var onEnd = new TimeSpan(8, 30, 0);

            True(SessionScan.InWindow(new TimeSpan(2, 0, 0), onStart, onEnd),
                 "02:00 is inside a wrapping overnight window");
            False(SessionScan.InWindow(new TimeSpan(12, 0, 0), onStart, onEnd),
                  "midday is not");

            // A window shorter than the bar cannot have its own profile.
            False(SessionScan.Resolvable(TimeSpan.FromDays(1), rthStart, rthEnd),
                  "a daily bar cannot resolve a 6.5 hour session");
            True(SessionScan.Resolvable(TimeSpan.FromMinutes(5), rthStart, rthEnd),
                 "a 5m bar can");
        }

        private static void BarClockResolution()
        {
            Section("bar clock");

            var zone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

            // Three weeks of 30-minute bars with the 16:00-17:00 halt genuinely empty and real
            // weekend gaps, so both the halt and the weekend signals have something to find.
            var local = MakeBars(new DateTime(2026, 8, 17), 15, zone, false);
            var utc = MakeBars(new DateTime(2026, 8, 17), 15, zone, true);

            var localCtx = BarClockContext.Create("Central Standard Time", BarClock.Auto, local,
                                                  default(DateTime), default(DateTime), 16,
                                                  TimeSpan.FromMinutes(30));

            True(localCtx.Valid, "local-stamped bars settle: " + localCtx.Error);
            Eq(BarClock.AlreadyLocal, localCtx.Clock, "local stamps read as local (" + localCtx.How + ")");

            var utcCtx = BarClockContext.Create("Central Standard Time", BarClock.Auto, utc,
                                                default(DateTime), default(DateTime), 16,
                                                TimeSpan.FromMinutes(30));

            True(utcCtx.Valid, "UTC-stamped bars settle: " + utcCtx.Error);
            Eq(BarClock.Utc, utcCtx.Clock, "UTC stamps read as UTC (" + utcCtx.How + ")");

            // The documented failure mode: 15 is the cash close, not the halt. With the wrong
            // halt hour the halt signal must not claim a resolution.
            var wrongHalt = BarClockContext.Create("Central Standard Time", BarClock.Auto, local,
                                                   default(DateTime), default(DateTime), 15,
                                                   TimeSpan.FromMinutes(30));

            True(!wrongHalt.Valid || wrongHalt.Clock == BarClock.AlreadyLocal,
                 "halt hour 15 never resolves to the WRONG answer");

            // Set by hand always wins and never consults the evidence.
            var forced = BarClockContext.Create("Central Standard Time", BarClock.Utc, local,
                                                default(DateTime), default(DateTime), 16,
                                                TimeSpan.FromMinutes(30));
            Eq(BarClock.Utc, forced.Clock, "a hand-set clock is obeyed");

            // A bad zone id is an error, never a silent fallback to some default zone.
            var bad = BarClockContext.Create("Nowhere/Nothing", BarClock.Auto, local,
                                             default(DateTime), default(DateTime), 16,
                                             TimeSpan.FromMinutes(30));
            False(bad.Valid, "an unknown time zone fails loudly");

            // Bar size from the median gap.
            var size = BarMath.Duration(local, 300);
            Eq(30.0, Math.Round(size.TotalMinutes), "median bar size is 30 minutes");
        }

        #endregion

        #region Absorption

        private static void Percentiles()
        {
            Section("percentiles");

            var v = new List<decimal>();
            for (var i = 1; i <= 11; i++) v.Add(i);

            Eq(1m, Stats.Percentile(v, 0m), "p0 is the minimum");
            Eq(11m, Stats.Percentile(v, 100m), "p100 is the maximum");
            Eq(6m, Stats.Percentile(v, 50m), "p50 of 1..11 is 6");
            Eq(2m, Stats.Percentile(v, 10m), "p10 of 1..11 is 2");

            // Unsorted input must give the same answer as sorted.
            var shuffled = new List<decimal> { 7m, 1m, 11m, 3m, 9m, 5m, 2m, 10m, 4m, 8m, 6m };
            Eq(6m, Stats.Percentile(shuffled, 50m), "percentile sorts its input");

            Eq(0m, Stats.Percentile(new List<decimal>(), 50m), "empty is zero, not a crash");
        }

        private static void TapeSideMatching()
        {
            Section("tape side matching");

            // Support is absorption of aggressive SELLING. Getting this backwards produces a
            // tool that fires on continuation and calls it a fade, so both sides are asserted.
            True(TapeAbsorption.SideMatches(Aggressor.Sell, TestSide.SupportLong),
                 "a long wants sell-side aggressors");
            False(TapeAbsorption.SideMatches(Aggressor.Buy, TestSide.SupportLong),
                  "a long does NOT want buy-side aggressors");

            True(TapeAbsorption.SideMatches(Aggressor.Buy, TestSide.ResistanceShort),
                 "a short wants buy-side aggressors");
            False(TapeAbsorption.SideMatches(Aggressor.Sell, TestSide.ResistanceShort),
                  "a short does NOT want sell-side aggressors");

            var rules = new AbsorptionRules { SizeFloor = 100m, ZoneBufferTicks = 8 };
            var zone = new Zone { Bottom = 100m, Top = 101m, Poc = 100.5m, Side = TestSide.SupportLong };
            zone.State = SignalState.Armed;

            var good = new TradeSnapshot
            {
                Volume = 250m, LastPrice = 100.5m, Direction = Aggressor.Sell,
                Time = new DateTime(2026, 9, 8, 9, 0, 0)
            };

            True(TapeAbsorption.Qualifies(good, zone, Tick, rules), "a 250 lot in the zone qualifies");

            var small = good; small.Volume = 99m;
            False(TapeAbsorption.Qualifies(small, zone, Tick, rules), "under the floor does not");

            var away = good; away.LastPrice = 110m;
            False(TapeAbsorption.Qualifies(away, zone, Tick, rules),
                  "absorption without location is noise");

            // Inside the buffer but outside the shelf still counts: 8 ticks is 2 points.
            var edge = good; edge.LastPrice = 101m + Tick * 6;
            True(TapeAbsorption.Qualifies(edge, zone, Tick, rules), "the zone buffer is honoured");

            var far = good; far.LastPrice = 101m + Tick * 12;
            False(TapeAbsorption.Qualifies(far, zone, Tick, rules), "but it is not unlimited");

            var spent = new Zone
            {
                Bottom = 100m, Top = 101m, Poc = 100.5m, Side = TestSide.SupportLong,
                TraversalCount = 3, State = SignalState.Armed
            };
            False(TapeAbsorption.Qualifies(good, spent, Tick, rules), "a spent zone takes nothing");
        }

        private static void TradeIdentity()
        {
            Section("cumulative trade identity");

            var t0 = new DateTime(2026, 9, 8, 9, 30, 0);

            var a = new TradeSnapshot { Time = t0, FirstPrice = 100m, Volume = 50m, Direction = Aggressor.Sell };
            var grown = new TradeSnapshot { Time = t0, FirstPrice = 100m, Volume = 400m, Direction = Aggressor.Sell };

            True(a.SameTradeAs(grown), "the same trade re-delivered larger is still the same trade");

            var later = new TradeSnapshot { Time = t0.AddMilliseconds(1), FirstPrice = 100m, Volume = 50m, Direction = Aggressor.Sell };
            False(a.SameTradeAs(later), "a different start time is a different trade");

            var elsewhere = new TradeSnapshot { Time = t0, FirstPrice = 101m, Volume = 50m, Direction = Aggressor.Sell };
            False(a.SameTradeAs(elsewhere), "a different start price is a different trade");

            var other = new TradeSnapshot { Time = t0, FirstPrice = 100m, Volume = 50m, Direction = Aggressor.Buy };
            False(a.SameTradeAs(other), "a different direction is a different trade");
        }

        private static void DisplacementResolution()
        {
            Section("displacement");

            var rules = new AbsorptionRules { MaxDisplacementTicks = 6, DisplacementWindowMs = 2000 };
            var t0 = new DateTime(2026, 9, 8, 9, 30, 0);

            // Absorbed: aggressive selling, price goes nowhere.
            var watch = new DisplacementWatch();
            var zone = new Zone { Bottom = 100m, Top = 101m, Side = TestSide.SupportLong };

            watch.Add(new AbsorptionEvent { Time = t0, Price = 100.5m, Direction = Aggressor.Sell }, zone, rules);

            watch.NotePrint(100.25m);
            watch.NotePrint(100.5m);

            Eq(0, watch.Resolve(t0.AddMilliseconds(1500), Tick, rules).Count,
               "nothing resolves before the window closes");

            var absorbed = watch.Resolve(t0.AddMilliseconds(2001), Tick, rules);
            Eq(1, absorbed.Count, "one lot two ticks lower was absorbed");
            Eq(1m, absorbed[0].Key.DisplacementTicks, "displacement measured in ticks");

            // Not absorbed: the same print, but price runs.
            var runner = new DisplacementWatch();
            runner.Add(new AbsorptionEvent { Time = t0, Price = 100.5m, Direction = Aggressor.Sell }, zone, rules);
            runner.NotePrint(98.0m);

            Eq(0, runner.Resolve(t0.AddMilliseconds(2001), Tick, rules).Count,
               "ten ticks of follow-through is initiative, not absorption");

            // Price going the WRONG way must not net off a later push.
            Eq(0m, TapeAbsorption.Displacement(100.5m, 102m, Aggressor.Sell, Tick),
               "adverse movement is zero displacement, never negative");
            Eq(6m, TapeAbsorption.Displacement(100.5m, 99m, Aggressor.Sell, Tick),
               "sell displacement is measured downward");
            Eq(6m, TapeAbsorption.Displacement(100.5m, 102m, Aggressor.Buy, Tick),
               "buy displacement is measured upward");
        }

        private static void ClusterScoring()
        {
            Section("cluster scoring");

            var rules = new ClusterRules
            {
                DeltaLookback = 20, DeltaPercentile = 10m,
                ExtremeTicks = 8, ExtremePct = 30m,
                MinWickTicks = 6, ClosePosPct = 50m, MinScore = 3
            };

            var zone = new Zone { Bottom = 100m, Top = 101m, Poc = 100.5m, Side = TestSide.SupportLong };

            var priors = new List<decimal>();
            for (var i = 0; i < 20; i++) priors.Add(i - 10);

            // The b-shape: a big negative delta outlier, size stacked at the low, a long lower
            // wick, and a close back up in the top half.
            var b = new BarFacts
            {
                Open = 101.0m, High = 101.25m, Low = 99.00m, Close = 101.00m,
                Volume = 1000m, Delta = -900m,
                ExtremeVolume = 500m, ExtremeDelta = -400m,
                MaxVolumePrice = 99.25m
            };

            var score = ClusterAbsorption.Score(b, priors, TestSide.SupportLong, zone, Tick, rules);

            True(score.DeltaOutlier, "-900 against a -10..9 distribution is an outlier");
            True(score.ExtremeConcentration, "half the volume at the low with negative delta");
            True(score.Wick, "nine-tick lower wick");
            True(score.ClosesBackInside, "closes in the top half and back inside the zone");
            Eq(4, score.Total, "all four tests pass on a textbook b-shape");

            // A nothing bar must score low. Without this, a scorer that always returned 4 would
            // pass the block above.
            var dull = new BarFacts
            {
                Open = 100.5m, High = 100.75m, Low = 100.25m, Close = 100.5m,
                Volume = 300m, Delta = 2m,
                ExtremeVolume = 20m, ExtremeDelta = 5m,
                MaxVolumePrice = 100.5m
            };

            var dullScore = ClusterAbsorption.Score(dull, priors, TestSide.SupportLong, zone, Tick, rules);
            True(dullScore.Total < rules.MinScore, "an ordinary bar does not trigger, got " + dullScore.Total);

            // Delta the WRONG way at the low is not absorption. This is the sign test.
            var wrongDelta = b;
            wrongDelta.ExtremeDelta = 400m;
            var wrongScore = ClusterAbsorption.Score(wrongDelta, priors, TestSide.SupportLong, zone, Tick, rules);
            False(wrongScore.ExtremeConcentration, "positive delta at a low is not absorption of selling");

            // Close-back-inside has two halves and both must be able to fail on their own.
            //
            // Half one: the bar closed AT its low. Whatever happened during it, the rejection
            // did not hold, and a wick with no close behind it is a bar that got saved by the
            // clock rather than by a buyer.
            var closedAtLow = b;
            closedAtLow.Close = 99.00m;
            var atLow = ClusterAbsorption.Score(closedAtLow, priors, TestSide.SupportLong, zone, Tick, rules);
            False(atLow.ClosesBackInside, "closing at the low is not closing back inside");
            Eq(0m, atLow.ClosePosPct, "close position is measured from the tested extreme");

            // Half two: the bar closed in the upper half of its OWN range but still below the
            // shelf. Recovering off the low while remaining under the level is not a level
            // holding, and scoring it as one is how a fade gets taken beneath broken support.
            var closedBelowZone = new BarFacts
            {
                Open = 100.5m, High = 100.00m, Low = 99.00m, Close = 99.75m,
                Volume = 1000m, Delta = -900m,
                ExtremeVolume = 500m, ExtremeDelta = -400m,
                MaxVolumePrice = 99.25m
            };

            var belowZone = ClusterAbsorption.Score(closedBelowZone, priors, TestSide.SupportLong,
                                                    zone, Tick, rules);
            True(belowZone.ClosePosPct >= rules.ClosePosPct, "it did close in the upper half");
            False(belowZone.ClosesBackInside, "but below the shelf, so it did not close back inside");

            // The p-shape at a high, so the short side is exercised too.
            var pShape = new BarFacts
            {
                Open = 100.0m, High = 102.00m, Low = 99.75m, Close = 100.00m,
                Volume = 1000m, Delta = 900m,
                ExtremeVolume = 500m, ExtremeDelta = 400m,
                MaxVolumePrice = 101.75m
            };

            var high = new Zone { Bottom = 100m, Top = 101m, Poc = 100.5m, Side = TestSide.ResistanceShort };
            var pScore = ClusterAbsorption.Score(pShape, priors, TestSide.ResistanceShort, high, Tick, rules);

            True(pScore.Total >= rules.MinScore, "the p-shape triggers a short, got " + pScore.Total);
        }

        #endregion

        #region Gates and state

        private static void OneTimeframingGuard()
        {
            Section("one-timeframing guard");

            var rules = new StateRules { OtfBars = 5, OtfGuardOn = true };

            var up = new List<BarFacts>();
            for (var i = 0; i < 10; i++)
                up.Add(new BarFacts { Open = 100 + i, High = 100.5m + i, Low = 99.5m + i, Close = 101.6m + i });

            Eq(1, SignalGate.OneTimeframing(up, 5), "five closes above the prior high is up-OTF");

            True(SignalGate.BlockedByOtf(TestSide.ResistanceShort, 1, rules),
                 "never fade the freight train up");
            False(SignalGate.BlockedByOtf(TestSide.SupportLong, 1, rules),
                  "going WITH it is fine");

            var chop = new List<BarFacts>();
            for (var i = 0; i < 10; i++)
                chop.Add(new BarFacts { Open = 100m, High = 101m, Low = 99m, Close = 100m });

            Eq(0, SignalGate.OneTimeframing(chop, 5), "chop is not one-timeframing");
            False(SignalGate.BlockedByOtf(TestSide.ResistanceShort, 0, rules), "chop blocks nothing");

            var down = new List<BarFacts>();
            for (var i = 0; i < 10; i++)
                down.Add(new BarFacts { Open = 100 - i, High = 100.5m - i, Low = 99.5m - i, Close = 98.4m - i });

            Eq(-1, SignalGate.OneTimeframing(down, 5), "five closes below the prior low is down-OTF");
            True(SignalGate.BlockedByOtf(TestSide.SupportLong, -1, rules), "do not catch that knife");

            var off = new StateRules { OtfBars = 5, OtfGuardOn = false };
            False(SignalGate.BlockedByOtf(TestSide.ResistanceShort, 1, off), "the guard is toggleable");
        }

        private static void AlertWindow()
        {
            Section("A+ window");

            var rules = new StateRules
            {
                WindowStart = new TimeSpan(8, 30, 0),
                WindowEnd = new TimeSpan(10, 30, 0),
                AllowAllHours = false,
                SuppressFriday = true
            };

            var tue = new DateTime(2026, 9, 8);

            True(SignalGate.Allowed(tue.AddHours(9), rules), "09:00 Tuesday is in the window");
            False(SignalGate.Allowed(tue.AddHours(8).AddMinutes(29), rules), "08:29 is not");
            False(SignalGate.Allowed(tue.AddHours(11), rules), "11:00 is not");
            False(SignalGate.Allowed(tue.AddHours(14), rules), "the afternoon is not");

            var fri = new DateTime(2026, 9, 11);
            False(SignalGate.Allowed(fri.AddHours(9), rules), "Friday is suppressed by the personal rule");

            rules.SuppressFriday = false;
            True(SignalGate.Allowed(fri.AddHours(9), rules), "and the rule is toggleable");

            rules.AllowAllHours = true;
            True(SignalGate.Allowed(tue.AddHours(14), rules), "all-hours opens the afternoon");
        }

        private static void NakedPocs()
        {
            Section("naked POCs");

            var t = new NakedPocTracker();
            t.Add(100m, new DateTime(2026, 9, 8), 10, 10);

            True(t.IsNaked(100m), "a fresh POC starts naked");

            // A bar BEFORE it was born cannot test it.
            t.NoteBar(5, 101m, 99m);
            True(t.IsNaked(100m), "an earlier bar does not test it");

            // A later bar that misses it does not test it either.
            t.NoteBar(11, 105m, 103m);
            True(t.IsNaked(100m), "a later bar that misses does not test it");

            t.NoteBar(12, 101m, 99m);
            False(t.IsNaked(100m), "a later bar covering the price tests it");

            // One POC per session, and the ring is capped.
            var ring = new NakedPocTracker();
            for (var i = 0; i < 15; i++)
                ring.Add(100m + i, new DateTime(2026, 9, 1).AddDays(i), i, 10);

            Eq(10, ring.All.Count, "the ring keeps only the last 10 sessions");

            ring.Add(999m, new DateTime(2026, 9, 14), 20, 10);
            var dupes = 0;
            foreach (var p in ring.All) if (p.TradeDate == new DateTime(2026, 9, 14)) dupes++;
            True(dupes <= 1, "one POC per session");
        }

        private static void ZoneTraversal()
        {
            Section("zone traversal");

            var z = new Zone { Bottom = 100m, Top = 101m, Poc = 100.5m };

            // Touching is the setup, not the failure of one.
            ZoneMaintenance.NoteBar(z, 102m, 100.5m);
            Eq(0, z.TraversalCount, "closing inside is not a traversal");

            // The half that matters: price came from below and STOPPED in the zone. It has to
            // close beyond the far edge, not merely past the near one -- a bar that pushes into
            // the shelf and stalls there is the zone working, and counting it as a traversal
            // would use the zone up on exactly the behaviour that proves it is real.
            ZoneMaintenance.NoteBar(z, 99m, 100.5m);
            Eq(0, z.TraversalCount, "opening below and closing INSIDE is not a traversal");

            ZoneMaintenance.NoteBar(z, 102m, 100.5m);
            Eq(0, z.TraversalCount, "opening above and closing INSIDE is not a traversal either");

            ZoneMaintenance.NoteBar(z, 99m, 102m);
            Eq(1, z.TraversalCount, "opening below and closing above is");

            ZoneMaintenance.NoteBar(z, 102m, 99m);
            Eq(2, z.TraversalCount, "and so is the other way");

            False(z.Spent, "two traversals is not spent");

            ZoneMaintenance.NoteBar(z, 99m, 102m);
            True(z.Spent, "three is");

            ZoneMaintenance.NoteBar(z, 99m, 102m);
            Eq(3, z.TraversalCount, "a spent zone stops counting");

            Eq(TestSide.SupportLong, ZoneMaintenance.SideFor(z, 105m),
               "approaching from above makes it support");
            Eq(TestSide.ResistanceShort, ZoneMaintenance.SideFor(z, 95m),
               "approaching from below makes it resistance");
        }

        private static void ZoneBuilding()
        {
            Section("zone building");

            var rth = new SessionProfile { TradeDate = new DateTime(2026, 9, 8) };
            rth.NoteBar(0, 102m, 99m);
            Fill(rth.VolByPrice, 100.00m, new[] { 20m, 60m, 140m, 200m, 140m, 60m, 20m });
            rth.TotalVol = 640m;
            rth.ComputePoc();

            var composite = new Dictionary<decimal, decimal>();
            Fill(composite, 100.00m, new[] { 200m, 600m, 1400m, 2000m, 1400m, 600m, 200m });

            var naked = new NakedPocTracker();
            naked.Add(rth.Poc, rth.TradeDate, 0, 10);

            var input = new ZoneBuildInput
            {
                PriorRth = rth,
                Composite = composite,
                Naked = naked,
                TickSize = Tick,
                Hvn = new HvnSettings { SmoothTicks = 1, PeakWindowTicks = 3 },
                MaxZones = 4,
                StartBar = 5,
                BornSession = new DateTime(2026, 9, 9)
            };

            var zones = ZoneBuilder.Build(input);

            True(zones.Count >= 1, "a session POC always yields a zone");
            Eq(1, zones[0].Rank, "a naked POC on a composite shelf is rank 1");
            True(zones[0].Contains(rth.Poc), "the rank 1 zone holds the POC");
            Eq(5, zones[0].StartBar, "zones carry their start bar for rendering");

            // The same POC, no longer naked, must drop a rank rather than vanish.
            var tested = new NakedPocTracker();
            tested.Add(rth.Poc, rth.TradeDate, 0, 10);
            tested.NoteBar(1, rth.Poc + 1m, rth.Poc - 1m);

            input.Naked = tested;
            var afterTest = ZoneBuilder.Build(input);

            True(afterTest.Count >= 1, "a tested POC still yields a zone");
            Eq(2, afterTest[0].Rank, "a tested prior-RTH POC is rank 2");

            // The cap is enforced.
            var wide = new Dictionary<decimal, decimal>();
            for (var i = 0; i < 12; i++)
                Fill(wide, 100m + i * 5m, new[] { 100m, 300m, 500m, 300m, 100m });

            input.Composite = wide;
            input.MaxZones = 3;
            Eq(3, ZoneBuilder.Build(input).Count, "the zone cap is enforced");

            // Rank ties break toward price, so the cap keeps the levels the day can reach.
            input.MaxZones = 4;
            input.SessionOpen = 100.5m;

            var capped = ZoneBuilder.Build(input);
            foreach (var z in capped)
                True(Math.Abs(z.Poc - 100.5m) < 40m,
                     "the cap keeps near zones over far ones, kept " + z.Poc);

            // The distance filter dims what the day cannot reach. 1.5 x 10 ADR = 15 points.
            input.MaxZones = 12;
            input.Adr = 10m;
            input.DistanceAdrMult = 1.5m;

            var withDistance = ZoneBuilder.Build(input);

            var sawFar = false;
            var sawNear = false;

            foreach (var z in withDistance)
            {
                if (z.TooFar) sawFar = true;
                else sawNear = true;

                // The flag has to track the actual distance, not just be set somewhere.
                var gap = z.Poc > 100.5m ? z.Bottom - 100.5m : 100.5m - z.Top;
                if (gap < 0m) gap = 0m;

                Eq(gap > 15m, z.TooFar, "too-far flag matches the distance for " + z.Poc);
            }

            True(sawFar, "a zone more than 1.5 ADR away is flagged too far");
            True(sawNear, "and a near one is not");

            // Zero ADR must disable the filter rather than flag everything.
            input.Adr = 0m;
            foreach (var z in ZoneBuilder.Build(input))
                False(z.TooFar, "no ADR means no distance filter, not everything filtered");
        }

        private static void StateMachine()
        {
            Section("state machine");

            var engine = new SignalEngine();
            engine.Rules.ClockBars = 3;
            engine.Rules.BreakBufferTicks = 3;
            engine.Rules.ZoneBufferTicks = 2;
            engine.Rules.OtfGuardOn = false;

            var tue = new DateTime(2026, 9, 8, 9, 0, 0);

            // Arms when price reaches it. The zone is 100-101 with a 2-tick buffer, so a bar
            // whose low is 104 is unambiguously nowhere near it.
            var z = NewZone();
            engine.Advance(z, 10, Bar(105m, 105.5m, 104m, 104.5m), tue, Tick, 0);
            Eq(SignalState.Dormant, z.State, "a zone price never reached stays dormant");

            engine.Advance(z, 11, Bar(102m, 102.5m, 100.5m, 101m), tue, Tick, 0);
            Eq(SignalState.Armed, z.State, "arms when the low reaches the shelf");
            Eq(TestSide.SupportLong, z.Side, "approached from above, so it is a long");

            // Triggers on a promotion.
            z.NoteCluster(100.25m, 100.75m);
            True(engine.Promote(z, new AbsorptionEvent { Price = 100.5m, Bar = 12 }, 12),
                 "an armed zone promotes");
            Eq(SignalState.Triggered, z.State, "promoted to triggered");

            // Confirms on a delta flip.
            engine.Advance(z, 13, Bar(101m, 102m, 100.75m, 101.75m, 400m), tue, Tick, 0);
            Eq(SignalState.Confirmed, z.State, "a positive delta bar confirms a long");

            // Expiry: triggered, then nothing happens for the whole clock.
            var e = NewZone();
            var expiring = new SignalEngine();
            expiring.Rules.ClockBars = 3;
            expiring.Rules.ZoneBufferTicks = 2;
            expiring.Rules.OtfGuardOn = false;

            expiring.Advance(e, 11, Bar(102m, 102.5m, 100.5m, 101m), tue, Tick, 0);
            e.NoteCluster(100.25m, 100.75m);
            expiring.Promote(e, new AbsorptionEvent { Price = 100.5m, Bar = 12 }, 12);

            for (var bar = 13; bar <= 15; bar++)
                expiring.Advance(e, bar, Bar(101m, 101.2m, 100.6m, 100.9m, -50m), tue, Tick, 0);

            Eq(SignalState.Expired, e.State, "absorption that keeps absorbing expires");

            // Broken: the cluster extreme trades through.
            var b = NewZone();
            var breaking = new SignalEngine();
            breaking.Rules.BreakBufferTicks = 3;
            breaking.Rules.ZoneBufferTicks = 2;
            breaking.Rules.OtfGuardOn = false;

            breaking.Advance(b, 11, Bar(102m, 102.5m, 100.5m, 101m), tue, Tick, 0);
            b.NoteCluster(100.25m, 100.75m);
            breaking.Promote(b, new AbsorptionEvent { Price = 100.5m, Bar = 12 }, 12);

            breaking.Advance(b, 13, Bar(100.5m, 100.6m, 99m, 99.25m, -600m), tue, Tick, 0);
            Eq(SignalState.Broken, b.State, "closing below the cluster low breaks the zone");

            // And it never comes back that session.
            breaking.Advance(b, 14, Bar(100m, 101m, 100.4m, 100.9m, 500m), tue, Tick, 0);
            Eq(SignalState.Broken, b.State, "a broken zone does not re-arm");

            // The OTF guard blocks arming the wrong way.
            var guarded = NewZone();
            var guardEngine = new SignalEngine();
            guardEngine.Rules.ZoneBufferTicks = 2;
            guardEngine.Rules.OtfGuardOn = true;

            guardEngine.Advance(guarded, 11, Bar(99m, 100.5m, 98.5m, 100.2m), tue, Tick, 1);
            Eq(SignalState.Dormant, guarded.State, "up-OTF blocks arming a short");
        }

        private static void CvdDivergence()
        {
            Section("CVD divergence");

            var engine = new SignalEngine();
            engine.Rules.ClockBars = 5;
            engine.Rules.ZoneBufferTicks = 2;
            engine.Rules.OtfGuardOn = false;

            var tue = new DateTime(2026, 9, 8, 9, 0, 0);
            var z = NewZone();

            // First visit: heavy selling into the shelf drives CVD down hard.
            engine.NoteBarDelta(-800m);
            engine.Advance(z, 10, Bar(102m, 102.2m, 100.40m, 100.8m, -800m), tue, Tick, 0);
            Eq(SignalState.Armed, z.State, "armed on the first visit");

            // Leave the zone entirely, so the second visit is a separate touch rather than a
            // continuation of the first.
            engine.NoteBarDelta(300m);
            engine.Advance(z, 11, Bar(101m, 104m, 103m, 103.5m, 300m), tue, Tick, 0);
            Eq(SignalState.Dormant, z.State, "leaving the zone disarms it");

            // Second visit: equal low, but far less selling to get there.
            engine.NoteBarDelta(-50m);
            engine.Advance(z, 12, Bar(103m, 103.2m, 100.40m, 100.9m, -50m), tue, Tick, 0);
            Eq(SignalState.Armed, z.State, "re-arms on the second visit");

            z.NoteCluster(100.25m, 100.75m);
            engine.Promote(z, new AbsorptionEvent { Price = 100.5m, Bar = 12 }, 12);

            // A negative-delta bar, so the flip test cannot be what confirms it -- only the
            // divergence can.
            engine.NoteBarDelta(-20m);
            engine.Advance(z, 13, Bar(100.9m, 101.4m, 100.45m, 101.2m, -20m), tue, Tick, 0);

            Eq(SignalState.Confirmed, z.State,
               "equal low on a higher CVD low confirms without a delta flip");
        }

        #endregion

        #region CSV

        private static void CsvRoundTrip()
        {
            Section("CSV log");

            var e = new Episode
            {
                StartedLocal = new DateTime(2026, 9, 8, 9, 42, 17),
                ZoneKind = "NakedPoc",
                Rank = 1,
                Side = TestSide.SupportLong,
                ArrivalAtrMult = 0.62m,
                LargestTrade = 340m,
                StackedEvents = 2,
                Path = EventPath.Tape,
                TouchDelta = -820m,
                ClosePosPct = 71.5m,
                DisplacementTicks = 2m,
                Confirmed = true,
                ResolvedInClock = true,
                TriggerBar = 120
            };

            var row = AnchorLog.Format(e);
            var cols = row.Split(',');
            var headers = AnchorLog.Header.Split(',');

            Eq(headers.Length, cols.Length, "the row has as many columns as the header");
            Eq(16, headers.Length, "sixteen calibration columns");

            Eq("2026-09-08", cols[0], "date");
            Eq("09:42:17", cols[1], "Houston time, no zone suffix because there is only one zone");
            Eq("NakedPoc", cols[2], "zone kind");
            Eq("1", cols[3], "rank");
            Eq("long", cols[4], "side");
            Eq("0.62", cols[5], "arrival in ADRs");
            Eq("340", cols[6], "largest trade -- the column that sets SizeFloor at p95");
            Eq("2", cols[7], "stacked events");
            Eq("tape", cols[8], "path");
            Eq("1", cols[11], "confirmed");
            Eq("0", cols[12], "not expired");

            // Commas in a field would silently shift every column after it.
            var nasty = new Episode
            {
                StartedLocal = new DateTime(2026, 9, 8, 9, 0, 0),
                ZoneKind = "weird,kind\"with\nbreaks",
                Side = TestSide.ResistanceShort
            };

            var nastyCols = AnchorLog.Format(nasty).Split(',');
            Eq(headers.Length, nastyCols.Length, "a field with a comma cannot shift the columns");
            Eq("short", nastyCols[4], "short side");

            // The header is written exactly once, and rows survive a reopen.
            var path = Path.Combine(Path.GetTempPath(), "anchor_test_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var log = new AnchorLog(path);
                True(log.Write(e), "first row written: " + log.LastError);

                var second = Clone(e);
                second.StartedLocal = e.StartedLocal.AddMinutes(25);
                True(log.Write(second), "second row written");

                // ATAS replays history on every load: the same episode arrives again as a new
                // object, and must not become a second row.
                var reopened = new AnchorLog(path);
                False(reopened.Write(Clone(e)), "a replayed episode already in the file is not appended");

                // Near-duplicates that are really different events must still get through,
                // or the guard is eating calibration rows.
                var otherSide = Clone(e);
                otherSide.Side = TestSide.ResistanceShort;
                True(reopened.Write(otherSide), "same second, other side is a different episode");

                var otherSecond = Clone(e);
                otherSecond.StartedLocal = e.StartedLocal.AddSeconds(1);
                True(reopened.Write(otherSecond), "one second later is a different episode");

                // Outcome columns can differ on a replay without it being a new event.
                var replayedResult = Clone(e);
                replayedResult.Confirmed = false;
                replayedResult.Broken = true;
                replayedResult.Rank = 3;
                False(reopened.Write(replayedResult), "a replay with a different result is still the same episode");

                var lines = File.ReadAllLines(path);
                Eq(5, lines.Length, "header plus four distinct rows");
                Eq(AnchorLog.Header, lines[0], "header is first");

                var headerCount = 0;
                foreach (var line in lines) if (line == AnchorLog.Header) headerCount++;
                Eq(1, headerCount, "the header is written exactly once");

                False(log.Write(e), "an already-written episode is not written twice");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }

            // An unwritable path must cost a row, never throw.
            var broken = new AnchorLog(Path.Combine("Z:\\nope\\nowhere", "x.csv"));
            var threw = false;
            try { broken.Write(Clone(e)); } catch { threw = true; }
            False(threw, "a failed log write never throws");
        }

        private static Episode Clone(Episode e)
        {
            return new Episode
            {
                StartedLocal = e.StartedLocal, ZoneKind = e.ZoneKind, Rank = e.Rank, Side = e.Side,
                ArrivalAtrMult = e.ArrivalAtrMult, LargestTrade = e.LargestTrade,
                StackedEvents = e.StackedEvents, Path = e.Path, TouchDelta = e.TouchDelta,
                ClosePosPct = e.ClosePosPct, DisplacementTicks = e.DisplacementTicks,
                Confirmed = e.Confirmed, ResolvedInClock = e.ResolvedInClock, TriggerBar = e.TriggerBar
            };
        }

        #endregion

        #region Helpers

        private static Zone NewZone()
        {
            return new Zone
            {
                Bottom = 100m, Top = 101m, Poc = 100.5m, Rank = 1,
                Kind = ZoneKind.PriorRthPoc, State = SignalState.Dormant
            };
        }

        private static BarFacts Bar(decimal o, decimal h, decimal l, decimal c)
        {
            return Bar(o, h, l, c, 0m);
        }

        private static BarFacts Bar(decimal o, decimal h, decimal l, decimal c, decimal delta)
        {
            return new BarFacts { Open = o, High = h, Low = l, Close = c, Delta = delta, Volume = 1000m };
        }

        private static void AddLadder(SessionProfile p, decimal start, decimal[] volumes)
        {
            for (var i = 0; i < volumes.Length; i++) p.Add(start + Tick * i, volumes[i]);
        }

        private static void Fill(IDictionary<decimal, decimal> ladder, decimal start, decimal[] volumes)
        {
            for (var i = 0; i < volumes.Length; i++)
            {
                var price = start + Tick * i;
                decimal v;
                ladder.TryGetValue(price, out v);
                ladder[price] = v + volumes[i];
            }
        }

        /// <summary>
        /// 30-minute bars across whole weeks, with the 16:00-17:00 halt genuinely empty and the
        /// weekend genuinely missing -- so both the halt signal and the weekend-gap signal have
        /// real evidence to find rather than a synthetic pattern that only one of them fits.
        /// </summary>
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
                    var local = day.AddMinutes(30 * slot);

                    // The halt: 16:00-17:00 has no bars, every weekday.
                    if (local.Hour == 16) continue;

                    // Sunday only trades from 17:00.
                    if (local.DayOfWeek == DayOfWeek.Sunday && local.Hour < 17) continue;

                    // Friday stops at the 16:00 close.
                    if (local.DayOfWeek == DayOfWeek.Friday && local.Hour >= 16) continue;

                    var stamp = local;
                    if (asUtc)
                    {
                        try
                        {
                            stamp = TimeZoneInfo.ConvertTimeToUtc(
                                DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
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

        private static void Eq(decimal expected, decimal actual, string what)
        {
            _checks++;
            if (expected == actual) return;

            _failures++;
            Console.WriteLine("   FAIL  " + what + " (expected " +
                              expected.ToString(CultureInfo.InvariantCulture) + ", got " +
                              actual.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private static void Eq(int expected, int actual, string what)
        {
            Eq((decimal)expected, (decimal)actual, what);
        }

        private static void Eq(double expected, double actual, string what)
        {
            Eq((decimal)expected, (decimal)actual, what);
        }

        private static void Eq(object expected, object actual, string what)
        {
            _checks++;
            if (Equals(expected, actual)) return;

            _failures++;
            Console.WriteLine("   FAIL  " + what + " (expected " + expected + ", got " + actual + ")");
        }

        #endregion
    }
}
