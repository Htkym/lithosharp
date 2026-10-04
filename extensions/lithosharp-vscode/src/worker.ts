import { createHash } from 'node:crypto';
import * as path from 'node:path';
import { requireTrusted } from './trust.js';

/** File surface the worker logic needs. VS Code types stay at the boundary. */
export interface WorkerFileSystem {
  readFile(filePath: string): Promise<Buffer | null>;
  isDirectory(dirPath: string): Promise<boolean>;
  isFile(filePath: string): Promise<boolean>;
  ensureDir(dirPath: string): Promise<void>;
  writeFile(filePath: string, contents: string): Promise<void>;
  copySourceTree(from: string, to: string): Promise<number>;
  runNpmCi(cwd: string): Promise<{ exit: number; stdout: string; stderr: string }>;
}

export interface WorkerDeps {
  isTrusted: boolean;
  /** Explicit lithosharp.workerDirectory setting, untrimmed. */
  workerPathSetting: string;
  /** Extension global storage directory (never a user project). */
  storageDir: string;
  /** VSIX-bundled worker source (package.json plus lockfile, no node_modules). */
  bundledDir: string;
  fs: WorkerFileSystem;
}

export interface ResolvedWorker {
  /** Worker directory to hand to the server. Empty when none is available. */
  directory: string;
  /** SHA-256 of package-lock.json when present, otherwise null. */
  lockHash: string | null;
  /** True when node_modules exists, so MDX analysis can run. */
  ready: boolean;
  source: 'explicit' | 'storage' | 'none';
}

/** SHA-256 of a worker package-lock.json, or null when it is absent. */
export async function workerLockHash(fs: WorkerFileSystem, dir: string): Promise<string | null> {
  const bytes = await fs.readFile(path.join(dir, 'package-lock.json'));
  if (!bytes) {
    return null;
  }
  return createHash('sha256').update(bytes).digest('hex');
}

function storageName(lockHash: string | null): string {
  return lockHash ? `worker-${lockHash.slice(0, 12)}` : 'worker-unversioned';
}

/**
 * Resolves the worker directory without installing anything. An explicit
 * setting always wins; otherwise the hash-named storage directory backs the
 * bundled worker. Markdown never needs the result: an empty or unready
 * directory only disables MDX diagnostics.
 */
export async function resolveWorker(deps: WorkerDeps): Promise<ResolvedWorker> {
  requireTrusted(deps.isTrusted, 'resolve the MDX worker');
  const explicit = deps.workerPathSetting.trim();
  if (explicit !== '') {
    const lockHash = await workerLockHash(deps.fs, explicit);
    const ready = await isRestored(deps.fs, explicit, lockHash);
    return { directory: explicit, lockHash, ready, source: 'explicit' };
  }
  const bundledLock = await workerLockHash(deps.fs, deps.bundledDir);
  if (bundledLock === null) {
    return { directory: '', lockHash: null, ready: false, source: 'none' };
  }
  const directory = path.join(deps.storageDir, storageName(bundledLock));
  const ready = await isRestored(deps.fs, directory, bundledLock);
  return { directory, lockHash: bundledLock, ready, source: 'storage' };
}

const restores = new Map<string, Promise<ResolvedWorker>>();

/**
 * Restores the bundled worker source into the resolved directory and runs
 * `npm ci` there. Only extension storage or an explicit directory is ever
 * written: user projects, global npm state and user npm configuration are
 * never touched. Returns silently when the target is already restored.
 */
export async function restoreWorker(
  deps: WorkerDeps,
  onLog: (line: string) => void,
): Promise<ResolvedWorker> {
  requireTrusted(deps.isTrusted, 'restore the MDX worker');
  const resolved = await resolveWorker(deps);
  if (resolved.source === 'none') {
    throw new Error('No bundled MDX worker is packaged with this extension.');
  }
  if (resolved.lockHash === null) {
    throw new Error('The MDX worker has no package-lock.json; a locked restore cannot be performed.');
  }
  const lockHash = resolved.lockHash;
  const absolute = path.resolve(resolved.directory);
  const key = process.platform === 'win32' ? absolute.toLowerCase() : absolute;
  const active = restores.get(key);
  if (active) return active;
  const pending = (async (): Promise<ResolvedWorker> => {
    // Another restore may have completed while initial discovery was pending.
    const current = { ...resolved, ready: await isRestored(deps.fs, resolved.directory, lockHash) };
    if (current.ready) {
      onLog(`MDX worker is already restored: ${current.directory}`);
      return current;
    }
    if (current.source === 'storage') {
      const copied = await deps.fs.copySourceTree(deps.bundledDir, current.directory);
      onLog(`Copied ${copied} worker files to ${current.directory}.`);
    }
    await deps.fs.writeFile(path.join(current.directory, '.lithosharp-worker-lock'), '');
    const result = await deps.fs.runNpmCi(current.directory);
    if (result.exit !== 0) {
      throw new Error(`npm ci failed in ${current.directory} (exit ${result.exit}).`);
    }
    const ready = await deps.fs.isDirectory(path.join(current.directory, 'node_modules'));
    if (!ready) {
      throw new Error(`npm ci reported success but node_modules is missing in ${current.directory}.`);
    }
    await deps.fs.writeFile(path.join(current.directory, '.lithosharp-worker-lock'), lockHash);
    onLog(`MDX worker restored: ${current.directory}`);
    return { ...current, ready: true };
  })();
  restores.set(key, pending);
  try {
    return await pending;
  } finally {
    if (restores.get(key) === pending) restores.delete(key);
  }
}

async function isRestored(fs: WorkerFileSystem, directory: string, lockHash: string | null): Promise<boolean> {
  if (lockHash === null || !(await fs.isDirectory(path.join(directory, 'node_modules')))) {
    return false;
  }

  const marker = await fs.readFile(path.join(directory, '.lithosharp-worker-lock'));
  return marker?.toString('utf8').trim() === lockHash;
}
