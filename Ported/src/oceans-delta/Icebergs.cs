using System;
using System.Collections.Generic;

namespace OceansDelta
{
    /// <summary>One bar's footprint, flattened for the scan.</summary>
    public struct BarLevels
    {
        public int Bar;
        public decimal Close;
        public decimal High;
        public decimal Low;
        public IList<PriceVolume> Levels;
    }

    /// <summary>What a price has to have done before it counts as a line in the sand.</summary>
    public struct IcebergFilter
    {
        /// <summary>Contracts traded at the price across the whole window.</summary>
        public decimal MinVolume;

        /// <summary>
        /// That volume as a share of everything the window traded, 0-100. Keeps the answer from
        /// changing character between a quiet hour and a news bar.
        /// </summary>
        public decimal MinSharePercent;

        /// <summary>
        /// How one-sided the aggression into the price was, 0-100. This is the measurement that
        /// matters: one side kept paying and the price kept being there to pay into.
        /// </summary>
        public decimal MinLeanPercent;

        /// <summary>
        /// How many separate bars had to trade there. A single enormous print is a big trade, not
        /// a level that kept reloading, and this is the only thing separating the two.
        /// </summary>
        public int MinBars;

        /// <summary>
        /// A close this far beyond the price, on the far side of whoever was passive, counts as
        /// the level going. In ticks.
        /// </summary>
        public int ThroughTicks;

        /// <summary>Report levels that were traded through as well, marked broken.</summary>
        public bool KeepBroken;
    }

    /// <summary>
    /// A price that kept absorbing one-sided aggression across several bars.
    ///
    /// This is evidence, not an identification. There is no order-book data behind it: a genuine
    /// refreshing iceberg leaves exactly this footprint, and so does one large resting order that
    /// was never replenished, and so does a price that simply kept attracting business. What is
    /// measured is what is claimed -- repeat one-sided absorption at a single price.
    /// </summary>
    public struct Iceberg
    {
        public decimal Price;

        public int FirstBar;
        public int LastBar;

        /// <summary>Separate bars that traded there.</summary>
        public int Bars;

        public decimal Volume;
        public decimal Bid;
        public decimal Ask;
        public decimal Delta;

        /// <summary>|Delta| as a share of Volume, 0-100.</summary>
        public decimal LeanPercent;

        /// <summary>Volume as a share of the window's whole volume, 0-100.</summary>
        public decimal SharePercent;

        /// <summary>
        /// +1 someone passive was BUYING -- sellers kept hitting the bid there and it held.
        /// -1 someone passive was SELLING -- buyers kept lifting the offer and it held.
        /// </summary>
        public int Side;

        /// <summary>No bar in the window closed through it against the passive side.</summary>
        public bool Held;

        /// <summary>Bars that closed through it. Zero when it held.</summary>
        public int Breaks;
    }

    /// <summary>The scan's result, and any reason it could not be trusted.</summary>
    public struct IcebergScan
    {
        public List<Iceberg> Found;

        /// <summary>Non-null when the scan was abandoned. Nothing is returned half-done.</summary>
        public string Problem;
    }

    public static class IcebergSearch
    {
        /// <summary>
        /// Walks a window of footprints, accumulates every price across all of them, and returns
        /// the prices that kept absorbing.
        ///
        /// The window is a fixed number of bars ending at the newest, never the visible range.
        /// Anchoring this to what happens to be on screen would move the levels when you scrolled,
        /// which is the one thing a level must not do.
        /// </summary>
        public static IcebergScan Scan(IList<BarLevels> window, decimal tick, IcebergFilter filter,
                                       int maxLevels)
        {
            var scan = new IcebergScan();
            var found = new List<Iceberg>();
            scan.Found = found;

            if (window == null || window.Count == 0 || tick <= 0m) return scan;

            var minBars = filter.MinBars < 1 ? 1 : filter.MinBars;
            if (window.Count < minBars) return scan;

            var tape = new Dictionary<decimal, Iceberg>();
            var total = 0m;

            for (var i = 0; i < window.Count; i++)
            {
                var bar = window[i];
                if (bar.Levels == null) continue;

                for (var j = 0; j < bar.Levels.Count; j++)
                {
                    var level = bar.Levels[j];
                    if (level.Volume <= 0m) continue;

                    total += level.Volume;

                    Iceberg at;
                    if (!tape.TryGetValue(level.Price, out at))
                    {
                        // Abandoned whole rather than truncated. A partial tape would answer the
                        // question with some of the window missing and look exactly like an answer.
                        if (tape.Count >= maxLevels)
                        {
                            scan.Problem = "iceberg scan: over " + maxLevels + " prices in the " +
                                           "window. Shorten it or raise the volume floor.";
                            return scan;
                        }

                        at = new Iceberg();
                        at.Price = level.Price;
                        at.FirstBar = bar.Bar;
                    }

                    at.LastBar = bar.Bar;
                    at.Bars++;
                    at.Volume += level.Volume;
                    at.Bid += level.Bid;
                    at.Ask += level.Ask;

                    tape[level.Price] = at;
                }
            }

            if (total <= 0m) return scan;

            var through = filter.ThroughTicks < 0 ? 0 : filter.ThroughTicks;

            foreach (var pair in tape)
            {
                var at = pair.Value;

                if (at.Bars < minBars) continue;
                if (at.Volume < filter.MinVolume) continue;

                at.Delta = at.Ask - at.Bid;

                var size = at.Delta < 0m ? -at.Delta : at.Delta;
                at.LeanPercent = size * 100m / at.Volume;
                if (at.LeanPercent < filter.MinLeanPercent) continue;

                at.SharePercent = at.Volume * 100m / total;
                if (at.SharePercent < filter.MinSharePercent) continue;

                // Heavier BID volume means sellers were the ones paying, so whoever stood on the
                // other side was buying. The passive side is the opposite of the aggressive one.
                at.Side = at.Delta < 0m ? 1 : at.Delta > 0m ? -1 : 0;
                if (at.Side == 0) continue;

                at.Breaks = Breaks(window, at, tick * through);
                at.Held = at.Breaks == 0;

                if (!at.Held && !filter.KeepBroken) continue;

                found.Add(at);
            }

            // Heaviest first, so a cap on how many are drawn keeps the ones that matter.
            found.Sort((a, b) => b.Volume.CompareTo(a.Volume));

            return scan;
        }

        /// <summary>
        /// Bars that CLOSED through the level against the side that was passive. Closes, not
        /// wicks: price reaching through a level and coming back is the level working, and
        /// counting that as a break would throw away every level that ever did its job.
        ///
        /// Only bars at or after the level first traded are looked at -- what price did before it
        /// existed is not evidence about it.
        /// </summary>
        private static int Breaks(IList<BarLevels> window, Iceberg at, decimal distance)
        {
            var breaks = 0;

            for (var i = 0; i < window.Count; i++)
            {
                var bar = window[i];
                if (bar.Bar < at.FirstBar) continue;

                if (at.Side > 0 && bar.Close < at.Price - distance) breaks++;
                if (at.Side < 0 && bar.Close > at.Price + distance) breaks++;
            }

            return breaks;
        }
    }
}
