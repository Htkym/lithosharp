import assert from 'node:assert/strict';
import test from 'node:test';
import { resolveNpmCiCommand } from '../../src/npmRestore.js';

const ci = ['ci', '--ignore-scripts', '--no-audit', '--no-fund'];
const key = (file: string): string => file.replace(/\//g, '\\').toLowerCase();
function options(files: string[], overrides: Partial<Parameters<typeof resolveNpmCiCommand>[0]> = {}) {
  const known = new Set(files.map(key));
  return { platform: 'win32' as const, nodeExecutable: '', envPath: 'C:\\Node', cwd: 'C:\\worker',
    isFile: async (file: string) => known.has(key(file)), readNpmPrefix: async () => '', ...overrides };
}

test('Windows resolves the real npm CLI before spawning, even with a PATH over cmd limits', async () => {
  const node = 'C:\\Node\\node.exe';
  const cli = 'C:\\Node\\node_modules\\npm\\bin\\npm-cli.js';
  const envPath = Array.from({ length: 300 }, (_, i) => 'C:\\irrelevant-directory-' + i).join(';') + ';C:\\Node';
  assert.ok(envPath.length > 8191);
  const command = await resolveNpmCiCommand(options([node, 'C:\\Node\\npm.cmd', cli], { envPath }));
  assert.deepEqual(command, [node, cli, ...ci]);
  assert.ok(!command.some(arg => /cmd\.exe|npm\.cmd/i.test(arg)));
});

test('configured Node and npm paths with spaces and shell metacharacters stay separate arguments', async () => {
  const node = 'C:\\Node & Tools\\node.exe';
  const cli = 'C:\\Npm & Tools\\node_modules\\npm\\bin\\npm-cli.js';
  const command = await resolveNpmCiCommand(options([node, 'C:\\Npm & Tools\\npm.cmd', cli], {
    nodeExecutable: node, envPath: '"C:\\Npm & Tools";C:\\Other',
  }));
  assert.deepEqual(command, [node, cli, ...ci]);
});

test('npm PATH precedence is retained rather than silently choosing bundled npm', async () => {
  const node = 'C:\\Node\\node.exe';
  const cli = 'C:\\Preferred\\node_modules\\npm\\bin\\npm-cli.js';
  const command = await resolveNpmCiCommand(options([node, 'C:\\Preferred\\npm.cmd', cli,
    'C:\\Node\\npm.cmd', 'C:\\Node\\node_modules\\npm\\bin\\npm-cli.js'], {
    envPath: 'C:\\Preferred;C:\\Node',
  }));
  assert.deepEqual(command, [node, cli, ...ci]);
});

test('default Node stays paired with npm.cmd even when another Node leads PATH', async () => {
  const node = 'C:\\Pinned Npm\\node.exe';
  const cli = 'C:\\Pinned Npm\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options(['C:\\Other Node\\node.exe', node,
    'C:\\Pinned Npm\\npm.cmd', cli], { envPath: 'C:\\Other Node;C:\\Pinned Npm' })), [node, cli, ...ci]);
});

test('explicit Node overrides npm.cmd adjacent Node', async () => {
  const node = 'C:\\Fixed Node\\node.exe';
  const cli = 'C:\\Npm\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, 'C:\\Npm\\node.exe', 'C:\\Npm\\npm.cmd', cli],
    { nodeExecutable: node, envPath: 'C:\\Npm' })), [node, cli, ...ci]);
});

test('standard npm prefix redirect uses the selected Node and final output line', async () => {
  const node = 'C:\\Node\\node.exe';
  const prefixScript = 'C:\\Node\\node_modules\\npm\\bin\\npm-prefix.js';
  const cli = 'C:\\Roaming Npm\\node_modules\\npm\\bin\\npm-cli.js';
  let probes = 0;
  const command = await resolveNpmCiCommand(options([node, 'C:\\Node\\npm.cmd', prefixScript, cli], {
    readNpmPrefix: async (actualNode, actualScript) => {
      assert.equal(actualNode, node); assert.equal(actualScript, prefixScript); probes++;
      return 'C:\\Ignored Prefix\r\nC:\\Roaming Npm\r\n';
    },
  }));
  assert.equal(probes, 1); assert.deepEqual(command, [node, cli, ...ci]);
});

test('missing prefix target retains the original npm CLI', async () => {
  const node = 'C:\\Node\\node.exe';
  const cli = 'C:\\Node\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, 'C:\\Node\\npm.cmd', cli,
    'C:\\Node\\node_modules\\npm\\bin\\npm-prefix.js'], { readNpmPrefix: async () => 'C:\\Missing Prefix\r\n' })), [node, cli, ...ci]);
});

test('empty prefix output retains the original npm CLI', async () => {
  const node = 'C:\\Node\\node.exe';
  const cli = 'C:\\Node\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, 'C:\\Node\\npm.cmd', cli,
    'C:\\Node\\node_modules\\npm\\bin\\npm-prefix.js'])), [node, cli, ...ci]);
});

test('an explicit Node distribution uses its bundled npm when absent from PATH', async () => {
  const node = 'C:\\Private Node\\node.exe';
  const cli = 'C:\\Private Node\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, cli], { nodeExecutable: node, envPath: '' })), [node, cli, ...ci]);
});

test('a configured command name resolves to an absolute executable without a shell', async () => {
  const node = 'C:\\Node\\custom-node.exe';
  const cli = 'C:\\Node\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, cli], { nodeExecutable: 'custom-node' })), [node, cli, ...ci]);
});

test('a configured relative executable is resolved against the restore cwd', async () => {
  const node = 'C:\\worker\\tools\\node.exe';
  const cli = 'C:\\worker\\tools\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, cli], { nodeExecutable: '.\\tools\\node.exe', envPath: '' })), [node, cli, ...ci]);
});

test('relative PATH entries are resolved against the restore cwd', async () => {
  const node = 'C:\\worker\\tools\\node.exe';
  const cli = 'C:\\worker\\tools\\node_modules\\npm\\bin\\npm-cli.js';
  assert.deepEqual(await resolveNpmCiCommand(options([node, 'C:\\worker\\tools\\npm.cmd', cli], { envPath: '.\\tools' })), [node, cli, ...ci]);
});

test('missing configured Node fails actionably without selecting another Node', async () => {
  await assert.rejects(resolveNpmCiCommand(options(['C:\\Node\\node.exe'], { nodeExecutable: 'C:\\Missing\\node.exe' })), /Configured lithosharp\.nodeExecutable was not found/);
});

test('missing default Node reports the supported install or setting', async () => {
  await assert.rejects(resolveNpmCiCommand(options([])), /Node\.js 24\.13\.0 or set lithosharp\.nodeExecutable/);
});

test('an incomplete selected npm install fails instead of falling through to another npm', async () => {
  await assert.rejects(resolveNpmCiCommand(options(['C:\\Node\\node.exe', 'C:\\First\\npm.cmd',
    'C:\\Node\\npm.cmd', 'C:\\Node\\node_modules\\npm\\bin\\npm-cli.js'], { envPath: 'C:\\First;C:\\Node' })), /npm\.cmd selected from PATH has no npm CLI/);
});

test('missing npm reports the repair instead of invoking cmd', async () => {
  await assert.rejects(resolveNpmCiCommand(options(['C:\\Node\\node.exe'])), /npm was not found beside Node or on PATH/);
});

test('POSIX retains the existing npm invocation without Windows file probes', async () => {
  assert.deepEqual(await resolveNpmCiCommand(options([], { platform: 'linux', isFile: async () => { throw new Error('No Windows lookup expected'); } })), ['npm', ...ci]);
});
