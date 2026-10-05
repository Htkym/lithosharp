import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { LspClient } from '../../src/lspClient.js';
import { LspConnection, type LspProcess } from '../../src/lspConnection.js';
import type { LspDiagnosticData } from '../../src/diagnostics.js';

interface Published {
  uri: string;
  version: number;
  lithosharpOpenGeneration: number;
  lithosharpContextGeneration: number;
  diagnostics: unknown[];
}
interface Accepted { wire: Published; diagnostics: LspDiagnosticData[] }
const digest = (file: string): string => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
function input(name: string): string {
  const value = process.env[name];
  assert.ok(value, `Explicit ${name} required; no stale default-bin fallback.`);
  assert.equal(path.resolve(value), value);
  assert.equal(fs.realpathSync.native(value), value);
  return value;
}
function pin(file: string, name: string): string {
  const expected = process.env[name];
  assert.match(expected ?? '', /^[a-f0-9]{64}$/);
  assert.equal(digest(file), expected);
  return expected!;
}
function fixture(): {
  client: LspClient;
  accepted: Accepted[];
  visible: Map<string, LspDiagnosticData[]>;
  wait(version: number, context: number): Promise<Accepted>;
  injectHeldActual(params: Published): void;
  close(): Promise<void>;
} {
  const executable = input('LITHOSHARP_LSP_TEST_DOTNET');
  const dll = input('LITHOSHARP_LSP_TEST_DLL');
  const executableSHA = pin(executable, 'LITHOSHARP_LSP_TEST_DOTNET_SHA256');
  const dllSHA = pin(dll, 'LITHOSHARP_LSP_TEST_DLL_SHA256');
  const cwd = path.resolve(__dirname, '../../../../..');
  const child = spawn(executable, [dll], { cwd, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  const callbacks: ((chunk: Buffer) => void)[] = [];
  const exits: ((code: number | null) => void)[] = [];
  let ended = false, exitCode: number | null = null, forced = false, last: Published | undefined;
  let processError = false;
  child.on('error', () => { processError = true; });
  child.stdin.on('error', () => { processError = true; });
  child.stdout.on('data', (chunk: Buffer) => { for (const callback of callbacks) callback(chunk); });
  child.stderr.resume(); // This test needs no raw error/path export.
  const closed = new Promise<void>((resolve) => child.once('close', (code) => {
    ended = true; exitCode = code; for (const callback of exits) callback(code); resolve();
  }));
  const adapter: LspProcess = {
    stdinWrite: (line) => { child.stdin.write(line); },
    closeStdin: () => { child.stdin.end(); },
    onStdout: (callback) => { callbacks.push(callback); },
    onStderr: () => {},
    onExit: (callback) => { exits.push(callback); if (ended) queueMicrotask(() => callback(exitCode)); },
    killTree: () => { if (!ended) { forced = true; child.kill('SIGKILL'); } },
  };
  // Passive real-wire observer: never answers or changes a server request.
  const observer = new LspConnection(adapter, { onNotification: (method, params) => {
    if (method === 'textDocument/publishDiagnostics') last = JSON.parse(JSON.stringify(params)) as Published;
  } });
  const visible = new Map<string, LspDiagnosticData[]>(), accepted: Accepted[] = [];
  const events = new EventEmitter();
  const client = new LspClient({ serverCommand: [executable, dll], cwd, debounceMs: 50,
    spawn: (command, directory) => { assert.deepEqual(command, [executable, dll]); assert.equal(directory, cwd); return adapter; },
    isTrusted: () => true }, {
    set: (uri, diagnostics) => {
      assert.equal(last?.uri, uri); visible.set(uri, diagnostics);
      const value = { wire: JSON.parse(JSON.stringify(last)) as Published, diagnostics };
      accepted.push(value); events.emit('publication', value);
    },
    delete: (uri) => { visible.delete(uri); }, clear: () => { visible.clear(); },
  });
  return { client, visible, accepted,
    wait: (version, context) => {
      const matches = (row: Accepted): boolean => row.wire.version === version && row.wire.lithosharpContextGeneration === context;
      const existing = accepted.find(matches); if (existing) return Promise.resolve(existing);
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => { events.off('publication', listener); reject(new Error('Current-epoch real client diagnostic missing after20s.')); }, 20_000);
        const listener = (row: Accepted): void => { if (matches(row)) { clearTimeout(timer); events.off('publication', listener); resolve(row); } };
        events.on('publication', listener);
      });
    },
    injectHeldActual: (params) => {
      const body = JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params });
      const chunk = Buffer.from(`Content-Length: ${Buffer.byteLength(body)}\r\n\r\n${body}`);
      for (const callback of callbacks) callback(chunk);
    },
    close: async () => {
      let failure: unknown;
      const deadline = Date.now() + 20_000;
      const awaitClose = async (limit: number): Promise<void> => {
        let timer: NodeJS.Timeout | undefined;
        try { await Promise.race([closed, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Owned real server close deadline exceeded.')), limit); })]); }
        finally { if (timer) clearTimeout(timer); }
      };
      try { child.stdin.end(); } catch (error) { failure = error; }
      try { await awaitClose(18_000); }
      catch (error) {
        failure ??= error;
        try { adapter.killTree(); await awaitClose(Math.max(1, deadline - Date.now())); }
        catch (secondary) { if (failure instanceof Error) failure = new AggregateError([failure, secondary], String(failure) + '; Owned cleanup also failed: ' + String(secondary)); }
      }
      finally { client.stop(); observer.dispose(); }
      if (failure) throw failure; // Forced fallback is always FAIL.
      assert.equal(forced, false); assert.equal(processError, false); assert.equal(exitCode, 0);
      assert.equal(digest(dll), dllSHA); assert.equal(digest(executable), executableSHA);
    },
  };
}
function snapshot(coreVersion = '1.1.0'): object {
  return { schemaVersion: '1.0', projectId: 'docs', projectGeneration: 1, coreVersion,
    collection: 'docs', language: 'markdown', schema: 'document', version: 'v1', locale: 'en',
    acquiredAt: '2026-10-05T00:00:00Z', routes: [] };
}
for (const rejection of ['non-object', 'unreadable', 'incompatible'] as const) {
  test(`real client/server rejected ${rejection} context preserves visible current edits and rejects old epochs`, { timeout: 90_000 }, async () => {
    const f = fixture(); let primary: unknown;
    const uri = 'file:///proj/client-rejected-context.md';
    const text = '---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Initial\n';
    const code = (row: Accepted, value: string): boolean => row.diagnostics.some((item) => item.code === value);
    try {
      await f.client.start();
      f.client.sendProjectContext('docs', ['file:///proj'], snapshot());
      f.client.didOpen({ uri, languageId: 'markdown', version: 1, text });
      const initial = await f.wait(1, 1); assert.equal(code(initial, 'LSC101'), true);
      console.log('PROTOCOL_CHECKPOINT ' + JSON.stringify({ rejection, stage: 'initial', version: initial.wire.version, context: initial.wire.lithosharpContextGeneration, LSC101: code(initial, 'LSC101') }));
      const invalid = rejection === 'non-object' ? 'invalid'
        : rejection === 'unreadable' ? { schemaVersion: '999.0', projectId: 'docs' } : snapshot('9999.0.0');
      f.client.sendProjectContext('docs', ['file:///proj'], invalid);
      const count = f.accepted.length; f.injectHeldActual(initial.wire);
      assert.equal(f.accepted.length, count); assert.equal(f.visible.has(uri), false);
      f.client.didChange(uri, 2, text + 'See [^missing].\n');
      await f.client.requestSymbols(uri); // Flush the real debounced edit; no sleeps or fake response.
      const current = await f.wait(2, 2);
      assert.equal(code(current, 'LSC101'), true); assert.equal(code(current, 'LIT001'), true);
      assert.ok(f.visible.get(uri)?.some((item) => item.code === 'LIT001'));
      console.log('PROTOCOL_CHECKPOINT ' + JSON.stringify({ rejection, stage: 'current', version: current.wire.version, context: current.wire.lithosharpContextGeneration, LSC101: code(current, 'LSC101'), LIT001: code(current, 'LIT001') }));
      f.client.sendProjectContext('docs', ['file:///proj'], null);
      const beforeAccepted = f.accepted.length; f.injectHeldActual(current.wire);
      assert.equal(f.accepted.length, beforeAccepted); assert.equal(f.visible.has(uri), false);
      const withdrawn = await f.wait(2, 3);
      assert.equal(code(withdrawn, 'LSC101'), false); assert.equal(code(withdrawn, 'LIT001'), true);
      console.log('PROTOCOL_CHECKPOINT ' + JSON.stringify({ rejection, stage: 'withdrawn', version: withdrawn.wire.version, context: withdrawn.wire.lithosharpContextGeneration, LSC101: code(withdrawn, 'LSC101'), LIT001: code(withdrawn, 'LIT001') }));
    } catch (error) { primary = error; }
    try { await f.close(); }
    catch (error) { if (primary instanceof Error) primary = new AggregateError([primary, error], String(primary) + '; Owned cleanup also failed: ' + String(error)); else if (!primary) primary = error; }
    if (primary) throw primary;
  });
}
