async page => {
  const browser = page.context().browser();
  const fallback = await browser.newContext({serviceWorkers: 'block'});
  try {
    await fallback.addInitScript(() => {
      for (const name of ['requestIdleCallback', 'IntersectionObserver', 'matchMedia']) Object.defineProperty(window, name, {value: undefined, configurable: true});
    });
    const tab = await fallback.newPage();
    await tab.goto('http://127.0.0.1:4317/guide/islands/');
    await tab.waitForFunction(() => [...(globalThis[Symbol.for('lithosharp.islands')]?.values() ?? [])].filter(value => value.root).length === 4);
    for (const value of [10, 20, 30, 40]) {
      await tab.getByRole('button', {name: 'Count ' + value, exact: true}).click();
      if (await tab.getByRole('button', {name: 'Count ' + (value + 1), exact: true}).count() !== 1) throw new Error('Missing API fallback did not hydrate.');
    }
  } finally { await fallback.close(); }
  const failure = await browser.newContext({serviceWorkers: 'block'});
  try {
    const tab = await failure.newPage();
    await tab.route('**/_mdx/chunks/Counter-*.js', route => route.abort());
    await tab.goto('http://127.0.0.1:4317/guide/islands/');
    await tab.locator('[data-ls-chunk-retry]').waitFor();
    if (!await tab.getByRole('button', {name: 'Count 10', exact: true}).isVisible()) throw new Error('Chunk failure removed the static fallback.');
    await tab.unroute('**/_mdx/chunks/Counter-*.js');
    await tab.locator('[data-ls-chunk-retry]').click();
    await tab.getByRole('button', {name: 'Count 10', exact: true}).click();
    if (await tab.getByRole('button', {name: 'Count 11', exact: true}).count() !== 1) throw new Error('Normal reload did not recover a failed chunk.');
  } finally { await failure.close(); }
  return {missingBrowserApis: 'passed', chunkFailureStaticFallback: 'passed', normalReloadRecovery: 'passed'};
}
