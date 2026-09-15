# ORB-IX

A Quantower indicator: opening ranges, market structure, volume profiles, VWAP, and a set of
order-flow displays built from the footprint of each bar.

It **draws**. It places no orders, reads no account, and sends nothing anywhere.

---

## Install

1. Close Quantower.
2. Copy `OrbIxIndicator.dll` to:

   ```
   C:\Quantower\Settings\Scripts\Indicators\ORB-IX\OrbIxIndicator.dll
   ```

   Create the `ORB-IX` folder if it is not there. The folder name is not important; that the
   DLL sits in its **own** folder is.
3. Start Quantower, open a chart, and add **ORB-IX** from the indicator list.

`OrbIxIndicator.deps.json` may sit beside the DLL. It is build metadata, not used at runtime —
Quantower loads scripts with `Assembly.Load(bytes)` — and you can delete it.

**Requires nothing else.** Configuration is compiled in; there is no file to edit and no folder
to create. Every setting is a chart input, reachable from the indicator's settings panel.

---

## What it draws

**Opening range and session structure** — the range box, its high and low, the midpoint, and
extension targets. Sessions that closed before the indicator loaded can be drawn too.

**Market structure** — a higher-high / lower-low read, with undecided bars marked separately
rather than forced into a direction.

**Volume profiles** — a fixed-range profile (FRVP) and an anchored profile (AVP), each with
point of control and value area.

**VWAP** — session and anchored, with standard-deviation bands.

**Order flow, from the footprint of each bar** — eleven displays, each switchable:

| | |
|---|---|
| cluster statistics | per-bar volume, delta and their extremes, as a table |
| cluster search | bars matching a volume or delta condition you set |
| stacked imbalance | runs of consecutive imbalanced diagonals |
| absorption | size resting into a price that does not move |
| unfinished auction | bars whose extreme traded on both sides |
| big trades | single prints above a size you set |
| live counter | the forming bar's buy/sell/delta, beside price |
| DOM levels | resting size from the order book |
| trend lines, fib fan, GEX walls | drawing aids |

**Delta** — a cumulative-delta read, flip levels where delta changes sign, and shelves.

---

## The volume floor, and why this build measures it

A stacked-imbalance display needs a **minimum volume** before a diagonal counts. Set it too low
and every bar marks; too high and nothing does.

That number is a property of the contract. It was originally swept on one instrument — MNQ —
over 37 sessions and roughly 74 million prints, giving 45 contracts on one-minute bars. **On a
different product that number is wrong by a large multiple, and nothing on the chart would tell
you.**

So this build **measures the floor from the tape in front of it** rather than shipping somebody
else's number. It reads the bars your chart has, finds the volume floor that produces about the
mark rate you asked for, and reports what it did. The status line says so:

```
stacked imbalance min volume 52, calibrated on this instrument:
6.8/session from 23 mark(s) over 1,313 bar(s), 99,371 diagonal(s)
```

Read it as: *the floor is 52 contracts; at that floor this instrument produced 6.8 marks per
session; that rate rests on 23 marks seen across 1,313 bars.* A small mark count is reported as
`THIN`, because a rate resting on four marks is not a settled number.

Before it has enough tape it says so, and uses a stand-in meanwhile:

```
min volume 45, STAND-IN while calibrating (not calibrated: 41 bar(s) of tape, 60 needed)
```

Sixty bars is the minimum. If you want more or fewer marks, change the target rate rather than
the floor — the floor is an output.

**How well does it work?** Calibrating each of those 37 sessions independently, the per-session
floor at one minute has a median of 42 against the 45 the full sweep chose, and at five minutes
37 against 40. One session's answer varies — the range across 37 was 32 to 65 — so treat the
number as an estimate that improves with tape, not a constant.

---

## What is measured, and what is not

This matters more than the feature list.

**Measured, and the displays rest on it:**

- The aggressor convention is **verified live on your own feed**, not assumed. There is no
  universal convention, and getting it backwards produces a chart that is coherent and exactly
  wrong. The status line reports which way your feed runs and how strongly:
  `aggressor convention: CONVENTIONAL, 100.00% agree over 931 judged`. Until 500 prints have
  been judged it says so instead of claiming.
- The volume floor, as above.

**Not measured — these are displays, and nothing here says they predict anything:**

Every drawing in this indicator is a description of what happened. None of it is a signal, and
the author's own testing of several of these ideas on one instrument did not find an edge. Use
them as a way of reading the tape, not as entries.

**Known limits, stated rather than discovered:**

- Charts whose bars are **not time bars** — tick, range, Renko, Kagi, Line Break,
  Points-and-Figures — do not currently produce footprint displays. The footprint engine is
  keyed to a bar period. Work to remove that is in progress; until it lands, use a time-bar
  chart for the order-flow tools.
- Volume profiles need per-price volume from your data connection. Where the platform does not
  serve it, the indicator falls back to tick history, and says which it used. Where neither is
  available it reports the gap rather than drawing an empty profile.
- DOM displays need a depth subscription. Without one the indicator says `book no depth
  received` rather than showing an empty ladder.

---

## Reading the status line

One line per chart update, in the Quantower log and — for problems only — on the chart. Healthy
charts are quiet on the chart itself; a line that appears there is worth reading.

```
status: flow: 8 of 11 on — cluster statistics, cluster search, stacked imbalance, …;
        stacked imbalance min volume 52, calibrated on this instrument: …
      · flow frame: stacked 23, absorption 0, auction 0, …, closed bars 1,334
      · aggressor convention: CONVENTIONAL, 100.00% agree over 931 judged
```

`flow:` is what is **switched on**. `flow frame:` is what was **found**. Those are different
questions, and keeping them apart is the difference between "the tool is off", "the tool is on
and the market is quiet", and "the tool is broken".

---

## Support

None. This is shared as-is, with no warranty and no undertaking to maintain it.
