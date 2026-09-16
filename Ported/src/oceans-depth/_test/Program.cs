using System;
using System.Collections.Generic;
using OceansDepth;

namespace OceansDepth.Tests
{
    /// <summary>
    /// Exercises the parts of Ocean Depth that can be wrong quietly: which ticks share a screen
    /// row, how a pulled level fades, and how a size becomes a colour and a number.
    ///
    /// The references here are deliberately naive -- a linear scan over the rows, a hand-written
    /// decay -- so a mistake in the fast path shows up as a disagreement rather than being
    /// confirmed by its own logic.
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
            AggregationMatchesABruteForceScan();
            SidesNeverBleedIntoEachOther();
            LargestOrderTakesTheMaxNotTheSum();
            LevelsOutsideTheGridAreDropped();

            PeakIsTakenImmediately();
            PulledSizeHoldsThenFadesToWhatIsThere();
            FadeIsNeverBelowWhatIsActuallyResting();
            AReturningBiggerOrderResetsThePeak();
            FullyFadedEmptyLevelsAreForgotten();
            ZeroHoldAndFadeMeansTheRawBook();
            OrderCountsAreNotInventedForAGhost();
            PruneDropsPricesOutOfRange();


            NormaliseClampsAndNeverInventsAScale();
            ShapeIsMonotonicAndKeepsItsEnds();
            GradientEndsExactlyOnTheChosenColours();
            CompactKeepsSmallSizesExact();
            TopIndicesRanksBiggestFirst();

            if (_failures == 0)
            {
                Console.WriteLine("All Ocean Depth tests passed.");
                return 0;
            }

