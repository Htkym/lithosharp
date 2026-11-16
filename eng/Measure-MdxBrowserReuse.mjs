import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdtemp, mkdir, readFile, readdir, realpath, rm, writeFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';

const usage = 'node eng/Measure-MdxBrowserReuse.mjs --compiler <restored-worker/compiler.mjs> --output <new-report.json> [--pages 100]';
if (process.argv.includes('--help')) {console.log(usage); process.exit(0);}
const args = process.argv.slice(2), options = new Map();
for (let index = 0; index < args.length; index += 2) {
  if (!['--compiler', '--output', '--pages'].includes(args[index]) || args[index + 1] === undefined || options.has(args[index]))
    throw new Error(usage);
  options.set(args[index], args[index + 1]);
}
if (!options.has('--compiler') || !options.has('--output')) throw new Error(usage);
const pages = Number(options.get('--pages') ?? 100);
if (!Number.isSafeInteger(pages) || pages < 2) throw new Error('--pages must be an integer of at least two.');
const compilerFile = await realpath(path.resolve(options.get('--compiler')));
const workerDirectory = path.dirname(compilerFile), output = path.resolve(options.get('--output'));
const manifest = JSON.parse(await readFile(path.join(workerDirectory, 'package.json'), 'utf8'));
if (process.versions.node !== manifest.engines.node) throw new Error('Use the worker locked Node version: ' + manifest.engines.node);
const {compileSite, getRetainedCacheMetrics} = await import(pathToFileURL(compilerFile).href);
const digest = value => createHash('sha256').update(value).digest('hex');
const files = ['compiler.mjs', 'worker.mjs', 'package-lock.json', ...(await readdir(path.join(workerDirectory, 'runtime')))
  .filter(name => name.endsWith('.mjs') || name.endsWith('.css')).map(name => 'runtime/' + name)];
const workerInputs = Object.fromEntries(await Promise.all(files.sort().map(async name =>
  [name, digest(await readFile(path.join(workerDirectory, name)))])));
