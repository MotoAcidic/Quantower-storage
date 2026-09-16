using System;
using System.Collections.Generic;
using System.Text;

namespace OceansCurrent
{
    public enum BiasState
    {
        Neutral = 0,
        Long = 1,
        Short = -1
    }

    /// <summary>What the engine decided on one closed bar, and everything it decided it from.</summary>
    public struct BiasRecord
    {
        public int Bar;
        public DateTime Local;
        public decimal Close;

        public Subscore[] Subs;

        /// <summary>The blend before smoothing and before the wall damper.</summary>
        public decimal Raw;

        /// <summary>The number the state machine actually judged. Absent when nothing could vote.</summary>
        public decimal Score;
        public bool ScoreKnown;

        public BiasState State;
        public decimal Confidence;
        public Level Flip;
        public RegimeMode Regime;
        public bool InWallZone;
        public int GexAgeSeconds;
        public string Notes;
        public string Drivers;

        /// <summary>How many committed bars this state has been held, this one included.</summary>
        public int BarsInState;
    }

    /// <summary>Thresholds that govern the state machine rather than any one factor.</summary>
    public sealed class StateConfig
    {
        public decimal EnterThreshold = 30m;
        public decimal ExitThreshold = 10m;
        public int MinDwellBars = 3;
        public bool SmoothScore = true;
        public int SmoothBars = 3;
    }

    /// <summary>
    /// The whole model: session accumulators in, one committed decision per closed bar out.
    ///
    /// Determinism is structural rather than promised. <see cref="Advance"/> is the only thing
    /// that changes state, it takes one bar, it must be handed every bar in ascending order, and
    /// it refuses anything else. So rebuilding the chart from bar zero runs the identical
    /// sequence of identical calls and lands on the identical states -- there is no second code
    /// path for history to drift down. <see cref="Provisional"/> writes nothing at all.
    ///
    /// The one honest caveat: the gamma regime is a LIVE reading with no history behind it, so a
    /// rebuild re-scores old bars under today's regime. States are reproducible for a given
    /// snapshot, not across a regime flip. The regime in force is written into every log row so
    /// the calibration fit can see which one was used.
    /// </summary>
    public sealed class BiasEngine
    {
        public const int FactorCount = 6;

        private static readonly string[] FactorNames = { "VWAP", "CVD", "VALUE", "INV", "STRUCT", "OTF" };

        private readonly FactorConfig _f;
        private readonly StateConfig _s;
        private readonly SessionState _session;
        private readonly StructureTracker _structure;
        private readonly decimal _tickSize;

        private readonly List<BiasRecord> _records = new List<BiasRecord>();
        private readonly Level[] _refs = new Level[4];

        // A separate buffer so the provisional read really does write nothing the committed
        // path can see, not even a scratch array it happens to refill with the same values.
        private readonly Level[] _provisionalRefs = new Level[4];

        private readonly StateMachine _machine;
        private int _nextBar;
        private decimal _ema;
        private bool _emaSeeded;

        public BiasEngine(SessionConfig session, FactorConfig factors, StateConfig state, decimal tickSize)
        {
            _f = factors;
            _s = state;
            _tickSize = tickSize;
            _session = new SessionState(session, factors.ThrustNormWindow);
            _structure = new StructureTracker(factors, _refs.Length);
            _machine = new StateMachine(state);
        }

        /// <summary>A throwaway copy for <see cref="WhatIf"/>. Shares config, owns every piece of state.</summary>
        private BiasEngine(BiasEngine src)
        {
            _f = src._f;
            _s = src._s;
            _tickSize = src._tickSize;
            _session = src._session.CloneForWhatIf();
            _structure = src._structure.CloneForWhatIf();
            _machine = src._machine.CloneForWhatIf();
            _nextBar = src._nextBar;
            _ema = src._ema;
            _emaSeeded = src._emaSeeded;
            Array.Copy(src._refs, _refs, _refs.Length);

            // Advance only ever appends; nothing it computes reads back through history.
            if (src._records.Count > 0) _records.Add(src._records[src._records.Count - 1]);
        }

