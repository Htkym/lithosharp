// Launches the Extension Host for integration tests (V110-19).
// Usage: node tests/integration/run.mjs [workspace-folder]
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { runTests } from '@vscode/test-electron';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const workspace = process.argv[2] ?? path.join(root, 'tests', 'fixtures', 'workspace');

const verbose = process.env['LITHOSHARP_VERBOSE'] === '1' ? ['--verbose'] : [];
await runTests({
  version: '1.139.1',
  extensionDevelopmentPath: root,
  extensionTestsPath: path.join(root, 'out', 'tests', 'integration', 'index.js'),
  extensionTestsEnv: {
    LITHOSHARP_LS_DLL: process.env['LITHOSHARP_LS_DLL'] ?? '',
  },
  launchArgs: [workspace, '--disable-gpu', '--disable-workspace-trust', ...verbose],
});
