using System;
using System.Collections.Generic;
using OrbIx.Core.Config;

namespace OrbIx.Core.Sessions;

/// <summary>
/// Why an adaptive opening range stopped extending. Recorded because the three conditions
/// mean different things about the session, and a range that hit its time cap is a
/// different object from one that closed on participation.
/// </summary>
public enum OrCloseReason
{
    /// <summary>A fixed wall-clock length elapsed.</summary>
    FixedLength,

    /// <summary>Participation reached the configured multiple of median opening volume.</summary>
    VolumeThreshold,

    /// <summary>The hard time cap arrived before participation did.</summary>
    TimeCap,

    /// <summary>Range width reached the configured fraction of average daily range.</summary>
    RangeSpent,
}

/// <summary>
/// The immutable result of an opening range, emitted the instant the range closes.
/// Everything downstream references this rather than recomputing, so a signal and the HUD
/// that explains it can never disagree.
///
/// Carries every primitive in §2.
/// </summary>
public sealed record OrSnapshot
{
    public required string SessionName { get; init; }
    public required string SymbolRoot { get; init; }
    public required DateTime OpenUtc { get; init; }
    public required DateTime CloseUtc { get; init; }
    public required OrCloseReason CloseReason { get; init; }

    /// <summary>
    /// When the session this range belongs to stops trading.
    ///
    /// Carried so a consumer knows how long the range's levels remain relevant. An opening
    /// range defines levels for its own session; drawing them onward for a month turns a
    /// chart into a grid and buries the session that is actually live.
    /// </summary>
    public required DateTime SessionEndUtc { get; init; }

    /// <summary>Range high.</summary>
    public required double Orh { get; init; }

    /// <summary>Range low.</summary>
    public required double Orl { get; init; }

    /// <summary>
    /// Volume-weighted average price inside the range. Meaningful only when
    /// <see cref="FlowObserved"/> is true — read <see cref="TryVwap"/> instead of this
    /// directly unless the caller has already checked.
    /// </summary>
    public required double Vwap { get; init; }

    /// <summary>
    /// Highest-volume price in the range — the magnet. Requires <see cref="FlowObserved"/>;
    /// see <see cref="TryPoc"/>.
    /// </summary>
    public required double Poc { get; init; }

    /// <summary>
    /// Signed aggressor volume across the range. Requires <see cref="FlowObserved"/>; see
    /// <see cref="TryDelta"/>. Zero here is genuinely ambiguous — it is both "balanced" and
    /// "never measured" — which is precisely why the flag exists.
    /// </summary>
    public required double Delta { get; init; }

    /// <summary>
    /// Touches on the high edge before the range closed. Requires <see cref="FlowObserved"/>;
    /// see <see cref="TryHighTests"/>.
    /// </summary>
    public required int HighTests { get; init; }

    /// <summary>
    /// Touches on the low edge before the range closed. Requires <see cref="FlowObserved"/>;
    /// see <see cref="TryLowTests"/>.
    /// </summary>
    public required int LowTests { get; init; }

    /// <summary>Last trade inside the range.</summary>
    public required double Close { get; init; }

    /// <summary>Total traded volume inside the range.</summary>
    public required double Volume { get; init; }

    /// <summary>
    /// Average true range of the range's own sub-bars, in ticks. This is the stop-buffer
    /// basis: the noise scale of the session that actually just happened, rather than of
    /// some earlier one.
    /// </summary>
    public required double AtrTicks { get; init; }

    /// <summary>
    /// Mean depth imbalance across the range window, in [-1, +1]. Zero when the depth tier
    /// was absent — read alongside <see cref="BookObserved"/> before using it.
    /// </summary>
    public required double BookImbalance { get; init; }

    /// <summary>Whether any depth was actually seen while the range was building.</summary>
    public required bool BookObserved { get; init; }

    /// <summary>
    /// Whether this range was built from the actual trade flow, or seeded from bars.
    ///
    /// A range seeded from bar history knows its high, low, close and volume, because a bar
    /// carries those. It cannot know <see cref="Vwap"/>, <see cref="Poc"/>,
    /// <see cref="Delta"/>, <see cref="HighTests"/> or <see cref="LowTests"/>, because a bar
    /// carries no aggressor side and no intra-bar sequence.
    ///
    /// Those fields are therefore left at zero on a seeded range and this flag reads false.
    /// Zero delta is indistinguishable from balanced delta, so anything that consumes flow
    /// must check this first — which is what the <c>Try*</c> accessors below make
    /// unavoidable.
    /// </summary>
    public required bool FlowObserved { get; init; }

