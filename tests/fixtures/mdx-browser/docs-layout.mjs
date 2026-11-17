import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserType} from './browser.mjs';
const generator = await readFile(new URL('../../../src/LithoSharp/SiteGenerator.cs', import.meta.url), 'utf8');
const css = generator.split('private static string BuildDocsCss')[1].split('+ """')[1].split('"""')[0];
const theme = await readFile(new URL('../../../src/LithoSharp.Mdx/Browser/theme.css', import.meta.url), 'utf8');
const browser = await browserType.launch();
try {
  const page = await browser.newPage();
  const positions = new Map();
  for (const width of [1440,1024,390]) for (const toc of [false,true]) for (const sidebar of [false,true]) for (const collapsed of [false,true]) {
    await page.setViewportSize({width,height:900});
    await page.setContent(`<style>${css}\n${theme}</style><body class="docs-body ${collapsed?'docs-enhanced docs-sidebar-collapsed':''}"><div class="docs-shell ${toc?'':'docs-shell-no-toc'} ${sidebar?'':'docs-shell-no-sidebar'}">${sidebar?'<aside class="docs-sidebar">Navigation</aside>':''}${toc?'<nav class="docs-toc post-toc">Contents</nav>':''}<main class="docs-main"><article class="docs-content"><h1>Documentation heading 日本語</h1><p style="height:400px">Article</p></article></main></div></body>`);
    const label = JSON.stringify({width,toc,sidebar,collapsed});
    const main = await page.locator('main').boundingBox();
    const columns = await page.locator('.docs-shell').evaluate(el=>getComputedStyle(el).gridTemplateColumns.split(' ').length);
    const activeSidebar = sidebar && !(collapsed && width > 832);
    assert.equal(columns, width<=832 ? 1 : width<=1120 ? (activeSidebar?2:1) : 2+(activeSidebar?1:0),label+' columns');
    assert.ok(main.width > 200,label+' usable article');
    if (width > 1120) {
      const key = JSON.stringify({width,sidebar,collapsed});
      if (!toc) positions.set(key,main);
      else { const previous=positions.get(key); assert.ok(Math.abs(previous.x-main.x)<1 && Math.abs(previous.width-main.width)<1,label+' stable article with/without TOC'); }
    }
    if(toc && width<=1120) { const bounds=await page.locator('.post-toc').boundingBox(); assert.ok(bounds.y+bounds.height<=main.y,label+' TOC precedes article'); }
    assert.ok(await page.locator('h1').evaluate(el=>parseFloat(getComputedStyle(el).fontSize)<=40),label+' heading');
  }
  await page.addStyleTag({content:'.docs-content h1{font-family:monospace;font-size:20px}'});
  assert.equal(await page.locator('h1').evaluate(el=>getComputedStyle(el).fontSize),'20px','custom styles remain effective');
  console.log('Docs layout: 48 viewport/TOC/sidebar/no-JS/collapsed combinations and custom heading override passed');
} finally { await browser.close(); }
