# Ocean Pivot Decoder

Finds the price bands where **different kinds of evidence agree**, and states where the long side
and the short side are for an overnight hold.

It also decodes a third party's quoted levels back to the formula and session window that produced
them, which is what it was originally built for.

It describes where evidence sits. It does not forecast that a band will hold, and nothing in it
measures that.

## The one idea

Every floor pivot, Camarilla level and mid is a rearrangement of the same three numbers. Thirty of
them landing together is arithmetic, not agreement. So nothing renders as a line: levels cluster
into **zones**, and a zone scores as the weighted sum of what it is made of.

| Family | Weight | Why |
|---|---|---|
| floor / Camarilla / mid | 1.0 | arithmetic on H/L/C |
| prior H/L/C | 1.0 | where price actually turned |
| session POC (Asia/London/NY) | 1.5 | where contracts actually traded |
| session VWAP | 0.75 | reference, not a decision |
| **naked POC** | **2.0** | a magnet price has never been back through |
| **unfinished extreme** | **2.0** | an auction cut off rather than exhausted |

Zones below price render **green** (candidate longs), above **red** (candidate shorts). Opacity
scales with score, so the strongest band is the one your eye lands on without reading anything.
Only bands within 400 pts of price draw, at most three a side. Everything else stays in memory for
the log.

## The absorption engine

A zone score says a level exists. It cannot say the level was ever *defended* — price reaches every
level eventually. Each zone runs a state machine: `UNTESTED → TESTING → VALIDATED | FAILED`.

VALIDATED needs four things together, each killing a different way of being fooled by something
that merely looks like a bounce:

- **size** — cumulative in-zone delta clears an *adaptive* bar (1.5× the 20-bar average absolute
  delta). Never a hardcoded contract count, which is wrong the moment the session changes character
  and wrong again on another instrument.
- **direction** — the delta points *into* the zone. Buying into support is not absorption.
- **containment** — price did not push more than 8 ticks past the far edge.
- **rejection** — price closed back out by 6+ ticks.

FAILED is acceptance: two consecutive closes more than 12 ticks beyond the far edge. The zone dims
to grey and drops out of the bias panel — it is no longer a level.

`CL` next to a zone means a single price inside it printed volume in the 90th percentile of the
last 20 bars. That is the footprint's big print without rendering a footprint.

## The readout

Three modes (`Readout`): **Full**, **One line**, **Off**.

One line, top right — the sentence you would say out loud:

```
SHORT  sell 30458.25  stop 30460.25  target 30105.00  17.6R  [x5 HELD]  6/10 held (60%), top-ticked 30%
```

Full, top left:

```
BIAS: SHORT -- overnight is long above yesterday's value; those buyers are exposed if the open sells
       net-LONG overnight (px > pRTH-C +38.00, > pVAH)
TOP TICK sell 30458.25   stop 30460.25 (2.00 pts)   target 30105.00 (353.25 pts, 176.6R)
         band 30450.00–30458.25 [x5] HELD -- size absorbed and price rejected   built from cR3, cR4, nPOC
         history: 6/10 held (60%), top-ticked 30%   (x4+ with magnet, 30 sessions on this chart)
BOT TICK buy 29612.00 ...
MAGNETS: nPOC 29847 (naked, 3d)  |  POOR HIGH 30269 (unrepaired)
RESISTANCE ABOVE
  S1  30450.00–30458.25  [x5]   183 up   HELD -- size absorbed and price rejected + magnet
SUPPORT BELOW
  L1  29612.00–29622.00  [x8]   405 dn   not tested yet + magnet
```

## Top tick / bottom tick

Entry is the **far edge** of the band — the top of resistance, the bottom of support — because
that is the price the band is worth trading at; entering mid-band gives up half the edge and moves
the stop no closer. Stop is `PlanStopTicks` beyond, target is the first opposing band, and every
price is snapped to the instrument tick.

## Base rates, not probabilities

The zone score is an *opinion* about which levels ought to matter. It has never been checked
against anything. So the decoder replays the same construction over the last `StatsLookbackDays`
(30) sessions on the chart, walks each day's bars, and counts what price actually did when it got
to bands like this one.

Bucketed on two axes only — strong/weak and magnet/no-magnet — because every extra split halves
the sample. **Below `MinSampleSize` (8) touches no rate is quoted at all**; the sample size is
reported instead. A 3-for-4 hit rate reads as 75% and means nothing, and showing it would be worse
than showing nothing because it invites size behind noise.

This is a base rate over a small, recent, single-instrument sample. It is not a forecast.

## Timeframes

A bar carries one timestamp — its open — and a window is now matched against the bar's whole
**span**, not that stamp. This is why an hourly chart used to disagree with a 15-minute one: RTH
opening at 08:30 discarded the 1h bar stamped 08:00 whole, losing 08:30–09:00, the cash open and
very often the session high or low.

On a chart whose bars do not divide the session boundary, the first and last bars straddle it, so
their high or low may have printed on the wrong side. That is only a problem when a straddling bar
actually **set** an extreme — usually one lying wholly inside the window did, and then nothing is in
doubt at all.

So the caution is not permanent. Each window tracks the extremes from bars lying wholly inside it,
and the readout speaks up only when a straddling bar set the published high or low — naming the
level and bounding it:

```
prior RTH high 29211.75 is between 29205.50 and 29211.75 (6.25 pts)
```

A standing banner would be learned and skipped; this one appears on the sessions where a number is
actually uncertain, and says how uncertain.

| Timeframe | Session levels | Notes |
|---|---|---|
| 1m – 1h | exact | boundaries land on bar edges |
| 4h | approximate | end bars straddle the 08:30 open; clock settles off the weekend gap |
| daily / weekly | full-day only | RTH/Asia/London/NY are shorter than one bar, so they are reported unavailable rather than served as full-day numbers under a session label |

