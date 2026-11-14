import { createHash } from 'node:crypto';
import * as path from 'node:path';

export interface SchemaEvidence {
  schemaVersion: string;
  projectId: string;
  projectGeneration: number;
  projectHash: string;
  collection: string;
  schemaJson: string;
  schemaHash: string;
  inputSetHash: string;
  inputPaths: string[];
}

export interface MarkdownComponentEvidence {
  name: string;
  propsTypeName: string;
  schemaJson: string | null;
  fingerprint: string;
  semanticRulesJson?: string;
}

const profiles = new Set(['lithosharp-markdown/1', 'commonmark/0.31.2', 'syntamark-docs/2']);
const hash = (text: string): string => 'sha256:' + createHash('sha256').update(text, 'utf8').digest('hex');
const object = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
const textInput = (inputs: Record<string, unknown>[], key: string): string | undefined => {
  const value = inputs.find(input => input['key'] === key)?.['value'];
  return typeof value === 'string' ? value : undefined;
};

/** Carries the input policy and the bundle reader's relations through the existing build receipt. */
export function acquireInputEvidence(inputs: Record<string, unknown>[], projectRoot: string, source: string,
  contentRoot: string | undefined): Record<string, unknown> | undefined {
  const inputProfile = textInput(inputs, 'content.inputProfile') ?? 'Markdown';
  const inputKind = textInput(inputs, 'content.inputKind') ?? 'Markdown';
  if (!['Markdown', 'OpenKnowledgeFormat'].includes(inputProfile) ||
      !['Markdown', 'Concept', 'Index', 'Log', 'Auxiliary'].includes(inputKind) ||
      (inputProfile === 'Markdown') !== (inputKind === 'Markdown')) return undefined;
  const inputRoot = path.resolve(projectRoot, contentRoot ?? '.').replace(/\\/g, '/');
  const contained = (value: string): string | undefined => {
    if (!value || path.isAbsolute(value) || value.replace(/\\/g, '/').split('/').some(part => !part || part === '.' || part === '..')) return undefined;
    const physical = path.resolve(inputRoot, value);
    const relative = path.relative(inputRoot, physical);
    return relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative)
      ? physical.replace(/\\/g, '/') : undefined;
  };
  if (inputProfile === 'OpenKnowledgeFormat' && !contained(source)) return undefined;
  const relations: Record<string, unknown>[] = [];
  const raw = textInput(inputs, 'content.inputRelations');
  if (raw !== undefined) {
    let parsed: unknown;
    try { parsed = JSON.parse(raw); } catch { return undefined; }
    if (!Array.isArray(parsed) || inputProfile !== 'OpenKnowledgeFormat') return undefined;
    for (const relation of parsed) {
      if (!object(relation) || !['Concept', 'ReservedDocument', 'Auxiliary', 'External', 'Unresolved', 'OutsideBundle'].includes(String(relation['Kind'])) ||
          typeof relation['IsImage'] !== 'boolean' || relation['Target'] !== null && typeof relation['Target'] !== 'string') return undefined;
      const target = relation['TargetSourcePath'];
      const targetSourcePath = target === null ? null : typeof target === 'string' ? contained(target) : undefined;
      if (targetSourcePath === undefined) return undefined;
      relations.push({ target: relation['Target'], targetSourcePath, kind: relation['Kind'], isImage: relation['IsImage'] });
    }
  }
  return { inputProfile, inputKind, inputRoot, inputPath: source, relations };
}

/** Keeps type metadata for an existing text input while discarding content-dependent catalog claims. */
export function retainStaticEvidenceForTextChange(snapshot: unknown, inputPath: string,
  knownInputs: readonly string[]): Record<string, unknown> | undefined {
  if (!object(snapshot) || !/\.(?:md|mdx)$/i.test(inputPath)) return undefined;
  const identity = (value: string): string => {
    const normalized = path.resolve(value).replace(/\\/g, '/').normalize('NFC');
    return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
  };
  if (!knownInputs.some(input => identity(input) === identity(inputPath))) return undefined;
  if (Array.isArray(snapshot['routes']) && snapshot['routes'].length === 0 && snapshot['semanticSnapshot'] === null &&
      object(snapshot['semanticRules']) && Object.keys(snapshot['semanticRules']).length === 0) return snapshot;
  return { ...snapshot, routes: [], semanticSnapshot: null, semanticRules: {} };
}

