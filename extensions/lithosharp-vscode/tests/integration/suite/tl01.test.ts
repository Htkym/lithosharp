import * as assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { execFile, spawnSync } from 'node:child_process';
import { promisify } from 'node:util';

if (process.env['LITHOSHARP_TL01_EVIDENCE']) {
  const root = process.env['LITHOSHARP_TL01_EVIDENCE']!;
  const workspace = vscode.Uri.file(process.env['LITHOSHARP_TEST_WORKSPACE']!).fsPath;
  const record: Record<string, unknown> = {};
  async function output(): Promise<string> {
    const parts: string[] = [];
    async function visit(dir: string): Promise<void> {
      for (const entry of await fs.readdir(dir, { withFileTypes: true }).catch(() => [])) {
        const file = path.join(dir, entry.name);
        if (entry.isDirectory()) await visit(file);
        else if (/^\d+-LithoSharp\.log$/.test(entry.name)) parts.push(await fs.readFile(file, 'utf8'));
      }
    }
    await visit(path.join(process.env['LITHOSHARP_TEST_USER_DATA']!, 'logs'));
    return parts.join('\n');
  }
  async function wait(probe: () => boolean | Promise<boolean>): Promise<void> {
    const started = Date.now();
    while (!await probe()) {
      if (Date.now() - started > 60000) throw new Error('Timed out waiting for the real installed consumer.');
      await new Promise(resolve => setTimeout(resolve, 200));
    }
  }
  describe('installed TL-01 consumer', () => {
    it('preserves original UTF16 Markdown positions and runs explicit preflight without rendering', async function () {
      this.timeout(180000);
      const ext = vscode.extensions.getExtension('htkym.lithosharp'); assert.ok(ext);
      assert.equal(await fs.realpath(ext.extensionPath), await fs.realpath(process.env['LITHOSHARP_INSTALLED_EXTENSION']!));
      await ext.activate();
      const beforeSelection = (await output()).length;
      await vscode.workspace.getConfiguration('lithosharp').update('cliPath', process.env['LITHOSHARP_TL01_CLI'], vscode.ConfigurationTarget.Global);
      await vscode.workspace.getConfiguration('lithosharp').update('projectPath', path.join(workspace, 'site.csproj'), vscode.ConfigurationTarget.Global);
      await wait(async () => (await output()).slice(beforeSelection).includes(`Project: ${path.join(workspace, 'site.csproj')}`));
      const file = path.join(workspace, 'position.md');
      const text = '---\r\ntitle: Position\r\n---\r\n\r\n😀 [^note]\r\n';
      await fs.writeFile(file, text);
      const doc = await vscode.workspace.openTextDocument(file); await vscode.window.showTextDocument(doc);
      await wait(() => vscode.languages.getDiagnostics(doc.uri).some(d => String(d.code) === 'LIT001'));
      const diagnostic = vscode.languages.getDiagnostics(doc.uri).find(d => String(d.code) === 'LIT001')!;
      assert.equal(diagnostic.range.start.line, 4); assert.equal(diagnostic.range.start.character, 3);
      record['markdown'] = { text, version: doc.version, id: String(diagnostic.code), severity: diagnostic.severity,
        range: [diagnostic.range.start.line, diagnostic.range.start.character, diagnostic.range.end.line, diagnostic.range.end.character] };
      await wait(async () => (await output()).includes('Language server bundled Core:'));
      await vscode.commands.executeCommand('lithosharp.preflight', 'static');
      await wait(async () => (await output()).includes('Preflight static:'));
      const first = await output(); assert.match(first, /"id": "LIT001"/); assert.match(first, /"line": 5/); assert.match(first, /"column": 4/);
      await assert.rejects(fs.access(path.join(workspace, 'calls.txt')));
      await vscode.commands.executeCommand('lithosharp.preflight', 'trusted');
      await wait(async () => (await output()).includes('Preflight trusted:'));
      const calls = (await fs.readFile(path.join(workspace, 'calls.txt'), 'utf8')).trim().split(/\r?\n/);
      assert.deepEqual(calls, ['ctor', 'create', 'route']);
      await assert.rejects(fs.access(path.join(workspace, 'dist')));
      record['candidatePreflight'] = { calls, renderCalled: false, published: false, originalLocationPreserved: true };
      await fs.writeFile(path.join(root, 'tl01-consumer-result.json'), JSON.stringify({ status: 'PARTIAL', ...record }, null, 2));
    });
    it('keeps old Core Inspect while withholding unadvertised preflight', async function () {
      this.timeout(180000);
      const old = vscode.Uri.file(process.env['LITHOSHARP_TL01_LEGACY_PROJECT']!).fsPath;
      const beforeSelection = (await output()).length;
      await vscode.workspace.getConfiguration('lithosharp').update('cliPath', process.env['LITHOSHARP_TL01_LEGACY_CLI'], vscode.ConfigurationTarget.Global);
      await vscode.workspace.getConfiguration('lithosharp').update('projectPath', old, vscode.ConfigurationTarget.Global);
      await wait(async () => (await output()).slice(beforeSelection).includes(`Project: ${old}`));
      await vscode.commands.executeCommand('lithosharp.preflight', 'trusted');
      await wait(async () => (await output()).includes('selected CLI/Core does not advertise compatible preflight capabilities'));
      await assert.rejects(fs.access(path.join(path.dirname(old), 'calls.txt')));
      await vscode.commands.executeCommand('lithosharp.inspectSite');
      await wait(async () => (await output()).includes('LithoSharp inspect succeeded'));
      const calls = (await fs.readFile(path.join(path.dirname(old), 'calls.txt'), 'utf8')).trim().split(/\r?\n/);
      assert.deepEqual(calls, ['ctor', 'create']);
      record['legacyCore'] = { unadvertisedPreflightExecuted: false, existingInspectSucceeded: true, calls };
      await vscode.workspace.getConfiguration('lithosharp').update('cliPath', process.env['LITHOSHARP_TL01_CLI'], vscode.ConfigurationTarget.Global);
      await vscode.workspace.getConfiguration('lithosharp').update('projectPath', path.join(workspace, 'site.csproj'), vscode.ConfigurationTarget.Global);
      await fs.writeFile(path.join(root, 'tl01-consumer-result.json'), JSON.stringify({ status: 'PASS', ...record }, null, 2));
    });
    it('uses the configured Node for real MDX preflight when PATH has no Node', async function () {
      this.timeout(90000);
      const configuredNode = vscode.workspace.getConfiguration('lithosharp').get<string>('nodeExecutable')!;
      const savedPath = Object.keys(process.env).filter(key => key.toLowerCase() === 'path').map(key => [key, process.env[key]!] as const);
      try {
        // Keep only the SDK and OS tools; other inherited PATH directories may contain another Node shim.
        const noNodePath = [process.env['DOTNET_ROOT']!, path.join(process.env['SystemRoot']!, 'System32')].join(path.delimiter);
        for (const [key] of savedPath) process.env[key] = noNodePath;
        const defaultNode = spawnSync('node', ['--version'], { cwd: workspace, windowsHide: true });
        assert.equal((defaultNode.error as NodeJS.ErrnoException | undefined)?.code, 'ENOENT', 'Default Node must actually be unavailable.');
        const file = path.join(workspace, 'explicit-node.mdx'); await fs.writeFile(file, '# Configured Node\n\nValid MDX.\n');
        const doc = await vscode.workspace.openTextDocument(file); await vscode.window.showTextDocument(doc);
        await wait(async () => (await output()).includes(`MDX analysis ready: ${doc.uri.toString()}`));
        const worker = JSON.parse(await fs.readFile(path.join(root, 'mdx-restore-evidence.json'), 'utf8')).worker as string;
        let unconfigured: Record<string, unknown> | undefined;
        try {
          await promisify(execFile)(process.env['LITHOSHARP_TL01_CLI']!, ['preflight', '--mode', 'static', '--input', file, '--worker-directory', worker, '--format', 'json'], { cwd: workspace, windowsHide: true });
          assert.fail('MDX preflight without the explicit Node must fail in this PATH.');
        } catch (error) {
          assert.ok(error && typeof error === 'object' && 'stdout' in error);
          unconfigured = JSON.parse(String(error.stdout)) as Record<string, unknown>;
          assert.equal(unconfigured['success'], false);
        }
        const before = (await output()).length;
        await vscode.commands.executeCommand('lithosharp.preflight', 'static');
        await wait(async () => (await output()).slice(before).includes('Preflight static: no Error'));
        const current = (await output()).slice(before); assert.match(current, /"success": true/);
        assert.match(current, /"mode": "static-inputs"/);
        const beforeTrusted = (await output()).length;
        await vscode.commands.executeCommand('lithosharp.preflight', 'trusted');
        await wait(async () => (await output()).slice(beforeTrusted).includes('Preflight trusted: no Error'));
        const calls = (await fs.readFile(path.join(workspace, 'calls.txt'), 'utf8')).trim().split(/\r?\n/);
        assert.deepEqual(calls, ['ctor', 'create', 'route', 'ctor', 'create', 'route']);
        await assert.rejects(fs.access(path.join(workspace, 'dist')));
        record['explicitNode'] = { configuredNode, defaultNodeUnavailable: true, liveMdxReady: true, extensionStaticPreflightSucceeded: true,
          extensionTrustedPreflightSucceeded: true, calls, renderCalled: false, unconfiguredCliResponse: unconfigured };
        await fs.writeFile(path.join(root, 'tl01-consumer-result.json'), JSON.stringify({ status: 'PASS', ...record }, null, 2));
      } finally { for (const [key, value] of savedPath) process.env[key] = value; }
    });
  });
}
