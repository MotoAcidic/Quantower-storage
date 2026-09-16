using System;
using System.Collections.Generic;

namespace OceansEffort
{
    /// <summary>What kind of market the last stretch of bars has been.</summary>
    public enum Regime
    {
        Unknown,

        /// <summary>
        /// The cage: price rotating inside value, buyers and sellers at fair value, nobody in
        /// control. A trend-following model loses here by design.
        /// </summary>
        Balance,

        /// <summary>Neither one thing nor the other. Not a reason to trade.</summary>
        Mixed,

        /// <summary>The auction is going somewhere and keeping what it takes.</summary>
        Trend
    }

    /// <summary>The regime and the two numbers behind it, so the verdict can be argued with.</summary>
    public struct RegimeRead
    {
        public Regime Regime;

        /// <summary>Share of closes that sat inside the value area, 0 to 1.</summary>
        public decimal InsideShare;

        /// <summary>
        /// How much of the range the auction converted into progress, 0 to 1. A market that
        /// travelled two hundred ticks and finished ten from where it started kept nothing.
        /// </summary>
        public decimal Efficiency;

        public decimal NetTicks;
        public decimal SpanTicks;

        public int From;
        public int To;

        public bool Tradeable { get { return Regime == Regime.Trend || Regime == Regime.Mixed; } }
    }

    public static class MarketRegime
    {
        /// <summary>
        /// Reads the regime from bars [from, to] against a value area.
        ///
        /// Two questions, both answered from what happened rather than from an oscillator. Did
        /// price stay inside value, and did any of the distance it covered turn into progress?
        /// Rotating inside value while converting almost none of its range is the cage, and the
        /// whole point of naming it is to stand aside in it.
        ///
        /// Returns Unknown rather than guessing when the window is too short or the value area is
        /// not known -- standing aside on no information is a decision, and it should be a
        /// deliberate one, not an accident of a default.
        /// </summary>
        public static RegimeRead Read(IList<BarFacts> bars, int from, int to, decimal tick,
                                      decimal valueHigh, decimal valueLow, bool hasValue,
                                      decimal balanceShare, decimal balanceEfficiency,
                                      decimal trendEfficiency)
        {
            var read = new RegimeRead();
            read.Regime = Regime.Unknown;

            if (bars == null || tick <= 0m) return read;
            if (from < 0) from = 0;
            if (to > bars.Count - 1) to = bars.Count - 1;
            if (to - from < 2) return read;

            read.From = from;
            read.To = to;

            var high = decimal.MinValue;
            var low = decimal.MaxValue;
            var inside = 0;
            var counted = 0;

            for (var i = from; i <= to; i++)
            {
                if (bars[i].High > high) high = bars[i].High;
                if (bars[i].Low < low) low = bars[i].Low;

                counted++;
                if (hasValue && bars[i].Close >= valueLow && bars[i].Close <= valueHigh) inside++;
            }

            if (counted == 0 || high <= decimal.MinValue) return read;

            var net = bars[to].Close - bars[from].Open;
            if (net < 0m) net = -net;

            read.NetTicks = Math.Round(net / tick);
            read.SpanTicks = Math.Round((high - low) / tick);
            read.InsideShare = hasValue ? (decimal)inside / counted : 0m;
            read.Efficiency = read.SpanTicks <= 0m ? 0m : net / (high - low);

            if (!hasValue) return read;

            if (read.Efficiency >= trendEfficiency)
            {
                read.Regime = Regime.Trend;
                return read;
            }

            read.Regime = read.InsideShare >= balanceShare && read.Efficiency <= balanceEfficiency
                ? Regime.Balance
                : Regime.Mixed;

            return read;
        }

        /// <summary>
        /// Whether a setup may be taken in this regime.
        ///
        /// Unknown refuses when the filter is on. Not knowing what kind of market this is is not
        /// the same as knowing it is a good one, and the filter exists precisely to stop the model
        /// trading where it is known to lose.
        /// </summary>
        public static bool Allows(RegimeRead read, bool avoidBalance)
        {
            if (!avoidBalance) return true;

            return read.Regime == Regime.Trend || read.Regime == Regime.Mixed;
        }

        public static string Word(Regime regime)
        {
            switch (regime)
            {
                case Regime.Balance: return "BALANCE";
                case Regime.Mixed: return "MIXED";
                case Regime.Trend: return "TREND";
                default: return "UNREAD";
            }
        }
    }
}
