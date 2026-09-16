using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OceansCurrent;

namespace OceansCurrent.Tests
{
    /// <summary>
    /// Checks the parts of Ocean's Current that would be wrong quietly.
    ///
    /// The recurring failure mode in a blended score is not a crash, it is a factor that cannot
    /// be computed voting zero anyway and dragging the blend toward neutral for reasons nothing
    /// on the chart could show. Most of what follows is about that: an absent input has to stay
    /// absent, through the factor, through the weight normalisation, into the log row.
    ///
    /// The other half is the state machine, which is path-dependent and therefore the one thing
    /// a chart reload can silently disagree with itself about.
    /// </summary>
    static class Program
    {
        private const decimal Tick = 0.25m;
        private static int _failures;

        private static readonly TimeZoneInfo Houston =
            TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

        static int Main()
        {
            SessionWindows();
            OvernightWrapsMidnight();

            PriorDayRollsAtTheEveningReopen();
            AHalfSessionIsNotAPriorDay();
            OvernightFreezesAtTheCashOpen();
            TheAnchorResetsTheAccumulators();
            SigmaIsAbsentUntilPriceHasSpread();

            VwapScoresInStandardDeviations();
            VwapSaturatesAtTwoSigma();
            ADisagreeingSlopeHalvesTheVote();
            LongGammaTurnsAnExtremeAround();
            NoVwapMeansNoVote();

            ThrustIsPricedAgainstItsOwnYardstick();
            NoYardstickMeansNoVote();
            DivergenceHalvesAndFlags();
            TheYardstickIsFedAfterTheReadingItScales();

            InventoryReadsTheCloseInsideTheRange();
            AGapAddsUpToHalfTheVote();
            AStretchedOpenIsDiscounted();
            NoOvernightMeansNoVote();
            AFlatOvernightHasNoPosition();

            TheLadderIsQuarterEachWhenAllFourAreKnown();
            TheLadderScalesToTheReferencesThatExist();
            NoReferencesMeansNoVote();
            ASweepAndReclaimReadsAgainstTheBreak();
            ABreakThatHoldsIsNotASweep();
            TheSweepPenaltyDecaysToNothing();

            AnAbsentFactorLeavesTheAverageAlone();
            AnAbsentFactorIsNotAZeroVote();
            EveryFactorAbsentMeansNoScore();
            TheRegimeReweightsWithoutChangingSigns();
            TheWallDamperNeverFlipsTheSign();

            HysteresisNeedsMoreToEnterThanToLeave();
            TheDwellHoldsTheStateShut();
            LongCannotBecomeShortWithoutPassingNeutral();
            AnUnscorableBarHoldsTheState();

            TheFlipLevelIsTheNearestOneBelow();
            NoReferenceBelowMeansNoFlipLevel();
            TheFlipLevelIsOnATick();

            ARebuildReproducesTheStateSequence();
            TheForminBarChangesNothingCommitted();
            NoFactorCanSeeTheNextBar();
            NothingEverReadsABarThatHasNotClosed();
            BarsOutOfOrderAreRefused();

            AGoodRowParses();
            AMissingWallIsAbsentNotZero();
            AMalformedRowHasNoRegime();
            ScientificNotationSurvives();
            StalenessIsMeasuredFromTheStamp();

            TheLogRowLeavesAbsentFactorsBlank();
            AReplayedDayReplacesItsRowsRatherThanDoublingThem();
            ThePanelSaysAbstainedNotNeutral();
            ThePanelWeightsAreTheOnesActuallyVoting();
            TheRegimeShowsUpInTheWeightColumn();
            ThePanelSaysNoVoteRatherThanZeroPercent();
            ASwitchedOffFactorSaysSoRatherThanNa();
            ThePanelNamesEveryFactor();
            EachSessionGivesTheOvernightItsOwnVwap();
            TradeGexIsConvertedTheWayTradeGexDrawsIt();
            TradeGexsRatioOfOneIsRefused();
            AFarWallIsAbsentButTheRegimeStands();
            ALiveTradeGexReadingIsNeverStale();
            OnlyTheSameInstrumentsGammaIsRead();
            WithoutTradeGexTheBridgeSaysSo();
            TheEngineActsOnTheLevelItPrints();
            PeekAgreesWithAdvance();
            AScanIsCheapEnoughForEveryBarClose();
            TheTrackRecordGradesEachCallEntryToExit();
            TheCalendarKeepsOnlyWhatCanMoveNq();
            ThePanelSaysWhatChangesItInWords();
            TheBadgeSaysWaitingRatherThanZero();

            Console.WriteLine(_failures == 0
                ? "All checks passed."
                : _failures + " CHECK(S) FAILED.");

            return _failures == 0 ? 0 : 1;
        }

        #region Sessions

        static void SessionWindows()
        {
            var cfg = new SessionConfig();

            Check("08:30 is the cash session", cfg.PhaseOf(T(8, 30)) == Phase.Rth);
            Check("14:59 is still the cash session", cfg.PhaseOf(T(14, 59)) == Phase.Rth);
            Check("15:00 is not", cfg.PhaseOf(T(15, 0)) == Phase.Between);
            Check("16:30 is the halt, not a session", cfg.PhaseOf(T(16, 30)) == Phase.Between);
            Check("17:00 is the evening reopen", cfg.PhaseOf(T(17, 0)) == Phase.Overnight);
            Check("08:29 is still overnight", cfg.PhaseOf(T(8, 29)) == Phase.Overnight);
        }

        static void OvernightWrapsMidnight()
        {
            var cfg = new SessionConfig();

            Check("23:59 is overnight", cfg.PhaseOf(T(23, 59)) == Phase.Overnight);
            Check("00:01 is overnight", cfg.PhaseOf(T(0, 1)) == Phase.Overnight);
            Check("03:00 is overnight", cfg.PhaseOf(T(3, 0)) == Phase.Overnight);
        }

        static void PriorDayRollsAtTheEveningReopen()
        {
            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, 5m);   // full day, seen from its open
            AddDay(feed, new DateTime(2026, 3, 11), 20200m, 5m);

            var s = Walk(feed);

            Check("the prior day high is known once the day has rolled", s.PriorRthHigh.Known);

            // Day one's cash session ran 20000 upward in 5-point steps over 13 bars.
            var expected = 20000m + 12 * 5m + 2m;
            Check("the prior day high is the previous cash session's high",
                s.PriorRthHigh.Value == expected);

            Check("the prior day low is its low", s.PriorRthLow.Value == 20000m - 2m);
            Check("the prior close is its last close", s.PriorRthClose.Value == 20000m + 12 * 5m);
        }

        static void AHalfSessionIsNotAPriorDay()
        {
            // The chart begins at 10:00, so the first cash session's open was never on it.
            var feed = new Feed();
            AddRth(feed, new DateTime(2026, 3, 10), 20000m, 5m, fromHour: 10);
            AddOvernight(feed, new DateTime(2026, 3, 11), 20100m, 1m);
            AddRth(feed, new DateTime(2026, 3, 11), 20150m, 5m, fromHour: 8);

            var s = Walk(feed);

            Check("a session whose open was off-screen never becomes the prior day",
                !s.PriorRthHigh.Known && !s.PriorRthLow.Known && !s.PriorRthClose.Known);
        }

        static void OvernightFreezesAtTheCashOpen()
        {
            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, 5m);
            AddOvernight(feed, new DateTime(2026, 3, 11), 20100m, 3m);
            AddRth(feed, new DateTime(2026, 3, 11), 20500m, 5m, fromHour: 8);

            var s = Walk(feed);

            // The overnight ran 20100 up in 3-point steps over 31 bars, then the cash session
            // opened 400 points away and never touched it.
            Check("the overnight high is the overnight's, not the morning's",
                s.OnHigh.Known && s.OnHigh.Value == 20100m + 30 * 3m + 2m);

