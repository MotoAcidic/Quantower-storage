# CLAUDE.md

Ocean's Current — ATAS indicator that computes one **bias state** (LONG/SHORT/NEUTRAL) per session
from four factors plus the Tide Engine gamma regime, and holds it through hysteresis. Built from
`oceans-current-build-spec.md` (v1 scope: F1, F2, F4, F5, regime modifier, state machine, badge,
alerts, logging). Started 2026-09-09. Appears as **Ocean → Oceans Current MNQ**. See README.md for
behaviour. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`OceansCurrentIndicator.cs` settings/render/the ATAS boundary · `BiasEngine.cs` the per-bar model ·
`StateMachine.cs` hysteresis + dwell · `Factors.cs` the four factors, `Scoring` the blend ·
`SessionState.cs` sessions/accumulators/references · `GexFeed.cs` · `BadgeModel.cs` ·
`SessionLogger.cs` · `TimeContext.cs` (copied from `oceans-market-view` — **keep them in sync**) ·
`_test/` · `_smoke/` · `_calib/` (the §9 analysis, Python — run it with the `~/trading-floor/.venv`
interpreter, which has pandas).

No `_reflect/` here. Use `oceans-market-view/_reflect` or `oceans-crabel/_reflect`.

## The one rule everything else follows: absent is not zero

A factor that cannot be computed **abstains** and drops out of the weight normalisation. It never
votes zero. Zero is a real reading — price exactly on VWAP — and hiding "I don't know" inside it
drags every blended score toward neutral for reasons nothing on the chart could show.

This runs the whole length of the code and every layer of it is asserted:

- `Level` carries `Known`; `Subscore` carries `Available` and a reason.
- `Scoring.Blend` returns **false** when nothing may vote, and the state machine treats that as no
  evidence and holds — not as evidence for neutral.
- `SessionLogger.Row` writes an absent factor **blank**. A `0` there enters the calibration
  regression as a real neutral vote and biases every fitted weight.
- `GexSnapshot` treats a blank *or zero* wall as absent. A wall at 0 is 24,800 points below price
  and reads as "never near a wall" forever.
- There is **no default thrust yardstick, no default gap scale, no last-known-value regime cache.**

If a future factor needs a fallback constant to work, it does not work.

## Where the money-losing bug would be

Every one of these fails *open* if written carelessly, and each failure draws a clean, plausible,
wrong state:

- **Sigma.** With no spread yet, `z = (close − vwap) / 0` is a maximum reading on one tick of
  drift. `Sigma` is absent while the variance is zero and F1 abstains.
- **The thrust yardstick.** A zero mean divides to infinity; a *default* mean scales every thrust
  by a number nobody measured. `RingMean.Mean` is absent while it is zero, and the ring is fed
  **after** the reading it scaled, so no bar is normalised against itself.
- **Half a session.** A cash session whose open was off-screen is not a prior day, and its high is
  not PDH. Guarded by `_devRthOpenSeen` / `_devOnOpenSeen`.
- **A stale VWAP overnight.** Anchored on the cash open, the accumulators are **closed** at 15:00,
  not left standing — otherwise F1 scores 3 AM against a number six hours dead.

## Determinism is structural, not promised

`BiasEngine.Advance` is the only thing that mutates state, takes one bar, and **throws** on a bar
out of order. So a rebuild from bar zero runs the identical call sequence — there is no second code
path for history to drift down. `Provisional` writes nothing, not even a scratch array
(`_provisionalRefs` exists solely so that claim is literally true).

The indicator rebuilds from zero whenever `bar < NextBar − 1`, and catches up with a `while` loop
otherwise, so a reload or a jump both land on the same states.

**Caveat:** the gamma regime is live and has no history, so a rebuild re-scores old bars under
today's regime. Reproducible for a given snapshot, not across a regime flip. The regime used is in
every log row.

## Test lessons — these cost real rounds

**A 30-minute fixture cannot exercise F2.** A cash session is 13 bars long, the 20-bar thrust can
never look back far enough, and F2 sat out every engine test silently. A mutation to the thrust
path survived the suite because of it. The engine fixtures are **5-minute bars** now, and
`ARebuildReproducesTheStateSequence` asserts every shipping factor actually voted somewhere in the
run. Keep that guard.

**A monotonic price generator cannot catch lookahead.** Every bar is a fresh 20-bar extreme, so
peeking one bar forward finds the same answer as looking back, in the altered feed and the clean
one alike — two identical wrong answers, test green. Hence `Wave()`: the path has to turn.

