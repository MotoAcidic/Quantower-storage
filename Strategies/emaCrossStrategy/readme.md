# EMA Cross Strategy — Timeframe Settings Reference
Trailing stop values are scaled to typical tick ranges per timeframe on MNQ/NQ.
Asia session = 6 PM – 3 AM EST (lower volume, tighter trail)
NY session   = 8 AM – 4 PM EST (higher volume, wider trail)
Off-Hours    = everything else (moderate settings)

---

## 1-Minute Settings
```
Micro EMA:                  5
Mid EMA:                    29
Period:                     1m
Stop Loss (ticks):          40
Take Profit (ticks):        0  (disabled)
Off-Hours Trail Activation: 30
Off-Hours Trailing Stop:    15
Exit Mode:                  2  (WeaknessBars + TrailingStop)
Impulse Filter (ticks):     20
HTF EMA Touch (ticks):      5
Weakness Close %:           50
Retrace Touch (ticks):      5
Retrace Bar Timeout:        3
Asia Session Start:         19  (7 PM EST)
Asia Session End:           3   (3 AM EST)
Asia Trail Activation:      20
Asia Trail Stop:            10
NY Session Start:           8   (8 AM EST)
NY Session End:             16  (4 PM EST)
NY Trail Activation:        50
NY Trailing Stop:           25
```

---

## 3-Minute Settings
```
Micro EMA:                  5
Mid EMA:                    29
Period:                     3m
Stop Loss (ticks):          60
Take Profit (ticks):        0  (disabled)
Off-Hours Trail Activation: 60
Off-Hours Trailing Stop:    30
Exit Mode:                  2  (WeaknessBars + TrailingStop)
Impulse Filter (ticks):     50
HTF EMA Touch (ticks):      5
Weakness Close %:           50
Retrace Touch (ticks):      5
Retrace Bar Timeout:        3
Asia Session Start:         19  (7 PM EST)
Asia Session End:           3   (3 AM EST)
Asia Trail Activation:      40
Asia Trail Stop:            20
NY Session Start:           8   (8 AM EST)
NY Session End:             16  (4 PM EST)
NY Trail Activation:        100
NY Trailing Stop:           50
```

---

## 5-Minute Settings
```
Micro EMA:                  5
Mid EMA:                    29
Period:                     5m
Stop Loss (ticks):          80
Take Profit (ticks):        0  (disabled)
Off-Hours Trail Activation: 80
Off-Hours Trailing Stop:    40
Exit Mode:                  2  (WeaknessBars + TrailingStop)
Impulse Filter (ticks):     50
HTF EMA Touch (ticks):      5
Weakness Close %:           50
Retrace Touch (ticks):      5
Retrace Bar Timeout:        3
Asia Session Start:         19  (7 PM EST)
Asia Session End:           3   (3 AM EST)
Asia Trail Activation:      60
Asia Trail Stop:            30
NY Session Start:           8   (8 AM EST)
NY Session End:             16  (4 PM EST)
NY Trail Activation:        140
NY Trailing Stop:           70
```

---

## 15-Minute Settings
```
Micro EMA:                  5
Mid EMA:                    29
Period:                     15m
Stop Loss (ticks):          150
Take Profit (ticks):        0  (disabled)
Off-Hours Trail Activation: 120
Off-Hours Trailing Stop:    60
Exit Mode:                  2  (WeaknessBars + TrailingStop)
Impulse Filter (ticks):     100
HTF EMA Touch (ticks):      10
Weakness Close %:           50
Retrace Touch (ticks):      10
Retrace Bar Timeout:        3
Asia Session Start:         19  (7 PM EST)
Asia Session End:           3   (3 AM EST)
Asia Trail Activation:      100
Asia Trail Stop:            50
NY Session Start:           8   (8 AM EST)
NY Session End:             16  (4 PM EST)
NY Trail Activation:        200
NY Trailing Stop:           100
```