using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OceansProfile
{
    /// <summary>Which side aggressed.</summary>
    public enum Side
    {
        None,
        Buy,
        Sell
    }

    /// <summary>
    /// Traded volume at one price. Every field here printed on the tape -- nothing is inferred,
    /// nothing is a resting order that could still be pulled.
    /// </summary>
    public struct VolumeLevel
    {
        /// <summary>Contracts traded at this price.</summary>
        public decimal Volume;

        /// <summary>Traded into the bid: the seller was the aggressor.</summary>
        public decimal Bid;

        /// <summary>Traded into the ask: the buyer was the aggressor.</summary>
        public decimal Ask;

        /// <summary>Number of separate trades.</summary>
        public int Trades;

        /// <summary>Buyers minus sellers by aggression.</summary>
        public decimal Delta { get { return Ask - Bid; } }

        /// <summary>
        /// How one-sided the aggression was, 0 to 1. A big level with a ratio near zero is
        /// two-sided trade that went nowhere -- someone passive absorbed it.
        /// </summary>
        public decimal Lopsidedness
        {
            get
            {
                if (Volume <= 0m) return 0m;

                var delta = Delta;
                if (delta < 0m) delta = -delta;

                var ratio = delta / Volume;
                return ratio > 1m ? 1m : ratio;
            }
        }
    }

    /// <summary>A price level flagged as imbalanced against its diagonal neighbour.</summary>
    public struct Imbalance
    {
        public int Index;
        public Side Side;
        public decimal Ratio;
    }

    /// <summary>Consecutive imbalances on the same side.</summary>
    public struct Stack
    {
        public int From;
        public int To;
        public Side Side;

        public int Length { get { return To - From + 1; } }
    }

    /// <summary>
    /// One higher-timeframe period's volume profile, held as a DENSE array of ticks from Low to
    /// High. Density matters: the footprint imbalance rules compare a price against the tick
    /// diagonally below it, and a tick that never traded is a real zero, not a missing row.
    /// </summary>
    public sealed class Profile
    {
        public decimal TickSize;
        public decimal LowPrice;      // price of Levels[0]
        public VolumeLevel[] Levels;

        public decimal TotalVolume;
        public decimal TotalDelta;
        public decimal MaxLevelVolume;

        public int PocIndex = -1;
        public int VahIndex = -1;
        public int ValIndex = -1;

        public DateTime Start;
        public DateTime End;
        public int FirstBar = -1;
        public int LastBar = -1;

        public int Count { get { return Levels == null ? 0 : Levels.Length; } }

        public decimal PriceAt(int index) { return LowPrice + TickSize * index; }

        public decimal Poc { get { return PocIndex < 0 ? 0m : PriceAt(PocIndex); } }
        public decimal ValueAreaHigh { get { return VahIndex < 0 ? 0m : PriceAt(VahIndex); } }
        public decimal ValueAreaLow { get { return ValIndex < 0 ? 0m : PriceAt(ValIndex); } }

        /// <summary>
        /// True when the extreme traded on both sides, so the auction never found a price
        /// nobody would take. Standard "unfinished business" -- price tends to come back for it.
        /// </summary>
        public bool UnfinishedHigh
        {
            get
            {
                if (Levels == null || Levels.Length == 0) return false;

                var top = Levels[Levels.Length - 1];
                return top.Bid > 0m && top.Ask > 0m;
            }
        }

        public bool UnfinishedLow
        {
            get
            {
                if (Levels == null || Levels.Length == 0) return false;

                var bottom = Levels[0];
                return bottom.Bid > 0m && bottom.Ask > 0m;
            }
        }
    }

    /// <summary>
    /// Accumulates traded volume by price, then lays it out as a dense profile. Kept separate
    /// from <see cref="Profile"/> so the indicator can add bars one at a time and only pay for
    /// the dense array once, when the period is actually drawn.
    /// </summary>
    public sealed class ProfileBuilder
    {
        private readonly Dictionary<decimal, VolumeLevel> _levels = new Dictionary<decimal, VolumeLevel>();

        public DateTime Start;
        public DateTime End;
        public int FirstBar = -1;
        public int LastBar = -1;

        public int Count { get { return _levels.Count; } }

        public void Clear()
        {
            _levels.Clear();
            FirstBar = -1;
            LastBar = -1;
        }

        public void Add(decimal price, decimal volume, decimal bid, decimal ask, int trades)
        {
            if (volume <= 0m && bid <= 0m && ask <= 0m) return;

            VolumeLevel level;
            _levels.TryGetValue(price, out level);

            level.Volume += volume;
            level.Bid += bid;
            level.Ask += ask;
            level.Trades += trades;

            _levels[price] = level;
        }

        public void NoteBar(int bar)
        {
            if (FirstBar < 0 || bar < FirstBar) FirstBar = bar;
            if (LastBar < 0 || bar > LastBar) LastBar = bar;
        }

        /// <summary>
        /// Lays the accumulated levels out densely. Returns null when there is nothing to draw,
        /// or when the range is wider than maxLevels ticks -- a silently truncated profile would
        /// put the POC in the wrong place, so it declines instead.
        /// </summary>
        public Profile Build(decimal tickSize, int maxLevels)
        {
            if (tickSize <= 0m || _levels.Count == 0) return null;

            var low = decimal.MaxValue;
            var high = decimal.MinValue;

            foreach (var pair in _levels)
            {
                if (pair.Key < low) low = pair.Key;
                if (pair.Key > high) high = pair.Key;
            }

            var span = (int)Math.Round((high - low) / tickSize, MidpointRounding.AwayFromZero);
            if (span < 0) return null;

            var count = span + 1;
            if (maxLevels > 0 && count > maxLevels) return null;

            var profile = new Profile();
            profile.TickSize = tickSize;
            profile.LowPrice = low;
            profile.Levels = new VolumeLevel[count];
            profile.Start = Start;
            profile.End = End;
            profile.FirstBar = FirstBar;
            profile.LastBar = LastBar;

            foreach (var pair in _levels)
            {
                var index = (int)Math.Round((pair.Key - low) / tickSize, MidpointRounding.AwayFromZero);
                if (index < 0 || index >= count) continue;

                profile.Levels[index] = pair.Value;
            }

            Summarise(profile);
            return profile;
        }

        private static void Summarise(Profile profile)
        {
            var total = 0m;
            var delta = 0m;
            var max = 0m;
            var poc = -1;

            for (var i = 0; i < profile.Levels.Length; i++)
            {
                var level = profile.Levels[i];
                total += level.Volume;
                delta += level.Delta;

                // Ties keep the lower price. Arbitrary, but fixed -- a POC that flickers between
                // two equal levels as ticks land is worse than one that always picks the same.
                if (level.Volume > max)
                {
                    max = level.Volume;
                    poc = i;
                }
            }

            profile.TotalVolume = total;
            profile.TotalDelta = delta;
            profile.MaxLevelVolume = max;
            profile.PocIndex = poc;
        }
    }

    /// <summary>
    /// One drawn row: one or more ticks of a profile collapsed to fit the screen.
    ///
    /// Merging happens for DISPLAY ONLY, after the analysis has run. The imbalance rules compare
    /// a price with the tick diagonally below it, so running them on pre-merged rows would be
    /// answering a different question and calling it a footprint.
    /// </summary>
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
        public bool Absorbed;
        public bool HighNode;
        public bool LowNode;
        public Side Stacked;

        public decimal Delta { get { return Ask - Bid; } }

        /// <summary>-1 all sellers, 0 balanced, +1 all buyers.</summary>
        public decimal Lean
        {
            get
            {
                if (Volume <= 0m) return 0m;

                var lean = Delta / Volume;
                if (lean > 1m) return 1m;
                return lean < -1m ? -1m : lean;
            }
        }
    }

    public static class ProfileMath
    {
        /// <summary>
        /// Collapses a profile to rows that fit the screen, carrying the analysis flags up with
        /// it: a row is flagged if any tick inside it was. Volumes sum, which is exact.
        /// </summary>
        public static DisplayRow[] BuildView(Profile profile, int ticksPerRow,
                                             bool[] absorbed, bool[] highNodes, bool[] lowNodes,
                                             Side[] stacked)
        {
            if (profile == null || profile.Levels == null) return new DisplayRow[0];
            if (ticksPerRow < 1) ticksPerRow = 1;

            var levels = profile.Levels;
            var count = (levels.Length + ticksPerRow - 1) / ticksPerRow;
            var rows = new DisplayRow[count];

            for (var r = 0; r < count; r++)
            {
                var from = r * ticksPerRow;
                var to = from + ticksPerRow - 1;
                if (to > levels.Length - 1) to = levels.Length - 1;

                rows[r].FromIndex = from;
                rows[r].ToIndex = to;
                rows[r].LowPrice = profile.PriceAt(from);
                rows[r].HighPrice = profile.PriceAt(to);
                rows[r].MidPrice = (rows[r].LowPrice + rows[r].HighPrice) / 2m;

                for (var i = from; i <= to; i++)
                {
                    rows[r].Volume += levels[i].Volume;
                    rows[r].Bid += levels[i].Bid;
                    rows[r].Ask += levels[i].Ask;

                    if (i == profile.PocIndex) rows[r].HasPoc = true;
                    if (absorbed != null && absorbed[i]) rows[r].Absorbed = true;
                    if (highNodes != null && highNodes[i]) rows[r].HighNode = true;
                    if (lowNodes != null && lowNodes[i]) rows[r].LowNode = true;

                    if (stacked != null && stacked[i] != Side.None && rows[r].Stacked == Side.None)
                        rows[r].Stacked = stacked[i];
                }
            }

            return rows;
        }

        /// <summary>Spreads stack runs out to a per-tick array, for merging into display rows.</summary>
        public static Side[] StackSides(Profile profile, Stack[] stacks)
        {
            if (profile == null || profile.Levels == null) return null;

            var sides = new Side[profile.Levels.Length];
            if (stacks == null) return sides;

            for (var s = 0; s < stacks.Length; s++)
            {
                for (var i = stacks[s].From; i <= stacks[s].To && i < sides.Length; i++)
                {
                    if (i >= 0) sides[i] = stacks[s].Side;
                }
            }

            return sides;
        }

        /// <summary>A price worth drawing a line at, with the numbers that earned it.</summary>
        public struct KeyLevel
        {
            public decimal Price;
            public decimal Volume;
            public decimal Delta;
            public int FromIndex;
            public int ToIndex;
        }

        /// <summary>
        /// Absorption prices, one per run of adjacent absorbed ticks rather than one per tick.
        /// Five neighbouring ticks that all absorbed are one shelf, and five lines across the
        /// chart for one shelf is how a useful mark becomes noise.
        /// </summary>
        public static KeyLevel[] AbsorptionLevels(Profile profile, bool[] absorbed)
        {
            var result = new List<KeyLevel>();
            if (profile == null || profile.Levels == null || absorbed == null) return result.ToArray();

            var i = 0;
            while (i < absorbed.Length)
            {
                if (!absorbed[i]) { i++; continue; }

                var from = i;
                while (i < absorbed.Length && absorbed[i]) i++;
                var to = i - 1;

                // The run is represented by its heaviest tick: that is the price the size
                // actually sat at, not the midpoint of a smear.
                var best = from;
                var volume = 0m;
                var delta = 0m;

                for (var j = from; j <= to; j++)
                {
                    volume += profile.Levels[j].Volume;
                    delta += profile.Levels[j].Delta;
                    if (profile.Levels[j].Volume > profile.Levels[best].Volume) best = j;
                }

                var level = new KeyLevel();
                level.Price = profile.PriceAt(best);
                level.Volume = volume;
                level.Delta = delta;
                level.FromIndex = from;
                level.ToIndex = to;
                result.Add(level);
            }

            return result.ToArray();
        }

        /// <summary>
        /// The most one-sided level in the profile, ignoring levels too small to mean anything.
        ///
        /// This is what the colour scale is measured against. Absolute lean is useless on a
        /// profile merged over hours: price oscillates through every price, buying and selling
        /// net off, and every level lands near balanced. Scaled against the profile's own spread
        /// the hue carries information again.
        /// </summary>
        public static decimal MaxLean(DisplayRow[] rows, decimal volumeFloor)
        {
            var max = 0m;
            if (rows == null) return max;

            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i].Volume < volumeFloor) continue;

                var lean = rows[i].Lean;
                if (lean < 0m) lean = -lean;
                if (lean > max) max = lean;
            }

            return max;
        }

        /// <summary>The biggest single-level delta, either way, for scaling the delta overlay.</summary>
        public static decimal MaxAbsDelta(DisplayRow[] rows)
        {
            var max = 0m;
            if (rows == null) return max;

            for (var i = 0; i < rows.Length; i++)
            {
                var delta = rows[i].Delta;
                if (delta < 0m) delta = -delta;
                if (delta > max) max = delta;
            }

            return max;
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

        /// <summary>How many ticks share a row for the row to clear a minimum pixel height.</summary>
        public static int TicksPerRow(decimal pixelsPerTick, int minRowPixels)
        {
            if (minRowPixels < 1) minRowPixels = 1;
            if (pixelsPerTick <= 0m) return 1;

            var ticks = (int)Math.Ceiling(minRowPixels / pixelsPerTick);
            return ticks < 1 ? 1 : ticks;
        }

        /// <summary>
        /// The conventional volume value area: start at the point of control and keep taking the
        /// heavier of the two pairs of levels above and below until the requested share of the
        /// volume is inside. The result is always contiguous and always contains the POC.
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
        /// High volume nodes: local peaks carrying at least the given share of the busiest
        /// level. These are the shelves price accepted and tends to return to.
        /// </summary>
        public static bool[] FindHighVolumeNodes(Profile profile, decimal percentOfMax)
        {
            var flags = NewFlags(profile);
            if (flags == null || profile.MaxLevelVolume <= 0m) return flags;

            var floor = profile.MaxLevelVolume * percentOfMax / 100m;
            var levels = profile.Levels;

            for (var i = 0; i < levels.Length; i++)
            {
                if (levels[i].Volume < floor) continue;
                if (levels[i].Volume <= 0m) continue;

                var left = i > 0 ? levels[i - 1].Volume : 0m;
                var right = i < levels.Length - 1 ? levels[i + 1].Volume : 0m;

                if (levels[i].Volume >= left && levels[i].Volume >= right) flags[i] = true;
            }

            return flags;
        }

        /// <summary>
        /// Low volume nodes: thin local troughs INSIDE the traded range. Price crossed them
        /// quickly and tends to cross them quickly again. The empty air beyond the extremes is
        /// not a node, so the scan stops at the first and last level that traded.
        /// </summary>
        public static bool[] FindLowVolumeNodes(Profile profile, decimal percentOfMax)
        {
            var flags = NewFlags(profile);
            if (flags == null || profile.MaxLevelVolume <= 0m) return flags;

            var ceiling = profile.MaxLevelVolume * percentOfMax / 100m;
            var levels = profile.Levels;

            for (var i = 1; i < levels.Length - 1; i++)
            {
                if (levels[i].Volume > ceiling) continue;

                var left = levels[i - 1].Volume;
                var right = levels[i + 1].Volume;

                if (levels[i].Volume <= left && levels[i].Volume <= right && (left > 0m || right > 0m))
                    flags[i] = true;
            }

            return flags;
        }

        /// <summary>
        /// Absorption: a level that traded heavily while the aggression stayed near balanced.
        /// Size crossed, and price did not go anywhere, which means something passive was on the
        /// other side of it and got filled.
        ///
        /// This is the honest version of "where are the big orders": it is evidence of a large
        /// resting order that actually TRADED, rather than a displayed one that may be pulled.
        /// </summary>
        public static bool[] FindAbsorption(Profile profile, decimal volumePercentOfMax,
                                            decimal maxLopsidedness)
        {
            var flags = NewFlags(profile);
            if (flags == null || profile.MaxLevelVolume <= 0m) return flags;

            var floor = profile.MaxLevelVolume * volumePercentOfMax / 100m;
            var levels = profile.Levels;

            for (var i = 0; i < levels.Length; i++)
            {
                if (levels[i].Volume <= 0m) continue;
                if (levels[i].Volume < floor) continue;
                if (levels[i].Lopsidedness > maxLopsidedness) continue;

                flags[i] = true;
            }

            return flags;
        }

        /// <summary>
        /// Footprint diagonal imbalances. A buy imbalance compares the buyers at a price with
        /// the sellers one tick BELOW it, because those two are the counterparties that met.
        /// A sell imbalance compares sellers at a price with buyers one tick above.
        ///
        /// minVolume keeps three-lot levels off the chart: 6 against 1 is the same ratio as
        /// 600 against 100 and means nothing.
        /// </summary>
        public static Imbalance[] FindImbalances(Profile profile, decimal ratio, decimal minVolume)
        {
            var result = new List<Imbalance>();
            if (profile == null || profile.Levels == null) return result.ToArray();
            if (ratio <= 0m) return result.ToArray();

            var levels = profile.Levels;

            for (var i = 0; i < levels.Length; i++)
            {
                var buyers = levels[i].Ask;
                if (i > 0 && buyers >= minVolume)
                {
                    var sellersBelow = levels[i - 1].Bid;
                    if (Beats(buyers, sellersBelow, ratio))
                    {
                        var imbalance = new Imbalance();
                        imbalance.Index = i;
                        imbalance.Side = Side.Buy;
                        imbalance.Ratio = sellersBelow <= 0m ? 0m : buyers / sellersBelow;
                        result.Add(imbalance);
                        continue;
                    }
                }

                var sellers = levels[i].Bid;
                if (i < levels.Length - 1 && sellers >= minVolume)
                {
                    var buyersAbove = levels[i + 1].Ask;
                    if (Beats(sellers, buyersAbove, ratio))
                    {
                        var imbalance = new Imbalance();
                        imbalance.Index = i;
                        imbalance.Side = Side.Sell;
                        imbalance.Ratio = buyersAbove <= 0m ? 0m : sellers / buyersAbove;
                        result.Add(imbalance);
                    }
                }
            }

            return result.ToArray();
        }

        private static bool Beats(decimal value, decimal against, decimal ratio)
        {
            if (value <= 0m) return false;
            if (against <= 0m) return true;   // nothing on the other side at all

            return value >= against * ratio;
        }

        /// <summary>
        /// Runs of consecutive same-side imbalances. One imbalance is noise; three stacked is
        /// the signal traders actually act on, and the run's edges are the levels that matter.
        /// </summary>
        public static Stack[] FindStacks(Imbalance[] imbalances, int minRun)
        {
            var result = new List<Stack>();
            if (imbalances == null || imbalances.Length == 0) return result.ToArray();
            if (minRun < 1) minRun = 1;

            var start = 0;

            for (var i = 1; i <= imbalances.Length; i++)
            {
                var breaks = i == imbalances.Length
                          || imbalances[i].Side != imbalances[start].Side
                          || imbalances[i].Index != imbalances[i - 1].Index + 1;

                if (!breaks) continue;

                var length = i - start;
                if (length >= minRun)
                {
                    var stack = new Stack();
                    stack.From = imbalances[start].Index;
                    stack.To = imbalances[i - 1].Index;
                    stack.Side = imbalances[start].Side;
                    result.Add(stack);
                }

                start = i;
            }

            return result.ToArray();
        }

        private static bool[] NewFlags(Profile profile)
        {
            if (profile == null || profile.Levels == null) return null;
            return new bool[profile.Levels.Length];
        }

        /// <summary>
        /// Short enough for a narrow column. Under 1000 stays exact: on a single price level
        /// that is most of them, and "0.1k" would hide the gap between 60 and 140.
        /// </summary>
        public static string Compact(decimal value)
        {
            var negative = value < 0m;
            if (negative) value = -value;

            var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            string text;

            if (rounded < 1000m)
            {
                text = ((long)rounded).ToString(CultureInfo.InvariantCulture);
            }
            else if (rounded < 1000000m)
            {
                var k = rounded / 1000m;
                text = k < 100m
                     ? k.ToString("0.#", CultureInfo.InvariantCulture) + "k"
                     : Math.Round(k, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "k";
            }
            else
            {
                text = (rounded / 1000000m).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            }

            return negative ? "-" + text : text;
        }

        /// <summary>Size as a fraction of a reference, clamped. A zero reference gives zero.</summary>
        public static decimal Normalise(decimal value, decimal reference)
        {
            if (reference <= 0m || value <= 0m) return 0m;

            var t = value / reference;
            return t > 1m ? 1m : t;
        }
    }
}
