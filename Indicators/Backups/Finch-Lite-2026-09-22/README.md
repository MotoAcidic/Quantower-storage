# Finch-Lite backup — 2026-09-22

A full source snapshot of Finch-Lite (`../../Finch-Lite/`) taken at the operator's own request
("now lets make a backup of this its perfect") right after the third feature and its follow-up
fixes were confirmed working on a live chart. Restore point, not a fork — the live project keeps
being developed at `../../Finch-Lite/`; this folder is what "known good" looked like on this date.

**What's here**: `src/Finch.Lite.Indicator/` (source only — `bin`/`obj` build output excluded,
same as every other backup in this repo, since a clean `dotnet build` reproduces them) and
`FinchLiteIndicator.dll`, the exact binary deployed to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\` at the time of this backup
(`sha256sum` confirmed identical to both the freshly-built DLL and the deployed one before this
copy was made).

**State captured, for reference**: three features, all verified working —
1. Large resting bid/ask orders (persisting for the whole trading day, session boundary 18:00
   America/New_York), colour convention green=bid/bottom=support, red=ask/top=resistance.
2. A full-depth DOM ladder along the pane's right edge (a bar per scanned level, not just the
   large ones), lower-opacity backdrop under the large-order lines.
3. Big trades — a line at the price of any recent execution at or above the size threshold,
   green=buy/red=sell, independent colours from the resting-order ones above.

Zero `OrbIx.Core`/ORB-IX/Finch-Scalping dependency — Finch-Lite compiles in nothing beyond the
Quantower SDK itself, by design (see the project's own `.csproj` comment for why).

**To restore**: copy `src/Finch.Lite.Indicator/` back over the live project's own folder, or
just deploy `FinchLiteIndicator.dll` directly to
`C:\Quantower\Settings\Scripts\Indicators\Finch-Lite\` if only the binary is needed.
