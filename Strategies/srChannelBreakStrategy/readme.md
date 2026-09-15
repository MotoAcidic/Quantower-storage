# S/R Channel Break Strategy

Breakout through a multi-touch support/resistance zone built from clustered swing
pivots — one of three strategies split out of `tvConfluenceStrategy` on 2026-09-12 so
each of the user's TradingView indicators (Trendlines.pine, keltnerChannel.pine,
supportResistanceChannels.pine) could be run, tuned, and backtested as its own
independent strategy instead of voting together inside one combined strategy.

Same math as `tvConfluenceStrategy`'s `EvaluateSrChannelBreak` (LonesomeTheBlue "Support
Resistance Channels") — clusters nearby confirmed swing pivots (within
`SR Max Channel Width` of each other) into zones; a zone with at least `Min Touches`
pivots is a validated S/R channel, and a close breaking through one is the signal.

---

## Running alongside keltnerReversionStrategy / trendlineBreakStrategy on the same account+contract

See `keltnerReversionStrategy/readme.md`'s section of the same name for the full
explanation — the short version: every order this strategy places is tagged
`StrategyTag = "SrChannelBreak"` via `PlaceOrderRequestParameters.Comment`, and every
`Position`/`Order`/`Trade`/`OrderHistory` query is filtered by that same `Comment`, so
this strategy only ever sees and manages what it placed itself. **Not verified against a
live Quantower session** - confirm `Comment` actually propagates through to `Position`
before relying on this alongside the other two strategies on the same account.

---

## What was simplified vs. the raw supportResistanceChannels.pine

Same simplification `tvConfluenceStrategy` already made: the pine script's 8-parameter
width/strength/pivot-source system was collapsed to 4 parameters (pivot lookback,
lookback window, a flat tick-based channel width, and min touches) - the Close/Open
pivot source option and per-pivot strength-by-touch-count weighting weren't ported.

## Parameters

| # | Name | Default | Notes |
|---|------|---------|-------|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Period | MIN5 | Chart timeframe |
| 3 | Start Point | −30 days | Historical data start date |
| 4 | Quantity | 1 | Contracts per trade |
| 5 | Pivot Lookback (bars) | 10 | Matches `prd` in supportResistanceChannels.pine |
| 6 | Lookback Window (bars) | 200 | How far back to scan for pivots |
| 7 | Max Channel Width (ticks) | 20 | Flat tick width a cluster of pivots must fit inside to form a zone |
| 8 | Min Touches | 2 | Matches `minstrength` (pivots per channel) |
| 9 | Stop Loss (ticks) | 80 | Hard bracket SL |
| 10 | Take Profit (ticks, 0=off) | 0 | 0 = disabled; trailing handles exits by default |
| 11 | Trail Activate At (ticks) | 40 | Profit in ticks to activate trailing; 0 = disabled |
| 12 | Trailing Stop (ticks) | 20 | Pullback from peak before close; 0 = disabled |
| 13 | RTH Only | 0 | 0 = trade 24h; 1 = block new entries outside RTH window |
| 14 | RTH Start Hour (EST) | 9 | — |
| 15 | RTH End Hour (EST) | 16 | — |
| 16 | Max Daily Loss ($) | 0 | 0 = disabled; resets at 6 PM EST |
| 17 | Max Drawdown ($) | 2000 | Total equity drawdown ceiling since strategy start; 0 = off |

## Build / Deployment

- **Project:** `srChannelBreakStrategy/srChannelBreakStrategy.sln`
- **Output:** `C:\Quantower\Settings\Scripts\Strategies\srChannelBreakStrategy\srChannelBreakStrategy.dll`
- **Target:** .NET 10, targets the installed Quantower v1.146.18 `TradingPlatform.BusinessLayer.dll`
