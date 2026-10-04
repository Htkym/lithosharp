// Source mode: node tests/integration/run.mjs [workspace-folder]
// Installed mode: node tests/integration/run.mjs --installed-vsix <archive> --sha256 <hash> --cli <native-executable>
// Uses an existing cached host; never downloads VS Code or falls back to the product development path.
import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { runTests, resolveCliPathFromVSCodeExecutablePath } from '@vscode/test-electron';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const args = process.argv.slice(2);
const installed = args[0] === '--installed-vsix';
const oldDiagnostic = process.env.LITHOSHARP_OLD_VSIX_DIAGNOSTIC === '1';
assert.ok(!oldDiagnostic || installed, 'Old-product diagnostic mode requires an explicit installed VSIX.');
const verifyRestart = !oldDiagnostic;
const options = new Map();
if (installed) {
  assert.equal(args.length, 6, 'Installed mode requires --installed-vsix, --sha256, and --cli.');
  for (let i = 0; i < args.length; i += 2) {
    assert.ok(['--installed-vsix', '--sha256', '--cli'].includes(args[i]), 'Unknown installed-mode option.');
    assert.ok(!options.has(args[i]) && args[i + 1], 'Missing or duplicate option.');
    options.set(args[i], args[i + 1]);
  }
} else {
  assert.ok(args.length <= 1 && !args[0]?.startsWith('--'), 'Expected one optional workspace folder.');
}
const host = process.env.LITHOSHARP_VSCODE_EXE ?? (process.platform === 'win32'
  ? path.join(root, '.vscode-test', 'vscode-win32-x64-archive-1.139.1', 'Code.exe') : '');
