// Fetch FRED series to CSV without an API key.
//
// FRED's chart-download endpoint (fredgraph.csv) is public — it is what the
// "Download CSV" button on any FRED series page uses. `cosd` forces the start
// date back to the beginning; without it FRED returns only its default ~3-year
// chart window, which silently truncates the history.
//
// Python's requests was being connection-reset by their CDN, so fetching happens
// here and the analysis stays in Python over the CSVs on disk.

import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));

const SERIES = [
  'BAMLH0A0HYM2', 'BAMLC0A0CM', 'NFCI', 'T10Y2Y', 'DGS10', 'SOFR', 'VIXCLS',
];

const url = (id) =>
  `https://fred.stlouisfed.org/graph/fredgraph.csv?id=${id}&cosd=1900-01-01&coed=2100-01-01`;

for (const id of SERIES) {
  try {
    const r = await fetch(url(id), { headers: { 'User-Agent': 'Mozilla/5.0' } });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    const text = await r.text();

    const lines = text.trim().split(/\r?\n/);
    if (lines.length < 2) throw new Error('empty response');

    // FRED writes "." for missing observations; drop them so pandas sees clean numbers.
    const header = 'date,value';
    const rows = lines.slice(1)
      .map((l) => l.split(','))
      .filter((p) => p.length >= 2 && p[1] !== '.' && p[1] !== '')
      .map((p) => `${p[0]},${p[1]}`);

    await writeFile(join(HERE, `${id}.csv`), `${header}\n${rows.join('\n')}\n`);
    console.log(`  ${id.padEnd(14)} ${String(rows.length).padStart(7)} rows  ${rows[0]?.split(',')[0]} -> ${rows.at(-1)?.split(',')[0]}`);
  } catch (e) {
    console.log(`  ${id.padEnd(14)} FAILED: ${e.message}`);
  }
}
