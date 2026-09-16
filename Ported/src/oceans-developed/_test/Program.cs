using System;
using System.Collections.Generic;
using OceansDeveloped;

namespace OceansDeveloped.Tests
{
    /// <summary>
    /// Checks the parts of Ocean Developed Profile that would be wrong quietly: where the point
    /// of control and value area land, which prices count as a shelf, whether summing days back
    /// into a week loses anything, and which futures trade date a bar belongs to.
    ///
    /// Every reference here is written independently of the code it checks -- a brute-force
    /// scan, a hand-worked example, or an invariant that must hold for any input. A test that
    /// re-implements the thing it is testing passes for the wrong reason.
    /// </summary>
    static class Program
    {
        private const decimal Tick = 0.25m;
        private static int _failures;

        static int Main()
        {
            // Building
            AccumulationAddsUpAcrossBars();
            UntradedTicksBecomeRealZeros();
            AnEmptyProfileIsRefused();
            AnOverlyWideProfileIsRefusedNotTruncated();
            PointOfControlIsTheHeaviestLevel();
            PointOfControlTieKeepsTheLowerPrice();
            TotalsMatchABruteForceScan();

            // Summing days into weeks, months and rolling windows
            MergingDaysEqualsBuildingFromTheSameTrades();
            MergingAlignsLaddersThatDoNotOverlap();
            MergingCarriesIncompletenessUpward();
            MergingNothingIsRefused();
            MergingCountsTheDaysItSummed();

            // Value area
            ValueAreaContainsThePocAndEnoughVolume();
            ValueAreaIsContiguousForAnyProfile();
            ValueAreaGrowsTowardTheHeavierSide();
            ValueAreaOfOneHundredPercentCoversEverything();

            // High volume shelves
            ShelvesAreTheRunsAboveTheFloor();
            AThinDipInsideAShelfIsBridged();
            AWideGapSplitsTwoShelves();
            ThinShelvesAreDropped();
            OnlyTheHeaviestShelvesSurvive();
            ShelvesComeBackInPriceOrder();
            ShelfPeakTieKeepsTheLowerPrice();
            ShelfVolumeIsTheSumOfItsOwnLevels();

            // Display folding
            FoldedRowsLoseNoVolume();
            FoldedRowsCarryTheFlagsUp();
            RowsAlwaysHoldAtLeastOneTick();

            // The clock
            TheTradeDateRollsAtFivePm();
            TradeDatesAreIdempotent();
            TheSundayReopenBelongsToMondaysWeek();
            TheMonthFollowsTheTradeDateNotTheStamp();
            CalendarPeriodsTileWithoutOverlapOrGap();
            FridayCloseAndSundayOpenAreDifferentTradeDates();
            OnlyTheCalendarKindsHaveAKey();

            if (_failures == 0)
            {
                Console.WriteLine("All Ocean Developed tests passed.");
                return 0;
            }

            Console.WriteLine(_failures + " test(s) FAILED.");
            return 1;
        }

        #region Building

        private static void AccumulationAddsUpAcrossBars()
        {
            var builder = new ProfileBuilder();

            // The same price touched by three bars is one level carrying all of it.
            builder.Add(100.00m, 10m, 6m, 4m);
            builder.Add(100.25m, 5m, 1m, 4m);
            builder.Add(100.00m, 7m, 2m, 5m);
            builder.Add(100.00m, 3m, 0m, 3m);

            var profile = builder.Build(Tick, 1000);

            Check("three bars at one price accumulate", profile.Levels[0].Volume == 20m);
            Check("the bid side accumulates", profile.Levels[0].Bid == 8m);
            Check("the ask side accumulates", profile.Levels[0].Ask == 12m);
            Check("delta is ask minus bid", profile.Levels[0].Delta == 4m);
            Check("the second price is untouched", profile.Levels[1].Volume == 5m);
        }

        private static void UntradedTicksBecomeRealZeros()
        {
            var builder = new ProfileBuilder();
            builder.Add(100.00m, 10m, 5m, 5m);
            builder.Add(101.00m, 10m, 5m, 5m);   // four ticks higher, nothing in between

            var profile = builder.Build(Tick, 1000);

            Check("the ladder spans the whole range", profile.Count == 5);
            Check("the low price is the lowest that traded", profile.LowPrice == 100.00m);
            Check("the high price is the highest that traded", profile.HighPrice == 101.00m);

            // The gap is the point: a sparse ladder would put 101.00 at index 1 and every
            // index-based comparison after it on the wrong price.
            Check("untraded ticks are present as zeros",
                  profile.Levels[1].Volume == 0m && profile.Levels[2].Volume == 0m
                  && profile.Levels[3].Volume == 0m);

            Check("index maths round-trips", profile.PriceAt(profile.IndexOfPrice(100.75m)) == 100.75m);
        }

