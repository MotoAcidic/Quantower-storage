using System;
using System.Collections.Generic;
using System.Linq;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;
using OrbIx.Core.Sessions;

namespace OrbIx.Core.Features;

/// <summary>
/// The overnight high and low — the extremes of the electronic session that precedes the day
/// session.
///
/// THESE LEVELS WERE PROMISED AND NEVER BUILT. `LevelKind.OvernightHigh` and `OvernightLow`
/// have existed since the level graph did, `config/orbix.default.json` weights them at 0.85 —
/// just below prior-day high and low — and `ChartOverlay` has a colour for them. Nothing
/// anywhere constructed one. The configuration described a level the engine did not compute,
/// which is worse than an absent feature: a person reading the config would reasonably conclude
/// the overnight range was active and weighted.
///
/// THE WINDOW IS CONFIGURED, NOT INFERRED. Which session starts the overnight and which one
/// ends it is a judgement — 18:00 to 09:30 is conventional for index futures, and gold's day
/// begins at the COMEX open instead — so it is written down in `levels.overnight` rather than
/// guessed from session times. A rule that infers "the first session after some hour" would be
/// an invented boundary wearing the appearance of a derivation.
/// </summary>
public static class OvernightRange
{
    /// <summary>
    /// The overnight window for one product on one session date, in UTC.
    ///
    /// Starts at the configured start session's open on the PREVIOUS local date and ends at the
    /// configured end session's open on <paramref name="localDate"/>. Returns false when either
    /// session is not configured, not enabled, or does not apply to this product — a window
    /// that cannot be located yields no levels rather than a guessed boundary.
    /// </summary>
    public static bool TryWindow(
        SessionClock clock,
        OvernightConfig config,
        DateOnly localDate,
        string symbolRoot,
        out DateTime fromUtc,
        out DateTime toUtc)
    {
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (config is null) throw new ArgumentNullException(nameof(config));

        fromUtc = default;
        toUtc = default;

        var start = Find(clock, config.StartSession, symbolRoot);
        var end = Find(clock, EndSessionFor(config, symbolRoot), symbolRoot);

        if (start is null || end is null)
            return false;

        fromUtc = clock.OpenInstantUtc(start, localDate.AddDays(-1));
        toUtc = clock.OpenInstantUtc(end, localDate);

        // A window that does not run forwards describes nothing. It cannot happen with the
        // shipped configuration, and if configuration ever makes it happen the answer is no
        // levels rather than an inverted range.
        return toUtc > fromUtc;
    }

    /// <summary>
    /// Which session ends the overnight for this product.
    ///
    /// Gold's day begins at the COMEX open, not at the equity open, so the end is overridable
    /// per contract root. The override is matched against the same root the level graph and the
    /// product lookup use.
    /// </summary>
    public static string EndSessionFor(OvernightConfig config, string symbolRoot)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        if (!string.IsNullOrWhiteSpace(symbolRoot)
            && config.EndSessionByRoot.TryGetValue(symbolRoot, out var overridden)
            && !string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        return config.EndSession;
    }

    /// <summary>
    /// The overnight high and low from bars, or nothing at all.
    /// </summary>
    /// <param name="bars">Bars in any order. Only those wholly inside the window count.</param>
    /// <param name="fromUtc">Window start, inclusive.</param>
    /// <param name="toUtc">Window end, exclusive.</param>
    /// <param name="establishedUtc">
    /// When the levels came into being — the window's end. Age decay measures from here, so
    /// stamping them with "now" would make them permanently young and permanently strong.
    /// </param>
    public static IReadOnlyList<Level> FromBars(
        IEnumerable<BarEvent> bars, DateTime fromUtc, DateTime toUtc, DateTime establishedUtc)
    {
        if (bars is null)
            throw new ArgumentNullException(nameof(bars));

        if (toUtc <= fromUtc)
            return Array.Empty<Level>();

        var high = double.NegativeInfinity;
        var low = double.PositiveInfinity;
        var seen = 0;

        foreach (var bar in bars)
        {
            // A bar is IN the window when it opened at or after the start and before the end.
            // Judged on the open alone: a bar straddling the boundary belongs to the session it
            // began in, which is how every other window in this engine treats one.
            if (bar.OpenTimeUtc < fromUtc || bar.OpenTimeUtc >= toUtc)
                continue;

            if (!IsUsable(bar))
                continue;

            seen++;

            if (bar.High > high) high = bar.High;
            if (bar.Low < low) low = bar.Low;
        }

        // NOTHING IS INVENTED WHERE THE WINDOW IS EMPTY. No bars means no overnight range —
        // never a zero, which on a chart is a line at the bottom of the axis that looks like a
        // real price, and never the current price standing in for one.
        if (seen == 0 || double.IsInfinity(high) || double.IsInfinity(low) || high < low)
            return Array.Empty<Level>();

        return new[]
        {
            new Level(high, LevelKind.OvernightHigh, establishedUtc, "ONH"),
            new Level(low, LevelKind.OvernightLow, establishedUtc, "ONL"),
        };
    }

    /// <summary>
    /// A bar whose prices are real and ordered.
    ///
    /// A high below its low is not an odd bar, it is a bar that did not arrive correctly, and
    /// letting it set an extreme would put a fictional price on the chart.
    /// </summary>
    private static bool IsUsable(BarEvent bar)
        => PriceValue.IsUsable(bar.High)
           && PriceValue.IsUsable(bar.Low)
           && bar.High >= bar.Low;

    private static SessionDefinition? Find(SessionClock clock, string name, string symbolRoot)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : clock.Definitions.FirstOrDefault(
                d => d.Enabled
                     && d.AppliesTo(symbolRoot)
                     && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}
