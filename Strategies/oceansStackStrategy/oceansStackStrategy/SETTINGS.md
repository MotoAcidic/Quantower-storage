# oceansStackStrategy — Settings Guide

A live port of `Quantower-storage/TradingView/ocean.pine` ("Ocean's Stack v2") — see
`Quantower-storage/CLAUDE.md`'s Strategy Catalog entry for the full build history and design
decisions. This file is the settings reference, kept current with the code's own defaults.

**No dry-run, no sim/eval gate.** Same explicit choice as `finchDomScalpStrategy` — this strategy
places real orders the instant it's attached and enabled. Attaching it to a sim/eval account is on
you. **This strategy has zero live track record** — cross-check its computed VAH/VAL/POC against
the same day's Ocean's Stack Pine chart directly before trusting a real signal.

## How a trade fires

This is a once-per-day, morning-window trigger, not a continuous scalp. Every gate below has to
line up:

1. Prior day's value area (VAH or VAL) exists.
2. That edge scores at least `Signal: minimum score to arm` out of 3 — one point each for: the
   edge existing at all, a held absorption print near it from a prior session, and a low-volume
   ledge just beyond it.
3. A qualifying liquidity pool (overnight/Asia/prior-day/prior-week high-or-low, whichever are
   enabled) sits within `[pool min, pool max]` distance beyond the edge.
4. Current time is inside the sweep window (default 09:30–10:30 ET) — outside it, nothing can arm
   regardless of score/fuel.
5. Price sweeps past that pool (a new high beyond it for the top/short zone, a new low beyond it
   for the bottom/long zone).
6. A later bar's CLOSE moves back inside the value area, AND the cumulative delta since the sweep
   began confirms the reversal direction.

Only then does an entry fire: stop beyond the sweep's own extreme, target at the OPPOSITE
value-area edge. Prior-day POC (T1 in the source script) is logged on the signal line as an
informational level only — never traded, per an explicit "single contract, one target" decision.

## Instrument & polling

| Setting | Default | Notes |
|---|---|---|
| Symbol | — | MNQ, matching `finchDomScalpStrategy`'s own instrument. |
| QQQ Symbol | — | Optional, informational only (see below) — leave unset to disable the whole QQQ pathway. |
| Period | 1 minute | Everything downstream assumes 1-minute bars. |
| Start Point | 21 days back | Needs enough backlog for a full prior week plus absorption context on first attach. |
| Quantity | 1 | Single contract — no scale-out. |

## Profile

| Setting | Default | Why |
|---|---|---|
| NQ bin size (ticks) | 10 (2.5 pts) | Volume-profile bin width — matches the source script's own `binPts` default. |
| QQQ bin size ($) | 0.05 | Informational only. |
| Value area (%) | 70 | Standard value-area convention — the % of volume the VAH/VAL expansion targets. |

## Sessions (all America/New_York — converted from the source script's America/Chicago)

| Window | Default (ET) | Used for |
|---|---|---|
| RTH | 09:30–16:00 | Value-area construction, prior-day H/L. |
| Overnight | 18:00–09:30 | A fuel-pool candidate. |
| Asia | 20:00–02:00 | A fuel-pool candidate. |
| Sweep window | 09:30–10:30 | The ONLY window the actual trigger is evaluated in. |
| QQQ RTH | 09:30–16:00 | QQQ's own value-area construction (informational). |

Overnight and Asia both cross midnight — handled correctly, same wraparound logic
`finchDomScalpStrategy`'s own session filter uses.

## Absorption proxy (real order-flow delta, not the source script's candle proxy)

| Setting | Default | Why |
|---|---|---|
| Bar volume >= x * 50-bar SMA | 1.2 | How much above-average volume a bar needs to even be considered. |
| \|delta\| >= x * bar volume | 0.15 | How one-sided that volume has to be. |
| Must hold intraday | true | A print gets invalidated if price closes back through it by more than the tolerance before the session ends. |
| Hold-break tolerance (ticks) | 20 (5 pts) | How far is "broken." |
| Counts near edge within (ticks) | 60 (15 pts) | How close a held print has to be to a value-area edge to count toward that edge's score. |

## LVN ledge

| Setting | Default | Why |
|---|---|---|
| Bin volume <= % of max bin | 30 | How "empty" the area just beyond an edge has to be to count as a low-volume node. |
| Band beyond edge (ticks) | 40 (10 pts) | How far beyond the edge that check looks. |

## Fuel pools

| Setting | Default | Why |
|---|---|---|
| Pool minimum distance beyond edge (ticks) | 20 (5 pts) | A pool right on top of the edge isn't a real sweep target. |
| Pool maximum distance beyond edge (ticks) | 320 (80 pts) | Too far and a sweep there isn't really about THIS edge anymore. |
| Overnight/Asia/prior-day H/L enable | all true | Independent toggles — turn off any pool source you don't want considered. |

## QQQ → NQ (informational only — never gates the signal)

| Setting | Default | Why |
|---|---|---|
| QQQ→NQ: enable | true | Master switch — also requires `QqqSymbol` to actually be set. |
| Use live ratio (vs. prior RTH close) | false | Source script's default ("Prior RTH close") is a best-effort port here — real bar-timestamp alignment between two different exchanges isn't guaranteed. Switch to live ratio if the fixed-anchor version looks noisy in practice. |
| Log only if disagreement >= (ticks) | 12 (3 pts) | Avoids logging routine agreement. |

## Weekly overlay

| Setting | Default | Why |
|---|---|---|
| Weekly: enable | true | Turns on the weekly value-area computation at all. |
| Prior-week H/L counts as fuel | true | **This one is load-bearing**, unlike the rest of the weekly group — it's a real fourth fuel-pool candidate, not just informational logging. |

## Signal

| Setting | Default | Why |
|---|---|---|
| Minimum score to arm (1-3) | 2 | Matches the source script's own default. |
| Stop buffer beyond sweep extreme (ticks) | 16 (4 pts) | |
| Minimum stop distance (ticks) | 20 | Floors the stop if the raw calculation comes in tighter. |
| Minimum target distance (ticks) | 40 | Same idea, target side. |

## Risk management

| Setting | Default | Why |
|---|---|---|
| Max daily loss ($, 0=off) | 0 (off) | |
| Max drawdown ($, 0=off) | 2000 | A circuit breaker exists by default — same reasoning as `finchDomScalpStrategy`'s own recommended setting. |
| Max trades per session | 3 | Deliberately much lower than a scalp strategy's own default — this setup should fire rarely by design. |
| Cooldown between entries (bars) | 5 | |

## Diagnostics

| Setting | Default | What it does |
|---|---|---|
| Heartbeat: log interval (minutes, 0=off) | 15 | Periodic snapshot (prior-day/week levels, pool values, zone state) so you can confirm the strategy is alive even on a day nothing fires. Goes silent while a position is open. |
| *(always on)* | — | `[Signal skipped]` logs once a zone is actually swept and waiting on reclaim — not on every armed-but-unswept bar, which would be routine noise. |
