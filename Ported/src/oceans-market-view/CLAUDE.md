# CLAUDE.md

Ocean Market View — ATAS indicator that **draws on the chart**: four independently toggleable
overlays in one indicator (weekly open, power hour breakout, market sessions Asia/London/NY,
initial balance by levels). Recreated from TradingView versions the trader was using. Started
2026-08-17. See README.md for behaviour. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`OceanMarketView.cs` render + settings · `MarketModel.cs` logic · `TimeContext.cs` the bar clock
(copied into `oceans-profile` — keep them in sync) · `_reflect/` API prober · `_test/`.

## Why this project is the starting point for overlay work

It has the working hand-drawn render path — `OFT.Rendering`, `SubscribeToDrawingEvents`,
`PriceChartContainer.GetXByBar` / `GetYByPrice`. Start here rather than `oceans-crabel`, which
draws nothing. `_reflect/` dumps the real API surface of any installed ATAS type; use it instead
of guessing from docs.

## MNQ hours — verified against CME 2026-08-18, all Central

Sunday **17:00** open · Friday **16:00** close · maintenance halt **16:00–17:00 every weekday** ·
RTH/cash **08:30–15:00** · London 02:00–10:30 · Asia 18:00–03:00.

**The halt is NOT the 15:00 cash close.** 15:00 shipped twice and was wrong both times.

## Two traps this project hit

1. **Renaming is mandatory when a setting's meaning changes.** Redefining a property from exchange
   time to Houston time without renaming it kept serving the old numbers under the new label, on
   every saved chart template. See the shared rule in `~/dev/CLAUDE.md`.
2. **A synthetic-data test proves nothing if the generator shares the code's assumption.** The
   generator must encode documented external reality — CME's published calendar — never mirror the
   config it is testing.
