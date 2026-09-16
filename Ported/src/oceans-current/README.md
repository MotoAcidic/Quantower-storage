# Ocean's Current

One directional state for the session — **LONG / SHORT / NEUTRAL** — held until the evidence for
it stops standing up. Appears in ATAS as **Ocean → Oceans Current MNQ**, overlaid on the price
panel.

It is not an entry tool and draws no arrows. It frames and gates; the orderflow confirmation stays
discretionary. The value is not that it produces a score — anything can produce a score — it is
that it produces about **three of them a day**.

## What it reads

Four factors, each voting in −100…+100, blended on weights you can see and change:

| | Factor | Default weight | What it says |
|---|---|---|---|
| F1 | VWAP position | 0.20 | Where price sits against the anchored VWAP, measured in that session's own spread. ±2σ saturates. Halved when the VWAP slopes against it. |
| F2 | CVD thrust | 0.20 | How hard cumulative delta pushed over 20 bars, priced against how hard it usually pushes. Halved and flagged on divergence. |
| F4 | Overnight inventory | 0.10 | Where the overnight closed inside its own range, and which way it gapped the open. Frozen at 08:30, constant all session. |
| F5 | Structure ladder | 0.20 | Price above or below PDH / PDL / ONH / ONL, plus a decaying penalty where a break was swept and reclaimed. |

**F3 (value migration) and F6 (30-minute one-timeframing) are not built.** They appear in the
settings, disabled, and they abstain rather than voting zero. Turning them on changes nothing yet.

Then the gamma regime out of Tide Engine transforms the *weights* — it never invents direction:

- **Long gamma:** CVD weight ×0.7, and F1 inverts at extremes (stretched, not strong).
- **Short gamma:** CVD weight ×1.3, no fade.
- **Inside a wall zone** (15 pts): the whole score ×0.6. Scales the answer, never flips its sign.
- **No feed, a stale feed (>20 min), or a mixed reading:** no transforms at all.

Finally the hysteresis: enter at ±30, leave at ±10, and no state change for three committed bars.
Long can only become short by passing through neutral. That is the part that earns the screen
space.

## The panel

Laid out like AlphaXtrade's **Bias Lite** — a row per factor, its weight, the direction it points,
a bar for how hard — with one bias value underneath. Top right by default, every colour an input.

```
OCEAN'S CURRENT                                    (+38)
─────────────────────────────────────────────────────────
VWAP                  33   [   Long   ]      ▐███▌
Session Delta         33   [  Short   ]   ▐██▌
Value Area Shift       -   ┆   n/a    ┆       ┆
Overnight Inventory   17   [   Long   ]      ▐█▌
Key Levels            17   [ Neutral  ]       ┆
One-Timeframing        -   ┆   n/a    ┆       ┆
─────────────────────────────────────────────────────────
ACTUAL BIAS VALUE                              +42 %
STATE   LONG    CONF 71
─────────────────────────────────────────────────────────
WHAT CHANGES IT ...                (see below)
─────────────────────────────────────────────────────────
NEXT / NOW / TRACK / TODAY / FEED
```

Two things in it are deliberately **not** Bias Lite, and both matter:

**Absent is a fourth state.** Bias Lite has three — Long, Short, Neutral — because a person fills
it in and a person always has an opinion. This engine does not. A factor with nothing to read
abstains, and it draws a hollow chip and **no bar at all**. A zero-length bar on the centre line
would be indistinguishable from a factor that genuinely read neutral, and those are different
claims. The gap in the column is the point.

**The weight column is the effective weight, not the setting.** It is what each factor is actually
worth on this bar — after the gamma regime has transformed it and the absent factors have dropped
out — as a share of the vote, so the column adds to 100 and describes the blend that produced the
number underneath it. Bias Lite shows a constant because nothing there moves it; here the regime
moves it, and watching the Session Delta share fall when gamma flips positive is the regime doing
its job in the open.

The bracketed number by the title is the forming bar's provisional read. It is never blended into
the committed value.

### What changes it

Under the bias, the panel says exactly what would change the state, and draws the levels on the
chart (dashed = the next close does it, dotted = price has to settle there):

```
WHAT CHANGES IT (on the next close)
LONG     close above 29168.00 (+57.3)
SHORT    close below 29094.25 (-16.5)
SHORT    one bar of -1,850 delta
SHORT    ONH 29210.00 swept+reclaimed
-        PDL 29042.50 swept+reclaimed: no change
GAMMA    flip 29318.50 (+207.8) below: moves run
NEXT     Core CPI m/m +3  Fri 07:30 CT (10h 25m)
TRACK    16 calls  63% right  avg +19.5  net +312.0  (too few)
```

These are not estimates. Each one is found by committing a hypothetical bar through the engine's
real decision code on a throwaway copy, and the test suite checks that a real bar closing at each
printed level does exactly what the panel says — and that one tick short, it does not.

What they hold fixed: price levels assume that bar's delta is flat; delta triggers assume price
stays put. In a real sell-off both move together, so it reaches SHORT sooner than the printed price.
"earliest in N bars" means the state is still serving its dwell; the levels hold, the timing waits.

