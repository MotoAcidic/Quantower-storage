using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>One completed rotation: the leg that ended at a pivot.</summary>
    public struct Swing
    {
        public int FromBar;
        public int PivotBar;
        public DateTime FromTime;
        public DateTime PivotTime;
        public decimal FromPrice;
        public decimal Price;
        public bool IsHigh;              // the pivot is a swing high, so the leg was an up leg

        public decimal Ticks;            // size of the leg in ticks, always positive
        public int Bars;
        public double Minutes;

        public int Direction => IsHigh ? 1 : -1;
    }

    /// <summary>How far through a typical rotation the leg in progress is.</summary>
    public enum LegMaturity
    {
        /// <summary>Not enough completed rotations to have a rhythm yet.</summary>
        Unknown,

        /// <summary>Young against the measured rhythm. This is the turn.</summary>
        AtTheTurn,

        /// <summary>Inside the usual band. Neither early nor late.</summary>
        Developing,

        /// <summary>Past three quarters of the rotations this market makes.</summary>
        Extended,

        /// <summary>Beyond nine rotations in ten. Chasing.</summary>
        Beyond
    }

    /// <summary>The measured cadence of the market: how far and how long a rotation usually runs.</summary>
    public sealed class Rhythm
    {
        public int Legs;                     // completed rotations the statistics rest on

        public decimal MedianTicks;
        public decimal QuarterTicks;         // 25th percentile
        public decimal ThreeQuarterTicks;    // 75th percentile
        public decimal NinetyTicks;

        public double MedianMinutes;
        public double QuarterMinutes;
        public double ThreeQuarterMinutes;
        public double NinetyMinutes;

        public decimal MedianUpTicks;
        public decimal MedianDownTicks;

        public bool Valid => Legs > 0;

        /// <summary>
        /// Whether the two sides run to different sizes. A market whose up rotations are half
        /// the size of its down rotations is not symmetric, and a single median hides that.
        /// </summary>
        public bool Lopsided
        {
            get
            {
                if (MedianUpTicks <= 0m || MedianDownTicks <= 0m) return false;

                var big = Math.Max(MedianUpTicks, MedianDownTicks);
                var small = Math.Min(MedianUpTicks, MedianDownTicks);

                return big >= small * 1.5m;
            }
        }

        private decimal[] _ticks = new decimal[0];
        private double[] _minutes = new double[0];

        internal void SetSamples(decimal[] ticks, double[] minutes)
        {
            _ticks = ticks ?? new decimal[0];
            _minutes = minutes ?? new double[0];
        }

        /// <summary>The share of completed rotations no larger than this, 0 to 1.</summary>
        public double TickPercentile(decimal ticks) => RhythmMath.Percentile(_ticks, ticks);

        public double MinutePercentile(double minutes) => RhythmMath.Percentile(_minutes, minutes);
    }

    /// <summary>What the leg in progress looks like against the measured rhythm.</summary>
    public struct LegRead
    {
        public bool Active;
        public bool Up;
        public decimal Ticks;
        public double Minutes;
        public decimal From;
        public decimal To;
        public int FromBar;

        public double TickPercentile;
        public double MinutePercentile;
        public LegMaturity Maturity;
    }

    /// <summary>
    /// Turns bars into rotations, causally.
    ///
    /// A pivot is confirmed only once price has pulled back from it by the reversal threshold,
    /// which means the pivot bar is always in the past when it is emitted -- that is honest,
    /// and it is why the leg in progress is reported separately as provisional rather than
    /// being counted in the statistics.
    ///
    /// The reversal has to happen on a bar AFTER the extreme. Allowing the same bar to both set
    /// an extreme and confirm the reversal reads an outside bar as a completed rotation, and
    /// bar data does not record which end it traded first.
    /// </summary>
    public sealed class SwingTracker
    {
        private readonly List<Swing> _swings = new List<Swing>();

        private int _dir;                 // +1 tracking a high, -1 tracking a low, 0 undecided
        private decimal _extreme;
        private int _extremeBar = -1;
        private DateTime _extremeTime;

        private decimal _anchorPrice;
        private int _anchorBar = -1;
        private DateTime _anchorTime;

        private decimal _seedHigh;
        private decimal _seedLow;
        private int _seedHighBar = -1;
        private int _seedLowBar = -1;
        private DateTime _seedHighTime;
        private DateTime _seedLowTime;

        public IReadOnlyList<Swing> Swings => _swings;

        /// <summary>The extreme the leg in progress has reached so far.</summary>
        public decimal ProvisionalPrice => _extreme;
        public int ProvisionalBar => _extremeBar;
        public DateTime ProvisionalTime => _extremeTime;
        public int Direction => _dir;
        public decimal AnchorPrice => _anchorPrice;
        public int AnchorBar => _anchorBar;
        public DateTime AnchorTime => _anchorTime;

        public void Clear()
        {
            _swings.Clear();
            _dir = 0;
            _extremeBar = -1;
            _anchorBar = -1;
            _seedHighBar = -1;
            _seedLowBar = -1;
        }

        /// <summary>
        /// One bar. The threshold is supplied per bar rather than fixed so it can track the
        /// market getting faster or slower without the caller rebuilding the whole series.
        /// </summary>
        public void Add(int bar, DateTime time, decimal high, decimal low, decimal tickSize,
                        decimal reversalTicks)
        {
            if (tickSize <= 0m || reversalTicks <= 0m) return;

            var threshold = reversalTicks * tickSize;

            if (_dir == 0)
            {
                Seed(bar, time, high, low, threshold);
                return;
            }

            if (_dir > 0)
            {
                // A bar that SET the extreme cannot also confirm the reversal away from it.
                // An outside bar does both on paper, and bar data does not record which end it
                // traded first, so reading it as a completed rotation invents one.
                var extended = high > _extreme;

                if (extended)
                {
                    _extreme = high;
                    _extremeBar = bar;
                    _extremeTime = time;
                }

                if (!extended && _extreme - low >= threshold)
                {
                    Emit(true, tickSize);
                    StartLeg(-1, bar, time, low);
                }

                return;
            }

            var deepened = low < _extreme;

            if (deepened)
            {
                _extreme = low;
                _extremeBar = bar;
                _extremeTime = time;
            }

            if (!deepened && high - _extreme >= threshold)
            {
                Emit(false, tickSize);
                StartLeg(1, bar, time, high);
            }
        }

        /// <summary>
        /// Before the first pivot there is no direction to track, so both extremes are carried
        /// until one of them travels far enough from the other to say which way the first leg
        /// ran. Picking a direction from the first bar instead would put a fictional pivot at
        /// whatever price the chart happened to start on.
        /// </summary>
        private void Seed(int bar, DateTime time, decimal high, decimal low, decimal threshold)
        {
            if (_seedHighBar < 0 || high > _seedHigh)
            {
                _seedHigh = high;
                _seedHighBar = bar;
                _seedHighTime = time;
            }

            if (_seedLowBar < 0 || low < _seedLow)
            {
                _seedLow = low;
                _seedLowBar = bar;
                _seedLowTime = time;
            }

            if (_seedHigh - _seedLow < threshold) return;

            // Whichever extreme came LAST is where the leg in progress is heading.
            if (_seedHighBar > _seedLowBar)
            {
                _anchorPrice = _seedLow;
                _anchorBar = _seedLowBar;
                _anchorTime = _seedLowTime;

                _dir = 1;
                _extreme = _seedHigh;
                _extremeBar = _seedHighBar;
                _extremeTime = _seedHighTime;
            }
            else
            {
                _anchorPrice = _seedHigh;
                _anchorBar = _seedHighBar;
                _anchorTime = _seedHighTime;

                _dir = -1;
                _extreme = _seedLow;
                _extremeBar = _seedLowBar;
                _extremeTime = _seedLowTime;
            }
        }

        private void Emit(bool isHigh, decimal tickSize)
        {
            var swing = new Swing
            {
                FromBar = _anchorBar,
                FromTime = _anchorTime,
                FromPrice = _anchorPrice,
                PivotBar = _extremeBar,
                PivotTime = _extremeTime,
                Price = _extreme,
                IsHigh = isHigh,
                Ticks = Math.Abs(_extreme - _anchorPrice) / tickSize,
                Bars = _extremeBar - _anchorBar,
                Minutes = (_extremeTime - _anchorTime).TotalMinutes
            };

            _swings.Add(swing);

            _anchorPrice = _extreme;
            _anchorBar = _extremeBar;
            _anchorTime = _extremeTime;
        }

        private void StartLeg(int dir, int bar, DateTime time, decimal price)
        {
            _dir = dir;
            _extreme = price;
            _extremeBar = bar;
            _extremeTime = time;
        }
    }

    public static class RhythmMath
    {
        /// <summary>
        /// The measured cadence over the last N completed rotations. Fewer than
        /// <paramref name="minLegs"/> and it returns invalid rather than a median of three
        /// samples -- a rhythm read off a handful of legs is a number, not a rhythm, and every
        /// acceptance threshold downstream is scaled by it.
        /// </summary>
        public static Rhythm Measure(IReadOnlyList<Swing> swings, int lookback, int minLegs)
        {
            var rhythm = new Rhythm();
            if (swings == null || swings.Count == 0) return rhythm;

            var from = lookback > 0 && swings.Count > lookback ? swings.Count - lookback : 0;
            var count = swings.Count - from;
            if (count < minLegs) return rhythm;

            var ticks = new decimal[count];
            var minutes = new double[count];
            var ups = new List<decimal>();
            var downs = new List<decimal>();

            for (var i = 0; i < count; i++)
            {
                var swing = swings[from + i];

                ticks[i] = swing.Ticks;
                minutes[i] = swing.Minutes;

                if (swing.IsHigh) ups.Add(swing.Ticks);
                else downs.Add(swing.Ticks);
            }

            var sortedTicks = (decimal[])ticks.Clone();
            var sortedMinutes = (double[])minutes.Clone();
            Array.Sort(sortedTicks);
            Array.Sort(sortedMinutes);

            rhythm.Legs = count;
            rhythm.MedianTicks = Quantile(sortedTicks, 0.50);
            rhythm.QuarterTicks = Quantile(sortedTicks, 0.25);
            rhythm.ThreeQuarterTicks = Quantile(sortedTicks, 0.75);
            rhythm.NinetyTicks = Quantile(sortedTicks, 0.90);

            rhythm.MedianMinutes = Quantile(sortedMinutes, 0.50);
            rhythm.QuarterMinutes = Quantile(sortedMinutes, 0.25);
            rhythm.ThreeQuarterMinutes = Quantile(sortedMinutes, 0.75);
            rhythm.NinetyMinutes = Quantile(sortedMinutes, 0.90);

            var upArray = ups.ToArray();
            var downArray = downs.ToArray();
            Array.Sort(upArray);
            Array.Sort(downArray);

            rhythm.MedianUpTicks = upArray.Length == 0 ? 0m : Quantile(upArray, 0.50);
            rhythm.MedianDownTicks = downArray.Length == 0 ? 0m : Quantile(downArray, 0.50);

            rhythm.SetSamples(sortedTicks, sortedMinutes);

            return rhythm;
        }

        /// <summary>
        /// The leg in progress measured against the rhythm. Maturity takes the FURTHER of the
        /// two readings: a rotation that has run a long way in a short time is just as mature
        /// as one that has ground sideways for an hour, and taking the lower of the two would
        /// call both of them early.
        /// </summary>
        public static LegRead ReadLeg(SwingTracker tracker, Rhythm rhythm, DateTime now, decimal tickSize)
        {
            var read = new LegRead();
            if (tracker == null || tracker.Direction == 0 || tickSize <= 0m) return read;

            read.Active = true;
            read.Up = tracker.Direction > 0;
            read.From = tracker.AnchorPrice;
            read.To = tracker.ProvisionalPrice;
            read.FromBar = tracker.AnchorBar;
            read.Ticks = Math.Abs(read.To - read.From) / tickSize;
            read.Minutes = (now - tracker.AnchorTime).TotalMinutes;

            if (!rhythm.Valid)
            {
                read.Maturity = LegMaturity.Unknown;
                return read;
            }

            read.TickPercentile = rhythm.TickPercentile(read.Ticks);
            read.MinutePercentile = rhythm.MinutePercentile(read.Minutes);

            var worst = Math.Max(read.TickPercentile, read.MinutePercentile);

            if (worst > 0.90) read.Maturity = LegMaturity.Beyond;
            else if (worst > 0.70) read.Maturity = LegMaturity.Extended;
            else if (worst > 0.25) read.Maturity = LegMaturity.Developing;
            else read.Maturity = LegMaturity.AtTheTurn;

            return read;
        }

        /// <summary>
        /// Linear-interpolated quantile of an already-sorted array. Interpolated rather than
        /// nearest-rank because these numbers are thresholds, and a threshold that steps by a
        /// whole sample every time a leg completes makes the acceptance test flicker.
        /// </summary>
        public static decimal Quantile(decimal[] sorted, double q)
        {
            if (sorted == null || sorted.Length == 0) return 0m;
            if (sorted.Length == 1) return sorted[0];

            if (q <= 0) return sorted[0];
            if (q >= 1) return sorted[sorted.Length - 1];

            var position = q * (sorted.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = lower + 1;
            if (upper >= sorted.Length) return sorted[sorted.Length - 1];

            var weight = (decimal)(position - lower);

            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
        }

        public static double Quantile(double[] sorted, double q)
        {
            if (sorted == null || sorted.Length == 0) return 0d;
            if (sorted.Length == 1) return sorted[0];

            if (q <= 0) return sorted[0];
            if (q >= 1) return sorted[sorted.Length - 1];

            var position = q * (sorted.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = lower + 1;
            if (upper >= sorted.Length) return sorted[sorted.Length - 1];

            var weight = position - lower;

            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
        }

        /// <summary>The share of samples no larger than the value, 0 to 1. Empty samples say 0.</summary>
        public static double Percentile(decimal[] sorted, decimal value)
        {
            if (sorted == null || sorted.Length == 0) return 0d;

            var below = 0;
            for (var i = 0; i < sorted.Length; i++)
                if (sorted[i] <= value) below++;

            return (double)below / sorted.Length;
        }

        public static double Percentile(double[] sorted, double value)
        {
            if (sorted == null || sorted.Length == 0) return 0d;

            var below = 0;
            for (var i = 0; i < sorted.Length; i++)
                if (sorted[i] <= value) below++;

            return (double)below / sorted.Length;
        }

        /// <summary>
        /// Average true range in ticks, over the last N bars ending at <paramref name="bar"/>.
        /// The swing threshold is scaled by this so one setting works on a quiet overnight and
        /// on a payroll morning.
        /// </summary>
        public static decimal AtrTicks(IReadOnlyList<decimal> high, IReadOnlyList<decimal> low,
                                       IReadOnlyList<decimal> close, int bar, int length, decimal tickSize)
        {
            if (high == null || low == null || close == null) return 0m;
            if (tickSize <= 0m || length <= 0 || bar <= 0) return 0m;
            if (bar >= high.Count) bar = high.Count - 1;

            var from = Math.Max(1, bar - length + 1);
            var sum = 0m;
            var count = 0;

            for (var i = from; i <= bar; i++)
            {
                var range = high[i] - low[i];
                var upGap = Math.Abs(high[i] - close[i - 1]);
                var downGap = Math.Abs(low[i] - close[i - 1]);

                sum += Math.Max(range, Math.Max(upGap, downGap));
                count++;
            }

            return count == 0 ? 0m : sum / count / tickSize;
        }

        /// <summary>
        /// How much of the ground covered was net progress: total displacement over total
        /// travel, 0 to 1. This is the arithmetic behind balancing and imbalancing -- a market
        /// that travelled 400 ticks to end 40 higher was rotating, not trending, whatever the
        /// close-to-close change says.
        /// </summary>
        public static decimal Efficiency(IReadOnlyList<Swing> swings, int lookback)
        {
            if (swings == null || swings.Count < 2) return 0m;

            var from = lookback > 0 && swings.Count > lookback ? swings.Count - lookback : 0;

            var travel = 0m;
            for (var i = from; i < swings.Count; i++) travel += swings[i].Ticks;

            if (travel <= 0m) return 0m;

            var start = swings[from].FromPrice;
            var end = swings[swings.Count - 1].Price;
            var net = Math.Abs(end - start);
            var tickSize = swings[from].Ticks <= 0m
                         ? 0m
                         : Math.Abs(swings[from].Price - swings[from].FromPrice) / swings[from].Ticks;

            if (tickSize <= 0m) return 0m;

            return net / tickSize / travel;
        }
    }
}
