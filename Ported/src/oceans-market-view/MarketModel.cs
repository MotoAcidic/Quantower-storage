using System;
using System.Collections.Generic;

namespace OceansMarketView
{
    /// <summary>One configurable session window. All times are on your clock (Houston).</summary>
    public sealed class SessionDef
    {
        public string Name;
        public TimeSpan Start;
        public TimeSpan End;

        /// <summary>Opening range length in minutes. 0 disables it for this session.</summary>
        public int OpeningRangeMinutes;
    }

    /// <summary>A single occurrence of a session, with the range it printed.</summary>
    public sealed class SessionBox
    {
        public int DefIndex;
        public string Name;

        /// <summary>Trading day the session belongs to -- the date it ENDS on, so an overnight
        /// session running Sunday evening into Monday morning is Monday's.</summary>
        public DateTime AnchorDate;

        public DateTime StartLocal;
        public DateTime EndLocal;

        public int StartBar = -1;
        public int EndBar = -1;
        public decimal High;
        public decimal Low;
        public bool Complete;

        /// <summary>Opening range: the first minutes of the session.</summary>
        public DateTime OpenRangeEndLocal;

        public bool HasOpenRange;
        public int OpenRangeStartBar = -1;
        public int OpenRangeEndBar = -1;
        public decimal OpenRangeHigh;
        public decimal OpenRangeLow;
        public bool OpenRangeComplete;

        public decimal OpenRangeMid => (OpenRangeHigh + OpenRangeLow) / 2m;
    }

    /// <summary>The opening price of a trading week.</summary>
    public sealed class WeekOpen
    {
        public DateTime WeekStartLocal;
        public decimal Price;
        public int StartBar = -1;
        public int EndBar = -1;
        public bool Current;
    }

    /// <summary>Per-trading-day levels: initial balance and power hour.</summary>
    public sealed class DayLevels
    {
        public DateTime Date;

        public bool HasIb;
        public int IbStartBar = -1;
        public int IbEndBar = -1;
        public decimal IbHigh;
        public decimal IbLow;
        public bool IbComplete;
        public decimal IbRange => IbHigh - IbLow;

        /// <summary>Halfway between the initial balance high and low.</summary>
        public decimal IbMid => (IbHigh + IbLow) / 2m;

        /// <summary>Last bar of the regular session, where IB level extension stops.</summary>
        public int RthEndBar = -1;

        public bool HasPh;
        public int PhStartBar = -1;
        public int PhEndBar = -1;
        public decimal PhHigh;
        public decimal PhLow;
        public bool PhComplete;

        /// <summary>First bar after the power hour that resolved the range. -1 = not yet.</summary>
        public int PhBreakBar = -1;
        public int PhBreakDir;
        public decimal PhBreakPrice;
    }

    /// <summary>One drawn initial-balance level: a multiple of the IB range off IBH or IBL.</summary>
    public struct IbLevel
    {
        public decimal Price;
        public decimal Multiple;
        public bool Above;
        public bool IsZero;
        public string Label;
    }

    public sealed class ModelConfig
    {
        public TimeContext Time;

        public DayOfWeek WeekStartDay = DayOfWeek.Sunday;
        public TimeSpan WeekStartTime = new TimeSpan(17, 0, 0);

        public TimeSpan IbStart = new TimeSpan(8, 30, 0);
        public int IbMinutes = 60;

        public TimeSpan RthStart = new TimeSpan(8, 30, 0);
        public TimeSpan RthEnd = new TimeSpan(15, 0, 0);

        public TimeSpan PhStart = new TimeSpan(14, 0, 0);
        public TimeSpan PhEnd = new TimeSpan(15, 0, 0);

        /// <summary>Require a close beyond the level, rather than a wick touch.</summary>
        public bool BreakoutOnClose = true;

        public SessionDef[] Sessions = Array.Empty<SessionDef>();
    }

