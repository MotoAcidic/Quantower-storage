using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>Which side aggressed on a print, as far as the feed could tell.</summary>
/// <summary>
/// One price with its volume already split by side — the shape every profile source
/// produces before the engine sees it.
///
/// WHY IT IS A TYPE AND NOT A TUPLE. Two sources now build these: the chart's own bars,
/// whose per-price levels the connector may or may not populate, and a tick-history load
/// that aggregates raw prints. They must agree exactly, because the engine cannot tell them
/// apart afterwards and a profile that quietly counted one source differently would be
/// wrong in a way nothing downstream could detect.
/// </summary>
/// <param name="Price">The traded price, on the instrument's tick grid.</param>
/// <param name="Buy">Volume where the buyer aggressed.</param>
/// <param name="Sell">Volume where the seller aggressed.</param>
/// <param name="Unclassified">Volume the feed did not classify. It still counts.</param>
public readonly record struct ProfileLevel(
    double Price, double Buy, double Sell, double Unclassified)
{
    /// <summary>Total volume at this price, however it was classified.</summary>
    public double Volume => this.Buy + this.Sell + this.Unclassified;
}

public enum PrintSide
{
    /// <summary>The feed did not classify the print. Its volume still counts.</summary>
    Unknown,

    /// <summary>The buyer aggressed.</summary>
    Buy,

    /// <summary>The seller aggressed.</summary>
    Sell,
}

/// <summary>
/// One price row of a volume profile. <see cref="Price"/> is the BOTTOM edge of the
/// row on the tick grid — display edges (top, centre) are derived by the renderer
/// from the engine's row height, never stored twice.
/// </summary>
public readonly record struct ProfileRow(
    double Price, double BuyVolume, double SellVolume, double UnclassifiedVolume)
{
    /// <summary>All volume in the row. Unclassified prints count — volume needs no aggressor.</summary>
    public double TotalVolume => this.BuyVolume + this.SellVolume + this.UnclassifiedVolume;

    /// <summary>Classified buy minus classified sell. Unclassified volume contributes nothing.</summary>
    public double Delta => this.BuyVolume - this.SellVolume;
}

/// <summary>
/// A computed profile: the contiguous row ladder, the point of control, and the
/// value area boundaries as INDICES into <see cref="Rows"/>.
/// </summary>
/// <param name="Rows">Contiguous ladder, ascending by price, including zero-volume rows.</param>
/// <param name="PocIndex">The row with the highest total volume (ratified tie rule on the engine).</param>
/// <param name="ValueAreaLowIndex">Lowest row included in the value area.</param>
/// <param name="ValueAreaHighIndex">Highest row included in the value area.</param>
/// <param name="TotalVolume">Volume across every row.</param>
/// <param name="ClassifiedFraction">Share of volume that carried an aggressor, in [0,1].</param>
/// <param name="PocTieBroken">
/// True when another row shares the CHOSEN POC's volume — that is, when the
/// ratified tie convention actually decided between equals.
///
/// It is computed from the FINAL POC rather than during selection. Until
/// 2026-08-28 it was set inside the selection loop's equality branch, which made
/// it true for any transient tie against the running best even when a strictly
/// greater row later won outright; the Python reference carried the identical
/// error, so the two agreed and the cross-check proved nothing here.
/// </param>
public sealed record VolumeProfileResult(
    ProfileRow[] Rows,
    int PocIndex,
    int ValueAreaLowIndex,
    int ValueAreaHighIndex,
    double TotalVolume,
    double ClassifiedFraction,
    bool PocTieBroken)
{
    /// <summary>Bottom-edge price of the point-of-control row.</summary>
    public double PocPrice => this.Rows[this.PocIndex].Price;

    /// <summary>Bottom-edge price of the lowest value-area row.</summary>
    public double ValPrice => this.Rows[this.ValueAreaLowIndex].Price;

    /// <summary>Bottom-edge price of the highest value-area row.</summary>
    public double VahPrice => this.Rows[this.ValueAreaHighIndex].Price;
}

