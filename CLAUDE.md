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

### Order-Flow Scalping Setup (`Indicators/order-flow-scalping/`) — added 2026-09-14
**Not a project** - a configuration/diagnosis document for getting `ORB-IX` (above) to show
delta, DOM/resting orders, absorption, auto-drawn fib, and FRVP+AVP (higher/lower timeframe
POC) all together, matching a working reference screenshot. Documents a CONFIRMED
platform/data-vendor refusal (Quantower's own toast: "Volume analysis calculation from ticks
history is not allowed for one data vendor") that explains historical-backfill gaps in
footprint/absorption data on some connections but not others - see that folder's README for
the full finding and the exact `InputParameter` names to enable each requested display.

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
