using System;

namespace oceansStackStrategy;

/// <summary>One closed 1-minute bar. Deliberately a SEPARATE type from Finch-Lite's own shared
/// `Bar` (OHLC only, no volume) rather than reusing it — almost everything this strategy does
/// (value area construction, absorption detection, the 50-bar volume SMA) needs each bar's own
/// Volume, which the shared type was never built to carry. Extending the shared struct instead
/// would be a breaking change to every existing positional `Bar(...)` call site in
/// finchDomScalpStrategy/FairValueGapEngine/OrderBlockEngine, well outside this strategy's own
/// scope.</summary>
internal readonly record struct Bar(DateTime OpenUtc, double Open, double High, double Low, double Close, double Volume);
