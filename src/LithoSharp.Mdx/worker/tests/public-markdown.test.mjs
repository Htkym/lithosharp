import assert from 'node:assert/strict';
import {mkdtemp, mkdir, writeFile, rm} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {compile} from '@mdx-js/mdx';
import remarkGfm from 'remark-gfm';
import remarkDirective from 'remark-directive';
import remarkMath from 'remark-math';
import {publicMarkdownFromTree, publicMarkdownImplementationId} from '../public-markdown.mjs';
import {compileSite} from '../compiler.mjs';

async function candidate(source, options = {}) {
  let result;
  await compile(source, {remarkPlugins: [remarkGfm, remarkDirective, remarkMath,
    function () { return tree => { result = publicMarkdownFromTree(tree, source, options); }; }]});
  return result;
}

const tabs = `<Tabs defaultValue="two" groupId="choice">
<TabItem value="one" label="First">

First body [guide](./guide.mdx?x=1#part).

</TabItem>
<TabItem value="two" label="Second">

Second body ![image](./image.png).

</TabItem>
</Tabs>`;
const note = `<Admonition type="warning" title="Keep &amp; explain">

Important **meaning** and a note[^a].

[^a]: Footnote kept.

</Admonition>`;

test('ordinary Markdown is preserved byte-for-byte, including code, links, CRLF and Unicode', async () => {
  const source = '\n\n---\r\nliteral body\r\n---\r\n# 😀\r\n\r\n[x][r]\r\n\r\n[r]: <a(b).md> "title"\r\n\r\n```md\r\n{/* PRIVATE-looking code */}\r\n<Unknown />\r\n```\r\n\r\n$math$\r\n';
  const result = await candidate(source);
  assert.equal(result.status, 'complete');
  assert.equal(result.markdown, source);
  assert.equal(result.implementationId, publicMarkdownImplementationId);
});

test('admonition kind/title/body, every tab and collapsed details body remain visible', async () => {
  const result = await candidate(`${note}\n\n${tabs}\n\n<Details summary="More">\n\nHidden by default, still important.\n\n</Details>\n`);
  assert.equal(result.status, 'complete');
  for (const text of ['warning', 'Keep', 'explain', 'Important **meaning**', 'Footnote kept.', '**First**', '**Second**', 'First body', 'Second body', '**More**', 'still important'])
    assert.ok(result.markdown.includes(text), text);
  assert.ok(result.markdown.includes('[guide](./guide.mdx?x=1#part)'));
  assert.ok(result.markdown.includes('![image](./image.png)'));
  assert.doesNotMatch(result.markdown, /<\/?(?:Tabs|TabItem|Admonition|Details)/);
});

test('known theme aliases and named runtime imports use their fixed static meanings', async () => {
  const source = `import Notice from '@theme/Admonition';\nimport {Details as More} from '@lithosharp/runtime';\n\n<Notice title="Visible">\n\nKept.\n\n</Notice>\n\n<More>\n\nAlso kept.\n\n</More>\n`;
  const result = await candidate(source);
  assert.equal(result.status, 'complete');
  assert.match(result.markdown, /Visible/);
  assert.match(result.markdown, /Also kept/);
  assert.doesNotMatch(result.markdown, /import Notice|@theme/);
});

test('known directives and AST comments transform without touching reader code examples', async () => {
  const source = ':::warning[Read this]\n\nKeep the warning.\n\n:::\n\nBefore {/* PRIVATE */} after.\n\n`{/* reader example */}`\n';
  const result = await candidate(source);
  assert.equal(result.status, 'complete');
  assert.match(result.markdown, /warning/);
  assert.match(result.markdown, /Read this/);
  assert.match(result.markdown, /Keep the warning/);
  assert.doesNotMatch(result.markdown, /PRIVATE|:::warning/);
  assert.match(result.markdown, /`\{\/\* reader example \*\/\}`/);
});