            Console.WriteLine(_failures + " test(s) FAILED.");
            return 1;
        }

        #region Row grid

        private static void TicksPerRowFollowsTheZoom()
        {
            // Zoomed in: a tick is already taller than the minimum, so one tick is one row.
            Check("zoomed in keeps one tick per row", RowGrid.TicksPerRow(10m, 3) == 1);
            Check("exactly at the minimum keeps one tick per row", RowGrid.TicksPerRow(3m, 3) == 1);

            // Zoomed out: 0.4px per tick needs 8 ticks to clear 3px.
            Check("zoomed out merges ticks", RowGrid.TicksPerRow(0.4m, 3) == 8);
            Check("far out merges more", RowGrid.TicksPerRow(0.05m, 3) == 60);

            // A taller minimum merges more at the same zoom.
            Check("a taller row merges more", RowGrid.TicksPerRow(1m, 6) == 6);

            // Nonsense from an unattached chart must not divide by zero or return 0.
            Check("no pixel height falls back to one tick", RowGrid.TicksPerRow(0m, 3) == 1);
            Check("negative pixel height falls back to one tick", RowGrid.TicksPerRow(-2m, 3) == 1);
            Check("a zero minimum still gives a real row", RowGrid.TicksPerRow(10m, 0) == 1);
        }

        private static void RowsAreAnchoredSoScrollingDoesNotReshuffleThem()
        {
            // Two windows over the same market, scrolled by an amount that is not a whole row.
            var a = RowGrid.Build(29200m, 29260m, Tick, 8, 4000);
            var b = RowGrid.Build(29203.75m, 29263.75m, Tick, 8, 4000);

            Check("both windows build", a != null && b != null);
            if (a == null || b == null) return;

            Check("row size matches", a.RowSize == b.RowSize);

            // The boundary a price falls on must be identical in both, or the strip visibly
            // reshuffles which ticks are grouped every time the chart scrolls.
            var price = 29231.25m;
            var ia = a.IndexOf(price);
            var ib = b.IndexOf(price);

            Check("the same price sits on the same boundary", ia >= 0 && ib >= 0 && a.Low(ia) == b.Low(ib));
            Check("anchors differ by whole rows", (a.Anchor - b.Anchor) % a.RowSize == 0m);
        }

        private static void EveryPriceLandsInTheRowThatContainsIt()
        {
            var grid = RowGrid.Build(29200m, 29260m, Tick, 5, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            var ok = true;
            for (var price = 29200m; price <= 29260m; price += Tick)
            {
                var index = grid.IndexOf(price);
                if (index < 0) { ok = false; break; }
                if (price < grid.Low(index) || price >= grid.High(index)) { ok = false; break; }
            }

            Check("every visible price is inside its own row", ok);
            Check("the grid reaches the top of the window", grid.High(grid.Count - 1) >= 29260m);
        }

        private static void AggregationMatchesABruteForceScan()
        {
            var levels = SyntheticBook(29200m, 29260m, 400);
            var grid = RowGrid.Build(29200m, 29260m, Tick, 7, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            var rows = grid.Aggregate(levels);

            var ok = true;
            for (var i = 0; i < rows.Length && ok; i++)
            {
                decimal bid = 0m, ask = 0m;

                // The reference: walk every level and add the ones inside this row's price band.
                for (var j = 0; j < levels.Count; j++)
                {
                    var level = levels[j];
                    if (level.Price < grid.Low(i) || level.Price >= grid.High(i)) continue;

                    if (level.IsAsk) ask += level.Size;
                    else bid += level.Size;
                }

                if (rows[i].BidSize != bid || rows[i].AskSize != ask) ok = false;
            }

            Check("aggregated rows match a brute-force scan", ok);

            // Nothing inside the window may be lost on the way into the rows.
            decimal inBid = 0m, inAsk = 0m, outBid = 0m, outAsk = 0m;
            for (var j = 0; j < levels.Count; j++)
            {
                if (grid.IndexOf(levels[j].Price) < 0) continue;
                if (levels[j].IsAsk) inAsk += levels[j].Size; else inBid += levels[j].Size;
            }
            for (var i = 0; i < rows.Length; i++) { outBid += rows[i].BidSize; outAsk += rows[i].AskSize; }

            Check("no bid size is lost in bucketing", inBid == outBid);
            Check("no ask size is lost in bucketing", inAsk == outAsk);
        }

        private static void SidesNeverBleedIntoEachOther()
        {
            var grid = RowGrid.Build(29200m, 29210m, Tick, 10, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            // A bid and an ask on the same tick -- a crossed or stale book. They must stay apart.
            var levels = new List<DepthLevel>();
            levels.Add(new DepthLevel(29205m, 40m, false));
            levels.Add(new DepthLevel(29205m, 70m, true));

            var rows = grid.Aggregate(levels);
            var index = grid.IndexOf(29205m);

            Check("the bid keeps its own size", rows[index].BidSize == 40m);
            Check("the ask keeps its own size", rows[index].AskSize == 70m);
        }

        private static void LargestOrderTakesTheMaxNotTheSum()
        {
            var grid = RowGrid.Build(29200m, 29210m, Tick, 8, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            // Three ticks that share one row. The biggest single order in the row is 300,
            // not 500 -- summing them would report an order that does not exist.
            var levels = new List<DepthLevel>();
            levels.Add(Order(29200.25m, 120m, 300m, 2, true));
            levels.Add(Order(29200.50m, 90m, 90m, 1, true));
            levels.Add(Order(29200.75m, 110m, 110m, 1, true));

            var rows = grid.Aggregate(levels);
            var index = grid.IndexOf(29200.25m);

            Check("all three ticks share one row",
                  index == grid.IndexOf(29200.50m) && index == grid.IndexOf(29200.75m));
            Check("total size adds up", rows[index].AskSize == 320m);
            Check("the biggest order is the max, not the sum", rows[index].AskLargest == 300m);
            Check("order counts add up", rows[index].AskOrders == 4);
        }

        private static void LevelsOutsideTheGridAreDropped()
        {
            var grid = RowGrid.Build(29200m, 29210m, Tick, 4, 4000);
            if (grid == null) { Check("grid builds", false); return; }

            var levels = new List<DepthLevel>();
            levels.Add(new DepthLevel(29100m, 999m, false));   // below the window
            levels.Add(new DepthLevel(29400m, 999m, true));    // above the window
            levels.Add(new DepthLevel(29205m, 12m, false));    // inside

            var rows = grid.Aggregate(levels);

            var total = 0m;
            for (var i = 0; i < rows.Length; i++) total += rows[i].BidSize + rows[i].AskSize;

            Check("off-screen levels do not appear anywhere", total == 12m);
            Check("a price below the anchor is rejected", grid.IndexOf(29100m) == -1);
            Check("a price above the last row is rejected", grid.IndexOf(29400m) == -1);
        }

        #endregion

        #region Fade

        private static void PeakIsTakenImmediately()
        {
            var book = NewBook(1000, 2000);

            book.Observe(One(29300m, 50m, true), 0);
            book.Observe(One(29300m, 250m, true), 100);

            var shown = Emitted(book, 100, 29300m, true);
            Check("a level that grows shows its new size at once", shown == 250m);
        }

        private static void PulledSizeHoldsThenFadesToWhatIsThere()
        {
            var book = NewBook(1000, 2000);

            book.Observe(One(29300m, 400m, true), 0);
            book.Observe(One(29300m, 100m, true), 0);   // same stamp: peak is 400, current 100

            Check("inside the hold the peak still shows", Emitted(book, 500, 29300m, true) == 400m);
            Check("at the end of the hold the peak still shows", Emitted(book, 1000, 29300m, true) == 400m);

            // Halfway through a 2000ms fade: halfway between 400 and 100.
            Check("halfway through the fade is halfway down", Emitted(book, 2000, 29300m, true) == 250m);
            Check("a quarter through the fade", Emitted(book, 1500, 29300m, true) == 325m);

            Check("after the fade only what is there is shown", Emitted(book, 3000, 29300m, true) == 100m);
            Check("long after, still only what is there", Emitted(book, 99999, 29300m, true) == 100m);
        }

        private static void FadeIsNeverBelowWhatIsActuallyResting()
        {
            var book = NewBook(500, 500);

            // A level that shrinks and then grows again must never be drawn smaller than it is.
            var ok = true;
            book.Observe(One(29300m, 300m, false), 0);

            for (var t = 0; t <= 3000; t += 50)
            {
                var current = t < 1500 ? 20m : 260m;
                book.Observe(One(29300m, current, false), t);

                if (Emitted(book, t, 29300m, false) < current) ok = false;
            }

            Check("the drawn size is never below the resting size", ok);
        }

        private static void AReturningBiggerOrderResetsThePeak()
        {
            var book = NewBook(1000, 1000);

            book.Observe(One(29300m, 200m, false), 0);
            book.Observe(One(29300m, 0m, false), 0);       // pulled
            book.Observe(One(29300m, 500m, false), 1200);  // came back bigger, mid-fade

            Check("a bigger return replaces the old peak", Emitted(book, 1200, 29300m, false) == 500m);
            Check("the new peak holds from when it arrived", Emitted(book, 2100, 29300m, false) == 500m);
        }

        private static void FullyFadedEmptyLevelsAreForgotten()
        {
            var book = NewBook(100, 100);

            book.Observe(One(29300m, 80m, true), 0);
            book.Observe(new List<DepthLevel>(), 10);   // the whole book went away

            var still = book.Emit(5000);
            Check("a faded-out level leaves nothing to draw", still.Count == 0);
            Check("and nothing to remember", book.Count == 0);
        }

        private static void ZeroHoldAndFadeMeansTheRawBook()
        {
            var book = NewBook(0, 0);

            book.Observe(One(29300m, 400m, true), 0);
            book.Observe(One(29300m, 60m, true), 0);

            Check("with no hold or fade the raw size is shown", Emitted(book, 1, 29300m, true) == 60m);
        }

        private static void OrderCountsAreNotInventedForAGhost()
        {
            var book = NewBook(5000, 5000);

            book.Observe(One(Order(29300m, 400m, 400m, 1, true)), 0);
            book.Observe(new List<DepthLevel>(), 10);   // pulled

            var levels = book.Emit(100);
            Check("the ghost is still drawn", levels.Count == 1 && levels[0].Size == 400m);

            // The size is a memory of what was there. The order count describes what IS there,
            // and there is nothing there, so it must not still claim an order.
            Check("a ghost claims no orders", levels.Count == 1 && levels[0].Orders == 0);
            Check("a ghost claims no biggest order", levels.Count == 1 && levels[0].Largest == 0m);
        }

        private static void PruneDropsPricesOutOfRange()
        {
            var book = NewBook(1000, 1000);

            var levels = new List<DepthLevel>();
            levels.Add(new DepthLevel(29000m, 10m, false));
            levels.Add(new DepthLevel(29300m, 10m, false));
            levels.Add(new DepthLevel(29600m, 10m, true));
            book.Observe(levels, 0);

            Check("all three are remembered", book.Count == 3);

            book.Prune(29200m, 29400m);
            Check("only the in-range price survives", book.Count == 1);
        }

        #endregion

        #region Gradient and numbers

        private static void NormaliseClampsAndNeverInventsAScale()
        {
            Check("half of the reference is a half", DepthMath.Normalise(50m, 100m) == 0.5m);
            Check("the reference itself is one", DepthMath.Normalise(100m, 100m) == 1m);
            Check("above the reference clamps to one", DepthMath.Normalise(400m, 100m) == 1m);
            Check("zero size is zero", DepthMath.Normalise(0m, 100m) == 0m);

            // An empty book has no scale. Drawing a full bar off a zero reference would put a
            // maximum-size level on the chart out of nothing.
            Check("an empty book draws nothing, not everything", DepthMath.Normalise(50m, 0m) == 0m);
            Check("a negative reference draws nothing", DepthMath.Normalise(50m, -5m) == 0m);
        }

        private static void ShapeIsMonotonicAndKeepsItsEnds()
        {
            var curves = new IntensityCurve[]
            {
                IntensityCurve.Linear, IntensityCurve.EmphasiseSmall, IntensityCurve.EmphasiseLarge
            };

            var ok = true;
            for (var c = 0; c < curves.Length; c++)
            {
                if (DepthMath.Shape(0m, curves[c]) != 0m) ok = false;
                if (DepthMath.Shape(1m, curves[c]) != 1m) ok = false;

                var previous = -1m;
                for (var t = 0m; t <= 1m; t += 0.01m)
                {
                    var v = DepthMath.Shape(t, curves[c]);
                    if (v < previous) ok = false;
                    if (v < 0m || v > 1m) ok = false;
                    previous = v;
                }
            }

            Check("every curve is monotonic and stays in range", ok);

            Check("linear is the identity", DepthMath.Shape(0.4m, IntensityCurve.Linear) == 0.4m);
            Check("emphasise large pushes the middle down",
                  DepthMath.Shape(0.5m, IntensityCurve.EmphasiseLarge) < 0.5m);
            Check("emphasise small lifts the middle up",
                  DepthMath.Shape(0.5m, IntensityCurve.EmphasiseSmall) > 0.5m);
        }

        private static void GradientEndsExactlyOnTheChosenColours()
        {
            var low = new Rgb(10, 60, 45);
            var high = new Rgb(40, 255, 150);

            var at0 = DepthMath.Lerp(low, high, 0m);
            var at1 = DepthMath.Lerp(low, high, 1m);
            var mid = DepthMath.Lerp(low, high, 0.5m);

            Check("the smallest level is exactly the low colour",
                  at0.R == 10 && at0.G == 60 && at0.B == 45);
            Check("the biggest level is exactly the high colour",
                  at1.R == 40 && at1.G == 255 && at1.B == 150);
            Check("the middle is between the two",
                  mid.R == 25 && mid.G == 158 && mid.B == 98);

            Check("opacity ends are exact", DepthMath.LerpByte(20, 250, 0m) == 20 &&
                                            DepthMath.LerpByte(20, 250, 1m) == 250);
            Check("opacity clamps below", DepthMath.LerpByte(20, 250, -3m) == 20);
            Check("opacity clamps above", DepthMath.LerpByte(20, 250, 9m) == 250);
        }

        private static void CompactKeepsSmallSizesExact()
        {
            // On MNQ almost every level is under 1000. Rounding those to "0.1k" would hide the
            // difference between a 60 lot and a 140 lot, which is the whole signal.
            Check("small sizes are exact", DepthMath.Compact(7m) == "7");
            Check("mid sizes are exact", DepthMath.Compact(140m) == "140");
            Check("just under a thousand is exact", DepthMath.Compact(999m) == "999");

            Check("a thousand rounds up cleanly", DepthMath.Compact(999.6m) == "1k");
            Check("thousands keep one decimal", DepthMath.Compact(1234m) == "1.2k");
            Check("tens of thousands keep one decimal", DepthMath.Compact(12500m) == "12.5k");
            Check("hundreds of thousands drop the decimal", DepthMath.Compact(150000m) == "150k");
            Check("millions keep one decimal", DepthMath.Compact(1500000m) == "1.5M");

            Check("zero is zero", DepthMath.Compact(0m) == "0");
        }

        private static void TopIndicesRanksBiggestFirst()
        {
            var rows = new DepthRow[5];
            rows[0].BidSize = 10m;
            rows[1].BidSize = 0m;
            rows[2].BidSize = 400m;
            rows[3].BidSize = 90m;
            rows[4].BidSize = 250m;

            var top = DepthMath.TopIndices(rows, DepthMetric.TotalSize, false, 3);

            Check("three are returned", top.Length == 3);
            Check("biggest first", top.Length == 3 && top[0] == 2 && top[1] == 4 && top[2] == 3);

            var all = DepthMath.TopIndices(rows, DepthMetric.TotalSize, false, 99);
            Check("empty rows are never labelled", all.Length == 4);

            var none = DepthMath.TopIndices(rows, DepthMetric.TotalSize, true, 3);
            Check("an empty side returns nothing", none.Length == 0);

            Check("asking for none returns none",
                  DepthMath.TopIndices(rows, DepthMetric.TotalSize, false, 0).Length == 0);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// A book with size clustered at a few prices and thin everywhere else. It encodes a
        /// shape of market, not anything about how the rows are built.
        /// </summary>
        private static List<DepthLevel> SyntheticBook(decimal low, decimal high, int count)
        {
            var levels = new List<DepthLevel>(count);
            var seed = 987654;
            var mid = low + (high - low) / 2m;

            for (var i = 0; i < count; i++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7fffffff;

                var ticks = seed % (int)((high - low) / Tick);
                var price = low + ticks * Tick;

                var size = 5m + (seed / 7) % 60;
                if ((seed / 11) % 17 == 0) size += 400m;   // the occasional wall

                levels.Add(new DepthLevel(price, size, price > mid));
            }

            return levels;
        }

        private static DepthLevel Order(decimal price, decimal size, decimal largest, int orders, bool isAsk)
        {
            var level = new DepthLevel(price, size, isAsk);
            level.Largest = largest;
            level.Orders = orders;
            return level;
        }

        private static PeakBook NewBook(int holdMs, int fadeMs)
        {
            var book = new PeakBook();
            book.HoldMs = holdMs;
            book.FadeMs = fadeMs;
            return book;
        }

        private static List<DepthLevel> One(decimal price, decimal size, bool isAsk)
        {
            var levels = new List<DepthLevel>();
            levels.Add(new DepthLevel(price, size, isAsk));
            return levels;
        }

        private static List<DepthLevel> One(DepthLevel level)
        {
            var levels = new List<DepthLevel>();
            levels.Add(level);
            return levels;
        }

        private static decimal Emitted(PeakBook book, long nowMs, decimal price, bool isAsk)
        {
            var levels = book.Emit(nowMs);

            for (var i = 0; i < levels.Count; i++)
            {
                if (levels[i].Price == price && levels[i].IsAsk == isAsk) return levels[i].Size;
            }

            return 0m;
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