**Comparing outputs cannot catch lookahead reliably at all.** It only catches a peek that happens
to change a number, and often none does. `NothingEverReadsABarThatHasNotClosed` wraps the bar
window and fails on the **read**, which caught the same mutation instantly. Prefer the structural
form; the output comparison stays as a complement.

**The dwell counts the entry bar.** Entering long sets `BarsInState = 1`, so a hard reversal costs
two more committed bars. That is the difference between three flips a day and four, and it is
asserted directly rather than left to the arithmetic.

## Nothing drew at all, twice over (fixed 2026-09-09)

Two independent defects, each of which alone produces a chart that looks exactly like the
indicator was never added: no badge, no line, **no exception and nothing in the ATAS log.**

**1. The render subscription.** It was `DrawingLayouts.Final | DrawingLayouts.LatestBar`, from the
spec. Every one of the nine working indicators in this suite passes `Final` **alone**, and
`SubscribeToDrawingEvents` just stores the value it is given -- so if the platform tests that
stored mask for equality rather than as flags, `OnRender` is never raised at all. Do not "tidy"
this back into a combination.

**2. `RenderFont` never throws on a font that does not exist.** Verified off-platform: it builds
happily from `"Definitely Not A Font 12345"`. So the `try/catch` that was supposed to fall back to
Consolas was dead code that could never fire, and the badge was being measured and drawn with
JetBrains Mono, which **is not installed on this machine** (only Consolas is). A badge measured at
zero is an invisible badge. The family is now CHECKED with `System.Drawing.FontFamily` -- that is
the type that does throw -- rather than caught.

The property had to be **renamed** `FontName` -> `BadgeFont` to change the default, per the
standing rule: ATAS restores a saved value whenever the name still matches, so the new default
would never have reached a chart that already had the indicator on it.

**Both are now asserted in `_smoke`, which `deploy.ps1` gates on** -- the layout is read back off
the base class by field type and must equal `Final`, and the default font must be installed. Both
checks were mutation-verified. This class of bug cannot be seen at runtime, so it has to be caught
before the DLL ships.

Three supporting changes so a blank chart can never again be silent:

- The empty `catch { }` around the badge is now `DrawDistress` -- a fixed-size red box built from
  its own Consolas font, using no setting and measuring nothing, printing the exception type.
- `DrawBadge` floors a degenerate measurement (`rowHeight < 6`, `width < 40`) instead of drawing a
  zero-size rectangle.
- `ConfigKey()` -- a `string.Join` over three dozen boxed settings -- was being rebuilt on **every
  tick**, on the path the spec required to be allocation-free. It is checked on bar boundaries now
  (`_configCheckedAt`), so an edited setting lands one bar later.

## The panel is AlphaXtrade Bias Lite, with one column it does not have

`BiasPanel.cs` is the model, `DrawPanel` in the indicator places it. `Badge → Layout` switches back
to the old eight-cell strip; `PanelLayout` is a **new** property rather than a changed default on
an old one, per the standing rename rule.

Bias Lite is a manual spreadsheet: a person picks Long/Short/Neutral per factor and it weighs them.
This reproduces its layout and its wording ("ACTUAL BIAS VALUE"), and departs from it twice, both
deliberately:

- **`RowKind.Absent` is a fourth direction.** Bias Lite needs only three because a person always
  has an opinion. A factor here that cannot compute abstains, and it draws a hollow chip and **no
  bar** — a zero-length bar on the centre line would read identically to a genuine neutral. This is
  the same rule as everywhere else in the codebase, now visible on the chart.
  `ThePanelSaysAbstainedNotNeutral` pins it and was mutation-checked.
- **The weight column is the effective, regime-transformed, renormalised share**, not `F2Weight`.
  It adds to 100 and describes the blend that produced the number under it. Printing the raw
  setting would describe a blend that is not the one being run — and would hide the regime, whose
  whole job is moving those weights.

Factor names follow AlphaXtrade's vocabulary **only where ours means the same thing** — "Session
Delta", "Value Area Shift", "Key Levels". Do not rename a factor into one of theirs that it does
not actually compute; the look is the layout and the colours, not the labels.

## Gamma comes from TradeGEX, in-process (2026-09-10)

The Tide Engine CSV was never written, so for its whole life the regime was `0` and the panel said
`GEX -` — while TradeGEX sat on the same chart drawing the flip and walls. `Gamma regime → Read
gamma from` now defaults to **TradeGEX (on the chart)**; the CSV is still an option.