    public sealed class MarketModel
    {
        public readonly List<WeekOpen> Weeks = new List<WeekOpen>();
        public readonly List<DayLevels> Days = new List<DayLevels>();
        public readonly List<SessionBox> Sessions = new List<SessionBox>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Local time of the newest bar; drives every completeness test.</summary>
        public DateTime LastBarLocal;

        public static MarketModel Build(IBarWindow bars, ModelConfig cfg)
        {
            var model = new MarketModel();

            if (bars == null || bars.Count == 0 || cfg?.Time == null || !cfg.Time.Valid)
                return model;

            var ibEnd = cfg.IbStart + TimeSpan.FromMinutes(Math.Max(1, cfg.IbMinutes));
            if (ibEnd >= TimeSpan.FromHours(24))
            {
                model.Warnings.Add("Initial balance runs past midnight; shorten the duration.");
                ibEnd = cfg.IbStart;
            }

            var weekMap = new Dictionary<DateTime, WeekOpen>();
            var dayMap = new Dictionary<DateTime, DayLevels>();
            var boxMap = new Dictionary<string, SessionBox>();

            for (var i = 0; i < bars.Count; i++)
            {
                var raw = bars.Time(i);
                if (raw == default) continue;

                var t = cfg.Time.ToLocal(raw);
                var tod = t.TimeOfDay;
                model.LastBarLocal = t;

                AccumulateWeek(weekMap, cfg, t, bars, i);
                AccumulateDay(dayMap, cfg, ibEnd, t, tod, bars, i);
                AccumulateSessions(boxMap, cfg, t, tod, bars, i);
            }

            model.Weeks.AddRange(weekMap.Values);
            model.Weeks.Sort((a, b) => a.WeekStartLocal.CompareTo(b.WeekStartLocal));
            if (model.Weeks.Count > 0) model.Weeks[model.Weeks.Count - 1].Current = true;

            model.Days.AddRange(dayMap.Values);
            model.Days.Sort((a, b) => a.Date.CompareTo(b.Date));

            model.Sessions.AddRange(boxMap.Values);
            model.Sessions.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));

