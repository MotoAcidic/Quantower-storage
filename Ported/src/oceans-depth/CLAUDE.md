# CLAUDE.md

Ocean Depth — narrow gradient liquidity strip on the right edge of the ATAS price panel: resting
size per price, bids green / asks red, number beside each bar, biggest level outlined. Auto-fits
any timeframe and zoom. Built and deployed 2026-08-20. Appears as **Ocean → Oceans Depth**.
See README.md for the layout derivation. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`OceansDepthIndicator.cs` render + settings · `DepthMath.cs` pure math (`PeakBook`, `Normalise`) ·
`_smoke/` · `_test/`.

## Invariants — all tested, do not regress

1. **Rows anchor in PRICE space, not to the window bottom.** `Anchor = floor(low/rowSize)*rowSize`.
   Anchoring to the visible low reshuffles which ticks share a row on every scroll and the strip
   shimmers. Test: two windows scrolled by a non-whole-row amount must put the same price on the
   same boundary.
2. **`PeakBook` holds then fades.** A raw DOM snapshot strobes — MNQ's book updates many times a
   second and the numbers are unreadable. A pulled level is held at full brightness then faded
   linearly to what is really there. It doubles as the only way a 400-lot that flashes and vanishes
   is ever seen. The clock is injected (ms) so decay is exactly testable.
3. **Ghost size carries no ghost order counts.** The faded size is a memory; `Largest` and `Orders`
   describe what IS resting, so they go to zero when the level is pulled.
4. **`Normalise` returns 0 when the reference is 0.** An empty book draws nothing — falling back to
   full scale would paint a maximum-size wall out of nothing.
5. **MBO is opt-in and reports honestly.** Market-by-order with a silent feed prints that on the
   chart after a 6s grace, with the depth-vs-order update counts, and never quietly draws
   aggregated depth under an MBO label.

## Do not reintroduce the DOM-delta estimate

It was built and then **removed** — the trader rejected it outright: *"i dont want estimates i need
facts only."* Deleted rather than defaulted off, because a rejected option sitting in a settings
menu still contradicts that. The facts-only replacement is absorption in `~/dev/oceans-profile`.

## Feed and API notes

**The feed does appear to carry MBO for MNQ.** `%APPDATA%\ATAS\Logs\app_<date>.log` shows
`xFeed prop Data: Subscribing MNQU6@CME for MarketByOrder` and `Order book sources: glbx`, with no
entitlement errors. Active connector is **dxFeed prop** (`IsMarketDataEnabled=True`), not the
delayed dxFeed. Note `UseLimitedOrderBook=True, DepthLevelsCount=0` — revisit if the book ever
looks truncated.

Reflected, not documented: `Indicator.GetMarketDepthSnapshot()` is obsolete, use
`MarketDepthInfo.GetMarketDepthSnapshot()`. `Dispose()` is `public` on `ExtendedIndicator`, so an
override cannot be `protected`. Do not name a property `Labels` — it hides the base member.

**The chart does not repaint when the book changes, only when bars do.** `SubscribeToTimer` +
`RedrawChart(new RedrawArg(region) { ForceRedraw = true })` drives it — which also means the book
is only *sampled* at the refresh rate. Size faster than that is missed, and the setting says so.