`TradeGexBridge.cs` reads TradeGEX **by reflection, no compile-time reference** — a missing or
reshaped TradeGEX becomes a Problem string on the panel, never an exception. Verified against the
decompiled `TradeGEX.dll` (deployed 2026-08-22):

- Every live `TradeGexLevels` registers in static `TgxAuth._consumers` (entry `.Owner`). TradeGEX
  mutates that list only under static `TgxAuth._hub`; the copy is taken under the same lock,
  bounded, and released before `TradeGexLevels._lock` is taken — never both at once.
- Fields: `_flipStrike`, `_cwStrike`, `_pwStrike` (`decimal?`), `_ratio`, `_status`,
  `_unsupported`. Only a TradeGEX on the **same instrument** is read.
- TradeGEX draws every level at **`strike × _ratio`**, and sets `_ratio = 1` whenever its feed
  omits `etfRatio` — its own silent fallback. In ETF mode that is a QQQ flip at ~709 on a 29,000
  chart. `GexSnapshot.FromLevels` refuses any converted level farther than
  `MaxLevelDistancePct` (10%) from price: a failed flip is a Problem and no regime. Mutation-checked.
- Regime = the side of the flip price is on. `_status` must start with "live".
- **Only bars near the live edge get a regime.** The levels are a snapshot of now; applying them
  to history on a reload would fabricate a regime in every old log row. History logs regime `0`.

If TradeGEX updates and a field name moves, the panel reads `TradeGEX changed` — re-run
`ilspycmd -t TradeGEX.TradeGexLevels` on the new DLL and fix the names.

## Visible, and awake overnight (2026-09-10)

Two reasons the panel "did nothing" on a 3440-wide monitor: it printed at font 10 (~7 px) and
overlapped the price axis, and VWAP + Session Delta read n/a every evening because the anchor shut
at 15:00. Fixes, both as **renamed/new properties** per the rename rule:

- `PanelFontSize` (default 15), separate from the compact badge's `FontSize`; every fixed panel
  width scales with it.
- `VwapAnchor` (was `Anchor`) defaults to the new `SessionAnchor.EachSession`: reset at the cash
  open AND the evening reopen. Cash hours read identically to `RthOpen` (asserted); the overnight
  gets its own VWAP/CVD instead of going dark. Mutation-checked.

## What changes it — and why it is exact (2026-09-10)

`Triggers.cs` answers "what would change its mind": the nearest next-close level each side, the
settled reversal level, the one-bar delta each way, a sweep+reclaim of the nearest PDH/PDL/ONH/ONL,
and the gamma flip. Drawn as dashed lines (next close) and dotted (settled) coloured by the state
they lead to. Computed once per closed bar at the live edge only, ~4–5 ms.

**The what-if IS the commit.** `BiasEngine.WhatIf` clones the engine (`CloneForWhatIf` on
SessionState, RingMean, StructureTracker, StateMachine — MemberwiseClone so no field can be
forgotten) and runs the real `Advance` on a one-bar window (`TriggerScan.OneBar`). The first
version re-scored with a copy of `Provisional`, and `TheEngineActsOnTheLevelItPrints` proved it
wrong: Provisional skips the new bar's effect on the VWAP slope, the CVD divergence flags and the
overnight H/L/C, so the printed levels were not the ones the engine acted on. **Never compute a
trigger with anything but Advance.** That test commits a real bar at every printed level (must
change state) and one tick short (must not), for price, delta and sweeps, with and without a live
gamma flip. Mutation-checked three ways.

The dwell is set aside for the levels and reported beside them ("earliest in N bars"); a close
*right here* that already flips it is reported as that, not as a level a tick away.

Also: the old "FLIP" line (nearest VWAP / ON-mid below price) was **not** the level the engine
exits at, despite the README saying so. It is gone from the chart and the panel; `FlipLevel` is
still written to the log so the calibration schema does not move. `AlertOnFlip` now means "near a
trigger level".

`EventCalendar.cs` — next high-impact USD release from the ForexFactory weekly feed (same source and
filter as `oceans-crabel/dash/server.mjs`), fetched on the thread pool, cached to
`%APPDATA%\ATAS\OceansCurrent\ff_week.json`, printed in CT. Week-only feed: an empty rest-of-week
says so. It does **not** change the bias around a release — that is a calibration question.

