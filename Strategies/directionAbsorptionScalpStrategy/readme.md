# Direction + Absorption Scalp Strategy

Trades the ORB-IX indicator's "possible long/short" callout against a real HH/LL structural
level, timed by Ocean's Anchor's absorption state machine (an ATAS indicator ported from a
friend's suite — see `Ported/PORTING-GUIDE.md`), targeting VWAP / HH-LL / prior day-week levels.

## What decides what

- **Which way**: `OrbIx.Core.Direction.DirectionEngine`'s callout — the same multi-timeframe
  structure + price-vs-VWAP + cumulative-delta read the ORB-IX indicator draws as a bright line.
- **When exactly**: a real HH/LL support/resistance level (from `OrbIx.Core.Structure.HhLlEngine`)
  is treated as an Ocean's Anchor `Zone`. Price testing it **arms** the zone; a qualifying
  absorption print — Anchor's tape-based `TapeAbsorption`/`DisplacementWatch` test (a large print
  that shows no follow-through within a timed window), or its bar-shape `ClusterAbsorption` test
  (3-of-4: delta outlier vs. recent distribution, close-back-inside, volume concentrated at the
  extreme, a real wick) when no tape event fires — **triggers** it; a delta flip or a CVD
  higher-low/lower-high in the trade's favour on a later bar **confirms** it, inside a bar-count
  clock, or the zone **expires** or **breaks**. Entry fires ONLY on the Confirmed transition, and
  only when `DirectionEngine`'s current side agrees with the zone's side — two independent
  readings, both required.
- **The stop**: the confirming absorption print's own price extreme (`Zone.ClusterLow`/
  `ClusterHigh`) — the trade is wrong the moment price trades through the size that was
  supposedly absorbing it. Clamped to a configurable min/max tick distance.
- **The target**: whichever of (nearest opposing HH/LL level, session VWAP, prior day high/low/
  close, prior week high/low) sits nearest to entry, ahead of price in the trade's direction,
  past a minimum-distance floor. A fixed-tick fallback applies only when nothing qualifies, and
  is logged as `FALLBACK` so it's never confused with a level-based target.

## Architecture decisions worth knowing before touching this

**`OrbIx.Core` and Ocean's Anchor's absorption files are compiled IN by source, never
referenced as separate DLLs.** Quantower loads scripts with `Assembly.Load(bytes)` and caches
them by assembly FullName — a shared `OrbIx.Core.dll` (or `OceansAnchor.dll`) under two script
folders collides on one cache key, and this has already caused an indicator in this same repo to
silently render nothing once. See the `.csproj`'s own comments. **Trade-off**: this strategy's
copies of both `OrbIx.Core` and Ocean's Anchor's logic are frozen snapshots as of its own last
build — a later fix in either source tree needs a rebuild here to take effect.

**This strategy builds its own `Zone` objects from `HhLlEngine` segments — it does NOT port
Ocean's Anchor's own zone detector (`ZoneBuilder`/`AnchorProfile`).** That detector finds
volume-profile HVN shelves, which needs per-price footprint data — the same historical
volume-analysis capability this Quantower connector has already been found to refuse for
ORB-IX (see `Indicators/ORB-IX/CLAUDE.md`). Porting it properly is its own large project. What's
reused instead is Anchor's absorption *math* and *state machine* (`AnchorModels.cs`,
`AnchorState.cs`'s `SignalEngine`/`ZoneMaintenance`/`SignalGate`, `AnchorAbsorption.cs`) applied
to a simpler zone source: the nearest currently-open HH/LL support/resistance level per side,
one zone per side rather than Anchor's own ranked set of up to four. `ZoneBuilder` and
`AnchorProfile`'s HVN-detection code are compiled in as unused dead code only because
`ZoneMaintenance` lives in the same file and references types they declare — same "compile the
clean tree, unused parts are harmless" reasoning `OrbIx.Core`'s own whole-tree include already
relies on.

