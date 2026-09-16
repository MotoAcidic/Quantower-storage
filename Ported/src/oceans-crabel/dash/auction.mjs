// Auction Market Theory — the value area, built the way Steidlmayer defined it.
//
// Market Profile counts *time* at price, not volume. Each half-hour the market
// spends at a price is one TPO (time-price opportunity); the price with the most
// TPOs is the point of control, and the band containing 70% of them is the value
// area. That definition is the original one and it is what this computes, which
// matters because the intraday feed here carries no volume — a "volume profile"
// would have to be faked, and a faked profile is worse than an honest TPO one.
//
// What the theory claims: price rotates around value, and an auction that opens
// outside the prior day's value area and stays there is doing something
// different from one that opens inside it. What this module does is compute the
// levels and report where the open sits. Whether that changes the odds is
// measured separately — nothing here asserts an edge.

const RTH_START = 9 * 60 + 30;
const RTH_END = 16 * 60;
const IB_END = RTH_START + 60;         // Initial Balance = the first hour
const VALUE_AREA = 0.70;

const toMin = (hm) => { const [h, m] = hm.split(':').map(Number); return h * 60 + m; };

/**
 * Bucket size in index points. Fixed buckets across sessions of different width
 * would make a quiet day look like it had a tight value area purely because the
 * bucket was coarse, so this scales with the session and then snaps to a round
 * number a person can actually read off a chart.
 */
function bucketFor(range) {
  const raw = range / 50;
  return Math.max(5, Math.round(raw / 5) * 5);
}

/**
 * One session's profile from its intraday bars.
 *
 * A bar contributes one TPO to every bucket between its low and its high. That
 * over-counts relative to true tick data — a 5-minute bar that spiked once
 * marks the spike bucket as fully visited — but it is the standard bar-based
 * approximation and it is symmetric, so the shape survives.
 */
export function sessionProfile(bars) {
  const rth = bars.filter((b) => {
    const m = toMin(b.hm);
    return m >= RTH_START && m < RTH_END;
  });
  if (rth.length < 12) return { available: false, reason: `only ${rth.length} RTH bars` };

  const high = Math.max(...rth.map((b) => b.high));
  const low = Math.min(...rth.map((b) => b.low));
  const range = high - low;
  if (!(range > 0)) return { available: false, reason: 'zero range' };

  const step = bucketFor(range);
  const base = Math.floor(low / step) * step;
  const counts = new Map();
  for (const b of rth) {
    const lo = Math.floor((b.low - base) / step);
    const hi = Math.floor((b.high - base) / step);
    for (let k = lo; k <= hi; k++) counts.set(k, (counts.get(k) ?? 0) + 1);
  }

  const total = [...counts.values()].reduce((a, b) => a + b, 0);
  const pocIdx = [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0] - b[0])[0][0];

  // Value area: expand from the POC, each step taking whichever neighbour holds
  // more TPOs, until 70% of them are inside. This is the standard construction —
  // it is asymmetric by design, because value usually is.
  let lo = pocIdx, hi = pocIdx, acc = counts.get(pocIdx);
  const target = total * VALUE_AREA;
  while (acc < target) {
    const up = counts.get(hi + 1) ?? 0;
    const down = counts.get(lo - 1) ?? 0;
    if (up === 0 && down === 0) break;
    if (up >= down) { hi += 1; acc += up; } else { lo -= 1; acc += down; }
  }

  const ib = rth.filter((b) => toMin(b.hm) < IB_END);
  const price = (idx) => base + idx * step + step / 2;

  return {
    available: true,
    date: rth[0].date,
    high,
    low,
    open: rth[0].open,
    close: rth.at(-1).close,
    step,
    poc: price(pocIdx),
    vah: price(hi),
    val: price(lo),
    valueAreaPct: acc / total,
    ib: ib.length
      ? { high: Math.max(...ib.map((b) => b.high)), low: Math.min(...ib.map((b) => b.low)), bars: ib.length }
      : null,
    buckets: [...counts.entries()]
      .sort((a, b) => a[0] - b[0])
      .map(([k, c]) => ({ price: price(k), tpo: c, inValue: k >= lo && k <= hi, isPoc: k === pocIdx })),
  };
}

/**
 * Where today's open sits relative to yesterday's value — the one question
 * Auction Market Theory actually asks before the bell.
 *
 * The three states are the classical ones. The wording deliberately describes
 * the geometry rather than predicting an outcome: "opened above value" is a
 * fact, "will trend" is not, and this tool does not have evidence for the second.
 */
export function openVsValue(openPrice, prior) {
  if (!prior?.available || openPrice == null) return null;
  const { vah, val, poc } = prior;
  const state = openPrice > vah ? 'above' : openPrice < val ? 'below' : 'inside';
  return {
    state,
    open: openPrice,
    vah, val, poc,
    distanceToValue: state === 'above' ? openPrice - vah : state === 'below' ? val - openPrice : 0,
    note: state === 'inside'
      ? 'Opened inside prior value. The auction is continuing where it left off — there is no imbalance '
        + 'to correct, and the day is more likely to rotate than to trend.'
      : state === 'above'
        ? 'Opened above prior value. Either the market accepts the higher prices and builds new value up '
          + 'here, or it rejects them and rotates back into the old area. Which one it does is the day.'
        : 'Opened below prior value. Either lower prices are accepted and value migrates down, or they are '
          + 'rejected and price returns into the old area. Which one it does is the day.',
  };
}

/**
 * The prior session's profile plus today's relationship to it.
 * `barsByDay` is a Map of date -> bars, the same structure the zone builder uses.
 */
export function buildAuction(barsByDay, dates, currentOpen) {
  if (!barsByDay || !dates?.length) return { available: false, reason: 'no intraday bars' };

  const priorDate = dates.at(-1);
  const prior = sessionProfile(barsByDay.get(priorDate) ?? []);
  if (!prior.available) return { available: false, reason: `prior session: ${prior.reason}` };

  return {
    available: true,
    prior,
    openVsValue: openVsValue(currentOpen, prior),
    method: 'TPO profile — time at price, 5-minute bars, 70% value area',
    caveats: [
      'TPO counts time, not volume; the intraday feed carries no volume, so a volume profile is not available.',
      'A 5-minute bar marks every bucket it touched as visited, which slightly widens the profile versus tick data.',
      'These are the prior RTH session\'s levels. They are reference prices, not signals — nothing here has been '
        + 'measured to show that trading them beats the baseline.',
    ],
  };
}
