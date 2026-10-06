import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, rm, cp} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {compileSite, getRetainedCacheMetrics, analyzeMdx, extractRegion, unwrapMdxCodeBlocks, highlight} from '../compiler.mjs';
import {createHash} from 'node:crypto';

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
    assert.ok(result.pages[0].links.some(link => link.url === 'xref:T:Example' && link.image === false));    assert.ok(result.inputs.some(input => input.file.endsWith('label.mjs')));
    assert.ok(result.assets.some(asset => asset.path.endsWith('.svg')));
    await writeFile(path.join(projectRoot, 'label.mjs'), `export const label='plugin-two'`);
    result = await compileSite({...request, workRoot: await mkdtemp(path.join(root, 'next-'))});
    assert.match(result.pages[0].html, /plugin-two/);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('Docusaurus profile built-ins render and unsupported aliases fail explicitly', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-profile-check-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    const sources = {
      'profile.mdx': `# Profile\n\n## Section\n\n<Admonition type="tip">Callout</Admonition>\n\n<Details summary="More">Hidden</Details>\n\n<Tabs><TabItem value="a" label="A">Alpha</TabItem><TabItem value="b" label="B">Beta</TabItem></Tabs>\n\n<Translate id="missing-key">Fallback words</Translate>\n\n<Link href="/product/other/">Other</Link>\n\n<Link to="/product/legacy/">Legacy</Link>\n\n<Card title="T" href="/product/other/">Card body</Card>\n\n<CodeBlock language="js">const bare = 2;</CodeBlock>\n\n<TOCInline toc={toc} />\n\n<DocCardList />\n\n:::my-custom-admonition\n\nCustom body.\n:::\n\n<Zoom>\n\n![Alt text](./pic.png)\n\n</Zoom>\n\n[external](https://github.com/example/repo/blob/main/commands.md)\n\n[relative](./other.mdx)\n\n[dangling](./missing.mdx)\n\n\`\`\`js\nconst code = 1;\n\`\`\`\n`,
      'context.mdx': `import {usePageContext} from '@lithosharp/runtime';\n\n# Context\n\nBase: {usePageContext().basePath}\n`};
    await writeFile(path.join(projectRoot, 'pic.png'), Buffer.from([137, 80, 78, 71]));
    for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
    const request = {projectRoot, workRoot, assetBaseUrl: '/product/_mdx', basePath: '/product/', sources, hydration: 'selective', linkMap: {'other.mdx': '/product/mapped-other/'},
      pages: [{id: 'profile', source: 'profile.mdx', url: '/product/profile/', title: 'Profile', locale: 'en', props: {}},
        {id: 'context', source: 'context.mdx', url: '/product/context/', title: 'Context', description: 'Sibling words', locale: 'en', props: {}}]};
    const result = await compileSite(request);
    const profile = result.pages.find(page => page.id === 'profile');
    assert.match(profile.html, /mdx-admonition mdx-tip/); assert.match(profile.html, /Callout/);
    assert.match(profile.html, /<details/); assert.match(profile.html, /Hidden/);
    assert.match(profile.html, /mdx-tabs/); assert.match(profile.html, /Alpha/);
    assert.match(profile.html, /Fallback words/);
    assert.match(profile.html, /href="\/product\/other\/"/);
    assert.match(profile.html, /href="\/product\/legacy\/"/);
    assert.match(profile.html, /mdx-card/); assert.match(profile.html, /Card body/);
    assert.match(profile.html, /const bare = 2;/);
    assert.match(profile.html, /On this page/); assert.match(profile.html, /href="#section"/);
    assert.match(profile.html, /mdx-code/);
    assert.match(profile.html, /mdx-doc-card-list/);
    assert.match(profile.html, /href=\"\/product\/context\/\"/);
    assert.match(profile.html, /Sibling words/);
    assert.match(profile.html, /href=\"\/product\/mapped-other\/\"/);
    assert.doesNotMatch(profile.html, /\/product\/profile\/\">Profile/);
    assert.match(profile.html, /<div class=\"my-custom-admonition\">/);
    assert.match(profile.html, /Custom body/);
    assert.match(profile.html, /Alt text/);
    assert.doesNotMatch(profile.html, /<Zoom/);
    assert.match(profile.html, /href=\"https:\/\/github\.com\/example\/repo\/blob\/main\/commands\.md\"/);
    assert.match(profile.html, /href=\"\.\/missing\.mdx\"/);
    assert.equal(profile.hydration, 'page');
    assert.match(profile.fallback, /Tabs/);
    const context = result.pages.find(page => page.id === 'context');
    assert.match(context.html, /Base: (?:<!-- -->)?\/product\//);
    for (const [source, pattern] of [[`import useBaseUrl from '@docusaurus/useBaseUrl';\n\n# Hook\n`, /Could not resolve "@docusaurus\/useBaseUrl"/],
      [`import Layout from '@theme/Layout';\n\n# Layout\n`, /Unsupported Docusaurus alias '@theme\/Layout'/]]) {
      await writeFile(path.join(projectRoot, 'bad.mdx'), source);
      await assert.rejects(compileSite({...request, workRoot: await mkdtemp(path.join(root, 'bad-')),
        sources: {'bad.mdx': source}, pages: [{id: 'bad', source: 'bad.mdx', url: '/product/bad/', title: 'Bad', locale: 'en', props: {}}]}),
        pattern);
    }
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('mdx-code-block fences unwrap to executable MDX while outer samples stay display', () => {
  const source = [
    '# Title',
    '',
    '```mdx-code-block',
    "import Widget from './Widget.jsx';",
    '```',
    '',
    '```mdx-code-block',
    '<Widget>',
    '```',
    '',
    'Body text.',
    '',
    '```mdx-code-block',
    '</Widget>',
    '```',
    '',
    '````md',
    '```mdx-code-block',
    '<Widget>',
    '```',
    '````',
  ].join('\n');
  const unwrapped = unwrapMdxCodeBlocks(source);
  assert.ok(unwrapped.includes("import Widget from './Widget.jsx';"));
  assert.ok(unwrapped.includes('<Widget>'));
  assert.ok(unwrapped.includes('Body text.'));
  // The display sample inside the outer ````md fence keeps its fences.
  assert.ok(unwrapped.includes('````md'));
  assert.equal((unwrapped.match(/```mdx-code-block/g) ?? []).length, 1);
  assert.equal(unwrapped.split('\n').length, source.split('\n').length);
  assert.equal(unwrapped.split('\n').indexOf('Body text.'), source.split('\n').indexOf('Body text.'));
});

test('mdx-code-block imports and docs-client hooks render statically', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-mdxblock-check-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot, {recursive: true}); await mkdir(workRoot);
    await mkdir(path.join(projectRoot, 'src', 'components', 'BrowserWindow'), {recursive: true});
    await writeFile(path.join(projectRoot, 'src', 'components', 'BrowserWindow', 'index.jsx'),
      `import React from 'react';\nexport default function BrowserWindow({children}) { return <div className="browser">{children}</div>; }\n`);
    const source = [
      '```mdx-code-block',
      "import BrowserWindow from '@site/src/components/BrowserWindow';",
      "import {useLocation} from '@docusaurus/router';",
      "import {useActiveDocContext} from '@docusaurus/plugin-content-docs/client';",
      '```',
      '',
      '# Demo',
      '',
      '```mdx-code-block',
      'export const PathName = () => <code>{useLocation().pathname}</code>;',
      '```',
      '',
      'Current: <PathName />',
      '',
      '<BrowserWindow>',
      '',
      'Version: {useActiveDocContext().activeVersion.name}',
      '',
      '</BrowserWindow>',
    ].join('\n');
    await writeFile(path.join(projectRoot, 'demo.mdx'), source);
    const request = {projectRoot, workRoot, assetBaseUrl: '/_mdx', basePath: '/',
      sources: {'demo.mdx': source},
      pages: [{id: 'demo', source: 'demo.mdx', url: '/demo/', title: 'Demo', locale: 'en', props: {}}]};
    const result = await compileSite(request);
    assert.match(result.pages[0].html, /browser/);
    assert.match(result.pages[0].html, /current/);
    assert.equal(result.pages[0].headings[0].line, 7);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('vendored components resolve docusaurus shims and npm helpers', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-vendor-check-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot, {recursive: true}); await mkdir(workRoot);
    await mkdir(path.join(projectRoot, 'src', 'components'), {recursive: true});
    await mkdir(path.join(projectRoot, 'src', 'components', 'Box'), {recursive: true});
    await writeFile(path.join(projectRoot, 'src', 'components', 'Box', 'index.jsx'),
      `import React from 'react';\n\nexport default function Box({children}) { return <div className="box">{children}</div>; }\n`);
    await writeFile(path.join(projectRoot, 'src', 'components', 'Widget.jsx'),
      `import clsx from 'clsx';\nimport Link from '@docusaurus/Link';\nimport Translate from '@docusaurus/Translate';\nimport useIsBrowser from '@docusaurus/useIsBrowser';\nimport {useHistory} from '@docusaurus/router';\nimport {useColorMode} from '@docusaurus/theme-common';\nimport useBrokenLinks from '@docusaurus/useBrokenLinks';\nimport React from 'react';\n\nexport default function Widget() {\n  const history = useHistory();\n  const {colorMode} = useColorMode();\n  useBrokenLinks().collectAnchor('a');\n  return <span className={clsx('w', colorMode)}><Link href="/product/other/">Other</Link><Translate>Fallback</Translate>{useIsBrowser() ? 'client' : 'server'}{history.location.pathname}</span>;\n}\n`);
    const sources = {'page.mdx': `import Widget from './src/components/Widget.jsx';\nimport Box from './src/components/Box';\nimport SiteBox from '@site/src/components/Box';\n\n# Page\n\n<Widget /><Box>Boxed</Box><SiteBox>SiteBoxed</SiteBox>\n`};
    await writeFile(path.join(projectRoot, 'page.mdx'), sources['page.mdx']);
    const request = {projectRoot, workRoot, assetBaseUrl: '/_mdx', basePath: '/',
      sources, pages: [{id: 'page', source: 'page.mdx', url: '/page/', title: 'Page', locale: 'en', props: {}}]};
    const result = await compileSite(request);
    assert.match(result.pages[0].html, /Other/);
    assert.match(result.pages[0].html, /Fallback/);
    assert.match(result.pages[0].html, /server/);
    assert.match(result.pages[0].html, /Boxed/);
    assert.match(result.pages[0].html, /SiteBoxed/);
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
    const validateAssets = result => {
      const paths = new Set(result.assets.map(asset => asset.path));
      assert.equal(paths.size, result.assets.length, 'duplicate asset paths');
      for (const asset of result.assets) {
        assert.equal(path.posix.normalize(asset.path), asset.path); assert.ok(!asset.path.startsWith('../') && !path.posix.isAbsolute(asset.path));
        assert.equal(createHash('sha256').update(Buffer.from(asset.bytes, 'base64')).digest('hex'), asset.hash);
        for (const reference of asset.imports) assert.ok(paths.has(reference), asset.path + ' -> ' + reference);
      }
      const assets = new Map(result.assets.map(asset => [asset.path, asset]));
      const seen = new Set(), queue = result.pages.flatMap(page => [page.entry, ...page.css].filter(Boolean));
      while (queue.length) {const name = queue.pop();if (seen.has(name)) continue;seen.add(name);assert.ok(assets.has(name), name);queue.push(...assets.get(name).imports);}
      assert.ok(result.assets.filter(asset => asset.path.endsWith('.js')).every(asset => seen.has(asset.path)), 'orphan browser JS');
      assert.ok(result.assets.some(asset => asset.path.startsWith('chunks/Counter-') && asset.path.endsWith('.js') && asset.imports.some(reference => reference.startsWith('chunks/Counter-') && reference.endsWith('.css'))), 'dynamic CSS bundle dependency missing');
    };
    const result = await compileSite(request);
    validateAssets(result);
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
    validateAssets(repeated);
    for (const page of result.pages.filter(page => page.hydration !== 'page'))
      assert.deepEqual(repeated.pages.find(candidate => candidate.id === page.id).css, page.css);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('browser reuse avoids actual bundling only when entries, resolution and dependencies are unchanged', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-browser-cache-'));
  try {
    const projectRoot = path.join(root, 'input');
    await mkdir(projectRoot);
    const component = path.join(projectRoot, 'Counter.jsx');
    const css = path.join(projectRoot, 'counter.css');
    await writeFile(component, `import {useState} from 'react';import './counter.css';export default function Counter(){const [n,set]=useState(3);return <button onClick={()=>set(n+1)}>Count {n}</button>}`);
    await writeFile(css, 'button{color:navy}');
    await writeFile(path.join(projectRoot, 'plugin.mjs'), `export default function plugin({label}){return tree=>{tree.children.unshift({type:'paragraph',children:[{type:'text',value:label}]})}}`);
    const sources = {'static.mdx': '# Static\n\nOriginal body.',
      'one.mdx': `import Counter from './Counter.jsx'\n\n# One\n\n<Counter />`,
      'two.mdx': `import Counter from './Counter.jsx'\n\n# Two\n\n<Counter />`};
    for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
    const request = {projectRoot, assetBaseUrl: '/_mdx', basePath: '/', sources, hydration: 'selective', cacheable: true,
      resolutionFingerprint: 'fixture-file-set-v1', plugins: [{stage: 'remark', module: './plugin.mjs', options: {label: 'first-plugin-value'}}],
      pages: Object.keys(sources).map((source, index) => ({id: 'cache-' + index, source, url: '/' + index + '/', title: source, locale: 'en', props: {}}))};
    const run = async overrides => {
      const workRoot = await mkdtemp(path.join(root, 'work-'));
      try { return await compileSite({...request, ...overrides, workRoot}); }
      finally { await rm(workRoot, {recursive: true, force: true}); }
    };
    const initial = await run();
    assert.deepEqual(initial.rebundledPages, ['cache-1', 'cache-2']);
    assert.equal(initial.bundledPages, 2);
    const snapshot = getRetainedCacheMetrics();
    for (const name of ['module', 'render']) {
      assert.ok(snapshot[name].entries > 0 && snapshot[name].entries <= snapshot[name].entryCeiling);
      assert.ok(snapshot[name].estimatedPayloadBytes > 0 && snapshot[name].estimatedPayloadBytes <= snapshot[name].payloadBudgetBytes);
    }
    assert.equal(snapshot.browser.retained, true);
    assert.ok(snapshot.browser.estimatedPayloadBytes <= snapshot.browser.payloadBudgetBytes);
    assert.ok(snapshot.browser.dependencyEntries <= snapshot.browser.dependencyCeiling);
    assert.ok(snapshot.browser.referenceEntries <= snapshot.browser.referenceCeiling);
    const untouched = structuredClone(snapshot);
    snapshot.module.entries = -1; snapshot.browser.estimatedPayloadBytes = Infinity;
    assert.deepEqual(getRetainedCacheMetrics(), untouched, 'Returned snapshot leaked mutable cache state.');
    sources['static.mdx'] = '# Static\n\nUpdated body.';
    await writeFile(path.join(projectRoot, 'static.mdx'), sources['static.mdx']);
    const reused = await run();
    assert.match(reused.pages[0].html, /Updated body/);
    assert.deepEqual(reused.rebundledPages, []);
    assert.equal(reused.bundledPages, 0);
    assert.equal(reused.timings.browserBundleMilliseconds, 0);
    assert.deepEqual(reused.assets, initial.assets);
    assert.deepEqual(reused.pages.map(page => [page.entry, page.css]), initial.pages.map(page => [page.entry, page.css]));

    // A dependency edit invalidates the result even when its browser bytes do not change.
    await writeFile(component, `// Bundling must run again.\nimport {useState} from 'react';import './counter.css';export default function Counter(){const [n,set]=useState(3);return <button onClick={()=>set(n+1)}>Count {n}</button>}`);
    const commentEdit = await run();
    assert.deepEqual(commentEdit.rebundledPages, ['cache-1', 'cache-2']);
    assert.deepEqual(commentEdit.assets, initial.assets);
    await writeFile(css, 'button{color:crimson}');
    const cssEdit = await run();
    assert.deepEqual(cssEdit.rebundledPages, ['cache-1', 'cache-2']);
    assert.notDeepEqual(cssEdit.pages[1].css, reused.pages[1].css);

    const pluginEdit = await run({plugins: [{stage: 'remark', module: './plugin.mjs', options: {label: 'second-plugin-value'}}]});
    assert.deepEqual(pluginEdit.rebundledPages, ['cache-1', 'cache-2']);
    assert.match(pluginEdit.pages[1].html, /second-plugin-value/);
    assert.ok(pluginEdit.assets.some(asset => asset.path.endsWith('.js') && Buffer.from(asset.bytes, 'base64').toString().includes('second-plugin-value')));

    // Resolution candidates include additions and package/configuration edits.
    const resolutionEdit = await run({resolutionFingerprint: 'fixture-file-set-v2'});
    assert.deepEqual(resolutionEdit.rebundledPages, ['cache-1', 'cache-2']);
    const publicPathEdit = await run({resolutionFingerprint: 'fixture-file-set-v2', assetBaseUrl: '/other/_mdx'});
    assert.deepEqual(publicPathEdit.rebundledPages, ['cache-1', 'cache-2']);
    assert.ok(publicPathEdit.assets.some(asset => asset.path.endsWith('.js') && Buffer.from(asset.bytes, 'base64').toString().includes('/other/_mdx/')));
    const withoutResolution = await run({resolutionFingerprint: undefined});
    assert.deepEqual(withoutResolution.rebundledPages, ['cache-1', 'cache-2']);
    const noncacheable = await run({cacheable: false});
    assert.deepEqual(noncacheable.rebundledPages, ['cache-1', 'cache-2']);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('partial browser builds preserve unrelated closures and regroup shared module dependencies', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-browser-groups-'));
  try {
    const projectRoot = path.join(root, 'input');
    await mkdir(projectRoot);
    const component = name => path.join(projectRoot, name + '.jsx');
    const code = (name, shared = false) => `import React,{useState} from 'react';import {usePageContext} from '@lithosharp/runtime';${shared ? "import {state} from './shared.js';" : ''}export default function ${name}(){const [n,set]=useState(0);const page=usePageContext();return <button onClick={()=>set(n+1)}>${name} {n} {page.title}${shared ? ' {state.label}' : ''}</button>}`;
    await writeFile(component('Counter'), code('Counter'));
    await writeFile(component('Toggle'), code('Toggle'));
    await writeFile(path.join(projectRoot, 'shared.js'), "export const state={label:'shared-module-canary'};");
    await writeFile(path.join(projectRoot, 'shared.css'), 'button{color:navy}');
    const sources = {'one.mdx': `import Counter from './Counter.jsx';import './shared.css';\n\n# One\n\n<Counter />`,
      'two.mdx': `import Toggle from './Toggle.jsx';import './shared.css';\n\n# Two\n\n<Island component={Toggle} strategy="load" />`};
    for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
    const request = {projectRoot, assetBaseUrl: '/_mdx', basePath: '/', sources, hydration: 'selective', cacheable: true,
      resolutionFingerprint: 'unchanged-project-file-set', pages: Object.keys(sources).map((source, index) =>
        ({id: 'group-' + index, source, url: '/' + index + '/', title: source, locale: 'en', props: {}}))};
    const run = async overrides => {
      const workRoot = await mkdtemp(path.join(root, 'work-'));
      try { return await compileSite({...request, ...overrides, workRoot}); }
      finally { await rm(workRoot, {recursive: true, force: true}); }
    };
    const initial = await run();
    assert.deepEqual(initial.rebundledPages, ['group-0', 'group-1']);
    assert.equal(initial.pages[0].hydration, 'page');
    assert.equal(initial.pages[1].hydration, 'selective');
    const closure = (result, page) => {
      const assets = new Map(result.assets.map(asset => [asset.path, asset]));
      const seen = new Set(), queue = [page.entry, ...page.css];
      while (queue.length) {
        const name = queue.pop(); if (seen.has(name)) continue; seen.add(name);
        assert.ok(assets.has(name), 'missing asset ' + name);
        queue.push(...assets.get(name).imports);
      }
      return [...seen].sort().map(name => [name, assets.get(name).hash]);
    };
    const checkAssets = result => {
      assert.equal(new Set(result.assets.map(asset => asset.path)).size, result.assets.length);
      for (const page of result.pages) closure(result, page);
    };
    await writeFile(component('Counter'), code('Counter') + '\n// Changed input with identical output.');
    const comment = await run();
    assert.deepEqual(comment.rebundledPages, ['group-0']);
    assert.equal(comment.pages[1].html, initial.pages[1].html);
    assert.equal(comment.pages[1].entry, initial.pages[1].entry);
    assert.deepEqual(closure(comment, comment.pages[1]), closure(initial, initial.pages[1]));
    checkAssets(comment);

    await writeFile(component('Counter'), code('Counter', true));
    const firstImport = await run();
    assert.deepEqual(firstImport.rebundledPages, ['group-0']);
    assert.equal(firstImport.pages[1].entry, comment.pages[1].entry);
    await writeFile(component('Toggle'), code('Toggle', true));
    const merged = await run();
    assert.deepEqual(merged.rebundledPages, ['group-0', 'group-1']);
    assert.equal(merged.workMetrics.complete, true);
    assert.equal(merged.workMetrics.browserBuildInvocations, 2);
    assert.equal(merged.workMetrics.browserEntryBuildAttempts, 3);
    assert.equal(merged.workMetrics.esbuildInvocations, 3);
    assert.equal(merged.bundledPages, 2);
    assert.equal(merged.pages[0].html, firstImport.pages[0].html);
    assert.equal(merged.assets.filter(asset => asset.path.endsWith('.js') && Buffer.from(asset.bytes, 'base64').toString().includes('shared-module-canary')).length, 1);
    checkAssets(merged);

    await writeFile(component('Counter'), code('Counter', true) + '\n// Both pages share a mutable JS module.');
    const sharedEdit = await run();
    assert.deepEqual(sharedEdit.rebundledPages, ['group-0', 'group-1']);
    await writeFile(component('Counter'), code('Counter'));
    const split = await run();
    assert.deepEqual(split.rebundledPages, ['group-0', 'group-1']);
    await writeFile(component('Counter'), code('Counter') + '\n// Independent again.');
    const independent = await run();
    assert.deepEqual(independent.rebundledPages, ['group-0']);
    assert.equal(independent.pages[1].entry, split.pages[1].entry);
    checkAssets(independent);

    await writeFile(path.join(projectRoot, 'shared.css'), 'button{color:crimson}');
    const cssEdit = await run();
    assert.deepEqual(cssEdit.rebundledPages, ['group-0', 'group-1']);
    const globalEdit = await run({linkMap: {'one.mdx': '/changed/', 'two.mdx': '/two/'}});
    assert.deepEqual(globalEdit.rebundledPages, ['group-0', 'group-1']);
    checkAssets(globalEdit);
    const deleted = await run({pages: [request.pages[0]], sources: {'one.mdx': sources['one.mdx']}, resolutionFingerprint: 'removed-page'});
    assert.deepEqual(deleted.rebundledPages, ['group-0']);
    assert.ok(!deleted.assets.some(asset => asset.path === globalEdit.pages[1].entry));
    assert.ok(deleted.assets.filter(asset => asset.path.endsWith('.js')).every(asset => !Buffer.from(asset.bytes, 'base64').toString().includes('shared-module-canary')));
    checkAssets(deleted);
    await writeFile(component('Counter'), code('Counter') + "\nimport './missing.js';");
    await assert.rejects(run({pages: [request.pages[0]], sources: {'one.mdx': sources['one.mdx']}, resolutionFingerprint: 'removed-page'}), /Could not resolve.*missing\.js/);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('removed package imports leave neither stale notices nor cache validation dependencies', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-browser-notices-'));
  try {
    const projectRoot = path.join(root, 'input');
    const packageRoot = path.join(projectRoot, 'node_modules', 'removed-fixture');
    await mkdir(packageRoot, {recursive: true});
    await writeFile(path.join(packageRoot, 'package.json'), JSON.stringify({name: 'removed-fixture', version: '1.0.0', main: 'index.js', license: 'MIT'}));
    await writeFile(path.join(packageRoot, 'LICENSE'), 'REMOVED_PACKAGE_LICENSE_CANARY');
    await writeFile(path.join(packageRoot, 'index.js'), `import React from 'react';export default function Widget(){return React.createElement('span',null,'User package')}`);
    const counter = `import {useState} from 'react';\nexport function Counter(){const [n,set]=useState(0);return <button onClick={()=>set(n+1)}>Count {n}</button>}\n\n# Page\n\n<Counter />`;
    const sources = {'page.mdx': `import Widget from 'removed-fixture';\n` + counter + '\n\n<Widget />'};
    await writeFile(path.join(projectRoot, 'page.mdx'), sources['page.mdx']);
    const request = {projectRoot, sources, assetBaseUrl: '/_mdx', basePath: '/', cacheable: true, hydration: 'selective',
      resolutionFingerprint: 'stable-files-and-manifests', pages: [{id: 'notices', source: 'page.mdx', url: '/page/', title: 'Page', locale: 'en', props: {}}]};
    const run = async overrides => {
      const workRoot = await mkdtemp(path.join(root, 'work-'));
      try { return await compileSite({...request, ...overrides, workRoot}); }
      finally { await rm(workRoot, {recursive: true, force: true}); }
    };
    const notices = result => Buffer.from(result.assets.find(asset => asset.path === 'third-party-notices.txt').bytes, 'base64').toString();
    const initial = await run();
    assert.match(notices(initial), /REMOVED_PACKAGE_LICENSE_CANARY/);
    sources['page.mdx'] = counter;
    await writeFile(path.join(projectRoot, 'page.mdx'), counter);
    const warm = await run();
    assert.doesNotMatch(notices(warm), /removed-fixture|REMOVED_PACKAGE_LICENSE_CANARY/);
    assert.ok(warm.inputs.every(input => !input.file.includes('removed-fixture')));
    await rm(packageRoot, {recursive: true, force: true});
    const deleted = await run();
    assert.equal(notices(deleted), notices(warm));
    const cold = await run({cacheable: false});
    assert.equal(notices(warm), notices(cold));

    sources['page.mdx'] = '# Static\n\nNo browser entry.';
    await writeFile(path.join(projectRoot, 'page.mdx'), sources['page.mdx']);
    await run(); // Warm a graph, then prove an all-static result retains no old runtime JS.
    assert.ok((await run()).assets.every(asset => !asset.path.endsWith('.js')));
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('an input changed during SSR cannot be certified with its later fingerprint', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-browser-race-'));
  try {
    const projectRoot = path.join(root, 'input'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    const css = path.join(projectRoot, 'style.css');
    await writeFile(css, 'button{color:navy}');
    await writeFile(path.join(projectRoot, 'Mutator.jsx'), `import './style.css';export default function Mutator(){if(typeof window==='undefined')process.getBuiltinModule('fs').writeFileSync(${JSON.stringify(css)},'button{color:red}');return <button>Mutated</button>}`);
    const source = `import Mutator from './Mutator.jsx'\n\n# Race\n\n<Mutator />`;
    await writeFile(path.join(projectRoot, 'race.mdx'), source);
    await assert.rejects(compileSite({projectRoot, workRoot, assetBaseUrl: '/_mdx', basePath: '/', sources: {'race.mdx': source},
      pages: [{id: 'race', source: 'race.mdx', url: '/race/', title: 'Race', locale: 'en', props: {}}]}), /input changed during compilation/);
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('analysis-only inspection structures MDX without executing imports', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-analyze-check-'));
  try {
    const sentinel = path.join(root, 'evil-ran.txt');
    await writeFile(path.join(root, 'evil.mjs'),
      `import {writeFileSync} from 'node:fs';\nwriteFileSync(${JSON.stringify(sentinel)}, 'executed');\nexport default () => null;\n`);
    // No network in analysis: any fetch attempt fails loudly.
    const guard = globalThis.fetch;
    globalThis.fetch = () => { throw new Error('network must not run during analysis'); };
    try {
      const result = await analyzeMdx({sourcePath: 'page.mdx',
        text: '# Guide\n\nimport Evil from "./evil.mjs";\nimport {useState} from "react";\n\n<Evil />\n\nSee [docs](./other.mdx).\n'});
      assert.equal(result.diagnostics.length, 0);
      assert.equal(result.headings[0].text, 'Guide');
      assert.equal(result.headings[0].id, 'guide');
      assert.deepEqual(result.imports.map(entry => entry.source), ['./evil.mjs', 'react']);
      assert.ok(result.links.some(link => link.url === './other.mdx'));
    } finally { globalThis.fetch = guard; }
    const {access} = await import('node:fs/promises');
    await assert.rejects(access(sentinel));
  } finally { await rm(root, {recursive: true, force: true}); }
});

test('analysis-only inspection returns fatal diagnostics without worker state', async () => {
  const broken = await analyzeMdx({sourcePath: 'broken.mdx', text: '# Hi\n\n<Unclosed>\n'});
  assert.equal(broken.diagnostics.length, 1);
  assert.equal(broken.headings.length, 0);
  assert.equal(broken.imports.length, 0);
  const next = await analyzeMdx({sourcePath: 'next.mdx', text: '# Next\n'});
  assert.equal(next.diagnostics.length, 0);
  assert.equal(next.headings[0].text, 'Next');
});

test('nested fence highlighting is stable across Prism load orders', () => {
  // Loading yaml after markdown makes Prism re-evaluate markdown and register
  // its global hooks twice, which used to double language-xxxx classes on
  // nested fences depending on page compilation order. (Bundled languages
  // such as js/css never lazy-load, so yaml is the regression trigger.)
  const sample = 'No semi:\n\n<!-- prettier-ignore -->\n```jsx\n<div>Example</div>\n```';
  const before = highlight(sample, 'markdown');
  highlight('key: value', 'yaml');
  const after = highlight(sample, 'markdown');
  assert.equal(after, before);
  assert.ok(!after.includes('language-jsx language-jsx'));
});

test('static runtime caches transition to islands without stale empty facades', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-runtime-transition-'));
  try {
    const projectRoot = path.join(root, 'input'); await mkdir(projectRoot);
    const sources = {'page.mdx': '# Static'};
    await writeFile(path.join(projectRoot, 'page.mdx'), sources['page.mdx']);
    await writeFile(path.join(projectRoot, 'Widget.jsx'), `import {useState} from 'react';export default function Widget(){const [n,set]=useState(0);return <button onClick={()=>set(n+1)}>Count {n}</button>}`);
    const request = {projectRoot, sources, assetBaseUrl: '/_mdx', basePath: '/', hydration: 'selective', cacheable: true, resolutionFingerprint: 'stable-inventory',
      pages: [{id: 'transition', source: 'page.mdx', url: '/', title: 'Page', locale: 'en', props: {}}]};
    const run = async () => { const workRoot = await mkdtemp(path.join(root, 'work-')); try {return await compileSite({...request, workRoot});} finally {await rm(workRoot, {recursive: true, force: true});} };
    assert.equal((await run()).pages[0].hydration, 'static');
    sources['page.mdx'] = `import Widget from './Widget.jsx';\n\n<Island component={Widget} strategy="load" />`;
    await writeFile(path.join(projectRoot, 'page.mdx'), sources['page.mdx']);
    const interactive = await run();
    assert.deepEqual(interactive.rebundledPages, ['transition']);
    assert.ok(interactive.assets.some(asset => asset.path.startsWith('runtime/react-')));
    const assets = new Map(interactive.assets.map(asset => [asset.path, asset]));
    for (const asset of interactive.assets) for (const dependency of asset.imports) assert.ok(assets.has(dependency), dependency);
    assert.deepEqual((await run()).rebundledPages, []);
  } finally {await rm(root, {recursive: true, force: true});}
});

test('partial builds keep colliding CSS module names aligned with server HTML', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-css-module-scope-'));
  try {
    const projectRoot = path.join(root, 'input'); const sources = {};
    for (const name of ['a', 'b']) {
      await mkdir(path.join(projectRoot, name), {recursive: true});
      await writeFile(path.join(projectRoot, name, 'Counter.module.css'), `.counter{color:${name === 'a' ? 'red' : 'blue'}}`);
      await writeFile(path.join(projectRoot, name, 'Counter.jsx'), `import {useState} from 'react';import styles from './Counter.module.css';export default function Counter(){const [n,set]=useState(0);return <button className={styles.counter} onClick={()=>set(n+1)}>${name} {n}</button>}`);
      sources[name + '.mdx'] = `import Counter from './${name}/Counter.jsx';\n\n<Counter />`;
      await writeFile(path.join(projectRoot, name + '.mdx'), sources[name + '.mdx']);
    }
    const request = {projectRoot, sources, assetBaseUrl: '/_mdx', basePath: '/', cacheable: true, resolutionFingerprint: 'fixed',
      pages: Object.keys(sources).map(source => ({id: source, source, url: '/' + source, title: source, locale: 'en', props: {}}))};
    const run = async () => { const workRoot = await mkdtemp(path.join(root, 'work-')); try {return await compileSite({...request, workRoot});} finally {await rm(workRoot, {recursive: true, force: true});} };
    const verify = result => {
      const classes = result.pages.map(page => /class="([^"]+)"/.exec(page.html)[1]);
      assert.notEqual(classes[0], classes[1]);
      for (const [index, page] of result.pages.entries()) {
        const css = page.css.map(file => Buffer.from(result.assets.find(asset => asset.path === file).bytes, 'base64').toString()).join('\n');
        assert.ok(css.includes('.' + classes[index]), classes[index] + ' missing from CSS');
        assert.ok(result.assets.filter(asset => asset.path.endsWith('.js')).some(asset => Buffer.from(asset.bytes, 'base64').toString().includes(classes[index])));
      }
    };
    verify(await run());
    await writeFile(path.join(projectRoot, 'b', 'Counter.module.css'), '.counter{color:green}');
    const edited = await run(); verify(edited);
    assert.deepEqual(edited.rebundledPages, ['a.mdx', 'b.mdx']);
  } finally {await rm(root, {recursive: true, force: true});}
});

test('equivalent provider imports use the shared runtime identity', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-provider-alias-'));
  try {
    const projectRoot = path.join(root, 'input'); await mkdir(projectRoot);
    const {createRequire} = await import('node:module');
    const require = createRequire(import.meta.url);
    const provider = require.resolve('@mdx-js/react').replace(/\.js$/, '');
    const context = path.resolve(import.meta.dirname, '../runtime/context.mjs').split(path.sep).join('/');
    await writeFile(path.join(projectRoot, 'tsconfig.json'), JSON.stringify({compilerOptions: {baseUrl: '.', paths: {'context-alias': [context]}}}));
    await writeFile(path.join(projectRoot, 'Widget.jsx'), `import {useState,useContext} from 'react';import {useMDXComponents} from ${JSON.stringify(provider)};import {PageContext} from 'context-alias';export default function Widget(){const [n,set]=useState(0);const page=useContext(PageContext);return <button onClick={()=>set(n+1)}>{page.title} {Object.keys(useMDXComponents()).length} {n}</button>}`);
    const source = `import Widget from './Widget.jsx';\n\n<Widget />`; await writeFile(path.join(projectRoot, 'page.mdx'), source);
    const workRoot = await mkdtemp(path.join(root, 'work-'));
    const result = await compileSite({projectRoot, workRoot, sources: {'page.mdx': source}, assetBaseUrl: '/_mdx', basePath: '/',
      pages: [{id: 'alias', source: 'page.mdx', url: '/', title: 'Alias', locale: 'en', props: {}}]});
    assert.match(result.pages[0].html, /Alias/);
    const main = result.assets.filter(asset => /^(?:pages|chunks)\//.test(asset.path) && asset.path.endsWith('.js')).map(asset => Buffer.from(asset.bytes, 'base64').toString()).join('\n');
    assert.doesNotMatch(main, /createContext\(/);
    assert.match(main, /runtime\/mdx-/);
    assert.match(main, /runtime\/context-/);
  } finally {await rm(root, {recursive: true, force: true});}
});

async function workerFixture(prefix, sources, overrides = {}) {
  const root = await mkdtemp(path.join(os.tmpdir(), prefix));
  const projectRoot = path.join(root, 'input'); await mkdir(projectRoot);
  for (const [file, source] of Object.entries(sources)) await writeFile(path.join(projectRoot, file), source);
  const request = {projectRoot, sources, assetBaseUrl: '/first/_mdx', basePath: '/', cacheable: true, resolutionFingerprint: 'stable',
    pages: Object.keys(sources).map(source => ({id: source, source, url: '/' + source, title: source, locale: 'en', props: {}})), ...overrides};
  return {root, projectRoot, request, run: async changes => {
    const workRoot = changes?.workRoot ?? await mkdtemp(path.join(root, 'work-'));
    return compileSite({...request, ...changes, workRoot});
  }, dispose: () => rm(root, {recursive: true, force: true})};
}

test('render cache hashes shared rewritten asset closures and reuses scratch safely', async () => {
  const fixture = await workerFixture('lithosharp-asset-base-', {'one.mdx': `import Image from './Image.jsx';\n\n<Image />`, 'two.mdx': `import Image from './Image.jsx';\n\n<Image />`});
  try {
    await writeFile(path.join(fixture.projectRoot, 'Image.jsx'), `import icon from './icon.svg';export default function Image(){return <img src={icon} alt="shared icon"/>}`);
    await writeFile(path.join(fixture.projectRoot, 'icon.svg'), '<svg xmlns="http://www.w3.org/2000/svg"/>');
    const workRoot = await mkdtemp(path.join(fixture.root, 'same-work-'));
    const initial = await fixture.run({workRoot});
    assert.ok(initial.pages.every(page => page.html.includes('/first/_mdx/assets/icon-')));
    const changed = await fixture.run({workRoot, assetBaseUrl: '/second/_mdx'});
    assert.ok(changed.pages.every(page => page.html.includes('/second/_mdx/assets/icon-')));
    assert.ok(changed.pages.every(page => !page.html.includes('/first/_mdx/')));
    assert.equal(changed.renderedPages, 2);
    assert.equal((await fixture.run({workRoot, assetBaseUrl: '/second/_mdx'})).renderedPages, 0);
  } finally {await fixture.dispose();}
});

test('compiler extensions fingerprint JSX TSX MTS and CTS consumed bytes', async () => {
  const fixture = await workerFixture('lithosharp-extension-loaders-', {'page.mdx': '# Plugin'});
  try {
    const formats = ['jsx', 'tsx', 'mts', 'cts'];
    for (const format of formats) await writeFile(path.join(fixture.projectRoot, 'label.' + format), `export const label='${format}-first';`);
    await writeFile(path.join(fixture.projectRoot, 'plugin.mjs'), formats.map((format, i) => `import {label as label${i}} from './label.${format}';`).join('\n') + `\nexport default function plugin(){return tree=>{tree.children.unshift({type:'paragraph',children:[{type:'text',value:[${formats.map((_, i) => 'label' + i).join(',')}].join(' ')}]})}}`);
    const changes = {plugins: [{stage: 'remark', module: './plugin.mjs'}]};
    const initial = await fixture.run(changes);
    for (const format of formats) assert.ok(initial.inputs.some(input => input.file.endsWith('label.' + format)), format);
    for (const format of formats) {
      await writeFile(path.join(fixture.projectRoot, 'label.' + format), `export const label='${format}-second';`);
      assert.ok((await fixture.run(changes)).pages[0].html.includes(format + '-second'), format);
    }
  } finally {await fixture.dispose();}
});

test('DocCardList cache tracks ordered sibling card metadata and membership', async () => {
  const fixture = await workerFixture('lithosharp-card-inputs-', {'index.mdx': '<DocCardList />', 'one.mdx': '# One', 'two.mdx': '# Two'}, {hydration: 'selective'});
  try {
    const initial = await fixture.run(); assert.match(initial.pages[0].html, /one.mdx/);
    const pages = fixture.request.pages.map(page => page.source === 'one.mdx' ? {...page, title: 'Renamed sibling', description: 'New description', url: '/new-url/'} : page);
    const changed = await fixture.run({pages});
    assert.match(changed.pages[0].html, /Renamed sibling/); assert.match(changed.pages[0].html, /New description/); assert.match(changed.pages[0].html, /new-url/);
    const removed = await fixture.run({pages: [pages[0], pages[2]]});
    assert.doesNotMatch(removed.pages[0].html, /Renamed sibling/);
    const reordered = await fixture.run({pages: [pages[0], pages[2], pages[1]]});
    assert.ok(reordered.pages[0].html.indexOf('two.mdx') < reordered.pages[0].html.indexOf('Renamed sibling'));
  } finally {await fixture.dispose();}
});

test('static page CSS preserves source import cascade and transitive order', async () => {
  const fixture = await workerFixture('lithosharp-css-order-', {'page.mdx': `import './z.css';\nimport './a.css';\n\n# Static`}, {hydration: 'selective'});
  try {
    await writeFile(path.join(fixture.projectRoot, 'z.css'), '@import "./base.css";h1{color:red}');
    await writeFile(path.join(fixture.projectRoot, 'a.css'), 'h1{color:blue}');
    await writeFile(path.join(fixture.projectRoot, 'base.css'), 'h1{color:black}');
    const result = await fixture.run(); assert.equal(result.pages[0].hydration, 'static');
    const css = result.pages[0].css.map(file => Buffer.from(result.assets.find(asset => asset.path === file).bytes, 'base64').toString()).join('\n').replaceAll('#000', 'black').replaceAll('#00f', 'blue');
    assert.ok(css.indexOf('black') < css.indexOf('red') && css.indexOf('red') < css.indexOf('blue'), css);
    assert.equal(css.split('black').length - 1, 1);
    assert.ok(result.assets.every(asset => !asset.path.endsWith('.js')));
  } finally {await fixture.dispose();}
});

test('invalid page entry names reject before output writes', async () => {
  const fixture = await workerFixture('lithosharp-entry-collisions-', {'page.mdx': '# Page'});
  try {
    const page = fixture.request.pages[0];
    await assert.rejects(fixture.run({pages: [page, page]}), /duplicate.*page.*id/i);
    await assert.rejects(fixture.run({pages: [{...page, id: '../../escape'}]}), /page.*id/i);
  } finally {await fixture.dispose();}
});

test('user imports of runtime implementation dependencies preserve one browser module instance', async () => {
  const fixture = await workerFixture('lithosharp-runtime-overlap-', {'one.mdx': `import Widget from './Widget.jsx';\n\n<Widget />`, 'two.mdx': '# Second'});
  try {
    const component = `import {useState} from 'react';import {unstable_now} from 'scheduler';export default function Widget(){const [n,set]=useState(0);return <button onClick={()=>set(n+unstable_now())}>Count {n}</button>}`;
    await writeFile(path.join(fixture.projectRoot, 'Widget.jsx'), component);
    const check = result => {
      assert.equal(result.assets.filter(asset => asset.path.endsWith('.js') && asset.inputs.some(file => file.endsWith('/scheduler.production.js'))).length, 1);
      assert.ok(result.assets.every(asset => !asset.path.startsWith('runtime/')));
      const assets = new Map(result.assets.map(asset => [asset.path, asset]));
      const seen = new Set(), queue = result.pages.flatMap(page => [page.entry, ...page.css].filter(Boolean));
      while (queue.length) {const name = queue.pop();if (seen.has(name)) continue;seen.add(name);assert.ok(assets.has(name));queue.push(...assets.get(name).imports);}
      assert.ok(result.assets.filter(asset => asset.path.endsWith('.js')).every(asset => seen.has(asset.path)), 'orphan browser JS');
    };
    check(await fixture.run());
    assert.deepEqual((await fixture.run()).rebundledPages, []);
    await writeFile(path.join(fixture.projectRoot, 'Widget.jsx'), component + '\n// A runtime overlap retains one complete naming/module scope.');
    const edited = await fixture.run();check(edited);assert.deepEqual(edited.rebundledPages, ['one.mdx', 'two.mdx']);
  } finally {await fixture.dispose();}
});

test('live iframe runtime bundles through tracked readers with complete asset closures', async () => {
  const fixture = await workerFixture('lithosharp-live-readers-', {'page.mdx': `import LiveCode from '@lithosharp/live-code';\n\n<LiveCode code="render(React.createElement('strong',null,'Example'));" />`});
  try {
    const result = await fixture.run();
    assert.match(result.pages[0].html, /React playground/);
    const code = result.assets.filter(asset => asset.path.endsWith('.js')).map(asset => Buffer.from(asset.bytes, 'base64').toString()).join('\n');
    assert.match(code, /globalThis\.React/); assert.match(code, /globalThis\.render/);
    assert.ok(result.inputs.some(input => input.file.endsWith('react-dom-client.production.js')));
    assert.ok(result.inputs.some(input => input.file.endsWith('scheduler.production.js')));
    const assets = new Map(result.assets.map(asset => [asset.path, asset]));
    const seen = new Set(), queue = result.pages.flatMap(page => [page.entry, ...page.css].filter(Boolean));
    while (queue.length) {const name = queue.pop();if (seen.has(name)) continue;seen.add(name);assert.ok(assets.has(name), name);queue.push(...assets.get(name).imports);}
    assert.ok(result.assets.filter(asset => asset.path.endsWith('.js')).every(asset => seen.has(asset.path)), 'orphan live-runtime JS');
    assert.deepEqual((await fixture.run()).rebundledPages, []);
  } finally {await fixture.dispose();}
});

test('native MDX sample compiles its real interactive assets', async () => {
  const sample = path.resolve(import.meta.dirname, '../../../../samples/LithoSharp.MdxSample/content');
  const {readFile} = await import('node:fs/promises');
  const sources = {};
  for (const name of ['01-static', '02-interactive', '03-islands', '04-live']) {
    const raw = await readFile(path.join(sample, name + '.mdx'), 'utf8');
    sources[name + '.mdx'] = raw.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '');
  }
  const fixture = await workerFixture('lithosharp-native-asset-contract-', sources, {hydration: 'selective'});
  try {
    for (const name of ['Counter.jsx', 'counter.module.css']) await cp(path.join(sample, name), path.join(fixture.projectRoot, name));
    const result = await fixture.run();
    assert.ok(result.assets.some(asset => asset.path.startsWith('chunks/Counter-') && asset.path.endsWith('.js') && asset.imports.some(reference => reference.startsWith('chunks/Counter-') && reference.endsWith('.css'))), 'sample dynamic CSS bundle dependency missing');
  } finally {await fixture.dispose();}
});


test('failed native compilation and rendering report observed work without claiming complete coverage', async () => {
  for (const [name, source, pattern, renders] of [
    ['compile', '# Broken\n\n<', /Unexpected|Could not parse|end of file/i, 0],
    ['render', "# Broken\n\n{(() => { throw new Error('MetricRenderFailure'); })()}", /MetricRenderFailure/, 1]
  ]) {
    const fixture = await workerFixture('lithosharp-partial-work-' + name + '-', {'page.mdx': source}, {hydration: 'selective'});
    try {
      const workRoot = await mkdtemp(path.join(fixture.root, 'failed-')), observed = {};
      await assert.rejects(compileSite({...fixture.request, workRoot}, observed), pattern);
      assert.equal(observed.complete, false);
      assert.equal(observed.esbuildInvocations, 1);
      assert.equal(observed.mdxCompileInvocations, 1);
      assert.equal(observed.compiledModules, renders);
      assert.equal(observed.renderInvocations, renders);
      assert.equal(observed.renderedPages, 0);
      assert.equal(observed.browserBuildInvocations, 0);
      assert.ok(observed.totalMilliseconds > 0 && Number.isFinite(observed.totalMilliseconds));
      assert.ok(observed.serverBundleMilliseconds > 0);
      if (renders) assert.ok(observed.renderMilliseconds > 0);
      const recoveredSource = '# RecoveredWorkMarker-' + name;
      await writeFile(path.join(fixture.projectRoot, 'page.mdx'), recoveredSource);
      const recovered = await fixture.run({sources: {'page.mdx': recoveredSource}});
      assert.match(recovered.pages[0].html, /RecoveredWorkMarker/);
      assert.equal(recovered.workMetrics.complete, true);
      assert.equal(recovered.workMetrics.renderedPages, 1);
    } finally { await fixture.dispose(); }
  }
});