        /// <summary>
        /// What committing this bar would produce -- by committing it, through the real
        /// <see cref="Advance"/>, on a throwaway copy. Nothing on this engine changes.
        ///
        /// This replaced a hand-built "score it as Advance would" method that was a copy of
        /// Provisional, and the harness proved it wrong: it missed the new bar's effect on the
        /// VWAP slope, the CVD divergence flags and the overnight high/low/close, so the levels
        /// it printed were not the levels the engine acted on. There is now no second path to
        /// drift: the what-if IS the commit.
        /// </summary>
        public BiasRecord WhatIf(IBarWindow hypothetical, TimeContext time, GexSnapshot gex, bool gexUsable)
        {
            var copy = new BiasEngine(this);
            copy.Advance(_nextBar, hypothetical, time, gex, gexUsable, 0);
            return copy.Last;
        }

        /// <summary>The next bar index this engine will accept. Bars must arrive in order.</summary>
        public int NextBar => _nextBar;

        public int Count => _records.Count;
        public BiasRecord this[int i] => _records[i];
        public bool Any => _records.Count > 0;
        public BiasRecord Last => _records[_records.Count - 1];
        public IReadOnlyList<BiasRecord> Records => _records;
        public SessionState Session => _session;

        /// <summary>
        /// Commit one closed bar. Throws on a bar out of order rather than absorbing it: a
        /// skipped bar is a missed session roll, and a repeated one double-counts volume into
        /// the VWAP. Both would draw a clean, plausible, wrong badge.
        /// </summary>
        public void Advance(int bar, IBarWindow bars, TimeContext time, GexSnapshot gex,
                            bool gexUsable, int gexAgeSeconds)
        {
            if (bar != _nextBar)
                throw new InvalidOperationException(
                    "Ocean's Current: bar " + bar + " arrived when " + _nextBar +
                    " was due. The engine has to be rebuilt from the start.");

            _nextBar = bar + 1;

            var local = time.ToLocal(bars.Time(bar));
            var close = bars.Close(bar);

            _session.Absorb(bar, local, bars.Open(bar), bars.High(bar), bars.Low(bar),
                            close, bars.Volume(bar), bars.Delta(bar));

            LoadReferences();
            _structure.Absorb(bar, _refs, bars.High(bar), bars.Low(bar), close);

            var regime = RegimeOf(gex, gexUsable);
            var subs = new Subscore[FactorCount];

            subs[(int)FactorId.Vwap] = Factors.Vwap(
                close, _session.Vwap, _session.Sigma, Slope(), regime == RegimeMode.Positive, _f);

            subs[(int)FactorId.Cvd] = CommitCvd(bar, bars);
            subs[(int)FactorId.Value] = Factors.Value();

            subs[(int)FactorId.Inventory] = Factors.Inventory(
                _session.OnHigh, _session.OnLow, _session.OnClose,
                _session.RthOpen, _session.PriorRthClose, _f);

            subs[(int)FactorId.Structure] = Factors.Structure(close, _refs, _structure.BiasAt(bar));
            subs[(int)FactorId.OneTimeframing] = Factors.OneTimeframing();

            var inWall = gexUsable && gex.InWallZone(close, _f.WallZonePoints);

            decimal raw;
            var known = Scoring.Blend(subs, _f, regime, inWall, out raw);

            var score = raw;
            if (known && _s.SmoothScore)
            {
                var span = Math.Max(1, _s.SmoothBars);
                var alpha = 2m / (span + 1);
                _ema = _emaSeeded ? _ema + alpha * (raw - _ema) : raw;
                _emaSeeded = true;
                score = _ema;
            }

            _machine.Advance(known, score);

            var record = new BiasRecord
            {
                Bar = bar,
                Local = local,
                Close = close,
                Subs = subs,
                Raw = raw,
                Score = score,
                ScoreKnown = known,
                State = _machine.State,
                BarsInState = _machine.BarsInState,
                Regime = regime,
                InWallZone = inWall,
                GexAgeSeconds = gexAgeSeconds,
                Confidence = Confidence(subs, known, score),
                Flip = FlipLevel(close),
                Notes = Notes(subs, inWall),
                Drivers = Drivers(subs, regime)
            };

            _records.Add(record);
        }

