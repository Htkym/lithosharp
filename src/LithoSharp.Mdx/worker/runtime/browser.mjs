import {hydrateRoot} from 'react-dom/client';

const key = Symbol.for('lithosharp.react-roots');
const roots = globalThis[key] ??= new Map();

export function mountPage(id, element, identifierPrefix) {
  const container = document.getElementById(id);
  if (!container || roots.has(container)) return;
  const root = hydrateRoot(container, element, {identifierPrefix, onRecoverableError(error) {
    console.error('LithoSharp hydration:', error);
    document.dispatchEvent(new CustomEvent('lithosharp:hydration-error', {detail: {id, message: String(error)}}));
  }});
  roots.set(container, root);
}

export function unmountPages(scope = document) {
  for (const [container, root] of roots) if (scope.contains(container)) { root.unmount(); roots.delete(container); }
}

document.addEventListener('lithosharp:before-navigate', () => unmountPages());
