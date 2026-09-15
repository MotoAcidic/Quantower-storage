namespace OrbIx.Core.Flow;

/// <summary>
/// How a print's side is decided.
///
/// MOVED FROM ARAMID FLOW UNCHANGED, including the reason it is a choice rather than a constant.
/// There is no universal aggressor convention: it has to be measured per vendor, and one data vendor's
/// tick-history flag was measured INVERTED on this stack (memory: aggressor conventions by
/// vendor). A single hard-coded reading would silently flip every delta figure on some feed.
/// </summary>
public enum AggressorSource
{
    /// <summary>
    /// Price at or above the prevailing ask is a buy, at or below the bid a sell, between them
    /// unknown. Independent of any vendor convention, which is why it is the default.
    /// </summary>
    QuoteRule,

    /// <summary>The connector's own aggressor flag, taken as delivered.</summary>
    VendorFlag,

    /// <summary>The connector's aggressor flag with buy and sell swapped.</summary>
    VendorFlagInverted,
}

/// <summary>
/// Where session-cumulative figures reset.
///
/// Named for what it does rather than after ATAS's labels, but the mapping is one to one:
/// <see cref="None"/> is ATAS "None", <see cref="Custom"/> is "Custom Session",
/// <see cref="Platform"/> is "Default Session".
/// </summary>
public enum SessionResetMode
{
    /// <summary>No reset: cumulative from the beginning of loaded history.</summary>
    None,

    /// <summary>At the configured start time, in the configured zone.</summary>
    Custom,

    /// <summary>
    /// At the platform's own primary session for the symbol.
    ///
    /// Its open time is applied in the configured zone because the installed assembly's
    /// SessionsContainer publishes no zone — its TimeZone member returns null, decompiled from
    /// 1.147.2 — so the zone input is not redundant here even though the time is not read from it.
    /// </summary>
    Platform,
}

/// <summary>Which fans a Fibonacci fan draws.</summary>
public enum FanAnchor
{
    /// <summary>From the look-back's earlier extreme to its later extreme.</summary>
    HighToLow,

    /// <summary>From the look-back's high to the current bar.</summary>
    HighToCurrent,

    /// <summary>Both fans.</summary>
    Both,
}

/// <summary>
/// Where a stacked-imbalance volume floor comes from.
///
/// THIS EXISTS BECAUSE THE FLOOR IS PERIOD-DEPENDENT AND A DOCUMENT IS NOT. The measured floors
/// differ by bar period — 45 at one minute, 40 at five, 30 at fifteen — so a single number in the
/// configuration would be right for one chart and wrong for the others. See
/// <see cref="ImbalanceVolumeFloor"/> and docs/IMBALANCE-VOLUME-FLOOR.md.
/// </summary>
public enum ImbalanceFloorSource
{
    /// <summary>
    /// Take the floor measured for the chart's bar period, borrowing the nearest measured period
    /// when this one was not swept. What the chart is using, and whether it was borrowed, reaches
    /// the log either way.
    /// </summary>
    Measured,

    /// <summary>
    /// Use the number in the document, whatever the period. For a product the sweep never covered,
    /// or to reproduce a setting somebody else is running.
    /// </summary>
    Fixed,

    /// <summary>
    /// MEASURE THE FLOOR FROM THE TAPE IN FRONT OF THE CHART, and fall back to
    /// <see cref="Measured"/> when there is not enough of it.
    ///
    /// The measured floors were swept on ONE contract, and the document recording them says
    /// they must not be carried to another by analogy. That holds while the indicator stays on
    /// one desk and breaks the moment it is handed to anybody else, because the NUMBER travels
    /// and the CAVEAT does not: a reader on a different product sees a blank display or a solid
    /// one, and the floor on the settings panel looks equally authoritative either way.
    ///
    /// Under this source the floor is chosen by the marks-per-session it actually produces on
    /// this instrument's own bars, and what was measured, on how much, reaches the status line.
    /// See <see cref="OrbIx.Core.Features.InstrumentCalibration"/>.
    /// </summary>
    Calibrated,
}
