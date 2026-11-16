/** Minimal vscode API double for unit tests. */
import { pathToFileURL } from 'node:url';
export interface FakeDisposable {
  disposed: boolean;
  dispose(): void;
}

export function trackDisposable(): FakeDisposable {
  const self = {
    disposed: false,
    dispose() {
      self.disposed = true;
    },
  };
  return self;
}

export interface FakeVscode {
  isTrusted: boolean;
  folders: { uri: { fsPath: string }; name: string }[];
  settings: Record<string, string>;
  updates: { key: string; value: string }[];
  picks: unknown[][];
  pickedIndex: number;
  configListeners: ((event: { affectsConfiguration(section: string): boolean }) => void)[];
  folderListeners: (() => void)[];
  trustListeners: (() => void)[];
  statusText: string[];
  shown: number;
  lines: string[];
  commands: Map<string, (...args: unknown[]) => Promise<unknown>>;
  editorProviders: Map<string, unknown>;
  window: {
    createOutputChannel(name: string): { appendLine(line: string): void; show(preserveFocus?: boolean): void; dispose(): void };
    onDidChangeActiveTextEditor(listener: () => void): FakeDisposable;
    createStatusBarItem(alignment: unknown, priority: number): {
      command: string | undefined;
      text: string;
      show(): void;
      dispose(): void;
    };
    showQuickPick<T>(items: T[]): Promise<T | undefined>;
    showInputBox(options?: unknown): Promise<string | undefined>;
    showWarningMessage(message: string, options?: unknown, ...items: string[]): Promise<string | undefined>;
  };
  commandsApi: {
    registerCommand(id: string, handler: (...args: unknown[]) => Promise<unknown>): FakeDisposable;
    executeCommand(id: string): Promise<unknown>;
  };
  workspaceApi: {
    isTrusted: boolean;
    workspaceFolders: { uri: { fsPath: string }; name: string }[] | undefined;
    textDocuments: { uri: { fsPath: string; toString(): string }; languageId: string; version: number; getText(): string }[];
    getConfiguration(section: string): {
      get<T>(key: string, def: T): T;
      update(key: string, value: string, target: unknown): Promise<void>;
    };
    onDidChangeConfiguration(listener: (event: { affectsConfiguration(section: string): boolean }) => void): FakeDisposable;
    onDidChangeWorkspaceFolders(listener: () => void): FakeDisposable;
    onDidGrantWorkspaceTrust(listener: () => void): FakeDisposable;
  };
  StatusBarAlignment: { Left: number };
  ConfigurationTarget: { Global: number };
  languages: {
    createDiagnosticCollection(name: string): { set(uri: unknown, diagnostics: unknown[]): void; delete(uri: unknown): void; clear(): void; dispose(): void };
    registerDocumentSymbolProvider(selector: unknown, provider: unknown): FakeDisposable;
    registerCompletionItemProvider(selector: unknown, provider: unknown, ...triggers: string[]): FakeDisposable;
    registerHoverProvider(selector: unknown, provider: unknown): FakeDisposable;
    registerDefinitionProvider(selector: unknown, provider: unknown): FakeDisposable;
    registerReferenceProvider(selector: unknown, provider: unknown): FakeDisposable;
    registerRenameProvider(selector: unknown, provider: unknown): FakeDisposable;
  };
  Uri: { parse(value: string): { toString(): string }; file(value: string): { toString(): string } };
  Range: new (startLine: number, startChar: number, endLine: number, endChar: number) => { start: { line: number; character: number }; end: { line: number; character: number } };
  Location: new (uri: unknown, range: unknown) => {uri: unknown; range: unknown};
  WorkspaceEdit: new () => {changes: unknown[]; replace(uri: unknown, range: unknown, text: string, metadata?: unknown): void};
  Diagnostic: new (range: unknown, message: string, severity: number) => { range: unknown; message: string; severity: number };
  DiagnosticSeverity: { Error: number; Warning: number; Information: number; Hint: number };
  MarkdownString: new () => { value: string; appendText(text: string): { value: string } };
  SymbolKind: { Namespace: number };
  DocumentSymbol: new (
    name: string,
    detail: string,
    kind: number,
    range: { start: { line: number; character: number }; end: { line: number; character: number } },
    selectionRange: { start: { line: number; character: number }; end: { line: number; character: number } },
  ) => { name: string; children: { name: string }[] };
}

