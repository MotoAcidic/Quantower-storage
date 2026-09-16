# CLAUDE.md

Ocean SMA — ATAS 50/100/200 SMA on the price panel with very small right-edge labels. Shipped and
deployed 2026-08-20. Appears as **Ocean → Ocean SMA**. See README.md. Shared build rules:
`~/dev/CLAUDE.md`.

## Layout

`OceansSmaIndicator.cs` render + settings · `SmaMath.cs` pure math · `_smoke/` · `_test/`.

## The render approach — and why it's the better default

The SMAs are plain `ValueDataSeries`, so ATAS draws, scales, legends and price-axis-tags them;
`OnRender` is used **only** for the labels. Contrast `oceans-market-view`, which hand-draws
everything because boxes and session shading have no series equivalent. For anything line-shaped,
copy this project, not that one.

## Two things worth reusing from here

1. **`RollingMean` in `SmaMath.cs`.** ATAS calls `OnCalculate` three ways: forward walk, the same
   bar repeatedly as ticks land, and a jump backwards after recalculate. The running sum is trusted
   **only** when the bar index advances by exactly one; anything else reseeds from source. Tested
   against a brute-force average in all three patterns.
2. **Warmup returns null, never a partial-window average.** An "SMA 200" computed off 40 bars is a
   different number wearing the same label.

## Traps

`[Range(4, 20)]` on a `float` property risks a validation type mismatch in the settings UI — the
text-size setting is an `int` for exactly that reason.

**`_smoke/` is the reusable part of this repo.** It constructs the indicator outside ATAS and
prints panel, series count and every visibility flag (`IsVisible`, `VisualType`, `ScaleIt`,
`Panel`). Copy it into any new ATAS project: a throwing constructor and an invisible series are
both undiagnosable inside the platform, and the ATAS log is silent about both.