            Check("the overnight low is the overnight's", s.OnLow.Value == 20100m - 2m);
            Check("the overnight close is its last", s.OnClose.Value == 20100m + 30 * 3m);
        }

        static void TheAnchorResetsTheAccumulators()
        {
            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, 5m);
            AddOvernight(feed, new DateTime(2026, 3, 11), 20100m, 3m);

            var atNight = Walk(feed);
            Check("anchored on the cash open, the overnight accumulates nothing",
                atNight.BarsSinceAnchor == 0 && atNight.Cvd == 0m);
            Check("and yesterday's session VWAP is not left standing overnight",
                !atNight.Vwap.Known);

            AddRth(feed, new DateTime(2026, 3, 11), 20150m, 5m, fromHour: 8);
            var atNoon = Walk(feed);

            Check("the cash session starts counting from its own open",
                atNoon.BarsSinceAnchor == 13);

            // Summed off the feed rather than off the generator's formula, so this keeps saying
            // what it means if the generator changes.
            var sessionDelta = 0m;
            var everything = 0m;
            for (var i = 0; i < feed.Count; i++)
            {
                everything += feed.Delta(i);
                if (i >= feed.Count - 13) sessionDelta += feed.Delta(i);
            }

            Check("cumulative delta is exactly this session's bars", atNoon.Cvd == sessionDelta);
            Check("and not the whole feed's", atNoon.Cvd != everything && everything != 0m);
        }

        static void SigmaIsAbsentUntilPriceHasSpread()
        {
            var s = new SessionState(new SessionConfig(), 100);

            // Through a real 08:30 transition: a chart that simply starts mid-session has no
            // anchored VWAP at all, which is a separate rule tested above.
            s.Absorb(0, new DateTime(2026, 3, 10, 8, 0, 0), 20000m, 20000m, 20000m, 20000m, 100m, 0m);
            s.Absorb(1, new DateTime(2026, 3, 10, 8, 30, 0), 20000m, 20000m, 20000m, 20000m, 100m, 0m);

            Check("one flat bar has a VWAP", s.Vwap.Known && s.Vwap.Value == 20000m);
            Check("but no spread, so no sigma", !s.Sigma.Known);

            var sub = Factors.Vwap(20001m, s.Vwap, s.Sigma, Level.None, false, new FactorConfig());
            Check("and with no sigma the factor abstains rather than reading maximum",
                !sub.Available);
        }

        #endregion

        #region F1 VWAP

        static void VwapScoresInStandardDeviations()
        {
            var cfg = new FactorConfig();

            var sub = Factors.Vwap(20010m, Level.At(20000m), Level.At(10m), Level.None, false, cfg);
            Check("one sigma above VWAP is +50", sub.Available && sub.Value == 50m);

            var below = Factors.Vwap(19990m, Level.At(20000m), Level.At(10m), Level.None, false, cfg);
            Check("one sigma below is -50", below.Value == -50m);

            var on = Factors.Vwap(20000m, Level.At(20000m), Level.At(10m), Level.None, false, cfg);
            Check("on VWAP is zero, and that is a reading not an absence",
                on.Available && on.Value == 0m);
        }

        static void VwapSaturatesAtTwoSigma()
        {
            var cfg = new FactorConfig();

            var two = Factors.Vwap(20020m, Level.At(20000m), Level.At(10m), Level.None, false, cfg);
            var five = Factors.Vwap(20050m, Level.At(20000m), Level.At(10m), Level.None, false, cfg);

            Check("two sigma is the maximum", two.Value == 100m);
            Check("five sigma is no more than two", five.Value == 100m);
        }

        static void ADisagreeingSlopeHalvesTheVote()
        {
            var cfg = new FactorConfig();

            var agreeing = Factors.Vwap(20010m, Level.At(20000m), Level.At(10m), Level.At(3m), false, cfg);
            var against = Factors.Vwap(20010m, Level.At(20000m), Level.At(10m), Level.At(-3m), false, cfg);

            Check("above a rising VWAP is the full vote", agreeing.Value == 50m);
            Check("above a falling VWAP is half of it", against.Value == 25m);
        }

        static void LongGammaTurnsAnExtremeAround()
        {
            var cfg = new FactorConfig();

            var trending = Factors.Vwap(20016m, Level.At(20000m), Level.At(10m), Level.None, true, cfg);
            var inside = Factors.Vwap(20010m, Level.At(20000m), Level.At(10m), Level.None, true, cfg);

            Check("in long gamma, 1.6 sigma above reads as stretched and turns bearish",
                trending.Value == -40m);

            Check("inside the fade threshold nothing is inverted", inside.Value == 50m);

            var offRegime = Factors.Vwap(20016m, Level.At(20000m), Level.At(10m), Level.None, false, cfg);
            Check("and outside long gamma the same bar is bullish", offRegime.Value == 80m);
        }

        static void NoVwapMeansNoVote()
        {
            var cfg = new FactorConfig();
            var sub = Factors.Vwap(20000m, Level.None, Level.At(10m), Level.None, false, cfg);

            Check("no VWAP, no vote", !sub.Available && sub.Absent != null);
        }

        #endregion

        #region F2 CVD

        static void ThrustIsPricedAgainstItsOwnYardstick()
        {
            var cfg = new FactorConfig();

            // A thrust of 1000 against a typical 1000 is half of the saturating 2x.
            var sub = Factors.Cvd(1000m, true, 0m, Level.At(1000m), false, false, false, false, cfg);
            Check("a typical thrust is half the scale", sub.Available && sub.Value == 50m);

            var double_ = Factors.Cvd(2000m, true, 0m, Level.At(1000m), false, false, false, false, cfg);
            Check("twice typical saturates", double_.Value == 100m);

            var negative = Factors.Cvd(-1000m, true, 0m, Level.At(1000m), false, false, false, false, cfg);
            Check("selling reads negative", negative.Value == -50m);

            var fromBase = Factors.Cvd(1500m, true, 500m, Level.At(1000m), false, false, false, false, cfg);
            Check("the thrust is measured from K bars back, not from zero", fromBase.Value == 50m);
        }

        static void NoYardstickMeansNoVote()
        {
            var cfg = new FactorConfig();

            var noNorm = Factors.Cvd(1000m, true, 0m, Level.None, false, false, false, false, cfg);
            Check("with nothing to scale against, the thrust abstains", !noNorm.Available);

            var noHistory = Factors.Cvd(1000m, false, 0m, Level.At(1000m), false, false, false, false, cfg);
            Check("and a session too young to look back abstains", !noHistory.Available);
        }

        static void DivergenceHalvesAndFlags()
        {
            var cfg = new FactorConfig();

            var confirmed = Factors.Cvd(1000m, true, 0m, Level.At(1000m), true, false, true, false, cfg);
            var diverging = Factors.Cvd(1000m, true, 0m, Level.At(1000m), true, false, false, false, cfg);

            Check("a new high delta confirms is the full vote", confirmed.Value == 50m);
            Check("a new high delta will not confirm is half", diverging.Value == 25m);
            Check("and it says so", diverging.Note == "DIVERGENCE");
            Check("a confirmed high carries no flag", confirmed.Note == null);

            var lowSide = Factors.Cvd(-1000m, true, 0m, Level.At(1000m), false, true, false, false, cfg);
            Check("the low side diverges the same way", lowSide.Value == -25m && lowSide.Note == "DIVERGENCE");
        }

        /// <summary>
        /// The yardstick a thrust is divided by must be built from EARLIER thrusts only. Feed it
        /// the current bar first and the first measurable thrust divides by itself, which reads
        /// as a solid half-scale push on a bar nothing is yet known about.
        /// </summary>
        static void TheYardstickIsFedAfterTheReadingItScales()
        {
            var feed = TwoDays();
            var time = Clock();
            var engine = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);

            for (var i = 0; i < feed.Count; i++)
                engine.Advance(i, feed, time, GexSnapshot.Empty, false, -1);

            var firstVote = -1;
            var abstainedForWantOfAYardstick = -1;

            for (var i = 0; i < engine.Count; i++)
            {
                var sub = engine[i].Subs[(int)FactorId.Cvd];

                if (!sub.Available && sub.Absent == "no thrust norm" && abstainedForWantOfAYardstick < 0)
                    abstainedForWantOfAYardstick = i;

                if (sub.Available) { firstVote = i; break; }
            }

            Check("the thrust does eventually vote", firstVote > 0);
            Check("but the first measurable thrust has no yardstick and abstains",
                abstainedForWantOfAYardstick >= 0);
            Check("and that happens before any thrust is scored",
                abstainedForWantOfAYardstick < firstVote);
        }

        #endregion

        #region F4 inventory

        static void InventoryReadsTheCloseInsideTheRange()
        {
            var cfg = new FactorConfig();

            var top = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20100m),
                                        Level.None, Level.None, cfg);
            Check("closing on the overnight high is +50 before any gap", top.Value == 50m);

            var mid = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20050m),
                                        Level.None, Level.None, cfg);
            Check("closing mid-range is nothing", mid.Available && mid.Value == 0m);

            var bottom = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20000m),
                                           Level.None, Level.None, cfg);
            Check("closing on the low is -50", bottom.Value == -50m);
        }

        static void AGapAddsUpToHalfTheVote()
        {
            var cfg = new FactorConfig();

            var full = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20050m),
                                         Level.At(20090m), Level.At(20050m), cfg);
            Check("a 40-point gap up is the whole gap term", full.Value == 50m);

            var half = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20050m),
                                         Level.At(20070m), Level.At(20050m), cfg);
            Check("a 20-point gap is half of it", half.Value == 25m);

            var huge = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20050m),
                                         Level.At(20500m), Level.At(20050m), cfg);
            Check("a 450-point gap is still just the gap term", huge.Value == 50m);
        }

        static void AStretchedOpenIsDiscounted()
        {
            var cfg = new FactorConfig();

            // Closed at the very top of the overnight AND gapped up: one-sided and stretched.
            var stretched = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20100m),
                                              Level.At(20140m), Level.At(20100m), cfg);

            Check("a stretched open is marked", stretched.Note == "INV STRETCHED");
            Check("and its vote is discounted, not cancelled", stretched.Value == 60m);

            // Same inventory, gapped the other way: not stretched, both terms stand.
            var opposed = Factors.Inventory(Level.At(20100m), Level.At(20000m), Level.At(20100m),
                                            Level.At(20060m), Level.At(20100m), cfg);
            Check("gapping against one-sided inventory is not stretched", opposed.Note == null);
            Check("and reads as the two terms opposing", opposed.Value == 0m);
        }

        static void NoOvernightMeansNoVote()
        {
            var cfg = new FactorConfig();

            var none = Factors.Inventory(Level.None, Level.None, Level.None,
                                         Level.At(20000m), Level.At(19990m), cfg);

            Check("with no overnight the gap alone does not get to vote", !none.Available);
        }

        static void AFlatOvernightHasNoPosition()
        {
            var cfg = new FactorConfig();

            var flat = Factors.Inventory(Level.At(20000m), Level.At(20000m), Level.At(20000m),
                                         Level.At(20000m), Level.At(20000m), cfg);

            Check("an overnight with no range cannot say where it closed inside it",
                !flat.Available);
        }

        #endregion

        #region F5 structure

        static void TheLadderIsQuarterEachWhenAllFourAreKnown()
        {
            var all = new[] { Level.At(20100m), Level.At(19900m), Level.At(20050m), Level.At(19950m) };

            Check("above all four is +100", Factors.Structure(20200m, all, 0m).Value == 100m);
            Check("below all four is -100", Factors.Structure(19800m, all, 0m).Value == -100m);
            Check("above two of four is nothing", Factors.Structure(20000m, all, 0m).Value == 0m);
            Check("above three of four is +50", Factors.Structure(20060m, all, 0m).Value == 50m);
        }

        static void TheLadderScalesToTheReferencesThatExist()
        {
            var two = new[] { Level.At(20100m), Level.None, Level.At(20050m), Level.None };

            Check("above both known references is still +100, not +50",
                Factors.Structure(20200m, two, 0m).Value == 100m);

            Check("the missing pair does not vote bearish",
                Factors.Structure(20060m, two, 0m).Value == 0m);
        }

        static void NoReferencesMeansNoVote()
        {
            var none = new[] { Level.None, Level.None, Level.None, Level.None };
            Check("no references, no ladder", !Factors.Structure(20000m, none, 0m).Available);
        }

        static void ASweepAndReclaimReadsAgainstTheBreak()
        {
            var cfg = new FactorConfig();
            var tracker = new StructureTracker(cfg, 1);
            var refs = new[] { Level.At(20100m) };

            tracker.Absorb(0, refs, 20090m, 20080m, 20085m);          // below it
            tracker.Absorb(1, refs, 20120m, 20090m, 20095m);          // poked through, closed back

            Check("a poke through that closes back is a sweep", tracker.BiasAt(1) == -50m);
            Check("and it reads against the direction of the break", tracker.BiasAt(1) < 0m);

            var down = new StructureTracker(cfg, 1);
            var low = new[] { Level.At(19900m) };
            down.Absorb(0, low, 19920m, 19910m, 19915m);
            down.Absorb(1, low, 19920m, 19880m, 19910m);

            Check("a swept low reads bullish", down.BiasAt(1) == 50m);
        }

        static void ABreakThatHoldsIsNotASweep()
        {
            var cfg = new FactorConfig();
            var tracker = new StructureTracker(cfg, 1);
            var refs = new[] { Level.At(20100m) };

            tracker.Absorb(0, refs, 20090m, 20080m, 20085m);
            tracker.Absorb(1, refs, 20120m, 20090m, 20110m);          // through and held
            tracker.Absorb(2, refs, 20130m, 20105m, 20125m);
            tracker.Absorb(3, refs, 20140m, 20110m, 20135m);
            tracker.Absorb(4, refs, 20140m, 20050m, 20060m);          // back below, too late

            Check("a break that held for the whole window is not a sweep",
                tracker.BiasAt(4) == 0m);
        }

        static void TheSweepPenaltyDecaysToNothing()
        {
            var cfg = new FactorConfig();
            var tracker = new StructureTracker(cfg, 1);
            var refs = new[] { Level.At(20100m) };

            tracker.Absorb(0, refs, 20090m, 20080m, 20085m);
            tracker.Absorb(1, refs, 20120m, 20090m, 20095m);

            Check("the penalty is full on the bar it fired", tracker.BiasAt(1) == -50m);
            Check("half gone at half the window", tracker.BiasAt(11) == -25m);
            Check("gone at the end of it", tracker.BiasAt(21) == 0m);
            Check("and stays gone", tracker.BiasAt(200) == 0m);
        }

        #endregion

        #region Blending

        static void AnAbsentFactorLeavesTheAverageAlone()
        {
            // Three factors at +80, three absent. The absent ones must not pull the average down.
            var subs = new[]
            {
                Subscore.At(80m), Subscore.At(80m), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.At(80m), Subscore.Missing("x")
            };

            Check("three agreeing factors and three absent ones average to the three",
                Blend(subs) == 80m);
        }

        static void AnAbsentFactorIsNotAZeroVote()
        {
            var present = new[]
            {
                Subscore.At(100m), Subscore.Missing("x"), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.Missing("x"), Subscore.Missing("x")
            };

            var zeroed = new[]
            {
                Subscore.At(100m), Subscore.At(0m), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.At(0m), Subscore.Missing("x")
            };

            Check("one factor at maximum with the rest absent is the maximum",
                Blend(present) == 100m);

            Check("but the same factor with the rest actually reading zero is not",
                Blend(zeroed) < 100m);
        }

        static void EveryFactorAbsentMeansNoScore()
        {
            var subs = new Subscore[6];
            for (var i = 0; i < subs.Length; i++) subs[i] = Subscore.Missing("x");

            Check("nothing to blend is no score, not a score of zero", !HasScore(subs));
        }

        static void TheRegimeReweightsWithoutChangingSigns()
        {
            var subs = new[]
            {
                Subscore.At(0m), Subscore.At(100m), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.At(0m), Subscore.Missing("x")
            };

            var flat = Blend(subs, RegimeMode.None);
            var longGamma = Blend(subs, RegimeMode.Positive);
            var shortGamma = Blend(subs, RegimeMode.Negative);

            Check("long gamma discounts the thrust factor", longGamma < flat);
            Check("short gamma upweights it", shortGamma > flat);
            Check("and neither turns a buying thrust into selling",
                longGamma > 0m && shortGamma > 0m);
        }

        static void TheWallDamperNeverFlipsTheSign()
        {
            var subs = new[]
            {
                Subscore.At(100m), Subscore.Missing("x"), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.Missing("x"), Subscore.Missing("x")
            };

            var open = Blend(subs, RegimeMode.None, false);
            var pinned = Blend(subs, RegimeMode.None, true);

            Check("pinned at a wall the score is damped", pinned < open);
            Check("but still says the same thing", pinned > 0m);
            Check("by the configured multiple", pinned == open * new FactorConfig().WallDampMultiplier);
        }

        #endregion

        #region State machine

        static void HysteresisNeedsMoreToEnterThanToLeave()
        {
            var m = Machine();

            Check("+20 is not enough to become long", Push(m, 20m, 5) == BiasState.Neutral);
            Check("+30 is", Push(m, 30m, 1) == BiasState.Long);
            Check("+15 does not end it", Push(m, 15m, 5) == BiasState.Long);
            Check("+10 does", Push(m, 10m, 1) == BiasState.Neutral);

            var down = Machine();
            Check("-20 is not enough to become short", Push(down, -20m, 5) == BiasState.Neutral);
            Check("-30 is", Push(down, -30m, 1) == BiasState.Short);
            Check("-15 does not end it", Push(down, -15m, 5) == BiasState.Short);
            Check("-10 does", Push(down, -10m, 1) == BiasState.Neutral);
        }

        static void TheDwellHoldsTheStateShut()
        {
            var m = Machine();

            Push(m, 50m, 3);
            Check("long after the dwell", m.State == BiasState.Long);

            // The bar it entered on is the first of the three, so a hard reversal costs two
            // more committed bars before it can land.
            Check("it will not leave on the very next bar", Push(m, -100m, 1) == BiasState.Long);
            Check("but it does on the third bar in the state", Push(m, -100m, 1) == BiasState.Neutral);

            // The entry bar counting toward the dwell is the whole difference between three
            // flips a day and four, so it is asserted rather than left to the arithmetic.
            var fresh = Machine();
            Push(fresh, 50m, 3);
            Check("entering counts as the first bar of the dwell", fresh.BarsInState == 1);
            Push(fresh, 50m, 2);
            Check("and two more bars serve the rest of it", fresh.BarsInState == 3);
        }

        static void LongCannotBecomeShortWithoutPassingNeutral()
        {
            var m = Machine();

            Push(m, 100m, 3);
            Check("long", m.State == BiasState.Long);

            var seen = new List<BiasState>();
            for (var i = 0; i < 12; i++) { Push(m, -100m, 1); seen.Add(m.State); }

            Check("it ends up short", m.State == BiasState.Short);

            var neutralAt = seen.IndexOf(BiasState.Neutral);
            var shortAt = seen.IndexOf(BiasState.Short);

            Check("neutral was passed through on the way", neutralAt >= 0);
            Check("in that order", shortAt > neutralAt);

            // The whole point of the rule: the reversal cannot happen on one bar.
            Check("and it took more than one bar to reverse", shortAt >= 2);
        }

        static void AnUnscorableBarHoldsTheState()
        {
            var m = Machine();
            Push(m, 100m, 3);

            Check("long", m.State == BiasState.Long);

            for (var i = 0; i < 10; i++) m.Advance(false, 0m);

            Check("a bar nothing could score leaves the state where it was",
                m.State == BiasState.Long);

            // It must not be frozen either: the dwell still ran, so real evidence still lands.
            Check("but it has not stopped listening", Push(m, -100m, 1) == BiasState.Neutral);
        }

        #endregion

        #region Flip level

        static void TheFlipLevelIsTheNearestOneBelow()
        {
            var run = Session(upward: true);
            var record = run.Last;

            Check("a long state has a flip level", record.State != BiasState.Long || record.Flip.Known);

            if (record.State == BiasState.Long && record.Flip.Known)
                Check("and it sits below price", record.Flip.Value < record.Close);
        }

        static void NoReferenceBelowMeansNoFlipLevel()
        {
            // A neutral state has nothing to invalidate, so it must not print a level.
            var engine = new BiasEngine(new SessionConfig(), new FactorConfig(),
                                        new StateConfig(), Tick);

            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, 0m);

            var time = Clock();
            for (var i = 0; i < feed.Count; i++)
                engine.Advance(i, feed, time, GexSnapshot.Empty, false, -1);

            var flat = engine.Last;
            Check("a flat day stays neutral", flat.State == BiasState.Neutral);
            Check("and prints no flip level", !flat.Flip.Known);
        }

        static void TheFlipLevelIsOnATick()
        {
            Check("a level rounds to the tick", BiasEngine.Round(20000.13m, 0.25m) == 20000.25m);
            Check("downwards too", BiasEngine.Round(20000.11m, 0.25m) == 20000m);
            Check("an unknown tick rounds to nothing", BiasEngine.Round(20000.13m, 0m) == 20000.13m);
        }

        #endregion

        #region Determinism

        static void ARebuildReproducesTheStateSequence()
        {
            var feed = TwoDays();
            var time = Clock();
            var gex = GexSnapshot.Parse(GoodRow);

            var live = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);
            var rebuilt = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);

            // The live pass is interrupted the way a real session interrupts it: a provisional
            // read of the forming bar between every commit, sometimes several.
            for (var i = 0; i < feed.Count - 1; i++)
            {
                decimal ignored;
                live.Provisional(i, feed, time, gex, true, out ignored);
                live.Advance(i, feed, time, gex, true, 60);
                live.Provisional(i + 1, feed, time, gex, true, out ignored);
                live.Provisional(i + 1, feed, time, gex, true, out ignored);
            }

            for (var i = 0; i < feed.Count - 1; i++)
                rebuilt.Advance(i, feed, time, gex, true, 60);

            Check("a rebuild commits the same number of bars", live.Count == rebuilt.Count);

            var same = live.Count == rebuilt.Count;
            for (var i = 0; same && i < live.Count; i++)
            {
                if (live[i].State != rebuilt[i].State) same = false;
                if (live[i].Score != rebuilt[i].Score) same = false;
                if (live[i].Confidence != rebuilt[i].Confidence) same = false;
                if (live[i].Flip.Known != rebuilt[i].Flip.Known) same = false;
                if (live[i].Flip.Known && live[i].Flip.Value != rebuilt[i].Flip.Value) same = false;
            }

            Check("and reproduces every state, score and flip level exactly", same);

            var moved = 0;
            for (var i = 1; i < rebuilt.Count; i++)
                if (rebuilt[i].State != rebuilt[i - 1].State) moved++;

            Check("the run is worth testing: the state actually moved", moved > 0);

            // A fixture where a factor never gets to vote tests nothing about that factor, and
            // says so nowhere. Every shipping factor has to be live somewhere in this run.
            for (var f = 0; f < BiasEngine.FactorCount; f++)
            {
                if (f == (int)FactorId.Value || f == (int)FactorId.OneTimeframing) continue;

                var voted = false;
                for (var i = 0; i < rebuilt.Count && !voted; i++)
                    if (rebuilt[i].Subs[f].Available) voted = true;

                Check("the run exercises " + BiasEngine.FactorName(f), voted);
            }
        }

        static void TheForminBarChangesNothingCommitted()
        {
            var feed = TwoDays();
            var time = Clock();

            var quiet = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);
            var noisy = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);

            for (var i = 0; i < feed.Count - 1; i++) quiet.Advance(i, feed, time, GexSnapshot.Empty, false, -1);

            for (var i = 0; i < feed.Count - 1; i++)
            {
                for (var tick = 0; tick < 5; tick++)
                {
                    decimal ignored;
                    noisy.Provisional(feed.Count - 1, feed, time, GexSnapshot.Empty, false, out ignored);
                }
                noisy.Advance(i, feed, time, GexSnapshot.Empty, false, -1);
            }

            var same = quiet.Count == noisy.Count;
            for (var i = 0; same && i < quiet.Count; i++)
                if (quiet[i].State != noisy[i].State || quiet[i].Score != noisy[i].Score) same = false;

            Check("reading the forming bar leaves the committed history untouched", same);
        }

        static void NoFactorCanSeeTheNextBar()
        {
            var feed = TwoDays();
            var time = Clock();
            var cut = feed.Count - 6;

            var truncated = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);
            for (var i = 0; i < cut; i++) truncated.Advance(i, feed, time, GexSnapshot.Empty, false, -1);

            // Same data, but the bars AFTER the cut are replaced with something wild.
            var altered = TwoDays();
            for (var i = cut; i < altered.Count; i++) altered.Wreck(i);

            var full = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);
            for (var i = 0; i < cut; i++) full.Advance(i, altered, time, GexSnapshot.Empty, false, -1);

            var same = truncated.Count == full.Count;
            for (var i = 0; same && i < truncated.Count; i++)
                if (truncated[i].Score != full[i].Score || truncated[i].State != full[i].State) same = false;

            Check("changing later bars changes nothing already committed", same);
        }

        /// <summary>
        /// The structural version of the test above, and the one that actually holds.
        ///
        /// Comparing committed scores against altered future bars only catches a peek that
        /// happens to change a number, and a peek very often does not: looking one bar ahead at
        /// a "is this the extreme of the window" test simply turns the flag off everywhere, in
        /// the altered feed and the clean one alike, and the comparison sees two identical
        /// wrong answers. A lookahead that mattered survived that test twice. This one refuses
        /// the read itself, so there is nothing for the data to hide.
        /// </summary>
        static void NothingEverReadsABarThatHasNotClosed()
        {
            var guard = new NoPeeking(TwoDays());
            var time = Clock();
            var engine = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);

            for (var i = 0; i < guard.Count; i++)
            {
                guard.Limit = i;
                engine.Advance(i, guard, time, GexSnapshot.Empty, false, -1);
            }

            Check("committing a bar never reads one that has not closed",
                guard.Peeked < 0);

            if (guard.Peeked >= 0)
                Console.WriteLine("  ...bar " + guard.PeekedAt + " read bar " + guard.Peeked);

            // The guard has to be capable of firing, or it is asserting nothing.
            var proof = new NoPeeking(TwoDays()) { Limit = 5 };
            proof.High(6);
            Check("and the guard would have said so", proof.Peeked == 6);
        }

        static void BarsOutOfOrderAreRefused()
        {
            var feed = TwoDays();
            var time = Clock();
            var engine = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);

            engine.Advance(0, feed, time, GexSnapshot.Empty, false, -1);
            engine.Advance(1, feed, time, GexSnapshot.Empty, false, -1);

            var refusedRepeat = false;
            try { engine.Advance(1, feed, time, GexSnapshot.Empty, false, -1); }
            catch (InvalidOperationException) { refusedRepeat = true; }

            var refusedSkip = false;
            try { engine.Advance(9, feed, time, GexSnapshot.Empty, false, -1); }
            catch (InvalidOperationException) { refusedSkip = true; }

            Check("a repeated bar is refused rather than double-counted", refusedRepeat);
            Check("a skipped bar is refused rather than silently missing a session roll", refusedSkip);
        }

        #endregion

        #region The GEX row

        private const string GoodRow =
            "ts_utc,spot_nq,regime,net_gex,gamma_flip,call_wall,put_wall,hvl\n" +
            "2026-09-09T13:45:12Z,24815.25,1,1.83e9,24700,24900,24550,24750\n";

        static void AGoodRowParses()
        {
            var g = GexSnapshot.Parse(GoodRow);

            Check("the regime reads", g.Regime == 1);
            Check("the flip level reads", g.GammaFlip.Known && g.GammaFlip.Value == 24700m);
            Check("the call wall reads", g.CallWall.Value == 24900m);
            Check("the put wall reads", g.PutWall.Value == 24550m);
            Check("the stamp reads as UTC", g.HasStamp && g.StampUtc.Kind == DateTimeKind.Utc);
            Check("and it is usable", g.Usable);

            Check("fifteen points from the call wall is the wall zone", g.InWallZone(24890m, 15m));
            Check("fifty points away is not", !g.InWallZone(24850m, 15m));
        }

        static void AMissingWallIsAbsentNotZero()
        {
            var row = "2026-09-09T13:45:12Z,24815.25,-1,-1.2e9,24700,,24550,\n";
            var g = GexSnapshot.Parse(row);

            Check("a blank wall is absent", !g.CallWall.Known);
            Check("not zero", g.CallWall.Value == 0m && !g.CallWall.Known);

            // The bug this exists to stop: a wall at zero is 24,800 points away and reads as
            // "never near a wall" forever.
            Check("and an absent wall never puts price in a wall zone", !g.InWallZone(20m, 15m));

            var zeroed = GexSnapshot.Parse("2026-09-09T13:45:12Z,24815.25,-1,-1.2e9,24700,0,24550,\n");
            Check("a wall written as zero is treated as absent too", !zeroed.CallWall.Known);
        }

        static void AMalformedRowHasNoRegime()
        {
            Check("an empty file has no regime", !GexSnapshot.Parse("").Usable);
            Check("a header alone has no regime", !GexSnapshot.Parse("ts_utc,spot_nq,regime\n").Usable);
            Check("a short row has no regime", !GexSnapshot.Parse("2026-09-09T13:45:12Z,1,1\n").Usable);
            Check("a bad stamp has no regime",
                !GexSnapshot.Parse("not-a-time,24815,1,1e9,1,2,3,4\n").Usable);
            Check("a blank regime field has no regime",
                !GexSnapshot.Parse("2026-09-09T13:45:12Z,24815,,1e9,1,2,3,4\n").Usable);
            Check("a mixed regime applies nothing",
                !GexSnapshot.Parse("2026-09-09T13:45:12Z,24815,0,1e9,1,2,3,4\n").Usable);
        }

        static void ScientificNotationSurvives()
        {
            var g = GexSnapshot.Parse("2026-09-09T13:45:12Z,24815.25,1,1.83e9,24700,24900,24550,24750\n");
            Check("net gamma in scientific notation reads", g.NetGex.Known && g.NetGex.Value > 1.8e9m);
        }

        static void StalenessIsMeasuredFromTheStamp()
        {
            var g = GexSnapshot.Parse(GoodRow);
            var stamp = new DateTime(2026, 9, 9, 13, 45, 12, DateTimeKind.Utc);

            Check("five minutes old is fresh", !g.IsStale(stamp.AddMinutes(5), 20));
            Check("an hour old is stale", g.IsStale(stamp.AddMinutes(60), 20));
            Check("a row with no stamp is stale by definition",
                GexSnapshot.Parse("").IsStale(stamp, 20));

            Check("the age reads in seconds", g.AgeSeconds(stamp.AddSeconds(90)) == 90);
        }

        #endregion

        #region Log and badge

        static void TheLogRowLeavesAbsentFactorsBlank()
        {
            var record = new BiasRecord
            {
                Local = new DateTime(2026, 9, 9, 10, 30, 0),
                Bar = 42,
                Close = 20000.25m,
                Subs = new[]
                {
                    Subscore.At(50m), Subscore.Missing("x"), Subscore.Missing("x"),
                    Subscore.At(-20m), Subscore.Missing("x"), Subscore.Missing("x")
                },
                Raw = 30m,
                Score = 28m,
                ScoreKnown = true,
                State = BiasState.Long,
                Confidence = 61m,
                Flip = Level.At(19980m),
                GexAgeSeconds = 240
            };

            var row = SessionLogger.Row(record, Tick);
            var f = row.Split(',');

            Check("the row has as many fields as the header",
                f.Length == SessionLogger.Header.Split(',').Length);

            Check("a factor that voted is written", f[4] == "50");
            Check("a factor that abstained is blank, not zero", f[5] == "");
            Check("the state is written in words", row.Contains("LONG"));

            // A zero here would enter the calibration regression as a real neutral vote.
            Check("no absent factor is ever written as a zero", f[5] != "0" && f[6] != "0");
        }

        static void AReplayedDayReplacesItsRowsRatherThanDoublingThem()
        {
            // The failure this pins down was silent and it cost the first calibration run: a chart
            // reload replays every historical bar, the logger appended them under the rows it had
            // already written, and 74 sessions of log came back with more than half their rows
            // duplicated. The engine replays a rebuild identically, so a replayed day has to be
            // rewritten, never extended.
            var folder = Path.Combine(Path.GetTempPath(), "oc_log_" + Guid.NewGuid().ToString("N"));

            try
            {
                var logger = new SessionLogger(folder);

                Func<int, BiasRecord> at = bar => new BiasRecord
                {
                    Local = new DateTime(2026, 9, 9, 10, 0, 0).AddMinutes(bar),
                    Bar = bar,
                    Close = 20000m + bar,
                    Subs = new Subscore[6],
                    ScoreKnown = false,
                    State = BiasState.Neutral,
                    Flip = Level.None,
                    GexAgeSeconds = -1
                };

                for (var bar = 0; bar < 5; bar++) logger.Append(at(bar), Tick);

                var path = Path.Combine(folder, "obe_20260909.csv");
                var first = File.ReadAllLines(path);
                Check("a first pass writes a header and one row per bar", first.Length == 6);

                // The reload: same bars, same rows, from zero.
                logger.Reset();
                for (var bar = 0; bar < 5; bar++) logger.Append(at(bar), Tick);

                var second = File.ReadAllLines(path);
                Check("a replay leaves the same number of rows", second.Length == first.Length);
                Check("a replay leaves the same rows", string.Join("\n", second) == string.Join("\n", first));
                Check("the header is not repeated mid-file",
                    Array.IndexOf(second, SessionLogger.Header) == 0
                    && Array.LastIndexOf(second, SessionLogger.Header) == 0);

                // A live session carrying on past the replay still appends.
                logger.Append(at(5), Tick);
                Check("a new bar after a replay is appended", File.ReadAllLines(path).Length == 7);

                // Without the reset it would be a second pass over a day it already owns, which is
                // exactly what a live bar is - so ownership must survive an ordinary append.
                logger.Append(at(6), Tick);
                Check("ownership survives an ordinary append", File.ReadAllLines(path).Length == 8);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        static PanelModel PanelWith(Subscore[] subs, FactorConfig cfg, RegimeMode regime,
                                    bool scoreKnown, decimal score, BiasState state)
        {
            var r = new BiasRecord
            {
                Close = 20000m,
                Subs = subs,
                Regime = regime,
                Score = score,
                ScoreKnown = scoreKnown,
                State = state,
                Confidence = 60m,
                Flip = Level.None
            };

            return PanelModel.Build(r, cfg, false, 0m, GexSnapshot.Empty, false, false, -1, Tick);
        }

        static FactorConfig AllOn()
        {
            var cfg = new FactorConfig();
            for (var i = 0; i < cfg.Enabled.Length; i++) cfg.Enabled[i] = true;
            for (var i = 0; i < cfg.Weight.Length; i++) cfg.Weight[i] = 0.2m;
            return cfg;
        }

        static void ThePanelSaysAbstainedNotNeutral()
        {
            // The whole reason this panel is not just a copy of the reference: Bias Lite has
            // three directions because a person always has an opinion. This engine has four.
            var subs = new[]
            {
                Subscore.At(40m), Subscore.Missing("no yardstick yet"), Subscore.Missing("not built"),
                Subscore.At(0m), Subscore.At(-30m), Subscore.Missing("not built")
            };

            var p = PanelWith(subs, AllOn(), RegimeMode.None, true, 20m, BiasState.Long);

            Check("a factor that abstained is not a direction",
                p.Rows[(int)FactorId.Cvd].Kind == RowKind.Absent);

            Check("and it does not say Neutral",
                p.Rows[(int)FactorId.Cvd].Direction != "Neutral");

            Check("and it draws no bar at all",
                !p.Rows[(int)FactorId.Cvd].HasBar);

            Check("and it carries the reason it could not vote",
                p.Rows[(int)FactorId.Cvd].Absent == "no yardstick yet");

            // A real zero is a reading and has to look different from an absence.
            Check("a factor that read exactly zero IS neutral",
                p.Rows[(int)FactorId.Inventory].Kind == RowKind.Neutral);

            Check("and a real zero still draws its bar",
                p.Rows[(int)FactorId.Inventory].HasBar);

            Check("an absent factor carries no weight share",
                p.Rows[(int)FactorId.Cvd].Weight == "-");
        }

        static void ThePanelWeightsAreTheOnesActuallyVoting()
        {
            // Three of six can vote, all at the same setting, so each must read 33 -- not 20,
            // which is what printing the raw setting would give.
            var subs = new[]
            {
                Subscore.At(40m), Subscore.Missing("x"), Subscore.Missing("x"),
                Subscore.At(10m), Subscore.At(-30m), Subscore.Missing("x")
            };

            var p = PanelWith(subs, AllOn(), RegimeMode.None, true, 20m, BiasState.Long);

            Check("the weight column is the share of the vote, not the setting",
                p.Rows[(int)FactorId.Vwap].Weight == "33");

            var total = 0;
            for (var i = 0; i < p.Rows.Length; i++)
                if (p.Rows[i].Weight != "-") total += int.Parse(p.Rows[i].Weight);

            Check("the shares add up to the whole vote", total >= 99 && total <= 101);
        }

        static void TheRegimeShowsUpInTheWeightColumn()
        {
            var subs = new[]
            {
                Subscore.At(40m), Subscore.At(40m), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.Missing("x"), Subscore.Missing("x")
            };

            var cfg = AllOn();
            var flat = PanelWith(subs, cfg, RegimeMode.None, true, 40m, BiasState.Long);
            var longGamma = PanelWith(subs, cfg, RegimeMode.Positive, true, 40m, BiasState.Long);

            Check("with no regime two equal factors split the vote",
                flat.Rows[(int)FactorId.Cvd].Weight == "50");

            // Long gamma discounts thrust, so its share of the vote has to fall visibly.
            Check("long gamma cuts the delta factor's share",
                int.Parse(longGamma.Rows[(int)FactorId.Cvd].Weight) < 50);

            Check("and the VWAP factor's share rises to match",
                int.Parse(longGamma.Rows[(int)FactorId.Vwap].Weight) > 50);
        }

        static void ThePanelSaysNoVoteRatherThanZeroPercent()
        {
            var subs = new[]
            {
                Subscore.Missing("x"), Subscore.Missing("x"), Subscore.Missing("x"),
                Subscore.Missing("x"), Subscore.Missing("x"), Subscore.Missing("x")
            };

            var p = PanelWith(subs, AllOn(), RegimeMode.None, false, 0m, BiasState.Neutral);

            Check("a bar nothing could score does not print a bias of zero", p.Bias != "+0" && p.Bias != "0");
            Check("it says so instead", p.Bias == "no vote");
            Check("and the value is flagged unknown", !p.BiasKnown);
        }

        static void ASwitchedOffFactorSaysSoRatherThanNa()
        {
            var subs = new[]
            {
                Subscore.At(40m), Subscore.At(20m), Subscore.Missing("not built"),
                Subscore.At(10m), Subscore.At(-30m), Subscore.Missing("not built")
            };

            var cfg = AllOn();
            cfg.Enabled[(int)FactorId.Cvd] = false;

            var p = PanelWith(subs, cfg, RegimeMode.None, true, 20m, BiasState.Long);

            Check("a factor turned off is out of the vote",
                p.Rows[(int)FactorId.Cvd].Kind == RowKind.Absent);

            Check("and says it was switched off, not that it had nothing to read",
                p.Rows[(int)FactorId.Cvd].Absent == "switched off");
        }

        static void ThePanelNamesEveryFactor()
        {
            var p = PanelWith(new Subscore[6], AllOn(), RegimeMode.None, false, 0m, BiasState.Neutral);

            Check("there is a row per factor", p.Rows.Length == BiasEngine.FactorCount);

            for (var i = 0; i < p.Rows.Length; i++)
                Check("factor " + i + " has a name",
                    !string.IsNullOrEmpty(p.Rows[i].Name) && p.Rows[i].Name != "F" + i);
        }

        static void EachSessionGivesTheOvernightItsOwnVwap()
        {
            // The complaint that prompted it: all evening, VWAP and Session Delta read n/a,
            // because a cash-anchored VWAP is shut at 15:00 and stays shut until 08:30.
            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, 5m);
            AddOvernight(feed, new DateTime(2026, 3, 11), 20100m, 3m);

            var cashOnly = Walk(feed, SessionAnchor.RthOpen);
            var each = Walk(feed, SessionAnchor.EachSession);

            Check("anchored on the cash open the overnight still has no VWAP", !cashOnly.Vwap.Known);
            Check("anchored on each session the overnight has one", each.Vwap.Known);

            // It is the overnight's own VWAP, not yesterday's cash one left standing. The overnight
            // traded 20100 upward; the cash day before it traded 20000-20062.
            Check("and it is built from the overnight's bars alone",
                each.Vwap.Known && each.Vwap.Value >= 20100m - 2m);

            AddRth(feed, new DateTime(2026, 3, 11), 20500m, 5m, fromHour: 8);
            var eachNoon = Walk(feed, SessionAnchor.EachSession);
            var cashNoon = Walk(feed, SessionAnchor.RthOpen);

            // The cash session must read exactly as it always did -- the overnight VWAP does not
            // carry into 08:30.
            Check("the cash session restarts at its own open",
                eachNoon.BarsSinceAnchor == cashNoon.BarsSinceAnchor);
            Check("and its VWAP is identical to the cash-anchored one",
                eachNoon.Vwap.Known && cashNoon.Vwap.Known && eachNoon.Vwap.Value == cashNoon.Vwap.Value);
        }

        static void TradeGexIsConvertedTheWayTradeGexDrawsIt()
        {
            // QQQ strikes x TradeGEX's own ratio. 709 x 41.1 = 29,139.9.
            var snap = GexSnapshot.FromLevels(new DateTime(2026, 9, 10, 20, 0, 0), 29200m,
                                              709m, 715m, 705m, 41.1m, 0.10m);

            Check("a good reading has no problem", snap.Problem == null);
            Check("the flip is strike x ratio", snap.GammaFlip.Known && snap.GammaFlip.Value == 709m * 41.1m);
            Check("the call wall too", snap.CallWall.Known && snap.CallWall.Value == 715m * 41.1m);
            Check("above the flip is long gamma", snap.Regime == 1);
            Check("and it is usable", snap.Usable);

            var below = GexSnapshot.FromLevels(new DateTime(2026, 9, 10, 20, 0, 0), 29000m,
                                               709m, 715m, 705m, 41.1m, 0.10m);
            Check("below the flip is short gamma", below.Regime == -1);
        }

        static void TradeGexsRatioOfOneIsRefused()
        {
            // TradeGEX's own code sets the ratio to 1 when its feed omits one. In ETF mode that
            // draws a QQQ flip at 709 on a 29,000 chart -- every bar above it, long gamma forever.
            var snap = GexSnapshot.FromLevels(new DateTime(2026, 9, 10, 20, 0, 0), 29200m,
                                              709m, 715m, 705m, 1m, 0.10m);

            Check("a ratio of 1 on QQQ strikes is refused, not used", snap.Problem != null);
            Check("and there is no regime", snap.Regime == 0 && !snap.Usable);
            Check("and no flip level", !snap.GammaFlip.Known);

            var zero = GexSnapshot.FromLevels(DateTime.UtcNow, 29200m, 709m, 715m, 705m, 0m, 0.10m);
            Check("no ratio at all is a problem, not a guess", zero.Problem == "no ratio" && !zero.Usable);

            var noFlip = GexSnapshot.FromLevels(DateTime.UtcNow, 29200m, null, 715m, 705m, 41.1m, 0.10m);
            Check("no flip strike means no regime", noFlip.Problem == "no flip" && !noFlip.Usable);
        }

        static void AFarWallIsAbsentButTheRegimeStands()
        {
            // A wall outside the band is dropped; it does not take the flip down with it.
            var snap = GexSnapshot.FromLevels(DateTime.UtcNow, 29200m, 709m, 900m, 705m, 41.1m, 0.10m);

            Check("the far wall is absent", !snap.CallWall.Known);
            Check("the near wall stays", snap.PutWall.Known);
            Check("and the regime is still read", snap.Usable && snap.Regime == 1);

            // The absent wall must not read as a wall at zero -- that is "never near a wall".
            Check("an absent wall never puts price in a wall zone",
                !GexSnapshot.FromLevels(DateTime.UtcNow, 29200m, 709m, 900m, null, 41.1m, 0.10m)
                    .InWallZone(29200m, 15m));
        }

        static void ALiveTradeGexReadingIsNeverStale()
        {
            var snap = GexSnapshot.FromLevels(new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
                                              29200m, 709m, 715m, 705m, 41.1m, 0.10m);

            // Its age is the time since this indicator looked, not since TradeGEX computed it.
            Check("a streamed reading is not judged by file age",
                !snap.IsStale(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc), 20));
        }

        static void OnlyTheSameInstrumentsGammaIsRead()
        {
            Check("the same contract matches", TradeGexBridge.SameInstrument("MNQU6", "MNQU6"));
            Check("a decorated symbol matches", TradeGexBridge.SameInstrument("MNQU6.CME", "MNQU6"));
            Check("a different contract does not", !TradeGexBridge.SameInstrument("ESU6", "MNQU6"));
            Check("an unknown symbol is never a match", !TradeGexBridge.SameInstrument(null, "MNQU6"));
            Check("on either side", !TradeGexBridge.SameInstrument("MNQU6", ""));
        }

        static void WithoutTradeGexTheBridgeSaysSo()
        {
            // The harness has no TradeGEX loaded: that is the real "not installed" case.
            var r = new TradeGexBridge().Read("MNQU6");
            Check("no TradeGEX is not a reading", !r.Found);
            Check("and it says why", r.Problem == "TradeGEX not loaded");
        }


        #region What changes it

        static BiasEngine Replay(Feed feed, int n, Func<int, GexSnapshot> gexAt)
        {
            var time = Clock();
            var engine = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);
            for (var i = 0; i < n; i++)
            {
                var g = gexAt(i);
                engine.Advance(i, feed, time, g, g != null && g.Usable, 0);
            }
            return engine;
        }

        /// <summary>
        /// Commits bars 0..n-1 of the real feed, then a bar n of our choosing, through the real
        /// Advance -- exactly what would happen if price printed it -- and returns the state.
        /// </summary>
        static BiasState CommitAt(Feed feed, int n, decimal high, decimal low, decimal close,
                                  decimal delta, decimal volume, Func<int, decimal, GexSnapshot> gexFor)
        {
            var f = feed.Prefix(n);
            f.AddUtc(feed.Time(n), close, high, low, close, volume, delta);

            var engine = Replay(f, n, i => gexFor(i, f.Close(i)));
            var g = gexFor(n, close);
            engine.Advance(n, f, Clock(), g, g != null && g.Usable, 0);
            return engine.State;
        }

        static void TheEngineActsOnTheLevelItPrints()
        {
            // The claim the whole feature rests on: a level on the panel is the level this engine
            // will act on. So for bars across a real two-session fixture, every printed level is
            // committed as a real bar -- and must change the state to what the panel said -- and
            // the same bar one tick short must not.
            RunLevelCheck("without gamma", (bar, close) => GexSnapshot.Empty);

            // And again with a live gamma flip sitting inside the range, so the scan has to cross
            // it. The real pipeline rebuilds the snapshot at each bar's own close; so does this.
            RunLevelCheck("across a live gamma flip", (bar, close) =>
                GexSnapshot.FromLevels(new DateTime(2026, 3, 11, 15, 0, 0), close, 20060m, 20140m, 19990m, 1m, 0.10m));
        }

        static void RunLevelCheck(string label, Func<int, decimal, GexSnapshot> gexFor)
        {
            var feed = TwoDays();
            var time = Clock();

            int levels = 0, wrongState = 0, notNearest = 0, deltas = 0, wrongDelta = 0, sweeps = 0, wrongSweep = 0;

            for (var n = feed.Count / 2; n < feed.Count - 1; n += 3)
            {
                var engine = Replay(feed, n, i => gexFor(i, feed.Close(i)));
                if (!engine.Any || engine.DwellLeft != 0) continue;

                var last = engine.Last;
                var g = gexFor(n - 1, last.Close);
                var report = TriggerScan.Run(engine, n, feed, time, g, g != null && g.Usable, Tick);
                if (report.Problem != null || report.Here.Found) continue;

                var volume = feed.Volume(n - 1);

                foreach (var tr in new[] { report.Below, report.Above })
                {
                    if (!tr.Found) continue;
                    levels++;

                    var at = CommitAt(feed, n, tr.Price, tr.Price, tr.Price, 0m, volume, gexFor);
                    if (at != tr.To) wrongState++;

                    var dir = tr.Kind == TriggerKind.CloseBelow ? -1 : 1;
                    var shy = tr.Price - dir * Tick;
                    if (CommitAt(feed, n, shy, shy, shy, 0m, volume, gexFor) != report.State) notNearest++;
                }

                foreach (var tr in new[] { report.SellDelta, report.BuyDelta })
                {
                    if (!tr.Found) continue;
                    deltas++;

                    var vol = Math.Max(volume, Math.Abs(tr.Delta));
                    if (CommitAt(feed, n, last.Close, last.Close, last.Close, tr.Delta, vol, gexFor) != tr.To)
                        wrongDelta++;
                }

                foreach (var tr in new[] { report.SweepHigh, report.SweepLow })
                {
                    if (!tr.Found) continue;
                    sweeps++;

                    var side = tr.Kind == TriggerKind.SweepHigh ? 1 : -1;
                    var through = tr.Price + side * Tick;
                    var reclaim = tr.Price - side * Tick;
                    var st = CommitAt(feed, n, side > 0 ? through : reclaim, side > 0 ? reclaim : through,
                                      reclaim, 0m, volume, gexFor);
                    if (st != tr.To) wrongSweep++;
                }
            }

            Check(label + ": the scan found price levels to test (" + levels + ")", levels >= 4);
            Check(label + ": a bar closing at a printed level goes where the panel said", wrongState == 0);
            Check(label + ": one tick short of it, it does not", notNearest == 0);
            Check(label + ": the scan found delta triggers to test (" + deltas + ")", deltas >= 1);
            Check(label + ": one bar of the printed delta goes where the panel said", wrongDelta == 0);
            Check(label + ": a sweep does what the panel said it would", wrongSweep == 0);
        }

        static void AScanIsCheapEnoughForEveryBarClose()
        {
            // It runs once per closed bar on the calculation thread. A few hundred milliseconds
            // would stutter the chart at every bar close; this is the budget, with the worst case
            // of a full engine history behind it.
            var feed = TwoDays();
            var n = feed.Count - 2;
            var engine = Replay(feed, n, i => GexSnapshot.Empty);

            TriggerScan.Run(engine, n, feed, Clock(), GexSnapshot.Empty, false, Tick);   // warm the JIT
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int runs = 5;
            for (var i = 0; i < runs; i++)
                TriggerScan.Run(engine, n, feed, Clock(), GexSnapshot.Empty, false, Tick);
            var ms = sw.Elapsed.TotalMilliseconds / runs;

            Console.WriteLine("  trigger scan: " + ms.ToString("0.0") + " ms per bar close");
            Check("a full trigger scan stays under 150 ms (" + ms.ToString("0") + " ms)", ms < 150);
        }

        static void PeekAgreesWithAdvance()
        {
            // The trigger levels use Peek; the chart uses Advance. They are one rule or the
            // levels are fiction. Driven straight, through every state and across the dwell.
            var cfg = new StateConfig();
            var a = new StateMachine(cfg);
            var scores = new[] { 0m, 35m, 40m, 20m, 9m, 5m, -5m, -35m, -40m, -31m, -9m, 0m, 50m, 50m, 50m, 10m, 11m, -30m };
            var agree = true;

            foreach (var sc in scores)
            {
                var predicted = a.Peek(true, sc);
                var actual = a.Advance(true, sc);
                if (predicted != actual) agree = false;
            }

            Check("the next state Peek predicts is the one Advance takes", agree);

            var fresh = new StateMachine(cfg);
            fresh.Advance(true, 0m);
            fresh.Advance(true, 0m);
            fresh.Advance(true, 0m);
            fresh.Advance(true, 40m);   // enters long: BarsInState = 1
            Check("a state just entered reports the dwell still to serve", fresh.DwellLeft == 1);
            Check("and Peek respects it", fresh.Peek(true, 0m) == BiasState.Long);
            Check("while the dwell-free peek shows where it WILL go", fresh.PeekIgnoringDwell(true, 0m) == BiasState.Neutral);
        }

        static BiasRecord Rec(int h, int m, BiasState s, decimal close) =>
            new BiasRecord { Local = new DateTime(2026, 9, 10, h, m, 0), State = s, Close = close };

        static void TheTrackRecordGradesEachCallEntryToExit()
        {
            var recs = new List<BiasRecord>
            {
                Rec(9, 0, BiasState.Neutral, 100m),
                Rec(9, 5, BiasState.Long, 101m),     // long at 101
                Rec(9, 10, BiasState.Long, 105m),
                Rec(9, 15, BiasState.Neutral, 104m), // out at 104: +3
                Rec(9, 20, BiasState.Short, 103m),   // short at 103
                Rec(9, 25, BiasState.Short, 106m),
                Rec(9, 30, BiasState.Neutral, 107m), // out at 107: -4
                Rec(9, 35, BiasState.Long, 108m),    // still open
                Rec(9, 40, BiasState.Long, 110m)
            };

            var card = Scorecard.Grade(recs, t => t.Date);

            Check("two finished calls", card.Calls == 2);
            Check("one right", card.Right == 1);
            Check("net is +3 - 4 = -1", card.NetPoints == -1m);
            Check("best +3, worst -4", card.Best == 3m && card.Worst == -4m);
            Check("the open call is reported, not graded", card.OpenCall && card.OpenState == BiasState.Long);
            Check("and marked to the last close", card.OpenPoints == 2m);
            Check("a short is graded short: price up is a loss", card.Worst == -4m);

            var none = Scorecard.Grade(new List<BiasRecord>(), t => t.Date);
            Check("no records, no calls, no made-up extremes", none.Calls == 0 && none.Best == 0m && none.Worst == 0m);
        }

        private const string CalendarJson = @"[
 {""title"":""Core CPI m/m"",""country"":""USD"",""date"":""2026-09-11T08:30:00-04:00"",""impact"":""High"",""forecast"":""0.2%"",""previous"":""0.2%""},
 {""title"":""CPI y/y"",""country"":""USD"",""date"":""2026-09-11T08:30:00-04:00"",""impact"":""High"",""forecast"":""3.4%"",""previous"":""3.4%""},
 {""title"":""ECB Rate"",""country"":""EUR"",""date"":""2026-09-11T08:15:00-04:00"",""impact"":""High""},
 {""title"":""Jobless Claims"",""country"":""USD"",""date"":""2026-09-11T08:30:00-04:00"",""impact"":""Medium""},
 {""title"":""PPI m/m"",""country"":""USD"",""date"":""2026-09-10T08:30:00-04:00"",""impact"":""High""},
 {""title"":""Broken"",""country"":""USD"",""date"":""not a date"",""impact"":""High""}
]";

        static void TheCalendarKeepsOnlyWhatCanMoveNq()
        {
            var ev = EventCalendar.Parse(CalendarJson);

            Check("USD high-impact only, same minute folded: PPI and CPI", ev.Count == 2);
            Check("in time order", ev[0].Title == "PPI m/m" && ev[1].Title == "Core CPI m/m");
            Check("the CPI line counts the release printing with it", ev[1].AlsoAtSameTime == 1);
            Check("08:30 ET is 12:30 UTC", ev[1].WhenUtc == new DateTime(2026, 9, 11, 12, 30, 0));
            Check("an unparseable date is dropped, not guessed", !ev.Any(e => e.Title == "Broken"));

            var afterPpi = new DateTime(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc);
            Check("the next release after PPI is CPI", EventCalendar.Next(ev, afterPpi, TimeSpan.FromMinutes(15)).Title == "Core CPI m/m");

            var justAfterCpi = new DateTime(2026, 9, 11, 12, 40, 0, DateTimeKind.Utc);
            Check("ten minutes after CPI it is still the one shown", EventCalendar.Next(ev, justAfterCpi, TimeSpan.FromMinutes(15)) == ev[1]);

            var weekend = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
            Check("past the end of the feed there is no next event", EventCalendar.Next(ev, weekend, TimeSpan.FromMinutes(15)) == null);

            Check("a rate-limited HTML answer is an empty list, not an exception",
                EventCalendar.Parse("<html>429 Too Many Requests</html>").Count == 0);
            Check("so is a JSON object where the week's array should be", EventCalendar.Parse("{\"error\":1}").Count == 0);
        }

        static void ThePanelSaysWhatChangesItInWords()
        {
            var model = PanelWith(new Subscore[6], AllOn(), RegimeMode.None, true, 20m, BiasState.Neutral);

            var t = new TriggerReport
            {
                State = BiasState.Neutral, Close = 29110.75m, Range = 580m, DeltaVoting = false,
                Below = new Trigger { Found = true, Kind = TriggerKind.CloseBelow, Price = 29094.25m, To = BiasState.Short },
                Above = new Trigger { Found = false, Kind = TriggerKind.CloseAbove }
            };
            model.AddTriggers(t, Tick);

            Check("a level names the state it leads to", model.Changes.Any(l => l.Label == "SHORT" && l.Text.Contains("29094.25")));
            Check("and how far it is", model.Changes.Any(l => l.Text.Contains("(-16.5)")));
            Check("a side with nothing says so rather than printing nothing", model.Changes.Any(l => l.Text.StartsWith("nothing above")));
            Check("delta that is not voting says so", model.Changes.Any(l => l.Text == "not voting yet"));

            var here = PanelWith(new Subscore[6], AllOn(), RegimeMode.None, true, -35m, BiasState.Neutral);
            here.AddTriggers(new TriggerReport
            {
                State = BiasState.Neutral, Close = 100m, DwellLeft = 2, DeltaVoting = false,
                Here = new Trigger { Found = true, Kind = TriggerKind.Here, Price = 100m, To = BiasState.Short },
                Below = new Trigger { Found = true, Kind = TriggerKind.CloseBelow, Price = 99.75m, To = BiasState.Short }
            }, Tick);

            Check("when a close right here does it, that is what it says", here.Changes.Any(l => l.Text == "if this bar closes here"));
            Check("and no level a tick away is dressed up as the trigger", !here.Changes.Any(l => l.Text.Contains("99.75")));
            Check("a locked state says when it can move", here.ChangesHeader == "earliest in 2 bars");

            var ev = PanelWith(new Subscore[6], AllOn(), RegimeMode.None, true, 0m, BiasState.Neutral);
            ev.AddEvent(null, null, DateTime.UtcNow, Houston, true);
            Check("an empty rest-of-week says the feed is weekly, not that nothing is scheduled",
                ev.Context[0].Text == "nothing more in this week's feed");

            var soon = PanelWith(new Subscore[6], AllOn(), RegimeMode.None, true, 0m, BiasState.Neutral);
            soon.AddEvent(new MacroEvent { Title = "CPI m/m", WhenUtc = new DateTime(2026, 9, 11, 12, 30, 0) }, null,
                          new DateTime(2026, 9, 11, 12, 10, 0, DateTimeKind.Utc), Houston, true);
            Check("a release twenty minutes out is a warning", soon.Context[0].Warn);
            Check("printed in Houston time", soon.Context[0].Text.Contains("07:30 CT"));
        }

        #endregion

        static void TheBadgeSaysWaitingRatherThanZero()
        {
            var waiting = BadgeModel.Waiting("warming up");
            Check("with no record the badge says why", waiting[0].Text == "warming up");

            var unscored = new BiasRecord { State = BiasState.Neutral, ScoreKnown = false, Subs = new Subscore[6] };
            var cells = BadgeModel.Build(unscored, false, 0m, GexSnapshot.Empty, false, false, -1, Tick);

            Check("an unscored bar does not print a confident zero", cells[1].Text == "no vote");
            Check("and its confidence is blank", cells[2].Text == "-");
            Check("with no feed the regime cell says so", cells[3].Text == "no feed");
            Check("and the feed cell says so", cells[7].Text == "GEX -");

            var age = BadgeModel.Age(240);
            Check("the feed age reads in minutes once it is minutes old", age == "4m");
        }

        #endregion

        #region Machinery

        static StateMachine Machine() => new StateMachine(new StateConfig());

        static BiasState Push(StateMachine m, decimal score, int bars)
        {
            for (var i = 0; i < bars; i++) m.Advance(true, score);
            return m.State;
        }

        static decimal Blend(Subscore[] subs) => Blend(subs, RegimeMode.None, false);

        static decimal Blend(Subscore[] subs, RegimeMode regime) => Blend(subs, regime, false);

        /// <summary>The engine's own blend, driven directly with chosen subscores.</summary>
        static decimal Blend(Subscore[] subs, RegimeMode regime, bool wall)
        {
            decimal score;
            Scoring.Blend(subs, new FactorConfig(), regime, wall, out score);
            return score;
        }

        static bool HasScore(Subscore[] subs)
        {
            decimal score;
            return Scoring.Blend(subs, new FactorConfig(), RegimeMode.None, false, out score);
        }

        static BiasEngine Session(bool upward)
        {
            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, upward ? 1m : -1m, 5, 9m);
            AddDay(feed, new DateTime(2026, 3, 11), upward ? 20100m : 19900m, upward ? 1.5m : -1.5m, 5, 9m);

            var time = Clock();
            var engine = new BiasEngine(new SessionConfig(), new FactorConfig(), new StateConfig(), Tick);

            for (var i = 0; i < feed.Count; i++)
                engine.Advance(i, feed, time, GexSnapshot.Empty, false, -1);

            return engine;
        }

        /// <summary>
        /// Two whole trading days of FIVE-minute bars. The timeframe is not decoration: on
        /// thirty-minute bars a cash session is thirteen bars long, the twenty-bar CVD thrust can
        /// never look back far enough, and F2 sits out every test that uses the feed -- which is
        /// exactly how a mutation to the thrust path once survived this suite.
        /// </summary>
        static Feed TwoDays()
        {
            var feed = new Feed();
            AddDay(feed, new DateTime(2026, 3, 10), 20000m, 1m, 5, 9m);
            AddDay(feed, new DateTime(2026, 3, 11), 20080m, -1.2m, 5, 9m);
            return feed;
        }

        static SessionState Walk(Feed feed) => Walk(feed, SessionAnchor.RthOpen);

        static SessionState Walk(Feed feed, SessionAnchor anchor)
        {
            var time = Clock();
            var engine = new BiasEngine(new SessionConfig { Anchor = anchor }, new FactorConfig(),
                                        new StateConfig(), Tick);

            for (var i = 0; i < feed.Count; i++)
                engine.Advance(i, feed, time, GexSnapshot.Empty, false, -1);

            return engine.Session;
        }

        static TimeContext Clock() =>
            TimeContext.Create("Central Standard Time", BarClock.Utc, null, default, 16);

        static TimeSpan T(int h, int m) => new TimeSpan(h, m, 0);

        static DateTime Utc(DateTime local) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Houston);

        static DateTime Local(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Houston);

        /// <summary>
        /// Passes every read through and remembers the first one that reached past the bar being
        /// committed. Reading an unclosed bar is the failure this whole design is arranged
        /// around, so it is caught at the read rather than inferred from the answer.
        /// </summary>
        sealed class NoPeeking : IBarWindow
        {
            private readonly IBarWindow _inner;

            public NoPeeking(IBarWindow inner) { _inner = inner; }

            /// <summary>The newest bar that may legitimately be read.</summary>
            public int Limit = -1;

            /// <summary>The first bar read beyond the limit, or -1.</summary>
            public int Peeked = -1;

            /// <summary>Which limit was in force when that happened.</summary>
            public int PeekedAt = -1;

            private int Guard(int bar)
            {
                if (bar > Limit && Peeked < 0) { Peeked = bar; PeekedAt = Limit; }
                return bar;
            }

            public int Count => _inner.Count;
            public DateTime Time(int bar) => _inner.Time(Guard(bar));
            public decimal Open(int bar) => _inner.Open(Guard(bar));
            public decimal High(int bar) => _inner.High(Guard(bar));
            public decimal Low(int bar) => _inner.Low(Guard(bar));
            public decimal Close(int bar) => _inner.Close(Guard(bar));
            public decimal Volume(int bar) => _inner.Volume(Guard(bar));
            public decimal Delta(int bar) => _inner.Delta(Guard(bar));
        }

        /// <summary>Bars every thirty minutes, stamped UTC, on the Houston session grid.</summary>
        sealed class Feed : IBarWindow
        {
            private readonly List<DateTime> _time = new List<DateTime>();
            private readonly List<decimal[]> _bars = new List<decimal[]>();

            public void Add(DateTime local, decimal open, decimal high, decimal low,
                            decimal close, decimal volume, decimal delta)
            {
                _time.Add(Utc(local));
                _bars.Add(new[] { open, high, low, close, volume, delta });
            }

            /// <summary>
            /// Replaces a bar with something violently different, for the lookahead test. It has
            /// to break the extremes in BOTH directions: a wreck that only prints higher leaves
            /// the low-side divergence flag exactly as it was, and a peek at a later bar then
            /// changes nothing measurable. That version of this method missed a real lookahead.
            /// </summary>
            public void Wreck(int bar)
            {
                var b = _bars[bar];
                b[0] = 30000m;
                b[1] = 30000m;
                b[2] = 10000m;
                b[3] = 10000m;
                b[4] = 999999m;
                b[5] = -50000m;
            }

            /// <summary>Adds a bar at a UTC stamp, for copying one feed into another.</summary>
            public void AddUtc(DateTime utc, decimal open, decimal high, decimal low,
                               decimal close, decimal volume, decimal delta)
            {
                _time.Add(utc);
                _bars.Add(new[] { open, high, low, close, volume, delta });
            }

            /// <summary>The first <paramref name="n"/> bars, as a new feed.</summary>
            public Feed Prefix(int n)
            {
                var f = new Feed();
                for (var i = 0; i < n; i++)
                    f.AddUtc(_time[i], Open(i), High(i), Low(i), Close(i), Volume(i), Delta(i));
                return f;
            }

            public int Count => _bars.Count;
            public DateTime Time(int bar) => _time[bar];
            public decimal Open(int bar) => _bars[bar][0];
            public decimal High(int bar) => _bars[bar][1];
            public decimal Low(int bar) => _bars[bar][2];
            public decimal Close(int bar) => _bars[bar][3];
            public decimal Volume(int bar) => _bars[bar][4];
            public decimal Delta(int bar) => _bars[bar][5];
        }

        static void AddDay(Feed feed, DateTime date, decimal start, decimal step) =>
            AddDay(feed, date, start, step, 30, 0m);

        static void AddDay(Feed feed, DateTime date, decimal start, decimal step,
                           int minutes, decimal swing)
        {
            AddOvernight(feed, date, start, step / 3m, minutes, swing);
            AddRth(feed, date, start, step, 8, minutes, swing);
        }

        static void AddOvernight(Feed feed, DateTime date, decimal start, decimal step) =>
            AddOvernight(feed, date, start, step, 30, 0m);

        /// <summary>The evening reopen through to the cash open: 17:00 the day before to 08:30.</summary>
        static void AddOvernight(Feed feed, DateTime date, decimal start, decimal step,
                                 int minutes, decimal swing)
        {
            var t = date.AddDays(-1).Add(new TimeSpan(17, 0, 0));
            var bars = (int)((date.Add(new TimeSpan(8, 30, 0)) - t).TotalMinutes / minutes);
            var previous = start;

            for (var i = 0; i < bars; i++)
            {
                var price = start + i * step + Wave(i, swing);
                feed.Add(t, previous, Math.Max(previous, price) + 2m, Math.Min(previous, price) - 2m,
                         price, 500m, Flow(price - previous, 3m));
                previous = price;
                t = t.AddMinutes(minutes);
            }
        }

        static void AddRth(Feed feed, DateTime date, decimal start, decimal step, int fromHour) =>
            AddRth(feed, date, start, step, fromHour, 30, 0m);

        /// <summary>The cash session, 08:30 to the close, or from a later hour for a part session.</summary>
        static void AddRth(Feed feed, DateTime date, decimal start, decimal step,
                           int fromHour, int minutes, decimal swing)
        {
            var t = date.Add(new TimeSpan(fromHour, 30, 0));
            var bars = (int)((new TimeSpan(15, 0, 0) - t.TimeOfDay).TotalMinutes / minutes);
            var previous = start;

            for (var i = 0; i < bars; i++)
            {
                var price = start + i * step + Wave(i, swing);
                feed.Add(t, previous, Math.Max(previous, price) + 2m, Math.Min(previous, price) - 2m,
                         price, 1000m, Flow(price - previous, 10m));
                previous = price;
                t = t.AddMinutes(minutes);
            }
        }

        /// <summary>
        /// A deterministic swing on top of the drift.
        ///
        /// Without it the generated price is monotonic, every single bar is a fresh twenty-bar
        /// extreme, and a factor that peeked one bar into the future would find the same answer
        /// it found looking backwards -- so the no-lookahead test passed against code that did
        /// exactly that. A path that turns is the whole reason that test can fail.
        /// </summary>
        static decimal Wave(int i, decimal amplitude) =>
            amplitude == 0m ? 0m : amplitude * (decimal)Math.Sin(i * 0.7d);

        /// <summary>Delta that follows the bar's own move, so cumulative delta is not a ramp either.</summary>
        static decimal Flow(decimal move, decimal drift) =>
            drift + Math.Round(move * 4m, 2, MidpointRounding.AwayFromZero);

        static void Check(string what, bool ok)
        {
            if (ok) return;

            _failures++;
            Console.WriteLine("FAILED: " + what);
        }

        #endregion
    }
}
