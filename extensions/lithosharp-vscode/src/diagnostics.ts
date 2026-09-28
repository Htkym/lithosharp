/** Plain diagnostic shape shared with the client. VS Code types are adapted at the boundary. */
export interface LspDiagnosticData {
  range: { start: { line: number; character: number }; end: { line: number; character: number } };
  severity: number;
  code: string;
  source: string;
  message: string;
}

/** Minimal diagnostic collection surface (mirrors vscode.DiagnosticCollection). */
export interface DiagnosticSink {
  set(uri: string, diagnostics: LspDiagnosticData[]): void;
  delete(uri: string): void;
  clear(): void;
}

/**
 * Version-guarded diagnostics. Late responses for older buffer versions are
 * dropped so stale results are never reshown. Unsaved analysis and build
 * diagnostics stay in this Editor-side collection only.
 */
export class DiagnosticStore {
  private readonly applied = new Map<string, number>();

  constructor(private readonly sink: DiagnosticSink) {}

  publish(uri: string, version: number | null, diagnostics: LspDiagnosticData[]): void {
    if (version !== null) {
      const current = this.applied.get(uri) ?? -1;
      if (version < current) {
        return;
      }
      this.applied.set(uri, version);
    }
    this.sink.set(uri, diagnostics);
  }

  clear(uri: string): void {
    this.applied.delete(uri);
    this.sink.delete(uri);
  }

  clearAll(): void {
    this.applied.clear();
    this.sink.clear();
  }
}
