# CLAUDE.md

Ocean Crabel — Crabel ORB indicator, and the original reference implementation of the ATAS file
bridge. Shared build rules: `~/dev/CLAUDE.md`.

## Layout

`CrabelIndicator.cs` · `CrabelMath.cs` · `_reflect/` SDK prober · `_test/` math harness ·
`dash/` a separate Node/browser dashboard (`*.mjs`, `index.html`) · `macro/`.

**No `deploy.ps1` here** — unlike the other five `oceans-*` projects. Build and copy the DLL to
`%APPDATA%\ATAS\Indicators\` manually, then restart ATAS.

## What this project is good for

It is the working example of the **file bridge**: a custom C# indicator that computes something and
writes it to `%APPDATA%\ATAS\`, for other apps to read. That is the only reliable way to get data
out of ATAS.

**It draws nothing.** For any overlay work start from `~/dev/oceans-market-view` (hand-drawn) or
`~/dev/oceans-sma` (data series) instead.

**Nested tool projects must be excluded from the parent csproj glob** or the build fails on
duplicate assembly attributes. This is where that was first hit.

## dash/

Holds `secrets.local.json` with `lseApiKey` — **the LSE live-feed key lives here**, not in
`~/dev/market-apis/.env`. It predates that convention. Do not commit it.
