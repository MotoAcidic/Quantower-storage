# Quantower Trading Strategies

A collection of automated trading strategies for the Quantower platform. Each strategy has been compiled and tested for compatibility with Quantower's trading engine.

## Table of Contents
- [Quantower Trading Strategies](#quantower-trading-strategies)
  - [Table of Contents](#table-of-contents)
  - [Box Range Strategy](#box-range-strategy)
    - [Parameters](#parameters)
  - [Price Surge Strategy](#price-surge-strategy)
    - [Parameters](#parameters-1)
  - [Range Scalp Strategy](#range-scalp-strategy)
    - [Parameters](#parameters-2)
  - [SMA Cross Strategy](#sma-cross-strategy)
    - [Parameters](#parameters-3)
  - [Price Slope Change Strategy](#price-slope-change-strategy)
    - [Parameters](#parameters-4)
  - [Weighted Surge Strategy](#weighted-surge-strategy)
    - [Parameters](#parameters-5)
  - [Gold ORB Strategy](#gold-orb-strategy)
    - [Parameters](#parameters-6)
  - [Common Parameters](#common-parameters)
    - [Universal Settings](#universal-settings)
    - [Risk Management](#risk-management)
    - [Strategy-Specific](#strategy-specific)
  - [Setup Notes](#setup-notes)
  - [Compilation](#compilation)

---

## Box Range Strategy

**Description:** A strategy that trades within defined price ranges by placing orders at range boundaries.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | -1 day | Historical data start point for range calculation |
| **Max Trades** | 20 | Maximum number of trades allowed before strategy stops |
| **Max Profit** | 1000 | Strategy stops when this profit level is reached |
| **Max Loss** | 500 | Strategy stops when this loss level is reached |
| **Range Offset (in ticks)** | 0 | Additional buffer above/below range boundaries in ticks |
| **Half Range** | false | If true, also trades at mid-range levels |
| **Updates Before Initialization** | 1000 | Number of price updates to wait before calculating range |
| **Stop Orders** | false | Use stop orders instead of market orders |

**How it works:** Identifies high and low price ranges and places buy orders near lows, sell orders near highs.

---

## Price Surge Strategy

**Description:** Detects sudden price movements and trades in the direction of the surge.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | -10 days | Historical data start point |
| **Multiplicative** | 1.15 | Threshold multiplier for detecting price surges (15% above average) |
| **Trailing Stoploss** | 20 | Distance in ticks for trailing stop loss |
| **Lookback Range (1-10)** | 10 | Number of bars to look back for surge calculation |

**How it works:** Monitors price movements and enters positions when price moves exceed the multiplicative threshold compared to recent price action.

---

## Range Scalp Strategy

**Description:** Scalping strategy that trades quick profits within identified price ranges.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | -1 day | Historical data start point |
| **Stop Loss** | 10 | Maximum loss per trade in ticks |
| **Take Profit** | 5 | Target profit per trade in ticks |
| **Max Trades** | 20 | Maximum number of trades allowed |
| **Max Profit** | 350 | Strategy stops when this profit level is reached |
| **Max Loss** | 250 | Strategy stops when this loss level is reached |
| **Range Offset (in ticks)** | 2 | Buffer distance from range boundaries in ticks |
| **Updates Before Initialization** | 1000 | Price updates to wait before range calculation |

**How it works:** Similar to Box Range but with smaller profit targets and stop losses for quick scalping trades.

---

## SMA Cross Strategy

**Description:** Classic moving average crossover strategy using two Simple Moving Averages.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | -100 days | Historical data start point for MA calculation |
| **Fast MA** | 10 | Period for fast moving average (1-100 bars) |
| **Slow MA** | 20 | Period for slow moving average (1-100 bars) |
| **Multiplicative** | 2.0 | Risk multiplier for position sizing |
| **Stoploss** | 100 | Fixed stop loss distance in ticks |
| **Trailing Stoploss** | 30 | Trailing stop loss distance in ticks |
| **Profit Threshold** | 40 | Profit level in ticks to activate trailing stop |

**How it works:** Buys when fast MA crosses above slow MA, sells when fast MA crosses below slow MA.

---

## Price Slope Change Strategy

**Description:** Trades based on changes in moving average slope direction.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | Current time | Historical data start point |
| **Lead SMA** | 20 | Period for leading simple moving average |
| **Base SMA** | 20 | Period for base simple moving average |
| **Max Trades** | 20 | Maximum number of trades allowed |
| **Max Profit** | 1000 | Strategy stops when this profit level is reached |
| **Max Loss** | 500 | Strategy stops when this loss level is reached |

**How it works:** Monitors slope changes in moving averages and trades when slope direction changes significantly.

---

## Weighted Surge Strategy

**Description:** Enhanced surge detection using weighted price calculations for more sensitive surge detection.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | -10 days | Historical data start point |
| **Multiplicative** | 1.15 | Threshold multiplier for detecting weighted surges (15% above average) |
| **Trailing Stoploss** | 20 | Distance in ticks for trailing stop loss |
| **Lookback Range (<= 10)** | 10 | Number of bars to look back (maximum 10 bars) |

**How it works:** Similar to Price Surge Strategy but uses weighted calculations for more sensitive and accurate surge detection.

---

## Gold ORB Strategy

**Description:** Opening Range Breakout strategy specifically designed for gold trading that captures price range during a specified session window (default 8:00 PM - 8:05 PM) and trades breakouts.

### Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | - | The trading instrument/symbol to trade |
| **Account** | - | Account to place orders on |
| **Quantity** | 1 | Number of contracts/shares per trade |
| **Period** | SECOND30 | Chart timeframe for analysis (30-second bars) |
| **Start Point** | -1 day | Historical data start point |
| **ORB Start Time - Hour (24hr)** | 20 | Hour when ORB session begins (20 = 8:00 PM EST) |
| **ORB Start Time - Minute** | 0 | Minute when ORB session begins (0 = :00 minutes) |
| **ORB End Time - Hour (24hr)** | 20 | Hour when ORB session ends (20 = 8:00 PM EST) |
| **ORB End Time - Minute** | 5 | Minute when ORB session ends (5 = :05 minutes = 5 minute window) |
| **Breakout Entry Mode** | ConfirmedBreakout | Dropdown: "AggressiveBreakout" or "ConfirmedBreakout" |
| **Confirmation Wait Time (minutes)** | 1 | Minutes to wait for breakout confirmation after initial break |
| **Risk:Reward Ratio** | 2.0 | Target profit as multiple of risk (2.0 = 2:1 ratio) |
| **ORB Buffer Distance (ticks)** | 0 | Additional safety buffer above/below ORB boundaries in ticks |
| **Max Daily Trades** | 10 | Maximum number of trades allowed per day |
| **Daily Profit Target** | 1000 | Strategy stops when this profit level is reached |
| **Daily Loss Limit** | 500 | Strategy stops when this loss level is reached |
| **Use Stop Orders (vs Market)** | true | Use stop orders for breakout entry vs immediate market orders |

**How it works:** 

1. **ORB Creation**: Captures the high/low during your specified time window (e.g., 8:00-8:05 PM EST)
2. **Breakout Detection**: Monitors for price breaking above ORB High or below ORB Low  
3. **Entry Modes**:
   - **Aggressive**: Enters immediately on breakout
   - **Confirmed**: Waits for confirmation time before entering
4. **Risk Management**: Stop loss placed at opposite ORB boundary + buffer, take profit calculated using risk:reward ratio

**Parameter Explanations:**
- **ORB Buffer Distance**: Adds extra ticks above/below ORB levels for safer entry/exit (0 = exact ORB levels)
- **Confirmation Wait**: Prevents false breakouts by waiting X minutes to confirm price stays broken out 
- **Risk:Reward**: If risk is $10, a 2:1 ratio means profit target is $20

---

## Common Parameters

### Universal Settings
- **Symbol**: Select your trading instrument from the available symbols in Quantower
- **Account**: Choose the trading account to execute trades
- **Quantity**: Number of contracts per trade (higher values increase risk/reward)
- **Period**: Chart timeframe (SECOND30 = 30-second bars, MINUTE1 = 1-minute bars, etc.)
- **Start Point**: How far back to load historical data for calculations

### Risk Management
- **Max Trades**: Prevents over-trading by limiting total number of trades
- **Max Profit/Loss**: Automatic strategy shutdown at profit/loss thresholds
- **Stop Loss/Take Profit**: Individual trade risk management
- **Trailing Stoploss**: Follows price to lock in profits

### Strategy-Specific
- **Multiplicative**: Sensitivity multiplier (higher = less sensitive to signals)
- **Lookback Range**: Number of historical bars used for calculations
- **Range Offset**: Additional buffer distance from trigger levels
- **Updates Before Initialization**: Warmup period before strategy starts trading

## Setup Notes

1. **Symbol Selection**: Ensure your selected symbol is actively traded and has sufficient liquidity
2. **Account Configuration**: Verify your account has sufficient margin for the quantity settings
3. **Period Settings**: Lower timeframes (SECOND30) provide more signals but may be noisier
4. **Risk Limits**: Always set appropriate Max Loss levels based on your risk tolerance
5. **Testing**: Consider paper trading before deploying with real capital

## Compilation

All strategies have been compiled successfully in Visual Studio and are ready for deployment in Quantower.