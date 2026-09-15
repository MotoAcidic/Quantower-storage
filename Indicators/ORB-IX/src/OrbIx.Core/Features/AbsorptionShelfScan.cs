using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Features;

/// <summary>
/// One closed bar of the window the shelf scan reads.
/// </summary>
/// <param name="OpenTimeUtc">Open of the bar, matching <see cref="FootprintEngine.BarFootprint"/>.</param>
/// <param name="Close">
/// The bar's close. Carried separately because <see cref="FootprintEngine.BarFootprint"/> holds
/// volume per price and not the bar's own close, and the break test needs a close: a bar's traded
/// prices cannot say where it finished.
/// </param>
/// <param name="Cells">Volume at each price, split by aggressor.</param>
public readonly record struct ShelfBar(
    DateTime OpenTimeUtc,
    double Close,
    IReadOnlyDictionary<double, FootprintEngine.PriceCell> Cells);

/// <summary>What a price must have done before it counts as a shelf.</summary>
public readonly record struct ShelfFilter
{
    /// <summary>Contracts traded at the price across the whole window.</summary>
    public required double MinVolume { get; init; }

    /// <summary>
    /// That volume as a share of everything the window traded, 0-100. Keeps the answer from
    /// changing character between an overnight hour and a news bar.
    /// </summary>
    public required double MinSharePercent { get; init; }

    /// <summary>
    /// How one-sided the aggression into the price was, 0-100, measured against CLASSIFIED
    /// volume only. See <see cref="AbsorptionShelfScan"/> for why unclassified volume is
    /// excluded rather than counted.
    /// </summary>
    public required double MinLeanPercent { get; init; }

    /// <summary>
    /// How many separate bars had to trade there. This is the condition that does the real work:
    /// one enormous print is a large trade, not a price that kept reloading.
    /// </summary>
    public required int MinBars { get; init; }

    /// <summary>
    /// Refuse a price whose largest single print is more than this share of its whole volume,
    /// 0-100. The bar count alone can be satisfied by one huge print plus a scattering of small
    /// ones; this closes that door. 100 disables the check.
    /// </summary>
    public required double MaxOnePrintSharePercent { get; init; }

    /// <summary>
    /// A CLOSE this far beyond the price, on the far side of whoever was passive, counts as the
    /// shelf going. In ticks.
    /// </summary>
    public required int ThroughTicks { get; init; }

    /// <summary>Report shelves that were traded through as well, marked broken.</summary>
    public required bool KeepBroken { get; init; }
}

/// <summary>
/// A price that repeatedly absorbed one-sided aggression.
/// </summary>
/// <param name="Price">The price.</param>
/// <param name="FirstBarOpenUtc">Open of the first bar that traded there.</param>
/// <param name="LastBarOpenUtc">Open of the last bar that traded there.</param>
/// <param name="Bars">Separate bars that traded there.</param>
/// <param name="BuyVolume">Contracts that lifted the offer there, across the window.</param>
/// <param name="SellVolume">Contracts that hit the bid there, across the window.</param>
/// <param name="UnclassifiedVolume">Contracts that could not be attributed to a side.</param>
/// <param name="MaxOneTradeVolume">Largest single print seen at the price.</param>
/// <param name="LeanPercent">|delta| over CLASSIFIED volume, 0-100.</param>
/// <param name="SharePercent">Total volume over the window's total volume, 0-100.</param>
/// <param name="PassiveSide">
/// +1 someone passive was BUYING — sellers kept hitting the bid there and it kept being there.
/// -1 someone passive was SELLING. This is the OPPOSITE of the aggressor, and it is the side the
/// shelf defends.
/// </param>
/// <param name="Held">No bar closed through it against the passive side.</param>
/// <param name="Breaks">Bars that closed through it. Zero when it held.</param>
public sealed record AbsorptionShelf(
    double Price,
    DateTime FirstBarOpenUtc,
    DateTime LastBarOpenUtc,
    int Bars,
    double BuyVolume,
    double SellVolume,
    double UnclassifiedVolume,
    double MaxOneTradeVolume,
    double LeanPercent,
    double SharePercent,
    int PassiveSide,
    bool Held,
    int Breaks)
{
    /// <summary>Everything traded at the price, classified or not.</summary>
    public double Volume => this.BuyVolume + this.SellVolume + this.UnclassifiedVolume;

    /// <summary>Volume attributable to a side.</summary>
    public double ClassifiedVolume => this.BuyVolume + this.SellVolume;

    /// <summary>Short label for the chart and the journal.</summary>
    public string Label => string.Format(
        CultureInfo.InvariantCulture,
        "{0} shelf {1:N0} x{2}",
        this.PassiveSide > 0 ? "bid" : "ask",
        this.Volume,
        this.Bars);
}

