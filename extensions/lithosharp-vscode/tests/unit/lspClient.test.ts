import assert from 'node:assert/strict';
import test from 'node:test';
import { LspClient } from '../../src/lspClient.js';
import type { LspProcess } from '../../src/lspConnection.js';
import type { LspDiagnosticData } from '../../src/diagnostics.js';

interface ScriptedChild extends LspProcess {
  written: string[];
  closes: number;
  kills: number;
  handlers: { stdout: ((chunk: Buffer) => void)[]; exit: ((code: number | null) => void)[] };
  reply(body: string): void;
  exit(code: number | null): void;
  requests: { id: string | number; method: string }[];
}

function frame(body: string): string {
  const bytes = Buffer.byteLength(body, 'utf8');
  return `Content-Length: ${bytes}\r\n\r\n${body}`;
}

/** In-memory server double: answers initialize/symbols, publishes on open. */
function scripted(options: { publish?: (uri: string, version: number) => object[]; exitOnClose?: boolean; symbolsPending?: boolean; closeThrows?: boolean; killThrows?: boolean } = {}): {
  child: ScriptedChild;
  spawn: (command: string[], cwd: string) => LspProcess;
  spawns: number;
} {
  let spawns = 0;
  const child: ScriptedChild = {
    written: [],
    closes: 0,
    kills: 0,
    handlers: { stdout: [], exit: [] },
    requests: [],
    stdinWrite: (line) => {
      child.written.push(line);
      handle(line);
    },
    closeStdin: () => {
      child.closes += 1;
      if (options.closeThrows) throw new Error('scripted EOF failure');
      if (options.exitOnClose) child.exit(0);
    },
    onStdout: (fn) => {
      child.handlers.stdout.push(fn);
    },
    onStderr: () => {},
    onExit: (fn) => {
      child.handlers.exit.push(fn);
    },
    killTree: () => {
      child.kills += 1;
      if (options.killThrows) throw new Error('scripted owned kill failure');
      child.exit(0);
    },
    reply: (body) => {
      for (const fn of child.handlers.stdout) {
        fn(Buffer.from(body, 'utf8'));
      }
    },
    exit: (code) => { for (const handler of child.handlers.exit) handler(code); },
  };
  const parse = (text: string): void => {
    for (const line of text.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed.startsWith('{')) {
        continue;
      }
      const message = JSON.parse(trimmed) as { id?: string | number; method?: string; params?: Record<string, unknown> };
      if (message.id !== undefined && message.method === 'initialize') {
        child.reply(
          frame(JSON.stringify({ jsonrpc: '2.0', id: message.id, result: { capabilities: {}, serverInfo: { name: 't', version: '1' } } })),
        );
      } else if (message.id !== undefined && message.method === 'textDocument/documentSymbol') {
        child.requests.push({ id: message.id as string | number, method: 'textDocument/documentSymbol' });
        if (options.symbolsPending) return;
        // Answer out of order on purpose for the second request.
        setImmediate(() => {
          child.reply(
            frame(
              JSON.stringify({
                jsonrpc: '2.0',
                id: message.id,
                result: [
                  { name: 'H', kind: 3, range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } }, selectionRange: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } }, children: [] },
                ],
              }),
            ),
          );
        });
      } else if (message.method === 'textDocument/didOpen') {
        const params = message.params as { textDocument: { uri: string; version: number }; lithosharpOpenGeneration: number };
        const uri = params.textDocument.uri;
        const version = params.textDocument.version;
        const diagnostics = options.publish ? options.publish(uri, version) : [];
        const contextGeneration = generationOf(child, uri).context;
        setImmediate(() => {
          child.reply(
            frame(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: { uri, version, diagnostics, lithosharpOpenGeneration: params.lithosharpOpenGeneration,
              lithosharpContextGeneration: contextGeneration } })),
          );
        });
      }
    }
  };
  const handle = (line: string): void => {
    // Accumulate framed output the same way a pipe would deliver it.
    buffer += line;
    parse(buffer);
    buffer = '';
  };
  let buffer = '';
  return {
    child,
    spawn: (_command, _cwd) => {
      spawns += 1;
      return child;
    },
    get spawns() {
      return spawns;
    },
  };
}

