# CLAUDE.md

Ocean Developed Profile — ATAS indicator drawing the volume profile of **completed** periods
(previous day/week/month plus two rolling day windows) and carrying their point of control,
value area and high-volume shelves across the chart. Started 2026-09-13, ported from the idea
behind claysul's *Developed Volume Profile + HVN* on TradingView (that script is closed source;
nothing was copied, only the concept). Appears as **Ocean → Oceans Developed Profile**.
See README.md. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`OceansDevelopedIndicator.cs` render + settings · `DevelopedMath.cs` pure math (ladder, merge,
value area, shelves) · `DevelopedClock.cs` trade dates and period keys · `TimeContext.cs`
(copied from `oceans-profile` — keep in sync) · `_test/` math harness · `_test/mutate.sh`
mutation check · `_smoke/`.

## The design decision worth not undoing

**The day is the unit of account.** Bars fold to a futures trade date; each completed date gets
one profile, cached forever because a finished day cannot change; week, month and both rolling
windows are *sums* of those day profiles. Volume at a price is additive over time so this is
exact, not an approximation — the harness pins it against a direct build from the same trades.
Rebuilding each composite period from bars instead would rescan a month of candles four times
over on every settings change.

Note the distinction from Ocean Profile's rule 1: merging **time** is exact, merging **price**
rows is display-only and must never feed analysis. Both hold here.

## Two refusals, both deliberate

**A period the loaded history does not cover is skipped, not drawn short.** Checked against the
period's own 17:00 boundary, not against which days happen to be loaded — a month missing its
first fortnight still has days in it and still produces a confident, wrong point of control.
`AllowPartial` turns this off and labels the result `PARTIAL`; it ships off.

**A period is not cut until `TimeContext` settles the bar clock.** Unlike Ocean Profile, there
is no safe subset here: every kind is day-anchored, so all of them shift under a wrong offset.
The indicator prints why it could not resolve rather than drawing.

## Things that cost thinking

**`haltHour` is 16, not 15.** The MNQ maintenance halt is 16:00–17:00 Central; 15:00 is the cash
close and trading carries straight through it, so a resolver told to look for an empty hour at
15 finds none and never resolves. `oceans-profile` passes 15 and is wrong about this — it gets
away with it because the wall-clock check usually fires first and because its short periods do
not need the zone at all. Do not copy that line back in.

**`ValueAreaSize`, not `ValueAreaPercent`.** The base class already has `ValueAreaPercent` and
hiding it is a silent CS0108. Build clean and read every warning.

**The week's `EndInstant` is the NEXT week's reopen, not Friday's close.** Both contain the same
trade dates (the weekend holds none), but running to the reopen is what makes consecutive
periods tile with no gap and no overlap. The first version of the harness asserted Friday 17:00
and was wrong about which property mattered.

**Shelves are bands, and the bridge is load-bearing.** Without `HvnGapTicks` one thin tick
inside a shelf reports two shelves where the market built one. Trim by weight, then re-sort into
price order so the draw order does not shuffle frame to frame.

**Every render layer is caught separately.** A layer that throws otherwise takes down every
layer after it in silence, including the status line that would have said so, and there is no
debugger on the render thread.

## Testing

`_test` covers the math and the clock, neither of which carries an ATAS type. Run
`_test/mutate.sh` after any change to `DevelopedMath.cs` or `DevelopedClock.cs` — it is not
wired into `deploy.ps1` because it rewrites source files.

**Two traps in `mutate.sh` itself, both of which produce convincing false results:**

- **Never capture `dotnet run` through `$( )`.** Command substitution waits for every process
  holding the pipe, and dotnet leaves a persistent `VBCSCompiler` build server behind that
  inherits it, so the substitution never returns and the run hangs on an already-finished test.
  Redirect to a file instead. While one run was hung, two more were started and raced each
  other over the same `.bak` — one restored another's mutation mid-test and reported a
  SURVIVOR that was really just a reverted edit. Hence the lock and the `trap ... EXIT`.
- **`touch` after restoring a `.bak`.** `mv` gives the file the backup's mtime, which predates
  the binary built from the mutated source, so MSBuild calls it up to date and the *next* run
  silently tests the previous mutation. This showed up as the suite failing immediately after
  a clean 20/20 run — which reads exactly like a real regression.

**Two mutations survived the first honest pass, and both were weak test DATA, not missing tests:**
the shelf-trimming test put the lightest shelf last in price order, so trimming by weight and
trimming the tail gave the same answer; and the value-area tests checked containment and
contiguity but never which way the band grows, so expanding toward the *thinner* side passed
everything. Fixed by moving the light shelf into the middle and adding a hand-worked asymmetric
value area. When a suite passes first try, mutate before believing it.
