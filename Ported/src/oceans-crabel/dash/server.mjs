// Local dashboard server for Ocean's Crabel. No local software required.
//
// Everything here is real 9:30-16:00 RTH. Two sources, each doing the job it is
// actually good at, never blended and always named in the payload:
//
//   LIVE READ (verdict, flags, Stretch, ORB)
//     NQ=F 5-minute bars, aggregated into RTH sessions. These are genuine NQ
//     futures in genuine index points -- no proxy, no conversion. Yahoo caps 5m
//     history at 60 days, which is ~50 sessions: thin for statistics, but ample
//     for the flags (NR20 needs 20) and the Stretch (needs 10).
//
//   BASE RATES (historical cohort statistics)
//     QQQ daily bars, which ARE the RTH session -- QQQ has no overnight tape, so
//     its official daily OHLC is the 9:30 auction open to the 16:00 auction close.
//     That sidesteps the intraday history cap entirely and reaches back a decade.
//     Verified against RTH sessions rebuilt from QQQ intraday: NR7 and NR4 flags
//     agreed 54/54, median range difference 0.05%.
//
// QQQ is a stand-in for NQ in the statistics only. Measured over 50 overlapping
// sessions: range% correlation 0.9951, NR7 and NR4 flags 44/44. It never touches
// the live numbers, so no ratio or unit conversion exists anywhere in this file.

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import * as C from './crabel.mjs';
import { getDeepRth } from './lse.mjs';
import { getLevels, bracketContext } from './menthorq.mjs';
import { getWeekEarnings, weekDates } from './earnings.mjs';
import { getOpenInterest, snapshotDates } from './cboe.mjs';
import { expiryCalendar, measureExpiryEffect } from './expiry.mjs';
import { buildAuction } from './auction.mjs';

// The trader is in Houston. The exchange runs on New York time, so every clock
// value is computed in ET (where the session boundaries are defined) and then
// presented in CT. Both zones observe DST on the same dates, so the offset is a
// constant hour — no separate conversion table is needed.
const CT_OFFSET_MIN = -60;
const toCentral = (hm) => {
  const [h, m] = hm.split(':').map(Number);
  const t = (h * 60 + m + CT_OFFSET_MIN + 1440) % 1440;
  return `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`;
};

const here = dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT ?? 7777);

const chartUrl = (sym, range, interval, prePost) =>
  `https://query1.finance.yahoo.com/v8/finance/chart/${encodeURIComponent(sym)}` +
  `?range=${range}&interval=${interval}&includePrePost=${prePost}`;

const YAHOO_TTL_MS = 60_000;
const HISTORY_TTL_MS = 60 * 60_000;   // base-rate sample changes once a day at most

const RTH_START = '09:30';
const RTH_END = '16:00';

const cache = new Map();              // key -> { at, sessions }

/** Bar timestamp rendered in exchange-local (New York) time. */
const etParts = (epochSec) => {
  const s = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'America/New_York',
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false,
  }).format(new Date(epochSec * 1000));
  const [date, hm] = s.split(', ');
  return { date, hm };
};

// ---------------------------------------------------------------- data sources

/** Raw bar fetch, tagged with exchange-local date and time-of-day. */
async function fetchBars(sym, range, interval, prePost, ttl) {
  const key = `${sym}|${range}|${interval}|${prePost}`;
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < ttl) return { ok: true, bars: hit.bars, cached: true };

  try {
    const r = await fetch(chartUrl(sym, range, interval, prePost), { headers: { 'User-Agent': 'Mozilla/5.0' } });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);

    const j = await r.json();
    if (j?.chart?.error) throw new Error(j.chart.error.description ?? j.chart.error.code);

    const res = j?.chart?.result?.[0];
    const ts = res?.timestamp ?? [];
    const q = res?.indicators?.quote?.[0] ?? {};

    const bars = [];
    for (let i = 0; i < ts.length; i++) {
      if (q.open?.[i] == null || q.high?.[i] == null || q.low?.[i] == null || q.close?.[i] == null) continue;
      const { date, hm } = etParts(ts[i]);
      bars.push({ date, hm, open: q.open[i], high: q.high[i], low: q.low[i], close: q.close[i] });
    }
    if (bars.length === 0) throw new Error('no usable bars in response');

    cache.set(key, { at: Date.now(), bars });
    return { ok: true, bars, cached: false };
  } catch (e) {
    return { ok: false, reason: `Yahoo ${sym} ${interval} fetch failed: ${e.message}` };
  }
}

/**
 * Aggregate intraday bars into RTH sessions. Only the session boundaries matter
 * for OHLC, not the bar granularity, so a coarse interval loses nothing as long
 * as its grid lands on 09:30 -- which is why this validates the first bar rather
 * than trusting it.
 */
function aggregateRth(bars, keep) {
  const byDay = new Map();
  for (const b of bars) {
    if (b.hm < RTH_START || b.hm >= RTH_END) continue;
    if (!byDay.has(b.date)) byDay.set(b.date, []);
    byDay.get(b.date).push(b);
  }
  if (keep) { keep.byDay = byDay; }

  const sessions = [], rejected = [];
  for (const [date, list] of [...byDay.entries()].sort()) {
    // A session whose first bar is not the 09:30 bar has a truncated open. That
    // is the failure that makes a Stretch meaningless, so it is dropped loudly
    // rather than quietly averaged in.
    if (list[0].hm !== RTH_START) {
      rejected.push({ date, reason: `first bar ${list[0].hm}, expected ${RTH_START}`, bars: list.length });
      continue;
    }
    if (list.length < 3) {
      rejected.push({ date, reason: `only ${list.length} bar(s) in session`, bars: list.length });
      continue;
    }
    sessions.push({
      date,
      open: list[0].open,
      high: Math.max(...list.map((x) => x.high)),
      low: Math.min(...list.map((x) => x.low)),
      close: list.at(-1).close,
      bars: list.length,
      lastBar: list.at(-1).hm,
      // A session only counts as finished once its bars reach the closing bell.
      // Mid-session the range is still growing, and folding a half-formed range
      // into the NR comparisons would manufacture a false narrow day every
      // morning -- the flags would read NR7 at 10am on almost any date.
      complete: list.at(-1).hm >= '15:55',
    });
  }
  return { sessions, rejected };
}

// MNQ friction, index points per round trip. $2/point, so 0.75 pts = $1.50.
// Every expectancy figure on this dashboard is quoted net of this.
const COST_PTS = { low: 0.75, high: 2.0 };