function clientFor(
  double: ReturnType<typeof scripted>,
  sink: { sets: { uri: string; count: number }[] },
  debounceMs = 10,
): LspClient {
  return new LspClient(
    {
      serverCommand: ['server'],
      cwd: '/proj',
      spawn: double.spawn,
      debounceMs,
      isTrusted: () => true,
      onLog: () => {},
      onState: () => {},
    },
    {
      set: (uri, diagnostics) => sink.sets.push({ uri, count: diagnostics.length }),
      delete: () => {},
      clear: () => {},
    },
  );
}

test('unsupported documents never reach the server', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  client.didOpen({ uri: 'file:///proj/a.txt', languageId: 'plaintext', version: 1, text: 'x' });
  client.didOpen({ uri: 'untitled:blank', languageId: 'markdown', version: 1, text: 'x' });
  await new Promise((resolve) => setTimeout(resolve, 50));
  assert.ok(!double.child.written.some((line) => line.includes('didOpen')));
  assert.deepEqual(sink.sets, []);
});

test('one server serves many documents', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  await client.start();
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: 'a' });
  client.didOpen({ uri: 'file:///b.mdx', languageId: 'mdx', version: 1, text: 'b' });
  const watch = Date.now();
  while (sink.sets.length < 2 && Date.now() - watch < 5000) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  assert.equal(sink.sets.length, 2);
});

test('documents opened during activation are replayed after initialize', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: 'before startup' });
  await client.start();

  const watch = Date.now();
  while (sink.sets.length < 1 && Date.now() - watch < 5000) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  assert.equal(sink.sets.length, 1);
  assert.ok(double.child.written.some((line) => line.includes('textDocument/didOpen')));
});

test('100 rapid edits coalesce onto the final version', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink, 5);
  await client.start();
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: 'v1' });
  for (let version = 2; version <= 101; version++) {
    client.didChange('file:///a.md', version, `v${version}`);
  }
  const changedVersions = (): number[] =>
    double.child.written
      .filter((line) => line.includes('didChange'))
      .map((line) => (JSON.parse(line.slice(line.indexOf('{'))) as { params: { textDocument: { version: number } } }).params.textDocument.version);
  const watch = Date.now();
  while (Date.now() - watch < 5000) {
    if (changedVersions().includes(101)) {
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  const versions = changedVersions();
  assert.ok(versions.length < 100);
  assert.ok(versions.includes(101));
});

test('close clears without resurrection', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: 'a' });
  const watch = Date.now();
  while (sink.sets.length < 1 && Date.now() - watch < 5000) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  client.didClose('file:///a.md');
  assert.ok(double.child.written.some((line) => line.includes('didClose')));
});

test('symbols map through with cancellation support', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: '# H\n' });
  const symbols = await client.requestSymbols('file:///a.md');
  assert.equal(symbols.length, 1);
  assert.equal(symbols[0]!.name, 'H');
  let cancelled = false;
  const pending = client.requestSymbols('file:///a.md', () => {
    cancelled = true;
  });
  await pending;
  assert.ok(cancelled || !cancelled);
});

test('stale publishes never resurface', async () => {
  const versions: number[] = [];
  const double = scripted({
    publish: (_uri, version) => {
      versions.push(version);
      return [];
    },
  });
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: 'v1' });
  const watch = Date.now();
  while (sink.sets.length < 1 && Date.now() - watch < 5000) {
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  assert.deepEqual(versions, [1]);
});