`Scorecard.cs` — every committed LONG/SHORT graded entry close → exit close, in points. Under 30
calls the panel says "(too few)". History is scored without gamma (levels are live-only).

## Seeing it without restarting ATAS

`_preview/` renders the real `DrawPanel` through ATAS's own `GDIPlusRenderContext` (public ctor
taking a Bitmap, in `Rendering.GDIPlus.dll`) into `panel_preview.png`. Scale differs from the live
chart (DPI), layout does not. Use it before every deploy that touches drawing.

`OnRender` writes `%APPDATA%\ATAS\OceansCurrent\render_probe.txt` (≤ every 5 s): the stage the
frame reached, the region, and the rectangle drawn. **File not updating = OnRender not called.**
On 2026-09-10 the panel drew nothing after a restart with no exception and correct saved settings
(`ShowBadge` true); the cause was not found before this probe existed. Check the probe first.

## Not built yet (v1.1)

F3 value migration (prior-day profile via `GetAllPriceLevels()`, POC/VA, the 80% rule) and F6
30-minute one-timeframing. Both are declared, disabled, and permanently `Missing` — a factor nobody
wrote must not have an opinion. `GetAllPriceLevels()` belongs in a once-per-session profile build,
**never** on the per-tick path.

The F3 constants in the spec (open-location base rates, the 80% rule) are Dalton folklore until
measured on the Crabel DB for NQ specifically. Treat them as placeholders.

## The log has to be idempotent, and was not (fixed 2026-09-09)

A chart reload replays every historical bar. The logger appended those rows underneath the ones a
previous load had already written, so a day file accumulated **several complete runs**, at whatever
timeframes the chart had been on. 74 files, 34,014 rows, **17,828 of them superseded replays.**

Nothing about it looked wrong. The files were the right size, the rows were well-formed, and the
first calibration run read straight through them and reported a state changing sixty times in a
session — which is the only reason it was caught at all.

`SessionLogger` now **takes a file over** the first time a run writes to it (`_owned`), and
`Reset()` — called from the rebuild branch in `Calculate` — clears that ownership so the replay
starts each day again. Replacing rather than appending is only safe because `Advance` replays a
rebuild bar-for-bar identically; that is what makes the two equivalent.
`AReplayedDayReplacesItsRowsRatherThanDoublingThem` pins it, and it was mutation-checked.

`_calib/calibrate.py` still de-interleaves defensively (`last_run`), because the logs written
before the fix are on disk and cannot be repaired. **Do not dedupe those on the timestamp** — a
5-minute bar at 10:30 and a 15-minute bar at 10:30 are different bars, not copies, and dropping
one interleaves two state sequences into a third that never happened. A run is a stretch of
strictly rising bar index; keep the last.

## What the first calibration actually said (2026-09-09, 56 sessions)

Not "pass" and not "fail" — **undecidable, and the protocol cannot decide it.**

Every hit rate's 95% interval spans 50%: OBE 36.4% [24–51] at 10:30, the VWAP benchmark 29.5%,
F1 28.6% [18–41], F4 58.9% [46–71]. The composite "beat" the benchmark by +6.8 pp only because
both were far under a coin flip. Sweeping the benchmark across the session clock gives 48, 38, 46,
29, 41, 50, 43, 43, 36 — noise around ~42%, with 10:30 the low draw. Reading the minimum of ten
correlated readings as a finding is how this fools you.

**The §9.3 threshold of ≥40 sessions is the real problem: resolving a 3 pp edge at 80% power needs
~2,200 sessions.** Forty cannot see it, four hundred cannot see it. Either the criterion moves to
an effect size a season can measure, or it is tested per bar rather than per session, or it stops
being the gate. Do not let a run of forty sessions license the weights.

Also true and worth keeping: **the GEX feed has never run.** `regime` is `0` in all 16,186 rows,
`%APPDATA%\ATAS\Ocean\` does not exist, and F7 is therefore entirely unexercised — every number
above describes the price-only composite. F4 is the only factor over 50% at both fit times, which
is the one lead worth pulling.

## Before touching the weights

Do not tune by feel. The protocol in README.md exists because the honest failure mode here is that
F1/F3/F5 are three correlated votes on "where is price relative to structure", and the composite
turns out to be a VWAP-side filter in a costume. The kill criterion — beat "long above session
VWAP at 10:30" by ≥3 pp over ≥40 sessions — decides whether this ships or gets stripped.
