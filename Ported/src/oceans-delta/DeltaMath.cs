using System;
using System.Collections.Generic;

namespace OceansDelta
{
    /// <summary>One traded price inside a bar -- an ATAS cluster, with no ATAS types attached.</summary>
    public struct PriceVolume
    {
        public decimal Price;
        public decimal Volume;

        /// <summary>Contracts that hit the bid: sellers paying up to get out.</summary>
        public decimal Bid;

        /// <summary>Contracts that lifted the ask: buyers paying up to get in.</summary>
        public decimal Ask;

        public decimal Delta => Ask - Bid;
    }

    /// <summary>What a cluster has to be before it counts as one.</summary>
    public struct ClusterFilter
    {
        /// <summary>Contracts traded at the level, total.</summary>
        public decimal MinVolume;

        /// <summary>How far apart the two sides have to be, in contracts.</summary>
        public decimal MinDelta;

        /// <summary>
        /// The same difference as a share of the level's own volume, 0-100. A 400-lot level split
        /// 210/190 is not one side doing anything; a 60-lot level split 55/5 is.
        /// </summary>
        public decimal MinLeanPercent;

        /// <summary>+1 keeps only levels the buyers won, -1 only the sellers, 0 keeps both.</summary>
        public int Sign;
    }

    /// <summary>A price level that passed the filter.</summary>
    public struct ClusterHit
    {
        public decimal Price;
        public decimal Volume;
        public decimal Bid;
        public decimal Ask;
        public decimal Delta;

        /// <summary>|Delta| as a share of Volume, 0-100.</summary>
        public decimal LeanPercent;
    }

    /// <summary>
    /// The cluster-search half of this indicator: which prices inside a bar carried size that was
    /// genuinely one-sided. Nothing here reads the order book -- it counts contracts that traded,
    /// and a level's lean is a fact about fills, not about what was resting.
    /// </summary>
    public static class ClusterSearch
    {
        public static List<ClusterHit> Find(IList<PriceVolume> levels, ClusterFilter filter)
        {
            var hits = new List<ClusterHit>();
            if (levels == null) return hits;

            for (var i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                if (level.Volume <= 0m) continue;
                if (level.Volume < filter.MinVolume) continue;

                var delta = level.Delta;
                var size = delta < 0m ? -delta : delta;
                if (size < filter.MinDelta) continue;

                if (filter.Sign > 0 && delta <= 0m) continue;
                if (filter.Sign < 0 && delta >= 0m) continue;

                var lean = size * 100m / level.Volume;
                if (lean < filter.MinLeanPercent) continue;

                var hit = new ClusterHit();
                hit.Price = level.Price;
                hit.Volume = level.Volume;
                hit.Bid = level.Bid;
                hit.Ask = level.Ask;
                hit.Delta = delta;
                hit.LeanPercent = lean;
                hits.Add(hit);
            }

            return hits;
        }

        /// <summary>
        /// The one that did the most work, by absolute delta. Ties keep the lower price so the
        /// same bar always answers the same way.
        /// </summary>
        public static bool Biggest(IList<ClusterHit> hits, out ClusterHit best)
        {
            best = new ClusterHit();
            if (hits == null || hits.Count == 0) return false;

            var found = false;

            for (var i = 0; i < hits.Count; i++)
            {
                var size = hits[i].Delta < 0m ? -hits[i].Delta : hits[i].Delta;
                var bestSize = best.Delta < 0m ? -best.Delta : best.Delta;

                if (!found || size > bestSize || (size == bestSize && hits[i].Price < best.Price))
                {
                    best = hits[i];
                    found = true;
                }
            }

            return found;
        }
    }

    /// <summary>A confirmed change of side in the session's cumulative delta.</summary>
    public struct Flip
    {
        /// <summary>The bar on which cumulative delta actually took the new sign.</summary>
        public int Bar;
        public DateTime Time;

        /// <summary>+1 the buyers took the session, -1 the sellers.</summary>
        public int Sign;

        /// <summary>Session delta entering and leaving the crossing bar.</summary>
        public decimal Before;
        public decimal After;

        /// <summary>The crossing bar's own delta.</summary>
        public decimal BarDelta;

        /// <summary>The later bar at which it had travelled far enough past zero to be believed.</summary>
        public int ConfirmBar;
        public DateTime ConfirmTime;
        public decimal ConfirmCum;

        /// <summary>
        /// This is the session choosing a side for the first time, not reversing one. Off by
        /// default, because every session would otherwise open with a cross on it.
        /// </summary>
        public bool FirstSide;

        /// <summary>
        /// Where in the crossing bar's NET delta zero was reached, 0-1. This is a fact about the
        /// order of contracts, not about price: bar data does not record which price traded first,
        /// so this must never be turned into a price.
        /// </summary>
        public decimal ShareOfBar;

