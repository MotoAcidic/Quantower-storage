using System;
using System.Collections.Generic;
using OceansDom;

namespace OceansDom.Tests
{
    /// <summary>
    /// Exercises the parts of Ocean DOM that can be wrong quietly: which ticks share a ladder row,
    /// how an ageing bar's trade is weighted, and -- the one that actually matters -- whether a
    /// break and a rejection are told apart, since they are the same distance from the level and
    /// mean the opposite thing.
    ///
    /// The references here are deliberately naive: a linear scan over the rows, a hand-written
    /// decay, a state machine written out longhand. A mistake in the real path shows up as a
    /// disagreement rather than being confirmed by its own logic.
    /// </summary>
    static class Program
    {
        private const decimal Tick = 0.25m;   // MNQ
        private static int _failures;

        static int Main()
        {
            TicksPerRowFollowsTheZoom();
            RowsAreAnchoredSoScrollingDoesNotReshuffleThem();
            EveryPriceLandsInTheRowThatContainsIt();
            BuildRefusesNonsenseGeometry();

            NewestBarCountsFull();
            WeightIsGoneAtTheWindowEdge();
            WeightNeverRisesWithAge();
            HalfLifeHalvesAtHalfTheWindow();
            BarsOutsideTheWindowAddNothing();
            TapeKeepsTheSidesApart();

            AggregationMatchesABruteForceScan();
            SidesNeverBleedIntoEachOther();
            PricesOutsideTheLadderAreDropped();
            RestAndTapeLandOnTheSameRow();

            FarAwayIsQuiet();
            InsideTheApproachBandIsApproach();
            InsideTheBandIsTest();
            BreakAndRejectAreTheSameDistanceAndOppositeMeaning();
            ChoppingInTheBandDoesNotForgetWhichSideItCameFrom();
            ApproachingFromAboveMirrorsApproachingFromBelow();
            LeavingTheBandTooLittleIsNotAnEvent();
            AnEventLatchesThenExpires();
            ReenteringTheBandClearsTheLatch();
            AZeroTickSizeCannotProduceAState();

            OnlyABreakIsEverLoud();
            NeverMoreThanOneLoudThing();
            ThePrimaryIsTheHighestRankedFirstOnATie();

            LevelsAreReadInEitherNotation();
            ATypoIsReportedRatherThanDropped();

            NormaliseClampsAndNeverInventsAScale();
            CompactKeepsSmallSizesExact();

            if (_failures == 0)
            {
                Console.WriteLine("All Ocean DOM tests passed.");
                return 0;
            }

            Console.WriteLine(_failures + " test(s) FAILED.");
            return 1;
        }

        #region Ladder geometry

        private static void TicksPerRowFollowsTheZoom()
        {
            // Zoomed in: a tick is already taller than the minimum, so one tick is one row.
            Check("zoomed in keeps one tick per row", LadderGrid.TicksPerRow(10m, 3) == 1);
            Check("exactly at the minimum keeps one tick per row", LadderGrid.TicksPerRow(3m, 3) == 1);

            // Zoomed out: 0.4px per tick needs 8 ticks to clear 3px.
            Check("zoomed out merges ticks", LadderGrid.TicksPerRow(0.4m, 3) == 8);
            Check("far out merges more", LadderGrid.TicksPerRow(0.05m, 3) == 60);
            Check("a taller row merges more at the same zoom", LadderGrid.TicksPerRow(1m, 6) == 6);

            // Nonsense from an unattached chart must not divide by zero or return 0.
            Check("no pixel height falls back to one tick", LadderGrid.TicksPerRow(0m, 3) == 1);
            Check("negative pixel height falls back to one tick", LadderGrid.TicksPerRow(-2m, 3) == 1);
            Check("a zero minimum still gives a real row", LadderGrid.TicksPerRow(10m, 0) == 1);
        }

        private static void RowsAreAnchoredSoScrollingDoesNotReshuffleThem()
        {
            // Two windows over the same market, scrolled by an amount that is not a whole row.
            var a = LadderGrid.Build(29200m, 29260m, Tick, 8, 4000);
            var b = LadderGrid.Build(29203.5m, 29263.5m, Tick, 8, 4000);

            Check("both windows build", a != null && b != null);
            if (a == null || b == null) return;

            Check("the row size is the same", a.RowSize == b.RowSize);

            // The same price has to sit on the same row boundary in both, or the ladder shimmers.
            var probe = 29234.75m;
            Check("a price keeps its row boundary across a scroll",
                a.Low(a.IndexOf(probe)) == b.Low(b.IndexOf(probe)));

            Check("the anchor is a whole number of rows",
                a.Anchor % a.RowSize == 0m);
        }

