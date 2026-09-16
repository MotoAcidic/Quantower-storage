# Phase Zero — Compatibility manifest

Produced by reflecting the **installed** assemblies, not the public Develop branch.
Probe source: `_probe/Program.cs` (kept in-tree, re-runnable).

Verified 2026-09-12 on this machine.

## Host

| Item | Verified value |
|---|---|
| ATAS product | ATAS Platform, `C:\Program Files (x86)\ATAS Platform` |
| ATAS build | **8.0.14.399** (ATAS.Indicators, ATAS.DataFeedsCore, ATAS.Types, OFT.Core all 8.0.14.399) |
| Host process | `OFT.Platform.exe` |
| OS | Windows 11 Pro 10.0.26200, x64 |
| .NET SDK | 10.0.100 |
| Runtimes present | Microsoft.NETCore.App 10.0.11, WindowsDesktop.App 10.0.11 |
| Target framework used | **`net10.0-windows`, `<UseWPF>true</UseWPF>`** |
| Indicator load directory | `%APPDATA%\ATAS\Indicators\` — read **only at ATAS startup**; restart required after deploy |

`ImageRuntimeVersion` on the ATAS assemblies reads `v4.0.30319` (a metadata artifact of the
obfuscator); the assemblies resolve only against .NET 10, and `net10.0-windows` is the
framework already proven by the other indicators on this machine.

## Referenced assemblies (all `<Private>false</Private>` — supplied by the host, never copied)

`ATAS.Indicators`, `ATAS.DataFeedsCore`, `ATAS.Types`, `OFT.Attributes`, `OFT.Core`,
`OFT.Localization`, `OFT.Rendering`, `Utils.Common`.

## Callback signatures — verified, and where the spec was wrong

BUILD-SPEC.md §4 lists symbols "to verify". Verified result:

| Spec §4 symbol | Actual member on this build | Status |
|---|---|---|
| `OnNewTrade(MarketDataArg)` | `protected virtual void OnNewTrade(MarketDataArg trade)` | confirmed |
| — | `protected virtual void OnNewTrades(IEnumerable<MarketDataArg>)` | **extra**, batch path — must not double-count |
| `OnBestBidAskChanged` | `protected virtual void OnBestBidAskChanged(MarketDataArg depth)` | confirmed |
| `MarketDepthChanged` | `protected virtual void MarketDepthChanged(MarketDataArg depth)` | confirmed |
| — | `protected virtual void MarketDepthsChanged(IEnumerable<MarketDataArg>)` | **extra** batch path |
| `GetMarketDepthSnapshot()` | `protected IEnumerable<MarketDataArg> GetMarketDepthSnapshot()` | confirmed |
| `SubscribeMarketByOrderData` | `protected Task SubscribeMarketByOrderData()` | confirmed — returns `Task` |
| `MarketByOrders` | **no such property** on the indicator | **spec wrong** |
| `OnMarketByOrdersChanged` | `protected virtual void OnMarketByOrdersChanged(IEnumerable<MarketByOrder>)` | confirmed |
| `OnCalculate(int, decimal)` | `protected virtual void OnCalculate(int bar, decimal value)` on `BaseIndicator` | confirmed |
| `OnRender` | `protected virtual void OnRender(RenderContext, DrawingLayouts)` | confirmed |
| `AddAlert` | `protected void AddAlert(string soundFile, string instrument, string message, Color bg, Color fg[, DateTime time])` | confirmed |
| `OnDispose` | `protected virtual void OnDispose()` on `BaseIndicator` (and `public override void Dispose()`) | **spec named it as a hook; it is on the base, not the documented surface** |

Additional verified surface used by this build:
`SubscribeToTimer(TimeSpan, Action)`, `RedrawChart(RedrawArg)`, `EnableCustomDrawing`,
`SubscribeToDrawingEvents(DrawingLayouts)`, `DataProvider.InstrumentInfo.TickSize`,
`ChartInfo.PriceChartContainer.GetYByPrice/GetXByBar`, `ChartInfo.DpiX/DpiY`,
`Container.Region`, `DoActionInGuiThread(Action)`.

## Data semantics — verified, and consequences for the build

**`MarketDataArg`** — `Price` (decimal), `Volume` (decimal), `Time` (DateTime), `Direction`
(`TradeDirection`), `DataType` (`MarketDataType`), `ExchangeOrderId` (`long?`),
`AggressorExchangeOrderId` (`long?`), `OriginPrice`, `OpenInterest`, `IsBid`, `IsAsk`.

- `TradeDirection`: `Between = 0`, `Buy = 1`, `Sell = 2`. **`Between` is mapped to
  direction 0 (unknown), never to a side** — §6 requires unknown volume to count toward
  total volume and side quality but never toward signed flow.
- `MarketDataType`: `Bid = 0`, `Ask = 1`, `Trade = 2`. Used for depth side **and** for
  `MarketByOrder.Side`; it is not an aggressor direction and is never substituted for one.
- There is **no receive timestamp on the event**. `Time` is a feed/exchange timestamp.
  Baseline feature time is therefore stamped by the adapter from a monotonic
  `Stopwatch` at the instant of callback entry (§5 "local receive elapsed time"),
  and `MarketDataArg.Time` is carried as diagnostic `ExchangeUtc` only.
- There is **no source sequence number**. `SourceSequence` is always null on this feed,
  so §5's "deduplicate only with a verified unique source identifier" means
  **no trade deduplication is performed at all** on this build.

**`MarketByOrder`** — `ExchangeOrderId` (`long`), `Price`, `Priority` (`long`), `Security`,
`Side` (**`MarketDataType`**, i.e. Bid/Ask), `Time`, `Type` (`MarketByOrderUpdateTypes`),
`Volume`. `MarketByOrderUpdateTypes`: `Snapshot = 0`, `New = 1`, `Change = 2`, `Delete = 3`.

## Capability matrix

| Capability | Status on this build | Effect |
|---|---|---|
| Individual trades | Available (`OnNewTrade`/`OnNewTrades`) | baseline enabled |
| Best bid/ask | Available (`OnBestBidAskChanged`) | baseline enabled |
| Price-level depth (MBP) | Available (`MarketDepthChanged` + snapshot) | OFI / imbalance enabled |
| MBP absolute-vs-delta semantics | **Unverified** — must be observed on live data | depth treated as absolute per price level; flagged in Health until observed |
| Order-level (MBO) | Interface present; feed is dxFeed prop which does carry MBO | **module OFF** |
| MBO snapshot/change atomic fence | **NOT established** — no documented fence in the API | §13: MBO stays `Unverified`, order-level evidence **disabled**. Per §21 Phase Zero, MBO attribution does not proceed. |
| Passive execution linkage | **NOT established** — cannot prove `AggressorExchangeOrderId` is the passive-side id without live verification | execution linkage flag false; §10 metrics unavailable |
| Source sequence numbers | Absent | no dedup; duplicate price/size/time trades stay distinct (correct per §5) |
| Frozen historical baseline | **Not supplied** | engine holds `SetupRequired`/`Warmup`; **no market alerts** |

## Blockers, stated plainly

1. **No baseline artifact exists.** Attacker-volume Q0.90 and plot scales are unavailable,
   so no candidate can arm against live data. This is the owner prerequisite from §1/§17
   and is not something the code can manufacture. Synthetic quantiles are never substituted.
2. **MBO evidence is off** and stays off until a snapshot fence and passive execution
   linkage are demonstrated on live data (§13, §21).
3. **Commissions, exchange fees and execution assumptions were not supplied**, so §20
   economic evaluation is not implemented beyond the pure `PnLnet` function.

Nothing above is worked around. Each is surfaced as an explicit unavailable state.
