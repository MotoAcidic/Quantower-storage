using System;
using System.Collections.Generic;
using ATAS.Indicators;
using Utils.Common.Logging;

namespace OceansAnchor
{
    public partial class OceansAnchor
    {
        /// <summary>
        /// The bar the fold has to pause at until this session's tape has arrived.
        ///
        /// This gate is the whole reason historical tape events are coherent rather than
        /// nonsense. The obvious implementation -- fold every bar, then ask for the tape, then
        /// apply whatever comes back -- promotes zones using trades from hours ago against a
        /// state machine that has already advanced to now. A zone armed at 09:15 and broken at
        /// 09:40 would get triggered by a 09:15 print while it is sitting Broken, and there is
        /// no ordering of that which makes sense. The CSV is the calibration, so a wrong row is
        /// worse than a missing one.
        ///
        /// So the tape is requested BEFORE today's bars are folded, and folding waits for it.
        /// Each bar then replays its own trades, in its own bar, in time order -- exactly as if
        /// the indicator had been running live all session.
        ///
        /// Returns -1 when there is nothing to wait for: the tape is off, the clock is not
        /// settled, or history has already been dealt with.
        /// </summary>
        private int HistoryGateBar()
        {
            if (!TapeOn || _historyDone) return -1;
            if (_clock == null || !_clock.Valid) return -1;

            if (_gateBar != int.MinValue) return _gateBar;

            var lastClosed = CurrentBar - 2;
            if (lastClosed < 1) return -1;

            var date = TradeDateOfBar(lastClosed);
            if (date == null) return -1;

            // Walk back to the first bar of that trade date. Timestamps only -- this has to run
            // before any of these bars have been folded.
            var first = lastClosed;
            for (var bar = lastClosed - 1; bar >= 1; bar--)
            {
                var at = TradeDateOfBar(bar);
                if (at == null) continue;
                if (at.Value != date.Value) break;

                first = bar;
            }

            _gateBar = first;
            return _gateBar;
        }

        private DateTime? TradeDateOfBar(int bar)
        {
            var candle = GetCandle(bar);
            if (candle == null) return null;

            try { return SessionScan.TradeDateOf(_clock.ToLocal(candle.Time), _session); }
            catch { return null; }
        }

        /// <summary>
        /// Asks the feed for this session's cumulative trades. One outstanding request, ever:
        /// re-issuing on every recalculation would replay the whole session's tape each time a
        /// setting is nudged.
        /// </summary>
        private void RequestHistory()
        {
            if (_requestWaiting || _requestFailed || _historyDone || !TapeOn) return;
            if (_clock == null || !_clock.Valid) return;

            // Every path out of here must either leave a request outstanding or release the
            // gate. The fold is parked at the session start while this is pending, so a quiet
            // early return would stall the indicator on an empty chart with nothing to say.
            var gate = _gateBar;
            var candle = gate >= 0 ? GetCandle(gate) : null;

            if (candle == null)
            {
                _historyDone = true;
                _tapeNote = "no session start to request tape from; cluster path only";
                return;
            }

            try
            {
                _requestWaiting = true;
                _requestBar = CurrentBar;

                RequestForCumulativeTrades(new CumulativeTradesRequest(
                    candle.Time, DateTime.MaxValue, HistoryMode));
            }
            catch (Exception ex)
            {
                _requestWaiting = false;
                _requestFailed = true;
                _historyDone = true;
                _tapeNote = "tape history unavailable (" + ex.Message + "); cluster path only";

                this.LogWarn("Ocean's Anchor: " + _tapeNote);
            }
        }

        /// <summary>
        /// A malformed or empty response must degrade to cluster-path-only, never kill the
        /// indicator. The cluster path already covers the chart, so losing this costs the tape
        /// label on today's marks and nothing else.
        /// </summary>
        protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request,
                                                           IEnumerable<CumulativeTrade> trades)
        {
            _requestWaiting = false;

            try
            {
                if (trades == null)
                {
                    _tapeNote = "tape history empty; cluster path only";
                    return;
                }

                var buffer = new List<TradeSnapshot>();

                // Everything, not just what clears the size floor. The small prints are what
                // displacement is measured AGAINST -- without them every historical trade looks
                // like it went nowhere, and all of them would score as absorbed.
                foreach (var trade in trades)
                {
                    if (trade == null) continue;
                    buffer.Add(Snapshot(trade));
                }

                buffer.Sort(delegate (TradeSnapshot a, TradeSnapshot b)
                {
                    return a.Time.CompareTo(b.Time);
                });

                lock (_inboxLock)
                {
                    _history = buffer;
                    _historyAt = 0;
                }

                if (buffer.Count == 0) _tapeNote = "tape history returned nothing";
            }
            catch (Exception ex)
            {
                _requestFailed = true;
                _tapeNote = "tape history failed (" + ex.Message + "); cluster path only";
                this.LogWarn("Ocean's Anchor: " + _tapeNote);
            }
            finally
            {
                // Whatever happened, the fold is released and the load-time siren wall is over.
                _historyDone = true;
            }
        }

        /// <summary>
        /// Replays the buffered tape for one closed bar, in time order, through exactly the same
        /// promote path a live trade takes.
        ///
        /// The displacement watch is a field rather than a local because a print near the end of
        /// a bar has its two-second window run into the next one, and resolving it early at the
        /// bar boundary would score it absorbed on no evidence at all.
        /// </summary>
        private void ReplayHistory(int bar, IndicatorCandle candle, decimal tick)
        {
            List<TradeSnapshot> history;
            lock (_inboxLock) history = _history;

            if (history == null || _historyAt >= history.Count) return;

            // Bar b is closed, so b+1 exists and its stamp is where this bar ends.
            var next = GetCandle(bar + 1);
            var end = next != null ? next.Time : DateTime.MaxValue;

            var rules = Tape();

            List<Zone> zones;
            lock (_sync) zones = _zones;

            while (_historyAt < history.Count && history[_historyAt].Time < end)
            {
                var trade = history[_historyAt++];

                foreach (var pair in _replayWatch.Resolve(trade.Time, tick, rules))
                    Promote(pair.Value, pair.Key, tick);

                _replayWatch.NotePrint(trade.LastPrice);

                if (trade.Volume < rules.SizeFloor || zones == null) continue;

                var zone = ZoneFor(zones, trade, tick, rules);
                if (zone == null) continue;

                _replayWatch.Add(new AbsorptionEvent
                {
                    Time = trade.Time,
                    Price = trade.LastPrice,
                    Volume = trade.Volume,
                    Direction = trade.Direction,
                    Path = EventPath.Tape,
                    Bar = bar
                }, zone, rules);
            }

            // Anything whose window closed inside this bar resolves now; the rest carries into
            // the next one.
            foreach (var pair in _replayWatch.Resolve(end, tick, rules))
                Promote(pair.Value, pair.Key, tick);

            if (_historyAt >= history.Count && history.Count > 0 && _tapeNote == null)
                _tapeNote = "tape history replayed (" + history.Count + " trades)";
        }
    }
}
