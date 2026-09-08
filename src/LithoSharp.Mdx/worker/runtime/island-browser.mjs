const registry = globalThis[Symbol.for('lithosharp.islands')] ??= new Map();
let generation = 0;
export function registerIsland(descriptor, context) {
  const element = document.getElementById(descriptor.id);
  if (!element || registry.has(element)) return;
  const epoch = generation;
  const state = {root: null, cancel: () => {}, started: false};
  registry.set(element, state);
  async function activate() {
    if (state.started || generation !== epoch || !element.isConnected) return;
    state.started = true; state.cancel();
    const start = performance.now();
    try {
      const [react, client, {PageContext, HydrationProbe, hydrationStarts}, module] = await Promise.all([import('react'), import('react-dom/client'), import('./context.mjs'), descriptor.load()]);
      const React = react.default ?? react;
      const {hydrateRoot} = client.default ?? client;
      if (generation !== epoch || !element.isConnected) return;
      const component = module[descriptor.exportName];
      if (!component) throw new Error('Island component export is unavailable.');
      hydrationStarts.set(descriptor.id, performance.now());
      state.clearTiming = () => hydrationStarts.delete(descriptor.id);
      state.root = hydrateRoot(element, React.createElement(HydrationProbe, {id: descriptor.id}, React.createElement(PageContext.Provider, {value: context}, React.createElement(component, JSON.parse(element.dataset.islandProps)))), {
        identifierPrefix: descriptor.id + '-', onRecoverableError(error) { document.dispatchEvent(new CustomEvent('lithosharp:hydration-error', {detail: {id: descriptor.id, message: String(error)}})); }
      });
      document.dispatchEvent(new CustomEvent('lithosharp:island-hydrated', {detail: {id: descriptor.id, milliseconds: performance.now() - start}}));
    } catch (error) {
      document.dispatchEvent(new CustomEvent('lithosharp:chunk-error', {detail: {id: descriptor.id, message: String(error)}}));
    }
  }
  if (descriptor.strategy === 'manual') {
    const listener = event => { if (event.detail?.id === descriptor.id) activate(); };
    document.addEventListener('lithosharp:hydrate', listener); state.cancel = () => document.removeEventListener('lithosharp:hydrate', listener);
  } else if (descriptor.strategy === 'idle' && typeof requestIdleCallback === 'function') {
    const task = requestIdleCallback(activate, {timeout: 2000}); state.cancel = () => cancelIdleCallback(task);
  } else if (descriptor.strategy === 'visible' && typeof IntersectionObserver === 'function') {
    const observer = new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting)) activate(); });
    observer.observe(element); state.cancel = () => observer.disconnect();
  } else if (descriptor.strategy === 'media' && typeof matchMedia === 'function') {
    const query = matchMedia(descriptor.media); const listener = () => { if (query.matches) activate(); };
    query.addEventListener('change', listener); state.cancel = () => query.removeEventListener('change', listener); listener();
  } else {
    const task = setTimeout(activate, 0); state.cancel = () => clearTimeout(task);
  }
}
const listenerKey = Symbol.for('lithosharp.island-cleanup');
if (!globalThis[listenerKey]) {
  globalThis[listenerKey] = true;
  document.addEventListener('lithosharp:before-navigate', () => {
    generation++; for (const state of registry.values()) { state.cancel(); state.root?.unmount(); state.clearTiming?.(); } registry.clear();
  });
}
