using System;
using System.Collections.Generic;
using System.Threading;
using System.Reflection;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Features;
using OrbIx.Core.Telemetry;
using TradingPlatform.BusinessLayer;
using Qt = TradingPlatform.BusinessLayer;
using QtI = TradingPlatform.BusinessLayer.Integration;

namespace OrbIx.Quantower.Indicator;

/// <summary>
/// Which delta calculation a volume-analysis request should ask for.
///
/// Declared here rather than taken from the platform enum because the third choice —
/// asking the symbol what it wants — is not one of the platform's two values, and because
/// the request diverts to a different code path when the value disagrees with the symbol.
/// </summary>
public enum VolumeAnalysisDeltaChoice
{
    /// <summary>What the parameterless call always sent. Retained as the default so the
    /// new input changes nothing until it is deliberately moved.</summary>
    AggressorFlag,

    /// <summary>The platform's other value.</summary>
    TickDirection,

    /// <summary>Ask the symbol which it uses for volume analysis, and send that.</summary>
    MatchSymbol,
}

/// <summary>
/// Loads daily bars so an opening range can be graded.
///
/// The only place in the project that calls <c>Symbol.GetHistory</c>, which is a documented
/// member of the installed assembly (present in the shipped
/// <c>TradingPlatform.BusinessLayer.xml</c> in four overloads). Keeping it to one place means
/// the blocking call has one call site, off the paint path and off the market-data path.
///
/// §2 grades a range by its width against average daily range. Without daily history that
/// figure is zero, the range is ungradeable, and the panel reports <c>UNGRADED</c> — which is
/// what it was doing. This supplies the number; it does not invent one when history is
/// unavailable.
/// </summary>
public static class QuantowerHistoryLoader
{
    /// <summary>
    /// The outcome of a load, including why it produced nothing.
    /// </summary>
    /// <param name="Adr">Average daily range in price, or zero when it could not be measured.</param>
    /// <param name="BarsUsed">How many daily bars contributed.</param>
    /// <param name="Status">A sentence for the operator. Always populated.</param>
    /// <param name="Bars">
    /// The daily bars the load actually returned, oldest first.
    ///
    /// Carried rather than discarded because the average-daily-range calculation reads exactly
    /// these and uses only the high and the low — so prior-day high, low and close, and the
    /// prior week's extremes, were one field away from being available for as long as this
    /// loader has existed. Empty when nothing usable came back.
    /// </param>
    public readonly record struct DailyHistoryResult(
        double Adr, int BarsUsed, string Status, IReadOnlyList<DailySessionBar> Bars)
    {
        /// <summary>A load that produced nothing, with the reason.</summary>
        public static DailyHistoryResult Nothing(string status)
            => new(0d, 0, status, Array.Empty<DailySessionBar>());
    }