        private static void AnEmptyProfileIsRefused()
        {
            Check("nothing traded is refused", new ProfileBuilder().Build(Tick, 1000) == null);

            var builder = new ProfileBuilder();
            builder.Add(100m, 1m, 0m, 1m);

            Check("a nonsense tick size is refused", builder.Build(0m, 1000) == null);
            Check("a negative tick size is refused", builder.Build(-0.25m, 1000) == null);
        }

        private static void AnOverlyWideProfileIsRefusedNotTruncated()
        {
            var builder = new ProfileBuilder();
            builder.Add(100.00m, 10m, 5m, 5m);
            builder.Add(200.00m, 10m, 5m, 5m);   // 401 ticks apart

            Check("a profile wider than the cap is refused", builder.Build(Tick, 100) == null);

            // Refused, not clipped: a clipped ladder can report a point of control at a price
            // the period never reached and look entirely reasonable doing it.
            var ok = builder.Build(Tick, 1000);
            Check("the same profile builds under a workable cap", ok != null && ok.Count == 401);
        }

        private static void PointOfControlIsTheHeaviestLevel()
        {
            var profile = Shape(SyntheticVolumes(120));

            var heaviest = 0;
            for (var i = 1; i < profile.Count; i++)
            {
                if (profile.Levels[i].Volume > profile.Levels[heaviest].Volume) heaviest = i;
            }

            Check("the point of control is the busiest price", profile.PocIndex == heaviest);
            Check("its volume is the reported maximum",
                  profile.MaxLevelVolume == profile.Levels[heaviest].Volume);
        }

        private static void PointOfControlTieKeepsTheLowerPrice()
        {
            // Two levels dead equal, and both the busiest. A point of control that flickers
            // between them as ticks land is worse than a fixed, arbitrary choice.
            var profile = Shape(new decimal[] { 1m, 50m, 3m, 50m, 2m });

            Check("a tied point of control keeps the lower price", profile.PocIndex == 1);
        }

        private static void TotalsMatchABruteForceScan()
        {
            var profile = Shape(SyntheticVolumes(90));

            decimal volume = 0m, bid = 0m, ask = 0m;
            for (var i = 0; i < profile.Count; i++)
            {
                volume += profile.Levels[i].Volume;
                bid += profile.Levels[i].Bid;
                ask += profile.Levels[i].Ask;
            }

            Check("total volume matches a hand scan", profile.TotalVolume == volume);
            Check("total delta matches a hand scan", profile.TotalDelta == ask - bid);
        }

        #endregion

        #region Merging

        /// <summary>
        /// The load-bearing claim behind the whole design: a week is the sum of its days, so
        /// the day can be the unit of account and nothing is approximated by reusing it.
        /// </summary>
        private static void MergingDaysEqualsBuildingFromTheSameTrades()
        {
            var trades = new decimal[][]
            {
                //  price      vol   bid   ask   which day
                new[] { 100.00m, 10m, 6m, 4m, 0m },
                new[] { 100.50m, 20m, 8m, 12m, 0m },
                new[] { 100.25m, 5m, 5m, 0m, 1m },
                new[] { 100.50m, 30m, 10m, 20m, 1m },
                new[] { 101.00m, 15m, 7m, 8m, 1m },
                new[] { 99.50m, 40m, 20m, 20m, 2m },
                new[] { 100.50m, 1m, 0m, 1m, 2m }
            };

            var whole = new ProfileBuilder();
            var days = new[] { new ProfileBuilder(), new ProfileBuilder(), new ProfileBuilder() };

            foreach (var t in trades)
            {
                whole.Add(t[0], t[1], t[2], t[3]);
                days[(int)t[4]].Add(t[0], t[1], t[2], t[3]);
            }

            var direct = whole.Build(Tick, 10000);
            var parts = new List<Profile>();
            for (var i = 0; i < days.Length; i++) parts.Add(days[i].Build(Tick, 10000));

            var merged = DevelopedMath.Merge(parts, Tick, 10000);

            Check("the summed ladder starts at the same price", merged.LowPrice == direct.LowPrice);
            Check("the summed ladder is the same length", merged.Count == direct.Count);
            Check("summing days gives the same total", merged.TotalVolume == direct.TotalVolume);
            Check("summing days gives the same delta", merged.TotalDelta == direct.TotalDelta);
            Check("summing days gives the same point of control", merged.PocIndex == direct.PocIndex);

            var identical = true;
            for (var i = 0; i < direct.Count; i++)
            {
                if (merged.Levels[i].Volume == direct.Levels[i].Volume
                    && merged.Levels[i].Bid == direct.Levels[i].Bid
                    && merged.Levels[i].Ask == direct.Levels[i].Ask) continue;

                identical = false;
                break;
            }

            Check("every level of the sum matches the direct build", identical);
        }

