using System;
using System.Collections.Generic;
using OceansDelta;

namespace OceansDelta.Tests
{
    /// <summary>
    /// Checks the parts of Ocean Delta Cross that would be wrong quietly: which bar a cross is
    /// anchored to, whether a crossing that came back still counts, and whether re-feeding the
    /// forming bar leaves the session's numbers where they were.
    ///
    /// Every reference here is worked out independently of the code under test -- summed by hand,
    /// found by brute force, or stated as an invariant that has to hold for any input.
    /// </summary>
    static class Program
    {
        private static int _failures;
        private static int _checks;

        static int Main()
        {
            AClusterMustClearVolumeDeltaAndLeanSeparately();
            LeanIsMeasuredAgainstTheLevelsOwnVolume();
            TheSideFilterKeepsOnlyOneSign();
            AnUntradedLevelIsNeverACluster();
            TheBiggestClusterIsTheBiggestByAbsoluteDelta();
            BiggestTiesKeepTheLowerPrice();
            AnOpenFilterKeepsEveryTradedLevel();

            CumulativeDeltaIsTheRunningSum();
            SessionDeltaResetsAtTheBoundary();
            PeakAndTroughAreTheRunningExtremes();

            AFlipNeedsTheRunningSumToChangeSign();
            TheCrossSitsOnTheBarThatCrossedNotTheOneThatConfirmed();
            ACrossingThatComesBackBeforeConfirmingNeverHappened();
            TheSessionsFirstSideIsNotAFlip();
            TheSessionsFirstSideIsAFlipWhenAsked();
            AZeroThresholdMarksEveryChangeOfSign();
            LandingExactlyOnZeroSettlesNothing();
            TheBarGapSuppressesTheCrossButNotTheSide();
            AFlipIsNeverReportedTwice();

            ReplayingTheFormingBarChangesNothing();
            AFlipTakenBackByTheFormingBarIsTakenBack();
            EverySessionStartsFromZero();

            ShareIsWhereTheRunningSumReachedZero();
            ShareIsSafeWhenTheBarDidNotMove();

            FlipsAlwaysAlternateSides();
            EveryFlipHasTravelledFarEnough();
            EveryFlipSitsInsideItsOwnSession();

            AZoneSpansTheClustersThatCarriedTheFlip();
            AZoneAlwaysContainsTheCrossPrice();
            WithNoClustersTheZoneIsAPriceAndSaysSo();
            ZoneVolumeIsTheSumOfItsClusters();
            OverlapIsInclusiveAtTheEdgeAndSymmetric();
            OverlappingZonesFromDifferentSessionsMergeAndCount();
            TwoFlipsInOneSessionAreNotTwoSessions();
            ZonesThatDoNotTouchStaySeparate();
            AMergedZoneSpansBothAndKeepsTheOldestStamp();
            MergeHandsBackNewestFirst();

            APriceThatKeptAbsorbingIsFound();
            OneEnormousPrintIsNotALevelThatReloaded();
            ABalancedPriceIsNotAbsorption();
            TheSideReportedIsThePassiveOne();
            APriceClosedThroughIsBrokenAndDropped();
            BrokenLevelsAreKeptWhenAsked();
            AWickThroughIsNotABreak();
            BarsBeforeTheLevelExistedAreNotBreaks();
            BreaksAreMeasuredInTicks();
            ShareIsMeasuredAgainstTheWholeWindow();
            VolumeAndBarsAccumulateAcrossTheWindow();
            TheHeaviestLevelComesFirst();
            AnEmptyWindowFindsNothing();
            TooManyPricesAbandonTheScanRatherThanTruncateIt();

            CloseModeUsesTheClose();
            ClusterModeUsesTheClusterThatCarriedIt();
            ClusterModeWithNothingBehindItSaysSo();

            Console.WriteLine(_failures == 0
                ? _checks + " checks passed."
                : _failures + " of " + _checks + " checks FAILED.");

            return _failures == 0 ? 0 : 1;
        }

        #region Cluster search

        private static void AClusterMustClearVolumeDeltaAndLeanSeparately()
        {
            // 100 traded, 70 lifted the ask and 30 hit the bid: delta 40, lean 40%.
            var levels = new List<PriceVolume> { Level(100m, 100m, 30m, 70m) };

            Check("a level clearing all three counts",
                  ClusterSearch.Find(levels, Filter(100m, 40m, 40m, 0)).Count == 1);

            Check("volume one contract short is rejected",
                  ClusterSearch.Find(levels, Filter(101m, 40m, 40m, 0)).Count == 0);

            Check("delta one contract short is rejected",
                  ClusterSearch.Find(levels, Filter(100m, 41m, 40m, 0)).Count == 0);

            Check("lean one point short is rejected",
                  ClusterSearch.Find(levels, Filter(100m, 40m, 41m, 0)).Count == 0);
        }

