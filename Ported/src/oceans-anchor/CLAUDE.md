# CLAUDE.md — Ocean's Anchor

Read `README.md` first for what this does and why. `~/dev/CLAUDE.md` has the rules shared by
every `oceans-*` project; they all apply here. This file is only what is specific to Anchor.

## The line that must not move

Everything in `Anchor*.cs` is free of ATAS types. Everything in `OceansAnchor.*.cs` is the
platform adapter. The `_test` harness compiles the first set and nothing else — 198 checks on
synthetic bars.

Do not "simplify" by pulling an `IndicatorCandle` into `AnchorProfile.cs` or `AnchorState.cs`.
The outlier filter, the value area, the two absorption scorers and the state machine are where
the bugs actually live, and they are only checkable off-platform. Four real bugs were caught by
this harness during the first build, three of them inverted comparisons that would have looked
plausible on a chart.

## Mutate before believing the suite

Per `~/dev/CLAUDE.md`, and it earned its place here: the first pass had four tests that survived
deliberate sabotage (a loosened traversal test, an arbitrary POC tie-break, a value-area test the
range clamp made vacuous, and a cluster test whose dull-bar case still scored under the
threshold). All four have been strengthened. Twenty mutants are now caught.

When you change any of this logic, sabotage it and confirm the harness turns red before you
believe a green run.

## Things that will bite

**`HaltHour` defaults to 16, not 15.** 15:00 Central is the cash close and trading continues; the
empty hour is 16:00–17:00. With 15 the bar clock never settles and the indicator draws *nothing*.
The smoke test asserts this. Do not "fix" it to match the cash session.

**Two buffers with similar names, different jobs.** `ZoneBufferTicks` is how close price must get
before a zone counts as tested; `BreakBufferTicks` is how far past the cluster price must go
before the zone counts as lost. Arming with the break buffer made zones arm on bars nowhere near
them — the harness catches it now.

**Order inside `FoldBar` is load-bearing.** `AdvanceZones` runs before either absorption path. The
bar that arms a zone is very often the same bar that does the absorbing, and running the cluster
path first meant the classic setup bar was always evaluated against a Dormant zone and thrown
away.

**Zone state does not survive the day.** `RollSession` drops the whole list before rebuilding,
because `CarryState` matches zones by key across a rebuild and a composite shelf reappearing at
the same POC would otherwise come back still Broken.

**The history gate.** The fold deliberately pauses at the session start until the tape response
arrives. If you remove that wait, historical trades get applied to a state machine that has
already advanced past them, and the CSV — which is the entire calibration — fills with rows that
never happened. Every path out of `RequestHistory` must either leave a request outstanding or set
`_historyDone`, or the fold stalls forever on a blank chart.

**Threading.** Tape callbacks snapshot and queue; a background worker applies only the size floor;
everything that touches a `Zone` happens on the chart thread in `OnCalculate`. That is what lets
the state machine and the renderer run without locks between them. Do not move zone logic onto
the worker to "save a hop".

**`OnRender` computes nothing.** It reads `ZoneView` structs frozen at bar close, and each layer
catches separately and prints its own failure on the chart. There is no debugger on the render
thread.

## Calibration state

Shipped with every threshold as a guess. `SizeFloor` 100 is an NQ round number, not a
measurement. Until ten sessions of `anchor_log.csv` exist, treat any signal quality claim as
unverified — including anything this file or the README implies. The protocol and the kill
criteria are in the README.
