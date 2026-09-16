using System;
using System.Collections.Generic;
using OceansProfile;

namespace OceansProfile.Tests
{
    /// <summary>
    /// Checks the parts of Ocean Profile that would be wrong quietly: where the point of control
    /// and value area land, which levels count as imbalanced, and which higher-timeframe period
    /// a bar belongs to.
    ///
    /// Every reference here is written independently of the code it checks -- a brute-force
    /// scan, a hand-worked example, or an invariant that must hold for any input.
    /// </summary>
    static class Program
    {
        private const decimal Tick = 0.25m;
        private static int _failures;

        static int Main()
        {
            AccumulationAddsUpAcrossBars();
            UntradedTicksBecomeRealZeros();
            AnEmptyOrImpossibleProfileIsRefused();
            AnOverlyWideProfileIsRefusedNotTruncated();
            PointOfControlIsTheHeaviestLevel();
            TotalsMatchABruteForceScan();
            UnfinishedBusinessIsBothSidedExtremes();

            ValueAreaContainsThePocAndEnoughVolume();
            ValueAreaIsContiguousForAnyProfile();
            ValueAreaOfOneHundredPercentCoversEverything();

            HighVolumeNodesArePeaks();
            LowVolumeNodesAreThinTroughsInsideTheRange();
            AbsorptionNeedsSizeAndBalance();

            BuyImbalanceLooksDiagonallyDown();
            SellImbalanceLooksDiagonallyUp();
            SmallLevelsAreNotImbalances();
            StacksGroupConsecutiveSameSideRuns();

            AbsorptionRunsBecomeOneLevelEach();
            ScalesAreMeasuredAgainstTheProfileItself();
            MergingSumsVolumeAndCarriesFlagsUp();
            MergingLeavesTheAnalysisAtTickResolution();

            ClockPeriodsFloorCorrectly();
            TheFuturesDayRollsAtFivePm();
            CashAndOvernightTileTheDay();
            EveryPeriodIsIdempotentAndOrdered();
            OnlyCoarsePeriodsNeedTheTimeZone();

            if (_failures == 0)
            {
                Console.WriteLine("All Ocean Profile tests passed.");
                return 0;
            }

            Console.WriteLine(_failures + " test(s) FAILED.");
            return 1;
        }

        #region Building

        private static void AccumulationAddsUpAcrossBars()
        {
            var builder = new ProfileBuilder();

            // The same price touched by three different bars is one level carrying all of it.
            builder.Add(29300m, 100m, 60m, 40m, 5);
            builder.Add(29300m, 50m, 20m, 30m, 3);
            builder.Add(29300.25m, 10m, 4m, 6m, 1);

            var profile = builder.Build(Tick, 0);
            if (profile == null) { Check("profile builds", false); return; }

            var index = 0;   // 29300 is the low
            Check("volume adds up", profile.Levels[index].Volume == 150m);
            Check("bid volume adds up", profile.Levels[index].Bid == 80m);
            Check("ask volume adds up", profile.Levels[index].Ask == 70m);
            Check("trades add up", profile.Levels[index].Trades == 8);
            Check("delta is ask minus bid", profile.Levels[index].Delta == -10m);
        }

        private static void UntradedTicksBecomeRealZeros()
        {
            var builder = new ProfileBuilder();
            builder.Add(29300m, 40m, 20m, 20m, 2);
            builder.Add(29301m, 60m, 30m, 30m, 3);   // four ticks higher

            var profile = builder.Build(Tick, 0);
            if (profile == null) { Check("profile builds", false); return; }

            // The gap must be present as zeros. The imbalance rules compare a price against the
            // tick diagonally below it, and a missing row would silently shift that comparison
            // onto the wrong price.
            Check("the dense array spans the range", profile.Count == 5);
            Check("the low is right", profile.PriceAt(0) == 29300m);
            Check("the high is right", profile.PriceAt(4) == 29301m);
            Check("the gap is zeros", profile.Levels[1].Volume == 0m &&
                                     profile.Levels[2].Volume == 0m &&
                                     profile.Levels[3].Volume == 0m);
        }

