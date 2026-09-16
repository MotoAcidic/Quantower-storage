using System;
using System.Collections.Generic;
using OceansEffort;

namespace OceansEffort.Tests
{
    /// <summary>
    /// Checks the parts of Ocean Effort that would be wrong quietly: where a bar's value area
    /// lands, which direction the last N bars actually cost less to travel, and which bars count
    /// as aggression that achieved nothing.
    ///
    /// Every reference here is worked out independently of the code under test -- by hand, by a
    /// brute-force scan, or as an invariant that has to hold whatever the input.
    /// </summary>
    static class Program
    {
        private const decimal Tick = 0.25m;
        private static int _failures;

        static int Main()
        {
            PocIsTheHeaviestLevel();
            PocTiesKeepTheLowerPrice();
            ValueAreaContainsThePocAndEnoughVolume();
            ValueAreaIsContiguousForAnyShape();
            UntradedTicksInsideABarAreRealZeros();
            AWholeHundredPercentCoversTheBar();
            AnEmptyBarHasNoValueArea();
            AnOverlyWideBarIsRefusedNotTruncated();

            ValueMigratesWhenBothTheAreaAndThePocMove();
            AWideningBarIsNotAMigratingOne();
            SmallShiftsAreNotMigration();
            MigrationNeedsBothBarsToHaveValue();
            MigrationIsSymmetric();

            EffortPricesEachDirectionPerTick();
            EffortNeedsOneSideToBeClearlyCheaper();
            EffortReportsAOneSidedWindowAsSuch();
            EffortSaysNothingWhenNothingMoved();
            BarsWithNoVolumeCannotMakeADirectionFree();
            BarsThatClosedFlatPayForNeitherSide();
            TheCheaperSideIsAlwaysTheOneWithTheLowerCost();

            AbsorptionIsHeavyLopsidedAggressionWithNoResult();
            AggressionThatWorkedIsNotAbsorption();
            ACloseAgainstTheAggressionCountsHowever();
            ABalancedBarIsNotAbsorption();
            ASmallBarIsNotAbsorption();
            WithNoYardstickNothingIsAbsorbed();

            AllThreeLayersMustAgree();
            TheTargetIsExactlyOneR();
            ATooDistantStopIsMarkedNotMoved();
            AbsorbedAggressionIsNotConfirmation();
            AValuelessBarCannotProduceASetup();
            AStopOnTheWrongSideOfEntryIsRefused();

            OnlyOneSetupRunsAtATime();
            ANewSetupCanStartOnceTheStopIsTaken();
            ATradeableSetupReplacesAnUntradeableOne();
            AnUntradeableSetupDoesNotReplaceATradeableOne();
            TheRunEndsAtTheFirstBarThroughTheStop();
            TheTrailStepsOnlyOnAggressionInTheTradesDirection();
            ReplayingTheSameBarChangesNothing();

            TheTrailOnlyEverTightens();
            AnInactiveTrailIgnoresEverything();

            AProfileIsDenseAcrossItsWholeRange();
            AbsorptionClustersAreHeavyAndBalanced();
            AggressionClustersAreHeavyAndOneSided();
            ASingleHeavyPriceIsNotACluster();
            ClustersDoNotStrideAcrossAChangeOfCharacter();
            OnlyTheHeaviestClustersAreKept();
            PriceIsPlacedAgainstValue();
            AClusterIsFoundUnderThePrice();
            AbsorbedSellersMakeADemandLevel();
            PriceInMidAirFavoursNobody();
            VwapIsWeightedByVolume();
            RowsMergeToFitTheScreen();

            LevelsParseWithLabelsAndKinds();
            BadLinesAreCountedNotSwallowed();
            AKindItDoesNotRecogniseIsNotGuessed();
            TheNearestLevelSetsTheBias();
            AWallOverheadRefusesALong();
            AWallTheWrongSideOfPriceIsNotInTheWay();
            ATargetBehindAWallIsRefused();
            TypicalSizeIsMeasuredNotAssumed();

            RotatingInsideValueIsTheCage();
            RotatingOUTSIDEValueIsNotTheCage();
            AMarketThatKeptItsRangeIsATrend();
            KeepingSomeOfItIsNeitherAndSaysSo();
            AnUnknownValueAreaLeavesTheRegimeUnread();
            TheFilterStandsAsideInBalanceAndInTheDark();
            AWindowTooShortToJudgeIsNotJudged();

            ANewLowTheSellingDidNotPayForIsBullish();
            ANewHighTheBuyingDidNotPayForIsBearish();
            ANewLowOnHeavierSellingIsNotDivergence();
            HoldingInsideTheRangeIsNotDivergence();
            ASmallDeltaGapDoesNotCount();
            DivergenceStopsBeyondTheExtremeItJustMade();
            ADivergenceInMidAirIsRefused();

            TheBiggestPrintsAreRankedAcrossEverything();
            AFloorKeepsSmallPrintsOffTheChart();
            RankingIsStableWhenSizesTie();
            TheBiggestDeltaLevelsAreByAbsoluteSize();
            LevelsWithNoDeltaAreNotRanked();

            ASetupMustComeFromALevel();

            TheBoxNamesTheTradeAndItsRisk();
            MoneyAppearsOnlyWhenThePlatformGaveATickValue();
            AnOpenTradeReportsWhereItStands();
            AClosedTradeReportsHowItEnded();
            AnOverCapSetupIsNotPresentedAsATrade();
            AnUnresolvedBarIsNotResolved();
            TheBoxCountsHowManyLayersAgree();
            TheBoxSaysWhenAValueAreaIsUnknown();
            LayerFailuresReachTheBox();

            AverageVolumeIsTheMeanOfTheRange();
            CompactKeepsTheMagnitude();

            if (_failures == 0)
            {
                Console.WriteLine("All Ocean Effort tests passed.");
                return 0;
            }

            Console.WriteLine(_failures + " test(s) FAILED.");
            return 1;
        }

        #region Value area

        private static void PocIsTheHeaviestLevel()
        {
            // Worked by eye: the 40 at 100.50 is the heaviest thing here.
            var levels = Levels(100m, 12m, 9m, 40m, 11m, 7m);

            decimal poc, high, low;
            var ok = EffortMath.ValueArea(levels, Tick, 70m, 4000, out poc, out high, out low);

            Check("point of control is the heaviest level", ok && poc == 100.50m);
        }

        private static void PocTiesKeepTheLowerPrice()
        {
            var levels = Levels(100m, 5m, 30m, 8m, 30m, 5m);

            decimal poc, high, low;
            EffortMath.ValueArea(levels, Tick, 70m, 4000, out poc, out high, out low);

            Check("a tie for the point of control keeps the lower price", poc == 100.25m);
        }

        private static void ValueAreaContainsThePocAndEnoughVolume()
        {
            var volumes = new decimal[] { 4m, 9m, 22m, 60m, 25m, 11m, 3m };
            var levels = Levels(100m, volumes);

            decimal poc, high, low;
            var ok = EffortMath.ValueArea(levels, Tick, 70m, 4000, out poc, out high, out low);

            var total = 0m;
            foreach (var volume in volumes) total += volume;

            // Sum the volume inside the returned range independently of how it was chosen.
            var inside = 0m;
            for (var i = 0; i < volumes.Length; i++)
            {
                var price = 100m + Tick * i;
                if (price >= low && price <= high) inside += volumes[i];
            }

            Check("the value area holds the point of control", ok && poc >= low && poc <= high);
            Check("the value area holds at least the requested share", inside >= total * 0.7m);
        }

        private static void ValueAreaIsContiguousForAnyShape()
        {
            // Contiguity is structural: whatever the shape, the value area is one unbroken band
            // that starts at or below the point of control and ends at or above it.
            var passed = true;

            for (var seed = 1; seed <= 200; seed++)
            {
                var volumes = Noisy(seed, 3 + seed % 25);
                var levels = Levels(100m, volumes);

                decimal poc, high, low;
                if (!EffortMath.ValueArea(levels, Tick, 20m + seed % 80, 4000, out poc, out high, out low))
                {
                    passed = false;
                    break;
                }

                if (low > poc || high < poc || high < low) { passed = false; break; }

                var topIndex = (int)Math.Round((high - 100m) / Tick);
                if (topIndex > volumes.Length - 1) { passed = false; break; }
            }

            Check("the value area is one band around the point of control, whatever the shape", passed);
        }

        private static void UntradedTicksInsideABarAreRealZeros()
        {
            // 100.50 and 100.75 never traded. Laid out densely they are zeros the value area has
            // to walk across; collapsed into a list they would look adjacent and the value area
            // would stop two ticks short of where it belongs.
            var levels = new List<PriceVolume>();
            levels.Add(Level(100.00m, 10m));
            levels.Add(Level(100.25m, 6m));
            levels.Add(Level(101.00m, 7m));
            levels.Add(Level(101.25m, 7m));

            decimal poc, high, low;
            var ok = EffortMath.ValueArea(levels, Tick, 70m, 4000, out poc, out high, out low);

            Check("a gap inside the bar is walked across, not collapsed", ok && high == 101.00m && low == 100.00m);
        }

        private static void AWholeHundredPercentCoversTheBar()
        {
            var levels = Levels(100m, 3m, 1m, 9m, 2m, 4m);

            decimal poc, high, low;
            EffortMath.ValueArea(levels, Tick, 100m, 4000, out poc, out high, out low);

            Check("a hundred percent value area is the whole bar", low == 100m && high == 101m);
        }

