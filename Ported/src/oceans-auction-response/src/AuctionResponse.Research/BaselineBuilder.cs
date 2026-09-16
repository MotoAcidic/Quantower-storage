using AuctionResponse.Core;

namespace AuctionResponse.Research;

/// <summary>One non-overlapping baseline observation.</summary>
public readonly record struct BaselineSample(
    string SessionId,
    int MinutesFromSessionStart,
    int WindowMs,
    double BuyVolume,
    double SellVolume,
    double AbsoluteDelta,
    double AbsoluteResponse);

public sealed record BaselineBuildReport(
    BaselineArtifact Artifact,
    int SessionsUsed,
    int SamplesUsed,
    IReadOnlyList<string> Warnings)
{
    public bool MeetsGates => Warnings.Count == 0;
}

/// <summary>
/// Builds the frozen baseline artifact from completed, eligible sessions (Section 8).
///
/// Offline only. It runs outside host callbacks and outside rendering, and every sample it
/// consumes must precede the session the artifact will be used to score.
///
/// The gates are enforced here rather than assumed: at least the configured number of prior
/// sessions, and at least the configured samples per bucket and window. A bucket that misses
/// them is emitted with its real sample count so the resolver reports Warmup, rather than
/// being quietly padded.
/// </summary>
public sealed class BaselineBuilder
{
    private readonly Config _config;

    public BaselineBuilder(Config config) { _config = config; }

    public BaselineBuildReport Build(
        IReadOnlyList<BaselineSample> samples,
        InstrumentKey instrument,
        decimal tickSize,
        string feedMode,
        string calendarVersion,
        DateTime fitEndUtc,
        IReadOnlyList<string> sourceLogHashes)
    {
        var warnings = new List<string>();

        var sessions = samples.Select(s => s.SessionId).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (sessions.Count < _config.Baseline.MinimumSessions)
            warnings.Add("only " + sessions.Count + " eligible sessions; the minimum is " + _config.Baseline.MinimumSessions);

        // Keep only the most recent lookback window of sessions. Nothing is merged across
        // expiries: the caller supplies samples for one dated contract.
        var kept = sessions.TakeLast(_config.Baseline.LookbackSessions).ToHashSet(StringComparer.Ordinal);
        var used = samples.Where(s => kept.Contains(s.SessionId)).ToList();

        var buckets = new List<BaselineBucket>();
        var p = _config.Candidate.AttackerVolumeQuantile;

        foreach (var group in used
                     .GroupBy(s => (Bucket: s.MinutesFromSessionStart / _config.Baseline.BucketMinutes * _config.Baseline.BucketMinutes, s.WindowMs))
                     .OrderBy(g => g.Key.Bucket).ThenBy(g => g.Key.WindowMs))
        {
            var buy = Statistics.Sorted(group.Select(s => s.BuyVolume));
            var sell = Statistics.Sorted(group.Select(s => s.SellVolume));
            var absDelta = Statistics.Sorted(group.Select(s => s.AbsoluteDelta));
            var absResponse = Statistics.Sorted(group.Select(s => s.AbsoluteResponse));

            var count = group.Count();
            if (count < _config.Baseline.MinimumSamplesPerBucketWindow)
                warnings.Add("bucket " + group.Key.Bucket + "min / " + group.Key.WindowMs + "ms has " + count +
                             " samples, below the minimum of " + _config.Baseline.MinimumSamplesPerBucketWindow);

            buckets.Add(new BaselineBucket
            {
                BucketStartMinute = group.Key.Bucket,
                WindowMs = group.Key.WindowMs,
                SampleCount = count,
                BuyVolumeQuantiles = new Dictionary<double, double> { [p] = Statistics.Quantile(buy, p) ?? 0d },
                SellVolumeQuantiles = new Dictionary<double, double> { [p] = Statistics.Quantile(sell, p) ?? 0d },
                AbsoluteDeltaQ75 = Statistics.Quantile(absDelta, 0.75),
                AbsoluteResponseQ75 = Statistics.Quantile(absResponse, 0.75),
                SortedBuyVolumes = buy,
                SortedSellVolumes = sell
            });
        }

        var artifact = new BaselineArtifact
        {
            Instrument = instrument,
            TickSize = tickSize,
            SessionTimezone = _config.Session.Timezone,
            CalendarVersion = calendarVersion,
            ClockMode = _config.Timing.ClockMode,
            FeedMode = feedMode,
            WindowsMs = _config.Timing.WindowsMs.ToArray(),
            BucketMinutes = _config.Baseline.BucketMinutes,
            EligibleSessionIds = kept.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            FitEndUtc = fitEndUtc,
            SourceLogHashes = sourceLogHashes,
            Sha256 = "",              // stamped by BaselineArtifactIo.Save
            Buckets = buckets
        };

        return new BaselineBuildReport(artifact, kept.Count, used.Count, warnings);
    }
}
