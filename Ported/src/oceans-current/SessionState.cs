using System;
using System.ComponentModel.DataAnnotations;

namespace OceansCurrent
{
    /// <summary>Which part of the trading day a bar belongs to, on the Houston clock.</summary>
    public enum Phase
    {
        /// <summary>Between the cash close and the evening reopen. Nothing accumulates.</summary>
        Between = 0,

        /// <summary>The evening reopen through to the cash open. Globex.</summary>
        Overnight = 1,

        /// <summary>The regular session.</summary>
        Rth = 2
    }

    /// <summary>Where the session VWAP and the session CVD start counting from.</summary>
    public enum SessionAnchor
    {
        [Display(Name = "The cash open")] RthOpen = 0,
        [Display(Name = "The evening reopen")] GlobexOpen = 1,

        /// <summary>
        /// Both: a cash-session VWAP from 08:30 to 15:00, and an overnight VWAP of its own from
        /// the 17:00 reopen. Cash hours read exactly as RthOpen does; the difference is that the
        /// evening has a reading instead of two factors sitting dark until tomorrow.
        /// </summary>
        [Display(Name = "Each session (cash and overnight)")] EachSession = 2
    }

    /// <summary>Session windows, entered in Houston time and compared in it. No second zone.</summary>
    public sealed class SessionConfig
    {
        public TimeSpan RthStart = new TimeSpan(8, 30, 0);
        public TimeSpan RthEnd = new TimeSpan(15, 0, 0);
        public TimeSpan OnStart = new TimeSpan(17, 0, 0);
        public TimeSpan OnEnd = new TimeSpan(8, 30, 0);
        public SessionAnchor Anchor = SessionAnchor.RthOpen;

        public Phase PhaseOf(TimeSpan tod)
        {
            // RTH wins any overlap: it is the window every other reading is framed against.
            if (tod >= RthStart && tod < RthEnd) return Phase.Rth;

            // The overnight window wraps midnight, so the two halves are tested separately.
            if (OnStart <= OnEnd)
                return tod >= OnStart && tod < OnEnd ? Phase.Overnight : Phase.Between;

            return tod >= OnStart || tod < OnEnd ? Phase.Overnight : Phase.Between;
        }
    }

    /// <summary>
    /// A number that may not exist yet. The whole point: a reference that has not printed is
    /// absent, never zero and never a stand-in. Every factor that reads one has to say what it
    /// does when it is missing, and a factor that cannot compute drops out of the blend rather
    /// than voting a made-up value.
    /// </summary>
    public struct Level
    {
        public decimal Value;
        public bool Known;

        public static readonly Level None = new Level();

        public static Level At(decimal value) => new Level { Value = value, Known = true };
    }

    /// <summary>
    /// Walks the bars once, in order, and keeps everything the factors read: the anchored VWAP
    /// and its spread, cumulative delta, the developing and prior session extremes, and the
    /// overnight statistics frozen at the cash open.
    ///
    /// Only CLOSED bars are ever absorbed here. The forming bar's volume and delta grow with
    /// every tick, so letting it into an accumulator would make the committed history depend on
    /// when the chart happened to be looked at. The provisional read of the live bar is computed
    /// from these scalars in <see cref="Factors"/> and written back nowhere.
    /// </summary>
    public sealed class SessionState
    {
        private readonly SessionConfig _cfg;

        private bool _started;
        private Phase _phase = Phase.Between;

        // Developing extremes of the cash session in progress, and whether its open was seen.
        private decimal _devRthHigh, _devRthLow, _devRthClose;
        private bool _devRthOpenSeen, _devRthAny;

        // Developing extremes of the overnight block in progress.
        private decimal _devOnHigh, _devOnLow, _devOnClose;
        private bool _devOnOpenSeen, _devOnAny;

        // Anchored accumulators. Reset whole at the anchor open.
        private decimal _sumVol, _sumVolTyp, _sumVolTyp2;
        private decimal _cvd;
        private int _anchorBar = -1;

        // Cumulative delta per bar since the anchor, so a K-bar thrust can look back inside the
        // session without ever spanning a reset.
        private decimal[] _cvdSeries = new decimal[512];
        private int _cvdCount;

