# CLAUDE.md

Ocean DOM — a price ladder pinned to the ATAS price panel that puts **what is resting** at a price
and **what actually traded** at that price on the same row. Built 2026-08-23. Appears as
**Ocean → Oceans DOM**. See README.md for the design derivation. Shared build rules:
`~/dev/CLAUDE.md`.

## Layout

`OceansDomIndicator.cs` render + settings · `DomMath.cs` pure math (`LadderGrid`, `TapeBook`,
`LevelWatch`, `Salience`, `LevelParser`) · `_smoke/` · `_test/`.

## Why this exists next to the other Ocean indicators

Everything else in `~/dev/oceans-*` is a **chart overlay**. There was no ladder. This is the
ladder, and it deliberately does not duplicate them: no value area, no profile, no absorption
verdict, no ORB. It shows resting size, traded size, and where price is relative to levels you
type in.

**Absorption is not computed here.** The ladder puts the two facts side by side on one row and
lets the read be yours. `oceans-profile` and `oceans-effort` own the derived version.

## Invariants — all tested, do not regress

1. **Rows anchor in PRICE space, not to the window bottom.** `Anchor = floor(low/rowSize)*rowSize`.
   Anchoring to the visible low reshuffles which ticks share a row on every scroll and the ladder
   shimmers. Same invariant as `oceans-depth`; the two `RowGrid`/`LadderGrid` implementations are
   deliberately separate copies because these projects are standalone, not a shared library.
2. **Column positions are fixed and reserve their space when empty.** A ladder that reflows as
   things appear cannot be read by muscle memory, and muscle memory is the only reason a ladder
   beats a chart for speed. This is also why there is no adaptive-declutter mode.
3. **Exactly one thing may be loud, and only a `Break` may be it.** `Salience.For` refuses the
   loud channel to anything else. Spending it on confirmation — "your level held!" — is what
   marries you to a position. The loud colour is a setting; what may use it is not.
4. **Quiet looks quiet.** With no level in play the whole panel drops to `QuietDim`. A screen that
   looks equally busy with no setup on it manufactures trades.
5. **The tape window is rebuilt from bars every frame, never accumulated.** An accumulator
   silently loses whatever arrived during a feed reconnect and the hole never shows.
6. **`Normalise` returns 0 when the reference is 0.** An empty book draws nothing — falling back
   to full scale would paint a maximum-size wall out of nothing.
7. **An unreadable level is reported on the chart, never dropped.** A level lost to a typo is a
   level you think you are watching and are not.
8. **A break and a rejection are the same distance from the level and opposite in meaning.** The
   only thing separating them is the side price originally approached from, and that side must
   survive price chopping across the level while inside the band. This is the single most
   breakable piece of logic here — it survived a mutation only after the chop case was added.

## Do not add

**No predicted anything.** No queue-decay model, no imbalance forecast, no signal arrows. Same
rule that got the DOM-delta estimate deleted from `oceans-depth`: *"i dont want estimates i need
facts only."* Every mark on this ladder is a measurement.

**No order entry.** Execution stays in ATAS Chart Trader. An indicator that can fire orders from
a custom-rendered panel is a mis-click away from real money on a prop account.

## API notes

`MarketDepthInfo.GetMarketDepthSnapshot()` for resting size (the `Indicator.` version is
obsolete). Traded-volume-per-price comes from `GetCandle(bar).GetAllPriceLevels()` → `.Price`,
`.Bid`, `.Ask` — the same idiom `oceans-profile` uses; the raw `OnNewTrade` stream is not used.

**The chart does not repaint when the book changes, only when bars do.** `SubscribeToTimer` +
`RedrawChart(new RedrawArg(region) { ForceRedraw = true })` drives it — which also means the book
is only *sampled* at the refresh rate, and the setting says so.

Every render layer is wrapped separately by `Layer()` and its failure is printed on the chart.
There is no debugger on the render thread.

## The event log

`WriteLog` appends one JSON line per level state change to
`%APPDATA%\ATAS\ocean-dom-events.jsonl` — utc, instrument, level name, level price, price,
from-state, to-state. It exists so what the ladder said can be checked afterwards rather than
remembered. Nothing reads it yet; that is the intended next piece.
