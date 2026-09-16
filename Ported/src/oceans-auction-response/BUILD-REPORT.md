# Build and test report

Checkpoint delivered: **Phase zero (compatibility) + Phase one (deterministic baseline) +
Phase two (integration and UI), with Phase three research surfaces present but disabled.**

Built and run on this machine, 2026-09-12. ATAS 8.0.14.399, .NET SDK 10.0.100,
Windows 11 Pro 10.0.26200 x64.

## What was actually run

```
dotnet build AuctionResponse.slnx -c Release      -> Build succeeded, 0 errors, 0 warnings
cd tests/AuctionResponse.Tests && dotnet run      -> ALL TESTS PASSED (390 checks)
cd _preview && dotnet run                         -> PREVIEW OK (layout PNGs)
cd _smoke && dotnet run                           -> SMOKE OK
cd _reflect && dotnet run                         -> produced COMPATIBILITY.md
```

Test sections and what each covers:

| Section | Covers |
|---|---|
| Numerical fixtures | All 21 cases in `spec/TEST-VECTORS.json`, driven from the file rather than retyped, plus a presence check so a case cannot be silently dropped |
| Units, grid and configuration | Tick-grid exactness and off-grid refusal, config validation, SetupRequired on missing owner inputs, config hashing |
| Health and ingress | Bounded queue, overflow flag surviving the drain, availability-vs-zero, time-weighted depth integral |
| §19 UI and safety invariants | Frozen vs live evidence, N/A with reasons, overflow arrows, no marker before detection time, no percentages in baseline, label dwell, no order surface, display settings cannot change a signal |
| §19 candidate failure | Each of the ten conditions defeated independently at event level, each asserting *which* gate refused |
| §19 state machine | Dwell mechanics, boundary touch equality, confirmation exactly at 30s, refusal after 30s, timeout, dwell reset, stale interval during dwell, invalidation boundary, reconnect, level deletion, session exit, config change, cooldown isolation, immutability, support mirror |
| §19 recording and replay | Byte-exact round trip, deterministic repeated playback, render frequency independence, appended future events leaving prior output unchanged, duplicate trades staying distinct, snapshots contributing no flow, incomplete tail recovery |
| Layout | Overlap, escape, tier degradation, column alignment, setup guidance, N/A preservation, clip balance — across 5 display scalings and 7 panel sizes |
| Research | Baseline gates, artifact round trip and tamper refusal, compatibility refusals, ridge recovery and shrinkage, activation gates, economic identity, manifest contract |

## Mutation testing

A passing suite proves nothing until it has been shown to fail. Eight deliberate defects were
injected, one at a time, rebuilt and re-run:

| Injected defect | Caught by |
|---|---|
| `>` becomes `>=` on the attacker-volume quantile | 2 checks |
| Confirmation allowed after the 30s deadline | 4 checks |
| Rolling window becomes left-closed | 2 checks |
| OFI ask-side sign flipped | 4 checks |
| Dwell survives a stale interval | 1 check |
| Confirmation buffer sign flipped (`K-2` becomes `K+2`) | 10 checks |
| Off-grid prices silently rounded onto the grid | 2 checks |
| Zero depth reported as balanced instead of unavailable | 5 checks |

All eight were caught. The tree was restored and the suite re-verified green afterwards.

## Defects found and fixed during the build

These were real bugs the suite caught, not test adjustments:

1. **Transient crossed book.** Applying a paired best-bid/ask update one side at a time
   manufactured a crossed quote whenever the whole quote moved further than the spread,
   which was then recorded as an invalid interval that never occurred on the feed.
   `ApplyBestQuote` is now atomic.
2. **Dwell inferred across unobserved time.** A confirmation could complete across a gap in
   which no quote arrived, which is precisely what §12 forbids. Dwells are now broken by any
   interval the quote path recorded as stale or invalid.
3. **A quiet window read as degraded.** Side quality is a fraction; over zero trades there is
   no fraction to violate. An empty trade window was terminating live candidates as
   `DataInterrupted`. It now reports Ready-with-no-trades, while the candidate rule still
   refuses to arm because its Data gate needs an available side quality.
4. **Insufficient baseline sessions reported as Incompatible.** §8 calls for Warmup in that
   case; Incompatible is reserved for a different instrument, clock or feed.

## Deviations from the specification, recorded deliberately

1. **Section 3 layout, one instance instead of two.** ATAS binds one indicator instance to
   one panel. Rather than requiring two instances plus a shared-engine lifetime keyed by
   instrument, configuration and feed, this build draws the price overlay *and* the companion
   sidebar inside the price panel, the sidebar as an in-chart HUD. The Section 3 *content*
   contract is unchanged. The shared-engine route remains available if a separate ATAS
   subpanel is later wanted.
