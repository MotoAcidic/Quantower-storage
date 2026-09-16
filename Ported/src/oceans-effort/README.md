# Ocean Effort

An ATAS indicator built from the Fabio Valentini interview: value migration, effort against
result, and absorption, drawn on the bars they came from, with a setup marked only where all
three agree.

Shows in ATAS as **Ocean → Oceans Effort**. `.\deploy.ps1` runs the tests, builds, smoke-tests
the constructor and copies the DLL to `%APPDATA%\ATAS\Indicators\`. **ATAS only reads that folder
at startup, so restart the platform after deploying.**

## The trade box

The box is the indicator. Everything the model knows is in it, in one block, colour-coded:
green where the reading is bullish, red bearish, amber for absorption, grey for anything that is
silent or unknown.

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
! no tick value from the platform, so risk is shown in ticks only
```

**READ** is the three layers of the model. **TRADE** is the setup they produced, with the stop,
the 1R target and where the trail has ratcheted to. The strip at the bottom is what became of it:
`LIVE` with the open result, `CLOSED` with the result off the trail, `NOT A TRADE` for a setup
whose stop is beyond the cap, or `UNRESOLVED` where one bar touched both target and stop.

Money appears only when the platform supplies a tick value. When it does not, the box shows ticks
and says why — a guessed tick value would be wrong on every instrument but the one it was guessed
for.

## What it draws on the chart

Deliberately little, so the candles stay readable:

- **The effort ribbon**, a strip along the bottom of the panel: one cell per bar, green where up is
  the cheaper direction to travel and red where down is, brighter the wider the gap. It never
  covers price.
- **Absorption marks** at the extreme the losing side pushed into, with the size that traded.
- **The live trade's levels** — entry, stop and 1R target carried across the chart and labelled,
  for the one setup the box is describing. Past trades leave nothing behind.
- **The trail**, stepping to the far side of each aggression bar in the trade's direction. It never
  loosens and it ends at the first bar that traded through it, marked with a cross.
- **A small arrow** on each setup bar. Over-cap ones are greyed and tagged `over cap`.

Off by default, available in the settings: shading each bar's value area, the value-midpoint
track, and an outline around each run of bars holding one direction.

## The three layers

**1. Value migration** — each bar's own volume value area against the bar before it. Migration
needs the band *and* the point of control to move the same way: a bar that stretched upward while
trade concentrated lower has widened, not migrated.

**2. Effort** — **contracts traded per tick of net progress**, summed separately for the up bars
and the down bars in a rolling window. If the last twenty bars bought 8 ticks up for 800 contracts
and 4 ticks down for 1600, up cost 100 per tick and down cost 400, so up is cheaper by 4×. One
side has to be clearly cheaper (default 1.35×) before this says anything.

**3. Absorption** — a bar where one side aggressed hard and got nothing: heavy volume against the
recent average, lopsided delta, and a close that went nowhere or the other way.

**Setups** — marked only where all three agree and that side was not just absorbed. Two rules keep
it honest, and both exist because earlier versions got them wrong:

- **One setup runs at a time.** While a stop is still standing, the bars that keep agreeing with it
  are the same idea, not new ones.
- **A stop further away than the cap is greyed out** and labelled `over cap`. The reading was real;
  the trade was not there. The stop is never pulled in to fit — that would be a different trade
  wearing this one's confirmation.

## What this is not

The interview describes a **proprietary** model. Nothing in it says how the boxes are computed, so
none of this is a reproduction of it — it is the same three ideas built from data that is actually
on the chart, where every number can be traced back to a print.

In particular, the effort layer measures **what price cost to travel**, not what is resting in the
book. The interview reads a cheap direction as a thinner book on that side; that is an
interpretation and it is not asserted here. Resting size is not visible to this indicator and is
not being inferred. For resting orders see `oceans-depth`; for where size actually got filled see
the absorption shelves in `oceans-profile`.

The 1R target is drawn as a reference. The trail is what the drawing actually follows, and where a
single bar touched both target and stop the readout says so rather than picking one — bars do not
record which came first.

## Where the honesty lines are

- A bar with no per-price data has **no value area**, not one guessed from its high and low. The
  migration layer goes dark for that bar and the readout counts how many are in view.
- A bar spanning more ticks than the cap is **refused, not truncated** — a value area computed
  from part of a bar looks plausible and is simply wrong.
- Absorption needs a real average of recent bars to call a bar heavy. With no yardstick it reports
  nothing rather than falling back to a fixed contract count, which would mean something different
  on every instrument.
- A window where only one direction made progress is labelled **one-sided**, not dressed up as a
  comparison. It can be hidden entirely.
- A bar that printed no volume buys no progress, so a data gap cannot make a direction look free.

## Layout

- `EffortMath.cs` — the whole model. No ATAS types, so it is tested against references that share
  none of its code.
- `TradeBox.cs` — every word and number in the box, with no idea how it is drawn. A string built
  inside `OnRender` can only be checked by looking at a chart; this one is checked by tests.
- `OceansEffortIndicator.cs` — reads candles and draws. Each layer is caught on its own, and a
  layer that throws is reported *in the box* rather than silently taking down every layer after it.
- `_test` — the checks. Every reference is worked out independently: by hand, by brute force, or
  as an invariant. Sixteen mutations to the model were confirmed to break them; the one that
  survived was equivalent code, not a gap.
- `_smoke` — constructs the indicator outside ATAS and prints every setting, because a constructor
  that throws is invisible inside the platform.

## Settings worth knowing

| Setting | Default | Why |
| --- | --- | --- |
| Migration needs (ticks) | 2 | Below this a bar is drawn balanced rather than migrating. |
| Effort window (bars) | 20 | How far back the contracts-per-tick comparison looks. |
| One side must be this much cheaper | 1.35 | Anything closer is noise. |
| Minimum progress (ticks) | 8 | A direction that barely moved has no real cost per tick. |
| Volume vs recent average % | 140 | What counts as a heavy bar. |
| Average over (bars) | 30 | The yardstick. Nothing is flagged before this many bars load. |
| Stop buffer (ticks) | 2 | How far beyond the value area the stop sits. |
| Text size | 10 | The trade box. Raise it if the box is the thing you read. |
| Most risk worth taking (ticks) | 40 | Beyond this a setup is greyed out rather than taken. Zero switches the check off. |
| Read the forming bar | off | The bar in progress changes with every trade; the model is measured at the close. |

Related: `oceans-profile`, `oceans-depth`, `oceans-market-view`, `oceans-sma`.
