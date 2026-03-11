# Quantower Trading Strategies

A collection of automated trading strategies for the Quantower platform. Each strategy has been compiled and tested for compatibility with Quantower's trading engine.

## Table of Contents
- [EMA Cross Strategy](#ema-cross-strategy)
- [Box Range Strategy](#box-range-strategy)
- [Price Surge Strategy](#price-surge-strategy)
- [Range Scalp Strategy](#range-scalp-strategy)
- [SMA Cross Strategy](#sma-cross-strategy)
- [Price Slope Change Strategy](#price-slope-change-strategy)
- [Weighted Surge Strategy](#weighted-surge-strategy)
- [Gold ORB Strategy](#gold-orb-strategy)
- [Setup Notes](#setup-notes)
- [Compilation](#compilation)

---

## EMA Cross Strategy

**Description:** Trades EMA crossovers between a fast (Micro) and slow (Mid) Exponential Moving Average. Inspired by the "3 Fib EMAs XO" TradingView script. All entry and exit decisions are made on bar close only — no tick-chasing. Includes smart filters to avoid chasing impulse candles, session-aware trailing stops, partial profit-taking at weakness, and higher-timeframe EMA re-entry logic.

### Entry Logic

1. **EMA Cross** — When the Micro EMA crosses above the Mid EMA, a long is entered. When it crosses below, a short is entered.
2. **Impulse Filter** — If the cross candle's body is too large (>= `Impulse Filter` ticks), the entry is deferred. Chasing a big spike is avoided.
   - If `Retrace Touch > 0`: the strategy waits for price to pull back to the Mid EMA and bounce away before entering (better fill, avoids the high of the move).
   - If `Retrace Touch = 0`: entry is deferred exactly one bar and EMA alignment is re-checked.
3. **Liquidity Sweep** — If a deferred (impulse) entry flips to a reverse cross on the next bar, the strategy enters in the **new** direction — treating the original move as a stop-hunt that reversed.
4. **HTF EMA Touch Re-entry** — After a non-reverse exit (weakness bar, trailing stop, fixed SL/TP), the strategy watches the higher-timeframe Mid EMA. Once price touches it and bounces back in the original trend direction, a re-entry is placed. The higher timeframe is auto-selected: 1m→3m, 3m→5m, 5m→15m, 15m→1h, 1h→4h.

### Exit Logic (controlled by Exit Mode)

- **Weakness Bars exit** — After `Weakness Bars` consecutive bars where the EMAs are narrowing (gap shrinking), the position is closed. If `Weakness Close %` is between 1–99, only that percentage of the position is closed first (partial profit-take), and the remainder's stop loss is set to that partial TP price. The remainder is then closed if price returns to that level or when the next full weakness signal fires.
- **Trailing Stop exit** — Once profit reaches `Trail Activation` ticks, the strategy begins tracking the best price seen. If price pulls back more than `Trailing Stop` ticks from that peak, the position is closed.
- A **reverse cross** always closes the current position in full and immediately opens a new position in the opposite direction, regardless of Exit Mode.

### Parameters

#### Core

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Symbol** | — | The trading instrument to trade |
| **Account** | — | Account to execute trades on |
| **Micro EMA** | 5 | Period for the fast EMA. When this crosses the Mid EMA, a signal fires |
| **Mid EMA** | 29 | Period for the slow EMA. Acts as the trend baseline |
| **Weakness Bars** | 2 | Number of consecutive bars where the EMA gap must be shrinking before the strategy considers the trend weakening and exits (or partially exits) |
| **Period** | 1 min | Chart timeframe to run the strategy on |
| **Start Point** | 30 days ago | How far back to load historical data for EMA warmup |
| **Quantity** | 1 | Number of contracts per trade |

#### Risk Management

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Stop Loss (ticks)** | 100 | Hard safety-net stop loss in ticks from entry. This is a last-resort backstop — the strategy's own exit logic (weakness bars / trailing) will usually close the trade first |
| **Take Profit (ticks)** | 0 (off) | Fixed take-profit in ticks. Set to 0 to rely entirely on the Exit Mode logic instead |
| **Exit Mode** | 2 (Both) | Controls which exit method is active: `0` = weakness bars only, `1` = trailing stop only, `2` = both (whichever fires first closes the trade) |

#### Trailing Stop

The trailing stop has three tiers — **Asia**, **NY**, and **Off-Hours** — so you can tune each session independently. The strategy checks your current EST time and uses the matching set. If you're in the dead zone between sessions (e.g. 3 AM–8 AM EST), it falls back to Off-Hours values.

**Off-Hours** (used outside both Asia and NY windows):

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Off-Hours Trail Activation (ticks)** | 30 | Ticks of profit required before trailing activates outside both session windows |
| **Off-Hours Trailing Stop (ticks)** | 15 | Pullback in ticks from the best price before the trade closes, during off-hours |

#### Session-Aware Trailing Stop

Different sessions have very different volatility. Asia is typically quieter and tighter; NY is louder and needs more room. Each session can have its own trail settings. **All hours are Eastern Time (EST/EDT) — enter them exactly as you'd read them on a clock in New York. The code converts to UTC automatically and handles Daylight Saving Time.** Set both tick values for a session to `0` to disable that session's override (falls back to the defaults above).

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Asia Session Start (EST hour)** | 19 | EST hour when Asia session begins. 19 = 7:00 PM EST |
| **Asia Session End (EST hour)** | 3 | EST hour when Asia session ends. 3 = 3:00 AM EST. Because this is before the start hour (19), it automatically wraps midnight correctly |
| **Asia Trail Activation (ticks)** | 20 | Ticks of profit required before trailing activates during Asia hours. Lower than NY because ranges are smaller |
| **Asia Trailing Stop (ticks)** | 10 | Pullback in ticks from peak before exit during Asia hours. Tighter because moves are smaller |
| **NY Session Start (EST hour)** | 8 | EST hour when NY session begins. 8 = 8:00 AM EST |
| **NY Session End (EST hour)** | 16 | EST hour when NY session ends. 16 = 4:00 PM EST |
| **NY Trail Activation (ticks)** | 50 | Ticks of profit required before trailing activates during NY hours. Higher because NY moves are larger |
| **NY Trailing Stop (ticks)** | 25 | Pullback in ticks from peak before exit during NY hours. Wider to avoid being shaken out by normal NY volatility |

#### Entry Filters

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Impulse Filter (ticks)** | 20 | If the candle body on the cross bar is this many ticks or larger, the entry is NOT taken immediately. A big impulse bar usually means you'd be chasing — price has already moved a lot and is likely to retrace. Set to 0 to disable this filter and enter on every cross |
| **Retrace Touch (ticks)** | 5 | Only applies when an impulse cross is detected. Instead of waiting one bar and checking alignment, the strategy watches for price to pull back and come within this many ticks of the Mid EMA (29), then enters on the first bar that closes back away from the EMA in the original direction. This gives a much better entry price after a big push. Set to 0 to use the original 1-bar confirmation instead |
| **HTF EMA Touch (ticks)** | 5 | After a position closes via weakness bars, trailing stop, or fixed SL/TP (not a reverse cross), the strategy watches the Mid EMA on the next-higher timeframe. Once price comes within this many ticks of that EMA and then bounces back in the original trend direction, a re-entry is placed. Set to 0 to disable this feature. The HTF is automatically derived from your chosen Period |

#### Partial Exit

| Parameter | Default | Description |
|-----------|---------|-------------|
| **Weakness Close %** | 50 | When a weakness bar signal fires, this controls what fraction of the position is closed. **50** = close half the position to bank some profit, then move the remaining half's stop loss to the price where the partial was taken (so you can't lose on the remainder). If price comes back to that level, the rest closes automatically. **0 or 100** = close the entire position immediately, same as the original behaviour. This lets you keep part of the trade running if the trend resumes |

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