using System;
using System.Collections.Generic;
using System.Linq;

namespace oceansStackStrategy;

/// <summary>
/// Absorption-print detection and hold/invalidation bookkeeping — a direct port of `ocean.pine`'s
/// own `absRes`/`absSup`/`curAbsL`/`curAbsS`/`heldAbsL`/`heldAbsS`/`f_absNear` logic. Unlike
/// `DeltaTracker`'s rolling-window sum, this needs a bar's OWN (not rolling) delta and volume plus
/// a positioned, prunable list of "print happened here, on this side, at this price" records — a
/// genuinely different shape, so it is its own class rather than an extension of `DeltaTracker`
/// (which it consumes via `DeltaTracker.LastBarDelta`/`LastBarVolume`, fed in by the caller each
/// bar rather than reaching into it directly, keeping this class independently testable).
///
/// An absorption print is a bar with volume well above its own recent average AND a strongly
/// one-sided delta — but which CLOSED the other way: an aggressive push that got absorbed rather
/// than continuing. Tracked per RTH session; only prints that "held" (price never closed back
/// through them by more than a tolerance during that same session) survive into the NEXT day's
/// `heldAbs` snapshot, which is the only list actually used for next-day scoring.
/// </summary>
internal sealed class AbsorptionTracker
{
    private const int VolumeSmaBars = 50;

    private readonly double holdToleranceTicks;
    private readonly bool mustHold;
    private readonly Queue<double> volumeHistory = new();

    private readonly List<(double Level, int Side)> current = new();
    private readonly List<(double Level, int Side)> held = new();

    private bool lastBarAbsRes;
    private bool lastBarAbsSup;

    /// <param name="holdToleranceTicks">Price units (already ticks * tickSize, not raw ticks) — how
    /// far price has to close back through a current-session print before it's invalidated.</param>
    /// <param name="mustHold">Ports `absMustHold` — when false, prints are never pruned
    /// intra-session (kept purely for parity with the source script's own toggle).</param>
    public AbsorptionTracker(double holdToleranceTicks, bool mustHold)
    {
        this.holdToleranceTicks = holdToleranceTicks;
        this.mustHold = mustHold;
    }

    /// <summary>Call once per newly-started RTH session, BEFORE feeding that session's own first
    /// bar — freezes whatever survived today into `held`, matching Pine's own
    /// `heldAbsL := curAbsL.copy(); curAbsL.clear()`.</summary>
    public void OnRthReset()
    {
        this.held.Clear();
        this.held.AddRange(this.current);
        this.current.Clear();
    }

    /// <summary>Call once per newly-closed bar, RTH or not — the 50-bar volume SMA accumulates
    /// unconditionally (matches Pine's own ungated `ta.sma(volume, 50)`), but absorption detection
    /// and must-hold pruning only run when <paramref name="inRth"/> is true.</summary>
    public void OnBarClosed(Bar bar, double barDelta, bool inRth, double volumeMultiplier, double deltaFraction)
    {
        this.volumeHistory.Enqueue(bar.Volume);
        if (this.volumeHistory.Count > VolumeSmaBars)
            this.volumeHistory.Dequeue();

        if (!inRth)
        {
            this.lastBarAbsRes = false;
            this.lastBarAbsSup = false;
            return;
        }

        var volumeSma = this.volumeHistory.Count > 0 ? this.volumeHistory.Average() : 0d;
        var bigVolume = bar.Volume >= volumeMultiplier * volumeSma;

        var absRes = bigVolume && barDelta >= deltaFraction * bar.Volume && bar.Close <= bar.Open;
        var absSup = bigVolume && barDelta <= -deltaFraction * bar.Volume && bar.Close >= bar.Open;

        this.lastBarAbsRes = absRes;
        this.lastBarAbsSup = absSup;

        if (this.mustHold)
        {
            for (var i = this.current.Count - 1; i >= 0; i--)
            {
                var (level, side) = this.current[i];
                var broken = (side > 0 && bar.Close > level + this.holdToleranceTicks)
                    || (side < 0 && bar.Close < level - this.holdToleranceTicks);
                if (broken) this.current.RemoveAt(i);
            }
        }

        if (absRes) this.current.Add((bar.High, 1));
        if (absSup) this.current.Add((bar.Low, -1));
    }

    /// <summary>Port of `f_absNear` — checked against the HELD (prior-session) list only, never the
    /// still-accumulating current session.</summary>
    public bool IsHeldNear(double edge, int side, double nearPoints)
        => this.held.Any(p => p.Side == side && Math.Abs(p.Level - edge) <= nearPoints);

    /// <summary>Whether the MOST RECENTLY closed bar itself was an absorption print on the given
    /// side — used for the sweep tracker's own "absorption at the sweep extreme" flag, not for
    /// scoring (scoring only ever looks at <see cref="IsHeldNear"/>).</summary>
    public bool LastBarWasAbsorption(int side) => side > 0 ? this.lastBarAbsRes : this.lastBarAbsSup;
}