        private static void EveryPriceLandsInTheRowThatContainsIt()
        {
            var grid = LadderGrid.Build(29600m, 29610m, Tick, 4, 4000);
            Check("grid builds", grid != null);
            if (grid == null) return;

            var wrong = 0;
            for (var price = 29600m; price <= 29610m; price += Tick)
            {
                var index = grid.IndexOf(price);
                if (index < 0) { wrong++; continue; }

                // Rows are half open: [Low, High).
                if (price < grid.Low(index) || price >= grid.High(index)) wrong++;
            }

            Check("every price sits inside its own row", wrong == 0);

            Check("a price below the ladder is not in it", grid.IndexOf(29500m) == -1);
            Check("a price above the ladder is not in it", grid.IndexOf(29700m) == -1);
        }

        private static void BuildRefusesNonsenseGeometry()
        {
            Check("no tick size means no grid", LadderGrid.Build(1m, 2m, 0m, 1, 4000) == null);
            Check("a negative tick size means no grid", LadderGrid.Build(1m, 2m, -0.25m, 1, 4000) == null);
            Check("a runaway row count is refused", LadderGrid.Build(0m, 100000m, Tick, 1, 4000) == null);

            var flipped = LadderGrid.Build(29610m, 29600m, Tick, 4, 4000);
            Check("an inverted range is still built", flipped != null && flipped.Count > 0);
        }

        #endregion

        #region Tape decay

        private static void NewestBarCountsFull()
        {
            Check("flat gives the newest bar full weight", TapeBook.Weight(0, 10, TapeDecay.Flat) == 1m);
            Check("linear gives the newest bar full weight", TapeBook.Weight(0, 10, TapeDecay.Linear) == 1m);
            Check("half life gives the newest bar full weight", TapeBook.Weight(0, 10, TapeDecay.HalfLife) == 1m);
        }

        private static void WeightIsGoneAtTheWindowEdge()
        {
            Check("flat stops at the edge", TapeBook.Weight(10, 10, TapeDecay.Flat) == 0m);
            Check("linear stops at the edge", TapeBook.Weight(10, 10, TapeDecay.Linear) == 0m);
            Check("half life stops at the edge", TapeBook.Weight(10, 10, TapeDecay.HalfLife) == 0m);
            Check("past the edge is still nothing", TapeBook.Weight(99, 10, TapeDecay.Linear) == 0m);

            Check("a negative age weighs nothing", TapeBook.Weight(-1, 10, TapeDecay.Linear) == 0m);
            Check("an empty window weighs nothing", TapeBook.Weight(0, 0, TapeDecay.Linear) == 0m);
        }

        private static void WeightNeverRisesWithAge()
        {
            foreach (TapeDecay decay in Enum.GetValues(typeof(TapeDecay)))
            {
                var previous = decimal.MaxValue;
                var rose = false;
                for (var age = 0; age <= 12; age++)
                {
                    var weight = TapeBook.Weight(age, 12, decay);
                    if (weight > previous) rose = true;
                    previous = weight;
                }

                Check(decay + " never weighs an older bar more", !rose);
            }
        }

        private static void HalfLifeHalvesAtHalfTheWindow()
        {
            var half = TapeBook.Weight(5, 10, TapeDecay.HalfLife);
            Check("half a window in is half the weight", Math.Abs(half - 0.5m) < 0.0001m);

            var quarter = TapeBook.Weight(8, 16, TapeDecay.HalfLife);
            Check("half a longer window is also half the weight", Math.Abs(quarter - 0.5m) < 0.0001m);
        }

        private static void BarsOutsideTheWindowAddNothing()
        {
            var tape = new TapeBook();
            var prints = new List<TapePrint> { new TapePrint(29600m, 10m, 20m) };

            tape.AddBar(prints, 10, 10, TapeDecay.Flat);
            Check("a bar at the window edge adds nothing", tape.PriceCount == 0);

            tape.AddBar(prints, 0, 10, TapeDecay.Flat);
            Check("a bar inside the window is added", tape.PriceCount == 1);
        }

