import assert from 'node:assert/strict';
import test from 'node:test';
import { BuildRunner } from '../../src/buildRunner.js';

function envelope(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    schemaVersion: '1.0',
    success: true,
    exitCode: 0,
    outputDirectory: '/tmp/out',
    diagnosticsText: null,
    ...overrides,
  });
}

function failureEnvelope(): string {
  return JSON.stringify({
    schemaVersion: '1.0',
    success: false,
    exitCode: 1,
    error: 'Route collision.',
    diagnosticsText: 'LSR001 text',
  });
}

test('build parses the machine envelope', async () => {
  const calls: string[][] = [];
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['lithosharp', command, '--format', 'json'] }),
    cwd: '/proj',
    probe: {
      run: async (command) => {
        calls.push(command);
        return { exit: 0, stdout: envelope(), stderr: '' };
      },
    },
  });
  const result = await runner.run('build');
  assert.equal(result.ok, true);
  assert.deepEqual(calls, [['lithosharp', 'build', '--format', 'json']]);
});

test('failure envelope surfaces the error, not a pattern', async () => {
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    probe: { run: async () => ({ exit: 1, stdout: failureEnvelope(), stderr: '' }) },
  });
  const result = await runner.run('check');
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.equal(result.error, 'Route collision.');
    assert.equal(result.diagnosticsText, 'LSR001 text');
  }
});

test('unparseable output fails without guessing', async () => {
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    probe: { run: async () => ({ exit: 0, stdout: 'Build succeeded!\n', stderr: '' }) },
  });
  const result = await runner.run('build');
  assert.equal(result.ok, false);
});

test('concurrent builds serialize per runner', async () => {
  const order: string[] = [];
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    probe: {
      run: async (command) => {
        order.push(`start:${command[1]}`);
        await new Promise((resolve) => setTimeout(resolve, 30));
        order.push(`end:${command[1]}`);
        return { exit: 0, stdout: envelope(), stderr: '' };
      },
    },
  });
  const [first, second] = await Promise.all([runner.run('build'), runner.run('check')]);
  assert.equal(first.ok && second.ok, true);
  assert.deepEqual(order, ['start:build', 'end:build', 'start:check', 'end:check']);
});

test('dispose cancels the active CLI and prevents queued commands from starting', async () => {
  let runs = 0;
  let markStarted!: () => void;
  const started = new Promise<void>((resolve) => {
    markStarted = resolve;
  });
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    probe: {
      run: async (_command, _cwd, _input, signal) => {
        runs++;
        markStarted();
        return await new Promise((resolve) => {
          signal?.addEventListener('abort', () => resolve({ exit: 130, stdout: '', stderr: '' }), { once: true });
        });
      },
    },
  });
  const active = runner.run('build');
  const queued = runner.run('check');
  await started;
  runner.dispose();
  const results = await Promise.all([active, queued]);

  assert.equal(runs, 1);
  assert.deepEqual(results.map((result) => result.exitCode), [130, 130]);
});

test('untrusted builds never execute', async () => {
  let calls = 0;
  const runner = new BuildRunner({
    isTrusted: () => false,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    probe: {
      run: async () => {
        calls += 1;
        return { exit: 0, stdout: envelope(), stderr: '' };
      },
    },
  });
  await assert.rejects(runner.run('build'));
  assert.equal(calls, 0);
});

test('huge outputs stay bounded', async () => {
  const logged: string[] = [];
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    maxOutputChars: 64,
    probe: { run: async () => ({ exit: 0, stdout: envelope() + 'x'.repeat(100000), stderr: 'y'.repeat(100000) }) },
    onLog: (line) => logged.push(line),
  });
  const result = await runner.run('inspect');
  // The envelope no longer parses after truncation, but nothing unbounded is kept.
  assert.equal(result.ok, false);
  assert.ok(logged.join('\n').length <= 4096);
});
