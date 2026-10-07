import assert from 'node:assert/strict';
import test from 'node:test';
import { requireTrusted, UntrustedWorkspaceError } from '../../src/trust.js';

test('untrusted workspaces explain instead of executing', () => {
  assert.throws(() => requireTrusted(false, 'build the site'), UntrustedWorkspaceError);
  try {
    requireTrusted(false, 'build the site');
    assert.fail('must throw');
  } catch (error) {
    assert.match(String((error as Error).message), /trust the workspace/i);
  }
});

test('trusted workspaces pass the gate', () => {
  requireTrusted(true, 'build the site');
});
