# Ocean DOM

A price ladder for the ATAS price panel.

```
dotnet build OceansDom.csproj -c Release
cd _test && dotnet run -c Release
./deploy.ps1        # test, build, smoke, copy to %APPDATA%\ATAS\Indicators
```

Restart ATAS after deploying. It appears as **Ocean → Oceans DOM**.

## What it shows

Five columns, left to right, in fixed positions:

```
   traded          resting bid   price   resting ask      level rail
 |=====     |            |====| 29612.75 |==       |    PDH  TEST
 |==        |        |========| 29612.50 |=        |
 |==============|  |=========| 29612.25 |===      |
```

- **traded** — volume that actually printed at that price over the last N bars, stacked: the part
  that hit the bid, then the part that hit the ask. Older bars weigh less.
- **resting bid / resting ask** — live book size, drawn as a bar whose *length* is the size, both
  sides baselined against the price column so the two meet in the middle and can be compared
  without the eye travelling.
- **price** — the row price. The row holding last trade is outlined.
- **level rail** — the levels you typed in, with their state.

## The one idea

**Resting size and traded size land on the same row.** That is the whole point.

Size that sits there while trade goes through it and price does not move is absorption. Size that
vanishes before trade reaches it was never real. Both readings need the same two numbers at the
same price, and every platform splits them across two panels. Here they are adjacent.

The ladder does not tell you which one it is. That read is yours — see `oceans-profile` and
`oceans-effort` for the derived version.

## Levels and the five states

Type levels into the **Levels** setting, comma or newline separated:

```
PDH=29650.25, PDL 29500, ORB-H=29612.5
```

`NAME=PRICE` or `NAME PRICE`; a bare number names itself. Anything unreadable is **printed on the
chart**, never silently skipped.

Each level is always in exactly one of five states, and every one is a measurement:

| state | what it means |
|---|---|
| **Quiet** | price is nowhere near it |
| **Approach** | price is within the approach distance |
| **Test** | price is inside the band right now |
| **Reject** | price entered the band and left on the side it came from |
| **Break** | price went through the band and out the far side |

Break and Reject sit the *same distance* from the level. The only thing separating them is which
side price originally approached from — which is why the watch remembers that, and why it has to
survive price chopping back and forth across the level while still inside the band.

## Two rules the display enforces on itself

**One loud thing, and only a break gets it.** `Salience.For` will not hand the loud colour to
anything but a `Break`. A ladder that shouts when your level *holds* is a ladder that talks you
into staying in a trade; the one moment worth a shout is the one that says you are wrong.

**Quiet looks quiet.** With no level in play the entire panel fades to `QuietDim`. Most of the
session is no-trade, and a screen that looks equally busy when there is nothing there is a screen
that manufactures trades.

Both of these are the reason the panel has fewer knobs than it could. Column positions never move
and never reflow — a display you read by muscle memory has to be in the same place every time,
which also rules out any "declutter when it gets busy" mode.

## What it deliberately does not do

- **Nothing is predicted.** No queue-decay model, no imbalance forecast, no arrows. Measurements
  only.
- **No order entry.** Execution stays in Chart Trader.
- **No absorption verdict, no value area, no ORB.** Those live in the other Ocean indicators.

## Settings worth knowing

| setting | why |
|---|---|
| **Refresh (ms)** | the chart only repaints when bars change, so this drives it. The book is *sampled* at this rate — size that appears and vanishes faster is not seen. |
| **Minimum row height** | ticks merge into one row until a row is this tall, so the ladder works at any zoom without touching anything. |
| **Window (bars)** + **Weight by age** | how far back the traded column reads, and how fast an old bar stops counting. `HalfLife` halves every half-window. |
| **Band / Approach / Break / Reject (ticks)** | the distances that turn a price into a state. |
| **Hold an event for (s)** | how long a break or rejection stays on screen after it happens, so you can look away and still see it. |
| **Write an event log** | appends every state change to `%APPDATA%\ATAS\ocean-dom-events.jsonl`. |

## The event log

One JSON line per state change:

```json
{"utc":"2026-08-23T14:31:07.412Z","instrument":"MNQU6","level":"PDH","levelPrice":29650.25,"price":29653.50,"from":"Test","to":"Break"}
```

It exists so what the ladder said can be checked later instead of remembered. Nothing reads it
yet — that is the intended next piece, and it is what would let each state carry its own hit rate
on the face of the ladder instead of asking you to trust it.

## Tests

`_test` drives the geometry, the tape decay, the state machine and the parser against naive
hand-written references, so a mistake shows up as a disagreement rather than being confirmed by
its own logic. `_smoke` constructs the indicator outside ATAS, because a constructor that throws
inside the platform is invisible — the indicator just never appears.

The suite passed first try, so it was mutation-tested: nine deliberate breaks, and the one that
survived (forgetting which side price approached from when it chops across a level) is now the
`ChoppingInTheBandDoesNotForgetWhichSideItCameFrom` case.