assert.ok(host && (await fs.stat(host)).isFile(), 'Provide an existing LITHOSHARP_VSCODE_EXE; this runner does not download a host.');
const owned = await fs.mkdtemp(path.join(os.tmpdir(), 'lithosharp-host-'));
const extensions = path.join(owned, 'extensions');
const userData = path.join(owned, 'user-data');
const resultPath = path.join(owned, 'test-result.json');
await fs.mkdir(extensions);
await fs.mkdir(path.join(userData, 'User'), { recursive: true });
await fs.writeFile(path.join(userData, 'User', 'settings.json'), JSON.stringify({
  'telemetry.telemetryLevel': 'off', 'extensions.autoUpdate': false, 'extensions.autoCheckUpdates': false,
}));
const profile = ['--extensions-dir=' + extensions, '--user-data-dir=' + userData];
let workspace = args[0] ?? path.join(root, 'tests', 'fixtures', 'workspace');
let developmentPath = root;
let testsPath = path.join(root, 'out', 'tests', 'integration', 'index.js');
let expectedExtension = '';
let archiveHash = '';
let payloadHashes = {};
let dependencyJunctionTarget = '';
const helperHashes = {};
if (installed) {
  assert.equal(process.platform, 'win32', 'Installed-mode CLI launcher currently supports Windows only.');
  const archivePath = path.resolve(options.get('--installed-vsix'));
  archiveHash = options.get('--sha256').toLowerCase();
  assert.match(archiveHash, /^[a-f0-9]{64}$/);
  const archiveBytes = await fs.readFile(archivePath);
  assert.equal(createHash('sha256').update(archiveBytes).digest('hex'), archiveHash, 'VSIX SHA256 mismatch.');
  const require = createRequire(import.meta.url);
  const JSZip = require('jszip');
  const zip = await JSZip.loadAsync(archiveBytes);
  // Check both the exact archive and current staged product bytes, including every bundled native library.
  for (const [entryPath, entry] of Object.entries(zip.files)) {
    if (entry.dir || !/^extension\/(out\/src\/|resources\/)/.test(entryPath)) continue;
    const relative = entryPath.slice('extension/'.length);
    assert.ok(!relative.split('/').includes('..'), 'Unsafe payload path.');
    const hash = createHash('sha256').update(await entry.async('nodebuffer')).digest('hex');
    assert.equal(createHash('sha256').update(await fs.readFile(path.join(root, relative))).digest('hex'),
      hash, 'Archive/source mismatch: ' + relative);
    payloadHashes[relative] = hash;
  }
  assert.ok(payloadHashes['out/src/extension.js'] && payloadHashes['resources/language-server/LithoSharp.LanguageServer.dll'],
    'Missing product payload.');

  // code.cmd tells us the actual packaged CLI entry point. Execute it directly without cmd.exe interpolation.
  const cliWrapper = resolveCliPathFromVSCodeExecutablePath(host);
  const wrapper = await fs.readFile(cliWrapper, 'utf8');
  const cliMatch = wrapper.match(/"%~dp0\.\.\\Code\.exe"\s+"%~dp0\.\.\\([^"]+cli\.js)"/i);
  assert.ok(cliMatch, 'Unsupported cached VS Code CLI wrapper.');
  const cliJs = path.resolve(path.dirname(cliWrapper), '..', cliMatch[1]);
  assert.ok((await fs.stat(cliJs)).isFile());
  const cli = promisify(execFile);
  const cliOptions = { env: { ...process.env, ELECTRON_RUN_AS_NODE: '1', VSCODE_DEV: '' }, windowsHide: true,
    timeout: 120000, maxBuffer: 4 * 1024 * 1024 };
  const help = (await cli(host, [cliJs, '--help'], cliOptions)).stdout;
  for (const flag of ['--install-extension', '--extensions-dir', '--user-data-dir', '--new-window', '--disable-extension']) {
    assert.ok(help.includes(flag), 'Cached CLI does not advertise ' + flag);
  }
  await cli(host, [cliJs, '--install-extension', archivePath, ...profile], cliOptions);
  for (const entry of await fs.readdir(extensions, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const dir = path.join(extensions, entry.name);
    const manifest = JSON.parse(await fs.readFile(path.join(dir, 'package.json'), 'utf8'));
    if (manifest.name === 'lithosharp' && (manifest.publisher ?? 'undefined_publisher') === 'undefined_publisher') {
      assert.equal(expectedExtension, '', 'Multiple installed product copies.');
      expectedExtension = await fs.realpath(dir);
    }
  }
  assert.ok(expectedExtension, 'VSIX installation did not create the product extension.');
  for (const [relative, hash] of Object.entries(payloadHashes)) {
    assert.equal(createHash('sha256').update(await fs.readFile(path.join(expectedExtension, relative))).digest('hex'),
      hash, 'Installed/archive mismatch: ' + relative);
  }
  // VS Code requires a development location to execute extensionTestsPath. This inert helper contains no product code.
  developmentPath = path.join(owned, 'test-host');
  await fs.mkdir(developmentPath);
  await fs.writeFile(path.join(developmentPath, 'package.json'), JSON.stringify({
    name: 'lithosharp-integration-host', publisher: 'local-test', version: '0.0.0', engines: { vscode: '^1.139.0' },
    main: './main.js', activationEvents: ['*'],
  }));
  await fs.writeFile(path.join(developmentPath, 'main.js'), 'exports.activate = () => {};\n');
  await fs.mkdir(path.join(developmentPath, 'tests', 'suite'), { recursive: true });
  for (const relative of ['index.js', 'suite/smoke.test.js']) {
    await fs.copyFile(path.join(root, 'out', 'tests', 'integration', relative), path.join(developmentPath, 'tests', relative));
  }
  dependencyJunctionTarget = await fs.realpath(path.join(root, 'node_modules'));
  assert.equal(path.relative(await fs.realpath(root), dependencyJunctionTarget), 'node_modules',
    'Test dependencies must be the existing extension node_modules directory.');
  await fs.symlink(dependencyJunctionTarget, path.join(developmentPath, 'node_modules'), 'junction');
  assert.equal(await fs.realpath(path.join(developmentPath, 'node_modules')), dependencyJunctionTarget);
  for (const relative of ['package.json', 'main.js', 'tests/index.js', 'tests/suite/smoke.test.js']) {
    helperHashes[relative] = createHash('sha256').update(await fs.readFile(path.join(developmentPath, relative))).digest('hex');
  }
  testsPath = path.join(developmentPath, 'tests', 'index.js');
  workspace = path.join(owned, 'workspace');
  await fs.mkdir(path.join(workspace, 'content'), { recursive: true });
  const escapeXml = (value) => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
  const core = escapeXml(path.resolve(root, '..', '..', 'src', 'LithoSharp', 'LithoSharp.csproj'));
  await fs.writeFile(path.join(workspace, 'site.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="' + core + '" /></ItemGroup></Project>');
  await fs.writeFile(path.join(workspace, 'SiteFactory.cs'), [
    'using LithoSharp; using LithoSharp.Configuration; using LithoSharp.Content;',
    'public sealed class FixtureFactory : ISiteFactory {',
    'public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default) {',
    'var posts = await new MarkdownPostReader().ReadAllAsync(Path.Combine(context.ProjectDirectory, "content"));',
    'return new SiteDefinition(new SiteSettings { Title="Installed VSIX fixture", BaseUrl="https://example.com/", Language="en", TimeZone="UTC" }, posts) { OutputDirectory="dist" };',
    '} }',
  ].join('\n'));
  await fs.writeFile(path.join(workspace, 'content', 'hello.md'),
    '---\ntitle: Installed fixture\ndate: 2024-01-01\n---\n# Installed fixture\n\ninstalled-vsix-fixture-marker\n');
  const tool = path.resolve(options.get('--cli'));
  assert.ok((await fs.stat(tool)).isFile(), 'Native LithoSharp CLI is missing.');
  await fs.writeFile(path.join(userData, 'User', 'settings.json'), JSON.stringify({
    'telemetry.telemetryLevel': 'off', 'extensions.autoUpdate': false, 'extensions.autoCheckUpdates': false,
    'lithosharp.cliPath': tool,
  }));
  await fs.writeFile(path.join(owned, 'identity.json'), JSON.stringify({
    archiveHash, expectedExtension, developmentPath, testsPath, dependencyJunctionTarget, helperHashes,
    restartCheckRequired: verifyRestart, payloadHashes, cliHash: createHash('sha256').update(await fs.readFile(tool)).digest('hex'),
  }, null, 2));
}
console.log('Integration evidence: ' + owned);
await runTests({
  vscodeExecutablePath: host,
  extensionDevelopmentPath: [developmentPath],
  extensionTestsPath: testsPath,
  extensionTestsEnv: {
    LITHOSHARP_TEST_RESULT: resultPath,
    LITHOSHARP_VERIFY_LSP_RESTART: verifyRestart ? '1' : '0',
    LITHOSHARP_TEST_DEVELOPMENT_PATH: developmentPath,
    LITHOSHARP_INSTALLED_EXTENSION: expectedExtension,
    LITHOSHARP_INSTALLED_EXTENSIONS_DIR: extensions,
    LITHOSHARP_TEST_USER_DATA: userData,
    LITHOSHARP_TEST_WORKSPACE: path.resolve(workspace),
  },
  launchArgs: [workspace, '--new-window', ...profile, '--disable-gpu', '--disable-workspace-trust',
    '--disable-extension=vscode.markdown-language-features',
    ...(process.env.LITHOSHARP_VERBOSE === '1' ? ['--verbose'] : [])],
});
const result = JSON.parse(await fs.readFile(resultPath, 'utf8'));
assert.ok(result.started && result.finished && result.tests >= (installed ? 4 : 3), 'Test runner did not execute all smoke cases.');
assert.equal(result.failures, 0);
assert.equal(result.passes, result.tests, 'Pending/skipped smoke cases do not satisfy installed-package validation.');
console.log(JSON.stringify({ mode: oldDiagnostic ? 'old-vsix-diagnostic' : installed ? 'installed-vsix' : 'source',
  archiveHash, restartCheckRequired: verifyRestart, restartVerified: verifyRestart, ...result }));
