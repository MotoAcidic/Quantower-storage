using System;
using System.Collections.Generic;

namespace OceansEffort
{
    /// <summary>Traded volume at one price across a range of bars.</summary>
    public struct ProfileLevel
    {
        public decimal Volume;
        public decimal Bid;
        public decimal Ask;

        public decimal Delta { get { return Ask - Bid; } }

        /// <summary>
        /// How one-sided the aggression was, 0 to 1. A heavy level near zero is size that changed
        /// hands and went nowhere: something passive was on the other side and got filled.
        /// </summary>
        public decimal Lean
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

    /// <summary>
    /// A volume profile over a range of bars, held as a DENSE array of ticks from low to high.
    /// Density matters everywhere a level is compared with its neighbour: a tick that never traded
    /// is a real zero, and a sparse list would put the price above it next door.
    /// </summary>
    public sealed class RangeProfile
    {
        public decimal TickSize;
        public decimal LowPrice;
        public ProfileLevel[] Levels;

        public decimal TotalVolume;
        public decimal TotalDelta;
        public decimal MaxLevelVolume;

        public int PocIndex = -1;
        public int VahIndex = -1;
        public int ValIndex = -1;

        public int FirstBar = -1;
        public int LastBar = -1;

        /// <summary>
        /// The first bar each price traded on, parallel to <see cref="Levels"/>. A cluster is
        /// drawn from where it printed, so it has to remember when that was.
        /// </summary>
        public int[] FirstBarAt;

        public int Count { get { return Levels == null ? 0 : Levels.Length; } }

        public decimal PriceAt(int index) { return LowPrice + TickSize * index; }

        public decimal HighPrice { get { return Count == 0 ? 0m : PriceAt(Count - 1); } }

        public decimal Poc { get { return PocIndex < 0 ? 0m : PriceAt(PocIndex); } }
        public decimal ValueHigh { get { return VahIndex < 0 ? 0m : PriceAt(VahIndex); } }
        public decimal ValueLow { get { return ValIndex < 0 ? 0m : PriceAt(ValIndex); } }
    }

    /// <summary>Accumulates traded volume by price, then lays it out densely once.</summary>
    public sealed class RangeProfileBuilder
    {
        private readonly Dictionary<decimal, ProfileLevel> _levels = new Dictionary<decimal, ProfileLevel>();
        private readonly Dictionary<decimal, int> _firstBar = new Dictionary<decimal, int>();
        private int _bar = -1;

        public int FirstBar = -1;
        public int LastBar = -1;

        public int Count { get { return _levels.Count; } }

        public void Clear()
        {
            _levels.Clear();
            _firstBar.Clear();
            _bar = -1;
            FirstBar = -1;
            LastBar = -1;
        }

        public void Add(decimal price, decimal volume, decimal bid, decimal ask)
        {
            if (volume <= 0m && bid <= 0m && ask <= 0m) return;

            if (_bar >= 0 && !_firstBar.ContainsKey(price)) _firstBar[price] = _bar;

            ProfileLevel level;
            _levels.TryGetValue(price, out level);

            level.Volume += volume;
            level.Bid += bid;
            level.Ask += ask;

            _levels[price] = level;
        }

        /// <summary>Call before adding a bar's levels: prices then remember which bar they came from.</summary>
        public void NoteBar(int bar)
        {
            _bar = bar;

            if (FirstBar < 0 || bar < FirstBar) FirstBar = bar;
            if (LastBar < 0 || bar > LastBar) LastBar = bar;
        }

        /// <summary>
        /// Lays the levels out densely. Returns null when there is nothing, or when the range is
        /// wider than maxLevels ticks -- a silently truncated profile puts the point of control
        /// somewhere it never was and looks perfectly plausible doing it.
        /// </summary>
        public RangeProfile Build(decimal tickSize, int maxLevels)
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

            var profile = new RangeProfile();
            profile.TickSize = tickSize;
            profile.LowPrice = low;
            profile.Levels = new ProfileLevel[count];
            profile.FirstBarAt = new int[count];
            profile.FirstBar = FirstBar;
            profile.LastBar = LastBar;

            for (var i = 0; i < count; i++) profile.FirstBarAt[i] = -1;

            foreach (var pair in _levels)
            {
                var index = (int)Math.Round((pair.Key - low) / tickSize, MidpointRounding.AwayFromZero);
                if (index < 0 || index >= count) continue;

                profile.Levels[index] = pair.Value;

                int bar;
                if (_firstBar.TryGetValue(pair.Key, out bar)) profile.FirstBarAt[index] = bar;
            }

            var total = 0m;
            var delta = 0m;
            var max = 0m;
            var poc = -1;

