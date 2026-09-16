# Ocean's Read

One structural read of the auction, in the order the method reads it. Shows in ATAS as
**Ocean → Oceans Read**.

The methodology this is built from says: no indicators, no guesswork, just learning to read what
the market is actually doing. So this is not an indicator in the usual sense — it does not
produce a signal from a formula. It **measures** the things the method reads, in the method's
own vocabulary, and prints them in one box so they can be read together. The chart keeps only
the levels the box refers to.

    dotnet build OceansRead.csproj -c Release   # build
    cd _test && dotnet run -c Release           # 348 checks
    cd _test && ./mutate.sh                     # 47 mutations, all must be killed
    ./deploy.ps1                                # test, build, smoke, copy the DLL

**Restart ATAS after deploying** — it reads the indicator folder only at startup.

---

## What it measures

| The concept | What is actually computed | Where |
|---|---|---|
| **Auction Market Theory** | Balancing or imbalancing, from two independent measurements: how much of the prior session's value today is still trading in, and how much of the ground covered turned into net progress. They disagree often; when they do the answer is **Transitioning**, not a casting vote. | `AuctionMath.cs` |
| **Volume Profile and TPO** | One dense ladder carrying both measures side by side — contracts traded, and how many time brackets visited each price. Point of control, value area, shelves and gaps as bands, single prints, poor highs and lows, tails, and the profile shape (D / P / b / double distribution / elongated). | `ProfileMath.cs` |
| **Acceptance** | A break is a bar **closing** through a level, never a wick. What settles it is **time and ground** on the far side — both, and both scaled by the market's own measured rhythm. Closing back through before either threshold is met is a rejection, whatever the excursion reached. The developing point of control crossing to the far side is noted separately: that is value following, the strongest form. | `AcceptanceMath.cs` |
| **Elliott Wave** | The character of the last rotations (impulsive: legs clear of each other; corrective: legs treading on the same ground) plus a five-wave count **only when all four hard rules hold** — wave 2 holding the start of wave 1, wave 3 clearing the end of wave 1, wave 3 not the shortest, wave 4 out of wave 1. Otherwise it prints "no valid count" and which rule broke. | `WaveMath.cs` |
| **VWAP, top down** | Yearly, quarterly, monthly, weekly, daily and session anchors, each with deviation bands, drawn as ATAS data series. The box reports which side price is on for each and whether the anchors are stacked in order or disagreeing. | `VwapMath.cs` |
| **Developing Value** | The session profile rebuilt on every closed bar, plus the point of control's trail — which way value is going, measured over a span of **time**, not a number of bars. The classic value relationships against the prior session: higher, overlapping higher, unchanged, inside, outside, overlapping lower, lower. | `ProfileMath.cs`, `AuctionMath.cs` |
| **Rhythm** | The market's own cadence, measured rather than assumed: the median, quartiles and 90th percentile of completed rotation size **in ticks and in minutes**, up legs and down legs separately. The leg in progress is placed against that distribution — at the turn, developing, extended, or beyond. This is also what every acceptance threshold is scaled by. | `RhythmMath.cs` |
| **Orderflow** | Delta per bar, per session and per leg; cumulative delta sampled **where rotations turned**, so a divergence compares the delta at the price extreme rather than the delta extreme; the footprint in a band around the level under test; absorption; and open interest read against price — new longs, short covering, new shorts, long liquidation. | `OrderflowMath.cs` |

---

## The read box is the deliverable

Everything above lands in one panel, in the order the method reads it, and every line carries
the measurement it was judged on. At the bottom is a **playbook** and a **checklist** — not a
signal:

- **Balancing → FADE THE EDGE.** The edges of value are the trade, the point of control is the
  target. Gates: price at an edge of value, the rotation has already run its usual distance, the
  leg is running *into* the edge, the tape is failing there, and value is not breaking out.
- **Imbalancing → GO WITH THE MOVE.** The move is the trade, the pullback is the entry. Gates: a
  side to take, acceptance established, entering at the turn rather than late, the VWAP stack
  agreeing, and orderflow confirming.
- **Transitioning → STAND ASIDE.** The measurements disagree. That is worth naming, and calling
  it either balance or imbalance loses it.

