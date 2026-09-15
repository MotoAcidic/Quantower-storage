using System;
using System.Collections.Generic;
using System.Globalization;

namespace OrbIx.Core.Features;

/// <summary>
/// A session's cumulative delta changing sign, kept as a price level.
/// </summary>
/// <param name="SessionOpenUtc">The session this belongs to, so levels can be aged by session.</param>
/// <param name="CrossedBarOpenUtc">
/// The bar that CROSSED. This is the anchor, and it is deliberately not the bar that confirmed —
/// see <see cref="DeltaFlipEngine"/>.
/// </param>
/// <param name="CrossedBarCloseUtc">When that bar closed.</param>
/// <param name="Sign">+1 the buyers took the session, -1 the sellers.</param>
/// <param name="Price">
/// The crossing bar's CLOSE. The price the market was at when the flip was on the record.
///
/// NOT AN INTERPOLATED CROSSING PRICE, AND THERE IS NO WAY TO PRODUCE ONE. A bar records what
/// traded at each price and never in what order, so the tick at which the running sum passed zero
/// is not recoverable. A price derived by splitting the bar's range in proportion to its delta
/// would look precise and mean nothing.
/// </param>
/// <param name="CumulativeBefore">Session cumulative delta entering the crossing bar.</param>
/// <param name="CumulativeAfter">Session cumulative delta leaving it.</param>
/// <param name="ConfirmBarCloseUtc">The later bar at which the crossing was believed.</param>
/// <param name="ConfirmCumulative">Cumulative delta at that point.</param>
/// <param name="FirstSide">
/// This is the session choosing a side for the first time rather than reversing one. Session
/// cumulative delta starts at zero, so the first move away from it is not a flip in the sense
/// that matters, and it is reported separately rather than counted silently.
/// </param>
public sealed record DeltaFlip(
    DateTime SessionOpenUtc,
    DateTime CrossedBarOpenUtc,
    DateTime CrossedBarCloseUtc,
    int Sign,
    double Price,
    double CumulativeBefore,
    double CumulativeAfter,
    DateTime ConfirmBarCloseUtc,
    double ConfirmCumulative,
    bool FirstSide)
{
    /// <summary>Short label for the chart and the journal.</summary>
    public string Label => string.Format(
        CultureInfo.InvariantCulture,
        "Δflip {0} {1:HH:mm}",
        this.Sign > 0 ? "up" : "down",
        this.CrossedBarCloseUtc);
}

/// <summary>Where the session's delta stands, for the panel.</summary>
/// <param name="Open">A session is under way.</param>
/// <param name="SessionOpenUtc">When it opened.</param>
/// <param name="Cumulative">Session cumulative delta now.</param>
/// <param name="Side">Last CONFIRMED side: +1, -1, or 0 while the session has not committed.</param>
/// <param name="Arming">A crossing is on the board and has not yet travelled far enough.</param>
/// <param name="ArmSign">Which way the arming crossing points.</param>
/// <param name="ArmCumulative">Cumulative delta at the arming crossing's latest reading.</param>
/// <param name="Flips">Confirmed flips this session.</param>
public readonly record struct DeltaFlipRead(
    bool Open,
    DateTime SessionOpenUtc,
    double Cumulative,
    int Side,
    bool Arming,
    int ArmSign,
    double ArmCumulative,
    int Flips)
{
    /// <summary>
    /// What the flip engine is doing, for the log.
    ///
    /// WRITTEN BECAUSE "IS IT BROKEN?" COULD ONLY BE ANSWERED BY A SCREENSHOT. The engine wrote
    /// nothing to the log at all — a direct check of the operator's file on 2026-09-14 found ZERO
    /// mentions of it across the whole run — so a chart with no flip line on it was
    /// indistinguishable from an engine that had never started. The operator asked, correctly,
    /// why it was not drawing; the honest answer needed a live capture because the log had none.
    ///
    /// THE THREE STATES THIS SEPARATES, which are the whole reason it exists:
    ///
    ///     no session open     the engine has not been fed a session yet — nothing is possible
    ///     armed, not confirmed  a crossing HAS happened and is waiting out its confirmation
    ///                           distance; it may yet come back and leave no level at all
    ///     n flips             crossings that confirmed, and are drawn
    ///
    /// The middle one is the one a reader cannot otherwise see. A crossing that armed and then
    /// retraced leaves no trace on the chart by design, and without this line that is identical
    /// to a market that never crossed.
    ///
    /// The distance still to travel is NOT reported here, because this type does not know the
    /// confirmation threshold — it is the caller's setting. What is reported is where cumulative
    /// delta actually is, which is the half the reader cannot get from the settings panel.
    /// </summary>
    public string Describe()
    {
        if (!this.Open)
            return "flips none — no session open";

        var parts = new List<string>(3)
        {
            string.Create(CultureInfo.InvariantCulture, $"flips {this.Flips}"),
            string.Create(CultureInfo.InvariantCulture, $"cumulative {this.Cumulative:N0}"),
        };

        // The side is stated in words rather than as a signed number: "side -1" has been read as
        // a delta value in this log before.
        parts.Add(this.Side switch
        {
            > 0 => "buyers have the session",
            < 0 => "sellers have the session",
            _ => "no side yet",
        });

        if (this.Arming)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"ARMING {(this.ArmSign > 0 ? "up" : "down")} at {this.ArmCumulative:N0} — not confirmed"));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Session cumulative delta changing sign, turned into levels.
