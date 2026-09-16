using System;
using System.Collections.Generic;

namespace OceansOrbBreakout
{
    /// <summary>Where the session VWAP starts counting.</summary>
    public enum VwapAnchor
    {
        /// <summary>The regular session open -- 8:30 AM Houston for MNQ.</summary>
        SessionOpen = 0,

        /// <summary>The overnight reopen -- 5:00 PM Houston the evening before.</summary>
        OvernightOpen = 1
    }

    /// <summary>One bar where every entry condition was true at the same time.</summary>
    public sealed class OrbSignal
    {
        public int Bar;
        public DateTime Local;
        public decimal Close;
        public decimal Volume;

        /// <summary>Bar volume as a multiple of the opening range average bar volume.</summary>
        public decimal VolumeRatio;

        public decimal Vwap;
        public decimal OrHigh;
    }

    /// <summary>The opening range for one trading day, and whatever broke out of it.</summary>
    public sealed class DayOrb
    {
        /// <summary>Trading day on your clock. Bars from 5 PM onward belong to the NEXT day,
        /// which is how the exchange counts and how the overnight VWAP anchor lines up.</summary>
        public DateTime Day;

        public int FirstBar = -1;
        public int LastBar = -1;

        /// <summary>Last bar of the regular session, where the levels stop extending.</summary>
        public int SessionEndBar = -1;

        public DateTime OrStartLocal;
        public DateTime OrEndLocal;
        public DateTime CutoffLocal;

        public int OrStartBar = -1;
        public int OrEndBar = -1;
        public int OrBars;
        public decimal OrHigh;
        public decimal OrLow;
        public decimal OrVolume;

        /// <summary>A bar at or past the end of the range has printed, so the range is settled.</summary>
        public bool RangeComplete;

        /// <summary>A bar opens exactly on each range boundary. False means the chart bars do
        /// not divide the range -- a 7-minute chart, or tick/volume/range bars -- so the high
        /// is being read off bars that straddle it.</summary>
        public bool Aligned;

        /// <summary>Last bar still inside the signal window.</summary>
        public int WindowEndBar = -1;

        public int VwapStartBar = -1;
        public readonly List<decimal> Vwap = new List<decimal>();

        public readonly List<OrbSignal> Signals = new List<OrbSignal>();

        public bool HasRange => OrBars > 0;

        /// <summary>Average volume of one bar in the opening range -- what condition two is
        /// measured against. Zero when the feed carried no volume, which must never be
        /// allowed to pass the test.</summary>
        public decimal OrAverageVolume => OrBars > 0 ? OrVolume / OrBars : 0m;

        /// <summary>A one-bar range is arithmetically fine and practically meaningless: the
        /// average is that one bar. Reported, never silently used.</summary>
        public bool ThinRange => OrBars == 1;

        public bool HasVwap => Vwap.Count > 0;

        /// <summary>Session VWAP as of the given bar, or 0 before the anchor.</summary>
        public decimal VwapAt(int bar)
        {
            var i = bar - VwapStartBar;
            return VwapStartBar >= 0 && i >= 0 && i < Vwap.Count ? Vwap[i] : 0m;
        }
    }

    public sealed class OrbConfig
    {
        public TimeContext Time;

        /// <summary>Regular session open. Every time in here is Houston time.</summary>
        public TimeSpan SessionOpen = new TimeSpan(8, 30, 0);

        public TimeSpan SessionClose = new TimeSpan(15, 0, 0);

        /// <summary>Length of the opening range in minutes, measured from the session open.</summary>
        public int OpeningRangeMinutes = 30;

        /// <summary>Last moment a signal may print.</summary>
        public TimeSpan Cutoff = new TimeSpan(10, 30, 0);

        /// <summary>Overnight reopen, and with it the boundary between trading days.</summary>
        public TimeSpan OvernightOpen = new TimeSpan(17, 0, 0);

        public decimal VolumeMultiple = 1.5m;
        public bool RequireAboveVwap = true;
        public bool FirstSignalOnly = true;
        public VwapAnchor Anchor = VwapAnchor.SessionOpen;

        /// <summary>Ignore the newest bar, which is still forming and whose volume is partial.
        /// Off makes signals appear and disappear intrabar.</summary>
        public bool ConfirmOnClose = true;

        public int Days = 5;
    }

    public sealed class OrbModel
    {
        public readonly List<DayOrb> Days = new List<DayOrb>();
        public DateTime LastBarLocal;

        /// <summary>The trading day a bar belongs to: the evening session trades the next day.</summary>
        public static DateTime TradingDay(DateTime local, TimeSpan rollover)
        {
            return local.TimeOfDay >= rollover ? local.Date.AddDays(1) : local.Date;
        }

