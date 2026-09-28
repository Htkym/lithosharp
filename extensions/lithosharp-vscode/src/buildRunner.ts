import { requireTrusted } from './trust.js';

/** One-shot CLI invocation probe. Tests substitute a fake. */
export interface RunProbe {
  run(command: string[], cwd: string, input?: string): Promise<{ exit: number; stdout: string; stderr: string }>;
}

/** Structured build result. Machine output is parsed, never pattern-matched. */
export type BuildResult =
  | {
      ok: true;
      exitCode: number;
      outputDirectory: string | null;
      diagnosticsText: string | null;
      raw: unknown;
    }
  | { ok: false; exitCode: number; error: string; diagnosticsText: string | null };

/**
 * Serialized one-shot builds per project. Concurrent build requests for the
 * same runner queue behind the running one instead of overlapping writes.
 * A build press is never a publish approval: this runner only builds.
 */
export class BuildRunner {
  private queue: Promise<unknown> = Promise.resolve();

  constructor(
    private readonly options: {
      isTrusted: () => boolean;
      cli: (command: string) => { command: string[]; cwd?: string | undefined };
      cwd: string;
      probe: RunProbe;
      maxOutputChars?: number;
      onLog?: (line: string) => void;
    },
  ) {}

  /** Runs `build`, `check`, or `inspect` with machine output and returns the envelope. */
  async run(command: 'build' | 'check' | 'inspect'): Promise<BuildResult> {
    requireTrusted(this.options.isTrusted(), `run lithosharp ${command}`);
    const pending = this.queue.then(() => this.execute(command));
    // A rejection must not poison later queued builds; each caller sees its own.
    this.queue = pending.catch(() => undefined);
    return pending;
  }

  private async execute(command: 'build' | 'check' | 'inspect'): Promise<BuildResult> {
    const cli = this.options.cli(command);
    let result: { exit: number; stdout: string; stderr: string };
    try {
      result = await this.options.probe.run(cli.command, cli.cwd ?? this.options.cwd);
    } catch (error) {
      return { ok: false, exitCode: 1, error: `CLI execution failed: ${String(error)}`, diagnosticsText: null };
    }
    const cap = this.options.maxOutputChars ?? 65536;
    const stdout = result.stdout.slice(-cap);
    const stderr = result.stderr.slice(-cap);
    if (stderr.trim() !== '') {
      this.options.onLog?.(stderr.trim().split('\n').slice(-5).join('\n'));
    }
    let envelope: Record<string, unknown>;
    try {
      const parsed: unknown = JSON.parse(stdout);
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        throw new Error('not an object');
      }
      envelope = parsed as Record<string, unknown>;
    } catch {
      return {
        ok: false,
        exitCode: result.exit,
        error: `Unparseable CLI ${command} output (exit ${result.exit}).`,
        diagnosticsText: null,
      };
    }
    if (typeof envelope['schemaVersion'] !== 'string' || typeof envelope['success'] !== 'boolean') {
      return { ok: false, exitCode: result.exit, error: `Unknown CLI ${command} envelope.`, diagnosticsText: null };
    }
    if (envelope['success'] !== true) {
      return {
        ok: false,
        exitCode: typeof envelope['exitCode'] === 'number' ? (envelope['exitCode'] as number) : result.exit,
        error: typeof envelope['error'] === 'string' ? (envelope['error'] as string) : `CLI ${command} failed.`,
        diagnosticsText: typeof envelope['diagnosticsText'] === 'string' ? (envelope['diagnosticsText'] as string) : null,
      };
    }
    return {
      ok: true,
      exitCode: typeof envelope['exitCode'] === 'number' ? (envelope['exitCode'] as number) : 0,
      outputDirectory: typeof envelope['outputDirectory'] === 'string' ? (envelope['outputDirectory'] as string) : null,
      diagnosticsText: typeof envelope['diagnosticsText'] === 'string' ? (envelope['diagnosticsText'] as string) : null,
      raw: envelope,
    };
  }
}
