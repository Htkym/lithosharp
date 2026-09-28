import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';

const expectedProject = path.resolve(__dirname, '..', '..', 'fixtures', 'workspace', 'site.csproj');

async function waitFor(
  probe: () => Promise<boolean> | boolean,
  message: string,
  timeoutMs = 60000,
): Promise<void> {
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

describe('lithosharp smoke', () => {
  it('probe host responsiveness', async () => {
    const ext = vscode.extensions.getExtension('undefined_publisher.lithosharp');
    assert.ok(ext, 'development extension is not installed');
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
    assert.ok(expectedProject.endsWith(path.join('fixtures', 'workspace', 'site.csproj')));
  });

  it('edits surface diagnostics and converge on each change', async () => {
    const dir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'lithosharp-int-'));
    const server = process.platform === 'win32' ? path.join(dir, 'lsp.cmd') : path.join(dir, 'lsp.sh');
    const dll = process.env['LITHOSHARP_LS_DLL'] ?? '';
    assert.ok(dll !== '', 'LITHOSHARP_LS_DLL must point at the Release language server.');
    await fs.promises.writeFile(
      server,
      process.platform === 'win32' ? `@echo off\r\ndotnet "${dll}" %*\r\n` : `#!/bin/sh\nexec dotnet "${dll}" "$@"\n`,
    );
    if (process.platform !== 'win32') {
      await fs.promises.chmod(server, 0o755);
    }
    await vscode.workspace.getConfiguration('lithosharp').update('languageServerPath', server, vscode.ConfigurationTarget.Global);

    const docPath = path.join(dir, 'note.md');
    await fs.promises.writeFile(docPath, '---\ntitle: T\n---\nSee [^a] here.\n');
    const document = await vscode.workspace.openTextDocument(docPath);
    await vscode.window.showTextDocument(document);
    const uri = document.uri;
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

    // Fix the footnote through the editor: the change must re-analyze and
    // drop the stale diagnostic instead of reshowing it.
    const editor = vscode.window.activeTextEditor;
    assert.ok(editor, 'note editor is not active');
    await editor!.edit((builder) => {
      builder.replace(editor!.document.lineAt(3).range, 'See [a](./other.md) here.');
    });
    await waitFor(() => {
      const diagnostics = vscode.languages.getDiagnostics(uri);
      return !diagnostics.some((item) => String(item.code) === 'LIT001');
    }, 'stale LIT001 survived the fix');

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
  });
});
