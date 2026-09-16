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
  -p:QuantowerSdkPath="C:\Quantower\TradingPlatform\v1.147.3\bin\TradingPlatform.BusinessLayer.dll"
```
(`v1.147.3` as of 2026-09-15 - confirm against whatever's under `C:\Quantower\TradingPlatform\`
on the machine actually building; BUILD.md deliberately doesn't hard-code it since it moves on
Quantower updates. **Quantower auto-updated from v1.146.18 to v1.147.3 mid-session on
2026-09-15** - every DLL deployed earlier that day had been built against the now-stale
v1.146.18 SDK reference while the live platform had already moved to v1.147.3, and is the
leading suspect for a same-day report of settings changes intermittently blanking the whole
chart. Rebuilt against the correct current SDK and redeployed; if a similarly erratic "changing
one setting breaks everything" report recurs, check this path mismatch FIRST, before
suspecting the paint code.)

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
