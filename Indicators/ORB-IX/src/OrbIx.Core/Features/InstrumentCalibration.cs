using System;
using System.Collections.Generic;
using System.Globalization;
using OrbIx.Core.Flow;

namespace OrbIx.Core.Features;

/// <summary>
/// What a volume floor measured on THIS instrument turned out to be, and what it buys.
/// </summary>
/// <param name="MinVolume">Contracts the dominant cell must carry.</param>
/// <param name="MarksPerSession">Stacks per session the floor produced on the sample.</param>
/// <param name="Marks">Raw stacks found — the count the rate was extrapolated from.</param>
/// <param name="Bars">Bars the sample covered.</param>
/// <param name="Diagonals">Judged diagonals behind the distribution.</param>
/// <param name="Measured">
/// False when the tape was too thin to measure. The caller then uses whatever it would have
/// used anyway — this type never invents a number to avoid saying it could not.
/// </param>
/// <param name="Explanation">
/// What was measured and on how much, in words, for the status line. Carries the refusal
/// reason when <paramref name="Measured"/> is false.
/// </param>
public readonly record struct InstrumentFloor(
    double MinVolume,
    double MarksPerSession,
    int Marks,
    int Bars,
    int Diagonals,
    bool Measured,
    string Explanation);

/// <summary>
/// Measures an instrument's own volume floor from its own tape, instead of carrying one
/// measured on a different contract.
///
/// THE PROBLEM THIS EXISTS FOR. The floors this indicator shipped with — 45, 40 and 30 by bar
/// period — were measured on 37 sessions of ONE contract, and the document that records them
/// says so in its own title: "MNQ only. Every number is MNQU6. Another product has its own
/// volume scale and needs its own sweep; none of this should be carried across by analogy."
/// That instruction was followed inside this repository and broken the moment the indicator
/// was handed to anybody else, because the number travels and the caveat does not. A reader on
/// another contract sees a blank display or a solid one and has no way to tell which, since
/// the floor looks authoritative and carries no provenance at the point of use.
///
/// THE CANDIDATES COME FROM THE DATA, NOT FROM A LIST. Trying a fixed ladder of floors —
/// 20, 25, 30 … — assumes the answer's ORDER OF MAGNITUDE, which is exactly the assumption
/// that does not survive a change of instrument. A contract trading in hundreds per level and
/// one trading in single digits need candidate sets that differ by a factor of a hundred. So
/// the candidates are PERCENTILES of this instrument's own dominant-cell distribution: on any
/// tape, p50 through p99.9 spans the range where a useful floor can possibly sit, and nothing
/// outside it needs trying.
///
/// IT CALIBRATES FROM THE BARS THE CHART ACTUALLY MADE, WHICH IS WHAT MAKES IT WORK ANYWHERE.
/// An earlier version folded per-minute volume out of the profile cache onto a bar period, and
/// that quietly could not serve most charts: the cache's granularity is a MINUTE, so a
/// thirty-second chart received one bar per minute and every rate came out half of what it
/// should be, while tick, volume and Renko charts have no bar period to fold onto at all. This
/// takes the closed footprint bars the builder already holds — seeded from tick history and
/// accumulated live — so whatever the chart aggregates by, the calibration measures that.
///
/// THE SAMPLE'S LENGTH COMES FROM THE BARS' OWN CLOCKS. Every bar carries the instant it opened
/// and the instant it closed, whatever produced it, so the span a sample covers is a fact about
/// the bars rather than an assumption about the period. That is the whole reason a rate can be
/// quoted per session on a chart that has no period.
///
/// THE SCAN IS THE SHIPPED ONE. This calls <see cref="StackedImbalanceScan"/> and
/// <see cref="ImbalanceRule"/> — the same code the chart draws from. A calibration that
/// measured a reimplementation of the rule would be calibrating something the chart does not
/// run, which is the mistake this repository already made once with a duplicated diagonal
/// rule and deleted.
///
/// IT MEASURES FREQUENCY AND NOTHING ELSE. How often a display speaks is not whether what it
/// says is worth acting on. Stacked imbalance and absorption are measured NULLS on the
/// instrument they were developed against (trial 008, 25,745 episodes, below the cost floor).
/// Calibrating the rate changes neither fact and is not evidence about any other contract.
/// </summary>
public static class InstrumentCalibration
{
    /// <summary>
    /// Bars the sample must cover before a floor is measured rather than borrowed.
    ///
    /// AN HOUR OF ONE-MINUTE BARS, and the reason is the rate rather than the bars. A floor is
    /// chosen by the marks-per-session it produces, and that rate is extrapolated from the
    /// marks actually seen; extrapolating a session's worth of behaviour from a handful of
    /// minutes produces a confident number standing on nothing. Sixty bars is the point below
    /// which this refuses outright — above it, <see cref="InstrumentFloor.Marks"/> carries the
    /// raw count so a reader can see how thin the estimate is rather than being told only the
    /// conclusion.
    /// </summary>
    public const int MinimumBars = 60;

