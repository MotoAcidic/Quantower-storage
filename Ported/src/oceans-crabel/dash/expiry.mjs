// The expiry calendar, and what the tape actually does around it.
//
// Every date here is deterministic — derived from exchange rules, not fetched —
// so this module never fails and never goes stale. What it deliberately does not
// do is assert an effect. "OPEX pins the market" is one of the most repeated
// claims in retail trading and one of the least often measured; the numbers that
// go with these dates are computed against the same deep session set everything
// else on this page uses, and where the effect is not there, it says so.
//
// Rules encoded:
//   Monthly OPEX   third Friday, equity and index options.
//   Triple witching third Friday of Mar / Jun / Sep / Dec — index futures, index
//                   options and single-stock options settle together.
//   VIX expiry     the Wednesday 30 days before the *following* month's third
//                   Friday. This is the settlement the VIX formula is defined
//                   against, which is why it does not track equity OPEX.
//   Month end      the last trading day, when index rebalancing concentrates.

const ymd = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

/** The nth occurrence of a weekday in a month. dow: 0=Sun..6=Sat. */
function nthWeekday(year, month, dow, n) {
  const first = new Date(year, month, 1);
  const shift = (dow - first.getDay() + 7) % 7;
  return new Date(year, month, 1 + shift + (n - 1) * 7);
}

/** Third Friday — the standard monthly options expiry. */
export const thirdFriday = (year, month) => nthWeekday(year, month, 5, 3);

/**
 * VIX expiry: 30 days before the third Friday of the following month, which
 * lands on a Wednesday by construction. Encoding the rule rather than a lookup
 * table means this stays correct in years nobody has hardcoded.
 */
export function vixExpiry(year, month) {
  const next = new Date(year, month + 1, 1);
  const tf = thirdFriday(next.getFullYear(), next.getMonth());
  const d = new Date(tf);
  d.setDate(tf.getDate() - 30);
  return d;
}

/** Last weekday of the month — a proxy for the last trading day, holidays aside. */
export function monthEnd(year, month) {
  const d = new Date(year, month + 1, 0);
  while (d.getDay() === 0 || d.getDay() === 6) d.setDate(d.getDate() - 1);
  return d;
}

const QUARTERLY = [2, 5, 8, 11]; // Mar, Jun, Sep, Dec

/**
 * Every expiry-flavoured date in a window around `from`, nearest first, each with
 * the number of sessions until it. Covers this month and the next two so a
 * quarterly always appears even early in a quarter.
 */
export function expiryCalendar(from = new Date()) {
  const today = new Date(from.getFullYear(), from.getMonth(), from.getDate());
  const out = [];

  for (let k = -1; k <= 2; k++) {
    const m = new Date(today.getFullYear(), today.getMonth() + k, 1);
    const y = m.getFullYear(), mo = m.getMonth();
    const tf = thirdFriday(y, mo);

    out.push({
      date: ymd(tf),
      kind: QUARTERLY.includes(mo) ? 'triple-witching' : 'monthly-opex',
      label: QUARTERLY.includes(mo) ? 'Triple witching' : 'Monthly OPEX',
      note: QUARTERLY.includes(mo)
        ? 'Index futures, index options and single-stock options all settle. The largest scheduled '
          + 'open-interest roll of the quarter, and the one day dealer books genuinely reset.'
        : 'Standard monthly equity and index expiry. Open interest that has been pinning strikes all '
          + 'month stops existing after the cash open.',
    });
    out.push({
      date: ymd(vixExpiry(y, mo)),
      kind: 'vix-expiry',
      label: 'VIX expiry',
      note: 'VIX futures and options settle on the SPX opening prints. It is 30 days before *next* '
        + 'month\'s third Friday, so it does not line up with equity OPEX — vol positioning unwinds '
        + 'on its own schedule.',
    });
    out.push({
      date: ymd(monthEnd(y, mo)),
      kind: 'month-end',
      label: 'Month end',
      note: 'Index funds rebalance to the close. Flow is concentrated in the closing auction rather '
        + 'than spread through the session.',
    });
  }

  const seen = new Set();
  return out
    .filter((e) => { const k = `${e.date}|${e.kind}`; if (seen.has(k)) return false; seen.add(k); return true; })
    .map((e) => {
      const [Y, M, D] = e.date.split('-').map(Number);
      const days = Math.round((new Date(Y, M - 1, D) - today) / 86400_000);
      return { ...e, days, past: days < 0, isToday: days === 0 };
    })
    .sort((a, b) => a.date.localeCompare(b.date));
}