2. **Session timezone default is Central, not Eastern.** `spec/DEFAULTS.json` is reproduced
   verbatim and still reads `America/New_York`. The shipped indicator defaults to
   `America/Chicago` 08:30–15:00, which is the same session. Baseline bucketing is measured
   in minutes from session start and is therefore timezone-agnostic.
3. **Test framework.** The harness is hand-rolled and zero-dependency rather than a pinned
   third-party framework, so it builds and gates deploys with no package restore. The deploy
   script keys off its exit code.
4. **No trade deduplication at all.** This feed supplies no unique source identifier, so §5's
   "deduplicate only with a verified unique source identifier" resolves to not deduplicating.
   Identical price/size/time trades stay distinct.

## Not verified, and why

These are stated rather than worked around. Each surfaces in the UI as an explicit state.

- **No live-market run.** Everything above is unit, fixture and replay testing against
  synthetic events. The adapter has not been observed against a live or recorded ATAS feed.
  §19's integration tests — opening-burst behaviour, trade-volume comparison against an
  independent ATAS view, disconnect recovery, add/remove cycling, instrument switching, DPI
  and resize, subscription release, paused native replay — **have not been run**.
- **No performance measurement on your hardware.** The §19 targets (callback handoff under
  1ms, feature/state update p99 under 10ms, no more than 10 draw requests per second, no
  unbounded memory growth) are instrumented in `EngineHost` but have not been measured under
  real event rates.
- **No baseline artifact exists.** Nothing can arm until one is built from eligible sessions.
- **MBO is off and stays off.** No snapshot fence and no passive execution linkage have been
  established on this feed, so per §13 and §21 order-level evidence does not proceed.
- **MBP absolute-vs-delta semantics unverified.** Depth is currently treated as absolute per
  price level; this needs to be confirmed by observation on live data.
- **No economic evaluation.** Commissions, exchange fees and execution assumptions were not
  supplied, so §20 is implemented only as the pure `PnLnet` identity.

## Interface correction (second checkpoint)

The first build rendered an unreadable panel. Cause: row spacing used hardcoded pixel
constants while the fonts were sized in POINTS, so every row overran the space allotted to it
(11pt is ~15px at 96 DPI, more at higher scaling). No test could have caught it, because the
layout could not be exercised without ATAS.

**Structural fix.** Layout moved into a new `AuctionResponse.Ui` project with no ATAS
reference, behind an `ISurface` drawing interface. The ATAS project supplies
`RenderContextSurface`; the tests supply `RecordingSurface`, which records every text
rectangle. Readability is now an assertion, not an inspection.

**Verified by test** (`LayoutTests`, within the 390-check suite):

- No text overlaps, across 3 view states x 5 display scalings (100/125/150/175/200%) x 7 panel
  sizes x diagnostics on/off — 180+ cases.
- No text escapes its panel at any of those sizes.
- Overlay labels never collide across 6 zoom densities x 5 scalings, including the degenerate
  case where every feature maps to the same y.
- Data health survives at every panel height, with and without diagnostics.
- Row spacing strictly exceeds the measured line box at every scaling.
- Evidence labels share a left edge and rows are evenly spaced.
- Unavailable values still render `N/A` at every width, and never as a zero.
- Clip pushes and pops are balanced.

**Mutation check.** Re-injecting the exact original defect (`Row() => 13`) fails 180 overlap
cases, and the first reported failure is the header overlapping the instrument line — the same
collision visible in the original screenshot. The suite reproduces the real bug.

**Also fixed, found while doing this:**

- Overlay price labels collided with each other; they now go through a `LabelPlacer` that
  offers each label several slots and drops the text, not the line, if none is free.
- The pressure plot could push Data Health off the bottom of the panel. Health is now anchored
  to the panel bottom and its space carved out first, measured from its wrapped text rather
  than estimated.
- `Zone execution` always read N/A: `FeatureSnapshot.ZoneAttackerVolume` was declared but never
  populated. It now reports for the focused level, with `ZoneVolumeFraction` alongside.
- An empty contract expiry rendered as a dangling separator (`MNQ@Exchange/`).
- `SetupRequired` displayed as a machine token; it now reads "Setup required".

