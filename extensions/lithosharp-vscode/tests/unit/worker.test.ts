import assert from 'node:assert/strict';
import test from 'node:test';
import { createHash } from 'node:crypto';
import { resolveWorker, restoreWorker, workerLockHash, workerSourceHash, type WorkerDeps, type WorkerFileSystem } from '../../src/worker.js';
import { UntrustedWorkspaceError } from '../../src/trust.js';

function fakeFs(files: Record<string, string>, dirs: string[] = []): WorkerFileSystem & { copied: [string, string][]; installs: string[]; failInstall: boolean } {
  const norm = (p: string): string => p.replace(/\\/g, '/');
  const handle: WorkerFileSystem & { copied: [string, string][]; installs: string[]; failInstall: boolean } = {
    copied: [],
    installs: [],
    failInstall: false,
    readFile: async (filePath) => {
      const hit = files[norm(filePath)];
      return hit === undefined ? null : Buffer.from(hit);
    },
    isDirectory: async (dirPath) => dirs.map(norm).includes(norm(dirPath)),
    isFile: async (filePath) => norm(filePath) in files,
    ensureDir: async () => {},
    writeFile: async (filePath, contents) => {
      files[norm(filePath)] = contents;
    },
    sourceFiles: async (dir) => Object.keys(files).filter(file => file.startsWith(norm(dir) + '/'))
      .map(file => file.slice(norm(dir).length + 1))
      .filter(file => !file.split('/').some(part => ['node_modules', '.cache', 'tests', '.git', '.lithosharp-worker-lock'].includes(part))),
    copySourceTree: async (from, to) => {
      handle.copied.push([from, to]);
      const source = await handle.sourceFiles(from);
      for (const file of source) files[norm(to) + '/' + file] = files[norm(from) + '/' + file]!;
      return source.length;
    },
    runNpmCi: async (cwd) => {
      handle.installs.push(cwd);
      return handle.failInstall ? { exit: 1, stdout: '', stderr: 'boom' } : { exit: 0, stdout: 'ok', stderr: '' };
    },
  };
  return handle;
}

function deps(overrides: Partial<WorkerDeps> & { fs: WorkerFileSystem }): WorkerDeps {
  return {
    isTrusted: true,
    workerPathSetting: '',
    storageDir: '/storage',
    bundledDir: '/bundled',
    ...overrides,
  };
}

const lockfile = '{"lockfileVersion": 3}';
const lockHash = createHash('sha256').update(lockfile).digest('hex');

test('explicit directory wins and reports readiness', async () => {
  const fs = fakeFs({
    '/custom/package-lock.json': lockfile,
    ['/custom/.lithosharp-worker-lock']: lockHash,
  }, ['/custom/node_modules']);
  const resolved = await resolveWorker(deps({ fs, workerPathSetting: '  /custom ' }));
  assert.equal(resolved.source, 'explicit');
  assert.equal(resolved.directory, '/custom');
  assert.equal(resolved.lockHash, lockHash);
  assert.equal(resolved.ready, true);
});

test('explicit directory without node_modules is not ready', async () => {
  const fs = fakeFs({ '/custom/package-lock.json': lockfile });
  const resolved = await resolveWorker(deps({ fs, workerPathSetting: '/custom' }));
  assert.equal(resolved.ready, false);
  assert.equal(resolved.lockHash, lockHash);
});

test('storage identity includes the bundled production source and lockfile', async () => {
  const files: Record<string, string> = {
    '/bundled/package.json': '{}',
    '/bundled/package-lock.json': lockfile,
  };
  const dirs: string[] = [];
  const fs = fakeFs(files, dirs);
  const sourceHash = await workerSourceHash(fs, '/bundled');
  const storage = '/storage/worker-' + sourceHash;
  await fs.copySourceTree('/bundled', storage);
  files[storage + '/.lithosharp-worker-lock'] = lockHash;
  dirs.push(storage + '/node_modules');
  const resolved = await resolveWorker(deps({ fs }));
  assert.equal(resolved.source, 'storage');
  assert.equal(resolved.directory.replace(/\\/g, '/'), storage);
  assert.equal(resolved.sourceHash, sourceHash);
  assert.equal(resolved.ready, true);
});

test('missing bundled worker resolves to none', async () => {
  const fs = fakeFs({});
  const resolved = await resolveWorker(deps({ fs }));
  assert.deepEqual(resolved, { directory: '', lockHash: null, ready: false, source: 'none' });
});

