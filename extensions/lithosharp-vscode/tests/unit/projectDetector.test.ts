import assert from 'node:assert/strict';
import test from 'node:test';
import * as path from 'node:path';
import { detectWorkspaceFlags, findProjectCandidates, keyOf } from '../../src/projectDetector.js';
import { fixturePath } from './helpers.js';

const fixtures = fixturePath();

test('no project yields no candidates', async () => {
  assert.deepEqual(await findProjectCandidates(path.join(fixtures, 'empty'), 'empty'), []);
});

test('one LithoSharp reference yields one candidate', async () => {
  const found = await findProjectCandidates(path.join(fixtures, 'single'), 'single');
  assert.equal(found.length, 1);
  assert.equal(found[0]!.displayName, 'app');
  assert.equal(found[0]!.kind, 'lithosharp');
});

test('same-name projects in two roots keep distinct keys', async () => {
  const a = await findProjectCandidates(path.join(fixtures, 'multi', 'a'), 'a');
  const b = await findProjectCandidates(path.join(fixtures, 'multi', 'b'), 'b');
  assert.equal(a.length, 1);
  assert.equal(b.length, 1);
  assert.equal(a[0]!.displayName, b[0]!.displayName);
  assert.notEqual(keyOf(a[0]!), keyOf(b[0]!));
});

test('whitespace and Japanese paths are detected', async () => {
  const found = await findProjectCandidates(path.join(fixtures, '日本語 dir'), 'jp');
  assert.equal(found.length, 1);
  assert.equal(found[0]!.displayName, 'サイト');
});

test('non-LithoSharp projects are not candidates', async () => {
  assert.deepEqual(await findProjectCandidates(path.join(fixtures, 'central'), 'central'), []);
});

test('central management flags come from static files', async () => {
  const flags = await detectWorkspaceFlags(path.join(fixtures, 'central'));
  assert.equal(flags.centralManagement, true);
  const plain = await detectWorkspaceFlags(path.join(fixtures, 'empty'));
  assert.equal(plain.centralManagement, false);
  assert.equal(plain.toolManifest, false);
});
