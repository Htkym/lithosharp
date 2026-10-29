// Generate DocsSample and serve its output with serve.mjs before running this check.
// node tests/fixtures/mdx-browser/docs.mjs http://127.0.0.1:4328
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const browser = await chromium.launch();
const base = (process.argv[2] ?? 'http://127.0.0.1:4328/').replace(/\/?$/, '/');
const url = new URL('posts/getting-started/installation.html', base).href;
try {
  const page = await browser.newPage();
  for (const colorScheme of ['light', 'dark']) {
    await page.emulateMedia({ colorScheme });
    for (const width of [1440, 1024, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(url);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
      const sidebar = page.locator('[data-docs-sidebar]');
      assert.equal(await sidebar.isVisible(), width > 832);
      assert.equal(await page.locator('.docs-toc').evaluate(el => el.scrollWidth <= el.clientWidth), true);
      assert.equal(await page.locator('[data-site-theme-toggle]').getAttribute('aria-pressed'), String(colorScheme === 'dark'));
      if (width === 320) {
        await page.locator('.docs-brand').evaluate(el => {
          el.textContent = 'LithoSharpDocumentationGuide / 日本語の長いサイト名でも読めるドキュメント';
        });
        await page.waitForFunction(() => {
          const measured = parseFloat(getComputedStyle(document.body).getPropertyValue('--docs-header-height'));
          return Math.abs(measured - document.querySelector('.docs-header').getBoundingClientRect().height) < 1;
        });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'Long Docs brand at 320px');
      }
      if (width <= 832) {
        const toggle = page.locator('[data-docs-menu-toggle]');
        await toggle.click();
        assert.equal(await sidebar.isVisible(), true);
        const headerBounds = await page.locator('.docs-header').boundingBox();
        const sidebarBounds = await sidebar.boundingBox();
        assert.ok(Math.abs(sidebarBounds.y - headerBounds.y - headerBounds.height) < 1, 'Docs menu starts below the full header');
        await page.keyboard.press('Escape');
        assert.equal(await sidebar.isVisible(), false);
        assert.equal(await toggle.getAttribute('aria-expanded'), 'false');
        assert.equal(await toggle.evaluate(el => el === document.activeElement), true);
      }
    }
  }
  await page.emulateMedia({ colorScheme: 'light' });
  const themeToggle = page.locator('[data-site-theme-toggle]');
  await themeToggle.click();
  await page.reload();
  assert.equal(await themeToggle.getAttribute('aria-pressed'), 'true');
  assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), 'dark');
  await themeToggle.click();
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.reload();
  assert.equal(await themeToggle.getAttribute('aria-pressed'), 'false');
  assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), 'light');
  await page.goto(url);
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'docs-main');
  await page.locator('[data-docs-menu-toggle]').click();
  await page.locator('[data-docs-sidebar] a[href$="/configuration.html"]').click();
  assert.ok(page.url().endsWith('/configuration.html'), 'Docs sidebar link navigates');
  await page.goto(url);
  const headerSearch = page.locator('header form[role="search"]');
  if (await headerSearch.count()) {
    const target = await headerSearch.locator('button').boundingBox();
    assert.ok(target.width >= 44 && target.height >= 44, 'Docs header search target');
    await headerSearch.locator('input[name="q"]').fill('LithoSharp');
    await headerSearch.locator('button').click();
    const result = page.locator('#search-results a').first();
    await result.waitFor({ state: 'visible' });
    assert.equal(await page.locator('header form[role="search"]').count(), 0);
    await result.click();
    assert.ok(!new URL(page.url()).pathname.endsWith('/search.html'), 'Docs search result navigates');
  }
  const noScript = await browser.newContext({ javaScriptEnabled: false, viewport: { width: 390, height: 900 } });
  const fallback = await noScript.newPage();
  await fallback.goto(url);
  assert.equal(await fallback.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'Docs no-JavaScript overflow');
  assert.equal(await fallback.locator('[data-docs-sidebar]').isVisible(), true);
  assert.equal(await fallback.locator('[data-docs-menu-toggle]').isVisible(), false);
  assert.equal(await fallback.locator('[data-site-theme-toggle]').isVisible(), false);
  const blockedStorage = await browser.newContext({ colorScheme: 'light' });
  await blockedStorage.addInitScript(() => {
    Object.defineProperty(window, 'localStorage', { get() { throw new DOMException('Blocked', 'SecurityError'); } });
  });
  const blocked = await blockedStorage.newPage();
  await blocked.goto(url);
  await blocked.locator('[data-site-theme-toggle]').click();
  assert.equal(await blocked.locator('[data-site-theme-toggle]').getAttribute('aria-pressed'), 'true');
  console.log('Docs: light/dark layouts, system preference, saved choice, blocked storage, menu, sidebar navigation, skip link, optional header search and no-JavaScript checks passed.');
} finally {
  await browser.close();
}
