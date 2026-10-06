# mesOrbStrategy — Settings Guide

A port of the operator's own discretionary MES opening-range-breakout play — see
`Quantower-storage/CLAUDE.md`'s Strategy Catalog entry for the full build history and design
decisions. This file is the settings reference, kept current with the code's own defaults.

**No dry-run, no sim/eval gate.** Same explicit choice as `finchDomScalpStrategy` and
`oceansStackStrategy` — this strategy places real orders the instant it's attached and enabled.
Attaching it to a sim/eval account is on you. **This strategy has zero live track record** — the
operator traded this setup discretionarily for a while, but this specific automated version has
never run live. Watch the first several days closely against the chart before trusting it
unattended.

## How a trade fires

At most ONE trade per day (the reversal case below is the sole exception, and only replaces the
original play — it never stacks on top of it):

1. Mark the high/low of the `ORB window` (default 8:00–8:15 ET) on the 5-minute chart.
2. Wait for a 5-minute bar to **close** beyond either side of that range — this is the breakout,
   and it fixes the trade's direction for the rest of the day.
3. Wait for price to pull back into the **retest zone** — CHANGED 2026-10-06: the whole ORB box
   (orbLow to orbHigh), not just a tight band around the midpoint. `Retest zone tolerance` percent
   now buffers a little BEYOND the box's own edges rather than around its center — "a retest of
   the ORB" means anywhere in the original range, per a live miss where price retested well into
   the box (not near the midpoint specifically) with a clean rejection that never got evaluated.
4. Once price is in that zone, drop to the 1-minute chart and watch for a **rejection**: a 1-min bar
   that wicks into/through the zone but **closes back through the midpoint** in the original
   breakout direction, AND closes on the confirming side of the `EMA confluence` (see below). That
   candle close is the entry trigger (market order).
5. Stop = entry ∓ `Stop loss (points, fixed)` (always a fixed distance, never box-relative — see
   the note below). Target = entry ± `Profit target (points)`.

**One retest shot per breakout** (added 2026-10-06, the operator's own words: "since the retest
already played out it should not enter again if it comes back down and touches it again"). Once a
1-min bar's CLOSE fully leaves the retest zone without having confirmed a rejection, the attempt is
considered resolved and the strategy stops watching for the rest of the day — it will NOT re-arm on
a later, separate touch of the same box.

## EMA confluence (added 2026-10-06)

The operator, after a session-level short got stopped out into a rally despite a clean-looking
rejection: "there wasnt enough confluence to determine the actual short lets add in the closure
below the 9ema as a confluence." A running EMA (`EMA confluence: period`, default 9, computed on
1-minute closes) now gates EVERY rejection-style entry in both playbooks (ORB rejection,
session-level reject, session-level pullback-reject — NOT the ORB reversal-breakout play, which
already requires a full 5-min close beyond the whole box on its own): a long needs its trigger
candle to close ABOVE the EMA, a short needs it to close BELOW. Disable via `EMA confluence:
enabled` to go back to price-action-only confirmation.

