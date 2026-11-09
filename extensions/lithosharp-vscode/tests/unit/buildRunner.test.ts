import assert from 'node:assert/strict';
import test from 'node:test';
import { BuildRunner, parseCliResult } from '../../src/buildRunner.js';

function envelope(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    schemaVersion: '1.0',
    success: true,
    exitCode: 0,
    outputDirectory: '/tmp/out',
    diagnosticsText: null,
    diagnostics: [],
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
    diagnostics: [],
  });
}

test('queued preflight captures arguments and preserves positioned failure diagnostics', async () => {
  const commands: string[][] = [];
  const raw = { schemaVersion: '1.0', success: false, exitCode: 1,
    preflight: { mode: 'static-inputs', stage: 'source', deferredReasons: [] }, diagnostics: [{ id: 'LIT001', severity: 'Error', message: 'Invalid source', line: 4, column: 8 }] };
  const runner = new BuildRunner({ isTrusted: () => true, cwd: '/project',
    cli: (name, args) => ({ command: ['cli', name, ...args] }),
    probe: { run: async command => { commands.push(command); return { exit: 1, stdout: JSON.stringify(raw), stderr: '' }; } } });
  const args = ['--mode', 'static', '--input', 'original.md'];
  const pending = runner.run('preflight', args); args[4] = 'changed.md';
  const result = await pending;
  assert.deepEqual(commands, [['cli', 'preflight', '--mode', 'static', '--input', 'original.md']]);
  assert.equal(result.ok, false);
  assert.deepEqual(result.raw, raw);
});

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

test('a signalled process is interrupted even after a successful envelope and retains diagnostics', async () => {
  const diagnostic = { id: 'LSM013', severity: 'Error', message: 'Incomplete', file: 'intro.md', line: null };
  const runner = new BuildRunner({ isTrusted: () => true, cwd: '/proj', cli: command => ({ command: ['cli', command] }),
    probe: { run: async () => ({ exit: 1, stdout: envelope({ diagnostics: [diagnostic] }), stderr: '', signal: 'SIGTERM' }) } });
  const result = await runner.run('inspect');
  assert.equal(result.outcome, 'cancelled');
  assert.equal(result.processExitCode, null);
  assert.deepEqual(result.diagnostics, [diagnostic]);
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

test('inspect parses a large valid machine envelope while stderr logs stay bounded', async () => {
  const routes = Array.from({ length: 3000 }, (_, index) => ({ path: `/page-${index}`, title: 'Large site route' }));
  const stdout = envelope({ routes });
  assert.ok(stdout.length > 65536);
  const logged: string[] = [];
  const runner = new BuildRunner({
    isTrusted: () => true,
    cli: (command) => ({ command: ['cli', command] }),
    cwd: '/proj',
    maxOutputChars: 64,
    probe: { run: async () => ({ exit: 0, stdout, stderr: 'y'.repeat(100000) }) },
    onLog: (line) => logged.push(line),
  });
  const result = await runner.run('inspect');
  assert.equal(result.ok, true);
  if (result.ok) assert.deepEqual((result.raw as Record<string, unknown>)['routes'], routes);
  assert.equal(logged.length, 1);
  assert.ok(logged[0]!.length <= 64);
});

test('invalid JSON fails while stderr logs stay bounded', async () => {
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
  // Trailing non-JSON content is invalid regardless of its length.
  assert.equal(result.ok, false);
  assert.ok(logged.join('\n').length <= 4096);
});

test('CLI trust boundary rejects incompatible, malformed and contradictory results without losing diagnostics', () => {
  const diagnostic = { id: 'LSM013', severity: 'Error', message: 'Analysis incomplete', file: 'intro.md', line: null };
  for (const [overrides, exit, outcome] of [
    [{ schemaVersion: '2.0' }, 0, 'unsupported-schema'],
    [{ exitCode: undefined }, 0, 'invalid-response'],
    [{ success: 'true' }, 0, 'invalid-response'],
    [{ outputDirectory: 1 }, 0, 'invalid-response'],
    [{ buildPlan: [{ inputs: 'invalid', artifacts: [] }] }, 0, 'invalid-response'],
    [{ preflight: { mode: 'static-inputs', stage: 'source', deferredReasons: 'not an array' } }, 0, 'invalid-response'],
    [{ diagnostics: [diagnostic, { id: 'broken' }] }, 0, 'invalid-response'],
    [{ success: true, exitCode: 0 }, 1, 'inconsistent'],
    [{ success: true, exitCode: 1 }, 1, 'inconsistent'],
    [{ success: false, exitCode: 0 }, 0, 'inconsistent'],
    [{ success: false, exitCode: 2 }, 1, 'inconsistent'],
    [{ success: false, exitCode: 130 }, 130, 'cancelled'],
  ] as [Record<string, unknown>, number, string][]) {
    const raw = { schemaVersion: '1.0', success: true, exitCode: 0, diagnostics: [diagnostic], diagnosticsText: 'original text', ...overrides };
    const result = parseCliResult('build', exit, JSON.stringify(raw));
    assert.equal(result.ok, false);
    assert.equal(result.outcome, outcome);
    assert.deepEqual(result.diagnostics, [diagnostic]);
    assert.equal(result.diagnosticsText, 'original text');
    assert.deepEqual(result.raw, JSON.parse(JSON.stringify(raw)));
    assert.equal(result.exitCode, exit);
    assert.equal(result.processExitCode, exit);
  }
  assert.equal(parseCliResult('inspect', 0, envelope({ schemaVersion: '1.42', futureField: { supported: false } })).ok, true);
});