        private static void MergingAlignsLaddersThatDoNotOverlap()
        {
            // Two days that never traded at the same price at all. The merged ladder has to
            // span both AND leave real zeros in the air between them.
            var low = new ProfileBuilder();
            low.Add(100.00m, 10m, 5m, 5m);
            low.Add(100.25m, 10m, 5m, 5m);

            var high = new ProfileBuilder();
            high.Add(101.00m, 7m, 3m, 4m);

            var merged = DevelopedMath.Merge(
                new List<Profile> { low.Build(Tick, 1000), high.Build(Tick, 1000) }, Tick, 1000);

            Check("the sum spans both days", merged.Count == 5 && merged.LowPrice == 100.00m);
            Check("the lower day landed at its own prices",
                  merged.Levels[0].Volume == 10m && merged.Levels[1].Volume == 10m);
            Check("the higher day landed at its own price", merged.Levels[4].Volume == 7m);
            Check("the air between them is zero",
                  merged.Levels[2].Volume == 0m && merged.Levels[3].Volume == 0m);
            Check("the sum totals both days", merged.TotalVolume == 27m);
        }

        private static void MergingCarriesIncompletenessUpward()
        {
            var a = new ProfileBuilder();
            a.Add(100m, 10m, 5m, 5m);

            var b = new ProfileBuilder();
            b.Add(100m, 10m, 5m, 5m);

            var good = a.Build(Tick, 1000);
            var short_ = b.Build(Tick, 1000);
            short_.Complete = false;

            var merged = DevelopedMath.Merge(new List<Profile> { good, short_ }, Tick, 1000);

            // One half-recorded day poisons the week that contains it, and it has to say so --
            // a week missing Tuesday still produces a confident point of control.
            Check("one incomplete day makes the sum incomplete", !merged.Complete);

            var clean = DevelopedMath.Merge(new List<Profile> { good, good }, Tick, 1000);
            Check("all-complete days stay complete", clean.Complete);
        }

        private static void MergingNothingIsRefused()
        {
            Check("merging null is refused", DevelopedMath.Merge(null, Tick, 1000) == null);
            Check("merging an empty list is refused",
                  DevelopedMath.Merge(new List<Profile>(), Tick, 1000) == null);
            Check("merging only nulls is refused",
                  DevelopedMath.Merge(new List<Profile> { null, null }, Tick, 1000) == null);

            var builder = new ProfileBuilder();
            builder.Add(100m, 1m, 0m, 1m);
            builder.Add(200m, 1m, 0m, 1m);

            Check("a sum wider than the cap is refused",
                  DevelopedMath.Merge(new List<Profile> { builder.Build(Tick, 5000) }, Tick, 10) == null);
        }

        private static void MergingCountsTheDaysItSummed()
        {
            var parts = new List<Profile>();
            for (var i = 0; i < 5; i++)
            {
                var builder = new ProfileBuilder();
                builder.Days = 1;
                builder.Add(100m + i * Tick, 10m, 5m, 5m);
                parts.Add(builder.Build(Tick, 1000));
            }

            var merged = DevelopedMath.Merge(parts, Tick, 1000);
            Check("a five day window says it holds five days", merged.Days == 5);
        }

        #endregion

        #region Value area

        private static void ValueAreaContainsThePocAndEnoughVolume()
        {
            var profile = Shape(SyntheticVolumes(140));
            DevelopedMath.ComputeValueArea(profile, 70m);

            Check("the value area holds the point of control",
                  profile.ValIndex <= profile.PocIndex && profile.PocIndex <= profile.VahIndex);

            var inside = 0m;
            for (var i = profile.ValIndex; i <= profile.VahIndex; i++) inside += profile.Levels[i].Volume;

            Check("the value area reaches its target", inside >= profile.TotalVolume * 0.70m);

            // And is not simply the whole profile: 70% of a humped distribution is a band.
            Check("the value area is narrower than the range",
                  profile.VahIndex - profile.ValIndex + 1 < profile.Count);
        }