///
/// WHAT THIS IS FOR. "The delta flipped" is a statement about the RUNNING SUM SINCE THE SESSION
/// OPENED, not about one bar printing red after a green one. The price at which that happened is
/// the only part of the event still worth anything in a later session, which is why the output is
/// a level rather than a marker.
///
/// TWO RULES KEEP IT OFF CHOP.
///
/// 1. A crossing is ARMED, not accepted. It becomes a flip only once cumulative delta has
///    travelled <see cref="ConfirmContracts"/> beyond zero. A crossing that comes back before
///    then never happened and leaves no level.
/// 2. The level is anchored to the bar that CROSSED, not the bar that confirmed it. The
///    confirmation is evidence about a moment that has already passed; anchoring the level to the
///    confirming bar would put it at a price the market had already left. This is the single
///    easiest thing to get wrong here and it has a named test.
///
/// STANDING AGAINST-EVIDENCE, AND IT APPLIES TO ANY USE OF THIS BEYOND DISPLAY. Cumulative-delta
/// divergence is a MEASURED NULL on MNQ: trial 025, 35 sessions of tick history, 6 cells, zero
/// survivors, and the best-powered cell (n=663) returned net −19.83 ticks with the ENTIRE 95%
/// interval below zero. A flip LEVEL is a different object from a divergence signal — it is a
/// price, not a prediction — but nothing here has been measured either, and this engine therefore
/// produces reference levels and takes no position on whether price respects them. It feeds no
/// playbook and gates no entry.
///
/// FED CLOSED BARS ONLY. <see cref="DeltaSeriesEngine"/> emits a bar when a later print proves it
/// closed, so there is no forming-bar state to rewind here: a flip, once confirmed, is confirmed
/// against data that cannot change. <see cref="Reset"/> exists for a full recalculation.
/// </summary>
public sealed class DeltaFlipEngine
{
    private readonly List<DeltaFlip> flips = new();

    private bool open;
    private DateTime sessionOpenUtc;
    private double cumulative;
    private int side;

    private bool arming;
    private DateTime armBarOpenUtc;
    private DateTime armBarCloseUtc;
    private int armSign;
    private double armBefore;

    /// <summary>Cumulative delta leaving the CROSSING bar, not the confirming one.</summary>
    private double armCrossedAfter;

    /// <summary>Latest cumulative reading while arming, for the "needs N more" panel line.</summary>
    private double armAfter;

    private double armPrice;

    /// <summary>
    /// How far beyond zero cumulative delta must travel before a crossing is believed, in
    /// contracts. Zero marks every change of sign, which is a legitimate setting and a noisy one.
    /// </summary>
    public double ConfirmContracts { get; set; }

