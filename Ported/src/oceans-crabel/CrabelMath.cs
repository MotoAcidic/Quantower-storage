using System;
using System.Collections.Generic;
using System.Linq;

namespace OceansCrabel
{
    /// <summary>
    /// One completed (or in-progress) RTH session, aggregated from intraday bars.
    /// </summary>
    public sealed class SessionBar
    {
        public DateTime Date;          // session calendar date, exchange-local
        public decimal Open;
        public decimal High;
        public decimal Low;
        public decimal Close;
        public int BarCount;
        public DateTime FirstBarLocal; // exchange-local time of first bar in session
        public DateTime LastBarLocal;  // exchange-local time of last bar in session
        public bool Complete;          // session window has fully elapsed
        public bool Valid;             // passed validation; only valid sessions feed the math
        public string InvalidReason;

        public decimal Range => High - Low;
    }

    /// <summary>
    /// Crabel pattern flags for a single session.
    ///
    /// Every field is nullable on purpose. A flag is null when there is not enough
    /// prior history to evaluate it. Returning false in that case would be a silent
    /// wrong answer -- "not a narrow day" and "cannot tell yet" are different claims.
    /// </summary>
    public sealed class CrabelFlags
    {
        public bool? Nr4;
        public bool? Nr7;
        public bool? Nr20;
        public bool? InsideDay;
        public bool? IdNr4;
        public bool? TwoBarNr20;
        public decimal? Clv;           // close location value, 0..1
        public decimal? RangePctOf20d; // range as % of 20-session average range
    }

    public static class CrabelMath
    {
        public const int StretchLookback = 10;

        /// <summary>
        /// The Stretch for session at <paramref name="index"/>: the average, over the
        /// <paramref name="lookback"/> sessions immediately BEFORE it, of the smaller of
        /// (high - open) and (open - low).
        ///
        /// Returns null when fewer than <paramref name="lookback"/> prior sessions exist.
        /// Callers must not substitute a default -- an ORB bracket built on a guessed
        /// Stretch is worse than no bracket.
        /// </summary>
        public static decimal? Stretch(IReadOnlyList<SessionBar> s, int index, int lookback = StretchLookback)
        {
            if (s == null || index < lookback || index > s.Count) return null;

            decimal sum = 0m;
            for (var i = index - lookback; i < index; i++)
            {
                var b = s[i];
                var upper = b.High - b.Open;
                var lower = b.Open - b.Low;
                sum += Math.Min(upper, lower);
            }

            return sum / lookback;
        }

        /// <summary>
        /// True when the range at <paramref name="index"/> is the narrowest of the
        /// trailing <paramref name="n"/> sessions (inclusive). Null if history is short.
        /// </summary>
        public static bool? IsNarrowestRange(IReadOnlyList<SessionBar> s, int index, int n)
        {
            if (s == null || index < 0 || index >= s.Count) return null;
            if (index - (n - 1) < 0) return null;

            var r = s[index].Range;
            for (var i = index - (n - 1); i < index; i++)
                if (s[i].Range <= r) return false;

            return true;
        }

        /// <summary>Inside day: range fully contained by the prior session's range.</summary>
        public static bool? IsInsideDay(IReadOnlyList<SessionBar> s, int index)
        {
            if (s == null || index < 1 || index >= s.Count) return null;
            var c = s[index];
            var p = s[index - 1];
            return c.High <= p.High && c.Low >= p.Low;
        }

        /// <summary>
        /// True when the combined 2-session range ending at <paramref name="index"/> is the
        /// narrowest such 2-session range over the trailing <paramref name="n"/> sessions.
        /// </summary>
        public static bool? IsTwoBarNarrowest(IReadOnlyList<SessionBar> s, int index, int n = 20)
        {
            if (s == null || index < 1 || index >= s.Count) return null;
            if (index - n < 0) return null;

            var cur = TwoBarRange(s, index);
            for (var i = index - n + 1; i < index; i++)
                if (TwoBarRange(s, i) <= cur) return false;

            return true;
        }

        private static decimal TwoBarRange(IReadOnlyList<SessionBar> s, int i)
        {
            var hi = Math.Max(s[i].High, s[i - 1].High);
            var lo = Math.Min(s[i].Low, s[i - 1].Low);
            return hi - lo;
        }

        /// <summary>Where the close sits inside the range: 0 = on the low, 1 = on the high.</summary>
        public static decimal? CloseLocationValue(SessionBar b)
        {
            if (b == null) return null;
            var r = b.Range;
            if (r <= 0m) return null;   // zero-range session: undefined, not 0.5
            return (b.Close - b.Low) / r;
        }

        /// <summary>Range at <paramref name="index"/> as a percentage of the trailing n-session average range.</summary>
        public static decimal? RangePctOfAverage(IReadOnlyList<SessionBar> s, int index, int n = 20)
        {
            if (s == null || index < 0 || index >= s.Count) return null;
            if (index - (n - 1) < 0) return null;

            decimal sum = 0m;
            for (var i = index - (n - 1); i <= index; i++) sum += s[i].Range;

            var avg = sum / n;
            if (avg <= 0m) return null;

            return s[index].Range / avg * 100m;
        }

        public static CrabelFlags Evaluate(IReadOnlyList<SessionBar> s, int index)
        {
            var nr4 = IsNarrowestRange(s, index, 4);
            var inside = IsInsideDay(s, index);

            return new CrabelFlags
            {
                Nr4 = nr4,
                Nr7 = IsNarrowestRange(s, index, 7),
                Nr20 = IsNarrowestRange(s, index, 20),
                InsideDay = inside,
                // ID/NR4 is only knowable when BOTH components are knowable.
                IdNr4 = (inside.HasValue && nr4.HasValue) ? (bool?)(inside.Value && nr4.Value) : null,
                TwoBarNr20 = IsTwoBarNarrowest(s, index, 20),
                Clv = CloseLocationValue(s[index]),
                RangePctOf20d = RangePctOfAverage(s, index, 20)
            };
        }
    }
}
