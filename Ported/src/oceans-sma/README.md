# Oceans SMA

The 50, 100 and 200 simple moving averages on the ATAS price panel, each tagged on the right
edge of the chart with a very small label.

Appears in ATAS under **Ocean → Oceans SMA**.

## What it draws

| Line | Default period | Default colour |
| --- | --- | --- |
| SMA 50 | 50 | blue |
| SMA 100 | 100 | orange |
| SMA 200 | 200 | red |

Every period, colour, width and line style is editable, and each line can be switched off on
its own. Changing a period renames its label and its legend entry to match, so a 20-period line
never keeps saying "SMA 50".

## Labels

Tiny text at the right edge of the chart, coloured to match its line and sitting at that
average's price level. Settings live under **04 Labels**:

- **Show labels** — off leaves just the lines.
- **Text size** — points, default **7**. 6 is about as small as stays readable; the range is 4–20.
- **Prefix with SMA** — on gives `SMA 50`, off gives just `50`.
- **Show value in label** — appends the current average, e.g. `SMA 50 21374.25`.

When two averages converge onto the same pixel the labels are pushed apart so all three stay
readable, keeping their order — the highest average holds the top slot.

The labels follow the **visible** right edge, so they stay put while panning rather than
scrolling off with the last bar.

## The averages themselves

Built from the candle, with **05 Display → Price** choosing which part of it: close (default),
open, high, low, median, typical or weighted.

This deliberately does **not** use ATAS's `SourceDataSeries`. That series is null until the
platform wires it up, and indexing it before then hands back `0` rather than failing — which
averages to 0, and 0 with `ShowZeroValue` off draws nothing at all. That is exactly the silent
wrong number that looks like a broken indicator; the candle is always read instead.

**A partial window is never averaged.** With 40 bars of history there is no "SMA 200" — the
line simply starts where a full 200-bar average first exists. An average of 40 bars printed
under a 200 label is a different number, and trading it as support costs real money.

## Status line

Because refusing to average a partial window is silent, **05 Display → Show status** prints a
line at the top left saying which average cannot draw and why:

- too little history — names the periods and the chart's actual bar count;
- enough bars but still no value — says the price came back empty and not to trade off it.

Leave it on. A blank chart that explains itself is the difference between a five-minute fix and
an afternoon of guessing.

## Building and installing

```powershell
.\deploy.ps1
```

Runs the tests, builds Release, and copies `OceansSma.dll` to `%APPDATA%\ATAS\Indicators\`.
**ATAS must be restarted** — it only reads that folder at startup.

## Tests

`_smoke\` constructs the indicator outside ATAS and prints what the platform would see: panel,
series count, and every visibility flag. A constructor that throws or a series that is silently
invisible is undiagnosable inside ATAS — the indicator simply never draws. Run it with
`dotnet run -c Release` from that folder whenever something does not appear.

`_test\` checks the rolling average against a brute-force reference that shares none of its
code, driven through the three patterns ATAS actually uses:

- a forward walk over history,
- the same bar hit repeatedly as ticks land on it (twenty repeats must not move the value),
- a jump backwards after a recalculate.

Plus warmup behaviour, period 1, and random access. The synthetic price path is a plain
deterministic random walk with gaps and flat stretches — it encodes nothing about how the
average is computed, so it can only confirm the arithmetic, never echo it.

## Notes

Same shape as `oceans-market-view`: reference the ATAS DLLs from
`C:\Program Files (x86)\ATAS Platform`, never copy them; keep the maths free of ATAS types so
it can be tested.

Unlike that indicator, this one draws its lines as ordinary ATAS data series and only hand-draws
the labels — so the platform handles scaling, the legend, and the value on the price axis.