        private static void AnEmptyBarHasNoValueArea()
        {
            decimal poc, high, low;

            var none = EffortMath.ValueArea(new List<PriceVolume>(), Tick, 70m, 4000, out poc, out high, out low);
            var zeros = EffortMath.ValueArea(Levels(100m, 0m, 0m, 0m), Tick, 70m, 4000, out poc, out high, out low);
            var noTick = EffortMath.ValueArea(Levels(100m, 5m, 5m), 0m, 70m, 4000, out poc, out high, out low);

            Check("an empty bar reports no value area", !none && !zeros && !noTick);
        }

        private static void AnOverlyWideBarIsRefusedNotTruncated()
        {
            var levels = new List<PriceVolume>();
            levels.Add(Level(100m, 5m));
            levels.Add(Level(200m, 5m));   // 400 ticks apart

            decimal poc, high, low;
            var ok = EffortMath.ValueArea(levels, Tick, 70m, 100, out poc, out high, out low);

            Check("a bar wider than the cap is refused rather than trimmed", !ok && poc == 0m && high == 0m);
        }

        #endregion

        #region Migration

        private static void ValueMigratesWhenBothTheAreaAndThePocMove()
        {
            var previous = Bar(1, value: true, poc: 100.00m, valueLow: 99.50m, valueHigh: 100.50m);
            var current = Bar(2, value: true, poc: 101.00m, valueLow: 100.50m, valueHigh: 101.50m);

            Check("value migrating up is a higher band and a higher point of control",
                  EffortMath.Migration(previous, current, Tick, 2) == Side.Buy);
        }

        private static void AWideningBarIsNotAMigratingOne()
        {
            // The band's midpoint rose, but the heaviest price fell. That is a bar that widened
            // upward while trade concentrated lower -- not the auction accepting higher prices.
            var previous = Bar(1, value: true, poc: 100.00m, valueLow: 99.50m, valueHigh: 100.50m);
            var current = Bar(2, value: true, poc: 99.75m, valueLow: 99.75m, valueHigh: 102.00m);

            Check("a bar that widened upward has not migrated",
                  EffortMath.Migration(previous, current, Tick, 2) == Side.None);
        }

        private static void SmallShiftsAreNotMigration()
        {
            var previous = Bar(1, value: true, poc: 100.00m, valueLow: 99.50m, valueHigh: 100.50m);
            var oneTickUp = Bar(2, value: true, poc: 100.25m, valueLow: 99.75m, valueHigh: 100.50m);

            Check("a shift under the threshold is not migration",
                  EffortMath.Migration(previous, oneTickUp, Tick, 4) == Side.None);
            Check("the same shift counts once the threshold allows it",
                  EffortMath.Migration(previous, oneTickUp, Tick, 0) == Side.Buy);
        }

        private static void MigrationNeedsBothBarsToHaveValue()
        {
            var known = Bar(1, value: true, poc: 100.00m, valueLow: 99.50m, valueHigh: 100.50m);
            var unknown = Bar(2, value: false, poc: 101.00m, valueLow: 100.50m, valueHigh: 101.50m);

            Check("a bar with no per-price data cannot migrate",
                  EffortMath.Migration(known, unknown, Tick, 2) == Side.None &&
                  EffortMath.Migration(unknown, known, Tick, 2) == Side.None);
        }

        private static void MigrationIsSymmetric()
        {
            var lower = Bar(1, value: true, poc: 100.00m, valueLow: 99.50m, valueHigh: 100.50m);
            var higher = Bar(2, value: true, poc: 101.00m, valueLow: 100.50m, valueHigh: 101.50m);

            Check("reading the same pair backwards gives the opposite verdict",
                  EffortMath.Migration(lower, higher, Tick, 2) == Side.Buy &&
                  EffortMath.Migration(higher, lower, Tick, 2) == Side.Sell);
        }

        #endregion

        #region Effort

        private static void EffortPricesEachDirectionPerTick()
        {
            // Two bars up: 8 ticks of progress on 800 contracts, so 100 per tick.
            // Two bars down: 4 ticks of progress on 1600 contracts, so 400 per tick.
            var bars = new List<BarFacts>();
            bars.Add(Move(0, +4, 400m));
            bars.Add(Move(1, -2, 800m));
            bars.Add(Move(2, +4, 400m));
            bars.Add(Move(3, -2, 800m));

            var verdict = EffortMath.Effort(bars, 0, 3, Tick, 1.35m, 1m);

            Check("up cost is contracts per tick of upward progress", verdict.UpCost == 100m);
            Check("down cost is contracts per tick of downward progress", verdict.DownCost == 400m);
            Check("the cheaper direction is called", verdict.Side == Side.Buy);
            Check("a two-sided window is reported as a comparison", verdict.Basis == EffortBasis.Both);
            Check("the ratio says how much dearer the other side was", verdict.Ratio == 4m);
        }

        private static void EffortNeedsOneSideToBeClearlyCheaper()
        {
            // 100 per tick up against 120 per tick down: real, but inside the noise band.
            var bars = new List<BarFacts>();
            bars.Add(Move(0, +10, 1000m));
            bars.Add(Move(1, -10, 1200m));

            var close = EffortMath.Effort(bars, 0, 1, Tick, 1.35m, 1m);
            var loose = EffortMath.Effort(bars, 0, 1, Tick, 1.1m, 1m);

            Check("a 20% edge is not enough at a 1.35 skew", close.Side == Side.None &&
                                                             close.Basis == EffortBasis.Both);
            Check("the same window calls a side once the skew allows it", loose.Side == Side.Buy);
        }

        private static void EffortReportsAOneSidedWindowAsSuch()
        {
            var bars = new List<BarFacts>();
            bars.Add(Move(0, -6, 600m));
            bars.Add(Move(1, -6, 600m));

            var verdict = EffortMath.Effort(bars, 0, 1, Tick, 1.35m, 4m);

            Check("a window that only went one way says so", verdict.Basis == EffortBasis.OneSided &&
                                                              verdict.Side == Side.Sell);
            Check("there is no cost for a direction that never moved", !verdict.HasUpCost);
            Check("the ratio is not a number when there is only one cost", verdict.Ratio == 0m);
        }

        private static void EffortSaysNothingWhenNothingMoved()
        {
            var bars = new List<BarFacts>();
            bars.Add(Move(0, +1, 500m));
            bars.Add(Move(1, -1, 500m));

            var verdict = EffortMath.Effort(bars, 0, 1, Tick, 1.35m, 8m);

            Check("a window inside the progress floor gives no verdict",
                  verdict.Basis == EffortBasis.Insufficient && verdict.Side == Side.None);
        }

        private static void BarsWithNoVolumeCannotMakeADirectionFree()
        {
            // A bar that printed no volume but moved would otherwise cost zero per tick, which
            // would make that direction unbeatably cheap on a data gap.
            var bars = new List<BarFacts>();
            bars.Add(Move(0, +20, 0m));
            bars.Add(Move(1, -8, 800m));

            var verdict = EffortMath.Effort(bars, 0, 1, Tick, 1.35m, 4m);

            Check("a volumeless bar buys no progress", verdict.UpTicks == 0m &&
                                                        verdict.Side == Side.Sell &&
                                                        verdict.Basis == EffortBasis.OneSided);
        }

        private static void BarsThatClosedFlatPayForNeitherSide()
        {
            var withFlat = new List<BarFacts>();
            withFlat.Add(Move(0, +8, 400m));
            withFlat.Add(Move(1, 0, 9000m));
            withFlat.Add(Move(2, -8, 800m));

            var without = new List<BarFacts>();
            without.Add(Move(0, +8, 400m));
            without.Add(Move(1, -8, 800m));

            var a = EffortMath.Effort(withFlat, 0, 2, Tick, 1.35m, 4m);
            var b = EffortMath.Effort(without, 0, 1, Tick, 1.35m, 4m);

            Check("a bar that closed where it opened is charged to neither direction",
                  a.UpCost == b.UpCost && a.DownCost == b.DownCost && a.Side == b.Side);
        }

        private static void TheCheaperSideIsAlwaysTheOneWithTheLowerCost()
        {
            // Whatever the window, a verdict must agree with its own numbers and clear its own
            // skew. Checked against random windows rather than a worked example.
            var passed = true;

            for (var seed = 1; seed <= 300 && passed; seed++)
            {
                var bars = new List<BarFacts>();
                var value = seed;

                for (var i = 0; i < 12; i++)
                {
                    value = (value * 1103515245 + 12345) & 0x7fffffff;
                    var ticks = value % 21 - 10;
                    var volume = 50m + value % 900;
                    bars.Add(Move(i, ticks, volume));
                }

                var verdict = EffortMath.Effort(bars, 0, 11, Tick, 1.5m, 3m);
                if (verdict.Basis != EffortBasis.Both || verdict.Side == Side.None) continue;

                var cheaper = verdict.Side == Side.Buy ? verdict.UpCost : verdict.DownCost;
                var dearer = verdict.Side == Side.Buy ? verdict.DownCost : verdict.UpCost;

                if (cheaper >= dearer || cheaper * 1.5m > dearer) passed = false;
            }

            Check("a called side is genuinely the cheaper one, by at least the skew", passed);
        }

        #endregion

        #region Absorption

        private static void AbsorptionIsHeavyLopsidedAggressionWithNoResult()
        {
            // 900 contracts against a 500 average, 60% of it buying, and the bar closed one tick
            // below where it opened. Buyers spent everything and got nothing.
            var bar = Traded(10, open: 100m, close: 99.75m, high: 100.75m, low: 99.50m,
                             volume: 900m, delta: 540m);

            AbsorptionMark mark;
            var ok = EffortMath.Absorption(bar, 500m, 140m, 25m, Tick, 2, out mark);

            Check("heavy one-sided aggression with no result is absorption", ok);
            Check("the absorbed side is the one that pushed", ok && mark.Absorbed == Side.Buy);
            Check("the mark sits at the extreme they pushed into", ok && mark.Price == 100.75m);
        }

