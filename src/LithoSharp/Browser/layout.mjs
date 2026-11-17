// Shared documentation chrome measurements and native API disclosure navigation.
(() => {
  const key = Symbol.for('lithosharp.docs-layout');
  if (globalThis[key]) { globalThis[key](); return; }
  let cleanup = () => {};
  const reveal = () => {
    let id; try { id = decodeURIComponent(location.hash.slice(1)); } catch { return; }
    const target = document.getElementById(id);
    if (target?.matches('details.api-category-card')) target.open = true;
  };
  function initialize() {
    cleanup();
    const body = document.body;
    const shell = body.querySelector('.docs-shell');
    if (!body.classList.contains('docs-body') || !shell) { cleanup = () => {}; return; }
    const header = body.querySelector('.docs-header');
    const footer = body.querySelector('.docs-footer');
    const measure = () => {
      if (document.body !== body) return;
      const style = getComputedStyle(shell);
      body.style.setProperty('--docs-header-height', `${header?.getBoundingClientRect().height ?? 0}px`);
      body.style.setProperty('--docs-footer-height', `${footer?.getBoundingClientRect().height ?? 0}px`);
      body.style.setProperty('--docs-shell-block-spacing', `${(parseFloat(style.paddingBlockStart) || 0) + (parseFloat(style.paddingBlockEnd) || 0)}px`);
    };
    const resize = new ResizeObserver(measure);
    for (const element of [header, footer, shell]) if (element) resize.observe(element);
    addEventListener('resize', measure);
    measure(); reveal();
    cleanup = () => { resize.disconnect(); removeEventListener('resize', measure); };
  }
  globalThis[key] = initialize;
  addEventListener('hashchange', reveal);
  document.addEventListener('lithosharp:before-navigate', () => { cleanup(); cleanup = () => {}; });
  document.addEventListener('lithosharp:page', initialize);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, {once:true}); else initialize();
})();