        private static void AnEmptyOrImpossibleProfileIsRefused()
        {
            Check("an empty builder makes no profile", new ProfileBuilder().Build(Tick, 0) == null);

            var builder = new ProfileBuilder();
            builder.Add(29300m, 10m, 5m, 5m, 1);

            // A zero tick size cannot describe price levels. Guessing one would put every level
            // at the wrong price.
            Check("a zero tick size is refused", builder.Build(0m, 0) == null);
            Check("a negative tick size is refused", builder.Build(-0.25m, 0) == null);
        }

        private static void AnOverlyWideProfileIsRefusedNotTruncated()
        {
            var builder = new ProfileBuilder();
            builder.Add(29000m, 10m, 5m, 5m, 1);
            builder.Add(30000m, 10m, 5m, 5m, 1);   // 4000 ticks apart

            // Truncating would drop levels and could move the point of control somewhere it
            // never was, which looks perfectly plausible on the chart.
            Check("too wide is refused", builder.Build(Tick, 1000) == null);
            Check("within the cap it builds", builder.Build(Tick, 5000) != null);
            Check("no cap builds", builder.Build(Tick, 0) != null);
        }

        private static void PointOfControlIsTheHeaviestLevel()
        {
            var profile = Make(new decimal[] { 10m, 40m, 90m, 25m, 5m });
            Check("the poc is the heaviest level", profile.PocIndex == 2);
            Check("and reports its price", profile.Poc == profile.PriceAt(2));
            Check("the max is recorded", profile.MaxLevelVolume == 90m);

            // A flickering point of control is worse than an arbitrary one, so ties are fixed:
            // the lower price keeps it.
            var tied = Make(new decimal[] { 10m, 90m, 30m, 90m, 5m });
            Check("ties keep the lower price", tied.PocIndex == 1);
        }

        private static void TotalsMatchABruteForceScan()
        {
            var volumes = SyntheticVolumes(180);
            var builder = new ProfileBuilder();

            decimal expectedVolume = 0m, expectedDelta = 0m;

            for (var i = 0; i < volumes.Length; i++)
            {
                var price = 29200m + i * Tick;
                var bid = volumes[i] * 4m / 10m;
                var ask = volumes[i] - bid;

                builder.Add(price, volumes[i], bid, ask, 1);
                expectedVolume += volumes[i];
                expectedDelta += ask - bid;
            }

            var profile = builder.Build(Tick, 0);
            if (profile == null) { Check("profile builds", false); return; }

            Check("total volume matches", profile.TotalVolume == expectedVolume);
            Check("total delta matches", profile.TotalDelta == expectedDelta);

            var summed = 0m;
            for (var i = 0; i < profile.Count; i++) summed += profile.Levels[i].Volume;
            Check("nothing is lost laying out the levels", summed == expectedVolume);
        }

        private static void UnfinishedBusinessIsBothSidedExtremes()
        {
            var builder = new ProfileBuilder();
            builder.Add(29300m, 20m, 20m, 0m, 2);      // low: sellers only -- auction finished
            builder.Add(29300.25m, 50m, 25m, 25m, 4);
            builder.Add(29300.50m, 30m, 10m, 20m, 3);  // high: traded both sides -- unfinished

            var profile = builder.Build(Tick, 0);
            if (profile == null) { Check("profile builds", false); return; }

            Check("a two-sided high is unfinished", profile.UnfinishedHigh);
            Check("a one-sided low is finished", !profile.UnfinishedLow);
        }

        #endregion

        #region Value area

