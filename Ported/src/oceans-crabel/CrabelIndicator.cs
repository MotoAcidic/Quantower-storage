using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json;
using ATAS.Indicators;

namespace OceansCrabel
{
    public enum BarTimeSource
    {
        /// <summary>Bar timestamps are UTC and must be converted to the session time zone.</summary>
        Utc = 0,

        /// <summary>Bar timestamps are already in the session time zone; use as-is.</summary>
        ExchangeLocal = 1
    }

    /// <summary>
    /// Ocean's Crabel -- Toby Crabel contraction patterns and Stretch, computed from RTH
    /// sessions and written to JSON for Operator OS. Draws nothing on the chart.
    ///
    /// Attach to an INTRADAY chart (1m-15m). Daily bars cannot be used: the daily bar's
    /// open is the Globex open, and a Stretch built on that is not a Crabel Stretch.
    /// </summary>
    [DisplayName("Ocean's Crabel")]
    public class CrabelIndicator : Indicator
    {
        private const int MinBarsPerSession = 2;

        private readonly object _sync = new object();
        private DateTime _lastWriteUtc = DateTime.MinValue;

        public CrabelIndicator() : base(false)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = false;

            if (DataSeries.Count > 0 && DataSeries[0] is ValueDataSeries v)
                v.VisualType = VisualMode.Hide;
        }

        #region Settings

        [Display(Name = "Session start", GroupName = "RTH session", Order = 10)]
        public TimeSpan SessionStart { get; set; } = new TimeSpan(9, 30, 0);

        [Display(Name = "Session end", GroupName = "RTH session", Order = 20)]
        public TimeSpan SessionEnd { get; set; } = new TimeSpan(16, 0, 0);

        [Display(Name = "Time zone id", GroupName = "RTH session", Order = 30,
                 Description = "Windows time zone id for the session window.")]
        public string TimeZoneId { get; set; } = "Eastern Standard Time";

        [Display(Name = "Bar time source", GroupName = "RTH session", Order = 40,
                 Description = "Whether chart bar timestamps are UTC or already exchange-local. " +
                               "If this is wrong, every session fails validation rather than " +
                               "producing shifted numbers -- check the JSON diagnostics block.")]
        public BarTimeSource BarTimes { get; set; } = BarTimeSource.Utc;

        [Display(Name = "Open tolerance (min)", GroupName = "RTH session", Order = 50,
                 Description = "A session is invalid if its first bar is more than this many " +
                               "minutes away from the configured session start.")]
        public int OpenToleranceMinutes { get; set; } = 5;

        // Deep by default. Base rates are computed from whatever this emits, and a
        // 60-session cap would silently starve them -- the whole point of driving
        // the statistics from real RTH data is having enough of it.
        [Display(Name = "Max sessions in output", GroupName = "Output", Order = 60)]
        public int MaxSessions { get; set; } = 750;

        [Display(Name = "Output file name", GroupName = "Output", Order = 70)]
        public string OutputFileName { get; set; } = "oceans_crabel.json";

        [Display(Name = "Min write interval (sec)", GroupName = "Output", Order = 80)]
        public int WriteThrottleSeconds { get; set; } = 15;

        #endregion

        protected override void OnCalculate(int bar, decimal value)
        {
            // All work happens once per pass, not per bar.
            if (bar != CurrentBar - 1) return;

            if ((DateTime.UtcNow - _lastWriteUtc).TotalSeconds < Math.Max(1, WriteThrottleSeconds))
                return;

            BuildAndWrite();
        }

        protected override void OnFinishRecalculate() => BuildAndWrite();

        private void BuildAndWrite()
        {
            lock (_sync)
            {
                try
                {
                    var payload = Build();
                    WriteAtomic(payload);
                    _lastWriteUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    TryWriteError(ex.ToString());
                }
            }
        }

        // ChartInfo/InstrumentInfo are null until the indicator is attached to a chart,
        // so every read goes through these rather than touching the properties directly.
        private string InstrumentName => InstrumentInfo?.Instrument;
        private string ChartTimeFrame => ChartInfo?.TimeFrame;
        private decimal? InstrumentTickSize => InstrumentInfo?.TickSize;

        #region Session construction