/**
 * Measured expectancy, from a path-aware backtest on 5-minute bars.
 *
 * These are CONSTANTS from a specific study, not live computations — recomputing
 * a 2,600-session backtest on every poll would be absurd. The provenance travels
 * with them so nobody mistakes them for today's numbers.
 *
 *   sample     NAS100/USD 5m RTH, 2016-01-04 .. 2026-08-06, 2,636 sessions
 *   method     enter first touch, stop 2x trigger distance, exit at the close;
 *              stop taken when a bar contains both stop and target (pessimistic)
 *   validation band multiplier fitted on 2016..2021-08, tested 2021-08..2026
 *   trials     ~21 variants; Bonferroni t for 5% at 21 trials ~= 3.4
 */
const EDGE = {
  sample: '2,636 RTH sessions, 2016-01 to 2026-08 (NAS100 5m, proxy for NQ)',
  variants: [
    { key: 'band075', label: 'Volatility-scaled band, 0.75x',
      expLow: 0.319, tLow: 5.05, n: 1236, oos: true, years: '11/11 positive',
      note: 'Trigger widens as the session ages. Out-of-sample after fitting on 2016-2021.' },
    { key: 'band100', label: 'Volatility-scaled band, 1.0x',
      expLow: 0.179, tLow: 5.15, expHigh: 0.144, tHigh: 4.14, n: 2507, oos: false,
      note: 'Full-sample.' },
    { key: 'stretch', label: 'Fixed Crabel Stretch (what this bracket uses)',
      expLow: 0.065, tLow: 2.43, expHigh: 0.036, tHigh: 1.34, n: 2603, oos: false,
      note: 'Fails the t>=3 bar once realistic friction is applied.' },
    { key: 'nr7c', label: 'Fixed Stretch, only after 2nd consecutive NR7',
      expLow: 0.086, tLow: 0.67, expHigh: 0.059, tHigh: 0.46, n: 70, oos: false,
      note: 'Indistinguishable from zero at any cost level. This is today\'s setup.' },
  ],
  hurdle: 3.4,
};

/**
 * The volatility-scaled trigger, rebuilt live from the same 5-minute bars.
 *
 * Unlike the Stretch — one distance fixed before the open — this widens through
 * the session: at each bar it is the typical absolute move from the open by that
 * point, averaged over recent sessions. That adaptation is the whole difference,
 * and it is what the measured edge above attaches to.
 */
function buildBand(barsByDay, dates, mult = 0.75, look = 14) {
  if (dates.length < look + 1) return { available: false, reason: `needs ${look + 1} sessions` };

  const recent = dates.slice(-look);
  const maxBars = Math.max(...recent.map((d) => barsByDay.get(d).length));
  const profile = [];
  for (let k = 0; k < maxBars; k++) {
    let s = 0, n = 0;
    for (const d of recent) {
      const list = barsByDay.get(d);
      if (!list[k]) continue;
      s += Math.abs(list[k].close - list[0].open);
      n++;
    }
    if (n >= Math.ceil(look * 0.6)) profile.push({ hm: barsByDay.get(recent.at(-1))[k]?.hm ?? null, points: (s / n) * mult });
  }
  if (!profile.length) return { available: false, reason: 'no usable bars' };

  const at = (hm) => profile.find((p) => p.hm === hm)?.points ?? null;
  return {
    available: true, mult, lookback: look,
    profile: profile.filter((p) => p.hm),
    at0935: at('09:35'), at1000: at('10:00'), at1030: at('10:30'), at1200: at('12:00'),
  };
}

/** Live read: real NQ futures RTH sessions, in real index points. */
async function fetchNqRth() {
  const r = await fetchBars('NQ=F', '60d', '5m', true, YAHOO_TTL_MS);
  if (!r.ok) return { ok: false, reason: r.reason };

  const keep = {};
  const { sessions, rejected } = aggregateRth(r.bars, keep);

  // Only finished sessions feed the pattern maths. An in-progress one is handed
  // back separately so its OPEN can anchor today's bracket.
  const history = sessions.filter((s) => s.complete);
  const lastRaw = sessions.at(-1);
  let current = lastRaw && !lastRaw.complete ? lastRaw : null;

  if (history.length < 21) {
    return { ok: false, reason: `only ${history.length} complete RTH sessions from NQ=F 5m (need 21+)` };
  }

  // Refine the running session with 1-minute bars. Bracket alerts fire off this
  // high/low, and 5-minute granularity would mean learning about a touch up to
  // five minutes after it happened.
  let granularity = '5m';
  if (current) {
    const fine = await fetchBars('NQ=F', '1d', '1m', true, 30_000);
    if (fine.ok) {
      const today = aggregateRth(fine.bars).sessions.find((s) => s.date === current.date);
      if (today) { current = { ...today, complete: false }; granularity = '1m'; }
    }
  }

  const deep = await getDeepRth();
  const completeDates = history.map((s) => s.date);
  return {
    ok: true, sessions: history, current, rejected, currentGranularity: granularity,
    zones: buildZones(r.bars, deep.ok ? deep.sessions : null),
    band: buildBand(keep.byDay, completeDates),
    // Prior-session value area. Built off the same bar map, so it costs one pass
    // and cannot drift out of sync with the zone work above.
    auction: buildAuction(keep.byDay, completeDates, current?.open ?? null),
  };
}

/**
 * Base-rate sample: QQQ daily bars, which are already the RTH session.
 *
 * Yahoo silently returns MONTHLY bars for range=max even with interval=1d, so
 * the bar spacing is asserted rather than assumed -- that trap produced a
 * plausible-looking but completely wrong NR7 count during investigation.
 */
async function fetchQqqRth(range = '10y') {
  const r = await fetchBars('QQQ', range, '1d', false, HISTORY_TTL_MS);
  if (!r.ok) return { ok: false, reason: r.reason };

  const bars = r.bars;
  const gaps = [];
  for (let i = 1; i < bars.length; i++) {
    gaps.push((Date.parse(bars[i].date) - Date.parse(bars[i - 1].date)) / 86_400_000);
  }
  gaps.sort((a, b) => a - b);
  const medianGap = gaps[Math.floor(gaps.length / 2)];
  if (medianGap > 5) {
    return { ok: false, reason: `QQQ ${range} returned downsampled bars (median gap ${medianGap}d, expected 1d)` };
  }

  return {
    ok: true,
    sessions: bars.map(({ date, open, high, low, close }) => ({ date, open, high, low, close })),
    medianGap,
  };
}

async function readYahoo(sym, range, ttl = YAHOO_TTL_MS) {
  const key = `${sym}|${range}|1d|false`;
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < ttl) return { ok: true, sessions: hit.bars, cached: true };

  const r = await fetchBars(sym, range, '1d', false, ttl);
  if (!r.ok) return r;
  return { ok: true, sessions: r.bars, cached: r.cached };
}

// ---------------------------------------------------------------- session map

