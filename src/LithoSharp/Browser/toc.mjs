// Current-heading tracking for Docs only. Anchor navigation remains entirely native.
(() => {
  const key = Symbol.for('lithosharp.docs-toc');
  if (globalThis[key]) { globalThis[key](); return; }
  let cleanup = () => {};
  function initialize() {
    cleanup();
    const article = document.querySelector('main .docs-content') ?? document.querySelector('main.docs-main');
    const toc = document.querySelector('.website-toc, .docs-toc, .docs-body .post-toc');
    if (!article || !toc) { cleanup = () => {}; return; }
    toc.setAttribute('data-docs-toc', '');
    let entries = [], frame = 0, disposed = false, preferred;
    const clear = () => entries.forEach(({link}) => { link.classList.remove('docs-toc-current'); link.removeAttribute('aria-current'); });
    const collect = () => {
      clear();
      entries = [...toc.querySelectorAll('a[href]')].flatMap(link => {
        let url, id;
        try { url = new URL(link.getAttribute('href'), location.href); id = decodeURIComponent(url.hash.slice(1)); } catch { return []; }
        if (url.origin !== location.origin || url.pathname !== location.pathname || url.search !== location.search || !id) return [];
        const heading = document.getElementById(id);
        return heading && article.contains(heading) && /^H[1-6]$/.test(heading.tagName) ? [{link, heading, id}] : [];
      }).sort((a,b) => a.heading === b.heading ? 0 : a.heading.compareDocumentPosition(b.heading) & 4 ? -1 : 1);
    };
    const hashEntry = () => { try { const id = decodeURIComponent(location.hash.slice(1)); return entries.find(entry => entry.id === id); } catch { return undefined; } };
    const update = () => {
      frame = 0;
      if (disposed || !entries.length) return;
      const visible = entries.filter(entry => entry.heading.getClientRects().length && getComputedStyle(entry.heading).visibility !== 'hidden');
      if (!visible.length) { clear(); return; }
      const header = document.querySelector('.docs-header');
      const threshold = Math.max(0, header?.getBoundingClientRect().bottom ?? 0) + 16;
      let current = visible[0];
      for (const entry of visible) {
        // Native fragment navigation honors each heading's scroll margin; match its landing point.
        const margin = Number.parseFloat(getComputedStyle(entry.heading).scrollMarginTop) || 0;
        if (entry.heading.getBoundingClientRect().top <= Math.max(threshold, margin) + 2) current = entry;
      }
      const articleBounds = article.getBoundingClientRect();
      const articleEnd = articleBounds.height > innerHeight - threshold && articleBounds.bottom <= innerHeight + 2;
      const scrolling = document.scrollingElement || document.documentElement;
      if (scrolling.scrollHeight > innerHeight + 2 && scrollY > 0 && (scrollY + innerHeight >= scrolling.scrollHeight - 2 || articleEnd)) current = visible.at(-1);
      if (preferred && visible.includes(preferred)) current = preferred;
      preferred = undefined;
      for (const entry of entries) {
        const active = entry === current;
        entry.link.classList.toggle('docs-toc-current', active);
        if (active) entry.link.setAttribute('aria-current', 'location'); else entry.link.removeAttribute('aria-current');
      }
    };
    const schedule = () => { if (!frame && !disposed) frame = requestAnimationFrame(update); };
    const hashChanged = () => { preferred = hashEntry(); schedule(); };
    const scrolled = () => { preferred = undefined; schedule(); };
    collect(); preferred = hashEntry();
    addEventListener('scroll', scrolled, {passive:true});
    addEventListener('resize', schedule);
    addEventListener('hashchange', hashChanged);
    addEventListener('popstate', schedule);
    const resize = new ResizeObserver(schedule);
    resize.observe(article);
    const header = document.querySelector('.docs-header'); if (header) resize.observe(header);
    const mutation = new MutationObserver(records => {
      if (!records.some(record => !toc.contains(record.target))) return;
      collect(); schedule();
    });
    mutation.observe(article, {childList:true, subtree:true, characterData:true, attributes:true, attributeFilter:['class','style','hidden','open']});
    schedule();
    cleanup = () => {
      disposed = true; if (frame) cancelAnimationFrame(frame);
      removeEventListener('scroll', scrolled); removeEventListener('resize', schedule);
      removeEventListener('hashchange', hashChanged); removeEventListener('popstate', schedule);
      resize.disconnect(); mutation.disconnect(); clear();
    };
  }
  globalThis[key] = initialize;
  document.addEventListener('lithosharp:before-navigate', () => { cleanup(); cleanup = () => {}; });
  document.addEventListener('lithosharp:page', initialize);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, {once:true}); else initialize();
})();
