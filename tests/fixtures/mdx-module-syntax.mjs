import {readFileSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
import {test} from 'node:test';
import assert from 'node:assert/strict';
const read = path => readFileSync(new URL(path, import.meta.url), 'utf8');
const client = read('../../src/LithoSharp.Mdx/Browser/client.mjs');
const bundled = read('../../src/LithoSharp/Browser/code-copy.mjs') + '\n'
  + read('../../src/LithoSharp/Browser/sidebar.mjs') + '\n'
  + read('../../src/LithoSharp/Browser/toc.mjs') + '\n'
  + client.replace('/*__LITHOSHARP_LOCAL_SEARCH__*/', read('../../src/LithoSharp/Search/query.mjs'));
for (const [name, source] of [['production bundle', bundled], ['menu fixture', client]]) {
  test(`${name} remains an ES module with a valid startup call`, () => {
    const result = spawnSync(process.execPath, ['--input-type=module', '--check'], {input: source + '\nstart({basePath:"/"});', encoding:'utf8'});
    assert.equal(result.status, 0, result.stderr);
  });
}