        private static void ValueAreaContainsThePocAndEnoughVolume()
        {
            var profile = Make(new decimal[] { 5m, 10m, 20m, 100m, 30m, 15m, 5m });
            ProfileMath.ComputeValueArea(profile, 70m);

            Check("the value area contains the poc",
                  profile.ValIndex <= profile.PocIndex && profile.PocIndex <= profile.VahIndex);

            var inside = 0m;
            for (var i = profile.ValIndex; i <= profile.VahIndex; i++) inside += profile.Levels[i].Volume;

            Check("the value area really holds 70% of the volume",
                  inside >= profile.TotalVolume * 70m / 100m);
            Check("value area low is below its high", profile.ValueAreaLow <= profile.ValueAreaHigh);
        }

        private static void ValueAreaIsContiguousForAnyProfile()
        {
            // The invariant has to hold for shapes that are not tidy single humps: twin peaks,
            // everything at one price, a flat slab.
            var shapes = new decimal[][]
            {
                new decimal[] { 100m },
                new decimal[] { 50m, 50m, 50m, 50m },
                new decimal[] { 90m, 5m, 5m, 5m, 90m },
                new decimal[] { 1m, 2m, 3m, 400m, 3m, 2m, 1m },
                new decimal[] { 40m, 0m, 0m, 0m, 40m }
            };

            var ok = true;
            for (var s = 0; s < shapes.Length; s++)
            {
                var profile = Make(shapes[s]);
                ProfileMath.ComputeValueArea(profile, 70m);

                if (profile.ValIndex < 0 || profile.VahIndex < 0) { ok = false; continue; }
                if (profile.ValIndex > profile.PocIndex) ok = false;
                if (profile.VahIndex < profile.PocIndex) ok = false;
                if (profile.ValIndex < 0 || profile.VahIndex >= profile.Count) ok = false;

                var inside = 0m;
                for (var i = profile.ValIndex; i <= profile.VahIndex; i++) inside += profile.Levels[i].Volume;

                // Either it reached the target, or it ran out of levels trying.
                var reached = inside >= profile.TotalVolume * 70m / 100m;
                var exhausted = profile.ValIndex == 0 && profile.VahIndex == profile.Count - 1;
                if (!reached && !exhausted) ok = false;
            }

            Check("the value area holds for every profile shape", ok);
        }

        private static void ValueAreaOfOneHundredPercentCoversEverything()
        {
            var profile = Make(new decimal[] { 5m, 10m, 20m, 100m, 30m });
            ProfileMath.ComputeValueArea(profile, 100m);

            Check("100% reaches the bottom", profile.ValIndex == 0);
            Check("100% reaches the top", profile.VahIndex == profile.Count - 1);
        }

        #endregion

        #region Nodes and absorption

        private static void HighVolumeNodesArePeaks()
        {
            var profile = Make(new decimal[] { 10m, 100m, 20m, 30m, 95m, 15m });
            var hvn = ProfileMath.FindHighVolumeNodes(profile, 80m);

            Check("the big peak is a node", hvn[1]);
            Check("the second peak clears the threshold", hvn[4]);
            Check("a mid-sized level is not a node", !hvn[3]);
            Check("a trough is not a node", !hvn[2]);
        }

        private static void LowVolumeNodesAreThinTroughsInsideTheRange()
        {
            // Both edges are deliberately THIN. If the scan ran to the ends it would flag them,
            // so this pins the rule rather than relying on the edges being busy.
            var profile = Make(new decimal[] { 5m, 100m, 3m, 90m, 4m });
            var lvn = ProfileMath.FindLowVolumeNodes(profile, 20m);

            Check("the thin gap between two shelves is a node", lvn[2]);
            Check("a busy level is not a node", !lvn[1]);

            // The empty air past the extremes is not a low volume node -- price never went
            // there at all, which is a different thing from crossing it quickly.
            Check("the bottom edge is never a node", !lvn[0]);
            Check("the top edge is never a node", !lvn[profile.Count - 1]);
        }

