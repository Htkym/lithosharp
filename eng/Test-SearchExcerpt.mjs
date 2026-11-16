import assert from 'node:assert/strict';
import test from 'node:test';
import {readFile} from 'node:fs/promises';
import {runInNewContext} from 'node:vm';
const source = await readFile(new URL('../src/LithoSharp/Search/query.mjs', import.meta.url), 'utf8');
const query = runInNewContext(source + '\nqueryDocuments;');
const excerpt = (body, needle = 'target') => query([{title: needle, url: '/test', body}], needle)[0].body;
test('keeps complete short content as plain text', () => {
  assert.equal(excerpt('<script>target & "quoted"</script>'), '<script>target & "quoted"</script>');
  assert.equal(excerpt(''), '');
});
test('omits partial English words and marks both truncated ends within the budget', () => {
  const body = 'introduction '.repeat(10) + 'target ' + 'documentation '.repeat(30);
  const result = excerpt(body);
  assert.match(result, /^…(?:introduction )+target /u);
  assert.match(result, /documentation…$/u);
  assert.ok(result.length <= 180);
});
test('preserves CJK and grapheme clusters at either cut', () => {
  for (const unit of ['説明', '😀', 'e\u0301', '👨‍👩‍👧‍👦']) {
    const body = unit.repeat(70) + 'target' + unit.repeat(200);
    const result = excerpt(body);
    assert.ok(result.includes('target'));
    assert.ok(result.length <= 180);
    assert.ok(result.isWellFormed());
    const content = result.replace(/^…|…$/gu, '');
    assert.ok(body.includes(content));
    const boundaries = new Set([...new Intl.Segmenter(undefined, {granularity:'grapheme'}).segment(body)].map(s => s.index));
    const start = body.indexOf(content);
    assert.ok(boundaries.has(start));
    assert.ok(boundaries.has(start + content.length));
  }
});
test('marks tail-only truncation and leaves an untruncated tail unmarked', () => {
  assert.match(excerpt('target ' + 'word '.repeat(60)), /^target .*…$/u);
  assert.match(excerpt('word '.repeat(60) + 'target'), /^….*target$/u);
});
test('fallback without Segmenter preserves surrogate pairs', () => {
  const fallback = runInNewContext(source + '\nqueryDocuments;', {Intl:{}});
  const result = fallback([{title:'target',url:'/test',body:'😀'.repeat(100) + 'target' + '😀'.repeat(100)}], 'target')[0].body;
  assert.ok(result.isWellFormed());
  assert.ok(result.length <= 180);
});
test('retains technical search ranking and scoped URLs', async () => {
  const fixture = JSON.parse(await readFile(new URL('../tests/LithoSharp.Tests/Fixtures/Search/technical-search.json', import.meta.url), 'utf8'));
  for (const example of fixture.cases)
    assert.deepEqual(Array.from(query(fixture.documents, example.query, example), hit => hit.url), example.expected);
});
