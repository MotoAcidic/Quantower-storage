# EMA Cross Strategy — Settings Guide (5m MES)

**Instrument:** Micro E-mini S&P 500 Futures (MES)  
**Timeframe:** 5 minutes  
**Tick size:** 0.25 index pts = $1.25 / contract

Each setting is listed in the exact order it appears in the Quantower strategy panel.

---

## 1. Symbol
**Set to:** `MESH6` (or current front-month MES contract)  
The instrument the strategy trades. Must match the chart you are watching.

---

## 2. Account
**Set to:** Your Topstep 50K account  
Determines which account orders are routed to. Must be on the same connection as the symbol.

---

## 3. Micro EMA
**Recommended:** `5`  
The fast EMA. Crossovers between this and the Mid EMA generate all entry and exit signals. 5 is the standard setting from the original Pine script. Going lower (e.g. 3) increases signal frequency but produces more false crosses in choppy markets.

---

## 4. Mid EMA
**Recommended:** `29`  
The slow EMA. EMA gap between Micro and Mid is what the weakness exit watches. 29 is calibrated to the original Pine script. Going higher (e.g. 50, 100) delays entries and exits; going lower increases noise.

---

## 5. Weakness Bars
**Recommended:** `2`  
How many consecutive bars of a **shrinking EMA gap** trigger an exit. At 2 on 5m that is 10 minutes — fast enough to cut a bad trade before it bleeds out, but slow enough not to shake out during a normal mid-trend pause. Increase to 3 if the strategy exits too early on strong trend days.

---

## 6. Period
**Set to:** `5 Min`  
The bar timeframe for all signal logic. Keep this matched to your chart. The EMA periods (5 and 29) work well on 5m — fewer false crosses than 3m, each bar filters more noise, and the trailing stop has room to breathe before activating.

---

## 7. Start Point
**Recommended:** `5 days ago` (e.g. `3/7/2026`)  
How far back historical data is loaded on startup. At 5m, 5 days is only ~480 bars — loads near-instantly and gives the EMAs far more than the 50–100 bars they need to stabilise. No benefit to going further back, and longer history causes unnecessary startup lag on Topstep's feed.

---

## 8. Stop Loss (ticks, safety net)
**Recommended:** `80`  
This is a hard backstop only — the trailing stop and weakness bars are the real exits. At MES 5m, a normal bar can swing 30–60 ticks. 80 ticks ($100/contract) gives about 1.5 bars of room — survives a normal retest without getting hunted, but caps the damage on a genuinely failed trade. Verify this fits inside your Topstep daily loss limit at your chosen contract size.

---

## 9. Take Profit (ticks, 0 = disabled)
**Recommended:** `0` (disabled)  
A fixed TP cap prevents runners. With a trailing stop active, letting winners run is the edge. Only enable this if you want a guaranteed minimum exit level — e.g. `40` for scalping sessions where you know the range is tight.

---

## 10. Off-Hours Trail Activation (ticks)
**Recommended:** `30`  
The strategy only uses these values when the current EST time is **outside** both the Asia and NY windows (roughly 4 PM – 7 PM EST). This is a dead zone with thin volume and narrow ranges. 30 ticks ($37.50) is reasonable — not so tight it fires on noise, not so loose it gives back the whole move.

---

## 11. Off-Hours Trailing Stop (ticks from peak)
**Recommended:** `15`  
Once the Off-Hours trail activates, if price pulls back more than 15 ticks from the best price seen, the position closes. 15 ticks = $18.75/contract. In dead-zone hours small reversals happen fast — 15t locks in most of whatever gain exists.

---

## 12. Exit Mode (0=WeaknessBars, 1=TrailingStop, 2=Both)
**Recommended:** `2` (Both)  
- **0 (WeaknessBars only):** Exits when the EMA gap narrows for N consecutive bars. No trailing stop. Good for choppy, range-bound days where the trailing stop would whip out.  
- **1 (TrailingStop only):** No weakness exit. Stays in until the trailing stop hits. Can sit in a bad trade a long time if the gap never narrows to trigger exit.  
- **2 (Both):** Weakness bars cuts losers early; trailing stop locks in runners. This is the best general setting. Whichever fires first exits the trade.

---

## 13. Impulse Filter (ticks, 0 = off)
**Recommended:** `35`  
When a cross bar's body (open-to-close distance) is larger than this, the entry is **deferred** — the strategy waits for a pullback to the Micro EMA before entering. On 5m, a normal trending bar body is 20–35 ticks, so using 25 (the 3m setting) would defer almost every cross. At 35 ticks only genuinely explosive impulse bars defer — CPI prints, FOMC spikes, gap opens — while normal trend crosses enter immediately.

---

## 14. Mid EMA Touch (ticks from Mid EMA, 0 = off)
**Recommended:** `5`  
After a **non-reverse exit** (weakness bar, SL, trailing stop), the strategy watches for price to pull back and wick within this many ticks of the 29 EMA, then confirms a bounce, then re-enters in the original trend direction. This catches the "pull back and reload" move after a trend is interrupted. At 5 ticks it arms on a close wick touch. Set to 0 if you do not want any re-entries after exits — only fresh crosses will trigger entries.

