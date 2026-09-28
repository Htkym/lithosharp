import { StringDecoder } from 'node:string_decoder';

/** Machine events the controller understands. Unknown events are ignored. */
export type ServeEvent =
  | { event: 'startup'; url: string; actualPort: number; requestedPort: number; generation: number }
  | { event: 'rebuild-started' }
  | { event: 'rebuild-succeeded'; generation: number }
  | { event: 'rebuild-failed'; generation: number; error?: string }
  | { event: 'startup-failed'; error?: string }
  | { event: 'control-ack'; requestId: string }
  | { event: 'control-error'; requestId: string | null }
  | { event: 'shutdown'; generation: number };

/** Controller states. Transitions come from real process events only. */
export type ServeState = 'Stopped' | 'Starting' | 'Running' | 'Rebuilding' | 'Failed' | 'Stopping';

export interface SpawnedProcess {
  readonly pid: number | undefined;
  stdinWrite(line: string): void;
  closeStdin(): void;
  onStdout(handler: (chunk: Buffer) => void): void;
  onStderr(handler: (chunk: Buffer) => void): void;
  onExit(callback: (code: number | null) => void): void;
  killTree(): void;
}

export interface ServeControllerOptions {
  projectId: string;
  cliCommand: string[];
  cwd: string;
  spawn: (command: string[], cwd: string) => SpawnedProcess;
  /** Startup grace before the server counts as failed. */
  startupTimeoutMs?: number;
  /** Grace after structured shutdown before tree recovery. */
  stopTimeoutMs?: number;
  /** Bounded stderr retention for diagnostics display. */
  maxStderrChars?: number;
  onEvent?: (event: ServeEvent) => void;
  onLog?: (line: string) => void;
}

/**
 * Per-project serve controller. One instance owns at most one server process
 * tree and never touches anything else. A second Start while active coalesces
 * onto the current state instead of spawning again.
 */
export class ServeController {
  private state: ServeState = 'Stopped';
  private child: SpawnedProcess | undefined;
  private readonly decoder = new StringDecoder('utf8');
  private pending = '';
  private stderrKept = '';
  private shutdownTimer: NodeJS.Timeout | undefined;
  private startupTimer: NodeJS.Timeout | undefined;
  private stopped = false;

  url: string | null = null;
  actualPort = 0;
  generation = 0;
  lastError: string | null = null;

  constructor(private readonly options: ServeControllerOptions) {}

  getState(): ServeState {
    return this.state;
  }

  getStderrTail(): string {
    return this.stderrKept;
  }

  start(): ServeState {
    if (this.stopped) {
      return this.state;
    }
    if (this.state === 'Starting' || this.state === 'Running' || this.state === 'Rebuilding') {
      return this.state;
    }
    if (this.state === 'Stopping') {
      return this.state;
    }
    this.lastError = null;
    this.pending = '';
    this.decoder.end();
    this.state = 'Starting';
    const child = this.options.spawn(this.options.cliCommand, this.options.cwd);
    this.child = child;
    child.onStdout((chunk) => this.ingestStdout(chunk));
    child.onStderr((chunk) => this.ingestStderr(chunk));
    child.onExit((code) => this.onExit(code));
    const timeout = this.options.startupTimeoutMs ?? 60000;
    this.startupTimer = setTimeout(() => this.onStartupTimeout(), timeout);
    this.startupTimer.unref?.();
    return this.state;
  }

  stop(requestId = 'stop'): ServeState {
    if (this.state === 'Stopped' || this.stopped) {
      return this.state;
    }
    if (this.state === 'Stopping') {
      return this.state;
    }
    this.state = 'Stopping';
    // Structured stdin shutdown first, even mid-startup: the CLI honors it
    // before serving. Tree recovery is the last resort after a timeout.
    this.requestStop(requestId);
    this.armShutdownTimer();
    return this.state;
  }

  dispose(): void {
    this.stopped = true;
    if (this.state !== 'Stopped' && this.state !== 'Stopping') {
      this.state = 'Stopping';
      this.requestStop('dispose');
    }
    // Keep the recovery timer armed (unref'd): a stuck server is still
    // reclaimed after unload without holding the host open.
    this.armShutdownTimer();
  }

