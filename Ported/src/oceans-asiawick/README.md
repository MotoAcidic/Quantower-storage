# AsiaWick

Asia-session top-wick signals for MNQ, in two halves:

- **Phase 1 — `AsiaWick Levels`** (indicator). Draws the two levels, marks the three signal
  variants, alerts once per variant per night. Ships enabled. This is the only half you should
  be running today.
- **Phase 2 — `AsiaWick Overnight`** (ChartStrategy). Trades the same signals, one attempt a
  night, short only. **Ships with `Signal only` ON — it sends no orders.** See the phase gate.

Both consume the *same* signal core (`AsiaWickMath.cs`), so what gets traded is exactly what
got marked. There is no second implementation to drift.

---

## The signal

Everything runs on the **Central** clock, one zone, no conversions anywhere.

- **Trade date** = the Globex session, rolling at **17:00 CT**. All per-night state keys off it.
- **Asia window** 18:00 → 02:00 CT (crosses midnight, one trade date).
- **RTH** 08:30 → 15:00 CT — the source of the prior-day high.
- Windows are half-open `[start, end)`: 17:59 out, 18:00 in, 01:59 in, 02:00 out.

Per closed Asia bar, with `range = high − low` and `upperWick = high − max(open, close)`:

| Variant | Fires when |
|---|---|
| **PDH sweep** | prior completed RTH high known, `high > pdh`, `close < pdh` |
| **Asia-high sweep** | prior Asia high known, `high > asiaHigh`, `close < asiaHigh` |
| **Wick rejection** | new Asia high (or first Asia bar), `range > 0`, `upperWick / range ≥ 0.50`, `close < low + range/2` |

The current bar folds into the Asia high **after** evaluation — otherwise every bar sweeps
itself. First signal per variant per night; the signal bar's **high** is the stop reference.
Short only.

The prior-day high is tracked as "the last RTH window that *completed*", walking forward. That
is what makes half-days, gaps and Sunday opens correct with no special case: whatever the last
completed window produced is the level.

---

## Install

```
./deploy.ps1        # math harness -> build both -> smoke -> copy
```

Copies `OceansAsiaWick.dll` to `%APPDATA%\ATAS\Indicators` and
`OceansAsiaWickStrategy.dll` to `%APPDATA%\ATAS\Strategies`. It refuses to copy anything if
the tests or the smoke test fail.

**ATAS reads both folders only at startup — restart the platform after every deploy.**

Manual alternative: indicator settings → *Add custom indicator* → point at the DLL.

Individual steps:

```
dotnet build OceansAsiaWick.csproj -c Release
dotnet build _strategy/OceansAsiaWickStrategy.csproj -c Release
cd _test  && dotnet run -c Release      # math harness
cd _smoke && dotnet run -c Release      # constructs both outside ATAS, dumps every setting
cd _reflect && dotnet run -c Release -- ChartStrategy    # dump any installed ATAS type
```

Attach to a **15m or 30m** chart (60m tolerated). Above 60m it logs a warning and draws nothing.

---

## Settings

**01 Sessions** — time zone, bar clock, the four window bounds, morning exit
(London 02:00 / NY open 08:30 / NY close 15:00 CT).

**02 Signals** — per-variant enable, wick %, status line.

**03 Alerts** *(indicator)* — enable, sound file, colours. One alert per variant per night, on
real-time bars only; historical recalculation never alerts.

**03 Execution / 04 Risk** *(strategy)* — signal-only, quantity (also the hard position cap),
stop ticks, tier-1 guard and dates, Friday/weekend block, nightly loss cutoff, status line.

### The bar clock

ATAS bar stamps are sometimes UTC and sometimes already exchange-local, and there is no reliable
way to know from the API. **Auto** settles it from evidence — the newest bar against the wall
clock, else the 16:00–17:00 CT maintenance halt showing up as an empty hour every day — and
prints which signal it used in the status line.

It never falls back to a guess. If it cannot tell, it draws nothing and says so, because a wrong
offset does not *look* wrong: it produces a clean, plausible, silently misplaced level, and then
a trade taken off it.

### The tier-1 date list

Comma-separated `yyyy-MM-dd`. Each entry is the **calendar date of the release morning** — a
07:30 CT CPI print on 2026-09-10 is `2026-09-10`, not the trade date of the night before.

On a listed morning the strategy forces flat at **07:28 CT** and blocks re-entry (the funded account wants
flat ≥2 min before the release). Entries earlier in that night are still allowed — that is what
the spec asks for.

An entry that does not parse is **logged loudly and ignored** — never dropped quietly. Check the
ATAS log after editing the list.

---

## The phase gate

`Signal only` ships **ON**. The strategy runs the whole pipeline — signals, stop maths, guards,
logging — and sends nothing. Before it is turned off:

1. The Python/TradingView backtest of *this exact logic* shows **PF > 1**.
2. It **beats the unconditional-short baseline** on the same nights.
3. **MAE survives** the 40-tick stop and the $500 nightly cutoff.
4. Five **Market Replay** nights checked by hand: markers, stop distance, exit times.
5. A run of sim nights in signal-only mode with the log read each morning.

The smoke test asserts the default is ON, so it cannot be flipped by accident in source.

---

## Tests

`_test` is the math harness — the core, clock and risk rules are ATAS-free precisely so they
can be exercised without the platform. `deploy.ps1` runs it and refuses to deploy on failure.