        private static void AggressionThatWorkedIsNotAbsorption()
        {
            var bar = Traded(10, open: 100m, close: 101.50m, high: 101.75m, low: 99.90m,
                             volume: 900m, delta: 540m);

            AbsorptionMark mark;
            Check("aggression that moved price is not absorption",
                  !EffortMath.Absorption(bar, 500m, 140m, 25m, Tick, 2, out mark));
        }

        private static void ACloseAgainstTheAggressionCountsHowever()
        {
            // Six ticks against the aggression is far past the "went nowhere" limit, and it is
            // worse for the buyers than going nowhere, not better.
            var bar = Traded(10, open: 100m, close: 98.50m, high: 100.75m, low: 98.25m,
                             volume: 900m, delta: 540m);

            AbsorptionMark mark;
            Check("a close against the aggression counts however far it went",
                  EffortMath.Absorption(bar, 500m, 140m, 25m, Tick, 2, out mark));
        }

        private static void ABalancedBarIsNotAbsorption()
        {
            var bar = Traded(10, open: 100m, close: 100m, high: 100.75m, low: 99.50m,
                             volume: 900m, delta: 90m);

            AbsorptionMark mark;
            Check("a two-sided bar that went nowhere is just a quiet bar",
                  !EffortMath.Absorption(bar, 500m, 140m, 25m, Tick, 2, out mark));
        }

        private static void ASmallBarIsNotAbsorption()
        {
            var bar = Traded(10, open: 100m, close: 99.75m, high: 100.75m, low: 99.50m,
                             volume: 200m, delta: 120m);

            AbsorptionMark mark;
            Check("a light bar is not absorption however lopsided",
                  !EffortMath.Absorption(bar, 500m, 140m, 25m, Tick, 2, out mark));
        }

        private static void WithNoYardstickNothingIsAbsorbed()
        {
            var bar = Traded(10, open: 100m, close: 99.75m, high: 100.75m, low: 99.50m,
                             volume: 900m, delta: 540m);

            AbsorptionMark mark;
            Check("with no average to compare against, nothing is called heavy",
                  !EffortMath.Absorption(bar, 0m, 140m, 25m, Tick, 2, out mark));
        }

        #endregion

        #region Confluence

        private static void AllThreeLayersMustAgree()
        {
            var bar = Traded(20, open: 100m, close: 100.75m, high: 101m, low: 99.75m,
                             volume: 800m, delta: 300m);
            bar.HasValue = true;
            bar.ValueLow = 99.90m;
            bar.ValueHigh = 100.60m;
            bar.Poc = 100.25m;

            var buying = Verdict(Side.Buy);
            var selling = Verdict(Side.Sell);
            var silent = Verdict(Side.None);

            var down = bar;
            down.Close = 99.50m;
            down.Delta = -300m;

            Setup setup;

            Check("all three agreeing produces a setup",
                  EffortMath.Confluence(Side.Buy, bar, buying, Side.None, NoLevel, false, Tick, 2, 0, out setup) &&
                  setup.Side == Side.Buy);

            Check("effort disagreeing refuses it",
                  !EffortMath.Confluence(Side.Buy, bar, selling, Side.None, NoLevel, false, Tick, 2, 0, out setup));

            Check("effort silent refuses it",
                  !EffortMath.Confluence(Side.Buy, bar, silent, Side.None, NoLevel, false, Tick, 2, 0, out setup));

            Check("migration silent refuses it",
                  !EffortMath.Confluence(Side.None, bar, buying, Side.None, NoLevel, false, Tick, 2, 0, out setup));

            Check("the bar's own aggression disagreeing refuses it",
                  !EffortMath.Confluence(Side.Buy, down, buying, Side.None, NoLevel, false, Tick, 2, 0, out setup));
        }

        private static void TheTargetIsExactlyOneR()
        {
            var bar = Traded(20, open: 100m, close: 100.75m, high: 101m, low: 99.75m,
                             volume: 800m, delta: 300m);
            bar.HasValue = true;
            bar.ValueLow = 99.90m;
            bar.ValueHigh = 100.60m;

            Setup setup;
            EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false, Tick, 2, 0, out setup);

            var risk = setup.Entry - setup.Stop;
            var reward = setup.Target - setup.Entry;

            Check("the stop sits the buffer beyond the value area", setup.Stop == 99.90m - Tick * 2);
            Check("the risk is positive", risk > 0m);
            Check("the target is the same distance again", reward == risk);
        }

