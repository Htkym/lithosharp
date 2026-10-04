import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { UntrustedWorkspaceError } from '../../src/trust.js';
import { createFakeVscode, installFakeVscode } from './fakeVscode.js';
import { fixturePath } from './helpers.js';

// eslint-disable-next-line @typescript-eslint/no-require-imports
function loadExtension(fake: ReturnType<typeof createFakeVscode>): typeof import('../../src/extension.js') {
  installFakeVscode(fake);
  // Fresh module per fake: the vscode binding happens at load time.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const key = require.resolve('../../src/extension.js');
  delete require.cache[key];
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  return require('../../src/extension.js') as typeof import('../../src/extension.js');
}

function folder(fsPath: string, name: string): { uri: { fsPath: string }; name: string } {
  return { uri: { fsPath }, name };
}

test('untrusted handler call executes nothing', async () => {
  const fake = createFakeVscode();
  const { selectProjectHandler } = loadExtension(fake);
  const calls: unknown[] = [];
  await assert.rejects(
    selectProjectHandler({
      isTrusted: false,
      folders: [],
      showQuickPick: async () => undefined,
      saveSelection: async () => {},
      cliPathSetting: '',
      envPath: '',
      probe: {
        calls: calls as never,
        run: async () => {
          calls.push(1);
          return { exit: 0, stdout: '' };
        },
      },
    }),
    UntrustedWorkspaceError,
  );
  assert.equal(calls.length, 0);
});

test('single project auto-selects and reports version', async () => {
  const fake = createFakeVscode();
  const { selectProjectHandler } = loadExtension(fake);
  const saved: { key: string; projectPath: string }[] = [];
  const calls: { command: string[]; cwd: string | undefined }[] = [];
  const bin = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'lithosharp-ext-'));
  const exe = path.join(bin, process.platform === 'win32' ? 'lithosharp.exe' : 'lithosharp');
  await fs.promises.writeFile(exe, '');
  const picked = await selectProjectHandler({
    isTrusted: true,
    folders: [folder(fixturePath('single'), 'single')],
    showQuickPick: async () => {
      throw new Error('must not prompt for one project');
    },
    saveSelection: async (key, projectPath) => {
      saved.push({ key, projectPath });
    },
    cliPathSetting: exe,
    envPath: '',
    probe: {
      calls,
      run: async (command, cwd) => {
        calls.push({ command, cwd });
        return { exit: 0, stdout: 'lithosharp 1.1.0' };
      },
    },
  });
  assert.equal(picked?.candidate.displayName, 'app');
  assert.equal(picked?.version, '1.1.0');
  assert.equal(saved.length, 1);
  assert.ok(saved[0]!.key.includes('single'));
  assert.equal(calls.length, 1);
});

test('same-name projects prompt with workspace identity', async () => {
  const fake = createFakeVscode();
  fake.pickedIndex = 1;
  const { selectProjectHandler } = loadExtension(fake);
  let shown: { label: string; description: string }[] = [];
  const picked = await selectProjectHandler({
    isTrusted: true,
    folders: [folder(fixturePath('multi', 'a'), 'a'), folder(fixturePath('multi', 'b'), 'b')],
    showQuickPick: async (items) => {
      shown = items as { label: string; description: string }[];
      return items[1];
    },
    saveSelection: async () => {},
    cliPathSetting: '',
    envPath: '',
  });
  assert.equal(shown.length, 2);
  assert.equal(shown[0]!.label, shown[1]!.label);
  assert.notEqual(shown[0]!.description, shown[1]!.description);
  assert.equal(picked?.candidate.workspaceName, 'b');
});

test('activate wires disposables and deactivate cleans up', async () => {
  const fake = createFakeVscode();
  fake.folders = [];
  const { activate, deactivate } = loadExtension(fake);
  const subscriptions: { dispose(): void }[] = [];
  activate({ subscriptions } as never);
  assert.ok(subscriptions.length >= 12);
  for (const id of ['lithosharp.selectProject', 'lithosharp.build', 'lithosharp.startServer', 'lithosharp.stopServer', 'lithosharp.inspectSite', 'lithosharp.restartServer', 'lithosharp.openPreview', 'lithosharp.refreshPreview', 'lithosharp.openInBrowser']) {
    assert.ok(fake.commands.has(id), `missing command ${id}`);
  }
  deactivate();
  for (const subscription of subscriptions) {
    subscription.dispose();
  }
});

test('untrusted build and serve commands never execute', async () => {
  const fake = createFakeVscode();
  fake.isTrusted = false;
  const { activate } = loadExtension(fake);
  const subscriptions: { dispose(): void }[] = [];
  activate({ subscriptions } as never);
  for (const id of ['lithosharp.build', 'lithosharp.startServer', 'lithosharp.stopServer', 'lithosharp.inspectSite', 'lithosharp.restartServer']) {
    await assert.rejects(fake.commands.get(id)!(), UntrustedWorkspaceError);
  }
});

