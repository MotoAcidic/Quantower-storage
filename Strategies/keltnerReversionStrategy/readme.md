# Keltner Reversion Strategy

Mean-reversion off the Keltner Channel band edge — one of three strategies split out of
`tvConfluenceStrategy` on 2026-09-12 so each of the user's TradingView indicators
(Trendlines.pine, keltnerChannel.pine, supportResistanceChannels.pine) could be run,
tuned, and backtested as its own independent strategy instead of voting together inside
one combined strategy.

Same math as `tvConfluenceStrategy`'s `EvaluateKeltnerReversion` — EMA basis +/- ATR
band; price at or beyond a band edge signals reversion back toward the basis.

---

## Running alongside trendlineBreakStrategy / srChannelBreakStrategy on the same account+contract

Quantower positions and orders belong to an account+symbol pair, not to a specific
strategy instance — `Core.Instance.Positions` returns every position on that
account+symbol, whichever strategy (or a human) opened it. Without something to tell
them apart, running all three of these strategies on the same account+contract at once
would mean each one's "am I already in a trade?" check sees the others' positions too —
only one could ever hold a position at a time.

Fixed by tagging every order this strategy places with `StrategyTag = "KeltnerReversion"`
via `PlaceOrderRequestParameters.Comment`, and filtering every
`Position`/`Order`/`Trade`/`OrderHistory` query by that same `Comment` — this strategy
only ever sees and manages what it placed itself.

**This has not been verified against a live Quantower session** (no running platform
connection was available while writing it). Before trusting this with real size next to
the other two strategies on the same account, confirm `Comment` actually propagates from
the placing order through to the resulting `Position`/`Trade`/`OrderHistory` records —
e.g. place one small test trade per strategy in sim and check each Position's `Comment`
field in Quantower's Positions panel. If it doesn't propagate, each strategy would
silently fall back to seeing every position on the account again, defeating the whole
point of the split.

---

## Parameters

| # | Name | Default | Notes |
|---|------|---------|-------|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Period | MIN5 | Chart timeframe |
| 3 | Start Point | −30 days | Historical data start date |
| 4 | Quantity | 1 | Contracts per trade |
| 5 | Keltner EMA Basis Length | 34 | Matches `movingAverageLength` in keltnerChannel.pine |
| 6 | Keltner ATR Length | 34 | Simplified to match the basis length by default |
| 7 | Keltner ATR Multiplier | 1.5 | Matches `atrMultiplierMin` in keltnerChannel.pine |
| 8 | Stop Loss (ticks) | 80 | Hard bracket SL |
| 9 | Take Profit (ticks, 0=off) | 0 | 0 = disabled; trailing handles exits by default |
| 10 | Trail Activate At (ticks) | 40 | Profit in ticks to activate trailing; 0 = disabled |
| 11 | Trailing Stop (ticks) | 20 | Pullback from peak before close; 0 = disabled |
| 12 | RTH Only | 0 | 0 = trade 24h; 1 = block new entries outside RTH window |
| 13 | RTH Start Hour (EST) | 9 | — |
| 14 | RTH End Hour (EST) | 16 | — |
| 15 | Max Daily Loss ($) | 0 | 0 = disabled; resets at 6 PM EST |
| 16 | Max Drawdown ($) | 2000 | Total equity drawdown ceiling since strategy start; 0 = off |

## Build / Deployment

- **Project:** `keltnerReversionStrategy/keltnerReversionStrategy.sln`
- **Output:** `C:\Quantower\Settings\Scripts\Strategies\keltnerReversionStrategy\keltnerReversionStrategy.dll`
- **Target:** .NET 10, targets the installed Quantower v1.146.18 `TradingPlatform.BusinessLayer.dll`
