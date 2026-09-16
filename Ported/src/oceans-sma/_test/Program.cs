using System;
using OceansSma;

namespace OceansSma.Tests
{
    /// <summary>
    /// Checks RollingMean against a brute-force average that shares none of its code, driven
    /// through the three call patterns ATAS actually uses: a forward walk over history, the
    /// same bar hit over and over as ticks land, and a jump backwards after a recalculate.
    /// </summary>
    static class Program
    {
        private const int Bars = 900;

        private static decimal[] _prices;
        private static int _failures;

        static int Main()
        {
            _prices = SyntheticPrices(Bars);

            ForwardWalkMatchesBruteForce();
            WarmupIsNullUntilTheWindowFills();
            RepeatedTicksOnTheSameBarDoNotDrift();
            JumpingBackwardsReseedsCorrectly();
            RandomAccessMatchesBruteForce();
            PeriodOneIsThePriceItself();
            ShortHistoryNeverAverages();

            if (_failures == 0)
            {
                Console.WriteLine("All RollingMean tests passed.");
                return 0;
            }

            Console.WriteLine(_failures + " test(s) FAILED.");
            return 1;
        }

        /// <summary>
        /// A deterministic random walk. It encodes nothing about how the average is computed --
        /// it is only a price path with drift, gaps and flat stretches for the window to chew on.
        /// </summary>
        private static decimal[] SyntheticPrices(int count)
        {
            var prices = new decimal[count];
            var price = 21000m;
            var seed = 12345;

            for (var i = 0; i < count; i++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                var step = (seed % 41) - 20;              // -20 .. +20 ticks
                if (i % 97 == 0) step *= 12;              // occasional gap
                if (i % 13 == 0) step = 0;                // occasional flat bar

                price += step * 0.25m;
                if (price < 1m) price = 1m;
                prices[i] = price;
            }

            return prices;
        }

        /// <summary>Average of the Period prices ending at bar, or null if there are not that many.</summary>
        private static decimal? BruteForce(int bar, int period)
        {
            if (bar < period - 1) return null;

            var sum = 0m;
            for (var i = bar - period + 1; i <= bar; i++) sum += _prices[i];
            return sum / period;
        }

        private static RollingMean New(int period)
        {
            return new RollingMean(period, delegate (int bar) { return _prices[bar]; });
        }

        private static void ForwardWalkMatchesBruteForce()
        {
            foreach (var period in new[] { 50, 100, 200 })
            {
                var mean = New(period);
                for (var bar = 0; bar < Bars; bar++)
                    Check("forward p" + period + " bar " + bar, mean.At(bar), BruteForce(bar, period));
            }
        }

        private static void WarmupIsNullUntilTheWindowFills()
        {
            var mean = New(200);
            for (var bar = 0; bar < Bars; bar++)
            {
                var got = mean.At(bar);
                var expectNull = bar < 199;

                if (expectNull && got != null)
                    Fail("warmup bar " + bar + ": expected null, got " + got);
                if (!expectNull && got == null)
                    Fail("warmup bar " + bar + ": expected a value, got null");
            }
        }

        private static void RepeatedTicksOnTheSameBarDoNotDrift()
        {
            var mean = New(100);

            for (var bar = 0; bar < Bars; bar++)
            {
                var first = mean.At(bar);

                // ATAS re-calls the forming bar on every tick. Twenty repeats must not move it.
                for (var tick = 0; tick < 20; tick++)
                    Check("tick replay bar " + bar, mean.At(bar), first);

                Check("tick replay value bar " + bar, first, BruteForce(bar, 100));
            }
        }

        private static void JumpingBackwardsReseedsCorrectly()
        {
            var mean = New(50);

            for (var bar = 0; bar < 400; bar++) mean.At(bar);

            // A recalculate restarts the walk at bar 0 without a fresh instance.
            for (var bar = 0; bar < Bars; bar++)
                Check("post-recalc bar " + bar, mean.At(bar), BruteForce(bar, 50));
        }

        private static void RandomAccessMatchesBruteForce()
        {
            var mean = New(200);
            var seed = 999;

            for (var n = 0; n < 3000; n++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                var bar = seed % Bars;
                Check("random bar " + bar, mean.At(bar), BruteForce(bar, 200));
            }
        }

        private static void PeriodOneIsThePriceItself()
        {
            var mean = New(1);
            for (var bar = 0; bar < Bars; bar++)
                Check("period 1 bar " + bar, mean.At(bar), _prices[bar]);
        }

        private static void ShortHistoryNeverAverages()
        {
            // Forty bars of history must not produce an "SMA 200" -- a partial window wearing
            // the 200 label is a different number, and reading it as support is a real loss.
            var mean = New(200);
            for (var bar = 0; bar < 40; bar++)
            {
                if (mean.At(bar) != null) Fail("short history bar " + bar + ": averaged a partial window");
            }
        }

        private static void Check(string what, decimal? got, decimal? expected)
        {
            if (got == null && expected == null) return;

            if (got == null || expected == null)
            {
                Fail(what + ": got " + Show(got) + ", expected " + Show(expected));
                return;
            }

            if (Math.Abs(got.Value - expected.Value) > 0.0000001m)
                Fail(what + ": got " + got.Value + ", expected " + expected.Value);
        }

        private static string Show(decimal? v)
        {
            return v == null ? "null" : v.Value.ToString();
        }

        private static void Fail(string message)
        {
            _failures++;
            if (_failures <= 10) Console.WriteLine("FAIL  " + message);
        }
    }
}
