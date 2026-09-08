import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, rm} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {compileSite, extractRegion} from '../compiler.mjs';

test('official MDX produces server HTML and shared browser assets without unused exports', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-mdx-check-'));
  try {
    const projectRoot = path.join(root, 'input');
    const workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    const source = `import {useState} from 'react';
export const serverSecret='CANARY_PRIVATE_EXPORT';
export function Counter(){const [n,set]=useState(3);return <button onClick={()=>set(n+1)}>Count: {n}</button>}

# Official MDX

<Counter />

{frontMatter.title}
`;
    await writeFile(path.join(projectRoot, 'counter.mdx'), source);
    const result = await compileSite({projectRoot, workRoot, assetBaseUrl: '/product/_mdx', basePath: '/product/',
      timestamp: '2026-01-01T00:00:00Z', sources: {'counter.mdx': source},
      pages: [{id: 'counter', source: 'counter.mdx', url: '/product/counter/', locale: 'en', title: 'Counter', props: {frontMatter: {title: 'Public title'}}}]});
    assert.match(result.pages[0].html, /Count: (?:<!-- -->)?3/);
    assert.match(result.pages[0].html, /id="official-mdx"/);
    assert.match(result.pages[0].html, /Public title/);
    assert.equal(result.pages[0].headings[0].id, 'official-mdx');
    assert.ok(result.assets.some(asset => asset.path === result.pages[0].entry));
    assert.ok(result.assets.some(asset => asset.path.endsWith('.css')));
    for (const asset of result.assets) {
      assert.ok(!Buffer.from(asset.bytes, 'base64').includes(Buffer.from('CANARY_PRIVATE_EXPORT')));
      assert.ok(!Buffer.from(asset.bytes, 'base64').includes(Buffer.from(projectRoot)));
    }
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('code regions preserve nesting and fail for missing closing directives', () => {
  assert.equal(extractRegion('#region demo\none\n#region inner\ntwo\n#endregion\nthree\n#endregion', 'demo'), 'one\n#region inner\ntwo\n#endregion\nthree');
  assert.throws(() => extractRegion('#region demo\nunclosed', 'demo'));
});

test('selective rendering emits no static-page entry and shares explicit island modules', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-island-check-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    await writeFile(path.join(projectRoot, 'Counter.jsx'), `import {useId,useState} from 'react';import styles from './counter.module.css';export default function Counter({initial=0}){const id=useId();const [n,set]=useState(initial);return <button id={id} className={styles.counter} onClick={()=>set(n+1)}>Count {n}</button>}`);
    await writeFile(path.join(projectRoot, 'counter.module.css'), '.counter{color:navy}');
    const sources = {
      'static.mdx': '# Static\n\nNo React entry is needed.',
      'islands.mdx': `import Counter from './Counter.jsx'\n\n# Islands\n\n` + ['load', 'idle', 'visible', 'media', 'manual'].map(strategy =>
        `<Island component={Counter} props={{initial: 3}} schema={{type:'object',properties:{initial:{type:'integer'}},additionalProperties:false}} strategy="${strategy}" ${strategy === 'media' ? 'media="(min-width: 800px)"' : ''} />`).join('\n\n'),
      'fallback.mdx': `import Counter from './Counter.jsx'\n\n# Whole page\n\n<Counter />`
    };
    for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
    const result = await compileSite({projectRoot, workRoot, assetBaseUrl: '/_mdx', basePath: '/', sources, hydration: 'selective',
      pages: Object.keys(sources).map((source, index) => ({id: 'page' + index, source, url: '/' + index + '/', title: source, locale: 'en', props: {}}))});
    assert.equal(result.pages[0].entry, null);
    assert.equal(result.pages[0].hydration, 'static');
    assert.equal(result.pages[1].hydration, 'selective');
    assert.equal(result.pages[1].islands.length, 5);
    assert.equal(new Set(result.pages[1].islands.map(island => island.id)).size, 5);
    assert.match(result.pages[1].html, /Count (?:<!-- -->)?3/);
    assert.match(result.pages[1].html, /counter_counter/);
    assert.equal(result.pages[2].hydration, 'page');
    assert.match(result.pages[2].fallback, /Counter/);
    assert.ok(result.assets.some(asset => asset.path.startsWith('chunks/')));
    const bootstrap = Buffer.from(result.assets.find(asset => asset.path === result.pages[1].entry).bytes, 'base64').toString();
    assert.ok(!bootstrap.includes('react-dom.production'));
  } finally { await rm(root, {recursive: true, force: true}); }
});
