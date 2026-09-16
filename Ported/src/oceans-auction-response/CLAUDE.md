# CLAUDE.md — Ocean Auction Response Monitor

Read `README.md` first for what it does, then `COMPATIBILITY.md` for the verified host API
and `BUILD-REPORT.md` for what is and is not tested.

This project does NOT follow the usual `oceans-*` single-project shape. It is a six-project
solution because the handoff specification requires an isolated core, adapter, replay, tests
and offline research. Keep that separation.

```bash
dotnet build AuctionResponse.slnx -c Release
cd tests/AuctionResponse.Tests && dotnet run -c Release   # 390 checks, exit code gates deploy
./deploy.ps1                                              # tests, build, smoke, backup, copy
```

## Two indicators, not one

`AuctionResponseMonitor` is the live read-only monitor. `BaselineBuilderIndicator` is an
OFFLINE tool that builds the frozen baseline artifact from loaded chart history — drop it on
a 5-second MNQ chart, tick "Write artifact", point the monitor at the file.

**The builder refuses any timeframe that is not exactly 5 seconds.** One candle must be one
non-overlapping 5-second window; on any other timeframe every sample would silently mean
something else. `Timeframe.ParseSeconds` lives in Core precisely so that gate is unit-tested,
including that tick, volume and range charts do NOT read as time-based.

**Footprint semantics:** `IndicatorCandle.Ask` is buy-initiated volume, `.Bid` is
sell-initiated, `.Betweens` is unattributed — the B, S and U of Section 6. Do not reach for
`.Delta` and assume a sign convention; compute `Ask - Bid`.

**The builder writes nothing when the gates fail** (10 sessions, 300 samples per 30-minute
bucket) unless explicitly overridden for inspection. It never invents a quantile.

**Candles carry no bid/ask**, so the artifact's response scale is close-to-close, not the
Section 6 midpoint. That is recorded in `ResponseScaleSource` so it can never pass as the real
thing. It scales a display axis only; the attacker-volume quantile that gates arming is exact.

## Contract roll

A baseline is stamped with its expiry and refused on a different contract by default.
`AllowCrossExpiry` relaxes ONLY the expiry — symbol, exchange, tick size, clock and feed mode
are still enforced — and `BaselineResolver.UsingCrossExpiryBaseline` makes it visible in the
UI. The relaxation exists because MNQ rolls quarterly and the strict rule leaves you with no
baseline at all on roll day. Never widen it further without a test.

## Rendering

**Layout lives in `AuctionResponse.Ui`, which has no ATAS reference**, behind an `ISurface`
interface. The ATAS project supplies `RenderContextSurface`; the tests supply
`RecordingSurface`, which records every text rectangle so overlap is an assertion rather than
something you check by squinting at a screenshot. `_preview/` renders the same layout to PNGs
through WPF, offline, at any panel size and scaling.

**Never advance a row by a pixel constant.** The first build shipped unreadable because rows
advanced 13px while the fonts were sized in POINTS (11pt is about 15px at 96 DPI, more at
higher scaling). Every vertical step comes from `ISurface.Measure`. `Metrics.Row()` is the
only thing that decides spacing, and `LayoutTests` re-injects the constant to prove the suite
still catches it.

**Sections are bounded and prioritised.** Each gets a `Cursor` that refuses to write past its
bottom, so sections cannot bleed into each other. Data health is ANCHORED to the panel bottom
and its space is carved out first: it explains whether the thing works at all, so the plot and
history are what give way when the panel is short. Degrade by dropping whole sections, never
by squeezing text.

**Overlay labels go through `LabelPlacer`.** Zone edges, the confirmation line and the failure
line can land within a few pixels of each other when zoomed out. Each label is offered several
slots and takes the first free one; a label with nowhere to go is dropped while its LINE is
still drawn, so the chart loses decoration rather than meaning.

## Rules specific to this project

**`AuctionResponse.Core` must never reference ATAS.** Not "should not" — the whole point of
the split is that the engine is testable and replayable without a platform. If something
seems to need a host type, it belongs in the adapter.

**Null is not zero, and it never becomes zero.** `Measure` carries `double?` plus a reason.
An unavailable value renders as `N/A (reason)`. Zero depth is *unavailable* imbalance, not
balanced. An empty window has *no* normalized delta. Anything that turns a missing input into
a zero is a bug, and the suite has checks that will catch it.

**Never round an off-grid price onto the grid.** `TickGrid.ToTicks` throws and
`TryToTicks` returns false. A rounded price silently moves a level, a zone edge, or a frozen
boundary.

**Midpoints and boundaries are in HALF ticks.** `MidHalfTicks = bid + ask`. Midpoints and
`C`/`F` can legitimately land on a half tick, and half-tick integer arithmetic keeps them
exact. Divide by 2 only at the display edge.

**The decision-tick priority order in `TerminalTransition.Resolve` is normative.** Steps 3
and 6 are why a confirmation exactly at 30 000 ms is allowed and one at 30 250 ms is not.
Do not reorder it to make a chart look better.

**Dwells advance on every quote, never on decision ticks alone**, and any interval the quote
path recorded as stale or invalid breaks them. A decision tick cannot infer uninterrupted
dwell across time it never observed. This was a real bug once already.

**Never add a synthetic baseline as a fallback.** No artifact means Warmup and nothing arms.
The test project has a synthetic baseline; it lives only there, it is named so it cannot be
mistaken for a trained one, and the indicator cannot load it.

**Thresholds are experimental.** Before changing any number in `CandidateConfig`, write down
why and get it reviewed. They are hypotheses, not tuned parameters, and quietly nudging one
to make a historical chart look better is how a backtest gets invented.

**Mutate before believing the suite.** The eight mutations in `BUILD-REPORT.md` were all
caught. If you add a rule, inject its inverse and confirm something fails.

**Time is Central, one zone.** `spec/DEFAULTS.json` is reproduced verbatim from the handoff
and still says `America/New_York`; that file is a spec artifact and is not the shipped
default. The indicator defaults to `America/Chicago` 08:30–15:00 and prints Central
everywhere. Baseline bucketing is minutes-from-session-start, so it is zone-agnostic.

## ATAS specifics verified on this build (8.0.14.399)

The reflection probe in `_reflect/` produced `COMPATIBILITY.md`. Re-run it after any ATAS
update rather than trusting the docs. Three things the handoff got wrong:

- `MarketByOrders` does not exist as an indicator property.
- `OnDispose` is on `BaseIndicator`, not the documented callback surface.
- `MarketDataArg` carries **no receive timestamp and no source sequence**, which is why
  feature time is stamped by the adapter from a `Stopwatch` and why no trade deduplication
  happens at all.

`AddAlert` takes **WPF** `System.Windows.Media.Color`, not `System.Drawing.Color`.

`OnBestBidAskChanged` delivers one side at a time. The adapter holds both sides and emits a
paired event; `QuotePath.ApplyBestQuote` applies the pair atomically. Applying the sides
sequentially manufactures a crossed book — that was the other real bug.

## Read-only, permanently

No order path, no account access, no connection changes. `Config.Validate()` rejects
`automaticTradingEnabled`, and both the test suite and the smoke test assert there is no
reachable order-submission surface. Keep it that way.
