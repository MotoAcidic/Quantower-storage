using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>Which measure a profile's point of control and value area are cut from.</summary>
    public enum ProfileBasis
    {
        /// <summary>Contracts traded at the price. What changed hands.</summary>
        Volume,

        /// <summary>Time-price opportunities: how many brackets visited the price. How long it stayed.</summary>
        Tpo
    }

    /// <summary>One price on the ladder and everything that happened there.</summary>
    public struct ReadLevel
    {
        public decimal Volume;
        public decimal Bid;        // traded into the bid -- sellers aggressing
        public decimal Ask;        // traded into the ask -- buyers aggressing
        public long TimeMs;        // milliseconds the market spent at the price, as the feed reports it
        public int Tpo;            // distinct time brackets that traded here
        public int FirstBracket;   // -1 until something trades
        public int LastBracket;

        public decimal Delta => Ask - Bid;
    }

    /// <summary>Where trade concentrated, as a band of prices rather than a single tick.</summary>
    public struct Node
    {
        public decimal Low;
        public decimal High;
        public decimal Weight;     // the measure summed across the band
        public bool IsHigh;        // a shelf (HVN) rather than a gap (LVN)

        public decimal Mid => (Low + High) / 2m;
    }

    /// <summary>
    /// The classic profile shapes. These are descriptions of a finished or developing
    /// distribution, not predictions -- what a shape MEANS is the trader's call.
    /// </summary>
    public enum ProfileShape
    {
        /// <summary>Too little trade to say anything.</summary>
        Forming,

        /// <summary>Balanced, fat in the middle. Two-sided auction, value agreed.</summary>
        Normal,

        /// <summary>Fat at the top with a tail below -- the auction stopped going down.</summary>
        PShape,

        /// <summary>Fat at the bottom with a tail above -- the auction stopped going up.</summary>
        BShape,

        /// <summary>Two shelves with a thin valley between: the market moved and rebuilt.</summary>
        DoubleDistribution,

        /// <summary>Thin all the way through -- one-timeframe travel, not an auction settling.</summary>
        Elongated
    }

    /// <summary>
    /// A dense ladder of prices with everything that traded at each, plus the point of control
    /// and value area derived from it.
    ///
    /// Dense is load-bearing. A tick that never traded is a real zero, not a missing row: on a
    /// sparse ladder every index-based comparison lands on the wrong price and the result still
    /// looks like a plausible profile.
    /// </summary>
    public sealed class ReadProfile
    {
        public decimal TickSize;
        public decimal LowPrice;                // the price of Levels[0]
        public ReadLevel[] Levels;

        public decimal TotalVolume;
        public decimal TotalDelta;
        public int TotalTpo;
        public decimal MaxVolume;
        public int Brackets;                    // distinct brackets the profile covers

        public DateTime Start;
        public DateTime End;
        public bool Complete = true;

        public int PocIndex = -1;
        public int VahIndex = -1;
        public int ValIndex = -1;
        public ProfileBasis Basis = ProfileBasis.Volume;

        public int Count => Levels == null ? 0 : Levels.Length;

        public decimal PriceAt(int index) => LowPrice + TickSize * index;
        public decimal HighPrice => PriceAt(Count - 1);

        public decimal Poc => PocIndex < 0 ? 0m : PriceAt(PocIndex);
        public decimal Vah => VahIndex < 0 ? 0m : PriceAt(VahIndex);
        public decimal Val => ValIndex < 0 ? 0m : PriceAt(ValIndex);

        public decimal ValueWidth => VahIndex < 0 || ValIndex < 0 ? 0m : Vah - Val;
        public decimal Range => Count <= 0 ? 0m : HighPrice - LowPrice;

        public int IndexOfPrice(decimal price)
        {
            if (TickSize <= 0m) return -1;

            return (int)Math.Round((price - LowPrice) / TickSize, MidpointRounding.AwayFromZero);
        }

        /// <summary>The index clamped onto the ladder, for asking where a price sits.</summary>
        public int ClampIndex(decimal price)
        {
            var i = IndexOfPrice(price);
            if (i < 0) return 0;
            if (i >= Count) return Count - 1;

            return i;
        }

        public bool InValue(decimal price)
        {
            if (VahIndex < 0 || ValIndex < 0) return false;

            return price >= Val && price <= Vah;
        }
    }

    /// <summary>
    /// Accumulates a profile a price at a time and hands back a dense <see cref="ReadProfile"/>.
    ///
    /// Both measures are carried side by side. Volume answers what changed hands here; the
    /// bracket count answers how long the market stayed -- the TPO reading. They disagree
    /// often, and the disagreement is information: a price with heavy volume and one bracket is
    /// a single violent trade, not accepted value.
    /// </summary>
    public sealed class ProfileBuilder
    {
        private readonly Dictionary<decimal, ReadLevel> _levels = new Dictionary<decimal, ReadLevel>();
        private readonly HashSet<int> _brackets = new HashSet<int>();

        public DateTime Start = DateTime.MaxValue;
        public DateTime End = DateTime.MinValue;

        public int Count => _levels.Count;
        public int BracketCount => _brackets.Count;

        public void Clear()
        {
            _levels.Clear();
            _brackets.Clear();
            Start = DateTime.MaxValue;
            End = DateTime.MinValue;
        }

        public void Note(DateTime time)
        {
            if (time < Start) Start = time;
            if (time > End) End = time;
        }

        /// <summary>
        /// One price level of one bar. The bracket is the time-bracket index the bar falls in.
        /// Brackets need not arrive in order, so the count tracks the lowest and highest seen
        /// rather than counting changes, which would double-count a revisit.
        /// </summary>
        public void Add(decimal price, decimal volume, decimal bid, decimal ask, long timeMs, int bracket)
        {
            if (volume <= 0m && bid <= 0m && ask <= 0m && timeMs <= 0L) return;

            _levels.TryGetValue(price, out var level);

            level.Volume += volume;
            level.Bid += bid;
            level.Ask += ask;
            level.TimeMs += timeMs;

            if (level.Tpo == 0)
            {
                level.Tpo = 1;
                level.FirstBracket = bracket;
                level.LastBracket = bracket;
            }
            else if (bracket > level.LastBracket)
            {
                level.Tpo++;
                level.LastBracket = bracket;
            }
            else if (bracket < level.FirstBracket)
            {
                level.Tpo++;
                level.FirstBracket = bracket;
            }

            _levels[price] = level;
            _brackets.Add(bracket);
        }

        /// <summary>
        /// Fills in every tick between the lowest and highest that traded, so the ladder is
        /// dense. An over-wide profile is REFUSED rather than truncated: a truncated ladder can
        /// put the point of control at a price the market never reached and look reasonable.
        /// </summary>
        public ReadProfile Build(decimal tickSize, int maxLevels)
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

            var profile = new ReadProfile
            {
                TickSize = tickSize,
                LowPrice = low,
                Levels = new ReadLevel[span],
                Start = Start == DateTime.MaxValue ? default : Start,
                End = End == DateTime.MinValue ? default : End,
                Brackets = _brackets.Count
            };

            for (var i = 0; i < span; i++) profile.Levels[i].FirstBracket = -1;

            foreach (var pair in _levels)
            {
                var index = (int)Math.Round((pair.Key - low) / tickSize, MidpointRounding.AwayFromZero);
                if (index < 0 || index >= span) continue;

                profile.Levels[index] = pair.Value;
                profile.TotalVolume += pair.Value.Volume;
                profile.TotalDelta += pair.Value.Delta;
                profile.TotalTpo += pair.Value.Tpo;

                if (pair.Value.Volume > profile.MaxVolume) profile.MaxVolume = pair.Value.Volume;
            }

            return profile;
        }
    }

    /// <summary>Everything read off a profile that is not the ladder itself.</summary>
    public static class ProfileMath
    {
        /// <summary>The measure a basis reads from one level.</summary>
        public static decimal Weight(ReadLevel level, ProfileBasis basis)
        {
            return basis == ProfileBasis.Volume ? level.Volume : level.Tpo;
        }

        public static decimal Total(ReadProfile profile, ProfileBasis basis)
        {
            if (profile == null) return 0m;

            return basis == ProfileBasis.Volume ? profile.TotalVolume : profile.TotalTpo;
        }

        /// <summary>
        /// The busiest price. Ties break toward the middle of the range, because a point of
        /// control that jumps to whichever tie the feed happened to enumerate first reads as
        /// the market moving when nothing moved.
        /// </summary>
        public static int FindPoc(ReadProfile profile, ProfileBasis basis)
        {
            if (profile == null || profile.Count == 0) return -1;

            var best = -1m;
            var bestIndex = -1;
            var middle = (profile.Count - 1) / 2.0;

            for (var i = 0; i < profile.Count; i++)
            {
                var w = Weight(profile.Levels[i], basis);
                if (w <= 0m) continue;

                if (w > best ||
                   (w == best && Math.Abs(i - middle) < Math.Abs(bestIndex - middle)))
                {
                    best = w;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// The value area: expand from the point of control, always toward the heavier of the
        /// two rows immediately outside, until the chosen share of the measure is inside.
        ///
        /// Two rows at a time, not one, is the market-profile standard -- a single-row walk
        /// zig-zags across a level pair and can settle a tick off the conventional band.
        /// </summary>
        public static void ComputeValueArea(ReadProfile profile, decimal percent, ProfileBasis basis)
        {
            if (profile == null || profile.Levels == null) return;

            profile.Basis = basis;
            profile.PocIndex = FindPoc(profile, basis);

            var total = Total(profile, basis);
            if (profile.PocIndex < 0 || total <= 0m) return;

            if (percent <= 0m) percent = 0m;
            if (percent > 100m) percent = 100m;

            var target = total * percent / 100m;
            var levels = profile.Levels;

            var lo = profile.PocIndex;
            var hi = profile.PocIndex;
            var inside = Weight(levels[profile.PocIndex], basis);

            while (inside < target && (lo > 0 || hi < levels.Length - 1))
            {
                var up = 0m;
                var upTo = hi;
                for (var i = 1; i <= 2 && hi + i < levels.Length; i++)
                {
                    up += Weight(levels[hi + i], basis);
                    upTo = hi + i;
                }

                var down = 0m;
                var downTo = lo;
                for (var i = 1; i <= 2 && lo - i >= 0; i++)
                {
                    down += Weight(levels[lo - i], basis);
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
        /// Shelves and gaps as BANDS rather than single ticks.
        ///
        /// A level carrying at least the given share of the busiest one is a shelf candidate
        /// (or at most that share, for a gap); runs within gapTicks of each other merge,
        /// because without that one thin tick reports two shelves where the market built one.
        /// Bands thinner than minTicks are dropped, the most pronounced are kept, and the
        /// survivors are re-sorted into price order so draw order does not shuffle frame to
        /// frame.
        /// </summary>
        public static Node[] FindNodes(ReadProfile profile, ProfileBasis basis, bool high,
                                       decimal percentOfMax, int minTicks, int gapTicks, int keep)
        {
            if (profile == null || profile.Count == 0) return new Node[0];

            var max = 0m;
            for (var i = 0; i < profile.Count; i++)
            {
                var w = Weight(profile.Levels[i], basis);
                if (w > max) max = w;
            }

            if (max <= 0m) return new Node[0];

            var threshold = max * percentOfMax / 100m;
            var runs = new List<Node>();

            var start = -1;
            var lastHit = -1;

            for (var i = 0; i < profile.Count; i++)
            {
                var w = Weight(profile.Levels[i], basis);
                var hit = high ? w >= threshold : w <= threshold;

                if (!hit) continue;

                if (start < 0) start = i;
                else if (i - lastHit > gapTicks + 1)
                {
                    runs.Add(MakeNode(profile, basis, start, lastHit, high));
                    start = i;
                }

                lastHit = i;
            }

            if (start >= 0) runs.Add(MakeNode(profile, basis, start, lastHit, high));

            var wide = new List<Node>();
            foreach (var run in runs)
            {
                var ticks = (int)Math.Round((run.High - run.Low) / profile.TickSize) + 1;
                if (ticks >= minTicks) wide.Add(run);
            }

            // Heaviest first for the trim; a gap is ranked by how thin it is, so both kinds
            // keep the most pronounced examples rather than the widest.
            wide.Sort((a, b) => high ? b.Weight.CompareTo(a.Weight) : a.Weight.CompareTo(b.Weight));
            if (keep > 0 && wide.Count > keep) wide.RemoveRange(keep, wide.Count - keep);

            wide.Sort((a, b) => a.Low.CompareTo(b.Low));

            return wide.ToArray();
        }

        private static Node MakeNode(ReadProfile profile, ProfileBasis basis, int from, int to, bool high)
        {
            var weight = 0m;
            for (var i = from; i <= to; i++) weight += Weight(profile.Levels[i], basis);

            return new Node
            {
                Low = profile.PriceAt(from),
                High = profile.PriceAt(to),
                Weight = weight,
                IsHigh = high
            };
        }

        /// <summary>
        /// Single prints: runs of prices only one bracket ever traded at, away from the
        /// extremes. The market went through them and never came back -- unfinished business
        /// that tends to get revisited.
        ///
        /// Extremes are excluded because a single print at the very top is a TAIL, which is the
        /// opposite reading: a tail is the auction being rejected, a single print inside the
        /// range is the auction skipping ground it never resolved.
        /// </summary>
        public static Node[] FindSinglePrints(ReadProfile profile, int minTicks, int edgeTicks)
        {
            if (profile == null || profile.Count == 0) return new Node[0];

            var found = new List<Node>();
            var start = -1;

            var first = edgeTicks;
            var last = profile.Count - 1 - edgeTicks;

            for (var i = 0; i < profile.Count; i++)
            {
                var single = profile.Levels[i].Tpo == 1 && i >= first && i <= last;

                if (single)
                {
                    if (start < 0) start = i;
                }
                else if (start >= 0)
                {
                    CloseRun(profile, found, start, i - 1, minTicks);
                    start = -1;
                }
            }

            if (start >= 0) CloseRun(profile, found, start, profile.Count - 1, minTicks);

            return found.ToArray();
        }

        private static void CloseRun(ReadProfile profile, List<Node> into, int from, int to, int minTicks)
        {
            if (to - from + 1 < minTicks) return;

            into.Add(MakeNode(profile, ProfileBasis.Volume, from, to, false));
        }

        /// <summary>
        /// A poor extreme: the profile stops flat, several brackets deep, with no tail. The
        /// auction ran out of time rather than out of buyers, so the high or low is unfinished
        /// and far more likely to be taken out than one that ended in excess.
        /// </summary>
        public static bool IsPoorHigh(ReadProfile profile, int minTpo, int depthTicks)
        {
            return PoorEnd(profile, minTpo, depthTicks, true);
        }

        public static bool IsPoorLow(ReadProfile profile, int minTpo, int depthTicks)
        {
            return PoorEnd(profile, minTpo, depthTicks, false);
        }

        private static bool PoorEnd(ReadProfile profile, int minTpo, int depthTicks, bool top)
        {
            if (profile == null || profile.Count < depthTicks || depthTicks < 1) return false;

            for (var i = 0; i < depthTicks; i++)
            {
                var index = top ? profile.Count - 1 - i : i;
                if (profile.Levels[index].Tpo < minTpo) return false;
            }

            return true;
        }

        /// <summary>
        /// The tail (excess) at an extreme: the run of single-bracket prices the auction was
        /// rejected from, in ticks. Zero means the profile ends flat.
        /// </summary>
        public static int TailTicks(ReadProfile profile, bool top)
        {
            if (profile == null || profile.Count == 0) return 0;

            var ticks = 0;
            for (var i = 0; i < profile.Count; i++)
            {
                var index = top ? profile.Count - 1 - i : i;
                if (profile.Levels[index].Tpo != 1) break;

                ticks++;
            }

            // A profile that is single prints all the way through has no tail; it has no body.
            return ticks >= profile.Count ? 0 : ticks;
        }

        /// <summary>
        /// The shape of the distribution. Read off where the point of control sits in the
        /// range, how much of the range the value area covers, and whether there are two
        /// separate shelves with a real valley between them.
        /// </summary>
        public static ProfileShape Shape(ReadProfile profile, ProfileBasis basis, int minBrackets)
        {
            if (profile == null || profile.Count < 3) return ProfileShape.Forming;
            if (profile.Brackets < minBrackets) return ProfileShape.Forming;
            if (profile.PocIndex < 0) return ProfileShape.Forming;

            var shelves = FindNodes(profile, basis, true, 70m, 2, 1, 4);
            if (shelves.Length >= 2 && SeparatedByValley(profile, basis, shelves))
                return ProfileShape.DoubleDistribution;

            var span = (decimal)(profile.Count - 1);
            var position = span <= 0m ? 0.5m : profile.PocIndex / span;
            var valueShare = profile.Range <= 0m ? 1m : profile.ValueWidth / profile.Range;

            // A value area covering almost the whole range means the market never concentrated:
            // it travelled. That is elongation, whatever the point of control's position.
            if (valueShare >= 0.80m) return ProfileShape.Elongated;

            if (position >= 0.65m) return ProfileShape.PShape;
            if (position <= 0.35m) return ProfileShape.BShape;

            return ProfileShape.Normal;
        }

        /// <summary>
        /// Two shelves only count as two distributions if the ground between them is genuinely
        /// thin -- under a third of the lighter shelf's own average. Adjacent shelves either
        /// side of a mild dip are one distribution with a rough top.
        /// </summary>
        private static bool SeparatedByValley(ReadProfile profile, ProfileBasis basis, Node[] shelves)
        {
            for (var s = 1; s < shelves.Length; s++)
            {
                var from = profile.IndexOfPrice(shelves[s - 1].High) + 1;
                var to = profile.IndexOfPrice(shelves[s].Low) - 1;
                if (to < from) continue;

                var valleyMax = 0m;
                for (var i = from; i <= to; i++)
                {
                    var w = Weight(profile.Levels[i], basis);
                    if (w > valleyMax) valleyMax = w;
                }

                var lower = Math.Min(Average(profile, basis, shelves[s - 1]),
                                     Average(profile, basis, shelves[s]));

                if (lower > 0m && valleyMax * 3m < lower) return true;
            }

            return false;
        }

        private static decimal Average(ReadProfile profile, ProfileBasis basis, Node node)
        {
            var from = profile.IndexOfPrice(node.Low);
            var to = profile.IndexOfPrice(node.High);
            var count = to - from + 1;

            return count <= 0 ? 0m : node.Weight / count;
        }

        /// <summary>
        /// The share of two value areas that overlap, 0 to 1, measured against their combined
        /// span. This is the number behind "is the market balancing": overlapping value means
        /// the auction is still trading where it traded before, whatever the bars look like.
        /// </summary>
        public static decimal Overlap(decimal aLow, decimal aHigh, decimal bLow, decimal bHigh)
        {
            if (aHigh < aLow || bHigh < bLow) return 0m;

            var low = Math.Max(aLow, bLow);
            var high = Math.Min(aHigh, bHigh);
            if (high < low) return 0m;

            var shared = high - low;
            var union = Math.Max(aHigh, bHigh) - Math.Min(aLow, bLow);

            // Two zero-width areas at the same price overlap completely; at different prices,
            // not at all. Neither case should divide by zero.
            if (union <= 0m) return aLow == bLow ? 1m : 0m;

            return shared / union;
        }
    }
}