// Intraday structure zones in ET. Named for how they are commonly traded, then
// measured against what actually happens rather than assumed to work.
const ZONES = [
  { key: 'asia',      label: 'Asia',                 from: '20:00', to: '24:00', rth: false },
  { key: 'asiaLate',  label: 'Asia late',            from: '00:00', to: '02:00', rth: false },
  { key: 'euPos',     label: 'Europe positioning',   from: '02:00', to: '05:00', rth: false },
  { key: 'euCont',    label: 'Europe continuation',  from: '05:00', to: '07:00', rth: false },
  { key: 'premkt',    label: 'NY premarket',         from: '07:00', to: '09:30', rth: false },
  { key: 'nyOpen',    label: 'NY open / repricing',  short: 'NY open',   from: '09:30', to: '10:00', rth: true },
  { key: 'amRev',     label: 'AM reversal zone',     short: 'AM rev',    from: '10:00', to: '11:00', rth: true },
  { key: 'lateAm',    label: 'Late morning',         short: 'Late am',   from: '11:00', to: '12:00', rth: true },
  { key: 'lunch',     label: 'Lunch',                short: 'Lunch',     from: '12:00', to: '13:00', rth: true },
  { key: 'pm',        label: 'PM trend',             short: 'PM trend',  from: '13:00', to: '15:00', rth: true },
  { key: 'close',     label: 'Close',                short: 'Close',     from: '15:00', to: '16:00', rth: true },
];
const zoneOf = (hm) => ZONES.find((z) => hm >= z.from && hm < z.to) ?? null;
const toMinutes = (hm) => { const [h, m] = hm.split(':').map(Number); return h * 60 + m; };
const hhmmOf = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;

/**
 * Where the session's extremes actually form, and whether three commonly-traded
 * claims hold on this instrument: that the open is a trap, that the premarket
 * range gets engineered, and that the first hour defines the day.
 */
const BUCKET_MIN = 30;   // 13 buckets across the 6.5-hour session

function buildZones(bars, deepSessions) {
  const highs = {}, lows = {};
  let n = 0, judasYes = 0, judasNo = 0, earlyExtreme = 0, bothEarly = 0;

  // Fine-grained profile: with thousands of sessions the shape of the day is
  // worth showing directly, rather than only as six coarse zone averages.
  const nBuckets = Math.ceil((toMinutes(RTH_END) - toMinutes(RTH_START)) / BUCKET_MIN);
  const profHigh = new Array(nBuckets).fill(0);
  const profLow = new Array(nBuckets).fill(0);
  const bucketOf = (hm) => {
    const i = Math.floor((toMinutes(hm) - toMinutes(RTH_START)) / BUCKET_MIN);
    return i >= 0 && i < nBuckets ? i : null;
  };

  // Where extremes form and whether the open traps: measured on the deep
  // intraday history when it is available, which is ~50x the sample the
  // 60-day Yahoo window can offer.
  const deep = Array.isArray(deepSessions) && deepSessions.length > 500;
  if (deep) {
    for (const s of deepSessions) {
      n++;
      const hz = zoneOf(s.highAt)?.key, lz = zoneOf(s.lowAt)?.key;
      if (hz) highs[hz] = (highs[hz] ?? 0) + 1;
      if (lz) lows[lz] = (lows[lz] ?? 0) + 1;
      const hb = bucketOf(s.highAt), lb = bucketOf(s.lowAt);
      if (hb != null) profHigh[hb]++;
      if (lb != null) profLow[lb]++;
      const d30 = s.first30Close - s.open, dDay = s.close - s.open;
      if (d30 !== 0 && dDay !== 0) (Math.sign(d30) !== Math.sign(dDay) ? judasYes++ : judasNo++);
      // Counted once per session: the share of sessions where AT LEAST ONE of
      // the two extremes lands before 10:30. Not the sum of the two shares —
      // that would double-count any session setting both early.
      if (s.highAt < '10:30' || s.lowAt < '10:30') earlyExtreme++;
      if (s.highAt < '10:30' && s.lowAt < '10:30') bothEarly++;
    }
  }

  // Premarket behaviour needs pre-9:30 bars, which the deep RTH-only feed does
  // not carry. It stays on the shallower Yahoo window and reports its own n.
  const byDay = new Map();
  for (const b of bars) {
    if (!byDay.has(b.date)) byDay.set(b.date, []);
    byDay.get(b.date).push(b);
  }
  let preN = 0, sweptEither = 0, sweptHigh = 0, sweptLow = 0;
  for (const date of [...byDay.keys()].sort()) {
    const list = byDay.get(date);
    const rth = list.filter((b) => b.hm >= RTH_START && b.hm < RTH_END);
    const pre = list.filter((b) => b.hm >= '07:00' && b.hm < RTH_START);
    if (rth.length < 70 || rth[0].hm !== RTH_START || pre.length < 20) continue;

    const hi = Math.max(...rth.map((b) => b.high));
    const lo = Math.min(...rth.map((b) => b.low));
    const preHi = Math.max(...pre.map((b) => b.high));
    const preLo = Math.min(...pre.map((b) => b.low));

    preN++;
    if (hi > preHi) sweptHigh++;
    if (lo < preLo) sweptLow++;
    if (hi > preHi || lo < preLo) sweptEither++;

    if (!deep) {
      // No deep history: fall back to measuring the rest here too, at this n.
      n++;
      const hiBar = rth.find((b) => b.high === hi), loBar = rth.find((b) => b.low === lo);
      const hz = zoneOf(hiBar.hm)?.key, lz = zoneOf(loBar.hm)?.key;
      if (hz) highs[hz] = (highs[hz] ?? 0) + 1;
      if (lz) lows[lz] = (lows[lz] ?? 0) + 1;
      const first30 = rth.filter((b) => b.hm < '10:00').at(-1).close;
      const d30 = first30 - rth[0].open, dDay = rth.at(-1).close - rth[0].open;
      if (d30 !== 0 && dDay !== 0) (Math.sign(d30) !== Math.sign(dDay) ? judasYes++ : judasNo++);
      if (hiBar.hm < '10:30' || loBar.hm < '10:30') earlyExtreme++;
    }
  }

  if (!n) return { available: false, reason: 'no complete sessions available' };

  const judasN = judasYes + judasNo;
  const judasRate = judasN ? judasYes / judasN : null;
  const judasSe = judasN ? Math.sqrt(0.25 / judasN) : null;   // under a 50/50 null

  return {
    available: true,
    sessions: n,
    sampleSource: deep ? 'NAS100/USD 5m RTH via London Strategic Edge' : 'NQ=F 5m (60-day window)',
    premarketSessions: preN,
    bucketMinutes: BUCKET_MIN,
    profile: profHigh.map((h, i) => ({
      from: hhmmOf(toMinutes(RTH_START) + i * BUCKET_MIN),
      to: hhmmOf(toMinutes(RTH_START) + (i + 1) * BUCKET_MIN),
      highPct: n ? h / n : 0,
      lowPct: n ? profLow[i] / n : 0,
      // Both extremes share one clock, so chance for either is 1/nBuckets.
      concentration: n ? ((h + profLow[i]) / (2 * n)) / (1 / nBuckets) : 0,
    })),
    zones: ZONES.filter((z) => z.rth).map((z) => {
      const mins = toMinutes(z.to) - toMinutes(z.from);
      const extremePct = ((highs[z.key] ?? 0) + (lows[z.key] ?? 0)) / (2 * n);
      // Raw share is misleading when zones differ in length: a two-hour window
      // has four times the chance of containing an extreme that a thirty-minute
      // one does. Concentration divides that out — 1.0 means "no more than
      // its share of the clock".
      const expected = mins / (toMinutes(RTH_END) - toMinutes(RTH_START));
      return {
        ...z,
        minutes: mins,
        highPct: (highs[z.key] ?? 0) / n,
        lowPct: (lows[z.key] ?? 0) / n,
        extremePct,
        expectedPct: expected,
        concentration: expected > 0 ? extremePct / expected : null,
      };
    }),
    overnight: ZONES.filter((z) => !z.rth),
    findings: {
      earlyExtremeRate: earlyExtreme / n,
      bothEarlyRate: bothEarly / n,
      judasExcluded: n - (judasYes + judasNo),
      judasRate,
      // The opening move reversing less than half the time is the interesting
      // result, so the test is two-sided against a coin flip.
      judasSignificant: judasRate != null && Math.abs(judasRate - 0.5) >= 2 * judasSe,
      judasN,
      // Premarket counters come from the shallow window, so they divide by ITS
      // count. Dividing by the deep n silently reported 98% as 1.9%.
      sweptEitherRate: preN ? sweptEither / preN : null,
      sweptHighRate: preN ? sweptHigh / preN : null,
      sweptLowRate: preN ? sweptLow / preN : null,
    },
  };
}