        private static void AbsorptionNeedsSizeAndBalance()
        {
            var builder = new ProfileBuilder();

            // Heavy and balanced: something passive was filled here.
            builder.Add(29300m, 1000m, 510m, 490m, 40);
            // Heavy but one-sided: that is aggression walking the price, not absorption.
            builder.Add(29300.25m, 900m, 60m, 840m, 35);
            // Balanced but tiny: true of half the book, and means nothing.
            builder.Add(29300.50m, 20m, 10m, 10m, 2);

            var profile = builder.Build(Tick, 0);
            if (profile == null) { Check("profile builds", false); return; }

            var absorbed = ProfileMath.FindAbsorption(profile, 50m, 0.25m);

            Check("heavy and balanced is absorption", absorbed[0]);
            Check("heavy and one-sided is not", !absorbed[1]);
            Check("small and balanced is not", !absorbed[2]);
        }

        #endregion

        #region Imbalances

        private static void BuyImbalanceLooksDiagonallyDown()
        {
            var builder = new ProfileBuilder();
            builder.Add(29300m, 60m, 10m, 50m, 4);        // sellers on the tick below: 10
            builder.Add(29300.25m, 120m, 40m, 80m, 6);    // buyers here: 80, vs 10 below -> 8x

            // The sellers AT this price are 40. Against those, 80 is only 2x and would not be
            // an imbalance at all -- so this only passes if the diagonal is really being used.

            var profile = builder.Build(Tick, 0);
            var found = ProfileMath.FindImbalances(profile, 3m, 20m);

            Check("one imbalance is found", found.Length == 1);
            Check("it is a buy imbalance", found.Length == 1 && found[0].Side == Side.Buy);
            Check("at the upper price", found.Length == 1 && found[0].Index == 1);

            // Raising the bar past 8x removes it -- the rule is a ratio, not a hunch.
            Check("a stricter ratio drops it",
                  ProfileMath.FindImbalances(profile, 9m, 20m).Length == 0);
        }

        private static void SellImbalanceLooksDiagonallyUp()
        {
            var builder = new ProfileBuilder();
            builder.Add(29300m, 100m, 90m, 10m, 6);       // sellers here: 90
            builder.Add(29300.25m, 60m, 50m, 10m, 4);     // buyers above: 10 -> 9x

            var profile = builder.Build(Tick, 0);
            var found = ProfileMath.FindImbalances(profile, 3m, 20m);

            var sell = -1;
            for (var i = 0; i < found.Length; i++) if (found[i].Side == Side.Sell) sell = i;

            Check("a sell imbalance is found", sell >= 0);
            Check("at the lower price", sell >= 0 && found[sell].Index == 0);
        }

        private static void SmallLevelsAreNotImbalances()
        {
            var builder = new ProfileBuilder();
            builder.Add(29300m, 2m, 1m, 1m, 1);
            builder.Add(29300.25m, 7m, 1m, 6m, 1);    // 6 against 1 is 6x, but on six lots

            var profile = builder.Build(Tick, 0);

            Check("a six-lot imbalance is filtered out",
                  ProfileMath.FindImbalances(profile, 3m, 20m).Length == 0);
            Check("with no minimum it would be reported",
                  ProfileMath.FindImbalances(profile, 3m, 0m).Length > 0);
        }

        private static void StacksGroupConsecutiveSameSideRuns()
        {
            var imbalances = new Imbalance[]
            {
                Imb(4, Side.Buy), Imb(5, Side.Buy), Imb(6, Side.Buy),   // a run of three
                Imb(8, Side.Buy),                                       // gap breaks it
                Imb(9, Side.Sell), Imb(10, Side.Sell)                   // side change breaks it
            };

            var stacks = ProfileMath.FindStacks(imbalances, 3);

            Check("only the run of three is a stack", stacks.Length == 1);
            Check("it spans the right levels",
                  stacks.Length == 1 && stacks[0].From == 4 && stacks[0].To == 6);
            Check("and knows its side", stacks.Length == 1 && stacks[0].Side == Side.Buy);
            Check("and its length", stacks.Length == 1 && stacks[0].Length == 3);

            // A gap of one price is not a stack, however close the two are.
            var loose = ProfileMath.FindStacks(new Imbalance[] { Imb(1, Side.Buy), Imb(3, Side.Buy) }, 2);
            Check("a gap is not a stack", loose.Length == 0);

            var pairs = ProfileMath.FindStacks(imbalances, 2);
            Check("a lower minimum finds the sell pair too", pairs.Length == 2);
        }

