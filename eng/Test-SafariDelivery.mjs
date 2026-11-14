import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {createServer} from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createFixtureServer} from '../tests/fixtures/mdx-browser/serve.mjs';

const help = 'node eng/Test-SafariDelivery.mjs --fixture <candidate-fixture> --output <new-evidence-directory> [--firefox]';
const args = process.argv.slice(2);
if (args.includes('--help')) { console.log(help); process.exit(0); }
const options = {};
for (let index = 0; index < args.length; index++) {
  const key = args[index];
  if (key === '--firefox') options.firefox = true;
  else if (['--fixture', '--output'].includes(key) && args[index + 1]) options[key.slice(2)] = args[++index];
  else throw new Error(help);
}
if (!options.fixture || !options.output) throw new Error(help);
if (process.platform !== 'darwin') throw new Error('Actual Safari requires macOS and /usr/bin/safaridriver. WebKit is not Safari.');
const fixtureDirectory = path.resolve(options.fixture);
const output = path.resolve(options.output);
await mkdir(output, {recursive: false});
const receipt = JSON.parse(await readFile(path.join(fixtureDirectory, 'fixture.json'), 'utf8'));
assert.equal(receipt.schemaVersion, 1);
assert.equal(receipt.source, 'candidate-packages');
assert.deepEqual(receipt.placements.map(value => value.basePath), ['/', '/sub/']);
const policy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'";
const elementKey = 'element-6066-11e4-a52e-4f735466cecf';
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const evidence = {schemaVersion: 1, status: 'running', actualSafari: false, node: process.version,
  platform: process.platform, arch: process.arch, packages: receipt.packages, packageHashes: receipt.packageHashes,
  workerCompilerHash: receipt.workerCompilerHash, workerLockHash: receipt.workerLockHash, policy,
  diagnostics: 'External same-origin fixture probe inserted before generated head scripts; response status checked with Node HTTP fetch.',
  placements: [], firefox: []};
let driver, session, address, driverLog = '', activeServer;

