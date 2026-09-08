const installed = Symbol.for('lithosharp.client');
export function start(config) {
  if (globalThis[installed]) { globalThis[installed].page(); return; }
  const namespace = `lithosharp:${config.basePath}:`;
  const saved = key => { try { return localStorage.getItem(namespace + key); } catch { return null; } };
  const save = (key, value) => { try { localStorage.setItem(namespace + key, value); } catch { /* Persistence is optional. */ } };
  const system = matchMedia('(prefers-color-scheme: dark)');
  let theme = saved('theme') || 'system';
  function applyTheme() {
    document.documentElement.dataset.theme = theme === 'system' ? system.matches ? 'dark' : 'light' : theme;
    document.documentElement.style.colorScheme = document.documentElement.dataset.theme;
    document.dispatchEvent(new CustomEvent('lithosharp:theme', {detail: {theme: document.documentElement.dataset.theme}}));
    document.querySelectorAll('[data-ls-theme]').forEach(button => { button.textContent = `Theme: ${theme}`; button.setAttribute('aria-label', `Color theme: ${theme}`); });
  }
  function page() {
    if (config.theme) {
      if (!document.querySelector('[data-ls-theme]')) {
        const button = document.createElement('button'); button.type = 'button'; button.dataset.lsTheme = '';
        document.querySelector('header')?.append(button);
      }
      applyTheme();
    }
    if (config.announcement && !saved('announcement:' + config.announcement) && !document.querySelector('[data-ls-announcement]')) {
      const aside = document.createElement('aside'); aside.dataset.lsAnnouncement = ''; aside.setAttribute('role', 'note');
      aside.textContent = config.announcement; const dismiss = document.createElement('button'); dismiss.type = 'button'; dismiss.dataset.lsDismiss = ''; dismiss.textContent = 'Dismiss'; aside.append(dismiss); document.body.prepend(aside);
    }
    for (const [selector, links] of [['header', config.navbar], ['footer', config.footer]]) {
      const parent = document.querySelector(selector); if (!parent || parent.querySelector('[data-ls-links]') || !links?.length) continue;
      const nav = document.createElement('nav'); nav.dataset.lsLinks = '';
      for (const item of links) { const link = document.createElement('a'); link.href = item.url; link.textContent = item.label; nav.append(link); }
      parent.append(nav);
    }
    document.dispatchEvent(new CustomEvent('lithosharp:page', {detail: {url: location.href, title: document.title}}));
  }
  globalThis[installed] = {page};
  if (config.theme) system.addEventListener('change', applyTheme);
  document.addEventListener('click', event => {
    if (event.target.closest('[data-ls-theme]')) { theme = theme === 'system' ? 'light' : theme === 'light' ? 'dark' : 'system'; save('theme', theme); applyTheme(); }
    if (event.target.closest('[data-ls-dismiss]')) { save('announcement:' + config.announcement, 'dismissed'); document.querySelector('[data-ls-announcement]')?.remove(); }
    const menu = event.target.closest('[data-docs-menu-toggle]');
    if (menu) { const sidebar = document.querySelector('[data-docs-sidebar]'); const expanded = menu.getAttribute('aria-expanded') !== 'true'; menu.setAttribute('aria-expanded', String(expanded)); sidebar?.classList.toggle('is-open', expanded); }
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') { document.querySelector('[data-docs-sidebar]')?.classList.remove('is-open'); document.querySelector('[data-docs-menu-toggle]')?.setAttribute('aria-expanded', 'false'); } });
  if (config.navigation) {
    let pending;
    const prefetched = new Map();
    history.scrollRestoration = 'manual';
    const eligible = link => {
      if (!link || link.target || link.download || link.hasAttribute('data-no-navigation')) return null;
      const url = new URL(link.href, location.href);
      return url.origin === location.origin && url.pathname.startsWith(config.basePath) && (url.pathname.endsWith('/') || url.pathname.endsWith('.html')) ? url : null;
    };
    const rememberScroll = () => history.replaceState({...history.state, lsScroll: [scrollX, scrollY]}, '', location.href);
    async function fetchPage(url, signal) {
      const response = await fetch(url, {signal, headers: {'Accept': 'text/html'}});
      if (!response.ok || !response.headers.get('content-type')?.includes('text/html') || new URL(response.url).origin !== location.origin) throw new Error('Full navigation required.');
      return response.text();
    }
    async function navigate(url, pop = false, scroll = null) {
      pending?.abort(); const controller = new AbortController(); pending = controller;
      if (!pop) rememberScroll();
      try {
        const key = url.href.split('#')[0];
        const html = await (prefetched.get(key) ?? fetchPage(url, controller.signal));
        const next = new DOMParser().parseFromString(html, 'text/html');
        if (!next.querySelector('main') || next.querySelector('meta[http-equiv=refresh]')) throw new Error('Full navigation required.');
        if (controller.signal.aborted) return;
        const scripts = [...next.querySelectorAll('script[type=module][src]')].map(script => new URL(script.getAttribute('src'), url).href);
        const selectors = 'title,meta[name=description],meta[name=robots],meta[property],link[rel=canonical],link[rel=alternate],link[rel=stylesheet]';
        document.dispatchEvent(new CustomEvent('lithosharp:before-navigate'));
        document.querySelectorAll('head ' + selectors.split(',').join(',head ')).forEach(element => element.remove());
        next.head.querySelectorAll(selectors).forEach(element => document.head.append(document.importNode(element, true)));
        document.documentElement.lang = next.documentElement.lang;
        document.documentElement.dir = next.documentElement.dir;
        document.body.replaceWith(document.importNode(next.body, true));
        if (!pop) history.pushState({lsScroll: [0,0]}, '', url);
        for (const source of scripts) {
          if (new URL(source).origin !== location.origin) throw new Error('External module requires full navigation.');
          const module = await import(source); if (controller.signal.aborted) return; await module.mount?.();
        }
        page();
        const target = url.hash ? document.getElementById(decodeURIComponent(url.hash.slice(1))) : document.querySelector('main');
        if (target) { target.setAttribute('tabindex', '-1'); target.focus({preventScroll: true}); }
        if (scroll) window.scrollTo(...scroll); else if (url.hash && target) target.scrollIntoView(); else window.scrollTo(0, 0);
        pending = null;
      } catch (error) { if (!controller.signal.aborted) location.assign(url); }
    }
    document.addEventListener('click', event => {
      if (event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      const url = eligible(event.target.closest('a'));
      if (!url || url.pathname === location.pathname && url.search === location.search) return;
      event.preventDefault(); navigate(url);
    });
    document.addEventListener('pointerover', event => {
      const url = eligible(event.target.closest('a')); if (!url) return;
      const key = url.href.split('#')[0]; if (prefetched.has(key)) return;
      if (prefetched.size >= 10) prefetched.delete(prefetched.keys().next().value);
      prefetched.set(key, fetchPage(url).catch(error => { prefetched.delete(key); throw error; }));
      prefetched.get(key).catch(() => {});
    });
    window.addEventListener('popstate', event => navigate(new URL(location.href), true, event.state?.lsScroll));
    window.addEventListener('scroll', () => { if (!pending) rememberScroll(); }, {passive: true});
  }
  page();
}
