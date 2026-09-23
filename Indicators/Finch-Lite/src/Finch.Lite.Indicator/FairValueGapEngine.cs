using System;
using System.Collections.Generic;

namespace FinchLite;

/// <summary>
/// "the ability to see inverse fairvalue gaps" (the operator's own ask, 2026-09-23) — fed the
/// CHART's own closed bars one at a time via <see cref="Feed"/> (the operator's own choice: the
/// chart's own timeframe, not a fixed higher one like the order blocks above).
///
/// A fair value gap is the classic 3-candle imbalance: candle 1's high below candle 3's low
/// (a bullish gap, expected to act as support later) or candle 1's low above candle 3's high (a
/// bearish gap, expected to act as resistance). Every gap is tracked internally the moment it
/// forms, but nothing is DISPLAYED yet — only once price CLOSES back through a gap in the
/// direction that disproves its original role does it invert and become visible: a bullish gap
/// that fails as support flips to a bearish inverse FVG (now expected resistance), and the
/// reverse for a bearish gap that fails as resistance. This was the operator's own explicit
/// choice over drawing the plain gap immediately on formation. Once inverted, the SAME
/// close-through invalidation rule chosen for order blocks removes it again — consistency, not a
/// second ask.
/// </summary>
internal sealed class FairValueGapEngine
{
    internal readonly record struct InverseFvgZone(DateTime StartUtc, double Top, double Bottom, bool IsBullish);

    private readonly record struct PendingGap(DateTime StartUtc, double Top, double Bottom, bool IsBullish);

    private const int MaxHistoryKept = 8; // only the last 3 bars matter for detection
    private const int MaxPendingKept = 300;
    private const int MaxActiveKept = 20;

    private readonly List<Bar> history = new();
    private readonly List<PendingGap> pending = new();
    private readonly List<InverseFvgZone> active = new();

    public IReadOnlyList<InverseFvgZone> Active => this.active;

    public void Reset()
    {
        this.history.Clear();
        this.pending.Clear();
        this.active.Clear();
    }

    public void Feed(Bar bar)
    {
        this.history.Add(bar);

        if (this.history.Count > MaxHistoryKept)
            this.history.RemoveAt(0);

        // A pending (not-yet-inverted) gap inverts once price closes through the side that was
        // supposed to hold — a bullish (support) gap inverts on a close BELOW its own bottom; a
        // bearish (resistance) gap inverts on a close ABOVE its own top. The zone's PRICE range
        // and ORIGIN time carry over unchanged; only the role (IsBullish) flips.
        for (var i = this.pending.Count - 1; i >= 0; i--)
        {
            var gap = this.pending[i];
            var inverted = gap.IsBullish ? bar.Close < gap.Bottom : bar.Close > gap.Top;

            if (!inverted)
                continue;

            this.pending.RemoveAt(i);
            this.AddActive(new InverseFvgZone(gap.StartUtc, gap.Top, gap.Bottom, IsBullish: !gap.IsBullish));
        }

        // An already-inverted zone can itself be closed through again later — same rule, removed
        // the same way an order block is.
        for (var i = this.active.Count - 1; i >= 0; i--)
        {
            var zone = this.active[i];
            var mitigated = zone.IsBullish ? bar.Close < zone.Bottom : bar.Close > zone.Top;

            if (mitigated)
                this.active.RemoveAt(i);
        }

        if (this.history.Count >= 3)
        {
            var first = this.history[^3];
            var third = this.history[^1];

            if (first.High < third.Low)
                this.AddPending(new PendingGap(first.OpenUtc, third.Low, first.High, IsBullish: true));
            else if (first.Low > third.High)
                this.AddPending(new PendingGap(first.OpenUtc, first.Low, third.High, IsBullish: false));
        }
    }

    private void AddPending(PendingGap gap)
    {
        this.pending.Add(gap);

        if (this.pending.Count > MaxPendingKept)
            this.pending.RemoveAt(0);
    }

    private void AddActive(InverseFvgZone zone)
    {
        this.active.Add(zone);

        if (this.active.Count > MaxActiveKept)
            this.active.RemoveAt(0);
    }
}