            for (var i = 0; i < count; i++)
            {
                total += profile.Levels[i].Volume;
                delta += profile.Levels[i].Delta;

                // Ties keep the lower price. Arbitrary, but fixed -- a point of control flickering
                // between two equal levels as ticks land is worse than one that always picks the same.
                if (profile.Levels[i].Volume > max)
                {
                    max = profile.Levels[i].Volume;
                    poc = i;
                }
            }

            profile.TotalVolume = total;
            profile.TotalDelta = delta;
            profile.MaxLevelVolume = max;
            profile.PocIndex = poc;

            return profile;
        }
    }

    /// <summary>What a cluster of prices was doing.</summary>
    public enum ClusterKind
    {
        /// <summary>Heavy trade with the aggression balanced: size was filled and price stayed.</summary>
        Absorption,

        /// <summary>Heavy trade, heavily one-sided: a side drove through here.</summary>
        Aggression
    }

    /// <summary>
    /// A run of adjacent prices that behaved the same way. This is the "cluster area" the model
    /// engages from -- one price on its own is a print, a band of them is a level.
    /// </summary>
    public struct Cluster
    {
        public int From;
        public int To;

        /// <summary>The earliest bar any price in this band traded on: where the area printed.</summary>
        public int FirstBar;

        public decimal Low;
        public decimal High;

        public decimal Volume;
        public decimal Delta;

        public ClusterKind Kind;

        /// <summary>
        /// Absorption: the side that pushed and was filled into. Aggression: the side that drove.
        /// A cluster where SELLERS were absorbed is demand -- they pushed and it held.
        /// </summary>
        public Side Side;

        /// <summary>Share of the whole profile's volume that traded in this band, 0 to 1.</summary>
        public decimal Share;

        public int Ticks { get { return To - From + 1; } }

        public bool Holds(decimal price)
        {
            return price >= Low && price <= High;
        }

        /// <summary>Which way this cluster leans as a trade location: demand is bullish, supply bearish.</summary>
        public Side Favours
        {
            get
            {
                if (Kind == ClusterKind.Absorption)
                {
                    // Sellers absorbed means sellers failed, so the level is demand.
                    if (Side == Side.Sell) return Side.Buy;
                    return Side == Side.Buy ? Side.Sell : Side.None;
                }

                // Aggression clusters mark where a side actually drove, and that is the side.
                return Side;
            }
        }
    }

    /// <summary>Where in the profile a price sits.</summary>
    public enum Zone
    {
        /// <summary>Above the value area. Expensive, in the transcript's words.</summary>
        Premium,

        InValue,

        /// <summary>Below the value area. Discounted.</summary>
        Discount
    }

    /// <summary>The full answer to "where is price, in profile terms".</summary>
    public struct Location
    {
        public bool Known;

        public Zone Zone;

        /// <summary>Signed ticks from the point of control. Positive is above it.</summary>
        public decimal ToPoc;

        public bool AtValueHigh;
        public bool AtValueLow;

        public bool AtCluster;
        public Cluster Cluster;

        /// <summary>Which side this location favours, from the cluster or the value edge it sits on.</summary>
        public Side Favours;
    }

    /// <summary>Size that traded at one price inside one bar.</summary>
    public struct PricePrint
    {
        public int Bar;
        public decimal Price;
        public decimal Volume;
        public decimal Delta;
    }

    public static class RangeMath
    {
        /// <summary>
        /// The biggest prints, biggest first, cut to a count.
        ///
        /// Ranking across the whole range rather than per bar is the difference between "where is
        /// the size" and "here is a mark on every bar". A per-bar cap marks the busiest price of a
        /// quiet bar as loudly as a real one.
        /// </summary>
        public static PricePrint[] Biggest(IList<PricePrint> prints, int keep, decimal floor)
        {
            if (prints == null || prints.Count == 0 || keep <= 0) return new PricePrint[0];

            var kept = new List<PricePrint>();
            for (var i = 0; i < prints.Count; i++)
            {
                if (prints[i].Volume < floor) continue;

                kept.Add(prints[i]);
            }

            kept.Sort(delegate (PricePrint a, PricePrint b)
            {
                var byVolume = b.Volume.CompareTo(a.Volume);
                if (byVolume != 0) return byVolume;

                // Ties break on bar then price, so the same chart always marks the same prints.
                var byBar = a.Bar.CompareTo(b.Bar);
                return byBar != 0 ? byBar : a.Price.CompareTo(b.Price);
            });

            if (kept.Count > keep) kept.RemoveRange(keep, kept.Count - keep);
            return kept.ToArray();
        }