// ------------------------------------------------------------------- calendar

const FF_URL = 'https://nfs.faireconomy.media/ff_calendar_thisweek.json';
const CAL_TTL_MS = 30 * 60_000;
const CAL_BACKOFF_MS = 5 * 60_000;   // the feed rate-limits; do not hammer it
let calCache = { at: 0, events: null, lastTry: 0, lastError: null };

/**
 * Scheduled catalysts from the ForexFactory weekly feed.
 *
 * The feed covers the CURRENT WEEK ONLY — there is no next-week endpoint — so
 * late in the week, and over the weekend, it legitimately contains no future
 * events. That is reported as "nothing scheduled in this feed", never as
 * "nothing scheduled".
 *
 * FinancialJuice has no public calendar API; see the note in the payload.
 */
async function fetchEvents() {
  const fresh = calCache.events && Date.now() - calCache.at < CAL_TTL_MS;
  if (fresh) return { ok: true, events: calCache.events, ageMinutes: 0 };

  // The feed returns 429 under repeated hits. Back off, and keep serving the
  // last good copy rather than blanking a panel that has usable data — a stale
  // calendar clearly labelled beats no calendar.
  const backingOff = Date.now() - calCache.lastTry < CAL_BACKOFF_MS;
  if (backingOff && calCache.events) {
    return { ok: true, events: calCache.events, stale: true,
             ageMinutes: Math.round((Date.now() - calCache.at) / 60000), note: calCache.lastError };
  }
  if (backingOff) return { ok: false, reason: calCache.lastError ?? 'backing off after a failed fetch' };

  calCache.lastTry = Date.now();
  try {
    const r = await fetch(FF_URL, { headers: { 'User-Agent': 'Mozilla/5.0' } });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const raw = await r.json();
    if (!Array.isArray(raw)) throw new Error('unexpected feed shape');

    const events = raw
      // USD plus global entries (OPEC, G20) — the rest cannot move NQ enough to matter.
      .filter((e) => e.country === 'USD' || e.country === 'All')
      .map((e) => {
        const when = new Date(e.date);
        const { hm } = etParts(Math.floor(when.getTime() / 1000));
        return {
          title: e.title,
          country: e.country,
          impact: e.impact,
          etTime: hm,
          ctTime: toCentral(hm),
          date: etParts(Math.floor(when.getTime() / 1000)).date,
          epoch: when.getTime(),
          forecast: e.forecast || null,
          previous: e.previous || null,
          // When it lands relative to the session is the part an ORB trader cares
          // about: a pre-open print sets the open, a mid-session one detonates it.
          window: hm < RTH_START ? 'pre-open' : hm >= RTH_END ? 'after-close' : 'in-session',
        };
      })
      .sort((a, b) => a.epoch - b.epoch);

    calCache = { at: Date.now(), events, lastTry: Date.now(), lastError: null };
    return { ok: true, events, ageMinutes: 0 };
  } catch (e) {
    const msg = `ForexFactory feed unavailable: ${e.message}`;
    calCache.lastError = msg;
    if (calCache.events) {
      return { ok: true, events: calCache.events, stale: true,
               ageMinutes: Math.round((Date.now() - calCache.at) / 60000), note: msg };
    }
    return { ok: false, reason: msg };
  }
}

const NEWS_TTL_MS = 10 * 60_000;
let newsCache = { at: 0, items: null, lastError: null };

/**
 * Headlines tied to the instrument, from Yahoo's search endpoint.
 *
 * This is context, not signal — there is no attempt to score sentiment or infer
 * direction from a headline, because nothing in this tool has shown that works.
 * It exists so a move has a possible explanation rather than being a mystery.
 */
async function fetchNews() {
  if (newsCache.items && Date.now() - newsCache.at < NEWS_TTL_MS) {
    return { ok: true, items: newsCache.items, ageMinutes: 0 };
  }
  // Ticker symbols return live news; free-text queries ("Nasdaq 100") return
  // evergreen explainers that were 41 days old when tested. Two symbols are
  // merged and de-duplicated, then anything older than three days is dropped —
  // a stale headline on a "what to watch" page is worse than an empty one.
  const MAX_AGE_MS = 72 * 3600_000;
  try {
    const pulls = await Promise.all(['QQQ', '^NDX'].map(async (sym) => {
      const url = 'https://query1.finance.yahoo.com/v1/finance/search'
        + `?q=${encodeURIComponent(sym)}&newsCount=12&quotesCount=0`;
      const r = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0' } });
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      return (await r.json()).news ?? [];
    }));

    const seen = new Set();
    const items = pulls.flat()
      .filter((n) => n.title && n.providerPublishTime)
      .map((n) => ({
        title: n.title,
        publisher: n.publisher ?? 'unknown',
        link: n.link ?? null,
        epoch: n.providerPublishTime * 1000,
      }))
      .filter((n) => Date.now() - n.epoch <= MAX_AGE_MS)
      .filter((n) => { const k = n.title.toLowerCase(); if (seen.has(k)) return false; seen.add(k); return true; })
      .sort((a, b) => b.epoch - a.epoch);
    if (!items.length) throw new Error('no headlines inside the 72-hour window');

    newsCache = { at: Date.now(), items, lastError: null };
    return { ok: true, items, ageMinutes: 0 };
  } catch (e) {
    const msg = `Yahoo news unavailable: ${e.message}`;
    newsCache.lastError = msg;
    if (newsCache.items) {
      return { ok: true, items: newsCache.items, stale: true,
               ageMinutes: Math.round((Date.now() - newsCache.at) / 60000), note: msg };
    }
    return { ok: false, reason: msg };
  }
}

