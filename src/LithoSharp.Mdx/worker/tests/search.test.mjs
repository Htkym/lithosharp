import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {runInNewContext} from 'node:vm';

const source = await readFile(new URL('../../../LithoSharp/Search/query.mjs', import.meta.url), 'utf8');
const queryDocuments = runInNewContext(source + '\nqueryDocuments;');
const fixture = JSON.parse(await readFile(new URL('../../../../tests/LithoSharp.Tests/Fixtures/Search/technical-search.json', import.meta.url), 'utf8'));

test('technical queries rank the same fixed document sections as LocalSearch', () => {
  const before = JSON.stringify(fixture.documents);
  for (const example of fixture.cases) {
    const hits = queryDocuments(fixture.documents, example.query, example);
    assert.deepEqual(Array.from(hits, hit => hit.url), example.expected, example.query);
  }
  assert.equal(JSON.stringify(fixture.documents), before, 'Search normalization must preserve reference identities');
});

test('built-in fuzzy matches stay below direct matches', () => {
  const documents = [
    {title: 'Clever File Reader', summary: '', tags: [], url: '/fuzzy/', body: ''},
    {title: 'CFR', summary: '', tags: [], url: '/exact/', body: ''}
  ];
  assert.deepEqual(Array.from(queryDocuments(documents, 'cfr', {fuzzy: true}), hit => hit.url), ['/exact/', '/fuzzy/']);
  assert.deepEqual(Array.from(queryDocuments(documents, 'cfr'), hit => hit.url), ['/exact/']);
});