/// <summary>
/// Volume profile aggregation and value-area computation, shared by the fixed-range
/// and anchored profiles. Prints in, rows and boundaries out; no platform types.
///
/// ROW BUCKETING is integer arithmetic on the tick grid: a price becomes a tick
/// index by rounding against the tick size, and tick indices are floor-divided by
/// the row size. Two prices one tick apart can never land in the same row twice
/// through float drift, because no float division survives past the first step.
///
/// THE VALUE AREA is the construction TradingView documents for its Volume Profile
/// (support article 43000502040, fetched and quoted 2026-08-28 — the raw page, not
/// a summary): start at the POC and include it first; then repeatedly compare the
/// next row above against the next row below and take the LARGER; on equal volumes
/// take the row CLOSER to the POC; at equal distance take the row ABOVE; a row
/// whose volume would exceed the remaining target STOPS the walk — the value area
/// holds at most the target, never more. That stop rule means "70%" is a ceiling,
/// not a promise; a large enough POC is a value area on its own.
///
/// RATIFIED CONVENTION (the one thing the vendor page does not state): when two
/// rows tie for the maximum volume, the POC is the row closer to the MIDDLE of the
/// ladder, and at equal distance the row ABOVE — mirroring the flavour of the
/// documented value-area tie rule.
///
/// Still a CHOICE rather than a fact — the vendor page is silent and that silence
/// has not gone away — but it is no longer an open question: ratified by the
/// operator 2026-08-28 after the behaviour was executed and checked against hand
/// arithmetic. Results carry <see cref="VolumeProfileResult.PocTieBroken"/> so a
/// reader can still see the day it decided.
///
/// DISPLAY ONLY, stated by every consumer: profile-derived levels measured
/// non-predictive here (levels-null, 572 candidates over 31 sessions).
/// </summary>
public sealed class VolumeProfileEngine
{
    /// <summary>
    /// Widest ladder the engine will materialise. A corrupt print miles off-market
    /// would otherwise allocate a row for every empty tick between it and the real
    /// range; past this bound the compute refuses with the span in the message.
    /// </summary>
    public const int MaxRows = 4096;

    private readonly Dictionary<long, RowAccumulator> rows = new();
    private readonly double tickSize;
    private readonly int rowSizeTicks;
    private long minRowIndex = long.MaxValue;
    private long maxRowIndex = long.MinValue;

    public VolumeProfileEngine(double tickSize, int rowSizeTicks)
    {
        if (!(tickSize > 0) || double.IsInfinity(tickSize))
            throw new ArgumentOutOfRangeException(nameof(tickSize), tickSize, "A positive finite tick size is required.");
        if (rowSizeTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(rowSizeTicks), rowSizeTicks, "At least one tick per row is required.");

        this.tickSize = tickSize;
        this.rowSizeTicks = rowSizeTicks;
    }

    /// <summary>Rows that received at least one print. Zero-volume filler rows appear only in results.</summary>
    public int PopulatedRowCount => this.rows.Count;

    /// <summary>
    /// Accumulates one print, or one already-aggregated price level. Non-finite
    /// prices and non-positive volumes are ignored rather than guessed at — the
    /// same guard the VWAP engine applies, for the same reason.
    /// </summary>
    public void Add(double price, double volume, PrintSide side)
    {
        if (!double.IsFinite(price) || !double.IsFinite(volume) || volume <= 0)
            return;

        var rowIndex = this.RowIndexOf(price);
        if (!this.rows.TryGetValue(rowIndex, out var accumulator))
            accumulator = default;

        switch (side)
        {
            case PrintSide.Buy:
                accumulator.Buy += volume;
                break;
            case PrintSide.Sell:
                accumulator.Sell += volume;
                break;
            default:
                accumulator.Unclassified += volume;
                break;
        }

        this.rows[rowIndex] = accumulator;
        if (rowIndex < this.minRowIndex)
            this.minRowIndex = rowIndex;
        if (rowIndex > this.maxRowIndex)
            this.maxRowIndex = rowIndex;
    }

    /// <summary>Forgets everything, for a range change or re-anchor.</summary>
    public void Clear()
    {
        this.rows.Clear();
        this.minRowIndex = long.MaxValue;
        this.maxRowIndex = long.MinValue;
    }

