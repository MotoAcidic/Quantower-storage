# CLAUDE.md

Ocean Effort — ATAS indicator built from a transcript of Fabio Valentini describing his NASDAQ
scalping model: a range volume profile with cluster areas, value migration, effort vs result, and
absorption, with a setup drawn only where all four agree. Started 2026-08-22. Appears as **Ocean → Oceans Effort**. See README.md for the
trade box format. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`OceansEffortIndicator.cs` layout only · `EffortMath.cs` the per-bar layers + `SetupTracker` ·
`RangeProfile.cs` the profile, clusters and location · `TradeBox.cs` every word and number as a
pure model · `_smoke/` · `_test/`.

**Do not label two box rows the same.** The PROFILE section's `VALUE` row collided with the READ
section's, and `Find(box, "VALUE")` silently returned the wrong one — the per-bar row is `MIGRATION`
now, which is what it actually reports.

## What his screens actually look like — four shots, four components

The trader sent screenshots of the video. They settled the drawing, and each one maps to a layer:

1. **Effort / cluster boxes** — filled rectangles with the top and bottom PRICE printed at the left
   edge, starting where the area formed and running forward. Red where sellers control, green where
   buyers do. Not full-width bands: a box that starts where it printed.
2. **The order track** — every candle is a thin body with its OWN horizontal volume profile beside
   it, plus dotted lines at that candle's value edges. This is the "read like a profile" he meant:
   value migration is visible as the shape moving bar to bar, not asserted by a line.
3. **Levels carried across** plus VWAP and initial-balance lines.
4. **Size bubbles** — circles on the candles with a contract count inside (60, 73, 105, 345), green
   for buy-side, purple for sell-side, radius scaled by size.

**The honesty line on bubbles:** it is size at ONE price inside one bar. Whether 345 contracts were
one participant or twenty is a market-by-order question and nothing here can answer it, so the
setting is called "size at one price" and the docs say so. The transcript agrees — *"you can set
with MBO data, so to see if these 345 orders were from one single market participant or were from
multiple."*

## Two variants, and thresholds that measure themselves

`Oceans Effort MNQ` and `Oceans Effort Crypto` (`OceansEffortCrypto.cs`, a thin subclass). Nothing
about the READ changes between them — value migrates, effort is paid, size gets absorbed and delta
refuses a new extreme on any instrument. What changes is every threshold ever expressed in
contracts or ticks, because a futures contract and a coin are not the same unit.

**The real fix is that the two absolute thresholds now measure themselves.** Zero means measured:
- `DivergenceMinGap` → 2× the **median** |bar delta| over the lookback
- `MaxRiskTicks` → 3× the **median** bar range in ticks

Median, not mean, so one 5000-lot bar or one limit candle cannot set the scale. Re-measured every
25 bars, and the status line prints what it worked out to — a threshold is never a number you
cannot see. Both default to zero now, on both variants.

## Outside levels — options positioning, honestly

The trader has option levels on his chart and wanted the model to use them. **There is no API for it on
his plans**, verified live rather than from notes: Massive's options chain needs a paid Options
plan; FlashAlpha blocks QQQ/SPY (Basic) and NQ/ES/MNQ (Growth) and allows 5 requests a DAY.

So the bridge reads a file he owns: `%APPDATA%\ATAS\Ocean\levels.csv`, one
`price, label, support|resistance|pivot` per line, re-read whenever it changes.
`market-apis/ocean_levels.py` fills it automatically **if** a plan ever allows, and otherwise says
exactly what is blocked and writes nothing — a stale call wall looks exactly like today's.

**Levels do not pick trades. They refuse them.** No long into resistance within `WallVetoTicks`
overhead; no long whose one-to-one target sits behind a wall. That is the one thing option
positioning is unambiguously good for: naming where a scalp has no room. Drawing them is OFF by
default — he already has them on the chart, and this is here to USE them.