async function buildWatch() {
  const [cal, news, earn] = await Promise.all([buildCalendar(), fetchNews(), getWeekEarnings()]);
  const now = Date.now();

  const upcoming = cal.available ? (cal.upcoming ?? []) : [];

  // The session being planned, and the week it sits in — so the page can answer
  // "what's on today" and "what's on this week" rather than only "what's next".
  const etNowStr = etParts(Math.floor(now / 1000));
  // The page speaks Houston time, so the calendar day it highlights is Houston's.
  // Between 23:00 and midnight CT the two dates disagree, and labelling that hour
  // "tomorrow" because New York rolled over would be wrong to the person reading it.
  const ctNowStr = etParts(Math.floor(now / 1000) - 3600);
  const week = weekDates(new Date(now));
  const allWeek = cal.available ? (cal.events ?? upcoming) : [];
  // The ForexFactory feed publishes one week at a time and rolls over on Sunday.
  // Ask it on a Saturday for the week starting Monday and it still holds the week
  // that just closed — zero overlap. That has to read as "not published yet",
  // never as an empty grid implying a quiet week.
  const macroCovered = allWeek.some((e) => week.includes(e.date));
  const byDay = week.map((d) => ({
    date: d,
    events: allWeek.filter((e) => e.date === d),
    high: allWeek.filter((e) => e.date === d && e.impact === 'High'),
    earnings: [...(earn.ok ? earn.mega : []), ...(earn.ok ? earn.large : [])].filter((x) => x.date === d),
  }));

  return {
    calendar: cal,
    today: ctNowStr.date,
    sessionDate: etNowStr.date,
    week,
    weekIsNext: !week.includes(ctNowStr.date),
    macroCovered,
    byDay,
    earnings: earn.ok
      ? { available: true, stale: earn.stale === true, source: earn.source,
          totalReports: earn.totalReports, mega: earn.mega, large: earn.large }
      : { available: false, reason: earn.reason },
    // Releases split by how they land relative to the session you are planning.
    beforeOpen: upcoming.filter((e) => e.window === 'pre-open'),
    inSession: upcoming.filter((e) => e.window === 'in-session'),
    afterClose: upcoming.filter((e) => e.window === 'after-close'),
    highImpact: upcoming.filter((e) => e.impact === 'High'),
    news: news.ok
      ? { available: true, stale: news.stale === true, note: news.note ?? null,
          items: news.items.slice(0, 12).map((n) => ({ ...n, ageMinutes: Math.round((now - n.epoch) / 60000) })) }
      : { available: false, reason: news.reason },
    notCovered: [
      'Fed speakers outside the scheduled calendar, and anything unscheduled.',
      'Earnings timing is the company\'s stated session (before the open / after the close), '
      + 'not a clock time — issuers move it and the calendar is not always updated. '
      + 'Nasdaq reports many names with no session at all; those show as "time not stated".',
      ...(macroCovered ? [] : ['The macro feed has not published this week yet — it rolls over on Sunday. '
        + 'The empty days below mean "unknown", not "nothing scheduled".']),
    ],
  };
}

async function buildCalendar() {
  const r = await fetchEvents();
  if (!r.ok) return { available: false, reason: r.reason };

  const now = Date.now();
  const upcoming = r.events.filter((e) => e.epoch > now);
  const high = upcoming.filter((e) => e.impact === 'High');

  return {
    available: true,
    source: 'ForexFactory (faireconomy weekly feed)',
    coverage: 'current week only — no next-week endpoint exists',
    financialJuice: 'FinancialJuice publishes no public calendar API, so it is not wired in.',
    stale: r.stale === true,
    ageMinutes: r.ageMinutes ?? 0,
    staleNote: r.note ?? null,
    totalInFeed: r.events.length,
    upcomingCount: upcoming.length,
    next: upcoming[0] ?? null,
    nextHigh: high[0] ?? null,
    upcoming: upcoming.slice(0, 8),
    // Loud, not silent: an empty list at the end of the week is a feed limit.
    exhausted: upcoming.length === 0,
    // Full week, not just what is still ahead — the page needs to answer "what
    // is on this week" as well as "what is next".
    events: r.events,
  };
}

// ---------------------------------------------------------------- computation

/**
 * Options-implied expected move for the next session, from VXN.
 * Deliberately kept separate from the Crabel numbers: one is implied and
 * forward-looking, the other realised and backward-looking. Comparing them is
 * the point, so they must not be averaged together.
 */
async function buildExpectedMove(spot) {
  const v = await readYahoo('^VXN', '5y', HISTORY_TTL_MS);
  if (!v.ok) return { available: false, reason: v.reason };

  const latest = v.sessions.at(-1);
  const em = C.expectedMove(spot, latest.close);
  if (!em) return { available: false, reason: 'VXN close unusable' };

  // Absolute vol tells you little without knowing where it sits historically:
  // 22.8 is a different world in a calm year than in a stressed one.
  const history = v.sessions.map((x) => x.close);
  const rank = C.percentileRank(history, latest.close);

  return {
    available: true,
    asOf: latest.date,
    iv: latest.close,
    spot,
    points: em.points,
    pct: em.pct,
    upper: spot + em.points,
    lower: spot - em.points,
    ivPercentile: rank,
    ivSampleYears: 5,
    // A band around the median reads as "middling"; 52nd percentile was being
    // called "above average", which overstates a number sitting on the median.
    ivRegime: rank == null ? null
      : rank < 0.2 ? 'low'
      : rank < 0.4 ? 'below average'
      : rank <= 0.6 ? 'middling'
      : rank <= 0.8 ? 'above average' : 'high',
    note: 'VXN implies Nasdaq-100 index vol, not NQ futures vol — close but not the same instrument',
  };
}

