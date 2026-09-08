async page => {
  const context = await page.context().browser().newContext({serviceWorkers: 'block'});
  const views = [];
  try {
    const tab = await context.newPage();
    await tab.route('**/_docs/client-*.js', async route => {
      const response = await route.fetch();
      const body = (await response.text()).replace('"analytics":null', '"analytics":{"endpoint":"http://127.0.0.1:4317/metrics","domain":"fixture","provider":"json"}');
      await route.fulfill({response, body});
    });
    await tab.route('**/metrics', async route => { views.push(JSON.parse(route.request().postData())); await route.fulfill({status: 204, body: ''}); });
    await tab.goto('http://127.0.0.1:4317/guide/static/');
    if (views.length) throw new Error('Analytics ran without consent');
    await Promise.all([tab.waitForResponse('**/metrics'), tab.evaluate(() => document.dispatchEvent(new CustomEvent('lithosharp:consent', {detail: {granted: true}})))]);
    await Promise.all([tab.waitForResponse('**/metrics'), tab.locator('#docs-sidebar a[href="/guide/interactive/"]').click()]);
    await tab.waitForURL('**/guide/interactive/');
    await tab.getByRole('button', {name: 'Count 3', exact: true}).click();
    if (views.length !== 2) throw new Error('Expected one initial and one navigation page view');
    await tab.evaluate(() => document.dispatchEvent(new CustomEvent('lithosharp:consent', {detail: {granted: true}})));
    if (views.length !== 2) throw new Error('Repeated consent double-counted the page');
    await tab.evaluate(() => document.dispatchEvent(new CustomEvent('lithosharp:consent', {detail: {granted: false}})));
    await tab.locator('#docs-sidebar a[href="/guide/static/"]').click();
    await tab.waitForURL('**/guide/static/');
    if (views.length !== 2) throw new Error('Analytics ran after consent withdrawal');
    return {beforeConsent: 0, pageViews: views.length, duplicateViews: 0, afterWithdrawal: 0};
  } finally { await context.close(); }
}