**NEXT** is the next high-impact USD release (ForexFactory weekly feed — it covers this week only,
so Friday afternoon it will say so). It warns 30 minutes out and alerts 15 minutes out: every level
on the panel was computed from a market that has not seen the print.

**TRACK** grades every LONG/SHORT on the chart from the close it entered to the close it left.
Under 30 calls it says "(too few)", and means it.

Set **Badge → Layout** to `Compact badge` for the original eight-cell strip, which is smaller and
says nothing per factor.

## Absent is not zero

The rule the whole design is arranged around: **a factor that cannot be computed abstains and
drops out of the weight normalisation.** It never votes zero.

Zero is a real reading — "price is exactly on VWAP". Burying "I don't know" inside it drags every
blended score toward neutral for reasons nothing on the chart could show. So:

- No session VWAP yet, or no spread yet → F1 abstains.
- No yardstick of past thrusts → F2 abstains. There is no default scale.
- No overnight on the chart, or an overnight with no range → F4 abstains.
- No carried-over references → F5 abstains. Two known references score the same ±100 as four; the
  missing pair does not vote bearish.
- Every factor absent → **no score**, and the state holds where it was. A bar nothing could score
  is not evidence for neutral.

The same rule reaches the log: an absent factor is written **blank**, never `0`, so the calibration
regression never sees a made-up neutral vote.

A cash session whose open was off the left edge of the chart never becomes "prior day", and an
overnight whose reopen was off-screen never becomes the overnight reference. Half a session's high
is not a day's high.

## One clock

Houston, everywhere. Session windows are entered in Central, the math runs in Central, the labels
print Central. `TimeContext` (copied from `oceans-market-view` — **keep them in sync**) works out
whether bar stamps are UTC or already local from the wall clock or the daily halt, and if it cannot
tell it says so on the chart rather than guessing. A wrong offset does not look wrong: it draws a
clean, plausible, silently misplaced level.

**The daily halt is 16:00 Central**, not 15:00. 15:00 is the cash close, when trading carries
straight on. Get this wrong and the clock never resolves and the badge shows an error instead of a
state.

The trading day rolls at **17:00**, not midnight: that is when today's cash session becomes "prior
day", which is when a trader starts reading it that way.

## The GEX feed

Tide Engine writes one wide row, overwritten atomically (`.tmp` then `os.replace()`):

```csv
ts_utc,spot_nq,regime,net_gex,gamma_flip,call_wall,put_wall,hvl
2026-09-09T13:45:12Z,24815.25,1,1.83e9,24700,24900,24550,24750
```

Default path: `%APPDATA%\ATAS\Ocean\obe_gex.csv`. Levels are NQ-native points and apply to MNQ
unchanged. The file is polled on **new bars only**, never per tick, and only when its timestamp or
length has actually changed.

A blank or zero level is **absent**, not zero — a call wall at 0 sits 24,800 points below price and
would read as "never near a wall" forever. A malformed row yields no regime and no transforms; it
never yields a partial reading, because the transforms are multiplicative.

There is **no last-known-value cache**. A regime remembered from this morning is exactly the input
that would flip the weights the wrong way this afternoon.

**Caveat worth knowing:** the regime is a live reading with no history behind it, so a chart reload
re-scores old bars under *today's* regime. States are reproducible for a given snapshot, not across
a regime flip. The regime in force is written into every log row so the fit can see which one was
used.

## Logging and the honest benchmark

One row per committed bar to `%APPDATA%\ATAS\OceansCurrent\logs\obe_YYYYMMDD.csv`. History is
silent — only bars at the live edge alert, and the log is the point of running this
decision-passive.

The protocol this exists to serve:

1. **Sessions 1–20:** run on hand weights, observe only. No tuning by feel.
2. **Fit:** join to the Crabel session DB by date; regress the 10:00 and 10:30 subscores against
   forward RTH-close direction; import fitted weights as defaults.
3. **Kill criterion:** the 10:30 state must beat the naive rule *"long if price > session VWAP at
   10:30"* by ≥3 pp over ≥40 sessions. **If it cannot, the composite is dead weight** — strip it
   to VWAP + GEX regime and stop.
4. **Behavioural check:** median committed flips/day ≤ 3. More means the hysteresis is too loose.

Point 3 is not decoration. F1, F3 and F5 all encode "where is price relative to structure", and the
main reason composite bias tools disappoint is that they turn out to be VWAP-side filters wearing a
costume.

## Build

```
dotnet build OceansCurrent.csproj -c Release   # build
cd _test && dotnet run -c Release              # math harness
./deploy.ps1                                   # test, build, smoke, copy DLL
```

`deploy.ps1` refuses to deploy if the tests fail. **Restart ATAS after deploying** — it reads the
indicator folder only at startup.

## Layout

`OceansCurrentIndicator.cs` settings + render + the ATAS boundary · `BiasEngine.cs` the per-bar
model · `StateMachine.cs` hysteresis and dwell · `Factors.cs` F1/F2/F4/F5 and the blend ·
`SessionState.cs` sessions, accumulators, carried-over references · `GexFeed.cs` the regime row ·
`BadgeModel.cs` every word on the badge as data · `SessionLogger.cs` the CSV row ·
`TimeContext.cs` the bar clock · `_test/` · `_smoke/`.
