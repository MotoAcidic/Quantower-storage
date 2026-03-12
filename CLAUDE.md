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

### 1. Box Range Strategy (`boxRangeStrategy`)
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

### 2. Price Surge Strategy (`priceSurgeStrategy`) 
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

### 3. Range Scalp Strategy (`rangeScalpStrategy`)
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

### 4. SMA Cross Strategy (`smaCrossStrategy`)
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

### 5. Price Slope Change Strategy (`priceSlopeChangeStrategy`)
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

### 6. Weighted Surge Strategy (`weightedSurgeStrategy`)
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

### 7. Gold ORB Strategy (`goldOrbStrategy`) 
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