        private static void TapeKeepsTheSidesApart()
        {
            var tape = new TapeBook();
            tape.Add(29600m, 10m, 0m, 1m);
            tape.Add(29600m, 0m, 4m, 1m);

            decimal bid, ask;
            Check("the price is there", tape.TryGet(29600m, out bid, out ask));
            Check("bid volume accumulates on the bid side", bid == 10m);
            Check("ask volume accumulates on the ask side", ask == 4m);

            Check("a price never traded is absent", !tape.TryGet(29601m, out bid, out ask));
            Check("an absent price reports nothing rather than something", bid == 0m && ask == 0m);

            var empty = new TapeBook();
            empty.Add(29600m, 0m, 0m, 1m);
            Check("a print with no volume is not a price", empty.PriceCount == 0);

            var unweighted = new TapeBook();
            unweighted.Add(29600m, 5m, 5m, 0m);
            Check("a print with no weight is not a price", unweighted.PriceCount == 0);
        }

        #endregion

        #region Aggregation

        private static void AggregationMatchesABruteForceScan()
        {
            var grid = LadderGrid.Build(29600m, 29620m, Tick, 4, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            var book = new List<BookLevel>();
            var seed = 7;
            for (var price = 29600m; price <= 29620m; price += Tick)
            {
                seed = (seed * 31 + 17) % 97;
                book.Add(new BookLevel(price, seed + 1, price > 29610m));
            }

            var rows = DomMath.Aggregate(grid, book, null);

            // Naive reference: for each row, walk the whole book and add what falls inside it.
            var wrong = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                decimal bid = 0m, ask = 0m;
                for (var j = 0; j < book.Count; j++)
                {
                    var level = book[j];
                    if (level.Price < grid.Low(i) || level.Price >= grid.High(i)) continue;
                    if (level.IsAsk) ask += level.Size; else bid += level.Size;
                }

                if (rows[i].BidRest != bid || rows[i].AskRest != ask) wrong++;
            }

            Check("aggregation agrees with a linear scan", wrong == 0);
        }

