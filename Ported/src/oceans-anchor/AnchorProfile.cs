using System;
using System.Collections.Generic;

namespace OceansAnchor
{
    /// <summary>
    /// Volume at price for one session window, plus where it sits in the bar array.
    ///
    /// The dictionary is keyed by exact tick price. Nothing here reads exchange session
    /// metadata: SDK versions disagree about what that contains, and a chart loaded from
    /// another feed can carry none at all.
    /// </summary>
    public sealed class SessionProfile
    {
        public readonly Dictionary<decimal, decimal> VolByPrice = new Dictionary<decimal, decimal>();

        public DateTime TradeDate;

        public decimal Poc, PocVol, Vah, Val, TotalVol;

        /// <summary>Extremes of THIS window only. The value area is clamped to them.</summary>
        public decimal High, Low;

        /// <summary>
        /// Extremes of the whole trade date this window belongs to, RTH and overnight together.
        /// Kept separately because ADR means the day's range, but the value area must never be
        /// stretched over hours whose volume this profile does not contain.
        /// </summary>
        public decimal DayHigh, DayLow;

        public int StartBar = -1, EndBar = -1;

        public bool HasBars { get { return StartBar >= 0; } }

        public decimal DayRange { get { return DayHigh > DayLow ? DayHigh - DayLow : Range; } }

        public void NoteDay(decimal high, decimal low)
        {
            if (DayHigh == 0m && DayLow == 0m) { DayHigh = high; DayLow = low; return; }
            if (high > DayHigh) DayHigh = high;
            if (low < DayLow) DayLow = low;
        }

        public void Add(decimal price, decimal volume)
        {
            if (volume <= 0m) return;

            decimal v;
            VolByPrice.TryGetValue(price, out v);
            VolByPrice[price] = v + volume;
            TotalVol += volume;
        }

        public void NoteBar(int bar, decimal high, decimal low)
        {
            if (StartBar < 0) { StartBar = bar; High = high; Low = low; }
            if (high > High) High = high;
            if (low < Low) Low = low;
            EndBar = bar;
        }

        public decimal Range { get { return High - Low; } }

        /// <summary>
        /// POC is the argmax of the RAW ladder, not the smoothed one. That is what ATAS's own
        /// Market Profile reports, and build step 2 is eyeballed against it -- a smoothed POC
        /// would sit a tick or two off and make the parity check fail for no reason.
        ///
        /// Ties go to the price nearest the middle of the range, so a flat two-bin top does not
        /// jump between sessions on rounding.
        /// </summary>
        public void ComputePoc()
        {
            Poc = 0m;
            PocVol = 0m;
            if (VolByPrice.Count == 0) return;

            var mid = (High + Low) / 2m;
            var best = 0m;
            var bestVol = -1m;
            var bestDist = decimal.MaxValue;

            foreach (var kv in VolByPrice)
            {
                var dist = Math.Abs(kv.Key - mid);

                if (kv.Value > bestVol || (kv.Value == bestVol && dist < bestDist))
                {
                    best = kv.Key;
                    bestVol = kv.Value;
                    bestDist = dist;
                }
            }

            Poc = best;
            PocVol = bestVol < 0m ? 0m : bestVol;
        }

