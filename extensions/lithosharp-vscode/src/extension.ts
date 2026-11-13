import * as vscode from 'vscode';
import { execFile as execFileCallback, spawn as spawnChildProcess } from 'node:child_process';
import * as nodeFs from 'node:fs';
import * as nodePath from 'node:path';
import { StringDecoder } from 'node:string_decoder';
import { promisify } from 'node:util';
import { findProjectCandidates, keyOf, type ProjectCandidate } from './projectDetector.js';
import { requireTrusted } from './trust.js';
import { fetchCliVersion, resolveCli, type CliCommand, type ExecProbe } from './cliResolver.js';
import { quickPickItems, editorPresentation, operationSummary, type OperationStatus, type ProjectState } from './state.js';
import { ServeController } from './serveController.js';
import { spawnProcess } from './process.js';
import { BuildRunner } from './buildRunner.js';
import { LspClient } from './lspClient.js';
import { resolveLanguageServerCommand } from './languageServer.js';
import { toVsDiagnostic } from './diagnostics.js';
import { resolveWorker, restoreWorker, type WorkerDeps, type WorkerFileSystem } from './worker.js';
import { PreviewManager } from './previewManager.js';
import type { InspectedRoute } from './preview.js';
import { preflightModes, serverCapabilityText } from './tooling.js';
import { acquireInputEvidence, acquireProjectEvidence, acquireSemanticEvidence, retainStaticEvidenceForTextChange } from './projectEvidence.js';
import {registerReferenceProviders} from './referenceProviders.js';

const execFile = promisify(execFileCallback);

