using System;
using System.Collections.Generic;
using System.Linq;

namespace LevelsMap;

/// <summary>One option contract's own chain data — strike/type/open interest/implied
/// volatility/expiration. Deliberately NOT Massive-shaped: this engine takes plain numbers, not
/// Massive's own JSON contract, so the math stays testable independent of the data source (the
/// same "small, independently-readable, decoupled engine" convention this project already uses
/// for VwapEngine/PocEngine).</summary>
internal readonly record struct OptionContract(double Strike, bool IsCall, double OpenInterest, double ImpliedVolatility, DateTime ExpirationUtc);

/// <summary>Zero Gamma, Call Wall, Put Wall, and Magnet for one chain slice (the full chain, or
/// the 0DTE-only sub-slice).</summary>
internal readonly record struct GexLevels(double? ZeroGamma, double? CallWall, double? PutWall, double? Magnet);

/// <summary>Reference type deliberately (not a struct like <see cref="GexLevels"/>) — this is
/// the snapshot held behind a `volatile` field in the indicator, same "volatile drawable" pattern
/// used throughout this codebase, which requires a reference type.</summary>
internal sealed record GexResult(GexLevels FullChain, GexLevels ZeroDte, double? VolSkew)
{
    public static readonly GexResult Empty = new(default, default, null);
}

/// <summary>
/// Dealer gamma exposure (GEX): Zero Gamma, Call Wall, Put Wall, Magnet, Vol Skew, and 0DTE-only
/// versions of the first four — the operator's own ask (2026-10-08/09), after confirming via
/// `AskUserQuestion` that Massive (not free CBOE data) is the chosen source, and that the Magnet/
/// 0DTE bonus metrics should be included alongside the original four.
///
/// METHODOLOGY, independently reimplemented from the PUBLIC, documented approach in
/// github.com/itsfabtrading/gex-multi (Apache-2.0) and its vendored `gamma_exposure.py`
/// (MIT, from GMestreM/gex_data) — the well-known "SqueezeMetrics convention" already used across
/// most retail GEX dashboards, NOT any single vendor's proprietary refinement. This is an
/// independent C# implementation of the publicly-documented formulas, not a port of their Python
/// source — same "credit the idea, write your own code" convention this codebase already uses for
/// Vol-Step Ranges in `srm.pine` (itself "inspired by LuxAlgo... independent implementation, not
/// their code").
///
/// Expect this to be DIRECTIONALLY useful, not a precise match to any paid service's own numbers
/// — real services layer additional proprietary adjustments (dealer-positioning assumptions,
/// expiration weighting, possibly excluding/down-weighting 0DTE in the "main" numbers) this
/// engine does not attempt to replicate, because they aren't public.
///
/// NOT COVERED: "MVC" — no public definition found anywhere in gex-multi's own metric list or in
/// general GEX literature; left out rather than guessed at.
/// </summary>
internal static class GexEngine
{
    private const double ContractMultiplier = 100.0;
    private const double MinYearsToExpiry = 1.0 / 262.0; // gex-multi's own numerical-stability floor

    public static GexResult Compute(IReadOnlyList<OptionContract> contracts, double spot, DateTime asOfUtc, double riskFreeRate = 0.05)
    {
        if (contracts.Count == 0 || spot <= 0)
            return new GexResult(default, default, null);

        var fullChain = ComputeLevels(contracts, spot, asOfUtc, riskFreeRate);

        var nearestExpiry = contracts.Min(c => c.ExpirationUtc.Date);
        var zeroDteContracts = contracts.Where(c => c.ExpirationUtc.Date == nearestExpiry).ToArray();
        var zeroDte = ComputeLevels(zeroDteContracts, spot, asOfUtc, riskFreeRate);

        var volSkew = ComputeVolSkew(contracts, spot);

        return new GexResult(fullChain, zeroDte, volSkew);
    }

    private static GexLevels ComputeLevels(IReadOnlyList<OptionContract> contracts, double spot, DateTime asOfUtc, double riskFreeRate)
    {
        if (contracts.Count == 0) return default;

        // Net (signed — calls positive, puts negative) and gross (unsigned) dollar gamma per strike.
        var netByStrike = new Dictionary<double, double>();
        var grossByStrike = new Dictionary<double, double>();

        foreach (var c in contracts)
        {
            if (c.OpenInterest <= 0 || c.ImpliedVolatility <= 0) continue;

            var gamma = BlackScholesGamma(spot, c.Strike, YearsToExpiry(c.ExpirationUtc, asOfUtc), c.ImpliedVolatility, riskFreeRate);
            var dollarGamma = gamma * c.OpenInterest * ContractMultiplier * spot * spot * 0.01;
            var signed = c.IsCall ? dollarGamma : -dollarGamma;

            netByStrike[c.Strike] = netByStrike.GetValueOrDefault(c.Strike) + signed;
            grossByStrike[c.Strike] = grossByStrike.GetValueOrDefault(c.Strike) + Math.Abs(dollarGamma);
        }

        if (netByStrike.Count == 0) return default;

        double? callWall = null, putWall = null, magnet = null;
        var bestPositive = double.MinValue;
        var bestNegative = double.MaxValue;
        var bestGross = double.MinValue;

        foreach (var (strike, net) in netByStrike)
        {
            if (net > bestPositive) { bestPositive = net; callWall = strike; }
            if (net < bestNegative) { bestNegative = net; putWall = strike; }
        }

        foreach (var (strike, gross) in grossByStrike)
        {
            if (gross > bestGross) { bestGross = gross; magnet = strike; }
        }

        var zeroGamma = FindZeroCrossing(netByStrike);

        return new GexLevels(zeroGamma, callWall, putWall, magnet);
    }

