(() => {
  const root = document.documentElement;
  let mode = 'system';
  try {
    const stored = localStorage.getItem('lithosharp:' + /*__BASE_PATH__*/ + ':theme') ?? localStorage.getItem('lithosharp-theme');
    if (['light', 'dark', 'system'].includes(stored)) mode = stored;
  } catch { /* Storage is optional. */ }
  const resolved = mode === 'system' ? (typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : mode;
  root.dataset.themeMode = mode;
  root.dataset.theme = root.dataset.siteTheme = resolved;
  root.style.colorScheme = resolved;
})();
