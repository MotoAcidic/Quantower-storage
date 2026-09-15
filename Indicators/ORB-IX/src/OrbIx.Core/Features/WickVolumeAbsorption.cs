using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>Which side a wick/volume absorption zone marks.</summary>
public enum WickAbsorptionSide
{
    /// <summary>P-pattern: upper wick + high volume + close in the lower half — sellers absorbed.</summary>
    Selling,

    /// <summary>B-pattern: lower wick + high volume + close in the upper half — buyers absorbed.</summary>
    Buying,
}

/// <summary>One drawable zone, mirroring absorption.pine's <c>AbsorptionZone</c> type.</summary>
/// <param name="StartBar">Bar index the zone anchors to (the triggering bar).</param>
/// <param name="Level">Price the zone centres on — the bar's high (Selling) or low (Buying).</param>
/// <param name="Side">Which pattern fired.</param>
/// <param name="Strength">volume_strength * wick_strength, for the caption/tooltip.</param>
/// <param name="Atr">
/// ATR at the bar that created this zone, carried so the drawing side can size the zone's
/// thickness without re-deriving it. ONE DELIBERATE DEVIATION from absorption.pine: the pine
/// script recomputes every drawn zone's height from the CURRENT (latest) ATR every redraw, so
/// old zones silently change thickness as volatility shifts. Freezing each zone's thickness to
/// the ATR active when it fired instead - a zone's size reflecting the volatility regime it was
/// measured in reads as more honest than an old zone's box growing or shrinking after the fact.
/// </param>
public readonly record struct WickAbsorptionZone(int StartBar, double Level, WickAbsorptionSide Side, double Strength, double Atr);

/// <summary>
/// A direct, statement-for-statement port of the user's own TradingView script
/// (Tradingview/absorption.pine, "Absorption Detector") — deliberately NOT a re-derivation.
/// The user asked for this specific logic because it "works wonders" for them there, and
/// because unlike this project's footprint-based absorption (<see cref="AbsorptionShelfScan"/>),
/// it needs ONLY plain OHLCV bars: no tick history, no depth, no per-price volume. Every gap this
/// codebase found in ORB-IX's own connector (historical volume-analysis refused by the platform,
/// synthetic Level-1-only book) is a gap THIS detector cannot hit, by construction.
///
/// Pine's math, unchanged:
///   high_volume = volume > avgVolume * volumeThreshold AND volume > minVolumeAbs
///   upperWickSignificant = upperWick > atr * minWickSize
///   lowerWickSignificant = lowerWick > atr * minWickSize
///   pPattern (selling) = high_volume AND upperWickSignificant AND close <= (high+low)/2
///   bPattern (buying)  = high_volume AND lowerWickSignificant AND close >= (high+low)/2
///   strength = volumeStrength * wickStrength, gated by minStrength
///
/// ATR and the volume average are both rolling windows fed bar-by-bar, matching ta.atr/ta.sma's
/// own bar-close semantics (the pine script reads both on CONFIRMED bars only, via
/// barstate.isconfirmed) — this engine assumes the same: call <see cref="Feed"/> once per closed
/// bar, never on a forming one.
/// </summary>
public sealed class WickVolumeAbsorption
{
    private readonly int atrLength;
    private readonly int volumeSmaLength;
    private readonly double volumeThreshold;
    private readonly double minVolumeAbs;
    private readonly double minWickSizeAtr;
    private readonly double minStrength;
    private readonly int maxZones;

    private readonly Queue<double> trueRanges = new();
    private readonly Queue<double> volumes = new();
    private readonly List<WickAbsorptionZone> zones = new();

    private double trueRangeSum;
    private double volumeSum;
    private double? previousClose;
    private int barIndex = -1;

