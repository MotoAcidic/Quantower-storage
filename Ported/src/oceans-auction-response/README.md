# Ocean Auction Response Monitor

A **read-only** ATAS indicator that looks for aggressive trading with limited price progress
at a level you declared in advance, then waits for a separate directional confirmation.

It places no orders, reads no account state, and contains no reachable order-submission path.

## What it is not

**There is no demonstrated edge here.** Every threshold in it is an engineering default from
the specification, not a calibrated or backtested number. There are no learned coefficients,
no calibrated probabilities, no win rate, and no evidence of net profitability after costs.
The probability panel reads "Not calibrated" and shows no number, because no validated model
exists. Treat a confirmation as "the rule fired", nothing more.

It also **will not arm at all** until you supply a frozen historical baseline artifact. That
is deliberate: the rule compares attacker volume against a frozen 90th percentile, and there
is no honest way to invent that number. Until the artifact exists, the health panel reads
Warmup and no candidate can arm.

## The rule, in one paragraph

At a declared level `L`, freeze the zone `Z = [L-2, L+2]` ticks. Over a rolling 5 second
window, a **candidate arms** when all of these hold at once at a decision tick: the data is
healthy, the level predated the window, the midpoint is inside the zone, attacker volume
strictly exceeds its frozen 90th percentile, oriented delta is positive, net oriented price
progress is between 0 and 2 ticks, the maximum forward excursion never exceeded 2 ticks, and
at least 25% of the attacker volume actually executed inside the zone. On arming, three
boundaries freeze: `K` (the oriented minimum midpoint over the window), `C = K - 2`
(confirmation) and `F = max(oriented zone edge) + 4` (failure). A **confirmation** then needs
the midpoint to hold at or beyond `C` for a full second of *observed* time, plus opposite
5-second flow, within 30 seconds. Anything beyond `F` for a second **invalidates**. Running
out of time **expires**. Those three are alternative outcomes, not a sequence.

## Install

```powershell
./deploy.ps1
```