  private requestStop(requestId: string): void {
    try {
      this.child?.stdinWrite(
        JSON.stringify({ schemaVersion: '1.0', command: 'shutdown', requestId }) + '\n',
      );
    } catch {
      this.recoverTree();
    }
  }

  private armShutdownTimer(): void {
    if (this.shutdownTimer) {
      clearTimeout(this.shutdownTimer);
    }
    const timeout = this.options.stopTimeoutMs ?? 10000;
    this.shutdownTimer = setTimeout(() => this.recoverTree(), timeout);
    this.shutdownTimer.unref?.();
  }

  private clearTimers(): void {
    if (this.startupTimer) {
      clearTimeout(this.startupTimer);
      this.startupTimer = undefined;
    }
    if (this.shutdownTimer) {
      clearTimeout(this.shutdownTimer);
      this.shutdownTimer = undefined;
    }
  }

  private ingestStdout(chunk: Buffer): void {
    this.pending += this.decoder.write(chunk);
    let index: number;
    while ((index = this.pending.indexOf('\n')) >= 0) {
      const line = this.pending.slice(0, index).replace(/\r$/, '');
      this.pending = this.pending.slice(index + 1);
      if (line.trim() === '') {
        continue;
      }
      this.handleLine(line);
    }
  }

  private handleLine(line: string): void {
    if (this.stopped) {
      return;
    }
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(line) as Record<string, unknown>;
    } catch {
      this.options.onLog?.(`Ignoring a non-JSON serve line of ${line.length} chars.`);
      return;
    }
    if (typeof parsed['event'] !== 'string') {
      this.options.onLog?.('Ignoring a serve event without a name.');
      return;
    }
    const event = parsed as unknown as ServeEvent;
    this.options.onEvent?.(event);
    switch (event.event) {
      case 'startup':
        this.clearStartupTimer();
        this.url = event.url;
        this.actualPort = event.actualPort;
        this.generation = event.generation;
        this.lastError = null;
        this.state = 'Running';
        break;
      case 'rebuild-started':
        if (this.state === 'Running') {
          this.state = 'Rebuilding';
        }
        break;
      case 'rebuild-succeeded':
        this.generation = event.generation;
        this.lastError = null;
        if (this.state === 'Rebuilding') {
          this.state = 'Running';
        }
        break;
      case 'rebuild-failed':
        this.generation = event.generation;
        this.lastError = event.error ?? 'Rebuild failed.';
        this.state = 'Failed';
        break;
      case 'startup-failed':
        this.clearStartupTimer();
        this.lastError = event.error ?? 'Serve failed to start.';
        this.state = 'Failed';
        break;
      case 'shutdown':
        this.generation = event.generation;
        break;
      default:
        break;
    }
  }

  private ingestStderr(chunk: Buffer): void {
    const text = chunk.toString('utf8');
    const cap = this.options.maxStderrChars ?? 65536;
    this.stderrKept = (this.stderrKept + text).slice(-cap);
  }

  private onStartupTimeout(): void {
    if (this.state !== 'Starting') {
      return;
    }
    this.lastError = 'Serve startup timed out waiting for the startup event.';
    this.options.onLog?.(this.lastError);
    this.recoverTree();
    this.state = 'Failed';
  }

  private onExit(code: number | null): void {
    this.clearTimers();
    this.child = undefined;
    if (this.stopped) {
      this.state = 'Stopped';
      return;
    }
    if (this.state === 'Stopping') {
      this.state = 'Stopped';
      return;
    }
    // An unexpected exit is a failure, including a clean code without events.
    if (this.state !== 'Stopped') {
      this.lastError = `Serve process exited unexpectedly (code ${code ?? 'unknown'}).`;
      this.state = 'Failed';
    }
  }

  private recoverTree(): void {
    try {
      this.child?.killTree();
    } catch {
      // Last-resort recovery never throws past the controller.
    }
  }

  private clearStartupTimer(): void {
    if (this.startupTimer) {
      clearTimeout(this.startupTimer);
      this.startupTimer = undefined;
    }
  }
}
