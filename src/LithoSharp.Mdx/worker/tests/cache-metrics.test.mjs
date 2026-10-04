import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {compileSite, getRetainedCacheMetrics} from '../compiler.mjs';
test('readonly cache payload snapshots preserve actual warm compilation', async () => {
  assert.equal(typeof getRetainedCacheMetrics, 'function');
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'private-cache-metrics-'));
  try {
    const projectRoot = path.join(root, 'site'); await fs.mkdir(projectRoot);
    const source = '# Metrics\n\nexport function Box(){return <span>Cache body</span>}\n\n<Box />\n';
    await fs.writeFile(path.join(projectRoot, 'index.mdx'), source);
    const request = {projectRoot, cacheable: true, resolutionFingerprint: 'private-snapshot-proof',
      assetBaseUrl: '/_mdx', basePath: '/', sources: {'index.mdx': source},
      pages: [{id: 'metrics', source: 'index.mdx', url: '/metrics/', locale: 'en', title: 'Metrics', props: {}}]};
    const compile = async () => compileSite({...request, workRoot: await fs.mkdtemp(path.join(root, 'work-'))});
    const first = await compile(); const snapshot = getRetainedCacheMetrics();
    for (const name of ['module', 'render']) {
      assert.ok(snapshot[name].entries > 0 && snapshot[name].entries <= snapshot[name].entryCeiling);
      assert.ok(snapshot[name].estimatedPayloadBytes > 0 && snapshot[name].estimatedPayloadBytes <= snapshot[name].payloadBudgetBytes);
      assert.equal(snapshot[name].lastAdmissionLimit, 2048);
    }
    assert.equal(snapshot.browser.retained, true);
    assert.ok(snapshot.browser.estimatedPayloadBytes <= snapshot.browser.payloadBudgetBytes);
    assert.ok(snapshot.browser.dependencyEntries <= snapshot.browser.dependencyCeiling);
    assert.ok(snapshot.browser.referenceEntries <= snapshot.browser.referenceCeiling);
    const untouched = structuredClone(snapshot);
    snapshot.module.entries = -1; snapshot.browser.estimatedPayloadBytes = Infinity;
    assert.deepEqual(getRetainedCacheMetrics(), untouched, 'Returned snapshot leaked mutable cache state.');
    const warm = await compile();
    assert.deepEqual(warm.pages, first.pages); assert.deepEqual(warm.assets, first.assets);
    assert.equal(warm.bundledPages, 0, 'Readonly metrics changed actual browser work.');
    assert.deepEqual(warm.rebundledPages, []);
    assert.deepEqual(getRetainedCacheMetrics(), untouched);
  } finally {await fs.rm(root, {recursive: true, force: true});}
});