        #endregion

        #region Key levels and scaling

        private static void AbsorptionRunsBecomeOneLevelEach()
        {
            var profile = Make(new decimal[] { 10m, 50m, 90m, 60m, 10m, 80m });

            var absorbed = new bool[6];
            absorbed[1] = true; absorbed[2] = true; absorbed[3] = true;   // one shelf, three ticks
            absorbed[5] = true;                                          // a separate one

            var levels = ProfileMath.AbsorptionLevels(profile, absorbed);

            // Three adjacent absorbed ticks are one shelf. Three lines across the chart for one
            // shelf is how a useful mark turns into noise.
            Check("adjacent ticks collapse to one level", levels.Length == 2);
            Check("the run is priced at its heaviest tick",
                  levels.Length == 2 && levels[0].Price == profile.PriceAt(2));
            Check("the run carries its whole volume",
                  levels.Length == 2 && levels[0].Volume == 200m);
            Check("the separate shelf is its own level",
                  levels.Length == 2 && levels[1].Price == profile.PriceAt(5));

            Check("nothing absorbed means no levels",
                  ProfileMath.AbsorptionLevels(profile, new bool[6]).Length == 0);
        }

        private static void ScalesAreMeasuredAgainstTheProfileItself()
        {
            var builder = new ProfileBuilder();
            builder.Add(29300m, 1000m, 520m, 480m, 10);    // lean -0.04
            builder.Add(29300.25m, 800m, 320m, 480m, 8);   // lean +0.20  <- the most one-sided
            builder.Add(29300.50m, 6m, 0m, 6m, 1);         // lean +1.00, but on six lots

            var profile = builder.Build(Tick, 0);
            var view = ProfileMath.BuildView(profile, 1, null, null, null, null);

            // A six-lot level is perfectly one-sided and completely meaningless. Letting it set
            // the scale would flatten every real level to nothing.
            Check("tiny levels do not set the colour scale",
                  ProfileMath.MaxLean(view, 100m) == 0.2m);
            Check("with no floor the six-lot level wins",
                  ProfileMath.MaxLean(view, 0m) == 1m);

            Check("the biggest delta is found", ProfileMath.MaxAbsDelta(view) == 160m);
            Check("an empty view has no scale", ProfileMath.MaxAbsDelta(new DisplayRow[0]) == 0m);
        }

        #endregion

        #region Display merging

        private static void MergingSumsVolumeAndCarriesFlagsUp()
        {
            var profile = Make(new decimal[] { 10m, 20m, 30m, 40m, 50m, 60m });

            var absorbed = new bool[6];
            absorbed[3] = true;                       // one tick inside the second row

            var view = ProfileMath.BuildView(profile, 3, absorbed, null, null, null);

            Check("six ticks become two rows of three", view.Length == 2);
            Check("the first row sums its ticks", view[0].Volume == 60m);
            Check("the second row sums its ticks", view[1].Volume == 150m);
            Check("nothing is lost merging",
                  view[0].Volume + view[1].Volume == profile.TotalVolume);

            // A row is flagged when ANY tick in it was: hiding a flagged tick because its
            // neighbours were quiet would lose the signal exactly when the chart is zoomed out.
            Check("a flag on one tick lifts the whole row", view[1].Absorbed);
            Check("a row with no flagged tick stays clear", !view[0].Absorbed);

            Check("the poc row is marked", view[1].HasPoc);
            Check("row prices span their ticks",
                  view[0].LowPrice == profile.PriceAt(0) && view[0].HighPrice == profile.PriceAt(2));
        }

