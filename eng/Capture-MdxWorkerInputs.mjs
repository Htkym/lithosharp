import {createHash} from 'node:crypto';
import {readFile, readdir} from 'node:fs/promises';
import path from 'node:path';

export async function captureMdxWorkerInputs(workerDirectory) {
  const rootEntries = await readdir(workerDirectory, {withFileTypes: true});
  const required = ['compiler.mjs', 'worker.mjs', 'package.json', 'package-lock.json'];
  const rootFiles = rootEntries.filter(entry => entry.name.endsWith('.mjs') || required.includes(entry.name));
  for (const name of required)
    if (!rootFiles.some(entry => entry.name === name)) throw new Error('Missing worker input: ' + name);
  const runtimeFiles = (await readdir(path.join(workerDirectory, 'runtime'), {withFileTypes: true}))
    .filter(entry => entry.name.endsWith('.mjs') || entry.name.endsWith('.css'));
  const files = [];
  for (const [prefix, entries] of [['', rootFiles], ['runtime/', runtimeFiles]]) {
    for (const entry of entries) {
      if (!entry.isFile()) throw new Error('Worker input must be a regular file: ' + prefix + entry.name);
      files.push(prefix + entry.name);
    }
  }
  return Object.fromEntries(await Promise.all(files.sort().map(async name =>
    [name, createHash('sha256').update(await readFile(path.join(workerDirectory, name))).digest('hex')])));
}