/** Uses documentation extension receipts from the actual CLI response envelope. */
export function acquireSemanticEvidence(extensions: unknown): {
  semanticSnapshot: Record<string, unknown>; semanticRules: Record<string, unknown>;
} | undefined {
  if (!Array.isArray(extensions)) return undefined;
  const sources = extensions.filter(object).filter(value => value['kind'] === 'documentation' && object(value['semanticSnapshot']));
  if (!sources.length) return undefined;
  const documents = new Map<string, Record<string, unknown>>();
  const routes = new Map<string, Record<string, unknown>>();
  const rules = new Map<string, unknown>();
  const scopes = new Map<string, unknown>();
  let complete = true;
  let global = false;
  const key = (item: Record<string, unknown>): string | undefined => {
    const identity = item['key'];
    if (!object(identity)) return undefined;
    const parts = ['Collection', 'Version', 'Locale', 'Id'].map(name => identity[name]);
    return parts.every(value => typeof value === 'string' && value.length > 0) ? JSON.stringify(parts) : undefined;
  };
  for (const source of sources) {
    const snapshot = source['semanticSnapshot'];
    const ruleSets = source['semanticRules'];
    if (!object(snapshot) || snapshot['schemaVersion'] !== '1.0' || typeof snapshot['isComplete'] !== 'boolean' ||
        !Array.isArray(snapshot['documents']) || !Array.isArray(snapshot['routes']) || !object(ruleSets)) return undefined;
    complete &&= snapshot['isComplete'];
    const claimedScopes = snapshot['completeScopes'];
    if (claimedScopes === null) global = true;
    else if (Array.isArray(claimedScopes)) {
      for (const scope of claimedScopes) {
        if (!object(scope) || !['Collection', 'Version', 'Locale'].every(name => typeof scope[name] === 'string')) return undefined;
        scopes.set(JSON.stringify([scope['Collection'], scope['Version'], scope['Locale']]), scope);
      }
    } else return undefined;
    for (const [items, target] of [[snapshot['documents'], documents], [snapshot['routes'], routes]] as const) {
      for (const item of items) {
        if (!object(item)) return undefined;
        const identity = key(item);
        if (!identity) return undefined;
        const existing = target.get(identity);
        if (existing && JSON.stringify(existing) !== JSON.stringify(item)) return undefined;
        target.set(identity, item);
      }
    }
    for (const [collection, ruleSet] of Object.entries(ruleSets)) {
      if (!object(ruleSet)) return undefined;
      if (rules.has(collection) && JSON.stringify(rules.get(collection)) !== JSON.stringify(ruleSet)) return undefined;
      rules.set(collection, ruleSet);
    }
  }
  return {
    semanticSnapshot: { schemaVersion: '1.0', isComplete: complete && (!global || sources.length === 1),
      completeScopes: global && sources.length === 1 ? null : [...scopes.values()],
      documents: [...documents.values()], routes: [...routes.values()] },
    semanticRules: Object.fromEntries(rules),
  };
}

function supportedSchema(value: unknown): value is string {
  if (typeof value !== 'string' || value.length > 4 * 1024 * 1024) return false;
  try {
    const root: unknown = JSON.parse(value);
    return object(root) && root['x-lithosharp-schemaVersion'] === '1.0' &&
      typeof root['$ref'] === 'string' && object(root['$defs']);
  } catch { return false; }
}