---

## 15. Weakness Close % (0 or 100 = close all)
**Recommended:** `50`  
On a weakness bar signal, close this percentage of the position and move the stop to the partial close price. The remaining 50% stays open to catch runners if the trend resumes. Set to `0` or `100` to close the entire position on weakness (original behaviour). Set to `75` if you want to bank more and leave less exposed.

---

## 16. Asia Session Start (EST hour)
**Recommended:** `19` (7:00 PM EST)  
Start of the Asia session trailing override. MES futures track the overnight Globex session. Asia volume picks up around 6–7 PM EST with the Tokyo open.

---

## 17. Asia Session End (EST hour)
**Recommended:** `3` (3:00 AM EST)  
End of Asia session. London open begins around 3–4 AM EST and transitions to the European session.

---

## 18. Asia Trail Activation (ticks, 0 = use default)
**Recommended:** `20`  
During Asia hours, the overnight range is typically 20–40 points on MES. A 20-tick activation (5 pts, $25/contract) means you need at least a half-point move in your favour before the trailing stop arms. This prevents the stop from triggering on a barely-open position when spreads are wide overnight.

---

## 19. Asia Trailing Stop (ticks, 0 = use default)
**Recommended:** `10`  
Once the 20-tick activation is hit, a 10-tick pullback from the peak closes the position. Asia moves are short-lived — locking in 10 ticks of the gain is appropriate. If you want to chase overnight trend moves, increase to 15–20, but you will give back more on the reversals that are common in Asia hours.

---

## 20. NY Session Start (EST hour)
**Recommended:** `8` (8:00 AM EST)  
Pre-market NY session begin. Economic data (jobs reports, CPI, FOMC) regularly drops at 8:30 AM EST, and the futures open at 9:30 AM EST. Starting at 8 AM captures pre-market momentum moves.

---

## 21. NY Session End (EST hour)
**Recommended:** `16` (4:00 PM EST)  
Cash market close. Volume drops sharply after 4 PM. The wider NY trail settings are appropriate through the full RTH session.

---

## 22. NY Trail Activation (ticks, 0 = use default)
**Recommended:** `80`  
On 5m MES, each bar averages 30–60 ticks so the initial post-cross move easily runs 60–100 ticks before the first meaningful pullback. An 80-tick activation (20 pts, $100/contract) ensures the trailing stop doesn't arm during normal bar-to-bar breathing and only locks in profit once a real move is developing.

---

## 23. NY Trailing Stop (ticks, 0 = use default)
**Recommended:** `40`  
Once activated, a 40-tick pullback from peak closes the position. 40 ticks = 10 points = $50/contract. On 5m a single bar retracement can easily be 30–40 ticks without the trend reversing — using 30t (the 3m setting) would exit on normal bar noise. 40t gives the runner room to breathe through a single weak bar while still locking in the bulk of the move.

---

## 24. Retrace Touch (ticks from Micro EMA, 0 = 1-bar confirm)
**Recommended:** `5`  
When an impulse cross fires (body ≥ Impulse Filter) this controls how close to the **Micro EMA (5)** price must pull back before arming the retracement re-entry. At 5 ticks, any bar that closes within 1.25 pts of the 5 EMA arms the watch. The strategy then enters when price bounces back in trend direction. Setting to 0 falls back to the original 1-bar defer: enter on the next bar if EMA alignment still holds, without waiting for a pullback. The retrace mode generally gives a better entry price.

---

## Summary — Recommended Settings for 5m MES

| # | Setting | Value | Changed from 3m? |
|---|---------|-------|------------------|
| 1 | Symbol | `MESH6` | — |
| 2 | Account | Your Topstep account | — |
| 3 | Micro EMA | `5` | — |
| 4 | Mid EMA | `29` | — |
| 5 | Weakness Bars | `2` | — |
| 6 | Period | `5 Min` | ✅ was `3 Min` |
| 7 | Start Point | 5 days ago | ✅ was 7 days |
| 8 | Stop Loss (ticks) | `80` | — |
| 9 | Take Profit (ticks) | `0` | — |
| 10 | Off-Hours Trail Activation | `30` | — |
| 11 | Off-Hours Trailing Stop | `15` | — |
| 12 | Exit Mode | `2` (Both) | — |
| 13 | Impulse Filter (ticks) | `35` | ✅ was `25` |
| 14 | Mid EMA Touch (ticks) | `5` | — |
| 15 | Weakness Close % | `50` | — |
| 16 | Asia Start (EST hour) | `19` | — |
| 17 | Asia End (EST hour) | `3` | — |
| 18 | Asia Trail Activation | `20` | — |
| 19 | Asia Trailing Stop | `10` | — |
| 20 | NY Start (EST hour) | `8` | — |
| 21 | NY End (EST hour) | `16` | — |
| 22 | NY Trail Activation | `80` | ✅ was `60` |
| 23 | NY Trailing Stop | `40` | ✅ was `30` |
| 24 | Retrace Touch (ticks) | `5` | — |

---

*Last updated: March 12, 2026 — updated from 3m to 5m for prop firm use*
