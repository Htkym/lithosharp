import assert from 'node:assert/strict';
import test from 'node:test';
import { DiagnosticStore, type LspDiagnosticData } from '../../src/diagnostics.js';
import { mapSymbols } from '../../src/symbols.js';

function diagnostic(line: number): LspDiagnosticData {
  return {
    range: { start: { line, character: 0 }, end: { line, character: 1 } },
    severity: 1,
    code: 'LIT001',
    source: 'lithosharp',
    message: 'm',
  };
}

test('stale versions never resurface', () => {
  const sets: { uri: string; count: number }[] = [];
  const store = new DiagnosticStore({
    set: (uri, diagnostics) => sets.push({ uri, count: diagnostics.length }),
    delete: () => {},
    clear: () => {},
  });
  store.publish('u', 3, [diagnostic(0)]);
  store.publish('u', 2, [diagnostic(0), diagnostic(1)]);
  assert.deepEqual(sets.map((entry) => entry.count), [1]);
  store.publish('u', null, [diagnostic(0)]);
  assert.deepEqual(sets.map((entry) => entry.count), [1, 1]);
});

test('close and rename clear versions', () => {
  const deleted: string[] = [];
  const store = new DiagnosticStore({
    set: () => {},
    delete: (uri) => deleted.push(uri),
    clear: () => {},
  });
  store.publish('u', 5, [diagnostic(0)]);
  store.clear('u');
  store.publish('u', 4, [diagnostic(0)]);
  assert.deepEqual(deleted, ['u']);
});

test('symbols keep hierarchy and Japanese names', () => {
  const symbols = mapSymbols([
    { name: 'Top', kind: 3, range: { start: { line: 0, character: 0 }, end: { line: 0, character: 5 } }, selectionRange: { start: { line: 0, character: 0 }, end: { line: 0, character: 5 } }, children: [] },
    { name: '日本語🎉', kind: 3, range: { start: { line: 2, character: 0 }, end: { line: 2, character: 7 } }, selectionRange: { start: { line: 2, character: 0 }, end: { line: 2, character: 7 } }, children: [] },
  ]);
  assert.equal(symbols.length, 2);
  assert.equal(symbols[1]!.name, '日本語🎉');
});

test('partial symbols stay partial', () => {
  assert.deepEqual(mapSymbols(null), []);
  assert.deepEqual(mapSymbols([{ name: 'x' }]), []);
  const kept = mapSymbols([
    { name: 'ok', kind: 3, range: { start: { line: 0, character: 0 }, end: { line: 0, character: 2 } }, selectionRange: { start: { line: 0, character: 0 }, end: { line: 0, character: 2 } }, children: [{ bogus: true }] },
  ]);
  assert.equal(kept.length, 1);
  assert.equal(kept[0]!.children.length, 0);
});