        /// <summary>
        /// Standard 70% value area, expanded in PAIRS of ticks.
        ///
        /// The pairing is not decoration: comparing one tick at a time lets a single noisy bin
        /// flip which side the area grows toward, and the area then zigzags out from the POC
        /// asymmetrically. Two-tick steps are the convention every profile package uses and the
        /// one ATAS's own value area agrees with to a tick or two.
        ///
        /// ComputePoc must have run first.
        /// </summary>
        public void ComputeValueArea(decimal tickSize, decimal valueAreaPct)
        {
            Vah = Poc;
            Val = Poc;

            if (tickSize <= 0m || TotalVol <= 0m || VolByPrice.Count == 0) return;

            var target = valueAreaPct * TotalVol;
            var inArea = PocVol;

            var above = Poc + tickSize;
            var below = Poc - tickSize;

            // Bounded by the ladder, so a profile that never reaches the target (possible when
            // valueAreaPct is set near 1) terminates at the extremes instead of spinning.
            while (inArea < target && (above <= High || below >= Low))
            {
                var upVol = PairVolume(above, tickSize, +1);
                var downVol = PairVolume(below, tickSize, -1);

                var canUp = above <= High;
                var canDown = below >= Low;

                if (canUp && (!canDown || upVol >= downVol))
                {
                    inArea += upVol;
                    Vah = Math.Min(above + tickSize, High);
                    above += tickSize * 2m;
                }
                else if (canDown)
                {
                    inArea += downVol;
                    Val = Math.Max(below - tickSize, Low);
                    below -= tickSize * 2m;
                }
                else break;
            }
        }

        private decimal PairVolume(decimal start, decimal tickSize, int direction)
        {
            var total = 0m;

            for (var i = 0; i < 2; i++)
            {
                var price = start + tickSize * i * direction;
                decimal v;
                if (VolByPrice.TryGetValue(price, out v)) total += v;
            }

            return total;
        }
    }

    /// <summary>One extracted high-volume shelf: the node peak plus the edges it holds out to.</summary>
    public sealed class HvnShelf
    {
        public decimal Bottom, Top, Peak;

        /// <summary>Smoothed volume at the peak. The outlier test is run against this, not the raw bin.</summary>
        public decimal PeakVol;

        public bool Contains(decimal p) { return p >= Bottom && p <= Top; }
        public bool Overlaps(HvnShelf o) { return Bottom <= o.Top && o.Bottom <= Top; }
    }

    /// <summary>Thresholds for shelf extraction. All of these are indicator settings.</summary>
    public sealed class HvnSettings
    {
        public int SmoothTicks = 4;
        public int PeakWindowTicks = 12;

        /// <summary>Outlier test: a peak must be this share of the POC bin or it is not an HVN.</summary>
        public decimal NodePeakPct = 0.70m;

        /// <summary>Shelf walk-out cutoff, as a share of the peak.</summary>
        public decimal ShelfEdgePct = 0.50m;

        /// <summary>Width cap in ticks. 100 ticks = 25 NQ points.</summary>
        public int MaxShelfTicks = 100;

        /// <summary>An LVN valley this shallow between two peaks makes it a double distribution.</summary>
        public decimal LvnValleyPct = 0.30m;
    }

