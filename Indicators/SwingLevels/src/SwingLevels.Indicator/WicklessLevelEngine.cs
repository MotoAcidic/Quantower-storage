using System;
using System.Collections.Generic;

namespace SwingLevels;

/// <summary>One wickless-candle extreme — a candle's own high or low with (within tolerance) no
/// wick on that side, marking a clean one-directional move with no rejection recorded at that exact
/// price. Extends forward as an untested level, same semantics as <see cref="SwingLevel"/> and
/// <see cref="SessionLevel"/>, until a later bar trades through it.</summary>
internal sealed class WicklessLevel
{
    public double Price { get; }
    public bool IsHigh { get; }
    public DateTime ConfirmedUtc { get; }
    public DateTime? TouchedUtc { get; private set; }

    public WicklessLevel(double price, bool isHigh, DateTime confirmedUtc)
    {
        this.Price = price;
        this.IsHigh = isHigh;
        this.ConfirmedUtc = confirmedUtc;
    }

    public void MarkTouched(DateTime utc) => this.TouchedUtc ??= utc;
}

/// <summary>
/// Wickless-candle level detector. Operator's own ask (2026-10-15): "i also have an indicator that
/// marks out untested wickless candles but i dont have the source to it" — no source to port from,
/// so this reuses the one "wickless" definition already established and verified against live
/// trading in this codebase: `srmAutoStrategy.cs`'s own `TryFindWicklessTarget` — a candle has "no
/// wick at all on a given side, within a small tolerance," i.e. `bodyExtreme` on that side sits
/// within <see cref="tolerancePoints"/> of the bar's own high/low. A top-wickless candle (no upper
/// wick) marks its HIGH as a level; a bottom-wickless candle (no lower wick) marks its LOW. Same
/// "extends forward until a later bar trades through it" semantics already used for swing and
/// session levels.
///
/// Inherits the source definition's own known limitation as-is, stated plainly rather than silently
/// diverging from the already-verified logic: no minimum body-size filter, so a tiny/doji bar can
/// register as wickless on both sides at once. `srmAutoStrategy` accepts this trade-off already (it
/// only ever uses the result as a target candidate, picking the nearest one) — kept identical here
/// rather than adding a filter the proven version doesn't have.
/// </summary>
internal sealed class WicklessLevelEngine
{
    private readonly double tolerancePoints;
    private readonly int maxLevelsKept;

    public WicklessLevelEngine(double tolerancePoints, int maxLevelsKept)
    {
        this.tolerancePoints = Math.Max(0, tolerancePoints);
        this.maxLevelsKept = Math.Max(2, maxLevelsKept);
    }

    public List<WicklessLevel> Levels { get; } = new();

    public void Reset() => this.Levels.Clear();

    public void FeedBar(DateTime timeUtc, double open, double high, double low, double close)
    {
        foreach (var lvl in this.Levels)
        {
            if (lvl.TouchedUtc is null && high >= lvl.Price && low <= lvl.Price)
                lvl.MarkTouched(timeUtc);
        }

        var bodyHigh = Math.Max(open, close);
        var bodyLow = Math.Min(open, close);

        if (high - bodyHigh <= this.tolerancePoints)
            this.Levels.Add(new WicklessLevel(high, true, timeUtc));

        if (bodyLow - low <= this.tolerancePoints)
            this.Levels.Add(new WicklessLevel(low, false, timeUtc));

        while (this.Levels.Count > this.maxLevelsKept)
            this.Levels.RemoveAt(0);
    }
}
