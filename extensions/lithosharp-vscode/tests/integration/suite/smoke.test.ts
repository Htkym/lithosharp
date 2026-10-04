import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { createConnection } from 'node:net';
import * as vscode from 'vscode';

const expectedProject = path.join(process.env['LITHOSHARP_TEST_WORKSPACE'] ?? vscode.workspace.workspaceFolders![0]!.uri.fsPath, 'site.csproj');

async function waitFor(
  probe: () => Promise<boolean> | boolean,
  message: string,
  timeoutMs = 60000,
): Promise<void> {
  console.log('[lithosharp] wait start: ' + message);
  const start = Date.now();
  for (;;) {
    if (await probe()) {
      return;
    }
    if (Date.now() - start > timeoutMs) {
      throw new Error(`Timed out: ${message}`);
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
}


async function ownedOutput(): Promise<string> {
  const root = path.join(process.env['LITHOSHARP_TEST_USER_DATA']!, 'logs');
  const parts: string[] = [];
  const visit = async (dir: string): Promise<void> => {
    let entries: fs.Dirent[];
    try { entries = await fs.promises.readdir(dir, { withFileTypes: true }); }
    catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return; throw error; }
    for (const entry of entries) {
      const target = path.join(dir, entry.name);
      if (entry.isDirectory()) await visit(target);
      else if (/^\d+-LithoSharp\.log$/.test(entry.name)) parts.push(await fs.promises.readFile(target, 'utf8'));
    }
  };
  await visit(root);
  return parts.join('\n');
}

describe('lithosharp smoke', () => {
  it('probe host responsiveness', async () => {
    const ext = vscode.extensions.getExtension('undefined_publisher.lithosharp');
    assert.ok(ext, 'extension is not installed');
    const expected = process.env['LITHOSHARP_INSTALLED_EXTENSION'];
    if (expected) {
      const actual = fs.realpathSync(ext.extensionPath);
      const extensionsDir = fs.realpathSync(process.env['LITHOSHARP_INSTALLED_EXTENSIONS_DIR']!);
      const relative = path.relative(extensionsDir, actual);
      assert.equal(actual, fs.realpathSync(expected), 'Product resolved outside the installed VSIX copy.');
      assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative), 'Product is outside the isolated extensions directory.');
      const developmentRoot = fs.realpathSync(process.env['LITHOSHARP_TEST_DEVELOPMENT_PATH']!);
      const developmentRelative = path.relative(developmentRoot, actual);
      assert.ok(developmentRelative.startsWith('..') || path.isAbsolute(developmentRelative),
        'Product must be outside the only development extension location.');
    }
    await ext!.activate();
    const after = await vscode.commands.getCommands(true);
    assert.ok(after.includes('lithosharp.selectProject'), 'extension did not register (activation failed)');
  });

  it('selects the project into settings', async () => {
    // Open a document first so onLanguage activation is settled before the
    // command runs, independent of onCommand dispatch timing.
    const probe = await vscode.workspace.openTextDocument({ language: 'markdown', content: '# probe\n' });
    await vscode.window.showTextDocument(probe);
    await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
    await vscode.extensions.getExtension('undefined_publisher.lithosharp')?.activate();
    const commands = await vscode.commands.getCommands(true);
    assert.ok(commands.includes('lithosharp.selectProject'), 'extension did not register (activation failed)');
    await vscode.commands.executeCommand('lithosharp.selectProject');
    await waitFor(async () => {
      const selected = vscode.workspace.getConfiguration('lithosharp').get<string>('projectPath', '');
      return selected.endsWith('site.csproj');
    }, 'project selection was not saved');
    assert.ok(vscode.workspace.getConfiguration('lithosharp').get<string>('projectPath', '').endsWith('site.csproj'));
    assert.equal(path.relative(fs.realpathSync(expectedProject),
      fs.realpathSync(vscode.workspace.getConfiguration('lithosharp').get<string>('projectPath', ''))), '');
  });

  it('edits surface diagnostics and converge on each change', async () => {
    const dir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'lithosharp-int-'));
    // Empty setting verifies the actual bundled default, without a server shim
    // or an explicit development DLL overriding the packaged path.
    const ext = vscode.extensions.getExtension('undefined_publisher.lithosharp');
    assert.ok(ext, 'extension is not installed');
    assert.ok(fs.existsSync(path.join(ext.extensionPath, 'resources', 'language-server', 'LithoSharp.LanguageServer.dll')),
      'stage the language server before running Extension Host tests');
    await vscode.workspace.getConfiguration('lithosharp').update('languageServerPath', '', vscode.ConfigurationTarget.Global);
    await vscode.commands.executeCommand('lithosharp.restartServer');

    const docPath = path.join(dir, 'note.md');
    await fs.promises.writeFile(docPath, '---\ntitle: T\n---\nSee [^a] here.\n# Smoke heading\n');
    const document = await vscode.workspace.openTextDocument(docPath);
    await vscode.window.showTextDocument(document);
    const uri = document.uri;
    assert.equal(document.languageId, 'markdown', 'Fixture was not recognized as Markdown.');
    await waitFor(() => {
      const diagnostics = vscode.languages.getDiagnostics(uri);
      return diagnostics.some((item) => String(item.code) === 'LIT001');
    }, 'LIT001 never appeared');
    // Wait for a stable single diagnostic: under load a second publish can
    // briefly overlap the first, so require three consecutive quiet polls.
    let stable = 0;
    await waitFor(() => {
      const count = vscode.languages.getDiagnostics(uri).filter((item) => String(item.code) === 'LIT001').length;
      stable = count === 1 ? stable + 1 : 0;
      return stable >= 3;
    }, 'LIT001 did not settle to a single diagnostic');
    const first = vscode.languages.getDiagnostics(uri).filter((item) => String(item.code) === 'LIT001');
    assert.equal(first.length, 1);
    assert.equal(first[0]!.severity, vscode.DiagnosticSeverity.Warning);
    let symbolResponseLogged = false;
    await waitFor(async () => {
      const symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', uri);
      if (!symbolResponseLogged) {
        console.log('[lithosharp] first symbol names: ' + JSON.stringify(symbols?.map((symbol) => symbol.name)));
        symbolResponseLogged = true;
      }
      return Boolean(symbols?.some((symbol) => symbol.name === 'Smoke heading'));
    }, 'bundled language server did not supply document symbols');


    const verifyRestart = process.env['LITHOSHARP_VERIFY_LSP_RESTART'] !== '0';
    if (verifyRestart) {
      // Diagnostics events are batched, so an empty set during restart may
      // never be observable. Require a fresh completed handshake instead.
      const readyCount = (output: string): number => output.match(/^Language server: ready\.\r?$/gm)?.length ?? 0;
      await waitFor(async () => readyCount(await ownedOutput()) > 0,
        'Initial language server readiness was not recorded');
      const readyBefore = readyCount(await ownedOutput());
      await vscode.commands.executeCommand('lithosharp.restartServer');
      await waitFor(async () => readyCount(await ownedOutput()) > readyBefore,
        'Restart did not complete a new language server handshake');
      await waitFor(() => vscode.languages.getDiagnostics(uri).some((item) => String(item.code) === 'LIT001'),
        'Restart did not reanalyze the already-open Markdown document');
    }

    // Fix the footnote through the editor: the change must re-analyze and
    // drop the stale diagnostic instead of reshowing it.
    const editor = vscode.window.activeTextEditor;
    assert.ok(editor, 'note editor is not active');
    await editor!.edit((builder) => {
      builder.replace(editor!.document.lineAt(3).range, 'See [a](./other.md) here.');
      if (verifyRestart) builder.replace(editor!.document.lineAt(4).range, '# Restarted smoke heading');
    });
    await waitFor(() => {
      const diagnostics = vscode.languages.getDiagnostics(uri);
      return !diagnostics.some((item) => String(item.code) === 'LIT001');
    }, 'stale LIT001 survived the fix');
    if (verifyRestart) {
      await waitFor(async () => {
        const symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', uri);
        return Boolean(symbols?.some((symbol) => symbol.name === 'Restarted smoke heading')
          && !symbols.some((symbol) => symbol.name === 'Smoke heading'));
      }, 'Restarted client did not converge to the renamed heading after the fix');
    }

    // Reintroduce the error: analysis must run again on the latest version.
    const editorAgain = vscode.window.activeTextEditor;
    assert.ok(editorAgain, 'note editor is not active');
    await editorAgain!.edit((builder) => {
      builder.replace(editorAgain!.document.lineAt(3).range, 'See [^a] here.');
    });
    await waitFor(() => {
      const diagnostics = vscode.languages.getDiagnostics(uri);
      return diagnostics.some((item) => String(item.code) === 'LIT001');
    }, 'LIT001 did not return after reintroducing the error');
    if (verifyRestart) {
      await waitFor(async () => {
        const symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', uri);
        return Boolean(symbols?.some((symbol) => symbol.name === 'Restarted smoke heading')
          && !symbols.some((symbol) => symbol.name === 'Smoke heading'));
      }, 'Restarted client symbols did not retain the current heading after the next edit');
    }
  });
  if (process.env['LITHOSHARP_INSTALLED_EXTENSION']) {
    it('builds, serves, previews and stops through the installed extension and real CLI', async function () {
      this.timeout(180000);
      const workspace = process.env['LITHOSHARP_TEST_WORKSPACE']!;
      const expectedHtml = path.join(workspace, 'dist', 'posts', 'hello.html');
      const marker = 'installed-vsix-fixture-marker';
      assert.ok(!fs.existsSync(expectedHtml), 'Fixture output must not exist before Build.');
      await vscode.commands.executeCommand('lithosharp.build');
      assert.ok(fs.existsSync(expectedHtml), 'Real Build did not generate the fixture page.');
      assert.ok((await fs.promises.readFile(expectedHtml, 'utf8')).includes(marker));
      await waitFor(async () => (await ownedOutput()).includes('LithoSharp build succeeded (exit 0)'),
        'Build command did not report real CLI success.');

      let serverUrl: URL | undefined;
      let previewTab: vscode.Tab | undefined;
      const response = async (): Promise<string> => {
        assert.ok(serverUrl);
        const result = await fetch(new URL('/posts/hello.html', serverUrl), { signal: AbortSignal.timeout(2000), redirect: 'error' });
        assert.equal(result.status, 200);
        return result.text();
      };
      try {
        await vscode.commands.executeCommand('lithosharp.startServer');
        await waitFor(async () => {
          const match = (await ownedOutput()).match(/Server started: (http:\/\/[^\s]+)/);
          if (!match) return false;
          serverUrl = new URL(match[1]!);
          assert.ok(['localhost', '127.0.0.1', '[::1]'].includes(serverUrl.hostname),
            'Only the owned loopback server may be contacted.');
          assert.ok(Number(serverUrl.port) > 0);
          return true;
        }, 'real CLI server startup event', 120000);
        await waitFor(async () => {
          try { return (await response()).includes(marker); } catch { return false; }
        }, 'owned server did not serve the built fixture');

        const document = await vscode.workspace.openTextDocument(path.join(workspace, 'content', 'hello.md'));
        await vscode.window.showTextDocument(document);
        await vscode.commands.executeCommand('lithosharp.openPreview');
        await waitFor(() => {
          previewTab = vscode.window.tabGroups.all.flatMap((group) => group.tabs).find((tab) =>
            tab.input instanceof vscode.TabInputWebview && tab.input.viewType.includes('lithosharpPreview'));
          return Boolean(previewTab);
        }, 'installed extension did not create its real preview WebView tab');
        assert.ok(previewTab!.label.includes('hello.md'));
        assert.ok((await response()).includes(marker));
        assert.equal(await vscode.window.tabGroups.close(previewTab!), true);
        previewTab = undefined;
        assert.ok((await response()).includes(marker), 'Closing preview stopped a user-started server.');
        await vscode.commands.executeCommand('lithosharp.stopServer');
        await waitFor(() => new Promise<boolean>((resolve) => {
          const socket = createConnection({ host: serverUrl!.hostname.replace(/^\[|\]$/g, ''), port: Number(serverUrl!.port) });
          const finish = (closed: boolean): void => { socket.destroy(); resolve(closed); };
          socket.once('connect', () => finish(false));
          socket.once('error', (error: NodeJS.ErrnoException) => finish(error.code === 'ECONNREFUSED'));
          socket.setTimeout(1000, () => finish(false));
        }), 'Stop Server left the owned TCP endpoint listening');
        await waitFor(async () => (await ownedOutput()).includes('Server stopped'),
          'Real CLI shutdown event was not observed.');
      } finally {
        try {
          if (previewTab) await vscode.window.tabGroups.close(previewTab);
        } finally {
          await vscode.commands.executeCommand('lithosharp.stopServer');
        }
      }
    });
  }

});
