import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { UntrustedWorkspaceError } from '../../src/trust.js';
import { createFakeVscode, installFakeVscode } from './fakeVscode.js';
import { fixturePath } from './helpers.js';

// eslint-disable-next-line @typescript-eslint/no-require-imports
function loadExtension(fake: ReturnType<typeof createFakeVscode>): typeof import('../../src/extension.js') {
  installFakeVscode(fake);
  // Fresh module per fake: the vscode binding happens at load time.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const key = require.resolve('../../src/extension.js');
  delete require.cache[key];
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  return require('../../src/extension.js') as typeof import('../../src/extension.js');
}

function folder(fsPath: string, name: string): { uri: { fsPath: string }; name: string } {
  return { uri: { fsPath }, name };
}

test('untrusted handler call executes nothing', async () => {
  const fake = createFakeVscode();
  const { selectProjectHandler } = loadExtension(fake);
  const calls: unknown[] = [];
  await assert.rejects(
    selectProjectHandler({
      isTrusted: false,
      folders: [],
      showQuickPick: async () => undefined,
      saveSelection: async () => {},
      cliPathSetting: '',
      envPath: '',
      probe: {
        calls: calls as never,
        run: async () => {
          calls.push(1);
          return { exit: 0, stdout: '' };
        },
      },
    }),
    UntrustedWorkspaceError,
  );
  assert.equal(calls.length, 0);
});

test('single project auto-selects and reports version', async () => {
  const fake = createFakeVscode();
  const { selectProjectHandler } = loadExtension(fake);
  const saved: { key: string; projectPath: string }[] = [];
  const calls: { command: string[]; cwd: string | undefined }[] = [];
  const bin = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'lithosharp-ext-'));
  const exe = path.join(bin, process.platform === 'win32' ? 'lithosharp.exe' : 'lithosharp');
  await fs.promises.writeFile(exe, '');
  const picked = await selectProjectHandler({
    isTrusted: true,
    folders: [folder(fixturePath('single'), 'single')],
    showQuickPick: async () => {
      throw new Error('must not prompt for one project');
    },
    saveSelection: async (key, projectPath) => {
      saved.push({ key, projectPath });
    },
    cliPathSetting: exe,
    envPath: '',
    probe: {
      calls,
      run: async (command, cwd) => {
        calls.push({ command, cwd });
        return { exit: 0, stdout: 'lithosharp 1.1.0' };
      },
    },
  });
  assert.equal(picked?.candidate.displayName, 'app');
  assert.equal(picked?.version, '1.1.0');
  assert.equal(saved.length, 1);
  assert.ok(saved[0]!.key.includes('single'));
  assert.equal(calls.length, 1);
});

test('same-name projects prompt with workspace identity', async () => {
  const fake = createFakeVscode();
  fake.pickedIndex = 1;
  const { selectProjectHandler } = loadExtension(fake);
  let shown: { label: string; description: string }[] = [];
  const picked = await selectProjectHandler({
    isTrusted: true,
    folders: [folder(fixturePath('multi', 'a'), 'a'), folder(fixturePath('multi', 'b'), 'b')],
    showQuickPick: async (items) => {
      shown = items as { label: string; description: string }[];
      return items[1];
    },
    saveSelection: async () => {},
    cliPathSetting: '',
    envPath: '',
  });
  assert.equal(shown.length, 2);
  assert.equal(shown[0]!.label, shown[1]!.label);
  assert.notEqual(shown[0]!.description, shown[1]!.description);
  assert.equal(picked?.candidate.workspaceName, 'b');
});

test('activate wires disposables and deactivate cleans up', async () => {
  const fake = createFakeVscode();
  fake.folders = [];
  const { activate, deactivate } = loadExtension(fake);
  const subscriptions: { dispose(): void }[] = [];
  activate({ subscriptions } as never);
  assert.ok(subscriptions.length >= 12);
  for (const id of ['lithosharp.selectProject', 'lithosharp.build', 'lithosharp.startServer', 'lithosharp.stopServer', 'lithosharp.inspectSite', 'lithosharp.restartServer', 'lithosharp.openPreview', 'lithosharp.refreshPreview', 'lithosharp.openInBrowser']) {
    assert.ok(fake.commands.has(id), `missing command ${id}`);
  }
  deactivate();
  for (const subscription of subscriptions) {
    subscription.dispose();
  }
});

test('untrusted build and serve commands never execute', async () => {
  const fake = createFakeVscode();
  fake.isTrusted = false;
  const { activate } = loadExtension(fake);
  const subscriptions: { dispose(): void }[] = [];
  activate({ subscriptions } as never);
  for (const id of ['lithosharp.build', 'lithosharp.startServer', 'lithosharp.stopServer', 'lithosharp.inspectSite', 'lithosharp.restartServer']) {
    await assert.rejects(fake.commands.get(id)!(), UntrustedWorkspaceError);
  }
});

test('configuration change refreshes the project state', async () => {
  const fake = createFakeVscode();
  fake.isTrusted = false;
  const { activate } = loadExtension(fake);
  const subscriptions: { dispose(): void }[] = [];
  activate({ subscriptions } as never);
  assert.equal(fake.configListeners.length, 1);
  fake.isTrusted = true;
  fake.folders = [folder(fixturePath('single'), 'single')];
  fake.configListeners[0]!({ affectsConfiguration: (section) => section === 'lithosharp' });
  const watch = Date.now();
  while (Date.now() - watch < 5000) {
    if (fake.statusText.some((text) => text.includes('select project'))) {
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  assert.ok(fake.statusText.some((text) => text.includes('select project')));
});
