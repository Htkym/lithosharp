// Shared Docs sidebar sizing. Native disclosure and navigation keep their own behavior.
(() => {
  const key = Symbol.for('lithosharp.sidebar-sizing');
  if (globalThis[key]) { globalThis[key](); return; }
  let cleanup = () => {};
  const compact = matchMedia('(max-width:52rem)');
  const storageKey = 'lithosharp:sidebar-width:' + new URL(document.querySelector('.docs-brand')?.href || '/', location.href).pathname.replace(/index\.html$/, '');
  let saved;
  try { const value = Number(localStorage.getItem(storageKey)); if (Number.isFinite(value) && value >= 200 && value <= 800) saved = value; } catch {}
  function initialize() {
    cleanup();
    const sidebar = document.querySelector('[data-docs-sidebar]');
    if (!sidebar) return;
    const body = document.body;
    const handle = document.createElement('div');
    handle.className = 'docs-sidebar-resizer';
    handle.tabIndex = 0;
    handle.setAttribute('role', 'separator');
    handle.setAttribute('aria-orientation', 'vertical');
    handle.setAttribute('aria-controls', sidebar.id);
    handle.setAttribute('aria-label', document.documentElement.lang.startsWith('ja') ? 'メニューの幅' : 'Navigation width');
    handle.title = document.documentElement.lang.startsWith('ja') ? 'ドラッグまたは左右キーで幅を変更。ダブルクリックで自動調整。' : 'Drag or use arrow keys to resize. Double-click to reset.';
    sidebar.append(handle);
    let width = 256;
    let pointer;
    const limits = () => ({min: 220, max: Math.max(220, Math.min(560, innerWidth * .42, innerWidth - 480))});
    const labels = () => [...sidebar.querySelectorAll('summary,a,li > span')];
    for (const label of labels()) label.title ||= label.textContent.trim();
    const natural = () => {
      const canvas = document.createElement('canvas');
      const context = canvas.getContext('2d');
      let result = 240;
      for (const label of labels()) {
        label.title ||= label.textContent.trim();
        if (!label.getClientRects().length) continue;
        const style = getComputedStyle(label);
        if (context) context.font = style.font;
        let depth = 0;
        for (let parent = label.parentElement; parent && parent !== sidebar; parent = parent.parentElement) if (parent.tagName === 'UL') depth++;
        result = Math.max(result, (context ? context.measureText(label.textContent.trim()).width : label.textContent.length * 8) + depth * 18 + 64);
      }
      return result;
    };
    const update = value => {
      const {min,max} = limits();
      width = Math.round(Math.max(min, Math.min(max, value)));
      handle.hidden = compact.matches;
      handle.setAttribute('aria-valuemin', String(min));
      handle.setAttribute('aria-valuemax', String(Math.round(max)));
      handle.setAttribute('aria-valuenow', String(width));
      handle.setAttribute('aria-valuetext', `${width} px`);
      if (compact.matches) body.style.removeProperty('--docs-sidebar-width');
      else body.style.setProperty('--docs-sidebar-width', `${width}px`);
    };
    const refresh = () => update(saved ?? natural());
    const persist = () => { saved = width; try { localStorage.setItem(storageKey, String(width)); } catch {} };
    handle.addEventListener('pointerdown', event => {
      if (compact.matches || event.button !== 0) return;
      pointer = event.pointerId; handle.setPointerCapture(pointer); event.preventDefault();
      body.classList.add('docs-sidebar-resizing');
    });
    handle.addEventListener('pointermove', event => {
      if (pointer !== event.pointerId) return;
      const rect = sidebar.getBoundingClientRect();
      update(getComputedStyle(body).direction === 'rtl' ? rect.right - event.clientX : event.clientX - rect.left);
    });
    const finish = event => {
      if (pointer !== event.pointerId) return;
      pointer = undefined; body.classList.remove('docs-sidebar-resizing'); persist();
    };
    handle.addEventListener('pointerup', finish);
    handle.addEventListener('pointercancel', finish);
    handle.addEventListener('lostpointercapture', finish);
    handle.addEventListener('keydown', event => {
      const {min,max} = limits();
      const direction = getComputedStyle(body).direction === 'rtl' ? -1 : 1;
      const value = event.key === 'Home' ? min : event.key === 'End' ? max : event.key === 'ArrowLeft' ? width - 16 * direction : event.key === 'ArrowRight' ? width + 16 * direction : null;
      if (value === null || compact.matches) return;
      event.preventDefault(); update(value); persist();
    });
    handle.addEventListener('dblclick', () => { saved = undefined; try { localStorage.removeItem(storageKey); } catch {} refresh(); });
    const toggled = () => { if (saved === undefined) refresh(); };
    sidebar.addEventListener('toggle', toggled, true);
    addEventListener('resize', refresh);
    compact.addEventListener('change', refresh);
    refresh();
    cleanup = () => { removeEventListener('resize', refresh); compact.removeEventListener('change', refresh); sidebar.removeEventListener('toggle', toggled, true); handle.remove(); body.classList.remove('docs-sidebar-resizing'); };
  }
  globalThis[key] = initialize;
  document.addEventListener('lithosharp:page', initialize);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, {once:true}); else initialize();
})();
