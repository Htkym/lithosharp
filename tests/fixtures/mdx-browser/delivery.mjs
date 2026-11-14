// Serve a built-in template with UseExternalThemeRestoration=true and the CSP
// described in docs/docs-template.md. Pass its complete root or sub-path URL.
import assert from 'node:assert/strict';
import { browserType, browserName } from './browser.mjs';

const base = new URL(process.argv[2] ?? 'http://127.0.0.1:4317/');
if (!base.pathname.endsWith('/')) throw new Error('Pass a base URL ending in /.');
const browser = await browserType.launch();
try {
  const context = await browser.newContext({ colorScheme: 'light', viewport: { width: 390, height: 900 } });
  const page = await context.newPage();
  page.setDefaultTimeout(15000);
  const errors = [];
  const violations = [];
  page.on('pageerror', error => errors.push(error.message));
  await context.exposeBinding('reportDeliveryViolation', (_, value) => violations.push(value));
  await context.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', event => reportDeliveryViolation(`${event.violatedDirective}: ${event.blockedURI}`));
  });
  const response = await page.goto(new URL('index.html', base).href);
  assert.equal(response.status(), 200);
  const csp = response.headers()['content-security-policy'];
  assert.ok(csp && /script-src\s+'self'/.test(csp), 'Serve this fixture with an enforced same-origin script CSP.');
  assert.ok(!csp.includes("'unsafe-inline'") && !csp.includes("'unsafe-eval'"), 'CSP permits inline or eval execution.');
  assert.equal(await page.locator('script:not([src]):not([type="application/json"]):not([type="application/ld+json"])').count(), 0);
  const bootstrap = page.locator('script[src$="/assets/theme.js"]');
  assert.equal(await bootstrap.count(), 1);
  assert.equal(await bootstrap.getAttribute('defer'), null);
  assert.ok((await bootstrap.getAttribute('src')).startsWith(base.pathname));
  const toggle = page.locator('[data-site-theme-toggle]');
  await toggle.click();
  assert.equal(await toggle.getAttribute('aria-pressed'), 'true');
  await page.reload();
  assert.equal(await page.locator('html').getAttribute('data-site-theme'), 'dark');
  assert.equal(await toggle.getAttribute('aria-pressed'), 'true');

  const menu = page.locator('[data-docs-menu-toggle], [data-site-menu-toggle]').first();
  await menu.click();
  assert.equal(await menu.getAttribute('aria-expanded'), 'true');
  await page.keyboard.press('Escape');
  assert.equal(await menu.getAttribute('aria-expanded'), 'false');
  assert.equal(await menu.evaluate(element => element === document.activeElement), true);

  const missing = await page.goto(new URL('missing/deep/page/', base).href);
  assert.equal(missing.status(), 404);
  assert.equal(await page.locator('meta[name="robots"]').getAttribute('content'), 'noindex');
  const homeLink = page.locator('main a[href]').filter({ hasText: /.+/ }).first();
  assert.equal(new URL(await homeLink.getAttribute('href'), page.url()).pathname, new URL('index.html', base).pathname);
  assert.equal(await page.locator('html').getAttribute('data-site-theme'), 'dark');
  await homeLink.click();
  assert.equal(new URL(page.url()).pathname, new URL('index.html', base).pathname);
  assert.deepEqual(violations, []);
  assert.deepEqual(errors, []);

  const blocked = await browser.newContext({ colorScheme: 'light' });
  await blocked.addInitScript(() => Object.defineProperty(window, 'localStorage', {
    get() { throw new DOMException('Blocked', 'SecurityError'); }
  }));
  const blockedPage = await blocked.newPage();
  await blockedPage.goto(new URL('index.html', base).href);
  await blockedPage.locator('[data-site-theme-toggle]').click();
  assert.equal(await blockedPage.locator('[data-site-theme-toggle]').getAttribute('aria-pressed'), 'true');
  await blocked.close();
  console.log(JSON.stringify({ browser: browserName, browserVersion: browser.version(), basePath: base.pathname, csp: 'passed', theme: 'passed', blockedStorage: 'passed', menuKeyboard: 'passed', missingPageStatus: 404, safariVerified: false }));
  await context.close();
} finally { await browser.close(); }
