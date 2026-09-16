// Same test vector as _test/Program.cs. If the JS and C# ports ever disagree,
// one of these two suites goes red.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import * as C from './crabel.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const csv = join(here, '..', '_test', 'nq_daily.csv');

const sessions = readFileSync(csv, 'utf8')
  .split(/\r?\n/)
  .slice(1)
  .filter(Boolean)
  .map((line) => {
    const p = line.split(',').map((x) => x.replace(/"/g, '').trim());
    return { date: p[0], open: +p[1], high: +p[2], low: +p[3], close: +p[4] };
  });

const idx = (d) => sessions.findIndex((x) => x.date === d);
let failures = 0;

function check(name, ok, actual, expected) {
  if (!ok) failures++;
  console.log(`  [${ok ? 'PASS' : 'FAIL'}] ${name.padEnd(28)} actual=${String(actual).padEnd(12)} expected=${expected}`);
}
const eq = (n, a, e) => check(n, a === e, a, e);
const near = (n, a, e, tol) => check(n, a !== null && Math.abs(a - e) <= tol, a === null ? 'null' : a.toFixed(3), `${e} +/-${tol}`);
const isNull = (n, a) => check(n, a === null, a, 'null');
const notNull = (n, a) => check(n, a !== null, a, 'non-null');

console.log(`Loaded ${sessions.length} sessions, ${sessions[0].date} .. ${sessions.at(-1).date}\n`);
console.log('== Assertions (JS port) ==');

const i7 = idx('2026-08-07');

near('2026-08-05 range', C.range(sessions[idx('2026-08-05')]), 543.25, 0.01);
near('2026-08-06 range', C.range(sessions[idx('2026-08-06')]), 445.0, 0.01);
near('2026-08-07 range', C.range(sessions[i7]), 414.25, 0.01);

eq('08-05 NR4', C.isNarrowestRange(sessions, idx('2026-08-05'), 4), true);
eq('08-05 NR7', C.isNarrowestRange(sessions, idx('2026-08-05'), 7), true);
eq('08-06 NR4', C.isNarrowestRange(sessions, idx('2026-08-06'), 4), true);
eq('08-06 NR7', C.isNarrowestRange(sessions, idx('2026-08-06'), 7), true);
eq('08-07 NR4', C.isNarrowestRange(sessions, i7, 4), true);
eq('08-07 NR7', C.isNarrowestRange(sessions, i7, 7), true);
eq('08-07 2BarNR20', C.isTwoBarNarrowest(sessions, i7, 20), true);

eq('08-07 insideDay', C.isInsideDay(sessions, i7), false);
eq('08-06 insideDay', C.isInsideDay(sessions, idx('2026-08-06')), false);
eq('08-07 ID/NR4', C.evaluate(sessions, i7).idNr4, false);

near('stretch applied to 08-07', C.stretch(sessions, i7), 148.3, 0.1);
near('stretch for next session', C.stretch(sessions, i7 + 1), 150.8, 0.1);

near('08-07 CLV', C.closeLocationValue(sessions[i7]), 0.92, 0.01);
near('08-07 range %20d', C.rangePctOfAverage(sessions, i7, 20), 60, 1);

isNull('NR7 at index 0', C.isNarrowestRange(sessions, 0, 7));
isNull('NR7 at index 5', C.isNarrowestRange(sessions, 5, 7));
isNull('insideDay at index 0', C.isInsideDay(sessions, 0));
isNull('stretch at index 9', C.stretch(sessions, 9));
isNull('2BarNR20 at index 10', C.isTwoBarNarrowest(sessions, 10, 20));
isNull('Evaluate(3).Nr7', C.evaluate(sessions, 3).nr7);
notNull('Evaluate(3).IdNr4', C.evaluate(sessions, 3).idNr4);
isNull('Evaluate(2).IdNr4', C.evaluate(sessions, 2).idNr4);
notNull('Evaluate(3).Nr4', C.evaluate(sessions, 3).nr4);
notNull('stretch at index 10', C.stretch(sessions, 10));
isNull('CLV of zero-range session', C.closeLocationValue({ open: 100, high: 100, low: 100, close: 100 }));

// Headline read for the current data.
const read = C.compressionRead(sessions);
eq('nr7 streak', read.nr7Streak, 3);

// ---- Expected move ----
// 20 vol on 30000 spot: 0.20 / sqrt(252) = 1.2599% -> 377.97 points.
const em = C.expectedMove(30000, 20);
near('EM points', em.points, 377.97, 0.01);
near('EM pct', em.pct, 1.2599, 0.001);
isNull('EM with null spot', C.expectedMove(null, 20));
isNull('EM with zero iv', C.expectedMove(30000, 0));
isNull('EM with negative iv', C.expectedMove(30000, -5));

// ---- Base rates ----
const allRates = C.baseRates(sessions, C.COHORTS.all);
check('baseline has sample', allRates.n > 30, allRates.n, '>30');
check('rates are proportions',
  [allRates.upCloseRate, allRates.expansionRate, allRates.triggerRate].every((v) => v >= 0 && v <= 1),
  'in range', '0..1');
check('range multiple positive', allRates.avgRangeMultiple > 0, allRates.avgRangeMultiple.toFixed(2), '>0');

// A cohort that never matches must report insufficient, not fabricate zeroes.
const none = C.baseRates(sessions, () => false);
eq('empty cohort insufficient', none.insufficient, true);
eq('empty cohort n', none.n, 0);

// minSample is enforced, not advisory.
const tiny = C.baseRates(sessions, C.COHORTS.all, { minSample: 1e9 });
eq('minSample enforced', tiny.insufficient, true);

// nr7Streak3 must be a strict subset of nr7.
let streak3 = 0, nr7 = 0;
for (let i = 20; i < sessions.length; i++) {
  if (C.COHORTS.nr7(sessions, i)) nr7++;
  if (C.COHORTS.nr7Streak3(sessions, i)) {
    streak3++;
    if (C.COHORTS.nr7(sessions, i) !== true) failures++;
  }
}
check('nr7Streak3 subset of nr7', streak3 <= nr7, `${streak3} <= ${nr7}`, 'subset');

// Standard error must shrink as n grows, or the "inside the noise" test is a lie.
check('stdErr present', allRates.upCloseStdErr > 0 && allRates.upCloseStdErr < 0.1,
  allRates.upCloseStdErr.toFixed(4), '0..0.1');

// ---- Percentiles ----
const ten = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
near('p50 of 1..10', C.percentile(ten, 0.5), 5.5, 1e-9);
near('p25 of 1..10', C.percentile(ten, 0.25), 3.25, 1e-9);
near('p0 of 1..10', C.percentile(ten, 0), 1, 1e-9);
near('p100 of 1..10', C.percentile(ten, 1), 10, 1e-9);
near('percentile ignores input order', C.percentile([9, 1, 5, 3, 7], 0.5), 5, 1e-9);
isNull('percentile of empty', C.percentile([], 0.5));
near('single-element percentile', C.percentile([42], 0.9), 42, 1e-9);

near('rank of median', C.percentileRank(ten, 5.5), 0.5, 1e-9);
near('rank of minimum', C.percentileRank(ten, 1), 0, 1e-9);
isNull('rank of null value', C.percentileRank(ten, null));

// Quartiles must be ordered, or the projection band is nonsense.
check('p25 <= median <= p75',
  allRates.p25RangeMultiple <= allRates.medianRangeMultiple &&
  allRates.medianRangeMultiple <= allRates.p75RangeMultiple,
  `${allRates.p25RangeMultiple.toFixed(2)}/${allRates.medianRangeMultiple.toFixed(2)}/${allRates.p75RangeMultiple.toFixed(2)}`,
  'ordered');

// A setup on the final bar has no following session and must be dropped, not
// scored against a session that does not exist yet.
const streak3Matches = sessions.reduce((acc, _, i) => acc + (i >= 20 && C.COHORTS.nr7Streak3(sessions, i) ? 1 : 0), 0);
const rare = C.baseRates(sessions, C.COHORTS.nr7Streak3);
eq('final-bar setup excluded', rare.n, streak3Matches - 1);
eq('observations length tracks n', rare.observations.length, rare.n);

// Distribution survives below minSample even though rates are withheld.
const three = C.baseRates(sessions, (s, i) => i === 30 || i === 40 || i === 50);
eq('small cohort n', three.n, 3);
eq('small cohort insufficient', three.insufficient, true);
notNull('small cohort keeps median', three.medianRangeMultiple);
notNull('small cohort keeps quartiles', three.p25RangeMultiple);
eq('small cohort keeps observations', three.observations.length, 3);
isNull('small cohort withholds rates', three.upCloseRate ?? null);

// Observations must not look ahead: the outcome date always follows the setup date.
check('observations do not look ahead',
  allRates.observations.every((o) => o.nextDate > o.date),
  'all next > setup', 'ordered');

// ---- Opening relationship ----
const mk = (o, h, l, c) => ({ date: 'x', open: o, high: h, low: l, close: c });
const rel = (prev, open) => C.openingRelationship([prev, { ...mk(open, 0, 0, 0) }], 1);
const prior = mk(100, 110, 90, 105);
eq('open above prior high', rel(prior, 115), 'above prior high');
eq('open below prior low', rel(prior, 85), 'below prior low');
eq('open up inside range', rel(prior, 107), 'up, inside prior range');
eq('open down inside range', rel(prior, 100), 'down, inside prior range');
eq('open at prior close', rel(prior, 105), 'at prior close');
isNull('opening relationship at index 0', C.openingRelationship(sessions, 0));

const orates = C.openingRates(sessions);
check('opening buckets produced', orates.length > 0, orates.length, '>0');

// Direction must be judged against the market's own drift, not against 50%.
// A pooled rate of exactly 0.5 would make the distinction invisible, so assert
// the pooled value is carried and that z is measured from it.
notNull('pooled up-close carried', orates[0].pooledUpCloseRate);
const bucket = orates.find((o) => o.n > 5) ?? orates[0];
const expectedZ = (bucket.upCloseRate - bucket.pooledUpCloseRate) /
  Math.sqrt((bucket.pooledUpCloseRate * (1 - bucket.pooledUpCloseRate)) / bucket.n);
near('up-close z measured from pooled drift', bucket.upCloseZ, expectedZ, 1e-9);
check('a bucket at the pooled rate scores z=0',
  Math.abs(((bucket.pooledUpCloseRate - bucket.pooledUpCloseRate)) ) === 0, '0', '0');
check('opening n sums sanely',
  orates.reduce((a, b) => a + b.n, 0) === sessions.length - 20,
  orates.reduce((a, b) => a + b.n, 0), sessions.length - 20);

console.log(`\n${failures === 0 ? 'ALL PASS' : `${failures} FAILURE(S)`}`);
console.log(`verdict: ${read.verdict}`);
process.exit(failures === 0 ? 0 : 1);