**Reversal case** (the operator's own second-chance description): if, instead of holding, price
pushes all the way through the midpoint and a 5-minute bar **closes beyond the OPPOSITE side of the
box**, the original setup is fully invalidated — the strategy follows that reversal directly (same
fixed stop/target), with no second retest-and-rejection wait. Disable via `Allow reversal entry`.

### Why the stop is ALWAYS fixed, never "the other side of the box"

The operator's own description named both "stop on the other side of the box" and "~5pt stop loss"
— these conflict on a wide-range day (a 20-point box with a 5pt stop is a very different trade than
a 20-point box with a 20pt stop). Confirmed via `AskUserQuestion`, 2026-10-05: **always fixed**. The
box's width still matters — see `Min/Max ORB range` below — just not as the stop's own distance.

## Instrument & timing

| Setting | Default | Notes |
|---|---|---|
| Symbol | — | MES (Micro E-mini S&P 500). Nothing in the code hardcodes the contract — any symbol works, but the point-based stop/target sizing was tuned for MES's own point value. |
| Quantity | 1 | Single contract. |
| ORB window start/end (hour/minute, ET) | 8:00–8:15 | The range-marking window. |
| Stop watching for a NEW breakout after (hour/minute, ET) | 11:00 | Classic ORB discipline — if no breakout has fired by this time, the day is a scratch. Does NOT cut off an already-armed retest/rejection wait, only a brand-new breakout. |
| Poll interval (ms) | 500 | Lower frequency than the scalpers — this strategy only ever needs to notice a bar close, not sub-second DOM movement. |

## Stops, targets & the retest zone

| Setting | Default | Why |
|---|---|---|
| Stop loss (points, fixed) | 5.0 | The operator's own number. Always fixed — see above. |
| Profit target (points) | 15.0 | Operator's own range was 15–20; defaulted to the conservative end. 3:1 reward:risk at the default stop. |
| Retest zone tolerance (% of ORB range) | 15% | How close to the exact midpoint counts as "the zone" — scales with the day's own range instead of a fixed point count. |
| Min ORB range to trade (points) | 3.0 | Skip a degenerate, near-zero range day (0 disables). |
| Max ORB range to trade (points) | 40.0 | Skip a freak wide-range day where a fixed 5pt stop/15pt target no longer has any real relationship to the box (0 disables). |

## Session levels — Asia/London/NY untested highs & lows

A second, INDEPENDENT setup running in parallel with the ORB play — same stop/target inputs above,
different trigger. Marks each session's own high and low, frozen the moment that session ends.
Once frozen, a level stays "untested" and live to watch until a 5-min bar touches it — ANY touch
consumes it immediately, whether or not a trade follows (classic liquidity-sweep semantics: the
resting liquidity is spent the moment price trades through it). Each of the six levels (3 sessions
x high/low) only ever gets ONE look per cycle.

Once touched, the 1-minute chart decides which of two plays fires:

1. **Rejection** — a 1-min bar wicks into/through the level but closes back away from it → enter
   opposite the approach direction (fade).
2. **Break + pullback** — instead, a 1-min bar CLOSES clean through the level → now watch for "a
   little pullback": price recedes back toward the level, and a 1-min rejection candle off THAT
   pullback (wicks toward the level, closes back away from it in the breakout direction) confirms
   following the breakout instead.

Up to six of these can fire in a day (one per level touched) — entirely independent of the ORB
play's own one-trade-per-day cap. The only shared constraint across BOTH setups is the ordinary
"never more than one position open at once" guard.

| Setting | Default | Notes |
|---|---|---|
| Session levels: enabled | On | Master toggle for this whole second setup. |
| Asia session (start/end, ET) | 18:00–03:00 | Crosses midnight — handled correctly (same wraparound-safe window check as `oceansStackStrategy`). |
| London session (start/end, ET) | 03:00–11:00 | |
| NY session (start/end, ET) | 08:00–17:00 | Deliberately broader than the ORB's own 8:00–8:15 window — this is the session's FULL high/low, not the opening range. |
| Level touch tolerance (points) | 0.5 | How close counts as "touched." |
| Min break distance beyond level (points) | 1.0 | Requires a real close past the level before arming the pullback watch — filters out single-tick noise "breaks." |
| Pullback tolerance beyond level (points) | 3.0 | How close back to the broken level counts as "the pullback zone" for the continuation play. |

## Risk management — real account balance, restart-immune

Same pattern as `finchDomScalpStrategy`'s own THIRTY-SIXTH feature: both checks read the broker's
own live `Account.Balance`, not an internal counter that would reset on every restart, persisted in
a small local file (`mes_orb_daily_risk_state.txt`, next to the deployed DLL) so a mid-session
restart doesn't quietly forget the day's own starting balance or a tripped floor.

| Setting | Default | Notes |
|---|---|---|
| Max daily loss ($) | 0 (off) | Closes any open position and stops entering once today's real balance is down this much from where it started today. Resets automatically at the next ET day boundary. |
| Account balance floor ($) | 0 (off) | Once real balance drops to/below this, trading stops **permanently** until you raise it yourself — STICKY, survives restarts, does not auto-reset. |

## Known limitation — restart mid-day

On every `OnRun`, the ORB box itself (high/low/midpoint) is always correctly rebuilt from history
for today — that's a simple min/max over today's window bars, so it's safe regardless of when the
strategy (re)starts. What is **not** retroactively replayed is the breakout → retest → rejection
sequence: restarting after the window closes always resumes as "awaiting a fresh breakout" from
that moment forward, even if a breakout/retest/rejection already fully played out earlier today
before the restart. Given this strategy takes at most one trade a day, check the log after any
restart during active hours before assuming it missed something.

Also not retroactively replayed on restart, same reasoning as the ORB box: `ReconstructSessionLevels`
correctly rebuilds each level's Price/Untested status from history (safe, order-independent — a
touch anywhere in the backlog is still a touch), but never resumes a mid-reaction/pullback watch.

## Observability

Every phase transition and skip reason logs at `StrategyLoggingLevel.Trading`:
`[ORB] range building started...`, `[ORB] range captured...`, `[ORB] bullish/bearish breakout
confirmed...`, `[ORB] price is in the retest zone...`, `[ORB] 1-min rejection confirmed...`,
`[ORB] original ... setup fully invalidated...`, `[Session] ... session ended...`, `[Session] ...
touched...`, `[Session] ... rejection/pullback rejection confirmed...`, `[Session] ... broken...`,
`[Signal] ... entry placed...`, `[Order] protective stop=... target=...`. `[Risk] ...` lines cover
the account-balance floor/daily-loss breaches and the async order-refusal self-heal (see
`finchDomScalpStrategy`'s own THIRTY-EIGHTH fix for the incident that pattern closes).
