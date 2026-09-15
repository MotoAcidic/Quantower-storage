# TV Confluence Strategy

Combines three of your own TradingView indicators into one Quantower confluence-gated
entry system, for MES / ES / NQ / MNQ or any futures contract:

1. **Trendlines.pine** (LuxAlgo "Trendlines with Breaks") — a dynamic, ATR-sloped
   trendline drawn from the most recent confirmed swing high/low. A close breaking
   through it is a momentum/breakout signal.
2. **keltnerChannel.pine** (Keltner Channel) — EMA basis +/- ATR band. Price
   stretched beyond a band edge is a mean-reversion signal back toward the basis.
3. **supportResistanceChannels.pine** (LonesomeTheBlue "Support Resistance
   Channels") — clusters nearby swing pivots into multi-touch S/R zones; a close
   breaking through a validated zone is a breakout signal.

Each detector votes LONG, SHORT, or nothing on every bar close. An entry only fires
once at least `MinConfluencesRequired` of the *enabled* detectors agree on the same
direction — same confluence philosophy used in the DayTrader-Portable project's
`confluences.ts`, which this file's math is a direct, verified port of (same
formulas, same pivot definition, same edge-case handling — not a re-derivation).

---

## Design choices

- **Bar-close only.** Swing pivots and S/R zones are inherently "did the last N
  bars form a confirmed shape" questions — they're evaluated in `Hdm_OnNewHistoryItem`
  (bar close), not on every tick. Intrabar re-evaluation would just add noise.
- **Flat-only entries, no reverse-cross flip.** Unlike the EMA-cross-style
  strategies elsewhere in this repo (`emaCrossStrategy`, `futuresProStrategy`),
  this strategy does **not** flip position on a fresh opposite-direction
  confluence. Once in a trade it's managed purely by stop loss / take profit /
  trailing stop / daily-loss / max-drawdown until flat again, then the next bar
  close is free to fire a new signal. This is a simpler, more predictable first
  version — a reverse-on-signal mode could be added later if wanted.
- **Confluences are independently toggleable.** Turn any of the three off
  (`Use Trendline Break` / `Use Keltner Reversion` / `Use S/R Channel Break`) to
  run this as a 1- or 2-indicator strategy instead of all three.

---

## Entry Logic (bar close)

| Direction | Condition |
|-----------|-----------|
| **Long**  | At least `MinConfluencesRequired` enabled detectors fire, and more of them vote LONG than SHORT |
| **Short** | At least `MinConfluencesRequired` enabled detectors fire, and more of them vote SHORT than LONG |
| (no trade) | Fewer than the minimum fire, or votes tie |

---

## Exit Logic

| Exit Type | Behavior |
|-----------|----------|
| **Hard SL** | Bracket stop loss placed at entry (`StopLossTicks`) |
| **Take Profit** | Optional bracket TP (`TakeProfitTicks`); 0 = disabled |
| **Trailing stop** | Activates once P&L reaches `TrailActivationTicks`; closes if price pulls back more than `TrailingStopTicks` from peak |
| **Daily loss limit** | Real-time check (includes unrealized P&L); closes open position and halts new entries until next session reset at 6 PM EST |
| **Max drawdown** | Total equity drawdown from strategy start; halts trading permanently when breached (restart the strategy instance to reset) |

---

## Parameters

