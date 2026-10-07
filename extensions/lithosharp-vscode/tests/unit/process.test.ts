import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { spawnProcess } from '../../src/process.js';

test('windows no-argument script launchers run through cmd without shell arguments', async () => {
  if (process.platform !== 'win32') {
    return;
  }
  const dir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'lithosharp proc-'));
  const script = path.join(dir, 'hello.cmd');
  await fs.promises.writeFile(script, '@echo off\r\necho hello-proc\r\n');
  assert.throws(() => spawnProcess([script, 'untrusted argument'], dir), /cannot safely receive arguments/);
  const child = spawnProcess([script], dir);
  const output = await new Promise<string>((resolve, reject) => {
    let text = '';
    const timer = setTimeout(() => reject(new Error('timed out waiting for script output')), 15000);
    child.onStdout((chunk) => {
      text += chunk.toString('utf8');
      if (text.includes('hello-proc')) {
        clearTimeout(timer);
        resolve(text);
      }
    });
    child.onExit(() => {
      clearTimeout(timer);
      resolve(text);
    });
  });
  assert.match(output, /hello-proc/);
  child.killTree();
});

test('missing executable reports stderr and settles each exit listener without crashing', async () => {
  const child = spawnProcess([path.join(os.tmpdir(), `missing-lithosharp-${process.pid}.exe`)], os.tmpdir());
  let errors = '';
  child.onStderr((chunk) => { errors += chunk.toString('utf8'); });
  const stopped = await Promise.all([1, 2].map(() => new Promise<number | null>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('missing executable did not settle')), 5000);
    child.onExit((code) => { clearTimeout(timer); resolve(code); });
  })));
  assert.deepEqual(stopped, [null, null]);
  assert.match(errors, /ENOENT/);
});
