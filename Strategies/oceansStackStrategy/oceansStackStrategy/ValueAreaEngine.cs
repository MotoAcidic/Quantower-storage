using System;
using System.Collections.Generic;
using System.Linq;

namespace oceansStackStrategy;

/// <summary>
/// Volume profile / value area — a direct port of `ocean.pine`'s own `f_va`/`f_isLVN` functions
/// (`Quantower-storage/TradingView/ocean.pine`): bins volume by price, finds the max-volume bin as
/// the point of control, then expands outward alternately (whichever neighboring bin holds more
/// volume) until the accumulated share reaches the configured value-area percentage.
///
/// FED FROM HISTORICAL 1-MINUTE BARS, NOT LIVE TICKS — deliberately, and this matters: prior-day
/// and prior-week value areas must already be COMPLETE the instant this strategy attaches (they
/// describe an already-finished session), but Quantower has no historical tick/time-and-sales
/// backfill (the same limitation `PocEngine.cs` already documents for its own current-move POC).
/// Bars, unlike ticks, ARE available historically, so this engine's own `AddSample` is fed
/// `(Bar.Close or HL2, Bar.Volume)` from `HistoricalData`, matching Pine's own
/// `request.security_lower_tf(..., [hl2, volume])` downsampling. This is a deliberate exception to
/// this strategy's own "prefer real tick-level order flow over the Pine script's proxies" default
/// (see `AbsorptionTracker`, which DOES use live tick-classified delta) — volume-PROFILE
/// construction is a different need (requires history) than delta confirmation (wants live-forward
/// accuracy), and only bars can supply the former here. Do not "fix" this into tick-feeding; doing
/// so would silently leave prior-day/prior-week profiles empty on every fresh attach.
///
/// One instance per profile needed (daily NQ, weekly NQ, QQQ's own) — reusable/instantiable, same
/// shape as `PocEngine`/`DeltaTracker`, not static.
/// </summary>
internal sealed class ValueAreaEngine
{
    private readonly double binSize;
    private readonly Dictionary<int, double> bins = new();

    public ValueAreaEngine(double binSize) => this.binSize = binSize;

    /// <summary>Stays populated across a `Finalize` call — only `Reset` clears it. The LVN-ledge
    /// check needs the CURRENT, still-accumulating day's bins compared against YESTERDAY's already
    /// frozen edges, so clearing on every finalize would break that.</summary>
    public IReadOnlyDictionary<int, double> Bins => this.bins;

    public void AddSample(double price, double volume)
    {
        if (volume <= 0 || this.binSize <= 0) return;

        var key = (int)Math.Round(price / this.binSize);
        this.bins[key] = this.bins.TryGetValue(key, out var existing) ? existing + volume : volume;
    }

    public void Reset() => this.bins.Clear();

    /// <summary>Port of `f_va`. Does NOT clear <see cref="Bins"/> — the caller calls
    /// <see cref="Reset"/> explicitly right after capturing the result, mirroring Pine's own
    /// two-step `[a,b,c] = f_va(...); ...; devNQ.clear()`.</summary>
    public (double? Poc, double? Vah, double? Val) Finalize(double valueAreaPct)
    {
        if (this.bins.Count == 0) return (null, null, null);

        var keys = this.bins.Keys.OrderBy(k => k).ToArray();
        var n = keys.Length;
        var vols = new double[n];
        var total = 0d;
        var ip = 0;

        for (var i = 0; i < n; i++)
        {
            var v = this.bins[keys[i]];
            vols[i] = v;
            total += v;
            if (v > vols[ip]) ip = i;
        }

        var target = total * valueAreaPct;
        var acc = vols[ip];
        var up = ip;
        var dn = ip;

        while (acc < target && (up < n - 1 || dn > 0))
        {
            var vu = up < n - 1 ? vols[up + 1] : -1d;
            var vd = dn > 0 ? vols[dn - 1] : -1d;

            if (vu >= vd) { up++; acc += vu; }
            else { dn--; acc += vd; }
        }

        var poc = keys[ip] * this.binSize;
        var vah = (keys[up] + 0.5) * this.binSize;
        var val = (keys[dn] - 0.5) * this.binSize;

        return (poc, vah, val);
    }

    /// <summary>Port of `f_isLVN` — is the band of bins just beyond <paramref name="edge"/> (in the
    /// direction away from value, <paramref name="dir"/> = +1 above / -1 below) a low-volume node
    /// relative to this SAME profile's own busiest bin? Missing bins inside the band count as zero
    /// volume in the average (matches Pine dividing by the band's own bin COUNT, not how many bins
    /// were actually found).</summary>
    public bool IsLvnLedge(double edge, int dir, double lvnBandPoints, double lvnPct)
    {
        if (this.bins.Count == 0 || this.binSize <= 0) return false;

        var maxVolume = this.bins.Values.Max();
        if (maxVolume <= 0) return false;

        var k0 = (int)Math.Round(edge / this.binSize);
        var bandBins = Math.Max((int)Math.Round(lvnBandPoints / this.binSize), 1);

        var sum = 0d;
        var hit = 0;

        for (var j = 1; j <= bandBins; j++)
        {
            if (this.bins.TryGetValue(k0 + dir * j, out var v))
            {
                hit++;
                sum += v;
            }
        }

        return hit > 0 && (sum / bandBins) <= maxVolume * lvnPct / 100.0;
    }
}
