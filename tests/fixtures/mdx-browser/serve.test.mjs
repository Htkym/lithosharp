import assert from 'node:assert/strict';
import {mkdtemp, writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {Readable} from 'node:stream';
import {test} from 'node:test';
import {Script} from 'node:vm';
import {createFixtureServer} from './serve.mjs';

async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'ls-route-'));
  await writeFile(path.join(root, 'index.html'), '<html><head></head><body>Home</body></html>');
  await writeFile(path.join(root, '404.html'), '<html><head></head><body>Missing</body></html>');
  return root;
}
function route(server, url, method = 'GET', body = '') {
  return new Promise(resolve => {
    const response = {status: 200, headers: {}, setHeader(name, value) { this.headers[name.toLowerCase()] = value; },
      writeHead(status, headers = {}) { this.status = status; for (const [name, value] of Object.entries(headers)) this.setHeader(name, value); },
      end(bytes = '') { resolve({status: this.status, headers: this.headers, body: bytes.toString()}); }};
    const request = Readable.from(body ? [Buffer.from(body)] : []);
    request.url = url; request.method = method;
    server.emit('request', request, response);
  });
}

test('default fixture behavior and revision switching remain unchanged', async () => {
  const root = await fixture(), next = await fixture();
  await writeFile(path.join(next, 'index.html'), '<html><head></head><body>Next</body></html>');
  const server = createFixtureServer({root, revisions: [next]});
  assert.deepEqual(await route(server, '/'), {status: 200, headers: {'content-type': 'text/html'}, body: '<html><head></head><body>Home</body></html>'});
  assert.equal((await route(server, '/_fixture/next', 'POST')).status, 200);
  assert.match((await route(server, '/index.html')).body, /Next/);
  assert.equal((await route(server, '/_fixture/delivery-probe.js')).status, 404);
});

for (const basePath of ['', '/sub']) test(`delivery diagnostics preserve CSP, 404 and base path ${basePath || '/'}`, async () => {
  const events = [];
  const server = createFixtureServer({root: await fixture(), basePath, csp: "script-src 'self'", probe: true, onDeliveryEvent: event => events.push(event)});
  const home = await route(server, basePath + '/index.html');
  assert.equal(home.status, 200);
  assert.equal(home.headers['content-security-policy'], "script-src 'self'");
  assert.ok(home.body.includes(`<head><script data-delivery-probe src="${basePath}/_fixture/delivery-probe.js"`));
  const probe = await route(server, basePath + '/_fixture/delivery-probe.js');
  assert.equal(probe.status, 200); assert.equal(probe.headers['content-type'], 'text/javascript');
  new Script(probe.body);
  assert.equal((await route(server, basePath + '/_fixture/delivery-events', 'POST', '{"kind":"control"}')).status, 204);
  assert.deepEqual(events, [{kind: 'control'}]);
  assert.equal((await route(server, basePath + '/_fixture/delivery-events', 'POST', 'x'.repeat(8193))).status, 413);
  const missing = await route(server, basePath + '/missing/deep/');
  assert.equal(missing.status, 404); assert.match(missing.body, /Missing/); assert.match(missing.body, /data-delivery-probe/);
  if (basePath) assert.equal((await route(server, '/outside-base/')).status, 404);
});
