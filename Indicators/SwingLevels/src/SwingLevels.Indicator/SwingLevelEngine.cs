using System;
using System.Collections.Generic;

namespace SwingLevels;

/// <summary>One confirmed swing high or low — a support/resistance level — extended forward from
/// its own confirmation moment until a later bar trades through it.</summary>
internal sealed class SwingLevel
{
    public double Price { get; }
    public bool IsHigh { get; }
    public DateTime ConfirmedUtc { get; }
    public DateTime? TouchedUtc { get; private set; }

    public SwingLevel(double price, bool isHigh, DateTime confirmedUtc)
    {
        this.Price = price;
        this.IsHigh = isHigh;
        this.ConfirmedUtc = confirmedUtc;
    }

    public void MarkTouched(DateTime utc) => this.TouchedUtc ??= utc;
}

/// <summary>
/// Higher-timeframe swing high/low detector — operator's own ask (2026-10-15): "i want to trade on
/// a higher time frame with less risk, i want to trade support and resistance levels," confirmed
/// via AskUserQuestion as higher-timeframe swing highs/lows specifically (not prior-period H/L or
/// volume-profile levels), 1-hour by default, visual only (no auto-trading).
///
/// Same fractal pivot-confirmation rule as every other swing detector already in this codebase
/// (`finchDomScalpStrategy`'s own `SwingTracker`, `goldOrbStrategy`'s own `SwingLevelTracker`,
/// `PocEngine`'s internal pivot check, `OrderBlockEngine`'s own pivot check) — a candidate bar needs
/// <see cref="pivotLookback"/> bars closed on BOTH sides without a more extreme high/low. Written
/// fresh here rather than reused via a cross-project `&lt;Compile Include&gt;` from
/// `goldOrbStrategy`'s own `SwingLevelTracker.cs`, even though that file does the same core pivot
/// math: that tracker only keeps raw price values for TARGETING use (nearest swing above/below a
/// given price) — it has no per-level timestamp or touched-state, both of which a DRAWN indicator
/// genuinely needs (where to anchor the line, when to stop extending it) and a pure stop/target
/// lookup never did. Matches this codebase's own established precedent of NOT sharing this exact
/// pivot logic across files even where it's conceptually identical — every existing swing detector
/// here is its own small, independent implementation already.
///
/// CONFIRMATION LAG, same accepted limitation as `PocEngine`'s own documented caveat: a swing is
/// only confirmed `pivotLookback` bars after it actually happened, so price may have already traded
/// through that exact price during the lag window before the level is even added — it still starts
/// as "untested." Not retroactively corrected; stated plainly rather than silently wrong.
/// </summary>
internal sealed class SwingLevelEngine
{
    private const int MaxBarsKept = 2000;

    private readonly List<(DateTime TimeUtc, double High, double Low)> bars = new();
    private readonly int pivotLookback;
    private readonly int maxLevelsKept;

    public SwingLevelEngine(int pivotLookback, int maxLevelsKept)
    {
        this.pivotLookback = Math.Max(1, pivotLookback);
        this.maxLevelsKept = Math.Max(1, maxLevelsKept);
    }

    public List<SwingLevel> Levels { get; } = new();

    public void Reset()
    {
        this.bars.Clear();
        this.Levels.Clear();
    }

    public void FeedBar(DateTime timeUtc, double high, double low)
    {
        this.bars.Add((timeUtc, high, low));
        if (this.bars.Count > MaxBarsKept)
            this.bars.RemoveRange(0, this.bars.Count - MaxBarsKept);

        foreach (var lvl in this.Levels)
        {
            if (lvl.TouchedUtc is null && high >= lvl.Price && low <= lvl.Price)
                lvl.MarkTouched(timeUtc);
        }

        var n = this.bars.Count;
        var pivotIndex = n - 1 - this.pivotLookback;
        if (pivotIndex - this.pivotLookback < 0) return;

        var candidate = this.bars[pivotIndex];
        var isSwingHigh = true;
        var isSwingLow = true;

        for (var j = pivotIndex - this.pivotLookback; j <= pivotIndex + this.pivotLookback; j++)
        {
            if (j == pivotIndex) continue;
            if (this.bars[j].High >= candidate.High) isSwingHigh = false;
            if (this.bars[j].Low <= candidate.Low) isSwingLow = false;
        }

        if (isSwingHigh) this.Levels.Add(new SwingLevel(candidate.High, true, candidate.TimeUtc));
        if (isSwingLow) this.Levels.Add(new SwingLevel(candidate.Low, false, candidate.TimeUtc));

        while (this.Levels.Count > this.maxLevelsKept)
            this.Levels.RemoveAt(0);
    }
}
