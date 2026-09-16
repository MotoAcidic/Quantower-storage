# CLAUDE.md

Ocean Profile — ATAS indicator drawing the higher timeframe's **traded** volume by price as a
heatmap. Two layouts: an edge column merging the last N periods, and one column per period sitting
at that period's bars. Started 2026-08-20. Appears as **Ocean → Oceans Profile**. See README.md.
Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`OceansProfileIndicator.cs` render + settings · `ProfileMath.cs` pure math · `PeriodClock.cs`
period boundaries · `TimeContext.cs` (copied from `oceans-market-view` — keep in sync) ·
`_smoke/` · `_test/`.

## Why it exists, and the core insight

The trader asked for market-by-order to see where big orders sit, then rejected the DOM-delta estimate
outright: *"i dont want estimates i need facts only."* This is the facts-only replacement.

**The factual answer to "where are the big orders" is absorption** — a price that traded heavily
while aggression stayed balanced. Size changed hands and price went nowhere, so something passive
was on the other side and got filled. That is proof of a *filled* order, which beats
market-by-order's displayed intention that can still be pulled.

## Encoding — colour-by-delta alone does NOT work, this shipped wrong once

Over three hours price oscillates through every price, buying and selling net off, and
delta/volume lands near zero at nearly every level. The whole column rendered grey; the trader's
verdict was *"this doesnt immediatly say who."* Two fixes, both kept:

1. **Default style is volume with a delta overlay** — a grey bar for everything that traded and a
   coloured bar for how much of it leaned, on the SAME scale. The gap between them is the part
   that changed hands without going anywhere. Absorption stops being a subtle hue and becomes a
   long grey bar with no colour in it.
2. **Hue mode scales lean against the profile's own spread** (`MaxLean`, ignoring levels under 5%
   of the busiest so a six-lot print cannot set the scale), never against an absolute 100%.

**A column of bars is not actionable on its own.** What made it useful: POC and the heaviest
absorption shelves carried ACROSS the chart as labelled rays, plus a plain-words verdict block.
Rays are capped to the **3 heaviest** shelves — marking every one turns signal into wallpaper.
Absorption thresholds are tight (70% of busiest, 15% lopsided) because loose ones flag the whole
POC area on a merged profile.

## Four correctness rules, all tested

1. **Analysis runs at full tick resolution; merge for DISPLAY only.** Footprint imbalance compares
   buyers at a price with sellers *one tick below* — the counterparties that actually met. Merging
   rows first nets them off inside the row and silently answers a different question.
2. **The profile array must be DENSE.** A tick that never traded is a real zero, not a missing row;
   a sparse array shifts every diagonal comparison onto the wrong price. (This is the mutation
   worth keeping in any future value-area code.)
3. **Refuse, never truncate.** An over-wide profile returns null rather than dropping levels — a
   truncated profile can put the POC somewhere it never was and looks perfectly plausible.
4. **POC ties keep the lower price.** A POC flickering between two equal levels as ticks land is
   worse than an arbitrary but fixed choice.

## Time

**The futures day rolls at 17:00 Central, not midnight** — cutting at midnight splits every
overnight session in two. Cash/overnight split is 08:30–15:00, with overnight keyed to the close
that opened it so the hours either side of midnight stay in one profile.

**Time-zone dependence is per-period, and the distinction matters:** 15m/30m/1h land on the same
instants under any whole-hour offset and draw without a resolved bar clock. 2h/4h/session/day do
not, and **refuse to draw** until `TimeContext` settles it — a shifted profile looks clean and
plausible, which is the dangerous failure mode.

## Data source and caching

Reflected, not documented: `GetCandle(bar).GetAllPriceLevels()` returns
`PriceVolumeInfo { Price, Volume, Bid, Ask, Ticks, Between, Time }`. Also on `IndicatorCandle`:
`MaxVolumePriceInfo` (bar POC), `ValueArea`, `Delta`, `MaxDelta`/`MinDelta`, `VWAP`. Full footprint
data is available per bar — no file bridge needed.

Cache is per-period, signed by (bar range + newest bar's volume), so only the period in progress
rebuilds. An early version cleared the merged edge-column entry every frame and rebuilt ~180 bars
of levels per repaint — check for that shape of bug when a render feels heavy.
