# Ocean's Stack Indicator — Settings Guide

The visual companion to `Strategies/oceansStackStrategy` — draws the same state the strategy trades
off of (value-area lines, zone boxes, fuel targets, absorption markers, sweep signals, QQQ
comparison lines), the way `Indicators/Finch-Lite` already does for `finchDomScalpStrategy`. See
`Quantower-storage/CLAUDE.md`'s Indicator Catalog entry for the full build history and design
decisions. Draws only — places no orders, reads no account.

**Attach to whatever chart you'd run `oceansStackStrategy` on** (MNQ). Unlike the strategy, there is
no `Symbol` setting here — this indicator uses the CHART's own attached symbol and pulls its own
dedicated 1-minute history series from it (same pattern Finch-Lite already uses for its own 5m/15m
POC series), regardless of what period the chart itself is displaying.

Every setting below (session windows, absorption/LVN/fuel thresholds, signal scoring) is copied
verbatim — same name, same default, same range — from `oceansStackStrategy`'s own `SETTINGS.md`.
**Keep both in sync by hand** if you tune one; this indicator's own copy of the scoring/arming logic
is a deliberate, hand-synced duplicate of the strategy's, not a shared engine (see the project's own
`.csproj` comment) — nothing enforces the two staying identical.

## QQQ comparison lines — verify this in isolation first

`QQQ->NQ: enable` defaults to **off** here (the strategy defaults it **on**) — this is the single
riskiest, least-precedented piece of this indicator. No indicator anywhere in this codebase has ever
pulled a second symbol's history before, and it is unconfirmed that Quantower's `Indicator` base
class resolves an `InputParameter Symbol` to live market data the same way `Strategy` does. Before
trusting anything else on this indicator: turn QQQ on with a valid `QQQ Symbol` and nothing else
enabled, confirm the two converted comparison lines (`QQQ->VAH`/`QQQ->VAL`) actually draw, THEN turn
the rest back on.

## Display (indicator-only — the strategy has no equivalent settings)

| Setting | Default | Why |
|---|---|---|
| Keep prior days on chart | 3 | How many PAST days' own VAH/VAL/POC lines stay visible (fainter, fixed-length) alongside today's live ones. 0 = today only. |
| Keep labels on prior days | false | Today's own lines always carry a label; this only gates whether the fainter historical lines also do — off by default so old labels don't clutter the chart. |
| Zone half-height (ticks) | 10 (2.5 pts) | Purely cosmetic — how tall the zone box is drawn around its own edge price. Pine's own `zoneTol`; the strategy has no equivalent since it never draws a box. |
| Show value-area lines / zone boxes / fuel target labels / absorption markers / sweep signals | all true | Independent per-feature toggles — turn any one off without affecting the others. Useful for isolating one feature at a time when first verifying this indicator (see below). |

## Colours

One colour per line/state group rather than per individual line (e.g. one "weekly" colour covers
wVAH/wVAL/wPOC/PWH/PWL together) — see `OceansStackIndicator.cs`'s own `InputParameter` list for the
full set and defaults. `Short/resistance` and `Long/support` each drive THREE things at once: a fired
zone's own box colour, the "SF" sweep-signal marker, and an absorption marker on that side — one
colour convention (this codebase's usual red=bearish/green=bullish) instead of Pine's own separate
white/cyan absorption-marker colours, a deliberate simplification for consistency with every other
overlay in this codebase.

## What's deliberately NOT ported

- **Pine's "Dalton open type" classification** — `oceansStackStrategy.cs` never ported this logic
  from `ocean.pine` in the first place; this indicator visualizes what the strategy actually
  computes, not new logic invented just for the chart.
- **Retained-day history for pools/absorption** — `Keep prior days on chart` covers only the daily
  VAH/VAL/POC lines, not multi-day pool or absorption history.

## Verification order (do this before trusting a live signal)

1. Build (`dotnet build src/OceansStack.Indicator/OceansStack.Indicator.csproj -c Release
   -p:Share=true -p:QuantowerSdkPath=...`), close Quantower, copy
   `OceansStackIndicator.dll` to `C:\Quantower\Settings\Scripts\Indicators\OceansStack\`.
2. Attach with only `Show value-area lines` on — confirm today's VAH/VAL/POC lines match a manual
   read of the chart.
3. Enable zone boxes + fuel targets — confirm box colour transitions grey → yellow → cyan as
   score/arm state changes through the morning.
4. Enable absorption markers — cross-check against `oceansStackStrategy`'s own log (run both on the
   same symbol/session).
5. Enable sweep signals last (rarest to trigger).
6. QQQ pathway tested alone, per the warning above.
7. Diff this indicator's zone status text against the strategy's own `[Heartbeat]`/`[Signal
   skipped]`/`[Signal]` log lines at the same wall-clock moment — any divergence means the two
   hand-synced copies have drifted.
