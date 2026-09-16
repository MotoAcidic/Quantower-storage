using System;
using System.Collections.Generic;

namespace OceansDeveloped
{
    /// <summary>One price on the ladder and everything that traded there.</summary>
    public struct VolumeLevel
    {
        public decimal Volume;
        public decimal Bid;      // traded into the bid -- sellers aggressing
        public decimal Ask;      // traded into the ask -- buyers aggressing

        public decimal Delta { get { return Ask - Bid; } }
    }

    /// <summary>
    /// A finished volume profile: a DENSE ladder of prices with the volume that traded at each,
    /// plus the point of control and value area derived from it.
    ///
    /// Dense is load-bearing. A tick that never traded is a real zero, not a missing row -- a
    /// sparse ladder puts every index-based comparison on the wrong price, and the result still
    /// looks like a plausible profile.
    /// </summary>
    public sealed class Profile
    {
        public decimal TickSize;
        public decimal LowPrice;          // the price of Levels[0]
        public VolumeLevel[] Levels;

        public decimal TotalVolume;
        public decimal TotalDelta;
        public decimal MaxLevelVolume;

        public int PocIndex = -1;
        public int VahIndex = -1;
        public int ValIndex = -1;

        /// <summary>The period this profile covers, and whether the loaded bars spanned it.</summary>
        public DateTime Start;
        public DateTime End;
        public int Days;
        public bool Complete = true;

        public int Count { get { return Levels == null ? 0 : Levels.Length; } }

        public decimal PriceAt(int index) { return LowPrice + TickSize * index; }
        public decimal HighPrice { get { return PriceAt(Count - 1); } }

        public decimal Poc { get { return PocIndex < 0 ? 0m : PriceAt(PocIndex); } }
        public decimal ValueAreaHigh { get { return VahIndex < 0 ? 0m : PriceAt(VahIndex); } }
        public decimal ValueAreaLow { get { return ValIndex < 0 ? 0m : PriceAt(ValIndex); } }

