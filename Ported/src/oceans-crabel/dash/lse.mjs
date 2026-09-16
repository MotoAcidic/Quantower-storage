// London Strategic Edge — deep RTH history.
//
// Why this source: /v1/candles serves NAS100/USD (the Nasdaq-100 index itself)
// at 5-minute granularity back to 2016. Historical intraday from this feed is
// RTH-only and lands exactly on 09:30–15:55 ET, 78 bars a session, so real
// 9:30-open sessions fall straight out of it.
//
// Why not NQ futures directly: every futures symbol (NQ.F, ES.F, GC.F, CL.F)
// returns "Symbol not available via this endpoint" — almost certainly exchange
// licensing. NAS100/USD stands in, and it is a closer stand-in than QQQ:
// measured over 42 overlapping sessions against NQ=F RTH, range% correlation
// 0.9996 and NR7/NR4 flags agreed 36/36.
//
// The live read does NOT use this. Today's Stretch and bracket come from NQ=F
// itself so they are quoted in the instrument's own points, with no conversion.

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const CACHE_DIR = join(here, 'cache');
const CACHE_FILE = join(CACHE_DIR, 'nas100-rth.json');

const API = 'https://data-api.londonstrategicedge.com';
const SYMBOL = 'NAS100/USD';
const START = '2016-01-01';

// The full pull is ~253k rows and takes ~20s, so it happens once a day at most.
const CACHE_TTL_MS = 20 * 60 * 60_000;

const RTH_START = '09:30';
const RTH_END = '16:00';
const MIN_BARS = 70;          // a full session is 78; below this is a half-day

let memo = null;

async function readKey() {
  try {
    const raw = await readFile(join(here, 'secrets.local.json'), 'utf8');
    const k = JSON.parse(raw).lseApiKey;
    return k && k.startsWith('lse_') ? k : null;
  } catch {
    return process.env.LSE_API_KEY || null;
  }
}

const etParts = (iso) => {
  const s = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'America/New_York',
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false,
  }).format(new Date(iso));
  const [date, hm] = s.split(', ');
  return { date, hm };
};

/** Collapse 5-minute bars into RTH sessions, dropping anything that is not one. */
function toSessions(bars) {
  const byDay = new Map();
  for (const b of bars) {
    const { date, hm } = etParts(b.timestamp);
    if (hm < RTH_START || hm >= RTH_END) continue;
    if (!byDay.has(date)) byDay.set(date, []);
    byDay.get(date).push({ hm, open: b.open, high: b.high, low: b.low, close: b.close });
  }

  const sessions = [], rejected = [];
  for (const [date, L] of [...byDay.entries()].sort()) {
    if (L[0].hm !== RTH_START) { rejected.push({ date, why: `starts ${L[0].hm}` }); continue; }
    if (L.length < MIN_BARS) { rejected.push({ date, why: `${L.length} bars (half day)` }); continue; }
    sessions.push({
      date,
      open: L[0].open,
      high: Math.max(...L.map((x) => x.high)),
      low: Math.min(...L.map((x) => x.low)),
      close: L.at(-1).close,
      bars: L.length,
      // Kept so the session map can ask WHERE inside the session the extremes formed.
      highAt: L.reduce((a, b) => (b.high > a.high ? b : a)).hm,
      lowAt: L.reduce((a, b) => (b.low < a.low ? b : a)).hm,
      first30Close: (L.find((x) => x.hm === '09:55') ?? L[Math.min(5, L.length - 1)]).close,
    });
  }
  return { sessions, rejected };
}

async function fetchAll(key) {
  const url = `${API}/v1/candles?symbol=${encodeURIComponent(SYMBOL)}` +
    `&start=${START}&end=${new Date().toISOString().slice(0, 10)}&timeframe=5m`;
  const r = await fetch(url, { headers: { 'x-api-key': key, 'User-Agent': 'Mozilla/5.0' } });
  if (!r.ok) throw new Error(`LSE HTTP ${r.status}: ${(await r.text()).slice(0, 160)}`);
  const j = await r.json();
  if (!Array.isArray(j.data) || !j.data.length) throw new Error('LSE returned no candles');
  return j;
}

/**
 * Deep RTH history, cached to disk. Returns { ok, sessions, ... } and never
 * throws into the request path — a stale or missing cache degrades the base
 * rates, it does not take the dashboard down.
 */
export async function getDeepRth({ force = false } = {}) {
  if (memo && !force && Date.now() - memo.at < CACHE_TTL_MS) return memo.value;

  if (!force) {
    try {
      const disk = JSON.parse(await readFile(CACHE_FILE, 'utf8'));
      if (Date.now() - disk.at < CACHE_TTL_MS) {
        memo = { at: disk.at, value: { ok: true, ...disk } };
        return memo.value;
      }
    } catch { /* no usable cache; fall through to a live pull */ }
  }

  const key = await readKey();
  if (!key) {
    const stale = await loadStale();
    return stale ?? { ok: false, reason: 'no LSE API key in secrets.local.json' };
  }

  try {
    const j = await fetchAll(key);
    const { sessions, rejected } = toSessions(j.data);
    const value = {
      at: Date.now(),
      source: 'London Strategic Edge · NAS100/USD 5m → RTH sessions',
      symbol: SYMBOL,
      plan: j.plan ?? null,
      rawRows: j.rows ?? j.data.length,
      sessions,
      rejectedCount: rejected.length,
      from: sessions[0]?.date ?? null,
      to: sessions.at(-1)?.date ?? null,
    };
    await mkdir(CACHE_DIR, { recursive: true });
    await writeFile(CACHE_FILE, JSON.stringify(value));
    memo = { at: value.at, value: { ok: true, ...value } };
    return memo.value;
  } catch (e) {
    // Serving yesterday's history beats serving none; the age is reported.
    const stale = await loadStale(e.message);
    return stale ?? { ok: false, reason: e.message };
  }
}

async function loadStale(why) {
  try {
    const disk = JSON.parse(await readFile(CACHE_FILE, 'utf8'));
    return {
      ok: true, ...disk, stale: true,
      ageHours: Math.round((Date.now() - disk.at) / 3600_000),
      staleReason: why ?? 'refresh skipped',
    };
  } catch {
    return null;
  }
}
