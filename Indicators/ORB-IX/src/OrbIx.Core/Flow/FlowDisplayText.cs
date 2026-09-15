using System;
using System.Globalization;

using OrbIx.Core.Config;
using OrbIx.Core.Features;

namespace OrbIx.Core.Flow;

/// <summary>
/// What the absorbed displays SAY and how big their marks are.
///
/// IN CORE BECAUSE THESE ARE DECISIONS, AND THE RENDERERS CANNOT BE TESTED. The chart project
/// targets net10.0-windows and this suite runs on Linux, so a rule left in an overlay is one
/// nothing can exercise. Formatting looks like presentation rather than logic, which is why it is
/// worth pinning here: a delta of 12,400 rendered as "12" is not a smaller number, it is a
/// different one, and it renders perfectly.
/// </summary>
public static class FlowDisplayText
{
    /// <summary>
    /// A statistics row's header.
    ///
    /// Spelled out rather than taken from the enum name where the enum name would be unreadable
    /// in a row eighteen pixels high. The header column is measured against these, so a longer
    /// name widens the column instead of being cut — "Session Delta" was clipped to "Session Del"
    /// at a fixed width on Aramid Flow's first live capture.
    /// </summary>
    public static string RowName(StatRow row) => row switch
    {
        StatRow.DeltaPercent => "Delta/Vol %",
        StatRow.SessionDelta => "Session Delta",
        StatRow.SessionDeltaPercent => "Sess Δ/Vol %",
        StatRow.MaxDelta => "Max Delta",
        StatRow.MinDelta => "Min Delta",
        StatRow.DeltaChange => "Delta Change",
        StatRow.VolumePerSecond => "Volume/sec",
        StatRow.SessionVolume => "Session Vol",
        StatRow.Height => "Height (t)",
        StatRow.Duration => "Duration (s)",
        _ => row.ToString(),
    };

    /// <summary>
    /// A statistics row's colour.
    ///
    /// THREE KINDS OF ROW, AND THE THIRD IS THE ONE THAT MATTERS. Ask and bid rows take their own
    /// side's colour. Rows that are a QUANTITY rather than a direction — volume, trades, height,
    /// duration — take the neutral colour, because colouring a volume by its sign would imply a
    /// side it does not have. Everything else is SIGNED, and takes the buy colour when it is at or
    /// above zero and the sell colour below.
    /// </summary>
    public static Rgb RowColour(StatRow row, double value, FlowClusterStatisticsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return row switch
        {
            StatRow.Ask => config.AskColour,
            StatRow.Bid => config.BidColour,

            StatRow.Volume or StatRow.VolumePerSecond or StatRow.SessionVolume or StatRow.Trades
                or StatRow.Height or StatRow.Time or StatRow.Duration => config.VolumeColour,

            _ => value >= 0 ? config.AskColour : config.BidColour,
        };
    }

    /// <summary>A statistics cell's text.</summary>
    public static string CellText(StatRow row, in StatColumn column) => row switch
    {
        StatRow.Time => column.OpenUtc.ToString("HH:mm", CultureInfo.InvariantCulture),

        // A PERCENTAGE AND A RATE ARE NOT COMPACTED. Compacting a number already scaled to a small
        // range would turn 12.4% into "12" and lose the only digit that varies.
        StatRow.DeltaPercent or StatRow.SessionDeltaPercent or StatRow.VolumePerSecond
            => column.Value(row).ToString("0.#", CultureInfo.InvariantCulture),

        _ => Compact(column.Value(row)),
    };

    /// <summary>
    /// A contract count, short enough for a cell one bar wide.
    ///
    /// THE BANDS ARE NOT UNIFORM AND THAT IS DELIBERATE. Between 1,000 and 10,000 a tenth of a
    /// thousand still carries information, so 1,200 reads "1.2K"; above ten thousand it does not,
    /// and 12,400 reads "12K" rather than "12.4K" to keep the cell narrow. Under a thousand the
    /// number is shown whole, because rounding a real count is the one thing a count must not do.
    /// </summary>
    public static string Compact(double value)
    {
        if (double.IsNaN(value))
            return "—";

        var magnitude = Math.Abs(value);

        if (magnitude >= 1_000_000)
            return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";

        if (magnitude >= 10_000)
            return (value / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K";

        if (magnitude >= 1_000)
            return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";

        return value.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>The live counter's three readings, in the order they are drawn.</summary>
    public static (string Buy, string Sell, string Delta) Counter(in CounterReading counter)
        => ("BUY " + Compact(counter.Buy),
            "SELL " + Compact(counter.Sell),
            "Δ " + (counter.Delta >= 0 ? "+" : string.Empty) + Compact(counter.Delta));

    /// <summary>
    /// A marker's size in pixels, from how far past its own threshold the thing that made it went.
    ///
    /// GROWS FROM THE MINIMUM, NEVER SHRINKS BELOW IT. Strength is the value over the threshold, so
    /// anything that qualified at all is at least 1 and draws at the minimum size; twice the
    /// threshold draws twice as large, up to the maximum. A marker smaller than the minimum would
    /// say "this barely counts" about something that met the operator's own bar.
    ///
    /// The bounds are sorted rather than trusted: a document with the two transposed produces a
    /// usable range instead of an empty one, and the loader refuses that document anyway.
    /// </summary>
    public static int MarkerSize(double strength, int minSize, int maxSize)
    {
        var low = Math.Min(minSize, maxSize);
        var high = Math.Max(minSize, maxSize);

        if (!double.IsFinite(strength))
            return low;

        // THE CLAMP IS WHAT ENFORCES THE FLOOR, and it is the only thing that needs to. This line
        // used to read Math.Max(strength, 1d) as well — a second copy of the same rule, which a
        // mutation proved could be deleted without changing any answer the clamp does not already
        // give. One rule, in one place.
        return (int)Math.Round(Math.Clamp(low * strength, low, high));
    }
}