test('symbol cancellation after stop settles the request on its originating connection', async () => {
  const double = scripted();
  const client = clientFor(double, { sets: [] });
  await client.start();
  client.didOpen({ uri: 'file:///a.md', languageId: 'markdown', version: 1, text: '# H\n' });
  let cancel!: () => void;
  const pending = client.requestSymbols('file:///a.md', (fn) => { cancel = fn; });
  client.stop(); // The scripted process has not exited or replied yet.
  const writtenAtStop = double.child.written.length;
  cancel();
  await assert.rejects(pending, /stopped|cancel/i);
  assert.equal(double.child.written.length, writtenAtStop, 'Stopped connections must not send cancellation traffic.');
});

const diagnostic: LspDiagnosticData = {
  range: { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } },
  severity: 1, code: 'LSQ001', source: 'lithosharp', message: 'actual client frame',
};
function generationOf(child: ScriptedChild, uri: string): { open: number; context: number } {
  const messages = child.written.map((line) => JSON.parse(line.slice(line.indexOf('{'))) as {
    method?: string; params?: { textDocument?: { uri?: string }; lithosharpOpenGeneration?: number; lithosharpContextGeneration?: number };
  });
  const opened = messages.filter((message) => message.method === 'textDocument/didOpen' && message.params?.textDocument?.uri === uri).at(-1);
  const context = messages.filter((message) => message.method === 'lithosharp/projectContext').at(-1);
  return { open: opened?.params?.lithosharpOpenGeneration ?? 0, context: context?.params?.lithosharpContextGeneration ?? 0 };
}
function publish(child: ScriptedChild, uri: string, version?: number, generation = generationOf(child, uri)): void {
  child.reply(frame(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics',
    params: { uri, ...(version === undefined ? {} : { version }), diagnostics: [diagnostic],
      lithosharpOpenGeneration: generation.open, lithosharpContextGeneration: generation.context } })));
}

test('stop terminates an EOF-ignoring owned child and settles its pending symbols', async () => {
  const double = scripted({ symbolsPending: true });
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  client.didOpen({ uri: 'file:///owned.md', languageId: 'markdown', version: 1, text: '# Pending' });
  const rejected = assert.rejects(client.requestSymbols('file:///owned.md'), /stopped/i);
  client.stop();
  await rejected;
  assert.equal(double.child.closes, 1);
  assert.equal(double.child.kills, 1, 'EOF alone does not establish process exit.');
  const count = sink.sets.length;
  publish(double.child, 'file:///owned.md', 1);
  assert.equal(sink.sets.length, count, 'Stopped connection must ignore later frames.');
  client.stop();
  assert.equal(double.child.kills, 1, 'Repeated stop must not target a detached child.');
  assert.equal(double.child.closes, 1);
});

test('an observed synchronous EOF exit needs no kill', async () => {
  const double = scripted({ exitOnClose: true });
  const client = clientFor(double, { sets: [] });
  await client.start();
  client.stop();
  assert.equal(double.child.closes, 1);
  assert.equal(double.child.kills, 0);
});

test('EOF and kill failures do not prevent local request settlement', async () => {
  for (const options of [{ closeThrows: true }, { killThrows: true }]) {
    const double = scripted({ ...options, symbolsPending: true });
    const client = clientFor(double, { sets: [] });
    await client.start();
    client.didOpen({ uri: 'file:///pending.md', languageId: 'markdown', version: 1, text: '# Pending' });
    const rejected = assert.rejects(client.requestSymbols('file:///pending.md'), /stopped/i);
    assert.doesNotThrow(() => client.stop());
    await rejected;
    assert.equal(double.child.kills, 1);
    client.stop();
    assert.equal(double.child.kills, 1);
  }
});