**An unrecognised `kind` stays a pivot and is counted as a bad line.** "Call Wall" obviously reads
as resistance to a human; guessing a side from a label would be the code inventing a bias out of a
string. Same reason bad lines are counted rather than skipped: a file that half-parsed looks
exactly like a file that was meant to be half that size.

**Nothing converts between price scales.** NQ and MNQ quote the same index level so NQ levels are
MNQ levels untouched; QQQ does not, and the ratio moves. See [[feedback-no-ratio-fallback]].

## Costs are applied to the record, not to trades

`RoundTurnCost` in money, converted with the platform's tick value. At one-to-one, commission is
most of the answer, so the status line reads `-46t -> -70t after 24t of cost`, or says
`(before costs)` when it is zero. Applied to the RECORD rather than per trade because a per-trade
figure would imply this knows the fill quality, and it does not.

## The regime filter is the biggest single thing in the model

The trader's own research notes on the strategy, and the screenshot that came with them, named the gap:
Fabio *"explicitly avoids trading during choppy, non-trending markets... he views his
trend-following model's failure in consolidation as a necessary physiological risk, choosing to
preserve capital rather than trade in poor conditions"*, and *"I want always to engage when I have
directional auction OUTSIDE the value."*

The screenshot was a long chop with `-34t, -24t, -2t, -34t, -6t, -33t, -25t` stacked on top of each
other. The model was trading the cage — the one place the source says not to.

`MarketRegime.cs` answers two questions from what happened, no oscillator:
- **How much of the window's closes sat inside the value area** (default 65% for balance)
- **How much of its range turned into progress** — `|close - open| / (high - low)`. A market that
  covered two hundred ticks and finished ten from where it started kept nothing. Under 30% is
  balance, over 50% is a trend, between is Mixed.

**Both conditions are needed for balance, and a test almost missed it.** Chop happening OUTSIDE
value is a stalled directional auction, not fair value — calling it the cage stands the model aside
in the one place it should be watching. The mutation that dropped the value-area half survived the
first suite; `RotatingOUTSIDEValueIsNotTheCage` is the test that now catches it.

**Unknown refuses.** Not knowing what kind of market this is is not the same as knowing it is a good
one, and the filter exists precisely to stop the model trading where it is known to lose.

The cage is drawn only while balanced — a box that is always there says nothing.

## The status line replaces the box for someone who does not want the box

One line, top left: the regime with its two numbers, and the model's own record on the bars in view
(`8 closed  3W 5L  -46t on the trail`). It is the model's arithmetic on its own rules, not a
backtest, and it carries no costs. The trader had switched the trade box off; the two things worth
knowing at a glance should not go with it.

**Labels only on the newest few trades** (`LabelledTrades`, 3). Labels stack on top of each other
the moment trades cluster, which is exactly when you most need to read them.

## He is a DELTA SCALPER — divergence is the trigger

The trader: *"the vwap and vah and val change when i scroll, that is not ideal, and you are adding lines
to my chart — i don't need more lines. i need a massive improvement for a delta scalper and the
trades are not obvious."* Three separate things, all acted on:

**1. The anchor was wrong and it was a real defect.** The drawn profile was built from the VISIBLE
range, so value high, value low and VWAP moved every time the chart scrolled. A reference level that
depends on the scrollbar is not a reference level. Everything is now built from a FIXED lookback
(`LevelLookback`) ending at the newest bar — the same profile the setup gate uses. The
visible-range idea is gone; it was mine, and it was wrong.

**2. Lines off by default.** `ShowValueLines`/`ShowVwap` renamed to `DrawValueLines`/`DrawVwap` and
defaulted off (renaming forced it past saved settings). Zones and boxes carry the same information
without another three lines to read past.

**3. Divergence is now the primary trigger.** `DeltaSignals.cs`: a new low the selling did not pay
for (cumulative delta HIGHER at the new low than at the swing low it broke), or a new high the
buying did not. That is the delta scalper's read — the side in control spent more and got less. It
becomes a setup in its own right with the stop beyond the extreme just made, and it still has to
come off a level. The continuation read (migration + effort + bar) survives as the slower one; both
feed the same one-at-a-time tracker.

