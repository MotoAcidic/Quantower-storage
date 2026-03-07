# Quantower Trading Strategies - Technical Documentation

## Overview
This document provides technical context and logic documentation for all trading strategies in this repository. Each strategy has been compiled successfully for the Quantower trading platform.

---

## Strategy Catalog

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

---

## Strategy Selection Guide

| Strategy | Best For | Market Conditions | Complexity |
|----------|----------|-------------------|------------|
| Box Range | Sideways markets | Low volatility | Medium |
| Price Surge | Trending markets | High volatility | Medium |
| Range Scalp | Quick profits | Ranging markets | Low |
| SMA Cross | Trend following | Trending markets | Low |
| Slope Change | Momentum shifts | Choppy trends | High |
| Weighted Surge | Refined momentum | Variable volatility | Medium |
| Gold ORB | Session breakouts | Gold futures | Medium |

This documentation should be updated whenever strategy logic or parameters are modified.