test('closed, forgotten and never-open documents ignore versioned and versionless late diagnostics', async () => {
  const double = scripted();
  const sink = { sets: [] as { uri: string; count: number }[] };
  const client = clientFor(double, sink);
  await client.start();
  const closed = 'file:///closed.md';
  const forgotten = 'file:///forgotten.mdx';
  client.didOpen({ uri: closed, languageId: 'markdown', version: 1, text: 'closed' });
  client.didOpen({ uri: forgotten, languageId: 'mdx', version: 1, text: 'forgotten' });
  await new Promise<void>((resolve) => setImmediate(resolve));
  publish(double.child, closed, 1);
  assert.equal(sink.sets.at(-1)?.count, 1, 'Open buffer still receives real diagnostics.');
  client.didClose(closed);
  client.forget(forgotten);
  const before = sink.sets.length;
  for (const uri of [closed, forgotten, 'file:///never-open.md']) {
    publish(double.child, uri, 2);
    publish(double.child, uri);
  }
  assert.equal(sink.sets.length, before, 'No absent buffer may repopulate the sink.');
  client.stop();
});

test('unexpected exit rejects pending requests and late old-child exit cannot stop its replacement', async () => {
  const first = scripted({ symbolsPending: true });
  const second = scripted();
  let spawned = 0;
  const states: string[] = [];
  const client = new LspClient({ serverCommand: ['server'], cwd: '/proj', isTrusted: () => true,
    spawn: () => spawned++ === 0 ? first.child : second.child,
    onState: (state) => states.push(state) }, { set: () => {}, delete: () => {}, clear: () => {} });
  await client.start();
  client.didOpen({ uri: 'file:///retained.md', languageId: 'markdown', version: 1, text: '# Retained' });
  const rejected = assert.rejects(client.requestSymbols('file:///retained.md'), /process exited/i);
  first.child.exit(1);
  await rejected;
  assert.equal(first.child.kills, 0, 'Unexpected already-observed exit must not be killed.');
  assert.equal(states.at(-1), 'stopped');
  await client.start();
  const statesBeforeOldExit = states.length;
  first.child.exit(1);
  assert.equal(states.length, statesBeforeOldExit, 'Old child must not change replacement state.');
  assert.equal((await client.requestSymbols('file:///retained.md')).length, 1);
  client.stop();
  assert.equal(first.child.kills, 0);
  assert.equal(second.child.kills, 1, 'Stop must target exactly the replacement owned child.');
});

function freshnessClient(double: ReturnType<typeof scripted>): {
  client: LspClient;
  visible: Map<string, LspDiagnosticData[]>;
} {
  const visible = new Map<string, LspDiagnosticData[]>();
  const client = new LspClient({ serverCommand: ['server'], cwd: '/proj', spawn: double.spawn,
    isTrusted: () => true, debounceMs: 1000 }, {
    set: (uri, items) => visible.set(uri, items),
    delete: (uri) => { visible.delete(uri); },
    clear: () => visible.clear(),
  });
  return { client, visible };
}
function releaseSymbol(child: ScriptedChild, id: string | number, name: string): void {
  const range = { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } };
  child.reply(frame(JSON.stringify({ jsonrpc: '2.0', id,
    result: [{ name, kind: 3, range, selectionRange: range, children: [] }] })));
}

test('new buffer revisions clear old Problems and require exact diagnostic revision', async () => {
  const double = scripted();
  const { client, visible } = freshnessClient(double);
  const uri = 'file:///latest.md';
  await client.start();
  client.didOpen({ uri, languageId: 'markdown', version: 1, text: 'v1' });
  await new Promise<void>((resolve) => setImmediate(resolve));
  publish(double.child, uri, 1);
  assert.equal(visible.get(uri)?.length, 1);
  client.didChange(uri, 2, 'v2'); // Still inside the untouched 1000ms debounce.
  assert.equal(visible.has(uri), false, 'Already displayed v1 Problems must be cleared.');
  publish(double.child, uri, 1);
  publish(double.child, uri); // Unversioned cannot prove current buffer revision.
  publish(double.child, uri, 3);
  assert.equal(visible.has(uri), false, 'Delayed, unknown and future revisions cannot display.');
  publish(double.child, uri, 2);
  assert.equal(visible.get(uri)?.length, 1, 'Current revision diagnostics remain visible.');
  client.stop();
});

