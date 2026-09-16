// MenthorQ gamma levels.
//
// The endpoint is undocumented — MenthorQ publishes no public API. It was
// derived from strings inside MqLevelsIndicatorAtas-1.0.3.dll, and the required
// query parameters came from the service's own Pydantic validation errors.
// It works, but there is no contract: if they change it, this breaks and the
// dashboard must fall back rather than show stale levels as current.
//
//   GET https://api.menthorq.io/getDailyLevels
//   header: x-api-key
//   query : ticker, level_type, platform, user_id
//
// Level types: gamma_levels, gamma_levels_intraday, gamma_scalping,
//              gamma_scalping_intraday, blindspots, swing

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const CACHE = join(here, 'cache', 'menthorq-nq.json');
const EP = 'https://api.menthorq.io/getDailyLevels';

// Levels are computed daily; there is no value in hammering this.
const TTL_MS = 30 * 60_000;

const TYPES = ['gamma_levels', 'gamma_levels_intraday', 'blindspots', 'swing'];

let memo = null;

/**
 * Not every level MenthorQ returns is dealer positioning.
 *
 * 1D Min / 1D Max are the bounds of their 1-Day Expected Move indicator — a
 * volatility band derived from implied vol, not a gamma level. Presenting them
 * as positioning would double-count the expected move the dashboard already
 * computes from VXN, and would let a vol band masquerade as a dealer wall.
 */
function familyOf(name) {
  if (/^1D M(in|ax)$/i.test(name)) return 'vol-band';
  if (/^BL \d+$/i.test(name)) return 'blindspot';
  if (/^(LB|RT) /i.test(name)) return 'swing';
  return 'positioning';   // Gamma Wall, Call Resistance, Put Support, HVL, GEX n
}

async function creds() {
  try {
    const j = JSON.parse(await readFile(join(here, 'secrets.local.json'), 'utf8'));
    return j.menthorq?.apiKey && j.menthorq?.userId ? j.menthorq : null;
  } catch {
    return null;
  }
}

async function one(cfg, ticker, levelType) {
  const qs = new URLSearchParams({
    ticker, level_type: levelType,
    platform: cfg.platform ?? 'atas_semi_automated',
    user_id: String(cfg.userId),
  });
  const r = await fetch(`${EP}?${qs}`, {
    headers: { 'x-api-key': cfg.apiKey, 'User-Agent': 'Mozilla/5.0' },
  });
  if (!r.ok) throw new Error(`${levelType}: HTTP ${r.status}`);
  return r.json();
}

/**
 * All level types for one ticker, flattened.
 *
 * Each level carries the date its block was computed for. 0DTE levels expire
 * with the options they came from, so a Friday 0DTE level is meaningless on
 * Monday — `staleFor0dte` marks that rather than letting it read as live.
 */
export async function getLevels(ticker = 'NQ') {
  if (memo && Date.now() - memo.at < TTL_MS) return memo.value;

  const cfg = await creds();
  if (!cfg) return { ok: false, reason: 'no MenthorQ credentials in secrets.local.json' };

  try {
    const out = [];
    let tickerMq = null;
    for (const t of TYPES) {
      const j = await one(cfg, ticker, t);
      tickerMq = j.ticker_mq ?? tickerMq;
      for (const block of j.levels ?? []) {
        for (const v of block.level_values ?? []) {
          out.push({
            type: t,
            kind: block.kind,
            date: block.date,
            name: v.name,
            value: Number(v.value),
            is0dte: /0DTE/i.test(v.name),
          });
        }
      }
    }
    if (!out.length) throw new Error('no levels returned');

    const today = new Date().toISOString().slice(0, 10);
    const value = {
      ok: true, ticker, tickerMq,
      fetchedUtc: new Date().toISOString(),
      levels: out.map((l) => ({
        ...l,
        staleFor0dte: l.is0dte && !String(l.date).startsWith(today),
        family: familyOf(l.name),
      })),
    };

    await mkdir(dirname(CACHE), { recursive: true });
    await writeFile(CACHE, JSON.stringify(value, null, 1));
    memo = { at: Date.now(), value };
    return value;
  } catch (e) {
    try {
      const disk = JSON.parse(await readFile(CACHE, 'utf8'));
      return { ...disk, stale: true, staleReason: e.message };
    } catch {
      return { ok: false, reason: e.message };
    }
  }
}

/**
 * Position the bracket against the level map: what price meets before either
 * stop fires, and how much clear air lies beyond each one.
 */
export function bracketContext(levels, { buyStop, sellStop, reference, projectedRange }) {
  if (!levels?.length || buyStop == null) return null;

  const near = levels.filter((l) => Math.abs(l.value - reference) <= projectedRange);
  const dedupe = (arr) => {
    const seen = new Map();
    for (const l of arr) {
      const k = l.value.toFixed(2);
      if (!seen.has(k)) seen.set(k, { ...l, names: [l.name], families: [l.family] });
      else {
        const e = seen.get(k);
        if (!e.names.includes(l.name)) e.names.push(l.name);
        if (l.family && !e.families.includes(l.family)) e.families.push(l.family);
      }
    }
    return [...seen.values()];
  };

  const inside = dedupe(near.filter((l) => l.value > sellStop && l.value < buyStop))
    .sort((a, b) => a.value - b.value);
  const above = dedupe(near.filter((l) => l.value >= buyStop)).sort((a, b) => a.value - b.value);
  const below = dedupe(near.filter((l) => l.value <= sellStop)).sort((a, b) => b.value - a.value);

  return {
    inside,
    above,
    below,
    // Clear air: distance from each stop to the first level it would run into.
    airAboveBuy: above.length ? above[0].value - buyStop : null,
    firstAboveBuy: above[0] ?? null,
    airBelowSell: below.length ? sellStop - below[0].value : null,
    firstBelowSell: below[0] ?? null,
  };
}