    /// <summary>
    /// Whether the session's first move off zero counts as a flip. Normally it does not: every
    /// session would open with one, and "the session has chosen a side" is not "the session has
    /// turned".
    /// </summary>
    public bool MarkFirstSide { get; set; }

    /// <summary>Confirmed flips, oldest first, across every session since the last reset.</summary>
    public IReadOnlyList<DeltaFlip> Flips => this.flips;

    /// <summary>What the panel needs about the session in progress.</summary>
    public DeltaFlipRead Read => new(
        this.open,
        this.sessionOpenUtc,
        this.cumulative,
        this.side,
        this.arming,
        this.armSign,
        this.armAfter,
        this.flips.Count);

    /// <summary>Clears everything, for a full recalculation.</summary>
    public void Reset()
    {
        this.flips.Clear();
        this.open = false;
        this.sessionOpenUtc = default;
        this.cumulative = 0d;
        this.side = 0;
        this.arming = false;
        this.armSign = 0;
        this.armAfter = 0d;
    }

    /// <summary>
    /// Starts a session. Cumulative delta, the side and any arming crossing all reset: a side
    /// carried across a session boundary would be this session wearing yesterday's verdict.
    /// </summary>
    public void OnSessionOpen(DateTime sessionOpenUtc)
    {
        this.open = true;
        this.sessionOpenUtc = sessionOpenUtc;
        this.cumulative = 0d;
        this.side = 0;
        this.arming = false;
        this.armSign = 0;
        this.armAfter = 0d;
    }

    /// <summary>
    /// Adds one CLOSED bar and reports whether it confirmed a flip.
    /// </summary>
    /// <param name="bar">
    /// A closed delta bar. <see cref="DeltaBar.CumulativeAfter"/> is read as authoritative rather
    /// than re-summed here: <see cref="DeltaSeriesEngine"/> already owns the running total and
    /// resets it on session open, and a second accumulator would be a second answer to the same
    /// question.
    /// </param>
    /// <param name="flip">The flip, when one was confirmed.</param>
    /// <returns>True when this bar confirmed a flip.</returns>
    public bool OnClosedBar(in DeltaBar bar, out DeltaFlip? flip)
    {
        flip = null;

        if (!this.open)
            return false;

        var before = this.cumulative;
        var after = bar.CumulativeAfter;

        this.cumulative = after;

        var confirm = this.ConfirmContracts < 0d ? 0d : this.ConfirmContracts;
        var sign = after > 0d ? 1 : after < 0d ? -1 : 0;

        // Sitting exactly on zero settles nothing, and it must not disarm a crossing either: a
        // bar that lands on the line has not taken the side back.
        if (sign == 0)
            return false;

        if (this.side != 0 && sign == this.side)
        {
            // Back on the established side. Whatever was arming did not stick.
            this.arming = false;
            return false;
        }

        if (!this.arming || this.armSign != sign)
        {
            this.arming = true;
            this.armBarOpenUtc = bar.OpenTimeUtc;
            this.armBarCloseUtc = bar.CloseTimeUtc;
            this.armSign = sign;
            this.armBefore = before;
            this.armCrossedAfter = after;
            this.armPrice = bar.Close;
        }

        this.armAfter = after;

        if (Math.Abs(after) < confirm)
            return false;

        var first = this.side == 0;

        // The side is taken whether or not a level is produced. Suppressing the side change along
        // with the level would leave the engine facing the wrong way and unable to find the next
        // flip at all.
        this.side = sign;
        this.arming = false;

        if (first && !this.MarkFirstSide)
            return false;

        // CumulativeBefore/After are the CROSSING bar's own numbers, captured when it armed. The
        // confirming bar is frequently a later one, and reporting its cumulative as the crossing's
        // would describe a different bar than the one the level sits on.
        var produced = new DeltaFlip(
            this.sessionOpenUtc,
            this.armBarOpenUtc,
            this.armBarCloseUtc,
            sign,
            this.armPrice,
            this.armBefore,
            this.armCrossedAfter,
            bar.CloseTimeUtc,
            after,
            first);

        this.flips.Add(produced);
        flip = produced;

        return true;
    }
}