        private static void MergingLeavesTheAnalysisAtTickResolution()
        {
            // Two ticks that would be one row on a zoomed-out chart. Merged first, the buyers
            // and sellers would net off inside the row and the imbalance would vanish.
            var builder = new ProfileBuilder();
            builder.Add(29300m, 60m, 10m, 50m, 4);
            builder.Add(29300.25m, 120m, 40m, 80m, 6);

            var profile = builder.Build(Tick, 0);
            var found = ProfileMath.FindImbalances(profile, 3m, 20m);

            Check("the imbalance is found at tick resolution", found.Length == 1);

            var sides = ProfileMath.StackSides(profile, ProfileMath.FindStacks(found, 1));
            var view = ProfileMath.BuildView(profile, 2, null, null, null, sides);

            Check("both ticks collapse to one row", view.Length == 1);
            Check("and the row still reports the imbalance", view[0].Stacked == Side.Buy);
            Check("the row carries the summed volume", view[0].Volume == 180m);
        }

        #endregion

        #region Period cutting

        private static void ClockPeriodsFloorCorrectly()
        {
            var t = new DateTime(2026, 8, 20, 9, 47, 30);

            Check("15 minutes floors", PeriodClock.StartOf(t, ProfilePeriod.M15) == At(9, 45));
            Check("30 minutes floors", PeriodClock.StartOf(t, ProfilePeriod.M30) == At(9, 30));
            Check("1 hour floors", PeriodClock.StartOf(t, ProfilePeriod.H1) == At(9, 0));
            Check("2 hours floors", PeriodClock.StartOf(t, ProfilePeriod.H2) == At(8, 0));
            Check("4 hours floors", PeriodClock.StartOf(t, ProfilePeriod.H4) == At(8, 0));

            Check("a time exactly on a boundary stays put",
                  PeriodClock.StartOf(At(9, 30), ProfilePeriod.M30) == At(9, 30));
        }

        private static void TheFuturesDayRollsAtFivePm()
        {
            // CME reopens at 5 PM, so 5 PM Thursday is already Friday's trade date.
            Check("just before the roll belongs to the old day",
                  PeriodClock.StartOf(At(16, 59), ProfilePeriod.Day) ==
                  new DateTime(2026, 8, 19, 17, 0, 0));

            Check("the roll starts the new day",
                  PeriodClock.StartOf(At(17, 0), ProfilePeriod.Day) ==
                  new DateTime(2026, 8, 20, 17, 0, 0));

            Check("after midnight still belongs to the evening that opened it",
                  PeriodClock.StartOf(new DateTime(2026, 8, 21, 2, 0, 0), ProfilePeriod.Day) ==
                  new DateTime(2026, 8, 20, 17, 0, 0));

            // Midnight is emphatically not the boundary -- that would cut every overnight
            // session in half and give two profiles where the market saw one.
            Check("midnight is not a day boundary",
                  PeriodClock.StartOf(new DateTime(2026, 8, 21, 0, 0, 0), ProfilePeriod.Day) !=
                  new DateTime(2026, 8, 21, 0, 0, 0));
        }