        // Session VWAP per bar since the anchor, for the slope check.
        private decimal[] _vwapSeries = new decimal[512];
        private int _vwapCount;

        // The spread of |thrust| a thrust is measured against. This one deliberately survives the
        // session reset: one session never holds enough of them to mean anything, and the
        // yardstick is not session-specific. The thrust itself never spans a reset.
        private RingMean _thrustNorm;   // not readonly: CloneForWhatIf gives the copy its own

        public SessionState(SessionConfig cfg, int thrustNormWindow)
        {
            _cfg = cfg;
            _thrustNorm = new RingMean(Math.Max(1, thrustNormWindow));
        }

        public SessionConfig Config => _cfg;

        /// <summary>
        /// A throwaway copy for a what-if commit. MemberwiseClone carries every scalar -- including
        /// any added later, which is the point: a hand-written field list is how a what-if quietly
        /// drifts from the real thing -- and the three mutable buffers are given their own.
        /// </summary>
        public SessionState CloneForWhatIf()
        {
            var c = (SessionState)MemberwiseClone();
            c._cvdSeries = Copy(_cvdSeries, _cvdCount);
            c._vwapSeries = Copy(_vwapSeries, _vwapCount);
            c._thrustNorm = _thrustNorm.CloneForWhatIf();
            return c;
        }

        private static decimal[] Copy(decimal[] source, int count)
        {
            var copy = new decimal[Math.Max(count + 8, 16)];
            Array.Copy(source, copy, Math.Min(count, source.Length));
            return copy;
        }
        public Phase CurrentPhase => _phase;
        public int BarsSinceAnchor => _cvdCount;
        public int AnchorBar => _anchorBar;
        public RingMean ThrustNorm => _thrustNorm;

        /// <summary>Bars elapsed since the cash open, or -1 while no seen open is in force.</summary>
        public int BarsIntoRth { get; private set; } = -1;

        public Level PriorRthHigh { get; private set; } = Level.None;
        public Level PriorRthLow { get; private set; } = Level.None;
        public Level PriorRthClose { get; private set; } = Level.None;

        public Level OnHigh { get; private set; } = Level.None;
        public Level OnLow { get; private set; } = Level.None;
        public Level OnClose { get; private set; } = Level.None;

        /// <summary>The cash open of the session in progress. Frozen for its whole length.</summary>
        public Level RthOpen { get; private set; } = Level.None;

        public decimal Cvd => _cvd;
        public decimal SumVolume => _sumVol;

        /// <summary>Anchored VWAP, absent until volume has traded since the anchor.</summary>
        public Level Vwap => _sumVol > 0m ? Level.At(_sumVolTyp / _sumVol) : Level.None;

        /// <summary>
        /// Volume-weighted standard deviation about the anchored VWAP. Absent while it is zero:
        /// a single price with no spread yet cannot say how far away is far, and dividing by it
        /// would turn one tick of drift into a maximum reading.
        /// </summary>
        public Level Sigma
        {
            get
            {
                if (_sumVol <= 0m) return Level.None;

                var mean = _sumVolTyp / _sumVol;
                var variance = _sumVolTyp2 / _sumVol - mean * mean;
                if (variance <= 0m) return Level.None;

                var sd = (decimal)Math.Sqrt((double)variance);
                return sd > 0m ? Level.At(sd) : Level.None;
            }
        }

        public bool HasCvdBack(int barsBack) => _cvdCount - 1 - barsBack >= 0;

        public decimal CvdAt(int barsBack)
        {
            var i = _cvdCount - 1 - barsBack;
            return i >= 0 && i < _cvdCount ? _cvdSeries[i] : 0m;
        }

        public bool HasVwapBack(int barsBack) => _vwapCount - 1 - barsBack >= 0;

        public decimal VwapAt(int barsBack)
        {
            var i = _vwapCount - 1 - barsBack;
            return i >= 0 && i < _vwapCount ? _vwapSeries[i] : 0m;
        }

