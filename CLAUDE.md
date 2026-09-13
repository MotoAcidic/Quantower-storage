# Quantower Trading Strategies - Technical Documentation

## Overview
This document provides technical context and logic documentation for all trading strategies in this repository. Each strategy has been compiled successfully for the Quantower trading platform.

---

## Strategy Catalog

### 0. EMA Cross Strategy (`emaCrossStrategy`)
**File:** `emaCrossStrategy/emaCrossStrategy/emaCrossStrategy.cs`
**Build output:** `C:\Quantower\Settings\Scripts\Strategies\emaCrossStrategy\emaCrossStrategy.dll`

#### Core Logic
- Enters on Micro (5) / Mid (29) EMA crossovers on any chosen timeframe
- All signals fire on **bar close only** — no tick-chasing on entries
- Exits via weakness bars (EMA gap shrinking), trailing stop, or reverse cross
- Reverse cross always closes current position and immediately opens the opposite direction
- Includes impulse candle filter, Mid EMA retracement entry, HTF EMA touch re-entry, partial close with automatic remainder SL, and session-aware trailing stops

#### Entry Flow (bar close)
1. **Impulse deferred bar check** (`pendingConfirmSide`) — if previous bar was an impulse cross, re-check EMA alignment; enter if still holds, or enter reversal if a clean opposite cross fired (liquidity sweep)
2. **Mid EMA retracement watch** (`retraceWatchSide`) — if an impulse cross set a retracement watch, monitor price coming within `RetraceTouchTicks` of the base Mid EMA, then enter on the first bar bouncing away in trend direction
3. **HTF EMA touch re-entry** (`lastExitSide` + `htfTouchArmed`) — after a non-reverse exit, watch for price to touch the auto-derived HTF Mid EMA and bounce back; auto-derived period: 1m→3m, 3m→5m, 5m→15m, 15m→1h, 1h→4h
4. **Fresh cross** — bullish or bearish EMA cross fires; if candle body ≥ `ImpulseFilterTicks`: set `retraceWatchSide` (if `RetraceTouchTicks > 0`) or `pendingConfirmSide` (1-bar fallback); else enter immediately

#### Exit Flow (bar close, position open)
- **Reverse cross** → close all, set `pendingEntrySide`, re-open opposite in `Core_PositionRemoved`
- **Weakness bars** (ExitMode 0 or 2) → `WeaknessBars` consecutive bars of shrinking EMA gap
  - If `WeaknessClosePercent` is 1–99: partial close that %, set `weaknessPartialPrice` as remainder SL
  - If 0 or 100: close entire position
- **Trailing stop** (ExitMode 1 or 2, tick-level in `Hdm_HistoryItemUpdated`)
  - `GetActiveTrailSettings()` picks Asia / NY / Off-Hours values by EST hour
  - Activates once P&L ≥ activation ticks; closes if pullback from `bestPrice` > trail ticks
- **Weakness partial SL** (tick-level) — if `weaknessPartialPrice > 0` and price returns to that level, close remainder

#### Key Private Fields
| Field | Purpose |
|---|---|
| `microEma` / `midEma` | Base-TF EMA indicators |
| `htfMidEma` / `htfHdm` / `htfPeriod` | Higher-TF Mid EMA for re-entry |
| `pendingEntrySide` | Queued reverse-cross flip (fires in PositionRemoved) |
| `pendingConfirmSide` | 1-bar impulse confirmation |
| `retraceWatchSide` / `retraceTouchArmed` | Mid EMA retracement entry state |
| `lastExitSide` / `htfTouchArmed` | HTF EMA re-entry state |
| `weaknessPartialDone` / `weaknessPartialPrice` | Partial close state and remainder SL |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state |