/**
 * Base rates come from QQQ daily bars -- already RTH sessions, and deep enough
 * to support cohort statistics that NQ=F's 60-day intraday cap cannot. The panel
 * carries its own source label; this is never merged with the live NQ numbers.
 */
async function buildStats(liveFlags, nr7Streak) {
  const deep = await getDeepRth();

  // Deep intraday-derived RTH is the preferred sample. QQQ daily remains the
  // documented fallback so a key problem degrades the statistics rather than
  // removing them — and the panel always names which one it used.
  let s, sourceLabel, proxy, proxyNote, sampleKind;
  if (deep.ok && deep.sessions.length > 500) {
    s = deep.sessions;
    sourceLabel = deep.source + (deep.stale ? ` (cached ${deep.ageHours}h)` : '');
    proxy = 'NAS100/USD';
    sampleKind = 'intraday';
    proxyNote =
      'Sessions built from 5-minute bars of the Nasdaq-100 index, so each one is a real ' +
      '08:30–15:00 CT session rather than a daily bar. It stands in for NQ in these statistics ' +
      'only — never in the live numbers above. Measured against NQ=F RTH over 42 overlapping ' +
      'sessions: range% correlation 0.9996, NR7 and NR4 flags agreed 36/36. ' +
      'NQ futures itself is not served by this API (exchange licensing).';
  } else {
    const h = await fetchQqqRth('10y');
    if (!h.ok) return { available: false, reason: `${deep.reason ?? 'deep history unavailable'}; ${h.reason}` };
    s = h.sessions;
    sourceLabel = 'QQQ daily = RTH 08:30–15:00 CT (fallback)';
    proxy = 'QQQ';
    sampleKind = 'daily';
    proxyNote = `Fallback sample — the deeper intraday history was unavailable (${deep.reason ?? 'unknown'}). ` +
      'QQQ daily bars are RTH sessions, but derived from daily bars rather than intraday.';
  }
  const isRth = true;

  // Name the setup we are actually in, most specific first. NR7 splits on
  // whether yesterday was also NR7 — pooling the two quotes a number that
  // belongs to a different setup than the one in front of you.
  let key = 'all', label = 'All sessions';
  if (nr7Streak >= 3) { key = 'nr7Streak3'; label = '3+ consecutive NR7'; }
  else if (liveFlags.idNr4 === true) { key = 'idnr4'; label = 'ID/NR4'; }
  else if (nr7Streak === 2) { key = 'nr7Consecutive'; label = '2nd consecutive NR7'; }
  else if (liveFlags.twoBarNr20 === true) { key = 'twoBarNr20'; label = '2BarNR20'; }
  else if (liveFlags.nr7 === true) { key = 'nr7Isolated'; label = 'isolated NR7'; }
  else if (liveFlags.nr4 === true) { key = 'nr4'; label = 'NR4'; }

  const rate = (k) => C.baseRates(s, C.COHORTS[k]);
  const current = { key, label, ...rate(key) };

  // When the exact setup is too rare to quote, name the closest broader cohort
  // that does have a sample. This is offered as a DIFFERENT cohort, labelled as
  // such — never silently swapped in for the one that was asked for.
  let nearestUsable = null;
  if (current.insufficient) {
    const broader = {
      nr7Streak3: 'nr7Consecutive', nr7Consecutive: 'nr7', nr7Isolated: 'nr7',
      idnr4: 'nr4', twoBarNr20: 'nr4', nr7: 'nr4', nr4: 'all',
    };
    const LABELS = { nr7Consecutive: '2nd consecutive NR7', nr7: 'any NR7', nr4: 'NR4', all: 'All sessions' };
    for (let k = broader[key]; k; k = broader[k]) {
      const r = rate(k);
      if (!r.insufficient) { nearestUsable = { key: k, label: LABELS[k] ?? k, ...r }; break; }
    }
  }

  // Cap the observation list: the UI only lists them when the sample is small
  // enough to read, and a 296-row array is pure payload weight.
  const trim = (r) => ({ ...r, observations: (r.observations ?? []).slice(-12) });

  return {
    available: true,
    source: sourceLabel,
    isRth,
    proxy,
    sampleKind,
    proxyNote,
    openingRates: C.openingRates(s),
    // Meaningful now: on real 6.5-hour RTH sessions the open sits near an extreme
    // often enough that reaching open +/- Stretch is a real event, unlike the
    // 23-hour Globex bar where it happened ~99% of the time regardless of setup.
    triggerRateMeaningful: true,
    triggerRateNote:
      'Bracket-touch rates are computed on real RTH sessions and carry signal — ' +
      'unlike the 23-hour Globex bars this replaced, where every cohort read ~99%.',
    sampleSessions: s.length,
    from: s[0].date,
    to: s.at(-1).date,
    current: trim(current),
    nearestUsable: nearestUsable ? trim(nearestUsable) : null,
    baseline: { key: 'all', label: 'All sessions', ...trim(rate('all')) },
    cohorts: [
      { key: 'nr7', label: 'NR7 (all, pooled)' },
      { key: 'nr7Isolated', label: '— isolated NR7' },
      { key: 'nr7Consecutive', label: '— 2nd+ consecutive NR7' },
      { key: 'nr4', label: 'NR4' },
      { key: 'idnr4', label: 'ID/NR4' },
      { key: 'twoBarNr20', label: '2BarNR20' },
      { key: 'nr7Streak3', label: '3+ consecutive NR7' },
    ].map((c) => ({ ...c, ...trim(rate(c.key)) })),
  };
}

/**
 * Project the next session's range from the setup's historical distribution.
 * Quoted as a p25-p75 band around the median, not a single number: the mean
 * multiple is skewed by a few violent expansions and on its own overstates the
 * typical case.
 */
function buildProjection(payload) {
  const st = payload.stats;
  if (!st?.available) return { available: false, reason: 'no base rates' };

  const q = st.current.insufficient ? st.nearestUsable : st.current;
  if (!q?.medianRangeMultiple) return { available: false, reason: 'no distribution for this setup' };

  const base = payload.last.range;
  return {
    available: true,
    cohort: q.label,
    n: q.n,
    basedOnRange: base,
    median: base * q.medianRangeMultiple,
    p25: base * q.p25RangeMultiple,
    p75: base * q.p75RangeMultiple,
    medianMultiple: q.medianRangeMultiple,
    meanMultiple: q.avgRangeMultiple,
  };
}

