import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fetchCliVersion, resolveCli, type ExecProbe } from '../../src/cliResolver.js';

function fakeProbe(version: string | null, exit = 0): ExecProbe {
  const calls: ExecProbe['calls'] = [];
  return {
    calls,
    run: async (command, cwd) => {
      calls.push({ command, cwd });
      return { exit, stdout: version === null ? '' : `lithosharp ${version}` };
    },
  };
}

async function tempDir(): Promise<string> {
  return await fs.promises.mkdtemp(path.join(os.tmpdir(), 'lithosharp-cli-'));
}

test('untrusted resolution returns missing', async () => {
  const resolved = await resolveCli({ isTrusted: false, explicitPath: '/bin/lithosharp', envPath: '/bin' });
  assert.equal(resolved.kind, 'missing');
});

test('explicit setting wins when the file exists', async () => {
  const dir = await tempDir();
  const exe = path.join(dir, process.platform === 'win32' ? 'lithosharp.exe' : 'lithosharp');
  await fs.promises.writeFile(exe, '');
  const resolved = await resolveCli({ isTrusted: true, explicitPath: exe, envPath: '' });
  if (resolved.kind === 'missing') {
    assert.fail(`unexpected missing: ${resolved.reason}`);
  }
  assert.deepEqual(resolved.command.command, [exe]);
  const probe = fakeProbe('1.1.0');
  const version = await fetchCliVersion(resolved as never, true, probe);
  assert.equal(version, '1.1.0');
  assert.equal(probe.calls.length, 1);
});

test('missing explicit path is actionable', async () => {
  const resolved = await resolveCli({ isTrusted: true, explicitPath: '/no/such/cli', envPath: '' });
  assert.equal(resolved.kind, 'missing');
  if (resolved.kind === 'missing') {
    assert.match(resolved.reason, /cliPath/);
  }
});

test('project-local tool manifest resolves the tool command', async () => {
  const dir = await tempDir();
  await fs.promises.mkdir(path.join(dir, '.config'), { recursive: true });
  await fs.promises.writeFile(
    path.join(dir, '.config', 'dotnet-tools.json'),
    JSON.stringify({ tools: { 'lithosharp': { version: '1.1.0' } } }),
  );
  const resolved = await resolveCli({ isTrusted: true, projectDir: dir, envPath: '' });
  if (resolved.kind === 'missing') {
    assert.fail(`unexpected missing: ${resolved.reason}`);
  }
  assert.equal(resolved.kind, 'local');
  assert.deepEqual(resolved.command.command, ['dotnet', 'tool', 'run', 'lithosharp']);
});

test('global PATH lookup finds executables', async () => {
  const dir = await tempDir();
  const exe = path.join(dir, process.platform === 'win32' ? 'lithosharp.exe' : 'lithosharp');
  await fs.promises.writeFile(exe, '');
  const resolved = await resolveCli({ isTrusted: true, envPath: dir });
  assert.equal(resolved.kind, 'global');
});

test('missing runtimes explain the next install step', async () => {
  const noDotnet = await resolveCli({ isTrusted: true, envPath: '' });
  assert.equal(noDotnet.kind, 'missing');
  if (noDotnet.kind === 'missing') {
    assert.match(noDotnet.reason, /\.NET 10 SDK/);
  }
});

test('version fetch never runs when untrusted', async () => {
  const probe = fakeProbe('1.1.0');
  await assert.rejects(fetchCliVersion({ command: { command: ['lithosharp'] }, version: null } as never, false, probe));
  assert.equal(probe.calls.length, 0);
});
