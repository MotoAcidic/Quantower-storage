// Nasdaq-100 earnings for the current week.
//
// Nasdaq's own calendar endpoint is keyless and server-reachable, which the
// Yahoo and Alpha Vantage routes were not. It returns every US report for a
// given date; this filters to the names that actually move NQ.
//
// Weighting matters more than count here: NVDA reporting is a different event
// from the 200 other companies reporting the same evening.

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const CACHE = join(here, 'cache', 'earnings-week.json');
const TTL_MS = 6 * 3600_000;

// Tiered by index weight. A "mega" report can move NQ on its own; "large" needs
// company-specific surprise to matter at the index level.
const MEGA = ['NVDA', 'AAPL', 'MSFT', 'AMZN', 'AVGO', 'META', 'GOOGL', 'GOOG', 'TSLA'];
const LARGE = ['NFLX', 'COST', 'AMD', 'PEP', 'ADBE', 'CSCO', 'QCOM', 'TXN', 'AMAT',
  'INTU', 'ISRG', 'BKNG', 'MU', 'LRCX', 'ADI', 'PANW', 'KLAC', 'SNPS', 'CDNS',
  'CRWD', 'MRVL', 'ASML', 'LIN', 'ABNB', 'ADP', 'SBUX', 'GILD', 'MDLZ', 'REGN',
  'VRTX', 'PYPL', 'INTC', 'ORLY', 'CMCSA', 'HON', 'AMGN', 'TMUS'];

let memo = null;

const ymd = (x) => `${x.getFullYear()}-${String(x.getMonth() + 1).padStart(2, '0')}-${String(x.getDate()).padStart(2, '0')}`;

/**
 * Monday..Friday of the *trading* week, as YYYY-MM-DD in local (Houston) time.
 *
 * Not the ISO week. On a Saturday or Sunday the week that contains "today" is
 * the one that already closed; what a trader is preparing for is the one that
 * opens Monday, so the weekend rolls forward. Mon-Fri it is the current week.
 *
 * Built from getFullYear/getMonth/getDate, never toISOString — a Houston
 * evening is already tomorrow in UTC, and that skew would shift the whole week.
 */
export function weekDates(from = new Date()) {
  const d = new Date(from);
  const dow = d.getDay();
  const monday = new Date(d);
  if (dow === 0) monday.setDate(d.getDate() + 1);        // Sunday  -> tomorrow
  else if (dow === 6) monday.setDate(d.getDate() + 2);   // Saturday -> Monday
  else monday.setDate(d.getDate() - ((dow + 6) % 7));    // weekday -> this Monday
  return Array.from({ length: 5 }, (_, i) => {
    const x = new Date(monday);
    x.setDate(monday.getDate() + i);
    return ymd(x);
  });
}

async function fetchDay(date) {
  const r = await fetch(`https://api.nasdaq.com/api/calendar/earnings?date=${date}`, {
    headers: {
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
      Accept: 'application/json, text/plain, */*',
    },
  });
  if (!r.ok) throw new Error(`HTTP ${r.status}`);
  const j = await r.json();
  return (j?.data?.rows ?? []).map((x) => ({
    symbol: (x.symbol ?? '').trim().toUpperCase(),
    name: x.name ?? '',
    // Nasdaq encodes this as time-pre-market / time-after-hours / time-not-supplied.
    // The third is common and genuinely means the issuer has not said, so it is
    // surfaced as unknown rather than quietly defaulted to one side.
    when: /pre-market/i.test(x.time ?? '') ? 'before the open'
      : /after-hours/i.test(x.time ?? '') ? 'after the close' : 'time not stated',
    date,
  }));
}

/**
 * The week's NDX-relevant reports. Returns tiered lists plus the raw count, so
 * the page can say "3 of 847 reports matter to you" rather than dumping a wall.
 */
export async function getWeekEarnings(now = new Date()) {
  if (memo && Date.now() - memo.at < TTL_MS) return memo.value;

  const dates = weekDates(now);
  try {
    const disk = JSON.parse(await readFile(CACHE, 'utf8'));
    if (Date.now() - disk.at < TTL_MS && disk.week === dates[0]) {
      memo = { at: disk.at, value: { ok: true, ...disk } };
      return memo.value;
    }
  } catch { /* no cache */ }

  try {
    const days = await Promise.all(dates.map(async (d) => {
      try { return await fetchDay(d); } catch { return []; }
    }));
    const all = days.flat();
    if (!all.length) throw new Error('no rows returned for any day this week');

    const pick = (list, tier) => all
      .filter((e) => list.includes(e.symbol))
      .map((e) => ({ ...e, tier }));

    const value = {
      at: Date.now(),
      week: dates[0],
      dates,
      totalReports: all.length,
      mega: pick(MEGA, 'mega').sort((a, b) => a.date.localeCompare(b.date)),
      large: pick(LARGE, 'large').sort((a, b) => a.date.localeCompare(b.date)),
      source: 'Nasdaq earnings calendar',
    };
    await mkdir(dirname(CACHE), { recursive: true });
    await writeFile(CACHE, JSON.stringify(value));
    memo = { at: value.at, value: { ok: true, ...value } };
    return memo.value;
  } catch (e) {
    try {
      const disk = JSON.parse(await readFile(CACHE, 'utf8'));
      return { ok: true, ...disk, stale: true, staleReason: e.message };
    } catch {
      return { ok: false, reason: `Nasdaq earnings unavailable: ${e.message}` };
    }
  }
}
