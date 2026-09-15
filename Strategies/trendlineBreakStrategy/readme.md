# Trendline Break Strategy

ATR-sloped trendline breakout off the nearest confirmed swing high/low — one of three
strategies split out of `tvConfluenceStrategy` on 2026-09-12 so each of the user's
TradingView indicators (Trendlines.pine, keltnerChannel.pine,
supportResistanceChannels.pine) could be run, tuned, and backtested as its own
independent strategy instead of voting together inside one combined strategy.

Same math as `tvConfluenceStrategy`'s `EvaluateTrendlineBreak` (LuxAlgo "Trendlines with
Breaks") — a confirmed swing high/low anchors a trendline that decays by an ATR-derived
slope each bar; a close crossing back through it is the breakout signal.

---

## Running alongside keltnerReversionStrategy / srChannelBreakStrategy on the same account+contract

See `keltnerReversionStrategy/readme.md`'s section of the same name for the full
explanation — the short version: every order this strategy places is tagged
`StrategyTag = "TrendlineBreak"` via `PlaceOrderRequestParameters.Comment`, and every
`Position`/`Order`/`Trade`/`OrderHistory` query is filtered by that same `Comment`, so
this strategy only ever sees and manages what it placed itself. **Not verified against a
live Quantower session** - confirm `Comment` actually propagates through to `Position`
before relying on this alongside the other two strategies on the same account.

---

## Parameters

| # | Name | Default | Notes |
|---|------|---------|-------|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Period | MIN5 | Chart timeframe |
| 3 | Start Point | −30 days | Historical data start date |
| 4 | Quantity | 1 | Contracts per trade |
| 5 | Swing Lookback (bars) | 14 | `ta.pivothigh/low` lookback, matches `length` in Trendlines.pine |
| 6 | Slope Multiplier | 1.0 | Matches `mult` in Trendlines.pine (ATR-based slope only - Stdev/Linreg modes weren't ported) |
| 7 | Stop Loss (ticks) | 80 | Hard bracket SL |
| 8 | Take Profit (ticks, 0=off) | 0 | 0 = disabled; trailing handles exits by default |
| 9 | Trail Activate At (ticks) | 40 | Profit in ticks to activate trailing; 0 = disabled |
| 10 | Trailing Stop (ticks) | 20 | Pullback from peak before close; 0 = disabled |
| 11 | RTH Only | 0 | 0 = trade 24h; 1 = block new entries outside RTH window |
| 12 | RTH Start Hour (EST) | 9 | — |
| 13 | RTH End Hour (EST) | 16 | — |
| 14 | Max Daily Loss ($) | 0 | 0 = disabled; resets at 6 PM EST |
| 15 | Max Drawdown ($) | 2000 | Total equity drawdown ceiling since strategy start; 0 = off |

## Build / Deployment

- **Project:** `trendlineBreakStrategy/trendlineBreakStrategy.sln`
- **Output:** `C:\Quantower\Settings\Scripts\Strategies\trendlineBreakStrategy\trendlineBreakStrategy.dll`
- **Target:** .NET 10, targets the installed Quantower v1.146.18 `TradingPlatform.BusinessLayer.dll`