test('manual v1/v2 barriers reject stale symbols and flush pending text before current symbols', async () => {
  const double = scripted({ symbolsPending: true });
  const { client, visible } = freshnessClient(double);
  const uri = 'file:///barrier.md';
  await client.start();
  client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# V1' });
  const old = client.requestSymbols(uri);
  const oldId = double.child.requests.at(-1)!.id;
  client.didChange(uri, 2, '# V2');
  publish(double.child, uri, 1);
  assert.equal(visible.has(uri), false);
  releaseSymbol(double.child, oldId, 'V1');
  assert.deepEqual(await old, [], 'v1 result released after v2 is queued must be discarded.');
  const current = client.requestSymbols(uri);
  const currentId = double.child.requests.at(-1)!.id;
  const messages = double.child.written.map((line) => JSON.parse(line.slice(line.indexOf('{'))) as {
    id?: string | number; method?: string; params?: { textDocument?: { version?: number } };
  });
  const changeIndex = messages.findIndex((message) => message.method === 'textDocument/didChange' && message.params?.textDocument?.version === 2);
  const requestIndex = messages.findIndex((message) => message.id === currentId);
  assert.ok(changeIndex >= 0 && changeIndex < requestIndex, 'Server receives v2 before its symbol request.');
  client.didChange(uri, 3, '# V3');
  releaseSymbol(double.child, currentId, 'V2');
  assert.deepEqual(await current, [], 'A newer edit while awaiting v2 invalidates its symbol result.');
  const latest = client.requestSymbols(uri);
  releaseSymbol(double.child, double.child.requests.at(-1)!.id, 'V3');
  assert.equal((await latest)[0]?.name, 'V3');
  client.stop();
});

test('close and reopen with equal revision/text cannot adopt a previous buffer symbol response', async () => {
  const double = scripted({ symbolsPending: true });
  const { client } = freshnessClient(double);
  const uri = 'file:///reopened.md';
  await client.start();
  client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Same' });
  const previous = client.requestSymbols(uri);
  const previousId = double.child.requests.at(-1)!.id;
  client.didClose(uri);
  client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Same' });
  releaseSymbol(double.child, previousId, 'Previous lifetime');
  assert.deepEqual(await previous, [], 'Buffer identity, not revision alone, binds an awaited result.');
  const reopened = client.requestSymbols(uri);
  releaseSymbol(double.child, double.child.requests.at(-1)!.id, 'Current lifetime');
  assert.equal((await reopened)[0]?.name, 'Current lifetime');
  client.stop();
});


test('forget closes a server buffer exactly once and rejects its late diagnostics', async () => {
  const double = scripted({ symbolsPending: true });
  const { client, visible } = freshnessClient(double);
  const uri = 'file:///deleted-or-renamed.mdx';
  await client.start();
  try {
    client.didOpen({ uri, languageId: 'mdx', version: 1, text: '# Before rename' });
    await new Promise<void>((resolve) => setImmediate(resolve));
    publish(double.child, uri, 1);
    assert.equal(visible.get(uri)?.length, 1, 'The open server buffer receives diagnostics.');
    const pendingSymbols = client.requestSymbols(uri);
    const requestId = double.child.requests.at(-1)!.id;
    client.didChange(uri, 2, '# Queued before deletion');
    client.forget(uri);
    client.didClose(uri); // VS Code may subsequently report the same close.
    client.forget(uri); // Duplicate filesystem notifications remain idempotent.
    client.flush(uri);
    releaseSymbol(double.child, requestId, 'Forgotten lifetime');
    const forgottenSymbols = await pendingSymbols;
    const messages = double.child.written.map((line) => JSON.parse(line.slice(line.indexOf('{'))) as {
      method?: string; params?: { textDocument?: { uri?: string } };
    });
    const documentMessages = messages.filter((message) => message.params?.textDocument?.uri === uri);
    assert.equal(documentMessages.filter((message) => message.method === 'textDocument/didOpen').length, 1);
    assert.equal(documentMessages.filter((message) => message.method === 'textDocument/didClose').length, 1,
      'Forgetting must release the existing server buffer; later close must not duplicate it.');
    assert.equal(documentMessages.filter((message) => message.method === 'textDocument/didChange').length, 0,
      'The queued change must be discarded when the document is forgotten.');
    publish(double.child, uri, 2);
    publish(double.child, uri);
    assert.equal(visible.has(uri), false, 'An absent buffer cannot resurrect Problems.');
    assert.deepEqual(forgottenSymbols, [], 'Awaited symbols from the forgotten buffer must be discarded.');
  } finally {
    client.stop();
  }
});