    /// <summary>
    /// Marks below which the rate is reported as thin in the explanation.
    ///
    /// Not a refusal: a floor that genuinely produces four marks a session is a correct answer
    /// about a quiet instrument, and refusing it would hide that. But four marks measured is a
    /// noisy basis for a rate, and the line says so rather than presenting it as settled.
    /// </summary>
    public const int ThinMarks = 10;

    /// <summary>
    /// The percentiles of the dominant-cell distribution tried as candidate floors.
    ///
    /// THE RANGE STARTS NEAR THE BOTTOM, AND AN EARLIER VERSION STARTING AT THE MEDIAN WAS
    /// MEASURABLY WRONG. The reasoning for starting high was that a floor at the median
    /// "admits half of every diagonal and marks almost every bar" — which is false, because a
    /// STACK requires three CONSECUTIVE imbalanced rows and that stays rare however low the
    /// floor goes. Measured on a 15-minute MNQ chart: at the median the scan produced four
    /// marks in eighty bars, so a target of nine per session was unreachable — not because of
    /// the tape but because the search could not look below p50. Asking for an impossible
    /// hundred per session returned the same floor, which is what proved the range was the
    /// binding constraint rather than the data.
    ///
    /// Still weighted towards the top, where usable floors mostly sit on short periods. Extra
    /// candidates cost one scan each and cannot make the answer worse: selection is by nearest
    /// achieved rate, so a candidate that misses is simply not chosen.
    /// </summary>
    /// <summary>
    /// The quantiles tried, exposed so the RANGE is assertable.
    ///
    /// Public because the range is part of what this type promises: a search that cannot look
    /// below the median cannot serve a chart whose target needs a low floor, and that failure
    /// is invisible in the result — it returns a real percentile of a real distribution and
    /// simply falls short of the target. Measured once on a 15-minute MNQ chart before the
    /// range was widened; asserting the range is what keeps it from narrowing again.
    /// </summary>
    public static IReadOnlyList<double> CandidateQuantiles => Candidates;

    private static readonly double[] Candidates =
    {
        0.02, 0.05, 0.10, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.45,
        0.50, 0.60, 0.70, 0.75, 0.80, 0.85, 0.88, 0.90, 0.92, 0.94,
        0.95, 0.96, 0.97, 0.975, 0.98, 0.985, 0.99, 0.993, 0.996, 0.999,
    };

