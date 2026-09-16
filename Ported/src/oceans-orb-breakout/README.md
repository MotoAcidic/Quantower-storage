# Ocean ORB Breakout

Marks the bars that leave the regular session's opening range with volume and the VWAP behind
them. Draws the opening range high as a horizontal line, labels the bar that qualifies, and can
raise an ATAS alert on it.

## The signal

All four have to be true on **one closed bar**, or nothing is marked:

1. **Close above the opening range high.** A wick through it does not count.
2. **Bar volume at least 1.5x the average volume of an opening range bar.** The average is the
   range's total volume over its bar count, on the chart's own timeframe.
3. **Close above the session VWAP.**
4. **Inside the signal window** — 8:30 to 10:30 Houston, the first two hours of the session.

## Times — Houston, and only Houston

Every setting, every calculation and every label in this indicator is Central time. There is no
second zone to convert from and nothing is entered in exchange time.

| | Houston (what you set) | The New York number it corresponds to |
|---|---|---|
| Session open | 08:30 | 9:30 AM |
| Opening range | 08:30–09:00 | 9:30–10:00 AM |
| Signal window ends | 10:30 | 11:30 AM |
| Session close | 15:00 | 4:00 PM |
| Overnight reopen | 17:00 | 6:00 PM |

Central and Eastern shift for daylight saving on the same dates, so these hold all year for CME
equity index products. The right-hand column is there once, for orientation; it appears nowhere
in the settings or on the chart.

## Timeframes

Works on any time-based chart whose bars divide the range. **1, 5 and 15 minute all do** — the
range comes out as 30, 6 and 2 bars.

On a chart whose bars do not divide it (7-minute, or tick/volume/range bars) the range high
would be read off bars straddling the boundary. The readout says so in capitals rather than
drawing a level that looks fine and is not. A 30-minute chart makes the range a single bar, so
the "average" is that one bar — the readout flags that too.

## Settings that matter

- **Volume multiple** (default 1.5) — condition 2.
- **Window ends at** (default 10:30) — condition 4.
- **Require close above VWAP** (default on) — switch off to drop condition 3.
- **First signal of the day only** (default on) — off marks every qualifying bar in the window.
- **Confirm on bar close** (default on) — the newest bar is still forming and its volume is only
  partial, so it is ignored. Turning this off makes marks and alerts appear and disappear inside
  the bar.
- **VWAP anchored at** — the 8:30 session open (default) or the 17:00 overnight reopen. On a
  gap day the two give opposite answers to condition 3, so pick deliberately.
- **Alert on the signal** (default off) — fires once per signal and only at the live edge.
  Loading or replaying history stays silent.

## What it refuses to do

- **No volume in the feed, no signal.** 1.5 times nothing is not a pass. The readout says the
  volume test cannot run.
- **No VWAP without volume.** The series stays empty rather than standing in the typical price,
  which would draw a plausible line that is not a VWAP.
- **Nothing before the range is settled.** A bar inside the opening range is still drawing it.
- **No range, no comparison.** A day whose opening range never printed — history starting
  mid-morning — is never measured against a high of zero.

## Build

    dotnet build OceansOrbBreakout.csproj -c Release
    cd _test && dotnet run -c Release
    ./deploy.ps1

`deploy.ps1` runs the harness first and refuses to deploy on failure. **Restart ATAS after
deploying** — it reads the indicator folder only at startup.
