import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, rm, cp} from 'node:fs/promises';
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

test('TSX, npm components, CSS, images, partial MDX and compiler plugins retain their dependencies', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-language-check-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    const sources = {'index.mdx': `import Widget from './Widget.tsx';\nimport Partial from './_Partial.mdx';\nimport BrowserOnly from '@docusaurus/BrowserOnly';\n\n# Features\n\n<Widget value={4} />\n\n<Partial label="imported" />\n\n<BrowserOnly fallback={<p>Browser fallback</p>}>{()=> <p>{window.location.href}</p>}</BrowserOnly>\n\n[Type](xref:T:Example)`,
      '_Partial.mdx': `# Partial\n\n{props.label}\n`};
    for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
    await writeFile(path.join(projectRoot, 'Widget.tsx'), `import React from 'react';import Box from 'fixture';import icon from './icon.svg';import './style.css';export default function Widget({value}:{value:number}){return <Box><img src={icon} alt="test icon"/><strong>{value+1}</strong></Box>}`);
    await writeFile(path.join(projectRoot, 'icon.svg'), '<svg xmlns="http://www.w3.org/2000/svg" width="8" height="8"><path d="M0 0h8v8H0z"/></svg>');
    await writeFile(path.join(projectRoot, 'style.css'), 'strong{color:navy}');
    await mkdir(path.join(projectRoot, 'node_modules/fixture'), {recursive: true});
    await writeFile(path.join(projectRoot, 'node_modules/fixture/package.json'), JSON.stringify({name: 'fixture', version: '1.0.0', main: 'index.js', license: 'MIT'}));
    await writeFile(path.join(projectRoot, 'node_modules/fixture/index.js'), `import React from 'react';export default function Box({children}){return React.createElement('section',{className:'npm-box'},children)}`);
    await writeFile(path.join(projectRoot, 'plugin.mjs'), `import {label} from './label.mjs';export default function plugin(){return tree=>{tree.children.unshift({type:'paragraph',children:[{type:'text',value:label}]})}}`);
    await writeFile(path.join(projectRoot, 'label.mjs'), `export const label='plugin-one'`);
    const request = {projectRoot, workRoot, assetBaseUrl: '/product/_mdx', basePath: '/product/', sources, cacheable: true, crossReferences: {'T:Example':'/product/api/example/'},
      plugins: [{stage: 'remark', module: './plugin.mjs', options: {}}], pages: [{id: 'page', source: 'index.mdx', url: '/product/features/', title: 'Features', locale: 'en', props: {}}]};
    let result = await compileSite(request);
    assert.match(result.pages[0].html, /npm-box/); assert.match(result.pages[0].html, /imported/); assert.match(result.pages[0].html, /Browser fallback/);
    assert.match(result.pages[0].html, /src="\/product\/_mdx\/assets\/icon-/);
    assert.match(result.pages[0].html, /href="\/product\/api\/example\/"/);
    assert.match(result.pages[0].html, /plugin-one/);
    assert.ok(result.inputs.some(input => input.file.endsWith('label.mjs')));
    assert.ok(result.assets.some(asset => asset.path.endsWith('.svg')));
    await writeFile(path.join(projectRoot, 'label.mjs'), `export const label='plugin-two'`);
    result = await compileSite({...request, workRoot: await mkdtemp(path.join(root, 'next-'))});
    assert.match(result.pages[0].html, /plugin-two/);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('selective rendering emits no static-page entry and shares explicit island modules', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-island-check-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    await writeFile(path.join(projectRoot, 'Counter.jsx'), `import {useId,useState} from 'react';import styles from './counter.module.css';export default function Counter({initial=0}){const id=useId();const [n,set]=useState(initial);return <button id={id} className={styles.counter} onClick={()=>set(n+1)}>Count {n}</button>}`);
    await writeFile(path.join(projectRoot, 'counter.module.css'), '.counter{color:navy}');
    await writeFile(path.join(projectRoot, 'static.css'), '.static{color:teal}');
    const sources = {
      'static.mdx': "import './static.css'\n\n# Static\n\nNo React entry is needed.",
      'islands.mdx': `import Counter from './Counter.jsx'\n\n# Islands\n\n` + ['load', 'idle', 'visible', 'media', 'manual'].map(strategy =>
        `<Island component={Counter} props={{initial: 3}} schema={{type:'object',properties:{initial:{type:'integer'}},additionalProperties:false}} strategy="${strategy}" ${strategy === 'media' ? 'media="(min-width: 800px)"' : ''} />`).join('\n\n'),
      'fallback.mdx': `import Counter from './Counter.jsx'\n\n# Whole page\n\n<Counter />`
    };
    for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
    const request = {projectRoot, workRoot, assetBaseUrl: '/_mdx', basePath: '/', sources, hydration: 'selective',
      linkMap: {'static.mdx':'/unlisted-route-canary/', 'islands.mdx':'/1/', 'fallback.mdx':'/2/'},
      pages: Object.keys(sources).map((source, index) => ({id: 'page' + index, source, url: '/' + index + '/', title: source, locale: 'en', props: {}, discoverable: index !== 0}))};
    const result = await compileSite(request);
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
    assert.ok(result.assets.filter(asset => asset.path.endsWith('.js')).every(asset => !Buffer.from(asset.bytes, 'base64').toString().includes('/unlisted-route-canary/')));
    await mkdir(path.join(root, 'repeated'));
    await cp(projectRoot, path.join(root, 'copied-input'), {recursive: true});
    const repeated = await compileSite({...request, projectRoot: path.join(root, 'copied-input'), workRoot: path.join(root, 'repeated'), pages: [...request.pages].reverse()});
    for (const page of result.pages.filter(page => page.hydration !== 'page'))
      assert.deepEqual(repeated.pages.find(candidate => candidate.id === page.id).css, page.css);
  } finally { await rm(root, {recursive: true, force: true}); }
});