Every gate met is `ACT`. One short is `PREPARE`, naming the missing one. More is `WAIT`.
Nothing here is a trade signal; orderflow confirms a read, it does not produce one.

---

## What it refuses to do

All three refusals exist because the failure mode is a clean-looking chart rather than an error.

1. **It will not cut a session until the bar clock is settled.** Whether ATAS stamps bars UTC or
   already local is resolved from the data (`TimeContext.cs`) — by the wall clock on a live
   chart, or by finding the daily maintenance halt on a stale one. A whole-day shift does not
   look wrong; it draws a perfectly plausible profile of the wrong session. Unresolved, the box
   prints the problem and stands the read down.

2. **It will not judge acceptance without a measured rhythm.** A fixed tick count that means
   "far" on a quiet overnight means "noise" at the open. Below `Fewest rotations to call it a
   rhythm` no test is opened at all, and the box says so.

3. **It will not print a wave count unless every rule holds.** A labeller that always finds five
   waves has told you nothing, because it would have found five in a random walk.

There is a fourth, softer one: **if the chart carries no footprint**, the profile is built from
bar closes and the box says that in as many words, in red, at the bottom. A profile built from
closes is a different object from one built from the tape and must never pass for one.

---

## Time

One zone. Central, everywhere — settings entered in it, the math done in it, the labels printed
in it. Session windows: cash 08:30–15:00, power hour 14:00–15:00, Sunday reopen 17:00.

**The CME maintenance break is 16:00–17:00 Central, not 15:00.** 15:00 is the cash close and
trading carries straight on through it. An indicator told to look for an empty hour at 15 finds
none, never resolves its clock, and then draws nothing at all with no error pointing at the
cause. This has shipped wrong three times across these projects; see `~/dev/CLAUDE.md`.

---

## History depth

Every anchor is only as good as the bars loaded behind it.

- A **prior-session** profile needs the previous session loaded.
- A **yearly VWAP** needs a year. An anchor the loaded history does not reach is marked
  incomplete: it is not drawn, and the box tags it with `?` rather than hiding it, because
  knowing the yearly reading is unavailable is itself part of a top-down read.
- Only the first instance of an anchor on the chart can be short of history. Every later one
  began after the chart did, so it is whole by construction.

Sessions further back than about six thousand bars from the right edge stop being rebuilt bar by
bar — they still accumulate, but their developing value is not tracked. That only affects reading
history; the live session is always fully developed.

---

## Testing

`_test` compiles nine source files that carry no ATAS type at all, which is why they are separate
files: the whole read — profile, rhythm, acceptance, waves, VWAP, orderflow and the box itself —
runs off-platform.

348 checks, and **47 mutations, all killed**. The mutation run is worth more than the check count:
the first pass reported eight survivors, and every one of them was a real hole.

- Three were weak test data — a shelf trim where the light shelf was never a candidate, an
  elongation threshold that would have passed at any value, a `Quantile` overload that was never
  interpolated.
- One was a genuine code fault: the outside-bar guard in `SwingTracker` was unreachable, so the
  protection it claimed to provide came from the `if/else` structure instead. It is now `!extended`
  and both directions are tested.
- One was an assertion that could not tell "no test was opened" from "a test opened with zero
  thresholds and resolved instantly".
- **Three were the harness lying.** The source files are CRLF, the patterns are LF, so multi-line
  mutations silently failed to apply — and `grep -F` treats a multi-line pattern as "any one of
  these lines", so the presence check did not catch the miss either. Every multi-line mutation
  reported SURVIVED against code that was never touched. The matching now lives in `mutate.pl`,
  which joins the pattern with `\r?\n`.

Two more traps carried over from `oceans-developed`, both of which produce convincing false
results: never capture `dotnet run` through `$( )` (the persistent build server inherits the pipe
and the substitution never returns), and always `touch` after restoring a `.bak` (`mv` restores
the old mtime, MSBuild skips the rebuild, and the next run silently tests the previous mutation).

A third was found here: a mutation that will not **compile** proves nothing about the assertions,
and counting it as a kill is how a suite full of holes comes to look complete. The harness now
reports those as `BROKEN` and fails the run.