            Finalise(model, cfg, ibEnd, bars);
            return model;
        }

        #region Accumulation

        private static void AccumulateWeek(Dictionary<DateTime, WeekOpen> map, ModelConfig cfg,
                                           DateTime t, IBarWindow bars, int i)
        {
            var anchor = WeekAnchor(t, cfg.WeekStartDay, cfg.WeekStartTime);

            if (!map.TryGetValue(anchor, out var w))
            {
                // The week's opening print. Set once -- re-entering the forming bar must not
                // move it, and neither must a later bar in the same week.
                w = new WeekOpen
                {
                    WeekStartLocal = anchor,
                    Price = bars.Open(i),
                    StartBar = i
                };
                map[anchor] = w;
            }

            w.EndBar = i;
        }

        private static void AccumulateDay(Dictionary<DateTime, DayLevels> map, ModelConfig cfg,
                                          TimeSpan ibEnd, DateTime t, TimeSpan tod,
                                          IBarWindow bars, int i)
        {
            // The initial balance and the power hour both sit inside the regular session, so
            // the calendar date is the trading day for both -- no midnight crossing to handle.
            var inIb = ibEnd > cfg.IbStart && tod >= cfg.IbStart && tod < ibEnd;
            var inPh = InWindow(tod, cfg.PhStart, cfg.PhEnd, out var phShift) && phShift == 0;
            var inRth = tod >= cfg.RthStart && tod < cfg.RthEnd;

            if (!inIb && !inPh && !inRth) return;

            if (!map.TryGetValue(t.Date, out var d))
            {
                d = new DayLevels { Date = t.Date };
                map[t.Date] = d;
            }

            if (inRth) d.RthEndBar = i;

            if (inIb)
            {
                if (!d.HasIb)
                {
                    d.HasIb = true;
                    d.IbStartBar = i;
                    d.IbHigh = bars.High(i);
                    d.IbLow = bars.Low(i);
                }
                else
                {
                    // max/min are idempotent, so re-processing the forming bar is harmless.
                    if (bars.High(i) > d.IbHigh) d.IbHigh = bars.High(i);
                    if (bars.Low(i) < d.IbLow) d.IbLow = bars.Low(i);
                }
                d.IbEndBar = i;
            }

            if (inPh)
            {
                if (!d.HasPh)
                {
                    d.HasPh = true;
                    d.PhStartBar = i;
                    d.PhHigh = bars.High(i);
                    d.PhLow = bars.Low(i);
                }
                else
                {
                    if (bars.High(i) > d.PhHigh) d.PhHigh = bars.High(i);
                    if (bars.Low(i) < d.PhLow) d.PhLow = bars.Low(i);
                }
                d.PhEndBar = i;
            }
        }

        private static void AccumulateSessions(Dictionary<string, SessionBox> map, ModelConfig cfg,
                                               DateTime t, TimeSpan tod, IBarWindow bars, int i)
        {
            for (var s = 0; s < cfg.Sessions.Length; s++)
            {
                var def = cfg.Sessions[s];
                if (def == null || def.Start == def.End) continue;
                if (!InWindow(tod, def.Start, def.End, out var shift)) continue;

                var anchor = t.Date.AddDays(shift);
                var key = s + "|" + anchor.Ticks;

                if (!map.TryGetValue(key, out var box))
                {
                    var crosses = def.Start > def.End;
                    var startLocal = anchor.AddDays(crosses ? -1 : 0) + def.Start;

                    box = new SessionBox
                    {
                        DefIndex = s,
                        Name = def.Name,
                        AnchorDate = anchor,
                        StartLocal = startLocal,
                        EndLocal = anchor + def.End,

                        // Off the session definition, not off the first bar seen, so a late or
                        // missing first bar cannot stretch the opening range.
                        OpenRangeEndLocal = startLocal + TimeSpan.FromMinutes(Math.Max(0, def.OpeningRangeMinutes)),

                        StartBar = i,
                        High = bars.High(i),
                        Low = bars.Low(i)
                    };
                    map[key] = box;
                }
                else
                {
                    if (bars.High(i) > box.High) box.High = bars.High(i);
                    if (bars.Low(i) < box.Low) box.Low = bars.Low(i);
                }

                box.EndBar = i;

                if (def.OpeningRangeMinutes > 0 && t < box.OpenRangeEndLocal)
                {
                    if (!box.HasOpenRange)
                    {
                        box.HasOpenRange = true;
                        box.OpenRangeStartBar = i;
                        box.OpenRangeHigh = bars.High(i);
                        box.OpenRangeLow = bars.Low(i);
                    }
                    else
                    {
                        if (bars.High(i) > box.OpenRangeHigh) box.OpenRangeHigh = bars.High(i);
                        if (bars.Low(i) < box.OpenRangeLow) box.OpenRangeLow = bars.Low(i);
                    }

                    box.OpenRangeEndBar = i;
                }
            }
        }

        #endregion

        #region Completeness and breakout

        private static void Finalise(MarketModel model, ModelConfig cfg, TimeSpan ibEnd, IBarWindow bars)
        {
            var last = model.LastBarLocal;

            foreach (var w in model.Weeks)
                if (w.StartBar >= 0 && w.EndBar < w.StartBar) w.EndBar = w.StartBar;

            foreach (var d in model.Days)
            {
                d.IbComplete = d.HasIb && last >= d.Date + ibEnd;
                d.PhComplete = d.HasPh && last >= d.Date + cfg.PhEnd;
            }

            foreach (var b in model.Sessions)
            {
                b.Complete = last >= b.EndLocal;
                b.OpenRangeComplete = b.HasOpenRange && last >= b.OpenRangeEndLocal;
            }

            ResolveBreakouts(model, cfg, bars);
        }

        /// <summary>
        /// Walks forward from each completed power hour to the first bar that resolves the
        /// range. Only the first break counts -- after that the level has done its job and a
        /// later re-cross is a different trade.
        /// </summary>
        private static void ResolveBreakouts(MarketModel model, ModelConfig cfg, IBarWindow bars)
        {
            for (var k = 0; k < model.Days.Count; k++)
            {
                var d = model.Days[k];
                if (!d.HasPh || !d.PhComplete || d.PhEndBar < 0) continue;

                // Stop at the next day's power hour: past that the level is stale.
                var stop = bars.Count - 1;
                for (var n = k + 1; n < model.Days.Count; n++)
                {
                    if (model.Days[n].HasPh && model.Days[n].PhStartBar >= 0)
                    {
                        stop = model.Days[n].PhStartBar - 1;
                        break;
                    }
                }

                for (var i = d.PhEndBar + 1; i <= stop && i < bars.Count; i++)
                {
                    var up = cfg.BreakoutOnClose ? bars.Close(i) : bars.High(i);
                    var dn = cfg.BreakoutOnClose ? bars.Close(i) : bars.Low(i);

                    if (up > d.PhHigh)
                    {
                        d.PhBreakBar = i;
                        d.PhBreakDir = 1;
                        d.PhBreakPrice = d.PhHigh;
                        break;
                    }

                    if (dn < d.PhLow)
                    {
                        d.PhBreakBar = i;
                        d.PhBreakDir = -1;
                        d.PhBreakPrice = d.PhLow;
                        break;
                    }
                }
            }
        }

        #endregion

        #region Initial balance levels

        /// <summary>
        /// The level ladder for one day. IBH and IBL are both zero; extensions run up from IBH
        /// and down from IBL in multiples of the IB range. Only the multiples passed in are
        /// produced, so switching a level off removes it here rather than at draw time. Lives
        /// in the model so the arithmetic can be checked off-platform.
        /// </summary>
        public static List<IbLevel> IbLevels(DayLevels d, IEnumerable<decimal> multiples)
        {
            var levels = new List<IbLevel>();
            if (d == null || !d.HasIb || d.IbRange <= 0 || multiples == null) return levels;

            foreach (var mult in multiples)
            {
                if (mult < 0) continue;
                var offset = d.IbRange * mult;

                levels.Add(new IbLevel
                {
                    Price = d.IbHigh + offset,
                    Multiple = mult,
                    Above = true,
                    IsZero = mult == 0,
                    Label = Fmt(mult)
                });

                levels.Add(new IbLevel
                {
                    Price = d.IbLow - offset,
                    Multiple = -mult,
                    Above = false,
                    IsZero = mult == 0,
                    Label = mult == 0 ? "0" : "-" + Fmt(mult)
                });
            }

            return levels;
        }

        private static string Fmt(decimal m) =>
            m == decimal.Truncate(m) ? m.ToString("0") : m.ToString("0.##");

        #endregion

        #region Time helpers

        /// <summary>
        /// True when <paramref name="tod"/> falls in [start, end). <paramref name="dayShift"/>
        /// is 1 for the evening leg of a window that crosses midnight, meaning the bar belongs
        /// to the NEXT calendar day's session.
        /// </summary>
        public static bool InWindow(TimeSpan tod, TimeSpan start, TimeSpan end, out int dayShift)
        {
            dayShift = 0;
            if (start == end) return false;

            if (start < end) return tod >= start && tod < end;

            if (tod >= start) { dayShift = 1; return true; }
            return tod < end;
        }

        /// <summary>Start of the trading week containing <paramref name="t"/>, on your clock.</summary>
        public static DateTime WeekAnchor(DateTime t, DayOfWeek startDay, TimeSpan startTime)
        {
            var back = ((int)t.Date.DayOfWeek - (int)startDay + 7) % 7;
            var anchor = t.Date.AddDays(-back) + startTime;
            if (t < anchor) anchor = anchor.AddDays(-7);
            return anchor;
        }

        #endregion
    }
}