**Setup message.** The crowded diagnostics block is replaced by a `SETUP REQUIRED` section
that names each missing input and how to supply it ("Set \"Baseline artifact path\" in the
indicator settings..."), carried on the snapshot as structured `MissingInput` records rather
than parsed out of a sentence. Full diagnostics are collapsed behind a **Show diagnostics**
setting and now include per-input event counters, so `Trades: no` is explainable: `none yet`
means no trade callback has fired.

**Offline preview.** `_preview/` renders the real layout to PNG through WPF at any size and
scaling, so the interface can be reviewed without deploying and restarting ATAS.

**Not verified in ATAS.** The previews are rendered through WPF, not through ATAS's own
`RenderContext`. What that does not prove is how ATAS measures text and what it reports for
`ChartInfo.DpiX` on a 150%-scaled display. Because all spacing is now derived from whatever
the host measures, the layout should be correct either way — but the panel also reports the
DPI it was given (in diagnostics), and a **UI scale** setting overrides it if the automatic
value is wrong. This needs one screenshot from the running platform to close out.

## Baseline builder (third checkpoint)

The blocker was always that nothing arms without a frozen baseline artifact, and that no
honest way existed to produce one. Resolved by reading it off ATAS footprint history.

**`BaselineBuilderIndicator`** — an offline tool, separate from the monitor. On a 5-second MNQ
chart it walks every loaded candle, filters to the session window in Central, buckets by
30-minute segment and emits non-overlapping 5-second and 30-second samples. `Ask` is
buy-initiated volume, `Bid` is sell-initiated, `Betweens` unattributed, which is exactly the
B, S and U of Section 6. It reports sessions, samples, buckets and any failed gate on the
chart, and writes nothing when the gates fail unless explicitly overridden for inspection.

**Sizing corrected.** An earlier statement in this report and in conversation put the baseline
at 60-90 sessions. That is the Section 20 figure for evaluating whether an edge exists. The
Section 8 artifact needs **10 sessions and 300 samples per bucket and window**; a 5-second
chart yields 360 samples per 30-minute bucket per session, so roughly 10-15 RTH sessions
clears both gates.

**Cross-expiry.** MNQ rolls quarterly, so a strictly same-expiry baseline leaves nothing usable
on roll day. `BaselineConfig.AllowCrossExpiry` relaxes the expiry check only — symbol,
exchange, tick size, clock mode and feed mode remain enforced — and the resolver exposes
`UsingCrossExpiryBaseline` so the UI can say when one is in use. Off by default. Covered by
test, including that a different symbol, exchange or tick size is still refused.

**Honest limit, recorded in the artifact.** Footprint candles carry no bid/ask, so the plot's
response scale is derived from close-to-close rather than the Section 6 midpoint. The artifact
carries `ResponseScaleSource` stating exactly that. It scales a display axis only; the
attacker-volume quantile that gates arming is computed from exact aggressor-tagged volume.

**Why not Bookmap.** The archive was surveyed: 26 GB of `.bmf` feeds plus 203 MNQU6 entries in
the Data library covering 2026-07-23 to 2026-08-12. Both formats are undocumented proprietary
binary, and no CSV-export addon is installed, so extraction would require `pip install
bookmap`, a custom Layer-1 addon, and a manual GUI replay per session. ATAS supplies the same
aggressor-tagged volume through the bridge already proven on this machine. This also matches
the owner's recorded architecture decision of 2026-08-22: ATAS is home, Bookmap validates.
Bookmap remains the intended independent cross-check once there is an artifact to check.

**Still not verified.** The builder has not been run against real loaded history — how many
sessions ATAS will actually supply at a 5-second timeframe is unknown and is the next thing to
find out. Footprint aggressor tagging has not been cross-checked against an independent view.

## Suggested next checkpoint

Phase two completion proper, which needs the live platform:

1. Restart ATAS, attach the monitor to an MNQ chart, turn on Show diagnostics and capture a
   screenshot. It settles two open questions at once: whether the layout is correct at the
   owner's 150 percent display scaling, and whether `Trades`/`Quotes` show counts or
   `none yet` — the latter being a feed question, not an indicator one.
2. Run the baseline builder on a 5-second chart and record what it reports. If it fails a
   gate, the numbers say whether the cause is history depth or the session settings.
3. With an artifact loaded, watch how often anything arms at all. A rule that never fires and
   a rule that fires constantly are both answers, and both are cheap to get here.
4. Record live sessions in parallel, on the actual dxFeed path, as the clean cross-check
   against a baseline built from footprint history.
5. Only once candidates exist in useful numbers does the Section 20 question — 60 to 90
   sessions, costs, delay sensitivity — become worth asking.

Nothing in step 5 is a foregone conclusion. If the edge is not there after costs, the honest
outcome is descriptive use or stopping.
