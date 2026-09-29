import * as vscode from 'vscode';
import { execFile as execFileCallback } from 'node:child_process';
import * as nodeFs from 'node:fs';
import * as nodePath from 'node:path';
import { promisify } from 'node:util';
import { findProjectCandidates, keyOf, type ProjectCandidate } from './projectDetector.js';
import { requireTrusted } from './trust.js';
import { fetchCliVersion, resolveCli, type CliCommand, type ExecProbe } from './cliResolver.js';
import { quickPickItems, statusText, type ProjectState } from './state.js';
import { ServeController } from './serveController.js';
import { spawnProcess } from './process.js';
import { BuildRunner } from './buildRunner.js';
import { LspClient } from './lspClient.js';
import { toVsDiagnostic } from './diagnostics.js';
import { resolveWorker, restoreWorker, type WorkerDeps, type WorkerFileSystem } from './worker.js';
import { PreviewManager } from './previewManager.js';
import type { InspectedRoute } from './preview.js';

const execFile = promisify(execFileCallback);

/** Node file operations for MDX worker management. Only extension storage or an explicit directory is ever written. */
function workerFileSystem(): WorkerFileSystem {
  const fsPromises = nodeFs.promises;
  const skipped = new Set(['node_modules', '.cache', 'tests', '.git']);
  const copyTree = async (from: string, to: string): Promise<number> => {
    let copied = 0;
    await fsPromises.mkdir(to, { recursive: true });
    for (const entry of await fsPromises.readdir(from, { withFileTypes: true })) {
      if (entry.name === '' || skipped.has(entry.name)) {
        continue;
      }
      const source = nodePath.join(from, entry.name);
      if (entry.isSymbolicLink()) {
        continue;
      }
      const target = nodePath.join(to, entry.name);
      if (entry.isDirectory()) {
        copied += await copyTree(source, target);
      } else if (entry.isFile()) {
        await fsPromises.copyFile(source, target);
        copied += 1;
      }
    }
    return copied;
  };
  return {
    readFile: async (filePath) => {
      try {
        return await fsPromises.readFile(filePath);
      } catch {
        return null;
      }
    },
    isDirectory: async (dirPath) => {
      try {
        return (await fsPromises.stat(dirPath)).isDirectory();
      } catch {
        return false;
      }
    },
    isFile: async (filePath) => {
      try {
        return (await fsPromises.stat(filePath)).isFile();
      } catch {
        return false;
      }
    },
    ensureDir: async (dirPath) => {
      await fsPromises.mkdir(dirPath, { recursive: true });
    },
    copySourceTree: copyTree,
    runNpmCi: async (cwd) => {
      // Lifecycle scripts stay off: restored packages never execute on install.
      try {
        const { stdout } = await execFile('npm', ['ci', '--ignore-scripts', '--no-audit', '--no-fund'], { cwd });
        return { exit: 0, stdout: String(stdout), stderr: '' };
      } catch (error) {
        const stderr = error instanceof Error ? error.message : String(error);
        return { exit: 1, stdout: '', stderr };
      }
    },
  };
}

/** Exported for tests: worker resolution shares the trust boundary with commands. */
export function workerDeps(context: vscode.ExtensionContext): WorkerDeps {
  return {
    isTrusted: vscode.workspace.isTrusted,
    workerPathSetting: vscode.workspace.getConfiguration('lithosharp').get<string>('workerDirectory', ''),
    storageDir: context.globalStorageUri.fsPath,
    bundledDir: nodePath.join(context.extensionPath, 'resources', 'worker'),
    fs: workerFileSystem(),
  };
}

function realProbe(): ExecProbe {
  const calls: ExecProbe['calls'] = [];
  return {
    calls,
    run: async (command, cwd) => {
      calls.push({ command, cwd });
      try {
        const { stdout } = await execFile(command[0]!, command.slice(1), { cwd });
        return { exit: 0, stdout: String(stdout) };
      } catch {
        return { exit: 1, stdout: '' };
      }
    },
  };
}

