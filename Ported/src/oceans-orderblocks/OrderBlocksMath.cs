using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansOrderBlocks
{
    // Everything in this file is free of ATAS types, so _test can run it on synthetic bars.
    // The adapter on the other side (OceansOrderBlocks*.cs) only converts candles and draws.

    public enum ObTf { H1 = 0, H4 = 1, Daily = 2, Weekly = 3, Session = 4 }

    public enum ZoneMode { WickToWick = 0, BodyOnly = 1 }

    public enum MitigationTrigger { Wick = 0, Close = 1 }

    public enum ZoneStatus { Fresh = 0, Tested = 1, Mitigated = 2 }

    public enum CvdAnchor { Session = 0, Daily = 1 }

    /// <summary>One chart bar, already on Houston time, with its real bid/ask delta.</summary>
    public struct ChartBar
    {
        public int Index;
        public DateTime Local;
        public decimal Open, High, Low, Close, Volume, Delta;
    }

    /// <summary>One footprint price level. Delta is ask volume minus bid volume.</summary>
    public struct Level
    {
        public decimal Price, Volume, Delta;

        public Level(decimal price, decimal volume, decimal delta)
        {
            Price = price;
            Volume = volume;
            Delta = delta;
        }
    }

    /// <summary>
    /// CME equity index session arithmetic, Houston time.
    ///
    /// The trade date rolls at the 17:00 reopen, and the day ends at the 16:00 halt -- not at
    /// the 15:00 cash close, which trading runs straight through. Higher-timeframe buckets are
    /// anchored to the reopen, the way TradingView aligns futures bars: 4H bars open at 17, 21,
    /// 01, 05, 09 and 13, and the last one is cut short by the halt.
    /// </summary>
    public static class Cme
    {
        public static readonly TimeSpan Reopen = new TimeSpan(17, 0, 0);
        public static readonly TimeSpan Halt = new TimeSpan(16, 0, 0);

        public static DateTime TradeDate(DateTime local)
        {
            return local.TimeOfDay >= Reopen ? local.Date.AddDays(1) : local.Date;
        }

        /// <summary>The 17:00 reopen that starts a trade date: the evening before it.</summary>
        public static DateTime SessionOpen(DateTime tradeDate)
        {
            return tradeDate.Date.AddDays(-1) + Reopen;
        }

        public static DateTime SessionHalt(DateTime tradeDate)
        {
            return tradeDate.Date + Halt;
        }

        public static DateTime BucketStart(ObTf tf, DateTime local)
        {
            switch (tf)
            {
                case ObTf.H1:
                    return new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);

                case ObTf.H4:
                    var open = SessionOpen(TradeDate(local));
                    var n = (int)Math.Floor((local - open).TotalHours / 4.0);
                    return open.AddHours(4 * n);

                case ObTf.Daily:
                    return SessionOpen(TradeDate(local));

                case ObTf.Weekly:
                    // Trade weeks run Sunday 17:00 to Friday 16:00, keyed by their Monday.
                    var td = TradeDate(local);
                    var back = ((int)td.DayOfWeek + 6) % 7;
                    return SessionOpen(td.AddDays(-back));

                default:
                    return local;
            }
        }

        /// <summary>When a bucket that opened at <paramref name="start"/> is done.</summary>
        public static DateTime BucketEnd(ObTf tf, DateTime start)
        {
            switch (tf)
            {
                case ObTf.H1:
                    return start.AddHours(1);

                case ObTf.H4:
                    var halt = SessionHalt(TradeDate(start));
                    var end = start.AddHours(4);
                    return end < halt ? end : halt;

                case ObTf.Daily:
                    return SessionHalt(TradeDate(start));

                case ObTf.Weekly:
                    return SessionHalt(TradeDate(start).AddDays(4));

                default:
                    return start;
            }
        }

        /// <summary>Nominal length, used only to refuse a timeframe finer than the chart.</summary>
        public static TimeSpan Nominal(ObTf tf)
        {
            switch (tf)
            {
                case ObTf.H1: return TimeSpan.FromHours(1);
                case ObTf.H4: return TimeSpan.FromHours(4);
                case ObTf.Daily: return TimeSpan.FromDays(1);
                case ObTf.Weekly: return TimeSpan.FromDays(7);
                default: return TimeSpan.Zero;
            }
        }
    }

    /// <summary>Wrap-aware time-of-day window. Start == end means all day.</summary>
    public static class Window
    {
        public static bool Contains(TimeSpan start, TimeSpan end, DateTime local)
        {
            var t = local.TimeOfDay;
            if (start < end) return t >= start && t < end;
            return t >= start || t < end;
        }
    }

    /// <summary>A higher-timeframe bar assembled from chart bars.</summary>
    public sealed class HtfBar
    {
        public DateTime Start;
        public int FirstBar, LastBar;
        public decimal Open, High, Low, Close, Volume, Delta;

        /// <summary>
        /// The first bucket of a loaded chart usually starts mid-bar: the history begins at
        /// whatever minute ATAS loaded from. Its OHLC is not the real bar's, so it may not be an
        /// order block candle, a breaker, or an ATR sample.
        /// </summary>
        public bool Partial;

        public static HtfBar Of(ChartBar b)
        {
            return new HtfBar
            {
                Start = b.Local,
                FirstBar = b.Index,
                LastBar = b.Index,
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
                Volume = b.Volume,
                Delta = b.Delta
            };
        }
    }

    /// <summary>
    /// Folds chart bars into one higher timeframe.
    ///
    /// A bucket closes the moment its last chart bar closes -- by time, when that bar's end
    /// reaches the bucket end -- or, when the bar size is unknown (tick and range charts) or a
    /// holiday cuts the day short, when the first bar of the next bucket arrives. Closing by
    /// time is what makes history and live agree: the block appears on the same chart bar
    /// either way, which is the exact thing the Pine version got wrong with lookahead_off.
    /// </summary>
    public sealed class HtfBuilder
    {
        public readonly ObTf Tf;

        private HtfBar _open;
        private bool _first = true;

        public HtfBuilder(ObTf tf) { Tf = tf; }

        public int CompleteBars { get; private set; }

        public void Push(ChartBar b, TimeSpan barDuration, List<HtfBar> closed)
        {
            var start = Cme.BucketStart(Tf, b.Local);

            if (_open != null && start != _open.Start) Close(closed);

            if (_open == null)
            {
                _open = HtfBar.Of(b);
                _open.Start = start;

                if (_first)
                {
                    _open.Partial = barDuration > TimeSpan.Zero
                        ? b.Local - start >= barDuration
                        : b.Local != start;
                    _first = false;
                }
            }
            else
            {
                if (b.High > _open.High) _open.High = b.High;
                if (b.Low < _open.Low) _open.Low = b.Low;
                _open.Close = b.Close;
                _open.Volume += b.Volume;
                _open.Delta += b.Delta;
                _open.LastBar = b.Index;
            }

            if (barDuration > TimeSpan.Zero && b.Local + barDuration >= Cme.BucketEnd(Tf, start))
                Close(closed);
        }

        private void Close(List<HtfBar> closed)
        {
            if (!_open.Partial) CompleteBars++;
            closed.Add(_open);
            _open = null;
        }
    }

    /// <summary>Wilder ATR, matching Pine's ta.atr: an SMA seed, then RMA.</summary>
    public sealed class WilderAtr
    {
        private readonly int _period;
        private decimal _sum, _value, _prevClose;
        private bool _hasPrev;

        public WilderAtr(int period) { _period = period; }

        public int Count { get; private set; }
        public bool Ready { get { return Count >= _period; } }
        public decimal Value { get { return Ready ? _value : 0m; } }

        public void Push(decimal high, decimal low, decimal close)
        {
            var tr = high - low;
            if (_hasPrev)
                tr = Math.Max(tr, Math.Max(Math.Abs(high - _prevClose), Math.Abs(low - _prevClose)));

            _prevClose = close;
            _hasPrev = true;
            Count++;

            if (Count < _period) _sum += tr;
            else if (Count == _period) _value = (_sum + tr) / _period;
            else _value = (_value * (_period - 1) + tr) / _period;
        }
    }

    public sealed class ObRules
    {
        public ZoneMode Mode = ZoneMode.WickToWick;

        /// <summary>Breaker body must be at least this many ATR14 of its timeframe. 0 = off.</summary>
        public decimal MinDisplacementAtr;

        /// <summary>
        /// Bull blocks need buyers to have done the breaking -- positive bid/ask delta on the
        /// breaker -- and bear blocks negative. Only possible with real footprint data.
        /// </summary>
        public bool BreakerDeltaAgrees;
    }

    public sealed class ObSignal
    {
        public bool Bull;
        public decimal Top, Bottom;
        public int StartBar;
        public DateTime ObStart, BreakerStart;

        /// <summary>The breaker's last chart bar. The zone is live from the bar after it.</summary>
        public int LiveFrom;

        public decimal BreakerDelta;
    }

    /// <summary>
    /// The Pine rule, on closed bars: an opposite-colour candle whose extreme is closed through
    /// by the next candle's body.
    /// </summary>
    public sealed class ObDetector
    {
        private readonly WilderAtr _atr = new WilderAtr(14);
        private HtfBar _prev;

        public int AtrCount { get { return _atr.Count; } }

        public ObSignal OnClose(HtfBar bar, ObRules r)
        {
            var prev = _prev;
            _prev = bar;

            if (bar.Partial) return null;
            _atr.Push(bar.High, bar.Low, bar.Close);

            if (prev == null || prev.Partial) return null;

            var bull = prev.Close < prev.Open && bar.Close > bar.Open && bar.Close > prev.High;
            var bear = prev.Close > prev.Open && bar.Close < bar.Open && bar.Close < prev.Low;
            if (!bull && !bear) return null;

            if (r.MinDisplacementAtr > 0m)
            {
                if (!_atr.Ready) return null;
                if (Math.Abs(bar.Close - bar.Open) < r.MinDisplacementAtr * _atr.Value) return null;
            }

            if (r.BreakerDeltaAgrees)
            {
                if (bull && bar.Delta <= 0m) return null;
                if (bear && bar.Delta >= 0m) return null;
            }

            var body = r.Mode == ZoneMode.BodyOnly;

            return new ObSignal
            {
                Bull = bull,
                Top = body ? Math.Max(prev.Open, prev.Close) : prev.High,
                Bottom = body ? Math.Min(prev.Open, prev.Close) : prev.Low,
                StartBar = prev.FirstBar,
                ObStart = prev.Start,
                BreakerStart = bar.Start,
                LiveFrom = bar.LastBar,
                BreakerDelta = bar.Delta
            };
        }
    }

    public sealed class Zone
    {
        public long Id;
        public ObTf Tf;
        public bool Bull;
        public decimal Top, Bottom;
        public int StartBar, LiveFrom;
        public int EndBar = -1;
        public decimal BreakerDelta;

        public ZoneStatus Status = ZoneStatus.Fresh;
        public int Touches;
        public bool Inside;

        // In-zone footprint: what actually traded at prices inside the zone since it went
        // live. This is the honest version of the Pine "zone delta", which measured the whole
        // market since confirmation whether price was anywhere near the zone or not.
        public decimal FlowVolume, FlowDelta;
        public decimal Poc, PocVolume;
        public readonly Dictionary<decimal, decimal> VolAt = new Dictionary<decimal, decimal>();

        public decimal Mid { get { return (Top + Bottom) / 2m; } }

        public bool Overlaps(decimal low, decimal high)
        {
            return low <= Top && high >= Bottom;
        }

        /// <summary>The first time price comes back into a zone. What the alert fires on.</summary>
        public bool FreshTouch(decimal low, decimal high, int barIndex)
        {
            return Status == ZoneStatus.Fresh && barIndex > LiveFrom && Overlaps(low, high);
        }
    }

    public static class ZoneFlow
    {
        /// <summary>
        /// Commits one closed bar's levels inside the zone. Edges are inclusive: a print at the
        /// exact top of a demand zone traded at the zone.
        /// </summary>
        public static void Add(Zone z, IList<Level> levels)
        {
            foreach (var l in levels)
            {
                if (l.Price < z.Bottom || l.Price > z.Top || l.Volume <= 0m) continue;

                z.FlowVolume += l.Volume;
                z.FlowDelta += l.Delta;

                decimal v;
                z.VolAt.TryGetValue(l.Price, out v);
                v += l.Volume;
                z.VolAt[l.Price] = v;

                // Level volumes only grow, so a running argmax stays exact. Ties keep the level
                // that got there first rather than flickering between two.
                if (v > z.PocVolume)
                {
                    z.PocVolume = v;
                    z.Poc = l.Price;
                }
            }
        }

        /// <summary>The forming bar's share, measured without committing it.</summary>
        public static void Measure(Zone z, IList<Level> levels, out decimal volume, out decimal delta)
        {
            volume = 0m;
            delta = 0m;

            foreach (var l in levels)
            {
                if (l.Price < z.Bottom || l.Price > z.Top || l.Volume <= 0m) continue;
                volume += l.Volume;
                delta += l.Delta;
            }
        }
    }

    public sealed class ZoneBook
    {
        public readonly List<Zone> Active = new List<Zone>();
        public readonly List<Zone> Faded = new List<Zone>();

        public int MaxPerSide = 10;
        public bool KeepMitigated;
        public int MaxFaded = 20;
        public MitigationTrigger Trigger = MitigationTrigger.Wick;

        private long _nextId = 1;

        public Zone Add(ObTf tf, ObSignal s)
        {
            var z = new Zone
            {
                Id = _nextId++,
                Tf = tf,
                Bull = s.Bull,
                Top = s.Top,
                Bottom = s.Bottom,
                StartBar = s.StartBar,
                LiveFrom = s.LiveFrom,
                BreakerDelta = s.BreakerDelta
            };

            Active.Add(z);

            // The cap is per timeframe AND side, so a busy 1H cannot push a weekly block off.
            var count = 0;
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var a = Active[i];
                if (a.Tf != tf || a.Bull != s.Bull) continue;

                count++;
                if (count > MaxPerSide) Active.RemoveAt(i);
            }

            return z;
        }

        /// <summary>
        /// Touch counting and full mitigation for one closed chart bar. Mitigation is price
        /// trading through the FAR side: below the bottom of demand, above the top of supply.
        /// The breaker itself is never checked -- the zone is only live from the bar after it.
        /// </summary>
        public void Advance(ChartBar b)
        {
            // Oldest first, so blocks mitigated on the same bar enter the faded list in age
            // order and the cap drops the oldest of them, not the newest.
            for (var i = 0; i < Active.Count; i++)
            {
                var z = Active[i];
                if (b.Index <= z.LiveFrom) continue;

                var inside = z.Overlaps(b.Low, b.High);
                if (inside && !z.Inside)
                {
                    z.Touches++;
                    if (z.Status == ZoneStatus.Fresh) z.Status = ZoneStatus.Tested;
                }

                z.Inside = inside;

                var hit = z.Bull
                    ? (Trigger == MitigationTrigger.Close ? b.Close : b.Low) < z.Bottom
                    : (Trigger == MitigationTrigger.Close ? b.Close : b.High) > z.Top;

                if (!hit) continue;

                z.Status = ZoneStatus.Mitigated;
                z.EndBar = b.Index;
                Active.RemoveAt(i);
                i--;

                // A bounded list of its own. The Pine version left faded boxes in no list at
                // all, and TradingView's 500-drawing collector then deleted the oldest drawings
                // -- which were the oldest ACTIVE weekly and daily blocks.
                if (!KeepMitigated) continue;

                Faded.Add(z);
                while (Faded.Count > MaxFaded) Faded.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Real cumulative delta. Session mode resets at the first bar inside the window and
    /// freezes outside it, so after 15:00 it still reads RTH and nothing else. Daily mode
    /// resets at the 17:00 reopen.
    /// </summary>
    public sealed class CvdTracker
    {
        private readonly CvdAnchor _mode;
        private readonly TimeSpan _start, _end;
        private bool _wasIn;
        private DateTime _tradeDate;

        public CvdTracker(CvdAnchor mode, TimeSpan start, TimeSpan end)
        {
            _mode = mode;
            _start = start;
            _end = end;
        }

        public decimal Value { get; private set; }

        public void Push(DateTime local, decimal delta)
        {
            Value = Next(local, delta);

            if (_mode == CvdAnchor.Session) _wasIn = Window.Contains(_start, _end, local);
            else _tradeDate = Cme.TradeDate(local);
        }

        /// <summary>What the value would be with the forming bar included.</summary>
        public decimal Peek(DateTime local, decimal delta)
        {
            return Next(local, delta);
        }

        private decimal Next(DateTime local, decimal delta)
        {
            if (_mode == CvdAnchor.Daily)
                return Cme.TradeDate(local) != _tradeDate ? delta : Value + delta;

            if (!Window.Contains(_start, _end, local)) return Value;
            return _wasIn ? Value + delta : delta;
        }
    }

    public sealed class EngineConfig
    {
        public readonly bool[] Enabled = new bool[5];
        public TimeSpan SessionStart = new TimeSpan(8, 30, 0);
        public TimeSpan SessionEnd = new TimeSpan(15, 0, 0);
        public ObRules Rules = new ObRules();
        public int MaxPerSide = 10;
        public bool KeepMitigated;
        public int MaxFaded = 20;
        public MitigationTrigger Trigger = MitigationTrigger.Wick;
        public CvdAnchor Cvd = CvdAnchor.Session;
        public TimeSpan BarDuration;
    }

    /// <summary>
    /// The whole per-bar pipeline, in the order it has to run:
    ///
    ///   1. flow    -- the bar's footprint into every live zone it overlaps
    ///   2. advance -- touches and mitigation
    ///   3. CVD
    ///   4. detect  -- higher-timeframe closes, then the session detector
    ///
    /// Detection runs last so a block born on this bar is first checked on the next one,
    /// which is where Pine starts checking it too.
    /// </summary>
    public sealed class ObEngine
    {
        private readonly EngineConfig _c;
        private readonly HtfBuilder[] _builders = new HtfBuilder[4];
        private readonly ObDetector[] _detectors = new ObDetector[5];
        private readonly List<HtfBar> _closed = new List<HtfBar>();

        public readonly ZoneBook Book;
        public readonly CvdTracker Cvd;

        public ObEngine(EngineConfig c)
        {
            _c = c;

            for (var i = 0; i < 4; i++) _builders[i] = new HtfBuilder((ObTf)i);
            for (var i = 0; i < 5; i++) _detectors[i] = new ObDetector();

            Book = new ZoneBook
            {
                MaxPerSide = c.MaxPerSide,
                KeepMitigated = c.KeepMitigated,
                MaxFaded = c.MaxFaded,
                Trigger = c.Trigger
            };

            Cvd = new CvdTracker(c.Cvd, c.SessionStart, c.SessionEnd);
        }

        public int CompleteBars(ObTf tf) { return tf == ObTf.Session ? 0 : _builders[(int)tf].CompleteBars; }

        public int AtrCount(ObTf tf) { return _detectors[(int)tf].AtrCount; }

        /// <param name="levels">
        /// Called at most once, and only when some live zone overlaps the bar. Reading a
        /// footprint costs a walk over every level, so bars nowhere near a zone never pay it.
        /// Null from it means no footprint on this feed.
        /// </param>
        public void Fold(ChartBar b, Func<IList<Level>> levels)
        {
            IList<Level> lv = null;
            var asked = false;

            foreach (var z in Book.Active)
            {
                if (b.Index <= z.LiveFrom || !z.Overlaps(b.Low, b.High)) continue;

                if (!asked)
                {
                    lv = levels != null ? levels() : null;
                    asked = true;
                }

                if (lv == null) break;
                ZoneFlow.Add(z, lv);
            }

            Book.Advance(b);
            Cvd.Push(b.Local, b.Delta);

            for (var i = 0; i < 4; i++)
            {
                if (!_c.Enabled[i]) continue;

                _closed.Clear();
                _builders[i].Push(b, _c.BarDuration, _closed);

                foreach (var h in _closed)
                {
                    var s = _detectors[i].OnClose(h, _c.Rules);
                    if (s != null) Book.Add((ObTf)i, s);
                }
            }

            if (_c.Enabled[(int)ObTf.Session])
            {
                var s = _detectors[(int)ObTf.Session].OnClose(HtfBar.Of(b), _c.Rules);

                // Both candles inside the window and on the same trade date. Without the date
                // test an RTH-only chart pairs yesterday's 14:59 bar with today's 08:30 one.
                if (s != null &&
                    Window.Contains(_c.SessionStart, _c.SessionEnd, s.ObStart) &&
                    Window.Contains(_c.SessionStart, _c.SessionEnd, s.BreakerStart) &&
                    Cme.TradeDate(s.ObStart) == Cme.TradeDate(s.BreakerStart))
                    Book.Add(ObTf.Session, s);
            }
        }
    }

    public static class Fmt
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>950, 12.3K, 1.25M. Sign only when negative.</summary>
        public static string Compact(decimal v)
        {
            var sign = v < 0m ? "-" : string.Empty;
            var a = Math.Abs(v);

            if (a < 1000m) return sign + Math.Round(a, 0).ToString(Inv);
            if (a < 1000000m) return sign + Math.Round(a / 1000m, 1).ToString("0.#", Inv) + "K";
            return sign + Math.Round(a / 1000000m, 2).ToString("0.##", Inv) + "M";
        }

        /// <summary>Compact with an explicit + on positives, for deltas.</summary>
        public static string Signed(decimal v)
        {
            return (v > 0m ? "+" : string.Empty) + Compact(v);
        }

        public static string Price(decimal v)
        {
            return v.ToString("0.00", Inv);
        }

        public static string Tag(ObTf tf)
        {
            switch (tf)
            {
                case ObTf.H1: return "1H";
                case ObTf.H4: return "4H";
                case ObTf.Daily: return "D";
                case ObTf.Weekly: return "W";
                default: return "S";
            }
        }
    }
}
