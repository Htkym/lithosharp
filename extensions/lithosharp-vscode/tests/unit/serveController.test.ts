import assert from 'node:assert/strict';
import test from 'node:test';
import * as net from 'node:net';
import { ServeController, type ServeEvent, type SpawnedProcess } from '../../src/serveController.js';
import { spawnProcess } from '../../src/process.js';
import { fixturePath } from './helpers.js';

interface FakeChild extends SpawnedProcess {
  stdinLines: string[];
  stdoutHandlers: ((chunk: Buffer) => void)[];
  stderrHandlers: ((chunk: Buffer) => void)[];
  exitHandlers: ((code: number | null) => void)[];
  kills: number;
  ends: number;
  emitStdout(text: string | Buffer): void;
  emitStderr(text: string): void;
  exit(code: number | null): void;
}

function fakeSpawn(): { spawn: (command: string[], cwd: string) => SpawnedProcess; children: FakeChild[]; commands: string[][] } {
  const children: FakeChild[] = [];
  const commands: string[][] = [];
  const spawn = (command: string[], _cwd: string): SpawnedProcess => {
    commands.push(command);
    const child: FakeChild = {
      pid: 1000 + children.length,
      stdinLines: [],
      stdoutHandlers: [],
      stderrHandlers: [],
      exitHandlers: [],
      kills: 0,
      ends: 0,
      stdinWrite: (line) => {
        child.stdinLines.push(line);
      },
      closeStdin: () => {
        child.ends += 1;
      },
      onStdout: (fn) => {
        child.stdoutHandlers.push(fn);
      },
      onStderr: (fn) => {
        child.stderrHandlers.push(fn);
      },
      onExit: (fn) => {
        child.exitHandlers.push(fn);
      },
      killTree: () => {
        child.kills += 1;
      },
      emitStdout: (text) => {
        for (const fn of child.stdoutHandlers) {
          fn(typeof text === 'string' ? Buffer.from(text, 'utf8') : text);
        }
      },
      emitStderr: (text) => {
        for (const fn of child.stderrHandlers) {
          fn(Buffer.from(text, 'utf8'));
        }
      },
      exit: (code) => {
        for (const fn of child.exitHandlers) {
          fn(code);
        }
      },
    };
    children.push(child);
    return child;
  };
  return { spawn, children, commands };
}

function startup(port = 53111, generation = 1): string {
  return JSON.stringify({
    schemaVersion: '1.0', event: 'startup', host: '127.0.0.1', requestedPort: 0, actualPort: port,
    url: `http://127.0.0.1:${port}`, outputDirectory: '/tmp/out', basePath: '/', siteBasePath: '/',
    generation, routes: [],
  }) + '\n';
}

function controllerFor(fake: ReturnType<typeof fakeSpawn>, extra: Record<string, unknown> = {}): ServeController {
  return new ServeController({
    projectId: 'p1',
    cliCommand: ['lithosharp', 'serve'],
    cwd: '/proj',
    spawn: fake.spawn,
    startupTimeoutMs: 200,
    stopTimeoutMs: 50,
    ...extra,
  });
}

test('start reaches Running from the startup event', () => {
  const fake = fakeSpawn();
  const events: ServeEvent[] = [];
  const controller = new ServeController({
    projectId: 'p1', cliCommand: ['cli'], cwd: '/proj', spawn: fake.spawn, onEvent: (event) => events.push(event),
  });
  assert.equal(controller.start(), 'Starting');
  fake.children[0]!.emitStdout(startup());
  assert.equal(controller.getState(), 'Running');
  assert.equal(controller.url, 'http://127.0.0.1:53111');
  assert.equal(controller.actualPort, 53111);
  assert.equal(controller.generation, 1);
  assert.equal(events[0]!.event, 'startup');
});

test('start spam coalesces onto one process', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  assert.equal(controller.start(), 'Starting');
  assert.equal(controller.start(), 'Starting');
  fake.children[0]!.emitStdout(startup());
  assert.equal(controller.start(), 'Running');
  assert.equal(fake.children.length, 1);
});

