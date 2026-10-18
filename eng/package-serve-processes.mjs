import assert from 'node:assert/strict';

function birthTime(birth) {
  assert.equal(typeof birth, 'string', 'Missing process creation identity');
  const utc = birth.match(/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$/);
  assert.ok(utc || /^[A-Z][a-z]{2} [A-Z][a-z]{2}\s+\d{1,2} \d{2}:\d{2}:\d{2} \d{4}$/.test(birth), 'Invalid process creation identity');
  const milliseconds = Date.parse(utc ? `${utc[1]}Z` : birth);
  assert.ok(Number.isFinite(milliseconds), 'Invalid process creation identity');
  // Preserve Windows sub-millisecond creation order; ps lstart has second precision.
  return BigInt(milliseconds) * 1_000_000n + BigInt((utc?.[2] ?? '').padEnd(9, '0'));
}

function indexRows(rows) {
  assert.ok(Array.isArray(rows) && rows.length > 0, 'Process enumeration returned no identities');
  const indexed = new Map();
  for (const row of rows) {
    assert.ok(Number.isSafeInteger(row.pid) && row.pid >= 0 && Number.isSafeInteger(row.parent) && row.parent >= 0, 'Invalid process PID');
    assert.ok(!indexed.has(row.pid), 'Duplicate process PID');
    indexed.set(row.pid, row);
  }
  return indexed;
}

export class PackageServeProcesses {
  constructor(rootPid) {
    this.rootPid = rootPid;
    this.root = null;
    this.owned = new Map();
    this.observed = [];
  }

  capture(rows) {
    const indexed = indexRows(rows);
    const root = indexed.get(this.rootPid);
    this.observed = rows.filter(row => row.pid === this.rootPid || this.owned.has(row.pid));
    assert.ok(root && root.pid > 0, 'Spawned root process identity unavailable');
    birthTime(root.birth);
    assert.ok(!this.root || this.root.birth === root.birth, 'Spawned root process identity changed');
    this.root ??= { pid: root.pid, birth: root.birth };
    this.owned.set(root.pid, this.root);
    const descendants = new Map([[root.pid, root]]);
    for (let changed = true; changed;) {
      changed = false;
      for (const row of rows) {
        const parent = descendants.get(row.parent);
        if (!parent || descendants.has(row.pid)) continue;
        if (birthTime(row.birth) < birthTime(parent.birth)) continue;
        descendants.set(row.pid, row);
        changed = true;
      }
    }
    for (const row of descendants.values()) this.owned.set(row.pid, { pid: row.pid, birth: row.birth });
    this.observed = rows.filter(row => this.owned.has(row.pid));
  }

  requireRoot() {
    assert.ok(this.root, 'Spawned root process identity was never captured');
  }

  remaining(rows) {
    this.requireRoot();
    indexRows(rows);
    this.observed = rows.filter(row => this.owned.has(row.pid));
    return this.observed.filter(row => {
      birthTime(row.birth);
      return this.owned.get(row.pid).birth === row.birth;
    });
  }

  evidence() {
    return { root: this.root, owned: [...this.owned.values()], observed: this.observed };
  }
}
