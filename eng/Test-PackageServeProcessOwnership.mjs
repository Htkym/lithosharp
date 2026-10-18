import assert from 'node:assert/strict';
import test from 'node:test';
import { PackageServeProcesses } from './package-serve-processes.mjs';

const row = (pid, parent, birth) => ({ pid, parent, birth });
const root = row(100, 1, '2026-10-18T00:59:00.1234567Z');
const worker = row(200, 100, '2026-10-18T00:59:00.1234568Z');
const grandchild = row(300, 200, '2026-10-18T00:59:01.0000000Z');
const unrelated = row(900, 1, '2026-10-18T00:36:20.0000000Z');

test('captures genuine descendants even when enumeration is not in parent order', () => {
  const processes = new PackageServeProcesses(root.pid);
  const unknown = { ...unrelated, birth: null };
  processes.capture([grandchild, worker, unknown, root]);
  assert.deepEqual(processes.remaining([grandchild, worker, unknown]), [grandchild, worker]);
  assert.deepEqual(processes.remaining([unknown]), []);
  assert.deepEqual(processes.evidence().root, { pid: root.pid, birth: root.birth });
});

test('rejects stale parent PID links to processes older than the spawned root', () => {
  const processes = new PackageServeProcesses(root.pid);
  const stale = row(4524, root.pid, '2026-10-18T00:36:20.7322770Z');
  const staleChild = row(6664, stale.pid, '2026-10-18T00:36:20.7433020Z');
  processes.capture([root, stale, staleChild, worker]);
  assert.deepEqual(processes.remaining([stale, staleChild, worker]), [worker]);
});

test('checks creation order at every edge, with Windows sub-millisecond precision', () => {
  const processes = new PackageServeProcesses(root.pid);
  const stale = row(400, worker.pid, root.birth);
  processes.capture([root, worker, stale, grandchild]);
  assert.deepEqual(processes.remaining([stale, grandchild]), [grandchild]);
});

test('does not authorize cleanup of a recorded PID reused by another process', () => {
  const processes = new PackageServeProcesses(root.pid);
  processes.capture([root, worker]);
  const reused = row(worker.pid, 1, '2026-10-18T01:00:00.0000000Z');
  const terminated = [];
  for (const process of processes.remaining([unrelated, reused])) terminated.push(process.pid);
  assert.deepEqual(terminated, []);
});

test('requires the same root creation identity on every capture', () => {
  const processes = new PackageServeProcesses(root.pid);
  processes.capture([root, worker]);
  const reused = { ...root, birth: grandchild.birth };
  assert.throws(() => processes.capture([reused]), /root process identity changed/);
  assert.deepEqual(processes.remaining([unrelated, reused]), []);
});

test('missing root or creation identity cannot produce successful empty ownership', () => {
  const processes = new PackageServeProcesses(root.pid);
  assert.throws(() => processes.capture([unrelated]), /root process identity unavailable/);
  assert.throws(() => processes.remaining([unrelated]), /never captured/);
  assert.throws(() => processes.capture([{ ...root, birth: undefined }]), /Missing process creation identity/);
  assert.throws(() => processes.capture([root, { ...worker, birth: '' }]), /Invalid process creation identity/);
});

test('missing identity during shutdown or cleanup fails before any termination is authorized', () => {
  const processes = new PackageServeProcesses(root.pid);
  processes.capture([root, worker]);
  const terminated = [];
  assert.throws(() => {
    for (const process of processes.remaining([root, { ...worker, birth: null }])) terminated.push(process.pid);
  }, /Missing process creation identity/);
  assert.deepEqual(terminated, []);
  assert.equal(processes.evidence().observed[1].pid, worker.pid);
  assert.throws(() => processes.remaining([]), /enumeration returned no identities/);
});

test('rejects ambiguous duplicate PID snapshots', () => {
  const processes = new PackageServeProcesses(root.pid);
  assert.throws(() => processes.capture([root, { ...root, birth: worker.birth }]), /Duplicate process PID/);
});

test('accepts ps lstart second precision and rejects an older Unix descendant', () => {
  const processes = new PackageServeProcesses(root.pid);
  const unixRoot = { ...root, birth: 'Sun Oct 18 00:59:00 2026' };
  const unixWorker = { ...worker, birth: unixRoot.birth };
  const stale = row(400, worker.pid, 'Sun Oct 18 00:58:59 2026');
  processes.capture([unixRoot, unixWorker, stale]);
  assert.deepEqual(processes.remaining([unixWorker, stale]), [unixWorker]);
});
