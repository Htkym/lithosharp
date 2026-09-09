// Generate BlogSample and serve it with serve.mjs before running this check.
// node tests/fixtures/mdx-browser/blog.mjs http://127.0.0.1:4329
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://127.0.0.1:4329';
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ reducedMotion: 'reduce' });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  for (const colorScheme of ['light', 'dark']) {
    await page.emulateMedia({ colorScheme });
    for (const width of [1440, 768, 390, 320]) {
      await page.setViewportSize({ width, height: 900 });
      for (const route of ['/', '/archives.html', '/tags.html', '/search.html', '/posts/getting-started.html']) {
        const response = await page.goto(new URL(route, base).href);
        assert.equal(response.status(), 200);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `${route} at ${width}`);
        assert.equal(await page.locator('[data-site-theme-toggle]').getAttribute('aria-pressed'), String(colorScheme === 'dark'));
      }
      const menu = page.locator('[data-site-menu-toggle]');
      await menu.click();
      assert.equal(await page.locator('[data-site-nav]').isVisible(), true);
      await page.keyboard.press('Escape');
      assert.equal(await page.locator('[data-site-nav]').isVisible(), false);
      assert.equal(await menu.evaluate(el => el === document.activeElement), true);
    }
  }
  await page.goto(base);
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'blog-main');
  await page.locator('[data-site-theme-toggle]').click();
  await page.reload();
  assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), 'light');
  await page.locator('#header-search-input').fill('API');
  await page.locator('#header-search-input').press('Enter');
  await page.locator('#search-results a').first().waitFor({ state: 'visible' });
  assert.match(await page.locator('#search-results').innerText(), /Getting started with the API/);
  const noScript = await browser.newContext({ javaScriptEnabled: false, viewport: { width: 390, height: 900 } });
  const fallback = await noScript.newPage();
  await fallback.goto(base);
  assert.equal(await fallback.locator('[data-site-nav]').isVisible(), true);
  assert.equal(await fallback.locator('[data-site-menu-toggle]').isVisible(), false);
  assert.equal(await fallback.locator('[data-site-theme-toggle]').isVisible(), false);
  assert.deepEqual(errors, []);
  console.log('Blog: all page types in light/dark, mobile overflow, menu, skip link, theme persistence, search and no-JavaScript navigation passed.');
} finally {
  await browser.close();
}