        public int IndexOfPrice(decimal price)
        {
            if (TickSize <= 0m) return -1;
            return (int)Math.Round((price - LowPrice) / TickSize, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Accumulates traded volume by price, then hands back a dense <see cref="Profile"/>.
    ///
    /// Built from the footprint ATAS already carries per bar, which is finer than the
    /// one-minute bars the original script reconstructs from -- there is no resampling step to
    /// lose, because the per-price volume is what the feed publishes.
    /// </summary>
    public sealed class ProfileBuilder
    {
        private readonly Dictionary<decimal, VolumeLevel> _levels = new Dictionary<decimal, VolumeLevel>();

        public DateTime Start;
        public DateTime End;
        public int Days;

        public int Count { get { return _levels.Count; } }

        public void Clear()
        {
            _levels.Clear();
            Days = 0;
        }

        public void Add(decimal price, decimal volume, decimal bid, decimal ask)
        {
            if (volume <= 0m && bid <= 0m && ask <= 0m) return;

            VolumeLevel level;
            _levels.TryGetValue(price, out level);

            level.Volume += volume;
            level.Bid += bid;
            level.Ask += ask;

            _levels[price] = level;
        }

        /// <summary>
        /// Fills in every tick between the lowest and highest that traded, so the ladder is
        /// dense. An over-wide profile is REFUSED rather than truncated: a truncated ladder can
        /// put the point of control at a price it never reached and look entirely reasonable.
        /// </summary>
        public Profile Build(decimal tickSize, int maxLevels)
        {
            if (tickSize <= 0m || _levels.Count == 0) return null;

            var low = decimal.MaxValue;
            var high = decimal.MinValue;

            foreach (var price in _levels.Keys)
            {
                if (price < low) low = price;
                if (price > high) high = price;
            }

            var span = (int)Math.Round((high - low) / tickSize, MidpointRounding.AwayFromZero) + 1;
            if (span <= 0 || span > maxLevels) return null;

            var profile = new Profile();
            profile.TickSize = tickSize;
            profile.LowPrice = low;
            profile.Levels = new VolumeLevel[span];
            profile.Start = Start;
            profile.End = End;
            profile.Days = Days;

            foreach (var pair in _levels)
            {
                var index = (int)Math.Round((pair.Key - low) / tickSize, MidpointRounding.AwayFromZero);
                if (index < 0 || index >= span) continue;

                var level = profile.Levels[index];
                level.Volume += pair.Value.Volume;
                level.Bid += pair.Value.Bid;
                level.Ask += pair.Value.Ask;
                profile.Levels[index] = level;
            }

            DevelopedMath.Summarise(profile);
            return profile;
        }
    }

    /// <summary>One drawn bar of the histogram: several ticks merged so the row is legible.</summary>
    public struct DisplayRow
    {
        public int FromIndex;
        public int ToIndex;

        public decimal LowPrice;
        public decimal HighPrice;
        public decimal MidPrice;

        public decimal Volume;
        public decimal Bid;
        public decimal Ask;

        public bool HasPoc;
        public bool InValueArea;
        public bool InHvn;

        public decimal Delta { get { return Ask - Bid; } }
    }

    /// <summary>
    /// A shelf of prices the period agreed on: contiguous high-volume levels merged into one
    /// band. A single high tick is a print; a band is a price area the auction kept coming back
    /// to, and that is what is worth carrying forward onto today's chart.
    /// </summary>
    public struct HvnZone
    {
        public int FromIndex;
        public int ToIndex;

        public decimal LowPrice;
        public decimal HighPrice;
        public decimal PeakPrice;

        public decimal Volume;        // everything inside the band
        public decimal PeakVolume;    // the busiest single tick in it

        public int Ticks { get { return ToIndex - FromIndex + 1; } }
    }

    public static class DevelopedMath
    {
        /// <summary>
        /// Totals, and the point of control. A tie keeps the LOWER price: a point of control
        /// that flickers between two equal levels as ticks land is worse than a choice that is
        /// arbitrary but fixed.
        /// </summary>
        public static void Summarise(Profile profile)
        {
            if (profile == null || profile.Levels == null) return;

            profile.TotalVolume = 0m;
            profile.TotalDelta = 0m;
            profile.MaxLevelVolume = 0m;
            profile.PocIndex = -1;

            for (var i = 0; i < profile.Levels.Length; i++)
            {
                var level = profile.Levels[i];

                profile.TotalVolume += level.Volume;
                profile.TotalDelta += level.Delta;

                if (level.Volume > profile.MaxLevelVolume)
                {
                    profile.MaxLevelVolume = level.Volume;
                    profile.PocIndex = i;
                }
            }
        }

        /// <summary>
        /// Sums several profiles onto one ladder. Volume at a price is additive over time, so a
        /// week is exactly the sum of its days and nothing is approximated here -- which is why
        /// the day is the unit of account: five days, twenty days and a month all reuse the
        /// same cached day profiles instead of rescanning the bars.
        ///
        /// (Note the distinction from merging PRICES for display, further down. Merging time is
        /// exact; merging price rows is a display convenience that must never feed analysis.)
        /// </summary>
        public static Profile Merge(IList<Profile> parts, decimal tickSize, int maxLevels)
        {
            if (parts == null || parts.Count == 0 || tickSize <= 0m) return null;

            var low = decimal.MaxValue;
            var high = decimal.MinValue;
            var found = false;

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.Count == 0) continue;

                if (part.LowPrice < low) low = part.LowPrice;
                if (part.HighPrice > high) high = part.HighPrice;
                found = true;
            }

            if (!found) return null;

            var span = (int)Math.Round((high - low) / tickSize, MidpointRounding.AwayFromZero) + 1;
            if (span <= 0 || span > maxLevels) return null;

            var merged = new Profile();
            merged.TickSize = tickSize;
            merged.LowPrice = low;
            merged.Levels = new VolumeLevel[span];
            merged.Start = DateTime.MaxValue;
            merged.End = DateTime.MinValue;

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part == null || part.Count == 0) continue;

                var offset = (int)Math.Round((part.LowPrice - low) / tickSize, MidpointRounding.AwayFromZero);

                for (var j = 0; j < part.Levels.Length; j++)
                {
                    var at = offset + j;
                    if (at < 0 || at >= span) continue;

                    var level = merged.Levels[at];
                    level.Volume += part.Levels[j].Volume;
                    level.Bid += part.Levels[j].Bid;
                    level.Ask += part.Levels[j].Ask;
                    merged.Levels[at] = level;
                }

                if (part.Start < merged.Start) merged.Start = part.Start;
                if (part.End > merged.End) merged.End = part.End;
                if (!part.Complete) merged.Complete = false;

                merged.Days += part.Days > 0 ? part.Days : 1;
            }

