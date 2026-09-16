# CLAUDE.md — Ocean's Order Blocks

Read `README.md` first. `~/dev/CLAUDE.md` has the rules shared by every `oceans-*` project;
they all apply. This file is only what is specific here.

## The line that must not move

`OrderBlocks*.cs` is free of ATAS types; `OceansOrderBlocks*.cs` is the adapter. `_test`
compiles only the first set: 176 checks, and 38 of 38 mutants caught at first build
(`mutate.py` pattern: sabotage one comparison, run, restore). The first run found one real bug:
same-bar mitigations entered the faded list newest-first, so the cap dropped the wrong block.

## Things that will bite

**Order inside `ObEngine.Fold` is load-bearing.** Flow → advance → CVD → detect. Detection is
last so a block born on a bar is first checked on the next one, which is where Pine checks it
too. `LiveFrom` (the breaker's last chart bar) guards the same thing inside `ZoneBook.Advance`.

**HTF buckets close by time, not by the next bar.** `HtfBuilder` closes a bucket when a chart
bar's end reaches `Cme.BucketEnd`, so a block appears on the breaker's last chart bar the same
way in history and live. That's the whole fix for the Pine `lookahead_off` lag. Key-change
closing is only the fallback, for tick/range charts and holiday early closes.

**The first bucket is partial and must stay excluded.** History starts wherever ATAS loaded it,
so the first 4H/D/W bucket has fake OHLC. `Partial` bars are never an OB candle, a breaker, or
an ATR sample.

**Zone state moves on closed bars only.** `LivePass` reads zones and never writes them. Folding
the live bar per tick would double-count footprint, and a wick that later closes back inside
would mitigate a block live that history would have kept.

**Alerts: seeded silently, then live prints only.** The first live pass after any recalc only
records which blocks the forming bar is already in. After that, an alert also needs
`candle.LastTime` within 5 minutes of the wall clock. Remove either gate and adding the
indicator sounds for every block the chart replays.

**Halt hour 16, not 15.** Same as every clock-aware indicator in the suite. Smoke asserts it.

## Not verified yet

Built and smoke-tested outside ATAS, not yet seen on a live chart. Unconfirmed: that
`GetAllPriceLevels` returns levels on the dxFeed MNQ chart (the panel says so if not), that
`candle.LastTime` is stamped the same way as `candle.Time` (the alert freshness gate assumes
it), and how the labels look at real zone density.