test('configuration change refreshes the project state', async () => {
  const fake = createFakeVscode();
  fake.isTrusted = false;
  const { activate } = loadExtension(fake);
  const subscriptions: { dispose(): void }[] = [];
  activate({ subscriptions } as never);
  assert.equal(fake.configListeners.length, 1);
  fake.isTrusted = true;
  fake.folders = [folder(fixturePath('single'), 'single')];
  fake.configListeners[0]!({ affectsConfiguration: (section) => section === 'lithosharp' });
  const watch = Date.now();
  while (Date.now() - watch < 5000) {
    if (fake.statusText.some((text) => text.includes('select project'))) {
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  assert.ok(fake.statusText.some((text) => text.includes('select project')));
});


/** A real LspClient/connection, with only worker discovery and process I/O controlled. */
async function delayedActivation(options: { initializeError?: boolean; strictOutput?: boolean; holdSymbols?: boolean; holdFirstInitialize?: boolean; holdInitializeCommand?: string; exitOnClose?: boolean; onSymbolResponse?: () => void } = {}) {
  const fake = createFakeVscode();
  if (options.strictOutput) {
    const create = fake.window.createOutputChannel;
    fake.window.createOutputChannel = (name) => {
      const channel = create(name);
      let closed = false;
      return { appendLine: (line) => { if (closed) throw new Error('Channel has been closed'); channel.appendLine(line); },
        dispose: () => { closed = true; channel.dispose(); } };
    };
  }
  fake.settings['languageServerPath'] = '/first-language-server';
  const document = { uri: { fsPath: '/note.md', toString: () => 'file:///note.md' },
    languageId: 'markdown', version: 1, isClosed: false, getText: () => '# Heading\n' };
  fake.workspaceApi.textDocuments.push(document);
  let closeDocument: (value: typeof document) => void = () => {};
  const events = fake.workspaceApi as unknown as Record<string, unknown>;
  events['onDidCloseTextDocument'] = (listener: typeof closeDocument) => {
    closeDocument = listener;
    return { disposed: false, dispose() {} };
  };
  const heldInitialization: (() => void)[] = [];
  let provider: { provideDocumentSymbols(document: unknown, token: unknown): Promise<unknown[]> } | undefined;
  fake.languages.registerDocumentSymbolProvider = (_selector, value) => {
    provider = value as typeof provider;
    return { disposed: false, dispose() {} };
  };
  // Mutable CommonJS module bindings keep the actual editor/client call flow.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const worker = require('../../src/worker.js') as typeof import('../../src/worker.js');
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const processModule = require('../../src/process.js') as typeof import('../../src/process.js');
  const originalResolve = worker.resolveWorker;
  const originalSpawn = processModule.spawnProcess;
  let entered!: () => void;
  const discoveryEntered = new Promise<void>((resolve) => { entered = resolve; });
  let release!: () => void;
  const discovery = new Promise<void>((resolve) => { release = resolve; });
  let discoveries = 0;
  worker.resolveWorker = async () => {
    if (++discoveries === 1) { entered(); await discovery; }
    return { source: 'none', directory: '', lockHash: null, ready: false };
  };
  const spawns: { command: string[]; messages: { id?: number; method?: string }[]; closed: boolean }[] = [];
  processModule.spawnProcess = (command) => {
    const record = { command: [...command], messages: [] as { id?: number; method?: string }[], closed: false };
    spawns.push(record);
    const stdout: ((chunk: Buffer) => void)[] = [];
    const exits: ((code: number | null) => void)[] = [];
    return {
      pid: undefined,
      stdinWrite: (frame) => {
        const message = JSON.parse(frame.slice(frame.indexOf('{'))) as { id?: number; method?: string };
        record.messages.push(message);
        if (message.id !== undefined) {
          const result = message.method === 'initialize' ? { capabilities: {} } : [{
            name: 'Heading', kind: 3, range: { start: { line: 0, character: 0 }, end: { line: 0, character: 9 } },
            selectionRange: { start: { line: 0, character: 2 }, end: { line: 0, character: 9 } }, children: [],
          }];
          const body = JSON.stringify(options.initializeError && message.method === 'initialize'
            ? { jsonrpc: '2.0', id: message.id, error: { code: -32603, message: 'controlled initialize rejection' } }
            : { jsonrpc: '2.0', id: message.id, result });
          const response = Buffer.from('Content-Length: ' + Buffer.byteLength(body) + '\r\n\r\n' + body);
          const emit = () => { for (const listener of stdout) listener(response); };
          if ((options.holdFirstInitialize && record === spawns[0] || options.holdInitializeCommand === command[0]) && message.method === 'initialize'
            || options.holdSymbols && message.method === 'textDocument/documentSymbol') {
            heldInitialization.push(emit);
            return;
          }
          emit();
          if (message.method === 'textDocument/documentSymbol') options.onSymbolResponse?.();
        }
      },
      closeStdin: () => { record.closed = true; if (options.exitOnClose !== false) for (const listener of exits) listener(0); },
      onStdout: (fn) => { stdout.push(fn); }, onStderr: () => {},
      onExit: (fn) => { exits.push(fn); }, killTree: () => {},
    };
  };
  const subscriptions: { dispose(): void }[] = [];
  try {
    loadExtension(fake).activate({ subscriptions, extensionPath: '/extension', globalStorageUri: { fsPath: '/storage' } } as never);
    await discoveryEntered;
  } catch (error) {
    release(); worker.resolveWorker = originalResolve; processModule.spawnProcess = originalSpawn;
    throw error;
  }
  return { fake, document, spawns, release, provider: provider!,
    dispose: () => { for (const item of subscriptions) item.dispose(); },
    closeDocument: () => { document.isClosed = true; fake.workspaceApi.textDocuments.length = 0; closeDocument(document); },
    cleanup: () => {
      release();
      for (const item of subscriptions) item.dispose();
      for (const emit of heldInitialization) emit();
      worker.resolveWorker = originalResolve;
      processModule.spawnProcess = originalSpawn;
    } };
}

test('restart during worker discovery replaces the pending client using current settings', async () => {
  const host = await delayedActivation();
  try {
    host.fake.settings['languageServerPath'] = '/replacement-language-server';
    const restarted = host.fake.commands.get('lithosharp.restartServer')!();
    host.release();
    await restarted;
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.deepEqual(host.spawns.map((spawn) => spawn.command), [['/replacement-language-server']]);
    assert.ok(host.spawns[0]!.messages.some((message) => message.method === 'textDocument/didOpen'),
      'Replacement must reopen the current document.');
  } finally { host.cleanup(); }
});

test('symbol cancellation during startup sends no symbol RPC after discovery completes', async () => {
  const host = await delayedActivation();
  try {
    let cancelled = false;
    const listeners = new Set<() => void>();
    const token = {
      get isCancellationRequested() { return cancelled; },
      onCancellationRequested: (listener: () => void) => {
        listeners.add(listener);
        return { dispose: () => { listeners.delete(listener); } };
      },
    };
    const pending = host.provider.provideDocumentSymbols(host.document, token);
    cancelled = true;
    for (const listener of listeners) listener();
    host.release();
    assert.deepEqual(await pending, []);
    assert.equal(host.spawns.flatMap((spawn) => spawn.messages)
      .filter((message) => message.method === 'textDocument/documentSymbol').length, 0);
    assert.equal(listeners.size, 0);
  } finally { host.cleanup(); }
});


test('symbols completed immediately before cancellation are not returned to the editor', async () => {
  let cancelled = false;
  const listeners = new Set<() => void>();
  const host = await delayedActivation({ onSymbolResponse: () => {
    cancelled = true;
    for (const listener of listeners) listener();
  } });
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    const token = {
      get isCancellationRequested() { return cancelled; },
      onCancellationRequested: (listener: () => void) => {
        listeners.add(listener);
        return { dispose: () => { listeners.delete(listener); } };
      },
    };
    assert.deepEqual(await host.provider.provideDocumentSymbols(host.document, token), []);
    assert.equal(host.spawns[0]!.messages.filter((message) => message.method === 'textDocument/documentSymbol').length, 1);
    assert.equal(listeners.size, 0);
  } finally { host.cleanup(); }
});

test('initialization rejection stops its live client before a subsequent attempt', async () => {
  const host = await delayedActivation({ initializeError: true });
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.ok(host.fake.lines.some((line) => line.includes('controlled initialize rejection')));
    assert.equal(host.spawns[0]!.closed, true, 'Failed initialization must not abandon a live process.');
  } finally { host.cleanup(); }
});