    public static class ProfileMath
    {
        /// <summary>
        /// Pulls the outlier high-volume shelves out of a volume-at-price ladder.
        ///
        /// This is the filter that makes the playbook rather than another profile drawing. The
        /// rule it implements is literal: if you have to squint, it is not an HVN. A peak that
        /// is merely locally highest still gets thrown away unless it is a real fraction of the
        /// session POC, because a local maximum in a thin patch of the ladder is noise wearing
        /// the shape of structure.
        ///
        /// Returns shelves sorted low to high, overlaps merged. The POC always survives: it is
        /// the global maximum, so it passes the peak test and the outlier test by construction.
        /// </summary>
        public static List<HvnShelf> ExtractShelves(IDictionary<decimal, decimal> volByPrice,
                                                    decimal tickSize, HvnSettings s)
        {
            var shelves = new List<HvnShelf>();
            if (volByPrice == null || volByPrice.Count == 0 || tickSize <= 0m || s == null)
                return shelves;

            decimal low = decimal.MaxValue, high = decimal.MinValue;
            foreach (var kv in volByPrice)
            {
                if (kv.Key < low) low = kv.Key;
                if (kv.Key > high) high = kv.Key;
            }

            var steps = (int)Math.Round((double)((high - low) / tickSize));
            if (steps < 0) return shelves;

            var n = steps + 1;

            // A ladder this short cannot express a peak with a window around it; extracting one
            // would just be relabelling the whole range as a shelf.
            if (n < 3) return shelves;

            var raw = new decimal[n];
            for (var i = 0; i < n; i++)
            {
                decimal v;
                volByPrice.TryGetValue(low + tickSize * i, out v);
                raw[i] = v;
            }

            var smoothed = Smooth(raw, s.SmoothTicks);

            var pocIdx = 0;
            for (var i = 1; i < n; i++)
                if (smoothed[i] > smoothed[pocIdx]) pocIdx = i;

            if (smoothed[pocIdx] <= 0m) return shelves;

            var floor = s.NodePeakPct * smoothed[pocIdx];
            var window = Math.Max(1, s.PeakWindowTicks);

            for (var i = 0; i < n; i++)
            {
                if (smoothed[i] < floor) continue;
                if (!IsLocalPeak(smoothed, i, window)) continue;

                shelves.Add(WalkOut(smoothed, i, low, tickSize, s));
            }

            shelves.Sort(delegate (HvnShelf a, HvnShelf b) { return a.Bottom.CompareTo(b.Bottom); });

            // Clamp AFTER merging, not just during the walk-out. Merging unions overlapping
            // shelves, so a featureless ladder where every bin is a local peak produces a chain
            // of individually-capped shelves that merge into one spanning the whole range --
            // the cap held at every step and still lost. A zone the height of the session is
            // not a level, it is a shrug.
            return Clamp(Merge(shelves), tickSize, s.MaxShelfTicks);
        }

        /// <summary>Holds a merged shelf to the width cap, keeping it centred on its peak.</summary>
        private static List<HvnShelf> Clamp(List<HvnShelf> shelves, decimal tickSize, int maxTicks)
        {
            if (maxTicks <= 0 || tickSize <= 0m) return shelves;

            var maxWidth = tickSize * maxTicks;

            foreach (var shelf in shelves)
            {
                if (shelf.Top - shelf.Bottom <= maxWidth) continue;

                var half = maxWidth / 2m;

                var bottom = shelf.Peak - half;
                var top = shelf.Peak + half;

                // Keep it inside the shelf it came from: a peak near one edge slides the window
                // rather than inventing prices the node never covered.
                if (bottom < shelf.Bottom) { bottom = shelf.Bottom; top = bottom + maxWidth; }
                if (top > shelf.Top) { top = shelf.Top; bottom = top - maxWidth; }

                shelf.Bottom = bottom;
                shelf.Top = top;
            }

            return shelves;
        }

        /// <summary>Boxcar mean over +/- k ticks, clamped at the ladder edges.</summary>
        public static decimal[] Smooth(decimal[] raw, int k)
        {
            var n = raw.Length;
            var outp = new decimal[n];
            if (k < 1) { Array.Copy(raw, outp, n); return outp; }

            for (var i = 0; i < n; i++)
            {
                var from = Math.Max(0, i - k);
                var to = Math.Min(n - 1, i + k);

                var sum = 0m;
                for (var j = from; j <= to; j++) sum += raw[j];

                outp[i] = sum / (to - from + 1);
            }

            return outp;
        }

        /// <summary>
        /// At least as high as everything within the window. Non-strict on both sides so a flat
        /// shelf top does not disqualify itself; the merge step folds the duplicates back
        /// together afterwards.
        /// </summary>
        private static bool IsLocalPeak(decimal[] v, int i, int window)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(v.Length - 1, i + window);

            for (var j = from; j <= to; j++)
                if (v[j] > v[i]) return false;

            return true;
        }

