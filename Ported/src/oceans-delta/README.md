# Ocean Delta Cross

Four delta reads wired into one another, and one mark on the chart.

Shows in ATAS as **Ocean → Oceans Delta Cross MNQ**.

## Why it exists

ATAS ships Delta, Delta Bars, Cluster Search and a cumulative-delta-per-session indicator, and
none of them can talk to the others. You can stack all four on a chart and still have to do the
join in your head: was that flip the one that mattered, and did anyone actually pay for it?

This does the join. One indicator reads all four and produces one thing: **a cross where the
session delta flipped** — a vertical line on the bar that crossed, and a horizontal at the price.

## The four reads

| | What it is | Where it shows |
|---|---|---|
| **Bar delta** | Each bar's aggressor imbalance | Thin heat strip at the panel edge |
| **Session delta** | Running sum since the session opened | Thin heat strip above it |
| **The flip** | Where that sum changed sign | The cross |
| **Cluster search** | Which prices inside the crossing bar carried one-sided size | Grades the cross; marks on the flip bar |
| **Flip levels** | What older sessions' flips left on the price axis | Bands from the bar that made them |
| **Lines in the sand** | Prices that kept absorbing one-sided aggression | Horizontal lines with the side that stood there |

Nothing is drawn over the candles except the cross itself. A layer that covers price is the
wrong layer.

## How much room it takes

**`Layout` decides, and the honest default is small.** Four indicators stacked in four panels is
the problem this was built to fix; a ninety-pixel band of its own would just be the same problem
wearing one name.

| Layout | Cost | What you lose |
|---|---|---|
| **Crosses only** | 0 px | The band. The cross is still marked in full. |
| **Two thin strips** (default) | ~19 px | The *shape* of the session delta run — but not where it turned, which is the colour boundary |
| **Full session delta track** | ~80 px | Nothing |

The band is capped at a third of the panel whatever the settings say. A band that has pushed
price into a quarter of its own chart has stopped being a reference.

The readout matches: **`One line`** by default — session delta, the side, the last flip, in a
single line in the corner. `Full box` is the whole account, and is the only place a failed render
layer can report itself, so switch to it if something looks missing. Cross labels are one line by
default too; `Three lines` adds the confirmation distance and the cluster count, which is worth
it on one cross and not on six.

## The flip rule

A crossing is **armed**, not accepted.

1. Session delta takes the opposite sign. That bar is the crossing — remembered, nothing drawn.
2. It only becomes a flip once the sum has travelled **Confirm a flip after** contracts past
   zero. A crossing that comes back before then never happened and leaves no mark.
3. The cross is anchored to the bar that **crossed**, not the bar that confirmed. The
   confirmation is evidence about a moment that already passed; drawing it late would put the
   line on the wrong bar.

Session delta starts every session at zero, so the first move away from it is the session
choosing a side, not reversing one. That is not a flip by default — otherwise every session
opens with a cross on it. `Mark the session's first side too` turns it on.

Live, a forming bar can arm and confirm a flip and then fill back in and take it away again. The
engine rewinds and the cross disappears, which is correct: the closed bar never crossed.

## The horizontal, and what it is not

The exact tick at which the running sum passed zero **is not recoverable from bar data**. A bar
records what traded at each price, never in what order. So there is no interpolated price here,
and there never will be. The horizontal is one of two things, and the label always says which:

- **Close** (default) — the crossing bar's close. The price the market was at when the flip was
  on the record. A fact.
- **Flip cluster** — the price level inside that bar that carried the most delta of the incoming
  side. Where the flip was *paid for*. A reading, and labelled as one. If no level qualified,
  this falls back to the close and the label says `close - no cluster carried it` rather than
  quietly drawing a line at a price nothing supports.

The readout also prints how far through the crossing bar's net delta zero was reached. That is a
fact about the order of *contracts*, not of prices, and is deliberately never turned into one.

## Cluster search

Inside the crossing bar, a level counts as a cluster when it clears all three of:

- **volume** — contracts traded at that price, both sides together
- **delta** — how far apart the two sides were there, in contracts
- **lean** — that same difference as a share of the level's own volume

The lean bar is what stops a 400-lot level split 210/190 counting as one side doing something,
while letting a 60-lot level split 55/5 through.

A flip with no qualifying cluster behind it is drawn **dashed**, not hidden. A flip nobody paid
for is still worth seeing — it is just worth less. `No qualifying cluster, no cross` hides them
if you would rather.

