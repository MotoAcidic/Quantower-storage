# Futures Pro Strategy

Multi-factor trend-following strategy for MES / ES / NQ futures. Designed for Quantower's live and paper trading environments.

---

## Summary

Combines five complementary filters to produce high-quality entry signals and stay flat during unfavorable conditions:

1. **EMA Cross (9/21)** — primary timing trigger, fires on bar close
2. **Trend EMA filter (200)** — only trade in the direction the 200 EMA agrees with
3. **RSI filter** — block entries at overbought/oversold extremes
4. **MACD histogram** — optional momentum confirmation
5. **RTH session gate** — optionally restrict new entries to regular trading hours

---

## Entry Logic (bar close, ALL conditions must pass)

| Direction | Conditions |
|-----------|-----------|
| **Long** | Fast EMA crosses above Slow EMA AND close > Trend EMA AND RSI < Overbought AND MACD histogram > 0 (if enabled) AND within RTH (if enabled) |
| **Short** | Fast EMA crosses below Slow EMA AND close < Trend EMA AND RSI > Oversold AND MACD histogram < 0 (if enabled) AND within RTH (if enabled) |

**Mean reversion modes (optional):**
- **Bounce (mode 1/3):** arms when price touches near the 200 EMA (`MeanRevTouchTicks`), then enters on the first EMA cross back in trend direction
- **Fade (mode 2/3):** arms when price is overextended from the 200 EMA (`MeanRevExtensionTicks`), enters a counter-trend cross targeting the 200 EMA
- Bounce auto-disarms if price moves too far away (2.5× the arm distance) before a cross fires

---

## Exit Logic

| Exit Type | Behavior |
|-----------|----------|
| **Reverse cross** | Closes current position; flips direction only if trend EMA AND all entry filters agree for the new side — otherwise just closes flat |
| **Trailing stop** | Activates once P&L reaches `TrailActivationTicks`; closes if price pulls back more than `TrailingStopTicks` from peak |
| **Hard SL** | Bracket stop loss placed at entry (`StopLossTicks`) |
| **Take Profit** | Optional bracket TP (`TakeProfitTicks`); disabled by default — trailing handles it |
| **Daily loss limit** | Real-time check (includes unrealized P&L); closes open position and halts new entries until next session reset at 6 PM EST |
| **Max drawdown** | Total equity drawdown from strategy peak; shuts strategy down permanently when breached |

---

## Parameters

| # | Name | Default | Notes |
|---|------|---------|-------|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Fast EMA | 9 | Fast EMA period for cross signal |
| 3 | Slow EMA | 21 | Slow EMA period for cross signal |
| 4 | Trend EMA Period | 200 | Trend direction filter; entries blocked against this |
| 5 | RSI Period | 14 | RSI lookback period |
| 6 | RSI Overbought | 70 | Blocks long entry if RSI ≥ this value |
| 7 | RSI Oversold | 30 | Blocks short entry if RSI ≤ this value |
| 8 | MACD Filter | 0 | 0=off, 1=on; histogram must agree with entry direction |
| 9 | MACD Fast Period | 12 | Only active when MACD Filter = 1 |
| 10 | MACD Slow Period | 26 | Only active when MACD Filter = 1 |
| 11 | MACD Signal Period | 9 | Only active when MACD Filter = 1 |
| 12 | Period | MIN5 | Chart timeframe for all indicators |
| 13 | Start Point | −30 days | Historical data start date |
| 14 | Quantity | 1 | Contracts per trade |
| 15 | Stop Loss (ticks) | 80 | Hard bracket SL; 80t = 20 pts on MES |
| 16 | Take Profit (ticks) | 0 | Fixed bracket TP; 0 = disabled (use trailing) |
| 17 | Trail Activate At (ticks) | 40 | Profit in ticks to activate trailing; 0 = disabled |
| 18 | Trailing Stop (ticks) | 20 | Pullback from peak before close; 0 = disabled |
| 19 | RTH Only | 0 | 0 = trade 24h; 1 = block new entries outside RTH window |
| 20 | RTH Start Hour (EST) | 9 | Inclusive start of RTH gate (9 AM EST) |
| 21 | RTH End Hour (EST) | 16 | Exclusive end of RTH gate (4 PM EST) |
| 22 | Max Daily Loss ($) | 0 | Dollar loss cap per session; 0 = disabled |
| 23 | Max Drawdown ($) | 2000 | Total equity drawdown limit (prop firm compliance); 0 = disabled |
| 24 | Max Trend EMA Distance (ticks) | 0 | Blocks trend entries if price is overextended from 200 EMA; 0 = disabled |
| 25 | MeanRev Mode | 0 | 0=off, 1=bounce, 2=fade, 3=both |
| 26 | MeanRev Bounce Arm (ticks) | 20 | Distance to 200 EMA that arms the bounce entry |
| 27 | MeanRev Fade Arm (ticks) | 60 | Distance from 200 EMA that arms the fade entry |

