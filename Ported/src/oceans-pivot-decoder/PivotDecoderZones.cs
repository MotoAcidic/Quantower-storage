using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansPivotDecoder
{
    /// <summary>
    /// What a level IS, which is what decides how much it counts for. Keeping this an enum rather
    /// than a string means a new family cannot be added without a weight being chosen for it.
    /// </summary>
    public enum LevelFamily
    {
        Floor = 0,
        Camarilla = 1,
        Mid = 2,

        /// <summary>Prior session high, low or close.</summary>
        PriorHlc = 3,

        /// <summary>POC of a completed Asia / London / NY session.</summary>
        SessionPoc = 4,

        /// <summary>A daily POC price has never traded back through.</summary>
        NakedPoc = 5,

        /// <summary>A poor high or poor low: an auction that was cut off rather than exhausted.</summary>
        PoorExtreme = 6,

        /// <summary>Session VWAP, prior or developing.</summary>
        Vwap = 7
    }

    /// <summary>
    /// How much each family contributes to a zone's score.
    ///
    /// The spread is the whole argument of the tool. A floor pivot is arithmetic on three numbers
    /// and scores 1. A naked POC or an unfinished extreme is unfinished business the market has
    /// left on the table, and scores 2 -- those are the prices that pull.
    /// </summary>
    public sealed class ZoneWeights
    {
        public decimal Floor = 1.0m;
        public decimal Camarilla = 1.0m;
        public decimal Mid = 1.0m;
        public decimal PriorHlc = 1.0m;
        public decimal SessionPoc = 1.5m;
        public decimal NakedPoc = 2.0m;
        public decimal PoorExtreme = 2.0m;
        public decimal Vwap = 0.75m;

        public decimal For(LevelFamily family)
        {
            switch (family)
            {
                case LevelFamily.Camarilla: return Camarilla;
                case LevelFamily.Mid: return Mid;
                case LevelFamily.PriorHlc: return PriorHlc;
                case LevelFamily.SessionPoc: return SessionPoc;
                case LevelFamily.NakedPoc: return NakedPoc;
                case LevelFamily.PoorExtreme: return PoorExtreme;
                case LevelFamily.Vwap: return Vwap;
                default: return Floor;
            }
        }
    }

    /// <summary>A band of prices where several levels land together.</summary>
    public sealed class Zone
    {
        public decimal Low;
        public decimal High;

        public List<PivotLevel> Members = new List<PivotLevel>();

        public decimal Score;

        /// <summary>True when the band sits below price: a candidate long.</summary>
        public bool IsLong;

        /// <summary>Rank on its own side, 1 being the best. Drives the L1 / S1 labels.</summary>
        public int Rank;

        /// <summary>Live absorption state, attached by the platform side.</summary>
        public ZoneState State;

        public decimal Center { get { return (Low + High) / 2m; } }
        public decimal Width { get { return High - Low; } }

        /// <summary>
        /// Identity across rebuilds. Levels are static intraday, so the same band reappears at the
        /// same prices every bar and its absorption state has to survive being recomputed.
        /// </summary>
        public string Key
        {
            get
            {
                return Low.ToString("F2", CultureInfo.InvariantCulture) + "/" +
                       High.ToString("F2", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Member names for the panel, e.g. "pRTH-L, pETH-L, cS1".</summary>
        public string MemberList
        {
            get
            {
                var names = new List<string>();
                foreach (var member in Members) names.Add(member.Name);

                return string.Join(", ", names.ToArray());
            }
        }

        /// <summary>Score as it appears in a label: x8 when whole, x8.5 when not.</summary>
        public string ScoreText
        {
            get
            {
                return Score == Math.Truncate(Score)
                    ? "x" + Score.ToString("F0", CultureInfo.InvariantCulture)
                    : "x" + Score.ToString("0.#", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Whether the band contains unfinished business -- a naked POC or a poor extreme. These
        /// are the members that pull price back, as opposed to the ones that merely describe
        /// where it has been, and the base rates are bucketed on it.
        /// </summary>
        public bool HasMagnet
        {
            get
            {
                foreach (var member in Members)
                    if (member.Family == LevelFamily.NakedPoc || member.Family == LevelFamily.PoorExtreme)
                        return true;

                return false;
            }
        }

        /// <summary>Distinct families in the band, for the panel.</summary>
        public int FamilyCount
        {
            get
            {
                var seen = new HashSet<LevelFamily>();
                foreach (var member in Members) seen.Add(member.Family);
                return seen.Count;
            }
        }
    }

    public static class ZoneClusterer
    {
        /// <summary>
        /// Groups levels that land within <paramref name="tolerance"/> of each other and scores
        /// each band as the weighted sum of its members.
        ///
        /// The band is capped at the tolerance overall rather than growing by it per member. That
        /// matters: the Camarilla ladder is evenly spaced, so a per-member rule would chain the
        /// whole ladder into one two-hundred-point "zone" that is not a level at all.
        /// </summary>
        public static List<Zone> Cluster(IList<PivotLevel> levels, decimal tolerance, ZoneWeights weights)
        {
            var zones = new List<Zone>();
            if (levels == null || levels.Count == 0) return zones;

            var sorted = new List<PivotLevel>(levels);
            sorted.Sort(delegate (PivotLevel a, PivotLevel b) { return a.Price.CompareTo(b.Price); });

            Zone current = null;

            foreach (var level in sorted)
            {
                if (current != null && level.Price - current.Low <= tolerance)
                {
                    current.Members.Add(level);
                    current.High = level.Price;
                    continue;
                }

                if (current != null) zones.Add(current);

                current = new Zone { Low = level.Price, High = level.Price };
                current.Members.Add(level);
            }

            if (current != null) zones.Add(current);

            foreach (var zone in zones) Score(zone, weights);

            return zones;
        }

        /// <summary>
        /// What actually reaches the chart: the highest-scoring bands within range of price, at
        /// most <paramref name="maxPerSide"/> on each side, ranked.
        ///
        /// Everything else stays in memory for the log. A band four hundred points away is true
        /// and irrelevant, and drawing it is how the ones that matter became impossible to find.
        /// </summary>
        public static List<Zone> Select(IList<Zone> zones, decimal price, decimal rangePts,
                                        int maxPerSide)
        {
            var longs = new List<Zone>();
            var shorts = new List<Zone>();

            if (zones != null)
            {
                foreach (var zone in zones)
                {
                    if (zone.Members.Count == 0) continue;
                    if (Math.Abs(zone.Center - price) > rangePts) continue;

                    // A band price is sitting inside belongs to neither side: there is no long or
                    // short to state until price leaves it.
                    if (zone.High < price) { zone.IsLong = true; longs.Add(zone); }
                    else if (zone.Low > price) { zone.IsLong = false; shorts.Add(zone); }
                }
            }

            var selected = new List<Zone>();
            selected.AddRange(Rank(longs, price, maxPerSide));
            selected.AddRange(Rank(shorts, price, maxPerSide));

            return selected;
        }

        private static List<Zone> Rank(List<Zone> side, decimal price, int maxPerSide)
        {
            side.Sort(delegate (Zone a, Zone b)
            {
                if (a.Score != b.Score) return b.Score.CompareTo(a.Score);

                return Math.Abs(a.Center - price).CompareTo(Math.Abs(b.Center - price));
            });

            if (maxPerSide > 0 && side.Count > maxPerSide) side.RemoveRange(maxPerSide, side.Count - maxPerSide);

            for (var i = 0; i < side.Count; i++) side[i].Rank = i + 1;

            return side;
        }

        /// <summary>
        /// Weighted sum of the members, counting each FACT once.
        ///
        /// Yesterday's RTH high and yesterday's 24h high are frequently the same price. Before
        /// this, that band scored 2.0 for prior-H/L/C when one thing had happened: the same
        /// number arrived twice under two labels and the score doubled. The same goes for a
        /// Camarilla level that happens to land exactly on a floor pivot.
        ///
        /// So a family only pays once per distinct price. Two DIFFERENT prices from the same
        /// family inside the band still both count -- that is a genuinely wider shelf, not a
        /// duplicate.
        /// </summary>
        private static void Score(Zone zone, ZoneWeights weights)
        {
            var counted = new HashSet<string>();
            var score = 0m;

            foreach (var member in zone.Members)
            {
                var key = (int)member.Family + "@" +
                          member.Price.ToString("F4", CultureInfo.InvariantCulture);

                if (!counted.Add(key)) continue;

                score += weights.For(member.Family);
            }

            zone.Score = score;
        }

        /// <summary>
        /// Narrows an already-ranked set to what should actually be shaded on the chart.
        ///
        /// Ranking happens ONCE, over the wider ladder, and this only filters. That is what keeps
        /// S2 on the chart called S2 in the panel: renumbering the drawn subset would give the
        /// same band two different names depending on where you read it, which is precisely the
        /// kind of thing that gets an order placed at the wrong level.
        /// </summary>
        public static List<Zone> WithinRange(IList<Zone> ranked, decimal price, decimal rangePts,
                                             int maxPerSide)
        {
            var kept = new List<Zone>();
            if (ranked == null) return kept;

            var longs = 0;
            var shorts = 0;

            foreach (var zone in ranked)
            {
                if (Math.Abs(zone.Center - price) > rangePts) continue;

                if (zone.IsLong)
                {
                    if (maxPerSide > 0 && longs >= maxPerSide) continue;
                    longs++;
                }
                else
                {
                    if (maxPerSide > 0 && shorts >= maxPerSide) continue;
                    shorts++;
                }

                kept.Add(zone);
            }

            return kept;
        }

        /// <summary>The band nearest price, whose full breakdown the panel shows.</summary>
        public static Zone Nearest(IList<Zone> zones, decimal price)
        {
            Zone best = null;
            var bestDistance = decimal.MaxValue;

            if (zones == null) return null;

            foreach (var zone in zones)
            {
                var distance = Math.Abs(zone.Center - price);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = zone;
            }

            return best;
        }
    }
}
