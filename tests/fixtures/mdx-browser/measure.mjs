import {chromium} from 'playwright';
import {writeFile} from 'node:fs/promises';
import {gzipSync} from 'node:zlib';
if (process.argv.length !== 5 && !(process.argv.length === 6 && process.argv[5] === '--activate')) throw new Error('Usage: node measure.mjs <page-origin> <selective-origin> <result.json> [--activate]');
const activate = process.argv[5] === '--activate';
const routes = activate ? ['islands'] : ['static', 'interactive', 'islands'];
const browser = await chromium.launch();
const measurements = [];
try {
  for (const route of routes) {
    for (let run = 0; run < 5; run++) {
      for (const [mode, origin] of (run % 2 ? [['selective', process.argv[3]], ['page', process.argv[2]]] : [['page', process.argv[2]], ['selective', process.argv[3]]])) {
        const context = await browser.newContext({serviceWorkers: 'block', viewport: activate ? {width: 640, height: 600} : {width: 1280, height: 720}});
        try {
          const page = await context.newPage();
          const bodies = [];
          page.on('response', response => { if (new URL(response.url()).pathname.endsWith('.js')) bodies.push(response.body()); });
          await page.addInitScript(() => {
            globalThis.lsMeasurement = {hydrations: [], longTasks: [], shifts: []};
            document.addEventListener('lithosharp:hydrated', event => lsMeasurement.hydrations.push(event.detail));
            new PerformanceObserver(list => lsMeasurement.longTasks.push(...list.getEntries().map(entry => entry.duration))).observe({type: 'longtask', buffered: true});
            new PerformanceObserver(list => lsMeasurement.shifts.push(...list.getEntries().filter(entry => !entry.hadRecentInput).map(entry => entry.value))).observe({type: 'layout-shift', buffered: true});
          });
          const session = await context.newCDPSession(page);
          await session.send('Performance.enable');
          const tracingComplete = new Promise(resolve => session.once('Tracing.tracingComplete', resolve));
          await session.send('Tracing.start', {categories: 'devtools.timeline,v8', transferMode: 'ReturnAsStream'});
          await page.goto(origin + '/guide/' + route + '/', {waitUntil: 'networkidle'});
          const snapshot = () => page.evaluate(() => {
            const js = performance.getEntriesByType('resource').filter(value => new URL(value.name).pathname.endsWith('.js'));
            return {jsRequests: js.length, jsDecodedBytes: js.reduce((sum, value) => sum + value.decodedBodySize, 0),
              jsTransferBytes: js.reduce((sum, value) => sum + value.transferSize, 0),
              pageRoots: globalThis[Symbol.for('lithosharp.react-roots')]?.size ?? 0,
              islandRoots: [...(globalThis[Symbol.for('lithosharp.islands')]?.values() ?? [])].filter(value => value.root).length,
              hydrationMilliseconds: lsMeasurement.hydrations.reduce((sum, value) => sum + value.milliseconds, 0),
              hydrationCount: lsMeasurement.hydrations.length,
              longTaskCount: lsMeasurement.longTasks.length, longTaskMilliseconds: lsMeasurement.longTasks.reduce((sum, value) => sum + value, 0),
              layoutShift: lsMeasurement.shifts.reduce((sum, value) => sum + value, 0),
              loadMilliseconds: performance.getEntriesByType('navigation')[0].loadEventEnd};
          });
          let beforeActivation;
          if (activate) {
            if (mode === 'selective') await page.waitForFunction(() => ['load', 'idle'].every(strategy => globalThis[Symbol.for('lithosharp.islands')]?.get(document.querySelector(`[data-island=${strategy}]`))?.root));
            const initialMetrics = Object.fromEntries((await session.send('Performance.getMetrics')).metrics.map(value => [value.name, value.value]));
            beforeActivation = {...await snapshot(), scriptMilliseconds: initialMetrics.ScriptDuration * 1000};
            if (mode === 'selective' && beforeActivation.islandRoots !== 2) throw new Error('Deferred island started before activation');
            await page.locator('[data-island=visible]').scrollIntoViewIfNeeded();
            await page.setViewportSize({width: 1200, height: 800});
            await page.evaluate(() => document.dispatchEvent(new CustomEvent('lithosharp:hydrate', {detail: {id: document.querySelector('[data-island=manual]').id}})));
            if (mode === 'selective') await page.waitForFunction(() => [...globalThis[Symbol.for('lithosharp.islands')].values()].filter(value => value.root).length === 5 && lsMeasurement.hydrations.length === 5);
            await page.waitForLoadState('networkidle');
          }
          const resource = await snapshot();
          const metrics = Object.fromEntries((await session.send('Performance.getMetrics')).metrics.map(value => [value.name, value.value]));
          await session.send('Tracing.end');
          const {stream} = await tracingComplete;
          let trace = '';
          while (true) { const chunk = await session.send('IO.read', {handle: stream}); trace += chunk.data; if (chunk.eof) break; }
          await session.send('IO.close', {handle: stream});
          const events = JSON.parse(trace).traceEvents.filter(event => event.ph === 'X');
          const duration = names => events.filter(event => names.includes(event.name)).reduce((sum, event) => sum + (event.dur ?? 0) / 1000, 0);
          const jsGzipBytes = (await Promise.all(bodies)).reduce((sum, body) => sum + gzipSync(body, {level: 6}).length, 0);
          measurements.push({mode, route, run, ...(beforeActivation ? {beforeActivation} : {}), ...resource, jsGzipBytes, scriptMilliseconds: metrics.ScriptDuration * 1000,
            parseTraceMilliseconds: duration(['v8.parseOnBackgroundParsing']), compileTraceMilliseconds: duration(['v8.compileModule', 'v8.compile']), evaluationTraceMilliseconds: duration(['v8.evaluateModule']),
            taskMilliseconds: metrics.TaskDuration * 1000, jsHeapUsedBytes: metrics.JSHeapUsedSize});
        } finally { await context.close(); }
      }
    }
  }
  const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
  const summary = [];
  for (const mode of ['page', 'selective']) for (const route of routes) {
    const rows = measurements.filter(value => value.mode === mode && value.route === route);
    summary.push({mode, route, ...(activate ? {beforeActivation: Object.fromEntries(Object.keys(rows[0].beforeActivation).map(key => [key, median(rows.map(value => value.beforeActivation[key]))]))} : {}), ...Object.fromEntries(Object.keys(rows[0]).filter(key => !['mode', 'route', 'run', 'beforeActivation'].includes(key)).map(key => [key, median(rows.map(value => value[key]))]))});
  }
  const result = {schema: 1, browser: browser.version(), runs: 5, viewport: activate ? '640x600 before activation, 1200x800 after activation' : '1280x720', compression: 'none',
    cache: 'fresh browser context per navigation; service workers blocked; alternating mode order within each repeated route', readiness: activate ? 'network idle before activation; scroll visible island, enlarge viewport and dispatch manual event; wait for all five selective hydration commits; after values are cumulative' : 'network idle; default island strategies; no manual activation or scrolling',
    memory: 'JSHeapUsedSize after navigation, not peak RSS', hydration: 'hydrateRoot call to first passive-effect commit; overlapping roots are summed, excluding dynamic import time',
    script: 'CDP ScriptDuration plus duration sums for v8.parseOnBackgroundParsing, v8.compileModule/v8.compile and v8.evaluateModule trace events. Trace durations can overlap and are not exclusive wall times. Tracing is enabled equally for both modes.', gzip: 'Sum of independently gzip-compressed loaded JS bodies at level 6; server transfer itself is uncompressed', measurements, summary};
  await writeFile(process.argv[4], JSON.stringify(result, null, 2));
  console.log(JSON.stringify(summary));
} finally { await browser.close(); }
