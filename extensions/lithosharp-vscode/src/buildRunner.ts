import { requireTrusted } from './trust.js';

export type CliOperation = 'build' | 'check' | 'inspect' | 'preflight';
export type OperationOutcome = 'succeeded' | 'failed' | 'cancelled' | 'execution-error' | 'invalid-response' | 'unsupported-schema' | 'inconsistent';

export interface CliDiagnostic {
  id: string;
  severity: string;
  message: string;
  file?: string | null;
  line?: number | null;
  column?: number | null;
}

/** One-shot CLI invocation probe. Tests substitute a fake. */
export interface RunProbe {
  run(command: string[], cwd: string, input?: string, signal?: AbortSignal): Promise<{ exit: number; stdout: string; stderr: string; signal?: string | null }>;
}

/** Structured build result. Machine output is parsed, never pattern-matched. */
export type BuildResult = { processExitCode: number | null } & (
  | {
      ok: true;
      outcome: 'succeeded';
      diagnostics: CliDiagnostic[];
      exitCode: number;
      outputDirectory: string | null;
      diagnosticsText: string | null;
      raw: unknown;
    }
  | { ok: false; outcome: Exclude<OperationOutcome, 'succeeded'>; diagnostics: CliDiagnostic[]; exitCode: number; error: string; diagnosticsText: string | null; raw?: unknown });

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isDiagnostic(value: unknown): value is CliDiagnostic {
  if (!isRecord(value) || typeof value['id'] !== 'string' || typeof value['severity'] !== 'string' || typeof value['message'] !== 'string') return false;
  if (value['file'] !== undefined && value['file'] !== null && typeof value['file'] !== 'string') return false;
  return ['line', 'column', 'endLine', 'endColumn'].every(key => value[key] === undefined || value[key] === null ||
    (Number.isSafeInteger(value[key]) && (value[key] as number) > 0));
}

function isPreflight(value: unknown): boolean {
  if (!isRecord(value) || typeof value['mode'] !== 'string' || typeof value['stage'] !== 'string' ||
      !Array.isArray(value['deferredReasons']) || !value['deferredReasons'].every(item => typeof item === 'string')) return false;
  if (['coverage', 'sourceCoverage'].some(key => value[key] !== undefined && typeof value[key] !== 'string')) return false;
  if (value['sources'] !== undefined && (!Array.isArray(value['sources']) || !value['sources'].every(source =>
    isRecord(source) && typeof source['file'] === 'string' && typeof source['readState'] === 'string' && typeof source['analysisState'] === 'string' &&
    (source['sourceHash'] === undefined || source['sourceHash'] === null || typeof source['sourceHash'] === 'string')))) return false;
  return true;
}

function isBuildPlan(value: unknown): boolean {
  return Array.isArray(value) && value.every(node => isRecord(node) &&
    Array.isArray(node['inputs']) && node['inputs'].every(input => isRecord(input) && typeof input['key'] === 'string' &&
      (input['value'] === undefined || input['value'] === null || typeof input['value'] === 'string')) &&
    Array.isArray(node['artifacts']) && node['artifacts'].every(artifact => isRecord(artifact) &&
      typeof artifact['path'] === 'string' && typeof artifact['publicPath'] === 'string'));
}

/** Only schema 1's declared envelope and consumed fields are trusted. Unknown additions remain in raw. */
export function parseCliResult(command: CliOperation, exit: number, stdout: string): BuildResult {
  let raw: unknown;
  try { raw = JSON.parse(stdout); } catch {
    return { ok: false, outcome: 'invalid-response', processExitCode: exit, exitCode: exit, error: `Unparseable CLI ${command} output (exit ${exit}).`, diagnosticsText: null, diagnostics: [], raw: stdout };
  }
  const envelope = isRecord(raw) ? raw : {};
  const diagnosticsText = typeof envelope['diagnosticsText'] === 'string' ? envelope['diagnosticsText'] : null;
  const diagnostics = Array.isArray(envelope['diagnostics']) ? envelope['diagnostics'].filter(isDiagnostic) : [];
  const failure = (outcome: Exclude<OperationOutcome, 'succeeded'>, error: string): BuildResult =>
    ({ ok: false, outcome, processExitCode: exit, exitCode: exit, error, diagnosticsText, diagnostics, raw });
  if (typeof envelope['schemaVersion'] !== 'string' || !/^\d+(?:\.\d+)*$/.test(envelope['schemaVersion']))
    return failure('invalid-response', `Unknown CLI ${command} envelope schema.`);
  if (Number(envelope['schemaVersion'].split('.')[0]) !== 1)
    return failure('unsupported-schema', `Unsupported CLI ${command} schema ${envelope['schemaVersion']}.`);
  if (typeof envelope['success'] !== 'boolean' || !Number.isSafeInteger(envelope['exitCode']) ||
      !Array.isArray(envelope['diagnostics']) || diagnostics.length !== envelope['diagnostics'].length ||
      ['outputDirectory', 'diagnosticsText', 'error', 'projectCoreVersion'].some(key =>
        envelope[key] !== undefined && envelope[key] !== null && typeof envelope[key] !== 'string') ||
      (envelope['buildPlan'] !== undefined && !isBuildPlan(envelope['buildPlan'])) ||
      (envelope['preflight'] !== undefined && envelope['preflight'] !== null && !isPreflight(envelope['preflight'])))
    return failure('invalid-response', `Invalid CLI ${command} envelope fields.`);
  if (exit !== envelope['exitCode'] || envelope['success'] !== (exit === 0))
    return failure('inconsistent', `CLI ${command} process exit ${exit} contradicts envelope success=${envelope['success']}, exitCode=${envelope['exitCode']}.`);
  if (envelope['success'] !== true)
    return failure(exit === 130 ? 'cancelled' : 'failed', typeof envelope['error'] === 'string' ? envelope['error'] : `CLI ${command} failed.`);
  return { ok: true, outcome: 'succeeded', processExitCode: exit, exitCode: exit, outputDirectory: typeof envelope['outputDirectory'] === 'string' ? envelope['outputDirectory'] : null,
    diagnosticsText, diagnostics, raw };
}

