import assert from 'node:assert/strict';
import test from 'node:test';
import * as path from 'node:path';
import { createHash } from 'node:crypto';
import { acquireInputEvidence, acquireProjectEvidence, acquireSemanticEvidence, retainStaticEvidenceForTextChange } from '../../src/projectEvidence.js';

const schemaJson = JSON.stringify({ 'x-lithosharp-schemaVersion': '1.0', $ref: '#/$defs/Article',
  $defs: { Article: { type: 'object', properties: { title: { type: 'string' } } } } });
const input = (key: string, value: string, kind = 'Configuration') => ({ key, value, kind });
const root = path.resolve('project');
const hash = (text: string) => 'sha256:' + createHash('sha256').update(text, 'utf8').digest('hex');
const node = (extra: ReturnType<typeof input>[] = []) => ({ inputs: [input('guide', '', 'Collection'),
  input('content.sourcePath', 'intro.md'), input('content.inputRoot', 'docs'), input('content.schemaJson', schemaJson),
  input('content.schemaInputs', JSON.stringify(['intro.md', 'cafe\u0301.md'])), ...extra] });

test('schema acquisition preserves the generated contract and exact NFC input-set provenance', () => {
  const plan = [node(), node()];
  const evidence = acquireProjectEvidence(plan, root, 'site', 4)!;
  assert.equal(evidence.projectHash, hash(JSON.stringify(plan)));
  assert.equal(evidence.schemas.length, 1);
  const schema = evidence.schemas[0]!;
  assert.equal(schema.schemaJson, schemaJson);
  assert.equal(schema.schemaHash, hash(schemaJson));
  assert.deepEqual(schema.inputPaths, ['caf\u00e9.md', 'intro.md'].map(value => path.resolve(root, 'docs', value).replace(/\\/g, '/')));
  assert.equal(schema.inputSetHash, hash(schema.inputPaths.join('\n')));
  assert.equal(schema.projectHash, evidence.projectHash);
  assert.equal(schema.projectGeneration, 4);
  const withoutMembership = node();
  withoutMembership.inputs = withoutMembership.inputs.filter(item => item.key !== 'content.schemaInputs');
  assert.deepEqual(acquireProjectEvidence([withoutMembership], root, 'site', 4)!.schemas, []);
  for (const membership of [['../outside.md'], ['intro.md', 'intro.md'], ['intro.md', 'INTRO.md'], ['caf\u00e9.md', 'cafe\u0301.md']]) {
    const invalid = node();
    invalid.inputs.find(item => item.key === 'content.schemaInputs')!.value = JSON.stringify(membership);
    assert.deepEqual(acquireProjectEvidence([invalid], root, 'site', 4)!.schemas, []);
  }
});

test('profile and component acquisition accepts one explicit contract and rejects mixed context', () => {
  assert.equal(acquireProjectEvidence([node()], root, 'site', 1)!.markdownProfileId, 'lithosharp-markdown/1');
  const component = { Name: 'badge', PropsTypeName: 'BadgeProps', SchemaJson: schemaJson, Fingerprint: 'a'.repeat(64) };
  const configured = node([input('markdown.profile', 'syntamark-docs/2'), input('markdown.components', JSON.stringify([component]))]);
  const evidence = acquireProjectEvidence([configured], root, 'site', 1)!;
  assert.deepEqual(evidence.markdownComponents, [{ name: 'badge', propsTypeName: 'BadgeProps', schemaJson, fingerprint: 'a'.repeat(64) }]);
  assert.equal(acquireProjectEvidence([configured, node([input('markdown.profile', 'commonmark/0.31.2')])], root, 'site', 1), undefined);
  assert.equal(acquireProjectEvidence([node([input('markdown.profile', 'unknown')])], root, 'site', 1), undefined);
  assert.equal(acquireProjectEvidence([node([input('markdown.components', JSON.stringify([component]))])], root, 'site', 1), undefined);
  assert.equal(acquireProjectEvidence([node([input('markdown.profile', 'syntamark-docs/2'),
    input('markdown.components', JSON.stringify([component, component]))])], root, 'site', 1), undefined);
  const semanticRulesJson = JSON.stringify({ schemaVersion: '1.0', rules: [{ kind: 'Reference', field: 'target' }] });
  const withRules = node([input('markdown.profile', 'syntamark-docs/2'),
    input('markdown.components', JSON.stringify([{ ...component, SemanticRulesJson: semanticRulesJson }]))]);
  assert.equal(acquireProjectEvidence([withRules], root, 'site', 1)!.markdownComponents[0]!.semanticRulesJson, semanticRulesJson);
  const unsupportedRules = node([input('markdown.profile', 'syntamark-docs/2'),
    input('markdown.components', JSON.stringify([{ ...component, SemanticRulesJson: '{"schemaVersion":"future","rules":[]}' }]))]);
  assert.equal(acquireProjectEvidence([unsupportedRules], root, 'site', 1), undefined);
  const unsupported = node();
  unsupported.inputs.find(item => item.key === 'content.schemaJson')!.value = '{"x-lithosharp-schemaVersion":"future"}';
  assert.deepEqual(acquireProjectEvidence([unsupported], root, 'site', 1)!.schemas, []);
});

