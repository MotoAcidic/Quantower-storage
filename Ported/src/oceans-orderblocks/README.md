# Ocean's Order Blocks

ATAS port of the Pine "MTF Order Blocks + Zone Delta" (OB Suite Δ), using the real footprint
where the Pine had to estimate.

    ./deploy.ps1          # test (176 checks), build, smoke, copy DLL. Restart ATAS after.

Chart it under **Ocean → Ocean's Order Blocks**.

## What it draws

Order blocks on **1H / 4H / D / W**, plus **session** blocks on the chart timeframe inside
08:30–15:00 Houston. Same rule as the Pine: an opposite-colour candle whose extreme is closed
through by the next candle's body. Zone is wick-to-wick or body-only. Mitigation is price
through the far side (wick or close). Border weight: W 3, D 2, the rest 1.

Right-edge label per block:

    4H ▲ 21460.25 · brk +2.1K · zΔ -840 / 3.1K · t2

| Field | Meaning |
|---|---|
| `4H ▲` | timeframe, demand (▲) or supply (▼) |
| `21460.25` | midline (50% / CE), also drawn dashed |
| `brk +2.1K` | real bid/ask delta of the breaker candle — who did the breaking |
| `zΔ -840 / 3.1K` | delta / volume traded at prices **inside the zone** since it went live, forming bar included |
| `fresh` / `t2` | untouched, or how many separate times price has come back in |

The solid line inside a block is the **in-zone POC**: the price inside it where the most
volume has traded. Fresh blocks draw at full opacity and tested ones dimmer.

Panel: forming bar's delta, and CVD (RTH, frozen after 15:00, or daily from the 17:00 reopen).
It also lists any timeframe that can't produce blocks and why.

## What changed from the Pine, and why

| Pine | Here | Why |
|---|---|---|
| Delta from 1m candle direction | Real bid/ask delta per bar and per price | ATAS has the footprint. |
| Zone Δ = whole-market CVD since confirmation | Δ and volume at prices inside the zone | The Pine number counted delta printed 200 points away. This version counts only what traded at the zone. |
| `request.security` + `lookahead_off` | Higher-timeframe bars built from chart bars, closed on their last chart bar | Pine loaded higher-timeframe blocks one bar late on history compared with live. Here they appear on the same bar both ways. |
| 09:30–16:00 New York session | 08:30–15:00 Houston | One zone. |
| Faded boxes left for TradingView to garbage-collect | Faded list capped by *Max faded kept* | Pine's collector deleted the oldest drawings, which were the active weekly blocks. |
| — | *Breaker delta must agree* (off by default) | Needs footprint data, which Pine doesn't have. |
| — | First-touch alert, once per block | Live prints only; silent on chart load. |

## Limits worth knowing

- **Higher-timeframe history comes from the chart.** An ATAS indicator can't request another
  timeframe, so weekly blocks need about three weeks of chart bars loaded (the first, partial
  week is thrown away), and daily blocks need three days. The panel says when it doesn't
  have enough. Raise the chart's days-to-load setting if W or D is missing.
- **Displacement filter needs 14 higher-timeframe bars of ATR.** With it on, weekly blocks
  need 14+ weeks of history. It's off by default, as it is in the Pine.
- 4H bars open at 17, 21, 01, 05, 09, 13 Houston, anchored to the reopen, which is how
  TradingView aligns CME futures. The 13:00 bar is cut short by the 16:00 halt.
- The chart timeframe should divide the higher timeframe evenly (1, 2, 3, 5, 15 min). A 7-min
  bar that straddles the hour goes into the bucket it opened in.
- Zone state (touches, mitigation, flow) moves on **closed** bars only, so history and live
  agree. The label's flow numbers include the forming bar; the status does not.

## Files

| File | ATAS types? | What |
|---|---|---|
| `OrderBlocksMath.cs` | no | buckets, HTF builder, detector, ATR, zones, flow, CVD, engine |
| `OrderBlocksClock.cs` | no | bar clock resolver, ported unchanged from oceans-anchor |
| `OceansOrderBlocksIndicator.cs` | yes | settings, fold, live pass, alerts |
| `OceansOrderBlocks.Render.cs` | yes | drawing only |
