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

const diagnosticReferences: Readonly<Record<string, string>> = {
  LSM001: 'front-matter',
  LSM002: 'front-matter',
  LSM003: 'front-matter',
  LSM004: 'front-matter',
  LSM005: 'front-matter',
  LSM006: 'front-matter',
  LSM007: 'front-matter',
  LSM008: 'front-matter',
  LSC101: 'front-matter-binding',
  LSC102: 'front-matter-binding',
  LSC103: 'front-matter-binding',
  LSC104: 'front-matter-binding',
  LSM013: 'lsm013',
  LSG005: 'lsg005',
  LSG006: 'lsg006',
  LSA1402: 'lsa1402',
  LSA9001: 'lsa9001',
  LSQ001: 'lsq001',
  LSQ002: 'lsq002',
};

/** Only registered IDs link to official guidance; server data never supplies a URL. */
export function diagnosticReferenceUrl(code: string): string | undefined {
  return Object.hasOwn(diagnosticReferences, code)
    ? `https://github.com/Htkym/lithosharp/blob/main/docs/diagnostics.md#${diagnosticReferences[code]}`
    : undefined;
}

/**
 * VS Code boundary shape. Keeps the diagnostic identity (code/source) that
 * the smoke test asserts on; range/message map one-to-one. Severity is
 * converted from LSP numbering (1=Error..4=Hint) to VS Code numbering
 * (0=Error..3=Hint); out-of-range values clamp to that range.
 */
export function toVsDiagnostic(item: LspDiagnosticData): {
  range: LspDiagnosticData['range'];
  message: string;
  severity: number;
  code: string | { value: string; target: string };
  source: string;
} {
  const vscodeSeverity = Math.min(3, Math.max(0, item.severity - 1));
  const target = diagnosticReferenceUrl(item.code);
  return {
    range: item.range,
    message: item.message,
    severity: vscodeSeverity,
    code: target === undefined ? item.code : { value: item.code, target },
    source: item.source,
  };
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