#### Parameters (InputParameter index order)
| # | Name | Default | Notes |
|---|---|---|---|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Micro EMA | 5 | Fast EMA period |
| 3 | Mid EMA | 29 | Slow EMA period |
| 4 | Weakness Bars | 2 | Consecutive narrowing bars to trigger exit |
| 5 | Period | MIN1 | Chart timeframe |
| 6 | Start Point | 30 days ago | Historical data start |
| 7 | Quantity | 1 | Contracts per trade |
| 8 | Stop Loss (ticks) | 100 | Hard backstop SL |
| 9 | Take Profit (ticks) | 0 | Fixed TP; 0 = disabled |
| 10 | Off-Hours Trail Activation | 30 | Ticks profit to start trailing outside Asia+NY |
| 11 | Off-Hours Trailing Stop | 15 | Ticks from peak before close outside Asia+NY |
| 12 | Exit Mode | 2 | 0=WeaknessBars, 1=TrailingStop, 2=Both |
| 13 | Impulse Filter (ticks) | 20 | Body size threshold to defer impulse entries |
| 14 | HTF EMA Touch (ticks) | 5 | Proximity to HTF Mid EMA to arm re-entry; 0=off |
| 15 | Weakness Close % | 50 | % to close on weakness bar; 0/100=close all |
| 16 | Asia Session Start (EST hour) | 19 | 7 PM EST |
| 17 | Asia Session End (EST hour) | 3 | 3 AM EST (wraps midnight) |
| 18 | Asia Trail Activation (ticks) | 20 | 0 = disable Asia override |
| 19 | Asia Trailing Stop (ticks) | 10 | 0 = disable Asia override |
| 20 | NY Session Start (EST hour) | 8 | 8 AM EST |
| 21 | NY Session End (EST hour) | 16 | 4 PM EST |
| 22 | NY Trail Activation (ticks) | 50 | 0 = disable NY override |
| 23 | NY Trailing Stop (ticks) | 25 | 0 = disable NY override |
| 24 | Retrace Touch (ticks) | 5 | Proximity to Mid EMA to arm post-impulse entry; 0=1-bar confirm |
| 25 | Micro EMA Pullback Touch (ticks) | 0 | Proximity to Micro EMA to arm re-entry on normal crosses; 0=off |

#### API / Platform Notes
- `[InputParameter]` only renders `int`, `double`, `bool`, `string`, `Period`, `Symbol`, `Account`, `DateTime` in Quantower UI — enums are invisible; use `int` with comment
- `GetValue(1)` = last closed bar (safe for signals); `GetValue(0)` = forming bar (tick use only)
- `TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")` used for DST-aware EST conversion
- `DeriveHtfPeriod()` uses both constant equality AND string fallbacks (`"3 Min"`, `"MIN3"`, `"3m"` etc.) because Quantower constructs non-standard periods differently depending on UI source

---

### 1. Futures Pro Strategy (`futuresProStrategy`)
**File:** `futuresProStrategy/futuresProStrategy/futuresProStrategy.cs`
**Build output:** `C:\Quantower\Settings\Scripts\Strategies\futuresProStrategy\futuresProStrategy.dll`
**Readme:** `futuresProStrategy/readme.md`

#### Core Logic
- Multi-factor trend-following for MES / ES / NQ on any chosen timeframe (default 5m)
- Entry requires a fresh 9/21 EMA cross on bar close — no mid-bar signals
- Five stacked filters must all pass: EMA cross + 200 EMA trend direction + RSI + MACD (optional) + RTH session gate (optional)
- Reverse cross closes current trade and re-enters opposite direction only if trend filter AND all entry filters agree; otherwise goes flat
- Optional mean reversion modes: **bounce** (arm near 200 EMA, enter on first cross back in trend direction) and **fade** (arm when overextended, enter counter-trend targeting 200 EMA as TP)
- Real-time trailing stop (tick level), daily loss cap (includes unrealized P&L), and total drawdown ceiling (prop firm safe)

#### Entry Flow (bar close)
1. Check daily/drawdown limits — halt if either hit
2. If in position: check for reverse cross; close and optionally queue flip
3. If flat: require `bullishCross` or `bearishCross` (one-bar strict crossover)
4. If `MeanRevMode` bounce armed: enter on matching cross (skips trend filter — price was just AT the trend EMA)
5. If `MeanRevMode` fade armed: enter counter-trend cross when overextended
6. Standard entries: run all 4 filters (trend, RSI, MACD, RTH) before placing

