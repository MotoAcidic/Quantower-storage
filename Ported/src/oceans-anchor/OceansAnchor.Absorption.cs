using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ATAS.Indicators;
using Utils.Common.Logging;

namespace OceansAnchor
{
    public partial class OceansAnchor
    {
        #region Path A - live tape

        /// <summary>
        /// The worker exists for one reason: the tape callbacks run on the platform's data
        /// thread, and a burst through the cash open delivers thousands of trades a second. Any
        /// work done in the callback itself is work the chart is not drawing during.
        ///
        /// So the callback snapshots and queues, the worker applies the size floor, and every
        /// decision that touches a Zone happens back on the chart thread. That last part is what
        /// lets the whole state machine and the renderer run without a single lock between them.
        /// </summary>
        private void StartWorker()
        {
            StopWorker();

            _queue = new BlockingCollection<TradeSnapshot>(new ConcurrentQueue<TradeSnapshot>(), 200000);

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OceansAnchor.Tape",
                Priority = ThreadPriority.BelowNormal
            };

            _worker.Start();
        }

        private void StopWorker()
        {
            try
            {
                if (_queue != null) _queue.CompleteAdding();
                if (_worker != null && _worker.IsAlive) _worker.Join(2000);
            }
            catch { }
            finally
            {
                _worker = null;
                _queue = null;
            }
        }

