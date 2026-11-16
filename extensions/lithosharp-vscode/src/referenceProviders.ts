import * as vscode from 'vscode';
import {isAbsolute} from 'node:path';
import {pathToFileURL} from 'node:url';
import {LspClient} from './lspClient.js';
import {localMarkdownUri, textHash, validateReferencePlan, validateReferenceWorkspaceEdit,
  type ReferenceDocument, type ReferencePlan, type ReferenceScope} from './referenceFeatures.js';
import type {EditorRange} from './editorFeatures.js';

const nativeRenameLimitation = 'VS Code RenameProvider does not preserve document versions. Use LithoSharp: Rename Markdown References for a guarded text-only transaction.';
const fileRenameLimitation = 'Automatic file-rename reference edits were skipped: VS Code collects participant edits before capturing versions. Use LithoSharp: Preview Markdown Move before moving a file; review and update references separately.';
const noCancellation: vscode.CancellationToken = {isCancellationRequested: false, onCancellationRequested: () => ({dispose() {}})};
interface Dependencies {
  ensureClient(): Promise<LspClient | undefined>;
  active(): boolean;
  log(line: string): void;
  showOutput(): void;
}
interface Session {
  client: LspClient; scope: ReferenceScope; source: ReferenceDocument;
  documents: ReadonlyMap<string, ReferenceDocument>; current(): boolean;
}
const range = (value: EditorRange): vscode.Range => new vscode.Range(value.start.line, value.start.character, value.end.line, value.end.character);