    /// <summary>
    /// Loads daily bars and returns the average daily range over <paramref name="adrPeriod"/>.
    ///
    /// Requests more calendar days than the period needs, because weekends and holidays are
    /// not trading days: asking for exactly fourteen days would reliably return ten bars.
    /// The multiple is deliberate headroom, not a magic number — <see cref="DailyRangeHistory"/>
    /// takes the most recent <paramref name="adrPeriod"/> usable bars from whatever arrives
    /// and reports how many it actually used.
    ///
    /// Never throws. A failure to load history is a reason to report UNGRADED, not a reason
    /// for the indicator to fail to start.
    /// </summary>
    /// <param name="symbol">The instrument. Must be resolved on a connection.</param>
    /// <param name="adrPeriod">Lookback in trading days, from <c>or.adrPeriod</c>.</param>
    /// <param name="asOfUtc">The instant to load up to.</param>
    public static DailyHistoryResult LoadAverageDailyRange(
        Qt.Symbol symbol, int adrPeriod, DateTime asOfUtc)
    {
        if (symbol is null)
            throw new ArgumentNullException(nameof(symbol));

        if (adrPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adrPeriod), adrPeriod, "The lookback must be positive.");
        }

        // Roughly seven calendar days per five trading days, plus a fortnight of slack for
        // holiday runs. Over-requesting costs one call; under-requesting silently shortens
        // the average.
        var calendarDays = (adrPeriod * 7 / 5) + 14;
        var from = asOfUtc.AddDays(-calendarDays);

        try
        {
            using var history = symbol.GetHistory(Period.DAY1, from, asOfUtc);

            if (history is null || history.Count == 0)
            {
                return DailyHistoryResult.Nothing(
                    $"No daily history returned for {symbol.Name}; ranges stay UNGRADED because "
                    + "there is no average daily range to grade them against.");
            }

            var bars = new List<DailyBar>(history.Count);
            var daily = new List<DailySessionBar>(history.Count);

            // SeekOriginHistory.Begin is stated rather than defaulted: the indexer's default
            // origin is End, where index 0 is the NEWEST bar and the sequence runs backwards.
            // DailyRangeHistory sorts by date and so survives either direction, but a loop
            // whose direction is implicit is one refactor away from averaging the wrong days.
            for (var i = 0; i < history.Count; i++)
            {
                if (history[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                    continue;

                var date = DateOnly.FromDateTime(bar.TimeLeft);

                bars.Add(new DailyBar(date, bar.High, bar.Low));
                daily.Add(new DailySessionBar(date, bar.High, bar.Low, bar.Close));
            }

            var adr = DailyRangeHistory.Average(bars, adrPeriod, out var used);

            if (used == 0)
            {
                return DailyHistoryResult.Nothing(
                    $"{history.Count} daily items returned for {symbol.Name} but none were usable "
                    + "bars; ranges stay UNGRADED.");
            }

            return new DailyHistoryResult(
                adr, used,
                $"Average daily range {adr:N2} over {used} of {adrPeriod} requested daily bars "
                + $"for {symbol.Name}.",
                daily);
        }
        catch (Exception ex)
        {
            // Broad by intention. History is a convenience the grade depends on, not a
            // precondition for running, and the failure must be visible rather than fatal —
            // the alternative is an indicator that refuses to draw because a data request
            // failed. The reason is reported verbatim so it can be acted on.
            return DailyHistoryResult.Nothing(
                $"Daily history for {symbol.Name} could not be loaded ({ex.GetType().Name}: "
                + $"{ex.Message}); ranges stay UNGRADED.");
        }
    }

    /// <summary>The outcome of a volume-analysis backfill, including why it produced nothing.</summary>
    /// <param name="Bars">Per-bar delta bars, oldest first. Empty when nothing usable came back.</param>
    /// <param name="Status">A sentence for the operator. Always populated.</param>
    public readonly record struct VolumeAnalysisBackfillResult(
        IReadOnlyList<DeltaBar> Bars, string Status)
    {
        /// <summary>A backfill that produced nothing, with the reason.</summary>
        public static VolumeAnalysisBackfillResult Nothing(string status)
            => new(Array.Empty<DeltaBar>(), status);
    }

    /// <summary>
    /// Builds the calculation parameters for a volume-analysis request.
    ///
    /// Every field is set explicitly, including the ones whose value matches the platform's
    /// own default. The defaults are three of the six gates that decide whether the
    /// vendor's precomputed analysis may be used, and leaving them implicit is how they
    /// stayed invisible while one of them may have been diverting every request we made.
    ///
    /// Never throws; a symbol that cannot answer falls back to the platform's own default
    /// value rather than to a guess of ours.
    /// </summary>
    public static VolumeAnalysisCalculationParameters BuildParameters(
        VolumeAnalysisDeltaChoice deltaChoice, bool forceTickData, Symbol? symbol)
    {
        var parameters = new VolumeAnalysisCalculationParameters
        {
            CalculatePriceLevels = true,
            ForceUsingTickData = forceTickData,
        };

        parameters.DeltaCalculationType = deltaChoice switch
        {
            VolumeAnalysisDeltaChoice.TickDirection => DeltaCalculationType.TickDirection,
            VolumeAnalysisDeltaChoice.MatchSymbol => ResolveSymbolDelta(symbol),
            _ => DeltaCalculationType.AggressorFlag,
        };

        return parameters;
    }

    private static DeltaCalculationType ResolveSymbolDelta(Symbol? symbol)
    {
        if (symbol is null)
            return DeltaCalculationType.AggressorFlag;

        try
        {
            return symbol.GetDeltaCalculationTypeForVolumeAnalysis();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return DeltaCalculationType.AggressorFlag;
        }
    }

    /// <summary>
    /// Observes what the platform declared about volume analysis for this chart's symbol,
    /// so the reason a profile found bars but no per-price levels can be NAMED rather than
    /// guessed at.
    ///
    /// WHY OBSERVE RATHER THAN INFER. The platform decides whether the vendor's precomputed
    /// analysis may be used from a set of gates whose INPUTS were all re-confirmed on
    /// v1.147.2. Five are readable here; the sixth is internal
    /// (<c>VolumeAnalysisMetadata.IsVolumeAnalysisAvailable</c>) and is read reflectively or
    /// reported unread. Nothing is concluded in this method — it reports, and
    /// <see cref="VolumeAnalysisCapability"/> judges.
    ///
    /// THAT THEY ARE EVALUATED IN A FIXED ORDER, SHORT-CIRCUITING ON THE FIRST FAILURE, IS
    /// UNVERIFIED ON v1.147.2 — it is carried from the v1.146.18 decompile. The ordering
    /// method was not re-located; see docs/RECONCILIATION.md. Nothing here depends on it:
    /// this reports which observable gates failed and never infers one from another.
    ///
    /// <c>Symbol.VolumeAnalysisMetadata</c> is marked <c>[NotPublished]</c> in the
    /// installed assembly: public, but outside the documented API surface. It is read
    /// defensively for that reason, and every failure degrades to a stated unknown.
    ///
    /// Never throws.
    /// </summary>
    public static VolumeAnalysisCapability ObserveCapability(
        HistoricalData bars, VolumeAnalysisCalculationParameters parameters,
        TimeSpan chartPeriodDuration)
    {
        var chartPeriod = chartPeriodDuration.ToString();

        var symbol = bars?.Symbol;

        if (symbol is null || parameters is null)
        {
            return new VolumeAnalysisCapability(
                MetadataPresent: false, AvailabilityUnread: true, Available: false,
                VolumeTypeIsVolume: false,
                RequestedDelta: parameters?.DeltaCalculationType.ToString() ?? "unknown",
                SymbolDelta: "unknown", VolumeFilterSet: false, ForceTickData: false,
                PeriodsWithLevels: Array.Empty<string>(),
                PeriodsWithoutLevels: Array.Empty<string>(),
                ChartPeriod: chartPeriod,
                ChartPeriodAllowsLevels: false,
                ChartPeriodAllowedWithoutLevels: false);
        }

        var requestedDelta = parameters.DeltaCalculationType.ToString();
        var symbolDelta = ReadSymbolDelta(symbol);
        var volumeIsVolume = symbol.VolumeType == SymbolVolumeType.Volume;

        // The request's filter mirrors the platform's own test: it diverts the vendor path
        // when the value is neither NaN nor zero.
        var filterSet = !double.IsNaN(parameters.FilteredVolume) && parameters.FilteredVolume != 0.0;

        var metadata = ReadMetadata(symbol);

        if (metadata is null)
        {
            return new VolumeAnalysisCapability(
                MetadataPresent: false, AvailabilityUnread: true, Available: false,
                VolumeTypeIsVolume: volumeIsVolume,
                RequestedDelta: requestedDelta, SymbolDelta: symbolDelta,
                VolumeFilterSet: filterSet, ForceTickData: parameters.ForceUsingTickData,
                PeriodsWithLevels: Array.Empty<string>(),
                PeriodsWithoutLevels: Array.Empty<string>(),
                ChartPeriod: chartPeriod,
                ChartPeriodAllowsLevels: false,
                ChartPeriodAllowedWithoutLevels: false);
        }

        var availabilityUnread = !TryReadAvailability(metadata, out var available);

        // MATCHED BY DURATION, never by name. Period.Duration is
        // `TimeSpan.FromTicks(Ticks)` (decompiled, re-confirmed on v1.147.2) and the
        // chart's period is derived from its own bar timestamps, so the
        // two are the same quantity in different clothes. Comparing their spellings would
        // report a mismatch that does not exist.
        var withLevels = ReadPeriods(
            metadata, includePriceLevels: true, chartPeriodDuration, out var levelsMatch);
        var withoutLevels = ReadPeriods(
            metadata, includePriceLevels: false, chartPeriodDuration, out var totalsMatch);

        return new VolumeAnalysisCapability(
            MetadataPresent: true,
            AvailabilityUnread: availabilityUnread,
            Available: available,
            VolumeTypeIsVolume: volumeIsVolume,
            RequestedDelta: requestedDelta,
            SymbolDelta: symbolDelta,
            VolumeFilterSet: filterSet,
            ForceTickData: parameters.ForceUsingTickData,
            PeriodsWithLevels: withLevels,
            PeriodsWithoutLevels: withoutLevels,
            ChartPeriod: chartPeriod,
            ChartPeriodAllowsLevels: levelsMatch,
            ChartPeriodAllowedWithoutLevels: totalsMatch,
            TickHistoryRule: ReadTickHistoryRule(symbol));
    }

    private static string ReadSymbolDelta(Symbol symbol)
    {
        try
        {
            return symbol.GetDeltaCalculationTypeForVolumeAnalysis().ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return "unknown";
        }
    }

    private static QtI.VolumeAnalysisMetadata? ReadMetadata(Symbol symbol)
    {
        try
        {
            return symbol.VolumeAnalysisMetadata;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>
    /// Quotes the platform's own volume-analysis entitlement rule for this symbol.
    ///
    /// WHY A QUOTE AND NOT A JUDGEMENT. `Rule.ALLOW_VOLUME_ANALYSIS_FROM_TICK_HISTORY` is
    /// the only one of the platform's twenty-eight published rules that bears on volume
    /// analysis, and `RulesManager.IsAllowed(string, Symbol)` is PUBLIC — so unlike the six
    /// gates this needs no reflection at all. What it returns is reported verbatim, status
    /// and reason, and nothing here decides what it means.
    ///
    /// IT IS NOT ESTABLISHED THAT THIS IS THE RULE THE PLATFORM'S OWN LICENCE PREDICATE
    /// CONSULTS. That predicate passes obfuscated string constants which were not decoded,
    /// so this is evidence about the account's entitlement rather than a reproduction of
    /// the platform's decision, and the wording downstream says exactly that.
    ///
    /// Never throws. An unreadable rule degrades to the EMPTY string, which reads
    /// downstream as "unread" and never as "refused" — the distinction the whole type is
    /// built on.
    /// </summary>
    private static string ReadTickHistoryRule(Qt.Symbol symbol)
    {
        try
        {
            var result = Qt.Core.Instance.RulesManager.IsAllowed(
                VolumeAnalysisCapability.TickHistoryRuleName, symbol);

            if (result is null)
                return string.Empty;

            var reason = result.Reason;

            return string.IsNullOrWhiteSpace(reason)
                ? result.Status.ToString()
                : $"{result.Status} ({reason})";
        }
        catch (Exception)
        {
            // UNREAD, not refused. Reporting a failure to ask as a negative answer is the
            // one mistake this probe must never make.
            return string.Empty;
        }
    }

    /// <summary>
    /// Quotes the platform's Level 2 rules for a symbol's connection.
    ///
    /// FOUR READS, NOT ONE, and deliberately: each rule is asked in BOTH its permission form
    /// and its value form, because a rule phrased as a fact may carry its answer as either
    /// and which one is not established. Reporting only the form that happened to return
    /// something would make the choice look settled.
    ///
    /// Never throws. Each read degrades to an empty string, which reads downstream as
    /// "unread" and never as an answer.
    /// </summary>
    public static Level2RuleReport ReadLevel2Rules(Qt.Symbol? symbol)
    {
        if (symbol is null)
            return Level2RuleReport.NoSymbol;

        var report = new Level2RuleReport(
            Allowed(Level2RuleReport.IsAggregatedRuleName, symbol),
            Value(Level2RuleReport.IsAggregatedRuleName, symbol),
            Allowed(Level2RuleReport.HasImpliedSizeRuleName, symbol),
            Value(Level2RuleReport.HasImpliedSizeRuleName, symbol),
            SymbolSeen: true);

        return report.AnyRead ? report : Level2RuleReport.NothingReturned;
    }

    private static string Allowed(string ruleName, Qt.Symbol symbol)
    {
        try
        {
            var result = Qt.Core.Instance.RulesManager.IsAllowed(ruleName, symbol);

            if (result is null)
                return string.Empty;

            return string.IsNullOrWhiteSpace(result.Reason)
                ? result.Status.ToString()
                : $"{result.Status} ({result.Reason})";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The value form. It takes a CONNECTION id rather than a symbol — there is no
    /// (string, Symbol) overload of GetStringValue on the installed assembly — which is
    /// also the right scope: whether a feed is aggregated is a property of the connection.
    /// </summary>
    private static string Value(string ruleName, Qt.Symbol symbol)
    {
        try
        {
            var connectionId = symbol.ConnectionId;

            if (string.IsNullOrWhiteSpace(connectionId))
                return string.Empty;

            var value = Qt.Core.Instance.RulesManager.GetStringValue(ruleName, connectionId);

            return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> ReadPeriods(
        QtI.VolumeAnalysisMetadata metadata, bool includePriceLevels,
        TimeSpan chartPeriodDuration, out bool chartPeriodAllowed)
    {
        chartPeriodAllowed = false;

        try
        {
            var periods = metadata.GetAllowedPeriods(includePriceLevels);

            if (periods is null || periods.Length == 0)
                return Array.Empty<string>();

            var names = new List<string>(periods.Length);

            foreach (var period in periods)
            {
                names.Add(period.ToString());

                if (period.Duration == chartPeriodDuration)
                    chartPeriodAllowed = true;
            }

            return names;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or NullReferenceException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Reads the platform's internal availability flag by name.
    ///
    /// IT IS INTERNAL, so there is no supported way to ask. Reflection reads it when it is
    /// there and the caller reports it UNREAD when it is not — an unknown, never a guess in
    /// either direction. A future version that renames or removes the property degrades to
    /// "unread"; it can never start returning a wrong answer.
    /// </summary>
    private static bool TryReadAvailability(QtI.VolumeAnalysisMetadata metadata, out bool available)
    {
        available = false;

        try
        {
            var property = metadata.GetType().GetProperty(
                "IsVolumeAnalysisAvailable",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property is null || property.PropertyType != typeof(bool))
                return false;

            if (property.GetValue(metadata) is not bool flag)
                return false;

            available = flag;
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MethodAccessException
                                      or AmbiguousMatchException or NotSupportedException
                                      or TypeLoadException)
        {
            return false;
        }
    }

    /// <summary>
    /// Requests the platform's volume-analysis calculation over the chart's
    /// own bars and converts each CLOSED bar's totals to a
    /// <see cref="DeltaBar"/> for <see cref="DeltaSeriesEngine.SeedHistorical"/>.
    ///
    /// The second sanctioned blocking call in this file, run from
    /// initialisation only — never the fold, never the paint path. Whether
    /// the calculation completes over the one data vendor connector is UNVERIFIED
    /// until the parity study reads the dump this feeds; the timeout is a
    /// cap on how long start-up will wait, not a claim about the platform.
    /// The state enum values (None=0, Processing=4, Finished=0x10) and
    /// <c>HistoryItem.VolumeAnalysisData</c> were read from the installed
    /// assembly and re-confirmed unchanged on v1.147.2. Never throws.
    /// </summary>
    public static VolumeAnalysisBackfillResult LoadVolumeAnalysis(
        HistoricalData bars, TimeSpan timeout, VolumeAnalysisCalculationParameters parameters)
    {
        if (bars is null || bars.Count < 2)
        {
            return VolumeAnalysisBackfillResult.Nothing(
                "history: none — chart holds no closed bars; delta since attach.");
        }

        if (parameters is null)
            throw new ArgumentNullException(nameof(parameters));

        try
        {
            // THE PARAMETERISED OVERLOAD, deliberately. The parameterless one is only a
            // wrapper — re-confirmed on v1.147.2 as literally
            // `CalculateProfile(h, new VolumeAnalysisCalculationParameters())` — and those
            // defaults feed three of the gates the platform uses to decide whether the
            // vendor's precomputed analysis may be used. Passing them explicitly is what
            // lets the operator change one and see what moves.
            using var progress = Qt.Core.Instance.VolumeAnalysis.CalculateProfile(bars, parameters);
            var deadline = DateTime.UtcNow + timeout;

            while (progress.State != VolumeAnalysisCalculationState.Finished
                   && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(200);
            }

            if (progress.State != VolumeAnalysisCalculationState.Finished)
            {
                return VolumeAnalysisBackfillResult.Nothing(
                    $"history: none — volume analysis still {progress.State} after "
                    + $"{timeout.TotalSeconds:N0}s; delta since attach.");
            }

            // Closed bars only: index Count-1 (Begin origin) is the forming bar.
            var seeded = new List<DeltaBar>(bars.Count - 1);
            var missing = 0;

            for (var i = 0; i < bars.Count - 1; i++)
            {
                if (bars[i, SeekOriginHistory.Begin] is not HistoryItemBar bar)
                    continue;

                var total = bar.VolumeAnalysisData?.Total;
                if (total is null)
                {
                    missing++;
                    continue;
                }

                // The platform reports classified buy/sell totals only, so
                // Unknowns is zero BY CONSTRUCTION here, not by measurement —
                // the parity study compares this against a feed that counts
                // its unknowns.
                seeded.Add(new DeltaBar(
                    bar.TimeLeft, bar.TimeRight,
                    total.Delta, total.Volume,
                    total.BuyTrades, total.SellTrades, Unknowns: 0,
                    bar.High, bar.Low, bar.Close,
                    DeltaSource.VolumeAnalysis));
            }

            if (seeded.Count == 0)
            {
                return VolumeAnalysisBackfillResult.Nothing(
                    $"history: none — volume analysis finished but no bar carried data "
                    + $"({missing} without totals); delta since attach.");
            }

            return new VolumeAnalysisBackfillResult(
                seeded,
                $"history: volume-analysis, {seeded.Count} bars seeded"
                + (missing > 0 ? $", {missing} without totals" : string.Empty) + ".");
        }
        catch (Exception ex)
        {
            return VolumeAnalysisBackfillResult.Nothing(
                $"history: none — volume analysis failed ({ex.GetType().Name}: "
                + $"{ex.Message}); delta since attach.");
        }
    }
    /// <summary>
    /// A tick-history load: the per-minute cache it produced, or the reason it produced none.
    /// </summary>
    /// <param name="Cache">Per-minute levels, sliceable by range. Never null.</param>
    /// <param name="TicksRead">Prints that contributed. Zero is a finding, not an error.</param>
    /// <param name="Status">A sentence for the operator. Always populated.</param>
    /// <param name="Served">
    /// Whether the connector serves tick history for this symbol AT ALL, established before
    /// the request rather than guessed from an empty answer. This is the whole reason the
    /// type exists — see <see cref="LoadTickLevels"/>.
    /// </param>
    public readonly record struct TickLevelsResult(
        ProfileTickCache Cache, int TicksRead, string Status, bool Served)
    {
        /// <summary>A load that produced no levels, with the reason and whether it could have.</summary>
        public static TickLevelsResult Nothing(string status, bool served, string sourceLabel)
            => new(
                new ProfileTickCache(
                    Array.Empty<ProfileMinuteLevels>(), default, default, served, status,
                    sourceLabel),
                0,
                status,
                served);
    }

    /// <summary>
    /// Whether this symbol's connection will serve tick history at all.
    ///
    /// PUBLIC BECAUSE TWO CALLERS MUST AGREE. The profile loader asks it before requesting, and
    /// the indicator asks it of every symbol the platform offers while choosing which
    /// connection to load from. Two copies of this test would eventually disagree, and the
    /// symptom would be a source chosen as capable and then refused by the loader.
    ///
    /// It replicates the platform's own internal gate. That gate is why asking anyway does not
    /// work: when the metadata does not list the aggregation, the platform returns an EMPTY
    /// list without contacting the vendor — measured at eight requests in 0.00 s total — so an
    /// unasked question and a refused one are indistinguishable downstream.
    /// </summary>
    /// <param name="symbol">The symbol to test. A null metadata reads as "does not serve".</param>
    /// <exception cref="ArgumentNullException">The symbol is null.</exception>
    public static bool ServesTickHistory(Qt.Symbol symbol)
    {
        if (symbol is null)
            throw new ArgumentNullException(nameof(symbol));

        var metadata = symbol.HistoryMetadata;

        if (metadata is null)
            return false;

        var aggregations = metadata.AllowedAggregations ?? Array.Empty<string>();
        var tickTypes = metadata.AllowedHistoryTypesHistoryAggregationTick
            ?? Array.Empty<Qt.HistoryType>();

        return Array.IndexOf(aggregations, Qt.HistoryAggregation.TICK) >= 0
            && Array.IndexOf(tickTypes, Qt.HistoryType.Last) >= 0;
    }

    /// <summary>
    /// Builds per-price volume for a time range from raw trade prints.
    ///
    /// WHY THIS EXISTS. The one data vendor connector does not attach per-price volume-analysis
    /// levels to historical bars: measured 2026-08-31, 210 of 210 in-range bars carried
    /// none, while the platform's own volume-analysis request covered 29 minutes of a range
    /// that began five and a half hours earlier. The profile was empty for want of a source,
    /// not for want of data — the same session's prints were on disk and in QuestDB the whole
    /// time. This asks the platform for the prints directly and aggregates them here.
    ///
    /// THE PRE-FLIGHT IS NOT OPTIONAL. <c>HistoricalData</c> exposes no status, no error and
    /// no exception: a vendor that refuses tick history returns an EMPTY LIST, identical in
    /// every observable way to a range that genuinely had no trades. The platform decides
    /// this internally by testing the symbol's own <c>HistoryMetadata</c> against the
    /// aggregation, and returns null — silently — when it fails. So the same test is made
    /// here, BEFORE the request, and its answer is carried in
    /// <see cref="TickLevelsResult.Served"/>. Without it, "the vendor will not serve this"
    /// and "there were no trades" would be one message again, which is precisely the class of
    /// defect the reference-price diagnostic was written to end.
    ///
    /// VOLUME MAY NOT MEAN CONTRACTS. When a symbol reports
    /// <see cref="Qt.SymbolVolumeType.Ticks"/> the platform's per-print volume is a trade
    /// COUNT, so the resulting profile is a trade-count profile. That is a different quantity
    /// from a contract-volume profile and it is stated rather than silently drawn as one.
    ///
    /// BLOCKING, AND THEREFORE INITIALISATION ONLY. <c>GetHistory</c> calls <c>Reload()</c>
    /// synchronously on the calling thread — never the fold, never the paint path, which is
    /// the same rule the volume-analysis backfill below already follows.
    /// </summary>
    /// <param name="symbol">The chart's symbol.</param>
    /// <param name="fromUtc">Start of the range, inclusive.</param>
    /// <param name="toUtc">End of the range, exclusive.</param>
    /// <param name="budget">
    /// How long the request may block before it is abandoned.
    ///
    /// BOUNDED BECAUSE THIS RUNS BEFORE THE CHART DRAWS. The very fault this whole change
    /// exists to fix was a start-up that waited on a vendor — one data vendor's own bar request was
    /// measured failing at 60.02s on 2026-08-31 — so an unbounded tick request here would
    /// have reintroduced it through a different door. The same shape, and the same reason, as
    /// the volume-analysis backfill's wait budget.
    /// </param>
    /// <param name="sourceLabel">
    /// Provenance for the chart — empty when this symbol is the chart's own, otherwise naming
    /// the connection being borrowed from. Carried into the cache so the profile can disclose
    /// it without the drawing code having to know how the source was chosen.
    /// </param>
    /// <exception cref="ArgumentNullException">The symbol or the label is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The budget is not positive.</exception>
    public static TickLevelsResult LoadTickLevels(
        Qt.Symbol symbol, DateTime fromUtc, DateTime toUtc, TimeSpan budget, string sourceLabel)
    {
        if (sourceLabel is null)
            throw new ArgumentNullException(nameof(sourceLabel));

        if (symbol is null)
            throw new ArgumentNullException(nameof(symbol));

        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget), budget, "A tick-history request needs a positive time budget.");
        }

        if (toUtc <= fromUtc)
        {
            return TickLevelsResult.Nothing(
                $"tick history: range is empty ({Stamp(fromUtc)} to {Stamp(toUtc)})", served: false, sourceLabel);
        }

        var metadata = symbol.HistoryMetadata;

        if (metadata is null)
        {
            return TickLevelsResult.Nothing(
                "tick history: the symbol publishes no history metadata, so what the connector "
                + "serves cannot be established", served: false, sourceLabel);
        }

        // ASK THE CAPABILITY ONCE. NO WAITING.
        //
        // An earlier build waited up to 90 s here for the metadata to change, on the theory
        // that an unpopulated list was a race. On one connection it sometimes is. On another connection/another connection
        // it never is: that connection reports available=False, periodsWithLevels=none and
        // AllowedAggregations=(Time) permanently, and the wait bought nothing but 91 seconds of
        // a blank chart — measured twice, 2026-09-01T01:35Z and 01:37Z.
        //
        // The race is now handled where it belongs: the CALLER retries symbol resolution while
        // the platform's symbol list is still filling, and hands this method a symbol already
        // known to serve tick history. By the time execution reaches here the answer is settled,
        // so a refusal is a fact about the connection rather than a question about timing.
        if (!ServesTickHistory(symbol))
        {
            return TickLevelsResult.Nothing(
                $"tick history: {symbol.Name} does not serve it — it lists "
                + $"{DescribeList(metadata.AllowedAggregations ?? Array.Empty<string>())}",
                served: false, sourceLabel);
        }

        // The connector publishes the largest span it will answer in ONE call, and on one data vendor
        // that is an hour — not the ten-day default. Measured 2026-08-31: a request for the
        // chart's whole span came back holding exactly 61 minutes, and the profile drawn from
        // it was labelled with the range's start while covering only its last hour. So the
        // range is walked in steps instead of clamped to one.
        var step = metadata.DownloadingStep_Tick;

        if (step <= TimeSpan.Zero)
        {
            return TickLevelsResult.Nothing(
                $"tick history: {symbol.Name} publishes no downloading step, so no request span "
                + "can be chosen", served: false, sourceLabel);
        }

        try
        {
            // Keyed by minute, then by price. A profile cannot be filtered by time once its
            // prints are summed into prices, and the ranges asked of it move — so the clock is
            // kept at the granularity the connector uses for bar volume analysis.
            var byMinute = new Dictionary<
                DateTime, Dictionary<double, (double Buy, double Sell, double Unclassified)>>(512);
            var printsPerMinute = new Dictionary<DateTime, int>(512);
            var tickSize = symbol.TickSize;
            var read = 0;
            var firstUtc = DateTime.MaxValue;
            var lastUtc = DateTime.MinValue;

            using var abandon = new CancellationTokenSource(budget);

            // NEWEST CHUNK FIRST — the platform's own order for this request, and not cosmetic.
            // If the budget runs out partway, what survives is the most RECENT part of the range.
            // A profile short at its old end is detectable by comparing covered start against
            // requested start; one short at its new end would silently omit the prints an
            // operator is most likely looking at.
            var chunkEnd = toUtc;
            var coveredFrom = toUtc;
            var chunks = 0;
            var exhausted = false;

            while (chunkEnd > fromUtc)
            {
                if (abandon.IsCancellationRequested)
                {
                    exhausted = true;
                    break;
                }

                var chunkStart = chunkEnd - step;
                if (chunkStart < fromUtc)
                    chunkStart = fromUtc;

                var request = new Qt.HistoryRequestParameters
                {
                    Symbol = symbol,
                    FromTime = chunkStart,
                    ToTime = chunkEnd,
                    Aggregation = new Qt.HistoryAggregationTick(Qt.HistoryType.Last),

                    // Stated, not defaulted. The default excludes out-of-session prints, which is
                    // right for a session profile and wrong to leave implicit.
                    ExcludeOutOfSession = true,

                    // The platform checks this between its own interval chunks and stops. It
                    // cannot interrupt a single stalled vendor call, so this is a ceiling on the
                    // work rather than a hard deadline — as the backfill's budget is too.
                    CancellationToken = abandon.Token,
                };

                using (var history = symbol.GetHistory(request))
                {
                    chunks++;

                    if (history is not null)
                    {
                    for (var i = 0; i < history.Count; i++)
                    {
                        if (history[i, Qt.SeekOriginHistory.Begin] is not Qt.HistoryItemLast print)
                            continue;

                        var price = print.Price;
                        var volume = print.Volume;

                        // Both default to NaN on this type, and every comparison against NaN is false —
                        // so a plain `volume > 0` would ADMIT it. The shared rule refuses it.
                        if (!PriceValue.IsUsable(price) || !PriceValue.IsUsable(volume))
                            continue;

                        if (tickSize > 0)
                            price = Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;

                        var stamp = print.TimeLeft;
                        if (stamp < firstUtc)
                            firstUtc = stamp;
                        if (stamp > lastUtc)
                            lastUtc = stamp;

                        var minute = new DateTime(
                            stamp.Year, stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, 0,
                            DateTimeKind.Utc);

                        if (!byMinute.TryGetValue(minute, out var prices))
                        {
                            prices = new Dictionary<double, (double Buy, double Sell, double Unclassified)>(64);
                            byMinute[minute] = prices;
                        }

                        prices.TryGetValue(price, out var slot);

                        switch (print.AggressorFlag)
                        {
                            case Qt.AggressorFlag.Buy:
                                slot.Buy += volume;
                                break;
                            case Qt.AggressorFlag.Sell:
                                slot.Sell += volume;
                                break;
                            default:
                                slot.Unclassified += volume;
                                break;
                        }

                        prices[price] = slot;
                        printsPerMinute.TryGetValue(minute, out var minutePrints);
                        printsPerMinute[minute] = minutePrints + 1;
                        read++;
                    }
                    }
                }

                // Advanced whether or not the chunk held anything: an empty hour inside a session
                // is ordinary, and treating it as the end of the data would stop the walk at the
                // first quiet stretch.
                coveredFrom = chunkStart;
                chunkEnd = chunkStart;
            }

            if (read == 0)
            {
                // Empty AND the metadata had said no: that combination is the only evidence this
                // API offers that a connector truly does not serve tick history, because the
                // request path itself reports nothing either way.
                // Capability was confirmed before any request was made, so this is a statement
                // about the RANGE — the connector would have answered and there was nothing to
                // answer with. Distinct from the readiness timeout above, which is about the
                // connector, and that distinction is the whole point of waiting first.
                return TickLevelsResult.Nothing(
                    $"tick history: {symbol.Name} returned no usable prints over {chunks} "
                    + $"request(s) for {Stamp(fromUtc)} to {Stamp(toUtc)}",
                    served: true, sourceLabel);
            }

            var minutes = new List<ProfileMinuteLevels>(byMinute.Count);

            foreach (var pair in byMinute)
            {
                var levels = new List<ProfileLevel>(pair.Value.Count);

                foreach (var level in pair.Value)
                {
                    levels.Add(new ProfileLevel(
                        level.Key, level.Value.Buy, level.Value.Sell, level.Value.Unclassified));
                }

                levels.Sort(static (a, b) => a.Price.CompareTo(b.Price));
                minutes.Add(new ProfileMinuteLevels(pair.Key, printsPerMinute[pair.Key], levels));
            }

            minutes.Sort(static (a, b) => a.MinuteUtc.CompareTo(b.MinuteUtc));

            // Volume is not always contracts. When the symbol reports a Ticks volume type the
            // platform's per-print volume is a trade COUNT, and a profile built from it is a
            // trade-count profile — a different quantity, said rather than silently drawn.
            var counted = symbol.VolumeType == Qt.SymbolVolumeType.Volume
                ? "contracts"
                : $"trade counts (symbol reports volume type {symbol.VolumeType})";

            // COVERAGE IS REPORTED SEPARATELY FROM CONTENT, because they are different facts and
            // conflating them is what drew a one-hour profile under a seven-hour label.
            var short_ = coveredFrom > fromUtc;

            var status =
                $"tick history: {read} print(s) over {minutes.Count} minute(s) in {chunks} "
                + $"request(s), {Stamp(firstUtc)} to {Stamp(lastUtc)}, volume in {counted}"
                + (short_
                    ? $"; COVERS FROM {Stamp(coveredFrom)}, short of the {Stamp(fromUtc)} asked "
                      + (exhausted ? "for (time budget spent)" : "for")
                    : string.Empty);

            return new TickLevelsResult(
                new ProfileTickCache(minutes, coveredFrom, toUtc, served: true, status,
                                     sourceLabel),
                read,
                status,
                Served: true);
        }
        catch (Exception ex)
        {
            // DELIBERATELY BROAD, for the same reason the daily loader's is: this runs during
            // initialisation, and a platform-side failure must degrade the profile to a stated
            // absence rather than take the whole indicator down with it.
            return TickLevelsResult.Nothing(
                $"tick history: request failed for {symbol.Name} ({ex.GetType().Name}: {ex.Message})",
                served: true, sourceLabel);
        }
    }

    /// <summary>Renders an instant for an operator-facing status line.</summary>
    private static string Stamp(DateTime utc) => LoadTiming.Stamp(utc);

    /// <summary>
    /// Renders what the connector DOES allow, so a refusal names the alternative.
    ///
    /// An empty list is its own finding — it means the connector published metadata and
    /// allowed nothing — so it says that rather than rendering as blank.
    /// </summary>
    private static string DescribeList<T>(IReadOnlyList<T> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);
}