        /// <summary>True while the anchor window is open and this bar feeds the accumulators.</summary>
        public bool Accumulating(Phase phase) => _cfg.Anchor == SessionAnchor.RthOpen
            ? phase == Phase.Rth
            : phase != Phase.Between;   // GlobexOpen and EachSession both run through the night

        /// <summary>
        /// What the accumulators WOULD read with this bar in them, without putting it in. This
        /// is the only way the forming bar is allowed to influence anything: the badge's
        /// provisional number. Nothing is written, so a tick that revises the bar's volume down
        /// leaves no trace, and the committed history stays a function of closed bars alone.
        /// </summary>
        public void Peek(Phase phase, decimal high, decimal low, decimal close,
                         decimal volume, decimal delta,
                         out Level vwap, out Level sigma, out decimal cvd)
        {
            var sumVol = _sumVol;
            var sumVolTyp = _sumVolTyp;
            var sumVolTyp2 = _sumVolTyp2;
            cvd = _cvd;

            if (Accumulating(phase) && _anchorBar >= 0)
            {
                var typical = (high + low + close) / 3m;
                sumVol += volume;
                sumVolTyp += volume * typical;
                sumVolTyp2 += volume * typical * typical;
                cvd += delta;
            }

            if (sumVol <= 0m) { vwap = Level.None; sigma = Level.None; return; }

            var mean = sumVolTyp / sumVol;
            vwap = Level.At(mean);

            var variance = sumVolTyp2 / sumVol - mean * mean;
            sigma = variance > 0m ? Level.At((decimal)Math.Sqrt((double)variance)) : Level.None;
        }

        /// <summary>
        /// Absorb one closed bar. Must be called for every bar in order: the session rolls are
        /// transitions between consecutive bars, and a skipped bar is a missed roll.
        /// </summary>
        public void Absorb(int bar, DateTime local, decimal open, decimal high, decimal low,
                           decimal close, decimal volume, decimal delta)
        {
            var phase = _cfg.PhaseOf(local.TimeOfDay);

            if (!_started)
            {
                _started = true;
                _phase = phase;

                // The chart starts wherever it starts. Whichever session is already underway had
                // its open off-screen, so nothing about it is claimed until a real open is seen.
                _devRthOpenSeen = false;
                _devOnOpenSeen = false;
            }
            else if (phase != _phase)
            {
                OnPhaseChange(_phase, phase, bar, open);
                _phase = phase;
            }

            if (Accumulating(phase) && _anchorBar >= 0)
            {
                var typical = (high + low + close) / 3m;
                _sumVol += volume;
                _sumVolTyp += volume * typical;
                _sumVolTyp2 += volume * typical * typical;
                _cvd += delta;

                Push(ref _cvdSeries, ref _cvdCount, _cvd);

                var vwap = Vwap;
                Push(ref _vwapSeries, ref _vwapCount, vwap.Known ? vwap.Value : close);
            }

            if (phase == Phase.Rth)
            {
                if (BarsIntoRth >= 0) BarsIntoRth++;

                if (!_devRthAny) { _devRthHigh = high; _devRthLow = low; _devRthAny = true; }
                else
                {
                    if (high > _devRthHigh) _devRthHigh = high;
                    if (low < _devRthLow) _devRthLow = low;
                }
                _devRthClose = close;
            }
            else if (phase == Phase.Overnight)
            {
                if (!_devOnAny) { _devOnHigh = high; _devOnLow = low; _devOnAny = true; }
                else
                {
                    if (high > _devOnHigh) _devOnHigh = high;
                    if (low < _devOnLow) _devOnLow = low;
                }
                _devOnClose = close;

                // Overnight refs are the block as it stands, so the ladder is readable at 3 AM.
                if (_devOnOpenSeen)
                {
                    OnHigh = Level.At(_devOnHigh);
                    OnLow = Level.At(_devOnLow);
                    OnClose = Level.At(_devOnClose);
                }
            }
        }

