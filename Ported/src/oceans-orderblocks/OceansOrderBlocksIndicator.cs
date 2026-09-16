using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ATAS.Indicators;
using Utils.Common.Logging;
using MColor = System.Windows.Media.Color;

namespace OceansOrderBlocks
{
    public enum PanelCorner { TopRight = 0, BottomRight = 1, TopLeft = 2, BottomLeft = 3 }

    /// <summary>
    /// Ocean's Order Blocks -- the MTF order block suite with real order flow.
    ///
    /// Same detection as the Pine "OB Suite Δ": an opposite-colour candle whose extreme is
    /// closed through by the next body, on 1H / 4H / D / W and inside the RTH window on the
    /// chart timeframe. What ATAS adds is the footprint, used three ways:
    ///
    ///   * Zone Δ is what traded at prices INSIDE the zone since it went live -- not, as in
    ///     Pine, the whole market's delta since confirmation wherever price was.
    ///   * The breaker's real bid/ask delta, shown on every block and optionally required to
    ///     agree with the break.
    ///   * The in-zone POC: where the size inside the block actually is.
    ///
    /// Higher-timeframe bars are built from the chart's own bars on Houston time, because an
    /// ATAS indicator cannot request another timeframe. So weekly blocks need weeks of chart
    /// history loaded; the panel says so when they do not have it.
    ///
    /// EVERY time here is Houston time. There is no second zone in the settings or the labels.
    /// </summary>
    [DisplayName("Ocean's Order Blocks")]
    [Category("Ocean")]
    public partial class OceansOrderBlocks : Indicator
    {
        private readonly object _sync = new object();
        private readonly BarWindow _bars;

        private BarClockContext _clock;
        private int _clockTriedAt = -1;
        private TimeSpan _barSize;

        private ObEngine _engine;
        private EngineConfig _config;
        private int _processed = -1;

        private int _footprintHits;
        private int _footprintMisses;

        // Live bar. Nothing here changes zone state -- that is decided on closed bars only, so
        // history and live agree -- it only feeds the readout and the first-touch alert.
        private readonly Dictionary<long, decimal[]> _liveFlow = new Dictionary<long, decimal[]>();
        private readonly HashSet<long> _alerted = new HashSet<long>();
        private bool _alertsSeeded;
        private bool _hasLive;
        private decimal _liveDelta;
        private decimal _liveCvd;

        public OceansOrderBlocks() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);

            _bars = new BarWindow(this);