        private static void CashAndOvernightTileTheDay()
        {
            Check("the cash open starts the cash block",
                  PeriodClock.StartOf(At(8, 30), ProfilePeriod.Rth) == At(8, 30));
            Check("mid-session is in the same block",
                  PeriodClock.StartOf(At(12, 0), ProfilePeriod.Rth) == At(8, 30));
            Check("one minute before the open is overnight",
                  PeriodClock.StartOf(At(8, 29), ProfilePeriod.Rth) ==
                  new DateTime(2026, 8, 19, 15, 0, 0));
            Check("the cash close starts the overnight block",
                  PeriodClock.StartOf(At(15, 0), ProfilePeriod.Rth) == At(15, 0));
            Check("after midnight joins the evening that opened it",
                  PeriodClock.StartOf(new DateTime(2026, 8, 21, 3, 0, 0), ProfilePeriod.Rth) ==
                  At(15, 0));

            Check("the cash session is recognised", PeriodClock.IsCash(At(10, 0)));
            Check("the open is inside it", PeriodClock.IsCash(At(8, 30)));
            Check("the close is outside it", !PeriodClock.IsCash(At(15, 0)));
            Check("the evening is outside it", !PeriodClock.IsCash(At(20, 0)));
        }

        private static void EveryPeriodIsIdempotentAndOrdered()
        {
            var periods = (ProfilePeriod[])Enum.GetValues(typeof(ProfilePeriod));
            var ok = true;
            var ordered = true;

            foreach (var period in periods)
            {
                var previous = DateTime.MinValue;

                // A whole week, minute by minute. Two invariants make the cut a real partition:
                // the start of a period is its own period, and walking forward in time never
                // walks backward through periods.
                for (var t = new DateTime(2026, 8, 17, 0, 0, 0);
                         t < new DateTime(2026, 8, 24, 0, 0, 0);
                         t = t.AddMinutes(1))
                {
                    var start = PeriodClock.StartOf(t, period);

                    if (PeriodClock.StartOf(start, period) != start) ok = false;
                    if (start > t) ok = false;
                    if (start < previous) ordered = false;

                    previous = start;
                }
            }

            Check("every period start is its own period", ok);
            Check("periods never run backwards through time", ordered);
        }

        private static void OnlyCoarsePeriodsNeedTheTimeZone()
        {
            // A whole-hour offset moves these by whole hours, so the cut lands on the same
            // instants either way and they are safe without a resolved bar clock.
            Check("15 minutes is zone independent", !PeriodClock.NeedsTimeZone(ProfilePeriod.M15));
            Check("30 minutes is zone independent", !PeriodClock.NeedsTimeZone(ProfilePeriod.M30));
            Check("1 hour is zone independent", !PeriodClock.NeedsTimeZone(ProfilePeriod.H1));

            // These do not survive a five or six hour shift, so they must not be drawn on a guess.
            Check("2 hours needs the zone", PeriodClock.NeedsTimeZone(ProfilePeriod.H2));
            Check("4 hours needs the zone", PeriodClock.NeedsTimeZone(ProfilePeriod.H4));
            Check("the cash session needs the zone", PeriodClock.NeedsTimeZone(ProfilePeriod.Rth));
            Check("the futures day needs the zone", PeriodClock.NeedsTimeZone(ProfilePeriod.Day));
        }

        #endregion

        #region Helpers


        private static DateTime At(int hour, int minute)
        {
            return new DateTime(2026, 8, 20, hour, minute, 0);
        }

        private static Imbalance Imb(int index, Side side)
        {
            var imbalance = new Imbalance();
            imbalance.Index = index;
            imbalance.Side = side;
            return imbalance;
        }

        /// <summary>A profile with the given volumes, split evenly so delta plays no part.</summary>
        private static Profile Make(decimal[] volumes)
        {
            var builder = new ProfileBuilder();

            for (var i = 0; i < volumes.Length; i++)
            {
                var half = volumes[i] / 2m;
                builder.Add(29300m + i * Tick, volumes[i], half, half, 1);
            }

            // The extremes of a real profile are traded prices by definition, so every shape
            // used here starts and ends with volume and the dense range is the whole array.
            var profile = builder.Build(Tick, 0);
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

        private static void Check(string what, bool passed)
        {
            if (passed) return;

            _failures++;
            Console.WriteLine("FAIL: " + what);
        }

        #endregion
    }
}
