async page => {
  const origin = 'http://127.0.0.1:4317';
  const failures = [];
  page.on('pageerror', error => failures.push(String(error)));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('404')) failures.push(message.text()); });
  await page.addInitScript(() => {
    globalThis.hydrationErrors = [];
    document.addEventListener('lithosharp:hydration-error', event => hydrationErrors.push(event.detail));
    document.addEventListener('lithosharp:chunk-error', event => hydrationErrors.push(event.detail));
  });
  function check(value, message) { if (!value) throw new Error(message); }
  await page.goto(origin + '/guide/interactive/');
  await page.getByRole('button', {name: 'Count 3', exact: true}).click();
  check(await page.getByRole('button', {name: 'Count 4', exact: true}).count() === 1, 'Counter did not hydrate');
  await page.getByRole('tab', {name: '日本語', exact: true}).first().click();
  check(await page.locator('[role=tab][aria-selected=true]').allTextContents().then(values => values.every(value => value === '日本語')), 'Tabs did not synchronize');
  await page.getByRole('tab', {name: '日本語', exact: true}).first().press('ArrowLeft');
  check(await page.locator('[role=tab][aria-selected=true]').allTextContents().then(values => values.every(value => value === 'English')), 'Keyboard tab selection failed');
  check(await page.evaluate(() => hydrationErrors.length) === 0, 'Page hydration mismatch');
  await page.setViewportSize({width: 640, height: 600});
  await page.goto(origin + '/guide/islands/');
  await page.waitForFunction(() => {
    const registry = globalThis[Symbol.for('lithosharp.islands')];
    return ['load','idle'].every(strategy => registry?.get(document.querySelector(`[data-island=${strategy}]`))?.root);
  });
  check(await page.evaluate(() => !globalThis[Symbol.for('lithosharp.islands')].get(document.querySelector('[data-island=media]')).root), 'Media island started before the query matched');
  check(await page.evaluate(() => !globalThis[Symbol.for('lithosharp.islands')].get(document.querySelector('[data-island=manual]')).root), 'Manual island started automatically');
  await page.getByRole('button', {name: 'Count 10', exact: true}).click();
  await page.getByRole('button', {name: 'Count 20', exact: true}).click();
  await page.locator('[data-island=visible]').scrollIntoViewIfNeeded();
  await page.waitForFunction(() => globalThis[Symbol.for('lithosharp.islands')].get(document.querySelector('[data-island=visible]')).root);
  await page.getByRole('button', {name: 'Count 30', exact: true}).click();
  await page.setViewportSize({width: 1200, height: 800});
  await page.waitForFunction(() => globalThis[Symbol.for('lithosharp.islands')].get(document.querySelector('[data-island=media]')).root);
  await page.getByRole('button', {name: 'Count 40', exact: true}).click();
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('lithosharp:hydrate', {detail: {id: document.querySelector('[data-island=manual]').id}})));
  await page.waitForFunction(() => globalThis[Symbol.for('lithosharp.islands')].get(document.querySelector('[data-island=manual]')).root);
  await page.getByRole('button', {name: 'Count 50', exact: true}).click();
  const counts = await page.locator('[data-island] button').allTextContents();
  check(JSON.stringify(counts) === JSON.stringify(['Count 11','Count 21','Count 31','Count 41','Count 51']), 'One or more Island strategies failed');
  check(await page.evaluate(() => hydrationErrors.length) === 0, 'Island hydration mismatch');
  const resources = await page.evaluate(() => performance.getEntriesByType('resource').filter(entry => entry.name.endsWith('.js')).map(entry => ({name: new URL(entry.name).pathname, bytes: entry.decodedBodySize, transfer: entry.transferSize, duration: entry.duration})));
  await page.goto(origin + '/guide/static/');
  check(await page.locator('script[src*="/_mdx/"]').count() === 0, 'Static MDX received a hydration entry');
  check(await page.evaluate(() => performance.getEntriesByType('resource').filter(entry => entry.name.includes('/_mdx/') && entry.name.endsWith('.js')).length) === 0, 'Static MDX fetched React chunks');
  await page.evaluate(() => { globalThis.navigationCanary = 'preserved'; });
  await page.locator('#docs-sidebar a[href="/guide/interactive/"]').click();
  await page.waitForURL('**/guide/interactive/');
  await page.getByRole('button', {name: 'Count 3', exact: true}).click();
  check(await page.getByRole('button', {name: 'Count 4', exact: true}).count() === 1, 'Navigation did not mount the page');
  check(await page.evaluate(() => navigationCanary) === 'preserved', 'Internal navigation performed a full reload');
  await page.locator('#docs-sidebar a[href="/guide/islands/"]').click();
  await page.waitForURL('**/guide/islands/');
  await page.waitForFunction(() => globalThis[Symbol.for('lithosharp.islands')]?.size === 5);
  check(await page.evaluate(() => globalThis[Symbol.for('lithosharp.react-roots')]?.size ?? 0) === 0, 'Page root was leaked');
  await page.goBack();
  await page.waitForURL('**/guide/interactive/');
  await page.getByRole('button', {name: 'Count 3', exact: true}).click();
  check(await page.getByRole('button', {name: 'Count 4', exact: true}).count() === 1, 'History navigation did not remount');
  check(await page.evaluate(() => globalThis[Symbol.for('lithosharp.islands')].size) === 0, 'Island roots were leaked');
  check(await page.evaluate(() => hydrationErrors.length) === 0, 'Navigation hydration mismatch');
  const noJs = await page.context().browser().newContext({javaScriptEnabled: false});
  try {
    const fallback = await noJs.newPage();
    await fallback.goto(origin + '/guide/interactive/');
    check(await fallback.getByText('日本語の本文', {exact: true}).isVisible(), 'No-JS tab content is hidden');
    check(await fallback.getByRole('button', {name: 'Count 3', exact: true}).isVisible(), 'No-JS counter fallback is missing');
  } finally { await noJs.close(); }
  check(failures.length === 0, failures.join('\n'));
  return {counter: 'passed', synchronizedTabs: 'passed', keyboard: 'passed', strategies: 5, hydrationErrors: 0, staticJsRequests: 0, noJs: 'passed', islandResources: resources};
}
