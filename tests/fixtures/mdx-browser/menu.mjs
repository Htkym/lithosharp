// Focused checks of both shipped menu controllers against the shared CSS contract.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserType} from './browser.mjs';
const generator = await readFile(new URL('../../../src/LithoSharp/SiteGenerator.cs', import.meta.url), 'utf8');
const docsCss = generator.split('private static string BuildDocsCss')[1].split('+ """')[1].split('"""')[0];
const core = generator.split('private static string BuildDocsScript')[1].split('+ """')[1].split('"""')[0];
const client = await readFile(new URL('../../../src/LithoSharp.Mdx/Browser/client.mjs', import.meta.url), 'utf8');
const theme = await readFile(new URL('../../../src/LithoSharp.Mdx/Browser/theme.css', import.meta.url), 'utf8');
const body = '<header class="docs-header"><button class="docs-menu-toggle" data-docs-menu-toggle aria-controls="docs-sidebar" aria-expanded="false">Menu</button></header><div class="docs-shell"><aside id="docs-sidebar" class="docs-sidebar" data-docs-sidebar><a href="#main">Introduction</a></aside><main id="main" class="docs-main">Article</main><nav class="docs-toc post-toc">Contents</nav></div>';
const browser = await browserType.launch();
try {
  for (const [name, script] of [['core', core], ['mdx', client.replace('export function start', 'function start') + '\nstart({basePath:"/"});']]) {
    const page = await browser.newPage({viewport:{width:1440,height:900}});
    await page.setContent(`<style>${docsCss}\n${theme}</style><body class="docs-body">${body}</body>`);
    const toggle = page.locator('[data-docs-menu-toggle]'), sidebar = page.locator('[data-docs-sidebar]');
    assert.equal(await sidebar.isVisible(), true, name + ' no-JS sidebar');
    assert.equal(await toggle.isVisible(), false, name + ' no-JS toggle');
    await page.addScriptTag({content:script});
    const state = async open => {
      assert.equal(await toggle.getAttribute('aria-expanded'), String(open), name + ' aria');
      assert.equal(await sidebar.isVisible(), open, name + ' visibility');
      assert.equal(await sidebar.evaluate(el => el.inert), !open, name + ' inert');
    };
    await state(true);
    await toggle.focus(); await page.keyboard.press('Enter'); await state(false);
    await page.setViewportSize({width:1280,height:900}); await state(false);
    if (name === 'mdx') {
      await page.evaluate(html => { document.dispatchEvent(new CustomEvent('lithosharp:before-navigate')); document.body.innerHTML = html; globalThis[Symbol.for('lithosharp.client')].page(); }, body);
      await state(false);
    }
    await toggle.focus(); await page.keyboard.press('Space'); await state(true);
    await sidebar.locator('a').click(); await state(true);
    await page.keyboard.press('Escape'); await state(false);
    assert.equal(await toggle.evaluate(el => el === document.activeElement), true);
    await toggle.click(); await state(true);
    await sidebar.locator('a').focus();
    await page.setViewportSize({width:390,height:900});
    await page.waitForFunction(() => document.querySelector('[data-docs-menu-toggle]').getAttribute('aria-expanded') === 'false');
    await state(false);
    assert.equal(await toggle.evaluate(el => el === document.activeElement), true, name + ' resize focus');
    await toggle.click(); await state(true);
    await sidebar.locator('a').click(); await state(false);
    await page.setViewportSize({width:1440,height:900});
    await page.waitForFunction(() => document.querySelector('[data-docs-menu-toggle]').getAttribute('aria-expanded') === 'true');
    await state(true);
    await toggle.click();
    const columns = await page.locator('.docs-shell').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
    assert.equal(columns, 2, name + ' collapsed desktop columns');
    await page.close();
    console.log(name + ': desktop/mobile, keyboard, ARIA, inert, resize, link behavior and no-JS passed');
  }
} finally { await browser.close(); }
