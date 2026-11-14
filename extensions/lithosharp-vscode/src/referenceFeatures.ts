import {createHash} from 'node:crypto';
import {isAbsolute, normalize} from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import {mapEditorRange, type EditorRange} from './editorFeatures.js';

export interface ReferenceScope { collection: string; version: string; locale: string }
export interface ReferenceScopeChoice extends ReferenceScope { publicPaths: string[] }
export interface ReferenceDocument { uri: string; version: number; text: string }
export interface ReferenceLocation { uri: string; range: EditorRange }
export interface ReferenceReplacement extends ReferenceLocation { newText: string; expectedText: string }
export interface ReferencePlan {
  sourceVersion: number; sourceTextHash: string; canApply: boolean; isComplete: boolean;
  manualScopes: string[]; moves: unknown[]; replacements: ReferenceReplacement[];
}
const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string');
export const textHash = (text: string): string => createHash('sha256').update(text, 'utf8').digest('hex');
export function localMarkdownUri(value: unknown): value is string {
  if (typeof value !== 'string') return false;
  try { const uri = new URL(value); return uri.protocol === 'file:' && !uri.search && !uri.hash && /\.md$/i.test(fileURLToPath(uri)); }
  catch { return false; }
}
function fileIdentity(uri: string): string {
  const key = normalize(fileURLToPath(uri)).normalize('NFC');
  return process.platform === 'win32' ? key.toLowerCase() : key;
}
function documentIndex(documents: ReadonlyMap<string, ReferenceDocument>): Map<string, ReferenceDocument> {
  const result = new Map<string, ReferenceDocument>();
  for (const document of documents.values()) {
    if (!localMarkdownUri(document.uri)) continue;
    const key = fileIdentity(document.uri);
    if (result.has(key)) throw new Error('Multiple editor identities refer to the same source file.');
    result.set(key, document);
  }
  return result;
}
export function mapReferenceScopes(input: unknown, version: number): ReferenceScopeChoice[] {
  if (!record(input) || input['sourceVersion'] !== version || !Array.isArray(input['scopes'])) return [];
  const result: ReferenceScopeChoice[] = [];
  for (const item of input['scopes']) {
    if (!record(item) || !['collection', 'version', 'locale'].every(key => typeof item[key] === 'string' && item[key] !== '') || !strings(item['publicPaths'])) return [];
    result.push({collection: item['collection'] as string, version: item['version'] as string,
      locale: item['locale'] as string, publicPaths: [...item['publicPaths']]});
  }
  return result;
}
export function mapReferenceLocations(input: unknown, documents: ReadonlyMap<string, ReferenceDocument>): ReferenceLocation[] {
  if (!Array.isArray(input)) return [];
  let index: Map<string, ReferenceDocument>;
  try { index = documentIndex(documents); } catch { return []; }
  const result: ReferenceLocation[] = [];
  for (const item of input) {
    if (!record(item) || !localMarkdownUri(item['uri'])) return [];
    const document = index.get(fileIdentity(item['uri']));
    const range = document && mapEditorRange(item['range'], document.text);
    if (!range) return [];
    result.push({uri: document!.uri, range});
  }
  return result;
}
export function mapPrepareRename(input: unknown, text: string): {range: EditorRange; placeholder: string} | undefined {
  if (!record(input) || typeof input['placeholder'] !== 'string') return undefined;
  const range = mapEditorRange(input['range'], text);
  return range ? {range, placeholder: input['placeholder']} : undefined;
}
function positionAt(text: string, offset: number): {line: number; character: number} {
  let line = 0, start = 0;
  for (let index = 0; index < offset; index++) {
    if (text[index] === '\r') {
      if (text[index + 1] === '\n') {
        if (index + 1 === offset) throw new Error('An edit splits a CRLF line ending.');
        index++;
      }
      line++; start = index + 1;
    } else if (text[index] === '\n') { line++; start = index + 1; }
  }
  return {line, character: offset - start};
}
/** Rejects the whole plan when any document, UTF-16 span, or expected text is unavailable. */
export function validateReferencePlan(input: unknown, source: ReferenceDocument,
  documents: ReadonlyMap<string, ReferenceDocument>): ReferencePlan {
  if (!record(input) || input['scope'] !== 'captured-static-markdown' || input['sourceVersion'] !== source.version ||
      input['sourceTextHash'] !== textHash(source.text) || typeof input['canApply'] !== 'boolean' || typeof input['isComplete'] !== 'boolean' ||
      !strings(input['manualScopes']) || !Array.isArray(input['edits']) || !Array.isArray(input['moves']))
    throw new Error('The reference preview is invalid or stale. Acquire a fresh preview.');
  const replacements: ReferenceReplacement[] = [], endings = new Map<string, number>(), starts = new Set<string>();
  const index = documentIndex(documents);
  const sorted = [...input['edits']].sort((a: unknown, b: unknown) => record(a) && record(b) && typeof a['start'] === 'number' && typeof b['start'] === 'number' ? a['start'] - b['start'] : 0);
  for (const item of sorted) {
    if (!record(item) || typeof item['sourcePath'] !== 'string' || !isAbsolute(item['sourcePath']) ||
        !Number.isSafeInteger(item['start']) || !Number.isSafeInteger(item['length']) ||
        typeof item['expectedText'] !== 'string' || typeof item['replacement'] !== 'string') throw new Error('Invalid reference edit.');
    const fileUri = pathToFileURL(item['sourcePath']).href, document = index.get(fileIdentity(fileUri));
    const uri = document?.uri ?? fileUri;
    const start = item['start'] as number, length = item['length'] as number;
    if (!localMarkdownUri(uri) || !document || document.version !== item['version'] || start < 0 || length < 0 ||
        start + length > document.text.length || document.text.slice(start, start + length) !== item['expectedText'] ||
        (endings.get(uri) ?? -1) > start) throw new Error('A reference edit has stale, overlapping, or unsupported source text.');
    // Coincident insertions are rejected rather than depending on edit ordering.
    const identity = `${uri}\0${start}`;
    if (starts.has(identity)) throw new Error('Coincident reference edits are unsupported.');
    starts.add(identity);
    endings.set(uri, start + length);
    replacements.push({uri, range: {start: positionAt(document.text, start), end: positionAt(document.text, start + length)},
      newText: item['replacement'], expectedText: item['expectedText']});
  }
  return {sourceVersion: source.version, sourceTextHash: input['sourceTextHash'] as string,
    canApply: input['canApply'], isComplete: input['isComplete'], manualScopes: [...input['manualScopes']],
    moves: [...input['moves']], replacements};
}
export interface ConfirmedReferenceEdit {uri: string; range: EditorRange; newText: string; metadata?: {label: string; needsConfirmation: boolean; description?: string}}
export function validateReferenceWorkspaceEdit(input: unknown, plan: ReferencePlan,
  documents: ReadonlyMap<string, ReferenceDocument>): ConfirmedReferenceEdit[] {
  if (!record(input) || input['changes'] !== undefined || !Array.isArray(input['documentChanges'])) throw new Error('Versioned text-only documentChanges are required.');
  const result: ConfirmedReferenceEdit[] = [], seen = new Set<string>();
  const index = documentIndex(documents);
  const annotations = input['changeAnnotations'];
  for (const change of input['documentChanges']) {
    if (!record(change) || change['kind'] !== undefined || !record(change['textDocument']) || !Array.isArray(change['edits'])) throw new Error('Resource operations are not accepted.');
    const identity = change['textDocument'];
    if (!localMarkdownUri(identity['uri']) || seen.has(fileIdentity(identity['uri']))) throw new Error('Invalid or repeated edit document.');
    seen.add(fileIdentity(identity['uri']));
    const document = index.get(fileIdentity(identity['uri']));
    if (!document || document.version !== identity['version']) throw new Error('A reference document changed before application.');
    for (const edit of change['edits']) {
      if (!record(edit) || typeof edit['newText'] !== 'string') throw new Error('Invalid text edit.');
      const range = mapEditorRange(edit['range'], document.text);
      if (!range) throw new Error('Invalid text edit range.');
      const value: ConfirmedReferenceEdit = {uri: document.uri, range, newText: edit['newText']};
      if (edit['annotationId'] !== undefined) {
        const annotation = typeof edit['annotationId'] === 'string' && record(annotations) ? annotations[edit['annotationId']] : undefined;
        if (!record(annotation) || typeof annotation['label'] !== 'string' || annotation['needsConfirmation'] !== true || typeof annotation['description'] !== 'string') throw new Error('Missing confirmation annotation.');
        value.metadata = {label: annotation['label'], needsConfirmation: true, description: annotation['description']};
      }
      if (plan.manualScopes.length && !value.metadata) throw new Error('Manual scope requires confirmation annotations.');
      result.push(value);
    }
  }
  const key = (edit: ReferenceLocation & {newText: string}): string => JSON.stringify([edit.uri, edit.range, edit.newText]);
  if (JSON.stringify(result.map(key).sort()) !== JSON.stringify(plan.replacements.map(key).sort())) throw new Error('The returned edits differ from the confirmed preview.');
  return result;
}
