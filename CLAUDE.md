# Quantower Trading Strategies - Technical Documentation

## Overview
This document provides technical context and logic documentation for all trading strategies in this repository. Each strategy has been compiled successfully for the Quantower trading platform.

---

## Repository Layout (reorganized 2026-09-14)

```text
Quantower-storage/
├── Strategies/          - every Quantower Strategy project (AlgoType=Strategy)
│   ├── <name>Strategy/  - one folder per strategy, see catalog below
│   └── Backups/         - dated/backup copies of strategies, kept separate from the active ones
├── Indicators/           - every Quantower Indicator project (AlgoType=Indicator)
│   └── ORB-IX/           - see "Indicator Catalog" below
├── TradingView/          - reference .pine scripts strategies here are ported FROM (not
│                           themselves a Strategy or Indicator project - source material only)
├── CLAUDE.md             - this file
└── README.md             - user-facing parameter reference (not yet updated for every
                            strategy below - see "Known gaps" at the end of this file)
```

Before this date everything sat loose at the repo root with no distinction between what
Quantower treats as a Strategy vs. an Indicator - `Strategies/` and `Indicators/` now mirror
that platform-level distinction directly, so it's visible at a glance which category
something belongs to without opening it. All strategy `**File:**`/`**Readme:**` paths below
are relative to the repo root and already reflect the new `Strategies/` prefix.
`C:\Quantower\Settings\Scripts\...` paths (deployment targets) are unaffected by this repo's
own layout and unchanged throughout.

**Note while moving files**: a background C# language server (VS Code's C# Dev Kit) was
holding file locks on `obj/`/`.vs/` folders inside several strategy projects, and had also
somehow gotten `.vs/` IDE-cache junk committed into git for a handful of projects (it should
never have been tracked - `.gitignore` already excludes `bin/`/`obj/` but not `.vs/`). Both
were cleared out as part of this move: all `obj/`/`bin/`/`.vs/` folders across the repo were
deleted (regenerable build/IDE artifacts, not source) and the stray tracked `.vs/` files were
removed from git. If `.vs/` reappears in `git status` after opening a project in Visual
Studio/VS Code, it's safe to `rm -rf` and never `git add` it.

---

## Indicator Catalog

### ORB-IX (`Indicators/ORB-IX/`)
**Files:** `Indicators/ORB-IX/src/` (full source, 3 projects), `config/orbix.share.json`,
`BUILD.md`, `MANUAL.md`, `README.md`, a pre-built `OrbIxIndicator.dll`, and
`Archive/` (superseded pre-built binaries kept for history - see that folder's own README)
**Source:** UPDATED 2026-09-14 - the friend originally sent only a compiled DLL (in a folder
he'd named after himself); he later sent the **full source** in that same folder. Both are
now merged into this one `ORB-IX/` home (dropping the person-named folder - this is what it
is, source and all, not just a binary anyone happened to send).

#### Build
Three projects (`OrbIx.Core` - pure rules/measurement, no platform types, deliberately
testable without a chart; `OrbIx.Quantower.Shared` - chart period/instrument bridge;
`OrbIx.Quantower.Indicator` - the indicator and its overlays, `net10.0-windows`). Full
instructions in `BUILD.md`; short version:
```bash
dotnet build src/OrbIx.Quantower.Indicator/OrbIx.Quantower.Indicator.csproj -c Release \
  -p:Share=true \
  -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\v1.147.4\bin\TradingPlatform.BusinessLayer.dll"
```
(`v1.147.4` as of 2026-09-23 - confirm against whatever's under `C:\Quantower\TradingPlatform\`
on the machine actually building; BUILD.md deliberately doesn't hard-code it since it moves on
Quantower updates. **Quantower auto-updated from v1.146.18 to v1.147.3 mid-session on
2026-09-15** - every DLL deployed earlier that day had been built against the now-stale
v1.146.18 SDK reference while the live platform had already moved to v1.147.3, and is the
leading suspect for a same-day report of settings changes intermittently blanking the whole
chart. Rebuilt against the correct current SDK and redeployed; if a similarly erratic "changing
one setting breaks everything" report recurs, check this path mismatch FIRST, before
suspecting the paint code. **IT HAPPENED AGAIN 2026-09-23**: Quantower auto-updated overnight
from v1.147.3 to v1.147.4 with no warning; a Finch-Lite build against the old v1.147.3 path
failed outright (MSB3245, "could not locate the assembly") rather than silently — a build
FAILURE is the easy case; the 2026-09-15 incident was worse precisely because that stale
reference still built and deployed something. Confirmed by listing
`C:\Quantower\TradingPlatform\` before assuming the path in any older doc entry, this one
included, is still current — treat every hard-coded `v1.147.x` string in this whole file as a
snapshot of "true as of that entry's date," not a fact to trust going forward.)

**Verified 2026-09-14**: builds clean (0 errors, 269 pre-existing nullable-annotation-
context warnings from the source itself - not touched, not this repo's code to restyle) once
one gap was fixed: the `.csproj` expects the embedded config at `../../config/orbix.share.json`
relative to the indicator project, but the file as sent sat loose at the `ORB-IX/` root with
no `config/` folder. Moved into `config/orbix.share.json` to match what the build expects -
if this indicator is ever re-sent/re-synced from the friend, check whether that's still
where it lands.

**Install:** copy the built `OrbIxIndicator.dll` to its own folder under
`C:\Quantower\Settings\Scripts\Indicators\ORB-IX\` (see the indicator's own README for the
full explanation of why the DLL needs its own folder). **Deployed 2026-09-14** with a fresh
build from this verified source, replacing the originally-provided pre-built DLL (different
hash - not a discrepancy to chase, just confirms it's a from-source build now rather than
whatever binary happened to be attached originally).

**What it is** (from its own README - draws only, places no orders, reads no account):
opening range/session structure, market structure (HH/LL read), volume profiles (FRVP +
anchored), VWAP with bands, and eleven switchable order-flow displays built from each bar's
footprint (cluster stats/search, stacked imbalance, absorption, unfinished auction, big
trades, live counter, DOM levels, delta with flip levels, trend lines/fib fan/GEX walls).
Notably self-calibrates its stacked-imbalance volume floor from the actual tape in front of
it rather than shipping a fixed number tuned on one instrument (MNQ) - see the indicator's
own README for the calibration methodology and status-line format.
**Explicitly not a signal generator** - the author's own README states plainly that none of
these displays are claimed to predict anything, and testing several of the ideas found no
edge; it's a reading-the-tape tool, not an entry system.

**Added 2026-09-15 - wick/volume absorption (`OrbIx.Core/Features/WickVolumeAbsorption.cs` +
`OrbIx.Quantower.Indicator/WickAbsorptionOverlay.cs`, `InputParameter` indices 163-174,
`"Wick Absorption: *"`)**: a direct, statement-for-statement port of the user's own
`Tradingview/absorption.pine` ("works wonders" there) - needs only plain OHLCV bars (no tick
history, no depth), so it's immune to every data-vendor gap documented above and in the
Order-Flow Scalping README. Same detection math as the pine script (high-volume gate vs. a
rolling average, wick-significance gate vs. rolling ATR, P/B pattern, `volume_strength *
wick_strength` scored against a minimum). Drawn as an actual filled+bordered box (selling =
red from the bar's high, buying = green from the bar's low), not another dotted line - the
user's explicit ask, since a chart already carrying HH/LL support/resistance lines, session
levels, and footprint absorption lines made "is this an absorption level or something else"
unanswerable at a glance. On by default (`WickAbsorptionEnabled = true`).

**Same date - three old absorption displays turned OFF by default** (`AbsorptionDrawEnabled`,
`ShowAbsorptionShelves`, `FlowAbsorption`, `FlowVolumeAbsorptionTier1/2`): all footprint-based,
all answering the same "is this absorption" question the new wick/volume port now answers
once instead of four times over, and `AbsorptionShelfScan`'s own docstring already calls that
approach a measured null on MNQ (trial 008: +1.006 ticks vs. matched-random, failed
Bonferroni, below the cost floor). **Only affects a fresh attach** - Quantower persists
settings per chart instance, so an already-attached chart keeps its old saved values; toggle
these off by hand in the settings panel (or remove/re-add the indicator) to get the same
effect on an existing chart. Everything else (Zones/FVG, Imbalance marks, Flip levels, Flow
cluster stats/search, stacked imbalance, unfinished auction, big trades, live counter) was
deliberately left on by default per the user's own call - not redundant with anything else,
just not on their original want-list.

**Same date - ORB box now extends its own fill instead of degrading to bare dotted lines**
(`ChartOverlay.cs`'s `DrawOne`): the crisp bordered box still marks only the formation window
(where the range was actually SET), but the light tinted fill now spans the level's whole
life - formation window through session end - instead of stopping at the formation window's
right edge and handing off to thin `Edges` lines for the rest of the session. That abrupt
handoff was the user's literal complaint ("the orb area should extend the box itself instead
of just a dotted line").

**Same date - fixed coupled settings/paint faults reported as "turn off the opening-range box
and everything disappears"** (`OrbIxIndicator.cs`'s `DrawRanges`): the OR box/edges/midline,
the key/reference price levels, and the active trade setup's lines were three unrelated
things sharing one try/catch AND one early return keyed off "any completed opening ranges to
draw." A paint fault in the OR box took the key levels and the trade setup down with it for
that frame, and a day with zero completed opening ranges (before the first session's range
closes, or after a data gap) hid the key levels and the trade setup too, though neither has
anything to do with an opening range existing. Split into three independently-guarded methods
(`DrawRanges`, `DrawKeyLevelsOverlay`, `DrawActiveSetup`), each gated on its own data. Also
split the one shared `"Draw level labels"` toggle, which controlled the OR box's own level
labels, the key/reference levels' labels, AND the setup's price/R tags at once: added
`"Draw key-level labels"` (`DrawKeyLevelLabels`) and `"Draw setup price/R labels"`
(`DrawSetupLabels`, indices 85/87) as their own settings, leaving `DrawLabels` (80) scoped to
the opening range's own levels only. **Not confirmed to be the full explanation** for
"everything" vanishing if that meant literally every overlay (HH/LL, absorption, delta, flow)
- each of those already has its own independent enable flag and its own try/catch writing to
the shared `overlayFault` string, so a fault in one should not silently take the others with
it; if the problem persists after this fix, the next thing to check is whatever text (if any)
appears where `overlayFault` is displayed on the chart when it reoccurs.

**Same date - found and fixed the actual likely cause: the Direction panel's paint call had NO
try/catch**, unlike every one of its ~20 sibling calls in `OnPaintChart`. Nothing wraps
`OnPaintChart` itself, so an exception there (or the SDK-version mismatch above) propagated
straight past every per-overlay guard and skipped every draw call sequenced after it for that
frame - "toggle a setting, lose everything" is exactly what an unguarded early call in an
otherwise fully-guarded sequence looks like from the chart. Wrapped it in the same
try/catch-into-`overlayFault` pattern as everything else. Also decoupled `PublishDirection()`
from `DirectionPanelOn` - it used to skip computing the panel entirely when the panel was
switched off, which will matter once something else (the callout below) reads the same
computed reading independently of whether the panel itself is displayed.

**Same date - identified the "MIXED 1-1 / structure / location / flow / regime" box that
overlaps order management** as the **Direction panel** (`DirectionPanelOn`, shared with the
standalone Direction indicator) - it paints at a FIXED pixel offset from the pane's corner
(`DirectionPanelOffsetX/Y`, default 12/220), so it can land on top of wherever an order line
happens to sit in price-space depending on zoom/scroll. Already had its own on/off toggle and
offset settings before this session; no code change needed, just repositioning/disabling via
those three inputs.

**Same date - added `"Status: show the problems line"` (`ShowStatusLine`, default true)**
gating `DrawStatus`: the bottom-left "problems" line (`StatusOverlay`/`StatusBlock` in Core)
is drawn BY DESIGN whenever something is genuinely degraded - on this connector its most
common tenant is the same permanent data-vendor refusal documented above (FRVP/AVP started
after their anchor, volume analysis unavailable). That's a deliberate "never hide a real
problem" design (`StatusBlock.cs`'s own doc comment), which the user asked to override for a
KNOWN, unfixable, already-understood limitation that was cluttering the exact spot used for
position management. The setting lets the operator choose; FRVP/AVP profiles themselves
already draw nothing when their data isn't there regardless of this flag - only the text
notice is gated by it.

**Added 2026-09-15 - the scalp/hold confluence callout** (`OrbIx.Core/Direction/
DirectionCallout.cs`, `Quantower.Indicator/DirectionCalloutOverlay.cs`, `InputParameter`
indices 397-402, `"Direction: ..."`/`"callout"`): a bright, hard-to-miss line reading
`POSSIBLE LONG SCALP`, `POSSIBLE HOLD SHORT`, etc., built entirely from confluences the
Direction panel already computes (per-timeframe structure votes, price vs. VWAP, cumulative
delta) - never a new prediction, only a louder rendering of the same verdict plus one extra
question. **Scalp vs. hold rule**: HOLD requires (a) at least `DirectionCalloutMinLanes`
(default 2) structure lanes agreeing with the verdict AND (b) the session range still under
`DirectionCalloutRoomSpentPercent` (default 100%) of the average daily range; anything
directional that fails either check reads as SCALP instead. Both thresholds are STATED
trading judgements, not measured or backtested ones - see `DirectionCallout.From`'s doc
comment. No callout at all when the verdict is Mixed or Undecided. **Independent of the
Direction panel's own on/off toggle** (`DirectionPanelOn`) - either can run without the
other, which is also why `PublishDirection()` was changed (same session, above) to compute
the panel regardless of that toggle. Colours default to pure green/red (`#00FF00`/`#FF0000`,
deliberately more saturated than any other green/red already on the chart) so the callout
reads as "the loud one" against everything else.

**Same date - the line anchors to a LEVEL, not to live price.** First shipped tracking
`this.lastPrice` every fold, which moved the line every tick and made it useless as something
to trade against - the operator's exact complaint ("it needs to stay at a level"). Now
(`PublishDirection()`, `directionCalloutSide`/`directionCalloutAnchorPrice` fields) the line's
price is set ONCE when the callout's SIDE changes (none to long, long to short, etc.) and held
fixed until the side changes again. A scalp read upgrading to a hold read (or the reverse) on
the SAME side updates the line's text and colour in place without relocating it, since that is
still the same opportunity, only re-graded - only a side flip moves the level.

**Same date - HH/LL support/resistance lines now visually stop at the bar that broke them**,
instead of running to the pane's right edge until the Pine-faithful engine gets around to
redrawing them. `HhLlSegment.EndBar` (in `HhLlEngine.cs`, the statement-for-statement Pine
port) only freezes on `rechange`/`suchange` - a NEW opposing pivot redefining the level -
which can lag well behind the bar that actually closed through it; drawing the still-open
segment out to the chart's edge in the meantime read as a level still live long after price
had broken it, which was the operator's exact complaint. Fixed entirely on the paint side
(`BuildHhLlDrawable()`/`FindHhLlBreakBar()` in `OrbIxIndicator.cs`, new `hhllBarClose` array
parallel to the existing `hhllBarOpen`/`High`/`Low`) - for a still-open segment, scans forward
from its pivot bar for the first close beyond its price (above for resistance, below for
support) and clips the DRAWN line there. `HhLlEngine.cs` itself, and `segment.EndBar`, are
untouched - the Pine port's own fidelity to the reference script (tested against
`tools/hhll_reference.py`) is not affected, only how an as-yet-unfrozen segment is rendered.

**Added 2026-09-15 - Ocean's Anchor absorption gate, brought into the indicator to match the
new `directionAbsorptionScalpStrategy`** (`AnchorGateOverlay.cs`, `FeedAnchorGate`/
`FeedAnchorTape`/`RefreshAnchorZone`/`AdvanceAnchorZone` in `OrbIxIndicator.cs`,
`InputParameter` indices 175-179/210-212, `"Anchor Gate: ..."`). Same engine as the strategy -
Ocean's Anchor's Dormant->Armed->Triggered->Confirmed/Expired/Broken state machine, ported from
a friend's ATAS suite at `Ported/src/oceans-anchor/`, compiled in by source (same `<Compile
Include>` pattern and same Assembly.Load cache-collision reasoning as `OrbIx.Core` itself) -
applied to THIS indicator's own HH/LL segments as the zone source, never Anchor's own
footprint-based HVN detector (would hit the same historical-volume-analysis refusal documented
above). Tape-based absorption (a large print showing no follow-through in a timed window) is
fed per-tick from the existing drain loop; the bar-shape 3-of-4 fallback test runs per fold,
bar-close only, and ONLY for the newest bar in a catch-up burst - older bars in a backfill burst
get OHLC-only zone maintenance (arm/break/traverse), never a cluster score, since there is no
buffered tick history to score them with. Drawn as a line at the zone's price, coloured/labelled
by state, with the confirming print's stop reference shown once Confirmed.

**Same date - three defaults changed to reduce clutter, all superseded-by-something-better
rather than arbitrary:**
- `WickAbsorptionEnabled` -> **false**. The Anchor gate above answers the same "is this
  absorption" question with a real tested state machine instead of a single-bar threshold: one
  primary absorption display instead of two. Still a settings-panel toggle away.
- `DirectionPanelOn` -> **false**. The operator's own complaint: this detail box (structure/
  location/flow/regime rows) sits on the chart and gets in the way of order management, and the
  scalp/hold callout line already surfaces the same verdict in far less space. The detail rows
  are one toggle away for anyone who wants to see exactly which inputs are voting.
- (Recall from earlier the same session: `AbsorptionDrawEnabled`, `ShowAbsorptionShelves`,
  `FlowAbsorption`, `FlowVolumeAbsorptionTier1/2` were already turned off for the same
  "redundant absorption display" reason, when `WickAbsorptionEnabled` was still the primary one.
  The Anchor gate is now the fourth and, so far, the last word on absorption redundancy - if a
  FIFTH one shows up, that itself is worth noticing.)

**Added 2026-09-16 - the callout line split into a bias line plus a reversal line, per the
operator's own re-framing.** The single "POSSIBLE LONG SCALP" entry-call line was replaced with
two independent lines, both still anchoring to a frozen PRICE rather than tracking live price
(the earlier "stay at a level" fix is untouched):
- **Bias line** (`DirectionCalloutEnabled`, still index 397, now `"Direction: show bias line"`)
  - same mechanic as before (freezes at the price where `directionCalloutSide` last flipped,
  scalp/hold grade changes update text in place without moving it) - but now reads as a fixed
  regime statement, `"ONLY LONG SCALPS ABOVE"` / `"ONLY SHORT SCALPS BELOW"`, not a graded entry
  call.
- **Reversal line** (`DirectionReversalEnabled`, index 403, new): fires when price crosses to
  the WRONG side of the FROZEN bias line - contradicting the regime that line established -
  tracked independently of `directionCalloutSide` because price moves every tick while the bias
  line does not, so a regime can be violated by price well before the full multi-input vote
  catches up and re-flips the bias line to agree. Text borrows the bias line's own scalp/hold
  wording once the fresh verdict agrees with the reversal direction (`"POSSIBLE REVERSAL — LONG
  SCALP"`), falling back to a plain `"POSSIBLE REVERSAL — LONG"`/`"...SHORT"` until it does.
  Drawn dashed, from the same `DirectionCalloutOverlay` (now takes a `Dashed` option) so no new
  paint machinery was needed - it only paints whatever `PublishDirection` hands it, same
  discipline as everything else here.

**Fixed 2026-09-16 - "Flow: trend lines" appeared to move when panning the chart.** Root cause
confirmed by direct investigation: `EnsureFlowBars()` rebuilds `flowBarOpen/High/Low/Close`
every fold from `HistoricalData[i, SeekOriginHistory.Begin]`, where index 0 is whatever bar is
CURRENTLY oldest in the platform's loaded window. Panning far enough back makes Quantower load
MORE history, prepending older bars - the array gets LONGER, but every existing index now names
an EARLIER calendar bar than it did before. `BuildFlowBias`'s staleness check
(`flowStructureBars > flowBarHigh.Count`) only ever caught the array getting SHORTER, so this
exact case slipped through: `flowStructure`'s already-emitted `HhLlLabel.Bar`/
`TrendLine.StartBar/EndBar` values kept pointing at whatever index they were assigned, which now
named a different bar in the freshly-rebuilt `BarTimes` - the line visibly relocating. Fixed by
comparing the origin bar's OWN TIME (`flowBarOriginUtc`, new field), not just the count: any
change to `flowBarOpen[0]`'s timestamp now forces the same full `flowStructure` rebuild the
shrink case already triggered. Same class of bug as the ADR/prior-day levels' own "strictly
before" date handling elsewhere in this file - an index that means something only as long as its
origin hasn't moved, quietly assumed to be stable.

**Added 2026-09-16 - "reversal incoming" flag on the Anchor Gate, and a trend-line break flag,
both from a live-chart request.** Two more signals layered onto machinery already built the
same day rather than new subsystems:
- **Reversal incoming** (`DirectionReversalIncomingEnabled`/`DirectionReversalIncomingDistanceTicks`,
  indices 404-405): when price has run at least the configured distance from the bias line -
  still IN the trend's own direction, not a violation of it (that is the separate dashed
  reversal line above) - AND the OPPOSING Anchor Gate zone (the one a reversal would actually
  trade) reaches Confirmed, its label changes from `"LONG CONFIRMED"`/`"SHORT CONFIRMED"` to
  `"POSSIBLE REVERSAL INCOMING — LONG"`/`"...SHORT"`. No new drawable or overlay - just an
  `IsReversalSignal` flag on the existing `AnchorGateZoneDraw`, computed in
  `BuildAnchorGateDrawable()`. One known staleness: `FeedAnchorGate()` (which calls this) runs
  BEFORE `PublishDirection()` in the fold sequence, so the label reflects the PREVIOUS fold's
  bias/distance reading, one fold interval (default ~100ms) behind — immaterial for a text
  label, not worth reordering the fold for.
- **Trend-line break flag** (`TrendLineBreakFlagEnabled` + up/down colours, indices 406-408,
  new `TrendLineBreakOverlay.cs`): flags "Flow: trend lines" (the two-tap high/low lines,
  `AutoTrendlines`/`FlowBias.TrendLines`) only when "all signs show break... in that direction"
  - the operator's own bar (2026-09-16): the bar that just closed broke the line's own
  `PriceAt(bar)` value AND the direction callout already agrees with that side ON THE SAME BAR.
  Checked in `UpdateTrendLineBreaks()`, called AFTER `PublishDirection()` specifically so both
  halves of the confirmation are from the same fold, using `this.flowFrame.Bias` (already
  rebuilt earlier the same fold by `FoldFlow`). Checked once per newly-closed bar per line kind
  (high/low) via `trendLineHighCheckedBar`/`trendLineLowCheckedBar`, so a fold that finds nothing
  new never re-flags a bar already judged.

**Added 2026-09-16 - a full-height vertical marker at a delta flip, matching a friend's chart**
(`DeltaFlipVerticalMarker`/`DeltaFlipVerticalMarkerNewestOnly`, indices 1031-1032,
`DeltaLevelsOverlay.FlipVertical`). The existing "Flip levels" feature already computed
everything needed (`DeltaFlip.CrossedBarCloseUtc`) but only ever drew it as a HORIZONTAL price
level running right - a deliberate design constraint stated in the overlay's own class doc
("HORIZONTAL ONLY... anything that reserved panel height here would be paid for by the chart it
is meant to explain"). The operator asked for what a friend's chart does instead: "when the big
moves in delta shift it sends a line straight up" - a vertical line at the FLIP'S TIME, full
pane height, labelled `"FLIP UP"`/`"FLIP DOWN"`. Added as an independently-toggleable exception
to that design note rather than folded into the existing level (WHERE vs. WHEN are different
questions; either can be on with the other off), defaulting to the newest flip only so it
doesn't accumulate into a forest of vertical lines across a long session.

**Added 2026-09-16 - three more defaults turned off, same "reduce clutter" pattern as the
2026-09-15 round above, plus one feature split rather than just muted:**
- `DrawBox` (the opening-range/session box - IB, overnight range) -> **false**. The operator's
  own bar, after asking what a red/purple shaded band on their chart meant and being told it
  was the OR/overnight box: "I dont want these anymore." Deferred full code removal ("do it
  after") - this only changes the default, the feature and its code are untouched and still a
  settings-panel toggle away. Same deferral applies to removing the ORB code from the indicator
  and the strategy entirely, per the operator's own words ("I also dont want the orb in my
  indicator or strategy either... just turn off by default, do it after") - **still not done**,
  tracked here so it isn't lost.
- `ShowStatusLine` -> **false** (was documented `true` on 2026-09-15, above). Same box the
  operator had already asked to keep muted for the FRVP/AVP-unavailable notice specifically now
  gets turned off outright after a screenshot showing it alongside "Δ flip levels 0" cluttering
  the chart with two lines of permanent, already-understood, unfixable-on-this-connector
  status text.
- `ShowDeltaFlips` -> **false**, and DECOUPLED from the vertical flip marker added earlier the
  same day. `ShowDeltaFlips` used to be the one gate for BOTH the horizontal flip-level line
  AND (transitively, since both read the same computed flip list) the vertical marker - turning
  it off would have silently killed the vertical marker too, which the operator explicitly
  wanted to KEEP ("the level flips can go" - horizontal only - "I want ... the line that shoots
  up for the delta" - vertical only, from the same message). Fixed by splitting the DATA gate
  from the DISPLAY gate, the same pattern as every other "one toggle secretly controls two
  things" fix this session: `PublishDeltaLevels()`'s `wantFlips` now reads
  `this.ShowDeltaFlips || this.DeltaFlipVerticalMarker` (either display still needs the
  underlying flip computed), while the horizontal line's own on-chart caption text is gated by
  a new `flipCaption` local that checks `ShowDeltaFlips` alone. Net effect: the horizontal
  level and its "Δ flip levels N" caption are gone by default; the vertical "FLIP UP"/"FLIP
  DOWN" marker (added earlier 2026-09-16, see above) is unaffected and still on.

**Added 2026-09-16 - a consolidated pass built from one large multi-part request** (kept both
the reversal line/callout and everything already built the same day; five additive pieces, all
reusing infrastructure already in place rather than new subsystems):
1. **Reversal-incoming: triangle + label, not just relabeled text.** The `IsReversalSignal` flag
   added earlier the same day (see the "reversal incoming" entry above) originally only swapped
   the Anchor Gate zone's text label. `AnchorGateOverlay.Draw` now also fills a small triangle
   at the confirming zone's own price, pointing toward the bias line's side, alongside the
   swapped `"POSSIBLE REVERSAL INCOMING — LONG/SHORT"` text - the operator's own distinction
   ("this might be a triangle with a label" vs. plain relabeled text on an existing line).
2. **Anchor Gate zones drawn as boxes, not lines** (`AnchorGateZoneDraw` gained `StartUtc`/
   `Top`/`Bottom`, resolved from `zone.StartBar`/`zone.Top`/`zone.Bottom` through the same
   `hhllBarOpen` index space HH/LL already uses). `AnchorGateOverlay.Draw` now fills+borders a
   box from the zone's own start bar to "now" (same still-extending convention
   `WickAbsorptionOverlay` uses for Dormant/Armed/Triggered/Confirmed) instead of a full-width
   line at a single price - directly answers "I want to see absorption boxes... so I know what
   to target as bounce zones." Dormant zones are still never drawn at all.
3. **The rich "ABSORPTION" panel** (new file `AnchorAbsorptionPanelOverlay.cs`,
   `AnchorAbsorptionPanelDrawable`, `InputParameter` indices 218-220
   `AnchorAbsorptionPanelEnabled`/`OffsetX`/`OffsetY`), built after the operator showed a
   friend's chart with a header/`gate: Veto/Confirm` badge/`GEN BUY/SELL · N bars` headline/
   `ΔPRICE`/`ΣDELTA` stat boxes/reasoning text/`LONGS`/`SHORTS` guidance boxes and said "I really
   love how my friend has this... this might be the better way of identifying this." Searched
   the whole `Ported/` bundle for the panel's exact wording (`gate:`, `GEN BUY`, `ΔPRICE`,
   `ΣDELTA`) first and found no match anywhere - **this is not a port of anything in the
   bundle**, it's designed fresh here, informed by the screenshot and by `oceans-effort`'s own
   documented "effort vs. result" idea (a genuine move has price and order flow agreeing;
   absorption is the divergence between them) applied to data the Anchor Gate already tracks.
   **The classifier** (`BuildAnchorAbsorptionPanelDrawable()` in `OrbIxIndicator.cs`): on a
   zone's Dormant/Expired/Broken -> Armed transition, captures `ArmPrice`/`ArmCvd` (new fields
   `anchorLong/ShortArmPrice`/`Cvd`). Each fold, `ΔPRICE = lastPrice - ArmPrice` and
   `ΣDELTA = AnchorSignalEngine.Cvd - ArmCvd` (net session delta since THIS arming, not since
   session open). Same sign on both -> reads as a genuine directional move, badge `"gate:
   Veto"`, headline `"GEN BUY"`/`"GEN SELL"`; opposite signs -> absorption is the more likely
   reading, badge `"gate: Confirm"`. **Validated post-hoc, not just asserted**: checked against
   two numbers independently observed on the operator's own screenshots (+63.8 ΔPRICE /
   +4759 ΣDELTA, and a second showing -4.5 / -241) - both same-sign pairs, both correctly read
   as Veto under this exact rule. This is a STATED heuristic, not a measured one, same
   disclosure standard as `DirectionCallout.From`'s scalp/hold thresholds - flagged as such in
   the drawable's own doc comment. When multiple zones are active, picks the highest-ranked one
   (Confirmed > Triggered > Armed, ties broken toward whichever side the bias line currently
   agrees with). The progress-dot row's denominator is `anchorZoneStateRules.ClockBars` - Ocean's
   Anchor's own confirmation-clock bar count (default 3, inherited as-is; never exposed as its
   own `InputParameter` since nothing so far needed to tune it independently of the ported
   default).
