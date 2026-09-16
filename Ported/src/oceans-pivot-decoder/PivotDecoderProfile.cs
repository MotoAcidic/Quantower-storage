using System;
using System.Collections.Generic;

namespace OceansPivotDecoder
{
    /// <summary>Volume traded at one price. The unit every profile calculation works in.</summary>
    public struct PriceVolume
    {
        public decimal Price;
        public decimal Volume;
        public decimal Bid;
        public decimal Ask;
    }

    /// <summary>A finished volume profile for one session window.</summary>
    public sealed class Profile
    {
        public bool Valid;

        public decimal Poc;
        public decimal Vah;
        public decimal Val;
        public decimal Vwap;
        public decimal TotalVolume;

        /// <summary>Why it could not be built, when it could not.</summary>
        public string Problem;
    }

    /// <summary>
    /// Everything derived from a price-volume array: POC, value area, VWAP, the poor-extreme test
    /// and the percentile used for big-print detection.
    ///
    /// Pure, and deliberately free of ATAS types -- the platform side hands it an array and gets
    /// numbers back, so all of this runs in the test harness against hand-built distributions.
    /// </summary>
    public static class ProfileMath
    {
        /// <summary>
        /// POC, value area and VWAP in one pass over the rows.
        ///
        /// Returns an invalid profile with a reason rather than a guess when there are no rows. A
        /// POC inferred from OHLC would look exactly as convincing on the chart as a real one and
        /// be worth nothing, so nothing here falls back to one.
        /// </summary>
        public static Profile Build(IList<PriceVolume> rows, decimal valueAreaPercent)
        {
            var profile = new Profile();

            if (rows == null || rows.Count == 0)
            {
                profile.Problem = "no volume-at-price on these bars";
                return profile;
            }

            var sorted = Sorted(rows);

            var total = 0m;
            var weighted = 0m;

            foreach (var row in sorted)
            {
                total += row.Volume;
                weighted += row.Price * row.Volume;
            }

            if (total <= 0m)
            {
                profile.Problem = "no volume-at-price on these bars";
                return profile;
            }

            var pocIndex = 0;
            for (var i = 1; i < sorted.Count; i++)
                if (sorted[i].Volume > sorted[pocIndex].Volume) pocIndex = i;

            // Value area by the standard PAIR rule: compare the next TWO rows above against the
            // next two below and take the heavier pair whole. Expanding one row at a time is the
            // obvious implementation and it is not the convention -- it walks a different path up
            // a jagged profile and lands on different edges. Since VAH and VAL drive the overnight
            // inventory read, being a row out there changes the stated bias.
            var target = total * valueAreaPercent / 100m;
            var covered = sorted[pocIndex].Volume;

            var low = pocIndex;
            var high = pocIndex;

            while (covered < target && (low > 0 || high < sorted.Count - 1))
            {
                var above = Pair(sorted, high + 1, 1);
                var below = Pair(sorted, low - 1, -1);

                if (above.Count == 0 && below.Count == 0) break;

                if (below.Count == 0 || (above.Count > 0 && above.Volume >= below.Volume))
                {
                    high += above.Count;
                    covered += above.Volume;
                }
                else
                {
                    low -= below.Count;
                    covered += below.Volume;
                }
            }

            profile.Valid = true;
            profile.Poc = sorted[pocIndex].Price;
            profile.Val = sorted[low].Price;
            profile.Vah = sorted[high].Price;
            profile.Vwap = weighted / total;
            profile.TotalVolume = total;

            return profile;
        }

        /// <summary>Up to two rows from <paramref name="start"/> in <paramref name="step"/> direction.</summary>
        private static RowPair Pair(IList<PriceVolume> rows, int start, int step)
        {
            var pair = new RowPair();

            for (var n = 0; n < 2; n++)
            {
                var i = start + n * step;
                if (i < 0 || i >= rows.Count) break;

                pair.Volume += rows[i].Volume;
                pair.Count++;
            }

            return pair;
        }

