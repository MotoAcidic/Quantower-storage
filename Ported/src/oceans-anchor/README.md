# Ocean's Anchor

An ATAS indicator for one trade: price arrives at a volume shelf the last two weeks agree on,
something large absorbs the aggressors there without price moving, and the delta turns.

Signal chart **NQ** (cleaner institutional tape); execution **MNQ**, manual, from its own DOM.
This is an indicator — it never places an order. Risk stays with the funded-account drawdown and the
personal rules. Its job is to make the A+ moment unmissable and to write down what it looked
like, so the thresholds stop being guesses after ten sessions.

    dotnet build OceansAnchor.csproj -c Release
    cd _test && dotnet run -c Release     # 198 checks
    ./deploy.ps1                          # test, build, smoke, copy DLL

**Restart ATAS after deploying** — the indicator folder is read only at startup.

---

## What it draws

**Zones.** Outlier high-volume shelves, at most four, ranked:

| Rank | What |
|---|---|
| 1 | A naked POC that is also a composite shelf |
| 2 | A fresh prior-RTH POC (or a second distribution) |
| 3 | A composite-only shelf |
| 4 | The overnight POC |

Opacity follows rank, so the strongest zone is the one the eye lands on without reading
anything. Spent zones (traversed three times) and zones further than 1.5 ADR from the open fade
almost out — still visible, because knowing a level is used up is information, but never
competing with a live rank 1.

The filter that makes this a playbook rather than another volume profile is the outlier test: a
peak survives only if it is at least 70% of the POC bin. *If you have to squint, it is not an
HVN.* If a rendered zone ever surprises you, tighten `NodePeakPct` before touching anything else.

**Absorption marks.** Copper rectangles spanning the cluster extremes, sized by volume. Tape
marks are solid; cluster-path marks are more transparent, so a reconstruction can never be
mistaken for the real tape at a glance.

**Status line.** One line per zone that is doing something:

    ANCHOR r1 21384.00-21402.00 · ARMED long
    ANCHOR r1 21384.00-21402.00 · TRIGGERED 2 evts · clock 1/3
    ANCHOR r1 21384.00-21402.00 · CONFIRMED ▶ stop 21379.50, 1.5R 21411.00+

The stop and 1.5R come free from the cluster extremes. That readout is the point: at signal time
the question is not "is this a setup" — the indicator just said it was — it is "does the R:R
clear the floor".

## The two absorption engines

**Live tape** is the real signal: actual cumulative market orders, the ATAS analogue of Bookmap
bubbles. A print qualifies if it clears the size floor, lands inside a live zone, comes from the
right side (support wants *sell* aggressors — someone is hitting the bid and the bid is not
moving), and then goes nowhere for two seconds.

**Cluster forensics** reconstructs an approximation from footprint bid/ask so history renders on
chart load. Four shape tests — outlier delta, close back inside, volume concentrated at the
extreme with the right delta sign, and a wick — three of which must pass. Without it, calibration
would mean ten sessions of watching a screen in real time.

Their agreement rate is itself a calibration output. Below ~60%, the cluster thresholds are
mis-set and the historical marks should not be trusted.

## The state machine

`Dormant → Armed → Triggered → Confirmed`, with `Expired` and `Broken` as the ways out.

- **Armed** — price is inside a live, ranked, non-spent zone.
- **Triggered** — qualifying absorption printed while Armed. Events within 90s stack: they raise
  the grade and widen the cluster, which widens the stop to where it belongs.
- **Confirmed** — within three bars, either the delta flips or CVD makes a higher low against an
  equal-or-lower price low. The second is quieter and often earlier: sellers spent less to reach
  the same place.
- **Expired** — the clock ran out. Absorption that keeps absorbing is distribution.
- **Broken** — the cluster extreme traded through. No re-fade that session.

Guards: the one-timeframing filter (never fade the freight train), the 08:30–10:30 A+ window, and
Friday suppression. Outside the window zones still render; nothing sounds.

## The CSV is the actual edge

`Documents\ATAS\OceansAnchor\anchor_log.csv`, one row per Triggered episode, finalised when it
resolves. Every threshold ships as a guess — `SizeFloor` 100 is an NQ round number, not a
measurement. Run ten sessions passive, then offline:

- `SizeFloor` → p95 of `largest_trade` on tape-path rows (keep 100 if it lands near p95)
- expiry rate over ~50% → loosen `ClockBars` to 4, or tighten zone quality
- tape-vs-cluster agreement under ~60% → fix the cluster thresholds first
- then the kill criteria: T1 hit under ~55%, or realised R under 1.3 → zones first, floor second,
  **one knob at a time**