test('untrusted resolution executes nothing', async () => {
  const fs = fakeFs({ '/bundled/package-lock.json': lockfile });
  await assert.rejects(resolveWorker(deps({ fs, isTrusted: false })), UntrustedWorkspaceError);
  assert.equal(fs.copied.length, 0);
  assert.equal(fs.installs.length, 0);
});

test('restore copies, installs and verifies', async () => {
  const logs: string[] = [];
  const restoring = fakeFs({ '/bundled/package.json': '{}', '/bundled/package-lock.json': lockfile });
  const originalIsDirectory = restoring.isDirectory;
  let installed = false;
  restoring.isDirectory = async (dirPath) => {
    if (dirPath.endsWith('node_modules')) {
      return installed;
    }
    return originalIsDirectory(dirPath);
  };
  const originalRun = restoring.runNpmCi;
  restoring.runNpmCi = async (cwd) => {
    const result = await originalRun(cwd);
    installed = result.exit === 0;
    return result;
  };
  const restored = await restoreWorker(deps({ fs: restoring }), (line) => logs.push(line));
  assert.equal(restored.ready, true);
  assert.equal(restoring.copied.length, 1);
  assert.deepEqual(restoring.copied[0]![0].replace(/\\/g, '/'), '/bundled');
  assert.equal(restoring.installs.length, 1);
  assert.ok(logs.some((line) => line.includes('restored')));
});

test('restore skips install when already ready', async () => {
  const files: Record<string, string> = {
      '/bundled/package.json': '{}',
      '/bundled/package-lock.json': lockfile,
  };
  const dirs: string[] = [];
  const fs = fakeFs(files, dirs);
  const storage = '/storage/worker-' + await workerSourceHash(fs, '/bundled');
  await fs.copySourceTree('/bundled', storage);
  fs.copied.length = 0;
  files[storage + '/.lithosharp-worker-lock'] = lockHash;
  dirs.push(storage + '/node_modules');
  const restored = await restoreWorker(deps({ fs }), () => {});
  assert.equal(restored.ready, true);
  assert.equal(fs.installs.length, 0);
  assert.equal(fs.copied.length, 0);
});

test('same-lock worker upgrades restore changed compiler and runtime source into fresh storage', async () => {
  const files: Record<string, string> = {
    '/bundled/package.json': '{}', '/bundled/package-lock.json': lockfile,
    '/bundled/worker.mjs': 'worker-v1', '/bundled/compiler.mjs': 'compiler-v1',
    '/bundled/runtime/components.mjs': 'runtime-v1',
  };
  const fs = fakeFs(files);
  fs.isDirectory = async dir => dir.endsWith('node_modules');
  const first = await restoreWorker(deps({ fs }), () => {});
  files['/bundled/compiler.mjs'] = 'compiler-v2';
  const pending = await resolveWorker(deps({ fs }));
  assert.equal(pending.ready, false);
  assert.equal(pending.lockHash, first.lockHash);
  assert.notEqual(pending.directory, first.directory);
  const second = await restoreWorker(deps({ fs }), () => {});
  assert.equal((await fs.readFile(second.directory + '/compiler.mjs'))?.toString(), 'compiler-v2');
  assert.equal((await fs.readFile(first.directory + '/compiler.mjs'))?.toString(), 'compiler-v1');
  files['/bundled/runtime/components.mjs'] = 'runtime-v2';
  const third = await restoreWorker(deps({ fs }), () => {});
  assert.notEqual(third.directory, second.directory);
  assert.equal((await fs.readFile(third.directory + '/runtime/components.mjs'))?.toString(), 'runtime-v2');
  assert.equal(fs.installs.length, 3);
});

test('restored source tampering invalidates readiness while dependencies stay present', async () => {
  const files: Record<string, string> = {'/bundled/package-lock.json': lockfile, '/bundled/worker.mjs': 'worker'};
  const fs = fakeFs(files);
  fs.isDirectory = async dir => dir.endsWith('node_modules');
  const restored = await restoreWorker(deps({ fs }), () => {});
  files[restored.directory.replace(/\\/g, '/') + '/worker.mjs'] = 'stale';
  assert.equal((await resolveWorker(deps({ fs }))).ready, false);
});