        public int SessionStartBar;
    }

    /// <summary>Everything the readout needs about the session in progress.</summary>
    public struct SessionRead
    {
        public bool Open;
        public int StartBar;
        public DateTime StartTime;
        public int Bars;

        public decimal Cum;
        public decimal Peak;
        public int PeakBar;
        public decimal Trough;
        public int TroughBar;

        /// <summary>Last confirmed side: +1, -1, or 0 while the session has not committed.</summary>
        public int Side;

        /// <summary>A crossing is on the board but has not travelled far enough to be believed.</summary>
        public bool Arming;
        public int ArmBar;
        public int ArmSign;
        public decimal ArmCum;

        public int Flips;
        public int LastFlipBar;
    }

    /// <summary>
    /// Session delta, and the flips in it.
    ///
    /// Cumulative delta resets at every session boundary, so "the delta flipped for the session"
    /// means the running sum since this session opened changed sign -- not that one bar printed
    /// red after a green one.
    ///
    /// Two rules keep the cross off chop:
    ///
    /// 1. A crossing is ARMED, not accepted. It only becomes a flip once cumulative delta has
    ///    travelled <see cref="ConfirmContracts"/> past zero. A crossing that comes back before
    ///    that never happened.
    /// 2. The cross is anchored to the bar that CROSSED, not the bar that confirmed it. The
    ///    confirmation is evidence about a moment that already passed; drawing it late would put
    ///    the line in the wrong place.
    ///
    /// Fed bar by bar and safe to re-feed the forming bar: the state before the last bar is kept,
    /// so replaying it restores and re-applies rather than double-counting.
    /// </summary>
    public sealed class DeltaEngine
    {
        private struct State
        {
            public bool Open;
            public int StartBar;
            public DateTime StartTime;
            public int Bars;

            public decimal Cum;
            public decimal Peak;
            public int PeakBar;
            public decimal Trough;
            public int TroughBar;

            public int Side;

            public bool Arming;
            public int ArmBar;
            public DateTime ArmTime;
            public int ArmSign;
            public decimal ArmBefore;
            public decimal ArmAfter;
            public decimal ArmBarDelta;

            public int LastFlipBar;
            public int FlipCount;
        }

        private State _state;
        private State _snapshot;
        private int _appliedBar = -1;

        private readonly List<Flip> _flips = new List<Flip>();

        /// <summary>How far past zero cumulative delta must travel before a crossing is believed.</summary>
        public decimal ConfirmContracts { get; set; }

        /// <summary>
        /// A recross this soon after the last one is chop. The side still changes -- pretending it
        /// did not would leave the engine stuck facing the wrong way -- but no cross is drawn.
        /// </summary>
        public int MinBarsBetween { get; set; }

        /// <summary>Whether the session picking its first side counts as a flip. Normally it does not.</summary>
        public bool MarkFirstSide { get; set; }

        public DeltaEngine()
        {
            Reset();
        }

        public IReadOnlyList<Flip> Flips => _flips;

        public SessionRead Read
        {
            get
            {
                var read = new SessionRead();
                read.Open = _state.Open;
                read.StartBar = _state.StartBar;
                read.StartTime = _state.StartTime;
                read.Bars = _state.Bars;
                read.Cum = _state.Cum;
                read.Peak = _state.Peak;
                read.PeakBar = _state.PeakBar;
                read.Trough = _state.Trough;
                read.TroughBar = _state.TroughBar;
                read.Side = _state.Side;
                read.Arming = _state.Arming;
                read.ArmBar = _state.ArmBar;
                read.ArmSign = _state.ArmSign;
                read.ArmCum = _state.ArmAfter;
                read.Flips = _state.FlipCount;
                read.LastFlipBar = _state.LastFlipBar;
                return read;
            }
        }

        public void Reset()
        {
            _state = new State();
            _state.LastFlipBar = int.MinValue / 2;
            _snapshot = _state;
            _appliedBar = -1;
            _flips.Clear();
        }