        private static void LeanIsMeasuredAgainstTheLevelsOwnVolume()
        {
            var big = new List<PriceVolume> { Level(100m, 400m, 190m, 210m) };   // delta 20, lean 5%
            var small = new List<PriceVolume> { Level(100m, 60m, 5m, 55m) };     // delta 50, lean 83%

            var filter = Filter(0m, 0m, 35m, 0);

            Check("a big level split near evenly is not a cluster",
                  ClusterSearch.Find(big, filter).Count == 0);

            Check("a small level taken almost entirely one way is",
                  ClusterSearch.Find(small, filter).Count == 1);
        }

        private static void TheSideFilterKeepsOnlyOneSign()
        {
            var levels = new List<PriceVolume>
            {
                Level(100m, 100m, 20m, 80m),   // +60
                Level(101m, 100m, 80m, 20m)    // -60
            };

            var open = ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 0));
            var buys = ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 1));
            var sells = ClusterSearch.Find(levels, Filter(0m, 0m, 0m, -1));

            Check("no side filter keeps both", open.Count == 2);
            Check("the buy filter keeps the level buyers won", buys.Count == 1 && buys[0].Delta > 0m);
            Check("the sell filter keeps the level sellers won", sells.Count == 1 && sells[0].Delta < 0m);
        }

        private static void AnUntradedLevelIsNeverACluster()
        {
            var levels = new List<PriceVolume> { Level(100m, 0m, 0m, 0m) };

            Check("an untraded level cannot pass even an open filter",
                  ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 0)).Count == 0);
        }

        private static void TheBiggestClusterIsTheBiggestByAbsoluteDelta()
        {
            // Brute force the answer from the same numbers, independently of Biggest().
            var levels = new List<PriceVolume>();
            var seed = 7;

            for (var i = 0; i < 40; i++)
            {
                seed = Next(seed);
                var bid = seed % 90 + 5;
                seed = Next(seed);
                var ask = seed % 90 + 5;
                levels.Add(Level(100m + i * 0.25m, bid + ask, bid, ask));
            }

            var hits = ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 0));

            var wantedIndex = 0;
            for (var i = 1; i < hits.Count; i++)
                if (Abs(hits[i].Delta) > Abs(hits[wantedIndex].Delta)) wantedIndex = i;

            ClusterHit best;
            ClusterSearch.Biggest(hits, out best);

            Check("the biggest cluster matches a brute-force scan",
                  Abs(best.Delta) == Abs(hits[wantedIndex].Delta));
        }

        private static void BiggestTiesKeepTheLowerPrice()
        {
            var levels = new List<PriceVolume>
            {
                Level(101m, 100m, 20m, 80m),
                Level(100m, 100m, 20m, 80m)
            };

            ClusterHit best;
            ClusterSearch.Biggest(ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 0)), out best);

            Check("a tie resolves to the lower price, whatever the order", best.Price == 100m);

            ClusterHit none;
            Check("nothing found reports nothing found",
                  !ClusterSearch.Biggest(new List<ClusterHit>(), out none));
        }

        private static void AnOpenFilterKeepsEveryTradedLevel()
        {
            var levels = new List<PriceVolume>();
            for (var i = 0; i < 25; i++) levels.Add(Level(100m + i, 10m + i, 5m, 5m + i));

            Check("an open filter keeps everything that traded",
                  ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 0)).Count == 25);
        }

        #endregion

        #region Session delta

        private static void CumulativeDeltaIsTheRunningSum()
        {
            var deltas = new[] { 40m, -10m, 25m, -100m, 5m };
            var engine = Engine(0m);

            var running = 0m;
            var ok = true;

            for (var bar = 0; bar < deltas.Length; bar++)
            {
                Flip flip;
                engine.Feed(bar, Time(bar), deltas[bar], bar == 0, out flip);

                running += deltas[bar];
                if (engine.Read.Cum != running) ok = false;
            }

            Check("session delta is the running sum of bar delta, bar for bar", ok);
        }

        private static void SessionDeltaResetsAtTheBoundary()
        {
            var engine = Engine(0m);
            Flip flip;

            engine.Feed(0, Time(0), 500m, true, out flip);
            engine.Feed(1, Time(1), 300m, false, out flip);

            Check("the session carries its own sum", engine.Read.Cum == 800m);

            engine.Feed(2, Time(2), -40m, true, out flip);

            Check("a new session starts from zero, not from yesterday", engine.Read.Cum == -40m);
            Check("the new session's bar count starts again", engine.Read.Bars == 1);
            Check("the new session's start bar is the boundary bar", engine.Read.StartBar == 2);
            Check("yesterday's peak does not carry over", engine.Read.Peak == 0m);
        }

        private static void PeakAndTroughAreTheRunningExtremes()
        {
            var deltas = new[] { 100m, -250m, 40m, 300m, -600m, 20m };
            var engine = Engine(0m);

            var running = 0m;
            var peak = 0m;
            var trough = 0m;

            for (var bar = 0; bar < deltas.Length; bar++)
            {
                Flip flip;
                engine.Feed(bar, Time(bar), deltas[bar], bar == 0, out flip);

                running += deltas[bar];
                if (running > peak) peak = running;
                if (running < trough) trough = running;
            }

            Check("the peak matches a hand-kept maximum", engine.Read.Peak == peak);
            Check("the trough matches a hand-kept minimum", engine.Read.Trough == trough);
        }

        #endregion

        #region Flips

        private static void AFlipNeedsTheRunningSumToChangeSign()
        {
            // Falls the whole way but never crosses: no flip, however big the fall.
            var engine = Engine(100m);
            var flips = Run(engine, new[] { 5000m, -1000m, -1000m, -1000m, -1000m });

            Check("a big move that stays one side of zero is not a flip", flips == 0);
        }

        private static void TheCrossSitsOnTheBarThatCrossedNotTheOneThatConfirmed()
        {
            // Bar 0: +600 (session takes the buy side, not a flip).
            // Bar 1: -700 -> sum -100. This is the crossing.
            // Bar 2: -50  -> sum -150.
            // Bar 3: -200 -> sum -350, which finally clears the 300 threshold.
            var engine = Engine(300m);
            Run(engine, new[] { 600m, -700m, -50m, -200m });

            Check("exactly one flip came out of that", engine.Flips.Count == 1);

            if (engine.Flips.Count != 1) return;

            var flip = engine.Flips[0];

            Check("the cross is anchored to the crossing bar", flip.Bar == 1);
            Check("the confirming bar is recorded separately", flip.ConfirmBar == 3);
            Check("the flip faces the way the sum went", flip.Sign == -1);
            Check("the sum entering the crossing bar is kept", flip.Before == 600m);
            Check("the sum leaving the crossing bar is kept", flip.After == -100m);
            Check("the confirming sum is the one that cleared the bar", flip.ConfirmCum == -350m);
            Check("this was a reversal, not the session picking a side", !flip.FirstSide);
        }

        private static void ACrossingThatComesBackBeforeConfirmingNeverHappened()
        {
            // Crosses to -100, then comes straight back over. Never travelled 300 past zero.
            var engine = Engine(300m);
            var flips = Run(engine, new[] { 600m, -700m, 400m, 200m });

            Check("a crossing that came back before confirming leaves no cross", flips == 0);
            Check("and the side is still the one it started on", engine.Read.Side == 1);
            Check("and nothing is left arming", !engine.Read.Arming);
        }

        private static void TheSessionsFirstSideIsNotAFlip()
        {
            var engine = Engine(100m);
            var flips = Run(engine, new[] { 500m, 200m });

            Check("a session choosing its first side is not a reversal", flips == 0);
            Check("but the side is taken", engine.Read.Side == 1);
        }

        private static void TheSessionsFirstSideIsAFlipWhenAsked()
        {
            var engine = Engine(100m);
            engine.MarkFirstSide = true;
            var flips = Run(engine, new[] { 500m, 200m });

            Check("asked for, the first side is marked", flips == 1);
            Check("and it says that is what it is", engine.Flips[0].FirstSide);
        }

        private static void AZeroThresholdMarksEveryChangeOfSign()
        {
            // Sum walks +10, -10, +10, -10: three changes of sign after the first side.
            var engine = Engine(0m);
            var flips = Run(engine, new[] { 10m, -20m, 20m, -20m });

            Check("with no threshold every change of sign is a cross", flips == 3);
        }

        private static void LandingExactlyOnZeroSettlesNothing()
        {
            var engine = Engine(0m);
            var flips = Run(engine, new[] { 100m, -100m, 50m });

            Check("a sum returning to exactly zero and going back is not a flip", flips == 0);
            Check("and the side is untouched", engine.Read.Side == 1);
        }

        private static void TheBarGapSuppressesTheCrossButNotTheSide()
        {
            var engine = Engine(0m);
            engine.MinBarsBetween = 5;

            // Bar 0 takes the buy side. Bar 1 crosses down -- inside the gap, so no cross drawn.
            // Bar 8 crosses back up, seven bars later, which clears it.
            var flips = Run(engine, new[] { 100m, -200m, 0m, 0m, 0m, 0m, 0m, 0m, 300m });

            Check("a recross inside the gap draws nothing", flips == 1);
            Check("but the side did change underneath, so the next one is found", engine.Read.Side == 1);
            Check("and the one drawn is the later crossing", engine.Flips[0].Bar == 8);
        }

        private static void AFlipIsNeverReportedTwice()
        {
            var engine = Engine(200m);
            Run(engine, new[] { 600m, -900m, -100m, -100m, -100m, -100m });

            var seen = new HashSet<int>();
            var duplicated = false;

            for (var i = 0; i < engine.Flips.Count; i++)
                if (!seen.Add(engine.Flips[i].Bar)) duplicated = true;

            Check("a confirmed flip is emitted once and then left alone", !duplicated);
        }

        #endregion

        #region Replay

        private static void ReplayingTheFormingBarChangesNothing()
        {
            // Every tick of a forming bar re-feeds the same bar index with a bigger delta. The
            // state afterwards has to match feeding that bar once with its final delta.
            var replayed = Engine(150m);
            var once = Engine(150m);

            Flip flip;

            for (var bar = 0; bar < 4; bar++)
            {
                replayed.Feed(bar, Time(bar), Walk(bar), bar == 0, out flip);
                once.Feed(bar, Time(bar), Walk(bar), bar == 0, out flip);
            }

            // Four ticks of bar 4, the last of which is the value the closed bar ends up with.
            replayed.Feed(4, Time(4), -100m, false, out flip);
            replayed.Feed(4, Time(4), -400m, false, out flip);
            replayed.Feed(4, Time(4), -900m, false, out flip);
            replayed.Feed(4, Time(4), Walk(4), false, out flip);

            once.Feed(4, Time(4), Walk(4), false, out flip);

            Check("re-feeding the forming bar leaves the same session delta",
                  replayed.Read.Cum == once.Read.Cum);
            Check("and the same bar count", replayed.Read.Bars == once.Read.Bars);
            Check("and the same side", replayed.Read.Side == once.Read.Side);
            Check("and the same number of crosses", replayed.Flips.Count == once.Flips.Count);
            Check("and the same peak", replayed.Read.Peak == once.Read.Peak);
            Check("and the same trough", replayed.Read.Trough == once.Read.Trough);
        }

        private static void AFlipTakenBackByTheFormingBarIsTakenBack()
        {
            var engine = Engine(300m);
            Flip flip;

            engine.Feed(0, Time(0), 600m, true, out flip);

            // The forming bar prints a huge sell: enough to cross and confirm at once.
            engine.Feed(1, Time(1), -1000m, false, out flip);
            Check("a forming bar can arm and confirm a cross on its own", engine.Flips.Count == 1);

            // Then it fills back in and closes barely down. The cross never happened.
            engine.Feed(1, Time(1), -100m, false, out flip);
            Check("and the same bar filling back in removes it", engine.Flips.Count == 0);
            Check("leaving the session delta where the closed bar actually put it",
                  engine.Read.Cum == 500m);
            Check("and the side unchanged", engine.Read.Side == 1);
        }

        private static void EverySessionStartsFromZero()
        {
            var engine = Engine(100m);
            var ok = true;

            for (var bar = 0; bar < 60; bar++)
            {
                Flip flip;
                var newSession = bar % 13 == 0;

                engine.Feed(bar, Time(bar), Walk(bar), newSession, out flip);

                if (newSession && engine.Read.Cum != Walk(bar)) ok = false;
                if (newSession && engine.Read.Side != 0 && Abs(Walk(bar)) < 100m) ok = false;
            }

            Check("every session opens from zero with no side inherited", ok);
        }

        #endregion

        #region Share of the bar

        private static void ShareIsWhereTheRunningSumReachedZero()
        {
            // Entering at -100, the bar prints +250 net. Zero came 100 of the way through 250.
            Check("the share is the distance to zero over the bar's net delta",
                  DeltaEngine.Share(-100m, 250m) == 100m / 250m);

            Check("a bar starting at zero crossed at its very start",
                  DeltaEngine.Share(0m, 250m) == 0m);

            Check("the same holds going the other way",
                  DeltaEngine.Share(100m, -250m) == 100m / 250m);
        }

        private static void ShareIsSafeWhenTheBarDidNotMove()
        {
            Check("a bar with no net delta reports the start rather than dividing by zero",
                  DeltaEngine.Share(-100m, 0m) == 0m);

            Check("a bar that did not reach zero is clamped, not extrapolated",
                  DeltaEngine.Share(-100m, -50m) == 0m);

            Check("and a bar that went further than the distance is clamped the other way",
                  DeltaEngine.Share(-100m, 40m) <= 1m);
        }

        #endregion

        #region Invariants

        private static void FlipsAlwaysAlternateSides()
        {
            var broken = false;

            for (var seed = 1; seed <= 30; seed++)
            {
                var engine = Engine(seed * 20m);
                var value = seed * 7919;

                for (var bar = 0; bar < 400; bar++)
                {
                    Flip flip;
                    value = Next(value);
                    engine.Feed(bar, Time(bar), value % 401 - 200m, bar % 97 == 0, out flip);
                }

                for (var i = 1; i < engine.Flips.Count; i++)
                {
                    // Only within one session: a new session resets the side, so the first flip
                    // of a session may legitimately face the same way as the last of the one before.
                    if (engine.Flips[i].SessionStartBar != engine.Flips[i - 1].SessionStartBar) continue;
                    if (engine.Flips[i].Sign == engine.Flips[i - 1].Sign) broken = true;
                }
            }

            Check("two crosses in a row never face the same way inside one session", !broken);
        }

        private static void EveryFlipHasTravelledFarEnough()
        {
            var broken = false;

            for (var seed = 1; seed <= 30; seed++)
            {
                var confirm = seed * 25m;
                var engine = Engine(confirm);
                var value = seed * 104729;

                for (var bar = 0; bar < 400; bar++)
                {
                    Flip flip;
                    value = Next(value);
                    engine.Feed(bar, Time(bar), value % 401 - 200m, bar % 97 == 0, out flip);
                }

                for (var i = 0; i < engine.Flips.Count; i++)
                {
                    var flip = engine.Flips[i];

                    if (Abs(flip.ConfirmCum) < confirm) broken = true;
                    if (flip.ConfirmBar < flip.Bar) broken = true;

                    // The crossing bar is the one that took the new sign, so the sum leaving it
                    // faces the flip's way and the sum entering it does not.
                    if (Sign(flip.After) != flip.Sign) broken = true;
                    if (Sign(flip.Before) == flip.Sign) broken = true;
                }
            }

            Check("every cross drawn had cleared its threshold, on a bar that really crossed", !broken);
        }

        private static void EveryFlipSitsInsideItsOwnSession()
        {
            var broken = false;

            for (var seed = 1; seed <= 20; seed++)
            {
                var engine = Engine(seed * 30m);
                var value = seed * 15485863;
                var start = 0;

                for (var bar = 0; bar < 300; bar++)
                {
                    Flip flip;
                    value = Next(value);
                    var newSession = bar % 61 == 0;
                    if (newSession) start = bar;

                    if (engine.Feed(bar, Time(bar), value % 401 - 200m, newSession, out flip))
                    {
                        if (flip.SessionStartBar != start) broken = true;
                        if (flip.Bar < start || flip.Bar > bar) broken = true;
                    }
                }
            }

            Check("a cross is always on a bar inside the session that produced it", !broken);
        }

        #endregion

        #region Cross price

        private static void CloseModeUsesTheClose()
        {
            var hits = ClusterSearch.Find(new List<PriceVolume> { Level(105m, 100m, 10m, 90m) },
                                          Filter(0m, 0m, 0m, 0));

            var price = CrossMath.Price(CrossPriceMode.Close, 100.25m, hits);

            Check("close mode uses the close even when a cluster is there", price.Price == 100.25m);
            Check("and does not claim a cluster", !price.FromCluster);
            Check("and says so", price.Note == "close");
        }

        private static void ClusterModeUsesTheClusterThatCarriedIt()
        {
            var levels = new List<PriceVolume>
            {
                Level(100m, 100m, 40m, 60m),   // +20
                Level(105m, 200m, 20m, 180m),  // +160, the one that did the work
                Level(110m, 100m, 45m, 55m)    // +10
            };

            var price = CrossMath.Price(CrossPriceMode.Cluster, 100.25m,
                                        ClusterSearch.Find(levels, Filter(0m, 0m, 0m, 0)));

            Check("cluster mode uses the level that carried the most delta", price.Price == 105m);
            Check("and says it came from a cluster", price.FromCluster);
        }

        private static void ClusterModeWithNothingBehindItSaysSo()
        {
            var price = CrossMath.Price(CrossPriceMode.Cluster, 100.25m, new List<ClusterHit>());

            Check("with no cluster the price falls back to the close", price.Price == 100.25m);
            Check("but never claims to be a cluster", !price.FromCluster);
            Check("and the note carries the fallback rather than hiding it",
                  price.Note.Contains("no cluster"));
        }

        #endregion

        #region Flip zones

        private static void AZoneSpansTheClustersThatCarriedTheFlip()
        {
            var hits = Hits(100m, 102m, 105m);
            var zone = ZoneMath.Zone(Flipped(7, 1), 102m, hits, 3, Time(0));

            Check("the zone reaches the highest cluster", zone.High == 105m);
            Check("and the lowest", zone.Low == 100m);
            Check("and says the band came from clusters", zone.FromClusters);
            Check("and keeps the cross price as its anchor", zone.Price == 102m);
            Check("and one flip stands for one session", zone.Sessions == 1);
        }

        private static void AZoneAlwaysContainsTheCrossPrice()
        {
            // Every cluster sat above the close, which is where the cross price came from.
            var zone = ZoneMath.Zone(Flipped(7, 1), 95m, Hits(100m, 102m), 1, Time(0));

            Check("a band that missed its own cross price is stretched to hold it", zone.Low == 95m);
            Check("and still reaches the clusters", zone.High == 102m);
        }

        private static void WithNoClustersTheZoneIsAPriceAndSaysSo()
        {
            var zone = ZoneMath.Zone(Flipped(7, -1), 101.25m, new List<ClusterHit>(), 2, Time(0));

            Check("with nothing behind it the zone collapses to the price",
                  zone.High == 101.25m && zone.Low == 101.25m);
            Check("and never claims a cluster span it does not have", !zone.FromClusters);
            Check("and carries no volume it cannot account for", zone.Volume == 0m);
        }

        private static void ZoneVolumeIsTheSumOfItsClusters()
        {
            var hits = new List<ClusterHit>();
            var total = 0m;

            for (var i = 0; i < 5; i++)
            {
                var hit = new ClusterHit();
                hit.Price = 100m + i;
                hit.Volume = 40m + i * 10m;
                hit.Delta = 30m;
                hits.Add(hit);
                total += hit.Volume;
            }

            var zone = ZoneMath.Zone(Flipped(7, 1), 100m, hits, 1, Time(0));

            Check("zone volume adds up its clusters", zone.Volume == total);
        }

        private static void OverlapIsInclusiveAtTheEdgeAndSymmetric()
        {
            var a = Zone(1, 100m, 105m);
            var b = Zone(2, 105m, 110m);
            var c = Zone(3, 106m, 110m);

            Check("bands that meet exactly at an edge overlap", a.Overlaps(b) && b.Overlaps(a));
            Check("bands with a gap do not", !a.Overlaps(c) && !c.Overlaps(a));
        }

        private static void OverlappingZonesFromDifferentSessionsMergeAndCount()
        {
            var merged = ZoneMath.Merge(new List<FlipZone> { Zone(1, 100m, 103m), Zone(2, 102m, 106m) });

            Check("two sessions turning on the same price become one level", merged.Count == 1);
            Check("and the level counts both sessions", merged[0].Sessions == 2);
        }

        private static void TwoFlipsInOneSessionAreNotTwoSessions()
        {
            var merged = ZoneMath.Merge(new List<FlipZone> { Zone(4, 100m, 103m), Zone(4, 101m, 104m) });

            Check("one session changing its mind twice is still one session", merged.Count == 1);
            Check("and does not inflate the count", merged[0].Sessions == 1);
        }

        private static void ZonesThatDoNotTouchStaySeparate()
        {
            var merged = ZoneMath.Merge(new List<FlipZone>
            {
                Zone(1, 100m, 101m), Zone(2, 110m, 111m), Zone(3, 120m, 121m)
            });

            Check("levels that do not overlap are left alone", merged.Count == 3);

            var counted = true;
            for (var i = 0; i < merged.Count; i++) if (merged[i].Sessions != 1) counted = false;

            Check("and none of them claims a session it did not have", counted);
        }

        private static void AMergedZoneSpansBothAndKeepsTheOldestStamp()
        {
            var older = Zone(1, 100m, 103m);
            older.SessionStart = Time(0);

            var newer = Zone(2, 102m, 106m);
            newer.SessionStart = Time(500);

            var merged = ZoneMath.Merge(new List<FlipZone> { older, newer });

            Check("the merged band covers both", merged[0].Low == 100m && merged[0].High == 106m);
            Check("and is stamped with the oldest session, so 'held since' reads right",
                  merged[0].SessionStart == Time(0) && merged[0].Session == 1);
        }

        private static void MergeHandsBackNewestFirst()
        {
            var merged = ZoneMath.Merge(new List<FlipZone>
            {
                Zone(1, 100m, 101m), Zone(2, 110m, 111m), Zone(3, 120m, 121m)
            });

            Check("the newest level comes back first, so a cap keeps the ones that matter",
                  merged[0].Session == 3 && merged[merged.Count - 1].Session == 1);
        }

        #endregion

        #region Lines in the sand

        private static void APriceThatKeptAbsorbingIsFound()
        {
            // 100.00 takes 200 lots a bar for five bars, almost all of it hitting the bid, and
            // every close holds above it.
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 5; bar++)
                window.Add(Bar(bar, 100.50m, Level(100m, 200m, 180m, 20m), Level(100.5m, 40m, 20m, 20m)));

            var scan = IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000);

            Check("a price that kept absorbing is found", scan.Found.Count == 1);
            Check("nothing went wrong finding it", scan.Problem == null);

            if (scan.Found.Count != 1) return;

            Check("it is the price that absorbed", scan.Found[0].Price == 100m);
            Check("it counted every bar", scan.Found[0].Bars == 5);
            Check("it added up the volume", scan.Found[0].Volume == 1000m);
            Check("and it held", scan.Found[0].Held && scan.Found[0].Breaks == 0);
        }

        private static void OneEnormousPrintIsNotALevelThatReloaded()
        {
            var window = new List<BarLevels>();
            window.Add(Bar(0, 100.5m, Level(100m, 5000m, 4800m, 200m)));
            for (var bar = 1; bar < 5; bar++) window.Add(Bar(bar, 100.5m, Level(100.5m, 50m, 25m, 25m)));

            var scan = IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000);

            Check("one huge print in one bar is a trade, not a level that reloaded",
                  scan.Found.Count == 0);
        }

        private static void ABalancedPriceIsNotAbsorption()
        {
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 6; bar++)
                window.Add(Bar(bar, 100.5m, Level(100m, 400m, 205m, 195m)));

            Check("a heavy price with no lean is business, not absorption",
                  IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000).Found.Count == 0);
        }

        private static void TheSideReportedIsThePassiveOne()
        {
            var hitBid = new List<BarLevels>();
            var liftAsk = new List<BarLevels>();

            for (var bar = 0; bar < 5; bar++)
            {
                hitBid.Add(Bar(bar, 101m, Level(100m, 200m, 180m, 20m)));
                liftAsk.Add(Bar(bar, 99m, Level(100m, 200m, 20m, 180m)));
            }

            var support = IcebergSearch.Scan(hitBid, 0.25m, IceFilter(), 1000).Found;
            var resistance = IcebergSearch.Scan(liftAsk, 0.25m, IceFilter(), 1000).Found;

            Check("sellers hitting a bid that held means someone passive was BUYING",
                  support.Count == 1 && support[0].Side == 1);
            Check("buyers lifting an offer that held means someone passive was SELLING",
                  resistance.Count == 1 && resistance[0].Side == -1);
        }

        private static void APriceClosedThroughIsBrokenAndDropped()
        {
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 5; bar++)
                window.Add(Bar(bar, bar == 4 ? 97m : 100.5m, Level(100m, 200m, 180m, 20m)));

            Check("a level closed through is not reported by default",
                  IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000).Found.Count == 0);
        }

        private static void BrokenLevelsAreKeptWhenAsked()
        {
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 5; bar++)
                window.Add(Bar(bar, bar == 4 ? 97m : 100.5m, Level(100m, 200m, 180m, 20m)));

            var filter = IceFilter();
            filter.KeepBroken = true;

            var found = IcebergSearch.Scan(window, 0.25m, filter, 1000).Found;

            Check("asked for, a broken level comes back", found.Count == 1);
            Check("marked broken rather than quietly downgraded",
                  found.Count == 1 && !found[0].Held && found[0].Breaks == 1);
        }

        private static void AWickThroughIsNotABreak()
        {
            // Price traded far below on every bar and closed back above every time. That is the
            // level working; counting it as a break would throw away every level that ever held.
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 5; bar++)
            {
                var candle = Bar(bar, 100.5m, Level(100m, 200m, 180m, 20m));
                candle.Low = 90m;
                window.Add(candle);
            }

            var found = IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000).Found;

            Check("wicking through a level is the level working, not a break",
                  found.Count == 1 && found[0].Held);
        }

        private static void BarsBeforeTheLevelExistedAreNotBreaks()
        {
            var window = new List<BarLevels>();

            // Two bars far below, before the level ever traded.
            window.Add(Bar(0, 80m, Level(80m, 30m, 15m, 15m)));
            window.Add(Bar(1, 80m, Level(80m, 30m, 15m, 15m)));

            for (var bar = 2; bar < 7; bar++)
                window.Add(Bar(bar, 100.5m, Level(100m, 200m, 180m, 20m)));

            var found = IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000).Found;

            Check("what price did before a level existed is not evidence against it",
                  found.Count == 1 && found[0].Held);
            Check("and the level is dated from when it first traded",
                  found.Count == 1 && found[0].FirstBar == 2);
        }

        private static void BreaksAreMeasuredInTicks()
        {
            // ThroughTicks 4 at a 0.25 tick is one whole point.
            var near = new List<BarLevels>();
            var far = new List<BarLevels>();

            for (var bar = 0; bar < 5; bar++)
            {
                near.Add(Bar(bar, bar == 4 ? 99.10m : 100.5m, Level(100m, 200m, 180m, 20m)));
                far.Add(Bar(bar, bar == 4 ? 98.90m : 100.5m, Level(100m, 200m, 180m, 20m)));
            }

            var filter = IceFilter();
            filter.KeepBroken = true;

            Check("a close inside the tolerance is not a break",
                  IcebergSearch.Scan(near, 0.25m, filter, 1000).Found[0].Held);
            Check("a close beyond it is",
                  !IcebergSearch.Scan(far, 0.25m, filter, 1000).Found[0].Held);
        }

        private static void ShareIsMeasuredAgainstTheWholeWindow()
        {
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 5; bar++)
                window.Add(Bar(bar, 100.5m, Level(100m, 200m, 180m, 20m), Level(101m, 200m, 100m, 100m)));

            // 1000 at the level out of 2000 in the window.
            var filter = IceFilter();
            filter.MinSharePercent = 50m;
            Check("a level holding exactly its share passes",
                  IcebergSearch.Scan(window, 0.25m, filter, 1000).Found.Count == 1);

            filter.MinSharePercent = 51m;
            Check("and one point more rejects it",
                  IcebergSearch.Scan(window, 0.25m, filter, 1000).Found.Count == 0);
        }

        private static void VolumeAndBarsAccumulateAcrossTheWindow()
        {
            var window = new List<BarLevels>();
            var volume = 0m;

            for (var bar = 0; bar < 9; bar++)
            {
                var size = 100m + bar * 10m;
                volume += size;
                window.Add(Bar(bar, 100.5m, Level(100m, size, size * 0.9m, size * 0.1m)));
            }

            var found = IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000).Found;

            Check("volume is summed across every bar that traded there",
                  found.Count == 1 && found[0].Volume == volume);
            Check("and so is the bar count", found.Count == 1 && found[0].Bars == 9);
        }

        private static void TheHeaviestLevelComesFirst()
        {
            var window = new List<BarLevels>();
            for (var bar = 0; bar < 5; bar++)
                window.Add(Bar(bar, 103m, Level(100m, 200m, 180m, 20m),
                                          Level(101m, 500m, 450m, 50m),
                                          Level(102m, 300m, 270m, 30m)));

            var found = IcebergSearch.Scan(window, 0.25m, IceFilter(), 1000).Found;

            Check("three levels qualified", found.Count == 3);

            var ordered = true;
            for (var i = 1; i < found.Count; i++)
                if (found[i].Volume > found[i - 1].Volume) ordered = false;

            Check("the heaviest comes first, so a cap keeps what matters", ordered);
        }

        private static void AnEmptyWindowFindsNothing()
        {
            Check("no bars, nothing found",
                  IcebergSearch.Scan(new List<BarLevels>(), 0.25m, IceFilter(), 1000).Found.Count == 0);

            Check("and a null window does not throw",
                  IcebergSearch.Scan(null, 0.25m, IceFilter(), 1000).Found.Count == 0);
        }

        private static void TooManyPricesAbandonTheScanRatherThanTruncateIt()
        {
            var window = new List<BarLevels>();

            for (var bar = 0; bar < 5; bar++)
            {
                var levels = new List<PriceVolume>();
                for (var i = 0; i < 200; i++) levels.Add(Level(1000m + i, 200m, 180m, 20m));

                var entry = new BarLevels();
                entry.Bar = bar;
                entry.Close = 1000m;
                entry.Levels = levels;
                window.Add(entry);
            }

            var scan = IcebergSearch.Scan(window, 0.25m, IceFilter(), 50);

            Check("a window with more prices than the cap is abandoned, not truncated",
                  scan.Found.Count == 0);
            Check("and it says why, rather than looking like an answer", scan.Problem != null);
        }

        #endregion

        #region Helpers

        private static DeltaEngine Engine(decimal confirm)
        {
            var engine = new DeltaEngine();
            engine.ConfirmContracts = confirm;
            engine.MinBarsBetween = 0;
            engine.MarkFirstSide = false;
            return engine;
        }

        /// <summary>Feeds a run of bar deltas as one session and returns how many flips came out.</summary>
        private static int Run(DeltaEngine engine, decimal[] deltas)
        {
            for (var bar = 0; bar < deltas.Length; bar++)
            {
                Flip flip;
                engine.Feed(bar, Time(bar), deltas[bar], bar == 0, out flip);
            }

            return engine.Flips.Count;
        }

        private static FlipZone Zone(int session, decimal low, decimal high)
        {
            var zone = new FlipZone();
            zone.Session = session;
            zone.SessionStart = Time(session * 100);
            zone.Low = low;
            zone.High = high;
            zone.Price = low;
            zone.Sessions = 1;
            zone.FromClusters = true;
            return zone;
        }

        private static Flip Flipped(int bar, int sign)
        {
            var flip = new Flip();
            flip.Bar = bar;
            flip.Sign = sign;
            flip.Time = Time(bar);
            return flip;
        }

        private static List<ClusterHit> Hits(params decimal[] prices)
        {
            var hits = new List<ClusterHit>();

            for (var i = 0; i < prices.Length; i++)
            {
                var hit = new ClusterHit();
                hit.Price = prices[i];
                hit.Volume = 100m;
                hit.Delta = 60m;
                hits.Add(hit);
            }

            return hits;
        }

        private static IcebergFilter IceFilter()
        {
            var filter = new IcebergFilter();
            filter.MinVolume = 500m;
            filter.MinSharePercent = 0m;
            filter.MinLeanPercent = 40m;
            filter.MinBars = 4;
            filter.ThroughTicks = 4;
            filter.KeepBroken = false;
            return filter;
        }

        private static BarLevels Bar(int bar, decimal close, params PriceVolume[] levels)
        {
            var entry = new BarLevels();
            entry.Bar = bar;
            entry.Close = close;
            entry.High = close;
            entry.Low = close;
            entry.Levels = new List<PriceVolume>(levels);
            return entry;
        }

        private static PriceVolume Level(decimal price, decimal volume, decimal bid, decimal ask)
        {
            var level = new PriceVolume();
            level.Price = price;
            level.Volume = volume;
            level.Bid = bid;
            level.Ask = ask;
            return level;
        }

        private static ClusterFilter Filter(decimal volume, decimal delta, decimal lean, int sign)
        {
            var filter = new ClusterFilter();
            filter.MinVolume = volume;
            filter.MinDelta = delta;
            filter.MinLeanPercent = lean;
            filter.Sign = sign;
            return filter;
        }

        private static DateTime Time(int bar)
        {
            return new DateTime(2026, 8, 20, 14, 0, 0, DateTimeKind.Utc).AddMinutes(bar);
        }

        /// <summary>A repeatable walk, so a failure can be looked at again.</summary>
        private static decimal Walk(int bar)
        {
            var value = Next(bar * 2654435761L % int.MaxValue == 0 ? 1 : (int)(bar * 48271L % 2147483647L));
            return value % 601 - 300m;
        }

        private static int Next(int value)
        {
            return (int)(((long)value * 1103515245 + 12345) & 0x7fffffff);
        }

        private static decimal Abs(decimal value)
        {
            return value < 0m ? -value : value;
        }

        private static int Sign(decimal value)
        {
            return value > 0m ? 1 : value < 0m ? -1 : 0;
        }

        private static void Check(string what, bool passed)
        {
            _checks++;
            if (passed) return;

            _failures++;
            Console.WriteLine("FAIL: " + what);
        }

        #endregion
    }
}
