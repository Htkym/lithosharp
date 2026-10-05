import { LspConnection, type LspProcess } from './lspConnection.js';
import { DiagnosticStore, type LspDiagnosticData } from './diagnostics.js';
import { mapSymbols, type SymbolData } from './symbols.js';
import { requireTrusted } from './trust.js';

export interface OpenDocument {
  uri: string;
  languageId: string;
  version: number;
  text: string;
}

export interface LspClientOptions {
  serverCommand: string[];
  cwd: string;
  spawn: (command: string[], cwd: string) => LspProcess;
  /** Debounce for change notifications in ms (50-1000, default 150). */
  debounceMs?: number;
  initializationOptions?: Record<string, unknown>;
  isTrusted: () => boolean;
  onLog?: (line: string) => void;
  onState?: (state: string) => void;
}

/**
 * Editor-side LSP client. Connects file-scheme Markdown/MDX documents in the
 * target project, debounces change traffic, guards versions, and never runs
 * full-site builds, SSR, or user modules on keystrokes.
 */
export class LspClient {
  private connection: LspConnection | undefined;
  private child: LspProcess | undefined;
  private requestSequence = 1;
  private readonly buffers = new Map<string, {
    version: number;
    languageId: string;
    text: string;
    pending: { version: number; text: string } | null;
    timer: NodeJS.Timeout | undefined;
  }>();
  private readonly projectContexts = new Map<string, { projectId: string; folders: string[]; snapshot: unknown | null }>();
  private readonly store: DiagnosticStore;
  private readonly debounceMs: number;
  private started = false;
  private ready = false;

  constructor(
    private readonly options: LspClientOptions,
    private readonly diagnosticsSink: { set(uri: string, diagnostics: LspDiagnosticData[]): void; delete(uri: string): void; clear(): void },
  ) {
    const debounce = options.debounceMs ?? 150;
    this.debounceMs = Math.min(1000, Math.max(50, debounce));
    this.store = new DiagnosticStore(diagnosticsSink);
  }

  /** Starts the server and handshakes. One client per host: no double servers. */
  async start(): Promise<void> {
    requireTrusted(this.options.isTrusted(), 'start the language server');
    if (this.started) {
      return;
    }
    this.started = true;
    this.ready = false;
    const child = this.options.spawn(this.options.serverCommand, this.options.cwd);
    this.child = child;
    child.onStderr((chunk) => this.options.onLog?.(chunk.toString('utf8').trimEnd()));
    child.onExit(() => {
      if (this.child !== child) {
        return;
      }
      this.started = false;
      this.ready = false;
      this.child = undefined;
      this.connection = undefined;
      this.store.clearAll();
      this.options.onState?.('stopped');
    });
    const connection = new LspConnection(
      child,
      {
        onNotification: (method, params) => this.onNotification(method, params),
        onRequestError: (_id, code, message) => this.options.onLog?.(`LSP request failed: ${code} ${message}`),
      },
      (line) => this.options.onLog?.(line),
    );
    this.connection = connection;
    const id = this.nextId();
    await connection.sendRequest<unknown>(id, 'initialize', {
      processId: null,
      capabilities: { textDocument: { publishDiagnostics: { versionSupport: true } } },
      initializationOptions: this.options.initializationOptions ?? {},
    });
    if (this.child !== child || this.connection !== connection) {
      return;
    }
    connection.sendNotification('initialized', {});
    this.ready = true;
    for (const context of this.projectContexts.values()) {
      connection.sendNotification('lithosharp/projectContext', context);
    }
    for (const [uri, buffer] of this.buffers) {
      connection.sendNotification('textDocument/didOpen', {
        textDocument: { uri, languageId: buffer.languageId, version: buffer.version, text: buffer.text },
      });
    }
    this.options.onState?.('ready');
  }

  stop(): void {
    const connection = this.connection;
    const child = this.child;
    try {
      child?.closeStdin();
    } catch {
      // Stopping never throws past the client.
    }
    connection?.dispose();
    try {
      // EOF may be ignored. Terminate only this client's still-owned child;
      // a synchronous exit callback already clears it and needs no kill.
      if (child && this.child === child) {
        child.killTree();
      }
    } catch {
      // Local requests/state still settle when process termination fails.
    }
    this.ready = false;
    for (const buffer of this.buffers.values()) {
      if (buffer.timer) {
        clearTimeout(buffer.timer);
        buffer.timer = undefined;
      }
    }
    this.store.clearAll();
    this.started = false;
    this.connection = undefined;
    this.child = undefined;
  }

  /** Only file-scheme Markdown/MDX belongs to the editing session. */
  static isSupported(uri: string, languageId: string): boolean {
    if (!uri.startsWith('file://')) {
      return false;
    }
    const language = languageId.toLowerCase();
    if (language === 'markdown' || language === 'mdx') {
      return true;
    }
    return uri.toLowerCase().endsWith('.md') || uri.toLowerCase().endsWith('.mdx');
  }

  didOpen(document: OpenDocument): void {
    if (!LspClient.isSupported(document.uri, document.languageId)) {
      return;
    }
    const existing = this.buffers.get(document.uri);
    if (existing?.version === document.version && existing.text === document.text) {
      return;
    }
    if (existing?.timer) {
      clearTimeout(existing.timer);
    }
    const buffer = {
      version: document.version,
      languageId: document.languageId,
      text: document.text,
      pending: null,
      timer: undefined,
    };
    this.buffers.set(document.uri, buffer);
    if (this.ready) {
      this.connection?.sendNotification('textDocument/didOpen', {
        textDocument: { uri: document.uri, languageId: document.languageId, version: document.version, text: document.text },
      });
    }
  }

