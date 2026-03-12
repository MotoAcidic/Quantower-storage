# emaCrossStrategy — Pending Features

## Deferred (commented out — need debugging before re-enabling)

### 1. Mid EMA Retracement Entry (`RetraceTouchTicks`)
- After an impulse cross (body ≥ ImpulseFilterTicks), instead of the 1-bar confirm,
  watch for price to pull back within `RetraceTouchTicks` of the base-TF 29 EMA and
  bounce back in trend direction before entering.
- Controlled by `retraceWatchSide` + `retraceTouchArmed` fields.
- Currently replaced with straight 1-bar confirm regardless of `RetraceTouchTicks` value.
- **Filed stub in code:** look for `// Retracement watch disabled` comment in `OnBarClose`.

### 2. Micro EMA Pullback Entry (`MicroRetraceTicks`)
- On normal (non-impulse) crosses, instead of entering immediately, watch tick-by-tick
  for price to come within `MicroRetraceTicks` of the live forming-bar 5 EMA and enter
  the instant it touches.
- Controlled by `microRetraceWatchSide` field, fires in `Hdm_HistoryItemUpdated`.
- Two bugs were found and fixed in this session (watch set to null instead of entrySide;
  PlaceEntry never called after touch) — but instability persisted, so the whole feature
  was commented out.
- **Filed stub in code:** look for `// Micro EMA pullback watch disabled` comment.

## Already Stable (in current build)

- HTF Mid EMA touch re-entry (original — HTF period only)
- Base-TF 29 EMA touch re-entry (added this session — fires on base TF bounce too)
- Session-aware trailing stop (Asia / NY / Off-Hours tiers)
- Weakness bar partial close + remainder SL
- Impulse candle filter with 1-bar confirm
- Trail move logging (logs every time stop level advances)

## Notes

- Platform: Quantower v1.145 on Topstep account (50KTC-V2 connection)
- Startup log lag is **normal** — Topstep throttles historical data feed; the strategy
  replays 30 days of bars on start and logs queue up until replay finishes. Not a bug.
- Suggested `StartPoint` workaround: reduce history to 5–7 days to speed up startup.
- Build output: `C:\Quantower\Settings\Scripts\Strategies\emaCrossStrategy\emaCrossStrategy.dll`