        /// <summary>
        /// The price levels carrying the most net aggression either way, biggest first. Level
        /// indices into the profile, so the caller keeps the price and the sign.
        /// </summary>
        public static int[] BiggestDelta(RangeProfile profile, int keep)
        {
            if (profile == null || profile.Levels == null || keep <= 0) return new int[0];

            var index = new List<int>();
            for (var i = 0; i < profile.Levels.Length; i++)
            {
                if (profile.Levels[i].Delta != 0m) index.Add(i);
            }

            index.Sort(delegate (int a, int b)
            {
                var da = Math.Abs(profile.Levels[a].Delta);
                var db = Math.Abs(profile.Levels[b].Delta);

                var bySize = db.CompareTo(da);
                return bySize != 0 ? bySize : a.CompareTo(b);
            });

            if (index.Count > keep) index.RemoveRange(keep, index.Count - keep);
            return index.ToArray();
        }

        /// <summary>
        /// The conventional volume value area: from the point of control, keep taking the heavier
        /// of the two pairs above and below until the requested share of volume is inside.
        /// </summary>
        public static void ComputeValueArea(RangeProfile profile, decimal percent)
        {
            if (profile == null || profile.Levels == null) return;
            if (profile.PocIndex < 0 || profile.TotalVolume <= 0m) return;

            if (percent <= 0m) percent = 0m;
            if (percent > 100m) percent = 100m;

            var levels = profile.Levels;
            var target = profile.TotalVolume * percent / 100m;

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
        /// Finds the cluster areas: runs of adjacent heavy prices that behaved the same way.
        ///
        /// A level counts as heavy against the BUSIEST level in the profile rather than an
        /// absolute size, so the same settings work on a quiet hour and a violent one. Runs
        /// shorter than minTicks are dropped: one heavy price is a print, a band is a level.
        /// </summary>
        public static Cluster[] FindClusters(RangeProfile profile, decimal heavyPercentOfMax,
                                             decimal balancedLean, decimal drivenLean, int minTicks)
        {
            var found = new List<Cluster>();
            if (profile == null || profile.Levels == null || profile.MaxLevelVolume <= 0m) return found.ToArray();
            if (minTicks < 1) minTicks = 1;

            var levels = profile.Levels;
            var floor = profile.MaxLevelVolume * heavyPercentOfMax / 100m;

            var runFrom = -1;
            var kind = ClusterKind.Absorption;
            var side = Side.None;

            for (var i = 0; i <= levels.Length; i++)
            {
                var thisKind = ClusterKind.Absorption;
                var thisSide = Side.None;
                var counts = false;

                if (i < levels.Length && levels[i].Volume >= floor && levels[i].Volume > 0m)
                {
                    var lean = levels[i].Lean;
                    var delta = levels[i].Delta;

                    if (lean <= balancedLean)
                    {
                        counts = true;
                        thisKind = ClusterKind.Absorption;

                        // With the aggression near balanced, the side that was NET aggressive is
                        // the one that pushed and got filled into.
                        thisSide = delta > 0m ? Side.Buy : delta < 0m ? Side.Sell : Side.None;
                    }
                    else if (lean >= drivenLean)
                    {
                        counts = true;
                        thisKind = ClusterKind.Aggression;
                        thisSide = delta > 0m ? Side.Buy : Side.Sell;
                    }
                }

                var same = counts && runFrom >= 0 && thisKind == kind && thisSide == side;
                if (same) continue;

                if (runFrom >= 0)
                {
                    var to = i - 1;
                    if (to - runFrom + 1 >= minTicks)
                        found.Add(Make(profile, runFrom, to, kind, side));
                }

                runFrom = counts ? i : -1;
                kind = thisKind;
                side = thisSide;
            }

            return found.ToArray();
        }

        private static Cluster Make(RangeProfile profile, int from, int to, ClusterKind kind, Side side)
        {
            var cluster = new Cluster();
            cluster.From = from;
            cluster.To = to;
            cluster.Low = profile.PriceAt(from);
            cluster.High = profile.PriceAt(to);
            cluster.Kind = kind;
            cluster.Side = side;

            for (var i = from; i <= to; i++)
            {
                cluster.Volume += profile.Levels[i].Volume;
                cluster.Delta += profile.Levels[i].Delta;
            }

            cluster.FirstBar = -1;
            if (profile.FirstBarAt != null)
            {
                for (var i = from; i <= to && i < profile.FirstBarAt.Length; i++)
                {
                    var bar = profile.FirstBarAt[i];
                    if (bar < 0) continue;

                    if (cluster.FirstBar < 0 || bar < cluster.FirstBar) cluster.FirstBar = bar;
                }
            }

            cluster.Share = profile.TotalVolume <= 0m ? 0m : cluster.Volume / profile.TotalVolume;
            return cluster;
        }

        /// <summary>
        /// The heaviest clusters first, cut to a count. Marking every one turns signal into
        /// wallpaper, which is the failure mode a profile drawing has by default.
        /// </summary>
        public static Cluster[] Heaviest(Cluster[] clusters, int keep)
        {
            if (clusters == null) return new Cluster[0];
            if (keep <= 0 || clusters.Length <= keep) return Sorted(clusters);

            var sorted = Sorted(clusters);
            var cut = new Cluster[keep];
            Array.Copy(sorted, cut, keep);
            return cut;
        }

        private static Cluster[] Sorted(Cluster[] clusters)
        {
            var copy = new Cluster[clusters.Length];
            Array.Copy(clusters, copy, clusters.Length);

            Array.Sort(copy, delegate (Cluster a, Cluster b)
            {
                var byVolume = b.Volume.CompareTo(a.Volume);

                // Volume ties break on price so the order never depends on how the array was built.
                return byVolume != 0 ? byVolume : a.Low.CompareTo(b.Low);
            });

            return copy;
        }

        /// <summary>
        /// Where a price sits in the profile: which zone, how far from the point of control, and
        /// whether it is standing on a cluster or a value edge.
        ///
        /// This is the "buy from levels that are relevant to the order flow" test. A price in
        /// mid-air belongs to nothing and reports so -- <see cref="Location.Favours"/> stays None.
        /// </summary>
        public static Location Where(RangeProfile profile, decimal price, Cluster[] clusters, int edgeTicks)
        {
            var location = new Location();
            if (profile == null || profile.Count == 0 || profile.PocIndex < 0) return location;
            if (edgeTicks < 0) edgeTicks = 0;

            location.Known = true;

            var tick = profile.TickSize;
            var poc = profile.Poc;
            location.ToPoc = tick <= 0m ? 0m : Math.Round((price - poc) / tick);

            var hasValue = profile.VahIndex >= 0 && profile.ValIndex >= 0;
            var high = profile.ValueHigh;
            var low = profile.ValueLow;

            if (!hasValue) location.Zone = Zone.InValue;
            else if (price > high) location.Zone = Zone.Premium;
            else if (price < low) location.Zone = Zone.Discount;
            else location.Zone = Zone.InValue;

            if (hasValue)
            {
                var reach = tick * edgeTicks;
                location.AtValueHigh = Math.Abs(price - high) <= reach;
                location.AtValueLow = Math.Abs(price - low) <= reach;
            }

            if (clusters != null)
            {
                // The heaviest cluster holding this price wins; they cannot overlap, but the
                // tolerance band around them can.
                var best = -1;
                for (var i = 0; i < clusters.Length; i++)
                {
                    var reach = tick * edgeTicks;
                    if (price < clusters[i].Low - reach || price > clusters[i].High + reach) continue;

                    if (best < 0 || clusters[i].Volume > clusters[best].Volume) best = i;
                }

                if (best >= 0)
                {
                    location.AtCluster = true;
                    location.Cluster = clusters[best];
                }
            }

            location.Favours = Favours(location);
            return location;
        }

        /// <summary>
        /// Which side the location argues for. A cluster is the stronger evidence and speaks
        /// first; failing that, the edges of value are where a rotation turns.
        /// </summary>
        private static Side Favours(Location location)
        {
            if (location.AtCluster && location.Cluster.Favours != Side.None) return location.Cluster.Favours;

            if (location.AtValueLow) return Side.Buy;
            if (location.AtValueHigh) return Side.Sell;

            if (location.Zone == Zone.Discount) return Side.Buy;
            if (location.Zone == Zone.Premium) return Side.Sell;

            return Side.None;
        }

        /// <summary>
        /// Volume-weighted average price over the profile. The transcript treats it as where the
        /// players reload, and it costs nothing once the profile is built.
        /// </summary>
        public static decimal Vwap(RangeProfile profile)
        {
            if (profile == null || profile.Levels == null || profile.TotalVolume <= 0m) return 0m;

            var weighted = 0m;
            for (var i = 0; i < profile.Levels.Length; i++)
            {
                if (profile.Levels[i].Volume <= 0m) continue;

                weighted += profile.PriceAt(i) * profile.Levels[i].Volume;
            }

            return weighted / profile.TotalVolume;
        }

        /// <summary>
        /// How many ticks share a drawn row so the profile fits whatever the chart is doing.
        /// This is what makes one setup work at every zoom instead of being re-fitted by hand.
        /// </summary>
        public static int TicksPerRow(decimal pixelsPerTick, int minRowPixels)
        {
            if (pixelsPerTick <= 0m) return 1;
            if (minRowPixels < 1) minRowPixels = 1;

            var perRow = (int)Math.Ceiling(minRowPixels / pixelsPerTick);
            return perRow < 1 ? 1 : perRow;
        }
    }
}