/** Node file operations for MDX worker management. Only extension storage or an explicit directory is ever written. */
function workerFileSystem(): WorkerFileSystem {
  const fsPromises = nodeFs.promises;
  const skipped = new Set(['node_modules', '.cache', 'tests', '.git', '.lithosharp-worker-lock']);
  const sourceFiles = async (directory: string, prefix = ''): Promise<string[]> => {
    const files: string[] = [];
    for (const entry of await fsPromises.readdir(directory, { withFileTypes: true })) {
      if (skipped.has(entry.name) || entry.isSymbolicLink()) continue;
      const relative = prefix + entry.name;
      if (entry.isDirectory()) files.push(...await sourceFiles(nodePath.join(directory, entry.name), relative + '/'));
      else if (entry.isFile()) files.push(relative);
    }
    return files;
  };
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
    writeFile: async (filePath, contents) => {
      await fsPromises.writeFile(filePath, contents, 'utf8');
    },
    copySourceTree: copyTree,
    sourceFiles,
    runNpmCi: async (cwd) => {
      // Lifecycle scripts stay off: restored packages never execute on install.
      try {
        const command = process.platform === 'win32' ? 'cmd.exe' : 'npm';
        const args = process.platform === 'win32'
          ? ['/d', '/c', 'npm.cmd ci --ignore-scripts --no-audit --no-fund']
          : ['ci', '--ignore-scripts', '--no-audit', '--no-fund'];
        const { stdout } = await execFile(command, args, { cwd, maxBuffer: 16 * 1024 * 1024 });
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
  status.command = 'lithosharp.showStatus';
  const state: { current: ProjectState } = { current: { kind: 'none', candidates: 0 } };
  const servers = new Map<string, ServeController>();
  const buildRunners = new Map<string, BuildRunner>();
  const buildCliCommands = new Map<string, CliCommand>();
  const userStartedServers = new Set<string>();
  const projectContexts = new Map<string, { folders: string[]; snapshot: unknown; inputPaths: string[] }>();
  const projectGenerations = new Map<string, number>();
  let projectContextEpoch = 0;
  const projectAcquisitions = new Map<string, number>();
  const staleContexts = new Set<string>();
  const projectRoots = new Map<string, string>();
  const operations = new Map<string, OperationStatus>();
  let operationSequence = 0;
  let disposed = false;
  let capabilityProbe: AbortController | undefined;
  let lspGeneration = 0;
  let lsp: LspClient | undefined;
  let lspStarting: Promise<LspClient | undefined> | undefined;
  let missingLspBundleLogged = false;
  const diagnosticsCollection = vscode.languages.createDiagnosticCollection('lithosharp');
  const presentation = (): ReturnType<typeof editorPresentation> => {
    const key = state.current.kind === 'selected' ? keyOf(state.current.candidate) : undefined;
    const uri = vscode.window.activeTextEditor?.document.uri.toString();
    return editorPresentation(state.current, uri ? lsp?.getAnalysisStatus(uri) : undefined,
      key && staleContexts.has(key) ? 'needs-refresh' : key && projectContexts.has(key) ? 'acquired' : 'unacquired',
      key ? operations.get(key) : undefined, key ? servers.get(key)?.getState() : undefined);
  };
  const renderStatus = (): void => {
    if (disposed) return;
    const view = presentation();
    status.text = view.text;
    status.tooltip = view.detail;
    status.show();
  };
  const beginOperation = (selected: ProjectCandidate, command: OperationStatus['command'], target = selected.projectPath): OperationStatus => {
    if (state.current.kind !== 'selected' || keyOf(state.current.candidate) !== keyOf(selected))
      state.current = { kind: 'selected', candidate: selected, cli: '', version: null };
    const operation: OperationStatus = { id: ++operationSequence, command, target, startedAt: new Date().toISOString() };
    operations.set(keyOf(selected), operation);
    renderStatus();
    return operation;
  };
  const finishOperation = (selected: ProjectCandidate, operation: OperationStatus, result: import('./buildRunner.js').BuildResult, epoch: number): boolean => {
    if (disposed || epoch !== projectContextEpoch || operations.get(keyOf(selected))?.id !== operation.id) return false;
    operation.finishedAt = new Date().toISOString();
    operation.result = result;
    renderStatus();
    return true;
  };
  const writeDiagnostics = (result: import('./buildRunner.js').BuildResult): void => {
    if (result.diagnosticsText) {
      output.appendLine(result.diagnosticsText);
      return;
    }
    for (const diagnostic of result.diagnostics) {
      const position = diagnostic.file ? diagnostic.file + (diagnostic.line == null ? '' : `:${diagnostic.line}${diagnostic.column == null ? '' : `:${diagnostic.column}`}`) : '';
      output.appendLine(`${diagnostic.id} ${diagnostic.severity}${position ? ` ${position}` : ''}: ${diagnostic.message}`);
    }
  };
  const isProjectInput = (root: string, filePath: string): boolean => {
    const relative = nodePath.relative(root, nodePath.resolve(filePath));
    if (relative === '..' || relative.startsWith('..' + nodePath.sep) || nodePath.isAbsolute(relative)) return false;
    return !relative.split(nodePath.sep).some(part => ['.git', 'bin', 'obj', 'node_modules', '.local', '.lithosharp'].includes(part));
  };
  const invalidateInput = (uri: vscode.Uri, textChange = false): void => {
    for (const [key, root] of projectRoots) {
      if (!isProjectInput(root, uri.fsPath)) continue;
      staleContexts.add(key);
      projectAcquisitions.set(key, (projectAcquisitions.get(key) ?? 0) + 1);
      const context = projectContexts.get(key);
      const retained = context && textChange
        ? retainStaticEvidenceForTextChange(context.snapshot, uri.fsPath, context.inputPaths) : undefined;
      if (retained) {
        if (retained !== context!.snapshot) {
          projectContexts.set(key, { ...context!, snapshot: retained });
          lsp?.sendProjectContext(key, context!.folders, retained);
        }
      } else if (projectContexts.delete(key)) lsp?.sendProjectContext(key, [], null);
    }
    renderStatus();
  };
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
    // Settings/workspace changes withdraw acquired ownership immediately. A
    // build/inspect begun before this refresh cannot restore a stale context.
    projectContextEpoch++;
    const refreshEpoch = projectContextEpoch;
    operations.clear();
    staleContexts.clear();
    projectRoots.clear();
    capabilityProbe?.abort();
    capabilityProbe = undefined;
    for (const key of projectContexts.keys()) lsp?.sendProjectContext(key, [], null);
    // Advance the publication epoch even when no project snapshot was acquired.
    lsp?.sendProjectContext('editor-selection', [], null);
    projectContexts.clear();
    const folders = vscode.workspace.workspaceFolders ?? [];
    const trusted = vscode.workspace.isTrusted;
    if (!trusted) {
      state.current = { kind: 'untrusted' };
    } else {
      const found: ProjectCandidate[] = [];
      for (const folder of folders) {
        found.push(...(await findProjectCandidates(folder.uri.fsPath, folder.name)));
      }
      if (disposed || refreshEpoch !== projectContextEpoch) return;
      for (const candidate of found) projectRoots.set(keyOf(candidate), nodePath.dirname(candidate.projectPath));
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
        if (disposed || refreshEpoch !== projectContextEpoch) return;
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
    renderStatus();
  };

  const currentSelection = async (): Promise<ProjectCandidate | undefined> => {
    const folders = vscode.workspace.workspaceFolders ?? [];
    const found: ProjectCandidate[] = [];
    for (const folder of folders) {
      found.push(...(await findProjectCandidates(folder.uri.fsPath, folder.name)));
    }
    const saved = vscode.workspace.getConfiguration('lithosharp').get<string>('projectPath', '');
    const selected = found.find((candidate) => candidate.projectPath === saved);
    const result = selected ?? (found.length === 1 ? found[0] : undefined);
    if (result) projectRoots.set(keyOf(result), nodePath.dirname(result.projectPath));
    return result;
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

  const buildRunnerFor = (selected: ProjectCandidate, cli: CliCommand): BuildRunner => {
    const key = keyOf(selected);
    buildCliCommands.set(key, cli);
    let runner = buildRunners.get(key);
    if (!runner) {
      runner = new BuildRunner({
        isTrusted: () => vscode.workspace.isTrusted,
        cli: (name, args) => {
          const current = buildCliCommands.get(key) ?? cli;
          return {
            command: [...current.command, name, ...(name === 'preflight' ? args : [selected.projectPath]), '--format', 'json'],
            cwd: current.cwd,
          };
        },
        cwd: selected.workspaceFolder,
        probe: realRunProbe(),
        onLog: (line) => output.appendLine(line),
      });
      buildRunners.set(key, runner);
      context.subscriptions.push({ dispose: () => runner!.dispose() });
    }
    return runner;
  };

  const createProjectContext = (
    selected: ProjectCandidate,
    raw: unknown,
    generation: number,
  ): { folders: string[]; snapshot: unknown; inputPaths: string[] } | undefined => {
    if (typeof raw !== 'object' || raw === null) return undefined;
    const response = raw as Record<string, unknown>;
    const coreVersion = response['projectCoreVersion'];
    const buildPlan = response['buildPlan'];
    if (typeof coreVersion !== 'string' || !/^\d+\.\d+\.\d+/.test(coreVersion) || !Array.isArray(buildPlan)) {
      return undefined;
    }

    const projectId = keyOf(selected);
    const projectRoot = nodePath.dirname(selected.projectPath);
    const evidence = acquireProjectEvidence(buildPlan, projectRoot, projectId, generation);
    if (!evidence) return undefined;
    const semanticEvidence = acquireSemanticEvidence(response['extensions']);
    const routes: Record<string, unknown>[] = [];
    const seen = new Set<string>();
    const schemas = new Set<string>();
    const inputPaths = new Set(evidence.schemas.flatMap(schema => schema.inputPaths));
    const textInput = (inputs: Record<string, unknown>[], key: string): string | undefined => {
      const value = inputs.find((input) => input['key'] === key)?.['value'];
      return typeof value === 'string' ? value : undefined;
    };
    const field = (value: Record<string, unknown>, pascal: string, camel: string): unknown =>
      value[pascal] ?? value[camel];

    for (const item of buildPlan) {
      if (typeof item !== 'object' || item === null) continue;
      const node = item as Record<string, unknown>;
      const inputs = Array.isArray(node['inputs'])
        ? node['inputs'].filter((input): input is Record<string, unknown> => typeof input === 'object' && input !== null)
        : [];
      const contentSource = textInput(inputs, 'content.sourcePath');
      const legacySource = textInput(inputs, 'page.source');
      const source = contentSource ?? legacySource;
      if (!source || !Array.isArray(node['artifacts'])) continue;
      const schema = contentSource
        ? textInput(inputs, 'content.schema') ?? 'custom'
        : 'post';
      schemas.add(schema);
      const contentRoot = contentSource ? textInput(inputs, 'content.inputRoot') : undefined;
      const inputEvidence = acquireInputEvidence(inputs, projectRoot, source, contentRoot);
      if (!inputEvidence) return undefined;
      const sourcePath = nodePath.resolve(projectRoot, contentRoot ?? '.', source).replace(/\\/g, '/');
      inputPaths.add(sourcePath);
      const metadataText = textInput(inputs, 'content.metadata');
      let metadata: Record<string, unknown> = {};
      if (metadataText) {
        try {
          const parsed: unknown = JSON.parse(metadataText);
          if (typeof parsed === 'object' && parsed !== null) metadata = parsed as Record<string, unknown>;
        } catch {
          // Route lookup remains useful when optional metadata is unavailable.
        }
      }
      const document = field(metadata, 'Document', 'document');
      const documentKey = typeof document === 'object' && document !== null
        ? document as Record<string, unknown>
        : {};
      const collectionInput = inputs.find((input) => input['kind'] === 'Collection');
      const collectionValue = field(documentKey, 'Collection', 'collection')
        ?? collectionInput?.['key']
        ?? textInput(inputs, 'content.collection')
        ?? (legacySource ? 'posts' : 'site');
      const versionValue = field(documentKey, 'Version', 'version') ?? 'current';
      const localeValue = field(documentKey, 'Locale', 'locale')
        ?? field(metadata, 'Language', 'language')
        ?? 'default';
      const collection = typeof collectionValue === 'string' ? collectionValue : 'site';
      const version = typeof versionValue === 'string' ? versionValue : 'current';
      const locale = typeof localeValue === 'string' ? localeValue : 'default';
      const publication = contentSource && textInput(inputs, 'content.publication') === 'unlisted'
        ? 'Unlisted'
        : 'Published';

      for (const artifact of node['artifacts']) {
        if (typeof artifact !== 'object' || artifact === null) continue;
        const output = artifact as Record<string, unknown>;
        if (typeof output['publicPath'] !== 'string' || typeof output['path'] !== 'string') continue;
        const unique = `${sourcePath}\0${output['publicPath']}`;
        if (seen.has(unique)) continue;
        seen.add(unique);
        routes.push({
          sourcePath,
          publicPath: output['publicPath'],
          projectId,
          collection,
          version,
          locale,
          publication,
          ...inputEvidence,
        });
      }
    }

    return {
      folders: [vscode.Uri.file(projectRoot).toString()],
      inputPaths: [...inputPaths],
      snapshot: {
        schemaVersion: '1.0',
        projectId,
        projectGeneration: generation,
        coreVersion,
        collection: 'site',
        language: 'markdown',
        // Mixed schemas deliberately degrade to syntax/route context only.
        schema: schemas.size === 1 ? [...schemas][0]! : 'custom',
        version: 'current',
        locale: 'default',
        acquiredAt: new Date().toISOString(),
        ...evidence,
        ...semanticEvidence,
        inputMetadata: routes.map(route => ({ sourcePath: route['sourcePath'], inputProfile: route['inputProfile'], inputKind: route['inputKind'],
          inputRoot: route['inputRoot'], inputPath: route['inputPath'] })),
        routes,
      },
    };
  };

  const beginProjectAcquisition = (selected: ProjectCandidate, epoch: number): number | undefined => {
    if (disposed || epoch !== projectContextEpoch) return undefined;
    const key = keyOf(selected);
    const ticket = (projectAcquisitions.get(key) ?? 0) + 1;
    projectAcquisitions.set(key, ticket);
    return ticket;
  };

  const updateProjectContext = (selected: ProjectCandidate, raw: unknown, epoch: number, ticket: number): void => {
    if (disposed || epoch !== projectContextEpoch || projectAcquisitions.get(keyOf(selected)) !== ticket) return;
    const key = keyOf(selected);
    const root = nodePath.dirname(selected.projectPath);
    if (vscode.workspace.textDocuments.some(document => document.uri.scheme === 'file' && document.isDirty
      && isProjectInput(root, document.uri.fsPath))) {
      staleContexts.add(key);
      projectContexts.delete(key);
      lsp?.sendProjectContext(key, [], null);
      output.appendLine('Project context needs refresh because a project input has unsaved changes. Save it and rerun Build or Inspect; editor diagnostics remain syntax-only.');
      renderStatus();
      return;
    }
    const generation = (projectGenerations.get(key) ?? 0) + 1;
    const context = createProjectContext(selected, raw, generation);
    if (!context) {
      projectContexts.delete(key);
      lsp?.sendProjectContext(key, [], null);
      output.appendLine('Project context was not acquired; editor diagnostics remain syntax-only.');
      renderStatus();
      return;
    }
    projectGenerations.set(key, generation);
    staleContexts.delete(key);
    projectContexts.set(key, context);
    lsp?.sendProjectContext(key, context.folders, context.snapshot);
    renderStatus();
  };

  const realRunProbe = (): import('./buildRunner.js').RunProbe => ({
    run: (command, cwd, _input, signal) => new Promise((resolve, reject) => {
      if (signal?.aborted) {
        resolve({ exit: 130, stdout: '', stderr: 'CLI invocation was cancelled.' });
        return;
      }
      const child = spawnChildProcess(command[0]!, command.slice(1), {
        cwd,
        detached: process.platform !== 'win32',
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      const stdoutDecoder = new StringDecoder('utf8');
      const stderrDecoder = new StringDecoder('utf8');
      const maxBytes = 16 * 1024 * 1024;
      let stdoutBytes = 0;
      let stderrBytes = 0;
      let stdout = '';
      let stderr = '';
      let overflow = false;
      let killTreeTask: Promise<void> | undefined;
      const onAbort = (): void => {
        if (child.pid === undefined || child.exitCode !== null || child.signalCode !== null) return;
        if (process.platform === 'win32') {
          killTreeTask = new Promise<void>((done) => {
            const killer = spawnChildProcess('taskkill', ['/pid', String(child.pid), '/T', '/F'], {
              stdio: 'ignore',
              windowsHide: true,
            });
            killer.once('error', () => {
              child.kill();
              done();
            });
            killer.once('close', () => done());
          });
        } else {
          try {
            process.kill(-child.pid, 'SIGKILL');
          } catch {
            child.kill('SIGKILL');
          }
        }
      };
      child.stdout?.on('data', (chunk: Buffer) => {
        stdoutBytes += chunk.length;
        if (stdoutBytes <= maxBytes) stdout += stdoutDecoder.write(chunk);
        else overflow = true;
      });
      child.stderr?.on('data', (chunk: Buffer) => {
        stderrBytes += chunk.length;
        if (stderrBytes <= maxBytes) stderr += stderrDecoder.write(chunk);
        else overflow = true;
      });
      child.once('error', (error) => {
        if (signal) signal.removeEventListener('abort', onAbort);
        reject(error);
      });
      child.once('close', (code, processSignal) => {
        void (async () => {
          if (signal) signal.removeEventListener('abort', onAbort);
          if (killTreeTask) await killTreeTask;
          stdout += stdoutDecoder.end();
          stderr += stderrDecoder.end();
          resolve(overflow
            ? { exit: 1, stdout: '', stderr: 'CLI output exceeded the 16 MiB capture limit.' }
            : { exit: code ?? 1, stdout, stderr, signal: processSignal });
        })();
      });
      if (signal) {
        signal.addEventListener('abort', onAbort, { once: true });
        if (signal.aborted) onAbort();
      }
    }),
  });

  const runOneShot = async (command: 'build' | 'check' | 'inspect'): Promise<void> => {
    const contextEpoch = projectContextEpoch;
    requireTrusted(vscode.workspace.isTrusted, `run lithosharp ${command}`);
    const selected = await currentSelection();
    if (!selected) {
      output.appendLine('LithoSharp: no project selected. Run LithoSharp: Select Project first.');
      return;
    }
    if (disposed || contextEpoch !== projectContextEpoch) return;
    const operation = beginOperation(selected, command);
    const ticket = command === 'check' ? undefined : beginProjectAcquisition(selected, contextEpoch);
    if (command !== 'check' && ticket === undefined) return;
    let result: import('./buildRunner.js').BuildResult;
    try {
      const cli = await cliFor(selected);
      const runner = buildRunnerFor(selected, cli);
      result = await runner.run(command);
    } catch (error) {
      finishOperation(selected, operation, { ok: false, outcome: 'execution-error', processExitCode: null, exitCode: 1,
        diagnostics: [], diagnosticsText: null, error: String(error) }, contextEpoch);
      try {
        if (ticket !== undefined) updateProjectContext(selected, undefined, contextEpoch, ticket);
      } finally {
        throw error;
      }
    }
    if (!finishOperation(selected, operation, result, contextEpoch)) return;
    output.appendLine(operationSummary(operation));
    if (result.ok) {
      output.appendLine(`LithoSharp ${command} succeeded (exit ${result.exitCode}). Validation only; not a publish approval.`);
      if (ticket !== undefined) {
        updateProjectContext(selected, result.raw, contextEpoch, ticket);
      }
    } else {
      if (ticket !== undefined) updateProjectContext(selected, undefined, contextEpoch, ticket);
      output.appendLine(`LithoSharp ${command} failed: ${result.error}`);
    }
    writeDiagnostics(result);
    if (result.raw) output.appendLine(JSON.stringify(result.raw, null, 2));
    output.appendLine(`Next: ${result.ok ? 'review the reported diagnostics and scope; run Build after further input changes' : 'review the diagnostics and error details, correct the input or CLI configuration, then rerun ' + command}.`);
  };

  const runPreflight = async (requestedMode?: unknown): Promise<void> => {
    requireTrusted(vscode.workspace.isTrusted, 'run preflight');
    const epoch = projectContextEpoch;
    const selected = await currentSelection();
    if (!selected) { output.appendLine('Preflight unavailable: select a project to resolve the CLI.'); return; }
    const cli = await cliFor(selected);
    requireTrusted(vscode.workspace.isTrusted, 'probe preflight capabilities');
    if (disposed || epoch !== projectContextEpoch) return;
    capabilityProbe?.abort();
    const abort = new AbortController();
    capabilityProbe = abort;
    let advertised: Awaited<ReturnType<import('./buildRunner.js').RunProbe['run']>>;
    try {
      advertised = await realRunProbe().run([...cli.command, 'capabilities', '--format', 'json'], cli.cwd ?? selected.workspaceFolder, undefined, abort.signal);
    } catch (error) {
      if (!abort.signal.aborted && !disposed && epoch === projectContextEpoch) {
        output.appendLine(`Preflight capability probe failed: ${String(error)}`);
        output.show(true);
      }
      return;
    } finally {
      if (capabilityProbe === abort) capabilityProbe = undefined;
    }
    if (abort.signal.aborted || disposed || epoch !== projectContextEpoch) return;
    requireTrusted(vscode.workspace.isTrusted, 'run preflight');
    let report: unknown;
    try { report = advertised.exit === 0 ? JSON.parse(advertised.stdout) : null; } catch { report = null; }
    const modes = preflightModes(report);
    if (modes.length === 0) {
      output.appendLine('Preflight unavailable: the selected CLI/Core does not advertise compatible preflight capabilities. Existing Build, Inspect and live Markdown inspection remain unchanged. No project was evaluated by this capability probe.');
      output.show(true);
      return;
    }
    output.appendLine('Preflight capability comes from the selected Tool and its bundled Core; project Core support has not been acquired by the capability probe.');
    const choices = modes.map(mode => ({ mode, label: mode === 'static' ? 'Static inputs' : 'Trusted project catalog',
      description: mode === 'static' ? 'Inspect the saved active Markdown/MDX file without project evaluation.' : 'Compile the selected project and execute its trusted factory/catalog; do not render or publish.' }));
    const chosen = requestedMode === undefined
      ? await vscode.window.showQuickPick(choices, { placeHolder: 'Choose the explicit preflight operation' })
      : choices.find(item => item.mode === requestedMode);
    if (!chosen || disposed || epoch !== projectContextEpoch) return;
    requireTrusted(vscode.workspace.isTrusted, 'run preflight');
    const document = vscode.window.activeTextEditor?.document;
    const supported = document && document.uri.scheme === 'file' && LspClient.isSupported(document.uri.toString(), document.languageId);
    const version = document?.version;
    if (chosen.mode === 'static' && !supported) { output.appendLine('Static preflight requires an active Markdown/MDX file.'); output.show(true); return; }
    if (supported && document.isDirty) { output.appendLine('Save the active document before explicit preflight; live diagnostics continue to use the unsaved buffer.'); output.show(true); return; }
    const args = [...(chosen.mode === 'trusted' ? [selected.projectPath] : []), '--mode', chosen.mode];
    if (supported) args.push('--input', document.uri.fsPath);
    if (supported && /\.mdx$/i.test(document.uri.fsPath)) {
      const worker = await resolveWorker(workerDeps(context));
      if (!worker.ready) { output.appendLine('MDX preflight unavailable: run LithoSharp: Restore MDX Worker first. Markdown diagnostics remain available.'); output.show(true); return; }
      args.push('--worker-directory', worker.directory);
      const nodeExecutable = vscode.workspace.getConfiguration('lithosharp').get<string>('nodeExecutable', '').trim();
      if (nodeExecutable !== '') args.push('--node-executable', nodeExecutable);
    }
    requireTrusted(vscode.workspace.isTrusted, 'run preflight');
    if (disposed || epoch !== projectContextEpoch) return;
    if (supported && (document.isClosed || document.isDirty || document.version !== version)) {
      output.appendLine('Preflight cancelled because the active buffer changed during preparation. Save it and run preflight again.'); return;
    }
    const operation = beginOperation(selected, 'preflight', supported ? `${selected.projectPath}; ${document.uri.fsPath}` : selected.projectPath);
    const result = await buildRunnerFor(selected, cli).run('preflight', args);
    if (disposed || epoch !== projectContextEpoch || !vscode.workspace.isTrusted) return;
    if (!finishOperation(selected, operation, result, epoch)) return;
    output.appendLine(operationSummary(operation));
    output.appendLine(`Preflight ${chosen.mode}: ${result.outcome} (exit ${result.exitCode}). Validation only; no render or publication authorization.`);
    if (result.ok) {
      output.appendLine(JSON.stringify(result.raw, null, 2));
    } else {
      output.appendLine(result.error);
      if (result.raw) output.appendLine(JSON.stringify(result.raw, null, 2));
    }
    writeDiagnostics(result);
    output.appendLine('Next: review the reported stage, coverage and deferred reasons; use Build for the configured site. Preflight does not check final generated HTML.');
    output.show(true);
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
          { key, url: created.url, generation: created.generation, owned: !userStartedServers.has(key), stop: () => created.stop() },
          {
            event: event.event,
            generation: 'generation' in event ? event.generation : created.generation,
            url: event.event === 'startup' ? event.url : undefined,
          },
        );
        renderStatus();
      },
      onState: (next) => {
        renderStatus();
      },
      onLog: (line) => output.appendLine(line),
    });
    servers.set(key, created);
    context.subscriptions.push({ dispose: () => created.dispose() });
    return created;
  };

  const previewRoutes = async (selected: ProjectCandidate): Promise<InspectedRoute[]> => {
    const contextEpoch = projectContextEpoch;
    const ticket = beginProjectAcquisition(selected, contextEpoch);
    if (ticket === undefined) return [];
    const operation = beginOperation(selected, 'inspect');
    let result: import('./buildRunner.js').BuildResult;
    try {
      const cli = await cliFor(selected);
      const runner = buildRunnerFor(selected, cli);
      result = await runner.run('inspect');
    } catch (error) {
      finishOperation(selected, operation, { ok: false, outcome: 'execution-error', processExitCode: null, exitCode: 1,
        diagnostics: [], diagnosticsText: null, error: String(error) }, contextEpoch);
      try {
        updateProjectContext(selected, undefined, contextEpoch, ticket);
      } finally {
        throw error;
      }
    }
    if (!finishOperation(selected, operation, result, contextEpoch)) return [];
    output.appendLine(operationSummary(operation));
    if (!result.ok) {
      updateProjectContext(selected, undefined, contextEpoch, ticket);
      return [];
    }
    updateProjectContext(selected, result.raw, contextEpoch, ticket);
    const raw = result.raw as {
      buildPlan?: {
        inputs?: { key?: unknown; value?: unknown }[];
        artifacts?: { path?: unknown; publicPath?: unknown }[];
      }[];
    };
    const routes: InspectedRoute[] = [];
    for (const node of raw.buildPlan ?? []) {
      const inputs = node.inputs ?? [];
      const contentRoot = inputs.find((input) => input.key === 'content.inputRoot')?.value;
      const contentSource = inputs.find((input) => input.key === 'content.sourcePath')?.value;
      const legacySource = inputs.find((input) => input.key === 'page.source')?.value;
      const source = typeof contentSource === 'string' ? contentSource : legacySource;
      if (typeof source !== 'string') {
        continue;
      }
      const sourcePath = nodePath.resolve(
        nodePath.dirname(selected.projectPath),
        typeof contentSource === 'string' && typeof contentRoot === 'string' ? contentRoot : '.',
        source,
      );
      for (const artifact of node.artifacts ?? []) {
        if (typeof artifact.path === 'string' && typeof artifact.publicPath === 'string') {
          routes.push({ sourcePath, path: artifact.path, publicPath: artifact.publicPath });
        }
      }
    }
    return routes;
  };

  const openPreviewForActiveEditor = async (): Promise<void> => {
    const epoch = projectContextEpoch;
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
    const key = keyOf(selected);
    const cli = await cliFor(selected);
    if (disposed || epoch !== projectContextEpoch) return;
    const controller = serverFor(selected, cli);
    const owned = !controller.isServing() && !userStartedServers.has(key);
    if (!controller.isServing()) {
      controller.start();
    }
    try {
      const routesTask = previewRoutes(selected);
      const [routes] = await Promise.all([routesTask, controller.waitUntilRunning()]);
      const opened = await previews.open(editor.document.uri.fsPath, routes, {
        key,
        url: controller.url,
        generation: controller.generation,
        owned,
        stop: () => controller.stop(),
      }, editor.document.isDirty);
      if (owned && !opened) {
        controller.stop();
      }
    } catch (error) {
      output.appendLine(`LithoSharp preview could not start: ${String(error)}`);
    }
  };

  const selector: vscode.DocumentSelector = [{ language: 'markdown' }, { language: 'mdx' }];

  const startLsp = async (): Promise<LspClient | undefined> => {
    const generation = lspGeneration;
    if (disposed || !vscode.workspace.isTrusted) {
      return undefined;
    }
    const explicitServer = vscode.workspace.getConfiguration('lithosharp').get<string>('languageServerPath', '');
    const serverCommand = resolveLanguageServerCommand(true, explicitServer, context.extensionPath ?? '',
      (file) => { try { return nodeFs.statSync(file).isFile(); } catch { return false; } });
    if (!serverCommand) {
      if (!missingLspBundleLogged) {
        output.appendLine('Language server bundle is missing. Reinstall the extension or set lithosharp.languageServerPath.');
        missingLspBundleLogged = true;
      }
      return undefined;
    }
    missingLspBundleLogged = false;
    // The worker directory rides along when one is resolvable; without it the
    // server still diagnoses Markdown and degrades MDX with an explanation.
    let workerDirectory = '';
    try {
      const resolved = await resolveWorker(workerDeps(context));
      if (disposed || generation !== lspGeneration || !vscode.workspace.isTrusted) return undefined;
      workerDirectory = resolved.directory;
      if (resolved.source !== 'none' && !resolved.ready) {
        output.appendLine(`MDX worker is not restored yet: run LithoSharp: Restore MDX Worker (${resolved.directory}). Markdown diagnostics keep working.`);
      }
    } catch (error) {
      if (!disposed && generation === lspGeneration) output.appendLine(`MDX worker resolution failed: ${String(error)}. Markdown diagnostics keep working.`);
    }
    if (disposed || generation !== lspGeneration || !vscode.workspace.isTrusted) return undefined;
    const nodeExecutable = vscode.workspace.getConfiguration('lithosharp').get<string>('nodeExecutable', '').trim();
    const client: LspClient = new LspClient(
      {
        serverCommand,
        cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd(),
        spawn: (command, cwd) => spawnProcess(command, cwd),
        debounceMs: vscode.workspace.getConfiguration('lithosharp').get<number>('diagnosticDebounceMs', 150),
        initializationOptions: {
          ...(workerDirectory === '' ? {} : { workerDirectory }),
          ...(nodeExecutable === '' ? {} : { nodeExecutable }),
        },
        referenceEditing: true,
        isTrusted: () => vscode.workspace.isTrusted,
        onLog: (line) => { if (!disposed) output.appendLine(line); },
        onCapabilities: (value) => { if (!disposed) output.appendLine(serverCapabilityText(value)); },
        onAnalysisStatus: (uri, state, message, language) => {
          if (!disposed) output.appendLine(`${language === 'mdx' ? 'MDX' : 'Markdown'} analysis ${state}: ${uri}. ${message}`);
        },
        onDocumentStatusChanged: () => renderStatus(),
        onState: (name) => {
          if (!disposed) output.appendLine(`Language server: ${name}.`);
          // Reconnect on the next editing request, never in an exit-triggered loop.
          if (name === 'stopped' && lsp === client) lsp = undefined;
          renderStatus();
        },
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
              diagnostic.code = typeof adapted.code === 'string' ? adapted.code
                : { value: adapted.code.value, target: vscode.Uri.parse(adapted.code.target) };
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
      if (lsp !== client) return undefined;
      if (disposed || generation !== lspGeneration || !vscode.workspace.isTrusted) {
        client.stop();
        if (lsp === client) lsp = undefined;
        return undefined;
      }
      for (const [projectId, projectContext] of projectContexts) {
        client.sendProjectContext(projectId, projectContext.folders, projectContext.snapshot);
      }
      if (projectContexts.size === 0) {
        output.appendLine('Project context not acquired; live diagnostics are syntax-only until an explicit Build or Inspect succeeds.');
      }
      // A fresh client needs all current buffers before a change/save can work.
      for (const document of vscode.workspace.textDocuments ?? []) {
        if (!document.isClosed && LspClient.isSupported(document.uri.toString(), document.languageId)) {
          client.didOpen({ uri: document.uri.toString(), languageId: document.languageId,
            version: document.version, text: document.getText() });
        }
      }
    } catch (error) {
      client.stop();
      if (lsp === client) lsp = undefined;
      if (!disposed && generation === lspGeneration) output.appendLine(`Language server failed to start: ${String(error)}`);
      return undefined;
    }
    return client;
  };

  const ensureLsp = async (): Promise<LspClient | undefined> => {
    if (disposed) return undefined;
    if (lspStarting) return lspStarting;
    if (lsp) return lsp;
    if (!vscode.workspace.isTrusted) return undefined;
    lspStarting = startLsp();
    try {
      return await lspStarting;
    } finally {
      lspStarting = undefined;
    }
  };

  const openExistingDocuments = (): void => {
    if (disposed || !vscode.workspace.isTrusted) return;
    for (const document of vscode.workspace.textDocuments ?? []) {
      if (document.languageId === 'markdown' || document.languageId === 'mdx') {
        void ensureLsp().then((client) => {
          if (disposed || document.isClosed || !vscode.workspace.isTrusted) return;
          client?.didOpen({
            uri: document.uri.toString(),
            languageId: document.languageId,
            version: document.version,
            text: document.getText(),
          });
        });
      }
    }
  };

  const restartLsp = async (): Promise<void> => {
    lspGeneration++;
    // Discovery/initialization may still own the client that must be replaced.
    if (lspStarting) {
      lsp?.stop();
      await lspStarting;
    }
    lsp?.stop();
    lsp = undefined;
    await ensureLsp();
    openExistingDocuments();
  };

  const requestEditorFeature = async <T>(document: vscode.TextDocument, token: vscode.CancellationToken,
    request: (client: LspClient, onCancel: (cancel: () => void) => void) => Promise<T>): Promise<T | undefined> => {
    const version = document.version;
    const current = (): boolean => !disposed && !document.isClosed && vscode.workspace.isTrusted &&
      !token.isCancellationRequested && document.version === version;
    if (!current()) return undefined;
    let waiting: vscode.Disposable | undefined;
    let client: LspClient | undefined;
    try {
      const cancelled = new Promise<undefined>(resolve => { waiting = token.onCancellationRequested(() => resolve(undefined)); });
      client = await Promise.race([ensureLsp(), cancelled]);
    } finally { waiting?.dispose(); }
    if (!client || !current()) return undefined;
    let cancel: (() => void) | undefined;
    const off = token.onCancellationRequested(() => cancel?.());
    try {
      if (!current()) return undefined;
      const result = await request(client, callback => { cancel = callback; if (token.isCancellationRequested) callback(); });
      return current() && lsp === client ? result : undefined;
    } catch (error) {
      if (current()) output.appendLine(`Language server editor request failed: ${String(error)}`);
      return undefined;
    } finally { off.dispose(); }
  };
  const featureRange = (range: import('./editorFeatures.js').EditorRange): vscode.Range =>
    new vscode.Range(range.start.line, range.start.character, range.end.line, range.end.character);
  const inputWatcher = vscode.workspace.createFileSystemWatcher('**/*.{md,mdx,cs,csproj,props,targets,yaml,yml}');

  context.subscriptions.push(
    { dispose: () => { disposed = true; capabilityProbe?.abort(); capabilityProbe = undefined; lsp?.stop(); lsp = undefined; } },
    output,
    status,
    diagnosticsCollection,
    ...registerReferenceProviders({ensureClient: ensureLsp, active: () => !disposed,
      log: line => { if (!disposed) output.appendLine(line); }, showOutput: () => { if (!disposed) output.show(true); }}),
    inputWatcher,
    inputWatcher.onDidChange(uri => invalidateInput(uri, true)),
    inputWatcher.onDidCreate(uri => invalidateInput(uri)),
    inputWatcher.onDidDelete(uri => invalidateInput(uri)),
    vscode.window.onDidChangeActiveTextEditor(() => renderStatus()),
    vscode.commands.registerCommand('lithosharp.showStatus', async () => {
      output.appendLine(presentation().detail);
      output.show(true);
      const actions = [
        { label: 'Inspect project', command: 'lithosharp.inspectSite' },
        { label: 'Build project', command: 'lithosharp.build' },
        { label: 'Preflight', command: 'lithosharp.preflight' },
        { label: 'Restore MDX Worker', command: 'lithosharp.restoreWorker' },
        { label: 'Restart Language Server', command: 'lithosharp.restartServer' },
        { label: 'Select project', command: 'lithosharp.selectProject' },
      ];
      const action = await vscode.window.showQuickPick(actions, { placeHolder: 'Inspection details are in LithoSharp Output. Choose an explicit next action.' });
      if (action) await vscode.commands.executeCommand(action.command);
    }),
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
    vscode.workspace.onDidGrantWorkspaceTrust(() => {
      void refresh();
      openExistingDocuments();
    }),
    vscode.workspace.onDidOpenTextDocument((document) => {
      if (!vscode.workspace.isTrusted) {
        return;
      }
      void ensureLsp().then((client) => {
        if (disposed || document.isClosed || !vscode.workspace.isTrusted) return;
        client?.didOpen({ uri: document.uri.toString(), languageId: document.languageId, version: document.version, text: document.getText() });
      });
    }),
    vscode.workspace.onDidChangeTextDocument((event) => {
      if (!vscode.workspace.isTrusted) {
        return;
      }
      previews.onDocumentDirty(event.document.uri.fsPath, event.document.isDirty);
      invalidateInput(event.document.uri, true);
      void ensureLsp().then((client) => {
        if (disposed || event.document.isClosed || !vscode.workspace.isTrusted) return;
        client?.didChange(event.document.uri.toString(), event.document.version, event.document.getText());
      });
    }),
    vscode.workspace.onDidSaveTextDocument((document) => {
      if (!vscode.workspace.isTrusted) {
        return;
      }
      previews.onDocumentDirty(document.uri.fsPath, false);
      void ensureLsp().then((client) => {
        if (disposed || document.isClosed || !vscode.workspace.isTrusted) return;
        client?.didSave(document.uri.toString());
      });
    }),
    vscode.workspace.onDidCloseTextDocument((document) => {
      lsp?.didClose(document.uri.toString());
    }),
    vscode.workspace.onDidDeleteFiles((event) => {
      for (const uri of event.files) {
        invalidateInput(uri);
        lsp?.forget(uri.toString());
      }
    }),
    vscode.workspace.onDidRenameFiles((event) => {
      for (const file of event.files) {
        invalidateInput(file.oldUri);
        invalidateInput(file.newUri);
        lsp?.forget(file.oldUri.toString());
      }
    }),
    vscode.languages.registerCompletionItemProvider(selector, {
      provideCompletionItems: async (document, position, token) => {
        const items = await requestEditorFeature(document, token,
          (client, onCancel) => client.requestCompletions(document.uri.toString(), position, onCancel));
        return (items ?? []).map(item => {
          const completion: vscode.CompletionItem = { label: item.label };
          if (item.kind !== undefined) completion.kind = (item.kind - 1) as vscode.CompletionItemKind;
          if (item.detail !== undefined) completion.detail = item.detail;
          if (item.documentation !== undefined) completion.documentation = new vscode.MarkdownString().appendText(item.documentation);
          if (item.textEdit) {
            completion.range = featureRange(item.textEdit.range);
            completion.insertText = item.textEdit.newText;
          }
          return completion;
        });
      },
    }, ':', ' ', '"', '=', '[', '(', '#', '/'),
    vscode.languages.registerHoverProvider(selector, {
      provideHover: async (document, position, token) => {
        const hover = await requestEditorFeature(document, token,
          (client, onCancel) => client.requestHover(document.uri.toString(), position, onCancel));
        if (!hover) return undefined;
        const result: vscode.Hover = { contents: [new vscode.MarkdownString().appendText(hover.text)] };
        if (hover.range) result.range = featureRange(hover.range);
        return result;
      },
    }),
    vscode.languages.registerDocumentSymbolProvider(selector, {
      provideDocumentSymbols: async (document, token) => {
        if (disposed || document.isClosed || !vscode.workspace.isTrusted || token.isCancellationRequested) {
          return [];
        }
        let cancelledWait: vscode.Disposable | undefined;
        const cancelled = new Promise<undefined>((resolve) => {
          cancelledWait = token.onCancellationRequested(() => resolve(undefined));
        });
        let client: LspClient | undefined;
        try {
          if (token.isCancellationRequested) return [];
          client = await Promise.race([ensureLsp(), cancelled]);
        } finally {
          cancelledWait?.dispose();
        }
        if (!client || disposed || document.isClosed || token.isCancellationRequested || !vscode.workspace.isTrusted) {
          return [];
        }
        let symbols: import('./symbols.js').SymbolData[];
        try {
          symbols = await (async () => {
            let cancel: (() => void) | undefined;
            const off = token.onCancellationRequested(() => cancel?.());
            try {
              if (token.isCancellationRequested) return [];
              return await client.requestSymbols(document.uri.toString(), (fn) => {
                cancel = fn;
                if (token.isCancellationRequested) fn();
              });
            } finally {
              off.dispose();
            }
          })();
        } catch (error) {
          if (!disposed && !token.isCancellationRequested) {
            output.appendLine(`Language server symbols failed: ${String(error)}`);
          }
          return [];
        }
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
        return token.isCancellationRequested || disposed ? [] : toVs(symbols);
      },
    }),
    vscode.commands.registerCommand('lithosharp.restartServer', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'restart the language server');
      await restartLsp();
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
      await restartLsp();
    }),
    vscode.commands.registerCommand('lithosharp.build', () => runOneShot('build')),
    vscode.commands.registerCommand('lithosharp.inspectSite', () => runOneShot('inspect')),
    vscode.commands.registerCommand('lithosharp.preflight', runPreflight),
    vscode.commands.registerCommand('lithosharp.startServer', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'start the server');
      const selected = await currentSelection();
      if (!selected) {
        output.appendLine('LithoSharp: no project selected. Run LithoSharp: Select Project first.');
        return;
      }
      const key = keyOf(selected);
      userStartedServers.add(key);
      previews.markServerUserOwned(key);
      const controller = serverFor(selected, await cliFor(selected));
      controller.start();
      renderStatus();
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
      userStartedServers.delete(keyOf(selected));
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
      if (!folder) return;
      previews.refreshPanel(editor.document.uri.fsPath, keyOf(selected));
    }),
    vscode.commands.registerCommand('lithosharp.openInBrowser', async () => {
      requireTrusted(vscode.workspace.isTrusted, 'open the browser');
      const editor = vscode.window.activeTextEditor;
      const selected = await currentSelection();
      if (!editor || !selected) {
        return;
      }
      const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
      if (!folder) return;
      await previews.openExternal(editor.document.uri.fsPath, keyOf(selected));
    }),
  );
  void refresh();
  openExistingDocuments();
}

export function deactivate(): void {
  // Owned resources (channel, status, listeners, per-project controllers) sit
  // in subscriptions and are disposed by the host: controller disposal sends a
  // structured shutdown and keeps tree recovery armed.
}
