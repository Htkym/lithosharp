import assert from 'node:assert/strict';
import test from 'node:test';
import { mapCompletions, mapHover } from '../../src/editorFeatures.js';

test('editor results preserve plain text and UTF-16 edits while rejecting foreign ranges', () => {
  const text = '---\r\ntitle: 😀\r\n---';
  const range = { start: { line: 1, character: 7 }, end: { line: 1, character: 9 } };
  const item = { label: 'name', kind: 10, documentation: { kind: 'markdown', value: '[run](command:untrusted)' },
    textEdit: { range, newText: 'replacement' } };
  assert.deepEqual(mapCompletions({ items: [item] }, text), [{ label: 'name', kind: 10,
    documentation: '[run](command:untrusted)', textEdit: { range, newText: 'replacement' } }]);
  assert.deepEqual(mapCompletions([item, { ...item, textEdit: { range: { ...range, end: { line: 5, character: 0 } }, newText: 'bad' } }], text).length, 1);
  assert.deepEqual(mapHover({ contents: { kind: 'plaintext', value: 'Description' }, range }, text), { text: 'Description', range });
  assert.equal(mapHover({ contents: 'Description', range: { start: range.end, end: range.start } }, text), undefined);
});
