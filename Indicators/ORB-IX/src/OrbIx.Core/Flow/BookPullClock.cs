using System;

namespace OrbIx.Core.Flow;

/// <summary>
/// Whether it is time to pull the book from the platform again.
///
/// IN CORE BECAUSE IT IS A DECISION, AND DECISIONS IN THE OVERLAY CANNOT BE TESTED. This began
/// as one line inside the indicator's fold — a subtraction against a stored instant — where the
/// project targets net10.0-windows and the suite runs on Linux, so nothing could reach it. This
/// codebase has already paid for that shape once: five source-text guards passed a mutation that
/// deleted the behaviour they protected, because the behaviour lived somewhere no test could
/// call. The rule that came out of it is the rule applied here.
///
/// A CADENCE OF ZERO IS OFF, NOT INSTANT. The document says so, and reading it as "no minimum
/// interval" would turn a disabled feature into a platform call on every fold — four hundred a
/// second at the 20ms fold floor, against a feature the operator switched off.
///
/// TIME GOING BACKWARDS RELEASES THE GATE RATHER THAN WEDGING IT. A clock that steps back — a
/// resumed machine, a corrected host clock, a caller passing an older instant than the last —
/// would otherwise leave the difference negative and, with the comparison written the obvious
/// way, hold the gate shut until real time caught back up. The book would silently stop being
/// read for as long as the step was large. Treating any backwards step as "due now" costs one
/// early pull and cannot stall.
/// </summary>
public sealed class BookPullClock
{
    private readonly TimeSpan cadence;
    private DateTime lastUtc = DateTime.MinValue;

    /// <summary>
    /// Whether a pull has been granted at all.
    ///
    /// A SEPARATE FLAG, NOT MINVALUE STANDING IN FOR ONE. This began as "lastUtc is MinValue
    /// means never", which makes a pull recorded AT MinValue indistinguishable from no pull at
    /// all — so that clock granted every single ask, forever, instead of once. A test written
    /// for the first-ask guard caught it. The same overloading of MinValue as a sentinel is why
    /// the live counter needed CounterReading.Idle.
    /// </summary>
    private bool pulled;

    /// <param name="cadenceMs">Milliseconds between pulls. Zero or less disables the pull.</param>
    /// <remarks>
    /// NO TERNARY CLAMPING A NON-POSITIVE CADENCE TO ZERO, BECAUSE ONE WOULD BE DEAD CODE.
    /// <see cref="Enabled"/> asks whether the interval is above zero, a negative interval fails
    /// that as surely as zero does, and <see cref="cadence"/> is never read while disabled — so
    /// clamping cannot change any answer. It was written, a mutation removing it survived the
    /// suite, and that is how it was caught rather than shipped as a guard guarding nothing.
    /// </remarks>
    public BookPullClock(int cadenceMs)
        => this.cadence = TimeSpan.FromMilliseconds(cadenceMs);

    /// <summary>False when the document turned the pull off.</summary>
    public bool Enabled => this.cadence > TimeSpan.Zero;

    /// <summary>When the last pull was granted, or MinValue before the first.</summary>
    public DateTime LastPullUtc => this.lastUtc;

    /// <summary>Whether any pull has been granted. Distinct from <see cref="LastPullUtc"/> being MinValue.</summary>
    public bool HasPulled => this.pulled;

    /// <summary>
    /// Grants a pull and records it, or refuses. Asking does not cost a pull — only a grant does.
    /// </summary>
    public bool ShouldPull(DateTime nowUtc)
    {
        if (!this.Enabled)
            return false;

        // The first ask always grants: a book nobody has read yet is not a book that was read
        // recently.
        if (this.pulled)
        {
            var since = nowUtc - this.lastUtc;

            if (since >= TimeSpan.Zero && since < this.cadence)
                return false;
        }

        this.lastUtc = nowUtc;
        this.pulled = true;
        return true;
    }

    /// <summary>Forgets the last pull, so the next ask is granted. For a symbol change.</summary>
    public void Reset()
    {
        this.lastUtc = DateTime.MinValue;
        this.pulled = false;
    }
}
