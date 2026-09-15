# ORB-IX — how each part works, and how to set it up

One indicator, twenty-odd displays, 121 chart inputs. This explains what each part measures, how
to set it, and — where it matters — what is actually known about it versus what is only drawn.

**Read this first.** Everything here **describes** the tape. Nothing predicts. Where a number was
measured, the measurement is quoted with its sample size; where nothing was measured, it says so
rather than offering a confident default. A setting with no measurement behind it is presented as
a trade-off, not a recommendation, because inventing a "best value" is the fastest way to make a
manual untrustworthy.

**Sizes and thresholds are per-instrument.** Almost every volume number below was measured on
MNQ. On ES, GC or NQ they are wrong by a large multiple. The parts that self-calibrate say so.

---

## Contents

1. [Getting a first chart working](#1-getting-a-first-chart-working)
2. [The status line — how to read it](#2-the-status-line)
3. [Order flow: the footprint displays](#3-order-flow-the-footprint-displays)
4. [Absorption tiers](#4-absorption-tiers)
5. [Opening range and sessions](#5-opening-range-and-sessions)
6. [Volume profiles: FRVP and AVP](#6-volume-profiles)
7. [VWAP](#7-vwap)
8. [Market structure: HH/LL, zones, shelves](#8-market-structure)
9. [Delta and flip levels](#9-delta-and-flip-levels)
10. [Fib, news, risk lines](#10-fib-news-and-risk)
11. [Setups by style](#11-setups-by-style)
12. [When something does not draw](#12-when-something-does-not-draw)

---

## 1. Getting a first chart working

Add ORB-IX to a chart. It starts with most things on. Three settings decide whether anything
useful appears:

| setting | why it matters first |
|---|---|
| **Fold interval, milliseconds** | How often everything recomputes. Lower is more responsive and more CPU. |
| **Product root override** | Blank derives the root from the symbol. Set it only if your broker's symbol naming defeats that. |
| **Volume analysis: force tick data** | Some connections serve no per-price volume. This makes the profiles read tick history instead. |

**Start on a 1-minute chart.** Most measured defaults in this indicator were taken on one-minute
bars, and everything works there. Move to other periods once you can read the status line.

---

## 2. The status line

One line per update, in the Quantower log; problems also reach the chart. It is the fastest way
to tell "switched off" from "on and the market is quiet" from "broken".

```
status: flow: 8 of 13 on — cluster statistics, stacked imbalance, …;
        stacked imbalance min volume 45, measured on 1-minute bars (7.2/session)
      · flow frame: stacked 23, absorption 0, auction 0, …, closed bars 1,334
      · aggressor convention: CONVENTIONAL, 100.00% agree over 931 judged
```

Three parts, and keeping them apart is the point:

- **`flow: N of 13 on`** — what is **switched on**.
- **`flow frame:`** — what was **found**. `stacked 23` means twenty-three marks exist; `absorption
  0` means the tool ran and found nothing.
- **`aggressor convention:`** — whether the feed's buy/sell flags can be trusted. See §3.

A tool that is on but cannot draw appears as a **problem**, with the reason.

---

## 3. Order flow: the footprint displays

ORB-IX builds a **footprint** — per-price buy and sell volume for every bar — from the trade
stream, and every display in this section reads it.

### 3.1 The aggressor convention, and why it is checked

Every footprint reading is signed by one flag: did the buyer or the seller aggress. **There is no
universal convention**, and a feed that reports it backwards produces a chart that is coherent
and exactly wrong — the hardest kind of error to notice.

ORB-IX does not trust the flag. It verifies it **against geometry**: a print at or above the ask
was a buyer lifting, at or below the bid a seller hitting. It needs no second data source, and it
reports what it found:

```
aggressor convention: CONVENTIONAL (flag matches where the print landed);
100.00% agree, 0.00% inverted over 931 judged
```

**Setting:** `Ingestion → aggressor` — `QuoteRule` (default), `VendorFlag`, `VendorFlagInverted`.

**Leave it on QuoteRule.** It derives the side from the quote at the instant of the print and
depends on no vendor's flag. Use `VendorFlag` only if your feed publishes no usable quote, and
then watch the agreement percentage. Below 500 judged prints the line says `not yet measured`
rather than claiming.

### 3.2 Stacked imbalance

Runs of consecutive **imbalanced diagonals** — where volume at one price on one side heavily
outweighs the opposing side one tick away. Three consecutive such rows is a stack.

| setting | default | what it does |
|---|---|---|
| `ratio` | 3.0 | How lopsided a diagonal must be. |
| `minLevels` | 3 | Consecutive rows required. |
| `minVolumeSource` | `Measured` | Where the volume floor comes from. |
| `targetMarksPerSession` | 7.2 | The rate the calibration aims for. |

**The volume floor is the setting that decides whether you see anything.** It is a property of
the contract, not a preference.

- **`Measured`** uses a swept table: **45 for 1-minute, 40 for 5-minute, 30 for 15-minute**, taken
  over 37 sessions and roughly 74 million prints of MNQ. **These are MNQ-only numbers.**
- **`Calibrated`** measures the floor from **the tape on your own chart**, targeting
  `targetMarksPerSession` instead. Needs 60 bars; below that it says so and uses the swept number
  as a stand-in.
- **`Fixed`** takes `minVolumeFixed` and asks nothing.

**Optimal setup:** on MNQ 1-minute, `Measured` is the better estimate — it rests on 37 sessions,
where a single session's calibration varies (the per-session floor ranged **32 to 65** across
that corpus). **On any other product, use `Calibrated`**, because the swept numbers are about a
different contract and nothing on the chart would tell you they are wrong.

`Calibrated` is also the only option on charts with no bar period (tick, range, Renko), since the
swept table is indexed by period.

### 3.3 Cluster statistics

A numeric table under each bar: volume, delta, delta percent, session delta, delta extremes,
trades, height in ticks, duration, volume per second.

**Setup:** pick the rows you actually read. The table is dense and every row costs vertical space.
Volume, delta and delta-percent is a reasonable three; add `MaxDelta`/`MinDelta` when you care
whether a bar's delta *path* went somewhere its close does not show.

### 3.4 Cluster search

Marks bars matching a filter you define — a volume threshold, a delta threshold, an
imbalance percent, a location in the bar.

| setting | default |
|---|---|
| `mode` | `Volume` |
| `minValue` | 2000 |
| `barsRange` / `priceRange` | 1 / 1 |

**Setup:** start with `mode: Volume` and set `minValue` from what you see in cluster statistics
on your instrument — 2000 is an MNQ 1-minute number. Widen `barsRange` to group adjacent bars into
one cluster when single bars are too noisy.

### 3.5 Unfinished auction

Marks bars whose **extreme traded on both sides** — price reached a high and still had buyers and
sellers there, so the auction at that price did not complete.

| setting | default |
|---|---|
| `bidFilter` / `askFilter` | 50 / 50 |

**Setup:** the filters are minimum volume at the extreme. Raise them until only the extremes with
real size behind them mark; 50 is an MNQ number and needs re-picking per product.

### 3.6 Big trades

Single prints above a size, optionally aggregated within a short window.

| setting | default |
|---|---|
| `minVolume` | 100 |
| `mode` | `Cumulative` |
| `aggregationWindowMs` | 100 |

**`Cumulative` with a 100 ms window is the setting that matters.** One 300-lot order routinely
arrives as many small prints in a few milliseconds; without aggregation you see none of it, and
with too wide a window unrelated trades merge. 100 ms is a reasonable default and worth tuning
once on your own feed.

### 3.7 DOM levels

The largest **resting** size in the order book, drawn beside price.

| setting | default |
|---|---|
| `withinLevels` | 50 |
| `topPerSide` | 2 |
| `volumeFilter` | 40 |
| `snapshotMs` | 250 |

**A limit worth knowing before you rely on it.** The book is sampled every `snapshotMs`. Measured
on MNQ, the **median resting order lives 11.7 milliseconds** (664,910 orders over ten minutes).
At a 250 ms sample you are reading a book roughly twenty generations stale. It is fine for "where
is the size sitting" and wrong for "is that order still there".

Needs a depth subscription. Without one it says `book no depth received` rather than showing an
empty ladder.

### 3.8 Live counter

The forming bar's buy, sell and delta, drawn beside price. No settings beyond on/off. The
cheapest useful display here.

---

## 4. Absorption tiers

**Heavy volume at a price the bar could not leave.** Passive limit orders absorbing aggressive
flow: big volume, small displacement.

The reading is **volume at the price ÷ how far the bar travelled, in ticks**. Ticks rather than
points, so one threshold means the same thing on MNQ, ES and GC. A bar that traded at a single
price spans one tick, not zero — it went nowhere, which is the strongest case of failing to
displace.

**Side comes from who aggressed at that price**, read from the footprint rather than inferred
from the close:

- **Bullish** — aggressive **sellers** absorbed (buyside absorption). Acts as support.
- **Bearish** — aggressive **buyers** absorbed (sellside). Acts as resistance.

**Two tiers, nested, each with its own switch:**

| tier | ratio | bullish | bearish |
|---|---|---|---|
| 1 — moderate | ≥ 6.3 | bright cyan | bright magenta |
| 2 — heavy | ≥ 7.9 | bright green | bright red |

| setting | default | what it does |
|---|---|---|
| `minVolume` | 70 | Contracts a price must carry to be considered. |
| `tier1Ratio` | 6.3 | Moderate threshold. |
| `tier2Ratio` | 7.9 | Heavy threshold. |
| `zoneTicks` | 4 | Prices this close collapse to one level. |
| `daysLookBack` | 30 | How long retired levels are kept. |
| `lineWidth` / `opacity` | 5 / 225 | Thickness and transparency. |

**Where the thresholds come from.** They were **solved for a frequency**, not chosen. Across 37
MNQ sessions and 353,669 qualifying levels, asking for roughly **10–15 moderate and 3–6 heavy
marks a session** gives 6.3 and 7.9. `minVolume: 70` sits at the 75th percentile of MNQ level
volume, so it selects the top quarter of prices.

**Zone width barely matters** — 2, 4 and 8 ticks solve to 6.50, 6.32 and 6.21, because levels that
strong are already sparse. Do not spend time tuning it.

**The line ends when price trades *through* it, not when it reaches it.** Every other level in
this indicator retires on a touch. An absorption line marks size that *held*, so it stands through
a retest — the moment it is most worth seeing — and ends only when price gets past it. Strictly
past: a bar whose low sits exactly on a support level has tested it.

**Optimal setup:**

- **Start at the defaults on MNQ 1-minute** and count what you get over a session. 10–15 and 3–6
  is the target.
- **Too many lines?** Raise `tier1Ratio` first. **Too few?** Lower it. The ratio is the knob; the
  volume floor is a coarse pre-filter.
- **On another product or period**, expect to re-solve both: level volume and bar travel each
  scale with the contract and the bar length. A rough approach is to set `tier1Ratio` so you get
  about a dozen marks a session and `tier2Ratio` about a third of that.
- **Run both tiers.** A level being tier 1 and not tier 2 is information; turning tier 1 off
  leaves only the rarest marks and you lose the context that makes them rare.

**What is not claimed:** that these predict anything. Absorption as an entry signal was tested on
MNQ over 25,745 episodes and came out a **measured null** — better than random by about one tick,
which is below the round-turn cost. Use these as a map of where size sat, not as entries.

---

## 5. Opening range and sessions

The opening range box, its high and low, midpoint, and extension targets — on **every** configured
session, not just one.

| setting | what it does |
|---|---|
| `Draw the opening-range box` | The range rectangle. |
| `Draw the range high and low` | The two edges as levels. |
| `Draw the range midpoint` | The midline. |
| `Draw the extension targets` | Projections beyond the range. |
| `Draw sessions that closed before this indicator loaded` | Back-draw earlier sessions. |
| `Draw key levels near price` | Hide levels far from current price. |
| `Level band, % of average daily range` | How near is "near". |

**Optimal setup:** turn on **key levels near price** with a band of a few percent of ADR. A chart
with every session's range drawn is unreadable; a chart with only the ranges price can actually
reach today is a map.

**On the entry logic specifically:** opening-range breakout entries were tested here and came out
**no better than random**, with the retest variant significantly negative across four products.
The ranges are drawn as structure — where the day organised itself — not as a signal.

---

## 6. Volume profiles

Two profiles: **FRVP** (fixed range) and **AVP** (anchored).

| setting | default |
|---|---|
| `row size (ticks)` | per config |
| `value area %` | 70 |
| `max width (% of range)` | per config |

**FRVP** takes an explicit start and end, or a click-select anchor. **AVP** takes an anchor —
session open, a custom UTC time.

**Both need per-price volume from your connection.** Where the platform does not serve it, ORB-IX
falls back to **tick history** and says which it used. Where neither is available, it reports the
gap rather than drawing an empty profile — `0 of 85 in-range bars carry per-price levels` is the
message, and it means the data is missing, not the market.

**Optimal setup:** row size wants to be coarse enough that the profile has shape. On MNQ a few
ticks per row is readable; one tick per row is noise. Value area 70% is the convention and there
is no measurement here to argue for another number.

**On the levels themselves:** POC, value-area edges and high-volume nodes were each tested as
predictive levels on this instrument and came out **non-predictive**. They describe where trade
happened. That is genuinely useful for knowing where you are; it is not an edge.

---

## 7. VWAP

Session VWAP and an anchored VWAP, each with standard-deviation bands.

| setting | default |
|---|---|
| `session line` / `anchored line` | on |
| `band multipliers` | comma list |
| `anchored colour` / `custom anchor, UTC` | — |

**Optimal setup:** one or two bands. Three or more and the chart becomes a ladder you stop
reading. The session line is the useful one intraday; the anchored line earns its place when you
have a specific event to anchor to.

**Measured:** VWAP-based *gates* — filtering entries on which side of VWAP price sits, or how far
it has stretched — went **net negative out of sample** when tested here. The line is a fair-value
reference, not a filter.

---

## 8. Market structure

### HH/LL

A higher-high / lower-low read, with **undecided bars marked separately** rather than forced into
a direction — which is the part most structure tools get wrong.

| setting | what it does |
|---|---|
| `left bars` / `right bars` | Pivot definition. |
| `colour the bars by trend` | Bar colouring. |
| `undecided bar colour` | The third state. |
| `show support/resistance` | Draw levels at the pivots. |

**Optimal setup:** `left`/`right` bars control sensitivity — larger values find fewer, more
significant pivots. Keep the undecided colour visibly different from both trend colours; the
whole value is being able to see when structure has not decided.

### Zones — HTF FVG and order blocks

Higher-timeframe fair-value gaps and order blocks, drawn on the current chart.

| setting | what it does |
|---|---|
| `timeframe` | Which HTF to read (e.g. `4h`, `1h`). |
| `max age, HTF bars` | Drop zones older than this. |
| `max height, % of ADR` | Drop zones too tall to be useful. |
| `rejection blocks` | Include rejection blocks. |

**Optimal setup:** `max height` is the setting that saves the display. An unfiltered FVG set
includes gaps half the day's range tall, which are not levels in any useful sense. Cap it at a
modest share of ADR.

### Shelves

Horizontal price shelves — "lines in the sand" — found by looking for prices that repeatedly held.

| setting | what it does |
|---|---|
| `look back (bars)` | Search window. |
| `needs this many bars` | Minimum touches. |
| `needs volume of` | Minimum volume. |
| `needs a lean of (%)` | Directional bias required. |
| `needs a share of the window (%)` | Concentration required. |
| `refuse if one print exceeds (% of volume)` | Rejects a shelf that is one large trade. |
| `broken after (ticks)` | How far through counts as broken. |
| `keep the ones that broke` | Retain broken shelves. |

**The last filter is the one to understand.** `refuse if one print exceeds` stops a single large
trade from creating a "shelf" — one order is not repeated defence of a price, and without this
filter it looks identical to one.

---

## 9. Delta and flip levels

**Delta panel** — cumulative delta as a sub-panel. Height and colours are the settings.

**Flip levels** — prices where cumulative delta **changed sign**.

| setting | what it does |
|---|---|
| `confirm after (contracts past zero)` | How far past zero before a flip counts. |
| `mark the session's first side` | Mark the opening bias. |
| `this session only` / `most to draw` | Scope and cap. |

**Optimal setup:** `confirm after` exists because delta crossing zero by one contract and crossing
back is not a flip. Set it to a size that is meaningful on your instrument — a few tens of
contracts on MNQ. Too low and every wobble is a level; too high and you get them late.

**Measured:** delta divergence — price making an extreme that delta does not confirm — was tested
across 35 sessions and came out **null**; the best-powered cell lost money outright. Flip levels
are drawn as structure, not as a fade signal.

---

## 10. Fib, news and risk

**Fib** — a fan and retracement set over the look-back's extremes, with the golden pocket and
optional extensions. Anchor and styling are the settings. A geometric overlay; nothing here was
measured.

**News** — shades Tier-1 economic-release windows.

| setting | default |
|---|---|
| `shade Tier-1 windows` | on |
| `minutes before` / `minutes after` | window width |

**Setup:** worth having on regardless of how you trade. The value is not prediction — it is
knowing the next two minutes are not a normal market.

**Risk** — horizon lines showing where a loss limit sits at the current size. These read from the
configuration's account block; in the shared build that block is neutral, so the lines will be
inert unless you configure your own.

**Cost meter** — round-turn cost for the product, so the geometry you are looking at can be
compared against what it costs to trade it. This is the single most under-used display here: a
setup worth four ticks on an instrument that costs three to trade is not a setup.

---

## 11. Setups by style

Four starting points. Each is a **starting point** — the measured caveats above still apply.

### Scalping order flow, 1-minute

On: cluster statistics (volume, delta), live counter, stacked imbalance, absorption tiers 1 and 2,
big trades, DOM levels, aggressor convention.
Off: profiles, fib, zones, HH/LL.

Rationale: everything on the chart is about the last few minutes of tape. Structure at this
horizon is clutter.

### Intraday structure, 5-minute

On: opening range (key levels near price), HH/LL with support/resistance, session VWAP with one
band, FRVP anchored to the session, absorption tier 2 only, news shading.
Off: cluster statistics, cluster search, DOM levels, live counter.

Rationale: you want the day's skeleton, not the tape. Tier 2 only because at this horizon you want
the few strongest walls, not a dozen moderate ones.

### Level-building for the session, before the open

On: profiles (FRVP over the prior session, AVP from the prior open), shelves, zones, prior
sessions' opening ranges, absorption tiers with a 30-day look-back.
Off: everything live.

Rationale: this is map-making. The 30-day absorption look-back is doing real work here — it is the
one display that remembers where size held over weeks.

### Reading a single move in detail

On: cluster statistics (all rows), footprint, absorption both tiers, big trades, DOM levels,
unfinished auction.
Off: everything else.

Rationale: maximum resolution on one thing. Unreadable as a standing layout, excellent for
answering "what just happened".

---

## 12. When something does not draw

Work down this list; the status line answers most of it.

| symptom | cause |
|---|---|
| `flow: waiting for the chart's bars` | The platform has not served bars yet. Transient. |
| `no measured volume floor … not time bars` | `minVolumeSource: Measured` on a tick/Renko chart. Switch to `Calibrated` or `Fixed`. |
| `STAND-IN while calibrating (N bar(s) of tape, 60 needed)` | `Calibrated` is still accumulating. Wait, or seed more history. |
| `0 of N in-range bars carry per-price levels` | Your connection serves no per-price volume. Turn on **force tick data**. |
| `book no depth received` | No depth subscription on this connection. |
| `aggressor convention: N of 500 print(s) needed` | Not enough prints judged yet. Wait. |
| `aggressor convention: UNDECIDED` | The flag does not track the book. Treat every delta reading as suspect until it settles. |
| A tool is on and nothing appears, no problem line | The tool ran and found nothing. Check `flow frame:` — that is what it is for. |
| Absorption draws far too many lines | `tier1Ratio` too low for this product/period. Raise it. |

**The general rule:** `flow:` is what is on, `flow frame:` is what was found, and a **problem**
line is something broken. If none of the three explains it, the setting is not the issue.
