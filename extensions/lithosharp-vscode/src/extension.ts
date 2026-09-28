import * as vscode from 'vscode';
import { execFile as execFileCallback } from 'node:child_process';
import { promisify } from 'node:util';
import { findProjectCandidates, keyOf, type ProjectCandidate } from './projectDetector.js';
import { requireTrusted } from './trust.js';
import { fetchCliVersion, resolveCli, type ExecProbe } from './cliResolver.js';
import { quickPickItems, statusText, type ProjectState } from './state.js';

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
  );
  void refresh();
}

export function deactivate(): void {
  // Owned resources (channel, status, listeners) sit in subscriptions and are
  // disposed by the host. No processes exist in the shell scope to stop.
}