test('source hashing tracks file names as well as bytes', async () => {
  const files = {'/bundled/package-lock.json': lockfile, '/bundled/a.mjs': 'body'} as Record<string, string>;
  const fs = fakeFs(files);
  const original = await workerSourceHash(fs, '/bundled');
  delete files['/bundled/a.mjs']; files['/bundled/b.mjs'] = 'body';
  assert.notEqual(await workerSourceHash(fs, '/bundled'), original);
});

test('restore without a bundle explains instead of guessing', async () => {
  const fs = fakeFs({});
  await assert.rejects(restoreWorker(deps({ fs }), () => {}), /No bundled MDX worker/);
});

test('failed npm install surfaces the exit code', async () => {
  const fs = fakeFs({ '/bundled/package.json': '{}', '/bundled/package-lock.json': lockfile });
  fs.failInstall = true;
  await assert.rejects(restoreWorker(deps({ fs }), () => {}), /exit 1/);
});

test('lock hash is null without a lockfile', async () => {
  const fs = fakeFs({ '/bundled/package.json': '{}' });
  assert.equal(await workerLockHash(fs, '/bundled'), null);
});


test('concurrent worker restores share one install through the success marker', async () => {
  const restoring = fakeFs({ '/bundled/package.json': '{}', '/bundled/package-lock.json': lockfile });
  let entered!: () => void;
  const installationEntered = new Promise<void>((resolve) => { entered = resolve; });
  let release!: () => void;
  const installation = new Promise<void>((resolve) => { release = resolve; });
  let installed = false;
  restoring.isDirectory = async (dir) => dir.endsWith('node_modules') && installed;
  restoring.runNpmCi = async (cwd) => {
    restoring.installs.push(cwd);
    entered();
    await installation;
    installed = true;
    return { exit: 0, stdout: '', stderr: '' };
  };
  const first = restoreWorker(deps({ fs: restoring }), () => {});
  await installationEntered;
  const second = restoreWorker(deps({ fs: restoring }), () => {});
  await new Promise<void>((resolve) => setImmediate(resolve));
  release();
  const result = await Promise.all([first, second]);
  assert.ok(result.every((item) => item.ready));
  assert.equal(restoring.installs.length, 1, 'Concurrent npm ci can replace node_modules after readiness.');
  assert.equal(restoring.copied.length, 1);
});


test('failed shared worker restore permits a later retry', async () => {
  const restoring = fakeFs({ '/bundled/package.json': '{}', '/bundled/package-lock.json': lockfile });
  restoring.failInstall = true;
  const results = await Promise.allSettled([
    restoreWorker(deps({ fs: restoring }), () => {}),
    restoreWorker(deps({ fs: restoring }), () => {}),
  ]);
  assert.ok(results.every((item) => item.status === 'rejected'));
  assert.equal(restoring.installs.length, 1);
  restoring.failInstall = false;
  restoring.isDirectory = async (dir) => dir.endsWith('node_modules');
  assert.equal((await restoreWorker(deps({ fs: restoring }), () => {})).ready, true);
  assert.equal(restoring.installs.length, 2, 'Failure must release the per-directory restore guard.');
});

test('a blocked worker restore does not block a different directory or bypass trust', async () => {
  const restoring = fakeFs({ '/first/package-lock.json': lockfile, '/second/package-lock.json': lockfile });
  let release!: () => void;
  const installation = new Promise<void>((resolve) => { release = resolve; });
  restoring.runNpmCi = async (cwd) => {
    restoring.installs.push(cwd);
    await installation;
    return { exit: 0, stdout: '', stderr: '' };
  };
  restoring.isDirectory = async () => false;
  const first = restoreWorker(deps({ fs: restoring, workerPathSetting: '/first' }), () => {});
  const second = restoreWorker(deps({ fs: restoring, workerPathSetting: '/second' }), () => {});
  await new Promise<void>((resolve) => setImmediate(resolve));
  try {
    assert.deepEqual(restoring.installs.map((dir) => dir.replace(/\\/g, '/')).sort(), ['/first', '/second']);
    await assert.rejects(restoreWorker(deps({ fs: restoring, workerPathSetting: '/first', isTrusted: false }), () => {}),
      UntrustedWorkspaceError);
  } finally {
    restoring.isDirectory = async (dir) => dir.endsWith('node_modules');
    release();
    await Promise.all([first, second]);
  }
});