## Flip levels — the sessions before this one

A cross is a moment, and the moment stops mattering when its session ends. **The price does not.**

Sessions older than the one in progress keep their flips as horizontal bands — no vertical, no
time, just the level and the day it was made. The band is the **span of the clusters that carried
the flip**: where the size that turned the session actually traded. A flip with no qualifying
cluster has no span to take, so it collapses to a dashed line at the price and is drawn as the
thin thing it is, rather than padded out into a band nothing traded in.

**Levels that overlap across sessions are merged and counted** — `1.2734  Wed 20  ×2` means two
different sessions turned on that price. Those are the ones worth having, and they are drawn
heavier. Two flips from the *same* session never add to that count: that is one session changing
its mind, not two sessions agreeing.

`Sessions to keep` (default 3) sets how far back it goes, `Most levels to draw` (default 8) caps
the ink, and the cap always keeps the newest.

## Lines in the sand — icebergs from the footprint

Prices that kept absorbing one-sided aggression across a window of bars. A level qualifies when
all five hold:

- **volume** at the price across the window
- **share** of everything the window traded — keeps a quiet hour and a news bar comparable
- **lean** — how one-sided the aggression into it was
- **bars** — it has to have happened in several bars. One enormous print is a big trade, not a
  level that kept reloading, and this is the only thing separating the two
- **it held** — no bar *closed* through it against the passive side. Wicks through are the level
  working, not breaks

The label shows the passive side, not the aggressor: `^ 1.2734 ×7 4.1K` means sellers kept hitting
a bid there and it kept being there — someone was buying. `v` is the reverse.

**This is evidence, not an identification.** There is no order-book data behind it. A genuine
refreshing iceberg leaves exactly this footprint — and so does one large resting order that was
never replenished, and so does a price that simply kept attracting business. What is measured is
repeat one-sided absorption at a single price, which is the part you can actually trade against.

If you want to confirm a specific one, the MBO-based `IcebergsTracker` already running in ATAS
writes real order-level events to `%APPDATA%\ATAS\IndicatorData\IcebergsTracker\`. That is the
ground truth; this is the footprint's shadow of it, and it works on history, which MBO does not.

The window is a **fixed number of bars ending at the newest**, never the visible range — a level
that moved when you scrolled would not be a level.

## Reading the chart

- **Solid cross** — session delta flipped and the cluster search found size behind it
- **Dashed cross** — it flipped, but on scraps
- **Filled square** — the intersection: the bar and the price
- **Bar to the left of the flip bar, with a number** — the biggest qualifying cluster and its delta
- **Strips at the bottom** — session delta above, bar delta below; colour is the sign, weight is
  the size. Where the session strip turns over **is** the crossing, and the vertical carries down
  through it so the two read as one event

- **Blue band with a date** — a flip level from an older session; heavier and marked `×2` when
  more than one session turned on it
- **Amber line with `^` or `v`** — a price that kept absorbing; the arrow is the side that stood
  there. Dashed and faded once it has been closed through

Everything is in the readout in words as well: session delta now, the side, an arming crossing
and how much further it has to go, the last flip in full, the kept levels and the absorbing
prices.

## Times

One zone, Houston, everywhere. Bar stamps are resolved to it and the labels print it. The
resolver never guesses — if it cannot tell whether the platform stamps bars UTC or already
local, it says so in the readout instead of printing a plausible time on the wrong bar. Set
`Bar clock` by hand if it asks.

## Build and deploy

    dotnet build OceansDelta.csproj -c Release
    cd _test && dotnet run -c Release     # 123 checks, no ATAS needed
    ./deploy.ps1                          # test, build, smoke, copy DLL

**Restart ATAS after deploying** — it reads `%APPDATA%\ATAS\Indicators\` only at startup.

## Files

    DeltaMath.cs             cluster search, session delta, the flip engine, the cross price,
                             and the flip zones. Pure -- no ATAS types, tested off-platform.
    Icebergs.cs              the footprint absorption scan. Also pure.
    OceansDeltaIndicator.cs  reads candles, renders, settings.
    TimeContext.cs           resolves the bar clock. Shared with Ocean Market View.
    _test/                   123 checks; 36 mutations applied, 36 killed by a named check.
    _smoke/                  constructs the indicator outside ATAS and prints every setting.
