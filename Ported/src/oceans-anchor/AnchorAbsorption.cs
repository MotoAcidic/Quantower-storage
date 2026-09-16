using System;
using System.Collections.Generic;

namespace OceansAnchor
{
    /// <summary>
    /// A cumulative trade, snapshotted off the platform's mutable object the instant it arrives.
    ///
    /// Never hold the ATAS CumulativeTrade across threads: the platform reuses and mutates it,
    /// so a reference read a millisecond later describes a different trade. Copying four fields
    /// is cheaper than the bug.
    /// </summary>
    public struct TradeSnapshot
    {
        public DateTime Time;
        public decimal FirstPrice;
        public decimal LastPrice;
        public decimal Volume;
        public Aggressor Direction;

        /// <summary>
        /// Identity for replace-don't-append. ATAS re-delivers the same in-flight trade through
        /// OnUpdateCumulativeTrade as it grows, and this build's CumulativeTrade has no IsEqual
        /// method, so the comparison is made here on the fields that do not change while a trade
        /// accumulates: when it started, where it started, and which way it is going.
        /// </summary>
        public bool SameTradeAs(TradeSnapshot o)
        {
            return Time == o.Time && FirstPrice == o.FirstPrice && Direction == o.Direction;
        }
    }

    /// <summary>Live-tape thresholds. All indicator settings.</summary>
    public sealed class AbsorptionRules
    {
        /// <summary>Contracts. An NQ number -- MNQ prints run 2-2.5x fatter and need their own.</summary>
        public decimal SizeFloor = 100m;

        public int ZoneBufferTicks = 8;

        /// <summary>Absorbed means no follow-through. More than this and it was not absorbed.</summary>
        public int MaxDisplacementTicks = 6;

        public int DisplacementWindowMs = 2000;

        /// <summary>Events inside this window stack into one higher-grade trigger.</summary>
        public int StackWindowSec = 90;
    }

    public static class TapeAbsorption
    {
        /// <summary>
        /// Whether a print is the right size, in the right place, going the right way.
        ///
        /// The side test is the one that gets written backwards. Support is absorption of
        /// AGGRESSIVE SELLING: someone is hitting the bid and the bid is not moving, which means
        /// a resting buyer is eating it. So a long setup wants Sell-side aggressors. Reading it
        /// the other way round produces a tool that fires on continuation and calls it a fade.
        /// </summary>
        public static bool Qualifies(TradeSnapshot t, Zone zone, decimal tickSize, AbsorptionRules r)
        {
            if (zone == null || r == null || tickSize <= 0m) return false;
            if (t.Volume < r.SizeFloor) return false;
            if (zone.Spent || zone.TooFar) return false;

            // Absorption without location is noise. This is the whole reason the zone engine
            // exists: a 400-lot print in the middle of nowhere is a trade, not a level.
            var buffer = tickSize * r.ZoneBufferTicks;
            if (!zone.ContainsWithBuffer(t.LastPrice, buffer)) return false;

            return SideMatches(t.Direction, zone.Side);
        }

        public static bool SideMatches(Aggressor direction, TestSide side)
        {
            return side == TestSide.SupportLong
                ? direction == Aggressor.Sell
                : direction == Aggressor.Buy;
        }

        /// <summary>
        /// How far price got in the aggressor's direction after the print, in ticks. Negative
        /// values are clamped away: price going the OTHER way is not displacement, it is the
        /// absorption working, and it must not net off against a later push.
        /// </summary>
        public static decimal Displacement(decimal printPrice, decimal extreme, Aggressor direction,
                                           decimal tickSize)
        {
            if (tickSize <= 0m) return 0m;

            var progress = direction == Aggressor.Sell
                ? printPrice - extreme
                : extreme - printPrice;

            return progress <= 0m ? 0m : progress / tickSize;
        }
    }

    /// <summary>
    /// Holds a qualifying print until the displacement window has elapsed, then decides whether
    /// it was actually absorbed.
    ///
    /// The delay is the entire test. At the moment a 300-lot print lands there is no way to tell
    /// absorption from initiative -- they look identical. Two seconds later they do not: one has
    /// gone nowhere and the other is six ticks away.
    /// </summary>
    public sealed class DisplacementWatch
    {
        private sealed class Pending
        {
            public AbsorptionEvent Event;
            public Zone Zone;
            public DateTime Deadline;

