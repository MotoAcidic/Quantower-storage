using System;
using System.Collections.Generic;

namespace OceansEffort
{
    /// <summary>What produced a setup.</summary>
    public enum SetupKind
    {
        /// <summary>Value migration, effort and the bar's own aggression all pointing one way.</summary>
        Continuation,

        /// <summary>Price made a new extreme and the aggression did not follow it there.</summary>
        Divergence
    }

    /// <summary>
    /// A new extreme that the aggression refused to confirm: price printed a lower low than the
    /// swing behind it while cumulative delta held above where it stood at that swing. The sellers
    /// got the price and did not get paid for it.
    /// </summary>
    public struct Divergence
    {
        public bool Found;

        /// <summary>The side the divergence argues FOR: a failed new low is bullish.</summary>
        public Side Side;

        public int Bar;

        /// <summary>The extreme price just made, which is what has to break for this to be wrong.</summary>
        public decimal Extreme;

        /// <summary>The bar carrying the extreme it failed to confirm.</summary>
        public int AgainstBar;
        public decimal AgainstPrice;

        /// <summary>How much cumulative delta held back, in contracts.</summary>
        public decimal Gap;
    }

    public static class DeltaSignals
    {
        /// <summary>
        /// Running sum of bar delta. Written into a caller-owned array so the indicator can keep
        /// it per bar without this having to know how bars are stored.
        /// </summary>
        public static decimal Cumulate(IList<BarFacts> bars, int bar, decimal previous)
        {
            if (bars == null || bar < 0 || bar >= bars.Count) return previous;

            return previous + bars[bar].Delta;
        }

        /// <summary>
        /// Looks for a new extreme the aggression did not follow.
        ///
        /// The rule, for a low: this bar's low is below every low in the window behind it, but
        /// cumulative delta is HIGHER than it was at the bar that made the old low. Price found a
        /// worse price and the selling that got it there was smaller than the selling before. For
        /// a scalper that is the signal -- the side in control spent more and got less.
        ///
        /// minGap keeps a one-contract difference from counting: the two deltas have to be
        /// genuinely apart, not merely unequal.
        /// </summary>
        public static Divergence Find(IList<BarFacts> bars, IList<decimal> cumulative, int at,
                                      int lookback, decimal minGap)
        {
            var found = new Divergence();

            if (bars == null || cumulative == null) return found;
            if (at < 1 || at >= bars.Count || at >= cumulative.Count) return found;
            if (lookback < 2) lookback = 2;
            if (minGap < 0m) minGap = 0m;

            var from = at - lookback;
            if (from < 0) from = 0;
            if (at - from < 2) return found;

            var lowBar = -1;
            var highBar = -1;

            for (var i = from; i <= at - 1; i++)
            {
                if (lowBar < 0 || bars[i].Low < bars[lowBar].Low) lowBar = i;
                if (highBar < 0 || bars[i].High > bars[highBar].High) highBar = i;
            }

            if (lowBar < 0) return found;

            // A new low the selling did not pay for.
            if (bars[at].Low < bars[lowBar].Low)
            {
                var gap = cumulative[at] - cumulative[lowBar];
                if (gap >= minGap && gap > 0m)
                {
                    found.Found = true;
                    found.Side = Side.Buy;
                    found.Bar = at;
                    found.Extreme = bars[at].Low;
                    found.AgainstBar = lowBar;
                    found.AgainstPrice = bars[lowBar].Low;
                    found.Gap = gap;
                    return found;
                }
            }

            // A new high the buying did not pay for.
            if (bars[at].High > bars[highBar].High)
            {
                var gap = cumulative[highBar] - cumulative[at];
                if (gap >= minGap && gap > 0m)
                {
                    found.Found = true;
                    found.Side = Side.Sell;
                    found.Bar = at;
                    found.Extreme = bars[at].High;
                    found.AgainstBar = highBar;
                    found.AgainstPrice = bars[highBar].High;
                    found.Gap = gap;
                }
            }

            return found;
        }

        /// <summary>
        /// Turns a divergence into a trade. The stop goes beyond the extreme price just made --
        /// the one thing that must not break for the divergence to mean anything -- and the target
        /// is the same distance again.
        ///
        /// The location gate applies here exactly as it does to a continuation: a failed new low
        /// in mid-air is a statistic, the same failure on a shelf where sellers were already
        /// absorbed is a trade.
        /// </summary>
        public static bool ToSetup(Divergence divergence, BarFacts bar, Location where, bool requireLevel,
                                   decimal tick, int stopBufferTicks, int maxRiskTicks, out Setup setup)
        {
            setup = new Setup();

            if (!divergence.Found || tick <= 0m) return false;
            if (stopBufferTicks < 0) stopBufferTicks = 0;
            if (requireLevel && where.Favours != divergence.Side) return false;

            var buffer = tick * stopBufferTicks;

            if (divergence.Side == Side.Buy)
            {
                setup.Stop = divergence.Extreme - buffer;
                setup.Entry = bar.Close;
                if (setup.Entry <= setup.Stop) return false;

                setup.Target = setup.Entry + (setup.Entry - setup.Stop);
            }
            else
            {
                setup.Stop = divergence.Extreme + buffer;
                setup.Entry = bar.Close;
                if (setup.Entry >= setup.Stop) return false;

                setup.Target = setup.Entry - (setup.Stop - setup.Entry);
            }

            setup.Bar = bar.Bar;
            setup.Side = divergence.Side;
            setup.Kind = SetupKind.Divergence;
            setup.Where = where;
            setup.OverCap = maxRiskTicks > 0 && setup.Risk > tick * maxRiskTicks;

            return true;
        }
    }
}
