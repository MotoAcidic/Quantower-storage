using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OceansCurrent
{
    /// <summary>One scheduled release. Times are UTC here and only become Houston time on the panel.</summary>
    public sealed class MacroEvent
    {
        public string Title;
        public DateTime WhenUtc;
        public string Impact;
        public string Forecast;
        public string Previous;

        /// <summary>Other high-impact releases at the same minute (CPI m/m + Core CPI y/y + ...).</summary>
        public int AlsoAtSameTime;
    }

    /// <summary>
    /// The scheduled releases that can move price straight through every trigger level on the
    /// panel -- CPI, NFP, FOMC -- from the ForexFactory weekly feed, the same source and the
    /// same USD filter the Crabel dashboard uses (<c>oceans-crabel/dash/server.mjs</c>).
    ///
    /// What it will not do:
    /// - Guess past the feed. It covers the CURRENT WEEK ONLY and rolls over on Sunday, so
    ///   Friday afternoon it legitimately holds nothing ahead. That is reported as "nothing more
    ///   in this week's feed", never "nothing scheduled".
    /// - Touch the network from the chart. The fetch runs on the thread pool, at most every 30
    ///   minutes, backing off 5 after a failure (the feed rate-limits with 429s), and the result
    ///   is swapped in as one immutable list.
    /// - Change the bias. A release is shown and alerted; the engine does not freeze or discount
    ///   itself around one. Whether it should is a calibration question, not a setting to guess.
    /// </summary>
    public sealed class EventCalendar
    {
        public const string FeedUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";

        private static readonly HttpClient Http = CreateClient();

        private readonly string _cachePath;
        private IReadOnlyList<MacroEvent> _events = new MacroEvent[0];
        private DateTime _fetchedUtc;
        private DateTime _lastTryUtc;
        private string _problem;
        private int _busy;

        public EventCalendar(string cacheFolder)
        {
            _cachePath = string.IsNullOrWhiteSpace(cacheFolder) ? null : Path.Combine(cacheFolder, "ff_week.json");
            LoadCache();
        }

        public IReadOnlyList<MacroEvent> Events => _events;
        public DateTime FetchedUtc => _fetchedUtc;
        public string Problem => _problem;

        /// <summary>Kicks off a fetch if one is due. Returns immediately; never blocks the caller.</summary>
        public void RefreshIfDue(DateTime nowUtc)
        {
            var fresh = _events.Count > 0 && nowUtc - _fetchedUtc < TimeSpan.FromMinutes(30);
            var backingOff = nowUtc - _lastTryUtc < TimeSpan.FromMinutes(5);
            if (fresh || backingOff) return;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

            _lastTryUtc = nowUtc;

            Task.Run(async () =>
            {
                try
                {
                    var json = await Http.GetStringAsync(FeedUrl).ConfigureAwait(false);
                    var parsed = Parse(json);
                    _events = parsed;
                    _fetchedUtc = DateTime.UtcNow;
                    _problem = null;

                    if (_cachePath != null)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
                            File.WriteAllText(_cachePath, json);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    // The last good list stays up, labelled by its age on the panel. A stale
                    // calendar that says it is stale beats a blank one.
                    _problem = ex is HttpRequestException ? "calendar feed unreachable" : "calendar " + ex.GetType().Name;
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }

        private void LoadCache()
        {
            try
            {
                if (_cachePath == null || !File.Exists(_cachePath)) return;
                _events = Parse(File.ReadAllText(_cachePath));
                _fetchedUtc = File.GetLastWriteTimeUtc(_cachePath);
            }
            catch { }
        }

        /// <summary>
        /// High-impact USD (and "All" -- OPEC, G20) releases, in time order, with same-minute
        /// releases folded into the first one. Anything unparseable is dropped, never guessed.
        /// </summary>
        public static IReadOnlyList<MacroEvent> Parse(string json)
        {
            var list = new List<MacroEvent>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            // A rate-limited feed answers with an HTML page. That is "no calendar", not a crash.
            JsonDocument parsed;
            try { parsed = JsonDocument.Parse(json); }
            catch (JsonException) { return list; }

            using (var doc = parsed)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var country = Str(e, "country");
                    if (country != "USD" && country != "All") continue;

                    var impact = Str(e, "impact");
                    if (!string.Equals(impact, "High", StringComparison.OrdinalIgnoreCase)) continue;

                    DateTimeOffset when;
                    if (!DateTimeOffset.TryParse(Str(e, "date"), CultureInfo.InvariantCulture,
                                                 DateTimeStyles.None, out when))
                        continue;

                    list.Add(new MacroEvent
                    {
                        Title = Str(e, "title"),
                        WhenUtc = when.UtcDateTime,
                        Impact = impact,
                        Forecast = Str(e, "forecast"),
                        Previous = Str(e, "previous")
                    });
                }
            }

            // Stable: releases at the same minute keep the feed's own order, so the headline one
            // (Core CPI, listed first) is the one named. List.Sort is not stable.
            list = System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(list, e => e.WhenUtc));

            var folded = new List<MacroEvent>();
            foreach (var ev in list)
            {
                var prev = folded.Count > 0 ? folded[folded.Count - 1] : null;
                if (prev != null && prev.WhenUtc == ev.WhenUtc) { prev.AlsoAtSameTime++; continue; }
                folded.Add(ev);
            }

            return folded;
        }

        /// <summary>
        /// The next release, or one that printed within the last <paramref name="justPrinted"/>
        /// -- the minutes after a CPI are when the bias is least trustworthy, so it stays up.
        /// </summary>
        public static MacroEvent Next(IReadOnlyList<MacroEvent> events, DateTime nowUtc, TimeSpan justPrinted)
        {
            if (events == null) return null;

            foreach (var e in events)
                if (e.WhenUtc >= nowUtc - justPrinted) return e;

            return null;
        }

        private static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            // The feed 429s anything that looks like a script.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            return c;
        }
    }
}
