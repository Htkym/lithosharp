import assert from 'node:assert/strict';
import {realpathSync} from 'node:fs';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';

assert.equal(process.versions.node, '24.13.0');
assert.ok(process.env.npm_config_user_agent?.startsWith('npm/11.6.2 '), 'Run with npm 11.6.2');
const require = createRequire(import.meta.url);
const reactPath = realpathSync(require.resolve('react'));
for (const owner of ['react-dom', '@mdx-js/react', '@docusaurus/core/package.json']) {
  assert.equal(realpathSync(createRequire(require.resolve(owner)).resolve('react')), reactPath, `${owner} resolves another React`);
}
const lock = JSON.parse(await readFile(new URL('package-lock.json', import.meta.url), 'utf8'));
for (const name of ['react', 'react-dom']) {
  const copies = Object.keys(lock.packages).filter(path => path.endsWith(`node_modules/${name}`));
  assert.deepEqual(copies, [`node_modules/${name}`], `Multiple ${name} copies`);
  assert.equal(lock.packages[copies[0]].version, '19.2.4');
}
const reference = await readFile(new URL('build/guide/counter/index.html', import.meta.url), 'utf8');
assert.ok(reference.includes('この本文はJavaScript無効時にも読めます。'));
assert.match(reference, /Count: (?:<!-- -->)?3/);
assert.ok(reference.includes('https://example.test/product/guide/counter/'));
console.log('PASS: pinned single React copy and Docusaurus static reference. LithoSharp worker and browser suites cover compilation, bundling and hydration.');
