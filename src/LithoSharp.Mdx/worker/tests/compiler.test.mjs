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
