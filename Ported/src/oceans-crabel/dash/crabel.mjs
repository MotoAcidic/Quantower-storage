// Port of CrabelMath.cs. Kept deliberately parallel to the C# so the two can be
// diffed by eye. Both are exercised by the same test vector (crabel.test.mjs /
// _test/Program.cs) so they cannot silently drift apart.
//
// Every predicate returns null when history is too short. null means "cannot tell
// yet" and must never be rendered as "no".

export const STRETCH_LOOKBACK = 10;

/** Average, over the `lookback` sessions BEFORE `index`, of min(high-open, open-low). */
export function stretch(s, index, lookback = STRETCH_LOOKBACK) {
  if (!s || index < lookback || index > s.length) return null;

  let sum = 0;
  for (let i = index - lookback; i < index; i++) {
    const b = s[i];
    sum += Math.min(b.high - b.open, b.open - b.low);
  }
  return sum / lookback;
}

/** Range at `index` is the narrowest of the trailing `n` sessions, inclusive. */
export function isNarrowestRange(s, index, n) {
  if (!s || index < 0 || index >= s.length) return null;
  if (index - (n - 1) < 0) return null;

  const r = range(s[index]);
  for (let i = index - (n - 1); i < index; i++) {
    if (range(s[i]) <= r) return false;
  }
  return true;
}

export function isInsideDay(s, index) {
  if (!s || index < 1 || index >= s.length) return null;
  const c = s[index], p = s[index - 1];
  return c.high <= p.high && c.low >= p.low;
}

export function isTwoBarNarrowest(s, index, n = 20) {
  if (!s || index < 1 || index >= s.length) return null;
  if (index - n < 0) return null;

  const cur = twoBarRange(s, index);
  for (let i = index - n + 1; i < index; i++) {
    if (twoBarRange(s, i) <= cur) return false;
  }
  return true;
}

export function closeLocationValue(b) {
  if (!b) return null;
  const r = range(b);
  if (r <= 0) return null; // undefined, not 0.5
  return (b.close - b.low) / r;
}

export function rangePctOfAverage(s, index, n = 20) {
  if (!s || index < 0 || index >= s.length) return null;
  if (index - (n - 1) < 0) return null;

  let sum = 0;
  for (let i = index - (n - 1); i <= index; i++) sum += range(s[i]);

  const avg = sum / n;
  if (avg <= 0) return null;
  return (range(s[index]) / avg) * 100;
}

export function evaluate(s, index) {
  const nr4 = isNarrowestRange(s, index, 4);
  const inside = isInsideDay(s, index);

  return {
    nr4,
    nr7: isNarrowestRange(s, index, 7),
    nr20: isNarrowestRange(s, index, 20),
    insideDay: inside,
    // Only knowable when both components are knowable.
    idNr4: inside !== null && nr4 !== null ? inside && nr4 : null,
    twoBarNr20: isTwoBarNarrowest(s, index, 20),
    clv: closeLocationValue(s[index]),
    rangePctOf20d: rangePctOfAverage(s, index, 20),
  };
}

export const range = (b) => b.high - b.low;

// ------------------------------------------------------------------ base rates

/**
 * What actually happened the session AFTER each setup match.
 *
 * No lookahead: the Stretch applied to session i+1 is built from sessions
 * i-9..i, all of which close before i+1 opens. Only i+1's own OHLC is used to
 * score the outcome.
 *
 * `cohort(s, i)` selects signal sessions. Returns null when the sample is too
 * small to quote — a base rate off 3 observations is noise wearing a percentage
 * sign, and the UI must say "insufficient sample" rather than print it.
 */