**Silent calibration** (Calibration group, ships ON) is how the ten sessions are run: nothing
drawn, nothing sounded, and the CSV log, live tape and cluster path forced on regardless of
their own toggles. Only a clock or log failure prints on the chart. Turn it off once the
thresholds are set.

Rows are keyed on `date,time_ct,zone_kind,side`. ATAS replays history on every load and
restart, which finishes the same episodes again; a row whose key is already in the file is
not appended. Rows for past days on first load are cluster-path only — tape history covers
the current session — so compute the agreement rate on days that were run live.

## If it draws nothing

Check **`HaltHour` is 16, not 15.** 15:00 Central is the cash close and trading carries straight
on through it; the genuinely empty hour is 16:00–17:00. With 15 the bar clock never settles, and
the indicator draws its cannot-settle message instead of zones, with nothing pointing at the
setting. This has shipped wrong three times across the `oceans-*` suite.

The clock refuses to guess by design. Every zone is anchored to a session window, so a wrong
UTC/local reading does not look wrong — it produces a clean, plausible, silently misplaced set of
shelves. It either settles from evidence (market clock, wall clock, weekend gaps, the daily halt)
or draws the reason it could not.

## Deviations from the build spec

The spec was written against the docs; these are what the installed DLLs actually expose,
verified by reflection against ATAS 8.0.14 (`_reflect` in `oceans-pivot-decoder` dumps them).

| Spec says | Reality here |
|---|---|
| Multitarget `net8.0;net10.0` | net10.0 only — the ATAS assemblies are net10.0 and net8 cannot reference them (CS1705) |
| Use `CrossColor`, never WPF colors | `CrossColor` does not exist. Settings use `System.Windows.Media.Color`, `RenderContext` takes `System.Drawing.Color` |
| `PriceVolumeInfo.Delta` | Not present — derived as `Ask - Bid` |
| `trade.IsEqual(prev)` for dedup | No such method — identity compared on start time, start price and direction |
| `ChartInfo.GetXByBar` | Lives on `ChartInfo.PriceChartContainer` |
| `CumulativeTradesMode.Aggressive` | Not a member; modes are Strong/Medium/Weak/Filter/FilterLimited. Exposed as a setting, default Medium |
| Walk `High → Low` by tick for profiles | `GetAllPriceLevels()` exists and enumerates only levels that traded. Tick walk kept as fallback |
| Alert on `bar == TotalBars` | Every transition is decided at bar close, so "now" is `CurrentBar - 2`. The literal reading silences every confirmation |
| Request tape history after loading | Requested **before** today's bars are folded, and the fold waits — see below |

**The history gate is the one substantive design change.** Folding every bar and then applying
the tape response promotes zones using trades from hours ago against a state machine that has
already advanced to now: a zone armed at 09:15 and broken at 09:40 gets triggered by a 09:15
print while sitting Broken. Since the CSV *is* the calibration, a wrong row is worse than a
missing one. So the tape is requested before the session's bars are folded and each bar replays
its own trades in its own bar, in time order — exactly as if the indicator had been running live
all session. If the feed never answers, it times out after three passes and carries on with the
cluster path, which is what covers history anyway.

## Layout

    AnchorClock.cs       bar clock + bar size          (ATAS-free, ported from PivotDecoder)
    AnchorSession.cs     trade dates, window overlap   (ATAS-free)
    AnchorModels.cs      Zone, AbsorptionEvent, Episode
    AnchorProfile.cs     POC, value area, HVN shelves  (ATAS-free)
    AnchorZones.cs       ranking, naked POCs, spent    (ATAS-free)
    AnchorAbsorption.cs  both paths, percentiles       (ATAS-free)
    AnchorState.cs       state machine + guards        (ATAS-free)
    AnchorLog.cs         CSV                           (ATAS-free)

    OceansAnchor.Core.cs        settings, OnCalculate, bar adapter
    OceansAnchor.Zones.cs       session segmentation, profile folding
    OceansAnchor.Absorption.cs  tape plumbing, cluster path
    OceansAnchor.History.cs     the history gate and replay
    OceansAnchor.Render.cs      OnRender
    OceansAnchor.Alerts.cs      alerts, marks, episodes

Everything above the line is free of ATAS types precisely so `_test` can exercise it on synthetic
bars. That is not tidiness — the outlier filter, the value area and the state machine are where
the bugs live, and they are only checkable off-platform.

## Not in v1

Iceberg/refill detection via `MarketDepthChanged`; GEX confluence from Tide Engine CSV; overnight
inventory panel; suggested size from stop distance vs dollar risk; continuation alerts on Broken
zones; multi-timeframe zone sources.
