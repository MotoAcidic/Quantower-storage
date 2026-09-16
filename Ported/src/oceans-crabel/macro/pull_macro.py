"""
Pull macro risk series to disk and describe where each one currently sits.

FRED series come through the fredapi package using FRED_API_KEY from .env.
The two volatility histories come straight from CBOE and need no key.

Everything lands as raw CSV in this folder so the data is on disk independent
of whatever reads it later.
"""

from __future__ import annotations

import io
import os
import sys
from pathlib import Path

import pandas as pd
import requests

HERE = Path(__file__).resolve().parent

# Where to look for FRED_API_KEY, in order. First hit wins.
ENV_CANDIDATES = [
    HERE / ".env",
    HERE.parent / ".env",
    Path.home() / ".env",
    Path.home() / "projects" / "zermind" / ".env",
]

FRED_SERIES = {
    "BAMLH0A0HYM2": "US high-yield credit spread (ICE BofA HY option-adjusted spread, %)",
    "BAMLC0A0CM":   "US investment-grade credit spread (ICE BofA IG option-adjusted spread, %)",
    "NFCI":         "Chicago Fed National Financial Conditions Index (0 = average; + = tighter)",
    "T10Y2Y":       "10-year minus 2-year Treasury spread (%; negative = inverted curve)",
    "DGS10":        "10-year Treasury constant-maturity yield (%)",
    "SOFR":         "Secured Overnight Financing Rate — overnight funding cost (%)",
    "VIXCLS":       "VIX daily close via FRED (%)",
}

CBOE = {
    "VIX": "https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv",
    "VXN": "https://cdn.cboe.com/api/global/us_indices/daily_prices/VXN_History.csv",
}

# Direction is judged over four weeks of trading days, not calendar days.
LOOKBACK_TRADING_DAYS = 20
PERCENTILE_YEARS = 2


def read_env_key(name: str = "FRED_API_KEY") -> str | None:
    """Return the key from the environment, or from the first .env that has it."""
    if os.environ.get(name):
        return os.environ[name].strip()

    for path in ENV_CANDIDATES:
        if not path.is_file():
            continue
        for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, _, v = line.partition("=")
            if k.strip() == name:
                return v.strip().strip('"').strip("'")
    return None


def fetch_fred(key: str) -> dict[str, pd.Series]:
    from fredapi import Fred

    fred = Fred(api_key=key)
    out: dict[str, pd.Series] = {}
    for code in FRED_SERIES:
        try:
            s = fred.get_series(code)          # full history; FRED decides the start
            s = s.dropna()
            s.index = pd.to_datetime(s.index)
            s.name = code
            out[code] = s
            s.to_frame("value").rename_axis("date").to_csv(HERE / f"{code}.csv")
            print(f"  {code:<14} {len(s):>6} rows  {s.index.min().date()} -> {s.index.max().date()}")
        except Exception as exc:                # one bad series must not sink the rest
            print(f"  {code:<14} FAILED: {exc}")
    return out


def load_local_fred() -> dict[str, pd.Series]:
    """
    Read the CSVs written by fetch_fred.mjs.

    Used when no API key is present. FRED's keyless chart-download endpoint
    serves five of these in full, but caps the two ICE BofA credit spreads at a
    rolling three-year window — they carry a "copyrighted: pre-approval
    required" licence, and no cosd value lifts it. Those two are flagged in the
    summary rather than quietly presented as full history.
    """
    out: dict[str, pd.Series] = {}
    for code in FRED_SERIES:
        path = HERE / f"{code}.csv"
        if not path.is_file():
            print(f"  {code:<14} MISSING — run fetch_fred.mjs first")
            continue
        df = pd.read_csv(path, parse_dates=["date"])
        s = pd.Series(pd.to_numeric(df["value"], errors="coerce").values,
                      index=df["date"], name=code).dropna()
        out[code] = s
        print(f"  {code:<14} {len(s):>6} rows  {s.index.min().date()} -> {s.index.max().date()}")
    return out


# Series whose keyless history is licence-capped rather than genuinely short.
LICENCE_CAPPED = {"BAMLH0A0HYM2", "BAMLC0A0CM"}