        private TimeZoneInfo ResolveTimeZone(List<string> errors)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
            }
            catch (Exception ex)
            {
                errors.Add($"Unknown time zone id '{TimeZoneId}': {ex.Message}");
                return null;
            }
        }

        private DateTime ToSessionLocal(DateTime barTime, TimeZoneInfo tz)
        {
            if (BarTimes == BarTimeSource.ExchangeLocal) return barTime;

            var utc = DateTime.SpecifyKind(barTime, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        }

        /// <summary>
        /// Median gap between consecutive bar open times, used to decide whether the final
        /// session has actually run to the closing bell. Data-driven so it does not depend
        /// on the wall clock or on the platform's own session definition.
        /// </summary>
        private TimeSpan EstimateBarSpan(TimeZoneInfo tz)
        {
            var gaps = new List<double>();
            var last = DateTime.MinValue;

            for (var i = Math.Max(0, CurrentBar - 200); i < CurrentBar; i++)
            {
                var t = ToSessionLocal(GetCandle(i).Time, tz);
                if (last != DateTime.MinValue)
                {
                    var g = (t - last).TotalSeconds;
                    if (g > 0 && g <= 3600) gaps.Add(g);
                }
                last = t;
            }

            if (gaps.Count == 0) return TimeSpan.Zero;

            gaps.Sort();
            return TimeSpan.FromSeconds(gaps[gaps.Count / 2]);
        }

        private List<SessionBar> BuildSessions(TimeZoneInfo tz, TimeSpan barSpan)
        {
            var map = new Dictionary<DateTime, SessionBar>();
            var order = new List<DateTime>();

            for (var i = 0; i < CurrentBar; i++)
            {
                var c = GetCandle(i);
                var local = ToSessionLocal(c.Time, tz);
                var tod = local.TimeOfDay;

                // RTH window only. Everything outside is discarded, which is the point.
                if (tod < SessionStart || tod >= SessionEnd) continue;

                var key = local.Date;

                if (!map.TryGetValue(key, out var s))
                {
                    s = new SessionBar
                    {
                        Date = key,
                        Open = c.Open,
                        High = c.High,
                        Low = c.Low,
                        FirstBarLocal = local
                    };
                    map[key] = s;
                    order.Add(key);
                }

                if (c.High > s.High) s.High = c.High;
                if (c.Low < s.Low) s.Low = c.Low;

                s.Close = c.Close;
                s.LastBarLocal = local;
                s.BarCount++;
            }

            order.Sort();
            var sessions = order.Select(k => map[k]).ToList();

            for (var i = 0; i < sessions.Count; i++)
                Validate(sessions[i], i == sessions.Count - 1, barSpan);

            return sessions;
        }

        private void Validate(SessionBar s, bool isLast, TimeSpan barSpan)
        {
            // A session counts as complete once its last bar plus one bar-span reaches the bell.
            s.Complete = !isLast || (barSpan > TimeSpan.Zero &&
                                     s.LastBarLocal.TimeOfDay + barSpan >= SessionEnd);

            if (s.BarCount < MinBarsPerSession)
            {
                s.Valid = false;
                s.InvalidReason = $"only {s.BarCount} bar(s) in session - chart timeframe is too " +
                                  "coarse for RTH aggregation (attach to an intraday chart)";
                return;
            }

            var drift = (s.FirstBarLocal.TimeOfDay - SessionStart).TotalMinutes;
            if (Math.Abs(drift) > OpenToleranceMinutes)
            {
                s.Valid = false;
                s.InvalidReason = $"first bar at {s.FirstBarLocal:HH:mm} is {drift:0.#} min from " +
                                  $"session start {SessionStart:hh\\:mm} - check 'Bar time source' " +
                                  "and 'Time zone id', or the chart lacks history for this session";
                return;
            }

            s.Valid = true;
            s.InvalidReason = null;
        }

        #endregion

        #region Payload

        private object Build()
        {
            var errors = new List<string>();
            var tz = ResolveTimeZone(errors);

            if (tz == null)
                return Error(errors);

            var barSpan = EstimateBarSpan(tz);
            var all = BuildSessions(tz, barSpan);

            // Only complete, validated sessions feed the pattern math.
            var history = all.Where(s => s.Valid && s.Complete).ToList();
            var current = all.LastOrDefault(s => s.Valid && !s.Complete);

            var invalid = all.Count(s => !s.Valid);
            if (invalid > 0)
                errors.Add($"{invalid} of {all.Count} session(s) failed validation and were excluded " +
                           "- see sessions[].invalidReason");

            if (history.Count == 0)
            {
                errors.Add("no complete valid RTH sessions could be built from this chart");
                return Error(errors);
            }

            var lastIdx = history.Count - 1;
            var last = history[lastIdx];
            var flags = CrabelMath.Evaluate(history, lastIdx);

            // Stretch for the NEXT session is built from the 10 completed sessions behind it.
            var stretch = CrabelMath.Stretch(history, history.Count);
            string stretchReason = null;
            if (!stretch.HasValue)
                stretchReason = $"needs {CrabelMath.StretchLookback} complete valid sessions, " +
                                $"have {history.Count}";

            return new
            {
                schema = "oceans-crabel/1",
                generatedUtc = DateTime.UtcNow.ToString("o"),
                status = errors.Count == 0 ? "ok" : "ok_with_warnings",
                warnings = errors,

                instrument = InstrumentName,
                chartTimeFrame = ChartTimeFrame,
                tickSize = InstrumentTickSize,

                session = new
                {
                    timeZoneId = TimeZoneId,
                    start = SessionStart.ToString(@"hh\:mm"),
                    end = SessionEnd.ToString(@"hh\:mm"),
                    barTimeSource = BarTimes.ToString(),
                    estimatedBarSpanSeconds = (int)barSpan.TotalSeconds,
                    completeSessions = history.Count
                },

                stretch = new
                {
                    value = stretch,
                    lookback = CrabelMath.StretchLookback,
                    unavailableReason = stretchReason
                },

                lastCompleteSession = Describe(last, flags),

                // ORB brackets for the session in progress. Present only when both the
                // open and the Stretch are genuinely known.
                orb = BuildOrb(current, stretch),

                sessions = history
                    .Skip(Math.Max(0, history.Count - Math.Max(1, MaxSessions)))
                    .Select((s, i) => Describe(s, null))
                    .ToList(),

                excludedSessions = all.Where(s => !s.Valid).Select(s => new
                {
                    date = s.Date.ToString("yyyy-MM-dd"),
                    barCount = s.BarCount,
                    firstBarLocal = s.BarCount > 0 ? s.FirstBarLocal.ToString("HH:mm:ss") : null,
                    lastBarLocal = s.BarCount > 0 ? s.LastBarLocal.ToString("HH:mm:ss") : null,
                    reason = s.InvalidReason
                }).ToList()
            };
        }

        private object BuildOrb(SessionBar current, decimal? stretch)
        {
            if (current == null)
                return new { available = false, reason = "no valid session in progress" };

            if (!stretch.HasValue)
                return new { available = false, reason = "Stretch unavailable - see stretch.unavailableReason" };

            return new
            {
                available = true,
                basis = "current session open +/- Stretch",
                sessionDate = current.Date.ToString("yyyy-MM-dd"),
                sessionOpen = current.Open,
                stretch = stretch.Value,
                buyStop = current.Open + stretch.Value,
                sellStop = current.Open - stretch.Value
            };
        }

        private static object Describe(SessionBar s, CrabelFlags f)
        {
            return new
            {
                date = s.Date.ToString("yyyy-MM-dd"),
                dayOfWeek = s.Date.DayOfWeek.ToString(),
                open = s.Open,
                high = s.High,
                low = s.Low,
                close = s.Close,
                range = s.Range,
                barCount = s.BarCount,
                firstBarLocal = s.FirstBarLocal.ToString("HH:mm:ss"),
                lastBarLocal = s.LastBarLocal.ToString("HH:mm:ss"),
                flags = f == null ? null : new
                {
                    nr4 = f.Nr4,
                    nr7 = f.Nr7,
                    nr20 = f.Nr20,
                    insideDay = f.InsideDay,
                    idNr4 = f.IdNr4,
                    twoBarNr20 = f.TwoBarNr20,
                    clv = f.Clv,
                    rangePctOf20dAvg = f.RangePctOf20d
                }
            };
        }

        private object Error(List<string> errors) => new
        {
            schema = "oceans-crabel/1",
            generatedUtc = DateTime.UtcNow.ToString("o"),
            status = "error",
            errors,
            instrument = InstrumentName,
            chartTimeFrame = ChartTimeFrame
        };

        #endregion

        #region Output

        private string OutputPath
        {
            get
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(dir, "ATAS", string.IsNullOrWhiteSpace(OutputFileName)
                    ? "oceans_crabel.json"
                    : OutputFileName);
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>
        /// Write via temp file + replace so a reader never sees a half-written document.
        /// </summary>
        private void WriteAtomic(object payload)
        {
            var path = OutputPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private void TryWriteError(string detail)
        {
            try
            {
                WriteAtomic(new
                {
                    schema = "oceans-crabel/1",
                    generatedUtc = DateTime.UtcNow.ToString("o"),
                    status = "error",
                    errors = new[] { detail },
                    instrument = InstrumentName
                });
            }
            catch
            {
                // Nothing further we can do; never take the platform down over a log write.
            }
        }

        #endregion
    }
}
