// CBOE open interest — the raw input, pulled fresh once a day.
//
// MenthorQ sells levels derived from option positioning. CBOE publishes the
// positioning itself, free: every listed strike with its open interest and
// CBOE's own gamma. That means the walls on the chart can be *computed here*
// from a source we can point at, instead of trusted from a black box.
//
// Two books are pulled. NDX is the index NQ actually tracks, so its strikes are
// already in index points. QQQ carries roughly ten times the open interest and
// is where most of the hedging flow sits, so it is the better read on where
// dealers are positioned — but its strikes are in ETF dollars and have to be
// scaled. That scale is computed from the two live prices in the same pull;
// it is never assumed, and if either price is missing the QQQ book is dropped
// rather than mapped with a stale ratio.
//
// The endpoint carries end-of-day open interest and refreshes overnight, so
// pulling more than once a day gets the same numbers. Snapshots are written per
// day and kept, which is what makes positioning *change* visible later.

import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const OI_DIR = join(here, 'cache', 'cboe');

const URL_FOR = (sym) => `https://cdn.cboe.com/api/global/delayed_quotes/options/${sym}.json`;

// NDX options are $100 per index point; QQQ options are 100 shares.
const CONTRACT_MULTIPLIER = 100;

let memo = null;

/** Local (Houston) calendar date — never toISOString, which rolls over at 19:00 CT. */
const ymd = (d = new Date()) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/**
 * OCC symbols look like NDX260821C28550000: root, YYMMDD, C|P, then the strike
 * in thousandths of a dollar. Anything that does not match is skipped rather
 * than guessed at — a misparsed strike would place a wall at a fictional price.
 */
function parseOcc(s) {
  const m = /^([A-Z]+)(\d{2})(\d{2})(\d{2})([CP])(\d{8})$/.exec(s ?? '');
  if (!m) return null;
  return {
    root: m[1],
    expiry: `20${m[2]}-${m[3]}-${m[4]}`,
    right: m[5],
    strike: Number(m[6]) / 1000,
  };
}

async function pull(sym) {
  const r = await fetch(URL_FOR(sym), {
    headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)', Accept: 'application/json' },
  });
  if (!r.ok) throw new Error(`${sym}: HTTP ${r.status}`);
  const j = await r.json();
  const d = j?.data;
  const spot = Number(d?.current_price);
  if (!Number.isFinite(spot) || spot <= 0) throw new Error(`${sym}: no usable spot price in the response`);

  const rows = [];
  for (const o of d.options ?? []) {
    const p = parseOcc(o.option);
    if (!p) continue;
    const oi = Number(o.open_interest) || 0;
    if (oi <= 0) continue;
    rows.push({
      ...p, oi,
      gamma: Number(o.gamma) || 0,
      delta: Number(o.delta) || 0,
      theta: Number(o.theta) || 0,
      volume: Number(o.volume) || 0,
    });
  }
  if (!rows.length) throw new Error(`${sym}: no strikes with open interest`);
  return { symbol: sym, spot, rows, timestamp: j.timestamp ?? null };
}

/**
 * Gamma exposure per strike, in index points of dealer hedging per 1% move.
 *
 * Sign convention: dealers are assumed long calls and short puts against
 * customer flow, which is the standard dealer-positioning read. It is an
 * assumption about who is on the other side, not a measurement — the strike
 * locations below are far more reliable than the sign.
 */
function gexByStrike(rows, spot, scale = 1) {
  const by = new Map();
  for (const r of rows) {
    const k = r.strike * scale;
    const g = r.gamma * r.oi * CONTRACT_MULTIPLIER * spot * spot * 0.01 * (r.right === 'C' ? 1 : -1);
    const cur = by.get(k) ?? { strike: k, gex: 0, callOi: 0, putOi: 0 };
    cur.gex += g;
    if (r.right === 'C') cur.callOi += r.oi; else cur.putOi += r.oi;
    by.set(k, cur);
  }
  return [...by.values()].sort((a, b) => a.strike - b.strike);
}

/**
 * The gamma flip: the strike where cumulative exposure crosses zero. Above it
 * dealer hedging is generally mean-reverting, below it trend-amplifying. Returns
 * null when the book never crosses, which is a real state and not an error.
 */
function gammaFlip(strikes) {
  let run = 0;
  const cum = strikes.map((s) => ({ strike: s.strike, cum: (run += s.gex) }));
  for (let i = 1; i < cum.length; i++) {
    const a = cum[i - 1], b = cum[i];
    if ((a.cum < 0 && b.cum >= 0) || (a.cum > 0 && b.cum <= 0)) {
      // Linear interpolation between the two bracketing strikes.
      const t = Math.abs(a.cum) / (Math.abs(a.cum) + Math.abs(b.cum));
      return a.strike + t * (b.strike - a.strike);
    }
  }
  return null;
}

