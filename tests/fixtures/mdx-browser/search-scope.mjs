import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserType} from './browser.mjs';
const query = await readFile(new URL('../../../src/LithoSharp/Search/query.mjs', import.meta.url), 'utf8');
const client = (await readFile(new URL('../../../src/LithoSharp.Mdx/Browser/client.mjs', import.meta.url), 'utf8')).replace('/*__LITHOSHARP_LOCAL_SEARCH__*/', query).replace('export function start', 'function start');
const documents = [
  {title:'Target guide',collection:'guide',version:'v1',locale:'en',url:'https://fixture.test/guide',body:'target'},
  {title:'Target API',collection:'api',version:'v2',locale:'en',url:'https://fixture.test/api',body:'target'},
  {title:'Target general',url:'https://fixture.test/general',body:'target <script>alert(1)</script>'},
  {title:'Target Japanese',collection:'guide',version:'v1',locale:'ja',url:'https://fixture.test/ja',body:'target'}
];
const browser = await browserType.launch();
try {
  for (const site of [false,true]) {
    const page = await browser.newPage();
    await page.route('https://fixture.test/search.json', route => route.fulfill({json:site ? {documents} : documents}));
    await page.setContent(`<section data-ls-search data-index="https://fixture.test/search.json" data-locale="en" ${site ? 'data-search-scope="site"' : 'data-collection="guide" data-version="v1"'}><input><p role="status"></p><div data-results></div></section>`);
    await page.addScriptTag({content:client+'\nstart({basePath:"/",search:true});'});
    await page.locator('input').fill('target');
    await page.waitForFunction(() => document.querySelector('[role=status]').textContent.includes('results'));
    const titles = await page.locator('[data-results] article > a').allTextContents();
    assert.deepEqual(titles.sort(), (site ? ['Target API','Target general','Target guide'] : ['Target guide']).sort());
    assert.equal(await page.locator('[data-results] script').count(),0);
    if (site) assert.ok((await page.locator('[data-results]').textContent()).includes('<script>alert(1)</script>'));
    await page.close();
  }
  console.log('Search scope: legacy collection array, site object, cross-collection/version, locale-neutral, locale exclusion and safe rendering passed');
} finally { await browser.close(); }