**`SetupTracker` no longer decides what a setup IS.** It is handed a candidate and owns only the
lifecycle. With two trigger kinds, a tracker that knew about both would be the wrong shape.

**Trades are drawn as ZONES, not lines.** A red block entry-to-stop, a green block entry-to-target,
running from the entry bar to where the trade ended, with `LONG 96.12  12t  divergence` on it. Three
thin lines were what this was, and they were not obvious — you should not have to hunt your own
chart for your own signal. Over-cap setups get a dotted outline and no fill, because a filled block
is the indicator saying "take this".

## Glanceable, not exhaustive — the ranking rule

The trader, on the first live screenshot of all this: *"i want to be able to glance at this chart and
know where the biggest orders are, it looks hard to read, and i also want the biggest delta levels
shown but not in a way that takes a lot of chart real estate."* He had already turned the trade box
off.

**Rank across the screen, never per bar.** A per-bar cap marks the busiest price of a quiet bar as
loudly as a real one, and then nothing stands out. `RangeMath.Biggest` takes every single-price
print in view, ranks them against each other and keeps the top N (8). Marks are solid tags with the
count, on a thin line across the chart so the price is readable off the axis. Ties break on bar then
price so the marks do not reshuffle between frames.

**Biggest delta levels are a strip, not a column.** `RangeMath.BiggestDelta` ranks price levels by
**absolute** delta — a heavily negative level is as much "biggest delta" as a positive one — and
they draw as a 78px strip at the edge: a bar out from a centre line, right for buyers, left for
sellers, number outside the strip so it never lands on the bars. **The full profile column is off by
default** (renamed `ShowProfile` → `ShowProfileColumn` to force it past saved settings); the value
lines and this strip answer the same question in a sliver.

**When the box is off there is nowhere for a warning to go**, so the one that matters — the order
track silently not drawing because bars are too narrow — falls back to a single line top-left.

**Saturation guard, because a threshold that catches everything marks nothing:** the order
track refuses to draw under `TrackMinBarWidth` pixels and says so. (The bubbles' own saturation
warning was deleted when ranking replaced thresholding — ranking cannot saturate.)

## The profile is the frame — VRVP and cluster areas

The trader: *"vrvp you keep building something over and over this is not what i want!!!!! Fabio
Valentini's new orderflow strategy... and the cluster search for confirmation, review the
transcript again."* The parts of the transcript that had been skipped:

- **The profile over a range** — "you can measure from the bottom swing to the top swing and remove
  the price", with **discount** below value ("imagine you are in the Apple store"), premium above,
  and the balanced middle he calls *the land of nowhere*.
- **Cluster areas** — "this red cluster area... an area where we saw previously strong absorption to
  the downside. The buyers tried to push, they got absorbed heavy and this area printed."
- **Engaging only from levels** — "use your confirmation to buy from levels that are relevant for
  the order flow", and *"I want always to engage when I have directional auction outside the value."*

`RangeProfile.cs` builds a dense profile over a bar range, finds **cluster areas** (runs of adjacent
heavy prices, split into *absorption* where the aggression netted off and *aggression* where a side
drove), and places a price in it (`Location`: premium / in value / discount, distance to POC, and
which cluster or value edge it stands on).

**The fractal part is the range.** The drawn profile is built from **the bars on screen**, so zoomed
out it is the day, zoomed in it is the swing — one tool at every degree, nothing reconfigured.

**But a setup is judged against a FIXED lookback** (`LevelLookback`, 150 bars), never the visible
range. A signal that changes when you scroll is not a signal. Same maths, two degrees, and they are
kept apart on purpose.

**Cost control:** the setup profile is only built on bars where the three cheap layers already agree
(`EffortMath.MightBeSetup`). Building one per bar is a full pass over the lookback to answer a
question that is almost always already "no".

## The four layers — the first three per-bar and causal

No clock, so no `TimeContext` and none of that class of trap.

1. **Value migration** — each bar's own value area vs the previous bar's. Requires the band AND the
   point of control to move the same way; a bar that widened upward while trade concentrated lower
   has not migrated.
