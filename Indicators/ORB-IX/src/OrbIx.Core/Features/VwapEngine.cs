using System;
using System.Collections.Generic;

namespace OrbIx.Core.Features;

/// <summary>Where an anchored VWAP starts accumulating.</summary>
public enum VwapAnchorMode
{
    Session,
    OrbClose,
    LastHigherHigh,
    LastLowerLow,
    CustomTime,
}

/// <summary>One drawable VWAP point, keyed to the chart bar it closes with.</summary>
public readonly record struct VwapSample(DateTime BarOpenUtc, double Vwap, double Sigma);

/// <summary>
/// Volume-weighted average price with volume-weighted standard-deviation
/// bands, per pinned rule 1 of the master plan (approved 2026-08-28):
/// VWAP = Σ(p·v)/Σv over ALL prints (volume needs no aggressor side);
/// σ = sqrt(Σ(p²·v)/Σv − VWAP²), the volume-weighted deviation of price
/// about VWAP. Bands draw at ±k·σ for a configurable k list.
///
/// The engine is anchor-agnostic: <see cref="Anchor"/> restarts
/// accumulation, and the caller decides when (session roll, ORB close, a
/// structure pivot, a custom instant). Historical seeding uses bar
/// close×volume — the standard approximation, and the reference
/// transcription's definition — so seeded and live stretches are labeled
/// by the caller, never blended silently.
///
/// DISPLAY ONLY: VWAP-side and VWAP-stretch gates measured net-negative
/// out of sample on these signals (trial 022); this engine draws
/// reference geometry, it never gates.
/// </summary>
public sealed class VwapEngine
{
    /// <summary>Sample cap; the oldest is evicted past it.</summary>
    public const int MaxSamples = 4000;

    private readonly List<VwapSample> samples = new();

    private double sumPv;
    private double sumV;
    private double sumPpv;
    private DateTime anchorUtc;
    private bool anchored;

    public IReadOnlyList<VwapSample> Samples => this.samples;

    public DateTime AnchorUtc => this.anchorUtc;

    public bool Anchored => this.anchored;

    /// <summary>Restarts accumulation from an instant; prior samples clear.</summary>
    public void Anchor(DateTime anchorUtc)
    {
        this.anchorUtc = anchorUtc;
        this.anchored = true;
        this.sumPv = 0;
        this.sumV = 0;
        this.sumPpv = 0;
        this.samples.Clear();
    }

    /// <summary>
    /// One print (live) or one bar's close×volume (seed). Zero or
    /// non-finite volume contributes nothing rather than poisoning the
    /// sums.
    /// </summary>
    public void Add(double price, double volume)
    {
        if (!this.anchored || !double.IsFinite(price)
            || !double.IsFinite(volume) || volume <= 0)
        {
            return;
        }

        this.sumPv += price * volume;
        this.sumV += volume;
        this.sumPpv += price * price * volume;
    }

    /// <summary>The running VWAP and σ; false until any volume has landed.</summary>
    public bool TryCurrent(out double vwap, out double sigma)
    {
        if (this.sumV <= 0)
        {
            vwap = double.NaN;
            sigma = double.NaN;
            return false;
        }

        vwap = this.sumPv / this.sumV;
        // Guarded at zero: floating-point cancellation can push the
        // variance a hair negative on near-constant price.
        double variance = Math.Max(0, (this.sumPpv / this.sumV) - (vwap * vwap));
        sigma = Math.Sqrt(variance);
        return true;
    }

    /// <summary>
    /// Freezes the current reading as the bar's drawable point. Called
    /// once per CLOSED chart bar, the delta engine's cadence.
    /// </summary>
    public void SampleBar(DateTime barOpenUtc)
    {
        if (!this.TryCurrent(out var vwap, out var sigma))
            return;

        this.samples.Add(new VwapSample(barOpenUtc, vwap, sigma));

        while (this.samples.Count > MaxSamples)
            this.samples.RemoveAt(0);
    }
}
