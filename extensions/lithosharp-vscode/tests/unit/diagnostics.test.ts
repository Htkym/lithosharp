import assert from 'node:assert/strict';
import test from 'node:test';
import { diagnosticReferenceUrl, toVsDiagnostic } from '../../src/diagnostics.js';

test('diagnostic identity survives the VS Code boundary', () => {
  const adapted = toVsDiagnostic({
    range: { start: { line: 3, character: 4 }, end: { line: 3, character: 9 } },
    severity: 2,
    code: 'LIT001',
    source: 'lithosharp',
    message: 'Undefined footnote.',
  });
  assert.equal(adapted.code, 'LIT001');
  assert.equal(adapted.source, 'lithosharp');
  assert.equal(adapted.message, 'Undefined footnote.');
  assert.deepEqual(adapted.range, { start: { line: 3, character: 4 }, end: { line: 3, character: 9 } });
});

test('registered diagnostics link to official guidance without changing their evidence', () => {
  const range = { start: { line: 3, character: 4 }, end: { line: 3, character: 9 } };
  for (const [code, anchor] of [
    ['LSM001', 'front-matter'], ['LSC104', 'front-matter-binding'],
    ['LSM013', 'lsm013'], ['LSG005', 'lsg005'], ['LSG006', 'lsg006'],
    ['LSA1402', 'lsa1402'], ['LSA9001', 'lsa9001'],
    ['LSQ001', 'lsq001'], ['LSQ002', 'lsq002'],
  ] as const) {
    const item = { range, severity: 1, code, source: 'lithosharp', message: 'Original diagnostic.' };
    const adapted = toVsDiagnostic(item);
    const target = `https://github.com/Htkym/lithosharp/blob/main/docs/diagnostics.md#${anchor}`;
    assert.deepEqual(adapted, {
      ...item, severity: 0, code: { value: code, target },
    });
    assert.equal(diagnosticReferenceUrl(code), target);
  }
});

test('unregistered IDs cannot become documentation links', () => {
  for (const code of ['LSM999', 'lsm013', '__proto__', 'constructor', 'https://example.invalid']) {
    assert.equal(diagnosticReferenceUrl(code), undefined);
    const adapted = toVsDiagnostic({
      range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } },
      severity: 2, code, source: 'lithosharp', message: 'Original diagnostic.',
    });
    assert.equal(adapted.code, code);
  }
});

test('LSP severity converts to VS Code numbering', () => {
  const convert = (severity: number): number =>
    toVsDiagnostic({
      range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } },
      severity,
      code: 'LIT001',
      source: 'lithosharp',
      message: 'm',
    }).severity;
  // LSP 1=Error..4=Hint becomes VS Code 0=Error..3=Hint.
  assert.equal(convert(1), 0);
  assert.equal(convert(2), 1);
  assert.equal(convert(3), 2);
  assert.equal(convert(4), 3);
  assert.equal(convert(99), 3);
});
