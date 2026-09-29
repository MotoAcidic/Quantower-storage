# finchDomScalpStrategy — Settings Guide

What every setting does, the recommended value, and why. For the full chronological history of
every bug fix and design change, see `Quantower-storage/CLAUDE.md`'s Strategy Catalog entry — this
file is just the settings reference, kept current with the code's own defaults.

**No dry-run, no sim/eval gate.** This strategy places real orders the instant it's attached and
enabled. That was a deliberate, explicit choice made when it was first built — nothing in the code
checks what account it's running on. Attaching it to a sim/eval account is on you.

## How an entry actually fires

Every trade comes from **POC rejection** — a bar that touched a point-of-control and closed back
away from it, meaning the level held rather than got accepted through. There is no other entry
pathway anymore (the original DOM/absorption standalone trigger was removed 2026-09-29). For a
trade to fire, ALL of the following have to line up, checked in this order:

1. At least one of the three POC timeframes (current-move / 5m / 15m) is enabled.
2. Not already waiting on a just-placed order to confirm.
3. Daily-loss, drawdown, and trade-count limits haven't tripped.
4. Currently flat — one position at a time, by design.
5. Current time falls inside an allowed session (only checked if the session filter is on).
6. Enough bars have passed since the last entry *or* the last close (cooldown).
7. **Rejection pattern**: the bar's high/low touched or pierced the POC, but its close ended up
   beyond a buffer on the origin side.
8. **Genuine approach**: at least one recent prior bar was genuinely away from the POC first — a
   market already chopping on top of a flat POC doesn't count.
9. *(current-move POC only)* **Sufficient swing size**: the most recent confirmed swing leg spans
   enough distance to qualify — current-move POC is the most reactive of the three and needs this
   extra check to avoid anchoring off a barely-there wiggle.
10. **Local delta filter** — off by default (see below for why).
11. **Trend filter**: the last 60 bars of net buy/sell volume can't be running against the trade.
12. **Absorption confluence** *(if enabled)*: a real resting order has to sit near the POC, on the
    side that would actually defend the rejection direction.
13. **Risk:reward filter**: once the stop and target are computed, the reward has to be worth at
    least the configured percentage of the risk — checked LAST, right before the order goes out.

Only when all 13 pass does an order actually get placed. Each POC timeframe is tried in order
(current-move, then 5m, then 15m) and the first one that clears everything wins — only one trade
per bar close, never multiple.

---

## Instrument & polling

| Setting | Default | Notes |
|---|---|---|
| Period | 1 minute | Everything below is counted in BARS, not minutes — the trend filter's "60-bar" window means 60 minutes at this period. Changing the chart period would silently change the real-world meaning of every bar-count setting below; don't change it without re-thinking those too. |
| Quantity | 1 | Contracts per trade. |
| Poll interval (ms) | 250 | How often the DOM gets pulled. No reason to change this. |
| DOM: levels to scan per side | 500 | How deep into the book each poll reads. |

## Resting-order / absorption thresholds

These define what counts as a "real" resting order anywhere it's checked (originally the DOM
pathway's own anchor requirement; today, purely the input to absorption confluence — see below).

| Setting | Default | Why |
|---|---|---|
| Min level size (contracts) | 100 | Floor for a resting order to be tracked as a real level at all. |
| Absorption: strong tier (contracts) | 200 | How much size has to have traded through a level while it's still standing before it counts as genuinely defended, not just a large order nobody's tested yet. |
| Unfinished auction: price must move past by (ticks) | 8 | How far price has to move past a level before it's flagged "unfinished" (informational only now — no longer gates anything since the standalone DOM pathway was removed). |

## Stops & targets

| Setting | Default | Why |
|---|---|---|
| Stop buffer beyond swing/level (ticks) | 8 | Stops sit at the most recent confirmed swing low/high (long/short), offset by this many ticks — "a few ticks below the swing low in case it bounces off that point again," the operator's own request. |
| Minimum stop distance (ticks) | 20 | Floors the stop distance if the swing-based calculation comes in tighter than this — a single bar's range says nothing about real volatility. |
| Minimum target distance (ticks) | 10 | Same idea, for the target side. |
| Fallback target if nothing qualifies ahead (ticks) | 40 | Used only when no opposing level/IFVG/POC qualifies as a target. |
| Minimum reward:risk (%, 0=off) | **100** | **Do not disable.** Added after a live trade risked 84 points to make 15 (~1:5.5 against the trade) — a technically-valid-but-stale swing stop passed every other check. This is the last-resort economic sanity check, checked against the REAL numbers after everything else is decided. 100 = require at least 1:1. |

## Breakeven & trailing