        /// <summary>
        /// The forming bar's reading, for display only. Nothing here is written back: the state
        /// machine, the session accumulators and the sweep watches are all untouched, so a
        /// tick-by-tick session and a bar-by-bar replay of the same data commit the same states.
        /// </summary>
        public bool Provisional(int bar, IBarWindow bars, TimeContext time,
                                GexSnapshot gex, bool gexUsable, out decimal score)
        {
            score = 0m;
            if (bar < 0 || bar >= bars.Count) return false;

            var local = time.ToLocal(bars.Time(bar));
            var phase = _session.Config.PhaseOf(local.TimeOfDay);
            var close = bars.Close(bar);

            Level vwap, sigma;
            decimal cvd;
            _session.Peek(phase, bars.High(bar), bars.Low(bar), close,
                          bars.Volume(bar), bars.Delta(bar), out vwap, out sigma, out cvd);

            var regime = RegimeOf(gex, gexUsable);
            var subs = new Subscore[FactorCount];

            subs[(int)FactorId.Vwap] = Factors.Vwap(
                close, vwap, sigma, Slope(), regime == RegimeMode.Positive, _f);

            // The thrust looks back from the live cumulative delta into committed history. One
            // fewer bar back, because the live bar is not in the committed series yet.
            var back = Math.Max(1, _f.ThrustBars) - 1;
            subs[(int)FactorId.Cvd] = Factors.Cvd(
                cvd, _session.HasCvdBack(back), _session.CvdAt(back), _session.ThrustNorm.Mean,
                false, false, false, false, _f);

            subs[(int)FactorId.Value] = Factors.Value();

            subs[(int)FactorId.Inventory] = Factors.Inventory(
                _session.OnHigh, _session.OnLow, _session.OnClose,
                _session.RthOpen, _session.PriorRthClose, _f);

            LoadReferences(_provisionalRefs);
            subs[(int)FactorId.Structure] = Factors.Structure(
                close, _provisionalRefs, _structure.BiasAt(bar));

            subs[(int)FactorId.OneTimeframing] = Factors.OneTimeframing();

            var inWall = gexUsable && gex.InWallZone(close, _f.WallZonePoints);

            decimal raw;
            if (!Scoring.Blend(subs, _f, regime, inWall, out raw)) return false;

            // Smoothed against the committed EMA, again without advancing it.
            if (_s.SmoothScore && _emaSeeded)
            {
                var span = Math.Max(1, _s.SmoothBars);
                var alpha = 2m / (span + 1);
                raw = _ema + alpha * (raw - _ema);
            }

            score = raw;
            return true;
        }

        /// <summary>What the next committed bar would do with this score. Changes nothing.</summary>
        public BiasState PeekState(bool known, decimal score) => _machine.Peek(known, score);

        public BiasState PeekIgnoringDwell(bool known, decimal score) =>
            _machine.PeekIgnoringDwell(known, score);

        public BiasState State => _machine.State;

                /// <summary>Committed bars still to serve before the state may change.</summary>
        public int DwellLeft => _machine.DwellLeft;

        /// <summary>
        /// The state the machine would reach from NEUTRAL with a settled score -- ignoring the
        /// dwell -- for the "where does it reverse outright" level.
        /// </summary>
        public BiasState FromNeutral(decimal score) =>
            score >= _s.EnterThreshold ? BiasState.Long
          : score <= -_s.EnterThreshold ? BiasState.Short
          : BiasState.Neutral;

