import assert from 'node:assert/strict';
import {realpathSync} from 'node:fs';
import {readFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {compile, run} from '@mdx-js/mdx';
import * as runtime from 'react/jsx-runtime';
import {createElement} from 'react';
import {renderToString} from 'react-dom/server';
import {build} from 'esbuild';

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
const source = await readFile(new URL('docs/counter.mdx', import.meta.url), 'utf8');
// This fixture has a fixed metadata header. Production YAML binding belongs to C#.
assert.ok(source.startsWith('---\n'));
const body = source.slice(source.indexOf('\n---\n') + 5);
const compiled = await compile(body, {outputFormat: 'function-body'});
const module = await run(compiled, {...runtime, baseUrl: import.meta.url});
assert.equal(typeof module.Counter, 'function');
const html = renderToString(createElement(module.default));
assert.match(html, /<h1>MDX integration fixture<\/h1>/);
assert.ok(html.includes('この本文はJavaScript無効時にも読めます。'));
assert.match(html, /Count: (?:<!-- -->)?3/);
const browser = await build({
  stdin: {
    contents: String(await compile(body)) + "\nimport {hydrateRoot} from 'react-dom/client'; export {hydrateRoot};",
    resolveDir: process.cwd()
  },
  bundle: true, write: false, platform: 'browser', format: 'esm', metafile: true
});
assert.ok(browser.outputFiles[0].contents.length > 0);
assert.ok(Object.keys(browser.metafile.inputs).some(path => path.includes('react-dom')));
const reference = await readFile(new URL('build/guide/counter/index.html', import.meta.url), 'utf8');
assert.ok(reference.includes('この本文はJavaScript無効時にも読めます。'));
assert.match(reference, /Count: (?:<!-- -->)?3/);
assert.ok(reference.includes('https://example.test/product/guide/counter/'));
console.log('PASS: single React copy, official MDX exports/SSR, browser bundling, Docusaurus static reference. Browser hydration is not tested here.');
