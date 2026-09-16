using System;
using System.Collections.Generic;
using ATAS.Indicators;

namespace OceansAnchor
{
    public partial class OceansAnchor
    {
        private bool _onZonesAdded;

        /// <summary>
        /// Everything one closed bar does, in the order it has to happen: roll the session over
        /// first so the state machine is looking at today's zones, then fold the bar into the
        /// profiles, then advance the zones across it.
        ///
        /// Called once per bar, ever. Re-entering it for a bar already folded would double the
        /// volume at every price in the session profile.
        /// </summary>
        private void FoldBar(int bar, decimal tick)
        {
            var candle = GetCandle(bar);
            if (candle == null) return;

            DateTime local;
            try { local = _clock.ToLocal(candle.Time); }
            catch { return; }

            var date = SessionScan.TradeDateOf(local, _session);

            // 16:00-17:00 belongs to neither trade date. Dropping those bars is deliberate:
            // smearing the maintenance break into a neighbouring session puts volume at prices
            // the session never traded.
            if (date == null) return;

            if (_liveDate == default(DateTime)) StartSession(date.Value);
            else if (date.Value != _liveDate) RollSession(date.Value, bar, tick);

            var inRth = SessionScan.Resolvable(_barSize, _session.RthStart, _session.RthEnd) &&
                        SessionScan.Overlaps(local.TimeOfDay, _barSize, _session.RthStart, _session.RthEnd);

            // The overnight is complete the moment regular hours begin, so this is where the
            // overnight POC can finally be admitted -- and it is the only place it can be,
            // because at the 17:00 rollover the session it describes has not happened yet.
            if (inRth && !_onZonesAdded)
            {
                _onZonesAdded = true;
                if (_sessionOpen == 0m) _sessionOpen = candle.Open;
                RebuildZones(bar, tick);
            }

            AccumulateProfile(inRth ? _liveRth : _liveOvernight, candle, bar, tick);

            _liveRth.NoteDay(candle.High, candle.Low);
            _liveOvernight.NoteDay(candle.High, candle.Low);

            _naked.NoteBar(bar, candle.High, candle.Low);

            var facts = BasicFacts(candle);
            _facts.Add(facts);

            var keep = Math.Max(DeltaLookback, OtfBars) + 4;
            while (_facts.Count > keep) _facts.RemoveAt(0);

            _engine.NoteBarDelta(candle.Delta);

            // Advance BEFORE the absorption paths, not after.
            //
            // A zone can only take an absorption event once it is Armed, and the bar that arms
            // it is very often the same bar that does the absorbing -- that is what the setup
            // looks like: price reaches the shelf and gets rejected inside one candle. Running
            // the cluster path first meant the classic bar was always evaluated against a
            // Dormant zone and thrown away, and the earliest a trigger could ever fire was the
            // bar after the one a human would have marked.
            AdvanceZones(bar, facts, local, tick);

            if (TapeOn) ReplayHistory(bar, candle, tick);
            if (ClusterOn) RunClusterPath(bar, candle, local, facts, tick);

            // The delta lookback is appended LAST, so the percentile a bar is judged against is
            // built from the bars BEFORE it. Including the bar in its own reference distribution
            // is self-defeating: the outlier drags the 10th percentile down toward itself and
            // the test gets harder to pass the more extreme the bar is, which is backwards.
            _deltas.Add(candle.Delta);
            while (_deltas.Count > DeltaLookback) _deltas.RemoveAt(0);
        }

        private void StartSession(DateTime date)
        {
            _liveDate = date;
            _liveRth = new SessionProfile { TradeDate = date };
            _liveOvernight = new SessionProfile { TradeDate = date };
            _onZonesAdded = false;
            _sessionOpen = 0m;
        }

        /// <summary>
        /// A new CME trade date. The session that just ended becomes history, the composite is
        /// re-merged, and the zones are rebuilt from what is now complete.
        /// </summary>
        private void RollSession(DateTime date, int bar, decimal tick)
        {
            Finalize(_liveRth, tick);
            Finalize(_liveOvernight, tick);

            if (_liveRth.VolByPrice.Count > 0)
            {
                _sessions.Add(_liveRth);

                // Yesterday's POC joins the naked set. It starts naked by definition -- price has
                // not traded since the session that made it ended.
                _naked.Add(_liveRth.Poc, _liveRth.TradeDate, bar, Math.Max(CompositeSessions, 10));
            }

            // The ring only ever needs the composite window plus a little slack for ADR.
            var keep = Math.Max(CompositeSessions, DailyAtrSessions) + 2;
            while (_sessions.Count > keep) _sessions.RemoveAt(0);

            // Zones do not survive the day, and neither does their state. Dropping the list
            // outright is what makes that true: CarryState matches zones across a rebuild by
            // key, so a composite shelf that reappears tomorrow at the same POC would otherwise
            // come back still wearing yesterday's Broken flag and never arm again.
            FlushOpenEpisodes();
            lock (_sync) _zones = new List<Zone>();

            StartSession(date);
            RebuildZones(bar, tick);
        }

        private static void Finalize(SessionProfile p, decimal tick)
        {
            if (p == null || p.VolByPrice.Count == 0) return;

            p.ComputePoc();
            p.ComputeValueArea(tick, 0.70m);
        }