/** Exported for tests: direct handler calls must honor trust without executing. */
export async function selectProjectHandler(deps: {
  isTrusted: boolean;
  folders: readonly { uri: { fsPath: string }; name: string }[];
  showQuickPick: <T>(items: T[]) => Promise<T | undefined>;
  saveSelection: (key: string, projectPath: string) => Promise<void>;
  cliPathSetting: string;
  envPath: string;
  probe?: ExecProbe;
}): Promise<{ candidate: ProjectCandidate; cli: string; version: string | null } | undefined> {
  requireTrusted(deps.isTrusted, 'select a project');
  const found: ProjectCandidate[] = [];
  for (const folder of deps.folders) {
    found.push(...(await findProjectCandidates(folder.uri.fsPath, folder.name)));
  }
  if (found.length === 0) {
    return undefined;
  }
  let candidate: ProjectCandidate | undefined;
  if (found.length === 1) {
    candidate = found[0]!;
  } else {
    const picked = await deps.showQuickPick(quickPickItems(found));
    if (!picked) {
      return undefined;
    }
    candidate = picked.candidate;
  }
  await deps.saveSelection(keyOf(candidate), candidate.projectPath);
  const resolved = await resolveCli({
    isTrusted: deps.isTrusted,
    explicitPath: deps.cliPathSetting,
    projectDir: candidate.workspaceFolder,
    envPath: deps.envPath,
  });
  if (resolved.kind === 'missing' || !deps.probe) {
    return { candidate, cli: resolved.kind === 'missing' ? resolved.reason : resolved.command.command.join(' '), version: null };
  }
  const version = await fetchCliVersion(resolved, deps.isTrusted, deps.probe);
  return { candidate, cli: resolved.command.command.join(' '), version };
}