**Not ported from Ocean's Anchor**: its A+ time-window/Friday-suppression rule
(`AnchorSession`/`AnchorClock`/`SignalGate.Allowed`) — a personal, Houston-time-specific rule
from the original author, not something to silently impose here (use this strategy's own
`RTH only`/`RTH start/end hour` instead if wanted); its CSV calibration log (`AnchorLog.cs`) —
nothing here calls it, decisions go to the strategy log instead; its ranked multi-zone builder
(above).

**FRVP/AVP point-of-control is not a target candidate.** Same connector-level historical
volume-analysis gap as above makes it unavailable most of the time; logged once at start, not
per-signal.

## Safety

- **`I confirm this account is sim/eval, not live`** (default **false**) — `OnRun` refuses to
  place orders until this is explicitly set true. There is no reliable way to ask the Quantower
  SDK "is this a sim account," so this is a deliberate, auditable manual step instead. The
  account's name/id/connection is logged loudly on every run regardless of this flag.
- **`Dry run`** (default **true**) — logs every fully-computed decision (side, entry, target +
  source, stop + source, confirming zone, callout kind) instead of placing an order. Meant to run
  for hours or days against live data before ever being turned off.
- Position isolation follows `srChannelBreakStrategy`'s `StrategyTag`/`Comment` pattern exactly —
  every position/order/trade query is filtered by `Comment == "DirectionAbsorptionScalp"`, since
  this can run on the same account+symbol as other strategies in this repo. That pattern is
  itself flagged, in every template that uses it, as **unverified against a live session** — this
  strategy is the first real chance to confirm or refute it.

## Build

```bash
dotnet build directionAbsorptionScalpStrategy/directionAbsorptionScalpStrategy.csproj -c Release
```

Confirm the `TradingPlatform.BusinessLayer` HintPath and `StartProgram` version against whatever
is actually installed under `C:\Quantower\TradingPlatform\` — it auto-updates (already moved once
mid-session, from v1.146.18 to v1.147.3, while this was being built). Output goes straight to
`C:\Quantower\Settings\Scripts\Strategies\directionAbsorptionScalpStrategy\` — no separate deploy
step. After building, confirm the output folder holds only this strategy's own `.dll`/`.pdb` —
no stray `OrbIx.Core.dll` or `OceansAnchor.dll`, which would mean the compile-in approach
silently reverted to a reference somewhere.

## Bring-up order

1. Attach with `Dry run = true` on any account (no orders are ever sent in this mode). Review the
   `[Signal]`/`[Absorption]`/`[Zone]`/`[Order]` log lines: callout flips and zone confirmations
   should be rare events, not constant noise; if the cluster/tape absorption gate never skips
   anything, the thresholds need retuning for this instrument.
2. Cross-check against the ORB-IX indicator on the same symbol/period with matching HH/LL
   settings — both now run identical compiled-in `OrbIx.Core` logic, so the strategy's logged
   HH/LL segment prices should match exactly what the indicator paints.
3. Only after a satisfactory dry-run review: check `I confirm this account is sim/eval`, uncheck
   `Dry run`, set `Quantity = 1`, **on a confirmed sim/eval account only**. Watch the first several
   order cycles closely — the `PlaceOrder` result, the resulting position's actual SL/TP against
   what was logged, whether `Position.Comment` actually carries the strategy tag, and that the
   daily-loss/drawdown/max-trades/cooldown guards actually halt new entries when tripped
   (temporarily set tiny thresholds to force it).

## Known limitations / unvalidated

Every absorption threshold (`SizeFloor`, `MaxDisplacementTicks`, the cluster test thresholds) is
a stated guess carried over from Ocean's Anchor's own defaults — the source explicitly says
`SizeFloor 100` is "an NQ round number, not a measurement," and this strategy's default of 150
for the same reason is equally uncalibrated for whatever instrument this actually runs on.
Nothing here has a demonstrated, costed, out-of-sample edge. Treat every trade this places as a
test of the logic, not a proven signal.