export function createFakeVscode(): FakeVscode {
  const fake: FakeVscode = {
    isTrusted: true,
    folders: [],
    settings: {},
    updates: [],
    picks: [],
    pickedIndex: 0,
    configListeners: [],
    folderListeners: [],
    trustListeners: [],
    statusText: [],
    shown: 0,
    lines: [],
    commands: new Map(),
    editorProviders: new Map(),
    window: {
      createOutputChannel(_name: string) {
        const disposable = trackDisposable();
        return {
          appendLine: (line: string) => {
            fake.lines.push(line);
          },
          show: () => {},
          dispose: () => disposable.dispose(),
        };
      },
      createStatusBarItem(_alignment: unknown, _priority: number) {
        const disposable = trackDisposable();
        let current = '';
        return {
          command: undefined as string | undefined,
          get text(): string {
            return current;
          },
          set text(value: string) {
            current = value;
            fake.statusText.push(value);
          },
          show: () => {
            fake.shown += 1;
          },
          dispose: () => disposable.dispose(),
        };
      },
      showQuickPick: async <T,>(items: T[]): Promise<T | undefined> => {
        fake.picks.push(items as unknown[]);
        const list = items as T[];
        return list[Math.min(fake.pickedIndex, list.length - 1)];
      },
      onDidChangeActiveTextEditor: () => trackDisposable(),
      showInputBox: async () => undefined,
      showWarningMessage: async () => undefined,
    },
    commandsApi: {
      executeCommand: async (id: string) => fake.commands.get(id)?.(),
      registerCommand: (id: string, handler: (...args: unknown[]) => Promise<unknown>) => {
        fake.commands.set(id, handler);
        return trackDisposable();
      },
    },
    workspaceApi: {
      isTrusted: true,
      workspaceFolders: [],
      textDocuments: [],
      getConfiguration: (_section: string) => ({
        get: <T,>(key: string, def: T): T => (fake.settings[key] !== undefined ? (fake.settings[key] as T) : def),
        update: async (key: string, value: string, _target: unknown): Promise<void> => {
          fake.updates.push({ key, value });
          fake.settings[key] = value;
        },
      }),
      onDidChangeConfiguration: (listener) => {
        fake.configListeners.push(listener);
        return trackDisposable();
      },
      onDidChangeWorkspaceFolders: (listener) => {
        fake.folderListeners.push(listener);
        return trackDisposable();
      },
      onDidGrantWorkspaceTrust: (listener) => {
        fake.trustListeners.push(listener);
        return trackDisposable();
      },
    },
    StatusBarAlignment: { Left: 1 },
    ConfigurationTarget: { Global: 1 },
    languages: {
      createDiagnosticCollection: (_name: string) => ({
        set: (_uri: unknown, _diagnostics: unknown[]) => {},
        delete: (_uri: unknown) => {},
        clear: () => {},
        dispose: () => {},
      }),
      registerDocumentSymbolProvider: (_selector: unknown, _provider: unknown) => trackDisposable(),
      registerCompletionItemProvider: (_selector: unknown, _provider: unknown) => trackDisposable(),
      registerHoverProvider: (_selector: unknown, _provider: unknown) => trackDisposable(),
      registerDefinitionProvider: (_selector, provider) => { fake.editorProviders.set('definition', provider); return trackDisposable(); },
      registerReferenceProvider: (_selector, provider) => { fake.editorProviders.set('references', provider); return trackDisposable(); },
      registerRenameProvider: (_selector, provider) => { fake.editorProviders.set('rename', provider); return trackDisposable(); },
    },
    Uri: {
      file: (value: string) => ({ toString: () => pathToFileURL(value).toString() }),
      parse: (value: string) => ({
        toString: () => value,
      }),
    },
    Range: class {
      start: { line: number; character: number };
      end: { line: number; character: number };
      constructor(startLine: number, startChar: number, endLine: number, endChar: number) {
        this.start = { line: startLine, character: startChar };
        this.end = { line: endLine, character: endChar };
      }
    },
    Location: class {
      constructor(public uri: unknown, public range: unknown) {}
    },
    WorkspaceEdit: class {
      changes: unknown[] = [];
      replace(uri: unknown, range: unknown, text: string, metadata?: unknown) { this.changes.push({uri, range, text, metadata}); }
    },
    Diagnostic: class {
      range: unknown;
      message: string;
      severity: number;
      constructor(range: unknown, message: string, severity: number) {
        this.range = range;
        this.message = message;
        this.severity = severity;
      }
    },
    DiagnosticSeverity: { Error: 1, Warning: 2, Information: 3, Hint: 4 },
    MarkdownString: class {
      value = '';
      appendText(text: string) { this.value += text; return this; }
    },
    SymbolKind: { Namespace: 3 },
    DocumentSymbol: class {
      name: string;
      children: { name: string }[] = [];
      constructor(name: string, _detail: string, _kind: number, _range: unknown, _selection: unknown) {
        this.name = name;
      }
    },
  };
  Object.defineProperty(fake.workspaceApi, 'isTrusted', {
    get: () => fake.isTrusted,
  });
  Object.defineProperty(fake.workspaceApi, 'workspaceFolders', {
    get: () => (fake.folders.length === 0 ? undefined : fake.folders),
  });
  const extended = fake.workspaceApi as Record<string, unknown>;
  extended['onDidOpenTextDocument'] = (_listener: unknown) => trackDisposable();
  extended['onDidChangeTextDocument'] = (_listener: unknown) => trackDisposable();
  extended['onDidSaveTextDocument'] = (_listener: unknown) => trackDisposable();
  extended['onDidCloseTextDocument'] = (_listener: unknown) => trackDisposable();
  extended['onDidDeleteFiles'] = (_listener: unknown) => trackDisposable();
  extended['onDidRenameFiles'] = (_listener: unknown) => trackDisposable();
  extended['onWillRenameFiles'] = (_listener: unknown) => trackDisposable();
  extended['applyEdit'] = async () => true;
  extended['createFileSystemWatcher'] = () => ({ dispose() {}, onDidChange: () => trackDisposable(),
    onDidCreate: () => trackDisposable(), onDidDelete: () => trackDisposable() });
  return fake;
}

