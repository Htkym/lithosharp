import assert from 'node:assert/strict';
import test from 'node:test';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {LspClient} from '../../src/lspClient.js';
import type {LspProcess} from '../../src/lspConnection.js';
import {textHash} from '../../src/referenceFeatures.js';

function fixture(editing = true) {
  const messages: {id?: string; method: string; params: Record<string, unknown>}[] = [];
  const stdout: ((bytes: Buffer) => void)[] = [], exits: ((code: number | null) => void)[] = [];
  const reply = (id: string, result: unknown) => {
    const body = JSON.stringify({jsonrpc: '2.0', id, result});
    for (const listener of stdout) listener(Buffer.from(`Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`));
  };
  const child: LspProcess = {
    stdinWrite(frame) { const message = JSON.parse(frame.slice(frame.indexOf('\r\n\r\n') + 4)) as typeof messages[number];
      messages.push(message); if (message.method === 'initialize') reply(message.id!, {capabilities: {}}); },
    onStdout(listener) { stdout.push(listener); }, onStderr() {}, onExit(listener) { exits.push(listener); }, closeStdin() {},
    killTree() { for (const listener of exits) listener(0); },
  };
  const client = new LspClient({serverCommand: ['server'], cwd: '.', spawn: () => child, referenceEditing: editing,
    isTrusted: () => true}, {set() {}, delete() {}, clear() {}});
  const uri = pathToFileURL(path.resolve('source.md')).href, other = pathToFileURL(path.resolve('other.md')).href;
  client.didOpen({uri, languageId: 'markdown', version: 1, text: '# Old'});
  client.didOpen({uri: other, languageId: 'markdown', version: 2, text: '[go](source.md#old)'});
  return {client, messages, reply, uri, other};
}
const scope = {collection: 'guide', version: 'current', locale: 'en'};
test('guarded command capabilities are text-only transactional and do not promise file participants', async () => {
  for (const editing of [false, true]) {
    const f = fixture(editing); await f.client.start();
    try {
      const capabilities = f.messages[0]!.params['capabilities'] as {workspace?: {workspaceEdit: {documentChanges: boolean; failureHandling: string}; fileOperations: {willRename: boolean}}};
      assert.equal(capabilities.workspace?.workspaceEdit.documentChanges, editing ? true : undefined);
      if (editing) { assert.equal(capabilities.workspace?.workspaceEdit.failureHandling, 'textOnlyTransactional'); assert.equal(capabilities.workspace?.fileOperations.willRename, false); }
      else await assert.rejects(f.client.requestRename(f.uri, {}, scope), /not enabled/);
    } finally { f.client.stop(); }
  }
});
test('reference requests flush every current buffer, pass explicit tuple and expected source identity', async () => {
  const f = fixture(); await f.client.start();
  try {
    f.client.didChange(f.other, 3, '[changed](source.md#old)');
    const pending = f.client.requestRenamePreview(f.uri, {position: {line: 0, character: 2}, newName: 'New'}, scope);
    const request = f.messages.at(-1)!;
    assert.equal(request.method, 'lithosharp/previewRename');
    assert.deepEqual(request.params['referenceScope'], scope);
    assert.deepEqual(request.params['textDocument'], {uri: f.uri, version: 1});
    assert.equal(request.params['expectedSourceTextHash'], textHash('# Old'));
    assert.ok(f.messages.some(message => message.method === 'textDocument/didChange'));
    f.reply(request.id!, {preview: 'current'});
    assert.deepEqual(await pending, {preview: 'current'});
  } finally { f.client.stop(); }
});
test('held reference results are discarded on another document change, context replacement and reopen', async () => {
  for (const change of ['other', 'context', 'reopen'] as const) {
    const f = fixture(); await f.client.start();
    try {
      const guard = f.client.referenceGuard();
      const pending = f.client.requestRename(f.uri, {position: {line: 0, character: 2}, newName: 'New'}, scope);
      const request = f.messages.at(-1)!;
      if (change === 'other') f.client.didChange(f.other, 3, 'changed');
      else if (change === 'context') f.client.sendProjectContext('site', [], null);
      else { f.client.didClose(f.uri); f.client.didOpen({uri: f.uri, languageId: 'markdown', version: 1, text: '# Old'}); }
      assert.equal(guard(), false);
      f.reply(request.id!, {documentChanges: []});
      assert.equal(await pending, undefined);
    } finally { f.client.stop(); }
  }
});
test('definition, references and prepareRename use the selected source tuple and owned ranges', async () => {
  const f = fixture(); await f.client.start();
  const range = {start: {line: 0, character: 2}, end: {line: 0, character: 5}};
  try {
    const definition = f.client.requestDefinition(f.uri, {line: 0, character: 2}, scope);
    f.reply(f.messages.at(-1)!.id!, [{uri: f.uri, range}]);
    assert.deepEqual(await definition, [{uri: f.uri, range}]);
    const references = f.client.requestReferences(f.uri, {line: 0, character: 2}, scope, true);
    assert.deepEqual(f.messages.at(-1)!.params['context'], {includeDeclaration: true});
    assert.deepEqual(f.messages.at(-1)!.params['referenceScope'], scope);
    f.reply(f.messages.at(-1)!.id!, [{uri: 'file:///unopened.md', range}]);
    assert.deepEqual(await references, []);
    const prepare = f.client.requestPrepareRename(f.uri, {line: 0, character: 2}, scope);
    f.reply(f.messages.at(-1)!.id!, {range, placeholder: 'Old'});
    assert.deepEqual(await prepare, {range, placeholder: 'Old'});
  } finally { f.client.stop(); }
});
