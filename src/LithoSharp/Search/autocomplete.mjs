// Progressive local autocomplete. Markup remains a normal search form without JavaScript.
const autocompleteState = globalThis[Symbol.for('lithosharp.autocomplete')] ??= {cache:new Map(), widgets:new WeakSet(), handlers:new WeakMap()};
const autocompleteCache = autocompleteState.cache;
const autocompleteWidgets = autocompleteState.widgets;
function startAutocomplete() {
  for (const widget of document.querySelectorAll('[data-site-autocomplete]')) {
    if (autocompleteWidgets.has(widget)) continue;
    autocompleteWidgets.add(widget);
    const input = widget.querySelector('input[type=search]');
    const results = widget.querySelector('[data-results]');
    const status = widget.querySelector('[role=status]');
    if (!input || !results || !status) continue;
    const panel = widget.querySelector('[data-autocomplete-panel],.website-search-panel');
    const resultId = 'ls-autocomplete-' + Math.random().toString(36).slice(2);
    results.id = resultId;
    results.setAttribute('role', 'listbox');
    results.setAttribute('aria-label', input.getAttribute('aria-label') || 'Search results');
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-controls', resultId);
    let selected = 0;
    const select = index => {
      const links = [...results.querySelectorAll('a')];
      selected = links.length ? (index + links.length) % links.length : 0;
      links.forEach((link, i) => { link.setAttribute('aria-selected', String(i === selected)); link.closest('article').classList.toggle('website-search-selected', i === selected); });
      if (links[selected]) input.setAttribute('aria-activedescendant', links[selected].id);
      else input.removeAttribute('aria-activedescendant');
      return links[selected];
    };
    const close = () => { widget.removeAttribute('data-search-open'); input.setAttribute('aria-expanded', 'false'); input.removeAttribute('aria-activedescendant'); if (panel) panel.hidden = true; };
    const open = () => { widget.setAttribute('data-search-open', ''); input.setAttribute('aria-expanded', 'true'); if (panel) panel.hidden = false; select(selected); };
    close();
    input.addEventListener('focus', () => { if (input.value.trim()) open(); });
    widget.addEventListener('focusout', event => { if (!widget.contains(event.relatedTarget)) close(); });
    widget.addEventListener('click', event => {
      if (event.target.closest('[data-autocomplete-clear],[data-website-search-clear]')) { input.value = ''; input.dispatchEvent(new Event('input', {bubbles:true})); input.focus(); }
      if (event.target.closest('[data-autocomplete-close],[data-website-search-close]')) { input.focus(); close(); }
    });
    let composing = false;
    input.addEventListener('compositionstart', () => { composing = true; });
    input.addEventListener('compositionend', () => { composing = false; });
    widget.addEventListener('submit', event => {
      event.preventDefault();
      if (composing) return;
      const link = results.querySelector('a[aria-selected=true]');
      if (link && widget.hasAttribute('data-search-open')) link.click();
    });
    input.addEventListener('keydown', event => {
      if (event.isComposing) return;
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); event.stopPropagation(); open(); select(selected + (event.key === 'ArrowDown' ? 1 : -1))?.scrollIntoView({block:'nearest'}); }
    });
    autocompleteState.handlers.set(widget, {close});
    let requestId = 0, timer;
    const japanese = widget.dataset.locale === 'ja';
    const text = (ja, en) => japanese ? ja : en;
    const highlight = (element, value, query) => {
      const index = value.toLocaleLowerCase().indexOf(query.toLocaleLowerCase());
      if (index < 0) { element.textContent = value; return; }
      const mark = document.createElement('mark'); mark.textContent = value.slice(index, index + query.length);
      element.append(document.createTextNode(value.slice(0, index)), mark, document.createTextNode(value.slice(index + query.length)));
    };
    const query = async () => {
      const request = ++requestId, value = input.value.trim();
      results.replaceChildren();
      if (!value) { status.textContent = ''; return; }
      status.textContent = text('検索中…', 'Searching…');
      try {
        const url = widget.dataset.index;
        if (!autocompleteCache.has(url)) autocompleteCache.set(url, fetch(url).then(response => {
          if (!response.ok) throw Error('Search unavailable'); return response.json();
        }).catch(error => { autocompleteCache.delete(url); throw error; }));
        const payload = await autocompleteCache.get(url);
        let documents = Array.isArray(payload) ? payload : payload.documents;
        if (widget.dataset.locale) documents = documents.filter(item => !item.locale || item.locale === widget.dataset.locale);
        const hits = queryDocuments(documents, value, {limit:10});
        if (request !== requestId || !widget.isConnected) return;
        for (const hit of hits) {
          const url = new URL(hit.url, location.href);
          if (url.origin !== location.origin || !['http:', 'https:'].includes(url.protocol)) continue;
          const article = document.createElement('article'), link = document.createElement('a'), excerpt = document.createElement('p');
          link.href = url.href; link.id = resultId + '-' + results.children.length; link.setAttribute('role','option'); link.tabIndex = -1;
          highlight(link, hit.title, value); highlight(excerpt, String(hit.body), value);
          link.append(excerpt); article.append(link); results.append(article);
        }
        select(0);
        status.textContent = text(`${hits.length} 件の候補`, `${hits.length} suggestions`);
      } catch { if (request === requestId && widget.isConnected) status.textContent = text('検索を利用できません。再入力してお試しください。', 'Search is unavailable. Try typing again.'); }
    };
    input.addEventListener('input', () => { ++requestId; clearTimeout(timer); results.replaceChildren(); select(0); if (input.value.trim()) open(); else close(); timer = setTimeout(query, 120); });
  }
}
startAutocomplete();
document.addEventListener('lithosharp:page', startAutocomplete);

if (!autocompleteState.listening) {
  autocompleteState.listening = true;
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
      const input = document.querySelector('[data-site-autocomplete] input[type=search]');
      if (input) { event.preventDefault(); input.focus(); }
    }
    const widget = event.target.closest('[data-site-autocomplete][data-search-open]');
    if (event.key === 'Escape' && widget) {
      event.preventDefault(); event.stopImmediatePropagation();
      widget.querySelector('input').focus(); autocompleteState.handlers.get(widget)?.close();
    }
  }, true);
  document.addEventListener('click', event => {
    for (const widget of document.querySelectorAll('[data-site-autocomplete][data-search-open]')) {
      if (!widget.contains(event.target)) autocompleteState.handlers.get(widget)?.close();
    }
  });
}