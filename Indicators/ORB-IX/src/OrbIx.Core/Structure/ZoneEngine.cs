using System;
using System.Collections.Generic;

namespace OrbIx.Core.Structure;

/// <summary>What produced a zone.</summary>
public enum ZoneKind
{
    FairValueGap,
    OrderBlock,
}

/// <summary>
/// Zone lifecycle. FRESH until a LATER bar's range overlaps it, TOUCHED
/// until a later CLOSE beyond the far edge, then MITIGATED — after which
/// the overlay stops extending it (the HH/LL horizon rule).
/// </summary>
public enum ZoneState
{
    Fresh,
    Touched,
    Mitigated,
}

/// <summary>
/// One price zone. <see cref="StartBar"/> is the bar whose CLOSE created it
/// (the third bar of an FVG; the same bar for the FVG's order block, whose
/// price range comes from the qualifying body bar). Top &gt; Bottom always.
/// </summary>
public sealed class Zone
{
    internal Zone(ZoneKind kind, bool isBullish, int startBar, double top, double bottom)
    {
        this.Kind = kind;
        this.IsBullish = isBullish;
        this.StartBar = startBar;
        this.Top = top;
        this.Bottom = bottom;
        this.State = ZoneState.Fresh;
    }

    public ZoneKind Kind { get; }

    public bool IsBullish { get; }

    public int StartBar { get; }

    public double Top { get; }

    public double Bottom { get; }

    public ZoneState State { get; internal set; }

    /// <summary>Null until a later bar's range overlaps the zone.</summary>
    public int? TouchedBar { get; internal set; }

    /// <summary>Null until a later close lands beyond the far edge.</summary>
    public int? MitigatedBar { get; internal set; }

    /// <summary>
    /// True once this zone has produced its rejection block. Amendment
    /// approved 2026-08-28 ("fix both"): a zone yields ONE rejection —
    /// the source setup waits for "a rejection block" at the array, and
    /// re-emitting on every hovering bar measured as 50 blocks (the cap)
    /// on the first deployed session.
    /// </summary>
    public bool RejectionClaimed { get; internal set; }
}

/// <summary>
/// FVG / order-block engine over CLOSED bars of ONE timeframe, per the
/// definitions pinned in the approved plan (2026-08-28):
///
///   1. FVG (3-bar imbalance): bullish when low[i] &gt; high[i-2] — zone
///      [high[i-2], low[i]] born at the close of bar i; bearish mirror
///      when high[i] &lt; low[i-2] — zone [high[i], low[i-2]].
///   2. Order block: for a bullish FVG born at i, the nearest bar
///      j &lt;= i-1 with i-1-j &lt;= 3 whose body is bearish
///      (close[j] &lt; open[j]); zone = the body. Bearish mirror wants a
///      bullish body. No qualifying bar — no OB; the FVG still exists.
///   3. Lifecycle: FRESH → TOUCHED (a LATER bar's high/low overlaps) →
///      MITIGATED (a LATER close beyond the far edge: below the bottom of
///      a bullish zone, above the top of a bearish one). A zone is never
///      advanced by its own birth bar. The cap evicts the oldest zone
///      outright once the list exceeds <see cref="MaxZones"/> — a total
///      cap, chosen over a live-only cap because it also bounds memory;
///      tools/zone_reference.py implements the identical rule.
///
/// Shape follows <see cref="HhLlEngine"/>: no dates, no platform types,
/// append-only series, one <see cref="Feed"/> per closed bar, everything
/// queryable afterwards. Display only — the level-retest entry class is
/// measured NEGATIVE here (levels-null library; ORB-IX replay P2); this
/// engine draws structure, it never recommends a trade.
/// </summary>
public sealed class ZoneEngine
{
    /// <summary>Total zone cap; the oldest zone is evicted past it.</summary>
    public const int MaxZones = 200;

    /// <summary>How far back an order-block body may sit from the displacement bar.</summary>
    public const int OrderBlockLookback = 3;

    private readonly List<double> opens = new();
    private readonly List<double> highs = new();
    private readonly List<double> lows = new();
    private readonly List<double> closes = new();
    private readonly List<Zone> zones = new();

    public int BarsFed => this.opens.Count;

    public IReadOnlyList<Zone> Zones => this.zones;

    /// <summary>One CLOSED bar: lifecycle first, then births, then eviction.</summary>
    public void Feed(double open, double high, double low, double close)
    {
        this.opens.Add(open);
        this.highs.Add(high);
        this.lows.Add(low);
        this.closes.Add(close);
        int n = this.opens.Count - 1;

        // Lifecycle strictly for zones born BEFORE this bar: a zone must
        // not be touched or mitigated by the bar that created it.
        foreach (var zone in this.zones)
        {
            if (zone.State == ZoneState.Mitigated || zone.StartBar >= n)
                continue;

            if (zone.State == ZoneState.Fresh && low <= zone.Top && high >= zone.Bottom)
            {
                zone.State = ZoneState.Touched;
                zone.TouchedBar = n;
            }

            bool closedThrough = zone.IsBullish ? close < zone.Bottom : close > zone.Top;
            if (closedThrough)
            {
                // A close through the zone also entered it; a zone cannot
                // be mitigated without having been touched.
                if (zone.State == ZoneState.Fresh)
                {
                    zone.State = ZoneState.Touched;
                    zone.TouchedBar = n;
                }

                zone.State = ZoneState.Mitigated;
                zone.MitigatedBar = n;
            }
        }

        if (n >= 2)
        {
            // Birth order is fixed for determinism: bullish FVG, its OB,
            // bearish FVG, its OB.
            if (low > this.highs[n - 2])
            {
                this.zones.Add(new Zone(
                    ZoneKind.FairValueGap, isBullish: true, startBar: n,
                    top: low, bottom: this.highs[n - 2]));
                this.TryBirthOrderBlock(n, wantBearishBody: true, isBullish: true);
            }

            if (high < this.lows[n - 2])
            {
                this.zones.Add(new Zone(
                    ZoneKind.FairValueGap, isBullish: false, startBar: n,
                    top: this.lows[n - 2], bottom: high));
                this.TryBirthOrderBlock(n, wantBearishBody: false, isBullish: false);
            }
        }

        while (this.zones.Count > MaxZones)
            this.zones.RemoveAt(0);
    }

    /// <summary>
    /// Pinned rule 2: the nearest bar at or before the displacement bar
    /// (i-1), within <see cref="OrderBlockLookback"/> bars of it, whose
    /// body opposes the gap direction. Doji bodies (open == close) never
    /// qualify — a body must actually oppose.
    /// </summary>
    private void TryBirthOrderBlock(int fvgBar, bool wantBearishBody, bool isBullish)
    {
        int displacement = fvgBar - 1;
        for (int j = displacement; j >= 0 && displacement - j <= OrderBlockLookback; j--)
        {
            bool bodyQualifies = wantBearishBody
                ? this.closes[j] < this.opens[j]
                : this.closes[j] > this.opens[j];
            if (!bodyQualifies)
                continue;

            double bodyTop = Math.Max(this.opens[j], this.closes[j]);
            double bodyBottom = Math.Min(this.opens[j], this.closes[j]);
            this.zones.Add(new Zone(
                ZoneKind.OrderBlock, isBullish, startBar: fvgBar,
                top: bodyTop, bottom: bodyBottom));
            return;
        }
    }
}
