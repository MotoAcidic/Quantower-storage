using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>Where price sits against one anchored VWAP.</summary>
    public enum VwapSide
    {
        Unknown,
        Above,
        At,
        Below
    }

    /// <summary>
    /// One anchored VWAP: the volume-weighted average price since the anchor opened, with
    /// standard-deviation bands around it.
    ///
    /// Anchors reset on their own key, so a yearly track only restarts in January and a weekly
    /// one only at the Sunday reopen. Whether the loaded history actually reaches back to the
    /// anchor is tracked separately: a yearly VWAP built from three weeks of bars is not a
    /// yearly VWAP, and it looks exactly like one.
    /// </summary>
    public sealed class VwapTrack
    {
        private decimal _pv;
        private decimal _v;
        private decimal _p2v;

        private readonly List<decimal> _history = new List<decimal>();
        private const int HistoryCap = 4096;

        public VwapAnchor Anchor;
        public DateTime Key;
        public DateTime AnchorStart;
        public bool Complete = true;
        public int Bars;

        public decimal Value;
        public decimal Sd;

        public bool Valid => _v > 0m && Value > 0m;

        public VwapTrack(VwapAnchor anchor)
        {
            Anchor = anchor;
        }

        /// <summary>Starts a new instance of the anchor. Everything accumulated is dropped.</summary>
        public void Reset(DateTime key, DateTime anchorStart, bool complete)
        {
            _pv = 0m;
            _v = 0m;
            _p2v = 0m;
            _history.Clear();

            Key = key;
            AnchorStart = anchorStart;
            Complete = complete;
            Bars = 0;
            Value = 0m;
            Sd = 0m;
        }

        /// <summary>
        /// One bar. Price is the bar's own volume-weighted price where the feed reports one,
        /// because the typical price of a bar is a worse estimate of where the volume actually
        /// traded, and on a wide bar it is wrong by most of the range.
        /// </summary>
        public void Add(decimal price, decimal volume)
        {
            if (price <= 0m || volume <= 0m) return;

            _pv += price * volume;
            _v += volume;
            _p2v += price * price * volume;
            Bars++;

            Value = _pv / _v;

            // Variance from the sums. Rounding can push a genuinely flat series a hair
            // negative, which is a zero deviation, not an error.
            var variance = _p2v / _v - Value * Value;
            Sd = variance <= 0m ? 0m : (decimal)Math.Sqrt((double)variance);

            _history.Add(Value);
            if (_history.Count > HistoryCap) _history.RemoveRange(0, _history.Count - HistoryCap);
        }

        /// <summary>The band n deviations out. Band(0) is the VWAP itself.</summary>
        public decimal Band(int n) => Value + Sd * n;

        /// <summary>
        /// Change in the VWAP over the last N bars, in price. A VWAP that is still rising is a
        /// different condition from one that has flattened at the same price, and the flattening
        /// is usually the earlier signal.
        /// </summary>
        public decimal Slope(int bars)
        {
            if (bars <= 0 || _history.Count < 2) return 0m;

            var back = _history.Count - 1 - bars;
            if (back < 0) back = 0;

            return _history[_history.Count - 1] - _history[back];
        }

        public VwapSide SideOf(decimal price, decimal tolerance)
        {
            if (!Valid) return VwapSide.Unknown;
            if (Math.Abs(price - Value) <= tolerance) return VwapSide.At;

            return price > Value ? VwapSide.Above : VwapSide.Below;
        }
    }

    /// <summary>The whole top-down stack, longest anchor first.</summary>
    public sealed class VwapStack
    {
        public static readonly VwapAnchor[] Order =
        {
            VwapAnchor.Year, VwapAnchor.Quarter, VwapAnchor.Month,
            VwapAnchor.Week, VwapAnchor.Day, VwapAnchor.Session
        };

        private readonly Dictionary<VwapAnchor, VwapTrack> _tracks =
            new Dictionary<VwapAnchor, VwapTrack>();

        public VwapStack()
        {
            foreach (var anchor in Order) _tracks[anchor] = new VwapTrack(anchor);
        }

        public VwapTrack this[VwapAnchor anchor] => _tracks[anchor];

        public void Clear()
        {
            foreach (var anchor in Order) _tracks[anchor] = new VwapTrack(anchor);
        }

        /// <summary>
        /// One bar into every anchor at once. The session anchor is driven by an explicit key
        /// from the caller because "session" means whichever window the profile is cut on, and
        /// that is a setting rather than a property of the calendar.
        /// </summary>
        public void Add(DateTime tradeDate, DateTime sessionKey, DateTime barTime,
                        decimal price, decimal volume, DateTime earliestBar)
        {
            foreach (var anchor in Order)
            {
                var track = _tracks[anchor];
                var key = anchor == VwapAnchor.Session ? sessionKey : ReadClock.AnchorKey(tradeDate, anchor);

                if (track.Bars == 0 || track.Key != key)
                {
                    var start = anchor == VwapAnchor.Session ? sessionKey : StartOf(key, anchor);

                    // Only the FIRST instance on the chart can be short of history; every later
                    // one began after the chart did, so it is whole by construction.
                    track.Reset(key, start, earliestBar <= start || track.Bars > 0);
                }

                track.Add(price, volume);
            }
        }

        private static DateTime StartOf(DateTime key, VwapAnchor anchor)
        {
            // Every anchor opens at the 5 PM reopen before its first trade date, not midnight.
            return ReadClock.OpenOf(key);
        }

        /// <summary>
        /// A compact reading of the whole stack: how many anchors price is above, and whether
        /// the anchors themselves line up in order. Stacked means every shorter anchor sits on
        /// the same side of every longer one -- the timeframes agreeing. A broken stack is the
        /// market disagreeing with itself, which is exactly the condition where a top-down read
        /// says wait.
        /// </summary>
        public string Describe(decimal price, decimal tolerance, out int above, out int below,
                               out int stack)
        {
            above = 0;
            below = 0;

            var parts = new List<string>();
            var previous = 0m;
            var seen = 0;
            var ascending = true;
            var descending = true;

            foreach (var anchor in Order)
            {
                var track = _tracks[anchor];
                if (!track.Valid) continue;

                var side = track.SideOf(price, tolerance);
                if (side == VwapSide.Above) above++;
                else if (side == VwapSide.Below) below++;

                var mark = side == VwapSide.Above ? "+" : side == VwapSide.Below ? "-" : "=";
                parts.Add(ReadClock.Tag(anchor) + mark + (track.Complete ? "" : "?"));

                // Order runs longest anchor first, so a market that has been going up all year
                // has each shorter VWAP ABOVE the one before it.
                if (seen > 0)
                {
                    if (track.Value < previous) ascending = false;
                    if (track.Value > previous) descending = false;
                }

                previous = track.Value;
                seen++;
            }

            stack = seen < 2 ? 0 : ascending ? 1 : descending ? -1 : 0;

            return parts.Count == 0 ? "no VWAP yet" : string.Join(" ", parts);
        }
    }
}