| # | Name | Default | Notes |
|---|------|---------|-------|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Period | MIN5 | Chart timeframe for all three detectors |
| 3 | Start Point | −30 days | Historical data start date |
| 4 | Quantity | 1 | Contracts per trade |
| 5 | Use Trendline Break | 1 | 0=off, 1=on |
| 6 | Trendline Swing Lookback (bars) | 14 | `ta.pivothigh/low` lookback, matches `length` in Trendlines.pine |
| 7 | Trendline Slope Multiplier | 1.0 | Matches `mult` in Trendlines.pine (ATR-based slope only — Stdev/Linreg modes weren't ported) |
| 8 | Use Keltner Reversion | 1 | 0=off, 1=on |
| 9 | Keltner EMA Basis Length | 34 | Matches `movingAverageLength` in keltnerChannel.pine |
| 10 | Keltner ATR Length | 34 | Simplified from the pine's separate `atrLength` (88) to match the basis length by default — tune independently as needed |
| 11 | Keltner ATR Multiplier | 1.5 | Matches `atrMultiplierMin` in keltnerChannel.pine |
| 12 | Use S/R Channel Break | 1 | 0=off, 1=on |
| 13 | SR Pivot Lookback (bars) | 10 | Matches `prd` in supportResistanceChannels.pine |
| 14 | SR Lookback Window (bars) | 200 | How far back to scan for pivots (simplified from the pine's 300-bar `prdhighest/lowest` width calc + separate 290-bar `loopback`) |
| 15 | SR Max Channel Width (ticks) | 20 | Simplified from the pine's `%`-of-300-bar-range `ChannelW` into a flat tick width — tune per instrument |
| 16 | SR Min Touches | 2 | Matches `minstrength` (pivots per channel) |
| 17 | Min Confluences Required | 2 | Of the *enabled* detectors, how many must agree |
| 18 | Stop Loss (ticks) | 80 | Hard bracket SL |
| 19 | Take Profit (ticks, 0=off) | 0 | 0 = disabled; trailing handles exits by default |
| 20 | Trail Activate At (ticks) | 40 | Profit in ticks to activate trailing; 0 = disabled |
| 21 | Trailing Stop (ticks) | 20 | Pullback from peak before close; 0 = disabled |
| 22 | RTH Only | 0 | 0 = trade 24h; 1 = block new entries outside RTH window |
| 23 | RTH Start Hour (EST) | 9 | — |
| 24 | RTH End Hour (EST) | 16 | — |
| 25 | Max Daily Loss ($) | 0 | 0 = disabled; resets at 6 PM EST |
| 26 | Max Drawdown ($) | 2000 | Total equity drawdown ceiling since strategy start; 0 = off |

---

## What was deliberately simplified vs. the original .pine scripts

- **Trendlines.pine**'s `Stdev` and `Linreg` slope-calculation modes weren't
  ported — only `Atr` (the default). Add them later if the ATR-based slope
  underperforms in testing.
- **keltnerChannel.pine** is actually three indicators bolted into one script
  (Keltner Channel + an unlabeled "overbought/oversold circle" reversal marker +
  a WaveTrend oscillator). Only the Keltner Channel band itself was ported here
  — WaveTrend already exists as a separate confluence (`wavetrend_cross`) in the
  DayTrader-Portable project if you want that ported here too later.
- **supportResistanceChannels.pine**'s 8-parameter width/strength/pivot-source
  system was collapsed to 4 parameters (pivot lookback, lookback window, a flat
  tick-based channel width, and min touches) — the `Close/Open` pivot source
  option and per-pivot strength-by-touch-count weighting weren't ported.

None of these are hard to add — they just weren't part of the base confluence
detection the DayTrader-Portable port (which this file mirrors) already proved
out. Start here, watch it on a live/paper account, then decide if any of the
above are worth the added complexity.

---

## Build / Deployment

- **Project:** `tvConfluenceStrategy/tvConfluenceStrategy.sln`
- **Output:** `C:\Quantower\Settings\Scripts\Strategies\tvConfluenceStrategy\tvConfluenceStrategy.dll`
- **Target:** .NET 10, targets the installed Quantower v1.146.18 `TradingPlatform.BusinessLayer.dll`
- Builds clean with the plain **dotnet CLI** — `dotnet build` (or `dotnet build -c Release`)
  from this folder. Visual Studio and Quantower's Algo SDK VS extension are only
  needed for their project-creation wizard and F5 debugging convenience; neither
  is required to write or compile a strategy by hand, as this project (and every
  other one in this repo) proves.