const temporaryRoot = await realpath(os.tmpdir());
const root = await mkdtemp(path.join(temporaryRoot, 'mx19-'));
try {
  const projectRoot = path.join(root, 'input'); await mkdir(projectRoot);
  await writeFile(path.join(projectRoot, 'package.json'), '{"private":true,"type":"module"}');
  await writeFile(path.join(projectRoot, 'shared.js'), `export const label='shared-first';`);
  await writeFile(path.join(projectRoot, 'Local.jsx'), `export default function Local(){return <span>local-import</span>}`);
  await writeFile(path.join(projectRoot, 'overrides.jsx'),
    `import {useState} from 'react';import {usePageContext} from '@lithosharp/runtime';import {label} from './shared.js';
    function Paragraph({children}){const [n,set]=useState(0);const page=usePageContext();return <p data-page={page.title} data-shared={label} onClick={()=>set(n+1)}>{children} {n}</p>}
    export default {p:Paragraph};`);
  const sources = Object.fromEntries(Array.from({length: pages}, (_, index) =>
    [`page-${index}.mdx`, `# Page ${index}\n\nOriginal paragraph.\n\nValue {frontMatter.value}\n`]));
  for (const [file, text] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), text);
  const request = {projectRoot, sources, componentsModule: './overrides.jsx', hydration: 'selective',
    cacheable: true, resolutionFingerprint: 'fixed-initial-file-set', assetBaseUrl: '/product/_mdx', basePath: '/product/',
    timestamp: '2026-01-01T00:00:00Z', linkMap: {},
    pages: Object.keys(sources).map((source, index) => ({id: 'page-' + index, source,
      url: '/product/' + index + '/', title: 'Page ' + index, locale: 'en', props: {frontMatter: {value: index}}}))};
  const measurements = [];
  let previous;
  async function measure(name, changes = {}) {
    const workRoot = await mkdtemp(path.join(root, 'w-'));
    const start = performance.now();
    const result = await compileSite({...request, ...changes, workRoot});
    const elapsedMilliseconds = performance.now() - start;
    assert.equal(result.workMetrics.complete, true);
    assert.equal(result.pages.length, pages);
    assert.ok(result.pages.every(page => page.entry && page.hydration === 'page'));
    const assets = new Map(result.assets.map(asset => [asset.path, asset]));
    assert.equal(assets.size, result.assets.length);
    for (const page of result.pages) {
      const pending = [page.entry, ...page.css], seen = new Set();
      while (pending.length) {
        const file = pending.pop(); if (seen.has(file)) continue; seen.add(file);
        assert.ok(assets.has(file), 'Missing output dependency: ' + file);
        pending.push(...assets.get(file).imports);
      }
    }
    const js = new Map(result.assets.filter(asset => asset.path.endsWith('.js')).map(asset => [asset.path, asset.hash]));
    const oldJs = new Map((previous?.assets ?? []).filter(asset => asset.path.endsWith('.js')).map(asset => [asset.path, asset.hash]));
    measurements.push({name, elapsedMilliseconds, work: result.workMetrics,
      compiledModules: result.compiledModules, renderedPages: result.renderedPages,
      rebundledPageIds: result.rebundledPages,
      unchangedJsAssets: [...js].filter(([name, fingerprint]) => oldJs.get(name) === fingerprint).length,
      addedJsAssets: [...js.keys()].filter(name => !oldJs.has(name)).length,
      removedJsAssets: [...oldJs.keys()].filter(name => !js.has(name)).length,
      emittedAssetBytes: result.assets.reduce((total, asset) => total + Buffer.from(asset.bytes, 'base64').length, 0),
      memorySnapshot: process.memoryUsage(), retainedPayload: getRetainedCacheMetrics()});
    previous = result;
    return result;
  }
  await measure('cold');
  const noop = await measure('no-op');
  assert.equal(noop.workMetrics.browserBuildInvocations, 0);
  sources['page-0.mdx'] += '\nLocal body edit.\n';
  await writeFile(path.join(projectRoot, 'page-0.mdx'), sources['page-0.mdx']);
  assert.ok((await measure('one-body')).pages[0].html.includes('Local body edit'));
  sources['page-0.mdx'] = `import Local from './Local.jsx'\n\n${sources['page-0.mdx']}\n<Local />\n`;
  await writeFile(path.join(projectRoot, 'page-0.mdx'), sources['page-0.mdx']);
  assert.ok((await measure('local-import')).pages[0].html.includes('local-import'));
  request.pages[0].props.frontMatter.value = 'new-props';
  assert.ok((await measure('one-props')).pages[0].html.includes('new-props'));
  await writeFile(path.join(projectRoot, 'shared.js'), `export const label='shared-second';`);
  assert.ok((await measure('shared-component-dependency')).pages.every(page => page.html.includes('shared-second')));
  const unknown = await measure('unavailable-resolution-evidence', {resolutionFingerprint: undefined});
  assert.equal(unknown.workMetrics.browserEntryBuildAttempts, pages);
  const report = {schemaVersion: 1, scope: 'worker-compile-site', pages,
    node: process.version, platform: process.platform, architecture: process.arch,
    runnerSha256: digest(await readFile(fileURLToPath(import.meta.url))), workerInputs,
    note: 'Run the same runner and page count for baseline and candidate in separate fresh processes. Memory fields are snapshots, not peaks. Canonical resource counters are not elapsed timings. Full-site, allocation and hydration/browser acceptance remain separate.',
    measurements};
  await mkdir(path.dirname(output), {recursive: true});
  await writeFile(output, JSON.stringify(report, null, 2) + '\n', {flag: 'wx'});
  console.log(output);
} finally {
  if (path.dirname(root) !== temporaryRoot || !path.basename(root).startsWith('mx19-') || await realpath(root) !== root)
    throw new Error('Preserving an unexpected measurement workspace: ' + root);
  await rm(root, {recursive: true, force: true});
}
