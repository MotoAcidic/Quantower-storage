using System;

namespace OrbIx.Core.Telemetry;

/// <summary>
/// How often a repeating attempt reports that it is still failing.
///
/// THIS EXISTS BECAUSE A DIAGNOSTIC BECAME THE NOISE IT WAS MEANT TO CUT THROUGH. Reading an
/// instrument's specifications used to happen once, so it logged its failure unconditionally.
/// Adding a one-second retry made that line per-second: a live session on 2026-08-22 wrote
/// "Tick cost could not be read" 130 times, and the startup log is the only diagnostic channel
/// the indicator has.
///
/// Both failure modes are real and they pull in opposite directions. Reporting every attempt
/// buries everything else in the file; reporting only the first hides a wait that never ends,
/// because after one line there is no evidence of elapsed time at all. A back-off satisfies
/// both: the report is immediate, then rarer, and never stops entirely.
///
/// It lives in Core and is a pure function of the attempt number, so the rule is asserted by
/// the offline suite rather than inspected by reading a log after the fact — which is how the
/// flood got shipped.
/// </summary>
public static class AttemptReporting
{
    /// <summary>
    /// Whether an attempt should write its diagnostic.
    ///
    /// TRUE ON THE POWERS OF TWO — attempts 1, 2, 4, 8, 16, 32, 64, 128. Doubling rather than
    /// a fixed interval because what is being waited on has no known duration: a fixed every-
    /// tenth-attempt rule is still linear in the length of the wait, and the 130-attempt
    /// session that prompted this would have written thirteen lines instead of eight hundred
    /// spread over a longer outage. Under this rule that same session writes EIGHT, and an
    /// hour of retrying at one a second writes twelve.
    ///
    /// The count is one-based: the first attempt is 1 and always reports, because a failure
    /// nobody is told about is the state this replaced.
    /// </summary>
    /// <param name="attemptNumber">Which attempt this is, counting from one.</param>
    public static bool ShouldReport(int attemptNumber)
    {
        if (attemptNumber < 1)
            return false;

        // A positive power of two has exactly one bit set, so clearing the lowest set bit
        // leaves zero. Cheaper and exact where a logarithm would need a tolerance.
        return (attemptNumber & (attemptNumber - 1)) == 0;
    }

    /// <summary>
    /// How a resolved wait is described, so the line carries what the operator needs to judge
    /// it rather than only announcing success.
    ///
    /// Returns null when the FIRST attempt succeeded: there was no wait, so saying anything
    /// about one would be noise, and a message that appears on every clean start is a message
    /// nobody reads on the start that was not clean.
    /// </summary>
    /// <param name="attempts">Total attempts made, counting from one.</param>
    /// <param name="waited">Time from the first attempt to the one that succeeded.</param>
    public static string? DescribeResolution(int attempts, TimeSpan waited)
    {
        if (attempts <= 1)
            return null;

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "after {0:N0} attempts over {1:N1}s",
            attempts,
            waited.TotalSeconds);
    }
}
