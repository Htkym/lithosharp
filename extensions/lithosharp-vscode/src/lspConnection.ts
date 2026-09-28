import { StringDecoder } from 'node:string_decoder';

/** JSON-RPC message callbacks. */
export interface LspHandlers {
  onNotification(method: string, params: unknown): void;
  onRequestError?(id: string | number, code: number, message: string): void;
}

/** Minimal process surface shared with the serve controller shape. */
export interface LspProcess {
  stdinWrite(line: string): void;
  closeStdin(): void;
  onStdout(handler: (chunk: Buffer) => void): void;
  onStderr(handler: (chunk: Buffer) => void): void;
  onExit(callback: (code: number | null) => void): void;
  killTree(): void;
}

/**
 * LSP Content-Length connection over a child process. Responses are matched
 * by id, so intentionally reordered answers still land on the right caller.
 * Exactly one outstanding read exists; stdout purity is the server's promise,
 * but garbage lines never crash this side.
 */
export class LspConnection {
  private readonly decoder = new StringDecoder('utf8');
  private pending = '';
  private readonly waiting = new Map<string | number, (result: unknown) => void>();
  private readonly errorWaiting = new Map<string | number, (code: number, message: string) => void>();
  private exited = false;

  constructor(
    private readonly child: LspProcess,
    private readonly handlers: LspHandlers,
    private readonly onLog: (line: string) => void = () => {},
  ) {
    child.onStdout((chunk) => this.ingest(chunk));
    child.onExit(() => {
      this.exited = true;
    });
  }

  get alive(): boolean {
    return !this.exited;
  }

  sendNotification(method: string, params: unknown): void {
    const body = JSON.stringify({ jsonrpc: '2.0', method, params });
    const bytes = Buffer.byteLength(body, 'utf8');
    this.child.stdinWrite(`Content-Length: ${bytes}\r\n\r\n${body}`);
  }

  sendRequest<T>(id: string | number, method: string, params: unknown): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      this.waiting.set(id, resolve as (result: unknown) => void);
      this.errorWaiting.set(id, (code, message) => reject(new Error(`LSP ${code}: ${message}`)));
      try {
        const body = JSON.stringify({ jsonrpc: '2.0', id, method, params });
        const bytes = Buffer.byteLength(body, 'utf8');
        this.child.stdinWrite(`Content-Length: ${bytes}\r\n\r\n${body}`);
      } catch (error) {
        this.waiting.delete(id);
        this.errorWaiting.delete(id);
        reject(error);
      }
    });
  }

  cancelRequest(id: string | number): void {
    this.sendNotification('$/cancelRequest', { id });
  }

  private ingest(chunk: Buffer): void {
    this.pending += this.decoder.write(chunk);
    for (;;) {
      const headerEnd = this.pending.indexOf('\r\n\r\n');
      if (headerEnd < 0) {
        return;
      }
      const header = this.pending.slice(0, headerEnd);
      const match = /Content-Length:\s*(\d+)/i.exec(header);
      if (!match) {
        this.onLog('Ignoring a frameless LSP line.');
        const next = this.pending.indexOf('\n', headerEnd + 4);
        this.pending = next < 0 ? '' : this.pending.slice(next + 1);
        continue;
      }
      const length = Number(match[1]);
      const bodyBytes = Buffer.byteLength(this.pending.slice(headerEnd + 4), 'utf8');
      if (bodyBytes < length) {
        return;
      }
      // Slice by bytes, not characters, so multibyte content survives splits.
      const raw = Buffer.from(this.pending, 'utf8').subarray(headerEnd + 4, headerEnd + 4 + length).toString('utf8');
      this.pending = Buffer.from(this.pending, 'utf8').subarray(headerEnd + 4 + length).toString('utf8');
      let message: Record<string, unknown>;
      try {
        message = JSON.parse(raw) as Record<string, unknown>;
      } catch {
        this.onLog('Ignoring a malformed LSP message.');
        continue;
      }
      this.dispatch(message);
    }
  }

  private dispatch(message: Record<string, unknown>): void {
    if (typeof message['method'] === 'string' && message['id'] === undefined) {
      this.handlers.onNotification(message['method'] as string, message['params']);
      return;
    }
    if (message['id'] !== undefined && typeof message['method'] !== 'string') {
      const id = message['id'] as string | number;
      if (typeof message['error'] === 'object' && message['error'] !== null) {
        const error = message['error'] as { code?: number; message?: string };
        this.errorWaiting.get(id)?.(typeof error.code === 'number' ? error.code : -32603, String(error.message ?? 'error'));
      } else {
        this.waiting.get(id)?.(message['result']);
      }
      this.waiting.delete(id);
      this.errorWaiting.delete(id);
      return;
    }
    this.onLog('Ignoring an LSP message without method or id.');
  }
}
