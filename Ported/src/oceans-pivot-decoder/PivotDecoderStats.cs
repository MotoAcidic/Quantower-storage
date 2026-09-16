using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansPivotDecoder
{
    #region Touch outcomes

    public enum TouchResult
    {
        /// <summary>Price never reached the band that session.</summary>
        NotTouched = 0,

        /// <summary>Price reached it and turned away before accepting through.</summary>
        Rejected = 1,

        /// <summary>Price went through and kept going.</summary>
        Accepted = 2,

        /// <summary>Touched, but the session ended before it resolved either way.</summary>
        Unresolved = 3
    }

    /// <summary>One historical band, and what price did when it got there.</summary>
    public struct TouchEvent
    {
        public decimal Score;
        public bool HasMagnet;
        public bool IsLong;
        public TouchResult Result;

        /// <summary>The band contained the session's extreme -- it top-ticked or bottom-ticked.</summary>
        public bool MarkedExtreme;
    }

    /// <summary>Thresholds for what counts as a rejection and what counts as giving up.</summary>
    public sealed class StatsConfig
    {
        /// <summary>How far price must travel back off the band to call it a rejection.</summary>
        public decimal RejectPts = 30m;

        /// <summary>How far through the band price must go to call it accepted.</summary>
        public decimal AcceptPts = 15m;

        /// <summary>
        /// Touches below this and no rate is reported at all.
        ///
        /// This is the single most important number in the file. A 3-for-4 hit rate reads as 75%
        /// and means nothing; showing it would be worse than showing nothing, because it invites
        /// size behind noise. Below the floor the panel says how many samples it has and stops.
        /// </summary>
        public int MinSample = 8;

        /// <summary>Score at or above which a band is bucketed as strong.</summary>
        public decimal StrongScore = 4m;
    }

    /// <summary>A measured base rate with the sample it came from attached.</summary>
    public sealed class BaseRate
    {
        public int Touches;
        public int Rejections;
        public int Zones;
        public int Extremes;

        public bool Sufficient;

        /// <summary>Rejections as a percentage of touches, 0-100.</summary>
        public decimal RejectPercent;

        /// <summary>How often a band of this kind held the session extreme, 0-100.</summary>
        public decimal ExtremePercent;

        public string Bucket = string.Empty;

        /// <summary>What the panel prints. Never a bare percentage -- always the sample with it.</summary>
        public string Text
        {
            get
            {
                if (!Sufficient)
                    return Touches + " prior touches" + (Zones > 0 ? " of " + Zones + " bands" : "") +
                           " -- too few to quote a rate";

                return Rejections + "/" + Touches + " held (" +
                       RejectPercent.ToString("F0", CultureInfo.InvariantCulture) + "%)" +
                       ", top-ticked " + ExtremePercent.ToString("F0", CultureInfo.InvariantCulture) + "%";
            }
        }
    }

    /// <summary>
    /// Measures what actually happened at bands like this one, on this instrument, over the
    /// history loaded on this chart.
    ///
    /// The reason this exists rather than a confidence score: a weighted sum of level families is
    /// an opinion about which levels ought to matter. It has never been checked against anything.
    /// Replaying the same construction over prior sessions and counting how often price turned is
    /// the only statement here that has been. Where the sample is too thin to support a number,
    /// nothing is quoted -- the sample size is reported instead, which is the honest answer.
    ///
    /// What it is NOT: a forecast, or a probability in any forward-looking sense. It is a base
    /// rate over a small, recent, single-instrument sample, and the panel says so.
    /// </summary>
    public static class BaseRateEngine
    {
        /// <summary>
        /// Replays one band against one historical session.
        ///
        /// Direction matters to what counts as a rejection. A support band is approached from
        /// above, so it held if price got back UP off it; a resistance band is the mirror. Getting
        /// this backwards would score every break as a hold.
        /// </summary>
        public static TouchEvent Measure(IList<BarFacts> bars, int firstBar, int lastBar,
                                         decimal low, decimal high, bool isLong,
                                         decimal sessionHigh, decimal sessionLow, StatsConfig cfg)
        {
            var e = new TouchEvent
            {
                IsLong = isLong,
                Result = TouchResult.NotTouched,
                MarkedExtreme = isLong
                    ? sessionLow >= low && sessionLow <= high
                    : sessionHigh >= low && sessionHigh <= high
            };

            if (bars == null || cfg == null) return e;
            if (firstBar < 0 || lastBar >= bars.Count || firstBar > lastBar) return e;

            var rejectAt = isLong ? high + cfg.RejectPts : low - cfg.RejectPts;
            var acceptAt = isLong ? low - cfg.AcceptPts : high + cfg.AcceptPts;

            var touched = false;

            for (var i = firstBar; i <= lastBar; i++)
            {
                var bar = bars[i];

                if (!touched)
                {
                    if (bar.Low > high || bar.High < low) continue;
                    touched = true;
                    e.Result = TouchResult.Unresolved;
                }

                var accepted = isLong ? bar.Low <= acceptAt : bar.High >= acceptAt;
                var rejected = isLong ? bar.High >= rejectAt : bar.Low <= rejectAt;

                // Acceptance is checked first. A bar that ran both ways cannot say which came
                // first, and counting it as a hold would inflate the very rate this exists to
                // measure -- the error would always point the same way.
                if (accepted) { e.Result = TouchResult.Accepted; return e; }
                if (rejected) { e.Result = TouchResult.Rejected; return e; }
            }

            return e;
        }

        /// <summary>
        /// The base rate for bands like this one: same strength bucket, same magnet status.
        ///
        /// Bucketing on those two and nothing else is deliberate. Every extra split halves the
        /// sample, and at thirty sessions of history there is not enough data to support a finer
        /// cut without reporting noise.
        /// </summary>
        public static BaseRate Summarise(IList<TouchEvent> events, decimal score, bool hasMagnet,
                                         StatsConfig cfg)
        {
            var rate = new BaseRate
            {
                Bucket = (score >= cfg.StrongScore ? "x" + Trim(cfg.StrongScore) + "+" : "under x" + Trim(cfg.StrongScore)) +
                         (hasMagnet ? " with magnet" : " no magnet")
            };

            if (events == null) return rate;

            var strong = score >= cfg.StrongScore;

            foreach (var e in events)
            {
                if (e.Score >= cfg.StrongScore != strong) continue;
                if (e.HasMagnet != hasMagnet) continue;

                rate.Zones++;
                if (e.MarkedExtreme) rate.Extremes++;

                if (e.Result == TouchResult.Rejected) { rate.Touches++; rate.Rejections++; }
                else if (e.Result == TouchResult.Accepted) rate.Touches++;
            }

            rate.Sufficient = rate.Touches >= cfg.MinSample;

            if (rate.Touches > 0)
                rate.RejectPercent = Math.Round(rate.Rejections * 100m / rate.Touches, 0);

            if (rate.Zones > 0)
                rate.ExtremePercent = Math.Round(rate.Extremes * 100m / rate.Zones, 0);

            return rate;
        }

        private static string Trim(decimal v)
        {
            return v == Math.Truncate(v)
                ? v.ToString("F0", CultureInfo.InvariantCulture)
                : v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    #endregion

    #region Pierce

    public static class Pierce
    {
        /// <summary>
        /// Has price traded at, or far enough through, <paramref name="price"/> between two bars?
        ///
        /// Bar by bar, deliberately. The obvious implementation compares against each later
        /// SESSION's high and low, and it is wrong: a session that gapped over the level has a
        /// range spanning it while never having traded there. That error retires exactly the
        /// magnets that matter most, because a gap is what leaves a POC naked in the first place.
        ///
        /// <paramref name="beyond"/> of zero asks "was it touched"; a positive value asks
        /// "was it cleared by that much", which is what repairing a poor extreme requires.
        /// </summary>
        public static bool Any(IList<BarFacts> bars, int from, int to, decimal price,
                               decimal beyond, bool upward)
        {
            if (bars == null) return false;

            if (from < 0) from = 0;
            if (to >= bars.Count) to = bars.Count - 1;

            for (var i = from; i <= to; i++)
            {
                var bar = bars[i];

                var hit = beyond <= 0m
                    ? bar.Low <= price && price <= bar.High
                    : upward ? bar.High >= price + beyond : bar.Low <= price - beyond;

                if (hit) return true;
            }

            return false;
        }
    }

    #endregion

    #region Trade plan

    /// <summary>A band turned into prices you can actually type into a ticket.</summary>
    public sealed class TradePlan
    {
        public bool Valid;
        public bool IsShort;

        public decimal Entry;
        public decimal Stop;
        public decimal Target;

        public decimal RiskPts;
        public decimal RewardPts;

        /// <summary>Reward divided by risk. Zero when there is no target to run to.</summary>
        public decimal R;

        public string Problem;
    }

    public static class PlanMath
    {
        /// <summary>
        /// The top-tick / bottom-tick plan for a band.
        ///
        /// Entry is the FAR edge -- the top of a resistance band, the bottom of a support band --
        /// because that is the price the band is worth trading at. Entering mid-band gives up
        /// half the edge and moves the stop no closer.
        ///
        /// Every price is snapped to the instrument tick. A pivot lands wherever the arithmetic
        /// puts it, and 30456.83 is not an order.
        /// </summary>
        public static TradePlan Build(decimal low, decimal high, bool isShort, decimal tick,
                                      int stopTicks, decimal opposingTarget, bool hasTarget)
        {
            var plan = new TradePlan { IsShort = isShort };

            if (tick <= 0m) { plan.Problem = "no tick size"; return plan; }

            plan.Entry = Snap(isShort ? high : low, tick);
            plan.Stop = Snap(isShort ? high + stopTicks * tick : low - stopTicks * tick, tick);

            plan.RiskPts = Math.Abs(plan.Stop - plan.Entry);

            if (plan.RiskPts <= 0m) { plan.Problem = "zero risk -- widen the stop"; return plan; }

            if (!hasTarget)
            {
                plan.Valid = true;
                plan.Problem = "no band on the other side to run to";
                return plan;
            }

            plan.Target = Snap(opposingTarget, tick);
            plan.RewardPts = Math.Abs(plan.Target - plan.Entry);

            // A target on the wrong side of entry means the bands overlap or price has already
            // travelled through one of them. Report it rather than printing a negative R.
            var sane = isShort ? plan.Target < plan.Entry : plan.Target > plan.Entry;

            if (!sane)
            {
                plan.Valid = true;
                plan.Problem = "the opposing band is not on the other side of entry";
                plan.RewardPts = 0m;
                return plan;
            }

            plan.R = Math.Round(plan.RewardPts / plan.RiskPts, 1);
            plan.Valid = true;

            return plan;
        }

        public static decimal Snap(decimal price, decimal tick)
        {
            if (tick <= 0m) return price;
            return Math.Round(price / tick, 0, MidpointRounding.AwayFromZero) * tick;
        }
    }

    #endregion
}