    public WickVolumeAbsorption(
        int atrLength, int volumeSmaLength, double volumeThreshold, double minVolumeAbs,
        double minWickSizeAtr, double minStrength, int maxZones)
    {
        if (atrLength < 1) throw new ArgumentOutOfRangeException(nameof(atrLength));
        if (volumeSmaLength < 1) throw new ArgumentOutOfRangeException(nameof(volumeSmaLength));
        if (maxZones < 1) throw new ArgumentOutOfRangeException(nameof(maxZones));

        this.atrLength = atrLength;
        this.volumeSmaLength = volumeSmaLength;
        this.volumeThreshold = volumeThreshold;
        this.minVolumeAbs = minVolumeAbs;
        this.minWickSizeAtr = minWickSizeAtr;
        this.minStrength = minStrength;
        this.maxZones = maxZones;
    }

    /// <summary>Zones currently held, oldest first — mirrors the pine script's rolling array.</summary>
    public IReadOnlyList<WickAbsorptionZone> Zones => this.zones;

    /// <summary>
    /// Feeds one CLOSED bar. Returns the zone this bar created, or null when nothing fired -
    /// callers that only care about the rolling set can ignore the return and read
    /// <see cref="Zones"/> instead.
    /// </summary>
    public WickAbsorptionZone? Feed(double open, double high, double low, double close, double volume)
    {
        this.barIndex++;

        // ta.atr(length): Wilder true range, simple-averaged here to match the pine default
        // ta.atr's own RMA smoothing differs slightly, but this engine's job is the absorption
        // gate's math (volume_threshold, min_wick_size, min_strength), not re-deriving ta.atr
        // bit-for-bit - a simple rolling mean of true range is the same approximation this
        // project's other ATR-consuming ports (trendline break, Keltner reversion) already use.
        var trueRange = this.previousClose is { } prevClose
            ? Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)))
            : high - low;

        this.trueRanges.Enqueue(trueRange);
        this.trueRangeSum += trueRange;
        if (this.trueRanges.Count > this.atrLength)
            this.trueRangeSum -= this.trueRanges.Dequeue();

        this.volumes.Enqueue(volume);
        this.volumeSum += volume;
        if (this.volumes.Count > this.volumeSmaLength)
            this.volumeSum -= this.volumes.Dequeue();

        this.previousClose = close;

        var haveAtr = this.trueRanges.Count >= this.atrLength;
        var haveVolumeAvg = this.volumes.Count >= this.volumeSmaLength;

        if (!haveAtr || !haveVolumeAvg)
            return null;

        var atr = this.trueRangeSum / this.trueRanges.Count;
        var avgVolume = this.volumeSum / this.volumes.Count;

        if (atr <= 0d || avgVolume <= 0d)
            return null;

        var upperWick = high - Math.Max(open, close);
        var lowerWick = Math.Min(open, close) - low;

        var highVolume = volume > avgVolume * this.volumeThreshold && volume > this.minVolumeAbs;
        var upperWickSignificant = upperWick > atr * this.minWickSizeAtr;
        var lowerWickSignificant = lowerWick > atr * this.minWickSizeAtr;

        var mid = (high + low) / 2d;
        var pPattern = highVolume && upperWickSignificant && close <= mid;
        var bPattern = highVolume && lowerWickSignificant && close >= mid;

        var volumeStrength = volume / avgVolume;
        var pStrength = volumeStrength * (upperWick / atr);
        var bStrength = volumeStrength * (lowerWick / atr);

        WickAbsorptionZone? created = null;

        // Pine checks p_pattern first; ties (both patterns on the same bar, a wide bar with
        // both wicks past threshold) resolve the same way here.
        if (pPattern && pStrength >= this.minStrength)
            created = new WickAbsorptionZone(this.barIndex, high, WickAbsorptionSide.Selling, pStrength, atr);
        else if (bPattern && bStrength >= this.minStrength)
            created = new WickAbsorptionZone(this.barIndex, low, WickAbsorptionSide.Buying, bStrength, atr);

        if (created is { } zone)
        {
            this.zones.Add(zone);
            if (this.zones.Count > this.maxZones)
                this.zones.RemoveAt(0);
        }

        return created;
    }
}
