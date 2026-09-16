using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using Color = System.Drawing.Color;
using MColor = System.Windows.Media.Color;

namespace OceansDeveloped
{
    /// <summary>Which side of the chart the histogram lanes sit on.</summary>
    public enum LaneSide
    {
        [Display(Name = "Left")] Left,
        [Display(Name = "Right")] Right
    }

    /// <summary>How far the carried levels run.</summary>
    public enum ExtendMode
    {
        [Display(Name = "From the period's own end")] FromPeriodEnd,
        [Display(Name = "Right across the chart")] WholeChart
    }

    /// <summary>
    /// Ocean Developed Profile -- the volume profile of the periods that are FINISHED, carried
    /// onto the chart you are trading.
    ///
    /// Previous day, previous week, previous month and two rolling day windows, each with its
    /// point of control, value area and high-volume shelves. Nothing in progress is ever drawn:
    /// a level that is still moving is not a level, and half of today's profile says nothing
    /// about where today's auction settled.
    ///
    /// Two things it refuses to do, both because the failure mode is a clean-looking chart
    /// rather than an error. It will not profile a period the loaded history does not fully
    /// cover -- a "previous month" built from the eleven days you happen to have loaded puts
    /// its point of control somewhere the month never agreed on. And it will not cut a period
    /// until <see cref="TimeContext"/> has settled whether ATAS is stamping bars UTC or local,
    /// because a whole-day shift draws a perfectly plausible profile of the wrong session.
    /// </summary>
    [DisplayName("Oceans Developed Profile")]
    [Category("Ocean")]
    public class OceansDevelopedIndicator : Indicator
    {
        private const int MaxProfileTicks = 60000;
        private const int MaxDaysHeld = 400;

        /// <summary>The order the lanes are laid out in, shortest period first.</summary>
        private static readonly PeriodKind[] Kinds =
        {
            PeriodKind.PrevDay, PeriodKind.PrevWeek, PeriodKind.PrevMonth,
            PeriodKind.RollingA, PeriodKind.RollingB
        };

        /// <summary>One drawn instance of one period kind.</summary>
        private sealed class Instance
        {
            public PeriodKind Kind;
            public DateTime Key;
            public string Tag;
            public string Caption;
            public Profile Profile;
            public HvnZone[] Zones;
            public int LastBar = -1;      // the final bar of the period, where its levels start
            public int UntilBar = -1;     // the next instance's first bar, where they stop
            public bool Stale;            // an older retained instance, drawn dimmer
        }

        // The fold. Day profiles are immutable once the day is behind us, so they are built at
        // most once and every composite period is a sum of them rather than another bar scan.
        private readonly Dictionary<int, DateTime> _tradeDate = new Dictionary<int, DateTime>();
        private readonly Dictionary<DateTime, List<int>> _dayBars = new Dictionary<DateTime, List<int>>();
        private readonly List<DateTime> _days = new List<DateTime>();
        private readonly Dictionary<DateTime, Profile> _dayProfile = new Dictionary<DateTime, Profile>();
        private readonly Dictionary<string, Instance> _instances = new Dictionary<string, Instance>();

        private int _foldedTo = -1;
        private DateTime _earliestBar = DateTime.MaxValue;
        private decimal _settingsStamp = decimal.MinValue;

        private int _timeTriedAtBar = -1;
        private TimeContext _time;
        private string _timeError;

        private readonly RenderFont _font = new RenderFont("Arial", 8f);
        private readonly RenderFont _statusFont = new RenderFont("Arial", 9f);

        private MColor _dayColor = MColor.FromRgb(255, 205, 70);
        private MColor _weekColor = MColor.FromRgb(90, 180, 255);
        private MColor _monthColor = MColor.FromRgb(190, 130, 255);
        private MColor _rollingAColor = MColor.FromRgb(60, 210, 130);
        private MColor _rollingBColor = MColor.FromRgb(255, 140, 70);
        private MColor _neutralColor = MColor.FromRgb(120, 125, 140);

        public OceansDevelopedIndicator() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Historical);
            DrawAbovePrice = false;