export function baseRates(s, cohort, { minIndex = 20, minSample = 10 } = {}) {
  let buy = 0, sell = 0, both = 0, neither = 0, up = 0, expanded = 0;
  const obs = [];

  for (let i = minIndex; i < s.length - 1; i++) {
    if (cohort(s, i) !== true) continue;

    const st = stretch(s, i + 1);
    if (st == null) continue;

    const avg20 = averageRange(s, i, 20);
    if (avg20 == null) continue;

    const nxt = s[i + 1];
    const hitBuy = nxt.high >= nxt.open + st;
    const hitSell = nxt.low <= nxt.open - st;

    if (hitBuy && hitSell) both++;
    else if (hitBuy) buy++;
    else if (hitSell) sell++;
    else neither++;

    if (nxt.close > nxt.open) up++;
    if (range(nxt) > avg20) expanded++;

    // Keep every observation. When the sample is too small to quote a rate, the
    // individual outcomes are still worth showing -- "here are all four, judge
    // for yourself" beats hiding the setup entirely.
    obs.push({
      date: s[i].date,
      nextDate: nxt.date,
      setupRange: range(s[i]),
      nextRange: range(nxt),
      multiple: range(nxt) / range(s[i]),
      nextUp: nxt.close > nxt.open,
      nextChange: nxt.close - nxt.open,
    });
  }

  const n = obs.length;
  const ratios = obs.map((o) => o.multiple);

  // Distribution is reported even below minSample; only the RATES are withheld,
  // because a proportion off 4 observations is noise and a list of 4 is not.
  const dist = n
    ? {
        medianRangeMultiple: percentile(ratios, 0.5),
        p25RangeMultiple: percentile(ratios, 0.25),
        p75RangeMultiple: percentile(ratios, 0.75),
        avgRangeMultiple: ratios.reduce((a, b) => a + b, 0) / n,
      }
    : {};

  if (n < minSample) return { n, insufficient: true, minSample, observations: obs, ...dist };

  const upRate = up / n;

  return {
    n,
    insufficient: false,
    // Either side of the bracket was reached at some point in the session.
    triggerRate: (buy + sell + both) / n,
    buyOnly: buy / n,
    sellOnly: sell / n,
    bothSides: both / n,
    neither: neither / n,
    upCloseRate: upRate,
    // Standard error on the up-close proportion, so the UI can refuse to call a
    // 53/47 split "bullish" when it is inside the noise.
    upCloseStdErr: Math.sqrt((upRate * (1 - upRate)) / n),
    expansionRate: expanded / n,
    observations: obs,
    ...dist,
  };
}

/** Linear-interpolated percentile of a numeric array. p in 0..1. */
export function percentile(arr, p) {
  if (!arr || arr.length === 0) return null;
  const a = [...arr].sort((x, y) => x - y);
  if (a.length === 1) return a[0];
  const i = p * (a.length - 1);
  const lo = Math.floor(i), hi = Math.ceil(i);
  return lo === hi ? a[lo] : a[lo] + (a[hi] - a[lo]) * (i - lo);
}

/** Where a value sits within a historical array, as a 0..1 rank. */
export function percentileRank(arr, v) {
  if (!arr || arr.length === 0 || v == null) return null;
  let below = 0;
  for (const x of arr) if (x < v) below++;
  return below / arr.length;
}

/**
 * How the session opened relative to the prior session. Crabel's second axis:
 * the same contraction pattern has different expectancy depending on where the
 * open lands. Evaluated at the open, so it is knowable before the session runs.
 */
export function openingRelationship(s, i) {
  if (!s || i < 1 || i >= s.length) return null;
  const o = s[i].open, p = s[i - 1];

  if (o > p.high) return 'above prior high';
  if (o < p.low) return 'below prior low';
  if (o > p.close) return 'up, inside prior range';
  if (o < p.close) return 'down, inside prior range';
  return 'at prior close';
}

/**
 * Base rates conditioned on the opening relationship. Unlike `baseRates`, the
 * outcome window is the SAME session -- the open is the signal, so the day it
 * opens is the day being predicted.
 */
export function openingRates(s, { minIndex = 20, minSample = 15 } = {}) {
  const buckets = new Map();

  for (let i = minIndex; i < s.length; i++) {
    const cls = openingRelationship(s, i);
    if (!cls) continue;

    const avg20 = averageRange(s, i - 1, 20);
    if (avg20 == null) continue;

    if (!buckets.has(cls)) buckets.set(cls, { n: 0, up: 0, expanded: 0, ratios: [] });
    const b = buckets.get(cls);

    b.n++;
    if (s[i].close > s[i].open) b.up++;
    if (range(s[i]) > avg20) b.expanded++;
    b.ratios.push(range(s[i]) / avg20);
  }

  // Pooled expansion rate across every bucket: the yardstick each class is
  // measured against, so "73% expand" is reported relative to what a session
  // does anyway rather than in a vacuum.
  let totalN = 0, totalExpanded = 0, totalUp = 0;
  for (const b of buckets.values()) { totalN += b.n; totalExpanded += b.expanded; totalUp += b.up; }
  const pooled = totalN ? totalExpanded / totalN : null;
  // Direction is judged against the market's own drift, not against 50%. Equities
  // close up more often than not, so a coin-flip yardstick would eventually label
  // every cohort bullish for simply existing in a rising market.
  const pooledUp = totalN ? totalUp / totalN : null;

  return [...buckets.entries()]
    .map(([label, b]) => {
      const expansionRate = b.expanded / b.n;
      // Normal approximation to the binomial, against the pooled rate.
      const se = pooled == null ? null : Math.sqrt((pooled * (1 - pooled)) / b.n);
      const z = se ? (expansionRate - pooled) / se : null;

      const upRate = b.up / b.n;
      const upSe = pooledUp == null ? null : Math.sqrt((pooledUp * (1 - pooledUp)) / b.n);
      const upZ = upSe ? (upRate - pooledUp) / upSe : null;

      return {
        label,
        n: b.n,
        insufficient: b.n < minSample,
        upCloseRate: upRate,
        upCloseStdErr: Math.sqrt((upRate * (1 - upRate)) / b.n),
        upCloseZ: upZ,
        upCloseSignificant: upZ != null && Math.abs(upZ) >= 2,
        expansionRate,
        expansionZ: z,
        expansionSignificant: z != null && Math.abs(z) >= 2,
        medianRangeVs20d: percentile(b.ratios, 0.5),
      };
    })
    .sort((a, b) => b.n - a.n)
    .map((x) => ({ ...x, pooledExpansionRate: pooled, pooledUpCloseRate: pooledUp }));
}

