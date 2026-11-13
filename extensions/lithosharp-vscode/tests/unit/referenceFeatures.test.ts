import assert from 'node:assert/strict';
import test from 'node:test';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {mapReferenceLocations, mapReferenceScopes, textHash, validateReferencePlan, validateReferenceWorkspaceEdit} from '../../src/referenceFeatures.js';

const sourcePath = path.resolve('source.md'), otherPath = path.resolve('other.md');
const source = {uri: pathToFileURL(sourcePath).href, version: 7, text: '# 😀 Heading\r\nText'};
const other = {uri: pathToFileURL(otherPath).href, version: 3, text: '[go](#old)'};
const documents = new Map([[source.uri, source], [other.uri, other]]);
function preview() {
  return {scope: 'captured-static-markdown', sourceVersion: 7, sourceTextHash: textHash(source.text),
    canApply: true, isComplete: false, manualScopes: ['C# references'], moves: [], edits: [
      {sourcePath, version: 7, start: 2, length: 10, expectedText: '😀 Heading', replacement: 'New'},
      {sourcePath: otherPath, version: 3, start: 6, length: 3, expectedText: 'old', replacement: 'new'},
    ]};
}
function workspace() {
  return {documentChanges: [
    {textDocument: {uri: source.uri, version: 7}, edits: [{range: {start: {line: 0, character: 2}, end: {line: 0, character: 12}}, newText: 'New', annotationId: 'review'}]},
    {textDocument: {uri: other.uri, version: 3}, edits: [{range: {start: {line: 0, character: 6}, end: {line: 0, character: 9}}, newText: 'new', annotationId: 'review'}]},
  ], changeAnnotations: {review: {label: 'Captured Markdown', needsConfirmation: true, description: 'C# references'}}};
}
test('reference previews retain UTF-16 expected text and versioned multi-document edits', () => {
  const plan = validateReferencePlan(preview(), source, documents);
  assert.equal(plan.replacements.length, 2);
  assert.deepEqual(plan.replacements.find(edit => edit.uri === source.uri)?.range, {start: {line: 0, character: 2}, end: {line: 0, character: 12}});
  assert.equal(validateReferenceWorkspaceEdit(workspace(), plan, documents).length, 2);
  assert.equal(mapReferenceLocations([{uri: source.uri, range: plan.replacements.find(edit => edit.uri === source.uri)!.range}], documents).length, 1);
});
test('one stale, missing, foreign or overlapping target rejects the whole transaction', () => {
  for (const mutate of [
    (value: ReturnType<typeof preview>) => { value.sourceTextHash = 'stale'; },
    (value: ReturnType<typeof preview>) => { value.edits[1]!.version++; },
    (value: ReturnType<typeof preview>) => { value.edits[1]!.expectedText = 'different'; },
    (value: ReturnType<typeof preview>) => { value.edits[1]!.sourcePath = path.resolve('Generated.g.cs'); },
    (value: ReturnType<typeof preview>) => { value.edits[1]!.sourcePath = path.resolve('dynamic.mdx'); },
    (value: ReturnType<typeof preview>) => { value.edits.push({...value.edits[0]!}); },
  ]) { const value = preview(); mutate(value); assert.throws(() => validateReferencePlan(value, source, documents)); }
  assert.throws(() => validateReferencePlan(preview(), source, new Map([[source.uri, source]])));
  const split = preview(); split.edits = [{sourcePath, version: 7, start: 13, length: 0, expectedText: '', replacement: 'bad'}];
  assert.throws(() => validateReferencePlan(split, source, documents), /CRLF/);
});
test('unversioned, resource, changed preview and unconfirmed manual-scope edits are refused', () => {
  const plan = validateReferencePlan(preview(), source, documents);
  assert.throws(() => validateReferenceWorkspaceEdit({changes: {}}, plan, documents));
  assert.throws(() => validateReferenceWorkspaceEdit({documentChanges: [{kind: 'rename', oldUri: source.uri, newUri: other.uri}]}, plan, documents));
  for (const mutate of [
    (value: ReturnType<typeof workspace>) => { value.documentChanges[1]!.textDocument.version++; },
    (value: ReturnType<typeof workspace>) => { value.documentChanges[1]!.edits[0]!.newText = 'changed'; },
    (value: ReturnType<typeof workspace>) => { value.changeAnnotations.review.needsConfirmation = false; },
    (value: ReturnType<typeof workspace>) => { value.documentChanges[0]!.edits = []; },
  ]) { const value = workspace(); mutate(value); assert.throws(() => validateReferenceWorkspaceEdit(value, plan, documents)); }
});
test('scope choices require current source evidence and preserve every acquired tuple', () => {
  const scopes = [{collection: 'guide', version: 'v1', locale: 'en', publicPaths: ['/guide/a/']},
    {collection: 'guide', version: 'v2', locale: 'ja', publicPaths: ['/ja/a/']}];
  assert.deepEqual(mapReferenceScopes({sourceVersion: 7, scopes}, 7), scopes);
  assert.deepEqual(mapReferenceScopes({sourceVersion: 6, scopes}, 7), []);
  assert.deepEqual(mapReferenceScopes({sourceVersion: 7, scopes: [{collection: 'missing'}]}, 7), []);
});
test('native source paths map to the existing editor URI without losing encoded or Windows drive identity', () => {
  let alias = source.uri.replace('source.md', '%73ource.md');
  if (process.platform === 'win32') alias = alias.replace(/^file:\/\/\/([A-Za-z]):/, (_match, drive: string) => `file:///${drive.toLowerCase()}%3A`);
  const aliased = {...source, uri: alias};
  const aliasedDocuments = new Map([[alias, aliased], [other.uri, other]]);
  const plan = validateReferencePlan(preview(), aliased, aliasedDocuments);
  assert.equal(plan.replacements.find(edit => edit.expectedText === '😀 Heading')?.uri, alias);
  assert.equal(validateReferenceWorkspaceEdit(workspace(), plan, aliasedDocuments).length, 2);
  assert.throws(() => validateReferencePlan(preview(), source, new Map([...documents, [alias, aliased]])), /Multiple editor identities/);
});