        /// <summary>
        /// Adds one bar. Emits at most one flip, which is appended to <see cref="Flips"/> and
        /// returned. Re-feeding the bar last fed rewinds first, so the forming bar can be fed on
        /// every tick without the numbers drifting.
        /// </summary>
        public bool Feed(int bar, DateTime time, decimal barDelta, bool newSession, out Flip flip)
        {
            flip = new Flip();

            if (bar < 0) return false;

            if (bar == _appliedBar)
            {
                _state = _snapshot;
                if (_flips.Count > _state.FlipCount) _flips.RemoveRange(_state.FlipCount,
                                                                        _flips.Count - _state.FlipCount);
            }
            else
            {
                _snapshot = _state;
                _appliedBar = bar;
            }

            if (newSession || !_state.Open)
            {
                _state.Open = true;
                _state.StartBar = bar;
                _state.StartTime = time;
                _state.Bars = 0;
                _state.Cum = 0m;
                _state.Peak = 0m;
                _state.PeakBar = bar;
                _state.Trough = 0m;
                _state.TroughBar = bar;
                _state.Side = 0;
                _state.Arming = false;
                _state.LastFlipBar = int.MinValue / 2;
            }

            var before = _state.Cum;
            var after = before + barDelta;

            _state.Cum = after;
            _state.Bars++;

            if (after > _state.Peak) { _state.Peak = after; _state.PeakBar = bar; }
            if (after < _state.Trough) { _state.Trough = after; _state.TroughBar = bar; }

            return Step(bar, time, before, after, barDelta, out flip);
        }

        private bool Step(int bar, DateTime time, decimal before, decimal after, decimal barDelta,
                          out Flip flip)
        {
            flip = new Flip();

            var confirm = ConfirmContracts < 0m ? 0m : ConfirmContracts;
            var sign = after > 0m ? 1 : after < 0m ? -1 : 0;

            // Sitting exactly on zero settles nothing, and it must not disarm a crossing either:
            // a bar that lands on the line has not taken back the side.
            if (sign == 0) return false;

            var size = after < 0m ? -after : after;

            if (_state.Side != 0 && sign == _state.Side)
            {
                // Back on the established side. Whatever was arming did not stick.
                _state.Arming = false;
                return false;
            }

            if (!_state.Arming || _state.ArmSign != sign)
            {
                _state.Arming = true;
                _state.ArmBar = bar;
                _state.ArmTime = time;
                _state.ArmSign = sign;
                _state.ArmBefore = before;
                _state.ArmBarDelta = barDelta;
            }

            _state.ArmAfter = after;

            if (size < confirm) return false;

            var first = _state.Side == 0;
            var tooSoon = MinBarsBetween > 0 && _state.ArmBar - _state.LastFlipBar < MinBarsBetween;

            var armBar = _state.ArmBar;
            var armTime = _state.ArmTime;
            var armBefore = _state.ArmBefore;
            var armDelta = _state.ArmBarDelta;

            _state.Side = sign;
            _state.LastFlipBar = armBar;
            _state.Arming = false;

            if (tooSoon) return false;
            if (first && !MarkFirstSide) return false;

            flip.Bar = armBar;
            flip.Time = armTime;
            flip.Sign = sign;
            flip.Before = armBefore;
            flip.After = armBefore + armDelta;
            flip.BarDelta = armDelta;
            flip.ConfirmBar = bar;
            flip.ConfirmTime = time;
            flip.ConfirmCum = after;
            flip.FirstSide = first;
            flip.ShareOfBar = Share(armBefore, armDelta);
            flip.SessionStartBar = _state.StartBar;

            _state.FlipCount++;
            _flips.Add(flip);

            return true;
        }

        /// <summary>
        /// How much of the crossing bar's net delta had gone by when the running sum reached zero.
        /// Returns 0 when the bar started at zero, and clamps, because a bar whose net delta is
        /// smaller than the distance it had to cover cannot be described this way at all.
        /// </summary>
        public static decimal Share(decimal before, decimal barDelta)
        {
            if (barDelta == 0m) return 0m;

            var share = -before / barDelta;
            if (share < 0m) return 0m;
            if (share > 1m) return 1m;
            return share;
        }
    }

    /// <summary>Which price the horizontal arm of the cross is drawn at.</summary>
    public enum CrossPriceMode
    {
        /// <summary>
        /// The crossing bar's close: the price the market was at when the session delta was, on
        /// the record, flipped. The only one of these that is a fact rather than a reading.
        /// </summary>
        Close = 0,

        /// <summary>
        /// The price level inside the crossing bar that carried the most delta of the new side --
        /// the cluster that did the work. An interpretation: bar data does not record what traded
        /// first, so this is where the flip was PAID FOR, not where it happened.
        /// </summary>
        Cluster = 1
    }

    /// <summary>The price for the horizontal, and an account of where it came from.</summary>
    public struct CrossPrice
    {
        public decimal Price;
        public bool FromCluster;

        /// <summary>Short account for the label, e.g. "close" or "flip cluster".</summary>
        public string Note;
    }