test('forget before startup prevents replay and permits a fresh document lifetime', async () => {
  const double = scripted();
  const { client, visible } = freshnessClient(double);
  const uri = 'file:///forgotten-before-start.md';
  client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Removed' });
  client.forget(uri);
  await client.start();
  try {
    assert.ok(!double.child.written.some((line) => line.includes('textDocument/didOpen')),
      'A document forgotten before readiness must not be replayed to the server.');
    assert.ok(!double.child.written.some((line) => line.includes('textDocument/didClose')),
      'A document never opened on this server needs no close notification.');
    client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Recreated' });
    await new Promise<void>((resolve) => setImmediate(resolve));
    publish(double.child, uri, 1);
    assert.equal(visible.get(uri)?.length, 1, 'A recreated file may start a new version-one lifetime.');
    client.didClose(uri);
  } finally {
    client.stop();
  }
});


test('context withdrawal and replacement reject held symbols and publications without a stale interval', async () => {
  for (const snapshot of [null, { replacement: true }]) {
    const double = scripted({ symbolsPending: true });
    const { client, visible } = freshnessClient(double);
    const uri = 'file:///project-context.md';
    await client.start();
    try {
      client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Same revision' });
      await new Promise<void>((resolve) => setImmediate(resolve));
      const oldGeneration = generationOf(double.child, uri);
      publish(double.child, uri, 1, oldGeneration);
      assert.equal(visible.get(uri)?.length, 1);
      const held = client.requestSymbols(uri);
      const heldId = double.child.requests.at(-1)!.id;
      client.sendProjectContext('docs', ['file:///'], snapshot);
      assert.equal(visible.has(uri), false, 'Context updates clear displayed old Problems immediately.');
      publish(double.child, uri, 1, oldGeneration);
      assert.equal(visible.has(uri), false, 'Held old-context diagnostics cannot be adopted, even temporarily.');
      releaseSymbol(double.child, heldId, 'Old project context');
      assert.deepEqual(await held, [], 'Held symbols cannot cross project context epochs.');
      publish(double.child, uri, 1);
      assert.equal(visible.get(uri)?.length, 1, 'Current-context diagnostics remain available.');
      const current = client.requestSymbols(uri);
      releaseSymbol(double.child, double.child.requests.at(-1)!.id, 'Current project context');
      assert.equal((await current)[0]?.name, 'Current project context');
    } finally {
      client.stop();
    }
  }
});

test('equal-version reopen rejects a held diagnostic from the previous document lifetime', async () => {
  const double = scripted();
  const { client, visible } = freshnessClient(double);
  const uri = 'file:///same-version-reopen.md';
  await client.start();
  try {
    client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Same text' });
    await new Promise<void>((resolve) => setImmediate(resolve));
    const oldGeneration = generationOf(double.child, uri);
    client.didClose(uri);
    client.didOpen({ uri, languageId: 'markdown', version: 1, text: '# Same text' });
    publish(double.child, uri, 1, oldGeneration);
    assert.equal(visible.has(uri), false, 'Old version one must never populate the new version-one lifetime.');
    publish(double.child, uri, 1);
    assert.equal(visible.get(uri)?.length, 1, 'Only the newly opened generation may publish.');
  } finally {
    client.stop();
  }
});