        public static OrbModel Build(IBarWindow bars, OrbConfig cfg)
        {
            var model = new OrbModel();
            if (bars == null || bars.Count == 0 || cfg == null || cfg.Time == null || !cfg.Time.Valid)
                return model;

            var last = bars.Count - 1;
            model.LastBarLocal = cfg.Time.ToLocal(bars.Time(last));

            var orStart = cfg.SessionOpen;
            var orEnd = cfg.SessionOpen + TimeSpan.FromMinutes(Math.Max(1, cfg.OpeningRangeMinutes));

            var startBar = FirstBarOfRecentDays(bars, cfg, last, Math.Max(1, cfg.Days));

            DayOrb day = null;
            var pv = 0m;
            var vol = 0m;

            for (var i = startBar; i <= last; i++)
            {
                var t = cfg.Time.ToLocal(bars.Time(i));
                var stamp = TradingDay(t, cfg.OvernightOpen);

                if (day == null || day.Day != stamp)
                {
                    day = new DayOrb
                    {
                        Day = stamp,
                        OrStartLocal = stamp.Add(orStart),
                        OrEndLocal = stamp.Add(orEnd),
                        CutoffLocal = stamp.Add(cfg.Cutoff)
                    };
                    model.Days.Add(day);
                    pv = 0m;
                    vol = 0m;
                }

                if (day.FirstBar < 0) day.FirstBar = i;
                day.LastBar = i;

                var tod = t.TimeOfDay;

                // Bars stamped with the trading day's own date are its regular session; the
                // evening bars that opened the day carry the PREVIOUS date.
                var sameDate = t.Date == day.Day;

                if (sameDate && tod < cfg.SessionClose) day.SessionEndBar = i;

                AccumulateVwap(day, cfg, bars, i, sameDate, tod, ref pv, ref vol);

                if (sameDate && tod >= orStart && tod < orEnd)
                {
                    var h = bars.High(i);
                    var l = bars.Low(i);

                    if (day.OrStartBar < 0)
                    {
                        day.OrStartBar = i;
                        day.Aligned = tod == orStart;
                        day.OrHigh = h;
                        day.OrLow = l;
                    }
                    else
                    {
                        if (h > day.OrHigh) day.OrHigh = h;
                        if (l < day.OrLow) day.OrLow = l;
                    }

                    day.OrEndBar = i;
                    day.OrBars++;
                    day.OrVolume += bars.Volume(i);
                    continue;
                }

                if (!sameDate || tod < orEnd) continue;

                // Past the range. A bar landing on the boundary settles the range and is itself
                // the first breakout candidate, so this runs before the test below, not after.
                if (!day.RangeComplete)
                {
                    // Nothing to break out of: the range never printed a bar. Do not fall
                    // through and compare against a high of zero.
                    if (!day.HasRange) continue;

                    day.RangeComplete = true;
                    if (day.Aligned && tod != orEnd) day.Aligned = false;
                }

                if (tod >= cfg.Cutoff) continue;

                day.WindowEndBar = i;

                if (cfg.ConfirmOnClose && i >= last) continue;
                if (cfg.FirstSignalOnly && day.Signals.Count > 0) continue;

                TestBar(day, cfg, bars, i, t);
            }

            return model;
        }

        /// <summary>
        /// Session VWAP, accumulated bar by bar so the value read at a signal is the value that
        /// was on the screen at the time. Typical price weighted by volume -- the standard
        /// reading, and the one the platform's own VWAP draws.
        /// </summary>
        private static void AccumulateVwap(DayOrb day, OrbConfig cfg, IBarWindow bars, int bar,
                                           bool sameDate, TimeSpan tod, ref decimal pv, ref decimal vol)
        {
            bool inWindow;

            if (cfg.Anchor == VwapAnchor.OvernightOpen)
            {
                inWindow = true;
            }
            else
            {
                // Anchored at the session open, so only the day's own daytime bars count. The
                // evening bars that START this trading day are past 8:30 on the clock but
                // belong to the night before, and must not anchor it.
                inWindow = sameDate && tod >= cfg.SessionOpen && tod < cfg.SessionClose;
            }

            if (!inWindow) return;

            var v = bars.Volume(bar);
            var typical = (bars.High(bar) + bars.Low(bar) + bars.Close(bar)) / 3m;

            pv += typical * v;
            vol += v;

            // A feed with no volume gives no VWAP. Publishing the typical price in its place
            // would draw a plausible line that is not a VWAP, so the series stays empty and
            // the third condition fails closed.
            if (vol <= 0m) return;

            if (day.VwapStartBar < 0) day.VwapStartBar = bar;

            // The series has to stay contiguous from its start bar for VwapAt to index it.
            if (bar != day.VwapStartBar + day.Vwap.Count) return;

            day.Vwap.Add(pv / vol);
        }

        /// <summary>Every condition, on one bar, or nothing.</summary>
        private static void TestBar(DayOrb day, OrbConfig cfg, IBarWindow bars, int bar, DateTime local)
        {
            // 1. Close above the opening range high.
            var close = bars.Close(bar);
            if (close <= day.OrHigh) return;

            // 2. Volume at least the multiple of the opening range average. No volume in the
            //    range means no yardstick, and 1.5 x nothing must not read as a pass.
            var average = day.OrAverageVolume;
            if (average <= 0m) return;

            var volume = bars.Volume(bar);
            var ratio = volume / average;
            if (ratio < cfg.VolumeMultiple) return;

            // 3. Above the session VWAP.
            var vwap = day.VwapAt(bar);

            if (cfg.RequireAboveVwap)
            {
                if (vwap <= 0m) return;
                if (close <= vwap) return;
            }

            // 4. Inside the signal window -- already enforced by the caller.
            day.Signals.Add(new OrbSignal
            {
                Bar = bar,
                Local = local,
                Close = close,
                Volume = volume,
                VolumeRatio = ratio,
                Vwap = vwap,
                OrHigh = day.OrHigh
            });
        }

        /// <summary>
        /// First bar of the newest trading days asked for, so a long history is not modelled
        /// for days that will never be drawn.
        /// </summary>
        private static int FirstBarOfRecentDays(IBarWindow bars, OrbConfig cfg, int last, int wanted)
        {
            var counted = 0;
            var current = DateTime.MinValue;

            for (var i = last; i >= 0; i--)
            {
                var stamp = TradingDay(cfg.Time.ToLocal(bars.Time(i)), cfg.OvernightOpen);
                if (stamp == current) continue;

                if (counted == wanted) return i + 1;

                current = stamp;
                counted++;
            }

            return 0;
        }
    }
}
