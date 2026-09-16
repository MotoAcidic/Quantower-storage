namespace AuctionResponse.Core;

/// <summary>
/// The reference distribution the indicator builds for itself, from what it actually sees.
///
/// This removes the setup burden without removing the honesty. Samples are tagged with the
/// session they came from, and the quantile served to a running session is built from OTHER
/// sessions only — so Section 8's "all contributing data must precede the scoring session"
/// holds by construction rather than by remembering to freeze something at the open.
///
/// Nothing here is synthetic. Until enough real sessions exist, the lookup reports Warmup and
/// no candidate can arm.
/// </summary>
public sealed class LiveBaseline
{
    private sealed class Bucket
    {
        public readonly List<double> Buy = new();
        public readonly List<double> Sell = new();
        public readonly List<double> AbsoluteDelta = new();
        public readonly List<double> AbsoluteResponse = new();
    }

    // sessionId -> (bucketStartMinute, windowMs) -> samples
    private readonly Dictionary<string, Dictionary<(int Bucket, int Window), Bucket>> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxSessions;

    public LiveBaseline(int maxSessions = 30) { _maxSessions = Math.Max(maxSessions, 1); }

    public int SessionCount => _sessions.Count;
    public IReadOnlyCollection<string> SessionIds => _sessions.Keys;

    /// <summary>Sessions usable for scoring <paramref name="currentSessionId"/>: everything but today.</summary>
    public int PriorSessionCount(string currentSessionId)
        => _sessions.Keys.Count(k => !string.Equals(k, currentSessionId, StringComparison.Ordinal));

    public int TotalSamples { get; private set; }

    public void Record(string sessionId, int bucketStartMinute, int windowMs,
        double buyVolume, double sellVolume, double absoluteDelta, double? absoluteResponse)
    {
        if (!_sessions.TryGetValue(sessionId, out var buckets))
        {
            buckets = new Dictionary<(int, int), Bucket>();
            _sessions[sessionId] = buckets;
            TrimOldestSessions(sessionId);
        }

        var key = (bucketStartMinute, windowMs);
        if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Bucket();

        bucket.Buy.Add(buyVolume);
        bucket.Sell.Add(sellVolume);
        bucket.AbsoluteDelta.Add(absoluteDelta);
        if (absoluteResponse is { } r) bucket.AbsoluteResponse.Add(r);
        TotalSamples++;
    }

    private void TrimOldestSessions(string keep)
    {
        while (_sessions.Count > _maxSessions)
        {
            // Session ids are yyyy-MM-dd, so ordinal order is chronological order.
            var oldest = _sessions.Keys.Where(k => !string.Equals(k, keep, StringComparison.Ordinal))
                                       .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (oldest is null) return;
            foreach (var b in _sessions[oldest].Values) TotalSamples -= b.Buy.Count;
            _sessions.Remove(oldest);
        }
    }

    /// <summary>
    /// Builds a frozen artifact from every session EXCEPT the one being scored. Returns null
    /// when no prior session exists at all.
    /// </summary>
    public BaselineArtifact? BuildExcluding(
        string currentSessionId, Config config, InstrumentKey instrument, decimal tickSize,
        string feedMode, string calendarVersion)
    {
        var contributing = _sessions
            .Where(kv => !string.Equals(kv.Key, currentSessionId, StringComparison.Ordinal))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .TakeLast(config.Baseline.LookbackSessions)
            .ToList();

        if (contributing.Count == 0) return null;

        var merged = new Dictionary<(int Bucket, int Window), Bucket>();
        foreach (var (_, buckets) in contributing)
            foreach (var (key, bucket) in buckets)
            {
                if (!merged.TryGetValue(key, out var target)) merged[key] = target = new Bucket();
                target.Buy.AddRange(bucket.Buy);
                target.Sell.AddRange(bucket.Sell);
                target.AbsoluteDelta.AddRange(bucket.AbsoluteDelta);
                target.AbsoluteResponse.AddRange(bucket.AbsoluteResponse);
            }

        var p = config.Candidate.AttackerVolumeQuantile;
        var output = new List<BaselineBucket>();

        foreach (var (key, bucket) in merged.OrderBy(k => k.Key.Bucket).ThenBy(k => k.Key.Window))
        {
            var buy = Statistics.Sorted(bucket.Buy);
            var sell = Statistics.Sorted(bucket.Sell);
            var absDelta = Statistics.Sorted(bucket.AbsoluteDelta);
            var absResponse = Statistics.Sorted(bucket.AbsoluteResponse);

            output.Add(new BaselineBucket
            {
                BucketStartMinute = key.Bucket,
                WindowMs = key.Window,
                SampleCount = buy.Length,
                BuyVolumeQuantiles = new Dictionary<double, double> { [p] = Statistics.Quantile(buy, p) ?? 0d },
                SellVolumeQuantiles = new Dictionary<double, double> { [p] = Statistics.Quantile(sell, p) ?? 0d },
                AbsoluteDeltaQ75 = Statistics.Quantile(absDelta, 0.75),
                AbsoluteResponseQ75 = Statistics.Quantile(absResponse, 0.75),
                SortedBuyVolumes = buy,
                SortedSellVolumes = sell
            });
        }

        return new BaselineArtifact
        {
            Instrument = instrument,
            TickSize = tickSize,
            SessionTimezone = config.Session.Timezone,
            CalendarVersion = calendarVersion,
            ClockMode = config.Timing.ClockMode,
            FeedMode = feedMode,
            WindowsMs = config.Timing.WindowsMs.ToArray(),
            BucketMinutes = config.Baseline.BucketMinutes,
            EligibleSessionIds = contributing.Select(kv => kv.Key).ToArray(),
            FitEndUtc = DateTime.UtcNow,
            SourceLogHashes = new[] { "live-observation" },
            Sha256 = "live",
            ResponseScaleSource = "midpoint",
            Buckets = output
        };
    }

    /// <summary>Progress toward being usable, for an honest status line.</summary>
    public string ProgressLabel(string currentSessionId, Config config)
    {
        var prior = PriorSessionCount(currentSessionId);
        return "learning: " + prior + " of " + config.Baseline.MinimumSessions + " sessions";
    }

    // -------------------------------------------------------------- persistence

    public sealed record Row(string SessionId, int Bucket, int WindowMs, double Buy, double Sell, double AbsDelta, double AbsResponse);

    public IEnumerable<Row> Export()
    {
        foreach (var (sessionId, buckets) in _sessions)
            foreach (var (key, bucket) in buckets)
                for (var i = 0; i < bucket.Buy.Count; i++)
                    yield return new Row(sessionId, key.Bucket, key.Window,
                        bucket.Buy[i], bucket.Sell[i], bucket.AbsoluteDelta[i],
                        i < bucket.AbsoluteResponse.Count ? bucket.AbsoluteResponse[i] : double.NaN);
    }

    public void Import(IEnumerable<Row> rows)
    {
        foreach (var r in rows)
            Record(r.SessionId, r.Bucket, r.WindowMs, r.Buy, r.Sell, r.AbsDelta,
                double.IsNaN(r.AbsResponse) ? null : r.AbsResponse);
    }
}
