import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import * as vscode from 'vscode';
async function waitFor(probe: () => boolean | Promise<boolean>, message: string): Promise<void> {
  console.log('[installed-mdx] wait: ' + message);
  const started = Date.now();
  while (!(await probe())) {
    if (Date.now() - started > 60000) throw new Error('Timed out: ' + message);
    await new Promise(resolve => setTimeout(resolve, 250));
  }
}
async function output(): Promise<string> {
  const parts: string[] = [];
  async function visit(dir: string): Promise<void> {
    let entries: fs.Dirent[];
    try { entries = await fs.promises.readdir(dir, { withFileTypes: true }); }
    catch (error) { if ((error as NodeJS.ErrnoException).code === 'ENOENT') return; throw error; }
    for (const entry of entries) {
      const file = path.join(dir, entry.name);
      if (entry.isDirectory()) await visit(file);
      else if (/^\d+-LithoSharp\.log$/.test(entry.name)) parts.push(await fs.promises.readFile(file, 'utf8'));
    }
  }
  await visit(path.join(process.env['LITHOSHARP_TEST_USER_DATA']!, 'logs'));
  return parts.join('\n');
}
const readyCount = (value: string): number => value.match(/^Language server: ready\.\r?$/gm)?.length ?? 0;
const hash = (data: Buffer): string => createHash('sha256').update(data).digest('hex');
if (process.env['LITHOSHARP_INSTALLED_EXTENSION']) {
  describe('installed MDX restore and reopen', () => {
  it('restores real bundled worker and reopens already-open MDX across restore and restart', async function () {
    this.timeout(180000);
    const ext = vscode.extensions.getExtension('undefined_publisher.lithosharp');
    assert.ok(ext);
    const actual = fs.realpathSync(ext.extensionPath);
    assert.equal(actual, fs.realpathSync(process.env['LITHOSHARP_INSTALLED_EXTENSION']!));
    const relative = path.relative(fs.realpathSync(process.env['LITHOSHARP_INSTALLED_EXTENSIONS_DIR']!), actual);
    assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
    const devRelative = path.relative(fs.realpathSync(process.env['LITHOSHARP_TEST_DEVELOPMENT_PATH']!), actual);
    assert.ok(devRelative.startsWith('..') || path.isAbsolute(devRelative));
    await ext.activate();
    await vscode.workspace.getConfiguration('lithosharp').update('languageServerPath', '', vscode.ConfigurationTarget.Global);
    await vscode.workspace.getConfiguration('lithosharp').update('workerDirectory', '', vscode.ConfigurationTarget.Global);
    const docPath = path.join(process.env['LITHOSHARP_TEST_WORKSPACE']!, 'content', 'restore-open.mdx');
    await fs.promises.writeFile(docPath, '# MDX before restore\n\n<Unclosed>\n');
    const doc = await vscode.workspace.openTextDocument(docPath);
    assert.equal(doc.languageId, 'mdx', 'Inert helper language declaration did not identify MDX.');
    await vscode.window.showTextDocument(doc);
    await waitFor(async () => (await output()).includes('MDX analysis unavailable'), 'unrestored worker explicitly degrades MDX');
    const readyBeforeRestore = readyCount(await output());
    assert.ok(readyBeforeRestore > 0);
    const versionBeforeRestore = doc.version;
    await vscode.commands.executeCommand('lithosharp.restoreWorker');
    await waitFor(async () => readyCount(await output()) > readyBeforeRestore, 'restore completed a new server handshake');
    await waitFor(() => vscode.languages.getDiagnostics(doc.uri).some(item => String(item.code) === 'LSMDX001'),
      'restore reanalyzed unchanged already-open MDX');
    assert.equal(doc.version, versionBeforeRestore, 'Initial restore proof must precede all document edits.');
    const match = (await output()).match(/MDX worker ready: (.+)\. Restarting the language server\./);
    assert.ok(match, 'Actual restore success was not recorded.');
    const worker = fs.realpathSync(match[1]!);
    const storageRelative = path.relative(fs.realpathSync(process.env['LITHOSHARP_TEST_USER_DATA']!), worker);
    assert.ok(storageRelative && !storageRelative.startsWith('..') && !path.isAbsolute(storageRelative));
    assert.ok(fs.statSync(path.join(worker, 'node_modules')).isDirectory());
    for (const name of ['worker.mjs', 'compiler.mjs', 'package.json', 'package-lock.json', 'runtime/components.mjs']) {
      assert.equal(hash(await fs.promises.readFile(path.join(worker, name))),
        hash(await fs.promises.readFile(path.join(actual, 'resources', 'worker', name))), 'Restored source differs: ' + name);
    }
    const lockHash = hash(await fs.promises.readFile(path.join(actual, 'resources', 'worker', 'package-lock.json')));
    assert.equal((await fs.promises.readFile(path.join(worker, '.lithosharp-worker-lock'), 'utf8')).trim(), lockHash);
    async function replace(text: string): Promise<void> {
      const editor = vscode.window.activeTextEditor;
      assert.ok(editor && editor.document.uri.toString() === doc.uri.toString());
      assert.equal(await editor.edit(builder => builder.replace(new vscode.Range(doc.positionAt(0), doc.positionAt(doc.getText().length)), text)), true);
    }
    async function currentHeading(name: string): Promise<boolean> {
      const symbols = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', doc.uri);
      return Boolean(symbols?.some(symbol => symbol.name === name) && !symbols.some(symbol => symbol.name === 'MDX before restore'));
    }
    await replace('# MDX restored heading\n\nValid MDX.\n');
    await waitFor(() => !vscode.languages.getDiagnostics(doc.uri).some(item => String(item.code) === 'LSMDX001'), 'valid edit cleared compiler diagnostic');
    await waitFor(() => currentHeading('MDX restored heading'), 'real restored worker supplied current MDX heading');
    const readyBeforeRestart = readyCount(await output());
    await vscode.commands.executeCommand('lithosharp.restartServer');
    await waitFor(async () => readyCount(await output()) > readyBeforeRestart, 'explicit restart completed a new handshake');
    await replace('# MDX restarted heading\n\n<Unclosed>\n');
    await waitFor(() => vscode.languages.getDiagnostics(doc.uri).some(item => String(item.code) === 'LSMDX001'), 'reopened MDX accepted edits after restart');
    await replace('# MDX restarted heading\n\nValid again.\n');
    await waitFor(() => !vscode.languages.getDiagnostics(doc.uri).some(item => String(item.code) === 'LSMDX001'), 'post-restart fix cleared diagnostic');
    await waitFor(() => currentHeading('MDX restarted heading'), 'post-restart worker supplied renamed heading');
    await fs.promises.writeFile(path.join(process.env['LITHOSHARP_TEST_USER_DATA']!, '..', 'mdx-restore-evidence.json'),
      JSON.stringify({ installedAcceptanceCoverage: true, worker, lockHash, versionBeforeRestore,
        readyBeforeRestore, readyBeforeRestart, readyAfterRestart: readyCount(await output()),
        restoredUnchangedOpenDocument: true, restoredSourceHashesMatch: true, currentHeading: 'MDX restarted heading' }, null, 2));
  });
});
}
