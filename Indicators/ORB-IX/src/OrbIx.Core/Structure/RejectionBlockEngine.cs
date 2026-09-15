using System;
using System.Collections.Generic;

namespace OrbIx.Core.Structure;

/// <summary>
/// One rejection block. <see cref="Bar"/> is the CHART-TF bar index that
/// formed it. Top &gt; Bottom; <see cref="Mid"/> is the 50% line the source
/// setup marks. <see cref="BarDelta"/> is the forming bar's tick delta
/// when a delta series is running — carried as INFORMATION, never a
/// qualifier (trial 018 measured delta-agreement anti-predictive here).
/// </summary>
public readonly record struct RejectionBlock(
    int Bar,
    bool IsBullish,
    double Top,
    double Bottom,
    double Mid,
    double? BarDelta);

/// <summary>
/// Rejection-block detector over CLOSED chart-timeframe bars against the
/// live HTF zone list, per pinned rule 4 of the approved plan
/// (2026-08-28), the video's setup made precise:
///
///   Bullish: a closed chart bar whose BODY sits entirely above an active
///   (unmitigated) bullish HTF zone's top (min(open, close) &gt; zone.Top)
///   while its lower wick reaches the zone (low &lt;= zone.Top). The block
///   is the wick range [low, min(open, close)]; Mid is its midpoint.
///   Bearish mirror: body entirely below a bearish zone's bottom, upper
///   wick reaching it; block = [max(open, close), high].
///
///   When several active zones qualify on one bar, the NEAREST one wins
///   (highest top for bullish, lowest bottom for bearish) — one block per
///   direction per bar, deterministically.
///
/// Display only, same honesty label as <see cref="ZoneEngine"/>.
/// </summary>
public sealed class RejectionBlockEngine
{
    /// <summary>Total block cap; the oldest is evicted past it.</summary>
    public const int MaxBlocks = 200;

    private readonly List<RejectionBlock> blocks = new();

    private int barsFed;

    public int BarsFed => this.barsFed;

    public IReadOnlyList<RejectionBlock> Blocks => this.blocks;

    /// <summary>
    /// One CLOSED chart-TF bar against the zones as they stand. The zone
    /// list is <see cref="ZoneEngine.Zones"/> at the time the chart bar
    /// closed — HTF lifecycle for the enclosing HTF bar may lag by up to
    /// one HTF period, which is inherent to mixing timeframes and is why
    /// the zone must be unmitigated AT CHART-BAR CLOSE to qualify.
    /// </summary>
    public void Feed(
        double open, double high, double low, double close,
        double? barDelta, IReadOnlyList<Zone> zones)
    {
        int bar = this.barsFed;
        this.barsFed++;

        double bodyBottom = Math.Min(open, close);
        double bodyTop = Math.Max(open, close);

        Zone? bullishHit = null;
        Zone? bearishHit = null;
        foreach (var zone in zones)
        {
            // One rejection per zone (amendment 2026-08-28): a zone that
            // already yielded its block never re-qualifies, so hovering at
            // an edge cannot mint block after block.
            if (zone.State == ZoneState.Mitigated || zone.RejectionClaimed)
                continue;

            if (zone.IsBullish && bodyBottom > zone.Top && low <= zone.Top)
            {
                if (bullishHit is null || zone.Top > bullishHit.Top)
                    bullishHit = zone;
            }
            else if (!zone.IsBullish && bodyTop < zone.Bottom && high >= zone.Bottom)
            {
                if (bearishHit is null || zone.Bottom < bearishHit.Bottom)
                    bearishHit = zone;
            }
        }

        if (bullishHit is not null)
        {
            bullishHit.RejectionClaimed = true;
            this.blocks.Add(new RejectionBlock(
                bar, IsBullish: true,
                Top: bodyBottom, Bottom: low,
                Mid: (low + bodyBottom) / 2.0, BarDelta: barDelta));
        }

        if (bearishHit is not null)
        {
            bearishHit.RejectionClaimed = true;
            this.blocks.Add(new RejectionBlock(
                bar, IsBullish: false,
                Top: high, Bottom: bodyTop,
                Mid: (bodyTop + high) / 2.0, BarDelta: barDelta));
        }

        while (this.blocks.Count > MaxBlocks)
            this.blocks.RemoveAt(0);
    }
}
