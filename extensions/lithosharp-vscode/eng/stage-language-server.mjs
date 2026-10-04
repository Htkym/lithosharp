// Framework-dependent LSP: retain deps/runtimeconfig, managed/native assets
// and worker sources. No runtime installation, trimming, AOT or fixed RID.
import * as fs from 'node:fs';
import * as path from 'node:path';
import { spawnSync } from 'node:child_process';

const extensionDir = path.resolve(import.meta.dirname, '..');
const repo = path.resolve(extensionDir, '..', '..');
const target = path.join(extensionDir, 'resources', 'language-server');
const staging = path.join(extensionDir, 'resources', `.language-server-stage-${process.pid}`);
const backup = path.join(extensionDir, 'resources', `.language-server-backup-${process.pid}`);
if (fs.existsSync(backup)) throw new Error(`Retained language server backup already exists: ${backup}`);
fs.mkdirSync(staging, { recursive: true });
try {
  const result = spawnSync('dotnet', [
    'publish', path.join(repo, 'src', 'LithoSharp.LanguageServer', 'LithoSharp.LanguageServer.csproj'),
    '--configuration', 'Release', '--no-restore', '--self-contained', 'false',
    '-p:UseAppHost=false', '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', '-p:PublishAot=false',
    '--output', staging,
  ], { cwd: repo, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Language server publish failed (${result.status}). Restore the solution before staging.`);
  for (const required of [
    'LithoSharp.LanguageServer.dll', 'LithoSharp.LanguageServer.deps.json',
    'LithoSharp.LanguageServer.runtimeconfig.json', 'LithoSharp.dll', 'LithoSharp.Mdx.dll',
    'worker/worker.mjs', 'worker/compiler.mjs', 'worker/package.json', 'worker/package-lock.json',
    'worker/runtime/components.mjs',
  ]) {
    if (!fs.statSync(path.join(staging, required)).isFile()) throw new Error(`Published server is missing ${required}`);
  }
  if (!fs.statSync(path.join(staging, 'runtimes')).isDirectory()) throw new Error('Published server has no native runtime assets.');
  if (fs.existsSync(path.join(staging, 'worker', 'node_modules'))) throw new Error('Published worker must not contain node_modules.');
  const hadPrevious = fs.existsSync(target);
  if (hadPrevious) fs.renameSync(target, backup);
  try {
    fs.renameSync(staging, target);
  } catch (error) {
    if (hadPrevious) fs.renameSync(backup, target);
    throw error;
  }
  if (hadPrevious) fs.rmSync(backup, { recursive: true, force: true });
  console.log('staged framework-dependent language server (requires .NET 10 runtime)');
} finally {
  fs.rmSync(staging, { recursive: true, force: true });
}