/** Acquires opaque generated schemas and exact membership without loading project code. */
export function acquireProjectEvidence(buildPlan: unknown[], projectRoot: string, projectId: string, generation: number): {
  projectHash: string; schemas: SchemaEvidence[]; markdownProfileId: string; markdownComponents: MarkdownComponentEvidence[];
} | undefined {
  const projectHash = hash(JSON.stringify(buildPlan));
  const selectedProfiles = new Set<string>();
  const componentSets = new Map<string, MarkdownComponentEvidence[]>();
  const schemas = new Map<string, { collection: string; schemaJson: string; inputs: Set<string> }>();
  for (const node of buildPlan) {
    if (!object(node) || !Array.isArray(node['inputs'])) continue;
    const inputs = node['inputs'].filter(object);
    for (const input of inputs.filter(item => item['key'] === 'markdown.profile')) {
      if (typeof input['value'] !== 'string' || !profiles.has(input['value'])) return undefined;
      selectedProfiles.add(input['value']);
    }
    for (const input of inputs.filter(item => item['key'] === 'markdown.components')) {
      if (typeof input['value'] !== 'string') return undefined;
      let raw: unknown;
      try { raw = JSON.parse(input['value']); } catch { return undefined; }
      if (!Array.isArray(raw)) return undefined;
      const components: MarkdownComponentEvidence[] = [];
      const names = new Set<string>();
      for (const item of raw) {
        if (!object(item)) return undefined;
        const name = item['name'] ?? item['Name'];
        const propsTypeName = item['propsTypeName'] ?? item['PropsTypeName'];
        const schemaJson = item['schemaJson'] ?? item['SchemaJson'];
        const fingerprint = item['fingerprint'] ?? item['Fingerprint'];
        const semanticRulesJson = item['semanticRulesJson'] ?? item['SemanticRulesJson'];
        if (typeof name !== 'string' || !name.trim() || names.has(name) || typeof propsTypeName !== 'string' ||
            !propsTypeName.trim() || typeof fingerprint !== 'string' || !/^[0-9a-f]{64}$/.test(fingerprint)) return undefined;
        names.add(name);
        if (semanticRulesJson !== undefined && semanticRulesJson !== null) {
          if (typeof semanticRulesJson !== 'string' || semanticRulesJson.length > 4 * 1024 * 1024) return undefined;
          let rules: unknown;
          try { rules = JSON.parse(semanticRulesJson); } catch { return undefined; }
          if (!object(rules) || rules['schemaVersion'] !== '1.0' || !Array.isArray(rules['rules'])) return undefined;
        }
        components.push({ name, propsTypeName, schemaJson: supportedSchema(schemaJson) ? schemaJson : null, fingerprint,
          ...(typeof semanticRulesJson === 'string' ? { semanticRulesJson } : {}) });
      }
      components.sort((left, right) => left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
      componentSets.set(JSON.stringify(components), components);
    }
    const schemaJson = textInput(inputs, 'content.schemaJson');
    const membership = textInput(inputs, 'content.schemaInputs');
    const root = textInput(inputs, 'content.inputRoot');
    const collection = inputs.find(item => item['kind'] === 'Collection')?.['key'];
    if (!supportedSchema(schemaJson) || !membership || !root || typeof collection !== 'string' || !collection.trim()) continue;
    let paths: unknown;
    try { paths = JSON.parse(membership); } catch { continue; }
    if (!Array.isArray(paths) || paths.length === 0 || paths.some(value => typeof value !== 'string' ||
        !value || path.isAbsolute(value) || value.replace(/\\/g, '/').split('/').some(part => !part || part === '.' || part === '..'))) continue;
    const inputPaths = (paths as string[]).map(value => path.resolve(projectRoot, root, value).replace(/\\/g, '/').normalize('NFC'));
    const identities = inputPaths.map(value => value.toLowerCase());
    if (new Set(identities).size !== identities.length) continue;
    const key = collection + '\0' + hash(schemaJson);
    const entry = schemas.get(key) ?? { collection, schemaJson, inputs: new Set<string>() };
    for (const input of inputPaths) entry.inputs.add(input);
    schemas.set(key, entry);
  }
  if (selectedProfiles.size > 1 || componentSets.size > 1) return undefined;
  const markdownProfileId = [...selectedProfiles][0] ?? 'lithosharp-markdown/1';
  const markdownComponents = [...componentSets.values()][0] ?? [];
  if (markdownComponents.length && markdownProfileId !== 'syntamark-docs/2') return undefined;
  const snapshots = [...schemas.values()].map(entry => {
    const inputPaths = [...entry.inputs].sort();
    return { schemaVersion: '1.0', projectId, projectGeneration: generation, projectHash,
      collection: entry.collection, schemaJson: entry.schemaJson, schemaHash: hash(entry.schemaJson),
      inputSetHash: hash(inputPaths.join('\n')), inputPaths };
  });
  return { projectHash, schemas: snapshots, markdownProfileId, markdownComponents };
}