function buildPayload(sessions, source, meta, current = null) {
  const last = sessions.length - 1;
  const read = C.compressionRead(sessions);

  // Stretch that applies to the NEXT session, from the 10 completed behind it.
  const nextStretch = C.stretch(sessions, sessions.length);

  const rows = sessions.slice(-20).map((s, i, arr) => {
    const gi = sessions.length - arr.length + i;
    const f = C.evaluate(sessions, gi);
    return {
      date: s.date,
      open: s.open,
      high: s.high,
      low: s.low,
      close: s.close,
      range: C.range(s),
      stretch: C.stretch(sessions, gi),
      ...f,
    };
  });

  return {
    generatedUtc: new Date().toISOString(),
    source,
    meta,
    sessionCount: sessions.length,
    verdict: read.verdict,
    nr7Streak: read.nr7Streak,
    last: {
      date: sessions[last].date,
      open: sessions[last].open,
      high: sessions[last].high,
      low: sessions[last].low,
      close: sessions[last].close,
      range: C.range(sessions[last]),
      ...read.flags,
    },
    // Stretch is null when history is short; the UI must render that as "not
    // enough data", never as a number.
    nextStretch,
    // Today's session while it is running: its open anchors the real bracket,
    // but its half-formed range is kept out of every pattern calculation.
    currentSession: current
      ? {
          date: current.date, open: current.open, high: current.high,
          low: current.low, last: current.close, bars: current.bars, lastBar: current.lastBar,
        }
      : null,

    orb:
      nextStretch == null
        ? { available: false, reason: `needs ${C.STRETCH_LOOKBACK} sessions, have ${sessions.length}` }
        : {
            available: true,
            // Crabel brackets the session's OWN 9:30 open. Once that open exists
            // it is used; before it does, the last RTH close stands in and says so.
            live: current != null,
            reference: current ? current.open : sessions[last].close,
            referenceLabel: current
              ? `today's 8:30 CT open (${current.date})`
              : 'last RTH close — provisional, rebracket off the 8:30 CT open',
            stretch: nextStretch,
            buyStop: (current ? current.open : sessions[last].close) + nextStretch,
            sellStop: (current ? current.open : sessions[last].close) - nextStretch,
          },
    rows,
  };
}

/**
 * Gamma levels around the bracket, plus the side-versus-side read.
 *
 * The question worth answering is not "where are the levels" but "which way has
 * room". A breakout with 60 points to a gamma wall is a different trade from one
 * with 250 points of clear ground, and that asymmetry is what this computes.
 */
function buildGamma(g, payload) {
  if (!g?.ok) return { available: false, reason: g?.reason ?? 'levels unavailable' };

  const orb = payload.orb;
  if (!orb?.available || payload.projection?.available !== true) {
    return { available: false, reason: 'no bracket or projection to position levels against' };
  }

  // Two corrections, both of which previously let bad levels into the numbers:
  //
  // 1. Expired 0DTE levels were excluded from the price map but NOT from the
  //    clear-air and target figures, so the panel claimed an exclusion it was
  //    not making. They are dropped here, once, for everything downstream.
  //
  // 2. gamma_levels (end-of-day) and gamma_levels_intraday are two different
  //    computations of the same names. Merging them produced GEX 2 at both
  //    29,800 and 29,500 with no way to tell which was which. The pre-open plan
  //    uses the settled end-of-day set; intraday is carried separately.
  const usable = g.levels.filter((l) => !l.staleFor0dte && l.kind !== 'intraday');
  const intraday = g.levels.filter((l) => l.kind === 'intraday' && !l.staleFor0dte);

  const ctx = bracketContext(usable, {
    buyStop: orb.buyStop,
    sellStop: orb.sellStop,
    reference: orb.reference,
    projectedRange: payload.projection.median,
  });
  if (!ctx) return { available: false, reason: 'no levels near the bracket' };

  const air = { long: ctx.airAboveBuy, short: ctx.airBelowSell };
  const proj = payload.projection.median;

  // Which side has more room, and is the difference big enough to mean anything?
  let roomier = null;
  if (air.long != null && air.short != null) {
    const ratio = Math.max(air.long, air.short) / Math.max(1, Math.min(air.long, air.short));
    roomier = ratio < 1.35 ? 'neither'
      : air.long > air.short ? 'long' : 'short';
  }

  const staleCount = g.levels.filter((l) => l.staleFor0dte).length;

  return {
    available: true,
    stale: g.stale === true,
    staleReason: g.staleReason ?? null,
    fetchedUtc: g.fetchedUtc,
    ticker: g.tickerMq ?? g.ticker,
    levelCount: g.levels.length,
    usableCount: usable.length,
    intradayCount: intraday.length,
    stale0dteCount: staleCount,
    volBandCount: usable.filter((l) => l.family === 'vol-band').length,
    // Vintages present, so the panel can say which day each set was computed for.
    vintages: [...new Set(usable.map((l) => `${String(l.date).slice(0, 10)} ${l.kind}`))].sort(),
    dates: [...new Set(g.levels.map((l) => String(l.date).slice(0, 10)))].sort(),
    inside: ctx.inside,
    above: ctx.above.slice(0, 6),
    below: ctx.below.slice(0, 6),
    airLong: air.long,
    airShort: air.short,
    firstAboveBuy: ctx.firstAboveBuy,
    firstBelowSell: ctx.firstBelowSell,
    // Clear air as a share of the range the session is projected to cover.
    airLongPctOfProjection: air.long != null ? air.long / proj : null,
    airShortPctOfProjection: air.short != null ? air.short / proj : null,
    roomier,
    // The same set the figures above are computed from, so map and numbers agree.
    map: usable.map((l) => ({
      name: l.name, value: l.value, type: l.type, is0dte: l.is0dte,
      family: l.family, kind: l.kind, date: String(l.date).slice(0, 10),
    })),
  };
}

/** Attach expected move and base rates to a computed payload. */
async function enrich(payload) {
  if (payload.error) return payload;

  const spot = payload.last.close;
  const [em, stats, calendar, gamma, watch, oi] = await Promise.all([
    buildExpectedMove(spot),
    buildStats(payload.last, payload.nr7Streak),
    buildCalendar(),
    getLevels('NQ'),
    buildWatch(),
    getOpenInterest(),
  ]);
  payload.watch = watch;
  // Open interest we computed ourselves, next to levels we were handed. Kept as
  // a separate field on purpose — where the two agree is the interesting part,
  // and averaging them would destroy exactly that signal.
  payload.openInterest = oi;

  // The expiry calendar is rule-derived and always available; the effect beside
  // it is measured against the same deep set as every other cohort here, so a
  // date on the calendar never implies an edge it has not earned.
  const deep = await getDeepRth();
  payload.expiry = {
    calendar: expiryCalendar(new Date()),
    effect: deep.ok ? measureExpiryEffect(deep.sessions) : { available: false, reason: 'deep history unavailable' },
  };

  payload.expectedMove = em;
  payload.stats = stats;
  payload.calendar = calendar;

  // Whether either bracket has actually been reached, for the alert layer.
  const cur = payload.currentSession;
  payload.touch = cur && payload.orb?.available
    ? {
        buy: cur.high >= payload.orb.buyStop,
        sell: cur.low <= payload.orb.sellStop,
        sessionDate: cur.date,
        asOf: cur.lastBar,
      }
    : null;
  payload.projection = buildProjection(payload);

  // Gamma levels positioned against the bracket. Must come after the projection,
  // which defines how far out levels are still relevant. Kept separate from the
  // Crabel numbers throughout: one is options positioning, the other realised
  // range — different questions, never blended into a single figure.
  payload.gamma = buildGamma(gamma, payload);

  // The headline comparison: a realised-vol trigger distance against an
  // implied-vol expectation for the same session.
  payload.stretchVsEm =
    em.available && payload.nextStretch != null
      ? { ratio: payload.nextStretch / em.points, stretch: payload.nextStretch, em: em.points }
      : null;

  return payload;
}

