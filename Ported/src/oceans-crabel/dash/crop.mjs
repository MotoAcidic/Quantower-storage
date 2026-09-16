// Crop a region out of a screenshot so a single panel can be inspected at full
// resolution instead of squinting at a 4200px-tall page.
import { spawn } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const [src, x, y, w, h, out] = process.argv.slice(2);
const profile = await mkdtemp(join(tmpdir(), 'crop-'));
const page = join(profile, 'c.html');
const abs = resolve(src).replace(/\\/g, '/');

await writeFile(page, `<body style="margin:0">
<div style="width:${w}px;height:${h}px;overflow:hidden;position:relative">
  <img src="file:///${abs}" style="position:absolute;left:${-x}px;top:${-y}px">
</div></body>`);

await new Promise((res) => {
  const p = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', `--user-data-dir=${profile}`,
    `--window-size=${w},${h}`, '--virtual-time-budget=4000',
    `--screenshot=${resolve(out)}`, `file:///${page.replace(/\\/g, '/')}`,
  ], { stdio: 'ignore' });
  p.on('exit', res); p.on('error', res);
});
await rm(profile, { recursive: true, force: true });
console.log(`wrote ${out}`);