    public static class CrossMath
    {
        /// <summary>
        /// Picks the price for the horizontal arm. In cluster mode a bar with no level carrying
        /// delta of the new side falls back to the close AND SAYS SO -- an unmarked fallback here
        /// would put a line at a price nothing supports.
        /// </summary>
        public static CrossPrice Price(CrossPriceMode mode, decimal close, IList<ClusterHit> hits)
        {
            var result = new CrossPrice();

            if (mode == CrossPriceMode.Cluster)
            {
                ClusterHit best;
                if (ClusterSearch.Biggest(hits, out best))
                {
                    result.Price = best.Price;
                    result.FromCluster = true;
                    result.Note = "flip cluster";
                    return result;
                }

                result.Price = close;
                result.FromCluster = false;
                result.Note = "close - no cluster carried it";
                return result;
            }

            result.Price = close;
            result.FromCluster = false;
            result.Note = "close";
            return result;
        }
    }

    /// <summary>
    /// A flip kept as a level after its session ended. The cross is a moment; the zone is what
    /// that moment left behind on the price axis, and it is the only part of a flip that is still
    /// worth anything two sessions later.
    /// </summary>
    public struct FlipZone
    {
        public int Session;
        public DateTime SessionStart;

        public int Bar;
        public DateTime Time;
        public int Sign;

        /// <summary>The cross price, which is where the label and any single line go.</summary>
        public decimal Price;

        /// <summary>The band. High == Low when only a price was available.</summary>
        public decimal High;
        public decimal Low;

        /// <summary>The band came from the span of qualifying clusters, not from the price alone.</summary>
        public bool FromClusters;

        /// <summary>Contracts in the qualifying clusters that made the band.</summary>
        public decimal Volume;

        /// <summary>Sessions whose zones this one stands for, once overlapping ones are merged.</summary>
        public int Sessions;

        public bool Overlaps(FlipZone other)
        {
            return Low <= other.High && other.Low <= High;
        }
    }

    public static class ZoneMath
    {
        /// <summary>
        /// The band a flip leaves behind: the span of the clusters that qualified inside the
        /// crossing bar. That is where the size which turned the session actually traded.
        ///
        /// With no qualifying clusters there is no span to take, and none is invented -- the zone
        /// collapses to the cross price and says so through <see cref="FlipZone.FromClusters"/>.
        /// Widening a bare price by some number of ticks would draw a band nothing traded in.
        /// </summary>
        public static FlipZone Zone(Flip flip, decimal crossPrice, IList<ClusterHit> hits,
                                    int session, DateTime sessionStart)
        {
            var zone = new FlipZone();
            zone.Session = session;
            zone.SessionStart = sessionStart;
            zone.Bar = flip.Bar;
            zone.Time = flip.Time;
            zone.Sign = flip.Sign;
            zone.Price = crossPrice;
            zone.High = crossPrice;
            zone.Low = crossPrice;
            zone.Sessions = 1;

            if (hits == null || hits.Count == 0) return zone;

            var high = hits[0].Price;
            var low = hits[0].Price;
            var volume = 0m;

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].Price > high) high = hits[i].Price;
                if (hits[i].Price < low) low = hits[i].Price;
                volume += hits[i].Volume;
            }

            // The cross price is the anchor, so the band has to contain it even when the price
            // came from the close and the clusters sat to one side of it.
            if (crossPrice > high) high = crossPrice;
            if (crossPrice < low) low = crossPrice;

            zone.High = high;
            zone.Low = low;
            zone.Volume = volume;
            zone.FromClusters = true;

            return zone;
        }

        /// <summary>
        /// Zones from different sessions that overlap are one level that has turned the tape more
        /// than once, and that is the whole reason to keep old sessions at all. Merged newest
        /// first, so the label and the side belong to the most recent flip and the count says how
        /// many sessions stand behind it.
        ///
        /// Zones from the SAME session are never merged into a count: two flips a few bars apart
        /// are one session changing its mind, not two sessions agreeing.
        /// </summary>
        public static List<FlipZone> Merge(IList<FlipZone> zones)
        {
            var merged = new List<FlipZone>();
            if (zones == null) return merged;

            for (var i = zones.Count - 1; i >= 0; i--)
            {
                var zone = zones[i];
                var absorbed = false;

                for (var j = 0; j < merged.Count; j++)
                {
                    var into = merged[j];
                    if (!into.Overlaps(zone)) continue;

                    if (into.High < zone.High) into.High = zone.High;
                    if (into.Low > zone.Low) into.Low = zone.Low;

                    into.Volume += zone.Volume;
                    if (zone.Session != into.Session) into.Sessions++;

                    // The session recorded on a merged zone is the OLDEST it covers, so "held
                    // since" reads correctly; the price and side stay with the newest flip.
                    if (zone.Session < into.Session) into.Session = zone.Session;
                    if (zone.SessionStart < into.SessionStart) into.SessionStart = zone.SessionStart;

                    merged[j] = into;
                    absorbed = true;
                    break;
                }

                if (!absorbed) merged.Add(zone);
            }

            return merged;
        }
    }
}
