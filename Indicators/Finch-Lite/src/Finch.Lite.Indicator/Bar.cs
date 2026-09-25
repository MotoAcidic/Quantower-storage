using System;

namespace FinchLite;

/// <summary>One closed bar, timeframe-agnostic — the shape <see cref="OrderBlockEngine"/> and
/// <see cref="FairValueGapEngine"/> both need from a platform <c>HistoricalData</c> series.
///
/// MOVED 2026-09-25 into its own file, out of <c>OrderBlockEngine.cs</c> — the
/// `finchDomScalpStrategy` strategy compiles in <c>FairValueGapEngine.cs</c> by source but has no
/// use for order blocks at all; keeping <c>Bar</c> separate means the strategy doesn't also pull
/// in the (irrelevant to it) <see cref="OrderBlockEngine"/> class just to get this struct.
/// </summary>
internal readonly record struct Bar(DateTime OpenUtc, double Open, double High, double Low, double Close);