        /// <summary>
        /// Volume at price for one bar.
        ///
        /// GetAllPriceLevels rather than the documented High-to-Low tick walk: the walk costs
        /// one dictionary probe per tick of the bar's range whether anything traded there or
        /// not, and on a wide bar most of those probes come back null. This enumerates only the
        /// levels that exist. The tick walk stays as the fallback for a feed that does not
        /// support the enumeration.
        /// </summary>
        private void AccumulateProfile(SessionProfile profile, IndicatorCandle candle, int bar,
                                       decimal tick)
        {
            if (profile == null) return;

            profile.NoteBar(bar, candle.High, candle.Low);

            IEnumerable<PriceVolumeInfo> levels = null;
            try { levels = candle.GetAllPriceLevels(); }
            catch { levels = null; }

            if (levels != null)
            {
                foreach (var info in levels)
                {
                    if (info == null) continue;
                    profile.Add(info.Price, info.Volume);
                }

                return;
            }

            for (var price = candle.High; price >= candle.Low; price -= tick)
            {
                var info = candle.GetPriceVolumeInfo(price);
                if (info == null) continue;

                profile.Add(price, info.Volume);
            }
        }

        /// <summary>
        /// Rebuilds the ranked zone set. Bar close and session boundary only -- never per tick.
        ///
        /// Zones that survive the rebuild keep their state. A zone Broken at 09:15 must stay
        /// broken through the 09:30 rebuild, or the same failed fade re-arms every time a
        /// profile is re-merged.
        /// </summary>
        private void RebuildZones(int bar, decimal tick)
        {
            if (bar <= _lastZoneBar && _lastZoneDate == _liveDate) return;

            _lastZoneBar = bar;
            _lastZoneDate = _liveDate;

            var priorRth = LastCompleted();
            if (priorRth == null) return;

            _adr = ProfileMath.AverageDailyRange(_sessions, DailyAtrSessions);

            var input = new ZoneBuildInput
            {
                PriorRth = priorRth,
                Composite = BuildComposite(),
                Overnight = _onZonesAdded ? Completed(_liveOvernight, tick) : null,
                Naked = _naked,
                TickSize = tick,
                Hvn = Hvn(),
                MaxZones = MaxZones,
                MinOnVolumePct = MinOnVolumePct,
                Adr = _adr,
                SessionOpen = _sessionOpen,
                DistanceAdrMult = DistanceAdrMult,
                StartBar = bar,
                BornSession = _liveDate
            };

            var rebuilt = ZoneBuilder.Build(input);
            CarryState(rebuilt);

            lock (_sync) _zones = rebuilt;
        }

        private SessionProfile Completed(SessionProfile p, decimal tick)
        {
            if (p == null || p.VolByPrice.Count == 0) return null;
            if (p.Poc == 0m) Finalize(p, tick);

            return p;
        }

        private SessionProfile LastCompleted()
        {
            for (var i = _sessions.Count - 1; i >= 0; i--)
                if (_sessions[i].VolByPrice.Count > 0) return _sessions[i];

            return null;
        }

        /// <summary>
        /// The composite ladder: the last N regular-hours profiles merged. This is what turns a
        /// level that yesterday happened to like into a level that two weeks of auctions agree
        /// on, and it is the difference between rank 1 and rank 3.
        /// </summary>
        private Dictionary<decimal, decimal> BuildComposite()
        {
            var merged = new Dictionary<decimal, decimal>();

            var taken = 0;
            for (var i = _sessions.Count - 1; i >= 0 && taken < CompositeSessions; i--)
            {
                var s = _sessions[i];
                if (s.VolByPrice.Count == 0) continue;

                foreach (var kv in s.VolByPrice)
                {
                    decimal v;
                    merged.TryGetValue(kv.Key, out v);
                    merged[kv.Key] = v + kv.Value;
                }

                taken++;
            }

            return merged;
        }

        /// <summary>
        /// Carries live state across a rebuild by zone identity, so a Broken or Triggered zone
        /// stays what it was.
        /// </summary>
        private void CarryState(List<Zone> rebuilt)
        {
            List<Zone> old;
            lock (_sync) old = _zones;

            if (old == null || old.Count == 0) return;

            var byKey = new Dictionary<string, Zone>();
            foreach (var z in old) byKey[z.Key] = z;

            foreach (var z in rebuilt)
            {
                Zone prior;
                if (!byKey.TryGetValue(z.Key, out prior)) continue;

                z.State = prior.State;
                z.Side = prior.Side;
                z.ArmedBar = prior.ArmedBar;
                z.TriggeredBar = prior.TriggeredBar;
                z.TraversalCount = prior.TraversalCount;
                z.ClusterLow = prior.ClusterLow;
                z.ClusterHigh = prior.ClusterHigh;
                z.HasCluster = prior.HasCluster;
                z.Live = prior.Live;

                foreach (var kv in prior.LastAlert) z.LastAlert[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Runs every zone across the closed bar and turns the transitions into alerts and CSV.
        /// </summary>
        private void AdvanceZones(int bar, BarFacts facts, DateTime local, decimal tick)
        {
            List<Zone> zones;
            lock (_sync) zones = _zones;

            if (zones == null || zones.Count == 0) return;

            ApplyStateRules();
            var otf = SignalGate.OneTimeframing(_facts, OtfBars);

            foreach (var zone in zones)
            {
                var t = _engine.Advance(zone, bar, facts, local, tick, otf);
                if (!t.Changed) continue;

                OnTransition(zone, t, bar, facts, local);
            }
        }

        /// <summary>Basic per-bar numbers. The extreme band is filled in only for in-zone bars.</summary>
        private static BarFacts BasicFacts(IndicatorCandle c)
        {
            var maxVolPrice = c.Close;
            try
            {
                var info = c.MaxVolumePriceInfo;
                if (info != null && info.Price > 0m) maxVolPrice = info.Price;
            }
            catch { }

            return new BarFacts
            {
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume,
                Delta = c.Delta,
                MaxVolumePrice = maxVolPrice
            };
        }
    }
}