async function command(executable, commandArgs, env = process.env) {
  const child = spawn(executable, commandArgs, {stdio: ['ignore', 'pipe', 'pipe'], env});
  let stdout = '', stderr = '';
  child.stdout.on('data', value => { stdout += value; });
  child.stderr.on('data', value => { stderr += value; });
  const [code, signal] = await once(child, 'exit');
  assert.equal(code, 0, `${executable} exited ${code ?? signal}: ${stderr}`);
  return {stdout: stdout.trim(), stderr: stderr.trim()};
}
async function webdriver(method, endpoint, body) {
  const response = await fetch(address + endpoint, {method,
    headers: body === undefined ? {} : {'Content-Type': 'application/json'},
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(45000)});
  const result = await response.json();
  if (!response.ok || result.value?.error) throw new Error(`WebDriver ${method} ${endpoint}: ${JSON.stringify(result.value)}`);
  return result.value;
}
const request = (method, endpoint, body) => webdriver(method, `/session/${session}${endpoint}`, body);
const execute = (fn, ...values) => request('POST', '/execute/sync', {script: `return (${fn}).apply(null, arguments);`, args: values});
async function wait(fn, label, ...values) {
  const started = performance.now();
  while (!await execute(fn, ...values)) {
    if (performance.now() - started > 20000) throw new Error(`Browser condition failed: ${label}`);
    await sleep(100);
  }
}
async function element(selector) {
  const value = await request('POST', '/element', {using: 'css selector', value: selector});
  return value[elementKey];
}
function pointerTargetState(selector, details = false) {
  const target = document.querySelector(selector), style = target && getComputedStyle(target);
  const rect = target?.getClientRects()[0];
  const left = Math.max(0, rect?.left ?? 0), right = Math.min(innerWidth, rect?.right ?? 0);
  const top = Math.max(0, rect?.top ?? 0), bottom = Math.min(innerHeight, rect?.bottom ?? 0);
  const center = right > left && bottom > top ? {x: Math.floor((left + right) / 2), y: Math.floor((top + bottom) / 2)} : null;
  const hit = center && document.elementFromPoint(center.x, center.y);
  const ready = !!(target?.isConnected && rect?.width > 0 && rect.height > 0 && style.visibility === 'visible' &&
    style.display !== 'none' && style.pointerEvents !== 'none' && hit && (hit === target || target.contains(hit)));
  if (!details) return ready;
  return {selector, ready, viewport: {innerWidth, innerHeight, outerWidth, outerHeight, devicePixelRatio, scrollX, scrollY},
    rect: rect ? {left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom, width: rect.width, height: rect.height} : null,
    style: style ? {display: style.display, visibility: style.visibility, pointerEvents: style.pointerEvents} : null,
    center, hit: hit ? {tag: hit.tagName, id: hit.id, class: hit.getAttribute('class')} : null};
}
async function waitForPointerTarget(selector, label) {
  try { await wait(pointerTargetState, label, selector); }
  catch (error) {
    try {
      const state = await execute(pointerTargetState, selector, true);
      evidence.pointerReadinessFailure = {label, state};
      error.message += '; pointer readiness: ' + JSON.stringify(state);
    }
    catch (diagnosticError) { error.message += '; pointer readiness diagnostics unavailable: ' + diagnosticError.message; }
    throw error;
  }
  return execute(pointerTargetState, selector, true);
}
const click = async selector => request('POST', `/element/${await element(selector)}/click`, {});
const keys = async (selector, text) => request('POST', `/element/${await element(selector)}/value`, {text});
async function clickText(text) {
  const value = await execute(text => [...document.querySelectorAll('button')].find(button => button.textContent.trim() === text), text);
  assert.ok(value?.[elementKey], `Missing button: ${text}`);
  await request('POST', `/element/${value[elementKey]}/click`, {});
}
async function navigate(url) {
  await request('POST', '/url', {url: url.href});
  await wait(() => document.readyState === 'complete', 'complete document');
}
async function checkHttp(url, status) {
  const response = await fetch(url, {signal: AbortSignal.timeout(15000)});
  assert.equal(response.status, status, url.href);
  assert.equal(response.headers.get('content-security-policy'), policy);
  await response.arrayBuffer();
}
async function checkPlacement(placement) {
  assert.ok(['root', 'sub'].includes(placement.directory));
  const events = [];
  activeServer = createFixtureServer({root: path.join(fixtureDirectory, placement.directory),
    basePath: placement.basePath, csp: policy, probe: true,
    onDeliveryEvent: event => { if (events.length < 100) events.push(event); }});
  activeServer.listen(0, '127.0.0.1');
  await once(activeServer, 'listening');
  const base = new URL(`http://127.0.0.1:${activeServer.address().port}${placement.basePath}`);
  const result = {name: placement.name, basePath: base.pathname, checks: [], events, layoutReadiness: []};
  evidence.placements.push(result);
  const home = new URL('index.html', base), pageA = new URL('guide/a/', base), pageB = new URL('guide/b/', base);
  await checkHttp(home, 200);
  await checkHttp(pageA, 200);
  await navigate(home);
  await execute(() => { localStorage.clear(); localStorage.setItem('lithosharp-theme', 'light'); });
  await request('POST', '/refresh', {});
  await wait(() => document.documentElement.dataset.siteTheme === 'light', 'light theme restored');
  const scripts = await execute(() => [...document.querySelectorAll('script[src]')].map(script => script.getAttribute('src')));
  assert.ok(scripts.length > 1 && scripts.every(src => src.startsWith(base.pathname)), JSON.stringify(scripts));
  assert.equal(await execute(() => document.querySelectorAll('script:not([src]):not([type="application/json"]):not([type="application/ld+json"])').length), 0);
  assert.equal(await execute(() => {
    const scripts = [...document.querySelectorAll('script[src$="/assets/theme.js"]')];
    return scripts.length === 1 && !scripts[0].hasAttribute('defer') && !scripts[0].hasAttribute('async');
  }), true);
  await click('[data-site-theme-toggle]');
  await wait(() => document.documentElement.dataset.siteTheme === 'dark', 'theme toggle');
  await request('POST', '/refresh', {});
  await wait(() => document.documentElement.dataset.siteTheme === 'dark' && document.querySelector('[data-site-theme-toggle]')?.getAttribute('aria-pressed') === 'true', 'persisted theme');
  result.checks.push('strict-csp-external-theme', 'theme-persistence');

  await request('POST', '/window/rect', {width: 640, height: 900});
  const menu = '[data-docs-menu-toggle], [data-site-menu-toggle]';
  result.layoutReadiness.push({phase: 'narrow-menu', state: await waitForPointerTarget(menu, 'narrow menu pointer readiness')});
  await click(menu);
  assert.equal(await execute(selector => document.querySelector(selector).getAttribute('aria-expanded'), menu), 'true');
  await keys(menu, '\uE00C');
  assert.equal(await execute(selector => document.querySelector(selector).getAttribute('aria-expanded'), menu), 'false');
  assert.equal(await execute(selector => document.querySelector(selector) === document.activeElement, menu), true);
  result.checks.push('menu-escape-focus');

  await navigate(pageA);
  await wait(() => globalThis[Symbol.for('lithosharp.react-roots')]?.size === 1, 'page hydration');
  const assets = await execute(() => [...document.querySelectorAll('script[src],link[rel=stylesheet][href]')].map(element => element.getAttribute('src') ?? element.getAttribute('href')));
  assert.ok(assets.every(value => value.startsWith(base.pathname)), JSON.stringify(assets));
  assert.ok(assets.some(value => value.includes('/_mdx/') && /-[\w-]+\.js$/.test(value)), 'Missing versioned MDX entry.');
  result.checks.push('basepath-versioned-assets');
  await clickText('Count 3');
  await wait(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Count 4'), 'counter click');
  await clickText('Japanese');
  await wait(() => [...document.querySelectorAll('[role=tab][aria-selected=true]')].every(tab => tab.textContent.trim() === 'Japanese'), 'Japanese tab');
  await keys('[role=tab][aria-selected=true]', '\uE012');
  await wait(() => [...document.querySelectorAll('[role=tab][aria-selected=true]')].some(tab => tab.textContent.trim() === 'English'), 'tab arrow keyboard');
  result.checks.push('mdx-hydration-counter', 'tabs-keyboard');

  await keys('[data-ls-search] input', 'HydraulicConstellation');
  await wait(() => [...document.querySelectorAll('[data-ls-search] [data-results] a')].some(link => link.pathname.endsWith('/guide/a/')), 'local search result');
  await keys('[data-ls-search] input', '\uE015');
  assert.equal(await execute(() => document.activeElement.matches('[data-ls-search] [data-results] a')), true);
  await keys('[data-ls-search] [data-results] a', '\uE00C');
  assert.equal(await execute(() => document.activeElement.matches('[data-ls-search] input')), true);
  result.checks.push('local-search-keyboard');

  await execute(() => { globalThis.deliveryNavigationCanary = 'preserved'; });
  // Select the real sidebar link in the wide layout.
  await request('POST', '/window/rect', {width: 1200, height: 900});
  const nextLink = `#docs-sidebar a[href="${pageB.pathname}"]`;
  result.layoutReadiness.push({phase: 'wide-sidebar', state: await waitForPointerTarget(nextLink, 'wide sidebar pointer readiness')});
  await click(nextLink);
  await wait(pathname => location.pathname === pathname && document.querySelector('main h1')?.textContent.trim() === 'Page B', 'progressive next page', pageB.pathname);
  assert.equal(await execute(() => globalThis.deliveryNavigationCanary), 'preserved');
  await wait(() => globalThis[Symbol.for('lithosharp.islands')]?.get(document.querySelector('[data-island=load]'))?.root &&
    (globalThis[Symbol.for('lithosharp.react-roots')]?.size ?? 0) === 0, 'load island hydration without page root');
  await clickText('Count 10');
  await wait(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Count 11'), 'load island interaction');
  await request('POST', '/back', {});
  await wait(pathname => location.pathname === pathname && globalThis[Symbol.for('lithosharp.react-roots')]?.size === 1 &&
    [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Count 3'), 'back navigation remount', pageA.pathname);
  assert.equal(await execute(() => globalThis.deliveryNavigationCanary), 'preserved');
  assert.equal(await execute(() => {
    const tabs = document.querySelector('.mdx-tabs');
    return tabs !== null && tabs.querySelector('style') === null;
  }), true, 'History navigation included inline tab fallback styles.');
  result.checks.push('progressive-navigation', 'load-island', 'history-remount', 'history-tabs-external-fallback');

  const missing = new URL('missing/deep/page/', base);
  await checkHttp(missing, 404);
  await navigate(missing);
  assert.equal(await execute(() => document.querySelector('meta[name=robots]')?.content), 'noindex');
  assert.equal(await execute(() => document.querySelector('main a[href]')?.pathname), home.pathname);
  assert.equal(await execute(() => document.documentElement.dataset.siteTheme), 'dark');
  await click('main a[href]');
  await wait(pathname => location.pathname === pathname, '404 home navigation', home.pathname);
  if (placement.basePath !== '/') await checkHttp(new URL('/outside-base/', base), 404);
  result.checks.push('404-http-noindex-basepath-home');

  await navigate(new URL('index.html?block-storage=1', base));
  assert.equal(await execute(() => { try { void localStorage; return false; } catch { return true; } }), true);
  const before = await execute(() => document.querySelector('[data-site-theme-toggle]').getAttribute('aria-pressed'));
  await click('[data-site-theme-toggle]');
  assert.notEqual(await execute(() => document.querySelector('[data-site-theme-toggle]').getAttribute('aria-pressed')), before);
  result.checks.push('blocked-storage');
  await sleep(250);
  assert.deepEqual(events, [], 'Browser CSP/runtime/resource failures');
  if (options.firefox) {
    const script = fileURLToPath(new URL('../tests/fixtures/mdx-browser/delivery.mjs', import.meta.url));
    const run = await command(process.execPath, [script, base.href], {...process.env, LS_BROWSER: 'firefox'});
    const browser = JSON.parse(run.stdout.split('\n').at(-1));
    assert.equal(browser.browser, 'firefox');
    assert.ok(browser.browserVersion);
    evidence.firefox.push({basePath: base.pathname, ...browser, stderr: run.stderr});
    assert.deepEqual(events, [], 'Firefox CSP/runtime/resource failures');
  }
  result.status = 'passed';
  await new Promise((resolve, reject) => activeServer.close(error => error ? reject(error) : resolve()));
  activeServer = undefined;
}

try {
  evidence.driver = (await command('/usr/bin/safaridriver', ['--version'])).stdout;
  evidence.os = (await command('/usr/bin/sw_vers', [])).stdout;
  const portProbe = createServer().listen(0, '127.0.0.1');
  await once(portProbe, 'listening');
  const port = portProbe.address().port;
  await new Promise(resolve => portProbe.close(resolve));
  address = `http://127.0.0.1:${port}`;
  driver = spawn('/usr/bin/safaridriver', ['-p', String(port)], {stdio: ['ignore', 'pipe', 'pipe']});
  driver.on('error', error => { driverLog += String(error); });
  const log = chunk => { driverLog = (driverLog + chunk).slice(-65536); };
  driver.stdout.on('data', log); driver.stderr.on('data', log);
  const started = performance.now();
  while (true) {
    if (driver.exitCode !== null) throw new Error(`safaridriver exited: ${driverLog}`);
    try { if ((await webdriver('GET', '/status')).ready) break; } catch (error) {
      if (performance.now() - started > 15000) throw new Error(`safaridriver unavailable. Enable Remote Automation on the ephemeral CI runner: ${error.message}`);
    }
    if (performance.now() - started > 15000) throw new Error('safaridriver did not become ready.');
    await sleep(100);
  }
  const created = await webdriver('POST', '/session', {capabilities: {alwaysMatch: {browserName: 'safari'}}});
  session = created.sessionId;
  assert.ok(session);
  evidence.capabilities = created.capabilities;
  assert.equal(created.capabilities.browserName.toLowerCase(), 'safari');
  assert.ok(created.capabilities.browserVersion, 'Safari did not report its actual version.');
  evidence.actualSafari = true;
  await request('POST', '/timeouts', {implicit: 0, pageLoad: 30000, script: 20000});
  for (const placement of receipt.placements) await checkPlacement(placement);
  evidence.status = 'passed';
} catch (error) {
  evidence.status = 'failed';
  evidence.error = error.stack ?? String(error);
  if (session) {
    try { await writeFile(path.join(output, 'failure.png'), Buffer.from(await request('GET', '/screenshot'), 'base64')); } catch { }
  }
  process.exitCode = 1;
} finally {
  if (session) try { await request('DELETE', ''); } catch (error) { evidence.status = 'failed'; evidence.cleanupError = error.message; process.exitCode = 1; }
  if (driver?.pid && driver.exitCode === null) { driver.kill(); await once(driver, 'exit'); }
  if (activeServer) { activeServer.closeAllConnections(); await new Promise(resolve => activeServer.close(resolve)); }
  await writeFile(path.join(output, 'safaridriver.log'), driverLog);
  await writeFile(path.join(output, 'result.json'), JSON.stringify(evidence, null, 2) + '\n');
}
console.log(JSON.stringify({status: evidence.status, actualSafari: evidence.actualSafari,
  browserVersion: evidence.capabilities?.browserVersion, output}));
