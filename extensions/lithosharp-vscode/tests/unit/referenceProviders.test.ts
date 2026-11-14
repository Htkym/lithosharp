import assert from 'node:assert/strict';
import test from 'node:test';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import type {LspClient} from '../../src/lspClient.js';
import {textHash} from '../../src/referenceFeatures.js';
import {createFakeVscode, installFakeVscode, trackDisposable} from './fakeVscode.js';

function fixture(changeAtConfirmation?: (source: {version: number; text: string}, other: {version: number; text: string}) => void) {
  const fake = createFakeVscode(), sourcePath = path.resolve('source.md'), otherPath = path.resolve('other.md');
  const document = (file: string, text: string) => ({uri: {fsPath: file, toString: () => pathToFileURL(file).href},
    languageId: 'markdown', version: 1, text, isClosed: false, getText() { return this.text; }});
  const source = document(sourcePath, '# Old'), other = document(otherPath, '[go](#old)');
  fake.workspaceApi.textDocuments.push(source, other);
  Object.assign(fake.window, {activeTextEditor: {document: source, selection: {active: {line: 0, character: 2}}}});
  fake.window.showInputBox = async () => 'New';
  fake.window.showWarningMessage = async (_message, _options, ...items) => { changeAtConfirmation?.(source, other); return items[0]; };
  const applications: unknown[] = [], calls: {method: string; scope: unknown}[] = [];
  let fileListener: ((event: {files: {oldUri: {toString(): string}; newUri: {toString(): string}}[]; token: unknown; waitUntil(value: Promise<unknown>): void}) => void) | undefined;
  const workspace = fake.workspaceApi as unknown as Record<string, unknown>;
  workspace['applyEdit'] = (edit: unknown) => { applications.push({edit, versions: [source.version, other.version]}); return Promise.resolve(true); };
  workspace['onWillRenameFiles'] = (listener: typeof fileListener) => { fileListener = listener; return trackDisposable(); };
  const snapshots = () => new Map([source, other].map(value => [value.uri.toString(), {uri: value.uri.toString(), version: 1,
    text: value === source ? '# Old' : '[go](#old)'}]));
  const preview = {scope: 'captured-static-markdown', sourceVersion: 1, sourceTextHash: textHash('# Old'),
    canApply: true, isComplete: false, manualScopes: ['C# references', 'closed files'], moves: [], edits: [
      {sourcePath, version: 1, start: 2, length: 3, expectedText: 'Old', replacement: 'New'},
      {sourcePath: otherPath, version: 1, start: 6, length: 3, expectedText: 'old', replacement: 'new'},
    ]};
  const range = {start: {line: 0, character: 2}, end: {line: 0, character: 5}};
  const client = {didOpen() {}, referenceGuard: () => () => true, referenceDocuments: snapshots,
    requestReferenceScopes: async () => [{collection: 'guide', version: 'v1', locale: 'en', publicPaths: ['/a/']}],
    requestRenamePreview: async (_uri: string, _parameters: unknown, scope: unknown) => { calls.push({method: 'preview', scope}); return preview; },
    requestRename: async (_uri: string, _parameters: unknown, scope: unknown) => { calls.push({method: 'rename', scope}); return {
      documentChanges: [
        {textDocument: {uri: source.uri.toString(), version: 1}, edits: [{range, newText: 'New', annotationId: 'review'}]},
        {textDocument: {uri: other.uri.toString(), version: 1}, edits: [{range: {start: {line: 0, character: 6}, end: {line: 0, character: 9}}, newText: 'new', annotationId: 'review'}]},
      ], changeAnnotations: {review: {label: 'Captured Markdown', needsConfirmation: true, description: 'C# references; closed files'}},
    }; },
    requestPrepareRename: async () => ({range, placeholder: 'Old'}),
    requestDefinition: async () => [{uri: source.uri.toString(), range}],
    requestReferences: async () => [{uri: other.uri.toString(), range}],
  };
  installFakeVscode(fake);
  const {registerReferenceProviders} = require('../../src/referenceProviders.js') as typeof import('../../src/referenceProviders.js');
  registerReferenceProviders({ensureClient: async () => client as unknown as LspClient, active: () => true,
    log: value => fake.lines.push(value), showOutput() {}});
  return {fake, source, other, applications, calls, client, fileListener: () => fileListener!};
}
test('explicit rename confirms and applies one multi-document text-only transaction', async () => {
  const f = fixture();
  await f.fake.commands.get('lithosharp.renameMarkdownReferences')!();
  assert.equal(f.applications.length, 1);
  assert.deepEqual(f.calls.map(value => value.method), ['preview', 'rename']);
  const applied = f.applications[0] as {edit: {changes: {metadata: {needsConfirmation: boolean}}[]}; versions: number[]};
  assert.deepEqual(applied.versions, [1, 1]); assert.equal(applied.edit.changes.length, 2);
  assert.ok(applied.edit.changes.every(change => change.metadata.needsConfirmation === false));
});
test('changes during confirmation reject the entire preview even at equal version', async () => {
  for (const change of ['source', 'other-version', 'other-text'] as const) {
    const f = fixture((source, other) => { if (change === 'source') source.version++;
      else if (change === 'other-version') other.version++; else other.text = 'same version, different raw text'; });
    await f.fake.commands.get('lithosharp.renameMarkdownReferences')!();
    assert.deepEqual(f.applications, []); assert.deepEqual(f.calls.map(value => value.method), ['preview']);
  }
});
test('read-only preview, F2 rejection and Explorer participant do not apply edits or move resources', async () => {
  const f = fixture();
  await f.fake.commands.get('lithosharp.previewMarkdownRename')!();
  const provider = f.fake.editorProviders.get('rename') as {provideRenameEdits(): unknown};
  assert.throws(() => provider.provideRenameEdits(), /does not preserve document versions/);
  let pending: Promise<unknown> | undefined;
  f.fileListener()({files: [{oldUri: f.source.uri, newUri: {toString: () => pathToFileURL(path.resolve('moved.md')).href}}],
    token: {isCancellationRequested: false, onCancellationRequested: () => ({dispose() {}})}, waitUntil: value => { pending = value; }});
  assert.ok(pending); assert.equal(await pending, undefined);
  assert.deepEqual(f.applications, []); assert.ok(f.calls.every(value => value.method === 'preview'));
  assert.ok(f.fake.lines.some(value => value.includes('Automatic file-rename reference edits were skipped')));
});
test('ambiguous acquired tuples require a visible selection and preserve that tuple', async () => {
  const f = fixture(); f.fake.pickedIndex = 1;
  f.client.requestReferenceScopes = async () => [
    {collection: 'guide', version: 'v1', locale: 'en', publicPaths: ['/a/']},
    {collection: 'guide', version: 'v2', locale: 'ja', publicPaths: ['/ja/a/']},
  ];
  await f.fake.commands.get('lithosharp.previewMarkdownRename')!();
  assert.equal(f.fake.picks[0]!.length, 2);
  assert.deepEqual(f.calls[0]!.scope, {collection: 'guide', version: 'v2', locale: 'ja'});
});
test('a target change after confirmation but before the final response prevents applyEdit', async () => {
  const f = fixture(), original = f.client.requestRename;
  f.client.requestRename = async (...args) => { const result = await original(...args); f.other.version++; return result; };
  await f.fake.commands.get('lithosharp.renameMarkdownReferences')!();
  assert.deepEqual(f.applications, []);
  assert.ok(f.fake.lines.some(line => line.includes('changed. Acquire a fresh preview')));
});
test('definition discovery cancellation settles without waiting for the server startup', async () => {
  const f = fixture();
  const {registerReferenceProviders} = require('../../src/referenceProviders.js') as typeof import('../../src/referenceProviders.js');
  registerReferenceProviders({ensureClient: () => new Promise(() => {}), active: () => true, log() {}, showOutput() {}});
  let cancelled = false;
  const listeners = new Set<() => void>();
  const token = {get isCancellationRequested() { return cancelled; }, onCancellationRequested(listener: () => void) {
    listeners.add(listener); return {dispose: () => listeners.delete(listener)};
  }};
  const provider = f.fake.editorProviders.get('definition') as {provideDefinition(document: unknown, position: unknown, token: unknown): Promise<unknown>};
  const pending = provider.provideDefinition(f.source, {line: 0, character: 2}, token);
  cancelled = true; for (const listener of listeners) listener();
  assert.equal(await pending, undefined); assert.equal(listeners.size, 0); assert.deepEqual(f.calls, []);
});
