using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansAsiaWick
{
    /// <summary>The three ways a night can signal. Each fires at most once per night.</summary>
    public enum SignalVariant
    {
        /// <summary>Swept the prior RTH high and closed back below it.</summary>
        PdhSweep = 0,

        /// <summary>Swept the running Asia high and closed back below it.</summary>
        AsiaHighSweep = 1,

        /// <summary>New Asia high rejected by a top-heavy wick, closing in the lower half.</summary>
        WickRejection = 2
    }

    /// <summary>Where the position is flattened. All Central.</summary>
    public enum ExitTime
    {
        LondonOpen = 0,
        NyOpen = 1,
        NyClose = 2
    }

    /// <summary>One fired signal. The stop reference is the signal bar's high.</summary>
    public sealed class WickSignal
    {
        public int Bar;
        public DateTime TimeCt;
        public DateTime TradeDate;
        public SignalVariant Variant;
        public decimal SignalPrice;
        public decimal StopReference;

        /// <summary>Stable per-night, per-variant key. The alert dedupe hangs off this.</summary>
        public string Key
        {
            get
            {
                return TradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                       "/" + Variant;
            }
        }

        public override string ToString()
        {
            return TimeCt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                   " " + Variant + " @ " + SignalPrice + " stop-ref " + StopReference;
        }
    }

    /// <summary>
    /// Everything the signal core reads. Held apart from the ATAS setting properties so the
    /// engine can be driven from the test harness with no platform present.
    /// </summary>
    public sealed class AsiaWickSettings
    {
        public TimeSpan AsiaStart = new TimeSpan(18, 0, 0);
        public TimeSpan AsiaEnd = new TimeSpan(2, 0, 0);
        public TimeSpan RthStart = new TimeSpan(8, 30, 0);
        public TimeSpan RthEnd = new TimeSpan(15, 0, 0);
        public ExitTime Exit = ExitTime.NyOpen;

        public decimal WickPct = 0.50m;

        public bool EnablePdhSweep = true;
        public bool EnableAsiaHighSweep = true;
        public bool EnableWickRejection = true;

        /// <summary>Diagnostic mirror. Ships off; the tested edge is short-only.</summary>
        public bool LongMirror = false;

        public TimeSpan ExitAt
        {
            get
            {
                switch (Exit)
                {
                    case ExitTime.LondonOpen: return new TimeSpan(2, 0, 0);
                    case ExitTime.NyClose: return new TimeSpan(15, 0, 0);
                    default: return new TimeSpan(8, 30, 0);
                }
            }
        }

        public bool Enabled(SignalVariant v)
        {
            switch (v)
            {
                case SignalVariant.PdhSweep: return EnablePdhSweep;
                case SignalVariant.AsiaHighSweep: return EnableAsiaHighSweep;
                default: return EnableWickRejection;
            }
        }
    }

    /// <summary>What one folded bar produced. Everything the renderer needs, nothing more.</summary>
    public struct BarState
    {
        /// <summary>Prior completed RTH high, or 0 when there is none to draw.</summary>
        public decimal PdhLevel;

        /// <summary>Running Asia high, or 0 outside the Asia window.</summary>
        public decimal AsiaLevel;

        public bool InAsia;
        public bool InDisplayWindow;

        /// <summary>True on the bar the Asia window closed, so the line can be broken there.</summary>
        public bool AsiaJustEnded;

        public DateTime TradeDate;
        public DateTime TimeCt;
    }

    /// <summary>
    /// The shared signal core. One implementation, two consumers: the indicator draws what it
    /// produces, the strategy trades it.
    ///
    /// Bars are folded in strictly forward, one at a time, closed bars only. State is explicit
    /// and per-night rather than derived by looking backwards over the series -- verbose and
    /// dependable beats elegant, and it is what makes a rerun over the same history produce a
    /// byte-identical signal set (ATAS recalculates constantly).
    /// </summary>
    public sealed class AsiaWickEngine
    {
        /// <summary>The Globex session rolls at 17:00 Central. All per-night state keys off this.</summary>
        public static readonly TimeSpan TradeDateRoll = new TimeSpan(17, 0, 0);

        private readonly AsiaWickSettings _s;
        private readonly SessionClock _clock;

        // --- carried across nights ---
        private decimal? _pdh;          // high of the prior COMPLETED RTH window
        private decimal _rthHigh;
        private bool _inRth;

        // --- per night ---
        private DateTime _tradeDate = DateTime.MinValue;
        private bool _nightOpen;
        private decimal? _asiaHigh;
        private bool _wasInAsia;
        private readonly bool[] _fired = new bool[3];

        private readonly List<WickSignal> _signals = new List<WickSignal>();

        public AsiaWickEngine(AsiaWickSettings settings, SessionClock clock)
        {
            _s = settings;
            _clock = clock;
        }

        /// <summary>Every signal produced so far, in bar order.</summary>
        public IReadOnlyList<WickSignal> Signals { get { return _signals; } }

        /// <summary>The night is still unspent: no variant has fired since the 17:00 roll.</summary>
        public bool Armed { get { return _nightOpen && !_fired[0] && !_fired[1] && !_fired[2]; } }

        public DateTime TradeDate { get { return _tradeDate; } }
        public decimal? Pdh { get { return _pdh; } }
        public decimal? AsiaHigh { get { return _asiaHigh; } }

        /// <summary>
        /// The Globex trade date: the session rolls at 17:00 Central, so anything from 17:00
        /// onwards belongs to that calendar day and anything before it to the day before.
        /// </summary>
        public static DateTime TradeDateOf(DateTime ct)
        {
            return (ct - TradeDateRoll).Date;
        }

        /// <summary>
        /// Half-open [start, end). Handles a window that crosses midnight, which the Asia window
        /// does. 17:59 out / 18:00 in / 01:59 in / 02:00 out.
        /// </summary>
        public static bool InWindow(TimeSpan t, TimeSpan start, TimeSpan end)
        {
            if (start == end) return false;
            if (start < end) return t >= start && t < end;
            return t >= start || t < end;
        }

        public void Reset()
        {
            _pdh = null;
            _rthHigh = 0m;
            _inRth = false;

            _tradeDate = DateTime.MinValue;
            _nightOpen = false;
            _asiaHigh = null;
            _wasInAsia = false;
            _fired[0] = _fired[1] = _fired[2] = false;

            _signals.Clear();
        }

        /// <summary>
        /// Fold one CLOSED bar. Returns what to draw for it, and appends any new signal to
        /// <see cref="Signals"/>. Bars must arrive in order and exactly once.
        /// </summary>
        public BarState Fold(int bar, DateTime barTime, decimal open, decimal high,
                             decimal low, decimal close, List<WickSignal> newSignals)
        {
            var ct = _clock.ToLocal(barTime);
            var td = TradeDateOf(ct);

            if (td != _tradeDate)
            {
                // 17:00 roll. PDH survives it; everything scoped to the night does not.
                _tradeDate = td;
                _nightOpen = true;
                _asiaHigh = null;
                _wasInAsia = false;
                _fired[0] = _fired[1] = _fired[2] = false;
            }

            var tod = ct.TimeOfDay;

            // --- prior-day high: track the running RTH window, commit it when it ends ---
            var inRth = InWindow(tod, _s.RthStart, _s.RthEnd);

            if (inRth)
            {
                if (!_inRth) _rthHigh = high;
                else if (high > _rthHigh) _rthHigh = high;
                _inRth = true;
            }
            else if (_inRth)
            {
                // The window just completed. Whatever it produced is the prior-day high --
                // which is also what makes half-days correct without a special case.
                _pdh = _rthHigh;
                _inRth = false;
            }

            // --- Asia window: evaluate against state that EXCLUDES this bar, then fold it in ---
            var inAsia = InWindow(tod, _s.AsiaStart, _s.AsiaEnd);

            var st = new BarState
            {
                TradeDate = td,
                TimeCt = ct,
                InAsia = inAsia,
                AsiaJustEnded = !inAsia && _wasInAsia,
                PdhLevel = _pdh.HasValue ? _pdh.Value : 0m,
                AsiaLevel = 0m,
                InDisplayWindow = InDisplay(tod, inAsia)
            };

            if (inAsia)
            {
                Evaluate(bar, ct, td, open, high, low, close, newSignals);

                // Only now does this bar count towards the Asia high.
                if (!_asiaHigh.HasValue || high > _asiaHigh.Value) _asiaHigh = high;

                st.AsiaLevel = _asiaHigh.Value;
            }

            _wasInAsia = inAsia;
            return st;
        }

        /// <summary>
        /// The PDH line runs from the Asia open through the morning exit -- not during the RTH
        /// window that produced it, where it would just trace the day's own high.
        /// </summary>
        private bool InDisplay(TimeSpan tod, bool inAsia)
        {
            if (inAsia) return true;

            var exit = _s.ExitAt;

            // London exit lands on the Asia close, so there is no post-Asia stretch to draw.
            if (exit <= _s.AsiaEnd) return false;

            return tod >= _s.AsiaEnd && tod < exit;
        }

        private void Evaluate(int bar, DateTime ct, DateTime td, decimal open, decimal high,
                              decimal low, decimal close, List<WickSignal> newSignals)
        {
            var range = high - low;
            var upperWick = high - Math.Max(open, close);

            // 1. Prior-day-high sweep: traded through it, closed back under.
            if (_pdh.HasValue && high > _pdh.Value && close < _pdh.Value)
                Fire(SignalVariant.PdhSweep, bar, ct, td, close, high, newSignals);

            // 2. Asia-high sweep. The level must predate this bar, which is why the fold happens
            //    after evaluation -- otherwise every bar sweeps itself.
            if (_asiaHigh.HasValue && high > _asiaHigh.Value && close < _asiaHigh.Value)
                Fire(SignalVariant.AsiaHighSweep, bar, ct, td, close, high, newSignals);

            // 3. Wick rejection off a new Asia high (the first Asia bar counts as one).
            var newHigh = !_asiaHigh.HasValue || high > _asiaHigh.Value;

            if (newHigh && range > 0m &&
                upperWick / range >= _s.WickPct &&
                close < low + range / 2m)
                Fire(SignalVariant.WickRejection, bar, ct, td, close, high, newSignals);
        }

        private void Fire(SignalVariant v, int bar, DateTime ct, DateTime td, decimal price,
                          decimal stopRef, List<WickSignal> newSignals)
        {
            if (!_s.Enabled(v)) return;

            var i = (int)v;
            if (_fired[i]) return;      // first signal per variant per night
            _fired[i] = true;

            var sig = new WickSignal
            {
                Bar = bar,
                TimeCt = ct,
                TradeDate = td,
                Variant = v,
                SignalPrice = price,
                StopReference = stopRef
            };

            _signals.Add(sig);
            if (newSignals != null) newSignals.Add(sig);
        }
    }

    /// <summary>What to do to a level series for one bar.</summary>
    public struct SegmentStep
    {
        /// <summary>Terminate the line at bar-1 before touching this bar.</summary>
        public bool BreakBefore;

        public bool HasValue;
        public decimal Value;
    }

    /// <summary>
    /// Turns a per-bar level -- a price while a window is open, zero while it is not -- into the
    /// draw instructions for a series that must appear as separate segments.
    ///
    /// A run has to be cut at BOTH ends. Cutting only the end is the bug this class exists to
    /// stop: the untouched gap bars still hold zero and still render, so the last of them gets
    /// joined to the first real value and the segment grows a vertical line from the level down
    /// to price zero. It shows up on the LEFT edge of every segment, because the right edge was
    /// already broken, and it runs straight off the bottom of the chart.
    ///
    /// Pure, so the sequence of breaks is a unit test rather than something only the chart can
    /// tell you.
    /// </summary>
    public sealed class LineSegmenter
    {
        private bool _on;

        /// <summary>True while a segment is open.</summary>
        public bool Open { get { return _on; } }

        public void Reset()
        {
            _on = false;
        }

        public SegmentStep Step(int bar, decimal level)
        {
            var step = new SegmentStep();

            if (level > 0m)
            {
                // Opening a run: cut the zero-valued bar before it loose first.
                step.BreakBefore = !_on && bar > 0;
                step.HasValue = true;
                step.Value = level;
                _on = true;
                return step;
            }

            step.BreakBefore = _on && bar > 0;
            _on = false;
            return step;
        }
    }

    /// <summary>Odds and ends shared by both consumers.</summary>
    public static class AsiaWickUtil
    {
        /// <summary>
        /// Bar length in minutes, measured from the bar stamps rather than parsed out of
        /// ChartInfo.TimeFrame -- that string's format is not contractual, and a misparse would
        /// silently disable the timeframe guard. The median gap ignores session breaks.
        /// </summary>
        public static int BarMinutes(IBarWindow bars)
        {
            if (bars == null || bars.Count < 3) return 0;

            var gaps = new List<double>();
            var from = Math.Max(1, bars.Count - 500);

            for (var i = from; i < bars.Count; i++)
            {
                var d = (bars.Time(i) - bars.Time(i - 1)).TotalMinutes;
                if (d > 0 && d < 1440) gaps.Add(d);
            }

            if (gaps.Count == 0) return 0;

            gaps.Sort();
            return (int)Math.Round(gaps[gaps.Count / 2]);
        }

        /// <summary>
        /// Parses the tier-1 date list. Entries are the CALENDAR DATE OF THE RELEASE MORNING
        /// (a 07:30 CT CPI print on 2026-09-10 is "2026-09-10"), not the trade date of the
        /// night before. Unparseable entries are returned in <paramref name="bad"/> rather than
        /// dropped -- a silently ignored news date is a rule breach on a funded account.
        /// </summary>
        public static HashSet<DateTime> ParseDates(string csv, List<string> bad)
        {
            var set = new HashSet<DateTime>();
            if (string.IsNullOrWhiteSpace(csv)) return set;

            foreach (var raw in csv.Split(',', ';', '\n', '\r'))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;

                DateTime d;
                if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out d))
                    set.Add(d.Date);
                else if (bad != null)
                    bad.Add(s);
            }

            return set;
        }
    }
}
