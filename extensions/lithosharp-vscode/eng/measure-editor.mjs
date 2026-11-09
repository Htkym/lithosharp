// Drives the real extension client and Release LSP. End-to-end diagnostics
// latency includes debounce, transport and scheduling. It is not parser time.
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { performance } from 'node:perf_hooks';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const clientFile = path.resolve(import.meta.dirname, '../out/src/lspClient.js');
const clientSource = path.resolve(import.meta.dirname, '../src/lspClient.ts');
if (fs.statSync(clientSource).mtimeMs > fs.statSync(clientFile).mtimeMs) {
  throw new Error('Recompile the extension before measuring: the LSP client source is newer than its built output.');
}
const { LspClient } = require('../out/src/lspClient.js');
const { spawnProcess } = require('../out/src/process.js');
const repoRoot = path.resolve(import.meta.dirname, '..', '..', '..');
const options = { out: path.join(repoRoot, '.local', 'performance', 'editor'),
  server: path.join(repoRoot, 'src/LithoSharp.LanguageServer/bin/Release/net10.0/LithoSharp.LanguageServer.dll'),
  worker: path.join(repoRoot, 'src/LithoSharp.Mdx/worker'), samples: 100, warmup: 10,
  soak: 1000, gap: 300, timeout: 60000, languages: 'md,mdx' };
for (let index = 2; index < process.argv.length; index++) {
  const arg = process.argv[index];
  if (index === 2 && !arg.startsWith('--')) { options.out = arg; continue; }
  const name = arg.replace(/^--/, '');
  if (!(name in options) || index + 1 === process.argv.length) throw new Error(`Unknown/incomplete option ${arg}`);
  const value = process.argv[++index];
  options[name] = typeof options[name] === 'number' ? Number(value) : value;
}
for (const name of ['samples', 'warmup', 'soak', 'gap', 'timeout']) {
  if (!Number.isSafeInteger(options[name]) || options[name] < 0 || (name === 'timeout' && options[name] === 0)) throw new Error(`Invalid --${name}`);
}
const languages = options.languages.split(',');
if (!languages.length || languages.some(kind => !['md', 'mdx'].includes(kind))) throw new Error('Use --languages md,mdx');
if (!fs.existsSync(options.server)) throw new Error(`Build the Release language server: ${options.server}`);
if (languages.includes('mdx') && !fs.existsSync(path.join(options.worker, 'node_modules'))) throw new Error('Restore the MDX worker first.');
fs.mkdirSync(options.out, { recursive: true });
for (const name of ['editor-latency.json', 'editor-soak.json']) {
  if (fs.existsSync(path.join(options.out, name))) throw new Error('Use a new output directory; earlier evidence is never overwritten.');
}

const hash = file => fs.existsSync(file) ? createHash('sha256').update(fs.readFileSync(file)).digest('hex') : null;
const command = (file, args) => { try { return execFileSync(file, args, { encoding: 'utf8', windowsHide: true, timeout: 5000 }).trim(); } catch { return null; } };
const identity = {
  serverDllSha256: hash(options.server), clientSha256: hash(clientFile), clientSourceSha256: hash(clientSource),
  harnessSha256: hash(import.meta.filename), sourceCommit: command('git', ['-C', repoRoot, 'rev-parse', 'HEAD']),
  dotnetSdk: command('dotnet', ['--version']), node: process.version,
  workerLockSha256: languages.includes('mdx') ? hash(path.join(options.worker, 'package-lock.json')) : null,
  loadedSyntamark: { status: 'unavailable', reason: 'The LSP protocol does not expose loaded runtime/embedded Source identity. See paired site/Generator provenance; do not infer this from package declarations.' },
};
function rss(pid) {
  if (!Number.isInteger(pid)) return { pid: null, bytes: null, status: 'unavailable' };
  try {
    let bytes;
    if (process.platform === 'win32') {
      const value = execFileSync('powershell', ['-NoProfile', '-Command', `(Get-Process -Id ${pid} -ErrorAction Stop).WorkingSet64`], { encoding: 'utf8', windowsHide: true, timeout: 5000 });
      bytes = Number(value.trim());
    } else bytes = Number(execFileSync('ps', ['-o', 'rss=', '-p', String(pid)], { encoding: 'utf8', timeout: 5000 }).trim()) * 1024;
    return { pid, bytes: Number.isFinite(bytes) && bytes > 0 ? bytes : null, status: Number.isFinite(bytes) && bytes > 0 ? 'measured' : 'unavailable' };
  } catch { return { pid, bytes: null, status: 'unavailable' }; }
}
const clients = [];
const processes = [];
const rounds = [];
const latency = { schemaVersion: '1.1', status: 'running', identity, options,
  machine: { platform: process.platform, arch: process.arch, cpus: os.cpus().length, cpu: os.cpus()[0]?.model },
  reference: 'Monotonic raw milliseconds from didChange to accepted diagnostics; debounce 50ms included. Warmups excluded. Burst shape is sequential edits without an extra gap.', rounds };