4. **Entry/target marking** (`AnchorEntryMarkingEnabled`/`AnchorTargetMinDistanceTicks`, indices
   216-217): "when it shows only shorts or only longs there needs to be an area marked out that
   says enter short here or enter long here and mark out what i should be targeting." Fires on
   the SAME two-signal gate the strategy already uses before it would enter a trade - an Anchor
   Gate zone reaching Confirmed AND `directionCalloutSide` (the bias line's frozen side)
   agreeing with that zone's side - kept in sync by hand with
   `directionAbsorptionScalpStrategy.cs`'s `OnZoneConfirmed`/`TryEnter` since the strategy and
   the indicator share no compiled dependency by design. Draws `"ENTER LONG/SHORT HERE"` at the
   zone and a target line/label at the nearest QUALIFYING candidate ahead of price, past a
   minimum-distance floor (`TryComputeAnchorTarget()`, reimplementing the strategy's own
   `TryComputeTarget` logic against data the INDICATOR already maintains - no new reference-
   level plumbing needed here, unlike the strategy which had to build `ReferenceLevels` from
   scratch): the nearest opposing `hhllEngine.Segments` level, current VWAP, prior day/week
   levels already in `this.levels`/`LevelGraph`, or a live volume-node shelf (point 5) - in that
   proximity order, whichever qualifies first.
5. **Live-forward ("since session open") volume-node zones** (new file
   `VolumeNodeOverlay.cs`, `AnchorVolumeZonesEnabled`/`Color`/`MinPeakPercent`, indices
   213-215): "I want to see... areas were alot of orders were placed so i know what to target as
   bounce zones," clarified via questions into: build it TICK-BY-TICK going forward rather than
   from history, since this connector already refuses the historical volume-analysis API
   everywhere else in this file (see the FRVP/AVP notes above) - never touching that API here
   either. A plain `OceansAnchor.SessionProfile` instance (already compiled in from the earlier
   Anchor Gate work, no `.csproj` change needed this time) is fed every tick in the same
   `FeedAnchorTape` drain loop (`profile.Add((decimal)price, (decimal)size)`), reset to a FRESH
   instance on every session open (same hook `vwapSession`/`deltaFlips`/`direction` already use)
   - proof it's live-built, not reconstructed from history, is that it is empty right after a
   fresh session open and fills in visibly through the session. Shelves extracted once per fold
   via `AnchorProfileMath.ExtractShelves(profile.VolByPrice, tickSize, hvnSettings)` and drawn as
   full-pane-width filled+dashed-bordered bands, independent of `AnchorGateEnabled` (its own
   toggle) - these are reference/target boxes in their own right, not just Anchor Gate plumbing,
   and also feed point 4's target-candidate list.

**All five pieces verified 2026-09-16**: `dotnet build ... -c Release` - 0 errors (281
pre-existing nullable-annotation-context warnings, same class already noted above, not this
session's code). Deployed to `C:\Quantower\Settings\Scripts\Indicators\ORB-IX\
OrbIxIndicator.dll`, `sha256sum` confirms the deployed DLL matches the freshly built one. As
with every other default change this session, **only affects a fresh attach or a manual
settings-panel change** - an already-attached chart instance keeps whatever it last saved.

**Still open, not yet revisited**: the operator asked once "why does my bias line stop appearing
after a while" - never got a definitive answer back to them. Most likely explanation:
`DirectionCallout.From` draws nothing at all when the multi-input verdict reads Mixed or
Undecided (by design, documented above), and that "no callout" state is now invisible with the
Direction panel defaulted off (no other on-chart indicator says "no bias right now" vs. "bias
line is broken"). Possible fix if this resurfaces: a small, low-key "no bias — verdict is
Mixed/Undecided" marker independent of the full Direction panel.

**Added 2026-09-16 (later same day) - "don't enter" warnings, and a louder entry call, from a
live-chart follow-up.** After seeing the neutral-yellow volume-node boxes and asking whether
they were long or short (they're deliberately neither - a volume node is a price the tape spent
time at, not a directional signal, per its own class doc comment), the operator asked for the
opposite kind of signal: "an indication like enter short or enter long now and something that
is like dont enter in this area." Two clarifying answers fixed scope: BOTH no-entry triggers
count (either is enough on its own), and the entry call reuses the EXISTING Confirmed+bias-
agreement gate rather than firing earlier on Triggered, just made louder. `NoEntryZonesEnabled`/
`NoEntryColor` (indices 221-222, default orange `#FF9900`):
- **Volume-node no-entry** (`VolumeNodeDraw.IsNoEntry`, computed in `BuildVolumeNodeDrawable()`
  against `this.lastPrice` each fold): only the ONE shelf price is currently inside gets
  flagged - not every shelf on the chart, which would just repaint everything orange. Flagged
  shelves swap from yellow to orange fill/border and their label from `"VOLUME NODE"` to
  `"DON'T ENTER HERE — VOLUME NODE"`.
- **Conflicting-signals no-entry** (`AnchorGateDrawable.ConflictWarning`, computed in
  `BuildAnchorGateDrawable()`): true when the Anchor Gate has zones live (Armed or later) on
  BOTH sides at once, OR the bias line's own verdict is Mixed/Undecided
  (`directionCalloutSide == 0`) - the signals disagreeing with themselves, independent of where
  price is. Drawn as a standing banner (`"⚠ DON'T ENTER — SIGNALS CONFLICTING"`) at a FIXED
  pixel position (top-centre of the pane), not anchored to any price, since this condition isn't
  about a price level - fixed `DrawAnchorGate`'s early-return (previously skipped the whole
  overlay call whenever there were zero zones, which would have silently dropped this banner on
  a Mixed-verdict day with no zones armed at all) to also let a zone-less, warning-only draw
  through.
- **"ENTER LONG/SHORT HERE" reworded to "...NOW" and made visually louder** (bigger bold font,
  solid colour-filled background instead of the translucent dark box every other label here
  uses, black text for contrast) - same trigger as before (Anchor Gate zone Confirmed AND the
  bias line already agreeing), the operator's own choice was to keep the trigger and just make
  the call read as more of a clear go-signal.

**Fixed 2026-09-17 - volume nodes were covering most of the visible chart right after a session
opened** ("it seems like the full chart is volume nodes... so were am i supposed to trade at").
Root cause: right after open there are too few distinct prices traded for the live profile to
have any real peak/valley shape yet, so `AnchorProfileMath.ExtractShelves`'s own walk-out
(`ShelfEdgePct` against a peak that wasn't a real peak) kept expanding until it hit Ocean's
Anchor's own default width cap (`MaxShelfTicks = 100`, i.e. 25 NQ points) on several adjacent
price clusters at once - that default was tuned for a full multi-day ATAS footprint profile with
a clearly shaped distribution, not a thin, since-session-open live accumulation. Three
independent tightenings (`AnchorVolumeMinTicks`/`MaxShelfTicks`/`MaxShelves`, indices 223-225):
- **`AnchorVolumeMinTicks`** (default 400): shows nothing at all until this many ticks have been
  fed into the session's own live profile - the honest fix for "the shape isn't real yet" rather
  than trying to tune around it.
- **`AnchorVolumeMaxShelfTicks`** (default 32, vs. Ocean's Anchor's own 100): the width cap
  actually applied to `anchorHvnSettings.MaxShelfTicks` each fold - a shelf now reads as "an
  area", not "a third of the day's range".
- **`AnchorVolumeMaxShelves`** (default 3): if the extractor still returns more candidates than
  this, only the top N by their own `PeakVol` (smoothed peak volume) are drawn - showing
  everything it finds is what covered the chart in the first place.

(This fix landed the morning after the "don't enter" warnings above, once the operator actually
watched the feature run live through a session open - see that entry for the `NoEntryZonesEnabled`
feature itself, which is unaffected by this narrower-shelf fix beyond now having narrower, fewer
shelves to flag against.)

**Renamed 2026-09-17 - "ONLY LONG/SHORT SCALPS ..." bias-line wording changed to "ONLY BULLISH
ABOVE"/"ONLY BEARISH BELOW"** (`OrbIxIndicator.cs`, the fixed regime sentence painted by
`DirectionCalloutOverlay`) - the operator's own preference, no logic change: still the same
regime divider frozen at the price where the multi-input verdict last flipped side, just
different words for the same two outcomes. The reversal line's own wording (`BuildReversalText`,
which still borrows the callout's scalp/hold grading, e.g. "POSSIBLE REVERSAL — LONG SCALP") was
left as-is - that is a different concept (how urgent/how long the setup reads), not just a label
for the same two states, and the operator's ask was specifically about the bias line's wording.

**Recoloured 2026-09-17 - every footprint-based Flow level (Unfinished Auction, Volume
Absorption tiers, Stacked Imbalance, footprint Absorption) now follows ONE consistent rule:
green = bullish = long-limit candidate, red = bearish = short-limit candidate** (`config/
orbix.share.json`'s per-feature `bullishColour`/`bearishColour`/`lowColour`/`highColour` keys -
these colours are config-only, never exposed as their own `InputParameter`s, so this is the only
place they can be changed; a rebuild is required for the embedded config to take effect, same as
any other change to this file). Explained to the operator first (what "UA high 122×4" and "126
over 17 tick(s) −x7.4" actually mean - Unfinished Auction and Volume Absorption, two SEPARATE
features that happened to draw in similarly-hued pinks at an overlapping price, which is what
actually prompted the confusion), then the operator said outright: "I have been putting limit
orders on them and scalping the bounce from it and works out perfect but i need to know in what
direction" - confirming this is a live, working trade practice, not idle curiosity, and that the
one missing piece was a legend a glance could read. Before this change each feature family had
its own unrelated colour pair (Unfinished Auction purple/pink, Stacked Imbalance blue/orange,
footprint Absorption teal/red-ish, Volume Absorption cyan/magenta for tier1) with no shared
meaning across the chart - a reader had to remember five different colour schemes instead of one.
Reused colours ALREADY established elsewhere in the same config file for "buy"/"sell" (line
~605's `buyColour`/`sellColour`, `#00E676`/`#FF5252`) rather than inventing new ones, so these
lines now read the same green/red as everything else on the chart already does (Anchor Gate
long/short, the bias/reversal lines, Wick Absorption boxes, big-trades markers). Volume
Absorption's own tier1-vs-tier2 "strength reads as brightness, not a label" design (see
`FlowLevelStyle.cs`'s own doc comment) is preserved - tier1 (moderate) got DIMMER green/red
(`#2E7D32`/`#B71C1C`) than tier2 (heavy, `#00E676`/`#FF5252`, the same bright pair as everything
else) - so strength is still legible at a glance, just within the green/red family now instead of
a separate cyan/magenta family. **Deliberately NOT recoloured**: Cluster Search's single yellow
`#FFEB3B` - that display has no bullish/bearish side of its own (its own doc comment: "a hit is a
hit, and its side is already said by where the marker sits against the bar"), so forcing it into
green/red would misrepresent a non-directional signal as directional.

### Finch-Scalping (`Indicators/Finch-Scalping/`) — added 2026-09-17, REBUILT TWICE since

**A brand-new, separately-installed indicator (its own DLL, its own name in Quantower's picker,
its own per-chart settings — entirely independent of ORB-IX), built on the operator's own
request** ("I want a brand new indicator setup were it just shows the delta on the bottom of my
chart and it also has the line that comes up from the delta and i also want the lines that show
up that show the bids and asks and the fib and golden pocket and call this indicator
Finch-Scalping", plus a mid-build follow-up adding "the box that is currently in the top right
that shows the buy and sells imbalance and the triangle delta thing").

**Two false starts, briefly, because they explain the current shape.**
1. First version: copied ORB-IX's ~9000-line `OrbIxIndicator.cs` verbatim, renamed the class,
   flipped every `InputParameter` default not on the keep-list to `false`. Kept showing clutter
   anyway — traced to every property still carrying ORB-IX's own `InputParameter` NAME AND INDEX
   verbatim, which Quantower's settings persistence most likely keys by rather than by which
   indicator assembly declares it, so ORB-IX's own saved values silently carried over.
2. Second version: rebuilt from scratch as a genuinely small, self-contained file (just
   `DeltaSeriesEngine`/`DeltaFlipEngine`/`SessionClock` from `OrbIx.Core`, two new small overlay
   files, no shared `InputParameter` identity with ORB-IX at all). Displayed NOTHING on a live
   chart — traced to `OnInit` trying exactly once and giving up permanently if
   `HistoricalData.Aggregation` was not yet populated at that instant (the same startup race
   ORB-IX's own `OnInit` already hit and fixed with a retry timer; this rebuild had dropped that
   retry on the wrong assumption that a pure delta engine needed nothing worth waiting for).
   Fixed with the same retry pattern ORB-IX uses, plus a minimal on-chart fault line so a future
   startup failure would be visible instead of silent — but by the time it was confirmed fixed,
   the operator had already decided the risk of debugging unfamiliar platform-integration code a
   second time wasn't worth it, and asked to go back to a literal ORB-IX copy and cut from a
   KNOWN-WORKING baseline instead.

**Current approach (2026-09-18): back to a literal copy of `OrbIxIndicator.cs`** (same file, same
`.csproj` shape as ORB-IX itself — `OrbIx.Core`, `OrbIx.Quantower.Shared`, every
`OrbIx.Quantower.Indicator` overlay file, `Ported/oceans-anchor`, an embedded config — everything
ORB-IX needs, because at this starting point Finch-Scalping IS ORB-IX byte-for-byte except the
class name and `this.Name` string), verified to render identically to ORB-IX on a live chart
before anything was removed. Features are being cut from THIS copy one at a time — actual
deletion, not just an `InputParameter` default flip, so the settings-collision class of bug
above cannot resur for anything actually removed (whatever's left still shares ORB-IX's
parameter identity until it's specifically dealt with).

**The operator also described their real trading workflow** (2026-09-18), which reframed what
the "keep" list should be: HTF (1h/4h) absorption levels, order blocks and FVG for read-only
top-down context, then drop to a 30s chart and scalp bounces off LARGE RESTING BID/ASK ORDERS.
Two corrections this produced:
- The "lines that show the bids and asks" feature picked earlier (Stacked Imbalance — historical
  footprint runs) is the WRONG one for "large bid and ask orders" — that's **DOM levels** (live
  resting order sizes), a different feature. Swapping to DOM levels.
- Anchor Gate absorption currently builds its zones from THIS CHART's own HH/LL structure — on a
  30s chart that means 30s swings, not the 1h/4h structure the operator actually wants read-only
  context from. This needs rebuilding against a higher timeframe, not just re-enabling — not yet
  started.
- The HTF Zones/FVG/order-blocks feature already exists (`ZonesEnabled` + a `ZoneTimeframe`
  string, default "4h") but only shows ONE timeframe at a time via a text field. The operator
  wants "two switches, pick one at a time" (a 1h toggle and a separate 4h toggle) instead of
  retyping a timeframe string — not yet built.

**Removed so far (actual deletion, from the fresh ORB-IX copy), all "debug stuff on the chart"
the operator explicitly did not want** (distinct from the actual absorption zone boxes/HH-LL
tags, which stay — those are the real signal the HTF workflow needs, not debug noise):
- The rich ABSORPTION panel entirely — `BuildAnchorAbsorptionPanelDrawable()`, `DrawAnchor
  AbsorptionPanel()`, the `anchorAbsorptionPanelOverlay`/`anchorAbsorptionPanelDrawable` fields,
  the `anchorLong/ShortArmPrice/Cvd` tracking fields that only fed it, and all three of its
  `InputParameter`s (`AnchorAbsorptionPanelEnabled`/`OffsetX`/`OffsetY`) — the "gate: Confirm"
  badge, ΔPRICE/ΣDELTA boxes, reasoning sentence, LONGS/SHORTS guidance boxes all came from this
  one method and are now gone from the code, not merely defaulted off.
- The old per-cell Imbalance display (`ImbalanceDrawEnabled`, was defaulting `true` — the
  "...min 10 / stack 3 · gate ON · UNVERIFIED on this stack" caption), the old book-based
  Absorption display (`AbsorptionDrawEnabled` — "absorption 5s: bid Quiet · ask Quiet · gate
  recording only"), the Absorption Shelves scan (`ShowAbsorptionShelves` — "absorption shelves:
  the footprint at ... has no matching delta bar..."), and the status/problems line
  (`ShowStatusLine`) — these four were already defaulting `false` (or, for Imbalance, got set to
  `false`) in ORB-IX's own current source, but were STILL rendering because ORB-IX itself is
  attached to the same test chart with these manually turned on from earlier testing, and (per
  the settings-collision theory above) Finch-Scalping's copy shared their exact parameter
  identity. Fixed the same way as the first rebuild's 38 properties: removed the
  `[InputParameter(...)]` attribute from all four so Quantower's settings system can no longer
  see or override them, regardless of what ORB-IX's own chart has saved for that name.

**Verified 2026-09-18**: `dotnet build src/Finch.Scalping.Indicator/Finch.Scalping.Indicator.csproj
-c Release -p:Share=true -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\v1.147.3\bin\
TradingPlatform.BusinessLayer.dll"` — 0 errors after the copy, and 0 errors again after the debug-
text removal pass above. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Scalping\
FinchScalpingIndicator.dll`, `sha256sum` confirms each deploy matched. Confirmed rendering on a
live chart (candles, structure, both indicators showing consistent output) before the removal
pass began.

**Fixed 2026-09-18 (same day) - the Delta panel disappeared** ("were is my delta that was at the
bottom") right after the debug-text removal pass above, even though `DeltaEnabled`/`PublishDelta`/
`DrawDelta` were all untouched by that pass. Same settings-collision mechanism as everything
else in this entry: `"Delta: enable panel", 140` is byte-for-byte identical to ORB-IX's own
parameter, and ORB-IX's chart (also attached, from earlier testing) most likely has a different
saved value for it than this copy's own `= true` default. Fixed the same way: stripped the
`InputParameter` attribute from `DeltaEnabled`. Applied the SAME fix proactively, before another
one of these went missing, to every other property this indicator currently depends on actually
working: `DeltaFlipVerticalMarker`/`DeltaFlipVerticalMarkerNewestOnly` (feature 2), `FlowEnabled`/
`FlowClusterStatistics` (the other half of "delta at the bottom"), and `HhLlEnabled`/
`AnchorGateEnabled`/`AnchorGateShowPanel` (required for the absorption zone boxes/HH-LL tags the
operator's HTF workflow actually wants kept). None of these are configurable from the settings
panel any more, same trade-off as every other property this fix has touched — worth revisiting
once Finch-Scalping stops sharing a chart with ORB-IX during testing, since the underlying
ORB-IX/Finch-Scalping identity collision is what makes losing configurability the safer choice
right now. Rebuilt (0 errors), redeployed, hash-verified.

**Not yet done, in order**: (1) confirm the panel is back and the debug-text removal actually
cleaned up the chart; (2) swap Stacked Imbalance off / DOM levels on; (3) turn off Big Trades and
the live counter box (not mentioned in the operator's actual workflow, may not be wanted at all —
ask before assuming either way); (4) split HTF Zones into two independent 1h/4h toggles; (5) the
hard one — rebuild Anchor Gate to read a higher timeframe's structure instead of the chart's own.
Fib/golden pocket status is also unresolved — not mentioned in the trading-workflow explanation,
needs asking. **A recurring lesson worth stating plainly**: nearly every regression in this
indicator so far has been the SAME settings-collision mechanism hitting a different property each
time — worth checking first, before assuming a code change broke something, whenever a feature
that should be working goes missing or reappears unexpectedly.

**Aside — ATAS X discovered and mapped (2026-09-21).** The operator installed a second platform,
"ATAS X" (`D:\ATAS X` — the program's own install folder, NOT where custom indicators go).
Custom indicator DLLs actually live in `%AppData%\ATAS X\Indicators\`; a friend-provided
`OceansDelta.dll` the operator had staged in `Quantower-storage\Atas\` turned out to be already
sitting there and byte-identical (`sha256sum` confirmed) — nothing needed copying. That same
folder also holds `OceansPivotDecoder.dll`/`V2`, `FinchysCross.dll`, a stray Quantower-built
`OrbIxIndicator.dll` (won't run in ATAS — different SDK, likely dropped there by accident), and
a stale duplicate `OceansDelta(1).dll` with a DIFFERENT hash from the current one — worth
cleaning up if the operator ever asks, not yet done since they hadn't decided.

**Added 2026-09-22 - Ocean Delta Cross, ported from the operator's own ATAS indicator into
Finch-Scalping** (`OceanDeltaCrossOverlay.cs`, new; `Ported/src/oceans-delta/DeltaMath.cs`
compiled in by source — 123 tests + a mutation pass in that bundle's own suite, platform-free,
same "compile the clean tree" reasoning as `oceans-anchor`). A richer sibling of the plain
vertical flip marker already in Finch-Scalping: same "session cumulative delta confirmed a
change of side" event, but now ALSO draws a horizontal arm at the price the flip was actually
PAID FOR — the crossing bar's close by default, or (when a genuine one-sided cluster inside that
bar clears volume/delta/lean thresholds, same defaults the friend's own ATAS indicator ships:
60 volume / 30 delta / 35% lean) the cluster price itself, drawn DASHED when no cluster
qualified rather than hidden ("a flip nobody paid for is still information" — copied verbatim
from that bundle's own CLAUDE.md). Cluster search is fed from THIS indicator's OWN
`FootprintEngine` (matched to the crossing bar by open time, the same join key `ScanShelves`
already uses and for the same reason — verified, not assumed), not ATAS's
`candle.GetAllPriceLevels()`. Feeds through `Ported/src/oceans-delta`'s own tested
`OceansDelta.DeltaEngine` (decimal-based, separate from `OrbIx.Core.Features.DeltaFlipEngine`
which still drives the plain vertical marker unchanged) rather than reimplementing the flip-arm/
confirm-distance state machine a second time. **Deliberately NOT ported yet** — the richer ATAS
original's multi-session flip-zone bands and "lines in the sand" iceberg levels — kept for later
per this project's "one feature at a time" discipline; the cross itself was the part the operator
actually asked for.

**Added 2026-09-22 (same day) - Swing High and Low, ported from the operator's own ATAS
indicator** (`SwingPointOverlay.cs`, new — simple up/down triangle arrows, matching the
reference chart exactly). Deliberately a FRESH, standalone detector rather than a re-skin of the
existing HH/LL structure engine, which uses a different confirmation rule (Pine's own
leftBars/rightBars) and a different visual language (boxed "HH"/"HL" tags) — this one implements
the plain symmetric N-bar pivot rule the operator's own ATAS settings screenshot describes
("Period" = 10, "Include Equal" = checked): a bar is a swing high/low once `SwingPeriod` bars on
EACH side confirm it is the window's own extreme. Reads off the ALREADY-maintained
`hhllBarOpen`/`High`/`Low` arrays (no second pass over `HistoricalData`) and scans incrementally
(`swingCheckedBar` cursor, each bar examined once) rather than rescanning from bar zero every
fold. **Guards against the exact origin-shift hazard "Flow: trend lines" already hit once**
(documented above, 2026-09-16): compares the array's own first-bar TIME each fold, not just its
length, since panning can silently renumber every existing index without necessarily changing
the array's own length — the bug class that slipped through a length-only check before. No
`InputParameter`s exposed for `SwingPeriod`/`SwingIncludeEqual`/colours (`SwingHighColor`/
`SwingLowColor`) — hardcoded consts, matching every other Finch-Scalping feature added since the
settings-collision fix, since exposing them would just be more surface for the same collision
class to eventually hit.

**Verified 2026-09-22**: both features built together, `dotnet build ... -c Release` — 0 errors.
Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Scalping\FinchScalpingIndicator.dll`,
`sha256sum` confirms each deploy matched. Not yet confirmed on a live chart — that verification
(does the cross's horizontal arm land at a sensible price, do the swing arrows match the ATAS
reference visually) is still outstanding.

### Finch-Lite (`Indicators/Finch-Lite/`) — added 2026-09-22

**CORRECTION 2026-09-23 — do NOT tell the operator to remove/re-add the indicator after a
redeploy.** Every "Verified" entry below this point that says "ask the operator to remove/re-add
Finch-Lite" is WRONG and should not be repeated or trusted as instruction — it was said
repeatedly across this whole file's Finch-Lite history despite this exact codebase already
documenting the correct behaviour elsewhere (see the WickAbsorption entry under ORB-IX: "Quantower
persists settings per chart instance, so an already-attached chart keeps its old saved values").
Removing and re-adding an indicator discards its ENTIRE saved settings blob and creates a fresh
instance at every parameter's coded default — not just a new one's. The operator hit this for
real ("this is showing but it removed my large orders on the dom" → "i got it working you reset
my settings thats all" → "stop reseting my settings its screwing me up"). Correct instruction
going forward: after a redeploy, **restart Quantower** — this reloads the updated DLL while
Quantower restores every EXISTING parameter's saved value from the chart's own settings blob;
only a genuinely brand-new `InputParameter` (absent from the old saved blob) falls back to its
coded default. Remove/re-add is for when a full reset is actually wanted, not the routine
post-deploy step.

**A THIRD, deliberately separate indicator** ("all these moving parts in quantower are making
the charts super slow lets start a new indicator and add only 1 thing at a time and it doesnt
need to be a port from the finch-scalping or anyhting we will build this on our own" — the
operator's own words). Root cause of the slowness this responds to: Finch-Scalping is still, at
its core, a literal copy of ORB-IX's ~9000-line fold sequence — every feature not currently
wanted was only ever disabled at the DISPLAY layer (an `InputParameter` or a paint-time guard),
never removed from the fold itself, so the full footprint engine, HH/LL engine, Anchor Gate
absorption tests, zone detection, Wave1, anchored VWAP etc. all still run every ~100ms regardless
of which of Finch-Scalping's own displays are switched on. Finch-Lite has none of that: it
compiles in NOTHING from `OrbIx.Core` or `Ported/` at all, and its fold-equivalent only ever
contains the features actually built so far.

**Architecture**: `Indicators/Finch-Lite/src/Finch.Lite.Indicator/` — a single small `.csproj`
(no `<Compile Include>` globs reaching into other projects — the SDK's own default globbing
picks up this project's own files), referencing only the Quantower SDK itself
(`TradingPlatform.BusinessLayer`) and `System.Drawing.Common`. `FinchLiteIndicator.cs` is a
fresh, from-scratch `Qt.Indicator` — its `OnInit` uses the SAME retry-until-ready pattern
Finch-Scalping had to relearn the hard way on 2026-09-18 (try once, and if the platform hasn't
published what's needed yet, a one-second timer keeps retrying rather than giving up
permanently) applied FROM THE START this time, plus an on-chart amber fault line
(`overlayFault`) from the first build rather than added after a "why is nothing showing" report.

**Feature 1 (2026-09-22) — large resting orders**: a full-pane-width horizontal line at any
price currently carrying a resting bid or ask at or above a configurable size threshold
(`LargeOrderMinSize`, default 100 contracts) — "an area marked out that has a lot of large
resting orders" (the operator's own phrase). Colour is the operator's own explicit, deliberate
choice, NOT this codebase's usual bullish=green/bearish=red convention: **red for a resting
BID** (a seller would have to hit it to clear it) and **green for a resting ASK** (a buyer would
have to lift it), each labelled with its side and size (`"BID 500"` / `"ASK 500"`).
- **Pulled, not streamed** — reusing a hard-won platform fact from ORB-IX's own `PullBook`
  method rather than re-discovering it: this connector's LIVE Level2 event stream arrives
  stamped `"generated_from_level1"`, meaning the connector fabricates a book from the top-of-book
  touch because it has nothing else to publish there (ORB-IX measured 19,762 such updates
  carrying zero real depth). The platform's own PULL API,
  `DepthOfMarket.GetDepthOfMarketAggregatedCollections`, returns the genuine book on the same
  connection — confirmed by ORB-IX to return 50 real prices a side. Finch-Lite polls this
  directly on its own `Timer` (`PollIntervalMs`, default 250ms) rather than subscribing to the
  event stream at all, so it can never fall into that trap in the first place.
  `GetMBOItems: false` — this feature shows AGGREGATE size resting at a price, not a breakdown of
  individual orders; that distinction can be revisited if the operator ever wants literal
  per-order counts.
- `RestingOrderOverlay.cs` — a small, self-contained overlay (its own local `TryY` coordinate
  helper, not shared with any other project) drawing the line + label per qualifying level.

**Verified 2026-09-22**: `dotnet build src/Finch.Lite.Indicator/Finch.Lite.Indicator.csproj -c
Release -p:Share=true -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\v1.147.3\bin\
TradingPlatform.BusinessLayer.dll"` — 0 errors on the FIRST build (8 warnings total, all the
same harmless nullable-annotation-context style noise every project here carries, none from
inherited ORB-IX bulk this time — the whole point of starting clean). Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart or checked for actual CPU/
frame-time improvement over Finch-Scalping — both still outstanding. All `InputParameter` names
here are unique to this indicator (never existed in ORB-IX), so the settings-collision class of
bug documented at length under Finch-Scalping above cannot apply to this project.

**Added 2026-09-22 (same day) - two session-specific thresholds instead of one** ("lets make the
options work for asia session and one for ny session for the min amount of orders so its easier"
— a single fixed "large" size does not fit both a thin overnight book and a busy NY day session).
`LargeOrderMinSizeNy`/`LargeOrderMinSizeAsia` (defaults 100/50) replace the original single
`LargeOrderMinSize`. Session windows are read in `America/New_York` wall-clock time via a plain
`TimeZoneInfo.ConvertTimeFromUtc` check (no `OrbIx.Core.Sessions.SessionClock` — this project
compiles in nothing from `OrbIx.Core` at all, and hand-rolling one time-of-day comparison is not
worth breaking that), matching boundaries ORB-IX's own configuration already established for
these same names rather than inventing a different convention here: NY session 09:30-18:00 ET
(RTH open through GLOBEX's own 18:00 reopen), Asia session everything else — an EXHAUSTIVE two-
way split of the full day, not a third "other" bucket the operator did not ask for. (Also fixed
in passing: `LevelsToScan` and `BidColor` had accidentally been given the same `InputParameter`
index, 12, while adding the two new thresholds — caught and renumbered before it could cause a
settings mix-up of its own.)

**Added 2026-09-22 (same day) - feature 2, a full-depth DOM ladder** (`DomLadderOverlay.cs`, new
— "is there a way to show like a dom on the right hand side thats a bar so i can tell all
resting orders"). The large-order lines from feature 1 only ever show levels that cleared the
threshold; this draws a bar for EVERY level the same poll already scans, length proportional to
size, along a configurable-width strip (`DomLadderWidth`, default 150px) on the pane's right
edge — a "quiet backdrop" at lower opacity (alpha 120 vs. the large-order lines' own fuller
colour) so the highlighted large-order lines still read as the louder signal sitting on top of
it, drawn first specifically so the ordering holds. Both sides share ONE scale
(`DomLadderDrawable.MaxSize`, the single largest size across bids AND asks) rather than each
side scaling against its own max — scaling separately would draw an equally-sized bid and ask as
different bar lengths, which would misrepresent the one thing a length-comparison ladder exists
to show honestly. Fed from the SAME poll `BuildLadder` runs off of (`book.Bids`/`book.Asks`,
already fetched for feature 1) — no second `DepthOfMarket` call, same "poll once, build every
drawable from it" discipline the periodic-pull design already established.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors after both additions. Deployed
to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**Added 2026-09-22 (same day) - feature 3, big trades** (`BigTradeOverlay.cs`, new — "a long bar
that comes out and makes a line on the chart were on the specified value for large trades so i
have a super clean line knowing were price would react off of"). A THIRD, distinct trigger from
features 1/2: those read the resting BOOK (what's sitting there right now); this reads the TAPE
(what already traded) via a `Symbol.NewLast` subscription — the first tick subscription this
project has needed — and marks the price of every recent print at or above
`BigTradeMinSize` (default 50) as a full-pane horizontal line, capped to the newest
`BigTradeMaxKept` (default 10). Same colour language as the resting-order lines: green for a buy
(lifted the ask), red for a sell (hit the bid); a print the feed cannot classify to a side is
never queued at all — a line asserting "buyers did this" for a fill the feed itself would not
attribute to a side would be a guess dressed up as a fact. **Kept the market-data thread
discipline this codebase repeats everywhere else**: `OnLast` does nothing but a size comparison
and a `ConcurrentQueue.Enqueue` — the actual list mutation and drawable rebuild happen in
`DrainBigTrades()`, called from the existing poll timer rather than a new one.

**Redesigned 2026-09-22 (same day) - features 1 and 2 both changed from a live-only snapshot to
"remembered for the whole trading day".** Two follow-up asks landed together and turned out to
be the same underlying request: "what i need is to see all the levels of the order book not just
a small snap shot and for the large orders i define it makes the line extend so i can see them
easily" (this was the answer to a clarifying question about an earlier, more narrowly-worded ask
— "instead of levels to scan i want to do it by the current trading day which starts at 6pm
EST" — that pointed at the real target once the operator restated it in their own words).
- **`LevelsToScan` raised from 50 to 500** (range extended to 2000) — asking for more than
  `GetDepthOfMarketAggregatedCollections` actually has to give is harmless on this connector
  (ORB-IX already measured it topping out around 50 real prices a side), so this reads as "give
  me everything the platform has" rather than a second, smaller ceiling stacked on top of the
  platform's own.
- **Large-order lines now persist for the whole trading day, not just while the order is still
  resting.** `restingOrderMemory` (`Dictionary<(double Price, bool IsBid), RestingOrderDraw>`)
  remembers every level that EVER cleared the threshold since the trading day opened, keeping
  whichever size was LARGEST ever seen there (a level once carrying 800 contracts still says 800
  after it thins out or gets pulled — relabelling it down as it fades would misreport what
  actually happened there). `RestingOrderDrawable` is now built from this memory every poll,
  not from the live book snapshot directly — the live snapshot only decides what to FOLD INTO
  the memory, per `RememberLargeLevels`.
- **Trading-day boundary is 18:00 America/New_York** (`TradingDayStart`, `TradingDayOpen`) — the
  same GLOBEX-reopen convention this project's own NY/Asia threshold split already established,
  reused here for consistency rather than inventing a second day-boundary rule. The memory clears
  itself the first poll after a new trading day starts (`tradingDayStartUtc` comparison inside
  `RememberLargeLevels`), and again on `OnClear` (a fresh attach starts the day's memory over,
  consistent with this project's live-forward-only design — there is no historical backfill to
  seed it from, by the same "never touch the historical volume-analysis API" discipline ORB-IX
  established for its own live-only volume nodes).
- The DOM ladder (feature 2) stays a pure live snapshot on purpose — it exists to show the
  CURRENT shape of the book, not a day's history of it; only its scan depth changed.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors after both changes (12 warnings
total, all the same harmless nullable-annotation-context style noise every project here
carries). Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — in particular, whether `restingOrderMemory` accumulating unboundedly through a very
active trading day becomes a real memory concern is unmeasured; worth revisiting if a session
runs long and the operator notices anything.

**Fixed 2026-09-22 (same day) - "some of the bid are a little hard to view"**, once large-order
lines started persisting for the whole trading day (above) a busy price band could accumulate
many close-together levels whose problems compounded: labels drew directly on top of each other
(`RestingOrderOverlay` had no label-collision avoidance at all), and GDI+ does not cap
accumulated alpha — several semi-transparent DOM-ladder rows stacked on the same pixels get MORE
opaque, not less, so a dense cluster of adjacent levels washed out into one solid,
undifferentiated red block rather than reading as several distinct sizes. Three fixes:
- **`RestingOrderOverlay`** now sorts levels largest-first, skips a line within `MinLineGapPx`
  (6px) of an already-drawn line on the SAME side (largest wins, so a dense cluster reads as its
  few biggest levels instead of a solid wall of near-identical adjacent lines), and reserves
  label space via the same collision-avoidance every other overlay in this codebase already uses
  — a lower-priority label is dropped rather than drawn illegibly on top of one already placed.
- **`DomLadderOverlay`**'s alpha lowered from 120 to 70, so overlapping rows in a busy band stay
  visually distinct instead of saturating to one flat colour.
- **One shared label registry** now covers BOTH `RestingOrderOverlay` and `BigTradeOverlay`
  (previously `BigTradeOverlay` used its own separate, local registry) — a resting-order label
  and a big-trade label landing at the same spot now also respect each other.

**Fixed 2026-09-22 (same day) - "the colors are swaped the red should be on top and green on the
bottom".** Not a bug in the literal sense — asks always rest ABOVE price and bids always rest
BELOW it, which is the order book's own structure and not something this indicator rearranges —
but the operator's actual intent was the COLOUR MEANING, not the book structure: they wanted the
top (ask/resistance) red and the bottom (bid/support) green, matching this codebase's usual
bullish=green/bearish=red convention, rather than the "bid=red, ask=green" they had originally
specified when this feature was first built (2026-09-22, earlier the same day). Fixed by swapping
`BidColor`'s and `AskColor`'s DEFAULT VALUES (bid now defaults green, ask now defaults red) —
the property names and their meaning are unchanged, only which literal colour each defaults to.
**Caught in the same pass**: `BigTradeOverlay`'s buy/sell colours were being derived from these
same two properties (`Options(this.AskColor, this.BidColor)`), so the bid/ask swap would have
silently flipped buy trades to red and sell trades to green too — buy/sell is a DIFFERENT axis
(the aggressor, not which side of the book a price rested on) and swapping one should not have
silently swapped the other. Given its own independent `BigTradeBuyColor`/`BigTradeSellColor`
(green/red, unaffected by whatever `BidColor`/`AskColor` are set to).

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors after all three fixes. Deployed
to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. **Confirmed working on a live chart the same day** — the
operator's own words, "now lets make a backup of this its perfect."

**Backed up 2026-09-22** at `Indicators/Backups/Finch-Lite-2026-09-22/` — a full source snapshot
(`bin`/`obj` excluded, same convention as every other backup in this repo) plus the exact
deployed `FinchLiteIndicator.dll` (`sha256sum`-confirmed identical to the live deployment at
backup time), taken at this "all three features verified working" milestone specifically so
there is a known-good restore point before any further changes. See that folder's own
`README.md` for what state it captures and how to restore from it.

**Fixed 2026-09-22 (same day) - a silent blind spot found while investigating "the dom on the
right some how got removed"** after removing ORB-IX from the same chart. No on-chart fault box
was showing, which meant `TryInitialise` had succeeded and the poll was not throwing — but a
call that succeeds while `GetDepthOfMarketAggregatedCollections` happens to come back with an
EMPTY book (zero bids AND zero asks — a dried-up feed, a connector hiccup, or some other cause
not yet isolated) previously produced empty drawables with no diagnostic at all, indistinguishable
from "the market is quiet right now" and from "something broke silently". `OnPollTimer` now
reports this case the same way every other poll problem already is, so it is visible on the
chart instead of a page that just looks clean with nothing on it and no way to tell why. This is
a DIAGNOSTIC addition, not a confirmed root-cause fix — whether an empty book is actually what
was happening in the reported case is still unconfirmed; the amber box appearing (or not) after
this deploy is itself the next piece of evidence. Rebuilt (0 errors), redeployed, hash-verified.

**ROOT CAUSE FOUND AND FIXED 2026-09-22 (same day) - Finch-Lite silently depended on ORB-IX
being attached to the SAME chart to get real depth-of-market data, despite sharing zero code or
data with it.** The amber box above confirmed the pull was returning a genuinely empty book;
disconnecting and reconnecting the whole data feed did not fix it; adding ORB-IX back to the
chart fixed it INSTANTLY, with everything Finch-Lite had already built up still present — proof
nothing was actually broken or lost, only that depth data had stopped flowing.

**The mechanism**: `Symbol.NewLevel2 +=` calls `SubscribeAction(Level2)` on the platform (read
in the decompiled assembly — this exact fact is already documented in ORB-IX's own
`OrbIxIndicator.cs`, `PullBook`'s doc comment, from a 2026-09-14 investigation into this same
API). That subscription is what tells Quantower to keep requesting/maintaining live depth for a
symbol from the connector AT ALL — `GetDepthOfMarketAggregatedCollections` (the PULL api this
whole indicator is built around) reads from that SAME maintained depth, it is not an independent
data source. ORB-IX subscribes to `NewLevel2` for its own unrelated reasons (its own footprint/
flow ladder), and that subscription was incidentally the ONLY thing on the chart keeping depth
alive for Finch-Lite's pull to read from. The moment ORB-IX was removed, nothing was left
telling the platform to keep depth flowing, and the pull started returning nothing — not a bug
in Finch-Lite's own logic, an unstated cross-indicator dependency this project was never
supposed to have in the first place (its whole `.csproj` comment states the goal as "add only 1
thing at a time... it doesnt need to be a port from finch-scalping or anything").

**The fix**: `FinchLiteIndicator.OnLevel2` — a handler subscribed purely for this SIDE EFFECT,
deliberately reading NOTHING from its own `Level2Quote`/`DOMQuote` payload (ORB-IX separately
measured this connector's live Level2 EVENT stream as fabricated from the top-of-book touch,
`"generated_from_level1"` — the same trap `RestingOrderOverlay`/`DomLadderOverlay`'s own doc
comments already cite as the reason this project pulls instead of streams). Subscribed/
unsubscribed alongside the existing `NewLast` wiring in `TryInitialise`/`OnClear`. Finch-Lite now
holds its own Level2 subscription open and no longer depends on any other indicator being
attached to the same chart for depth data to exist at all.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet re-confirmed on a live chart WITHOUT ORB-IX attached
— that is the actual test of this fix and is still outstanding. Worth remembering for ANY future
indicator built in this repo that touches `DepthOfMarket`: the pull API is not self-sufficient on
its own; something has to hold a `NewLevel2` subscription open first, even one that does nothing
with what it receives.

**Redesigned 2026-09-22 (same day) - large-order levels now resolve, instead of only ever
persisting.** Two asks landed together: "once a bid has been filled i need it to disapear from
the chart" and "if its an unfinished auction it needs to label that and show how many more
contracts are at that unfinished auction". The earlier same-day "persist all day" redesign had
gone too far in one direction — a level, once flagged, stayed at its peak size forever
regardless of what actually happened to it. `RestingOrderDraw` gained an `IsUnfinished` flag (and
its `Size` field now means "the number to SHOW", not always "the peak") and `restingOrderMemory`
was replaced with `restingOrderPeaks` (`Dictionary<(double Price, bool IsBid), double>`, PEAK
size only — never what to display) plus a new `ReconcileRestingLevels`, called once per poll
instead of the old per-side `RememberLargeLevels`:
- A remembered level absent from the current book (or at size 0) is REMOVED outright — "once a
  bid has been filled ... disapear from the chart". The book alone cannot distinguish a genuine
  fill from a plain cancel; this codebase already states elsewhere that where a real distinction
  cannot be drawn, the honest thing is not to claim one — either way, nothing is left resting
  there worth marking.
- A remembered level still present but BELOW its recorded peak now draws as **"UNFINISHED
  AUCTION — BID/ASK N LEFT"** instead of its ordinary label, with the REMAINING (current) size
  shown, not the original peak — a deliberate departure from ATAS's own bar-footprint-based
  "Unfinished Auction" (both sides trading at a bar's own extreme, see ORB-IX's
  `UnfinishedAuctionScan.cs`) since Finch-Lite has no footprint/tape-correlation infrastructure
  to build that exact concept from; this reuses the NAME for the closest concept this indicator
  actually has the data to support honestly — a large resting order that started getting taken
  but was not fully cleared.
- A remembered level still present AT OR ABOVE peak is unchanged from before (persists, peak
  raised if the book now shows more than last recorded).
- Every dictionary mutation is collect-then-apply (`toRemove`/`toRaise` lists, applied after the
  enumeration loop) rather than mutating `restingOrderPeaks` mid-foreach — removing a key during
  enumeration is never safe in .NET, so this never relies on the narrower claim that updating an
  existing key's value alone would have been.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**FEATURE 4 (2026-09-22, same day) — delta panel: "now lets add the delta at the bottom"**, the
very first thing ever asked for across this indicator's whole history (back when it was still
Finch-Scalping), now built fresh here with zero ORB-IX/Finch-Scalping code reused. New file
`DeltaPanelOverlay.cs`: `DeltaBarDraw(DateTime OpenUtc, double Delta, double CumulativeAfter)`,
`DeltaDrawable` (immutable paint snapshot, `Empty` static), `DeltaPanelOverlay` — a band anchored
to the pane's own bottom edge showing a buy-minus-sell histogram plus a cumulative-delta line,
independently discovered to use the same `Options(int HeightPx, Color UpColor, Color DownColor,
double BarsWidth)` shape ORB-IX's own `ZoneOverlay.cs` already uses for the identical feature —
not copied, converged on independently from the same problem.
- `TryInitialise()` now also resolves the chart's own bar period (`ChartPeriod(HistoricalData?)`,
  reading `HistoricalData.Aggregation as HistoryAggregationTime`) before starting the poll timer,
  storing it in `deltaBarPeriod` — reusing the exact "retry until the platform actually has this
  populated" discipline this project already documented for `Symbol`/`DepthOfMarket` above,
  since a chart's `HistoricalData.Aggregation` can be transiently unpopulated right after attach
  too.
- `OnLast` now enqueues EVERY classified tick (buy/sell, not just ones over `BigTradeMinSize`)
  onto a new `deltaTickQueue`, still doing nothing but the enqueue on the market-data thread —
  same discipline as `bigTradeQueue`.
- `DrainDeltaTicks()` (poll timer only) buckets queued ticks onto the chart's own bar period via
  `Bucket(DateTime, TimeSpan) => new(ticks - (ticks % period.Ticks), Utc)`, closing a bucket into
  `deltaBars` (capped at `MaxDeltaBarsKept = 2000`) when a tick's bucket moves past the currently
  open one, and always including the still-forming bucket as a live, updating bar in the rebuilt
  `deltaDrawable` — waiting for a bar to close before showing it at all would make the panel look
  a whole bar-period behind. Cumulative delta resets at the same 18:00 America/New_York trading-
  day boundary (`TradingDayStart`) already established for the large-order features — one
  day-boundary convention for the whole indicator, not a second one invented for this feature.
- New `InputParameter`s: `DeltaPanelEnabled` (default true), `DeltaPanelHeightPx` (default 110),
  `DeltaUpColor`/`DeltaDownColor` (green/red, matching this codebase's usual convention).
- Drawn in `OnPaintChart` gated on `DeltaPanelEnabled`, using `this.CurrentChart?.BarsWidth ?? 1d`
  for histogram bar width (the established pattern every other bar-aligned overlay in this
  codebase already uses). Disposed in `Dispose()`; all delta fields reset in `OnClear()`.

**Same-day follow-ups, all in this one pass — absorption colour strength, and separating
unfinished auctions from ordinary large orders visually:**
- **"the strength of the color of the bids based on asorbstion were lets say sellers are
  defending or an area were buyers are defending"** — a resting level's line now draws at colour
  STRENGTH proportional to how many contracts have traded through it while it kept standing, not
  a flat opacity for every qualifying level. New tracking alongside `restingOrderPeaks`:
  `restingOrderLastSize` (the size actually observed last poll, per price+side) and
  `restingOrderAbsorbed` (running total of size drops while the level stayed present — a refill
  afterward does not erase this; the level still had to absorb that flow to still be there).
  `ReconcileRestingLevels` computes the delta each poll (collect-then-apply, same discipline as
  the existing `toRemove`/`toRaise` lists) and both new dictionaries clear alongside
  `restingOrderPeaks` at the trading-day boundary and whenever a level fully empties out (a level
  that reappears later is a NEW level, not a continuation of one already filled).
  `RestingOrderDraw` gained an `Absorbed` field; `RestingOrderOverlay.Pen(Color, double strength)`
  blends alpha from 70 (never yet absorbed anything) up to 220 (absorbed
  `AbsorptionStrongContracts`, new `InputParameter`, default 200) — the stronger the colour, the
  harder that side has defended the level. Pens are cached by the BLENDED colour's own ARGB value,
  so this stays bounded to however many distinct strength buckets are actually on screen, not one
  pen per level.
- **"lets also make the unfinished auctions white because right now its laying out large orders
  as unfinished auctions so its like bundling the two together"** — unfinished auctions previously
  drew in the same bid/ask colour as an ordinary still-full-size large order (just with different
  label text), so the two categories read as one at a glance. New `UnfinishedAuctionColor`
  `InputParameter` (default white) always wins for an unfinished level regardless of side, drawn
  at full strength (1.0) rather than the absorption-scaled strength above — a distinct, fixed
  category marker, not a shade of the defense signal.
- **"move that text over some so i can see the dom more clearer"** — the "UNFINISHED AUCTION —
  BID/ASK N LEFT" (and ordinary "BID/ASK N") labels anchor at the pane's right edge, which is
  exactly where the DOM ladder (Feature 2) draws its bars — labels were sitting on top of the
  ladder. `RestingOrderOverlay.Options` gained `LabelInsetPx`; `OnPaintChart` now passes
  `DomLadderWidth` as that inset whenever the ladder is enabled (0 otherwise), so labels always
  clear the ladder strip instead of overlapping it.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors (same pre-existing nullable-
annotation warnings only). Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart — ask the operator to
remove/re-add Finch-Lite (new `InputParameter`s need a fresh attach to pick up their defaults)
and confirm: the delta panel appears at the bottom and updates live; large-order lines now vary
visibly in strength as they get hit and hold; unfinished auctions read as white, distinct from
ordinary bid/ask lines; and the DOM ladder on the right is no longer covered by label text.

**Same-day follow-up fixes (screenshot review) — delta panel readability, unfinished-auction
label placement:**
- **"the delta looks very weird and not like my old one"** — screenshot showed the delta band's
  histogram/cumulative-line marks visually tangled with the candles behind them. Root cause: the
  band's background was filled at 140/255 alpha, so the price action sitting in that same screen
  region showed straight through instead of being hidden — every other Finch-Lite feature draws
  ON the one price pane by design (no separate window), so a genuinely separate-reading panel
  needs a NEAR-OPAQUE background to stand in for that separation, not a translucent tint. Raised
  `DeltaPanelOverlay`'s background to 235/255 alpha (`Color.FromArgb(235, 12, 14, 20)`, same RGB
  family the other overlays' label backgrounds already use), added a visible top border line
  (`Color.FromArgb(150, Gray)`) so the panel's own top edge reads as a clean boundary rather than
  fading into price above it, and raised the zero-line's own alpha from 90 to 130 so it stays
  legible against the now much darker band.
- **"lets make those lines come off the candle it self instead of the dom to keep it clean"** —
  the unfinished-auction inset fix earlier this same day (above) kept those labels on the pane's
  RIGHT edge, just pulled left of the DOM ladder strip; the operator wants them off that side
  entirely. `RestingOrderOverlay.Draw` now anchors an `IsUnfinished` level's label at the pane's
  LEFT edge (`pane.Left + 4f`, the same anchor `BigTradeOverlay`'s own labels already use)
  instead of the right — the right side (DOM ladder + ordinary "BID/ASK N" labels) now stays
  completely free of this category, while unfinished-auction text sits over near the candles on
  the left. `LabelInsetPx` still applies to ordinary bid/ask labels, unchanged.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**Same-day follow-up round 2 (screenshot review) — delta panel redesigned into three rows,
unfinished-auction lines now originate from their own candle:**
- **"it needs to be volume as one catagory and session delta as another category and delta as
  the other category"** — the single combined histogram+cumulative-line design read as one
  blurred signal once the background was made opaque. `DeltaPanelOverlay` now splits its band
  into three stacked, separately-scaled rows, top to bottom: **VOLUME** (total size traded per
  bar, one neutral colour — new `DeltaVolumeColor` `InputParameter`, default cornflower blue —
  since volume itself has no direction), **DELTA** (buy-minus-sell per bar, the up/down
  histogram, unchanged design but confined to its own row with its own zero line), **SESSION
  DELTA** (the running session total, now a dedicated row instead of sharing space with the
  per-bar histogram). `DeltaBarDraw` gained a `Volume` field (`buy + sell` per bucket, computed
  in `DrainDeltaTicks`'s `CloseBucket` and the still-forming-bucket path alongside `Delta`).
  `DeltaPanelHeightPx`'s default raised 110 → 150 (three rows split three ways needs more total
  height to stay legible) and its minimum raised 40 → 60 to match. Each row carries a small
  left-aligned text label ("VOL" / "DELTA" / "SESSION DELTA") so the three are never ambiguous.
- **"the unfished auctions need to be centered just to the right of the candle it comes off of"**
  (illustrated with a manually-drawn ray starting just past a specific candle) — an unfinished
  auction's line previously spanned the whole pane from the left edge, same as an ordinary
  large-order line, which said nothing about when the level was actually noticed. New tracking:
  `restingOrderFirstSeenUtc` (`Dictionary<(double Price, bool IsBid), DateTime>`), stamped with
  the poll's own `nowUtc` the moment a level is first added in `AddNewLevels`, cleared alongside
  `restingOrderPeaks`/`restingOrderLastSize`/`restingOrderAbsorbed` on removal and at the
  trading-day boundary. `RestingOrderDraw` gained a `FirstSeenUtc` field. `RestingOrderOverlay`
  gained a `TryX` helper (converts a UTC time to a pane-clamped pixel column, same shape as
  `DeltaPanelOverlay`'s own) — an unfinished level's line now starts at `FirstSeenUtc`'s own
  screen column and extends right to "now" (the same "still extending" convention already used
  elsewhere in this codebase for live zones) instead of starting at the pane's left edge; its
  label anchors just to the right of that same start column, clamped so it never runs past the
  pane's own right edge. Ordinary (non-unfinished) large-order lines are unchanged — still full
  pane width, still anchored right for their label.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**Same-day follow-up round 3 — unfinished-auction colour reverted, label shortened:**
- **"lets color the unfinished auction line the acording color like we do for the large
  orders"** — the dedicated white `UnfinishedAuctionColor` added earlier today is gone again;
  an unfinished auction's line and label now use the same `BidColor`/`AskColor` (and the same
  absorption-driven colour-strength scaling) as an ordinary large order. Index 16 (where
  `UnfinishedAuctionColor` lived) is retired rather than reused, so a saved workspace still
  carrying a value there simply has it ignored, not silently reinterpreted as something else.
  `RestingOrderOverlay.Options` dropped its `UnfinishedColor` field.
- **"lets shorten the words for unfinished auctions of UA - Bid and what not"** —
  "UNFINISHED AUCTION — BID N LEFT" is now "UA - BID N" (and "UA - ASK N"), matching the
  operator's own example. The line's own origin-based positioning (fixed earlier today — starts
  at the level's `FirstSeenUtc` and extends right) is unchanged; only the colour and label text
  changed here.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**Same-day follow-up round 4 — origin-based line span extended to ALL large-order lines, not
just unfinished auctions:** "lets shorten the ask and bids for large orders as well instead of
going all the way across the chart lets only go across like the ua does" — the origin-based line
span fixed for unfinished auctions earlier today (start at `FirstSeenUtc`'s own screen column,
extend right to "now") now applies unconditionally to every resting-order line, ordinary and
unfinished alike; the `level.IsUnfinished &&` guard on the `TryX` call was simply dropped.
Ordinary large-order labels are UNCHANGED — still anchor near the pane's right edge, inset clear
of the DOM ladder; only the LINE's own span changed. `restingOrderFirstSeenUtc` already existed
for every tracked level (not only unfinished ones), so no new tracking was needed for this.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**Same-day round 5 — delta panel redesign abandoned; ported ORB-IX's own design instead:**
"lets not use this one and rip out the delta at the bottom from the orb-ix if tat makes it
easier" — rather than a third from-scratch redesign, `DeltaPanelOverlay.cs` is now a direct COPY
of ORB-IX's own `DeltaPanelOverlay` (`OrbIx.Quantower.Indicator/ZoneOverlay.cs`) — the single-band
design (one shared row: per-bar delta histogram + session cumulative-delta polyline) the operator
was already used to, in place of both this same day's earlier attempts (the original combined
band, then the three-row volume/delta/session-delta split).
- **Not a shared dependency** — no reference to `OrbIx.Core`, no shared assembly. The drawing
  code is copied and re-typed against Finch-Lite's own `DeltaBarDraw` (reverted to just
  `OpenUtc`/`Delta`/`CumulativeAfter`, dropping the `Volume` field the three-row attempt added).
  Finch-Lite's zero-dependency architecture stays intact — this is a port, the same way the
  project's own founding principle already allows for ("it doesn't need to be a port from the
  finch-scalping" was about not COPYING that codebase wholesale, not a ban on borrowing a design
  the operator explicitly asks for by name).
- **Deliberately NOT ported**: ORB-IX's divergence markers and its research status line
  (data source / unclassified-% / parity) — both read from ORB-IX's own historical-volume-
  analysis delta engine, which Finch-Lite has no equivalent of and was never asked to build.
  Finch-Lite's bars still come from live ticks only (`FinchLiteIndicator.DrainDeltaTicks`,
  unchanged).
- **Kept from this project's own earlier attempt**: the near-opaque background fix (235/255
  alpha with a visible top border), rather than reintroducing ORB-IX's own original 140/255 —
  the exact "candles show through and blend with the histogram" problem this same session
  already diagnosed and fixed here is present in ORB-IX's original too, it just never surfaced
  there; no reason to bring back a known problem while porting the rest of the look.
- `DeltaPanelHeightPx` reverted 150 → 110 default (min 60 → 40), matching a single band's own
  sizing instead of three stacked rows. `DeltaVolumeColor` `InputParameter` removed (retired,
  not reused — same reasoning as the earlier `UnfinishedAuctionColor` retirement above).

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**ROOT CAUSE FOUND AND FIXED 2026-09-22 (same day) — the delta panel was nearly empty because
`OnLast` was silently dropping most prints, not because the market was quiet.** Screenshot on
MGC/Rithmic showed a near-blank delta band (one lone bar) against a reference platform's dense,
gap-free histogram — "why is the delta at the bottom showing like this still and not like this".
`OnLast` originally trusted ONLY `Last.AggressorFlag`, dropping any print flagged neither Buy nor
Sell from both `deltaTickQueue` and `bigTradeQueue` entirely. This codebase already found and
NAMED this exact class of problem: ORB-IX's own `AggressorConvention`
(`OrbIx.Core/Features/AggressorConvention.cs`) measured that a vendor's aggressor flag can be
missing, sparse, or even INVERTED, and classifies a print from its own GEOMETRY against the
quote instead — a print at or above the ask was taken by a buyer lifting the offer, a print at
or below the bid was hit into a resting bid, inclusive on both touches (a strict cross would
discard nearly every print, since a touch is the overwhelmingly common case). New
`FinchLiteIndicator.TryClassify(Qt.Symbol, Last, out bool isBuy)`: trusts `AggressorFlag` FIRST
when it says Buy or Sell outright; only when it says neither does it fall back to comparing
`last.Price` against `symbol.Bid`/`symbol.Ask` (the current quote, same proxy-for-quote-at-print
technique ORB-IX's own `OnLast` already uses). A print strictly inside the spread, or a quote
that's missing/locked/crossed, still carries no evidence and is still dropped, not guessed — same
"no guess dressed up as fact" discipline this method already had, just no longer throwing away
prints a flag never bothered to mark. Applies to both the delta panel and the big-trade lines
(Feature 3), since both read the same classification.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches.

**FEATURE 4 REMOVED 2026-09-22 (same day) — "lets just remove the delta bar its not helpful at
all."** After five rounds of iteration this same day (fresh combined band → three-row split →
ORB-IX-ported single band → tick-classification fix), the operator decided the feature itself
wasn't earning its place, not that any particular version of it was wrong. Fully torn out:
- Deleted `DeltaPanelOverlay.cs` (the `DeltaBarDraw`/`DeltaDrawable`/`DeltaPanelOverlay` types and
  drawing code).
- Removed all delta fields/state from `FinchLiteIndicator.cs`: `DeltaPanelEnabled`,
  `DeltaPanelHeightPx`, `DeltaUpColor`, `DeltaDownColor` (`InputParameter` indices 40-43, now
  retired — not reused, same convention as the earlier `UnfinishedAuctionColor` retirement),
  `deltaBarPeriod`, `deltaTickQueue`, `deltaBars`, `deltaBucketOpen`/`deltaBucketOpenUtc`/
  `deltaBucketBuy`/`deltaBucketSell`, `deltaCumulative`, `deltaDayStartUtc`, `deltaPanelOverlay`,
  `deltaDrawable`, `MaxDeltaBarsKept`.
- Removed the `DrainDeltaTicks()` method entirely and its call from `OnPollTimer`.
- Removed the chart-bar-period gate from `TryInitialise()` (`ChartPeriod(HistoricalData?)` and its
  "waiting for the chart to publish its own bar period" retry) — nothing else in Finch-Lite needs
  the chart's own period now that delta bucketing is gone, so `TryInitialise` no longer waits on
  it. The `ChartPeriod` helper method was deleted too (no remaining callers).
- Removed the delta paint call from `OnPaintChart` and `deltaPanelOverlay.Dispose()` from
  `Dispose()`; removed all delta-field resets from `OnClear()`.
- **Kept**: `FinchLiteIndicator.TryClassify` (the geometry-fallback aggressor classifier fixed
  earlier this same day) — it still serves `BigTradeOverlay`'s buy/sell classification (Feature
  3), so it stays even though the feature that first exposed its importance is gone. `OnLast` no
  longer enqueues onto a delta queue, only the big-trade queue when a print clears
  `BigTradeMinSize`.

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**ROOT CAUSE FOUND AND FIXED 2026-09-22 (same day) — some large-order lines flashed between
"BID N" and "UA - BID N" every ~250ms.** "why do some of the line flash between UA and bid i
have seen it happen a few times now" — `isUnfinished` in `ReconcileRestingLevels` was a bare
`current < peak` comparison with no dead zone. A price level often carries orders from MULTIPLE
participants, not just the one large order this feature is tracking; ordinary unrelated add/
cancel churn at that exact price can wobble the aggregate size by a contract or two poll to
poll, crossing under the recorded peak on one poll and back over it on the next — toggling the
label and colour every poll. New `UnfinishedDropThresholdPct` `InputParameter` (index 17,
default 10): a level now only reads as unfinished once it has dropped by at least this many
PERCENT of its own recorded peak (`current <= peak * (1 - pct/100)`), so routine single-digit-
contract noise stays under the bar while a real fill big enough to matter still clears it easily.
Only the entry condition changed — peak-raising (`toRaise`) and absorption accumulation are
unaffected, since neither of those flickers (peak only ever rises, absorbed only ever
accumulates).

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches.

**BIG ROUND 2026-09-22 (same day) — "lets work on adding tiered absorbtion that is color
cordinated also mark out were big trades happened", plus two bugs found via screenshot review
that landed in the same pass:**

1. **Tiered absorption colour.** Asked which style via `AskUserQuestion`; the operator picked
   "same hue, discrete steps" over an independent heat-map ramp — bid stays green, ask stays red,
   always. `RestingOrderOverlay`'s continuous alpha fade replaced with `AbsorptionTier(double
   fraction)`, five fixed steps against `AbsorptionStrongContracts`: fresh (alpha 70) → light
   (120, ≥25%) → moderate (170, ≥50%) → strong (210, ≥75%) → maxed out (255 AND a thicker 2.5px
   line, ≥100%) — the top tier gets an extra visual cue since "fully proven" is worth more than
   one more alpha step alone can say. `Pen()` cache key extended to `(Argb, Width)` since the
   maxed tier now varies width too.

2. **Mark out where big trades happened.** The persisting horizontal line (Feature 3) says WHERE
   but never WHEN. `BigTradeDraw` gained a `TimeUtc` field (from `last.Time`); `BigTradeOverlay`
   now also drops a filled circle at the exact (time, price) of the print, radius scaled by size
   relative to `BigTradeMinSize` on a square-root curve (`MarkerRadius`, clamped 3-14px) so a
   10x-threshold print reads bigger without a 10x-wider circle, plus a thin dark border for
   contrast against candles. New `MinSize` field on `BigTradeOverlay.Options`.

3. **ROOT CAUSE FOUND AND FIXED — "why is the unflished auction levels moving they should be
   static lines".** A price briefly missing from ONE poll's returned depth snapshot (the
   platform's own pull is not perfectly stable poll to poll for deeper levels, independent of
   anything actually trading) was removed outright and, the instant it reappeared, re-added as a
   brand-new level — resetting `restingOrderFirstSeenUtc` to that instant, walking the line's own
   origin rightward every time it happened. New `restingOrderMissingPolls`
   (`Dictionary<(double,bool), int>`, consecutive-miss counter) and `MissingPollGrace = 2`: only
   past 2 consecutive misses is a level actually removed; a shorter blip keeps the level (and its
   origin, peak, and absorbed history) fully intact. Reset to zero the moment the level is seen
   again — a miss streak never carries across a good poll.

4. **REDEFINED — "unfinished auctions work were price moved past a price fast and left orders
   behind that is what is considered a unfinished auction".** Screenshot review showed the OLD
   trigger (current size dropped some % below its own recorded peak) flagging dozens of
   2-19-contract "UA" levels while price sat essentially still — the size-drop signal was simply
   too noisy and didn't match what the operator actually means by the term. Asked via
   `AskUserQuestion` whether to detect this by DISTANCE (price has already moved a meaningful
   amount past the level) or by SPEED (a rolling price-velocity check, needing new tracking
   machinery); operator picked distance. New `UnfinishedDistanceTicks` `InputParameter` (default
   8, replacing the retired `UnfinishedDropThresholdPct` from earlier today) — a level now reads
   as unfinished once the CURRENT BOOK'S OWN MID PRICE (best bid + best ask, from the exact same
   snapshot the poll already pulled — no extra call) has moved at least this many ticks
   (`Symbol.TickSize`) past the level's own price, in the direction that would have consumed it —
   below a bid, above an ask. The size no longer has to have dropped AT ALL: "left orders behind"
   means the orders are still resting there, whatever their size, once price has moved on. Applies
   only to entries already in `restingOrderPeaks` (large-order-qualified levels), which is what
   the operator meant by "should only happen on the large order lines that appear" — true by
   construction, since this loop never visits anything else.

5. **New size filter.** "i also need a setting to allow me to filter out the size of the
   unfinished auctions so if they are low then i dont need to see them" — new
   `UnfinishedMinRemainingSize` `InputParameter` (default 0 = show all, matching prior behaviour).
   DISPLAY-only: a level below this remaining size is skipped from the drawable but stays fully
   tracked (peak, absorption, origin, miss-grace) — it can still reappear in the drawable later if
   its remaining size changes, or continue accumulating absorption/tier colour in the background
   even while hidden.

**Files touched**: `RestingOrderOverlay.cs` (tiered pens, `AbsorptionTier`), `BigTradeOverlay.cs`
(`TimeUtc`, marker drawing, `MarkerBrush`/`TryX`), `FinchLiteIndicator.cs` (all five items above —
new fields/dictionaries/`InputParameter`s, `ReconcileRestingLevels` rewritten for the miss-grace
and distance-based trigger, `OnLast`/paint-call site updates for the marker fields).

**Verified 2026-09-22**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart — ask the operator to
remove/re-add Finch-Lite (new `InputParameter`s need a fresh attach) and confirm: absorption
colour visibly steps between a handful of distinct strengths rather than fading smoothly; big
trades show a sized dot at the exact print location in addition to the standing line; UA lines no
longer creep/jump; and far fewer, more meaningful UA levels appear (only once price has actually
moved past them), filterable further by `UnfinishedMinRemainingSize` if still noisy.

**FOLLOW-UP 2026-09-23 — big-trade label moved off the fixed left edge.** Screenshot showed a
"SELL 54" label pinned at the pane's far left while its line/marker sat near the current price on
the right — "lets make this text more in the center instead of the far left". `BigTradeOverlay`'s
label now anchors just to the right of the print's own marker (same `TryX(trade.TimeUtc)` already
used to place the marker) instead of always at `pane.Left + 4f`, falling back to the old left-edge
anchor only when the print's own time can't be placed on screen (scrolled out of view), and
clamped so it never runs past the pane's right edge.

**Verified 2026-09-23**: `dotnet build ... -c Release` — 0 errors. Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart.

**ROOT CAUSE FOUND AND FIXED 2026-09-23 — large-order lines showed stale, no-longer-real sizes,
disagreeing with this indicator's own DOM ladder right next to them.** Screenshot showed a dense
stack of "ASK 75/73/159/113/..." lines the operator said were no longer really on the book —
"these orders need to update on the dom because i dont see these large orders still on the dom...
they need to update in real time." Root cause was a REGRESSION from yesterday's UA-trigger
redesign, not a data-refresh problem: `ReconcileRestingLevels`'s final loop still displayed
`isUnfinished ? current : peak` — correct back when `IsUnfinished` meant exactly "current is
below peak" (the two conditions covered each other), but yesterday's redesign changed the trigger
to a PRICE-DISTANCE rule instead. A large ask sitting well above current price can never be
flagged unfinished under that rule (price hasn't crossed above it), so it kept displaying its
remembered PEAK size no matter how much its live size had actually shrunk — while the DOM ladder
(Feature 2, built fresh from the exact same poll's book every time) correctly showed the smaller
live number right next to it, producing the mismatch. Fix: `RestingOrderDraw`'s `Size` now ALWAYS
shows the live `current` size; `peak` is bookkeeping only from here on (raising the bar,
absorption-delta comparisons) and is never again what gets displayed.

**Also this same day**: Quantower auto-updated `v1.147.3` → `v1.147.4` overnight with no warning
— see the auto-update lesson under ORB-IX's own build section above (now updated with a second
occurrence). Rebuilt against `C:\Quantower\TradingPlatform\v1.147.4\bin\...` after the first build
attempt failed outright with MSB3245 (the DLL simply couldn't be found) — a much easier failure
mode to catch than the silent stale-build incident from 2026-09-15, but still worth checking
first on any Quantower-adjacent build weirdness.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — ask the operator to remove/re-add Finch-Lite and confirm large-order line sizes now
track the DOM ladder's own numbers in real time, including levels that have shrunk without price
ever moving past them.

**ROOT CAUSE FOUND AND FIXED 2026-09-23 (same day) — "now my dom on the right is super small on
the order size".** `DomLadderOverlay` had scaled every bar's length against the single LARGEST
size anywhere in that poll's scanned book (`DomLadderDrawable.MaxSize`) since Feature 2 was first
built. A screenshot showed a rare 575-contract ask outlier had become that denominator, squashing
every ordinary 20-150 contract level down to a near-invisible sliver — the ladder's entire visual
scale rode on whatever the single biggest resting order happened to be at that instant, with no
floor against one outlier wrecking it for everyone else. Fixed by scaling against a fixed,
configurable reference size instead: new `DomLadderFillSize` `InputParameter` (default 150) — a
level at or above it fills the strip fully (clipped via `Math.Clamp`, not stretched further), so
one huge order no longer flattens everyone else's scale. `DomLadderDrawable.MaxSize` removed
entirely; `BuildLadder` no longer computes a running max, just builds the bar array.
`DomLadderOverlay.Options` gained `FillSize`, used directly at paint time instead of a value
baked into the drawable at poll time.

Caught and fixed one self-inflicted regression while removing `MaxSize`: `BuildLadder`'s
`DomLadderEnabled` gate had lived INSIDE that method (return `Empty` when disabled) — it was the
ONLY place that flag was ever checked, since the paint call site draws unconditionally. Making
`BuildLadder` briefly `static` while restructuring nearly dropped that check silently (disabling
the ladder would have stopped doing anything). Kept it as an instance method specifically to keep
the enabled-gate in the one place it actually lived.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — ask the operator to remove/re-add Finch-Lite and confirm ordinary-sized DOM ladder
bars are visibly longer again even when one outlier level is on the book, and that toggling `DOM
ladder: enable` off still actually hides it.

**FEATURE 5 + 6 (2026-09-23) — 15m/1h order blocks and inverse fair value gaps.** "i would like to
see the 15minute order blocks and 1hr order blocks that are labeled and the ability to see inverse
fairvalue gaps" — Finch-Lite's first departure from pure order-book/tape reading into
multi-timeframe price-STRUCTURE analysis. Genuinely large addition (four new files); before
writing any of it, dispatched a research agent to find how this codebase already solves
higher-timeframe historical pulls, rather than rediscovering platform quirks blind — found the
live, self-updating `Symbol.GetHistory(Period, HistoryType, DateTime)` pattern (confirmed working
in `Strategies/emaCrossStrategy/emaCrossStrategy.cs`) and the exact bar-indexing syntax
(`data[i, SeekOriginHistory.Begin] is HistoryItemBar item`) already used throughout
`OrbIxIndicator.cs`. Also found `OrbIx.Core/Structure/ZoneEngine.cs` already has a working FVG +
order-block detector — read for reference/precedent, but NOT ported or referenced: this is
genuinely fresh logic, re-derived to match the operator's own specific answers below (ZoneEngine's
own rules differ in several particulars).

Three explicit design decisions clarified via `AskUserQuestion` before writing any detection code
(getting the core algorithm wrong would have meant redoing real work, same lesson as the
"unfinished auction" redefinition earlier this week) — operator picked the first/recommended
option on all four questions:
- **Order block detection: structure-break.** A swing high/low is confirmed once
  `OrderBlockPivotLookback` bars (default 2) have closed on BOTH sides without a more extreme
  high/low (a standard fractal pivot). Once a bar CLOSES beyond the most recently confirmed swing,
  that is a structure break; the last OPPOSITE-coloured candle before the break bar (scanned back
  up to 20 bars) is the order block. The broken swing is consumed (reset to unknown) so the same
  swing cannot fire twice — only a freshly confirmed one, broken again, fires again.
- **Order block invalidation: closes-through, not wick-touch, not never.** A bullish (support)
  block is removed the moment a bar CLOSES below its own bottom; a bearish (resistance) block is
  removed the moment a bar CLOSES above its own top. A wick alone does not remove it.
- **IFVG timeframe: the chart's own**, independent of the 15m/1h order blocks (which are always
  15m/1h regardless of what period the chart itself is showing).
- **IFVG display: inverted only.** A plain fair value gap (classic 3-candle imbalance: candle 1's
  high below candle 3's low, or the reverse) is tracked internally the moment it forms but drawn
  nowhere. Only once price CLOSES back through it in the direction that disproves its original
  role does it invert and become visible — a failed bullish (support) gap flips to a bearish
  inverse FVG, and the reverse. Once inverted, the SAME close-through rule chosen for order blocks
  removes it again.

**New files** (all fresh, no `OrbIx.Core` dependency):
- `OrderBlockEngine.cs` — pure logic (no platform types beyond `DateTime`/`double`), one instance
  per timeframe (15m, 1h), fed closed bars via `Feed(Bar)`. Also defines the shared `Bar` struct
  (`OpenUtc, Open, High, Low, Close`) both engines use.
- `FairValueGapEngine.cs` — pure logic, fed the chart's own closed bars.
- `StructureBoxOverlay.cs` — ONE shared renderer for both features (same visual shape: a filled,
  bordered, labelled box live since it formed, extending right — same "still extending" convention
  already used for resting-order lines) — told apart only by the colours and pre-built label text
  (`"15m OB - BULLISH"`, `"1H OB - BEARISH"`, `"IFVG - BULLISH"`) the caller supplies per box.
- `FinchLiteIndicator.cs` wiring: `TryStartOrderBlocks()` (called from `TryInitialise`, wrapped in
  its OWN try/catch per timeframe — deliberately non-fatal, since order blocks are additive on top
  of Features 1-4 and a connector that can't supply 15m/1h aggregated history for some reason must
  not take DOM/tape reading down with it); `DrainStructure()` (called from `OnPollTimer`, feeds
  newly-closed bars from both HTF series and the chart's own series into their engines, rebuilds
  the two paint drawables) — wrapped in its OWN try/catch SEPARATE from the existing DOM-pull
  block, since an exception escaping a `Timer` callback entirely is unhandled and terminates the
  whole platform process (the same reason ORB-IX's own fold is wrapped; this is the highest-risk
  addition to the poll timer so far, touching more platform history-data surface than anything
  else on it).
- Backlog safety: the 15m/1h pulls are inherently bounded by `OrderBlockLookbackDays` (default 10)
  at the `GetHistory` call itself. The chart's OWN history has no such bound — `chartBarsSeen`
  starts at `-1` and seeds itself on the first poll to `Math.Max(0, count - 500)`, capping how far
  back a long-running chart's already-loaded history gets backfilled in one burst, rather than
  walking however many bars (potentially years) the chart happens to have.
- New `InputParameter`s: `OrderBlock15mEnabled`/`OrderBlock1hEnabled` (both default true),
  `OrderBlockPivotLookback` (default 2), `OrderBlockBullishColor`/`OrderBlockBearishColor`,
  `OrderBlockLookbackDays` (default 10), `InverseFvgEnabled` (default true),
  `InverseFvgBullishColor`/`InverseFvgBearishColor` — indices 50-55 and 60-62, fresh ranges (not
  reusing the retired 16/40-44 delta-panel indices, same reasoning as every other retirement this
  week).

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors (18 pre-existing-pattern nullable-annotation warnings, one more than before from the
new `HistoricalData?` fields — same warning class as always, not new noise). Deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum` confirms
the deployed DLL matches. **NOT yet confirmed on a live chart** — this is real new algorithmic
surface (swing-pivot detection, structure breaks, 3-candle imbalance, inversion tracking) that
could not be visually verified without a live chart in this session; ask the operator to remove/
re-add Finch-Lite and confirm: 15m and 1h order-block boxes appear labelled and coloured by
direction, disappear when price closes through them, and the 1h boxes are visibly less frequent
than the 15m ones; inverse FVG boxes only appear after a gap has been closed through once (never
show a plain, not-yet-inverted gap).

**FOLLOW-UP 2026-09-23 — "this is showing but it removed my large orders on the dom".** Turned
out to be the operator's SAVED SETTINGS resetting to defaults on the fresh attach the new
`InputParameter`s required (self-confirmed: "i got it working you reset my settings thats all") —
not a code bug. Independently found and fixed a real, secondary issue while investigating though,
worth keeping regardless: order-block and inverse-FVG boxes drew LAST in `OnPaintChart`, on top of
everything else, and their boxes extend the full width out to the pane's own right edge — the
exact same screen region the DOM ladder occupies — so a wide box sitting over it could visually
wash the ladder's colours out even with settings otherwise correct. Reordered so the two structure
overlays draw FIRST, as quiet background context, with the ladder/resting-order/big-trade signals
layered on top of them — same "quiet backdrop, louder signal on top" convention already used
between the ladder and resting-order lines.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches.

**FOLLOW-UP 2026-09-23 — big-trade standing line removed, marker+label kept.** "for those large
sells i love the bubble it makes and text next to it but i dont want the line that goes all the
way through my chart" — `BigTradeOverlay` dropped the `graphics.DrawLine(...)` call that drew a
full-pane-width standing reference line at the print's price (the ORIGINAL design for this
feature, before the marker existed); the marker circle and its "BUY N"/"SELL N" label are
unchanged. The now-unused per-colour `Pen` cache that only that line used was removed too (`pens`
dictionary, `Pen()` method, its disposal).

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches.

**ROOT CAUSE FOUND AND FIXED 2026-09-23 (same day) — sub-threshold levels lingered
indefinitely, only vanishing on a refresh.** "some of the orders dont disapear or correlate
because when i refresh my screen some of these lines go away like these 6 asks and super small
orders when i have my filter set to 50" — screenshots showed "ASK 4"/"ASK 7"/"BID 9"/"BID 12"
plain (non-UA) labels well below a 50-contract filter. Root cause: a level that qualified at its
PEAK, then shrank far below the qualifying threshold, kept being tracked (and, per yesterday's
stale-peak fix, shown at its live current size) indefinitely as long as price never moved past it
— nothing in `ReconcileRestingLevels` ever re-checked a tracked level against the threshold that
let it in, only against zero (fully gone) or the price-distance rule (unfinished). A fresh
restart never showed these in the first place, since `AddNewLevels` never tracks a level under
threshold to begin with — the "refresh makes them disappear" symptom was that correct behaviour
finally getting applied, once, at the moment of restart, while the running indicator never
re-applied it continuously. Fixed by moving the price-distance ("left behind") computation
earlier in the method (before the removal-decision loop, not just before the draw loop) and
adding: a tracked level whose CURRENT size has fallen below the qualifying threshold AND has NOT
been left behind by price is now removed outright, same as a level that's gone to zero. A level
that HAS been left behind (price moved past it) is deliberately exempt from this — that is
exactly what unfinished-auction status is for, and it stays filterable separately via
`UnfinishedMinRemainingSize` regardless of how far below the general threshold it's shrunk.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — restart Quantower (not remove/re-add — see the correction note at the top of this
section) and confirm sub-threshold plain "ASK N"/"BID N" labels no longer linger; a level whose
size drops below the filter should disappear on its own, without needing a refresh, unless price
has genuinely moved past it (in which case it should read "UA - ..." instead).

**FEATURE 4 REBUILT 2026-09-23 — the delta panel is back, for a specific reason this time.**
"i really need the delta that was in that finch-scalping at the bottom of my chart so i can tell
when the delta flips" — after being fully torn out earlier this same week ("its not helpful at
all"), the panel returns because the actual need turned out narrower than "show delta": spotting
a FLIP (session cumulative delta crossing zero), the same event Finch-Scalping/ORB-IX already
mark with a full-pane vertical line (`DeltaFlipVerticalMarker`) — "when the big moves in delta
shift it sends a line straight up" was that feature's own original ask, reused here rather than
inventing a different signal.
- **The band itself** is the same single-row histogram+cumulative-line design this indicator
  settled on once before (`DeltaPanelOverlay.cs`, recreated — near-opaque 235/255-alpha
  background with a top border, both kept from the earlier "candles show through" fix).
- **New: the flip marker.** `DeltaDrawable` gained `FlipUtc`/`FlipIsUp`. `DrainDeltaTicks`'s
  `CloseBucket` tracks `deltaLastCumulativeSign` (`int?`, null until the first non-zero
  cumulative, then persistently +1/-1) and records a flip only on an actual SIGN CHANGE — an
  exactly-zero bar in between two same-signed bars is neither a flip nor a reset of the tracked
  sign. `DeltaPanelOverlay.Draw` draws the newest flip as a full-PANE-HEIGHT dashed vertical line
  (not just band-height — matches "sends a line straight up" through the price action, not just
  the delta strip), labelled "FLIP UP"/"FLIP DOWN", gated on new `DeltaFlipMarkerEnabled`
  (default true).
- **Non-fatal startup this time**: the original delta panel gated the chart's own bar-period
  resolution inside `TryInitialise`, meaning a slow-to-publish period could hold up Features 1-3
  too. Rebuilt to resolve `deltaBarPeriod` LAZILY inside `DrainDeltaTicks` itself (checked once
  per poll, cheap when already known) — same "additive, never fatal" discipline order blocks
  established this same week. `DrainDeltaTicks` also gets its own try/catch in `OnPollTimer`,
  separate from the DOM-pull block, matching every other addition to that timer since the
  Timer-callback-exception lesson.
- New `InputParameter`s: `DeltaPanelEnabled`/`DeltaPanelHeightPx`/`DeltaUpColor`/
  `DeltaDownColor`/`DeltaFlipMarkerEnabled` — indices 70-74, a fresh range (not reusing the
  retired 40-44 from the first delta panel's own indices).

**Also caught and fixed while wiring this in**: a real duplication bug from the 2026-09-23
draw-order fix above (order blocks/IFVG were meant to MOVE to draw first, but the old call site
at the end of `OnPaintChart` was never actually removed) — both overlays were drawing TWICE every
frame, harmlessly visually (same drawable, same position) but wastefully, and consuming the
shared label registry twice over for no reason. Removed the stale duplicate block.

**Files touched**: `DeltaPanelOverlay.cs` (recreated, now with the flip marker),
`FinchLiteIndicator.cs` (all fields/parameters/wiring above, plus the duplicate-draw fix).

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors, 0 warnings. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — restart Quantower (not remove/re-add) and confirm: the delta band appears at the
bottom again; a full-height dashed line appears the moment session cumulative delta crosses zero,
labelled FLIP UP/FLIP DOWN; and Features 1-3 still work exactly as before (the whole point of the
lazy, non-fatal period resolution this time).

**ROOT CAUSE FOUND AND FIXED 2026-09-23 (same day) — the delta panel showed one static bar
instead of one per candle.** "i need it to continue with the delta for each candle not just
static on 1 candle like this ... this was working perfect in the finch-scalping". Root cause:
the rebuilt panel closed a delta bar only when a NEW classified tick arrived with a different
bucket time than whatever bucket was currently open — so any candle that happened to receive
ZERO classified prints got no bar at all, not even a zero one. On this connector that is common,
not rare: `TryClassify`'s own doc comment already documents this exact feed's aggressor flag as
sparse, which is why the geometry fallback exists in the first place — a quiet MGC candle with no
cleanly classified prints simply vanished from the panel instead of showing zero, leaving long
gaps that read as "frozen on one bar."

**Fix — delta bars are now driven by the CHART's own bar closes, not by tick arrival**, reusing
the exact same `TryReadBar`/`SeekOriginHistory.Begin` reading `DrainStructure` already uses for
IFVG detection. Classified ticks accumulate into a new `deltaPending`
(`Dictionary<DateTime, (double Buy, double Sell)>`, keyed by which bar they belong to) purely as
bookkeeping; `DrainDeltaTicks` now walks the chart's own newly-closed bars (`deltaChartBarsSeen`
cursor, same backlog-capping pattern as `chartBarsSeen` for IFVG) and closes exactly one delta
bar per real candle, looking up (or defaulting to zero for) whatever accumulated for that bar's
own `OpenUtc` — a quiet candle now gets a proper zero-delta bar instead of being skipped, keeping
the panel in lock-step with the candles the way the old Finch-Scalping/ORB-IX version always was.
The old tick-bucket fields (`deltaBucketOpen`/`deltaBucketOpenUtc`/`deltaBucketBuy`/
`deltaBucketSell`) are gone; flip detection (`deltaLastCumulativeSign`/`deltaFlipUtc`, added
earlier this same day) is unchanged in logic, just triggered from `CloseBar` now instead of
`CloseBucket`.

**Also addressed**: "stop with this lint noise it doesnt make sense" — the markdown-lint
diagnostics (MD022/MD031/MD032) that show up in this tool's own output after editing this file
are pre-existing formatting nitpicks (headings/lists/fenced blocks without surrounding blank
lines) scattered throughout this document from long before this session, unrelated to any code
correctness. Not flagging them in responses going forward.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors (one intermediate error caught and fixed: C# drops named-tuple element names in a
ternary against a bare `(0d, 0d)` literal unless the target variable is explicitly typed with
those names — `(double Buy, double Sell) acc = ... ? existing : (0d, 0d);` rather than `var`).
Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`,
`sha256sum` confirms the deployed DLL matches. Not yet confirmed on a live chart — restart
Quantower and confirm the delta band now shows a bar for every visible candle, including quiet
ones, with no gaps.

**FOLLOW-UP 2026-09-23 (same day) — delta panel split back into three rows.** "now i need to see
the session delta and delta and volume like in the finch-scalping" — the SAME three-row design
this indicator built once before this same week (VOLUME / DELTA / SESSION DELTA, each its own
row and scale), abandoned partway through in favour of a single-band ORB-IX-ported design, is
back — named directly, so no design ambiguity this time. `DeltaBarDraw` gained back its `Volume`
field (`buy + sell` per bar, computed in both `CloseBar` and the still-forming-bar path inside
`DrainDeltaTicks` — unaffected by the chart-bar-driven rewrite earlier today, since that changed
WHEN a bar closes, not what data it carries). New `DeltaVolumeColor` `InputParameter` (index 75,
default cornflower blue — volume has no direction, so one neutral colour). `DeltaPanelHeightPx`
default raised 110 → 150 (min 40 → 60) — three rows split three ways needs more total height to
stay legible, same reasoning as the first time this split existed. The flip marker added earlier
today (full-pane vertical line at a session cumulative delta sign change) is UNCHANGED and drawn
independently of the row layout — it already spanned the whole pane, not just the band, so the
row split underneath it doesn't affect it.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — restart Quantower and confirm three labelled rows (VOL/DELTA/SESSION DELTA) each
show their own bar for every candle, and the flip marker still spans the full pane height.

**FOLLOW-UP 2026-09-23 (same day) — session delta row switched from a line to bars.** "session
delta should also be in bars not a white line" — the SESSION DELTA row now draws one bar per
candle from a zero baseline (clamped within the row if zero falls outside that session's own
min/max range), coloured up/down by the CURRENT sign of cumulative delta at that bar — same
visual language as the DELTA row above it, just plotted against the running-total's own
[min, max] scale instead of a symmetric one. The now-unused `cumPen` (Gainsboro line pen) was
removed entirely.

**Verified 2026-09-23**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches.

**FOLLOW-UP 2026-09-25 — `RestingOrderEngine` extracted, in support of a new Strategy (see
Strategy Catalog below).** "i wanna make this into a strategy now that i can run in quantower
were it plays off the orders from the dom and the ifvg and absorption levels and unfinished
auctions" — the DOM/absorption/unfinished-auction reconciliation logic
(`FinchLiteIndicator.ReconcileRestingLevels`, this indicator's most mature, most-iterated
feature) was pulled out into a standalone `internal sealed class RestingOrderEngine`
(`Indicators/Finch-Lite/src/Finch.Lite.Indicator/RestingOrderEngine.cs`), so a new order-placing
Strategy can trade off the EXACT SAME detection the indicator draws instead of a second,
independently-maintained copy that could drift. `midPrice`/`distanceThresholdPrice`/`dayStart`
are now parameters to `Reconcile(...)` rather than read off `this.symbol`/instance fields — this
is what keeps the engine free of any platform `Symbol`/`Indicator` dependency and usable from a
`Strategy` too. Also split the shared `Bar` record out of `OrderBlockEngine.cs` into its own
`Bar.cs`, so the new Strategy can compile in `FairValueGapEngine.cs` without also pulling in the
(irrelevant to it) order-block engine. **Pure refactor — zero display/behavior change intended**
to what this indicator already showed; `FinchLiteIndicator.cs` itself now just owns a
`private readonly RestingOrderEngine restingOrderEngine = new();` and a thin call site mapping
`RestingOrderEngine.RestingLevel` → the paint drawable.

**Verified 2026-09-25**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors, 0 warnings. Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — restart Quantower and confirm large orders/absorption tiers/unfinished auctions
still look and behave exactly as before this refactor (highest-risk step of this change, since
it touches the most-iterated code in the project).

**FEATURE 7 (2026-09-25) — point of control (POC) of the current move, plus a 15-minute
higher-timeframe POC.** "what would be very nice to have in this indicator is the poc of the
current move and a higher time frame poc of like the 15min." Four design questions resolved via
`AskUserQuestion` before building (guessing wrong on "current move" risked a wasted rebuild, same
reasoning as Features 5+6):
- **"Current move"** = the leg since the most recently CONFIRMED swing pivot (high or low), using
  the exact same fractal pivot-confirmation rule `OrderBlockEngine` already uses — every new
  swing resets the accumulator, so the POC always describes "since the market last turned," not a
  fixed lookback.
- **15m POC** = the same swing-leg concept, but detected on a DEDICATED 15-minute series
  (independent of the existing 15m order blocks — disabling one must not starve the other of
  history), giving a broader, more significant level than the chart-timeframe one.
- **Volume source** = tick-by-tick, reusing the exact same live trade stream already driving the
  delta panel and big-trade markers — POC just doesn't care which side was the aggressor, unlike
  delta.
- **Display** = a single dashed reference line per POC, labeled with its price — deliberately NOT
  a full profile histogram (that was offered as an option and declined).

**New files**: `PocEngine.cs` (one instance per timeframe; owns its own swing-pivot detection and
a `Dictionary<double, double>` volume-by-price accumulator that clears on every new confirmed
swing) and `PocOverlay.cs` (dashed line + label, same coordinate-conversion/label-collision
helpers every other overlay in this file already uses). **Stated design limitation, not an
oversight**: a swing pivot is only CONFIRMED `PocSwingPivotLookback` bars after it actually
happened, and Finch-Lite has no historical tick backfill (see Feature 4's own delta-panel doc
comment above) — so the profile starts accumulating live from the CONFIRMATION moment forward,
not retroactively from the pivot bar's own timestamp.

New `InputParameter`s (indices 80-85): `PocCurrentMoveEnabled`/`Poc15mEnabled` (both default on),
`PocSwingPivotLookback` (default 3, shared by both engines), `PocCurrentMoveColor`/`Poc15mColor`
(gold / light-blue defaults), `PocLookbackDays` (default 5 — only needs enough 15m history to
locate the current swing, since the profile itself never backfills volume regardless of how far
back this reaches). The current-move engine reads the CHART's own bars via its OWN cursor
(`pocChartBarsSeen`), deliberately NOT sharing `chartBarsSeen` with the IFVG engine — that cursor
stops advancing entirely whenever Inverse FVG is switched off, which must not also silently stall
POC.

**Verified 2026-09-25**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors, 27 warnings (all pre-existing nullable-annotation-context style warnings, none new
from this feature). Deployed to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\
FinchLiteIndicator.dll`, `sha256sum` confirms the deployed DLL matches. Not yet confirmed on a
live chart — restart Quantower and confirm two dashed reference lines appear once a swing has
confirmed on each timeframe, each labeled with its own price, each extending from its own move's
start rather than the whole pane.

**FOLLOW-UP 2026-09-28 — 5-minute POC added, mirroring the strategy's own same-day addition.**
"add the 5min poc to my indicator also," directly following `finchDomScalpStrategy` growing a 5m
POC pathway the same day (a middle ground between the fast current-move POC and the slow 15m one —
see that strategy's own Catalog entry below). New `Poc5mEnabled` (default on, index 86) and
`Poc5mColor` (default a magenta/purple, index 87) mirror the 15m POC's own shape exactly — own
dedicated 5-minute `HistoricalData`, own `PocEngine` instance, own bar-drain cursor
(`poc5mBarsSeen`), sharing `PocSwingPivotLookback`/`PocLookbackDays` with the 15m one (renamed in
its own doc comment from "15m history lookback" to "5m/15m history lookback" to reflect that).

**One structural change this required**: `PocDraw`'s `IsHigherTimeframe` bool (current-move vs.
15m only) couldn't represent a third category cleanly, so `PocOverlay.cs` changed it to a
`string Label` ("current-move"/"5m"/"15m"), used for both colour selection and the label text
(`PocOverlay.Options` grew a third colour, `Poc5mColor`, alongside `CurrentMoveColor`/`Poc15mColor`).

**Verified 2026-09-28**: `dotnet build ... -c Release -p:QuantowerSdkPath="...v1.147.4\bin\..."`
— 0 errors, 29 warnings (all pre-existing nullable-annotation-context style, none new). Deployed
to `C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\FinchLiteIndicator.dll`, `sha256sum`
confirms the deployed DLL matches. Not yet confirmed on a live chart — restart Quantower and
confirm a THIRD dashed reference line (5m, its own colour) appears alongside the existing
current-move/15m ones once a swing confirms on that timeframe.

### Order-Flow Scalping Setup (`Indicators/order-flow-scalping/`) — added 2026-09-14
**Not a project** - a configuration/diagnosis document for getting `ORB-IX` (above) to show
delta, DOM/resting orders, absorption, auto-drawn fib, and FRVP+AVP (higher/lower timeframe
POC) all together, matching a working reference screenshot. Documents a CONFIRMED
platform/data-vendor refusal (Quantower's own toast: "Volume analysis calculation from ticks
history is not allowed for one data vendor") that explains historical-backfill gaps in
footprint/absorption data on some connections but not others - see that folder's README for
the full finding and the exact `InputParameter` names to enable each requested display.

### Ocean's Stack Indicator (`Indicators/OceansStack/`) — added 2026-09-30

**Files:** `Indicators/OceansStack/src/OceansStack.Indicator/OceansStack.Indicator.csproj`,
`OceansStackIndicator.cs`, `ValueAreaLineOverlay.cs`, `ZoneBoxOverlay.cs`, `FuelTargetOverlay.cs`,
`AbsorptionMarkerOverlay.cs`, `SweepSignalOverlay.cs`, `SETTINGS.md`.

The visual companion to `oceansStackStrategy` (below) — "that oceans strategy we created i want
this .pine indicator turned into a indicator in quatower," confirmed via `AskUserQuestion` as a
**full visual port** (every element `ocean.pine` draws, not a stripped-down subset): daily/weekly
value-area lines, zone boxes with live status text, fuel-target labels, absorption dot markers,
sweep "SF" signals, and QQQ comparison lines. Same relationship Finch-Lite already has to
`finchDomScalpStrategy` — the operator can see the SAME state the strategy trades off of.

**Engine sharing, reversed direction from the Finch-Lite precedent**: `finchDomScalpStrategy`
reaches INTO Finch-Lite's own folder for shared engines (the strategy is the newer, narrower
consumer there). Here, `oceansStackStrategy` is the OLDER, foundational side — it owns the five
detection engines (`ValueAreaEngine`/`SessionPoolTracker`/`AbsorptionTracker`/`FuelPoolSelector`/
`SweepZoneTracker`) plus its own `Bar`/`DeltaTracker` — so this new, narrower INDICATOR reaches
into the STRATEGY's folder instead, via the same `<Compile Include>`/frozen-snapshot trade-off
already established everywhere else in this codebase. No edits needed to any of the six files
reached into.

**Orchestration is a deliberate, hand-synced duplicate**: `ProcessBar`/`EvaluateZone`/`PollQqq`/
`EvaluateQqq` in `OceansStackIndicator.cs` re-derive the SAME score/armed/fuel/sweep logic
`oceansStackStrategy.cs` already computes, rather than a new three-way-shared file (same
precedent as `finchDomScalpStrategy`'s own independent re-derivation from `PocEngine`). A future
scoring/arming rule change must be applied to BOTH files by hand — verify by running both side by
side and diffing the indicator's zone status text against the strategy's own `[Heartbeat]`/
`[Signal skipped]`/`[Signal]` log lines.

**Two deliberate scope trims**, documented in the indicator's own class doc comment: no Pine
"Dalton open type" classification (never ported into the strategy in the first place — visualizing
unported Pine logic would mean inventing new, unreviewed detection code); and retained-day history
(`Keep prior days on chart`) covers only the daily VAH/VAL/POC lines, not multi-day pool/absorption
history.

**No `Symbol` InputParameter** — unlike the strategy, this indicator uses the CHART's own attached
symbol (`this.Symbol`, inherited from the platform's `Indicator` base class) and pulls its own
dedicated 1-minute series from it via `symbol.GetHistory(Period.MIN1, ...)`, same pattern
Finch-Lite already uses for its own 5m/15m POC series — independent of whatever period the chart
itself displays. No configurable `Period` either, since every engine here assumes 1-minute bars.

**QQQ comparison lines are the single highest-risk piece**: no indicator anywhere in this codebase
has ever pulled a SECOND symbol's history before, and it is unconfirmed that Quantower's
`Indicator` base class resolves an `InputParameter Symbol` to live market data the same way
`Strategy` does. Defaults OFF here (unlike the strategy, which defaults it on) — see the
indicator's own `SETTINGS.md` for the isolated-verification steps required before trusting it.

**Verified 2026-09-30**: `dotnet build src/OceansStack.Indicator/OceansStack.Indicator.csproj -c
Release -p:Share=true -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\v1.147.5\bin\
TradingPlatform.BusinessLayer.dll"` — 0 errors, 18 warnings (all pre-existing nullable-annotation-
context style, matching every other project in this codebase). **NOT YET deployed or confirmed on
a live chart** — copy the built DLL to
`C:\Quantower\Settings\Scripts\Indicators\OceansStack\OceansStackIndicator.dll` (close Quantower
first), then work through `SETTINGS.md`'s own verification order (value-area lines alone, then
zone boxes, then absorption markers, then sweep signals, then QQQ in isolation).

---

### ORB + Session Levels (`Indicators/OrbLevels/`) — added 2026-10-06

**Files:** `Indicators/OrbLevels/src/OrbLevels.Indicator/OrbLevels.Indicator.csproj`,
`OrbLevelsIndicator.cs`, `OrbBoxOverlay.cs`, `LevelLineOverlay.cs`.

The operator: "can you make me an indicator that works for painting out the orb for mes and mgc
and marks out the highs and lows like the strategy does" — a visual companion to both
`mesOrbStrategy` and `goldOrbStrategy`. Paints the ORB box (high/low/midpoint, 8:00-8:15 AM ET
default — switch to 20:00-20:05 for a gold chart) and the Asia/London/NY untested session
highs/lows exactly as `mesOrbStrategy`'s own SESSION LEVELS setup tracks them: frozen the moment
each session ends, solid/bright while untested, dim/dotted with a "(tested)" label suffix once a
bar touches one. Works unmodified on MES, MGC, or anything else — every session window and the
ORB window itself are plain InputParameters, and nothing reads a hardcoded tick size or contract
spec.

**Not a shared-code port of either strategy** — an indicator can't inherit `Strategy`, and the
strategies' own ORB/session-level logic lives as private methods on their own classes, not
standalone files `<Compile Include>` could pull in. This is a deliberate, hand-synced DUPLICATE
of the box-building and session-freeze-and-touch logic, stripped of everything entry/stop/target/
risk related (an indicator draws, it never places an order or reads the account) — the same
accepted tradeoff already documented for the `OceansStack` indicator above: a future tuning
change to either strategy's own ORB/session logic does not reach this indicator until hand-
applied here too.

**Scaffold and overlay patterns copied from Finch-Lite**, not reinvented: the `OnInit`/
`TryInitialise`/`OnRetryTimer` lifecycle is identical (minus Finch-Lite's own DepthOfMarket
precondition — this indicator never touches the DOM, only historical bars, so the only
precondition is `this.Symbol` being attached). `OrbBoxOverlay` is `StructureBoxOverlay.cs`
adapted with one real difference: the box itself has a FIXED end time (an ORB window is a
specific, closed range, not a "still live" zone) — the high/low/midpoint then extend onward as
separate rays once the window closes, which is the part the strategy actually trades off for the
rest of the day. `LevelLineOverlay` is `PocOverlay.cs` adapted to carry an `Untested` flag per
line, driving both line style and the label text.

**Startup reconstruction reuses the live per-bar method directly**, same pattern as
`mesOrbStrategy`'s own `ReconstructSessionLevels`: `ReplayHistory` replays the whole fetched
backlog through `ProcessBar` once on attach, so a mid-day attach shows today's (and
`KeepPriorDays` prior) already-known levels immediately rather than waiting to rebuild them live.

**Verified 2026-10-06**: `dotnet build src/OrbLevels.Indicator/OrbLevels.Indicator.csproj -c
Release -p:Share=true -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\v1.147.5\bin\
TradingPlatform.BusinessLayer.dll"` — 0 errors, 7 warnings (all pre-existing nullable-annotation-
context style). Deployed to `C:\Quantower\Settings\Scripts\Indicators\OrbLevels\
OrbLevelsIndicator.dll`.

#### FIX, same day — no ORB box drawn when attached after the window already closed

The operator, after attaching mid-day: "i had this running why did it not have a orb box drawn it
should look at historical bars to build it if it started after the time frame." Exact same root
cause as `mesOrbStrategy`'s own same-day fix (see its own entry above): `TryInitialise` called
`symbol.GetHistory(...)` then immediately called `ReplayHistory()` synchronously — but
`GetHistory()` can return a handle that's still populating its own backlog in the background,
so reading `.Count` right away can see it empty or near-empty well before the real data arrives.
Replaying an empty backlog reconstructs nothing (no box, no session levels) and, unlike the live
poll path, nothing ever re-triggers a second replay — so it just sits blank.

**Fixed differently from the strategy's own fix, and more cleanly**: the strategy has no retry
mechanism (`OnRun` runs once), so it blocks briefly instead. This indicator already has a retry
timer built for exactly this "dependency not ready yet" shape (`OnRetryTimer`, originally for
Finch-Lite's own DepthOfMarket precondition). Reused it instead of blocking: if `history.Count <
100` right after fetch, the attempt is disposed and `TryInitialise` returns `false` — the existing
retry timer calls it again a second later, which re-fetches and re-checks from scratch, same as
any other not-ready-yet precondition.

**Verified 2026-10-06**: rebuilt (0 errors) and redeployed to the same path. **Caveat**: per
`ORB-IX/BUILD.md`'s own documented deploy step, Quantower loads scripts at startup and keeps what
it loaded — copying a new DLL over a running platform can appear to succeed while changing
nothing. If the fix doesn't show up, remove and re-attach the indicator (or restart Quantower)
rather than assuming the fix didn't work.

---

## Strategy Catalog

### 0. EMA Cross Strategy (`emaCrossStrategy`)
**File:** `Strategies/emaCrossStrategy/emaCrossStrategy/emaCrossStrategy.cs`
**Build output:** `C:\Quantower\Settings\Scripts\Strategies\emaCrossStrategy\emaCrossStrategy.dll`

#### Core Logic
- Enters on Micro (5) / Mid (29) EMA crossovers on any chosen timeframe
- All signals fire on **bar close only** — no tick-chasing on entries
- Exits via weakness bars (EMA gap shrinking), trailing stop, or reverse cross
- Reverse cross always closes current position and immediately opens the opposite direction
- Includes impulse candle filter, Mid EMA retracement entry, HTF EMA touch re-entry, partial close with automatic remainder SL, and session-aware trailing stops

#### Entry Flow (bar close)
1. **Impulse deferred bar check** (`pendingConfirmSide`) — if previous bar was an impulse cross, re-check EMA alignment; enter if still holds, or enter reversal if a clean opposite cross fired (liquidity sweep)
2. **Mid EMA retracement watch** (`retraceWatchSide`) — if an impulse cross set a retracement watch, monitor price coming within `RetraceTouchTicks` of the base Mid EMA, then enter on the first bar bouncing away in trend direction
3. **HTF EMA touch re-entry** (`lastExitSide` + `htfTouchArmed`) — after a non-reverse exit, watch for price to touch the auto-derived HTF Mid EMA and bounce back; auto-derived period: 1m→3m, 3m→5m, 5m→15m, 15m→1h, 1h→4h
4. **Fresh cross** — bullish or bearish EMA cross fires; if candle body ≥ `ImpulseFilterTicks`: set `retraceWatchSide` (if `RetraceTouchTicks > 0`) or `pendingConfirmSide` (1-bar fallback); else enter immediately

#### Exit Flow (bar close, position open)
- **Reverse cross** → close all, set `pendingEntrySide`, re-open opposite in `Core_PositionRemoved`
- **Weakness bars** (ExitMode 0 or 2) → `WeaknessBars` consecutive bars of shrinking EMA gap
  - If `WeaknessClosePercent` is 1–99: partial close that %, set `weaknessPartialPrice` as remainder SL
  - If 0 or 100: close entire position
- **Trailing stop** (ExitMode 1 or 2, tick-level in `Hdm_HistoryItemUpdated`)
  - `GetActiveTrailSettings()` picks Asia / NY / Off-Hours values by EST hour
  - Activates once P&L ≥ activation ticks; closes if pullback from `bestPrice` > trail ticks
- **Weakness partial SL** (tick-level) — if `weaknessPartialPrice > 0` and price returns to that level, close remainder

#### Key Private Fields
| Field | Purpose |
|---|---|
| `microEma` / `midEma` | Base-TF EMA indicators |
| `htfMidEma` / `htfHdm` / `htfPeriod` | Higher-TF Mid EMA for re-entry |
| `pendingEntrySide` | Queued reverse-cross flip (fires in PositionRemoved) |
| `pendingConfirmSide` | 1-bar impulse confirmation |
| `retraceWatchSide` / `retraceTouchArmed` | Mid EMA retracement entry state |
| `lastExitSide` / `htfTouchArmed` | HTF EMA re-entry state |
| `weaknessPartialDone` / `weaknessPartialPrice` | Partial close state and remainder SL |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state |

#### Parameters (InputParameter index order)
| # | Name | Default | Notes |
|---|---|---|---|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Micro EMA | 5 | Fast EMA period |
| 3 | Mid EMA | 29 | Slow EMA period |
| 4 | Weakness Bars | 2 | Consecutive narrowing bars to trigger exit |
| 5 | Period | MIN1 | Chart timeframe |
| 6 | Start Point | 30 days ago | Historical data start |
| 7 | Quantity | 1 | Contracts per trade |
| 8 | Stop Loss (ticks) | 100 | Hard backstop SL |
| 9 | Take Profit (ticks) | 0 | Fixed TP; 0 = disabled |
| 10 | Off-Hours Trail Activation | 30 | Ticks profit to start trailing outside Asia+NY |
| 11 | Off-Hours Trailing Stop | 15 | Ticks from peak before close outside Asia+NY |
| 12 | Exit Mode | 2 | 0=WeaknessBars, 1=TrailingStop, 2=Both |
| 13 | Impulse Filter (ticks) | 20 | Body size threshold to defer impulse entries |
| 14 | HTF EMA Touch (ticks) | 5 | Proximity to HTF Mid EMA to arm re-entry; 0=off |
| 15 | Weakness Close % | 50 | % to close on weakness bar; 0/100=close all |
| 16 | Asia Session Start (EST hour) | 19 | 7 PM EST |
| 17 | Asia Session End (EST hour) | 3 | 3 AM EST (wraps midnight) |
| 18 | Asia Trail Activation (ticks) | 20 | 0 = disable Asia override |
| 19 | Asia Trailing Stop (ticks) | 10 | 0 = disable Asia override |
| 20 | NY Session Start (EST hour) | 8 | 8 AM EST |
| 21 | NY Session End (EST hour) | 16 | 4 PM EST |
| 22 | NY Trail Activation (ticks) | 50 | 0 = disable NY override |
| 23 | NY Trailing Stop (ticks) | 25 | 0 = disable NY override |
| 24 | Retrace Touch (ticks) | 5 | Proximity to Mid EMA to arm post-impulse entry; 0=1-bar confirm |
| 25 | Micro EMA Pullback Touch (ticks) | 0 | Proximity to Micro EMA to arm re-entry on normal crosses; 0=off |

#### API / Platform Notes
- `[InputParameter]` only renders `int`, `double`, `bool`, `string`, `Period`, `Symbol`, `Account`, `DateTime` in Quantower UI — enums are invisible; use `int` with comment
- `GetValue(1)` = last closed bar (safe for signals); `GetValue(0)` = forming bar (tick use only)
- `TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")` used for DST-aware EST conversion
- `DeriveHtfPeriod()` uses both constant equality AND string fallbacks (`"3 Min"`, `"MIN3"`, `"3m"` etc.) because Quantower constructs non-standard periods differently depending on UI source

---

### 1. Futures Pro Strategy (`futuresProStrategy`)
**File:** `Strategies/futuresProStrategy/futuresProStrategy/futuresProStrategy.cs`
**Build output:** `C:\Quantower\Settings\Scripts\Strategies\futuresProStrategy\futuresProStrategy.dll`
**Readme:** `Strategies/futuresProStrategy/readme.md`

#### Core Logic
- Multi-factor trend-following for MES / ES / NQ on any chosen timeframe (default 5m)
- Entry requires a fresh 9/21 EMA cross on bar close — no mid-bar signals
- Five stacked filters must all pass: EMA cross + 200 EMA trend direction + RSI + MACD (optional) + RTH session gate (optional)
- Reverse cross closes current trade and re-enters opposite direction only if trend filter AND all entry filters agree; otherwise goes flat
- Optional mean reversion modes: **bounce** (arm near 200 EMA, enter on first cross back in trend direction) and **fade** (arm when overextended, enter counter-trend targeting 200 EMA as TP)
- Real-time trailing stop (tick level), daily loss cap (includes unrealized P&L), and total drawdown ceiling (prop firm safe)

#### Entry Flow (bar close)
1. Check daily/drawdown limits — halt if either hit
2. If in position: check for reverse cross; close and optionally queue flip
3. If flat: require `bullishCross` or `bearishCross` (one-bar strict crossover)
4. If `MeanRevMode` bounce armed: enter on matching cross (skips trend filter — price was just AT the trend EMA)
5. If `MeanRevMode` fade armed: enter counter-trend cross when overextended
6. Standard entries: run all 4 filters (trend, RSI, MACD, RTH) before placing

#### Exit Flow
- **Reverse cross** → close, queue flip, re-open in `Core_PositionRemoved` if all filters agree
- **Trailing stop** (tick-level in `Hdm_HistoryItemUpdated`) → activates at `TrailActivationTicks` profit; closes on pullback > `TrailingStopTicks` from peak
- **Hard SL / TP** → bracket order placed at entry
- **Daily loss / drawdown** → real-time check with unrealized P&L included; closes immediately when threshold hit

#### Key Private Fields
| Field | Purpose |
|---|---|
| `fastEma` / `slowEma` / `trendEma` | EMA indicators for cross and trend filter |
| `rsi` / `macd` | RSI and MACD indicators |
| `pendingEntrySide` | Queued reverse-cross flip (fires in `Core_PositionRemoved`) |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state |
| `meanRevBounceArmed` | Side direction armed for mean rev bounce entry |
| `dailyPnl` / `dailyLimitHit` / `lastResetDay` | Daily loss cap state (resets at 6 PM EST) |
| `totalRealizedPnl` / `peakEquity` / `drawdownLimitHit` | Max drawdown tracking |

#### Parameters (InputParameter index order)
| # | Name | Default | Notes |
|---|---|---|---|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Fast EMA | 9 | Fast EMA period |
| 3 | Slow EMA | 21 | Slow EMA period |
| 4 | Trend EMA Period | 200 | Direction filter; entries blocked against it |
| 5 | RSI Period | 14 | RSI lookback |
| 6 | RSI Overbought | 70 | Blocks long if RSI ≥ value |
| 7 | RSI Oversold | 30 | Blocks short if RSI ≤ value |
| 8 | MACD Filter | 0 | 0=off, 1=histogram must agree |
| 9 | MACD Fast Period | 12 | — |
| 10 | MACD Slow Period | 26 | — |
| 11 | MACD Signal Period | 9 | — |
| 12 | Period | MIN5 | Chart timeframe |
| 13 | Start Point | -30 days | Historical data start |
| 14 | Quantity | 1 | Contracts per trade |
| 15 | Stop Loss (ticks) | 80 | Hard bracket SL |
| 16 | Take Profit (ticks) | 0 | 0=disabled; use trailing instead |
| 17 | Trail Activate At (ticks) | 40 | Profit to arm trailing; 0=off |
| 18 | Trailing Stop (ticks) | 20 | Pullback from peak to close; 0=off |
| 19 | RTH Only | 0 | 0=24h, 1=block entries outside RTH |
| 20 | RTH Start Hour (EST) | 9 | — |
| 21 | RTH End Hour (EST) | 16 | — |
| 22 | Max Daily Loss ($) | 0 | 0=disabled; resets at 6 PM EST |
| 23 | Max Drawdown ($) | 2000 | Total equity ceiling since strategy start; 0=off |
| 24 | Max Trend EMA Distance (ticks) | 0 | Blocks entries if price too far from 200 EMA; 0=off |
| 25 | MeanRev Mode | 0 | 0=off, 1=bounce, 2=fade, 3=both |
| 26 | MeanRev Bounce Arm (ticks) | 20 | Distance to 200 EMA to arm bounce |
| 27 | MeanRev Fade Arm (ticks) | 60 | Distance from 200 EMA to arm fade |

#### API / Platform Notes
- RTH gate uses `DateTimeUtcNow` (live clock), not bar timestamp — RTH filtering is inaccurate in backtest/replay mode
- `GetValue(1)` used for all signal reads (last closed bar); `GetValue(0)` only safe in tick handler
- `MaxDrawdown` never resets; restart strategy instance to clear it
- Reverse-cross flip is gated: position closes regardless, but re-entry only fires if trend + all filters agree for the new side

---

### 2. Box Range Strategy (`boxRangeStrategy`)
**File:** `Strategies/boxRangeStrategy/boxRangeStrategy/boxRangeStrategy.cs`

#### Core Logic
- Identifies high and low price ranges over a lookback period
- Places limit/stop orders at range boundaries
- Uses configurable range offset for entry precision
- Supports both full range and half-range trading modes

#### Key Implementation Details
- **Range Calculation:** Uses arrays of historical highs/lows from bars 2-11
- **Entry Conditions:** Waits for price to be inside range before placing orders
- **Order Placement:** Bracket orders with calculated take profit and stop loss
- **Range Detection:** `insideRange` flag based on previous bar staying within boundaries

#### Parameters
- `rangeOffsetTicks`: Buffer distance from range boundaries
- `halfRange`: Enables trading at mid-range levels
- `stopOrders`: Toggle between limit and stop order types
- `updateCounter`: Warmup period before range calculation

---

### 3. Price Surge Strategy (`priceSurgeStrategy`) 
**File:** `Strategies/priceSurgeStrategy/priceSurgeStrategy/priceSurgeStrategy.cs`

#### Core Logic
- Detects sudden price movements exceeding normal volatility
- Uses multiplicative threshold to identify surge conditions
- Implements trailing stop loss for profit protection
- Tracks previous trade direction to avoid conflicting signals

#### Key Implementation Details
- **Surge Detection:** Compares current move to lookback range average
- **Entry Timing:** Uses `newBar` flag to enter on bar close
- **Position Management:** Single position tracking with `inPosition` flag
- **Risk Management:** Dynamic trailing stop based on `trailingStop` parameter

#### Parameters
- `multiplicative`: Surge threshold multiplier (default 1.15 = 15% above average)
- `trailingStop`: Distance in ticks for trailing stop
- `lookbackRange`: Historical bars for surge calculation (1-10)

---

### 4. Range Scalp Strategy (`rangeScalpStrategy`)
**File:** `Strategies/rangeScalpStrategy/rangeScalpStrategy/rangeScalpStrategy.cs`

#### Core Logic
- Quick scalping within identified price ranges
- Fixed take profit and stop loss for rapid trades
- Similar range detection to Box Range but optimized for scalping
- Uses smaller profit targets and tighter risk management

#### Key Implementation Details
- **Range Logic:** Inherited from Box Range Strategy
- **Scalping Focus:** Fixed 5 tick TP, 10 tick SL by default
- **Speed Optimization:** Simpler logic for faster execution
- **Risk Control:** Lower max profit/loss thresholds (350/250)

#### Parameters
- `takeProfit`: Fixed profit target in ticks
- `stopLoss`: Fixed stop loss in ticks
- `rangeOffsetTicks`: Entry adjustment from range boundaries

---

### 5. SMA Cross Strategy (`smaCrossStrategy`)
**File:** `Strategies/smaCrossStrategy/smaCrossStrategy/smaCrossStrategy.cs`

#### Core Logic
- Classic moving average crossover system
- Fast SMA crossing above/below slow SMA generates signals
- Includes trailing stop loss and profit threshold features
- Position tracking prevents multiple entries in same direction

#### Key Implementation Details
- **Indicators:** Built-in SMA indicators for fast/slow periods
- **Signal Generation:** Crossover detection with previous bar comparison
- **Risk Management:** Combined fixed and trailing stop system
- **Entry Logic:** `prevSide` tracking prevents overtrading

#### Parameters
- `FastMA`: Fast moving average period (1-100, default 10)
- `SlowMA`: Slow moving average period (1-100, default 20)
- `stoploss`: Fixed stop loss in ticks
- `trailingStop`: Trailing stop distance
- `profitThreshold`: Profit level to activate trailing stop

---

### 6. Price Slope Change Strategy (`priceSlopeChangeStrategy`)
**File:** `Strategies/smaSlopeChangeStrategy/priceSlopeChangeStrategy/priceSlopeChangeStrategy.cs`

#### Core Logic
- Monitors slope changes in moving averages
- Detects directional changes in price momentum
- Uses dual SMA system for lead/lag comparison
- Tracks slope change magnitude for entry signals

#### Key Implementation Details
- **Slope Calculation:** `baseSlopeChange` vs `baseSlopeChangePrev`
- **Signal Logic:** Slope direction reversals trigger entries
- **Counters:** `buyCounter`/`sellCounter` for signal strength
- **Cross Tracking:** `lastCross` prevents immediate reversals

#### Parameters
- `leadValue`: Lead SMA period for slope calculation
- `baseValue`: Base SMA period for slope reference
- **Note:** Both default to 20, creating sensitive slope detection

---

### 7. Weighted Surge Strategy (`weightedSurgeStrategy`)
**File:** `Strategies/weightedSurgeStrategy/weightedSurgeStrategy/weightedSurgeStrategy.cs`

#### Core Logic
- Enhanced version of Price Surge Strategy
- Uses weighted calculations for more accurate surge detection
- Implements similar trailing stop and position management
- Improved sensitivity over basic surge detection

#### Key Implementation Details
- **Enhancement:** Weighted price calculations vs simple average
- **Base Logic:** Inherits core structure from Price Surge Strategy
- **Timing:** Same `newBar` based entry system
- **Risk:** Identical trailing stop mechanism

#### Parameters
- `multiplicative`: Weighted surge threshold (default 1.15)
- `trailingStop`: Trailing stop distance in ticks
- `lookbackRange`: Weighted calculation period (≤10)

---

### 10-12. Keltner Reversion / Trendline Break / S/R Channel Break Strategies — added 2026-09-12

**Files:** `Strategies/keltnerReversionStrategy/`, `Strategies/trendlineBreakStrategy/`, `Strategies/srChannelBreakStrategy/`
(each its own project, `.cs`/`.csproj`/`.sln`/`readme.md`)
**Build outputs:** `C:\Quantower\Settings\Scripts\Strategies\{keltnerReversionStrategy,trendlineBreakStrategy,srChannelBreakStrategy}\*.dll`

Split out of `tvConfluenceStrategy` (below) per the user: rather than one strategy
voting across all three TradingView indicators, each gets its own independent strategy
so it can be tuned, backtested, and evaluated on its own, AND so all three can hold
independent positions on the same account+contract simultaneously rather than only one
ever being able to trade at a time.

#### Core Logic (each)
- **keltnerReversionStrategy** - Keltner Channel mean-reversion (`keltnerChannel.pine`)
- **trendlineBreakStrategy** - ATR-sloped trendline breakout (LuxAlgo `Trendlines.pine`)
- **srChannelBreakStrategy** - multi-touch S/R zone breakout (LonesomeTheBlue
  `supportResistanceChannels.pine`)
- All three: same detector math as `tvConfluenceStrategy`'s corresponding
  `EvaluateX` method, not re-derived; same risk-management skeleton (SL/TP/trailing/
  RTH/daily-loss/max-drawdown) as every other strategy in this repo, just single-
  detector instead of confluence-voting.

#### Multi-instance position isolation (the reason this split needed real code, not just copy-pasting files)
Quantower positions/orders belong to an account+symbol pair, not to a specific strategy
instance - `Core.Instance.Positions` returns every position on that account+symbol
regardless of which strategy (or a human) opened it. Running all three on the same
account+contract without a way to tell them apart would mean each one's "am I already
in a trade?" check sees the OTHERS' positions too - only one could ever hold a position
at a time. Fixed via a `StrategyTag` const per strategy (`"KeltnerReversion"`,
`"TrendlineBreak"`, `"SrChannelBreak"`), set as `PlaceOrderRequestParameters.Comment`
on every order placed, with every `Position`/`Order`/`Trade`/`OrderHistory` query
(including inside the `Core_PositionAdded`/`Core_PositionRemoved`/
`Core_OrdersHistoryAdded`/`Core_TradeAdded` event handlers, which fire globally for
ANY strategy's positions) filtered by `.Comment == StrategyTag`.

**⚠️ NOT verified against a live Quantower session** - confirmed via reflection that
`PlaceOrderRequestParameters`, `Position`, `Order`, `Trade`, and `OrderHistory` all
expose a `Comment` property, and all three strategies build clean, but there was no
running platform connection available to confirm `Comment` actually propagates from
the placing order through to the resulting `Position`/`Trade`/`OrderHistory` records.
**Before running these three together with real size on the same account, place one
small test trade per strategy in sim and check each Position's Comment field in
Quantower's Positions panel.** If it doesn't propagate, each strategy silently falls
back to seeing every position on the account again, defeating the whole point.

#### API / Platform Notes
- All three verified with a clean `dotnet build` (0 errors, 0 warnings) against
  v1.146.18/.NET 10, and confirmed `tvConfluenceStrategy` itself still builds
  unaffected by the split.
- `tvConfluenceStrategy` was left in place, unmodified - it's still there if the
  combined confluence-vote behavior is ever wanted again; these three are additive,
  not a replacement.

---

### 9. TV Confluence Strategy (`tvConfluenceStrategy`) — added 2026-09-12
**File:** `Strategies/tvConfluenceStrategy/tvConfluenceStrategy/tvConfluenceStrategy.cs`
**Readme:** `Strategies/tvConfluenceStrategy/readme.md`
**Build output:** `C:\Quantower\Settings\Scripts\Strategies\tvConfluenceStrategy\tvConfluenceStrategy.dll`

#### Core Logic
- Combines three TradingView indicators from `TradingView/` into one confluence-gated system: **Trendlines.pine** (LuxAlgo trendline break), **keltnerChannel.pine** (Keltner Channel mean-reversion), **supportResistanceChannels.pine** (LonesomeTheBlue S/R channel break)
- Each detector votes LONG/SHORT/nothing on bar close; entry fires only once `MinConfluencesRequired` of the *enabled* detectors agree on direction
- Math is a direct, verified port of DayTrader-Portable's `confluences.ts` (`trendline_break`/`keltner_reversion`/`sr_channel_break`) — same pivot definition, same ATR/EMA formulas, same edge-case handling, not a re-derivation from the raw .pine
- Bar-close only (no intrabar re-evaluation — pivots/zones are a "confirmed shape" question, not a tick-level one)
- Flat-only entries — unlike the EMA-cross strategies, does NOT flip on an opposite signal while in a position; managed purely by SL/TP/trailing/daily-loss/drawdown until flat, matching the DayTrader-Portable confluence system's own "independent one-shot signal" philosophy rather than a continuous cross state

#### Key Private Fields
| Field | Purpose |
|---|---|
| `barsNeeded` | Computed in `OnRun()` from whichever enabled detector needs the most history |
| `BuildBarArray()` | Builds an ascending `Bar[]` from `HistoricalDataExtensions.High/Low/Close` offsets, mirroring confluences.ts's `Bar[]` convention |
| `EvaluateTrendlineBreak` / `EvaluateKeltnerReversion` / `EvaluateSrChannelBreak` | The three ported detectors, each returning a `ConfluenceResult { Met, Direction, Strength, Detail }` |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state (same pattern as `futuresProStrategy`) |
| `dailyPnl` / `dailyLimitHit` / `totalRealizedPnl` / `peakEquity` / `drawdownLimitHit` | Daily loss cap + max drawdown state (same pattern as `futuresProStrategy`) |

#### Parameters (InputParameter index order)
See `tvConfluenceStrategy/readme.md` for the full table (27 parameters: instrument/period/quantity, 3 params per detector + an on/off toggle each, min-confluences gate, SL/TP/trailing, RTH, daily loss, max drawdown).

#### What was simplified vs. the raw .pine scripts
- Trendlines.pine's `Stdev`/`Linreg` slope modes not ported (ATR mode only)
- keltnerChannel.pine's embedded WaveTrend oscillator and overbought/oversold circle markers not ported (Keltner Channel band only)
- supportResistanceChannels.pine's Close/Open pivot source option and per-touch strength weighting not ported
- Full detail and rationale in the readme.

#### API / Platform Notes
- Confirmed `HistoricalDataExtensions.Close/High/Low(hdm, offset)` still resolves as a static extension method against v1.146.18 — same signature as every other strategy in this repo
- Deliberately does NOT use `Core.Instance.Indicators.BuiltIn.*` for EMA/ATR — computes both directly from the bar array (see "Platform version notes" below for why)
- Verified with a clean `dotnet build` (0 errors, 0 warnings) against the currently-installed Quantower v1.146.18 / .NET 10

---

### 8. Gold ORB Strategy (`goldOrbStrategy`) 
**File:** `Strategies/goldOrbStrategy/goldOrbStrategy/goldOrbStrategy.cs`

#### Core Logic
- Opening Range Breakout strategy for gold trading
- Captures 8:00 PM - 8:05 PM price range (configurable)
- Waits for breakout with optional confirmation candles
- Implements risk/reward ratio-based targets

#### Key Implementation Details
- **Session Detection:** Time-based ORB session tracking
- **Range Capture:** `orbHigh`/`orbLow` during session window
- **Breakout Logic:** Price break above/below range with confirmation
- **Strategy Modes:** "Aggressive" vs "Confirmed Breakout"
- **Daily Reset:** New range calculation each trading day

#### Parameters
- `orbSessionStartHour/Minute`: ORB session start (default 20:00)
- `orbSessionEndHour/Minute`: ORB session end (default 20:05)  
- `strategyMode`: Breakout confirmation mode
- `confirmationCandleMinutes`: Time to wait for confirmation
- `riskRewardRatio`: Target calculation multiplier

#### UPDATE 2026-10-05 — repurposed: real 1-min continuation confirmation + swing HH/LL targets

The operator: "there is also a mgc gold orb strategy i have in quantower that i want to repurpose,"
then, confirmed via `AskUserQuestion`, scoped it narrowly: **keep the existing logic, fix/tune it
in place** (not a rebuild like `mesOrbStrategy`'s own fresh ORB build the same day) — "the main
strategy is to mark out the 8:00-8:05pm est range and then wait for a candle body closure out of
the range and then a continuation in the 1min timeframe," plus "target hh and ll levels" for the
take-profit.

**Found and fixed first — the project wouldn't even build**: `.csproj` still pointed at
`v1.146.18` (`StartProgram`/`HintPath`/`OutputPath`), which no longer exists on disk (only
`v1.147.5` is installed) — same drift documented elsewhere in this file for other projects.
Bumped to `v1.147.5` and the `OutputPath` to the standard `C:\Quantower\Settings\Scripts\
Strategies\goldOrbStrategy` (it had been nested under the old version folder).

**`BreakoutMode.CandleClosure` already existed** (detects a body CLOSE beyond the ORB range on
the main `hdm` series) but its own "confirmation" step (`CheckForConfirmation`) was a pure TIME
delay (`confirmationMinutes`, default 1) — it never actually checked price action, just waited a
clock and entered regardless of whether the move had continued or reversed in that time. Replaced
with a genuine 1-minute continuation check: a new, dedicated `continuationHistory` series
(`ContinuationPeriod`, default `Period.MIN1`) decides off the NEXT real 1-min bar to close after
the breakout — a bullish body that closes beyond the breakout price confirms (enter); anything
else resets the breakout watch entirely rather than entering blind. `entryMode` default changed
`FirstBreakout` → `CandleClosure`, matching the operator's own "the main strategy" framing.
`confirmationMinutes` left declared but unused (removing an `InputParameter` risks breaking a
saved instance's index-based settings mapping) with a note explaining why.

**New `TargetMode` (`RiskReward` | `SwingHighLow`, default `SwingHighLow`)**: a new
`SwingLevelTracker.cs` (fractal pivot confirmation, same rule as `finchDomScalpStrategy`'s own
`SwingTracker`, but keeping a bounded HISTORY of confirmed swings on each side rather than just
the latest one — needed because the target must be the nearest swing AHEAD of price, and the most
recent confirmed swing is often already behind it once price has broken out). Fed from its own
dedicated `swingHistory` series (`SwingDetectionPeriod`, default `Period.MIN5` — sub-minute pivots
are noise, same lesson already learned fixing `finchDomScalpStrategy`'s structure filter).
`CalculateTarget` picks the nearest qualifying swing high (longs) / low (shorts), falling back to
the old Risk:Reward-multiple calculation whenever none exists yet or the nearest one is too close
to be worth the trade (`minTargetRiskMultiple`, default 1.0x the stop's own risk) — never leaves a
trade with no target.

**Also fixed, found incidentally while touching `CalculateStopLoss`**: `FullOrbRange`/
`FiftyPercentOrb`/the default case all multiplied `orbBufferTicks` by a hardcoded `0.25` — MNQ's
tick size, not gold's (MGC's own tick size is 0.10). Changed to `this.CurrentSymbol.TickSize` in
all three places. `FixedTickAmount` mode already did this correctly, which is how the mismatch
surfaced — directly adjacent, clearly in scope for "tune it," fixed rather than left for later.

**Verified 2026-10-05**: `dotnet build goldOrbStrategy/goldOrbStrategy.csproj -c Release` — 0
errors, both right after the version-path fix alone and again after the full feature set. Deployed
to `C:\Quantower\Settings\Scripts\Strategies\goldOrbStrategy\`. **NOT YET verified live** — the
1-min continuation logic and the swing-target logic are both brand new paths with zero track
record; watch the log (`"1-min continuation CONFIRMED/FAILED..."`, `"Target Mode: ..."`) against
the real chart before trusting it unattended.

---

### 9. EMA Simple Strategy (`emaSimpleStrategy`) — undocumented until 2026-09-14
**File:** `Strategies/emaSimpleStrategy/emaSimpleStrategy/emaSimpleStrategy.cs`

#### Core Logic
A deliberately stripped-down sibling of `emaCrossStrategy` — same Micro/Mid EMA crossover
and reverse-cross-flips-the-position idea, but with **none** of the impulse-filter/retrace-
watch/HTF-touch-re-entry/partial-close machinery. Enters on a fresh cross, reverses on the
opposite cross, manages risk with a hard SL + optional TP bracket and an optional simple
trailing stop. Entirely bar-close driven for signals; the trailing stop is the only tick-
level logic (`Hdm_HistoryItemUpdated`).

#### Key Private Fields
| Field | Purpose |
|---|---|
| `pendingEntrySide` | Queued reverse-cross flip, fires in `Core_PositionRemoved` |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state |
| `inPosition` / `waitOpenPosition` / `waitClosePositions` | Order-in-flight guards |

#### Parameters (InputParameter index order)
| # | Name | Default | Notes |
|---|---|---|---|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Micro EMA (fast) | 5 | Fast EMA period |
| 3 | Mid EMA (slow) | 29 | Slow EMA period |
| 4 | Period | MIN1 | Chart timeframe |
| 5 | Start Point | 30 days ago | Historical data start |
| 6 | Quantity | 1 | Contracts per trade |
| 7 | Stop Loss (ticks) | 100 | Hard bracket SL |
| 8 | Take Profit (ticks) | 0 | 0 = disabled |
| 9 | Trail Activation (ticks) | 30 | Profit to arm trailing; 0 = off |
| 10 | Trailing Stop (ticks) | 15 | Pullback from peak to close; 0 = off |

---

### 10. EMA Trend Strategy (`emaTrendStrategy`) — undocumented until 2026-09-14
**File:** `Strategies/emaTrendStrategy/emaTrendStrategy/emaTrendStrategy.cs`

#### Core Logic
EMA crossover (Fast/Slow) gated by two OPTIONAL filters the file's own header comment
describes clearly: a **trend filter** (price must be on the correct side of a third,
slower Trend EMA - 0 disables it) and a **momentum filter** (the Fast/Slow spread at the
crossover bar must exceed the prior 4 bars' average spread × a multiplier - 1.0 disables
it). Three selectable exit modes:
- **Mode 0 (Bar Push)** - code-managed exit when price ticks past the prior bar's high/low
  against the position; fixed SL as a safety net only.
- **Mode 1 (SL/TP + Trailing)** - a bracket order placed at entry does all exit management.
- **Mode 2 (TV Match)** - mirrors `emaCrossStrategy`'s TradingView-ported weakness-bar exit
  (EMA gap shrinking for N consecutive bars) plus reverse-cross re-entry; fixed SL as a
  hard backstop underneath.

#### Parameters (InputParameter index order, partial - see file for the full exit-mode block)
| # | Name | Default | Notes |
|---|---|---|---|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Fast EMA | — | Fast EMA period |
| 3 | Slow EMA | — | Slow EMA period |
| 4 | Trend EMA | 0 = disabled | Direction filter |
| 5 | Period | — | Chart timeframe |
| 6 | Start Point | — | Historical data start |
| 7 | Quantity | 1 | Contracts per trade |
| 8 | Momentum Multiplier | 1.0 = disabled | Crossover-strength filter |
| 10 | Exit Mode | 2 (TV Match) | 0 = Bar Push, 1 = SL/TP+Trailing, 2 = TV Match |
| 11 | Stop Loss (ticks) | 100 | Hard safety net, all modes |
| 12 | Trailing Stop (ticks) | 40 | Exit Mode 1 only |

---

### 11. ES ORB Strategy (`esOrbStrategy`) — undocumented until 2026-09-14
**File:** `Strategies/esOrbStrategy/esOrbStrategy/esOrbStrategy.cs`

#### Core Logic
Opening Range Breakout for ES, more configurable than `goldOrbStrategy`: three **entry
modes** (`Immediate Breakout`, `Dynamic Multi-Level Retest` i.e. wait for a 50% retest of
the range, `Wait for Any Retest`) crossed with three **stop-loss modes** (`Full Range`,
`50% Range`, `Fixed Points`) via C# enums exposed as dropdown `InputParameter` variants
(same UI pattern as `esOrbStrategy.cs`'s own `EntryMode`/`StopLossMode` enums - note enums
need the explicit `variants:` array since `[InputParameter]` doesn't render raw enums, per
the platform note already captured under EMA Cross Strategy above).

#### API / Platform Notes
- Demonstrates the enum-as-dropdown `variants: new object[] { "label", EnumValue, ... }`
  pattern other strategies in this repo could reuse instead of the `int`-with-a-comment
  workaround `emaCrossStrategy`/`emaTrendStrategy` use for their mode selectors.

---

### 12. Direction + Absorption Scalp Strategy (`directionAbsorptionScalpStrategy`) — added 2026-09-15

**Files:** `Strategies/directionAbsorptionScalpStrategy/directionAbsorptionScalpStrategy/
directionAbsorptionScalpStrategy.cs` (+ its own `readme.md` at the project root — read it first,
it's more detailed than this entry)

#### What it is
The first strategy in this repo built by REUSING an indicator's logic library rather than
writing detection math from scratch: trades ORB-IX's "possible long/short" direction callout
(`OrbIx.Core.Direction.DirectionEngine`) against a real HH/LL support/resistance level, timed by
**Ocean's Anchor**'s absorption state machine — an ATAS indicator from a friend's 18-indicator
suite dropped into `Ported/src/oceans-anchor/` this same session (see `Ported/PORTING-GUIDE.md`).
Zone lifecycle: Dormant (nothing near it) -> Armed (price is testing it) -> Triggered (a
qualifying absorption print fired — either a tape-based test requiring NO follow-through within
a timed window, or a bar-shape 3-of-4 test when no tape event fires) -> Confirmed (a later bar's
delta flip or CVD divergence favours the trade, inside a bar-count clock) or Expired/Broken.
**Entry fires only on the Confirmed transition, gated on DirectionEngine's current side agreeing
with the zone's side** — two independent readings both required, directly matching the session's
"keep an eye on absorption levels... and target areas for the scalp" request. Stop = the
confirming print's own price extreme; target = nearest of (opposing HH/LL, VWAP, prior day/week
level) ahead of price, clamped by a minimum distance.

#### Two deliberate architecture decisions, both explained at length in the csproj/readme
1. **`OrbIx.Core` AND Ocean's Anchor's absorption files are compiled in by source
   (`<Compile Include>` glob), never referenced as separate DLLs** — mirrors
   `OrbIx.Quantower.Indicator.csproj`'s own documented reasoning: Quantower's `Assembly.Load`
   caches by FullName, so a shared DLL under two script folders collides on one cache key (a
   defect already measured once in this repo for the indicator itself).
2. **This strategy builds its OWN `Zone` objects from `HhLlEngine` segments — it does NOT port
   Ocean's Anchor's footprint-based HVN zone detector** (`ZoneBuilder`/`AnchorProfile`). That
   detector needs per-price volume data, which hits the SAME historical-volume-analysis refusal
   already documented above for this Quantower connector. Only `AnchorModels.cs`, `AnchorState.
   cs`'s `SignalEngine`/`ZoneMaintenance`/`SignalGate`, and `AnchorAbsorption.cs`'s
   `TapeAbsorption`/`DisplacementWatch`/`ClusterAbsorption` are actually exercised; the rest of
   `oceans-anchor` compiles in as harmless unused code for the same reason.

#### Safety
Two gates before any order can be sent: `ConfirmSimOrEvalAccount` (default **false** — a manual,
auditable acknowledgment; there's no reliable SDK-level way to detect "is this a sim account")
and `DryRun` (default **true** — logs the full decision, places nothing). Position isolation
uses the `srChannelBreakStrategy`-style `StrategyTag`/`Comment` filter. **Not yet run against a
live session** — see the readme's bring-up order (dry-run review, cross-check against the
ORB-IX indicator on the same symbol, then sim/eval-only with `Quantity=1`) before trusting it
with anything.

---

### 13. Finch DOM Scalp Strategy (`finchDomScalpStrategy`) — added 2026-09-25

**Files:** `Strategies/finchDomScalpStrategy/finchDomScalpStrategy/finchDomScalpStrategy.csproj`,
`finchDomScalpStrategy.cs`

#### What it is
"i wanna make this into a strategy now that i can run in quantower were it plays off the orders
from the dom and the ifvg and absorption levels and unfinished auctions" — automates Finch-Lite's
own manual read (large resting DOM orders, tiered absorption, unfinished auctions, inverse fair
value gaps) into a real order-placing Strategy. Compiles `RestingOrderEngine.cs`,
`FairValueGapEngine.cs`, and `Bar.cs` IN BY SOURCE from `Indicators/Finch-Lite/src/
Finch.Lite.Indicator/` (same `<Compile Include>` collision-avoidance reasoning as
`directionAbsorptionScalpStrategy`'s own csproj comment, restated in this one) — this strategy
trades off the EXACT SAME detection logic the Finch-Lite indicator draws, not a second,
independently-maintained copy. **Trade-off, stated in the csproj**: this is a frozen snapshot as
of this strategy's own last build; a later fix to Finch-Lite's own copy of these three files does
not reach this strategy until it is rebuilt.

**Entry** (`TryEnter`): a DOM/unfinished-auction level is the ANCHOR (`Current >= MinLevelSize`);
strong absorption (`Absorbed >= AbsorptionStrongContracts`) and an aligned inverse-FVG zone (same
side, price within `IfvgProximityTicks` of the zone) are CONFIRMATION filters — all three
required, fewer/higher-conviction trades over independent triggers. Direction conflict (level
side vs. IFVG side disagree) → skip the trade entirely, never pick a side. Unfinished-auction
levels are NOT given separate retest-only logic — they pass through the same anchor check as any
other qualifying level (`IsUnfinished` is just a flag on the same record); **flagged as a
judgment call, not confirmed**, since "plays off... unfinished auctions" could have meant a
retest-specific trigger instead.

**Exits**: stop = the anchor level's own price ± `StopBufferTicks`; target = nearest OTHER
opposing DOM/UA level or opposing IFVG zone edge, ahead of price past `MinTargetDistanceTicks`,
nearest wins (`TryComputeTarget` — same `IsAhead`/min-distance/nearest-wins shape as
`directionAbsorptionScalpStrategy.TryComputeTarget`); falls back to a fixed
`FallbackTargetTicks` R:R target if nothing qualifies ahead.

**No tick classification needed for absorption**: absorption comes purely from BOOK SIZE CHANGES
between DOM polls inside `RestingOrderEngine` itself. It does keep a no-op `Symbol.NewLevel2`
handler, for the same platform reason discovered in the indicator: without SOME `NewLevel2`
subscriber, the platform stops maintaining live depth and the DOM pull returns an empty book.
(An `OnLast` subscription WAS added later the same day — see "POC trading" below — but only to
feed POC's own volume-by-price accumulator, not absorption.)

Risk management block (`MaxDailyLoss`/`MaxDrawdown`/`MaxTradesPerSession`/
`MinBarsBetweenEntries`/`RthOnly`+hours) and the `StrategyTag`/`Comment` position-isolation
pattern (`MyPositions()`/`MyOrders()` filters, every `Core_*` handler tag-checks first) are
copied from `directionAbsorptionScalpStrategy`'s own already-working shape — same names/defaults
where the concept is shared.

#### Safety — deliberately NO gate, unlike every other auto-trading strategy in this repo
The operator was asked directly and chose **"Skip the gate, ready to trade immediately."**
There is **no `DryRun`, no `ConfirmSimOrEvalAccount` parameter at all** in this strategy — it
places real orders the moment it is attached and enabled on a real account, with no dry-run
stage and no manual confirmation step in the code. This is a confirmed, deliberate choice, not
an oversight — **do not silently add a gate back in** without being asked. `OnRun` still logs
account identity loudly on start (kept from the reference strategy, costs nothing). The operator
is responsible for attaching this to a sim/eval account themselves; nothing in the code checks
that for them.

#### Verified 2026-09-25

- Indicator-side refactor (`RestingOrderEngine` extraction, see Finch-Lite's own catalog entry
  above) built and deployed first, alone, as the higher-risk step — 0 errors, 0 warnings.
- `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release` — 0 errors (10
  nullable-annotation-context warnings only, pre-existing style, not a correctness issue).
  Deploys directly to its `OutputPath` (`C:\Quantower\Settings\Scripts\Strategies\
  finchDomScalpStrategy\`) — confirmed that folder holds only this strategy's own
  DLL/PDB/deps.json, no stray shared assembly (proof the `<Compile Include>` source-sharing
  didn't silently revert to a project reference).
- Found and fixed a real integer-overflow bug during review (not caught by the compiler, a logic
  bug): `lastEntryBarIndex` was initialized to `int.MinValue`, so the very first
  `MinBarsBetweenEntries` cooldown check (`barCounter - lastEntryBarIndex`) would overflow `int`
  and wrap to a large NEGATIVE number under C#'s default unchecked arithmetic — incorrectly
  satisfying `< MinBarsBetweenEntries` and permanently blocking the first-ever entry. Fixed by
  initializing to `-1_000_000` instead (far enough negative to never look "within cooldown" for
  any realistic `barCounter`, without being close enough to `int.MinValue` to risk the same
  overflow again). Rebuilt clean after the fix — 0 errors.
- **NOT YET verified live** — attaching to a real (sim/eval) Quantower session, confirming the
  account-identity log line, watching for a first signal, and confirming one full round trip
  (entry with SL/TP attached, exit via stop/target/risk-limit, correct `Comment` tagging in the
  Positions panel) all require the operator's own live session and have not been done. Given the
  no-dry-run design above, the FIRST confirmed signal on whatever account this is attached to
  places a real order — attach to sim/eval, not live, before enabling.

#### POC trading, added 2026-09-25 ("can we add trading with the poc in the strategy as well")

Compiles in `PocEngine.cs` by source too (same collision-avoidance reasoning), giving this
strategy its own current-move and 15m POC values computed by the EXACT SAME engine the
indicator's own POC lines are drawn from. Six more clarifying questions answered before writing
any of this, since it added a genuinely new entry pathway with real-money implications:

- **Target role (both entry pathways)**: current-move and 15m POC now join the candidate pool in
  `TryComputeTarget` alongside opposing DOM/UA levels and IFVG zone edges — same
  ahead-of-price/min-distance/nearest-wins selection, just two more candidates. `TryComputeTarget`
  was refactored to take the trade's own `bool isLong` directly instead of a `RestingLevel`
  anchor, specifically so both entry pathways below can share it.
- **Entry role**: a brand-new, STANDALONE trigger (`CheckPocRejection`) — does NOT touch
  `TryEnter`'s own DOM/absorption/IFVG logic at all.
- **Direction**: a REJECTION away from the POC (POC acts as support/resistance), not a reversion
  toward it.
- **Detection**: bar-close confirmed (`TryPocRejection`) — a closed bar's high/low must touch or
  pierce the POC, but its CLOSE must end up beyond `PocRejectionBufferTicks` on the origin side,
  same close-based confirmation style as `OrderBlockEngine`/`FairValueGapEngine`.
- **Eligible POCs**: either the current-move or the 15m POC independently qualifies —
  current-move checked first, 15m second, first match wins.
- **Gating**: fully SHARED with the DOM/absorption pathway — same cooldown
  (`MinBarsBetweenEntries`), same daily-loss/drawdown/trade-count limits, same `StrategyTag`, same
  one-position-at-a-time check. `CheckPocRejection` runs once per poll, after `TryEnter` — whoever
  qualifies first wins the poll.

**New machinery this required**: an `OnLast` subscription (this strategy previously had none —
absorption/IFVG needed only DOM polls and closed bars) queuing raw `(Price, Size)` prints, drained
into both `PocEngine`s on the poll timer, same §10 "no work on the market-data thread beyond an
enqueue" discipline as the indicator. A dedicated 15-minute `HistoricalData` (`PocLookbackDays`,
default 5) feeds the higher-timeframe engine, independent of `hdm` (the strategy's own chart
period). Stop for a POC-rejection trade sits beyond the rejection bar's OWN extreme (the wick that
got rejected) ± `StopBufferTicks`; target/fallback distance is measured from the rejection bar's
own CLOSE, not the POC price itself, since the bar has by definition already closed away from the
POC by the rejection buffer.

**A real bug caught and fixed during writing, before the first build**: an early draft of
`PlacePocEntry` called `RestingOrderEngine.Reconcile(...)` a SECOND time (with `null` bids/asks)
just to get "the levels" for its own `TryComputeTarget` call. `Reconcile` is STATEFUL — it mutates
the engine's own tracking dictionaries as a side effect every time it runs, including treating a
`null` book as "every tracked level just went missing this poll." Calling it again inside the same
poll would have corrupted the shared `restingOrderEngine`'s live state out from under the
DOM/absorption pathway. Fixed by threading the SAME `levels`/`ifvgZones` this poll's `RunPoll`
already computed through to `CheckPocRejection`/`PlacePocEntry` instead of recomputing anything.

**Verified 2026-09-25**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c
Release` — 0 errors, 13 warnings (all pre-existing nullable-annotation-context style, none new
from this change). Deployed folder still holds only this strategy's own DLL/PDB/deps.json.
**NOT YET verified live** — same live-session caveat as above applies to this new pathway too,
and now doubly so: it is a SEPARATE, never-yet-run trigger from the original DOM/absorption one.

#### CRITICAL LIVE-FILL INCIDENT, 2026-09-27 — embedded bracket SL/TP confirmed broken, position went live with NO stop

The strategy's first-ever live signal fired on a real Rithmic account (`RTG25761173337`, MNQ) —
a POC-rejection Buy at 30789.75, logged correctly with `stop=30787.75 target=30801.75`. The
position opened with **neither a stop nor a target attached** — confirmed by the operator's own
screenshot (Positions panel, both "Stop lo..." and "Take p..." columns empty) while the position
sat open and unprotected. The operator manually protected it and stopped the strategy; asked to
dig through the platform's own logs for root cause.

**Root cause, found in `C:\Quantower\Logs\Serilog\<date>.slog`** (structured JSON, one event per
line — worth remembering as a diagnostic tool for this whole repo, not just this incident): the
entry request DID carry the right numbers (`Stop loss: 30,787.75; Take profit: 30,801.75`), but
the platform's own attempt to turn that embedded `SlTpHolder.CreateSL/CreateTP(price,
PriceMeasurement.Absolute)` bracket into child orders produced a **Sell Limit at 38,490.00**
(instantly refused by Rithmic — "price crossed high limit") and a **Sell Stop at 23,093.00**
(accepted, but ~7700 points from market — useless). The math confirms exactly what happened: the
requested price got treated as a TICK-COUNT OFFSET from entry rather than an absolute price —
`30801.75 × 0.25 (MNQ tick size) = 7700.4375`, and `30789.75 + 7700.4375 ≈ 38,490.19`; same for
the stop leg the other direction. Confirmed via SDK reflection that `PriceMeasurement.Absolute`
(0) really is the "use this as literally the price" value (the only other value is `Offset` = 1),
so this is a bug in how this connection converts an embedded bracket into child orders, not a
mistake in the price math feeding it. **This SlTpHolder pattern was already flagged as "unverified
against a live session" in `directionAbsorptionScalpStrategy`'s own readme — now confirmed broken
on a real connection, for BOTH strategies that use it.**

**A second, independent bug found in the same log window**: five identical "Exception has been
thrown by the target of an invocation" errors, exactly correlated with this one order's
state-change broadcasts and nowhere else in two full days of logs — one of this strategy's own
`Core_PositionAdded`/`Core_PositionRemoved`/`Core_OrdersHistoryAdded`/`Core_TradeAdded` handlers
was throwing, almost certainly from dereferencing a null event argument (none of the four
null-checked their argument). Quantower's own log doesn't retain the inner exception's type/
message past the generic reflection-wrapper text, so the exact original line is unconfirmed. Left
unfixed this risked `waitOpenPosition` getting stuck permanently true if the throw happened before
that flag cleared, silently blocking every future entry regardless of the bracket bug.

**Also found while investigating**: the mispriced stop order (23,093.00) was left resting on the
account — Quantower's own auto-generated bracket child orders come back with an EMPTY `Comment`,
so this strategy's own `Core_PositionRemoved` cleanup (which cancels `MyOrders()`, filtered by
`Comment == StrategyTag`) could never find or cancel it. Operator cancelled it by hand.

#### FIX, 2026-09-27 — separate explicit stop/target orders, hardened event handlers, one-time breakeven

Per the operator's own follow-up ask ("work on the logic for opening a take profit and stop loss
and if its running good in profit to move the stop to inprofit to cover fees"):

1. **No more embedded `SlTpHolder` bracket on the entry order at all.** `PlaceEntry` now places
   ONLY the market entry, storing the intended stop/target as `pendingStopPrice`/
   `pendingTargetPrice`. Once `Core_PositionAdded` confirms the position is actually open,
   `PlaceProtectiveOrders` places the stop and target as TWO SEPARATE, EXPLICIT orders — a Stop
   order using `TriggerPrice`, a Limit order using `Price` — each resolved via its own
   `OrderTypeBehavior.Stop`/`.Limit` order type (same resolution pattern as the existing Market
   order type, done once in `OnRun`). Both legs carry the SAME `StrategyTag` comment as the entry,
   so the EXISTING `Core_PositionRemoved` cleanup correctly finds and cancels whichever leg didn't
   fill once the position closes — no broker-side OCO grouping is used or needed.
2. **All four `Core_*` event handlers now null-check their argument first**, and the two with any
   real follow-on work (`Core_PositionAdded`'s protective-order placement,
   `Core_PositionRemoved`'s leftover-order cancellation) wrap that work in its own try/catch with
   logging — so a future failure there is visible in this strategy's own log with an actual
   exception type/message, rather than only in Quantower's own generic, detail-free platform log.
3. **One-time breakeven move** (operator's own explicit choice over a continuous trail, via
   `AskUserQuestion`): new `BreakevenTriggerTicks` (default 20, 0=off) and `BreakevenBufferTicks`
   (default 3) parameters. `CheckBreakeven`, run every poll alongside `CheckRiskLimits`, checks
   `Position.GrossPnLTicks` against the trigger and — once, via a `breakevenMoved` latch reset on
   every new position — modifies the protective stop order's own `TriggerPrice` in place (via
   `Core.Instance.ModifyOrder(new ModifyOrderRequestParameters(order) { TriggerPrice = ... })`,
   not a cancel/replace) to sit `BreakevenBufferTicks` beyond entry, covering round-turn fees.

**Verified 2026-09-27**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c
Release` — 0 errors, 17 warnings (all pre-existing nullable-annotation-context style). Deployed
folder still holds only this strategy's own DLL/PDB/deps.json.

#### SAME-DAY FOLLOW-UP, 2026-09-27 — the fix above still didn't place protective orders; root cause was much bigger than the bracket bug

The very next live signal (a POC-rejection Buy, filled at 30,780.00) STILL placed no stop or
target — confirmed both from the operator's own screenshot and, more reliably, from the
strategy's own per-instance log at `C:\Quantower\Settings\Scripts\ScriptsData\
finchDomScalpStrategy (<run-guid>)\logs\<date>.slog` (a SEPARATE file from the main platform
Serilog — worth remembering: platform-level order/fill events are in
`C:\Quantower\Logs\Serilog\<date>.slog`, this STRATEGY's own `this.Log(...)` output is in
the ScriptsData one). That log showed the `[Signal]` line and NOTHING after it — no
`[Order] protective...` success or failure line at all, meaning `PlaceProtectiveOrders` was never
even called.

**Root cause, found via SDK reflection**: this strategy's `CurrentSymbol` (a continuous selection,
e.g. "MNQ") is a DIFFERENT OBJECT from the specific underlying contract ("MNQZ6") that every
actual `Position`/`Order`/`Trade` comes back tagged with — the order-placing log itself showed
"Symbol: MNQ" in the request but "MNQZ6" in every resulting record. Reflecting on
`Symbol`/`Position`/`Order`/`Account` confirmed NONE of them overload the `==` operator (only
`IEquatable<T>.Equals`, which `==` does NOT use for a class without an explicit operator overload)
— so `obj.Symbol == this.CurrentSymbol` throughout this file was comparing two DIFFERENT,
non-identical objects and was **ALWAYS false**. This one broken comparison pattern silently took
out: `MyPositions()`/`MyOrders()` (always returned empty), every `Core_*` event handler's own
identity filter (never matched, so `Core_PositionAdded` never reached its protective-order call),
risk limits and breakeven (both gated on `MyPositions()`), and `Core_TradeAdded`'s PnL
accumulation — nearly everything in the file keyed off "is this position/order mine." **This exact
pattern was copied from `directionAbsorptionScalpStrategy`'s own reference shape — presumably
equally broken there whenever it trades a continuous/generic symbol selection, though that file
was not touched in this pass.**

**Fix**: a new `IsMine(Symbol, Account, string comment)` helper replaces every `==`/`!=` Symbol/
Account comparison in the file. It never compares Symbol object identity — the FIRST position/
order this strategy observes (matched on `Account.Id` + `ConnectionId` + the `StrategyTag` Comment
alone, since the specific contract isn't known yet) resolves and caches the actual underlying
contract's own `Symbol.Id` in `resolvedSymbolId`; every later match additionally requires that
same resolved `Symbol.Id`, comparing STRINGS, never objects. Resolved once per run, never reset on
a flat position (a contract roll mid-run would need a restart — an accepted limitation).

**A separate, fourth issue flagged directly by the operator from that same signal**: "the stop it
called was so small and short it didnt make sense." The POC-rejection pathway's stop sits just
beyond the rejection bar's own wick, which can be an arbitrarily tiny distance from the actual
fill price — a single bar's range says nothing about the instrument's real volatility. New
`MinStopDistanceTicks` parameter (default 20) now floors BOTH entry pathways' computed stop
(`EnforceMinStopDistance`) to at least that many ticks from the current/reference price, widening
it outward when the naive calculation comes in tighter than that.

**Verified 2026-09-27 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still holds only this strategy's own DLL/PDB/deps.json.
**STILL NOT verified live** — three real, load-bearing bugs have now been found and fixed from
exactly two live signals; the next one needs the same close scrutiny (both platform AND
per-instance ScriptsData logs) before trusting this strategy's bracket placement at all.

#### FIFTH ISSUE, same day — POC-rejection trigger fired on ordinary chop at a flat POC

Operator, after enabling the strategy: "it seems as soon as i turn it on it enters a trade from
the poc line which just sits there and bounces what are the triggers to get into a trade." A real
rejection is supposed to mean price approached the POC from a distance and got turned away, but
`TryPocRejection` alone never checked for an approach — a market simply chopping right on top of a
flat POC satisfies "touch it, close beyond a 3-tick buffer" on nearly every bar, since some wick
always touches a nearby POC and ordinary noise closes past a small buffer constantly.

**Fix**: a new `HasGenuineApproach` check, ANDed onto `TryPocRejection`'s own result in
`CheckPocRejection`. Requires at least one of the `PocApproachLookbackBars` (default 5) bars
BEFORE the rejection bar to have been `PocApproachDistanceTicks` (default 10) away from the POC on
the origin side (above it for a bullish rejection, below it for a bearish one) — confirming price
actually travelled from a distance rather than already sitting on the level. No prior bars
available (e.g. right after the backlog-priming window) is treated as "cannot confirm an approach"
and skipped, not allowed through. `RunPoll` now gathers this small prior-bars window from `hdm`
alongside the existing single rejection-bar read, threaded through to `CheckPocRejection`.

**Verified 2026-09-27 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean.

#### SIXTH AND SEVENTH ISSUES, same day — restart-instant firing, then Position.Comment unreliable

Live-testing the fifth fix immediately surfaced two more real bugs, both found the same way as
every other one in this saga: reading both the platform Serilog and the strategy's own
per-instance ScriptsData log side by side rather than guessing.

**Sixth**: the operator restarted the strategy to test the approach-distance fix, and it fired a
trade 267ms after starting — nowhere near enough time for a new bar to close. Root cause:
`isFirstDrain` (meant to skip the stale backlog burst) only ever gated the FIRST poll. On the
SECOND poll, `isFirstDrain` was already false, so the old guard happily read `closedUpTo - 1` and
treated whatever bar was ALREADY sitting there at attach time as "the latest closed bar" — meaning
every restart re-traded whatever setup happened to already exist the instant two polls had passed.
Confirmed by three consecutive restarts in the log, each firing within seconds. **Fix**: a real
bar-index cursor, `pocRejectionCheckedUpTo`, replacing the one-shot boolean. The first bar this run
ever sees is marked as already-accounted-for and never itself eligible; only a bar index STRICTLY
LATER than that — meaning one that closes live, after the run started watching — ever reaches
`CheckPocRejection`.

**Seventh**: with the restart bug fixed, the next signal correctly waited over two minutes before
firing off a genuinely new bar — but STILL placed no protective orders, with zero trace of
`PlaceProtectiveOrders` even being attempted in either log. By this point Order/Trade/OrderHistory
had shown the correct `Comment: FinchDomScalp` in every single logged fill across four separate
trades with no exception — but `Position.Comment`, which `Core_PositionAdded`'s own gate depended
on, had never once been confirmed working. **Fix**: `IsMyPosition`, a new matcher that primarily
compares the ALREADY-RESOLVED `Symbol.Id` (bootstrapped by an Order/Trade event via `IsMine`,
which reliably fires several times over before any position event needs it — see the platform
log's own repeated "Order update"/"Order history" broadcasts preceding every fill) plus
Account.Id — no Comment involved at all. Falls back to Position's own Comment only if
`resolvedSymbolId` somehow isn't set yet. `MyPositions()`/`Core_PositionRemoved` switched to this
matcher too; `MyOrders()`/`Core_OrdersHistoryAdded`/`Core_TradeAdded` keep the original
Comment-based `IsMine`, since Order/Trade/OrderHistory's own Comment field IS confirmed reliable.
A one-line diagnostic now logs whenever `Core_PositionAdded` still fails to match, printing every
compared field, so any further surprise is visible on the very next attempt instead of requiring
another blind investigation.

**ACCEPTED RISK, stated plainly**: once `resolvedSymbolId` is set, `IsMyPosition` treats ANY
position on that exact contract+account as this strategy's own regardless of Comment — including
one the operator opened manually. Narrower than ideal, but this is what's actually reliable on
this connection; the alternative it replaces appears to simply never have matched at all.

**Verified 2026-09-27 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean.

#### EIGHTH ISSUE, same day — the duplicate-order guard itself was checking a value that never got set

The very next live signal, on the ALREADY-deployed seventh fix, reproduced the exact same
duplicate-order storm — roughly ten protective stop/target pairs placed and cancelled within
~100ms, several rejected outright by Rithmic's own risk system ("Total buy quantity of contract
would exceed its limit"), with the account eventually going flat only because the operator
manually closed the position from the chart. Operator: "this is all sorts of screwed up tell me
why im doing this with 1 contract but there is stop loss for more than 1 order for a ton of
seperate orders this needs to be reviewed."

**Root cause**: the seventh fix's guard checked `protectiveStopOrder is not null` — but that field
was only ever set by calling `Core.Instance.GetOrderById(...)` IMMEDIATELY after `PlaceOrder`
returned, and that lookup was failing essentially every time (logged plainly:
"[Order] protective stop placed but could not be looked up for later breakeven modification" —
almost certainly a race between `PlaceOrder`'s synchronous return and the platform's own internal
indexing of the new order). Since the field never actually became non-null, the "already placed"
guard was a complete no-op on every single `Core_PositionAdded` firing — the fix looked correct on
read-through but never once engaged.

**Fix**: replaced the field-based guard with `protectiveOrdersPlaced`, a plain boolean set the
INSTANT placement is attempted, with no dependency on any lookup succeeding. Finding the actual
stop order later (for breakeven) now happens ON DEMAND via a new `FindProtectiveStopOrder()`,
filtered through `MyOrders()` — which relies on `Order.Comment`, confirmed reliable in every
logged fill — rather than a cached reference from the unreliable immediate lookup. The immediate
`GetOrderById` calls and their now-pointless warning log line were removed from
`PlaceProtectiveOrders` entirely.

**Lesson for reviewing this file further**: a guard that checks cached STATE set by an unreliable
async-vs-sync API call is not actually a guard — prefer a plain flag set unconditionally at the
moment of intent, and look up anything else needed later, on demand, through a path already
proven reliable (here, `MyOrders()`'s Comment-based filter).

**Verified 2026-09-27 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean.

#### NINTH ISSUE, same day — target realized far tighter than its own configured minimum

Operator, reading the SAME incident's chart: "even the spot the order was placed looks super
weird in that picture i sent because price was never even there at that price." Pulled the exact
signal from the strategy's own log: `Buy anchor=current-move POC 30731.5 rejection stop=30718.75
target=30735` — filled at 30,732.75 (confirmed from the platform log's `AvgOpenFillPrice`). The
target sits only 9 ticks (2.25 points) from the actual fill — under the configured
`MinTargetDistanceTicks` (10) floor that's supposed to prevent exactly this.

**Root cause**: that minimum-distance floor is enforced when the target candidate is CHOSEN, in
`TryComputeTarget`, measured against the DOM mid-price (or rejection-bar close) at SIGNAL time —
not the price the market order actually fills at, a moment later. Between signal and fill, price
moved enough that the realized distance ended up under the floor even though the candidate
qualified when it was picked.

**Fix**: `PlaceProtectiveOrders` now re-validates BOTH `pendingStopPrice` and `pendingTargetPrice`
against `position.OpenPrice` — the REAL fill, known only once the position actually exists — right
before submitting either order. Reuses `EnforceMinStopDistance` (already built for the stop side)
and a new mirror-image `EnforceMinTargetDistance` for the target side. Both widen outward, never
inward, if the realized distance from the actual fill would otherwise come in under the
configured minimum. The `[Order] protective stop=... target=...` log line now also prints the
actual fill price alongside the two (possibly adjusted) levels, for exactly this kind of
after-the-fact review.

**Verified 2026-09-27 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. This closed out the protective-order-placement
saga — six consecutive rounds of "fix, redeploy, still broken, dig through both logs" on that one
feature, from the same-day live testing that started with the very first signal.

#### FOUR TRADING-LOGIC CHANGES, 2026-09-28 — swing stops, 5m POC, min swing size, delta filter

All four came from the operator reviewing live signals in real time and reasoning about the
underlying logic, not from a bug — same day as the protective-order fixes above, following
straight on from that testing session.

1. **Swing-based stops** — "stop losses should be places at recent swing low for longs and recent
   swing high for shorts right." New `SwingTracker.cs` (own file, same fractal pivot-confirmation
   rule as `OrderBlockEngine`/`PocEngine`, deliberately NOT sharing code with either — this
   project's own convention favors small independent files here). Runs UNCONDITIONALLY in
   `OnRun`/`RunPoll`, regardless of whether any POC feature is enabled, since stop placement now
   depends on it for BOTH entry pathways. `ComputeSwingStop` replaces the old level-price
   (DOM/absorption) and rejection-bar-extreme (POC) stop calculations, falling back to the old
   calculation only if no swing has confirmed yet this run. `MinStopDistanceTicks` and the
   fill-price re-validation from the ninth protective-order fix still apply on top unchanged.

2. **5-minute POC** — "lets add in the 5min poc that would be safer to play off," directly
   following a discussion about the 15m POC's own risk: "while trading based off the 15min poc
   could be dangerous because if its in a down trend and we are looking for a pull back it could
   take a long time for that to play out." `Poc5mEnabled` (default on) mirrors the 15m POC exactly
   — own dedicated 5-minute `HistoricalData`, own `PocEngine` instance, own bar-drain cursor,
   eligible as both a `TryComputeTarget` candidate and its own standalone rejection trigger in
   `CheckPocRejection`, sharing `PocLookbackDays`/`PocSwingPivotLookback` with the 15m one.

3. **Current-move minimum swing size** — from the operator reading an actual signal's numbers:
   "playing off the 1min poc this makes no sense the rr for this trade is super negative." Checked
   the math (`Sell anchor=current-move POC 30704.5 ... stop=30706.5 target=30686`, filled
   30701.25): the R:R itself was fine (~2.9:1) — the real problem is the current-move POC sitting
   on the CHART's own timeframe, which can be formed by a tiny, barely-there swing yet still
   produces a target sized the same as any other anchor. New `PocCurrentMoveMinSwingTicks`
   (default 20, 0=off) requires the most recently completed swing leg (from the same
   `SwingTracker`) to span at least that many ticks before the current-move POC is allowed to
   anchor a trade — checked via `HasSufficientSwingSize`, applied ONLY to current-move, not 5m/15m
   (which already represent more deliberate structure by virtue of their own timeframe).

4. **Delta filter** — "we should also use the delta as a decision factor for when the reversal is
   going to happen because if we keep trying to take longs when delta is negative and in the red
   we are just fighting our selves." Confirmed relevant on a REAL trade from the same session: a
   Sell fired right where the chart showed a large BUY print (a "BUY 51" marker), exactly the
   "fighting ourselves" scenario described. New `DeltaTracker.cs` (own file) buckets classified
   trade prints per chart bar (same tick-classification/bucketing approach as Finch-Lite's own
   delta panel — `TryClassify` copied into the strategy, since it previously needed no
   classification at all) and exposes a rolling sum over the last `DeltaLookbackBars` (default 8,
   the operator's own choice of a recent rolling window over session-cumulative or single-bar) via
   `PassesDeltaFilter`. A HARD block (the operator's own choice over a softer confirmation,
   matching their own framing): a long is skipped entirely when the rolling window is net
   negative, a short is skipped entirely when it's net positive — applied to BOTH entry pathways,
   not just the POC one that prompted it.

**Verified 2026-09-28**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c
Release` — 0 errors. Deployed folder still holds only this strategy's own DLL/PDB/deps.json.

#### TWO MORE FIXES, 2026-09-28 (same day) — benign breakeven race, and a real R:R asymmetry from swing stops

Both found on the first live signals after the four changes above — the swing-based stop change
specifically surfaced a real, if quiet, design mismatch.

**Breakeven "Time out" on a trade that already closed**: "i got this error on break even stop
failed after it got out of the trade." `CheckBreakeven` snapshots the position/stop BEFORE the
network round-trip to `Core.Instance.ModifyOrder` — if the target fills (or the position otherwise
closes) in that gap, the order being modified may already be gone, surfacing as a generic "Time
out" rather than a clean "not found." The trade itself closed correctly through its own existing
bracket regardless. Fixed by checking `MyPositions()` again on a modify failure: if the position is
already flat, log it as an informational "skipped" note, not an Error — only log loudly when a
position still genuinely exists, since THAT would mean something real went wrong.

**Breakeven capping winners far below losers**: "it also seems like the rr for trades are way
negative because it will loose like $30 with no issue but once it gets into a trade a little bit
it moves the breakeven and only makes like 2-10$." Checked the actual numbers on a live trade:
`stop=30601.75 target=30565`, filled 30572.25 — a **118-tick stop**, since swing-based stops (from
the same-day change above) can now be far wider than the old level/bar-extreme calculation ever
was. `BreakevenTriggerTicks` was still a fixed 20 — meaning breakeven fired after recovering only
~17% of that trade's own risk, capping the winner at a 3-tick buffer while a loser would still run
the full 118 ticks. A fixed tick trigger made sense when stops were consistently narrow; it stopped
making sense once stops became this variable trade to trade.

**Fix**: `BreakevenTriggerTicks` replaced with `BreakevenTriggerRiskPercent` (default 50) — the
trigger now scales to a PERCENTAGE of the trade's own actual risk, not a fixed tick count. The risk
distance itself (`pendingRiskDistance`, in price units) is captured fresh in
`PlaceProtectiveOrders` from the REAL fill price and the final, re-validated stop — the same
values the ninth protective-order fix already made accurate — so a wide-stop trade needs
proportionally more profit before breakeven kicks in, and a narrow-stop trade needs proportionally
less. `BreakevenBufferTicks` (covers round-turn fees) stays a fixed tick amount, unchanged — fees
don't scale with stop distance the way risk does.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live.**

**Context from the operator, worth keeping in mind for future work on this file**: "this strategy
is the start of the orderflow trading strategy i want in the end" — the DOM/absorption/IFVG/POC
features built so far are steps toward a larger order-flow strategy, not the final design. Treat
future feature requests here as incremental progress toward that destination rather than isolated
asks.

#### STOP BUFFER WIDENED, 2026-09-28 — from indicator feedback

After adding the 5m POC to the indicator too (see Finch-Lite's own Catalog entry), the operator
liked the result but flagged the strategy's own stop placement: "have it a few ticks below the
swing low incase it comes and bounces off that point again." `StopBufferTicks` (used by
`ComputeSwingStop` to sit beyond the swing reference) defaulted to 2 — barely any room for an
ordinary wick retest of the exact swing point. Raised the default to 8; renamed the InputParameter
label from "Stop buffer beyond level" to "Stop buffer beyond swing/level" to reflect its dual role
since the swing-stop change. **Note**: changing a code default does NOT retroactively update an
already-running instance's saved settings — told the operator to bump it directly in the settings
panel for immediate effect, this default only matters for a future fresh attach.

#### TENTH, ELEVENTH, TWELFTH ISSUES, 2026-09-28 (same day) — a naked position and a cooldown gap

All three found from ONE live sequence the operator flagged: "its like also working against it
self going into stacked orders it looks like," then, after digging into the log together: "this
time it worked out but... after it hit tp it opened another order instantly with no stop loss."
The operator stopped the strategy while this was investigated.

**What the log actually showed**: a short closed via target at 15:36:25.77. About 200ms later, a
BRAND NEW short entry fired and filled at 30,510.75. Its protective stop request (Buy Stop @
30,503.00) was **refused outright by Rithmic** — only the target got placed. That position ran
with a target and ZERO stop-loss until it closed.

**Tenth — wrong-side swing stop**: 30,503.00 sits BELOW the short's own entry (30,510.75), which
is backwards for a stop meant to protect a short (it needs to sit ABOVE entry). Root cause:
`SwingTracker.LastSwingHigh` only updates when a NEW swing high actually confirms — after a strong
one-directional rally with no pullback long enough to confirm a fresh pivot, the tracked "swing
high" was stale, already sitting below where price had since rallied to. Using it produced a stop
on the wrong side of the market entirely, which the broker correctly refused.

**Fix**: `ComputeSwingStop` now takes the trade's own current/entry price and validates the
computed stop is actually on the CORRECT side of it before returning it — a stale/wrong-side swing
is rejected outright (returns null), falling back to the caller's own level/rejection-bar
calculation instead, which is correct-side by construction.

**Eleventh — no safety net when a stop is refused for ANY reason**: even with the tenth fix,
nothing guarantees some OTHER refusal reason (an exchange price-band check, for instance) could
never happen again. `PlaceProtectiveOrders` now treats "the stop leg failed" as reason to close
the position IMMEDIATELY (`position.Close()`) rather than continue on to place the target and
leave the position running unprotected — this strategy has no dry-run gate and no human watching
every signal, so a position with zero stop is never an acceptable state to leave running, for any
reason.

**Twelfth — the cooldown never measured time since a CLOSE, only since an entry**:
`MinBarsBetweenEntries` only ever compared against `lastEntryBarIndex`, set when an entry was
PLACED — never updated when a position CLOSED. A trade that rides for a while before hitting
target can easily have already cleared that cooldown by the time it closes, leaving nothing to
stop a fresh signal from firing the instant the position goes flat — exactly what the ~200ms gap
in the log showed. **Fix**: `Core_PositionRemoved` now also resets `lastEntryBarIndex` to the
current bar the moment a position closes, so a new entry needs `MinBarsBetweenEntries` bars from
whichever happened more recently — the last entry OR the last close.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live** — given the eleventh
fix specifically exists to catch failures this codebase hasn't fully anticipated, the NEXT live
signal chain (entry → close → next signal) is the real test of whether the cooldown gap and the
wrong-side-stop validation both hold up together.

#### THIRTEENTH ISSUE, 2026-09-28 (same day) — breakeven buffer too thin to survive real slippage

Operator, after the twelfth fix's very next live trade: "it just did an order that was so tight
that i actually lost money because of fees when it moved the stop loss and the trade had such a
small area it wasnt really worth it." Pulled the exact fill sequence from the log: entry (short)
filled at 30,521.5; `CheckBreakeven` correctly triggered at 17.0 ticks profit (exactly 50% of the
trade's own 34-tick risk, confirming `BreakevenTriggerRiskPercent` itself worked as designed) and
moved the stop's trigger to 30,520.00. When price came back and the stop fired, being a
STOP-MARKET order, it does NOT guarantee a fill at its own trigger price — the actual fill landed
at **30,521.75**, seven ticks of adverse slippage that alone put the exit WORSE than the original
entry, before fees were even counted.

**This was not a bug in the breakeven logic itself** — `CheckBreakeven` computed and triggered
correctly. The problem was `BreakevenBufferTicks` (3) having no real margin against ordinary
stop-order slippage on a fast move, on top of the round-turn fees it was already meant to cover.
**Fix**: widened the default to 15. A bigger buffer gives up more of the original risk before a
trade counts as "locked in," but a thin one that gets erased by a single fast fill isn't actually
protecting anything — this is a judgment call the operator can retune further from their own
observation of typical slippage on this instrument/connection.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live.**

#### FOURTEENTH ISSUE, 2026-09-28 (same day) — technically-valid but absurd risk:reward

Operator, on the very next live trade after the thirteenth fix: "also playing off the 1min poc
this makes no sense the rr for this trade is super negative," then, once it was clear which trade
they meant: "what is this risk to reward here this is crazy." The trade's stop passed the tenth
fix's own wrong-side check (it WAS on the correct side of entry) but sat roughly 84 points away —
an unretraced rally had left the last confirmed swing low far below current price, and nothing in
`ComputeSwingStop` or `MinStopDistanceTicks` catches "correct side, but stale and absurdly far." The
resulting trade risked ~84 points to make ~15.25 — roughly 1:5.5 AGAINST the trade.

A maximum stop-distance cap was considered and rejected: an earlier, legitimate live trade had
risked 118 ticks for a ~2.9:1 reward, and a distance cap alone can't distinguish that trade from
this one — both are "wide," only one is proportionate. **Fix**: `PassesRiskRewardFilter` checks
the actual economics directly — `reward / risk >= MinRewardRiskPercent / 100.0` — computed fresh
from whatever stop/target the rest of the pipeline produced, regardless of what produced either
number. Applied as the LAST gate before any entry, in both the DOM/absorption/IFVG pathway
(`TryEnter`) and the POC-rejection pathway (`PlacePocEntry`). Default 100 (require at least 1:1);
0 disables the check entirely.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live** at the time this was
written; confirmed on a LATER live trade the same day (a long that ran to +$50.68 off a current-move
POC entry with two prior breakeven moves logged), which is what prompted the fifteenth feature below.

#### FIFTEENTH FEATURE, 2026-09-28 (same day) — continuous trailing stop after breakeven

Operator, watching that same well-in-profit long keep climbing past its second breakeven move:
"does it ever trail the stop loss after it keeps going in profit?" — answer: no, `CheckBreakeven`
was always a deliberate ONE-TIME move (the operator's own earlier choice via `AskUserQuestion`, over
a continuous trail). The operator then proposed a specific design: "maybe we should trail to the
bottom of the previous candle once its in profit already like its already trailed the stop into
profit and it keeps going up like this." Immediately validated by doing exactly that BY HAND on the
same live trade: "like were i just moved the stop manually worked wonders price came down to it but
never hit it because that is the support area for the continue push up" — and summarized the actual
goal as "look for the point were we can move the stop more into profit but avoid being filled."

**Built as `CheckTrailingStop`**, layered on top of (never instead of, never before) the existing
one-time breakeven move:
- Gated on `TrailAfterBreakevenEnabled` AND `this.breakevenMoved` already being true — this never
  runs until the one-time breakeven step has already fired.
- Runs once per newly-closed chart bar (the same `latestClosedBar` event `CheckPocRejection` already
  reacts to, called right alongside it in `RunPoll`), not every poll — the reference level (the
  bar's own low/high) only changes when a bar actually closes.
- Candidate stop = that bar's own Low (long) / High (short), offset by `TrailBufferTicks` (default
  4) beyond it — same "trail behind a level price already respected" idea the operator validated
  manually, deliberately NOT a tight ATR/percentage-based trail, so an ordinary pullback that
  retests support/resistance doesn't stop the trade out of its own trend.
- **Ratchet-only**: the candidate is only applied if it's strictly more favorable than the stop
  order's current `TriggerPrice` (higher for a long, lower for a short) — a worse or equal candidate
  is silently skipped, so this can never give back ground the breakeven move or an earlier trail
  step already locked in.
- Same benign-race handling as `CheckBreakeven`'s own modify: a failed `ModifyOrder` is logged as
  informational (not an error) if the position has already closed by the time the request reaches
  the broker, since that's a race with the trade finishing first, not a real failure.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live.**

#### SIXTEENTH FEATURE, 2026-09-28 (same day) — explicit breakeven on/off checkbox

Operator: "is there a way we can have a button to turn off break evens." `BreakevenTriggerRiskPercent`
already doubled as an off-switch at 0, but that's a numeric-field trick, not an obvious toggle.
Added `BreakevenEnabled` (plain checkbox, default true) gating `CheckBreakeven` directly, alongside
(not replacing) the trigger's own 0-disables behavior.

Asked explicitly via `AskUserQuestion` whether the new trailing stop (fifteenth feature, above)
should keep depending on breakeven having fired, or gain its own independent profit trigger so it
could still run with breakeven off. **Operator's choice: keep it dependent** — turning
`BreakevenEnabled` off also means the stop will never trail for that run, since `CheckTrailingStop`
only activates once `this.breakevenMoved` is true. Simpler behavior, explicitly traded off against
losing trailing whenever breakeven itself is disabled.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live.**

#### SEVENTEENTH FEATURE, 2026-09-28 (same day) — Asia/London/New York session toggles

Operator: "i also need the ability to choose to trade in asia and london and ny sessions so i need
to be able to turn on and off sessions" — the standard order-flow session breakdown, directly tied
to the strategy's own long-term direction (see `finchdomscalp-orderflow-vision` memory).

**Replaced** the old single RTH on/off window (`RthOnly`/`RthStartHour`/`RthEndHour`, which was
really just a crude stand-in for "NY session only") with three independently toggleable sessions,
each with its own enable checkbox and start/end hour (ET, same `SessionZone` every other
time-of-day check in this file already uses):
- `SessionFilterEnabled` — master switch, default OFF (24h trading, matching this strategy's
  behavior before this feature existed; a non-breaking default, same reasoning as every other
  toggle added this session).
- Asia: default ON, 19:00–04:00 ET (spans midnight — Tokyo/Sydney open).
- London: default ON, 03:00–12:00 ET (London open through the NY overlap).
- New York: default ON, 08:00–17:00 ET (the full NY futures session — wider than the old RTH
  default of 9–16, which was specifically equity regular hours).

`IsInAllowedSession` (replaces `IsInRth`) returns true whenever the filter itself is off, or the
current hour falls inside at least one ENABLED session's window — sessions are OR'd together, so
enabling Asia+London lets a signal through during either, not just their overlap. `IsInHourWindow`
handles the midnight-wraparound case (start hour > end hour) that a plain `hour >= start && hour <
end` comparison can't. Both entry pathways (`TryEnter`, `CheckPocRejection`) gate on this exactly
where they used to gate on `RthOnly`.

**Verified 2026-09-28 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live.**

#### EIGHTEENTH FEATURE, 2026-09-29 — broader trend filter, from a full overnight loss review

Operator: "review some of the trades from last night in the logs and see what we could have done
better because we are down -130ish over the night." Pulled every `FinchDomScalp`-tagged `Trade`
event from the platform's own Serilog (`C:\Quantower\Logs\Serilog\20260929.slog`) and paired them
into round trips against the strategy's own signal log: **20 round trips, 9 wins (+$313), 11
losses (-$419), net -$106 gross** (matches "-130ish" once commissions are included).

**One pattern caused almost all of it**: from 08:07 to 10:52, the DOM/absorption pathway
(`TryEnter`) took 9 separate short entries, ALL anchored on the same resting ask level around
30635, while price was in a sustained overnight rally from ~30500 to past 30700. Only 3 of those 9
shorts won (+$111.50 combined); the other 6 lost -$278.00. The signal log's own "absorbed" count
for that level kept climbing across every re-entry (201 → 246 → 377 → 428 → 439 → 457 → 513 → 573 →
604) — read by the entry logic as growing conviction to short, when a level that keeps refilling
while price keeps climbing through it is actually the opposite signal. Meanwhile every LONG entry
anchored on the opposing bid level (trading WITH that same rally) went 4-2 for +$124.50 net.

`PassesDeltaFilter` (the existing hard block) was already wired into this pathway, confirmed in
code — it just wasn't enough: its rolling window (default 8 bars) is local enough to clear on
almost any short pullback, even inside an hours-long trend, so nothing stopped the pathway from
re-fading the same level over and over through a directionally one-sided night.

Presented the finding and three candidate fixes via `AskUserQuestion` (a broader trend filter, a
per-level cooldown/blacklist, or simply widening the existing delta window) — **operator chose the
broader trend filter**. Built as `PassesTrendFilter`, reusing the exact same `DeltaTracker`
mechanism as `PassesDeltaFilter` (no new engine needed — `RollingDelta` already accepts any
lookback), just with its own much longer window (`TrendDeltaLookbackBars`, default 60 bars vs. the
local filter's 8) and its own independent on/off switch (`TrendFilterEnabled`, default on) — either
filter, both, or neither can be active, since they answer different questions ("did the last few
bars just react against me" vs. "is the whole session running against me"). Applied at every call
site `PassesDeltaFilter` already was (the DOM/absorption pathway and all three POC pathways).
`deltaTracker` construction and the `NewLast` tick subscription gate both extended to run whenever
EITHER filter is enabled, not just the local one.

**Verified 2026-09-29**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c
Release` — 0 errors. Deployed folder still clean. **NOT YET verified live** — the real test is
whether a future session with a similarly strong one-directional trend stops producing this same
repeated-fade pattern.

#### NINETEENTH FEATURE, 2026-09-29 (same day) — DOM/absorption becomes POC confluence, not its own trigger

A follow-up loss review (this time from `C:\Quantower\Logs\Serilog\` and the Quantower Trades
panel directly, cross-referenced chronologically) found something the earlier gross-total review
missed: last night had TWO separate 5-loss-in-a-row streaks (-$214 and -$140), and — more useful —
**8 of the roughly 10 losing round trips came from the standalone DOM/absorption pathway
(`TryEnter`)**, virtually all of them re-fading a level price had already moved well past before
the entry even fired. The operator: "would it make more sense to use the absorption levels and
resting orders as a confluence to the poc trading instead of its own trading since it doesnt work
out really well on its own?"

Confirmed via `AskUserQuestion` (two questions): **remove `TryEnter` entirely** (no standalone
DOM/absorption entry trigger of any kind going forward) and make a qualifying resting-order level a
**hard requirement** inside the POC-rejection pathway, not a soft confidence boost.

**Built**:
- `HasAbsorptionConfluence(side, poc, tickSize, levels, out confirmingLevel)` — a qualifying level
  (same `MinLevelSize`/`AbsorptionStrongContracts` thresholds the old standalone pathway used) must
  sit within `PocAbsorptionProximityTicks` (new, default 10) of the POC price, on the side that
  actually defends the rejection direction (a BID for a bullish/Buy rejection, an ASK for a
  bearish/Sell one). Returns the nearest qualifying level, not just a bool, so the trade's own log
  line can name which order flow actually backed it.
- Added as a new hard `&&` condition alongside the existing delta/trend filters in all three POC
  pathways (`CheckPocRejection`) — current-move, 5m, and 15m all now require it independently.
- `TryEnter` removed entirely, along with its call site in `RunPoll` and the now-dead
  `IfvgProximityTicks` parameter (IFVG alignment was only ever checked inside `TryEnter`; IFVG
  zones are still consumed elsewhere, as a target candidate in `TryComputeTarget`, unaffected).
  `CheckPocRejection` is now the only entry trigger in this strategy.

**Related, same conversation** — the operator flagged a second issue from a live chart screenshot
while this was in progress: "i also see this were the stop is right ontop of the resting orders
which is were price normaly wants to go to pick up orders so it should be a few ticks past that so
if it comes and touches it doesnt stop us out but we need to be out if it blows through those
orders." A resting order sitting right where the stop naturally lands is exactly where price is
likely to go to sweep that liquidity — not evidence the trade is wrong, just normal order-flow
behavior — so a stop parked directly on top of one risks getting clipped by a bare touch rather
than a genuine break. **Built** `ExtendStopPastRestingLevel(stopPrice, isLong, tickSize, levels)`:
scans for a qualifying resting level (same thresholds) within one `StopBufferTicks` width of the
already-computed stop, on the side that would defend against it, and pushes the stop
`StopBufferTicks` further PAST that level instead — only ever widening the stop, never tightening
it. Called in `PlacePocEntry` right after the existing `EnforceMinStopDistance` step.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live** — this is a genuine
architecture change (one fewer entry pathway, a new hard gate on the only remaining one), so the
real test is both trade FREQUENCY (confluence + POC rejection lining up simultaneously may be rare)
and whether the quality bar this adds actually holds up.

#### TWENTIETH FEATURE, 2026-09-29 (same day) — observability: signal-skip logging + heartbeat

Direct follow-up to the confluence change above, since it added several new stacked gates in one
pass: "is there a way to show logs on if some of the conditions are not met so i know its working
becuase it hasnt gotten in a trade in a while." Pure observability — neither addition touches
trading logic.

- **`[Signal skipped]` logging** (`TryGatePocSignal`, `LogPocSignalSkipped`): once a genuine POC
  rejection is actually detected (a real touch-and-close-beyond-buffer pattern with a confirmed
  prior approach — not routine chop), the remaining gates (swing size for current-move only, local
  delta, trend, absorption confluence) are now checked one at a time instead of one big `&&` chain,
  and the FIRST one that fails logs exactly which it was and why (e.g. the actual rolling delta
  value, or that no absorption sat within `PocAbsorptionProximityTicks`). Only fires on a genuine
  near-miss — a rejection pattern that almost became a trade — never as noise on an ordinary bar
  where nothing was ever close.
- **`[Heartbeat]` logging** (`CheckHeartbeat`, `HeartbeatIntervalMinutes` — new, default 15 min,
  0=off): a periodic snapshot (current POC levels, local/trend delta readings, session-filter
  state, minutes since the last trade) logged on a fixed interval regardless of whether any
  rejection pattern has even been attempted — this is what actually answers "is the loop alive" for
  a session where price never even approaches a POC, which the skip-logging above can't cover since
  it only fires once a rejection is already detected. Skipped entirely while a position is open —
  the open position is already proof of life.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean.

#### TWENTY-FIRST ISSUE, 2026-09-29 (same day) — confluence proximity confirmed too tight, widened

The new observability logging (nineteenth/twentieth features above) did exactly what it was built
for: within an hour of going live, "did we make the restrictions to tight now" was answered with
real data instead of a guess. The skip log showed a strongly trending session (60-bar trend delta
around -2181) where Buy rejections were correctly blocked by the trend/delta filters (working as
designed — don't fight a real trend), but Sell rejections ALIGNED with that same trend were
clearing delta/trend fine and dying specifically on "no qualifying absorption within 10 ticks" —
zero trades fired all session, and the confluence gate (`PocAbsorptionProximityTicks`, added
earlier the same day at a default of 10) was the confirmed bottleneck, not the directional filters.

Confirmed via `AskUserQuestion`: widen the proximity rather than touch `MinLevelSize`/
`AbsorptionStrongContracts` (those thresholds also define "real absorption" everywhere else, so a
proximity change is more targeted). **`PocAbsorptionProximityTicks` default changed 10 → 20.**

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **Reminder**: this is a CODE default change
only — the live instance's own saved setting (10, from earlier the same day) will NOT pick this up
automatically; Quantower persists parameter values by name across restarts regardless of what the
constructor default changes to (same lesson as the breakeven-buffer incident). Update it directly in
the strategy's own settings panel, or detach/reattach fresh, to actually apply 20 to the running
instance.

**IMPORTANT — direct file-edit attempt failed**: tried editing the running instance's own saved
`info.xml` directly on disk (strategy was confirmed STOPPED at the time) to apply this plus the
breakeven-buffer and cooldown fixes without making the operator retype them in the UI. Confirmed
this does NOT work: Quantower held its own in-memory copy of the old settings and overwrote the
on-disk edit the moment the strategy was started again — all three edited values reverted to their
pre-edit numbers. **Settings changes must go through the strategy's own settings panel in the UI;
direct `info.xml` edits do not survive a start, even from a cleanly stopped state.**

#### TWENTY-SECOND ISSUE, 2026-09-29 (same day) — local delta filter structurally fights reversal entries, disabled by default

Almost immediately after the confluence-proximity widen above, the new skip-log showed the LOCAL
(8-bar) delta filter blocking nearly every genuine rejection attempt: "it keeps saying local delta
blocks everything is the delta really important to this trading style?"

Diagnosed as a structural mismatch, not a tuning problem: a POC rejection is a REVERSAL bet — it's
betting price is about to turn AWAY from what it was just doing. `PassesDeltaFilter` requires the
last `DeltaLookbackBars` (8) bars' order flow to ALREADY agree with the NEW direction before letting
the trade through. But at the exact moment of a genuine rejection, recent delta is still going to
reflect the OLD, about-to-reverse direction almost by definition — that's what a turning point looks
like. This filter fits a continuation/trend entry far better than the reversal entries this strategy
actually trades (especially since `TryEnter`, the one pathway that WAS a continuation-style trade,
was removed the day before).

Confirmed via `AskUserQuestion`: **`DeltaFilterEnabled` now defaults to false.** `TrendFilterEnabled`
(60-bar window) stays on by default — it asks a different, still-valid question ("is the whole
SESSION running against this trade"), which doesn't have the same immediate-pre-reversal conflict.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. Same caveat as every other default change
today: this needs to be applied through the strategy's own settings UI on the running instance, not
assumed to take effect automatically.

#### TWENTY-THIRD FEATURE, 2026-09-29 (same day) — absorption confluence on/off checkbox

Even after widening the confluence proximity (twenty-first issue) and disabling the conflicting
local delta filter (twenty-second issue), absorption confluence kept rejecting trades the operator
felt should have gone through: "it keeps rejecting trades based on absorption has this always been
apart of the strategy?" (answered: absorption itself has been part of the strategy since
2026-09-25, but only as confirmation for the now-removed standalone `TryEnter` pathway — making it
a HARD requirement on the POC pathway, the only pathway left, was today's change). Immediate
follow-up: "lets make a check box to add that confluence so to enable or disable it."

**Built** `PocAbsorptionConfluenceEnabled` (plain checkbox, default true — does NOT silently revert
the nineteenth-feature architecture change). When off, `HasAbsorptionConfluence` is bypassed
entirely (always passes, `confirmingLevel` stays null) and a POC rejection can fire on the
delta/trend/R:R gates alone, exactly how the POC pathway worked before absorption confluence became
a requirement. Gives the operator a direct way to fall back to "POC-only" trading without touching
`PocAbsorptionProximityTicks` or reverting the pathway-removal decision.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. Same UI-application caveat as every other
default/parameter change today.

#### TWENTY-FOURTH FEATURE, 2026-09-29 (same day) — absorption-miss diagnostics + SDK path break

Operator: "what qualifies as an absorption level for a trade to be triggered because maybe we have
that value to high" — answered by walking through `RestingOrderEngine.Reconcile`'s actual mechanics
(a level needs BOTH live `Current >= MinLevelSize` right now AND cumulative `Absorbed >=
AbsorptionStrongContracts` built up over repeated tests at that exact price, not just one big
resting order). Follow-up: "we need to narrow this down to see if the poc never gets near the
absorption level and if this confluence is not needed or making way to strict."

**Built**: `HasAbsorptionConfluence` now returns a `missDetail` string on failure — the nearest
same-side level's actual distance from the POC plus its real current/absorbed numbers next to the
configured thresholds, or a plain "no level tracked at all on this side" if even that's missing.
`[Signal skipped]` now surfaces this instead of the old generic "no qualifying absorption within N
ticks" — e.g. `nearest BID is 34 ticks from POC (current=120, absorbed=85; needs current>=100,
absorbed>=200, within 20 ticks)`, which immediately tells you whether the miss is a PROXIMITY
problem (POC never gets near any resting order) or a THRESHOLD problem (something's there but
under-sized) — the two very different explanations the operator was trying to separate.

**Unrelated build break found and fixed along the way**: `dotnet build` failed with `CS0246: The
type or namespace name 'TradingPlatform' could not be found` — Quantower had auto-updated from
v1.147.4 to v1.147.5 sometime during tonight's restarts, and `finchDomScalpStrategy.csproj`'s SDK
`HintPath` was still hardcoded to the old version's now-nonexistent path. This is a known recurring
risk in this project (flagged in the original build plan: "confirm the live version under
`C:\Quantower\TradingPlatform\` at build time"). Fixed by updating the `HintPath` to
`v1.147.5\bin\TradingPlatform.BusinessLayer.dll`. Worth checking this path first if a FUTURE build
ever fails with the same `TradingPlatform` namespace-not-found error, before assuming a real code
problem.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean.

#### TWENTY-FIFTH FEATURE, 2026-09-29 (same day) — TryEnter restored as a second, independent pathway

Same-day reversal of the nineteenth feature's "remove `TryEnter` entirely" decision. Sequence: "is
even having the absorption level close to the poc even a valid trade" (a fair challenge to whether
POC rejection needs absorption confluence at all) → "should we have both in seperate strategies
working indepentally" → clarified to "like have both in the single strategy in quantower but let
them both fire on their own but only have 1 order open at a time" → clarified further "also be able
to turn each one of them off if i want" → and the conceptual split that settled the design: "poc
should be for quick scalps and purely scalping and absorption levels should tell us its going the
other direction."

**Built**: `TryEnter` restored as written before its removal, PLUS everything added to the POC
pathway since (`ExtendStopPastRestingLevel`, matching `[Signal skipped]` diagnostics via a new
`LogAbsorptionSignalSkipped`). Runs as a genuinely separate pathway from `CheckPocRejection` — not
a confirmation gate on it, which is what the nineteenth feature had turned absorption into.
`HasAbsorptionConfluence`/`PocAbsorptionConfluenceEnabled` on the POC side are UNCHANGED and still
fully available; the two roles now coexist rather than one replacing the other.

Two new independent toggles, one per pathway: `AbsorptionEntryEnabled` (new, default true) for
`TryEnter`; POC already had the equivalent (all three of `PocCurrentMoveEnabled`/`Poc5mEnabled`/
`Poc15mEnabled` off = POC fully disabled), so no new parameter was needed there. `IfvgProximityTicks`
(removed in the nineteenth feature as dead code) is also back, since `TryEnter`'s own IFVG-alignment
requirement needs it again.

Both pathways share every existing risk gate (one position at a time, cooldown, daily-loss/
drawdown/trade-count limits, session filter) — `RunPoll` calls `TryEnter` first, then
`CheckPocRejection`; whichever clears its own conditions first in a given poll wins, and both check
"already in a position" before doing anything, so only one trade is ever open regardless of which
pathway produced it.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live** — `TryEnter` in this
form (with the trend/R:R filters and resting-liquidity stop extension it never had originally) has
never actually traded; its previous incarnation is what produced most of one bad night's losses, so
this is a genuine re-test of the same concept with more protection around it, not an assumption it's
now safe.

#### TWENTY-SIXTH FEATURE, 2026-09-29 (same day) — near-level pushback protection, independent of breakeven

A live trade ran deep into profit, grazed its target, then reversed all the way back toward its
original stop with nothing protecting it — the operator caught this live and closed it manually:
"if i didnt just manually close it i would be in the negative right now going to my stop loss." The
root cause: the operator has `BreakevenEnabled` off ("i found the break even feature losses me
money in the long run so i have that turned off"), and `CheckTrailingStop` only ever activates
*after* breakeven fires — so with breakeven disabled, NEITHER mechanism engages, leaving a
favorably-moving trade with zero dynamic protection all the way from entry to the original stop.

**Built as a THIRD, fully independent risk mechanism** (`CheckNearLevelPushback`), unaffected by
whether breakeven/trailing are on or off: tracks the best price reached toward target and the worst
price reached toward stop since entry. If either extreme came within `NearLevelTicks` (default 15)
of its own level, and price has since reversed away from that extreme by `PushbackCloseTicks`
(default 15), the position closes immediately at market — protecting the gain on the target side
("if there is big push back from the take profit line it should just close out instead of running
all the way back"), and taking the smaller loss/scratch rather than risking a full round-trip back
to stop on the other side, per the operator's own explicit symmetric follow-up: "if it edges stop
loss like that with pressure going back in the other direction it should just close it."

Looks up the REAL current stop/target order prices on demand (`FindProtectiveStopOrder`, plus a new
matching `FindProtectiveTargetOrder`) rather than the signal-time `pendingStopPrice`/
`pendingTargetPrice`, so this stays correct even if breakeven/trailing has since moved the stop —
this was a deliberate design choice for forward-compatibility, even though the operator's own
current setup has both of those off.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live** — this closes at
market on trigger, which carries its own slippage risk on a fast reversal; watch the first few
triggers closely to confirm the exit price is actually better than what letting it run would have
produced.

#### TWENTY-SEVENTH ISSUE, 2026-09-29 (same day) — near-level pushback rebuilt as per-leg percentages, not flat ticks

Caught immediately, before ever going live: "now will this mess up the smaller scalps being 15
ticks away and a pull back from what i set to 10 ticks" — the exact same class of bug already found
and fixed once for `BreakevenTriggerRiskPercent` ("a fixed tick count stopped making sense once
stops became swing-based and variable"). `NearLevelTicks`/`PushbackCloseTicks` as flat tick counts
meant "near target" on a wide swing-sized target (80+ ticks away) but triggered almost immediately
on a tight scalp target (20-30 ticks away), where 10-15 ticks is already most of the move — turning
completely normal small pullbacks into premature closes.

**Fixed the same way breakeven was fixed**: `NearLevelPercent`/`PushbackClosePercent` (both
percentages, default 25%) now scale against EACH LEG'S OWN entry-to-level distance — entry-to-target
for the target-side check, entry-to-stop for the stop-side check — computed fresh each poll from
`position.OpenPrice` and the real current stop/target order prices. A tight scalp target and a wide
swing-based stop on the SAME trade are each judged against their own length now, not a one-size-
fits-all tick count.

**Verified 2026-09-29 (same day)**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj
-c Release` — 0 errors. Deployed folder still clean. **NOT YET verified live**, same as the feature
this replaces.

---

#### TWENTY-EIGHTH FEATURE, 2026-09-30 — NY open blackout, a minute-precision exclusion window

A live Buy fired right at the 9:30 ET NY open with a swing-based stop 68+ points/274 ticks away
(entry 30751.50, stop 30683.00 — screenshot showed net -$33.32, -67 ticks open). The operator: "also
in my current strategy thats running the finch one i dont want to trade the first 15mins of the ny
open because it has entered me into a stupid trade with a massive stop loss." The opening minutes of
RTH are exactly when a swing-based stop (computed off whatever overnight/pre-market range happens to
exist) is most likely to be absurdly wide, since the "recent swing" reference hasn't reset to a
genuine intraday range yet.

The existing `SessionFilterEnabled`/`NySessionStartHour`/`NySessionEndHour` + `IsInHourWindow` is
hour-only and serves a different purpose (a broad session ALLOW-list) — it can't express a precise
9:30-9:45 EXCLUSION carved out of an already-allowed session without either turning off entirely or
blocking the whole 9:00-10:00 hour. Added a genuinely separate, minute-precision blackout:
`NyOpenBlackoutEnabled`/`NyOpenBlackoutStartHour`/`Minute`/`NyOpenBlackoutEndHour`/`Minute` (default
on, 09:30-09:45 ET) and a new `IsInNyOpenBlackout()` — ported the exact minute-precision window-check
logic from `oceansStackStrategy`'s own `IsInWindow` helper (which was built for the identical reason:
`IsInHourWindow` isn't precise enough for a 30-minute boundary there either). Checked as an
additional gate alongside `IsInAllowedSession()` at both entry pathways (`TryEnter` and
`CheckPocRejection`), and surfaced in the heartbeat's own `Session=` status line.

**Verified 2026-09-30**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live.** Same "code default doesn't retroactively update an
already-saved instance" caveat as every other setting change — the operator may need to set this
explicitly in the currently-running instance's own settings panel.

#### TWENTY-NINTH FEATURE, 2026-09-30 (same day) — daily profit target, the profit-side mirror of the daily loss limit

The operator: "lets add in a feature to set a profit target and once its met on the day it stops
trading." New `DailyProfitTarget` ($, 0=off, default off) checked in `CheckRiskLimits` against
realized + unrealized daily P&L — the profit-side counterpart to the existing `MaxDailyLoss`.

**One deliberate structural difference from `MaxDailyLoss`/`MaxDrawdown`**: those two only ever run
while a position is OPEN (`CheckRiskLimits` returns immediately if `MyPositions()` is empty, since
there is nothing to close otherwise) — a gap that doesn't matter for a LOSS limit (if you're flat,
you're not losing more) but would defeat the entire point of a PROFIT target, whose job is to keep
you flat and refusing new entries for the rest of the day. Restructured `CheckRiskLimits` so
unrealized P&L is computed once up front (0 while flat), the new profit-target check runs against
`dailyPnl + unrealizedPnl` BEFORE the "no positions, nothing to do" early return, and only the
existing loss/drawdown checks stay gated behind it. `profitTargetHit` (mirroring `dailyLimitHit`) is
checked alongside the existing `dailyLimitHit || drawdownLimitHit || tradesLimitHit` guard at both
entry pathways (`TryEnter`/`CheckPocRejection`), and reset in both `OnRun` and `CheckSessionReset`
(new EST day).

**Verified 2026-09-30**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live** — off by default, so no behavior change until the operator sets
a target explicitly.

---

#### THIRTIETH FIX, 2026-10-01 — POC demoted to last-resort target, not a peer competing on distance

Live win/loss investigation (operator: "the win loss ratio needs to be improved"), diagnosed by
pulling the per-instance strategy log's own `[Signal]` lines (which carry the anchor AND the
`targetSource` string) and cross-referencing each against its real Serilog fill outcome for one full
trading day. Broken down by WHAT THE TARGET WAS:

| Target reason | Trades | Win rate | Net |
|---|---|---|---|
| Opposing IFVG zone | 14 | 42.9% | +$214.50 |
| Opposing DOM/UA level | 5 | 40.0% | +$48.00 |
| **Another POC** (5m or current-move) | 5 | **0%** | **-$49.50** |

Every winning trade that day targeted a real structural level (an IFVG zone or a DOM/UA resting
wall); every trade that targeted a different POC instead lost, 5-for-5. Root cause in
`TryComputeTarget`: it gathered DOM/UA levels, IFVG zones, AND every enabled POC into one candidate
pool and picked whichever was NEAREST, full stop — a POC has no side or role of its own (just
wherever volume happened to cluster, unlike a resting-order wall or a gap), so a weak-but-closer POC
could steal the target slot from a stronger-but-slightly-farther real level purely on proximity.

Confirmed via `AskUserQuestion` over three options (demote POC to last-resort / remove POC-as-target
entirely / wait for more days of data) — operator chose **demote to last resort**. `TryComputeTarget`
rewritten as a two-tier search: DOM/UA + IFVG candidates are tried first, nearest of those wins if
any qualify; a POC is only even considered when **none** of those qualify at all (not "none closer
than the POC" — none, period). Applies to BOTH entry pathways (`TryEnter`/`PlacePocEntry`), since
both share this one function.

**Verified 2026-10-01**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live** — based on one day's sample (24 trades); watch whether
POC-as-target trades (now rarer) keep the same losing pattern, or whether this was partly noise.

---

#### THIRTY-FIRST FIX, 2026-10-02 — near-level pushback's stop side turned OFF, no longer symmetric with the target side

The operator: "when it comes to the stop loss i dont want it to close if it edges the stop loss like
it does for the take profit." This REVERSES part of the near-level-pushback feature's own original
design (2026-09-29, see the TWENTY-SIXTH/TWENTY-SEVENTH entries above), which deliberately built the
stop side as the symmetric mirror of the target side.

The insight: the two sides were never actually equivalent in what a reversal MEANS. Price edging the
TARGET then reversing away is a genuine failure to break through — closing protects the gain. Price
edging the STOP then recovering away from it is the market defending the trade's own structure —
closing there risks cutting the position right as the real move starts working, exactly the scenario
the trade was placed to catch.

New `NearStopPushbackEnabled` (default **false**) gates only the stop-side branch inside
`CheckNearLevelPushback`; the target-side branch is unchanged and still governed solely by the
existing `NearLevelPushbackEnabled`. Kept as its own toggle rather than deleting the stop-side code
outright, matching this codebase's own established "a checkbox, not a revert" pattern (see
`PocAbsorptionConfluenceEnabled`'s own history) — the operator can turn it back on later without
anyone having to rebuild the mechanism from scratch.

**Verified 2026-10-02**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live.**

---

#### BACKUP, 2026-10-04 — `FinchDomScalpStrategy-Backup`, a frozen snapshot of the live strategy

Before adding a new market-structure entry gate, the operator asked for "a full backup of this
current strategy... i need a working backup of this incase i want to re-use it." A complete,
renamed copy of the live strategy — `Strategies/FinchDomScalpStrategy-Backup/
FinchDomScalpStrategy-Backup/` — taken as of right after the THIRTY-FIRST FIX above. Renamed
throughout (`AssemblyName`/`RootNamespace`/class name `FinchDomScalpStrategyBackup`/`StrategyTag`
→ `FinchDomScalpBackup`/display `Name` → `FinchDomScalpStrategy-Backup`/`OutputPath`/debug port
62744→62745) so Quantower loads it as a genuinely separate script with its own identity and deployed
folder — never colliding with the live strategy's own positions, orders, or settings, even if both
were ever attached at once. Same `<Compile Include>` sources from Finch-Lite's own folder as the
live strategy's own csproj.

**Not meant to run alongside the live strategy day-to-day** — it exists purely so the operator can
load this one back up if the market-structure experiment (below) doesn't work out, without anyone
having to reconstruct the pre-change strategy from git history or memory.

**Verified 2026-10-04**: `dotnet build FinchDomScalpStrategy-Backup/FinchDomScalpStrategy-Backup.csproj
-c Release` — 0 errors, same 28 pre-existing warnings as the live strategy. Deployed DLL confirmed at
`C:\Quantower\Settings\Scripts\Strategies\FinchDomScalpStrategy-Backup\`.

---

#### THIRTY-SECOND FEATURE, 2026-10-04 (same day) — structure filter: a PRICE-based hard block, distinct from the volume-based trend filter

The operator: "i feel like purely trading off the poc can be dangourus without really looking at the
market structure of the chart at that given time i have seen us try to reverse a trade a few time
just for the price to keep going in the direction so we are not following the momentum of the
price." Diagnosed by reading `PassesTrendFilter`'s own code: it sums buy/sell VOLUME over a rolling
window, which can sit flat or even disagree with the real trend during a slow, absorption-driven
grind to new highs/lows — no single aggressive push, so the delta sum never clearly flips, even
though price itself has unambiguously broken structure.

New `StructureFilterEnabled` (default **true**) and `PassesStructureFilter(Side)` — reuses
`SwingTracker`'s own fractal pivots (already running unconditionally for stop placement, no new
tracker needed): blocks a SELL if current price has already traded above the last CONFIRMED swing
high, blocks a BUY the mirror way against the last confirmed swing low. No swing confirmed yet this
run passes through, matching `PassesTrendFilter`'s own "insufficient data doesn't block" convention.
Wired into both entry pathways (`TryEnter`/`TryGatePocSignal`), logged via the same
`LogAbsorptionSignalSkipped`/`LogPocSignalSkipped` lines the other filters already use.

**Deliberately a first pass, not the full idea**: this only checks the single most recent swing each
side, not a genuine HH/HL-vs-LH/LL sequence (`SwingTracker` currently only remembers the LATEST
confirmed high and low, not a short history of each). A richer sequence-based version was discussed
and explicitly deferred — the operator asked to keep that idea in reserve in case this simpler
version doesn't work out, which is also why the `FinchDomScalpStrategy-Backup` snapshot above exists:
a clean rollback path that doesn't depend on reconstructing the pre-filter strategy from memory.

**Verified 2026-10-04**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live.**

---

#### THIRTY-THIRD FIX, 2026-10-04 (same day) — structure filter given its own higher-timeframe series, same-day catch

The structure filter above shipped and was live for well under an hour before a screenshot caught
it in the act: a run of `[Signal skipped] Buy ... blocked: structure filter (price already broke the
opposing swing)` lines firing repeatedly right before price rallied hard — exactly the "fighting
momentum" pattern the filter was built to prevent, except it was the FILTER doing the fighting. The
operator: "should we really be basing everything just off the 1min poc."

Root cause: the first version reused `SwingTracker` (built for stop placement), fed the chart's own
1-minute bars with `PocSwingPivotLookback` (default 3) — a pivot confirms with only 3 bars on each
side, so on 1-minute bars that's roughly a 7-minute window. That's noise-level, not genuine market
structure — a routine local dip gets tagged as "the" swing low, and the filter then reads an
ordinary pullback as "structure broken" and blocks the very entry that would have caught the move.

Fixed by giving the structure filter its OWN dedicated higher-timeframe series and its OWN
`SwingTracker` instance (`structureHistory`/`structureSwingTracker`/`structureBarsSeen`, drained by
a new `DrainStructureSwings()`), completely independent of the 1-minute tracker stop placement still
uses — same "own cursor, own history object" pattern the 5m/15m POC engines already established.
New `StructureFilterPeriod` InputParameter (default 5-minute, confirmed via `AskUserQuestion` as
configurable rather than hard-coded) picks the timeframe.

**Verified 2026-10-04**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live** — watch whether 5-minute swings still catch the same kind of
false block; `StructureFilterPeriod` can be raised to 15-minute live, no rebuild needed, if so.

---

#### THIRTY-FOURTH FEATURE, 2026-10-04 (same day) — profit momentum dying: a DOM-aware FOURTH risk mechanism

Live trade watched in real time: Buy at 31196.75, peaked around +$50 (~25 points), then gave it all
back to -$8.32 with NOTHING protecting it — breakeven wasn't enabled on that run, and near-level-
pushback's own target-side check never engaged (it only arms in the final 25% stretch toward target;
the target was 53 points away, so the protected zone only started ~13 points short of it, and the
trade topped out roughly 15 points before even reaching that zone). The operator: "it should be
looking at the dom for the levels its rejecting off of and know when a trade that is in profit is
dying out" — then, watching the SAME live trade: "i saw there was a larger resting order at the
level it topped at and reversed."

That live observation IS the mechanism: a FOURTH, independent check (`ProfitDecayEnabled`) that
reads the exact same `RestingOrderEngine` levels `TryEnter` already scans for entries, now pointed at
an OPEN position. Once a trade is in profit beyond `ProfitDecayMinProfitTicks`, if a large opposing
resting order (ask wall for a long, bid wall for a short — size at or above the existing
`AbsorptionStrongContracts` threshold) sits within `ProfitDecayLevelProximityTicks` of the best price
reached so far, AND price has pulled back off that peak by `ProfitDecayPullbackTicks`, the position
closes immediately — the DOM itself is the trigger, not a fixed percentage of distance to a target
that might be nowhere close.

**One shared-state fix along the way**: `bestPriceSinceEntry`/`worstPriceSinceEntry` used to only get
updated INSIDE `CheckNearLevelPushback`, gated behind `NearLevelPushbackEnabled` — meaning if that
unrelated toggle were ever off, this new feature would have silently gotten stale data. Extracted
into its own always-on `UpdatePriceExtremesSinceEntry`, called unconditionally every poll before
either feature reads it.

**Verified 2026-10-04**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors. **NOT YET verified live** — built directly from watching one real trade; worth confirming
it fires correctly (and not too eagerly) on the next few winners that stall out.

---

#### THIRTY-FIFTH FEATURE, 2026-10-05 — five eval-hardening changes from one overnight -$300 review

The operator: "review the trades from overnight and see what can be done better because we are down
300," then, after finding the structure filter couldn't be directly blamed (it can only ever block a
trade, never cause a loss): "i want this strategy to pass evaluations for me" — which reframes the
whole priority. For an evaluation, avoiding a ruinous stretch matters more than maximizing average
trade quality: one breach of a daily-loss or max-drawdown rule fails the eval regardless of how
profitable the strategy is on average. Then, mid-review: "even if we scalp for like 15-20$ wins makes
the strategy a true scalping strategy and more profitable than what we have going right now."

**Root-caused from real data, not guessed**: pulled every fill + signal from the overnight session
and found two concrete, confirmed patterns — not "targets too far, ran back to stop" (the operator's
own first guess, checked and found NOT to match: the two biggest losses had R:R sitting at 1.06:1
and exactly 1.0:1 and went straight to a full stop, no pullback-from-near-target involved).

1. **The SAME resting order at 31200 triggered 7 separate absorption-pathway entries** over ~3 hours
   as it kept absorbing more size (252→285 contracts) without ever actually holding — net -$87 from
   that one level. The exact repeated-re-shorting failure mode already documented once before in
   this file's own history (2026-09-29, a different level) — recurred because nothing in the code
   actually remembers "we already lost against this specific price today."
2. **Two near-1:1 R:R trades** (1.06:1, exactly 1.0:1) produced the two biggest losses of the night
   (-$80.50, -$69.50), both going straight to a full stop with no real margin for error.

**Five changes, all data-justified**:

- **`MaxTargetDistanceTicks`** (new, default 40 ticks = $20) — caps every computed target (IFVG/
  DOM-UA/last-resort POC/fallback) at this distance from entry, even when the qualifying level sits
  farther out. The scalp-mode change: consistent small wins instead of chasing whatever level
  happens to be 50+ points away. `TryComputeTarget` clamps the PRICE, not the candidate search — a
  real level still has to exist and qualify, it just doesn't need the full distance. This also
  forces stop distance to stay proportionate for a trade to clear the (unchanged) R:R filter,
  without a second explicit stop cap — one change, two effects.
- **`MinRewardRiskPercent`: 100 → 120** — modest margin increase, directly targeting the "barely
  clears 1:1" pattern behind the night's two biggest losses. Not raised further since the target cap
  above already does most of the proportionality work.
- **`MaxLossesPerLevel`** (new, default 2) — tracked per EXACT resting-level price
  (`lossesPerLevel`, reset daily). After this many LOSING entries against one specific level,
  `TryEnter` stops trading that exact level for the rest of the session. Required threading a new
  `pendingEntryLevelPrice` through `PlaceEntry` (null for POC entries, the level's own price for
  absorption entries) so `Core_TradeAdded` knows which level to blame at trade-close time.
- **Consecutive-loss cooldown** (new: `ConsecutiveLossCooldownEnabled`/`ConsecutiveLossThreshold`=4/
  `ConsecutiveLossCooldownMinutes`=30) — a classic risk-of-ruin control, proposed days earlier and
  finally built. Time-based, not signal-count-based. Tracked in `Core_TradeAdded` off the SAME
  per-trade realized-P&L sign the per-level tracker uses.
- **`MaxDailyLoss`: off → $300** — there was NO automatic circuit breaker before this; the overnight
  session that prompted this whole review ran to -$300 with nothing stopping it. **Explicitly a
  data-driven placeholder** (it's exactly what that session lost), not a real evaluation's own rule —
  needs tightening to the operator's actual eval limit, with real margin below it, once known.

**One more bug found and fixed along the way, unrelated to P&L**: `LogAbsorptionSignalSkipped` had
no de-duplication, unlike every other fault-reporting path in this file (`ReportPollFault`,
`CheckPocRejection`'s own skip logging). Reachable every poll (not bar-gated like the POC pathway),
a persistently-blocked level re-logged the identical line 4x/second — 26,703 lines in the one
overnight session reviewed. Fixed with the same `lastX`-compare-and-skip pattern already used
elsewhere.

**Verified 2026-10-05**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors (one real compile error along the way — `source` is an `out` parameter and C# won't allow
capturing it inside a local function; fixed by returning the clamped price and comparing it against
the original in the caller instead of assigning `source` from inside `ClampToMaxDistance`).
**NOT YET verified live** — five simultaneous changes from one review; worth watching the next
session closely rather than assuming all five land exactly as intended.

---

#### THIRTY-SIXTH FEATURE, 2026-10-05 (same day) — account-balance-based risk, restart-immune, with the real eval numbers

Immediately following the THIRTY-FIFTH entry above, the operator gave the real evaluation numbers:
"i have currently a 25k account with a 600 daily draw down and a 1k max loss limit and the current
max loss limit is set to 24,108.04 and the current account balance is 24,541.94." Working through
those numbers surfaced a much bigger problem than the $300/$2000 placeholders from the entry above.

**The bug**: `MaxDailyLoss`/`MaxDrawdown` were (and `MaxDrawdown` still is) checked against this
strategy's own internal counters — `dailyPnl`, `peakEquity`. Both reset to ZERO in `OnRun`, which
runs on every STRATEGY RESTART, not just at a new trading day. The operator restarts this strategy
to redeploy builds constantly — dozens of times across single sessions this week alone (visible
throughout this file's own changelog). A circuit breaker that forgets everything it knew the moment
the operator redeploys a fix is not a circuit breaker for an evaluation account.

**Prior art found and used**: `Indicators/ORB-IX/src/OrbIx.Core/Risk/AccountSnapshot.cs` had already
solved an adjacent version of this problem for the ORB-IX indicator, and documented one thing as
explicitly UNVERIFIED: whether `Account.Balance` already includes unrealized P&L intraday on this
connector, or only updates on a closed trade ("balance - start double-counts if it already carries
open profit"). The fix here sidesteps needing to know the answer: using `Balance_now -
Balance_at_day_start` directly, with NO separately-tracked unrealized P&L added on top, is correct
either way — if Balance already marks-to-market, the delta already reflects today's live P&L; if it
doesn't, the delta is exactly today's realized P&L. No double-counting risk in either case.

**Built**:
- **`AccountBalanceFloor`** (new, default $24,200.00 — the operator's own $24,108.04 real floor plus
  ~$92 margin) — a hard floor on the REAL, LIVE `Account.Balance`. Deliberately NOT an attempt to
  reconstruct the firm's own trailing-drawdown ratchet formula automatically (unconfirmed whether it
  uses intraday or EOD peaks, whether it caps at the starting balance — none of that is knowable from
  this codebase) — the operator updates this themselves from their own firm dashboard. **STICKY**:
  once breached, stays breached across both day boundaries AND restarts (persisted to
  `daily_risk_state.txt`, written next to the deployed DLL — a stable path across restarts/rebuilds,
  unlike the per-instance ScriptsData log folder which gets a new GUID every attach). Breaching a
  real trailing floor generally means the evaluation itself is over, not "pause for today."
- **`MaxDailyLoss`: $300 → $450** (75% of the real $600 daily limit — genuine margin, not a round
  guess) — and more importantly, its CHECK was rebuilt: `IsDailyLossLimitBreached()` recomputes LIVE,
  every call, from `Account.Balance - dayStartBalance`, never a cached/latched flag. `dayStartBalance`
  itself IS persisted (same state file), reloaded once per EST day per restart by
  `EnsureDailyBalanceState`, defaulting to "today starts now" if the file is new, unreadable, or
  stale from a prior day — never blocking or crashing on a read failure.
- **Old `dailyPnl`-based `MaxDailyLoss` check REMOVED** from `CheckRiskLimits`, fully superseded by
  the balance-based one. `DailyProfitTarget`/`MaxDrawdown` deliberately left on the OLD internal-
  counter approach — missing a profit-target stop a bit late, or having a same-session-only secondary
  drawdown layer, isn't dangerous the way a forgotten LOSS limit is.
- Both new checks wired into the entry gates too (`TryEnter`/`CheckPocRejection`) — a floor breach or
  daily-loss breach must block NEW entries, not just close an already-open position.

**Verified 2026-10-05**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors, 31 warnings (all pre-existing nullable-annotation-context style). **NOT YET verified
live** — specifically, the one thing AccountSnapshot.cs itself flagged as never confirmed (does
`Account.Balance` move intraday on this Rithmic connection, or only on a closed trade) is STILL
unconfirmed here — the design is correct either way, but worth watching the `daily_risk_state.txt`
file's own written values against what the account actually does over the next session to build real
confidence.

---

#### THIRTY-SEVENTH FIX, 2026-10-05 (same day) — orphaned positions ran with ZERO protection; position re-identification relaxed on bootstrap too

The operator: "i just manually closed out an order that was up $71.50 because the take profit was so
far away, I dont want these larger trades i want quick scalps." Investigation found the target cap
from earlier today WAS working correctly (the entry signal showed `target=31205.125 ... capped`, 10
points from entry) — the real problem was the position's own CLOSING fill carried an EMPTY Comment,
meaning this strategy instance never recognized the position as its own after a mid-session restart
and never armed a stop or target for it at all. A second, worse case the SAME session: two
Comment-empty short fills sat with literally no protection for ~11 minutes until a manual close
realized a **-$138.50 loss**. Both happened during a cascade of THREE restarts within about 90
seconds (14:10–14:13 ET) — exactly the kind of rapid mid-session redeploy this operator does
constantly, now confirmed to actually cost real money, not just a theoretical risk (this exact
mechanism was diagnosed and explained, unfixed, a few turns earlier in this same conversation).

**Root cause**: `IsMyPosition`/`IsMine`'s own BOOTSTRAP path (`resolvedSymbolId` still null, i.e.
right after a fresh restart) required an EXACT `Comment == "FinchDomScalp"` match with no fallback —
unlike the POST-bootstrap path, whose own doc comment already documents an "ACCEPTED RISK" of
adopting ANY position on the exact contract+account regardless of Comment. The bootstrap path was
stricter than the codebase's own already-accepted philosophy, for no good reason — and Comment
coming back empty on a position event is a repeatedly-observed platform quirk this file has hit
before (see the TENTH-issue-era history above `IsMyPosition`'s own doc comment).

**Fixed**: both methods now accept EITHER a Comment match OR an exact contract-id match
(`symbol.Id == this.CurrentSymbol.Id`) on the bootstrap path too — the same tradeoff the
post-bootstrap path already accepts, just no longer gated behind having bootstrapped once already.
A genuinely different symbol on the same connection/account still requires the Comment to match.

**A second bug found while fixing the first**: an ADOPTED position (one this instance never itself
signaled) leaves `pendingStopPrice`/`pendingTargetPrice` at their `OnRun`-reset default of **0** —
and `EnforceMinStopDistance`/`EnforceMinTargetDistance` would have passed 0 straight through (it's
"far enough" from the real fill to look like an already-qualifying price), producing a protective
stop order at price ZERO. `PlaceProtectiveOrders` now detects this exact state (`pendingStopPrice ==
0 && pendingTargetPrice == 0`) and falls back to a sane stop/target computed straight from the
position's own real fill (`MinStopDistanceTicks`/`FallbackTargetTicks`, the same shape `TryEnter`'s
own no-real-level fallback already uses), logging clearly that this was an adopted position being
given fallback protection rather than its own real signal data.

**Verified 2026-10-05**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors, 31 warnings (all pre-existing). **NOT YET verified live** — next restart-while-in-a-trade
should show a `[Risk] ADOPTED a position...` log line and a real (non-zero) protective bracket,
instead of today's silent, unprotected orphan.

---

#### THIRTY-EIGHTH FIX, 2026-10-05 (same day) — protective target silently refused by the broker ~2ms after reporting success; off-tick-grid prices from the `midPrice` anchor

The operator, screenshot of an open `2@31,214.50 -11.64 USD` position with only a STP order visible:
"this position never put a take profit." The strategy's own per-instance log said the opposite —
`[Order] protective stop=31204.5 target=31224.375 ... placed as separate orders.` — so this was
checked against Quantower's raw Serilog (`C:\Quantower\Logs\Serilog\20261005.slog`) around the fill,
not taken at either the operator's or the strategy's own word (see
`quantower-strategy-log-debugging` memory). The full sequence for order 41254858 (the Sell Limit
target, Qty 2 @ 31,224.38):

```
15:36:07.4905  Trading operation result: Success. Order Id: 41254858
15:36:07.4920  Order update: ... Sell, Limit, 0/2, Pr = 31,224.38, Opened
15:36:07.4926  Refuse — Description: bad price
15:36:07.4927  Order remove / Order history: ... Refused
```

The broker accepted the order, flipped it to "Opened" for about half a millisecond, then refused it
outright with **"bad price."** The operator was right; the strategy's own "placed" log was true only
of the SYNCHRONOUS `PlaceOrder` result — Rithmic's real, async verdict arrived ~2ms later and nothing
in this file was listening for it.

**Root cause of the bad price**: `TryEnter` anchors its target math on `midPrice` (bid+ask midpoint,
its own call site: `this.TryEnter(levels, fvg.Active, midPrice, tickSize)`), not the fill price.
MNQ trades in 0.25 ticks, so any time the spread is an odd number of ticks the midpoint itself sits
exactly half a tick off-grid (e.g. bid 31214.25 / ask 31214.50 → mid 31214.375). `MaxTargetDistanceTicks`'s
own clamp (`TryComputeTarget`'s `ClampToMaxDistance`, added earlier today) computes `midPrice ±
(ticks * tickSize)`, which inherits that same fractional offset straight into the final order price —
exactly how 31224.375 (not a legal MNQ price) got sent to the exchange.

**Fixed, two layers**:
1. **Prevent it**: `PlaceProtectiveOrders` now rounds both `stopPrice` and `targetPrice` to the
   nearest real tick (`Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize`)
   immediately before either `Core.Instance.PlaceOrder` call — the single choke point every pricing
   path (signal-computed, POC-computed, adopted-position fallback) already funnels through, so one
   fix covers all of them rather than patching `ClampToMaxDistance` and every other price source
   individually.
2. **Catch it if it still happens for some other reason**: `Core_OrdersHistoryAdded` already handled
   `OrderStatus.Refused` but only reset two unrelated wait-flags — it never checked whether the
   refused order was one of THIS position's protective legs. Added `RepairMissingProtectiveOrders()`,
   called on every refusal: checks what's ACTUALLY resting right now via the existing
   `FindProtectiveStopOrder`/`FindProtectiveTargetOrder` lookups, and self-heals — a missing TARGET
   gets a fresh fallback re-placed (`FallbackTargetTicks` from the real fill, tick-rounded); a missing
   STOP closes the position immediately, matching the existing synchronous stop-failure philosophy
   ("a trade must never run with no stop at all") now extended to the asynchronous case. Self-correcting
   by design — if both legs are genuinely resting, it does nothing.

**Verified 2026-10-05**: `dotnet build finchDomScalpStrategy/finchDomScalpStrategy.csproj -c Release`
— 0 errors, 31 warnings (all pre-existing, unrelated). Deployed (`OutputPath` writes straight to
`C:\Quantower\Settings\Scripts\Strategies\finchDomScalpStrategy\`). **NOT YET verified live** — next
live target placement should never again show a fractional (non-multiple-of-0.25) price in the
`[Order] protective ...` log line, and if a refusal ever does slip through for some other reason, a
new `[Risk] protective TARGET/STOP is missing ...` log line should appear instead of silence.

---

### 14. Ocean's Stack Strategy (`oceansStackStrategy`) — added 2026-09-29

**Files:** `Strategies/oceansStackStrategy/oceansStackStrategy/oceansStackStrategy.csproj`,
`oceansStackStrategy.cs`, `ValueAreaEngine.cs`, `SessionPoolTracker.cs`, `AbsorptionTracker.cs`,
`FuelPoolSelector.cs`, `SweepZoneTracker.cs`, `DeltaTracker.cs`, `Bar.cs`, `SETTINGS.md`.

A live port of `Quantower-storage/TradingView/ocean.pine` ("Ocean's Stack v2") — a session-based
**liquidity sweep failure** swing system for NQ, handed to the operator as a TradingView indicator
and turned into a real, order-placing Quantower Strategy. Genuinely different character from
`finchDomScalpStrategy`: once-per-day zones and a single morning trigger window, swing-sized
targets, instead of continuous 1-minute scalping.

**The trade**: build the prior day's value area (VAH/VAL/POC) and a prior-week version, score each
value-area edge on absorption + low-volume-ledge evidence, arm a zone when a nearby liquidity pool
(overnight/Asia/prior-day/prior-week high-or-low) sits in range, then — only inside a configured
morning window (default 09:30–10:30 ET) — watch for price to sweep that pool and fail back inside
the value area on confirming delta. Entry at the failed-sweep bar's close, stop beyond the sweep
extreme, target at the opposite value-area edge.

**Five decisions confirmed via `AskUserQuestion` before any code was written** (plan file
`starry-petting-pike.md`, researched via a `Plan` subagent first given the size of this build):
1. **Real order-flow delta**, not Pine's own candle proxy (`sign(close-open)*volume`) — classified
   from actual tick prints via a newly-extracted, shared `TickClassifier` (see below).
2. **No dry-run/sim-account gate** — matches `finchDomScalpStrategy`'s own explicit policy exactly.
3. **Full port, everything at once** — weekly overlay, QQQ→NQ cross-asset check (informational
   only), and LVN ledge scoring all included from day one, not a stripped-down v1.
4. **Instrument: MNQ**, same account as `finchDomScalpStrategy`.
5. **Exit structure**: Pine names two targets (T1 = prior-day POC, T2 = opposite value-area edge),
   but nothing in this codebase does partial/scale-out exits. **Single contract, target T2 only**
   — T1/POC is logged as an informational level on the signal line, never traded.

**Prerequisite refactor**: `TryClassify(Symbol, Last, out bool isBuy)` existed as two independent,
byte-identical copies (`FinchLiteIndicator.cs`, `finchDomScalpStrategy.cs`). Extracted into a new
shared `Indicators/Finch-Lite/src/Finch.Lite.Indicator/TickClassifier.cs`
(`internal static class TickClassifier`), compiled in by source (same `Assembly.Load` cache-
collision reasoning as `RestingOrderEngine.cs`/`PocEngine.cs`) rather than pasting a third copy. The
two pre-existing copies were left as-is (out of this task's own scope) — this new strategy is simply
the first consumer of the shared version.

**One deliberate, load-bearing divergence from decision #1**: `ValueAreaEngine` is fed from
**historical 1-minute bars**, not live ticks. Quantower has no historical tick/time-and-sales
backfill (the same limitation `PocEngine.cs` already documents for its own current-move POC) —
prior-day and prior-week value areas must already be COMPLETE the instant the strategy attaches,
which only bar history can supply. Volume-*profile* construction is a different need (requires
history) than delta *confirmation* (wants live-forward accuracy, and gets it via real ticks here).

**New engine classes, all strategy-local** (nothing else in this codebase needs them yet):
- `ValueAreaEngine` — bins volume by price, finds the max-volume bin as POC, expands outward
  alternately (whichever neighboring bin holds more volume) until reaching the configured
  value-area %. Three independent instances: daily NQ, weekly NQ, QQQ's own. Also exposes an
  `IsLvnLedge` check against its own retained bin map (direct port of Pine's `f_isLVN`).
- `SessionPoolTracker` — the four "fuel" pools (overnight/Asia/prior-day/prior-week H/L), each
  fresh-started on window entry and frozen at window exit, ported directly from Pine's own
  accumulation blocks. Needed a new minute-precision `IsInWindow` helper alongside the existing
  hour-only `IsInHourWindow` (Ocean's Stack needs 30-minute boundaries; the existing helper was
  left untouched rather than risking a regression in `finchDomScalpStrategy`).
- `AbsorptionTracker` — discrete, positioned absorption prints with hold/invalidation state (a bar
  with volume well above its own 50-bar average and a strongly one-sided delta that closed the
  OTHER way). Only prints that survive a full session without price closing back through them
  ("must hold") carry into the next day's scoring snapshot. Reuses a copy of `DeltaTracker`
  extended with `LastBarBuy`/`LastBarSell`/`LastBarDelta` (per-bar, not rolling-window) accessors.
- `FuelPoolSelector` — pure function, direct port of Pine's `f_fuel`: nearest qualifying pool
  within `[poolMin, poolMax]` beyond a value-area edge.
- `SweepZoneTracker` — the actual trading trigger, one instance per zone (top/short, bottom/long).
  Owns only the sweep/fire state machine; score/armed are recomputed fresh every bar by the main
  strategy and passed in. A single bar can both extend/begin a sweep AND fire the reversal in the
  same close (a spike-and-reject candle) — replicated exactly as Pine allows it.

**QQQ second-symbol subscription** — genuinely new territory for this codebase (every existing
strategy is single-symbol). Exposed as a second `[InputParameter] Symbol QqqSymbol`, same
resolution mechanism as `CurrentSymbol`. Entirely informational (never gates the score/armed/fire
pipeline, matching the source script's own design) and wrapped in its own try/catch per poll so a
QQQ-side failure can never stall or crash the real NQ-only trading logic — degrades to a persistent
`[QQQ] unavailable` log line if the second connection/symbol doesn't cooperate. Flagged explicitly
as unverified: whether Quantower delivers bars smoothly for a symbol on an unrelated connection,
and whether the "prior RTH close" ratio anchor (which needs NQ's and QQQ's own bar timestamps
reasonably aligned) holds up in practice — a "use live ratio instead" toggle exists as the simpler
fallback if the fixed-anchor approach proves noisy.

**Verified 2026-09-29**: `dotnet build oceansStackStrategy/oceansStackStrategy.csproj -c Release` —
0 errors, 0 warnings (beyond the usual nullable-annotation-context notices). Deployed to
`C:\Quantower\Settings\Scripts\Strategies\oceansStackStrategy\`. **NOT YET verified live** — this
is a brand-new strategy with zero track record; per its own plan, the value-area math should be
cross-checked against the same day's Ocean's Stack Pine chart directly before trusting any live
signal, and it fires rarely by design (score >= threshold AND fuel AND sweep AND reclaim, all
inside a 60-minute window) — expect to watch `[Signal skipped]`/heartbeat logs for a while before
seeing a real trade.

---

### 15. MES ORB Strategy (`mesOrbStrategy`) — added 2026-10-05

**Files:** `Strategies/mesOrbStrategy/mesOrbStrategy/mesOrbStrategy.csproj`, `mesOrbStrategy.cs`,
`SETTINGS.md`.

A port of the operator's own discretionary MES opening-range-breakout play ("this is one i
personally traded for a good while"): mark the 8:00-8:15 ET high/low, wait for a 5-minute close
beyond either side, wait for a ~50% retest of the midpoint, drop to the 1-minute chart to confirm a
rejection off that zone in the break direction, then enter with a fixed stop/target. Genuinely
different character from both `finchDomScalpStrategy` (DOM/order-flow, continuous scalping) and
`oceansStackStrategy` (value-area sweep-and-reclaim) — this one is pure price-action, cross-
timeframe (5m for the break/retest, 1m for the rejection confirmation), at most once a day.

**Three decisions confirmed via `AskUserQuestion` before any code was written** (the operator's
own description had two genuine ambiguities that mattered for real money):
1. **Rejection definition**: a 1-min bar that wicks into/through the retest zone but CLOSES back
   through the midpoint in the breakout direction — pure price action, no DOM/order-flow data
   (the stricter pin-bar and the full order-flow-absorption alternatives were both declined).
2. **Stop placement**: the operator's own words named both "the other side of the box" AND "~5pt
   stop loss" — these conflict on a wide-range day. Resolved: **always a fixed ~5pt stop**, never
   box-relative; the box's width still gates whether the day trades at all (`Min/Max ORB range`),
   just not the stop's own distance.
3. **Second-chance reversal** (the operator's own description, not one of the offered options):
   if the midpoint rejection gets "disrespected" and price closes beyond the OPPOSITE side of the
   box, follow that reversal directly — same fixed stop/target, no second retest-and-rejection
   wait, since the full-range move already showed the conviction that wait would otherwise check
   for. This is the sole exception to "one trade per day."

**Prior art checked first**: `esOrbStrategy` already exists in this repo and covers similar
ground (three entry modes including a "50% retest" mode, three stop-loss modes) but is a single-
timeframe strategy with a hand-rolled EST offset (`estTimezoneOffset`, a manual double rather than
real `TimeZoneInfo` conversion — silently wrong across a DST transition) and a lot of unrelated
scope (session-reversal trading, pre-market volume gating). Not reused — this is a genuinely new,
cross-timeframe build, using `oceansStackStrategy`'s own proper `TimeZoneInfo.FindSystemTimeZoneById
("America/New_York")` pattern instead.

**Order-placement safety ported wholesale from `finchDomScalpStrategy`**, not re-learned from
scratch on a second live strategy: separate stop/target orders tick-rounded immediately before
either reaches the broker (closes the exact "bad price" async-refusal bug found and fixed there
today — THIRTY-EIGHTH FIX above), an adopted position (one this instance didn't itself open) gets
a sane fallback stop/target from its real fill instead of a zeroed default, any async order refusal
self-heals (missing target re-placed, missing stop closes the position immediately — never run
with no stop at all), and `IsMine`/`IsMyPosition` use the same bootstrap-relaxed contract+account
matching. Daily-loss/account-floor risk limits read the real broker `Account.Balance`, persisted in
their own `mes_orb_daily_risk_state.txt` file so they survive a mid-session restart.

**Known, accepted limitation**: on `OnRun`, the ORB box itself (high/low/midpoint) is always
correctly rebuilt from history for today — a simple min/max over today's window bars, order-
independent, safe at any restart time. The breakout → retest → rejection SEQUENCE is deliberately
NOT retroactively replayed — a restart after the window closes always resumes "awaiting a fresh
breakout" from that moment forward, even if a breakout/retest/rejection already fully completed
earlier today. An early design considered replaying 1-minute bars through the full state machine on
startup to close this gap, but was rejected: the phase a replayed bar would be checked against is
whatever the state machine ends up at AFTER processing all of today's 5-minute bars, not the phase
that was actually active when each 1-minute bar historically closed — a bar from right after the
breakout (before ever reaching the retest zone) could spuriously look like a rejection by
coincidence and fire a stale, wrong entry immediately on restart. Given this strategy trades at
most once a day, the honest documented gap was judged safer than a subtly-wrong fix.

**Same-day addition: SESSION LEVELS, a second independent setup** ("the other part of my
strategy... mark out the untested highs and lows from each session so asia, london, ny"). Marks
each of Asia/London/NY's own high/low, frozen the instant that session ends — same accumulate-
and-freeze pattern as `oceansStackStrategy`'s own `SessionPoolTracker`, just three sessions instead
of overnight/Asia/prior-day/prior-week. Six independent level state machines (one per session per
side), each watched until a 5-min bar touches it, then handed to the 1-min chart for one of two
reads: a REJECTION right there (fade, opposite the approach direction), or a clean BREAK through
followed by "a little pullback" and a 1-min rejection candle off THAT pullback (confirming a move
WITH the breakout). Same fixed stop/target as the ORB play, reusing the exact same
`StopLossPoints`/`ProfitTargetPoints` — the operator's own words were "the same 5pt stop and
15-20pt tp," so this intentionally shares the risk-sizing inputs rather than duplicating them.

**Four decisions confirmed via `AskUserQuestion`**:
1. **Same strategy, not a separate project** — one attach runs both playbooks, sharing the same
   account-balance risk limits (avoids the two setups double-counting risk against each other).
2. **A touch marks a level tested, period** — classic liquidity-sweep semantics: once price trades
   through a level, that resting liquidity is spent whether or not THIS strategy acts on it. Each
   of the six levels gets exactly one look per cycle, never re-armed until the next time that
   specific session completes again.
3. **Independent daily cap**: up to six session-level trades a day (one per level touched),
   entirely separate from the ORB play's own one-trade-per-day cap — the only shared constraint is
   the ordinary "never more than one position open at once" guard (`EnterTrade` itself).
4. **Pullback entry = a 1-min rejection candle off the pullback**, not a bare "price receded a bit"
   check — same confirmation discipline as the main rejection play, applied to the continuation
   case too.

**Decoupled `EnterTrade` from the ORB's own day-end flag**: it used to set `phase =
OrbPhase.DoneForDay` internally, which was fine while the ORB play was the only caller, but would
have wrongly ended the ORB's own day every time a session-level trade fired (and vice versa, had
the dependency run the other way). `EnterTrade` now just returns whether the order was placed; each
ORB call site sets its own `DoneForDay` afterward, regardless of the return value (an attempt that
fails to place still shouldn't retry the same stale signal every bar) — session-level entries don't
touch ORB state at all, and the two setups now genuinely run independently.

**Startup reconstruction reuses the live code path directly** rather than a second, parallel
implementation: `ReconstructSessionLevels` just replays all of `history5m` through the exact same
`ProcessSessionLevels5mBar` the live poll loop calls, then forces every level back to `Idle` before
going live (same restart-limitation reasoning as the ORB's own box — reconstructing
Price/Untested status is safe and order-independent, but resuming a mid-reaction/pullback watch is
not). Zero risk of the replay path drifting from the live path, since it IS the live path.

**Verified 2026-10-05**: `dotnet build mesOrbStrategy/mesOrbStrategy.csproj -c Release` — 0 errors
(both before and after the session-levels addition; warning count unchanged, all pre-existing
nullable-annotation-context notices). Deployed to
`C:\Quantower\Settings\Scripts\Strategies\mesOrbStrategy\`. **NOT YET verified live** — brand new,
zero automated track record for either setup; watch the `[ORB]`/`[Session]`/`[Signal]`/`[Order]`
log lines against the real chart for several days before trusting it unattended.

#### FIX, 2026-10-06 — strategy silently stalled for hours with ZERO log trace; added a heartbeat

The operator, after a live move rallied up toward the ORB midpoint and rejected without a short
firing: "why did it not take this short here it came up and barely missed the midpoint and
rejected from it." Pulled the instance's own per-instance log (restarted 7:33:41 AM ET) — it
contained exactly three lines: the account warning, `[Session] reconstructed...`, and `Started
[MesOrb].`. Nothing else, for hours, across a window that should have produced at minimum an
`[ORB] range captured` line once 8:15 ET passed. Cross-checked the platform's own Serilog too —
one `'mesOrbStrategy' state changed to: 'Working'` line and nothing else; no exception anywhere.
`mes_orb_daily_risk_state.txt` didn't exist at all, which is itself informative: `IsAccountFloorBreached`
unconditionally writes that file on its very first successful call regardless of whether a floor is
even configured — its total absence means `RunPoll` had not completed even ONE successful pass
since the restart, not just that the ORB logic specifically had nothing to report.

**Root cause confirmed minutes later**, once the `[Heartbeat]` fix below was deployed and the
operator restarted: the heartbeat fired immediately (`13:28:55` and again `13:29:04`, nine seconds
apart after a second restart) showing `ORB phase=DoneForDay high=-1797693...` — `orbHigh` was
still sitting at the raw `double.MinValue` sentinel, never actually assigned. The per-instance log
confirmed why: `"[ORB] could not reconstruct today's ORB range from history after a restart —
skipping today."` fired on BOTH restarts, instantly, even though both happened a full hour-plus
after the 8:00-8:15 ET window closed, with that window's own 5-minute bars unquestionably real and
settled by then. Session levels came back empty for the identical reason (`Untested session
levels: none`). `ReconstructTodayState`/`ReconstructSessionLevels` both read `this.history5m.Count`
synchronously, immediately after `GetHistory()` returns — but `GetHistory()` can hand back a
`HistoricalData` object that is still populating its own backlog in the background; reading
`.Count` at that exact instant saw it empty or near-empty on every observed restart, not just an
occasional one. Once reconstruction found nothing, it permanently set `phase = DoneForDay` — and
`ProcessClosed5mBar`'s own switch has NO `DoneForDay` case, so no live bar arriving moments later
(once the real history actually finished loading) could ever recover it. This also explains the
EARLIER 7:33 AM silent-stall incident this same morning: that restart happened before the window
even opened, so reconstruction correctly (if silently) set `AwaitingWindow` — but the same
underlying empty-history snapshot meant `barsSeen5m` got seeded past bars that hadn't loaded yet
either, so the live 8:00 AM transition never fired, with nothing left in the log to explain why.

**Fixed, two parts**:
1. Added a `[Heartbeat]` log (every 5 minutes, reporting ORB phase and which session levels are
   still untested) — same diagnostic shape `finchDomScalpStrategy` already has. This is what
   actually surfaced the bug: without it, "ORB phase=DoneForDay, high/low still at their sentinel
   defaults" would have stayed invisible indefinitely.
2. `OnRun` now waits (polling `history5m.Count` every 100ms, up to a 5s deadline) for at least 100
   bars to actually be present before calling either reconstruction method — directly targeting
   the confirmed race rather than guessing at a timeout. Logs an error if the wait times out
   without reaching that bar count, so a genuinely slow/failed history fetch is now visible too
   instead of silently producing an empty reconstruction.

**Verified 2026-10-06**: `dotnet build mesOrbStrategy/mesOrbStrategy.csproj -c Release` — 0 errors.
Deployed.

#### FOLLOW-UP FIX, same day — the 5s wait wasn't the real fix; missing Fake-symbol resolution was

The operator restarted again to test the fix above and stopped it almost immediately: "i stopped
the strategy because look at this clean break to the upside that is fully missed on because it
wasnt able to draw the box even though i had this turned on before the market opened to draw the
box." The new error log (added by fix #2 above) proved the 5-second wait theory wrong on its own:
`"[ORB] history5m only has 0 bars after a 5s wait"` — not near-empty, ZERO, even after the full
wait. No amount of waiting was ever going to fix that; it wasn't a brief population race.

**Real root cause**: every other strategy in this codebase — `finchDomScalpStrategy` included —
re-resolves `CurrentSymbol`/`CurrentAccount` at the top of `OnRun` if either comes back in a
`BusinessObjectState.Fake` state (`this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol
.CreateInfo())`, same for Account). A saved `[InputParameter] Symbol` can come back as a
serialized PLACEHOLDER on reload/restart, not yet resolved to a live, connected symbol —
`GetHistory()` called on a Fake symbol returns a handle that never populates, no matter how long
you wait. `mesOrbStrategy` was built fresh this week rather than copied from an existing
strategy's `OnRun`, and this one established, repo-wide step got missed. This also fully explains
the ORIGINAL 7:33 AM silent-stall incident (the one that started this whole investigation): if the
symbol was Fake from the start, `Drain5m`/`Drain1m`'s own `h.Count <= 1` guard would skip
processing FOREVER, completely silently, exactly matching "zero ORB logs ever" — no race, no
partial failure, just a dead data source nothing downstream could ever recover from.

**Fixed**: added the exact same two-line re-resolution step `finchDomScalpStrategy` already uses,
at the very top of `OnRun`, before the existing null-check. The earlier 5-second-wait fix is kept
(harmless, and still useful for a genuine brief population race on an already-resolved symbol) but
is no longer the primary defense.

**Verified 2026-10-06**: `dotnet build mesOrbStrategy/mesOrbStrategy.csproj -c Release` — 0 errors.
Deployed. **NOT YET re-verified live** — next restart should show `[ORB] range captured: ...`
promptly, including on a restart taken well after the window closed, and the `"[ORB] history5m
only has 0 bars"` error should not recur.

#### FEATURE/FIX, same day — the Fake-symbol fix worked live; three more changes from watching it trade

The fix above held: the heartbeat showed a real `ORB phase=AwaitingRetest` with real high/low/mid
values, a session-level rejection short fired and was protected correctly (`[Order] protective
stop=7891.25 target=7871.25`), and the operator confirmed the structural bug was gone. Three
follow-on changes came from watching that live trade and the ORB play afterward:

1. **EMA confluence** — the short above got stopped out into a rally despite a clean-looking
   rejection: "there wasnt enough confluence to determine the actual short lets add in the closure
   below the 9ema as a confluence to show the direction is actually changing." Added a running EMA
   (`EmaPeriod`, default 9, computed on 1-minute closes, fed unconditionally in `Drain1m` so it's
   always current) gating every rejection-style entry in BOTH playbooks — ORB rejection,
   session-level reject, session-level pullback-reject. A long needs its trigger candle to close
   above the EMA; a short needs it below. Deliberately NOT applied to the ORB reversal-breakout
   play, which already requires a full 5-min close beyond the whole box on its own.
2. **Retest zone widened to the whole box** — separately, a clean ORB retest-and-rejection never
   triggered an entry: "it played out perfect on the retest of the orb but this was never actually
   placed an order for the long... we need to show a retest of the orb not just at the 50% mark."
   `RetestZone()` previously returned a tight band around the midpoint only; a pullback that
   touched well into the box without reaching that band never armed `AwaitingRejection` at all.
   Widened to the full `orbLow..orbHigh` range, with `RetestZoneTolerancePercent` repurposed as a
   buffer BEYOND the box's edges instead of a band around its center. The rejection confirmation
   itself is unchanged (still requires closing back through the midpoint specifically).
3. **One retest shot per breakout** — immediately flagged as a necessary companion to #2: "since
   the retest already played out it should not enter again if it comes back down and touches it
   again — that break and retest already played out for today." Widening the zone alone would let
   `AwaitingRejection` sit armed indefinitely, potentially firing off a completely separate, much
   later touch of the box. `ProcessClosed1mBar` now resolves the attempt the moment a bar's CLOSE
   fully leaves the (now box-wide) zone without confirming — `phase = DoneForDay` either way, no
   re-arming on a later touch. (The equivalent latent risk exists in `ProcessLevelReaction`'s own
   `AwaitingReaction`/`AwaitingPullback` states too — not fixed here since the operator didn't flag
   it there and each session level can in any case only be touched once per cycle; worth applying
   the same resolution rule there if it ever shows the same symptom.)

**Verified 2026-10-06**: `dotnet build mesOrbStrategy/mesOrbStrategy.csproj -c Release` — 0 errors.
Deployed. **NOT YET re-verified live** — watch for `[ORB] 1-min rejection confirmed ... (above/
below the 9-EMA)` on the next entry, and `[ORB] retest attempt resolved without a confirmed
rejection ... done watching for today` on a retest that fails to confirm.

---

## Common Architecture Patterns

### Event Handling
All strategies implement these core event handlers:
- `Core_PositionAdded`: Tracks position counts and quantities
- `Core_PositionRemoved`: Resets flags and cancels pending orders
- `Core_TradeAdded`: Updates P&L tracking
- `Hdm_HistoryItemUpdated`: Real-time price updates
- `Hdm_OnNewHistoryItem`: New bar formation events

### Risk Management Framework
Standard risk controls across all strategies:
- `maxTrades`: Maximum number of trades per session
- `maxProfit`: Automatic stop at profit target
- `maxLoss`: Automatic stop at loss threshold
- Position quantity validation and tracking

### Order Management
Common order placement patterns:
- Bracket orders with SL/TP when supported
- Order status validation and error handling
- Pending order cancellation on position close
- `waitOpenPosition` flag for timing control

### Historical Data Access
Standard pattern for accessing market data:
```csharp
double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
double high_1 = HistoricalDataExtensions.High(this.hdm, 1);
double low_1 = HistoricalDataExtensions.Low(this.hdm, 1);
```

---

## Development Notes

### Quantower Platform Integration
- All strategies inherit from `Strategy, ICurrentAccount, ICurrentSymbol`
- Use Quantower's `InputParameter` attributes for UI configuration
- Implement `MonitoringConnectionsIds` for connection management
- Follow Quantower's order placement API patterns

### Code Organization
- Each strategy in separate solution/project structure
- Consistent naming: `{strategyName}Strategy` class and namespace
- Project files configured for Quantower deployment paths
- All strategies target .NET 8 with latest C# language version

### Testing Status
✅ All strategies compile successfully in Visual Studio
✅ All strategies use compatible Quantower API patterns  
✅ Parameter validation and error handling implemented
✅ Risk management controls in place
✅ `emaCrossStrategy` — actively developed; last build 0 errors 0 warnings (.NET 8, Release)
✅ `futuresProStrategy` — actively running on MESM6; filters confirmed via live log analysis
✅ `tvConfluenceStrategy` — new 2026-09-12, verified with a clean `dotnet build` (0 errors, 0 warnings), not yet run live/paper

### Platform Version Notes (2026-09-12)

The installed Quantower platform had moved on to **v1.146.18** while every
`.csproj` in this repo still pointed at **v1.145.16/17** (a version that no
longer exists on disk) and targeted **.NET 8**, while v1.146.18's
`TradingPlatform.BusinessLayer.dll` now requires **.NET 10**. Net effect:
`dotnet build` failed on every single strategy in this repo before today,
not just new ones — confirmed by actually running `dotnet build` (no Visual
Studio needed; the plain CLI is enough once the `.csproj` points at a real
install).

**Fixed across every active (non-`Backup`) project** — a path/TFM bump only,
no logic changes:
- `<HintPath>`/`<StartProgram>` bumped from `v1.145.16`/`v1.145.17` → `v1.146.18`
- `<TargetFramework>` bumped from `net8` → `net10.0`
- All 12 active strategies now build clean with these two changes alone.

**Also fixed:** `futuresProStrategy.cs` had two `CrossMinGapTicks` properties
declared with the same name (`InputParameter` indices 8 and 28) — a
duplicate-member compile error once the reference actually resolved. Removed
the second (dead, unused) declaration; kept index 8, which is the one every
other cross-debounce read in the file actually references.

**Found but deliberately NOT fixed — needs your input:**
`futuresProStrategy.cs` (the one strategy noted above as "actively running on
MESM6") calls `Core.Instance.Indicators.BuiltIn.VWAP()` and
`.ATR(int period)` — v1.146.18 removed the parameterless `VWAP()` entirely and
changed `ATR` to require a `MaMode` argument. This is a real behavior
decision (how to replace VWAP, or whether to), not a mechanical path fix, so
it was left alone rather than guessed at. **If this strategy is still live,
confirm whether it's running an already-built DLL from before the platform
update** (which may or may not still work at runtime against the new
platform) rather than assuming the fixed reference path alone makes it safe
to rebuild and redeploy as-is.

### Scripts Folder Cleanup + Output-Path Collisions (2026-09-12)

**`C:\Quantower\Settings\Scripts\ScriptsData\` had grown to 373MB** — 443
near-empty per-instance folders (one auto-created every time any strategy was
ever added to a chart, each holding just a day's `.slog` text file) plus a
`Backtest results\` folder holding the actual saved backtest performance
data. Deleted all 443 per-instance folders (with the user's confirmation) —
Quantower recreates them automatically as needed, nothing functional is
lost. Also removed 7 confirmed-empty (zero files) subfolders inside
`Backtest results\` for orphaned/renamed strategies. Left the live
`Futures Pro Strategy (...)` instance's folder alone since Quantower had its
log file open at the time (actively running).

**Left alone, flagged for the user**: `Indicators\EMACrossBackTestingStrategy`,
`Indicators\GridbotScalper`, and `Strategies\GridbotScalper` are compiled
DLLs with no matching source anywhere in this repo — deleting them would be
unrecoverable, so they were kept as-is rather than guessed at.

**Fixed a real output-path collision**: `futuresProStrategy-Backup\` and
`smaCrossStrategy-OG\` were both still configured (via stale `AssemblyName`/
`OutputPath`) to build into the *exact same* `Strategies\` output folder as
their live counterparts (`futuresProStrategy\` and `smaCrossStrategy\`) —
building either backup would have silently overwritten the active strategy's
deployed DLL. Gave each its own distinct assembly name and output folder
(`Strategies\futuresProStrategy-Backup\` and `Strategies\smaCrossStrategy-OG\`),
matching the pattern `emaCrossStrategy-Backup\` already used correctly. All
three (plus every active project) were also brought onto the same
v1.146.18/.NET 10 pin as everything else and verified with a clean
`dotnet build`.

**Current state**: every project in this repo (16 total, including backups)
builds into its own uniquely-named folder under
`C:\Quantower\Settings\Scripts\Strategies\<name>\` — this is Quantower's own
canonical location for `AlgoType=Strategy` projects (separate categories
exist for Indicators/PlaceOrderStrategies, which is why dropping a strategy
DLL loose in `Scripts\` root instead would actually stop it from showing up
in Quantower's Strategies list). The only project that still fails to build
is `futuresProStrategy` itself, for the unrelated VWAP/ATR reason documented
above.

---

## Strategy Selection Guide

| Strategy | Best For | Market Conditions | Complexity |
|----------|----------|-------------------|------------|
| **EMA Cross** | Trend + retracement entries | Trending / session breakouts | High |
| Box Range | Sideways markets | Low volatility | Medium |
| Price Surge | Trending markets | High volatility | Medium |
| Range Scalp | Quick profits | Ranging markets | Low |
| SMA Cross | Trend following | Trending markets | Low |
| Slope Change | Momentum shifts | Choppy trends | High |
| Weighted Surge | Refined momentum | Variable volatility | Medium |
| Gold ORB | Session breakouts | Gold futures | Medium |
| MES ORB | Opening-range breakout + retest/rejection | MES futures, morning session | Medium |

This documentation should be updated whenever strategy logic or parameters are modified.

---

## Known gaps (as of the 2026-09-14 reorganization)

- **`README.md` is stale** - it documents 8 strategies in user-facing parameter-table form
  (EMA Cross through Gold ORB) but was never updated for `tvConfluenceStrategy`,
  `keltnerReversionStrategy`, `trendlineBreakStrategy`, `srChannelBreakStrategy`,
  `esOrbStrategy`, `emaSimpleStrategy`, or `emaTrendStrategy` - all of those exist only in
  this file (CLAUDE.md) and/or their own per-project `readme.md`. Worth a pass if the
  README is meant to stay the user-facing entry point.
- ~~`ORB-IX` has no build/verification story in this repo~~ - resolved 2026-09-14, full
  source arrived and now builds clean from this repo (see Indicator Catalog above).
- **`Strategies/Backups/`** holds 5 dated snapshots; none were reviewed for whether they're
  still worth keeping vs. superseded by their active counterpart - untouched during this
  reorganization beyond the relocation itself.
