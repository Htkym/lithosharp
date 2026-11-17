// Shared by classic templates, MDX navigation and custom site chrome.
(() => {
  const key = Symbol.for('lithosharp.theme');
  if (globalThis[key]) return;
  const selector = '[data-site-theme-toggle], [data-ls-theme]';
  const valid = value => ['system', 'light', 'dark'].includes(value);
  const system = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : {matches: false};
  let basePath = '/', mode = 'system', started = false, translate = (key, fallback) => document.documentElement.lang?.startsWith('ja')
    ? ({theme:'テーマ', system:'システム設定', light:'ライト', dark:'ダーク'}[key] ?? fallback) : fallback;
  const storageKey = () => `lithosharp:${basePath}:theme`;
  function apply(notify = false) {
    const root = document.documentElement;
    const resolved = mode === 'system' ? system.matches ? 'dark' : 'light' : mode;
    root.dataset.themeMode = mode;
    root.dataset.theme = root.dataset.siteTheme = resolved;
    root.style.colorScheme = resolved;
    if (!started) return;
    for (const button of document.querySelectorAll(selector)) {
      button.hidden = false;
      button.dataset.themeMode = mode;
      const label = `${translate('theme', 'Theme')}: ${translate(mode, mode)}`;
      button.setAttribute('aria-label', label);
      button.title = label;
      // Preserve icon markup while exposing the same three-state preference.
      if (button.hasAttribute('data-ls-theme')) button.textContent = label;
      button.removeAttribute('aria-pressed');
    }
    if (notify) document.dispatchEvent(new CustomEvent('lithosharp:theme', {detail: {theme: resolved, mode}}));
  }
  function restore(path = '/') {
    basePath = path;
    mode = 'system';
    try {
      const scoped = localStorage.getItem(storageKey());
      const stored = valid(scoped) ? scoped : localStorage.getItem('lithosharp-theme');
      if (valid(stored)) mode = stored;
    } catch { /* Storage is optional. */ }
    apply();
  }
  function start(path = '/', t) {
    if (t) translate = t;
    if (!started || basePath !== path) restore(path);
    if (!started) {
      started = true;
      document.addEventListener('click', event => {
        const button = event.target?.closest?.(selector);
        if (!button) return;
        mode = mode === 'system' ? 'light' : mode === 'light' ? 'dark' : 'system';
        try { localStorage.setItem(storageKey(), mode); } catch { /* Storage is optional. */ }
        apply(true);
      });
      system.addEventListener?.('change', () => { if (mode === 'system') apply(true); });
      document.addEventListener('lithosharp:page', () => apply());
    }
    apply(true);
  }
  globalThis[key] = {restore, start};
})();
