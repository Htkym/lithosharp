import assert from 'node:assert/strict';
import {spawn, execFileSync} from 'node:child_process';
import {once} from 'node:events';
import {mkdir, writeFile} from 'node:fs/promises';
import {createServer} from 'node:net';
import path from 'node:path';

// A single empty session, before any product build or page navigation. No retries.
if (process.argv.includes('--help')) {
  console.log('node eng/Test-SafariStartup.mjs <new-evidence-directory>');
  process.exit(0);
}
assert.equal(process.platform, 'darwin', 'Requires actual macOS Safari.');
assert.equal(process.argv.length, 3);
const output = path.resolve(process.argv[2]);
await mkdir(path.dirname(output), {recursive: true});
await mkdir(output, {recursive: false});
const evidence = {schemaVersion: 1, status: 'running', phase: 'driver-startup',
  image: process.env.ImageOS, imageVersion: process.env.ImageVersion,
  node: process.version, arch: process.arch, actualSafari: false, productNavigation: false};
let driver, session, address, driverLog = '';
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
async function webdriver(method, endpoint, body) {
  const response = await fetch(address + endpoint, {method,
    headers: body === undefined ? {} : {'Content-Type': 'application/json'},
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(endpoint === '/status' ? 2000 : 45000)});
  const result = await response.json();
  if (!response.ok || result.value?.error) throw new Error(`WebDriver ${method} ${endpoint}: ${JSON.stringify(result.value)}`);
  return result.value;
}
try {
  evidence.driver = execFileSync('/usr/bin/safaridriver', ['--version'], {encoding: 'utf8', timeout: 10000}).trim();
  evidence.os = execFileSync('/usr/bin/sw_vers', [], {encoding: 'utf8', timeout: 10000}).trim();
  const probe = createServer().listen(0, '127.0.0.1');
  await once(probe, 'listening');
  const port = probe.address().port;
  await new Promise(resolve => probe.close(resolve));
  address = `http://127.0.0.1:${port}`;
  driver = spawn('/usr/bin/safaridriver', ['--diagnose', '-p', String(port)], {stdio: ['ignore', 'pipe', 'pipe']});
  const log = chunk => { driverLog = (driverLog + chunk).slice(-65536); };
  driver.stdout.on('data', log);
  driver.stderr.on('data', log);
  await once(driver, 'spawn');
  const started = performance.now();
  while (true) {
    if (driver.exitCode !== null) throw new Error(`safaridriver exited: ${driverLog}`);
    try { if ((await webdriver('GET', '/status')).ready) break; } catch { }
    if (performance.now() - started > 15000) throw new Error('safaridriver did not become ready.');
    await sleep(100);
  }
  evidence.phase = 'empty-session';
  const created = await webdriver('POST', '/session', {capabilities: {alwaysMatch: {browserName: 'safari'}}});
  session = created.sessionId;
  assert.ok(session);
  assert.equal(created.capabilities.browserName.toLowerCase(), 'safari');
  assert.ok(created.capabilities.browserVersion);
  evidence.capabilities = created.capabilities;
  evidence.actualSafari = true;
  evidence.status = 'passed';
} catch (error) {
  evidence.status = 'failed';
  evidence.error = error.stack ?? String(error);
  process.exitCode = 1;
} finally {
  if (session) {
    try { await webdriver('DELETE', `/session/${session}`); }
    catch (error) { evidence.cleanupError = error.message; evidence.status = 'failed'; process.exitCode = 1; }
  }
  if (driver?.pid && driver.exitCode === null) {
    const exited = once(driver, 'exit');
    driver.kill();
    const killTimer = setTimeout(() => driver.kill('SIGKILL'), 3000);
    try { await exited; } finally { clearTimeout(killTimer); }
  }
  await writeFile(path.join(output, 'safaridriver.log'), driverLog);
  await writeFile(path.join(output, 'result.json'), JSON.stringify(evidence, null, 2) + '\n');
}
console.log(JSON.stringify(evidence));