test('disposing activation stops the active language server', async () => {
  const host = await delayedActivation();
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns.length, 1);
    host.dispose();
    assert.equal(host.spawns[0]!.closed, true);
  } finally { host.cleanup(); }
});

test('disposing activation during discovery suppresses late startup and future requests', async () => {
  const host = await delayedActivation();
  try {
    host.dispose();
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns.length, 0, 'Discovery must not spawn a process after extension disposal.');
    const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) };
    assert.deepEqual(await host.provider.provideDocumentSymbols(host.document, token), []);
    assert.equal(host.spawns.length, 0);
  } finally { host.cleanup(); }
});


test('restart closes an initializing client before waiting for its handshake to settle', async () => {
  const host = await delayedActivation({ holdFirstInitialize: true });
  let restarted: Promise<unknown> | undefined;
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns.length, 1);
    assert.ok(host.spawns[0]!.messages.some((message) => message.method === 'initialize'));
    restarted = host.fake.commands.get('lithosharp.restartServer')!();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns[0]!.closed, true, 'Pending initialize must not prevent restart from stopping its process.');
    await restarted;
    assert.equal(host.spawns.length, 2);
    assert.ok(host.spawns[1]!.messages.some((message) => message.method === 'textDocument/didOpen'));
  } finally {
    host.cleanup();
    await restarted;
  }
});