    /// <summary>Volume-weighted average price, or false when it was never measured.</summary>
    public bool TryVwap(out double value) => this.Flow(this.Vwap, out value);

    /// <summary>Point of control, or false when it was never measured.</summary>
    public bool TryPoc(out double value) => this.Flow(this.Poc, out value);

    /// <summary>Signed aggressor volume, or false when it was never measured.</summary>
    public bool TryDelta(out double value) => this.Flow(this.Delta, out value);

    /// <summary>High-edge touch count, or false when it was never measured.</summary>
    public bool TryHighTests(out int value)
    {
        value = this.FlowObserved ? this.HighTests : 0;
        return this.FlowObserved;
    }

    /// <summary>Low-edge touch count, or false when it was never measured.</summary>
    public bool TryLowTests(out int value)
    {
        value = this.FlowObserved ? this.LowTests : 0;
        return this.FlowObserved;
    }

    private bool Flow(double candidate, out double value)
    {
        value = this.FlowObserved ? candidate : 0d;
        return this.FlowObserved;
    }

    /// <summary>
    /// The interval each true-range sample in <see cref="AtrTicks"/> was measured over.
    ///
    /// A live range measures its noise on the configured sub-bar length; a range seeded from
    /// bars measures it on the chart's bar period. Those are different units, and an
    /// <see cref="AtrTicks"/> compared across the two without checking this would size a stop
    /// against the wrong noise scale. Zero when no true range was measured at all.
    /// </summary>
    public required TimeSpan AtrBasis { get; init; }

    /// <summary>Average daily range used to grade the width.</summary>
    public required double Adr { get; init; }

    public required double TickSize { get; init; }

    /// <summary>Range width in price.</summary>
    public double Width => this.Orh - this.Orl;

    /// <summary>Range width in ticks.</summary>
    public double WidthTicks => this.TickSize > 0 ? this.Width / this.TickSize : 0d;

    /// <summary>Range midpoint.</summary>
    public double Orm => (this.Orh + this.Orl) / 2d;

    /// <summary>
    /// Width as a fraction of average daily range — the day-type classifier. Zero when no
    /// average daily range was available, which <see cref="Grade"/> reports as
    /// <see cref="OrGrade.Normal"/> only because there is nothing to say otherwise.
    /// </summary>
    public double Orw => this.Adr > 0 ? this.Width / this.Adr : 0d;

    /// <summary>Where the close sits in the range: 0 at the low, 1 at the high.</summary>
    public double Skew => this.Width > 0 ? (this.Close - this.Orl) / this.Width : 0.5d;

    /// <summary>
    /// Whether the range width could be graded. A range with no average daily range behind
    /// it is ungraded, not Normal, and the distinction belongs in the journal.
    /// </summary>
    public bool Gradeable => this.Adr > 0;

    public required OrGrade Grade { get; init; }

    /// <summary>
    /// Projection multiples this range was built with, ascending. Carried on the snapshot
    /// rather than read from configuration at use, so a recorded range replays with the
    /// projections it actually had even if the configuration has since changed.
    /// </summary>
    public required EquatableArray<double> ExtensionMultiples { get; init; }

    /// <summary>
    /// Projection above the high at a multiple of the range width.
    /// </summary>
    public double ExtensionAbove(double multiple) => this.Orh + (this.Width * multiple);

    /// <summary>
    /// Projection below the low at a multiple of the range width.
    /// </summary>
    public double ExtensionBelow(double multiple) => this.Orl - (this.Width * multiple);

    /// <summary>
    /// The projections §2 names, above and below, at the multiples this range was built
    /// with.
    /// </summary>
    public IReadOnlyList<(double Multiple, double Above, double Below)> Extensions()
    {
        var result = new List<(double, double, double)>(this.ExtensionMultiples.Count);

        foreach (var m in this.ExtensionMultiples)
            result.Add((m, this.ExtensionAbove(m), this.ExtensionBelow(m)));

        return result;
    }

    /// <summary>
    /// Grades a width against the configured thresholds. Kept here so the builder and any
    /// re-grading in a replay cannot drift apart.
    /// </summary>
    public static OrGrade GradeFor(double orw, OrGradeConfig thresholds)
    {
        if (thresholds is null)
            throw new ArgumentNullException(nameof(thresholds));

        if (orw <= 0)
            return OrGrade.Normal;

        if (orw < thresholds.CompressedMaxOrw)
            return OrGrade.Compressed;

        return orw > thresholds.ExhaustedMinOrw ? OrGrade.Exhausted : OrGrade.Normal;
    }
}
