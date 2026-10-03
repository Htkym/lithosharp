import * as childProcess from 'node:child_process';
import type { SpawnedProcess } from './serveController.js';

/**
 * Production process spawner. The child owns its process group so timeout
 * recovery can reclaim the whole tree without touching anything else.
 * Windows script launchers (.cmd/.bat) run through cmd.exe; PowerShell
 * scripts need their own cmd wrapper.
 */
export function spawnProcess(command: string[], cwd: string): SpawnedProcess {
  let executable = command[0]!;
  let args = command.slice(1);
  let windowsVerbatimArguments = false;
  if (process.platform === 'win32' && /\.(cmd|bat)$/i.test(executable)) {
    // cmd.exe interprets metacharacters in the joined command string even when
    // Node received an argument array. These script launchers are used only for
    // no-argument server shims; CLI commands must resolve to a native .exe.
    if (args.length !== 0 || /[&|<>^%!()"]/.test(executable)) {
      throw new Error('Windows .cmd/.bat launchers cannot safely receive arguments; configure a native executable instead.');
    }
    args = ['/d', '/s', '/c', `call "${executable}"`];
    executable = 'cmd.exe';
    windowsVerbatimArguments = true;
  }
  const child = childProcess.spawn(executable, args, {
    cwd,
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
    windowsVerbatimArguments,
    detached: process.platform !== 'win32',
  });
  return {
    get pid() {
      return child.pid;
    },
    stdinWrite: (line) => {
      child.stdin!.write(line);
    },
    closeStdin: () => {
      child.stdin!.end();
    },
    onStdout: (fn) => {
      child.stdout!.on('data', fn);
    },
    onStderr: (fn) => {
      child.stderr!.on('data', fn);
    },
    onExit: (fn) => {
      child.on('exit', (code) => fn(code));
    },
    killTree: () => {
      if (child.pid === undefined || child.exitCode !== null || child.signalCode !== null) {
        return;
      }
      if (process.platform === 'win32') {
        childProcess.spawn('taskkill', ['/pid', String(child.pid), '/T', '/F']);
      } else {
        try {
          process.kill(-child.pid, 'SIGKILL');
        } catch {
          try {
            child.kill('SIGKILL');
          } catch {
            // Last resort already failed; nothing else owns this tree.
          }
        }
      }
    },
  };
}