            Summarise(merged);
            return merged;
        }

        /// <summary>
        /// The value area: the band around the point of control holding the given share of the
        /// period's volume, grown a pair of ticks at a time toward whichever side is heavier.
        /// This is the standard construction, so the numbers line up with everyone else's.
        /// </summary>
        public static void ComputeValueArea(Profile profile, decimal percent)
        {
            if (profile == null || profile.Levels == null) return;
            if (profile.PocIndex < 0 || profile.TotalVolume <= 0m) return;

            if (percent <= 0m) percent = 0m;
            if (percent > 100m) percent = 100m;

            var target = profile.TotalVolume * percent / 100m;
            var levels = profile.Levels;

            var lo = profile.PocIndex;
            var hi = profile.PocIndex;
            var inside = levels[profile.PocIndex].Volume;

            while (inside < target && (lo > 0 || hi < levels.Length - 1))
            {
                var up = 0m;
                var upTo = hi;
                for (var i = 1; i <= 2 && hi + i < levels.Length; i++)
                {
                    up += levels[hi + i].Volume;
                    upTo = hi + i;
                }

                var down = 0m;
                var downTo = lo;
                for (var i = 1; i <= 2 && lo - i >= 0; i++)
                {
                    down += levels[lo - i].Volume;
                    downTo = lo - i;
                }

                var canUp = upTo != hi;
                var canDown = downTo != lo;

                if (canUp && (!canDown || up >= down))
                {
                    inside += up;
                    hi = upTo;
                }
                else if (canDown)
                {
                    inside += down;
                    lo = downTo;
                }
                else
                {
                    break;
                }
            }

            profile.ValIndex = lo;
            profile.VahIndex = hi;
        }

        /// <summary>
        /// High volume nodes as ZONES rather than single ticks.
        ///
        /// Every level carrying at least <paramref name="percentOfMax"/> of the busiest one is a
        /// candidate; candidates that sit within <paramref name="gapTicks"/> of each other are
        /// one shelf, because a single thin tick inside a shelf is noise and splitting on it
        /// reports two levels where the market only built one. Bands thinner than
        /// <paramref name="minTicks"/> are dropped for the same reason, and only the heaviest
        /// <paramref name="maxZones"/> survive -- marking every shelf is marking none.
        ///
        /// Returned in price order, lowest first, so the draw order is stable frame to frame.
        /// </summary>
        public static HvnZone[] FindHvnZones(Profile profile, decimal percentOfMax,
                                             int minTicks, int gapTicks, int maxZones)
        {
            if (profile == null || profile.Levels == null) return new HvnZone[0];
            if (profile.MaxLevelVolume <= 0m || maxZones <= 0) return new HvnZone[0];

            if (minTicks < 1) minTicks = 1;
            if (gapTicks < 0) gapTicks = 0;

            var floor = profile.MaxLevelVolume * percentOfMax / 100m;
            var levels = profile.Levels;
            var zones = new List<HvnZone>();

            var from = -1;

            for (var i = 0; i < levels.Length; i++)
            {
                var qualifies = levels[i].Volume >= floor && levels[i].Volume > 0m;

                if (qualifies)
                {
                    if (from < 0) from = i;
                    continue;
                }

                if (from < 0) continue;

                // The run ended at i-1. Look ahead: if another qualifying level starts within
                // the gap allowance, the shelf continues through the dip rather than ending.
                var bridged = false;
                for (var j = i + 1; j <= i + gapTicks && j < levels.Length; j++)
                {
                    if (levels[j].Volume < floor || levels[j].Volume <= 0m) continue;
                    bridged = true;
                    break;
                }

                if (bridged) continue;

                zones.Add(Zone(profile, from, i - 1));
                from = -1;
            }

            if (from >= 0) zones.Add(Zone(profile, from, levels.Length - 1));

            // Thin bands are prints, not shelves.
            for (var i = zones.Count - 1; i >= 0; i--)
            {
                if (zones[i].Ticks < minTicks) zones.RemoveAt(i);
            }

            if (zones.Count > maxZones)
            {
                zones.Sort(delegate (HvnZone a, HvnZone b) { return b.Volume.CompareTo(a.Volume); });
                zones.RemoveRange(maxZones, zones.Count - maxZones);
            }

            zones.Sort(delegate (HvnZone a, HvnZone b) { return a.FromIndex.CompareTo(b.FromIndex); });
            return zones.ToArray();
        }