#### Exit Flow
- **Reverse cross** → close, queue flip, re-open in `Core_PositionRemoved` if all filters agree
- **Trailing stop** (tick-level in `Hdm_HistoryItemUpdated`) → activates at `TrailActivationTicks` profit; closes on pullback > `TrailingStopTicks` from peak
- **Hard SL / TP** → bracket order placed at entry
- **Daily loss / drawdown** → real-time check with unrealized P&L included; closes immediately when threshold hit

#### Key Private Fields
| Field | Purpose |
|---|---|
| `fastEma` / `slowEma` / `trendEma` | EMA indicators for cross and trend filter |
| `rsi` / `macd` | RSI and MACD indicators |
| `pendingEntrySide` | Queued reverse-cross flip (fires in `Core_PositionRemoved`) |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state |
| `meanRevBounceArmed` | Side direction armed for mean rev bounce entry |
| `dailyPnl` / `dailyLimitHit` / `lastResetDay` | Daily loss cap state (resets at 6 PM EST) |
| `totalRealizedPnl` / `peakEquity` / `drawdownLimitHit` | Max drawdown tracking |

#### Parameters (InputParameter index order)
| # | Name | Default | Notes |
|---|---|---|---|
| 0 | Symbol | — | Trading instrument |
| 1 | Account | — | Trading account |
| 2 | Fast EMA | 9 | Fast EMA period |
| 3 | Slow EMA | 21 | Slow EMA period |
| 4 | Trend EMA Period | 200 | Direction filter; entries blocked against it |
| 5 | RSI Period | 14 | RSI lookback |
| 6 | RSI Overbought | 70 | Blocks long if RSI ≥ value |
| 7 | RSI Oversold | 30 | Blocks short if RSI ≤ value |
| 8 | MACD Filter | 0 | 0=off, 1=histogram must agree |
| 9 | MACD Fast Period | 12 | — |
| 10 | MACD Slow Period | 26 | — |
| 11 | MACD Signal Period | 9 | — |
| 12 | Period | MIN5 | Chart timeframe |
| 13 | Start Point | -30 days | Historical data start |
| 14 | Quantity | 1 | Contracts per trade |
| 15 | Stop Loss (ticks) | 80 | Hard bracket SL |
| 16 | Take Profit (ticks) | 0 | 0=disabled; use trailing instead |
| 17 | Trail Activate At (ticks) | 40 | Profit to arm trailing; 0=off |
| 18 | Trailing Stop (ticks) | 20 | Pullback from peak to close; 0=off |
| 19 | RTH Only | 0 | 0=24h, 1=block entries outside RTH |
| 20 | RTH Start Hour (EST) | 9 | — |
| 21 | RTH End Hour (EST) | 16 | — |
| 22 | Max Daily Loss ($) | 0 | 0=disabled; resets at 6 PM EST |
| 23 | Max Drawdown ($) | 2000 | Total equity ceiling since strategy start; 0=off |
| 24 | Max Trend EMA Distance (ticks) | 0 | Blocks entries if price too far from 200 EMA; 0=off |
| 25 | MeanRev Mode | 0 | 0=off, 1=bounce, 2=fade, 3=both |
| 26 | MeanRev Bounce Arm (ticks) | 20 | Distance to 200 EMA to arm bounce |
| 27 | MeanRev Fade Arm (ticks) | 60 | Distance from 200 EMA to arm fade |

#### API / Platform Notes
- RTH gate uses `DateTimeUtcNow` (live clock), not bar timestamp — RTH filtering is inaccurate in backtest/replay mode
- `GetValue(1)` used for all signal reads (last closed bar); `GetValue(0)` only safe in tick handler
- `MaxDrawdown` never resets; restart strategy instance to clear it
- Reverse-cross flip is gated: position closes regardless, but re-entry only fires if trend + all filters agree for the new side

---

### 2. Box Range Strategy (`boxRangeStrategy`)
**File:** `boxRangeStrategy/boxRangeStrategy/boxRangeStrategy.cs`

#### Core Logic
- Identifies high and low price ranges over a lookback period
- Places limit/stop orders at range boundaries
- Uses configurable range offset for entry precision
- Supports both full range and half-range trading modes

