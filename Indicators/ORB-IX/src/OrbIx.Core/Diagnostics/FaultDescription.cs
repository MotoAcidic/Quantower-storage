using System;
using System.Diagnostics;
using System.Security;

namespace OrbIx.Core.Diagnostics;

/// <summary>
/// An exception described in one line, naming WHERE it came from.
///
/// WHY THIS IS A TYPE AND NOT A PRIVATE HELPER. It began as a private method in the
/// indicator, which the test project does not reference — so the only guard available there
/// is one that reads the source as text, and on 2026-09-05 four such guards in this codebase
/// each PASSED the mutation that removed the behaviour they claimed to protect. A formatter
/// whose whole job is to be correct when something has already gone wrong is exactly the
/// thing that must be exercised rather than asserted.
///
/// WHAT IT IS FOR. "NullReferenceException" alone is the least informative sentence a log
/// can hold: it says something was null somewhere. On 2026-09-04 that sentence covered three
/// different platform calls and named none of them, and a whole afternoon went into
/// hypotheses that naming the site refuted in one line.
/// </summary>
public static class FaultDescription
{
    /// <summary>
    /// The exception's type, the method it was thrown from, and the frame's IL offset.
    /// </summary>
    /// <param name="ex">The exception to describe.</param>
    /// <returns>One line, safe to put on a status panel.</returns>
    /// <exception cref="ArgumentNullException">The exception is null.</exception>
    public static string Of(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var site = ex.TargetSite;
        var where = site is null
            ? "site unavailable"
            : $"{site.DeclaringType?.Name ?? "?"}.{site.Name}";

        return $"{ex.GetType().Name} in {where}{Frame(ex)}";
    }

    /// <summary>
    /// An IL offset as text, or a plain statement that there is none.
    /// </summary>
    /// <param name="offset">The frame's offset, or <see cref="StackFrame.OFFSET_UNKNOWN"/>.</param>
    /// <remarks>
    /// SEPARATE AND PUBLIC SO IT CAN BE EXERCISED. OFFSET_UNKNOWN is -1, and printing "-1"
    /// would read as a real position that someone would then go looking for. Forcing a real
    /// throw to produce an unknown offset is not reliably reproducible across runtimes, so
    /// the DECISION is lifted out where it can be called directly — the same move that made
    /// four other guards in this codebase real rather than decorative.
    /// </remarks>
    public static string FormatOffset(int offset)
        => offset == StackFrame.OFFSET_UNKNOWN
            ? "IL offset unavailable"
            : $"IL+{offset}";

    /// <summary>
    /// The throwing frame with its IL offset, or an empty string when there is no stack.
    /// </summary>
    /// <param name="ex">The exception to read.</param>
    /// <remarks>
    /// WHY AN OFFSET AND NOT JUST THE METHOD NAME. TargetSite already gives the method, and
    /// that was enough to refute a wrong hypothesis on 2026-09-04 but not to finish:
    /// Connection.GetTrades has TWO unconditional dereferences that can throw before any
    /// inner call, and a method name cannot separate them. The vendor's assembly is
    /// obfuscated and ships without symbols, so there are no line numbers — the IL offset is
    /// what remains, and it distinguishes two sites within one method.
    ///
    /// FIRST FRAME ONLY: a full stack would push the breach text off the panel, and every
    /// frame below the first is the platform calling itself.
    ///
    /// A DIAGNOSTIC MUST NEVER BECOME THE FAULT. A stack that cannot be read is reported as
    /// unreadable rather than as absent, because those are different facts and the second
    /// would quietly look like "no stack was available".
    /// </remarks>
    private static string Frame(Exception ex)
    {
        try
        {
            var trace = new StackTrace(ex, fNeedFileInfo: false);

            if (trace.FrameCount == 0 || trace.GetFrame(0) is not { } frame)
                return string.Empty;

            var method = frame.GetMethod();
            var offset = frame.GetILOffset();

            var at = FormatOffset(offset);

            return method is null
                ? $" ({at})"
                : $" ({method.DeclaringType?.Name ?? "?"}.{method.Name} {at})";
        }
        catch (Exception probe) when (probe is NotSupportedException or SecurityException)
        {
            return $" (stack unavailable: {probe.GetType().Name})";
        }
    }
}
