# CLAUDE.md

AsiaWick — Asia-session top-wick signals for MNQ, in two halves: an **indicator** that marks and
alerts (Phase 1, live) and a **ChartStrategy** that trades them (Phase 2, gated behind a
backtest and shipping with `Signal only` ON). Started 2026-09-09. See README.md for behaviour,
settings and the full list of deviations from the build spec. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`AsiaWickClock.cs` bar clock + `IBarWindow` · `AsiaWickMath.cs` the signal core · `AsiaWickRisk.cs`
the guards · `AsiaWickIndicator.cs` Phase 1 · `_strategy/AsiaWickStrategy.cs` Phase 2 ·
`_test/` math harness (+ CSV cross-validation mode) · `_smoke/` constructs both outside ATAS ·
`_reflect/` API prober.

Two assemblies, one core. The first three files are ATAS-free and are compiled into **both**
`OceansAsiaWick.dll` (→ `%APPDATA%\ATAS\Indicators`) and `OceansAsiaWickStrategy.dll`
(→ `%APPDATA%\ATAS\Strategies`). `_strategy/` is excluded from the parent csproj glob, or the
build fails on duplicate assembly attributes.

## The rule that shapes the whole design

**One signal implementation, two consumers.** The indicator and the strategy must never be able
to disagree about what fired. Anything about the signal goes in `AsiaWickMath.cs`; anything about
position or orders goes in the strategy. If you find yourself reimplementing a condition, stop.

## Things that are the way they are on purpose

**The current bar folds into the Asia high AFTER evaluation.** Reverse it and every bar sweeps
itself. There is a test (`level predates bar`) and a mutation check pinning this.

**A segmented level must be broken at BOTH ends of every run.** `ShowZeroValue = false` only
suppresses the axis label, not the line: the untouched gap bars still hold zero and still render,
so the last of them gets joined to the first real value and the segment grows a vertical line
from the level down to price zero, off the bottom of the chart. It appears on the **left** edge
of every segment, because the right edge was already broken — that asymmetry is the tell. Shipped
wrong once (2026-09-09). The decision now lives in `LineSegmenter` in `AsiaWickMath.cs`, which is
ATAS-free and tested, so the indicator only executes instructions.

**Bar-clock resolution, never a fallback.** The build spec said "convert every candle timestamp
to CT", which assumes stamps are UTC — they are not reliably UTC. A wrong offset draws a clean,
plausible, misplaced level and a trade taken off it, so the clock either resolves from evidence
or the indicator draws its error instead. Copied from `oceans-market-view/TimeContext.cs`; keep
them in sync.

**Timeframe guard measures the bar interval from the stamps**, rather than parsing
`ChartInfo.TimeFrame` — that string's format is not contractual and a misparse would silently
disable the guard.

**The halt hour is 16:00 CT.** Only used by the clock's fallback detection. Note the global
`~/.claude/CLAUDE.md` says 15:00–16:00 while `~/dev/CLAUDE.md` says 16:00–17:00 — the latter is
verified against the CME calendar and records the mistake shipping three times. Unresolved
between the two memory files; flagged in README.md.

**`ChartStrategy` derives from `Indicator`, not `Strategy`.** So
`ATAS.Strategies.StrategyLoggingExtensions.LogWarn` does *not* apply. Log through
`Utils.Common.Logging.LoggerHelper` (`using Utils.Common.Logging;`).

**Marked spent before the order is sent.** `_enteredTonight = true` happens *before* `OpenOrder`.
If the send throws or a fill never confirms, the night is still over — one attempt means one.

**The protective stop goes on only after a fill is confirmed** (`OnNewMyTrade`). Placing it off
the entry *send* would leave a naked stop working if the entry were rejected.

## Testing

`cd _test && dotnet run -c Release` — 124 assertions, no ATAS needed.

**Mutate before believing it.** This suite passed first try and only earned trust after ten
deliberate breaks were each confirmed caught (see README). Two things the mutation round
actually found: the harness aborted on the first bad index instead of reporting a failure — now
every group runs through `Case()` and a throw is a reported failure — and `WeekendClose` was
shadowing `FridayTradeDate` in the entry log.

One equivalent mutant: committing the PDH mid-RTH changes nothing, since the level is only read
during Asia, when RTH is closed either way.

`cd _test && dotnet run -c Release -- bars.csv [utc|local]` prints the signal set for a CSV —
this is acceptance test 5, still open because the reference `asia_wick_backtest.py` and
`asia_wick_overnight.pine` were **not found on this machine**. The core was built from §2 of the
build spec, which the spec says wins on disagreement. Cross-validate as soon as they turn up.

## Still open before this can trade

Market Replay checks (5 nights), the backtest gate, and confirming that `OrderTypes.Stop` with
`TriggerPrice` places where expected. `Signal only` stays ON until all three are done.