/** Version-safe edits use applyEdit, never RenameAdapter or a file-operation participant. */
export function registerReferenceProviders(deps: Dependencies): vscode.Disposable[] {
  const selector: vscode.DocumentSelector = [{scheme: 'file', language: 'markdown'}];
  const active = (): boolean => deps.active() && vscode.workspace.isTrusted;
  const report = (error: unknown): void => deps.log(`Reference request failed: ${error instanceof Error ? error.message : String(error)}`);
  async function session(document: vscode.TextDocument, token: vscode.CancellationToken, prompt = true): Promise<Session | undefined> {
    if (!active() || document.isClosed || !localMarkdownUri(document.uri.toString()) || token.isCancellationRequested) return undefined;
    const sourceVersion = document.version;
    let cancelledWait: vscode.Disposable | undefined;
    let client: LspClient | undefined;
    try {
      const cancelled = new Promise<undefined>(resolve => { cancelledWait = token.onCancellationRequested(() => resolve(undefined)); });
      client = await Promise.race([deps.ensureClient(), cancelled]);
    } finally { cancelledWait?.dispose(); }
    if (!client || !active() || document.isClosed || document.version !== sourceVersion || token.isCancellationRequested) return undefined;
    for (const opened of vscode.workspace.textDocuments) {
      if (!opened.isClosed && LspClient.isSupported(opened.uri.toString(), opened.languageId))
        client.didOpen({uri: opened.uri.toString(), languageId: opened.languageId, version: opened.version, text: opened.getText()});
    }
    const guard = client.referenceGuard();
    const scopes = await cancellable(client, token, onCancel => client.requestReferenceScopes(document.uri.toString(), onCancel));
    if (!guard() || !active() || token.isCancellationRequested || document.isClosed || document.version !== sourceVersion) return undefined;
    if (!scopes.length) throw new Error('Acquire a fresh project scope with Build or Inspect before resolving references.');
    const selected = scopes.length === 1 ? scopes[0] : prompt ? (await vscode.window.showQuickPick(scopes.map(scope => ({
      label: `${scope.collection} / ${scope.version} / ${scope.locale}`, description: scope.publicPaths.join(', '), scope,
    })), {placeHolder: 'Choose the acquired collection, version and locale for this source.'}, token))?.scope : undefined;
    if (!selected || !guard() || !active() || token.isCancellationRequested) return undefined;
    const documents = client.referenceDocuments(), source = documents.get(document.uri.toString());
    if (!source) return undefined;
    const snapshots = [...documents.values()].map(value => ({...value, hash: textHash(value.text),
      document: vscode.workspace.textDocuments.find(opened => opened.uri.toString() === value.uri)}));
    const current = (): boolean => active() && guard() && !token.isCancellationRequested && snapshots.every(value =>
      value.document && !value.document.isClosed && vscode.workspace.textDocuments.includes(value.document) &&
      value.document.version === value.version && textHash(value.document.getText()) === value.hash);
    return current() ? {client, scope: {collection: selected.collection, version: selected.version, locale: selected.locale}, source, documents, current} : undefined;
  }
  async function cancellable<T>(client: LspClient, token: vscode.CancellationToken,
    request: (onCancel: (cancel: () => void) => void) => Promise<T>): Promise<T> {
    let cancel: (() => void) | undefined;
    const subscription = token.onCancellationRequested(() => cancel?.());
    try {
      return await request(callback => { cancel = callback; if (token.isCancellationRequested) callback(); });
    } finally { subscription.dispose(); }
  }
  async function locations(document: vscode.TextDocument, position: vscode.Position, token: vscode.CancellationToken,
    includeDeclaration?: boolean): Promise<vscode.Location[] | undefined> {
    try {
      const state = await session(document, token);
      if (!state) return undefined;
      const result = await cancellable(state.client, token, onCancel => includeDeclaration === undefined
        ? state.client.requestDefinition(state.source.uri, position, state.scope, onCancel)
        : state.client.requestReferences(state.source.uri, position, state.scope, includeDeclaration, onCancel));
      if (!state.current()) return undefined;
      deps.log('Reference results cover captured static Markdown. Closed files, C# and dynamic MDX can require separate review.');
      return result.map(item => new vscode.Location(vscode.Uri.parse(item.uri), range(item.range)));
    } catch (error) { report(error); return undefined; }
  }
  function logPreview(raw: unknown, plan: ReferencePlan): void {
    deps.log(`Captured static Markdown preview: ${plan.replacements.length} text edits; remaining review: ${plan.manualScopes.join('; ') || 'none reported'}.`);
    deps.log(JSON.stringify(raw, null, 2));
    deps.showOutput();
  }
  async function rename(apply: boolean): Promise<void> {
    try {
      const editor = vscode.window.activeTextEditor;
      if (!editor) return;
      const state = await session(editor.document, noCancellation);
      if (!state) return;
      const position = {line: editor.selection.active.line, character: editor.selection.active.character};
      const kind = await vscode.window.showQuickPick([
        {label: 'Heading and its static Markdown references', operation: 'heading'},
        {label: 'Explicit static Markdown link target', operation: 'target'},
      ], {placeHolder: 'Choose what to change. C# and dynamic MDX remain manual.'});
      if (!kind || !state.current()) return;
      const newName = await vscode.window.showInputBox({prompt: kind.operation === 'target' ? 'New explicit Markdown link target' : 'New heading text', ignoreFocusOut: true});
      if (newName === undefined || !newName.trim() || !state.current()) return;
      const parameters = {position, newName, kind: kind.operation};
      const raw = await state.client.requestRenamePreview(state.source.uri, parameters, state.scope);
      if (!state.current()) return;
      const plan = validateReferencePlan(raw, state.source, state.documents);
      logPreview(raw, plan);
      if (!apply) return;
      if (!plan.canApply || plan.moves.length || !plan.replacements.length) throw new Error('The preview has no supported automatic text-only transaction. Review the remaining scope in LithoSharp Output.');
      const accepted = await vscode.window.showWarningMessage(
        'Apply only the previewed static Markdown edits?', {modal: true, detail:
          `${plan.replacements.length} edits in ${new Set(plan.replacements.map(edit => edit.uri)).size} open Markdown documents.\nRemaining manual scope:\n${plan.manualScopes.join('\n') || 'None reported.'}\nC# and generated C# are not edited.`},
        'Apply captured Markdown edits');
      if (accepted !== 'Apply captured Markdown edits' || !state.current()) return;
      const result = await state.client.requestRename(state.source.uri, parameters, state.scope);
      if (!state.current()) throw new Error('Documents or project evidence changed. Acquire a fresh preview.');
      const changes = validateReferenceWorkspaceEdit(result, plan, state.documents);
      const edit = new vscode.WorkspaceEdit();
      for (const change of changes) {
        // The modal confirmed the whole annotated plan. Avoid a second preview
        // that would allow selecting only part of this reference transaction.
        const metadata = change.metadata ? {...change.metadata, needsConfirmation: false} : undefined;
        edit.replace(vscode.Uri.parse(change.uri), range(change.range), change.newText, metadata);
      }
      // No await between the final raw-text/version check and applyEdit, whose
      // official 1.139.0 converter snapshots all open document versions.
      if (!state.current()) throw new Error('The confirmed preview is stale. No edits were applied.');
      const applying = vscode.workspace.applyEdit(edit, {isRefactoring: true});
      if (!await applying) throw new Error('VS Code rejected the text-only transaction. No partial result is accepted.');
      deps.log('Applied the confirmed captured Markdown transaction. Review the reported manual scopes separately.');
    } catch (error) { report(error); if (active()) void vscode.window.showWarningMessage(error instanceof Error ? error.message : String(error)); }
  }
  async function previewMove(document: vscode.TextDocument, newUri: string, prompt: boolean,
    token: vscode.CancellationToken = noCancellation, newPublicPath?: string): Promise<void> {
    const state = await session(document, token, prompt);
    if (!state) return;
    const raw = await cancellable(state.client, token, onCancel => state.client.requestRenamePreview(state.source.uri,
      {newUri, ...(newPublicPath ? {newPublicPath} : {})}, state.scope, onCancel));
    if (state.current()) logPreview(raw, validateReferencePlan(raw, state.source, state.documents));
  }
  return [
    vscode.languages.registerDefinitionProvider(selector, {provideDefinition: (document, position, token) => locations(document, position, token)}),
    vscode.languages.registerReferenceProvider(selector, {provideReferences: (document, position, context, token) => locations(document, position, token, context.includeDeclaration)}),
    vscode.languages.registerRenameProvider(selector, {
      prepareRename: async (document, position, token) => {
        const state = await session(document, token);
        if (!state) return undefined;
        const result = await cancellable(state.client, token, onCancel => state.client.requestPrepareRename(state.source.uri, position, state.scope, onCancel));
        return result && state.current() ? {range: range(result.range), placeholder: result.placeholder} : undefined;
      },
      provideRenameEdits: () => { throw new Error(nativeRenameLimitation); },
    }),
    vscode.commands.registerCommand('lithosharp.previewMarkdownRename', () => rename(false)),
    vscode.commands.registerCommand('lithosharp.renameMarkdownReferences', () => rename(true)),
    vscode.commands.registerCommand('lithosharp.previewMarkdownMove', async () => {
      try {
        const editor = vscode.window.activeTextEditor;
        if (!editor || !active()) return;
        const destination = await vscode.window.showInputBox({prompt: 'Destination Markdown file path (preview only; move the file separately)', value: editor.document.uri.fsPath});
        if (!destination) return;
        if (!isAbsolute(destination) || !localMarkdownUri(pathToFileURL(destination).href)) throw new Error('Use an absolute destination Markdown file path.');
        const publicPath = await vscode.window.showInputBox({prompt: 'Optional new public route path; leave empty to keep the acquired route'});
        if (publicPath === undefined) return;
        await previewMove(editor.document, pathToFileURL(destination).href, true, noCancellation, publicPath || undefined);
      } catch (error) { report(error); }
    }),
    vscode.workspace.onWillRenameFiles(event => {
      if (!active() || !event.files.some(file => localMarkdownUri(file.oldUri.toString()))) return;
      event.waitUntil((async () => {
        deps.log(fileRenameLimitation);
        if (event.files.length === 1) {
          const file = event.files[0]!;
          const document = vscode.workspace.textDocuments.find(value => value.uri.toString() === file.oldUri.toString());
          if (document && localMarkdownUri(file.newUri.toString())) {
            try { await previewMove(document, file.newUri.toString(), false, event.token); } catch (error) { report(error); }
          }
        }
        // Resource renames belong to VS Code. This participant returns no edits.
        return undefined;
      })());
    }),
  ];
}