        private static void SidesNeverBleedIntoEachOther()
        {
            var grid = LadderGrid.Build(29600m, 29604m, Tick, 8, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            // Both sides deliberately on the same price, which a merged row makes possible.
            var book = new List<BookLevel>
            {
                new BookLevel(29601m, 40m, false),
                new BookLevel(29601m, 9m, true)
            };

            var rows = DomMath.Aggregate(grid, book, null);
            var index = grid.IndexOf(29601m);

            Check("the bid keeps its own size", rows[index].BidRest == 40m);
            Check("the ask keeps its own size", rows[index].AskRest == 9m);
        }

        private static void PricesOutsideTheLadderAreDropped()
        {
            var grid = LadderGrid.Build(29600m, 29604m, Tick, 4, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            var book = new List<BookLevel>
            {
                new BookLevel(29500m, 999m, false),
                new BookLevel(29700m, 999m, true)
            };

            var rows = DomMath.Aggregate(grid, book, null);

            var total = 0m;
            for (var i = 0; i < rows.Length; i++) total += rows[i].BidRest + rows[i].AskRest;

            Check("size outside the ladder is not folded back into it", total == 0m);
            Check("an empty ladder has no scale", DomMath.MaxRest(rows) == 0m);
        }

        private static void RestAndTapeLandOnTheSameRow()
        {
            var grid = LadderGrid.Build(29600m, 29610m, Tick, 4, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            var book = new List<BookLevel> { new BookLevel(29605m, 250m, false) };

            var tape = new TapeBook();
            tape.Add(29605m, 80m, 12m, 1m);

            var rows = DomMath.Aggregate(grid, book, tape);
            var index = grid.IndexOf(29605m);

            // The whole point of the ladder: both facts about a price arrive on one row.
            Check("resting size is on the row", rows[index].BidRest == 250m);
            Check("traded size is on the same row", rows[index].TapeBid == 80m && rows[index].TapeAsk == 12m);
            Check("the row totals its trade", rows[index].TapeTotal == 92m);
            Check("the tape scale sees it", DomMath.MaxTape(rows) == 92m);
        }

        #endregion

        #region Level state machine

        private static LevelTuning Tuning()
        {
            return LevelTuning.Default(Tick);
        }

        private static void FarAwayIsQuiet()
        {
            var watch = new LevelWatch("PDH", 29600m);
            Check("far below is quiet", watch.Update(29580m, 0, Tuning()) == LevelState.Quiet);
            Check("far above is quiet", watch.Update(29620m, 0, Tuning()) == LevelState.Quiet);
        }

        private static void InsideTheApproachBandIsApproach()
        {
            var watch = new LevelWatch("PDH", 29600m);

            // 20 approach ticks at 0.25 = 5.00 points.
            Check("just outside the approach is quiet", watch.Update(29594.5m, 0, Tuning()) == LevelState.Quiet);
            Check("just inside the approach is approach", watch.Update(29595.25m, 0, Tuning()) == LevelState.Approach);
        }

        private static void InsideTheBandIsTest()
        {
            var watch = new LevelWatch("PDH", 29600m);
            watch.Update(29597m, 0, Tuning());

            // 4 band ticks at 0.25 = 1.00 point.
            Check("just outside the band is not a test", watch.Update(29598.75m, 0, Tuning()) == LevelState.Approach);
            Check("inside the band is a test", watch.Update(29599.25m, 0, Tuning()) == LevelState.Test);
            Check("right on the level is a test", watch.Update(29600m, 0, Tuning()) == LevelState.Test);
        }

        private static void BreakAndRejectAreTheSameDistanceAndOppositeMeaning()
        {
            // Band 4 + break 8 = 12 ticks = 3.00 points clear of the level either way.
            var broke = new LevelWatch("PDH", 29600m);
            broke.Update(29596m, 0, Tuning());        // approaching from below
            broke.Update(29599.75m, 0, Tuning());     // into the band
            var brokeState = broke.Update(29603m, 0, Tuning());   // out the far side

            var rejected = new LevelWatch("PDH", 29600m);
            rejected.Update(29596m, 0, Tuning());     // approaching from below
            rejected.Update(29599.75m, 0, Tuning());  // into the band
            var rejectedState = rejected.Update(29597m, 0, Tuning()); // back the way it came

            Check("through the level is a break", brokeState == LevelState.Break);
            Check("back the way it came is a rejection", rejectedState == LevelState.Reject);

            // The distance is identical -- only the side it left on separates them.
            Check("both left the band by the same distance",
                Math.Abs(29603m - 29600m) == Math.Abs(29597m - 29600m));
        }

        private static void ApproachingFromAboveMirrorsApproachingFromBelow()
        {
            var broke = new LevelWatch("PDL", 29600m);
            broke.Update(29604m, 0, Tuning());        // approaching from above
            broke.Update(29600.25m, 0, Tuning());     // into the band
            Check("down through the level is a break",
                broke.Update(29597m, 0, Tuning()) == LevelState.Break);

            var rejected = new LevelWatch("PDL", 29600m);
            rejected.Update(29604m, 0, Tuning());
            rejected.Update(29600.25m, 0, Tuning());
            Check("back up the way it came is a rejection",
                rejected.Update(29603m, 0, Tuning()) == LevelState.Reject);
        }

        /// <summary>
        /// Price rarely walks into a level and straight out again -- it chops across it first.
        /// The side it originally came from has to survive that, or a break that happens to have
        /// wobbled above the level on its way through gets called a rejection.
        /// </summary>
        private static void ChoppingInTheBandDoesNotForgetWhichSideItCameFrom()
        {
            var broke = new LevelWatch("PDH", 29600m);
            broke.Update(29596m, 0, Tuning());        // approaching from below
            broke.Update(29599.75m, 0, Tuning());     // into the band, still under the level
            broke.Update(29600.25m, 0, Tuning());     // chops over the level, still in the band
            broke.Update(29599.75m, 0, Tuning());     // and back under it
            broke.Update(29600.5m, 0, Tuning());      // and over again
            Check("chopping then going through is still a break",
                broke.Update(29603m, 0, Tuning()) == LevelState.Break);

            var rejected = new LevelWatch("PDH", 29600m);
            rejected.Update(29596m, 0, Tuning());
            rejected.Update(29599.75m, 0, Tuning());
            rejected.Update(29600.25m, 0, Tuning());  // chops over the level
            Check("chopping then falling away is still a rejection",
                rejected.Update(29597m, 0, Tuning()) == LevelState.Reject);

            // The mirror: came from above, chopped under, then carried on down.
            var down = new LevelWatch("PDL", 29600m);
            down.Update(29604m, 0, Tuning());
            down.Update(29600.25m, 0, Tuning());
            down.Update(29599.75m, 0, Tuning());
            Check("chopping then going through downward is still a break",
                down.Update(29597m, 0, Tuning()) == LevelState.Break);
        }

        private static void LeavingTheBandTooLittleIsNotAnEvent()
        {
            var watch = new LevelWatch("PDH", 29600m);
            watch.Update(29596m, 0, Tuning());
            watch.Update(29599.75m, 0, Tuning());

            // Out of the band but only 8 ticks clear, short of the 12 an event needs.
            var state = watch.Update(29602m, 0, Tuning());
            Check("drifting out of the band is not a break", state != LevelState.Break);
            Check("drifting out of the band is not a rejection", state != LevelState.Reject);
            Check("it is just an approach again", state == LevelState.Approach);
        }

        private static void AnEventLatchesThenExpires()
        {
            var tuning = Tuning();
            var watch = new LevelWatch("PDH", 29600m);

            watch.Update(29596m, 0, tuning);
            watch.Update(29599.75m, 0, tuning);
            Check("the break fires", watch.Update(29603m, 1000, tuning) == LevelState.Break);

            // Price walks away entirely; the break stays on screen for its latch.
            Check("the break is held while the latch runs",
                watch.Update(29640m, 5000, tuning) == LevelState.Break);
            Check("the break is still held just before it expires",
                watch.Update(29640m, 20999, tuning) == LevelState.Break);
            Check("the break is gone once the latch expires",
                watch.Update(29640m, 21001, tuning) == LevelState.Quiet);
        }

        private static void ReenteringTheBandClearsTheLatch()
        {
            var tuning = Tuning();
            var watch = new LevelWatch("PDH", 29600m);

            watch.Update(29596m, 0, tuning);
            watch.Update(29599.75m, 0, tuning);
            watch.Update(29603m, 1000, tuning);

            Check("coming back to the level replaces the held break with a live test",
                watch.Update(29600m, 2000, tuning) == LevelState.Test);
        }

        private static void AZeroTickSizeCannotProduceAState()
        {
            var tuning = Tuning();
            tuning.TickSize = 0m;

            var watch = new LevelWatch("PDH", 29600m);
            Check("no tick size means no state", watch.Update(29600m, 0, tuning) == LevelState.Quiet);
        }

        #endregion

        #region Salience budget

        private static void OnlyABreakIsEverLoud()
        {
            Check("a break is loud", Salience.For(LevelState.Break) == Loudness.Loud);
            Check("a rejection is not loud", Salience.For(LevelState.Reject) == Loudness.Soft);
            Check("a test is not loud", Salience.For(LevelState.Test) == Loudness.Soft);
            Check("an approach is silent", Salience.For(LevelState.Approach) == Loudness.Silent);
            Check("quiet is silent", Salience.For(LevelState.Quiet) == Loudness.Silent);
        }

        private static void NeverMoreThanOneLoudThing()
        {
            var none = new List<LevelState> { LevelState.Quiet, LevelState.Approach, LevelState.Test };
            Check("nothing is loud when nothing broke", Salience.LoudIndex(none) == -1);

            var two = new List<LevelState> { LevelState.Break, LevelState.Test, LevelState.Break };
            var loud = Salience.LoudIndex(two);
            Check("two breaks still yield one loud row", loud == 0);

            Check("an empty list has nothing loud", Salience.LoudIndex(new List<LevelState>()) == -1);
            Check("a missing list has nothing loud", Salience.LoudIndex(null) == -1);
        }

        private static void ThePrimaryIsTheHighestRankedFirstOnATie()
        {
            var states = new List<LevelState> { LevelState.Approach, LevelState.Break, LevelState.Test };
            Check("the break speaks for the chip", Salience.PrimaryIndex(states) == 1);

            var tie = new List<LevelState> { LevelState.Test, LevelState.Test };
            Check("a tie takes the first", Salience.PrimaryIndex(tie) == 0);

            var quiet = new List<LevelState> { LevelState.Quiet, LevelState.Quiet };
            Check("an all quiet screen has no primary", Salience.PrimaryIndex(quiet) == -1);

            Check("a break outranks a rejection",
                Salience.Rank(LevelState.Break) > Salience.Rank(LevelState.Reject));
            Check("a rejection outranks a test",
                Salience.Rank(LevelState.Reject) > Salience.Rank(LevelState.Test));
            Check("a test outranks an approach",
                Salience.Rank(LevelState.Test) > Salience.Rank(LevelState.Approach));
        }

        #endregion

        #region Levels setting

        private static void LevelsAreReadInEitherNotation()
        {
            var parsed = LevelParser.Parse("PDH = 29650.25, PDL 29500; ORB-H=29612.5");

            Check("all three levels are read", parsed.Levels.Count == 3);
            Check("nothing was unreadable", parsed.Unreadable.Count == 0);
            if (parsed.Levels.Count != 3) return;

            Check("equals notation keeps its name", parsed.Levels[0].Name == "PDH");
            Check("equals notation keeps its price", parsed.Levels[0].Price == 29650.25m);
            Check("space notation keeps its name", parsed.Levels[1].Name == "PDL");
            Check("space notation keeps its price", parsed.Levels[1].Price == 29500m);
            Check("a hyphenated name survives", parsed.Levels[2].Name == "ORB-H");

            var bare = LevelParser.Parse("29655.75");
            Check("a bare price is a level", bare.Levels.Count == 1);
            Check("a bare price names itself", bare.Levels[0].Name == "29655.75");

            var newlines = LevelParser.Parse("PDH 1\nPDL 2\r\nVWAP 3");
            Check("newlines separate levels too", newlines.Levels.Count == 3);

            var nothing = LevelParser.Parse("   ");
            Check("blank input is no levels and no complaint",
                nothing.Levels.Count == 0 && nothing.Unreadable.Count == 0);

            var missing = LevelParser.Parse(null);
            Check("missing input does not throw", missing.Levels.Count == 0);
        }

        private static void ATypoIsReportedRatherThanDropped()
        {
            var parsed = LevelParser.Parse("PDH 29650, PDL twentynine, ORB 29612.5");

            Check("the readable levels are still read", parsed.Levels.Count == 2);
            Check("the typo is reported", parsed.Unreadable.Count == 1);
            Check("the reported text is the entry as typed", parsed.Unreadable[0] == "PDL twentynine");

            var negative = LevelParser.Parse("PDH -100");
            Check("a negative price is not a level", negative.Levels.Count == 0);
            Check("a negative price is reported", negative.Unreadable.Count == 1);

            var zero = LevelParser.Parse("PDH 0");
            Check("a zero price is not a level", zero.Levels.Count == 0);
            Check("a zero price is reported", zero.Unreadable.Count == 1);
        }

        #endregion

        #region Scaling and formatting

        private static void NormaliseClampsAndNeverInventsAScale()
        {
            Check("no reference means nothing to draw", DomMath.Normalise(50m, 0m) == 0m);
            Check("a negative reference means nothing to draw", DomMath.Normalise(50m, -1m) == 0m);
            Check("nothing there draws nothing", DomMath.Normalise(0m, 100m) == 0m);
            Check("half the reference is half the bar", DomMath.Normalise(50m, 100m) == 0.5m);
            Check("the reference itself is a full bar", DomMath.Normalise(100m, 100m) == 1m);
            Check("more than the reference is clamped", DomMath.Normalise(500m, 100m) == 1m);
        }

        private static void CompactKeepsSmallSizesExact()
        {
            Check("nothing prints as nothing", DomMath.Compact(0m) == "");
            Check("a single lot is exact", DomMath.Compact(1m) == "1");
            Check("a book-sized number is exact", DomMath.Compact(487m) == "487");
            Check("just under a thousand is exact", DomMath.Compact(999m) == "999");
            Check("a thousand abbreviates", DomMath.Compact(1000m) == "1.0k");
            Check("thousands keep one decimal while small", DomMath.Compact(4500m) == "4.5k");
            Check("big thousands drop the decimal", DomMath.Compact(45000m) == "45k");
            Check("millions abbreviate", DomMath.Compact(2500000m) == "2.5M");
        }

        #endregion

        private static void Check(string what, bool ok)
        {
            if (ok) return;

            _failures++;
            Console.WriteLine("FAIL: " + what);
        }
    }
}
