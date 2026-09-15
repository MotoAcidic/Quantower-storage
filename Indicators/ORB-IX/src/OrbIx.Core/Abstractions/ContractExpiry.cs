using System;

namespace OrbIx.Core.Abstractions;

/// <summary>
/// The delivery month and year a futures contract carries IN ITS NAME.
/// </summary>
/// <param name="MonthCode">
/// The single delivery-month letter, upper-cased. One of the twelve listed-futures codes.
/// </param>
/// <param name="YearDigits">
/// The year AS THE VENDOR WROTE IT, one or two digits, NOT expanded to a calendar year.
///
/// Kept as text deliberately. <c>MNQU6</c> writes the year as one digit and
/// <c>/MNQU26:XCME</c> as two, and no vendor documentation states how the single digit
/// expands — so turning "6" into 2026 would be an inference dressed as a calculation.
/// <see cref="ContractExpiry.SameTerm"/> compares only the digits both sides supplied.
/// </param>
public readonly record struct ContractTerm(char MonthCode, string YearDigits);

/// <summary>
/// Reads a contract's delivery term out of its name, for the case where the platform did
/// not supply an expiry date.
///
/// WHY THIS EXISTS. Measured on quantower-pc 2026-09-01: four ORB-IX instances, one
/// process, all charting MNQU6, and two of them reported their own chart symbol's
/// ExpirationDate as <c>default(DateTime)</c> while the other two had it populated. The
/// profile source matches a borrow candidate on root AND expiry, so the two with no date
/// could never borrow — they drew no volume profile at all, and the refusal read as "no
/// connection offers MNQ expiring 0001-01-01", which looks like a corrupt date rather
/// than an absent one.
///
/// WHY THE PLATFORM LEAVES IT UNSET IS UNVERIFIED. No isolating check has been run, so
/// nothing here explains it. This exists so the answer does not matter: the name carries
/// the term on every observed spelling, and reading it works whatever the cause.
///
/// A NAME THIS CANNOT PARSE YIELDS null, AND null REFUSES. That property is the whole
/// safety argument. Every failure mode of this parser costs a borrow that does not
/// happen — never a borrow of the wrong instrument, which is the outcome that would be
/// undetectable downstream.
/// </summary>
public static class ContractExpiry
{
    /// <summary>
    /// The twelve delivery-month codes used by listed futures, in calendar order:
    /// January through December.
    ///
    /// A MARKET CONVENTION, NOT VENDOR DOCUMENTATION, and named as such rather than cited
    /// as an authority. Its only job here is deciding whether a name LOOKS like a dated
    /// contract; by the null-refuses property above, a letter wrongly excluded costs a
    /// refusal and a letter wrongly included still has to match the other side's letter
    /// exactly before anything is borrowed.
    /// </summary>
    private const string MonthCodes = "FGHJKMNQUVXZ";

    /// <summary>
    /// The delivery term behind a full contract name — <c>/MNQU26:XCME</c> becomes
    /// <c>('U', "26")</c> — or null when the name carries no term.
    ///
    /// THE DIGIT/LETTER BOUNDARY IS THE SAME ONE
    /// <see cref="ContractRoot.FromContractName"/> USES: trailing year digits, then the
    /// single month letter in front of them. The two functions therefore cannot disagree
    /// about where a root ends and an expiry begins — one returns the part before that
    /// boundary and this returns the part after it.
    ///
    /// null is returned for a continuous series (<c>/MNQ:XCME</c>, no digits), for a bare
    /// root (<c>MNQ</c>), for a name whose letter is not a month code, and for anything
    /// left with no root once the term is removed.
    /// </summary>
    public static ContractTerm? FromContractName(string? raw)
    {
        var text = ContractRoot.Normalise(raw);

        if (text.Length == 0)
            return null;

        var withoutYear = text.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        // No trailing digits: a bare root or a continuous series. Not a dated contract.
        if (withoutYear.Length == text.Length)
            return null;

        // The month letter, and at least one character of root in front of it. A name that
        // is nothing but a letter and digits has no product and is not a contract name.
        if (withoutYear.Length < 2)
            return null;

        var month = char.ToUpperInvariant(withoutYear[^1]);

        if (MonthCodes.IndexOf(month) < 0)
            return null;

        return new ContractTerm(month, text[withoutYear.Length..]);
    }

    /// <summary>
    /// Whether two terms name the same delivery month.
    ///
    /// The month must match exactly — that is what stops December being borrowed onto a
    /// September chart, and it is the guard doing most of the work here.
    ///
    /// THE YEAR IS COMPARED OVER THE DIGITS BOTH SIDES SUPPLIED, shortest wins: "6"
    /// against "26" compares "6" against "6". The alternative — expanding "6" to 2026
    /// against the current decade — would encode a calendar rule no vendor documents.
    ///
    /// The stated limitation, chosen with the operator on 2026-09-01: two contracts
    /// exactly ten years apart compare equal on a one-digit year. Listed front months do
    /// not span a decade, and every other pair is still separated by the month code.
    /// </summary>
    public static bool SameTerm(ContractTerm a, ContractTerm b)
    {
        if (a.MonthCode != b.MonthCode)
            return false;

        var digits = Math.Min(a.YearDigits.Length, b.YearDigits.Length);

        if (digits == 0)
            return false;

        return string.Equals(
            a.YearDigits[^digits..], b.YearDigits[^digits..], StringComparison.Ordinal);
    }

    /// <summary>
    /// A term written back the way a vendor writes it — <c>U6</c> — for status lines.
    ///
    /// Exists so the log can name what was matched on. A borrow justified by a term read
    /// out of a name is weaker evidence than one justified by a date, and the operator can
    /// only weigh that if the line says which it was.
    /// </summary>
    public static string Describe(ContractTerm term)
        => string.Concat(term.MonthCode.ToString(), term.YearDigits);
}
