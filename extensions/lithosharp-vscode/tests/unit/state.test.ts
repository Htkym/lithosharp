import assert from 'node:assert/strict';
import test from 'node:test';
import { editorPresentation, operationSummary, quickPickItems, statusText } from '../../src/state.js';
import { parseCliResult } from '../../src/buildRunner.js';

test('status text covers every state', () => {
  assert.equal(statusText({ kind: 'none', candidates: 0 }), 'LithoSharp: no project');
  assert.equal(statusText({ kind: 'ambiguous', candidates: 3 }), 'LithoSharp: select project (3)');
  assert.equal(statusText({ kind: 'untrusted' }), 'LithoSharp: untrusted');
  assert.equal(statusText({ kind: 'missing-cli', reason: 'x' }), 'LithoSharp: no CLI');
});

test('four status axes and serving remain independent and preflight preserves reported scope', () => {
  const result = parseCliResult('preflight', 0, JSON.stringify({ schemaVersion: '1.1', success: true, exitCode: 0, diagnostics: [],
    preflight: { mode: 'static-inputs', stage: 'source', coverage: 'Partial', sourceCoverage: 'Incomplete',
      deferredReasons: ['Catalog not inspected.'] } }));
  const operation = { id: 1, command: 'preflight' as const, target: '/docs/site.csproj; intro.md', startedAt: '2026-11-07T00:00:00Z', result };
  const view = editorPresentation({ kind: 'none', candidates: 0 }, { uri: 'file:///intro.md', version: 2, language: 'markdown',
    state: 'ready', message: 'Syntax completed', projectStatus: 'Unresolved', schemaName: 'custom', schemaScope: 'build-required' },
    'needs-refresh', operation, 'Running');
  assert.match(view.text, /analysis complete.*context needs refresh.*preflight succeeded.*serve Running/);
  assert.match(view.detail, /document resolution: Unresolved/);
  assert.match(view.detail, /Custom schema: check with Build/);
  assert.match(operationSummary(operation), /coverage: Partial; sourceCoverage: Incomplete/);
  assert.match(operationSummary(operation), /Deferred: Catalog not inspected/);
  const legacy = editorPresentation({ kind: 'none', candidates: 0 }, { uri: 'file:///intro.md', version: 1, language: 'markdown',
    state: 'ready', message: '', schemaName: 'document' }, 'unacquired');
  assert.match(legacy.detail, /Inspection scope unknown/);
  assert.match(legacy.detail, /Build success does not establish that Quality ran/);
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
