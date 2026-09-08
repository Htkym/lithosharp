async page => {
  const origin = 'http://127.0.0.1:4317';
  function check(value, message) { if (!value) throw new Error(message); }
  await page.goto(origin + '/guide/live/');
  await page.getByRole('button', {name: 'Run', exact: true}).click();
  const frame = page.frameLocator('iframe[title="React playground"]');
  await frame.getByRole('button', {name: 'Live 1', exact: true}).click();
  check(await frame.getByRole('button', {name: 'Live 2', exact: true}).count() === 1, 'Live React did not run');
  await page.getByRole('textbox').fill('render(React.createElement("p",null,parent.document.title));');
  await page.getByRole('button', {name: 'Run', exact: true}).click();
  await page.waitForFunction(() => document.querySelector('[role=alert]')?.textContent.includes('SecurityError'));
  await page.getByRole('textbox').fill('throw new Error("edited-example-error")');
  await page.getByRole('button', {name: 'Run', exact: true}).click();
  await page.waitForFunction(() => document.querySelector('[role=alert]')?.textContent.includes('edited-example-error'));
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.goto(origin + '/guide/static/');
  await page.waitForFunction(() => !!navigator.serviceWorker.controller);
  const cacheKeys = await page.evaluate(() => caches.keys());
  check(cacheKeys.some(key => key.startsWith('lithosharp:/:')), 'Offline cache not installed');
  await page.context().setOffline(true);
  try {
    await page.goto(origin + '/guide/interactive/');
    await page.getByRole('button', {name: 'Count 3', exact: true}).click();
    check(await page.getByRole('button', {name: 'Count 4', exact: true}).count() === 1, 'Offline HTML and chunks are inconsistent');
  } finally { await page.context().setOffline(false); }
  return {liveEditing: 'passed', sandboxParentAccess: 'blocked', errors: 'displayed', offlineHydration: 'passed', cacheKeys};
}