            // Everything is hand-drawn; the inherited series would plot a line of zeroes.
            DataSeries[0] = new ValueDataSeries("OB", "OB")
            {
                IsHidden = true,
                VisualType = VisualMode.Hide,
                ShowZeroValue = false
            };
        }

        #region Settings - Timeframes

        [Display(GroupName = "Timeframes", Name = "1H blocks", Order = 0)]
        public bool Show1H { get; set; } = false;

        [Display(GroupName = "Timeframes", Name = "4H blocks", Order = 1,
                 Description = "4H bars open at 17, 21, 01, 05, 09, 13 Houston -- anchored to the reopen.")]
        public bool Show4H { get; set; } = true;

        [Display(GroupName = "Timeframes", Name = "Daily blocks", Order = 2,
                 Description = "CME trade date: 17:00 reopen to 16:00 halt.")]
        public bool ShowDaily { get; set; } = true;

        [Display(GroupName = "Timeframes", Name = "Weekly blocks", Order = 3,
                 Description = "Sunday 17:00 to Friday 16:00. Needs weeks of chart history loaded.")]
        public bool ShowWeekly { get; set; } = true;

        [Display(GroupName = "Timeframes", Name = "Session blocks (chart TF)", Order = 4,
                 Description = "Chart-timeframe blocks whose candle and breaker both print inside the window.")]
        public bool ShowSession { get; set; } = false;

        [Display(GroupName = "Timeframes", Name = "Session start (Houston)", Order = 5)]
        public TimeSpan SessionStart { get; set; } = new TimeSpan(8, 30, 0);

        [Display(GroupName = "Timeframes", Name = "Session end (Houston)", Order = 6,
                 Description = "Also the window the session CVD counts in.")]
        public TimeSpan SessionEnd { get; set; } = new TimeSpan(15, 0, 0);

        #endregion

        #region Settings - Logic

        [Display(GroupName = "Logic", Name = "Zone edges", Order = 10)]
        public ZoneMode ZoneEdges { get; set; } = ZoneMode.WickToWick;

        [Range(0, 10)]
        [Display(GroupName = "Logic", Name = "Min displacement (x ATR14)", Order = 11,
                 Description = "0 = off. Breaker body must be at least this many ATR14 of its own " +
                               "timeframe. Weekly ATR needs 14 weeks of history before it passes anything.")]
        public decimal MinDisplacementAtr { get; set; } = 0m;

        [Display(GroupName = "Logic", Name = "Breaker delta must agree", Order = 12,
                 Description = "Bull blocks need positive bid/ask delta on the breaker, bear blocks negative. " +
                               "Buyers did the breaking, or it does not count.")]
        public bool BreakerDeltaAgrees { get; set; } = false;

        [Display(GroupName = "Logic", Name = "Mitigation trigger", Order = 13,
                 Description = "Full mitigation is price through the FAR side of the zone.")]
        public MitigationTrigger Mitigation { get; set; } = MitigationTrigger.Wick;

        [Display(GroupName = "Logic", Name = "Keep mitigated (faded)", Order = 14)]
        public bool KeepMitigated { get; set; } = false;

        [Range(1, 200)]
        [Display(GroupName = "Logic", Name = "Max faded kept", Order = 15)]
        public int MaxFaded { get; set; } = 20;

        [Range(1, 40)]
        [Display(GroupName = "Logic", Name = "Max active per side per TF", Order = 16)]
        public int MaxPerSide { get; set; } = 10;

        #endregion

        #region Settings - Order flow

        [Display(GroupName = "Order flow", Name = "Zone delta in labels", Order = 20,
                 Description = "Volume and bid/ask delta traded at prices inside the zone since it went live.")]
        public bool ShowZoneFlow { get; set; } = true;

        [Display(GroupName = "Order flow", Name = "In-zone POC line", Order = 21,
                 Description = "The price inside the block where the most volume has traded.")]
        public bool ShowZonePoc { get; set; } = true;

        [Display(GroupName = "Order flow", Name = "Delta panel", Order = 22)]
        public bool ShowDeltaPanel { get; set; } = true;

        [Display(GroupName = "Order flow", Name = "CVD anchor", Order = 23,
                 Description = "Session: resets at the session start and holds after the end. Daily: resets at the 17:00 reopen.")]
        public CvdAnchor CvdMode { get; set; } = CvdAnchor.Session;

        [Display(GroupName = "Order flow", Name = "Panel corner", Order = 24)]
        public PanelCorner DeltaPanelCorner { get; set; } = PanelCorner.TopRight;

        #endregion

        #region Settings - Alerts

        [Display(GroupName = "Alerts", Name = "Alert on first touch", Order = 30,
                 Description = "Once per block, the first time price comes back into it. Live prints only -- " +
                               "never on chart load.")]
        public bool AlertFirstTouch { get; set; } = true;

        [Display(GroupName = "Alerts", Name = "Alert sound", Order = 31)]
        public string AlertSound { get; set; } = "alert1";

        #endregion

        #region Settings - Clock

        [Display(GroupName = "Clock", Name = "Time zone", Order = 40,
                 Description = "One zone for everything. Houston is Central Standard Time.")]
        public string TimeZoneId { get; set; } = "Central Standard Time";

        [Display(GroupName = "Clock", Name = "Bar clock", Order = 41,
                 Description = "Auto settles it from evidence and refuses to guess. Set by hand only if it cannot.")]
        public BarClock BarTimes { get; set; } = BarClock.Auto;

        /// <summary>
        /// 16, NOT 15. 15:00 is the cash close and trading carries straight on; the empty hour
        /// is 16:00-17:00. With 15 the bar clock never settles and nothing draws. This has
        /// shipped wrong three times across the oceans-* suite.
        /// </summary>
        [Range(0, 23)]
        [Display(GroupName = "Clock", Name = "Daily halt hour", Order = 42,
                 Description = "The hour MNQ is shut, Houston time (16, not 15 - 15:00 is the cash close). " +
                               "Used to settle the bar clock.")]
        public int HaltHour { get; set; } = 16;

        #endregion

        #region Settings - Visual

        [Display(GroupName = "Visual", Name = "Demand colour", Order = 50)]
        public MColor DemandColor { get; set; } = MColor.FromRgb(0x22, 0xC5, 0x5E);

        [Display(GroupName = "Visual", Name = "Supply colour", Order = 51)]
        public MColor SupplyColor { get; set; } = MColor.FromRgb(0xEF, 0x44, 0x44);

        [Display(GroupName = "Visual", Name = "Mitigated colour", Order = 52)]
        public MColor FadedColor { get; set; } = MColor.FromRgb(128, 128, 128);

        [Range(1, 100)]
        [Display(GroupName = "Visual", Name = "Fill opacity %", Order = 53)]
        public int FillOpacity { get; set; } = 15;

        [Display(GroupName = "Visual", Name = "Midlines (50% / CE)", Order = 54)]
        public bool ShowMidlines { get; set; } = true;

        [Display(GroupName = "Visual", Name = "Right-edge labels", Order = 55)]
        public bool ShowZoneLabels { get; set; } = true;

        #endregion

        private decimal Tick
        {
            get
            {
                var info = InstrumentInfo;
                return info != null && info.TickSize > 0m ? info.TickSize : 0m;
            }
        }

        #region Lifecycle

        /// <summary>
        /// ATAS re-runs the whole series on load and on any setting change, arriving here as
        /// bar 0. Everything stateful is torn down there, which is also how a changed setting
        /// reaches the engine: it is rebuilt from the properties on the next call.
        /// </summary>
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
            {
                ResetAll();
                return;
            }

            if (_clock == null || !_clock.Valid)
            {
                // Once per new bar, not per tick: the resolver scans every bar loaded.
                if (CurrentBar != _clockTriedAt)
                {
                    _clockTriedAt = CurrentBar;
                    SettleClock();
                }

                if (_clock == null || !_clock.Valid)
                {
                    if (bar >= CurrentBar - 1) CacheForRender();
                    return;
                }
            }

            if (_engine == null)
            {
                _config = BuildConfig();
                _engine = new ObEngine(_config);
            }

            // Zone state moves on CLOSED bars only. The live bar is CurrentBar - 1; folding it
            // per tick would count its footprint again on every tick, and would let a wick
            // that later closes back inside mitigate a zone on the live chart that history
            // would have kept.
            var lastClosed = CurrentBar - 2;
            while (_processed < lastClosed)
            {
                _processed++;
                FoldBar(_processed);
            }

            if (bar >= CurrentBar - 1)
            {
                LivePass();
                CacheForRender();
            }
        }

        private void ResetAll()
        {
            lock (_sync)
            {
                _engine = null;
                _config = null;
                _processed = -1;
                _clock = null;
                _clockTriedAt = -1;
                _barSize = TimeSpan.Zero;
                _footprintHits = 0;
                _footprintMisses = 0;

                _liveFlow.Clear();
                _alerted.Clear();
                _alertsSeeded = false;
                _hasLive = false;

                _renderZones = new List<ZoneView>();
                _renderRows = new List<PanelRow>();
                _clockError = null;
            }
        }

        private void SettleClock()
        {
            if (CurrentBar < 3) return;

            _barSize = BarMath.Duration(_bars, 300);

            var marketNow = MarketTime.Year > 2000 ? MarketTime : default(DateTime);

            _clock = BarClockContext.Create(TimeZoneId, BarTimes, _bars, DateTime.UtcNow, marketNow,
                                            HaltHour, _barSize);

            if (_clock != null && !_clock.Valid) this.LogWarn(_clock.Error);
        }

        private EngineConfig BuildConfig()
        {
            var c = new EngineConfig
            {
                SessionStart = SessionStart,
                SessionEnd = SessionEnd,
                Rules = new ObRules
                {
                    Mode = ZoneEdges,
                    MinDisplacementAtr = MinDisplacementAtr,
                    BreakerDeltaAgrees = BreakerDeltaAgrees
                },
                MaxPerSide = MaxPerSide,
                KeepMitigated = KeepMitigated,
                MaxFaded = MaxFaded,
                Trigger = Mitigation,
                Cvd = CvdMode,
                BarDuration = _barSize
            };

            c.Enabled[(int)ObTf.H1] = Show1H && Fits(ObTf.H1);
            c.Enabled[(int)ObTf.H4] = Show4H && Fits(ObTf.H4);
            c.Enabled[(int)ObTf.Daily] = ShowDaily && Fits(ObTf.Daily);
            c.Enabled[(int)ObTf.Weekly] = ShowWeekly && Fits(ObTf.Weekly);
            c.Enabled[(int)ObTf.Session] = ShowSession && _barSize < TimeSpan.FromDays(1);

            return c;
        }

        /// <summary>
        /// A timeframe finer than the chart cannot be built from it: a 2H chart bar would
        /// become a "1H bar" with two hours of range in it. Unknown bar size (tick, range)
        /// is allowed -- those bars are finer than any of these in practice.
        /// </summary>
        private bool Fits(ObTf tf)
        {
            return _barSize <= TimeSpan.Zero || _barSize <= Cme.Nominal(tf);
        }

        #endregion

        #region Folding

        private void FoldBar(int i)
        {
            var c = GetCandle(i);
            if (c == null) return;

            var b = new ChartBar
            {
                Index = i,
                Local = _clock.ToLocal(c.Time),
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume,
                Delta = c.Delta
            };

            _engine.Fold(b, () => Levels(c));
        }

        /// <summary>
        /// One bar's footprint. GetAllPriceLevels enumerates only the levels that traded; the
        /// tick walk is the fallback for a feed that does not support it. Null when there is
        /// no footprint at all, which the engine reads as "no zone flow", never as zero flow.
        /// </summary>
        private IList<Level> Levels(IndicatorCandle c)
        {
            var list = new List<Level>();

            IEnumerable<PriceVolumeInfo> all;
            try { all = c.GetAllPriceLevels(); }
            catch { all = null; }

            if (all != null)
            {
                foreach (var info in all)
                {
                    if (info == null) continue;
                    list.Add(new Level(info.Price, info.Volume, info.Ask - info.Bid));
                }
            }

            if (list.Count == 0)
            {
                var tick = Tick;
                if (tick > 0m)
                {
                    for (var p = c.High; p >= c.Low; p -= tick)
                    {
                        var info = c.GetPriceVolumeInfo(p);
                        if (info == null) continue;
                        list.Add(new Level(p, info.Volume, info.Ask - info.Bid));
                    }
                }
            }

            if (list.Count == 0)
            {
                _footprintMisses++;
                return null;
            }

            _footprintHits++;
            return list;
        }

        /// <summary>
        /// The forming bar: live CVD, live in-zone flow, and the first-touch alert. Reads zone
        /// state, never writes it.
        /// </summary>
        private void LivePass()
        {
            _liveFlow.Clear();
            _hasLive = false;

            var i = CurrentBar - 1;
            if (i <= _processed || i < 0) return;

            var c = GetCandle(i);
            if (c == null) return;

            var local = _clock.ToLocal(c.Time);

            _hasLive = true;
            _liveDelta = c.Delta;
            _liveCvd = _engine.Cvd.Peek(local, c.Delta);

            IList<Level> levels = null;
            var asked = false;

            foreach (var z in _engine.Book.Active)
            {
                if (i <= z.LiveFrom || !z.Overlaps(c.Low, c.High)) continue;

                if (!asked)
                {
                    levels = Levels(c);
                    asked = true;
                }

                if (levels == null) break;

                decimal v, d;
                ZoneFlow.Measure(z, levels, out v, out d);
                _liveFlow[z.Id] = new[] { v, d };
            }

            TouchAlerts(c, i);
        }

        #endregion

        #region Alerts

        /// <summary>
        /// Two gates keep this from crying on load, and an indicator that cries on load gets
        /// muted. The first live pass after any recalculation only records which blocks the
        /// forming bar already sits in, silently. After that, a block alerts only when its first
        /// touch arrives on a print less than five minutes old -- so a replayed or stale bar
        /// never sounds.
        /// </summary>
        private void TouchAlerts(IndicatorCandle c, int i)
        {
            var seeding = !_alertsSeeded;
            _alertsSeeded = true;

            var sound = !seeding && AlertFirstTouch && IsLivePrint(c);

            foreach (var z in _engine.Book.Active)
            {
                if (!z.FreshTouch(c.Low, c.High, i)) continue;
                if (!_alerted.Add(z.Id)) continue;

                if (sound) Fire(z);
            }
        }

        private bool IsLivePrint(IndicatorCandle c)
        {
            var utc = _clock.ToUtc(c.LastTime);
            return utc.HasValue && Math.Abs((DateTime.UtcNow - utc.Value).TotalMinutes) <= 5.0;
        }

        private void Fire(Zone z)
        {
            if (!AlertsEnabled) return;

            var text = "OB first touch " + Fmt.Tag(z.Tf) + (z.Bull ? " demand " : " supply ") +
                       Fmt.Price(z.Bottom) + "-" + Fmt.Price(z.Top) +
                       " | brk " + Fmt.Signed(z.BreakerDelta);

            try
            {
                AddAlert(AlertSound, InstrumentInfo != null ? InstrumentInfo.Instrument : "MNQ", text,
                         z.Bull ? DemandColor : SupplyColor, MColor.FromRgb(250, 250, 250));
            }
            catch (Exception) { }
        }

        #endregion

        #region Bar adapter

        /// <summary>
        /// Candle access for the clock resolver. GetCandle, never SourceDataSeries: that series
        /// is null until the platform wires it up and returns 0 rather than failing.
        /// </summary>
        private sealed class BarWindow : IBarWindow
        {
            private readonly OceansOrderBlocks _owner;
            private int _cachedIndex = -1;
            private IndicatorCandle _cached;

            public BarWindow(OceansOrderBlocks owner) { _owner = owner; }

            public int Count { get { return _owner.CurrentBar; } }

            private IndicatorCandle At(int bar)
            {
                if (bar != _cachedIndex)
                {
                    _cached = _owner.GetCandle(bar);
                    _cachedIndex = bar;
                }

                return _cached;
            }

            public DateTime Time(int bar) { return At(bar).Time; }
            public decimal Open(int bar) { return At(bar).Open; }
            public decimal High(int bar) { return At(bar).High; }
            public decimal Low(int bar) { return At(bar).Low; }
            public decimal Close(int bar) { return At(bar).Close; }
        }

        #endregion
    }
}