async function getData() {
  const nq = await fetchNqRth();
  if (!nq.ok) {
    // No silent downgrade to Globex bars. A wrong Stretch is worse than none.
    return { error: `RTH data unavailable — ${nq.reason}` };
  }

  return enrich(buildPayload(nq.sessions, 'RTH', {
    label: 'RTH 08:30–15:00 CT · NQ=F 5-minute bars',
    tradable: true,
    instrument: 'NQ=F',
    timeFrame: '5m aggregated to RTH sessions',
    rejectedSessions: nq.rejected,
    barsPerSession: nq.sessions.at(-1)?.bars ?? null,
    sessionInProgress: nq.current != null,
    currentGranularity: nq.currentGranularity ?? null,
    zones: nq.zones,
    band: nq.band,
    auction: nq.auction,
    edge: EDGE,
    costPts: COST_PTS,
  }, nq.current));
}

// ------------------------------------------------------------------ pdf

/**
 * Render the Session brief to PDF using the copy of Edge already on this
 * machine. Printing our own page means the PDF can never drift from what the
 * dashboard shows — there is no second template to keep in sync.
 */
/**
 * The trading session this brief is for: today if RTH is running or still to
 * come, otherwise the next weekday. Holidays are not known here, so this is the
 * next *weekday* session — stated rather than implied.
 */
async function nextSessionDate() {
  const now = new Date();
  const et = new Date(now.toLocaleString('en-US', { timeZone: 'America/New_York' }));
  const mins = et.getHours() * 60 + et.getMinutes();
  if (mins >= toMinutes(RTH_END)) et.setDate(et.getDate() + 1);
  while (et.getDay() === 0 || et.getDay() === 6) et.setDate(et.getDate() + 1);
  return `${et.getFullYear()}-${String(et.getMonth() + 1).padStart(2, '0')}-${String(et.getDate()).padStart(2, '0')}`;
}

async function renderBriefPdf() {
  const { execFile } = await import('node:child_process');
  const { tmpdir } = await import('node:os');
  const { promisify } = await import('node:util');
  const run = promisify(execFile);

  const candidates = [
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  ];
  let exe = null;
  for (const c of candidates) {
    try { await stat(c); exe = c; break; } catch { /* try the next one */ }
  }
  if (!exe) throw new Error('Microsoft Edge not found; cannot render a PDF');

  const out = join(tmpdir(), `oceans-crabel-brief-${Date.now()}.pdf`);
  const url = `http://127.0.0.1:${PORT}/?print=1#brief`;

  await run(exe, [
    '--headless', '--disable-gpu', '--no-first-run',
    '--virtual-time-budget=20000',
    '--no-pdf-header-footer',
    `--print-to-pdf=${out}`,
    url,
  ], { timeout: 90_000 });

  const buf = await readFile(out);
  // Best effort: a leftover temp file is not worth failing the download over.
  try { (await import('node:fs/promises')).unlink(out); } catch { /* ignore */ }
  return buf;
}

// ---------------------------------------------------------------- http

const server = createServer(async (req, res) => {
  try {
    if (req.url.startsWith('/brief.pdf')) {
      try {
        const pdf = await renderBriefPdf();
        // Name the file for the session it plans, not the day it was generated.
        // A brief written on Sunday is for Monday; "2026-08-09" named a day with
        // no RTH session at all.
        const stamp = await nextSessionDate();
        res.writeHead(200, {
          'content-type': 'application/pdf',
          'content-disposition': `attachment; filename="oceans-crabel-brief-${stamp}.pdf"`,
          'cache-control': 'no-store',
        });
        return res.end(pdf);
      } catch (e) {
        res.writeHead(500, { 'content-type': 'text/plain' });
        return res.end(`PDF render failed: ${e.message}`);
      }
    }

    if (req.url === '/api/data') {
      const data = await getData();
      try { data.buildTime = String(Math.floor((await stat(join(here, 'index.html'))).mtimeMs)); } catch { /* optional */ }
      res.writeHead(200, {
        'content-type': 'application/json',
        'cache-control': 'no-store',
      });
      return res.end(JSON.stringify(data));
    }

    // Stamp the page with the build it was served from. The client compares this
    // against the live value in /api/data, so a tab left open across an edit can
    // say so instead of quietly showing an old dashboard.
    const file = join(here, 'index.html');
    const html = await readFile(file, 'utf8');
    const build = String(Math.floor((await stat(file)).mtimeMs));
    if (!html.includes('</head>')) console.warn('index.html has no </head> — build stamp not injected');
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
    res.end(html.replace('</head>', `<script>window.__BUILD__=${JSON.stringify(build)}</script></head>`));
  } catch (e) {
    res.writeHead(500, { 'content-type': 'text/plain' });
    res.end(String(e));
  }
});

/**
 * Pull CBOE open interest once per calendar day, whether or not anyone opens the
 * page. getOpenInterest is a no-op when today's snapshot already exists, so the
 * hourly tick is a cheap way to catch the date rolling over and to retry a day
 * whose first attempt failed. Kept unref'd so it never holds the process open.
 */
function scheduleDailyOi() {
  const tick = () => getOpenInterest().then(
    (r) => { if (r.available && !r.fromCache) console.log(`CBOE open interest: snapshot written for ${r.date}`); },
    (e) => console.log(`CBOE open interest: ${e.message}`),
  );
  tick();
  setInterval(tick, 3600_000).unref();
}

server.listen(PORT, '127.0.0.1', async () => {
  console.log(`Ocean's Crabel dashboard  ->  http://127.0.0.1:${PORT}`);
  console.log('Live read : NQ=F 5m -> RTH 09:30-16:00 ET sessions (08:30-15:00 CT, real NQ points)');
  console.log('Base rates: QQQ daily = RTH sessions, 10y');
  console.log(`CBOE OI   : ${(await snapshotDates()).length} daily snapshot(s) on disk`);
  scheduleDailyOi();
});