export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel('LithoSharp');
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
  status.command = 'lithosharp.selectProject';
  const state: { current: ProjectState } = { current: { kind: 'none', candidates: 0 } };
  const servers = new Map<string, ServeController>();
  let lsp: LspClient | undefined;
  const diagnosticsCollection = vscode.languages.createDiagnosticCollection('lithosharp');
  const previews = new PreviewManager(
    {
      createPanel: (title) => {
        const panel = vscode.window.createWebviewPanel('lithosharpPreview', title, vscode.ViewColumn.Beside, {
          enableScripts: true,
        });
        return {
          setHtml: (html) => {
            panel.webview.html = html;
          },
          postMessage: (message) => {
            void panel.webview.postMessage(message);
          },
          onDidDispose: (callback) => {
            panel.onDidDispose(callback);
          },
          reveal: () => panel.reveal(vscode.ViewColumn.Beside),
        };
      },
      openExternal: (url) => Promise.resolve(vscode.env.openExternal(vscode.Uri.parse(url))),
      showQuickPick: <T,>(items: T[]) =>
        Promise.resolve(vscode.window.showQuickPick(items as never)) as Promise<T | undefined>,
      showMessage: (message) => {
        void vscode.window.showInformationMessage(message);
      },
    },
    (serverKey) => {
      servers.get(serverKey)?.stop();
    },
  );

  const refresh = async (): Promise<void> => {
    const folders = vscode.workspace.workspaceFolders ?? [];
    const trusted = vscode.workspace.isTrusted;
    if (!trusted) {
      state.current = { kind: 'untrusted' };
    } else {
      const found: ProjectCandidate[] = [];
      for (const folder of folders) {
        found.push(...(await findProjectCandidates(folder.uri.fsPath, folder.name)));
      }
      const saved = vscode.workspace.getConfiguration('lithosharp').get<string>('projectPath', '');
      const selected = found.find((candidate) => candidate.projectPath === saved);
      if (selected) {
        const cliPath = vscode.workspace.getConfiguration('lithosharp').get<string>('cliPath', '').trim();
        const resolved = await resolveCli({
          isTrusted: true,
          explicitPath: cliPath,
          projectDir: selected.workspaceFolder,
          envPath: process.env['PATH'] ?? '',
        });
        if (resolved.kind === 'missing') {
          state.current = { kind: 'missing-cli', reason: resolved.reason };
          output.appendLine(`CLI: ${resolved.reason}`);
        } else {
          state.current = {
            kind: 'selected',
            candidate: selected,
            cli: resolved.command.command.join(' '),
            version: null,
          };
          output.appendLine(`Project: ${selected.projectPath} (${keyOf(selected)})`);
          output.appendLine(`CLI: ${resolved.command.command.join(' ')} (version verified on explicit use)`);
        }
      } else {
        state.current = found.length === 0 ? { kind: 'none', candidates: 0 } : { kind: 'ambiguous', candidates: found.length };
      }
    }
    status.text = statusText(state.current);
    status.show();
  };

  const currentSelection = async (): Promise<ProjectCandidate | undefined> => {
    const folders = vscode.workspace.workspaceFolders ?? [];
    const found: ProjectCandidate[] = [];
    for (const folder of folders) {
      found.push(...(await findProjectCandidates(folder.uri.fsPath, folder.name)));
    }
    const saved = vscode.workspace.getConfiguration('lithosharp').get<string>('projectPath', '');
    const selected = found.find((candidate) => candidate.projectPath === saved);
    if (selected) {
      return selected;
    }
    if (found.length === 1) {
      return found[0];
    }
    return undefined;
  };

  const cliFor = async (selected: ProjectCandidate): Promise<CliCommand> => {
    const cliPath = vscode.workspace.getConfiguration('lithosharp').get<string>('cliPath', '').trim();
    const resolved = await resolveCli({
      isTrusted: vscode.workspace.isTrusted,
      explicitPath: cliPath,
      projectDir: selected.workspaceFolder,
      envPath: process.env['PATH'] ?? '',
    });
    if (resolved.kind === 'missing') {
      throw new Error(`LithoSharp: ${resolved.reason}`);
    }
    return resolved.command;
  };

  const realRunProbe = (): import('./buildRunner.js').RunProbe => ({
    run: async (command, cwd) => {
      const { execFile } = await import('node:child_process');
      return await new Promise((resolve) => {
        execFile(command[0]!, command.slice(1), { cwd, maxBuffer: 16 * 1024 * 1024 }, (error, stdout, stderr) => {
          resolve({ exit: error ? 1 : 0, stdout: String(stdout), stderr: String(stderr) });
        });
      });
    },
  });

  const runOneShot = async (command: 'build' | 'check' | 'inspect'): Promise<void> => {
    requireTrusted(vscode.workspace.isTrusted, `run lithosharp ${command}`);
    const selected = await currentSelection();
    if (!selected) {
      output.appendLine('LithoSharp: no project selected. Run LithoSharp: Select Project first.');
      return;
    }
    const cli = await cliFor(selected);
    const runner = new BuildRunner({
      isTrusted: () => vscode.workspace.isTrusted,
      cli: (name) => ({ command: [...cli.command, name, selected.projectPath, '--format', 'json'], cwd: cli.cwd }),
      cwd: selected.workspaceFolder,
      probe: realRunProbe(),
      onLog: (line) => output.appendLine(line),
    });
    const result = await runner.run(command);
    if (result.ok) {
      output.appendLine(`LithoSharp ${command} succeeded (exit ${result.exitCode}). Validation only; not a publish approval.`);
      if (result.diagnosticsText) {
        output.appendLine(result.diagnosticsText);
      }
    } else {
      output.appendLine(`LithoSharp ${command} failed: ${result.error}`);
      if (result.diagnosticsText) {
        output.appendLine(result.diagnosticsText);
      }
    }
  };

  const serverFor = (selected: ProjectCandidate, cli: { command: string[]; cwd: string | undefined }): ServeController => {
    const key = keyOf(selected);
    const existing = servers.get(key);
    if (existing) {
      return existing;
    }
    const created = new ServeController({
      projectId: key,
      cliCommand: [...cli.command, 'serve', selected.projectPath, '-c', 'Release', '--port', '0', '--format', 'json', '--control-stdin'],
      cwd: cli.cwd ?? selected.workspaceFolder,
      spawn: spawnProcess,
      onEvent: (event) => {
        if (event.event === 'startup') {
          output.appendLine(`Server started: ${event.url} (generation ${event.generation})`);
        } else if (event.event === 'shutdown') {
          output.appendLine(`Server stopped (generation ${event.generation}).`);
        } else if (event.event === 'rebuild-failed') {
          output.appendLine(`Rebuild failed: ${event.error ?? 'unknown error'}`);
        }
        previews.onServerEvent(
          { key, url: created.url, generation: created.generation, owned: false, stop: () => created.stop() },
          event.event === 'startup' || event.event === 'shutdown'
            ? { event: event.event, generation: created.generation, url: created.url ?? undefined }
            : event.event === 'rebuild-started'
              ? { event: 'rebuild-started' }
              : { event: event.event, generation: created.generation },
        );
        const current = servers.get(key);
        status.text = `LithoSharp: ${current ? current.getState() : 'Stopped'}${current?.url ? ` ${current.url}` : ''}`;
        status.show();
      },
      onLog: (line) => output.appendLine(line),
    });
    servers.set(key, created);
    context.subscriptions.push({ dispose: () => created.dispose() });
    return created;
  };

  const previewRoutes = async (selected: ProjectCandidate): Promise<InspectedRoute[]> => {
    const cli = await cliFor(selected);
    const runner = new BuildRunner({
      isTrusted: () => vscode.workspace.isTrusted,
      cli: (name) => ({ command: [...cli.command, name, selected.projectPath, '--format', 'json'], cwd: cli.cwd }),
      cwd: selected.workspaceFolder,
      probe: realRunProbe(),
      onLog: (line) => output.appendLine(line),
    });
    const result = await runner.run('inspect');
    if (!result.ok) {
      return [];
    }
    const raw = result.raw as { buildPlan?: { artifacts?: { path?: unknown; publicPath?: unknown }[] }[] };
    const routes: InspectedRoute[] = [];
    for (const node of raw.buildPlan ?? []) {
      for (const artifact of node.artifacts ?? []) {
        if (typeof artifact.path === 'string' && typeof artifact.publicPath === 'string') {
          routes.push({ path: artifact.path, publicPath: artifact.publicPath });
        }
      }
    }
    return routes;
  };

  const openPreviewForActiveEditor = async (): Promise<void> => {
    requireTrusted(vscode.workspace.isTrusted, 'open the preview');
    const editor = vscode.window.activeTextEditor;
    if (!editor || (editor.document.languageId !== 'markdown' && editor.document.languageId !== 'mdx')) {
      output.appendLine('LithoSharp: open a Markdown or MDX document first.');
      return;
    }
    const selected = await currentSelection();
    if (!selected) {
      output.appendLine('LithoSharp: no project selected. Run LithoSharp: Select Project first.');
      return;
    }
    const controller = serverFor(selected, await cliFor(selected));
    const owned = controller.getState() === 'Stopped';
    if (owned) {
      controller.start();
    }
    const routes = await previewRoutes(selected);
    const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
    const relative = folder
      ? vscode.workspace.asRelativePath(editor.document.uri).replace(/\\/g, '/')
      : editor.document.fileName.replace(/\\/g, '/');
    await previews.open(relative, routes, {
      key: keyOf(selected),
      url: controller.url,
      generation: controller.generation,
      owned,
      stop: () => controller.stop(),
    }, editor.document.isDirty);
  };

  const selector: vscode.DocumentSelector = [{ language: 'markdown' }, { language: 'mdx' }];

  const ensureLsp = async (): Promise<LspClient | undefined> => {
    if (lsp) {
      return lsp;
    }
    if (!vscode.workspace.isTrusted) {
      return undefined;
    }
    // Bundled delivery covers the MDX worker source only: the language server
    // itself still needs an explicit path (framework-dependent publish, one
    // RID per install), and the worker needs an explicit restore. Nothing is
    // guessed or auto-installed.
    const explicitServer = vscode.workspace.getConfiguration('lithosharp').get<string>('languageServerPath', '').trim();
    if (explicitServer === '') {
      output.appendLine('Language server: set lithosharp.languageServerPath to enable live diagnostics.');
      return undefined;
    }
    // The worker directory rides along when one is resolvable; without it the
    // server still diagnoses Markdown and degrades MDX with an explanation.
    let workerDirectory = '';
    try {
      const resolved = await resolveWorker(workerDeps(context));
      workerDirectory = resolved.directory;
      if (resolved.source !== 'none' && !resolved.ready) {
        output.appendLine(`MDX worker is not restored yet: run LithoSharp: Restore MDX Worker (${resolved.directory}). Markdown diagnostics keep working.`);
      }
    } catch (error) {
      output.appendLine(`MDX worker resolution failed: ${String(error)}. Markdown diagnostics keep working.`);
    }
    const client = new LspClient(
      {
        serverCommand: [explicitServer],
        cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd(),
        spawn: (command, cwd) => spawnProcess(command, cwd),
        debounceMs: vscode.workspace.getConfiguration('lithosharp').get<number>('diagnosticDebounceMs', 150),
        initializationOptions: workerDirectory === '' ? {} : { workerDirectory },
        isTrusted: () => vscode.workspace.isTrusted,
        onLog: (line) => output.appendLine(line),
        onState: (name) => output.appendLine(`Language server: ${name}.`),
      },
      {
        set: (uri, diagnostics) =>
          diagnosticsCollection.set(
            vscode.Uri.parse(uri),
            diagnostics.map((item) => {
              const adapted = toVsDiagnostic(item);
              const diagnostic = new vscode.Diagnostic(
                new vscode.Range(
                  adapted.range.start.line,
                  adapted.range.start.character,
                  adapted.range.end.line,
                  adapted.range.end.character,
                ),
                adapted.message,
                adapted.severity as vscode.DiagnosticSeverity,
              );
              diagnostic.code = adapted.code;
              diagnostic.source = adapted.source;
              return diagnostic;
            }),
          ),
        delete: (uri) => diagnosticsCollection.delete(vscode.Uri.parse(uri)),
        clear: () => diagnosticsCollection.clear(),
      },
    );
    lsp = client;
    try {
      await client.start();
    } catch (error) {
      lsp = undefined;
      output.appendLine(`Language server failed to start: ${String(error)}`);
      return undefined;
    }
    return client;
  };

  context.subscriptions.push(
    output,
    status,
    diagnosticsCollection,
    vscode.commands.registerCommand('lithosharp.selectProject', async () => {
      const picked = await selectProjectHandler({
        isTrusted: vscode.workspace.isTrusted,
        folders: vscode.workspace.workspaceFolders ?? [],
        showQuickPick: <T,>(items: T[]) => vscode.window.showQuickPick(items as never) as Promise<T | undefined>,
        saveSelection: async (_key, projectPath) => {
          await vscode.workspace.getConfiguration('lithosharp').update('projectPath', projectPath, vscode.ConfigurationTarget.Global);
          await refresh();
        },
        cliPathSetting: vscode.workspace.getConfiguration('lithosharp').get<string>('cliPath', ''),
        envPath: process.env['PATH'] ?? '',
        probe: realProbe(),
      });
      if (picked) {
        output.appendLine(`Selected project: ${picked.candidate.projectPath} via ${picked.cli}${picked.version ? ` ${picked.version}` : ''}`);
      }
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('lithosharp')) {
        void refresh();
      }
    }),
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      void refresh();
    }),
    vscode.workspace.onDidOpenTextDocument((document) => {
      if (!vscode.workspace.isTrusted) {
        return;
      }
      void ensureLsp().then((client) => {
        client?.didOpen({ uri: document.uri.toString(), languageId: document.languageId, version: document.version, text: document.getText() });
      });
    }),
    vscode.workspace.onDidChangeTextDocument((event) => {
      if (!vscode.workspace.isTrusted) {
        return;
      }
      void ensureLsp().then((client) => {
        client?.didChange(event.document.uri.toString(), event.document.version, event.document.getText());
      });
    }),
    vscode.workspace.onDidSaveTextDocument((document) => {
      if (!vscode.workspace.isTrusted) {
        return;
      }
      void ensureLsp().then((client) => {
        client?.didSave(document.uri.toString());
      });
    }),
    vscode.workspace.onDidCloseTextDocument((document) => {
      lsp?.didClose(document.uri.toString());
    }),
    vscode.workspace.onDidDeleteFiles((event) => {
      for (const uri of event.files) {
        lsp?.forget(uri.toString());
      }
    }),
    vscode.workspace.onDidRenameFiles((event) => {
      for (const file of event.files) {
        lsp?.forget(file.oldUri.toString());
      }
    }),
    vscode.languages.registerDocumentSymbolProvider(selector, {
      provideDocumentSymbols: async (document, token) => {
        if (!vscode.workspace.isTrusted) {
          return [];
        }
        const client = await ensureLsp();
        if (!client) {
          return [];
        }
        const symbols = await (async () => {
          let cancel: (() => void) | undefined;
          const pending = client.requestSymbols(document.uri.toString(), (fn) => {
            cancel = fn;
          });
          const off = token.onCancellationRequested(() => cancel?.());
          try {
            return await pending;
          } finally {
            off.dispose();
          }
        })();
        const toVs = (items: typeof symbols): vscode.DocumentSymbol[] =>
          items.map((item) => {
            const symbol = new vscode.DocumentSymbol(
              item.name,
              '',
              vscode.SymbolKind.Namespace,
              new vscode.Range(item.range.start.line, item.range.start.character, item.range.end.line, item.range.end.character),
              new vscode.Range(
                item.selectionRange.start.line,
                item.selectionRange.start.character,
                item.selectionRange.end.line,
                item.selectionRange.end.character,
              ),
            );
            symbol.children = toVs(item.children);
            return symbol;
          });
        return toVs(symbols);
      },
    }),
    vscode.commands.registerCommand('lithosharp.restartServer', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'restart the language server');
      lsp?.stop();
      lsp = undefined;
      await ensureLsp();
    }),
    vscode.commands.registerCommand('lithosharp.restoreWorker', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'restore the MDX worker');
      try {
        const restored = await restoreWorker(workerDeps(context), (line) => output.appendLine(line));
        output.appendLine(`MDX worker ready: ${restored.directory}. Restarting the language server.`);
      } catch (error) {
        output.appendLine(`MDX worker restore failed: ${String(error)}. Markdown diagnostics keep working.`);
        return;
      }
      lsp?.stop();
      lsp = undefined;
      await ensureLsp();
    }),
    vscode.commands.registerCommand('lithosharp.build', () => runOneShot('build')),
    vscode.commands.registerCommand('lithosharp.inspectSite', () => runOneShot('inspect')),
    vscode.commands.registerCommand('lithosharp.startServer', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'start the server');
      const selected = await currentSelection();
      if (!selected) {
        output.appendLine('LithoSharp: no project selected. Run LithoSharp: Select Project first.');
        return;
      }
      const controller = serverFor(selected, await cliFor(selected));
      controller.start();
      status.text = `LithoSharp: ${controller.getState()}`;
      status.show();
    }),
    vscode.commands.registerCommand('lithosharp.stopServer', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'stop the server');
      const selected = await currentSelection();
      if (!selected) {
        output.appendLine('LithoSharp: no project selected.');
        return;
      }
      const controller = servers.get(keyOf(selected));
      if (!controller) {
        output.appendLine('LithoSharp: server is not running for this project.');
        return;
      }
      controller.stop();
    }),
    vscode.commands.registerCommand('lithosharp.openPreview', async () => {
      await openPreviewForActiveEditor();
    }),
    vscode.commands.registerCommand('lithosharp.refreshPreview', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'refresh the preview');
      const editor = vscode.window.activeTextEditor;
      const selected = await currentSelection();
      if (!editor || !selected) {
        return;
      }
      const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
      const relative = folder
        ? vscode.workspace.asRelativePath(editor.document.uri).replace(/\\/g, '/')
        : editor.document.fileName.replace(/\\/g, '/');
      previews.refreshPanel(relative, keyOf(selected));
    }),
    vscode.commands.registerCommand('lithosharp.openInBrowser', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'open the browser');
      const editor = vscode.window.activeTextEditor;
      const selected = await currentSelection();
      if (!editor || !selected) {
        return;
      }
      const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
      const relative = folder
        ? vscode.workspace.asRelativePath(editor.document.uri).replace(/\\/g, '/')
        : editor.document.fileName.replace(/\\/g, '/');
      await previews.openExternal(relative, keyOf(selected));
    }),
  );
  void refresh();
}

export function deactivate(): void {
  // Owned resources (channel, status, listeners, per-project controllers) sit
  // in subscriptions and are disposed by the host: controller disposal sends a
  // structured shutdown and keeps tree recovery armed.
}
