using System;
using System.Globalization;
using OrbIx.Core.Abstractions;

namespace OrbIx.Core.Features;

/// <summary>
/// What the tape says about which side initiated, measured against where the print actually
/// landed in the book.
///
/// WHY THIS IS NOT A MATTER OF OPINION. A print at or above the ASK was taken by someone
/// lifting the offer; a print at or below the BID was hit into a resting bid. That is what
/// those words mean, and it is true on every venue and every vendor. The FLAG a feed attaches
/// is a convention, and conventions differ: the one data vendor tick archive is measurably INVERTED
/// (bid_volume &gt; 0 means the BUYER aggressed, 99.8% over 1.35M prints) while the live QuestDB
/// column reads the conventional way. Two vendors, two answers, the same market.
///
/// SO THE FLAG IS CHECKED AGAINST THE GEOMETRY RATHER THAN TRUSTED. Nothing here needs a second
/// data source, a second vendor, or a historical archive: the quote that prevailed at the print
/// is already carried on every <see cref="TickEvent"/>, because classification is a property of
/// the event rather than of whatever the book looked like when a consumer got round to reading
/// it.
///
/// BOTH READINGS ARE REPORTED, ALWAYS. It would be easy to report only "agreement" and let a
/// low number read as a noisy feed. An INVERTED feed also produces low agreement — and the two
/// demand opposite fixes. <see cref="Agreed"/> and <see cref="Inverted"/> are counted
/// separately so the answer is legible instead of inferred.
///
/// PRINTS INSIDE THE SPREAD ARE NOT EVIDENCE AND ARE NOT COUNTED. A trade strictly between the
/// bid and the ask was not a cross of either — a midpoint match, an implied leg from a calendar
/// spread, a block. Its geometry says nothing about who initiated, and folding it into either
/// bucket would manufacture agreement or disagreement out of a case that has neither.
/// </summary>
public sealed class AggressorConvention
{
    private long agreed;
    private long inverted;
    private long insideSpread;
    private long unusableQuote;
    private long unflagged;

    /// <summary>Prints whose flag matched the geometry: flagged Buy and taken at or above the ask.</summary>
    public long Agreed => this.agreed;

    /// <summary>
    /// Prints whose flag was the OPPOSITE of the geometry. A feed where this dominates is
    /// inverted, not broken, and the fix is a mapping rather than a bug hunt.
    /// </summary>
    public long Inverted => this.inverted;

    /// <summary>Prints strictly between the quotes, which carry no directional geometry.</summary>
    public long InsideSpread => this.insideSpread;

    /// <summary>Prints whose quote was missing, locked or crossed, so geometry could not be read.</summary>
    public long UnusableQuote => this.unusableQuote;

    /// <summary>Prints the feed did not flag at all.</summary>
    public long Unflagged => this.unflagged;

    /// <summary>Prints that produced evidence either way.</summary>
    public long Judged => this.agreed + this.inverted;

    /// <summary>
    /// Takes one print.
    ///
    /// THE COMPARISONS ARE INCLUSIVE ON BOTH SIDES. A print exactly AT the ask lifted the offer
    /// and a print exactly AT the bid hit the bid; those are the overwhelmingly common cases,
    /// and requiring a strict cross would discard nearly every piece of evidence there is.
    /// </summary>
    public void Add(in TickEvent tick)
    {
        if (tick.Aggressor is not (Aggressor.Buy or Aggressor.Sell))
        {
            this.unflagged++;
            return;
        }

        // A quote that is absent, locked or crossed cannot place the print. Refused rather than
        // guessed: a crossed book is a real state of a feed mid-update, not a malformed message,
        // and reading geometry off it would produce confident nonsense.
        if (!double.IsFinite(tick.Bid) || !double.IsFinite(tick.Ask)
            || !double.IsFinite(tick.Price) || tick.Bid <= 0 || tick.Ask <= 0
            || tick.Bid >= tick.Ask)
        {
            this.unusableQuote++;
            return;
        }

        var takenByBuyer = tick.Price >= tick.Ask;
        var takenBySeller = tick.Price <= tick.Bid;

        if (takenByBuyer == takenBySeller)
        {
            // Strictly inside the spread: neither quote was crossed. Not evidence.
            this.insideSpread++;
            return;
        }

        var flaggedBuy = tick.Aggressor == Aggressor.Buy;

        if (flaggedBuy == takenByBuyer)
            this.agreed++;
        else
            this.inverted++;
    }

    /// <summary>Forgets everything, for a symbol or connection change.</summary>
    public void Reset()
    {
        this.agreed = 0;
        this.inverted = 0;
        this.insideSpread = 0;
        this.unusableQuote = 0;
        this.unflagged = 0;
    }

    /// <summary>
    /// The verdict, in words, with the numbers that decided it.
    ///
    /// A THRESHOLD, NOT A MAJORITY. A feed that is 60% one way is not "conventional with noise",
    /// it is a feed nobody has explained, and calling it either way would put a direction on
    /// every footprint in the product on the strength of a coin weighted 6-4. The bar is 95% of
    /// JUDGED prints, and anything below it is reported as exactly what it is.
    ///
    /// THE SAMPLE SIZE IS PART OF THE VERDICT. Ninety-five percent of forty prints is not a
    /// finding, so below a floor the line says how far it has got instead of pronouncing.
    /// </summary>
    public string Describe()
    {
        var judged = this.Judged;

        if (judged < MinimumSample)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"aggressor convention: {judged:N0} of {MinimumSample:N0} print(s) needed — not yet measured");
        }

        var agreeShare = (double)this.agreed / judged;
        var invertShare = (double)this.inverted / judged;

        var verdict = agreeShare >= Threshold
            ? "CONVENTIONAL (flag matches where the print landed)"
            : invertShare >= Threshold
                ? "INVERTED — the flag is the OPPOSITE of where the print landed"
                : "UNDECIDED — the flag does not track the book either way";

        return string.Create(
            CultureInfo.InvariantCulture,
            // Formatted explicitly rather than with "P": the invariant culture's percent pattern
            // puts a SPACE before the sign ("100.00 %"), which is a formatting accident nobody
            // reading a log line would predict.
            $"aggressor convention: {verdict}; {agreeShare * 100:N2}% agree, "
            + $"{invertShare * 100:N2}% inverted "
            + $"over {judged:N0} judged; {this.insideSpread:N0} inside the spread, "
            + $"{this.unusableQuote:N0} no usable quote, {this.unflagged:N0} unflagged");
    }

    /// <summary>Share of judged prints one reading must hold to be called.</summary>
    public const double Threshold = 0.95d;

    /// <summary>Judged prints required before the line pronounces at all.</summary>
    public const int MinimumSample = 500;
}