def fetch_cboe() -> dict[str, pd.Series]:
    out: dict[str, pd.Series] = {}
    for name, url in CBOE.items():
        try:
            r = requests.get(url, timeout=60, headers={"User-Agent": "Mozilla/5.0"})
            r.raise_for_status()
            df = pd.read_csv(io.StringIO(r.text))

            date_col = next(c for c in df.columns if "date" in c.lower())
            close_col = next(c for c in df.columns if "close" in c.lower())
            df[date_col] = pd.to_datetime(df[date_col], errors="coerce")
            df = df.dropna(subset=[date_col]).sort_values(date_col)

            df.to_csv(HERE / f"CBOE_{name}.csv", index=False)   # raw file, all columns
            s = pd.Series(pd.to_numeric(df[close_col], errors="coerce").values,
                          index=df[date_col], name=name).dropna()
            out[name] = s
            print(f"  CBOE {name:<9} {len(s):>6} rows  {s.index.min().date()} -> {s.index.max().date()}")
        except Exception as exc:
            print(f"  CBOE {name:<9} FAILED: {exc}")
    return out


def describe(code: str, label: str, s: pd.Series, unit: str = "") -> str:
    """Latest value, its place in two years of its own history, and 4-week drift."""
    if s.empty:
        return f"{code}: no data."

    last_date = s.index[-1]
    last = float(s.iloc[-1])

    window = s[s.index >= last_date - pd.DateOffset(years=PERCENTILE_YEARS)]
    pct = float((window < last).mean() * 100) if len(window) > 1 else float("nan")

    prior = s.iloc[-(LOOKBACK_TRADING_DAYS + 1)] if len(s) > LOOKBACK_TRADING_DAYS else s.iloc[0]
    change = last - float(prior)
    # Flat band scaled to the series' own variability, so "rising" means moved.
    noise = float(window.std()) if len(window) > 2 else 0.0
    if abs(change) < 0.05 * noise:
        drift = "flat"
    else:
        drift = "rising" if change > 0 else "falling"

    if pct != pct:
        where = "not enough history for a percentile"
    elif pct >= 80:
        where = f"high — {pct:.0f}th percentile of its last {PERCENTILE_YEARS} years"
    elif pct <= 20:
        where = f"low — {pct:.0f}th percentile of its last {PERCENTILE_YEARS} years"
    else:
        where = f"middling — {pct:.0f}th percentile of its last {PERCENTILE_YEARS} years"

    return (
        f"{code} — {label}\n"
        f"    Latest: {last:,.4g}{unit} on {last_date.date()}\n"
        f"    Versus itself: {where}\n"
        f"    Last 4 weeks: {drift} ({change:+.4g}{unit} over {LOOKBACK_TRADING_DAYS} trading days)\n"
        f"    Full history: {s.index.min().date()} to {s.index.max().date()} ({len(s):,} observations)"
    )


def main() -> int:
    print(f"Saving to {HERE}\n")

    key = read_env_key()
    fred_data: dict[str, pd.Series] = {}
    used_key = bool(key)

    if key:
        print("FRED (via fredapi, using FRED_API_KEY):")
        fred_data = fetch_fred(key)
    else:
        print("FRED (no API key found — reading CSVs from the keyless chart endpoint):")
        fred_data = load_local_fred()

    print("\nCBOE (no key needed):")
    cboe_data = fetch_cboe()

    print("\n" + "=" * 72)
    print("SUMMARY")
    print("=" * 72)

    for code, label in FRED_SERIES.items():
        if code in fred_data:
            print("\n" + describe(code, label, fred_data[code], "%" if code != "NFCI" else ""))
            if code in LICENCE_CAPPED and not used_key:
                print("    NOTE: keyless download is licence-capped to a rolling 3 years "
                      "(ICE BofA data, 'pre-approval required'). An API key should reach 1996.")

    for name, label in [
        ("VIX", "CBOE Volatility Index — 30-day implied vol on the S&P 500"),
        ("VXN", "CBOE Nasdaq-100 Volatility Index — 30-day implied vol on the NDX"),
    ]:
        if name in cboe_data:
            print("\n" + describe(name, label, cboe_data[name]))

    if not used_key:
        print("\nNOTE: FRED came from the public chart endpoint, not fredapi — no key was found.")
        print("Drop FRED_API_KEY into macro/.env and rerun to use fredapi and lift the 3-year cap.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
