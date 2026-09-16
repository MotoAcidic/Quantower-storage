# CLAUDE.md — Ocean's Read

Read `README.md` first: it explains what each concept actually computes and why the refusals are
there. `~/dev/CLAUDE.md` holds the rules shared with every other `oceans-*` project. This file is
only what is specific to this one.

## Shape

    ReadClock.cs        futures dates, session windows, VWAP anchor keys
    TimeContext.cs      UTC-or-local, resolved from the data (straight copy from oceans-developed)
    ProfileMath.cs      the dense ladder, value area, nodes, single prints, shape
    RhythmMath.cs       SwingTracker (rotations) + the measured cadence
    AuctionMath.cs      balance/imbalance, value relationships, day type, the POC trail
    AcceptanceMath.cs   the acceptance test, plus shared number formatting
    WaveMath.cs         sequence character and the rule-checked count
    VwapMath.cs         anchored VWAP tracks and the top-down stack
    OrderflowMath.cs    delta, divergence, absorption, open interest
    ReadModel.cs        assembles all of it into the box, the gates and the stance
    OceansReadIndicator.cs   settings, the incremental fold, the render layers

Everything except the indicator is free of ATAS types **on purpose** — `_test` compiles the other
nine directly. Do not reach for an ATAS type in a math file; put it in the indicator and pass the
value in.

## Rules specific to this project

**Only closed bars are folded.** `OnCalculate(bar)` folds up to `bar - 1`. The forming bar changes
under us, and a session profile that includes a bar still being written moves its own point of
control every tick. Live price and bar delta come from the last candle at render time; everything
else is as of the last close. Acceptance is judged on closes by design.

**Acceptance references must STAND STILL.** `References()` returns prior-session value and range,
the overnight extremes and the initial balance — never the developing value area. A test opened on
a level that moves every bar chases price and never resolves. If you add a reference, ask first
whether it is still at the same price in an hour.

**A live test keeps the price it was opened at.** Re-reading the level each bar is the same bug in
a different place.

**The rhythm scales everything downstream.** Acceptance thresholds, and therefore the acceptance
box on the chart, are `median rotation x multiplier`. Changing how rotations are detected
(`SwingAtr`, `AtrBars`) changes what counts as acceptance. That is intended, and it is why the box
prints the rotation count the median rests on.

**`Stamp()` must include every setting that changes what a measurement IS.** It forces a full
recalculation. Settings that only change how something is drawn must stay out of it, or dragging
an opacity slider rebuilds the whole chart.

**The halt is 16, via `ReadClock.HaltHour`.** Do not pass a literal. `oceans-profile` passes 15,
which is wrong; do not copy that line in.

## Traps hit here

**`RenderFont(name, size, bool)` does not exist** — the third argument is a `FontStyle`.

**`ChartObject.Visible` is a base member.** A private helper called `Visible(...)` produces CS0108.
It is `VwapDrawable` now. Same family as the `Labels` and `ValueAreaPercent` collisions in the
other projects: build clean and read every warning before deploying.

**`ReadModel.Condition(...)` collided with the `Condition` enum** and made every use of the enum a
compile error inside that class. The method is `ConditionSection`.

**The mutation harness lied about three survivors.** The files are CRLF and the patterns are LF, so
multi-line mutations silently failed to apply, and `grep -F` with a multi-line pattern matches
line-by-line so the presence check did not notice. Matching now lives in `_test/mutate.pl`, which
joins the pattern with `\r?\n` and exits 2 when it genuinely cannot find it. If you add a
multi-line mutation, confirm it reports `killed` or `SKIP` — never assume `SURVIVED` means the
code is untested.

**A mutation that will not compile is not a kill.** Four early "kills" were C# that never built,
caused by writing `\&\&` in a bash double-quoted string (the backslashes survive; `&` needs no
escaping there). The harness now reports `BROKEN` for anything with `error CS` in the log and
fails the run on it.

**A test that dereferences a null reports a crash, not a failure**, which the harness cannot tell
from a build error. The acceptance tests hold `Last()` in a local and null-check it.

## Where the render can fail

Every layer in `OnRender` is wrapped in `Layer(name, ...)` and its failure is printed **in the
box**, which is drawn last for exactly that reason. A layer that throws takes down every layer
after it, silently, and there is no debugger on the render thread. Keep it that way, and keep the
box last.

## Not done yet

- No `_reflect` here; use the one in `oceans-market-view`, it scans the same DLLs.
- The wave count is drawn only when valid. There is deliberately no "most likely count" mode.
- Open interest depends on the feed publishing it per bar. If `candle.OI` is zero throughout, the
  box reports open interest as not published rather than guessing — check that before concluding
  the OI read is broken.
- Not a git repo, like the rest of `~/dev`.