/**
 * Serialized one-shot builds per project. Concurrent build requests for the
 * same runner queue behind the running one instead of overlapping writes.
 * A build press is never a publish approval: this runner only builds.
 */
export class BuildRunner {
  private queue: Promise<unknown> = Promise.resolve();
  private active: AbortController | undefined;
  private disposed = false;

  constructor(
    private readonly options: {
      isTrusted: () => boolean;
      cli: (command: string, args: readonly string[]) => { command: string[]; cwd?: string | undefined };
      cwd: string;
      probe: RunProbe;
      maxOutputChars?: number;
      onLog?: (line: string) => void;
    },
  ) {}

  /** Runs `build`, `check`, or `inspect` with machine output and returns the envelope. */
  async run(command: CliOperation, args: readonly string[] = []): Promise<BuildResult> {
    requireTrusted(this.options.isTrusted(), `run lithosharp ${command}`);
    if (this.disposed) {
      return { ok: false, outcome: 'cancelled', diagnostics: [], processExitCode: null, exitCode: 130, error: 'Build runner is disposed.', diagnosticsText: null };
    }
    const capturedArgs = [...args];
    const resolved = this.options.cli(command, capturedArgs);
    const invocation = { ...resolved, command: [...resolved.command] };
    const pending = this.queue.then(() => this.execute(command, invocation));
    // A rejection must not poison later queued builds; each caller sees its own.
    this.queue = pending.catch(() => undefined);
    return pending;
  }

  /** Cancels the owned CLI process and prevents queued requests from starting. */
  dispose(): void {
    this.disposed = true;
    this.active?.abort();
  }

  private async execute(command: CliOperation, cli: { command: string[]; cwd?: string | undefined }): Promise<BuildResult> {
    if (this.disposed) {
      return { ok: false, outcome: 'cancelled', diagnostics: [], processExitCode: null, exitCode: 130, error: 'Build runner was disposed before this command started.', diagnosticsText: null };
    }
    // The request may have waited behind another project build while trust changed.
    requireTrusted(this.options.isTrusted(), `run lithosharp ${command}`);
    const abort = new AbortController();
    this.active = abort;
    let result: Awaited<ReturnType<RunProbe['run']>>;
    try {
      result = await this.options.probe.run(cli.command, cli.cwd ?? this.options.cwd, undefined, abort.signal);
    } catch (error) {
      return { ok: false, outcome: abort.signal.aborted ? 'cancelled' : 'execution-error', diagnostics: [], processExitCode: null, exitCode: abort.signal.aborted ? 130 : 1, error: `CLI execution failed: ${String(error)}`, diagnosticsText: null };
    } finally {
      if (this.active === abort) {
        this.active = undefined;
      }
    }
    const cap = this.options.maxOutputChars ?? 65536;
    // The process probe bounds capture; only display text is truncated here.
    const stdout = result.stdout;
    const stderr = result.stderr.slice(-cap);
    if (!this.disposed && stderr.trim() !== '') {
      this.options.onLog?.(stderr.trim().split('\n').slice(-5).join('\n'));
    }
    if (abort.signal.aborted || this.disposed || result.signal) {
      const partial = parseCliResult(command, result.exit, stdout);
      return { ...partial, ok: false, outcome: 'cancelled', processExitCode: result.signal ? null : result.exit, exitCode: 130,
        error: result.signal ? `CLI invocation was interrupted by ${result.signal}.` : 'CLI invocation was cancelled.' };
    }
    return parseCliResult(command, result.exit, stdout);
  }
}
