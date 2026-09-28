import assert from 'node:assert/strict';
import test from 'node:test';
import { quickPickItems, statusText } from '../../src/state.js';

test('status text covers every state', () => {
  assert.equal(statusText({ kind: 'none', candidates: 0 }), 'LithoSharp: no project');
  assert.equal(statusText({ kind: 'ambiguous', candidates: 3 }), 'LithoSharp: select project (3)');
  assert.equal(statusText({ kind: 'untrusted' }), 'LithoSharp: untrusted');
  assert.equal(statusText({ kind: 'missing-cli', reason: 'x' }), 'LithoSharp: no CLI');
});

test('quick pick keeps workspace identity visible', () => {
  const items = quickPickItems([
    { workspaceFolder: '/a', workspaceName: 'a', projectPath: '/a/site.csproj', displayName: 'site', kind: 'lithosharp' },
    { workspaceFolder: '/b', workspaceName: 'b', projectPath: '/b/site.csproj', displayName: 'site', kind: 'lithosharp' },
  ]);
  assert.equal(items.length, 2);
  assert.equal(items[0]!.label, items[1]!.label);
  assert.notEqual(items[0]!.description, items[1]!.description);
  assert.notEqual(items[0]!.detail, items[1]!.detail);
});
