// Stages the MDX worker source for VSIX packaging (V110-23). Copies only the
// runtime source plus package.json and the lockfile into
// resources/worker/, never node_modules, caches, tests or VCS state, and
// records the lockfile hash the extension uses to name its storage directory.
import * as fs from 'node:fs';
import * as path from 'node:path';
import { createHash } from 'node:crypto';

const extensionDir = path.resolve(import.meta.dirname, '..');
const workerDir = path.resolve(extensionDir, '..', '..', 'src', 'LithoSharp.Mdx', 'worker');
const targetDir = path.join(extensionDir, 'resources', 'worker');

const topFiles = ['worker.mjs', 'compiler.mjs', 'package.json', 'package-lock.json'];

function copyTree(from, to) {
  let copied = 0;
  fs.mkdirSync(to, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    if (entry.isSymbolicLink()) {
      continue;
    }
    const source = path.join(from, entry.name);
    const target = path.join(to, entry.name);
    if (entry.isDirectory()) {
      copied += copyTree(source, target);
    } else if (entry.isFile()) {
      fs.copyFileSync(source, target);
      copied += 1;
    }
  }
  return copied;
}

function hashFile(filePath) {
  return createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}

fs.rmSync(targetDir, { recursive: true, force: true });
let copied = 0;
for (const file of topFiles) {
  const source = path.join(workerDir, file);
  if (!fs.existsSync(source)) {
    throw new Error(`Worker source is missing: ${source}`);
  }
  fs.mkdirSync(targetDir, { recursive: true });
  fs.copyFileSync(source, path.join(targetDir, file));
  copied += 1;
}
copied += copyTree(path.join(workerDir, 'runtime'), path.join(targetDir, 'runtime'));

const staged = [];
function collect(dir, prefix) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    const relative = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) {
      collect(full, relative);
    } else {
      staged.push({ path: relative, sha256: hashFile(full) });
    }
  }
}
collect(targetDir, '');
staged.sort((a, b) => (a.path < b.path ? -1 : 1));

const manifest = {
  schemaVersion: '1.0',
  lockHash: hashFile(path.join(targetDir, 'package-lock.json')),
  files: staged,
};
fs.writeFileSync(path.join(targetDir, 'worker.json'), `${JSON.stringify(manifest, null, 2)}\n`);
console.log(`staged ${copied} worker files, lock ${manifest.lockHash.slice(0, 12)}`);