        private static HvnZone Zone(Profile profile, int from, int to)
        {
            var zone = new HvnZone();
            zone.FromIndex = from;
            zone.ToIndex = to;
            zone.LowPrice = profile.PriceAt(from);
            zone.HighPrice = profile.PriceAt(to);
            zone.PeakPrice = profile.PriceAt(from);

            for (var i = from; i <= to; i++)
            {
                zone.Volume += profile.Levels[i].Volume;

                // Ties keep the lower price, for the same reason the point of control does.
                if (profile.Levels[i].Volume <= zone.PeakVolume) continue;

                zone.PeakVolume = profile.Levels[i].Volume;
                zone.PeakPrice = profile.PriceAt(i);
            }

            return zone;
        }

        /// <summary>
        /// Merges ticks into drawable rows. DISPLAY ONLY -- the point of control, value area and
        /// high volume nodes are all computed on the full-resolution ladder above and passed in
        /// here as flags, so what is drawn can be coarse without the analysis being coarse.
        /// </summary>
        public static DisplayRow[] BuildView(Profile profile, int ticksPerRow, HvnZone[] zones)
        {
            if (profile == null || profile.Levels == null) return new DisplayRow[0];
            if (ticksPerRow < 1) ticksPerRow = 1;

            var count = (profile.Levels.Length + ticksPerRow - 1) / ticksPerRow;
            var rows = new DisplayRow[count];

            for (var r = 0; r < count; r++)
            {
                var from = r * ticksPerRow;
                var to = from + ticksPerRow - 1;
                if (to > profile.Levels.Length - 1) to = profile.Levels.Length - 1;

                var row = new DisplayRow();
                row.FromIndex = from;
                row.ToIndex = to;
                row.LowPrice = profile.PriceAt(from);
                row.HighPrice = profile.PriceAt(to);
                row.MidPrice = (row.LowPrice + row.HighPrice) / 2m;

                for (var i = from; i <= to; i++)
                {
                    row.Volume += profile.Levels[i].Volume;
                    row.Bid += profile.Levels[i].Bid;
                    row.Ask += profile.Levels[i].Ask;

                    if (i == profile.PocIndex) row.HasPoc = true;
                    if (profile.ValIndex >= 0 && i >= profile.ValIndex && i <= profile.VahIndex)
                        row.InValueArea = true;
                }

                if (zones != null)
                {
                    for (var z = 0; z < zones.Length; z++)
                    {
                        if (zones[z].ToIndex < from || zones[z].FromIndex > to) continue;
                        row.InHvn = true;
                        break;
                    }
                }

                rows[r] = row;
            }

            return rows;
        }

        /// <summary>How many ticks to fold into one drawn row so a row is at least readable.</summary>
        public static int TicksPerRow(decimal pixelsPerTick, int minRowPixels)
        {
            if (minRowPixels < 1) minRowPixels = 1;
            if (pixelsPerTick <= 0m) return 1;

            var ticks = (int)Math.Ceiling(minRowPixels / pixelsPerTick);
            return ticks < 1 ? 1 : ticks;
        }

        public static decimal MaxRowVolume(DisplayRow[] rows)
        {
            var max = 0m;
            if (rows == null) return max;

            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i].Volume > max) max = rows[i].Volume;
            }

            return max;
        }

        public static decimal Normalise(decimal value, decimal reference)
        {
            if (reference <= 0m) return 0m;

            var t = value / reference;
            if (t < 0m) t = 0m;
            if (t > 1m) t = 1m;

            return t;
        }

        /// <summary>Volume in as few characters as it can be read in.</summary>
        public static string Compact(decimal value)
        {
            var negative = value < 0m;
            if (negative) value = -value;

            string text;

            if (value >= 1000000m) text = Math.Round(value / 1000000m, 1).ToString("0.#") + "M";
            else if (value >= 1000m) text = Math.Round(value / 1000m, 1).ToString("0.#") + "k";
            else text = Math.Round(value, 0).ToString("0");

            return negative ? "-" + text : text;
        }

        public static string Signed(decimal value)
        {
            return (value > 0m ? "+" : "") + Compact(value);
        }
    }
}
