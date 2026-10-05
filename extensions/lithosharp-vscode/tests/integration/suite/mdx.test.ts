import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import * as vscode from 'vscode';
type ErrorCounts = { lineCount: number; categoryCounts: { category: string; count: number }[]; reasonCounts: { reason: string; count: number }[] };
const observed: { doc?: vscode.TextDocument; readyBeforeRestore: number; errorBaseline?: { counts: ErrorCounts; characters: number; sha256: string } } = { readyBeforeRestore: 0 };
function summarizeErrors(log: string): ErrorCounts {
  const categoryPatterns: [string, RegExp][] = [
    ['analysis-unavailable', /MDX analysis unavailable/],
    ['unpositioned-diagnostic', /MDX diagnostic .*no reliable/],
    ['worker-resolution-failed', /MDX worker resolution failed/],
    ['worker-restore-failed', /MDX worker restore failed/],
    ['server-start-failed', /Language server failed to start/],
  ];
  const errors = log.split(/\r?\n/).filter(line => categoryPatterns.some(([, pattern]) => pattern.test(line)));
  const reasonPatterns: [string, RegExp][] = [
    ['node-start-failed', /Node worker could not be started/],
    ['locked-toolchain-mismatch', /supported locked versions/],
    ['worker-response-stream-closed', /closed its response stream/],
    ['worker-timeout', /worker timed out/],
    ['module-not-found', /ERR_MODULE_NOT_FOUND/],
    ['native-module-load-failed', /ERR_DLOPEN_FAILED/],
    ['access-denied', /\bEACCES\b/],
    ['missing-file', /\bENOENT\b/],
  ];
  return { lineCount: errors.length,
    categoryCounts: categoryPatterns.map(([category, pattern]) => ({ category, count: errors.filter(line => pattern.test(line)).length })),
    reasonCounts: reasonPatterns.map(([reason, pattern]) => ({ reason, count: errors.filter(line => pattern.test(line)).length })) };
}
function captureErrorBaseline(log: string): void {
  observed.errorBaseline = { counts: summarizeErrors(log), characters: log.length, sha256: createHash('sha256').update(log).digest('hex') };
}
async function captureFailure(stage: string): Promise<void> {
  const doc = observed.doc;
  const workspace = process.env['LITHOSHARP_TEST_WORKSPACE']!;
  const knownStages: Record<string, string> = {
    'unrestored worker explicitly degrades MDX': 'initial-worker-degradation',
    'restore completed a new server handshake': 'restore-handshake',
    'restore reanalyzed unchanged already-open MDX': 'restore-unchanged-document',
    'valid edit cleared compiler diagnostic': 'valid-edit-diagnostic-clear',
    'real restored worker supplied current MDX heading': 'restored-heading',
    'explicit restart completed a new handshake': 'explicit-restart-handshake',
    'reopened MDX accepted edits after restart': 'restarted-edit-diagnostic',
    'post-restart fix cleared diagnostic': 'restarted-diagnostic-clear',
    'post-restart worker supplied renamed heading': 'restarted-heading',
  };
  function identity(uri: vscode.Uri): { relativePath: string; canonicalRelativePath: string | null; exactDocumentUri: boolean; canonicalDocumentPath: boolean } {
    const safeRelative = (file: string): string => {
      const relative = path.relative(workspace, file).replace(/\\/g, '/');
      if (relative.startsWith('..') || path.isAbsolute(relative)) return '<outside-workspace>';
      return relative.toLowerCase() === 'content/restore-open.mdx' ? relative : '<other-workspace-file>';
    };
    let canonical: string | null = null;
    let canonicalDoc: string | null = null;
    if (uri.scheme === 'file') {
      try { canonical = fs.realpathSync.native(uri.fsPath); } catch { /* Missing path is explicit below. */ }
      if (doc) try { canonicalDoc = fs.realpathSync.native(doc.uri.fsPath); } catch { /* No alias claim. */ }
    }
    return { relativePath: uri.scheme === 'file' ? safeRelative(uri.fsPath) : '<non-file-uri>',
      canonicalRelativePath: canonical === null ? null : safeRelative(canonical),
      exactDocumentUri: doc !== undefined && uri.toString() === doc.uri.toString(),
      canonicalDocumentPath: canonical !== null && canonicalDoc !== null && canonical === canonicalDoc };
  }
  const log = await output();
  const currentErrors = summarizeErrors(log);
  const baseline = observed.errorBaseline;
  const continuous = baseline !== undefined && log.length >= baseline.characters
    && createHash('sha256').update(log.slice(0, baseline.characters)).digest('hex') === baseline.sha256;
  const monotonic = baseline !== undefined && currentErrors.lineCount >= baseline.counts.lineCount
    && currentErrors.categoryCounts.every((item, index) => item.count >= baseline.counts.categoryCounts[index]!.count)
    && currentErrors.reasonCounts.every((item, index) => item.count >= baseline.counts.reasonCounts[index]!.count);
  const deltaVerified = continuous && monotonic;
  const delta = deltaVerified && baseline ? {
    lineCount: currentErrors.lineCount - baseline.counts.lineCount,
    categoryCounts: currentErrors.categoryCounts.map((item, index) => ({ category: item.category, count: item.count - baseline.counts.categoryCounts[index]!.count })),
    reasonCounts: currentErrors.reasonCounts.map((item, index) => ({ reason: item.reason, count: item.count - baseline.counts.reasonCounts[index]!.count })),
  } : null;
  const groups = vscode.languages.getDiagnostics();
  const fileGroups = groups.filter(([uri]) => uri.scheme === 'file');
  const safeCode = (code: vscode.Diagnostic['code']): string => {
    const value = typeof code === 'object' && code !== null ? code.value : code;
    return typeof value === 'string' && /^(?:LSMDX00[1-4]|LIT\d{3})$/.test(value) ? value : '<other-code>';
  };
  const diagnostics = fileGroups.slice(0, 32).map(([uri, items]) => ({ ...identity(uri), count: items.length,
    items: items.slice(0, 8).map(item => ({ code: safeCode(item.code), severity: item.severity,
      range: [item.range.start.line, item.range.start.character, item.range.end.line, item.range.end.character] })) }));
  const snapshot = { diagnosticOnly: true, stage: knownStages[stage] ?? '<other-stage>',
    document: doc ? { ...identity(doc.uri), version: doc.version, language: doc.languageId === 'mdx' ? 'mdx' : '<other-language>', isClosed: doc.isClosed } : null,
    readyBeforeRestore: observed.readyBeforeRestore, readyNow: readyCount(log),
    diagnosticGroupCount: groups.length, fileDiagnosticGroupCount: fileGroups.length, retainedDiagnosticGroups: diagnostics.length,
    diagnostics, errorLineCount: currentErrors.lineCount,
    errorCategoryCounts: currentErrors.categoryCounts, errorReasonCounts: currentErrors.reasonCounts,
    preRestoreErrorCounts: baseline?.counts ?? null,
    preRestoreLogProof: baseline ? { characters: baseline.characters, sha256: baseline.sha256 } : null,
    postRestoreErrorDelta: { status: baseline === undefined ? 'unknown-no-baseline'
      : deltaVerified ? 'verified-monotonic-continuous-prefix' : 'unknown-log-rotation-or-nonmonotonic', counts: delta } };
  const text = JSON.stringify(snapshot, null, 2);
  await fs.promises.writeFile(path.join(process.env['LITHOSHARP_TEST_EVIDENCE_DIR']!, 'mdx-failure-evidence.json'), text, { flag: 'wx' });
  console.error('[installed-mdx] failure evidence: ' + JSON.stringify(snapshot));
}
async function waitFor(probe: () => boolean | Promise<boolean>, message: string): Promise<void> {
  console.log('[installed-mdx] wait: ' + message);
  const started = Date.now();
  while (!(await probe())) {
    if (Date.now() - started > 60000) {
      try { await captureFailure(message); } catch { console.error('[installed-mdx] failure evidence capture failed; preserving original timeout.'); }
      throw new Error('Timed out: ' + message);
    }
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
    observed.doc = doc;
    assert.equal(doc.languageId, 'mdx', 'Inert helper language declaration did not identify MDX.');
    await vscode.window.showTextDocument(doc);
    await waitFor(async () => (await output()).includes('MDX analysis unavailable'), 'unrestored worker explicitly degrades MDX');
    const readyBeforeRestore = readyCount(await output());
    observed.readyBeforeRestore = readyBeforeRestore;
    assert.ok(readyBeforeRestore > 0);
    const versionBeforeRestore = doc.version;
    captureErrorBaseline(await output());
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
    await fs.promises.writeFile(path.join(process.env['LITHOSHARP_TEST_EVIDENCE_DIR']!, 'mdx-restore-evidence.json'),
      JSON.stringify({ installedAcceptanceCoverage: true, worker, lockHash, versionBeforeRestore,
        readyBeforeRestore, readyBeforeRestart, readyAfterRestart: readyCount(await output()),
        restoredUnchangedOpenDocument: true, restoredSourceHashesMatch: true, currentHeading: 'MDX restarted heading' }, null, 2));
  });
});
}
