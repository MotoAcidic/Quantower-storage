using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>The two states an auction is ever in, plus the honest middle.</summary>
    public enum Condition
    {
        /// <summary>Not enough of a session, or no prior value to compare against.</summary>
        Unknown,

        /// <summary>Trading where it already traded. Building value, rotating, two-sided.</summary>
        Balancing,

        /// <summary>Leaving. Seeking value elsewhere, one-timeframing, little overlap.</summary>
        Imbalancing,

        /// <summary>The measurements disagree: one says balance, another says the market left.</summary>
        Transitioning
    }

    /// <summary>Today's value against yesterday's -- the relationship Dalton reads first.</summary>
    public enum ValueRelation
    {
        Unknown,
        [System.ComponentModel.Description("higher")] Higher,
        [System.ComponentModel.Description("overlapping higher")] OverlappingHigher,
        [System.ComponentModel.Description("unchanged")] Unchanged,
        [System.ComponentModel.Description("inside")] Inside,
        [System.ComponentModel.Description("outside")] Outside,
        [System.ComponentModel.Description("overlapping lower")] OverlappingLower,
        [System.ComponentModel.Description("lower")] Lower
    }

    /// <summary>The day types, as far as the session has got.</summary>
    public enum DayType
    {
        Forming,
        Normal,
        NormalVariation,
        Trend,
        DoubleDistribution,
        Neutral
    }

    /// <summary>The first hour of cash trade, and what the rest of the day did with it.</summary>
    public struct InitialBalance
    {
        public bool Set;
        public decimal High;
        public decimal Low;
        public DateTime Closed;

        public decimal Range => High - Low;

        /// <summary>Range extension above the initial balance, as a multiple of its own range.</summary>
        public decimal ExtensionUp(decimal sessionHigh)
        {
            if (!Set || Range <= 0m || sessionHigh <= High) return 0m;

            return (sessionHigh - High) / Range;
        }

        public decimal ExtensionDown(decimal sessionLow)
        {
            if (!Set || Range <= 0m || sessionLow >= Low) return 0m;

            return (Low - sessionLow) / Range;
        }
    }

    /// <summary>Where the developing point of control has been going.</summary>
    public struct Migration
    {
        public bool Set;
        public decimal Ticks;        // signed: positive is higher
        public double Minutes;
        public decimal From;
        public decimal To;

        public string Direction
        {
            get
            {
                if (!Set) return "not yet";
                if (Ticks > 0m) return "higher";
                if (Ticks < 0m) return "lower";

                return "flat";
            }
        }
    }

    /// <summary>The whole auction-theory read of the session.</summary>
    public sealed class AuctionRead
    {
        public Condition Condition = Condition.Unknown;
        public string ConditionWhy = string.Empty;

        public ValueRelation Relation = ValueRelation.Unknown;
        public decimal ValueOverlap;         // 0 to 1 against the prior session

        public DayType DayType = DayType.Forming;
        public string DayWhy = string.Empty;

        public ProfileShape Shape = ProfileShape.Forming;
        public InitialBalance Ib;
        public decimal ExtensionUp;
        public decimal ExtensionDown;

        public decimal Efficiency;           // net progress over ground covered, 0 to 1
        public Migration ValueMigration;
    }

    /// <summary>
    /// Follows the developing point of control so the read can say which way value is going
    /// rather than only where it is.
    ///
    /// The comparison is against the point of control a fixed span of TIME ago, not a fixed
    /// number of bars, so the answer means the same thing on a one-minute chart and a
    /// five-hundred-tick one.
    /// </summary>
    public sealed class PocTrail
    {
        private struct Point
        {
            public DateTime Time;
            public decimal Poc;
        }

        private readonly List<Point> _points = new List<Point>();
        private const int Cap = 4096;

        public void Clear() => _points.Clear();

        public void Add(DateTime time, decimal poc)
        {
            if (poc <= 0m) return;

            _points.Add(new Point { Time = time, Poc = poc });
            if (_points.Count > Cap) _points.RemoveRange(0, _points.Count - Cap);
        }

        public Migration Measure(double overMinutes, decimal tickSize)
        {
            var migration = new Migration();
            if (_points.Count < 2 || tickSize <= 0m || overMinutes <= 0d) return migration;

            var last = _points[_points.Count - 1];
            var cutoff = last.Time.AddMinutes(-overMinutes);

            // The oldest point still inside the window, or the oldest there is. Using the
            // oldest overall when the window is not yet full reports a shorter span honestly
            // rather than reporting no migration at all for the first hour of every session.
            var index = 0;
            for (var i = _points.Count - 1; i >= 0; i--)
            {
                if (_points[i].Time <= cutoff) { index = i; break; }
                index = i;
            }

            var first = _points[index];
            if (first.Time == last.Time) return migration;

            migration.Set = true;
            migration.From = first.Poc;
            migration.To = last.Poc;
            migration.Ticks = (last.Poc - first.Poc) / tickSize;
            migration.Minutes = (last.Time - first.Time).TotalMinutes;

            return migration;
        }

        public decimal Latest => _points.Count == 0 ? 0m : _points[_points.Count - 1].Poc;
    }

    public static class AuctionMath
    {
        /// <summary>
        /// Today's value area against the previous session's, in the classic vocabulary.
        ///
        /// The tolerance is what stops a one-tick difference reading as a migration. Below it
        /// the two areas are the same area, which is the whole point of "unchanged" -- the
        /// market opened, traded, and agreed with yesterday.
        /// </summary>
        public static ValueRelation Relate(decimal todayLow, decimal todayHigh,
                                           decimal priorLow, decimal priorHigh, decimal tolerance)
        {
            if (todayHigh < todayLow || priorHigh < priorLow) return ValueRelation.Unknown;
            if (priorHigh <= 0m || todayHigh <= 0m) return ValueRelation.Unknown;

            if (Math.Abs(todayHigh - priorHigh) <= tolerance &&
                Math.Abs(todayLow - priorLow) <= tolerance)
                return ValueRelation.Unchanged;

            if (todayLow > priorHigh) return ValueRelation.Higher;
            if (todayHigh < priorLow) return ValueRelation.Lower;

            if (todayLow >= priorLow - tolerance && todayHigh <= priorHigh + tolerance)
                return ValueRelation.Inside;

            if (todayLow <= priorLow + tolerance && todayHigh >= priorHigh - tolerance)
                return ValueRelation.Outside;

            return todayHigh > priorHigh ? ValueRelation.OverlappingHigher : ValueRelation.OverlappingLower;
        }

        public static string Describe(ValueRelation relation)
        {
            switch (relation)
            {
                case ValueRelation.Higher: return "higher";
                case ValueRelation.OverlappingHigher: return "overlapping higher";
                case ValueRelation.Unchanged: return "unchanged";
                case ValueRelation.Inside: return "inside";
                case ValueRelation.Outside: return "outside";
                case ValueRelation.OverlappingLower: return "overlapping lower";
                case ValueRelation.Lower: return "lower";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Balancing or imbalancing, from two independent measurements: how much of yesterday's
        /// value today is still trading in, and how much of the ground covered turned into net
        /// progress.
        ///
        /// When the two disagree the answer is Transitioning, not a casting vote. A market that
        /// is still inside yesterday's value but travelling one way all morning is doing
        /// something worth naming, and calling it either balance or imbalance loses that.
        /// </summary>
        public static Condition Judge(decimal overlap, decimal efficiency, bool haveOverlap,
                                      bool haveEfficiency, decimal balancedOverlap,
                                      decimal directionalEfficiency, out string why)
        {
            why = string.Empty;

            if (!haveOverlap && !haveEfficiency)
            {
                why = "nothing measured yet";
                return Condition.Unknown;
            }

            var overlapSays = !haveOverlap
                            ? Condition.Unknown
                            : overlap >= balancedOverlap ? Condition.Balancing : Condition.Imbalancing;

            var travelSays = !haveEfficiency
                           ? Condition.Unknown
                           : efficiency >= directionalEfficiency ? Condition.Imbalancing : Condition.Balancing;

            var parts = new List<string>();
            if (haveOverlap) parts.Add("value overlap " + Format.Percent((double)overlap));
            if (haveEfficiency) parts.Add("efficiency " + Math.Round(efficiency, 2).ToString("0.00"));
            why = string.Join(", ", parts);

            if (overlapSays == Condition.Unknown) return travelSays;
            if (travelSays == Condition.Unknown) return overlapSays;
            if (overlapSays == travelSays) return overlapSays;

            return Condition.Transitioning;
        }

        /// <summary>
        /// The day type as far as the session has got. Every one of these is provisional until
        /// the cash close -- a normal day becomes a trend day at 13:00 often enough that
        /// labelling it early and never revisiting it is how a profile read goes wrong.
        /// </summary>
        public static DayType ClassifyDay(InitialBalance ib, decimal sessionHigh, decimal sessionLow,
                                          ProfileShape shape, decimal trendExtension,
                                          decimal variationExtension, out string why)
        {
            why = string.Empty;

            if (!ib.Set || ib.Range <= 0m)
            {
                why = "initial balance not closed yet";
                return DayType.Forming;
            }

            var up = ib.ExtensionUp(sessionHigh);
            var down = ib.ExtensionDown(sessionLow);

            if (shape == ProfileShape.DoubleDistribution)
            {
                why = "two shelves with a thin valley between";
                return DayType.DoubleDistribution;
            }

            var extendedUp = up >= variationExtension;
            var extendedDown = down >= variationExtension;

            if (extendedUp && extendedDown)
            {
                why = "extended both ways (" + Round(up) + " up, " + Round(down) + " x IB down)";
                return DayType.Neutral;
            }

            if (up >= trendExtension || down >= trendExtension)
            {
                why = "extended " + Round(Math.Max(up, down)) + " x IB " + (up > down ? "up" : "down");
                return DayType.Trend;
            }

            if (extendedUp || extendedDown)
            {
                why = "extended " + Round(Math.Max(up, down)) + " x IB " + (up > down ? "up" : "down");
                return DayType.NormalVariation;
            }

            why = "initial balance holding";
            return DayType.Normal;
        }

        private static string Round(decimal value) => Math.Round(value, 2).ToString("0.##");

        public static string Describe(DayType type)
        {
            switch (type)
            {
                case DayType.Normal: return "Normal";
                case DayType.NormalVariation: return "Normal Variation";
                case DayType.Trend: return "Trend";
                case DayType.DoubleDistribution: return "Double Distribution";
                case DayType.Neutral: return "Neutral";
                default: return "forming";
            }
        }

        public static string Describe(ProfileShape shape)
        {
            switch (shape)
            {
                case ProfileShape.Normal: return "balanced (D)";
                case ProfileShape.PShape: return "P -- fat above, tail below";
                case ProfileShape.BShape: return "b -- fat below, tail above";
                case ProfileShape.DoubleDistribution: return "double distribution";
                case ProfileShape.Elongated: return "elongated -- travelling, not settling";
                default: return "forming";
            }
        }
    }
}