Acceptance tests **1–4** are automated (124 assertions): trade-date mapping across both DST
boundaries and Sunday opens, session membership at every edge, each variant on crafted fixtures
including the wick-% boundary and "level must predate the bar", one-per-night, idempotency, and
the tier-1 forced flat.

The suite has been **mutation-checked** — ten deliberate breaks (self-sweeping bar, 17:00→18:00
roll, `>=`→`>` on the wick, dropping the close-back-below test, breaking the midnight-crossing
window, moving the tier-1 flat to 07:35, removing one-per-night, shipping signal-only OFF) were
each caught. One mutant is equivalent by construction: committing the PDH mid-RTH changes
nothing, because the level is only ever read during the Asia window, when RTH is closed either
way.

### Acceptance test 5 — cross-validation (manual, still open)

```
cd _test && dotnet run -c Release -- <bars.csv> [utc|local]
```

Prints `trade_date,time_ct,variant,signal_price,stop_reference` for a bar CSV exported from
ATAS, to be diffed against `asia_wick_backtest.py` on the same bars. Timestamps must match
within one bar. **Investigate every mismatch — do not average it away.**

You have to tell it whether the CSV stamps are UTC or Central: a CSV carries no evidence either
way, and guessing would produce a plausible, wrong signal set.

### Acceptance test 6 — Market Replay (manual, still open)

Five replay nights, markers and stop distances and exit times verified by hand.

---

## Deviations from the build spec

Each of these is a place the spec and the installed platform (or this repo's hard-won rules)
disagreed. Everything else was built as written.

1. **`net10.0-windows` only**, not `net8.0-windows;net10.0-windows`. Only the 10.0.0 targeting
   packs are installed, and ATAS 8.0.14.399 runs net10.0 — a net8.0 leg could not reference the
   platform DLLs. Matches every other `oceans-*` project.

2. **One repo, two assemblies, `_test` console harness** rather than a four-project xUnit
   solution. This is the shape every `~/dev/oceans-*` project uses and what `deploy.ps1` expects;
   it also needs no NuGet restore. The Core/Indicator/Strategy *separation* the spec wanted is
   intact — `AsiaWickClock.cs` / `AsiaWickMath.cs` / `AsiaWickRisk.cs` are ATAS-free and compiled
   into both assemblies.

3. **Bar clock resolution added.** The spec says to convert every candle timestamp to CT, which
   assumes stamps are UTC. They are not reliably UTC. See *The bar clock* above.

4. **Timeframe guard measures the bar interval from the stamps** instead of parsing
   `ChartInfo.TimeFrame`. That string's format is not contractual, and a misparse would silently
   disable the guard.

5. **The maintenance halt is treated as 16:00–17:00 CT.** ⚠️ Your global `CLAUDE.md` says
   "daily halt 15:00–16:00"; `~/dev/CLAUDE.md` says 16:00–17:00, verified against the CME
   calendar, and records it shipping wrong three times. This build follows 16:00–17:00 (15:00 is
   the *cash close*, when trading carries straight on). **The two memory files disagree — worth
   settling.** It only affects the clock's fallback halt-detection, not any session window.

6. **Reference implementations not found.** `asia_wick_overnight.pine` and
   `asia_wick_backtest.py` are not on this machine (searched `~/dev`, `~/Downloads`, `~/Desktop`,
   `~/Documents`). The signal core is built from §2 of the spec, which the spec itself says wins
   on any disagreement. **Acceptance test 5 cannot be closed until those files turn up** — the
   CSV mode above is ready for them.

7. **Logging goes through `Utils.Common.Logging.LoggerHelper`**, not
   `ATAS.Strategies.StrategyLoggingExtensions`. The latter extends `Strategy`, and `ChartStrategy`
   derives from `Indicator`, not `Strategy` — so its `LogWarn` does not apply here.

8. **`Indicator.TickSize` is deprecated**; the stop prices off `InstrumentInfo.TickSize`. A zero
   tick is treated as a hard error that flattens — never defaulted to a number.

9. **Friday is checked before the flatten reasons** in `EntryBlock`, so the log says
   "FridayTradeDate" rather than the vaguer "WeekendClose". Note that on MNQ a Friday *trade
   date* is nearly empty anyway (the week closes 16:00 Friday and reopens 17:00 Sunday), so the
   real weekend protection is the 15:55 Friday flatten — which under the three shipped exits
   never has to fire.

## Known gaps, called out rather than hidden

- **FOMC is not covered by the 07:28 flat.** The spec specifies 07:28 CT, which handles the
  07:30 CPI/Employment releases. FOMC lands at 13:00 CT. With the default *NY open* exit you are
  long flat by then, but with the **NY close** exit a position could still be open through an
  FOMC release on a listed date. Either keep the NY-open exit on FOMC days or add a second flat
  time before going live.

- **`close < low + range/2` is nearly redundant.** If `upperWick / range ≥ 0.5` then
  `max(open, close) ≤ midpoint`, so the close is already at or below the midpoint. The clause
  only ever excludes a close landing *exactly* on the midpoint. Implemented as specified, and
  there is a test pinning that exact case — but it is not the independent filter it looks like.

- **Stop orders use `TriggerPrice`** with `OrderTypes.Stop`. That matches the `Order` shape in
  `ATAS.DataFeedsCore`, but it has not been confirmed against a live fill. **Verify stop
  placement in Market Replay before disabling signal-only.**

- Non-goals, as specified: no partial targets, no trailing, no re-entries, no Sunday special
  cases beyond the session windows. The "Long mirror" diagnostic exists in the settings model but
  is not wired to a mirrored signal — it ships off and short-only is the tested path.
