// Source mode: node tests/integration/run.mjs [workspace-folder]
// Installed mode: --installed-vsix <file> --sha256 <producer hash> --artifact-manifest <file>
//                 --rc-feed <directory> --package-report <file>
// CI downloads the pinned host separately; this runner never downloads or restages product code.
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
const options = new Map();
if (installed) {
  const names = ['--installed-vsix', '--sha256', '--artifact-manifest', '--rc-feed', '--package-report'];
  assert.equal(args.length, names.length * 2, 'Installed mode requires the VSIX, producer SHA/manifest, and exact seven-package RC feed/report.');
  for (let i = 0; i < args.length; i += 2) {
    assert.ok(names.includes(args[i]), 'Unknown installed-mode option.');
    assert.ok(!options.has(args[i]) && args[i + 1], 'Missing or duplicate option.');
    options.set(args[i], args[i + 1]);
  }
} else {
  assert.ok(args.length <= 1 && !args[0]?.startsWith('--'), 'Expected one optional workspace folder.');
}
assert.notEqual(process.env.LITHOSHARP_OLD_VSIX_DIAGNOSTIC, '1', 'Historical diagnostic mode cannot satisfy candidate acceptance.');
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const fileHash = async (file) => hash(await fs.readFile(file));
const escapeXml = (value) => value.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');
const exec = promisify(execFile);
const host = process.env.LITHOSHARP_VSCODE_EXE ?? (process.platform === 'win32'
  ? path.join(root, '.vscode-test', 'vscode-win32-x64-archive-1.139.1', 'Code.exe') : '');
assert.ok(host && (await fs.stat(host)).isFile(), 'Provide an existing LITHOSHARP_VSCODE_EXE; this runner does not download a host.');
const evidenceRoot = process.env.LITHOSHARP_EVIDENCE_DIR ?? os.tmpdir();
await fs.mkdir(evidenceRoot, { recursive: true });
const owned = await fs.mkdtemp(path.join(evidenceRoot, 'lithosharp-host-'));
console.log('Integration evidence: ' + owned);
const extensions = path.join(owned, 'extensions');
const userData = path.join(owned, 'user-data');
const resultPath = path.join(owned, 'test-result.json');
await fs.mkdir(extensions);
await fs.mkdir(path.join(userData, 'User'), { recursive: true });
const settings = { 'telemetry.telemetryLevel': 'off', 'extensions.autoUpdate': false, 'extensions.autoCheckUpdates': false };
await fs.writeFile(path.join(userData, 'User', 'settings.json'), JSON.stringify(settings));
const profile = ['--extensions-dir=' + extensions, '--user-data-dir=' + userData];
const cliWrapper = resolveCliPathFromVSCodeExecutablePath(host);
let codeCommand = cliWrapper;
let codePrefix = [];
if (process.platform === 'win32') {
  // Read the advertised Windows wrapper, then invoke its JavaScript entry without cmd interpolation.
  const wrapper = await fs.readFile(cliWrapper, 'utf8');
  const match = wrapper.match(/"%~dp0\.\.\\Code\.exe"\s+"%~dp0\.\.\\([^"]+cli\.js)"/i);
  assert.ok(match, 'Unsupported cached VS Code CLI wrapper.');
  const cliJs = path.resolve(path.dirname(cliWrapper), '..', match[1]);
  assert.ok((await fs.stat(cliJs)).isFile());
  codeCommand = host;
  codePrefix = [cliJs];
}
const codeOptions = { env: { ...process.env, ELECTRON_RUN_AS_NODE: '1', VSCODE_DEV: '' }, windowsHide: true,
  timeout: 120000, maxBuffer: 4 * 1024 * 1024 };
