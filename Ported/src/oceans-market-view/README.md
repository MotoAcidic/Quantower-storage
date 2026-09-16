# Ocean Market View

One ATAS indicator, four independently toggleable overlays. **Every time in it is Houston time** —
there is no exchange time zone to convert from, in the settings or on the chart.

| Module | What it draws |
|---|---|
| **Weekly open** | Line at the week's opening print, Sunday reopen to the right edge |
| **Power hour** | The 2–3 PM range as a box, its high/low carried forward, and the bar that breaks it |
| **Market sessions** | Asia / London / New York boxes, each labelled with its trading day |
| **Session opening range** | First 15 min of each session: high, low, and the middle line |
| **Initial balance** | IB high, low and middle in white, plus extensions every 0.5× out to 3.5×, each switchable |

Each module's **Show** toggle is the first entry in its settings group.

## Times (Houston)

| | |
|---|---|
| Week opens | Sunday 5:00 PM |
| Asia | 6:00 PM → 3:00 AM (labelled with the day it *ends* on) |
| London | 2:00 AM → 10:30 AM |
| New York | 8:30 AM → 3:00 PM |
| Regular session | 8:30 AM → 3:00 PM |
| Initial balance | 8:30 AM + 60 min (high, low and middle white; extensions green above, red below) |
| Power hour | 2:00 PM → 3:00 PM |
| Opening range | first 15 min of each session |
| Daily halt | 4:00 PM → 5:00 PM — CME's published MNQ maintenance window, used to verify the bar clock. Not the 3:00 PM cash close. |
| Week closes | Friday 4:00 PM |

All of these are settings; change any of them and the levels rebuild.

Every property holding a clock time carries a `Ct` suffix in code (`NewYorkStartCt`). When the
indicator moved from exchange time to Houston time these were all renamed, because ATAS restores
a saved value whenever the property name still matches — leaving the names alone would have kept
serving the old Eastern numbers under the new Houston labels on every chart and saved template.
If you ever change what a time setting *means* again, rename it.

## Build and install

```powershell
.\deploy.ps1
```

Runs the model tests, builds, and copies `OceansMarketView.dll` into
`%APPDATA%\ATAS\Indicators\`. **Restart ATAS** — the folder is only read at startup.

Then on an intraday MNQ chart: right-click → Indicators → *Ocean* → **Ocean Market View**.

## The one thing to check on first attach

ATAS has never been confirmed to stamp bars in UTC on this machine, and every level here is
anchored to a clock time — so a wrong offset would not look wrong. It would draw a clean,
plausible, silently misplaced line. So the indicator does not assume:

- **Bar clock = Auto** works it out from the data, two ways:
  1. **Matched to the clock** — on a live chart the newest bar is minutes old, and the two
     readings are five or six hours apart, so the comparison settles it outright.
  2. **Found the daily halt** — independent of the wall clock. MNQ shuts 3–4 PM Houston every
     weekday, so under the right reading that hour is empty on every single day while every
     other hour has bars. Under the wrong one the empty hour lands five or six hours off.
- If neither is decisive, **nothing is drawn** and the chart shows why. It never guesses.
- The status readout (top-left) prints the last bar's time in Houston time.
  **Check it against your own clock once.**
- `Bar clock` can be set by hand if you ever need to override it.

## Known limit

London and New York shift their clocks on different dates — for about three weeks in March and
one in late October, London's real open is an hour earlier in Houston terms than the rest of the
year. The London session setting is a fixed Houston time, so nudge it by an hour during those
weeks if it matters to you. Asia and New York are unaffected.

## Layout

| Path | |
|---|---|
| `OceanMarketView.cs` | Indicator: settings, rendering |
| `MarketModel.cs` | Sessions, opening ranges, weeks, IB and power hour; the IB level ladder |
| `TimeContext.cs` | Time zone and bar-clock resolution |
| `_test/` | Model tests on synthetic bars — `dotnet run` |
| `_reflect/` | Dumps the installed ATAS DLLs' real API — `dotnet run -- RenderContext IChart` |

`_test` and `_reflect` are standalone projects, excluded from the indicator's csproj glob;
leaving them in would fail the build on duplicate assembly attributes.

## Tests

```powershell
cd _test; dotnet run
```

The generator works in Houston wall time and knows the truth; the builder only ever sees UTC
stamps and has to recover it. Covers the clock resolution, the session windows (including the
overnight Asia leg and the November clocks change), the opening ranges, the power hour break,
and the IB level arithmetic including switching individual levels off.
