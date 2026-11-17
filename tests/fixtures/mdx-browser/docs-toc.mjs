// Runs in normal CI only; the fixture uses the shipped shared module unchanged.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserType} from './browser.mjs';
const script = await readFile(new URL('../../../src/LithoSharp/Browser/toc.mjs',import.meta.url),'utf8');
const browser = await browserType.launch();
try {
  const page = await browser.newPage({viewport:{width:1440,height:900}});
  await page.route('https://fixture.test/**',route=>route.fulfill({contentType:'text/html',body:`<!doctype html><html><head><style>body{margin:0}.docs-header{position:sticky;top:0;height:80px}h2{scroll-margin-top:96px}section{height:900px}.docs-toc{position:fixed;right:0;top:90px}</style></head><body class="docs-body"><header class="docs-header">Header</header><aside class="docs-toc"><a href="#a">A</a><a href="#b">B</a><a href="#c">C</a></aside><main><article class="docs-content"><section><h2 id="a">A</h2></section><section><h2 id="b">B</h2></section><section><h2 id="c">C</h2></section></article></main><footer style="height:500px">Footer</footer></body></html>`}));
  await page.goto('https://fixture.test/guide/#b');
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addScriptTag({type:'module',content:script});
  const active=async id=>{await page.waitForFunction(id=>document.querySelector('[aria-current=location]')?.getAttribute('href')==='#'+id,id);assert.equal(await page.locator('[aria-current=location]').count(),1);};
  await active('b');
  await page.locator('.docs-toc a[href="#a"]').focus();await page.keyboard.press('Enter');await active('a');
  await page.goBack();await active('b');
  await page.evaluate(()=>scrollTo(0,document.documentElement.scrollHeight));await active('c');
  await page.setViewportSize({width:390,height:844});await active('c');
  await page.evaluate(()=>{document.dispatchEvent(new CustomEvent('lithosharp:before-navigate'));document.querySelector('.docs-toc').remove();document.dispatchEvent(new CustomEvent('lithosharp:page'));});
  assert.equal(await page.locator('[aria-current=location]').count(),0);assert.deepEqual(errors,[]);
  await page.close();
  console.log('Docs TOC: initial hash, keyboard anchor, history, end, resize and SPA cleanup passed');
} finally {await browser.close();}
