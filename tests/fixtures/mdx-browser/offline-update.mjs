import {chromium} from 'playwright';
const origin = process.argv[2];
if (!origin) throw new Error('Serve current, next and retired sample revisions with serve.mjs, then pass its origin.');
const browser = await chromium.launch();
try {
  const page = await browser.newPage();
  await page.goto(origin + '/guide/static/');
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.waitForFunction(() => !!navigator.serviceWorker.controller);
  await page.reload();
  await page.evaluate(() => caches.open('unrelated-fixture'));
  await page.request.post(origin + '/_fixture/next');
  await page.evaluate(async () => (await navigator.serviceWorker.ready).update());
  await page.locator('[data-ls-update]').click();
  await page.waitForFunction(() => document.title.includes('Next revision'));
  const revisions = await page.evaluate(async () => (await caches.keys()).filter(key => key.startsWith('lithosharp:/:')));
  if (revisions.length !== 2) throw new Error('The update must keep the new and one previous cache generation.');
  await page.request.post(origin + '/_fixture/next');
  let retirementNavigations = 0;
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) retirementNavigations++; });
  const retirement = page.waitForNavigation({waitUntil: 'networkidle'});
  await page.evaluate(async () => { const registration = await navigator.serviceWorker.ready; void registration.update(); });
  await retirement;
  if (retirementNavigations !== 1) throw new Error('Retirement must reload the document exactly once.');
  await page.waitForFunction(async () => !navigator.serviceWorker.controller && (await navigator.serviceWorker.getRegistrations()).length === 0 && !(await caches.keys()).some(key => key.startsWith('lithosharp:/:')));
  if (!await page.evaluate(async () => (await caches.keys()).includes('unrelated-fixture'))) throw new Error('Retirement removed an unrelated cache.');
  console.log(JSON.stringify({updateNotification: 'passed', atomicRevision: 'passed', generations: revisions.length, retirement: 'passed', unrelatedCache: 'preserved'}));
} finally { await browser.close(); }