        private void WorkerLoop()
        {
            var queue = _queue;
            if (queue == null) return;

            try
            {
                foreach (var trade in queue.GetConsumingEnumerable())
                {
                    // The only test done off the chart thread, because it is the one that throws
                    // away 99% of the tape and it costs nothing.
                    if (trade.Volume < SizeFloor) continue;

                    lock (_inboxLock)
                    {
                        // A stalled chart must not turn into unbounded memory. Dropping the
                        // oldest is right: absorption is about what is happening now, and a
                        // print from forty seconds ago has already been resolved or missed.
                        if (_inbox.Count > 20000) _inbox.RemoveRange(0, 10000);
                        _inbox.Add(trade);
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                this.LogWarn("Ocean's Anchor tape worker stopped: " + ex.Message);
            }
        }

        protected override void OnCumulativeTrade(CumulativeTrade trade) { Accept(trade); }

        protected override void OnUpdateCumulativeTrade(CumulativeTrade trade) { Accept(trade); }

        /// <summary>
        /// Replace-don't-append.
        ///
        /// ATAS re-delivers the same in-flight cumulative trade through OnUpdateCumulativeTrade
        /// as it grows, so appending every delivery counts one 400-lot trade as a 50, a 180 and
        /// a 400. Only when a DIFFERENT trade arrives is the previous one finished and safe to
        /// act on.
        ///
        /// The build spec calls for trade.IsEqual(prev) here. This install's CumulativeTrade has
        /// no such method, so identity is compared on the fields that do not change while a
        /// trade accumulates -- start time, start price, direction. See TradeSnapshot.
        /// </summary>
        private void Accept(CumulativeTrade trade)
        {
            if (trade == null || !TapeOn) return;

            var snap = Snapshot(trade);

            TradeSnapshot finished;
            bool haveFinished;

            lock (_inboxLock)
            {
                haveFinished = _haveLastTrade && !snap.SameTradeAs(_lastTrade);
                finished = _lastTrade;

                _lastTrade = snap;
                _haveLastTrade = true;
            }

            if (!haveFinished) return;

            var queue = _queue;
            if (queue == null || queue.IsAddingCompleted) return;

            try { queue.TryAdd(finished); }
            catch (Exception) { }
        }

        /// <summary>
        /// Snapshot immediately. The platform reuses and mutates the CumulativeTrade it hands
        /// out, so a reference read on another thread a millisecond later describes a different
        /// trade entirely.
        ///
        /// Lastprice, lowercase p. It is spelled that way in the SDK and it will bite.
        /// </summary>
        private static TradeSnapshot Snapshot(CumulativeTrade t)
        {
            return new TradeSnapshot
            {
                Time = t.Time,
                FirstPrice = t.FirstPrice,
                LastPrice = t.Lastprice,
                Volume = t.Volume,
                Direction = Map(t.Direction)
            };
        }

        private static Aggressor Map(TradeDirection d)
        {
            if (d == TradeDirection.Buy) return Aggressor.Buy;
            if (d == TradeDirection.Sell) return Aggressor.Sell;
            return Aggressor.Between;
        }

        /// <summary>
        /// Individual prints, used only to measure displacement. A rolling per-second tape
        /// volume could be built from the same stream and v2 wants one; v1 does not read it.
        /// </summary>
        protected override void OnNewTrade(MarketDataArg arg)
        {
            if (arg == null || !TapeOn) return;

            // Bounded by the chart thread draining it every tick. If the chart has stalled hard
            // enough for this to matter, displacement measurement is already meaningless.
            if (_prints.Count < 100000) _prints.Enqueue(arg.Price);
        }

        /// <summary>
        /// Chart thread. Everything queued by the tape becomes zone state here, and nowhere
        /// else.
        /// </summary>
        private void DrainTape(decimal tick)
        {
            decimal price;
            var drained = 0;
            while (drained++ < 200000 && _prints.TryDequeue(out price))
                _watch.NotePrint(price);

            List<TradeSnapshot> batch = null;
            lock (_inboxLock)
            {
                if (_inbox.Count > 0)
                {
                    batch = new List<TradeSnapshot>(_inbox);
                    _inbox.Clear();
                }
            }

            var rules = Tape();
            var liveBar = Math.Max(0, CurrentBar - 1);

            if (batch != null)
            {
                List<Zone> zones;
                lock (_sync) zones = _zones;

                if (zones != null)
                {
                    foreach (var trade in batch)
                    {
                        var zone = ZoneFor(zones, trade, tick, rules);
                        if (zone == null) continue;

                        var evt = new AbsorptionEvent
                        {
                            Time = trade.Time,
                            Price = trade.LastPrice,
                            Volume = trade.Volume,
                            Direction = trade.Direction,
                            Path = EventPath.Tape,
                            Bar = liveBar
                        };

                        _watch.Add(evt, zone, rules);
                    }
                }
            }

            var now = MarketTime.Year > 2000 ? MarketTime : DateTime.UtcNow;

            foreach (var pair in _watch.Resolve(now, tick, rules))
                Promote(pair.Value, pair.Key, tick);
        }

        /// <summary>
        /// The zone a print belongs to, or null. Only Armed and already-Triggered zones can take
        /// an event: absorption in a zone price has not actually reached is a print at a number,
        /// not a test of a level.
        /// </summary>
        private static Zone ZoneFor(List<Zone> zones, TradeSnapshot trade, decimal tick,
                                    AbsorptionRules rules)
        {
            foreach (var zone in zones)
            {
                if (zone.State != SignalState.Armed && zone.State != SignalState.Triggered) continue;
                if (!TapeAbsorption.Qualifies(trade, zone, tick, rules)) continue;

                return zone;
            }

            return null;
        }

        #endregion

        #region Promotion and episodes

        /// <summary>
        /// An absorbed print promotes its zone and opens (or extends) the episode that will
        /// become a CSV row.
        ///
        /// Stacking is not bookkeeping. Two 300-lot prints absorbed at the same shelf ninety
        /// seconds apart is a materially different event from one, because it says the size is
        /// still there after the first one got filled -- so it raises the grade and it widens
        /// the cluster, which widens the stop to where it actually belongs.
        /// </summary>
        private void Promote(Zone zone, AbsorptionEvent evt, decimal tick)
        {
            var bar = evt.Bar;
            var wasArmed = zone.State == SignalState.Armed;

            if (!_engine.Promote(zone, evt, bar)) return;

            var band = tick * Math.Max(1, ZoneBufferTicks) / 2m;
            zone.NoteCluster(evt.Price - band, evt.Price + band);

            // Both sides of this comparison have to be in the same frame. Episode.StartedLocal is
            // Houston time; evt.Time is whatever the feed stamps, which on a UTC feed is five or
            // six hours away. Comparing them raw made every event look stale, so nothing ever
            // stacked and the two-print setup -- the higher-grade one -- was logged as two
            // unrelated singles.
            var episode = zone.Live;
            var stale = episode != null &&
                        (LocalOf(evt.Time) - episode.StartedLocal).TotalSeconds > StackWindowSec;

            if (episode == null || stale || wasArmed)
            {
                episode = NewEpisode(zone, evt);
                zone.Live = episode;
            }
            else
            {
                episode.StackedEvents++;
                if (evt.Volume > episode.LargestTrade) episode.LargestTrade = evt.Volume;

                // A tape event always outranks a cluster reconstruction in the log: if the real
                // tape saw it, that is what the row should say the trigger was.
                if (evt.Path == EventPath.Tape) episode.Path = EventPath.Tape;
            }

            episode.DisplacementTicks = evt.DisplacementTicks;

            AddMark(zone, evt, tick);

            if (wasArmed) OnTriggered(zone, evt);
        }

        private Episode NewEpisode(Zone zone, AbsorptionEvent evt)
        {
            var local = LocalOf(evt.Time);

            return new Episode
            {
                StartedLocal = local,
                ZoneKind = zone.Kind.ToString(),
                Rank = zone.Rank,
                Side = zone.Side,
                ArrivalAtrMult = Arrival(evt.Price),
                LargestTrade = evt.Volume,
                StackedEvents = 1,
                Path = evt.Path,
                TouchDelta = _facts.Count > 0 ? _facts[_facts.Count - 1].Delta : 0m,
                DisplacementTicks = evt.DisplacementTicks,
                TriggerBar = evt.Bar
            };
        }

        /// <summary>
        /// How far the day had already travelled when the zone was reached, in ADRs. A shelf
        /// tested on the first push of the morning and the same shelf tested after a 1.4 ADR
        /// run are not the same trade, and the CSV has to be able to tell them apart offline.
        /// </summary>
        private decimal Arrival(decimal price)
        {
            if (_adr <= 0m || _sessionOpen <= 0m) return 0m;

            return Math.Abs(price - _sessionOpen) / _adr;
        }

        private DateTime LocalOf(DateTime stamp)
        {
            if (_clock == null || !_clock.Valid) return stamp;

            // Tape stamps come from the feed already in the platform's clock, so they take the
            // same reading the bars did.
            try { return _clock.ToLocal(stamp); }
            catch { return stamp; }
        }

        /// <summary>Closes an episode out to CSV. Called on every terminal transition.</summary>
        private void FinishEpisode(Zone zone, SignalState reason, int bar)
        {
            var e = zone.Live;
            if (e == null) return;

            e.Confirmed = reason == SignalState.Confirmed;
            e.Expired = reason == SignalState.Expired;
            e.Broken = reason == SignalState.Broken;
            e.ResolvedInClock = !e.Expired && bar - e.TriggerBar <= ClockBars;

            if (_facts.Count > 0)
            {
                var f = _facts[_facts.Count - 1];
                var range = f.Range;

                if (range > 0m)
                {
                    e.ClosePosPct = e.Side == TestSide.SupportLong
                        ? (f.Close - f.Low) * 100m / range
                        : (f.High - f.Close) * 100m / range;
                }
            }

            if (_log != null) _log.Write(e);

            zone.Live = null;
        }

        /// <summary>Anything still open when the day rolls over is written as it stands.</summary>
        private void FlushOpenEpisodes()
        {
            List<Zone> zones;
            lock (_sync) zones = _zones;

            if (zones == null) return;

            foreach (var z in zones)
            {
                if (z.Live == null) continue;

                z.Live.Expired = true;
                z.Live.ResolvedInClock = false;

                if (_log != null) _log.Write(z.Live);
                z.Live = null;
            }
        }

        #endregion

        #region Path B - historical cluster forensics

        /// <summary>
        /// The footprint reconstruction, on closed bars whose tested extreme sits in a zone.
        ///
        /// Only in-zone bars are evaluated, and that is not an optimisation -- it is the same
        /// rule the tape path follows. Absorption is a statement about a level. The identical
        /// bar shape fifty points away from anything is a bar with a wick.
        /// </summary>
        private void RunClusterPath(int bar, IndicatorCandle candle, DateTime local, BarFacts facts,
                                    decimal tick)
        {
            List<Zone> zones;
            lock (_sync) zones = _zones;

            if (zones == null || zones.Count == 0) return;

            var rules = Tape();
            var cluster = Cluster();

            foreach (var zone in zones)
            {
                if (zone.State != SignalState.Armed && zone.State != SignalState.Triggered) continue;

                var side = zone.Side;
                if (!ClusterAbsorption.Touches(facts, zone, side, tick, rules)) continue;

                var withBand = facts;
                FillExtremeBand(ref withBand, candle, side, tick);

                var score = ClusterAbsorption.Score(withBand, _deltas, side, zone, tick, cluster);
                if (score.Total < cluster.MinScore) continue;

                var evt = new AbsorptionEvent
                {
                    Time = candle.LastTime != default(DateTime) ? candle.LastTime : candle.Time,
                    Price = withBand.MaxVolumePrice,
                    Volume = withBand.ExtremeVolume,
                    Direction = side == TestSide.SupportLong ? Aggressor.Sell : Aggressor.Buy,
                    Path = EventPath.Cluster,
                    Bar = bar,

                    // A closed bar that closed back inside HAS no follow-through by
                    // construction; the shape test already decided that.
                    DisplacementTicks = 0m
                };

                // The cluster extremes come from the bar itself here, not from a synthetic band
                // around one print: the whole bar is the evidence.
                zone.NoteCluster(withBand.Low, withBand.High);

                Promote(zone, evt, tick);
            }
        }

        /// <summary>
        /// Volume and delta held in the ExtremeTicks nearest the tested extreme.
        ///
        /// PriceVolumeInfo on this build carries no Delta property -- the build spec allows for
        /// exactly this -- so it is derived as Ask minus Bid: volume that lifted the offer minus
        /// volume that hit the bid. Negative at a low is aggressive selling that went nowhere,
        /// which is the b-shape the whole test is looking for.
        /// </summary>
        private void FillExtremeBand(ref BarFacts facts, IndicatorCandle candle, TestSide side,
                                     decimal tick)
        {
            var ticks = Math.Max(1, ExtremeTicks);

            var from = side == TestSide.SupportLong ? candle.Low : candle.High - tick * (ticks - 1);
            var to = side == TestSide.SupportLong ? candle.Low + tick * (ticks - 1) : candle.High;

            if (from < candle.Low) from = candle.Low;
            if (to > candle.High) to = candle.High;

            var volume = 0m;
            var delta = 0m;

            for (var price = from; price <= to; price += tick)
            {
                PriceVolumeInfo info;
                try { info = candle.GetPriceVolumeInfo(price); }
                catch { continue; }

                if (info == null) continue;

                volume += info.Volume;
                delta += info.Ask - info.Bid;
            }

            facts.ExtremeVolume = volume;
            facts.ExtremeDelta = delta;
        }

        #endregion

    }
}