#### Key Implementation Details
- **Range Calculation:** Uses arrays of historical highs/lows from bars 2-11
- **Entry Conditions:** Waits for price to be inside range before placing orders
- **Order Placement:** Bracket orders with calculated take profit and stop loss
- **Range Detection:** `insideRange` flag based on previous bar staying within boundaries

#### Parameters
- `rangeOffsetTicks`: Buffer distance from range boundaries
- `halfRange`: Enables trading at mid-range levels
- `stopOrders`: Toggle between limit and stop order types
- `updateCounter`: Warmup period before range calculation

---

### 3. Price Surge Strategy (`priceSurgeStrategy`) 
**File:** `priceSurgeStrategy/priceSurgeStrategy/priceSurgeStrategy.cs`

#### Core Logic
- Detects sudden price movements exceeding normal volatility
- Uses multiplicative threshold to identify surge conditions
- Implements trailing stop loss for profit protection
- Tracks previous trade direction to avoid conflicting signals

#### Key Implementation Details
- **Surge Detection:** Compares current move to lookback range average
- **Entry Timing:** Uses `newBar` flag to enter on bar close
- **Position Management:** Single position tracking with `inPosition` flag
- **Risk Management:** Dynamic trailing stop based on `trailingStop` parameter

#### Parameters
- `multiplicative`: Surge threshold multiplier (default 1.15 = 15% above average)
- `trailingStop`: Distance in ticks for trailing stop
- `lookbackRange`: Historical bars for surge calculation (1-10)

---

### 4. Range Scalp Strategy (`rangeScalpStrategy`)
**File:** `rangeScalpStrategy/rangeScalpStrategy/rangeScalpStrategy.cs`

#### Core Logic
- Quick scalping within identified price ranges
- Fixed take profit and stop loss for rapid trades
- Similar range detection to Box Range but optimized for scalping
- Uses smaller profit targets and tighter risk management

#### Key Implementation Details
- **Range Logic:** Inherited from Box Range Strategy
- **Scalping Focus:** Fixed 5 tick TP, 10 tick SL by default
- **Speed Optimization:** Simpler logic for faster execution
- **Risk Control:** Lower max profit/loss thresholds (350/250)

#### Parameters
- `takeProfit`: Fixed profit target in ticks
- `stopLoss`: Fixed stop loss in ticks
- `rangeOffsetTicks`: Entry adjustment from range boundaries

---

### 5. SMA Cross Strategy (`smaCrossStrategy`)
**File:** `smaCrossStrategy/smaCrossStrategy/smaCrossStrategy.cs`

#### Core Logic
- Classic moving average crossover system
- Fast SMA crossing above/below slow SMA generates signals
- Includes trailing stop loss and profit threshold features
- Position tracking prevents multiple entries in same direction

#### Key Implementation Details
- **Indicators:** Built-in SMA indicators for fast/slow periods
- **Signal Generation:** Crossover detection with previous bar comparison
- **Risk Management:** Combined fixed and trailing stop system
- **Entry Logic:** `prevSide` tracking prevents overtrading

#### Parameters
- `FastMA`: Fast moving average period (1-100, default 10)
- `SlowMA`: Slow moving average period (1-100, default 20)
- `stoploss`: Fixed stop loss in ticks
- `trailingStop`: Trailing stop distance
- `profitThreshold`: Profit level to activate trailing stop

---

### 6. Price Slope Change Strategy (`priceSlopeChangeStrategy`)
**File:** `smaSlopeChangeStrategy/priceSlopeChangeStrategy/priceSlopeChangeStrategy.cs`

#### Core Logic
- Monitors slope changes in moving averages
- Detects directional changes in price momentum
- Uses dual SMA system for lead/lag comparison
- Tracks slope change magnitude for entry signals

#### Key Implementation Details
- **Slope Calculation:** `baseSlopeChange` vs `baseSlopeChangePrev`
- **Signal Logic:** Slope direction reversals trigger entries
- **Counters:** `buyCounter`/`sellCounter` for signal strength
- **Cross Tracking:** `lastCross` prevents immediate reversals