It runs the test harness, builds, runs the smoke test, backs up any previous build, then
copies the DLLs into `%APPDATA%\ATAS\Indicators\`. It refuses to copy anything if the tests
or the smoke test fail.

**ATAS reads that folder only at startup — restart the platform after deploying.**

Then add *Auction Response Monitor* from the Ocean category to a chart.

## Rollback

`deploy.ps1` writes a timestamped backup folder next to the installed DLLs, e.g.
`%APPDATA%\ATAS\Indicators\_backup_AuctionResponse_20260912_141530\`.

To roll back: close ATAS, copy the DLLs from that folder back over the installed ones,
restart ATAS. To remove entirely: close ATAS, delete `AuctionResponseMonitor.dll`,
`AuctionResponse.Core.dll`, `AuctionResponse.Ui.dll` and `AuctionResponse.Replay.dll` from
`%APPDATA%\ATAS\Indicators\`, restart.

Nothing else on the system is touched. The indicator never modifies connection settings and
never writes outside the recording directory you explicitly approve.

## Settings you must supply

| Setting | Why it has no default |
|---|---|
| **Contract expiry** | A baseline built on one dated contract must never be used on another. |
| **Baseline artifact path** | The frozen 90th percentile. Without it nothing arms. |
| **Resistance / support levels** | Levels are declared manually and must predate the window they are judged on. Automatic PDH/PDL and opening ranges need a session convention this build does not invent. |
| **Recording directory + approval** | Recording market data is your decision and your data rights. The recorder refuses an unapproved destination. |

## Building the baseline

Nothing arms without a frozen baseline artifact. Build one from chart history:

1. Open an **MNQ chart on a 5-second timeframe** and load as much history as ATAS will give
   you. One candle must be exactly one non-overlapping 5-second window; the builder refuses
   any other timeframe rather than quietly producing samples that mean something else.
2. Add **Auction Response Baseline Builder** (Ocean category).
3. Set **Contract expiry** to the contract that history belongs to — `202609` for U6. Stamp
   what the data *is*, not what you are trading.
4. Tick **Write artifact**. It reports sessions, samples, buckets and any gate it failed,
   straight on the chart, and writes nothing if the gates fail.
5. Point the monitor's **Baseline artifact path** at the file it wrote.

It reads aggressor-tagged volume straight off ATAS footprint candles: `Ask` is buy-initiated,
`Bid` is sell-initiated, `Betweens` is unattributed — the B, S and U of Section 6.

**Gates:** at least 10 sessions and 300 samples per 30-minute bucket. A 5-second chart gives
360 samples per bucket per session, so roughly 10–15 RTH sessions clears both.

**One honest limit:** footprint candles carry no bid/ask, so the plot's *response* scale is
derived from close-to-close rather than the midpoint. The artifact records that provenance
(`responseScaleSource`) so it can never pass as the real thing. It scales a display axis only
— the attacker-volume quantile that actually gates arming is exact.

### Across a contract roll

A baseline is stamped with its expiry and refused on a different contract by default. On roll
day that would leave you with no baseline at all, so **Allow cross-expiry baseline** permits
one from another dated contract of the same instrument. Symbol, exchange and tick size are
still enforced, and the health panel says when a cross-expiry baseline is in use.

## If the panel looks wrong

The layout measures its own text, so it adapts to whatever DPI the chart reports. If it still
looks too small or too large, set **UI scale** explicitly (1.0, 1.25, 1.5) instead of leaving
it on 0 (follow Windows). Turn on **Show diagnostics** to see the DPI the chart actually
reported, alongside per-input event counters — `Trades: none yet` means no trade callback has
fired at all, which is a feed question, not a layout one.

## Time

**One zone: Central.** The session window is entered in Central, the session math is done in
Central, and every timestamp printed on the chart is Central with its abbreviation. Defaults
are 08:30–15:00 America/Chicago, which is the CME equity index RTH session.

The specification defines baseline buckets as "30-minute New York session segments". That is
the same session: baseline bucketing is measured in *minutes from session start*, so it is
timezone-agnostic, and CT and ET shift for DST on the same dates for CME equity index.
`spec/DEFAULTS.json` is reproduced verbatim from the handoff and therefore still says
`America/New_York`; the shipped indicator default is Central. Both describe the same window.

## Repository layout

| Path | What |
|---|---|
| `src/AuctionResponse.Core` | Platform-independent engine: events, windows, math, health, state machine. No ATAS reference. |
| `src/AuctionResponse.ATAS` | Host adapter: callbacks, threading, drawing surface, alerts. |
| `src/AuctionResponse.Ui` | Panel layout, measured from real text metrics. No ATAS reference, so overlap is unit-tested. |
| `src/AuctionResponse.Replay` | JSONL recorder, log reader, deterministic replay, baseline artifact IO. |
| `src/AuctionResponse.Research` | Offline only: baseline construction, ridge regression, trade economics. |
| `tests/AuctionResponse.Tests` | The whole acceptance suite. Zero dependencies; exit code gates the deploy. |
| `spec/` | `DEFAULTS.json` and `TEST-VECTORS.json`, reproduced verbatim from the handoff. |
| `_reflect/` | Reflection probe that produced `COMPATIBILITY.md`. Re-runnable. |
| `_smoke/` | Constructs the indicator outside ATAS, where a throwing constructor is visible. |
| `_preview/` | Renders the real layout to PNGs offline, at any panel size and display scaling. |

## Commands

```bash
dotnet build AuctionResponse.slnx -c Release        # everything
cd tests/AuctionResponse.Tests && dotnet run -c Release   # the suite
cd _reflect && dotnet run -c Release                # re-probe the installed ATAS API
cd _preview && dotnet run -c Release                # render the layout to PNGs, offline
cd _smoke && dotnet run -c Release                  # construct the indicator standalone
```

See `COMPATIBILITY.md` for the verified host API surface and the capability matrix, and
`BUILD-REPORT.md` for what was actually tested and what remains unverified.
