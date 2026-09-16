"""Ocean's Current - the section 9 calibration.

Answers the only question that decides whether this indicator ships:

    Does the composite 10:30 state beat "long if price is above the session VWAP
    at 10:30" by three points or more, over forty sessions or more?

Everything here reads the committed-bar logs the indicator writes and nothing
else. The outcome variable (where the cash session closed) and the benchmark
(which side of VWAP price sat on) are both already in those rows, so no
external session database is needed and none is assumed.

Absent is not zero, here as everywhere: a bar where a factor did not vote is
NaN and drops out of that factor's sample. It never enters as a neutral vote.
"""

import sys
from pathlib import Path

import numpy as np
import pandas as pd

LOGS = Path.home() / "AppData/Roaming/ATAS/OceansCurrent/logs"

RTH_OPEN = pd.Timedelta(hours=8, minutes=30)
RTH_CLOSE = pd.Timedelta(hours=15)
FIT_TIMES = [pd.Timedelta(hours=10), pd.Timedelta(hours=10, minutes=30)]

SUBS = ["sub1", "sub2", "sub3", "sub4", "sub5", "sub6"]
FACTOR_NAME = {
    "sub1": "F1 VWAP position",
    "sub2": "F2 CVD thrust",
    "sub3": "F3 value migration",
    "sub4": "F4 overnight inventory",
    "sub5": "F5 structure ladder",
    "sub6": "F6 one-timeframing",
}