#### Parameters
- `leadValue`: Lead SMA period for slope calculation
- `baseValue`: Base SMA period for slope reference
- **Note:** Both default to 20, creating sensitive slope detection

---

### 7. Weighted Surge Strategy (`weightedSurgeStrategy`)
**File:** `weightedSurgeStrategy/weightedSurgeStrategy/weightedSurgeStrategy.cs`

#### Core Logic
- Enhanced version of Price Surge Strategy
- Uses weighted calculations for more accurate surge detection
- Implements similar trailing stop and position management
- Improved sensitivity over basic surge detection

#### Key Implementation Details
- **Enhancement:** Weighted price calculations vs simple average
- **Base Logic:** Inherits core structure from Price Surge Strategy
- **Timing:** Same `newBar` based entry system
- **Risk:** Identical trailing stop mechanism

#### Parameters
- `multiplicative`: Weighted surge threshold (default 1.15)
- `trailingStop`: Trailing stop distance in ticks
- `lookbackRange`: Weighted calculation period (≤10)

---

### 10-12. Keltner Reversion / Trendline Break / S/R Channel Break Strategies — added 2026-09-12

**Files:** `keltnerReversionStrategy/`, `trendlineBreakStrategy/`, `srChannelBreakStrategy/`
(each its own project, `.cs`/`.csproj`/`.sln`/`readme.md`)
**Build outputs:** `C:\Quantower\Settings\Scripts\Strategies\{keltnerReversionStrategy,trendlineBreakStrategy,srChannelBreakStrategy}\*.dll`

Split out of `tvConfluenceStrategy` (below) per the user: rather than one strategy
voting across all three TradingView indicators, each gets its own independent strategy
so it can be tuned, backtested, and evaluated on its own, AND so all three can hold
independent positions on the same account+contract simultaneously rather than only one
ever being able to trade at a time.

#### Core Logic (each)
- **keltnerReversionStrategy** - Keltner Channel mean-reversion (`keltnerChannel.pine`)
- **trendlineBreakStrategy** - ATR-sloped trendline breakout (LuxAlgo `Trendlines.pine`)
- **srChannelBreakStrategy** - multi-touch S/R zone breakout (LonesomeTheBlue
  `supportResistanceChannels.pine`)
- All three: same detector math as `tvConfluenceStrategy`'s corresponding
  `EvaluateX` method, not re-derived; same risk-management skeleton (SL/TP/trailing/
  RTH/daily-loss/max-drawdown) as every other strategy in this repo, just single-
  detector instead of confluence-voting.