/** The expiry classifications that apply to one date. A day can carry several. */
export function tagsFor(iso, cal) {
  return cal.filter((e) => e.date === iso).map((e) => e.kind);
}

/**
 * Does OPEX week actually behave differently?
 *
 * Takes the deep RTH session set and splits it by expiry classification, then
 * reports range and expansion against the same baseline every other cohort on
 * this page is measured against. No claim is made here — the caller compares the
 * numbers and the standard error decides whether anything is there.
 */
export function measureExpiryEffect(sessions) {
  if (!sessions?.length) return { available: false, reason: 'no sessions supplied' };

  // Classify by rule, per session date, so this works over the whole history
  // without needing a calendar fetch for every past year.
  const classify = (iso) => {
    const [Y, M, D] = iso.split('-').map(Number);
    const d = new Date(Y, M - 1, D);
    const tf = thirdFriday(Y, M - 1);
    const isOpex = ymd(tf) === iso;
    const isQuarterly = isOpex && QUARTERLY.includes(M - 1);
    // OPEX week runs Monday to the third Friday inclusive.
    const weekStart = new Date(tf); weekStart.setDate(tf.getDate() - 4);
    const inOpexWeek = d >= weekStart && d <= tf;
    return { isOpex, isQuarterly, inOpexWeek, isVix: ymd(vixExpiry(Y, M - 1)) === iso };
  };

  // The deep set is raw RTH OHLC, so range is computed here rather than assumed
  // present. Each session is expressed as a percentage of the trailing 20-session
  // average range — the same normalisation the rest of the page uses, and the only
  // way to compare a 2016 session against a 2026 one without volatility drowning
  // the comparison. The first 20 sessions have no trailing window and are dropped.
  const ranges = sessions.map((s) => s.high - s.low);
  const withRange = [];
  for (let i = 20; i < sessions.length; i++) {
    let sum = 0;
    for (let k = i - 20; k < i; k++) sum += ranges[k];
    const avg = sum / 20;
    if (!(avg > 0)) continue;
    withRange.push({ date: sessions[i].date, rangePctOf20d: (ranges[i] / avg) * 100 });
  }
  if (withRange.length < 200) return { available: false, reason: `only ${withRange.length} usable sessions` };

  const stat = (list) => {
    if (list.length < 20) return null;
    const rs = list.map((s) => s.rangePctOf20d).sort((a, b) => a - b);
    const median = rs[Math.floor(rs.length / 2)];
    const mean = rs.reduce((a, b) => a + b, 0) / rs.length;
    const sd = Math.sqrt(rs.reduce((a, b) => a + (b - mean) ** 2, 0) / (rs.length - 1));
    const expansion = list.filter((s) => s.rangePctOf20d > 100).length / list.length;
    return { n: list.length, medianRangePct: median, meanRangePct: mean, se: sd / Math.sqrt(list.length), expansion };
  };

  const base = stat(withRange);
  const cohorts = {
    opexDay: stat(withRange.filter((s) => classify(s.date).isOpex)),
    quarterly: stat(withRange.filter((s) => classify(s.date).isQuarterly)),
    opexWeek: stat(withRange.filter((s) => classify(s.date).inOpexWeek)),
    vixExpiry: stat(withRange.filter((s) => classify(s.date).isVix)),
  };

  // Difference in mean range, in standard errors of the cohort. Two sigma is the
  // bar used everywhere else in this tool, and almost nothing clears it.
  const scored = Object.fromEntries(Object.entries(cohorts).map(([k, c]) => [
    k,
    c && { ...c, vsBaseline: c.meanRangePct - base.meanRangePct, z: (c.meanRangePct - base.meanRangePct) / c.se },
  ]));

  return { available: true, baseline: base, cohorts: scored, sessions: withRange.length };
}
