export default async (page, origin) => {
  const failures = [];
  page.on('pageerror', error => failures.push(String(error)));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('404')) failures.push(message.text()); });
  function check(value, message) { if (!value) throw new Error(message); }
  const base = origin + '/sub';

  const first = await page.goto(base + '/guide/a/');
  check(first.status() === 200, 'Sub-path page did not return 200.');
  const heading = await page.getByRole('heading', {name: 'Page A', exact: true}).textContent();
  check((heading ?? '').trim() === 'Page A', 'Sub-path page body mismatch.');

  const scripts = await page.locator('script[src]').evaluateAll(elements => elements.map(element => element.getAttribute('src')));
  check(scripts.length > 0, 'Sub-path page loads no scripts.');
  check(scripts.every(src => src.startsWith('/sub/')), 'Script URL escapes the base path: ' + JSON.stringify(scripts));
  check(scripts.some(src => /-[0-9A-Za-z_-]{4,}\.js$/.test(src)), 'No versioned (hashed) script URL: ' + JSON.stringify(scripts));
  const styles = await page.locator('link[rel=stylesheet][href]').evaluateAll(elements => elements.map(element => element.getAttribute('href')));
  check(styles.every(href => href.startsWith('/sub/')), 'Stylesheet URL escapes the base path.');

  const flat = await page.goto(base + '/guide/a');
  check(flat.status() === 200, 'Flat trailing-slash-less URL did not return 200.');
  check(((await page.getByRole('heading', {name: 'Page A', exact: true}).textContent()) ?? '').trim() === 'Page A', 'Flat URL serves different content.');

  await page.goto(base + '/guide/a/');
  await page.evaluate(() => { globalThis.navigationCanary = 'preserved'; });
  await page.locator('#docs-sidebar a[href="/sub/guide/b/"]').click();
  await page.waitForURL('**/sub/guide/b/');
  check(await page.evaluate(() => navigationCanary) === 'preserved', 'Sub-path navigation performed a full reload.');
  check(((await page.getByRole('heading', {name: 'Page B', exact: true}).textContent()) ?? '').trim() === 'Page B', 'Sub-path navigation landed on the wrong page.');

  const missing = await page.goto(base + '/no-such-page/');
  check(missing.status() === 404, 'Unknown sub-path did not return 404.');
  const outside = await page.goto(origin + '/other/');
  check(outside.status() === 404, 'Outside-base URL did not return 404.');

  check(failures.length === 0, failures.join('\n'));
  return {basePath: 'passed', versionedAssets: scripts.length, trailingSlash: 'passed', progressiveNavigation: 'passed', notFound: 'passed'};
};