        private static void ValueAreaIsContiguousForAnyProfile()
        {
            // An invariant, checked over many shapes rather than one: the value area is a band
            // around the point of control, never two disconnected pieces.
            for (var seed = 1; seed <= 40; seed++)
            {
                var profile = Shape(RandomVolumes(60 + seed, seed));
                DevelopedMath.ComputeValueArea(profile, 70m);

                var ok = profile.ValIndex >= 0
                      && profile.VahIndex < profile.Count
                      && profile.ValIndex <= profile.PocIndex
                      && profile.PocIndex <= profile.VahIndex;

                if (ok) continue;

                Check("value area stays a band around the poc (seed " + seed + ")", false);
                return;
            }

            Check("value area stays a band around the poc, over 40 shapes", true);
        }

        /// <summary>
        /// Hand-worked, because containment and contiguity alone do not pin down WHICH WAY the
        /// band grows -- a value area that expands toward the thinner side is still a
        /// contiguous band holding the point of control and 70% of the volume, and every
        /// invariant above passes while the levels sit in the wrong place.
        /// </summary>
        private static void ValueAreaGrowsTowardTheHeavierSide()
        {
            //                                 0   1    2     3    4    5   6
            var profile = Shape(new decimal[] { 1m, 1m, 100m, 50m, 50m, 1m, 1m });

            // Total 204, target 142.8. From the point of control at index 2, the pair above
            // carries 100 and the pair below carries 2, so the band has to open upward and
            // stop: 100 + 100 = 200 clears the target in one step.
            DevelopedMath.ComputeValueArea(profile, 70m);

            Check("the value area low is the point of control itself", profile.ValIndex == 2);
            Check("the value area high is two ticks up", profile.VahIndex == 4);

            // The thin side must NOT have been taken in: reaching down to index 0 would add
            // two ticks that carry one lot each.
            Check("the thin side is left outside", profile.ValIndex != 0);
        }

        private static void ValueAreaOfOneHundredPercentCoversEverything()
        {
            var profile = Shape(SyntheticVolumes(50));
            DevelopedMath.ComputeValueArea(profile, 100m);

            Check("a 100% value area is the whole ladder",
                  profile.ValIndex == 0 && profile.VahIndex == profile.Count - 1);
        }

        #endregion

        #region High volume shelves

        /// <summary>
        /// Brute force: with no bridging and no minimum width, a shelf is exactly a maximal run
        /// of levels at or above the floor. That reference is written here from the definition,
        /// not from the implementation.
        /// </summary>
        private static void ShelvesAreTheRunsAboveTheFloor()
        {
            var profile = Shape(SyntheticVolumes(150));
            var floor = profile.MaxLevelVolume * 60m / 100m;

            var expected = new List<int[]>();
            var from = -1;

            for (var i = 0; i < profile.Count; i++)
            {
                var over = profile.Levels[i].Volume >= floor && profile.Levels[i].Volume > 0m;

                if (over && from < 0) from = i;
                else if (!over && from >= 0) { expected.Add(new[] { from, i - 1 }); from = -1; }
            }

            if (from >= 0) expected.Add(new[] { from, profile.Count - 1 });

            var zones = DevelopedMath.FindHvnZones(profile, 60m, 1, 0, 99);

            Check("the shelf count matches a brute-force scan", zones.Length == expected.Count);

            var same = zones.Length == expected.Count;
            for (var i = 0; i < zones.Length && same; i++)
            {
                same = zones[i].FromIndex == expected[i][0] && zones[i].ToIndex == expected[i][1];
            }

            Check("every shelf spans the same run as the scan", same);
        }

        private static void AThinDipInsideAShelfIsBridged()
        {
            //                            0    1    2   3    4
            var profile = Shape(new decimal[] { 100m, 100m, 5m, 100m, 100m });

            var split = DevelopedMath.FindHvnZones(profile, 50m, 1, 0, 99);
            Check("without bridging one dip reports two shelves", split.Length == 2);

            var joined = DevelopedMath.FindHvnZones(profile, 50m, 1, 1, 99);
            Check("bridging one tick reports one shelf", joined.Length == 1);
            Check("the bridged shelf spans the dip",
                  joined.Length == 1 && joined[0].FromIndex == 0 && joined[0].ToIndex == 4);

            // And the dip's own volume is inside the shelf's total -- the shelf is the band,
            // not just the qualifying levels in it.
            Check("the bridged shelf counts the dip's volume",
                  joined.Length == 1 && joined[0].Volume == 405m);
        }

