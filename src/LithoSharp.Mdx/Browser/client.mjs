const installed = Symbol.for('lithosharp.client');
export function start(config) {
  if (globalThis[installed]) { globalThis[installed].page(); return; }
  const namespace = `lithosharp:${config.basePath}:`;
  const saved = key => { try { return localStorage.getItem(namespace + key); } catch { return null; } };
  const save = (key, value) => { try { localStorage.setItem(namespace + key, value); } catch { /* Persistence is optional. */ } };
  const system = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : {matches: false, addEventListener() {}};
  let messages = {};
  const t = (key, fallback) => messages[key] ?? fallback;
  document.addEventListener('lithosharp:chunk-error', () => {
    const key = namespace + 'chunk-reload:' + location.pathname;
    try { if (!sessionStorage.getItem(key)) { sessionStorage.setItem(key, '1'); location.reload(); return; } } catch { }
    if (!document.querySelector('[data-ls-chunk-retry]')) {
      const link = document.createElement('a'); link.dataset.lsChunkRetry = ''; link.dataset.noNavigation = ''; link.href = location.href;
      link.textContent = t('reloadPage', 'Reload this page to retry interactive content'); document.body.prepend(link);
    }
  });
  let consent = false, lastView = null;
  function track() {
    if (!config.analytics || !consent || lastView === location.href) return;
    lastView = location.href;
    const data = config.analytics.provider === 'plausible'
      ? {name: 'pageview', url: location.href, domain: config.analytics.domain, referrer: document.referrer}
      : {event: 'pageview', url: location.href, title: document.title};
    fetch(config.analytics.endpoint, {method: 'POST', mode: 'cors', credentials: 'omit', headers: {'Content-Type': 'text/plain'}, body: JSON.stringify(data), keepalive: true}).catch(() => {});
  }
  document.addEventListener('lithosharp:consent', event => { consent = event.detail?.granted === true; if (!consent) lastView = null; else track(); });
  if (config.offline && 'serviceWorker' in navigator) {
    const controlled = !!navigator.serviceWorker.controller;
    navigator.serviceWorker.register(config.basePath + 'lithosharp-sw.js', {scope: config.basePath, updateViaCache: 'none'}).then(registration => {
      const offer = () => {
        if (!registration.waiting || !navigator.serviceWorker.controller || document.querySelector('[data-ls-update]')) return;
        const button = document.createElement('button'); button.type = 'button'; button.dataset.lsUpdate = ''; button.textContent = t('offlineUpdate', 'Update offline content');
        button.addEventListener('click', () => registration.waiting?.postMessage({type: 'lithosharp:activate'})); document.body.prepend(button);
      };
      offer(); registration.addEventListener('updatefound', () => registration.installing?.addEventListener('statechange', offer));
    }).catch(error => document.dispatchEvent(new CustomEvent('lithosharp:offline-error', {detail: String(error)})));
    let reloading = false;
    const reload = () => { if (!reloading) { reloading = true; location.reload(); } };
    navigator.serviceWorker.addEventListener('controllerchange', () => { if (controlled) reload(); });
    navigator.serviceWorker.addEventListener('message', event => { if (event.data?.type === 'lithosharp:offline-retired') reload(); });
  }
  let theme = saved('theme') || 'system';
  const searchCache = new Map();
  const searchWidgets = new WeakSet();
  function startSearch() {
    if (!config.search) return;
    for (const scope of document.querySelectorAll('[data-ls-search]')) {
      if (searchWidgets.has(scope)) continue;
      searchWidgets.add(scope);
      const input = scope.querySelector('input'), results = scope.querySelector('[data-results]'), status = scope.querySelector('[role=status]');
      let pending = 0, timer;
      const normalize = value => String(value ?? '').normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
      async function query() {
        const request = ++pending;
        const value = input.value.trim(); results.replaceChildren();
        if (!value) { status.textContent = ''; return; }
        status.textContent = t('searching', 'Searching…');
        try {
          let hits;
          if (config.algolia) {
            const response = await fetch(`https://${config.algolia.applicationId}-dsn.algolia.net/1/indexes/${encodeURIComponent(config.algolia.indexName)}/query`, {
              method: 'POST', credentials: 'omit', headers: {'Content-Type': 'application/json', 'X-Algolia-Application-Id': config.algolia.applicationId, 'X-Algolia-API-Key': config.algolia.searchOnlyApiKey},
              body: JSON.stringify({query: value, hitsPerPage: 30, facetFilters: ['collection','version','locale'].map(key => key + ':' + scope.dataset[key])})
            });
            if (!response.ok) throw new Error('Search service unavailable');
            hits = (await response.json()).hits.map(hit => ({title: hit.title || Object.values(hit.hierarchy ?? {}).filter(Boolean).join(' · '), url: hit.url, body: hit.content || ''}));
          } else {
            const url = scope.dataset.index;
            if (!searchCache.has(url)) {
              if (searchCache.size >= 4) searchCache.delete(searchCache.keys().next().value);
              searchCache.set(url, fetch(url).then(response => { if (!response.ok) throw new Error('Search data unavailable'); return response.json(); }).catch(error => { searchCache.delete(url); throw error; }));
            }
            const documents = await searchCache.get(url);
            const tokens = normalize(value).split(' ');
            hits = documents.filter(doc => tokens.every(token => normalize(doc.title + ' ' + doc.summary + ' ' + doc.body).includes(token))).map(doc => {
              const section = doc.sections?.find(section => tokens.every(token => normalize(section.title + ' ' + section.body).includes(token)));
              return {title: doc.title + (section ? ' · ' + section.title : ''), url: doc.url + (section ? '#' + encodeURIComponent(section.anchor) : ''), body: section?.body || doc.summary || doc.body,
                score: tokens.reduce((score, token) => score + (normalize(doc.title).includes(token) ? 3 : 1), 0)};
            }).sort((left, right) => right.score - left.score).slice(0, 30);
          }
          if (request !== pending || !scope.isConnected) return;
          const highlight = (element, text) => {
            const index = text.toLowerCase().indexOf(value.toLowerCase());
            if (index < 0) { element.textContent = text; return; }
            element.append(document.createTextNode(text.slice(0, index)));
            const mark = document.createElement('mark'); mark.textContent = text.slice(index, index + value.length); element.append(mark, document.createTextNode(text.slice(index + value.length)));
          };
          for (const hit of hits) {
            const url = new URL(hit.url, location.href); if (!['http:', 'https:'].includes(url.protocol)) continue;
            const article = document.createElement('article'), link = document.createElement('a'), excerpt = document.createElement('p');
            link.href = url.href; highlight(link, hit.title); highlight(excerpt, String(hit.body).slice(0, 220)); article.append(link, excerpt); results.append(article);
          }
          status.textContent = t('results', '{count} results').replace('{count}', hits.length);
        } catch { if (request === pending && scope.isConnected) status.textContent = t('searchUnavailable', 'Search unavailable. You can still browse this documentation.'); }
      }
      input.addEventListener('input', () => { pending++; clearTimeout(timer); timer = setTimeout(query, 150); });
      scope.addEventListener('keydown', event => {
        const links = [...results.querySelectorAll('a')];
        if (event.key === 'Escape') { input.focus(); return; }
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); const index = links.indexOf(document.activeElement); links[(index + (event.key === 'ArrowDown' ? 1 : links.length - 1)) % links.length]?.focus(); }
      });
    }
  }
  function applyTheme() {
    document.documentElement.dataset.theme = theme === 'system' ? system.matches ? 'dark' : 'light' : theme;
    document.documentElement.style.colorScheme = document.documentElement.dataset.theme;
    document.dispatchEvent(new CustomEvent('lithosharp:theme', {detail: {theme: document.documentElement.dataset.theme}}));
    document.querySelectorAll('[data-ls-theme]').forEach(button => { button.textContent = `${t('theme', 'Theme')}: ${t(theme, theme)}`; button.setAttribute('aria-label', button.textContent); });
  }
  function page() {
    try { messages = JSON.parse(document.querySelector('[data-ls-messages]')?.textContent || '{}'); } catch { messages = {}; }
    startSearch();
    if (config.theme) {
      if (!document.querySelector('[data-ls-theme]')) {
        const button = document.createElement('button'); button.type = 'button'; button.dataset.lsTheme = '';
        document.querySelector('header')?.append(button);
      }
      applyTheme();
    }
    if (config.announcement && !saved('announcement:' + config.announcement) && !document.querySelector('[data-ls-announcement]')) {
      const aside = document.createElement('aside'); aside.dataset.lsAnnouncement = ''; aside.setAttribute('role', 'note');
      aside.textContent = config.announcement; const dismiss = document.createElement('button'); dismiss.type = 'button'; dismiss.dataset.lsDismiss = ''; dismiss.textContent = t('dismiss', 'Dismiss'); aside.append(dismiss); document.body.prepend(aside);
    }
    for (const [selector, links] of [['header', config.navbar], ['footer', config.footer]]) {
      const parent = document.querySelector(selector); if (!parent || parent.querySelector('[data-ls-links]') || !links?.length) continue;
      const nav = document.createElement('nav'); nav.dataset.lsLinks = '';
      for (const item of links) { const link = document.createElement('a'); link.href = item.url; link.textContent = item.label; nav.append(link); }
      parent.append(nav);
    }
    document.dispatchEvent(new CustomEvent('lithosharp:page', {detail: {url: location.href, title: document.title}}));
    track();
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
