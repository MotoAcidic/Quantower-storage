namespace OrbIx.Core.Abstractions;

/// <summary>
/// Whether a number the platform handed us can be used as a price.
///
/// THIS EXISTS BECAUSE THE SAME TRAP HAS NOW BEEN FOUND THREE TIMES. Every comparison against
/// <see cref="double.NaN"/> is false, so the natural-looking rejection `value &lt;= 0` ADMITS
/// NaN rather than refusing it:
///
///   * OrBuilder guarded its book totals with `total &lt;= 0`; one NaN-sized sentinel turned the
///     opening range's imbalance into NaN permanently.
///   * FootprintEngine carried the same shape against a print's size.
///   * ReadInstrument filtered its reference prices with `candidate &lt;= 0`. OBSERVED
///     2026-08-22 on a live MNQ chart: `symbol.Last` reads NaN — not zero, as that method's
///     own comment assumed — so NaN passed the filter and reached GetTickCost, where only an
///     exception handler stopped it. An exception was doing a guard's job.
///
/// The rule lives in Core and is a pure function so the offline suite asserts it, rather than
/// each call site hand-rolling the predicate and one of them getting it wrong again. The
/// indicator targets net10.0-windows and the suite runs on Linux, so a rule that stays in the
/// indicator is a rule nothing checks.
/// </summary>
public static class PriceValue
{
    /// <summary>
    /// A price that arithmetic can be done on: a real number, strictly positive.
    ///
    /// WRITTEN AS A POSITIVE TEST, deliberately. `!(value &lt;= 0)` would be the same mistake
    /// again — it is true for NaN. Asking whether the value IS greater than zero is false for
    /// NaN, which is the answer wanted.
    ///
    /// Zero is not a price on any instrument traded here; it is what the platform reports when
    /// it has nothing to report, and treating it as a price is how a tick cost of zero gets
    /// computed from a market that is merely closed.
    ///
    /// The infinities are excluded separately: positive infinity IS greater than zero, so the
    /// positive test alone would let it through.
    /// </summary>
    public static bool IsUsable(double value)
        => value > 0 && !double.IsInfinity(value);
}
