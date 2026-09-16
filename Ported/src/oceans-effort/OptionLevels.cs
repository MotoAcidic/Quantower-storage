using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansEffort
{
    /// <summary>What a level from outside the tape is expected to do.</summary>
    public enum LevelKind
    {
        /// <summary>A level to note, arguing for neither side. Gamma flip, VWAP anchors, whatever you put there.</summary>
        Pivot,

        Support,
        Resistance
    }

    /// <summary>A price level supplied from outside: options positioning, or anything else you keep.</summary>
    public struct OptionLevel
    {
        public decimal Price;
        public string Label;
        public LevelKind Kind;
    }

    /// <summary>
    /// A parsed level file, with what went wrong kept alongside what went right.
    ///
    /// Bad lines are COUNTED, not skipped quietly. A file that half-parsed and drew three levels
    /// out of six looks exactly like a file that was meant to have three.
    /// </summary>
    public sealed class LevelSet
    {
        public readonly List<OptionLevel> Levels = new List<OptionLevel>();

        public int BadLines;
        public string Error;

        public int Count { get { return Levels.Count; } }
        public bool Usable { get { return Error == null && Levels.Count > 0; } }
    }

    public static class OptionLevels
    {
        /// <summary>
        /// Reads the level file. One level per line:
        ///
        ///     price, label, kind
        ///     23980.00, Call Wall, resistance
        ///     23800.00, Gamma Flip, pivot
        ///
        /// Blank lines and lines starting with # are ignored. Kind may be omitted, in which case
        /// the level is a pivot and argues for nobody -- guessing a side from a label would be
        /// this code inventing a bias out of a string.
        /// </summary>
        public static LevelSet Parse(IEnumerable<string> lines)
        {
            var set = new LevelSet();

            if (lines == null)
            {
                set.Error = "no level file";
                return set;
            }

            foreach (var raw in lines)
            {
                if (raw == null) continue;

                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var parts = line.Split(',');

                decimal price;
                if (!decimal.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out price)
                    || price <= 0m)
                {
                    set.BadLines++;
                    continue;
                }

                var level = new OptionLevel();
                level.Price = price;
                level.Label = parts.Length > 1 ? parts[1].Trim() : "";
                level.Kind = LevelKind.Pivot;

                if (parts.Length > 2)
                {
                    var kind = parts[2].Trim().ToLowerInvariant();

                    if (kind == "support" || kind == "s") level.Kind = LevelKind.Support;
                    else if (kind == "resistance" || kind == "r") level.Kind = LevelKind.Resistance;
                    else if (kind != "pivot" && kind != "p" && kind.Length > 0) set.BadLines++;
                }

                if (level.Label.Length == 0) level.Label = "level";

                set.Levels.Add(level);
            }

            return set;
        }

        /// <summary>
        /// The nearest level within reach of a price, and which side it argues for.
        ///
        /// Support under your feet argues for buyers, resistance overhead for sellers, and a pivot
        /// argues for nobody while still being worth naming.
        /// </summary>
        public static Side Bias(LevelSet set, decimal price, decimal tick, int reachTicks,
                                out OptionLevel nearest)
        {
            nearest = new OptionLevel();

            if (set == null || tick <= 0m || set.Levels.Count == 0) return Side.None;
            if (reachTicks < 0) reachTicks = 0;

            var reach = tick * reachTicks;
            var best = -1;
            var bestGap = decimal.MaxValue;

            for (var i = 0; i < set.Levels.Count; i++)
            {
                var gap = set.Levels[i].Price - price;
                if (gap < 0m) gap = -gap;
                if (gap > reach) continue;

                if (gap >= bestGap) continue;

                bestGap = gap;
                best = i;
            }

            if (best < 0) return Side.None;

            nearest = set.Levels[best];

            if (nearest.Kind == LevelKind.Support) return Side.Buy;
            return nearest.Kind == LevelKind.Resistance ? Side.Sell : Side.None;
        }

        /// <summary>
        /// Whether a wall sits in the way of this trade: resistance just overhead for a long, or
        /// support just underneath for a short.
        ///
        /// This is the one thing option levels are unambiguously good for -- not predicting where
        /// price goes, but naming where a scalp has no room. A one-to-one target on the far side
        /// of a wall is a target you were never going to be paid.
        /// </summary>
        public static bool Blocks(LevelSet set, Side side, decimal price, decimal tick, int vetoTicks,
                                  out OptionLevel wall)
        {
            wall = new OptionLevel();

            if (set == null || tick <= 0m || vetoTicks <= 0) return false;
            if (side != Side.Buy && side != Side.Sell) return false;

            var reach = tick * vetoTicks;

            for (var i = 0; i < set.Levels.Count; i++)
            {
                var level = set.Levels[i];
                var gap = level.Price - price;

                if (side == Side.Buy)
                {
                    // Only a level ABOVE and within reach is in the way of a long.
                    if (level.Kind != LevelKind.Resistance) continue;
                    if (gap <= 0m || gap > reach) continue;
                }
                else
                {
                    if (level.Kind != LevelKind.Support) continue;
                    if (gap >= 0m || -gap > reach) continue;
                }

                wall = level;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a target can be reached without crossing a wall. Same idea as
        /// <see cref="Blocks"/>, measured against where the trade is actually trying to get to
        /// rather than a fixed distance.
        /// </summary>
        public static bool BlocksTarget(LevelSet set, Side side, decimal entry, decimal target,
                                        out OptionLevel wall)
        {
            wall = new OptionLevel();
            if (set == null) return false;

            for (var i = 0; i < set.Levels.Count; i++)
            {
                var level = set.Levels[i];

                if (side == Side.Buy)
                {
                    if (level.Kind != LevelKind.Resistance) continue;
                    if (level.Price <= entry || level.Price >= target) continue;
                }
                else
                {
                    if (level.Kind != LevelKind.Support) continue;
                    if (level.Price >= entry || level.Price <= target) continue;
                }

                wall = level;
                return true;
            }

            return false;
        }
    }
}