test('typing in a known Markdown input retains schema metadata and drops stale routing and semantics', () => {
  const evidence = acquireProjectEvidence([node()], root, 'site', 4)!;
  const input = path.resolve(root, 'docs', 'intro.md');
  const inputMetadata = [{ sourcePath: input, inputProfile: 'OpenKnowledgeFormat', inputKind: 'Index', inputPath: 'intro.md' }];
  const original = { ...evidence, projectGeneration: 4, inputMetadata,
    routes: [{ sourcePath: input, publicPath: '/old/' }], semanticSnapshot: { isComplete: true },
    semanticRules: { guide: { rules: [] } } };
  const retained = retainStaticEvidenceForTextChange(original, input, [input])!;
  assert.equal(retained['schemas'], original.schemas);
  assert.equal(retained['projectHash'], original.projectHash);
  assert.equal(retained['markdownProfileId'], original.markdownProfileId);
  assert.equal(retained['inputMetadata'], inputMetadata);
  assert.deepEqual(retained['routes'], []);
  assert.equal(retained['semanticSnapshot'], null);
  assert.deepEqual(retained['semanticRules'], {});
  assert.equal(original.routes.length, 1, 'The acquired snapshot is immutable.');
  assert.equal(retainStaticEvidenceForTextChange(retained, input, [input]), retained,
    'Further keystrokes must not reset the unchanged static context or its parser session.');
  assert.equal(retainStaticEvidenceForTextChange(original, path.resolve(root, 'Front.cs'), [input]), undefined);
  assert.equal(retainStaticEvidenceForTextChange(original, path.resolve(root, 'docs', 'new.md'), [input]), undefined);
});

test('OKF input receipts retain native relation kinds and contained physical identities', () => {
  const inputs = [input('content.inputProfile', 'OpenKnowledgeFormat'), input('content.inputKind', 'Concept'),
    input('content.inputRelations', JSON.stringify([
      { Target: 'index.md', TargetSourcePath: 'index.md', Kind: 'ReservedDocument', IsImage: false },
      { Target: 'data.bin', TargetSourcePath: 'data.bin', Kind: 'Auxiliary', IsImage: true },
      { Target: '../outside.md', TargetSourcePath: null, Kind: 'OutsideBundle', IsImage: false },
    ]))];
  const evidence = acquireInputEvidence(inputs, root, 'guide.md', 'bundle')!;
  assert.equal(evidence['inputProfile'], 'OpenKnowledgeFormat');
  assert.equal(evidence['inputKind'], 'Concept');
  assert.equal(evidence['inputRoot'], path.resolve(root, 'bundle').replace(/\\/g, '/'));
  assert.equal(evidence['inputPath'], 'guide.md');
  assert.deepEqual(evidence['relations'], [
    { target: 'index.md', targetSourcePath: path.resolve(root, 'bundle/index.md').replace(/\\/g, '/'), kind: 'ReservedDocument', isImage: false },
    { target: 'data.bin', targetSourcePath: path.resolve(root, 'bundle/data.bin').replace(/\\/g, '/'), kind: 'Auxiliary', isImage: true },
    { target: '../outside.md', targetSourcePath: null, kind: 'OutsideBundle', isImage: false },
  ]);
  assert.equal(acquireInputEvidence(inputs, root, '../outside.md', 'bundle'), undefined);
  assert.equal(acquireInputEvidence([input('content.inputProfile', 'future')], root, 'guide.md', 'bundle'), undefined);
  assert.equal(acquireInputEvidence([input('content.inputKind', 'Concept')], root, 'guide.md', 'bundle'), undefined);
  const altered = [...inputs.slice(0, 2), input('content.inputRelations', JSON.stringify([
    { Target: 'unsafe', TargetSourcePath: '../outside.md', Kind: 'Concept', IsImage: false },
  ]))];
  assert.equal(acquireInputEvidence(altered, root, 'guide.md', 'bundle'), undefined);
  assert.notEqual(acquireProjectEvidence([{ inputs }], root, 'site', 1)!.projectHash,
    acquireProjectEvidence([{ inputs: altered }], root, 'site', 1)!.projectHash);
});

test('semantic acquisition consumes real extension receipts and preserves scoped absence evidence', () => {
  const document = (id: string) => ({ key: { Collection: 'guide', Version: 'v1', Locale: 'en', Id: id },
    sourcePath: id + '.md', values: {}, publication: 'Unpublished', isComplete: true, locations: {} });
  const receipt = (id: string) => ({ kind: 'documentation', semanticSnapshot: { schemaVersion: '1.0', isComplete: true,
    completeScopes: [{ Collection: 'guide', Version: 'v1', Locale: 'en' }], documents: [document(id)], routes: [] },
    semanticRules: { guide: { schemaVersion: '1.0', rules: [] } } });
  const acquired = acquireSemanticEvidence([receipt('intro'), receipt('topic')])!;
  assert.equal(acquired.semanticSnapshot['isComplete'], true);
  assert.equal((acquired.semanticSnapshot['documents'] as unknown[]).length, 2);
  assert.equal((acquired.semanticSnapshot['completeScopes'] as unknown[]).length, 1);
  assert.deepEqual(acquired.semanticRules, receipt('intro').semanticRules);
  const partial = receipt('topic');
  partial.semanticSnapshot.isComplete = false;
  assert.equal(acquireSemanticEvidence([receipt('intro'), partial])!.semanticSnapshot['isComplete'], false);
  const conflicting = receipt('intro');
  conflicting.semanticSnapshot.documents[0]!.sourcePath = 'other.md';
  assert.equal(acquireSemanticEvidence([receipt('intro'), conflicting]), undefined);
  assert.equal(acquireSemanticEvidence([{ kind: 'mdx' }, { kind: 'documentation', semanticSnapshot: null }]), undefined);
});
