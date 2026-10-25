async page => {
  const browser = page.context().browser();
  const origin = 'http://127.0.0.1:4317';
  function check(value, message, observation) {
    if (!value) throw new Error(message + ': ' + JSON.stringify(observation));
  }
  function installControl({mode}) {
    const key = Symbol.for('lithosharp.islands');
    const observed = globalThis.lifecycleControl = {
      committed: [], hydrationErrors: [], chunkErrors: [], unhandledErrors: [],
      navigationCount: 0, retirement: null, mutation: null, boundaryReached: false,
    };
    document.addEventListener('lithosharp:hydrated', event => observed.committed.push(event.detail?.id), true);
    document.addEventListener('lithosharp:before-navigate', () => observed.navigationCount++, true);
    document.addEventListener('lithosharp:chunk-error', event => observed.chunkErrors.push(event.detail), true);
    document.addEventListener('lithosharp:hydration-error', event => {
      const element = document.getElementById(event.detail?.id);
      observed.hydrationErrors.push({detail: event.detail, connected: element?.isConnected ?? false,
        rootPresent: Boolean(element && globalThis[key]?.get(element)?.root), navigationCount: observed.navigationCount});
    }, true);
    window.addEventListener('error', event => observed.unhandledErrors.push(String(event.error ?? event.message)));
    window.addEventListener('unhandledrejection', event => observed.unhandledErrors.push(String(event.reason)));
    if (mode === 'retired') {
      document.addEventListener('lithosharp:island-hydrated', event => {
        if (observed.retirement) return;
        const element = document.getElementById(event.detail?.id);
        const root = element && globalThis[key]?.get(element)?.root;
        const witness = observed.retirement = {id: event.detail?.id,
          rootAssigned: Boolean(root), connected: element?.isConnected ?? false,
          committedBeforeRetirement: observed.committed.includes(event.detail?.id),
          unmountCalls: 0, correctReceiver: true, dispatchFinished: false};
        if (root && typeof root.unmount === 'function') {
          const original = root.unmount;
          const own = Object.getOwnPropertyDescriptor(root, 'unmount');
          function unmount(...args) {
            witness.unmountCalls++;
            witness.correctReceiver &&= this === root;
            return Reflect.apply(original, this, args);
          }
          Object.defineProperty(root, 'unmount', own ? {...own, value: unmount}
            : {value: unmount, writable: true, enumerable: true, configurable: true});
        }
        document.dispatchEvent(new CustomEvent('lithosharp:before-navigate'));
        witness.dispatchFinished = true;
      }, true);
    } else {
      class MismatchMap extends Map {
        set(element, state) {
          if (!observed.mutation && element.dataset.island === 'load') {
            const button = element.querySelector('button');
            observed.mutation = {id: element.id, beforeActivation: !state.started && state.root === null, buttonFound: Boolean(button)};
            if (button) button.textContent += ' [SSR mismatch canary]';
          }
          return super.set(element, state);
        }
      }
      globalThis[key] = new MismatchMap();
    }
  }
  async function runControl(mode) {
    const context = await browser.newContext({viewport: {width: 640, height: 600}});
    try {
      const pageErrors = [];
      const tab = await context.newPage();
      tab.setDefaultTimeout(15000);
      tab.on('pageerror', error => pageErrors.push(String(error)));
      await tab.addInitScript(installControl, {mode});
      await tab.goto(origin + '/guide/islands/');
      if (mode === 'retired') {
        await tab.waitForFunction(() => lifecycleControl.retirement?.dispatchFinished);
      } else {
        await tab.waitForFunction(() => lifecycleControl.mutation
          && lifecycleControl.hydrationErrors.some(error => error.detail?.id === lifecycleControl.mutation.id));
      }
      await tab.evaluate(() => requestAnimationFrame(() => requestAnimationFrame(() => {
        lifecycleControl.boundaryReached = true;
      })));
      await tab.waitForFunction(() => lifecycleControl.boundaryReached);
      const observation = await tab.evaluate(() => {
        const registry = globalThis[Symbol.for('lithosharp.islands')];
        const element = lifecycleControl.mutation && document.getElementById(lifecycleControl.mutation.id);
        return {...lifecycleControl, registrySize: registry?.size,
          activeRoot: Boolean(element?.isConnected && registry?.get(element)?.root)};
      });
      check(pageErrors.length === 0 && observation.unhandledErrors.length === 0,
        'An unhandled lifecycle error occurred', {pageErrors, observation});
      check(observation.chunkErrors.length === 0, 'A lifecycle control failed to load its real component', observation);
      if (mode === 'retired') {
        check(observation.retirement.rootAssigned && observation.retirement.connected
          && !observation.retirement.committedBeforeRetirement,
        'The cleanup did not target a real root before its passive commit observation', observation);
        check(observation.retirement.unmountCalls === 1 && observation.retirement.correctReceiver
          && observation.navigationCount === 1 && observation.registrySize === 0,
        'The real navigation cleanup did not retire the root exactly once', observation);
        check(observation.hydrationErrors.length === 0, 'A retired root reported a recoverable hydration error', observation);
      } else {
        check(observation.mutation.beforeActivation && observation.mutation.buttonFound
          && observation.activeRoot && observation.navigationCount === 0,
        'The mismatch canary did not preserve an active real root', observation);
        check(observation.hydrationErrors.length > 0 && observation.hydrationErrors.every(error =>
          error.detail?.id === observation.mutation.id && error.connected && error.rootPresent && error.navigationCount === 0),
        'Active recoverable hydration errors were suppressed or attributed to another island', observation);
      }
    } finally { await context.close(); }
  }
  await runControl('retired');
  await runControl('active');
  return {pendingRootRetirement: 'passed', activeRecoverableErrors: 'passed', controls: 2};
}
