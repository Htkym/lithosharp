import * as childProcess from 'node:child_process';
import type { SpawnedProcess } from './serveController.js';

/**
 * Production process spawner. The child owns its process group so timeout
 * recovery can reclaim the whole tree without touching anything else.
 */
export function spawnProcess(command: string[], cwd: string): SpawnedProcess {
  const child = childProcess.spawn(command[0]!, command.slice(1), {
    cwd,
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
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
      if (child.pid === undefined) {
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
