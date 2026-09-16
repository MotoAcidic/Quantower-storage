# Ocean Developed Profile

An ATAS port of the idea behind claysul's *Developed Volume Profile + HVN* on TradingView:
the volume profile of the periods that are **finished**, carried onto the chart you are trading.

Shows in ATAS as **Ocean → Oceans Developed Profile**.

    dotnet build OceansDeveloped.csproj -c Release
    cd _test && dotnet run -c Release      # math harness
    cd _test && ./mutate.sh                # mutation check, by hand
    ./deploy.ps1                           # test, build, smoke, copy the DLL

ATAS reads `%APPDATA%\ATAS\Indicators` only at startup. **Restart ATAS after deploying.**

## What it draws

Five period kinds, each independently switchable:

| Kind | Tag | What it covers |
|---|---|---|
| Previous day | `PD` | the last completed futures day, 17:00→17:00 Central |
| Previous week | `PW` | the last completed week, Sunday 17:00 → Friday's close |
| Previous month | `PM` | the last completed calendar month |
| Rolling window A | `5D` | the last N completed days, rolling at each 17:00 close |
| Rolling window B | `20D` | the same, at a second length |

For each: a histogram lane at the edge of the chart, and the levels that matter carried across
it — point of control, value area high and low, and the high-volume shelves.

Nothing in progress is ever drawn. A level that is still moving is not a level, and half of
today's profile says nothing about where today's auction settled. That is what "developed"
means and it is the whole point of the indicator.

## Two things it refuses to do

Both because the failure mode is a clean-looking chart rather than an error.

**It will not profile a period the loaded history does not cover.** A "previous month" built
from the eleven days you happen to have loaded has a confident point of control at a price the
month never agreed on, and looks exactly like a real one. Each period is checked against its own
boundary — 17:00 on the evening before it opens — and skipped with a note at the bottom of the
chart if the earliest loaded bar is later than that. `Draw periods history does not cover` turns
this off and labels the result `PARTIAL`; it is off by default and worth leaving off.

**It will not cut a period until the bar clock is settled.** Every boundary here is a clock
time, so a wrong UTC/local reading shifts whole sessions and draws a perfectly plausible profile
of the wrong day. `TimeContext` resolves it from the data — first against the wall clock, then
against the daily maintenance halt — and the indicator prints why it could not rather than
guessing.

## How it is built

**The day is the unit of account.** Every bar is folded to its futures trade date, each
completed date gets one profile, and that profile is cached forever because a day behind us
cannot change. A week, a month and both rolling windows are then *sums* of those day profiles
rather than four more walks over the bars. Volume at a price is additive over time, so nothing
is approximated by doing this — `MergingDaysEqualsBuildingFromTheSameTrades` in the harness
pins that down against a direct build from the same trades.

**It builds from the footprint, not from one-minute bars.** The original script reconstructs a
profile from 1m volume because Pine has nothing finer. ATAS publishes per-price volume per bar
via `GetCandle(bar).GetAllPriceLevels()`, so the ladder here is at tick resolution with no
resampling step to lose.

**History depth is the real constraint.** These are higher-timeframe profiles assembled from
chart bars, so a previous-month profile needs a month of bars actually loaded. On a 1-minute
chart that is around 30,000. If the chart is not loaded that deep, the month is skipped and the
status line says so — which is the intended behaviour, not a limitation to work around.

## Shelves

A high-volume node here is a **band**, not a tick. Every level at or above `Size (% of the
busiest level)` is a candidate; candidates within `Bridge dips up to` ticks of each other are
one shelf, because a single thin tick inside a shelf is noise and splitting on it reports two
levels where the market built one. Bands thinner than `Thinnest shelf` are dropped, and only the
heaviest `How many to mark` survive — marking every shelf is marking none.

Inside a shelf the histogram bar takes the period's own colour, so the shape and the shelves are
one read rather than two.

## Layout

Histogram lanes sit at the chosen edge, shortest period nearest the price, and never take more
than half the chart however many are switched on. Only the most recent instance of each kind
gets a lane; older retained instances contribute levels only, dimmed, and their levels stop
where the period that replaced them begins — so `Keep this many past instances` at 3 gives three
blocks of levels rather than a cross-hatch.

## Conventions

Central time everywhere, one zone, never two. The futures day rolls at 17:00, not midnight.
Period boundaries tile: one period's end is the next one's start exactly, so no session can fall
outside all of them or inside two.

Shared build rules for every `oceans-*` project: `~/dev/CLAUDE.md`. Project rules: `CLAUDE.md`.