test('invalid JSON never breaks the state machine', () => {
  const fake = fakeSpawn();
  const logs: string[] = [];
  const controller = new ServeController({
    projectId: 'p1', cliCommand: ['cli'], cwd: '/proj', spawn: fake.spawn, onLog: (line) => logs.push(line),
  });
  controller.start();
  fake.children[0]!.emitStdout('not json\n');
  fake.children[0]!.emitStdout(JSON.stringify({ schemaVersion: '1.0', nope: true }) + '\n');
  assert.equal(controller.getState(), 'Starting');
  assert.ok(logs.length >= 2);
  fake.children[0]!.emitStdout(startup());
  assert.equal(controller.getState(), 'Running');
});

test('chunk-split multibyte JSON reassembles', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  const url = 'http://127.0.0.1:53112/日本語🎉';
  const line = JSON.stringify({ ...JSON.parse(startup(53112)), url }) + '\n';
  const bytes = Buffer.from(line, 'utf8');
  const characterStart = bytes.indexOf(Buffer.from('日', 'utf8'));
  assert.ok(characterStart >= 0);
  const split = characterStart + 1;
  // Both seams split the three UTF-8 bytes of 日 in the public URL.
  fake.children[0]!.emitStdout(bytes.subarray(0, split));
  fake.children[0]!.emitStdout(bytes.subarray(split, split + 1));
  fake.children[0]!.emitStdout(bytes.subarray(split + 1));
  assert.equal(controller.getState(), 'Running');
  assert.equal(controller.actualPort, 53112);
  assert.equal(controller.url, url);
});

test('double stop coalesces onto one shutdown', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  fake.children[0]!.emitStdout(startup());
  assert.equal(controller.stop('a'), 'Stopping');
  assert.equal(controller.stop('b'), 'Stopping');
  const lines = fake.children[0]!.stdinLines;
  assert.equal(lines.length, 1);
  assert.equal(JSON.parse(lines[0]!).command, 'shutdown');
  fake.children[0]!.exit(0);
  assert.equal(controller.getState(), 'Stopped');
  assert.equal(fake.children[0]!.kills, 0);
});

test('stop during Starting shuts down without serving', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  assert.equal(controller.stop('early'), 'Stopping');
  assert.equal(JSON.parse(fake.children[0]!.stdinLines[0]!).command, 'shutdown');
  fake.children[0]!.exit(0);
  assert.equal(controller.getState(), 'Stopped');
});

test('late startup event cannot undo a requested stop', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  controller.stop('early');
  fake.children[0]!.emitStdout(startup());
  assert.equal(controller.getState(), 'Stopping');
  assert.equal(fake.children[0]!.stdinLines.length, 1);
  fake.children[0]!.exit(0);
  assert.equal(controller.getState(), 'Stopped');
});

test('process crash becomes Failed', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  fake.children[0]!.emitStdout(startup());
  fake.children[0]!.exit(3);
  assert.equal(controller.getState(), 'Failed');
  assert.match(controller.lastError ?? '', /unexpectedly/);
});

test('startup failure reports the error', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  fake.children[0]!.emitStdout(
    JSON.stringify({ schemaVersion: '1.0', event: 'startup-failed', success: false, exitCode: 1, error: 'Port 9999 is occupied.' }) + '\n',
  );
  assert.equal(controller.getState(), 'Failed');
  assert.equal(controller.lastError, 'Port 9999 is occupied.');
});

test('missing startup event times out', async () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake, { startupTimeoutMs: 30 });
  controller.start();
  await new Promise((resolve) => setTimeout(resolve, 120));
  assert.equal(controller.getState(), 'Failed');
  assert.equal(fake.children[0]!.kills, 1);
});

test('huge stderr stays bounded', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake, { maxStderrChars: 100 });
  controller.start();
  fake.children[0]!.emitStderr('y'.repeat(100000));
  assert.ok(controller.getStderrTail().length <= 100);
  assert.equal(controller.getState(), 'Starting');
});

