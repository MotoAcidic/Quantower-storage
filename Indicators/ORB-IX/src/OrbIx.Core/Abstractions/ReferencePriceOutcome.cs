using System.Globalization;
using System.Text;

namespace OrbIx.Core.Abstractions;

/// <summary>
/// What happened to the last-bar fallback when no live quote could price a tick.
///
/// FIVE STATES, BECAUSE THEY ARE FIVE DIFFERENT FINDINGS. The diagnostic used to render
/// all of them as silence, so "the chart has no history yet" and "the platform refused a
/// real price we already hold" produced identical text.
/// </summary>
public enum BarFallbackOutcome
{
    /// <summary>A live quote priced the tick; the fallback was never needed.</summary>
    NotNeeded,

    /// <summary>The chart supplied no bar series at all.</summary>
    NoSeries,

    /// <summary>The series exists and holds no bars — the platform is still loading.</summary>
    NoBars,

    /// <summary>The last bar could not be read; the platform mutated the series mid-read.</summary>
    BarUnreadable,

    /// <summary>The last bar's close is not a usable price — NaN, zero or infinite.</summary>
    CloseNotUsable,

    /// <summary>A usable close the platform declined to price a tick from.</summary>
    CloseRejected,
}

/// <summary>
/// Every reference price tried while looking for a tick cost, and what each yielded.
///
/// THIS EXISTS BECAUSE A DIAGNOSTIC NAMED THE WRONG FAULT. On 2026-08-31 a chart took 52
/// seconds to draw. The message said:
///
///     Tick cost could not be read for MNQU6 from any available price
///     (last NaN, ask NaN, bid NaN).
///
/// Three candidates — but ReferencePrices tries FOUR, the fourth being the last historical
/// bar's close. Its outcome was reported nowhere, so a reader could not tell "the market is
/// quiet" from "the chart had not loaded its history yet". Measured over those same 52
/// seconds: MNQU6 printed 1,862 trades. The market was not quiet, and the message pointed
/// away from the actual fault.
///
/// IT ALSO REPORTED PRICES IT NEVER TRIED. The old message re-read symbol.Last/Ask/Bid
/// AFTER the loop finished. Those are live platform fields: a quote arriving in between
/// meant the diagnostic printed values no candidate was ever tested against. The values are
/// now captured as they are used and rendered from that capture.
///
/// Lives in Core so the offline suite asserts the wording (§11); the indicator observes and
/// this renders.
/// </summary>
/// <param name="Last">symbol.Last as it was when tried.</param>
/// <param name="Ask">symbol.Ask as it was when tried.</param>
/// <param name="Bid">symbol.Bid as it was when tried.</param>
/// <param name="Fallback">How the last-bar candidate ended.</param>
/// <param name="BarCount">Bars the series held, or 0 when there was no series.</param>
/// <param name="BarClose">
/// The close that was tried, when one was read. Meaningless unless <paramref name="Fallback"/>
/// is <see cref="BarFallbackOutcome.CloseNotUsable"/> or
/// <see cref="BarFallbackOutcome.CloseRejected"/>.
/// </param>
public readonly record struct ReferencePriceOutcome(
    double Last,
    double Ask,
    double Bid,
    BarFallbackOutcome Fallback,
    int BarCount,
    double BarClose)
{
    /// <summary>
    /// The parenthetical clause naming every candidate and how the fallback ended.
    ///
    /// Invariant culture throughout: a decimal comma on one machine and a point on another
    /// would make the same price look like two different ones in a log read across hosts.
    /// </summary>
    public string Describe()
    {
        var text = new StringBuilder()
            .Append("last ").Append(Number(this.Last))
            .Append(", ask ").Append(Number(this.Ask))
            .Append(", bid ").Append(Number(this.Bid));

        switch (this.Fallback)
        {
            case BarFallbackOutcome.NotNeeded:
                break;

            case BarFallbackOutcome.NoSeries:
                text.Append(", no bar series on the chart");
                break;

            case BarFallbackOutcome.NoBars:
                // The state that matters most on a cold start: the platform is loading and
                // there is nothing wrong with anything.
                text.Append(", bars 0 — no history yet");
                break;

            case BarFallbackOutcome.BarUnreadable:
                text.Append(CultureInfo.InvariantCulture, $", bars {this.BarCount}, last bar unreadable (read race)");
                break;

            case BarFallbackOutcome.CloseNotUsable:
                text.Append(CultureInfo.InvariantCulture,
                    $", bars {this.BarCount}, last close {Number(this.BarClose)} is not a usable price");
                break;

            case BarFallbackOutcome.CloseRejected:
                // The opposite finding: a real price the platform would not price a tick
                // from. Nothing here is missing, so waiting will not help.
                text.Append(CultureInfo.InvariantCulture,
                    $", bars {this.BarCount}, last close {Number(this.BarClose)} rejected by GetTickCost");
                break;
        }

        return text.ToString();
    }

    /// <summary>
    /// A candidate as the platform reported it.
    ///
    /// NaN is rendered as the word rather than left to the formatter, because "NaN" is the
    /// single most informative thing this message can say and it must survive any culture.
    /// </summary>
    private static string Number(double value)
        => double.IsNaN(value)
            ? "NaN"
            : value.ToString("0.##########", CultureInfo.InvariantCulture);
}