The bar clock is measured from the stamps (median gap — the mean would be wrecked by weekends, and
range/tick/volume bars have no timeframe string at all). Coarse charts settle it off the **weekend
gap**: the last bar before every multi-day gap *ends* at the Friday close. It is the bar's end that
is tested, not its stamp — the open lands at a different hour on every bar size, and a window wide
enough to cover them all starts matching the wrong reading too.

Daily and above cannot settle the clock at all, and that is allowed: at that size the reading
cannot change which *bars* make up the prior session, only the date printed beside it.

## Unfinished extremes

A completed auction thins out as it runs out of buyers. Two readings flag one that did not:

- **no taper** — the extreme tick still holds 35%+ of the volume three ticks back
- **ledge** — two or more bars printed the exact same extreme

Retired once price trades 4 ticks through. Marked `¬`.

## Not included, on purpose

- **Auto-trendlines.** Deliberately excluded. There is no stable definition of the right one — pick
  different swing points and you get a different line, so it would add a level that always agrees
  with whatever you already believed. Session VWAPs fill that slot instead: same "dynamic
  reference" job, reproducible from the same bars by anyone.
- **A footprint / delta engine.** `oceans-profile`, `oceans-effort` and `oceans-delta` already do
  this properly and have the display work behind them. What is here is the narrow slice a level
  tool needs: volume and delta *inside a band*, and the bid x ask *at a session extreme*.

## The R3/S3 trap

The two conventions usually quoted as different are the same formula written two ways:

| | R3 | S3 |
|---|---|---|
| "Standard" | `H + 2(PP - L)` | `L - 2(H - PP)` |
| "Narrow" | `R1 + (H - L)` | `S1 - (H - L)` |

Both reduce to `2PP + H - 2L` and `2PP - 2H + L`. They agree to the cent, always. Choosing **Both**
therefore draws **one** line, labelled `R3sn` / `S3sn`.

The convention that genuinely differs is the PP-anchored pair, behind its own toggle:

    R3 = PP + 2(H - L)      S3 = PP - 2(H - L)

On the validation case those sit 121 points from the classic pair. Confusing the two is what makes
a decoder report NO MATCH against a caller who is in fact using floor pivots.

## Validation case

    H = 29211.75   L = 29017.25   C = 29186.25

    floor PP     29138.42
    classic S3   28870.58
    Camarilla R4 29293.23

Pinned in `PivotMath.SelfTest`, run in the constructor, and reproduced end-to-end from synthetic
bars in `_test`. A failure paints a red banner on the chart rather than drawing levels.

Two traps it guards:

- **S3 is 28870.58, not .59.** 28870.59 comes from rounding PP to 2 dp *first*. That is a one-cent
  artefact of early rounding, not a different formula, and not something to "fix" the math for.
- **29293.225 rounds to 29293.23 only half-up.** Banker's rounding — the .NET default — gives
  29293.22, so every display path goes through `PivotMath.Round2`.

## Caller levels

Comma separated, full prices (`28872`) or last-three shorthand (`872`). Shorthand resolves against
the thousands of the last close. The three candidates sit 1000 apart, so the nearest is always
within 500: the search-range setting is a tightening knob for genuinely ambiguous cases, not a
routine rejection path. Nothing is ever placed on a guess — an unresolvable token is reported as
unresolved, with the reason.

## Reaction tracking

For each EXACT-matched level, after the first touch: whether price moved 30+ pts away within two
hours, and whether it reached +50 in the called direction before -30 against. Direction is inferred
from the session open (below = long, above = short) unless overridden with `CallerDirections`
(`872:L,293:S`). A bar that reaches both target and stop is recorded as AMBIGUOUS, never guessed —
guessing there would inflate the win rate of the very protocol this exists to measure.

Written to `Documents\ATAS\PivotDecoder_log.csv`, one row per caller level per session, rewritten
in place as the session plays out and closed out when it rolls. Every IO path is wrapped: a locked
or unwritable file costs you the log line, not the levels.

## Build

    dotnet build OceansPivotDecoder.csproj -c Release   # also copies to %APPDATA%\ATAS\Indicators
    cd _test && dotnet run -c Release                   # math harness
    cd _smoke && dotnet run -c Release                  # constructs the indicator outside ATAS
    ./deploy.ps1                                        # test, build, smoke, deploy

ATAS reads its indicator folder **only at startup** — restart the platform after every deploy.
`-p:SkipAtasDeploy=true` builds without copying.

## Notes

- Targets `net10.0-windows`. The installed ATAS assemblies are built for net10.0 and a net8.0
  project cannot reference them.
- Deploys to `%APPDATA%\ATAS\Indicators`. ATAS does not read `Documents\ATAS\Indicators`.
- Every time in the settings and on the chart is **Houston time**. There is no second zone.
- No exchange session metadata is read. Sessions are derived from bar timestamps against the
  configurable windows, because SDK versions disagree about that metadata and a chart loaded from
  another feed may carry none.
- The bar clock (UTC vs already-local) is settled from evidence, never guessed: the platform's
  market clock first, then the wall clock, then the daily halt. If it cannot be settled the
  indicator says what the newest bar is stamped and what that would mean, instead of drawing a
  clean, plausible, silently misplaced set of levels.
- **The maintenance halt is 16:00–17:00 Houston, not 15:00.** 15:00 is the cash close, when
  trading carries straight on. That setting only feeds clock resolution, but getting it wrong
  stops the clock resolving at all and nothing is drawn. It has now shipped wrong three times
  across this repo — check it first when an indicator draws nothing.
- Line labels show the price snapped to the instrument tick, so they can be typed straight into a
  limit order. Lines and the match table keep the exact level, which is what formula decoding
  needs.
