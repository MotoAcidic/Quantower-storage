using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>What the tape is saying about the structural read.</summary>
    public enum FlowVerdict
    {
        /// <summary>No footprint, no open interest, nothing to say. Not the same as disagreeing.</summary>
        Silent,

        /// <summary>Aggression and positioning line up with the move.</summary>
        Confirms,

        /// <summary>They do not. Effort without result, or positioning leaving as price goes.</summary>
        Diverges,

        /// <summary>Some of it agrees and some does not.</summary>
        Mixed
    }

    /// <summary>Cumulative delta sampled where a rotation turned.</summary>
    public struct PivotFlow
    {
        public bool IsHigh;
        public decimal Price;
        public decimal CumulativeDelta;
        public int Bar;
    }

    /// <summary>What the tape shows at the level under test.</summary>
    public struct LevelFlow
    {
        public bool Have;
        public decimal Volume;
        public decimal Bid;             // sellers aggressing
        public decimal Ask;             // buyers aggressing
        public decimal Delta => Ask - Bid;

        /// <summary>Buyers as a share of aggression at the level, 0 to 1.</summary>
        public double AskShare => Volume <= 0m || Bid + Ask <= 0m ? 0.5d : (double)(Ask / (Bid + Ask));
    }

    /// <summary>How open interest changed against how price moved.</summary>
    public enum OiRead
    {
        /// <summary>The feed is not publishing open interest on this chart.</summary>
        None,

        /// <summary>Price up, open interest up. New longs -- money coming in behind the move.</summary>
        NewLongs,

        /// <summary>Price up, open interest down. Short covering -- the move is fuelled by exits.</summary>
        ShortCovering,

        /// <summary>Price down, open interest up. New shorts.</summary>
        NewShorts,

        /// <summary>Price down, open interest down. Long liquidation.</summary>
        LongLiquidation,

        /// <summary>Neither moved enough to read.</summary>
        Flat
    }

    /// <summary>The whole order-flow reading, assembled at the moment of execution.</summary>
    public sealed class OrderflowRead
    {
        public bool HaveFootprint;

        public decimal BarDelta;
        public decimal SessionDelta;
        public decimal LegDelta;
        public decimal SessionVolume;

        public LevelFlow AtLevel;
        public string LevelName = string.Empty;

        public bool Absorption;
        public string AbsorptionWhy = string.Empty;

        public bool Divergence;
        public string DivergenceWhy = string.Empty;

        public OiRead Oi = OiRead.None;
        public decimal OiChange;

        public FlowVerdict Verdict = FlowVerdict.Silent;
        public string Why = string.Empty;
    }

    public static class OrderflowMath
    {
        /// <summary>
        /// The footprint within a few ticks of a price. A band rather than the exact tick
        /// because the level being defended is never traded to the tick -- taking only the
        /// exact price reports an empty level on a market that was fighting one tick above it.
        /// </summary>
        public static LevelFlow AtLevel(ReadProfile profile, decimal price, int radiusTicks)
        {
            var flow = new LevelFlow();
            if (profile == null || profile.Count == 0 || profile.TickSize <= 0m) return flow;

            var centre = profile.IndexOfPrice(price);
            var from = Math.Max(0, centre - radiusTicks);
            var to = Math.Min(profile.Count - 1, centre + radiusTicks);
            if (to < from) return flow;

            for (var i = from; i <= to; i++)
            {
                flow.Volume += profile.Levels[i].Volume;
                flow.Bid += profile.Levels[i].Bid;
                flow.Ask += profile.Levels[i].Ask;
            }

            flow.Have = flow.Volume > 0m;

            return flow;
        }

        /// <summary>
        /// Absorption: heavy, one-sided aggression at the level and no ground given up. Someone
        /// is filling it passively.
        ///
        /// All three conditions are needed. Heavy and one-sided with price moving away is just
        /// a breakout, and heavy with price stuck but two-sided is a normal fight.
        /// </summary>
        public static bool IsAbsorption(LevelFlow flow, decimal sessionVolume, decimal groundTicks,
                                        decimal heavyShare, double lopsided, decimal maxGroundTicks,
                                        out string why)
        {
            why = string.Empty;
            if (!flow.Have || sessionVolume <= 0m) return false;

            var share = flow.Volume / sessionVolume;
            if (share < heavyShare) return false;

            var askShare = flow.AskShare;
            var oneSided = askShare >= lopsided || askShare <= 1d - lopsided;
            if (!oneSided) return false;

            if (Math.Abs(groundTicks) > maxGroundTicks) return false;

            why = Format.Percent((double)share) + " of session volume, " +
                  Format.Percent(askShare) + " buyers, " +
                  Format.Ticks(Math.Abs(groundTicks)) + " of progress";

            return true;
        }

        /// <summary>
        /// Open interest against price. The four readings are the standard ones: what matters
        /// is whether the move is being funded by new positions or by old ones leaving.
        /// </summary>
        public static OiRead ReadOpenInterest(decimal oiChange, decimal priceChangeTicks,
                                              decimal minOi, decimal minTicks)
        {
            if (Math.Abs(oiChange) < minOi || Math.Abs(priceChangeTicks) < minTicks) return OiRead.Flat;

            if (priceChangeTicks > 0m) return oiChange > 0m ? OiRead.NewLongs : OiRead.ShortCovering;

            return oiChange > 0m ? OiRead.NewShorts : OiRead.LongLiquidation;
        }

        public static string Describe(OiRead read)
        {
            switch (read)
            {
                case OiRead.NewLongs: return "new longs";
                case OiRead.ShortCovering: return "short covering";
                case OiRead.NewShorts: return "new shorts";
                case OiRead.LongLiquidation: return "long liquidation";
                case OiRead.Flat: return "flat";
                default: return "not published";
            }
        }

        /// <summary>Whether an open-interest reading backs a move in the given direction.</summary>
        public static bool Backs(OiRead read, bool up)
        {
            if (up) return read == OiRead.NewLongs;

            return read == OiRead.NewShorts;
        }

        /// <summary>
        /// Delta divergence at the turn: a new extreme in price that the tape did not pay for.
        ///
        /// Compares the last two rotations that ended the SAME way. A higher high made on less
        /// cumulative delta than the previous high is buying that is not there any more.
        /// </summary>
        public static bool Divergence(IReadOnlyList<PivotFlow> pivots, bool wantHighs, out string why)
        {
            why = string.Empty;
            if (pivots == null || pivots.Count < 2) return false;

            PivotFlow? last = null;
            PivotFlow? previous = null;

            for (var i = pivots.Count - 1; i >= 0; i--)
            {
                if (pivots[i].IsHigh != wantHighs) continue;

                if (last == null) last = pivots[i];
                else { previous = pivots[i]; break; }
            }

            if (last == null || previous == null) return false;

            var a = previous.Value;
            var b = last.Value;

            if (wantHighs)
            {
                if (b.Price <= a.Price) return false;
                if (b.CumulativeDelta >= a.CumulativeDelta) return false;

                why = "higher high on less delta (" + Format.Signed(a.CumulativeDelta) +
                      " then " + Format.Signed(b.CumulativeDelta) + ")";

                return true;
            }

            if (b.Price >= a.Price) return false;
            if (b.CumulativeDelta <= a.CumulativeDelta) return false;

            why = "lower low on less selling (" + Format.Signed(a.CumulativeDelta) +
                  " then " + Format.Signed(b.CumulativeDelta) + ")";

            return true;
        }

        /// <summary>
        /// The order-flow verdict on a move in a given direction.
        ///
        /// Silence is a distinct answer from disagreement. A chart with no footprint loaded has
        /// not told us the flow is against the trade, and reporting "no confirmation" as though
        /// it were "contradicted" is the kind of quiet wrongness that costs money.
        /// </summary>
        public static FlowVerdict Judge(OrderflowRead flow, bool up, decimal minDelta, out string why)
        {
            why = string.Empty;
            if (flow == null) return FlowVerdict.Silent;

            var votes = new List<string>();
            var forCount = 0;
            var againstCount = 0;

            if (flow.HaveFootprint && Math.Abs(flow.LegDelta) >= minDelta)
            {
                var agrees = up ? flow.LegDelta > 0m : flow.LegDelta < 0m;
                votes.Add("leg delta " + Format.Signed(flow.LegDelta));

                if (agrees) forCount++;
                else againstCount++;
            }

            if (flow.Divergence)
            {
                votes.Add(flow.DivergenceWhy);
                againstCount++;
            }

            if (flow.Oi != OiRead.None && flow.Oi != OiRead.Flat)
            {
                votes.Add("OI " + Format.Signed(flow.OiChange) + ", " + Describe(flow.Oi));

                if (Backs(flow.Oi, up)) forCount++;
                else againstCount++;
            }

            if (flow.Absorption)
            {
                // Absorption is against whoever is doing the aggressing: the passive side is
                // winning. Which way that cuts depends on who is hitting.
                var buyersAggressing = flow.AtLevel.AskShare > 0.5d;
                votes.Add("absorption at " + flow.LevelName);

                if (buyersAggressing == up) againstCount++;
                else forCount++;
            }

            why = votes.Count == 0 ? "no footprint or open interest on this chart" : string.Join("; ", votes);

            if (forCount == 0 && againstCount == 0) return FlowVerdict.Silent;
            if (againstCount == 0) return FlowVerdict.Confirms;
            if (forCount == 0) return FlowVerdict.Diverges;

            return FlowVerdict.Mixed;
        }

        public static string Describe(FlowVerdict verdict)
        {
            switch (verdict)
            {
                case FlowVerdict.Confirms: return "CONFIRMS";
                case FlowVerdict.Diverges: return "DIVERGES";
                case FlowVerdict.Mixed: return "MIXED";
                default: return "SILENT";
            }
        }
    }
}