        public decimal SweepPenalty => _f.SweepPenalty;
        public bool CvdVoting => _session.ThrustNorm.Mean.Known && _f.Enabled[(int)FactorId.Cvd];

        /// <summary>The carried-over references, named, for the sweep events.</summary>
        public static readonly string[] ReferenceNames = { "PDH", "PDL", "ONH", "ONL" };

        public Level[] References()
        {
            var r = new Level[4];
            LoadReferences(r);
            return r;
        }

        private void LoadReferences() => LoadReferences(_refs);

        private void LoadReferences(Level[] into)
        {
            into[0] = _session.PriorRthHigh;
            into[1] = _session.PriorRthLow;
            into[2] = _session.OnHigh;
            into[3] = _session.OnLow;
        }

        private Level Slope()
        {
            var back = Math.Max(1, _f.VwapSlopeBars);
            if (!_session.HasVwapBack(back)) return Level.None;

            return Level.At(_session.VwapAt(0) - _session.VwapAt(back));
        }

        private Subscore CommitCvd(int bar, IBarWindow bars)
        {
            var back = Math.Max(1, _f.ThrustBars);
            var hasBack = _session.HasCvdBack(back);
            var norm = _session.ThrustNorm.Mean;

            var window = Math.Max(1, _f.DivergenceBars);
            bool priceHigh, priceLow, cvdHigh, cvdLow;
            Extremes(bar, bars, window, out priceHigh, out priceLow, out cvdHigh, out cvdLow);

            var sub = Factors.Cvd(_session.Cvd, hasBack, _session.CvdAt(back), norm,
                                  priceHigh, priceLow, cvdHigh, cvdLow, _f);

            // The yardstick is fed AFTER the reading it scaled, so no bar is ever normalised
            // against itself and nothing here can see forward.
            if (hasBack) _session.ThrustNorm.Add(Math.Abs(_session.Cvd - _session.CvdAt(back)));

            return sub;
        }

        /// <summary>
        /// Did this bar make the window's extreme in price, and did cumulative delta make its
        /// own? The delta window is clipped to the anchor, because a cumulative delta compared
        /// across a session reset compares a number to a different number.
        /// </summary>
        private void Extremes(int bar, IBarWindow bars, int window,
                              out bool priceHigh, out bool priceLow,
                              out bool cvdHigh, out bool cvdLow)
        {
            var high = bars.High(bar);
            var low = bars.Low(bar);

            priceHigh = true;
            priceLow = true;

            for (var back = 1; back < window; back++)
            {
                var i = bar - back;
                if (i < 0) break;

                if (bars.High(i) >= high) priceHigh = false;
                if (bars.Low(i) <= low) priceLow = false;
            }

            cvdHigh = false;
            cvdLow = false;

            if (_session.BarsSinceAnchor < 2) return;

            var cvd = _session.CvdAt(0);
            cvdHigh = true;
            cvdLow = true;

            for (var back = 1; back < window; back++)
            {
                if (!_session.HasCvdBack(back)) break;

                var earlier = _session.CvdAt(back);
                if (earlier >= cvd) cvdHigh = false;
                if (earlier <= cvd) cvdLow = false;
            }
        }

        private static RegimeMode RegimeOf(GexSnapshot gex, bool usable)
        {
            if (!usable || gex == null) return RegimeMode.None;

            return gex.Regime > 0 ? RegimeMode.Positive
                 : gex.Regime < 0 ? RegimeMode.Negative
                 : RegimeMode.None;
        }

