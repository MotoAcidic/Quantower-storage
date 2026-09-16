# CLAUDE.md

Ocean ORB Breakout — ATAS indicator that **draws on the chart**: the regular session's opening
range high, the session VWAP, and a marker on bars that clear the range on volume inside the
first two hours. Started 2026-09-09. See README.md for behaviour. Shared build rules:
`~/dev/CLAUDE.md`.

## Layout

`OceansOrbBreakoutIndicator.cs` render + settings · `OrbModel.cs` logic · `TimeContext.cs` the
bar clock (copied from `oceans-market-view` — keep them in sync) · `_test/` math harness.

No `_reflect/` here. Use `oceans-market-view/_reflect` or `oceans-crabel/_reflect` to probe the
ATAS API; both dump any installed type and neither is worth duplicating.

## Where the money-losing bug would be

Every condition can fail *open* if it is written carelessly, and each failure draws a clean,
plausible, wrong mark:

- **Volume.** `volume >= 1.5 * average` is trivially true when `average` is 0, which is exactly
  what a feed with no volume gives. `OrAverageVolume <= 0` returns before the comparison.
- **VWAP.** With no volume there is no VWAP. Substituting the typical price would draw a line
  that looks like a VWAP and is not, so `Vwap` stays empty and condition 3 fails closed.
- **The range high.** A day whose opening range never printed has `OrHigh == 0`, and every bar
  closes above zero. `HasRange` gates the whole post-range branch.
- **The forming bar.** Its volume is partial, so it can pass the volume test on one tick and
  fail on the next. `ConfirmOnClose` skips the newest bar by default.

All four are asserted in both directions in `_test/`.

## The trading day rolls at 17:00, not midnight

`OrbModel.TradingDay` puts bars from 5 PM onward on the NEXT date. That is what makes the
overnight VWAP anchor line up, and it is why the session-open anchor also has to check
`t.Date == day.Day` — an evening bar is past 8:30 on the clock but belongs to the night before,
and would otherwise anchor the following day's VWAP to it.

## Chart timeframe is a correctness input, not a preference

The opening range is 30 minutes of wall time. On a 7-minute chart, or on tick/volume/range bars,
no bar opens on the boundary and the high gets read off bars that straddle it. `DayOrb.Aligned`
records this and the readout says so in capitals. Do not "fix" it by snapping to the nearest bar.

## MNQ hours — all Central, same as the rest of `~/dev`

Sunday **17:00** open · Friday **16:00** close · maintenance halt **16:00–17:00 every weekday** ·
RTH/cash **08:30–15:00**.

**The halt is NOT the 15:00 cash close.** The bar-clock resolver uses the empty hour to settle
whether stamps are UTC or local; with 15 it fails to resolve and the indicator draws *nothing*,
with no error pointing at the halt setting. This has shipped wrong three times elsewhere.