  didChange(uri: string, version: number, text: string): void {
    const buffer = this.buffers.get(uri);
    if (!buffer || version <= buffer.version) {
      return;
    }
    buffer.version = version;
    buffer.text = text;
    this.store.clear(uri);
    if (!this.ready) {
      buffer.pending = null;
      return;
    }
    if (buffer.timer) {
      clearTimeout(buffer.timer);
    }
    buffer.pending = { version, text };
    buffer.timer = setTimeout(() => this.flush(uri), this.debounceMs);
    buffer.timer.unref?.();
  }

  /** Flushes a pending change immediately (used for save). */
  flush(uri: string): void {
    const buffer = this.buffers.get(uri);
    if (!buffer || !buffer.pending) {
      return;
    }
    if (buffer.timer) {
      clearTimeout(buffer.timer);
      buffer.timer = undefined;
    }
    const pending = buffer.pending;
    buffer.pending = null;
    if (!this.ready) {
      return;
    }
    // Incremental resync by full text keeps ordering trivially correct here;
    // the server still applies ranges when clients send them.
    this.connection?.sendNotification('textDocument/didChange', {
      textDocument: { uri, version: pending.version },
      contentChanges: [{ text: pending.text }],
    });
  }

  didSave(uri: string): void {
    if (!this.buffers.has(uri)) {
      return;
    }
    if (!this.ready) {
      return;
    }
    this.flush(uri);
    this.connection?.sendNotification('textDocument/didSave', { textDocument: { uri } });
  }

  didClose(uri: string): void {
    const buffer = this.buffers.get(uri);
    if (!buffer) {
      return;
    }
    if (buffer.timer) {
      clearTimeout(buffer.timer);
    }
    this.buffers.delete(uri);
    this.store.clear(uri);
    if (this.ready) {
      this.connection?.sendNotification('textDocument/didClose', { textDocument: { uri } });
    }
  }

  /** Clears diagnostics for a deleted, renamed-away, or project-switched document. */
  forget(uri: string): void {
    const buffer = this.buffers.get(uri);
    if (buffer?.timer) {
      clearTimeout(buffer.timer);
    }
    this.buffers.delete(uri);
    this.store.clear(uri);
  }

  async requestSymbols(uri: string, onCancel?: (cancel: () => void) => void): Promise<SymbolData[]> {
    const buffer = this.buffers.get(uri);
    if (!buffer || !this.connection || !this.ready) {
      return [];
    }
    const connection = this.connection;
    const version = buffer.version;
    const text = buffer.text;
    this.flush(uri);
    if (this.connection !== connection || !this.ready || this.buffers.get(uri) !== buffer ||
        buffer.version !== version || buffer.text !== text) {
      return [];
    }
    const id = this.nextId();
    const promise = connection.sendRequest<unknown>(id, 'textDocument/documentSymbol', {
      textDocument: { uri },
    });
    onCancel?.(() => connection.cancelRequest(id));
    const result = await promise;
    if (this.connection !== connection || !this.ready || this.buffers.get(uri) !== buffer ||
        buffer.version !== version || buffer.text !== text) {
      return [];
    }
    return mapSymbols(result);
  }

  sendProjectContext(projectId: string, folders: string[], snapshot: unknown | null): void {
    const context = { projectId, folders: [...folders], snapshot };
    this.projectContexts.set(projectId, context);
    if (this.ready) {
      this.connection?.sendNotification('lithosharp/projectContext', context);
    }
  }

  private onNotification(method: string, params: unknown): void {
    if (method !== 'textDocument/publishDiagnostics' || typeof params !== 'object' || params === null) {
      return;
    }
    const record = params as { uri?: unknown; version?: unknown; diagnostics?: unknown };
    if (typeof record.uri !== 'string' || !Array.isArray(record.diagnostics)) {
      return;
    }
    const buffer = this.buffers.get(record.uri);
    const version = typeof record.version === 'number' ? record.version : null;
    if (!buffer || version !== buffer.version) {
      return;
    }
    const diagnostics: LspDiagnosticData[] = [];
    for (const item of record.diagnostics) {
      const mapped = mapDiagnostic(item);
      if (mapped) {
        diagnostics.push(mapped);
      }
    }
    this.store.publish(record.uri, version, diagnostics);
  }

  private nextId(): string {
    this.requestSequence += 1;
    return `lsp-${this.requestSequence}`;
  }
}

function mapDiagnostic(item: unknown): LspDiagnosticData | null {
  if (typeof item !== 'object' || item === null) {
    return null;
  }
  const record = item as Record<string, unknown>;
  const range = record['range'];
  if (typeof range !== 'object' || range === null) {
    return null;
  }
  // IDs, source, severity, and positions pass through untouched: this side
  // never invents diagnostic identities.
  return {
    range: range as LspDiagnosticData['range'],
    severity: typeof record['severity'] === 'number' ? (record['severity'] as number) : 3,
    code: String(record['code'] ?? ''),
    source: String(record['source'] ?? 'lithosharp'),
    message: String(record['message'] ?? ''),
  };
}
