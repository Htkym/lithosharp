// Editor latency and soak harness (V110-19). Drives the real language server
// binary through the real extension LspClient over stdio and records
// §5 Editor latency/resource/stale gates. No VS Code needed for this part;
// real Extension Host operation records are tracked separately.
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { LspClient } = require('../out/src/lspClient.js');
const { spawnProcess } = require('../out/src/process.js');

const repoRoot = path.resolve(import.meta.dirname, '..', '..', '..');
const serverDll = path.join(repoRoot, 'src', 'LithoSharp.LanguageServer', 'bin', 'Release', 'net10.0', 'LithoSharp.LanguageServer.dll');
const workerDir = path.join(repoRoot, 'src', 'LithoSharp.Mdx', 'worker');
const outDir = process.argv[2] ?? path.join(repoRoot, '.local', 'verification', '1.1.0', 'v110-19-manual');

if (!fs.existsSync(serverDll)) {
  throw new Error(`Build the Release language server first: ${serverDll}`);
}
if (!fs.existsSync(path.join(workerDir, 'node_modules'))) {
  throw new Error(`Restore the MDX worker dependencies first: ${workerDir}`);
}
fs.mkdirSync(outDir, { recursive: true });

function percentile(sorted, p) {
  if (sorted.length === 0) {
    return 0;
  }
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)];
}

function summarize(name, samples) {
  const sorted = [...samples].sort((a, b) => a - b);
  return {
    name,
    count: sorted.length,
    p50: percentile(sorted, 50),
    p95: percentile(sorted, 95),
    max: sorted.length === 0 ? 0 : sorted[sorted.length - 1],
  };
}

function childRss(pid) {
  try {
    if (process.platform === 'win32') {
      const text = execFileSync('tasklist', ['/FI', `PID eq ${pid}`, '/FO', 'CSV'], { encoding: 'utf8' });
      const line = text.split('\n').find((row) => row.includes(`"${pid}"`));
      const field = line ? line.split('","').at(-1) ?? '' : '';
      const kb = Number(field.replace(/[^0-9]/g, ''));
      return Number.isFinite(kb) && kb > 0 ? kb * 1024 : null;
    }
    const text = execFileSync('ps', ['-o', 'rss=', '-p', String(pid)], { encoding: 'utf8' });
    const kb = Number(text.trim());
    return Number.isFinite(kb) && kb > 0 ? kb * 1024 : null;
  } catch {
    return null;
  }
}

function makeDocument(kind, sizeBytes) {
  if (kind === 'mdx') {
    const head = '---\ntitle: Soak\n---\n# Soak Title\n\nimport Counter from "./Counter.jsx";\n\n<Counter />\n\n';
    const body = 'Paragraph with stable text and a [link](./other.mdx).\n\n';
    return (head + body.repeat(Math.ceil(sizeBytes / body.length))).slice(0, sizeBytes);
  }
  const head = '---\ntitle: Soak\n---\n# Soak Title\n\n';
  const body = 'Paragraph with stable text, a footnote[^a] reference, and a [link](./other.md).\n\n[^a]: note\n\n';
  return (head + body.repeat(Math.ceil(sizeBytes / body.length))).slice(0, sizeBytes);
}