            /// <summary>Furthest price reached in the aggressor's direction since the print.</summary>
            public decimal Extreme;
        }

        private readonly List<Pending> _pending = new List<Pending>();

        public int Count { get { return _pending.Count; } }

        public void Clear() { _pending.Clear(); }

        public void Add(AbsorptionEvent evt, Zone zone, AbsorptionRules rules)
        {
            _pending.Add(new Pending
            {
                Event = evt,
                Zone = zone,
                Deadline = evt.Time.AddMilliseconds(rules.DisplacementWindowMs),
                Extreme = evt.Price
            });
        }

        /// <summary>Every later print moves the extreme, which is what the verdict is measured against.</summary>
        public void NotePrint(decimal price)
        {
            foreach (var p in _pending)
            {
                if (p.Event.Direction == Aggressor.Sell)
                {
                    if (price < p.Extreme) p.Extreme = price;
                }
                else
                {
                    if (price > p.Extreme) p.Extreme = price;
                }
            }
        }

        /// <summary>
        /// Resolves everything whose window has closed. Returns the ones that were absorbed,
        /// with DisplacementTicks filled in; the rest are dropped.
        /// </summary>
        public List<KeyValuePair<AbsorptionEvent, Zone>> Resolve(DateTime now, decimal tickSize,
                                                                 AbsorptionRules rules)
        {
            var absorbed = new List<KeyValuePair<AbsorptionEvent, Zone>>();

            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (now < p.Deadline) continue;

                _pending.RemoveAt(i);

                var ticks = TapeAbsorption.Displacement(p.Event.Price, p.Extreme, p.Event.Direction,
                                                        tickSize);
                p.Event.DisplacementTicks = ticks;

                if (ticks <= rules.MaxDisplacementTicks)
                    absorbed.Add(new KeyValuePair<AbsorptionEvent, Zone>(p.Event, p.Zone));
            }