    /// <summary>"Interpolated zero-crossing of the net gamma profile nearest spot" — sweep strikes
    /// low to high, accumulate net GEX, and linearly interpolate between the two strikes straddling
    /// the sign flip. With more than one crossing (a genuinely lumpy chain), the one nearest the
    /// CURRENT cumulative trend close to spot is preferred by just taking the first crossing found
    /// while sweeping — matches the "nearest spot" framing without needing a second spot-distance
    /// pass.</summary>
    private static double? FindZeroCrossing(Dictionary<double, double> netByStrike)
    {
        var strikes = netByStrike.Keys.OrderBy(k => k).ToArray();
        if (strikes.Length < 2) return strikes.Length == 1 ? strikes[0] : null;

        var cumulative = 0.0;
        var prevStrike = strikes[0];
        var prevCumulative = cumulative;

        for (var i = 0; i < strikes.Length; i++)
        {
            var strike = strikes[i];
            cumulative += netByStrike[strike];

            if (i > 0 && Math.Sign(prevCumulative) != Math.Sign(cumulative) && prevCumulative != 0)
            {
                // Linear interpolation between (prevStrike, prevCumulative) and (strike, cumulative).
                var span = cumulative - prevCumulative;
                if (Math.Abs(span) > double.Epsilon)
                {
                    var t = -prevCumulative / span;
                    return prevStrike + (t * (strike - prevStrike));
                }
            }

            prevStrike = strike;
            prevCumulative = cumulative;
        }

        return null; // no sign change across the whole chain — net gamma never actually flips
    }

    /// <summary>Standard Black-Scholes gamma — the same formula for a call or a put (gamma is
    /// identical for both at a given strike/expiry/vol), public-domain mathematics, not sourced
    /// from any single implementation.</summary>
    private static double BlackScholesGamma(double spot, double strike, double yearsToExpiry, double impliedVol, double riskFreeRate)
    {
        var t = Math.Max(yearsToExpiry, MinYearsToExpiry);
        var d1 = (Math.Log(spot / strike) + ((riskFreeRate + (impliedVol * impliedVol / 2.0)) * t)) / (impliedVol * Math.Sqrt(t));
        var pdf = Math.Exp(-d1 * d1 / 2.0) / Math.Sqrt(2.0 * Math.PI);
        return pdf / (spot * impliedVol * Math.Sqrt(t));
    }

    private static double YearsToExpiry(DateTime expirationUtc, DateTime asOfUtc)
    {
        var days = (expirationUtc.Date - asOfUtc.Date).TotalDays;
        return Math.Max(days, 0) / 365.0;
    }

    /// <summary>Simple skew read: implied vol of the nearest-available ~5%-OTM put minus the
    /// nearest-available ~5%-OTM call, at the nearest expiration with contracts on both sides.
    /// Positive = puts pricier than calls (the usual "fear" skew). A simpler proxy than a proper
    /// delta-matched comparison, but doesn't need delta to be supplied by the data source.</summary>
    private static double? ComputeVolSkew(IReadOnlyList<OptionContract> contracts, double spot)
    {
        if (contracts.Count == 0 || spot <= 0) return null;

        var nearestExpiry = contracts.Min(c => c.ExpirationUtc.Date);
        var sameExpiry = contracts.Where(c => c.ExpirationUtc.Date == nearestExpiry).ToArray();

        var targetPutStrike = spot * 0.95;
        var targetCallStrike = spot * 1.05;

        var put = sameExpiry.Where(c => !c.IsCall && c.ImpliedVolatility > 0)
            .OrderBy(c => Math.Abs(c.Strike - targetPutStrike))
            .FirstOrDefault();

        var call = sameExpiry.Where(c => c.IsCall && c.ImpliedVolatility > 0)
            .OrderBy(c => Math.Abs(c.Strike - targetCallStrike))
            .FirstOrDefault();

        if (put.ImpliedVolatility <= 0 || call.ImpliedVolatility <= 0) return null;

        return put.ImpliedVolatility - call.ImpliedVolatility;
    }
}