/**
 * Delta decay — how much of the book's directional hedge expires, and when.
 *
 * Charm is the drift of delta as time passes with price unchanged. Options that
 * finish out of the money bleed their delta to zero, and the dealer hedging that
 * delta has to unwind it. The unwind is mechanical and its *timing* is known in
 * advance, which is the only genuinely predictable thing in this whole file.
 *
 * This does not compute charm analytically — that needs a pricing model and an
 * implied-vol surface. It measures the exposed quantity instead: net delta by
 * expiry bucket. A large near-dated delta is a large scheduled unwind; how
 * violent that unwind is depends on flow nobody here can see.
 */
function deltaDecay(rows, spot, scale, today) {
  // A strike that expired on Friday is not "expiring today" on a Sunday — it is
  // gone, and the hedge behind it has already unwound. Counting dead open
  // interest as a pending unwind would overstate the nearest bucket every
  // weekend and every morning after an expiry.
  const bucket = (expiry) => {
    const days = Math.round((new Date(`${expiry}T00:00:00`) - today) / 86400_000);
    return days < 0 ? 'expired' : days === 0 ? '0dte' : days <= 1 ? '1d'
      : days <= 7 ? 'week' : days <= 35 ? 'month' : 'beyond';
  };

  const acc = {};
  for (const r of rows) {
    const k = bucket(r.expiry);
    // Net delta in index points of underlying exposure. Sign follows the same
    // dealer convention as the gamma figures: long calls, short puts.
    const notional = r.delta * r.oi * CONTRACT_MULTIPLIER * (r.right === 'C' ? 1 : -1) * scale;
    const a = acc[k] ?? (acc[k] = { bucket: k, netDelta: 0, oi: 0, contracts: 0 });
    a.netDelta += notional;
    a.oi += r.oi;
    a.contracts += 1;
  }

  // Shares are of the *live* book, so an expired tail cannot dilute them.
  const order = ['0dte', '1d', 'week', 'month', 'beyond'];
  const liveOi = order.reduce((a, k) => a + (acc[k]?.oi ?? 0), 0);
  const buckets = order.filter((k) => acc[k]).map((k) => ({
    ...acc[k],
    shareOfOi: liveOi > 0 ? acc[k].oi / liveOi : 0,
    label: { '0dte': 'Expires today', '1d': 'Expires tomorrow', week: 'Within a week',
             month: 'Within 35 days', beyond: 'Beyond 35 days' }[k],
  }));
  return {
    buckets,
    liveOi,
    expiredOi: acc.expired?.oi ?? 0,
    asOf: `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`,
  };
}

/** The book for one symbol, reduced to the handful of levels worth plotting. */
function summarise(book, scale, spot, today) {
  const strikes = gexByStrike(book.rows, book.spot, scale);
  const near = strikes.filter((s) => Math.abs(s.strike - spot) / spot < 0.08);
  const walls = [...near].sort((a, b) => Math.abs(b.gex) - Math.abs(a.gex)).slice(0, 6);
  // A wall is only a wall if price has to travel to reach it. Gamma peaks at the
  // money, so ranking the whole book by exposure just returns spot — the call
  // wall is the heaviest positive strike *above* here, the put wall the heaviest
  // negative one *below*. Excluding the strike price is sitting on is the point.
  const callWall = [...near].filter((s) => s.gex > 0 && s.strike > spot)
    .sort((a, b) => b.gex - a.gex)[0] ?? null;
  const putWall = [...near].filter((s) => s.gex < 0 && s.strike < spot)
    .sort((a, b) => a.gex - b.gex)[0] ?? null;

  // A wall two points from spot is arithmetically the heaviest strike on its
  // side and useless as a destination. Thin books get this a lot, because gamma
  // peaks at the money and there is not enough open interest further out to
  // outweigh it. Flag it rather than hide it — the reader needs to know the
  // number is real but not a target.
  const ATM_FRACTION = 0.0025;                      // ~75 points at 30,000
  const atMoney = (w) => w != null && Math.abs(w.strike - spot) < spot * ATM_FRACTION;
  const flip = gammaFlip(strikes);
  return {
    symbol: book.symbol,
    nativeSpot: book.spot,
    scale,
    strikeCount: strikes.length,
    totalOi: book.rows.reduce((a, r) => a + r.oi, 0),
    flip,
    // Which side of the flip spot sits on is the regime, and it is the part the
    // number alone does not tell you: below it, dealer hedging amplifies moves;
    // above it, hedging damps them.
    belowFlip: flip == null ? null : spot < flip,
    decay: deltaDecay(book.rows, spot, scale, today),
    callWall: callWall && { price: callWall.strike, gex: callWall.gex, oi: callWall.callOi, atMoney: atMoney(callWall) },
    putWall: putWall && { price: putWall.strike, gex: putWall.gex, oi: putWall.putOi, atMoney: atMoney(putWall) },
    walls: walls.map((w) => ({ price: w.strike, gex: w.gex, callOi: w.callOi, putOi: w.putOi }))
      .sort((a, b) => b.price - a.price),
  };
}

/**
 * Today's snapshot, pulled once and reused. `force` re-pulls even if today's
 * file exists — used by the manual refresh, not by the page.
 */