---

## Recommended Settings

### Baseline (start here — maximum entries, no exotic filters)
These settings disable every optional gate so you can see signals firing and verify order execution before adding filters.

| Parameter | Value | Reason |
|-----------|-------|--------|
| Period | MIN5 | Best balance of signal quality vs. noise on MES |
| Fast EMA | 9 | Standard; responsive without excessive whipsaw |
| Slow EMA | 21 | Standard; stable trend confirmation |
| Trend EMA | 200 | Standard trend filter |
| RSI OB/OS | 70 / 30 | Wide defaults; keep wide until tuning |
| MACD Filter | 0 (off) | Adds latency; disable until baseline validated |
| RTH Only | **0 (off)** | 24h trading; RTH=1 silences most overnight signals |
| Max Daily Loss | 0 (off) | Disable until you know typical daily swings |
| Max Drawdown | 2000 | Keep on for prop firm protection |
| Max Trend EMA Distance | **0 (off)** | 70t cap was blocking many valid entries; disable or set ≥ 100 |
| MeanRev Mode | 0 (off) | Add after baseline is confirmed |
| SL | 60–80 ticks | ~15–20 pts on MES |
| Trail Activate | 35 ticks | ~8.75 pts |
| Trailing Stop | 20 ticks | ~5 pts |

### Why most entries were being blocked (from live log analysis)
| Block Reason | Root Cause | Fix |
|---|---|---|
| "outside RTH window (9:00-16:00 EST)" | `RthOnly = 1`; most signals fire pre-market/overnight on MES | Set `RthOnly = 0` |
| "price above/below Trend EMA" | 200 EMA trend disagreement — crossed but price is on wrong side | Normal filter; expect 40–60% of crosses to be blocked in choppy markets |
| "78t from Trend EMA (max 70t allowed)" | `MaxExtensionTicks = 70` too tight for MES volatility | Set to 0 (off) or ≥ 100 |
| "Mean rev bounce disarmed" | Price bounced near but didn't generate a cross in time | Normal when `MeanRevMode` is on; disable if not actively using it |

### Tighter / Production Settings (MES 5m, after baseline verified)
| Parameter | Value |
|-----------|-------|
| RTH Only | 1 (on) |
| RTH Start / End | 8 / 17 (pre-market open to close) |
| MACD Filter | 1 (on) |
| Max Daily Loss | 500 |
| Max Drawdown | 2000 |
| Max Trend EMA Distance | 100 |
| MeanRev Mode | 1 (bounce only) |
| MeanRev Bounce Arm | 15 |
| SL | 60 ticks |
| Trail Activate | 30 ticks |
| Trailing Stop | 15 ticks |

---

## Known Behavior Notes

- **No trades on startup unless a cross fires:** The strategy only enters on a fresh cross at bar close after startup. If the 9/21 EMAs are already crossed when you start, trade will not fire until the next crossover event.
- **RTH gate uses live clock, not bar timestamp:** When running in backtest/replay mode, the RTH window check uses `DateTimeUtcNow`, so it evaluates the real-world current time, not the bar's historical time. Either disable RTH for backtests or be aware it only gates entries correctly in live mode.
- **Reverse cross flip is gated:** A reverse cross closes the position but only reopens the opposite direction if the trend filter AND all entry filters agree. If they don't, the strategy goes flat.
- **Daily loss reset at 6 PM EST:** Futures trading day boundary. `MaxDailyLoss` persists until 6 PM EST and then resets.
- **Max drawdown never resets:** `MaxDrawdown` is total equity from strategy start — it will permanently halt trading if breached. Restart the strategy instance to reset it.

---

## Build / Deployment

- **Project:** `futuresProStrategy/futuresProStrategy.sln`
- **Output:** `C:\Quantower\Settings\Scripts\Strategies\futuresProStrategy\futuresProStrategy.dll`
- **Target:** .NET 8, Release configuration
- **Platform:** Quantower v1.145.16+
