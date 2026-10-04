import assert from 'node:assert/strict';
import test from 'node:test';
import { LspConnection, type LspProcess } from '../../src/lspConnection.js';

function frame(body: string): Buffer {
  const bytes = Buffer.byteLength(body, 'utf8');
  return Buffer.from(`Content-Length: ${bytes}\r\n\r\n${body}`, 'utf8');
}

function fakeChild(): LspProcess & {
  written: string[];
  stdout: ((chunk: Buffer) => void)[];
  exits: ((code: number | null) => void)[];
  kills: number;
  emit(chunk: Buffer): void;
  exit(code: number | null): void;
} {
  const child = {
    written: [] as string[],
    stdout: [] as ((chunk: Buffer) => void)[],
    exits: [] as ((code: number | null) => void)[],
    kills: 0,
    stdinWrite: (line: string) => {
      child.written.push(line);
    },
    closeStdin: () => {},
    onStdout: (fn: (chunk: Buffer) => void) => {
      child.stdout.push(fn);
    },
    onStderr: (_fn: (chunk: Buffer) => void) => {},
    onExit: (fn: (code: number | null) => void) => {
      child.exits.push(fn);
    },
    killTree: () => {
      child.kills += 1;
    },
    emit: (chunk: Buffer) => {
      for (const fn of child.stdout) {
        fn(chunk);
      }
    },
    exit: (code: number | null) => {
      for (const fn of child.exits) {
        fn(code);
      }
    },
  };
  return child;
}

test('split multibyte frames reassemble', async () => {
  const child = fakeChild();
  const notifications: { method: string; params: unknown }[] = [];
  const connection = new LspConnection(child, {
    onNotification: (method, params) => notifications.push({ method, params }),
  });
  void connection;
  const body = JSON.stringify({ jsonrpc: '2.0', method: 'ping', params: { text: '日本語🎉' } });
  const framed = frame(body);
  // Split inside multibyte sequences on both seams.
  child.emit(framed.subarray(0, 45));
  child.emit(framed.subarray(45, 47));
  child.emit(framed.subarray(47));
  assert.equal(notifications.length, 1);
  assert.equal(notifications[0]!.method, 'ping');
});

test('concatenated responses match out of order', async () => {
  const child = fakeChild();
  const connection = new LspConnection(child, { onNotification: () => {} });
  const first = connection.sendRequest('a', 'one', {});
  const second = connection.sendRequest('b', 'two', {});
  const reply = (id: string, n: number): Buffer =>
    frame(JSON.stringify({ jsonrpc: '2.0', id, result: n }));
  // Answer the second request first.
  child.emit(Buffer.concat([reply('b', 2), reply('a', 1)]));
  assert.equal(await first, 1);
  assert.equal(await second, 2);
});

test('garbage never breaks framing', async () => {
  const child = fakeChild();
  const notifications: string[] = [];
  const connection = new LspConnection(child, {
    onNotification: (method) => notifications.push(method),
  });
  void connection;
  child.emit(Buffer.from('garbage line\n', 'utf8'));
  child.emit(frame(JSON.stringify({ jsonrpc: '2.0', method: 'ok', params: {} })));
  assert.deepEqual(notifications, ['ok']);
});

test('unknown methods answer MethodNotFound', async () => {
  const child = fakeChild();
  const connection = new LspConnection(child, { onNotification: () => {} });
  const pending = connection.sendRequest('x', 'm', {});
  child.emit(frame(JSON.stringify({ jsonrpc: '2.0', id: 'x', error: { code: -32601, message: 'nope' } })));
  await assert.rejects(pending, /-32601/);
});

test('process exit rejects outstanding requests', async () => {
  const child = fakeChild();
  const connection = new LspConnection(child, { onNotification: () => {} });
  const pending = connection.sendRequest('x', 'textDocument/documentSymbol', {});
  child.exit(1);
  await assert.rejects(pending, /process exited/);
  await assert.rejects(connection.sendRequest('y', 'initialize', {}), /not running/);
});

test('cancelling a request settles its local promise immediately', async () => {
  const child = fakeChild();
  const connection = new LspConnection(child, { onNotification: () => {} });
  const pending = connection.sendRequest('x', 'textDocument/documentSymbol', {});
  connection.cancelRequest('x');
  await assert.rejects(pending, /cancelled/);
  assert.ok(child.written.some((line) => line.includes('$/cancelRequest')));
});


test('disposal settles every pending request and ignores late process traffic', async () => {
  const child = fakeChild();
  const notifications: string[] = [];
  const connection = new LspConnection(child, { onNotification: (method) => notifications.push(method) });
  const first = connection.sendRequest('first', 'initialize', {});
  const second = connection.sendRequest('second', 'textDocument/documentSymbol', {});
  connection.dispose();
  connection.dispose();
  await assert.rejects(first, /connection stopped/);
  await assert.rejects(second, /connection stopped/);
  const writes = child.written.length;
  child.emit(frame(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: {} })));
  child.emit(frame(JSON.stringify({ jsonrpc: '2.0', id: 'second', result: [] })));
  child.exit(0);
  connection.sendNotification('initialized', {});
  connection.cancelRequest('second');
  await assert.rejects(connection.sendRequest('third', 'initialize', {}), /not running/);
  assert.deepEqual(notifications, []);
  assert.equal(child.written.length, writes);
  assert.equal(connection.alive, false);
});