        /// <summary>
        /// The trading day rolls at the evening reopen, not midnight: at 17:00 the cash session
        /// that just finished becomes "prior day", which is when a trader starts reading it that
        /// way. Every carried-over number is claimed only when its own open was on the chart.
        /// </summary>
        private void OnPhaseChange(Phase from, Phase to, int bar, decimal open)
        {
            if (to == Phase.Overnight)
            {
                if (_devRthOpenSeen && _devRthAny)
                {
                    PriorRthHigh = Level.At(_devRthHigh);
                    PriorRthLow = Level.At(_devRthLow);
                    PriorRthClose = Level.At(_devRthClose);
                }

                _devOnHigh = _devOnLow = _devOnClose = 0m;
                _devOnAny = false;
                _devOnOpenSeen = true;
                OnHigh = OnLow = OnClose = Level.None;
            }
            else if (to == Phase.Rth)
            {
                if (_devOnOpenSeen && _devOnAny)
                {
                    OnHigh = Level.At(_devOnHigh);
                    OnLow = Level.At(_devOnLow);
                    OnClose = Level.At(_devOnClose);
                }
                else
                {
                    OnHigh = OnLow = OnClose = Level.None;
                }

                _devRthHigh = _devRthLow = _devRthClose = 0m;
                _devRthAny = false;
                _devRthOpenSeen = true;
                BarsIntoRth = 0;
                RthOpen = Level.At(open);
            }

            // Anchored on the cash open: one reset a day, at 08:30. Anchored on the evening
            // reopen: the accumulators run the whole trading day, so the reset is at 17:00 --
            // and a chart that begins mid-morning still anchors at the open it does see.
            // Each session: a fresh start on entering either one, so the overnight VWAP never
            // carries into the cash open and the cash VWAP never carries into the evening.
            var anchorHere = _cfg.Anchor == SessionAnchor.RthOpen ? to == Phase.Rth
                : _cfg.Anchor == SessionAnchor.EachSession ? to == Phase.Rth || to == Phase.Overnight
                : to == Phase.Overnight || (to == Phase.Rth && from == Phase.Between);

            if (anchorHere) { ResetAnchor(bar); return; }

            // Leaving the anchor window CLOSES it rather than freezing it. Anchored on the cash
            // open, yesterday's session VWAP is not a level anything overnight trades against,
            // and leaving it standing would have F1 scoring 3 AM against a number six hours
            // dead. With the window shut there is no VWAP, and the factor abstains and says so.
            if (!Accumulating(to)) CloseAnchor();
        }

        private void CloseAnchor()
        {
            _sumVol = _sumVolTyp = _sumVolTyp2 = 0m;
            _cvd = 0m;
            _cvdCount = 0;
            _vwapCount = 0;
            _anchorBar = -1;
        }

        private void ResetAnchor(int bar)
        {
            _sumVol = _sumVolTyp = _sumVolTyp2 = 0m;
            _cvd = 0m;
            _cvdCount = 0;
            _vwapCount = 0;
            _anchorBar = bar;
        }

        private static void Push(ref decimal[] buffer, ref int count, decimal value)
        {
            if (count == buffer.Length)
            {
                var bigger = new decimal[buffer.Length * 2];
                Array.Copy(buffer, bigger, count);
                buffer = bigger;
            }
            buffer[count++] = value;
        }
    }

    /// <summary>
    /// Mean of the last N values, kept in a fixed ring. No LINQ, no reallocation, nothing that
    /// walks history -- this is read on the per-tick path.
    /// </summary>
    public sealed class RingMean
    {
        private decimal[] _values;
        private int _next;
        private int _filled;
        private decimal _sum;

        public RingMean(int window) { _values = new decimal[Math.Max(1, window)]; }

        public RingMean CloneForWhatIf()
        {
            var c = (RingMean)MemberwiseClone();
            c._values = (decimal[])_values.Clone();
            return c;
        }

        public int Count => _filled;

        public void Add(decimal value)
        {
            if (_filled == _values.Length) _sum -= _values[_next];
            else _filled++;

            _values[_next] = value;
            _sum += value;
            _next = (_next + 1) % _values.Length;
        }

        /// <summary>The mean, or absent while it is zero -- a zero yardstick measures nothing.</summary>
        public Level Mean
        {
            get
            {
                if (_filled == 0) return Level.None;

                var mean = _sum / _filled;
                return mean > 0m ? Level.At(mean) : Level.None;
            }
        }
    }
}
