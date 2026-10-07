/** Plain symbol tree. VS Code types are adapted at the boundary. */
export interface SymbolData {
  name: string;
  kind: number;
  range: { start: { line: number; character: number }; end: { line: number; character: number } };
  selectionRange: { start: { line: number; character: number }; end: { line: number; character: number } };
  children: SymbolData[];
}

/**
 * Keeps the server symbol tree as-is: hierarchy, names, and owned ranges.
 * Nothing is re-estimated here; partial results stay partial.
 */
export function mapSymbols(input: unknown): SymbolData[] {
  if (!Array.isArray(input)) {
    return [];
  }
  const mapped: SymbolData[] = [];
  for (const item of input) {
    const symbol = mapOne(item);
    if (symbol) {
      mapped.push(symbol);
    }
  }
  return mapped;
}

function mapOne(item: unknown): SymbolData | null {
  if (typeof item !== 'object' || item === null) {
    return null;
  }
  const record = item as Record<string, unknown>;
  if (typeof record['name'] !== 'string') {
    return null;
  }
  const range = mapRange(record['range']);
  const selectionRange = mapRange(record['selectionRange']) ?? range;
  if (!range) {
    return null;
  }
  return {
    name: record['name'] as string,
    kind: typeof record['kind'] === 'number' ? (record['kind'] as number) : 3,
    range,
    selectionRange: selectionRange ?? range,
    children: mapSymbols(record['children']),
  };
}

function mapRange(value: unknown): SymbolData['range'] | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const record = value as Record<string, { line?: unknown; character?: unknown }>;
  const start = mapPosition(record['start']);
  const end = mapPosition(record['end']);
  return start && end ? { start, end } : null;
}

function mapPosition(value: unknown): { line: number; character: number } | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const record = value as Record<string, unknown>;
  return typeof record['line'] === 'number' && typeof record['character'] === 'number' && record['line'] >= 0 && record['character'] >= 0
    ? { line: record['line'], character: record['character'] }
    : null;
}