2. **Effort** — contracts traded per tick of net progress, up bars and down bars summed separately
   over a rolling window. Cheaper direction = where price has been travelling more easily.
3. **Absorption** — heavy, lopsided, no result. Same idea as `oceans-profile`'s but per bar rather
   than per price level.

4. **Location** — the entry must stand on a cluster area or an edge of value that argues the same
   way. An unknown location refuses too: not knowing where price is is not the same as it being
   fine.

A setup is drawn only where all four agree; stop beyond that bar's value area, target 1R, then a
ratcheting trail that **terminates** at the first bar trading through it.

## The line held here — matters for anything built from trading content

Fabio reads a cheap direction as "the book is thinner on that side." That is an *interpretation of*
a measurement. The indicator does not assert it — it measures contracts per tick and says so. A
proprietary model cannot be reproduced from a video; the honest thing is to build the ideas from
data on the chart and say plainly that it is not his model.

## The trade box IS the deliverable — the chart was the wrong one

The trader, twice: *"this is not a clear read and very difficult to interpret and drifted from the
original concept... we need the transcript interpreted in a clear way like a trade box with all the
information of the trade in a color scheme as you can see i am very granular in how i trade."*

So `TradeBox.cs` builds the whole thing as a pure model — READ section (the three layers), TRADE
section (side, entry, stop, target, trail), then a status strip: LIVE / CLOSED / NOT A TRADE /
UNRESOLVED — and the indicator only lays it out. The chart keeps almost nothing: ribbon, absorption
marks, the live trade's three levels, the trail. **Value shading and the value track are OFF by
default** — the value track drawn on the candles was the noise he was reacting to.

## Three display rules from the unreadable first chart

The screenshot showed three setups a few ticks apart on consecutive bars, risk labels of
161/168/208 ticks, and effort boxes spanning each run's full high-to-low, covering the candles.

1. **One setup runs at a time.** While a stop still stands, bars that keep agreeing are the same
   idea, not new ones. This alone removed most of the clutter.
2. **A stop beyond the risk cap is greyed out, never pulled in to fit** — pulling it in would be a
   different trade wearing this one's confirmation. Exception: a tradeable setup arriving behind an
   untradeable one replaces it.
3. **A layer that covers price is the wrong layer.** Effort became a ribbon along the bottom of the
   panel, one cell per bar; value migration became a track through the value midpoints. Boxes
   survive as an off-by-default option, drawn around value areas not extremes.

## Traps hit here

- **Money comes from `DataProvider.TradingManager.Security.TickCost`** (reflected; `InstrumentInfo`
  has TickSize only, no cost). Zero when the platform has not said — the box then prints ticks and
  a warning instead of money.
- **`ValueAreaPercent` hides `Indicator.ValueAreaPercent`**, a real base property. Renamed to
  `BarValuePercent`.
- **`ShowValueBoxes` etc. were renamed when their defaults flipped to off** — ATAS restores by
  property name, so the new default would never have reached his chart.
- **A trailing stop with no exit condition keeps drawing a live trade hours after it ended.** It
  stops at the first bar through it. Relatedly the readout **refuses to resolve** a bar that touched
  both target and stop, because bar data does not record which came first.
- The readout and trail pick the last setup **at or before the last visible bar**, so scrolling back
  reads as living through it did rather than describing a later trade.

## Testing

227 checks, all references worked independently. The setup lifecycle was moved OUT of the indicator
into `SetupTracker` specifically so the one-at-a-time rule could be tested without ATAS — worth
doing for any stateful rule that lives in a render loop. Everything passed first try every round, so
**57 mutations have been applied and 55 broke a named test**; both survivors were equivalent code (the Trailer's
ratchet makes the offer-before-test reorder a no-op; a zero wall-veto reaches nothing either way). The mutations worth keeping in mind
for profile work: walking a sparse level list instead of a dense one, letting a cluster stride
across a change of character, and reading the absorbed side as the winner rather than the pusher.