    /// <summary>
    /// Builds the ladder and walks the value area. False carries WHY in
    /// <paramref name="failure"/> — an empty engine and an over-wide ladder are
    /// stated conditions, never an exception and never a silent empty profile.
    /// </summary>
    public bool TryCompute(
        double valueAreaPercent, out VolumeProfileResult? result, out string failure)
    {
        result = null;

        if (!(valueAreaPercent > 0) || valueAreaPercent > 100)
        {
            failure = $"value-area percent {valueAreaPercent} is outside (0,100]";
            return false;
        }

        if (this.rows.Count == 0)
        {
            failure = "no volume in range";
            return false;
        }

        var span = this.maxRowIndex - this.minRowIndex + 1;
        if (span > MaxRows)
        {
            failure = $"range spans {span} rows of {this.rowSizeTicks} tick(s), above the {MaxRows}-row bound — widen the row size";
            return false;
        }

        var ladder = new ProfileRow[span];
        double total = 0;
        double classified = 0;

        for (long i = 0; i < span; i++)
        {
            var rowIndex = this.minRowIndex + i;
            this.rows.TryGetValue(rowIndex, out var accumulator);
            var price = rowIndex * (double)this.rowSizeTicks * this.tickSize;
            ladder[i] = new ProfileRow(price, accumulator.Buy, accumulator.Sell, accumulator.Unclassified);
            total += ladder[i].TotalVolume;
            classified += accumulator.Buy + accumulator.Sell;
        }

        var poc = PickPoc(ladder, out var pocTieBroken);
        var (low, high) = WalkValueArea(ladder, poc, total * (valueAreaPercent / 100.0));

        result = new VolumeProfileResult(
            ladder, poc, low, high, total,
            total > 0 ? classified / total : 0, pocTieBroken);
        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// The POC row, with the RATIFIED tie convention (operator, 2026-08-28): equal
    /// maxima resolve to the row closer to the ladder's middle, then to the row
    /// above.
    /// </summary>
    private static int PickPoc(ProfileRow[] ladder, out bool tieBroken)
    {
        var best = 0;
        double middle = (ladder.Length - 1) / 2.0;

        for (var i = 1; i < ladder.Length; i++)
        {
            if (ladder[i].TotalVolume > ladder[best].TotalVolume)
            {
                best = i;
            }
            else if (ladder[i].TotalVolume == ladder[best].TotalVolume)
            {
                var challengerDistance = Math.Abs(i - middle);
                var incumbentDistance = Math.Abs(best - middle);
                // Closer to the middle wins; at equal distance the ABOVE row wins,
                // and i > best always here, so the challenger takes it.
                if (challengerDistance <= incumbentDistance)
                    best = i;
            }
        }

        // DECIDED FROM THE FINAL POC, in a second pass, and that is the fix.
        //
        // This flag used to be set inside the equality branch above, which made it true
        // for any TRANSIENT tie against the running best — even when a strictly greater
        // row replaced that best moments later and the convention never decided anything.
        // Measured 2026-08-28: the ladder [10, 10, 100] reported PocIndex=2 (a strict
        // maximum) with PocTieBroken=true. An over-reporting notice is worse than none,
        // because it teaches the reader to discount the one line that should never be
        // discounted.
        //
        // A tie is real when some OTHER row shares the volume of the row actually chosen.
        tieBroken = false;

        for (var i = 0; i < ladder.Length; i++)
        {
            if (i != best && ladder[i].TotalVolume == ladder[best].TotalVolume)
            {
                tieBroken = true;
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// The documented value-area walk, transcribed step for step. Returns the
    /// lowest and highest included row indices.
    /// </summary>
    private static (int Low, int High) WalkValueArea(
        ProfileRow[] ladder, int poc, double targetVolume)
    {
        var low = poc;
        var high = poc;
        var remaining = targetVolume - ladder[poc].TotalVolume;

        while (remaining > 0)
        {
            var hasAbove = high + 1 < ladder.Length;
            var hasBelow = low - 1 >= 0;
            if (!hasAbove && !hasBelow)
                break;

            bool takeAbove;
            if (!hasBelow)
            {
                takeAbove = true;
            }
            else if (!hasAbove)
            {
                takeAbove = false;
            }
            else
            {
                var above = ladder[high + 1].TotalVolume;
                var below = ladder[low - 1].TotalVolume;
                if (above != below)
                {
                    takeAbove = above > below;
                }
                else
                {
                    // Equal volumes: the row closer to the POC; at equal
                    // distance, the row above — both straight from the page.
                    var aboveDistance = (high + 1) - poc;
                    var belowDistance = poc - (low - 1);
                    takeAbove = aboveDistance <= belowDistance;
                }
            }

            var chosen = takeAbove ? ladder[high + 1] : ladder[low - 1];

            // "If it would exceed the target, stop — the value area is complete."
            if (chosen.TotalVolume > remaining)
                break;

            remaining -= chosen.TotalVolume;
            if (takeAbove)
                high++;
            else
                low--;
        }

        return (low, high);
    }

    private long RowIndexOf(double price)
    {
        var tickIndex = (long)Math.Round(price / this.tickSize, MidpointRounding.AwayFromZero);
        // Floor division that stays a floor for negative indices; C# '/' truncates
        // toward zero, which would round a below-zero tick index the wrong way.
        var quotient = tickIndex / this.rowSizeTicks;
        if (tickIndex < 0 && quotient * this.rowSizeTicks != tickIndex)
            quotient--;
        return quotient;
    }

    private struct RowAccumulator
    {
        public double Buy;
        public double Sell;
        public double Unclassified;
    }
}