| Setting | Default | Why |
|---|---|---|
| Breakeven: enable | true | Plain on/off checkbox. |
| Breakeven: trigger (% of trade's own risk) | 50 | Moves the stop to breakeven once a trade has earned back this % of its own risk. Scaled to each trade's own risk (not a fixed tick count) since swing-based stops vary a lot trade to trade — a fixed count either triggered too early on wide-stop trades or too late on tight ones. |
| Breakeven: buffer beyond entry (ticks) | **15** | **Don't set this below ~10.** A live trade's breakeven stop triggered at its exact level but filled 7 ticks worse due to normal stop-market slippage — a thin buffer (originally 3, briefly reverted to 6 on a live instance) turned a would-be small win into a real loss after fees. |
| Trailing stop after breakeven: enable | true | A SEPARATE, continuous mechanism that only activates after the one-time breakeven move above has already fired. |
| Trailing stop: buffer beyond prior candle (ticks) | 4 | Once trailing, the stop rides just beyond the previous closed bar's own low/high (long/short) — a level price has already respected, so an ordinary pullback retesting it doesn't stop the trade out; only a genuine break past it does. |

## Risk management

| Setting | Default | Why |
|---|---|---|
| Max daily loss ($, 0=off) | 0 (off) | Your call — no strong evidence pushing this either way yet. |
| Max drawdown ($, 0=off) | **2000** | Recommended ON. At one point this was manually disabled on a live instance with no cap of any kind — worth having *some* circuit breaker even if $2,000 isn't the exact right number for your size. |
| Max trades per session (0=off) | **10** | Same reasoning — a live instance ran with this off (unlimited) at the same time as the drawdown cap being off, which is a combination worth avoiding. |
| Cooldown between entries (bars) | **5** | A live instance had this at 1 bar, which let it re-fire almost instantly after a close — part of what let a bad repeated-fade pattern run up a real loss in one overnight session. 5 gives a beat before trying again. |

## Session filter

| Setting | Default | Why |
|---|---|---|
| Session filter: enable | false (24h) | Master switch. When off, every session toggle below is ignored. |
| Session - Asia/London/New York: enable + start/end hour (ET) | Asia 19:00–04:00, London 03:00–12:00, New York 08:00–17:00, all enabled | Standard order-flow session breakdown. One early review found trades taken during the Asia/London overnight window mostly lost, while NY-hours trades (including one +$104 winner) did much better — but that's one session's worth of evidence, not a settled conclusion. Worth testing session-by-session rather than assuming NY-only is definitely right. |

Windows can leave gaps: NY closing at 17:00 and Asia not opening until 19:00 means nothing is
allowed to trade in that 2-hour stretch if London stays off — check your configured hours don't
leave a gap you didn't intend, or embrace it if you did.

## POC (point of control)

| Setting | Default | Why |
|---|---|---|
| POC: enable current-move / 5m / 15m | all true | Three independent timeframes, each can anchor its own rejection trade. Current-move resets every time a new swing confirms (fastest, noisiest); 5m and 15m are fixed, slower, more deliberate. |
| POC: swing pivot lookback (bars) | 3 | How many bars on each side confirm a swing pivot — feeds both the current-move POC's own reset logic and the stop-placement swing tracker. |
| POC: 5m/15m history lookback (days) | 5 | How much backlog the 5m/15m POC engines prime from on attach. |
| POC: rejection close buffer (ticks) | 3 | How far beyond the POC a bar's close has to end up to count as a genuine rejection, not just noise. |
| POC rejection: approach lookback (bars) | 5 | How many prior bars are checked for a genuine approach. |
| POC rejection: minimum approach distance (ticks) | 10 | How far away those prior bars have to have been from the POC to count as a real approach. |
| POC current-move: minimum swing size to qualify (ticks, 0=off) | 20 | Current-move POC only — requires the swing that formed it to be a real move, not a tiny wiggle producing a target sized like any other. |

## Absorption confluence (the POC entry gate)

| Setting | Default | Why |
|---|---|---|
| POC: require absorption confluence | **true** | The core 2026-09-29 architecture change: a POC rejection now needs a real resting order backing it up, on the side that defends the rejection direction. This checkbox exists specifically so you can drop back to "POC-only" trading (no absorption requirement) without touching anything else if it proves too restrictive. |
| POC: required absorption confluence proximity (ticks) | **20** | How close that resting order has to be to the POC. Started at 10, confirmed too tight in a live trending session (zero trades fired all session; otherwise-qualifying, trend-aligned trades were dying here specifically) — widened to 20. |

## Delta & trend filters

Two independently-switched filters answering different questions — either, both, or neither can be
on.

| Setting | Default | Why |
|---|---|---|
| Delta filter: enable | **false** | **Recommended OFF.** This isn't a tuning call — it's a structural mismatch. A POC rejection is a REVERSAL bet, but this filter demanded the last 8 bars of order flow already agree with the NEW direction. At the exact moment of a genuine reversal, recent delta still reflects the OLD, about-to-reverse direction almost by definition — so this filter was blocking nearly every real setup it saw. |
| Delta filter: rolling window (bars) | 8 | Only matters if you turn the filter back on. |
| Trend filter: enable | true | Recommended ON. Asks a different, still-valid question — "is the whole SESSION running against this trade" — using a much longer window, so it doesn't have the same immediate-pre-reversal conflict as the local filter above. |
| Trend filter: rolling window (bars) | 60 | Deliberately much longer than the local delta window — meant to span roughly an hour at 1-minute bars, not a handful of minutes. |

## Observability

Pure logging — neither of these touches trading logic.

| Setting | Default | What it does |
|---|---|---|
| Heartbeat: log interval (minutes, 0=off) | 15 | Logs a snapshot (POC levels, delta readings, session state, minutes since last trade) on a fixed interval regardless of whether anything's happening — proof the poll loop is alive during a quiet stretch. Goes silent while a position is open. |
| *(always on, no switch)* | — | Once a genuine POC rejection is actually detected, a `[Signal skipped]` log line names the exact gate (swing size, delta, trend, absorption confluence) that blocked it and why — e.g. the actual rolling delta value, or that no absorption sat within the configured proximity. Only fires on a real near-miss, never as noise on an ordinary bar. |
