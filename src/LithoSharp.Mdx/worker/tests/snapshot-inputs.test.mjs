import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdtemp, mkdir, writeFile, readFile, realpath, rm} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {compileSite} from '../compiler.mjs';

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
async function fixture(run) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-mdx-snapshot-'));
  try {
    const projectRoot = path.join(root, 'project'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    const raw = '---\ntitle: Snapshot\n---\n# CapturedOldMarker\n';
    await writeFile(path.join(projectRoot, 'page.mdx'), raw);
    const request = {projectRoot, workRoot, assetBaseUrl: '/_mdx/', basePath: '/', hydration: 'selective',
      cacheable: true, resolutionFingerprint: 'snapshot-fixture-v1', sources: {'page.mdx': '# CapturedOldMarker\n'},
      capturedInputs: {'page.mdx': digest(raw)},
      pages: [{id: 'snapshot', source: 'page.mdx', url: '/snapshot/', title: 'Snapshot', locale: 'en', props: {}}]};
    await run(request, raw);
  } finally { await rm(root, {recursive: true, force: true}); }
}

test('raw captured bytes can differ from processed source and unchanged compilation and rendering genuinely reuse work', async () => {
  await fixture(async request => {
    const cold = await compileSite(request);
    assert.match(cold.pages[0].html, /CapturedOldMarker/);
    assert.equal(cold.compiledModules, 1);
    const file = await realpath(path.join(request.projectRoot, 'page.mdx'));
    assert.equal(cold.inputs.find(input => input.file === file)?.hash, request.capturedInputs['page.mdx']);
    const warm = await compileSite(request);
    assert.deepEqual(warm.pages, cold.pages);
    const orderedAssets = assets => [...assets].sort((a, b) => a.path.localeCompare(b.path));
    assert.deepEqual(orderedAssets(warm.assets), orderedAssets(cold.assets));
    assert.equal(warm.compiledModules, 0);
    assert.equal(warm.renderedPages, 0);
    assert.equal(warm.bundledPages, 0);
    const orderedInputs = inputs => [...inputs].sort((a, b) => a.file.localeCompare(b.file));
    assert.deepEqual(orderedInputs(warm.inputs), orderedInputs(cold.inputs));
  });
});

test('an entry changed after capture rejects rather than certifying stale text with the new disk hash', async () => {
  await fixture(async request => {
    await compileSite(request);
    await writeFile(path.join(request.projectRoot, 'page.mdx'), '---\ntitle: Snapshot\n---\n# CurrentNewMarker\n');
    await assert.rejects(compileSite(request), /changed after its source snapshot was captured/);
    request.capturedInputs['page.mdx'] = digest(await readFile(path.join(request.projectRoot, 'page.mdx')));
    request.sources['page.mdx'] = '# CurrentNewMarker\n';
    const current = await compileSite(request);
    assert.match(current.pages[0].html, /CurrentNewMarker/);
    assert.doesNotMatch(current.pages[0].html, /CapturedOldMarker/);
  });
});

test('captured preprocessing inputs remain mandatory even when absent from the JavaScript graph', async () => {
  await fixture(async request => {
    const file = path.join(request.projectRoot, 'included.cs');
    await writeFile(file, '// CapturedOldMarker\n');
    request.capturedInputs['included.cs'] = digest(await readFile(file));
    // The resolved body has no import/source directive left. Its input must still be bound.
    request.sources['page.mdx'] = '# Snapshot\n\n```cs\n// CapturedOldMarker\n```\n';
    const cold = await compileSite(request);
    const canonical = await realpath(file);
    assert.equal(cold.inputs.find(input => input.file === canonical)?.hash, request.capturedInputs['included.cs']);
    await writeFile(file, '// CurrentNewMarker\n');
    await assert.rejects(compileSite(request), /changed after its source snapshot was captured/);
    request.capturedInputs['included.cs'] = digest(await readFile(file));
    request.sources['page.mdx'] = '# Snapshot\n\n```cs\n// CurrentNewMarker\n```\n';
    const current = await compileSite(request);
    assert.match(current.pages[0].html, /CurrentNewMarker/);
    assert.doesNotMatch(current.pages[0].html, /CapturedOldMarker/);
    await rm(file);
    await assert.rejects(compileSite(request), /ENOENT/);
  });
});

test('captured imported partials reject raw edits and recover with a fresh validated partial', async () => {
  await fixture(async request => {
    const file = path.join(request.projectRoot, '_partial.mdx');
    request.sources['page.mdx'] = "import Partial from './_partial.mdx';\n\n<Partial />\n";
    request.sources['_partial.mdx'] = '# PartialOldMarker\n';
    await writeFile(file, request.sources['_partial.mdx']);
    request.capturedInputs['_partial.mdx'] = digest(await readFile(file));
    const cold = await compileSite(request);
    assert.match(cold.pages[0].html, /PartialOldMarker/);
    await writeFile(file, '# PartialNewMarker\n');
    await assert.rejects(compileSite(request), /changed after its source snapshot was captured/);
    request.sources['_partial.mdx'] = '# PartialNewMarker\n';
    request.capturedInputs['_partial.mdx'] = digest(await readFile(file));
    assert.match((await compileSite(request)).pages[0].html, /PartialNewMarker/);
  });
});

test('direct caller-source semantics remain explicit when no original-read metadata exists', async () => {
  await fixture(async request => {
    delete request.capturedInputs;
    request.sources['page.mdx'] = '# DeliberateCallerBody\n';
    const result = await compileSite(request);
    assert.match(result.pages[0].html, /DeliberateCallerBody/);
    assert.doesNotMatch(result.pages[0].html, /CapturedOldMarker/);
  });
});

test('invalid captured identities and escapes fail closed without changing direct protocol semantics', async () => {
  await fixture(async request => {
    for (const capturedInputs of [null, [], 'hash', {'page.mdx': 1}, {'page.mdx': 'bad'}, {'../outside.mdx': 'a'.repeat(64)}])
      await assert.rejects(compileSite({...request, capturedInputs}), /Invalid captured MDX input/);
    await assert.rejects(compileSite({...request, capturedInputs: {'page.mdx': request.capturedInputs['page.mdx'], './page.mdx': 'b'.repeat(64)}}), /Conflicting captured MDX input/);
  });
});