        private static void AWideGapSplitsTwoShelves()
        {
            //                            0    1    2   3   4   5    6
            var profile = Shape(new decimal[] { 100m, 100m, 5m, 5m, 5m, 100m, 100m });

            var under = DevelopedMath.FindHvnZones(profile, 50m, 1, 2, 99);
            Check("a three tick gap is not bridged by two", under.Length == 2);

            var over = DevelopedMath.FindHvnZones(profile, 50m, 1, 3, 99);
            Check("a three tick gap is bridged by three", over.Length == 1);
        }

        private static void ThinShelvesAreDropped()
        {
            //                            0   1     2   3     4     5     6   7
            var profile = Shape(new decimal[] { 5m, 100m, 5m, 100m, 100m, 100m, 5m, 5m });

            var all = DevelopedMath.FindHvnZones(profile, 50m, 1, 0, 99);
            Check("without a minimum both runs are shelves", all.Length == 2);

            var wide = DevelopedMath.FindHvnZones(profile, 50m, 3, 0, 99);
            Check("a one tick print is not a shelf", wide.Length == 1);
            Check("the surviving shelf is the wide one",
                  wide.Length == 1 && wide[0].FromIndex == 3 && wide[0].ToIndex == 5);
        }

        private static void OnlyTheHeaviestShelvesSurvive()
        {
            // Three shelves of clearly different weight, separated by real gaps. The LIGHTEST
            // sits in the middle by price on purpose: with it at one end, trimming by weight
            // and simply trimming the tail of the list give the same answer and the test
            // passes either way without checking anything.
            var volumes = new decimal[]
            {
                100m, 100m, 100m, 1m, 1m, 1m,   // 300, lowest prices
                60m, 60m, 1m, 1m, 1m,           // 120, in the middle
                90m, 90m                        // 180, highest prices
            };

            var profile = Shape(volumes);

            var all = DevelopedMath.FindHvnZones(profile, 50m, 1, 0, 99);
            Check("all three shelves are found", all.Length == 3);

            var top = DevelopedMath.FindHvnZones(profile, 50m, 1, 0, 2);
            Check("only two shelves are kept", top.Length == 2);

            // The 120 shelf is the one that must go, and it is not the last in price order.
            var kept = top.Length == 2 && top[0].Volume == 300m && top[1].Volume == 180m;
            Check("the lightest shelf is the one dropped, wherever it sits", kept);
        }

        private static void ShelvesComeBackInPriceOrder()
        {
            // The heaviest shelf is at the TOP of the range, so weight order and price order
            // disagree -- otherwise the final sort could be missing entirely and this would
            // still pass.
            var volumes = new decimal[]
            {
                90m, 90m, 1m, 1m, 1m,            // 180, lowest prices
                60m, 60m, 1m, 1m, 1m,            // 120
                100m, 100m, 100m                 // 300, highest prices
            };

            var zones = DevelopedMath.FindHvnZones(Shape(volumes), 50m, 1, 0, 2);

            // Kept by weight, but handed back lowest price first, so the draw order does not
            // shuffle from frame to frame as volume moves between them.
            Check("two shelves survive the trim", zones.Length == 2);
            Check("the kept shelves come back lowest first",
                  zones.Length == 2 && zones[0].FromIndex < zones[1].FromIndex);
            Check("and the lighter of the two is the one drawn first",
                  zones.Length == 2 && zones[0].Volume == 180m && zones[1].Volume == 300m);
        }

        private static void ShelfPeakTieKeepsTheLowerPrice()
        {
            var profile = Shape(new decimal[] { 100m, 80m, 100m });
            var zones = DevelopedMath.FindHvnZones(profile, 50m, 1, 0, 99);

            Check("the shelf covers all three", zones.Length == 1 && zones[0].Ticks == 3);
            Check("a tied peak keeps the lower price",
                  zones.Length == 1 && zones[0].PeakPrice == profile.PriceAt(0));
        }

        private static void ShelfVolumeIsTheSumOfItsOwnLevels()
        {
            var profile = Shape(SyntheticVolumes(120));
            var zones = DevelopedMath.FindHvnZones(profile, 55m, 2, 2, 10);

            var ok = zones.Length > 0;

            for (var z = 0; z < zones.Length; z++)
            {
                var total = 0m;
                var peak = 0m;

                for (var i = zones[z].FromIndex; i <= zones[z].ToIndex; i++)
                {
                    total += profile.Levels[i].Volume;
                    if (profile.Levels[i].Volume > peak) peak = profile.Levels[i].Volume;
                }

                if (zones[z].Volume == total && zones[z].PeakVolume == peak
                    && zones[z].LowPrice == profile.PriceAt(zones[z].FromIndex)
                    && zones[z].HighPrice == profile.PriceAt(zones[z].ToIndex)) continue;

                ok = false;
                break;
            }

            Check("each shelf reports its own totals and prices", ok);
        }

