import assert from 'node:assert/strict';
import test from 'node:test';
import { preflightModes, serverCapabilityText } from '../../src/tooling.js';

test('old Core capabilities retain existing features without inventing preflight from a version', () => {
  const report = { success: true, schemaVersion: '1.0', core: { version: '1.1.0' }, capabilities: [
    { name: 'document-inspection', schemaVersion: '1.0', maturity: 'Stable' },
    { name: 'future-preflight', schemaVersion: '1.0', maturity: 'Stable' },
  ] };
  assert.deepEqual(preflightModes(report), []);
  const candidate = { ...report, capabilities: [...report.capabilities,
    { name: 'preflight-static-inputs', schemaVersion: '1.0', maturity: 'Preview' },
    { name: 'preflight-trusted-catalog', schemaVersion: '1.0', maturity: 'Preview' }] };
  assert.deepEqual(preflightModes(candidate), ['static', 'trusted']);
  assert.deepEqual(preflightModes({ ...candidate, success: false }), []);
  assert.deepEqual(preflightModes({ ...candidate, schemaVersion: '2.0' }), []);
  assert.deepEqual(preflightModes({ ...candidate, capabilities: [{ name: 'preflight-static-inputs', schemaVersion: '2.0', maturity: 'Preview' }] }), []);
  assert.match(serverCapabilityText({ capabilities: {} }), /unreported/);
  assert.match(serverCapabilityText({ capabilities: { experimental: { lithosharp: { schemaVersion: '1.0', coreVersion: '2.0.0', markdownInspection: true } } } }), /project Core is not acquired/);
});
