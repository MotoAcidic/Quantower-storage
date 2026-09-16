# CLAUDE.md — Ocean Delta Cross

Read `README.md` first: it says what the thing is and why the horizontal is drawn where it is.
`~/dev/CLAUDE.md` holds the rules shared by every `oceans-*` project — they all apply here.

## The line that must not move

**No interpolated cross price, ever.** The tick at which the running sum passed zero is not
recoverable: bar data records what traded at each price, never in what order. Any future request
for "draw the line exactly where it crossed" has to be answered with that, not with a plausible
number. Same family as the ratio fallback rule in `~/.claude/CLAUDE.md`.

The one intra-bar fact that IS recoverable is `Flip.ShareOfBar` — how far through the crossing
bar's *net delta* zero was reached. It is printed in the readout and must never be multiplied by
the bar's range to make a price.

**The cluster fallback is labelled, not silent.** `CrossMath.Price` in cluster mode with no
qualifying cluster returns the close AND a note saying so. Do not shorten that note to "close".

## Space is a constraint, not a setting

The trader's first reaction to the built version, with screenshots of four delta panels stacked on one
chart: *"its taking up way too much chart real estate."* The 90 px band was the same problem this
indicator existed to fix, wearing one name instead of four.

So the defaults are small and the expensive layouts are opt-in: `DeltaLayout` defaults to two
~9 px heat strips, `Readout` to one line, `CrossLabels` to one line. `PanelLayout.CrossOnly`
spends nothing at all and still marks every flip — that is the shape of the deliverable, and the
band is only ever the working shown. `BandRect` caps the band at a third of the panel whatever
the settings say.

**Before adding anything that draws, decide what it costs in pixels and default it off if the
answer is more than a strip.** The heat strip is the pattern to copy: colour carries the sign,
alpha carries the size, and the crossing is where the colour turns over — which is the one thing
the tall track showed that had to survive the shrink.

## Two more things that are measurements, not claims

**A flip zone is the cluster span, or it is a price.** `ZoneMath.Zone` widens a bare cross price
by exactly nothing when no cluster qualified — it sets `FromClusters = false` and the renderer
draws a dashed line instead of a band. Widening a bare price by "a few ticks" would draw a band
nothing traded in, which is the ratio-fallback mistake in another costume.

**"Lines in the sand" are repeat one-sided absorption, NOT identified icebergs.** There is no
order book behind `Icebergs.cs`. A refreshing iceberg leaves this footprint; so does one big
resting order that was never replenished. The setting description, the README and the readout all
say so, and none of that wording is decoration — do not tighten it into "finds icebergs".

If a future task needs real icebergs, the MBO `IcebergsTracker` already installed in ATAS writes
order-level events to `%APPDATA%\ATAS\IndicatorData\IcebergsTracker\`. That is the ground truth
to validate this against. It only works live; the footprint scan works on history.

**`IcebergSearch.Scan` abandons an oversized window whole and says why.** It never returns a
partial tape: half a window looks exactly like an answer. Same rule as the bar-count guard in
`OnRender`.

## Design decisions that already cost something

- **The cross is anchored to the bar that CROSSED, not the one that confirmed.** The confirmation
  is evidence about a moment that has passed. Anchoring it to the confirming bar is the obvious
  implementation and it is wrong; there is a named test for it.
- **The session's first side is not a flip.** Session delta starts at zero, so the first move is
  the session choosing, not reversing. Default off, or every session opens with a cross on it.
- **A bar-gap suppression changes the side silently.** When `MinBarsBetweenCrosses` swallows a
  recross, the engine still takes the new side. Not doing so leaves it facing the wrong way and
  it never finds the next flip. Named test.
- **Unbacked flips are dashed, not hidden.** A flip nobody paid for is still information.
- **Flip zones cover sessions OLDER than the one in progress only.** The current session draws
  full crosses; a band under its own cross says nothing the cross has not. Turning
  `ThisSessionOnly` off deliberately gives both, as emphasis.
- **Merging counts SESSIONS, not flips.** Two flips in one session are one session changing its
  mind. A merged zone keeps the OLDEST session stamp so "held since" reads right, and the
  merge hands back newest-first so the draw cap keeps what matters.
- **Break detection uses CLOSES and only from the bar the level first traded.** Wicks through a
  level are the level working; counting them would throw away every level that ever held.
- **The iceberg window is anchored to the newest bar, never the visible range.** Keyed on
  `LastVisibleBarNumber` it would rebuild and move every time the chart scrolled.

## Replay

`DeltaEngine.Feed` is safe to call repeatedly for the forming bar: it snapshots the state before
each new bar index and restores on a repeat, truncating `Flips` back to the snapshot's count.
This is the reason the flip logic is a state machine and not a re-scan — a re-scan from bar 0 on
every tick is O(n²) across a history load.

`OceansDeltaIndicator.SyncMarks()` follows that: it truncates, checks that the marks it kept
still point at the same flips, and tops up. A cross that a forming bar arms and then takes back
has to disappear from the chart, which is what the truncate is for.

## Traps hit here

- **`Visible` collides with `ChartObject.Visible`** (CS0108). Renamed to `PickCrosses`. Third
  member of the family after `Labels` and `ValueAreaPercent` — build clean, read the warnings.
- **`RenderContext.MeasureString` returns int-valued `Size`, and `DrawString` takes int
  coordinates.** Float text metrics do not compile against these overloads. All layout arithmetic
  in this file is int on purpose.
- **`ClusterSearch` divides by the level's volume** to get the lean, so the `Volume <= 0` guard is
  load-bearing, not tidiness. Removing it throws rather than mis-answering — a mutation proved it.
- Cluster settings are baked into a mark when it is built, so `_markSignature` clears the marks
  when they change. ATAS normally recalculates on a settings change and this never fires; it is
  there so a build that did not recalculate cannot show yesterday's answer under today's labels.

## Testing

123 checks in `_test`, no ATAS required. Every batch passed first try, so both were mutated:
**36 mutations applied across two passes, all 36 killed by a named check** — including the
tie-break in `ClusterSearch.Biggest`, the share-of-bar sign, both forming-bar rewind paths, the
passive-side inversion in `IcebergSearch`, close-vs-wick break detection, and the abandon-don't-
truncate guard. Re-run the mutation pass after any change to `DeltaMath.cs` or `Icebergs.cs`.
