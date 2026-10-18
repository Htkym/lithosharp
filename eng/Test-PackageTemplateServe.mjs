import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { fileURLToPath } from 'node:url';
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const markdownPair = JSON.parse(await fs.readFile(path.join(repo, 'eng/markdown/component-pair.json'), 'utf8'));
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { PackageServeProcesses } from './package-serve-processes.mjs';

const args = process.argv.slice(2);
assert.equal(args.length, 4, 'Usage: node Test-PackageTemplateServe.mjs <tool> <project> <candidate-version> <evidence>');
const [toolArg, projectArg, version, evidenceArg] = args;
const tool = path.resolve(toolArg), project = path.resolve(projectArg), evidence = path.resolve(evidenceArg);
assert.match(version, /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/);
const assets = JSON.parse(await fs.readFile(path.join(project, 'obj/project.assets.json'), 'utf8'));
const projects = (await fs.readdir(project)).filter(name => name.endsWith('.csproj'));
assert.equal(projects.length, 1);
assert.doesNotMatch(await fs.readFile(path.join(project, projects[0]), 'utf8'), /<ProjectReference\b/i);
for (const [name, library] of Object.entries(assets.libraries)) {
  if (!/^(?:LithoSharp(?:[./])|Syntamark\/)/.test(name) || name.startsWith('LithoSharp.FixtureExtension/')) continue;
  const [id, resolvedVersion] = name.split('/');
  assert.equal(resolvedVersion, id === 'Syntamark' ? markdownPair.componentVersion : version, `Unexpected package: ${name}`);
  assert.equal(library.type, 'package', `Source project escape: ${name}`);
}
assert.ok(assets.libraries[`LithoSharp/${version}`], 'Candidate Core was not resolved');
assert.ok(assets.libraries[`Syntamark/${markdownPair.componentVersion}`], 'Fixed Markdown runtime was not resolved');
await fs.mkdir(evidence, { recursive: false });
const events = [];
const startedAt = performance.now();
let phase = 'capture-owned-processes', phaseStartedAt = startedAt, startupInfo;
const initialHttp = { headersReceived: false, bodyReceived: false };
function markPhase(value) { phase = value; phaseStartedAt = performance.now(); }
let pending = '', stdout = '', stderr = '', invalidJson, inputError, exitResult;
const child = spawn(tool, ['serve', project, '-c', 'Release', '--port', '0', '--format', 'json', '--control-stdin'], {
  cwd: project, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
const processes = new PackageServeProcesses(child.pid);
const exit = new Promise((resolve, reject) => {
  child.once('error', reject);
  child.once('close', (code, signal) => { exitResult = { code, signal }; resolve(exitResult); });
});
// Attach an immediate handler even when an earlier assertion fails.
exit.catch(() => {});
child.stdout.setEncoding('utf8');
child.stderr.setEncoding('utf8');
child.stdin.on('error', error => { inputError = error; });
child.stderr.on('data', text => { stderr += text; });
child.stdout.on('data', text => {
  stdout += text;
  pending += text;
  let index;
  while ((index = pending.indexOf('\n')) >= 0) {
    const line = pending.slice(0, index).trim();
    pending = pending.slice(index + 1);
    if (!line) continue;
    try { events.push(JSON.parse(line)); } catch (error) { invalidJson = error; }
  }
});
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function withTimeout(promise, ms, label) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(Error(label)), ms); })]); }
  finally { clearTimeout(timer); }
}
async function processRows() {
  const exec = promisify(execFile);
  if (process.platform === 'win32') {
    const result = await exec('pwsh', ['-NoProfile', '-Command', '$ErrorActionPreference="Stop"; Get-CimInstance Win32_Process | ForEach-Object { [pscustomobject]@{pid=$_.ProcessId;parent=$_.ParentProcessId;birth=$_.CreationDate.ToUniversalTime().ToString("o")} } | ConvertTo-Json -Compress'], { windowsHide: true });
    return JSON.parse(result.stdout).map(row => ({ pid: Number(row.pid), parent: Number(row.parent), birth: row.birth }));
  }
  const result = await exec('ps', ['-A', '-o', 'pid=,ppid=,lstart='], { env: { ...process.env, LC_ALL: 'C' } });
  return result.stdout.trim().split('\n').filter(Boolean).map(line => {
    const match = line.trim().match(/^(\d+)\s+(\d+)\s+(.+)$/);
    assert.ok(match, 'Malformed process identity row');
    return { pid: Number(match[1]), parent: Number(match[2]), birth: match[3] };
  });
}
async function captureDescendants() {
  if (child.exitCode !== null || child.signalCode !== null) { processes.requireRoot(); return; }
  const rows = await processRows();
  if (child.exitCode !== null || child.signalCode !== null) { processes.requireRoot(); return; }
  processes.capture(rows);
}
async function waitFor(predicate, label, timeout = 180_000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (invalidJson) throw invalidJson;
    if (inputError) throw inputError;
    const result = predicate();
    if (result) return result;
    if (exitResult) throw Error(`Tool exited before ${label}: ${JSON.stringify(exitResult)}\n${stderr}`);
    await delay(100);
  }
  throw Error(`Timed out waiting for ${label}\n${stderr}`);
}
async function portOpen(port) {
  return new Promise(resolve => {
    const socket = net.connect({ host: '127.0.0.1', port });
    const done = result => { socket.destroy(); resolve(result); };
    socket.once('connect', () => done(true)); socket.once('error', () => done(false));
    socket.setTimeout(1000, () => done(false));
  });
}
function errorDetails(error, depth = 0) {
  if (depth === 3) return { truncated: true };
  try {
    if (!error || typeof error !== 'object') return { message: String(error).slice(0, 2048) };
    const text = value => typeof value === 'string' ? value.slice(0, 2048) : undefined;
    return { name: text(error.name), message: text(error.message), code: typeof error.code === 'number' ? error.code : text(error.code),
      ...(error.cause === undefined ? {} : { cause: errorDetails(error.cause, depth + 1) }) };
  } catch { return { unavailable: true }; }
}
function processState() {
  return { pid: child.pid ?? null, exitCode: child.exitCode, signalCode: child.signalCode, close: exitResult ?? null };
}
async function captureFailure(error, beforeCleanup) {
  // No additional HTTP request. TCP is a separate, bounded failure observation.
  try {
    const failure = result.failure = { phase, elapsedMs: Math.round(performance.now() - startedAt),
      phaseElapsedMs: Math.round(performance.now() - phaseStartedAt), beforeCleanup,
      error: errorDetails(error), startup: startupInfo ?? { observed: false }, initialHttp: { ...initialHttp },
      processAtFailure: processState() };
    const port = startupInfo?.actualPort;
    if (Number.isInteger(port) && port > 0 && port <= 65535) {
      const tcpStartedAt = performance.now();
      try {
        failure.tcp = { reachable: await withTimeout(portOpen(port), 1500, 'Failure TCP observation timed out'),
          elapsedMs: Math.round(performance.now() - tcpStartedAt), maximumWaitMs: 1500 };
      } catch (diagnosticError) { failure.tcp = { error: errorDetails(diagnosticError), maximumWaitMs: 1500 }; }
    } else failure.tcp = { skipped: 'No valid startup port' };
  } catch (diagnosticError) {
    result.failureDiagnosticError = errorDetails(diagnosticError);
  }
}
let result, primaryError, sourceMutation;
try {
  await captureDescendants();
  markPhase('startup');
  const startup = await waitFor(() => events.find(event => event.event === 'startup'), 'startup');
  startupInfo = { observed: true, generation: Number.isSafeInteger(startup.generation) ? startup.generation : null,
    actualPort: Number.isSafeInteger(startup.actualPort) ? startup.actualPort : null };
  assert.equal(startup.schemaVersion, '1.0'); assert.equal(startup.generation, 1);
  assert.equal(startup.requestedPort, 0); assert.ok(startup.actualPort > 0);
  assert.equal(startup.url, `http://127.0.0.1:${startup.actualPort}`);
  markPhase('capture-started-processes');
  await captureDescendants();
  markPhase('read-homepage');
  const disk = await fs.readFile(path.join(project, 'dist/index.html'), 'utf8');
  const main = disk.match(/<main\b[^>]*>[\s\S]*?<\/main>/i)?.[0];
  assert.ok(main, 'Generated homepage has no main content');
  markPhase('initial-http-headers');
  const response = await fetch(new URL('/', startup.url), { redirect: 'error', signal: AbortSignal.timeout(10_000) });
  initialHttp.headersReceived = true; initialHttp.status = response.status;
  markPhase('initial-http-status');
  assert.equal(response.status, 200);
  markPhase('initial-http-body');
  const html = await response.text();
  initialHttp.bodyReceived = true;
  markPhase('verify-homepage');
  assert.ok(html.includes(main), 'Served homepage differs from generated main content');
  assert.ok(html.includes("EventSource('/_lithosharp/reload')"), 'Live reload client missing');
  assert.ok(!disk.includes('EventSource('), 'Serve mutated the production output');
  if (path.basename(project) === 'mdx') {
    markPhase('mdx-http');
    const external = await fetch(new URL('/external-extension.txt', startup.url), { redirect: 'error', signal: AbortSignal.timeout(10_000) });
    assert.equal(external.status, 200); assert.equal(await external.text(), 'external C# package');
    const react = await fetch(new URL('/guide/index/', startup.url), { redirect: 'error', signal: AbortSignal.timeout(10_000) });
    assert.equal(react.status, 200); assert.match(await react.text(), /External package (?:<!-- -->)?7/);
  }
  markPhase('edit-source');
  const sourcePath = path.join(project, 'content', path.basename(project) === 'mdx' ? 'index.mdx' : 'index.md');
  const original = await fs.readFile(sourcePath, 'utf8');
  const marker = `Package template live edit ${process.pid} ${Date.now()}`;
  const modified = original + `\n\n${marker}\n`;
  sourceMutation = { sourcePath, original, modified };
  await fs.writeFile(sourcePath, modified);
  markPhase('rebuild');
  const rebuilt = await waitFor(() => events.find(event => event.event === 'rebuild-succeeded' && event.generation > startup.generation), 'new output generation');
  assert.equal(rebuilt.success, true);
  let changedRoute;
  markPhase('rebuilt-http');
  for (const route of startup.routes.filter(route => route.path.endsWith('.html'))) {
    const updated = await fetch(new URL(route.publicPath, startup.url), { redirect: 'error', signal: AbortSignal.timeout(10_000) });
    assert.equal(updated.status, 200);
    if ((await updated.text()).includes(marker)) { changedRoute = route.publicPath; break; }
  }
  assert.ok(changedRoute, 'HTTP did not converge to the edited source at the new generation');
  markPhase('shutdown-ack');
  await captureDescendants();
  child.stdin.write('{"schemaVersion":"1.0","command":"shutdown","requestId":"package-template-stop"}\n');
  const ack = await waitFor(() => events.find(event => event.event === 'control-ack' && event.requestId === 'package-template-stop'), 'shutdown acknowledgement');
  assert.equal(ack.success, true);
  markPhase('shutdown-exit');
  const terminal = await withTimeout(exit, 30_000, 'Owned server did not exit after shutdown');
  assert.equal(terminal.code, 0); assert.equal(terminal.signal, null);
  markPhase('shutdown-port');
  assert.equal(await portOpen(startup.actualPort), false, 'Owned TCP listener remained');
  assert.equal(events.filter(event => event.event === 'shutdown').length, 1);
  markPhase('shutdown-processes');
  const alive = processes.remaining(await processRows());
  assert.deepEqual(alive, [], 'Owned server/worker process remained after shutdown');
  result = { passed: true, template: path.basename(project), candidateVersion: version, packageOnly: true, startup, rebuildGeneration: rebuilt.generation, changedRoute, marker, ownedPids: [...processes.owned.keys()], exit: terminal, listenerClosed: true, ownedProcessesRemaining: 0 };
} catch (error) {
  primaryError = error;
  result = { passed: false, template: path.basename(project), candidateVersion: version, error: String(error), exit: exitResult };
  await captureFailure(error, true);
  result.processOwnershipBeforeCleanup = processes.evidence();
  try { await fs.writeFile(path.join(evidence, 'result.json'), JSON.stringify(result, null, 2) + '\n'); }
  catch (diagnosticError) { result.failureEvidenceWriteError = errorDetails(diagnosticError); }
} finally {
  // Capture only descendants of the handle we spawned while it is still alive.
  if (!exitResult) {
    try { await captureDescendants(); } catch (error) { result.cleanupCaptureError = String(error); }
    try { child.stdin.write('{"schemaVersion":"1.0","command":"shutdown","requestId":"package-template-finally"}\n'); } catch {}
    try { await withTimeout(exit, 5000, 'Cleanup shutdown timeout'); } catch {}
  }
  try { child.stdin.end(); } catch {}
  if (!exitResult) {
    try { await Promise.race([exit, delay(3000)]); } catch {}
    if (!exitResult) {
      // Only the handle returned by our spawn may be terminated.
      child.kill();
      try { await Promise.race([exit, delay(3000)]); } catch {}
    }
  }
  const cleanupKilled = [], cleanupRemaining = [];
  try {
    // A PID alone is insufficient ownership proof after its parent exits.
    // Require the same recorded creation identity before touching a descendant.
    for (const row of processes.remaining(await processRows())) {
      if (row.pid === child.pid) continue;
      try { process.kill(row.pid); cleanupKilled.push(row.pid); }
      catch (error) { if (error.code !== 'ESRCH') cleanupRemaining.push({ pid: row.pid, error: String(error) }); }
    }
    if (cleanupKilled.length) await delay(1000);
    for (const row of processes.remaining(await processRows())) cleanupRemaining.push({ pid: row.pid });
  } catch (error) { cleanupRemaining.push({ error: String(error) }); }
  result.cleanupKilledOwnedPids = cleanupKilled;
  result.cleanupRemaining = cleanupRemaining;
  result.processOwnership = processes.evidence();
  if (cleanupRemaining.length && !primaryError) {
    primaryError = Error('Owned processes remained after cleanup');
    result.passed = false; result.error = String(primaryError);
  }
  if (sourceMutation) {
    try {
      if (await fs.readFile(sourceMutation.sourcePath, 'utf8') === sourceMutation.modified) {
        await fs.writeFile(sourceMutation.sourcePath, sourceMutation.original);
        result.fixtureSourceRestored = true;
      } else {
        result.fixtureSourceRestored = false;
        if (!primaryError) { primaryError = Error('Fixture source changed unexpectedly; preserved it'); result.passed = false; result.error = String(primaryError); }
      }
    } catch (error) {
      result.fixtureSourceRestored = false;
      result.fixtureRestoreError = errorDetails(error);
      if (!primaryError) { primaryError = error; result.passed = false; result.error = String(error); }
    }
  }
  if (primaryError) {
    if (!result.failure) { markPhase('cleanup'); await captureFailure(primaryError, false); }
    if (result.failure) result.failure.processAfterCleanup = processState();
    result.exit = exitResult ?? { code: child.exitCode, signal: child.signalCode, closeObserved: false };
  }
  for (const [name, text] of [['stdout.jsonl', stdout], ['stderr.log', stderr], ['result.json', JSON.stringify(result, null, 2) + '\n']]) {
    try { await fs.writeFile(path.join(evidence, name), text); }
    catch (error) {
      if (!primaryError) throw error;
      console.error('Could not write package serve failure evidence:', JSON.stringify(errorDetails(error)));
    }
  }
}
if (primaryError) throw primaryError;
console.log(`Package-only ${path.basename(project)} serve passed: HTTP/content/generation/shutdown/owned processes.`);
