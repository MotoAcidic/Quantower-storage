# The Ocean Indicator Suite — What Each One Does, and How to Port It

A bundle of 18 custom order-flow indicators written for **ATAS Platform 8.0.14** (C#, .NET 10),
used to trade **MNQ** (Micro E-mini Nasdaq-100) intraday. This document is written for a developer
porting them to **Quantower**, and for a trader who needs to know what each one is actually for.

Everything here describes code in `src/`. Nothing in this document is aspirational: where an
indicator is uncalibrated, unvalidated, or unverified on a live chart, it says so.

---

## 0. The single most important fact about this codebase

**58% of it has no platform dependency at all.**

| | Lines of C# |
|---|---|
| Platform-free logic (`*Math.cs`, `*Model.cs`, `*Clock.cs`, `*.Core`) | **33,109** |
| ATAS-bound adapter (settings, `OnCalculate`, `OnRender`) | 23,673 |

This was deliberate and it is the reason a port is tractable. Every project follows the same split:

```
<Name>Math.cs           pure logic — no `using ATAS.*`, no `using OFT.*`
<Name>Clock.cs          session/date logic — pure
<Name>Model.cs          the panel text as data — pure
Oceans<Name>Indicator.cs   the ONLY file that touches the platform
_test/                  console harness that compiles the pure files and asserts against them
_smoke/                 constructs the indicator outside the platform (catches throwing ctors)
_reflect/               dumps the real API surface of an installed platform type
```

The pure files compile under any .NET 10 host. **Quantower's API is also C# / .NET**
(`TradingPlatform.BusinessLayer`), so for most of these projects the port is:

1. Copy the pure files across unchanged.
2. Rewrite one adapter file against Quantower's `Indicator` base class.
3. Run the existing `_test` harness — it never needed ATAS and doesn't need Quantower either.

Do **not** start by rewriting the math. It is tested, it is mutation-tested, and it is the part
that took the longest to get right.

A file inventory with the pure/adapter classification for every file is in **Appendix A**.

---

## 1. What's in the box

Grouped by what they need from the platform, because that is what determines port difficulty.

### Group A — Bars only (easiest port, no order-flow dependency)

| Project | Appears as | What it is |
|---|---|---|
| `oceans-sma` | Ocean SMA | 50/100/200 SMA with tiny right-edge labels |
| `oceans-market-view` | Ocean Market View | Weekly open, power hour, session boxes, opening ranges, initial balance |
| `oceans-orb-breakout` | Ocean ORB Breakout | Opening-range break needing volume + VWAP + the first two hours |
| `oceans-asiawick` | AsiaWick Levels | Asia-session top-wick sweep signals (+ a strategy that ships disarmed) |
| `oceans-crabel` | — | Crabel ORB; the reference implementation of the file bridge. Draws nothing. |

### Group B — Footprint required (per-price bid/ask volume inside each bar)

| Project | Appears as | What it is |
|---|---|---|
| `oceans-profile` | Ocean Profile | Higher-timeframe volume-at-price heatmap; absorption shelves |
| `oceans-developed` | Ocean Developed Profile | Completed prior day/week/month + rolling windows |
| `oceans-effort` | Ocean Effort | Value migration + effort/result + absorption → one trade box |
| `oceans-delta` | Ocean Delta Cross | Session-delta flip joined to cluster search and footprint icebergs |
| `oceans-orderblocks` | Ocean's Order Blocks | MTF order blocks with real in-zone delta |
| `oceans-read` | Ocean's Read | The whole methodology in one box: AMT, profile/TPO, acceptance, Elliott, VWAP stack, rhythm, order flow |
| `oceans-pivot-decoder` | Ocean Pivot Decoder | Confluence zones + absorption state machine + base rates |
| `oceans-pivot-decoder-v2` | Ocean Pivot Decoder V2 | Single-file rewrite of the level engine |
| `oceans-anchor` | Ocean's Anchor | Outlier HVN zones + two absorption engines + CSV calibration log |
| `oceans-current` | Ocean's Current | Session bias engine (LONG/SHORT/NEUTRAL) with a Bias-Lite panel |

### Group C — Live order book required (DOM or market-by-order)

| Project | Appears as | What it is |
|---|---|---|
| `oceans-depth` | Ocean Depth | Right-edge gradient liquidity strip; auto-fits any zoom |
| `oceans-dom` | Ocean DOM | Price ladder putting resting size and traded size on the same row |

### Group D — Tick-level trade stream required

| Project | Appears as | What it is |
|---|---|---|
| `oceans-auction-response` | Auction Response Monitor | 5-second rolling absorption test at a declared level, against a frozen baseline |

---

## 2. The platform capability contract

This is exactly what the suite asks of its host, measured by grepping the adapter files.
Port order should follow this list — if a capability is missing, the indicators that need it
cannot be ported at all, only reimagined.

| Capability | ATAS call | Used | Quantower equivalent |
|---|---|---|---|
| OHLCV per bar | `GetCandle(bar)` → `.Open/.High/.Low/.Close/.Volume/.Time` | 80× | `HistoricalData[i]` → `HistoryItem` |
| **Per-price volume inside a bar** | `GetCandle(bar).GetAllPriceLevels()` → `PriceVolumeInfo { Price, Volume, Bid, Ask, Ticks, Between }` | 19× | `HistoricalData[i].VolumeAnalysisData.PriceLevels[price]` — requires implementing `IVolumeAnalysisIndicator` and waiting for `VolumeAnalysisData_Loaded` |
| Bar delta / max / min delta | `candle.Delta`, `.MaxDelta`, `.MinDelta` | 7× | `VolumeAnalysisData.Total.Delta` etc. |
| Bar POC / value area | `candle.MaxVolumePriceInfo`, `candle.ValueArea` | 13× | Derive from `PriceLevels` (the pure `ProfileMath` already does this — prefer it) |
| Bar VWAP | `candle.VWAP` | 2× | Derive, or use the platform's |
| Open interest | `candle.OI` | 3× | Feed-dependent on both platforms |
| Tick size | `InstrumentInfo.TickSize` | 17× | `Symbol.TickSize` |
| Tick **value** (money) | `DataProvider.TradingManager.Security.TickCost` | 9× | `Symbol.GetTickCost()` — **see §4, never default this** |
| Chart geometry (price→Y, bar→X) | `ChartInfo.PriceChartContainer.GetYByPrice / GetXByBar` | 122× | Quantower `Indicator` drawing coordinates |
| Custom drawing | `OnRender` + `SubscribeToDrawingEvents` + `OFT.Rendering` | 35× | `OnPaintChart(PaintChartEventArgs)` |
| Line-shaped output | `ValueDataSeries` | 42× | `AddLineSeries` / `SetValue` |
| Live order book | `MarketDepthChanged`, `MarketDepthInfo` | 5× | `Symbol.DepthOfMarket` |
| Market-by-order | `MarketByOrder` events | 3× | Feed-dependent; **verify before porting `oceans-depth`** |
| Raw trade stream | `OnNewTrade`, `RequestForCumulativeTrades` / `OnCumulativeTrade` | 4× | `Symbol.NewLast` / Time & Sales |

**The footprint mapping is the whole ball game.** Ten of eighteen indicators stand on
`GetAllPriceLevels()`. Quantower exposes the same thing through a different door:

```csharp
// ATAS
foreach (var lvl in GetCandle(bar).GetAllPriceLevels())
    ladder[lvl.Price] = (lvl.Volume, lvl.Bid, lvl.Ask);

// Quantower — indicator must implement IVolumeAnalysisIndicator
public void VolumeAnalysisData_Loaded() { /* now safe to read */ }
var levels = HistoricalData[i].VolumeAnalysisData.PriceLevels;
foreach (var kv in levels)
    ladder[kv.Key] = (kv.Value.Volume, kv.Value.SellVolume, kv.Value.BuyVolume);
```

Write **one** adapter shim that produces the neutral ladder type the pure math already expects,
and every Group B project inherits it. Do not repeat this mapping eighteen times.

---

## 3. Per-indicator reference

Each entry: what it does, the rule in enough detail to reimplement, how it is used in real
trading, and what to watch on the port.

---

### 3.1 Ocean SMA — `oceans-sma`

**What it does.** The 50/100/200 simple moving averages, each labelled at the right edge in 7pt
text so three lines don't cost three legend rows.

**The rule.** Nothing exotic. Two things that matter:

- **A partial window is never averaged.** With 40 bars loaded there is no SMA 200 — the line
  simply starts where a full 200-bar window first exists. An average of 40 bars printed under a
  200 label is a different number, and people trade it as support.
- `RollingMean` handles the three ways a platform calls `OnCalculate`: a forward walk, the same
  bar repeatedly as ticks land, and a jump backwards after recalculation. The running sum is
  trusted **only** when the bar index advances by exactly one; anything else reseeds from source.

**Real-world use.** Trend context and nothing more. The 200 is the line that matters on MNQ
intraday; price accepting above or below it changes which playbook (fade vs go-with) the other
indicators are being read through.

**Porting notes.** Easiest in the suite — start here to learn the target API. It is also the
reference for the "use a line series, don't hand-draw" pattern: the SMAs are `ValueDataSeries`
so the platform handles scaling, legend and price-axis tags; `OnRender` is used only for labels.
For anything line-shaped in the rest of the suite, copy this project's approach, not Market
View's.

**Trap carried in the source.** Never read ATAS's `SourceDataSeries` — it is null until the
platform wires it up and indexing it returns `0` instead of failing, which averages to zero and
draws nothing with no error anywhere. Check whether Quantower has the same hazard before trusting
any "source series" convenience.

---

### 3.2 Ocean Market View — `oceans-market-view`

**What it does.** Four independently toggleable overlays in one indicator:

| Module | Draws |
|---|---|
| Weekly open | Line at the week's opening print, Sunday reopen → right edge |
| Power hour | The 14:00–15:00 range as a box, high/low carried forward, and the bar that breaks it |
| Market sessions | Asia / London / New York boxes, labelled with the trading day |
| Session opening range | First 15 min of each session: high, low, midline |
| Initial balance | IB high/low/mid, plus extensions every 0.5× out to 3.5×, each switchable |

**Times (all Central).** Week opens Sun 17:00 · Asia 18:00→03:00 · London 02:00→10:30 ·
New York 08:30→15:00 · IB = 08:30 + 60 min · Power hour 14:00→15:00 · Week closes Fri 16:00 ·
**maintenance halt 16:00–17:00** (not the 15:00 cash close — see §4).

**Real-world use.** This is the frame every other indicator is read inside. The IB extensions are
the intraday targets; the power-hour box is where the afternoon trade sets up; the session boxes
tell you whether an overnight level was made by Asia or London, which changes how much it is worth.

**Porting notes.** This is the **hand-drawn reference** — boxes and session shading have no line-
series equivalent, so it exercises the full custom-drawing path. Port this second, after SMA, to
learn the target's drawing API properly.

**Known limit, kept in the source.** London and New York shift their clocks on different dates, so
for ~3 weeks in March and ~1 in late October the London session setting is an hour off. It is a
fixed Central time by design; nudge it manually those weeks. Asia and NY are unaffected.

---

### 3.3 Ocean ORB Breakout — `oceans-orb-breakout`

**What it does.** Marks bars that leave the regular session's opening range with volume and VWAP
behind them.

**The rule — all four must hold on ONE closed bar:**

1. **Close** above the opening-range high. A wick through does not count.
2. Bar volume ≥ **1.5×** the average volume of an opening-range bar (range total ÷ range bar count,
   on the chart's own timeframe).
3. Close above the **session VWAP**.
4. Inside the **signal window** — 08:30 to 10:30 Central, the first two hours.

**Real-world use.** This is the Crabel-style ORB entry. One signal a day by default, in the window
where MNQ actually trends. The VWAP condition is what keeps you out of the opening-range break
that immediately fails back into value.

**Where the money-losing bug would be** — every condition can fail *open* if written carelessly,
and each failure draws a clean, plausible, wrong mark. All four are asserted in both directions
in `_test/` and must stay that way:

- `volume >= 1.5 * average` is trivially true when `average` is 0, which is exactly what a feed
  with no volume gives. Guard returns before the comparison.
- With no volume there is no VWAP. Substituting typical price would draw a line that looks like a
  VWAP and is not — the series stays empty and condition 3 fails closed.
- A day whose opening range never printed has `OrHigh == 0`, and every bar closes above zero.
- The forming bar's volume is partial: it can pass on one tick and fail on the next.

**Timeframe is a correctness input, not a preference.** The opening range is 30 minutes of wall
time. On a 7-minute chart, or on tick/volume/range bars, no bar opens on the boundary and the high
gets read off bars that straddle it. `DayOrb.Aligned` records this and the readout says so in
capitals. Do not "fix" it by snapping to the nearest bar.

---

### 3.4 AsiaWick — `oceans-asiawick`

**What it does.** Asia-session top-wick short signals for MNQ, in two halves: an indicator
(`AsiaWick Levels`, ships enabled) and a `ChartStrategy` (`AsiaWick Overnight`) that trades the
same signals. **The strategy ships with `Signal only` ON — it sends no orders.**

Both consume the same signal core (`AsiaWickMath.cs`), so what gets traded is exactly what got
marked. There is no second implementation to drift.

**The rule.** Trade date = the Globex session rolling at **17:00 CT**. Asia window 18:00 → 02:00 CT
(crosses midnight, one trade date). RTH 08:30 → 15:00 CT is the source of the prior-day high.
Windows are half-open `[start, end)`.

Per closed Asia bar, with `range = high − low` and `upperWick = high − max(open, close)`:

| Variant | Fires when |
|---|---|
| **PDH sweep** | prior completed RTH high known, `high > pdh`, `close < pdh` |
| **Asia-high sweep** | prior Asia high known, `high > asiaHigh`, `close < asiaHigh` |
| **Wick rejection** | new Asia high, `range > 0`, `upperWick / range ≥ 0.50`, `close < low + range/2` |

The current bar folds into the Asia high **after** evaluation — otherwise every bar sweeps itself.
First signal per variant per night; the signal bar's **high** is the stop reference. Short only.

**Real-world use.** Overnight liquidity-sweep short. Asia runs the prior day's high, fails to hold
it, and gives back the level — the classic ICT/SMC stop-raid read, mechanised. Attach to a 15m or
30m chart (60m tolerated; above that it draws nothing).

**The phase gate — do not remove it on the port.** Five conditions before `Signal only` comes off:
backtest PF > 1 on this exact logic; beats the unconditional-short baseline on the same nights;
MAE survives the 40-tick stop and the $500 nightly cutoff; five Market Replay nights checked by
hand; a run of sim nights with the log read each morning. The smoke test asserts the default is ON
so it cannot be flipped by accident in source. **None of those five are closed.**

**Two honest gaps carried in the source.** FOMC (13:00 CT) is not covered by the 07:28 tier-1 flat,
which only handles the 07:30 releases. And `close < low + range/2` is nearly redundant given the
50% wick test — it only excludes a close landing exactly on the midpoint.

---

### 3.5 Ocean Crabel — `oceans-crabel`

**What it does.** A Crabel opening-range-breakout indicator. **It draws nothing.** Its real value
is being the working reference implementation of the **file bridge**: a custom indicator that
computes something and writes it to `%APPDATA%\ATAS\` for other applications to read.

**Why that mattered.** On ATAS that was the *only* reliable way to get order-flow data out of the
platform — reading the internal cache is undocumented proprietary binary and yields silently wrong
OHLC.

**Porting note.** Quantower's API is open enough that the file bridge may be unnecessary. Check
whether the target exposes what you need in-process before reimplementing this pattern. Also: this
project has **no `deploy.ps1`** — it is built and copied by hand.

The `dash/` subfolder is a separate Node/browser dashboard. **Its `secrets.local.json` (an LSE
live-feed API key) has been removed from this bundle** — the dashboard will need its own key.
Cached market data CSVs were also stripped; they are data, not source.

---

### 3.6 Ocean Profile — `oceans-profile`

**What it does.** The higher timeframe's traded volume by price, as a heatmap, on the chart you
are actually trading.

**The core insight — this is the "facts only" indicator.** Everything drawn printed on the tape.
Volume at price, the bid/ask split behind it, the trade count: published facts, not resting orders
that could be pulled.

Where that matters most is **absorption**. A level that traded heavily while aggression stayed
balanced is evidence of a large *passive* order that actually got filled. On a feed with no
market-by-order data that is the honest answer to "where is the size" — and in one way it beats
MBO, because it is proof of a **filled** order rather than a displayed intention that can be pulled.

**The encoding, which shipped wrong once and is worth copying exactly.** Each level draws **two
bars on one scale**: a grey bar for everything that traded, and a coloured bar for how much of it
*leaned* (green = buyers aggressing, red = sellers). **The gap between them is the part that
changed hands without going anywhere.**

| What you see | What it means |
|---|---|
| Long grey, long green | Buyers took size and price moved |
| Long grey, long red | Sellers hit size and price moved |
| **Long grey, almost no colour** | **Size traded, price went nowhere — absorption** |
| Short grey | Thin. Price passed straight through |

Colouring a single bar by delta was the first attempt and it does not work on a profile merged
over hours: price oscillates through every price, buying and selling net off, and nearly every
level lands near balanced — the whole column renders grey and says nothing.

**Four correctness rules, all tested, all of which must survive the port:**

1. **Analysis at full tick resolution; merge for DISPLAY only.** Footprint imbalance compares
   buyers at a price with sellers *one tick below* — the counterparties that actually met. Merging
   rows first nets them off inside the row and silently answers a different question.
2. **The profile array must be DENSE.** A tick that never traded is a real zero, not a missing row;
   a sparse array shifts every diagonal comparison onto the wrong price.
3. **Refuse, never truncate.** An over-wide profile returns null rather than dropping levels — a
   truncated profile can put the POC somewhere it never was and looks perfectly plausible.
4. **POC ties keep the lower price.** A POC flickering between two equal levels as ticks land is
   worse than an arbitrary but fixed choice.

**Marks.** POC · value area (70% band around it) · absorption (outlined: volume above a share of
the busiest level, with aggression no more lopsided than the balance setting) · stacked imbalances
(runs of 3+) · high/low volume nodes (off by default) · unfinished auction (off by default).

**Real-world use.** Two layouts. **Edge column** merges the last N periods into one column at the
chart edge — pairs with Ocean Depth on the opposite edge, so one side shows where size *traded*
and the other what is *resting now*. **One column per period** sits at each period's own bars.
The POC and the three heaviest absorption shelves are carried across the chart as labelled rays,
because a shelf where size was absorbed is only useful if you can see price approaching it.

**Porting note.** `ProfileMath.cs` (762 lines, pure) is the most reused piece of logic in the
suite — `oceans-read` carries its own variant, and `oceans-anchor`, `oceans-developed` and
`oceans-effort` all build profiles. Port it once, carefully, and build the others on it.

---

### 3.7 Ocean Developed Profile — `oceans-developed`

**What it does.** The volume profile of the periods that are **finished**, carried onto the chart
you are trading. Five period kinds, each switchable: previous day (`PD`, 17:00→17:00 Central),
previous week (`PW`), previous month (`PM`), and two rolling windows (`5D`, `20D`).

For each: a histogram lane at the chart edge, plus POC, value-area high/low and the high-volume
shelves carried across.

**Nothing in progress is ever drawn.** A level that is still moving is not a level, and half of
today's profile says nothing about where today's auction settled. That is what "developed" means
and it is the whole point.

**Two refusals, both because the failure mode is a clean-looking chart rather than an error:**

- **It will not profile a period the loaded history does not cover.** A "previous month" built
  from the eleven days you happen to have loaded has a confident POC at a price the month never
  agreed on and looks exactly like a real one. Each period is checked against its own boundary and
  skipped with a note if the earliest loaded bar is later.
- **It will not cut a period until the bar clock is settled** (see §4).

**How it is built — worth copying.** **The day is the unit of account.** Every bar folds to its
futures trade date, each completed date gets one profile, and that profile is cached forever
because a day behind us cannot change. A week, a month and both rolling windows are then *sums* of
those day profiles rather than four more walks over the bars. Volume at a price is additive over
time, so nothing is approximated — there is a harness test pinning that against a direct build
from the same trades.

**Shelves are bands, not ticks.** Every level at or above `Size (% of the busiest level)` is a
candidate; candidates within `Bridge dips up to` ticks are one shelf, because a single thin tick
inside a shelf is noise and splitting on it reports two levels where the market built one.

**Real-world use.** Prior-day and prior-week POC/VAH/VAL are the overnight and morning reference
levels — where the auction last agreed on price. The rolling 20D profile is the swing context.

**Porting constraint.** History depth is the real limit: these are higher-timeframe profiles
assembled from chart bars, so a previous-month profile needs a month of bars actually loaded
(~30,000 on a 1-minute chart). If Quantower can request a separate timeframe's data directly, this
project gets *simpler* on the port — and so does `oceans-orderblocks`.

---

### 3.8 Ocean Effort — `oceans-effort`

**What it does.** Built from a Fabio Valentini interview on NASDAQ scalping: value migration,
effort against result, absorption, and location — with a setup marked only where all four agree.
The deliverable is a **trade box**, not chart clutter.

```
OCEAN EFFORT                                              SHORT
READ ─────────────────────────────────────────────────────────
VALUE    migrating down       29488.00 - 29502.00
EFFORT   down cheaper 1.5x    up 176 vs down 118 per tick     over 20 bars
ABSORB   buyers at 29510.00   1.2k traded, delta +740         4 bars ago
BAR      delta -740 of 2.1k   closed -6t
AGREE    3 of 3 point down    this is a setup bar
TRADE ────────────────────────────────────────────────────────
SIDE     SHORT                bar 812                         6 bars ago
ENTRY    29488.00
STOP     29502.00             14t                             $7.00
TARGET   29474.00             14t                             $7.00      1R
TRAIL    29499.00             +11t locked in
LIVE  6 bars in  --  open +8t  $4.00  --  target not reached
```

**The four layers:**

1. **Value migration** — each bar's own volume value area against the bar before it. Migration
   needs the band *and* the POC to move the same way: a bar that stretched upward while trade
   concentrated lower has widened, not migrated.
2. **Effort** — **contracts traded per tick of net progress**, summed separately for up bars and
   down bars over a rolling window. If the last twenty bars bought 8 ticks up for 800 contracts and
   4 ticks down for 1600, up cost 100/tick and down cost 400, so up is cheaper by 4×. One side must
   be clearly cheaper (default 1.35×) before this says anything.
3. **Absorption** — a bar where one side aggressed hard and got nothing: heavy volume against the
   recent average, lopsided delta, close that went nowhere or the other way.
4. **Location** — the entry must stand on a cluster area or an edge of value that argues the same
   way. **An unknown location refuses too**: not knowing where price is is not the same as it
   being fine.

**The regime filter is the biggest single thing in the model.** The source material explicitly
avoids choppy non-trending markets — preserving capital rather than trading poor conditions. An
early version was trading the cage: a long chop with `-34t, -24t, -2t, -34t, -6t, -33t, -25t`
stacked up. `MarketRegime.cs` answers two questions from what happened, no oscillator:

- **How much of the window's closes sat inside the value area** (default 65% = balance)
- **How much of its range turned into progress** — `|close − open| / (high − low)`. A market that
  covered two hundred ticks and finished ten from where it started kept nothing. Under 30% is
  balance, over 50% is trend, between is Mixed.

**Both conditions are needed for balance**, and a test almost missed it: chop happening *outside*
value is a stalled directional auction, not fair value — calling that "the cage" stands the model
aside exactly where it should be watching. **Unknown refuses**: not knowing what kind of market
this is is not the same as knowing it is a good one.

**Divergence is the primary trigger** (`DeltaSignals.cs`) — a new low the selling did not pay for
(cumulative delta *higher* at the new low than at the swing low it broke), or a new high the buying
did not. That is the delta scalper's read: the side in control spent more and got less. The
continuation read (migration + effort + bar) survives as the slower one; both feed the same
one-at-a-time tracker.

**Two display rules that exist because earlier versions got them wrong:**

- **One setup runs at a time.** While a stop still stands, bars that keep agreeing with it are the
  same idea, not new ones.
- **A stop beyond the risk cap is greyed out, never pulled in to fit.** Pulling it in would be a
  different trade wearing this one's confirmation.

**Thresholds that measure themselves.** `DivergenceMinGap` → 2× the **median** |bar delta| over the
lookback; `MaxRiskTicks` → 3× the **median** bar range in ticks. Median not mean, so one 5000-lot
bar cannot set the scale. Re-measured every 25 bars, and the status line prints what it worked out
to — a threshold is never a number you cannot see. Both default to zero (= measured).

**The anchor bug worth knowing about.** The drawn profile was originally built from the *visible*
range, so value high, value low and VWAP moved every time the chart scrolled. A reference level
that depends on the scrollbar is not a reference level. Everything is now built from a fixed
lookback (`LevelLookback`, 150 bars) ending at the newest bar. The drawn profile may be fractal
(zoomed out = the day, zoomed in = the swing) but **a setup is judged against the fixed lookback,
never the visible range.**

**Real-world use.** This is the primary scalping tool. The box is read, the chart stays clean: an
effort ribbon along the panel bottom, absorption marks, and the live trade drawn as **zones** (red
block entry→stop, green block entry→target) rather than three thin lines you have to hunt for.

**Two variants** — `Oceans Effort MNQ` and `Oceans Effort Crypto` (a thin subclass). Nothing about
the read changes; what changes is every threshold expressed in contracts or ticks.

---

### 3.9 Ocean Delta Cross — `oceans-delta`

**What it does.** Four delta reads wired into one another, producing one mark: **a cross where the
session delta flipped** — a vertical on the bar that crossed, a horizontal at the price.

| Read | Where it shows |
|---|---|
| Bar delta | Thin heat strip at the panel edge |
| Session delta | Thin heat strip above it |
| The flip | The cross |
| Cluster search | Grades the cross; marks on the flip bar |
| Flip levels | Bands from older sessions' flips |
| Lines in the sand | Horizontal lines where one-sided aggression kept getting absorbed |

**The flip rule — a crossing is armed, not accepted:**

1. Session delta takes the opposite sign. That bar is the crossing — remembered, nothing drawn.
2. It becomes a flip only once the sum has travelled *Confirm a flip after* contracts past zero.
   A crossing that comes back before then never happened and leaves no mark.
3. The cross is anchored to the bar that **crossed**, not the bar that confirmed. The confirmation
   is evidence about a moment that already passed; drawing it late would put the line on the wrong
   bar.

Live, a forming bar can arm and confirm a flip and then fill back in and take it away. The engine
rewinds and the cross disappears, which is correct: the closed bar never crossed.

**The honesty line on the horizontal — copy this reasoning.** The exact tick at which the running
sum passed zero **is not recoverable from bar data**. A bar records what traded at each price,
never in what order. So there is no interpolated price and there never will be. The horizontal is
one of two things and the label always says which: the crossing bar's **close** (a fact), or the
**flip cluster** (the price inside that bar carrying the most delta of the incoming side — a
reading, labelled as one). If no level qualified it falls back to the close and says
`close - no cluster carried it` rather than quietly drawing a line at a price nothing supports.

**Cluster search.** A level inside the crossing bar counts as a cluster when it clears all three of
**volume** (contracts at that price, both sides), **delta** (how far apart the two sides were), and
**lean** (that difference as a share of the level's own volume). The lean bar is what stops a
400-lot level split 210/190 counting as one side doing something, while letting a 60-lot level
split 55/5 through. A flip with no qualifying cluster draws **dashed**, not hidden.

**Lines in the sand — icebergs from the footprint.** A price qualifies when all five hold: volume
at the price across the window · share of everything the window traded · lean · **bars** (it has to
have happened in several bars — one enormous print is a big trade, not a level that kept reloading,
and this is the only thing separating the two) · **it held** (no bar *closed* through it against
the passive side; wicks through are the level working, not breaks).

The label shows the **passive** side: `^ 21734 ×7 4.1K` means sellers kept hitting a bid there and
it kept being there. **This is evidence, not an identification** — a genuine refreshing iceberg
leaves exactly this footprint, and so does one large resting order never replenished, and so does a
price that simply kept attracting business.

**Real-world use.** The cross tells you when the session changed hands and whether anyone paid for
it. The flip levels from older sessions are the ones that matter — `1.2734 Wed 20 ×2` means two
different sessions turned on that price, and those are drawn heavier.

**Layout discipline worth keeping.** Four stacked panels was the problem this was built to fix, so
the default costs ~19px, capped at a third of the panel whatever the settings say. Nothing is drawn
over the candles except the cross itself.

---

### 3.10 Ocean's Order Blocks — `oceans-orderblocks`

**What it does.** An ATAS port of the Pine "MTF Order Blocks + Zone Delta" (OB Suite Δ), using the
real footprint where the Pine had to estimate. Order blocks on **1H / 4H / D / W**, plus session
blocks on the chart timeframe inside 08:30–15:00 Central.

**The rule (same as the Pine).** An opposite-colour candle whose extreme is closed through by the
next candle's body. Zone is wick-to-wick or body-only. Mitigation is price through the far side.
Border weight: W 3, D 2, the rest 1.

**Right-edge label per block:**

```
4H ▲ 21460.25 · brk +2.1K · zΔ -840 / 3.1K · t2
```

`4H ▲` timeframe + demand/supply · `21460.25` midline (50%/CE, also drawn dashed) · `brk +2.1K`
real bid/ask delta of the breaker candle — who did the breaking · `zΔ -840 / 3.1K` delta and volume
traded at prices **inside the zone** since it went live · `fresh`/`t2` untouched or touch count.

**What changed from the Pine, and why — this is the interesting part:**

| Pine | Here | Why |
|---|---|---|
| Delta from 1m candle direction | Real bid/ask delta per bar and per price | The platform has the footprint |
| Zone Δ = whole-market CVD since confirmation | Δ and volume at prices **inside the zone** | The Pine number counted delta printed 200 points away |
| `request.security` + `lookahead_off` | HTF bars built from chart bars, closed on their last chart bar | Pine loaded HTF blocks one bar late on history vs live; here they appear on the same bar both ways |
| Faded boxes left for TradingView to GC | Faded list capped by *Max faded kept* | Pine's collector deleted the oldest drawings — which were the active weekly blocks |

**Order inside `ObEngine.Fold` is load-bearing:** flow → advance → CVD → detect. Detection is last
so a block born on a bar is first checked on the next one, which is where Pine checks it too.

**Zone state moves on closed bars only**, so history and live agree. Folding the live bar per tick
would double-count footprint, and a wick that later closes back inside would mitigate a block live
that history would have kept.

**Real-world use.** ICT/SMC order blocks with the one thing TradingView cannot give you: whether
anyone is actually trading inside the zone. A fresh 4H demand block with `zΔ +2.1K` is a different
proposition from the same block with `zΔ -840`.

**Porting note — this project gets easier.** Its biggest limitation is that an ATAS indicator
cannot request another timeframe, so weekly blocks need ~3 weeks of chart bars loaded. If
Quantower can request a separate `HistoricalData` series at another aggregation, `HtfBuilder` can
be deleted and replaced with a direct request. **Verify the closing semantics match** — the whole
reason `HtfBuilder` closes buckets by *time* rather than by the next bar is to avoid the Pine
lookahead lag.

**Not yet verified on a live chart.** Built and smoke-tested outside the platform only.

---

### 3.11 Ocean's Read — `oceans-read`

**What it does.** The largest and most ambitious of the suite: one structural read of the auction,
in the order the method reads it, in a single box. 6,683 lines, of which all but the indicator file
are platform-free.

The methodology it is built from says *no indicators, no guesswork, just learning to read what the
market is actually doing*. So it does not produce a signal from a formula. It **measures** the
things the method reads, in the method's own vocabulary.

| Concept | What is actually computed | File |
|---|---|---|
| **Auction Market Theory** | Balancing or imbalancing, from two independent measurements: how much of prior session's value today is still trading in, and how much ground covered turned into net progress. They disagree often; when they do the answer is **Transitioning**, not a casting vote. | `AuctionMath.cs` |
| **Volume Profile / TPO** | One dense ladder carrying both measures side by side — contracts traded, and how many time brackets visited each price. POC, value area, shelves and gaps as bands, single prints, poor highs/lows, tails, profile shape (D / P / b / double distribution / elongated). | `ProfileMath.cs` |
| **Acceptance** | A break is a bar **closing** through a level, never a wick. What settles it is **time and ground** on the far side — both, and both scaled by the market's own measured rhythm. Closing back through before either threshold is met is a rejection, whatever the excursion reached. The developing POC crossing to the far side is noted separately: that is value following, the strongest form. | `AcceptanceMath.cs` |
| **Elliott Wave** | The character of the last rotations (impulsive: legs clear of each other; corrective: legs treading the same ground) plus a five-wave count **only when all four hard rules hold**. Otherwise "no valid count" and which rule broke. | `WaveMath.cs` |
| **VWAP, top down** | Yearly, quarterly, monthly, weekly, daily and session anchors with deviation bands. The box reports which side price is on for each and whether the anchors are stacked in order or disagreeing. | `VwapMath.cs` |
| **Developing value** | The session profile rebuilt on every closed bar, plus the POC's trail — which way value is going, measured over a span of **time**, not bars. The classic value relationships vs prior session. | `ProfileMath.cs`, `AuctionMath.cs` |
| **Rhythm** | The market's own cadence, measured rather than assumed: median, quartiles and 90th percentile of completed rotation size **in ticks and in minutes**, up legs and down legs separately. The leg in progress is placed against that distribution. **This is what every acceptance threshold is scaled by.** | `RhythmMath.cs` |
| **Order flow** | Delta per bar, session and leg; cumulative delta sampled **where rotations turned**, so a divergence compares the delta at the price extreme rather than the delta extreme; footprint in a band around the level under test; absorption; and open interest read against price — new longs, short covering, new shorts, long liquidation. | `OrderflowMath.cs` |

**The playbook at the bottom of the box — not a signal:**

- **Balancing → FADE THE EDGE.** Edges of value are the trade, POC is the target. Gates: price at
  an edge of value, the rotation has already run its usual distance, the leg is running *into* the
  edge, the tape is failing there, and value is not breaking out.
- **Imbalancing → GO WITH THE MOVE.** The move is the trade, the pullback is the entry. Gates: a
  side to take, acceptance established, entering at the turn rather than late, the VWAP stack
  agreeing, and order flow confirming.
- **Transitioning → STAND ASIDE.** The measurements disagree. Calling it either balance or
  imbalance loses that.

Every gate met is `ACT`. One short is `PREPARE`, naming the missing one. More is `WAIT`.

**Three refusals, all because the failure mode is a clean-looking chart rather than an error:**

1. It will not cut a session until the bar clock is settled.
2. It will not judge acceptance without a measured rhythm. A fixed tick count that means "far" on a
   quiet overnight means "noise" at the open.
3. It will not print a wave count unless every rule holds. A labeller that always finds five waves
   has told you nothing, because it would have found five in a random walk.

A fourth, softer one: **if the chart carries no footprint**, the profile is built from bar closes
and the box says so in red at the bottom. A profile built from closes is a different object from
one built from the tape and must never pass for one.

**Real-world use.** This is the pre-trade read — what kind of day is it, where is value, has the
level been accepted or rejected, is the tape confirming. It gates the other indicators rather than
competing with them.

**Porting notes — port this LAST.** It is the biggest single piece and it depends on profile and
order-flow primitives that the earlier ports will have already settled. `_test` compiles nine
source files carrying no platform type at all: 348 checks and 47 mutations, all killed.

**Acceptance references must stand still.** `References()` returns prior-session value and range,
overnight extremes, and initial balance — **never the developing value area**. A test opened on a
level that moves every bar chases price and never resolves. And a live test keeps the price it was
opened at; re-reading the level each bar is the same bug in a different place.

---

### 3.12 Ocean Pivot Decoder — `oceans-pivot-decoder`

**What it does.** Finds the price bands where **different kinds of evidence agree**, and states
where the long side and the short side are for an overnight hold. It also decodes a third party's
quoted levels back to the formula and session window that produced them, which is what it was
originally built for.

**The one idea.** Every floor pivot, Camarilla level and mid is a rearrangement of the same three
numbers. **Thirty of them landing together is arithmetic, not agreement.** So nothing renders as a
line: levels cluster into **zones**, and a zone scores as the weighted sum of what it is made of.

| Family | Weight | Why |
|---|---|---|
| floor / Camarilla / mid | 1.0 | arithmetic on H/L/C |
| prior H/L/C | 1.0 | where price actually turned |
| session POC (Asia/London/NY) | 1.5 | where contracts actually traded |
| session VWAP | 0.75 | reference, not a decision |
| **naked POC** | **2.0** | a magnet price has never been back through |
| **unfinished extreme** | **2.0** | an auction cut off rather than exhausted |

Zones below price render green (candidate longs), above red (candidate shorts); opacity scales with
score. Only bands within 400 pts of price draw, at most three a side.

**The absorption engine.** A zone score says a level exists. It cannot say the level was ever
*defended* — price reaches every level eventually. Each zone runs
`UNTESTED → TESTING → VALIDATED | FAILED`. VALIDATED needs four things together, each killing a
different way of being fooled by something that merely looks like a bounce:

- **size** — cumulative in-zone delta clears an **adaptive** bar (1.5× the 20-bar average absolute
  delta). Never a hardcoded contract count, which is wrong the moment the session changes character
  and wrong again on another instrument.
- **direction** — the delta points *into* the zone. Buying into support is not absorption.
- **containment** — price did not push more than 8 ticks past the far edge.
- **rejection** — price closed back out by 6+ ticks.

FAILED is acceptance: two consecutive closes more than 12 ticks beyond the far edge. The zone dims
to grey and drops out of the bias panel — it is no longer a level.

**Base rates, not probabilities.** The zone score is an *opinion* about which levels ought to
matter, and it has never been validated. So the decoder replays the same construction over the last
30 sessions on the chart, walks each day's bars, and counts what price actually did at bands like
this one. Bucketed on two axes only (strong/weak, magnet/no-magnet) because every extra split
halves the sample. **Below 8 touches no rate is quoted at all** — a 3-for-4 hit rate reads as 75%,
means nothing, and showing it invites size behind noise.

**Unfinished extremes.** A completed auction thins as it runs out of buyers. Two readings flag one
that did not: **no taper** (the extreme tick still holds 35%+ of the volume three ticks back) and
**ledge** (two or more bars printed the exact same extreme). Retired once price trades 4 ticks
through.

**Real-world use.** Overnight positioning and the morning's level map. The one-line readout is the
sentence you would say out loud:

```
SHORT  sell 30458.25  stop 30460.25  target 30105.00  17.6R  [x5 HELD]  6/10 held (60%), top-ticked 30%
```

Entry is the **far edge** of the band — the top of resistance, the bottom of support — because
entering mid-band gives up half the edge and moves the stop no closer.

**The R3/S3 trap, worth knowing for any level tool.** The two conventions usually quoted as
different are the same formula written two ways: "Standard" `R3 = H + 2(PP − L)` and "Narrow"
`R3 = R1 + (H − L)` both reduce to `2PP + H − 2L`. They agree to the cent, always. The convention
that genuinely differs is the PP-anchored pair `R3 = PP + 2(H − L)`, which sits 121 points away on
the validation case. Confusing the two is what makes a decoder report NO MATCH against a caller who
is in fact using floor pivots.

**Validation case, pinned in `PivotMath.SelfTest` and run in the constructor:**

```
H = 29211.75   L = 29017.25   C = 29186.25
floor PP 29138.42 · classic S3 28870.58 · Camarilla R4 29293.23
```

Two rounding traps it guards: S3 is 28870.**58**, not .59 (.59 comes from rounding PP to 2dp
*first*); and 29293.225 rounds to 29293.23 only **half-up** — banker's rounding, the .NET default,
gives 29293.22. Every display path goes through `PivotMath.Round2`. **Check your target platform's
rounding on the port.**

**Timeframes.** A bar carries one timestamp — its open — so a window is matched against the bar's
whole **span**, not that stamp. This is why an hourly chart used to disagree with a 15-minute one:
RTH opening at 08:30 discarded the 1h bar stamped 08:00 whole, losing 08:30–09:00, the cash open
and very often the session high or low.

---

### 3.13 Ocean Pivot Decoder V2 — `oceans-pivot-decoder-v2`

A single-file (1,909-line) rewrite of the level engine: institutional reference levels labelled with
both full price **and** last three digits so a caller's "@605" resolves at a glance, coloured by
which side of price they sit on, merged into a shaded band where two or more stack, and marked
where a sweep-and-reclaim actually happened rather than where one was merely possible.

**Notable difference from V1.** V2 does **not** auto-detect the bar clock — `BarClockSource` is a
stated setting (`Utc` / `ExchangeLocal`), on the grounds that a wrong guess silently shifts every
session boundary. V1 and the rest of the suite resolve it from evidence instead (see §4). Both
positions are defensible; **pick one for the port and apply it everywhere.**

The file header is an unusually good artefact: it documents every platform assumption that was
verified by reflection against the installed ATAS metadata rather than taken from docs. Read it
before porting anything else — it is a checklist of exactly what to re-verify on the new platform.

**Porting note.** This is the one project with no pure/adapter split, so it is the one that has to
be genuinely rewritten rather than re-hosted. If V1 and V2 overlap in purpose, consider porting V1
(which has the pure math already extracted) and folding V2's labelling ideas into it.

---

### 3.14 Ocean's Anchor — `oceans-anchor`

**What it does.** Built for exactly one trade: price arrives at a volume shelf the last two weeks
agree on, something large absorbs the aggressors there without price moving, and the delta turns.

Signal chart **NQ** (cleaner institutional tape); execution **MNQ**, manual, from its own DOM. It
never places an order.

**Zones — outlier high-volume shelves, at most four, ranked:**

| Rank | What |
|---|---|
| 1 | A naked POC that is also a composite shelf |
| 2 | A fresh prior-RTH POC (or a second distribution) |
| 3 | A composite-only shelf |
| 4 | The overnight POC |

Opacity follows rank. Spent zones (traversed three times) and zones further than 1.5 ADR from the
open fade almost out — still visible, because knowing a level is used up is information.

**The filter that makes this a playbook rather than another volume profile is the outlier test:**
a peak survives only if it is at least **70% of the POC bin**. *If you have to squint, it is not an
HVN.*

**Two absorption engines, and their agreement rate is itself an output:**

- **Live tape** is the real signal: actual cumulative market orders. A print qualifies if it clears
  the size floor, lands inside a live zone, comes from the right side (support wants *sell*
  aggressors — someone hitting the bid and the bid not moving), and then goes nowhere for 2 seconds.
- **Cluster forensics** reconstructs an approximation from footprint bid/ask so history renders on
  chart load. Four shape tests — outlier delta, close back inside, volume concentrated at the
  extreme with the right delta sign, and a wick — three of which must pass.

Below ~60% agreement, the cluster thresholds are mis-set and the historical marks should not be
trusted.

**The state machine.** `Dormant → Armed → Triggered → Confirmed`, with `Expired` and `Broken` as
the ways out.

- **Armed** — price is inside a live, ranked, non-spent zone.
- **Triggered** — qualifying absorption printed while Armed. Events within 90s stack: they raise
  the grade and widen the cluster, which widens the stop to where it belongs.
- **Confirmed** — within three bars, either the delta flips or CVD makes a higher low against an
  equal-or-lower price low. The second is quieter and often earlier: sellers spent less to reach
  the same place.
- **Expired** — the clock ran out. **Absorption that keeps absorbing is distribution.**
- **Broken** — the cluster extreme traded through. No re-fade that session.

Guards: the one-timeframing filter (never fade the freight train), the 08:30–10:30 A+ window, and
Friday suppression.

**Status line — the point of the whole thing:**

```
ANCHOR r1 21384.00-21402.00 · CONFIRMED ▶ stop 21379.50, 1.5R 21411.00+
```

The stop and 1.5R come free from the cluster extremes. At signal time the question is not "is this
a setup" — the indicator just said it was — it is **"does the R:R clear the floor"**.

**The CSV is the actual edge.** One row per Triggered episode to
`Documents\ATAS\OceansAnchor\anchor_log.csv`, finalised when it resolves. **Every threshold ships
as a guess** — `SizeFloor` 100 is an NQ round number, not a measurement. The protocol: run ten
sessions passive, then offline set `SizeFloor` to p95 of `largest_trade` on tape-path rows; if the
expiry rate is over ~50% loosen `ClockBars`; if tape-vs-cluster agreement is under ~60% fix the
cluster thresholds first. Then the kill criteria: T1 hit under ~55%, or realised R under 1.3 →
zones first, floor second, **one knob at a time**.

**Status: thresholds still uncalibrated.** A silent-calibration mode ships ON — nothing drawn,
nothing sounded, CSV forced on — specifically so the ten sessions can be run without the indicator
talking you into trades off numbers nobody has checked.

**The history gate is the one substantive design decision to preserve on a port.** Folding every
bar and *then* applying the tape response promotes zones using trades from hours ago against a
state machine that has already advanced to now: a zone armed at 09:15 and broken at 09:40 gets
triggered by a 09:15 print while sitting Broken. Since the CSV *is* the calibration, a wrong row is
worse than a missing one. So the tape is requested **before** the session's bars are folded, and
each bar replays its own trades in its own bar, in time order — exactly as if it had been running
live all session.

---

### 3.15 Ocean's Current — `oceans-current`

**What it does.** One directional state for the session — **LONG / SHORT / NEUTRAL** — held until
the evidence for it stops standing up. Not an entry tool; it draws no arrows. It frames and gates.

**The value is not that it produces a score — anything can produce a score — it is that it produces
about three of them a day.**

**Four factors, each voting −100…+100:**

| | Factor | Weight | What it says |
|---|---|---|---|
| F1 | VWAP position | 0.20 | Where price sits against the anchored VWAP, in that session's own spread. ±2σ saturates. Halved when the VWAP slopes against it. |
| F2 | CVD thrust | 0.20 | How hard cumulative delta pushed over 20 bars, priced against how hard it usually pushes. Halved and flagged on divergence. |
| F4 | Overnight inventory | 0.10 | Where the overnight closed inside its own range, and which way it gapped the open. Frozen at 08:30. |
| F5 | Structure ladder | 0.20 | Price above/below PDH/PDL/ONH/ONL, plus a decaying penalty where a break was swept and reclaimed. |

F3 (value migration) and F6 (30-min one-timeframing) are **not built** — they appear disabled and
they **abstain** rather than voting zero.

**The gamma regime transforms the weights — it never invents direction:**

- **Long gamma:** CVD weight ×0.7, and F1 inverts at extremes (stretched, not strong).
- **Short gamma:** CVD weight ×1.3, no fade.
- **Inside a wall zone** (15 pts): the whole score ×0.6. Scales the answer, never flips its sign.
- **No feed, stale feed (>20 min), or a mixed reading:** no transforms at all.

**Hysteresis:** enter at ±30, leave at ±10, no state change for three committed bars. Long can only
become short by passing through neutral. That is the part that earns the screen space.

**"Absent is not zero" — the rule the whole design is arranged around.** A factor that cannot be
computed **abstains and drops out of the weight normalisation**. It never votes zero, because zero
is a real reading ("price is exactly on VWAP") and burying "I don't know" inside it drags every
blended score toward neutral for reasons nothing on the chart could show. Every factor absent → no
score, and the state holds. The same rule reaches the log: an absent factor is written **blank**,
never `0`, so the calibration regression never sees a made-up neutral vote.

The panel reflects this: an abstaining factor draws a hollow chip and **no bar at all**. A
zero-length bar on the centre line would be indistinguishable from a factor that genuinely read
neutral, and those are different claims.

**The weight column is the *effective* weight, not the setting** — what each factor is actually
worth on this bar after the regime transform and the absent factors have dropped out, as a share of
the vote, so the column adds to 100.

**"What changes it"** — the panel says exactly what would flip the state and draws those levels:

```
LONG     close above 29168.00 (+57.3)
SHORT    close below 29094.25 (-16.5)
SHORT    one bar of -1,850 delta
GAMMA    flip 29318.50 (+207.8) below: moves run
```

These are not estimates. Each is found by committing a hypothetical bar through the engine's real
decision code on a throwaway copy, and the test suite checks that a real bar closing at each printed
level does exactly what the panel says — and that one tick short, it does not.

**Status: no proven edge.** 56 sessions ≈ a coin flip. The honest benchmark is written into the
project: **the 10:30 state must beat the naive rule "long if price > session VWAP at 10:30" by ≥3pp
over ≥40 sessions. If it cannot, the composite is dead weight** — strip it to VWAP + GEX regime and
stop. That is not decoration: F1, F3 and F5 all encode "where is price relative to structure", and
the main reason composite bias tools disappoint is that they turn out to be VWAP-side filters
wearing a costume.

**Porting note.** The GEX feed reads a single CSV row written by an external process
(`%APPDATA%\ATAS\Ocean\obe_gex.csv`, polled on new bars only). **There is no last-known-value
cache** — a regime remembered from this morning is exactly the input that would flip the weights
the wrong way this afternoon. Keep that.

---

### 3.16 Ocean Depth — `oceans-depth`

**What it does.** A narrow gradient liquidity strip pinned to the right edge of the price panel:
where the resting size is on each side, with the number written next to it.

**Why it exists.** The platform's own market-depth indicator lays rows out in fixed pixels and has
to be re-fitted every time the chart is zoomed. This one derives its whole layout from live chart
geometry every frame:

- **Row height** from `PriceChartContainer.PriceRowHeight`, so a row is exactly as tall as the
  price it covers.
- **Row bucketing** chosen so a row is never thinner than *Minimum row height*. Zoomed in that is
  one tick per row; zoomed out, ticks merge automatically. **This is what makes the same settings
  work on 1m, 5m and 1h without touching anything.**
- **The colour scale** renormalised each frame against the biggest level in view.

Rows are anchored to a multiple of the row size in *price* space, not to the bottom of the window,
so scrolling slides rows across the screen instead of reshuffling which ticks share a row —
without that the strip shimmers whenever the chart moves.

**Persistence.** Size that gets pulled does not vanish instantly: it holds at full brightness for
*Hold*, then fades over *Fade*. Two reasons this is on by default — MNQ's book changes many times a
second and drawn raw the strip strobes; and a 400-lot that appears and disappears **is information**,
and the fade is the only way it is ever visible.

**Three sources, and the honesty around them:**

- **Depth of market (aggregated)** — total size per price. Every feed has it. Default.
- **Market by order** — every individual order separately, which additionally gives *biggest single
  order at a price* (300 lots as one order and as thirty orders are very different things, and
  aggregated depth cannot tell them apart) and an order count (`240/3`).
- **Biggest chunk added (estimated)** — the workaround for feeds with no per-order data. Watches
  total size at each price and records the biggest single *jump*. CME publishes an incremental
  update per order action, so a lone 300-lot shows up as one `+300`.

When MBO is selected and the feed is not sending it, **the strip says so on the chart** along with
a count of how many aggregated depth updates *did* arrive. It does not quietly draw aggregated depth
under an MBO label.

Three things the chunk estimator deliberately does not do: the first update at a price is never
counted as an add (everything already resting arrived before it was watching); the estimate is
capped at what is actually resting; and a level that empties completely forgets everything.

**Real-world use.** Pairs with Ocean Profile on the opposite edge: profile on one side for where
size *traded*, depth on the other for what is *resting now*.

**Porting notes — verify MBO availability first.** This is the project most exposed to feed
capability. Also: the subscription is made from `OnInitialize()`, which is where the platform's own
DOM makes it; requesting it from the render thread does not reliably deliver data. And the chunk
estimator is fed from the depth-changed event at **full feed rate**, not from the render-rate poll
— sampled a few times a second, separate orders merge into one delta and the estimate inflates.

---

### 3.17 Ocean DOM — `oceans-dom`

**What it does.** A price ladder for the price panel. Five columns in fixed positions:

```
   traded          resting bid   price   resting ask      level rail
 |=====     |            |====| 29612.75 |==       |    PDH  TEST
 |==        |        |========| 29612.50 |=        |
 |==============|  |=========| 29612.25 |===      |
```

**The one idea: resting size and traded size land on the same row.** Size that sits there while
trade goes through it and price does not move is absorption. Size that vanishes before trade reaches
it was never real. Both readings need the same two numbers at the same price, and every platform
splits them across two panels. Here they are adjacent.

**The ladder does not tell you which one it is.** That read is yours — `oceans-profile` and
`oceans-effort` are the derived version.

**Levels and the five states.** Type levels in as `PDH=29650.25, PDL 29500, ORB-H=29612.5`. Anything
unreadable is **printed on the chart**, never silently skipped. Each level is always in exactly one
of five states, and every one is a measurement:

| State | Meaning |
|---|---|
| **Quiet** | price is nowhere near it |
| **Approach** | price is within the approach distance |
| **Test** | price is inside the band right now |
| **Reject** | price entered the band and left on the side it came from |
| **Break** | price went through the band and out the far side |

Break and Reject sit the *same distance* from the level. The only thing separating them is which
side price originally approached from — which is why the watch remembers that, and why it has to
survive price chopping back and forth across the level while still inside the band. (That was the
one mutation that survived the first test suite; it is now a named test.)

**Two rules the display enforces on itself — worth preserving verbatim:**

- **One loud thing, and only a break gets it.** `Salience.For` will not hand the loud colour to
  anything but a `Break`. *A ladder that shouts when your level holds is a ladder that talks you
  into staying in a trade; the one moment worth a shout is the one that says you are wrong.*
- **Quiet looks quiet.** With no level in play the entire panel fades. Most of the session is
  no-trade, and a screen that looks equally busy when there is nothing there is a screen that
  manufactures trades.

Both are why the panel has fewer knobs than it could. Column positions never move and never
reflow — a display you read by muscle memory has to be in the same place every time, which also
rules out any "declutter when it gets busy" mode.

**Real-world use.** Execution. This is what is read at the moment of entry, after the other
indicators have said where.

**Event log.** One JSON line per state change to `%APPDATA%\ATAS\ocean-dom-events.jsonl`. Nothing
reads it yet — that is the intended next piece, and it is what would let each state carry its own
hit rate on the face of the ladder instead of asking you to trust it.

---

### 3.18 Ocean Auction Response Monitor — `oceans-auction-response`

**What it does.** A **read-only** monitor that looks for aggressive trading with limited price
progress at a level you declared in advance, then waits for a separate directional confirmation.
It places no orders, reads no account state, and contains no reachable order-submission path.

**Read this before anything else in this project:** there is **no demonstrated edge here**. Every
threshold is an engineering default from a specification, not a calibrated or backtested number.
There are no learned coefficients, no calibrated probabilities, no win rate, and no evidence of net
profitability after costs. The probability panel reads "Not calibrated" and shows no number.

**It also will not arm at all until you supply a frozen historical baseline artifact.** The rule
compares attacker volume against a frozen 90th percentile, and there is no honest way to invent
that number. Until the artifact exists the health panel reads Warmup and no candidate can arm.

**The rule, in one paragraph.** At a declared level `L`, freeze the zone `Z = [L−2, L+2]` ticks.
Over a rolling 5-second window a **candidate arms** when all of these hold at once at a decision
tick: the data is healthy, the level predated the window, the midpoint is inside the zone, attacker
volume strictly exceeds its frozen 90th percentile, oriented delta is positive, net oriented price
progress is between 0 and 2 ticks, the maximum forward excursion never exceeded 2 ticks, and at
least 25% of the attacker volume actually executed inside the zone. On arming, three boundaries
freeze: `K` (the oriented minimum midpoint over the window), `C = K − 2` (confirmation) and
`F = max(oriented zone edge) + 4` (failure). A **confirmation** then needs the midpoint to hold at
or beyond `C` for a full second of *observed* time, plus opposite 5-second flow, within 30 seconds.
Anything beyond `F` for a second **invalidates**. Running out of time **expires**. Those three are
alternative outcomes, not a sequence.

**Building the baseline.** Open a 5-second chart with as much history as the platform will give,
add the Baseline Builder, stamp it with the contract expiry the data belongs to, and write the
artifact. **Gates:** at least 10 sessions and 300 samples per 30-minute bucket. A 5-second chart
gives 360 samples per bucket per session, so roughly 10–15 RTH sessions clears both.

A baseline is stamped with its expiry and **refused on a different contract** by default; on roll
day *Allow cross-expiry baseline* permits one from another dated contract of the same instrument,
and the health panel says when one is in use.

**Settings that deliberately have no default:** contract expiry, baseline artifact path,
resistance/support levels (declared manually, and must predate the window they are judged on), and
the recording directory plus explicit approval.

**Real-world use.** Sub-minute absorption confirmation at a level you already chose for other
reasons. It is the fastest thing in the suite and the least proven.

**Porting notes.** This is the only project with a real multi-assembly architecture, and it is the
best-organised code in the bundle:

| Path | What |
|---|---|
| `src/AuctionResponse.Core` | Platform-independent engine: events, windows, math, health, state machine. **~3,600 lines, no platform reference.** |
| `src/AuctionResponse.ATAS` | Host adapter: callbacks, threading, drawing surface, alerts. **This is the only part that needs rewriting.** |
| `src/AuctionResponse.Ui` | Panel layout, measured from real text metrics. No platform reference, so overlap is unit-tested. |
| `src/AuctionResponse.Replay` | JSONL recorder, log reader, deterministic replay, baseline artifact IO. |
| `src/AuctionResponse.Research` | Offline only: baseline construction, ridge regression, trade economics. |
| `tests/AuctionResponse.Tests` | The whole acceptance suite. Zero dependencies; exit code gates the deploy. |
| `spec/` | `DEFAULTS.json` and `TEST-VECTORS.json`, verbatim from the original handoff. |

**`spec/TEST-VECTORS.json` is the most valuable single file for a port** — it lets you prove the
new host produces identical results, which is the only way to know a port of a 5-second rule is
right. `COMPATIBILITY.md` documents the verified host API surface; regenerate its equivalent for
the new platform with the `_reflect` probe pattern.

---

## 4. Invariants that must survive the port

These are not style preferences. Each one is written down because getting it wrong produced a
**clean-looking chart that was silently wrong** — the worst possible failure for a trading tool.

### 4.1 One time zone: Central. Never two.

Settings entered in Central, math done in Central, labels printed in Central. Showing both zones is
itself the problem: an ET label forces a conversion on every read, and "08:30 ET" invites being
misread as the 08:30 CT open.

| | Central |
|---|---|
| RTH / cash session | 08:30 – 15:00 |
| Power hour | 14:00 – 15:00 |
| Sunday reopen | 17:00 |
| Friday close | 16:00 |
| **Maintenance halt** | **16:00 – 17:00** |
| Futures trade date rolls | **17:00**, not midnight |

Safe for CME equity index: Central and Eastern shift for DST on the same dates. The one real
exception is **London**, which shifts on different dates — a London session pinned to a Central time
drifts for ~3 weeks in March and ~1 in late October. Say so rather than hiding it.

**The 16:00 halt has shipped wrong three times across this suite.** 15:00 is the *cash close*, when
trading carries straight on; the genuinely empty hour is 16:00–17:00. Several indicators use that
empty hour to settle whether bar stamps are UTC or local — with 15 they never resolve and draw
*nothing*, with no error pointing at the setting. **Check this first when a clock-aware indicator
draws nothing.**

### 4.2 The bar clock must be resolved from evidence, never guessed

ATAS bar stamps are sometimes UTC and sometimes already exchange-local, with no reliable way to know
from the API. `TimeContext.cs` (254 lines, pure, copied across six projects — **keep them in sync**)
settles it from evidence, in order: the platform's market clock, then the newest bar against the
wall clock, then the daily maintenance halt showing up as an empty hour every day, then weekend gaps
on coarse charts.

**If it cannot tell, the indicator draws nothing and says why.** It never falls back to a guess,
because a wrong offset does not *look* wrong — it produces a clean, plausible, silently misplaced
level, and then a trade taken off it.

**On the port:** determine Quantower's stamp convention once, definitively, and then decide whether
to keep the resolver or replace it with a stated setting (which is what `pivot-decoder-v2` does).
Either is defensible. Mixing the two across projects is not.

### 4.3 Absent is not zero

A measurement that cannot be computed **abstains**. It never contributes a zero, it drops out of any
normalisation, and it is written **blank** to any log — never `0`, so a later regression never sees
a made-up neutral vote.

Zero is a real reading. Burying "I don't know" inside it moves every blended number for reasons
nothing on the chart could show.

### 4.4 Refuse, never truncate; refuse, never default

- An over-wide profile returns null rather than dropping levels.
- A period the loaded history does not cover is skipped with a note, not built from what happens
  to be there.
- No tick value from the platform → the box prints ticks and says why. **A guessed tick value would
  be wrong on every instrument but the one it was guessed for.**
- No volume in the feed → no VWAP, and no volume test. `1.5 ×` nothing is not a pass.
- An unparseable level or date is **logged loudly and printed on the chart**, never dropped quietly.

**There is a standing rule behind all of these: never add a default numeric fallback.** No
`|| 41.4`, no magic constant standing in for a missing ratio or tick value. If it is invalid the
conversion returns null and the UI says so. A wrong number on a prop-firm account is real money;
a silent fallback is worse than a visible error.

### 4.5 Rename a property when its meaning OR its default changes

ATAS restores a saved property value whenever the **name** still matches — on every chart and every
saved template. Changing what "session start" means, or flipping a `Show*` default to off, silently
keeps serving the old value under the new label, and the new default never reaches the chart.
Renaming is the only way to force it.

**Verify whether Quantower has the same persistence behaviour before changing any setting's meaning
during the port.** If it does, every property you redefine needs a new name.

### 4.6 Closed bars only

The forming bar changes underneath you. A session profile that includes a bar still being written
moves its own POC every tick; a volume test can pass on one tick and fail on the next; a wick that
later closes back inside would mitigate an order block live that history would have kept.

So: fold up to `bar − 1`, decide state transitions at bar close, and read live price and bar delta
from the last candle only at render time. Where an indicator does read the forming bar (Order
Blocks' label flow numbers, Current's provisional read), it is marked as provisional and **never
blended into the committed value**.

### 4.7 Catch every render layer separately and print its failure on the chart

A layer that throws takes down every layer after it, **silently** — including the readout that would
have reported the problem. There is no debugger on the render thread. Two builds shipped invisible
before this became standard. `oceans-read` wraps each layer in `Layer(name, ...)` and draws the box
**last**, specifically so a failure has somewhere to be printed.

### 4.8 Subscribe to data from initialisation, never from the render thread

A subscription requested from `OnRender` does not reliably deliver. When unsure of a call pattern,
scan the platform's own indicators doing the same thing rather than guessing from docs — that is
what the `_reflect/` projects are for.

---

## 5. Suggested port order

Bottom-up, so each stage de-risks the next.

| Stage | Port | Why |
|---|---|---|
| **0** | `_reflect` equivalent for Quantower | Dump the real API surface before writing anything. Every assumption in this bundle was verified this way, not taken from docs. |
| **1** | `oceans-sma` | Smallest. Teaches the line-series path and the `OnCalculate` call patterns. |
| **2** | `oceans-market-view` | Teaches the hand-drawn path, and forces you to settle the **bar clock** question (§4.2) once for everything else. |
| **3** | `oceans-orb-breakout` | First real signal. Small, fully tested, and its four fail-closed guards are a good audit of the new adapter. |
| **4** | The **footprint shim** | One adapter producing the neutral price-ladder type from `VolumeAnalysisData.PriceLevels`. Everything in Group B depends on this and nothing else should be written until it is right. |
| **5** | `oceans-profile` | The most reused pure logic in the suite. Validate against the `_test` harness before moving on. |
| **6** | `oceans-developed`, `oceans-delta`, `oceans-effort` | All stand on stage 4–5. Each has its own tested math. |
| **7** | `oceans-orderblocks` | May get *simpler* if Quantower can request another timeframe directly. |
| **8** | `oceans-depth`, `oceans-dom` | Only after confirming DOM and MBO availability on the target feed. |
| **9** | `oceans-anchor`, `oceans-current`, `oceans-pivot-decoder` | Large, stateful, and **uncalibrated** — port them decision-passive and run the logging protocols before trusting output. |
| **10** | `oceans-read` | Biggest. Everything it needs will exist by now. |
| **—** | `oceans-auction-response` | Independent of the rest. Port `Core` unchanged, rewrite only `AuctionResponse.ATAS`, and prove it with `spec/TEST-VECTORS.json`. |
| **—** | `oceans-crabel` | Port only if the file bridge is still needed. It draws nothing. |

---

## 6. Testing: what to keep

Every project ships a console test harness that needs no platform and no NuGet restore:

```bash
cd _test && dotnet run -c Release
```

`deploy.ps1` runs it first and **refuses to deploy on failure**. Reproduce that gate on the new
platform on day one.

**The rule that matters more than the check count: when a suite passes first try, mutate before
believing it.** Weak assertions survive coverage. Real examples from this codebase:

- A buy-imbalance test whose data gave the same verdict either way.
- A low-volume-node test whose edges were skipped for being *busy* rather than for being *edges*.
- `oceans-read` reported eight mutation survivors on the first pass; **every one was a real hole**,
  including an unreachable outside-bar guard whose claimed protection actually came from the
  surrounding `if/else`.
- `oceans-effort`: the mutation that dropped the value-area half of the regime filter survived the
  first suite. Chop *outside* value is a stalled directional auction, not fair value —
  `RotatingOUTSIDEValueIsNotTheCage` is the test that now catches it.

Current state: `oceans-read` 348 checks / 47 mutations all killed · `oceans-orderblocks` 176 checks
/ 38 of 38 mutants caught · `oceans-effort` 227 checks / 57 mutations, 55 killed, 2 equivalent ·
`oceans-anchor` 198 checks · `oceans-delta` 123 checks / 36 of 36 · `oceans-asiawick` 124 assertions.

**Three harness traps that produce convincing false results** — worth knowing if you reuse the
mutation scripts:

1. Source files are CRLF and mutation patterns are LF, so multi-line mutations silently fail to
   apply — and `grep -F` with a multi-line pattern matches line-by-line, so the presence check does
   not notice either. Every multi-line mutation then reports SURVIVED against code that was never
   touched. `_test/mutate.pl` joins patterns with `\r?\n` and exits 2 when it genuinely cannot find
   one.
2. **A mutation that will not compile is not a kill.** Counting it as one is how a suite full of
   holes comes to look complete. The harness reports `BROKEN` on any `error CS` and fails the run.
3. Never capture `dotnet run` through `$( )` — the persistent build server inherits the pipe and the
   substitution never returns. And always `touch` after restoring a `.bak`: `mv` restores the old
   mtime, MSBuild skips the rebuild, and the next run silently tests the previous mutation.

Also keep the `_smoke/` pattern: it constructs the indicator outside the platform and prints panel,
series count and every visibility flag. **A constructor that throws inside a trading platform is
invisible — the indicator simply never appears, and the platform log says nothing.**

---

## 7. What is NOT in this bundle

- **Compiled DLLs, `bin/`, `obj/`.** Source only.
- **`oceans-crabel/dash/secrets.local.json`** — held an LSE live-feed API key. Removed deliberately.
- **Cached market data** (`oceans-crabel/dash/cache/`, macro CSVs). Data, not source.
- **`oceans-current/_calib/sessions.csv`** — the author's own session calibration record. Removed; `calibrate.py` remains and will rebuild it from a fresh log.
- **`OceansGexProfile.dll`** is deployed on the origin machine but **no source project for it exists
  in `~/dev`**. It is not in this bundle and cannot be ported from here.
- **Third-party indicators** also installed alongside these (TradeGEX, GexRadar, MqLevels). Not
  authored here, not included.
- These are **not git repositories**, so there is no history to inspect — the `CLAUDE.md` files in
  each project serve that role and are worth reading first.

---

## 8. Honest status summary

| Project | Runs live | Validated edge | Notes |
|---|---|---|---|
| `oceans-sma` | yes | n/a | Utility |
| `oceans-market-view` | yes | n/a | Frame, not a signal |
| `oceans-profile` | yes | n/a | Measurement, not a signal |
| `oceans-developed` | yes | n/a | Measurement |
| `oceans-depth` / `oceans-dom` | yes | n/a | Measurement |
| `oceans-delta` | yes | n/a | Measurement |
| `oceans-read` | yes | n/a | Read, explicitly not a signal |
| `oceans-effort` | yes | **no** | Model arithmetic on its own rules, not a backtest |
| `oceans-orb-breakout` | yes | **no** | Rule fires; hit rate unmeasured |
| `oceans-orderblocks` | **not yet seen on a live chart** | no | Smoke-tested only |
| `oceans-anchor` | yes, calibration mode | **no** | Every threshold ships as a guess; CSV protocol not yet run |
| `oceans-current` | yes | **no** | 56 sessions ≈ coin flip; kill criterion defined and not yet met |
| `oceans-pivot-decoder` | yes | base rates only | Zone score never validated; quotes no rate under 8 samples |
| `oceans-asiawick` | indicator yes, strategy **disarmed** | **no** | Five-point phase gate, none closed |
| `oceans-auction-response` | yes, read-only | **no** | Will not arm without a frozen baseline |
| `oceans-crabel` | yes | n/a | Draws nothing; file-bridge reference |

**Nothing in this suite has a demonstrated, costed, out-of-sample edge.** They are measurement and
framing tools plus several explicitly uncalibrated signal engines, each shipping with the protocol
for calibrating it written down. Port them as such. The most valuable thing here is not any single
signal — it is the discipline in §4, which is what stops a trading tool from being confidently
wrong.

---

## Appendix A — File inventory

`pure` = no `using ATAS.*` / `using OFT.*`; ports unchanged. `ADAPTER` = rewrite for the target.

```
oceans-anchor              AnchorAbsorption.cs        pure     353
                           AnchorClock.cs             pure     423
                           AnchorLog.cs               pure     159
                           AnchorModels.cs            pure     211
                           AnchorProfile.cs           pure     449
                           AnchorSession.cs           pure     128
                           AnchorState.cs             pure     330
                           AnchorZones.cs             pure     360
                           OceansAnchor.Absorption.cs ADAPTER   486
                           OceansAnchor.Alerts.cs     ADAPTER   254
                           OceansAnchor.Core.cs       ADAPTER   678
                           OceansAnchor.History.cs    ADAPTER   221
                           OceansAnchor.Render.cs     ADAPTER   325
                           OceansAnchor.Zones.cs      ADAPTER   341

oceans-asiawick            AsiaWickClock.cs           pure     271
                           AsiaWickMath.cs            pure     441
                           AsiaWickRisk.cs            pure     158
                           AsiaWickIndicator.cs       ADAPTER   478
                           _strategy/                 ADAPTER   (ChartStrategy, ships disarmed)

oceans-auction-response    AuctionResponse.Core/*     pure    ~3,600  (14 files)
                           AuctionResponse.Ui/*       pure    ~1,050  (5 files)
                           AuctionResponse.Replay/*   pure      ~790  (5 files)
                           AuctionResponse.Research/* pure      ~310  (2 files)
                           AuctionResponse.ATAS/*     ADAPTER ~1,580  (7 files)
                           tests/*                    pure    ~2,900

oceans-crabel              CrabelMath.cs              pure     166
                           CrabelIndicator.cs         ADAPTER   449

oceans-current             BadgeModel.cs              pure     185
                           BiasEngine.cs              pure     558
                           BiasPanel.cs               pure     517
                           EventCalendar.cs           pure     203
                           Factors.cs                 pure     456
                           GexFeed.cs                 pure     238
                           Scorecard.cs               pure     113
                           SessionLogger.cs           pure     118
                           SessionState.cs            pure     446
                           StateMachine.cs            pure      96
                           TimeContext.cs             pure     258
                           TradeGexBridge.cs          pure     215
                           Triggers.cs                pure     346
                           OceansCurrentIndicator.cs  ADAPTER  1,529

oceans-delta               DeltaMath.cs               pure     623
                           Icebergs.cs                pure     228
                           TimeContext.cs             pure     254
                           OceansDeltaIndicator.cs    ADAPTER  1,705

oceans-depth               DepthMath.cs               pure     505
                           OceansDepthIndicator.cs    ADAPTER   858

oceans-developed           DevelopedClock.cs          pure     170
                           DevelopedMath.cs           pure     548
                           TimeContext.cs             pure     254
                           OceansDevelopedIndicator.cs ADAPTER 1,115

oceans-dom                 DomMath.cs                 pure     620
                           OceansDomIndicator.cs      ADAPTER   968

oceans-effort              DeltaSignals.cs            pure     172
                           EffortMath.cs              pure     844
                           MarketRegime.cs            pure     141
                           OptionLevels.cs            pure     217
                           RangeProfile.cs            pure     657
                           TradeBox.cs                pure     606
                           OceansEffortIndicator.cs   ADAPTER  2,333
                           OceansEffortCrypto.cs      ADAPTER     43

oceans-market-view         MarketModel.cs             pure     470
                           TimeContext.cs             pure     254
                           OceanMarketView.cs         ADAPTER   968

oceans-orb-breakout        OrbModel.cs                pure     338
                           TimeContext.cs             pure     255
                           OceansOrbBreakoutIndicator.cs ADAPTER 725

oceans-orderblocks         OrderBlocksClock.cs        pure     442
                           OrderBlocksMath.cs         pure     709
                           OceansOrderBlocksIndicator.cs ADAPTER 561
                           OceansOrderBlocks.Render.cs   ADAPTER 383

oceans-pivot-decoder       PivotDecoderAbsorption.cs  pure     291
                           PivotDecoderLog.cs         pure     223
                           PivotDecoderMath.cs        pure    1,779
                           PivotDecoderProfile.cs     pure     258
                           PivotDecoderStats.cs       pure     335
                           PivotDecoderZones.cs       pure     334
                           PivotDecoderIndicator.cs   ADAPTER  2,300

oceans-pivot-decoder-v2    PivotDecoderV2.cs          ADAPTER  1,909   (no pure/adapter split)

oceans-profile             PeriodClock.cs             pure     133
                           ProfileMath.cs             pure     762
                           TimeContext.cs             pure     254
                           OceansProfileIndicator.cs  ADAPTER  1,128

oceans-read                AcceptanceMath.cs          pure     299
                           AuctionMath.cs             pure     343
                           OrderflowMath.cs           pure     303
                           ProfileMath.cs             pure     630
                           ReadClock.cs               pure     156
                           ReadModel.cs               pure     718
                           RhythmMath.cs              pure     523
                           TimeContext.cs             pure     254
                           VwapMath.cs                pure     221
                           WaveMath.cs                pure     337
                           OceansReadIndicator.cs     ADAPTER  1,419

oceans-sma                 SmaMath.cs                 pure      72
                           OceansSmaIndicator.cs      ADAPTER   470
```

**Totals: 33,109 pure lines · 23,673 adapter lines.**

Note that `TimeContext.cs` appears in six projects as a copy and they are kept in sync by hand.
On the port, make it a shared library once.

---

## Appendix B — Reference documents inside `src/`

Each project keeps its own `README.md` (what it does and why) and `CLAUDE.md` (build rules, traps
hit, design decisions). Read the `CLAUDE.md` for any project before changing its behaviour — they
are the closest thing to commit history this codebase has.

Also worth reading:

- `src/oceans-auction-response/COMPATIBILITY.md` — the verified host API surface and capability
  matrix, generated by reflection against the installed platform. The model for what to produce for
  Quantower.
- `src/oceans-auction-response/BUILD-REPORT.md` — what was actually tested and what remains
  unverified.
- `src/oceans-auction-response/spec/TEST-VECTORS.json` — port-verification fixtures.
- `src/oceans-pivot-decoder-v2/PivotDecoderV2.cs` header — a numbered list of every platform
  assumption that was verified rather than assumed. Use it as the re-verification checklist.
