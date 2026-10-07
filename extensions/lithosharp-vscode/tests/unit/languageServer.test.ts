import assert from 'node:assert/strict';
import test from 'node:test';
import * as path from 'node:path';
import { resolveLanguageServerCommand } from '../../src/languageServer.js';

test('untrusted language server resolution ignores both override and bundle', () => {
  assert.equal(resolveLanguageServerCommand(false, 'evil.exe', '/extension', () => { throw new Error('must not probe'); }), undefined);
});

test('empty setting resolves the bundled DLL with an argument array', () => {
  const extension = path.resolve('extension with spaces');
  const dll = path.join(extension, 'resources', 'language-server', 'LithoSharp.LanguageServer.dll');
  assert.deepEqual(resolveLanguageServerCommand(true, '  ', extension, (file) => file === dll), ['dotnet', dll]);
  assert.equal(resolveLanguageServerCommand(true, '', extension, () => false), undefined);
});

test('explicit executable remains authoritative and explicit DLL uses dotnet', () => {
  const probe = () => { throw new Error('override must not probe bundle'); };
  assert.deepEqual(resolveLanguageServerCommand(true, ' custom server.exe ', '/extension', probe), ['custom server.exe']);
  assert.deepEqual(resolveLanguageServerCommand(true, ' custom server.DLL ', '/extension', probe), ['dotnet', 'custom server.DLL']);
});