export function averageRange(s, index, n = 20) {
  if (!s || index < 0 || index >= s.length || index - (n - 1) < 0) return null;
  let sum = 0;
  for (let i = index - (n - 1); i <= index; i++) sum += range(s[i]);
  return sum / n;
}

export const COHORTS = {
  all: () => true,
  nr7: (s, i) => isNarrowestRange(s, i, 7) === true,
  nr4: (s, i) => isNarrowestRange(s, i, 4) === true,
  idnr4: (s, i) => evaluate(s, i).idNr4 === true,
  twoBarNr20: (s, i) => isTwoBarNarrowest(s, i, 20) === true,
  nr7Streak3: (s, i) => {
    for (let k = 0; k < 3; k++) {
      if (isNarrowestRange(s, i - k, 7) !== true) return false;
    }
    return true;
  },
  // Compression clusters, so a pooled NR7 sample mixes isolated days with
  // consecutive ones. They are not the same setup: on this instrument the
  // consecutive case beats the 20-session average far less often, which is
  // exactly the claim the pooled figure was overstating.
  nr7Isolated: (s, i) =>
    isNarrowestRange(s, i, 7) === true && isNarrowestRange(s, i - 1, 7) !== true,
  nr7Consecutive: (s, i) =>
    isNarrowestRange(s, i, 7) === true && isNarrowestRange(s, i - 1, 7) === true,
};

// -------------------------------------------------------------- expected move

/**
 * Options-implied one-session expected move from an annualised volatility index.
 *
 * EM = spot x (iv/100) / sqrt(tradingDays). This is the ~1 standard deviation
 * move, so roughly a 68% chance of finishing inside it -- NOT a range the market
 * cannot leave.
 *
 * The index (VXN) measures Nasdaq-100 INDEX vol, while you trade the future.
 * They track closely but are not identical instruments; the UI says so.
 */
export function expectedMove(spot, iv, tradingDays = 252) {
  if (spot == null || iv == null || !(iv > 0) || !(spot > 0)) return null;
  const pct = iv / 100 / Math.sqrt(tradingDays);
  return { points: spot * pct, pct: pct * 100, iv, spot };
}


function twoBarRange(s, i) {
  return Math.max(s[i].high, s[i - 1].high) - Math.min(s[i].low, s[i - 1].low);
}

/**
 * The headline read: how deep is the current compression, and what is it made of.
 * Returns a short verdict plus the streak of consecutive NR7 sessions.
 */
export function compressionRead(s) {
  if (!s || s.length === 0) return { verdict: 'no data', nr7Streak: 0 };

  const last = s.length - 1;
  const f = evaluate(s, last);

  let nr7Streak = 0;
  for (let i = last; i >= 0; i--) {
    if (isNarrowestRange(s, i, 7) === true) nr7Streak++;
    else break;
  }

  let verdict;
  if (f.idNr4 === true) verdict = 'ID/NR4 — highest-grade Crabel setup';
  else if (nr7Streak >= 3) verdict = `${nr7Streak} consecutive NR7 — deep compression`;
  else if (f.nr7 === true) verdict = 'NR7 — contraction';
  else if (f.nr4 === true) verdict = 'NR4 — mild contraction';
  else if (f.insideDay === true) verdict = 'Inside day';
  else verdict = 'No contraction signal';

  return { verdict, nr7Streak, flags: f };
}