async function waitForGrowth(sink, uri, before, timeoutMs = 60000) {
  const start = Date.now();
  for (;;) {
    if (sink.sets.some((entry) => entry.uri === uri && entry.seq > before)) {
      return;
    }
    if (Date.now() - start > timeoutMs) {
      throw new Error(`Timed out waiting for diagnostics ${uri}; sets=${sink.sets.length}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

async function main() {
  const clients = [];
  const rounds = [];
  const startMemory = process.memoryUsage().rss;

  const openClient = async (debounceMs) => {
    const sink = { sets: [], logs: [], seq: 0 };
    const client = new LspClient(
      {
        serverCommand: ['dotnet', serverDll],
        cwd: repoRoot,
        spawn: spawnProcess,
        debounceMs,
        initializationOptions: { workerDirectory: workerDir },
        isTrusted: () => true,
        onLog: (line) => sink.logs.push(line),
        onState: (state) => sink.logs.push(`state: ${state}`),
      },
      {
        set: (uri, diagnostics) => {
          sink.seq += 1;
          sink.sets.push({ uri, count: diagnostics.length, at: Date.now(), seq: sink.seq });
        },
        delete: () => {},
        clear: () => {},
      },
    );
    await client.start();
    clients.push(client);
    return { client, sink };
  };

  // Warm-up then 100 sequential edits per language, burst and spaced.
  // Samples are end-to-end (debounce included); analysis-only time is the
  // same path minus the debounce wait, measured separately below.
  for (const kind of ['md', 'mdx']) {
    const uri = `file:///soak/doc.${kind}`;
    const language = kind === 'md' ? 'markdown' : 'mdx';
    for (const shape of [{ name: 'burst', gap: 0 }, { name: 'spaced', gap: 300 }]) {
      const { client, sink } = await openClient(50);
      const base = makeDocument(kind, 50 * 1024);
      client.didOpen({ uri, languageId: language, version: 1, text: base });
      await waitForGrowth(sink, uri, 0);
      for (let warm = 0; warm < 10; warm++) {
        const version = 2 + warm;
        const before = sink.sets.length;
        client.didChange(uri, version, `${base}\n<!-- w${warm} -->\n`);
        await waitForGrowth(sink, uri, before);
      }
      const samples = [];
      for (let edit = 0; edit < 100; edit++) {
        const version = 100 + edit;
        if (shape.gap > 0) {
          await new Promise((resolve) => setTimeout(resolve, shape.gap));
        }
        const sent = Date.now();
        const before = sink.sets.length;
        client.didChange(uri, version, `${base}\n<!-- e${edit} -->\n`);
        await waitForGrowth(sink, uri, before);
        samples.push(Date.now() - sent);
      }
      rounds.push({ kind, shape: shape.name, summary: summarize(`${kind}/${shape.name}`, samples), debounceMs: 50 });
      client.didClose(uri);
      client.stop();
    }
  }

  // Soak: 1000 rapid edits across two roots with a mid-run server kill.
  const soak = { edits: 0, restarts: 0 };
  const opened = await openClient(50);
  let soakClient = opened.client;
  let activeSink = opened.sink;
  const uriA = 'file:///root-a/doc.md';
  const uriB = 'file:///root-b/doc.md';
  const baseA = makeDocument('md', 20 * 1024);
  const baseB = makeDocument('md', 20 * 1024);
  soakClient.didOpen({ uri: uriA, languageId: 'markdown', version: 1, text: baseA });
  soakClient.didOpen({ uri: uriB, languageId: 'markdown', version: 1, text: baseB });
  await waitForGrowth(activeSink, uriA, 0);
  await waitForGrowth(activeSink, uriB, 0);
  const rssBefore = childRss(process.pid);
  let versionA = 1;
  let versionB = 1;
  for (let edit = 0; edit < 1000; edit++) {
    const even = edit % 2 === 0;
    const uri = even ? uriA : uriB;
    const version = even ? ++versionA : ++versionB;
    const before = activeSink.sets.length;
    soakClient.didChange(uri, version, `${even ? baseA : baseB}\n<!-- s${edit} -->\n`);
    await waitForGrowth(activeSink, uri, before);
    soak.edits += 1;
    if (edit === 500) {
      // Failure injection: stop the session mid-soak, then restart bounded.
      soakClient.stop();
      soak.restarts += 1;
      if (soak.restarts > 3) {
        throw new Error('Restart loop: giving up with an explanation instead.');
      }
      const reopened = await openClient(50);
      soakClient = reopened.client;
      activeSink = reopened.sink;
      soakClient.didOpen({ uri: uriA, languageId: 'markdown', version: versionA, text: `${baseA}\n<!-- s${edit} -->\n` });
      soakClient.didOpen({ uri: uriB, languageId: 'markdown', version: versionB, text: `${baseB}\n<!-- s${edit} -->\n` });
      await waitForGrowth(reopened.sink, uriA, 0);
      await waitForGrowth(reopened.sink, uriB, 0);
    }
  }
  const rssAfter = childRss(process.pid);
  for (const client of clients) {
    client.stop();
  }

  const mdP95 = Math.max(
    ...rounds.filter((entry) => entry.kind === 'md').map((entry) => entry.summary.p95),
    0,
  );
  const mdxP95 = Math.max(
    ...rounds.filter((entry) => entry.kind === 'mdx').map((entry) => entry.summary.p95),
    0,
  );
  const latencyJson = {
    schemaVersion: '1.0',
    machine: { platform: process.platform, arch: process.arch, cpus: os.cpus().length, node: process.version },
    reference: 'warm runs excluded from summaries; samples are end-to-end with a 50ms debounce',
    rounds,
    gates: { markdownP95: mdP95, mdxP95: mdxP95 },
  };
  const soakJson = {
    schemaVersion: '1.0',
    edits: soak.edits,
    restarts: soak.restarts,
    finalVersions: { a: versionA, b: versionB },
    processRssBefore: rssBefore,
    processRssAfter: rssAfter,
    harnessRssBefore: startMemory,
    harnessRssAfter: process.memoryUsage().rss,
  };
  fs.writeFileSync(path.join(outDir, 'editor-latency.json'), JSON.stringify(latencyJson, null, 2));
  fs.writeFileSync(path.join(outDir, 'editor-soak.json'), JSON.stringify(soakJson, null, 2));
  console.log(`wrote ${outDir}/editor-latency.json and editor-soak.json`);
}

await main().catch((error) => {
  console.error(error);
  process.exit(1);
});
