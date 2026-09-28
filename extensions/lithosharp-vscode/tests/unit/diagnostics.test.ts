import assert from 'node:assert/strict';
import test from 'node:test';
import { toVsDiagnostic } from '../../src/diagnostics.js';

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