            return absorbed;
        }
    }

    /// <summary>
    /// One closed bar reduced to the numbers the cluster path actually tests. Built on the ATAS
    /// side from the footprint; everything below is arithmetic on plain decimals so it can be
    /// checked in _test against bars whose shape is known.
    /// </summary>
    public struct BarFacts
    {
        public decimal Open, High, Low, Close, Volume, Delta;

        /// <summary>Volume held in the ExtremeTicks nearest the tested extreme.</summary>
        public decimal ExtremeVolume;

        /// <summary>Net delta in that same band. Negative at a low is the b-shape.</summary>
        public decimal ExtremeDelta;

        /// <summary>Where the event mark goes: the price that traded the most in this bar.</summary>
        public decimal MaxVolumePrice;

        public decimal Range { get { return High - Low; } }
    }

    /// <summary>Cluster-forensics thresholds. All indicator settings.</summary>
    public sealed class ClusterRules
    {
        public int DeltaLookback = 20;

        /// <summary>Percentile for the outlier delta. 10 for longs; the short side uses 100 - this.</summary>
        public decimal DeltaPercentile = 10m;

        public int ExtremeTicks = 8;
        public decimal ExtremePct = 30m;

        public int MinWickTicks = 6;

        /// <summary>Close must land in this share of the range, measured from the tested extreme.</summary>
        public decimal ClosePosPct = 50m;

        /// <summary>Tests that must pass. Three of four.</summary>
        public int MinScore = 3;
    }

    /// <summary>The four shape tests and how they scored.</summary>
    public struct ClusterScore
    {
        public bool DeltaOutlier;
        public bool ClosesBackInside;
        public bool ExtremeConcentration;
        public bool Wick;

        public decimal ClosePosPct;
        public decimal WickTicks;

        public int Total
        {
            get
            {
                var n = 0;
                if (DeltaOutlier) n++;
                if (ClosesBackInside) n++;
                if (ExtremeConcentration) n++;
                if (Wick) n++;
                return n;
            }
        }
    }

    public static class ClusterAbsorption
    {
        /// <summary>
        /// Scores a bar against the ATAS-blog absorption picture, made numeric: unproportionally
        /// high volume near the extreme, no price movement to show for it, and a close back
        /// inside. The b-shape at a low, the p-shape at a high.
        ///
        /// This is a RECONSTRUCTION, not the real signal. Footprint bid/ask sums cannot tell a
        /// resting buyer eating 300 lots from three separate buyers taking 100 each; the tape
        /// path can. What it buys is history: on chart load there is no tape to replay, so
        /// without this every past setup would render blank and the ten-session calibration
        /// would take ten sessions of sitting in front of the screen. Events from here are
        /// marked Cluster, drawn dimmer, and their agreement rate with the tape path is itself
        /// one of the numbers being calibrated.
        /// </summary>
        public static ClusterScore Score(BarFacts bar, IList<decimal> priorDeltas, TestSide side,
                                         Zone zone, decimal tickSize, ClusterRules r)
        {
            var s = new ClusterScore();
            if (tickSize <= 0m || r == null) return s;

            var isLong = side == TestSide.SupportLong;

            // 1. Delta outlier against the recent distribution, not an absolute number. A quiet
            //    morning and a CPI print have completely different delta scales, and a fixed
            //    threshold would fire constantly on one and never on the other.
            if (priorDeltas != null && priorDeltas.Count > 0)
            {
                var pct = isLong ? r.DeltaPercentile : 100m - r.DeltaPercentile;
                var cut = Stats.Percentile(priorDeltas, pct);

                s.DeltaOutlier = isLong ? bar.Delta <= cut : bar.Delta >= cut;
            }

            // 2. Close back inside: the rejection actually held into the bell.
            var range = bar.Range;
            if (range > 0m)
            {
                s.ClosePosPct = isLong
                    ? (bar.Close - bar.Low) * 100m / range
                    : (bar.High - bar.Close) * 100m / range;

                var inZone = isLong ? bar.Close >= zone.Bottom : bar.Close <= zone.Top;
                s.ClosesBackInside = s.ClosePosPct >= r.ClosePosPct && inZone;
            }

            // 3. Extreme concentration: the size sat AT the extreme and the extreme held. Volume
            //    spread evenly through the bar is participation, not a wall.
            if (bar.Volume > 0m)
            {
                var concentrated = bar.ExtremeVolume * 100m >= r.ExtremePct * bar.Volume;
                var rightWay = isLong ? bar.ExtremeDelta < 0m : bar.ExtremeDelta > 0m;

                s.ExtremeConcentration = concentrated && rightWay;
            }

            // 4. Wick: price was rejected from somewhere, visibly.
            var body = isLong ? Math.Min(bar.Open, bar.Close) : Math.Max(bar.Open, bar.Close);
            var wick = isLong ? body - bar.Low : bar.High - body;

            s.WickTicks = wick <= 0m ? 0m : wick / tickSize;
            s.Wick = s.WickTicks >= r.MinWickTicks;

            return s;
        }

        /// <summary>Whether a bar's low (long) or high (short) is in the zone at all.</summary>
        public static bool Touches(BarFacts bar, Zone zone, TestSide side, decimal tickSize,
                                   AbsorptionRules r)
        {
            if (zone == null || zone.Spent || zone.TooFar) return false;

            var buffer = tickSize * r.ZoneBufferTicks;
            var probe = side == TestSide.SupportLong ? bar.Low : bar.High;

            return zone.ContainsWithBuffer(probe, buffer);
        }
    }

    public static class Stats
    {
        /// <summary>
        /// Linear-interpolated percentile (the R-7 / numpy default), so a twenty-bar lookback
        /// does not quantise the 10th percentile onto whichever single bar happens to be second
        /// from the bottom.
        /// </summary>
        public static decimal Percentile(IList<decimal> values, decimal pct)
        {
            if (values == null || values.Count == 0) return 0m;

            var sorted = new List<decimal>(values);
            sorted.Sort();

            if (pct <= 0m) return sorted[0];
            if (pct >= 100m) return sorted[sorted.Count - 1];
            if (sorted.Count == 1) return sorted[0];

            var rank = (pct / 100m) * (sorted.Count - 1);
            var lower = (int)Math.Floor(rank);
            var upper = lower + 1;

            if (upper >= sorted.Count) return sorted[sorted.Count - 1];

            var frac = rank - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * frac;
        }
    }
}