test('unknown components, expressions, imports, attributes and unsupported transforms fail closed', async () => {
  for (const source of [
    '<Unknown>\n\nDo not silently lose me.\n\n</Unknown>',
    'Value: {1 + 2}',
    "import Notice from './custom.mjs';\n\n<Notice />",
    'export const x = 1;\n\n# Text',
    "import {Details as DocCardList} from '@lithosharp/runtime';\n\n<DocCardList />",
    "import notice from '@theme/Admonition';\n\n<notice>Body.</notice>",
    "import {Details as Tabs} from '@lithosharp/runtime';\n\n<Tabs />",
    '<Admonition title={"dynamic"}>\n\nBody.\n\n</Admonition>',
    '<Tabs values={[{label: "A", value: "a"}]}><TabItem value="a">A</TabItem></Tabs>',
    '<Details hidden>\n\nImportant.\n\n</Details>',
    ':::unknown\n\nImportant.\n\n:::',
    '<TabItem label="Orphan">\n\nBody.\n\n</TabItem>',
    '> <Admonition>\n>\n> Body.\n>\n> </Admonition>',
  ]) {
    const result = await candidate(source, {staticComponents: ['Unknown']});
    assert.equal(result.status, 'unsupported', source);
    assert.equal(result.markdown, null);
  }
  for (const options of [{plugins: [{stage: 'remark'}]}, {componentsModule: './overrides.mjs'}, {preprocessedSource: '# Different'}])
    assert.equal((await candidate('# Body', options)).status, 'unsupported');
});

test('missing ranges cannot turn a wrapper into a complete Markdown body', () => {
  const result = publicMarkdownFromTree({type: 'root', children: [{type: 'mdxJsxFlowElement', name: 'Admonition', attributes: [], children: []}]}, '<Admonition />');
  assert.equal(result.status, 'unsupported');
  assert.equal(result.reason, 'unknownSourceRange');
});

test('the actual HTML worker projects only when opted in and separates on/off cache entries', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'lithosharp-public-mdx-'));
  try {
    const projectRoot = path.join(root, 'project'), workRoot = path.join(root, 'work');
    await mkdir(projectRoot); await mkdir(workRoot);
    const source = `${note}\n\n${tabs}\n`;
    await writeFile(path.join(projectRoot, 'page.mdx'), source);
    const request = {projectRoot, workRoot, assetBaseUrl: '/_mdx/', basePath: '/', hydration: 'selective',
      cacheable: true, publicMarkdown: true, resolutionFingerprint: 'public-markdown-fixture-v1', linkMap: {},
      sources: {'page.mdx': source}, pages: [{id: 'page', source: 'page.mdx', url: '/page/', title: 'Page', locale: 'en', props: {}}]};
    const offRequest = {...request};
    delete offRequest.publicMarkdown;
    const off = await compileSite(offRequest);
    assert.equal(off.workMetrics.mdxCompileInvocations, 1);
    assert.equal(Object.hasOwn(off.pages[0], 'publicMarkdown'), false);
    const cold = await compileSite(request);
    assert.equal(cold.workMetrics.mdxCompileInvocations, 1);
    assert.equal(cold.pages[0].publicMarkdown.status, 'complete');
    assert.match(cold.pages[0].html, /First body/);
    assert.match(cold.pages[0].html, /Second body/);
    assert.match(cold.pages[0].publicMarkdown.markdown, /First body/);
    assert.match(cold.pages[0].publicMarkdown.markdown, /Second body/);
    const warm = await compileSite(request);
    assert.equal(warm.workMetrics.mdxCompileInvocations, 0);
    assert.deepEqual(warm.pages[0].publicMarkdown, cold.pages[0].publicMarkdown);
    const offAgain = await compileSite({...request, publicMarkdown: false});
    assert.equal(offAgain.workMetrics.mdxCompileInvocations, 0);
    assert.equal(Object.hasOwn(offAgain.pages[0], 'publicMarkdown'), false);
    await writeFile(path.join(projectRoot, 'overrides.mjs'), 'export default {};\n');
    const changed = await compileSite({...request, componentsModule: './overrides.mjs'});
    assert.equal(changed.pages[0].publicMarkdown.status, 'unsupported');
    assert.equal(changed.pages[0].publicMarkdown.reason, 'componentOverrides');
  } finally { await rm(root, {recursive: true, force: true}); }
});
