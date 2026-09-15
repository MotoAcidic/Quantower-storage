# Order-Flow Scalping Setup (ORB-IX configuration, not a new indicator)

Per the 2026-09-14 decision: rather than build a new indicator from scratch, this documents
how to get the **existing** `ORB-IX` indicator (`../ORB-IX/`) showing everything from the
friend's working screenshot - delta, resting orders/DOM, absorption levels, auto-drawn fib,
and higher/lower-timeframe POC. Every feature requested already exists in ORB-IX's source;
this is a configuration/diagnosis document, not a project.

---

## Why your chart looked different from your friend's

**Confirmed, not guessed** - the indicator's own source (`OrbIxIndicator.cs`, the
`VolumeAnalysisForceTickData` property) documents an exact reproduction from 2026-08-28:

> On a 1m MNQU6 chart with [force tick data] on, Quantower raised a warning toast reading
> verbatim: **"Volume analysis calculation from ticks history is not allowed for one data
> vendor."** ... Both routes to per-price levels are therefore closed on this connector: the
> vendor's precomputed path declares levels for "1 - Minute" and delivers `0 covered` on
> every scan, and the tick path is declined by the platform.

This is a **platform/data-vendor refusal that Quantower itself reports**, not a bug in ORB-IX
and not a setting you missed. It specifically blocks **historical backfill** of per-price
volume (what FRVP/AVP profiles and the footprint's bar-total seeding need to reach back
before the indicator was attached) - it's why the absorption shelf scan hit a bar
(`02:48:15`) with no matching delta data and refused to guess rather than draw something
wrong.

**It does not necessarily block live-forward data.** The cumulative delta counter on your
own chart (`Δ live · cum +50`) was already updating - that comes from the live tick/quote
stream as new prints arrive, not the historical volume-analysis API call that got refused.
Given enough time attached with fresh data flowing, footprint + delta bars built AFTER
attach should align correctly; only bars need to be treated as backfilled.

**Most likely explanation for your friend's fuller chart**: Quantower supports multiple
market-data connections for the same instrument (e.g. a paid Rithmic/CQG feed vs. a
TopStep-style ProjectX gateway). The toast is scoped to "one data vendor" - implying others
aren't refused this way. **Ask your friend which data connection/source he's using in
Quantower's connection manager** and compare it to yours; that's the real lever here, not
an indicator setting.

**Do not enable "Volume analysis: force tick data."** It defaults to `false` for a measured
reason: turning it on doesn't unlock anything on a refusing vendor, it actively breaks the
one thing that was still working (bar-total seeding for the delta panel) - the source
comment documents "6072 bars seeded" degrading to "none" when this was flipped on.

**What to try in the meantime**: remove and re-attach ORB-IX for a clean start, and give it
time to accumulate live bars before expecting the shelf scan / absorption to populate -
anything built entirely from live-forward data shouldn't hit the backfill refusal.

---

## Settings checklist to match the screenshot

All of these already exist as `InputParameter`s on ORB-IX - open the indicator's settings
panel in Quantower and check each:

| You asked for | Setting name in ORB-IX | Notes |
|---|---|---|
| Delta at the bottom | `Delta: enable panel` | Also `Delta: panel height (px)`, up/down colours |
| Resting orders / DOM | `Flow: DOM levels (largest resting size)` | Needs `Flow: draw the absorbed tools` on too (defaults **on**) - also needs a real Level 2/depth subscription on your connection, separate from the volume-analysis issue above (see the "Level 2 data" answer from earlier - DOM specifically needs full depth, not just tick+quote) |
| Absorption levels | `Absorption: draw on chart` **and** `Flow: absorption stack lines` / `Flow: absorption tier 1 (moderate)` / `Flow: absorption tier 2 (heavy)` | The dedicated `Absorption: *` settings drive the touch-bracket overlay (`AbsorptionOverlay.cs`); the `Flow: absorption *` settings drive the footprint-cluster absorption marks - turn on whichever matched what you saw, or both |
| Fib auto-draw | `Fib: enable` | Then `Fib: anchor` (pick the swing/anchor source), `Fib: show golden pocket`, `Fib: show extensions` - it draws itself from there, no manual drawing needed |
| Higher-timeframe POC | `FRVP: fixed-range profile` | Fixed-Range Volume Profile - anchor this to a longer/session-based range for the "higher timeframe" reference point of control |
| Lower-timeframe POC | `AVP: anchored profile` | Anchored Volume Profile - anchor this to something shorter (e.g. since a recent session open) for the "lower timeframe" POC. Run FRVP and AVP **together**, same as the screenshot showing both `FRVP POC` and `AVP POC` at once |

**One thing I could not confirm from source**: the colored bars running down the far right
edge of your friend's screenshot, right next to the price scale. That may be ORB-IX's DOM
levels display, or it may be **Quantower's own native chart feature** (right-click the
chart → chart settings → look for a depth-on-price-scale option) unrelated to ORB-IX
entirely. Worth checking both before assuming it's an indicator setting.

---

## Still true regardless of data quality

Per `AbsorptionShelfScan.cs`'s own documentation: absorption was statistically tested by the
author on MNQ (25,745 episodes) and **failed significance** (below the measured cost floor).
Getting the display working is about seeing the tape clearly, not about turning on a signal -
the source is explicit that none of this feeds a playbook or gates an entry.