const soak = { schemaVersion: '1.1', status: 'not-run', edits: 0, restarts: 0,
  harnessRssBefore: rss(process.pid), serverProcesses: processes,
  workerNodeRss: { status: 'unavailable', bytes: null, reason: 'Worker process RSS is not sampled by this harness.' } };
function save() {
  fs.writeFileSync(path.join(options.out, 'editor-latency.json'), JSON.stringify(latency, null, 2) + '\n');
  fs.writeFileSync(path.join(options.out, 'editor-soak.json'), JSON.stringify(soak, null, 2) + '\n');
}
function summary(samples) {
  const sorted = samples.map(sample => sample.elapsedMilliseconds).sort((a, b) => a - b);
  const p = value => sorted.length ? sorted[Math.min(sorted.length - 1, Math.ceil(value * sorted.length) - 1)] : null;
  return { count: sorted.length, p50: p(.5), p95: p(.95), max: sorted.at(-1) ?? null, unit: 'ms' };
}
function document(kind, size) {
  const header = '---\ntitle: Soak\n---\n# Soak Title\n\n' + (kind === 'mdx' ? 'import Counter from "./Counter.jsx";\n\n<Counter />\n\n' : '');
  const body = 'Paragraph with stable text and a [link](./other.md).\n\n';
  return (header + body.repeat(Math.ceil(size / body.length))).slice(0, size);
}
async function growth(sink, uri, before) {
  const start = performance.now();
  while (!sink.sets.some(entry => entry.uri === uri && entry.seq > before)) {
    if (performance.now() - start > options.timeout) { const error = new Error(`Timed out waiting for diagnostics: ${uri}`); error.code = 'TIMEOUT'; throw error; }
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  return sink.sets.find(entry => entry.uri === uri && entry.seq > before).at;
}
async function open() {
  const sink = { sets: [], logs: [], seq: 0 };
  let owned;
  const client = new LspClient({ serverCommand: ['dotnet', options.server], cwd: repoRoot,
    spawn: (args, cwd) => {
      const child = spawnProcess(args, cwd);
      const record = { pid: child.pid ?? null, role: 'language-server', status: 'running', rssAtStart: rss(child.pid), rssBeforeStop: null };
      let resolveExit;
      record.exited = new Promise(resolve => { resolveExit = resolve; });
      child.onExit(code => { record.status = 'exited'; record.exitCode = code; resolveExit(); });
      processes.push(record); owned = record; return child;
    }, debounceMs: 50, initializationOptions: { workerDirectory: options.worker }, isTrusted: () => true,
    onLog: line => sink.logs.push(line), onState: state => sink.logs.push(`state: ${state}`),
    onCapabilities: value => { identity.serverInitializeResult = value; } },
  { set: (uri, diagnostics) => sink.sets.push({ uri, count: diagnostics.length, at: performance.now(), seq: ++sink.seq }), delete: () => {}, clear: () => {} });
  clients.push({ client, get owned() { return owned; } });
  let timer;
  try { await Promise.race([client.start(), new Promise((_, reject) => { timer = setTimeout(() => { const error = new Error('LSP initialization timeout'); error.code = 'TIMEOUT'; reject(error); }, options.timeout); })]); }
  finally { clearTimeout(timer); }
  return { client, sink, owned };
}
function stop(opened) { if (opened.owned?.status === 'running') opened.owned.rssBeforeStop = rss(opened.owned.pid); opened.client.stop(); }

save();
try {
  for (const kind of languages) for (const shape of [{ name: 'burst', gap: 0 }, { name: 'spaced', gap: options.gap }]) {
    const opened = await open(); const { client, sink } = opened;
    const uri = `file:///soak/doc.${kind}`; const base = document(kind, 50 * 1024);
    const round = { kind, shape: shape.name, status: 'running', debounceMs: 50, rawSamples: [], summary: null };
    rounds.push(round);
    client.didOpen({ uri, languageId: kind === 'md' ? 'markdown' : 'mdx', version: 1, text: base });
    await growth(sink, uri, 0);
    let version = 1;
    for (let warm = 0; warm < options.warmup; warm++) { const before = sink.seq; client.didChange(uri, ++version, `${base}\n<!-- warm${warm} -->\n`); await growth(sink, uri, before); }
    for (let edit = 0; edit < options.samples; edit++) {
      if (shape.gap) await new Promise(resolve => setTimeout(resolve, shape.gap));
      const before = sink.seq; const sent = performance.now();
      client.didChange(uri, ++version, `${base}\n<!-- edit${edit} -->\n`);
      const published = await growth(sink, uri, before);
      round.rawSamples.push({ edit, version, elapsedMilliseconds: published - sent });
    }
    round.summary = summary(round.rawSamples); round.status = options.samples ? 'completed' : 'not-run';
    client.didClose(uri); stop(opened); save();
  }
  if (options.soak) {
    soak.status = 'running'; let opened = await open();
    const uris = ['file:///root-a/doc.md', 'file:///root-b/doc.md']; const versions = [1, 1]; const base = document('md', 20 * 1024);
    const openRoots = async () => { for (let i = 0; i < 2; i++) { const before = opened.sink.seq; opened.client.didOpen({ uri: uris[i], languageId: 'markdown', version: versions[i], text: base }); await growth(opened.sink, uris[i], before); } };
    await openRoots();
    for (let edit = 0; edit < options.soak; edit++) {
      const i = edit % 2; const before = opened.sink.seq;
      opened.client.didChange(uris[i], ++versions[i], `${base}\n<!-- soak${edit} -->\n`);
      await growth(opened.sink, uris[i], before); soak.edits++;
      if (options.soak > 1 && edit === Math.floor(options.soak / 2)) { stop(opened); opened = await open(); soak.restarts++; await openRoots(); }
    }
    soak.finalVersions = { a: versions[0], b: versions[1] }; soak.status = 'completed'; stop(opened);
  }
  latency.status = options.samples ? 'completed' : 'not-run';
} catch (error) {
  const status = error.code === 'TIMEOUT' ? 'timeout' : 'failed'; latency.status = status; latency.error = error.message;
  for (const round of rounds.filter(round => round.status === 'running')) { round.status = status; round.summary = summary(round.rawSamples); }
  if (soak.status === 'running') { soak.status = status; soak.error = error.message; }
  process.exitCode = 1;
} finally {
  for (const opened of clients) stop(opened);
  await Promise.all(processes.map(async record => {
    let timer;
    try { await Promise.race([record.exited, new Promise(resolve => { timer = setTimeout(resolve, 5000); })]); }
    finally { clearTimeout(timer); delete record.exited; }
    if (record.status !== 'exited') { record.status = 'cleanup-unconfirmed'; process.exitCode = 1; }
  }));
  if (hash(options.server) !== identity.serverDllSha256 || hash(clientFile) !== identity.clientSha256 || hash(clientSource) !== identity.clientSourceSha256) {
    latency.status = 'invalid-source-change'; latency.error = 'Measured server/client changed during capture.'; process.exitCode = 1;
  }
  soak.harnessRssAfter = rss(process.pid); save();
}
// The client may retain a shutdown-request timer after stdio closes. Owned
// children have already been awaited and evidence is synchronously durable.
process.stdout.write(`Saved editor latency/soak evidence in ${options.out}: ${latency.status}\n`, () => process.exit(process.exitCode ?? 0));