        /// <summary>
        /// Walks out from the peak while the ladder still holds a real share of it, which is
        /// what turns a peak into a shelf with edges you can put a stop behind. The width cap
        /// stops a flat, featureless session from producing one shelf that swallows the range.
        /// </summary>
        private static HvnShelf WalkOut(decimal[] smoothed, int peak, decimal low, decimal tickSize,
                                        HvnSettings s)
        {
            var cutoff = s.ShelfEdgePct * smoothed[peak];
            var maxHalf = Math.Max(1, s.MaxShelfTicks / 2);

            var lo = peak;
            while (lo > 0 && smoothed[lo - 1] >= cutoff && peak - (lo - 1) <= maxHalf) lo--;

            var hi = peak;
            while (hi < smoothed.Length - 1 && smoothed[hi + 1] >= cutoff && (hi + 1) - peak <= maxHalf) hi++;

            return new HvnShelf
            {
                Bottom = low + tickSize * lo,
                Top = low + tickSize * hi,
                Peak = low + tickSize * peak,
                PeakVol = smoothed[peak]
            };
        }

        /// <summary>Overlapping shelves become one, keeping the stronger peak.</summary>
        public static List<HvnShelf> Merge(List<HvnShelf> sorted)
        {
            var merged = new List<HvnShelf>();

            foreach (var shelf in sorted)
            {
                if (merged.Count > 0 && merged[merged.Count - 1].Overlaps(shelf))
                {
                    var last = merged[merged.Count - 1];
                    if (shelf.Top > last.Top) last.Top = shelf.Top;
                    if (shelf.Bottom < last.Bottom) last.Bottom = shelf.Bottom;

                    if (shelf.PeakVol > last.PeakVol)
                    {
                        last.Peak = shelf.Peak;
                        last.PeakVol = shelf.PeakVol;
                    }

                    continue;
                }

                merged.Add(shelf);
            }

            return merged;
        }

        /// <summary>
        /// A double-distribution day: two qualifying peaks with a genuine LVN valley between
        /// them. Both deserve a zone -- the day had two auctions, and the one that is not the
        /// session POC is exactly the level nobody else has drawn.
        ///
        /// Needs the shelves and the ladder they came from, because the valley test is about
        /// what sits BETWEEN two shelves, which the shelves themselves no longer record.
        /// </summary>
        public static bool IsDoubleDistribution(IDictionary<decimal, decimal> volByPrice,
                                                decimal tickSize, HvnSettings s,
                                                HvnShelf a, HvnShelf b)
        {
            if (a == null || b == null || tickSize <= 0m) return false;

            var lower = a.Peak < b.Peak ? a : b;
            var upper = a.Peak < b.Peak ? b : a;
            if (upper.Peak <= lower.Peak) return false;

            var pocVol = Math.Max(a.PeakVol, b.PeakVol);
            if (pocVol <= 0m) return false;

            var valleyCut = s.LvnValleyPct * pocVol;

            var deepest = decimal.MaxValue;
            for (var p = lower.Top + tickSize; p < upper.Bottom; p += tickSize)
            {
                decimal v;
                volByPrice.TryGetValue(p, out v);
                if (v < deepest) deepest = v;
            }

            // Shelves that touch have no valley between them at all.
            if (deepest == decimal.MaxValue) return false;

            return deepest <= valleyCut;
        }

        /// <summary>
        /// Average daily range over the last N sessions, used for the distance cut and for the
        /// arrival multiple logged to CSV. Zero when there is not enough history, which every
        /// caller treats as "do not apply the distance filter" rather than as "everything is
        /// far away".
        /// </summary>
        public static decimal AverageDailyRange(IList<SessionProfile> sessions, int count)
        {
            if (sessions == null || sessions.Count == 0 || count <= 0) return 0m;

            var taken = 0;
            var sum = 0m;

            for (var i = sessions.Count - 1; i >= 0 && taken < count; i--)
            {
                var s = sessions[i];
                if (!s.HasBars || s.DayRange <= 0m) continue;

                sum += s.DayRange;
                taken++;
            }

            return taken == 0 ? 0m : sum / taken;
        }
    }
}