        /// <summary>
        /// How much the factors agree with the state they produced. For a directional state
        /// that is the share of voting factors pointing the same way. For neutral there is no
        /// direction to match, so it is the share of voting factors that are themselves too
        /// small to enter a state -- factors quietly agreeing that there is nothing here.
        /// </summary>
        private decimal Confidence(Subscore[] subs, bool known, decimal score)
        {
            if (!known) return 0m;

            var voting = 0;
            var agreeing = 0;

            for (var i = 0; i < subs.Length; i++)
            {
                if (!_f.Enabled[i] || !subs[i].Available) continue;
                if (_f.Weight[i] <= 0m) continue;

                voting++;

                if (_machine.State == BiasState.Neutral)
                {
                    if (Math.Abs(subs[i].Value) < _s.EnterThreshold) agreeing++;
                }
                else if (subs[i].Sign == (int)_machine.State)
                {
                    agreeing++;
                }
            }

            if (voting == 0) return 0m;

            var agreement = 100m * agreeing / voting;
            var magnitude = Math.Abs(score);
            if (magnitude > 100m) magnitude = 100m;

            return Math.Round(0.6m * magnitude + 0.4m * agreement, 0, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Where this state stops being true. The nearest thing price would have to give back
        /// to stop being long: the highest reference strictly below it. No reference below
        /// price means no flip level, and the badge says so rather than inventing one.
        /// </summary>
        private Level FlipLevel(decimal close)
        {
            if (_machine.State == BiasState.Neutral) return Level.None;

            var best = Level.None;

            Consider(ref best, _session.Vwap, close);
            Consider(ref best, OvernightMid(), close);

            if (!best.Known) return Level.None;

            return Level.At(Round(best.Value, _tickSize));
        }

        private void Consider(ref Level best, Level candidate, decimal close)
        {
            if (!candidate.Known) return;

            if (_machine.State == BiasState.Long)
            {
                if (candidate.Value >= close) return;
                if (!best.Known || candidate.Value > best.Value) best = candidate;
            }
            else
            {
                if (candidate.Value <= close) return;
                if (!best.Known || candidate.Value < best.Value) best = candidate;
            }
        }

        private Level OvernightMid()
        {
            if (!_session.OnHigh.Known || !_session.OnLow.Known) return Level.None;

            return Level.At((_session.OnHigh.Value + _session.OnLow.Value) / 2m);
        }

        /// <summary>Rounds to the instrument's tick. A tick size of zero means round to nothing.</summary>
        public static decimal Round(decimal value, decimal tick)
        {
            if (tick <= 0m) return value;

            return Math.Round(value / tick, MidpointRounding.AwayFromZero) * tick;
        }

        private string Notes(Subscore[] subs, bool inWall)
        {
            var sb = new StringBuilder();

            for (var i = 0; i < subs.Length; i++)
            {
                if (subs[i].Note == null) continue;
                if (!_f.Enabled[i]) continue;

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(subs[i].Note);
            }

            if (inWall)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("WALL ZONE");
            }

            return sb.Length == 0 ? "-" : sb.ToString();
        }

        /// <summary>The two factors carrying the score, by how much weight times vote each moved.</summary>
        private string Drivers(Subscore[] subs, RegimeMode regime)
        {
            var firstIndex = -1;
            var secondIndex = -1;
            var first = 0m;
            var second = 0m;

            for (var i = 0; i < subs.Length; i++)
            {
                var w = Scoring.WeightOf(i, subs[i], _f, regime);
                if (w <= 0m) continue;

                var pull = Math.Abs(w * subs[i].Value);
                if (pull <= 0m) continue;

                if (pull > first) { second = first; secondIndex = firstIndex; first = pull; firstIndex = i; }
                else if (pull > second) { second = pull; secondIndex = i; }
            }

            if (firstIndex < 0) return "-";

            var text = Name(subs, firstIndex);
            if (secondIndex >= 0) text += " " + Name(subs, secondIndex);

            return text;
        }

        private static string Name(Subscore[] subs, int i) =>
            FactorNames[i] + (subs[i].Value >= 0m ? "+" : "-");

        public static string StateText(BiasState state) =>
            state == BiasState.Long ? "LONG" : state == BiasState.Short ? "SHORT" : "NEUTRAL";

        public static string FactorName(int i) => FactorNames[i];
    }
}
