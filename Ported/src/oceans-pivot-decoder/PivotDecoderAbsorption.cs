using System;
using System.Collections.Generic;

namespace OceansPivotDecoder
{
    public enum AbsorptionState
    {
        /// <summary>Price has not been in the band this session.</summary>
        Untested = 0,

        /// <summary>Price is in the band, or has been and has not resolved either way.</summary>
        Testing = 1,

        /// <summary>Size was absorbed into the band and price rejected out of it.</summary>
        Validated = 2,

        /// <summary>Price accepted through the band. The level is gone.</summary>
        Failed = 3
    }

    /// <summary>
    /// One bar, reduced to what the absorption engine needs. Built on the platform side so the
    /// engine itself never touches an ATAS type and can be replayed against hand-built bars.
    /// </summary>
    public struct BarFacts
    {
        public decimal High;
        public decimal Low;
        public decimal Close;

        /// <summary>Ask-side minus bid-side volume for the bar.</summary>
        public decimal Delta;

        public decimal Volume;

        /// <summary>Largest single-price volume printed in the bar. Feeds the big-print flag.</summary>
        public decimal MaxLevelVolume;
    }

    public sealed class AbsorptionConfig
    {
        /// <summary>Multiple of average absolute delta that counts as real size. Adaptive.</summary>
        public decimal DeltaMultiplier = 1.5m;

        /// <summary>How many bars the average absolute delta is taken over.</summary>
        public int AverageBars = 20;

        /// <summary>How far price may push past the far edge and still count as held.</summary>
        public int MaxProgressTicks = 8;

        /// <summary>How far back out of the band price must close to confirm the rejection.</summary>
        public int RejectionConfirmTicks = 6;

        /// <summary>How far beyond the far edge counts as acceptance rather than a probe.</summary>
        public int AcceptanceTicks = 12;

        /// <summary>Bars that must close beyond before the band is written off.</summary>
        public int AcceptanceBars = 2;

        /// <summary>Percentile of recent per-price volumes that counts as a big print.</summary>
        public decimal ClusterPercentile = 90m;
    }

    /// <summary>What has happened to one band this session.</summary>
    public sealed class ZoneState
    {
        public AbsorptionState State = AbsorptionState.Untested;

        /// <summary>Which side the band was being traded as when the test began.</summary>
        public bool IsLong;

        /// <summary>Delta accumulated over the bars that were actually inside the band.</summary>
        public decimal CumulativeDelta;

        public decimal Volume;

        /// <summary>Furthest price pushed past the far edge, in ticks.</summary>
        public decimal ProgressTicks;

        /// <summary>A single price inside the band printed size in the top percentile.</summary>
        public bool ClusterPrint;

        /// <summary>The adaptive delta bar this test had to clear, kept for the readout.</summary>
        public decimal DeltaThreshold;

        public int TestingBar = -1;
        public int ResolvedBar = -1;

        private int _acceptanceRun;

        /// <summary>ABS with the direction of the rejection, or empty.</summary>
        public string Marker
        {
            get
            {
                if (State != AbsorptionState.Validated) return string.Empty;
                return IsLong ? "ABS UP" : "ABS DN";
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case AbsorptionState.Testing: return "TESTING";
                    case AbsorptionState.Validated: return "VALIDATED";
                    case AbsorptionState.Failed: return "FAILED";
                    default: return "UNTESTED";
                }
            }
        }