/** Installs the fake as the 'vscode' module for CJS requires. */
export function installFakeVscode(fake: FakeVscode): void {
  // Providers also bind vscode at module load, so each fake owns a fresh binding.
  delete require.cache[require.resolve('../../src/referenceProviders.js')];
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const Module = require('node:module') as {
    _load(request: string, ...args: unknown[]): unknown;
  };
  const original = Module._load.bind(Module);
  const extended = fake.workspaceApi as Record<string, unknown>;
  const shim = {
    window: fake.window,
    commands: fake.commandsApi,
    workspace: {
      get isTrusted(): boolean {
        return fake.isTrusted;
      },
      get workspaceFolders(): { uri: { fsPath: string }; name: string }[] | undefined {
        return fake.folders.length === 0 ? undefined : fake.folders;
      },
      textDocuments: fake.workspaceApi.textDocuments,
      getConfiguration: fake.workspaceApi.getConfiguration,
      onDidChangeConfiguration: fake.workspaceApi.onDidChangeConfiguration,
      onDidChangeWorkspaceFolders: fake.workspaceApi.onDidChangeWorkspaceFolders,
      onDidGrantWorkspaceTrust: fake.workspaceApi.onDidGrantWorkspaceTrust,
      onDidOpenTextDocument: extended['onDidOpenTextDocument'],
      onDidChangeTextDocument: extended['onDidChangeTextDocument'],
      onDidSaveTextDocument: extended['onDidSaveTextDocument'],
      onDidCloseTextDocument: extended['onDidCloseTextDocument'],
      onDidDeleteFiles: extended['onDidDeleteFiles'],
      onDidRenameFiles: extended['onDidRenameFiles'],
      onWillRenameFiles: extended['onWillRenameFiles'],
      applyEdit: extended['applyEdit'],
      createFileSystemWatcher: extended['createFileSystemWatcher'],
    },
    languages: fake.languages,
    Uri: fake.Uri,
    Range: fake.Range,
    Location: fake.Location,
    WorkspaceEdit: fake.WorkspaceEdit,
    Diagnostic: fake.Diagnostic,
    DiagnosticSeverity: fake.DiagnosticSeverity,
    MarkdownString: fake.MarkdownString,
    SymbolKind: fake.SymbolKind,
    DocumentSymbol: fake.DocumentSymbol,
    StatusBarAlignment: fake.StatusBarAlignment,
    ConfigurationTarget: fake.ConfigurationTarget,
  };
  Module._load = function (request: string, ...args: unknown[]): unknown {
    if (request === 'vscode') {
      return shim;
    }
    return original(request, ...args);
  };
}