            var series = DataSeries[0] as ValueDataSeries;
            if (series != null)
            {
                series.VisualType = VisualMode.Hide;
                series.IsHidden = true;
                series.ShowZeroValue = false;
                series.ScaleIt = false;
                series.IgnoredByAlerts = true;
            }
        }

        #region Settings -- which periods

        [Display(Name = "Previous day", GroupName = "01 Periods", Order = 100,
                 Description = "The last completed futures day, 5 PM to 5 PM Central.")]
        public bool ShowPrevDay { get; set; } = true;

        [Display(Name = "Previous week", GroupName = "01 Periods", Order = 110,
                 Description = "The last completed week, Sunday's 5 PM reopen to Friday's close.")]
        public bool ShowPrevWeek { get; set; } = true;

        [Display(Name = "Previous month", GroupName = "01 Periods", Order = 120,
                 Description = "The last completed calendar month. Needs a month of bars " +
                               "loaded on the chart, or it says so rather than drawing part of one.")]
        public bool ShowPrevMonth { get; set; } = false;

        [Display(Name = "Rolling window A", GroupName = "01 Periods", Order = 130,
                 Description = "A window of the last N completed days, rolling forward at " +
                               "each 5 PM close.")]
        public bool ShowRollingA { get; set; } = true;

        [Display(Name = "Rolling window A: days", GroupName = "01 Periods", Order = 140)]
        [Range(2, 60)]
        public int RollingADays { get; set; } = 5;

        [Display(Name = "Rolling window B", GroupName = "01 Periods", Order = 150)]
        public bool ShowRollingB { get; set; } = false;

        [Display(Name = "Rolling window B: days", GroupName = "01 Periods", Order = 160)]
        [Range(2, 90)]
        public int RollingBDays { get; set; } = 20;

        [Display(Name = "Keep this many past instances", GroupName = "01 Periods", Order = 170,
                 Description = "1 draws only the most recent completed period of each kind. " +
                               "Higher keeps older ones, dimmed, with their levels stopping " +
                               "where the next period begins.")]
        [Range(1, 12)]
        public int Retain { get; set; } = 1;

        #endregion

        #region Settings -- clock

        [Display(Name = "Time zone", GroupName = "02 Clock", Order = 200,
                 Description = "Houston is 'Central Standard Time'. Everything is cut and " +
                               "labelled in this one zone.")]
        public string ZoneId { get; set; } = "Central Standard Time";

        [Display(Name = "Bar clock", GroupName = "02 Clock", Order = 210,
                 Description = "Whether ATAS stamps bars UTC or already local. Auto works it " +
                               "out from the data.")]
        public BarClock Clock { get; set; } = BarClock.Auto;

        [Display(Name = "Draw periods history does not cover", GroupName = "02 Clock", Order = 220,
                 Description = "Off by default, and worth leaving off. A month profile built " +
                               "from a fortnight of loaded bars looks exactly like a real one.")]
        public bool AllowPartial { get; set; } = false;

        #endregion

        #region Settings -- histogram

        [Display(Name = "Show the histogram", GroupName = "03 Histogram", Order = 300,
                 Description = "One lane per period at the edge of the chart. Turn it off to " +
                               "keep only the carried levels.")]
        public bool ShowHistogram { get; set; } = true;

        [Display(Name = "Which side", GroupName = "03 Histogram", Order = 310)]
        public LaneSide Side { get; set; } = LaneSide.Right;

        [Display(Name = "Lane width (px)", GroupName = "03 Histogram", Order = 320)]
        [Range(16, 400)]
        public int LaneWidth { get; set; } = 54;

        [Display(Name = "Gap between lanes (px)", GroupName = "03 Histogram", Order = 330)]
        [Range(0, 40)]
        public int LaneGap { get; set; } = 6;

        [Display(Name = "Minimum row height (px)", GroupName = "03 Histogram", Order = 340,
                 Description = "Ticks are folded into rows only to draw them. The point of " +
                               "control, value area and shelves are all found at full " +
                               "resolution first.")]
        [Range(1, 40)]
        public int MinRowHeight { get; set; } = 2;

        [Display(Name = "Row gap (px)", GroupName = "03 Histogram", Order = 350)]
        [Range(0, 6)]
        public int RowGap { get; set; } = 0;

        [Display(Name = "Shade the value area", GroupName = "03 Histogram", Order = 360)]
        public bool ShadeValueArea { get; set; } = true;

        [Display(Name = "Lane header", GroupName = "03 Histogram", Order = 370)]
        public bool ShowHeader { get; set; } = true;

        #endregion

        #region Settings -- carried levels

        [Display(Name = "Point of control", GroupName = "04 Levels", Order = 400)]
        public bool ShowPoc { get; set; } = true;

        [Display(Name = "Value area high and low", GroupName = "04 Levels", Order = 410)]
        public bool ShowValueArea { get; set; } = true;

        [Display(Name = "Value area (%)", GroupName = "04 Levels", Order = 420,
                 Description = "Named ValueAreaSize rather than ValueAreaPercent: the base " +
                               "class already has that name and hiding it is silent.")]
        [Range(30, 100)]
        public decimal ValueAreaSize { get; set; } = 70m;

        [Display(Name = "How far the levels run", GroupName = "04 Levels", Order = 430)]
        public ExtendMode Extend { get; set; } = ExtendMode.FromPeriodEnd;

        [Display(Name = "Label the levels", GroupName = "04 Levels", Order = 440)]
        public bool ShowTags { get; set; } = true;

        [Display(Name = "Line width (px)", GroupName = "04 Levels", Order = 450)]
        [Range(1, 4)]
        public int LineWidth { get; set; } = 1;

        #endregion

        #region Settings -- high volume nodes

        [Display(Name = "High volume shelves", GroupName = "05 HVN", Order = 500,
                 Description = "Bands of prices the period kept trading at, not single ticks.")]
        public bool ShowHvn { get; set; } = true;

        [Display(Name = "Size (% of the busiest level)", GroupName = "05 HVN", Order = 510,
                 Description = "A level counts toward a shelf at or above this share of the " +
                               "busiest one. Lower finds more, and eventually finds the whole " +
                               "value area.")]
        [Range(20, 99)]
        public decimal HvnPercent { get; set; } = 70m;

        [Display(Name = "Thinnest shelf (ticks)", GroupName = "05 HVN", Order = 520,
                 Description = "Anything thinner is one print, not a shelf.")]
        [Range(1, 200)]
        public int HvnMinTicks { get; set; } = 4;

        [Display(Name = "Bridge dips up to (ticks)", GroupName = "05 HVN", Order = 530,
                 Description = "A thin tick inside a shelf is noise. Without this, one quiet " +
                               "tick reports two shelves where the market built one.")]
        [Range(0, 100)]
        public int HvnGapTicks { get; set; } = 3;

        [Display(Name = "How many to mark", GroupName = "05 HVN", Order = 540,
                 Description = "Marking every shelf is marking none.")]
        [Range(1, 20)]
        public int HvnCount { get; set; } = 3;

        [Display(Name = "Shelf opacity (%)", GroupName = "05 HVN", Order = 550)]
        [Range(2, 60)]
        public int HvnOpacity { get; set; } = 12;

        #endregion

        #region Settings -- colours

        [Display(Name = "Previous day", GroupName = "06 Colours", Order = 600)]
        public MColor DayColor { get { return _dayColor; } set { _dayColor = value; } }

        [Display(Name = "Previous week", GroupName = "06 Colours", Order = 610)]
        public MColor WeekColor { get { return _weekColor; } set { _weekColor = value; } }

        [Display(Name = "Previous month", GroupName = "06 Colours", Order = 620)]
        public MColor MonthColor { get { return _monthColor; } set { _monthColor = value; } }

        [Display(Name = "Rolling window A", GroupName = "06 Colours", Order = 630)]
        public MColor RollingAColor { get { return _rollingAColor; } set { _rollingAColor = value; } }

        [Display(Name = "Rolling window B", GroupName = "06 Colours", Order = 640)]
        public MColor RollingBColor { get { return _rollingBColor; } set { _rollingBColor = value; } }

        [Display(Name = "Histogram body", GroupName = "06 Colours", Order = 650)]
        public MColor NeutralColor { get { return _neutralColor; } set { _neutralColor = value; } }

        #endregion

        protected override void OnCalculate(int bar, decimal value)
        {
            // Profiles are built from the fold below, on demand, and cached per trade date.
            // Nothing to do per bar.
        }

        protected override void OnRecalculate()
        {
            _tradeDate.Clear();
            _dayBars.Clear();
            _days.Clear();
            _dayProfile.Clear();
            _instances.Clear();

            _foldedTo = -1;
            _earliestBar = DateTime.MaxValue;
            _time = null;
            _timeError = null;
            _timeTriedAtBar = -1;
        }

        #region Clock

        private bool ResolveTime()
        {
            if (_time != null && _time.Valid) return true;

            // Failing once on a chart that was still loading must not condemn the indicator for
            // the rest of the session, so it is retried as history arrives -- but not every
            // frame, because the halt scan walks every bar.
            if (_time != null && CurrentBar - _timeTriedAtBar < 200) return false;

            _timeTriedAtBar = CurrentBar;

            // 16, not 15. The MNQ maintenance halt is 4-5 PM Central; 3 PM is the cash close and
            // trading carries straight on through it, so a resolver told to look for an empty
            // hour at 15 finds none and never resolves at all.
            _time = TimeContext.Create(ZoneId, Clock, new BarWindow(this), DateTime.UtcNow, 16);
            _timeError = _time.Valid ? null : _time.Error;

            return _time.Valid;
        }

        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansDevelopedIndicator _owner;

            public BarWindow(OceansDevelopedIndicator owner) { _owner = owner; }

            public int Count { get { return _owner.CurrentBar; } }
            public DateTime Time(int bar) { return _owner.GetCandle(bar).Time; }
            public decimal Open(int bar) { return _owner.GetCandle(bar).Open; }
            public decimal High(int bar) { return _owner.GetCandle(bar).High; }
            public decimal Low(int bar) { return _owner.GetCandle(bar).Low; }
            public decimal Close(int bar) { return _owner.GetCandle(bar).Close; }
        }

        #endregion

        #region The fold

        /// <summary>
        /// Groups every LOADED bar by futures trade date, extending the fold from wherever it
        /// last reached. Visible bars are not enough here: the levels of a period that scrolled
        /// off the left of the screen are exactly the ones being carried forward.
        /// </summary>
        private void Fold()
        {
            var last = CurrentBar - 1;
            if (last < 0) return;

            // The bar in progress is refolded each pass; its trade date can only change at the
            // 5 PM roll, and re-reading one candle is cheaper than being wrong about it.
            var from = _foldedTo < 0 ? 0 : _foldedTo;

            for (var bar = from; bar <= last; bar++)
            {
                var candle = GetCandle(bar);
                var local = _time.ToLocal(candle.Time);

                if (local < _earliestBar) _earliestBar = local;

                var date = DevelopedClock.TradeDate(local);

                DateTime already;
                if (_tradeDate.TryGetValue(bar, out already) && already == date) continue;

                _tradeDate[bar] = date;

                List<int> bars;
                if (!_dayBars.TryGetValue(date, out bars))
                {
                    bars = new List<int>();
                    _dayBars[date] = bars;
                    _days.Add(date);
                    _days.Sort();
                }

                if (bars.Count == 0 || bars[bars.Count - 1] != bar) bars.Add(bar);
            }

            _foldedTo = last;

            // Scrolling back through years of history would otherwise grow these without limit.
            while (_days.Count > MaxDaysHeld)
            {
                var oldest = _days[0];
                _days.RemoveAt(0);
                _dayBars.Remove(oldest);
                _dayProfile.Remove(oldest);
            }
        }

        /// <summary>
        /// The profile of one completed trade date. Cached forever: a day that is behind us
        /// cannot change, so this runs once per day per chart load and every week, month and
        /// rolling window is a sum of these rather than another walk over the bars.
        /// </summary>
        private Profile DayProfile(DateTime date)
        {
            Profile profile;
            if (_dayProfile.TryGetValue(date, out profile)) return profile;

            List<int> bars;
            if (!_dayBars.TryGetValue(date, out bars) || bars.Count == 0) return null;

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m) return null;

            var builder = new ProfileBuilder();
            builder.Start = DevelopedClock.OpenOf(date);
            builder.End = DevelopedClock.CloseOf(date);
            builder.Days = 1;

            for (var i = 0; i < bars.Count; i++)
            {
                var candle = GetCandle(bars[i]);

                foreach (var level in candle.GetAllPriceLevels())
                {
                    if (level == null) continue;
                    builder.Add(level.Price, level.Volume, level.Bid, level.Ask);
                }
            }

            profile = builder.Build(tick, MaxProfileTicks);

            // A day whose bars fall outside the loaded history at its open was only partly
            // recorded, and every period that sums it inherits that.
            if (profile != null) profile.Complete = _earliestBar <= DevelopedClock.OpenOf(date);

            _dayProfile[date] = profile;
            return profile;
        }

        /// <summary>
        /// The completed trade dates, oldest first. An unfinished session is the one thing this
        /// indicator exists not to draw, so a date qualifies only once it is actually behind us.
        ///
        /// Two ways to be behind us, and both are needed. A later trade date in the fold settles
        /// it outright, and covers replay and loaded history where the wall clock means nothing.
        /// The clock covers the case that rule alone gets wrong: on a Saturday the newest date
        /// IS Friday, and Friday is plainly finished -- reading it as in progress all weekend
        /// would quietly move "previous day" back to Thursday during the one stretch of the week
        /// there is time to review it.
        /// </summary>
        private List<DateTime> CompletedDays()
        {
            var days = new List<DateTime>();
            if (_days.Count == 0) return days;

            // Converted here rather than added to TimeContext, which is a straight copy from
            // oceans-profile and stays that way so the two can be diffed.
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _time.Zone);

            for (var i = 0; i < _days.Count; i++)
            {
                var settled = i < _days.Count - 1
                           || now >= DevelopedClock.CloseOf(_days[i]);

                // _days is sorted, so the first unfinished date ends the run.
                if (!settled) break;

                days.Add(_days[i]);
            }

            return days;
        }

        #endregion

        #region Composing the periods

        /// <summary>
        /// Everything drawn this frame: for each enabled kind, its most recent completed
        /// instance and however many older ones are retained, newest first within the kind.
        /// </summary>
        private List<Instance> Compose(List<DateTime> completed, out List<string> notes)
        {
            notes = new List<string>();
            var all = new List<Instance>();

            if (completed.Count == 0)
            {
                notes.Add("no completed session yet");
                return all;
            }

            for (var k = 0; k < Kinds.Length; k++)
            {
                var kind = Kinds[k];
                if (!Enabled(kind)) continue;

                string note;
                var made = ComposeKind(kind, completed, out note);

                if (!string.IsNullOrEmpty(note)) notes.Add(note);
                all.AddRange(made);
            }

            return all;
        }

        private List<Instance> ComposeKind(PeriodKind kind, List<DateTime> completed, out string note)
        {
            note = null;
            var made = new List<Instance>();

            var groups = DevelopedClock.IsCalendar(kind)
                       ? CalendarGroups(kind, completed)
                       : RollingGroups(kind, completed);

            if (groups.Count == 0)
            {
                note = DevelopedClock.Tag(kind, Days(kind)) + ": not enough history loaded";
                return made;
            }

            var skipped = 0;

            for (var i = 0; i < groups.Count; i++)
            {
                var instance = BuildInstance(kind, groups[i].Key, groups[i].Value);

                if (instance == null) { skipped++; continue; }

                instance.Stale = i > 0;
                made.Add(instance);
            }

            if (skipped > 0 && made.Count == 0)
            {
                note = DevelopedClock.Tag(kind, Days(kind)) +
                       ": history does not cover the whole period";
            }

            return made;
        }

        /// <summary>
        /// The retained instances of a calendar-cut kind, newest first. The group the current
        /// session belongs to is skipped: it is still being written.
        /// </summary>
        private List<KeyValuePair<DateTime, List<DateTime>>> CalendarGroups(
            PeriodKind kind, List<DateTime> completed)
        {
            var result = new List<KeyValuePair<DateTime, List<DateTime>>>();
            var current = DevelopedClock.KeyOf(_days[_days.Count - 1], kind);

            var index = new Dictionary<DateTime, List<DateTime>>();
            var order = new List<DateTime>();

            for (var i = 0; i < completed.Count; i++)
            {
                var key = DevelopedClock.KeyOf(completed[i], kind);
                if (key == current) continue;          // the period in progress

                List<DateTime> days;
                if (!index.TryGetValue(key, out days))
                {
                    days = new List<DateTime>();
                    index[key] = days;
                    order.Add(key);
                }

                days.Add(completed[i]);
            }

            order.Sort();
            order.Reverse();

            for (var i = 0; i < order.Count && i < Retain; i++)
                result.Add(new KeyValuePair<DateTime, List<DateTime>>(order[i], index[order[i]]));

            return result;
        }

        /// <summary>
        /// The retained rolling windows, newest first. Instance 0 is the last N completed days;
        /// instance 1 is the same window as it stood one close earlier, and so on -- which is
        /// what "rolls at the daily close" means when you can still see yesterday on the chart.
        /// </summary>
        private List<KeyValuePair<DateTime, List<DateTime>>> RollingGroups(
            PeriodKind kind, List<DateTime> completed)
        {
            var result = new List<KeyValuePair<DateTime, List<DateTime>>>();
            var span = Days(kind);
            if (span < 1) span = 1;

            for (var back = 0; back < Retain; back++)
            {
                var end = completed.Count - 1 - back;
                var start = end - span + 1;

                // A short window is not a smaller window, it is a different period. Refuse it.
                if (start < 0) break;

                var days = new List<DateTime>();
                for (var i = start; i <= end; i++) days.Add(completed[i]);

                result.Add(new KeyValuePair<DateTime, List<DateTime>>(completed[end], days));
            }

            return result;
        }

        private Instance BuildInstance(PeriodKind kind, DateTime key, List<DateTime> days)
        {
            if (days == null || days.Count == 0) return null;

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m) return null;

            var signature = Signature();
            var id = kind + "|" + key.Ticks + "|" + days.Count + "|" + signature;

            Instance cached;
            if (_instances.TryGetValue(id, out cached))
            {
                Locate(cached, days);
                return cached;
            }

            var parts = new List<Profile>(days.Count);
            for (var i = 0; i < days.Count; i++)
            {
                var day = DayProfile(days[i]);
                if (day != null) parts.Add(day);
            }

            var profile = DevelopedMath.Merge(parts, tick, MaxProfileTicks);
            if (profile == null) return null;

            // Coverage, checked against the real period boundary rather than against the days
            // that happen to be loaded -- a month with its first fortnight missing still has
            // days in it, and still produces a confident, wrong point of control.
            var start = DevelopedClock.IsCalendar(kind)
                      ? DevelopedClock.StartInstant(key, kind)
                      : DevelopedClock.OpenOf(days[0]);

            if (_earliestBar > start) profile.Complete = false;
            if (!profile.Complete && !AllowPartial) return null;

            DevelopedMath.ComputeValueArea(profile, ValueAreaSize);

            var instance = new Instance();
            instance.Kind = kind;
            instance.Key = key;
            instance.Profile = profile;
            instance.Tag = DevelopedClock.Tag(kind, Days(kind));
            instance.Caption = DevelopedClock.Describe(key, kind, Days(kind));

            if (!profile.Complete) instance.Caption += "  PARTIAL";

            instance.Zones = ShowHvn
                ? DevelopedMath.FindHvnZones(profile, HvnPercent, HvnMinTicks, HvnGapTicks, HvnCount)
                : new HvnZone[0];

            Locate(instance, days);

            if (_instances.Count > 200) _instances.Clear();
            _instances[id] = instance;

            return instance;
        }

        /// <summary>
        /// Where on the chart the instance's levels start, and where they stop. An older
        /// retained instance stops at the next one's first bar, so two weeks of levels read as
        /// two blocks rather than one cross-hatch.
        /// </summary>
        private void Locate(Instance instance, List<DateTime> days)
        {
            instance.LastBar = -1;
            instance.UntilBar = -1;

            List<int> bars;
            if (_dayBars.TryGetValue(days[days.Count - 1], out bars) && bars.Count > 0)
                instance.LastBar = bars[bars.Count - 1];

            var after = _days.IndexOf(days[days.Count - 1]) + 1;
            if (after > 0 && after < _days.Count &&
                _dayBars.TryGetValue(_days[after], out bars) && bars.Count > 0)
            {
                instance.UntilBar = bars[0];
            }
        }

        /// <summary>
        /// A stamp of every setting the composed profiles depend on. Anything that changes what
        /// a profile IS invalidates the cache; anything that only changes how it is drawn does
        /// not, so dragging an opacity slider does not rebuild a month.
        /// </summary>
        private decimal Signature()
        {
            var stamp = ValueAreaSize * 1000m
                      + HvnPercent
                      + HvnMinTicks * 7m
                      + HvnGapTicks * 13m
                      + HvnCount * 31m
                      + RollingADays * 101m
                      + RollingBDays * 211m
                      + (ShowHvn ? 1m : 0m)
                      + (AllowPartial ? 2m : 0m);

            if (stamp != _settingsStamp)
            {
                _instances.Clear();
                _settingsStamp = stamp;
            }

            return stamp;
        }

        private bool Enabled(PeriodKind kind)
        {
            switch (kind)
            {
                case PeriodKind.PrevDay: return ShowPrevDay;
                case PeriodKind.PrevWeek: return ShowPrevWeek;
                case PeriodKind.PrevMonth: return ShowPrevMonth;
                case PeriodKind.RollingA: return ShowRollingA;
                default: return ShowRollingB;
            }
        }

        private int Days(PeriodKind kind)
        {
            if (kind == PeriodKind.RollingA) return RollingADays;
            if (kind == PeriodKind.RollingB) return RollingBDays;

            return 0;
        }

        private MColor Tint(PeriodKind kind)
        {
            switch (kind)
            {
                case PeriodKind.PrevDay: return _dayColor;
                case PeriodKind.PrevWeek: return _weekColor;
                case PeriodKind.PrevMonth: return _monthColor;
                case PeriodKind.RollingA: return _rollingAColor;
                default: return _rollingBColor;
            }
        }

        #endregion

        #region Render

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            var chart = ChartInfo;
            var container = chart == null ? null : chart.PriceChartContainer;
            if (container == null) return;

            var region = container.Region;
            if (region.Width <= 0 || region.Height <= 0) return;

            if (CurrentBar < 2) { Status(context, region, "Ocean Developed: no bars loaded yet."); return; }

            var tick = InstrumentInfo == null ? 0m : InstrumentInfo.TickSize;
            if (tick <= 0m) { Status(context, region, "Ocean Developed: waiting for the instrument."); return; }

            // Every boundary here is a clock time and a wrong offset shifts whole sessions, so
            // this refuses to guess rather than drawing a plausible profile of the wrong day.
            if (!ResolveTime()) { Status(context, region, _timeError); return; }

            List<Instance> instances;
            List<string> notes;

            try
            {
                Fold();
                instances = Compose(CompletedDays(), out notes);
            }
            catch (Exception ex)
            {
                Status(context, region, "Ocean Developed: could not build the profiles - " + ex.Message);
                return;
            }

            context.SetTextRenderingHint(RenderTextRenderingHint.AntiAlias);
            context.SetClip(region);

            try
            {
                var lanes = LaneBounds(region, instances);

                // Each layer is caught on its own. A layer that throws otherwise takes down
                // every layer after it in silence -- including the status line that would have
                // said so -- and there is no debugger on the render thread.
                notes.AddRange(Layer("shelves", delegate { DrawShelves(context, container, region, instances, lanes); }));
                notes.AddRange(Layer("levels", delegate { DrawLevels(context, container, region, instances, lanes); }));

                if (ShowHistogram)
                    notes.AddRange(Layer("histogram", delegate { DrawLanes(context, container, region, instances, lanes); }));

                if (notes.Count > 0) Status(context, region, "Ocean Developed: " + string.Join("   ", notes));
            }
            finally
            {
                context.ResetClip();
            }
        }

        private static List<string> Layer(string name, Action draw)
        {
            var problems = new List<string>();

            try { draw(); }
            catch (Exception ex) { problems.Add(name + " failed: " + ex.Message); }

            return problems;
        }

        /// <summary>
        /// The x span of each lane, keyed by instance. Only the newest instance of a kind gets
        /// one: an older retained period is worth a set of levels, not a second histogram.
        /// </summary>
        private Dictionary<Instance, Rectangle> LaneBounds(Rectangle region, List<Instance> instances)
        {
            var bounds = new Dictionary<Instance, Rectangle>();
            if (!ShowHistogram) return bounds;

            var fresh = new List<Instance>();
            for (var i = 0; i < instances.Count; i++)
            {
                if (!instances[i].Stale) fresh.Add(instances[i]);
            }

            if (fresh.Count == 0) return bounds;

            var step = LaneWidth + LaneGap;
            var block = step * fresh.Count - LaneGap;

            // Never eat more than half the chart, however many lanes are switched on.
            var room = region.Width / 2;
            var width = LaneWidth;

            if (block > room && fresh.Count > 0)
            {
                width = (room - LaneGap * (fresh.Count - 1)) / fresh.Count;
                if (width < 8) width = 8;
                step = width + LaneGap;
            }

            for (var i = 0; i < fresh.Count; i++)
            {
                int left;

                // Laid out from the chart edge inward, so the shortest period -- the one being
                // read most often -- sits nearest the price.
                if (Side == LaneSide.Right) left = region.Right - width - step * i;
                else left = region.Left + step * i;

                bounds[fresh[i]] = new Rectangle(left, region.Top, width, region.Height);
            }

            return bounds;
        }

        /// <summary>The x the carried levels stop at, so they never run under a histogram.</summary>
        private int LevelLimit(Rectangle region, Dictionary<Instance, Rectangle> lanes)
        {
            if (lanes.Count == 0) return Side == LaneSide.Right ? region.Right : region.Left;

            var edge = Side == LaneSide.Right ? region.Right : region.Left;

            foreach (var lane in lanes.Values)
            {
                if (Side == LaneSide.Right) { if (lane.Left < edge) edge = lane.Left; }
                else if (lane.Right > edge) edge = lane.Right;
            }

            return edge;
        }

        private void Span(IChartContainer container, Rectangle region, Instance instance,
                          Dictionary<Instance, Rectangle> lanes, out int from, out int to)
        {
            var limit = LevelLimit(region, lanes);

            if (Extend == ExtendMode.WholeChart || instance.LastBar < 0)
            {
                from = region.Left;
                to = limit;
            }
            else
            {
                from = container.GetXByBar(instance.LastBar, false);
                to = limit;
            }

            // A retained older period hands over to the one that replaced it rather than
            // running on underneath it.
            if (instance.Stale && instance.UntilBar >= 0)
            {
                var handover = container.GetXByBar(instance.UntilBar, true);
                if (handover < to) to = handover;
            }

            if (from < region.Left) from = region.Left;
            if (to > region.Right) to = region.Right;
        }

        private void DrawShelves(RenderContext context, IChartContainer container, Rectangle region,
                                 List<Instance> instances, Dictionary<Instance, Rectangle> lanes)
        {
            if (!ShowHvn) return;

            for (var i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (instance.Zones == null || instance.Zones.Length == 0) continue;

                int from, to;
                Span(container, region, instance, lanes, out from, out to);
                if (to <= from) continue;

                var tint = Tint(instance.Kind);
                var alpha = (byte)(HvnOpacity * 255 / 100);
                if (instance.Stale) alpha = (byte)(alpha / 2);

                for (var z = 0; z < instance.Zones.Length; z++)
                {
                    var zone = instance.Zones[z];

                    var top = container.GetYByPrice(zone.HighPrice, false);
                    var bottom = container.GetYByPrice(zone.LowPrice, false);
                    if (bottom < top) { var swap = top; top = bottom; bottom = swap; }

                    var height = bottom - top;
                    if (height < 1) height = 1;
                    if (bottom < region.Top || top > region.Bottom) continue;

                    context.FillRectangle(Color.FromArgb(alpha, tint.R, tint.G, tint.B),
                                          new Rectangle(from, top, to - from, height));
                }
            }
        }

        private void DrawLevels(RenderContext context, IChartContainer container, Rectangle region,
                                List<Instance> instances, Dictionary<Instance, Rectangle> lanes)
        {
            for (var i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                var profile = instance.Profile;

                int from, to;
                Span(container, region, instance, lanes, out from, out to);
                if (to <= from) continue;

                var tint = Tint(instance.Kind);
                var strong = instance.Stale ? (byte)110 : (byte)225;
                var faint = instance.Stale ? (byte)70 : (byte)150;

                if (ShowValueArea && profile.ValIndex >= 0 && profile.VahIndex >= 0)
                {
                    Ray(context, container, region, profile.ValueAreaHigh, from, to,
                        Color.FromArgb(faint, tint.R, tint.G, tint.B), true, instance.Tag + " VAH");

                    Ray(context, container, region, profile.ValueAreaLow, from, to,
                        Color.FromArgb(faint, tint.R, tint.G, tint.B), true, instance.Tag + " VAL");
                }

                if (ShowPoc && profile.PocIndex >= 0)
                {
                    Ray(context, container, region, profile.Poc, from, to,
                        Color.FromArgb(strong, tint.R, tint.G, tint.B), false, instance.Tag + " POC");
                }
            }
        }

        private void Ray(RenderContext context, IChartContainer container, Rectangle region,
                         decimal price, int from, int to, Color color, bool dashed, string tag)
        {
            var y = container.GetYByPrice(price, false);
            if (y < region.Top || y > region.Bottom) return;

            var pen = dashed
                ? new RenderPen(color, LineWidth, System.Drawing.Drawing2D.DashStyle.Dash)
                : new RenderPen(color, LineWidth);

            context.DrawLine(pen, from, y, to, y);

            if (!ShowTags) return;

            var text = tag + " " + ChartInfo.GetPriceString(price);
            var size = context.MeasureString(text, _font);

            // Against the end of the ray, where price is now, rather than back in the history.
            var x = to - size.Width - 4;
            if (x < from) x = from + 2;

            context.DrawString(text, _font, color, x, y - size.Height - 1);
        }

        private void DrawLanes(RenderContext context, IChartContainer container, Rectangle region,
                               List<Instance> instances, Dictionary<Instance, Rectangle> lanes)
        {
            var ticksPerRow = DevelopedMath.TicksPerRow(container.PriceRowHeight, MinRowHeight);
            var rowPixels = (int)Math.Round(container.PriceRowHeight * ticksPerRow);
            if (rowPixels < 1) rowPixels = 1;

            for (var i = 0; i < instances.Count; i++)
            {
                Rectangle lane;
                if (!lanes.TryGetValue(instances[i], out lane)) continue;

                DrawLane(context, container, region, instances[i], lane, ticksPerRow, rowPixels);
            }
        }

        private void DrawLane(RenderContext context, IChartContainer container, Rectangle region,
                              Instance instance, Rectangle lane, int ticksPerRow, int rowPixels)
        {
            var profile = instance.Profile;
            var view = DevelopedMath.BuildView(profile, ticksPerRow, instance.Zones);
            var max = DevelopedMath.MaxRowVolume(view);
            if (max <= 0m) return;

            var tint = Tint(instance.Kind);
            var body = _neutralColor;

            if (ShadeValueArea && profile.ValIndex >= 0 && profile.VahIndex >= 0)
            {
                var top = container.GetYByPrice(profile.ValueAreaHigh, false) - rowPixels / 2;
                var bottom = container.GetYByPrice(profile.ValueAreaLow, false) + rowPixels / 2;

                if (bottom > top && top < region.Bottom && bottom > region.Top)
                {
                    context.FillRectangle(Color.FromArgb(26, tint.R, tint.G, tint.B),
                                          new Rectangle(lane.Left, top, lane.Width, bottom - top));
                }
            }

            var growRight = Side == LaneSide.Left;

            for (var r = 0; r < view.Length; r++)
            {
                var row = view[r];
                if (row.Volume <= 0m) continue;

                var height = rowPixels - RowGap;
                if (height < 1) height = 1;

                var y = container.GetYByPrice(row.MidPrice, false) - height / 2;
                if (y + height < region.Top || y > region.Bottom) continue;

                var length = (int)Math.Round(lane.Width * DevelopedMath.Normalise(row.Volume, max));
                if (length < 1) length = 1;

                // Bars grow away from the chart edge the lane is pinned to, so the profile
                // reads outward from the price axis rather than into it.
                var left = growRight ? lane.Left : lane.Right - length;

                // The shelves are the point of the thing: inside one the bar takes the period's
                // own colour, outside it stays plain, so the shape and the shelves are one read.
                var fill = row.InHvn
                         ? Color.FromArgb(235, tint.R, tint.G, tint.B)
                         : Color.FromArgb(row.InValueArea ? (byte)190 : (byte)120, body.R, body.G, body.B);

                context.FillRectangle(fill, new Rectangle(left, y, length, height));

                if (!row.HasPoc) continue;

                context.DrawLine(new RenderPen(Color.FromArgb(255, tint.R, tint.G, tint.B), 2f),
                                 lane.Left, y + height / 2, lane.Right, y + height / 2);
            }

            if (!ShowHeader) return;

            var header = instance.Tag;
            context.DrawString(header, _statusFont, Color.FromArgb(255, tint.R, tint.G, tint.B),
                               lane.Left + 2, region.Top + 2);

            var caption = instance.Caption;
            var size = context.MeasureString(header, _statusFont);
            context.DrawString(caption, _font, Color.FromArgb(170, 170, 180),
                               lane.Left + 2, region.Top + 2 + (int)size.Height);

            var totals = DevelopedMath.Compact(profile.TotalVolume) + "  " +
                         DevelopedMath.Signed(profile.TotalDelta);

            context.DrawString(totals, _font, Color.FromArgb(150, 150, 160),
                               lane.Left + 2, region.Top + 2 + (int)size.Height * 2);
        }

        private void Status(RenderContext context, Rectangle region, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            context.DrawString(text, _statusFont, Color.FromArgb(230, 170, 80),
                               region.Left + 6, region.Bottom - 18);
        }

        #endregion
    }
}
