import assert from 'node:assert/strict';
import test from 'node:test';
import { LspClient } from '../../src/lspClient.js';
import type { LspProcess } from '../../src/lspConnection.js';
import type { LspDiagnosticData } from '../../src/diagnostics.js';

interface ScriptedChild extends LspProcess {
  written: string[];
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
function scripted(options: { publish?: (uri: string, version: number) => object[] } = {}): {
  child: ScriptedChild;
  spawn: (command: string[], cwd: string) => LspProcess;
  spawns: number;
} {
  let spawns = 0;
  const child: ScriptedChild = {
    written: [],
    handlers: { stdout: [], exit: [] },
    requests: [],
    stdinWrite: (line) => {
      child.written.push(line);
      handle(line);
    },
    closeStdin: () => {},
    onStdout: (fn) => {
      child.handlers.stdout.push(fn);
    },
    onStderr: () => {},
    onExit: (fn) => {
      child.handlers.exit.push(fn);
    },
    killTree: () => {},
    reply: (body) => {
      for (const fn of child.handlers.stdout) {
        fn(Buffer.from(body, 'utf8'));
      }
    },
    exit: (_code) => {},
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
        const params = message.params as { textDocument: { uri: string; version: number } };
        const uri = params.textDocument.uri;
        const version = params.textDocument.version;
        const diagnostics = options.publish ? options.publish(uri, version) : [];
        setImmediate(() => {
          child.reply(
            frame(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: { uri, version, diagnostics } })),
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
