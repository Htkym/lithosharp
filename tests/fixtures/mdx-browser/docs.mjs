// Generate DocsSample and serve its output with serve.mjs before running this check.
// node tests/fixtures/mdx-browser/docs.mjs http://127.0.0.1:4328
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const browser = await chromium.launch();
const url = new URL('/posts/getting-started/installation.html', process.argv[2] ?? 'http://127.0.0.1:4328').href;
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
      if (width <= 832) {
        const toggle = page.locator('[data-docs-menu-toggle]');
        await toggle.click();
        assert.equal(await sidebar.isVisible(), true);
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
  const noScript = await browser.newContext({ javaScriptEnabled: false, viewport: { width: 390, height: 900 } });
  const fallback = await noScript.newPage();
  await fallback.goto(url);
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
  console.log('Docs: light/dark layouts, system preference, saved choice, blocked storage, menu, skip link and no-JavaScript checks passed.');
} finally {
  await browser.close();
}
