using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Playbooks;

/// <summary>
/// Where a playbook's measured record stands in the research provenance ledger.
/// </summary>
public enum LedgerStatus
{
    /// <summary>
    /// Measured, but never classified. THE DEFAULT, AND THE HONEST ONE — a number that has not
    /// been through the ledger has not been checked for the instrument defects the ledger
    /// exists to catch, and is not the same thing as a number that passed.
    /// </summary>
    Unregistered,

    /// <summary>Raw data through independently verified code.</summary>
    Clean,

    /// <summary>Withdrawn. The measurement stands but the pipeline behind it does not.</summary>
    Tainted,
}

/// <summary>What a playbook measured on one product.</summary>
/// <param name="Trades">Trades in the sample.</param>
/// <param name="MeanR">Mean R-multiple.</param>
/// <param name="CiLowR">Low end of the 95% interval.</param>
/// <param name="CiHighR">High end of the 95% interval.</param>
public readonly record struct MeasuredRecord(int Trades, double MeanR, double CiLowR, double CiHighR)
{
    /// <summary>
    /// Whether the interval excludes zero.
    ///
    /// REPORTED INSTEAD OF A VERDICT WORD. Calling a result "negative" or "profitable" on a
    /// chart compresses an interval into an adjective, and the adjective survives long after
    /// the interval that justified it is forgotten. The reader gets the interval.
    /// </summary>
    public bool ExcludesZero => (this.CiLowR > 0 && this.CiHighR > 0)
                                || (this.CiLowR < 0 && this.CiHighR < 0);

    /// <summary>Whether the numbers are internally coherent enough to show.</summary>
    public bool IsUsable => this.Trades > 0
                            && !double.IsNaN(this.MeanR)
                            && !double.IsNaN(this.CiLowR)
                            && !double.IsNaN(this.CiHighR)
                            && this.CiLowR <= this.CiHighR;
}

/// <summary>A playbook's measured record across products, and its ledger standing.</summary>
public sealed record PlaybookProvenance
{
    public required LedgerStatus Ledger { get; init; }

    /// <summary>Where the numbers came from, shown so a reader can go and check them.</summary>
    public required string Source { get; init; }

    /// <summary>Keyed by CONTRACT root — MNQ, ES, GC — matching <c>InstrumentSpec.Tier</c>.</summary>
    public required IReadOnlyDictionary<string, MeasuredRecord> ByProduct { get; init; }
}

/// <summary>
/// The label a drawn signal carries.
///
/// A SIGNAL ON A CHART IMPLIES AN EDGE, and these have not earned that implication. The
/// offline replay measured P1 as indistinguishable from zero and P2 as negative on the pooled
/// sample — and the document reporting it says the pooled interval is too narrow because the
/// products are not independent, so the per-product row is the honest unit, where n runs from
/// 4 to 18. Drawing either playbook as an ordinary trade signal would state something the
/// evidence does not support.
///
/// So every signal carries its own record: how many trades measured it, what they averaged,
/// what the interval was, and whether the number has been through the provenance ledger at
/// all. A playbook with no record says so rather than appearing unqualified, because an
/// unlabelled signal and a signal measured to work look identical on a chart.
///
/// THE NUMBERS COME FROM CONFIGURATION, never from a literal in the drawing code. They change
/// whenever the study is re-run, and a figure baked into a renderer is a figure that silently
/// stops being true.
///
/// The rule lives in Core so the offline suite asserts it: the indicator targets
/// net10.0-windows and the suite runs on Linux, which is why <see cref="Features.SessionPalette"/>,
/// <see cref="Features.LevelSelection"/> and <see cref="Telemetry.AttemptReporting"/> live here too.
/// </summary>
public static class SignalProvenance
{
    /// <summary>Shown when a playbook has no measured record for the product being traded.</summary>
    public const string NoRecord = "UNREGISTERED - no measured record";

    /// <summary>
    /// The label for one playbook on one product.
    /// </summary>
    /// <param name="playbookId">The playbook, e.g. <c>P1</c>.</param>
    /// <param name="productTier">
    /// The CONTRACT root, e.g. <c>MNQ</c> — <c>InstrumentSpec.Tier</c>, not <c>Root</c>. The
    /// study measured MNQ and NQ separately and they are different sample sizes, so keying on
    /// the family would report one product's evidence under the other's name.
    /// </param>
    /// <param name="provenance">The playbook's record, or null when it has none.</param>
    public static string Describe(
        string playbookId, string productTier, PlaybookProvenance? provenance)
    {
        if (string.IsNullOrWhiteSpace(playbookId))
            throw new ArgumentException("A playbook id is required.", nameof(playbookId));

        if (string.IsNullOrWhiteSpace(productTier))
            throw new ArgumentException("A product tier is required.", nameof(productTier));

        var head = playbookId + " " + productTier;

        if (provenance is null
            || !provenance.ByProduct.TryGetValue(productTier, out var record)
            || !record.IsUsable)
        {
            return head + " - " + NoRecord;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} - n={1} mean {2:+0.000;-0.000}R CI [{3:+0.00;-0.00}, {4:+0.00;-0.00}] {5}{6}",
            head,
            record.Trades,
            record.MeanR,
            record.CiLowR,
            record.CiHighR,
            record.ExcludesZero ? "excludes zero" : "spans zero",
            Suffix(provenance.Ledger));
    }

    /// <summary>
    /// The ledger standing, appended only when it is something the reader must weigh.
    ///
    /// A CLEAN record adds nothing, because clean is what a cited number is supposed to be and
    /// saying so on every label would train the eye to skip the field that matters.
    /// </summary>
    private static string Suffix(LedgerStatus ledger) => ledger switch
    {
        LedgerStatus.Unregistered => " - UNREGISTERED",
        LedgerStatus.Tainted => " - WITHDRAWN",
        LedgerStatus.Clean => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(ledger), ledger, "Unknown ledger status."),
    };
}