        internal int AcceptanceRun { get { return _acceptanceRun; } set { _acceptanceRun = value; } }
    }

    /// <summary>
    /// Decides whether a band was defended or given up.
    ///
    /// The distinction it is drawing: price reaching a level tells you nothing, because price
    /// reaches every level eventually. What separates a level that held from one that happened to
    /// be in the way is that size traded INTO it and price still came back out. Selling hitting
    /// bids all the way into a support band, no follow-through below it, then a close back above
    /// -- somebody was filled down there and the market could not push past them.
    ///
    /// So validation needs all four of these together, and each rules out a different way of being
    /// fooled by a chart that merely looks like a bounce:
    ///
    ///   size          |cumulative delta| clears an ADAPTIVE bar -- a multiple of recent average
    ///                 absolute delta, never a hardcoded contract count, which would be wrong the
    ///                 moment the session changed character and wrong again on another instrument
    ///   direction     the delta points INTO the band. Buying into support is not absorption,
    ///                 it is just buying; it is sellers being absorbed that matters
    ///   containment   price did not push more than MaxProgressTicks past the far edge. A band
    ///                 that gave up thirty ticks before turning did not hold, whatever came next
    ///   rejection     price closed back out on the rejection side. Without this the first three
    ///                 describe a level that is still being fought over, not one that won
    ///
    /// Replayed from scratch on every rebuild rather than updated incrementally. A repaint, a
    /// history reload or a reconnect cannot then leave half-finished state behind claiming a
    /// validation that never happened.
    /// </summary>
    public static class AbsorptionEngine
    {
        /// <summary>
        /// The adaptive size bar: a multiple of the average absolute per-bar delta over the
        /// trailing window. Returns zero when there is not enough history, which the caller must
        /// treat as "cannot validate" rather than as "everything qualifies".
        /// </summary>
        public static decimal DeltaThreshold(IList<BarFacts> bars, int upToBar, AbsorptionConfig cfg)
        {
            if (bars == null || cfg == null || cfg.AverageBars <= 0) return 0m;

            var first = upToBar - cfg.AverageBars + 1;
            if (first < 0) first = 0;
            if (upToBar >= bars.Count) upToBar = bars.Count - 1;
            if (upToBar < first) return 0m;

            var total = 0m;
            var count = 0;

            for (var i = first; i <= upToBar; i++)
            {
                total += Math.Abs(bars[i].Delta);
                count++;
            }

            if (count == 0) return 0m;

            return total / count * cfg.DeltaMultiplier;
        }

        /// <summary>
        /// The big-print bar: the given percentile of every per-price volume printed over the
        /// trailing window. Adaptive for the same reason the delta bar is.
        /// </summary>
        public static decimal ClusterThreshold(IList<decimal[]> levelVolumes, int upToBar,
                                               AbsorptionConfig cfg)
        {
            if (levelVolumes == null || cfg == null) return 0m;

            var first = upToBar - cfg.AverageBars + 1;
            if (first < 0) first = 0;
            if (upToBar >= levelVolumes.Count) upToBar = levelVolumes.Count - 1;
            if (upToBar < first) return 0m;

            var sample = new List<decimal>();

            for (var i = first; i <= upToBar; i++)
            {
                var row = levelVolumes[i];
                if (row == null) continue;

                foreach (var volume in row) if (volume > 0m) sample.Add(volume);
            }

            return ProfileMath.Percentile(sample, cfg.ClusterPercentile);
        }

        /// <summary>
        /// Replays one band against the session's bars. <paramref name="isLong"/> is the side the
        /// band is being read as: below price is a long, above is a short.
        /// </summary>
        public static ZoneState Run(IList<BarFacts> bars, int firstBar, int lastBar,
                                    decimal low, decimal high, bool isLong, decimal tick,
                                    decimal deltaThreshold, decimal clusterThreshold,
                                    AbsorptionConfig cfg)
        {
            var state = new ZoneState { IsLong = isLong, DeltaThreshold = deltaThreshold };

            if (bars == null || cfg == null || tick <= 0m) return state;
            if (firstBar < 0 || lastBar >= bars.Count || firstBar > lastBar) return state;

            var rejectionLevel = isLong
                ? high + cfg.RejectionConfirmTicks * tick
                : low - cfg.RejectionConfirmTicks * tick;

            var acceptanceLevel = isLong
                ? low - cfg.AcceptanceTicks * tick
                : high + cfg.AcceptanceTicks * tick;

            for (var i = firstBar; i <= lastBar; i++)
            {
                var bar = bars[i];

                var inside = bar.Low <= high && bar.High >= low;

                if (state.State == AbsorptionState.Untested)
                {
                    if (!inside) continue;

                    state.State = AbsorptionState.Testing;
                    state.TestingBar = i;
                }

                if (state.State != AbsorptionState.Testing) break;

                if (inside)
                {
                    state.CumulativeDelta += bar.Delta;
                    state.Volume += bar.Volume;

                    if (clusterThreshold > 0m && bar.MaxLevelVolume >= clusterThreshold)
                        state.ClusterPrint = true;

                    var progress = isLong ? (low - bar.Low) / tick : (bar.High - high) / tick;
                    if (progress > state.ProgressTicks) state.ProgressTicks = progress;
                }

                // Acceptance first: a bar that has closed well beyond the far edge cannot also be
                // the rejection close, and treating it as one would validate a band price has left.
                var beyond = isLong ? bar.Close < acceptanceLevel : bar.Close > acceptanceLevel;

                if (beyond)
                {
                    state.AcceptanceRun++;

                    if (state.AcceptanceRun >= cfg.AcceptanceBars)
                    {
                        state.State = AbsorptionState.Failed;
                        state.ResolvedBar = i;
                        break;
                    }

                    continue;
                }

                state.AcceptanceRun = 0;

                var rejected = isLong ? bar.Close >= rejectionLevel : bar.Close <= rejectionLevel;
                if (!rejected) continue;

                var absorbed = deltaThreshold > 0m && (isLong
                    ? state.CumulativeDelta <= -deltaThreshold
                    : state.CumulativeDelta >= deltaThreshold);

                var contained = state.ProgressTicks <= cfg.MaxProgressTicks;

                if (absorbed && contained)
                {
                    state.State = AbsorptionState.Validated;
                    state.ResolvedBar = i;
                    break;
                }
            }

            return state;
        }
    }
}