test('cancelled symbol provider settles before discovery while shared startup remains alive', async () => {
  const host = await delayedActivation();
  let pending: Promise<unknown[]> | undefined;
  try {
    let cancelled = false;
    const listeners = new Set<() => void>();
    const token = { get isCancellationRequested() { return cancelled; },
      onCancellationRequested: (listener: () => void) => {
        listeners.add(listener); return { dispose: () => { listeners.delete(listener); } };
      } };
    let settled = false;
    pending = host.provider.provideDocumentSymbols(host.document, token).then((result) => { settled = true; return result; });
    cancelled = true;
    for (const listener of listeners) listener();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(settled, true, 'Cancellation must settle the caller without waiting for shared startup.');
    assert.deepEqual(await pending, []);
    assert.equal(listeners.size, 0);
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns.length, 1, 'Caller cancellation must not cancel shared startup.');
    assert.ok(host.spawns[0]!.messages.some((message) => message.method === 'textDocument/didOpen'));
  } finally { host.cleanup(); await pending; }
});

test('restart settles initializing requests locally before the old process exits', async () => {
  const host = await delayedActivation({ holdFirstInitialize: true, exitOnClose: false });
  let restarted: Promise<unknown> | undefined;
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    let settled = false;
    restarted = host.fake.commands.get('lithosharp.restartServer')!().then((result) => { settled = true; return result; });
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns[0]!.closed, true);
    assert.equal(settled, true, 'Restart must not depend on the stopped process exit event.');
    await restarted;
    assert.equal(host.spawns.length, 2);
  } finally { host.cleanup(); await restarted; }
});

test('closing a document during discovery suppresses its deferred didOpen', async () => {
  const host = await delayedActivation();
  try {
    host.closeDocument();
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(host.spawns.length, 1);
    assert.ok(!host.spawns[0]!.messages.some((message) => message.method === 'textDocument/didOpen'),
      'A closed document must not be retained by the server after deferred startup.');
  } finally { host.cleanup(); }
});


test('disposal during initialize settles callers without writing to disposed output', async () => {
  const host = await delayedActivation({ holdFirstInitialize: true, exitOnClose: false, strictOutput: true });
  let pending: Promise<unknown[]> | undefined;
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) };
    pending = host.provider.provideDocumentSymbols(host.document, token);
    host.dispose();
    assert.deepEqual(await pending, []);
    assert.equal(host.spawns[0]!.closed, true);
  } finally { host.cleanup(); await pending?.catch(() => {}); }
});


test('restart during discovery suppresses the obsolete client before its held initialize', async () => {
  const host = await delayedActivation({ holdInitializeCommand: '/first-language-server', exitOnClose: false });
  let restarted: Promise<unknown> | undefined;
  try {
    host.fake.settings['languageServerPath'] = '/replacement-language-server';
    restarted = host.fake.commands.get('lithosharp.restartServer')!();
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.deepEqual(host.spawns.map((item) => item.command), [['/replacement-language-server']],
      'Superseded discovery must not create a client that can block restart in initialize.');
    await restarted;
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.ok(host.spawns[0]!.messages.some((message) => message.method === 'textDocument/didOpen'));
  } finally { host.cleanup(); await restarted; }
});


test('disposal settles ready pending symbols without writing to disposed output', async () => {
  const host = await delayedActivation({ holdSymbols: true, exitOnClose: false, strictOutput: true });
  let pending: Promise<unknown[]> | undefined;
  try {
    host.release();
    await new Promise<void>((resolve) => setImmediate(resolve));
    const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) };
    pending = host.provider.provideDocumentSymbols(host.document, token);
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.ok(host.spawns[0]!.messages.some((message) => message.method === 'textDocument/documentSymbol'));
    host.dispose();
    assert.deepEqual(await pending, []);
    assert.equal(host.spawns[0]!.closed, true);
  } finally { host.cleanup(); await pending?.catch(() => {}); }
});