        private static void ATooDistantStopIsMarkedNotMoved()
        {
            // Entry 100.75 against a value low of 99.75 with no buffer: a whole point of risk,
            // which is four ticks. Worked out here so the cap is checked against a known number.
            var bar = Traded(20, open: 100m, close: 100.75m, high: 101m, low: 99.75m,
                             volume: 800m, delta: 300m);
            bar.HasValue = true;
            bar.ValueLow = 99.75m;
            bar.ValueHigh = 100.60m;

            Setup tight, loose, uncapped;
            EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false, Tick, 0, 2, out tight);
            EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false, Tick, 0, 8, out loose);
            EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false, Tick, 0, 0, out uncapped);

            Check("a stop beyond the cap is flagged", tight.OverCap);
            Check("the same stop inside the cap is not", !loose.OverCap);
            Check("a cap of zero switches the check off", !uncapped.OverCap);
            Check("the cap never moves the stop it flagged",
                  tight.Stop == loose.Stop && tight.Stop == 99.75m && tight.Target == loose.Target);
        }

        private static void AbsorbedAggressionIsNotConfirmation()
        {
            var bar = Traded(20, open: 100m, close: 100.75m, high: 101m, low: 99.75m,
                             volume: 800m, delta: 300m);
            bar.HasValue = true;
            bar.ValueLow = 99.90m;
            bar.ValueHigh = 100.60m;

            Setup setup;
            Check("buyers who just got absorbed do not confirm a long",
                  !EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.Buy, NoLevel, false, Tick, 2, 0, out setup));
            Check("sellers being absorbed does not block a long",
                  EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.Sell, NoLevel, false, Tick, 2, 0, out setup));
        }

        private static void AValuelessBarCannotProduceASetup()
        {
            var bar = Traded(20, open: 100m, close: 100.75m, high: 101m, low: 99.75m,
                             volume: 800m, delta: 300m);
            bar.HasValue = false;

            Setup setup;
            Check("with no value area there is nowhere to put the stop",
                  !EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false, Tick, 2, 0, out setup));
        }

        private static void AStopOnTheWrongSideOfEntryIsRefused()
        {
            // The close came back inside its own value area, so the stop would sit above entry.
            var bar = Traded(20, open: 100m, close: 100.10m, high: 101m, low: 99.75m,
                             volume: 800m, delta: 300m);
            bar.HasValue = true;
            bar.ValueLow = 100.50m;
            bar.ValueHigh = 100.90m;

            Setup setup;
            Check("a stop that is not below a long entry is refused",
                  !EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false, Tick, 0, 0, out setup));
        }

        #endregion

        #region The setup lifecycle

        private static void OnlyOneSetupRunsAtATime()
        {
            // Three consecutive bars that all agree. Before the one-at-a-time rule this printed
            // three setups a few ticks apart, which is what made the first chart unreadable.
            var tracker = new SetupTracker();

            for (var bar = 1; bar <= 3; bar++)
                Agreeing(tracker, bar, 100m + Tick * bar);

            Check("consecutive agreeing bars are one setup, not three", tracker.Runs.Count == 1);
            Check("the setup is the first of them", tracker.Runs[0].Setup.Bar == 1);
        }

        private static void ANewSetupCanStartOnceTheStopIsTaken()
        {
            var tracker = new SetupTracker();
            Agreeing(tracker, 1, 100m);

            var stop = tracker.Runs[0].Setup.Stop;

            // A bar that trades clean through the stop and agrees with nothing.
            Silent(tracker, 2, stop - 1m);

            Check("the run ends when a bar trades through the stop", !tracker.Runs[0].Live);
            Check("nothing is running afterwards", tracker.Active == null);

            Agreeing(tracker, 3, 100m);
            Check("a later agreeing bar starts a new setup", tracker.Runs.Count == 2);
        }

        private static void ATradeableSetupReplacesAnUntradeableOne()
        {
            // Nine ticks of risk on bar 1 against a six tick cap, then five on bar 2. The second
            // is the tradeable expression of the same idea.
            var tracker = new SetupTracker();

            Agreeing(tracker, 1, 100m, 99m, 6);
            Check("the wide setup is flagged over cap", tracker.Runs.Count == 1 &&
                                                        tracker.Runs[0].Setup.OverCap);

            Agreeing(tracker, 2, 100m, 99.90m, 6);

            Check("the tradeable one replaces it rather than hiding behind it",
                  tracker.Runs.Count == 1 && !tracker.Runs[0].Setup.OverCap &&
                  tracker.Runs[0].Setup.Bar == 2);
        }

        private static void AnUntradeableSetupDoesNotReplaceATradeableOne()
        {
            var tracker = new SetupTracker();

            Agreeing(tracker, 1, 100m, 99.90m, 6);
            Agreeing(tracker, 2, 102m, 101m, 6);   // wide, but well clear of the first stop

            Check("a setup you could not take does not displace one you could",
                  tracker.Runs.Count == 1 && tracker.Runs[0].Setup.Bar == 1 &&
                  !tracker.Runs[0].Setup.OverCap);
        }

        private static void TheRunEndsAtTheFirstBarThroughTheStop()
        {
            var tracker = new SetupTracker();
            Agreeing(tracker, 1, 100m);

            var run = tracker.Runs[0];
            var stop = run.Setup.Stop;

            Silent(tracker, 2, stop + 1m);         // nowhere near it
            Silent(tracker, 3, stop - 0.25m);      // through it
            Silent(tracker, 4, stop - 5m);         // long gone

            Check("the exit is the first bar through, not the last", run.ExitBar == 3);
            Check("the exit price is the stop as it stood, not the bar's low", run.ExitLevel == stop);
            Check("nothing is recorded after the exit", run.Levels.Count == 3);
        }

        private static void TheTrailStepsOnlyOnAggressionInTheTradesDirection()
        {
            var tracker = new SetupTracker();
            Agreeing(tracker, 1, 100m);

            var run = tracker.Runs[0];
            var opened = run.Setup.Stop;

            // A bar well above the stop with heavy SELL aggression: the wrong side, so the stop
            // must not move to its low.
            Push(tracker, 2, opened + 4m, opened + 8m, Side.Sell);
            var afterWrongSide = run.Trail;

            Push(tracker, 3, opened + 5m, opened + 9m, Side.Buy);
            var afterRightSide = run.Trail;

            // Buy aggression, but it dips below where the stop now stands.
            Push(tracker, 4, opened + 1m, opened + 9m, Side.Buy);

            Check("aggression on the wrong side leaves the stop alone", afterWrongSide == opened);
            Check("aggression in the trade's direction moves it up", afterRightSide == opened + 5m);
            Check("a bar through the stop ends the trade even while aggressing the right way",
                  run.ExitBar == 4 && run.ExitLevel == opened + 5m);
            Check("and the stop never came back down", run.Trail == opened + 5m);
        }

        private static void ReplayingTheSameBarChangesNothing()
        {
            // A forming bar makes the platform call this many times over. Processing bar N twice
            // must not open a second setup or step the trail twice.
            var once = new SetupTracker();
            Agreeing(once, 1, 100m);
            Push(once, 2, once.Runs[0].Setup.Stop + 5m, 110m, Side.Buy);

            var twice = new SetupTracker();
            Agreeing(twice, 1, 100m);
            Agreeing(twice, 1, 100m);
            Push(twice, 2, twice.Runs[0].Setup.Stop + 5m, 110m, Side.Buy);
            Push(twice, 2, twice.Runs[0].Setup.Stop + 5m, 110m, Side.Buy);

            Check("replaying a bar opens no second setup", twice.Runs.Count == once.Runs.Count);
            Check("replaying a bar does not step the trail twice",
                  twice.Runs[0].Trail == once.Runs[0].Trail &&
                  twice.Runs[0].Levels.Count == once.Runs[0].Levels.Count);
        }

        #endregion

        #region The range profile

        private static void AProfileIsDenseAcrossItsWholeRange()
        {
            // 100.00 and 100.75 traded; the two ticks between them did not. They are real zeros
            // and have to be in the array, or every neighbour comparison lands on a wrong price.
            var builder = new RangeProfileBuilder();
            builder.Add(100.00m, 10m, 5m, 5m);
            builder.Add(100.75m, 8m, 4m, 4m);

            var profile = builder.Build(Tick, 4000);

            Check("the profile spans every tick of its range", profile != null && profile.Count == 4);
            Check("untraded ticks inside it are zeros", profile.Levels[1].Volume == 0m &&
                                                        profile.Levels[2].Volume == 0m);
            Check("its ends are the prices that traded", profile.LowPrice == 100.00m &&
                                                          profile.HighPrice == 100.75m);
            Check("totals add up", profile.TotalVolume == 18m);
        }

        private static void AbsorptionClustersAreHeavyAndBalanced()
        {
            // Three heavy prices where the aggression netted off: size changed hands and price
            // stayed. Buyers were the net aggressor, so they are the side that got filled into.
            var profile = Shape(new[] { 10m, 100m, 100m, 100m, 10m },
                                new[] { 0m, 10m, 10m, 10m, 0m });

            var clusters = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 2);

            Check("a balanced heavy band is absorption", clusters.Length == 1 &&
                                                          clusters[0].Kind == ClusterKind.Absorption);
            Check("the absorbed side is the one that pushed", clusters[0].Side == Side.Buy);
            Check("the band covers the run", clusters[0].Ticks == 3);
            Check("absorbed buyers make the level supply", clusters[0].Favours == Side.Sell);
        }

        private static void AggressionClustersAreHeavyAndOneSided()
        {
            var profile = Shape(new[] { 10m, 100m, 100m, 10m },
                                new[] { 0m, -80m, -80m, 0m });

            var clusters = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 2);

            Check("a one-sided heavy band is aggression", clusters.Length == 1 &&
                                                           clusters[0].Kind == ClusterKind.Aggression);
            Check("the side is the one that drove", clusters[0].Side == Side.Sell);
            Check("and the level argues that way", clusters[0].Favours == Side.Sell);
        }

        private static void ASingleHeavyPriceIsNotACluster()
        {
            var profile = Shape(new[] { 10m, 100m, 10m, 10m },
                                new[] { 0m, 5m, 0m, 0m });

            var strict = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 2);
            var loose = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 1);

            Check("one heavy price is a print, not a level", strict.Length == 0);
            Check("unless you ask for single ticks", loose.Length == 1);
        }

        private static void ClustersDoNotStrideAcrossAChangeOfCharacter()
        {
            // Two heavy prices absorbed, then two heavy prices driven. One band would claim the
            // auction did the same thing throughout, and it did not.
            var profile = Shape(new[] { 100m, 100m, 100m, 100m },
                                new[] { 5m, 5m, -85m, -85m });

            var clusters = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 2);

            Check("absorbed and driven prices are separate levels", clusters.Length == 2);
            Check("each keeps its own character",
                  (clusters[0].Kind == ClusterKind.Absorption && clusters[1].Kind == ClusterKind.Aggression) ||
                  (clusters[1].Kind == ClusterKind.Absorption && clusters[0].Kind == ClusterKind.Aggression));
        }

        private static void OnlyTheHeaviestClustersAreKept()
        {
            var profile = Shape(new[] { 100m, 100m, 5m, 60m, 60m, 5m, 80m, 80m },
                                new[] { 5m, 5m, 0m, 3m, 3m, 0m, 4m, 4m });

            var all = RangeMath.FindClusters(profile, 50m, 0.22m, 0.55m, 2);
            var kept = RangeMath.Heaviest(all, 2);

            Check("three bands are found", all.Length == 3);
            Check("two are kept", kept.Length == 2);
            Check("and they are the two heaviest, heaviest first",
                  kept[0].Volume == 200m && kept[1].Volume == 160m);
        }

        private static void PriceIsPlacedAgainstValue()
        {
            var profile = Shape(new[] { 5m, 10m, 90m, 10m, 5m }, new[] { 0m, 0m, 0m, 0m, 0m });
            RangeMath.ComputeValueArea(profile, 70m);

            var above = RangeMath.Where(profile, profile.ValueHigh + 5m, null, 0);
            var below = RangeMath.Where(profile, profile.ValueLow - 5m, null, 0);
            var inside = RangeMath.Where(profile, profile.Poc, null, 0);

            Check("above value is premium", above.Zone == Zone.Premium && above.Favours == Side.Sell);
            Check("below value is discount", below.Zone == Zone.Discount && below.Favours == Side.Buy);
            Check("the point of control is inside value", inside.Zone == Zone.InValue);
            Check("distance to the point of control is signed in ticks", inside.ToPoc == 0m &&
                                                                          above.ToPoc > 0m &&
                                                                          below.ToPoc < 0m);
        }

        private static void AClusterIsFoundUnderThePrice()
        {
            var profile = Shape(new[] { 10m, 100m, 100m, 100m, 10m },
                                new[] { 0m, 10m, 10m, 10m, 0m });
            RangeMath.ComputeValueArea(profile, 70m);

            var clusters = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 2);
            var on = RangeMath.Where(profile, clusters[0].Low, clusters, 0);
            var off = RangeMath.Where(profile, clusters[0].High + Tick * 8m, clusters, 0);

            Check("a price inside the band is on the level", on.AtCluster);
            Check("a price well clear of it is not", !off.AtCluster);
        }

        private static void AbsorbedSellersMakeADemandLevel()
        {
            // Sellers pushed into it and it held, so it is demand -- the level argues for longs.
            var profile = Shape(new[] { 10m, 100m, 100m, 10m },
                                new[] { 0m, -10m, -10m, 0m });

            var clusters = RangeMath.FindClusters(profile, 55m, 0.22m, 0.55m, 2);
            var here = RangeMath.Where(profile, clusters[0].Low, clusters, 0);

            Check("sellers absorbed is a level that favours buyers", here.AtCluster &&
                                                                     here.Favours == Side.Buy);
        }

        private static void PriceInMidAirFavoursNobody()
        {
            var profile = Shape(new[] { 5m, 10m, 90m, 10m, 5m }, new[] { 0m, 0m, 0m, 0m, 0m });
            RangeMath.ComputeValueArea(profile, 100m);

            // With the whole profile inside value, a price at the point of control stands on no
            // edge and no cluster.
            var here = RangeMath.Where(profile, profile.Poc, new Cluster[0], 0);

            Check("a price on nothing argues for nobody", here.Known && here.Favours == Side.None);
        }

        private static void VwapIsWeightedByVolume()
        {
            // 100.00 with 30 lots and 100.25 with 10: the average sits a quarter of the way up.
            var builder = new RangeProfileBuilder();
            builder.Add(100.00m, 30m, 15m, 15m);
            builder.Add(100.25m, 10m, 5m, 5m);

            var profile = builder.Build(Tick, 4000);
            var vwap = RangeMath.Vwap(profile);

            Check("vwap is the volume weighted mean", vwap == 100.0625m);
            Check("an empty profile has no vwap", RangeMath.Vwap(null) == 0m);
        }

        private static void RowsMergeToFitTheScreen()
        {
            Check("a tall row needs no merging", RangeMath.TicksPerRow(4m, 2) == 1);
            Check("a squeezed one merges ticks", RangeMath.TicksPerRow(0.25m, 2) == 8);
            Check("an unknown row height merges nothing", RangeMath.TicksPerRow(0m, 2) == 1);
        }

        private static void ASetupMustComeFromALevel()
        {
            var bar = Traded(20, 100m, 100.75m, 101m, 99.75m, 800m, 300m);
            bar.HasValue = true;
            bar.ValueLow = 99.90m;
            bar.ValueHigh = 100.60m;

            var demand = new Location();
            demand.Known = true;
            demand.Favours = Side.Buy;

            var supply = new Location();
            supply.Known = true;
            supply.Favours = Side.Sell;

            Setup setup;

            Check("a long off a level that favours buyers is taken",
                  EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, demand, true,
                                        Tick, 2, 0, out setup));

            Check("the same long off a level that favours sellers is refused",
                  !EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, supply, true,
                                         Tick, 2, 0, out setup));

            Check("and off no level at all is refused",
                  !EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, true,
                                         Tick, 2, 0, out setup));

            Check("with the gate off, the three layers stand alone",
                  EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, NoLevel, false,
                                        Tick, 2, 0, out setup));

            Check("the setup remembers what it came off",
                  EffortMath.Confluence(Side.Buy, bar, Verdict(Side.Buy), Side.None, demand, true,
                                        Tick, 2, 0, out setup) && setup.Where.Favours == Side.Buy);
        }

        #endregion

        #region Outside levels

        private static void LevelsParseWithLabelsAndKinds()
        {
            var set = OptionLevels.Parse(new[]
            {
                "# my levels",
                "",
                "23980.00, Call Wall, resistance",
                "23800.25, Gamma Flip, pivot",
                "23650, Put Wall, s"
            });

            Check("three levels come back", set.Count == 3 && set.Usable);
            Check("prices parse exactly", set.Levels[0].Price == 23980.00m &&
                                          set.Levels[1].Price == 23800.25m);
            Check("labels are kept", set.Levels[0].Label == "Call Wall");
            Check("kinds are read, long form and short", set.Levels[0].Kind == LevelKind.Resistance &&
                                                          set.Levels[2].Kind == LevelKind.Support);
            Check("comments and blanks are ignored, not counted as errors", set.BadLines == 0);
        }

        private static void BadLinesAreCountedNotSwallowed()
        {
            var set = OptionLevels.Parse(new[]
            {
                "23980, Call Wall, resistance",
                "not a price, nonsense, support",
                "-5, negative, support",
                "23650, Put Wall, support"
            });

            Check("the good ones are kept", set.Count == 2);
            Check("the bad ones are counted", set.BadLines == 2);
        }

        private static void AKindItDoesNotRecogniseIsNotGuessed()
        {
            // "Call Wall" obviously reads as resistance to a human. Guessing a side from a label
            // would be this code inventing a bias out of a string, so an unknown kind is a pivot
            // and is counted as a line that did not fully parse.
            var set = OptionLevels.Parse(new[] { "23980, Call Wall, wall" });

            Check("an unrecognised kind stays a pivot", set.Levels[0].Kind == LevelKind.Pivot);
            Check("and is reported rather than guessed at", set.BadLines == 1);
        }

        private static void TheNearestLevelSetsTheBias()
        {
            var set = OptionLevels.Parse(new[]
            {
                "100.00, Put Wall, support",
                "104.00, Call Wall, resistance",
                "102.00, Flip, pivot"
            });

            OptionLevel nearest;

            Check("support underfoot argues for buyers",
                  OptionLevels.Bias(set, 100.10m, Tick, 8, out nearest) == Side.Buy &&
                  nearest.Label == "Put Wall");

            Check("resistance underfoot argues for sellers",
                  OptionLevels.Bias(set, 103.90m, Tick, 8, out nearest) == Side.Sell);

            Check("a pivot argues for nobody but is still named",
                  OptionLevels.Bias(set, 102.00m, Tick, 8, out nearest) == Side.None &&
                  nearest.Label == "Flip");

            Check("out of reach of everything argues for nobody",
                  OptionLevels.Bias(set, 101.00m, Tick, 2, out nearest) == Side.None);
        }

        private static void AWallOverheadRefusesALong()
        {
            var set = OptionLevels.Parse(new[] { "104.00, Call Wall, resistance" });

            OptionLevel wall;

            // Entry 103.00, wall one point above, which is four ticks at a quarter-point tick.
            // Inside an eight tick veto; clear of a two tick one.
            Check("a wall inside the veto blocks the long",
                  OptionLevels.Blocks(set, Side.Buy, 103.00m, Tick, 8, out wall) &&
                  wall.Label == "Call Wall");

            Check("a veto too tight to reach it does not",
                  !OptionLevels.Blocks(set, Side.Buy, 103.00m, Tick, 2, out wall));

            Check("zero switches the veto off",
                  !OptionLevels.Blocks(set, Side.Buy, 103.00m, Tick, 0, out wall));
        }

        private static void AWallTheWrongSideOfPriceIsNotInTheWay()
        {
            var set = OptionLevels.Parse(new[]
            {
                "104.00, Call Wall, resistance",
                "100.00, Put Wall, support"
            });

            OptionLevel wall;

            Check("resistance BELOW a long is behind it, not in front",
                  !OptionLevels.Blocks(set, Side.Buy, 105.00m, Tick, 40, out wall));

            Check("support ABOVE a short is behind it too",
                  !OptionLevels.Blocks(set, Side.Sell, 99.00m, Tick, 40, out wall));

            Check("but support below a short is in the way",
                  OptionLevels.Blocks(set, Side.Sell, 101.00m, Tick, 40, out wall) &&
                  wall.Label == "Put Wall");
        }

        private static void ATargetBehindAWallIsRefused()
        {
            var set = OptionLevels.Parse(new[] { "104.00, Call Wall, resistance" });

            OptionLevel wall;

            Check("a target on the far side of the wall is refused",
                  OptionLevels.BlocksTarget(set, Side.Buy, 103.00m, 105.00m, out wall));

            Check("a target short of it is fine",
                  !OptionLevels.BlocksTarget(set, Side.Buy, 103.00m, 103.75m, out wall));

            Check("a wall behind the entry is irrelevant",
                  !OptionLevels.BlocksTarget(set, Side.Buy, 105.00m, 107.00m, out wall));
        }

        private static void TypicalSizeIsMeasuredNotAssumed()
        {
            var bars = new List<BarFacts>();
            bars.Add(Sized(0, 100m, 60m));
            bars.Add(Sized(1, 200m, -100m));
            bars.Add(Sized(2, 300m, 80m));
            bars.Add(Sized(3, 400m, -9000m));    // one monster

            Check("the typical delta is the middle one, not dragged by the monster",
                  EffortMath.TypicalDelta(bars, 0, 3) == 90m);
            Check("an empty range measures nothing", EffortMath.TypicalDelta(bars, 3, 0) == 0m);
            Check("the typical range is in ticks",
                  EffortMath.TypicalRangeTicks(bars, 0, 3, Tick) == 10m);
            Check("with no tick size there is nothing to measure",
                  EffortMath.TypicalRangeTicks(bars, 0, 3, 0m) == 0m);
        }

        #endregion

        #region Regime

        private static void RotatingInsideValueIsTheCage()
        {
            // Twenty bars covering four points and finishing where they started, every close
            // inside value. This is the chop the model is not supposed to trade.
            var bars = Rotation(20, 100m, 1m);

            var read = MarketRegime.Read(bars, 0, 19, Tick, 101m, 99m, true, 0.65m, 0.30m, 0.50m);

            Check("rotating inside value is balance", read.Regime == Regime.Balance);
            Check("every close was inside value", read.InsideShare == 1m);
            Check("and almost none of the range was kept", read.Efficiency < 0.30m);
            Check("balance is not tradeable", !read.Tradeable);
        }

        private static void RotatingOUTSIDEValueIsNotTheCage()
        {
            // The same going-nowhere rotation, but happening well below value. Price left the
            // balance and stalled; that is a stalled directional auction, not fair value, and
            // calling it the cage would stand the model aside in the one place it should be
            // watching. Efficiency alone cannot tell these apart -- only the value area can.
            var bars = Rotation(20, 100m, 1m);

            var inside = MarketRegime.Read(bars, 0, 19, Tick, 101m, 99m, true, 0.65m, 0.30m, 0.50m);
            var outside = MarketRegime.Read(bars, 0, 19, Tick, 112m, 110m, true, 0.65m, 0.30m, 0.50m);

            Check("the same rotation inside value is the cage", inside.Regime == Regime.Balance);
            Check("and outside value it is not", outside.Regime == Regime.Mixed);
            Check("because no close was inside", outside.InsideShare == 0m);
        }

        private static void AMarketThatKeptItsRangeIsATrend()
        {
            // Straight up: the close ends where the high is, so the whole range was progress.
            var bars = new List<BarFacts>();
            for (var i = 0; i < 10; i++) bars.Add(Step(i, 100m + i * 0.5m, 0.5m));

            var read = MarketRegime.Read(bars, 0, 9, Tick, 101m, 100m, true, 0.65m, 0.30m, 0.50m);

            Check("keeping its range is a trend", read.Regime == Regime.Trend);
            Check("and a trend is tradeable", read.Tradeable);
            Check("efficiency is the share of range converted", read.Efficiency > 0.8m);
        }

        private static void KeepingSomeOfItIsNeitherAndSaysSo()
        {
            // Drifts up but wanders doing it: too directional for the cage, too sloppy for a trend.
            var bars = Rotation(20, 100m, 1m);
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                bar.Open += i * 0.05m;
                bar.Close += i * 0.05m;
                bar.High += i * 0.05m;
                bar.Low += i * 0.05m;
                bars[i] = bar;
            }

            var read = MarketRegime.Read(bars, 0, 19, Tick, 101m, 99m, true, 0.65m, 0.30m, 0.90m);

            Check("neither balanced nor trending is mixed", read.Regime == Regime.Mixed);
            Check("and mixed is still tradeable", read.Tradeable);
        }

        private static void AnUnknownValueAreaLeavesTheRegimeUnread()
        {
            var bars = Rotation(20, 100m, 1m);

            var read = MarketRegime.Read(bars, 0, 19, Tick, 0m, 0m, false, 0.65m, 0.30m, 0.50m);

            Check("with no value area the regime is unread", read.Regime == Regime.Unknown);
            Check("and unread is not treated as fine", !read.Tradeable);
        }

        private static void TheFilterStandsAsideInBalanceAndInTheDark()
        {
            var balance = new RegimeRead(); balance.Regime = Regime.Balance;
            var trend = new RegimeRead(); trend.Regime = Regime.Trend;
            var mixed = new RegimeRead(); mixed.Regime = Regime.Mixed;
            var unread = new RegimeRead(); unread.Regime = Regime.Unknown;

            Check("balance is refused", !MarketRegime.Allows(balance, true));
            Check("not knowing is refused too", !MarketRegime.Allows(unread, true));
            Check("a trend is allowed", MarketRegime.Allows(trend, true));
            Check("so is mixed", MarketRegime.Allows(mixed, true));
            Check("with the filter off everything is allowed",
                  MarketRegime.Allows(balance, false) && MarketRegime.Allows(unread, false));
        }

        private static void AWindowTooShortToJudgeIsNotJudged()
        {
            var bars = Rotation(20, 100m, 1m);

            Check("two bars is not a regime",
                  MarketRegime.Read(bars, 0, 1, Tick, 101m, 99m, true, 0.65m, 0.30m, 0.50m).Regime
                  == Regime.Unknown);
            Check("and neither is nothing",
                  MarketRegime.Read(null, 0, 19, Tick, 101m, 99m, true, 0.65m, 0.30m, 0.50m).Regime
                  == Regime.Unknown);
        }

        #endregion

        #region Delta divergence

        private static void ANewLowTheSellingDidNotPayForIsBullish()
        {
            // Bar 2 makes the swing low on heavy selling. Bar 6 takes it out on much less, so
            // cumulative delta is higher there than it was at the old low.
            var bars = new List<BarFacts>();
            var cumulative = new List<decimal>();

            Leg(bars, cumulative, 0, 100.00m, 0m);
            Leg(bars, cumulative, 1, 99.50m, -400m);
            Leg(bars, cumulative, 2, 99.00m, -600m);      // the swing low, sold hard
            Leg(bars, cumulative, 3, 99.40m, 200m);
            Leg(bars, cumulative, 4, 99.30m, 150m);
            Leg(bars, cumulative, 5, 99.20m, 100m);
            Leg(bars, cumulative, 6, 98.90m, -50m);       // lower low, barely sold

            var found = DeltaSignals.Find(bars, cumulative, 6, 12, 100m);

            Check("a lower low on lighter selling is divergence", found.Found);
            Check("and it argues for buyers", found.Side == Side.Buy);
            Check("the extreme is the low just made", found.Extreme == 98.90m);
            Check("it names the swing it failed to confirm", found.AgainstBar == 2);
            Check("and how far the delta held back", found.Gap == 400m);
        }

        private static void ANewHighTheBuyingDidNotPayForIsBearish()
        {
            var bars = new List<BarFacts>();
            var cumulative = new List<decimal>();

            // Written as lows; each bar is half a point tall, so these are highs of 100.00,
            // 100.50, 101.00, 100.60, 100.70, 100.80 and 101.10.
            Leg(bars, cumulative, 0, 99.50m, 0m);
            Leg(bars, cumulative, 1, 100.00m, 400m);
            Leg(bars, cumulative, 2, 100.50m, 600m);      // the swing high, bought hard
            Leg(bars, cumulative, 3, 100.10m, -200m);
            Leg(bars, cumulative, 4, 100.20m, -150m);
            Leg(bars, cumulative, 5, 100.30m, -100m);
            Leg(bars, cumulative, 6, 100.60m, 50m);       // higher high, barely bought

            var found = DeltaSignals.Find(bars, cumulative, 6, 12, 100m);

            Check("a higher high on lighter buying is divergence", found.Found &&
                                                                    found.Side == Side.Sell);
            Check("the extreme is the high just made", found.Extreme == 101.10m);
        }

        private static void ANewLowOnHeavierSellingIsNotDivergence()
        {
            // This is what a trend looks like: each new low is paid for.
            var bars = new List<BarFacts>();
            var cumulative = new List<decimal>();

            Leg(bars, cumulative, 0, 100.00m, 0m);
            Leg(bars, cumulative, 1, 99.50m, -300m);
            Leg(bars, cumulative, 2, 99.00m, -400m);
            Leg(bars, cumulative, 3, 98.50m, -500m);

            Check("a lower low on heavier selling is just a downtrend",
                  !DeltaSignals.Find(bars, cumulative, 3, 12, 100m).Found);
        }

        private static void HoldingInsideTheRangeIsNotDivergence()
        {
            var bars = new List<BarFacts>();
            var cumulative = new List<decimal>();

            Leg(bars, cumulative, 0, 100.00m, 0m);
            Leg(bars, cumulative, 1, 99.00m, -600m);
            Leg(bars, cumulative, 2, 99.50m, 400m);
            Leg(bars, cumulative, 3, 99.60m, 300m);

            Check("a bar that broke nothing is not divergence",
                  !DeltaSignals.Find(bars, cumulative, 3, 12, 0m).Found);
        }

        private static void ASmallDeltaGapDoesNotCount()
        {
            var bars = new List<BarFacts>();
            var cumulative = new List<decimal>();

            Leg(bars, cumulative, 0, 100.00m, 0m);
            Leg(bars, cumulative, 1, 99.00m, -500m);
            Leg(bars, cumulative, 2, 99.20m, 260m);
            Leg(bars, cumulative, 3, 98.90m, -250m);      // 10 contracts of hold-back

            var strict = DeltaSignals.Find(bars, cumulative, 3, 12, 150m);
            var loose = DeltaSignals.Find(bars, cumulative, 3, 12, 5m);

            Check("ten contracts is not the aggression refusing to follow", !strict.Found);
            Check("with the bar set low enough it counts", loose.Found && loose.Side == Side.Buy);
        }

        private static void DivergenceStopsBeyondTheExtremeItJustMade()
        {
            var divergence = new Divergence();
            divergence.Found = true;
            divergence.Side = Side.Buy;
            divergence.Extreme = 98.90m;

            var bar = Traded(6, 99.00m, 99.30m, 99.35m, 98.90m, 900m, 200m);

            var level = new Location();
            level.Known = true;
            level.Favours = Side.Buy;

            Setup setup;
            var made = DeltaSignals.ToSetup(divergence, bar, level, true, Tick, 2, 0, out setup);

            Check("the divergence becomes a trade", made && setup.Side == Side.Buy);
            Check("it is marked as a divergence trade", setup.Kind == SetupKind.Divergence);
            Check("the stop sits beyond the extreme it just made", setup.Stop == 98.90m - Tick * 2);
            Check("entry is the close", setup.Entry == 99.30m);
            Check("and the target is one R", setup.Target - setup.Entry == setup.Entry - setup.Stop);
        }

        private static void ADivergenceInMidAirIsRefused()
        {
            var divergence = new Divergence();
            divergence.Found = true;
            divergence.Side = Side.Buy;
            divergence.Extreme = 98.90m;

            var bar = Traded(6, 99.00m, 99.30m, 99.35m, 98.90m, 900m, 200m);

            var against = new Location();
            against.Known = true;
            against.Favours = Side.Sell;

            Setup setup;

            Check("a divergence on a level that argues the other way is refused",
                  !DeltaSignals.ToSetup(divergence, bar, against, true, Tick, 2, 0, out setup));
            Check("and on no level at all",
                  !DeltaSignals.ToSetup(divergence, bar, NoLevel, true, Tick, 2, 0, out setup));
            Check("with the gate off it stands on the delta alone",
                  DeltaSignals.ToSetup(divergence, bar, NoLevel, false, Tick, 2, 0, out setup));
        }

        #endregion

        #region Ranking what to mark

        private static void TheBiggestPrintsAreRankedAcrossEverything()
        {
            // Bar 1 is busy and bar 5 has the single biggest print on the screen. Ranked per bar,
            // bar 1's second-largest would be marked as loudly as bar 5's monster.
            var prints = new List<PricePrint>
            {
                Print(1, 100.00m, 400m), Print(1, 100.25m, 380m), Print(1, 100.50m, 360m),
                Print(5, 101.00m, 900m),
                Print(9, 102.00m, 120m)
            };

            var top = RangeMath.Biggest(prints, 2, 0m);

            Check("the biggest print comes first", top.Length == 2 && top[0].Volume == 900m);
            Check("and it is the one from the bar that actually had it", top[0].Bar == 5);
            Check("the runner-up is the next biggest anywhere", top[1].Volume == 400m);
        }

        private static void AFloorKeepsSmallPrintsOffTheChart()
        {
            var prints = new List<PricePrint> { Print(1, 100m, 90m), Print(2, 101m, 40m) };

            var floored = RangeMath.Biggest(prints, 5, 50m);
            var free = RangeMath.Biggest(prints, 5, 0m);

            Check("a floor drops what is under it", floored.Length == 1 && floored[0].Volume == 90m);
            Check("no floor marks the biggest whatever they are", free.Length == 2);
            Check("asking for none marks none", RangeMath.Biggest(prints, 0, 0m).Length == 0);
        }

        private static void RankingIsStableWhenSizesTie()
        {
            // Three identical sizes. Whatever order they arrive in, the same two are marked --
            // otherwise the chart reshuffles its own marks between frames.
            var forwards = new List<PricePrint> { Print(1, 100m, 500m), Print(2, 101m, 500m), Print(3, 102m, 500m) };
            var backwards = new List<PricePrint> { Print(3, 102m, 500m), Print(2, 101m, 500m), Print(1, 100m, 500m) };

            var a = RangeMath.Biggest(forwards, 2, 0m);
            var b = RangeMath.Biggest(backwards, 2, 0m);

            Check("ties break the same way whatever the input order",
                  a[0].Bar == b[0].Bar && a[1].Bar == b[1].Bar);
            Check("and they break on the earlier bar", a[0].Bar == 1 && a[1].Bar == 2);
        }

        private static void TheBiggestDeltaLevelsAreByAbsoluteSize()
        {
            // A heavily negative level is as much "biggest delta" as a heavily positive one.
            var profile = Shape(new[] { 100m, 100m, 100m, 100m },
                                new[] { 10m, -90m, 30m, 60m });

            var top = RangeMath.BiggestDelta(profile, 2);

            Check("two are returned", top.Length == 2);
            Check("the largest is the one furthest from zero either way",
                  profile.Levels[top[0]].Delta == -90m);
            Check("then the next furthest", profile.Levels[top[1]].Delta == 60m);
        }

        private static void LevelsWithNoDeltaAreNotRanked()
        {
            var profile = Shape(new[] { 100m, 100m, 100m }, new[] { 0m, 0m, 0m });

            Check("a profile with no net aggression ranks nothing",
                  RangeMath.BiggestDelta(profile, 5).Length == 0);
            Check("and neither does nothing at all", RangeMath.BiggestDelta(null, 5).Length == 0);
        }

        #endregion

        #region The trade box

        private static void TheBoxNamesTheTradeAndItsRisk()
        {
            var box = TradeBox.Build(Inputs(Running(), 2m));

            Check("the headline is the side", box.Headline == "LONG");
            Check("the entry is shown", Cell(box, "ENTRY", 0) == "100.75");
            Check("the stop is shown", Cell(box, "STOP", 0) == "99.75");

            // Entry 100.75, stop 99.75: a whole point, four ticks at a quarter-point tick.
            Check("risk is in ticks", Cell(box, "STOP", 1) == "4t");
            Check("the target is the same distance again", Cell(box, "TARGET", 1) == "4t" &&
                                                           Cell(box, "TARGET", 0) == "101.75");
            Check("and it is labelled as one R", Cell(box, "TARGET", 3) == "1R");
        }

        private static void MoneyAppearsOnlyWhenThePlatformGaveATickValue()
        {
            var priced = TradeBox.Build(Inputs(Running(), 2m));
            var unpriced = TradeBox.Build(Inputs(Running(), 0m));

            Check("money is shown at the given tick value", Cell(priced, "STOP", 2) == "$8.00");
            Check("with no tick value there is no money", Cell(unpriced, "STOP", 2) == "");
            Check("and the box says why", Warned(unpriced, "no tick value"));
            Check("a priced box does not warn", !Warned(priced, "no tick value"));
        }

        private static void AnOpenTradeReportsWhereItStands()
        {
            var run = Running();
            var input = Inputs(run, 2m);

            // Price has run eight ticks past the entry.
            input.Facts.Close = run.Setup.Entry + 2m;

            var box = TradeBox.Build(input);

            Check("an open trade is live", box.Status.StartsWith("LIVE"));
            Check("it reports the open result in ticks", box.Status.Contains("+8t"));
            Check("and in money", box.Status.Contains("$16.00"));
            Check("and whether the target was reached", box.Status.Contains("target not reached"));
        }

        private static void AClosedTradeReportsHowItEnded()
        {
            var run = Running();
            run.ExitBar = 20;
            run.ExitLevel = run.Setup.Entry + 1m;      // four ticks better than entry
            run.TargetBar = 18;

            var box = TradeBox.Build(Inputs(run, 2m));

            Check("a finished trade says so", box.Status.StartsWith("CLOSED"));
            Check("it reports the result off the exit", box.Status.Contains("+4t"));
            Check("in money too", box.Status.Contains("$8.00"));
            Check("and notes the target was reached first", box.Status.Contains("after the target"));
        }

        private static void AnOverCapSetupIsNotPresentedAsATrade()
        {
            var run = Running();
            var setup = run.Setup;
            setup.OverCap = true;
            run.Setup = setup;

            var box = TradeBox.Build(Inputs(run, 2m));

            Check("an over-cap setup is not called a trade", box.Status.StartsWith("NOT A TRADE"));
            Check("it is toned down rather than coloured up", box.HeadlineTone == Tone.Muted);
            Check("and it carries no trail", Find(box, "TRAIL") == null);
        }

        private static void AnUnresolvedBarIsNotResolved()
        {
            var run = Running();
            run.SameBar = true;
            run.TargetBar = 20;
            run.ExitBar = 20;
            run.ExitLevel = run.Setup.Stop;

            var box = TradeBox.Build(Inputs(run, 2m));

            Check("a bar that touched both is left unresolved", box.Status.StartsWith("UNRESOLVED"));
            Check("and it is flagged rather than coloured as a win or a loss",
                  box.StatusTone == Tone.Warning);
        }

        private static void TheBoxCountsHowManyLayersAgree()
        {
            var input = Inputs(Running(), 2m);
            var all = TradeBox.Build(input);

            input.Migration = Side.Sell;
            var split = TradeBox.Build(input);

            Check("three layers pointing up is called out", Cell(all, "AGREE", 0) == "3 of 3 point up");
            Check("and named as a setup bar", Cell(all, "AGREE", 1) == "this is a setup bar");
            Check("two of three is counted honestly", Cell(split, "AGREE", 0) == "2 of 3 point up");
            Check("and not called a setup", Cell(split, "AGREE", 1) == "not all of them");
        }

        private static void TheBoxSaysWhenAValueAreaIsUnknown()
        {
            var input = Inputs(Running(), 2m);
            input.Facts.HasValue = false;
            input.BarsMissingValue = 4;
            input.BarsInView = 60;

            var box = TradeBox.Build(input);

            Check("an unknown value area is named, not filled in",
                  Cell(box, "MIGRATION", 1) == "no per-price data on this bar");
            Check("and the count in view is reported", Warned(box, "4 of 60"));
        }

        private static void LayerFailuresReachTheBox()
        {
            var input = Inputs(Running(), 2m);
            input.Errors = new List<string> { "ribbon layer failed -- NullReferenceException" };

            var box = TradeBox.Build(input);

            Check("a layer that failed is reported in the box, not swallowed",
                  Warned(box, "ribbon layer failed"));
        }

        #endregion

        #region Trailing

        private static void TheTrailOnlyEverTightens()
        {
            var longTrail = new Trailer();
            longTrail.Start(Side.Buy, 100m);

            var movedUp = longTrail.Offer(101m);
            var ignoredDown = longTrail.Offer(99m);

            var shortTrail = new Trailer();
            shortTrail.Start(Side.Sell, 100m);

            var movedDown = shortTrail.Offer(99m);
            var ignoredUp = shortTrail.Offer(101m);

            Check("a long stop moves up and never down",
                  movedUp && !ignoredDown && longTrail.Level == 101m);
            Check("a short stop moves down and never up",
                  movedDown && !ignoredUp && shortTrail.Level == 99m);
        }

        private static void AnInactiveTrailIgnoresEverything()
        {
            var trail = new Trailer();
            var moved = trail.Offer(120m);

            trail.Start(Side.Buy, 100m);
            trail.Stop();
            var afterStop = trail.Offer(120m);

            Check("a stop that was never started does not move", !moved && !afterStop && !trail.Active);
        }

        #endregion

        #region Odds and ends

        private static void AverageVolumeIsTheMeanOfTheRange()
        {
            var bars = new List<BarFacts>();
            bars.Add(Move(0, 0, 100m));
            bars.Add(Move(1, 0, 200m));
            bars.Add(Move(2, 0, 300m));
            bars.Add(Move(3, 0, 900m));

            Check("the average covers exactly the bars asked for",
                  EffortMath.AverageVolume(bars, 0, 2) == 200m);
            Check("an out-of-range start is clamped, not wrapped",
                  EffortMath.AverageVolume(bars, -5, 2) == 200m);
            Check("an empty range averages nothing", EffortMath.AverageVolume(bars, 3, 2) == 0m);
        }

        private static void CompactKeepsTheMagnitude()
        {
            Check("hundreds print whole", EffortMath.Compact(412m) == "412");
            Check("thousands keep a decimal", EffortMath.Compact(1240m) == "1.2k");
            Check("tens of thousands keep it too", EffortMath.Compact(24500m) == "24.5k");
            Check("hundreds of thousands drop it", EffortMath.Compact(124500m) == "125k");
            Check("negatives keep their sign", EffortMath.Compact(-1240m) == "-1.2k");
        }

        #endregion

        #region Building test data

        /// <summary>A bar with a given delta and a two-and-a-half point range.</summary>
        private static BarFacts Sized(int bar, decimal low, decimal delta)
        {
            var facts = new BarFacts();
            facts.Bar = bar;
            facts.Low = low;
            facts.High = low + 2.5m;
            facts.Open = low;
            facts.Close = low + 1m;
            facts.Volume = 1000m;
            facts.Delta = delta;
            return facts;
        }


        /// <summary>Bars oscillating around a centre, finishing where they started.</summary>
        private static List<BarFacts> Rotation(int count, decimal centre, decimal half)
        {
            var bars = new List<BarFacts>();

            for (var i = 0; i < count; i++)
            {
                var up = i % 2 == 0;
                var bar = new BarFacts();
                bar.Bar = i;
                bar.Open = centre;
                bar.Close = centre;
                bar.High = centre + (up ? half : half / 2m);
                bar.Low = centre - (up ? half / 2m : half);
                bar.Volume = 500m;
                bars.Add(bar);
            }

            return bars;
        }

        /// <summary>One bar of a clean directional leg: opens at the low, closes at the high.</summary>
        private static BarFacts Step(int bar, decimal low, decimal height)
        {
            var facts = new BarFacts();
            facts.Bar = bar;
            facts.Low = low;
            facts.Open = low;
            facts.High = low + height;
            facts.Close = low + height;
            facts.Volume = 500m;
            return facts;
        }


        /// <summary>
        /// One bar of a leg: its low, a high half a point above it, and the delta that got it
        /// there. A fixed bar height means a sequence of lows is also a sequence of highs, so one
        /// helper writes both a low-making leg and a high-making one.
        /// </summary>
        private static void Leg(List<BarFacts> bars, List<decimal> cumulative, int bar,
                                decimal low, decimal delta)
        {
            var facts = new BarFacts();
            facts.Bar = bar;
            facts.Low = low;
            facts.High = low + 0.5m;
            facts.Open = low + 0.25m;
            facts.Close = low + 0.25m;
            facts.Volume = 500m;
            facts.Delta = delta;

            bars.Add(facts);
            cumulative.Add((cumulative.Count == 0 ? 0m : cumulative[cumulative.Count - 1]) + delta);
        }


        private static PricePrint Print(int bar, decimal price, decimal volume)
        {
            var print = new PricePrint();
            print.Bar = bar;
            print.Price = price;
            print.Volume = volume;
            return print;
        }


        /// <summary>
        /// A profile from volumes and deltas per tick, from 100.00 up. The shape is written by
        /// hand so every cluster test says what the auction did, not what the code does.
        /// </summary>
        private static RangeProfile Shape(decimal[] volumes, decimal[] deltas)
        {
            var builder = new RangeProfileBuilder();

            for (var i = 0; i < volumes.Length; i++)
            {
                if (volumes[i] <= 0m) continue;

                var delta = deltas[i];
                var ask = (volumes[i] + delta) / 2m;
                var bid = volumes[i] - ask;

                builder.Add(100m + Tick * i, volumes[i], bid, ask);
            }

            var profile = builder.Build(Tick, 4000);
            if (profile == null || profile.Count != volumes.Length)
                throw new Exception("test shape does not span its own range");

            return profile;
        }


        /// <summary>A long that is running: entry 100.75, stop 99.75, target 101.75.</summary>
        private static SetupRun Running()
        {
            var setup = new Setup();
            setup.Bar = 12;
            setup.Side = Side.Buy;
            setup.Entry = 100.75m;
            setup.Stop = 99.75m;
            setup.Target = 101.75m;

            var run = new SetupRun();
            run.Setup = setup;
            run.Levels.Add(setup.Stop);
            return run;
        }

        private static BoxInputs Inputs(SetupRun run, decimal tickCost)
        {
            var facts = Traded(20, 100m, 100.75m, 101m, 99.75m, 800m, 300m);
            facts.HasValue = true;
            facts.ValueLow = 99.90m;
            facts.ValueHigh = 100.60m;
            facts.Poc = 100.25m;

            var effort = new EffortVerdict();
            effort.Side = Side.Buy;
            effort.Basis = EffortBasis.Both;
            effort.UpCost = 100m;
            effort.DownCost = 400m;
            effort.HasUpCost = true;
            effort.HasDownCost = true;

            var input = new BoxInputs();
            input.LastBar = 20;
            input.Facts = facts;
            input.Migration = Side.Buy;
            input.Effort = effort;
            input.EffortWindowBars = 20;
            input.Run = run;
            input.Tick = Tick;
            input.TickCost = tickCost;
            input.Price = price => price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            input.BarsInView = 60;
            return input;
        }

        /// <summary>No profile under the price: the level gate has nothing to work with.</summary>
        private static Location NoLevel { get { return new Location(); } }

        private static BoxRow Find(BoxModel box, string label)
        {
            for (var i = 0; i < box.Rows.Count; i++)
            {
                if (!box.Rows[i].Section && box.Rows[i].Label == label) return box.Rows[i];
            }

            return null;
        }

        private static string Cell(BoxModel box, string label, int column)
        {
            var row = Find(box, label);
            if (row == null) return null;

            return column < row.Cells.Length ? row.Cells[column] : "";
        }

        private static bool Warned(BoxModel box, string fragment)
        {
            for (var i = 0; i < box.Warnings.Count; i++)
            {
                if (box.Warnings[i].Contains(fragment)) return true;
            }

            return false;
        }


        /// <summary>A closed bar on which all three layers point up, so a long setup is there.</summary>
        private static void Agreeing(SetupTracker tracker, int bar, decimal open)
        {
            Agreeing(tracker, bar, open, 99.50m, 0);
        }

        private static void Agreeing(SetupTracker tracker, int bar, decimal open, decimal valueLow, int cap)
        {
            // The low dips one tick under the value area and the stop goes two ticks under that,
            // so the bar does not trade through its own stop.
            var facts = Traded(bar, open, open + 0.75m, open + 1m, valueLow - 0.25m, 800m, 300m);
            facts.HasValue = true;
            facts.ValueLow = valueLow;
            facts.ValueHigh = open + 0.60m;
            facts.Poc = open + 0.25m;

            Setup setup;
            var found = EffortMath.Confluence(Side.Buy, facts, Verdict(Side.Buy), Side.None, NoLevel,
                                              false, Tick, 2, cap, out setup);

            tracker.Advance(bar, facts, Side.None, found, setup);
        }

        /// <summary>A closed bar that agrees with nothing, so it can only affect a running setup.</summary>
        private static void Silent(SetupTracker tracker, int bar, decimal low)
        {
            var facts = Traded(bar, low + 0.25m, low + 0.25m, low + 0.50m, low, 100m, 0m);

            tracker.Advance(bar, facts, Side.None, false, new Setup());
        }

        /// <summary>A closed bar carrying aggression, for testing what moves the trail.</summary>
        private static void Push(SetupTracker tracker, int bar, decimal low, decimal high, Side aggression)
        {
            var facts = Traded(bar, low, high, high, low, 800m, aggression == Side.Buy ? 300m : -300m);

            tracker.Advance(bar, facts, aggression, false, new Setup());
        }


        private static PriceVolume Level(decimal price, decimal volume)
        {
            var level = new PriceVolume();
            level.Price = price;
            level.Volume = volume;
            level.Ask = volume / 2m;
            level.Bid = volume / 2m;
            return level;
        }

        private static List<PriceVolume> Levels(decimal from, params decimal[] volumes)
        {
            var levels = new List<PriceVolume>();
            for (var i = 0; i < volumes.Length; i++)
                levels.Add(Level(from + Tick * i, volumes[i]));

            return levels;
        }

        private static BarFacts Bar(int index, bool value, decimal poc, decimal valueLow, decimal valueHigh)
        {
            var bar = new BarFacts();
            bar.Bar = index;
            bar.HasValue = value;
            bar.Poc = poc;
            bar.ValueLow = valueLow;
            bar.ValueHigh = valueHigh;
            return bar;
        }

        /// <summary>A bar described only by how far it travelled and what it cost.</summary>
        private static BarFacts Move(int index, int ticks, decimal volume)
        {
            var bar = new BarFacts();
            bar.Bar = index;
            bar.Open = 100m;
            bar.Close = 100m + Tick * ticks;
            bar.High = Math.Max(bar.Open, bar.Close);
            bar.Low = Math.Min(bar.Open, bar.Close);
            bar.Volume = volume;
            return bar;
        }

        private static BarFacts Traded(int index, decimal open, decimal close, decimal high, decimal low,
                                       decimal volume, decimal delta)
        {
            var bar = new BarFacts();
            bar.Bar = index;
            bar.Open = open;
            bar.Close = close;
            bar.High = high;
            bar.Low = low;
            bar.Volume = volume;
            bar.Delta = delta;
            return bar;
        }

        private static EffortVerdict Verdict(Side side)
        {
            var verdict = new EffortVerdict();
            verdict.Side = side;
            verdict.Basis = side == Side.None ? EffortBasis.Insufficient : EffortBasis.Both;
            return verdict;
        }

        /// <summary>A humped shape with noise. It encodes a shape of bar, nothing about the code.</summary>
        private static decimal[] Noisy(int seed, int count)
        {
            var volumes = new decimal[count];
            var value = seed * 7919;

            for (var i = 0; i < count; i++)
            {
                value = (value * 1103515245 + 12345) & 0x7fffffff;

                var fromMiddle = i - count / 2;
                if (fromMiddle < 0) fromMiddle = -fromMiddle;

                var hump = count / 2 - fromMiddle;
                volumes[i] = hump * 3m + value % 30 + 1m;
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