        private struct RowPair
        {
            public decimal Volume;
            public int Count;
        }

        /// <summary>
        /// Whether a session extreme was left unfinished -- a poor high or poor low.
        ///
        /// Two independent readings, either of which flags it:
        ///
        ///   NO TAPER. A completed auction thins out as it runs out of buyers, so the last tick
        ///   of a good high trades a fraction of what three ticks below it traded. When the
        ///   extreme tick still holds <paramref name="ratio"/> or more of the volume three ticks
        ///   back, the move was cut off rather than exhausted.
        ///
        ///   LEDGE. Two or more bars printing the exact same extreme is a flat top: price was
        ///   stopped by something, not by the auction finishing.
        ///
        /// Both are revisit magnets, which is why they carry the same weight as a naked POC.
        ///
        /// Returns false when the rows do not reach three ticks back -- unknown, not clean. A thin
        /// session with no data behind its high must never be reported as a good high.
        /// </summary>
        public static bool IsPoorExtreme(IList<PriceVolume> rows, decimal extreme, bool isHigh,
                                         decimal tick, decimal ratio, int barsAtExtreme,
                                         out string reason)
        {
            reason = null;

            if (barsAtExtreme >= 2)
            {
                reason = "ledge, " + barsAtExtreme + " bars at " + extreme.ToString("F2");
                return true;
            }

            if (rows == null || rows.Count == 0 || tick <= 0m) return false;

            var atExtreme = VolumeAt(rows, extreme);
            var behind = VolumeAt(rows, isHigh ? extreme - 3m * tick : extreme + 3m * tick);

            if (behind <= 0m) return false;

            if (atExtreme >= ratio * behind)
            {
                reason = "no taper, " + Share(atExtreme, behind) + "% of 3 ticks back";
                return true;
            }

            return false;
        }

        private static string Share(decimal at, decimal behind)
        {
            return Math.Round(at * 100m / behind, 0).ToString("F0");
        }

        public static decimal VolumeAt(IList<PriceVolume> rows, decimal price)
        {
            foreach (var row in rows) if (row.Price == price) return row.Volume;
            return 0m;
        }

        /// <summary>
        /// Nearest-rank percentile of a set of per-price volumes, used to decide what counts as a
        /// big print. Adaptive on purpose: a fixed contract count is wrong the moment the session
        /// changes character, and badly wrong across instruments.
        /// </summary>
        public static decimal Percentile(IList<decimal> values, decimal percentile)
        {
            if (values == null || values.Count == 0) return 0m;

            var sorted = new List<decimal>(values);
            sorted.Sort();

            if (percentile <= 0m) return sorted[0];
            if (percentile >= 100m) return sorted[sorted.Count - 1];

            var rank = (int)Math.Ceiling((double)(percentile / 100m) * sorted.Count) - 1;
            if (rank < 0) rank = 0;
            if (rank >= sorted.Count) rank = sorted.Count - 1;

            return sorted[rank];
        }

        private static List<PriceVolume> Sorted(IList<PriceVolume> rows)
        {
            var list = new List<PriceVolume>(rows);
            list.Sort(delegate (PriceVolume a, PriceVolume b) { return a.Price.CompareTo(b.Price); });
            return list;
        }
    }

    #region Naked POCs

    /// <summary>A daily POC and whether price has been back through it.</summary>
    public sealed class NakedPoc
    {
        public DateTime TradeDate;
        public decimal Price;

        /// <summary>False once any later bar has traded through it.</summary>
        public bool Naked;

        /// <summary>Sessions since it printed, for the readout.</summary>
        public int AgeDays;
    }

    /// <summary>An unfinished session extreme and whether it has been repaired.</summary>
    public sealed class PoorExtreme
    {
        public DateTime TradeDate;
        public string Window;
        public decimal Price;
        public bool IsHigh;
        public string Reason;

        /// <summary>False once price has traded far enough through to complete the auction.</summary>
        public bool Unrepaired;

        public int AgeDays;
    }

    #endregion
}
