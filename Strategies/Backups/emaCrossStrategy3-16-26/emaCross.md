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
**Note: this only applies when LTF Confirm Bars (setting #27) is set to 0.** When LTF mode is active, impulse crosses are confirmed on the 1m chart instead and this setting is bypassed.

When LTF is disabled and an impulse cross fires (body ≥ Impulse Filter), this controls how close to the **Micro EMA (5)** price must pull back before arming the retracement re-entry. At 5 ticks, any bar that closes within 1.25 pts of the 5 EMA arms the watch. Set to 0 for the legacy 1-bar defer (enter on next bar if EMA alignment holds).

---

## 25. Trend EMA Period (0 = disabled)
**Recommended:** `0` (disabled)  
When non-zero, only long entries are allowed when price is above this EMA, and only shorts when below it. A valid concept but problematic on 5m MES — a 200-period EMA spans 16+ hours and price oscillates around it constantly, blocking both sides in alternation. Only experiment with shorter values like `50` (4 hours) and only in clearly one-directional sessions. Leave at `0` for normal trading.

---

## 26. Min EMA Gap at Entry (ticks, 0 = off)
**Recommended:** `0` (disabled)  
Requires at least this many ticks of separation between the 5 and 29 EMAs at entry, filtering out near-miss crosses where the EMAs are barely touching. Disabled by default — the LTF confirm (setting #27) already serves as a quality filter for impulse crosses, and the Min Gap risks blocking valid early trend crosses. Enable only if you're seeing many hair-trigger reversal signals on flat days.

---

## 27. LTF Confirm Bars (max 1m bars to wait after impulse cross, 0 = legacy)
**Recommended:** `5`  
This is the key entry timing improvement. When an **impulse cross** fires (cross bar body ≥ Impulse Filter), instead of waiting a full 5m bar or a retracement back to the EMA, the strategy drops to a **1m chart** and uses the same 5/29 EMAs there to confirm direction. It enters on the first 1m bar where the 1m EMAs are aligned with the cross — typically within 1–3 minutes rather than waiting up to 10 minutes.

If no 1m bar confirms within this many bars (5 minutes), the signal is considered stale and dropped. This prevents entering after the whole spike has already reversed.

Set to `0` to revert to the legacy behaviour (retrace watch or 1-bar confirm from setting #24).

**5m → 1m mapping is automatic.** For other timeframes: 15m uses 3m confirmation, 30m uses 5m, 1h uses 15m.

---

## 28. Swing Trail Lookback Bars (0 = off, e.g. 2)
**Recommended:** `2`  
Enables a **swing-structure trailing stop** that locks in profits at structurally meaningful levels — wick highs/lows — rather than a generic tick distance from peak price.

**How it works:**
- On every **bar close** while a position is open and `SwingTrailActivationTicks` profit has been reached, the strategy looks back this many bars and finds:
  - **Long:** the *lowest low* of the last N bars → becomes the new stop level
  - **Short:** the *highest high* of the last N bars → becomes the new stop level
- The stop **only ratchets in the trade direction** — it never moves against you (a rising stop on longs never pulls back down).
- The stop is **checked per tick** in real time. When price breaches it, the position is closed immediately via market order, then the swing fields reset.

**Setting to 2** means the stop sits below the lowest wick of the most recent 2 closed bars on a long — giving the trade room to breathe through normal one-bar retracements while still capturing the structure of the move. Setting to 1 is tighter (just the previous bar's low), setting to 3 or 4 gives more room on choppier moves.

**Coexistence with tick trail:** Both the swing trail and the existing tick-based sessions trail (settings #10–#23) can run simultaneously. Whichever fires first closes the position. On 5m MES with NY trail activation at 80 ticks, the swing trail (activation 20 ticks) will typically become the primary exit; the tick trail acts as a wider safety net for very large runners.

Set to `0` to disable swing trail entirely and rely solely on the session tick trail.

---

## 29. Swing Trail Activation (ticks profit to arm, 0 = immediate)
**Recommended:** `20`  
The swing stop doesn't arm until the position is this many ticks in profit. This prevents a very early wick (on the first 1–2 bars) from closing a good trade at a loss because the swing structure level happens to be below the entry at the start.

Once the profit threshold is exceeded for the first time, the swing trail arms immediately on the next bar close and stays armed for the life of the trade.

Set to `0` to arm from the very first bar close (use only if your entries are already well into profit quickly, e.g. after a strong impulse).

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
| 25 | Trend EMA Period | `0` (disabled) | ✅ new |
| 26 | Min EMA Gap (ticks) | `0` (disabled) | ✅ new |
| 27 | LTF Confirm Bars | `5` | ✅ new |
| 28 | Swing Trail Lookback Bars | `2` | ✅ new |
| 29 | Swing Trail Activation (ticks) | `20` | ✅ new |

---

*Last updated: March 13, 2026 — added swing-structure trailing stop (#28, #29): stop trails at wick high/low of last N bars, arms after 20-tick profit, replaces tick-trail as primary exit; added LTF 1m confirmation for impulse crosses (#27); Trend EMA and Min Gap filters kept at 0 (disabled); Retrace Touch now acts as legacy fallback only when LTF is off.*
