# Ocean Profile

The higher timeframe's traded volume by price, as a heatmap, on the same chart you are trading.

Appears in ATAS as **Ocean → Oceans Profile**.

## Facts only

Everything drawn here printed on the tape. Volume at price, the bid/ask split behind it, and the
trade count are published facts — not resting orders that could be pulled, and not an estimate of
anything. Aggregating chart bars into a higher timeframe is exact summation.

Where that matters most is **absorption**. A level that traded heavily while the aggression stayed
balanced is evidence of a large *passive* order that actually got filled. On a feed with no
market-by-order data that is the honest answer to "where is the size" — and in one way it beats
market-by-order, because it is proof of a filled order rather than a displayed intention.

## Reading it

Each level draws **two bars on one scale**. The grey bar is everything that traded there. The
coloured bar is how much of it *leaned* — green when buyers were the aggressor, red when sellers
were. The **gap between them** is the part that changed hands without going anywhere.

| What you see | What it means |
|---|---|
| Long grey, long green | Buyers took size and price moved |
| Long grey, long red | Sellers hit size and price moved |
| **Long grey, almost no colour** | **Size traded, price went nowhere — absorption** |
| Short grey | Thin. Price passed straight through |

Colouring a single bar by delta instead was the first attempt, and it does not work on a profile
merged over hours: price oscillates through every price, buying and selling net off, and nearly
every level lands near balanced — so the whole column renders grey and says nothing. That mode
still exists under *Bar style*, but its hue is now scaled against the profile's own spread rather
than an absolute 100%, and levels under 5% of the busiest are ignored so one six-lot print cannot
set the scale.

### It says it in words too

A short block under the header reads the profile out: whether price is above, below or inside
value, how far the POC is, the nearest absorbed shelf each way, and whether anybody actually
carried the period. Turn it off with *Say it in words*.

### Levels carried across the chart

The POC and the heaviest absorption shelves are drawn as labelled rays running out of the column
and across to current price — a shelf where size was absorbed is only useful if you can see price
approaching it. Capped to the three heaviest by default; marking every one turns signal back into
wallpaper. Value-area edges are available but off.

### Marks

- **Point of control** — the heaviest price of the period. Ties keep the lower price, so it does
  not flicker between two equal levels as ticks land.
- **Value area** — the contiguous band around the POC holding 70% of the volume, drawn as a wash.
- **Absorption** — outlined. Volume at or above a share of the busiest level, with aggression no
  more lopsided than the balance setting. Both thresholds are yours to set.
- **Stacked imbalances** — a coloured tab on the edge of the row. A buy imbalance compares the
  buyers at a price with the sellers *one tick below* — those two are the counterparties that
  met. A single imbalance is noise; the default only marks runs of three or more.
- **High / low volume nodes** — shelves price accepted, and thin patches it crossed fast. Off by
  default; they add a lot of ink.
- **Unfinished auction** — a period high or low that traded on *both* sides, so the auction never
  found a price nobody would take. Off by default.

Numbers go on the heaviest rows only, just past the end of each bar, so the biggest levels push
their numbers furthest and land on the eye first.

## Layouts

**Edge column** — one column pinned to the left or right edge, merging the last N periods. Clear
of the candles. Pairs with Ocean Depth on the opposite edge: profile on one side for where size
*traded*, depth on the other for what is *resting now*.

**One column per period** — a column at each period's own bars, showing how that hour (or day, or
session) built its volume in place. Drawn behind the candles.

## The higher timeframe

A real clock period, so it means the same thing whichever chart it is dropped on: 1h is 60 bars on
a 1m chart and 12 on a 5m chart. Options are 15m, 30m, 1h, 2h, 4h, cash-session/overnight, and the
futures day.

The **futures day rolls at 5 PM**, not midnight — that is when CME reopens, and cutting at
midnight would split every overnight session in two and show two profiles where the market saw
one. The cash/overnight split is 08:30–15:00 Central, with the overnight block keyed to the close
that opened it so the hours either side of midnight stay in one profile.

### Time zones, and why some periods refuse to draw

Every boundary here is a clock time, so a wrong offset does not *look* wrong — it produces a
clean, plausible, silently misplaced profile. Whether ATAS stamps bars UTC or already local is
therefore resolved from the data (`TimeContext`, shared with Ocean Market View), never assumed.

15m, 30m and 1h land on the same instants under any whole-hour offset, so they draw regardless.
2h, 4h, session and day do not — those refuse to draw until the bar clock is settled, and say so
on the chart instead of guessing.

## Performance

Profiles are cached per period. The signature covers the bar range and the newest bar's volume,
so only the period in progress is ever rebuilt; finished periods are built once. Bar-to-period
keys are cached too, and both caches drop themselves if the chart is scrolled far enough to grow
them without bound.

The analysis always runs at **full tick resolution**. Rows are merged only for display, after the
fact, and a merged row is flagged if any tick inside it was. Running the imbalance rules on
pre-merged rows would be answering a different question and calling it a footprint.

## Layout

| File | |
|---|---|
| `ProfileMath.cs` | Profile building, value area, nodes, absorption, imbalances, merging. No ATAS types. |
| `PeriodClock.cs` | Cutting bars into higher-timeframe periods. Pure. |
| `TimeContext.cs` | Resolves the bar clock from evidence. Shared with Ocean Market View. |
| `OceansProfileIndicator.cs` | ATAS glue and the two renderers. |
| `_test/` | Drives the maths against brute-force references and invariants. |
| `_smoke/` | Constructs the indicator outside ATAS and prints every setting. |

## Deploying

```powershell
.\deploy.ps1
```

Tests, builds, smoke-tests the constructor, copies the DLL to `%APPDATA%\ATAS\Indicators\`.
**ATAS must be restarted** — it only reads that folder at startup.
