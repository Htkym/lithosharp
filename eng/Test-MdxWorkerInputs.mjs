import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {mkdtemp, mkdir, readFile, realpath, rm, writeFile} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {captureMdxWorkerInputs} from './Capture-MdxWorkerInputs.mjs';

async function fixture(t) {
  const parent = await realpath(os.tmpdir());
  const root = await mkdtemp(path.join(parent, 'mdx-input-test-'));
  t.after(async () => {
    assert.equal(path.dirname(root), parent);
    assert.ok(path.basename(root).startsWith('mdx-input-test-'));
    assert.equal(await realpath(root), root);
    await rm(root, {recursive: true, force: true});
  });
  await mkdir(path.join(root, 'runtime'));
  for (const [name, text] of Object.entries({
    'compiler.mjs': "throw new Error('This synthetic compiler must not run');\n",
    'worker.mjs': 'worker-v1', 'public-markdown.mjs': 'helper-v1',
    'package.json': '{"engines":{"node":"24.13.0"}}', 'package-lock.json': 'lock-v1',
    'runtime/browser.mjs': 'browser-v1', 'runtime/style.css': 'style-v1'
  })) await writeFile(path.join(root, name), text);
  return root;
}
const hash = text => createHash('sha256').update(text).digest('hex');

test('records root helper modules, package metadata and runtime bytes without importing them', async t => {
  const root = await fixture(t);
  await writeFile(path.join(root, 'future-helper.mjs'), 'future-v1');
  await writeFile(path.join(root, 'README.md'), 'not a runtime input');
  await mkdir(path.join(root, 'node_modules'));
  await writeFile(path.join(root, 'node_modules', 'ignored.mjs'), 'outside bounded capture');
  const inputs = await captureMdxWorkerInputs(root);
  assert.deepEqual(Object.keys(inputs), [
    'compiler.mjs', 'future-helper.mjs', 'package-lock.json', 'package.json',
    'public-markdown.mjs', 'runtime/browser.mjs', 'runtime/style.css', 'worker.mjs'
  ]);
  for (const [name, digest] of Object.entries(inputs))
    assert.equal(digest, hash(await readFile(path.join(root, name))));
  assert.deepEqual(await captureMdxWorkerInputs(root), inputs);
});

test('helper-only and package-metadata changes are visible in the receipt', async t => {
  const root = await fixture(t);
  const before = await captureMdxWorkerInputs(root);
  await writeFile(path.join(root, 'public-markdown.mjs'), 'helper-v2');
  const after = await captureMdxWorkerInputs(root);
  assert.equal(after['public-markdown.mjs'], hash('helper-v2'));
  assert.deepEqual(Object.keys(after).filter(name => before[name] !== after[name]), ['public-markdown.mjs']);
  await writeFile(path.join(root, 'package.json'), '{"engines":{"node":"different"}}');
  const changed = await captureMdxWorkerInputs(root);
  assert.deepEqual(Object.keys(changed).filter(name => after[name] !== changed[name]), ['package.json']);
});

test('retains compatibility with a baseline worker that has no public Markdown helper', async t => {
  const root = await fixture(t);
  await rm(path.join(root, 'public-markdown.mjs'));
  const inputs = await captureMdxWorkerInputs(root);
  assert.equal(Object.hasOwn(inputs, 'public-markdown.mjs'), false);
  assert.equal(inputs['compiler.mjs'], hash(await readFile(path.join(root, 'compiler.mjs'))));
});

test('refuses a missing required worker input', async t => {
  const root = await fixture(t);
  await rm(path.join(root, 'worker.mjs'));
  await assert.rejects(captureMdxWorkerInputs(root), /Missing worker input: worker\.mjs/);
});

test('refuses a module-shaped directory rather than omitting it', async t => {
  const root = await fixture(t);
  await mkdir(path.join(root, 'unexpected.mjs'));
  await assert.rejects(captureMdxWorkerInputs(root), /Worker input must be a regular file: unexpected\.mjs/);
});
