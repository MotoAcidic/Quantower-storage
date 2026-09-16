using System;

namespace OceansSma
{
    /// <summary>
    /// Simple moving average of the last N prices, fed one bar at a time.
    ///
    /// ATAS drives OnCalculate three different ways: forward one bar at a time while it walks
    /// history, repeatedly on the SAME bar as ticks land on it, and from the top after a
    /// recalculate. All three have to produce the identical number, so the running sum is only
    /// trusted when the bar index advances by exactly one. Any other jump reseeds the window
    /// straight from the price source, which costs Period reads and cannot drift.
    /// </summary>
    public sealed class RollingMean
    {
        private readonly Func<int, decimal> _source;
        private readonly decimal[] _ring;
        private decimal _sum;
        private int _filled;
        private int _next;
        private int _lastBar = int.MinValue;

        public RollingMean(int period, Func<int, decimal> source)
        {
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
            Period = period;
            _ring = new decimal[period];
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public int Period { get; }

        /// <summary>
        /// The average at <paramref name="bar"/>, or null while fewer than Period bars exist
        /// at or before it. A partial window is never averaged -- an "SMA 200" printed off
        /// 40 bars is a different number wearing the same name.
        /// </summary>
        public decimal? At(int bar)
        {
            if (bar < 0) return null;

            if (bar == _lastBar + 1) Push(_source(bar));
            else Seed(bar);

            _lastBar = bar;
            return _filled == Period ? _sum / Period : (decimal?)null;
        }

        private void Push(decimal price)
        {
            if (_filled == Period) _sum -= _ring[_next];
            else _filled++;

            _ring[_next] = price;
            _sum += price;
            _next = (_next + 1) % Period;
        }

        private void Seed(int bar)
        {
            Array.Clear(_ring, 0, Period);
            _sum = 0m;
            _filled = 0;
            _next = 0;

            var from = bar - Period + 1;
            if (from < 0) from = 0;

            for (var i = from; i <= bar; i++) Push(_source(i));
        }
    }
}
