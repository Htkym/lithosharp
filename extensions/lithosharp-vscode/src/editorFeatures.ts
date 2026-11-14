export interface EditorPosition { line: number; character: number }
export interface EditorRange { start: EditorPosition; end: EditorPosition }
export interface CompletionData {
  label: string;
  kind?: number;
  detail?: string;
  documentation?: string;
  textEdit?: { range: EditorRange; newText: string };
}
export interface HoverData { text: string; range?: EditorRange }

const object = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
export function isEditorPosition(value: unknown): value is EditorPosition {
  return object(value) && Number.isSafeInteger(value['line']) && Number.isSafeInteger(value['character']) &&
    (value['line'] as number) >= 0 && (value['character'] as number) >= 0;
}
function range(value: unknown, text: string): EditorRange | undefined {
  if (!object(value) || !isEditorPosition(value['start']) || !isEditorPosition(value['end'])) return undefined;
  const start = value['start']; const end = value['end'];
  const lines = text.split(/\r\n|\r|\n/);
  if (start.line > end.line || (start.line === end.line && start.character > end.character) ||
      start.line >= lines.length || end.line >= lines.length ||
      start.character > lines[start.line]!.length || end.character > lines[end.line]!.length) return undefined;
  return { start: { ...start }, end: { ...end } };
}
export const mapEditorRange = range;
function description(value: unknown): string | undefined {
  return typeof value === 'string' ? value : object(value) && typeof value['value'] === 'string' ? value['value'] : undefined;
}

/** Converts only plain text and owned UTF-16 ranges; server markup is never trusted. */
export function mapCompletions(input: unknown, text: string): CompletionData[] {
  const items = Array.isArray(input) ? input : object(input) && Array.isArray(input['items']) ? input['items'] : [];
  const result: CompletionData[] = [];
  for (const value of items) {
    if (!object(value) || typeof value['label'] !== 'string') continue;
    const item: CompletionData = { label: value['label'] };
    if (Number.isInteger(value['kind']) && (value['kind'] as number) >= 1 && (value['kind'] as number) <= 25)
      item.kind = value['kind'] as number;
    if (typeof value['detail'] === 'string') item.detail = value['detail'];
    const documentation = description(value['documentation']);
    if (documentation !== undefined) item.documentation = documentation;
    if (value['textEdit'] !== undefined) {
      const edit = value['textEdit'];
      if (!object(edit) || typeof edit['newText'] !== 'string') continue;
      const editRange = range(edit['range'], text);
      if (!editRange) continue;
      item.textEdit = { range: editRange, newText: edit['newText'] };
    }
    result.push(item);
  }
  return result;
}

export function mapHover(input: unknown, text: string): HoverData | undefined {
  if (!object(input)) return undefined;
  const contents = description(input['contents']);
  if (contents === undefined) return undefined;
  const result: HoverData = { text: contents };
  if (input['range'] !== undefined && input['range'] !== null) {
    const ownedRange = range(input['range'], text);
    if (!ownedRange) return undefined;
    result.range = ownedRange;
  }
  return result;
}
