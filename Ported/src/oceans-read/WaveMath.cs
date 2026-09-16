using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>What the last few rotations look like as a sequence.</summary>
    public enum Sequence
    {
        /// <summary>Fewer rotations than it takes to say anything.</summary>
        TooShort,

        /// <summary>Legs mostly clear of each other and going one way. An impulse looks like this.</summary>
        Impulsive,

        /// <summary>Legs treading on each other. A correction looks like this.</summary>
        Corrective
    }

    /// <summary>One rule from the count, and whether the market kept it.</summary>
    public struct WaveRule
    {
        public string Name;
        public bool Passed;
        public string Detail;
    }

    /// <summary>
    /// A candidate Elliott count over the last five rotations, and every rule it was checked
    /// against.
    ///
    /// The rules are the three that are not negotiable -- wave 2 holding the origin of wave 1,
    /// wave 3 never the shortest, wave 4 staying out of wave 1 -- plus wave 3 clearing the end
    /// of wave 1. When any of them fails there is NO COUNT, and that is what gets printed. A
    /// labeller that always finds five waves has told you nothing, because it would have found
    /// five in a random walk too.
    /// </summary>
    public sealed class WaveCount
    {
        public bool Valid;
        public bool Up;
        public decimal[] Points = new decimal[0];       // p0..p5, the pivots the count runs through
        public int[] Bars = new int[0];
        public DateTime[] Times = new DateTime[0];

        public List<WaveRule> Rules = new List<WaveRule>();

        public decimal Wave2Retrace;                    // as a fraction of wave 1
        public decimal Wave4Retrace;                    // as a fraction of wave 3
        public decimal Wave3Extension;                  // as a multiple of wave 1
        public bool Truncated;                          // wave 5 failed to exceed wave 3

        public string Failure = string.Empty;

        /// <summary>The one-line verdict for the read box.</summary>
        public string Verdict
        {
            get
            {
                if (!Valid) return "no valid count" + (Failure.Length == 0 ? "" : " (" + Failure + ")");

                var text = (Up ? "impulse up" : "impulse down") + " 1-5 complete";
                if (Truncated) text += ", truncated 5th";

                return text;
            }
        }
    }

    /// <summary>What the correction after an impulse looks like, when there is one.</summary>
    public sealed class CorrectionRead
    {
        public bool Present;
        public bool Up;                                 // the correction is running upward
        public string Kind = string.Empty;              // zigzag, flat, or unclassified
        public decimal Retrace;                         // of the impulse it follows
    }

    public static class WaveMath
    {
        /// <summary>
        /// The character of the last N rotations, which is the part of Elliott that survives
        /// contact with a live chart.
        ///
        /// Overlap is the whole test: an impulse leaves ground behind it, a correction keeps
        /// trading back over the same prices. This needs no count and cannot be wrong about
        /// which wave we are in, because it does not claim to know.
        /// </summary>
        public static Sequence Character(IReadOnlyList<Swing> swings, int legs, decimal overlapShare)
        {
            if (swings == null || swings.Count < 4 || legs < 4) return Sequence.TooShort;

            var from = swings.Count > legs ? swings.Count - legs : 0;
            var count = swings.Count - from;
            if (count < 4) return Sequence.TooShort;

            // How much two same-direction legs must share before they count as covering the
            // same ground. It cannot be "they touch at all": wave 3 begins where wave 2 ended,
            // which is inside wave 1 by construction, so ANY contact would read every impulse
            // ever made as a correction.
            const decimal Shared = 0.5m;

            // Compare each leg with the one two back -- the previous leg in the SAME direction.
            // Legs next to each other always overlap; that is what a rotation is.
            var overlaps = 0;
            var pairs = 0;

            for (var i = from + 2; i < swings.Count; i++)
            {
                var now = swings[i];
                var before = swings[i - 2];

                var nowLow = Math.Min(now.FromPrice, now.Price);
                var nowHigh = Math.Max(now.FromPrice, now.Price);
                var wasLow = Math.Min(before.FromPrice, before.Price);
                var wasHigh = Math.Max(before.FromPrice, before.Price);

                if (ProfileMath.Overlap(nowLow, nowHigh, wasLow, wasHigh) >= Shared) overlaps++;
                pairs++;
            }

            if (pairs == 0) return Sequence.TooShort;

            var share = (decimal)overlaps / pairs;

            return share >= overlapShare ? Sequence.Corrective : Sequence.Impulsive;
        }

        /// <summary>
        /// Tries to read the last five completed rotations as an impulse. Returns a count that
        /// says explicitly which rule broke when one did.
        /// </summary>
        public static WaveCount Count(IReadOnlyList<Swing> swings)
        {
            return swings == null ? Count(null, -1) : Count(swings, swings.Count - 1);
        }

        /// <summary>
        /// The most recent valid impulse ending at or before the last rotation, searched back
        /// up to <paramref name="maxBack"/> rotations. <paramref name="endIndex"/> comes back
        /// as the rotation the count ends on, so the caller can read whatever followed it as
        /// the correction.
        ///
        /// Searching back is what makes the count useful mid-move: an impulse that finished
        /// three rotations ago is still the structure we are correcting inside, and a count
        /// that only ever looks at the last five rotations goes blank the moment one completes.
        /// </summary>
        public static WaveCount Recent(IReadOnlyList<Swing> swings, int maxBack, out int endIndex)
        {
            endIndex = -1;
            if (swings == null || swings.Count < 5) return Count(swings);

            WaveCount last = null;

            for (var back = 0; back <= maxBack; back++)
            {
                var end = swings.Count - 1 - back;
                if (end < 4) break;

                var candidate = Count(swings, end);
                if (back == 0) last = candidate;

                if (!candidate.Valid) continue;

                endIndex = end;
                return candidate;
            }

            return last ?? Count(swings);
        }

        /// <summary>The five rotations ending at <paramref name="endIndex"/>, read as an impulse.</summary>
        public static WaveCount Count(IReadOnlyList<Swing> swings, int endIndex)
        {
            var count = new WaveCount();

            if (swings == null || swings.Count < 5 || endIndex < 4 || endIndex >= swings.Count)
            {
                count.Failure = "fewer than five completed rotations";
                return count;
            }

            var first = endIndex - 4;
            var points = new decimal[6];
            var bars = new int[6];
            var times = new DateTime[6];

            points[0] = swings[first].FromPrice;
            bars[0] = swings[first].FromBar;
            times[0] = swings[first].FromTime;

            for (var i = 0; i < 5; i++)
            {
                var swing = swings[first + i];

                // The zigzag chains pivot to pivot, so a break in the chain means the caller
                // handed over a list that was rebuilt mid-series. Refuse rather than count it.
                if (i > 0 && swings[first + i].FromPrice != swings[first + i - 1].Price)
                {
                    count.Failure = "the rotations do not chain";
                    return count;
                }

                points[i + 1] = swing.Price;
                bars[i + 1] = swing.PivotBar;
                times[i + 1] = swing.PivotTime;
            }

            count.Points = points;
            count.Bars = bars;
            count.Times = times;
            count.Up = swings[endIndex].IsHigh;

            var sign = count.Up ? 1m : -1m;

            decimal Move(int from, int to) => (points[to] - points[from]) * sign;

            var w1 = Move(0, 1);
            var w2 = -Move(1, 2);
            var w3 = Move(2, 3);
            var w4 = -Move(3, 4);
            var w5 = Move(4, 5);

            // The zigzag alternates by construction, so a non-positive leg here means the last
            // five swings do not run 1-2-3-4-5 in the direction of the final pivot.
            if (w1 <= 0m || w2 <= 0m || w3 <= 0m || w4 <= 0m || w5 <= 0m)
            {
                count.Failure = "the rotations do not alternate into a five";
                return count;
            }

            count.Wave2Retrace = w1 <= 0m ? 0m : w2 / w1;
            count.Wave4Retrace = w3 <= 0m ? 0m : w4 / w3;
            count.Wave3Extension = w1 <= 0m ? 0m : w3 / w1;
            count.Truncated = Move(3, 5) <= 0m;

            Rule(count, "wave 2 holds the start of wave 1",
                 Move(0, 2) > 0m,
                 "retraced " + Pct(count.Wave2Retrace) + " of wave 1");

            Rule(count, "wave 3 clears the end of wave 1",
                 Move(1, 3) > 0m,
                 "wave 3 ran " + Mult(count.Wave3Extension) + " of wave 1");

            Rule(count, "wave 3 is not the shortest",
                 w3 >= w1 || w3 >= w5,
                 "1:" + Round(w1) + "  3:" + Round(w3) + "  5:" + Round(w5));

            Rule(count, "wave 4 stays out of wave 1",
                 Move(1, 4) > 0m,
                 "retraced " + Pct(count.Wave4Retrace) + " of wave 3");

            count.Valid = true;
            foreach (var rule in count.Rules)
            {
                if (rule.Passed) continue;

                count.Valid = false;
                if (count.Failure.Length == 0) count.Failure = rule.Name + " -- broken";
            }

            return count;
        }

        private static void Rule(WaveCount count, string name, bool passed, string detail)
        {
            count.Rules.Add(new WaveRule { Name = name, Passed = passed, Detail = detail });
        }

        /// <summary>
        /// The correction that follows a valid impulse, read off the rotations after it. Only
        /// meaningful when the count is valid -- a correction of nothing is just a rotation.
        /// </summary>
        public static CorrectionRead Correction(IReadOnlyList<Swing> swings, WaveCount count, int endIndex)
        {
            var read = new CorrectionRead();
            if (count == null || !count.Valid || swings == null || endIndex < 0) return read;

            var impulseEnd = count.Points[5];
            var impulseStart = count.Points[0];
            var size = Math.Abs(impulseEnd - impulseStart);
            if (size <= 0m) return read;

            // Everything the zigzag has completed since the fifth wave landed.
            var after = new List<Swing>();
            for (var i = endIndex + 1; i < swings.Count; i++) after.Add(swings[i]);

            if (after.Count == 0) return read;

            read.Present = true;
            read.Up = !count.Up;

            var deepest = impulseEnd;
            foreach (var swing in after)
            {
                if (count.Up && swing.Price < deepest) deepest = swing.Price;
                if (!count.Up && swing.Price > deepest) deepest = swing.Price;
            }

            read.Retrace = Math.Abs(impulseEnd - deepest) / size;

            if (after.Count >= 3)
            {
                var a = after[after.Count - 3];
                var b = after[after.Count - 2];
                var c = after[after.Count - 1];

                var aSize = Math.Abs(a.Price - a.FromPrice);
                var bSize = Math.Abs(b.Price - b.FromPrice);
                var cBeyondA = count.Up ? c.Price < a.Price : c.Price > a.Price;

                if (aSize > 0m && bSize <= aSize * 0.62m && cBeyondA) read.Kind = "zigzag";
                else if (aSize > 0m && bSize >= aSize * 0.90m) read.Kind = "flat";
                else read.Kind = "unclassified";
            }
            else
            {
                read.Kind = "in progress";
            }

            return read;
        }

        private static string Pct(decimal fraction) => Math.Round(fraction * 100m) + "%";
        private static string Mult(decimal ratio) => Math.Round(ratio, 2).ToString("0.##") + "x";
        private static string Round(decimal value) => Math.Round(value, 2).ToString("0.##");

        public static string Describe(Sequence sequence)
        {
            switch (sequence)
            {
                case Sequence.Impulsive: return "impulsive -- legs clear of each other";
                case Sequence.Corrective: return "corrective -- legs overlapping";
                default: return "too few rotations to say";
            }
        }
    }
}