const code = async (arguments_) => exec(codeCommand, [...codePrefix, ...arguments_], codeOptions);
const help = (await code(['--help'])).stdout;
for (const flag of ['--install-extension', '--extensions-dir', '--user-data-dir', '--new-window', '--disable-extension']) {
  assert.ok(help.includes(flag), 'Cached CLI does not advertise ' + flag);
}
const hostVersion = (await code(['--version'])).stdout.trim().split(/\r?\n/);
assert.equal(hostVersion[0], '1.139.1', 'Candidate acceptance pins the actual host version.');
assert.match(hostVersion[1], /^[a-f0-9]{40}$/i, 'Host commit was not reported.');
let workspace = args[0] ?? path.join(root, 'tests', 'fixtures', 'workspace');
let developmentPath = root;
let testsPath = path.join(root, 'out', 'tests', 'integration', 'index.js');
let expectedExtension = '';
let archiveHash = '';
let payloadHashes = {};
let dependencyTarget = '';
let packageVersion = '';
let coreAssemblyHash = '';
let packageProvenance;
const helperHashes = {};
const isolatedEnv = { DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false' };
if (installed) {
  const archivePath = path.resolve(options.get('--installed-vsix'));
  archiveHash = options.get('--sha256').toLowerCase();
  assert.match(archiveHash, /^[a-f0-9]{64}$/);
  const archiveBytes = await fs.readFile(archivePath);
  assert.equal(hash(archiveBytes), archiveHash, 'VSIX SHA256 mismatch.');
  const manifest = JSON.parse(await fs.readFile(options.get('--artifact-manifest'), 'utf8'));
  assert.equal(manifest.schemaVersion, '1.0');
  assert.equal(manifest.vsix.sha256.toLowerCase(), archiveHash, 'Producer manifest/independent job SHA mismatch.');
  assert.equal(manifest.vsix.file, path.basename(archivePath));
  const commit = (await exec('git', ['rev-parse', 'HEAD'], { cwd: root, windowsHide: true })).stdout.trim();
  assert.equal(manifest.sourceCommit, commit, 'Test source is not the exact producer candidate commit.');
  const require = createRequire(import.meta.url);
  const JSZip = require('jszip');
  const zip = await JSZip.loadAsync(archiveBytes);
  // The producer validated staging once. Consumers hash the SAME archive against its trusted manifest;
  // rebuilding/staging OS-specific product DLLs would change the artifact under test.
  for (const [entryPath, entry] of Object.entries(zip.files)) {
    if (entry.dir || !/^extension\/(out\/src\/|resources\/)/.test(entryPath)) continue;
    const relative = entryPath.slice('extension/'.length);
    assert.ok(!relative.split('/').includes('..') && !path.isAbsolute(relative), 'Unsafe payload path.');
    payloadHashes[relative] = hash(await entry.async('nodebuffer'));
  }
  assert.deepEqual(payloadHashes, manifest.payloadHashes, 'Archive payload differs from producer-validated bytes.');
  assert.ok(payloadHashes['out/src/extension.js'] && payloadHashes['resources/language-server/LithoSharp.LanguageServer.dll']);
  await code(['--install-extension', archivePath, ...profile]);
  for (const entry of await fs.readdir(extensions, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const dir = path.join(extensions, entry.name);
    const package_ = JSON.parse(await fs.readFile(path.join(dir, 'package.json'), 'utf8'));
    if (package_.name === 'lithosharp' && (package_.publisher ?? 'undefined_publisher') === 'undefined_publisher') {
      assert.equal(expectedExtension, '', 'Multiple installed product copies.');
      expectedExtension = await fs.realpath(dir);
    }
  }
  assert.ok(expectedExtension, 'VSIX installation did not create the product extension.');
  for (const [relative, expected] of Object.entries(payloadHashes)) {
    assert.equal(await fileHash(path.join(expectedExtension, relative)), expected, 'Installed/archive mismatch: ' + relative);
  }
  // Only the inert helper is a development extension. Its MDX contribution supplies a language ID,
  // never a diagnostics or symbol provider; all product APIs come from the installed VSIX.
  developmentPath = path.join(owned, 'test-host');
  await fs.mkdir(developmentPath);
  await fs.writeFile(path.join(developmentPath, 'package.json'), JSON.stringify({
    name: 'lithosharp-integration-host', publisher: 'local-test', version: '0.0.0', engines: { vscode: '^1.139.0' },
    main: './main.js', activationEvents: ['*'], contributes: { languages: [{ id: 'mdx', extensions: ['.mdx'] }] },
  }));
  await fs.writeFile(path.join(developmentPath, 'main.js'), 'exports.activate = () => {};\n');
  await fs.mkdir(path.join(developmentPath, 'tests', 'suite'), { recursive: true });
  for (const relative of ['index.js', 'suite/smoke.test.js', 'suite/mdx.test.js']) {
    await fs.copyFile(path.join(root, 'out', 'tests', 'integration', relative), path.join(developmentPath, 'tests', relative));
  }
  dependencyTarget = await fs.realpath(path.join(root, 'node_modules'));
  assert.equal(path.relative(await fs.realpath(root), dependencyTarget), 'node_modules', 'Dependencies must belong to this checkout.');
  await fs.symlink(dependencyTarget, path.join(developmentPath, 'node_modules'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.equal(await fs.realpath(path.join(developmentPath, 'node_modules')), dependencyTarget);
  for (const relative of ['package.json', 'main.js', 'tests/index.js', 'tests/suite/smoke.test.js', 'tests/suite/mdx.test.js']) {
    helperHashes[relative] = await fileHash(path.join(developmentPath, relative));
  }
  testsPath = path.join(developmentPath, 'tests', 'index.js');
  workspace = path.join(owned, 'workspace');
  await fs.mkdir(path.join(workspace, 'content'), { recursive: true });
  const feed = await fs.realpath(path.resolve(options.get('--rc-feed')));
  const report = JSON.parse(await fs.readFile(options.get('--package-report'), 'utf8'));
  packageVersion = report.version;
  assert.match(packageVersion, /^\d+\.\d+\.\d+-rc\.[0-9A-Za-z.-]+$/, 'Only the local validation RC feed is accepted.');
  assert.equal(report.packageCount, 7);
  assert.equal(manifest.packageVersion, packageVersion);
  assert.deepEqual(report.packages, manifest.packages, 'Package receipt differs from immutable producer receipt.');
  const ids = ['LithoSharp', 'LithoSharp.Generators', 'LithoSharp.Images', 'LithoSharp.Tool', 'LithoSharp.Testing', 'LithoSharp.Mdx', 'LithoSharp.ProjectTemplates'];
  assert.deepEqual(report.packages.map(item => item.id).sort(), [...ids].sort());
  const names = report.packages.map(item => {
    assert.equal(item.version, packageVersion);
    assert.equal(item.file, item.id + '.' + packageVersion + '.nupkg');
    assert.match(item.sha256, /^[a-f0-9]{64}$/i);
    return item.file;
  });
  assert.deepEqual((await fs.readdir(feed)).filter(name => name.endsWith('.nupkg')).sort(), [...names].sort(), 'Feed must contain exactly seven candidate nupkgs.');
  const packageHashes = {};
  let toolZip;
  for (const item of report.packages) {
    const file = path.join(feed, item.file);
    assert.equal(await fs.realpath(file), file, 'Candidate package must be a regular file within the feed.');
    const bytes = await fs.readFile(file);
    assert.equal(hash(bytes), item.sha256.toLowerCase(), 'Candidate package SHA mismatch: ' + item.id);
    packageHashes[item.file] = hash(bytes);
    if (item.id === 'LithoSharp') {
      const coreZip = await JSZip.loadAsync(bytes);
      const coreDll = coreZip.file('lib/net10.0/LithoSharp.dll');
      assert.ok(coreDll, 'Core package has no candidate assembly.');
      coreAssemblyHash = hash(await coreDll.async('nodebuffer'));
    }
    if (item.id === 'LithoSharp.Tool') toolZip = await JSZip.loadAsync(bytes);
  }
  isolatedEnv.NUGET_PACKAGES = path.join(owned, 'nuget-packages');
  isolatedEnv.DOTNET_CLI_HOME = path.join(owned, 'dotnet-home');
  await fs.mkdir(isolatedEnv.NUGET_PACKAGES);
  await fs.mkdir(isolatedEnv.DOTNET_CLI_HOME);
  const config = path.join(workspace, 'NuGet.Config');
  await fs.writeFile(config, '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="candidate" value="'
    + escapeXml(feed) + '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>'
    + '<fallbackPackageFolders><clear/></fallbackPackageFolders><packageSourceMapping><clear/><packageSource key="candidate"><package pattern="LithoSharp*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>');
  const toolDir = path.join(owned, 'tools');
  await exec('dotnet', ['tool', 'install', 'LithoSharp.Tool', '--version', packageVersion, '--tool-path', toolDir, '--configfile', config],
    { cwd: workspace, env: { ...process.env, ...isolatedEnv }, windowsHide: true, timeout: 120000, maxBuffer: 8 * 1024 * 1024 });
  const tool = path.join(toolDir, process.platform === 'win32' ? 'lithosharp.exe' : 'lithosharp');
  assert.ok((await fs.stat(tool)).isFile(), 'Candidate tool installation produced no platform shim.');
  async function findDll(dir) {
    const matches = [];
    for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
      const file = path.join(dir, entry.name);
      if (entry.isDirectory()) matches.push(...await findDll(file));
      else if (entry.name === 'LithoSharp.Tool.dll') matches.push(file);
    }
    return matches;
  }
  const toolDlls = await findDll(toolDir);
  assert.equal(toolDlls.length, 1, 'Candidate tool installation must contain one managed entry point.');
  const installedToolDir = path.dirname(toolDlls[0]);
  const toolPayloadHashes = {};
  const prefix = 'tools/net10.0/any/';
  for (const [name, entry] of Object.entries(toolZip.files)) {
    if (entry.dir || !name.startsWith(prefix)) continue;
    const relative = name.slice(prefix.length);
    assert.ok(relative && !relative.split('/').includes('..') && !path.isAbsolute(relative));
    const expected = hash(await entry.async('nodebuffer'));
    assert.equal(await fileHash(path.join(installedToolDir, relative)), expected, 'Installed Tool/package mismatch: ' + relative);
    toolPayloadHashes[relative] = expected;
  }
  assert.ok(toolPayloadHashes['LithoSharp.Tool.dll'] && toolPayloadHashes['LithoSharp.dll']);
  await fs.writeFile(path.join(workspace, 'site.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies></PropertyGroup><ItemGroup><PackageReference Include="LithoSharp" Version="'
    + escapeXml(packageVersion) + '"/></ItemGroup></Project>');
  await fs.writeFile(path.join(workspace, 'SiteFactory.cs'), [
    'using LithoSharp; using LithoSharp.Configuration; using LithoSharp.Content;',
    'public sealed class FixtureFactory : ISiteFactory {',
    'public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default) {',
    'var loadedCore = typeof(SiteSettings).Assembly.Location;',
    'await File.WriteAllTextAsync(Path.Combine(context.ProjectDirectory, "loaded-core.json"), System.Text.Json.JsonSerializer.Serialize(new { path=loadedCore, sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(loadedCore, cancellationToken))).ToLowerInvariant(), factoryPath=typeof(FixtureFactory).Assembly.Location }), cancellationToken);',
    'var posts = await new MarkdownPostReader().ReadAllAsync(Path.Combine(context.ProjectDirectory, "content"));',
    'return new SiteDefinition(new SiteSettings { Title="Installed VSIX fixture", BaseUrl="https://example.com/", Language="en", TimeZone="UTC" }, posts) { OutputDirectory="dist" };',
    '} }',
  ].join('\n'));
  await fs.writeFile(path.join(workspace, 'content', 'hello.md'), '---\ntitle: Installed fixture\ndate: 2024-01-01\n---\n# Installed fixture\n\ninstalled-vsix-fixture-marker\n');
  settings['lithosharp.cliPath'] = tool;
  await fs.writeFile(path.join(userData, 'User', 'settings.json'), JSON.stringify(settings));
  packageProvenance = { version: packageVersion, feed, packageHashes, tool, shimHash: await fileHash(tool),
    toolDll: toolDlls[0], toolPayloadHashes, expectedSiteCoreAssemblyHash: coreAssemblyHash,
    nugetPackages: isolatedEnv.NUGET_PACKAGES, sourceMapping: 'LithoSharp* exclusively from exact local candidate feed' };
  await fs.writeFile(path.join(owned, 'package-provenance.json'), JSON.stringify(packageProvenance, null, 2));
}
await fs.writeFile(path.join(owned, 'identity.json'), JSON.stringify({
  platform: process.platform, arch: process.arch, hostVersion: hostVersion[0], hostCommit: hostVersion[1], hostArchitecture: hostVersion[2],
  archiveHash, expectedExtension, developmentPath, testsPath, dependencyTarget, helperHashes,
  restartCheckRequired: true, payloadHashes, packageProvenance,
}, null, 2));
await runTests({
  vscodeExecutablePath: host, extensionDevelopmentPath: [developmentPath], extensionTestsPath: testsPath,
  extensionTestsEnv: {
    ...isolatedEnv, LITHOSHARP_TEST_RESULT: resultPath, LITHOSHARP_VERIFY_LSP_RESTART: '1',
    LITHOSHARP_TEST_DEVELOPMENT_PATH: developmentPath, LITHOSHARP_INSTALLED_EXTENSION: expectedExtension,
    LITHOSHARP_INSTALLED_EXTENSIONS_DIR: extensions, LITHOSHARP_TEST_USER_DATA: userData,
    LITHOSHARP_TEST_WORKSPACE: path.resolve(workspace), LITHOSHARP_TEST_PACKAGE_VERSION: packageVersion,
    LITHOSHARP_TEST_CORE_DLL_HASH: coreAssemblyHash,
    LITHOSHARP_TEST_TOOL_CORE_DLL_PATH: packageProvenance ? path.join(path.dirname(packageProvenance.toolDll), 'LithoSharp.dll') : '',
    LITHOSHARP_TEST_TOOL_CORE_DLL_HASH: packageProvenance?.toolPayloadHashes['LithoSharp.dll'] ?? '',
  },
  launchArgs: [workspace, '--new-window', ...profile, '--disable-gpu', '--disable-workspace-trust',
    '--disable-extension=vscode.markdown-language-features', ...(process.env.LITHOSHARP_VERBOSE === '1' ? ['--verbose'] : [])],
});
const result = JSON.parse(await fs.readFile(resultPath, 'utf8'));
assert.ok(result.started && result.finished && result.tests === (installed ? 5 : 3), 'Test runner did not execute the complete acceptance suite.');
assert.equal(result.failures, 0);
assert.equal(result.passes, result.tests, 'Pending/skipped cases do not satisfy installed-package validation.');
console.log(JSON.stringify({ mode: installed ? 'installed-vsix-package-only' : 'source', platform: process.platform,
  hostVersion: hostVersion[0], hostCommit: hostVersion[1], archiveHash, packageVersion, restartVerified: true, ...result }));
