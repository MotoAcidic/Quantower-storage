using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OceansPivotDecoder;

namespace OceansPivotDecoder.Tests
{
    /// <summary>
    /// Math harness. deploy.ps1 runs this first and refuses to deploy on a failure.
    ///
    /// The bars below are built so that a BROKEN session filter fails the test rather than
    /// quietly passing it: the overnight bars sit well outside the regular-hours range, and there
    /// are bars immediately before 08:30 and exactly at 15:00 whose prices would change the
    /// answer if either window boundary were off by one bar.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            ValidationCase();
            ConventionSeparation();
            SessionFiltering();
            WindowBoundaries();
            ShorthandResolution();
            DirectionOverride();
            MatchVerdicts();
            ZoneScoring();
            ProfileMaths();
            PoorExtremes();
            AbsorptionStates();
            ScoreDeduplication();
            GapsAndPierces();
            BaseRates();
            TradePlans();
            BarClockResolution();
            Timeframes();
            TradeablePrices();
            ReactionOutcomes();
            CsvRoundTrip();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "ALL PASS"
                : _failures + " FAILURE(S)");

            return _failures == 0 ? 0 : 1;
        }

        #region The validation case

        private static void ValidationCase()
        {
            Section("validation case");

            string report;
            var ok = PivotMath.SelfTest(out report);

            foreach (var line in report.Split('\n')) Console.WriteLine("    " + line.TrimEnd());

            if (!ok)
            {
                Console.WriteLine("    -> the self-test the indicator runs at construction FAILED");
                _failures++;
            }
        }

        /// <summary>
        /// The whole decoder rests on the classic and PP-anchored third levels being far enough
        /// apart to tell apart. If they ever converge, a NO MATCH stops meaning anything.
        /// </summary>
        private static void ConventionSeparation()
        {
            Section("R3/S3 conventions");

            var h = PivotMath.CaseHigh;
            var l = PivotMath.CaseLow;
            var c = PivotMath.CaseClose;
            var pp = PivotMath.Pp(h, l, c);

            Eq("standard S3 == narrow S3", PivotMath.S3Standard(pp, h, l), PivotMath.S3Narrow(pp, h, l));
            Eq("standard R3 == narrow R3", PivotMath.R3Standard(pp, h, l), PivotMath.R3Narrow(pp, h, l));

            var gap = Math.Abs(PivotMath.S3Standard(pp, h, l) - PivotMath.S3Wide(pp, h, l));
            True("classic and wide S3 are far apart (" + PivotMath.Round2(gap) + " pts)", gap > 100m);

            // "Both" must collapse to one line, not two identical ones stacked on a pixel.
            var both = PivotMath.BuildFloor("RTH", h, l, c, R3S3Variant.Both, false);
            var s3Count = 0;
            foreach (var level in both) if (level.Name.StartsWith("S3")) s3Count++;

            Eq("Both emits one S3", s3Count, 1);
            True("...named S3sn", both.Exists(x => x.Name == "S3sn"));
        }

        #endregion

        #region Session scanning

        private static void SessionFiltering()
        {
            Section("session filtering");

            var clock = Clock();
            var days = SessionScan.Scan(PriorDayBars(), clock, new SessionConfig());

            Eq("one trade date found", days.Count, 1);

            var day = days[0];
            Eq("trade date", day.TradeDate, new DateTime(2026, 9, 2));

            // If the RTH filter leaked a single overnight bar, these three move.
            Eq("RTH high", day.Rth.High, 29211.75m);
            Eq("RTH low", day.Rth.Low, 29017.25m);
            Eq("RTH close", day.Rth.Close, 29186.25m);

            Eq("ETH high", day.Eth.High, 29350m);
            Eq("ETH low", day.Eth.Low, 28900m);
            Eq("ETH close", day.Eth.Close, 29300m);

            // End to end: bars in, session filter, formula out, validation case reproduced.
            var pp = PivotMath.Pp(day.Rth.High, day.Rth.Low, day.Rth.Close);
            Eq("PP off the scanned RTH window", PivotMath.Round2(pp), 29138.42m);
            Eq("Camarilla R4 off the scanned RTH window",
               PivotMath.Round2(PivotMath.CamR(day.Rth.High, day.Rth.Low, day.Rth.Close, 4)), 29293.23m);

            // The 24h window must NOT reproduce it -- otherwise the test proves nothing about
            // which window was actually used.
            var ppEth = PivotMath.Pp(day.Eth.High, day.Eth.Low, day.Eth.Close);
            True("the 24h window gives a different PP (" + PivotMath.Round2(ppEth) + ")",
                 PivotMath.Round2(ppEth) != 29138.42m);
        }

        /// <summary>
        /// The two boundaries an off-by-one would land on: the bar just before the open and the
        /// bar stamped exactly at the close.
        /// </summary>
        private static void WindowBoundaries()
        {
            Section("window boundaries");

            var cfg = new SessionConfig();

            True("08:29 is outside RTH",
                 !SessionScan.InWindow(new TimeSpan(8, 29, 0), cfg.RthStart, cfg.RthEnd));
            True("08:30 is inside RTH",
                 SessionScan.InWindow(new TimeSpan(8, 30, 0), cfg.RthStart, cfg.RthEnd));
            True("14:59 is inside RTH",
                 SessionScan.InWindow(new TimeSpan(14, 59, 0), cfg.RthStart, cfg.RthEnd));
            True("15:00 is outside RTH -- the window is half open",
                 !SessionScan.InWindow(new TimeSpan(15, 0, 0), cfg.RthStart, cfg.RthEnd));

            // Trade-date assignment across the Globex break and the weekend.
            Eq("17:00 Tue belongs to Wed",
               SessionScan.TradeDateOf(new DateTime(2026, 9, 1, 17, 0, 0), cfg), new DateTime(2026, 9, 2));
            Eq("09:00 Wed belongs to Wed",
               SessionScan.TradeDateOf(new DateTime(2026, 9, 2, 9, 0, 0), cfg), new DateTime(2026, 9, 2));
            True("16:30 falls in the daily break and belongs to no session",
               SessionScan.TradeDateOf(new DateTime(2026, 9, 2, 16, 30, 0), cfg) == null);
            Eq("17:00 Sunday belongs to Monday",
               SessionScan.TradeDateOf(new DateTime(2026, 9, 6, 17, 0, 0), cfg), new DateTime(2026, 9, 7));

            // A wrapping window, for anyone who retimes RTH to an overnight session.
            True("wrapping window contains 23:00",
                 SessionScan.InWindow(new TimeSpan(23, 0, 0), new TimeSpan(20, 0, 0), new TimeSpan(3, 0, 0)));
            True("wrapping window contains 01:00",
                 SessionScan.InWindow(new TimeSpan(1, 0, 0), new TimeSpan(20, 0, 0), new TimeSpan(3, 0, 0)));
            True("wrapping window excludes 12:00",
                 !SessionScan.InWindow(new TimeSpan(12, 0, 0), new TimeSpan(20, 0, 0), new TimeSpan(3, 0, 0)));
        }

        #endregion

        #region Caller levels

        private static void ShorthandResolution()
        {
            Section("caller level parsing");

            var levels = CallerParse.Parse("28872, 872, 293, 29293.25", 29100m, 500m);

            Eq("four tokens", levels.Count, 4);
            Eq("full price passes through", levels[0].Price, 28872m);
            Eq("872 resolves down to 28872", levels[1].Price, 28872m);
            Eq("293 resolves up to 29293", levels[2].Price, 29293m);
            Eq("a four-digit-plus token is never shorthand", levels[3].Price, 29293.25m);

            // The three candidates sit 1000 apart with the reference between two of them, so the
            // nearest is ALWAYS within 500. At the default range nothing is ever rejected -- the
            // setting is a tightening knob, not a routine rejection path, and it earns its keep
            // when a shorthand lands far enough from spot to be genuinely ambiguous.
            var edge = CallerParse.Parse("500", 29100m, 500m);
            True("at the default range the nearest candidate always resolves", edge[0].Resolved);
            Eq("...to the nearer thousand", edge[0].Price, 29500m);

            // Tightened, the same token must come back unresolved with a reason, never placed on
            // a guess: a mis-resolved caller level would make the entire match table lie.
            var far = CallerParse.Parse("500", 29100m, 200m);
            True("tightened, a 400 pt shorthand is unresolved", !far[0].Resolved);
            True("...and says why", !string.IsNullOrEmpty(far[0].Problem));

            var noRef = CallerParse.Parse("872", 0m, 500m);
            True("shorthand with no reference price is unresolved", !noRef[0].Resolved);

            var junk = CallerParse.Parse("abc", 29100m, 500m);
            True("junk is unresolved", !junk[0].Resolved);

            Eq("last three digits of 28872", CallerParse.LastThree(28872m), "872");
            Eq("last three digits of 29000", CallerParse.LastThree(29000m), "000");
        }

        private static void DirectionOverride()
        {
            Section("direction override");

            var levels = CallerParse.Parse("872, 28293", 29100m, 500m);
            CallerParse.ApplyDirections(levels, "872:L,293:S");

            Eq("shorthand token matched", levels[0].Direction, CallDirection.Long);
            True("...and flagged as overridden", levels[0].DirectionOverridden);
            Eq("full price matched on its last three digits", levels[1].Direction, CallDirection.Short);

            var untouched = CallerParse.Parse("872", 29100m, 500m);
            CallerParse.ApplyDirections(untouched, "");
            Eq("no spec leaves direction unknown", untouched[0].Direction, CallDirection.Unknown);
            True("...and not flagged", !untouched[0].DirectionOverridden);
        }

        private static void MatchVerdicts()
        {
            Section("matching");

            var h = PivotMath.CaseHigh;
            var l = PivotMath.CaseLow;
            var c = PivotMath.CaseClose;

            var levels = new List<PivotLevel>();
            levels.AddRange(PivotMath.BuildFloor("RTH", h, l, c, R3S3Variant.Both, true));
            levels.AddRange(PivotMath.BuildCamarilla("RTH", h, l, c));

            // 28872 against classic S3 = 28870.58: 1.42 pts, the difference the caller's numbers
            // have already been showing.
            var callers = CallerParse.Parse("28872, 29293", 29100m, 500m);
            var reports = Matcher.Build(callers, levels, 3.0m, 10.0m);

            var first = reports[0].Best;
            Eq("nearest family", first.Family, "FLOOR-RTH");
            Eq("nearest level", first.Level.Name, "S3sn");
            Eq("delta is signed, computed minus caller", PivotMath.Round2(first.Delta), -1.42m);
            Eq("verdict", first.Verdict, Verdict.Exact);

            var second = reports[1].Best;
            Eq("Camarilla R4 is the nearest to 29293", second.Level.Name, "cR4");
            Eq("...and exact", second.Verdict, Verdict.Exact);

            True("matches are sorted nearest first",
                 reports[0].Matches[0].AbsDelta <= reports[0].Matches[1].AbsDelta);

            Eq("boundary: exactly the tolerance is EXACT", Matcher.Judge(3.0m, 3.0m, 10.0m), Verdict.Exact);
            Eq("just past it is NEAR", Matcher.Judge(3.01m, 3.0m, 10.0m), Verdict.Near);
            Eq("exactly the loose tolerance is NEAR", Matcher.Judge(10.0m, 3.0m, 10.0m), Verdict.Near);
            Eq("past that is NO MATCH", Matcher.Judge(10.01m, 3.0m, 10.0m), Verdict.NoMatch);
        }

        /// <summary>
        /// Zones are scored by what they are MADE OF. The weights are the argument: a naked POC
        /// or an unfinished extreme is unfinished business the market left on the table and
        /// counts double; a floor pivot is arithmetic on three numbers and counts one.
        /// </summary>
        private static void ZoneScoring()
        {
            Section("zone clustering");

            var weights = new ZoneWeights();

            // Four Camarilla levels bunched inside 8 points: the densest band on a chart, and
            // four rearrangements of one H/L/C triplet.
            var ladder = new List<PivotLevel>
            {
                L("CAM-RTH", "cR1", 30450m, LevelFamily.Camarilla),
                L("CAM-RTH", "cR2", 30453m, LevelFamily.Camarilla),
                L("CAM-RTH", "cR3", 30456m, LevelFamily.Camarilla),
                L("CAM-RTH", "cR4", 30458m, LevelFamily.Camarilla)
            };

            var dense = ZoneClusterer.Cluster(ladder, 10m, weights)[0];
            Eq("four members", dense.Members.Count, 4);
            Eq("scores 4 at weight 1.0", dense.Score, 4.0m);

            // Two members, but one of them is a level price has never traded back through.
            var magnet = new List<PivotLevel>
            {
                L("FLOOR-RTH", "S2", 29220m, LevelFamily.Floor),
                L("NPOC", "nPOC", 29224m, LevelFamily.NakedPoc)
            };

            var strong = ZoneClusterer.Cluster(magnet, 10m, weights)[0];
            Eq("floor 1.0 + naked POC 2.0", strong.Score, 3.0m);

            var poor = ZoneClusterer.Cluster(new List<PivotLevel>
            {
                L("REF-RTH", "pRTH-H", 30260m, LevelFamily.PriorHlc),
                L("POOR-RTH", "POOR-H", 30263m, LevelFamily.PoorExtreme),
                L("VP-NY", "NY-POC", 30265m, LevelFamily.SessionPoc)
            }, 10m, weights)[0];

            Eq("1.0 + 2.0 + 1.5", poor.Score, 4.5m);
            Eq("score prints without a trailing zero", poor.ScoreText, "x4.5");
            Eq("a whole score prints clean", strong.ScoreText, "x3");

            // The band is capped overall, not per member, or the evenly spaced Camarilla ladder
            // chains into one two-hundred-point "zone" that is not a level at all.
            var spread = new List<PivotLevel>
            {
                L("CAM-RTH", "cR1", 30400m, LevelFamily.Camarilla),
                L("CAM-RTH", "cR2", 30410m, LevelFamily.Camarilla),
                L("CAM-RTH", "cR3", 30420m, LevelFamily.Camarilla),
                L("CAM-RTH", "cR4", 30430m, LevelFamily.Camarilla)
            };

            var chained = ZoneClusterer.Cluster(spread, 6m, weights);
            True("evenly spaced levels do not chain", chained.Count > 1);
            foreach (var zone in chained) True("...and no band exceeds the tolerance", zone.Width <= 6m);

            // Selection: sides, proximity, cap, ranking.
            var all = new List<PivotLevel>();
            all.AddRange(magnet);                                             // 29220ish, below
            all.AddRange(ladder);                                             // 30450ish, above
            // Beyond the render range but inside the ladder range, and the strongest thing on
            // the board: the case the ladder exists for.
            all.Add(L("NPOC", "nPOC", 31300m, LevelFamily.NakedPoc));
            all.Add(L("POOR-RTH", "POOR-H", 31303m, LevelFamily.PoorExtreme));
            all.Add(L("REF-ETH", "pETH-H", 31306m, LevelFamily.PriorHlc));
            all.Add(L("FLOOR-ETH", "R1", 30100m, LevelFamily.Floor));         // below, in range
            all.Add(L("VP-Asia", "Asia-POC", 30103m, LevelFamily.SessionPoc));
            all.Add(L("FLOOR-RTH", "R2", 30600m, LevelFamily.Floor));          // above, weaker
            all.Add(L("VP-London", "London-POC", 30603m, LevelFamily.SessionPoc));

            var selected = ZoneClusterer.Select(ZoneClusterer.Cluster(all, 10m, weights),
                                                30275m, 400m, 3);

            foreach (var zone in selected)
                True("nothing beyond the render range is selected",
                     Math.Abs(zone.Center - 30275m) <= 400m);

            var longs = 0;
            var shorts = 0;
            foreach (var zone in selected) { if (zone.IsLong) longs++; else shorts++; }

            Eq("one band below price in range", longs, 1);
            Eq("two bands above price in range", shorts, 2);

            foreach (var zone in selected)
                if (!zone.IsLong && zone.Rank == 1)
                    Eq("the stronger band above ranks first", zone.Score, 4.0m);

            // The cap is per side, not overall.
            var capped = ZoneClusterer.Select(ZoneClusterer.Cluster(all, 10m, weights),
                                              30275m, 400m, 1);
            Eq("capped to one a side", capped.Count, 2);

            // The panel lists further than the chart shades, and both must agree on what S2
            // means: rank once over the wider set, then narrow. Renumbering the drawn subset
            // would give one band two names depending on where you read it.
            var wide = ZoneClusterer.Select(ZoneClusterer.Cluster(all, 10m, weights),
                                              30275m, 1500m, 4);
            var shaded = ZoneClusterer.WithinRange(wide, 30275m, 400m, 3);

            True("the ladder reaches further than the chart", wide.Count > shaded.Count);

            foreach (var zone in shaded)
            {
                var matched = false;

                foreach (var listed in wide)
                    if (listed.Key == zone.Key)
                    {
                        matched = true;
                        Eq("a shaded band keeps its ladder rank", zone.Rank, listed.Rank);
                    }

                True("every shaded band is in the ladder", matched);
            }

            // That band scores 5.0 (naked 2.0 + poor extreme 2.0 + prior high 1.0), outranking
            // everything above price -- and it was invisible under the proximity filter alone.
            var strongestAbove = 0;
            foreach (var zone in wide) if (!zone.IsLong && zone.Rank == 1) strongestAbove++;

            Eq("one rank-1 above price", strongestAbove, 1);

            foreach (var zone in wide)
                if (!zone.IsLong && zone.Rank == 1)
                {
                    Eq("...and it is the far magnet the chart cannot show", zone.Score, 5.0m);
                    True("...which sits outside the render range",
                         Math.Abs(zone.Center - 30275m) > 400m);
                }
        }

        /// <summary>
        /// Yesterday's RTH high and yesterday's 24h high are very often the same price. Counting
        /// both inflated the band's score for what is one fact.
        /// </summary>
        private static void ScoreDeduplication()
        {
            Section("score deduplication");

            var weights = new ZoneWeights();

            var duplicate = ZoneClusterer.Cluster(new List<PivotLevel>
            {
                L("REF-RTH", "pRTH-H", 30260m, LevelFamily.PriorHlc),
                L("REF-ETH", "pETH-H", 30260m, LevelFamily.PriorHlc)
            }, 6m, weights)[0];

            Eq("the same price twice in one family counts once", duplicate.Score, 1.0m);
            Eq("...both are still listed as members", duplicate.Members.Count, 2);

            // Two genuinely different prices from one family is a wider shelf, not a duplicate.
            var shelf = ZoneClusterer.Cluster(new List<PivotLevel>
            {
                L("REF-RTH", "pRTH-H", 30260m, LevelFamily.PriorHlc),
                L("REF-ETH", "pETH-H", 30263m, LevelFamily.PriorHlc)
            }, 6m, weights)[0];

            Eq("two different prices in one family both count", shelf.Score, 2.0m);

            // A Camarilla level landing exactly on a floor pivot is two families, so two facts.
            var crossFamily = ZoneClusterer.Cluster(new List<PivotLevel>
            {
                L("FLOOR-RTH", "R2", 30260m, LevelFamily.Floor),
                L("CAM-RTH", "cR3", 30260m, LevelFamily.Camarilla)
            }, 6m, weights)[0];

            Eq("same price, different families, still two", crossFamily.Score, 2.0m);

            var magnet = ZoneClusterer.Cluster(new List<PivotLevel>
            {
                L("FLOOR-RTH", "R2", 30260m, LevelFamily.Floor),
                L("NPOC", "nPOC", 30262m, LevelFamily.NakedPoc)
            }, 6m, weights)[0];

            True("a naked POC flags the band as a magnet", magnet.HasMagnet);
            True("a plain pivot band does not", !crossFamily.HasMagnet);
        }

        /// <summary>
        /// The bug this pins: testing "has price been back through" against a later session's
        /// HIGH and LOW marks a level as traded when the session gapped clean over it. That
        /// retires exactly the magnets that matter, because a gap is what leaves one naked.
        /// </summary>
        private static void GapsAndPierces()
        {
            Section("gaps and pierces");

            // Two bars with a gap between 29900 and 30100. Nothing traded in that hole.
            var bars = new List<BarFacts>
            {
                Bar(29900m, 29800m, 29880m, 0m, 100m, 10m),
                Bar(30200m, 30100m, 30150m, 0m, 100m, 10m)
            };

            True("a price inside the gap was never traded",
                 !Pierce.Any(bars, 0, 1, 30000m, 0m, true));

            True("a price inside a bar was traded",
                 Pierce.Any(bars, 0, 1, 29850m, 0m, true));

            True("a price at a bar edge counts", Pierce.Any(bars, 0, 1, 30100m, 0m, true));

            // The session range spans the gap, which is exactly what the old test looked at.
            var spannedLow = 29800m;
            var spannedHigh = 30200m;

            True("...even though the session range spans it",
                 spannedLow <= 30000m && 30000m <= spannedHigh);

            // Repair needs clearing by a margin, not merely touching.
            True("a high is not repaired by a touch",
                 !Pierce.Any(bars, 0, 1, 30200m, 4m, true));

            True("...but is by clearing it", Pierce.Any(bars, 0, 1, 30190m, 4m, true));

            True("a low repairs downward", Pierce.Any(bars, 0, 1, 29810m, 4m, false));
            True("...and not upward", !Pierce.Any(bars, 0, 1, 29790m, 4m, false));

            True("an empty range finds nothing", !Pierce.Any(bars, 5, 9, 29850m, 0m, true));
        }

        private static void BaseRates()
        {
            Section("base rates");

            var cfg = new StatsConfig { RejectPts = 30m, AcceptPts = 15m, MinSample = 8, StrongScore = 4m };

            const decimal low = 29600m;
            const decimal high = 29610m;

            // Approaches support, turns, runs 30 up. Held.
            var held = new List<BarFacts>
            {
                Bar(29650m, 29620m, 29630m, 0m, 100m, 10m),
                Bar(29615m, 29602m, 29608m, 0m, 100m, 10m),
                Bar(29645m, 29606m, 29642m, 0m, 100m, 10m)
            };

            var a = BaseRateEngine.Measure(held, 0, 2, low, high, true, 29650m, 29602m, cfg);
            Eq("rejected", a.Result, TouchResult.Rejected);
            True("...and it bottom-ticked the session", a.MarkedExtreme);

            // Goes through and keeps going. Broken.
            var broken = new List<BarFacts>
            {
                Bar(29650m, 29620m, 29630m, 0m, 100m, 10m),
                Bar(29615m, 29602m, 29608m, 0m, 100m, 10m),
                Bar(29606m, 29580m, 29584m, 0m, 100m, 10m)
            };

            Eq("accepted through",
               BaseRateEngine.Measure(broken, 0, 2, low, high, true, 29650m, 29580m, cfg).Result,
               TouchResult.Accepted);

            // Never got there.
            var far = new List<BarFacts> { Bar(29900m, 29850m, 29880m, 0m, 100m, 10m) };
            var untouched = BaseRateEngine.Measure(far, 0, 0, low, high, true, 29900m, 29850m, cfg);
            Eq("not touched", untouched.Result, TouchResult.NotTouched);
            True("...and did not mark the extreme", !untouched.MarkedExtreme);

            // Touched and the session ended before it resolved.
            var open = new List<BarFacts> { Bar(29615m, 29602m, 29608m, 0m, 100m, 10m) };
            Eq("unresolved", BaseRateEngine.Measure(open, 0, 0, low, high, true, 29615m, 29602m, cfg).Result,
               TouchResult.Unresolved);

            // One bar that did both. Counting it as a hold would bias the rate upward every time.
            var both = new List<BarFacts>
            {
                Bar(29645m, 29580m, 29600m, 0m, 100m, 10m)
            };

            Eq("a bar that ran both ways counts as broken, not held",
               BaseRateEngine.Measure(both, 0, 0, low, high, true, 29645m, 29580m, cfg).Result,
               TouchResult.Accepted);

            // The short side mirrors: a resistance band holds when price comes back DOWN.
            var shortHeld = new List<BarFacts>
            {
                Bar(29590m, 29560m, 29580m, 0m, 100m, 10m),
                Bar(29608m, 29595m, 29604m, 0m, 100m, 10m),
                Bar(29606m, 29565m, 29568m, 0m, 100m, 10m)
            };

            Eq("resistance held", BaseRateEngine.Measure(shortHeld, 0, 2, low, high, false,
                                                         29608m, 29560m, cfg).Result,
               TouchResult.Rejected);

            // Summarising: buckets, and the small-sample floor.
            var events = new List<TouchEvent>();

            for (var i = 0; i < 6; i++) events.Add(Event(5m, true, TouchResult.Rejected, i < 3));
            for (var i = 0; i < 4; i++) events.Add(Event(5m, true, TouchResult.Accepted, false));
            for (var i = 0; i < 3; i++) events.Add(Event(2m, false, TouchResult.Rejected, false));
            events.Add(Event(5m, false, TouchResult.Rejected, false));

            var strong = BaseRateEngine.Summarise(events, 5m, true, cfg);
            Eq("ten touches in the strong-with-magnet bucket", strong.Touches, 10);
            Eq("six held", strong.Rejections, 6);
            Eq("60 percent", strong.RejectPercent, 60m);
            True("sample clears the floor", strong.Sufficient);
            Eq("top-ticked three of ten bands", strong.ExtremePercent, 30m);
            True("the quote carries its sample", strong.Text.Contains("6/10"));

            var thin = BaseRateEngine.Summarise(events, 2m, false, cfg);
            Eq("three touches in the weak bucket", thin.Touches, 3);
            True("below the floor, no rate is quoted", !thin.Sufficient);
            True("...it reports the sample instead", thin.Text.Contains("too few"));
            True("...and never prints a percentage", !thin.Text.Contains("%"));

            // Bucketing is coarse on purpose: a score of 9 shares the strong-with-magnet bucket
            // with a score of 5. Splitting finer halves the sample and starts reporting noise.
            var alsoStrong = BaseRateEngine.Summarise(events, 9m, true, cfg);
            Eq("a much stronger band uses the same bucket", alsoStrong.Touches, 10);

            // Weak AND magnet has no history at all in this fixture.
            var missing = BaseRateEngine.Summarise(events, 2m, true, cfg);
            Eq("a bucket with no history reports nothing", missing.Touches, 0);
            True("...rather than a rate", !missing.Sufficient);
        }

        /// <summary>
        /// The bug behind "why does the 15 minute look different from the 1 hour".
        ///
        /// A bar carries one timestamp, its open. Testing only that stamp discards every bar that
        /// starts before a window and runs into it -- so an 08:30 RTH open threw away the whole
        /// 1-hour bar stamped 08:00, losing 08:30-09:00: the cash open, and very often the session
        /// high or low. Two timeframes then disagreed for a reason that had nothing to do with the
        /// market.
        /// </summary>
        private static void Timeframes()
        {
            Section("timeframes");

            var rthStart = new TimeSpan(8, 30, 0);
            var rthEnd = new TimeSpan(15, 0, 0);

            var hour = TimeSpan.FromHours(1);
            var quarter = TimeSpan.FromMinutes(15);

            // The bar at the heart of it.
            True("stamp-only test drops the 1h bar at 08:00",
                 !SessionScan.InWindow(new TimeSpan(8, 0, 0), rthStart, rthEnd));

            True("span test keeps it, because it runs into the session",
                 SessionScan.Overlaps(new TimeSpan(8, 0, 0), hour, rthStart, rthEnd));

            True("the 15m bar at 08:15 still does not reach the open",
                 !SessionScan.Overlaps(new TimeSpan(8, 15, 0), quarter, rthStart, rthEnd));

            True("the 15m bar at 08:30 does",
                 SessionScan.Overlaps(new TimeSpan(8, 30, 0), quarter, rthStart, rthEnd));

            // The close boundary must NOT gain a bar: 15:00 is outside a half-open window.
            True("the 1h bar at 15:00 stays out",
                 !SessionScan.Overlaps(new TimeSpan(15, 0, 0), hour, rthStart, rthEnd));

            True("the 1h bar at 14:00 stays in",
                 SessionScan.Overlaps(new TimeSpan(14, 0, 0), hour, rthStart, rthEnd));

            True("a bar entirely overnight is still out",
                 !SessionScan.Overlaps(new TimeSpan(2, 0, 0), hour, rthStart, rthEnd));

            // A 4h bar at 04:00 runs 04:00-08:00 and must not be pulled in by the wrap logic.
            var four = TimeSpan.FromHours(4);
            True("a 4h bar ending exactly at the open is out",
                 !SessionScan.Overlaps(new TimeSpan(4, 0, 0), four, rthStart, rthEnd));
            True("a 4h bar at 08:00 is in", SessionScan.Overlaps(new TimeSpan(8, 0, 0), four, rthStart, rthEnd));

            // Asia wraps midnight; the span test has to wrap with it.
            var asiaStart = new TimeSpan(17, 0, 0);
            var asiaEnd = new TimeSpan(2, 0, 0);

            True("a bar at 23:00 is inside Asia", SessionScan.Overlaps(new TimeSpan(23, 0, 0), hour, asiaStart, asiaEnd));
            True("a bar at 01:00 is inside Asia", SessionScan.Overlaps(new TimeSpan(1, 0, 0), hour, asiaStart, asiaEnd));
            True("a bar ending exactly as Asia opens is out",
                 !SessionScan.Overlaps(new TimeSpan(16, 0, 0), hour, asiaStart, asiaEnd));
            True("a 4h bar at 16:00 does run into Asia",
                 SessionScan.Overlaps(new TimeSpan(16, 0, 0), four, asiaStart, asiaEnd));
            True("a bar at 09:00 does not", !SessionScan.Overlaps(new TimeSpan(9, 0, 0), hour, asiaStart, asiaEnd));

            // Windows shorter than a bar cannot be resolved at all, and must not be served as
            // full-day numbers wearing a session label.
            True("1h resolves a 6.5h session", SessionScan.Resolvable(hour, rthStart, rthEnd));
            True("4h resolves it too", SessionScan.Resolvable(four, rthStart, rthEnd));
            True("a daily bar does not",
                 !SessionScan.Resolvable(TimeSpan.FromDays(1), rthStart, rthEnd));
            True("nor a weekly", !SessionScan.Resolvable(TimeSpan.FromDays(7), rthStart, rthEnd));
            True("4h cannot resolve the 6.5h London window either",
                 !SessionScan.Resolvable(TimeSpan.FromHours(7), new TimeSpan(2, 0, 0), new TimeSpan(8, 30, 0)));

            // Bar size from the stamps, not from a timeframe string that range bars do not have.
            var hourly = new FakeBars();
            for (var i = 0; i < 30; i++)
                hourly.Add(new DateTime(2026, 9, 14).AddHours(i), 100m, 101m, 99m, 100m);

            Eq("hourly bars measure as an hour", BarMath.Duration(hourly, 400), hour);
            Eq("...and describe as 1h", BarMath.Describe(hour), "1h");
            Eq("15m describes", BarMath.Describe(quarter), "15m");
            Eq("daily describes", BarMath.Describe(TimeSpan.FromDays(1)), "1d");

            // The median has to survive the overnight break and the weekend, which the mean
            // would not: those gaps are twenty times a bar.
            var gappy = new FakeBars();
            var t = new DateTime(2026, 9, 14, 9, 0, 0);

            for (var i = 0; i < 20; i++) { gappy.Add(t, 100m, 101m, 99m, 100m); t = t.AddHours(1); }
            t = t.AddDays(3);
            for (var i = 0; i < 20; i++) { gappy.Add(t, 100m, 101m, 99m, 100m); t = t.AddHours(1); }

            Eq("a weekend gap does not move the median", BarMath.Duration(gappy, 400), hour);

            // Wholly-inside vs merely overlapping: this is what decides whether a session extreme
            // is beyond question or only bounded.
            True("the 1h bar at 09:00 lies wholly inside RTH",
                 SessionScan.Contains(new TimeSpan(9, 0, 0), hour, rthStart, rthEnd));
            True("the 1h bar at 08:00 does not",
                 !SessionScan.Contains(new TimeSpan(8, 0, 0), hour, rthStart, rthEnd));
            True("the 1h bar at 14:00 does",
                 SessionScan.Contains(new TimeSpan(14, 0, 0), hour, rthStart, rthEnd));
            True("a bar longer than the window never fits",
                 !SessionScan.Contains(new TimeSpan(8, 30, 0), TimeSpan.FromHours(7), rthStart, rthEnd));

            // The doubt is sized, and it is ZERO whenever a clean bar set the extreme -- which is
            // the usual case, and the reason the caution should not be permanent.
            var clean = new Window();
            clean.Add(0, new DateTime(2026, 9, 2, 8, 0, 0), 100m, 104m, 99m, 103m, false);
            clean.Add(1, new DateTime(2026, 9, 2, 9, 0, 0), 103m, 110m, 95m, 108m, true);

            Eq("a clean bar set the high", clean.High, 110m);
            Eq("...so there is nothing to doubt", clean.HighDoubt, 0m);
            Eq("...nor on the low", clean.LowDoubt, 0m);

            var doubtful = new Window();
            doubtful.Add(0, new DateTime(2026, 9, 2, 8, 0, 0), 100m, 112m, 102m, 103m, false);
            doubtful.Add(1, new DateTime(2026, 9, 2, 9, 0, 0), 103m, 110m, 101m, 108m, true);

            Eq("the straddling bar set the high", doubtful.High, 112m);
            True("...so it is flagged", doubtful.HighFromEdge);
            Eq("...bounded by the best clean bar", doubtful.HighDoubt, 2m);
            Eq("...while the low, set by a clean bar, carries none", doubtful.LowDoubt, 0m);

            // A clean bar matching the straddler's extreme removes the doubt entirely.
            var tied = new Window();
            tied.Add(0, new DateTime(2026, 9, 2, 8, 0, 0), 100m, 112m, 99m, 103m, false);
            tied.Add(1, new DateTime(2026, 9, 2, 9, 0, 0), 103m, 112m, 101m, 108m, true);

            Eq("a clean bar reaching the same high settles it", tied.HighDoubt, 0m);

            // The 4-hour case, which is what the suppression exists for. A 6.5h session on 4h
            // bars has both edge bars straddling and at most one bar wholly inside, so the only
            // thing beyond question is a single bar's range.
            var fourHourRth = new Window();
            fourHourRth.Add(0, new DateTime(2026, 9, 2, 5, 0, 0), 29200m, 29505m, 29150m, 29400m, false);
            fourHourRth.Add(1, new DateTime(2026, 9, 2, 9, 0, 0), 29400m, 29473m, 29241.50m, 29300m, true);
            fourHourRth.Add(2, new DateTime(2026, 9, 2, 13, 0, 0), 29300m, 29450m, 29101.75m, 29200m, false);

            Eq("the session low came from a straddling bar", fourHourRth.Low, 29101.75m);
            True("...and is flagged", fourHourRth.LowFromEdge);
            Eq("...bounded only by the single clean bar", fourHourRth.LowDoubt, 139.75m);

            var range = fourHourRth.Range;
            True("the doubt is over a third of the session range",
                 fourHourRth.LowDoubt * 100m / range > 30m);

            True("so the session is not trustworthy at 10%", !fourHourRth.Trustworthy(10m));
            True("...nor at any sane limit", !fourHourRth.Trustworthy(25m));

            // A 1h session with one straddling bar that did NOT set an extreme is fine.
            var hourly15 = new Window();
            hourly15.Add(0, new DateTime(2026, 9, 2, 8, 0, 0), 29200m, 29300m, 29250m, 29280m, false);
            hourly15.Add(1, new DateTime(2026, 9, 2, 9, 0, 0), 29280m, 29505m, 29101.75m, 29400m, true);

            Eq("clean bars set both extremes", hourly15.HighDoubt, 0m);
            True("so it is trustworthy", hourly15.Trustworthy(10m));

            // No clean bar at all can never be trusted, whatever the arithmetic says: there is
            // nothing to bound it with.
            var allEdge = new Window();
            allEdge.Add(0, new DateTime(2026, 9, 2, 8, 0, 0), 29200m, 29300m, 29100m, 29280m, false);
            allEdge.Add(1, new DateTime(2026, 9, 2, 12, 0, 0), 29280m, 29310m, 29090m, 29300m, false);

            True("no bar wholly inside means no measurement", !allEdge.HasInside);
            True("...and never trustworthy", !allEdge.Trustworthy(90m));

            Eq("window length", BarMath.Length(rthStart, rthEnd), TimeSpan.FromHours(6.5));
            Eq("...wrapping", BarMath.Length(asiaStart, asiaEnd), TimeSpan.FromHours(9));
        }

        private static void TradePlans()
        {
            Section("trade plans");

            const decimal tick = 0.25m;

            // Short: entry at the TOP of the band, because that is the tick worth selling.
            var shortPlan = PlanMath.Build(30450m, 30458.33m, true, tick, 8, 30105m, true);

            True("built", shortPlan.Valid);
            Eq("entry snapped to the tick at the top edge", shortPlan.Entry, 30458.25m);
            Eq("stop 8 ticks beyond", shortPlan.Stop, 30460.25m);
            Eq("risk", shortPlan.RiskPts, 2.00m);
            Eq("reward", shortPlan.RewardPts, 353.25m);
            True("R is large because the stop is tight", shortPlan.R > 100m);

            // Long: entry at the BOTTOM edge.
            var longPlan = PlanMath.Build(29612.10m, 29622m, false, tick, 8, 30105m, true);
            Eq("entry snapped at the bottom edge", longPlan.Entry, 29612.00m);
            Eq("stop below", longPlan.Stop, 29610.00m);

            // No opposing band: still a valid entry and stop, but say there is no target.
            var noTarget = PlanMath.Build(30450m, 30458m, true, tick, 8, 0m, false);
            True("valid without a target", noTarget.Valid);
            Eq("...and no R", noTarget.R, 0m);
            True("...and says why", noTarget.Problem.Contains("no band"));

            // A target on the wrong side must not print as a negative R.
            var backwards = PlanMath.Build(30450m, 30458m, true, tick, 8, 30600m, true);
            Eq("a target above a short entry yields no R", backwards.R, 0m);
            True("...and explains itself", backwards.Problem != null);

            var noTick = PlanMath.Build(30450m, 30458m, true, 0m, 8, 30105m, true);
            True("no tick size means no plan", !noTick.Valid);

            var zeroStop = PlanMath.Build(30450m, 30458m, true, tick, 0, 30105m, true);
            True("a zero-tick stop is refused rather than dividing by zero", !zeroStop.Valid);
        }

        private static TouchEvent Event(decimal score, bool magnet, TouchResult result, bool extreme)
        {
            return new TouchEvent
            {
                Score = score,
                HasMagnet = magnet,
                Result = result,
                MarkedExtreme = extreme
            };
        }

        private static void ProfileMaths()
        {
            Section("profile math");

            // Volume deliberately lopsided, so a bug returning the middle of the range fails
            // rather than looking plausible.
            var rows = new List<PriceVolume>
            {
                PV(100m, 10m), PV(101m, 20m), PV(102m, 100m), PV(103m, 30m), PV(104m, 10m)
            };

            var profile = ProfileMath.Build(rows, 70m);

            True("built", profile.Valid);
            Eq("POC is the heaviest row, not the mid-range", profile.Poc, 102m);
            Eq("total volume", profile.TotalVolume, 170m);
            True("value area holds the POC", profile.Val <= 102m && profile.Vah >= 102m);
            True("value area is narrower than the range", profile.Vah - profile.Val < 4m);

            // The pair rule: rows above the POC are 30 + 10 = 40, below are 20 + 10 = 30, so the
            // first expansion takes BOTH rows above. Expanding one row at a time is the obvious
            // implementation and lands on a different edge.
            var pairRule = ProfileMath.Build(new List<PriceVolume>
            {
                PV(100m, 10m), PV(101m, 20m), PV(102m, 100m), PV(103m, 30m), PV(104m, 10m)
            }, 80m);

            Eq("the heavier pair is taken whole", pairRule.Vah, 104m);

            var vwap = ProfileMath.Build(new List<PriceVolume> { PV(100m, 1m), PV(200m, 3m) }, 70m);
            Eq("VWAP is volume weighted, not the midpoint", vwap.Vwap, 175m);

            var empty = ProfileMath.Build(new List<PriceVolume>(), 70m);
            True("no rows yields no profile", !empty.Valid);
            True("...and says why", !string.IsNullOrEmpty(empty.Problem));

            Eq("percentile picks the top of the sample",
               ProfileMath.Percentile(new List<decimal> { 1m, 2m, 3m, 4m, 100m }, 90m), 100m);
            Eq("...and the middle at 50", ProfileMath.Percentile(new List<decimal> { 1m, 2m, 3m }, 50m), 2m);
            Eq("an empty sample is zero, not a crash",
               ProfileMath.Percentile(new List<decimal>(), 90m), 0m);
        }

        /// <summary>
        /// The poor-extreme detector, against the case named in the spec: a high whose own tick
        /// still holds 35% or more of the volume three ticks below never tapered, so the auction
        /// was cut off rather than finished. A clean taper must not flag.
        /// </summary>
        private static void PoorExtremes()
        {
            Section("poor extremes");

            const decimal tick = 0.25m;
            string reason;

            // 40 at the high against 100 three ticks back: 40%, over the 35% floor. POOR.
            var cut = new List<PriceVolume>
            {
                PV(29200.00m, 100m), PV(29200.25m, 90m), PV(29200.50m, 70m), PV(29200.75m, 40m)
            };

            True("a high holding 40% of three ticks back is poor",
                 ProfileMath.IsPoorExtreme(cut, 29200.75m, true, tick, 0.35m, 1, out reason));
            True("...and says why", reason.Contains("no taper"));

            // 20 against 100: 20%, a clean taper. Not poor.
            var tapered = new List<PriceVolume>
            {
                PV(29200.00m, 100m), PV(29200.25m, 70m), PV(29200.50m, 40m), PV(29200.75m, 20m)
            };

            True("a tapered high is not poor",
                 !ProfileMath.IsPoorExtreme(tapered, 29200.75m, true, tick, 0.35m, 1, out reason));

            // Exactly on the threshold counts, or the boundary is undefined.
            var boundary = new List<PriceVolume>
            {
                PV(29200.00m, 100m), PV(29200.25m, 60m), PV(29200.50m, 45m), PV(29200.75m, 35m)
            };

            True("exactly 35% counts as poor",
                 ProfileMath.IsPoorExtreme(boundary, 29200.75m, true, tick, 0.35m, 1, out reason));

            // Lows taper the other way; a sign error here would flag every low ever printed.
            var lowTaper = new List<PriceVolume>
            {
                PV(29200.00m, 20m), PV(29200.25m, 40m), PV(29200.50m, 70m), PV(29200.75m, 100m)
            };

            True("a tapered low is not poor",
                 !ProfileMath.IsPoorExtreme(lowTaper, 29200.00m, false, tick, 0.35m, 1, out reason));

            var lowCut = new List<PriceVolume>
            {
                PV(29200.00m, 40m), PV(29200.25m, 70m), PV(29200.50m, 90m), PV(29200.75m, 100m)
            };

            True("a low holding 40% three ticks up is poor",
                 ProfileMath.IsPoorExtreme(lowCut, 29200.00m, false, tick, 0.35m, 1, out reason));

            // A ledge flags whatever the taper says.
            True("two bars sharing the extreme is a ledge",
                 ProfileMath.IsPoorExtreme(tapered, 29200.75m, true, tick, 0.35m, 2, out reason));
            True("...and says so", reason.Contains("ledge"));

            // No data behind the high is UNKNOWN, and must not be reported as a good high.
            var thin = new List<PriceVolume> { PV(29200.75m, 40m) };
            True("no rows behind the extreme means no call",
                 !ProfileMath.IsPoorExtreme(thin, 29200.75m, true, tick, 0.35m, 1, out reason));
        }

        /// <summary>
        /// The state machine. Each test kills one way of being fooled by a chart that merely
        /// looks like a bounce.
        /// </summary>
        private static void AbsorptionStates()
        {
            Section("absorption");

            const decimal tick = 0.25m;
            const decimal low = 29600m;
            const decimal high = 29610m;

            var cfg = new AbsorptionConfig();

            // Twenty quiet bars set the adaptive bar: average |delta| of 100 x 1.5 = 150.
            var quiet = new List<BarFacts>();
            for (var i = 0; i < 20; i++)
                quiet.Add(Bar(29700m, 29690m, 29695m, i % 2 == 0 ? 100m : -100m, 1000m, 50m));

            Eq("the size bar is adaptive, not hardcoded",
               AbsorptionEngine.DeltaThreshold(quiet, 19, cfg), 150m);

            // Untouched.
            var away = new List<BarFacts>(quiet);
            var untested = AbsorptionEngine.Run(away, 0, away.Count - 1, low, high, true, tick,
                                                150m, 500m, cfg);
            Eq("price never reached the band", untested.State, AbsorptionState.Untested);

            // Sellers hit bids into support, no follow-through, close back above. VALIDATED.
            var held = new List<BarFacts>(quiet);
            held.Add(Bar(29612m, 29601m, 29604m, -400m, 5000m, 900m));  // inside, heavy selling
            held.Add(Bar(29620m, 29606m, 29618m, 50m, 2000m, 100m));    // closes back out above

            var validated = AbsorptionEngine.Run(held, 0, held.Count - 1, low, high, true, tick,
                                                 150m, 500m, cfg);
            Eq("absorbed and rejected", validated.State, AbsorptionState.Validated);
            Eq("...marked up", validated.Marker, "ABS UP");
            True("...big print noticed", validated.ClusterPrint);
            // -350, not -400: the rejection bar wicks back into the band before closing above,
            // so its delta belongs to the test too. Only bars that never touch the band are
            // excluded.
            Eq("...delta accumulated from every bar that touched the band",
               validated.CumulativeDelta, -350m);

            // Same shape, but the delta points the WRONG way: buying into support is not
            // absorption, it is just buying.
            var wrongWay = new List<BarFacts>(quiet);
            wrongWay.Add(Bar(29612m, 29601m, 29604m, 400m, 5000m, 900m));
            wrongWay.Add(Bar(29620m, 29606m, 29618m, 50m, 2000m, 100m));

            Eq("delta pointing out of the band does not validate",
               AbsorptionEngine.Run(wrongWay, 0, wrongWay.Count - 1, low, high, true, tick,
                                    150m, 500m, cfg).State,
               AbsorptionState.Testing);

            // Right delta, right close, but price gave up thirty ticks first. Not held.
            var overshoot = new List<BarFacts>(quiet);
            overshoot.Add(Bar(29612m, 29592m, 29596m, -400m, 5000m, 900m));  // 32 ticks past
            overshoot.Add(Bar(29620m, 29606m, 29618m, 50m, 2000m, 100m));

            Eq("a band that gave up ground first does not validate",
               AbsorptionEngine.Run(overshoot, 0, overshoot.Count - 1, low, high, true, tick,
                                    150m, 500m, cfg).State,
               AbsorptionState.Testing);

            // Size and containment, but no close back out: still being fought over.
            var unresolved = new List<BarFacts>(quiet);
            unresolved.Add(Bar(29612m, 29601m, 29604m, -400m, 5000m, 900m));
            unresolved.Add(Bar(29609m, 29602m, 29607m, -100m, 2000m, 100m));

            Eq("no rejection close leaves it testing",
               AbsorptionEngine.Run(unresolved, 0, unresolved.Count - 1, low, high, true, tick,
                                    150m, 500m, cfg).State,
               AbsorptionState.Testing);

            // Two closes well below the far edge is acceptance. The level is gone.
            var gone = new List<BarFacts>(quiet);
            gone.Add(Bar(29612m, 29601m, 29604m, -400m, 5000m, 900m));
            gone.Add(Bar(29605m, 29590m, 29594m, -300m, 3000m, 100m));
            gone.Add(Bar(29596m, 29585m, 29590m, -200m, 3000m, 100m));

            var failed = AbsorptionEngine.Run(gone, 0, gone.Count - 1, low, high, true, tick,
                                              150m, 500m, cfg);
            Eq("accepted through", failed.State, AbsorptionState.Failed);

            // One close beyond is a probe, not acceptance.
            var probe = new List<BarFacts>(quiet);
            probe.Add(Bar(29612m, 29601m, 29604m, -400m, 5000m, 900m));
            probe.Add(Bar(29605m, 29590m, 29594m, -300m, 3000m, 100m));
            probe.Add(Bar(29608m, 29596m, 29605m, 100m, 3000m, 100m));

            Eq("a single close beyond is not acceptance",
               AbsorptionEngine.Run(probe, 0, probe.Count - 1, low, high, true, tick,
                                    150m, 500m, cfg).State,
               AbsorptionState.Testing);

            // The short side is the mirror: buying lifted into resistance, close back below.
            var shortHeld = new List<BarFacts>(quiet);
            shortHeld.Add(Bar(29609m, 29598m, 29606m, 400m, 5000m, 900m));
            shortHeld.Add(Bar(29604m, 29590m, 29592m, -50m, 2000m, 100m));

            var shortSide = AbsorptionEngine.Run(shortHeld, 0, shortHeld.Count - 1, low, high,
                                                 false, tick, 150m, 500m, cfg);
            Eq("the short side mirrors", shortSide.State, AbsorptionState.Validated);
            Eq("...marked down", shortSide.Marker, "ABS DN");

            // With no delta history there is no bar to clear, and that must read as "cannot
            // validate" rather than "everything qualifies".
            Eq("a zero threshold never validates",
               AbsorptionEngine.Run(held, 0, held.Count - 1, low, high, true, tick,
                                    0m, 500m, cfg).State,
               AbsorptionState.Testing);
        }

        private static PivotLevel L(string group, string name, decimal price, LevelFamily family)
        {
            return PivotMath.Make(group, "RTH", name, price, family, null);
        }

        private static PriceVolume PV(decimal price, decimal volume)
        {
            return new PriceVolume { Price = price, Volume = volume };
        }

        private static BarFacts Bar(decimal high, decimal low, decimal close, decimal delta,
                                    decimal volume, decimal maxLevel)
        {
            return new BarFacts
            {
                High = high,
                Low = low,
                Close = close,
                Delta = delta,
                Volume = volume,
                MaxLevelVolume = maxLevel
            };
        }

        /// <summary>
        /// The clock resolver, against bars that encode CME's PUBLISHED hours rather than this
        /// project's config: the maintenance halt is 16:00-17:00 Houston, every weekday. Shipping
        /// 15:00 -- the cash close, when trading carries straight on -- is what stopped this
        /// indicator drawing anything on its first build, and it is the same mistake Market View
        /// shipped twice.
        /// </summary>
        private static void BarClockResolution()
        {
            Section("bar clock");

            var local = HaltBars(false);
            var utc = HaltBars(true);

            // No market clock, no live wall clock: the halt is the only signal left, which is
            // exactly the case that was failing.
            var stale = new DateTime(2026, 9, 20, 12, 0, 0);

            var fromLocal = BarClockContext.Create("Central Standard Time", BarClock.Auto, local,
                                                   stale, default(DateTime), 16);
            True("local-stamped bars resolve", fromLocal.Valid);
            Eq("...as already local", fromLocal.Clock, BarClock.AlreadyLocal);
            Eq("...off the halt", fromLocal.How, "found the daily halt");

            var fromUtc = BarClockContext.Create("Central Standard Time", BarClock.Auto, utc,
                                                 stale, default(DateTime), 16);
            True("UTC-stamped bars resolve", fromUtc.Valid);
            Eq("...as UTC", fromUtc.Clock, BarClock.Utc);

            // The bug, pinned. 15:00 is a busy hour, so neither reading finds a halt there and
            // the resolver correctly refuses -- which is why nothing was drawn.
            var wrongHour = BarClockContext.Create("Central Standard Time", BarClock.Auto, local,
                                                   stale, default(DateTime), 15);
            True("the cash close is not the halt, so 15 settles nothing", !wrongHour.Valid);
            True("...and the message says what the newest bar is stamped",
                 wrongHour.Error.Contains("Newest bar is stamped"));

            // The weekend gap: works at 4h, where the one-hour halt is invisible because no bar
            // fits inside it. This is what unblocked the coarse timeframes.
            var fourHourLocal = WeekendBars(false);
            var fourHourUtc = WeekendBars(true);

            var fromGapLocal = BarClockContext.Create("Central Standard Time", BarClock.Auto,
                                                      fourHourLocal, stale, default(DateTime), 16,
                                                      TimeSpan.FromHours(4));
            True("4h local-stamped bars resolve", fromGapLocal.Valid);
            Eq("...as already local", fromGapLocal.Clock, BarClock.AlreadyLocal);
            Eq("...off the weekend gap", fromGapLocal.How, "found the weekend gap");

            var fromGapUtc = BarClockContext.Create("Central Standard Time", BarClock.Auto,
                                                    fourHourUtc, stale, default(DateTime), 16,
                                                    TimeSpan.FromHours(4));
            True("4h UTC-stamped bars resolve", fromGapUtc.Valid);
            Eq("...as UTC", fromGapUtc.Clock, BarClock.Utc);

            // Daily bars settle nothing, and that is fine: the reading cannot change which bars
            // make up the prior session, only the date printed beside it.
            var daily = new FakeBars();
            for (var i = 0; i < 10; i++)
                daily.Add(new DateTime(2026, 9, 1).AddDays(i), 100m, 101m, 99m, 100m);

            var coarse = BarClockContext.Create("Central Standard Time", BarClock.Auto, daily,
                                                stale, default(DateTime), 16, TimeSpan.FromDays(1));
            True("daily bars still produce a usable clock", coarse.Valid);
            True("...and say the reading only affects the date label",
                 coarse.How.Contains("date label"));

            // Intraday bars get no such benefit of the doubt.
            var undecidable = BarClockContext.Create("Central Standard Time", BarClock.Auto, daily,
                                                     stale, default(DateTime), 16,
                                                     TimeSpan.FromMinutes(5));
            True("a 5m chart that settles nothing still refuses", !undecidable.Valid);

            // The market clock settles it with no history at all.
            var nowUtc = new DateTime(2026, 9, 3, 19, 0, 0);
            var oneBar = new FakeBars();
            oneBar.Add(new DateTime(2026, 9, 3, 14, 0, 0), 29100m, 29110m, 29090m, 29100m);

            var byMarket = BarClockContext.Create("Central Standard Time", BarClock.Auto, oneBar,
                                                  nowUtc, new DateTime(2026, 9, 3, 14, 0, 0), 16);
            True("one bar and a market clock is enough", byMarket.Valid);
            Eq("...already local", byMarket.Clock, BarClock.AlreadyLocal);
            Eq("...off the market clock", byMarket.How, "matched to the platform's market clock");

            // A market clock that is really UTC must not be trusted as a zone signal.
            var vacuous = BarClockContext.Create("Central Standard Time", BarClock.Auto, oneBar,
                                                 nowUtc, nowUtc, 16);
            True("a market clock equal to UTC is ignored",
                 !vacuous.Valid || vacuous.How != "matched to the platform's market clock");
        }

        private static void TradeablePrices()
        {
            Section("tradeable prices");

            // The classic S3 on the validation case is not an orderable price on MNQ.
            Eq("28870.58 snaps to the quarter", PivotMath.SnapToTick(28870.5833333m, 0.25m), 28870.50m);
            Eq("29293.225 snaps up", PivotMath.SnapToTick(29293.225m, 0.25m), 29293.25m);
            Eq("an exact tick is left alone", PivotMath.SnapToTick(29293.25m, 0.25m), 29293.25m);
            Eq("no tick size means no snapping", PivotMath.SnapToTick(28870.5833333m, 0m), 28870.5833333m);
        }

        /// <summary>
        /// Four-hour bars across three weeks, stopping Friday 16:00 Houston and restarting Sunday
        /// 17:00, per CME's published calendar. Stamped either in Houston time or in UTC.
        /// </summary>
        private static FakeBars WeekendBars(bool stampUtc)
        {
            var bars = new FakeBars();
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

            // 2026-09-06 is a Sunday.
            for (var week = 0; week < 3; week++)
            {
                var open = new DateTime(2026, 9, 6).AddDays(week * 7).AddHours(17);

                // Sunday 17:00 through Friday 16:00 is 119 hours; 4-hour bars step through it.
                for (var h = 0; h < 119; h += 4)
                {
                    var local = open.AddHours(h);

                    var stamp = stampUtc
                        ? TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone)
                        : local;

                    bars.Add(stamp, 100m, 101m, 99m, 100m);
                }
            }

            return bars;
        }

        /// <summary>
        /// Three weekdays of hourly bars with 16:00 Houston empty every day, per CME's published
        /// maintenance window. Stamped either in Houston time or in UTC.
        /// </summary>
        private static FakeBars HaltBars(bool stampUtc)
        {
            var bars = new FakeBars();
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

            for (var day = 0; day < 3; day++)
            {
                var date = new DateTime(2026, 9, 14).AddDays(day);

                for (var hour = 0; hour < 24; hour++)
                {
                    if (hour == 16) continue; // the halt

                    var local = date.AddHours(hour);
                    var stamp = stampUtc
                        ? TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone)
                        : local;

                    // Several bars an hour, so one empty hour reads as decisively empty.
                    for (var n = 0; n < 6; n++)
                        bars.Add(stamp.AddMinutes(n * 10), 29100m, 29110m, 29090m, 29100m);
                }
            }

            return bars;
        }

        #endregion

        #region Reactions

        private static void ReactionOutcomes()
        {
            Section("reaction tracking");

            var cfg = new ReactionConfig();
            var clock = Clock();

            var win = ReactionTracker.Track(ReactionBars(29050m, 29055m), clock, 0, 2,
                                            29000m, 29100m, CallDirection.Unknown, cfg);

            Eq("direction inferred long from a level below the open", win.Direction, CallDirection.Long);
            True("touched", win.Touched);
            Eq("touch bar", win.TouchBar, 1);
            Eq("max favourable", win.MaxFavorable, 55m);
            Eq("max adverse", win.MaxAdverse, 5m);
            True("moved away inside the window", win.MovedAwayInWindow);
            Eq("outcome", win.Outcome, ReactionOutcome.Target);

            var lose = ReactionTracker.Track(ReactionBars(28965m, 28970m), clock, 0, 2,
                                             29000m, 29100m, CallDirection.Unknown, cfg);
            Eq("stopped out", lose.Outcome, ReactionOutcome.Stop);
            Eq("max adverse", lose.MaxAdverse, 35m);

            // One bar that reaches both: the bar has no sequence, so this must not be guessed.
            var both = ReactionTracker.Track(BothBars(), clock, 0, 1,
                                             29000m, 29100m, CallDirection.Unknown, cfg);
            Eq("target and stop in one bar is ambiguous", both.Outcome, ReactionOutcome.Ambiguous);

            var never = ReactionTracker.Track(ReactionBars(29050m, 29055m), clock, 0, 2,
                                              28000m, 29100m, CallDirection.Unknown, cfg);
            True("a level price never reached is untouched", !never.Touched);
            Eq("...and reports as such", never.Outcome, ReactionOutcome.Untouched);

            var forced = ReactionTracker.Track(ReactionBars(29050m, 29055m), clock, 0, 2,
                                               29000m, 29100m, CallDirection.Short, cfg);
            Eq("an override beats the inference", forced.Direction, CallDirection.Short);
            Eq("...which flips favourable and adverse", forced.MaxAdverse, 55m);
        }

        #endregion

        #region CSV

        private static void CsvRoundTrip()
        {
            Section("csv log");

            Eq("a plain line splits back", string.Join("|", PivotLog.Split(PivotLog.Join(new[] { "a", "b", "c" })).ToArray()), "a|b|c");
            Eq("a comma is quoted and survives", PivotLog.Split(PivotLog.Join(new[] { "a,b", "c" }))[0], "a,b");
            Eq("a quote is doubled and survives", PivotLog.Split(PivotLog.Join(new[] { "say \"hi\"" }))[0], "say \"hi\"");

            var path = Path.Combine(Path.GetTempPath(), "pivotdecoder_test_" + Guid.NewGuid().ToString("N") + ".csv");

            try
            {
                var open = Row("2026-09-03", "MNQ", "28872.00", "OPEN");
                Eq("first write succeeds", PivotLog.Upsert(path, new List<LogRow> { open }), null);

                var other = Row("2026-09-03", "MNQ", "29293.00", "OPEN");
                PivotLog.Upsert(path, new List<LogRow> { other });

                Eq("header plus two rows", File.ReadAllLines(path).Length, 3);

                // The same session's row is REWRITTEN in place at session end, not appended again.
                var closed = Row("2026-09-03", "MNQ", "28872.00", "CLOSED");
                closed.Outcome = "TARGET";
                PivotLog.Upsert(path, new List<LogRow> { closed });

                var lines = File.ReadAllLines(path);
                Eq("still two rows after closing one out", lines.Length, 3);
                True("the row now reads CLOSED", lines[1].Contains("CLOSED"));
                True("...with its outcome filled in", lines[1].Contains("TARGET"));
                True("the untouched row is left alone", lines[2].Contains("29293.00") && lines[2].Contains("OPEN"));

                // A different session date is a different row, not an overwrite.
                PivotLog.Upsert(path, new List<LogRow> { Row("2026-09-04", "MNQ", "28872.00", "OPEN") });
                Eq("a new session appends", File.ReadAllLines(path).Length, 4);
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            // Logging must never be able to take the chart down.
            var failed = PivotLog.Upsert(Path.Combine("Z:\\", "nope", "log.csv"),
                                         new List<LogRow> { Row("2026-09-03", "MNQ", "1", "OPEN") });
            True("an unwritable path returns a reason instead of throwing", failed != null);
        }

        private static LogRow Row(string date, string instrument, string level, string status)
        {
            return new LogRow
            {
                Date = date,
                Instrument = instrument,
                CallerLevel = level,
                BestMatchFamily = "FLOOR-RTH",
                BestMatchLevelName = "S3sn",
                BestMatchPrice = "28870.58",
                DeltaPts = "-1.42",
                SessionModeOfMatch = "RTH",
                MatchVerdict = "EXACT",
                Status = status
            };
        }

        #endregion

        #region Fixtures

        private static BarClockContext Clock()
        {
            // The synthetic bars are stamped Houston time already, so the resolver is told
            // outright rather than being asked to infer it from three days of history.
            return BarClockContext.Create("Central Standard Time", BarClock.AlreadyLocal, null,
                                          DateTime.UtcNow, default(DateTime), 16);
        }

        /// <summary>
        /// One complete trade date, 2026-09-02. The overnight bars sit outside the regular-hours
        /// range in both directions, and the 08:00 and 15:00 bars are placed so that a boundary
        /// off by one bar changes the answer.
        /// </summary>
        private static FakeBars PriorDayBars()
        {
            var bars = new FakeBars();

            bars.Add(new DateTime(2026, 9, 1, 17, 0, 0), 29100m, 29120m, 29080m, 29110m);
            bars.Add(new DateTime(2026, 9, 1, 21, 0, 0), 29110m, 29350m, 29100m, 29300m);
            bars.Add(new DateTime(2026, 9, 2, 3, 0, 0), 29300m, 29310m, 28900m, 28950m);
            bars.Add(new DateTime(2026, 9, 2, 8, 0, 0), 28950m, 28990m, 28930m, 28960m);

            bars.Add(new DateTime(2026, 9, 2, 8, 30, 0), 29050m, 29100m, 29017.25m, 29060m);
            bars.Add(new DateTime(2026, 9, 2, 10, 0, 0), 29060m, 29211.75m, 29050m, 29150m);
            bars.Add(new DateTime(2026, 9, 2, 14, 30, 0), 29150m, 29200m, 29140m, 29186.25m);

            bars.Add(new DateTime(2026, 9, 2, 15, 0, 0), 29186.25m, 29320m, 29180m, 29300m);

            return bars;
        }

        /// <summary>Three bars: pre-touch, the touch, then the resolution.</summary>
        private static FakeBars ReactionBars(decimal thirdLow, decimal thirdClose)
        {
            var bars = new FakeBars();

            bars.Add(new DateTime(2026, 9, 3, 8, 30, 0), 29100m, 29105m, 29050m, 29060m);
            bars.Add(new DateTime(2026, 9, 3, 9, 0, 0), 29060m, 29010m, 28995m, 29005m);
            bars.Add(new DateTime(2026, 9, 3, 9, 30, 0), 29005m,
                     thirdLow > 29000m ? 29055m : 29008m, thirdLow, thirdClose);

            return bars;
        }

        private static FakeBars BothBars()
        {
            var bars = new FakeBars();

            bars.Add(new DateTime(2026, 9, 3, 8, 30, 0), 29100m, 29105m, 29050m, 29060m);
            bars.Add(new DateTime(2026, 9, 3, 9, 0, 0), 29060m, 29060m, 28960m, 29000m);

            return bars;
        }

        private sealed class FakeBars : IBarWindow
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

        #region Assertions

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("-- " + name);
        }

        private static void Eq(string what, object got, object want)
        {
            var ok = Equals(got, want);
            Report(ok, what, got, want);
        }

        private static void True(string what, bool ok)
        {
            Report(ok, what, ok, true);
        }

        private static void Report(bool ok, string what, object got, object want)
        {
            if (ok)
            {
                Console.WriteLine("    ok   " + what);
                return;
            }

            _failures++;
            Console.WriteLine("    FAIL " + what +
                              "  got " + Show(got) + "  want " + Show(want));
        }

        private static string Show(object v)
        {
            if (v == null) return "null";
            if (v is decimal d) return d.ToString(CultureInfo.InvariantCulture);
            if (v is DateTime t) return t.ToString("yyyy-MM-dd HH:mm");
            return v.ToString();
        }

        #endregion
    }
}