/// <summary>The scan's result, and any reason it could not be trusted.</summary>
/// <param name="Found">Shelves, heaviest first. Empty when the scan was abandoned.</param>
/// <param name="Problem">
/// Non-null when the scan was abandoned. Nothing is ever returned half-done: see
/// <see cref="AbsorptionShelfScan"/>.
/// </param>
/// <param name="WindowVolume">Everything the window traded, for the panel.</param>
/// <param name="PricesSeen">Distinct prices in the window, for the panel.</param>
public sealed record ShelfScan(
    IReadOnlyList<AbsorptionShelf> Found,
    string? Problem,
    double WindowVolume,
    int PricesSeen)
{
    /// <summary>A scan that found nothing and hit no problem.</summary>
    public static ShelfScan Empty { get; } = new(Array.Empty<AbsorptionShelf>(), null, 0d, 0);
}

/// <summary>
/// Prices that kept absorbing one-sided aggression across a window of footprints — the "lines in
/// the sand".
///
/// WHAT THIS MEASURES, AND WHAT IT DOES NOT CLAIM. There is no order-book data behind this. A
/// genuine refreshing iceberg leaves exactly this footprint; so does one large resting order that
/// was never replenished; and so does a price that simply kept attracting business. What is
/// measured is REPEAT ONE-SIDED ABSORPTION AT A SINGLE PRICE, and that is all that is claimed.
/// Nothing here identifies an iceberg, and the type is not named as though it did.
///
/// STANDING AGAINST-EVIDENCE, WHICH IS WHY THIS FEEDS NO DECISION. Absorption is a MEASURED NULL
/// on MNQ: trial 008, 25,745 episodes across 24 windows and 6 sessions, +1.006 ticks versus
/// matched-random at p 0.0260 — failing Bonferroni at p&lt;0.0100 and below the 2.76-tick passive
/// cost floor. This module therefore produces reference levels for a human to look at. It is not
/// wired to a playbook, it gates no entry, and it must not become either without a new trial
/// registration, exactly as <c>AbsorptionOverlay</c> and <c>AbsorptionGate</c> already record.
///
/// FIVE INDEPENDENT CONDITIONS, and each one exists because of a specific way the other four can
/// be satisfied by something that is not a shelf:
///
/// 1. VOLUME — absolute size at the price.
/// 2. SHARE of the window's volume, so a quiet overnight hour and a news bar stay comparable.
/// 3. LEAN — how one-sided the aggression was, over CLASSIFIED volume only.
/// 4. BARS — it has to have happened repeatedly. One print is a trade, not a shelf.
/// 5. ONE-PRINT SHARE — the bar count can be met by one huge print plus scraps, so a price whose
///    volume is mostly a single print is refused even when it traded in many bars.
///
/// UNCLASSIFIED VOLUME IS EXCLUDED FROM THE LEAN, NOT COUNTED AS AGREEMENT. The live aggressor
/// column carries roughly 13.5% symmetric classification noise and is sound for AGGREGATED delta
/// while never certain per print — which is what a shelf's lean is, aggregated over many bars. But
/// volume that could not be attributed to a side is evidence about nothing, and folding it into
/// the denominator would make a well-classified price look less one-sided than a poorly classified
/// one. It is reported separately so a shelf built on thin classification is visible as such.
///
/// BREAKS ARE CLOSES, AND ONLY FROM THE BAR THE SHELF FIRST TRADED. Price reaching through a level
/// and coming back is the level working; counting that as a break would discard every shelf that
/// ever did its job. And what price did before a shelf existed is not evidence about it.
/// </summary>
public static class AbsorptionShelfScan
{
    /// <summary>
    /// Scans a window of closed-bar footprints.
    /// </summary>
    /// <param name="window">
    /// Closed bars, oldest first. A FIXED window ending at the newest closed bar — never the
    /// visible range. A level that moved when the chart scrolled would not be a level.
    /// </param>
    /// <param name="tickSize">The instrument's price increment, for the break tolerance.</param>
    /// <param name="filter">What a price must have done.</param>
    /// <param name="maxPrices">
    /// Distinct prices the scan will hold. Exceeding it ABANDONS the scan whole rather than
    /// truncating: a partial tape answers the question with part of the window missing and looks
    /// exactly like an answer.
    /// </param>
    public static ShelfScan Scan(
        IReadOnlyList<ShelfBar> window, double tickSize, in ShelfFilter filter, int maxPrices)
    {
        if (window is null)
            throw new ArgumentNullException(nameof(window));

        if (maxPrices <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPrices), maxPrices, "The price cap must be positive.");
        }

        if (window.Count == 0 || tickSize <= 0d)
            return ShelfScan.Empty;

        var minBars = filter.MinBars < 1 ? 1 : filter.MinBars;

        if (window.Count < minBars)
            return ShelfScan.Empty;

        var tape = new Dictionary<double, Accumulated>();
        var windowVolume = 0d;

        for (var i = 0; i < window.Count; i++)
        {
            var bar = window[i];

            if (bar.Cells is null)
                continue;

            foreach (var (price, cell) in bar.Cells)
            {
                var total = cell.Total;

                if (total <= 0d)
                    continue;

                windowVolume += total;

                if (!tape.TryGetValue(price, out var at))
                {
                    if (tape.Count >= maxPrices)
                    {
                        return new ShelfScan(
                            Array.Empty<AbsorptionShelf>(),
                            $"absorption shelves: more than {maxPrices} prices traded in the last "
                            + $"{window.Count} bars. Shorten the window or raise the volume floor.",
                            windowVolume,
                            tape.Count);
                    }

                    at = new Accumulated { FirstBarOpenUtc = bar.OpenTimeUtc };
                }

                at.LastBarOpenUtc = bar.OpenTimeUtc;
                at.Bars++;
                at.BuyVolume += cell.BuyVolume;
                at.SellVolume += cell.SellVolume;
                at.UnclassifiedVolume += cell.UnclassifiedVolume;

                if (cell.MaxOneTradeVolume > at.MaxOneTradeVolume)
                    at.MaxOneTradeVolume = cell.MaxOneTradeVolume;

                tape[price] = at;
            }
        }

        if (windowVolume <= 0d)
            return ShelfScan.Empty;

        var found = new List<AbsorptionShelf>();
        var tolerance = tickSize * (filter.ThroughTicks < 0 ? 0 : filter.ThroughTicks);

        foreach (var (price, at) in tape)
        {
            if (at.Bars < minBars)
                continue;

            var volume = at.BuyVolume + at.SellVolume + at.UnclassifiedVolume;

            if (volume < filter.MinVolume)
                continue;

            var share = volume * 100d / windowVolume;

            if (share < filter.MinSharePercent)
                continue;

            // One print dominating is a large trade wearing a shelf's bar count.
            if (filter.MaxOnePrintSharePercent < 100d
                && at.MaxOneTradeVolume * 100d > filter.MaxOnePrintSharePercent * volume)
            {
                continue;
            }

            var delta = at.BuyVolume - at.SellVolume;

            // Heavier SELL volume means sellers were the ones paying, so whoever stood on the
            // other side was buying. The passive side is the opposite of the aggressive one.
            //
            // THIS ALSO COVERS "NO CLASSIFIED VOLUME", AND A SEPARATE GUARD FOR THAT WOULD BE
            // DEAD. Buy and sell volume are both non-negative, so classified == 0 implies
            // buy == sell == 0, which implies delta == 0 — rejected here. A `classified <= 0`
            // check was written above this line and a mutation proved it unreachable: negating it
            // changed no result, because nothing that could satisfy it ever got past this. It was
            // removed rather than kept as reassurance, and the ordering below is what makes the
            // division safe: delta != 0 guarantees classified > 0.
            var passive = delta < 0d ? 1 : delta > 0d ? -1 : 0;

            if (passive == 0)
                continue;

            var classified = at.BuyVolume + at.SellVolume;
            var lean = Math.Abs(delta) * 100d / classified;

            if (lean < filter.MinLeanPercent)
                continue;

            var breaks = Breaks(window, at.FirstBarOpenUtc, price, passive, tolerance);
            var held = breaks == 0;

            if (!held && !filter.KeepBroken)
                continue;

            found.Add(new AbsorptionShelf(
                price,
                at.FirstBarOpenUtc,
                at.LastBarOpenUtc,
                at.Bars,
                at.BuyVolume,
                at.SellVolume,
                at.UnclassifiedVolume,
                at.MaxOneTradeVolume,
                lean,
                share,
                passive,
                held,
                breaks));
        }

        // Heaviest first, so a cap on how many are DRAWN keeps the ones that matter. Ties break on
        // price so the same window always answers in the same order — a set that reshuffled between
        // frames would make the drawn subset flicker.
        found.Sort(static (a, b) =>
        {
            var byVolume = b.Volume.CompareTo(a.Volume);
            return byVolume != 0 ? byVolume : a.Price.CompareTo(b.Price);
        });

        return new ShelfScan(found, null, windowVolume, tape.Count);
    }

    /// <summary>
    /// Bars that CLOSED through the price against the passive side, counted only from the bar the
    /// shelf first traded.
    /// </summary>
    private static int Breaks(
        IReadOnlyList<ShelfBar> window, DateTime firstBarOpenUtc, double price, int passive,
        double tolerance)
    {
        var breaks = 0;

        for (var i = 0; i < window.Count; i++)
        {
            var bar = window[i];

            if (bar.OpenTimeUtc < firstBarOpenUtc)
                continue;

            if (passive > 0 && bar.Close < price - tolerance)
                breaks++;

            if (passive < 0 && bar.Close > price + tolerance)
                breaks++;
        }

        return breaks;
    }

    /// <summary>One price's running totals while the window is walked.</summary>
    private struct Accumulated
    {
        public DateTime FirstBarOpenUtc;
        public DateTime LastBarOpenUtc;
        public int Bars;
        public double BuyVolume;
        public double SellVolume;
        public double UnclassifiedVolume;
        public double MaxOneTradeVolume;
    }
}