        #endregion

        #region Display folding

        private static void FoldedRowsLoseNoVolume()
        {
            var profile = Shape(SyntheticVolumes(103));   // deliberately not a round multiple

            for (var ticks = 1; ticks <= 8; ticks++)
            {
                var rows = DevelopedMath.BuildView(profile, ticks, null);

                var total = 0m;
                for (var i = 0; i < rows.Length; i++) total += rows[i].Volume;

                if (total == profile.TotalVolume) continue;

                Check("folding " + ticks + " ticks per row keeps every trade", false);
                return;
            }

            Check("folding loses and duplicates nothing, 1 to 8 ticks per row", true);
        }

        private static void FoldedRowsCarryTheFlagsUp()
        {
            var profile = Shape(SyntheticVolumes(80));
            DevelopedMath.ComputeValueArea(profile, 70m);

            var zones = DevelopedMath.FindHvnZones(profile, 60m, 1, 1, 5);
            var rows = DevelopedMath.BuildView(profile, 3, zones);

            var pocRows = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i].HasPoc) pocRows++;
            }

            Check("exactly one drawn row holds the point of control", pocRows == 1);

            // Flags come from the full-resolution ladder, so a row is inside a shelf when ANY
            // of its ticks is -- the analysis is never done on the folded rows.
            var ok = true;
            for (var i = 0; i < rows.Length && ok; i++)
            {
                var shouldHvn = false;
                var shouldVa = false;

                for (var t = rows[i].FromIndex; t <= rows[i].ToIndex; t++)
                {
                    if (profile.ValIndex >= 0 && t >= profile.ValIndex && t <= profile.VahIndex)
                        shouldVa = true;

                    for (var z = 0; z < zones.Length; z++)
                    {
                        if (t < zones[z].FromIndex || t > zones[z].ToIndex) continue;
                        shouldHvn = true;
                        break;
                    }
                }

                ok = rows[i].InHvn == shouldHvn && rows[i].InValueArea == shouldVa;
            }

            Check("shelf and value area flags survive folding", ok);
        }

        private static void RowsAlwaysHoldAtLeastOneTick()
        {
            Check("a huge row height still folds at least one tick",
                  DevelopedMath.TicksPerRow(0.01m, 40) >= 1);
            Check("a zoomed in chart folds nothing", DevelopedMath.TicksPerRow(20m, 2) == 1);
            Check("a nonsense row height is survivable", DevelopedMath.TicksPerRow(0m, 2) == 1);
            Check("four ticks fit a two pixel minimum at half a pixel each",
                  DevelopedMath.TicksPerRow(0.5m, 2) == 4);
        }

        #endregion

        #region The clock

        private static void TheTradeDateRollsAtFivePm()
        {
            var monday = new DateTime(2026, 9, 7);

            Check("mid-session is its own trade date",
                  DevelopedClock.TradeDate(monday.AddHours(10)) == monday);
            Check("the cash close is still the same trade date",
                  DevelopedClock.TradeDate(monday.AddHours(15)) == monday);
            Check("4:59 PM is still the same trade date",
                  DevelopedClock.TradeDate(monday.AddHours(16).AddMinutes(59)) == monday);

            // 5 PM is the reopen: those bars belong to the session that settles tomorrow.
            Check("5 PM starts the next trade date",
                  DevelopedClock.TradeDate(monday.AddHours(17)) == monday.AddDays(1));
            Check("11 PM belongs to the next trade date",
                  DevelopedClock.TradeDate(monday.AddHours(23)) == monday.AddDays(1));

            // Cutting at midnight instead would split this pair. It must not.
            var before = DevelopedClock.TradeDate(monday.AddHours(23).AddMinutes(30));
            var after = DevelopedClock.TradeDate(monday.AddDays(1).AddHours(2));
            Check("the hours either side of midnight are one trade date", before == after);

            Check("a trade date opens at 5 PM the day before",
                  DevelopedClock.OpenOf(monday) == monday.AddDays(-1).AddHours(17));
            Check("a trade date closes at its own 5 PM",
                  DevelopedClock.CloseOf(monday) == monday.AddHours(17));
        }

        private static void TradeDatesAreIdempotent()
        {
            var start = new DateTime(2026, 3, 2, 0, 0, 0);

            for (var minutes = 0; minutes < 60 * 24 * 9; minutes += 7)
            {
                var at = start.AddMinutes(minutes);
                var date = DevelopedClock.TradeDate(at);

                // Every instant lands inside the window its own trade date defines. That is
                // what makes the fold a partition rather than an overlapping mess.
                if (at >= DevelopedClock.OpenOf(date) && at < DevelopedClock.CloseOf(date)) continue;

                Check("every instant sits inside its own trade date window, at " + at, false);
                return;
            }

            Check("every instant sits inside its own trade date window, over nine days", true);
        }

        private static void TheSundayReopenBelongsToMondaysWeek()
        {
            var sunday = new DateTime(2026, 9, 6);          // a Sunday
            var monday = new DateTime(2026, 9, 7);
            var friday = new DateTime(2026, 9, 11);

            var reopen = DevelopedClock.TradeDate(sunday.AddHours(17));
            Check("the Sunday reopen is Monday's trade date", reopen == monday);

            Check("that trade date is in Monday's week", DevelopedClock.WeekOf(reopen) == monday);
            Check("Friday is in the same week", DevelopedClock.WeekOf(friday) == monday);
            Check("the next Monday starts a new week",
                  DevelopedClock.WeekOf(monday.AddDays(7)) == monday.AddDays(7));

            // Every trade date of the week keys to the same Monday -- the grouping key IS the
            // week, so this is what makes one profile out of five days.
            var same = true;
            for (var i = 0; i < 5; i++)
            {
                if (DevelopedClock.WeekOf(monday.AddDays(i)) == monday) continue;
                same = false;
            }

            Check("all five trade dates key to one week", same);
        }

        private static void TheMonthFollowsTheTradeDateNotTheStamp()
        {
            // 30 September at 6 PM is stamped in September but trades into October's first
            // session. The month it belongs to follows the trade date.
            var lateSeptember = new DateTime(2026, 9, 30, 18, 0, 0);
            var tradeDate = DevelopedClock.TradeDate(lateSeptember);

            Check("the last evening of the month rolls into the next day",
                  tradeDate == new DateTime(2026, 10, 1));
            Check("and so into the next month",
                  DevelopedClock.MonthOf(tradeDate) == new DateTime(2026, 10, 1));

            Check("a mid-month date keys to its own month",
                  DevelopedClock.MonthOf(new DateTime(2026, 10, 15)) == new DateTime(2026, 10, 1));
        }

        private static void CalendarPeriodsTileWithoutOverlapOrGap()
        {
            // One period's end is the next one's start, exactly. A gap loses trades and an
            // overlap counts them twice, and both look fine on the chart.
            var week = new DateTime(2026, 9, 7);   // a Monday
            Check("weeks tile",
                  DevelopedClock.EndInstant(week, PeriodKind.PrevWeek)
                  == DevelopedClock.StartInstant(week.AddDays(7), PeriodKind.PrevWeek));

            var month = new DateTime(2026, 9, 1);
            Check("months tile",
                  DevelopedClock.EndInstant(month, PeriodKind.PrevMonth)
                  == DevelopedClock.StartInstant(month.AddMonths(1), PeriodKind.PrevMonth));

            var day = new DateTime(2026, 9, 8);
            Check("days tile",
                  DevelopedClock.EndInstant(day, PeriodKind.PrevDay)
                  == DevelopedClock.StartInstant(day.AddDays(1), PeriodKind.PrevDay));

            // And a week actually spans its own five sessions. The boundary runs to the NEXT
            // Sunday reopen rather than to Friday's close: the weekend holds no trade dates, so
            // both contain the same sessions, and running to the reopen is what makes the
            // periods tile. What has to be true is that Friday's close falls inside.
            Check("a week opens at Sunday 5 PM",
                  DevelopedClock.StartInstant(week, PeriodKind.PrevWeek)
                  == week.AddDays(-1).AddHours(17));

            Check("a week runs to the next Sunday reopen",
                  DevelopedClock.EndInstant(week, PeriodKind.PrevWeek)
                  == week.AddDays(6).AddHours(17));

            Check("Friday's close is inside the week",
                  DevelopedClock.CloseOf(week.AddDays(4))
                  < DevelopedClock.EndInstant(week, PeriodKind.PrevWeek));

            // Every trade date of the week lands inside the week's own window, and none of the
            // next week's does. That is the property the tiling exists for.
            var contained = true;
            for (var i = 0; i < 5; i++)
            {
                var close = DevelopedClock.CloseOf(week.AddDays(i));

                if (close > DevelopedClock.StartInstant(week, PeriodKind.PrevWeek)
                    && close <= DevelopedClock.EndInstant(week, PeriodKind.PrevWeek)) continue;

                contained = false;
            }

            Check("all five sessions fall inside the week's window", contained);

            Check("next Monday's session does not",
                  DevelopedClock.CloseOf(week.AddDays(7))
                  > DevelopedClock.EndInstant(week, PeriodKind.PrevWeek));

            // February, to catch a month-end computed by adding 30 days.
            var february = new DateTime(2026, 2, 1);
            Check("a 28 day month ends on the 28th",
                  DevelopedClock.EndInstant(february, PeriodKind.PrevMonth)
                  == new DateTime(2026, 2, 28).AddHours(17));
        }

        private static void FridayCloseAndSundayOpenAreDifferentTradeDates()
        {
            var friday = new DateTime(2026, 9, 11, 15, 30, 0);
            var sunday = new DateTime(2026, 9, 13, 17, 30, 0);

            var a = DevelopedClock.TradeDate(friday);
            var b = DevelopedClock.TradeDate(sunday);

            Check("Friday afternoon is Friday's trade date", a == new DateTime(2026, 9, 11));
            Check("Sunday evening is Monday's trade date", b == new DateTime(2026, 9, 14));
            Check("the weekend does not produce a Saturday trade date", a != b);

            // The weekend gap is not a period boundary problem: the two dates fall in
            // different weeks, which is exactly what a weekly profile needs.
            Check("they fall in different weeks", DevelopedClock.WeekOf(a) != DevelopedClock.WeekOf(b));
        }

        private static void OnlyTheCalendarKindsHaveAKey()
        {
            Check("the day is calendar cut", DevelopedClock.IsCalendar(PeriodKind.PrevDay));
            Check("the week is calendar cut", DevelopedClock.IsCalendar(PeriodKind.PrevWeek));
            Check("the month is calendar cut", DevelopedClock.IsCalendar(PeriodKind.PrevMonth));
            Check("rolling A is not", !DevelopedClock.IsCalendar(PeriodKind.RollingA));
            Check("rolling B is not", !DevelopedClock.IsCalendar(PeriodKind.RollingB));

            var date = new DateTime(2026, 9, 9);   // a Wednesday
            Check("the day keys to itself", DevelopedClock.KeyOf(date, PeriodKind.PrevDay) == date);
            Check("the week keys to its Monday",
                  DevelopedClock.KeyOf(date, PeriodKind.PrevWeek) == new DateTime(2026, 9, 7));
            Check("the month keys to the first",
                  DevelopedClock.KeyOf(date, PeriodKind.PrevMonth) == new DateTime(2026, 9, 1));
        }

        #endregion

        #region Shapes

        /// <summary>A profile built straight from a list of per-tick volumes.</summary>
        private static Profile Shape(decimal[] volumes)
        {
            var builder = new ProfileBuilder();

            for (var i = 0; i < volumes.Length; i++)
            {
                // A one-sided split, so delta is never accidentally zero everywhere.
                builder.Add(100m + Tick * i, volumes[i], volumes[i] * 0.4m, volumes[i] * 0.6m);
            }

            var profile = builder.Build(Tick, 100000);

            if (profile == null || profile.Count != volumes.Length)
                throw new Exception("test shape does not span its own range: " + volumes.Length);

            return profile;
        }

        /// <summary>
        /// A humped distribution with noise and a couple of shelves. It encodes a shape of
        /// market, nothing about how the profile is computed.
        /// </summary>
        private static decimal[] SyntheticVolumes(int count)
        {
            var volumes = new decimal[count];
            var seed = 24680;

            for (var i = 0; i < count; i++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7fffffff;

                var fromMiddle = i - count / 2;
                if (fromMiddle < 0) fromMiddle = -fromMiddle;

                var hump = count / 2 - fromMiddle;
                volumes[i] = hump * 3m + seed % 40;
            }

            return volumes;
        }

        private static decimal[] RandomVolumes(int count, int seedValue)
        {
            var volumes = new decimal[count];
            var seed = seedValue * 7919 + 13;

            for (var i = 0; i < count; i++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7fffffff;
                volumes[i] = seed % 500;
            }

            // At least one level must trade, or there is no profile to speak of.
            volumes[count / 3] += 100m;
            return volumes;
        }

        private static void Check(string what, bool passed)
        {
            if (passed) return;

            _failures++;
            Console.WriteLine("FAIL: " + what);
        }

        #endregion
    }
}
