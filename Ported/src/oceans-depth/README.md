# Ocean Depth

A narrow gradient liquidity strip pinned to the right edge of the ATAS price panel. It shows
where the resting size is on each side, with the number written next to it, and marks the
biggest level so it catches the eye without adding another line to an already busy chart.

Appears in ATAS as **Ocean → Oceans Depth**.

## Why it exists

ATAS's own market-depth indicator has to be re-fitted to the screen every time the chart is
zoomed or the timeframe changed, because its rows are laid out in fixed pixels. This one derives
its whole layout from the live chart geometry on every frame:

- **Row height** comes from `PriceChartContainer.PriceRowHeight`, so a row is always exactly as
  tall as the price it covers.
- **Row bucketing** is chosen so a row is never thinner than *Minimum row height*. Zoomed in that
  is one tick per row; zoomed out, ticks merge automatically. This is what makes the same settings
  work on 1 minute, 5 minute and 1 hour without touching anything.
- **The colour scale** is renormalised each frame against the biggest level in view, so the
  gradient always uses its full range instead of washing out.

Rows are anchored to a multiple of the row size in *price* space, not to the bottom of the
window. Scrolling therefore slides rows across the screen instead of reshuffling which ticks
share a row — without that the strip shimmers whenever the chart moves.

## Reading it

- Bars grow leftward from the right edge. Length **and** colour both encode size, so a wall is
  long and bright at the same time.
- Bids use the green gradient, asks the red one. With **Scale = Shared** both sides are measured
  against the same number, so a heavy offer really does out-glow a light bid — that comparison is
  the point. **Per side** scales each side to its own maximum instead.
- Numbers sit just outside the left end of their own bar, so the biggest levels push their
  numbers furthest into the chart.
- The header above the strip reads e.g. `DOM 20 lvl  max 412` — the source, how many price levels
  the feed is actually publishing, and the size the gradient is scaled to.

### Persistence

Size that gets pulled does not vanish instantly: it holds at full brightness for *Hold*, then
fades to whatever is really there over *Fade*. Two reasons this is on by default.

1. MNQ's book changes many times a second. Drawn raw, the strip strobes and the numbers cannot
   be read.
2. A 400-lot that appears and disappears is information. The fade is the only way it is ever
   visible on a chart that repaints a few times a second.

Set both to `0` for the raw book.

The book is sampled at the **Refresh (ms)** rate, so size that appears *and* disappears faster
than that is missed entirely. Lower the refresh to catch more of it.

## Sources

**Depth of market (aggregated)** — total size per price. Every feed has it. This is the default.

**Market by order** — every individual order separately, which additionally gives:

- *Size shown → biggest order*: the largest single order at a price, rather than the total.
  300 lots as one order and 300 lots as thirty orders are very different things, and aggregated
  depth cannot tell them apart.
- *Add order count*: writes `240/3` — size, and how many orders make it up.

Not every feed carries market-by-order data. When it is selected and the feed is not sending it,
the strip says so on the chart, along with a count of how many aggregated depth updates *did*
arrive. Depth updates climbing while order updates stay at zero is proof the feed carries no
per-order data; both at zero means nothing is reaching the indicator at all, which is a different
problem. It does **not** quietly draw aggregated depth under a market-by-order label — a wrong
number here is worse than no number.

The subscription is made from `OnInitialize()`, which is where ATAS's own DOM (`DomV10`) makes
it. Requesting it from `OnRender` — the render thread, and much later in the indicator's life —
does not reliably deliver data.

**Biggest chunk added (estimated)** — the workaround for feeds with no per-order data. It watches
the total size at each price and records the biggest single *jump*. CME publishes an incremental
depth update per order action, so a lone 300-lot arriving shows up as one `+300` update rather
than as drift, and the jump is a usable estimate of one order.

It is fed from `MarketDepthChanged`, at full feed rate, **not** from the render-rate poll —
sampled a few times a second, separate orders merge into one delta and the estimate inflates.

Three things it deliberately does not do:

- The first update at a price is never counted as an add. Everything already resting when the
  indicator attaches arrived before it was watching; counting it would report the whole book as
  one enormous order on every startup.
- The estimate is capped at what is actually resting. Once a level is eaten below the recorded
  chunk, part of that chunk has gone and the number comes down with it.
- A level that empties completely forgets everything. Size returning is a fresh baseline, not an
  order appearing out of nowhere.

It is still an estimate. Several orders landing inside one update merge; one order split across
two updates is undercounted. The header reads `DOM est` whenever the number is inferred rather
than published, and the setting is named *estimated* for the same reason.

## Pairing it with the rest of the chart

- Turn ATAS's own market depth / DOM levels indicator **off**, or the two overlap on the same
  right edge.
- Default width is 90px. *Right margin* pushes the strip left if something else lives there.
- *Only when it is this many times the average* keeps the cross-chart line off the chart until
  something genuinely outsized shows up. Raise it if the line is drawing too often; set it to 1
  to always mark the biggest level.

## Layout

| File | |
|---|---|
| `DepthMath.cs` | Bucketing, fade and gradient. No ATAS types, so it is testable. |
| `OceansDepthIndicator.cs` | ATAS glue: the book, the timer, the rendering. |
| `_test/` | Drives `DepthMath` against brute-force references. |
| `_smoke/` | Constructs the indicator outside ATAS and prints every setting. |

## Deploying

```powershell
.\deploy.ps1
```

Runs the tests, builds, smoke-tests the constructor, and copies the DLL to
`%APPDATA%\ATAS\Indicators\`. **ATAS must be restarted** — it only reads that folder at startup.

The `Could not resolve type ... assembly may not be loaded` warning in
`%APPDATA%\ATAS\Logs\app_<date>.log` at startup is a red herring; every custom indicator gets it.