#### Multi-instance position isolation (the reason this split needed real code, not just copy-pasting files)
Quantower positions/orders belong to an account+symbol pair, not to a specific strategy
instance - `Core.Instance.Positions` returns every position on that account+symbol
regardless of which strategy (or a human) opened it. Running all three on the same
account+contract without a way to tell them apart would mean each one's "am I already
in a trade?" check sees the OTHERS' positions too - only one could ever hold a position
at a time. Fixed via a `StrategyTag` const per strategy (`"KeltnerReversion"`,
`"TrendlineBreak"`, `"SrChannelBreak"`), set as `PlaceOrderRequestParameters.Comment`
on every order placed, with every `Position`/`Order`/`Trade`/`OrderHistory` query
(including inside the `Core_PositionAdded`/`Core_PositionRemoved`/
`Core_OrdersHistoryAdded`/`Core_TradeAdded` event handlers, which fire globally for
ANY strategy's positions) filtered by `.Comment == StrategyTag`.

**⚠️ NOT verified against a live Quantower session** - confirmed via reflection that
`PlaceOrderRequestParameters`, `Position`, `Order`, `Trade`, and `OrderHistory` all
expose a `Comment` property, and all three strategies build clean, but there was no
running platform connection available to confirm `Comment` actually propagates from
the placing order through to the resulting `Position`/`Trade`/`OrderHistory` records.
**Before running these three together with real size on the same account, place one
small test trade per strategy in sim and check each Position's Comment field in
Quantower's Positions panel.** If it doesn't propagate, each strategy silently falls
back to seeing every position on the account again, defeating the whole point.

#### API / Platform Notes
- All three verified with a clean `dotnet build` (0 errors, 0 warnings) against
  v1.146.18/.NET 10, and confirmed `tvConfluenceStrategy` itself still builds
  unaffected by the split.
- `tvConfluenceStrategy` was left in place, unmodified - it's still there if the
  combined confluence-vote behavior is ever wanted again; these three are additive,
  not a replacement.

---

### 9. TV Confluence Strategy (`tvConfluenceStrategy`) — added 2026-09-12
**File:** `tvConfluenceStrategy/tvConfluenceStrategy/tvConfluenceStrategy.cs`
**Readme:** `tvConfluenceStrategy/readme.md`
**Build output:** `C:\Quantower\Settings\Scripts\Strategies\tvConfluenceStrategy\tvConfluenceStrategy.dll`

#### Core Logic
- Combines three TradingView indicators from `TradingView/` into one confluence-gated system: **Trendlines.pine** (LuxAlgo trendline break), **keltnerChannel.pine** (Keltner Channel mean-reversion), **supportResistanceChannels.pine** (LonesomeTheBlue S/R channel break)
- Each detector votes LONG/SHORT/nothing on bar close; entry fires only once `MinConfluencesRequired` of the *enabled* detectors agree on direction
- Math is a direct, verified port of DayTrader-Portable's `confluences.ts` (`trendline_break`/`keltner_reversion`/`sr_channel_break`) — same pivot definition, same ATR/EMA formulas, same edge-case handling, not a re-derivation from the raw .pine
- Bar-close only (no intrabar re-evaluation — pivots/zones are a "confirmed shape" question, not a tick-level one)
- Flat-only entries — unlike the EMA-cross strategies, does NOT flip on an opposite signal while in a position; managed purely by SL/TP/trailing/daily-loss/drawdown until flat, matching the DayTrader-Portable confluence system's own "independent one-shot signal" philosophy rather than a continuous cross state

#### Key Private Fields
| Field | Purpose |
|---|---|
| `barsNeeded` | Computed in `OnRun()` from whichever enabled detector needs the most history |
| `BuildBarArray()` | Builds an ascending `Bar[]` from `HistoricalDataExtensions.High/Low/Close` offsets, mirroring confluences.ts's `Bar[]` convention |
| `EvaluateTrendlineBreak` / `EvaluateKeltnerReversion` / `EvaluateSrChannelBreak` | The three ported detectors, each returning a `ConfluenceResult { Met, Direction, Strength, Detail }` |
| `trailingActivated` / `bestPrice` / `currentSide` | Trailing stop state (same pattern as `futuresProStrategy`) |
| `dailyPnl` / `dailyLimitHit` / `totalRealizedPnl` / `peakEquity` / `drawdownLimitHit` | Daily loss cap + max drawdown state (same pattern as `futuresProStrategy`) |

#### Parameters (InputParameter index order)
See `tvConfluenceStrategy/readme.md` for the full table (27 parameters: instrument/period/quantity, 3 params per detector + an on/off toggle each, min-confluences gate, SL/TP/trailing, RTH, daily loss, max drawdown).

#### What was simplified vs. the raw .pine scripts
- Trendlines.pine's `Stdev`/`Linreg` slope modes not ported (ATR mode only)
- keltnerChannel.pine's embedded WaveTrend oscillator and overbought/oversold circle markers not ported (Keltner Channel band only)
- supportResistanceChannels.pine's Close/Open pivot source option and per-touch strength weighting not ported
- Full detail and rationale in the readme.

#### API / Platform Notes
- Confirmed `HistoricalDataExtensions.Close/High/Low(hdm, offset)` still resolves as a static extension method against v1.146.18 — same signature as every other strategy in this repo
- Deliberately does NOT use `Core.Instance.Indicators.BuiltIn.*` for EMA/ATR — computes both directly from the bar array (see "Platform version notes" below for why)
- Verified with a clean `dotnet build` (0 errors, 0 warnings) against the currently-installed Quantower v1.146.18 / .NET 10

---

### 8. Gold ORB Strategy (`goldOrbStrategy`) 
**File:** `goldOrbStrategy/goldOrbStrategy/goldOrbStrategy.cs`

#### Core Logic
- Opening Range Breakout strategy for gold trading
- Captures 8:00 PM - 8:05 PM price range (configurable)
- Waits for breakout with optional confirmation candles
- Implements risk/reward ratio-based targets

#### Key Implementation Details
- **Session Detection:** Time-based ORB session tracking
- **Range Capture:** `orbHigh`/`orbLow` during session window
- **Breakout Logic:** Price break above/below range with confirmation
- **Strategy Modes:** "Aggressive" vs "Confirmed Breakout"
- **Daily Reset:** New range calculation each trading day

#### Parameters
- `orbSessionStartHour/Minute`: ORB session start (default 20:00)
- `orbSessionEndHour/Minute`: ORB session end (default 20:05)  
- `strategyMode`: Breakout confirmation mode
- `confirmationCandleMinutes`: Time to wait for confirmation
- `riskRewardRatio`: Target calculation multiplier

---

## Common Architecture Patterns

### Event Handling
All strategies implement these core event handlers:
- `Core_PositionAdded`: Tracks position counts and quantities
- `Core_PositionRemoved`: Resets flags and cancels pending orders
- `Core_TradeAdded`: Updates P&L tracking
- `Hdm_HistoryItemUpdated`: Real-time price updates
- `Hdm_OnNewHistoryItem`: New bar formation events

### Risk Management Framework
Standard risk controls across all strategies:
- `maxTrades`: Maximum number of trades per session
- `maxProfit`: Automatic stop at profit target
- `maxLoss`: Automatic stop at loss threshold
- Position quantity validation and tracking

### Order Management
Common order placement patterns:
- Bracket orders with SL/TP when supported
- Order status validation and error handling
- Pending order cancellation on position close
- `waitOpenPosition` flag for timing control

### Historical Data Access
Standard pattern for accessing market data:
```csharp
double close_1 = HistoricalDataExtensions.Close(this.hdm, 1);
double high_1 = HistoricalDataExtensions.High(this.hdm, 1);
double low_1 = HistoricalDataExtensions.Low(this.hdm, 1);
```

---

## Development Notes

### Quantower Platform Integration
- All strategies inherit from `Strategy, ICurrentAccount, ICurrentSymbol`
- Use Quantower's `InputParameter` attributes for UI configuration
- Implement `MonitoringConnectionsIds` for connection management
- Follow Quantower's order placement API patterns

### Code Organization
- Each strategy in separate solution/project structure
- Consistent naming: `{strategyName}Strategy` class and namespace
- Project files configured for Quantower deployment paths
- All strategies target .NET 8 with latest C# language version

### Testing Status
✅ All strategies compile successfully in Visual Studio
✅ All strategies use compatible Quantower API patterns  
✅ Parameter validation and error handling implemented
✅ Risk management controls in place
✅ `emaCrossStrategy` — actively developed; last build 0 errors 0 warnings (.NET 8, Release)
✅ `futuresProStrategy` — actively running on MESM6; filters confirmed via live log analysis
✅ `tvConfluenceStrategy` — new 2026-09-12, verified with a clean `dotnet build` (0 errors, 0 warnings), not yet run live/paper

### Platform Version Notes (2026-09-12)

The installed Quantower platform had moved on to **v1.146.18** while every
`.csproj` in this repo still pointed at **v1.145.16/17** (a version that no
longer exists on disk) and targeted **.NET 8**, while v1.146.18's
`TradingPlatform.BusinessLayer.dll` now requires **.NET 10**. Net effect:
`dotnet build` failed on every single strategy in this repo before today,
not just new ones — confirmed by actually running `dotnet build` (no Visual
Studio needed; the plain CLI is enough once the `.csproj` points at a real
install).

**Fixed across every active (non-`Backup`) project** — a path/TFM bump only,
no logic changes:
- `<HintPath>`/`<StartProgram>` bumped from `v1.145.16`/`v1.145.17` → `v1.146.18`
- `<TargetFramework>` bumped from `net8` → `net10.0`
- All 12 active strategies now build clean with these two changes alone.

**Also fixed:** `futuresProStrategy.cs` had two `CrossMinGapTicks` properties
declared with the same name (`InputParameter` indices 8 and 28) — a
duplicate-member compile error once the reference actually resolved. Removed
the second (dead, unused) declaration; kept index 8, which is the one every
other cross-debounce read in the file actually references.

**Found but deliberately NOT fixed — needs your input:**
`futuresProStrategy.cs` (the one strategy noted above as "actively running on
MESM6") calls `Core.Instance.Indicators.BuiltIn.VWAP()` and
`.ATR(int period)` — v1.146.18 removed the parameterless `VWAP()` entirely and
changed `ATR` to require a `MaMode` argument. This is a real behavior
decision (how to replace VWAP, or whether to), not a mechanical path fix, so
it was left alone rather than guessed at. **If this strategy is still live,
confirm whether it's running an already-built DLL from before the platform
update** (which may or may not still work at runtime against the new
platform) rather than assuming the fixed reference path alone makes it safe
to rebuild and redeploy as-is.

### Scripts Folder Cleanup + Output-Path Collisions (2026-09-12)

**`C:\Quantower\Settings\Scripts\ScriptsData\` had grown to 373MB** — 443
near-empty per-instance folders (one auto-created every time any strategy was
ever added to a chart, each holding just a day's `.slog` text file) plus a
`Backtest results\` folder holding the actual saved backtest performance
data. Deleted all 443 per-instance folders (with the user's confirmation) —
Quantower recreates them automatically as needed, nothing functional is
lost. Also removed 7 confirmed-empty (zero files) subfolders inside
`Backtest results\` for orphaned/renamed strategies. Left the live
`Futures Pro Strategy (...)` instance's folder alone since Quantower had its
log file open at the time (actively running).

**Left alone, flagged for the user**: `Indicators\EMACrossBackTestingStrategy`,
`Indicators\GridbotScalper`, and `Strategies\GridbotScalper` are compiled
DLLs with no matching source anywhere in this repo — deleting them would be
unrecoverable, so they were kept as-is rather than guessed at.

**Fixed a real output-path collision**: `futuresProStrategy-Backup\` and
`smaCrossStrategy-OG\` were both still configured (via stale `AssemblyName`/
`OutputPath`) to build into the *exact same* `Strategies\` output folder as
their live counterparts (`futuresProStrategy\` and `smaCrossStrategy\`) —
building either backup would have silently overwritten the active strategy's
deployed DLL. Gave each its own distinct assembly name and output folder
(`Strategies\futuresProStrategy-Backup\` and `Strategies\smaCrossStrategy-OG\`),
matching the pattern `emaCrossStrategy-Backup\` already used correctly. All
three (plus every active project) were also brought onto the same
v1.146.18/.NET 10 pin as everything else and verified with a clean
`dotnet build`.

**Current state**: every project in this repo (16 total, including backups)
builds into its own uniquely-named folder under
`C:\Quantower\Settings\Scripts\Strategies\<name>\` — this is Quantower's own
canonical location for `AlgoType=Strategy` projects (separate categories
exist for Indicators/PlaceOrderStrategies, which is why dropping a strategy
DLL loose in `Scripts\` root instead would actually stop it from showing up
in Quantower's Strategies list). The only project that still fails to build
is `futuresProStrategy` itself, for the unrelated VWAP/ATR reason documented
above.

---

## Strategy Selection Guide

| Strategy | Best For | Market Conditions | Complexity |
|----------|----------|-------------------|------------|
| **EMA Cross** | Trend + retracement entries | Trending / session breakouts | High |
| Box Range | Sideways markets | Low volatility | Medium |
| Price Surge | Trending markets | High volatility | Medium |
| Range Scalp | Quick profits | Ranging markets | Low |
| SMA Cross | Trend following | Trending markets | Low |
| Slope Change | Momentum shifts | Choppy trends | High |
| Weighted Surge | Refined momentum | Variable volatility | Medium |
| Gold ORB | Session breakouts | Gold futures | Medium |

This documentation should be updated whenever strategy logic or parameters are modified.