def label_for(t):
    h = int(t.total_seconds() // 3600)
    m = int(t.total_seconds() % 3600 // 60)
    return "t%02d%02d" % (h, m)


def last_run(df):
    """Keep only the most recent pass over a day.

    Before the logger learned to take a file over, every chart reload appended a
    fresh replay of the whole history underneath the previous one - so a day
    file can hold several complete runs, and at different chart timeframes.

    They cannot be deduplicated on the timestamp: a 5-minute bar at 10:30 and a
    15-minute bar at 10:30 are different bars, not copies, and mixing them
    interleaves two state sequences into one that never happened (that is what
    read as sixty flips in a session). A run is a stretch of strictly rising bar
    index; the last one is what the newest chart load actually saw.
    """
    bar = df["bar"].to_numpy()
    starts = np.flatnonzero(np.r_[True, bar[1:] <= bar[:-1]])
    return df.iloc[starts[-1]:].copy()


def load(logs=LOGS):
    """Every committed row of the latest run over each day, as one frame."""
    files = sorted(logs.glob("obe_*.csv"))
    if not files:
        sys.exit("No logs in %s" % logs)

    frames, dropped = [], 0
    for f in files:
        # Blank means the factor abstained. Do not let the parser read it as 0.
        d = pd.read_csv(f, na_values=[""], keep_default_na=True)
        if d.empty:
            continue
        kept = last_run(d)
        dropped += len(d) - len(kept)
        frames.append(kept)

    df = pd.concat(frames, ignore_index=True)
    df["date"] = pd.to_datetime(df["date"]).dt.date
    df["tod"] = pd.to_timedelta(df["time"])
    df.attrs["dropped"] = dropped
    return df.sort_values(["date", "tod"], kind="stable").reset_index(drop=True)


def sessions(df):
    """One row per cash session: the readings at each fit time, and the outcome.

    A session that never reached the cash close is dropped rather than closed at
    whatever its last bar happened to be - a half session's last print is not a
    settlement, and scoring against it would invent an outcome.
    """
    out = []
    for date, day in df.groupby("date", sort=True):
        rth = day[(day.tod >= RTH_OPEN) & (day.tod <= RTH_CLOSE)]
        if rth.empty:
            continue

        # The cash close has to actually be on the chart for this session to
        # have an outcome at all.
        if rth.tod.max() < RTH_CLOSE - pd.Timedelta(minutes=30):
            continue
        rth_close = rth.iloc[-1].close

        row = {"date": date, "rth_close": rth_close, "bars": len(rth)}

        for t in FIT_TIMES:
            label = label_for(t)
            at = rth[rth.tod <= t]
            if at.empty:
                continue
            r = at.iloc[-1]
            # Only accept a reading close to the intended clock time; a chart on
            # a coarse timeframe must not silently supply an 09:00 reading here.
            if t - r.tod > pd.Timedelta(minutes=30):
                continue

            row["%s_close" % label] = r.close
            row["%s_state" % label] = r.state
            row["%s_score" % label] = r.score
            row["%s_conf" % label] = r.confidence
            row["%s_regime" % label] = r.regime
            for s in SUBS:
                row["%s_%s" % (label, s)] = r[s]

            # Outcome is measured from the reading, not from the open: the
            # question is what the state predicted from where it was called.
            row["%s_fwd" % label] = np.sign(rth_close - r.close)

        out.append(row)

    return pd.DataFrame(out)


def hits(pred, fwd):
    """Directional hit rate over the sessions where both sides actually committed.

    Returns (rate, n). A prediction of 0 is an abstention, not a wrong guess,
    and is excluded - as is a session that closed exactly flat.
    """
    pred = pd.Series(np.asarray(pred, dtype=float))
    fwd = pd.Series(np.asarray(fwd, dtype=float))
    m = (pred != 0) & pred.notna() & (fwd != 0) & fwd.notna()
    if m.sum() == 0:
        return float("nan"), 0
    return float((np.sign(pred[m]) == fwd[m]).mean()), int(m.sum())


def wilson(k, n, z=1.96):
    """95% interval for a hit rate. Wilson, because the normal approximation is
    useless at the sample sizes this protocol actually produces."""
    if n == 0:
        return float("nan"), float("nan")
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * ((p * (1 - p) / n + z * z / (4 * n * n)) ** 0.5) / d
    return c - h, c + h


def sessions_needed(edge_pp, base=0.5, power=0.8, alpha=0.05):
    """Roughly how many sessions it takes to see an edge of this size at all.

    Two-sided, one sample against a fixed rate. This is the number the section 9
    threshold of forty has to be read against, and it is the reason the kill
    criterion cannot be settled by running the tool for a month.
    """
    from math import sqrt
    za, zb = 1.959963985, 0.8416212336
    p1 = base + edge_pp / 100.0
    num = za * sqrt(base * (1 - base)) + zb * sqrt(p1 * (1 - p1))
    return int(round((num / (p1 - base)) ** 2))


def state_sign(s):
    return s.map({"LONG": 1.0, "SHORT": -1.0, "NEUTRAL": 0.0}).astype(float)


def flips(df):
    """Committed state changes per session - the behavioural metric."""
    per = []
    for date, day in df.groupby("date", sort=True):
        rth = day[(day.tod >= RTH_OPEN) & (day.tod <= RTH_CLOSE)]
        if rth.empty:
            continue
        s = rth.state.values
        per.append((date, int((s[1:] != s[:-1]).sum())))
    return pd.DataFrame(per, columns=["date", "flips"])


def report(s, label):
    """The kill criterion at one fit time."""
    need = ["%s_state" % label, "%s_fwd" % label, "%s_sub1" % label]
    if any(c not in s.columns for c in need):
        print("  no readings at %s" % label)
        return

    d = s.dropna(subset=["%s_fwd" % label]).reset_index(drop=True)
    obe = state_sign(d["%s_state" % label])
    fwd = d["%s_fwd" % label].astype(float)

    # The benchmark. sub1 is the VWAP z-score scaled, so its sign IS the side of
    # VWAP price sat on: the slope rule only halves it and the gamma fade never
    # applied (no regime in these logs). Where F1 abstained there is no VWAP
    # reading, so the benchmark abstains too rather than guessing a side.
    vwap = np.sign(d["%s_sub1" % label].astype(float))

    # Compare on the sample where BOTH have an opinion, or the comparison is
    # between two different sets of days.
    both = (obe != 0) & obe.notna() & (vwap != 0) & vwap.notna() & (fwd != 0)

    o_all, o_n = hits(obe, fwd)
    v_all, v_n = hits(vwap, fwd)
    o_c, c_n = hits(obe[both], fwd[both])
    v_c, _ = hits(vwap[both], fwd[both])

    print("\n  %s:%s CT" % (label[1:3], label[3:]))
    print("    sessions with an outcome        %d" % len(d))
    print("    OBE directional (not NEUTRAL)   %d  (%.0f%% coverage)"
          % (o_n, 100.0 * o_n / max(len(d), 1)))
    print("    OBE hit rate                    %.1f%%  n=%d" % (100 * o_all, o_n))
    print("    VWAP-side hit rate              %.1f%%  n=%d" % (100 * v_all, v_n))
    print("    -- head to head, same sessions --")
    print("    OBE                             %.1f%%  n=%d" % (100 * o_c, c_n))
    print("    VWAP side                       %.1f%%  n=%d" % (100 * v_c, c_n))
    edge = (o_c - v_c) * 100
    lo, hi = wilson(int(round(o_c * c_n)), c_n)
    print("    OBE 95%% interval                %.1f%% to %.1f%%" % (100 * lo, 100 * hi))
    print("    edge                            %+.1f pp   (spec wants >= +3.0 over >= 40)" % edge)

    # The spec's criterion compares the composite to the VWAP rule and stops there. Beating a
    # benchmark that is itself under a coin flip is not evidence of anything, so the coin is
    # checked too - and separately, whether the sample can carry the question at all.
    beats_coin = lo > 0.5
    if not beats_coin:
        note = "OBE is not distinguishable from a coin flip"
    elif edge >= 3.0:
        note = "clears the spec threshold"
    else:
        note = "below the spec threshold"

    need = sessions_needed(3.0)
    print("    sessions to resolve +3.0 pp     ~%d at 80%% power   (have %d)" % (need, c_n))
    print("    verdict                         UNDECIDABLE AT THIS SAMPLE - %s" % note)

    print("    -- each factor alone --")
    for sub in SUBS:
        col = "%s_%s" % (label, sub)
        if col not in d.columns:
            continue
        r, n = hits(np.sign(d[col].astype(float)), fwd)
        if n == 0:
            print("      %-24s never voted" % FACTOR_NAME[sub])
        else:
            lo, hi = wilson(int(round(r * n)), n)
            flag = "  <- clears the coin" if lo > 0.5 else ""
            print("      %-24s %.1f%%  n=%d  [%.0f-%.0f%%]%s"
                  % (FACTOR_NAME[sub], 100 * r, n, 100 * lo, 100 * hi, flag))


def main():
    df = load()
    print("Logs        %s" % LOGS)
    print("Rows        %d committed bars  (%d dropped as superseded replays)"
          % (len(df), df.attrs.get("dropped", 0)))
    print("Dates       %s to %s  (%d files)" % (df.date.min(), df.date.max(), df.date.nunique()))

    regimes = sorted(df.regime.dropna().unique())
    print("Regimes     %s   (0 = no GEX feed; no transforms applied)" % regimes)

    s = sessions(df)
    print("Sessions    %d with a cash close on the chart" % len(s))

    print("\n=== Kill criterion (section 9.3) ===")
    for t in FIT_TIMES:
        report(s, label_for(t))

    f = flips(df)
    print("\n=== Behavioural check (section 9.4) ===")
    print("  median committed flips/session  %.1f   (need <= 3)" % f.flips.median())
    print("  mean                            %.1f" % f.flips.mean())
    print("  worst session                   %d on %s"
          % (f.flips.max(), f.loc[f.flips.idxmax(), "date"]))
    print("  sessions over 3                 %d of %d" % ((f.flips > 3).sum(), len(f)))

    out = Path(__file__).parent / "sessions.csv"
    s.to_csv(out, index=False)
    print("\nPer-session table -> %s" % out)


if __name__ == "__main__":
    main()
