import * as fs from 'node:fs';
import * as path from 'node:path';
import { requireTrusted } from './trust.js';

/** How a CLI executable is invoked. */
export interface CliCommand {
  command: string[];
  cwd: string | undefined;
}

/** CLI resolution outcome. Resolving never executes anything. */
export type CliResolution =
  | { kind: 'explicit' | 'local' | 'global'; command: CliCommand; version: null }
  | { kind: 'missing'; reason: string };

/** Minimal process probe. Tests substitute a fake and count calls. */
export interface ExecProbe {
  calls: Array<{ command: string[]; cwd: string | undefined }>;
  run(command: string[], cwd?: string): Promise<{ exit: number; stdout: string }>;
}

export interface ResolveOptions {
  isTrusted: boolean;
  /** Explicit `lithosharp.cliPath` setting. Workspace values apply only when trusted. */
  explicitPath?: string;
  /** Selected project directory for the local-tool probe. */
  projectDir?: string;
  /** PATH value used for lookup. Never executes. */
  envPath: string;
}

const VERSION_PATTERN = /(\d+\.\d+\.\d+)/;

/**
 * Resolves the CLI without executing anything: explicit setting, then the
 * project-local dotnet tool manifest, then the global PATH. Every branch is
 * a static file check; version lookup is a separate explicit step.
 */
export async function resolveCli(options: ResolveOptions): Promise<CliResolution> {
  if (!options.isTrusted) {
    return {
      kind: 'missing',
      reason: 'Workspace is untrusted: the CLI is not resolved and nothing is executed. Trust the workspace first.',
    };
  }
  const explicitPath = (options.explicitPath ?? '').trim();
  if (explicitPath !== '') {
    if (!(await isFile(explicitPath))) {
      return { kind: 'missing', reason: `Configured lithosharp.cliPath does not exist: ${explicitPath}` };
    }
    return { kind: 'explicit', command: { command: [explicitPath], cwd: undefined }, version: null };
  }
  if (options.projectDir) {
    const manifest = path.join(options.projectDir, '.config', 'dotnet-tools.json');
    if (await isFile(manifest)) {
      try {
        const parsed: unknown = JSON.parse(await fs.promises.readFile(manifest, 'utf8'));
        const tools = (parsed as { tools?: Record<string, unknown> }).tools ?? {};
        if (Object.keys(tools).some((name) => /lithosharp/i.test(name))) {
          return { kind: 'local', command: { command: ['dotnet', 'tool', 'run', 'lithosharp'], cwd: options.projectDir }, version: null };
        }
      } catch {
        // An unreadable manifest is not a local tool; fall through to PATH.
      }
    }
  }
  const global = await findOnPath('lithosharp', options.envPath);
  if (global) {
    return { kind: 'global', command: { command: [global], cwd: undefined }, version: null };
  }
  if (!(await findOnPath('dotnet', options.envPath))) {
    return {
      kind: 'missing',
      reason: 'No dotnet runtime on PATH. Install the .NET 10 SDK, then install the LithoSharp tool.',
    };
  }
  return {
    kind: 'missing',
    reason: 'LithoSharp CLI not found. Run `dotnet tool install LithoSharp.Tool` or set lithosharp.cliPath.',
  };
}

/**
 * Fetches the CLI version with a single explicit execution.
 * Never runs in untrusted workspaces.
 */
export async function fetchCliVersion(
  resolution: Exclude<CliResolution, { kind: 'missing' }>,
  isTrusted: boolean,
  probe: ExecProbe,
): Promise<string | null> {
  requireTrusted(isTrusted, 'query the CLI version');
  const result = await probe.run([...resolution.command.command, '--version'], resolution.command.cwd);
  if (result.exit !== 0) {
    return null;
  }
  return result.stdout.match(VERSION_PATTERN)?.[1] ?? null;
}

async function isFile(candidate: string): Promise<boolean> {
  try {
    return (await fs.promises.stat(candidate)).isFile();
  } catch {
    return false;
  }
}

async function findOnPath(name: string, envPath: string): Promise<string | null> {
  const names = process.platform === 'win32' ? [`${name}.exe`, `${name}.cmd`, name] : [name];
  for (const dir of envPath.split(path.delimiter)) {
    if (!dir) {
      continue;
    }
    for (const candidate of names) {
      const full = path.join(dir, candidate);
      if (await isFile(full)) {
        return full;
      }
    }
  }
  return null;
}
