// Screenshot a tab via headless Edge, so rendering can be verified rather than assumed.
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { resolve } from 'node:path';

const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const tab = process.argv[2] ?? 'live';
// Headless Edge resolves --screenshot against its own working directory, not
// ours, so an absolute path is the difference between a file and silence.
const out = resolve(process.argv[3] ?? `shot_${tab}.png`);

const profile = await mkdtemp(join(tmpdir(), 'shot-'));
const args = [
  '--headless=new', '--disable-gpu', '--hide-scrollbars',
  `--user-data-dir=${profile}`,
  '--window-size=1400,4200',
  '--virtual-time-budget=15000',
  `--screenshot=${out}`,
  `http://127.0.0.1:7777/#${tab}`,
];

await new Promise((res) => {
  const p = spawn(EDGE, args, { stdio: 'ignore' });
  p.on('exit', res);
  p.on('error', res);
});
await rm(profile, { recursive: true, force: true });
console.log(`wrote ${out}`);