export async function getOpenInterest({ force = false } = {}) {
  const today = ymd();
  if (!force && memo?.date === today) return memo.value;

  const file = join(OI_DIR, `${today}.json`);
  if (!force) {
    try {
      const disk = JSON.parse(await readFile(file, 'utf8'));
      memo = { date: today, value: { ...disk, fromCache: true } };
      return memo.value;
    } catch { /* not pulled yet today */ }
  }

  try {
    // NDX first: its price is the scale reference, so a QQQ-only pull is useless.
    const [ndx, corr] = await Promise.all([pull('_NDX'), pullCorrelation()]);
    const spot = ndx.spot;
    const midnight = new Date();
    midnight.setHours(0, 0, 0, 0);

    let qqq = null;
    try {
      const q = await pull('QQQ');
      // Ratio from the two prices in this same pull. No default, no fallback:
      // a wrong scale would put every QQQ wall at a fictional NDX price.
      const scale = spot / q.spot;
      qqq = summarise(q, scale, spot, midnight);
      qqq.note = `QQQ strikes scaled to index points at ${scale.toFixed(4)} `
        + `(NDX ${spot.toFixed(2)} / QQQ ${q.spot.toFixed(2)}), computed from this pull.`;
    } catch (e) {
      qqq = { unavailable: e.message };
    }

    const value = {
      available: true,
      date: today,
      pulledAt: Date.now(),
      source: 'CBOE delayed quotes (cdn.cboe.com), end-of-day open interest',
      spot,
      ndx: summarise(ndx, 1, spot, midnight),
      qqq,
      correlation: {
        ...corr,
        // Term structure, not a level judgement: 1-month above 3-month means the
        // market is pricing more co-movement near term than far, which is what
        // stress looks like. The reverse is the calm, dispersed state.
        inverted: corr.cor1m != null && corr.cor3m != null ? corr.cor1m > corr.cor3m : null,
        note: 'Implied correlation between S&P components. Index volatility is component volatility times '
          + 'correlation, so a low reading means the index is damped while the stocks inside it still move. '
          + 'The feed publishes a spot value and no history, so no percentile is shown — the daily snapshots '
          + 'here are what will make one possible.',
      },
      caveats: [
        'Open interest is end-of-day and settles overnight — it describes yesterday\'s book, not intraday flow.',
        'Quotes are delayed; the strike locations are what matter here, not the prices attached to them.',
        'The dealer-positioning sign (long calls, short puts) is a convention, not a measurement.',
      ],
    };

    await mkdir(OI_DIR, { recursive: true });
    await writeFile(file, JSON.stringify(value));
    memo = { date: today, value };
    return value;
  } catch (e) {
    // Fall back to the most recent snapshot on disk, clearly aged. A wall from
    // two days ago is still roughly where it was; a blank panel tells you nothing.
    try {
      const files = (await readdir(OI_DIR)).filter((f) => f.endsWith('.json')).sort();
      if (!files.length) throw new Error('no snapshots on disk');
      const last = files.at(-1);
      const disk = JSON.parse(await readFile(join(OI_DIR, last), 'utf8'));
      const ageDays = Math.round((Date.now() - disk.pulledAt) / 86400_000);
      return { ...disk, stale: true, ageDays, staleReason: e.message };
    } catch {
      return { available: false, reason: `CBOE open interest unavailable: ${e.message}` };
    }
  }
}

/**
 * Implied correlation, and the vol term structure beside it.
 *
 * COR1M is the correlation the option market is pricing between S&P components.
 * Index vol is component vol times correlation, so when correlation collapses,
 * index vol is suppressed even though single names are still moving — a
 * dispersion regime. That is directly relevant to an index breakout strategy:
 * it is the state in which the index grinds while the stocks inside it do not.
 *
 * The honest limitation: the feed publishes a current value and no history, so
 * no percentile can be computed and none is shown. Each daily snapshot stores
 * the reading, which means the history this needs accumulates from here rather
 * than being asserted from a range nobody verified.
 */
async function pullCorrelation() {
  const syms = { cor1m: '^COR1M', cor3m: '^COR3M', vix1d: '^VIX1D', vxn: '^VXN' };
  const out = {};
  await Promise.all(Object.entries(syms).map(async ([k, s]) => {
    try {
      const r = await fetch(
        `https://query1.finance.yahoo.com/v8/finance/chart/${encodeURIComponent(s)}?interval=1d&range=5d`,
        { headers: { 'User-Agent': 'Mozilla/5.0' } });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      const meta = (await r.json())?.chart?.result?.[0]?.meta;
      const v = Number(meta?.regularMarketPrice);
      out[k] = Number.isFinite(v) ? v : null;
    } catch { out[k] = null; }
  }));
  return out;
}

/** Snapshot dates already on disk, oldest first — the history that accumulates. */
export async function snapshotDates() {
  try {
    return (await readdir(OI_DIR)).filter((f) => f.endsWith('.json')).map((f) => f.slice(0, -5)).sort();
  } catch { return []; }
}