    /// <summary>
    /// Measures the floor that comes nearest a target rate on this instrument's own tape.
    /// </summary>
    /// <param name="bars">
    /// The chart's own closed footprint bars, oldest first. Time bars, tick bars, volume bars
    /// or Renko — whatever the chart aggregates by is what gets measured.
    /// </param>
    /// <param name="tickSize">The instrument's price grid.</param>
    /// <param name="sessionLength">
    /// How long a session runs. REQUIRED rather than assumed: "per session" means nothing
    /// without it, and a venue's session is not six and a half hours everywhere.
    /// </param>
    /// <param name="ratio">The imbalance ratio, unchanged by calibration.</param>
    /// <param name="minLevels">Consecutive rows required for a stack, unchanged.</param>
    /// <param name="ignoreZero">Whether an empty opposing side is left unjudged.</param>
    /// <param name="targetMarksPerSession">How often the display should speak.</param>
    public static InstrumentFloor Measure(
        IReadOnlyList<FootprintBar> bars,
        double tickSize,
        TimeSpan sessionLength,
        double ratio,
        int minLevels,
        bool ignoreZero,
        double targetMarksPerSession)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (!(tickSize > 0) || double.IsInfinity(tickSize))
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "A price grid is required.");

        if (sessionLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionLength), sessionLength,
                "A session length is required: a rate per session cannot be computed without one.");
        }

        if (!(targetMarksPerSession > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetMarksPerSession), targetMarksPerSession,
                "A target of zero marks asks for a display that never speaks.");
        }

        if (bars.Count < MinimumBars)
        {
            return Unmeasured(
                bars.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"not calibrated: {bars.Count} bar(s) of tape, {MinimumBars} needed"));
        }

        var dominants = Dominants(bars, tickSize, ratio, ignoreZero);

        if (dominants.Count == 0)
        {
            return Unmeasured(
                bars.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"not calibrated: {bars.Count} bar(s) carried no judgeable diagonal"));
        }

        dominants.Sort();

        // HOW LONG THE SAMPLE ACTUALLY COVERS, taken from the bars' own clocks rather than
        // from a period nobody supplied. This is what lets a rate be quoted per session on a
        // tick or Renko chart, where there is no period to multiply by.
        var span = bars[^1].CloseUtc - bars[0].OpenUtc;

        if (span <= TimeSpan.Zero)
        {
            return Unmeasured(
                bars.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"not calibrated: {bars.Count} bar(s) span no time, so no rate per session"));
        }

        var sessionsCovered = span.TotalSeconds / sessionLength.TotalSeconds;

        var best = double.NaN;
        var bestMarks = 0;
        var bestRate = 0d;
        var bestDistance = double.MaxValue;

        foreach (var quantile in Candidates)
        {
            var candidate = Percentile(dominants, quantile);

            // Percentiles collide on a coarse distribution; trying the same floor twice would
            // cost a full sweep to reach the same answer.
            //
            // THIS SKIP IS AN OPTIMISATION AND NOTHING ELSE, which is worth saying because a
            // mutation removing it survives the suite and will keep surviving it. The comparison
            // below is a STRICT `<`, so a repeat of the current best scores an identical
            // distance and cannot displace it: the result is the same either way. It is an
            // equivalent mutant, not a hole in the tests, and no assertion should be invented
            // to kill it.
            if (candidate <= 0 || candidate.Equals(best))
                continue;

            var marks = MarksAt(bars, tickSize, ratio, candidate, ignoreZero, minLevels);

            // sessionsCovered is a double, which is not an accident. An earlier form divided
            // an int by an int: integer division truncated EVERY rate to zero, every candidate
            // scored identically, the first won by default, and the chosen floor was a real
            // percentile of a real distribution selected by nothing at all. It looked entirely
            // plausible. Only running it against an instrument whose floor was already known
            // exposed it.
            var rate = marks / sessionsCovered;
            var distance = Math.Abs(rate - targetMarksPerSession);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
                bestMarks = marks;
                bestRate = rate;
            }
        }

        if (double.IsNaN(best))
        {
            return Unmeasured(
                bars.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"not calibrated: no candidate floor could be formed from {dominants.Count:N0} diagonal(s)"));
        }

        var thin = bestMarks < ThinMarks
            ? string.Create(CultureInfo.InvariantCulture, $", THIN — only {bestMarks} mark(s) seen")
            : string.Empty;

        return new InstrumentFloor(
            best,
            bestRate,
            bestMarks,
            bars.Count,
            dominants.Count,
            Measured: true,
            string.Create(
                CultureInfo.InvariantCulture,
                $"min volume {best:N0}, calibrated on this instrument: {bestRate:N1}/session "
                + $"from {bestMarks} mark(s) over {bars.Count:N0} bar(s), "
                + $"{dominants.Count:N0} diagonal(s){thin}"));
    }

    private static InstrumentFloor Unmeasured(int bars, string why)
        => new(0d, 0d, 0, bars, 0, Measured: false, why);

    /// <summary>Every judged diagonal's dominant-cell volume, across the sample.</summary>
    private static List<double> Dominants(
        IReadOnlyList<FootprintBar> bars, double tickSize, double ratio, bool ignoreZero)
    {
        var dominants = new List<double>();

        // THE FLOOR PASSED HERE IS IRRELEVANT TO WHAT IS READ, and the first version of this
        // comment claimed otherwise. It said a floor of 1 was needed so the rule "judges
        // everything it can", on the belief that a higher floor would filter the distribution
        // before the floor was chosen. It does not: DiagonalVerdict carries Dominant whatever
        // the verdict, so an UNJUDGED diagonal still reports its volume. A mutation raising
        // this to 1000 changed nothing and survived the suite, which is how the belief was
        // caught. One is passed because the parameter is required to be positive.
        var semantics = ImbalanceSemantics.Atas with { IgnoreZeroOpposing = ignoreZero };

        foreach (var bar in bars)
        {
            foreach (var level in ImbalanceRule.Evaluate(bar.ToPriceCells(), tickSize, ratio, 1d, semantics))
            {
                if (level.BuySide.Dominant > 0)
                    dominants.Add(level.BuySide.Dominant);

                if (level.SellSide.Dominant > 0)
                    dominants.Add(level.SellSide.Dominant);
            }
        }

        return dominants;
    }

    /// <summary>Stacks the shipped scan finds across the sample at one floor.</summary>
    private static int MarksAt(
        IReadOnlyList<FootprintBar> bars, double tickSize, double ratio,
        double minVolume, bool ignoreZero, int minLevels)
    {
        var settings = new ImbalanceSettings(ratio, minVolume, ignoreZero, minLevels);
        var marks = 0;

        foreach (var bar in bars)
            marks += StackedImbalanceScan.Scan(bar, settings, LevelFeature.StackedImbalance).Count;

        return marks;
    }

    /// <summary>
    /// The value at a quantile of a sorted list, by nearest rank.
    ///
    /// No interpolation: the values are contract counts, and a floor of "37.4 contracts" is a
    /// number no diagonal can carry. Nearest rank returns a volume that was actually observed.
    /// </summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double quantile)
    {
        if (sorted.Count == 0)
            return 0d;

        var index = (int)Math.Ceiling(quantile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