test('rebuild generations track success and failure', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  fake.children[0]!.emitStdout(startup());
  fake.children[0]!.emitStdout(JSON.stringify({ schemaVersion: '1.0', event: 'rebuild-started' }) + '\n');
  assert.equal(controller.getState(), 'Rebuilding');
  fake.children[0]!.emitStdout(
    JSON.stringify({ schemaVersion: '1.0', event: 'rebuild-succeeded', success: true, exitCode: 0, generation: 2 }) + '\n',
  );
  assert.equal(controller.getState(), 'Running');
  assert.equal(controller.generation, 2);
  fake.children[0]!.emitStdout(
    JSON.stringify({ schemaVersion: '1.0', event: 'rebuild-failed', success: false, exitCode: 1, error: 'broken', generation: 3 }) + '\n',
  );
  assert.equal(controller.getState(), 'Failed');
  assert.equal(controller.lastError, 'broken');
  // A failed rebuild keeps the server process alive; later successful rebuilds recover it.
  assert.equal(controller.start(), 'Failed');
  assert.equal(fake.children.length, 1);
  fake.children[0]!.emitStdout(JSON.stringify({ schemaVersion: '1.0', event: 'rebuild-started' }) + '\n');
  assert.equal(controller.getState(), 'Rebuilding');
  fake.children[0]!.emitStdout(JSON.stringify({ schemaVersion: '1.0', event: 'rebuild-succeeded', generation: 4 }) + '\n');
  assert.equal(controller.getState(), 'Running');
});

test('waitUntilRunning follows the startup event', async () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  const waiting = controller.waitUntilRunning();
  fake.children[0]!.emitStdout(startup());
  await waiting;
  assert.equal(controller.isServing(), true);
});

test('dispose stops without throwing', () => {
  const fake = fakeSpawn();
  const controller = controllerFor(fake);
  controller.start();
  fake.children[0]!.emitStdout(startup());
  controller.dispose();
  assert.equal(fake.children[0]!.stdinLines.length, 1);
  fake.children[0]!.exit(0);
  assert.equal(controller.getState(), 'Stopped');
});

test('real port is released after structured stop', async () => {
  const controller = new ServeController({
    projectId: 'real',
    cliCommand: [process.execPath, fixturePath('fake-cli.mjs')],
    cwd: fixturePath(),
    spawn: spawnProcess,
    startupTimeoutMs: 20000,
    stopTimeoutMs: 5000,
  });
  assert.equal(controller.start(), 'Starting');
  const watch = Date.now();
  while (controller.getState() === 'Starting' && Date.now() - watch < 20000) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assert.equal(controller.getState(), 'Running');
  const port = controller.actualPort;
  assert.ok(port > 0);
  await new Promise<void>((resolve, reject) => {
    const socket = net.connect(port, '127.0.0.1');
    socket.on('connect', () => {
      socket.end();
      resolve();
    });
    socket.on('error', reject);
  });
  // An unrelated server coexists on its own port and survives our stop.
  const external = net.createServer();
  await new Promise<void>((resolve) => external.listen(0, '127.0.0.1', () => resolve()));
  const externalPort = (external.address() as net.AddressInfo).port;
  assert.equal(controller.stop('test-stop'), 'Stopping');
  const exited = Date.now();
  while (controller.getState() !== 'Stopped' && Date.now() - exited < 20000) {
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  assert.equal(controller.getState(), 'Stopped');
  await new Promise<void>((resolve, reject) => {
    const socket = net.connect(port, '127.0.0.1');
    socket.on('connect', () => reject(new Error('port still occupied')));
    socket.on('error', () => resolve());
  });
  await new Promise<void>((resolve, reject) => {
    const socket = net.connect(externalPort, '127.0.0.1');
    socket.on('connect', () => {
      socket.end();
      resolve();
    });
    socket.on('error', reject);
  });
  external.close();
});

test('two projects stay independent', () => {
  const fake = fakeSpawn();
  const first = new ServeController({ projectId: 'a', cliCommand: ['cli'], cwd: '/a', spawn: fake.spawn });
  const second = new ServeController({ projectId: 'b', cliCommand: ['cli'], cwd: '/b', spawn: fake.spawn });
  first.start();
  second.start();
  fake.children[0]!.emitStdout(startup(54111));
  assert.equal(first.getState(), 'Running');
  assert.equal(second.getState(), 'Starting');
  fake.children[1]!.emitStdout(startup(54222));
  assert.equal(first.actualPort, 54111);
  assert.equal(second.actualPort, 54222);
});
