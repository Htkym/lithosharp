/** Minimal vscode API double for unit tests. */
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
  statusText: string[];
  shown: number;
  lines: string[];
  commands: Map<string, (...args: unknown[]) => Promise<unknown>>;
  window: {
    createOutputChannel(name: string): { appendLine(line: string): void; dispose(): void };
    createStatusBarItem(alignment: unknown, priority: number): {
      command: string | undefined;
      text: string;
      show(): void;
      dispose(): void;
    };
    showQuickPick<T>(items: T[]): Promise<T | undefined>;
  };
  commandsApi: {
    registerCommand(id: string, handler: (...args: unknown[]) => Promise<unknown>): FakeDisposable;
  };
  workspaceApi: {
    isTrusted: boolean;
    workspaceFolders: { uri: { fsPath: string }; name: string }[] | undefined;
    getConfiguration(section: string): {
      get<T>(key: string, def: T): T;
      update(key: string, value: string, target: unknown): Promise<void>;
    };
    onDidChangeConfiguration(listener: (event: { affectsConfiguration(section: string): boolean }) => void): FakeDisposable;
    onDidChangeWorkspaceFolders(listener: () => void): FakeDisposable;
  };
  StatusBarAlignment: { Left: number };
  ConfigurationTarget: { Global: number };
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
    statusText: [],
    shown: 0,
    lines: [],
    commands: new Map(),
    window: {
      createOutputChannel(_name: string) {
        const disposable = trackDisposable();
        return {
          appendLine: (line: string) => {
            fake.lines.push(line);
          },
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
    },
    commandsApi: {
      registerCommand: (id: string, handler: (...args: unknown[]) => Promise<unknown>) => {
        fake.commands.set(id, handler);
        return trackDisposable();
      },
    },
    workspaceApi: {
      isTrusted: true,
      workspaceFolders: [],
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
    },
    StatusBarAlignment: { Left: 1 },
    ConfigurationTarget: { Global: 1 },
  };
  Object.defineProperty(fake.workspaceApi, 'isTrusted', {
    get: () => fake.isTrusted,
  });
  Object.defineProperty(fake.workspaceApi, 'workspaceFolders', {
    get: () => (fake.folders.length === 0 ? undefined : fake.folders),
  });
  return fake;
}

/** Installs the fake as the 'vscode' module for CJS requires. */
export function installFakeVscode(fake: FakeVscode): void {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const Module = require('node:module') as {
    _load(request: string, ...args: unknown[]): unknown;
  };
  const original = Module._load.bind(Module);
  const shim = {
    window: fake.window,
    commands: fake.commandsApi,
    workspace: fake.workspaceApi,
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
