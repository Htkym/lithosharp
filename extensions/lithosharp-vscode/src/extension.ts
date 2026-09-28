import * as vscode from 'vscode';
import { execFile as execFileCallback } from 'node:child_process';
import { promisify } from 'node:util';
import { findProjectCandidates, keyOf, type ProjectCandidate } from './projectDetector.js';
import { requireTrusted } from './trust.js';
import { fetchCliVersion, resolveCli, type CliCommand, type ExecProbe } from './cliResolver.js';
import { quickPickItems, statusText, type ProjectState } from './state.js';
import { ServeController } from './serveController.js';
import { spawnProcess } from './process.js';
import { BuildRunner } from './buildRunner.js';

const execFile = promisify(execFileCallback);

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

  context.subscriptions.push(
    output,
    status,
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
  );
  void refresh();
}

export function deactivate(): void {
  // Owned resources (channel, status, listeners, per-project controllers) sit
  // in subscriptions and are disposed by the host: controller disposal sends a
  // structured shutdown and keeps tree recovery armed.
}
